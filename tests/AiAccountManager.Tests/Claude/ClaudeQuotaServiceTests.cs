using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Tests.Codex;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Claude;

[TestFixture]
public sealed class ClaudeQuotaServiceTests {
    [Test]
    public async Task StartupRefresh_SettingsChange_DoesNotRefreshAgain() {
        var setup = CreateSingleProfile(IClaudeOAuthClient.Outcome.Success);
        setup.OAuth.UsageResults["token-a"] = Success(ClaudeTestData.ReadFixture("usage-new-limits.json"));
        ClaudeQuotaService service = CreateService(setup.AccountRepository, setup.ProfileStore, setup.OAuth, new());
        SettingsService settings = new(new FakeSettingsRepository(AppSettings.Defaults), new FakeAccountCatalogRepository());
        await settings.InitializeAsync(CancellationToken.None);
        using ClaudeQuotaScheduler scheduler = new(settings, setup.AccountRepository, service);

        await scheduler.StartAsync();
        await WaitForIntervalAsync(scheduler, 5);
        bool saved = await settings.SaveAsync(AppSettings.Defaults with { QuotaRefreshIntervalMinutes = 1 }, CancellationToken.None);
        await WaitForIntervalAsync(scheduler, 1);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.That(saved, Is.True);
        Assert.That(setup.OAuth.UsageTokens, Is.EqualTo(new[] { "token-a" }));
    }

    private static async Task WaitForIntervalAsync(ClaudeQuotaScheduler scheduler, int interval) {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (scheduler.CurrentIntervalMinutes != interval) {
            if (DateTimeOffset.UtcNow >= deadline) {
                throw new AssertionException("The Claude scheduler did not apply the expected interval.");
            }

            await Task.Delay(10);
        }
    }
    [Test]
    public void Parser_UsesNewLimitsAndPreservesServerResetTimes() {
        JsonElement payload = ClaudeTestData.ReadFixture("usage-new-limits.json");

        ClaudeQuotaResponseParser.ParseResult result = ClaudeQuotaResponseParser.Parse(
            payload,
            Guid.NewGuid(),
            ClaudeTestData.Now
        );

        Assert.That(result.IsComplete, Is.True);
        Assert.That(result.Snapshot, Is.Not.Null);
        Assert.That(result.Snapshot!.Windows, Has.Count.EqualTo(3));
        ClaudeQuotaSnapshot.Window session = result.Snapshot.Windows.Single(window => window.Key == "session");
        ClaudeQuotaSnapshot.Window weekly = result.Snapshot.Windows.Single(window => window.Key == "weekly_all");
        ClaudeQuotaSnapshot.Window model = result.Snapshot.Windows.Single(window => window.Key == "weekly_scoped:Fable");
        Assert.That(session.UtilizationPercent, Is.EqualTo(15m));
        Assert.That(session.DurationMinutes, Is.EqualTo(300));
        Assert.That(session.ResetsAtUtc, Is.EqualTo(DateTimeOffset.Parse("2026-10-26T14:00:00Z")));
        Assert.That(weekly.UtilizationPercent, Is.EqualTo(36m));
        Assert.That(model.UtilizationPercent, Is.EqualTo(8m));
        Assert.That(model.DurationMinutes, Is.EqualTo(10080));
    }

    [Test]
    public void Parser_UsesLegacyWindowsAndOptionalModelWindow() {
        JsonElement payload = ClaudeTestData.ReadFixture("usage-legacy-windows.json");

        ClaudeQuotaResponseParser.ParseResult result = ClaudeQuotaResponseParser.Parse(
            payload,
            Guid.NewGuid(),
            ClaudeTestData.Now
        );

        Assert.That(result.IsComplete, Is.True);
        Assert.That(result.Windows, Has.Count.EqualTo(3));
        Assert.That(result.Windows.Single(window => window.Key == "session").UtilizationPercent, Is.EqualTo(23m));
        Assert.That(result.Windows.Single(window => window.Key == "weekly_all").UtilizationPercent, Is.EqualTo(47.5m));
        Assert.That(result.Windows.Single(window => window.Key == "weekly_sonnet").UtilizationPercent, Is.EqualTo(12m));
    }

