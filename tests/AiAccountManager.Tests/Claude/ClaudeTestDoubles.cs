using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Application.Operations;
using CloudyWing.AiAccountManager.Application.Runtime;
using CloudyWing.AiAccountManager.Application.Security;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Claude;

internal static class ClaudeTestData {
    public static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    public static readonly Guid BindingId = new("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");

    public static ClaudeDesktopRootBinding.Observation Observation { get; } = new(
        1,
        BindingId,
        "S-1-5-21-100-200-300-1001",
        "msix:Claude.Package_abcd1234wxyz1",
        IClaudeDesktopInstallationLocator.InstallKind.Msix,
        "Claude.Package_abcd1234wxyz1!Claude",
        "Claude.Package_abcd1234wxyz1",
        "Claude.Package_1.0.0.0_x64__abcd1234wxyz1",
        null,
        null,
        null,
        "C:\\Synthetic\\ClaudeData",
        "Network/Cookies",
        123,
        "synthetic-root-file-id",
        1,
        [new ClaudeDesktopRootBinding.ObservedProcess(1234, Now)]
    );

    public static ClaudeDesktopRootBinding Binding { get; } = new(Observation, new string('a', 64));

    public static IClaudeDesktopInstallationLocator.Installation Installation { get; } = new(
        "msix:Claude.Package_abcd1234wxyz1",
        IClaudeDesktopInstallationLocator.InstallKind.Msix,
        "C:\\Synthetic\\ClaudeData",
        "root-fingerprint-a",
        "Claude.Package_abcd1234wxyz1!Claude",
        null,
        Observation,
        Binding
    );

    public static ClaudeAccountProfile CreateProfile(
        Guid profileId,
        string accountUuid,
        string organizationUuid,
        long revision = 1,
        string? alias = null,
        string? keyDigest = null
    ) {
        return new(
            profileId,
            alias,
            "a***@example.test",
            accountUuid,
            organizationUuid,
            Installation.InstallationId,
            Installation.DataRootFingerprint,
            "S-1-5-21-100-200-300-1001",
            keyDigest ?? new string('a', 64),
            revision,
            Now.AddDays(-2),
            Now.AddMinutes(-5),
            ClaudeAccountProfile.VerificationStatus.Verified,
            BindingId,
            1
        );
    }

    public static ClaudeAccountProfile.DesktopSnapshot CreateSnapshot(
        string accountUuid,
        string tokenLabel,
        string? keyDigest = null
    ) {
        return new(
            tokenLabel,
            $"{{\"accessToken\":\"{tokenLabel}\"}}",
            accountUuid,
            [1, 2, 3],
            [4, 5],
            "Network/Cookies",
            "S-1-5-21-100-200-300-1001",
            Installation.InstallationId,
            Installation.DataRootFingerprint,
            keyDigest ?? new string('a', 64),
            Now.AddMinutes(-2),
            [6, 7],
            [8],
            1,
            1,
            Binding with { LocalStateEncryptedKeySha256 = keyDigest ?? new string('a', 64) }
        );
    }

    public static IClaudeOAuthClient.ProfileResult VerifiedProfile(
        string accountUuid,
        string organizationUuid,
        string? email = "user@example.test"
    ) {
        return new(IClaudeOAuthClient.Outcome.Success, accountUuid, organizationUuid, email, null, null);
    }

    public static JsonElement ReadJson(string json) {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonElement ReadFixture(string fixtureName) {
        string fixturePath = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Claude",
            "Fixtures",
            fixtureName
        );
        return ReadJson(File.ReadAllText(fixturePath));
    }

    public static ClaudeOperationGate CreateOperationGate() {
        return new(new ExclusiveOperationCoordinator());
    }
}

internal sealed class ClaudeTestClock : IClock {
    public DateTimeOffset UtcNow { get; set; } = ClaudeTestData.Now;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        UtcNow += delay;
        return Task.CompletedTask;
    }
}

