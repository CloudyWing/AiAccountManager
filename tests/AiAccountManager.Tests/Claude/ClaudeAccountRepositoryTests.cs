using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Windows.Claude;
using CloudyWing.AiAccountManager.Windows.Security;
using CloudyWing.AiAccountManager.Windows.Storage;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Claude;

[TestFixture]
public sealed class WindowsClaudeAccountRepositoryTests {
    private static readonly Guid ProfileId = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid AccountId = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid OrganizationId = new("cccccccc-cccc-4ccc-8ccc-cccccccccccc");

    [Test]
    public async Task SaveProfileAsync_F002_PersistsWalAndShmBeforeLeaseBufferCleanup() {
        using WindowsClaudeTestWorkspace workspace = new();
        string appDataPath = Path.Combine(workspace.RootPath, "application-data");
        CurrentUserDataDirectory dataDirectory = new(appDataPath);
        WindowsClaudeAccountRepository repository = new(dataDirectory, new AtomicJsonFileStore(dataDirectory));
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        SecurityIdentifier currentSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The test process has no Windows user SID.");
        ClaudeAccountProfile profile = new(
            ProfileId,
            "Fixture account",
            "fixture@example.test",
            AccountId.ToString("D"),
            OrganizationId.ToString("D"),
            installation.InstallationId,
            installation.DataRootFingerprint,
            currentSid.Value,
            new string('a', 64),
            1,
            ClaudeTestData.Now.AddDays(-1),
            ClaudeTestData.Now,
            ClaudeAccountProfile.VerificationStatus.Verified,
            CreateBinding(installation, currentSid.Value).Source.BindingId,
            1
        );
        const string fixtureToken = "fixture-token-not-valid-for-anthropic";
        ClaudeAccountProfile.DesktopSnapshot desktopSnapshot = new(
            $"{{\"accessToken\":\"{fixtureToken}\"}}",
            null,
            AccountId.ToString("D"),
            [1, 2, 3, 4],
            [5, 6],
            "Network/Cookies",
            currentSid.Value,
            installation.InstallationId,
            installation.DataRootFingerprint,
            new string('a', 64),
            ClaudeTestData.Now,
            [7, 8, 9],
            [10, 11],
            1,
            1,
            CreateBinding(installation, currentSid.Value)
        );

        IClaudeAccountRepository.Snapshot catalog = await repository.LoadProfilesAsync(CancellationToken.None);
        IClaudeAccountRepository.SaveResult save = await repository.SaveProfileAsync(
            profile,
            desktopSnapshot,
            catalog.Sha256,
            CancellationToken.None
        );
        CryptographicOperations.ZeroMemory(desktopSnapshot.Cookies);
        CryptographicOperations.ZeroMemory(desktopSnapshot.CookieJournal!);
        CryptographicOperations.ZeroMemory(desktopSnapshot.CookieWriteAheadLog!);
        CryptographicOperations.ZeroMemory(desktopSnapshot.CookieSharedMemory!);
        ClaudeAccountProfile.DesktopSnapshot? restored = await repository.LoadDesktopSnapshotAsync(
            ProfileId,
            CancellationToken.None
        );

        Assert.That(save.Saved, Is.True);
        Assert.That(save.Conflict, Is.False);
        Assert.That(save.Snapshot!.Profiles, Has.Count.EqualTo(1));
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!.TokenCacheJson, Is.EqualTo(desktopSnapshot.TokenCacheJson));
        Assert.That(restored.Cookies, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        Assert.That(restored.CookieJournal, Is.EqualTo(new byte[] { 5, 6 }));
        Assert.That(restored.CookieWriteAheadLog, Is.EqualTo(new byte[] { 7, 8, 9 }));
        Assert.That(restored.CookieSharedMemory, Is.EqualTo(new byte[] { 10, 11 }));
        Assert.That(restored.CookieSidecarSnapshotVersion, Is.EqualTo(1));
        Assert.That(restored.DataRootFingerprint, Is.EqualTo(installation.DataRootFingerprint));

        string encryptedSnapshotPath = Path.Combine(
            appDataPath,
            "claude-profiles",
            "snapshots",
            ProfileId.ToString("N") + "-1.json"
        );
        string persistedSnapshot = await File.ReadAllTextAsync(encryptedSnapshotPath);
        Assert.That(persistedSnapshot, Does.Not.Contain(fixtureToken));
        Assert.That(desktopSnapshot.ToString(), Does.Not.Contain(fixtureToken));
        CryptographicOperations.ZeroMemory(restored.Cookies);
        CryptographicOperations.ZeroMemory(restored.CookieJournal!);
        CryptographicOperations.ZeroMemory(restored.CookieWriteAheadLog!);
        CryptographicOperations.ZeroMemory(restored.CookieSharedMemory!);
    }

