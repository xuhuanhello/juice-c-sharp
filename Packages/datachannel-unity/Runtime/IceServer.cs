using System.Collections.Generic;

namespace DataChannelUnity
{
    /// <summary>
    /// STUN/TURN 端点描述。包只接受 ICE 服务器配置，不运行 STUN/TURN 服务。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 凭证以**结构化字段**传给后端，包不会把它们拼进任何 URL 字符串。
    /// </para>
    /// <para>
    /// 本类刻意**没有** <c>[Serializable]</c>，这是安全取舍而非疏漏：
    /// 一旦可在 Inspector 里填写，<see cref="Username"/> / <see cref="Credential"/>
    /// 就会进 <c>.unity</c> / <c>.prefab</c>，**随构建产物发出去且可被轻易提取**。
    /// TURN 凭证的正确来源是信令服务器运行时下发（常见是短期 REST 凭证）。
    /// 确实需要 Inspector 配置的应用应自写 DTO —— 那一步恰好逼人想清楚凭证从哪来。
    /// 详见 <c>docs/SPEC.md</c> §5。
    /// </para>
    /// </remarks>
    public sealed class IceServer
    {
        /// <summary>STUN/TURN URL。应用也可自行在此内嵌凭证；日志会对该形态脱敏。</summary>
        public List<string> Urls { get; set; } = new List<string>();

        /// <summary>TURN 用户名，可选。</summary>
        public string Username { get; set; }

        /// <summary>TURN 凭证，可选。</summary>
        public string Credential { get; set; }

        public IceServer() { }

        public IceServer(string url, string username = null, string credential = null)
        {
            if (!string.IsNullOrEmpty(url))
                Urls.Add(url);
            Username = username;
            Credential = credential;
        }

        public IceServer(IEnumerable<string> urls, string username = null, string credential = null)
        {
            if (urls != null)
                Urls.AddRange(urls);
            Username = username;
            Credential = credential;
        }
    }
}
