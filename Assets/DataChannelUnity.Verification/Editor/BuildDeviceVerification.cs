using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace DataChannelUnity.Verification.Editor
{
    /// <summary>
    /// 把 <see cref="DeviceVerificationRunner"/> 的场景构成一个 Player，供真机验证使用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这里**只出 Player**，不负责装机与启动。iOS 上装机与启动必须由 <c>xcodebuild</c>
    /// 在 Editor 之外完成，原因见 <see cref="DeviceVerificationRunner"/> 的注释：
    /// Unity 那条 launch 路只从 Build Settings 窗口的按钮进，batchmode 下没有调用者。
    /// 把装机也塞进这个脚本只会让它在 batchmode 里静默地什么都不做。
    /// </para>
    /// <para>
    /// 签名参数**不写死在这里**，也不入库（#96）：Team ID 是每个贡献者自己的。
    /// 构出 Xcode 工程后由调用方把签名参数传给 <c>xcodebuild</c>。
    /// </para>
    /// </remarks>
    public static class BuildDeviceVerification
    {
        private const string ScenePath = "Assets/DataChannelUnity.Verification/DeviceVerification.unity";

        [MenuItem("Tools/DataChannelUnity/Build device verification (iOS)")]
        public static void BuildIOSFromMenu() => BuildIOS(DefaultOutputPath("ios"));

        /// <summary>batchmode 入口。用 <c>-deviceVerificationOutput &lt;path&gt;</c> 指定输出目录。</summary>
        public static void BuildIOS()
        {
            var output = ArgValue("-deviceVerificationOutput") ?? DefaultOutputPath("ios");
            BuildIOS(output);
        }

        private static void BuildIOS(string outputPath)
        {
            if (!File.Exists(ScenePath))
                throw new FileNotFoundException("Verification scene is missing: " + ScenePath);

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = outputPath,
                target = BuildTarget.iOS,
                targetGroup = BuildTargetGroup.iOS,
                options = BuildOptions.None,
            };

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;

            Debug.Log($"[DeviceVerification] iOS build {summary.result}: {outputPath}");

            // batchmode 下必须显式定退出码：BuildPlayer 失败时进程默认仍然退 0，
            // 调用方会把一次失败的构建当成成功，然后在装机那一步才炸，且看不出根因。
            if (Application.isBatchMode)
                EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        private static string DefaultOutputPath(string suffix) =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Build", "device-verification-" + suffix);

        private static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
