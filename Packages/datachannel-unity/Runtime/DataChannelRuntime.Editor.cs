#if UNITY_EDITOR
using UnityEditor;

namespace DataChannelUnity
{
    /// <summary>
    /// 编辑器侧的生命周期接线（#37 / SPEC §6）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **一条原则，五个场景全由它推出，没有特例要背：**
    /// </para>
    /// <para>
    /// &gt; 域**要死了** → 托管侧即将失去全部引用 → 只能抡大锤 <c>dcu_shutdown()</c>。<br/>
    /// &gt; 域**还活着** → 我们还握着引用 → 用精确工具 <c>DisposeAllLive()</c>，不抡锤。
    /// </para>
    /// <para>
    /// 拆成 partial 的另一半文件，只是因为它整块是 Editor-only；语义上它就是
    /// <see cref="DataChannelRuntime"/> 的一部分，不是外挂的工具类。
    /// </para>
    /// </remarks>
    public static partial class DataChannelRuntime
    {
        /// <summary>
        /// 「上一次域死掉之前，native 初始化过吗」。
        /// </summary>
        /// <remarks>
        /// 用 <see cref="SessionState"/> 而不是静态字段：它是唯一能活过域重载、
        /// 又不会活过编辑器重启的存储，正好对上「本次编辑器会话」这个语义。
        /// </remarks>
        private const string SessionKeyWasInitialized = "DataChannelUnity.NativeWasInitialized";

        [InitializeOnLoadMethod]
        private static void WireEditorLifecycle()
        {
            // 每次域重载后都会跑一遍。新域里这些事件本来就是空的，先 -= 是为了
            // 让「同一个域里被调用两次」也不会重复订阅。
            EditorApplication.update -= EditorPump;
            EditorApplication.update += EditorPump;

            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.quitting += OnEditorQuitting;
        }

        /// <summary>
        /// 编辑模式的**常驻** pump（#37 决议 1）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// **不能用 <c>PlayerLoop</c>** —— 它在编辑模式根本不执行。这一点是实测出来的
        /// 而不是查文档得知的：S7 期间在编辑模式往 <c>PlayerLoop</c> 插了 pump 条目，
        /// 条目确实在树里，而 <c>Pump()</c> 934.8 秒一次没跑。所以编辑模式只能挂
        /// <see cref="EditorApplication.update"/>。SPEC §6 原先那格写的是
        /// 「<c>EnteredEditMode</c> → <c>RegisterPump()</c>」，那个手段达不到它自己
        /// 要的效果，已随本片改正。
        /// </para>
        /// <para>
        /// **native 仍是懒初始化**：这里只调 <see cref="Pump"/>，而 <c>Pump</c> 在
        /// native 未就绪时会在戳完时间戳之后直接返回。所以「编辑模式常驻」并不意味着
        /// 「一打开编辑器就加载原生库」。
        /// </para>
        /// </remarks>
        private static void EditorPump()
        {
            // 播放模式下由 PlayerLoop 那条驱动。两条一起跑不会出错（排空是幂等的），
            // 但没必要，而且会让「pump 到底是谁在驱动」多出一个答案。
            if (EditorApplication.isPlaying) return;
            Pump();
        }

        /// <summary>域将死：大锤。覆盖编辑模式重编译，以及 Reload Domain 开时进入播放。</summary>
        private static void OnBeforeAssemblyReload()
        {
            // 先记下「刚才是否 init 过」—— 静态字段马上就要蒸发了。
            SessionState.SetBool(SessionKeyWasInitialized, _nativeReady);
            DisposeAllLive();
            UnregisterPump();
            ShutdownNative();
        }

