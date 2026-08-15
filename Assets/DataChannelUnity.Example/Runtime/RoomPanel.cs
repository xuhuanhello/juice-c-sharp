using FishNet.Managing;
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

        private void Awake()
        {
            if (_networkManager == null)
                _networkManager = GetComponentInParent<NetworkManager>() ?? FindObjectOfType<NetworkManager>();
            if (_transport == null && _networkManager != null)
                _transport = _networkManager.GetComponent<DataChannelTransport>();
        }

        private void OnGUI()
        {
            if (_networkManager == null || _transport == null) return;

            _label ??= new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = Color.white } };

            const int w = 300, h = 150;
            GUILayout.BeginArea(new Rect(10, 10, w, h), GUI.skin.box);

            var serverOn = _networkManager.ServerManager.Started;
            var clientOn = _networkManager.ClientManager.Started;

            if (!serverOn && !clientOn)
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
                var code = _transport.RoomCode;
                GUILayout.Label(string.IsNullOrEmpty(code)
                    ? "房间码：等信令返回…"
                    : $"房间码：{code}   ← 把这个给对手", _label);
                GUILayout.Label($"信令={( _transport.SignalingConnected ? "已连" : "未连")}"
                                + $"  server={serverOn}  client={clientOn}", _label);

                if (GUILayout.Button("断开"))
                {
                    if (clientOn) _networkManager.ClientManager.StopConnection();
                    if (serverOn) _networkManager.ServerManager.StopConnection(true);
                }
            }

            GUILayout.EndArea();
        }
    }
}