internal sealed class ClaudeTestFingerprinter : IIdentityFingerprinter {
    public string CreateFingerprint(string? accountId, string? email, string authMode) {
        return $"{authMode}:{accountId ?? email ?? "unknown"}";
    }

    public string MaskEmail(string? email) {
        return string.IsNullOrWhiteSpace(email) ? string.Empty : "masked@example.test";
    }
}

internal sealed class ClaudeTestAccountRepository : IClaudeAccountRepository {
    private readonly Dictionary<Guid, ClaudeAccountProfile.DesktopSnapshot> desktopSnapshots = [];
    private long version = 1;

    public ClaudeTestAccountRepository(params ClaudeAccountProfile[] profiles) {
        Profiles = profiles.ToList();
    }

    public List<ClaudeAccountProfile> Profiles { get; }

    public ClaudeSwitchTransaction? Transaction { get; set; }

    public int SaveProfileCallCount { get; private set; }

    public int SaveTransactionCallCount { get; private set; }

    public bool ThrowOnCommittedTransactionSave { get; set; }

    public Func<CancellationToken, Task>? BeforeSaveProfileAsync { get; set; }

    public string CatalogHash => $"catalog-{version}";

    public void AddSnapshot(Guid profileId, ClaudeAccountProfile.DesktopSnapshot snapshot) {
        desktopSnapshots[profileId] = snapshot;
    }

    public Func<CancellationToken, Task>? BeforeLoadProfilesAsync { get; set; }

    public async Task<IClaudeAccountRepository.Snapshot> LoadProfilesAsync(CancellationToken cancellationToken) {
        if (BeforeLoadProfilesAsync is not null) {
            await BeforeLoadProfilesAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new IClaudeAccountRepository.Snapshot(Profiles.ToArray(), CatalogHash);
    }

    public Task<ClaudeAccountProfile.DesktopSnapshot?> LoadDesktopSnapshotAsync(
        Guid profileId,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        desktopSnapshots.TryGetValue(profileId, out ClaudeAccountProfile.DesktopSnapshot? snapshot);
        return Task.FromResult(snapshot is null ? null : snapshot with {
            Cookies = snapshot.Cookies.ToArray(),
            CookieJournal = snapshot.CookieJournal?.ToArray(),
            CookieWriteAheadLog = snapshot.CookieWriteAheadLog?.ToArray(),
            CookieSharedMemory = snapshot.CookieSharedMemory?.ToArray()
        });
    }

    public async Task<IClaudeAccountRepository.SaveResult> SaveProfileAsync(
        ClaudeAccountProfile profile,
        ClaudeAccountProfile.DesktopSnapshot desktopSnapshot,
        string expectedSha256,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        SaveProfileCallCount++;
        if (BeforeSaveProfileAsync is not null) {
            await BeforeSaveProfileAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(expectedSha256, CatalogHash, StringComparison.Ordinal)) {
            return new IClaudeAccountRepository.SaveResult(false, null, true);
        }

        int index = Profiles.FindIndex(candidate => candidate.ProfileId == profile.ProfileId);
        if (index < 0) {
            Profiles.Add(profile);
        } else {
            Profiles[index] = profile;
        }

        desktopSnapshots[profile.ProfileId] = desktopSnapshot;
        version++;
        return new IClaudeAccountRepository.SaveResult(
            true,
            new IClaudeAccountRepository.Snapshot(Profiles.ToArray(), CatalogHash),
            false
        );
    }

    public Task<IClaudeAccountRepository.SaveResult> SaveCatalogMetadataAsync(
        IReadOnlyList<ClaudeAccountProfile> profiles,
        string expectedSha256,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(expectedSha256, CatalogHash, StringComparison.Ordinal)) {
            return Task.FromResult(new IClaudeAccountRepository.SaveResult(false, null, true));
        }

        foreach (Guid removedProfileId in Profiles
            .Select(profile => profile.ProfileId)
            .Except(profiles.Select(profile => profile.ProfileId))
            .ToArray()) {
            desktopSnapshots.Remove(removedProfileId);
        }

        ClaudeAccountProfile[] updatedProfiles = profiles.ToArray();
        Profiles.Clear();
        Profiles.AddRange(updatedProfiles);
        version++;
        return Task.FromResult(new IClaudeAccountRepository.SaveResult(
            true,
            new IClaudeAccountRepository.Snapshot(Profiles.ToArray(), CatalogHash),
            false
        ));
    }

