using System.Collections;
using DataChannelUnity.Example;
using FishNet.Managing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace DataChannelUnity.Example.Tests
{
    /// <summary>
    /// Measures the outbound burst of a break, which is #136's acceptance line and #130's input.
    ///
    /// PlayMode, and it has to be: everything under test only exists once time is running — FishNet's
    /// tick, scene-object spawning, NetworkTransform's per-tick send. The existing headless check
    /// (BilliardsHeadlessCheck) steps physics by hand from an editor menu and deliberately covers only
    /// the physics claims; no amount of stepping makes a NetworkManager tick.
    ///
    /// Headless run, with the Editor not holding the project:
    ///
    ///   Unity -runTests -testPlatform PlayMode -batchmode -projectPath . \
    ///     -testResults Logs/playmode-billiards.xml \
    ///     -testFilter DataChannelUnity.Example.Tests
    ///
    /// Not run through the MCP bridge: entering play mode kills it (three attempts, editor alive
    /// afterwards), and a verification channel that dies as the thing under test starts is no channel.
    /// </summary>
    public sealed class BilliardsBurstPlayModeTests
    {
        private const string ScenePath =
            "Assets/DataChannelUnity.Example/Scenes/Billiards over DataChannel.unity";

        /// <summary>
        /// Generous: the host's local client is a real loopback PeerConnection (#120), so this waits on
        /// ICE gathering plus a DTLS handshake, not just a flag flip.
        /// </summary>
        private const float HostStartTimeout = 30f;

        private const float SettleTimeout = 25f;

        private NetworkManager _manager;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            yield return new EnterPlayMode();

            AsyncOperation load = SceneManager.LoadSceneAsync(ScenePath, LoadSceneMode.Single);
            while (load != null && !load.isDone)
                yield return null;

            _manager = Object.FindObjectOfType<NetworkManager>();
            Assert.IsNotNull(_manager, $"No NetworkManager in {ScenePath}. Rebuild it with " +
                                       "Tools/DataChannel Example/Build Billiards Scene.");
        }

        [UnityTest]
        public IEnumerator BreakBurstStaysUnderMtu()
        {
            var meter = Object.FindObjectOfType<OutboundByteMeter>();
            Assert.IsNotNull(meter, "No OutboundByteMeter in the scene; nothing would be measured.");

            var rack = Object.FindObjectOfType<BilliardsRack>();
            Assert.IsNotNull(rack, "No BilliardsRack in the scene.");

            // Host: server and client in one process, the local client on a real loopback
            // PeerConnection (#120). That loopback is why host mode measures real bytes — the server
            // sends to it through the ordinary transport path.
            _manager.ServerManager.StartConnection();
            _manager.ClientManager.StartConnection();

            float waited = 0f;
            while (!_manager.IsHostStarted && waited < HostStartTimeout)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Assert.IsTrue(_manager.IsHostStarted,
                $"Host did not start within {HostStartTimeout}s (ICE + DTLS on loopback).");

            // Spawned as scene objects, or nothing replicates. This is the assertion that catches an
            // unset SceneId or WasActiveDuringEdit_Set1 — FishNet skips those objects, and a skipped
            // ball rolls perfectly while sending nothing.
            int spawned = 0;
            foreach (BilliardsBall ball in rack.Balls)
            {
                var nob = ball.GetComponent<FishNet.Object.NetworkObject>();
                if (nob != null && nob.IsSpawned)
                    spawned++;
            }

            Assert.AreEqual(16, spawned,
                $"{spawned}/16 balls spawned. Zero means the NetworkObjects have no SceneId or were " +
                "never marked active during edit; rebuild the scene.");

            // Racked explicitly before measuring. The scene carries BilliardsBreakProbe with
            // _breakOnStart, which fires half a second into play — well before a loopback connection
            // has finished its handshake — so without this the measurement would be taken from a
            // scattered table with balls already pocketed, and would silently not be a break at all.
            rack.ResetRack();

            // Then let it go quiet: spawn messages are large one-off reliable sends, and the reset
            // itself moves sixteen balls, both of which would dominate a figure meant to describe the
            // rolling burst.
            for (int i = 0; i < 90; i++)
                yield return null;

            meter.Reset();
            meter.MeasureFromTick = _manager.TimeManager.LocalTick;

            rack.Break(Vector3.right, 4.0f);

            float shot = 0f;
            while (rack.ShotInFlight && shot < SettleTimeout)
            {
                shot += Time.deltaTime;
                yield return null;
            }

            // Recorded alongside the bytes because it changes what the bytes mean: a pocketed ball is
            // parked and silent, so a break that sinks several is a smaller load than one that sinks
            // none. A peak with no pocket count next to it cannot be compared against another run.
            int pocketed = 0;
            foreach (BilliardsBall ball in rack.Balls)
            {
                if (ball.IsPocketed)
                    pocketed++;
            }

            string report = meter.Report();
            Debug.Log($"[BilliardsBurst] settled={!rack.ShotInFlight} after {shot:F2}s " +
                      $"pocketed={pocketed}\n{report}");
            WriteReport(report, shot, !rack.ShotInFlight, spawned, pocketed);

            Assert.Greater(meter.TicksRecorded, 0,
                "No outbound bytes recorded during the break. Either the meter never attached or the " +
                "balls are not replicating.");

            // The only hard assertion, and deliberately the only one. Crossing GetMTU's 1282 would put
            // FishNet's split-packet path in play — it forces oversized messages onto the reliable
            // channel, so stale ball positions would be retransmitted (#119). #131 predicted this
            // never happens; asserting a floor instead would either fail on arrival or pass by luck,
            // and the number itself belongs in the report for #130 to read.
            Assert.LessOrEqual(PeakUnreliable(report), 1282,
                "A tick's unreliable payload crossed GetMTU (1282); FishNet would split it onto the " +
                "reliable channel. See the report.");
        }

        /// <summary>
        /// Written to a file as well as the log: the test log is the only other copy, and the number is
        /// #130's input rather than a pass/fail.
        /// </summary>
        private static void WriteReport(string report, float settleSeconds, bool settled, int spawned,
            int pocketed)
        {
            try
            {
                string path = System.IO.Path.Combine(
                    Application.dataPath, "..", "Logs", "billiards-burst.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path,
                    $"spawned={spawned}/16 settled={settled} settleSeconds={settleSeconds:F2} " +
                    $"pocketed={pocketed}\n\n{report}");
                Debug.Log($"[BilliardsBurst] report written to {System.IO.Path.GetFullPath(path)}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BilliardsBurst] could not write report: {e.Message}");
            }
        }

        private static int PeakUnreliable(string report)
        {
            foreach (string line in report.Split('\n'))
            {
                if (!line.StartsWith("peakUnreliable="))
                    continue;

                string value = line.Substring("peakUnreliable=".Length);
                int end = value.IndexOf('B');
                if (end > 0 && int.TryParse(value.Substring(0, end), out int bytes))
                    return bytes;
            }

            return 0;
        }

        /// <summary>
        /// Each manager is null-checked separately rather than guarded by `_manager != null` alone.
        /// NetworkManager abandons Awake when validation fails (NetworkManager.cs:260), leaving the
        /// object alive with its sub-managers unset — so a teardown that assumes they exist throws a
        /// NullReferenceException that lands on top of, and hides, whatever actually went wrong.
        /// </summary>
        [UnityTearDown]
        public IEnumerator StopHost()
        {
            // Tolerated only from here on, and only because an orderly shutdown genuinely produces an
            // Error: closing the host tears down DTLS, and libdatachannel logs "DataChannel is closed"
            // through DataChannelLog.Emit at Error severity (DataChannelLog.cs:79). The test framework
            // fails any test that logs an unexpected Error, so without this every PlayMode test that
            // starts a connection fails during cleanup, whatever it was measuring.
            //
            // Scoped to teardown deliberately: errors during the measurement itself still fail the
            // test. Worth a separate look at whether an ordinary close should be Error at all — that
            // is a package-level severity question, not this ticket's.
            LogAssert.ignoreFailingMessages = true;

            if (_manager != null)
            {
                if (_manager.ClientManager != null)
                    _manager.ClientManager.StopConnection();
                if (_manager.ServerManager != null)
                    _manager.ServerManager.StopConnection(true);
                yield return null;
            }

            yield return new ExitPlayMode();
        }
    }
}
