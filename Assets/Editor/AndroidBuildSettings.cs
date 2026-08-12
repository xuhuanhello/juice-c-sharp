using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public enum AndroidBuildFormat
{
    APK,
    AAB,
}

[Serializable]
public sealed class AndroidBuildSettings
{
    private const string DefaultPackageName = "com.xuhuanhello.datachannel";
    private const string DefaultOutputDirectory = "Build/Android";

    public string ProductName;
    public string PackageName;
    public string Version;
    public int VersionCode;
    public bool DevelopmentBuild;
    public AndroidBuildFormat Format;
    public string OutputDirectory;
    public string KeystorePath;
    public string KeyAlias;
    public string KeystorePassword;
    public string KeyAliasPassword;

    private static string Prefix => "juice-c-sharp.AndroidBuild.";

    public static AndroidBuildSettings Load()
    {
        return new AndroidBuildSettings
        {
            ProductName = EditorPrefs.GetString(Prefix + "ProductName", PlayerSettings.productName),
            PackageName = EditorPrefs.GetString(Prefix + "PackageName", DefaultPackageName),
            Version = EditorPrefs.GetString(Prefix + "Version", PlayerSettings.bundleVersion),
            VersionCode = EditorPrefs.GetInt(Prefix + "VersionCode", PlayerSettings.Android.bundleVersionCode),
            DevelopmentBuild = EditorPrefs.GetBool(Prefix + "DevelopmentBuild", EditorUserBuildSettings.development),
            Format = (AndroidBuildFormat)EditorPrefs.GetInt(Prefix + "Format", EditorUserBuildSettings.buildAppBundle ? (int)AndroidBuildFormat.AAB : (int)AndroidBuildFormat.APK),
            OutputDirectory = EditorPrefs.GetString(Prefix + "OutputDirectory", DefaultOutputDirectory),
            KeystorePath = EditorPrefs.GetString(Prefix + "KeystorePath", string.Empty),
            KeyAlias = EditorPrefs.GetString(Prefix + "KeyAlias", PlayerSettings.Android.keyaliasName),
            KeystorePassword = EditorPrefs.GetString(Prefix + "KeystorePassword", string.Empty),
            KeyAliasPassword = EditorPrefs.GetString(Prefix + "KeyAliasPassword", string.Empty),
        };
    }

    public void Save()
    {
        EditorPrefs.SetString(Prefix + "ProductName", ProductName ?? string.Empty);
        EditorPrefs.SetString(Prefix + "PackageName", PackageName ?? string.Empty);
        EditorPrefs.SetString(Prefix + "Version", Version ?? string.Empty);
        EditorPrefs.SetInt(Prefix + "VersionCode", VersionCode);
        EditorPrefs.SetBool(Prefix + "DevelopmentBuild", DevelopmentBuild);
        EditorPrefs.SetInt(Prefix + "Format", (int)Format);
        EditorPrefs.SetString(Prefix + "OutputDirectory", OutputDirectory ?? DefaultOutputDirectory);
        EditorPrefs.SetString(Prefix + "KeystorePath", KeystorePath ?? string.Empty);
        EditorPrefs.SetString(Prefix + "KeyAlias", KeyAlias ?? string.Empty);
        EditorPrefs.SetString(Prefix + "KeystorePassword", KeystorePassword ?? string.Empty);
        EditorPrefs.SetString(Prefix + "KeyAliasPassword", KeyAliasPassword ?? string.Empty);
    }

    public void ApplyToPlayerSettings()
    {
        PlayerSettings.productName = ProductName;
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, PackageName);
        PlayerSettings.bundleVersion = Version;
        PlayerSettings.Android.bundleVersionCode = VersionCode;
        PlayerSettings.Android.useCustomKeystore = true;
        PlayerSettings.Android.keystoreName = KeystorePath;
        PlayerSettings.Android.keystorePass = KeystorePassword;
        PlayerSettings.Android.keyaliasName = KeyAlias;
        PlayerSettings.Android.keyaliasPass = KeyAliasPassword;
        EditorUserBuildSettings.buildAppBundle = Format == AndroidBuildFormat.AAB;
        EditorUserBuildSettings.development = DevelopmentBuild;
    }

    public string GetOutputPath(string baseName)
    {
        var directory = string.IsNullOrWhiteSpace(OutputDirectory) ? DefaultOutputDirectory : OutputDirectory;
        var extension = Format == AndroidBuildFormat.AAB ? ".aab" : ".apk";
        return Path.Combine(directory, baseName + extension).Replace('\\', '/');
    }

    public string Validate()
    {
        if (string.IsNullOrWhiteSpace(ProductName)) return "显示名称不能为空。";
        if (string.IsNullOrWhiteSpace(PackageName) || !IsValidPackageName(PackageName))
            return "包名必须至少包含两个以点分隔的合法标识符，例如 com.example.app。";
        if (string.IsNullOrWhiteSpace(Version)) return "版本不能为空。";
        if (VersionCode <= 0) return "Version Code 必须是正整数。";
        if (string.IsNullOrWhiteSpace(KeystorePath) || !File.Exists(KeystorePath))
            return "签名文件不存在：" + KeystorePath;
        if (string.IsNullOrWhiteSpace(KeyAlias)) return "Alias 不能为空。";
        if (string.IsNullOrEmpty(KeystorePassword)) return "Keystore 密码不能为空。";
        if (string.IsNullOrEmpty(KeyAliasPassword)) return "Alias 密码不能为空。";
        return null;
    }

    private static bool IsValidPackageName(string packageName)
    {
        var parts = packageName.Split('.');
        if (parts.Length < 2) return false;
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part) || !(char.IsLetter(part[0]) || part[0] == '_')) return false;
            for (var i = 1; i < part.Length; i++)
                if (!(char.IsLetterOrDigit(part[i]) || part[i] == '_')) return false;
        }
        return true;
    }
}

public static class AndroidBuildService
{
    public static BuildReport Build(AndroidBuildSettings settings, string outputName, string[] scenes = null, AndroidBuildFormat? forcedFormat = null)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (forcedFormat.HasValue) settings.Format = forcedFormat.Value;

        var validationError = settings.Validate();
        if (validationError != null) throw new BuildFailedException(validationError);

        settings.Save();
        settings.ApplyToPlayerSettings();

        if (scenes == null) scenes = GetEnabledScenes();
        if (scenes.Length == 0) throw new BuildFailedException("Build Settings 中没有启用的场景。");

        var outputPath = settings.GetOutputPath(outputName);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        var options = settings.DevelopmentBuild ? BuildOptions.Development : BuildOptions.None;
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = BuildTarget.Android,
            options = options,
        });

        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException("Android 构建失败：" + report.summary.totalErrors + " 个错误。请查看 Console 与 BuildReport。");

        return report;
    }

    private static string[] GetEnabledScenes()
    {
        var enabled = new System.Collections.Generic.List<string>();
        foreach (var scene in EditorBuildSettings.scenes)
            if (scene.enabled) enabled.Add(scene.path);
        return enabled.ToArray();
    }
}
