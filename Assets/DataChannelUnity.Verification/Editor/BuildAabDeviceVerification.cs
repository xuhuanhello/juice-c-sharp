using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace DataChannelUnity.Verification.Editor
{
    /// <summary>
    /// 把 <see cref="DeviceVerificationRunner"/> 的场景构成一个 Android AAB，供真机验证使用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="BuildDeviceVerification"/> 同形：**只出产物**，不负责装机与启动，
    /// 用的也是同一个入库场景，而不是每次现生成一个 —— 现生成会让真机验证的对象
    /// 与仓库里的场景脱节，出了问题无法复现。
    /// </para>
    /// <para>
    /// 签名参数**不写死在这里**，也不入库：keystore 与口令是每个贡献者自己的，
    /// 经 <see cref="AndroidBuildSettings"/> 存在本机 EditorPrefs 里。
    /// </para>
    /// </remarks>
    public static class BuildAabDeviceVerification
    {
        private const string ScenePath = "Assets/DataChannelUnity.Verification/DeviceVerification.unity";

        [MenuItem("Tools/DataChannelUnity/Build device verification (Android AAB)")]
        public static void BuildFromMenu() => Build(null);

        /// <summary>batchmode 入口。用 <c>-deviceVerificationOutput &lt;name&gt;</c> 指定产物名。</summary>
        public static void Build()
        {
            Build(ArgValue("-deviceVerificationOutput"));
        }

        private static void Build(string outputName)
        {
            if (!File.Exists(ScenePath))
                throw new FileNotFoundException("Verification scene is missing: " + ScenePath);

            var settings = AndroidBuildSettings.Load();
            var succeeded = false;
            try
            {
                var report = AndroidBuildService.Build(
                    settings,
                    outputName ?? "aab-device-verification",
                    new[] { ScenePath },
                    AndroidBuildFormat.AAB);

                succeeded = true;
                Debug.Log($"[DeviceVerification] Android AAB build {report.summary.result}: {report.summary.outputPath}");
            }
            catch (BuildFailedException e)
            {
                Debug.LogError("[DeviceVerification] Android AAB build failed: " + e.Message);
            }

            // batchmode 下必须显式定退出码：理由同 BuildDeviceVerification —— 失败时
            // 进程默认仍然退 0，调用方会把一次失败的构建当成成功。
            if (Application.isBatchMode)
                EditorApplication.Exit(succeeded ? 0 : 1);
        }

        private static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
