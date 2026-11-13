using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Runtime;
using CloudyWing.AiAccountManager.Application.Security;

namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 執行 Claude Desktop 帳號切換、durable journal 與失敗回復。
/// </summary>
public sealed class ClaudeDesktopSwitchService {
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(20);
    private readonly IClaudeAccountRepository accountRepository;
    private readonly IClaudeDesktopInstallationLocator installationLocator;
    private readonly IClaudeDesktopProfileStore profileStore;
    private readonly IClaudeDesktopProcessController processController;
    private readonly IClaudeOAuthClient oauthClient;
    private readonly ClaudeDesktopIdentityReader identityReader;
    private readonly ClaudeSessionHistorySyncService? historyService;
    private readonly ClaudeOperationGate operationGate;
    private readonly IClock clock;

    /// <summary>
    /// 建立 Claude Desktop 切換服務。
    /// </summary>
    /// <param name="accountRepository">保存 profiles、snapshots 與交易 journal 的 repository。</param>
    /// <param name="installationLocator">Claude Desktop 安裝定位器。</param>
    /// <param name="profileStore">Desktop config 與 Cookie snapshot adapter。</param>
    /// <param name="processController">Claude Desktop 程序控制 adapter。</param>
    /// <param name="oauthClient">Claude OAuth profile API client。</param>
    /// <param name="identityFingerprinter">遮蔽 API 回應中的 email。</param>
    /// <param name="historyService">Code Session history sync service。生產環境提供此服務，null 僅表示 auth-only 階段。</param>
    /// <param name="operationGate">Claude 作業閘門。</param>
    /// <param name="clock">目前 UTC 時間來源。</param>
    public ClaudeDesktopSwitchService(
        IClaudeAccountRepository accountRepository,
        IClaudeDesktopInstallationLocator installationLocator,
        IClaudeDesktopProfileStore profileStore,
        IClaudeDesktopProcessController processController,
        IClaudeOAuthClient oauthClient,
        IIdentityFingerprinter identityFingerprinter,
        ClaudeSessionHistorySyncService? historyService,
        ClaudeOperationGate operationGate,
        IClock clock
    ) {
        this.accountRepository = accountRepository;
        this.installationLocator = installationLocator;
        this.profileStore = profileStore;
        this.processController = processController;
        this.oauthClient = oauthClient;
        this.identityReader = new(profileStore, oauthClient, identityFingerprinter, accountRepository);
        this.historyService = historyService;
        this.operationGate = operationGate;
        this.clock = clock;
    }

    /// <summary>
    /// 於切換預覽、交易階段或結果更新時發生。
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// 建立帳號切換確認所需的來源、目標及 history 覆蓋預覽。
    /// </summary>
    /// <param name="targetProfileId">要切換至的 profile 識別碼。</param>
    /// <param name="cancellationToken">取消唯讀預覽作業的權杖。</param>
    /// <returns>切換預覽及可否繼續的原因。</returns>
    public async Task<SwitchPreview> PreviewSwitchAsync(Guid targetProfileId, CancellationToken cancellationToken) {
        await using ClaudeOperationGate.Lease lease = await operationGate
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false);

