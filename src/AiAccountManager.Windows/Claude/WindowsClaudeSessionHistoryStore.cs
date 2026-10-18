using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;

namespace CloudyWing.AiAccountManager.Windows.Claude;

/// <summary>
/// 驗證、備份、套用與回復 Claude Code account index。
/// </summary>
public sealed class WindowsClaudeSessionHistoryStore : IClaudeSessionHistoryStore {
    private const string SessionsDirectoryName = "claude-code-sessions";
    private const string TransactionsDirectoryName = ".ai-account-manager-transactions";
    private const int MaximumIndexFileBytes = 8 * 1024 * 1024;
    private const int MaximumSourceIndexBytes = 128 * 1024 * 1024;
    private const long MaximumTargetBackupBytes = 1024L * 1024 * 1024;
    private const int MaximumIndexFiles = 10_000;
    private const int MaximumManifestBytes = 8 * 1024 * 1024;
    private const string ManifestFileName = "manifest.json";
    private const string CleanupStartedFileName = "cleanup-started";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly string[] TransactionDirectoryNames = [
        "source-staging",
        "target-backup",
        "target-backup.partial",
        "target-displaced",
        "target-rollback-staging",
        "target-rollback-displaced",
        "live-files"
    ];
    private static readonly string[] PreparationDirectoryNames = [
        "source-staging",
        "target-backup.partial",
        "target-backup"
    ];
    private static readonly string[] TransactionMarkerFileNames = [
        "history-apply-started",
        "history-rollback-completed",
        CleanupStartedFileName
    ];
    private readonly string userProfilePath;
    private readonly IClaudeDesktopProcessController processController;
    private readonly Action<string, string>? cleanupObserver;
    private readonly Action? afterPreparationPayloadWritten;
    private readonly Action? beforePreparationIntentPublished;

