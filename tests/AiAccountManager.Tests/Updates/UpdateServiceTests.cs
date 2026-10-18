using System.Globalization;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Operations;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Application.Updates;
using CloudyWing.AiAccountManager.Tests.Codex;
using CloudyWing.AiAccountManager.Windows.Storage;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Updates;

[TestFixture]
public sealed class UpdateServiceTests {
    [Test]
    public async Task Reload_AfterFailureChangedFeedDuringHandoff_RejectsPublication() {
        UpdateHarness harness = await UpdateHarness.CreateAsync(null, coordinated: true);
        using (harness.Scheduler) {
            await harness.Service.CheckAsync(CancellationToken.None);
            await harness.Service.DownloadAsync(CancellationToken.None);
            harness.Repository.FailLoad = true;
            await harness.Settings.RetryLoadAsync(CancellationToken.None);
            Assert.That(harness.Settings.Settings, Is.Null);
            harness.Repository.FailLoad = false;
            harness.Adapter.ApplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<UpdateServiceState> apply = harness.Service.ApplyAsync(CancellationToken.None);
            await harness.Adapter.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            ISettingsRepository.Snapshot stored = await harness.Context.SettingsRepository.LoadAsync(CancellationToken.None);
            await harness.Context.SettingsRepository.SaveAsync(stored.Settings with { UpdateFeedUrl = "https://updates.example.test/b" }, stored.Sha256, CancellationToken.None);
            long version = harness.Settings.UpdateFeedVersion;

            await harness.Settings.RetryLoadAsync(CancellationToken.None);

            Assert.That(harness.Settings.Settings, Is.Null);
            Assert.That(harness.Settings.UpdateFeedVersion, Is.EqualTo(version));
            Assert.That(harness.Settings.SettingsError, Is.Not.Null.And.Not.Empty);
            harness.Adapter.ApplyGate.SetResult(true);
            await apply.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Test]
    public async Task Reload_AfterFailureChangedFeed_RejectsOldDownload() {
        UpdateHarness harness = await UpdateHarness.CreateAsync(null, coordinated: true);
        using (harness.Scheduler) {
            await harness.Service.CheckAsync(CancellationToken.None);
            await harness.Service.DownloadAsync(CancellationToken.None);
            harness.Repository.FailLoad = true;
            await harness.Settings.RetryLoadAsync(CancellationToken.None);
            harness.Repository.FailLoad = false;
            long version = harness.Settings.UpdateFeedVersion;
            ISettingsRepository.Snapshot stored = await harness.Context.SettingsRepository.LoadAsync(CancellationToken.None);
            await harness.Context.SettingsRepository.SaveAsync(stored.Settings with { UpdateFeedUrl = "https://updates.example.test/b" }, stored.Sha256, CancellationToken.None);

            await harness.Settings.RetryLoadAsync(CancellationToken.None);
            UpdateServiceState result = await harness.Service.ApplyAsync(CancellationToken.None);

            Assert.That(harness.Settings.Settings!.UpdateFeedUrl, Is.EqualTo("https://updates.example.test/b"));
            Assert.That(harness.Settings.UpdateFeedVersion, Is.EqualTo(version + 1));
            Assert.That(result.Status, Is.EqualTo(UpdateStatus.Error));
            Assert.That(harness.Adapter.ApplyCount, Is.Zero);
        }
    }

    [Test]
    public async Task Reload_AfterFailureUnchangedFeed_PublishesWithoutChangingVersion() {
        UpdateHarness harness = await UpdateHarness.CreateAsync(null, coordinated: true);
        using (harness.Scheduler) {
            harness.Repository.FailLoad = true;
            await harness.Settings.RetryLoadAsync(CancellationToken.None);
            harness.Repository.FailLoad = false;
            long version = harness.Settings.UpdateFeedVersion;

            await harness.Settings.RetryLoadAsync(CancellationToken.None);

            Assert.That(harness.Settings.Settings, Is.Not.Null);
            Assert.That(harness.Settings.Settings!.UpdateFeedUrl, Is.Null);
            Assert.That(harness.Settings.UpdateFeedVersion, Is.EqualTo(version));
            Assert.That(harness.Settings.SettingsError, Is.Null);
        }
    }

    [Test]
    public async Task Reload_ChangedFeedDuringHandoff_PreservesSnapshotAndVersion() {
        UpdateHarness harness = await UpdateHarness.CreateAsync("https://updates.example.test/a", coordinated: true);
        using (harness.Scheduler) {
            await harness.Service.CheckAsync(CancellationToken.None);
            await harness.Service.DownloadAsync(CancellationToken.None);
            harness.Adapter.ApplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<UpdateServiceState> apply = harness.Service.ApplyAsync(CancellationToken.None);
            await harness.Adapter.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            AppSettings original = harness.Settings.Settings!;
            long version = harness.Settings.UpdateFeedVersion;
            ISettingsRepository.Snapshot stored = await harness.Context.SettingsRepository.LoadAsync(CancellationToken.None);
            await harness.Context.SettingsRepository.SaveAsync(stored.Settings with { UpdateFeedUrl = "https://updates.example.test/b" }, stored.Sha256, CancellationToken.None);
            bool catalogLoaded = false;
            harness.Context.Catalog.BeforeLoadAsync = _ => {
                catalogLoaded = true;
                return Task.CompletedTask;
            };
            int startupReads = harness.Startup.ReadCount;

            await harness.Settings.RetryLoadAsync(CancellationToken.None);

            Assert.That(harness.Settings.Settings, Is.SameAs(original));
            Assert.That(harness.Settings.UpdateFeedVersion, Is.EqualTo(version));
            Assert.That(harness.Settings.SettingsError, Is.EqualTo("正在套用更新，暫時無法載入變更後的更新來源。"));
            Assert.That(catalogLoaded, Is.True);
            Assert.That(harness.Startup.ReadCount, Is.EqualTo(startupReads + 1));
            Assert.That(harness.Adapter.ApplyCount, Is.EqualTo(1));
            harness.Adapter.ApplyGate.SetResult(true);
            Assert.That((await apply.WaitAsync(TimeSpan.FromSeconds(3))).Status, Is.EqualTo(UpdateStatus.Applied));
        }
    }

    [Test]
    public async Task Reload_ChangedFeedWhileApplyWaits_RejectsOldDownload() {
        UpdateHarness harness = await UpdateHarness.CreateAsync("https://updates.example.test/a", coordinated: true);
        using (harness.Scheduler) {
            await harness.Service.CheckAsync(CancellationToken.None);
            await harness.Service.DownloadAsync(CancellationToken.None);
            IOperationLease lease = await harness.Coordinator.EnterProfileOrQuotaOperationAsync(CancellationToken.None);
            Task<UpdateServiceState> apply = harness.Service.ApplyAsync(CancellationToken.None);
            ISettingsRepository.Snapshot stored = await harness.Context.SettingsRepository.LoadAsync(CancellationToken.None);
            await harness.Context.SettingsRepository.SaveAsync(stored.Settings with { UpdateFeedUrl = "https://updates.example.test/b" }, stored.Sha256, CancellationToken.None);
            await harness.Settings.RetryLoadAsync(CancellationToken.None);
            await lease.DisposeAsync();

            Assert.That((await apply.WaitAsync(TimeSpan.FromSeconds(3))).Status, Is.EqualTo(UpdateStatus.Error));
            Assert.That(harness.Settings.Settings!.UpdateFeedUrl, Is.EqualTo("https://updates.example.test/b"));
            Assert.That(harness.Adapter.ApplyCount, Is.Zero);
        }
    }

    [Test]
    public async Task Reload_UnchangedFeedDuringHandoff_Succeeds() {
        UpdateHarness harness = await UpdateHarness.CreateAsync("https://updates.example.test/a", coordinated: true);
        using (harness.Scheduler) {
            await harness.Service.CheckAsync(CancellationToken.None);
            await harness.Service.DownloadAsync(CancellationToken.None);
            harness.Adapter.ApplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<UpdateServiceState> apply = harness.Service.ApplyAsync(CancellationToken.None);
            await harness.Adapter.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            long version = harness.Settings.UpdateFeedVersion;
            ISettingsRepository.Snapshot stored = await harness.Context.SettingsRepository.LoadAsync(CancellationToken.None);
            await harness.Context.SettingsRepository.SaveAsync(stored.Settings with { QuotaRefreshIntervalMinutes = 15 }, stored.Sha256, CancellationToken.None);

            await harness.Settings.RetryLoadAsync(CancellationToken.None);

            Assert.That(harness.Settings.Settings!.QuotaRefreshIntervalMinutes, Is.EqualTo(15));
            Assert.That(harness.Settings.UpdateFeedVersion, Is.EqualTo(version));
            Assert.That(harness.Settings.SettingsError, Is.Null);
            harness.Adapter.ApplyGate.SetResult(true);
            await apply.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Test]
    public async Task Apply_AdapterCanceled_ReleasesFeedFreeze() {
        UpdateHarness harness = await UpdateHarness.CreateAsync("https://updates.example.test/a", coordinated: true);
        using (harness.Scheduler) {
            await harness.Service.CheckAsync(CancellationToken.None);
            await harness.Service.DownloadAsync(CancellationToken.None);
            harness.Adapter.ApplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenSource cancellation = new();
            Task<UpdateServiceState> apply = harness.Service.ApplyAsync(cancellation.Token);
            await harness.Adapter.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            UpdateServiceState result = await apply.WaitAsync(TimeSpan.FromSeconds(3));

            bool saved = await harness.Settings.SaveAsync(harness.Settings.Settings! with { UpdateFeedUrl = "https://updates.example.test/b" }, CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(UpdateStatus.Error));
            Assert.That(saved, Is.True);
        }
    }

    [Test]
    public async Task Apply_SaveInProgressFirst_WaitsForPublicationAndRejects() {
        UpdateHarness harness = await UpdateHarness.CreateAsync("https://updates.example.test/a", coordinated: true);
        using (harness.Scheduler) {
            await harness.Service.CheckAsync(CancellationToken.None);
            await harness.Service.DownloadAsync(CancellationToken.None);
            harness.Repository.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> save = harness.Settings.SaveAsync(harness.Settings.Settings! with { UpdateFeedUrl = "https://updates.example.test/b" }, CancellationToken.None);
            await harness.Repository.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Task<UpdateServiceState> apply = harness.Service.ApplyAsync(CancellationToken.None);
            Assert.That(apply.IsCompleted, Is.False);
            harness.Repository.SaveGate.SetResult(true);

            Assert.That(await save.WaitAsync(TimeSpan.FromSeconds(3)), Is.True);
            Assert.That((await apply.WaitAsync(TimeSpan.FromSeconds(3))).Status, Is.EqualTo(UpdateStatus.Error));
            Assert.That(harness.Adapter.ApplyCount, Is.Zero);
        }
    }

    [Test]
    public async Task Save_ApplyHandoffFirst_RejectsBeforeRepositorySave() {
        UpdateHarness harness = await UpdateHarness.CreateAsync("https://updates.example.test/a", coordinated: true);
        using (harness.Scheduler) {
            await harness.Service.CheckAsync(CancellationToken.None);
            await harness.Service.DownloadAsync(CancellationToken.None);
            harness.Adapter.ApplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<UpdateServiceState> apply = harness.Service.ApplyAsync(CancellationToken.None);
            await harness.Adapter.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            bool saved = await harness.Settings.SaveAsync(harness.Settings.Settings! with { UpdateFeedUrl = "https://updates.example.test/b" }, CancellationToken.None);

            Assert.That(saved, Is.False);
            Assert.That(harness.Repository.SaveStarted.Task.IsCompleted, Is.False);
            harness.Adapter.ApplyGate.SetResult(true);
            Assert.That((await apply.WaitAsync(TimeSpan.FromSeconds(3))).Status, Is.EqualTo(UpdateStatus.Applied));
        }
    }

    [Test]
    public async Task Apply_FeedSavedFirst_RejectsOldDownload() {
        UpdateHarness harness = await UpdateHarness.CreateAsync("https://updates.example.test/a", coordinated: true);
        using (harness.Scheduler) {
            await harness.Service.CheckAsync(CancellationToken.None);
            await harness.Service.DownloadAsync(CancellationToken.None);
            bool saved = await harness.Settings.SaveAsync(harness.Settings.Settings! with { UpdateFeedUrl = "https://updates.example.test/b" }, CancellationToken.None);

            UpdateServiceState result = await harness.Service.ApplyAsync(CancellationToken.None);

            Assert.That(saved, Is.True);
            Assert.That(result.Status, Is.EqualTo(UpdateStatus.Error));
            Assert.That(harness.Adapter.ApplyCount, Is.Zero);
        }
    }

    [Test]
    public async Task Save_FeedChangedDuringHandoff_RejectsWithoutPersisting() {
        UpdateHarness harness = await UpdateHarness.CreateAsync("https://updates.example.test/a", coordinated: true);
        using (harness.Scheduler) {
            await harness.Service.CheckAsync(CancellationToken.None);
            await harness.Service.DownloadAsync(CancellationToken.None);
            harness.Adapter.ApplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<UpdateServiceState> apply = harness.Service.ApplyAsync(CancellationToken.None);
            await harness.Adapter.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            bool saved = await harness.Settings.SaveAsync(harness.Settings.Settings! with { UpdateFeedUrl = "https://updates.example.test/b" }, CancellationToken.None);
            ISettingsRepository.Snapshot snapshot = await harness.Context.SettingsRepository.LoadAsync(CancellationToken.None);
            harness.Adapter.ApplyGate.SetResult(true);
            await apply;
            bool savedAfterHandoff = await harness.Settings.SaveAsync(harness.Settings.Settings! with { UpdateFeedUrl = "https://updates.example.test/b" }, CancellationToken.None);

            Assert.That(saved, Is.False);
            Assert.That(savedAfterHandoff, Is.False);
            Assert.That(harness.Settings.SettingsError, Does.Contain("正在套用更新"));
            Assert.That(snapshot.Settings.UpdateFeedUrl, Is.EqualTo("https://updates.example.test/a"));
        }
    }

    [Test]
    public async Task Apply_FeedChangedWhileWaitingForAccountOperation_RejectsOldDownload() {
        UpdateHarness harness = await UpdateHarness.CreateAsync("https://updates.example.test/a", coordinated: true);
        using (harness.Scheduler) {
            await harness.Service.CheckAsync(CancellationToken.None);
            await harness.Service.DownloadAsync(CancellationToken.None);
            IOperationLease lease = await harness.Coordinator.EnterProfileOrQuotaOperationAsync(CancellationToken.None);
            Task<UpdateServiceState> apply = harness.Service.ApplyAsync(CancellationToken.None);
            bool saved = await harness.Settings.SaveAsync(harness.Settings.Settings! with { UpdateFeedUrl = "https://updates.example.test/b" }, CancellationToken.None);
            await lease.DisposeAsync();

            UpdateServiceState result = await apply.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.That(saved, Is.True);
            Assert.That(result.Status, Is.EqualTo(UpdateStatus.Error));
            Assert.That(harness.Adapter.ApplyCount, Is.Zero);
        }
    }

    [Test]
    public async Task Apply_AdapterThrows_ReleasesFeedFreeze() {
        UpdateHarness harness = await UpdateHarness.CreateAsync("https://updates.example.test/a", coordinated: true);
        using (harness.Scheduler) {
            await harness.Service.CheckAsync(CancellationToken.None);
            await harness.Service.DownloadAsync(CancellationToken.None);
            harness.Adapter.FailApply = true;

            UpdateServiceState result = await harness.Service.ApplyAsync(CancellationToken.None);
            bool saved = await harness.Settings.SaveAsync(harness.Settings.Settings! with { UpdateFeedUrl = "https://updates.example.test/b" }, CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(UpdateStatus.Error));
            Assert.That(saved, Is.True);
        }
    }

    [Test]
    public async Task Save_UnchangedFeedDuringHandoff_Succeeds() {
        UpdateHarness harness = await UpdateHarness.CreateAsync("https://updates.example.test/a", coordinated: true);
        using (harness.Scheduler) {
            await harness.Service.CheckAsync(CancellationToken.None);
            await harness.Service.DownloadAsync(CancellationToken.None);
            harness.Adapter.ApplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<UpdateServiceState> apply = harness.Service.ApplyAsync(CancellationToken.None);
            await harness.Adapter.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            bool saved = await harness.Settings.SaveAsync(harness.Settings.Settings! with { QuotaRefreshIntervalMinutes = 15 }, CancellationToken.None);
            harness.Adapter.ApplyGate.SetResult(true);
            await apply;

            Assert.That(saved, Is.True);
            Assert.That((await harness.Context.SettingsRepository.LoadAsync(CancellationToken.None)).Settings.QuotaRefreshIntervalMinutes, Is.EqualTo(15));
        }
    }

    [Test]
    public async Task ApplyWaitsForQuotaRefreshAndCallsRestartOnce() {
        UpdateHarness harness = await UpdateHarness.CreateAsync("https://updates.example.test/feed");
        using (harness.Scheduler) {
            _ = await harness.Service.CheckAsync(CancellationToken.None);
            _ = await harness.Service.DownloadAsync(CancellationToken.None);
            IOperationLease quotaLease = await harness.Coordinator.EnterProfileOrQuotaOperationAsync(
                Guid.NewGuid(),
                CancellationToken.None
            );

            Task<UpdateServiceState> apply = harness.Service.ApplyAsync(CancellationToken.None);
            await WaitUntilAsync(() => harness.Scheduler.IsPaused);
            await Task.Delay(50);
            Assert.That(harness.Adapter.ApplyCount, Is.Zero);

            await quotaLease.DisposeAsync();
            UpdateServiceState result = await apply.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.That(result.Status, Is.EqualTo(UpdateStatus.Applied));
            Assert.That(harness.Adapter.ApplyCount, Is.EqualTo(1));
            Assert.That(harness.Adapter.RestartCount, Is.EqualTo(1));
            Assert.That(harness.Scheduler.IsPaused, Is.False);
        }
    }

    [Test]
    public async Task NullFeedUsesDefaultUpdateSource() {
        UpdateHarness harness = await UpdateHarness.CreateAsync(null);
        using (harness.Scheduler) {
            UpdateServiceState result = await harness.Service.CheckAsync(CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(UpdateStatus.Available));
            Assert.That(harness.Adapter.CheckCount, Is.EqualTo(1));
            Assert.That(harness.Adapter.LastFeedUri?.AbsoluteUri, Is.EqualTo(UpdateFeedResolver.DefaultFeedUrl));
            Assert.That(harness.Adapter.DownloadCount, Is.Zero);
            Assert.That(harness.Adapter.ApplyCount, Is.Zero);
        }
    }

    [Test]
    public async Task ChangingAndClearingFeed_UsesSavedSource() {
        UpdateHarness harness = await UpdateHarness.CreateAsync("https://updates.example.test/custom");
        using (harness.Scheduler) {
            _ = await harness.Service.CheckAsync(CancellationToken.None);
            Assert.That(harness.Adapter.LastFeedUri?.AbsoluteUri, Is.EqualTo("https://updates.example.test/custom"));

            ISettingsRepository.Snapshot snapshot = await harness.Context.SettingsRepository.LoadAsync(CancellationToken.None);
            ISettingsRepository.SaveResult saved = await harness.Context.SettingsRepository.SaveAsync(
                snapshot.Settings with { UpdateFeedUrl = null }, snapshot.Sha256, CancellationToken.None
            );
            _ = await harness.Service.CheckAsync(CancellationToken.None);

            Assert.That(saved.Saved, Is.True);
            Assert.That(harness.Adapter.LastFeedUri?.AbsoluteUri, Is.EqualTo(UpdateFeedResolver.DefaultFeedUrl));
        }
    }

    [TestCase("http://updates.example.test/feed")]
    [TestCase("https://user:secret@updates.example.test/feed")]
    [TestCase("relative/feed")]
    public async Task InvalidFeedDoesNotCallUpdateAdapter(string feedUrl) {
        UpdateHarness harness = await UpdateHarness.CreateAsync(feedUrl);
        using (harness.Scheduler) {
            UpdateServiceState result = await harness.Service.CheckAsync(CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(UpdateStatus.Error));
            Assert.That(harness.Adapter.CheckCount, Is.Zero);
        }
    }

    [Test]
    public async Task CancelingDownloadDoesNotPermitApply() {
        UpdateHarness harness = await UpdateHarness.CreateAsync("https://updates.example.test/feed");
        using (harness.Scheduler) {
            _ = await harness.Service.CheckAsync(CancellationToken.None);
            using CancellationTokenSource cancellation = new();
            harness.Adapter.DownloadGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<UpdateServiceState> download = harness.Service.DownloadAsync(cancellation.Token);
            await harness.Adapter.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            UpdateServiceState canceled = await download.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.That(canceled.Status, Is.EqualTo(UpdateStatus.Error));
            Assert.That(harness.Service.State.Status, Is.EqualTo(UpdateStatus.Error));
            Assert.That(harness.Adapter.ApplyCount, Is.Zero);
        }
    }

    [Test]
    public async Task FeedChangeAfterDownloadRequiresNewCheckBeforeApply() {
        UpdateHarness harness = await UpdateHarness.CreateAsync("https://updates.example.test/old");
        using (harness.Scheduler) {
            _ = await harness.Service.CheckAsync(CancellationToken.None);
            _ = await harness.Service.DownloadAsync(CancellationToken.None);
            ISettingsRepository.SaveResult save = await harness.Context.SettingsRepository.SaveAsync(
                AppSettings.Defaults with { UpdateFeedUrl = "https://updates.example.test/new" },
                (await harness.Context.SettingsRepository.LoadAsync(CancellationToken.None)).Sha256,
                CancellationToken.None
            );
            bool saved = save.Saved;

            Assert.That(saved, Is.True);
            UpdateServiceState result = await harness.Service.ApplyAsync(CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(UpdateStatus.Error));
            Assert.That(harness.Adapter.ApplyCount, Is.Zero);
        }
    }

    [Test]
    public async Task FeedChangesAwayAndBack_InvalidatesDownloadedUpdate() {
        const string feedA = "https://updates.example.test/a";
        UpdateHarness harness = await UpdateHarness.CreateAsync(feedA, coordinated: true);
        using (harness.Scheduler) {
            _ = await harness.Service.CheckAsync(CancellationToken.None);
            _ = await harness.Service.DownloadAsync(CancellationToken.None);

            bool savedB = await harness.Settings.SaveAsync(
                harness.Settings.Settings! with { UpdateFeedUrl = "https://updates.example.test/b" }, CancellationToken.None
            );
            bool savedA = await harness.Settings.SaveAsync(
                harness.Settings.Settings! with { UpdateFeedUrl = feedA }, CancellationToken.None
            );
            UpdateServiceState invalidated = harness.Service.State;
            UpdateServiceState result = await harness.Service.ApplyAsync(CancellationToken.None);

            Assert.That(savedB && savedA, Is.True);
            Assert.That(invalidated.Status, Is.EqualTo(UpdateStatus.Error));
            Assert.That(result.Status, Is.EqualTo(UpdateStatus.Error));
            Assert.That(harness.Adapter.ApplyCount, Is.Zero);
            UpdateServiceState checkedAgain = await harness.Service.CheckAsync(CancellationToken.None);
            UpdateServiceState downloadedAgain = await harness.Service.DownloadAsync(CancellationToken.None);
            Assert.That(checkedAgain.Status, Is.EqualTo(UpdateStatus.Available));
            Assert.That(downloadedAgain.Status, Is.EqualTo(UpdateStatus.ReadyToApply));
        }
    }

    [TestCase("https://updates.example.test?channel=preview")]
    [TestCase("https://updates.example.test/Channel/Stable?channel=preview&path=%2Fbeta")]
    public async Task CheckKeepsCanonicalFeedPathAndQuery(string feedUrl) {
        UpdateHarness harness = await UpdateHarness.CreateAsync(feedUrl);
        using (harness.Scheduler) {
            UpdateServiceState result = await harness.Service.CheckAsync(CancellationToken.None);

            Assert.That(result.FeedUri, Is.EqualTo(new Uri(feedUrl).AbsoluteUri));
            Assert.That(harness.Adapter.LastFeedUri?.AbsoluteUri, Is.EqualTo(new Uri(feedUrl).AbsoluteUri));
        }
    }

    [Test]
    public async Task UpdateServiceApplyPreservesRepositorySettingsAndCatalog() {
        string dataRoot = Path.Combine(Path.GetTempPath(), "AiAccountManagerTests", Guid.NewGuid().ToString("N"));

        try {
            CurrentUserDataDirectory dataDirectory = new(dataRoot);
            AtomicJsonFileStore fileStore = new(dataDirectory);
            JsonSettingsRepository settingsRepository = new(fileStore);
            JsonAccountCatalogRepository catalogRepository = new(fileStore);
            const string feedUrl = "https://updates.example.test/channel/stable?ring=preview";

            ISettingsRepository.Snapshot initialSettings = await settingsRepository.LoadAsync(CancellationToken.None);
            AppSettings configuredSettings = initialSettings.Settings with {
                QuotaRefreshIntervalMinutes = 15,
                UpdateFeedUrl = feedUrl
            };
            ISettingsRepository.SaveResult settingsSave = await settingsRepository.SaveAsync(
                configuredSettings,
                initialSettings.Sha256,
                CancellationToken.None
            );
            Assert.That(settingsSave.Saved, Is.True);

            IAccountCatalogRepository.Snapshot initialCatalog = await catalogRepository.LoadAsync(CancellationToken.None);
            CodexAccountProfile profile = new(
                Guid.NewGuid(),
                "Work",
                "w•••@example.test",
                "identity-fingerprint",
                1,
                DateTimeOffset.UtcNow,
                "connected",
                "external-account-id"
            );
            IAccountCatalogRepository.SaveResult catalogSave = await catalogRepository.SaveAsync(
                [profile],
                initialCatalog.Sha256,
                CancellationToken.None
            );
            Assert.That(catalogSave.Saved, Is.True);

            ISettingsRepository.Snapshot settingsBefore = await settingsRepository.LoadAsync(CancellationToken.None);
            IAccountCatalogRepository.Snapshot catalogBefore = await catalogRepository.LoadAsync(CancellationToken.None);
            SettingsService settingsService = new(settingsRepository, catalogRepository);
            await settingsService.InitializeAsync(CancellationToken.None);

            QuotaTestContext testContext = new(includeAccount: false);
            ExclusiveOperationCoordinator coordinator = new();
            CodexQuotaService quotaService = new(
                catalogRepository,
                new JsonQuotaSnapshotRepository(fileStore),
                settingsRepository,
                new FakeQuotaSessionFactory(() => throw new AssertionException("The updater test must not start quota sessions.")),
                new QuotaResponseParser(),
                testContext.Fingerprinter,
                testContext.Clock,
                coordinator
            );
            using CodexQuotaScheduler scheduler = new(settingsService, catalogRepository, quotaService);
            FakeUpdateAdapter adapter = new();
            UpdateService service = new(settingsRepository, adapter, coordinator, scheduler, settingsService);

            _ = await service.CheckAsync(CancellationToken.None);
            _ = await service.DownloadAsync(CancellationToken.None);
            UpdateServiceState applied = await service.ApplyAsync(CancellationToken.None);

            ISettingsRepository.Snapshot settingsAfter = await settingsRepository.LoadAsync(CancellationToken.None);
            IAccountCatalogRepository.Snapshot catalogAfter = await catalogRepository.LoadAsync(CancellationToken.None);

            Assert.That(applied.Status, Is.EqualTo(UpdateStatus.Applied));
            Assert.That(settingsAfter.Settings, Is.EqualTo(settingsBefore.Settings));
            Assert.That(catalogAfter.Accounts, Is.EqualTo(catalogBefore.Accounts));
            Assert.That(adapter.ApplyCount, Is.EqualTo(1));
            Assert.That(adapter.RestartCount, Is.EqualTo(1));
        } finally {
            if (Directory.Exists(dataRoot)) {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition) {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition()) {
            if (DateTimeOffset.UtcNow >= deadline) {
                throw new AssertionException("The update service did not pause the quota scheduler.");
            }

            await Task.Delay(10);
        }
    }
}

internal sealed class UpdateHarness {
    private UpdateHarness(
        QuotaTestContext context,
        ControlledSettingsRepository repository,
        FakeStartupRegistration startup,
        FakeUpdateAdapter adapter,
        ExclusiveOperationCoordinator coordinator,
        SettingsService settings,
        CodexQuotaScheduler scheduler,
        UpdateService service
    ) {
        Context = context;
        Repository = repository;
        Startup = startup;
        Adapter = adapter;
        Coordinator = coordinator;
        Settings = settings;
        Scheduler = scheduler;
        Service = service;
    }

    public QuotaTestContext Context { get; }

    public ControlledSettingsRepository Repository { get; }

    public FakeStartupRegistration Startup { get; }

    public FakeUpdateAdapter Adapter { get; }

    public ExclusiveOperationCoordinator Coordinator { get; }

    public SettingsService Settings { get; }

    public CodexQuotaScheduler Scheduler { get; }

    public UpdateService Service { get; }

    public static async Task<UpdateHarness> CreateAsync(string? feedUrl, bool coordinated = false) {
        QuotaTestContext context = new(updateFeedUrl: feedUrl, includeAccount: false);
        ExclusiveOperationCoordinator coordinator = new();
        UpdateFeedApplyCoordinator? feedCoordinator = coordinated ? new() : null;
        ControlledSettingsRepository repository = new(context.SettingsRepository);
        FakeStartupRegistration startup = new();
        SettingsService settings = new(repository, context.Catalog, startup, feedCoordinator);
        await settings.InitializeAsync(CancellationToken.None);
        CodexQuotaScheduler scheduler = new(settings, context.Catalog, context.Service);
        FakeUpdateAdapter adapter = new();
        UpdateService service = new(repository, adapter, coordinator, scheduler, settings, feedCoordinator);
        return new UpdateHarness(context, repository, startup, adapter, coordinator, settings, scheduler, service);
    }
}

internal sealed class FakeStartupRegistration : SettingsService.IStartupRegistration {
    public int ReadCount { get; private set; }

    public string? Read() {
        ReadCount++;
        return null;
    }

    public void SetEnabled(bool enabled) { }

    public void Restore(string? value) { }
}

internal sealed class ControlledSettingsRepository(ISettingsRepository inner) : ISettingsRepository {
    public bool FailLoad { get; set; }
    public TaskCompletionSource<bool> SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool>? SaveGate { get; set; }

    public Task<ISettingsRepository.Snapshot> LoadAsync(CancellationToken cancellationToken) {
        if (FailLoad) {
            throw new IOException("Simulated settings load failure.");
        }
        return inner.LoadAsync(cancellationToken);
    }

    public async Task<ISettingsRepository.SaveResult> SaveAsync(AppSettings settings, string expectedSha256, CancellationToken cancellationToken) {
        SaveStarted.TrySetResult(true);
        if (SaveGate is not null) {
            await SaveGate.Task.WaitAsync(cancellationToken);
        }
        return await inner.SaveAsync(settings, expectedSha256, cancellationToken);
    }
}

internal sealed class FakeUpdateAdapter : IUpdateAdapter {
    public Uri? LastFeedUri { get; private set; }

    public int CheckCount { get; private set; }

    public int DownloadCount { get; private set; }

    public int ApplyCount { get; private set; }

    public int RestartCount { get; private set; }

    public TaskCompletionSource<bool> DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool>? DownloadGate { get; set; }

    public TaskCompletionSource<bool> ApplyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool>? ApplyGate { get; set; }

    public bool FailApply { get; set; }

    public Task<UpdateCheckResult> CheckAsync(Uri feedUri, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        CheckCount++;
        LastFeedUri = feedUri;
        return Task.FromResult(new UpdateCheckResult(true, "0.2.0"));
    }

    public async Task DownloadAsync(Uri feedUri, IProgress<int> progress, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        DownloadCount++;
        LastFeedUri = feedUri;
        DownloadStarted.TrySetResult(true);
        progress.Report(45);
        if (DownloadGate is not null) {
            await DownloadGate.Task.WaitAsync(cancellationToken);
        }
    }

    public async Task ApplyAndRestartAsync(Uri feedUri, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyCount++;
        RestartCount++;
        LastFeedUri = feedUri;
        ApplyStarted.TrySetResult(true);
        if (ApplyGate is not null) {
            await ApplyGate.Task.WaitAsync(cancellationToken);
        }
        if (FailApply) {
            throw new InvalidOperationException("Apply failed.");
        }
    }
}
