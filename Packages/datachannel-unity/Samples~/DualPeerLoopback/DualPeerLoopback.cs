using System.Text;
using System.Collections;
using UnityEngine;
using DataChannelUnity;

namespace DataChannelUnity.Samples
{
    /// <summary>
    /// In-process two peers with in-memory signaling. Sends one binary message each way.
    /// Requires a built datachannel_unity native plugin for the Editor platform.
    /// </summary>
    public sealed class DualPeerLoopback : MonoBehaviour
    {
        [SerializeField] float timeoutSeconds = 15f;

        PeerConnection _a;
        PeerConnection _b;
        DataChannel _dcA;
        DataChannel _dcB;
        bool _gotA;
        bool _gotB;

        IEnumerator Start()
        {
            DataChannelLog.Level = LogLevel.Info;
            // #146：native 是惰性加载的，读 IsNativeAvailable 不再触发加载 ——
            // 示例在自选时刻显式预热，失败时异常消息点名修法。
            try
            {
                DataChannelRuntime.Preload();
            }
            catch (DataChannelException e)
            {
                Debug.LogError("DualPeerLoopback: native plugin missing. Run native/scripts/build-*.sh — " + e.Message);
                yield break;
            }

            var config = new PeerConnectionConfig(); // empty ICE → host candidates / loopback

            _a = new PeerConnection(config);
            _b = new PeerConnection(config);

            // In-memory signaling A → B
            _a.LocalDescriptionGenerated += (sdp, type) =>
            {
                Debug.Log($"A local {type}");
                _b.SetRemoteDescription(sdp, type);
            };
            _a.LocalCandidateGenerated += (cand, mid) =>
            {
                _b.AddRemoteCandidate(cand, mid);
            };

            // B → A
            _b.LocalDescriptionGenerated += (sdp, type) =>
            {
                Debug.Log($"B local {type}");
                _a.SetRemoteDescription(sdp, type);
            };
            _b.LocalCandidateGenerated += (cand, mid) =>
            {
                _a.AddRemoteCandidate(cand, mid);
            };

            _b.DataChannelReceived += ch =>
            {
                _dcB = ch;
                _dcB.Opened += () => Debug.Log("B DC open");
                _dcB.MessageReceived += data =>
                {
                    var text = Encoding.UTF8.GetString(data);
                    Debug.Log("B received: " + text);
                    _gotB = true;
                    _dcB.Send(Encoding.UTF8.GetBytes("pong-from-b"));
                };
            };

            _dcA = _a.CreateDataChannel("loopback");
            _dcA.Opened += () =>
            {
                Debug.Log("A DC open — sending ping");
                _dcA.Send(Encoding.UTF8.GetBytes("ping-from-a"));
            };
            _dcA.MessageReceived += data =>
            {
                var text = Encoding.UTF8.GetString(data);
                Debug.Log("A received: " + text);
                _gotA = true;
            };

            float t = 0f;
            while (t < timeoutSeconds && !(_gotA && _gotB))
            {
                // Pump is automatic via PlayerLoop; keep a frame yield.
                t += Time.deltaTime;
                yield return null;
            }

            if (_gotA && _gotB)
                Debug.Log("DualPeerLoopback SUCCESS");
            else
                Debug.LogError($"DualPeerLoopback TIMEOUT gotA={_gotA} gotB={_gotB}");
        }

        void OnDestroy()
        {
            _dcA?.Dispose();
            _dcB?.Dispose();
            _a?.Dispose();
            _b?.Dispose();
        }
    }
}