    /// <summary>
    /// 建立使用目前 Windows 使用者 profile 的 Claude Code history store。
    /// </summary>
    /// <param name="processController">查詢 Claude Desktop 程序狀態的控制器。</param>
    public WindowsClaudeSessionHistoryStore(IClaudeDesktopProcessController processController)
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            processController,
            null,
            null,
            null
        ) {
    }

    internal WindowsClaudeSessionHistoryStore(
        string userProfilePath,
        IClaudeDesktopProcessController processController
    )
        : this(userProfilePath, processController, null, null, null) {
    }

    internal WindowsClaudeSessionHistoryStore(
        string userProfilePath,
        IClaudeDesktopProcessController processController,
        Action<string, string>? cleanupObserver
    )
        : this(userProfilePath, processController, cleanupObserver, null, null) {
    }

    internal WindowsClaudeSessionHistoryStore(
        string userProfilePath,
        IClaudeDesktopProcessController processController,
        Action<string, string>? cleanupObserver,
        Action? afterPreparationPayloadWritten
    )
        : this(userProfilePath, processController, cleanupObserver, afterPreparationPayloadWritten, null) {
    }

    internal WindowsClaudeSessionHistoryStore(
        string userProfilePath,
        IClaudeDesktopProcessController processController,
        Action<string, string>? cleanupObserver,
        Action? afterPreparationPayloadWritten,
        Action? beforePreparationIntentPublished
    ) {
        ArgumentNullException.ThrowIfNull(processController);
        this.userProfilePath = string.IsNullOrWhiteSpace(userProfilePath)
            ? ""
            : Path.GetFullPath(userProfilePath);
        this.processController = processController;
        this.cleanupObserver = cleanupObserver;
        this.afterPreparationPayloadWritten = afterPreparationPayloadWritten;
        this.beforePreparationIntentPublished = beforePreparationIntentPublished;
    }

    /// <inheritdoc />
    public Task<IClaudeSessionHistoryStore.ValidationResult> ValidateAccountAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        string accountUuid,
        string organizationUuid,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(installation);
        Guid accountId = ParseUuid(accountUuid, nameof(accountUuid));
        Guid organizationId = ParseUuid(organizationUuid, nameof(organizationUuid));
        return Task.Run(
            () => ValidateAccount(installation, accountId, organizationId, cancellationToken),
            CancellationToken.None
        );
    }

    /// <inheritdoc />
    public Task<IClaudeSessionHistoryStore.PreparationResult> PrepareReplacementAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        string sourceAccountUuid,
        string sourceOrganizationUuid,
        string targetAccountUuid,
        string targetOrganizationUuid,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(installation);
        Guid sourceAccount = ParseUuid(sourceAccountUuid, nameof(sourceAccountUuid));
        Guid sourceOrganization = ParseUuid(sourceOrganizationUuid, nameof(sourceOrganizationUuid));
        Guid targetAccount = ParseUuid(targetAccountUuid, nameof(targetAccountUuid));
        Guid targetOrganization = ParseUuid(targetOrganizationUuid, nameof(targetOrganizationUuid));
        ValidateTransactionId(transactionId);
        if (sourceAccount == targetAccount && sourceOrganization == targetOrganization) {
            throw new ArgumentException("Claude source and target histories must be different.", nameof(targetAccountUuid));
        }

        return Task.Run(
            () => PrepareReplacement(
                installation,
                sourceAccount,
                sourceOrganization,
                targetAccount,
                targetOrganization,
                transactionId,
                cancellationToken
            ),
            CancellationToken.None
        );
    }

    /// <inheritdoc />
    public Task ApplyReplacementAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        string targetAccountUuid,
        string targetOrganizationUuid,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(installation);
        Guid targetAccount = ParseUuid(targetAccountUuid, nameof(targetAccountUuid));
        Guid targetOrganization = ParseUuid(targetOrganizationUuid, nameof(targetOrganizationUuid));
        ValidateTransactionId(transactionId);
        return Task.Run(
            () => ApplyReplacementAsync(installation, targetAccount, targetOrganization, transactionId, cancellationToken),
            CancellationToken.None
        );
    }

    /// <inheritdoc />
    public Task RestoreTargetAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        string targetAccountUuid,
        string targetOrganizationUuid,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(installation);
        Guid targetAccount = ParseUuid(targetAccountUuid, nameof(targetAccountUuid));
        Guid targetOrganization = ParseUuid(targetOrganizationUuid, nameof(targetOrganizationUuid));
        ValidateTransactionId(transactionId);
        return Task.Run(
            () => RestoreTargetAsync(installation, targetAccount, targetOrganization, transactionId, cancellationToken),
            CancellationToken.None
        );
    }

    /// <inheritdoc />
    public Task CommitAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid transactionId,
        ClaudeSessionHistoryCleanupIntent cleanupIntent,
        CancellationToken cancellationToken
    ) {
        if (cleanupIntent is not (
            ClaudeSessionHistoryCleanupIntent.Preparation or ClaudeSessionHistoryCleanupIntent.Committed
        )) {
            return Task.FromException(
                new ArgumentOutOfRangeException(
                    nameof(cleanupIntent),
                    cleanupIntent,
                    "Claude history cleanup intent is invalid."
                )
            );
        }

        ArgumentNullException.ThrowIfNull(installation);
        ValidateTransactionId(transactionId);
        return Task.Run(
            () => Commit(installation, transactionId, cleanupIntent, cancellationToken),
            CancellationToken.None
        );
    }

    private IClaudeSessionHistoryStore.ValidationResult ValidateAccount(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid accountId,
        Guid organizationId,
        CancellationToken cancellationToken
    ) {
        try {
            cancellationToken.ThrowIfCancellationRequested();
            using IndexSnapshot snapshot = ReadIndexSnapshot(installation, accountId, organizationId, cancellationToken);
            return new IClaudeSessionHistoryStore.ValidationResult(
                IClaudeSessionHistoryStore.ValidationStatus.Valid,
                snapshot.SessionCount,
                null
            );
        } catch (HistoryFailure failure) {
            return new IClaudeSessionHistoryStore.ValidationResult(failure.Status, 0, failure.FailureCode);
        } catch (UnauthorizedAccessException) {
            return ValidationFailure(IClaudeSessionHistoryStore.ValidationStatus.Unreadable, "source-unreadable");
        } catch (IOException) {
            return ValidationFailure(IClaudeSessionHistoryStore.ValidationStatus.Unreadable, "source-unreadable");
        } catch (InvalidDataException) {
            return ValidationFailure(IClaudeSessionHistoryStore.ValidationStatus.InvalidIndex, "source-path-unsafe");
        }
    }

    private IClaudeSessionHistoryStore.PreparationResult PrepareReplacement(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid sourceAccount,
        Guid sourceOrganization,
        Guid targetAccount,
        Guid targetOrganization,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        IndexSnapshot? source = null;
        try {
            cancellationToken.ThrowIfCancellationRequested();
            source = ReadIndexSnapshot(installation, sourceAccount, sourceOrganization, cancellationToken);
            string root = ValidateInstallation(installation);
            string sessionsRoot = GetSessionsRoot(root);
            string targetPath = GetIndexPath(root, targetAccount, targetOrganization);
            string transactionPath = GetTransactionPath(sessionsRoot, transactionId);
            ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, transactionPath);

            if (Directory.Exists(transactionPath) || File.Exists(transactionPath)) {
                return LoadPreparedTransaction(
                    transactionPath,
                    root,
                    transactionId,
                    sourceAccount,
                    sourceOrganization,
                    targetAccount,
                    targetOrganization,
                    targetPath
                );
            }

            TargetSnapshot target = CaptureTargetSnapshot(root, targetPath);
            using IndexSnapshot? targetIndex = target.Exists
                ? ReadIndexSnapshot(installation, targetAccount, targetOrganization, cancellationToken)
                : null;
            if (targetIndex is not null && !InventoriesEqual(target.Files, targetIndex.AllFiles)) {
                throw new IOException("Claude target history changed while it was being validated.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(transactionPath);
            cancellationToken.ThrowIfCancellationRequested();
            string sourceStagingPath = GetSourceStagingPath(transactionPath);
            string targetBackupPath = GetTargetBackupPath(transactionPath);
            string targetBackupTemporaryPath = GetTargetBackupTemporaryPath(transactionPath);
            TransactionManifest manifest = new(
                2,
                transactionId,
                root,
                sourceAccount.ToString("D"),
                sourceOrganization.ToString("D"),
                targetAccount.ToString("D"),
                targetOrganization.ToString("D"),
                target.Exists,
                target.AccountDirectoryExisted,
                target.OrganizationDirectoryExisted,
                source.SessionCount,
                targetIndex?.SessionCount ?? 0,
                source.Files,
                target.Files,
                Path.GetFullPath(targetPath),
                PreparationState.Preparing,
                TransactionDirectoryNames,
                ProvenanceSchemaVersion: 1,
                RootBinding: installation.Binding ?? throw new InvalidDataException("Claude history preparation requires trusted provenance.")
            );
            beforePreparationIntentPublished?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            WriteManifest(transactionPath, manifest);

            WriteSourceStaging(source, sourceStagingPath, cancellationToken, afterPreparationPayloadWritten);
            cancellationToken.ThrowIfCancellationRequested();
            if (target.Exists) {
                CopyTargetIndexContents(targetPath, targetBackupTemporaryPath);
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<FileFingerprint> copiedTarget = CaptureDirectoryInventory(
                    targetBackupTemporaryPath,
                    MaximumTargetBackupBytes
                );
                if (!InventoriesEqual(target.Files, copiedTarget)) {
                    throw new IOException("Claude target history changed while its backup was being created.");
                }

                Directory.Move(targetBackupTemporaryPath, targetBackupPath);
            }

            IReadOnlyList<FileFingerprint> stagedSource = CaptureDirectoryInventory(
                sourceStagingPath,
                MaximumSourceIndexBytes
            );
            if (!InventoriesEqual(source.Files, stagedSource)) {
                throw new IOException("Claude source history changed while it was being staged.");
            }

            IReadOnlyList<FileFingerprint> currentSource = CaptureDirectoryInventory(
                source.DirectoryPath,
                MaximumSourceIndexBytes,
                firstLevelOnly: true
            );
            if (!InventoriesEqual(source.AllFiles, currentSource)) {
                throw new HistoryFailure(
                    IClaudeSessionHistoryStore.ValidationStatus.Unreadable,
                    "source-changed-during-preparation"
                );
            }

            List<WindowsClaudeHistoryCleanupFileSystem.FileChange> changes = [];
            foreach (FileFingerprint file in target.Files.Where(file => !source.Files.Any(item =>
                string.Equals(item.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase)))) {
                changes.Add(new(ClaudeWindowsPathSafety.ResolveContainedPath(targetPath, file.RelativePath), null));
            }

            foreach (SourceEntry entry in source.Entries) {
                changes.Add(new(ClaudeWindowsPathSafety.ResolveContainedPath(targetPath, entry.FileName), entry.Content));
            }

            CreateFileTransaction(installation, transactionId, transactionPath).Prepare(changes, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            manifest = manifest with { PreparationState = PreparationState.Prepared };
            WriteManifest(transactionPath, manifest);
            return new IClaudeSessionHistoryStore.PreparationResult(
                IClaudeSessionHistoryStore.ValidationStatus.Valid,
                source.SessionCount,
                manifest.TargetSessionCount,
                null
            );
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (HistoryFailure failure) {
            return new IClaudeSessionHistoryStore.PreparationResult(failure.Status, 0, 0, failure.FailureCode);
        } catch (UnauthorizedAccessException) {
            return new IClaudeSessionHistoryStore.PreparationResult(
                IClaudeSessionHistoryStore.ValidationStatus.Unreadable,
                0,
                0,
                "history-access-denied"
            );
        } catch (IOException) {
            return new IClaudeSessionHistoryStore.PreparationResult(
                IClaudeSessionHistoryStore.ValidationStatus.Unreadable,
                0,
                0,
                "history-preparation-failed"
            );
        } catch (InvalidDataException) {
            return new IClaudeSessionHistoryStore.PreparationResult(
                IClaudeSessionHistoryStore.ValidationStatus.InvalidIndex,
                0,
                0,
                "history-path-unsafe"
            );
        } finally {
            source?.Dispose();
        }
    }

    private IClaudeSessionHistoryStore.PreparationResult LoadPreparedTransaction(
        string transactionPath,
        string root,
        Guid transactionId,
        Guid sourceAccount,
        Guid sourceOrganization,
        Guid targetAccount,
        Guid targetOrganization,
        string targetPath
    ) {
        TransactionManifest manifest = ReadManifest(transactionPath);
        if (manifest.ProvenanceSchemaVersion != 1 || manifest.RootBinding is null) {
            throw new InvalidDataException("Claude history preparation lacks source provenance.");
        }
        string organizationDirectoryPath = Path.GetDirectoryName(targetPath)!;
        string accountDirectoryPath = Path.GetDirectoryName(organizationDirectoryPath)!;
        if (!IsManifestFor(
            manifest,
            root,
            transactionId,
            sourceAccount,
            sourceOrganization,
            targetAccount,
            targetOrganization
        ) || !Directory.Exists(GetSourceStagingPath(transactionPath))
            || File.Exists(GetApplyStartedPath(transactionPath))
            || File.Exists(GetCleanupStartedPath(transactionPath))
            || (manifest.SchemaVersion == 2 && manifest.PreparationState != PreparationState.Prepared)
            || !ManifestTargetPathMatches(manifest, root, targetPath)
            || !InventoriesEqual(
                manifest.SourceFiles,
                CaptureDirectoryInventory(GetSourceStagingPath(transactionPath), MaximumSourceIndexBytes)
            )
            || (manifest.TargetExisted
                && (!Directory.Exists(GetTargetBackupPath(transactionPath))
                    || !InventoriesEqual(
                        manifest.TargetFiles,
                        CaptureDirectoryInventory(GetTargetBackupPath(transactionPath), MaximumTargetBackupBytes)
                    )))
            || File.Exists(targetPath)
            || Directory.Exists(targetPath)
                && !InventoriesEqual(manifest.TargetFiles, CaptureDirectoryInventory(targetPath, MaximumTargetBackupBytes, true))
            || Directory.Exists(organizationDirectoryPath) != manifest.OrganizationDirectoryExisted
            || Directory.Exists(accountDirectoryPath) != manifest.AccountDirectoryExisted) {
            throw new HistoryFailure(
                IClaudeSessionHistoryStore.ValidationStatus.Unreadable,
                "transaction-preparation-is-inconsistent"
            );
        }

        return new IClaudeSessionHistoryStore.PreparationResult(
            IClaudeSessionHistoryStore.ValidationStatus.Valid,
            manifest.SourceSessionCount,
            manifest.TargetSessionCount,
            null
        );
    }

    private async Task ApplyReplacementAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid targetAccount,
        Guid targetOrganization,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        string root = ValidateInstallation(installation);
        string transactionPath = GetTransactionPath(GetSessionsRoot(root), transactionId);
        TransactionManifest manifest = ReadManifest(transactionPath);
        EnsureManifestBinding(installation, manifest);
        EnsureManifestTargets(manifest, root, transactionId, targetAccount, targetOrganization);
        EnsurePreparedManifest(manifest);
        if (!File.Exists(GetApplyStartedPath(transactionPath))) {
            ClaudeWindowsPathSafety.WriteFileAtomically(GetApplyStartedPath(transactionPath), "started"u8.ToArray());
        }

        await CreateFileTransaction(installation, transactionId, transactionPath).ApplyAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RestoreTargetAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid targetAccount,
        Guid targetOrganization,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        string root = ValidateInstallation(installation);
        string transactionPath = GetTransactionPath(GetSessionsRoot(root), transactionId);
        if (!Directory.Exists(transactionPath)) {
            throw new IOException("Claude history rollback evidence is missing.");
        }

        TransactionManifest manifest = ReadManifest(transactionPath);
        EnsureManifestBinding(installation, manifest);
        EnsureManifestTargets(manifest, root, transactionId, targetAccount, targetOrganization);
        if (!File.Exists(GetApplyStartedPath(transactionPath))) {
            return;
        }

        EnsurePreparedManifest(manifest);
        WindowsClaudeHistoryCleanupFileSystem.DurableFileTransaction fileTransaction =
            CreateFileTransaction(installation, transactionId, transactionPath);
        await fileTransaction.RestoreAsync(cancellationToken).ConfigureAwait(false);
        string targetPath = GetIndexPath(root, targetAccount, targetOrganization);
        WindowsClaudeHistoryCleanupFileSystem.FileIdentity? targetDirectoryIdentity = fileTransaction.GetParentIdentity(targetPath);
        await RestoreTargetDirectoriesAsync(
            installation,
            root,
            GetSessionsRoot(root),
            targetPath,
            manifest,
            targetDirectoryIdentity,
            cancellationToken
        )
            .ConfigureAwait(false);
        if (!IsTargetRestored(targetPath, manifest)) {
            throw new IOException("Claude history rollback did not restore the original inventory.");
        }

        ClaudeWindowsPathSafety.WriteFileAtomically(GetRollbackCompletedPath(transactionPath), "restored"u8.ToArray());
    }

    private WindowsClaudeHistoryCleanupFileSystem.DurableFileTransaction CreateFileTransaction(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid transactionId,
        string transactionPath,
        Action? cleanupLiveStateGuard = null
    ) {
        ClaudeDesktopRootBinding binding = installation.Binding
            ?? throw new InvalidDataException("Claude history transaction requires a trusted binding.");
        return new(ValidateInstallation(installation), Path.Combine(transactionPath, "live-files"), transactionId, binding,
            token => EnsureDesktopNotRunningAsync(installation, token), cleanupObserver, cleanupLiveStateGuard);
    }

    private static void EnsureManifestBinding(
        IClaudeDesktopInstallationLocator.Installation installation, TransactionManifest manifest
    ) {
        if (manifest.ProvenanceSchemaVersion != 1 || manifest.RootBinding is null || installation.Binding is null
            || !string.Equals(JsonSerializer.Serialize(manifest.RootBinding), JsonSerializer.Serialize(installation.Binding),
                StringComparison.Ordinal)) {
            throw new InvalidDataException("Claude history transaction source provenance is unavailable or changed.");
        }
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
            throw new HistoryProcessGuardFailure("Claude Desktop process status could not be confirmed.", failure);
        }

        if (status != IClaudeDesktopProcessController.ProcessStatus.NotRunning) {
            throw new HistoryProcessGuardFailure("Claude Desktop must be confirmed stopped before changing its history.");
        }
    }

    private async Task EnsureTargetParentDirectoriesAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        string root,
        string sessionsRoot,
        string targetOrganizationDirectoryPath,
        CancellationToken cancellationToken
    ) {
        string targetAccountDirectoryPath = Path.GetDirectoryName(targetOrganizationDirectoryPath)!;
        ClaudeWindowsPathSafety.EnsureContained(sessionsRoot, targetAccountDirectoryPath);
        ClaudeWindowsPathSafety.EnsureContained(sessionsRoot, targetOrganizationDirectoryPath);
        if (!Directory.Exists(sessionsRoot)) {
            throw new DirectoryNotFoundException("Claude Code sessions root disappeared during history replacement.");
        }

        if (!Directory.Exists(targetAccountDirectoryPath)) {
            if (File.Exists(targetAccountDirectoryPath)) {
                throw new IOException("Claude target account path was replaced by a file.");
            }

            ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, targetAccountDirectoryPath);
            await EnsureDesktopNotRunningAsync(installation, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(targetAccountDirectoryPath);
        }

        if (!Directory.Exists(targetOrganizationDirectoryPath)) {
            if (File.Exists(targetOrganizationDirectoryPath)) {
                throw new IOException("Claude target organization path was replaced by a file.");
            }

            ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, targetOrganizationDirectoryPath);
            await EnsureDesktopNotRunningAsync(installation, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(targetOrganizationDirectoryPath);
        }
    }

    private void Commit(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid transactionId,
        ClaudeSessionHistoryCleanupIntent cleanupIntent,
        CancellationToken cancellationToken
    ) {
        if (cleanupIntent is not (
            ClaudeSessionHistoryCleanupIntent.Preparation or ClaudeSessionHistoryCleanupIntent.Committed
        )) {
            throw new ArgumentOutOfRangeException(
                nameof(cleanupIntent),
                cleanupIntent,
                "Claude history cleanup intent is invalid."
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        string root = ValidateInstallation(installation);
        string sessionsRoot = GetSessionsRoot(root);
        string transactionPath = GetTransactionPath(sessionsRoot, transactionId);
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, transactionPath);
        if (!Directory.Exists(transactionPath)) {
            if (File.Exists(transactionPath)) {
                throw new IOException("Claude history transaction path was replaced by a file.");
            }

            return;
        }

        string manifestPath = GetManifestPath(transactionPath);
        if (!File.Exists(manifestPath)) {
            CleanupManifestlessTransactionDirectory(root, transactionPath, cleanupIntent, cancellationToken);
            return;
        }

        using WindowsClaudeHistoryCleanupFileSystem.FileContentsSnapshot manifestContents =
            WindowsClaudeHistoryCleanupFileSystem.ReadFileContentsSnapshot(root, manifestPath, MaximumManifestBytes);
        TransactionManifest manifest = DeserializeManifest(manifestContents.Content);
        EnsureManifestBinding(installation, manifest);
        if (manifest.TransactionId != transactionId
            || !string.Equals(manifest.DataRoot, root, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException("Claude history transaction does not match this Desktop installation.");
        }

        string targetPath = GetIndexPath(
            root,
            ParseUuid(manifest.TargetAccountUuid, nameof(manifest.TargetAccountUuid)),
            ParseUuid(manifest.TargetOrganizationUuid, nameof(manifest.TargetOrganizationUuid))
        );
        EnsureManifestTargetPath(manifest, root, targetPath);

        EnsureKnownTransactionEntries(transactionPath);
        string cleanupStartedPath = GetCleanupStartedPath(transactionPath);
        bool cleanupStarted = File.Exists(cleanupStartedPath);
        if (cleanupStarted) {
            ValidateCleanupStartedMarker(cleanupStartedPath);
        }

        bool isPreparation = cleanupIntent == ClaudeSessionHistoryCleanupIntent.Preparation;
        if (isPreparation && !IsTargetRestored(targetPath, manifest)) {
            throw new InvalidDataException("Claude history preparation cleanup requires the original target state.");
        }

        Action? ensureTargetState = isPreparation ? () => {
            if (!IsTargetRestored(targetPath, manifest)) {
                throw new InvalidDataException("Claude history target changed while transaction cleanup was in progress.");
            }
        } : null;

        CreateFileTransaction(installation, transactionId, transactionPath, ensureTargetState)
            .Cleanup(cancellationToken, verifyLiveState: isPreparation);
        ensureTargetState?.Invoke();

        if (cleanupIntent == ClaudeSessionHistoryCleanupIntent.Preparation) {
            if (manifest.SchemaVersion == 2 && manifest.PreparationState == PreparationState.Preparing) {
                EnsurePreparationTransactionCanBeCleaned(
                    root,
                    transactionPath,
                    manifest,
                    targetPath,
                    cleanupStarted
                );
                if (!cleanupStarted) {
                    ClaudeWindowsPathSafety.WriteFileAtomically(cleanupStartedPath, "cleanup-started"u8.ToArray());
                }

                CleanupPreparationTransactionDirectory(
                    root,
                    transactionPath,
                    manifest,
                    manifestPath,
                    cleanupStartedPath,
                    manifestContents.Snapshot,
                    ensureTargetState!,
                    cancellationToken
                );
                return;
            }

            EnsurePreparedManifest(manifest);
            if (!cleanupStarted) {
                ClaudeWindowsPathSafety.WriteFileAtomically(cleanupStartedPath, "cleanup-started"u8.ToArray());
            }

            CleanupTransactionDirectory(
                root,
                transactionPath,
                manifest,
                manifestPath,
                cleanupStartedPath,
                manifestContents.Snapshot,
                ensureTargetState,
                cancellationToken
            );
            return;
        }

        EnsurePreparedManifest(manifest);
        if (!cleanupStarted) {
            ClaudeWindowsPathSafety.WriteFileAtomically(cleanupStartedPath, "cleanup-started"u8.ToArray());
        }

        CleanupTransactionDirectory(
            root,
            transactionPath,
            manifest,
            manifestPath,
            cleanupStartedPath,
            manifestContents.Snapshot,
            ensureTargetState,
            cancellationToken
        );
    }

    private void CleanupTransactionDirectory(
        string root,
        string transactionPath,
        TransactionManifest manifest,
        string manifestPath,
        string cleanupStartedPath,
        WindowsClaudeHistoryCleanupFileSystem.FileSnapshot manifestSnapshot,
        Action? ensureTargetState,
        CancellationToken cancellationToken
    ) {
        CleanupTransactionContents(
            root,
            transactionPath,
            manifest,
            manifestPath,
            cleanupStartedPath,
            manifestSnapshot,
            ensureTargetState,
            preparationOnly: false,
            cancellationToken
        );
    }
    private static void EnsurePreparationTransactionCanBeCleaned(
        string root,
        string transactionPath,
        TransactionManifest manifest,
        string targetPath,
        bool cleanupStarted
    ) {
        if (manifest.SchemaVersion != 2
            || manifest.PreparationState is not PreparationState.Preparing and not PreparationState.Prepared
            || File.Exists(GetApplyStartedPath(transactionPath))
            || File.Exists(GetRollbackCompletedPath(transactionPath))
            || !IsTargetRestored(targetPath, manifest)) {
            throw new InvalidDataException("Claude history preparation intent is inconsistent with the live target.");
        }

        string manifestPath = GetManifestPath(transactionPath);
        string cleanupStartedPath = GetCleanupStartedPath(transactionPath);
        foreach (string entry in Directory.EnumerateFileSystemEntries(transactionPath)) {
            string name = Path.GetFileName(entry);
            if (string.Equals(entry, manifestPath, StringComparison.OrdinalIgnoreCase)) {
                ValidateTransactionEntryType(root, entry, expectDirectory: false);
                continue;
            }

            if (string.Equals(entry, cleanupStartedPath, StringComparison.OrdinalIgnoreCase)) {
                ValidateTransactionEntryType(root, entry, expectDirectory: false);
                ValidateCleanupStartedMarker(entry);
                continue;
            }

            if (TryGetAtomicTemporaryFileBaseName(name, out string baseName)
                && (string.Equals(baseName, ManifestFileName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(baseName, CleanupStartedFileName, StringComparison.OrdinalIgnoreCase))) {
                ValidateTransactionEntryType(root, entry, expectDirectory: false);
                continue;
            }

            if (PreparationDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase)) {
                ValidateTransactionEntryType(root, entry, expectDirectory: true);
                EnsurePreparationDirectoryContents(root, name, entry, manifest);
                continue;
            }

            throw new InvalidDataException("Claude history preparation contains an unknown transaction entry.");
        }

        if (File.Exists(GetApplyStartedPath(transactionPath))
            || Directory.Exists(GetApplyStartedPath(transactionPath))
            || File.Exists(Path.Combine(transactionPath, "history-rollback-completed"))
            || Directory.Exists(Path.Combine(transactionPath, "history-rollback-completed"))) {
            throw new InvalidDataException("Claude history preparation has an unexpected live apply marker.");
        }

        if (cleanupStarted && !File.Exists(cleanupStartedPath)) {
            throw new IOException("Claude history preparation cleanup marker disappeared.");
        }
    }

    private static void EnsurePreparationDirectoryContents(
        string root,
        string directoryName,
        string directoryPath,
        TransactionManifest manifest
    ) {
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, directoryPath);
        if (!manifest.TargetExisted && directoryName is "target-backup.partial" or "target-backup") {
            throw new InvalidDataException("Claude history preparation contains a backup for a missing target.");
        }

        IReadOnlyList<FileFingerprint> expectedFiles = directoryName == "source-staging"
            ? manifest.SourceFiles
            : manifest.TargetFiles;
        long maximumBytes = directoryName == "source-staging"
            ? MaximumSourceIndexBytes
            : MaximumTargetBackupBytes;
        if (manifest.PreparationState == PreparationState.Prepared) {
            EnsureDirectoryInventoryIsSubset(root, directoryPath, expectedFiles, maximumBytes);
            return;
        }

        EnsureDirectoryContainsOnlyIntentFiles(root, directoryPath, expectedFiles, maximumBytes);
    }

    private static void EnsureDirectoryContainsOnlyIntentFiles(
        string root,
        string directoryPath,
        IReadOnlyList<FileFingerprint> expectedFiles,
        long maximumBytes
    ) {
        HashSet<string> expectedDirectoryPaths = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FileFingerprint> expectedByPath = expectedFiles.ToDictionary(
            file => file.RelativePath,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (FileFingerprint file in expectedFiles) {
            string relativePath = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            string? relativeDirectory = Path.GetDirectoryName(relativePath);
            while (!string.IsNullOrEmpty(relativeDirectory)) {
                expectedDirectoryPaths.Add(relativeDirectory.Replace('\\', '/'));
                relativeDirectory = Path.GetDirectoryName(relativeDirectory);
            }
        }

        long totalBytes = 0;
        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(directoryPath);
        while (pendingDirectories.Count > 0) {
            string currentDirectory = pendingDirectories.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(currentDirectory)) {
                ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) {
                    string relativeDirectory = Path.GetRelativePath(directoryPath, entry).Replace('\\', '/');
                    if (!expectedDirectoryPaths.Contains(relativeDirectory)) {
                        throw new InvalidDataException("Claude history preparation contains an unknown directory.");
                    }

                    pendingDirectories.Push(entry);
                    continue;
                }

                string relativePath = Path.GetRelativePath(directoryPath, entry).Replace('\\', '/');
                FileInfo fileInfo = new(entry);
                if (!expectedByPath.TryGetValue(relativePath, out FileFingerprint? expected)
                    || expected is null
                    || fileInfo.Length > expected.Length) {
                    throw new InvalidDataException("Claude history preparation contains a file outside its intent.");
                }

                totalBytes = checked(totalBytes + fileInfo.Length);
                if (totalBytes > maximumBytes) {
                    throw new InvalidDataException("Claude history preparation exceeded its recorded size limit.");
                }
            }
        }
    }

    private void CleanupPreparationTransactionDirectory(
        string root,
        string transactionPath,
        TransactionManifest manifest,
        string manifestPath,
        string cleanupStartedPath,
        WindowsClaudeHistoryCleanupFileSystem.FileSnapshot manifestSnapshot,
        Action ensureTargetState,
        CancellationToken cancellationToken
    ) {
        CleanupTransactionContents(
            root,
            transactionPath,
            manifest,
            manifestPath,
            cleanupStartedPath,
            manifestSnapshot,
            ensureTargetState,
            preparationOnly: true,
            cancellationToken
        );
    }

    private void CleanupTransactionContents(
        string root,
        string transactionPath,
        TransactionManifest manifest,
        string manifestPath,
        string cleanupStartedPath,
        WindowsClaudeHistoryCleanupFileSystem.FileSnapshot manifestSnapshot,
        Action? ensureTargetState,
        bool preparationOnly,
        CancellationToken cancellationToken
    ) {
        CleanupInventory inventory = CaptureCleanupInventory(
            root,
            transactionPath,
            manifest,
            manifestPath,
            manifestSnapshot,
            preparationOnly
        );
        ObserveCleanup("inventory-captured", transactionPath);
        ensureTargetState?.Invoke();
        EnsureCleanupTreeContainsOnlyOwnedEntries(inventory);

        foreach (CleanupItem item in inventory.RemainingEntries.Values
            .Where(item => item.Kind == CleanupItemKind.Payload)
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray()) {
            DeleteCleanupFile(inventory, item, CleanupDeleteKind.Payload, ensureTargetState, cancellationToken);
        }

        foreach (CleanupItem item in inventory.RemainingEntries.Values
            .Where(item => item.Kind == CleanupItemKind.Directory)
            .OrderByDescending(item => item.RelativePath.Count(character => character == '/'))
            .ThenByDescending(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray()) {
            DeleteCleanupDirectory(inventory, item, ensureTargetState, cancellationToken);
        }

        foreach (CleanupItem item in inventory.RemainingEntries.Values
            .Where(item => item.Kind == CleanupItemKind.Marker)
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray()) {
            DeleteCleanupFile(inventory, item, CleanupDeleteKind.Marker, ensureTargetState, cancellationToken);
        }

        CleanupItem? cleanupMarker = inventory.Find(GetRelativeCleanupPath(transactionPath, cleanupStartedPath));
        if (cleanupMarker is not null) {
            DeleteCleanupFile(inventory, cleanupMarker, CleanupDeleteKind.CleanupMarker, ensureTargetState, cancellationToken);
        }

        CleanupItem? manifestItem = inventory.Find(GetRelativeCleanupPath(transactionPath, manifestPath));
        if (manifestItem is null) {
            throw new IOException("Claude history transaction manifest disappeared before final cleanup.");
        }

        DeleteCleanupFile(inventory, manifestItem, CleanupDeleteKind.Manifest, ensureTargetState, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        DeleteEmptyTransactionDirectory(inventory, cancellationToken);
    }

    private CleanupInventory CaptureCleanupInventory(
        string root,
        string transactionPath,
        TransactionManifest manifest,
        string manifestPath,
        WindowsClaudeHistoryCleanupFileSystem.FileSnapshot manifestSnapshot,
        bool preparationOnly
    ) {
        string[] directoryNames = preparationOnly ? PreparationDirectoryNames : TransactionDirectoryNames;
        HashSet<string> expectedDirectories = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CleanupPayload> expectedPayloads = new(StringComparer.OrdinalIgnoreCase);
        foreach (string directoryName in directoryNames) {
            IReadOnlyList<FileFingerprint> expectedFiles;
            if (!manifest.TargetExisted
                && directoryName is (
                    "target-backup" or "target-backup.partial" or "target-displaced" or "target-rollback-staging"
                )) {
                continue;
            }

            if (preparationOnly) {
                expectedFiles = directoryName == "source-staging" ? manifest.SourceFiles : manifest.TargetFiles;
            } else {
                expectedFiles = GetExpectedCleanupInventory(directoryName, manifest);
            }

            string normalizedDirectoryName = directoryName.Replace('\\', '/');
            expectedDirectories.Add(normalizedDirectoryName);
            long maximumBytes = directoryName is "source-staging" or "target-rollback-displaced"
                ? MaximumSourceIndexBytes
                : MaximumTargetBackupBytes;
            long totalBytes = 0;
            foreach (FileFingerprint file in expectedFiles) {
                string relativeFilePath = file.RelativePath.Replace('\\', '/');
                string relativePath = normalizedDirectoryName + "/" + relativeFilePath;
                totalBytes = checked(totalBytes + file.Length);
                if (totalBytes > maximumBytes) {
                    throw new InvalidDataException("Claude history transaction manifest exceeds its recorded size limit.");
                }

                if (!expectedPayloads.TryAdd(relativePath, new CleanupPayload(file, maximumBytes))) {
                    throw new InvalidDataException("Claude history transaction inventory contains duplicate payload paths.");
                }

                string? parentPath = Path.GetDirectoryName(relativeFilePath.Replace('/', Path.DirectorySeparatorChar));
                while (!string.IsNullOrEmpty(parentPath)) {
                    string nestedDirectory = normalizedDirectoryName + "/" + parentPath.Replace('\\', '/');
                    if (expectedPayloads.ContainsKey(nestedDirectory)) {
                        throw new InvalidDataException("Claude history transaction inventory contains conflicting file and directory paths.");
                    }

                    expectedDirectories.Add(nestedDirectory);
                    parentPath = Path.GetDirectoryName(parentPath);
                }
            }
        }

        if (expectedPayloads.Keys.Any(expectedDirectories.Contains)) {
            throw new InvalidDataException("Claude history transaction inventory contains conflicting file and directory paths.");
        }

        Dictionary<string, CleanupItem> entries = new(StringComparer.OrdinalIgnoreCase);
        WindowsClaudeHistoryCleanupFileSystem.FileIdentity transactionIdentity =
            WindowsClaudeHistoryCleanupFileSystem.CaptureEntryIdentity(root, transactionPath, expectDirectory: true);
        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(transactionPath);
        while (pendingDirectories.Count > 0) {
            string currentDirectory = pendingDirectories.Pop();
            foreach (string entryPath in Directory.EnumerateFileSystemEntries(currentDirectory)) {
                string relativePath = GetRelativeCleanupPath(transactionPath, entryPath);
                FileAttributes attributes = File.GetAttributes(entryPath);
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                WindowsClaudeHistoryCleanupFileSystem.FileIdentity identity =
                    WindowsClaudeHistoryCleanupFileSystem.CaptureEntryIdentity(root, entryPath, isDirectory);

                if (isDirectory) {
                    if (!expectedDirectories.Contains(relativePath)) {
                        throw new InvalidDataException("Claude history transaction contains a directory outside its inventory.");
                    }

                    AddCleanupEntry(entries, new CleanupItem(relativePath, entryPath, CleanupItemKind.Directory, identity, null, 0));
                    pendingDirectories.Push(entryPath);
                    continue;
                }

                if (expectedPayloads.TryGetValue(relativePath, out CleanupPayload? payload)) {
                    WindowsClaudeHistoryCleanupFileSystem.FileSnapshot snapshot =
                        WindowsClaudeHistoryCleanupFileSystem.CaptureFileSnapshot(root, entryPath, payload.MaximumBytes);
                    if (snapshot.Identity != identity
                        || snapshot.Length != payload.Fingerprint.Length
                        || !string.Equals(snapshot.Sha256, payload.Fingerprint.Sha256, StringComparison.OrdinalIgnoreCase)) {
                        throw new InvalidDataException("Claude history transaction payload does not match its manifest inventory.");
                    }

                    AddCleanupEntry(
                        entries,
                        new CleanupItem(relativePath, entryPath, CleanupItemKind.Payload, identity, snapshot, payload.MaximumBytes)
                    );
                    continue;
                }

                if (relativePath.Contains('/')) {
                    throw new InvalidDataException("Claude history transaction contains a file outside its manifest inventory.");
                }

                if (string.Equals(entryPath, manifestPath, StringComparison.OrdinalIgnoreCase)) {
                    if (identity != manifestSnapshot.Identity) {
                        throw new InvalidDataException("Claude history transaction manifest changed after it was read.");
                    }

                    AddCleanupEntry(
                        entries,
                        new CleanupItem(relativePath, entryPath, CleanupItemKind.Manifest, identity, manifestSnapshot, MaximumManifestBytes)
                    );
                    continue;
                }

                string fileName = Path.GetFileName(entryPath);
                if (TryGetAtomicTemporaryFileBaseName(fileName, out string temporaryBaseName)) {
                    WindowsClaudeHistoryCleanupFileSystem.FileSnapshot snapshot =
                        CaptureAndValidateControlFile(root, entryPath, temporaryBaseName, manifest.TransactionId, manifest.DataRoot);
                    if (snapshot.Identity != identity) {
                        throw new InvalidDataException("Claude history transaction temporary file changed during inventory capture.");
                    }

                    AddCleanupEntry(entries, new CleanupItem(relativePath, entryPath, CleanupItemKind.Marker, identity, snapshot, MaximumManifestBytes));
                    continue;
                }

                if (GetTransactionMarkerContent(fileName) is null) {
                    throw new InvalidDataException("Claude history transaction contains a file outside its inventory.");
                }

                WindowsClaudeHistoryCleanupFileSystem.FileSnapshot markerSnapshot =
                    CaptureAndValidateControlFile(root, entryPath, fileName, manifest.TransactionId, manifest.DataRoot);
                if (markerSnapshot.Identity != identity) {
                    throw new InvalidDataException("Claude history transaction marker changed during inventory capture.");
                }

                CleanupItemKind kind = string.Equals(fileName, CleanupStartedFileName, StringComparison.OrdinalIgnoreCase)
                    ? CleanupItemKind.CleanupMarker
                    : CleanupItemKind.Marker;
                AddCleanupEntry(
                    entries,
                    new CleanupItem(relativePath, entryPath, kind, identity, markerSnapshot, 64)
                );
            }
        }

        return new CleanupInventory(root, transactionPath, transactionIdentity, entries);
    }

    private static WindowsClaudeHistoryCleanupFileSystem.FileSnapshot CaptureAndValidateControlFile(
        string root,
        string path,
        string fileNameOrBaseName,
        Guid transactionId,
        string dataRoot
    ) {
        int maximumBytes = string.Equals(fileNameOrBaseName, ManifestFileName, StringComparison.OrdinalIgnoreCase)
            ? MaximumManifestBytes
            : 64;
        using WindowsClaudeHistoryCleanupFileSystem.FileContentsSnapshot contents =
            WindowsClaudeHistoryCleanupFileSystem.ReadFileContentsSnapshot(root, path, maximumBytes);
        if (string.Equals(fileNameOrBaseName, ManifestFileName, StringComparison.OrdinalIgnoreCase)) {
            TransactionManifest temporaryManifest = DeserializeManifest(contents.Content);
            if (temporaryManifest.TransactionId != transactionId
                || !string.Equals(temporaryManifest.DataRoot, dataRoot, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException("Claude history manifest temporary file belongs to another transaction.");
            }
        } else {
            string? expectedContent = GetTransactionMarkerContent(fileNameOrBaseName);
            if (expectedContent is null
                || !contents.Content.AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(expectedContent))) {
                throw new InvalidDataException("Claude history transaction marker contents are invalid.");
            }
        }

        return contents.Snapshot;
    }

    private static string? GetTransactionMarkerContent(string fileName) {
        if (string.Equals(fileName, "history-apply-started", StringComparison.OrdinalIgnoreCase)) {
            return "started";
        }

        if (string.Equals(fileName, "history-rollback-completed", StringComparison.OrdinalIgnoreCase)) {
            return "restored";
        }

        return string.Equals(fileName, CleanupStartedFileName, StringComparison.OrdinalIgnoreCase)
            ? "cleanup-started"
            : null;
    }

    private static void AddCleanupEntry(Dictionary<string, CleanupItem> entries, CleanupItem item) {
        if (!entries.TryAdd(item.RelativePath, item)) {
            throw new InvalidDataException("Claude history transaction contains duplicate paths.");
        }
    }

    private void DeleteCleanupFile(
        CleanupInventory inventory,
        CleanupItem item,
        CleanupDeleteKind deleteKind,
        Action? ensureTargetState,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        string beforeEvent = deleteKind switch {
            CleanupDeleteKind.Payload => "before-payload-delete",
            CleanupDeleteKind.Marker => "before-marker-delete",
            CleanupDeleteKind.CleanupMarker => "before-cleanup-marker-delete",
            CleanupDeleteKind.Manifest => "before-manifest-delete",
            _ => throw new ArgumentOutOfRangeException(nameof(deleteKind))
        };
        string afterEvent = deleteKind switch {
            CleanupDeleteKind.Payload => "after-payload-delete",
            CleanupDeleteKind.Marker => "after-marker-delete",
            CleanupDeleteKind.CleanupMarker => "after-cleanup-marker-delete",
            CleanupDeleteKind.Manifest => "after-manifest-delete",
            _ => throw new ArgumentOutOfRangeException(nameof(deleteKind))
        };
        ensureTargetState?.Invoke();
        ObserveCleanup(beforeEvent, item.Path);
        ensureTargetState?.Invoke();
        EnsureCleanupTreeContainsOnlyOwnedEntries(inventory);
        if (item.Snapshot is null) {
            throw new InvalidDataException("Claude history transaction file inventory is incomplete.");
        }

        WindowsClaudeHistoryCleanupFileSystem.DeleteFileIfUnchanged(
            inventory.Root,
            item.Path,
            item.Snapshot.Value,
            item.MaximumBytes
        );
        inventory.Remove(item.RelativePath);
        ObserveCleanup(afterEvent, item.Path);
        if (deleteKind != CleanupDeleteKind.Manifest) {
            ensureTargetState?.Invoke();
        }

        EnsureCleanupTreeContainsOnlyOwnedEntries(inventory);
    }

    private void DeleteCleanupDirectory(
        CleanupInventory inventory,
        CleanupItem item,
        Action? ensureTargetState,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        ensureTargetState?.Invoke();
        ObserveCleanup("before-directory-delete", item.Path);
        ensureTargetState?.Invoke();
        EnsureCleanupTreeContainsOnlyOwnedEntries(inventory);
        WindowsClaudeHistoryCleanupFileSystem.DeleteDirectoryIfEmpty(inventory.Root, item.Path, item.Identity);
        inventory.Remove(item.RelativePath);
        ObserveCleanup("after-directory-delete", item.Path);
        ensureTargetState?.Invoke();
        EnsureCleanupTreeContainsOnlyOwnedEntries(inventory);
    }

    private void DeleteEmptyTransactionDirectory(CleanupInventory inventory, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        ObserveCleanup("before-transaction-directory-delete", inventory.TransactionPath);
        EnsureCleanupTreeContainsOnlyOwnedEntries(inventory);
        WindowsClaudeHistoryCleanupFileSystem.DeleteDirectoryIfEmpty(
            inventory.Root,
            inventory.TransactionPath,
            inventory.TransactionIdentity
        );
        ObserveCleanup("after-transaction-directory-delete", inventory.TransactionPath);
        EnsureCleanupTreeContainsOnlyOwnedEntries(inventory, allowMissingTransactionDirectory: true);
    }

    private void EnsureCleanupTreeContainsOnlyOwnedEntries(
        CleanupInventory inventory,
        bool allowMissingTransactionDirectory = false
    ) {
        FileAttributes transactionAttributes;
        try {
            transactionAttributes = File.GetAttributes(inventory.TransactionPath);
        } catch (FileNotFoundException) when (allowMissingTransactionDirectory) {
            return;
        } catch (DirectoryNotFoundException) when (allowMissingTransactionDirectory) {
            return;
        } catch (FileNotFoundException failure) {
            throw new IOException("Claude history transaction directory disappeared during cleanup.", failure);
        } catch (DirectoryNotFoundException failure) {
            throw new IOException("Claude history transaction directory disappeared during cleanup.", failure);
        }

        if ((transactionAttributes & FileAttributes.Directory) == 0) {
            throw new InvalidDataException("Claude history transaction path was replaced by a file.");
        }

        WindowsClaudeHistoryCleanupFileSystem.FileIdentity actualTransactionIdentity =
            WindowsClaudeHistoryCleanupFileSystem.CaptureEntryIdentity(
                inventory.Root,
                inventory.TransactionPath,
                expectDirectory: true
            );
        if (actualTransactionIdentity != inventory.TransactionIdentity) {
            throw new InvalidDataException("Claude history transaction directory identity changed during cleanup.");
        }

        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(inventory.TransactionPath);
        while (pendingDirectories.Count > 0) {
            string currentDirectory = pendingDirectories.Pop();
            foreach (string entryPath in Directory.EnumerateFileSystemEntries(currentDirectory)) {
                string relativePath = GetRelativeCleanupPath(inventory.TransactionPath, entryPath);
                bool isDirectory = (File.GetAttributes(entryPath) & FileAttributes.Directory) != 0;
                if (!inventory.TryGet(relativePath, out CleanupItem? expected)
                    || expected is null
                    || (expected.Kind == CleanupItemKind.Directory) != isDirectory) {
                    throw new InvalidDataException("Claude history transaction contains a new or unlisted entry during cleanup.");
                }

                WindowsClaudeHistoryCleanupFileSystem.FileIdentity actualIdentity =
                    WindowsClaudeHistoryCleanupFileSystem.CaptureEntryIdentity(inventory.Root, entryPath, isDirectory);
                if (actualIdentity != expected.Identity) {
                    throw new InvalidDataException("Claude history transaction entry identity changed during cleanup.");
                }

                if (isDirectory) {
                    pendingDirectories.Push(entryPath);
                }
            }
        }
    }

    private void ObserveCleanup(string operation, string path) {
        cleanupObserver?.Invoke(operation, path);
    }

    private static string GetRelativeCleanupPath(string transactionPath, string path) {
        return Path.GetRelativePath(transactionPath, path).Replace('\\', '/');
    }

    private static IReadOnlyList<FileFingerprint> GetExpectedCleanupInventory(
        string directoryName,
        TransactionManifest manifest
    ) {
        if (!manifest.TargetExisted
            && directoryName is (
                "target-backup" or "target-backup.partial" or "target-displaced" or "target-rollback-staging"
            )) {
            throw new InvalidDataException("Claude history transaction contains an unexpected target backup.");
        }

        return directoryName switch {
            "live-files" => [],
            "source-staging" or "target-rollback-displaced" => manifest.SourceFiles,
            "target-backup" or "target-backup.partial" or "target-displaced" or "target-rollback-staging" =>
                manifest.TargetFiles,
            _ => throw new InvalidDataException("Claude history transaction directory is not recognized.")
        };
    }

    private static void EnsureDirectoryInventoryIsSubset(
        string root,
        string directoryPath,
        IReadOnlyList<FileFingerprint> expectedFiles,
        long maximumBytes
    ) {
        HashSet<string> expectedDirectoryPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (FileFingerprint file in expectedFiles) {
            string relativePath = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            string? relativeDirectory = Path.GetDirectoryName(relativePath);
            while (!string.IsNullOrEmpty(relativeDirectory)) {
                expectedDirectoryPaths.Add(relativeDirectory.Replace('\\', '/'));
                relativeDirectory = Path.GetDirectoryName(relativeDirectory);
            }
        }

        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(directoryPath);
        while (pendingDirectories.Count > 0) {
            string currentDirectory = pendingDirectories.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(currentDirectory)) {
                ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) == 0) {
                    continue;
                }

                string relativePath = Path.GetRelativePath(directoryPath, entry).Replace('\\', '/');
                if (!expectedDirectoryPaths.Contains(relativePath)) {
                    throw new InvalidDataException(
                        "Claude history transaction contains a directory outside its recorded inventory."
                    );
                }

                pendingDirectories.Push(entry);
            }
        }

        Dictionary<string, FileFingerprint> expectedByPath = expectedFiles.ToDictionary(
            file => file.RelativePath,
            StringComparer.OrdinalIgnoreCase
        );
        IReadOnlyList<FileFingerprint> actualFiles = CaptureDirectoryInventory(directoryPath, maximumBytes);
        foreach (FileFingerprint actual in actualFiles) {
            if (!expectedByPath.TryGetValue(actual.RelativePath, out FileFingerprint? expected)
                || expected is null
                || expected.Length != actual.Length
                || !string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException(
                    "Claude history transaction contains data outside its recorded inventory."
                );
            }
        }
    }

    private void CleanupManifestlessTransactionDirectory(
        string root,
        string transactionPath,
        ClaudeSessionHistoryCleanupIntent cleanupIntent,
        CancellationToken cancellationToken
    ) {
        WindowsClaudeHistoryCleanupFileSystem.FileIdentity transactionIdentity =
            WindowsClaudeHistoryCleanupFileSystem.CaptureEntryIdentity(root, transactionPath, expectDirectory: true);
        Dictionary<string, CleanupItem> entries = new(StringComparer.OrdinalIgnoreCase);
        string[] paths = Directory.EnumerateFileSystemEntries(transactionPath).ToArray();
        if (paths.Length == 0) {
            CleanupInventory emptyInventory = new(root, transactionPath, transactionIdentity, entries);
            ObserveCleanup("inventory-captured", transactionPath);
            EnsureCleanupTreeContainsOnlyOwnedEntries(emptyInventory);
            DeleteEmptyTransactionDirectory(emptyInventory, cancellationToken);
            return;
        }

        bool markerOnly = paths.Length == 1
            && string.Equals(Path.GetFileName(paths[0]), CleanupStartedFileName, StringComparison.OrdinalIgnoreCase)
            && (File.GetAttributes(paths[0]) & FileAttributes.Directory) == 0;
        if (markerOnly) {
            string markerPath = paths[0];
            using WindowsClaudeHistoryCleanupFileSystem.FileContentsSnapshot markerContents =
                WindowsClaudeHistoryCleanupFileSystem.ReadFileContentsSnapshot(root, markerPath, 64);
            if (!markerContents.Content.AsSpan().SequenceEqual("cleanup-started"u8)) {
                throw new InvalidDataException("Claude manifestless cleanup marker is invalid.");
            }

            string relativePath = GetRelativeCleanupPath(transactionPath, markerPath);
            AddCleanupEntry(
                entries,
                new CleanupItem(
                    relativePath,
                    markerPath,
                    CleanupItemKind.CleanupMarker,
                    markerContents.Snapshot.Identity,
                    markerContents.Snapshot,
                    64
                )
            );
            CleanupInventory markerInventory = new(root, transactionPath, transactionIdentity, entries);
            ObserveCleanup("inventory-captured", transactionPath);
            DeleteCleanupFile(
                markerInventory,
                markerInventory.Find(relativePath)!,
                CleanupDeleteKind.CleanupMarker,
                null,
                cancellationToken
            );
            DeleteEmptyTransactionDirectory(markerInventory, cancellationToken);
            return;
        }

        if (cleanupIntent != ClaudeSessionHistoryCleanupIntent.Preparation) {
            throw new InvalidDataException(
                "Claude committed history transaction is missing its manifest or contains only a preparation temporary file."
            );
        }

        foreach (string path in paths) {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0
                || !TryGetAtomicTemporaryFileBaseName(fileName, out string baseName)
                || !string.Equals(baseName, ManifestFileName, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException(
                    "Claude manifestless history transaction contains data outside its preparation intent."
                );
            }

            WindowsClaudeHistoryCleanupFileSystem.FileSnapshot snapshot =
                WindowsClaudeHistoryCleanupFileSystem.CaptureFileSnapshot(root, path, MaximumManifestBytes);
            string relativePath = GetRelativeCleanupPath(transactionPath, path);
            AddCleanupEntry(
                entries,
                new CleanupItem(relativePath, path, CleanupItemKind.Marker, snapshot.Identity, snapshot, MaximumManifestBytes)
            );
        }

        CleanupInventory intentInventory = new(root, transactionPath, transactionIdentity, entries);
        ObserveCleanup("inventory-captured", transactionPath);
        foreach (CleanupItem item in intentInventory.RemainingEntries.Values.ToArray()) {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCleanupFile(intentInventory, item, CleanupDeleteKind.Marker, null, cancellationToken);
        }

        DeleteEmptyTransactionDirectory(intentInventory, cancellationToken);
    }

    private static void EnsureKnownTransactionEntries(string transactionPath) {
        HashSet<string> directoryNames = new(TransactionDirectoryNames, StringComparer.OrdinalIgnoreCase);
        HashSet<string> fileNames = new(TransactionMarkerFileNames, StringComparer.OrdinalIgnoreCase) {
            ManifestFileName
        };
        foreach (string entry in Directory.EnumerateFileSystemEntries(transactionPath)) {
            string name = Path.GetFileName(entry);
            if (directoryNames.Contains(name)) {
                ValidateTransactionEntryType(transactionPath, entry, expectDirectory: true);
            } else if (fileNames.Contains(name)) {
                ValidateTransactionEntryType(transactionPath, entry, expectDirectory: false);
            } else if (TryGetAtomicTemporaryFileBaseName(name, out string baseName)
                && fileNames.Contains(baseName)) {
                ValidateTransactionEntryType(transactionPath, entry, expectDirectory: false);
            } else {
                throw new InvalidDataException("Claude history transaction contains an unknown entry.");
            }
        }
    }

    private static void ValidateTransactionEntryType(string root, string path, bool expectDirectory) {
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, path);
        FileAttributes attributes = File.GetAttributes(path);
        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory != expectDirectory) {
            throw new InvalidDataException("Claude history transaction entry has an unexpected type.");
        }
    }

    private static void ValidateCleanupStartedMarker(string path) {
        byte[] content = ClaudeWindowsPathSafety.ReadBoundedFile(path, 64);
        if (!content.AsSpan().SequenceEqual("cleanup-started"u8)) {
            throw new InvalidDataException("Claude history cleanup marker is invalid.");
        }
    }

    private static bool TryGetAtomicTemporaryFileBaseName(string fileName, out string baseName) {
        baseName = "";
        if (fileName.Length == 0
            || fileName[0] != '.'
            || !fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        int separator = fileName.LastIndexOf('.');
        int guidSeparator = separator - 33;
        if (guidSeparator <= 0
            || fileName[guidSeparator] != '.'
            || !Guid.TryParseExact(fileName.AsSpan(guidSeparator + 1, 32), "N", out _)) {
            return false;
        }

        baseName = fileName[1..guidSeparator];
        return string.Equals(fileName[separator..], ".tmp", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetManifestPath(string transactionPath) => Path.Combine(transactionPath, ManifestFileName);

    private static string GetCleanupStartedPath(string transactionPath) => Path.Combine(transactionPath, CleanupStartedFileName);

    private IndexSnapshot ReadIndexSnapshot(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid accountId,
        Guid organizationId,
        CancellationToken cancellationToken
    ) {
        string root = ValidateInstallation(installation);
        string indexPath = GetIndexPath(root, accountId, organizationId);
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, indexPath);
        if (!Directory.Exists(indexPath)) {
            if (File.Exists(indexPath)) {
                throw new HistoryFailure(IClaudeSessionHistoryStore.ValidationStatus.InvalidIndex, "index-path-is-file");
            }

            throw new HistoryFailure(IClaudeSessionHistoryStore.ValidationStatus.MissingDirectory, "source-directory-missing");
        }

        string[] entries;
        try {
            entries = Directory.EnumerateFileSystemEntries(indexPath)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        } catch (UnauthorizedAccessException) {
            throw new HistoryFailure(IClaudeSessionHistoryStore.ValidationStatus.Unreadable, "source-directory-unreadable");
        } catch (IOException) {
            throw new HistoryFailure(IClaudeSessionHistoryStore.ValidationStatus.Unreadable, "source-directory-unreadable");
        }

        List<SourceEntry> sourceEntries = [];
        List<FileFingerprint> fingerprints = [];
        List<FileFingerprint> allFiles = [];
        int sessionCount = 0;
        long totalBytes = 0;
        try {
            foreach (string path in entries) {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) {
                    throw new HistoryFailure(IClaudeSessionHistoryStore.ValidationStatus.InvalidIndex, "source-index-entry-unsupported");
                }
                if ((attributes & FileAttributes.Directory) != 0) {
                    continue;
                }

                if (allFiles.Count >= MaximumIndexFiles) {
                    throw new HistoryFailure(IClaudeSessionHistoryStore.ValidationStatus.InvalidIndex, "source-index-too-large");
                }

                byte[] content = ClaudeWindowsPathSafety.ReadBoundedFile(path, MaximumIndexFileBytes);
                totalBytes = checked(totalBytes + content.Length);
                if (totalBytes > MaximumSourceIndexBytes) {
                    CryptographicOperations.ZeroMemory(content);
                    throw new HistoryFailure(IClaudeSessionHistoryStore.ValidationStatus.InvalidIndex, "source-index-too-large");
                }

                string fileName = Path.GetFileName(path);
                FileFingerprint fingerprint = new(fileName, content.Length, ClaudeWindowsPathSafety.ComputeSha256(content));
                allFiles.Add(fingerprint);
                if (IsSessionFile(fileName)) {
                    try {
                        string? cliSessionId = ParseCliSessionId(content);
                        if (cliSessionId is null) {
                            throw new HistoryFailure(IClaudeSessionHistoryStore.ValidationStatus.InvalidIndex, "source-index-session-id-invalid");
                        }
                        ValidateTranscript(cliSessionId, cancellationToken);
                        sessionCount++;
                    } catch (HistoryFailure failure) when (failure.FailureCode is "source-index-json-invalid"
                        or "source-index-session-id-invalid" or "transcript-missing" or "transcript-not-unique") {
                        CryptographicOperations.ZeroMemory(content);
                        continue;
                    }
                }
                sourceEntries.Add(new SourceEntry(fileName, content));
                fingerprints.Add(fingerprint);
            }

            return new IndexSnapshot(indexPath, sourceEntries, fingerprints, allFiles, sessionCount);
        } catch {
            foreach (SourceEntry entry in sourceEntries) {
                CryptographicOperations.ZeroMemory(entry.Content);
            }

            throw;
        }
    }

    private void ValidateTranscript(string cliSessionId, CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(userProfilePath)) {
            throw new HistoryFailure(
                IClaudeSessionHistoryStore.ValidationStatus.InvalidTranscriptReference,
                "transcript-root-unavailable"
            );
        }

        string projectsRoot = Path.Combine(userProfilePath, ".claude", "projects");
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(userProfilePath, projectsRoot);
        if (!Directory.Exists(projectsRoot)) {
            throw new HistoryFailure(
                IClaudeSessionHistoryStore.ValidationStatus.InvalidTranscriptReference,
                "transcript-root-missing"
            );
        }

        string[] projectDirectories;
        try {
            projectDirectories = Directory.EnumerateDirectories(projectsRoot).ToArray();
        } catch (UnauthorizedAccessException) {
            throw new HistoryFailure(
                IClaudeSessionHistoryStore.ValidationStatus.Unreadable,
                "transcript-root-unreadable"
            );
        } catch (IOException) {
            throw new HistoryFailure(
                IClaudeSessionHistoryStore.ValidationStatus.Unreadable,
                "transcript-root-unreadable"
            );
        }

        string expectedFileName = cliSessionId + ".jsonl";
        List<string> matches = [];
        foreach (string projectDirectory in projectDirectories) {
            cancellationToken.ThrowIfCancellationRequested();
            ClaudeWindowsPathSafety.EnsureNoReparsePoints(userProfilePath, projectDirectory);
            string candidatePath = Path.Combine(projectDirectory, expectedFileName);
            if (File.Exists(candidatePath)) {
                ClaudeWindowsPathSafety.EnsureNoReparsePoints(userProfilePath, candidatePath);
                matches.Add(candidatePath);
            }
        }

        if (matches.Count != 1) {
            throw new HistoryFailure(
                IClaudeSessionHistoryStore.ValidationStatus.InvalidTranscriptReference,
                matches.Count == 0 ? "transcript-missing" : "transcript-not-unique"
            );
        }

        try {
            using FileStream stream = new(
                matches[0],
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                1,
                FileOptions.SequentialScan
            );
            _ = stream.ReadByte();
        } catch (UnauthorizedAccessException) {
            throw new HistoryFailure(
                IClaudeSessionHistoryStore.ValidationStatus.InvalidTranscriptReference,
                "transcript-unreadable"
            );
        } catch (IOException) {
            throw new HistoryFailure(
                IClaudeSessionHistoryStore.ValidationStatus.InvalidTranscriptReference,
                "transcript-unreadable"
            );
        }
    }

    private static string? ParseCliSessionId(byte[] content) {
        try {
            using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("cliSessionId", out JsonElement sessionIdElement)
                || sessionIdElement.ValueKind != JsonValueKind.String) {
                return null;
            }

            string? sessionIdText = sessionIdElement.GetString();
            if (sessionIdText is null
                || !Guid.TryParse(sessionIdText, out Guid sessionId)
                || sessionId == Guid.Empty) {
                return null;
            }

            return sessionId.ToString("D");
        } catch (JsonException failure) {
            throw new HistoryFailure(
                IClaudeSessionHistoryStore.ValidationStatus.InvalidIndex,
                "source-index-json-invalid",
                failure
            );
        }
    }

    private static TargetSnapshot CaptureTargetSnapshot(string root, string targetPath) {
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, targetPath);
        string organizationDirectoryPath = Path.GetDirectoryName(targetPath)!;
        string accountDirectoryPath = Path.GetDirectoryName(organizationDirectoryPath)!;
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, organizationDirectoryPath);
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, accountDirectoryPath);
        if (File.Exists(organizationDirectoryPath) || File.Exists(accountDirectoryPath)) {
            throw new HistoryFailure(IClaudeSessionHistoryStore.ValidationStatus.InvalidIndex, "target-parent-path-is-file");
        }

        bool accountDirectoryExisted = Directory.Exists(accountDirectoryPath);
        bool organizationDirectoryExisted = Directory.Exists(organizationDirectoryPath);
        if (File.Exists(targetPath)) {
            throw new HistoryFailure(IClaudeSessionHistoryStore.ValidationStatus.InvalidIndex, "target-path-is-file");
        }

        if (!Directory.Exists(targetPath)) {
            return new TargetSnapshot(false, accountDirectoryExisted, organizationDirectoryExisted, []);
        }

        try {
            IReadOnlyList<FileFingerprint> files = CaptureDirectoryInventory(targetPath, MaximumTargetBackupBytes, true);
            return new TargetSnapshot(true, accountDirectoryExisted, organizationDirectoryExisted, files);
        } catch (UnauthorizedAccessException) {
            throw new HistoryFailure(IClaudeSessionHistoryStore.ValidationStatus.Unreadable, "target-backup-unreadable");
        } catch (IOException) {
            throw new HistoryFailure(IClaudeSessionHistoryStore.ValidationStatus.Unreadable, "target-backup-unreadable");
        }
    }

    private static bool IsSessionFile(string name) {
        return name.Length == 47
            && name.StartsWith("local_", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(name.AsSpan(6, 36), "D", out _);
    }

    private static void CopyTargetIndexContents(string source, string destination) {
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(source, source);
        if (Directory.Exists(destination) || File.Exists(destination)) {
            throw new IOException("A Claude staging destination already exists.");
        }

        Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source)) {
            ClaudeWindowsPathSafety.EnsureNoReparsePoints(source, entry);
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) == 0) {
                File.Copy(entry, Path.Combine(destination, Path.GetFileName(entry)));
            }
        }
    }

    private static IReadOnlyList<FileFingerprint> CaptureDirectoryInventory(
        string directoryPath, long maximumBytes, bool firstLevelOnly = false
    ) {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, root);
        if (!Directory.Exists(root)) {
            throw new DirectoryNotFoundException("A Claude history directory is missing.");
        }

        List<string> files = [];
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0) {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) {
                ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, entry);
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0) {
                    if (!firstLevelOnly) {
                        pending.Push(entry);
                    }
                } else {
                    files.Add(entry);
                }
            }
        }
        if (files.Count > MaximumIndexFiles) {
            throw new InvalidDataException("A Claude history directory has too many files.");
        }

        List<FileFingerprint> fingerprints = [];
        long totalBytes = 0;
        foreach (string filePath in files) {
            string relativePath = Path.GetRelativePath(root, filePath).Replace('\\', '/');
            if (!IsSafeRelativeFilePath(relativePath)) {
                throw new InvalidDataException("A Claude history file has an unsafe relative path.");
            }

            FileAttributes attributes = File.GetAttributes(filePath);
            if ((attributes & FileAttributes.ReparsePoint) != 0) {
                throw new InvalidDataException("A Claude history directory contains a reparse point.");
            }

            FileFingerprint fingerprint = HashFile(filePath, relativePath);
            totalBytes = checked(totalBytes + fingerprint.Length);
            if (totalBytes > maximumBytes) {
                throw new InvalidDataException("A Claude history directory exceeded its supported size limit.");
            }

            fingerprints.Add(fingerprint);
        }

        return fingerprints.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static FileFingerprint HashFile(string path, string relativePath) {
        FileInfo fileInfoBefore = new(path);
        long lengthBefore = fileInfoBefore.Length;
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] digest = SHA256.HashData(stream);
        FileInfo fileInfoAfter = new(path);
        if (lengthBefore != fileInfoAfter.Length || lengthBefore != stream.Length) {
            CryptographicOperations.ZeroMemory(digest);
            throw new IOException("Claude history changed while its file inventory was being read.");
        }

        string sha256 = Convert.ToHexString(digest).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(digest);
        return new FileFingerprint(relativePath, lengthBefore, sha256);
    }

    private static void WriteSourceStaging(
        IndexSnapshot source,
        string stagingPath,
        CancellationToken cancellationToken,
        Action? afterPayloadWritten
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(stagingPath);
        foreach (SourceEntry entry in source.Entries) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSafeLeafFileName(entry.FileName)) {
                throw new InvalidDataException("A Claude source index contains an unsafe file name.");
            }

            WriteNewFileDurably(Path.Combine(stagingPath, entry.FileName), entry.Content);
            afterPayloadWritten?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void WriteManifest(string transactionPath, TransactionManifest manifest) {
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (content.Length > MaximumManifestBytes) {
            CryptographicOperations.ZeroMemory(content);
            throw new InvalidDataException("Claude history transaction manifest is too large.");
        }

        try {
            ClaudeWindowsPathSafety.WriteFileAtomically(GetManifestPath(transactionPath), content);
        } finally {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static TransactionManifest ReadManifest(string transactionPath) {
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(transactionPath, transactionPath);
        string manifestPath = GetManifestPath(transactionPath);
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(transactionPath, manifestPath);
        byte[] content = ClaudeWindowsPathSafety.ReadBoundedFile(manifestPath, MaximumManifestBytes);
        try {
            return DeserializeManifest(content);
        } finally {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static TransactionManifest DeserializeManifest(ReadOnlySpan<byte> content) {
        TransactionManifest? manifest;
        try {
            manifest = JsonSerializer.Deserialize<TransactionManifest>(content, JsonOptions);
        } catch (JsonException failure) {
            throw new InvalidDataException("Claude history transaction manifest is malformed.", failure);
        }

        if (manifest is null || !IsValidManifest(manifest)) {
            throw new InvalidDataException("Claude history transaction manifest is invalid.");
        }

        return manifest;
    }

    private static bool IsValidManifest(TransactionManifest manifest) {
        if (manifest.SchemaVersion is not 1 and not 2
            || manifest.TransactionId == Guid.Empty
            || string.IsNullOrWhiteSpace(manifest.DataRoot)
            || !Guid.TryParseExact(manifest.SourceAccountUuid, "D", out Guid sourceAccount)
            || sourceAccount == Guid.Empty
            || !Guid.TryParseExact(manifest.SourceOrganizationUuid, "D", out Guid sourceOrganization)
            || sourceOrganization == Guid.Empty
            || !Guid.TryParseExact(manifest.TargetAccountUuid, "D", out Guid targetAccount)
            || targetAccount == Guid.Empty
            || !Guid.TryParseExact(manifest.TargetOrganizationUuid, "D", out Guid targetOrganization)
            || targetOrganization == Guid.Empty
            || (sourceAccount == targetAccount && sourceOrganization == targetOrganization)
            || (manifest.OrganizationDirectoryExisted && !manifest.AccountDirectoryExisted)
            || (manifest.TargetExisted && (!manifest.AccountDirectoryExisted || !manifest.OrganizationDirectoryExisted))
            || manifest.SourceSessionCount < 0
            || manifest.TargetSessionCount < 0
            || manifest.SourceFiles is null
            || manifest.TargetFiles is null
            || manifest.SourceFiles.Count > MaximumIndexFiles
            || manifest.TargetFiles.Count > MaximumIndexFiles
            || (!manifest.TargetExisted && manifest.TargetFiles.Count != 0)
            || (manifest.SchemaVersion == 1
                && (manifest.TargetIndexPath is not null
                    || manifest.PreparationState is not null
                    || manifest.OwnedDirectoryNames is not null))
            || (manifest.SchemaVersion == 2 && !IsValidPreparationIntentMetadata(manifest))) {
            return false;
        }

        return IsValidInventory(manifest.SourceFiles, requireLeafNames: true)
            && IsValidInventory(manifest.TargetFiles, requireLeafNames: false)
            && manifest.SourceSessionCount == manifest.SourceFiles.Count(file => IsSessionFile(file.RelativePath))
            && manifest.TargetSessionCount <= manifest.TargetFiles.Count(file => IsSessionFile(file.RelativePath));
    }

    private static bool IsValidPreparationIntentMetadata(TransactionManifest manifest) {
        if (manifest.PreparationState is not PreparationState.Preparing and not PreparationState.Prepared
            || string.IsNullOrWhiteSpace(manifest.TargetIndexPath)
            || !Path.IsPathFullyQualified(manifest.TargetIndexPath)
            || manifest.OwnedDirectoryNames is null
            || manifest.OwnedDirectoryNames.Count != TransactionDirectoryNames.Length) {
            return false;
        }

        HashSet<string> ownedDirectoryNames = new(manifest.OwnedDirectoryNames, StringComparer.OrdinalIgnoreCase);
        return ownedDirectoryNames.Count == TransactionDirectoryNames.Length
            && TransactionDirectoryNames.All(ownedDirectoryNames.Contains);
    }

    private static bool IsValidInventory(IReadOnlyList<FileFingerprint> files, bool requireLeafNames) {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (FileFingerprint? file in files) {
            if (file is null
                || string.IsNullOrWhiteSpace(file.RelativePath)
                || (requireLeafNames && !IsSafeLeafFileName(file.RelativePath))
                || (!requireLeafNames && !IsSafeRelativeFilePath(file.RelativePath))
                || file.Length < 0
                || !IsSha256(file.Sha256)
                || !names.Add(file.RelativePath)) {
                return false;
            }
        }

        return true;
    }

    private static bool InventoriesEqual(
        IReadOnlyList<FileFingerprint> expected,
        IReadOnlyList<FileFingerprint> actual
    ) {
        if (expected.Count != actual.Count) {
            return false;
        }

        Dictionary<string, FileFingerprint> actualFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (FileFingerprint file in actual) {
            if (!actualFiles.TryAdd(file.RelativePath, file)) {
                return false;
            }
        }

        foreach (FileFingerprint file in expected) {
            if (!actualFiles.TryGetValue(file.RelativePath, out FileFingerprint? current)
                || file.Length != current.Length
                || !string.Equals(file.Sha256, current.Sha256, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        return true;
    }

    private static bool IsManifestFor(
        TransactionManifest manifest,
        string root,
        Guid transactionId,
        Guid sourceAccount,
        Guid sourceOrganization,
        Guid targetAccount,
        Guid targetOrganization
    ) {
        return manifest.TransactionId == transactionId
            && string.Equals(manifest.DataRoot, root, StringComparison.OrdinalIgnoreCase)
            && string.Equals(manifest.SourceAccountUuid, sourceAccount.ToString("D"), StringComparison.OrdinalIgnoreCase)
            && string.Equals(manifest.SourceOrganizationUuid, sourceOrganization.ToString("D"), StringComparison.OrdinalIgnoreCase)
            && string.Equals(manifest.TargetAccountUuid, targetAccount.ToString("D"), StringComparison.OrdinalIgnoreCase)
            && string.Equals(manifest.TargetOrganizationUuid, targetOrganization.ToString("D"), StringComparison.OrdinalIgnoreCase)
            && ManifestTargetPathMatches(
                manifest,
                root,
                GetIndexPath(root, targetAccount, targetOrganization)
            );
    }

    private static void EnsureManifestTargets(
        TransactionManifest manifest,
        string root,
        Guid transactionId,
        Guid targetAccount,
        Guid targetOrganization
    ) {
        if (manifest.TransactionId != transactionId
            || !string.Equals(manifest.DataRoot, root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.TargetAccountUuid, targetAccount.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.TargetOrganizationUuid, targetOrganization.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || !ManifestTargetPathMatches(
                manifest,
                root,
                GetIndexPath(root, targetAccount, targetOrganization)
            )) {
            throw new InvalidDataException("Claude history transaction does not match the requested target.");
        }
    }

    private static void EnsureManifestTargetPath(TransactionManifest manifest, string root, string targetPath) {
        if (!ManifestTargetPathMatches(manifest, root, targetPath)) {
            throw new InvalidDataException("Claude history transaction target path does not match its preparation intent.");
        }
    }

    private static bool ManifestTargetPathMatches(TransactionManifest manifest, string root, string targetPath) {
        if (manifest.SchemaVersion == 1) {
            return true;
        }

        if (manifest.TargetIndexPath is null) {
            return false;
        }

        try {
            string recordedTargetPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.TargetIndexPath));
            string expectedTargetPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
            ClaudeWindowsPathSafety.EnsureContained(root, recordedTargetPath);
            return string.Equals(recordedTargetPath, expectedTargetPath, StringComparison.OrdinalIgnoreCase);
        } catch (ArgumentException) {
            return false;
        } catch (NotSupportedException) {
            return false;
        }
    }

    private static void EnsurePreparedManifest(TransactionManifest manifest) {
        if (manifest.SchemaVersion == 2 && manifest.PreparationState != PreparationState.Prepared) {
            throw new InvalidDataException("Claude history transaction preparation has not completed.");
        }
    }

    private static string ValidateInstallation(IClaudeDesktopInstallationLocator.Installation installation) {
        if (string.IsNullOrWhiteSpace(installation.DataRoot)
            || string.IsNullOrWhiteSpace(installation.DataRootFingerprint)) {
            throw new ArgumentException("Claude Desktop installation data root is invalid.", nameof(installation));
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.DataRoot));
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, root);
        if (!string.Equals(
            ClaudeWindowsPathSafety.ComputePathFingerprint(root),
            installation.DataRootFingerprint,
            StringComparison.OrdinalIgnoreCase
        )) {
            throw new InvalidOperationException("Claude Desktop data root fingerprint changed.");
        }

        return root;
    }

    private static string GetSessionsRoot(string root) {
        return ClaudeWindowsPathSafety.ResolveContainedPath(root, SessionsDirectoryName);
    }

    private static string GetIndexPath(string root, Guid accountId, Guid organizationId) {
        string relativePath = Path.Combine(
            SessionsDirectoryName,
            accountId.ToString("D"),
            organizationId.ToString("D")
        );
        return ClaudeWindowsPathSafety.ResolveContainedPath(root, relativePath);
    }

    private static string GetTransactionPath(string sessionsRoot, Guid transactionId) {
        return Path.Combine(
            sessionsRoot,
            TransactionsDirectoryName,
            transactionId.ToString("N")
        );
    }

    private static string GetSourceStagingPath(string transactionPath) => Path.Combine(transactionPath, "source-staging");

    private static string GetTargetBackupPath(string transactionPath) => Path.Combine(transactionPath, "target-backup");

    private static string GetTargetBackupTemporaryPath(string transactionPath) => Path.Combine(transactionPath, "target-backup.partial");

    private static string GetTargetDisplacedPath(string transactionPath) => Path.Combine(transactionPath, "target-displaced");

    private static string GetRollbackStagingPath(string transactionPath) => Path.Combine(transactionPath, "target-rollback-staging");

    private static string GetRollbackDisplacedPath(string transactionPath) => Path.Combine(transactionPath, "target-rollback-displaced");

    private static string GetApplyStartedPath(string transactionPath) => Path.Combine(transactionPath, "history-apply-started");

    private static string GetRollbackCompletedPath(string transactionPath) => Path.Combine(transactionPath, "history-rollback-completed");

    private static bool IsTargetRestored(string targetPath, TransactionManifest manifest) {
        if (!manifest.TargetExisted) {
            string organizationDirectoryPath = Path.GetDirectoryName(targetPath)!;
            string accountDirectoryPath = Path.GetDirectoryName(organizationDirectoryPath)!;
            return !Directory.Exists(targetPath)
                && !File.Exists(targetPath)
                && Directory.Exists(accountDirectoryPath) == manifest.AccountDirectoryExisted
                && Directory.Exists(organizationDirectoryPath) == manifest.OrganizationDirectoryExisted;
        }

        return Directory.Exists(targetPath)
            && InventoriesEqual(manifest.TargetFiles, CaptureDirectoryInventory(targetPath, MaximumTargetBackupBytes, true));
    }

    private static bool IsTargetCommitted(string targetPath, TransactionManifest manifest) {
        string organizationDirectoryPath = Path.GetDirectoryName(targetPath)!;
        string accountDirectoryPath = Path.GetDirectoryName(organizationDirectoryPath)!;
        return Directory.Exists(accountDirectoryPath)
            && Directory.Exists(organizationDirectoryPath)
            && Directory.Exists(targetPath)
            && !File.Exists(targetPath)
            && InventoriesEqual(manifest.SourceFiles, CaptureDirectoryInventory(targetPath, MaximumSourceIndexBytes, true));
    }

    private async Task RestoreTargetDirectoriesAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        string root,
        string sessionsRoot,
        string targetPath,
        TransactionManifest manifest,
        WindowsClaudeHistoryCleanupFileSystem.FileIdentity? targetDirectoryIdentity,
        CancellationToken cancellationToken
    ) {
        string organizationDirectoryPath = Path.GetDirectoryName(targetPath)!;
        string accountDirectoryPath = Path.GetDirectoryName(organizationDirectoryPath)!;
        ClaudeWindowsPathSafety.EnsureContained(sessionsRoot, accountDirectoryPath);
        if (!manifest.TargetExisted && Directory.Exists(targetPath)) {
            ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, targetPath);
            if (targetDirectoryIdentity is null
                || WindowsClaudeHistoryCleanupFileSystem.CaptureEntryIdentity(root, targetPath, expectDirectory: true)
                    != targetDirectoryIdentity
                || Directory.EnumerateFileSystemEntries(targetPath).Any()) {
                throw new IOException("Claude target directory changed before rollback removed it.");
            }

            await EnsureDesktopNotRunningAsync(installation, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, targetPath);
            if (WindowsClaudeHistoryCleanupFileSystem.CaptureEntryIdentity(root, targetPath, expectDirectory: true)
                    != targetDirectoryIdentity
                || Directory.EnumerateFileSystemEntries(targetPath).Any()) {
                throw new IOException("Claude target directory changed during rollback.");
            }

            Directory.Delete(targetPath);
        }

        if (!manifest.OrganizationDirectoryExisted && Directory.Exists(organizationDirectoryPath)) {
            ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, organizationDirectoryPath);
            if (!Directory.EnumerateFileSystemEntries(organizationDirectoryPath).Any()) {
                await EnsureDesktopNotRunningAsync(installation, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.EnumerateFileSystemEntries(organizationDirectoryPath).Any()) {
                    throw new IOException("Claude target organization directory changed during rollback.");
                }

                Directory.Delete(organizationDirectoryPath);
            }
        }

        if (!manifest.AccountDirectoryExisted && Directory.Exists(accountDirectoryPath)) {
            ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, accountDirectoryPath);
            if (!Directory.EnumerateFileSystemEntries(accountDirectoryPath).Any()) {
                await EnsureDesktopNotRunningAsync(installation, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.EnumerateFileSystemEntries(accountDirectoryPath).Any()) {
                    throw new IOException("Claude target account directory changed during rollback.");
                }

                Directory.Delete(accountDirectoryPath);
            }
        }
    }

    private static void EnsureSameVolume(string targetPath, string transactionPath) {
        string? targetVolume = Path.GetPathRoot(Path.GetFullPath(targetPath));
        string? transactionVolume = Path.GetPathRoot(Path.GetFullPath(transactionPath));
        if (targetVolume is null
            || transactionVolume is null
            || !string.Equals(targetVolume, transactionVolume, StringComparison.OrdinalIgnoreCase)) {
            throw new IOException("Claude history staging and target directories are not on the same volume.");
        }
    }

    private static void WriteNewFileDurably(string path, byte[] content) {
        using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough
        );
        try {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        } catch {
            stream.Dispose();
            if (File.Exists(path)) {
                File.Delete(path);
            }

            throw;
        }
    }

    private static IClaudeSessionHistoryStore.ValidationResult ValidationFailure(
        IClaudeSessionHistoryStore.ValidationStatus status,
        string failureCode
    ) {
        return new IClaudeSessionHistoryStore.ValidationResult(status, 0, failureCode);
    }

    private static Guid ParseUuid(string value, string parameterName) {
        if (!Guid.TryParse(value, out Guid parsed) || parsed == Guid.Empty) {
            throw new ArgumentException("Claude account and organization identifiers must be UUIDs.", parameterName);
        }

        return parsed;
    }

    private static void ValidateTransactionId(Guid transactionId) {
        if (transactionId == Guid.Empty) {
            throw new ArgumentException("Claude history transaction ID must not be empty.", nameof(transactionId));
        }
    }

    private static bool IsSafeLeafFileName(string value) {
        return !string.IsNullOrWhiteSpace(value)
            && !Path.IsPathRooted(value)
            && !value.Contains(Path.DirectorySeparatorChar)
            && !value.Contains(Path.AltDirectorySeparatorChar)
            && value is not "." and not "..";
    }

    private static bool IsSafeRelativeFilePath(string value) {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || value.Contains('\\')) {
            return false;
        }

        return value.Split('/', StringSplitOptions.None)
            .All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static bool IsSha256(string value) {
        return value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static JsonSerializerOptions CreateJsonOptions() {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed class HistoryFailure : Exception {
        internal HistoryFailure(
            IClaudeSessionHistoryStore.ValidationStatus status,
            string failureCode,
            Exception? innerException = null
        )
            : base(failureCode, innerException) {
            Status = status;
            FailureCode = failureCode;
        }

        internal IClaudeSessionHistoryStore.ValidationStatus Status { get; }

        internal string FailureCode { get; }
    }

    private sealed record SourceEntry(string FileName, byte[] Content);

    private sealed class IndexSnapshot : IDisposable {
        internal IndexSnapshot(
            string directoryPath,
            IReadOnlyList<SourceEntry> entries,
            IReadOnlyList<FileFingerprint> files,
            IReadOnlyList<FileFingerprint> allFiles,
            int sessionCount
        ) {
            DirectoryPath = directoryPath;
            Entries = entries;
            Files = files;
            AllFiles = allFiles;
            SessionCount = sessionCount;
        }

        internal string DirectoryPath { get; }

        internal IReadOnlyList<SourceEntry> Entries { get; }

        internal IReadOnlyList<FileFingerprint> Files { get; }

        internal IReadOnlyList<FileFingerprint> AllFiles { get; }

        internal int SessionCount { get; }

        public void Dispose() {
            foreach (SourceEntry entry in Entries) {
                CryptographicOperations.ZeroMemory(entry.Content);
            }
        }
    }

    private sealed record TargetSnapshot(
        bool Exists,
        bool AccountDirectoryExisted,
        bool OrganizationDirectoryExisted,
        IReadOnlyList<FileFingerprint> Files
    );

    private sealed record FileFingerprint(string RelativePath, long Length, string Sha256);

    private sealed record CleanupPayload(FileFingerprint Fingerprint, long MaximumBytes);

    private sealed record CleanupItem(
        string RelativePath,
        string Path,
        CleanupItemKind Kind,
        WindowsClaudeHistoryCleanupFileSystem.FileIdentity Identity,
        WindowsClaudeHistoryCleanupFileSystem.FileSnapshot? Snapshot,
        long MaximumBytes
    );

    private sealed class CleanupInventory(
        string root,
        string transactionPath,
        WindowsClaudeHistoryCleanupFileSystem.FileIdentity transactionIdentity,
        Dictionary<string, CleanupItem> entries
    ) {
        internal string Root { get; } = root;

        internal string TransactionPath { get; } = transactionPath;

        internal WindowsClaudeHistoryCleanupFileSystem.FileIdentity TransactionIdentity { get; } = transactionIdentity;

        internal Dictionary<string, CleanupItem> RemainingEntries { get; } = entries;

        internal CleanupItem? Find(string relativePath) {
            return RemainingEntries.TryGetValue(relativePath, out CleanupItem? item) ? item : null;
        }

        internal bool TryGet(string relativePath, out CleanupItem? item) {
            return RemainingEntries.TryGetValue(relativePath, out item);
        }

        internal void Remove(string relativePath) {
            RemainingEntries.Remove(relativePath);
        }
    }

    private enum CleanupItemKind {
        Payload,
        Directory,
        Marker,
        CleanupMarker,
        Manifest
    }

    private enum CleanupDeleteKind {
        Payload,
        Marker,
        CleanupMarker,
        Manifest
    }

    private enum PreparationState {
        Preparing,
        Prepared
    }

    private sealed record TransactionManifest(
        int SchemaVersion,
        Guid TransactionId,
        string DataRoot,
        string SourceAccountUuid,
        string SourceOrganizationUuid,
        string TargetAccountUuid,
        string TargetOrganizationUuid,
        bool TargetExisted,
        bool AccountDirectoryExisted,
        bool OrganizationDirectoryExisted,
        int SourceSessionCount,
        int TargetSessionCount,
        IReadOnlyList<FileFingerprint> SourceFiles,
        IReadOnlyList<FileFingerprint> TargetFiles,
        string? TargetIndexPath = null,
        PreparationState? PreparationState = null,
        IReadOnlyList<string>? OwnedDirectoryNames = null,
        int ProvenanceSchemaVersion = 0,
        ClaudeDesktopRootBinding? RootBinding = null
    );

    private sealed class HistoryProcessGuardFailure : IOException {
        internal HistoryProcessGuardFailure(string message, Exception? innerException = null)
            : base(message, innerException) {
        }
    }
}
