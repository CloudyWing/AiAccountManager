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

        await evaluator.EvaluateAsync([Snapshot(id, 25, reset)], true, 20, CancellationToken.None);
        await evaluator.EvaluateAsync([low], false, 20, CancellationToken.None);
        await evaluator.EvaluateAsync([low], true, 20, CancellationToken.None);
        await evaluator.EvaluateAsync([low], true, 20, CancellationToken.None);
        await evaluator.EvaluateAsync([Snapshot(id, 15, reset.AddDays(1))], true, 20, CancellationToken.None);

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

        await evaluator.EvaluateAsync([Snapshot(id, 15, null)], true, 20, CancellationToken.None);
        await evaluator.EvaluateAsync([Snapshot(id, null, null)], true, 20, CancellationToken.None);
        await evaluator.EvaluateAsync([Snapshot(id, 15, null)], true, 20, CancellationToken.None);
        await evaluator.EvaluateAsync([Snapshot(id, 20, null)], true, 20, CancellationToken.None);
        await evaluator.EvaluateAsync([Snapshot(id, 15, null)], true, 20, CancellationToken.None);

        Assert.That(sink.Messages, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task EvaluateAsync_SendFails_RetriesOnNextEvaluation() {
        FakeSink sink = new() { Succeed = false };
        FakeStore store = new();
        LowQuotaNotificationEvaluator evaluator = new(sink, store);
        NotificationSnapshot low = Snapshot(Guid.NewGuid(), 15, null);

        await evaluator.EvaluateAsync([low], true, 20, CancellationToken.None);
        sink.Succeed = true;
        await evaluator.EvaluateAsync([low], true, 20, CancellationToken.None);

        Assert.That(sink.Messages, Has.Count.EqualTo(2));
        Assert.That(store.Keys, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task EvaluateAsync_SaveFails_RetainsSentStateAndRetriesPersistence() {
        FakeSink sink = new();
        FakeStore store = new() { FailNextSave = true };
        LowQuotaNotificationEvaluator evaluator = new(sink, store);
        NotificationSnapshot low = Snapshot(Guid.NewGuid(), 15, null);

        await Assert.ThrowsAsync<IOException>(async () => await evaluator.EvaluateAsync([low], true, 20, CancellationToken.None));
        await evaluator.EvaluateAsync([low], true, 20, CancellationToken.None);

        Assert.That(sink.Messages, Has.Count.EqualTo(1));
        Assert.That(store.Keys, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task EvaluateAsync_MinuteJitterAndNextMinute_NotifiesOncePerCycle() {
        FakeSink sink = new();
        LowQuotaNotificationEvaluator evaluator = new(sink, new FakeStore());
        Guid id = Guid.NewGuid();
        DateTimeOffset reset = new(2026, 11, 1, 0, 0, 5, TimeSpan.Zero);
        await evaluator.EvaluateAsync([Snapshot(id, 10, reset)], true, 20, CancellationToken.None);
        await evaluator.EvaluateAsync([Snapshot(id, 10, reset.AddSeconds(40))], true, 20, CancellationToken.None);
        Assert.That(sink.Messages, Has.Count.EqualTo(1));
        await evaluator.EvaluateAsync([Snapshot(id, 10, reset.AddMinutes(1))], true, 20, CancellationToken.None);
        Assert.That(sink.Messages, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task EvaluateAsync_MultipleWindowsAndProviders_SelectsLowestAndMatchingProvider() {
        FakeSink sink = new();
        LowQuotaNotificationEvaluator evaluator = new(sink, new FakeStore());
        Guid id = Guid.NewGuid();
        NotificationSnapshot codex = new("codex", id, null, [new("five-hour", "5 小時", 12, null), new("weekly", "每週", 8, null)]);
        NotificationSnapshot claude = new("claude", id, null, [new("weekly", "每週", 5, null)]);
        IReadOnlyList<NotificationDecision> decisions = await evaluator.EvaluateAsync([codex, claude], true, 20, CancellationToken.None);
        Assert.That(sink.Messages, Has.Count.EqualTo(1));
        Assert.That(sink.Messages[0].Provider, Is.EqualTo("claude"));
        Assert.That(decisions.Count(x => x.Outcome == NotificationDecisionOutcome.Superseded), Is.EqualTo(2));
        Assert.That(decisions.Single(x => x.Sent).Provider, Is.EqualTo("claude"));
    }

    [Test]
    public async Task EvaluateAsync_Ties_PrefersCodexAndFiveHour() {
        FakeSink sink = new();
        LowQuotaNotificationEvaluator evaluator = new(sink, new FakeStore());
        Guid id = Guid.NewGuid();
        NotificationSnapshot claude = new("claude", id, null, [new("five-hour", "5 小時", 10, null)]);
        NotificationSnapshot codex = new("codex", id, null, [new("weekly", "每週", 10, null), new("five-hour", "5 小時", 10, null)]);
        IReadOnlyList<NotificationDecision> decisions = await evaluator.EvaluateAsync([claude, codex], true, 20, CancellationToken.None);
        Assert.That(sink.Messages, Has.Count.EqualTo(1));
        Assert.That(sink.Messages[0].Provider, Is.EqualTo("codex"));
        Assert.That(decisions.Single(x => x.Sent).WindowKey, Is.EqualTo("five-hour"));
    }

    [Test]
    public async Task EvaluateAsync_LowestAlreadySent_SelectsNextCandidate() {
        FakeSink sink = new();
        LowQuotaNotificationEvaluator evaluator = new(sink, new FakeStore());
        Guid id = Guid.NewGuid();
        NotificationSnapshot first = Snapshot(id, 5, null);
        await evaluator.EvaluateAsync([first], true, 20, CancellationToken.None);
        NotificationSnapshot second = new("claude", id, null, [new("weekly", "每週", 10, null)]);
        IReadOnlyList<NotificationDecision> decisions = await evaluator.EvaluateAsync([first, second], true, 20, CancellationToken.None);
        Assert.That(sink.Messages, Has.Count.EqualTo(2));
        Assert.That(sink.Messages[1].Provider, Is.EqualTo("claude"));
        Assert.That(decisions[0].Outcome, Is.EqualTo(NotificationDecisionOutcome.Deduplicated));
        Assert.That(decisions[0].DedupHit, Is.True);
    }

    [Test]
    public async Task EvaluateAsync_EmptyAndUnknown_DoesNotNotifyOrLoadEmpty() {
        FakeSink sink = new();
        FakeStore store = new();
        LowQuotaNotificationEvaluator evaluator = new(sink, store);
        Assert.That(await evaluator.EvaluateAsync([], true, 20, CancellationToken.None), Is.Empty);
        Assert.That(store.LoadCount, Is.Zero);
        Assert.That(await evaluator.EvaluateAsync([Snapshot(Guid.NewGuid(), null, null)], true, 20, CancellationToken.None), Is.Empty);
        Assert.That(sink.Messages, Is.Empty);
    }

    [Test]
    public async Task EvaluateAsync_LegacyKey_MigratesAndSavesWithoutResending() {
        Guid id = Guid.NewGuid();
        DateTimeOffset reset = new(2026, 11, 1, 0, 0, 25, TimeSpan.Zero);
        FakeStore store = new() { InitialKeys = [$"codex/{id:N}/five-hour/{reset:O}"] };
        FakeSink sink = new();
        LowQuotaNotificationEvaluator evaluator = new(sink, store);
        IReadOnlyList<NotificationDecision> decisions = await evaluator.EvaluateAsync([Snapshot(id, 10, reset)], true, 20, CancellationToken.None);
        Assert.That(sink.Messages, Is.Empty);
        Assert.That(store.Keys, Does.Contain(NotificationCycleKey.Create("codex", id, "five-hour", reset)));
        Assert.That(store.SaveCount, Is.EqualTo(1));
        Assert.That(decisions[0], Is.EqualTo(new NotificationDecision("codex", "five-hour", "2026-11-01T00:00Z", true, false, NotificationDecisionOutcome.Deduplicated)));
    }

    [Test]
    public void NotificationDecision_DoesNotExposeIdentityFields() {
        string[] names = typeof(NotificationDecision).GetProperties().Select(x => x.Name).ToArray();
        Assert.That(names, Does.Not.Contain("AccountId"));
        Assert.That(names, Does.Not.Contain("Alias"));
        Assert.That(names, Does.Not.Contain("Email"));
    }

    [Test]
    public async Task EvaluateAsync_LowQuota_SendsExactFormalTitleAndMessage() {
        FakeSink sink = new();
        LowQuotaNotificationEvaluator evaluator = new(sink, new FakeStore());
        Guid id = Guid.NewGuid();
        DateTimeOffset reset = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        await evaluator.EvaluateAsync([Snapshot(id, 12, reset, "備用帳號")], true, 20, CancellationToken.None);

        Assert.That(sink.Messages, Has.Count.EqualTo(1));
        NotificationRequest request = sink.Messages[0];
        using (Assert.EnterMultipleScope()) {
            Assert.That(request.Title, Is.EqualTo("額度偏低"));
            Assert.That(request.Message, Is.EqualTo("Codex 5 小時額度剩 12%，建議切換至 備用帳號"));
            Assert.That(request.Type, Is.EqualTo("low-quota"));
            Assert.That(request.Provider, Is.EqualTo("codex"));
            Assert.That(request.Activatable, Is.True);
        }
    }

    [Test]
    public async Task EvaluateAsync_LowQuotaWithoutRecommendation_SendsFractionalMessageWithoutSuffix() {
        FakeSink sink = new();
        LowQuotaNotificationEvaluator evaluator = new(sink, new FakeStore());
        Guid id = Guid.NewGuid();
        DateTimeOffset reset = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        await evaluator.EvaluateAsync([Snapshot(id, 12.5m, reset)], true, 20, CancellationToken.None);

        Assert.That(sink.Messages, Has.Count.EqualTo(1));
        Assert.That(sink.Messages[0].Message, Is.EqualTo("Codex 5 小時額度剩 12.5%"));
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
        public IReadOnlyCollection<string> InitialKeys { get; set; } = [];
        public int LoadCount { get; private set; }
        public int SaveCount { get; private set; }
        public bool FailNextSave { get; set; }
        public Task<IReadOnlyCollection<string>> LoadAsync(CancellationToken cancellationToken) {
            LoadCount++;
            return Task.FromResult(InitialKeys.Count > 0 ? InitialKeys : Keys);
        }
        public Task SaveAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken) {
            SaveCount++;
            if (FailNextSave) {
                FailNextSave = false;
                throw new IOException("synthetic save failure");
            }
            Keys = keys.ToArray();
            return Task.CompletedTask;
        }
    }
}
