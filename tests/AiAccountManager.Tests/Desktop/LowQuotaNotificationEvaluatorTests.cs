using CloudyWing.AiAccountManager.Application.Notifications;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class LowQuotaNotificationEvaluatorTests {
    [Test]
    public async Task EvaluateAsync_LowQuotaAndNewPeriod_NotifiesOncePerPeriod() {
        FakeSink sink = new();
        FakeStore store = new();
        LowQuotaNotificationEvaluator evaluator = new(sink, store);
        Guid id = Guid.NewGuid();
        DateTimeOffset reset = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        NotificationSnapshot low = Snapshot(id, 15, reset, "備用帳號");

        await evaluator.EvaluateAsync(Snapshot(id, 25, reset), true, 20, CancellationToken.None);
        await evaluator.EvaluateAsync(low, false, 20, CancellationToken.None);
        await evaluator.EvaluateAsync(low, true, 20, CancellationToken.None);
        await evaluator.EvaluateAsync(low, true, 20, CancellationToken.None);
        await evaluator.EvaluateAsync(Snapshot(id, 15, reset.AddDays(1)), true, 20, CancellationToken.None);

        Assert.That(sink.Messages, Has.Count.EqualTo(2));
        Assert.That(sink.Messages[0].Message, Does.Contain("建議切換至 備用帳號"));
        Assert.That(store.Keys, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task EvaluateAsync_NoReset_RearmsOnlyAfterValidRecovery() {
        FakeSink sink = new();
        FakeStore store = new();
        LowQuotaNotificationEvaluator evaluator = new(sink, store);
        Guid id = Guid.NewGuid();

        await evaluator.EvaluateAsync(Snapshot(id, 15, null), true, 20, CancellationToken.None);
        await evaluator.EvaluateAsync(Snapshot(id, null, null), true, 20, CancellationToken.None);
        await evaluator.EvaluateAsync(Snapshot(id, 15, null), true, 20, CancellationToken.None);
        await evaluator.EvaluateAsync(Snapshot(id, 20, null), true, 20, CancellationToken.None);
        await evaluator.EvaluateAsync(Snapshot(id, 15, null), true, 20, CancellationToken.None);

        Assert.That(sink.Messages, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task EvaluateAsync_SendFails_RetriesOnNextEvaluation() {
        FakeSink sink = new() { Succeed = false };
        FakeStore store = new();
        LowQuotaNotificationEvaluator evaluator = new(sink, store);
        NotificationSnapshot low = Snapshot(Guid.NewGuid(), 15, null);

        await evaluator.EvaluateAsync(low, true, 20, CancellationToken.None);
        sink.Succeed = true;
        await evaluator.EvaluateAsync(low, true, 20, CancellationToken.None);

        Assert.That(sink.Messages, Has.Count.EqualTo(2));
        Assert.That(store.Keys, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task EvaluateAsync_SaveFails_RetainsSentStateAndRetriesPersistence() {
        FakeSink sink = new();
        FakeStore store = new() { FailNextSave = true };
        LowQuotaNotificationEvaluator evaluator = new(sink, store);
        NotificationSnapshot low = Snapshot(Guid.NewGuid(), 15, null);

        await Assert.ThrowsAsync<IOException>(async () => await evaluator.EvaluateAsync(low, true, 20, CancellationToken.None));
        await evaluator.EvaluateAsync(low, true, 20, CancellationToken.None);

        Assert.That(sink.Messages, Has.Count.EqualTo(1));
        Assert.That(store.Keys, Has.Count.EqualTo(1));
    }

    private static NotificationSnapshot Snapshot(Guid id, decimal? remaining, DateTimeOffset? reset, string? recommendation = null) {
        return new("codex", id, recommendation, [new("five-hour", "5 小時", remaining, reset)]);
    }

    private sealed class FakeSink : INotificationSink {
        public event Action<string>? Activated { add { } remove { } }
        public bool Succeed { get; set; } = true;
        public List<NotificationRequest> Messages { get; } = [];
        public Task<bool> SendAsync(NotificationRequest request, CancellationToken cancellationToken) {
            Messages.Add(request);
            return Task.FromResult(Succeed);
        }
    }

    private sealed class FakeStore : INotificationDedupStore {
        public IReadOnlyCollection<string> Keys { get; private set; } = [];
        public bool FailNextSave { get; set; }
        public Task<IReadOnlyCollection<string>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Keys);
        public Task SaveAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken) {
            if (FailNextSave) {
                FailNextSave = false;
                throw new IOException("synthetic save failure");
            }
            Keys = keys.ToArray();
            return Task.CompletedTask;
        }
    }
}
