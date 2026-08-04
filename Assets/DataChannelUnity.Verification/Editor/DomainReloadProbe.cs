using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using DataChannelUnity;
using UnityEditor;
using UnityEngine;

namespace DataChannelUnity.Verification
{
    /// <summary>
    /// 域重载生命周期自证（SPEC §11「Manual steps must still be machine-judged」/ #37）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **为什么是磁盘上的持久脚本。** 域重载会销毁动态编译的内存程序集 ——
    /// 用一个会被这次转换销毁的东西去测量这次转换，方向就是错的。#37 的实测过程
    /// 正是先用 <c>execute_code</c> 挂钩子失败，才得出这条。
    /// </para>
    /// <para>
    /// **为什么它不在包里。** 这是宿主工程的验证工具，不该随 UPM 包发给使用者。
    /// </para>
    /// <para>
    /// **判据是机器可判的，不是「Console 里有没有一行英文」。** 后者是
    /// CONTRIBUTING 那条「让缺席变成沉默」的第四种形态。判据如下：
    /// </para>
    /// <para>
    /// 布置阶段故意创建 N 个 <see cref="PeerConnection"/> / <see cref="DataChannel"/>
    /// 并**由本类静态字段持有**（持有是必需的 —— 否则它们可能在域重载前就被 GC，
    /// 那时 <c>DisposeAllLive()</c> 根本找不到它们，判据会随 GC 时机摇摆）。
    /// 域重载之后调 <c>dcu_shutdown</c> 取未销毁计数：
    /// </para>
    /// <list type="bullet">
    /// <item>清理钩子跑过 → 重载前已 <c>DisposeAllLive()</c> + <c>dcu_shutdown()</c>，
    /// 此刻原生表是空的 → 计数 <b>0</b>。</item>
    /// <item>钩子没跑 → 原生侧一切原封不动 → 计数 <b>N</b>，且正好等于布置时写下的数。</item>
    /// </list>
    /// </remarks>
    public static class DomainReloadProbe
    {
        private const string Menu = "Tools/DataChannelUnity/域重载自证/";
        private static readonly string ArtifactPath =
            Path.Combine(Directory.GetCurrentDirectory(), "Library", "dcu-domain-reload-probe.json");

        // **必须强持有。** 见类注释：不持有的话它们可能在域重载前被 GC，
        // DisposeAllLive() 就找不到它们，判据会随 GC 时机摇摆而不是随代码正确性变化。
        private static readonly List<PeerConnection> Held = new List<PeerConnection>();

        [DllImport("datachannel_unity", CallingConvention = CallingConvention.Cdecl)]
        private static extern int dcu_shutdown(out int undestroyed);

        [DllImport("datachannel_unity", CallingConvention = CallingConvention.Cdecl)]
        private static extern int dcu_init();

        [Serializable]
        private class Artifact
        {
            public string stage;
            public int plantedObjects;         // 布置了多少个原生对象（PC + DC）
            public bool enterPlayModeOptionsEnabled;
            public string enterPlayModeOptions;
            public string expectedPath;        // 本次配置下应当走哪条清理路径
            public int undestroyedAfterReload = -1;
            public string verdict = "未判定";
        }

        // ------------------------------------------------------------------