    public Task<ClaudeSwitchTransaction?> LoadSwitchTransactionAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Transaction);
    }

    public Task SaveSwitchTransactionAsync(
        ClaudeSwitchTransaction transaction,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnCommittedTransactionSave
            && transaction.CurrentStage == ClaudeSwitchTransaction.Stage.Committed) {
            throw new IOException("Synthetic committed journal update failure.");
        }

        if (Transaction is ClaudeSwitchTransaction current
            && current.CurrentStage is not (ClaudeSwitchTransaction.Stage.Committed or ClaudeSwitchTransaction.Stage.RolledBack)
            && (current.ProvenanceSchemaVersion != 1 || transaction.ProvenanceSchemaVersion != 1
                || current.RootBinding is null || transaction.RootBinding is null
                || !string.Equals(current.InstallationId, current.RootBinding.Source.InstallationId, StringComparison.Ordinal)
                || !string.Equals(transaction.InstallationId, transaction.RootBinding.Source.InstallationId, StringComparison.Ordinal)
                || current.TransactionId != transaction.TransactionId
                || current.SourceProfileId != transaction.SourceProfileId
                || current.TargetProfileId != transaction.TargetProfileId
                || (current.SourceProfileRevision != transaction.SourceProfileRevision
                    && !(current.CurrentStage == ClaudeSwitchTransaction.Stage.DesktopClosed
                        && transaction.CurrentStage == ClaudeSwitchTransaction.Stage.SourceCaptured
                        && current.SourceProfileRevision < long.MaxValue
                        && transaction.SourceProfileRevision == current.SourceProfileRevision + 1))
                || current.TargetProfileRevision != transaction.TargetProfileRevision
                || !BindingsEqual(current.RootBinding, transaction.RootBinding))) {
            throw new InvalidDataException("A pending Claude switch journal cannot be replaced or rebound.");
        }

        SaveTransactionCallCount++;
        Transaction = transaction;
        return Task.CompletedTask;
    }

    private static bool BindingsEqual(ClaudeDesktopRootBinding? left, ClaudeDesktopRootBinding? right) {
        if (left is null || right is null) {
            return left is null && right is null;
        }

        ClaudeDesktopRootBinding.Observation a = left.Source;
        ClaudeDesktopRootBinding.Observation b = right.Source;
        return a.SchemaVersion == b.SchemaVersion && a.BindingId == b.BindingId
            && string.Equals(a.WindowsUserSid, b.WindowsUserSid, StringComparison.Ordinal)
            && string.Equals(a.InstallationId, b.InstallationId, StringComparison.Ordinal)
            && a.Kind == b.Kind
            && string.Equals(a.AppUserModelId, b.AppUserModelId, StringComparison.Ordinal)
            && string.Equals(a.PackageFamilyName, b.PackageFamilyName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.PackageFullName, b.PackageFullName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.DirectRegistrationSource, b.DirectRegistrationSource, StringComparison.Ordinal)
            && string.Equals(a.ExecutablePath, b.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.ExecutableSha256, b.ExecutableSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.DataRoot, b.DataRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.CookieRelativePath, b.CookieRelativePath, StringComparison.OrdinalIgnoreCase)
            && a.RootVolumeSerialNumber == b.RootVolumeSerialNumber
            && string.Equals(a.RootFileId, b.RootFileId, StringComparison.Ordinal)
            && a.LayoutVersion == b.LayoutVersion && a.Processes.SequenceEqual(b.Processes)
            && string.Equals(left.LocalStateEncryptedKeySha256, right.LocalStateEncryptedKeySha256, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class ClaudeTestInstallationLocator : IClaudeDesktopInstallationLocator {
    public IClaudeDesktopInstallationLocator.ObservationResult? ObservationResult { get; set; }

    public Action? OnResolveBound { get; set; }

    public int ResolveBoundCallCount { get; private set; }

    public IClaudeDesktopInstallationLocator.LocateResult Result { get; set; } = new(
        IClaudeDesktopInstallationLocator.LocateStatus.Found,
        ClaudeTestData.Installation,
        null
    );

    public Task<IClaudeDesktopInstallationLocator.LocateResult> LocateAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result);
    }

    public Task<IClaudeDesktopInstallationLocator.ObservationResult> ObserveRunningAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (ObservationResult is not null) {
            return Task.FromResult(ObservationResult);
        }
        IClaudeDesktopInstallationLocator.Installation? installation = Result.Installation;
        ClaudeDesktopRootBinding.Observation? observation = installation?.Observation;
        return Task.FromResult(new IClaudeDesktopInstallationLocator.ObservationResult(
            Result.Status,
            observation,
            Result.FailureCode,
            installation
        ));
    }

    public Task<IClaudeDesktopInstallationLocator.LocateResult> ResolveObservationAfterStopAsync(
        ClaudeDesktopRootBinding.Observation observation,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result.Installation is null
            ? Result
            : Result with { Installation = Result.Installation with { Observation = observation, Binding = null } });
    }

    public Task<IClaudeDesktopInstallationLocator.LocateResult> ResolveBoundAsync(
        ClaudeDesktopRootBinding binding,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        ResolveBoundCallCount++;
        OnResolveBound?.Invoke();
        return Task.FromResult(Result.Installation is null
            ? Result
            : Result with { Installation = Result.Installation with { Binding = binding } });
    }
}

