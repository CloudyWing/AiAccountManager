using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Windows.Security;
using CloudyWing.AiAccountManager.Windows.Storage;

namespace CloudyWing.AiAccountManager.Windows.Claude;

/// <summary>
/// 在目前 Windows 使用者的應用程式資料目錄保存 Claude profiles 與切換交易。
/// </summary>
public sealed class WindowsClaudeAccountRepository : IClaudeAccountRepository {
    private const string CatalogRelativePath = "claude-profiles/catalog.json";
    private const string SnapshotRelativeRoot = "claude-profiles/snapshots";
    private const string TransactionRelativePath = "claude-profiles/switch-transaction.json";
    private const int MaximumCatalogBytes = 4 * 1024 * 1024;
    private const int MaximumSnapshotBytes = 256 * 1024 * 1024;
    private const int MaximumTransactionBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly AtomicJsonFileStore fileStore;
    private readonly DpapiSecretProtector secretProtector;
    private readonly string dataRootPath;

    /// <summary>
    /// 建立受目前使用者 ACL 保護的 Claude account repository。
    /// </summary>
    /// <param name="dataDirectory">Claude profile 資料目錄。</param>
    /// <param name="fileStore">提供原子 JSON 寫入的檔案 store。</param>
    public WindowsClaudeAccountRepository(
        CurrentUserDataDirectory dataDirectory,
        AtomicJsonFileStore fileStore
    ) {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        this.fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        dataRootPath = dataDirectory.EnsureCreated();
        secretProtector = new DpapiSecretProtector();
    }

    /// <inheritdoc />
    public async Task<IClaudeAccountRepository.Snapshot> LoadProfilesAsync(CancellationToken cancellationToken) {
        AtomicJsonFileStore.ReadResult file = await fileStore
            .ReadAsync(CatalogRelativePath, cancellationToken)
            .ConfigureAwait(false);
        if (!file.Exists) {
            return new IClaudeAccountRepository.Snapshot([], "");
        }

        if (file.Content.Length > MaximumCatalogBytes) {
            throw new InvalidDataException("Claude profile catalog exceeded its supported size limit.");
        }

        CatalogDocument document = Deserialize<CatalogDocument>(file.Content, "Claude profile catalog");
        if (document.SchemaVersion != 1 || document.Profiles is null || !IsValidCatalog(document)) {
            throw new InvalidDataException("Claude profile catalog content or schema is invalid.");
        }

        ClaudeAccountProfile[] profiles = document.Profiles.Select(profile =>
            profile.ProvenanceSchemaVersion == 0
                ? profile with { Status = ClaudeAccountProfile.VerificationStatus.Unknown }
                : profile
        ).ToArray();
        return new IClaudeAccountRepository.Snapshot(profiles, file.Sha256);
    }

    /// <inheritdoc />
    public async Task<ClaudeAccountProfile.DesktopSnapshot?> LoadDesktopSnapshotAsync(
        Guid profileId,
        CancellationToken cancellationToken
    ) {
        ValidateProfileId(profileId);
        IClaudeAccountRepository.Snapshot catalog = await LoadProfilesAsync(cancellationToken).ConfigureAwait(false);
        ClaudeAccountProfile? profile = catalog.Profiles.FirstOrDefault(item => item.ProfileId == profileId);
        if (profile is null) {
            return null;
        }

        string relativePath = GetSnapshotRelativePath(profile.ProfileId, profile.ProfileRevision);
        AtomicJsonFileStore.ReadResult file = await fileStore.ReadAsync(relativePath, cancellationToken)
            .ConfigureAwait(false);
        if (!file.Exists) {
            throw new InvalidDataException("A Claude profile snapshot referenced by the catalog is missing.");
        }

        if (file.Content.Length > MaximumSnapshotBytes) {
            throw new InvalidDataException("Claude profile snapshot exceeded its supported size limit.");
        }

        return ReadSnapshot(file.Content, profile);
    }

