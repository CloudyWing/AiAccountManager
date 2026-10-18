using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Operations;
using CloudyWing.AiAccountManager.Application.Runtime;
using CloudyWing.AiAccountManager.Application.Security;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Windows.Storage;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Codex;

[TestFixture]
public sealed class CodexQuotaServiceTests {
    [Test]
    public void CreditDialogDisplaysFixedUtcAsTaiwanTime() {
        QuotaWindow window = new(300, 80, DateTimeOffset.Parse("2026-10-28T06:29:38Z"));
        System.Reflection.MethodInfo describe = typeof(CloudyWing.AiAccountManager.Desktop.Components.Codex.ResetCreditDialog)
            .GetMethod("Describe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        string text = (string)describe.Invoke(null, [window])!;

        Assert.That(text, Does.Contain("10/28 14:29"));
    }
    [Test]
    public async Task ConsumeUsesOneKeyAndSelectedCredit() {
        QuotaTestContext context = new();
        context.Session.ConsumeOutcome = "noCredit";

        string outcome = await context.Service.ConsumeResetCreditAsync(
            context.Account.AccountId, context.Account.ProfileRevision, "credit-1", CancellationToken.None);

        Assert.That(outcome, Is.EqualTo("noCredit"));
        Assert.That(context.Session.ConsumeCount, Is.EqualTo(1));
        Assert.That(context.Session.ConsumedCreditId, Is.EqualTo("credit-1"));
        Assert.That(Guid.TryParse(context.Session.IdempotencyKey, out _), Is.True);
        Assert.That(context.Session.RateLimitReadCount, Is.Zero);
    }

    [Test]
    public async Task ConsumeRejectsChangedProfileBeforeCreatingSession() {
        QuotaTestContext context = new();

        string outcome = await context.Service.ConsumeResetCreditAsync(
            context.Account.AccountId, context.Account.ProfileRevision + 1, null, CancellationToken.None);

        Assert.That(outcome, Is.EqualTo("profileChanged"));
        Assert.That(context.Factory.CreateCount, Is.Zero);
    }

    [Test]
    public async Task SuccessfulConsumeRefreshesQuotaOnce() {
        QuotaTestContext context = new();

        string outcome = await context.Service.ConsumeResetCreditAsync(
            context.Account.AccountId, context.Account.ProfileRevision, null, CancellationToken.None);

        Assert.That(outcome, Is.EqualTo("reset"));
        Assert.That(context.Session.ConsumeCount, Is.EqualTo(1));
        Assert.That(context.Session.ConsumedCreditId, Is.Null);
        Assert.That(context.Session.RateLimitReadCount, Is.EqualTo(1));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task SuccessfulConsumeBypassesBackoffAndReportsRefreshFailure(bool automatic, bool refreshFails) {
        QuotaTestContext context = new();
        await context.SeedQuotaAsync(context.CreateSnapshot(context.Clock.UtcNow));
        context.Session.RateFailure = new TimeoutException();
        await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);
        int readsBeforeConsume = context.Session.RateLimitReadCount;
        context.Session.RateFailure = refreshFails ? new TimeoutException() : null;

        string outcome = automatic
            ? await context.Service.ConsumeResetCreditAutomaticallyAsync(
                context.Account.AccountId, context.Account.ProfileRevision, "credit", Guid.NewGuid().ToString(),
                _ => Task.FromResult(true), CancellationToken.None
            )
            : await context.Service.ConsumeResetCreditAsync(
                context.Account.AccountId, context.Account.ProfileRevision, null, CancellationToken.None
            );
        QuotaSnapshotView view = await context.Service.GetViewAsync(context.Account.AccountId, CancellationToken.None);

        Assert.That(context.Session.RateLimitReadCount, Is.EqualTo(readsBeforeConsume + 1));
        Assert.That(outcome, Is.EqualTo(refreshFails ? "resetRefreshFailed" : "reset"));
        Assert.That(view.Status, refreshFails ? Is.Not.EqualTo(QuotaDisplayStatus.Fresh) : Is.EqualTo(QuotaDisplayStatus.Fresh));
    }
    [TestCase(1, "reset", 1)]
    [TestCase(2, "resetRefreshFailed", 0)]
    public async Task ResetRefreshRetriesSavingAfterConflict(int conflicts, string expected, int reads) {
        QuotaTestContext context = new();
        await context.SeedQuotaAsync(context.CreateSnapshot(context.Clock.UtcNow));
        context.SnapshotRepository.ConflictsRemaining = conflicts;

        string outcome = await context.Service.ConsumeResetCreditAsync(
            context.Account.AccountId, context.Account.ProfileRevision, null, CancellationToken.None
        );

        Assert.That(outcome, Is.EqualTo(expected));
        Assert.That(context.Session.RateLimitReadCount, Is.EqualTo(reads));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Consume_WhenSuccessfulSnapshotSaveConflicts_ReportsRefreshFailure(bool automatic) {
        QuotaTestContext context = new();
        await context.SeedQuotaAsync(context.CreateSnapshot(context.Clock.UtcNow));
        context.SnapshotRepository.ConflictSuccessfulSave = true;

        string outcome = automatic
            ? await context.Service.ConsumeResetCreditAutomaticallyAsync(
                context.Account.AccountId, context.Account.ProfileRevision, "credit", Guid.NewGuid().ToString(),
                _ => Task.FromResult(true), CancellationToken.None
            )
            : await context.Service.ConsumeResetCreditAsync(
                context.Account.AccountId, context.Account.ProfileRevision, null, CancellationToken.None
            );

        Assert.That(context.Session.RateLimitReadCount, Is.EqualTo(1));
        Assert.That(outcome, Is.EqualTo("resetRefreshFailed"));
        Assert.That((await context.Service.GetViewAsync(context.Account.AccountId, CancellationToken.None)).Status,
            Is.EqualTo(QuotaDisplayStatus.Fresh));
    }

    [Test]
    public async Task Consume_WhenRefreshFailsAndFailureSaveConflicts_ReportsRefreshFailure() {
        QuotaTestContext context = new();
        await context.SeedQuotaAsync(context.CreateSnapshot(context.Clock.UtcNow));
        context.Session.RateFailure = new TimeoutException();
        context.SnapshotRepository.ConflictFailedSave = true;

        string outcome = await context.Service.ConsumeResetCreditAsync(
            context.Account.AccountId, context.Account.ProfileRevision, null, CancellationToken.None
        );

        Assert.That(context.Session.RateLimitReadCount, Is.EqualTo(1));
        Assert.That(outcome, Is.EqualTo("resetRefreshFailed"));
        Assert.That((await context.Service.GetViewAsync(context.Account.AccountId, CancellationToken.None)).Status,
            Is.EqualTo(QuotaDisplayStatus.Fresh));
    }

    [Test]
    public async Task Refresh_WhenSuccessfulSnapshotSaveConflicts_ReturnsCurrentView() {
        QuotaTestContext context = new();
        await context.SeedQuotaAsync(context.CreateSnapshot(context.Clock.UtcNow));
        context.SnapshotRepository.ConflictSuccessfulSave = true;

        QuotaSnapshotView view = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);

        Assert.That(view.Status, Is.EqualTo(QuotaDisplayStatus.Refreshing));
    }

    [Test]
    public async Task AuthenticatedChatGptWithRequiresOpenaiAuthTrueReadsQuota() {
        QuotaTestContext context = new();
        context.Session.Account = context.Session.Account with { RequiresOpenaiAuth = true };

        QuotaSnapshotView view = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);

        Assert.That(view.Status, Is.EqualTo(QuotaDisplayStatus.Fresh));
        Assert.That(view.Snapshot?.LastSuccessfulSnapshot?.FiveHour.RemainingPercent, Is.EqualTo(80));
        Assert.That(view.Snapshot?.LastSuccessfulSnapshot?.Weekly.RemainingPercent, Is.EqualTo(65));
        Assert.That(view.Snapshot?.LastAttempt?.Status, Is.Not.EqualTo(QuotaRefreshStatus.ReauthenticationRequired));
        Assert.That(context.Session.RateLimitReadCount, Is.EqualTo(1));
    }