internal sealed class ClaudeTestProfileStore : IClaudeDesktopProfileStore {
    private readonly Dictionary<string, IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate>> candidatesBySnapshotToken = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, (ClaudeAccountProfile.DesktopSnapshot Before, ClaudeAccountProfile.DesktopSnapshot After)> transactions = [];

    public ClaudeAccountProfile.DesktopSnapshot? CurrentSnapshot { get; set; }

    public IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> CurrentCandidates { get; set; } = [];

    public List<string> Operations { get; } = [];

    public int ReadCurrentCallCount { get; private set; }

    public int RestoreCallCount { get; private set; }

    public bool ThrowOnRead { get; set; }

    public bool ThrowOnRestore { get; set; }

    public bool SnapshotLeaseValidationResult { get; set; } = true;

    public bool ThrowOnSnapshotLeaseValidation { get; set; }

    public IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate>? StableSnapshotCandidates { get; set; }

    public ClaudeTestSnapshotLease? LastSnapshotLease { get; private set; }

    public void SetCandidates(ClaudeAccountProfile.DesktopSnapshot snapshot, params string[] accessTokens) {
        candidatesBySnapshotToken[snapshot.TokenCacheJson ?? string.Empty] = accessTokens
            .Select(token => new IClaudeDesktopProfileStore.TokenCandidate(token, "oauth:tokenCacheV2"))
            .ToArray();
    }

    public Task<IClaudeDesktopProfileStore.ReadResult> ReadCurrentAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCurrentCallCount++;
        Operations.Add("read-current");
        if (ThrowOnRead) {
            throw new IOException("Synthetic read failure.");
        }

        if (CurrentSnapshot is null) {
            throw new InvalidDataException("No synthetic current snapshot was set.");
        }