    [TestCase("{\"limits\":[{\"kind\":\"unknown\",\"percent\":10}],\"five_hour\":{\"utilization\":10},\"seven_day\":{\"utilization\":20}}")]
    [TestCase("{\"five_hour\":{\"utilization\":10}}")]
    [TestCase("[]")]
    public void Parser_MarksUnknownOrMissingQuotaDataIncomplete(string json) {
        JsonElement payload = ClaudeTestData.ReadJson(json);

        ClaudeQuotaResponseParser.ParseResult result = ClaudeQuotaResponseParser.Parse(
            payload,
            Guid.NewGuid(),
            ClaudeTestData.Now
        );

        Assert.That(result.IsComplete, Is.False);
        Assert.That(result.Snapshot, Is.Null);
        Assert.That(result.Message, Is.Not.Empty);
    }

    [Test]
    public async Task RefreshAsync_F002_LegacySidecarSnapshotStillSupportsQuotaLookup() {
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        ClaudeAccountProfile first = ClaudeTestData.CreateProfile(firstId, "account-a", "org-a");
        ClaudeAccountProfile second = ClaudeTestData.CreateProfile(secondId, "account-b", "org-b");
        ClaudeAccountProfile.DesktopSnapshot firstSnapshot = ClaudeTestData.CreateSnapshot("account-a", "token-a") with {
            CookieWriteAheadLog = null,
            CookieSharedMemory = null,
            CookieSidecarSnapshotVersion = 0
        };
        ClaudeAccountProfile.DesktopSnapshot secondSnapshot = ClaudeTestData.CreateSnapshot("account-b", "token-b");
        ClaudeTestAccountRepository accountRepository = new(first, second);
        accountRepository.AddSnapshot(firstId, firstSnapshot);
        accountRepository.AddSnapshot(secondId, secondSnapshot);
        ClaudeTestProfileStore profileStore = new();
        profileStore.SetCandidates(firstSnapshot, "token-a");
        profileStore.SetCandidates(secondSnapshot, "token-b");
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["token-a"] = ClaudeTestData.VerifiedProfile("account-a", "org-a");
        oauth.ProfileResults["token-b"] = ClaudeTestData.VerifiedProfile("account-b", "org-b");
        oauth.UsageResults["token-a"] = Success(ClaudeTestData.ReadFixture("usage-new-limits.json"));
        oauth.UsageResults["token-b"] = new(
            IClaudeOAuthClient.Outcome.Error,
            null,
            null,
            "synthetic_usage_error"
        );
        ClaudeTestQuotaRepository quotaRepository = new();
        ClaudeQuotaService service = CreateService(accountRepository, profileStore, oauth, quotaRepository);

        ClaudeQuotaSnapshot.View firstResult = await service.RefreshAsync(firstId, CancellationToken.None);
        ClaudeQuotaSnapshot.View secondResult = await service.RefreshAsync(secondId, CancellationToken.None);
        IClaudeQuotaRepository.Snapshot? firstStored = await quotaRepository.LoadAsync(firstId, CancellationToken.None);

        Assert.That(firstResult.Status, Is.EqualTo(ClaudeQuotaSnapshot.RefreshStatus.Fresh));
        Assert.That(firstSnapshot.CookieSidecarSnapshotVersion, Is.Zero);
        Assert.That(firstResult.Snapshot!.ProfileId, Is.EqualTo(firstId));
        Assert.That(secondResult.Status, Is.EqualTo(ClaudeQuotaSnapshot.RefreshStatus.Error));
        Assert.That(secondResult.Snapshot, Is.Null);
        Assert.That(firstStored!.View.Snapshot!.ProfileId, Is.EqualTo(firstId));
        Assert.That(oauth.UsageTokens, Is.EqualTo(new[] { "token-a", "token-b" }));
    }