    /// <inheritdoc />
    public async Task<IClaudeAccountRepository.SaveResult> SaveProfileAsync(
        ClaudeAccountProfile profile,
        ClaudeAccountProfile.DesktopSnapshot desktopSnapshot,
        string expectedSha256,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(desktopSnapshot);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        if (expectedSha256.Length != 0 && !IsSha256(expectedSha256)) {
            throw new ArgumentException("The Claude profile catalog hash is invalid.", nameof(expectedSha256));
        }
        ValidateProfile(profile);
        ValidateDesktopSnapshot(desktopSnapshot);
        if (profile.ProvenanceSchemaVersion != 1 || !IsBindingConsistent(profile, desktopSnapshot)) {
            throw new ArgumentException("The Claude profile and Desktop snapshot binding do not match.", nameof(desktopSnapshot));
        }

        IClaudeAccountRepository.Snapshot current = await LoadProfilesAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase)) {
            return new IClaudeAccountRepository.SaveResult(false, null, true);
        }

        ClaudeAccountProfile? existingProfile = current.Profiles.FirstOrDefault(
            item => item.ProfileId == profile.ProfileId
        );
        if ((existingProfile is null && profile.ProfileRevision != 1)
            || (existingProfile is not null && profile.ProfileRevision != existingProfile.ProfileRevision + 1)) {
            throw new ArgumentException("Claude profile revision is invalid.", nameof(profile));
        }
        if (existingProfile is not null
            && (existingProfile.ProvenanceSchemaVersion != 1
                || existingProfile.BindingId != profile.BindingId)) {
            throw new InvalidDataException("A profile with unknown or different provenance cannot be overwritten.");
        }

        byte[] snapshotContent = SerializeSnapshot(profile, desktopSnapshot);
        try {
            if (snapshotContent.Length > MaximumSnapshotBytes) {
                throw new InvalidDataException("Claude profile snapshot exceeded its supported size limit.");
            }

            await SaveImmutableSnapshotAsync(
                GetSnapshotRelativePath(profile.ProfileId, profile.ProfileRevision),
                snapshotContent,
                profile,
                desktopSnapshot,
                cancellationToken
            ).ConfigureAwait(false);
        } finally {
            CryptographicOperations.ZeroMemory(snapshotContent);
        }

        ClaudeAccountProfile[] profiles = existingProfile is null
            ? [.. current.Profiles, profile]
            : current.Profiles.Select(item => item.ProfileId == profile.ProfileId ? profile : item).ToArray();
        byte[] catalogContent = JsonSerializer.SerializeToUtf8Bytes(new CatalogDocument(1, profiles), JsonOptions);
        AtomicJsonFileStore.WriteResult write = await fileStore.WriteAsync(
            CatalogRelativePath,
            string.IsNullOrEmpty(expectedSha256) ? null : expectedSha256,
            catalogContent,
            IsValidCatalogContent,
            cancellationToken
        ).ConfigureAwait(false);
        if (!write.Saved) {
            return new IClaudeAccountRepository.SaveResult(false, null, true);
        }

        IClaudeAccountRepository.Snapshot updated = new(profiles, write.Sha256);
        return new IClaudeAccountRepository.SaveResult(true, updated, false);
    }

    /// <inheritdoc />
    public async Task<IClaudeAccountRepository.SaveResult> SaveCatalogMetadataAsync(
        IReadOnlyList<ClaudeAccountProfile> profiles,
        string expectedSha256,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        if (!IsSha256(expectedSha256)) {
            throw new ArgumentException("The Claude profile catalog hash is invalid.", nameof(expectedSha256));
        }

        IClaudeAccountRepository.Snapshot current = await LoadProfilesAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase)) {
            return new IClaudeAccountRepository.SaveResult(false, null, true);
        }

        Dictionary<Guid, ClaudeAccountProfile> existingProfiles = current.Profiles.ToDictionary(profile => profile.ProfileId);
        HashSet<Guid> keptProfileIds = [];
        foreach (ClaudeAccountProfile profile in profiles) {
            ArgumentNullException.ThrowIfNull(profile);
            if (!existingProfiles.TryGetValue(profile.ProfileId, out ClaudeAccountProfile? existing)
                || !keptProfileIds.Add(profile.ProfileId)) {
                throw new ArgumentException("Claude catalog metadata may only reorder or remove existing profiles.", nameof(profiles));
            }

            if (existing with { Alias = profile.Alias } != profile) {
                throw new ArgumentException("Claude catalog metadata may only change profile aliases.", nameof(profiles));
            }
        }

        ClaudeAccountProfile[] updatedProfiles = profiles.ToArray();
        byte[] catalogContent = JsonSerializer.SerializeToUtf8Bytes(new CatalogDocument(1, updatedProfiles), JsonOptions);
        AtomicJsonFileStore.WriteResult write = await fileStore.WriteAsync(
            CatalogRelativePath,
            expectedSha256,
            catalogContent,
            IsValidCatalogContent,
            cancellationToken
        ).ConfigureAwait(false);
        if (!write.Saved) {
            return new IClaudeAccountRepository.SaveResult(false, null, true);
        }

        foreach (Guid removedProfileId in existingProfiles.Keys.Where(profileId => !keptProfileIds.Contains(profileId))) {
            DeleteProfileSnapshots(removedProfileId);
        }

        IClaudeAccountRepository.Snapshot updated = new(updatedProfiles, write.Sha256);
        return new IClaudeAccountRepository.SaveResult(true, updated, false);
    }

    /// <inheritdoc />
    public async Task<ClaudeSwitchTransaction?> LoadSwitchTransactionAsync(CancellationToken cancellationToken) {
        AtomicJsonFileStore.ReadResult file = await fileStore
            .ReadAsync(TransactionRelativePath, cancellationToken)
            .ConfigureAwait(false);
        if (!file.Exists) {
            return null;
        }

        if (file.Content.Length > MaximumTransactionBytes) {
            throw new InvalidDataException("Claude switch journal exceeded its supported size limit.");
        }

        return ReadJournal(file.Content);
    }

    /// <inheritdoc />
    public async Task SaveSwitchTransactionAsync(
        ClaudeSwitchTransaction transaction,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(transaction);
        if (!IsValidTransaction(transaction) || !HasTrustedTransactionBinding(transaction)) {
            throw new ArgumentException("Claude switch journal is invalid.", nameof(transaction));
        }

        AtomicJsonFileStore.ReadResult existing = await fileStore
            .ReadAsync(TransactionRelativePath, cancellationToken)
            .ConfigureAwait(false);
        if (existing.Exists) {
            ClaudeSwitchTransaction current = ReadJournal(existing.Content);
            bool terminal = current.CurrentStage is ClaudeSwitchTransaction.Stage.Committed
                or ClaudeSwitchTransaction.Stage.RolledBack;
            if (!terminal && (!HasTrustedTransactionBinding(current)
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
        }

        byte[] content = SerializeProtectedJournal(transaction);
        if (content.Length > MaximumTransactionBytes) {
            throw new InvalidDataException("Claude switch journal exceeded its supported size limit.");
        }

        AtomicJsonFileStore.WriteResult write = await fileStore.WriteAsync(
            TransactionRelativePath,
            existing.Exists ? existing.Sha256 : null,
            content,
            IsValidJournalContent,
            cancellationToken
        ).ConfigureAwait(false);
        if (!write.Saved) {
            throw new IOException("Claude switch journal changed while it was being updated.");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions() {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private async Task SaveImmutableSnapshotAsync(
        string relativePath,
        byte[] content,
        ClaudeAccountProfile profile,
        ClaudeAccountProfile.DesktopSnapshot snapshot,
        CancellationToken cancellationToken
    ) {
        AtomicJsonFileStore.ReadResult existing = await fileStore.ReadAsync(relativePath, cancellationToken)
            .ConfigureAwait(false);
        if (existing.Exists) {
            ClaudeAccountProfile.DesktopSnapshot existingSnapshot = ReadSnapshot(existing.Content, profile);
            try {
                if (!SnapshotsEqual(existingSnapshot, snapshot)) {
                    throw new IOException("A Claude profile snapshot revision already contains different data.");
                }
            } finally {
                CryptographicOperations.ZeroMemory(existingSnapshot.Cookies);
                if (existingSnapshot.CookieJournal is not null) {
                    CryptographicOperations.ZeroMemory(existingSnapshot.CookieJournal);
                }

                Zero(existingSnapshot.CookieWriteAheadLog);
                Zero(existingSnapshot.CookieSharedMemory);
            }

            return;
        }

        AtomicJsonFileStore.WriteResult write = await fileStore.WriteAsync(
            relativePath,
            null,
            content,
            IsValidSnapshotDocument,
            cancellationToken
        ).ConfigureAwait(false);
        if (!write.Saved) {
            throw new IOException("A Claude profile snapshot revision could not be created.");
        }
    }

    private byte[] SerializeSnapshot(
        ClaudeAccountProfile profile,
        ClaudeAccountProfile.DesktopSnapshot snapshot
    ) {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        byte[]? protectedSnapshot = null;
        try {
            protectedSnapshot = secretProtector.Protect(plaintext);
            ProtectedSnapshotDocument document = new(
                2,
                profile.ProfileId,
                profile.ProfileRevision,
                Convert.ToBase64String(protectedSnapshot)
            );
            return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        } finally {
            Zero(plaintext);
            Zero(protectedSnapshot);
        }
    }

    private ClaudeAccountProfile.DesktopSnapshot ReadSnapshot(byte[] content, ClaudeAccountProfile profile) {
        int schemaVersion = ReadSchemaVersion(content);
        if (schemaVersion == 1) {
            SnapshotDocument legacy = Deserialize<SnapshotDocument>(content, "Claude profile snapshot");
            if (profile.ProvenanceSchemaVersion != 0 || profile.BindingId is not null
                || legacy.ProfileId != profile.ProfileId || legacy.ProfileRevision != profile.ProfileRevision
                || !IsValidSnapshotDocument(legacy) || !IsBindingConsistent(profile, legacy)) {
                throw new InvalidDataException("Legacy Claude profile snapshot content is invalid.");
            }

            return UnprotectSnapshot(legacy);
        }

        ProtectedSnapshotDocument document = Deserialize<ProtectedSnapshotDocument>(content, "Claude profile snapshot");
        if (schemaVersion != 2 || document.ProfileId != profile.ProfileId
            || document.ProfileRevision != profile.ProfileRevision
            || !IsValidBase64(document.ProtectedPayloadBase64, requireNonEmpty: true)) {
            throw new InvalidDataException("Claude profile snapshot envelope is invalid.");
        }

        byte[] protectedPayload = DecodeBase64(document.ProtectedPayloadBase64);
        byte[]? plaintext = null;
        ClaudeAccountProfile.DesktopSnapshot? snapshot = null;
        bool accepted = false;
        try {
            plaintext = secretProtector.Unprotect(protectedPayload);
            snapshot = Deserialize<ClaudeAccountProfile.DesktopSnapshot>(
                plaintext,
                "Claude profile snapshot payload"
            );
            ValidateDesktopSnapshot(snapshot);
            if (!IsBindingConsistent(profile, snapshot)) {
                throw new InvalidDataException("Claude profile snapshot binding is invalid.");
            }

            accepted = true;
            return snapshot;
        } finally {
            Zero(protectedPayload);
            Zero(plaintext);
            if (!accepted && snapshot is not null) {
                Zero(snapshot.Cookies);
                Zero(snapshot.CookieJournal);
                Zero(snapshot.CookieWriteAheadLog);
                Zero(snapshot.CookieSharedMemory);
            }
        }
    }

    private ClaudeAccountProfile.DesktopSnapshot UnprotectSnapshot(SnapshotDocument document) {
        byte[]? tokenCacheBytes = null;
        byte[]? tokenCacheV2Bytes = null;
        byte[]? protectedTokenCache = null;
        byte[]? protectedTokenCacheV2 = null;
        byte[] cookies = DecodeBase64(document.CookiesBase64);
        byte[]? cookieJournal = document.CookieJournalBase64 is null
            ? null
            : DecodeBase64(document.CookieJournalBase64);
        byte[]? cookieWriteAheadLog = document.CookieWriteAheadLogBase64 is null
            ? null
            : DecodeBase64(document.CookieWriteAheadLogBase64);
        byte[]? cookieSharedMemory = document.CookieSharedMemoryBase64 is null
            ? null
            : DecodeBase64(document.CookieSharedMemoryBase64);
        try {
            if (document.ProtectedTokenCacheBase64 is not null) {
                protectedTokenCache = DecodeBase64(document.ProtectedTokenCacheBase64);
                tokenCacheBytes = secretProtector.Unprotect(protectedTokenCache);
            }

            if (document.ProtectedTokenCacheV2Base64 is not null) {
                protectedTokenCacheV2 = DecodeBase64(document.ProtectedTokenCacheV2Base64);
                tokenCacheV2Bytes = secretProtector.Unprotect(protectedTokenCacheV2);
            }

            string? tokenCacheJson = tokenCacheBytes is null ? null : DecodeUtf8(tokenCacheBytes);
            string? tokenCacheV2Json = tokenCacheV2Bytes is null ? null : DecodeUtf8(tokenCacheV2Bytes);
            return new ClaudeAccountProfile.DesktopSnapshot(
                tokenCacheJson,
                tokenCacheV2Json,
                document.LastKnownAccountUuid,
                cookies,
                cookieJournal,
                document.CookieRelativePath,
                document.WindowsUserSid,
                document.InstallationId,
                document.DataRootFingerprint,
                document.LocalStateEncryptedKeySha256,
                document.CapturedAtUtc,
                cookieWriteAheadLog,
                cookieSharedMemory,
                document.CookieSidecarSnapshotVersion
            );
        } catch {
            CryptographicOperations.ZeroMemory(cookies);
            if (cookieJournal is not null) {
                CryptographicOperations.ZeroMemory(cookieJournal);
            }

            Zero(cookieWriteAheadLog);
            Zero(cookieSharedMemory);

            throw;
        } finally {
            Zero(protectedTokenCache);
            Zero(protectedTokenCacheV2);
            Zero(tokenCacheBytes);
            Zero(tokenCacheV2Bytes);
        }
    }

    private static string DecodeUtf8(byte[] content) {
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);
    }

    private static byte[] DecodeBase64(string value) {
        try {
            return Convert.FromBase64String(value);
        } catch (FormatException failure) {
            throw new InvalidDataException("Claude profile snapshot contains invalid binary data.", failure);
        }
    }

    private static bool IsValidCatalogContent(byte[] content) {
        try {
            CatalogDocument document = Deserialize<CatalogDocument>(content, "Claude profile catalog");
            return document.SchemaVersion == 1 && IsValidCatalog(document);
        } catch (JsonException) {
            return false;
        } catch (InvalidDataException) {
            return false;
        } catch (ArgumentException) {
            return false;
        }
    }

    private static bool IsValidCatalog(CatalogDocument document) {
        if (document.Profiles is null) {
            return false;
        }

        HashSet<Guid> profileIds = [];
        HashSet<string> identities = new(StringComparer.OrdinalIgnoreCase);
        foreach (ClaudeAccountProfile? profile in document.Profiles) {
            if (profile is null || !IsValidProfile(profile) || !profileIds.Add(profile.ProfileId)) {
                return false;
            }

            string identity = profile.AccountUuid + "/" + profile.OrganizationUuid + "/" + profile.InstallationId;
            string provenance = profile.BindingId?.ToString("D") ?? "legacy";
            if (!identities.Add(identity + "/" + provenance)) {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidProfile(ClaudeAccountProfile profile) {
        return profile.ProfileId != Guid.Empty
            && IsUuid(profile.AccountUuid)
            && IsUuid(profile.OrganizationUuid)
            && !string.IsNullOrWhiteSpace(profile.MaskedEmail)
            && !string.IsNullOrWhiteSpace(profile.InstallationId)
            && IsSha256(profile.DataRootFingerprint)
            && IsSid(profile.WindowsUserSid)
            && IsSha256(profile.LocalStateEncryptedKeySha256)
            && profile.InstallationId.Length <= 256
            && profile.MaskedEmail.Length <= 254
            && profile.ProfileRevision > 0
            && IsUtc(profile.CreatedAtUtc)
            && IsUtc(profile.LastVerifiedAtUtc)
            && Enum.IsDefined(profile.Status)
            && ((profile.ProvenanceSchemaVersion == 0 && profile.BindingId is null)
                || (profile.ProvenanceSchemaVersion == 1 && profile.BindingId is Guid bindingId && bindingId != Guid.Empty))
            && (profile.Alias is null || profile.Alias.Length <= 80);
    }

    private bool IsValidSnapshotDocument(byte[] content) {
        try {
            if (ReadSchemaVersion(content) == 1) {
                SnapshotDocument legacy = Deserialize<SnapshotDocument>(content, "Claude profile snapshot");
                return IsValidSnapshotDocument(legacy);
            }

            ProtectedSnapshotDocument document = Deserialize<ProtectedSnapshotDocument>(content, "Claude profile snapshot");
            if (document.SchemaVersion != 2 || document.ProfileId == Guid.Empty || document.ProfileRevision <= 0
                || !IsValidBase64(document.ProtectedPayloadBase64, requireNonEmpty: true)) {
                return false;
            }

            byte[] protectedPayload = DecodeBase64(document.ProtectedPayloadBase64);
            byte[]? plaintext = null;
            ClaudeAccountProfile.DesktopSnapshot? snapshot = null;
            try {
                plaintext = secretProtector.Unprotect(protectedPayload);
                snapshot = Deserialize<ClaudeAccountProfile.DesktopSnapshot>(plaintext, "Claude profile snapshot payload");
                ValidateDesktopSnapshot(snapshot);
                return true;
            } finally {
                Zero(protectedPayload);
                Zero(plaintext);
                if (snapshot is not null) {
                    Zero(snapshot.Cookies);
                    Zero(snapshot.CookieJournal);
                    Zero(snapshot.CookieWriteAheadLog);
                    Zero(snapshot.CookieSharedMemory);
                }
            }
        } catch (JsonException) {
            return false;
        } catch (InvalidDataException) {
            return false;
        } catch (ArgumentException) {
            return false;
        } catch (CryptographicException) {
            return false;
        }
    }

    private static bool IsValidSnapshotDocument(SnapshotDocument document) {
        return document.ProfileId != Guid.Empty
            && document.ProfileRevision > 0
            && IsValidProtectedString(document.ProtectedTokenCacheBase64)
            && IsValidProtectedString(document.ProtectedTokenCacheV2Base64)
            && (document.LastKnownAccountUuid is null || IsUuid(document.LastKnownAccountUuid))
            && document.CookiesBase64 is not null
            && IsValidBase64(document.CookiesBase64, requireNonEmpty: true)
            && (document.CookieJournalBase64 is null || IsValidBase64(document.CookieJournalBase64, requireNonEmpty: false))
            && (document.CookieSidecarSnapshotVersion == 0
                ? document.CookieWriteAheadLogBase64 is null && document.CookieSharedMemoryBase64 is null
                : document.CookieSidecarSnapshotVersion == 1
                    && (document.CookieWriteAheadLogBase64 is null
                        || IsValidBase64(document.CookieWriteAheadLogBase64, requireNonEmpty: false))
                    && (document.CookieSharedMemoryBase64 is null
                        || IsValidBase64(document.CookieSharedMemoryBase64, requireNonEmpty: false)))
            && document.CookieRelativePath is not null
            && IsValidCookieRelativePath(document.CookieRelativePath)
            && document.WindowsUserSid is not null
            && IsSid(document.WindowsUserSid)
            && !string.IsNullOrWhiteSpace(document.InstallationId)
            && document.DataRootFingerprint is not null
            && IsSha256(document.DataRootFingerprint)
            && document.LocalStateEncryptedKeySha256 is not null
            && IsSha256(document.LocalStateEncryptedKeySha256)
            && IsUtc(document.CapturedAtUtc);
    }

    private static bool IsValidProtectedString(string? value) {
        return value is null || IsValidBase64(value, requireNonEmpty: true);
    }

    private static bool IsValidBase64(string value, bool requireNonEmpty) {
        if (string.IsNullOrWhiteSpace(value)) {
            return !requireNonEmpty;
        }

        try {
            byte[] bytes = Convert.FromBase64String(value);
            bool valid = !requireNonEmpty || bytes.Length > 0;
            CryptographicOperations.ZeroMemory(bytes);
            return valid;
        } catch (FormatException) {
            return false;
        }
    }

    private static bool IsBindingConsistent(
        ClaudeAccountProfile profile,
        ClaudeAccountProfile.DesktopSnapshot snapshot
    ) {
        return string.Equals(profile.WindowsUserSid, snapshot.WindowsUserSid, StringComparison.Ordinal)
            && string.Equals(profile.InstallationId, snapshot.InstallationId, StringComparison.Ordinal)
            && string.Equals(profile.DataRootFingerprint, snapshot.DataRootFingerprint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.LocalStateEncryptedKeySha256, snapshot.LocalStateEncryptedKeySha256, StringComparison.OrdinalIgnoreCase)
            && profile.ProvenanceSchemaVersion == 1
            && snapshot.ProvenanceSchemaVersion == 1
            && snapshot.RootBinding is not null
            && profile.BindingId == snapshot.RootBinding.Source.BindingId;
    }

    private static bool IsBindingConsistent(ClaudeAccountProfile profile, SnapshotDocument snapshot) {
        return string.Equals(profile.WindowsUserSid, snapshot.WindowsUserSid, StringComparison.Ordinal)
            && string.Equals(profile.InstallationId, snapshot.InstallationId, StringComparison.Ordinal)
            && string.Equals(profile.DataRootFingerprint, snapshot.DataRootFingerprint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.LocalStateEncryptedKeySha256, snapshot.LocalStateEncryptedKeySha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateDesktopSnapshot(ClaudeAccountProfile.DesktopSnapshot snapshot) {
        if (!IsValidSnapshotDocument(new SnapshotDocument(
            1,
            Guid.NewGuid(),
            1,
            null,
            null,
            snapshot.LastKnownAccountUuid,
            Convert.ToBase64String(snapshot.Cookies ?? []),
            snapshot.CookieJournal is null ? null : Convert.ToBase64String(snapshot.CookieJournal),
            snapshot.CookieRelativePath,
            snapshot.WindowsUserSid,
            snapshot.InstallationId,
            snapshot.DataRootFingerprint,
            snapshot.LocalStateEncryptedKeySha256,
            snapshot.CapturedAtUtc,
            snapshot.CookieWriteAheadLog is null ? null : Convert.ToBase64String(snapshot.CookieWriteAheadLog),
            snapshot.CookieSharedMemory is null ? null : Convert.ToBase64String(snapshot.CookieSharedMemory),
            snapshot.CookieSidecarSnapshotVersion
        )) || snapshot.Cookies is null || snapshot.Cookies.Length == 0
            || snapshot.CookieSidecarSnapshotVersion != 1
            || snapshot.ProvenanceSchemaVersion != 1
            || snapshot.RootBinding is not ClaudeDesktopRootBinding binding
            || !IsValidBinding(binding)
            || !string.Equals(snapshot.WindowsUserSid, binding.Source.WindowsUserSid, StringComparison.Ordinal)
            || !string.Equals(snapshot.InstallationId, binding.Source.InstallationId, StringComparison.Ordinal)
            || !string.Equals(
                snapshot.CookieRelativePath.Replace('/', '\\'),
                binding.Source.CookieRelativePath.Replace('/', '\\'),
                StringComparison.OrdinalIgnoreCase
            )
            || !string.Equals(snapshot.LocalStateEncryptedKeySha256, binding.LocalStateEncryptedKeySha256, StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException("Claude Desktop snapshot content or binding is invalid.", nameof(snapshot));
        }
    }

    private static void ValidateProfile(ClaudeAccountProfile profile) {
        if (!IsValidProfile(profile)) {
            throw new ArgumentException("Claude profile content or binding is invalid.", nameof(profile));
        }
    }

    private static void ValidateProfileId(Guid profileId) {
        if (profileId == Guid.Empty) {
            throw new ArgumentException("Claude profile ID must not be empty.", nameof(profileId));
        }
    }

    private static string GetSnapshotRelativePath(Guid profileId, long profileRevision) {
        ValidateProfileId(profileId);
        if (profileRevision < 1) {
            throw new ArgumentOutOfRangeException(nameof(profileRevision));
        }

        return Path.Combine(
            SnapshotRelativeRoot,
            profileId.ToString("N") + "-" + profileRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json"
        );
    }

    private void DeleteProfileSnapshots(Guid profileId) {
        string snapshotDirectory = Path.Combine(dataRootPath, SnapshotRelativeRoot);
        if (!Directory.Exists(snapshotDirectory)) {
            return;
        }

        // catalog 已不再引用這些 revision；刪除失敗時保留受 DPAPI 保護的孤立檔，不影響 catalog 一致性。
        foreach (string path in Directory.EnumerateFiles(snapshotDirectory, profileId.ToString("N") + "-*.json")) {
            try {
                File.Delete(path);
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        }
    }

    private bool IsValidJournalContent(byte[] content) {
        try {
            ClaudeSwitchTransaction transaction = ReadJournal(content);
            return HasTrustedTransactionBinding(transaction);
        } catch (JsonException) {
            return false;
        } catch (InvalidDataException) {
            return false;
        } catch (CryptographicException) {
            return false;
        } catch (ArgumentException) {
            return false;
        }
    }

    private ClaudeSwitchTransaction ReadJournal(byte[] content) {
        int schemaVersion = ReadSchemaVersion(content);
        if (schemaVersion == 1) {
            JournalDocument legacy = DeserializeJournal(content, "Claude switch journal");
            if (legacy.Transaction is null || !IsValidTransaction(legacy.Transaction)) {
                throw new InvalidDataException("Legacy Claude switch journal is invalid.");
            }

            return legacy.Transaction with { ProvenanceSchemaVersion = 0, RootBinding = null };
        }

        ProtectedJournalDocument document = Deserialize<ProtectedJournalDocument>(content, "Claude switch journal");
        if (schemaVersion != 2 || !IsValidBase64(document.ProtectedPayloadBase64, requireNonEmpty: true)) {
            throw new InvalidDataException("Claude switch journal envelope is invalid.");
        }

        byte[] protectedPayload = DecodeBase64(document.ProtectedPayloadBase64);
        byte[]? plaintext = null;
        try {
            plaintext = secretProtector.Unprotect(protectedPayload);
            ClaudeSwitchTransaction transaction = Deserialize<ClaudeSwitchTransaction>(plaintext, "Claude switch journal payload");
            if (!IsValidTransaction(transaction) || !HasTrustedTransactionBinding(transaction)) {
                throw new InvalidDataException("Claude switch journal binding is invalid.");
            }

            return transaction;
        } finally {
            Zero(protectedPayload);
            Zero(plaintext);
        }
    }

    private byte[] SerializeProtectedJournal(ClaudeSwitchTransaction transaction) {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(transaction, JsonOptions);
        byte[]? protectedPayload = null;
        try {
            protectedPayload = secretProtector.Protect(plaintext);
            return JsonSerializer.SerializeToUtf8Bytes(
                new ProtectedJournalDocument(2, Convert.ToBase64String(protectedPayload)),
                JsonOptions
            );
        } finally {
            Zero(plaintext);
            Zero(protectedPayload);
        }
    }

    private static bool IsValidTransaction(ClaudeSwitchTransaction transaction) {
        return transaction.TransactionId != Guid.Empty
            && transaction.SourceProfileId != Guid.Empty
            && transaction.TargetProfileId != Guid.Empty
            && transaction.SourceProfileId != transaction.TargetProfileId
            && transaction.SourceProfileRevision > 0
            && transaction.TargetProfileRevision > 0
            && !string.IsNullOrWhiteSpace(transaction.InstallationId)
            && Enum.IsDefined(transaction.CurrentStage)
            && (transaction.RecoveryFromStage is null || Enum.IsDefined(transaction.RecoveryFromStage.Value))
            && transaction.InstallationId.Length <= 256
            && IsUtc(transaction.StartedAtUtc)
            && IsUtc(transaction.UpdatedAtUtc)
            && transaction.UpdatedAtUtc >= transaction.StartedAtUtc
            && (transaction.FailureCode is null || transaction.FailureCode.Length <= 120);
    }

    private static bool HasTrustedTransactionBinding(ClaudeSwitchTransaction transaction) {
        return transaction.ProvenanceSchemaVersion == 1
            && IsValidBinding(transaction.RootBinding)
            && string.Equals(
                transaction.InstallationId,
                transaction.RootBinding?.Source.InstallationId,
                StringComparison.Ordinal
            );
    }

    private static bool IsValidBinding(ClaudeDesktopRootBinding? binding) {
        if (binding?.Source is not ClaudeDesktopRootBinding.Observation source
            || source.SchemaVersion != 1 || source.BindingId == Guid.Empty
            || !IsSid(source.WindowsUserSid) || string.IsNullOrWhiteSpace(source.InstallationId)
            || !Enum.IsDefined(source.Kind) || string.IsNullOrWhiteSpace(source.DataRoot)
            || !Path.IsPathFullyQualified(source.DataRoot) || !IsValidCookieRelativePath(source.CookieRelativePath)
            || string.IsNullOrWhiteSpace(source.RootFileId) || source.LayoutVersion < 1
            || source.Processes is null || source.Processes.Count == 0
            || source.Processes.Any(process => process is null || process.ProcessId <= 0 || !IsUtc(process.CreatedAtUtc))
            || !IsSha256(binding.LocalStateEncryptedKeySha256)) {
            return false;
        }

        return source.Kind switch {
            IClaudeDesktopInstallationLocator.InstallKind.Msix =>
                !string.IsNullOrWhiteSpace(source.AppUserModelId)
                && !string.IsNullOrWhiteSpace(source.PackageFamilyName)
                && !string.IsNullOrWhiteSpace(source.PackageFullName),
            IClaudeDesktopInstallationLocator.InstallKind.Direct =>
                !string.IsNullOrWhiteSpace(source.ExecutablePath)
                && Path.IsPathFullyQualified(source.ExecutablePath)
                && IsSha256(source.ExecutableSha256),
            _ => false
        };
    }

    private static bool BindingsEqual(ClaudeDesktopRootBinding? left, ClaudeDesktopRootBinding? right) {
        if (left is null || right is null) {
            return left is null && right is null;
        }

        ClaudeDesktopRootBinding.Observation a = left.Source;
        ClaudeDesktopRootBinding.Observation b = right.Source;
        return a.SchemaVersion == b.SchemaVersion
            && a.BindingId == b.BindingId
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
            && a.LayoutVersion == b.LayoutVersion
            && a.Processes.SequenceEqual(b.Processes)
            && string.Equals(left.LocalStateEncryptedKeySha256, right.LocalStateEncryptedKeySha256, StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadSchemaVersion(byte[] content) {
        using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !TryGetPropertyIgnoreCase(document.RootElement, "schemaVersion", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int value)) {
            throw new InvalidDataException("Claude storage schema is missing or invalid.");
        }

        return value;
    }

    private static JournalDocument DeserializeJournal(byte[] content, string dataName) {
        try {
            using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !TryGetPropertyIgnoreCase(document.RootElement, "transaction", out JsonElement transactionElement)
                || transactionElement.ValueKind != JsonValueKind.Object
                || !TryGetPropertyIgnoreCase(transactionElement, "currentStage", out JsonElement stageElement)
                || !IsKnownStage(stageElement)
                || (TryGetPropertyIgnoreCase(transactionElement, "recoveryFromStage", out JsonElement recoveryStageElement)
                    && recoveryStageElement.ValueKind != JsonValueKind.Null
                    && !IsKnownStage(recoveryStageElement))) {
                byte[] recoveryContent = NormalizeJournalToRecoveryRequired(content);
                try {
                    return Deserialize<JournalDocument>(recoveryContent, dataName);
                } finally {
                    CryptographicOperations.ZeroMemory(recoveryContent);
                }
            }

            return Deserialize<JournalDocument>(content, dataName);
        } catch (JsonException failure) {
            throw new InvalidDataException($"{dataName} is malformed.", failure);
        }
    }

    private static byte[] NormalizeJournalToRecoveryRequired(byte[] content) {
        JsonNode? root = JsonNode.Parse(content, documentOptions: new JsonDocumentOptions { MaxDepth = 32 });
        if (root is not JsonObject rootObject
            || FindPropertyName(rootObject, "transaction") is not string transactionProperty
            || rootObject[transactionProperty] is not JsonObject transactionObject) {
            throw new InvalidDataException("Claude switch journal transaction is malformed.");
        }

        string stageProperty = FindPropertyName(transactionObject, "currentStage") ?? "currentStage";
        string failureProperty = FindPropertyName(transactionObject, "failureCode") ?? "failureCode";
        transactionObject[stageProperty] = "recoveryRequired";
        string recoveryStageProperty = FindPropertyName(transactionObject, "recoveryFromStage") ?? "recoveryFromStage";
        transactionObject[recoveryStageProperty] = "targetVerified";
        transactionObject[failureProperty] = "unknown-journal-stage";
        return JsonSerializer.SerializeToUtf8Bytes(rootObject, JsonOptions);
    }

    private static bool IsKnownStage(JsonElement stageElement) {
        return stageElement.ValueKind == JsonValueKind.String
            && Enum.TryParse(stageElement.GetString(), ignoreCase: true, out ClaudeSwitchTransaction.Stage stage)
            && Enum.IsDefined(stage);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value) {
        foreach (JsonProperty property in element.EnumerateObject()) {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? FindPropertyName(JsonObject value, string name) {
        foreach (KeyValuePair<string, JsonNode?> property in value) {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)) {
                return property.Key;
            }
        }

        return null;
    }

    private static bool SnapshotsEqual(
        ClaudeAccountProfile.DesktopSnapshot left,
        ClaudeAccountProfile.DesktopSnapshot right
    ) {
        return string.Equals(left.TokenCacheJson, right.TokenCacheJson, StringComparison.Ordinal)
            && string.Equals(left.TokenCacheV2Json, right.TokenCacheV2Json, StringComparison.Ordinal)
            && string.Equals(left.LastKnownAccountUuid, right.LastKnownAccountUuid, StringComparison.Ordinal)
            && left.Cookies.AsSpan().SequenceEqual(right.Cookies)
            && ((left.CookieJournal is null && right.CookieJournal is null)
                || (left.CookieJournal is not null
                    && right.CookieJournal is not null
                    && left.CookieJournal.AsSpan().SequenceEqual(right.CookieJournal)))
            && ByteArraysEqual(left.CookieWriteAheadLog, right.CookieWriteAheadLog)
            && ByteArraysEqual(left.CookieSharedMemory, right.CookieSharedMemory)
            && left.CookieSidecarSnapshotVersion == right.CookieSidecarSnapshotVersion
            && string.Equals(left.CookieRelativePath, right.CookieRelativePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.WindowsUserSid, right.WindowsUserSid, StringComparison.Ordinal)
            && string.Equals(left.InstallationId, right.InstallationId, StringComparison.Ordinal)
            && string.Equals(left.DataRootFingerprint, right.DataRootFingerprint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.LocalStateEncryptedKeySha256, right.LocalStateEncryptedKeySha256, StringComparison.OrdinalIgnoreCase)
            && left.ProvenanceSchemaVersion == right.ProvenanceSchemaVersion
            && BindingsEqual(left.RootBinding, right.RootBinding)
            && left.CapturedAtUtc == right.CapturedAtUtc;
    }

    private static T Deserialize<T>(byte[] content, string dataName) where T : class {
        try {
            return JsonSerializer.Deserialize<T>(content, JsonOptions)
                ?? throw new InvalidDataException($"{dataName} is empty.");
        } catch (JsonException failure) {
            throw new InvalidDataException($"{dataName} is malformed.", failure);
        }
    }

    private static void Zero(byte[]? bytes) {
        if (bytes is not null) {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool ByteArraysEqual(byte[]? left, byte[]? right) {
        return (left is null && right is null)
            || (left is not null && right is not null && left.AsSpan().SequenceEqual(right));
    }

    private static bool IsUuid(string value) => Guid.TryParseExact(value, "D", out Guid parsed) && parsed != Guid.Empty;

    private static bool IsSha256(string? value) {
        return value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static bool IsSid(string value) {
        try {
            return value is not null
                && string.Equals(new SecurityIdentifier(value).Value, value, StringComparison.Ordinal);
        } catch (ArgumentException) {
            return false;
        }
    }

    private static bool IsValidCookieRelativePath(string value) {
        if (value is null) {
            return false;
        }

        string normalized = value.Replace('/', '\\');
        return string.Equals(normalized, "Cookies", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, Path.Combine("Network", "Cookies"), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private sealed record CatalogDocument(int SchemaVersion, IReadOnlyList<ClaudeAccountProfile>? Profiles);

    private sealed record ProtectedSnapshotDocument(
        int SchemaVersion,
        Guid ProfileId,
        long ProfileRevision,
        string ProtectedPayloadBase64
    );

    private sealed record ProtectedJournalDocument(int SchemaVersion, string ProtectedPayloadBase64);

    private sealed record SnapshotDocument(
        int SchemaVersion,
        Guid ProfileId,
        long ProfileRevision,
        string? ProtectedTokenCacheBase64,
        string? ProtectedTokenCacheV2Base64,
        string? LastKnownAccountUuid,
        string CookiesBase64,
        string? CookieJournalBase64,
        string CookieRelativePath,
        string WindowsUserSid,
        string InstallationId,
        string DataRootFingerprint,
        string LocalStateEncryptedKeySha256,
        DateTimeOffset CapturedAtUtc,
        string? CookieWriteAheadLogBase64 = null,
        string? CookieSharedMemoryBase64 = null,
        int CookieSidecarSnapshotVersion = 0
    );

    private sealed record JournalDocument(int SchemaVersion, ClaudeSwitchTransaction? Transaction);
}
