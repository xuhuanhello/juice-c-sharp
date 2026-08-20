using System;
using System.Text;
using DataChannelUnity.Internal;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace DataChannelUnity
{
    /// <summary>
    /// Library lifecycle and main-thread event pump.
    /// </summary>
    public static partial class DataChannelRuntime
    {
        // 可观测性阈值。全部是 **internal 常量，不开配置面**（SPEC §6）：给旋钮就要
        // 定义语义、写进规格、测它，而没有任何证据说这些值是错的。90fps VR 与 30fps
        // 移动端对慢帧的容忍度确实不同，真有人撞上再议（map #26 的 fog 里记着这条）。
        private const double SlowFrameMs = 4.0;                 // 60fps 的 16.7ms 里吃掉 4ms 就该说话
        private const int ControlQueueDepthWarn = 1024;         // 与上游 RECV_QUEUE_LIMIT 同量级
        private const double PumpStaleSeconds = 5.0;            // 秒级；只在应用调 API 时查，绝不后台轮询

        // #147：存活判定的帧推进门槛。健康稳态 delta ∈ {0,1}（脚本 Update 先于
        // 尾插的 pump 条目，同帧调用可见 delta=1；LateUpdate/协程里调用可见 0），
        // 3 留余量。帧没推进（编辑器暂停、移动端后台挂起、一切冻结形态）= 循环
        // 本身没在跑，不是泵的故障 —— #145 实测：暂停 >5s 后一次 API 调用就把
        // 唯一的重试额度烧在幻影上，之后真被抹除时得不到自愈。
        private const long PumpStallFrames = 3;

        private static bool _nativeReady;
        private static bool _initAttempted;
        private static bool _pumpRegistered;

        // ABI 横幅「每进程至多一次」的 latch（SPEC §7 identity-banner 例外，#140 / #142）。
        // **刻意不进 ResetStaticsOnEnterPlayMode 的清单，也不随 ShutdownNative 复位**：
        // 那两处复位的是「init 可以再跑」，而横幅的边界是每进程至多一次 —— init 在
        // 同一进程里合法地多跑是真实发生过的（#141 的两行）。原生 g_inited 的幂等
        // 挡不住它：幂等的是 init，不是这行日志。域重载清空静态量，latch 随域自然
        // 复位 —— 那就是「进程」在 Editor 里的读法。
        private static bool _abiBannerEmitted;

        // pump 存活：单调计数 + 单调墙钟 + 帧号。计数只用于诊断文本；判定看
        // 时间戳（多久没跑）与帧号（循环是否真在推进）两个观测面（#147）。
        private static long _pumpTicks;
        private static long _lastPumpTimestamp;
        private static long _lastPumpFrame;
        private static bool _pumpReregisterAttempted;
        private static bool _pumpRetryExhausted;

        // #148：Pump 的重入守卫。常驻（非 Conditional）—— 它防的是 Release 下
        // 也真实的数据损坏（复用缓冲被内层覆写，外层回调手里的 Span 变质）。
        private static bool _pumping;

        private static byte[] _payloadBuf = new byte[65536];
        private static byte[] _payload2Buf = new byte[4096];

        // 四路缓冲各记一次「首次超基线」。**只涨不缩**（#45 决议 1，推翻 #38 的滞回收缩）。
        private static bool _grewPayload, _grewPayload2, _grewMessage, _grewLog;

        // 消息缓冲与控制缓冲**分开**（SPEC §6）：SDP/candidate 稳定在几 KB，
        // 消息可能几 MB，共用一个就是让前者永远按后者的尺寸躺着。
        private static byte[] _messageBuf = new byte[65536];

        // 日志行短，基线小。
        private static byte[] _logBuf = new byte[4096];

        // 复用的通道快照，零分配。见 HandleTable.SnapshotDataChannels 的说明。
        private static readonly System.Collections.Generic.List<DataChannel> ChannelSnapshot =
            new System.Collections.Generic.List<DataChannel>();

        // DisposeAllLive 用。不在热路径上，但没理由每次新建。
        private static readonly System.Collections.Generic.List<PeerConnection> PeerSnapshot =
            new System.Collections.Generic.List<PeerConnection>();

        /// <summary>
        /// 原生库**当前**是否已加载并初始化。**被动探询，读取不触发加载**（#146）。
        /// </summary>
        /// <remarks>
        /// 未加载时如实返回 <c>false</c>；要加载，构造第一个
        /// <see cref="PeerConnection"/>，或调用 <see cref="Preload"/>。
        /// 旧语义（读取即尝试加载）在 0.4.0 移除 —— 惰性范式下「查询不改变系统状态」
        /// 比「查询即答案」更值钱：一个诊断 HUD 读个属性就把原生库拉起来，
        /// 恰好破坏范式要给的时机控制。
        /// </remarks>
        public static bool IsNativeAvailable
        {
            get
            {
                MainThread.Assert("DataChannelRuntime.IsNativeAvailable");
                return _nativeReady;
            }
        }

        /// <summary>
        /// 已加载原生库的 ABI 版本。**被动探询：未加载时返回 0，不触发加载**（#146）。
        /// </summary>
        public static int AbiVersion
        {
            get
            {
                MainThread.Assert("DataChannelRuntime.AbiVersion");
                if (!_nativeReady) return 0;
                return NativeMethods.dcu_abi_version(out var v) == NativeMethods.Success ? v : 0;
            }
        }

        /// <summary>
        /// 可选的显式预热：立即加载并初始化原生库。**不调用也完全可用** ——
        /// 首次构造 <see cref="PeerConnection"/> 会自动加载（#146，与上游
        /// <c>rtcPreload</c> 的「惰性 + 可选预热」同形）。
        /// </summary>
        /// <remarks>
        /// 两类场景需要它：在加载画面里预热 DTLS/SCTP，别让「加入对局」那一下
        /// 付首连延迟；以及排查崩溃时把原生加载钉到自选时刻做二分。
        /// 幂等；主线程限定。**失败抛 <see cref="DataChannelException"/>** ——
        /// 调用者显式要了，缺席就该是异常，而不是内部懒加载路径里的一行日志。
        /// 失败的具体成因（插件缺失 / dcu_init 返回码）在抛出前已记入日志。
        /// </remarks>
        public static void Preload()
        {
            MainThread.Assert("DataChannelRuntime.Preload");
            EnsureNative();
            if (!_nativeReady)
                throw new DataChannelException(
                    "Preload failed: the native plugin could not be loaded or initialized. "
                    + "The cause was logged just above (missing plugin binary, or a dcu_init failure). "
                    + "Build and place the plugin per docs/SPEC.md.");
        }

        /// <summary>
        /// 进入播放模式。**同时覆盖 Reload Domain 开与关两种配置**（#37 决议 4）。
        /// </summary>
        /// <remarks>
        /// Reload Domain **关**的时候，静态量不会被域重载清掉 —— 上一次播放模式的
        /// 对象会原样漏进这一次。所以先精确释放一遍再重置。
        ///
        /// Reload Domain **开**的时候域刚重建，表本来就是空的，
        /// <see cref="DisposeAllLive"/> 是个空操作。**一条路径覆盖两种配置**，
        /// 不需要先判断自己身处哪一种 —— 那种判断正是容易写反的东西。
        ///
        /// 这里**不 shutdown**：域还活着，用精确工具就够了。
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticsOnEnterPlayMode()
        {
            DisposeAllLive();
            _nativeReady = false;
            _initAttempted = false;
            _pumpRegistered = false;
            _pumpTicks = 0;
            _lastPumpTimestamp = 0;
            _lastPumpFrame = 0;
            _pumpReregisterAttempted = false;
            _pumpRetryExhausted = false;
            _pumping = false;
            _grewPayload = _grewPayload2 = _grewMessage = _grewLog = false;
            // _abiBannerEmitted 刻意不在这份清单里：横幅每进程至多一次（SPEC §7），
            // 播放会话不是进程边界。
            // 上个域残留的泄漏记录不该报进新域 —— 那些对象的表项已经随静态字段一起没了。
            LeakTracker.Clear();
            // 同理：别让上个域的节流窗口把新域的第一条告警压掉。
            Throttle.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            // **这里刻意不 EnsureNative**（#146）：native 的加载只经两条门 ——
            // 首次构造 PeerConnection，或显式 Preload()。从不使用本包的应用不再
            // 在启动付 dlopen + rtc::Preload；要确定性时机的应用自己选门。
            // pump 接线保持自动（#146 决议一）：纯托管 PlayerLoop 条目，native
            // 未加载时每帧只有一次时间戳、零 P/Invoke（Pump 对 !_nativeReady 直接
            // 返回），且它是事件能送达的前提 —— 惰性化的是加载，不是接线。
            RegisterPump();
            Application.quitting -= OnApplicationQuitting;
            Application.quitting += OnApplicationQuitting;
        }

        /// <summary>
        /// Player 退出：**只 <see cref="DisposeAllLive"/>，不 shutdown**（#37 决议 6）。
        /// </summary>
        /// <remarks>
        /// 两半的价值完全不对称。<see cref="DisposeAllLive"/> 会发出 DTLS/SCTP 关闭
        /// 通知，**对端立刻知道你走了**，而不是干等一次 ICE 超时 —— 对一个 P2P 库
        /// 这是实打实的对外价值。而在一个即将终止的进程里回收线程池毫无意义，
        /// <c>rtcCleanup</c> 却可能阻塞约 10 秒：iOS 终止时只给约 5 秒，Android 可能 ANR。
        ///
        /// 另注：这条钩子在 iOS（挂起而非退出）与 Android（后台被杀）**经常不触发**，
        /// 见 SPEC §6。「切后台要不要主动断连」是产品语义，本包不替应用决定。
        /// </remarks>
        private static void OnApplicationQuitting()
        {
            DisposeAllLive();
        }

        /// <summary>
        /// 级别变更的单向入口。**刻意不调 <see cref="EnsureNative"/>** —— 那正是原先
        /// 那条互相递归（EnsureNative → EnsureDefaults → SetLogLevel →
        /// IsNativeAvailable → EnsureNative）赖以终止的隐含不变量所在。剪断环，
        /// 而不是给环加护栏。
        /// </summary>
        internal static void OnLogLevelChanged(LogLevel level)
        {
            if (!_nativeReady) return;
            try { NativeMethods.dcu_set_log_level((int)level); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        internal static void EnsureNative()
        {
            if (_initAttempted) return;
            _initAttempted = true;
            try
            {
                var rc = NativeMethods.dcu_init();
                if (rc == NativeMethods.Success)
                {
                    _nativeReady = true;
                    // 存活判定的基准点。不设的话，EditMode 下第一次 new PeerConnection
                    // 会因为「pump 从来没跑过」被误报成死泵 —— 编辑模式本来就没有
                    // PlayerLoop，那不是故障。帧号同点设基准（#147）。
                    _lastPumpTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    _lastPumpFrame = Time.frameCount;
                    NativeMethods.dcu_set_log_level((int)DataChannelLog.Level);
                    if (!_abiBannerEmitted)
                    {
                        _abiBannerEmitted = true;
                        NativeMethods.dcu_abi_version(out var abi);
                        DataChannelLog.EmitProcessIdentity("Native library initialized (abi=" + abi + ").");
                    }
                }
                else
                {
                    DataChannelLog.Emit(LogLevel.Error, "dcu_init failed: " + rc);
                }
            }
            catch (DllNotFoundException e)
            {
                DataChannelLog.Emit(LogLevel.Error,
                    "Native plugin '" + NativeMethods.DllName + "' not found. Build and place Plugins per docs/SPEC.md. " + e.Message);
            }
            catch (Exception e)
            {
                DataChannelLog.Emit(LogLevel.Error, "Native init exception: " + e.Message);
            }
        }

        /// <summary>
        /// 把 pump 装进 <c>PlayerLoop</c>。**注册失败抛异常，不是记 warning**（SPEC §6）。
        /// </summary>
        /// <remarks>
        /// 理由就一句：**一个自以为在 pump、实际没有的包，是所有状态里最糟的那个。**
        /// 事件永远不送达，而唯一的线索是一行早就被刷走的 warning ——
        /// 与本仓库反复批判的「让缺席变成沉默」是同一个病。
        /// </remarks>
        internal static void RegisterPump()
        {
            if (_pumpRegistered) return;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (!InsertPump(ref loop))
                throw new InvalidOperationException(
                    "Cannot install the pump into the PlayerLoop: no Update segment was found. "
                    + "This package deliberately throws here instead of logging a warning: believing the pump runs when it does not "
                    + "presents as every event failing to arrive, with a single scrolled-away log line as the only clue. "
                    + "If this is intentional (a custom loop), call DataChannelRuntime.Pump() yourself every frame.");
            PlayerLoop.SetPlayerLoop(loop);
            _pumpRegistered = true;
        }

        /// <summary>把 pump 从 <c>PlayerLoop</c> 摘掉。找不到条目不算失败。</summary>
        internal static void UnregisterPump()
        {
            _pumpRegistered = false;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (loop.subSystemList == null) return;

            var changed = false;
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                var subs = loop.subSystemList[i].subSystemList;
                if (subs == null) continue;
                var kept = new System.Collections.Generic.List<PlayerLoopSystem>(subs.Length);
                for (int j = 0; j < subs.Length; j++)
                {
                    if (subs[j].type == typeof(DataChannelRuntime)) changed = true;
                    else kept.Add(subs[j]);
                }
                if (changed) loop.subSystemList[i].subSystemList = kept.ToArray();
            }
            if (changed) PlayerLoop.SetPlayerLoop(loop);
        }

        /// <summary>
        /// 精确释放当前所有存活对象。**域还活着时用的工具** —— 我们还握着引用，
        /// 就不该抡 <c>dcu_shutdown</c> 那把大锤（#37 的贯穿原则）。
        /// </summary>
        /// <remarks>
        /// 只遍历 PC 就够：每条 DataChannel 都由某个 PC 拥有，<c>Dispose</c> 会级联
        /// 带走（#29 决议 1）。之后仍扫一遍 DC 收尾 —— 若真出现「表里有 DC 而它的 PC
        /// 不在表里」，那是簿记出了错，得让它被释放而不是被忽略。
        ///
        /// 幂等：已 <c>Dispose</c> 的对象再调一次是空操作（S6 的门禁盯着这条）。
        /// </remarks>
        internal static void DisposeAllLive()
        {
            HandleTable.SnapshotPeerConnections(PeerSnapshot);
            for (int i = 0; i < PeerSnapshot.Count; i++)
            {
                try { PeerSnapshot[i].Dispose(); }
                catch (Exception e) { DataChannelLog.Emit(LogLevel.Error, "DisposeAllLive: failed to dispose a PeerConnection", e); }
            }
            PeerSnapshot.Clear();

            HandleTable.SnapshotDataChannels(ChannelSnapshot);
            for (int i = 0; i < ChannelSnapshot.Count; i++)
            {
                var dc = ChannelSnapshot[i];
                if (dc == null || dc.IsDisposed) continue;
                try { dc.Dispose(); }
                catch (Exception e) { DataChannelLog.Emit(LogLevel.Error, "DisposeAllLive: failed to dispose a DataChannel", e); }
            }
            ChannelSnapshot.Clear();
        }

        /// <summary>
        /// 抡大锤。**只在域将死时用**，并把未销毁对象数如实报出来。
        /// </summary>
        /// <remarks>
        /// 正常收尾这个数应当是 0 —— 因为调用方总是先 <see cref="DisposeAllLive"/>。
        /// 非 0 意味着有对象在托管侧已经失联（应用忘了 Dispose，且它已被 GC 而
        /// 终结器只入队不销毁原生对象），这正是这个计数存在的理由。
        /// </remarks>
        private static void ShutdownNative()
        {
            if (!_nativeReady) return;
            try
            {
                var rc = NativeMethods.dcu_shutdown(out var undestroyed);
                if (rc != NativeMethods.Success)
                    DataChannelLog.Emit(LogLevel.Error,
                        "dcu_shutdown failed: " + MapError(rc) + "（raw=" + rc + "）。"
                        + "An upstream Cleanup timeout lands here, and usually means an object stalled during destruction.");
                else if (undestroyed > 0)
                    DataChannelLog.Emit(LogLevel.Error,
                        "At dcu_shutdown, " + undestroyed + " native object(s) were still undestroyed. "
                        + "They were force-dropped rather than released normally, which means some PeerConnection / DataChannel "
                        + "was never disposed and the managed side had already lost track of it. In Editor / Development builds, "
                        + "leak diagnostics name the creation stack (DataChannelLog.LeakDetection).");
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (Exception e) { DataChannelLog.Emit(LogLevel.Error, "dcu_shutdown threw", e); }

            // 允许之后重新 init：域重载后 EnsureNative 得能再走一遍。
            // _abiBannerEmitted 不复位：re-init 是新的初始化，不是新的进程。
            _nativeReady = false;
            _initAttempted = false;
        }

        private static bool InsertPump(ref PlayerLoopSystem loop)
        {
            var pump = new PlayerLoopSystem
            {
                type = typeof(DataChannelRuntime),
                updateDelegate = Pump
            };

            if (loop.subSystemList == null) return false;
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type == typeof(Update))
                {
                    var subs = loop.subSystemList[i].subSystemList;
                    var list = subs != null
                        ? new System.Collections.Generic.List<PlayerLoopSystem>(subs)
                        : new System.Collections.Generic.List<PlayerLoopSystem>();
                    // Avoid duplicate inserts on domain reload edge cases.
                    list.RemoveAll(s => s.type == typeof(DataChannelRuntime));
                    list.Add(pump);
                    loop.subSystemList[i].subSystemList = list.ToArray();
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 排空原生事件并在调用线程（必须是主线程）上派发。**不可重入**：
        /// 在事件/消息回调里再调本方法会抛 <see cref="InvalidOperationException"/>（#148）。
        /// </summary>
        /// <remarks>
        /// 两段结构（SPEC §6）：先排空**控制**队列，再逐通道**拉取**消息。
        /// 两段都排空，**都不设每帧预算** —— 系统本来就会自己收敛：pump 排空则
        /// 上游 mRecvQueue 不涨、无背压；应用回调慢则帧变长、拉取变慢、队列涨到
        /// RECV_QUEUE_LIMIT 而阻塞，真背压顶回 SCTP。设预算是人为提前触发背压。
        ///
        /// 已接受的软肋：高速对端能让本段随流量线性变长。可见性靠慢帧告警（S7）。
        /// </remarks>
        public static void Pump()
        {
            MainThread.Assert("DataChannelRuntime.Pump");

            // #148：重入守卫，**常驻分支**（不用 Conditional —— 它防的是 Release 下
            // 也真实的数据损坏：内层 pump 覆写复用的消息缓冲，外层回调手里的
            // ReadOnlySpan 在回调期间变质）。重入按定义发生在本方法已在栈上时，
            // 而本方法内所有进入用户代码的口子都有每订阅者隔离 —— 这个异常必然被
            // 外层自己接住，变成一条带完整栈的 Error 日志：响亮、不崩，且让
            // 「发一条、泵到回复为止」的同步自旋写法立刻失败而不是静默挂死。
            if (_pumping)
                throw new InvalidOperationException(
                    "DataChannelRuntime.Pump() was called re-entrantly from inside a pump dispatch (an event or message callback). "
                    + "Do not pump from callbacks: the outer pump is already dispatching and will continue draining after your callback returns. "
                    + "A re-entrant pump would overwrite the shared message buffer while your ReadOnlySpan<byte> still points into it.");

            _pumping = true;
            try
            {
                // **存活戳记在最前面，且在 _nativeReady 检查之前。** 原生没就绪时 pump
                // 照样是在跑的，那不是「泵死了」；把戳记放在 return 之后会让存活检测
                // 去报一个根本不存在的故障。帧号与时间戳同点记录（#147）：墙钟回答
                // 「多久没跑」，帧号回答「循环是否真在推进」。
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                _pumpTicks++;
                _lastPumpTimestamp = start;
                _lastPumpFrame = Time.frameCount;

                if (!_nativeReady) return;

                // 泄漏报告最先排，且**必须在派发之前** —— 它要摘 HandleTable 的表项，
                // 那是一次字典改动，夹在派发中间就是在自己迭代的脚下拆桥。
                LeakTracker.Drain();
                // 日志先排：原生日志往往是后面那些事件的成因，先出来才有上下文。
                DrainNativeLogs();
                WarnIfControlQueueBacklogged();
                DrainControlEvents();
                DrainMessages();

                var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0
                                / System.Diagnostics.Stopwatch.Frequency;
                if (elapsedMs > SlowFrameMs) WarnSlowFrame(elapsedMs);
            }
            finally
            {
                // try/finally 复位：派发中任何异常（含穿透隔离的未知路径）都不能
                // 把守卫毒化成「永远拒绝泵」。
                _pumping = false;
            }
        }

        /// <summary>
        /// pump 段吃掉的帧时间超阈值时告警。**常驻，不条件编译** ——
        /// 每帧两次 <c>Stopwatch.GetTimestamp()</c> 的开销可以忽略，
        /// 与 #29 那个真的贵的泄漏栈捕获不是一回事。
        /// </summary>
        /// <remarks>
        /// 这条是 #38 决议 3 接受那个软肋的**代价条款**：高速对端能让本段随流量线性
        /// 变长，我们没有任何东西挡在前面（挡它的正确位置是连接层的每连接入站速率，
        /// 那是 out of scope 的另一件事）。软肋既然接受了，就必须能被看见 ——
        /// 否则临床表现是「不明原因掉帧」，而排查的人第一反应绝不会是网络层。
        /// </remarks>
        private static void WarnSlowFrame(double elapsedMs)
        {
            if (!Throttle.Note("pump-slow-frame", elapsedMs, out var suppressed, out var peak)) return;
            DataChannelLog.Emit(LogLevel.Warning,
                "The pump took " + elapsedMs.ToString("0.##") + " ms this frame, over the " + SlowFrameMs + " ms threshold. "
                + "Common causes: too much remote traffic, or a slow message callback of your own (callbacks run synchronously inside this segment)."
                + Throttle.SuppressedSuffix(suppressed, peak, " ms"));
        }

        /// <summary>
        /// 控制队列积压告警。**必须在排空之前查** —— 排完必然是 0，那时再查什么也看不见。
        /// </summary>
        /// <remarks>
        /// 控制队列无界、永不丢事件，所以积压到这个量只可能意味着两件事之一：
        /// pump 没在跑，或者某个回调卡住了。它与 pump 存活检测是同一件事的两个观测面。
        /// </remarks>
        private static void WarnIfControlQueueBacklogged()
        {
            if (NativeMethods.dcu_event_queue_depth(out var depth) != NativeMethods.Success) return;
            if (depth <= ControlQueueDepthWarn) return;
            if (!Throttle.Note("control-queue-depth", depth, out var suppressed, out var peak)) return;

            DataChannelLog.Emit(LogLevel.Warning,
                "The control-event queue has " + depth + " entries backed up, over the " + ControlQueueDepthWarn + " threshold. "
                + "The queue is unbounded and never drops control events, so a backlog can only mean the pump is not running or a callback is stuck."
                + Throttle.SuppressedSuffix(suppressed, peak, " entries"));
        }

        /// <summary>
        /// 检查「距上次 <see cref="Pump"/> 过去多久」，超阈值就报并**重试注册一次**。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 它防的不是启动时注册失败（那几乎不可能，而且当场就会throw），而是**注册之后被
        /// 抹掉**：任何第三方包只要从 <c>GetDefaultPlayerLoop()</c> 重建再
        /// <c>SetPlayerLoop</c>，就会把我们的条目连同别人的一起丢掉，而我们的
        /// <c>_pumpRegistered</c> 还写着 true。这不是假想 —— 本仓库里 vendored 的 R3
        /// 就往 PlayerLoop 里插东西。
        /// </para>
        /// <para>
        /// **价值在检测与归因，不在修复**（#45 决议 2）。故障本身极显眼（什么都不通），
        /// 难的是归因 —— 第一嫌疑人永远是网络或 TURN，绝不会是帧循环。所以错误消息
        /// 必须点名成因与修法。
        /// </para>
        /// <para>
        /// **重试恰好一次，之后停手。** 无限自愈是在跟另一个包来回抢 PlayerLoop，而且
        /// 是静默地抢 —— 与 <see cref="SafeDispatch"/> 里否掉「连抛 N 次自动退订」是
        /// 同一个形状：**静默改变别人建立的状态，比一条吵闹的日志更坏。**
        /// </para>
        /// <para>
        /// 用 <c>Stopwatch</c> 的单调墙钟而不是 <c>Time.frameCount</c>（编辑模式下不可靠，
        /// 而 pump 在编辑模式是常驻的），也不必为编辑模式分叉出
        /// <c>EditorApplication.timeSinceStartup</c> —— 同一个时间戳每帧已经为慢帧告警取过。
        /// </para>
        /// </remarks>
        /// <summary>存活判定的三值结论（#147）。</summary>
        internal enum PumpLivenessVerdict
        {
            /// <summary>无可判定故障：不够陈旧，或循环本身没在推进（暂停/挂起等冻结形态）。</summary>
            Silent = 0,
            /// <summary>编辑模式：常驻 pump 缺席是已知限制，如实提示，不动重试状态机。</summary>
            EditModeNotice = 1,
            /// <summary>循环在推进而泵没跑 —— 真停摆，进入重试状态机。</summary>
            Stalled = 2
        }

        /// <summary>
        /// 纯谓词：不读不写任何状态，三个观测值进、一个结论出（#147）。
        /// </summary>
        /// <remarks>
        /// 抽出来是为了托管档表测边界（delta 0/1/2 沉默、3 触发、5s 界）——
        /// 「编辑器暂停」「移动端挂起」没法在测试进程里自演（#145 是真编辑器手测的），
        /// 谓词表测 + 现有 PlayMode 集成测试合起来才是可自动化的覆盖。
        /// 判序有意为之：编辑模式先于帧判定 —— 编辑模式下帧号不推进，
        /// 若先查帧会把「常驻 pump 缺席」的如实提示也一并吞掉。
        /// </remarks>
        internal static PumpLivenessVerdict JudgePumpLiveness(double staleSeconds, long frameDelta, bool isPlaying)
        {
            if (staleSeconds < PumpStaleSeconds) return PumpLivenessVerdict.Silent;
            if (!isPlaying) return PumpLivenessVerdict.EditModeNotice;
            if (frameDelta < PumpStallFrames) return PumpLivenessVerdict.Silent;
            return PumpLivenessVerdict.Stalled;
        }

        internal static void CheckPumpLiveness(string api)
        {
            if (!_nativeReady || _lastPumpTimestamp == 0) return;

            var staleSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - _lastPumpTimestamp)
                               / (double)System.Diagnostics.Stopwatch.Frequency;
            // #147（依据 #145 的实测）：触发条件是「stale > 5s **且** 帧推进 ≥ 3」。
            // 诊断的真实问题从来是**循环在跑而我们的条目没跑** —— 帧没推进（编辑器
            // 暂停、移动端后台挂起、一切冻结形态）说明循环本身没在跑，不是泵的故障：
            // 天然沉默，唯一的重试额度不再被幻影消耗。#145 实测过反例：暂停 >5s 后
            // 一次 new PeerConnection 就把额度烧掉，之后真被第三方抹除时得不到自愈，
            // 且报错把矛头指向不存在的第三方。帧门禁让下面那段「第三方抹了
            // PlayerLoop」的文案只在它唯一成立的场景打出 —— 文案零改动（#147）。
            var frameDelta = (long)Time.frameCount - _lastPumpFrame;
            var verdict = JudgePumpLiveness(staleSeconds, frameDelta, Application.isPlaying);
            if (verdict == PumpLivenessVerdict.Silent) return;

            // 编辑模式单独一条路径。**pump 在编辑模式确实没在跑，报出来没有错** ——
            // SPEC §6 要求编辑模式下 pump 常驻，而那条（连同五个生命周期场景）属于
            // S8 / #37，还没落地。错的是把它当成「被第三方抹掉」来处理：
            //
            //   1. 措辞会指向一个不存在的第三方，把人引到错误的方向；
            //   2. 重试注册在这里**根本无效** —— 编辑模式不跑 PlayerLoop 的 Update；
            //   3. 那次无效的重试会把「只重试一次」的额度用掉，等真到播放模式里
            //      被第三方抹掉时，保护机制已经没了。
            //
            // 实测过这三条都会发生：编辑模式下调一次 new PeerConnection 就报出
            // 「pump 已经 934.8 秒没有运行」，并把 _pumpReregisterAttempted 置了位。
            // 退出播放模式**不触发域重载**（#37 实测，本次复现：pumpTicks 4147 -> 8358
            // 未归零、_pumpRegistered 残留 true），所以播放模式装上的那个标志会一路
            // 留到编辑模式，光看 _pumpRegistered 挡不住。
            //
            // S8 落地后编辑模式有了常驻 pump，时间戳一直在更新，这条自然不再触发 ——
            // 不需要谁记得回来删掉一个临时分支。
            if (verdict == PumpLivenessVerdict.EditModeNotice)
            {
                if (Throttle.Note("pump-edit-mode", staleSeconds, out var es, out var ep))
                    DataChannelLog.Emit(LogLevel.Warning,
                        api + ": in Edit Mode the pump has not run for " + staleSeconds.ToString("0.#") + " s. "
                        + "This is a known limitation, not a fault: a resident Edit Mode pump is not implemented yet (SPEC section 6 / #37). "
                        + "To receive events in Edit Mode, call DataChannelRuntime.Pump() yourself every frame."
                        + Throttle.SuppressedSuffix(es, ep, " s"));
                return;
            }

            if (_pumpRetryExhausted)
            {
                if (Throttle.Note("pump-dead", staleSeconds, out var s, out var p))
                    DataChannelLog.Emit(LogLevel.Error,
                        api + ": the pump has not run for " + staleSeconds.ToString("0.#") + " s (it has run for "
                        + _pumpTicks + " frames in total), "
                        + "and registration retries have STOPPED (one retry was already wiped out again). This package will not keep fighting another package over the PlayerLoop. "
                        + "Find out who is calling SetPlayerLoop, or call DataChannelRuntime.Pump() manually every frame."
                        + Throttle.SuppressedSuffix(s, p, " s"));
                return;
            }

            if (!_pumpReregisterAttempted)
            {
                _pumpReregisterAttempted = true;
                DataChannelLog.Emit(LogLevel.Error,
                    api + ": the pump has not run for " + staleSeconds.ToString("0.#") + " s"
                    + " (it ran for " + _pumpTicks + " frames this session). "
                    + "Events will not be delivered and the connection will look like it cannot connect. The most likely cause is NOT the network: "
                    + "some third-party package rebuilt the loop with PlayerLoop.GetDefaultPlayerLoop() and then called SetPlayerLoop, "
                    + "dropping this package's entry along the way (R3 in this repo inserts into the PlayerLoop). "
                    + "Fix: initialise this package after that one finishes registering, or call "
                    + "DataChannelRuntime.Pump() manually every frame. Retrying registration ONCE now.");

                _pumpRegistered = false;
                // 这里**不能**让 RegisterPump 的异常穿出去：调用方只是在
                // new PeerConnection / Send，不该因为「重试注册没成功」拿到一个异常。
                // 注册路径本身该抛（见 RegisterPump），但这条是诊断路径，已经在报错了。
                try { RegisterPump(); }
                catch (Exception e) { DataChannelLog.Emit(LogLevel.Error, "Retrying pump registration failed", e); }
                // 给新注册一个宽限期，否则下一次调用会立刻又判超时 —— 那时它还没跑过一帧。
                _lastPumpTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                return;
            }

            _pumpRetryExhausted = true;
            DataChannelLog.Emit(LogLevel.Error,
                api + ": the pump still has not run after a registration retry (stalled for " + staleSeconds.ToString("0.#") + " s). "
                + "Retries have STOPPED: re-inserting again would mean an endless tug-of-war with another package over the PlayerLoop, "
                + "and that silent back-and-forth is harder to diagnose than one explicit error. Call DataChannelRuntime.Pump() manually.");
        }

        /// <summary>
        /// 排空原生日志队列。该队列**有界、丢最旧**（与永不丢的控制队列语义相反），
        /// 丢弃数以一条 warning 暴露。
        /// </summary>
        private static void DrainNativeLogs()
        {
            var droppedTotal = 0;
            while (true)
            {
                var rc = NativeMethods.dcu_log_next(
                    out var level, _logBuf, _logBuf.Length, out var len, out var dropped);
                droppedTotal += dropped;

                if (rc == NativeMethods.ErrTooSmall)
                {
                    EnsureCapacity(ref _logBuf, len, "log", ref _grewLog);
                    rc = NativeMethods.dcu_log_next(
                        out level, _logBuf, _logBuf.Length, out len, out dropped);
                    droppedTotal += dropped;
                }

                if (rc == NativeMethods.ErrNotAvail) break;
                if (rc != NativeMethods.Success)
                {
                    DataChannelLog.Emit(LogLevel.Warning,
                        "dcu_log_next failed: " + MapError(rc) + " (raw=" + rc + ")");
                    break;
                }

                DataChannelLog.Emit((LogLevel)level, Encoding.UTF8.GetString(_logBuf, 0, len));
            }

            if (droppedTotal > 0)
            {
                // 在 Verbose 压测下这是**预期行为**，不是缺陷 —— 日志可丢，
                // 控制事件不可丢，两条队列的策略是刻意相反的。
                DataChannelLog.Emit(LogLevel.Warning,
                    "dropped " + droppedTotal + " native log line(s): log queue is bounded by design");
            }
        }

        private static void DrainControlEvents()
        {
            // #149：这里曾有一个 256/帧 的安全上限 —— 首个提交的无决议遗留，早于
            // #38 定「两段都无预算」。事件不会丢（队列无界），但规格说排空就必须
            // 排空；它护的威胁（派发中同步再生产事件的持续环）经 #149 审定不存在，
            // 真出现时单帧挂死也比被上限掩成静默慢化更可查。终止条件三条不变：
            // NOT_AVAIL / header.type == None / 非 Success。
            while (true)
            {
                var rc = NativeMethods.dcu_event_next(out var header,
                    _payloadBuf, _payloadBuf.Length, _payload2Buf, _payload2Buf.Length);

                if (rc == NativeMethods.ErrTooSmall)
                {
                    // header 里两个长度都是**精确值**，且事件未被消费；单消费者契约
                    // 保证两次调用之间队首不变，所以扩容后**一次重试必然成功**。
                    EnsureCapacity(ref _payloadBuf, header.payload_len, "control-event payload", ref _grewPayload);
                    EnsureCapacity(ref _payload2Buf, header.payload2_len, "control-event payload2", ref _grewPayload2);
                    rc = NativeMethods.dcu_event_next(out header,
                        _payloadBuf, _payloadBuf.Length, _payload2Buf, _payload2Buf.Length);
                }

                if (rc == NativeMethods.ErrNotAvail || header.type == NativeMethods.EventType.None)
                    break;

                if (rc != NativeMethods.Success)
                {
                    DataChannelLog.Emit(LogLevel.Warning,
                        "dcu_event_next failed: " + MapError(rc) + " (raw=" + rc + ")");
                    break;
                }

                string payload = null;
                string payload2 = null;
                if (header.payload_len > 0)
                    payload = Encoding.UTF8.GetString(_payloadBuf, 0, header.payload_len);
                if (header.payload2_len > 0)
                    payload2 = Encoding.UTF8.GetString(_payload2Buf, 0, header.payload2_len);

                Dispatch(header, payload, payload2);
            }
        }

        private static void DrainMessages()
        {
            // 遍历**快照**而非字典本身：拉到消息会当场派发，而应用在回调里
            // Dispose() 通道或 CreateDataChannel() 都合法，两者都改动 HandleTable
            // 的字典 —— Dictionary 迭代中被修改会抛，且那个异常来自我们自己的迭代，
            // 每订阅者的隔离罩不住它。
            HandleTable.SnapshotDataChannels(ChannelSnapshot);
            for (int i = 0; i < ChannelSnapshot.Count; i++)
            {
                var dc = ChannelSnapshot[i];
                // 不按 open 状态过滤：State 现在是活查询（一次 P/Invoke），
                // 而 dcu_dc_receive 对未 open 的通道本来就返回 NOT_AVAIL ——
                // 让它来判，省掉每通道每帧一次多余的穿越。
                if (dc == null || dc.IsDisposed) continue;
                DrainChannel(dc);
            }
            ChannelSnapshot.Clear();
        }

        /// <summary>拉空一个通道的接收队列。每次拉取前**逐项验活**。</summary>
        private static void DrainChannel(DataChannel dc)
        {
            while (true)
            {
                // 回调可能刚把它 Dispose 掉。只查这个 —— 不查 open，因为关闭中的
                // 通道仍可能有残留消息要投递（见 DcClosed 的处理）。
                if (dc.IsDisposed) return;

                var rc = NativeMethods.dcu_dc_receive(
                    dc.NativeHandle, _messageBuf, _messageBuf.Length, out var n);

                if (rc == NativeMethods.ErrTooSmall)
                {
                    EnsureCapacity(ref _messageBuf, n, "message", ref _grewMessage);
                    rc = NativeMethods.dcu_dc_receive(
                        dc.NativeHandle, _messageBuf, _messageBuf.Length, out n);
                }

                if (rc == NativeMethods.ErrNotAvail) return;

                if (rc != NativeMethods.Success)
                {
                    DataChannelLog.Emit(LogLevel.Warning,
                        "dcu_dc_receive failed: " + MapError(rc) + " (raw=" + rc + ")");
                    return;
                }

                dc.RaiseMessage(new ReadOnlySpan<byte>(_messageBuf, 0, n));
            }
        }

        /// <summary>
        /// 按需增长，**永不收缩**（#45 决议 1，推翻 #38 决议 7 的滞回收缩）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 决定性事实是**单条消息尺寸有天花板**：
        /// <c>PeerConnection::remoteMaxMessageSize()</c> 取本地上限
        /// （<c>DEFAULT_LOCAL_MAX_MESSAGE_SIZE</c> = 256KB）与对端 SDP 声明值的较小者。
        /// 默认配置下四路缓冲合计常驻约 0.5MB —— 为省这点建一套双窗口峰值跟踪，
        /// 换来的是它自己的规格、测试与失败模式，不划算。应用把
        /// <c>MaxMessageSize</c> 调大，常驻上界随之变大，那是它自己要求的。
        /// </para>
        /// <para>
        /// 顺带说明为什么「固定容量 + 超尺寸走临时数组」仍然被否：它会**静默拆掉零分配
        /// 承诺** —— 正常载荷就是 200KB 的应用（视频切片、地图数据、存档同步都在这个
        /// 量级）会变成每条消息一次堆分配。只涨不缩没有这个问题，它只是持有内存。
        /// </para>
        /// </remarks>
        private static void EnsureCapacity(ref byte[] buf, int need, string which, ref bool alreadyGrew)
        {
            if (buf != null && buf.Length >= need) return;

            var from = buf?.Length ?? 0;
            buf = new byte[Math.Max(need, 1024)];

            // 只记**首次**超基线，且只记 Info：这是正常的自适应，不是故障。
            if (alreadyGrew) return;
            alreadyGrew = true;
            DataChannelLog.Emit(LogLevel.Info,
                which + " buffer grew past its baseline for the first time: " + from + " -> " + buf.Length + " bytes. "
                + "Buffers only grow, never shrink, so this size is now resident (bounded by the negotiated MaxMessageSize).");
        }

        private static void Dispatch(NativeMethods.EventHeader h, string p1, string p2)
        {
            switch (h.type)
            {
                case NativeMethods.EventType.LocalDescription:
                    if (HandleTable.TryGetPc(h.pc, out var pc1))
                        pc1.RaiseLocalDescription(p1 ?? string.Empty, p2 ?? string.Empty);
                    break;
                case NativeMethods.EventType.LocalCandidate:
                    if (HandleTable.TryGetPc(h.pc, out var pc2))
                        pc2.RaiseLocalCandidate(p1 ?? string.Empty, p2);
                    break;
                case NativeMethods.EventType.ConnectionState:
                    if (HandleTable.TryGetPc(h.pc, out var pc3))
                        pc3.RaiseConnectionState(MapConnectionState(h.state));
                    break;
                case NativeMethods.EventType.GatheringState:
                    if (HandleTable.TryGetPc(h.pc, out var pc4))
                        pc4.RaiseGatheringState(MapGatheringState(h.state));
                    break;
                case NativeMethods.EventType.IncomingDataChannel:
                    if (HandleTable.TryGetPc(h.pc, out var pc5))
                    {
                        // 入向通道**照单全收**，由 PC 拥有（#29 决议 3）。
                        var dc = pc5.AdoptIncomingDataChannel(h.dc, p1 ?? string.Empty);
                        if (dc != null) pc5.RaiseIncomingDataChannel(dc);
                    }
                    else
                    {
                        // 事件排队期间父 PC 已被释放（或已被 GC）。上游
                        // rtcDeletePeerConnection 不清子通道的表项，所以这个句柄
                        // 现在谁也够不着 —— 不就地销毁它就是一个纯原生泄漏。
                        try { NativeMethods.dcu_dc_destroy(h.dc); } catch { /* ignore */ }
                    }
                    break;
                case NativeMethods.EventType.DcOpen:
                    if (HandleTable.TryGetDc(h.dc, out var d1))
                        d1.RaiseOpen();
                    break;
                case NativeMethods.EventType.DcClosed:
                    if (HandleTable.TryGetDc(h.dc, out var d2))
                    {
                        // 先把该通道的接收队列拉空再报 Closed，否则「关闭前收到的
                        // 消息」会丢或乱序（SPEC §4）。此刻句柄仍可解析 —— 原生对象
                        // 要到 dcu_dc_destroy 才从表里摘除。
                        DrainChannel(d2);
                        d2.RaiseClosed();
                    }
                    break;
                case NativeMethods.EventType.DcError:
                    if (HandleTable.TryGetDc(h.dc, out var d3))
                        d3.RaiseError(p1 ?? "error");
                    break;
            }
        }

        /// <summary>
        /// 把 ABI 的原始返回值映射到 <see cref="DataChannelError"/>。
        /// 不认识的值落 <see cref="DataChannelError.Unknown"/>，原始数值仍由
        /// <see cref="DataChannelException.RawCode"/> 带出。
        /// </summary>
        internal static DataChannelError MapError(int raw)
        {
            switch (raw)
            {
                case NativeMethods.ErrInvalid: return DataChannelError.Invalid;
                case NativeMethods.ErrFailure: return DataChannelError.Failure;
                case NativeMethods.ErrNotAvail: return DataChannelError.NotAvailable;
                case NativeMethods.ErrTooSmall: return DataChannelError.TooSmall;
                case NativeMethods.ErrUpstreamUnknown: return DataChannelError.UpstreamUnknown;
                default: return DataChannelError.Unknown;
            }
        }

        // RequireCreate 已删除：#31 把 ABI 统一成「返回码 + out 参数」之后，
        // 「返回值兼作数据」的两种形状都没了，判成功只剩 rc == DCU_OK 一种写法。
        internal static void RequireOk(int code, string what)
        {
            if (code == NativeMethods.Success) return;
            throw NewException(code, what);
        }

        /// <summary>
        /// 异常消息分**两类**措辞而非逐码一句：分流的价值是告诉人**该查自己还是该找我们**，
        /// 再细就是维护负担。<c>RawCode</c> 始终带在消息里。
        /// </summary>
        private static DataChannelException NewException(int code, string what)
        {
            var err = MapError(code);
            var selfFixable = err == DataChannelError.Invalid || err == DataChannelError.TooSmall;
            var message = selfFixable
                ? what + ": invalid argument (code=" + err + ", raw=" + code
                  + "). Check whether the channel is already disposed, or the payload exceeds the negotiated MaxMessageSize."
                : what + ": runtime failure (code=" + err + ", raw=" + code
                  + "). This is usually not a usage problem; please file an issue and attach the DataChannelLog output.";
            return new DataChannelException(message, err, code);
        }

        private static ConnectionState MapConnectionState(int raw)
        {
            switch (raw)
            {
                case 0: return ConnectionState.New;
                case 1: return ConnectionState.Connecting;
                case 2: return ConnectionState.Connected;
                case 3: return ConnectionState.Disconnected;
                case 4: return ConnectionState.Failed;
                case 5: return ConnectionState.Closed;
                default:
                    DataChannelLog.Emit(LogLevel.Warning,
                        "Unrecognised ConnectionState from native: " + raw + " -> Unknown");
                    return ConnectionState.Unknown;
            }
        }

        private static GatheringState MapGatheringState(int raw)
        {
            switch (raw)
            {
                case 0: return GatheringState.New;
                case 1: return GatheringState.InProgress;
                case 2: return GatheringState.Complete;
                default:
                    DataChannelLog.Emit(LogLevel.Warning,
                        "Unrecognised GatheringState from native: " + raw + " -> Unknown");
                    return GatheringState.Unknown;
            }
        }
    }
}
