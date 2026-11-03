using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using CloudyWing.AiAccountManager.Windows.Runtime;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class ActivationPipeTests {
    [Test]
    public async Task Server_StreamCreationFails_RetriesAndAcceptsNextRequest() {
        string name = "activation-test-" + Guid.NewGuid().ToString("N");
        int attempts = 0;
        int activations = 0;
        TaskCompletionSource<bool> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> recreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ActivationPipeServer server = new(name, () => Interlocked.Increment(ref activations), () => {
            int attempt = Interlocked.Increment(ref attempts);
            if (attempt is 2 or 3) {
                failed.TrySetResult(true);
                throw new IOException("Simulated pipe creation failure.");
            }
            NamedPipeServerStream stream = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            if (attempt > 3) {
                recreated.TrySetResult(true);
            }
            return stream;
        });
        server.Start();

        ActivationAttempt first = ActivationPipeClient.TrySend(name, TimeSpan.FromSeconds(2));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await recreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ActivationAttempt second = ActivationPipeClient.TrySend(name, TimeSpan.FromSeconds(2));

        Assert.Multiple(() => {
            Assert.That(first.Outcome, Is.EqualTo(ActivationOutcome.Accepted));
            Assert.That(second.Outcome, Is.EqualTo(ActivationOutcome.Accepted));
            Assert.That(activations, Is.EqualTo(2));
            Assert.That(attempts, Is.GreaterThanOrEqualTo(4));
        });
    }

    [Test]
    public void Dispose_FaultedListener_DoesNotThrow() {
        ActivationPipeServer server = new("activation-test-" + Guid.NewGuid().ToString("N"), () => { });
        FieldInfo listener = typeof(ActivationPipeServer).GetField("listener", BindingFlags.Instance | BindingFlags.NonPublic)!;
        listener.SetValue(server, Task.FromException(new IOException("Simulated listener failure.")));

        Assert.DoesNotThrow(server.Dispose);
    }

    [Test]
    public void TrySend_RunningServer_ReturnsProcessId() {
        string name = "activation-test-" + Guid.NewGuid().ToString("N");
        int count = 0;
        using ActivationPipeServer server = new(name, () => Interlocked.Increment(ref count));
        server.Start();

        ActivationAttempt result = ActivationPipeClient.TrySend(name, TimeSpan.FromSeconds(2));

        Assert.Multiple(() => {
            Assert.That(result.Outcome, Is.EqualTo(ActivationOutcome.Accepted));
            Assert.That(result.ServerProcessId, Is.EqualTo(Environment.ProcessId));
            Assert.That(count, Is.EqualTo(1));
        });
    }

    [Test]
    public void TrySend_NoServer_ReturnsNoResponse() {
        ActivationAttempt result = ActivationPipeClient.TrySend("activation-test-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(100));

        Assert.That(result.Outcome, Is.EqualTo(ActivationOutcome.NoResponse));
    }

    [Test]
    public void Server_AccessControl_RestrictsUserAndDeniesNetwork() {
        string name = "activation-test-" + Guid.NewGuid().ToString("N");
        using ActivationPipeServer server = new(name, () => { });
        server.Start();
        using NamedPipeClientStream client = new(".", name, PipeDirection.InOut);
        client.Connect(2000);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        AuthorizationRuleCollection rules = client.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier));

        Assert.Multiple(() => {
            Assert.That(rules.Count, Is.EqualTo(2));
            Assert.That(rules.Cast<PipeAccessRule>().Any(rule => rule.IdentityReference.Equals(identity.User) && rule.AccessControlType == AccessControlType.Allow && (rule.PipeAccessRights & (PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance)) == (PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance)), Is.True);
            Assert.That(rules.Cast<PipeAccessRule>().Any(rule => rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)) && rule.AccessControlType == AccessControlType.Deny), Is.True);
        });
    }
}