        return Task.FromResult(new IClaudeDesktopProfileStore.ReadResult(
            CurrentSnapshot.LastKnownAccountUuid,
            CurrentCandidates
        ));
    }

    public Task<IClaudeDesktopProfileStore.StableSnapshotLease> CaptureStableSnapshotAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("capture-stable-snapshot");
        if (CurrentSnapshot is null) {
            throw new InvalidDataException("No synthetic stable snapshot was set.");
        }

        LastSnapshotLease = new(
            CurrentSnapshot,
            StableSnapshotCandidates ?? CurrentCandidates,
            () => SnapshotLeaseValidationResult,
            () => ThrowOnSnapshotLeaseValidation,
            Operations
        );
        return Task.FromResult<IClaudeDesktopProfileStore.StableSnapshotLease>(LastSnapshotLease);
    }

    public IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> GetTokenCandidates(ClaudeAccountProfile.DesktopSnapshot snapshot) {
        candidatesBySnapshotToken.TryGetValue(
            snapshot.TokenCacheJson ?? string.Empty,
            out IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate>? candidates
        );
        return candidates ?? [];
    }

    public Task RestoreAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        ClaudeAccountProfile.DesktopSnapshot snapshot,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreCallCount++;
        Operations.Add("restore");
        if (ThrowOnRestore) {
            throw new IOException("Synthetic restore failure.");
        }

        if (!string.Equals(snapshot.WindowsUserSid, "S-1-5-21-100-200-300-1001", StringComparison.Ordinal)
            || !string.Equals(snapshot.InstallationId, installation.InstallationId, StringComparison.Ordinal)
            || !string.Equals(snapshot.DataRootFingerprint, installation.DataRootFingerprint, StringComparison.Ordinal)) {
            throw new InvalidOperationException("Synthetic security binding mismatch.");
        }

        CurrentSnapshot = snapshot;
        CurrentCandidates = GetTokenCandidates(snapshot);
        return Task.CompletedTask;
    }

    public Task PrepareRestoreAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        ClaudeAccountProfile.DesktopSnapshot targetSnapshot,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        if (CurrentSnapshot is null) {
            throw new InvalidOperationException("No synthetic source snapshot was set.");
        }

        Operations.Add("prepare-restore");
        transactions.Add(transactionId, (CurrentSnapshot, targetSnapshot));
        return Task.CompletedTask;
    }

    public Task ApplyRestoreAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("apply-restore");
        return RestoreAsync(installation, transactions[transactionId].After, cancellationToken);
    }

    public Task RestoreTransactionAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("restore-transaction");
        return RestoreAsync(installation, transactions[transactionId].Before, cancellationToken);
    }

    public Task CommitTransactionAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("commit-transaction");
        // 與實際交易相同，沒有暫存證據時視為已清理完成，讓重試可重複執行。
        transactions.Remove(transactionId);
        return Task.CompletedTask;
    }

    public void SeedTransaction(
        Guid transactionId,
        ClaudeAccountProfile.DesktopSnapshot before,
        ClaudeAccountProfile.DesktopSnapshot after
    ) {
        transactions[transactionId] = (before, after);
    }
}

internal sealed class ClaudeTestSnapshotLease : IClaudeDesktopProfileStore.StableSnapshotLease {
    private readonly Func<bool> validationResult;
    private readonly Func<bool> throwOnValidation;
    private readonly List<string> operations;

    public ClaudeTestSnapshotLease(
        ClaudeAccountProfile.DesktopSnapshot snapshot,
        IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> tokenCandidates,
        Func<bool> validationResult,
        Func<bool> throwOnValidation,
        List<string> operations
    ) {
        Snapshot = snapshot;
        TokenCandidates = tokenCandidates;
        this.validationResult = validationResult;
        this.throwOnValidation = throwOnValidation;
        this.operations = operations;
    }

    public ClaudeAccountProfile.DesktopSnapshot Snapshot { get; }

    public IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> TokenCandidates { get; }

    public int ValidateCallCount { get; private set; }

    public int DisposeCallCount { get; private set; }

    public async Task<bool> ValidateAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCallCount++;
        operations.Add("validate-stable-snapshot");
        await Task.Yield();
        if (throwOnValidation()) {
            throw new IOException("Synthetic stable snapshot validation failure.");
        }

