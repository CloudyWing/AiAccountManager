using System.Security.Cryptography;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Runtime;
using CloudyWing.AiAccountManager.Application.Security;

namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 載入已保存的 Claude profile、辨識目前 Desktop 身分並保存目前帳號。
/// </summary>
public sealed class ClaudeAccountService {
    private readonly IClaudeAccountRepository accountRepository;
    private readonly IClaudeDesktopInstallationLocator installationLocator;
    private readonly IClaudeDesktopProcessController processController;
    private readonly IClaudeDesktopProfileStore profileStore;
    private readonly ClaudeDesktopIdentityReader identityReader;
    private readonly ClaudeOperationGate operationGate;
    private readonly IClock clock;
    private readonly object stateLock = new();
    private PageState currentPageState;
    private ClaudeDesktopRootBinding.Observation? pendingObservation;

    /// <summary>
    /// 建立 Claude profile 與 Desktop 身分服務。
    /// </summary>
    /// <param name="accountRepository">Claude profile 與快照存放區。</param>
    /// <param name="installationLocator">Claude Desktop 安裝定位器。</param>
    /// <param name="processController">Claude Desktop 程序狀態來源。</param>
    /// <param name="profileStore">提供 lockless Desktop 身分讀取、穩定 auth／Cookie 快照與安全還原的 adapter。</param>
    /// <param name="oauthClient">Claude OAuth profile API client。</param>
    /// <param name="identityFingerprinter">遮蔽 API 回應中的 email。</param>
    /// <param name="operationGate">Claude 作業閘門。</param>
    /// <param name="clock">目前 UTC 時間來源。</param>
    public ClaudeAccountService(
        IClaudeAccountRepository accountRepository,
        IClaudeDesktopInstallationLocator installationLocator,
        IClaudeDesktopProcessController processController,
        IClaudeDesktopProfileStore profileStore,
        IClaudeOAuthClient oauthClient,
        IIdentityFingerprinter identityFingerprinter,
        ClaudeOperationGate operationGate,
        IClock clock
    ) {
        this.accountRepository = accountRepository;
        this.installationLocator = installationLocator;
        this.processController = processController;
        this.profileStore = profileStore;
        this.identityReader = new(profileStore, oauthClient, identityFingerprinter, accountRepository);
        this.operationGate = operationGate;
        this.clock = clock;
        currentPageState = new(
            Array.Empty<ClaudeAccountProfile>(),
            new(IdentityStatus.Unknown, null, null, null, null, null, "尚未讀取 Claude Desktop 身分。"),
            IClaudeDesktopProcessController.ProcessStatus.Unknown,
            null,
            clock.UtcNow
        );
    }

    /// <summary>
    /// 於 profile、目前身分或程序狀態更新時發生。
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// 取得最近一次載入的頁面狀態。
    /// </summary>
    /// <value>不含 token、Cookie 或未遮蔽 email 的 Claude 頁面資料。</value>
    public PageState CurrentPageState {
        get {
            lock (stateLock) {
                return currentPageState;
            }
        }
    }

    /// <summary>
    /// 載入 profiles、目前 Desktop 身分、程序狀態及待復原交易。
    /// </summary>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>最新 Claude 頁面狀態。</returns>
    public async Task<PageState> GetPageStateAsync(CancellationToken cancellationToken) {
        await using ClaudeOperationGate.Lease lease = await operationGate
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false);