        SwitchPreview preview = await BuildPreviewCoreAsync(targetProfileId, lease.CancellationToken)
            .ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return preview;
    }

    /// <summary>
    /// 依使用者確認的預覽執行一筆 Claude Desktop profile 與 history 交易。
    /// </summary>
    /// <param name="confirmedPreview">使用者確認的來源、目標與 revision 預覽。</param>
    /// <param name="cancellationToken">取消交易開始前作業的權杖。</param>
    /// <returns>成功、完成 rollback、待復原或拒絕的交易結果。</returns>
    public async Task<SwitchResult> SwitchAsync(
        SwitchPreview confirmedPreview,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(confirmedPreview);
        if (!confirmedPreview.CanProceed
            || confirmedPreview.SourceProfile is null
            || confirmedPreview.TargetProfile is null) {
            return new(SwitchOutcome.Rejected, null, "切換預覽尚未通過驗證，請重新載入帳號與確認內容。");
        }

        await using ClaudeOperationGate.Lease lease = await operationGate
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        CancellationToken operationCancellationToken = lease.CancellationToken;
        SwitchPreview currentPreview = await BuildPreviewCoreAsync(
            confirmedPreview.TargetProfile.ProfileId,
            operationCancellationToken
        ).ConfigureAwait(false);
        if (!currentPreview.CanProceed
            || currentPreview.SourceProfile is null
            || currentPreview.TargetProfile is null) {
            return new(SwitchOutcome.Rejected, null,
                currentPreview.Message ?? "帳號狀態已變更，請重新預覽並確認切換內容。");
        }

        if (currentPreview.SourceProfile.ProfileId != confirmedPreview.SourceProfile.ProfileId
            || currentPreview.TargetProfile.ProfileId != confirmedPreview.TargetProfile.ProfileId
            || currentPreview.SourceProfile.ProfileRevision != confirmedPreview.SourceProfile.ProfileRevision
            || currentPreview.TargetProfile.ProfileRevision != confirmedPreview.TargetProfile.ProfileRevision
            || currentPreview.SourceSessionCount != confirmedPreview.SourceSessionCount
            || currentPreview.TargetSessionCount != confirmedPreview.TargetSessionCount) {
            return new(SwitchOutcome.Rejected, null, "來源、目標或 Session 數量已變更，請重新預覽並確認。");
        }

        IClaudeDesktopInstallationLocator.LocateResult locateResult = await ResolveProfileInstallationAsync(
                currentPreview.TargetProfile.ProfileId, allowRunningObservation: true, operationCancellationToken)
            .ConfigureAwait(false);
        if (locateResult.Status != IClaudeDesktopInstallationLocator.LocateStatus.Found
            || locateResult.Installation is null) {
            return new(SwitchOutcome.Blocked, null, "無法唯一辨識受支援的 Claude Desktop 安裝。");
        }

        IClaudeDesktopInstallationLocator.Installation installation = locateResult.Installation;

        IClaudeAccountRepository.Snapshot catalog = await accountRepository
            .LoadProfilesAsync(operationCancellationToken)
            .ConfigureAwait(false);
        ClaudeAccountProfile? sourceProfile = catalog.Profiles.FirstOrDefault(
            profile => profile.ProfileId == currentPreview.SourceProfile.ProfileId
        );
        ClaudeAccountProfile? targetProfile = catalog.Profiles.FirstOrDefault(
            profile => profile.ProfileId == currentPreview.TargetProfile.ProfileId
        );
        if (sourceProfile is null
            || targetProfile is null
            || sourceProfile.ProfileRevision != currentPreview.SourceProfile.ProfileRevision
            || targetProfile.ProfileRevision != currentPreview.TargetProfile.ProfileRevision) {
            return new(SwitchOutcome.Rejected, null, "Claude 帳號已在其他作業中更新，請重新預覽。");
        }

        ClaudeAccountProfile.DesktopSnapshot? targetSnapshot = await accountRepository
            .LoadDesktopSnapshotAsync(targetProfile.ProfileId, operationCancellationToken)
            .ConfigureAwait(false);
        if (targetSnapshot is null
            || !IsProfileSnapshot(targetProfile, targetSnapshot, locateResult.Installation)
            || !HasCompleteCookieSidecarSnapshot(targetSnapshot)) {
            return new(SwitchOutcome.Blocked, null,
                "目標帳號的登入資料已遺失或失效，請重新保存此帳號。未修改 Claude Desktop 資料。");
        }

        IClaudeDesktopProcessController.ProcessStatus initialProcessStatus;
        try {
            initialProcessStatus = await processController
                .GetStatusAsync(locateResult.Installation, operationCancellationToken)
                .ConfigureAwait(false);
        } catch (OperationCanceledException) when (operationCancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            initialProcessStatus = IClaudeDesktopProcessController.ProcessStatus.Unknown;
        }

        if (initialProcessStatus == IClaudeDesktopProcessController.ProcessStatus.Unknown) {
            return new(SwitchOutcome.Blocked, null, "目前無法確認 Claude Desktop 程序狀態，未開始切換。");
        }

        ClaudeSwitchTransaction transaction = new(
            Guid.NewGuid(),
            sourceProfile.ProfileId,
            targetProfile.ProfileId,
            sourceProfile.ProfileRevision,
            targetProfile.ProfileRevision,
            locateResult.Installation.InstallationId,
            ClaudeSwitchTransaction.Stage.Prepared,
            clock.UtcNow,
            clock.UtcNow,
            null,
            ProvenanceSchemaVersion: 1,
            RootBinding: targetSnapshot.RootBinding
        );
        try {
            await accountRepository.SaveSwitchTransactionAsync(transaction, operationCancellationToken)
                .ConfigureAwait(false);
        } catch (OperationCanceledException) when (operationCancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return new(SwitchOutcome.Rejected, null, "無法記錄切換狀態，未修改 Claude Desktop 資料。");
        }

        try {
            bool closed = await processController
                .CloseGracefullyAsync(locateResult.Installation, CloseTimeout, operationCancellationToken)
                .ConfigureAwait(false);
            if (!closed) {
                transaction = await SaveStageAsync(
                    transaction,
                    ClaudeSwitchTransaction.Stage.RolledBack,
                    null,
                    operationCancellationToken
                ).ConfigureAwait(false);
                return new(SwitchOutcome.FailedRolledBack, transaction, "Claude Desktop 未能正常關閉，沒有修改設定或 Session。");
            }

            await RequireDesktopStoppedAsync(
                locateResult.Installation,
                operationCancellationToken,
                "desktop_restarted_after_close"
            ).ConfigureAwait(false);

            IClaudeDesktopInstallationLocator.LocateResult rebound = await installationLocator.ResolveBoundAsync(transaction.RootBinding!, operationCancellationToken)
                .ConfigureAwait(false);
            if (rebound.Status != IClaudeDesktopInstallationLocator.LocateStatus.Found
                || rebound.Installation is not { } reboundInstallation) {
                throw new SwitchOperationException("source_binding_changed_after_close");
            }

            installation = reboundInstallation;
            locateResult = rebound;

            transaction = await SaveStageAsync(
                transaction,
                ClaudeSwitchTransaction.Stage.DesktopClosed,
                null,
                operationCancellationToken
            ).ConfigureAwait(false);

            ClaudeDesktopIdentityReader.Result sourceIdentity = await identityReader
                .ReadAsync(locateResult.Installation, catalog.Profiles, operationCancellationToken)
                .ConfigureAwait(false);
            await RequireDesktopStoppedAsync(
                locateResult.Installation,
                operationCancellationToken,
                "desktop_restarted_during_source_capture"
            ).ConfigureAwait(false);
            if (!MatchesProfile(sourceIdentity, sourceProfile)) {
                throw new SwitchOperationException("source_identity_changed");
            }

            sourceProfile = await CaptureAndSaveSourceAsync(
                catalog,
                sourceProfile,
                sourceIdentity,
                locateResult.Installation,
                sourceIdentity.MaskedEmail,
                operationCancellationToken
            ).ConfigureAwait(false);
            catalog = await accountRepository.LoadProfilesAsync(operationCancellationToken).ConfigureAwait(false);
            transaction = await SaveStageAsync(
                transaction with { SourceProfileRevision = sourceProfile.ProfileRevision },
                ClaudeSwitchTransaction.Stage.SourceCaptured,
                null,
                operationCancellationToken
            ).ConfigureAwait(false);

            await profileStore.PrepareRestoreAsync(locateResult.Installation, targetSnapshot, transaction.TransactionId,
                operationCancellationToken).ConfigureAwait(false);

            if (historyService is not null) {
                transaction = await SaveStageAsync(
                    transaction,
                    ClaudeSwitchTransaction.Stage.TargetBackedUp,
                    null,
                    operationCancellationToken
                ).ConfigureAwait(false);
                transaction = await SaveStageAsync(
                    transaction,
                    ClaudeSwitchTransaction.Stage.HistoryPreparationStarted,
                    null,
                    operationCancellationToken
                ).ConfigureAwait(false);
                await RequireDesktopStoppedAsync(
                    locateResult.Installation,
                    operationCancellationToken,
                    "desktop_restarted_before_history_prepare"
                ).ConfigureAwait(false);
                ClaudeSessionHistorySyncService.PreparationResult preparation = await historyService
                    .PrepareReplacementAsync(
                        locateResult.Installation,
                        sourceProfile,
                        targetProfile,
                        transaction.TransactionId,
                        operationCancellationToken
                    )
                    .ConfigureAwait(false);
                if (preparation.Status != IClaudeSessionHistoryStore.ValidationStatus.Valid) {
                    throw new SwitchOperationException("source_history_invalid");
                }

                if (preparation.SourceSessionCount != confirmedPreview.SourceSessionCount
                    || preparation.TargetSessionCount != confirmedPreview.TargetSessionCount) {
                    throw new SwitchOperationException("history_changed_after_confirmation");
                }

                transaction = await SaveStageAsync(
                    transaction,
                    ClaudeSwitchTransaction.Stage.HistoryPrepared,
                    null,
                    operationCancellationToken
                ).ConfigureAwait(false);
                transaction = await SaveStageAsync(
                    transaction,
                    ClaudeSwitchTransaction.Stage.HistoryApplyStarted,
                    null,
                    operationCancellationToken
                ).ConfigureAwait(false);
                await RequireDesktopStoppedAsync(
                    locateResult.Installation,
                    operationCancellationToken,
                    "desktop_restarted_before_history_apply"
                ).ConfigureAwait(false);
                await historyService.ApplyReplacementAsync(
                    locateResult.Installation,
                    targetProfile,
                    transaction.TransactionId,
                    operationCancellationToken
                ).ConfigureAwait(false);
                transaction = await SaveStageAsync(
                    transaction,
                    ClaudeSwitchTransaction.Stage.HistoryApplied,
                    null,
                    operationCancellationToken
                ).ConfigureAwait(false);
            } else {
                transaction = await SaveStageAsync(
                    transaction,
                    ClaudeSwitchTransaction.Stage.TargetBackedUp,
                    null,
                    operationCancellationToken
                ).ConfigureAwait(false);
            }

            transaction = await SaveStageAsync(
                transaction,
                ClaudeSwitchTransaction.Stage.TargetAuthApplyStarted,
                null,
                operationCancellationToken
            ).ConfigureAwait(false);
            await RequireDesktopStoppedAsync(
                locateResult.Installation,
                operationCancellationToken,
                "desktop_restarted_before_auth_apply"
            ).ConfigureAwait(false);
            await profileStore.ApplyRestoreAsync(locateResult.Installation, transaction.TransactionId, operationCancellationToken)
                .ConfigureAwait(false);
            transaction = await SaveStageAsync(
                transaction,
                ClaudeSwitchTransaction.Stage.TargetAuthApplied,
                null,
                operationCancellationToken
            ).ConfigureAwait(false);
            transaction = await SaveStageAsync(
                transaction,
                ClaudeSwitchTransaction.Stage.TargetLaunchStarted,
                null,
                operationCancellationToken
            ).ConfigureAwait(false);
            await processController.LaunchAsync(locateResult.Installation, operationCancellationToken)
                .ConfigureAwait(false);
            transaction = await SaveStageAsync(
                transaction,
                ClaudeSwitchTransaction.Stage.TargetLaunched,
                null,
                operationCancellationToken
            ).ConfigureAwait(false);

            ClaudeDesktopIdentityReader.Result targetIdentity = await identityReader
                .ReadAsync(locateResult.Installation, catalog.Profiles, operationCancellationToken)
                .ConfigureAwait(false);
            if (!MatchesProfile(targetIdentity, targetProfile)) {
                throw new SwitchOperationException("target_identity_unverified");
            }

            transaction = await SaveStageAsync(
                transaction,
                ClaudeSwitchTransaction.Stage.TargetVerified,
                null,
                operationCancellationToken
            ).ConfigureAwait(false);
            {
                transaction = await SaveStageAsync(
                    transaction,
                    ClaudeSwitchTransaction.Stage.CommittedCleanupPending,
                    null,
                    CancellationToken.None
                ).ConfigureAwait(false);
                SwitchResult cleanupResult = await RetryCommittedCleanupAsync(
                    transaction,
                    locateResult.Installation,
                    CancellationToken.None
                ).ConfigureAwait(false);
                StateChanged?.Invoke(this, EventArgs.Empty);
                return new(
                    SwitchOutcome.Succeeded,
                    cleanupResult.Transaction,
                    cleanupResult.Outcome == SwitchOutcome.Succeeded
                        ? "Claude Desktop 已切換至目標帳號。"
                        : cleanupResult.Message
                );
            }

        } catch (OperationCanceledException) when (operationCancellationToken.IsCancellationRequested) {
            return await RollBackAfterFailureAsync(
                transaction,
                installation,
                "operation_cancelled"
            ).ConfigureAwait(false);
        } catch (SwitchOperationException exception) {
            return await RollBackAfterFailureAsync(
                transaction,
                installation,
                exception.FailureCode
            ).ConfigureAwait(false);
        } catch (Exception) {
            return await RollBackAfterFailureAsync(
                transaction,
                installation,
                "switch_failed"
            ).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 對初始化時發現的未終止 journal 執行冪等來源帳號回復。
    /// </summary>
    /// <param name="cancellationToken">取消復原開始前等待的權杖。</param>
    /// <returns>回復成功或仍需人工處理的結果。</returns>
    public async Task<SwitchResult> RecoverPendingSwitchAsync(CancellationToken cancellationToken) {
        await using ClaudeOperationGate.Lease lease = await operationGate
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false);

        ClaudeSwitchTransaction? transaction = await accountRepository
            .LoadSwitchTransactionAsync(lease.CancellationToken)
            .ConfigureAwait(false);
        if (transaction is not null && (ClaudeAccountService.IsRecoveryRequired(transaction) || IsCommittedCleanupPending(transaction))
            && !HasTrustedProvenance(transaction)) {
            return new(SwitchOutcome.RecoveryRequired, transaction, "無法自動確認上次切換的狀態，資料已保留。");
        }
        if (IsCommittedCleanupPending(transaction)) {
            return await RetryCommittedCleanupAsync(
                transaction!,
                lease.CancellationToken
            ).ConfigureAwait(false);
        }

        if (!ClaudeAccountService.IsRecoveryRequired(transaction)) {
            return new(SwitchOutcome.Rejected, transaction, "目前沒有待復原的帳號切換。");
        }

        IClaudeDesktopInstallationLocator.ObservationResult observed = await installationLocator
            .ObserveRunningAsync(lease.CancellationToken).ConfigureAwait(false);
        if (observed.Status == IClaudeDesktopInstallationLocator.LocateStatus.Found
            && (observed.Observation is null || observed.Installation is null)) {
            return await RecordInstallationRecoveryFailureAsync(transaction!, "running-installation-mismatch")
                .ConfigureAwait(false);
        }
        if (observed.Status == IClaudeDesktopInstallationLocator.LocateStatus.Found
            && observed.Installation is IClaudeDesktopInstallationLocator.Installation running) {
            if (observed.Observation is null
                || !string.Equals(observed.Observation.InstallationId, transaction!.InstallationId, StringComparison.Ordinal)
                || !string.Equals(running.InstallationId, transaction.InstallationId, StringComparison.Ordinal)) {
                return await RecordInstallationRecoveryFailureAsync(transaction!, "running-installation-mismatch")
                    .ConfigureAwait(false);
            }

            IClaudeDesktopProcessController.ProcessStatus status = await ReadProcessStatusAsync(running, lease.CancellationToken)
                .ConfigureAwait(false);
            if (status == IClaudeDesktopProcessController.ProcessStatus.Unknown) {
                return await RecordRecoveryRequiredAsync(transaction!, "process_status_unknown").ConfigureAwait(false);
            }
            if (status == IClaudeDesktopProcessController.ProcessStatus.Running) {
                bool closed = await processController.CloseGracefullyAsync(running, CloseTimeout, lease.CancellationToken)
                    .ConfigureAwait(false);
                if (!closed) {
                    return await RecordRecoveryRequiredAsync(transaction!, "rollback_close_timeout").ConfigureAwait(false);
                }
                if (await ReadProcessStatusAsync(running, lease.CancellationToken).ConfigureAwait(false)
                    != IClaudeDesktopProcessController.ProcessStatus.NotRunning) {
                    return await RecordRecoveryRequiredAsync(transaction!, "rollback_process_still_running").ConfigureAwait(false);
                }
            }
        }

        IClaudeDesktopInstallationLocator.LocateResult locateResult = await installationLocator
            .ResolveBoundAsync(transaction!.RootBinding!, lease.CancellationToken)
            .ConfigureAwait(false);
        if (locateResult.Status != IClaudeDesktopInstallationLocator.LocateStatus.Found
            || locateResult.Installation is null) {
            return await RecordInstallationRecoveryFailureAsync(transaction!, locateResult.FailureCode)
                .ConfigureAwait(false);
        }
        if (!string.Equals(
                locateResult.Installation.InstallationId,
                transaction!.InstallationId,
                StringComparison.Ordinal
            )) {
            return await RecordInstallationRecoveryFailureAsync(transaction!, "installation-id-mismatch")
                .ConfigureAwait(false);
        }

        return await RollBackAfterFailureAsync(
            transaction!,
            locateResult.Installation,
            transaction!.FailureCode ?? "recovery_requested"
        ).ConfigureAwait(false);
    }

    private async Task<SwitchResult> RecordInstallationRecoveryFailureAsync(
        ClaudeSwitchTransaction transaction,
        string? locatorCode
    ) {
        string code = string.IsNullOrWhiteSpace(locatorCode) ? "unknown" : new string(locatorCode
            .Take(64)
            .Select(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' ? character : '-')
            .ToArray());
        SwitchResult result = await RecordRecoveryRequiredAsync(transaction, $"installation_unavailable:{code}")
            .ConfigureAwait(false);
        return result with { Message = DescribeInstallationFailure(code) };
    }

    private static string DescribeInstallationFailure(string code) {
        return code switch {
            "desktop-not-stopped" or "running-installation-mismatch" or "source-changed"
                => "Claude Desktop 尚未完全結束，請先正常結束 Claude Desktop 後按「重試復原」。",
            "multiple-data-roots" or "installation-id-mismatch"
                => "找到多份 Claude Desktop 安裝或資料，請確認只保留一份後按「重試復原」。",
            // 復原待處理期間無法重新保存帳號，因此資料已變動時不指示使用者重新保存。
            "local-state-changed" or "data-root-changed" or "binding-invalid" or "data-layout-unknown"
                => "Claude Desktop 的資料已變動，無法自動復原，原資料已保留。",
            "source-identity-unknown"
                => "無法確認原 Claude Desktop 的來源，請確認使用切換時的 Windows 使用者登入，稍後按「重試復原」。",
            _ => "無法確認原 Claude Desktop 安裝，請稍後按「重試復原」。"
        };
    }

    private async Task<SwitchPreview> BuildPreviewCoreAsync(Guid targetProfileId, CancellationToken cancellationToken) {
        IClaudeAccountRepository.Snapshot catalog;
        try {
            catalog = await accountRepository.LoadProfilesAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return SwitchPreview.Failed(PreviewOutcome.Blocked, null, null, false, "無法載入 Claude 帳號清單。");
        }

        ClaudeSwitchTransaction? transaction;
        try {
            transaction = await accountRepository.LoadSwitchTransactionAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return SwitchPreview.Failed(PreviewOutcome.Blocked, null, null, false, "無法確認是否有待復原的帳號切換。");
        }

        if (IsCommittedCleanupPending(transaction)) {
            SwitchResult cleanupResult = await RetryCommittedCleanupAsync(
                transaction!,
                cancellationToken
            ).ConfigureAwait(false);
            if (cleanupResult.Outcome != SwitchOutcome.Succeeded) {
                return SwitchPreview.Failed(
                    PreviewOutcome.CleanupPending,
                    catalog.Profiles.FirstOrDefault(profile => profile.ProfileId == transaction!.SourceProfileId),
                    catalog.Profiles.FirstOrDefault(profile => profile.ProfileId == targetProfileId),
                    historyService is not null,
                    cleanupResult.Message
                ) with { Transaction = cleanupResult.Transaction };
            }

            transaction = null;
        }

        if (ClaudeAccountService.IsRecoveryRequired(transaction)) {
            return SwitchPreview.Failed(
                PreviewOutcome.RecoveryRequired,
                catalog.Profiles.FirstOrDefault(profile => profile.ProfileId == transaction!.SourceProfileId),
                catalog.Profiles.FirstOrDefault(profile => profile.ProfileId == targetProfileId),
                historyService is not null,
                "有尚未完成的 Claude Desktop 帳號切換，請先完成復原。"
            ) with { Transaction = transaction };
        }

        IClaudeDesktopInstallationLocator.LocateResult locateResult;
        try {
            locateResult = await ResolveProfileInstallationAsync(targetProfileId, allowRunningObservation: true, cancellationToken)
                .ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return SwitchPreview.Failed(PreviewOutcome.InstallationUnavailable, null, null, historyService is not null,
                "無法定位 Claude Desktop 安裝。");
        }

        if (locateResult.Status != IClaudeDesktopInstallationLocator.LocateStatus.Found
            || locateResult.Installation is null) {
            return SwitchPreview.Failed(
                locateResult.Status == IClaudeDesktopInstallationLocator.LocateStatus.Ambiguous
                    ? PreviewOutcome.AmbiguousInstallation
                    : locateResult.Status == IClaudeDesktopInstallationLocator.LocateStatus.Unsupported
                        ? PreviewOutcome.UnsupportedInstallation
                        : PreviewOutcome.InstallationUnavailable,
                null,
                null,
                historyService is not null,
                "無法唯一辨識受支援的 Claude Desktop 安裝。"
            );
        }

        ClaudeAccountProfile? targetProfile = catalog.Profiles.FirstOrDefault(
            profile => profile.ProfileId == targetProfileId
        );
        if (targetProfile is null) {
            return SwitchPreview.Failed(PreviewOutcome.TargetMissing, null, null, historyService is not null,
                "找不到指定的目標 Claude 帳號。");
        }

        ClaudeDesktopIdentityReader.Result currentIdentity;
        try {
            currentIdentity = await identityReader
                .ReadAsync(locateResult.Installation, catalog.Profiles, cancellationToken)
                .ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return SwitchPreview.Failed(PreviewOutcome.IdentityUnavailable, null, targetProfile, historyService is not null,
                "無法驗證目前 Claude Desktop 身分。");
        }

        ClaudeAccountProfile? sourceProfile = currentIdentity.MatchedProfile;
        if (currentIdentity.Status != ClaudeAccountService.IdentityStatus.Verified || sourceProfile is null) {
            PreviewOutcome identityOutcome = currentIdentity.Status switch {
                ClaudeAccountService.IdentityStatus.ReauthenticationRequired => PreviewOutcome.ReauthenticationRequired,
                ClaudeAccountService.IdentityStatus.Mismatch => PreviewOutcome.IdentityMismatch,
                _ => PreviewOutcome.IdentityUnavailable
            };
            return SwitchPreview.Failed(identityOutcome, sourceProfile, targetProfile, historyService is not null,
                currentIdentity.Message ?? "目前 Claude Desktop 身分尚未驗證。");
        }

        ClaudeAccountProfile.DesktopSnapshot? sourceSnapshot;
        try {
            sourceSnapshot = await accountRepository
                .LoadDesktopSnapshotAsync(sourceProfile.ProfileId, cancellationToken)
                .ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return SwitchPreview.Failed(PreviewOutcome.SourceBindingMismatch, sourceProfile, targetProfile,
                historyService is not null, "來源帳號的資料與目前安裝不符，請重新保存來源帳號。");
        }

        if (sourceSnapshot is null || !IsProfileSnapshot(sourceProfile, sourceSnapshot, locateResult.Installation)) {
            return SwitchPreview.Failed(PreviewOutcome.SourceBindingMismatch, sourceProfile, targetProfile,
                historyService is not null, "來源帳號的資料與目前安裝不符，請重新保存來源帳號。");
        }

        if (sourceProfile.ProfileId == targetProfile.ProfileId) {
            return SwitchPreview.Failed(PreviewOutcome.SameProfile, sourceProfile, targetProfile, historyService is not null,
                "目前 Claude Desktop 已使用此帳號。");
        }

        ClaudeAccountProfile.DesktopSnapshot? targetSnapshot;
        try {
            targetSnapshot = await accountRepository
                .LoadDesktopSnapshotAsync(targetProfile.ProfileId, cancellationToken)
                .ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return SwitchPreview.Failed(PreviewOutcome.TargetSnapshotMissing, sourceProfile, targetProfile,
                historyService is not null, "目標帳號的登入資料已遺失或失效，請重新保存此帳號。");
        }

        if (targetSnapshot is null) {
            return SwitchPreview.Failed(PreviewOutcome.TargetSnapshotMissing, sourceProfile, targetProfile,
                historyService is not null, "目標帳號的登入資料已遺失或失效，請重新保存此帳號。");
        }

        if (!IsProfileSnapshot(targetProfile, targetSnapshot, locateResult.Installation)
            || !HasCompleteCookieSidecarSnapshot(targetSnapshot)) {
            return SwitchPreview.Failed(PreviewOutcome.TargetBindingMismatch, sourceProfile, targetProfile,
                historyService is not null, "目標帳號的登入資料已遺失或失效，請重新保存此帳號。");
        }

        SavedProfileVerification targetVerification = await VerifySavedProfileAsync(
            targetProfile,
            targetSnapshot,
            cancellationToken
        ).ConfigureAwait(false);
        if (!targetVerification.Verified) {
            return SwitchPreview.Failed(
                targetVerification.ReauthenticationRequired
                    ? PreviewOutcome.ReauthenticationRequired
                    : PreviewOutcome.TargetIdentityMismatch,
                sourceProfile,
                targetProfile,
                historyService is not null,
                targetVerification.Message
            );
        }

        IClaudeDesktopProcessController.ProcessStatus processStatus;
        try {
            processStatus = await processController
                .GetStatusAsync(locateResult.Installation, cancellationToken)
                .ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            processStatus = IClaudeDesktopProcessController.ProcessStatus.Unknown;
        }

        if (processStatus == IClaudeDesktopProcessController.ProcessStatus.Unknown) {
            return SwitchPreview.Failed(PreviewOutcome.ProcessStatusUnknown, sourceProfile, targetProfile,
                historyService is not null, "無法確認 Claude Desktop 程序狀態。");
        }

        int? sourceSessionCount = null;
        int? targetSessionCount = null;
        if (historyService is not null) {
            ClaudeSessionHistorySyncService.PreviewResult history = await historyService
                .PreviewReplacementAsync(locateResult.Installation, sourceProfile, targetProfile, cancellationToken)
                .ConfigureAwait(false);
            if (!history.CanProceed) {
                return SwitchPreview.Failed(PreviewOutcome.HistoryUnavailable, sourceProfile, targetProfile, true,
                    "無法讀取來源帳號的 Code Session 資料，未開始切換。") with {
                        SourceSessionCount = history.SourceSessionCount,
                        TargetSessionCount = history.TargetSessionCount
                    };
            }

            sourceSessionCount = history.SourceSessionCount;
            targetSessionCount = history.TargetSessionCount;
        }

        return new(
            PreviewOutcome.Ready,
            sourceProfile,
            targetProfile,
            sourceSessionCount,
            targetSessionCount,
            historyService is not null,
            true,
            historyService is null
                ? "Claude Desktop 會關閉後以目標帳號重新啟動。"
                : "Claude Desktop 會關閉後重新啟動，目標 Code Session 清單會以來源清單完整覆蓋。",
            clock.UtcNow,
            null
        );
    }

    private async Task<SavedProfileVerification> VerifySavedProfileAsync(
        ClaudeAccountProfile profile,
        ClaudeAccountProfile.DesktopSnapshot snapshot,
        CancellationToken cancellationToken
    ) {
        IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> candidates;
        try {
            candidates = profileStore.GetTokenCandidates(snapshot);
        } catch (Exception) {
            return new(false, true, "目標帳號的登入已失效，請重新登入並保存。");
        }

        if (candidates.Count == 0) {
            return new(false, true, "目標帳號的登入已失效，請重新登入並保存。");
        }

        bool hadUnauthorized = false;
        bool hadForbidden = false;
        foreach (IClaudeDesktopProfileStore.TokenCandidate candidate in candidates) {
            IClaudeOAuthClient.ProfileResult result;
            try {
                result = await oauthClient.GetProfileAsync(candidate.AccessToken, cancellationToken)
                    .ConfigureAwait(false);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception) {
                continue;
            }

            if (result.Outcome == IClaudeOAuthClient.Outcome.Unauthorized) {
                hadUnauthorized = true;
                continue;
            }

            if (result.Outcome == IClaudeOAuthClient.Outcome.Forbidden) {
                hadForbidden = true;
                continue;
            }

            if (result.Outcome == IClaudeOAuthClient.Outcome.Success
                && SameIdentity(profile.AccountUuid, profile.OrganizationUuid, result.AccountUuid, result.OrganizationUuid)) {
                return new(true, false, "目標帳號的身分已確認。");
            }
        }

        return hadUnauthorized || hadForbidden
            ? new(false, true, "目標帳號的登入已失效，請重新登入並保存。")
            : new(false, false, "目標帳號與保存時的身分不一致，請重新保存。");
    }

    private async Task<SwitchResult> RetryCommittedCleanupAsync(
        ClaudeSwitchTransaction transaction,
        CancellationToken cancellationToken
    ) {
        if (!HasTrustedProvenance(transaction)) {
            return new(SwitchOutcome.CleanupPending, transaction, "無法自動確認上次切換的狀態，資料已保留。");
        }

        IClaudeDesktopInstallationLocator.LocateResult locateResult;
        try {
            locateResult = await installationLocator.ResolveBoundAsync(transaction.RootBinding!, cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception failure) {
            return CleanupFailure(transaction, failure is SwitchOperationException operation ? operation.FailureCode : failure.GetType().Name);
        }

        if (locateResult.FailureCode == "desktop-not-stopped") {
            try {
                IClaudeDesktopInstallationLocator.ObservationResult observed = await installationLocator
                    .ObserveRunningAsync(cancellationToken).ConfigureAwait(false);
                ClaudeDesktopRootBinding.Observation expected = transaction.RootBinding!.Source;
                ClaudeDesktopRootBinding.Observation? actual = observed.Observation;
                if (observed.Status != IClaudeDesktopInstallationLocator.LocateStatus.Found
                    || observed.Installation is null || actual is null
                    || !string.Equals(actual.InstallationId, transaction.InstallationId, StringComparison.Ordinal)
                    || !string.Equals(observed.Installation.InstallationId, transaction.InstallationId, StringComparison.Ordinal)
                    || !string.Equals(actual.DataRoot, expected.DataRoot, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(observed.Installation.DataRoot, actual.DataRoot, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(actual.WindowsUserSid, expected.WindowsUserSid, StringComparison.OrdinalIgnoreCase)
                    || actual.RootVolumeSerialNumber != expected.RootVolumeSerialNumber
                    || !string.Equals(actual.RootFileId, expected.RootFileId, StringComparison.OrdinalIgnoreCase)) {
                    return CleanupFailure(transaction, observed.FailureCode ?? "running-installation-mismatch");
                }

                locateResult = new(IClaudeDesktopInstallationLocator.LocateStatus.Found,
                    observed.Installation with { Binding = transaction.RootBinding }, null);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception failure) {
                return CleanupFailure(transaction, failure is SwitchOperationException operation ? operation.FailureCode : failure.GetType().Name);
            }
        }

        if (locateResult.Status != IClaudeDesktopInstallationLocator.LocateStatus.Found
            || locateResult.Installation is null
            || !string.Equals(locateResult.Installation.InstallationId, transaction.InstallationId, StringComparison.Ordinal)) {
            return CleanupFailure(transaction, locateResult.FailureCode ?? "installation-mismatch");
        }

        return await RetryCommittedCleanupAsync(transaction, locateResult.Installation, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SwitchResult> RetryCommittedCleanupAsync(
        ClaudeSwitchTransaction transaction,
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    ) {
        if (transaction.CurrentStage != ClaudeSwitchTransaction.Stage.CommittedCleanupPending) {
            return new(SwitchOutcome.Rejected, transaction, "目前沒有待重試的暫存資料清理。");
        }

        if (transaction.TransactionId == Guid.Empty
            || !HasTrustedProvenance(transaction)
            || !BindingsEqual(transaction.RootBinding, installation.Binding)
            || string.IsNullOrWhiteSpace(transaction.InstallationId)
            || !string.Equals(transaction.InstallationId, installation.InstallationId, StringComparison.Ordinal)) {
            return CleanupFailure(transaction, "transaction-binding-mismatch");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ClaudeSwitchTransaction committed;
        try {
            if (historyService is not null) {
                await historyService.CommitAsync(installation, transaction.TransactionId,
                    ClaudeSessionHistoryCleanupIntent.Committed, CancellationToken.None).ConfigureAwait(false);
            }

            await profileStore.CommitTransactionAsync(installation, transaction.TransactionId, CancellationToken.None)
                .ConfigureAwait(false);
            committed = await SaveStageAsync(
                transaction,
                ClaudeSwitchTransaction.Stage.Committed,
                null,
                CancellationToken.None
            ).ConfigureAwait(false);
        } catch (Exception failure) {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return CleanupFailure(transaction, failure is SwitchOperationException operation ? operation.FailureCode : failure.GetType().Name);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return new(SwitchOutcome.Succeeded, committed, "切換已完成，暫存資料已清理。");
    }

    private static SwitchResult CleanupFailure(ClaudeSwitchTransaction transaction, string failureCode) {
        string description = failureCode switch {
            "desktop-not-stopped" => "暫存資料待清理，請先正常結束 Claude Desktop 後重試。",
            "running-installation-mismatch" or "installation-mismatch" or "transaction-binding-mismatch"
                => "暫存資料待清理，但無法確認原 Claude Desktop 安裝，請確認只保留一份後按「重試暫存清理」。",
            _ => "暫存資料待清理，稍後可重試。"
        };
        return new(SwitchOutcome.CleanupPending, transaction, $"切換已完成，{description}");
    }

    private async Task RequireDesktopStoppedAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken,
        string failureCode
    ) {
        if (installation.Kind == IClaudeDesktopInstallationLocator.InstallKind.Msix) {
            ClaudeDesktopRootBinding? binding = installation.Binding;
            ClaudeDesktopRootBinding.Observation? expectedObservation = installation.Observation ?? binding?.Source;
            if (binding is null || expectedObservation is null) {
                throw new SwitchOperationException(failureCode);
            }

            IClaudeDesktopInstallationLocator.LocateResult rebound = await installationLocator
                .ResolveBoundAsync(binding, cancellationToken).ConfigureAwait(false);
            ClaudeDesktopRootBinding.Observation? actualObservation =
                rebound.Installation?.Observation ?? rebound.Installation?.Binding?.Source;
            if (rebound.Status != IClaudeDesktopInstallationLocator.LocateStatus.Found
                || rebound.Installation is null
                || !BindingsEqual(binding, rebound.Installation.Binding)
                || actualObservation is null
                || !ClaudeDesktopIdentityReader.HasSameSource(expectedObservation, actualObservation)
                || !string.Equals(
                    actualObservation.PackageFullName,
                    expectedObservation.PackageFullName,
                    StringComparison.OrdinalIgnoreCase
                )) {
                throw new SwitchOperationException(failureCode);
            }
        }

        IClaudeDesktopProcessController.ProcessStatus status = await ReadProcessStatusAsync(
            installation,
            cancellationToken
        ).ConfigureAwait(false);
        if (status != IClaudeDesktopProcessController.ProcessStatus.NotRunning) {
            throw new SwitchOperationException(failureCode);
        }
    }

    private async Task<bool> EnsureRollbackWriteSafeAsync(
        IClaudeDesktopInstallationLocator.Installation installation
    ) {
        IClaudeDesktopProcessController.ProcessStatus status = await ReadProcessStatusAsync(
            installation,
            CancellationToken.None
        ).ConfigureAwait(false);
        if (status == IClaudeDesktopProcessController.ProcessStatus.Unknown) {
            return false;
        }

        if (status == IClaudeDesktopProcessController.ProcessStatus.NotRunning) {
            return true;
        }

        bool closed = await processController
            .CloseGracefullyAsync(installation, CloseTimeout, CancellationToken.None)
            .ConfigureAwait(false);
        if (!closed) {
            return false;
        }

        return await ReadProcessStatusAsync(installation, CancellationToken.None).ConfigureAwait(false)
            == IClaudeDesktopProcessController.ProcessStatus.NotRunning;
    }

    private async Task<IClaudeDesktopProcessController.ProcessStatus> ReadProcessStatusAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    ) {
        try {
            return await processController.GetStatusAsync(installation, cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return IClaudeDesktopProcessController.ProcessStatus.Unknown;
        }
    }

    private static bool IsCommittedCleanupPending(ClaudeSwitchTransaction? transaction) {
        return transaction?.CurrentStage == ClaudeSwitchTransaction.Stage.CommittedCleanupPending;
    }

    private async Task<ClaudeAccountProfile> SaveCapturedSourceAsync(
        IClaudeAccountRepository.Snapshot catalog,
        ClaudeAccountProfile sourceProfile,
        ClaudeAccountProfile.DesktopSnapshot snapshot,
        string? maskedEmail,
        CancellationToken cancellationToken
    ) {
        ClaudeAccountProfile updated = sourceProfile with {
            MaskedEmail = maskedEmail ?? sourceProfile.MaskedEmail,
            WindowsUserSid = snapshot.WindowsUserSid,
            InstallationId = snapshot.InstallationId,
            DataRootFingerprint = snapshot.DataRootFingerprint,
            LocalStateEncryptedKeySha256 = snapshot.LocalStateEncryptedKeySha256,
            ProfileRevision = checked(sourceProfile.ProfileRevision + 1),
            LastVerifiedAtUtc = clock.UtcNow,
            Status = ClaudeAccountProfile.VerificationStatus.Verified
        };
        IClaudeAccountRepository.SaveResult saveResult = await accountRepository
            .SaveProfileAsync(updated, snapshot, catalog.Sha256, cancellationToken)
            .ConfigureAwait(false);
        if (!saveResult.Saved) {
            throw new SwitchOperationException(saveResult.Conflict ? "profile_catalog_conflict" : "source_profile_save_failed");
        }

        return saveResult.Snapshot?.Profiles.FirstOrDefault(profile => profile.ProfileId == updated.ProfileId)
            ?? updated;
    }

    private async Task<ClaudeAccountProfile> CaptureAndSaveSourceAsync(
        IClaudeAccountRepository.Snapshot catalog,
        ClaudeAccountProfile sourceProfile,
        ClaudeDesktopIdentityReader.Result identity,
        IClaudeDesktopInstallationLocator.Installation installation,
        string? maskedEmail,
        CancellationToken cancellationToken
    ) {
        await using IClaudeDesktopProfileStore.StableSnapshotLease snapshotLease = await profileStore
            .CaptureStableSnapshotAsync(installation, cancellationToken)
            .ConfigureAwait(false);
        ClaudeAccountProfile.DesktopSnapshot snapshot = snapshotLease.Snapshot;
        ClaudeAccountProfile.DesktopSnapshot? previousSnapshot = await accountRepository
            .LoadDesktopSnapshotAsync(sourceProfile.ProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (previousSnapshot is null
            || !IsProfileSnapshot(sourceProfile, previousSnapshot, installation)
            || !ClaudeDesktopIdentityReader.HasMatchingBinding(previousSnapshot, snapshot)
            || !IsProfileSnapshot(sourceProfile, snapshot, installation)
            || !HasCompleteCookieSidecarSnapshot(snapshot)
            || !string.Equals(
                snapshot.LastKnownAccountUuid,
                identity.LastKnownAccountUuid,
                StringComparison.OrdinalIgnoreCase
            )
            || !ClaudeDesktopIdentityReader.HasSameTokenCandidates(identity.TokenCandidates, snapshotLease.TokenCandidates)) {
            throw new SwitchOperationException("source_snapshot_changed_after_identity_read");
        }

        if (!await snapshotLease.ValidateAsync(cancellationToken).ConfigureAwait(false)) {
            throw new SwitchOperationException("source_snapshot_unstable");
        }

        await RequireDesktopStoppedAsync(
            installation,
            cancellationToken,
            "desktop_restarted_before_source_save"
        ).ConfigureAwait(false);
        if (!await snapshotLease.ValidateAsync(cancellationToken).ConfigureAwait(false)) {
            throw new SwitchOperationException("source_snapshot_unstable_before_save");
        }

        return await SaveCapturedSourceAsync(
            catalog,
            sourceProfile,
            snapshot,
            maskedEmail,
            cancellationToken
        ).ConfigureAwait(false);
    }

    private async Task<SwitchResult> RollBackAfterFailureAsync(
        ClaudeSwitchTransaction transaction,
        IClaudeDesktopInstallationLocator.Installation installation,
        string failureCode
    ) {
        if (!HasTrustedProvenance(transaction) || !BindingsEqual(transaction.RootBinding, installation.Binding)) {
            return new(SwitchOutcome.RecoveryRequired, transaction, "無法自動確認上次切換的狀態，資料已保留。");
        }

        try {
            IClaudeAccountRepository.Snapshot catalog = await accountRepository
                .LoadProfilesAsync(CancellationToken.None)
                .ConfigureAwait(false);
            ClaudeAccountProfile? sourceProfile = catalog.Profiles.FirstOrDefault(
                profile => profile.ProfileId == transaction.SourceProfileId
            );
            ClaudeAccountProfile? targetProfile = catalog.Profiles.FirstOrDefault(
                profile => profile.ProfileId == transaction.TargetProfileId
            );
            if (sourceProfile is null) {
                throw new SwitchOperationException("source_profile_missing");
            }
            if (targetProfile is null) {
                throw new SwitchOperationException("target_profile_missing");
            }
            if (sourceProfile.ProfileRevision != transaction.SourceProfileRevision
                && !((transaction.RecoveryFromStage ?? transaction.CurrentStage) == ClaudeSwitchTransaction.Stage.DesktopClosed
                    && transaction.SourceProfileRevision < long.MaxValue
                    && sourceProfile.ProfileRevision == transaction.SourceProfileRevision + 1)) {
                throw new SwitchOperationException("source_profile_revision_changed");
            }
            if (targetProfile.ProfileRevision != transaction.TargetProfileRevision) {
                throw new SwitchOperationException("target_profile_revision_changed");
            }
            if (sourceProfile.BindingId != transaction.RootBinding!.Source.BindingId) {
                throw new SwitchOperationException("source_profile_binding_changed");
            }
            if (targetProfile.BindingId != transaction.RootBinding.Source.BindingId) {
                throw new SwitchOperationException("target_profile_binding_changed");
            }

            IClaudeDesktopProcessController.ProcessStatus processStatus = await ReadProcessStatusAsync(
                installation,
                CancellationToken.None
            ).ConfigureAwait(false);
            if (processStatus == IClaudeDesktopProcessController.ProcessStatus.Unknown) {
                throw new SwitchOperationException("process_status_unknown");
            }

            if (processStatus == IClaudeDesktopProcessController.ProcessStatus.Running) {
                bool closed = await processController
                    .CloseGracefullyAsync(installation, CloseTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!closed) {
                    throw new SwitchOperationException("rollback_close_timeout");
                }

                if (await ReadProcessStatusAsync(installation, CancellationToken.None).ConfigureAwait(false)
                    != IClaudeDesktopProcessController.ProcessStatus.NotRunning) {
                    throw new SwitchOperationException("rollback_process_still_running");
                }
            }

            ClaudeSwitchTransaction.Stage recoveryStage = transaction.RecoveryFromStage
                ?? transaction.CurrentStage;
            IClaudeDesktopInstallationLocator.LocateResult rebound = await installationLocator
                .ResolveBoundAsync(transaction.RootBinding!, CancellationToken.None).ConfigureAwait(false);
            if (rebound.Status != IClaudeDesktopInstallationLocator.LocateStatus.Found || rebound.Installation is null
                || !BindingsEqual(rebound.Installation.Binding, transaction.RootBinding)) {
                throw new SwitchOperationException("rollback_binding_revalidation_failed");
            }

            installation = rebound.Installation;
            bool historyPreparationOnly = historyService is not null && IsHistoryPreparationOnlyStage(recoveryStage);
            if (historyPreparationOnly) {
                await historyService!.CommitAsync(
                    installation,
                    transaction.TransactionId,
                    ClaudeSessionHistoryCleanupIntent.Preparation,
                    CancellationToken.None
                ).ConfigureAwait(false);
            } else if (historyService is not null && HasHistoryApplyStarted(recoveryStage)) {
                if (targetProfile is null) {
                    throw new SwitchOperationException("target_profile_missing");
                }

                if (!await EnsureRollbackWriteSafeAsync(installation).ConfigureAwait(false)) {
                    throw new SwitchOperationException("rollback_process_not_stopped_before_history_restore");
                }

                await historyService.RestoreTargetAsync(
                    installation,
                    targetProfile,
                    transaction.TransactionId,
                    CancellationToken.None
                ).ConfigureAwait(false);
            }

            if (HasAuthApplyStarted(recoveryStage)) {
                ClaudeAccountProfile.DesktopSnapshot? sourceSnapshot = await accountRepository
                    .LoadDesktopSnapshotAsync(sourceProfile.ProfileId, CancellationToken.None)
                    .ConfigureAwait(false);
                if (sourceSnapshot is null
                    || !IsProfileSnapshot(sourceProfile, sourceSnapshot, installation)
                    || !HasCompleteCookieSidecarSnapshot(sourceSnapshot)) {
                    throw new SwitchOperationException("source_snapshot_invalid");
                }

                if (!await EnsureRollbackWriteSafeAsync(installation).ConfigureAwait(false)) {
                    throw new SwitchOperationException("rollback_process_not_stopped_before_auth_restore");
                }

                await profileStore.RestoreTransactionAsync(installation, transaction.TransactionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await processController.LaunchAsync(installation, CancellationToken.None).ConfigureAwait(false);
            ClaudeDesktopIdentityReader.Result restoredIdentity = await identityReader
                .ReadAsync(installation, catalog.Profiles, CancellationToken.None)
                .ConfigureAwait(false);
            if (!MatchesProfile(restoredIdentity, sourceProfile)) {
                throw new SwitchOperationException("source_restore_unverified");
            }

            if (historyService is not null && HasHistoryApplyStarted(recoveryStage)) {
                await historyService.CommitAsync(
                    installation,
                    transaction.TransactionId,
                    ClaudeSessionHistoryCleanupIntent.Preparation,
                    CancellationToken.None
                ).ConfigureAwait(false);
            }

            if (recoveryStage >= ClaudeSwitchTransaction.Stage.SourceCaptured) {
                await profileStore.CommitTransactionAsync(installation, transaction.TransactionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            ClaudeSwitchTransaction rolledBack = await SaveStageAsync(
                transaction,
                ClaudeSwitchTransaction.Stage.RolledBack,
                failureCode,
                CancellationToken.None
            ).ConfigureAwait(false);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return new(SwitchOutcome.FailedRolledBack, rolledBack,
                "Claude 帳號切換失敗，原來源帳號已還原並重新驗證。");
        } catch (Exception exception) {
            return await RecordRecoveryRequiredAsync(transaction, GetRollbackFailureCode(exception))
                .ConfigureAwait(false);
        }
    }

    private static string GetRollbackFailureCode(Exception exception) {
        string stepCode = exception is SwitchOperationException operationException
            ? operationException.FailureCode
            : exception.GetType().Name;
        const string prefix = "rollback:";
        int length = Math.Min(stepCode.Length, 96 - prefix.Length);
        char[] safeCode = new char[length];
        for (int index = 0; index < length; index++) {
            char character = stepCode[index];
            safeCode[index] = char.IsAsciiLetterOrDigit(character) || character is '_' or '-'
                ? character
                : '-';
        }

        return prefix + new string(safeCode);
    }

    private async Task<SwitchResult> RecordRecoveryRequiredAsync(
        ClaudeSwitchTransaction transaction,
        string failureCode
    ) {
        if (!HasTrustedProvenance(transaction)) {
            return new(SwitchOutcome.RecoveryRequired, transaction, "無法自動確認上次切換的狀態，資料已保留。");
        }

        ClaudeSwitchTransaction pending = transaction with {
            RecoveryFromStage = transaction.RecoveryFromStage ?? transaction.CurrentStage,
            CurrentStage = ClaudeSwitchTransaction.Stage.RecoveryRequired,
            UpdatedAtUtc = clock.UtcNow,
            FailureCode = failureCode
        };
        try {
            await accountRepository.SaveSwitchTransactionAsync(pending, CancellationToken.None)
                .ConfigureAwait(false);
        } catch (Exception) {
            pending = transaction with {
                CurrentStage = ClaudeSwitchTransaction.Stage.RecoveryRequired,
                UpdatedAtUtc = clock.UtcNow,
                FailureCode = "journal_save_failed"
            };
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        string message = IsProcessFailureCode(failureCode)
            ? "復原未完成：Claude Desktop 仍在執行或無法確認已結束，請先正常結束 Claude Desktop 後按「重試復原」。"
            : pending.FailureCode?.StartsWith("rollback:", StringComparison.Ordinal) == true
                ? "復原未完成，請稍後重試；仍失敗時請保留 Claude Desktop 資料。"
                : "切換尚未完成。請保留 Claude Desktop 資料，並先完成復原再切換。";
        return new(SwitchOutcome.RecoveryRequired, pending, message);
    }

    private static bool IsProcessFailureCode(string failureCode) {
        const string rollbackPrefix = "rollback:";
        string stepCode = failureCode.StartsWith(rollbackPrefix, StringComparison.Ordinal)
            ? failureCode[rollbackPrefix.Length..]
            : failureCode;
        return stepCode is "process_status_unknown"
            or "rollback_close_timeout"
            or "rollback_process_still_running"
            or "rollback_process_not_stopped_before_history_restore"
            or "rollback_process_not_stopped_before_auth_restore";
    }

    private async Task<ClaudeSwitchTransaction> SaveStageAsync(
        ClaudeSwitchTransaction transaction,
        ClaudeSwitchTransaction.Stage stage,
        string? failureCode,
        CancellationToken cancellationToken
    ) {
        ClaudeSwitchTransaction updated = transaction with {
            CurrentStage = stage,
            UpdatedAtUtc = clock.UtcNow,
            FailureCode = failureCode,
            RecoveryFromStage = stage == ClaudeSwitchTransaction.Stage.RecoveryRequired
                ? transaction.RecoveryFromStage ?? transaction.CurrentStage
                : transaction.RecoveryFromStage
        };
        await accountRepository.SaveSwitchTransactionAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private bool IsProfileSnapshot(
        ClaudeAccountProfile profile,
        ClaudeAccountProfile.DesktopSnapshot snapshot,
        IClaudeDesktopInstallationLocator.Installation installation
    ) {
        return profile.ProvenanceSchemaVersion == 1 && snapshot.ProvenanceSchemaVersion == 1
            && profile.BindingId == snapshot.RootBinding?.Source.BindingId
            && BindingsEqual(snapshot.RootBinding, installation.Binding)
            && string.Equals(profile.WindowsUserSid, snapshot.WindowsUserSid, StringComparison.Ordinal)
            && string.Equals(profile.InstallationId, snapshot.InstallationId, StringComparison.Ordinal)
            && string.Equals(profile.InstallationId, installation.InstallationId, StringComparison.Ordinal)
            && string.Equals(profile.DataRootFingerprint, snapshot.DataRootFingerprint, StringComparison.Ordinal)
            && string.Equals(profile.DataRootFingerprint, installation.DataRootFingerprint, StringComparison.Ordinal)
            && string.Equals(
                profile.LocalStateEncryptedKeySha256,
                snapshot.LocalStateEncryptedKeySha256,
                StringComparison.Ordinal
            )
            && IsSha256(snapshot.LocalStateEncryptedKeySha256)
            && string.Equals(profile.AccountUuid, snapshot.LastKnownAccountUuid, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IClaudeDesktopInstallationLocator.LocateResult> ResolveProfileInstallationAsync(
        Guid profileId,
        bool allowRunningObservation,
        CancellationToken cancellationToken
    ) {
        ClaudeAccountProfile.DesktopSnapshot? snapshot = await accountRepository.LoadDesktopSnapshotAsync(profileId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot?.ProvenanceSchemaVersion != 1 || snapshot.RootBinding is not ClaudeDesktopRootBinding binding) {
            return new(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, null, "profile-source-unknown");
        }

        if (allowRunningObservation) {
            IClaudeDesktopInstallationLocator.ObservationResult observed = await installationLocator.ObserveRunningAsync(cancellationToken)
                .ConfigureAwait(false);
            if (observed.Status == IClaudeDesktopInstallationLocator.LocateStatus.Found
                && observed.Observation is ClaudeDesktopRootBinding.Observation source) {
                ClaudeDesktopRootBinding.Observation comparable = source with {
                    BindingId = binding.Source.BindingId,
                    Processes = binding.Source.Processes
                };
                if (!ClaudeDesktopIdentityReader.HasSameSource(comparable, binding.Source)) {
                    return new(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, null, "observed-source-binding-mismatch");
                }

                return new(IClaudeDesktopInstallationLocator.LocateStatus.Found,
                    new(source.InstallationId, source.Kind, source.DataRoot, snapshot.DataRootFingerprint,
                        source.AppUserModelId, source.ExecutablePath, source, binding), null);
            }

            if (observed.Status is IClaudeDesktopInstallationLocator.LocateStatus.Ambiguous
                or IClaudeDesktopInstallationLocator.LocateStatus.Unsupported) {
                return new(observed.Status, null, observed.FailureCode);
            }
        }

        return await installationLocator.ResolveBoundAsync(binding, cancellationToken).ConfigureAwait(false);
    }

    private static bool HasTrustedProvenance(ClaudeSwitchTransaction transaction) {
        return transaction.ProvenanceSchemaVersion == 1 && transaction.TransactionId != Guid.Empty
            && transaction.RootBinding is ClaudeDesktopRootBinding binding && binding.Source.SchemaVersion == 1
            && binding.Source.BindingId != Guid.Empty
            && string.Equals(binding.Source.InstallationId, transaction.InstallationId, StringComparison.Ordinal);
    }

    private static bool BindingsEqual(ClaudeDesktopRootBinding? left, ClaudeDesktopRootBinding? right) {
        return left is not null && right is not null
            && string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right), StringComparison.Ordinal);
    }

    private static bool MatchesProfile(
        ClaudeDesktopIdentityReader.Result identity,
        ClaudeAccountProfile profile
    ) {
        return identity.Status == ClaudeAccountService.IdentityStatus.Verified
            && SameIdentity(profile.AccountUuid, profile.OrganizationUuid, identity.AccountUuid, identity.OrganizationUuid);
    }

    private static bool SameIdentity(
        string expectedAccountUuid,
        string expectedOrganizationUuid,
        string? actualAccountUuid,
        string? actualOrganizationUuid
    ) {
        return string.Equals(expectedAccountUuid, actualAccountUuid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(expectedOrganizationUuid, actualOrganizationUuid, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHistoryPreparationOnlyStage(ClaudeSwitchTransaction.Stage stage) {
        return stage is ClaudeSwitchTransaction.Stage.HistoryPreparationStarted
            or ClaudeSwitchTransaction.Stage.HistoryPrepared;
    }

    private static bool HasHistoryApplyStarted(ClaudeSwitchTransaction.Stage stage) {
        return stage is ClaudeSwitchTransaction.Stage.HistoryApplyStarted
            or ClaudeSwitchTransaction.Stage.HistoryApplied
            or ClaudeSwitchTransaction.Stage.TargetAuthApplyStarted
            or ClaudeSwitchTransaction.Stage.TargetAuthApplied
            or ClaudeSwitchTransaction.Stage.TargetLaunchStarted
            or ClaudeSwitchTransaction.Stage.TargetLaunched
            or ClaudeSwitchTransaction.Stage.TargetVerified;
    }

    private static bool HasAuthApplyStarted(ClaudeSwitchTransaction.Stage stage) {
        return stage is ClaudeSwitchTransaction.Stage.TargetAuthApplyStarted
            or ClaudeSwitchTransaction.Stage.TargetAuthApplied
            or ClaudeSwitchTransaction.Stage.TargetLaunchStarted
            or ClaudeSwitchTransaction.Stage.TargetLaunched
            or ClaudeSwitchTransaction.Stage.TargetVerified;
    }

    private static bool IsSha256(string? value) {
        if (value is null || value.Length != 64) {
            return false;
        }

        foreach (char character in value) {
            if (!char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)) {
                return false;
            }
        }

        return true;
    }

    private static bool HasCompleteCookieSidecarSnapshot(ClaudeAccountProfile.DesktopSnapshot snapshot) {
        return snapshot.CookieSidecarSnapshotVersion == 1;
    }

    /// <summary>
    /// 定義切換預覽可能的狀態。
    /// </summary>
    public enum PreviewOutcome {
        /// <summary>目前身份、profile、安全綁定與 history 均可安全切換。</summary>
        Ready,

        /// <summary>有未終止切換交易待復原。</summary>
        RecoveryRequired,

        /// <summary>切換已提交，只待清理該筆交易的 history 暫存。</summary>
        CleanupPending,

        /// <summary>目前 Claude Desktop 身分無法驗證。</summary>
        IdentityUnavailable,

        /// <summary>目前本機帳號 UUID 與 profile API 回應不一致。</summary>
        IdentityMismatch,

        /// <summary>目標 profile 身分無法通過 profile API 驗證。</summary>
        TargetIdentityMismatch,

        /// <summary>目標 profile token 需要重新驗證。</summary>
        ReauthenticationRequired,

        /// <summary>目標 profile 識別碼不存在。</summary>
        TargetMissing,

        /// <summary>目標 profile snapshot 不存在。</summary>
        TargetSnapshotMissing,

        /// <summary>目標 snapshot 的 SID、資料根或 Local State key 綁定不符。</summary>
        TargetBindingMismatch,

        /// <summary>來源 snapshot 的 SID、資料根或 Local State key 綁定不符。</summary>
        SourceBindingMismatch,

        /// <summary>目前 profile 已是目標帳號。</summary>
        SameProfile,

        /// <summary>來源 Code Session 或 transcript 目前無法驗證。</summary>
        HistoryUnavailable,

        /// <summary>找不到唯一 Desktop 安裝。</summary>
        InstallationUnavailable,

        /// <summary>Desktop 安裝型態不支援。</summary>
        UnsupportedInstallation,

        /// <summary>存在多個安裝候選。</summary>
        AmbiguousInstallation,

        /// <summary>目前無法確認 Desktop 程序狀態。</summary>
        ProcessStatusUnknown,

        /// <summary>無法載入交易必要資料。</summary>
        Blocked
    }

    /// <summary>
    /// 提供帳號切換 Dialog 顯示的 profile、Session 數量與確認內容。
    /// </summary>
    /// <param name="Outcome">預覽狀態。</param>
    /// <param name="SourceProfile">目前已驗證的來源 profile。</param>
    /// <param name="TargetProfile">使用者選取的目標 profile。</param>
    /// <param name="SourceSessionCount">來源 Code Session 數量。</param>
    /// <param name="TargetSessionCount">目標既有 Code Session 數量。</param>
    /// <param name="RequiresHistoryOverwrite">是否會以來源完整清單覆蓋目標清單。</param>
    /// <param name="CanProceed">是否可顯示最後確認動作。</param>
    /// <param name="Message">安全且可供畫面顯示的確認或阻擋說明。</param>
    /// <param name="PreviewedAtUtc">預覽建立時間。</param>
    /// <param name="Transaction">阻擋預覽的待復原交易。</param>
    public sealed record SwitchPreview(
        PreviewOutcome Outcome,
        ClaudeAccountProfile? SourceProfile,
        ClaudeAccountProfile? TargetProfile,
        int? SourceSessionCount,
        int? TargetSessionCount,
        bool RequiresHistoryOverwrite,
        bool CanProceed,
        string Message,
        DateTimeOffset PreviewedAtUtc,
        ClaudeSwitchTransaction? Transaction
    ) {
        internal static SwitchPreview Failed(
            PreviewOutcome outcome,
            ClaudeAccountProfile? sourceProfile,
            ClaudeAccountProfile? targetProfile,
            bool requiresHistoryOverwrite,
            string message
        ) {
            return new(
                outcome,
                sourceProfile,
                targetProfile,
                null,
                null,
                requiresHistoryOverwrite,
                false,
                message,
                DateTimeOffset.UtcNow,
                null
            );
        }
    }

    /// <summary>
    /// 定義 Claude Desktop 切換及復原的結果。
    /// </summary>
    public enum SwitchOutcome {
        /// <summary>目標 Desktop 身分通過 profile API 核對，交易已提交。</summary>
        Succeeded,

        /// <summary>切換失敗，來源帳號已還原並重新驗證。</summary>
        FailedRolledBack,

        /// <summary>來源回復失敗或交易 journal 尚未終止。</summary>
        RecoveryRequired,

        /// <summary>切換已提交，只待清理該筆交易的 history 暫存。</summary>
        CleanupPending,

        /// <summary>目前狀態不允許開始交易。</summary>
        Blocked,

        /// <summary>確認內容過期、沒有待復原交易或 journal 無法建立。</summary>
        Rejected
    }

    /// <summary>
    /// 提供切換或回復結果與交易狀態。
    /// </summary>
    /// <param name="Outcome">操作結果。</param>
    /// <param name="Transaction">最後一次保存的 durable journal。</param>
    /// <param name="Message">安全且可供畫面顯示的結果說明。</param>
    public sealed record SwitchResult(
        SwitchOutcome Outcome,
        ClaudeSwitchTransaction? Transaction,
        string Message
    );

    private sealed record SavedProfileVerification(bool Verified, bool ReauthenticationRequired, string Message);

    private sealed class SwitchOperationException : Exception {
        public SwitchOperationException(string failureCode) {
            FailureCode = failureCode;
        }

        public string FailureCode { get; }
    }
}
