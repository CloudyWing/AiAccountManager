using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;

namespace CloudyWing.AiAccountManager.Windows.Claude;

/// <summary>
/// 讀取及還原 Claude Desktop auth 欄位與 Cookie 檔案。
/// </summary>
public sealed class WindowsClaudeDesktopProfileStore : IClaudeDesktopProfileStore {
    private const string ConfigFileName = "config.json";
    private const string LocalStateFileName = "Local State";
    private const string CookiesRelativePath = "Cookies";
    private const string NetworkCookiesRelativePath = "Network/Cookies";
    private const string CookieJournalFileName = "Cookies-journal";
    private const string CookieWriteAheadLogFileName = "Cookies-wal";
    private const string CookieSharedMemoryFileName = "Cookies-shm";
    private const string TokenCacheProperty = "oauth:tokenCache";
    private const string TokenCacheV2Property = "oauth:tokenCacheV2";
    private const string LastKnownAccountProperty = "lastKnownAccountUuid";
    private const int MaximumConfigBytes = 4 * 1024 * 1024;
    private const int MaximumLocalStateBytes = 8 * 1024 * 1024;
    private const int MaximumTokenCacheBytes = 2 * 1024 * 1024;
    private const int MaximumCookieBytes = 128 * 1024 * 1024;
    private const int MaximumTokenCandidates = 256;
    private readonly Func<string> getCurrentSid;
    private readonly Func<DateTimeOffset> getUtcNow;
    private readonly IClaudeDesktopProcessController processController;
    private readonly ClaudeDesktopSnapshotFileSystem snapshotFileSystem;
    private readonly Action<string, string>? transactionObserver;

    /// <summary>
    /// 建立使用目前 Windows 使用者、UTC 系統時鐘與 Desktop 程序控制器的 profile store。
    /// </summary>
    /// <param name="processController">提供新鮮 Claude Desktop 程序狀態的控制器。</param>
    public WindowsClaudeDesktopProfileStore(IClaudeDesktopProcessController processController)
        : this(processController, GetCurrentSid, static () => DateTimeOffset.UtcNow) {
    }

    internal WindowsClaudeDesktopProfileStore(
        IClaudeDesktopProcessController processController,
        Func<string> getCurrentSid,
        Func<DateTimeOffset> getUtcNow,
        Action<ClaudeDesktopSnapshotFileSystem.Operation, string>? snapshotObserver = null,
        Action<string, string>? transactionObserver = null
    ) {
        this.processController = processController ?? throw new ArgumentNullException(nameof(processController));
        this.getCurrentSid = getCurrentSid ?? throw new ArgumentNullException(nameof(getCurrentSid));
        this.getUtcNow = getUtcNow ?? throw new ArgumentNullException(nameof(getUtcNow));
        this.transactionObserver = transactionObserver;
        snapshotFileSystem = new ClaudeDesktopSnapshotFileSystem(snapshotObserver);
    }