        PageState pageState = await LoadPageStateCoreAsync(lease.CancellationToken).ConfigureAwait(false);
        SetPageState(pageState);
        return pageState;
    }

    /// <summary>
    /// 驗證目前 Claude Desktop 身分並保存其 auth／Cookie snapshot。
    /// </summary>
    /// <param name="alias">可選的 profile 別名。</param>
    /// <param name="cancellationToken">取消寫入前作業的權杖。</param>
    /// <returns>保存狀態、profile 及安全的結果訊息。</returns>
    public async Task<CaptureResult> CaptureCurrentProfileAsync(
        string? alias,
        CancellationToken cancellationToken
    ) {
        await using ClaudeOperationGate.Lease lease = await operationGate
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false);

        CancellationToken operationCancellationToken = lease.CancellationToken;
        IClaudeAccountRepository.Snapshot catalog = await accountRepository
            .LoadProfilesAsync(operationCancellationToken)
            .ConfigureAwait(false);
        ClaudeSwitchTransaction? transaction = await accountRepository
            .LoadSwitchTransactionAsync(operationCancellationToken)
            .ConfigureAwait(false);
        if (IsRecoveryRequired(transaction)) {
            return await CaptureFailedAsync(
                CaptureOutcome.RecoveryRequired,
                "有未完成的 Claude Desktop 復原交易，完成復原前無法保存 profile。",
                operationCancellationToken
            ).ConfigureAwait(false);
        }

        // 頁面載入時可能已記錄觀察結果，仍須確認 Desktop 目前是否執行中，才能提示先正常結束。
        IClaudeDesktopInstallationLocator.ObservationResult running = await installationLocator
            .ObserveRunningAsync(operationCancellationToken)
            .ConfigureAwait(false);
        if (running.Status == IClaudeDesktopInstallationLocator.LocateStatus.Found
            && running.Observation is not null && running.Installation is not null
            && running.Installation.Observation == running.Observation) {
            pendingObservation = running.Observation;
            IClaudeDesktopProcessController.ProcessStatus runningStatus = await ReadProcessStatusAsync(
                running.Installation,
                operationCancellationToken
            ).ConfigureAwait(false);
            if (runningStatus != IClaudeDesktopProcessController.ProcessStatus.NotRunning) {
                return await CaptureFailedAsync(
                    runningStatus == IClaudeDesktopProcessController.ProcessStatus.Running
                        ? CaptureOutcome.DesktopRunning
                        : CaptureOutcome.ProcessStatusUnknown,
                    "已辨識 Claude Desktop，請先正常結束後再保存帳號。",
                    operationCancellationToken
                ).ConfigureAwait(false);
            }
        }

        IClaudeDesktopInstallationLocator.LocateResult locateResult = await ResolveStoppedSourceAsync(
            catalog.Profiles,
            operationCancellationToken
        ).ConfigureAwait(false);
        if (locateResult.Status != IClaudeDesktopInstallationLocator.LocateStatus.Found
            || locateResult.Installation is null) {
            IdentityStatus identityStatus = locateResult.Status switch {
                IClaudeDesktopInstallationLocator.LocateStatus.Unsupported => IdentityStatus.UnsupportedInstallation,
                IClaudeDesktopInstallationLocator.LocateStatus.Ambiguous => IdentityStatus.AmbiguousInstallation,
                _ => IdentityStatus.Unknown
            };
            return await CaptureFailedAsync(
                CaptureOutcome.IdentityUnavailable,
                "請先開啟 Claude Desktop 供辨識，再正常結束後保存帳號。",
                operationCancellationToken,
                identityStatus
            ).ConfigureAwait(false);
        }

        IClaudeDesktopProcessController.ProcessStatus beforeReadStatus = await ReadProcessStatusAsync(
            locateResult.Installation,
            operationCancellationToken
        ).ConfigureAwait(false);
        if (beforeReadStatus != IClaudeDesktopProcessController.ProcessStatus.NotRunning) {
            CaptureOutcome outcome = beforeReadStatus == IClaudeDesktopProcessController.ProcessStatus.Running
                ? CaptureOutcome.DesktopRunning
                : CaptureOutcome.ProcessStatusUnknown;
            string message = beforeReadStatus == IClaudeDesktopProcessController.ProcessStatus.Running
                ? "請先正常結束 Claude Desktop，再保存目前帳號。"
                : "目前無法確認 Claude Desktop 已停止，未保存帳號。";
            return await CaptureFailedAsync(
                outcome,
                message,
                operationCancellationToken
            ).ConfigureAwait(false);
        }

        ClaudeDesktopIdentityReader.Result? identityResult = null;
        bool identityReadFailed = false;
        try {
            identityResult = await identityReader
                .ReadAsync(locateResult.Installation, catalog.Profiles, operationCancellationToken)
                .ConfigureAwait(false);
        } catch (OperationCanceledException) when (operationCancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            identityReadFailed = true;
        }

        IClaudeDesktopProcessController.ProcessStatus afterReadStatus = await ReadProcessStatusAsync(
            locateResult.Installation,
            operationCancellationToken
        ).ConfigureAwait(false);
        if (afterReadStatus != IClaudeDesktopProcessController.ProcessStatus.NotRunning) {
            CaptureOutcome outcome = afterReadStatus == IClaudeDesktopProcessController.ProcessStatus.Running
                ? CaptureOutcome.DesktopRunning
                : CaptureOutcome.ProcessStatusUnknown;
            string message = afterReadStatus == IClaudeDesktopProcessController.ProcessStatus.Running
                ? "Claude Desktop 在讀取認證快照期間重新啟動，未保存帳號。請正常結束後重試。"
                : "讀取認證快照後無法確認 Claude Desktop 已停止，未保存帳號。";
            return await CaptureFailedAsync(
                outcome,
                message,
                operationCancellationToken
            ).ConfigureAwait(false);
        }

        if (identityReadFailed || identityResult is null) {
            return await CaptureFailedAsync(
                CaptureOutcome.IdentityUnavailable,
                "無法讀取或驗證 Claude Desktop 身分，未保存帳號。",
                operationCancellationToken
            ).ConfigureAwait(false);
        }

        if (identityResult.Status != IdentityStatus.Verified
            || identityResult.AccountUuid is null
            || identityResult.OrganizationUuid is null) {
            return await CaptureFailedAsync(
                CaptureOutcome.IdentityUnavailable,
                identityResult.Message ?? "Claude Desktop 身分尚未通過驗證，未保存帳號。",
                operationCancellationToken,
                identityResult.Status
            ).ConfigureAwait(false);
        }

        IClaudeAccountRepository.SaveResult saveResult;
        ClaudeAccountProfile profile;
        try {
            await using IClaudeDesktopProfileStore.StableSnapshotLease snapshotLease = await profileStore
                .CaptureStableSnapshotAsync(locateResult.Installation, operationCancellationToken)
                .ConfigureAwait(false);
            ClaudeAccountProfile.DesktopSnapshot desktopSnapshot = snapshotLease.Snapshot;
            if (desktopSnapshot.CookieSidecarSnapshotVersion != 1
                || !IsBoundToInstallation(desktopSnapshot, locateResult.Installation)
                || !string.Equals(
                    desktopSnapshot.LastKnownAccountUuid,
                    identityResult.LastKnownAccountUuid,
                    StringComparison.OrdinalIgnoreCase
                )
                || !ClaudeDesktopIdentityReader.HasSameTokenCandidates(
                    identityResult.TokenCandidates,
                    snapshotLease.TokenCandidates
                )) {
                return await CaptureFailedAsync(
                    CaptureOutcome.IdentityUnavailable,
                    "Claude Desktop 認證檔在身分驗證後發生變動，未保存帳號。",
                    operationCancellationToken,
                    IdentityStatus.Unknown
                ).ConfigureAwait(false);
            }

            if (!await snapshotLease.ValidateAsync(operationCancellationToken).ConfigureAwait(false)) {
                return await CaptureFailedAsync(
                    CaptureOutcome.BindingMismatch,
                    "Claude Desktop 認證檔案集合或快照內容已變動，未保存帳號。",
                    operationCancellationToken,
                    IdentityStatus.ReauthenticationRequired
                ).ConfigureAwait(false);
            }

            ClaudeDesktopRootBinding? binding = locateResult.Installation.Binding;
            if (binding is null && locateResult.Installation.Observation is ClaudeDesktopRootBinding.Observation observation) {
                binding = new(observation, desktopSnapshot.LocalStateEncryptedKeySha256);
            }

            if (binding is null || !SnapshotMatchesSource(desktopSnapshot, binding)) {
                return await CaptureFailedAsync(
                    CaptureOutcome.BindingMismatch,
                    "Desktop 資料來源或 Local State key 已變動，未保存帳號。",
                    operationCancellationToken,
                    IdentityStatus.Unknown
                ).ConfigureAwait(false);
            }

            ClaudeAccountProfile? existingProfile = null;
            Guid? savedBindingId = null;
            foreach (ClaudeAccountProfile candidate in catalog.Profiles.Where(profile =>
                profile.ProvenanceSchemaVersion == 1
                && profile.BindingId is not null
            )) {
                ClaudeAccountProfile.DesktopSnapshot? existingSnapshot = await accountRepository
                    .LoadDesktopSnapshotAsync(candidate.ProfileId, operationCancellationToken)
                    .ConfigureAwait(false);
                if (existingSnapshot is null) {
                    continue;
                }

                try {
                    if (existingSnapshot.ProvenanceSchemaVersion != 1
                        || existingSnapshot.RootBinding is not ClaudeDesktopRootBinding existingBinding
                        || candidate.BindingId != existingBinding.Source.BindingId
                        || !SnapshotMatchesSource(existingSnapshot, existingBinding)) {
                        continue;
                    }

                    if (ClaudeDesktopIdentityReader.HasSameSource(binding.Source, existingBinding.Source)
                        && !string.Equals(
                            binding.LocalStateEncryptedKeySha256,
                            existingBinding.LocalStateEncryptedKeySha256,
                            StringComparison.OrdinalIgnoreCase
                        )) {
                        return await CaptureFailedAsync(
                            CaptureOutcome.BindingMismatch,
                            "Claude Desktop 的 Local State key 與已保存來源不同，未保存帳號。",
                            operationCancellationToken,
                            IdentityStatus.Unknown
                        ).ConfigureAwait(false);
                    }

                    if (!SameSource(binding, existingBinding)) {
                        continue;
                    }

                    IClaudeDesktopInstallationLocator.LocateResult existingResolution = await installationLocator
                        .ResolveBoundAsync(existingBinding, operationCancellationToken)
                        .ConfigureAwait(false);
                    if (existingResolution.Status != IClaudeDesktopInstallationLocator.LocateStatus.Found
                        || existingResolution.Installation?.Binding is null) {
                        continue;
                    }

                    if (savedBindingId is Guid previousBindingId && previousBindingId != existingBinding.Source.BindingId) {
                        return await CaptureFailedAsync(
                            CaptureOutcome.BindingMismatch,
                            "多個已保存來源綁定同時符合目前安裝，未覆寫任何資料。",
                            operationCancellationToken,
                            IdentityStatus.Unknown
                        ).ConfigureAwait(false);
                    }

                    savedBindingId = existingBinding.Source.BindingId;
                    binding = existingBinding;
                    if (!SameIdentity(candidate, identityResult.AccountUuid, identityResult.OrganizationUuid)) {
                        continue;
                    }

                    if (existingProfile is not null) {
                        return await CaptureFailedAsync(
                            CaptureOutcome.BindingMismatch,
                            "多個已保存 profile 同時符合目前身分及來源，未覆寫任何資料。",
                            operationCancellationToken,
                            IdentityStatus.Unknown
                        ).ConfigureAwait(false);
                    }

                    existingProfile = candidate;
                } finally {
                    ClearSnapshotBytes(existingSnapshot);
                }
            }

            desktopSnapshot = desktopSnapshot with { ProvenanceSchemaVersion = 1, RootBinding = binding };

            profile = new(
                existingProfile?.ProfileId ?? Guid.NewGuid(),
                NormalizeAlias(alias) ?? existingProfile?.Alias,
                identityResult.MaskedEmail ?? existingProfile?.MaskedEmail ?? string.Empty,
                identityResult.AccountUuid,
                identityResult.OrganizationUuid,
                locateResult.Installation.InstallationId,
                locateResult.Installation.DataRootFingerprint,
                desktopSnapshot.WindowsUserSid,
                desktopSnapshot.LocalStateEncryptedKeySha256,
                existingProfile is null ? 1 : checked(existingProfile.ProfileRevision + 1),
                existingProfile?.CreatedAtUtc ?? clock.UtcNow,
                clock.UtcNow,
                ClaudeAccountProfile.VerificationStatus.Verified,
                binding.Source.BindingId,
                1
            );

            IClaudeDesktopProcessController.ProcessStatus beforeSaveStatus = await ReadProcessStatusAsync(
                locateResult.Installation,
                operationCancellationToken
            ).ConfigureAwait(false);
            if (beforeSaveStatus != IClaudeDesktopProcessController.ProcessStatus.NotRunning) {
                CaptureOutcome outcome = beforeSaveStatus == IClaudeDesktopProcessController.ProcessStatus.Running
                    ? CaptureOutcome.DesktopRunning
                    : CaptureOutcome.ProcessStatusUnknown;
                string message = beforeSaveStatus == IClaudeDesktopProcessController.ProcessStatus.Running
                    ? "Claude Desktop 在保存前重新啟動，未保存帳號。請正常結束後重試。"
                    : "保存前無法確認 Claude Desktop 已停止，未保存帳號。";
                return await CaptureFailedAsync(
                    outcome,
                    message,
                    operationCancellationToken
                ).ConfigureAwait(false);
            }

            if (!await snapshotLease.ValidateAsync(operationCancellationToken).ConfigureAwait(false)) {
                return await CaptureFailedAsync(
                    CaptureOutcome.BindingMismatch,
                    "Claude Desktop 在保存前認證檔案集合或快照內容已變動，未保存帳號。",
                    operationCancellationToken,
                    IdentityStatus.ReauthenticationRequired
                ).ConfigureAwait(false);
            }

            saveResult = await accountRepository
                .SaveProfileAsync(profile, desktopSnapshot, catalog.Sha256, operationCancellationToken)
                .ConfigureAwait(false);
        } catch (OperationCanceledException) when (operationCancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return await CaptureFailedAsync(
                CaptureOutcome.Failed,
                "無法安全擷取或保存 Claude Desktop 認證快照。",
                operationCancellationToken
            ).ConfigureAwait(false);
        }

        if (!saveResult.Saved || saveResult.Snapshot is null) {
            return await CaptureFailedAsync(
                saveResult.Conflict ? CaptureOutcome.Conflict : CaptureOutcome.Failed,
                saveResult.Conflict
                    ? "Claude profile 清單已在其他作業中更新。請重新載入後再保存。"
                    : "無法安全保存 Claude profile。",
                operationCancellationToken
            ).ConfigureAwait(false);
        }

        ClaudeAccountProfile savedProfile = saveResult.Snapshot.Profiles.FirstOrDefault(
            candidate => candidate.ProfileId == profile.ProfileId
        ) ?? profile;
        pendingObservation = null;
        PageState state = CurrentPageState with {
            Profiles = saveResult.Snapshot.Profiles,
            CurrentIdentity = new(
                IdentityStatus.Verified,
                savedProfile.AccountUuid,
                savedProfile.OrganizationUuid,
                savedProfile.MaskedEmail,
                clock.UtcNow,
                savedProfile,
                "目前 Claude Desktop 身分已由本機設定與 profile API 確認。"
            ),
            LoadedAtUtc = clock.UtcNow
        };
        SetPageState(state);
        return new(CaptureOutcome.Saved, savedProfile, "Claude Desktop 帳號已保存。");
    }

    /// <summary>
    /// 更新已保存 Claude profile 的別名，不改寫 Desktop snapshot 或 profile 版次。
    /// </summary>
    /// <param name="profileId">要更新的 profile 識別碼。</param>
    /// <param name="alias">新的別名；空白表示清除別名。</param>
    /// <param name="cancellationToken">取消寫入前作業的權杖。</param>
    /// <returns>保存結果與可供畫面顯示的訊息。</returns>
    public Task<MetadataResult> UpdateAliasAsync(
        Guid profileId,
        string? alias,
        CancellationToken cancellationToken
    ) {
        string? normalizedAlias = NormalizeAlias(alias);
        if (normalizedAlias is { Length: > 80 }) {
            return Task.FromResult(new MetadataResult(false, "帳號別名最多 80 個字元。"));
        }

        return SaveMetadataAsync(
            profileId,
            (profiles, index) => {
                profiles[index] = profiles[index] with { Alias = normalizedAlias };
                return true;
            },
            "已保存 Claude 帳號別名。",
            cancellationToken
        );
    }

    /// <summary>
    /// 將已保存 Claude profile 在清單中上移或下移一個位置。
    /// </summary>
    /// <param name="profileId">要移動的 profile 識別碼。</param>
    /// <param name="direction">移動方向。</param>
    /// <param name="cancellationToken">取消寫入前作業的權杖。</param>
    /// <returns>保存結果與可供畫面顯示的訊息。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="direction" /> 不是支援的移動方向。</exception>
    public Task<MetadataResult> MoveAsync(
        Guid profileId,
        MoveDirection direction,
        CancellationToken cancellationToken
    ) {
        if (!Enum.IsDefined(direction)) {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        return SaveMetadataAsync(
            profileId,
            (profiles, index) => {
                int targetIndex = direction == MoveDirection.Up ? index - 1 : index + 1;
                if (targetIndex < 0 || targetIndex >= profiles.Count) {
                    return false;
                }

                (profiles[index], profiles[targetIndex]) = (profiles[targetIndex], profiles[index]);
                return true;
            },
            null,
            cancellationToken
        );
    }

    /// <summary>
    /// 移除已保存 Claude profile 及其受保護 snapshot，不影響 Claude Desktop 目前的登入狀態。
    /// </summary>
    /// <param name="profileId">要移除的 profile 識別碼。</param>
    /// <param name="cancellationToken">取消寫入前作業的權杖。</param>
    /// <returns>保存結果與可供畫面顯示的訊息。</returns>
    public Task<MetadataResult> RemoveAsync(Guid profileId, CancellationToken cancellationToken) {
        return SaveMetadataAsync(
            profileId,
            (profiles, index) => {
                profiles.RemoveAt(index);
                return true;
            },
            "已移除 Claude 帳號。Claude Desktop 目前的登入狀態不受影響。",
            cancellationToken
        );
    }

    internal static bool IsRecoveryRequired(ClaudeSwitchTransaction? transaction) {
        return transaction is not null
            && transaction.CurrentStage is not ClaudeSwitchTransaction.Stage.Committed
                and not ClaudeSwitchTransaction.Stage.CommittedCleanupPending
                and not ClaudeSwitchTransaction.Stage.RolledBack;
    }

    private async Task<PageState> LoadPageStateCoreAsync(CancellationToken cancellationToken) {
        IClaudeAccountRepository.Snapshot catalog = await accountRepository
            .LoadProfilesAsync(cancellationToken)
            .ConfigureAwait(false);
        ClaudeSwitchTransaction? transaction = await accountRepository
            .LoadSwitchTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (transaction?.CurrentStage is ClaudeSwitchTransaction.Stage.Committed
            or ClaudeSwitchTransaction.Stage.RolledBack) {
            transaction = null;
        }

        IClaudeDesktopInstallationLocator.ObservationResult observationResult = await installationLocator
            .ObserveRunningAsync(cancellationToken)
            .ConfigureAwait(false);
        IClaudeDesktopInstallationLocator.LocateResult locateResult;
        if (observationResult.Status == IClaudeDesktopInstallationLocator.LocateStatus.Found
            && observationResult.Observation is not null
            && observationResult.Installation is not null
            && observationResult.Installation.Observation == observationResult.Observation) {
            pendingObservation = observationResult.Observation;
            locateResult = new(observationResult.Status, observationResult.Installation, null);
        } else {
            locateResult = await ResolveStoppedSourceAsync(catalog.Profiles, cancellationToken).ConfigureAwait(false);
            if (locateResult.Status != IClaudeDesktopInstallationLocator.LocateStatus.Found
                && observationResult.Status == IClaudeDesktopInstallationLocator.LocateStatus.Ambiguous) {
                locateResult = new(observationResult.Status, null, observationResult.FailureCode);
            }
        }
        IClaudeDesktopProcessController.ProcessStatus processStatus = locateResult.Installation is null
            ? IClaudeDesktopProcessController.ProcessStatus.Unknown
            : await ReadProcessStatusAsync(locateResult.Installation, cancellationToken).ConfigureAwait(false);

        CurrentIdentity currentIdentity;
        if (IsRecoveryRequired(transaction)) {
            ClaudeSwitchTransaction recoveryTransaction = transaction!;
            ClaudeAccountProfile? sourceProfile = catalog.Profiles.FirstOrDefault(
                profile => profile.ProfileId == recoveryTransaction.SourceProfileId
            );
            currentIdentity = new(
                IdentityStatus.RecoveryRequired,
                sourceProfile?.AccountUuid,
                sourceProfile?.OrganizationUuid,
                sourceProfile?.MaskedEmail,
                null,
                sourceProfile,
                "有 Claude Desktop 切換交易待復原。完成復原前無法保存 profile 或開始切換。"
            );
        } else if (locateResult.Status != IClaudeDesktopInstallationLocator.LocateStatus.Found
            || locateResult.Installation is null) {
            IdentityStatus status = locateResult.Status switch {
                IClaudeDesktopInstallationLocator.LocateStatus.Unsupported => IdentityStatus.UnsupportedInstallation,
                IClaudeDesktopInstallationLocator.LocateStatus.Ambiguous => IdentityStatus.AmbiguousInstallation,
                _ => IdentityStatus.Unknown
            };
            currentIdentity = new(status, null, null, null, null, null, "請先開啟 Claude Desktop 供辨識，再正常結束後保存帳號。");
        } else {
            try {
                ClaudeDesktopIdentityReader.Result identity = await identityReader
                    .ReadAsync(locateResult.Installation, catalog.Profiles, cancellationToken)
                    .ConfigureAwait(false);
                currentIdentity = new(
                    identity.Status,
                    identity.AccountUuid,
                    identity.OrganizationUuid,
                    identity.MaskedEmail,
                    identity.Status == IdentityStatus.Verified ? clock.UtcNow : null,
                    identity.MatchedProfile,
                    processStatus == IClaudeDesktopProcessController.ProcessStatus.Running
                        ? "已辨識 Claude Desktop，請先正常結束後再保存帳號。"
                        : identity.Message
                );
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception) {
                currentIdentity = new(
                    IdentityStatus.Unknown,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "無法讀取 Claude Desktop 設定或向 profile API 驗證身分。"
                );
            }
        }

        return new(catalog.Profiles, currentIdentity, processStatus, transaction, clock.UtcNow);
    }

    private async Task<IClaudeDesktopInstallationLocator.LocateResult> ResolveStoppedSourceAsync(
        IReadOnlyList<ClaudeAccountProfile> profiles,
        CancellationToken cancellationToken
    ) {
        if (pendingObservation is not null) {
            return await installationLocator
                .ResolveObservationAfterStopAsync(pendingObservation, cancellationToken)
                .ConfigureAwait(false);
        }

        IClaudeDesktopInstallationLocator.LocateResult? resolved = null;
        foreach (ClaudeAccountProfile profile in profiles.Where(candidate =>
            candidate.ProvenanceSchemaVersion == 1 && candidate.BindingId is not null
        )) {
            ClaudeAccountProfile.DesktopSnapshot? snapshot = await accountRepository
                .LoadDesktopSnapshotAsync(profile.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null) {
                continue;
            }

            try {
                if (snapshot.ProvenanceSchemaVersion != 1 || snapshot.RootBinding is null
                    || profile.BindingId != snapshot.RootBinding.Source.BindingId) {
                    continue;
                }

                IClaudeDesktopInstallationLocator.LocateResult candidate = await installationLocator
                    .ResolveBoundAsync(snapshot.RootBinding, cancellationToken)
                    .ConfigureAwait(false);
                if (candidate.Status != IClaudeDesktopInstallationLocator.LocateStatus.Found
                    || candidate.Installation?.Binding is not ClaudeDesktopRootBinding candidateBinding) {
                    continue;
                }

                if (resolved?.Installation?.Binding is ClaudeDesktopRootBinding previousBinding
                    && !SameSource(previousBinding, candidateBinding)) {
                    return new(IClaudeDesktopInstallationLocator.LocateStatus.Ambiguous, null, "multiple-bound-sources");
                }

                resolved ??= candidate;
            } finally {
                ClearSnapshotBytes(snapshot);
            }
        }

        return resolved ?? new(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, null, "observation-required");
    }

    private async Task<CaptureResult> CaptureFailedAsync(
        CaptureOutcome outcome,
        string message,
        CancellationToken cancellationToken,
        IdentityStatus? identityStatus = null
    ) {
        PageState state;
        try {
            state = await LoadPageStateCoreAsync(cancellationToken).ConfigureAwait(false);
            if (identityStatus is IdentityStatus status) {
                state = state with { CurrentIdentity = state.CurrentIdentity with { Status = status, Message = message } };
            } else {
                state = state with { CurrentIdentity = state.CurrentIdentity with { Message = message } };
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            state = CurrentPageState with {
                CurrentIdentity = CurrentPageState.CurrentIdentity with { Message = message },
                LoadedAtUtc = clock.UtcNow
            };
        }

        SetPageState(state);
        return new(outcome, null, message);
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

    private void SetPageState(PageState pageState) {
        lock (stateLock) {
            currentPageState = pageState;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameIdentity(ClaudeAccountProfile profile, string accountUuid, string organizationUuid) {
        return string.Equals(profile.AccountUuid, accountUuid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.OrganizationUuid, organizationUuid, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBoundToInstallation(
        ClaudeAccountProfile.DesktopSnapshot snapshot,
        IClaudeDesktopInstallationLocator.Installation installation
    ) {
        return !string.IsNullOrWhiteSpace(snapshot.WindowsUserSid)
            && string.Equals(snapshot.InstallationId, installation.InstallationId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(snapshot.DataRootFingerprint)
            && string.Equals(snapshot.DataRootFingerprint, installation.DataRootFingerprint, StringComparison.Ordinal)
            && IsSha256(snapshot.LocalStateEncryptedKeySha256);
    }

    private static bool SnapshotMatchesSource(
        ClaudeAccountProfile.DesktopSnapshot snapshot,
        ClaudeDesktopRootBinding binding
    ) {
        ClaudeDesktopRootBinding.Observation source = binding.Source;
        return source.SchemaVersion == 1 && source.BindingId != Guid.Empty
            && string.Equals(snapshot.WindowsUserSid, source.WindowsUserSid, StringComparison.Ordinal)
            && string.Equals(snapshot.InstallationId, source.InstallationId, StringComparison.Ordinal)
            && string.Equals(
                snapshot.CookieRelativePath.Replace('/', '\\'),
                source.CookieRelativePath.Replace('/', '\\'),
                StringComparison.OrdinalIgnoreCase
            )
            && string.Equals(
                snapshot.LocalStateEncryptedKeySha256,
                binding.LocalStateEncryptedKeySha256,
                StringComparison.OrdinalIgnoreCase
            );
    }

    private static bool SameSource(ClaudeDesktopRootBinding left, ClaudeDesktopRootBinding right) {
        return ClaudeDesktopIdentityReader.HasSameSource(left.Source, right.Source)
            && string.Equals(left.LocalStateEncryptedKeySha256, right.LocalStateEncryptedKeySha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void ClearSnapshotBytes(ClaudeAccountProfile.DesktopSnapshot snapshot) {
        CryptographicOperations.ZeroMemory(snapshot.Cookies);
        if (snapshot.CookieJournal is not null) {
            CryptographicOperations.ZeroMemory(snapshot.CookieJournal);
        }

        if (snapshot.CookieWriteAheadLog is not null) {
            CryptographicOperations.ZeroMemory(snapshot.CookieWriteAheadLog);
        }

        if (snapshot.CookieSharedMemory is not null) {
            CryptographicOperations.ZeroMemory(snapshot.CookieSharedMemory);
        }
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

    private async Task<MetadataResult> SaveMetadataAsync(
        Guid profileId,
        Func<List<ClaudeAccountProfile>, int, bool> apply,
        string? successMessage,
        CancellationToken cancellationToken
    ) {
        await using ClaudeOperationGate.Lease lease = await operationGate
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false);

        CancellationToken operationCancellationToken = lease.CancellationToken;
        ClaudeSwitchTransaction? transaction = await accountRepository
            .LoadSwitchTransactionAsync(operationCancellationToken)
            .ConfigureAwait(false);
        if (transaction is not null
            && transaction.CurrentStage is not ClaudeSwitchTransaction.Stage.Committed
                and not ClaudeSwitchTransaction.Stage.RolledBack) {
            return new(false, "有尚未完成的 Claude Desktop 切換交易，完成復原或清理前無法變更帳號清單。");
        }

        IClaudeAccountRepository.Snapshot catalog = await accountRepository
            .LoadProfilesAsync(operationCancellationToken)
            .ConfigureAwait(false);
        List<ClaudeAccountProfile> profiles = [.. catalog.Profiles];
        int index = profiles.FindIndex(profile => profile.ProfileId == profileId);
        if (index < 0) {
            return new(false, "找不到指定的 Claude 帳號；清單沒有變更，請重新載入。");
        }

        if (!apply(profiles, index)) {
            return new(true, null);
        }

        IClaudeAccountRepository.SaveResult save = await accountRepository
            .SaveCatalogMetadataAsync(profiles, catalog.Sha256, operationCancellationToken)
            .ConfigureAwait(false);
        if (!save.Saved || save.Snapshot is null) {
            return new(
                false,
                save.Conflict
                    ? "Claude 帳號清單已在其他位置更新；未保存這次變更，請重新載入後重試。"
                    : "無法保存 Claude 帳號清單；清單沒有變更，請重試。"
            );
        }

        IReadOnlyList<ClaudeAccountProfile> savedProfiles = save.Snapshot.Profiles;
        PageState state;
        lock (stateLock) {
            Guid? matchedProfileId = currentPageState.CurrentIdentity.MatchedProfile?.ProfileId;
            ClaudeAccountProfile? matchedProfile = matchedProfileId is Guid matchedId
                ? savedProfiles.FirstOrDefault(profile => profile.ProfileId == matchedId)
                : null;
            state = currentPageState with {
                Profiles = savedProfiles,
                CurrentIdentity = currentPageState.CurrentIdentity with { MatchedProfile = matchedProfile }
            };
        }

        SetPageState(state);
        return new(true, successMessage);
    }

    private static string? NormalizeAlias(string? alias) {
        string? normalized = alias?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    /// <summary>
    /// 定義目前 Claude 身分辨識狀態。
    /// </summary>
    public enum IdentityStatus {
        /// <summary>本機 Desktop UUID 與 profile API 回應相符。</summary>
        Verified,

        /// <summary>目前無法取得或驗證身分。</summary>
        Unknown,

        /// <summary>Desktop token 已失效或 profile snapshot 需要重新擷取。</summary>
        ReauthenticationRequired,

        /// <summary>本機 Desktop UUID 與 OAuth profile 回應不一致。</summary>
        Mismatch,

        /// <summary>目前安裝版本或型態不受支援。</summary>
        UnsupportedInstallation,

        /// <summary>找到多個安裝候選，無法安全選定。</summary>
        AmbiguousInstallation,

        /// <summary>有未終止切換 journal，需要先完成復原。</summary>
        RecoveryRequired
    }

    /// <summary>
    /// 提供目前 Desktop 身分及其驗證依據。
    /// </summary>
    /// <param name="Status">目前身分狀態。</param>
    /// <param name="AccountUuid">已由 profile API 驗證的帳號 UUID。</param>
    /// <param name="OrganizationUuid">已由 profile API 驗證的組織 UUID。</param>
    /// <param name="MaskedEmail">遮蔽後的 email。</param>
    /// <param name="VerifiedAtUtc">最近一次成功驗證時間。</param>
    /// <param name="MatchedProfile">與目前帳號／組織相符的已保存 profile。</param>
    /// <param name="Message">安全且可供畫面顯示的狀態說明。</param>
    public sealed record CurrentIdentity(
        IdentityStatus Status,
        string? AccountUuid,
        string? OrganizationUuid,
        string? MaskedEmail,
        DateTimeOffset? VerifiedAtUtc,
        ClaudeAccountProfile? MatchedProfile,
        string? Message
    );

    /// <summary>
    /// 提供 Claude 頁面需要的 profile、identity、程序與復原狀態。
    /// </summary>
    /// <param name="Profiles">已保存的 Claude profiles。</param>
    /// <param name="CurrentIdentity">目前 Desktop 身分辨識結果。</param>
    /// <param name="ProcessStatus">獨立讀取的 Claude Desktop 程序狀態。</param>
    /// <param name="PendingTransaction">需要復原的未終止交易。</param>
    /// <param name="LoadedAtUtc">頁面狀態載入時間。</param>
    public sealed record PageState(
        IReadOnlyList<ClaudeAccountProfile> Profiles,
        CurrentIdentity CurrentIdentity,
        IClaudeDesktopProcessController.ProcessStatus ProcessStatus,
        ClaudeSwitchTransaction? PendingTransaction,
        DateTimeOffset LoadedAtUtc
    ) {
        /// <summary>
        /// 判斷是否有待復原交易並應停用新增、覆寫 profile 與切換操作。
        /// </summary>
        public bool RecoveryRequired => (PendingTransaction is not null
                && PendingTransaction.CurrentStage != ClaudeSwitchTransaction.Stage.CommittedCleanupPending)
            || CurrentIdentity.Status == IdentityStatus.RecoveryRequired;

        /// <summary>
        /// 判斷切換已提交且只待清理該交易的 history 暫存。
        /// </summary>
        public bool CleanupPending => PendingTransaction?.CurrentStage
            == ClaudeSwitchTransaction.Stage.CommittedCleanupPending;
    }

    /// <summary>
    /// 定義保存目前 Claude profile 的結果狀態。
    /// </summary>
    public enum CaptureOutcome {
        /// <summary>已保存或更新目前 profile。</summary>
        Saved,

        /// <summary>Claude Desktop 正在執行或於保存期間重新啟動。</summary>
        DesktopRunning,

        /// <summary>目前無法確認 Claude Desktop 程序已停止。</summary>
        ProcessStatusUnknown,

        /// <summary>目前 Desktop 身分無法確認。</summary>
        IdentityUnavailable,

        /// <summary>已有待復原交易。</summary>
        RecoveryRequired,

        /// <summary>現有 profile 綁定與目前 Desktop 安裝不同。</summary>
        BindingMismatch,

        /// <summary>Profile catalog 已被其他作業更新。</summary>
        Conflict,

        /// <summary>保存作業失敗。</summary>
        Failed
    }

    /// <summary>
    /// 回傳保存目前 profile 的狀態與 profile 資料。
    /// </summary>
    /// <param name="Outcome">保存結果。</param>
    /// <param name="Profile">成功保存的 profile。</param>
    /// <param name="Message">安全且可供畫面顯示的結果訊息。</param>
    public sealed record CaptureResult(CaptureOutcome Outcome, ClaudeAccountProfile? Profile, string Message);

    /// <summary>
    /// 定義帳號在清單中的移動方向。
    /// </summary>
    public enum MoveDirection {
        /// <summary>往清單前方移動。</summary>
        Up,

        /// <summary>往清單後方移動。</summary>
        Down
    }

    /// <summary>
    /// 回傳帳號清單變更的結果。
    /// </summary>
    /// <param name="Saved">變更已保存或不需變更時為 <see langword="true" />。</param>
    /// <param name="Message">可供畫面顯示的結果訊息；無需提示時為 <see langword="null" />。</param>
    public sealed record MetadataResult(bool Saved, string? Message);
}
