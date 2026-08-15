// #128：把 DataChannelTransport 从进程内信令接到 wss 信令。
//
// 协议不是照 #116 的票面写的，是**照 deploy/signal/src/server.py 写的** —— 那是实际
// 部署在 wss://signal.xsmxu.cn 上跑着的东西，且有 16 条冒烟（deploy/signal/smoke.py）
// 钉住它的行为。票与实现万一有分歧，实现赢。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// 信令客户端：一条 wss 连接，收发 <c>{type,from,to,payload}</c> 信封。
    ///
    /// ## 只做搬运，不懂 WebRTC
    ///
    /// 它不解析 SDP、不碰 PeerConnection —— 收到什么就以事件抛给 Transport。这与服务端
    /// 的「哑中继、从不解析 payload」是同一个取舍，也是 #126 定的 Nakama 可换性所依赖
    /// 的：换载体时只有这个文件要改。
    ///
    /// ## per-sender FIFO 是硬约束，不是优化
    ///
    /// candidate 早于 description 到达对端会在上游 <c>processRemoteCandidate</c> 当场抛
    /// （<c>Got a remote candidate without remote description</c>）。服务端用「一个连接
    /// 一个协程」保住顺序；**我们这侧靠一条接收循环 + 一个队列 + 主线程单点排空**保住
    /// 它。别把 <see cref="Drain"/> 改成并发处理，也别为「快一点」给某类消息开快车道。
    ///
    /// ## 线程
    ///
    /// 收发在后台 <see cref="Task"/> 上，但**所有事件都在主线程 raise**（由
    /// <see cref="Drain"/> 驱动）。这是被包的契约逼的：<c>SetRemoteDescription</c> /
    /// <c>AddRemoteCandidate</c> 都带 <c>MainThread.Assert</c>。
    /// </summary>
    public sealed class SignalingClient : IDisposable
    {
        // ── 信封 ───────────────────────────────────────────────────────────
        //
        // JsonUtility 按字段名匹配，缺的留默认值、多的忽略，所以**一个扁平的 Payload
        // 装下所有消息的所有字段**是可行的，不必按 type 分出十个类。
        //
        // 一处必须知道的重叠：`code` 在 room-created 里是**房间码**，在 error 里是
        // **错误码**（no-such-room / room-closed / malformed）。同名不同义 —— 派发时先
        // 看 type，所以用起来不含糊，但读代码时别想当然。
        [Serializable]
        private sealed class Envelope
        {
            public string type;
            public string from;
            public string to;
            public Payload payload;
        }

        [Serializable]
        private sealed class Payload
        {
            public string code;         // room-created: 房间码 / error: 错误码
            public string peerId;       // room-created, joined, peer-left
            public string hostPeerId;   // joined
            public string sdp;          // description
            public string sdpType;      // description: "offer" | "answer"
            public string candidate;    // candidate
            public string mid;          // candidate
            public string reason;       // room-closed
            public string message;      // error

            /// <summary>
            /// description: 重连时客户端出示的座位令牌（#134）。
            ///
            /// **搭在 description 的 payload 里，所以服务端零改动** —— `description` 走
            /// `relay`，服务端只看信封的 `to` 转发、**从不解析 payload**（#134 事实 5）。
            /// 换成新增一种消息就要动服务端。
            ///
            /// 对本类和 Transport 都是**不透明字符串**：谁签、里面是什么、怎么比，全归游戏层
            /// （见 <see cref="ISeatAuthority"/>）。
            /// </summary>
            public string seatToken;
            public IceServerDto[] iceServers; // room-created, joined
        }

        [Serializable]
        private sealed class IceServerDto
        {
            public string[] urls;
            public string username;
            public string credential;
        }

        // ── 状态 ───────────────────────────────────────────────────────────

        /// <summary>服务器分配给自己的 peer id。**从不自报** —— 服务器给 from 盖章。</summary>
        public string PeerId { get; private set; }

        /// <summary>6 位房间码。host 侧由 room-created 带回；client 侧是自己输入的。</summary>
        public string RoomCode { get; private set; }

        /// <summary>host 的 peer id。client 侧由 joined 带回；host 侧等于 <see cref="PeerId"/>。</summary>
        public string HostPeerId { get; private set; }

        /// <summary>
        /// 服务器下发的 ICE 服务器（#117：TURN 凭据是时限 HMAC，每次建/进房重签）。
        /// 在 <see cref="RoomCreated"/> / <see cref="Joined"/> raise 之后才有值。
        /// </summary>
        public List<IceServer> IceServers { get; } = new List<IceServer>();

        public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open;

        // ── 事件（全部主线程）─────────────────────────────────────────────

        /// <summary>host：房间已建好，<see cref="RoomCode"/> 与 <see cref="IceServers"/> 可读。</summary>
        public event Action RoomCreated;

        /// <summary>client：已进房，<see cref="HostPeerId"/> 与 <see cref="IceServers"/> 可读。</summary>
        public event Action Joined;

        /// <summary>收到对端的 description。参数：from、sdp、sdpType。</summary>
        /// <summary>
        /// 收到 description：(from, sdp, sdpType, seatToken)。第四个参数是 #134 的座位令牌，
        /// 无则为 null；本类只转述，不解析。
        /// </summary>
        public event Action<string, string, string, string> DescriptionReceived;

        /// <summary>收到对端的 candidate。参数：from、candidate、mid。</summary>
        public event Action<string, string, string> CandidateReceived;

        /// <summary>host：某个 client 的信令连接断了。参数：peerId。</summary>
        public event Action<string> PeerLeft;

        /// <summary>client：房间没了（host 走了）。参数：reason。</summary>
        public event Action<string> RoomClosed;

        /// <summary>连接失败或服务器回了 error。参数：code、message。</summary>
        public event Action<string, string> Failed;

        // ── 管道 ───────────────────────────────────────────────────────────

        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;

        // 接收：后台循环 → 这个队列 → 主线程 Drain。**一条循环、一个队列**，顺序即
        // 到达顺序，per-sender FIFO 由此保住。
        private readonly ConcurrentQueue<string> _inbound = new ConcurrentQueue<string>();

        // 发送：ClientWebSocket.SendAsync **不允许并发调用**（会抛
        // InvalidOperationException）。用一条串行链把所有发送排成队。
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        // 后台任务里出的错，搬到主线程报。直接 Debug.Log 在别的线程上是可以，但事件
        // 必须主线程 raise，所以统一走这里。
        private readonly ConcurrentQueue<(string code, string message)> _failures
            = new ConcurrentQueue<(string, string)>();

        private bool _disposed;

        /// <summary>
        /// 连上并建房（host 侧）。
        /// </summary>
        public void ConnectAndCreateRoom(string url) => Connect(url, null);

        /// <summary>
        /// 连上并进房（client 侧）。<paramref name="code"/> 大小写不敏感（服务端
        /// <c>_norm</c> 会 upper，见 server.py:105-108）。
        /// </summary>
        public void ConnectAndJoinRoom(string url, string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("房间码不能为空", nameof(code));
            RoomCode = code.Trim();
            Connect(url, RoomCode);
        }

        private void Connect(string url, string joinCode)
        {
            if (_ws != null) throw new InvalidOperationException("这个 SignalingClient 已经用过了，请新建一个。");

            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            // 不 await：Unity 主线程不能阻塞。失败经 _failures 搬回主线程。
            _ = RunAsync(url, joinCode, _cts.Token);
        }

        private async Task RunAsync(string url, string joinCode, CancellationToken ct)
        {
            try
            {
                await _ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);

                // 连上就立刻发控制面第一条。服务端只认这两种上行控制消息
                // （server.py:250 的 CONTROL_UP）。
                if (joinCode == null)
                    await SendRawAsync("{\"type\":\"create-room\",\"payload\":{}}", ct).ConfigureAwait(false);
                else
                    await SendRawAsync(
                        "{\"type\":\"join-room\",\"payload\":{\"code\":" + JsonString(joinCode) + "}}",
                        ct).ConfigureAwait(false);

                await ReceiveLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常关闭路径（Dispose 取消了 token），不报错。
            }
            catch (Exception e)
            {
                _failures.Enqueue(("signaling-failed", $"{e.GetType().Name}: {e.Message}"));
            }
        }

        /// <summary>
        /// 接收循环。**一条**，不并发 —— 见类注释里 per-sender FIFO 那段。
        /// </summary>
        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            // 4 KB 起步。SDP 会超它（一条完整 offer 常有几 KB），所以下面按分片累积，
            // 不是「一次 ReceiveAsync 就是一条消息」—— 那个假设在 SDP 上会碎。
            var buf = new byte[4096];
            var sb = new StringBuilder();

            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var seg = new ArraySegment<byte>(buf);
                WebSocketReceiveResult r;
                try
                {
                    r = await _ws.ReceiveAsync(seg, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                if (r.MessageType == WebSocketMessageType.Close)
                {
                    _failures.Enqueue(("signaling-closed", $"服务器关闭了连接：{r.CloseStatus} {r.CloseStatusDescription}"));
                    break;
                }

                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (!r.EndOfMessage) continue; // 分片未完，接着收

                _inbound.Enqueue(sb.ToString());
                sb.Clear();
            }
        }

        // ── 发送 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 发 description。<paramref name="to"/> 是对端 peer id；<c>from</c> **不填** ——
        /// 服务器盖章，自报会被忽略（smoke.py 专门验了这条：故意自报假 from 仍被改回）。
        /// </summary>
        /// <param name="seatToken">
        /// 座位令牌，没有则传 null（#134）。只有 client 侧的 offer 会带它 —— host 的 answer
        /// 不需要证明自己是谁，它就是房间的权威方。
        /// </param>
        public void SendDescription(string to, string sdp, string sdpType, string seatToken = null)
        {
            var payload = "{\"sdp\":" + JsonString(sdp) + ",\"sdpType\":" + JsonString(sdpType);
            // 没有令牌时**不写这个键**，而不是写 null：服务端从不解析 payload，所以这纯粹是
            // 为了让抓下来的信令日志里「这条是不是重连」一眼可读。
            if (!string.IsNullOrEmpty(seatToken))
                payload += ",\"seatToken\":" + JsonString(seatToken);
            SendEnvelope("description", to, payload + "}");
        }

        /// <summary>发 candidate。<paramref name="mid"/> 可为 null（包的 mid 是可选的）。</summary>
        public void SendCandidate(string to, string candidate, string mid)
        {
            SendEnvelope("candidate", to,
                "{\"candidate\":" + JsonString(candidate) + ",\"mid\":" + JsonString(mid ?? "0") + "}");
        }

        /// <summary>
        /// 发 reject —— 满员时 host 回它。#120 定的：上限归房间层拦，而这是拦的动作本身。
        /// </summary>
        public void SendReject(string to, string reason)
        {
            SendEnvelope("reject", to, "{\"reason\":" + JsonString(reason) + "}");
        }

        private void SendEnvelope(string type, string to, string payloadJson)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return; // 静默丢：信令断了不该炸游戏
            var json = "{\"type\":" + JsonString(type)
                       + ",\"to\":" + JsonString(to)
                       + ",\"payload\":" + payloadJson + "}";
            _ = SendRawAsync(json, _cts.Token);
        }

        private async Task SendRawAsync(string json, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _ws.SendAsync(new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                _failures.Enqueue(("signaling-send-failed", $"{e.GetType().Name}: {e.Message}"));
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// 最小 JSON 字符串转义。**自己写而不是用 JsonUtility**：JsonUtility 只能序列化
        /// 整个对象，而我们要拼的是「信封套一个形状不定的 payload」—— 用它反而要为每种
        /// 消息建一个类。SDP 里全是 <c>\r\n</c>，转义是这里唯一真正要做对的事。
        /// </summary>
        private static string JsonString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        // ── 排空（主线程）─────────────────────────────────────────────────

        /// <summary>
        /// 主线程每帧调一次。**顺序处理，一条不落、不重排** —— per-sender FIFO 就在
        /// 这个循环里兑现。
        /// </summary>
        public void Drain()
        {
            while (_failures.TryDequeue(out var f))
                Failed?.Invoke(f.code, f.message);

            while (_inbound.TryDequeue(out var raw))
            {
                Envelope env;
                try
                {
                    env = JsonUtility.FromJson<Envelope>(raw);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SignalingClient] 收到无法解析的消息，已丢弃：{e.Message}");
                    continue;
                }
                if (env == null || string.IsNullOrEmpty(env.type)) continue;

                var p = env.payload ?? new Payload();
                switch (env.type)
                {
                    case "room-created":
                        PeerId = p.peerId;
                        RoomCode = p.code;      // 这里 code 是房间码
                        HostPeerId = p.peerId;  // host 自己就是 host
                        LoadIceServers(p.iceServers);
                        RoomCreated?.Invoke();
                        break;

                    case "joined":
                        PeerId = p.peerId;
                        HostPeerId = p.hostPeerId;
                        LoadIceServers(p.iceServers);
                        Joined?.Invoke();
                        break;

                    case "description":
                        DescriptionReceived?.Invoke(env.from, p.sdp, p.sdpType, p.seatToken);
                        break;

                    case "candidate":
                        CandidateReceived?.Invoke(env.from, p.candidate, p.mid);
                        break;

                    case "peer-left":
                        PeerLeft?.Invoke(p.peerId);
                        break;

                    case "room-closed":
                        RoomClosed?.Invoke(p.reason);
                        break;

                    case "reject":
                        // 对端拒了我们（满员）。当成一次失败上报 —— 它和连不上在语义上
                        // 是一回事：这条连接不会成立。
                        Failed?.Invoke("rejected", p.reason ?? "对端拒绝了连接");
                        break;

                    case "error":
                        // 这里 code 是错误码：no-such-room / room-closed / malformed
                        Failed?.Invoke(p.code ?? "error", p.message);
                        break;

                    default:
                        // 未知 type 不当错误 —— 服务端刻意对未知种类友好（server.py:280-283），
                        // 信封留了扩展位（host-changed 之类）。我们保持一致。
                        break;
                }
            }
        }

        private void LoadIceServers(IceServerDto[] dtos)
        {
            IceServers.Clear();
            if (dtos == null) return;
            foreach (var d in dtos)
            {
                if (d?.urls == null || d.urls.Length == 0) continue;
                IceServers.Add(new IceServer(d.urls, d.username, d.credential));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _cts?.Cancel(); } catch { /* 已释放 */ }

            // 不等 CloseAsync：Dispose 常在 OnDestroy / 域重载里被调，那时候不能阻塞，
            // 而且服务端不依赖优雅关闭 —— 它在 finally 里做 disconnect 清理
            // （server.py:286-288），TCP 断掉就够。
            try { _ws?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }
            try { _sendLock?.Dispose(); } catch { }
            _ws = null;
            _cts = null;
        }
    }
}