    [Test]
    public async Task IncompleteResponsePreservesWholeLastSuccessfulSnapshot() {
        QuotaTestContext context = new();
        QuotaSuccessfulSnapshot previous = context.CreateSnapshot(DateTimeOffset.UtcNow.AddHours(-1));
        await context.SeedQuotaAsync(previous);
        context.Session.RateLimits = JsonElementFrom("""
            {
              "accountId": "chatgpt-account-123",
              "rateLimits": {
                "limitId": null,
                "primary": { "usedPercent": 40, "windowDurationMins": 300 },
                "secondary": null
              }
            }
            """);

        QuotaSnapshotView view = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);

        Assert.That(view.Status, Is.EqualTo(QuotaDisplayStatus.Incomplete));
        Assert.That(view.Snapshot?.LastSuccessfulSnapshot, Is.EqualTo(previous));
        Assert.That(view.Snapshot?.LastAttempt?.MissingWindows, Does.Contain("每週"));
    }

    [Test]
    public async Task InvalidPercentDoesNotReplaceLastSuccessfulSnapshot() {
        QuotaTestContext context = new();
        QuotaSuccessfulSnapshot previous = context.CreateSnapshot(DateTimeOffset.UtcNow.AddMinutes(-1));
        await context.SeedQuotaAsync(previous);
        context.Session.RateLimits = JsonElementFrom("""
            {
              "accountId": "chatgpt-account-123",
              "rateLimits": {
                "limitId": "codex",
                "primary": { "usedPercent": 101, "windowDurationMins": 300 },
                "secondary": { "usedPercent": 40, "windowDurationMins": 10080 }
              }
            }
            """);

        QuotaSnapshotView view = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);

        Assert.That(view.Status, Is.EqualTo(QuotaDisplayStatus.Fresh));
        Assert.That(view.Snapshot?.LastSuccessfulSnapshot, Is.EqualTo(previous));
        Assert.That(view.Snapshot?.LastAttempt?.Status, Is.EqualTo(QuotaRefreshStatus.Error));
    }

    [Test]
    public async Task UnauthorizedResponseKeepsOldValuesAndRequiresReauthentication() {
        QuotaTestContext context = new();
        QuotaSuccessfulSnapshot previous = context.CreateSnapshot(DateTimeOffset.UtcNow.AddMinutes(-1));
        await context.SeedQuotaAsync(previous);
        context.Session.RateFailure = new CodexAppServerRpcException(401, unauthorized: true);

        QuotaSnapshotView view = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);

        Assert.That(view.Status, Is.EqualTo(QuotaDisplayStatus.ReauthenticationRequired));
        Assert.That(view.IsStale, Is.True);
        Assert.That(view.Snapshot?.LastSuccessfulSnapshot, Is.EqualTo(previous));
    }

    [Test]
    public async Task ConcurrentRefreshesShareOneSessionAndOneResult() {
        QuotaTestContext context = new();
        using CancellationTokenSource firstWaiterCancellation = new();
        context.Session.AccountReadGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<QuotaSnapshotView> first = context.Service.RefreshAsync(context.Account.AccountId, firstWaiterCancellation.Token);
        await context.Session.AccountReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task<QuotaSnapshotView> second = context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);
        firstWaiterCancellation.Cancel();
        context.Session.AccountReadGate.TrySetResult(true);

        await Assert.CatchAsync<OperationCanceledException>(async () => await first);
        QuotaSnapshotView result = await second.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.That(result.Status, Is.EqualTo(QuotaDisplayStatus.Fresh));
        Assert.That(context.Factory.CreateCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CoalescedAccountUpdateNotificationRetainsIdentityChangeSignal() {
        QuotaTestContext context = new();
        const string updatedAccountId = "chatgpt-account-456";
        const string updatedEmail = "updated@example.test";
        context.Session.Account = context.Session.Account with {
            AccountId = updatedAccountId,
            Email = updatedEmail
        };
        context.Session.RateLimits = JsonElementFrom("""
            {
              "accountId": "chatgpt-account-456",
              "rateLimitsByLimitId": {
                "codex": {
                  "limitId": null,
                  "primary": { "usedPercent": 20, "windowDurationMins": 300 },
                  "secondary": { "usedPercent": 35, "windowDurationMins": 10080 }
                }
              }
            }
            """);
        context.Session.AccountReadGate = context.Session.NotificationsRead;
        context.Session.SignalNotificationsReadAfter = 3;
        context.Session.QueueNotification("account/updated");
        context.Session.QueueNotification("account/rateLimits/updated");
        context.Session.QueueNotification("account/rateLimits/updated");

        QuotaSnapshotView view = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        CodexAccountProfile updatedProfile = context.Catalog.Accounts.Single();
        Assert.That(view.Status, Is.EqualTo(QuotaDisplayStatus.Fresh));
        Assert.That(context.Session.RateLimitReadCount, Is.EqualTo(2));
        Assert.That(updatedProfile.ExternalAccountId, Is.EqualTo(updatedAccountId));
        Assert.That(
            updatedProfile.IdentityFingerprint,
            Is.EqualTo(context.Fingerprinter.CreateFingerprint(updatedAccountId, updatedEmail, "chatgpt"))
        );
        Assert.That(view.Snapshot?.IdentityFingerprint, Is.EqualTo(updatedProfile.IdentityFingerprint));
    }

    [Test]
    public async Task TailNotificationsDuringPumpShutdownTriggerOneBoundedReread() {
        QuotaTestContext context = new();
        context.Session.OnNotificationsCanceled = () => {
            context.Session.RateLimits = JsonElementFrom("""
                {
                  "accountId": "chatgpt-account-123",
                  "rateLimitsByLimitId": {
                    "codex": {
                      "limitId": null,
                      "primary": { "usedPercent": 25, "windowDurationMins": 300 },
                      "secondary": { "usedPercent": 40, "windowDurationMins": 10080 }
                    }
                  }
                }
                """);
        };
        context.Session.NotificationsOnCancellation = [
            new CodexAppServerNotification("account/rateLimits/updated", default),
            new CodexAppServerNotification("account/rateLimits/updated", default),
            new CodexAppServerNotification("account/rateLimits/updated", default)
        ];

        QuotaSnapshotView view = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.That(view.Status, Is.EqualTo(QuotaDisplayStatus.Fresh));
        Assert.That(context.Session.RateLimitReadCount, Is.EqualTo(2));
        Assert.That(view.Snapshot?.LastSuccessfulSnapshot?.FiveHour.RemainingPercent, Is.EqualTo(75));
        Assert.That(view.Snapshot?.LastSuccessfulSnapshot?.Weekly.RemainingPercent, Is.EqualTo(60));
    }

    [Test]
    public async Task IdentityChangeWithoutAccountUpdatedNotificationFailsClosed() {
        QuotaTestContext context = new();
        QuotaSuccessfulSnapshot previous = context.CreateSnapshot(context.Clock.UtcNow.AddMinutes(-1));
        await context.SeedQuotaAsync(previous);
        context.Session.Account = context.Session.Account with {
            AccountId = "chatgpt-account-456",
            Email = "updated@example.test"
        };
        context.Session.RateLimits = JsonElementFrom("""
            {
              "accountId": "chatgpt-account-456",
              "rateLimitsByLimitId": {
                "codex": {
                  "limitId": null,
                  "primary": { "usedPercent": 20, "windowDurationMins": 300 },
                  "secondary": { "usedPercent": 35, "windowDurationMins": 10080 }
                }
              }
            }
            """);

        QuotaSnapshotView view = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);
        IQuotaSnapshotRepository.Snapshot? saved = await context.SnapshotRepository.LoadAsync(
            context.Account.AccountId,
            CancellationToken.None
        );

        Assert.That(view.Status, Is.EqualTo(QuotaDisplayStatus.ReauthenticationRequired));
        Assert.That(view.Snapshot?.LastSuccessfulSnapshot, Is.EqualTo(previous));
        Assert.That(view.Snapshot?.LastAttempt?.FailureCode, Is.EqualTo("account-identity-unconfirmed"));
        Assert.That(context.Catalog.Accounts.Single(), Is.EqualTo(context.Account));
        Assert.That(saved?.Data.LastSuccessfulSnapshot, Is.EqualTo(previous));
    }

    [Test]
    public async Task CanceledOnlyWaiterCancelsUnderlyingRefresh() {
        QuotaTestContext context = new();
        using CancellationTokenSource cancellation = new();
        context.Session.AccountReadGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<QuotaSnapshotView> refresh = context.Service.RefreshAsync(context.Account.AccountId, cancellation.Token);
        await context.Session.AccountReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.CatchAsync<OperationCanceledException>(async () => await refresh.WaitAsync(TimeSpan.FromSeconds(3)));
        await context.Session.AccountReadCanceledSignal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(context.Session.AccountReadCanceled, Is.True);
    }

    [Test]
    public async Task CanceledOnlyWaiterStartingNewRefreshDuringCancellationCreatesNewFlight() {
        QuotaTestContext context = new();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource<bool> accountReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Session.AccountReadGate = accountReadGate;
        TaskCompletionSource<Task<QuotaSnapshotView>> replacementRefreshSource = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource<bool> replacementRateReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseReplacementRateRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> oldFlightFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Session.OnAccountReadCanceled = () => {
            replacementRefreshSource.TrySetResult(
                context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None)
            );
        };
        context.Session.BeforeReadRateLimits = () => {
            replacementRateReadStarted.TrySetResult(true);
            return releaseReplacementRateRead.Task;
        };
        context.Service.StateChanged += (_, _) => oldFlightFinished.TrySetResult(true);

        Task<QuotaSnapshotView> canceledRefresh = context.Service.RefreshAsync(context.Account.AccountId, cancellation.Token);
        await context.Session.AccountReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.CatchAsync<OperationCanceledException>(
            async () => await canceledRefresh.WaitAsync(TimeSpan.FromSeconds(3))
        );
        Task<QuotaSnapshotView> replacementRefresh = await replacementRefreshSource.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await context.Session.AccountReadCanceledSignal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await oldFlightFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(replacementRefresh.IsCompleted, Is.False);

        accountReadGate.TrySetResult(true);
        await replacementRateReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Task<QuotaSnapshotView> sharedReplacementRefresh = context.Service.RefreshAsync(
            context.Account.AccountId,
            CancellationToken.None
        );
        releaseReplacementRateRead.TrySetResult(true);
        QuotaSnapshotView replacement = await replacementRefresh.WaitAsync(TimeSpan.FromSeconds(3));
        QuotaSnapshotView sharedReplacement = await sharedReplacementRefresh.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.That(replacement.Status, Is.EqualTo(QuotaDisplayStatus.Fresh));
        Assert.That(sharedReplacement.Status, Is.EqualTo(QuotaDisplayStatus.Fresh));
        Assert.That(context.Factory.CreateCount, Is.EqualTo(2));
    }

    [Test]
    public async Task LateResponseAfterProfileRevisionChangeIsDiscarded() {
        QuotaTestContext context = new();
        context.Session.BeforeReadRateLimits = () => {
            context.Catalog.ChangeProfile(
                context.Account.AccountId,
                profile => profile with { ProfileRevision = profile.ProfileRevision + 1 }
            );
            return Task.CompletedTask;
        };

        _ = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);
        IQuotaSnapshotRepository.Snapshot? saved = await context.SnapshotRepository.LoadAsync(
            context.Account.AccountId,
            CancellationToken.None
        );

        Assert.That(context.Catalog.Accounts.Single().ProfileRevision, Is.EqualTo(context.Account.ProfileRevision + 1));
        Assert.That(saved?.Data.LastSuccessfulSnapshot, Is.Null);
    }

    [Test]
    public async Task TimedOutReadPreservesOldSnapshotAndRecordsRetry() {
        QuotaTestContext context = new();
        QuotaSuccessfulSnapshot previous = context.CreateSnapshot(context.Clock.UtcNow.AddMinutes(-1));
        await context.SeedQuotaAsync(previous);
        context.Session.RateFailure = new TimeoutException();

        QuotaSnapshotView view = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);

        Assert.That(view.Status, Is.EqualTo(QuotaDisplayStatus.Fresh));
        Assert.That(view.Snapshot?.LastSuccessfulSnapshot, Is.EqualTo(previous));
        Assert.That(view.Snapshot?.LastAttempt?.FailureCode, Is.EqualTo("timeout"));
        Assert.That(view.Snapshot?.LastAttempt?.RetryAtUtc, Is.Not.Null);
    }
    [Test]
    public async Task ErrorBackoffPreventsAnotherSessionUntilRetryAt() {
        QuotaTestContext context = new();
        context.Session.RateFailure = new CodexAppServerRpcException(503, unauthorized: false);

        QuotaSnapshotView first = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);
        DateTimeOffset retryAt = first.Snapshot?.LastAttempt?.RetryAtUtc
            ?? throw new AssertionException("A retry deadline was not recorded.");
        int creationCount = context.Factory.CreateCount;

        QuotaSnapshotView blocked = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);
        Assert.That(blocked.Status, Is.EqualTo(QuotaDisplayStatus.Error));
        Assert.That(context.Factory.CreateCount, Is.EqualTo(creationCount));

        context.Session.RateFailure = null;
        context.Clock.UtcNow = retryAt.AddSeconds(1);
        QuotaSnapshotView retried = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);

        Assert.That(retried.Status, Is.EqualTo(QuotaDisplayStatus.Fresh));
        Assert.That(context.Factory.CreateCount, Is.EqualTo(creationCount + 1));
    }

    [Test]
    public async Task IncompleteBackoffPreventsAnotherSessionUntilRetryAt() {
        QuotaTestContext context = new();
        JsonElement completeRateLimits = context.Session.RateLimits.Clone();
        context.Session.RateLimits = JsonElementFrom("""
            {
              "accountId": "chatgpt-account-123",
              "rateLimits": {
                "limitId": null,
                "primary": { "usedPercent": 40, "windowDurationMins": 300 },
                "secondary": null
              }
            }
            """);

        QuotaSnapshotView first = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);
        DateTimeOffset retryAt = first.Snapshot?.LastAttempt?.RetryAtUtc
            ?? throw new AssertionException("An incomplete-response retry deadline was not recorded.");
        int creationCount = context.Factory.CreateCount;

        QuotaSnapshotView blocked = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);
        Assert.That(blocked.Status, Is.EqualTo(QuotaDisplayStatus.Incomplete));
        Assert.That(context.Factory.CreateCount, Is.EqualTo(creationCount));

        context.Session.RateLimits = completeRateLimits;
        context.Clock.UtcNow = retryAt.AddSeconds(1);
        QuotaSnapshotView retried = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);

        Assert.That(retried.Status, Is.EqualTo(QuotaDisplayStatus.Fresh));
        Assert.That(context.Factory.CreateCount, Is.EqualTo(creationCount + 1));
    }

    [Test]
    public async Task Http429RetryAfterOverridesExponentialDelay() {
        QuotaTestContext context = new();
        context.Session.RateFailure = new CodexAppServerRpcException(429, unauthorized: false, TimeSpan.FromMinutes(7));

        QuotaSnapshotView view = await context.Service.RefreshAsync(context.Account.AccountId, CancellationToken.None);

        Assert.That(view.Snapshot?.LastAttempt?.RetryAtUtc, Is.EqualTo(context.Clock.UtcNow.AddMinutes(7)));
    }

    [Test]
    public async Task SnapshotOlderThanTwiceConfiguredIntervalIsStale() {
        QuotaTestContext context = new();
        QuotaSuccessfulSnapshot previous = context.CreateSnapshot(context.Clock.UtcNow.AddMinutes(-10).AddSeconds(-1));
        await context.SeedQuotaAsync(previous);

        QuotaSnapshotView view = await context.Service.GetViewAsync(context.Account.AccountId, CancellationToken.None);

        Assert.That(view.Status, Is.EqualTo(QuotaDisplayStatus.Stale));
        Assert.That(view.IsStale, Is.True);
    }

    [Test]
    public async Task CorruptedSnapshotReturnsErrorAndPreservesOriginalFileOnRetry() {
        QuotaTestContext context = new();
        string dataRoot = Path.Combine(Path.GetTempPath(), "AiAccountManager.Tests", Guid.NewGuid().ToString("N"));

        try {
            CurrentUserDataDirectory dataDirectory = new(dataRoot);
            AtomicJsonFileStore fileStore = new(dataDirectory);
            JsonQuotaSnapshotRepository snapshotRepository = new(fileStore);
            string quotaDirectory = Path.Combine(dataRoot, "quota");
            Directory.CreateDirectory(quotaDirectory);
            string snapshotPath = Path.Combine(quotaDirectory, $"quota-{context.Account.AccountId:N}.json");
            byte[] corruptedContent = Encoding.UTF8.GetBytes("{\"schemaVersion\":");
            await File.WriteAllBytesAsync(snapshotPath, corruptedContent);
            string originalHash = Convert.ToHexString(SHA256.HashData(corruptedContent));
            CodexQuotaService service = new(
                context.Catalog,
                snapshotRepository,
                context.SettingsRepository,
                context.Factory,
                new QuotaResponseParser(),
                context.Fingerprinter,
                context.Clock,
                new ExclusiveOperationCoordinator()
            );

            QuotaSnapshotView view = await service.GetViewAsync(context.Account.AccountId, CancellationToken.None);
            QuotaSnapshotView retry = await service.RefreshAsync(context.Account.AccountId, CancellationToken.None);
            byte[] retainedContent = await File.ReadAllBytesAsync(snapshotPath);

            Assert.That(view.AccountId, Is.EqualTo(context.Account.AccountId));
            Assert.That(view.Status, Is.EqualTo(QuotaDisplayStatus.Error));
            Assert.That(view.Message, Is.Not.Empty);
            Assert.That(view.Snapshot, Is.Null);
            Assert.That(context.Catalog.Accounts, Does.Contain(context.Account));
            Assert.That(retry.Status, Is.EqualTo(QuotaDisplayStatus.Error));
            Assert.That(context.Factory.CreateCount, Is.Zero);
            Assert.That(Convert.ToHexString(SHA256.HashData(retainedContent)), Is.EqualTo(originalHash));
        } finally {
            if (Directory.Exists(dataRoot)) {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Test]
    public async Task GetViewAsync_SnapshotIOException_ReturnsErrorAndPreservesOriginalFiles() {
        await AssertSnapshotReadIoFailureAsync(new IOException("Simulated quota snapshot read failure."));
    }

    [Test]
    public async Task GetViewAsync_SnapshotAccessDenied_ReturnsErrorAndPreservesOriginalFiles() {
        await AssertSnapshotReadIoFailureAsync(new UnauthorizedAccessException("Simulated quota snapshot access denial."));
    }

    [Test]
    public async Task GetViewAsync_SnapshotReadCancellation_Propagates() {
        QuotaTestContext context = new();
        context.SnapshotRepository.LoadFailure = new OperationCanceledException("Simulated snapshot read cancellation.");

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await context.Service.GetViewAsync(context.Account.AccountId, CancellationToken.None)
        );
        Assert.That(context.SnapshotRepository.SaveCount, Is.Zero);
    }

    [Test]
    public async Task GetViewAsync_UnexpectedSnapshotReadFailure_Propagates() {
        QuotaTestContext context = new();
        context.SnapshotRepository.LoadFailure = new InvalidOperationException("Simulated snapshot repository failure.");

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await context.Service.GetViewAsync(context.Account.AccountId, CancellationToken.None)
        );
        Assert.That(context.SnapshotRepository.SaveCount, Is.Zero);
    }

    private static async Task AssertSnapshotReadIoFailureAsync(Exception failure) {
        QuotaTestContext context = new();
        string dataRoot = Path.Combine(Path.GetTempPath(), "AiAccountManager.Tests", Guid.NewGuid().ToString("N"));
        string quotaDirectory = Path.Combine(dataRoot, "quota");
        string snapshotPath = Path.Combine(quotaDirectory, $"quota-{context.Account.AccountId:N}.json");

        try {
            Directory.CreateDirectory(quotaDirectory);
            byte[] originalContent = Encoding.UTF8.GetBytes("{\"preserved\":true}");
            await File.WriteAllBytesAsync(snapshotPath, originalContent);
            string originalHash = Convert.ToHexString(SHA256.HashData(originalContent));
            string[] originalEntries = Directory.GetFileSystemEntries(dataRoot, "*", SearchOption.AllDirectories);
            context.SnapshotRepository.LoadFailure = failure;

            QuotaSnapshotView view = await context.Service.GetViewAsync(context.Account.AccountId, CancellationToken.None);
            byte[] retainedContent = await File.ReadAllBytesAsync(snapshotPath);
            string[] retainedEntries = Directory.GetFileSystemEntries(dataRoot, "*", SearchOption.AllDirectories);

            Assert.That(view.AccountId, Is.EqualTo(context.Account.AccountId));
            Assert.That(view.Status, Is.EqualTo(QuotaDisplayStatus.Error));
            Assert.That(view.Message, Is.Not.Empty);
            Assert.That(view.Snapshot, Is.Null);
            Assert.That(context.Catalog.Accounts, Does.Contain(context.Account));
            Assert.That(context.Factory.CreateCount, Is.Zero);
            Assert.That(context.SnapshotRepository.LoadCount, Is.EqualTo(1));
            Assert.That(context.SnapshotRepository.SaveCount, Is.Zero);
            Assert.That(Convert.ToHexString(SHA256.HashData(retainedContent)), Is.EqualTo(originalHash));
            Assert.That(retainedEntries, Is.EquivalentTo(originalEntries));
        } finally {
            if (Directory.Exists(dataRoot)) {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static JsonElement JsonElementFrom(string json) {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

internal sealed class QuotaTestContext {
    public QuotaTestContext(string? updateFeedUrl = null, bool includeAccount = true) {
        Clock = new FakeClock(DateTimeOffset.Parse("2026-10-18T12:00:00Z"));
        Catalog = new FakeAccountCatalogRepository();
        Account = new CodexAccountProfile(
            Guid.NewGuid(),
            null,
            "p•••@example.test",
            Fingerprinter.CreateFingerprint("chatgpt-account-123", null, "chatgpt"),
            1,
            Clock.UtcNow,
            "connected",
            "chatgpt-account-123",
            Fingerprinter.CreateFingerprint(null, "person@example.test", "chatgpt")
        );
        Catalog.SetAccounts(includeAccount ? [Account] : []);
        SnapshotRepository = new FakeQuotaSnapshotRepository();
        SettingsRepository = new FakeSettingsRepository(AppSettings.Defaults with { UpdateFeedUrl = updateFeedUrl });
        Session = new FakeQuotaSession(Account);
        Factory = new FakeQuotaSessionFactory(() => Session);
        Service = new CodexQuotaService(
            Catalog,
            SnapshotRepository,
            SettingsRepository,
            Factory,
            new QuotaResponseParser(),
            Fingerprinter,
            Clock,
            new ExclusiveOperationCoordinator()
        );
    }

    public FakeClock Clock { get; }

    public FakeIdentityFingerprinter Fingerprinter { get; } = new();

    public FakeAccountCatalogRepository Catalog { get; }

    public CodexAccountProfile Account { get; }

    public FakeQuotaSnapshotRepository SnapshotRepository { get; }

    public FakeSettingsRepository SettingsRepository { get; }

    public FakeQuotaSession Session { get; }

    public FakeQuotaSessionFactory Factory { get; }

    public CodexQuotaService Service { get; }

    public QuotaSuccessfulSnapshot CreateSnapshot(DateTimeOffset retrievedAt) {
        return new QuotaSuccessfulSnapshot(
            retrievedAt,
            new QuotaWindow(300, 73, null),
            new QuotaWindow(10080, 51, null)
        );
    }

    public Task SeedQuotaAsync(QuotaSuccessfulSnapshot success) {
        QuotaSnapshotRecord record = new(
            Account.AccountId,
            Account.IdentityFingerprint,
            Account.ProfileRevision,
            success,
            new QuotaRefreshAttempt(QuotaRefreshStatus.Unknown, Clock.UtcNow, null, 0, null, [])
        );
        return SnapshotRepository.SeedAsync(record);
    }
}

internal sealed class FakeClock(DateTimeOffset initialTime) : IClock {
    public DateTimeOffset UtcNow { get; set; } = initialTime;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) {
        return Task.Delay(delay, cancellationToken);
    }
}

internal sealed class FakeIdentityFingerprinter : IIdentityFingerprinter {
    public string CreateFingerprint(string? accountId, string? email, string authMode) {
        string identity = !string.IsNullOrWhiteSpace(accountId)
            ? "id:" + accountId.Trim()
            : "email:" + (email ?? "").Trim().ToLowerInvariant() + ":" + authMode.ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public string MaskEmail(string? email) {
        if (string.IsNullOrWhiteSpace(email)) {
            return "Codex 帳號";
        }

        int atIndex = email.IndexOf('@');
        return atIndex <= 0 ? "Codex 帳號" : email[..1] + "•••" + email[atIndex..];
    }
}

internal sealed class FakeAccountCatalogRepository : IAccountCatalogRepository {
    private long revision;

    public IReadOnlyList<CodexAccountProfile> Accounts { get; private set; } = [];

    public Func<CancellationToken, Task>? BeforeLoadAsync { get; set; }

    public async Task<IAccountCatalogRepository.Snapshot> LoadAsync(CancellationToken cancellationToken) {
        if (BeforeLoadAsync is not null) {
            await BeforeLoadAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new IAccountCatalogRepository.Snapshot(Accounts.ToArray(), Hash);
    }

    public Task<IAccountCatalogRepository.SaveResult> SaveAsync(
        IReadOnlyList<CodexAccountProfile> accounts,
        string expectedSha256,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(expectedSha256, Hash, StringComparison.Ordinal)) {
            return Task.FromResult(new IAccountCatalogRepository.SaveResult(false, null, true));
        }

        Accounts = accounts.ToArray();
        revision++;
        return Task.FromResult(new IAccountCatalogRepository.SaveResult(
            true,
            new IAccountCatalogRepository.Snapshot(Accounts, Hash),
            false
        ));
    }

    public void SetAccounts(IReadOnlyList<CodexAccountProfile> accounts) {
        Accounts = accounts.ToArray();
    }

    public void ChangeProfile(Guid accountId, Func<CodexAccountProfile, CodexAccountProfile> change) {
        CodexAccountProfile[] changed = Accounts.ToArray();
        int index = Array.FindIndex(changed, profile => profile.AccountId == accountId);
        if (index < 0) {
            return;
        }

        changed[index] = change(changed[index]);
        Accounts = changed;
        revision++;
    }

    private string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision.ToString(CultureInfo.InvariantCulture))));
}

internal sealed class FakeQuotaSnapshotRepository : IQuotaSnapshotRepository {
    private readonly Dictionary<Guid, (QuotaSnapshotRecord Data, string Hash)> records = [];
    private long revision;

    public Exception? LoadFailure { get; set; }

    public int LoadCount { get; private set; }

    public int SaveCount { get; private set; }

    public int ConflictsRemaining { get; set; }

    public bool ConflictSuccessfulSave { get; set; }

    public bool ConflictFailedSave { get; set; }

    public Task<IQuotaSnapshotRepository.Snapshot?> LoadAsync(Guid accountId, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        LoadCount++;
        if (LoadFailure is Exception loadFailure) {
            return Task.FromException<IQuotaSnapshotRepository.Snapshot?>(loadFailure);
        }

        return Task.FromResult(records.TryGetValue(accountId, out var stored)
            ? new IQuotaSnapshotRepository.Snapshot(stored.Data, stored.Hash)
            : null);
    }

    public Task<IQuotaSnapshotRepository.SaveResult> SaveAsync(
        QuotaSnapshotRecord snapshot,
        string? expectedSha256,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        SaveCount++;
        if (ConflictsRemaining > 0) {
            ConflictsRemaining--;
            return Task.FromResult(new IQuotaSnapshotRepository.SaveResult(false, null, true));
        }
        if (ConflictSuccessfulSave && snapshot.LastAttempt?.Status == QuotaRefreshStatus.Unknown
            && records.TryGetValue(snapshot.AccountId, out var current)
        ) {
            records[snapshot.AccountId] = (current.Data with { LastAttempt = null }, (++revision).ToString(CultureInfo.InvariantCulture));
            return Task.FromResult(new IQuotaSnapshotRepository.SaveResult(false, null, true));
        }
        if (ConflictFailedSave && snapshot.LastAttempt?.Status == QuotaRefreshStatus.Error
            && records.TryGetValue(snapshot.AccountId, out var latest)
        ) {
            records[snapshot.AccountId] = (latest.Data with { LastAttempt = null }, (++revision).ToString(CultureInfo.InvariantCulture));
            return Task.FromResult(new IQuotaSnapshotRepository.SaveResult(false, null, true));
        }
        bool exists = records.TryGetValue(snapshot.AccountId, out var stored);
        if (exists != (expectedSha256 is not null)
            || (exists && !string.Equals(stored.Hash, expectedSha256, StringComparison.Ordinal))) {
            return Task.FromResult(new IQuotaSnapshotRepository.SaveResult(false, null, true));
        }

        string hash = (++revision).ToString(CultureInfo.InvariantCulture);
        records[snapshot.AccountId] = (snapshot, hash);
        return Task.FromResult(new IQuotaSnapshotRepository.SaveResult(
            true,
            new IQuotaSnapshotRepository.Snapshot(snapshot, hash),
            false
        ));
    }

    public Task SeedAsync(QuotaSnapshotRecord snapshot) {
        records[snapshot.AccountId] = (snapshot, (++revision).ToString(CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }
}

internal sealed class FakeSettingsRepository(AppSettings settings) : ISettingsRepository {
    private AppSettings current = settings;
    private int revision;

    public Task<ISettingsRepository.Snapshot> LoadAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ISettingsRepository.Snapshot(current, Hash));
    }

    public Task<ISettingsRepository.SaveResult> SaveAsync(
        AppSettings newSettings,
        string expectedSha256,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(expectedSha256, Hash, StringComparison.Ordinal)) {
            return Task.FromResult(new ISettingsRepository.SaveResult(false, null, true));
        }

        current = newSettings;
        revision++;
        return Task.FromResult(new ISettingsRepository.SaveResult(
            true,
            new ISettingsRepository.Snapshot(current, Hash),
            false
        ));
    }

    private string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision.ToString(CultureInfo.InvariantCulture))));
}

