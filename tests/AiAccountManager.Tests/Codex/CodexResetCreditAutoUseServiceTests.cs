using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Notifications;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Windows.Codex;
using CloudyWing.AiAccountManager.Windows.Storage;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Codex;

[TestFixture]
public sealed class CodexResetCreditAutoUseServiceTests {
    [Test]
    public async Task DisabledDoesNotConsume() {
        Fixture fixture = await Fixture.CreateAsync(false, TimeSpan.FromMinutes(30));
        using CodexResetCreditAutoUseService service = fixture.CreateService();
        await service.StartAsync();
        await fixture.Delay.WaitForTimerAsync();
        Assert.That(fixture.Context.Session.ConsumeCount, Is.Zero);
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task DisabledAfterPendingIsSavedCancelsBeforeConsume() {
        Fixture fixture = await Fixture.CreateAsync(true, TimeSpan.FromMinutes(30));
        fixture.Repository.ResumeBegin = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CodexResetCreditAutoUseService service = fixture.CreateService();
        await service.StartAsync();
        await fixture.Repository.BeginCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Settings.SaveAsync(fixture.Settings.Settings! with { AutoUseResetCredits = false }, CancellationToken.None);
        fixture.Repository.ResumeBegin.SetResult(true);
        await fixture.Repository.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(fixture.Context.Session.ConsumeCount, Is.Zero);
        Assert.That(fixture.Repository.Items.Single().Result, Is.EqualTo("cancelled"));
        await service.StopAsync(CancellationToken.None);
    }
    [Test]
    public async Task DisabledDuringConsumeRecordsUnconfirmedAndDoesNotRetry() {
        Fixture fixture = await Fixture.CreateAsync(true, TimeSpan.FromMinutes(30));
        TaskCompletionSource<bool> consumeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Context.Session.ConsumeInProgress = async cancellationToken => {
            consumeStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        using CodexResetCreditAutoUseService service = fixture.CreateService();
        await service.StartAsync();
        await consumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(fixture.Repository.Items.Single().Result, Is.EqualTo("pending"));

        await fixture.Settings.SaveAsync(fixture.Settings.Settings! with { AutoUseResetCredits = false }, CancellationToken.None);
        await fixture.Repository.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Settings.SaveAsync(fixture.Settings.Settings! with { AutoUseResetCredits = true }, CancellationToken.None);
        await fixture.Delay.WaitForTimerAsync();

        Assert.That(fixture.Repository.Items.Single().Result, Is.EqualTo("unconfirmed"));
        Assert.That(fixture.Context.Session.ConsumeCount, Is.EqualTo(1));
        await service.StopAsync(CancellationToken.None);
    }

    [TestCase("reset")]
    [TestCase("nothingToReset")]
    [TestCase("noCredit")]
    [TestCase("alreadyRedeemed")]
    public async Task UsesAtDeadlineOnceAndPersistsOutcomeAcrossRestart(string outcome) {
        Fixture fixture = await Fixture.CreateAsync(true, TimeSpan.FromMinutes(31));
        fixture.Context.Session.ConsumeOutcome = outcome;
        fixture.Context.Session.BeforeConsume = () => Assert.That(fixture.Repository.Items.Single().Result, Is.EqualTo("pending"));
        using (CodexResetCreditAutoUseService service = fixture.CreateService()) {
            await service.StartAsync();
            await fixture.Delay.WaitForTimerAsync();
            Assert.That(fixture.Context.Session.ConsumeCount, Is.Zero);
            fixture.Context.Clock.UtcNow += TimeSpan.FromMinutes(1);
            fixture.Delay.Release();
            await fixture.Repository.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(fixture.Context.Session.ConsumeCount, Is.EqualTo(1));
            Assert.That(fixture.Context.Session.ConsumedCreditId, Is.EqualTo("credit-1"));
            Assert.That(Guid.TryParse(fixture.Context.Session.IdempotencyKey, out _), Is.True);
            Assert.That(fixture.Repository.Items.Single().Result, Is.EqualTo(outcome));
            await service.StopAsync(CancellationToken.None);
        }
        fixture.Delay = new ControlledDelay();
        using CodexResetCreditAutoUseService restarted = fixture.CreateService();
        await restarted.StartAsync();
        await fixture.Delay.WaitForTimerAsync();
        Assert.That(fixture.Context.Session.ConsumeCount, Is.EqualTo(1));
        await restarted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task CompletedAttemptSendsCodexNotification() {
        Fixture fixture = await Fixture.CreateAsync(true, TimeSpan.FromMinutes(30));
        fixture.Context.Session.ConsumeOutcome = "reset";
        using CodexResetCreditAutoUseService service = fixture.CreateService();
        await service.StartAsync();
        NotificationRequest request = await fixture.Notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(request.Provider, Is.EqualTo("codex"));
        Assert.That(request.Message, Does.Contain("重置成功"));
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task CreditWithoutExpiryIsNeverConsumed() {
        Fixture fixture = await Fixture.CreateAsync(true, null);
        using CodexResetCreditAutoUseService service = fixture.CreateService();
        await service.StartAsync();
        await fixture.Context.Session.AccountReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Delay.WaitForTimerAsync();
        Assert.That(fixture.Context.Session.ConsumeCount, Is.Zero);
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task FailedPersistenceDoesNotSendRequest() {
        Fixture fixture = await Fixture.CreateAsync(true, TimeSpan.FromMinutes(30));
        fixture.Repository.FailBegin = true;
        using CodexResetCreditAutoUseService service = fixture.CreateService();
        await service.StartAsync();
        await fixture.Repository.BeginCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(fixture.Context.Session.ConsumeCount, Is.Zero);
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task RpcFailureIsRecordedAndNotRetried() {
        Fixture fixture = await Fixture.CreateAsync(true, TimeSpan.FromMinutes(30));
        fixture.Context.Session.ConsumeFailure = new CodexAppServerRpcException(503, unauthorized: false);
        using CodexResetCreditAutoUseService service = fixture.CreateService();
        await service.StartAsync();
        await fixture.Repository.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(fixture.Repository.Items.Single().Result, Is.EqualTo("error"));
        Assert.That(fixture.Context.Session.ConsumeCount, Is.EqualTo(1));
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task JsonRepositoryPreservesPendingAndDeduplicatesAfterReopening() {
        string root = Path.Combine(Path.GetTempPath(), "AiAccountManager.Tests", Guid.NewGuid().ToString("N"));
        try {
            AtomicJsonFileStore store = new(new CurrentUserDataDirectory(root));
            JsonCodexResetCreditAttemptRepository first = new(store);
            CodexResetCreditAttempt attempt = new(Guid.NewGuid(), "credit-1", DateTimeOffset.UtcNow, Guid.NewGuid().ToString(), "pending");
            Assert.That(await first.TryBeginAsync(attempt, CancellationToken.None), Is.True);
            JsonCodexResetCreditAttemptRepository reopened = new(store);
            Assert.That(await reopened.TryBeginAsync(attempt, CancellationToken.None), Is.False);
            Assert.That(await reopened.LoadAsync(CancellationToken.None), Is.EqualTo(new[] { attempt }));
            await reopened.CompleteAsync(attempt with { Result = "nothingToReset" }, CancellationToken.None);
            Assert.That((await first.LoadAsync(CancellationToken.None)).Single().Result, Is.EqualTo("nothingToReset"));
        } finally {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    private sealed class Fixture {
        private Fixture(QuotaTestContext context, SettingsService settings) { Context = context; Settings = settings; }
        public QuotaTestContext Context { get; }
        public SettingsService Settings { get; }
        public MemoryRepository Repository { get; } = new();
        public ControlledDelay Delay { get; set; } = new();
        public RecordingSink Notifications { get; } = new();

        public static async Task<Fixture> CreateAsync(bool enabled, TimeSpan? expiry) {
            QuotaTestContext context = new();
            long? expiresAt = expiry.HasValue ? context.Clock.UtcNow.Add(expiry.Value).ToUnixTimeSeconds() : null;
            context.Session.RateLimits = JsonSerializer.SerializeToElement(new {
                accountId = "chatgpt-account-123",
                rateLimits = new {
                    primary = new { usedPercent = 20, windowDurationMins = 300 },
                    secondary = new { usedPercent = 35, windowDurationMins = 10080 }
                },
                rateLimitResetCredits = new { availableCount = 1, credits = new[] {
                    new { id = "credit-1", resetType = "codexRateLimits", status = "available",
                        grantedAt = context.Clock.UtcNow.ToUnixTimeSeconds(), expiresAt, title = "券", description = (string?)null }
                } }
            });
            SettingsService settings = new(context.SettingsRepository, context.Catalog);
            await settings.InitializeAsync(CancellationToken.None);
            await settings.SaveAsync(settings.Settings! with { AutoUseResetCredits = enabled, QuotaRefreshIntervalMinutes = 30 }, CancellationToken.None);
            return new Fixture(context, settings);
        }

        public CodexResetCreditAutoUseService CreateService()
            => new(Settings, Context.Service, Repository, Delay, Context.Clock, Notifications);
    }

    private sealed class RecordingSink : INotificationSink {
        public event Action<string>? Activated { add { } remove { } }
        public TaskCompletionSource<NotificationRequest> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> SendAsync(NotificationRequest request, CancellationToken cancellationToken) {
            Sent.TrySetResult(request);
            return Task.FromResult(true);
        }
    }

    private sealed class MemoryRepository : ICodexResetCreditAttemptRepository {
        public List<CodexResetCreditAttempt> Items { get; } = [];
        public TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> BeginCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailBegin { get; set; }
        public TaskCompletionSource<bool>? ResumeBegin { get; set; }
        public Task<IReadOnlyList<CodexResetCreditAttempt>> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CodexResetCreditAttempt>>(Items.ToArray());
        public async Task<bool> TryBeginAsync(CodexResetCreditAttempt attempt, CancellationToken cancellationToken) {
            if (FailBegin) { BeginCalled.TrySetResult(true); }
            if (FailBegin) { throw new IOException("保存失敗"); }
            if (Items.Any(item => item.AccountId == attempt.AccountId && item.CreditId == attempt.CreditId)) { return false; }
            Items.Add(attempt);
            BeginCalled.TrySetResult(true);
            if (ResumeBegin is not null) { await ResumeBegin.Task; }
            return true;
        }
        public Task CompleteAsync(CodexResetCreditAttempt attempt, CancellationToken cancellationToken) {
            Items[Items.FindIndex(item => item.AccountId == attempt.AccountId && item.CreditId == attempt.CreditId)] = attempt;
            Completed.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    private sealed class ControlledDelay : ICodexResetCreditDelay {
        private readonly TaskCompletionSource<bool> waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) {
            waiting.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken);
        }
        public Task WaitForTimerAsync() => waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void Release() => release.TrySetResult(true);
    }
}
