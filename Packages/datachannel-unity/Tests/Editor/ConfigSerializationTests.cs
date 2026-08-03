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
                type.Name + " 不得标 [Serializable]。这不是疏漏：一旦可在 Inspector 里填写，"
                + "IceServer.Username / Credential 就会进 .unity / .prefab，随构建产物发出去且可被轻易提取。"
                + "TURN 凭证应由信令服务器运行时下发。见 docs/SPEC.md §5 与 issue #34。");
        }
    }
}
