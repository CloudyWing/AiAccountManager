using CloudyWing.AiAccountManager.Windows.Runtime;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
[NonParallelizable]
public sealed class SingleInstanceNegotiatorTests {
    [Test]
    public void Negotiate_MutexAvailableAfterRetry_BecomesPrimary() {
        int calls = 0;
        CancellationTokenSource instance = new();
        NegotiationResult result = SingleInstanceNegotiator.Negotiate(
            () => ++calls == 1 ? null : instance,
            _ => new ActivationAttempt(ActivationOutcome.NoResponse, 0),
            TimeSpan.FromSeconds(2), TimeSpan.Zero, TimeSpan.Zero, _ => { }
        );
        using (result.Instance) {
            Assert.That(result.Outcome, Is.EqualTo(NegotiationOutcome.BecamePrimary));
            Assert.That(result.Instance, Is.SameAs(instance));
            Assert.That(calls, Is.EqualTo(2));
        }
    }

    [Test]
    public void Negotiate_ServerAccepts_ReturnsSignaled() {
        NegotiationResult result = SingleInstanceNegotiator.Negotiate(
            () => null, _ => new ActivationAttempt(ActivationOutcome.Accepted, 123),
            TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.Zero, _ => { }
        );

        Assert.That(result, Is.EqualTo(new NegotiationResult(NegotiationOutcome.Signaled, null, 123)));
    }

    [Test]
    public void Negotiate_NoResponse_ReturnsTimedOut() {
        NegotiationResult result = SingleInstanceNegotiator.Negotiate(
            () => null, _ => new ActivationAttempt(ActivationOutcome.NoResponse, 0),
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, _ => { }
        );

        Assert.That(result.Outcome, Is.EqualTo(NegotiationOutcome.TimedOut));
    }
}
