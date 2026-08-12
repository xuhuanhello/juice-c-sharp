using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public sealed class AndroidBuildWindow : EditorWindow
{
    private AndroidBuildSettings _settings;
    private Vector2 _scroll;
    private string _status;
    private MessageType _statusType;

    [MenuItem("Tools/DataChannelUnity/Android 构建面板")]
    public static void Open()
    {
        var window = GetWindow<AndroidBuildWindow>("Android 构建");
        window.minSize = new Vector2(420f, 520f);
        window.Show();
    }

    private void OnEnable()
    {
        _settings = AndroidBuildSettings.Load();
    }

    private void OnGUI()
    {
        if (_settings == null) _settings = AndroidBuildSettings.Load();

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Android 构建", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("所有字段保存在本机 EditorPrefs。两个密码以明文保存在本机，不会写入项目文件或 Git。", MessageType.Info);

        DrawApplicationSection();
        DrawOutputSection();
        DrawSigningSection();
        DrawBuildSection();

        EditorGUILayout.EndScrollView();
    }

    private void DrawApplicationSection()
    {
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("应用标识", EditorStyles.boldLabel);
        _settings.ProductName = EditorGUILayout.TextField("显示名称", _settings.ProductName);
        _settings.PackageName = EditorGUILayout.TextField("包名", _settings.PackageName);
        _settings.Version = EditorGUILayout.TextField("Version", _settings.Version);
        _settings.VersionCode = EditorGUILayout.IntField("Version Code", _settings.VersionCode);
        _settings.DevelopmentBuild = EditorGUILayout.Toggle("Development Build", _settings.DevelopmentBuild);
    }

    private void DrawOutputSection()
    {
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("输出", EditorStyles.boldLabel);
        _settings.Format = (AndroidBuildFormat)EditorGUILayout.EnumPopup("格式", _settings.Format);

        EditorGUILayout.BeginHorizontal();
        _settings.OutputDirectory = EditorGUILayout.TextField("输出目录", _settings.OutputDirectory);
        if (GUILayout.Button("选择", GUILayout.Width(56f)))
        {
            var selected = EditorUtility.OpenFolderPanel("选择 Android 构建输出目录", _settings.OutputDirectory, string.Empty);
            if (!string.IsNullOrEmpty(selected)) _settings.OutputDirectory = selected;
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawSigningSection()
    {
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("签名", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        _settings.KeystorePath = EditorGUILayout.TextField("Keystore", _settings.KeystorePath);
        if (GUILayout.Button("选择", GUILayout.Width(56f)))
        {
            var selected = EditorUtility.OpenFilePanel("选择 Keystore", _settings.KeystorePath, "keystore");
            if (!string.IsNullOrEmpty(selected)) _settings.KeystorePath = selected;
        }
        EditorGUILayout.EndHorizontal();

        _settings.KeystorePassword = EditorGUILayout.PasswordField("Keystore 密码", _settings.KeystorePassword);
        _settings.KeyAlias = EditorGUILayout.TextField("Alias", _settings.KeyAlias);
        _settings.KeyAliasPassword = EditorGUILayout.PasswordField("Alias 密码", _settings.KeyAliasPassword);
        EditorGUILayout.HelpBox("Unity 2022.3 的公开 Editor API 不提供从 Keystore 读取 Alias 的能力；此处与 Player Settings 一样由你输入 Alias。", MessageType.None);
    }

    private void DrawBuildSection()
    {
        EditorGUILayout.Space(12f);
        var validationError = _settings.Validate();
        using (new EditorGUI.DisabledScope(validationError != null))
        {
            if (GUILayout.Button("构建 " + (_settings.Format == AndroidBuildFormat.AAB ? "AAB" : "APK"), GUILayout.Height(32f)))
                Build();
        }

        if (validationError != null) EditorGUILayout.HelpBox(validationError, MessageType.Warning);
        if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, _statusType);
    }

    private void Build()
    {
        try
        {
            _settings.Save();
            var outputName = BuildName(_settings);
            var report = AndroidBuildService.Build(_settings, outputName);
            _statusType = MessageType.Info;
            _status = "构建成功\n" + report.summary.outputPath + "\n耗时：" + report.summary.totalTime;
            EditorUtility.RevealInFinder(report.summary.outputPath);
        }
        catch (Exception e)
        {
            _statusType = MessageType.Error;
            _status = e.Message;
            Debug.LogException(e);
        }
    }

    private void OnLostFocus()
    {
        _settings?.Save();
    }

    private static string BuildName(AndroidBuildSettings settings)
    {
        var productName = SanitizeFileName(settings.ProductName);
        var channel = settings.DevelopmentBuild ? "debug" : "release";
        var alias = SanitizeFileName(settings.KeyAlias);
        var date = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
        return productName + "-" + channel + "-" + alias + "-" + date;
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "android-build";
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value;
    }
}
