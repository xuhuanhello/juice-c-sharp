using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// 信令服务器地址。**客户端唯一需要的配置，而且它不是秘密**
    /// （<a href="https://github.com/xuhuanhello/juice-c-sharp/issues/117">#117</a>：
    /// TURN 凭据由信令服务器签发下发，客户端一个秘密都不持有）。
    ///
    /// 文件在 `Assets/Resources/SignalingConfig.json`，**不入库** —— 它是每套部署各
    /// 一份的值，不是仓库的一部分。入库的是同目录外的
    /// `SignalingConfig.example.json`。
    ///
    /// ## 为什么放 Resources 而不是 StreamingAssets
    ///
    /// 要在 Editor、Android、iOS 上行为一致。`Resources.Load` 三处都是同步、同一种
    /// 写法；StreamingAssets 在 Android 上位于 APK 内部，必须走 `UnityWebRequest`
    /// 异步读，于是配置读取会分叉出一条平台分支 —— 为一个只有一行的配置不值得。
    ///
    /// 代价：值被烘进 build，改地址要重新构建。示例可以接受。
    /// </summary>
    [System.Serializable]
    public sealed class SignalingConfig
    {
        /// <summary>`wss://…`。JSON 字段名必须与此一致（JsonUtility 按字段名匹配）。</summary>
        public string signalingUrl;

        private const string ResourceName = "SignalingConfig";
        private const string ExpectedPath = "Assets/Resources/" + ResourceName + ".json";

        /// <summary>
        /// 读配置。**缺文件或缺值时抛，不给默认值** —— CONTRIBUTING 的第一原则是
        /// 「让缺失变成失败，而不是沉默」。这里退回一个内置地址的话，症状会变成
        /// 「连到了别人的服务器」或「莫名连不上」，而两者都比一条明确的异常难查得多。
        /// </summary>
        public static SignalingConfig Load()
        {
            var asset = Resources.Load<TextAsset>(ResourceName);
            if (asset == null)
                throw new System.IO.FileNotFoundException(
                    $"找不到 {ExpectedPath}。它不入库（每套部署一份），需要自己创建：" +
                    $"把 Assets/DataChannelUnity.Example/SignalingConfig.example.json " +
                    $"复制过去并填上你的 wss 地址。");

            var cfg = JsonUtility.FromJson<SignalingConfig>(asset.text);
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.signalingUrl))
                throw new System.InvalidOperationException(
                    $"{ExpectedPath} 里没有 signalingUrl。字段名必须逐字是 " +
                    $"\"signalingUrl\" —— JsonUtility 按字段名匹配，拼错不会报错，只会得到 null。");

            // wss 之外一律拦下。#116 选 wss 是为了买断 Android cleartext 与 iOS ATS
            // 的不确定性；配成 ws:// 在 Editor 里能跑、到真机上才炸，那是最坏的时机。
            if (!cfg.signalingUrl.StartsWith("wss://", System.StringComparison.OrdinalIgnoreCase))
                Debug.LogWarning(
                    $"[SignalingConfig] signalingUrl 不是 wss://（当前 {cfg.signalingUrl}）。" +
                    "Editor 里也许能连，但 Android 的 cleartext 策略与 iOS ATS 可能在真机上拦掉它 —— " +
                    "#116 选 wss 正是为了不必回答这个问题。");

            return cfg;
        }
    }
}