        return validationResult();
    }

    public ValueTask DisposeAsync() {
        DisposeCallCount++;
        operations.Add("dispose-stable-snapshot");
        return ValueTask.CompletedTask;
    }
}

internal sealed class ClaudeTestOAuthClient : IClaudeOAuthClient {
    public Dictionary<string, IClaudeOAuthClient.ProfileResult> ProfileResults { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Queue<IClaudeOAuthClient.ProfileResult>> ProfileResultSequences { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IClaudeOAuthClient.UsageResult> UsageResults { get; } = new(StringComparer.Ordinal);

    public List<string> ProfileTokens { get; } = [];

    public List<string> UsageTokens { get; } = [];

    public HashSet<string> ThrowOnProfile { get; } = new(StringComparer.Ordinal);

    public void SetProfileSequence(string token, params IClaudeOAuthClient.ProfileResult[] results) {
        ProfileResultSequences[token] = new Queue<IClaudeOAuthClient.ProfileResult>(results);
    }

    public Task<IClaudeOAuthClient.ProfileResult> GetProfileAsync(
        string bearerToken,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        ProfileTokens.Add(bearerToken);
        if (ThrowOnProfile.Contains(bearerToken)) {
            return Task.FromException<IClaudeOAuthClient.ProfileResult>(new IOException("Synthetic profile timeout."));
        }

        if (ProfileResultSequences.TryGetValue(bearerToken, out Queue<IClaudeOAuthClient.ProfileResult>? sequence)
            && sequence.TryDequeue(out IClaudeOAuthClient.ProfileResult? sequencedResult)) {
            return Task.FromResult(sequencedResult);
        }

        return Task.FromResult(ProfileResults.TryGetValue(bearerToken, out IClaudeOAuthClient.ProfileResult? result)
            ? result
            : new IClaudeOAuthClient.ProfileResult(IClaudeOAuthClient.Outcome.Error, null, null, null, null, "synthetic_missing"));
    }

    public Task<IClaudeOAuthClient.UsageResult> GetUsageAsync(
        string bearerToken,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        UsageTokens.Add(bearerToken);
        return Task.FromResult(UsageResults.TryGetValue(bearerToken, out IClaudeOAuthClient.UsageResult? result)
            ? result
            : new IClaudeOAuthClient.UsageResult(IClaudeOAuthClient.Outcome.Error, null, null, "synthetic_missing"));
    }
}

internal sealed class ClaudeTestProcessController : IClaudeDesktopProcessController {
    private readonly Queue<IClaudeDesktopProcessController.ProcessStatus> statusSequence = new();
    private readonly Queue<bool> closeResultSequence = new();

    public IClaudeDesktopProcessController.ProcessStatus Status { get; set; } =
        IClaudeDesktopProcessController.ProcessStatus.Running;

    public bool CloseResult { get; set; } = true;

    public bool ThrowOnLaunch { get; set; }

    public List<string> Operations { get; } = [];

    public int CloseCallCount { get; private set; }

    public int LaunchCallCount { get; private set; }

    public void SetStatusSequence(params IClaudeDesktopProcessController.ProcessStatus[] statuses) {
        statusSequence.Clear();
        foreach (IClaudeDesktopProcessController.ProcessStatus status in statuses) {
            statusSequence.Enqueue(status);
        }
    }

    public void SetCloseResultSequence(params bool[] results) {
        closeResultSequence.Clear();
        foreach (bool result in results) {
            closeResultSequence.Enqueue(result);
        }
    }

    public Task<IClaudeDesktopProcessController.ProcessStatus> GetStatusAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("process-status");
        if (statusSequence.TryDequeue(out IClaudeDesktopProcessController.ProcessStatus status)) {
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
        CloseCallCount++;
        Operations.Add("close");
        bool closeResult = closeResultSequence.TryDequeue(out bool result) ? result : CloseResult;
        if (closeResult) {
            Status = IClaudeDesktopProcessController.ProcessStatus.NotRunning;
        }

        return Task.FromResult(closeResult);
    }

    public Task LaunchAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        LaunchCallCount++;
        Operations.Add("launch");
        if (ThrowOnLaunch) {
            throw new IOException("Synthetic launch failure.");
        }

        Status = IClaudeDesktopProcessController.ProcessStatus.Running;
        return Task.CompletedTask;
    }
}

internal sealed class ClaudeTestQuotaRepository : IClaudeQuotaRepository {
    private readonly Dictionary<Guid, IClaudeQuotaRepository.Snapshot> snapshots = [];
    private long version;

