using System;
using System.Collections.Generic;

namespace DataChannelUnity
{
    /// <summary>
    /// STUN/TURN endpoint description. The package builds backend URIs; it does not run STUN/TURN services.
    /// </summary>
    [Serializable]
    public sealed class IceServer
    {
        public List<string> Urls { get; set; } = new List<string>();
        public string Username { get; set; }
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
