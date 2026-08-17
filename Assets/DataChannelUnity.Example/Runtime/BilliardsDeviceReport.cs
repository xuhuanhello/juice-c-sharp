using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FishNet.Managing;
using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// 真机报告写入器：一局打完之后留下**机器能判**的证据。
    ///
    /// 截图不是证据，「我打了一局看着没问题」也不是（`CONTRIBUTING.md`）。而 iOS 上没有日志文件
    /// 可收 —— `Debug.Log` 进设备 console 就没了。所以写
    /// <see cref="Application.persistentDataPath"/>，用 `devicectl` 抄下来
    /// （`docs/verification-mcp.md` §8b-iOS 第 4 步）。
    ///
    /// **不写 `Application.dataPath/../Logs`**，虽然台球现有的两处报告都写那里：那在真机上是
    /// 只读的 app bundle，写不进去**且失败是静默的** —— 于是会得到一次「跑完了、没有报告」，
    /// 而那与「压根没跑」不可区分。
    ///
    /// ## 形状：结构化 JSON，一条 claim 一行（Q5）
    ///
    /// 刻意**不借** `DeviceVerificationRunner` 那套 NUnit 词汇。那个 runner 复现三条 Runtime
    /// 契约，`total/passed/failed` 有意义，而它自己都在 `framework` 属性里写明「不是 Unity Test
    /// Framework」—— 理由是一份误报自己身份的报告会告诉读者首选路线跑过了而它没跑。
    /// **打一局台球不是一套契约**，套上 `total/passed/failed` 正好会犯它防的那个错。
    ///
    /// 所以：逐条 claim 带实测值，判词是 <c>holds</c> / <c>violated</c> /
    /// <c>not-observed</c>，没有 pass/fail。判据是「离开产生它的机器之后，它还能不能自己交代
    /// 自己是谁跑的」—— 于是 <see cref="WriteProvenance"/> 里那一段是必需的，不是装饰。
    ///
    /// ## not-observed 不是失败
    ///
    /// 三分而不是二分，因为**这份报告里有一半的 claim 只有 host 看得见**：每 tick 的字节数与背压
    /// 由服务端的发送回调采样，每一杆的 settle 与首碰由 host 的物理步产生。纯 client 上它们不是
    /// 「不成立」，是**没有观测**。二分会把这个差别压成 fail，于是一份正常的 client 报告看着像
    /// 一半都坏了。两端各写一份，合起来才是完整的。
    ///
    /// ## 一台机器上跑两个进程时的文件名
    ///
    /// Editor 当 host、打包的 player 当 client 时，两者在 macOS 上的
    /// <see cref="Application.persistentDataPath"/> 由 companyName/productName 决定，很可能是
    /// **同一个目录**。所以文件名里带角色（`-host` / `-client`），否则后写的那份会盖掉先写的，
    /// 而两份报告的互补正是它们的价值。
    /// </summary>
    public sealed class BilliardsDeviceReport : MonoBehaviour
    {
        #region Tunables

        [SerializeField]
        [Tooltip("留空则自己找场景里的那一个。")]
        private BilliardsGame _game;

        [SerializeField]
        [Tooltip("留空则自己找。每 tick 的字节数与背压从它读。")]
        private OutboundByteMeter _meter;

        [SerializeField]
        [Tooltip("报告文件名的前缀。实际文件是 <前缀>-<角色>.json。")]
        private string _fileNamePrefix = "billiards-device-report";

        [SerializeField]
        [Tooltip("最低 fps 的判据。#139 定 50 —— 理由见 docs/billiards-touch-controls.md §8.2。")]
        private float _minimumFpsCriterion = 50f;

        [SerializeField]
        [Tooltip("起头这么多秒的帧不计入 fps —— 加载与首次编译着色器的抖动不是运行时帧率。")]
        private float _fpsWarmupSeconds = 3f;

        #endregion

        #region Claim model

        /// <summary>
        /// 一条 claim 的三种判词。**没有 pass/fail** —— 见类注释。
        /// </summary>
        private enum Verdict
        {
            /// <summary>量到了，且与判据相符。</summary>
            Holds,

            /// <summary>量到了，与判据不符。</summary>
            Violated,

            /// <summary>这一端看不到这个量，或者这次运行里它没发生过。不是失败。</summary>
            NotObserved
        }

        private sealed class Claim
        {
            public string Id;

            /// <summary>断言本身，一句话。</summary>
            public string Statement;

            /// <summary>判据，写成能对着实测值核的形式。</summary>
            public string Criterion;

            /// <summary>实测值。字符串而不是数字，因为有的 claim 的值是一组数。</summary>
            public string Measured;

            public Verdict Verdict;

            /// <summary>出处或者为什么是 not-observed。</summary>
            public string Note;
        }

        private static string VerdictText(Verdict verdict) => verdict switch
        {
            Verdict.Holds => "holds",
            Verdict.Violated => "violated",
            _ => "not-observed"
        };

        #endregion

        #region Observations

        private NetworkManager _manager;
        private DataChannelTransport _transport;
        private BilliardsRack _rack;

        /// <summary>一条连接量到的路径。key 是 connectionId，−1 是本机 client 那条。</summary>
        private readonly Dictionary<int, string> _connectionPaths = new();
        private readonly Dictionary<int, string> _remoteCandidates = new();

        private readonly List<long> _rttSamples = new();

        /// <summary>非零样本数。判据只看这些 —— 理由在 <see cref="SampleRoundTripTime"/> 里。</summary>
        private int _rttMeasuredSamples;

        /// <summary>读到 0 的样本数：那是「还没量到」，不是「0 毫秒」。</summary>
        private int _rttZeroSamples;

        private int _rttTickMultipleSamples;
        private float _rttSampleTimer;

        /// <summary>一杆的记录。settle 与首碰来自两个不同的事件，所以先攒后配。</summary>
        private sealed class ShotRecord
        {
            public int Shooter;
            public bool WasBreak;
            public float SettleSeconds;
            public int FirstContact;
            public ushort PocketedBefore;
            public ushort PocketedAfter;
            public bool Foul;
            public string FoulReason;
        }

        private readonly List<ShotRecord> _shots = new();
        private float _pendingSettleSeconds = -1f;

        private int _containmentTrips;

        // fps：两个窗口。整场那个答「这台机器跑得动吗」，Simulate 那个答「球在动的时候糊不糊」——
        // §8.2 的每帧位移只在球动的时候才有意义，而那才是判据要管的。
        private float _sessionMinFps = float.MaxValue;
        private double _sessionFrameSeconds;
        private int _sessionFrames;
        private float _shotMinFps = float.MaxValue;
        private double _shotFrameSeconds;
        private int _shotFrames;
        private float _sinceStart;

        private BilliardsPhase _phase = BilliardsPhase.Lobby;
        private byte _lastWinner = BilliardsRules.SeatNone;
        private bool _sawGameOver;
        private bool _sawAbandoned;

        // 重连那次：断连前后要对得上的三样（座位、分组、落袋掩码）。分组由座位派生（#132 预分组），
        // 所以座位没变就是分组没变 —— 不单独记一份会与掩码不一致的第二真相。
        //
        // **记的必须是「被留座的那个座位」，不是 LocalSeat。** 前一版记了 LocalSeat，而它在 host
        // 上永远是 0（host 自己），于是这条 claim 量的是「我自己的座位有没有变」—— host 从来没断过，
        // 所以它必然成立。#139 实测撞到这个假绿：报告写「座位 0→0 成立」，而日志里真正重连的是
        // **座位 1**（`座位 1 留 30s 等重连` → `座位 1 被令牌取回 ← connection 2`）。
        // 一条在没有观测到目标时也判成立的 claim，正是这份报告存在的目的要防的东西。
        private bool _reconnectObserved;
        private int _heldSeat = BilliardsRules.SeatNone;
        private int _connectionBeforeDrop = -1;
        private int _connectionAfterReturn = -1;
        private ushort _maskBeforeDrop;
        private ushort _maskAfterReturn;
        private bool _waitingForReconnect;

        /// <summary>
        /// 这一端自己掉过线又回来了没有。客户端**只能**从这个事实出发判重连 ——
        /// 座位表是 host 侧的，client 问不到别人的座位。
        /// </summary>
        private bool _localClientDropped;

        private int _localSeatBeforeDrop = BilliardsRules.SeatNone;
        private bool _clientWasUp;

        /// <summary>
        /// 每个座位上**最后一个活着的** connection id，每帧记一次。
        ///
        /// 必须提前记，不能等断连事件：<see cref="BilliardsGame.ReleaseConnection"/> 先把
        /// <c>ConnectionId</c> 置 −1，**再**发带 AwaitingReconnect 的状态消息。所以在
        /// <see cref="OnStateChanged"/> 里问座位要 id，拿到的永远是 −1 —— 于是「id 必须变」
        /// 那半条判据变成 −1→N，永真，判不出任何东西。#139 实测撞到：报告写
        /// <c>connection -1→2</c> 而它照样判成立。
        /// </summary>
        private readonly int[] _lastLiveConnection =
        {
            -1, -1
        };

        private string _writtenPath;
        private string _lastWriteError;

        #endregion

        #region Lifecycle

        private void Awake()
        {
            if (_game == null)
                _game = FindObjectOfType<BilliardsGame>();
            if (_meter == null)
                _meter = FindObjectOfType<OutboundByteMeter>();

            _rack = FindObjectOfType<BilliardsRack>();
            ResolveNetworking();
        }

        /// <summary>
        /// 找 NetworkManager 与它的 transport，从 <c>Update</c> 重试而不只在 <c>Awake</c> 试一次。
        ///
        /// 与 <see cref="OutboundByteMeter.TrySubscribe"/> 同一个理由：transport 要经
        /// NetworkManager 拿，而它在自己的 Awake 里才建 TransportManager，两个 Awake 之间 Unity
        /// 不给顺序保证。只在 Awake 试一次的后果是报告里 <c>transport</c> 为 null、
        /// <c>tickRate</c> 为 0 —— 而那两个字段正是用来分辨「量的是这个包」的。
        /// </summary>
        private void ResolveNetworking()
        {
            if (_manager == null)
                _manager = FindObjectOfType<NetworkManager>();

            if (_transport == null && _manager != null && _manager.TransportManager != null)
                _transport = _manager.TransportManager.Transport as DataChannelTransport;
        }

        private void OnEnable()
        {
            if (_game != null)
            {
                _game.StateChanged += OnStateChanged;
                _game.ShotJudged += OnShotJudged;
            }

            if (_rack != null)
            {
                _rack.ShotSettled += OnShotSettled;
                _rack.BallClamped += OnBallClamped;
            }
        }

        private void OnDisable()
        {
            if (_game != null)
            {
                _game.StateChanged -= OnStateChanged;
                _game.ShotJudged -= OnShotJudged;
            }

            if (_rack != null)
            {
                _rack.ShotSettled -= OnShotSettled;
                _rack.BallClamped -= OnBallClamped;
            }
        }

        /// <summary>
        /// 落盘的时机。都写同一个文件、内容全量，所以多写几次是幂等的 —— 而少写一次就什么都
        /// 没有。<c>OnApplicationPause</c> 那条是给 iOS 的：系统可能在暂停之后直接杀掉进程，
        /// 那时 <c>OnApplicationQuit</c> 不会来。
        ///
        /// <c>OnApplicationQuit</c> 与 <c>OnDestroy</c> 在同一次退出里**都会来**（Editor 里退出
        /// 播放态也是），所以第二次是纯噪声：一模一样的内容再写一遍、日志里再多一行。
        /// <see cref="_wroteOnShutdown"/> 把它们收成一次。
        /// </summary>
        private bool _wroteOnShutdown;

        private void OnApplicationQuit() => WriteOnce("application-quit");

        private void OnApplicationPause(bool paused)
        {
            // 暂停不是退出：恢复之后还会接着跑，所以这条不占用「退出只写一次」那个额度。
            if (paused)
                Write("application-pause");
        }

        private void OnDestroy() => WriteOnce("destroy");

        private void WriteOnce(string trigger)
        {
            if (_wroteOnShutdown)
                return;

            _wroteOnShutdown = true;
            Write(trigger);
        }

        #endregion

        #region Sampling

        private void Update()
        {
            _sinceStart += Time.unscaledDeltaTime;

            ResolveNetworking();
            TrackSeatConnections();
            TrackLocalClientDrop();
            SampleFps();
            SampleRoundTripTime();
            SampleConnectionPaths();
        }

        private void SampleFps()
        {
            // 起头几秒不计：加载、着色器首次编译、连接握手都在这段里，它们的抖动不是运行时帧率。
            if (_sinceStart < _fpsWarmupSeconds)
                return;

            float delta = Time.unscaledDeltaTime;
            if (delta <= 0f)
                return;

            float fps = 1f / delta;

            _sessionFrames++;
            _sessionFrameSeconds += delta;
            if (fps < _sessionMinFps)
                _sessionMinFps = fps;

            if (_phase != BilliardsPhase.Simulate)
                return;

            _shotFrames++;
            _shotFrameSeconds += delta;
            if (fps < _shotMinFps)
                _shotMinFps = fps;
        }

        /// <summary>
        /// 每秒一次，与 FishNet 自己更新 <c>RoundTripTime</c> 的频率同阶。同时数「是 tick 的整数
        /// 倍」的样本数：那个值量化到 tick，所以不是整数倍就说明读错了对象。
        /// </summary>
        private void SampleRoundTripTime()
        {
            if (_manager == null || _manager.TimeManager == null)
                return;
            if (!ClientUp)
                return;

            _rttSampleTimer += Time.unscaledDeltaTime;
            if (_rttSampleTimer < 1f)
                return;

            _rttSampleTimer = 0f;

            long rtt = _manager.TimeManager.RoundTripTime;
            _rttSamples.Add(rtt);

            // **0 单独数，不当成一次测量。** FishNet 在第一次 ping 往返回来之前这个值就是 0，
            // 而 0 是任何数的整数倍 —— 全是 0 的一组样本会让下面那条判据成立，于是「RTT 从没量到」
            // 会读成「RTT 是 tick 的整数倍」。#139 实测踩到：98 个样本 98 个「通过」，其中 min=0。
            if (rtt <= 0)
            {
                _rttZeroSamples++;
                return;
            }

            double tickMs = _manager.TimeManager.TickDelta * 1000d;
            if (tickMs <= 0d)
                return;

            _rttMeasuredSamples++;

            double quotient = rtt / tickMs;
            // 半档以内算整数倍。档宽本身是 33 ms（TickRate 30），而 RTT 是整毫秒，所以
            // 严格取整会因为一两毫秒的舍入判错。
            if (Math.Abs(quotient - Math.Round(quotient)) < 0.5d)
                _rttTickMultipleSamples++;
        }

        private void SampleConnectionPaths()
        {
            if (_transport == null || _manager == null)
                return;

            // 本机 client 那条用负数（transport 的约定）。host 上它是 loopback，所以必然 Direct ——
            // 记下来正是为了让读报告的人看见这一点，而不是把它当成「直连成功了」。
            if (ClientUp &&
                _transport.TryGetConnectionPath(-1, out var localPath, out var localSdp))
            {
                _connectionPaths[-1] = localPath.ToString();
                _remoteCandidates[-1] = Shorten(localSdp);
            }

            if (!ServerUp)
                return;

            foreach (var kv in _manager.ServerManager.Clients)
            {
                if (_transport.TryGetConnectionPath(kv.Key, out var path, out var sdp))
                {
                    _connectionPaths[kv.Key] = path.ToString();
                    _remoteCandidates[kv.Key] = Shorten(sdp);
                }
            }
        }

        private static string Shorten(string sdp)
        {
            if (string.IsNullOrEmpty(sdp))
                return string.Empty;

            int i = sdp.IndexOf("typ ", StringComparison.Ordinal);
            return i >= 0 ? sdp.Substring(i) : sdp;
        }

        #endregion

        #region Event capture

        private void OnShotSettled(float seconds)
        {
            // 先来的是这条（物理步里发出），`ShotJudged` 紧随其后。攒着等判定，而不是这里就建一条
            // 记录：`ShotSettled` 也会由场景里的 BreakProbe 与突发测量触发，那些不是回合。
            _pendingSettleSeconds = seconds;
        }

        private void OnShotJudged(ShotOutcome shot, TurnVerdict verdict)
        {
            _shots.Add(new ShotRecord
            {
                Shooter = shot.Shooter,
                WasBreak = shot.WasBreak,
                SettleSeconds = _pendingSettleSeconds,
                FirstContact = shot.FirstContact,
                PocketedBefore = shot.PocketedBefore,
                PocketedAfter = shot.PocketedAfter,
                Foul = verdict.Foul,
                FoulReason = verdict.FoulReason
            });

            _pendingSettleSeconds = -1f;
        }

        private void OnBallClamped(BilliardsBall ball) => _containmentTrips++;

        /// <summary>
        /// 哪个座位正被留着等重连。只有 host 答得出来 —— 座位表是它的。
        /// </summary>
        private int FindHeldSeat()
        {
            if (_game == null || !IsHost)
                return BilliardsRules.SeatNone;

            for (int seat = 0; seat < BilliardsRules.SeatCount; seat++)
            {
                if (_game.IsSeatHeld(seat))
                    return seat;
            }

            return BilliardsRules.SeatNone;
        }

        /// <summary>
        /// 跟本机 client 的起落。这一端自己断过又回来，是 client 侧唯一能自证重连的事实：
        /// 它看不到别人的座位，但它知道自己拿回的座位号是不是原来那个。
        /// </summary>
        /// <summary>
        /// 每帧记一次每个座位上活着的 connection id。理由见 <see cref="_lastLiveConnection"/>。
        /// </summary>
        private void TrackSeatConnections()
        {
            if (_game == null || !IsHost)
                return;

            for (int seat = 0; seat < _lastLiveConnection.Length; seat++)
            {
                int id = _game.SeatConnectionId(seat);
                if (id >= 0)
                    _lastLiveConnection[seat] = id;
            }
        }

        private void TrackLocalClientDrop()
        {
            bool up = ClientUp;

            if (_clientWasUp && !up)
            {
                _localClientDropped = true;
                _localSeatBeforeDrop = _game == null ? BilliardsRules.SeatNone : _game.LocalSeat;
                _maskBeforeDrop = _game == null ? _maskBeforeDrop : _game.State.Pocketed;
            }

            _clientWasUp = up;
        }

        private void OnStateChanged(BilliardsState state)
        {
            _phase = state.Phase;

            bool nowWaiting = state.HasFlag(BilliardsFlags.AwaitingReconnect);

            if (nowWaiting && !_waitingForReconnect)
            {
                // 断连那一刻。host 能问出**哪个**座位被留住了，那才是要跟的那个座位；
                // client 问不到（座位表是 host 侧的），它走 _localClientDropped 那条。
                _heldSeat = FindHeldSeat();

                // 读**记下来的**那个，不是现在的：现在的已经是 −1 了（见 _lastLiveConnection）。
                _connectionBeforeDrop =
                    _heldSeat >= 0 && _heldSeat < _lastLiveConnection.Length
                        ? _lastLiveConnection[_heldSeat]
                        : -1;
                _maskBeforeDrop = state.Pocketed;
            }
            else if (!nowWaiting && _waitingForReconnect && !state.HasFlag(BilliardsFlags.Abandoned))
            {
                _reconnectObserved = true;
                _connectionAfterReturn = _heldSeat == BilliardsRules.SeatNone
                    ? -1
                    : _game.SeatConnectionId(_heldSeat);
                _maskAfterReturn = state.Pocketed;
            }

            _waitingForReconnect = nowWaiting;

            if (state.Phase != BilliardsPhase.GameOver)
                return;

            _sawGameOver = true;
            _lastWinner = state.Winner;
            if (state.HasFlag(BilliardsFlags.Abandoned))
                _sawAbandoned = true;

            // 一局分出胜负就是这份报告要证的那次运行结束了。立刻落盘，不等退出 ——
            // 退出可能是被系统杀掉的。
            Write("game-over");
        }

        #endregion

        #region Claims

        /// <summary>
        /// server/client 起了没有，问得住 FishNet 还没建好自己的 manager 的那一刻。
        ///
        /// <c>NetworkManager.IsServerStarted</c> 直接读 <c>ServerManager.Started</c>，而那两个
        /// manager 是 NetworkManager 在自己的 Awake 里建的 —— 在那之前问它会**抛 NPE**。
        /// 这不是理论问题：<see cref="OnDestroy"/> 也写报告，而进程可以在 FishNet 起来之前就退出，
        /// 那时该得到一份 role=unknown 的报告，不是一个异常。
        /// </summary>
        private bool ServerUp => _manager != null && _manager.ServerManager != null &&
                                 _manager.ServerManager.Started;

        private bool ClientUp => _manager != null && _manager.ClientManager != null &&
                                 _manager.ClientManager.Started;

        private bool IsHost => ServerUp;

        private string Role()
        {
            if (_manager == null)
                return "unknown";
            if (ServerUp)
                return "host";
            if (ClientUp)
                return "client";

            return "pending";
        }

        private List<Claim> BuildClaims()
        {
            var claims = new List<Claim>();

            AddConnectionPathClaim(claims);
            AddRoundTripTimeClaim(claims);
            AddByteClaims(claims);
            AddShotClaims(claims);
            AddOutcomeClaim(claims);
            AddReconnectClaim(claims);
            AddFpsClaim(claims);

            return claims;
        }

        /// <summary>主验收线本身：这条连接走的是直连还是中继，且与当时的网络摆法对得上。</summary>
        private void AddConnectionPathClaim(List<Claim> claims)
        {
            var claim = new Claim
            {
                Id = "connectionPath",
                Statement = "每条连接都读出了它走的是直连还是中继",
                Criterion = "每条连接有 Direct 或 Relayed，且与当时的网络摆法对得上（后半条要人核）",
                Note = "host 上本机 client 那条（connection -1）是 loopback，必然 Direct —— " +
                       "它不是「直连成功」的证据"
            };

            if (_connectionPaths.Count == 0)
            {
                claim.Verdict = Verdict.NotObserved;
                claim.Measured = "没有连接被量到";
                claims.Add(claim);
                return;
            }

            var sb = new StringBuilder();
            foreach (var kv in _connectionPaths)
            {
                if (sb.Length > 0)
                    sb.Append("; ");

                string label = kv.Key < 0 ? "本机 client" : $"connection {kv.Key}";
                sb.Append($"{label}={kv.Value}");
                if (_remoteCandidates.TryGetValue(kv.Key, out string candidate) &&
                    !string.IsNullOrEmpty(candidate))
                    sb.Append($" [{candidate}]");
            }

            claim.Measured = sb.ToString();
            claim.Verdict = Verdict.Holds;
            claims.Add(claim);
        }

        private void AddRoundTripTimeClaim(List<Claim> claims)
        {
            double tickMs = _manager != null && _manager.TimeManager != null
                ? _manager.TimeManager.TickDelta * 1000d
                : 0d;

            // 「0.0 ms 的半档以内」是一句读不通的判据，而它出现的原因（没有 TimeManager）恰好是
            // 读报告的人需要知道的，所以说出来而不是印一个 0。
            string tickText = tickMs > 0d ? $"{tickMs:F1} ms" : "未知 —— 没有 TimeManager 可读";

            var claim = new Claim
            {
                Id = "roundTripTimeIsTickMultiple",
                Statement = "RTT 是 tick 时长的整数倍",
                Criterion = $"每个样本落在 tick 档（{tickText}）的半档以内",
                Note = "TimeManager.RoundTripTime 量化到 tick；不是整数倍说明读的不是这个量"
            };

            if (_rttSamples.Count == 0)
            {
                claim.Verdict = Verdict.NotObserved;
                claim.Measured = "没有 RTT 样本（这一端没起 client）";
                claims.Add(claim);
                return;
            }

            // 全是 0 ＝ ping 一次都没往返回来。那是未观测，不是「判据成立」。
            if (_rttMeasuredSamples == 0)
            {
                claim.Verdict = Verdict.NotObserved;
                claim.Measured = $"{_rttSamples.Count} 个样本全是 0 ms —— ping 一次都没往返回来，" +
                                 "RTT 未被测到（0 是任何数的整数倍，所以这不算判据成立）";
                claims.Add(claim);
                return;
            }

            long min = long.MaxValue, max = long.MinValue, sum = 0;
            foreach (long sample in _rttSamples)
            {
                if (sample <= 0)
                    continue;

                if (sample < min) min = sample;
                if (sample > max) max = sample;
                sum += sample;
            }

            double mean = sum / (double)_rttMeasuredSamples;
            claim.Measured = $"{_rttMeasuredSamples} 个非零样本：min={min}ms mean={mean:F1}ms max={max}ms，" +
                             $"其中 {_rttTickMultipleSamples} 个是 tick 的整数倍；tick 档 {tickMs:F1}ms" +
                             (_rttZeroSamples > 0
                                 ? $"（另有 {_rttZeroSamples} 个读到 0，是首次往返之前的采样，不计入判据）"
                                 : "");
            claim.Verdict = _rttTickMultipleSamples == _rttMeasuredSamples
                ? Verdict.Holds
                : Verdict.Violated;
            claims.Add(claim);
        }

        /// <summary>
        /// 每 tick 的字节峰值与背压。两条都**只有 host 看得见** —— 采样点是服务端的发送回调。
        /// </summary>
        private void AddByteClaims(List<Claim> claims)
        {
            var mtuClaim = new Claim
            {
                Id = "peakUnreliableUnderMtu",
                Statement = "没有任何一个 tick 的 Unreliable 载荷越过 GetMTU",
                Criterion = $"越过 {OutboundByteMeter.MtuBytes} B 的 tick 数 = 0；峰值与 248B@30 / 228B@60 同量级",
                Note = "越过会让 FishNet 的分片路径把消息改走 Reliable，于是过期球位被重传（#119）"
            };

            var backlogClaim = new Claim
            {
                Id = "peakBacklog",
                Statement = "出站背压被量到了",
                Criterion = "走真 TURN 时不再恒为 0 —— 恒 0 说明压根没压出背压，那个数就没有信息量",
                Note = "loopback 上恒 0 是预期的（无瓶颈），不是这条 claim 成立的证据"
            };

            if (_meter == null || !IsHost)
            {
                mtuClaim.Verdict = Verdict.NotObserved;
                mtuClaim.Measured = _meter == null
                    ? "场景里没有 OutboundByteMeter"
                    : "这一端不是 host —— 每 tick 字节由服务端发送回调采样";
                backlogClaim.Verdict = Verdict.NotObserved;
                backlogClaim.Measured = mtuClaim.Measured;

                claims.Add(mtuClaim);
                claims.Add(backlogClaim);
                return;
            }

            OutboundByteMeter.Summary s = _meter.Summarise();

            if (s.TicksCounted == 0)
            {
                mtuClaim.Verdict = Verdict.NotObserved;
                mtuClaim.Measured = "meter 一次发送都没看到";
                backlogClaim.Verdict = Verdict.NotObserved;
                backlogClaim.Measured = mtuClaim.Measured;

                claims.Add(mtuClaim);
                claims.Add(backlogClaim);
                return;
            }

            mtuClaim.Measured = $"{s.TicksCounted} 个 tick：peakUnreliable={s.PeakUnreliable}B " +
                                $"peakReliable={s.PeakReliable}B peakTotal={s.PeakTotal}B " +
                                $"(tick {s.PeakTick}, {s.PeakMessages} 条消息) " +
                                $"meanUnreliable={s.MeanUnreliable:F1}B " +
                                $"ticksOverMtu={s.TicksOverMtu}";
            mtuClaim.Verdict = s.TicksOverMtu == 0 ? Verdict.Holds : Verdict.Violated;
            claims.Add(mtuClaim);

            backlogClaim.Measured = $"peakBacklog={s.PeakBacklog}B at tick {s.PeakBacklogTick} " +
                                    $"(reliable {s.PeakBacklogReliable}B, " +
                                    $"unreliable {s.PeakBacklogUnreliable}B); " +
                                    $"背压非零的 tick {s.TicksWithAnyBacklog}/{s.TicksCounted}";
            // 恒 0 不是「不成立」—— 在 loopback 上它就该是 0。所以这条按「有没有量到背压」三分，
            // 而不是按一个阈值判对错。
            backlogClaim.Verdict = s.TicksWithAnyBacklog > 0 ? Verdict.Holds : Verdict.NotObserved;
            claims.Add(backlogClaim);
        }

        /// <summary>每一杆的 settle 与首碰。也只有 host 看得见 —— 物理在它那边跑。</summary>
        private void AddShotClaims(List<Claim> claims)
        {
            var settleClaim = new Claim
            {
                Id = "settleSeconds",
                Statement = "每一杆都在物理该有的时间内停下",
                Criterion = $"3～5 s；读到 {BilliardsRack.MaxShotSeconds:F1} s 就是上限在兜底，物理有问题",
                Note = "上限是 BilliardsRack.MaxShotSeconds；撞上它意味着有球永远停不下来"
            };

            var contactClaim = new Claim
            {
                Id = "firstContact",
                Statement = "每一杆的首碰都被记录到了",
                Criterion = "除真空杆外不能是 −1；−1 说明碰撞回调没投递",
                Note = "#138 踩过：球自己掉进袋而白球没参与"
            };

            if (_shots.Count == 0)
            {
                settleClaim.Verdict = Verdict.NotObserved;
                settleClaim.Measured = IsHost
                    ? "这次运行里没有一杆被判定过"
                    : "这一端不是 host —— settle 与首碰由 host 的物理步产生";
                contactClaim.Verdict = Verdict.NotObserved;
                contactClaim.Measured = settleClaim.Measured;

                claims.Add(settleClaim);
                claims.Add(contactClaim);
                return;
            }

            float minSettle = float.MaxValue, maxSettle = 0f;
            double sumSettle = 0;
            int atBackstop = 0;
            int whiffs = 0;
            int missingContact = 0;

            foreach (ShotRecord shot in _shots)
            {
                if (shot.SettleSeconds >= 0f)
                {
                    if (shot.SettleSeconds < minSettle) minSettle = shot.SettleSeconds;
                    if (shot.SettleSeconds > maxSettle) maxSettle = shot.SettleSeconds;
                    sumSettle += shot.SettleSeconds;
                }

                if (shot.SettleSeconds >= BilliardsRack.MaxShotSeconds - 0.05f)
                    atBackstop++;

                if (shot.FirstContact >= 0)
                    continue;

                // 真空杆是合法的（referee 会判它犯规），所以 −1 本身不足以说明回调坏了。
                // 分开数：真空杆的 −1 与「碰了但没记到」的 −1 是两件事，而后者只能靠落袋掩码
                // 变了却没有首碰来认。
                if (shot.PocketedBefore != shot.PocketedAfter)
                    missingContact++;
                else
                    whiffs++;
            }

            settleClaim.Measured = $"{_shots.Count} 杆：min={minSettle:F2}s " +
                                   $"mean={sumSettle / _shots.Count:F2}s max={maxSettle:F2}s，" +
                                   $"撞 {BilliardsRack.MaxShotSeconds:F0}s 上限的 {atBackstop} 杆";
            settleClaim.Verdict = atBackstop == 0 ? Verdict.Holds : Verdict.Violated;
            claims.Add(settleClaim);

            contactClaim.Measured = $"{_shots.Count} 杆：首碰为 −1 的 {whiffs + missingContact} 杆，" +
                                    $"其中落袋掩码没变的（真空杆）{whiffs} 杆、" +
                                    $"掩码变了却没首碰的 {missingContact} 杆";
            contactClaim.Verdict = missingContact == 0 ? Verdict.Holds : Verdict.Violated;
            claims.Add(contactClaim);
        }

        /// <summary>终局：有座位赢了，或这局作废。**明说，不靠推断。**</summary>
        private void AddOutcomeClaim(List<Claim> claims)
        {
            var claim = new Claim
            {
                Id = "outcome",
                Statement = "这一局的结局被明确记下：有人赢了，或者这局作废",
                Criterion = "GameOver 且 winner ∈ {0,1}；或 Abandoned 且 winner = 255（作废不是输）",
                Note = "两者要用不同的话告诉玩家（#134）；这里也分开记，不共用一个字段"
            };

            if (!_sawGameOver)
            {
                claim.Verdict = Verdict.NotObserved;
                claim.Measured = $"这次运行没走到 GameOver（停在 {_phase}）";
                claims.Add(claim);
                return;
            }

            if (_sawAbandoned)
            {
                claim.Measured = $"abandoned=true winner={_lastWinner}（255 = 没有赢家）";
                claim.Verdict = _lastWinner == BilliardsRules.SeatNone
                    ? Verdict.Holds
                    : Verdict.Violated;
                claims.Add(claim);
                return;
            }

            claim.Measured = $"abandoned=false winner={_lastWinner}";
            claim.Verdict = _lastWinner == BilliardsRules.SeatHost || _lastWinner == BilliardsRules.SeatClient
                ? Verdict.Holds
                : Verdict.Violated;
            claims.Add(claim);
        }

        /// <summary>
        /// 重连那次。**两端验的不是同一件事**，所以判据也不同：
        ///
        /// - host 跟的是**被留住的那个座位**：它是否被同一个座位号、不同的 connection id 接回，
        ///   且落袋掩码没变。
        /// - client 只能跟**自己**：它掉过线、回来之后拿到的座位号是不是原来那个。它问不到别人的
        ///   座位（座位表是 host 侧的），所以一个**全程没断过**的 client 对这条只能是未观测 ——
        ///   而前一版会让它报「我的座位没变，成立」。
        /// </summary>
        private void AddReconnectClaim(List<Claim> claims)
        {
            var claim = new Claim
            {
                Id = "reconnect",
                Statement = "掉线的那一端被接回原座位，且局面与断连前一致",
                Criterion = "座位号不变（分组由座位派生，所以分组同时不变）、落袋掩码不变、" +
                            "connection id **必须**变（#120 从不复用，所以不变就说明没真的重连过）",
                Note = "host 跟被留住的那个座位；client 只能跟自己那一个。全程没断过的一端是未观测"
            };

            if (IsHost)
            {
                AddHostReconnectClaim(claim, claims);
                return;
            }

            AddClientReconnectClaim(claim, claims);
        }

        private void AddHostReconnectClaim(Claim claim, List<Claim> claims)
        {
            if (!_reconnectObserved)
            {
                claim.Verdict = Verdict.NotObserved;
                claim.Measured = _waitingForReconnect
                    ? $"座位 {_heldSeat} 正被留着等重连，还没回来"
                    : "这次运行里没有座位被留过";
                claims.Add(claim);
                return;
            }

            if (_heldSeat == BilliardsRules.SeatNone)
            {
                // 标志立起来过而问不出哪个座位 —— 那说明两者不一致，说出来而不是判它成立。
                claim.Verdict = Verdict.Violated;
                claim.Measured = "等重连的标志立过，但当时没有任何座位处于留座状态 —— " +
                                 "标志与座位表不一致";
                claims.Add(claim);
                return;
            }

            bool cameBack = _connectionAfterReturn >= 0;
            bool maskSame = _maskBeforeDrop == _maskAfterReturn;

            // 断连前那个 id 没记到，就**不要**拿它去比 —— −1 与任何真 id 都不同，那样比出来的
            // 「变了」是算术的性质而不是观测。说出这半条没验到，别让它顺带通过。
            bool haveBefore = _connectionBeforeDrop >= 0;
            bool idChanged = haveBefore && _connectionAfterReturn != _connectionBeforeDrop;

            string beforeText = haveBefore ? _connectionBeforeDrop.ToString() : "未记到";
            claim.Measured = $"座位 {_heldSeat} 被留住并接回：connection " +
                             $"{beforeText}→{_connectionAfterReturn}，" +
                             $"落袋掩码 {_maskBeforeDrop:X4}→{_maskAfterReturn:X4}" +
                             (haveBefore ? "" : "（断连前的 connection id 没记到，" +
                                                "「id 必须变」这半条未验证）");

            claim.Verdict = !cameBack || !maskSame
                ? Verdict.Violated
                : haveBefore
                    ? (idChanged ? Verdict.Holds : Verdict.Violated)
                    // 座位回来了、掩码没变，但少一半判据 —— 那是未观测，不是成立。
                    : Verdict.NotObserved;
            claims.Add(claim);
        }

        private void AddClientReconnectClaim(Claim claim, List<Claim> claims)
        {
            if (!_localClientDropped)
            {
                claim.Verdict = Verdict.NotObserved;
                claim.Measured = "这一端自己没掉过线 —— 别人的座位它问不到，所以这条无从验证";
                claims.Add(claim);
                return;
            }

            int seatNow = _game == null ? BilliardsRules.SeatNone : _game.LocalSeat;

            if (seatNow == BilliardsRules.SeatNone)
            {
                claim.Verdict = Verdict.Violated;
                claim.Measured = $"掉线前是座位 {_localSeatBeforeDrop}，回来之后没有拿到任何座位";
                claims.Add(claim);
                return;
            }

            ushort maskNow = _game.State.Pocketed;
            bool seatSame = seatNow == _localSeatBeforeDrop;
            bool maskSame = maskNow == _maskBeforeDrop;

            claim.Measured = $"本机掉线后回来：座位 {_localSeatBeforeDrop}→{seatNow}，" +
                             $"落袋掩码 {_maskBeforeDrop:X4}→{maskNow:X4}" +
                             (_game.LocalGameVoided ? "（但这一局已被告知作废）" : "");
            claim.Verdict = seatSame && maskSame && !_game.LocalGameVoided
                ? Verdict.Holds
                : Verdict.Violated;
            claims.Add(claim);
        }

        /// <summary>
        /// 帧率。这条是 §8.2 转出来的：三渲二要的「球不模糊」与渲染无关，是**每帧位移** ——
        /// 8.5 px 的球，一帧挪过两个球宽就是人眼读到的拖影。判据取最低 fps ≥ 50，因为开球那一杆
        /// 峰值 3.96 m/s，50 fps 下每帧约 1.4 球宽。
        /// </summary>
        private void AddFpsClaim(List<Claim> claims)
        {
            // 这条判据**只在真机 Player 上成立**。Editor 里有 Game view、Inspector、Console、
            // 资源导入与 GC 一起跑，随便一个都会造出几十毫秒的长帧 —— #139 实测：Editor 里
            // 平均 354 fps 而最低 15.1 fps，于是这条判成「不成立」，而那不是被测对象的性质。
            // §8.2 那个 50 fps 是给 8.5 px 的球在手机上写的，所以在 Editor 里它是噪声。
            bool meaningful = !Application.isEditor;

            var claim = new Claim
            {
                Id = "frameRate",
                Statement = "球在动的时候帧率没掉到会看出拖影的程度",
                Criterion = meaningful
                    ? $"Simulate 阶段最低 fps ≥ {_minimumFpsCriterion:F0}"
                    : $"Simulate 阶段最低 fps ≥ {_minimumFpsCriterion:F0}（**在 Editor 里不判** —— " +
                      "编辑器自身的长帧不是被测对象的性质）",
                Note = "开球峰值 3.96 m/s；50 fps 下每帧约 1.4 球宽，30 fps 下 2.3 球宽（§8.2）。" +
                       $"起头 {_fpsWarmupSeconds:F0} s 的帧不计入。这条只在真机 Player 上有意义"
            };

            if (_shotFrames == 0 && _sessionFrames == 0)
            {
                claim.Verdict = Verdict.NotObserved;
                claim.Measured = "没有采到帧（还没过热机窗口）";
                claims.Add(claim);
                return;
            }

            string session = _sessionFrames > 0
                ? $"整场 {_sessionFrames} 帧：mean={_sessionFrames / _sessionFrameSeconds:F1} " +
                  $"min={_sessionMinFps:F1}"
                : "整场没有采到帧";

            if (_shotFrames == 0)
            {
                claim.Verdict = Verdict.NotObserved;
                claim.Measured = $"{session}；Simulate 阶段没有采到帧（这次运行里没有球在跑）";
                claims.Add(claim);
                return;
            }

            claim.Measured = $"{session}；球在跑的 {_shotFrames} 帧：" +
                             $"mean={_shotFrames / _shotFrameSeconds:F1} min={_shotMinFps:F1}" +
                             (meaningful ? "" : "（Editor，不判）");

            claim.Verdict = !meaningful
                ? Verdict.NotObserved
                : _shotMinFps >= _minimumFpsCriterion ? Verdict.Holds : Verdict.Violated;
            claims.Add(claim);
        }

        #endregion

        #region Writing

        /// <summary>
        /// 报告的落盘路径。角色在文件名里 —— 同一台机器上两个进程很可能共用
        /// <see cref="Application.persistentDataPath"/>。
        /// </summary>
        public string ReportPath =>
            Path.Combine(Application.persistentDataPath, $"{_fileNamePrefix}-{Role()}.json");

        /// <summary>
        /// 这次运行到底观测到了什么没有。全空意味着这个实例从头到尾没连上、没出杆、没采到帧 ——
        /// 它没有可报的东西。
        /// </summary>
        private bool ObservedAnything =>
            _connectionPaths.Count > 0 || _rttSamples.Count > 0 || _shots.Count > 0 ||
            _sessionFrames > 0 || _sawGameOver || _reconnectObserved;

        /// <summary>
        /// 写一份全量报告。多次调用是幂等的（同一路径全量覆盖），所以每个可能是「最后一刻」的
        /// 时机都调它。
        ///
        /// **一份什么都没观测到的报告不落盘**（除非是手动按的）。理由不是省一个文件：它会
        /// **盖掉一份真的**。踩到过 —— 一批验证用的实例（从没连过网、role 是 unknown）在退出
        /// 播放态时各写了一次，最后落在磁盘上的那份带着注入的合成数字，而它正躺在真实测量该在
        /// 的位置。手动那一下是例外：那时人就是想看「现在写会是什么样」。
        /// </summary>
        public void Write(string trigger)
        {
            bool manual = trigger == "manual";
            if (!manual && !ObservedAnything)
            {
                Debug.Log($"[BilliardsReport] 这次运行没有任何观测（触发：{trigger}），" +
                          "不落盘 —— 一份空报告会盖掉一份真的。");
                return;
            }

            string path = ReportPath;

            try
            {
                var sb = new StringBuilder(4096);
                List<Claim> claims = BuildClaims();

                int held = 0, violated = 0, notObserved = 0;
                foreach (Claim claim in claims)
                {
                    switch (claim.Verdict)
                    {
                        case Verdict.Holds: held++; break;
                        case Verdict.Violated: violated++; break;
                        default: notObserved++; break;
                    }
                }

                sb.AppendLine("{");
                WriteProvenance(sb, trigger);
                sb.AppendLine($"  \"claimsHeld\": {held},");
                sb.AppendLine($"  \"claimsViolated\": {violated},");
                sb.AppendLine($"  \"claimsNotObserved\": {notObserved},");

                // 一个字段就能被机器判：violated 有一条就 violated，否则有 not-observed 就
                // incomplete。刻意不叫 result/passed —— 见类注释。
                string runVerdict = violated > 0
                    ? "violated"
                    : notObserved > 0 ? "incomplete" : "holds";
                sb.AppendLine($"  \"runVerdict\": \"{runVerdict}\",");

                WriteObservations(sb);

                sb.AppendLine("  \"claims\": [");
                for (int i = 0; i < claims.Count; i++)
                {
                    Claim claim = claims[i];
                    // 一条 claim 一行：这份文件要能被 grep 与 diff，而缩进过的 JSON 会把一条
                    // claim 摊成七行，两份报告的差别就看不出来了。
                    sb.Append("    {");
                    sb.Append($"\"id\": {Json(claim.Id)}, ");
                    sb.Append($"\"verdict\": \"{VerdictText(claim.Verdict)}\", ");
                    sb.Append($"\"statement\": {Json(claim.Statement)}, ");
                    sb.Append($"\"criterion\": {Json(claim.Criterion)}, ");
                    sb.Append($"\"measured\": {Json(claim.Measured)}, ");
                    sb.Append($"\"note\": {Json(claim.Note)}");
                    sb.AppendLine(i == claims.Count - 1 ? "}" : "},");
                }

                sb.AppendLine("  ]");
                sb.AppendLine("}");

                // **不带 BOM。** `Encoding.UTF8` 那个静态实例会写一个 BOM，而它会让标准 JSON
                // 解析器当场拒收（Python 的 `json.load` 报 "Unexpected UTF-8 BOM"）——
                // 一份机器判不了的报告不是机器可判的报告。实测踩到过，不是预防性的。
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

                // 回读一次再报成功。写进只读位置在真机上是**静默失败**，而一份没落盘的报告与
                // 「压根没跑」不可区分 —— 这正是不写 Logs/ 的那条理由，所以这里也不能只相信
                // WriteAllText 没抛。
                if (!File.Exists(path))
                    throw new IOException("WriteAllText 没抛错，但文件不在那里。");

                _writtenPath = path;
                _lastWriteError = null;
                Debug.Log($"[BilliardsReport] 报告已写入 {path}（触发：{trigger}，" +
                          $"{runVerdict}: {held} 成立 / {violated} 不成立 / {notObserved} 未观测）");
            }
            catch (Exception e)
            {
                _lastWriteError = e.Message;
                Debug.LogError($"[BilliardsReport] 写 {path} 失败：{e}");
            }
        }

        /// <summary>
        /// 出处。这一段是判据的一半：报告会离开产生它的机器，那时它只剩自己交代自己是谁跑的。
        /// </summary>
        private void WriteProvenance(StringBuilder sb, string trigger)
        {
            // 拼在洞外面：C# 9 不允许插值洞里换行，而这条说明必须够长。
            string producer = "BilliardsDeviceReport（逐条 claim 带实测值；" +
                              "不是 Unity Test Framework，也不是它的 XML 形状）";
            sb.AppendLine($"  \"producer\": {Json(producer)},");
            sb.AppendLine($"  \"ticket\": \"#139\",");
            sb.AppendLine($"  \"writtenAtUtc\": {Json(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))},");
            sb.AppendLine($"  \"writeTrigger\": {Json(trigger)},");
            sb.AppendLine($"  \"role\": {Json(Role())},");
            sb.AppendLine($"  \"localSeat\": {(_game == null ? BilliardsRules.SeatNone : _game.LocalSeat)},");
            sb.AppendLine($"  \"roomCode\": {Json(_transport == null ? null : _transport.RoomCode)},");
            sb.AppendLine($"  \"platform\": {Json(Application.platform.ToString())},");
            sb.AppendLine($"  \"deviceModel\": {Json(SystemInfo.deviceModel)},");
            sb.AppendLine($"  \"operatingSystem\": {Json(SystemInfo.operatingSystem)},");
            sb.AppendLine($"  \"unityVersion\": {Json(Application.unityVersion)},");
            sb.AppendLine($"  \"appVersion\": {Json(Application.version)},");
            sb.AppendLine($"  \"isEditor\": {(Application.isEditor ? "true" : "false")},");
            sb.AppendLine($"  \"persistentDataPath\": {Json(Application.persistentDataPath)},");

            // 原生二进制的身份，与 transport 那条同一个论证、往下一层：托管层永远是本仓库
            // 这一份，而**加载起来的是哪个 .so/.dll/.a 由 Plugins/ 决定**。拿旧二进制配新
            // 托管层时，动态库平台不会在启动时报错 —— 要等真调到缺失的那个导出才抛
            // EntryPointNotFoundException。所以「跑的是哪个 ABI」必须是报告里的一个字段，
            // 而不是靠 logcat 捞那行 init 日志：Release 播放器的默认级别是 Warning，
            // 那行是 Info，压根不会发（DataChannelLog.cs 的 #if / #12 的决议）。
            //
            // 0 = 原生从来没起来（dcu_init 失败或插件缺失），与 2/3 可区分。
            sb.AppendLine($"  \"abiVersion\": {DataChannelRuntime.AbiVersion},");

            // 传输层的身份要写进去：FishNet 在组件缺失时会**静默**换成 Tugboat，那时每一个字节
            // 数字都还看着合理，但量的不是这个包。
            sb.AppendLine($"  \"transport\": {Json(_manager == null || _manager.TransportManager == null || _manager.TransportManager.Transport == null ? null : _manager.TransportManager.Transport.GetType().Name)},");
            sb.AppendLine($"  \"tickRate\": {(_manager == null || _manager.TimeManager == null ? 0 : _manager.TimeManager.TickRate)},");

            // 渲染那几档决定 §8.2 的前提（每帧位移与球的屏幕尺寸），所以与 fps 一起记。
            sb.AppendLine($"  \"qualityLevel\": {Json(QualitySettings.names[QualitySettings.GetQualityLevel()])},");
            sb.AppendLine($"  \"vSyncCount\": {QualitySettings.vSyncCount},");
            sb.AppendLine($"  \"targetFrameRate\": {Application.targetFrameRate},");
            sb.AppendLine($"  \"screen\": {Json($"{Screen.width}x{Screen.height} @{Screen.dpi:F0}dpi {Screen.orientation}")},");
        }

        /// <summary>
        /// 不构成 claim 但改变读数含义的量。放在 claims 外面，因为它们**没有判据** ——
        /// 混进去会让「未观测」的计数里出现几条本来就不打算判的东西。
        /// </summary>
        private void WriteObservations(StringBuilder sb)
        {
            sb.AppendLine("  \"observations\": {");
            sb.AppendLine($"    \"shotsJudged\": {_shots.Count},");
            sb.AppendLine($"    \"containmentTrips\": {_containmentTrips},");
            sb.AppendLine($"    \"phaseAtWrite\": {Json(_phase.ToString())},");
            sb.AppendLine($"    \"pocketedMaskAtWrite\": {Json(_game == null ? null : $"{_game.State.Pocketed:X4}")},");
            sb.AppendLine($"    \"shots\": [");

            for (int i = 0; i < _shots.Count; i++)
            {
                ShotRecord shot = _shots[i];
                sb.Append("      {");
                sb.Append($"\"n\": {i + 1}, ");
                sb.Append($"\"shooter\": {shot.Shooter}, ");
                sb.Append($"\"wasBreak\": {(shot.WasBreak ? "true" : "false")}, ");
                sb.Append(shot.SettleSeconds >= 0f
                    ? $"\"settleSeconds\": {shot.SettleSeconds.ToString("F2", CultureInfo.InvariantCulture)}, "
                    : "\"settleSeconds\": null, ");
                sb.Append($"\"firstContact\": {shot.FirstContact}, ");
                sb.Append($"\"pocketedBefore\": {Json($"{shot.PocketedBefore:X4}")}, ");
                sb.Append($"\"pocketedAfter\": {Json($"{shot.PocketedAfter:X4}")}, ");
                sb.Append($"\"foul\": {(shot.Foul ? "true" : "false")}, ");
                sb.Append($"\"foulReason\": {Json(shot.FoulReason)}");
                sb.AppendLine(i == _shots.Count - 1 ? "}" : "},");
            }

            sb.AppendLine("    ]");
            sb.AppendLine("  },");
        }

        /// <summary>
        /// JSON 字符串字面量，含 null。手写而不是用 <c>JsonUtility</c>：那个序列化不了
        /// <c>Dictionary</c>、也给不出「一条 claim 一行」这个布局，而这份报告要能被 grep。
        /// </summary>
        private static string Json(string value)
        {
            if (value == null)
                return "null";

            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');

            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        #endregion

        #region Readout

        /// <summary>
        /// 右下角一块小读数：路径与「现在写会是什么结果」。
        ///
        /// 路径必须在屏幕上，不能只在日志里：iOS 上 <c>Debug.Log</c> 进设备 console 就没了，而
        /// §8b-iOS 第 4 步要拿这个文件名去 <c>devicectl copy from</c>。
        /// </summary>
        private GUIStyle _readoutStyle;

        private void OnGUI()
        {
            // 缓存而不是每帧新建：OnGUI 一帧会被调用多次（Layout 与 Repaint 各一次），
            // 与 RoomPanel / ConnectionDiagnosticsHud 同一个写法。
            _readoutStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 11 };
            _readoutStyle.normal.textColor =
                _lastWriteError == null ? Color.white : new Color(1f, 0.5f, 0.5f);
            GUIStyle style = _readoutStyle;

            const float w = 420f, h = 74f;
            var rect = new Rect(Screen.width - w - 10f, Screen.height - h - 10f, w, h);

            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = prev;

            GUILayout.BeginArea(new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, rect.height - 12f));

            GUILayout.Label(_lastWriteError != null
                ? $"报告写入失败：{_lastWriteError}"
                : _writtenPath == null
                    ? $"报告将写入 {ReportPath}"
                    : $"报告已写入 {_writtenPath}", style);

            if (GUILayout.Button("现在写一份报告"))
                Write("manual");

            GUILayout.EndArea();
        }

        #endregion
    }
}
