using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Windows.Claude;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Claude;

[TestFixture]
public sealed class WindowsClaudeDesktopProfileStoreTests {
    private const string FixtureSid = "S-1-5-21-100-200-300-1001";
    private static readonly DateTimeOffset FixtureUtcNow = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Transaction_CommittedCleanupWhileRunning_RemovesTransactionDirectory() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath, includeAllSidecars: true);
        FakeProcessController controller = new();
        WindowsClaudeDesktopProfileStore store = CreateStore(controller);
        ClaudeAccountProfile.DesktopSnapshot target = await CaptureSnapshotAsync(store, workspace.Installation);
        Guid transactionId = Guid.NewGuid();
        await store.PrepareRestoreAsync(workspace.Installation, target, transactionId, CancellationToken.None);
        await store.ApplyRestoreAsync(workspace.Installation, transactionId, CancellationToken.None);
        controller.Status = IClaudeDesktopProcessController.ProcessStatus.Running;

        await store.CommitTransactionAsync(workspace.Installation, transactionId, CancellationToken.None);

        Assert.That(Directory.Exists(Path.Combine(workspace.DataRootPath, ".ai-account-manager-auth-transactions", transactionId.ToString("N"))), Is.False);
    }

    [Test]
    public async Task Transaction_RestartsBeforeAndAfterEveryApplyAndRollbackRename(
        [Range(1, 10)] int renameNumber,
        [Values] bool afterRename,
        [Values] bool duringRollback
    ) {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath, includeAllSidecars: true);
        FakeProcessController controller = new();
        WindowsClaudeDesktopProfileStore store = CreateStore(controller);
        ClaudeAccountProfile.DesktopSnapshot target = await CaptureSnapshotAsync(store, workspace.Installation);
        target = target with {
            TokenCacheJson = """{"accessToken":"synthetic-target-token"}""",
            LastKnownAccountUuid = "22222222-2222-4222-8222-222222222222",
            Cookies = [20, 21], CookieJournal = [22], CookieWriteAheadLog = [23], CookieSharedMemory = [24]
        };
        Dictionary<string, byte[]> originals = ProfileFilePaths(workspace.DataRootPath)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        Guid transactionId = Guid.NewGuid();
        await store.PrepareRestoreAsync(workspace.Installation, target, transactionId, CancellationToken.None);
        int observed = 0;
        ClaudeDesktopSnapshotFileSystem.Operation selected = (duringRollback, afterRename) switch {
            (false, false) => ClaudeDesktopSnapshotFileSystem.Operation.BeforeRestoreMutation,
            (false, true) => ClaudeDesktopSnapshotFileSystem.Operation.AfterRestoreMutation,
            (true, false) => ClaudeDesktopSnapshotFileSystem.Operation.BeforeRollbackMutation,
            (true, true) => ClaudeDesktopSnapshotFileSystem.Operation.AfterRollbackMutation
        };
        WindowsClaudeDesktopProfileStore interrupted = CreateStore(controller, (operation, _) => {
            if (operation == selected && ++observed == renameNumber) {
                throw new IOException("Synthetic process interruption.");
            }
        });

        if (duringRollback) {
            await store.ApplyRestoreAsync(workspace.Installation, transactionId, CancellationToken.None);
            await Assert.CatchAsync<IOException>(() => interrupted.RestoreTransactionAsync(
                workspace.Installation, transactionId, CancellationToken.None));
        } else {
            await Assert.CatchAsync<IOException>(() => interrupted.ApplyRestoreAsync(
                workspace.Installation, transactionId, CancellationToken.None));
        }

        WindowsClaudeDesktopProfileStore restarted = CreateStore(new FakeProcessController());
        await restarted.RestoreTransactionAsync(workspace.Installation, transactionId, CancellationToken.None);
        await restarted.RestoreTransactionAsync(workspace.Installation, transactionId, CancellationToken.None);

        Assert.That(observed, Is.EqualTo(renameNumber));
        foreach (KeyValuePair<string, byte[]> original in originals) {
            Assert.That(File.ReadAllBytes(original.Key), Is.EqualTo(original.Value), original.Key);
        }

        await restarted.CommitTransactionAsync(workspace.Installation, transactionId, CancellationToken.None);
        Assert.That(Directory.Exists(Path.Combine(workspace.DataRootPath, ".ai-account-manager-auth-transactions", transactionId.ToString("N"))), Is.False);
    }

    [Test]
    public async Task Transaction_PreservesForeignPendingManifestInsteadOfDeletingIt() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath, includeAllSidecars: true);
        WindowsClaudeDesktopProfileStore store = CreateStore(new FakeProcessController());
        ClaudeAccountProfile.DesktopSnapshot target = await CaptureSnapshotAsync(store, workspace.Installation);
        Guid transactionId = Guid.NewGuid();
        await store.PrepareRestoreAsync(workspace.Installation, target with { Cookies = [91] }, transactionId, CancellationToken.None);
        string pendingPath = Path.Combine(
            workspace.DataRootPath, ".ai-account-manager-auth-transactions", transactionId.ToString("N"), "file-transaction.json.pending"
        );
        File.WriteAllText(pendingPath, """{"foreign":true}""");

        await Assert.CatchAsync<Exception>(() => store.ApplyRestoreAsync(workspace.Installation, transactionId, CancellationToken.None));

        Assert.That(File.ReadAllText(pendingPath), Is.EqualTo("""{"foreign":true}"""));
    }

    [Test]
    public async Task Transaction_ExternallyChangedLiveFilePreservesAllRecoveryEvidence() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath, includeAllSidecars: true);
        WindowsClaudeDesktopProfileStore store = CreateStore(new FakeProcessController());
        ClaudeAccountProfile.DesktopSnapshot target = await CaptureSnapshotAsync(store, workspace.Installation);
        Guid transactionId = Guid.NewGuid();
        await store.PrepareRestoreAsync(workspace.Installation, target with { Cookies = [90] }, transactionId, CancellationToken.None);
        await store.ApplyRestoreAsync(workspace.Installation, transactionId, CancellationToken.None);
        string cookiePath = Path.Combine(workspace.DataRootPath, "Network", "Cookies");
        File.WriteAllBytes(cookiePath, [99, 100]);
        string transactionPath = Path.Combine(workspace.DataRootPath, ".ai-account-manager-auth-transactions", transactionId.ToString("N"));
        Dictionary<string, byte[]> evidence = Directory.GetFiles(transactionPath).ToDictionary(path => path, File.ReadAllBytes);

        WindowsClaudeDesktopProfileStore restarted = CreateStore(new FakeProcessController());
        await Assert.CatchAsync<IOException>(() => restarted.RestoreTransactionAsync(workspace.Installation, transactionId, CancellationToken.None));

        Assert.That(File.ReadAllBytes(cookiePath), Is.EqualTo(new byte[] { 99, 100 }));
        foreach (KeyValuePair<string, byte[]> entry in evidence) {
            Assert.That(File.ReadAllBytes(entry.Key), Is.EqualTo(entry.Value));
        }
    }

    [Test]
    public async Task Transaction_CleansPublishedPendingManifestAfterRestartAndPreservesUnknownEntries() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath, includeAllSidecars: true);
        Dictionary<string, byte[]> originals = ProfileFilePaths(workspace.DataRootPath)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        WindowsClaudeDesktopProfileStore store = CreateStore(
            new FakeProcessController(),
            transactionObserver: (operation, _) => {
                if (operation == "before-manifest-publish") {
                    throw new IOException("Synthetic interruption before the first manifest publish.");
                }
            }
        );
        ClaudeAccountProfile.DesktopSnapshot target = await CaptureSnapshotAsync(store, workspace.Installation);
        ClaudeAccountProfile.DesktopSnapshot requested = target with { Cookies = [90, 91] };
        Guid transactionId = Guid.NewGuid();
        string transactionPath = Path.Combine(
            workspace.DataRootPath,
            ".ai-account-manager-auth-transactions",
            transactionId.ToString("N")
        );
        string pendingPath = Path.Combine(transactionPath, "file-transaction.json.pending");
        string manifestPath = Path.Combine(transactionPath, "file-transaction.json");

        try {
            await Assert.CatchAsync<IOException>(() => store.PrepareRestoreAsync(
                workspace.Installation,
                requested,
                transactionId,
                CancellationToken.None
            ));

            Assert.That(File.Exists(pendingPath), Is.True);
            Assert.That(File.Exists(manifestPath), Is.False);
            foreach (KeyValuePair<string, byte[]> original in originals) {
                Assert.That(File.ReadAllBytes(original.Key), Is.EqualTo(original.Value), original.Key);
            }

            string unknownPath = Path.Combine(transactionPath, "unexpected.bin");
            File.WriteAllBytes(unknownPath, [31, 32]);
            WindowsClaudeDesktopProfileStore restarted = CreateStore(new FakeProcessController());
            await Assert.CatchAsync<IOException>(() => restarted.CommitTransactionAsync(
                workspace.Installation,
                transactionId,
                CancellationToken.None
            ));

            Assert.That(File.Exists(pendingPath), Is.True);
            Assert.That(File.ReadAllBytes(unknownPath), Is.EqualTo(new byte[] { 31, 32 }));
            foreach (KeyValuePair<string, byte[]> original in originals) {
                Assert.That(File.ReadAllBytes(original.Key), Is.EqualTo(original.Value), original.Key);
            }

            File.Delete(unknownPath);
            await restarted.CommitTransactionAsync(workspace.Installation, transactionId, CancellationToken.None);
            Assert.That(Directory.Exists(transactionPath), Is.False);
        } finally {
            ZeroSnapshot(target);
            CryptographicOperations.ZeroMemory(requested.Cookies);
            foreach (byte[] original in originals.Values) {
                Zero(original);
            }
        }
    }

    [Test]
    public async Task Transaction_CommittedCleanupWhileRunning_AllowsChangedLiveAuth() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath, includeAllSidecars: true);
        WindowsClaudeDesktopProfileStore store = CreateStore(new FakeProcessController());
        ClaudeAccountProfile.DesktopSnapshot captured = await CaptureSnapshotAsync(store, workspace.Installation);
        ClaudeAccountProfile.DesktopSnapshot target = captured with { Cookies = [90, 91] };
        Guid transactionId = Guid.NewGuid();
        string transactionPath = Path.Combine(
            workspace.DataRootPath,
            ".ai-account-manager-auth-transactions",
            transactionId.ToString("N")
        );

        try {
            await store.PrepareRestoreAsync(workspace.Installation, target, transactionId, CancellationToken.None);
            await store.ApplyRestoreAsync(workspace.Installation, transactionId, CancellationToken.None);
            string cookiePath = Path.Combine(workspace.DataRootPath, "Network", "Cookies");
            File.WriteAllBytes(cookiePath, [99, 100]);

            WindowsClaudeDesktopProfileStore restarted = CreateStore(new FakeProcessController {
                Status = IClaudeDesktopProcessController.ProcessStatus.Running
            });
            await restarted.CommitTransactionAsync(workspace.Installation, transactionId, CancellationToken.None);

            Assert.That(File.ReadAllBytes(cookiePath), Is.EqualTo(new byte[] { 99, 100 }));
            Assert.That(Directory.Exists(transactionPath), Is.False);
        } finally {
            ZeroSnapshot(captured);
            CryptographicOperations.ZeroMemory(target.Cookies);
        }
    }

    [Test]
    public async Task ReadCurrentAsync_DecryptsSafeStorageTokenCacheWithLocalStateKey() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath);
        WriteEncryptedTokenCache(
            workspace.DataRootPath,
            """
            {
              "acct:fixture|client:org:https://api.anthropic.com:user:inference user:profile":{"token":"encrypted-fixture-token","refreshToken":"refresh-fixture-token","expiresAt":1},
              "acct:fixture|client:org:https://api.anthropic.com:user:inference":{"token":"inference-only-token","expiresAt":1}
            }
            """
        );
        WindowsClaudeDesktopProfileStore store = CreateStore(new FakeProcessController());

        IClaudeDesktopProfileStore.ReadResult result = await store.ReadCurrentAsync(
            workspace.Installation,
            CancellationToken.None
        );

        Assert.That(result.TokenCandidates.Select(candidate => candidate.AccessToken), Is.EqualTo(new[] { "encrypted-fixture-token" }));
        Assert.That(result.TokenCandidates[0].SourceField,
            Is.EqualTo("oauth:tokenCache.acct:fixture|client:org:https://api.anthropic.com:user:inference user:profile.token"));
    }

    [Test]
    public async Task CaptureStableSnapshotAsync_KeepsEncryptedTokenCacheAndDecryptsFromSnapshot() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath);
        string encrypted = WriteEncryptedTokenCache(
            workspace.DataRootPath,
            """{"acct:fixture|client:org:https://api.anthropic.com:user:profile":{"token":"snapshot-fixture-token"},"unscoped":{"token":"unscoped-token"}}"""
        );
        WindowsClaudeDesktopProfileStore store = CreateStore(new FakeProcessController());

        ClaudeAccountProfile.DesktopSnapshot snapshot = await CaptureSnapshotAsync(store, workspace.Installation);
        try {
            Assert.That(snapshot.TokenCacheJson, Is.EqualTo(JsonSerializer.Serialize(encrypted)));
            Assert.That(store.GetTokenCandidates(snapshot).Select(candidate => candidate.AccessToken),
                Is.EqualTo(new[] { "snapshot-fixture-token" }));
        } finally {
            ZeroSnapshot(snapshot);
        }
    }

    [Test]
    public async Task ReadCurrentAsync_RejectsEncryptedTokenCacheArray() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath);
        WriteEncryptedTokenCache(workspace.DataRootPath, """[{"token":"array-token"}]""");
        WindowsClaudeDesktopProfileStore store = CreateStore(new FakeProcessController());

        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.ReadCurrentAsync(
            workspace.Installation,
            CancellationToken.None
        ));
    }

    [Test]
    public void SafeStorage_RejectsTamperedOrUnsupportedValues() {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        string encrypted = ClaudeSafeStorage.Encrypt("""{"accessToken":"value"}"""u8, key);
        byte[] tampered = Convert.FromBase64String(encrypted);
        tampered[^1] ^= 0xFF;
        byte[] unsupported = Convert.FromBase64String(encrypted);
        unsupported[2] = (byte)'9';

        Assert.That(ClaudeSafeStorage.Decrypt(encrypted, key), Is.EqualTo("""{"accessToken":"value"}"""u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => ClaudeSafeStorage.Decrypt(Convert.ToBase64String(tampered), key));
        Assert.Throws<InvalidDataException>(() => ClaudeSafeStorage.Decrypt(Convert.ToBase64String(unsupported), key));
        Assert.Throws<InvalidDataException>(() => ClaudeSafeStorage.Decrypt(encrypted, RandomNumberGenerator.GetBytes(32)));
    }

    [Test]
    public async Task ReadCurrentAsync_ReadsOnlyConfigWhileDesktopIsRunning() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath);
        File.Delete(Path.Combine(workspace.DataRootPath, "Local State"));
        File.Delete(Path.Combine(workspace.DataRootPath, "Network", "Cookies"));
        FakeProcessController processController = new() {
            Status = IClaudeDesktopProcessController.ProcessStatus.Running
        };
        WindowsClaudeDesktopProfileStore store = CreateStore(processController);

        IClaudeDesktopProfileStore.ReadResult result = await store.ReadCurrentAsync(
            workspace.Installation,
            CancellationToken.None
        );

        Assert.That(result.LastKnownAccountUuid, Is.EqualTo("11111111-1111-4111-8111-111111111111"));
        Assert.That(result.TokenCandidates, Has.Count.EqualTo(2));
        Assert.That(result.TokenCandidates.Select(candidate => candidate.AccessToken), Is.EquivalentTo(new[] {
            "fixture-oauth-token-not-valid",
            "fixture-oauth-token-v2-not-valid"
        }));
        Assert.That(result.TokenCandidates[0].ToString(), Does.Not.Contain(result.TokenCandidates[0].AccessToken));
        Assert.That(processController.StatusCallCount, Is.Zero);
    }

    [Test]
    public async Task CaptureStableSnapshotAsync_LocksEveryExistingFileBeforeReadingAndClearsBuffersOnDispose() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath, includeAllSidecars: true);
        FakeProcessController processController = new();
        List<ClaudeDesktopSnapshotFileSystem.Operation> operations = [];
        int lockedCount = 0;
        int lockedCountAtFirstRead = -1;
        WindowsClaudeDesktopProfileStore store = CreateStore(
            processController,
            (operation, _) => {
                operations.Add(operation);
                if (operation == ClaudeDesktopSnapshotFileSystem.Operation.HandleLocked) {
                    lockedCount++;
                } else if (operation == ClaudeDesktopSnapshotFileSystem.Operation.BeforeRead
                    && lockedCountAtFirstRead < 0) {
                    lockedCountAtFirstRead = lockedCount;
                }
            }
        );

        await using IClaudeDesktopProfileStore.StableSnapshotLease lease = await store.CaptureStableSnapshotAsync(
            workspace.Installation,
            CancellationToken.None
        );

        Assert.That(lease.Snapshot.CookieSidecarSnapshotVersion, Is.EqualTo(1));
        Assert.That(lease.Snapshot.CookieRelativePath, Is.EqualTo("Network/Cookies"));
        Assert.That(lease.Snapshot.Cookies, Is.EqualTo(new byte[] { 3, 1, 4, 1, 5, 9 }));
        Assert.That(lease.Snapshot.CookieJournal, Is.EqualTo(new byte[] { 2, 7, 1 }));
        Assert.That(lease.Snapshot.CookieWriteAheadLog, Is.EqualTo(new byte[] { 8, 5, 3 }));
        Assert.That(lease.Snapshot.CookieSharedMemory, Is.EqualTo(new byte[] { 9, 7, 9 }));
        Assert.That(lease.TokenCandidates, Has.Count.EqualTo(2));
        Assert.That(lockedCount, Is.EqualTo(6));
        Assert.That(lockedCountAtFirstRead, Is.EqualTo(6));
        Assert.That(operations.IndexOf(ClaudeDesktopSnapshotFileSystem.Operation.AllHandlesLocked),
            Is.LessThan(operations.IndexOf(ClaudeDesktopSnapshotFileSystem.Operation.BeforeRead)));
        Assert.That(await lease.ValidateAsync(CancellationToken.None), Is.True);
        foreach (string path in ProfileFilePaths(workspace.DataRootPath)) {
            AssertCannotOpenForWrite(path);
        }

        byte[] cookies = lease.Snapshot.Cookies;
        byte[] journal = lease.Snapshot.CookieJournal!;
        byte[] wal = lease.Snapshot.CookieWriteAheadLog!;
        byte[] sharedMemory = lease.Snapshot.CookieSharedMemory!;
        await lease.DisposeAsync();

        AssertAllZero(cookies);
        AssertAllZero(journal);
        AssertAllZero(wal);
        AssertAllZero(sharedMemory);
        foreach (string path in ProfileFilePaths(workspace.DataRootPath)) {
            AssertCanOpenForWrite(path);
        }
        Assert.That(processController.StatusCallCount, Is.Zero);
    }

    [Test]
    public async Task CaptureStableSnapshotAsync_RejectsSidecarAddedAfterLockingBeforeAnyReadAndReleasesHandles() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath);
        string walPath = Path.Combine(workspace.DataRootPath, "Network", "Cookies-wal");
        bool added = false;
        int readCount = 0;
        WindowsClaudeDesktopProfileStore store = CreateStore(
            new FakeProcessController(),
            (operation, _) => {
                if (operation == ClaudeDesktopSnapshotFileSystem.Operation.AllHandlesLocked && !added) {
                    File.WriteAllBytes(walPath, [4, 2]);
                    added = true;
                }

                if (operation == ClaudeDesktopSnapshotFileSystem.Operation.BeforeRead) {
                    readCount++;
                }
            }
        );

        await Assert.ThrowsAsync<IOException>(async () => await store.CaptureStableSnapshotAsync(
            workspace.Installation,
            CancellationToken.None
        ));

        Assert.That(added, Is.True);
        Assert.That(readCount, Is.Zero);
        AssertCanOpenForWrite(Path.Combine(workspace.DataRootPath, "config.json"));
        AssertCanOpenForWrite(Path.Combine(workspace.DataRootPath, "Network", "Cookies"));
    }

    [Test]
    public async Task CaptureStableSnapshotAsync_RejectsFileReplacedAfterPreflightAndReleasesEarlierHandles() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath, includeAllSidecars: true);
        string walPath = Path.Combine(workspace.DataRootPath, "Network", "Cookies-wal");
        bool replaced = false;
        WindowsClaudeDesktopProfileStore store = CreateStore(
            new FakeProcessController(),
            (operation, path) => {
                if (operation == ClaudeDesktopSnapshotFileSystem.Operation.AfterPreflight
                    && string.Equals(path, walPath, StringComparison.OrdinalIgnoreCase)
                    && !replaced) {
                    File.Move(walPath, walPath + ".old");
                    File.WriteAllBytes(walPath, [6, 2, 6, 4]);
                    replaced = true;
                }
            }
        );

        await Assert.ThrowsAsync<IOException>(async () => await store.CaptureStableSnapshotAsync(
            workspace.Installation,
            CancellationToken.None
        ));

        Assert.That(replaced, Is.True);
        AssertCanOpenForWrite(Path.Combine(workspace.DataRootPath, "config.json"));
        AssertCanOpenForWrite(Path.Combine(workspace.DataRootPath, "Network", "Cookies"));
        AssertCanOpenForWrite(Path.Combine(workspace.DataRootPath, "Network", "Cookies-wal"));
    }

    [Test]
    public async Task CaptureStableSnapshotAsync_CompetingWriterFailureReleasesPreviouslyAcquiredHandles() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath, includeAllSidecars: true);
        string cookiePath = Path.Combine(workspace.DataRootPath, "Network", "Cookies");
        FileStream? competingWriter = null;
        int previouslyLocked = 0;
        WindowsClaudeDesktopProfileStore store = CreateStore(
            new FakeProcessController(),
            (operation, path) => {
                if (operation == ClaudeDesktopSnapshotFileSystem.Operation.HandleLocked) {
                    previouslyLocked++;
                }

                if (operation == ClaudeDesktopSnapshotFileSystem.Operation.BeforeLock
                    && string.Equals(path, cookiePath, StringComparison.OrdinalIgnoreCase)) {
                    competingWriter = new FileStream(cookiePath, FileMode.Open, FileAccess.Write, FileShare.Read);
                }
            }
        );

        try {
            await Assert.ThrowsAsync<IOException>(async () => await store.CaptureStableSnapshotAsync(
                workspace.Installation,
                CancellationToken.None
            ));
        } finally {
            competingWriter?.Dispose();
        }

        Assert.That(previouslyLocked, Is.GreaterThan(0));
        AssertCanOpenForWrite(Path.Combine(workspace.DataRootPath, "Local State"));
        AssertCanOpenForWrite(cookiePath);
        AssertCanOpenForWrite(Path.Combine(workspace.DataRootPath, "config.json"));
    }

    [Test]
    public async Task StableSnapshotLease_ValidateAsyncRejectsSidecarAddedAfterCapture() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath);
        WindowsClaudeDesktopProfileStore store = CreateStore(new FakeProcessController());
        await using IClaudeDesktopProfileStore.StableSnapshotLease lease = await store.CaptureStableSnapshotAsync(
            workspace.Installation,
            CancellationToken.None
        );
        File.WriteAllBytes(Path.Combine(workspace.DataRootPath, "Network", "Cookies-shm"), [5, 8]);

        Assert.That(await lease.ValidateAsync(CancellationToken.None), Is.False);
    }

    [Test]
    public async Task RestoreAsync_VersionOneAbsentSidecarsRemovesLiveJournalWalAndShm() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath);
        FakeProcessController processController = new();
        WindowsClaudeDesktopProfileStore store = CreateStore(processController);
        ClaudeAccountProfile.DesktopSnapshot target = await CaptureSnapshotAsync(store, workspace.Installation);
        string networkDirectory = Path.Combine(workspace.DataRootPath, "Network");
        string journalPath = Path.Combine(networkDirectory, "Cookies-journal");
        string walPath = Path.Combine(networkDirectory, "Cookies-wal");
        string sharedMemoryPath = Path.Combine(networkDirectory, "Cookies-shm");
        File.WriteAllBytes(journalPath, [1, 3, 3]);
        File.WriteAllBytes(walPath, [8, 4, 8]);
        File.WriteAllBytes(sharedMemoryPath, [2, 7, 1]);
        target = target with {
            TokenCacheJson = """{"fixture":{"accessToken":"next-fixture-token-not-valid"}}""",
            TokenCacheV2Json = null,
            LastKnownAccountUuid = "44444444-4444-4444-8444-444444444444",
            Cookies = [9, 8, 7]
        };

        try {
            await store.RestoreAsync(workspace.Installation, target, CancellationToken.None);

            byte[] configBytes = File.ReadAllBytes(Path.Combine(workspace.DataRootPath, "config.json"));
            try {
                using JsonDocument config = JsonDocument.Parse(configBytes);
                Assert.That(config.RootElement.GetProperty("unrelatedSetting").GetString(), Is.EqualTo("preserved fixture value"));
                Assert.That(
                    config.RootElement.GetProperty("oauth:tokenCache").GetProperty("fixture").GetProperty("accessToken").GetString(),
                    Is.EqualTo("next-fixture-token-not-valid")
                );
                Assert.That(config.RootElement.TryGetProperty("oauth:tokenCacheV2", out _), Is.False);
                Assert.That(config.RootElement.GetProperty("lastKnownAccountUuid").GetString(), Is.EqualTo(target.LastKnownAccountUuid));
            } finally {
                CryptographicOperations.ZeroMemory(configBytes);
            }

            Assert.That(File.ReadAllBytes(Path.Combine(networkDirectory, "Cookies")), Is.EqualTo(target.Cookies));
            Assert.That(File.Exists(journalPath), Is.False);
            Assert.That(File.Exists(walPath), Is.False);
            Assert.That(File.Exists(sharedMemoryPath), Is.False);
            // 準備階段一次；config 與 Cookies 各有移出與發布兩次 rename，三個 sidecar 各移除一次，每次 rename 前各檢查一次。
            Assert.That(processController.StatusCallCount, Is.EqualTo(8));
        } finally {
            ZeroSnapshot(target);
        }
    }

    [Test]
    public async Task RestoreAsync_RejectsLegacyVersionZeroBeforeWritesButStillParsesTokenCandidates() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath);
        FakeProcessController processController = new();
        WindowsClaudeDesktopProfileStore store = CreateStore(processController);
        ClaudeAccountProfile.DesktopSnapshot captured = await CaptureSnapshotAsync(store, workspace.Installation);
        ClaudeAccountProfile.DesktopSnapshot legacy = captured with { CookieSidecarSnapshotVersion = 0 };
        string configPath = Path.Combine(workspace.DataRootPath, "config.json");
        byte[] originalConfig = File.ReadAllBytes(configPath);

        try {
            Assert.That(store.GetTokenCandidates(legacy), Has.Count.EqualTo(2));
            await Assert.ThrowsAsync<ArgumentException>(async () => await store.RestoreAsync(
                workspace.Installation,
                legacy,
                CancellationToken.None
            ));
            Assert.That(File.ReadAllBytes(configPath), Is.EqualTo(originalConfig));
            Assert.That(processController.StatusCallCount, Is.Zero);
        } finally {
            CryptographicOperations.ZeroMemory(originalConfig);
            ZeroSnapshot(legacy);
        }
    }

    [TestCase(IClaudeDesktopProcessController.ProcessStatus.Running)]
    [TestCase(IClaudeDesktopProcessController.ProcessStatus.Unknown)]
    public async Task RestoreAsync_StopsAfterDesktopStartsBetweenFileMutationsAndKeepsRecoveryBackups(
        IClaudeDesktopProcessController.ProcessStatus statusAfterFirstMutation
    ) {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath);
        FakeProcessController processController = new();
        WindowsClaudeDesktopProfileStore store = CreateStore(processController);
        ClaudeAccountProfile.DesktopSnapshot target = await CaptureSnapshotAsync(store, workspace.Installation);
        string configPath = Path.Combine(workspace.DataRootPath, "config.json");
        string cookiePath = Path.Combine(workspace.DataRootPath, "Network", "Cookies");
        byte[] originalCookies = File.ReadAllBytes(cookiePath);
        target = target with {
            TokenCacheJson = """{"fixture":{"accessToken":"replacement-token-not-valid"}}""",
            TokenCacheV2Json = null,
            Cookies = [9, 8, 7]
        };
        byte[] originalConfig = File.ReadAllBytes(configPath);
        // 依序為準備階段與第一次 rename 前的檢查；第二次 rename 前 Desktop 已啟動，之後的復原檢查也維持此狀態。
        processController.SetStatusSequence(
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            statusAfterFirstMutation
        );
        List<string> rollbackMutations = [];
        store = CreateStore(processController, (operation, path) => {
            if (operation == ClaudeDesktopSnapshotFileSystem.Operation.AfterRollbackMutation) {
                rollbackMutations.Add(operation + ":" + path);
            }
        });

        try {
            await Assert.CatchAsync<Exception>(async () => await store.RestoreAsync(
                workspace.Installation,
                target,
                CancellationToken.None
            ));

            Assert.That(rollbackMutations, Is.Empty);
            Assert.That(TransactionDirectories(workspace.DataRootPath), Is.Not.Empty);

            WindowsClaudeDesktopProfileStore restarted = CreateStore(new FakeProcessController());
            foreach (string transactionDirectory in TransactionDirectories(workspace.DataRootPath)) {
                Guid transactionId = Guid.ParseExact(Path.GetFileName(transactionDirectory), "N");
                await restarted.RestoreTransactionAsync(workspace.Installation, transactionId, CancellationToken.None);
            }

            Assert.That(File.ReadAllBytes(configPath), Is.EqualTo(originalConfig));
            Assert.That(File.ReadAllBytes(cookiePath), Is.EqualTo(originalCookies));
        } finally {
            CryptographicOperations.ZeroMemory(originalConfig);
            CryptographicOperations.ZeroMemory(originalCookies);
            ZeroSnapshot(target);
        }
    }

    [Test]
    public async Task RestoreAsync_StopsInternalRollbackWhenDesktopStartsAndKeepsRecoveryBackups() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath);
        FakeProcessController processController = new();
        List<ClaudeDesktopSnapshotFileSystem.Operation> mutationEvents = [];
        bool threwAfterFirstMutation = false;
        WindowsClaudeDesktopProfileStore store = CreateStore(processController, (operation, _) => {
            mutationEvents.Add(operation);
            if (operation == ClaudeDesktopSnapshotFileSystem.Operation.AfterRestoreMutation
                && !threwAfterFirstMutation) {
                threwAfterFirstMutation = true;
                throw new IOException("Synthetic failure after the first live file mutation.");
            }
        });
        ClaudeAccountProfile.DesktopSnapshot target = await CaptureSnapshotAsync(store, workspace.Installation);
        string configPath = Path.Combine(workspace.DataRootPath, "config.json");
        string cookiePath = Path.Combine(workspace.DataRootPath, "Network", "Cookies");
        byte[] originalCookies = File.ReadAllBytes(cookiePath);
        target = target with {
            TokenCacheJson = """{"fixture":{"accessToken":"rollback-token-not-valid"}}""",
            TokenCacheV2Json = null,
            Cookies = [6, 2, 6, 4]
        };
        byte[] originalConfig = File.ReadAllBytes(configPath);
        // 依序為準備階段與第一次 rename 前的檢查；第一次 rename 後套用失敗，內部復原前 Desktop 已啟動。
        processController.SetStatusSequence(
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.Running
        );

        try {
            await Assert.ThrowsAsync<AggregateException>(async () => await store.RestoreAsync(
                workspace.Installation,
                target,
                CancellationToken.None
            ));

            Assert.That(threwAfterFirstMutation, Is.True);
            Assert.That(mutationEvents, Does.Not.Contain(ClaudeDesktopSnapshotFileSystem.Operation.AfterRollbackMutation));
            Assert.That(TransactionDirectories(workspace.DataRootPath), Is.Not.Empty);

            WindowsClaudeDesktopProfileStore restarted = CreateStore(new FakeProcessController());
            foreach (string transactionDirectory in TransactionDirectories(workspace.DataRootPath)) {
                Guid transactionId = Guid.ParseExact(Path.GetFileName(transactionDirectory), "N");
                await restarted.RestoreTransactionAsync(workspace.Installation, transactionId, CancellationToken.None);
            }

            Assert.That(File.ReadAllBytes(configPath), Is.EqualTo(originalConfig));
            Assert.That(File.ReadAllBytes(cookiePath), Is.EqualTo(originalCookies));
        } finally {
            CryptographicOperations.ZeroMemory(originalConfig);
            CryptographicOperations.ZeroMemory(originalCookies);
            ZeroSnapshot(target);
        }
    }

    [Test]
    public async Task RestoreAsync_RejectsMismatchedRootBindingBeforeWritingFiles() {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath);
        FakeProcessController processController = new();
        WindowsClaudeDesktopProfileStore store = CreateStore(processController);
        ClaudeAccountProfile.DesktopSnapshot captured = await CaptureSnapshotAsync(store, workspace.Installation);
        ClaudeAccountProfile.DesktopSnapshot mismatched = captured with {
            DataRootFingerprint = new string('f', 64)
        };
        string configPath = Path.Combine(workspace.DataRootPath, "config.json");
        byte[] originalConfig = File.ReadAllBytes(configPath);

        try {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.RestoreAsync(
                workspace.Installation,
                mismatched,
                CancellationToken.None
            ));
            Assert.That(File.ReadAllBytes(configPath), Is.EqualTo(originalConfig));
            Assert.That(processController.StatusCallCount, Is.Zero);
        } finally {
            CryptographicOperations.ZeroMemory(originalConfig);
            ZeroSnapshot(captured);
        }
    }

    [TestCase("sid")]
    [TestCase("key")]
    public async Task RestoreAsync_RejectsMismatchedSidOrLocalStateKeyBeforeWritingFiles(string mismatchKind) {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateDesktopFiles(workspace.DataRootPath);
        FakeProcessController processController = new();
        WindowsClaudeDesktopProfileStore store = CreateStore(processController);
        ClaudeAccountProfile.DesktopSnapshot captured = await CaptureSnapshotAsync(store, workspace.Installation);
        ClaudeAccountProfile.DesktopSnapshot mismatched = mismatchKind switch {
            "sid" => captured with { WindowsUserSid = "S-1-5-18" },
            "key" => captured with { LocalStateEncryptedKeySha256 = new string('f', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatchKind))
        };
        string configPath = Path.Combine(workspace.DataRootPath, "config.json");
        string cookiePath = Path.Combine(workspace.DataRootPath, "Network", "Cookies");
        byte[] originalConfig = File.ReadAllBytes(configPath);
        byte[] originalCookies = File.ReadAllBytes(cookiePath);

        try {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.RestoreAsync(
                workspace.Installation,
                mismatched,
                CancellationToken.None
            ));
            Assert.That(File.ReadAllBytes(configPath), Is.EqualTo(originalConfig));
            Assert.That(File.ReadAllBytes(cookiePath), Is.EqualTo(originalCookies));
            Assert.That(processController.StatusCallCount, Is.Zero);
        } finally {
            CryptographicOperations.ZeroMemory(originalConfig);
            CryptographicOperations.ZeroMemory(originalCookies);
            ZeroSnapshot(captured);
        }
    }

    private static string[] TransactionDirectories(string dataRootPath) {
        string transactionsRoot = Path.Combine(dataRootPath, ".ai-account-manager-auth-transactions");
        return Directory.Exists(transactionsRoot) ? Directory.GetDirectories(transactionsRoot) : [];
    }

    private static WindowsClaudeDesktopProfileStore CreateStore(
        FakeProcessController processController,
        Action<ClaudeDesktopSnapshotFileSystem.Operation, string>? observer = null,
        Action<string, string>? transactionObserver = null
    ) {
        return new WindowsClaudeDesktopProfileStore(
            processController,
            static () => FixtureSid,
            static () => FixtureUtcNow,
            observer,
            transactionObserver
        );
    }

    private static async Task<ClaudeAccountProfile.DesktopSnapshot> CaptureSnapshotAsync(
        WindowsClaudeDesktopProfileStore store,
        IClaudeDesktopInstallationLocator.Installation installation
    ) {
        await using IClaudeDesktopProfileStore.StableSnapshotLease lease = await store.CaptureStableSnapshotAsync(
            installation,
            CancellationToken.None
        );
        return CloneSnapshot(lease.Snapshot);
    }

    private static ClaudeAccountProfile.DesktopSnapshot CloneSnapshot(
        ClaudeAccountProfile.DesktopSnapshot snapshot
    ) {
        return snapshot with {
            Cookies = (byte[])snapshot.Cookies.Clone(),
            CookieJournal = snapshot.CookieJournal is null ? null : (byte[])snapshot.CookieJournal.Clone(),
            CookieWriteAheadLog = snapshot.CookieWriteAheadLog is null
                ? null
                : (byte[])snapshot.CookieWriteAheadLog.Clone(),
            CookieSharedMemory = snapshot.CookieSharedMemory is null
                ? null
                : (byte[])snapshot.CookieSharedMemory.Clone()
        };
    }

    private static void ZeroSnapshot(ClaudeAccountProfile.DesktopSnapshot snapshot) {
        CryptographicOperations.ZeroMemory(snapshot.Cookies);
        Zero(snapshot.CookieJournal);
        Zero(snapshot.CookieWriteAheadLog);
        Zero(snapshot.CookieSharedMemory);
    }

    private static void Zero(byte[]? bytes) {
        if (bytes is not null) {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void AssertAllZero(byte[] bytes) {
        Assert.That(bytes, Has.All.EqualTo((byte)0));
    }

    private static void AssertCannotOpenForWrite(string path) {
        Assert.Throws<IOException>(() => {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        });
    }

    private static void AssertCanOpenForWrite(string path) {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
    }

    private static IReadOnlyList<string> ProfileFilePaths(string dataRootPath) {
        string networkDirectory = Path.Combine(dataRootPath, "Network");
        return [
            Path.Combine(dataRootPath, "config.json"),
            Path.Combine(dataRootPath, "Local State"),
            Path.Combine(networkDirectory, "Cookies"),
            Path.Combine(networkDirectory, "Cookies-journal"),
            Path.Combine(networkDirectory, "Cookies-wal"),
            Path.Combine(networkDirectory, "Cookies-shm")
        ];
    }

    private static void CreateDesktopFiles(string dataRootPath, bool includeAllSidecars = false) {
        string networkDirectory = Path.Combine(dataRootPath, "Network");
        Directory.CreateDirectory(networkDirectory);
        File.Copy(Fixture("desktop-config.json"), Path.Combine(dataRootPath, "config.json"));
        File.Copy(Fixture("desktop-local-state.json"), Path.Combine(dataRootPath, "Local State"));
        File.WriteAllBytes(Path.Combine(networkDirectory, "Cookies"), [3, 1, 4, 1, 5, 9]);
        if (includeAllSidecars) {
            File.WriteAllBytes(Path.Combine(networkDirectory, "Cookies-journal"), [2, 7, 1]);
            File.WriteAllBytes(Path.Combine(networkDirectory, "Cookies-wal"), [8, 5, 3]);
            File.WriteAllBytes(Path.Combine(networkDirectory, "Cookies-shm"), [9, 7, 9]);
        }
    }

    // 依實機格式建立 DPAPI 保護的 Local State key，並以 safeStorage 格式加密 oauth:tokenCache。
    private static string WriteEncryptedTokenCache(string dataRootPath, string tokenCacheJson) {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try {
            byte[] protectedKey = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
            string encryptedKey = Convert.ToBase64String([.. "DPAPI"u8, .. protectedKey]);
            File.WriteAllText(
                Path.Combine(dataRootPath, "Local State"),
                JsonSerializer.Serialize(new { os_crypt = new { encrypted_key = encryptedKey } })
            );

            string encrypted = ClaudeSafeStorage.Encrypt(Encoding.UTF8.GetBytes(tokenCacheJson), key);
            string configPath = Path.Combine(dataRootPath, "config.json");
            JsonObject config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
            config["oauth:tokenCache"] = encrypted;
            config.Remove("oauth:tokenCacheV2");
            File.WriteAllText(configPath, config.ToJsonString());
            return encrypted;
        } finally {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string Fixture(string fileName) {
        return Path.Combine(TestContext.CurrentContext.TestDirectory, "Claude", "Fixtures", fileName);
    }

    private sealed class FakeProcessController : IClaudeDesktopProcessController {
        private readonly Queue<IClaudeDesktopProcessController.ProcessStatus> statuses = new();

        internal IClaudeDesktopProcessController.ProcessStatus Status { get; set; } =
            IClaudeDesktopProcessController.ProcessStatus.NotRunning;

        internal int StatusCallCount { get; private set; }

        internal void SetStatusSequence(params IClaudeDesktopProcessController.ProcessStatus[] values) {
            statuses.Clear();
            foreach (IClaudeDesktopProcessController.ProcessStatus value in values) {
                statuses.Enqueue(value);
            }
        }

        public Task<IClaudeDesktopProcessController.ProcessStatus> GetStatusAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            StatusCallCount++;
            if (statuses.TryDequeue(out IClaudeDesktopProcessController.ProcessStatus status)) {
                Status = status;
            }

            return Task.FromResult(Status);
        }

        public Task<bool> CloseGracefullyAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            TimeSpan timeout,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }

        public Task LaunchAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
