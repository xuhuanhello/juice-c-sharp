using System.Text;
using FishNet.Managing;
using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// 屏幕角上一块诊断面板：ping、连接走直连还是中继、tick。
    ///
    /// demo 自带 `BandwidthDisplay`（进出 kbps），这块补它没有的那两项。用 IMGUI 而不
    /// 是 uGUI，因为它只是个读数窗口，不值得为它建 Canvas 层级。
    /// </summary>
    public sealed class ConnectionDiagnosticsHud : MonoBehaviour
    {
        [SerializeField] private NetworkManager _networkManager;
        [SerializeField] private DataChannelTransport _transport;

        [SerializeField]
        [Tooltip("面板贴哪个角。")]
        private TextAnchor _corner = TextAnchor.UpperRight;

        private GUIStyle _style;
        private readonly StringBuilder _sb = new StringBuilder();

        private void Awake()
        {
            if (_networkManager == null) _networkManager = GetComponentInParent<NetworkManager>() ?? FindObjectOfType<NetworkManager>();
            if (_transport == null && _networkManager != null) _transport = _networkManager.GetComponent<DataChannelTransport>();
        }

        private void OnGUI()
        {
            if (_networkManager == null) return;

            _style ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = Color.white },
            };

            _sb.Clear();

            var tm = _networkManager.TimeManager;
            if (tm != null)
            {
                // RoundTripTime 量化到 tick —— 一档就是 TickDelta。把两者一起显示，
                // 否则「100ms」看着像真的测到了 100ms，其实是「1 个 tick」。
                var tickMs = tm.TickDelta * 1000d;

                // **这个数是本机 client 的 RTT，不是对手的。** 在 host 上它量的是那条进程内
                // loopback —— pong 恒定晚一个 tick，所以它永远读一档（TickRate 30 下就是 33 ms），
                // 与对手的延迟毫无关系。FishNet 没有服务端 per-connection RTT 的公开面
                // （`NetworkConnection.PingPong.cs` 里只有限流用的 private 成员），要显示各玩家的
                // ping 得让每个 client 用 RPC 上报，那是 app 层的活。
                //
                // 实测过这个读数怎么骗人（#139）：host 显示 33 ms 而 client 显示 0 ms，看着像
                // 「client 那端更快」，其实两个数量的是两条不同的连接。所以标签必须说清是谁的。
                bool onHost = _networkManager.IsServerStarted && _networkManager.IsClientStarted;
                string whose = onHost ? "本机 loopback，非对手" : "本机到 host";
                _sb.AppendLine($"ping   {tm.RoundTripTime} ms   ({whose}；量化到 tick，1 档 = {tickMs:F0} ms)");
                if (onHost)
                    _sb.AppendLine("       ↑ host 上这个数恒为一档，不反映对手延迟");
                _sb.AppendLine($"tick   {tm.Tick}   @ {tm.TickRate}/s");
            }

            if (_transport != null)
            {
                // 本地 client 那条：host 上它是 loopback，所以必然 Direct。
                if (_networkManager.IsClientStarted &&
                    _transport.TryGetConnectionPath(-1, out var myPath, out _))
                    _sb.AppendLine($"本机连接 {myPath}");

                if (_networkManager.IsServerStarted)
                {
                    var server = _networkManager.ServerManager;
                    if (server != null)
                    {
                        foreach (var kv in server.Clients)
                        {
                            var label = kv.Value.IsHost ? $"client {kv.Key} (host 本机)" : $"client {kv.Key}";
                            _sb.AppendLine(_transport.TryGetConnectionPath(kv.Key, out var p, out var sdp)
                                ? $"{label}  {p}   {Shorten(sdp)}"
                                : $"{label}  路径未知（未连接）");
                        }
                    }
                }
            }

            if (_sb.Length == 0) return;

            var text = _sb.ToString();
            var size = _style.CalcSize(new GUIContent(text));
            const float pad = 8f;
            var w = size.x + pad * 2;
            var h = size.y + pad * 2;
            var x = _corner == TextAnchor.UpperRight || _corner == TextAnchor.LowerRight ? Screen.width - w - 10f : 10f;
            var y = _corner == TextAnchor.LowerLeft || _corner == TextAnchor.LowerRight ? Screen.height - h - 10f : 10f;

            var rect = new Rect(x, y, w, h);
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = prev;
            GUI.Label(new Rect(x + pad, y + pad, size.x, size.y), text, _style);
        }

        /// <summary>候选 SDP 整行太长，只留「typ xxx」那部分 —— 那是有信息量的一半。</summary>
        private static string Shorten(string sdp)
        {
            if (string.IsNullOrEmpty(sdp)) return string.Empty;
            var i = sdp.IndexOf("typ ", System.StringComparison.Ordinal);
            return i >= 0 ? sdp.Substring(i) : sdp;
        }
    }
}
