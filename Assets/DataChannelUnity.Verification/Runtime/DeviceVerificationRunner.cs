using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DataChannelUnity;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace DataChannelUnity.Verification
{
    /// <summary>
    /// 真机设备验证入口（Player-resident）。
    /// 它复现 Runtime PlayMode 测试的断言，但不引用 Unity Test Framework；结果写为 NUnit 3 XML。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是 CONTRIBUTING「Keep a machine-judged Runtime report」那条**替代条款**的实现：
    /// 首选仍是用 Test Runner 把 <c>DataChannelUnity.Tests.Runtime</c> 构进 Player 并附上
    /// 它的 NUnit XML；只有当那条工具路在某平台上走不通时，才用这个等价 runner 顶上。
    /// </para>
    /// <para>
    /// 两个平台各有各的走不通：
    /// Android 是 Play 分发的 AAB 安装路径本身要被验证（那条路装出来的包不是 Test Runner 出的）；
    /// iOS 是 <c>-runTests -testPlatform iOS -batchmode</c> **结构上**不会启动真机 Player ——
    /// launch 由 <c>iOStvOSCommonBuildWindowExtension.DoBuildAndRun()</c> 执行，那是 Build
    /// Settings 窗口的扩展，只从「Build And Run」按钮进；而 <c>-runTests</c> 走
    /// <c>PlayerLauncher</c> → <c>BuildPipeline.BuildPlayer</c>，不经过它。batchmode 没有窗口，
    /// 也就没有调用者，于是工程出得来、<c>xcodebuild</c> 从不被调用、600 秒心跳超时（#97）。
    /// </para>
    /// <para>
    /// 报告里的 <c>framework</c> 属性**必须**继续写明「不是 Unity Test Framework」。
    /// 替代条款换掉的是产出 XML 的那个工具，不是「机器可判、计数非零」这条判据本身；
    /// 把它伪装成 UTF 的输出会让读报告的人以为跑的是首选路径。
    /// </para>
    /// </remarks>
    public sealed class DeviceVerificationRunner : MonoBehaviour
    {
        private const float LoopbackTimeoutSeconds = 20f;
        private const float PumpStaleWaitSeconds = 6.5f;

        // iOS 上插件是静态库，符号直接链进可执行文件，所以是 __Internal 而不是库名 ——
        // 与 Tests/Runtime/NativeSuiteTeardown.cs 里同一条判别保持一致。
#if UNITY_IOS && !UNITY_EDITOR
        private const string NativeDll = "__Internal";
#elif UNITY_WEBGL && !UNITY_EDITOR
        private const string NativeDll = "__Internal";
#else
        private const string NativeDll = "datachannel_unity";
#endif

        private readonly List<CaseResult> _results = new List<CaseResult>();
        private readonly List<string> _capturedLogs = new List<string>();
        private string _reportPath;
        private string _phase = "准备中";

        [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dcu_shutdown(out int undestroyed);

        [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dcu_init();

        [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dcu_event_queue_depth(out int depth);

        private void Start()
        {
            _reportPath = Path.Combine(Application.persistentDataPath, "device-verification.xml");
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            DataChannelLog.MessageLogged += CaptureLog;
            try
            {
                yield return RunCase("DataChannelUnity.Tests.Runtime.PumpRegistrationPlayModeTests.DualPeerLoopback_FlowsWithoutAnyManualPump", RunLoopback);
                yield return RunCase("DataChannelUnity.Tests.Runtime.PumpLivenessPlayModeTests.ErasedPump_ReregistersOnce_ThenStopsRetrying", RunPumpLiveness);
                yield return RunCase("DataChannelUnity.Tests.Runtime.NativeSuiteTeardown.AssertDrainedAndShutDownCleanly", RunTeardown);
            }
            finally
            {
                DataChannelLog.MessageLogged -= CaptureLog;
                WriteReport();
                _phase = AllPassed() ? "验证通过；XML 已写入" : "验证失败；XML 已写入";
            }
        }

        private IEnumerator RunCase(string name, Func<IEnumerator> test)
        {
            _phase = "运行 " + name;
            var result = new CaseResult { Name = name };
            var started = Time.realtimeSinceStartup;
            var enumerator = test();
            while (true)
            {
                object current;
                try
                {
                    if (!enumerator.MoveNext()) break;
                    current = enumerator.Current;
                }
                catch (Exception e)
                {
                    result.Failure = e.ToString();
                    break;
                }

                yield return current;
            }
            result.DurationSeconds = Time.realtimeSinceStartup - started;
            _results.Add(result);
        }

        private IEnumerator RunLoopback()
        {
            // #146：惰性范式下第一阶段要自己显式预热（旧行为靠 Bootstrap 急切加载）。
            // 后续阶段的 IsNativeAvailable 是被动检查，此时已加载，保持原样。
            DataChannelRuntime.Preload();
            Require(DataChannelRuntime.IsNativeAvailable, "Native plugin not loaded. This is a failure, not a skip.");

            string gotA = null;
            string gotB = null;
            var aOpened = false;
            DataChannel incoming = null;

            using (var a = new PeerConnection(new PeerConnectionConfig()))
            using (var b = new PeerConnection(new PeerConnectionConfig()))
            {
                a.LocalDescriptionGenerated += (sdp, type) => b.SetRemoteDescription(sdp, type);
                b.LocalDescriptionGenerated += (sdp, type) => a.SetRemoteDescription(sdp, type);
                a.LocalCandidateGenerated += (candidate, mid) => b.AddRemoteCandidate(candidate, mid);
                b.LocalCandidateGenerated += (candidate, mid) => a.AddRemoteCandidate(candidate, mid);

                b.DataChannelReceived += channel =>
                {
                    incoming = channel;
                    channel.MessageReceived += bytes =>
                    {
                        gotB = Encoding.UTF8.GetString(bytes.ToArray());
                        channel.Send(Encoding.UTF8.GetBytes("pong"));
                    };
                };

                var outgoing = a.CreateDataChannel("device-playmode-smoke");
                outgoing.Opened += () =>
                {
                    aOpened = true;
                    outgoing.Send(Encoding.UTF8.GetBytes("ping"));
                };
                outgoing.MessageReceived += bytes => gotA = Encoding.UTF8.GetString(bytes.ToArray());

                var deadline = Time.realtimeSinceStartup + LoopbackTimeoutSeconds;
                while (Time.realtimeSinceStartup < deadline && (gotA == null || gotB == null)) yield return null;

                Require(incoming != null, "The remote peer never received DataChannelReceived.");
                Require(aOpened, "The outbound channel never received an Open event.");
                Require(gotB == "ping", "The remote peer received no ping through the PlayerLoop.");
                Require(gotA == "pong", "The outbound peer received no pong through the PlayerLoop.");
            }
        }

        private IEnumerator RunPumpLiveness()
        {
            Require(DataChannelRuntime.IsNativeAvailable, "Native plugin not loaded. This is a failure, not a skip.");
            Require(PumpIsRegistered(), "Precondition violated: the pump should be registered.");

            _capturedLogs.Clear();
            ErasePumpEntry();
            Require(!PumpIsRegistered(), "The first erase had no effect.");
            yield return WaitRealtime(PumpStaleWaitSeconds);
            TouchPublicApi();
            Require(PumpIsRegistered(), "The first stale API call did not re-register the pump.");
            Require(ContainsLog("Retrying registration ONCE"), "The first retry diagnostic was not emitted.");
            Require(ContainsLog("SetPlayerLoop"), "The first retry diagnostic did not identify SetPlayerLoop.");

            _capturedLogs.Clear();
            ErasePumpEntry();
            Require(!PumpIsRegistered(), "The second erase had no effect.");
            yield return WaitRealtime(PumpStaleWaitSeconds);
            TouchPublicApi();
            Require(!PumpIsRegistered(), "The pump re-registered a second time.");
            Require(ContainsLog("Retries have STOPPED"), "The exhausted-retry diagnostic was not emitted.");

            InsertPumpEntry();
            for (var i = 0; i < 60; i++) yield return null;
        }

        private IEnumerator RunTeardown()
        {
            Require(DataChannelRuntime.IsNativeAvailable, "Native plugin not loaded, so suite teardown cannot run.");
            for (var i = 0; i < 40; i++)
            {
                DataChannelRuntime.Pump();
                Thread.Sleep(2);
            }

            Require(dcu_event_queue_depth(out var depthResult) == 0, "dcu_event_queue_depth call failed.");
            Require(depthResult == 0, "Control-event queue was not drained: " + depthResult + ".");

            var shutdownResult = dcu_shutdown(out var undestroyed);
            try
            {
                Require(shutdownResult == 0, "dcu_shutdown call failed: " + shutdownResult + ".");
                Require(undestroyed == 0, "Native objects still undestroyed: " + undestroyed + ".");
            }
            finally
            {
                dcu_init();
            }
            yield break;
        }

        private static IEnumerator WaitRealtime(float seconds)
        {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline) yield return null;
        }

        private static void TouchPublicApi()
        {
            using (var peer = new PeerConnection(new PeerConnectionConfig())) { }
        }

        private void CaptureLog(LogLevel level, string message)
        {
            _capturedLogs.Add(message);
        }

        private bool ContainsLog(string text)
        {
            for (var i = 0; i < _capturedLogs.Count; i++)
                if (_capturedLogs[i].Contains(text)) return true;
            return false;
        }

        private static void ErasePumpEntry()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            for (var i = 0; i < loop.subSystemList.Length; i++)
            {
                var subsystems = loop.subSystemList[i].subSystemList;
                if (subsystems == null) continue;
                var kept = new List<PlayerLoopSystem>(subsystems.Length);
                for (var j = 0; j < subsystems.Length; j++)
                    if (subsystems[j].type != typeof(DataChannelRuntime)) kept.Add(subsystems[j]);
                loop.subSystemList[i].subSystemList = kept.ToArray();
            }
            PlayerLoop.SetPlayerLoop(loop);
        }

        private static void InsertPumpEntry()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            for (var i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != typeof(Update)) continue;
                var subsystems = loop.subSystemList[i].subSystemList;
                var entries = subsystems == null ? new List<PlayerLoopSystem>() : new List<PlayerLoopSystem>(subsystems);
                entries.Add(new PlayerLoopSystem { type = typeof(DataChannelRuntime), updateDelegate = DataChannelRuntime.Pump });
                loop.subSystemList[i].subSystemList = entries.ToArray();
                PlayerLoop.SetPlayerLoop(loop);
                return;
            }
            throw new InvalidOperationException("No Update PlayerLoop segment found.");
        }

        private static bool PumpIsRegistered() => Contains(PlayerLoop.GetCurrentPlayerLoop());

        private static bool Contains(PlayerLoopSystem system)
        {
            if (system.type == typeof(DataChannelRuntime)) return true;
            if (system.subSystemList == null) return false;
            for (var i = 0; i < system.subSystemList.Length; i++)
                if (Contains(system.subSystemList[i])) return true;
            return false;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private bool AllPassed()
        {
            for (var i = 0; i < _results.Count; i++)
                if (_results[i].Failure != null) return false;
            return _results.Count > 0;
        }

        private void WriteReport()
        {
            try
            {
                var failures = 0;
                var totalDuration = 0f;
                for (var i = 0; i < _results.Count; i++)
                {
                    totalDuration += _results[i].DurationSeconds;
                    if (_results[i].Failure != null) failures++;
                }

                using (var writer = new System.Xml.XmlTextWriter(_reportPath, Encoding.UTF8))
                {
                    writer.Formatting = System.Xml.Formatting.Indented;
                    writer.WriteStartDocument();
                    writer.WriteStartElement("test-run");
                    writer.WriteAttributeString("id", "DeviceVerification");
                    writer.WriteAttributeString("name", "DeviceVerification");
                    writer.WriteAttributeString("testcasecount", _results.Count.ToString());
                    writer.WriteAttributeString("result", failures == 0 ? "Passed" : "Failed");
                    writer.WriteAttributeString("total", _results.Count.ToString());
                    writer.WriteAttributeString("passed", (_results.Count - failures).ToString());
                    writer.WriteAttributeString("failed", failures.ToString());
                    writer.WriteAttributeString("duration", totalDuration.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("framework", "DeviceVerificationRunner (equivalent Runtime suite; NOT Unity Test Framework)");
                    writer.WriteAttributeString("persistentDataPath", Application.persistentDataPath);
                    // 出处必须落在报告里：这份 XML 会被从设备上取回、贴进票里，
                    // 那时它已经离开了产生它的机器，只剩自己交代自己是谁跑的。
                    writer.WriteAttributeString("platform", Application.platform.ToString());
                    writer.WriteAttributeString("unityVersion", Application.unityVersion);
                    writer.WriteAttributeString("deviceModel", SystemInfo.deviceModel);
                    writer.WriteAttributeString("operatingSystem", SystemInfo.operatingSystem);
                    for (var i = 0; i < _results.Count; i++) WriteCase(writer, _results[i]);
                    writer.WriteEndElement();
                    writer.WriteEndDocument();
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[DeviceVerification] Failed to write XML: " + e);
            }
        }

        private static void WriteCase(System.Xml.XmlWriter writer, CaseResult result)
        {
            writer.WriteStartElement("test-case");
            writer.WriteAttributeString("name", result.Name);
            writer.WriteAttributeString("fullname", result.Name);
            writer.WriteAttributeString("result", result.Failure == null ? "Passed" : "Failed");
            writer.WriteAttributeString("duration", result.DurationSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            if (result.Failure != null)
            {
                writer.WriteStartElement("failure");
                writer.WriteElementString("message", result.Failure);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        private void OnGUI()
        {
            GUI.Label(new Rect(24, 24, Screen.width - 48, 100), _phase + "\n" + _reportPath);
            var y = 140;
            for (var i = 0; i < _results.Count; i++)
            {
                var result = _results[i];
                GUI.Label(new Rect(24, y, Screen.width - 48, 80), (result.Failure == null ? "PASS " : "FAIL ") + result.Name + (result.Failure == null ? "" : "\n" + result.Failure));
                y += result.Failure == null ? 34 : 100;
            }
        }

        private sealed class CaseResult
        {
            public string Name;
            public string Failure;
            public float DurationSeconds;
        }
    }
}