    public int SaveCallCount { get; private set; }

    public void Seed(ClaudeQuotaSnapshot.View view) {
        version++;
        snapshots[view.ProfileId] = new(view, $"quota-{version}");
    }

    public Task<IClaudeQuotaRepository.Snapshot?> LoadAsync(Guid profileId, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        snapshots.TryGetValue(profileId, out IClaudeQuotaRepository.Snapshot? snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<IClaudeQuotaRepository.SaveResult> SaveAsync(
        ClaudeQuotaSnapshot.View view,
        string? expectedSha256,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        SaveCallCount++;
        snapshots.TryGetValue(view.ProfileId, out IClaudeQuotaRepository.Snapshot? current);
        if (!string.Equals(expectedSha256, current?.Sha256, StringComparison.Ordinal)) {
            return Task.FromResult(new IClaudeQuotaRepository.SaveResult(false, current, true));
        }

        version++;
        IClaudeQuotaRepository.Snapshot saved = new(view, $"quota-{version}");
        snapshots[view.ProfileId] = saved;
        return Task.FromResult(new IClaudeQuotaRepository.SaveResult(true, saved, false));
    }
}

internal sealed class ClaudeTestHistoryStore : IClaudeSessionHistoryStore {
    private readonly Dictionary<(string Account, string Organization), List<string>> indexes = [];
    private readonly Dictionary<Guid, List<string>?> backups = [];
    private readonly Dictionary<Guid, List<string>> staging = [];

    public Dictionary<string, int> TranscriptTurns { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, byte[]> TranscriptBytes { get; } = new(StringComparer.Ordinal);

    public List<string> Operations { get; } = [];

    public bool ThrowOnApplyBeforeWrite { get; set; }

    public bool ThrowOnPrepareAfterPayload { get; set; }

    public bool ThrowOnApplyAfterWrite { get; set; }

    public bool ThrowOnRestore { get; set; }

    public bool ThrowOnCommit { get; set; }

    public int CommitCallCount { get; private set; }

    public List<ClaudeSessionHistoryCleanupIntent> CommitIntents { get; } = [];

    public Action? BeforePrepare { get; set; }

    public Action<Guid>? AfterPreparationPayload { get; set; }

    public IClaudeSessionHistoryStore.ValidationResult? ValidationOverride { get; set; }

    public void SetIndex(string accountUuid, string organizationUuid, IEnumerable<string> sessionIds) {
        indexes[(accountUuid, organizationUuid)] = sessionIds.ToList();
    }

    public IReadOnlyList<string> GetIndex(string accountUuid, string organizationUuid) {
        return indexes.TryGetValue((accountUuid, organizationUuid), out List<string>? sessions)
            ? sessions.ToArray()
            : [];
    }

    public void SetTranscript(string sessionId, int turns, byte[]? content = null) {
        TranscriptTurns[sessionId] = turns;
        TranscriptBytes[sessionId] = content?.ToArray() ?? System.Text.Encoding.UTF8.GetBytes($"synthetic transcript {turns}");
    }

    public void ContinueTranscript(string sessionId, int turns) {
        TranscriptTurns[sessionId] = turns;
        TranscriptBytes[sessionId] = System.Text.Encoding.UTF8.GetBytes($"synthetic transcript {turns}");
    }

    public Task<IClaudeSessionHistoryStore.ValidationResult> ValidateAccountAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        string accountUuid,
        string organizationUuid,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add($"validate:{accountUuid}");
        if (ValidationOverride is not null) {
            return Task.FromResult(ValidationOverride);
        }

        if (!indexes.TryGetValue((accountUuid, organizationUuid), out List<string>? sessions)) {
            return Task.FromResult(new IClaudeSessionHistoryStore.ValidationResult(
                IClaudeSessionHistoryStore.ValidationStatus.MissingDirectory,
                0,
                "missing_directory"
            ));
        }

        if (sessions.Any(sessionId => !TranscriptTurns.ContainsKey(sessionId))) {
            return Task.FromResult(new IClaudeSessionHistoryStore.ValidationResult(
                IClaudeSessionHistoryStore.ValidationStatus.InvalidTranscriptReference,
                sessions.Count,
                "missing_transcript"
            ));
        }

        return Task.FromResult(new IClaudeSessionHistoryStore.ValidationResult(
            IClaudeSessionHistoryStore.ValidationStatus.Valid,
            sessions.Count,
            null
        ));
    }

    public async Task<IClaudeSessionHistoryStore.PreparationResult> PrepareReplacementAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        string sourceAccountUuid,
        string sourceOrganizationUuid,
        string targetAccountUuid,
        string targetOrganizationUuid,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        Operations.Add("prepare");
        BeforePrepare?.Invoke();
        IClaudeSessionHistoryStore.ValidationResult source = await ValidateAccountAsync(
            installation,
            sourceAccountUuid,
            sourceOrganizationUuid,
            cancellationToken
        ).ConfigureAwait(false);
        if (source.Status != IClaudeSessionHistoryStore.ValidationStatus.Valid) {
            return new(source.Status, source.SessionCount, 0, source.FailureCode);
        }

        List<string>? target = indexes.TryGetValue((targetAccountUuid, targetOrganizationUuid), out List<string>? foundTarget)
            ? foundTarget.ToList()
            : null;
        backups[transactionId] = target;
        staging[transactionId] = indexes[(sourceAccountUuid, sourceOrganizationUuid)].ToList();
        Operations.Add("prepare-payload-written");
        AfterPreparationPayload?.Invoke(transactionId);
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnPrepareAfterPayload) {
            throw new IOException("Synthetic preparation failure after first payload.");
        }

        return new(IClaudeSessionHistoryStore.ValidationStatus.Valid, source.SessionCount, target?.Count ?? 0, null);
    }