internal sealed class FakeQuotaSessionFactory(Func<FakeQuotaSession> create) : ICodexAppServerSessionFactory {
    public int CreateCount { get; private set; }
    public List<Guid> CreatedAccountIds { get; } = [];

    public Task<ICodexAppServerSession> CreateAsync(
        Guid accountId,
        long profileRevision,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        CreateCount++;
        CreatedAccountIds.Add(accountId);
        return Task.FromResult<ICodexAppServerSession>(create());
    }
}

internal sealed class FakeQuotaSession(CodexAccountProfile profile) : ICodexAppServerSession {
    private readonly Channel<CodexAppServerNotification> notifications = Channel.CreateUnbounded<CodexAppServerNotification>();
    private int notificationsRead;

    public CodexAccountRead Account { get; set; } = new(
        "chatgpt",
        profile.ExternalAccountId,
        "person@example.test",
        "plus",
        false
    );

    public JsonElement RateLimits { get; set; } = JsonElementFrom("""
        {
          "accountId": "chatgpt-account-123",
          "rateLimitsByLimitId": {
            "codex": {
              "limitId": null,
              "primary": { "usedPercent": 20, "windowDurationMins": 300, "resetsAt": 1900000000 },
              "secondary": { "usedPercent": 35, "windowDurationMins": 10080, "resetsAt": 1900000100 }
            }
          }
        }
        """);

