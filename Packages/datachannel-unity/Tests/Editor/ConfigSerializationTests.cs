using System;
using NUnit.Framework;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// 托管档：纯 C#，零 P/Invoke。
    ///
    /// 守的是 #34 决议 1 —— 一个**看起来像疏漏、实际是安全取舍**的决定，
    /// 正是最容易被后人「顺手修好」的那类。
    /// </summary>
    public sealed class ConfigSerializationTests
    {
        [TestCase(typeof(IceServer))]
        [TestCase(typeof(PeerConnectionConfig))]
        [TestCase(typeof(DataChannelInit))]
        public void ConfigTypes_MustNotBeSerializable(Type type)
        {
            Assert.IsNull(
                Attribute.GetCustomAttribute(type, typeof(SerializableAttribute)),
                type.Name + " must not be marked [Serializable]. This is not an oversight: once it can be filled in from the Inspector, "
                + "IceServer.Username / Credential end up in .unity / .prefab files, ship inside the build, and are trivially extractable. "
                + "TURN credentials should be issued at runtime by the signalling server. See docs/SPEC.md section 5 and issue #34.");
        }
    }
}