    [Test]
    public async Task LoadDesktopSnapshotAsync_F002_MapsLegacyMissingSidecarFieldsToVersionZero() {
        using WindowsClaudeTestWorkspace workspace = new();
        string appDataPath = Path.Combine(workspace.RootPath, "application-data");
        CurrentUserDataDirectory dataDirectory = new(appDataPath);
        WindowsClaudeAccountRepository repository = new(dataDirectory, new AtomicJsonFileStore(dataDirectory));
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        SecurityIdentifier currentSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The test process has no Windows user SID.");
        // 舊版 profile 沒有來源 binding，快照為 schema 1 文件且不含 sidecar 欄位。
        ClaudeAccountProfile profile = CreateProfile(installation, currentSid.Value, revision: 1) with {
            BindingId = null,
            ProvenanceSchemaVersion = 0
        };
        WriteCatalog(appDataPath, profile);
        DpapiSecretProtector protector = new();
        string snapshotPath = Path.Combine(
            appDataPath,
            "claude-profiles",
            "snapshots",
            ProfileId.ToString("N") + "-1.json"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        await File.WriteAllTextAsync(snapshotPath, JsonSerializer.Serialize(new {
            schemaVersion = 1,
            profileId = ProfileId,
            profileRevision = 1,
            protectedTokenCacheBase64 = Convert.ToBase64String(
                protector.Protect(System.Text.Encoding.UTF8.GetBytes("{\"accessToken\":\"fixture-token\"}"))
            ),
            protectedTokenCacheV2Base64 = (string?)null,
            lastKnownAccountUuid = AccountId.ToString("D"),
            cookiesBase64 = Convert.ToBase64String([1, 2, 3, 4]),
            cookieJournalBase64 = Convert.ToBase64String([5, 6]),
            cookieRelativePath = "Network/Cookies",
            windowsUserSid = currentSid.Value,
            installationId = installation.InstallationId,
            dataRootFingerprint = installation.DataRootFingerprint,
            localStateEncryptedKeySha256 = new string('a', 64),
            capturedAtUtc = ClaudeTestData.Now
        }));

        ClaudeAccountProfile.DesktopSnapshot? restored = await repository.LoadDesktopSnapshotAsync(
            ProfileId,
            CancellationToken.None
        );

        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!.CookieSidecarSnapshotVersion, Is.Zero);
        Assert.That(restored.CookieWriteAheadLog, Is.Null);
        Assert.That(restored.CookieSharedMemory, Is.Null);
        CryptographicOperations.ZeroMemory(restored.Cookies);
        CryptographicOperations.ZeroMemory(restored.CookieJournal!);
    }

