using CloudyWing.AiAccountManager.Desktop;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class ActivationRelayTests {
    [Test]
    public void Attach_MultiplePendingRequests_DeliversOnce() {
        ActivationRelay relay = new();
        int count = 0;
        relay.Request();
        relay.Request();

        relay.Attach(() => count++);

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void Detach_PendingRequest_ClearsPending() {
        ActivationRelay relay = new();
        int count = 0;
        relay.Request();
        relay.Detach();

        relay.Attach(() => count++);

        Assert.That(count, Is.Zero);
    }
}