        [MenuItem(Menu + "1. 布置（创建对象，故意不 Dispose）")]
        public static void Plant()
        {
            if (!DataChannelRuntime.IsNativeAvailable)
            {
                Debug.LogError("[Probe] 原生插件未加载，无法布置。");
                return;
            }

            Held.Clear();
            var planted = 0;
            for (int i = 0; i < 2; i++)
            {
                var pc = new PeerConnection(new PeerConnectionConfig());
                planted++;                                  // PC 本身
                pc.CreateDataChannel("probe-" + i);
                planted++;                                  // 它的子通道
                Held.Add(pc);
            }

            // #44 留给本片的未决项：**先采样配置再判定**，否则不知道自己验的是哪条路径。
            // EnterPlayModeOptions 决定进入播放时走 beforeAssemblyReload（#37 决议 2）
            // 还是 SubsystemRegistration（#37 决议 4）—— SPEC §6 两条都写了，
            // 而它们由这个开关二选一。
            var optionsOn = EditorSettings.enterPlayModeOptionsEnabled;
            var options = EditorSettings.enterPlayModeOptions;
            var domainReloadDisabled = optionsOn && options.HasFlag(EnterPlayModeOptions.DisableDomainReload);

            var a = new Artifact
            {
                stage = "planted",
                plantedObjects = planted,
                enterPlayModeOptionsEnabled = optionsOn,
                enterPlayModeOptions = optionsOn ? options.ToString() : "(disabled)",
                expectedPath = domainReloadDisabled
                    ? "进入播放不重载域 → 走 SubsystemRegistration（#37 决议 4）。"
                      + "注意：这条路径下必须用「重新编译脚本」来触发域重载，进入播放是验不到 beforeAssemblyReload 的。"
                    : "域重载正常发生 → 走 beforeAssemblyReload（#37 决议 2）。"
                      + "编译脚本或进入播放模式都能触发。"
            };
            Write(a);

            Debug.Log("[Probe] 已布置 " + planted + " 个原生对象并持有，**刻意不 Dispose**。\n"
                      + "本次配置：" + a.enterPlayModeOptions + " → " + a.expectedPath + "\n"
                      + "下一步：触发一次域重载（改任意脚本重编译，或按配置进入播放模式），然后跑菜单「2. 判定」。\n"
                      + "产物：" + ArtifactPath);
        }

        [MenuItem(Menu + "2. 判定（域重载之后跑）")]
        public static void Judge()
        {
            var a = Read();
            if (a == null)
            {
                Debug.LogError("[Probe] 没有找到布置产物，先跑「1. 布置」。");
                return;
            }

            if (Held.Count > 0)
            {
                Debug.LogError("[Probe] 静态字段里还持有 " + Held.Count + " 个对象 —— "
                               + "说明**域重载没有发生**，这次判定无效。请先真正触发一次重载。");
                return;
            }

            // 域重载之后问原生侧：还剩几个没被销毁的对象？
            //   钩子跑过 → 重载前已 DisposeAllLive() + shutdown → 0
            //   钩子没跑 → 原生侧原封不动 → 等于布置时写下的 plantedObjects
            var rc = dcu_shutdown(out var undestroyed);
            a.stage = "judged";
            a.undestroyedAfterReload = undestroyed;

            if (rc != 0)
                a.verdict = "失败：dcu_shutdown 调用本身返回 " + rc;
            else if (undestroyed == 0)
                a.verdict = "通过：域重载前清理钩子已把 " + a.plantedObjects + " 个原生对象全部释放。";
            else
                a.verdict = "失败：域重载后仍有 " + undestroyed + " 个原生对象未销毁"
                            + (undestroyed == a.plantedObjects
                                ? "（正好等于布置数 —— 清理钩子完全没跑）。"
                                : "（布置了 " + a.plantedObjects + " 个 —— 清理只跑了一半）。");

            Write(a);

            // **判定之后必须把原生库重新拉起来。** dcu_shutdown 会跑 rtc::Cleanup，
            // 而托管侧的 DataChannelRuntime 完全不知情 —— 它的 _initAttempted 仍是 true，
            // 所以 EnsureNative() 会直接返回，**不会**重新 init。于是这个会话里后续
            // 每一次 dcu_pc_create 都打在一个已 Cleanup 的库上，返回 Failure。
            //
            // 这是实测撞出来的：跑完判定后整个 native 档 25 个用例全挂在
            // 「dcu_pc_create: 运行时失败 (raw=-102)」上。恢复动作发生在读完计数**之后**，
            // 不掩盖任何判定结果。
            dcu_init();

            var pass = rc == 0 && undestroyed == 0;
            if (pass) Debug.Log("[Probe] " + a.verdict + "\n产物：" + ArtifactPath);
            else Debug.LogError("[Probe] " + a.verdict + "\n产物：" + ArtifactPath);
        }

        [MenuItem(Menu + "3. 清除产物")]
        public static void ClearArtifact()
        {
            Held.Clear();
            if (File.Exists(ArtifactPath)) File.Delete(ArtifactPath);
            Debug.Log("[Probe] 已清除。");
        }

        // ------------------------------------------------------------------

        private static void Write(Artifact a)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ArtifactPath));
            File.WriteAllText(ArtifactPath, JsonUtility.ToJson(a, true));
        }

        private static Artifact Read()
        {
            if (!File.Exists(ArtifactPath)) return null;
            try { return JsonUtility.FromJson<Artifact>(File.ReadAllText(ArtifactPath)); }
            catch { return null; }
        }
    }
}
