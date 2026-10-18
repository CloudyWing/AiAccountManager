using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Runtime;
using CloudyWing.AiAccountManager.Application.Security;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Codex;

[TestFixture]
public sealed class CodexLocalIdentityServiceTests {
    internal static CodexLocalIdentityService CreateRenderingService(IReadOnlyList<CodexAccountProfile> profiles)
        => new Fixture(profiles).Service;

    [Test]
    public async Task ReadCurrentSnapshotAsync_IdentityMatchesByAccountId_ReturnsMaskedUniqueProfile() {
        Guid accountId = Guid.NewGuid();
        FakeFingerprinter fingerprinter = new();
        CodexAccountProfile profile = CreateProfile(
            accountId,
            fingerprinter.CreateFingerprint("account-1234", "private@example.test", "chatgpt"),
            "fallback:private@example.test"
        );
        Fixture fixture = new([profile]);
        fixture.Reader.Result = new(
            CodexLocalAuthReadStatus.Success,
            new CodexLocalAuthIdentity("account-1234", "private@example.test")
        );

        CodexLocalIdentitySnapshot snapshot = await fixture.Service.ReadCurrentSnapshotAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(snapshot.Status, Is.EqualTo(CodexLocalIdentityStatus.Matched));
            Assert.That(snapshot.MatchedProfileAccountId, Is.EqualTo(accountId));
            Assert.That(snapshot.MaskedIdentity, Is.EqualTo("p•••@example.test"));
        }
    }

    [Test]
    public async Task ReadCurrentSnapshotAsync_AccountIdExists_DoesNotFallbackToEmail() {
        Guid accountId = Guid.NewGuid();
        Fixture fixture = new([
            CreateProfile(accountId, "other-account-fingerprint", "email:private@example.test|mode:chatgpt")
        ]);
        fixture.Reader.Result = new(
            CodexLocalAuthReadStatus.Success,
            new CodexLocalAuthIdentity("account-1234", "private@example.test")
        );

        CodexLocalIdentitySnapshot snapshot = await fixture.Service.ReadCurrentSnapshotAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(snapshot.Status, Is.EqualTo(CodexLocalIdentityStatus.Unmatched));
            Assert.That(snapshot.MatchedProfileAccountId, Is.Null);
        }
    }

    [Test]
    public async Task ReadCurrentSnapshotAsync_EmailOnlyIdentity_MatchesFallbackFingerprint() {
        Guid accountId = Guid.NewGuid();
        Fixture fixture = new([
            CreateProfile(accountId, "primary-fingerprint", "email:private@example.test|mode:chatgpt")
        ]);
        fixture.Reader.Result = new(
            CodexLocalAuthReadStatus.Success,
            new CodexLocalAuthIdentity(null, "private@example.test")
        );

        CodexLocalIdentitySnapshot snapshot = await fixture.Service.ReadCurrentSnapshotAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(snapshot.Status, Is.EqualTo(CodexLocalIdentityStatus.Matched));
            Assert.That(snapshot.MatchedProfileAccountId, Is.EqualTo(accountId));
        }
    }

    [Test]
    public async Task ReadCurrentSnapshotAsync_ReaderFails_ClearsPreviouslyMatchedProfile() {
        Guid accountId = Guid.NewGuid();
        FakeFingerprinter fingerprinter = new();
        Fixture fixture = new([
            CreateProfile(
                accountId,
                fingerprinter.CreateFingerprint("account-1234", null, "chatgpt"),
                null
            )
        ]);
        fixture.Reader.Result = new(
            CodexLocalAuthReadStatus.Success,
            new CodexLocalAuthIdentity("account-1234", null)
        );

        CodexLocalIdentitySnapshot firstSnapshot = await fixture.Service.ReadCurrentSnapshotAsync(CancellationToken.None);
        fixture.Reader.Result = new(CodexLocalAuthReadStatus.FileMissing, null);
        CodexLocalIdentitySnapshot secondSnapshot = await fixture.Service.ReadCurrentSnapshotAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(firstSnapshot.Status, Is.EqualTo(CodexLocalIdentityStatus.Matched));
            Assert.That(secondSnapshot.Status, Is.EqualTo(CodexLocalIdentityStatus.FileMissing));
            Assert.That(secondSnapshot.MatchedProfileAccountId, Is.Null);
        }
    }

    [Test]
    public async Task ReadCurrentSnapshotAsync_NewRequestCancelsOlderReadAndPublishesOnlyLatestResult() {
        Guid accountId = Guid.NewGuid();
        FakeFingerprinter fingerprinter = new();
        Fixture fixture = new([
            CreateProfile(accountId, fingerprinter.CreateFingerprint("latest-account", null, "chatgpt"), null)
        ]);
        TaskCompletionSource<bool> firstReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int readSequence = 0;
        fixture.Reader.ReadHandler = async cancellationToken => {
            if (Interlocked.Increment(ref readSequence) == 1) {
                firstReadStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new(CodexLocalAuthReadStatus.Success, new CodexLocalAuthIdentity("latest-account", null));
        };

        Task<CodexLocalIdentitySnapshot> olderRead = fixture.Service.ReadCurrentSnapshotAsync(CancellationToken.None);
        await firstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<CodexLocalIdentitySnapshot> latestRead = fixture.Service.ReadCurrentSnapshotAsync(CancellationToken.None);
        CodexLocalIdentitySnapshot latestSnapshot = await latestRead.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.CatchAsync<OperationCanceledException>(async () => await olderRead);
        CodexLocalIdentitySnapshot subsequentSnapshot = await fixture.Service.ReadCurrentSnapshotAsync(
            CancellationToken.None
        ).WaitAsync(TimeSpan.FromSeconds(2));

        using (Assert.EnterMultipleScope()) {
            Assert.That(latestSnapshot.Status, Is.EqualTo(CodexLocalIdentityStatus.Matched));
            Assert.That(latestSnapshot.MatchedProfileAccountId, Is.EqualTo(accountId));
            Assert.That(fixture.Service.CurrentSnapshot, Is.EqualTo(subsequentSnapshot));
            Assert.That(subsequentSnapshot.Status, Is.EqualTo(CodexLocalIdentityStatus.Matched));
            Assert.That(subsequentSnapshot.MatchedProfileAccountId, Is.EqualTo(accountId));
        }
    }

    [Test]
    public async Task ReadCurrentSnapshotAsync_MultipleProfilesMatch_ReturnsAmbiguousWithoutBadge() {
        FakeFingerprinter fingerprinter = new();
        string fingerprint = fingerprinter.CreateFingerprint("account-1234", null, "chatgpt");
        Fixture fixture = new([
            CreateProfile(Guid.NewGuid(), fingerprint, null),
            CreateProfile(Guid.NewGuid(), fingerprint, null)
        ]);
        fixture.Reader.Result = new(
            CodexLocalAuthReadStatus.Success,
            new CodexLocalAuthIdentity("account-1234", null)
        );

        CodexLocalIdentitySnapshot snapshot = await fixture.Service.ReadCurrentSnapshotAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(snapshot.Status, Is.EqualTo(CodexLocalIdentityStatus.Ambiguous));
            Assert.That(snapshot.MatchedProfileAccountId, Is.Null);
        }
    }

    [Test]
    public async Task ReadCurrentSnapshotAsync_CatalogChangesOnce_ReloadsBeforeMatching() {
        FakeFingerprinter fingerprinter = new();
        Guid newAccountId = Guid.NewGuid();
        string fingerprint = fingerprinter.CreateFingerprint("account-1234", null, "chatgpt");
        FakeAccountCatalogRepository catalog = new(
            new IAccountCatalogRepository.Snapshot([], "sha-old"),
            new IAccountCatalogRepository.Snapshot([CreateProfile(newAccountId, fingerprint, null)], "sha-new"),
            new IAccountCatalogRepository.Snapshot([CreateProfile(newAccountId, fingerprint, null)], "sha-new")
        );
        Fixture fixture = new(catalog);
        fixture.Reader.Result = new(
            CodexLocalAuthReadStatus.Success,
            new CodexLocalAuthIdentity("account-1234", null)
        );

        CodexLocalIdentitySnapshot snapshot = await fixture.Service.ReadCurrentSnapshotAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(snapshot.Status, Is.EqualTo(CodexLocalIdentityStatus.Matched));
            Assert.That(snapshot.MatchedProfileAccountId, Is.EqualTo(newAccountId));
            Assert.That(catalog.LoadCount, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task ReadCurrentSnapshotAsync_CatalogChangesTwice_ClearsMatchAndReturnsUnavailable() {
        FakeFingerprinter fingerprinter = new();
        string fingerprint = fingerprinter.CreateFingerprint("account-1234", null, "chatgpt");
        FakeAccountCatalogRepository catalog = new(
            new IAccountCatalogRepository.Snapshot([CreateProfile(Guid.NewGuid(), fingerprint, null)], "sha-1"),
            new IAccountCatalogRepository.Snapshot([CreateProfile(Guid.NewGuid(), fingerprint, null)], "sha-2"),
            new IAccountCatalogRepository.Snapshot([CreateProfile(Guid.NewGuid(), fingerprint, null)], "sha-3")
        );
        Fixture fixture = new(catalog);
        fixture.Reader.Result = new(
            CodexLocalAuthReadStatus.Success,
            new CodexLocalAuthIdentity("account-1234", null)
        );

        CodexLocalIdentitySnapshot snapshot = await fixture.Service.ReadCurrentSnapshotAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(snapshot.Status, Is.EqualTo(CodexLocalIdentityStatus.CatalogUnavailable));
            Assert.That(snapshot.MatchedProfileAccountId, Is.Null);
        }
    }

    [Test]
    public async Task ReadProcessStatusAsync_RunningStatus_DoesNotChangeIdentitySnapshot() {
        Fixture fixture = new([]);
        fixture.Reader.Result = new(CodexLocalAuthReadStatus.FileMissing, null);
        await fixture.Service.ReadCurrentSnapshotAsync(CancellationToken.None);
        CodexLocalIdentitySnapshot before = fixture.Service.CurrentSnapshot;
        fixture.ProcessSource.Result = new(CodexDesktopProcessStatus.Running, fixture.Clock.UtcNow);

        CodexDesktopProcessStatusSnapshot processSnapshot = await fixture.Service.ReadProcessStatusAsync(
            CancellationToken.None
        );

        using (Assert.EnterMultipleScope()) {
            Assert.That(processSnapshot.Status, Is.EqualTo(CodexDesktopProcessStatus.Running));
            Assert.That(fixture.Service.CurrentSnapshot, Is.EqualTo(before));
            Assert.That(fixture.Service.ProcessStatusSnapshot.Status, Is.EqualTo(CodexDesktopProcessStatus.Running));
        }
    }

    [Test]
    public async Task StartMonitoringAsync_StopAndRestart_RecreatesWatcherAndReadsAgain() {
        Fixture fixture = new([]);
        fixture.Reader.Result = new(CodexLocalAuthReadStatus.FileMissing, null);

        await fixture.Service.StartMonitoringAsync(CancellationToken.None);
        await fixture.Service.StopMonitoringAsync();
        await fixture.Service.StartMonitoringAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(fixture.Notifier.StartCount, Is.EqualTo(2));
            Assert.That(fixture.Notifier.StopCount, Is.EqualTo(1));
            Assert.That(fixture.Reader.ReadCount, Is.EqualTo(2));
            Assert.That(fixture.Service.CurrentSnapshot.IsMonitoring, Is.True);
        }
    }

    [Test]
    public async Task StartMonitoringAsync_FileChangeSupersedesInitialRead_ReturnsAndPublishesLatestSnapshot() {
        Guid accountId = Guid.NewGuid();
        FakeFingerprinter fingerprinter = new();
        Fixture fixture = new([
            CreateProfile(accountId, fingerprinter.CreateFingerprint("latest-account", null, "chatgpt"), null)
        ]);
        TaskCompletionSource<bool> initialReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CodexLocalIdentitySnapshot> latestSnapshotPublished = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int readSequence = 0;
        fixture.Reader.ReadHandler = async cancellationToken => {
            if (Interlocked.Increment(ref readSequence) == 1) {
                initialReadStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new(CodexLocalAuthReadStatus.Success, new CodexLocalAuthIdentity("latest-account", null));
        };
        fixture.Service.StateChanged += (_, _) => {
            CodexLocalIdentitySnapshot snapshot = fixture.Service.CurrentSnapshot;
            if (snapshot.Status == CodexLocalIdentityStatus.Matched) {
                latestSnapshotPublished.TrySetResult(snapshot);
            }
        };
        fixture.ProcessSource.Result = new(
            CodexDesktopProcessStatus.Running,
            DateTimeOffset.Parse("2026-10-25T12:00:00Z")
        );

        Task<CodexLocalIdentitySnapshot> startTask = fixture.Service.StartMonitoringAsync(CancellationToken.None);
        try {
            await initialReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            fixture.Notifier.RaiseFileChanged();
            CodexLocalIdentitySnapshot startSnapshot = await startTask.WaitAsync(TimeSpan.FromSeconds(2));
            CodexLocalIdentitySnapshot latestSnapshot = await latestSnapshotPublished.Task.WaitAsync(
                TimeSpan.FromSeconds(2)
            );
            CodexDesktopProcessStatusSnapshot processStatus = await fixture.Service.ReadProcessStatusAsync(
                CancellationToken.None
            );

            using (Assert.EnterMultipleScope()) {
                Assert.That(startSnapshot.IsMonitoring, Is.True);
                Assert.That(latestSnapshot.Status, Is.EqualTo(CodexLocalIdentityStatus.Matched));
                Assert.That(latestSnapshot.MatchedProfileAccountId, Is.EqualTo(accountId));
                Assert.That(fixture.Service.CurrentSnapshot, Is.EqualTo(latestSnapshot));
                Assert.That(processStatus.Status, Is.EqualTo(CodexDesktopProcessStatus.Running));
            }
        } finally {
            await fixture.Service.StopMonitoringAsync();
        }
    }

    [Test]
    public async Task StartMonitoringAsync_CallerCancelsInitialRead_PropagatesCancellation() {
        Fixture fixture = new([]);
        TaskCompletionSource<bool> initialReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Reader.ReadHandler = async cancellationToken => {
            initialReadStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(CodexLocalAuthReadStatus.FileMissing, null);
        };
        using CancellationTokenSource cancellation = new();

        Task<CodexLocalIdentitySnapshot> startTask = fixture.Service.StartMonitoringAsync(cancellation.Token);
        try {
            await initialReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            await Assert.CatchAsync<OperationCanceledException>(
                async () => await startTask.WaitAsync(TimeSpan.FromSeconds(2))
            );
        } finally {
            await fixture.Service.StopMonitoringAsync();
        }
    }

    [Test]
    public async Task FileChangedEvent_ReadsCurrentSnapshotAgain() {
        Fixture fixture = new([]);
        fixture.Reader.Result = new(CodexLocalAuthReadStatus.FileMissing, null);
        await fixture.Service.StartMonitoringAsync(CancellationToken.None);

        fixture.Notifier.RaiseFileChanged();

        using (Assert.EnterMultipleScope()) {
            Assert.That(fixture.Reader.ReadCount, Is.EqualTo(2));
            Assert.That(fixture.Service.CurrentSnapshot.Status, Is.EqualTo(CodexLocalIdentityStatus.FileMissing));
        }
    }

    [Test]
    public async Task DisposeAsync_StopsAndDisposesWatcher() {
        Fixture fixture = new([]);
        await fixture.Service.StartMonitoringAsync(CancellationToken.None);

        await fixture.Service.DisposeAsync();

        using (Assert.EnterMultipleScope()) {
            Assert.That(fixture.Notifier.StopCount, Is.EqualTo(1));
            Assert.That(fixture.Notifier.DisposeCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task StopAndDisposeAsync_CompeteWithActiveRead_CompleteCleanup() {
        Fixture fixture = new([]);
        await fixture.Service.StartMonitoringAsync(CancellationToken.None);
        TaskCompletionSource<bool> readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> cancellationCallbackEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseCancellationCallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Reader.ReadHandler = async cancellationToken => {
            Task canceledRead = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            using CancellationTokenRegistration registration = cancellationToken.Register(() => {
                cancellationCallbackEntered.TrySetResult(true);
                releaseCancellationCallback.Task.GetAwaiter().GetResult();
            });
            readStarted.TrySetResult(true);
            await canceledRead;
            return new(CodexLocalAuthReadStatus.FileMissing, null);
        };

        Task<CodexLocalIdentitySnapshot> activeRead = fixture.Service.ReadCurrentSnapshotAsync(CancellationToken.None);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task stopTask = Task.Run(() => fixture.Service.StopMonitoringAsync());
        await cancellationCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        TaskCompletionSource<bool> disposeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task disposeTask = Task.Run(async () => {
            disposeStarted.TrySetResult(true);
            await fixture.Service.DisposeAsync();
        });
        await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseCancellationCallback.TrySetResult(true);

        await Assert.CatchAsync<OperationCanceledException>(async () => await activeRead.WaitAsync(TimeSpan.FromSeconds(2)));
        await Task.WhenAll(stopTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(2));

        using (Assert.EnterMultipleScope()) {
            Assert.That(fixture.Service.CurrentSnapshot.Status, Is.EqualTo(CodexLocalIdentityStatus.NotChecked));
            Assert.That(fixture.Service.CurrentSnapshot.IsMonitoring, Is.False);
            Assert.That(fixture.Notifier.StopCount, Is.GreaterThan(0));
            Assert.That(fixture.Notifier.DisposeCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void CodexLocalAuthIdentity_ToString_DoesNotExposeIdentityValues() {
        CodexLocalAuthIdentity identity = new("account-secret", "private@example.test");

        string representation = identity.ToString();
        identity.Clear();

        using (Assert.EnterMultipleScope()) {
            Assert.That(representation, Does.Not.Contain("account-secret"));
            Assert.That(representation, Does.Not.Contain("private@example.test"));
            Assert.That(identity.AccountId, Is.Null);
            Assert.That(identity.Email, Is.Null);
        }
    }

    private static CodexAccountProfile CreateProfile(Guid accountId, string fingerprint, string? fallbackFingerprint) {
        return new(
            accountId,
            null,
            "test•••@example.test",
            fingerprint,
            1,
            DateTimeOffset.Parse("2026-10-25T00:00:00Z"),
            "connected",
            null,
            fallbackFingerprint
        );
    }

    private sealed class Fixture {
        public Fixture(IReadOnlyList<CodexAccountProfile> profiles) : this(
            new FakeAccountCatalogRepository(new IAccountCatalogRepository.Snapshot(profiles, "sha-1"))
        ) {
        }

        public Fixture(FakeAccountCatalogRepository catalog) {
            Catalog = catalog;
            Reader = new();
            Notifier = new();
            ProcessSource = new();
            Fingerprinter = new();
            Clock = new();
            Service = new(
                Catalog,
                Reader,
                Notifier,
                ProcessSource,
                Fingerprinter,
                Clock
            );
        }

        public FakeAccountCatalogRepository Catalog { get; }

        public FakeReader Reader { get; }

        public FakeNotifier Notifier { get; }

        public FakeProcessSource ProcessSource { get; }

        public FakeFingerprinter Fingerprinter { get; }

        public FakeClock Clock { get; }

        public CodexLocalIdentityService Service { get; }
    }

    private sealed class FakeAccountCatalogRepository : IAccountCatalogRepository {
        private readonly Queue<IAccountCatalogRepository.Snapshot> snapshots;

        public FakeAccountCatalogRepository(params IAccountCatalogRepository.Snapshot[] snapshots) {
            this.snapshots = new(snapshots);
            LastSnapshot = snapshots[^1];
        }

        public int LoadCount { get; private set; }

        public IAccountCatalogRepository.Snapshot LastSnapshot { get; private set; }

        public Task<IAccountCatalogRepository.Snapshot> LoadAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            if (snapshots.TryDequeue(out IAccountCatalogRepository.Snapshot? snapshot)) {
                LastSnapshot = snapshot;
            }

            return Task.FromResult(LastSnapshot);
        }

        public Task<IAccountCatalogRepository.SaveResult> SaveAsync(
            IReadOnlyList<CodexAccountProfile> accounts,
            string expectedSha256,
            CancellationToken cancellationToken
        ) {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeReader : ICodexLocalAuthIdentityReader {
        public CodexLocalAuthReadResult Result { get; set; } = new(CodexLocalAuthReadStatus.FileMissing, null);

        public Func<CancellationToken, Task<CodexLocalAuthReadResult>>? ReadHandler { get; set; }

        public int ReadCount { get; private set; }

        public Task<CodexLocalAuthReadResult> ReadCurrentAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (ReadHandler is not null) {
                return ReadHandler(cancellationToken);
            }

            CodexLocalAuthReadResult result = Result;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeNotifier : ICodexLocalAuthFileChangeNotifier {
        public event Action? FileChanged;

        public event Action<bool>? MonitoringAvailabilityChanged;

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            MonitoringAvailabilityChanged?.Invoke(true);
            return Task.CompletedTask;
        }

        public Task StopAsync() {
            StopCount++;
            return Task.CompletedTask;
        }

        public void RaiseFileChanged() {
            FileChanged?.Invoke();
        }

        public ValueTask DisposeAsync() {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeProcessSource : ICodexDesktopProcessStatusSource {
        public CodexDesktopProcessStatusSnapshot Result { get; set; } = new(
            CodexDesktopProcessStatus.Unknown,
            DateTimeOffset.Parse("2026-10-25T00:00:00Z")
        );

        public Task<CodexDesktopProcessStatusSnapshot> ReadStatusAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeFingerprinter : IIdentityFingerprinter {
        public string CreateFingerprint(string? accountId, string? email, string authMode) {
            return !string.IsNullOrWhiteSpace(accountId)
                ? "id:" + accountId.Trim()
                : "email:" + (email ?? "").Trim().ToLowerInvariant() + "|mode:" + authMode.Trim().ToLowerInvariant();
        }

        public string MaskEmail(string? email) {
            return string.IsNullOrWhiteSpace(email) ? "Codex 帳號" : "p•••@example.test";
        }
    }

    private sealed class FakeClock : IClock {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.Parse("2026-10-25T12:00:00Z");

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) {
            return Task.Delay(delay, cancellationToken);
        }
    }
}