    /// <inheritdoc />
    public async Task<IClaudeDesktopProfileStore.ReadResult> ReadCurrentAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        string root = ValidateInstallation(installation);
        string configPath = ClaudeWindowsPathSafety.ResolveContainedPath(root, ConfigFileName);
        byte[] configBytes = await snapshotFileSystem.ReadSharedBoundedAsync(
            root,
            configPath,
            MaximumConfigBytes,
            cancellationToken
        ).ConfigureAwait(false);
        byte[]? localStateBytes = null;
        try {
            AuthFields authFields = ReadAuthFields(configBytes);
            if (RequiresLocalStateKey(authFields)) {
                localStateBytes = await snapshotFileSystem.ReadSharedBoundedAsync(
                    root,
                    ClaudeWindowsPathSafety.ResolveContainedPath(root, LocalStateFileName),
                    MaximumLocalStateBytes,
                    cancellationToken
                ).ConfigureAwait(false);
            }

            return new IClaudeDesktopProfileStore.ReadResult(
                authFields.LastKnownAccountUuid,
                GetTokenCandidates(authFields, localStateBytes)
            );
        } finally {
            CryptographicOperations.ZeroMemory(configBytes);
            if (localStateBytes is not null) {
                CryptographicOperations.ZeroMemory(localStateBytes);
            }
        }
    }

    /// <inheritdoc />
    public async Task<IClaudeDesktopProfileStore.StableSnapshotLease> CaptureStableSnapshotAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        string root = ValidateInstallation(installation);
        string configPath = ClaudeWindowsPathSafety.ResolveContainedPath(root, ConfigFileName);
        string localStatePath = ClaudeWindowsPathSafety.ResolveContainedPath(root, LocalStateFileName);
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, configPath);
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, localStatePath);
        CookieFiles cookieFiles = LocateCookieFiles(root, requireDatabase: true);
        string[] expectedPaths = DiscoverSnapshotFileSet(root, cookieFiles);
        Dictionary<string, ClaudeDesktopSnapshotFileSystem.FileMetadata> preflight = new(StringComparer.OrdinalIgnoreCase);
        List<ClaudeDesktopSnapshotFileSystem.SnapshotFile> lockedFiles = [];
        Dictionary<string, byte[]> contents = new(StringComparer.OrdinalIgnoreCase);
        ClaudeAccountProfile.DesktopSnapshot? snapshot = null;
        bool leaseCreated = false;
        try {
            foreach (string path in expectedPaths) {
                cancellationToken.ThrowIfCancellationRequested();
                snapshotFileSystem.Observe(ClaudeDesktopSnapshotFileSystem.Operation.BeforePreflight, path);
                preflight.Add(path, snapshotFileSystem.CaptureMetadata(root, path));
                snapshotFileSystem.Observe(ClaudeDesktopSnapshotFileSystem.Operation.AfterPreflight, path);
            }

            foreach (string path in expectedPaths) {
                cancellationToken.ThrowIfCancellationRequested();
                snapshotFileSystem.Observe(ClaudeDesktopSnapshotFileSystem.Operation.BeforeLock, path);
                ClaudeDesktopSnapshotFileSystem.SnapshotFile file = snapshotFileSystem.OpenLockedFile(
                    root,
                    path,
                    preflight[path]
                );
                lockedFiles.Add(file);
                snapshotFileSystem.Observe(ClaudeDesktopSnapshotFileSystem.Operation.HandleLocked, path);
            }

            snapshotFileSystem.Observe(ClaudeDesktopSnapshotFileSystem.Operation.AllHandlesLocked, root);
            EnsureStableFileSet(root, cookieFiles, expectedPaths, lockedFiles);

            foreach (ClaudeDesktopSnapshotFileSystem.SnapshotFile file in lockedFiles) {
                cancellationToken.ThrowIfCancellationRequested();
                snapshotFileSystem.Observe(ClaudeDesktopSnapshotFileSystem.Operation.BeforeRead, file.Path);
                byte[] bytes = await file.ReadBoundedAsync(GetMaximumBytes(root, file.Path), cancellationToken)
                    .ConfigureAwait(false);
                contents.Add(file.Path, bytes);
                snapshotFileSystem.Observe(ClaudeDesktopSnapshotFileSystem.Operation.AfterRead, file.Path);
            }

            EnsureStableFileSet(root, cookieFiles, expectedPaths, lockedFiles);
            AuthFields authFields = ReadAuthFields(contents[configPath]);
            string sid = ValidateSid(getCurrentSid());
            string keyDigest = ReadLocalStateKeyDigest(contents[localStatePath]);
            ClaudeDesktopRootBinding.Observation? source = installation.Binding?.Source ?? installation.Observation;
            if (source is null || source.SchemaVersion != 1 || source.BindingId == Guid.Empty
                || !string.Equals(source.WindowsUserSid, sid, StringComparison.Ordinal)
                || !string.Equals(source.CookieRelativePath.Replace('\\', '/'), cookieFiles.RelativePath, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException("Claude snapshot requires verified source observation.");
            }

            ClaudeDesktopRootBinding binding = new(source, keyDigest);
            snapshot = new ClaudeAccountProfile.DesktopSnapshot(
                authFields.TokenCacheJson,
                authFields.TokenCacheV2Json,
                authFields.LastKnownAccountUuid,
                TakeContent(contents, cookieFiles.DatabasePath),
                TakeOptionalContent(contents, cookieFiles.JournalPath),
                cookieFiles.RelativePath,
                sid,
                installation.InstallationId,
                installation.DataRootFingerprint,
                keyDigest,
                EnsureUtc(getUtcNow()),
                TakeOptionalContent(contents, cookieFiles.WriteAheadLogPath),
                TakeOptionalContent(contents, cookieFiles.SharedMemoryPath),
                CookieSidecarSnapshotVersion: 1,
                ProvenanceSchemaVersion: 1,
                RootBinding: binding
            );
            IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> tokenCandidates = GetTokenCandidates(
                authFields,
                contents[localStatePath]
            );
            IClaudeDesktopProfileStore.StableSnapshotLease lease = new WindowsStableSnapshotLease(
                this,
                root,
                cookieFiles,
                expectedPaths,
                lockedFiles,
                snapshot,
                tokenCandidates
            );
            leaseCreated = true;
            return lease;
        } finally {
            foreach (byte[] bytes in contents.Values) {
                CryptographicOperations.ZeroMemory(bytes);
            }

            if (!leaseCreated) {
                if (snapshot is not null) {
                    ZeroSnapshot(snapshot);
                }

                foreach (ClaudeDesktopSnapshotFileSystem.SnapshotFile file in lockedFiles) {
                    file.Dispose();
                }
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> GetTokenCandidates(
        ClaudeAccountProfile.DesktopSnapshot snapshot
    ) {
        ArgumentNullException.ThrowIfNull(snapshot);
        AuthFields authFields = new(snapshot.TokenCacheJson, snapshot.TokenCacheV2Json, snapshot.LastKnownAccountUuid);
        if (!RequiresLocalStateKey(authFields)) {
            return GetTokenCandidates(authFields, null);
        }

        byte[] localStateBytes = ReadSnapshotLocalState(snapshot);
        try {
            return GetTokenCandidates(authFields, localStateBytes);
        } finally {
            CryptographicOperations.ZeroMemory(localStateBytes);
        }
    }

    private static IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> GetTokenCandidates(
        AuthFields authFields,
        byte[]? localStateBytes
    ) {
        List<IClaudeDesktopProfileStore.TokenCandidate> candidates = [];
        HashSet<string> tokens = new(StringComparer.Ordinal);
        byte[]? key = null;
        try {
            if (RequiresLocalStateKey(authFields)) {
                key = ClaudeSafeStorage.UnprotectKey(
                    localStateBytes ?? throw new InvalidDataException("Claude Local State is required to read the token cache.")
                );
            }

            ParseTokenCache(authFields.TokenCacheJson, TokenCacheProperty, candidates, tokens, key);
            ParseTokenCache(authFields.TokenCacheV2Json, TokenCacheV2Property, candidates, tokens, key);
            return candidates;
        } finally {
            if (key is not null) {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private static bool RequiresLocalStateKey(AuthFields authFields) {
        return IsEncryptedTokenCache(authFields.TokenCacheJson) || IsEncryptedTokenCache(authFields.TokenCacheV2Json);
    }

    private static bool IsEncryptedTokenCache(string? json) {
        return json is not null && ClaudeSafeStorage.TryReadEncryptedValue(json, out _);
    }

    // 已保存的 snapshot 只保存加密字串，解密時重新讀取同一資料根的 Local State，並要求 key 摘要與 snapshot 相同。
    private static byte[] ReadSnapshotLocalState(ClaudeAccountProfile.DesktopSnapshot snapshot) {
        string dataRoot = snapshot.RootBinding?.Source.DataRoot
            ?? throw new InvalidDataException("Claude snapshot has no trusted data root.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        string path = ClaudeWindowsPathSafety.ResolveContainedPath(root, LocalStateFileName);
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, path);
        byte[] bytes;
        using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            if (stream.Length > MaximumLocalStateBytes) {
                throw new InvalidDataException("Claude Local State exceeds its supported size limit.");
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        if (!string.Equals(ReadLocalStateKeyDigest(bytes), snapshot.LocalStateEncryptedKeySha256, StringComparison.OrdinalIgnoreCase)) {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("Claude Local State key no longer matches the saved snapshot.");
        }

        return bytes;
    }

    /// <inheritdoc />
    public Task RestoreAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        ClaudeAccountProfile.DesktopSnapshot snapshot,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(snapshot);
        return RestoreStandaloneAsync(installation, snapshot, cancellationToken);
    }

    private async Task RestoreStandaloneAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        ClaudeAccountProfile.DesktopSnapshot snapshot,
        CancellationToken cancellationToken
    ) {
        Guid transactionId = Guid.NewGuid();
        await PrepareRestoreAsync(installation, snapshot, transactionId, cancellationToken).ConfigureAwait(false);
        try {
            await ApplyRestoreAsync(installation, transactionId, cancellationToken).ConfigureAwait(false);
        } catch (Exception applyFailure) {
            try {
                await RestoreTransactionAsync(installation, transactionId, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception restoreFailure) {
                throw new AggregateException("Claude profile transaction requires recovery.", applyFailure, restoreFailure);
            }

            throw;
        }

        await CommitTransactionAsync(installation, transactionId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PrepareRestoreAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        ClaudeAccountProfile.DesktopSnapshot targetSnapshot,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        string root = ValidateInstallation(installation);
        ValidateSnapshot(targetSnapshot);
        VerifySnapshotBinding(root, installation, targetSnapshot);
        await EnsureDesktopNotRunningAsync(installation, cancellationToken).ConfigureAwait(false);
        string digest = await ReadLocalStateKeyDigestAsync(installation, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(digest, targetSnapshot.LocalStateEncryptedKeySha256, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException("Claude Local State key changed before transaction preparation.");
        }

        CookieFiles files = LocateSnapshotCookieFiles(root, targetSnapshot.CookieRelativePath);
        string config = ClaudeWindowsPathSafety.ResolveContainedPath(root, ConfigFileName);
        byte[] original = await snapshotFileSystem.ReadSharedBoundedAsync(root, config, MaximumConfigBytes, cancellationToken)
            .ConfigureAwait(false);
        byte[]? replacement = null;
        try {
            replacement = ReplaceAuthFields(original, targetSnapshot);
            CreateTransaction(installation, transactionId).Prepare([
                new(config, replacement),
                new(files.DatabasePath, targetSnapshot.Cookies),
                new(files.JournalPath, targetSnapshot.CookieJournal),
                new(files.WriteAheadLogPath, targetSnapshot.CookieWriteAheadLog),
                new(files.SharedMemoryPath, targetSnapshot.CookieSharedMemory)
            ], cancellationToken);
        } finally {
            Zero(original);
            Zero(replacement);
        }
    }

    /// <inheritdoc />
    public Task ApplyRestoreAsync(IClaudeDesktopInstallationLocator.Installation installation, Guid transactionId,
        CancellationToken cancellationToken) {
        return CreateTransaction(installation, transactionId).ApplyAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task RestoreTransactionAsync(IClaudeDesktopInstallationLocator.Installation installation, Guid transactionId,
        CancellationToken cancellationToken) {
        return CreateTransaction(installation, transactionId).RestoreAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task CommitTransactionAsync(IClaudeDesktopInstallationLocator.Installation installation, Guid transactionId,
        CancellationToken cancellationToken) {
        try {
            CreateTransaction(installation, transactionId).Cleanup(cancellationToken, verifyLiveState: false);
            return Task.CompletedTask;
        } catch (Exception failure) {
            return Task.FromException(failure);
        }
    }

    private WindowsClaudeHistoryCleanupFileSystem.DurableFileTransaction CreateTransaction(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid transactionId
    ) {
        string root = ValidateInstallation(installation);
        ClaudeDesktopRootBinding binding = installation.Binding
            ?? throw new InvalidDataException("Claude auth transaction requires a trusted root binding.");
        string parent = ClaudeWindowsPathSafety.ResolveContainedPath(root, ".ai-account-manager-auth-transactions");
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, parent);
        Directory.CreateDirectory(parent);
        return new(root, Path.Combine(parent, transactionId.ToString("N")), transactionId, binding,
            token => EnsureDesktopNotRunningAsync(installation, token),
            (operation, path) => {
                // manifest 發布只更新交易紀錄，不屬於 live 檔案變動。
                bool liveRename = !operation.EndsWith("manifest-publish", StringComparison.Ordinal);
                bool before = liveRename && operation.StartsWith("before-", StringComparison.Ordinal);
                bool after = liveRename && operation.StartsWith("after-", StringComparison.Ordinal);
                if (before || after) {
                    ObserveMutation(path, operation.Contains("restore-", StringComparison.Ordinal), before);
                }

                transactionObserver?.Invoke(operation, path);
            });
    }

    internal static async Task<string> ReadLocalStateKeyDigestAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    ) {
        string root = ValidateInstallation(installation);
        string path = ClaudeWindowsPathSafety.ResolveContainedPath(root, LocalStateFileName);
        ClaudeDesktopSnapshotFileSystem fileSystem = new();
        ClaudeDesktopSnapshotFileSystem.FileMetadata metadata = fileSystem.CaptureMetadata(root, path);
        using ClaudeDesktopSnapshotFileSystem.SnapshotFile file = fileSystem.OpenLockedFile(root, path, metadata);
        byte[] bytes = await file.ReadBoundedAsync(MaximumLocalStateBytes, cancellationToken).ConfigureAwait(false);
        try {
            return ReadLocalStateKeyDigest(bytes);
        } finally {
            Zero(bytes);
        }
    }


    private void VerifySnapshotBinding(
        string root,
        IClaudeDesktopInstallationLocator.Installation installation,
        ClaudeAccountProfile.DesktopSnapshot snapshot
    ) {
        string sid = ValidateSid(getCurrentSid());
        if (snapshot.ProvenanceSchemaVersion != 1 || snapshot.RootBinding is null || installation.Binding is null
            || !string.Equals(JsonSerializer.Serialize(snapshot.RootBinding), JsonSerializer.Serialize(installation.Binding), StringComparison.Ordinal)
            || !string.Equals(sid, snapshot.WindowsUserSid, StringComparison.Ordinal)
            || !string.Equals(installation.InstallationId, snapshot.InstallationId, StringComparison.Ordinal)
            || !string.Equals(
                snapshot.LocalStateEncryptedKeySha256,
                snapshot.RootBinding.LocalStateEncryptedKeySha256,
                StringComparison.OrdinalIgnoreCase
            )
            || !string.Equals(
                ClaudeWindowsPathSafety.ComputePathFingerprint(root),
                snapshot.DataRootFingerprint,
                StringComparison.OrdinalIgnoreCase
            )
            || !string.Equals(installation.DataRootFingerprint, snapshot.DataRootFingerprint, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidOperationException("Claude Desktop profile binding does not match the current Windows user or installation.");
        }
    }

    private static string ValidateInstallation(IClaudeDesktopInstallationLocator.Installation installation) {
        if (string.IsNullOrWhiteSpace(installation.InstallationId)
            || string.IsNullOrWhiteSpace(installation.DataRoot)
            || string.IsNullOrWhiteSpace(installation.DataRootFingerprint)) {
            throw new ArgumentException("Claude Desktop installation metadata is incomplete.", nameof(installation));
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.DataRoot));
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, root);
        if (!string.Equals(
            ClaudeWindowsPathSafety.ComputePathFingerprint(root),
            installation.DataRootFingerprint,
            StringComparison.OrdinalIgnoreCase
        )) {
            throw new InvalidOperationException("Claude Desktop data root does not match its verified fingerprint.");
        }

        return root;
    }

    private static AuthFields ReadAuthFields(byte[] configBytes) {
        try {
            using JsonDocument document = JsonDocument.Parse(configBytes, new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) {
                throw new InvalidDataException("Claude Desktop config.json must contain a JSON object.");
            }

            HashSet<string> propertyNames = new(StringComparer.Ordinal);
            string? tokenCache = null;
            string? tokenCacheV2 = null;
            string? lastKnownAccountUuid = null;
            foreach (JsonProperty property in root.EnumerateObject()) {
                if (!propertyNames.Add(property.Name)) {
                    throw new InvalidDataException("Claude Desktop config.json contains duplicate properties.");
                }

                if (string.Equals(property.Name, TokenCacheProperty, StringComparison.Ordinal)) {
                    tokenCache = GetRawJson(property.Value, TokenCacheProperty);
                } else if (string.Equals(property.Name, TokenCacheV2Property, StringComparison.Ordinal)) {
                    tokenCacheV2 = GetRawJson(property.Value, TokenCacheV2Property);
                } else if (string.Equals(property.Name, LastKnownAccountProperty, StringComparison.Ordinal)) {
                    lastKnownAccountUuid = GetOptionalUuid(property.Value, LastKnownAccountProperty);
                }
            }

            ValidateTokenCacheJson(tokenCache);
            ValidateTokenCacheJson(tokenCacheV2);
            return new AuthFields(tokenCache, tokenCacheV2, lastKnownAccountUuid);
        } catch (JsonException failure) {
            throw new InvalidDataException("Claude Desktop config.json is malformed.", failure);
        }
    }

    private static string? GetRawJson(JsonElement element, string fieldName) {
        string rawJson = element.GetRawText();
        if (Encoding.UTF8.GetByteCount(rawJson) > MaximumTokenCacheBytes) {
            throw new InvalidDataException($"Claude Desktop {fieldName} exceeds its supported size limit.");
        }

        return rawJson;
    }

    private static string? GetOptionalUuid(JsonElement element, string fieldName) {
        if (element.ValueKind == JsonValueKind.Null) {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException($"Claude Desktop {fieldName} has an unsupported format.");
        }

        string? value = element.GetString();
        if (value is null) {
            return null;
        }

        if (!Guid.TryParseExact(value, "D", out Guid parsed) || parsed == Guid.Empty) {
            throw new InvalidDataException($"Claude Desktop {fieldName} has an invalid UUID.");
        }

        return parsed.ToString("D");
    }

    private static CookieFiles LocateCookieFiles(string root, bool requireDatabase) {
        string networkPath = ClaudeWindowsPathSafety.ResolveContainedPath(root, NetworkCookiesRelativePath);
        string legacyPath = ClaudeWindowsPathSafety.ResolveContainedPath(root, CookiesRelativePath);
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, networkPath);
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, legacyPath);

        bool hasNetworkDatabase = File.Exists(networkPath);
        bool hasLegacyDatabase = File.Exists(legacyPath);
        if (hasNetworkDatabase && hasLegacyDatabase) {
            throw new InvalidDataException("Claude Desktop has more than one supported Cookie database path.");
        }

        string? databasePath = hasNetworkDatabase
            ? networkPath
            : hasLegacyDatabase
                ? legacyPath
                : null;
        if (databasePath is null && requireDatabase) {
            throw new FileNotFoundException("Claude Desktop Cookie database is missing.");
        }

        string relativePath = databasePath is null || string.Equals(databasePath, legacyPath, StringComparison.OrdinalIgnoreCase)
            ? CookiesRelativePath
            : NetworkCookiesRelativePath;
        string selectedDatabasePath = databasePath ?? legacyPath;
        CookieFiles cookieFiles = CreateCookieFiles(relativePath, selectedDatabasePath);
        if (databasePath is not null) {
            ValidateCookieSidecarDirectory(root, cookieFiles);
        }

        return cookieFiles;
    }

    private static CookieFiles LocateSnapshotCookieFiles(string root, string relativePath) {
        if (!IsSupportedCookieRelativePath(relativePath)) {
            throw new InvalidDataException("Claude profile snapshot contains an unsupported Cookie path.");
        }

        string databasePath = ClaudeWindowsPathSafety.ResolveContainedPath(root, relativePath);
        string parent = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidDataException("Claude Cookie database path has no parent directory.");
        if (!Directory.Exists(parent)) {
            throw new DirectoryNotFoundException("The Claude Cookie directory required by the snapshot is missing.");
        }

        CookieFiles cookieFiles = CreateCookieFiles(relativePath.Replace('\\', '/'), databasePath);
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, cookieFiles.DatabasePath);
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, cookieFiles.JournalPath);
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, cookieFiles.WriteAheadLogPath);
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, cookieFiles.SharedMemoryPath);
        ValidateCookieSidecarDirectory(root, cookieFiles);
        return cookieFiles;
    }

    private static CookieFiles CreateCookieFiles(string relativePath, string cookieDatabasePath) {
        string directory = Path.GetDirectoryName(cookieDatabasePath)
            ?? throw new InvalidDataException("Claude Cookie database path has no parent directory.");
        return new CookieFiles(
            relativePath,
            cookieDatabasePath,
            Path.Combine(directory, CookieJournalFileName),
            Path.Combine(directory, CookieWriteAheadLogFileName),
            Path.Combine(directory, CookieSharedMemoryFileName)
        );
    }

    private static string[] DiscoverSnapshotFileSet(string root, CookieFiles cookieFiles) {
        CookieFiles currentCookieFiles = LocateCookieFiles(root, requireDatabase: true);
        if (!string.Equals(currentCookieFiles.RelativePath, cookieFiles.RelativePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(currentCookieFiles.DatabasePath, cookieFiles.DatabasePath, StringComparison.OrdinalIgnoreCase)) {
            throw new IOException("Claude Desktop Cookie database location changed during snapshot capture.");
        }

        string configPath = ClaudeWindowsPathSafety.ResolveContainedPath(root, ConfigFileName);
        string localStatePath = ClaudeWindowsPathSafety.ResolveContainedPath(root, LocalStateFileName);
        List<string> paths = [configPath, localStatePath, cookieFiles.DatabasePath];
        AddExistingSidecar(paths, root, cookieFiles.JournalPath);
        AddExistingSidecar(paths, root, cookieFiles.WriteAheadLogPath);
        AddExistingSidecar(paths, root, cookieFiles.SharedMemoryPath);
        return paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddExistingSidecar(List<string> paths, string root, string sidecarPath) {
        if (Directory.Exists(sidecarPath)) {
            throw new InvalidDataException("Claude Desktop Cookie sidecar path is a directory.");
        }

        if (!File.Exists(sidecarPath)) {
            return;
        }

        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, sidecarPath);
        paths.Add(sidecarPath);
    }

    private static void ValidateCookieSidecarDirectory(string root, CookieFiles cookieFiles) {
        string directory = Path.GetDirectoryName(cookieFiles.DatabasePath)
            ?? throw new InvalidDataException("Claude Cookie database path has no parent directory.");
        HashSet<string> supported = new(StringComparer.OrdinalIgnoreCase) {
            Path.GetFileName(cookieFiles.DatabasePath),
            CookieJournalFileName,
            CookieWriteAheadLogFileName,
            CookieSharedMemoryFileName
        };
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) {
            string fileName = Path.GetFileName(entry);
            if (!fileName.StartsWith("Cookies-", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, entry);
            if (!supported.Contains(fileName)) {
                throw new InvalidDataException("Claude Desktop Cookie directory contains an unsupported sidecar file.");
            }

            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) {
                throw new InvalidDataException("Claude Desktop Cookie sidecar path is a directory.");
            }
        }
    }

    private static string ReadLocalStateKeyDigest(byte[] localStateBytes) {
        try {
            using JsonDocument document = JsonDocument.Parse(localStateBytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("os_crypt", out JsonElement osCrypt)
                || osCrypt.ValueKind != JsonValueKind.Object
                || !osCrypt.TryGetProperty("encrypted_key", out JsonElement encryptedKeyElement)
                || encryptedKeyElement.ValueKind != JsonValueKind.String) {
                throw new InvalidDataException("Claude Local State encrypted key is missing.");
            }

            string? encryptedKey = encryptedKeyElement.GetString();
            if (string.IsNullOrWhiteSpace(encryptedKey)) {
                throw new InvalidDataException("Claude Local State encrypted key is empty.");
            }

            byte[] decodedKey;
            try {
                decodedKey = Convert.FromBase64String(encryptedKey);
            } catch (FormatException failure) {
                throw new InvalidDataException("Claude Local State encrypted key has an invalid format.", failure);
            }

            try {
                if (decodedKey.Length <= 5
                    || !decodedKey.AsSpan(0, 5).SequenceEqual("DPAPI"u8)) {
                    throw new InvalidDataException("Claude Local State encrypted key has an unsupported format.");
                }

                byte[] keyTextBytes = Encoding.UTF8.GetBytes(encryptedKey);
                try {
                    return ClaudeWindowsPathSafety.ComputeSha256(keyTextBytes);
                } finally {
                    CryptographicOperations.ZeroMemory(keyTextBytes);
                }
            } finally {
                CryptographicOperations.ZeroMemory(decodedKey);
            }
        } catch (JsonException failure) {
            throw new InvalidDataException("Claude Local State is malformed.", failure);
        }
    }

    private static byte[] ReplaceAuthFields(
        byte[] configBytes,
        ClaudeAccountProfile.DesktopSnapshot snapshot
    ) {
        ValidateTokenCacheJson(snapshot.TokenCacheJson);
        ValidateTokenCacheJson(snapshot.TokenCacheV2Json);
        try {
            using JsonDocument config = JsonDocument.Parse(configBytes, new JsonDocumentOptions { MaxDepth = 64 });
            if (config.RootElement.ValueKind != JsonValueKind.Object) {
                throw new InvalidDataException("Claude Desktop config.json must contain a JSON object.");
            }

            using JsonDocument? tokenCache = ParseOptionalJson(snapshot.TokenCacheJson);
            using JsonDocument? tokenCacheV2 = ParseOptionalJson(snapshot.TokenCacheV2Json);
            using MemoryStream output = new();
            using (Utf8JsonWriter writer = new(output, new JsonWriterOptions { Indented = true })) {
                writer.WriteStartObject();
                HashSet<string> propertyNames = new(StringComparer.Ordinal);
                foreach (JsonProperty property in config.RootElement.EnumerateObject()) {
                    if (!propertyNames.Add(property.Name)) {
                        throw new InvalidDataException("Claude Desktop config.json contains duplicate properties.");
                    }

                    if (IsAuthProperty(property.Name)) {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                }

                WriteOptionalJsonProperty(writer, TokenCacheProperty, tokenCache);
                WriteOptionalJsonProperty(writer, TokenCacheV2Property, tokenCacheV2);
                if (snapshot.LastKnownAccountUuid is not null) {
                    writer.WriteString(LastKnownAccountProperty, snapshot.LastKnownAccountUuid);
                }

                writer.WriteEndObject();
            }

            if (output.Length > MaximumConfigBytes) {
                throw new InvalidDataException("Claude Desktop config.json exceeded its supported size limit.");
            }

            return output.ToArray();
        } catch (JsonException failure) {
            throw new InvalidDataException("Claude Desktop config.json or auth snapshot is malformed.", failure);
        }
    }

    private static byte[] TakeContent(Dictionary<string, byte[]> contents, string path) {
        if (!contents.Remove(path, out byte[]? bytes)) {
            throw new InvalidDataException("A required Claude Desktop snapshot file was not captured.");
        }

        return bytes ?? throw new InvalidDataException("A required Claude Desktop snapshot file was not captured.");
    }

    private static byte[]? TakeOptionalContent(Dictionary<string, byte[]> contents, string path) {
        return contents.Remove(path, out byte[]? bytes) ? bytes : null;
    }

    private static int GetMaximumBytes(string root, string path) {
        if (string.Equals(
            Path.GetFullPath(path),
            ClaudeWindowsPathSafety.ResolveContainedPath(root, ConfigFileName),
            StringComparison.OrdinalIgnoreCase
        )) {
            return MaximumConfigBytes;
        }

        if (string.Equals(
            Path.GetFullPath(path),
            ClaudeWindowsPathSafety.ResolveContainedPath(root, LocalStateFileName),
            StringComparison.OrdinalIgnoreCase
        )) {
            return MaximumLocalStateBytes;
        }

        return MaximumCookieBytes;
    }

    private async Task<byte[]?> ReadOptionalFileAsync(
        string root,
        string path,
        CancellationToken cancellationToken
    ) {
        if (Directory.Exists(path)) {
            throw new InvalidDataException("Claude Desktop profile file path is a directory.");
        }

        if (!File.Exists(path)) {
            return null;
        }

        return await snapshotFileSystem.ReadSharedBoundedAsync(
            root,
            path,
            MaximumCookieBytes,
            cancellationToken
        ).ConfigureAwait(false);
    }

    private static bool PathSetsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right) {
        return left.Count == right.Count
            && left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);
    }

    private void EnsureStableFileSet(
        string root,
        CookieFiles cookieFiles,
        IReadOnlyList<string> expectedPaths,
        IReadOnlyList<ClaudeDesktopSnapshotFileSystem.SnapshotFile> lockedFiles
    ) {
        if (!IsStableFileSet(root, cookieFiles, expectedPaths, lockedFiles)) {
            throw new IOException("Claude Desktop profile files changed while a stable snapshot was being captured.");
        }
    }

    private bool IsStableFileSet(
        string root,
        CookieFiles cookieFiles,
        IReadOnlyList<string> expectedPaths,
        IReadOnlyList<ClaudeDesktopSnapshotFileSystem.SnapshotFile> lockedFiles
    ) {
        snapshotFileSystem.Observe(ClaudeDesktopSnapshotFileSystem.Operation.BeforeValidation, root);
        string[] currentPaths = DiscoverSnapshotFileSet(root, cookieFiles);
        if (!PathSetsEqual(expectedPaths, currentPaths) || lockedFiles.Count != expectedPaths.Count) {
            return false;
        }

        foreach (ClaudeDesktopSnapshotFileSystem.SnapshotFile file in lockedFiles) {
            if (!file.IsStable()) {
                return false;
            }
        }

        return true;
    }

    private static JsonDocument? ParseOptionalJson(string? json) {
        if (json is null) {
            return null;
        }

        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
    }

    private static void WriteOptionalJsonProperty(Utf8JsonWriter writer, string propertyName, JsonDocument? document) {
        if (document is null) {
            return;
        }

        writer.WritePropertyName(propertyName);
        document.RootElement.WriteTo(writer);
    }


    private void ObserveMutation(string path, bool rollback, bool before) {
        ClaudeDesktopSnapshotFileSystem.Operation operation = (rollback, before) switch {
            (false, true) => ClaudeDesktopSnapshotFileSystem.Operation.BeforeRestoreMutation,
            (false, false) => ClaudeDesktopSnapshotFileSystem.Operation.AfterRestoreMutation,
            (true, true) => ClaudeDesktopSnapshotFileSystem.Operation.BeforeRollbackMutation,
            (true, false) => ClaudeDesktopSnapshotFileSystem.Operation.AfterRollbackMutation
        };
        snapshotFileSystem.Observe(operation, path);
    }

    private async Task EnsureDesktopNotRunningAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    ) {
        IClaudeDesktopProcessController.ProcessStatus status;
        try {
            status = await processController.GetStatusAsync(installation, cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception failure) {
            throw new DesktopNotStoppedException("Claude Desktop process status could not be confirmed.", failure);
        }

        if (status != IClaudeDesktopProcessController.ProcessStatus.NotRunning) {
            throw new DesktopNotStoppedException("Claude Desktop must remain NotRunning before each profile file mutation.");
        }
    }


    private static void ParseTokenCache(
        string? json,
        string sourceField,
        List<IClaudeDesktopProfileStore.TokenCandidate> candidates,
        HashSet<string> tokens,
        byte[]? key
    ) {
        if (json is null) {
            return;
        }

        ValidateTokenCacheJson(json);
        if (!ClaudeSafeStorage.TryReadEncryptedValue(json, out string? encryptedValue)) {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            WalkTokenValues(document.RootElement, sourceField, candidates, tokens);
            return;
        }

        byte[] plaintext = ClaudeSafeStorage.Decrypt(
            encryptedValue!,
            key ?? throw new InvalidDataException("Claude token cache key is unavailable.")
        );
        try {
            using JsonDocument decrypted = JsonDocument.Parse(plaintext, new JsonDocumentOptions { MaxDepth = 64 });
            if (decrypted.RootElement.ValueKind != JsonValueKind.Object) {
                throw new InvalidDataException("Claude Desktop token cache has an unsupported format.");
            }

            // 只有明確含 user:profile 權限範圍的 token 能核對身分；其他範圍或無法判斷範圍的項目不列為候選。
            foreach (JsonProperty entry in decrypted.RootElement.EnumerateObject()) {
                if (HasProfileScope(entry.Name)) {
                    WalkTokenValues(entry.Value, sourceField + "." + entry.Name, candidates, tokens);
                }
            }
        } catch (JsonException failure) {
            throw new InvalidDataException("Claude Desktop token cache is malformed.", failure);
        } finally {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static bool HasProfileScope(string entryKey) {
        const string ScopeMarker = "https://api.anthropic.com:";
        int markerIndex = entryKey.LastIndexOf(ScopeMarker, StringComparison.Ordinal);
        if (markerIndex < 0) {
            return false;
        }

        return entryKey[(markerIndex + ScopeMarker.Length)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("user:profile", StringComparer.Ordinal);
    }

    private static void WalkTokenValues(
        JsonElement element,
        string sourceField,
        List<IClaudeDesktopProfileStore.TokenCandidate> candidates,
        HashSet<string> tokens
    ) {
        switch (element.ValueKind) {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject()) {
                    string childField = sourceField + "." + property.Name;
                    // safeStorage 版本以 token 欄位保存 access token；refreshToken 不作為候選。
                    if (string.Equals(property.Name, "accessToken", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(property.Name, "access_token", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(property.Name, "token", StringComparison.Ordinal)) {
                        if (property.Value.ValueKind == JsonValueKind.String) {
                            string? token = property.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(token)
                                && token.Length <= 32_768
                                && IsPrintableAscii(token)
                                && tokens.Add(token)) {
                                candidates.Add(new IClaudeDesktopProfileStore.TokenCandidate(token, childField));
                                if (candidates.Count > MaximumTokenCandidates) {
                                    throw new InvalidDataException("Claude Desktop token cache contains too many candidates.");
                                }
                            }
                        }
                    } else {
                        WalkTokenValues(property.Value, childField, candidates, tokens);
                    }
                }

                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray()) {
                    WalkTokenValues(item, sourceField + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]", candidates, tokens);
                    index++;
                }

                break;
        }
    }

    private static void ValidateTokenCacheJson(string? json) {
        if (json is null) {
            return;
        }

        if (Encoding.UTF8.GetByteCount(json) > MaximumTokenCacheBytes) {
            throw new InvalidDataException("Claude Desktop token cache exceeds its supported size limit.");
        }

        try {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            // 新版 Claude Desktop 以 safeStorage 加密後的字串保存 token cache。
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array
                or JsonValueKind.Null or JsonValueKind.String)) {
                throw new InvalidDataException("Claude Desktop token cache has an unsupported format.");
            }
        } catch (JsonException failure) {
            throw new InvalidDataException("Claude Desktop token cache is malformed.", failure);
        }
    }

    private static void ValidateSnapshot(ClaudeAccountProfile.DesktopSnapshot snapshot) {
        if (snapshot.Cookies is null
            || snapshot.Cookies.Length == 0
            || snapshot.Cookies.Length > MaximumCookieBytes
            || (snapshot.CookieJournal is not null && snapshot.CookieJournal.Length > MaximumCookieBytes)
            || (snapshot.CookieWriteAheadLog is not null && snapshot.CookieWriteAheadLog.Length > MaximumCookieBytes)
            || (snapshot.CookieSharedMemory is not null && snapshot.CookieSharedMemory.Length > MaximumCookieBytes)
            || snapshot.CookieSidecarSnapshotVersion != 1
            || string.IsNullOrWhiteSpace(snapshot.CookieRelativePath)
            || !IsSupportedCookieRelativePath(snapshot.CookieRelativePath)
            || string.IsNullOrWhiteSpace(snapshot.InstallationId)
            || !IsSha256(snapshot.DataRootFingerprint)
            || !IsSha256(snapshot.LocalStateEncryptedKeySha256)
            || !IsSid(snapshot.WindowsUserSid)
            || snapshot.CapturedAtUtc.Offset != TimeSpan.Zero) {
            throw new ArgumentException("Claude Desktop profile snapshot is invalid.", nameof(snapshot));
        }

        ValidateTokenCacheJson(snapshot.TokenCacheJson);
        ValidateTokenCacheJson(snapshot.TokenCacheV2Json);
        if (snapshot.LastKnownAccountUuid is not null
            && (!Guid.TryParseExact(snapshot.LastKnownAccountUuid, "D", out Guid accountId) || accountId == Guid.Empty)) {
            throw new ArgumentException("Claude Desktop snapshot account UUID is invalid.", nameof(snapshot));
        }
    }

    private static string ValidateSid(string sid) {
        if (!IsSid(sid)) {
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        }

        return sid;
    }

    private static bool IsSid(string sid) {
        try {
            return !string.IsNullOrWhiteSpace(sid)
                && string.Equals(new SecurityIdentifier(sid).Value, sid, StringComparison.Ordinal);
        } catch (ArgumentException) {
            return false;
        }
    }

    private static DateTimeOffset EnsureUtc(DateTimeOffset value) {
        if (value.Offset != TimeSpan.Zero) {
            throw new InvalidOperationException("The Claude profile store clock must return UTC.");
        }

        return value;
    }

    private static bool IsAuthProperty(string propertyName) {
        return string.Equals(propertyName, TokenCacheProperty, StringComparison.Ordinal)
            || string.Equals(propertyName, TokenCacheV2Property, StringComparison.Ordinal)
            || string.Equals(propertyName, LastKnownAccountProperty, StringComparison.Ordinal);
    }

    private static bool IsSupportedCookieRelativePath(string value) {
        string normalized = value.Replace('\\', '/');
        return string.Equals(normalized, CookiesRelativePath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, NetworkCookiesRelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string value) {
        return value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static bool IsPrintableAscii(string value) {
        foreach (char character in value) {
            if (character < '!' || character > '~') {
                return false;
            }
        }

        return true;
    }

    private static void Zero(byte[]? value) {
        if (value is not null) {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static void ZeroSnapshot(ClaudeAccountProfile.DesktopSnapshot snapshot) {
        CryptographicOperations.ZeroMemory(snapshot.Cookies);
        Zero(snapshot.CookieJournal);
        Zero(snapshot.CookieWriteAheadLog);
        Zero(snapshot.CookieSharedMemory);
    }

    private sealed class WindowsStableSnapshotLease : IClaudeDesktopProfileStore.StableSnapshotLease {
        private readonly WindowsClaudeDesktopProfileStore owner;
        private readonly string root;
        private readonly CookieFiles cookieFiles;
        private readonly string[] expectedPaths;
        private readonly ClaudeAccountProfile.DesktopSnapshot snapshot;
        private readonly IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> tokenCandidates;
        private List<ClaudeDesktopSnapshotFileSystem.SnapshotFile>? lockedFiles;
        private int disposed;

        internal WindowsStableSnapshotLease(
            WindowsClaudeDesktopProfileStore owner,
            string root,
            CookieFiles cookieFiles,
            string[] expectedPaths,
            List<ClaudeDesktopSnapshotFileSystem.SnapshotFile> lockedFiles,
            ClaudeAccountProfile.DesktopSnapshot snapshot,
            IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> tokenCandidates
        ) {
            this.owner = owner;
            this.root = root;
            this.cookieFiles = cookieFiles;
            this.expectedPaths = expectedPaths;
            this.lockedFiles = lockedFiles;
            this.snapshot = snapshot;
            this.tokenCandidates = tokenCandidates;
        }

        public ClaudeAccountProfile.DesktopSnapshot Snapshot => snapshot;

        public IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> TokenCandidates => tokenCandidates;

        public Task<bool> ValidateAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            List<ClaudeDesktopSnapshotFileSystem.SnapshotFile>? files = Volatile.Read(ref lockedFiles);
            if (Volatile.Read(ref disposed) != 0 || files is null) {
                return Task.FromResult(false);
            }

            try {
                return Task.FromResult(owner.IsStableFileSet(root, cookieFiles, expectedPaths, files));
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception failure) when (
                failure is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException
                    or ArgumentException
                    or ObjectDisposedException
            ) {
                return Task.FromResult(false);
            }
        }

        public ValueTask DisposeAsync() {
            if (Interlocked.Exchange(ref disposed, 1) == 0) {
                List<ClaudeDesktopSnapshotFileSystem.SnapshotFile>? files = Interlocked.Exchange(ref lockedFiles, null);
                if (files is not null) {
                    foreach (ClaudeDesktopSnapshotFileSystem.SnapshotFile file in files) {
                        file.Dispose();
                    }
                }

                ZeroSnapshot(snapshot);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record AuthFields(string? TokenCacheJson, string? TokenCacheV2Json, string? LastKnownAccountUuid);

    private sealed record CookieFiles(
        string RelativePath,
        string DatabasePath,
        string JournalPath,
        string WriteAheadLogPath,
        string SharedMemoryPath
    );

    private sealed record FileUpdate(string? Path, byte[]? Content);

    private sealed record ExistingFile(string? Path, byte[]? Content);

    private sealed record StagedFile(string DestinationPath, string StagingPath, bool Delete);

    private sealed record BackupFile(string? DestinationPath, string? BackupPath, bool Existed);

    private sealed class DesktopNotStoppedException : IOException {
        internal DesktopNotStoppedException(string message)
            : base(message) {
        }

        internal DesktopNotStoppedException(string message, Exception innerException)
            : base(message, innerException) {
        }
    }

    private static string GetCurrentSid() {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
    }
}
