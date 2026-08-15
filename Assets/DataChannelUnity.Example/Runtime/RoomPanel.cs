using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// 建房 / 进房的最小面板（#128）。
    ///
    /// ## 为什么必须有它
    ///
    /// 房间码是 host 连上信令之后由**服务器**分配的，所以进房那一端没法把它烘进 build ——
    /// 必须运行时输入。两个进程的验收因此绕不开一个输入框。
    ///
    /// ## 为什么是 IMGUI
    ///
    /// 和 `ConnectionDiagnosticsHud` 同一个理由：它是个临时的读数/输入窗口，不值得为它
    /// 建 Canvas 层级。台球真正的房间 UI 归 #132 之后的游戏层，不是这块。
    ///
    /// ## 这是 UI，不是房间层
    ///
    /// #126 定的「房间层藏在一个小接口后面让 Nakama 可插拔」指的是**建房/进房/匹配的
    /// 实现**，不是这块面板。面板只调 Transport 上的两个入口；换成 Nakama 时它照样能用。
    /// </summary>
    public sealed class RoomPanel : MonoBehaviour
    {
        [SerializeField] private NetworkManager _networkManager;
        [SerializeField] private DataChannelTransport _transport;

        private string _codeInput = "";
        private GUIStyle _label;

        /// <summary>
        /// 两端各自最后一次上报的连接状态。
        ///
        /// 存在的理由是 <c>ServerManager.Started</c> / <c>ClientManager.Started</c> 是**布尔**，
        /// 而这个 transport 的状态有四档（`Starting` / `Started` / `Stopping` / `Stopped`）——
        /// 布尔把前两档压成同一个值，于是「正在连」与「连上了」在面板上长得一样。#139 实测的症状：
        /// 一端还没连上，面板已经显示房间码与「断开」按钮。
        /// </summary>
        private LocalConnectionState _serverState = LocalConnectionState.Stopped;
        private LocalConnectionState _clientState = LocalConnectionState.Stopped;

        private void Awake()
        {
            if (_networkManager == null)
                _networkManager = GetComponentInParent<NetworkManager>() ?? FindObjectOfType<NetworkManager>();
            if (_transport == null && _networkManager != null)
                _transport = _networkManager.GetComponent<DataChannelTransport>();
        }

        private void OnEnable()
        {
            if (_networkManager == null)
                return;

            _networkManager.ServerManager.OnServerConnectionState += OnServerState;
            _networkManager.ClientManager.OnClientConnectionState += OnClientState;
        }

        private void OnDisable()
        {
            if (_networkManager == null)
                return;

            _networkManager.ServerManager.OnServerConnectionState -= OnServerState;
            _networkManager.ClientManager.OnClientConnectionState -= OnClientState;
        }

        private void OnServerState(ServerConnectionStateArgs args) => _serverState = args.ConnectionState;
        private void OnClientState(ClientConnectionStateArgs args) => _clientState = args.ConnectionState;

        private static string Text(LocalConnectionState state) => state switch
        {
            LocalConnectionState.Stopped => "停",
            LocalConnectionState.Starting => "连接中",
            LocalConnectionState.Started => "已连",
            LocalConnectionState.Stopping => "断开中",
            _ => state.ToString()
        };

        private void OnGUI()
        {
            if (_networkManager == null || _transport == null) return;

            _label ??= new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = Color.white } };

            const int w = 300, h = 170;
            GUILayout.BeginArea(new Rect(10, 10, w, h), GUI.skin.box);

            // 「彻底停着」才显示建房/进房。Starting 也算已经在动，否则连按钮会在连接中途仍然可按。
            bool serverIdle = _serverState == LocalConnectionState.Stopped;
            bool clientIdle = _clientState == LocalConnectionState.Stopped;

            if (serverIdle && clientIdle)
            {
                GUILayout.Label("① 一端建房（当 host），另一端输码加入", _label);

                if (GUILayout.Button("建房并当 host"))
                {
                    // host = server + client 同时起（契约 4.4）。先 server，因为
                    // StartClient 要靠 _serverStartRequested 判断走 loopback 还是 wss。
                    _networkManager.ServerManager.StartConnection();
                    _networkManager.ClientManager.StartConnection();
                }

                GUILayout.Space(6);
                GUILayout.Label("② 或者输房间码加入：", _label);
                _codeInput = GUILayout.TextField(_codeInput ?? "", 10);
                if (GUILayout.Button("加入房间"))
                {
                    if (string.IsNullOrWhiteSpace(_codeInput))
                    {
                        Debug.LogError("[RoomPanel] 先填房间码。");
                    }
                    else
                    {
                        _transport.SetJoinRoomCode(_codeInput.Trim());
                        // **只起 client，不起 server** —— 这正是纯 client 那条路
                        // （StartRemoteClient），走 wss 进房。
                        _networkManager.ClientManager.StartConnection();
                    }
                }
            }
            else
            {
                // 「连上了」是两端至少一端 Started。只在 Starting 的时候说「正在连」而不是显示
                // 一个房间码加一个「断开」—— 后者读起来像已经连上了，而那时什么都还没成。
                bool anyStarted = _serverState == LocalConnectionState.Started ||
                                  _clientState == LocalConnectionState.Started;

                bool anyStopping = _serverState == LocalConnectionState.Stopping ||
                                   _clientState == LocalConnectionState.Stopping;

                var code = _transport.RoomCode;
                if (anyStopping)
                    // 断开中优先说：这时说「正在连接」是反的。
                    GUILayout.Label("正在断开…", _label);
                else if (!anyStarted)
                    GUILayout.Label("正在连接…（还没连上）", _label);
                else
                    GUILayout.Label(string.IsNullOrEmpty(code)
                        ? "房间码：等信令返回…"
                        : $"房间码：{code}   ← 把这个给对手", _label);

                // 两端各自的四档状态都写出来，而不是两个 true/false。
                GUILayout.Label($"信令={(_transport.SignalingConnected ? "已连" : "未连")}"
                                + $"   server={Text(_serverState)}   client={Text(_clientState)}", _label);

                // host 起了 server 但自己那条 client 没连上，是一个**会让游戏永远停在 Lobby**
                // 的状态，而它在别处完全看不出来：座位 0 一直空，`TryBeginGame` 永不触发。
                // #139 实测踩过（_forceRelay 把 loopback 也强制成 RelayOnly），所以在这里说出来。
                if (_serverState == LocalConnectionState.Started &&
                    _clientState != LocalConnectionState.Started)
                {
                    GUILayout.Label("⚠ server 起来了，但本机 client 没连上 —— 座位 0 会一直空着，"
                                    + "游戏停在 Lobby。看 Console 里的 ICE 状态。", _label);
                }

                // 正在停的时候按钮变灰：那一下没有可断的东西，而一个可按的按钮会让人以为有。
                bool anythingUp = _serverState != LocalConnectionState.Stopped ||
                                  _clientState != LocalConnectionState.Stopped;

                // 按钮说它实际做的那件事：还没连上时它取消一次尝试，连上了才是断开。
                string label = anyStopping ? "正在断开…" : anyStarted ? "断开" : "取消连接";

                GUI.enabled = anythingUp && !anyStopping;
                if (GUILayout.Button(label))
                {
                    if (_clientState != LocalConnectionState.Stopped)
                        _networkManager.ClientManager.StopConnection();
                    if (_serverState != LocalConnectionState.Stopped)
                        _networkManager.ServerManager.StopConnection(true);
                }
                GUI.enabled = true;
            }

            GUILayout.EndArea();
        }
    }
}