    public Exception? RateFailure { get; set; }

    public TaskCompletionSource<bool> AccountReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool>? AccountReadGate { get; set; }

    public bool AccountReadCanceled { get; private set; }

    public TaskCompletionSource<bool> AccountReadCanceledSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Action? OnAccountReadCanceled { get; set; }

    public TaskCompletionSource<bool> NotificationsRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int? SignalNotificationsReadAfter { get; set; }

    public Action? OnNotificationsCanceled { get; set; }

    public IReadOnlyList<CodexAppServerNotification> NotificationsOnCancellation { get; set; } = [];

    public Func<Task>? BeforeReadRateLimits { get; set; }

    public int RateLimitReadCount { get; private set; }

    public int ConsumeCount { get; private set; }

    public string? IdempotencyKey { get; private set; }

    public string? ConsumedCreditId { get; private set; }

    public string ConsumeOutcome { get; set; } = "reset";

    public Action? BeforeConsume { get; set; }

    public Func<CancellationToken, Task>? ConsumeInProgress { get; set; }

    public Exception? ConsumeFailure { get; set; }

    public Guid AccountId => profile.AccountId;

    public long ProfileRevision => profile.ProfileRevision;

    public async IAsyncEnumerable<CodexAppServerNotification> ReadNotificationsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken
    ) {
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() => {
            OnNotificationsCanceled?.Invoke();
            foreach (CodexAppServerNotification notification in NotificationsOnCancellation) {
                notifications.Writer.TryWrite(notification);
            }

            notifications.Writer.TryComplete();
        });
        await foreach (CodexAppServerNotification notification in notifications.Reader.ReadAllAsync()) {
            yield return notification;
            int currentCount = Interlocked.Increment(ref notificationsRead);
            if (SignalNotificationsReadAfter is int signalAfter && currentCount >= signalAfter) {
                NotificationsRead.TrySetResult(true);
            }
        }
    }

    public void QueueNotification(string method) {
        notifications.Writer.TryWrite(new CodexAppServerNotification(method, default));
    }

    public Task InitializeAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<CodexLoginStart> StartChatGptLoginAsync(CancellationToken cancellationToken) {
        throw new NotSupportedException();
    }

    public Task<CodexLoginCompletion> WaitForLoginCompletionAsync(
        string loginId,
        TimeSpan timeout,
        CancellationToken cancellationToken
    ) {
        throw new NotSupportedException();
    }

    public Task<bool> CancelChatGptLoginAsync(string loginId, CancellationToken cancellationToken) {
        throw new NotSupportedException();
    }

    public async Task<CodexAccountRead> ReadAccountAsync(bool refreshToken, CancellationToken cancellationToken) {
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() => {
            OnAccountReadCanceled?.Invoke();
        });
        cancellationToken.ThrowIfCancellationRequested();
        AccountReadStarted.TrySetResult(true);
        if (AccountReadGate is not null) {
            try {
                await AccountReadGate.Task.WaitAsync(cancellationToken);
            } catch (OperationCanceledException) {
                AccountReadCanceled = true;
                AccountReadCanceledSignal.TrySetResult(true);
                throw;
            }
        }

        return Account;
    }

    public async Task<JsonElement> ReadRateLimitsAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        RateLimitReadCount++;
        if (BeforeReadRateLimits is not null) {
            await BeforeReadRateLimits();
        }

        if (RateFailure is not null) {
            throw RateFailure;
        }

        return RateLimits.Clone();
    }

    public async Task<string> ConsumeResetCreditAsync(string idempotencyKey, string? creditId, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeConsume?.Invoke();
        ConsumeCount++;
        if (ConsumeFailure is not null) { throw ConsumeFailure; }
        IdempotencyKey = idempotencyKey;
        ConsumedCreditId = creditId;
        if (ConsumeInProgress is not null) {
            await ConsumeInProgress(cancellationToken);
        }
        return ConsumeOutcome;
    }

    public Task LogoutAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() {
        notifications.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private static JsonElement JsonElementFrom(string json) {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