    public Task ApplyReplacementAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        string targetAccountUuid,
        string targetOrganizationUuid,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("apply");
        if (ThrowOnApplyBeforeWrite) {
            throw new IOException("Synthetic apply failure before write.");
        }

        indexes[(targetAccountUuid, targetOrganizationUuid)] = staging[transactionId].ToList();
        if (ThrowOnApplyAfterWrite) {
            throw new IOException("Synthetic apply failure after write.");
        }

        return Task.CompletedTask;
    }

    public Task RestoreTargetAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        string targetAccountUuid,
        string targetOrganizationUuid,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("restore-history");
        if (ThrowOnRestore) {
            throw new IOException("Synthetic history restore failure.");
        }

        if (backups.TryGetValue(transactionId, out List<string>? targetBackup)) {
            if (targetBackup is null) {
                indexes.Remove((targetAccountUuid, targetOrganizationUuid));
            } else {
                indexes[(targetAccountUuid, targetOrganizationUuid)] = targetBackup.ToList();
            }
        }

        return Task.CompletedTask;
    }

    public Task CommitAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid transactionId,
        ClaudeSessionHistoryCleanupIntent cleanupIntent,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("commit-history");
        CommitCallCount++;
        CommitIntents.Add(cleanupIntent);
        if (ThrowOnCommit) {
            throw new IOException("Synthetic history cleanup failure.");
        }

        backups.Remove(transactionId);
        staging.Remove(transactionId);
        return Task.CompletedTask;
    }
}