        /// <summary>
        /// 域重建之后，**仅当之前 init 过**才重新初始化。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 无条件重建会让同一个编辑模式的状态取决于「你有没有编译过一次」——
        /// Unity 官方的 <c>ContextManager</c> 正是因为不记这个标志而有这个毛病：
        /// 冷启动编辑器没有 context，随便触发一次编译就有了。
        /// 补上标志位得到的才是它想要的「懒初始化 + 跨重载恢复」。
        /// </para>
        /// <para>
        /// **进播放态引起的那次重载在这里不做恢复。** #146 之前的理由是「做了会被
        /// 立刻撤销再重做」—— <c>ResetStaticsOnEnterPlayMode</c> 清标志后
        /// <c>Bootstrap</c> 会再 <c>EnsureNative()</c>，Console 出现两行
        /// <c>Native library initialized</c>（#141 记的噪声）。#146 落地后
        /// <c>Bootstrap</c> **不再加载 native**，这里的跳过依然正确：播放会话由
        /// 首次构造 / <c>Preload()</c> 惰性加载，本方法若在此恢复反而会把
        /// 「进播放不用包也加载」的旧行为带回来。
        /// </para>
        /// <para>
        /// **这道门不能换成删掉本方法。** 本方法覆盖 <c>Bootstrap</c> 到不了的场景 ——
        /// 编辑模式下用 API、从不进播放态（EditorWindow 驱动一个 PeerConnection，或
        /// EditMode 测试）：那条路上 <c>RuntimeInitializeOnLoadMethod</c> 根本不触发
        /// （见 <c>MainThread</c> 的两个 Capture 入口）。两者只在这一次重载上重叠。
        /// </para>
        /// <para>
        /// 谓词取值是**实测**的，不是推的（2022.3.62f3，Reload Domain 开）：主编辑器
        /// 进程里，纯编辑模式重编时 <c>afterAssemblyReload</c> 读到 <c>false</c>，
        /// 进播放态那次读到 <c>true</c>。量的时候必须按进程区分 ——
        /// <c>AssetImportWorker</c> 是独立 Unity 实例、各有自己的域、
        /// <c>[InitializeOnLoad]</c> 照样执行，而它们没有播放态，谓词恒为 <c>false</c>；
        /// 不区分就会把 worker 的 false 读成主编辑器的答案，结论正好相反。
        /// </para>
        /// </remarks>
        private static void OnAfterAssemblyReload()
        {
            if (!SessionState.GetBool(SessionKeyWasInitialized, false)) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            EnsureNative();
        }

        /// <summary>
        /// 退出播放模式。**这个钩子必须显式挂**（#37 决议 3）。
        /// </summary>
        /// <remarks>
        /// 退出播放**不触发域重载** —— 实测两次确认（SPEC §6），S7 期间又复现了一遍：
        /// pump 计数 4147 → 8358 没有归零、注册标志原样残留。不挂这个钩子，
        /// 播放期的托管静态量与原生对象会一起漏进编辑模式。
        ///
        /// <c>com.unity.webrtc</c> 只靠域重载事件驱动，带的正是这个 bug；而
        /// <c>com.unity.entities</c> 的 <c>RuntimeContentSystem</c> 专门处理
        /// <c>ExitingPlayMode</c>，是在绕开同一个坑。
        ///
        /// **不 shutdown**：域还活着，精确工具就够。编辑模式的常驻 pump 由
        /// <see cref="EditorPump"/> 一直挂着，不需要在 <c>EnteredEditMode</c> 里重装。
        /// </remarks>
        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingPlayMode) return;
            DisposeAllLive();
            UnregisterPump();
        }

        /// <summary>关闭编辑器：先摘订阅，再全量清理。</summary>
        /// <remarks>
        /// **先 <c>-=</c> 掉自己全部订阅**再动手（#37 决议 5）：清理会触发原生回调、
        /// 进而推事件，而我们正在把自己拆掉，不该在拆到一半时被回调打回来。
        ///
        /// 这里**抡锤**：关编辑器时阻塞无所谓，而且正需要靠它打出最终的泄漏账单。
        /// </remarks>
        private static void OnEditorQuitting()
        {
            EditorApplication.update -= EditorPump;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.quitting -= OnEditorQuitting;

            DisposeAllLive();
            UnregisterPump();
            ShutdownNative();
        }
    }
}
#endif
