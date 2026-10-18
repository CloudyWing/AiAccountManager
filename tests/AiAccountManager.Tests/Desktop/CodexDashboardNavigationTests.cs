using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Operations;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Desktop.Components.Dashboard;
using CloudyWing.AiAccountManager.Tests.Claude;
using CloudyWing.AiAccountManager.Tests.Codex;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class CodexDashboardNavigationTests {
    [TestCase("stop-only")]
    [TestCase("callback-after-stop")]
    [TestCase("callback-before-stop")]
    public async Task ReadCodexAsync_MonitoringStopsDuringDashboardRead_PreservesMatchedQuota(
        string interleaving
    ) {
        DateTimeOffset now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
        Guid accountId = Guid.NewGuid();
        FakeIdentityFingerprinter fingerprinter = new();
        CodexAccountProfile profile = new(
            accountId,
            null,
            "p•••@example.test",
            fingerprinter.CreateFingerprint("synthetic-codex-account", "private@example.test", "chatgpt"),
            1,
            now,
            "connected",
            "synthetic-codex-account"
        );
        FakeAccountCatalogRepository catalog = new();
        catalog.SetAccounts([profile]);
        FakeQuotaSnapshotRepository quotaRepository = new();
        await quotaRepository.SeedAsync(new(
            accountId,
            profile.IdentityFingerprint,
            profile.ProfileRevision,
            new(
                now,
                new(300, 72, now.AddHours(3)),
                new(10080, 53, now.AddDays(4))
            ),
            null
        ));
        BlockingCodexIdentityReader identityReader = new();
        SyntheticCodexNotifier notifier = new();
        FakeClock clock = new(now);
        CodexLocalIdentityService identityService = new(
            catalog,
            identityReader,
            notifier,
            new SyntheticCodexProcessSource(now),
            fingerprinter,
            clock
        );
        CodexQuotaService quotaService = new(
            catalog,
            quotaRepository,
            new FakeSettingsRepository(AppSettings.Defaults),
            new FakeQuotaSessionFactory(() => throw new InvalidOperationException()),
            new QuotaResponseParser(),
            fingerprinter,
            clock,
            new ExclusiveOperationCoordinator()
        );
        (ClaudeAccountService claudeAccounts, ClaudeQuotaService claudeQuota) = CreateClaudeServices();
        DashboardOverviewReader reader = new(identityService, quotaService, claudeAccounts, claudeQuota);
        TaskCompletionSource<bool> monitoringReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler monitoringStateChanged = (_, _) => {
            CodexLocalIdentitySnapshot snapshot = identityService.CurrentSnapshot;
            if (snapshot.IsMonitoring && snapshot.Status == CodexLocalIdentityStatus.Loading) {
                monitoringReadStarted.TrySetResult(true);
            }
        };

        try {
            CodexLocalIdentitySnapshot initialSnapshot = await identityService.StartMonitoringAsync(CancellationToken.None);
            Assert.That(initialSnapshot.Status, Is.EqualTo(CodexLocalIdentityStatus.Matched));

            Task<DashboardOverviewSnapshot.Provider> dashboardRead = reader.ReadCodexAsync(CancellationToken.None);
            Task nextRead;
            try {
                nextRead = await Task.WhenAny(identityReader.DashboardReadStarted.Task, dashboardRead)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            } catch (TimeoutException) {
                Assert.Fail(
                    $"Neither the synthetic identity read nor the dashboard read completed. "
                    + $"Read count: {identityReader.ReadCount}; current identity status: {identityService.CurrentSnapshot.Status}."
                );
                return;
            }

            if (nextRead == dashboardRead) {
                DashboardOverviewSnapshot.Provider earlyProvider = await dashboardRead;
                Assert.Fail(
                    $"Dashboard read completed before the synthetic identity read started. "
                    + $"Read count: {identityReader.ReadCount}; status: {earlyProvider.IdentityStatus}."
                );
            }

            Assert.That(
                identityService.CurrentSnapshot.Status,
                Is.EqualTo(CodexLocalIdentityStatus.Matched),
                "The Dashboard identity read must not publish into the page monitor snapshot."
            );

            if (interleaving != "stop-only") {
                notifier.CapturePendingFileChangedCallback();
            }

            if (interleaving == "callback-before-stop") {
                identityService.StateChanged += monitoringStateChanged;
                notifier.DeliverPendingFileChangedCallback();
                await monitoringReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            await identityService.StopMonitoringAsync();
            if (interleaving == "callback-after-stop") {
                notifier.DeliverPendingFileChangedCallback();
            }

            identityReader.ReleaseDashboardRead.TrySetResult(true);
            DashboardOverviewSnapshot.Provider provider = await dashboardRead.WaitAsync(TimeSpan.FromSeconds(5));

            using (Assert.EnterMultipleScope()) {
                Assert.That(provider.IdentityStatus, Is.EqualTo(DashboardOverviewSnapshot.IdentityStatus.Matched));
                Assert.That(provider.QuotaStatus, Is.EqualTo(DashboardOverviewSnapshot.QuotaStatus.Fresh));
                Assert.That(provider.Windows.Select(window => window.RemainingPercent), Is.EqualTo(new decimal?[] { 72m, 53m }));
                Assert.That(quotaRepository.LoadCount, Is.EqualTo(1));
                Assert.That(identityReader.ReadCount, Is.EqualTo(2));
            }
        } finally {
            identityService.StateChanged -= monitoringStateChanged;
            await identityService.DisposeAsync();
        }
    }

    private static (ClaudeAccountService Accounts, ClaudeQuotaService Quota) CreateClaudeServices() {
        ClaudeTestAccountRepository repository = new();
        ClaudeTestProfileStore profileStore = new();
        ClaudeTestOAuthClient oauthClient = new();
        ClaudeOperationGate operationGate = ClaudeTestData.CreateOperationGate();
        ClaudeTestClock clock = new();
        ClaudeAccountService accounts = new(
            repository,
            new ClaudeTestInstallationLocator(),
            new ClaudeTestProcessController(),
            profileStore,
            oauthClient,
            new ClaudeTestFingerprinter(),
            operationGate,
            clock
        );
        ClaudeQuotaService quota = new(
            repository,
            profileStore,
            oauthClient,
            new ClaudeTestQuotaRepository(),
            operationGate,
            clock
        );
        return (accounts, quota);
    }

    private sealed class BlockingCodexIdentityReader : ICodexLocalAuthIdentityReader {
        private int readCount;

        public TaskCompletionSource<bool> DashboardReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseDashboardRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadCount => Volatile.Read(ref readCount);

        public async Task<CodexLocalAuthReadResult> ReadCurrentAsync(CancellationToken cancellationToken) {
            int currentRead = Interlocked.Increment(ref readCount);
            if (currentRead == 1) {
                return CreateSuccessResult();
            }

            if (currentRead == 2) {
                DashboardReadStarted.TrySetResult(true);
                await ReleaseDashboardRead.Task.WaitAsync(cancellationToken);
            }

            return CreateSuccessResult();
        }

        private static CodexLocalAuthReadResult CreateSuccessResult() {
            return new(
                CodexLocalAuthReadStatus.Success,
                new CodexLocalAuthIdentity("synthetic-codex-account", "private@example.test")
            );
        }
    }

    private sealed class SyntheticCodexNotifier : ICodexLocalAuthFileChangeNotifier {
        private Action? fileChangedHandlers;
        private Action? pendingFileChangedCallback;

        public event Action? FileChanged {
            add {
                fileChangedHandlers += value;
            }
            remove {
                fileChangedHandlers -= value;
            }
        }

        public event Action<bool>? MonitoringAvailabilityChanged;

        public Task StartAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            MonitoringAvailabilityChanged?.Invoke(true);
            return Task.CompletedTask;
        }

        public Task StopAsync() {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() {
            return ValueTask.CompletedTask;
        }

        public void CapturePendingFileChangedCallback() {
            pendingFileChangedCallback = fileChangedHandlers;
        }

        public void DeliverPendingFileChangedCallback() {
            pendingFileChangedCallback?.Invoke();
        }
    }

    private sealed class SyntheticCodexProcessSource(DateTimeOffset now) : ICodexDesktopProcessStatusSource {
        public Task<CodexDesktopProcessStatusSnapshot> ReadStatusAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CodexDesktopProcessStatusSnapshot(CodexDesktopProcessStatus.Unknown, now));
        }
    }
}