    [Test]
    public async Task RefreshAsync_DoesNotUseUsageTokenWhenProfileIdentityMismatches() {
        Guid profileId = Guid.NewGuid();
        ClaudeAccountProfile profile = ClaudeTestData.CreateProfile(profileId, "account-a", "org-a");
        ClaudeAccountProfile.DesktopSnapshot snapshot = ClaudeTestData.CreateSnapshot("account-a", "token-a");
        ClaudeTestAccountRepository accountRepository = new(profile);
        accountRepository.AddSnapshot(profileId, snapshot);
        ClaudeTestProfileStore profileStore = new();
        profileStore.SetCandidates(snapshot, "token-a");
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["token-a"] = ClaudeTestData.VerifiedProfile("account-other", "org-other");
        ClaudeQuotaService service = CreateService(accountRepository, profileStore, oauth, new());

        ClaudeQuotaSnapshot.View result = await service.RefreshAsync(profileId, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(ClaudeQuotaSnapshot.RefreshStatus.Incomplete));
        Assert.That(oauth.UsageTokens, Is.Empty);
    }

    [Test]
    public async Task RefreshAsync_DoesNotPersistQuotaForMissingProfile() {
        ClaudeTestQuotaRepository quotaRepository = new();
        ClaudeQuotaService service = CreateService(new(), new(), new(), quotaRepository);

        ClaudeQuotaSnapshot.View result = await service.RefreshAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(ClaudeQuotaSnapshot.RefreshStatus.Error));
        Assert.That(result.Message, Does.Contain("找不到指定的 Claude profile"));
        Assert.That(quotaRepository.SaveCallCount, Is.Zero);
    }

    [TestCase(IClaudeOAuthClient.Outcome.Unauthorized)]
    [TestCase(IClaudeOAuthClient.Outcome.Forbidden)]
    public async Task RefreshAsync_MarksRevokedProfileTokenForReauthentication(IClaudeOAuthClient.Outcome outcome) {
        (ClaudeAccountProfile profile, ClaudeAccountProfile.DesktopSnapshot snapshot,
            ClaudeTestAccountRepository accountRepository, ClaudeTestProfileStore profileStore,
            ClaudeTestOAuthClient oauth) = CreateSingleProfile(outcome);
        ClaudeQuotaService service = CreateService(accountRepository, profileStore, oauth, new());

        ClaudeQuotaSnapshot.View result = await service.RefreshAsync(profile.ProfileId, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(ClaudeQuotaSnapshot.RefreshStatus.ReauthenticationRequired));
        Assert.That(result.Snapshot, Is.Null);
        Assert.That(oauth.UsageTokens, Is.Empty);
    }

    [Test]
    public async Task RefreshAsync_RateLimitKeepsLastGoodSnapshotAndRetryTime() {
        (ClaudeAccountProfile profile, ClaudeAccountProfile.DesktopSnapshot snapshot,
            ClaudeTestAccountRepository accountRepository, ClaudeTestProfileStore profileStore,
            ClaudeTestOAuthClient oauth) = CreateSingleProfile(IClaudeOAuthClient.Outcome.Success);
        ClaudeQuotaSnapshot saved = new(
            profile.ProfileId,
            ClaudeTestData.Now.AddHours(-1),
            [new("session", 300, 40m, ClaudeTestData.Now.AddHours(2)), new("weekly_all", 10080, 30m, ClaudeTestData.Now.AddDays(2))]
        );
        ClaudeTestQuotaRepository quotaRepository = new();
        quotaRepository.Seed(new(
            profile.ProfileId,
            saved,
            ClaudeQuotaSnapshot.RefreshStatus.Fresh,
            ClaudeTestData.Now.AddHours(-1),
            null
        ));
        oauth.UsageResults["token-a"] = new(
            IClaudeOAuthClient.Outcome.RateLimited,
            null,
            TimeSpan.FromSeconds(45),
            "rate_limited"
        );
        ClaudeQuotaService service = CreateService(accountRepository, profileStore, oauth, quotaRepository);

        ClaudeQuotaSnapshot.View result = await service.RefreshAsync(profile.ProfileId, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(ClaudeQuotaSnapshot.RefreshStatus.Stale));
        Assert.That(result.Snapshot, Is.SameAs(saved));
        Assert.That(result.RetryAfterUtc, Is.EqualTo(ClaudeTestData.Now.AddSeconds(45)));
        Assert.That(oauth.UsageTokens, Is.EqualTo(new[] { "token-a" }));
    }

    [TestCase(IClaudeOAuthClient.Outcome.Unauthorized)]
    [TestCase(IClaudeOAuthClient.Outcome.Forbidden)]
    public async Task RefreshAsync_RequiresReauthenticationWhenUsageTokenIsRejected(
        IClaudeOAuthClient.Outcome outcome
    ) {
        var setup = CreateSingleProfile(IClaudeOAuthClient.Outcome.Success);
        ClaudeAccountProfile profile = setup.Profile;
        ClaudeQuotaSnapshot saved = new(
            profile.ProfileId,
            ClaudeTestData.Now.AddHours(-1),
            [new("session", 300, 40m, ClaudeTestData.Now.AddHours(2)), new("weekly_all", 10080, 30m, ClaudeTestData.Now.AddDays(2))]
        );
        ClaudeTestQuotaRepository quotaRepository = new();
        quotaRepository.Seed(new(
            profile.ProfileId,
            saved,
            ClaudeQuotaSnapshot.RefreshStatus.Fresh,
            ClaudeTestData.Now.AddHours(-1),
            null
        ));
        setup.OAuth.UsageResults["token-a"] = new(outcome, null, null, "synthetic_usage_rejection");
        ClaudeQuotaService service = CreateService(setup.AccountRepository, setup.ProfileStore, setup.OAuth, quotaRepository);

        ClaudeQuotaSnapshot.View result = await service.RefreshAsync(profile.ProfileId, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(ClaudeQuotaSnapshot.RefreshStatus.ReauthenticationRequired));
        Assert.That(result.Snapshot, Is.SameAs(saved));
        Assert.That(setup.OAuth.UsageTokens, Is.EqualTo(new[] { "token-a" }));
    }

    private static ClaudeQuotaService CreateService(
        ClaudeTestAccountRepository accountRepository,
        ClaudeTestProfileStore profileStore,
        ClaudeTestOAuthClient oauth,
        ClaudeTestQuotaRepository quotaRepository
    ) {
        return new(
            accountRepository,
            profileStore,
            oauth,
            quotaRepository,
            ClaudeTestData.CreateOperationGate(),
            new ClaudeTestClock()
        );
    }

    private static IClaudeOAuthClient.UsageResult Success(JsonElement payload) {
        return new(IClaudeOAuthClient.Outcome.Success, payload, null, null);
    }

    private static (
        ClaudeAccountProfile Profile,
        ClaudeAccountProfile.DesktopSnapshot Snapshot,
        ClaudeTestAccountRepository AccountRepository,
        ClaudeTestProfileStore ProfileStore,
        ClaudeTestOAuthClient OAuth
    ) CreateSingleProfile(IClaudeOAuthClient.Outcome profileOutcome) {
        Guid profileId = Guid.NewGuid();
        ClaudeAccountProfile profile = ClaudeTestData.CreateProfile(profileId, "account-a", "org-a");
        ClaudeAccountProfile.DesktopSnapshot snapshot = ClaudeTestData.CreateSnapshot("account-a", "token-a");
        ClaudeTestAccountRepository accountRepository = new(profile);
        accountRepository.AddSnapshot(profileId, snapshot);
        ClaudeTestProfileStore profileStore = new();
        profileStore.SetCandidates(snapshot, "token-a");
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["token-a"] = profileOutcome == IClaudeOAuthClient.Outcome.Success
            ? ClaudeTestData.VerifiedProfile("account-a", "org-a")
            : new(profileOutcome, null, null, null, null, "synthetic_profile_failure");
        return (profile, snapshot, accountRepository, profileStore, oauth);
    }
}