    [Test]
    public async Task SaveProfileAsync_F002_RejectsLegacySnapshotWithoutSidecarExistenceEvidence() {
        using WindowsClaudeTestWorkspace workspace = new();
        string appDataPath = Path.Combine(workspace.RootPath, "application-data");
        CurrentUserDataDirectory dataDirectory = new(appDataPath);
        WindowsClaudeAccountRepository repository = new(dataDirectory, new AtomicJsonFileStore(dataDirectory));
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        SecurityIdentifier currentSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The test process has no Windows user SID.");
        ClaudeAccountProfile profile = CreateProfile(installation, currentSid.Value, revision: 1);
        ClaudeAccountProfile.DesktopSnapshot legacySnapshot = CreateSnapshot(installation, currentSid.Value, version: 0);
        IClaudeAccountRepository.Snapshot catalog = await repository.LoadProfilesAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(async () => {
            await repository.SaveProfileAsync(profile, legacySnapshot, catalog.Sha256, CancellationToken.None);
        });
    }

    [Test]
    public async Task SaveProfileAsync_F002_PreservesExplicitlyAbsentSidecarsInVersionOneSnapshot() {
        using WindowsClaudeTestWorkspace workspace = new();
        string appDataPath = Path.Combine(workspace.RootPath, "application-data");
        CurrentUserDataDirectory dataDirectory = new(appDataPath);
        WindowsClaudeAccountRepository repository = new(dataDirectory, new AtomicJsonFileStore(dataDirectory));
        SecurityIdentifier currentSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The test process has no Windows user SID.");
        ClaudeAccountProfile profile = CreateProfile(workspace.Installation, currentSid.Value, revision: 1);
        ClaudeAccountProfile.DesktopSnapshot snapshot = CreateSnapshot(
            workspace.Installation,
            currentSid.Value,
            version: 1
        ) with {
            CookieJournal = null,
            CookieWriteAheadLog = null,
            CookieSharedMemory = null
        };
        IClaudeAccountRepository.Snapshot catalog = await repository.LoadProfilesAsync(CancellationToken.None);

        IClaudeAccountRepository.SaveResult saved = await repository.SaveProfileAsync(
            profile,
            snapshot,
            catalog.Sha256,
            CancellationToken.None
        );
        ClaudeAccountProfile.DesktopSnapshot? restored = await repository.LoadDesktopSnapshotAsync(
            profile.ProfileId,
            CancellationToken.None
        );

        Assert.That(saved.Saved, Is.True);
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!.CookieSidecarSnapshotVersion, Is.EqualTo(1));
        Assert.That(restored.CookieJournal, Is.Null);
        Assert.That(restored.CookieWriteAheadLog, Is.Null);
        Assert.That(restored.CookieSharedMemory, Is.Null);
        CryptographicOperations.ZeroMemory(snapshot.Cookies);
        CryptographicOperations.ZeroMemory(restored.Cookies);
    }

    [Test]
    public async Task LoadSwitchTransactionAsync_MapsUnknownStagesToRecoverableRollbackState() {
        using WindowsClaudeTestWorkspace workspace = new();
        string appDataPath = Path.Combine(workspace.RootPath, "application-data");
        CurrentUserDataDirectory dataDirectory = new(appDataPath);
        WindowsClaudeAccountRepository repository = new(dataDirectory, new AtomicJsonFileStore(dataDirectory));
        string journalPath = Path.Combine(appDataPath, "claude-profiles", "switch-transaction.json");
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);

        await File.WriteAllTextAsync(journalPath, CreateJournalJson("futureStage", "futureRecoveryStage"));
        ClaudeSwitchTransaction? transaction = await repository.LoadSwitchTransactionAsync(CancellationToken.None);

        Assert.That(transaction, Is.Not.Null);
        Assert.That(transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RecoveryRequired));
        Assert.That(transaction.RecoveryFromStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.TargetVerified));
        Assert.That(transaction.FailureCode, Is.EqualTo("unknown-journal-stage"));

        // 舊版 journal 沒有來源 binding；未完成的 journal 不可被重新綁定或取代。
        Assert.That(transaction.ProvenanceSchemaVersion, Is.Zero);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await repository.SaveSwitchTransactionAsync(transaction with {
            InstallationId = workspace.Installation.InstallationId,
            ProvenanceSchemaVersion = 1,
            RootBinding = workspace.Installation.Binding
        }, CancellationToken.None));

        await File.WriteAllTextAsync(journalPath, CreateJournalJson("recoveryRequired", "futureRecoveryStage"));
        ClaudeSwitchTransaction? invalidRecoveryStage = await repository.LoadSwitchTransactionAsync(CancellationToken.None);
        Assert.That(invalidRecoveryStage!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RecoveryRequired));
        Assert.That(invalidRecoveryStage.RecoveryFromStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.TargetVerified));
    }

    [Test]
    public async Task SaveSwitchTransactionAsync_T303_RoundTripsHistoryPreparationStartedStage() {
        using WindowsClaudeTestWorkspace workspace = new();
        string appDataPath = Path.Combine(workspace.RootPath, "application-data");
        CurrentUserDataDirectory dataDirectory = new(appDataPath);
        WindowsClaudeAccountRepository repository = new(dataDirectory, new AtomicJsonFileStore(dataDirectory));
        ClaudeSwitchTransaction transaction = new ClaudeSwitchTransaction(
            Guid.NewGuid(),
            ProfileId,
            AccountId,
            2,
            1,
            workspace.Installation.InstallationId,
            ClaudeSwitchTransaction.Stage.HistoryPreparationStarted,
            ClaudeTestData.Now.AddMinutes(-1),
            ClaudeTestData.Now,
            null
        ) with {
            ProvenanceSchemaVersion = 1,
            RootBinding = workspace.Installation.Binding
        };

        await repository.SaveSwitchTransactionAsync(transaction, CancellationToken.None);
        ClaudeSwitchTransaction? restored = await repository.LoadSwitchTransactionAsync(CancellationToken.None);

        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!.TransactionId, Is.EqualTo(transaction.TransactionId));
        Assert.That(restored.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.HistoryPreparationStarted));
    }

    [TestCase(ClaudeSwitchTransaction.Stage.DesktopClosed, 1, true)]
    [TestCase(ClaudeSwitchTransaction.Stage.DesktopClosed, 2, false)]
    [TestCase(ClaudeSwitchTransaction.Stage.Prepared, 1, false)]
    public async Task SaveSwitchTransactionAsync_SourceCaptureRevision_RestrictsChanges(
        ClaudeSwitchTransaction.Stage initialStage, int increment, bool allowed
    ) {
        using WindowsClaudeTestWorkspace workspace = new();
        CurrentUserDataDirectory directory = new(Path.Combine(workspace.RootPath, "application-data"));
        WindowsClaudeAccountRepository repository = new(directory, new AtomicJsonFileStore(directory));
        ClaudeSwitchTransaction transaction = new(
            Guid.NewGuid(), ProfileId, AccountId, 2, 1, workspace.Installation.InstallationId,
            initialStage, ClaudeTestData.Now.AddMinutes(-1), ClaudeTestData.Now, null,
            ProvenanceSchemaVersion: 1, RootBinding: workspace.Installation.Binding
        );
        await repository.SaveSwitchTransactionAsync(transaction, CancellationToken.None);
        ClaudeSwitchTransaction updated = transaction with {
            CurrentStage = ClaudeSwitchTransaction.Stage.SourceCaptured,
            SourceProfileRevision = 2 + increment
        };

        if (allowed) {
            await repository.SaveSwitchTransactionAsync(updated, CancellationToken.None);
            ClaudeSwitchTransaction? saved = await repository.LoadSwitchTransactionAsync(CancellationToken.None);
            Assert.That(saved!.SourceProfileRevision, Is.EqualTo(3));
        } else {
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await repository.SaveSwitchTransactionAsync(updated, CancellationToken.None));
        }
    }

    private static string CreateJournalJson(string currentStage, string recoveryFromStage) {
        return JsonSerializer.Serialize(new {
            schemaVersion = 1,
            transaction = new {
                transactionId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
                sourceProfileId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                targetProfileId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                sourceProfileRevision = 1,
                targetProfileRevision = 1,
                installationId = "fixture-installation",
                currentStage,
                startedAtUtc = "2026-10-26T12:00:00Z",
                updatedAtUtc = "2026-10-26T12:00:00Z",
                failureCode = (string?)null,
                recoveryFromStage
            }
        });
    }

    [Test]
    public async Task SaveCatalogMetadataAsync_ReordersRenamesAndDeletesOnlyRemovedSnapshots() {
        using WindowsClaudeTestWorkspace workspace = new();
        string appDataPath = Path.Combine(workspace.RootPath, "application-data");
        CurrentUserDataDirectory dataDirectory = new(appDataPath);
        WindowsClaudeAccountRepository repository = new(dataDirectory, new AtomicJsonFileStore(dataDirectory));
        ClaudeAccountProfile first = CreateCatalogProfile(Guid.NewGuid(), "first@example.test");
        ClaudeAccountProfile second = CreateCatalogProfile(Guid.NewGuid(), "second@example.test");
        ClaudeAccountProfile removed = CreateCatalogProfile(Guid.NewGuid(), "removed@example.test");
        WriteCatalog(appDataPath, first, second, removed);
        string removedSnapshotPath = WriteSnapshotPlaceholder(appDataPath, removed.ProfileId);
        string keptSnapshotPath = WriteSnapshotPlaceholder(appDataPath, first.ProfileId);
        IClaudeAccountRepository.Snapshot catalog = await repository.LoadProfilesAsync(CancellationToken.None);

        IClaudeAccountRepository.SaveResult result = await repository.SaveCatalogMetadataAsync(
            [second, first with { Alias = "Renamed" }],
            catalog.Sha256,
            CancellationToken.None
        );
        IClaudeAccountRepository.Snapshot reloaded = await repository.LoadProfilesAsync(CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(result.Saved, Is.True);
            Assert.That(reloaded.Profiles.Select(profile => profile.ProfileId), Is.EqualTo(new[] { second.ProfileId, first.ProfileId }));
            Assert.That(reloaded.Profiles[1].Alias, Is.EqualTo("Renamed"));
            Assert.That(reloaded.Profiles[1].ProfileRevision, Is.EqualTo(first.ProfileRevision));
            Assert.That(File.Exists(removedSnapshotPath), Is.False);
            Assert.That(File.Exists(keptSnapshotPath), Is.True);
        });
    }

    [Test]
    public async Task SaveCatalogMetadataAsync_RejectsChangesBeyondAlias() {
        using WindowsClaudeTestWorkspace workspace = new();
        string appDataPath = Path.Combine(workspace.RootPath, "application-data");
        CurrentUserDataDirectory dataDirectory = new(appDataPath);
        WindowsClaudeAccountRepository repository = new(dataDirectory, new AtomicJsonFileStore(dataDirectory));
        ClaudeAccountProfile profile = CreateCatalogProfile(Guid.NewGuid(), "first@example.test");
        WriteCatalog(appDataPath, profile);
        IClaudeAccountRepository.Snapshot catalog = await repository.LoadProfilesAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(async () => await repository.SaveCatalogMetadataAsync(
            [profile with { ProfileRevision = 2 }],
            catalog.Sha256,
            CancellationToken.None
        ));
        await Assert.ThrowsAsync<ArgumentException>(async () => await repository.SaveCatalogMetadataAsync(
            [CreateCatalogProfile(Guid.NewGuid(), "unknown@example.test")],
            catalog.Sha256,
            CancellationToken.None
        ));
    }

    [Test]
    public async Task SaveCatalogMetadataAsync_StaleHashReturnsConflictWithoutWriting() {
        using WindowsClaudeTestWorkspace workspace = new();
        string appDataPath = Path.Combine(workspace.RootPath, "application-data");
        CurrentUserDataDirectory dataDirectory = new(appDataPath);
        WindowsClaudeAccountRepository repository = new(dataDirectory, new AtomicJsonFileStore(dataDirectory));
        ClaudeAccountProfile profile = CreateCatalogProfile(Guid.NewGuid(), "first@example.test");
        WriteCatalog(appDataPath, profile);

        IClaudeAccountRepository.SaveResult result = await repository.SaveCatalogMetadataAsync(
            [profile with { Alias = "Renamed" }],
            new string('f', 64),
            CancellationToken.None
        );
        IClaudeAccountRepository.Snapshot reloaded = await repository.LoadProfilesAsync(CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(result.Saved, Is.False);
            Assert.That(result.Conflict, Is.True);
            Assert.That(reloaded.Profiles.Single().Alias, Is.EqualTo(profile.Alias));
        });
    }

    private static ClaudeAccountProfile CreateCatalogProfile(Guid profileId, string maskedEmail) {
        return new(
            profileId,
            "Fixture account",
            maskedEmail,
            Guid.NewGuid().ToString("D"),
            OrganizationId.ToString("D"),
            "fixture-installation",
            new string('b', 64),
            "S-1-5-21-100-200-300-1001",
            new string('a', 64),
            1,
            ClaudeTestData.Now.AddDays(-1),
            ClaudeTestData.Now,
            ClaudeAccountProfile.VerificationStatus.Verified,
            Guid.NewGuid(),
            1
        );
    }

    private static void WriteCatalog(string appDataPath, params ClaudeAccountProfile[] profiles) {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) {
            WriteIndented = true
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        string catalogPath = Path.Combine(appDataPath, "claude-profiles", "catalog.json");
        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        File.WriteAllBytes(
            catalogPath,
            JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Profiles = profiles }, options)
        );
    }

    private static string WriteSnapshotPlaceholder(string appDataPath, Guid profileId) {
        string snapshotPath = Path.Combine(appDataPath, "claude-profiles", "snapshots", profileId.ToString("N") + "-1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        File.WriteAllText(snapshotPath, "{}");
        return snapshotPath;
    }

    private static ClaudeAccountProfile CreateProfile(
        IClaudeDesktopInstallationLocator.Installation installation,
        string sid,
        long revision
    ) {
        return new(
            ProfileId,
            "Fixture account",
            "fixture@example.test",
            AccountId.ToString("D"),
            OrganizationId.ToString("D"),
            installation.InstallationId,
            installation.DataRootFingerprint,
            sid,
            new string('a', 64),
            revision,
            ClaudeTestData.Now.AddDays(-1),
            ClaudeTestData.Now,
            ClaudeAccountProfile.VerificationStatus.Verified,
            CreateBinding(installation, sid).Source.BindingId,
            1
        );
    }

    private static ClaudeDesktopRootBinding CreateBinding(
        IClaudeDesktopInstallationLocator.Installation installation,
        string sid
    ) {
        ClaudeDesktopRootBinding binding = installation.Binding
            ?? throw new InvalidOperationException("The fixture installation has no binding.");
        return binding with {
            Source = binding.Source with { WindowsUserSid = sid, InstallationId = installation.InstallationId },
            LocalStateEncryptedKeySha256 = new string('a', 64)
        };
    }

    private static ClaudeAccountProfile.DesktopSnapshot CreateSnapshot(
        IClaudeDesktopInstallationLocator.Installation installation,
        string sid,
        int version
    ) {
        return CreateLegacySnapshot(installation, sid, version) with {
            ProvenanceSchemaVersion = 1,
            RootBinding = CreateBinding(installation, sid)
        };
    }

    private static ClaudeAccountProfile.DesktopSnapshot CreateLegacySnapshot(
        IClaudeDesktopInstallationLocator.Installation installation,
        string sid,
        int version
    ) {
        return new(
            "{\"accessToken\":\"fixture-token\"}",
            null,
            AccountId.ToString("D"),
            [1, 2, 3, 4],
            [5, 6],
            "Network/Cookies",
            sid,
            installation.InstallationId,
            installation.DataRootFingerprint,
            new string('a', 64),
            ClaudeTestData.Now,
            version == 1 ? [7, 8] : null,
            version == 1 ? [9] : null,
            version
        );
    }
}
