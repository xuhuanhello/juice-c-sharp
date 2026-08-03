using DataChannelUnity;
using NUnit.Framework;

namespace DataChannelUnity.Tests
{
    public class RedactAndConfigTests
    {
        [Test]
        public void RedactIceCredentials_HidesUserInfo()
        {
            var input = "using turn:myuser:s3cret@turn.example.com:3478";
            var outp = DataChannelLog.RedactIceCredentials(input);
            Assert.That(outp, Does.Contain("credentials=redacted@"));
            Assert.That(outp, Does.Not.Contain("s3cret"));
            Assert.That(outp, Does.Not.Contain("myuser:"));
        }

        [Test]
        public void PeerConnectionConfig_Defaults()
        {
            var c = new PeerConnectionConfig();
            Assert.AreEqual(IceTransportPolicy.All, c.TransportPolicy);
            Assert.IsNotNull(c.IceServers);
            Assert.AreEqual(0, c.IceServers.Count);
        }

        [Test]
        public void DataChannelInit_DefaultsReliableOrdered()
        {
            var i = new DataChannelInit();
            Assert.IsTrue(i.Ordered);
            Assert.IsTrue(i.Reliable);
        }

        [Test]
        public void IceServer_CtorWithUrl()
        {
            var s = new IceServer("stun:stun.l.google.com:19302");
            Assert.AreEqual(1, s.Urls.Count);
            Assert.AreEqual("stun:stun.l.google.com:19302", s.Urls[0]);
        }
    }
}
