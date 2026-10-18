using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 驗證 Claude Code Session 索引，並準備將來源列項完整覆蓋至目標帳號。
/// </summary>
public sealed class ClaudeSessionHistorySyncService {
    private readonly IClaudeSessionHistoryStore historyStore;

    /// <summary>
    /// 建立 Claude Code Session history 同步服務。
    /// </summary>
    /// <param name="historyStore">Claude Code account／organization index 存放區。</param>
    public ClaudeSessionHistorySyncService(IClaudeSessionHistoryStore historyStore) {
        this.historyStore = historyStore;
    }

    /// <summary>
    /// 驗證來源與目標索引，取得切換確認 Dialog 所需的 Session 數量。
    /// </summary>
    /// <param name="installation">已唯一定位的 Claude Desktop 安裝。</param>
    /// <param name="sourceProfile">目前已驗證的來源 profile。</param>
    /// <param name="targetProfile">使用者選取的目標 profile。</param>
    /// <param name="cancellationToken">取消唯讀驗證作業的權杖。</param>
    /// <returns>來源／目標 Session 數量與可否覆蓋。</returns>
    public async Task<PreviewResult> PreviewReplacementAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        ClaudeAccountProfile sourceProfile,
        ClaudeAccountProfile targetProfile,
        CancellationToken cancellationToken
    ) {
        if (!HasTrustedProfile(installation, sourceProfile) || !HasTrustedProfile(installation, targetProfile)) {
            return new(false, null, null, IClaudeSessionHistoryStore.ValidationStatus.InvalidIndex, "history-source-binding-unknown");
        }

        IClaudeSessionHistoryStore.ValidationResult source = await historyStore
            .ValidateAccountAsync(
                installation,
                sourceProfile.AccountUuid,
                sourceProfile.OrganizationUuid,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (source.Status != IClaudeSessionHistoryStore.ValidationStatus.Valid) {
            return new(false, source.SessionCount, null, source.Status, source.FailureCode);
        }

        IClaudeSessionHistoryStore.ValidationResult target = await historyStore
            .ValidateAccountAsync(
                installation,
                targetProfile.AccountUuid,
                targetProfile.OrganizationUuid,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (target.Status is not IClaudeSessionHistoryStore.ValidationStatus.Valid
            and not IClaudeSessionHistoryStore.ValidationStatus.MissingDirectory) {
            return new(false, source.SessionCount, target.SessionCount, target.Status, target.FailureCode);
        }

        return new(
            true,
            source.SessionCount,
            target.Status == IClaudeSessionHistoryStore.ValidationStatus.MissingDirectory ? 0 : target.SessionCount,
            IClaudeSessionHistoryStore.ValidationStatus.Valid,
            null
        );
    }

    /// <summary>
    /// 驗證來源並建立來源 staging 與目標索引回復備份。
    /// </summary>
    /// <param name="installation">已唯一定位的 Claude Desktop 安裝。</param>
    /// <param name="sourceProfile">目前已驗證的來源 profile。</param>
    /// <param name="targetProfile">要被來源 Session 清單覆蓋的目標 profile。</param>
    /// <param name="transactionId">共用 Claude Desktop 切換交易識別碼。</param>
    /// <param name="cancellationToken">取消 staging 作業的權杖。</param>
    /// <returns>來源與目標 Session 數量，及 staging 驗證結果。</returns>
    public async Task<PreparationResult> PrepareReplacementAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        ClaudeAccountProfile sourceProfile,
        ClaudeAccountProfile targetProfile,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        if (transactionId == Guid.Empty || !HasTrustedProfile(installation, sourceProfile)
            || !HasTrustedProfile(installation, targetProfile)) {
            return new(IClaudeSessionHistoryStore.ValidationStatus.InvalidIndex, 0, 0, "history-source-binding-unknown");
        }

        IClaudeSessionHistoryStore.PreparationResult result = await historyStore
            .PrepareReplacementAsync(
                installation,
                sourceProfile.AccountUuid,
                sourceProfile.OrganizationUuid,
                targetProfile.AccountUuid,
                targetProfile.OrganizationUuid,
                transactionId,
                cancellationToken
            )
            .ConfigureAwait(false);
        return new(result.Status, result.SourceSessionCount, result.TargetSessionCount, result.FailureCode);
    }

    /// <summary>
    /// 套用指定交易中已備妥的來源索引列項。
    /// </summary>
    /// <param name="installation">已唯一定位的 Claude Desktop 安裝。</param>
    /// <param name="targetProfile">目標 profile。</param>
    /// <param name="transactionId">切換交易識別碼。</param>
    /// <param name="cancellationToken">取消套用作業的權杖。</param>
    public Task ApplyReplacementAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        ClaudeAccountProfile targetProfile,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        if (!HasTrustedProfile(installation, targetProfile) || transactionId == Guid.Empty) {
            return Task.FromException(new InvalidOperationException("Claude history apply requires trusted transaction provenance."));
        }

        return historyStore.ApplyReplacementAsync(
            installation,
            targetProfile.AccountUuid,
            targetProfile.OrganizationUuid,
            transactionId,
            cancellationToken
        );
    }

    /// <summary>
    /// 從交易備份冪等還原目標索引。
    /// </summary>
    /// <param name="installation">已唯一定位的 Claude Desktop 安裝。</param>
    /// <param name="targetProfile">目標 profile。</param>
    /// <param name="transactionId">切換交易識別碼。</param>
    /// <param name="cancellationToken">取消還原作業的權杖。</param>
    public Task RestoreTargetAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        ClaudeAccountProfile targetProfile,
        Guid transactionId,
        CancellationToken cancellationToken
    ) {
        if (!HasTrustedProfile(installation, targetProfile) || transactionId == Guid.Empty) {
            return Task.FromException(new InvalidOperationException("Claude history restore requires trusted transaction provenance."));
        }

        return historyStore.RestoreTargetAsync(
            installation,
            targetProfile.AccountUuid,
            targetProfile.OrganizationUuid,
            transactionId,
            cancellationToken
        );
    }

    /// <summary>
    /// 依已確認的 journal 清理意圖清除交易所用的 staging 與備份。
    /// </summary>
    /// <param name="installation">已唯一定位的 Claude Desktop 安裝。</param>
    /// <param name="transactionId">Application journal 中的交易識別碼。</param>
    /// <param name="cleanupIntent">依 Application journal 確認的清理意圖。</param>
    /// <param name="cancellationToken">取消清理作業的權杖。</param>
    public Task CommitAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid transactionId,
        ClaudeSessionHistoryCleanupIntent cleanupIntent,
        CancellationToken cancellationToken
    ) {
        if (installation.Binding?.Source.SchemaVersion != 1 || transactionId == Guid.Empty) {
            return Task.FromException(new InvalidOperationException("Claude history cleanup requires trusted transaction provenance."));
        }

        return historyStore.CommitAsync(installation, transactionId, cleanupIntent, cancellationToken);
    }

    private static bool HasTrustedProfile(IClaudeDesktopInstallationLocator.Installation installation, ClaudeAccountProfile profile) {
        return profile.ProvenanceSchemaVersion == 1 && profile.BindingId is Guid bindingId
            && bindingId != Guid.Empty && installation.Binding?.Source.SchemaVersion == 1
            && bindingId == installation.Binding.Source.BindingId
            && string.Equals(profile.WindowsUserSid, installation.Binding.Source.WindowsUserSid, StringComparison.Ordinal)
            && string.Equals(profile.InstallationId, installation.InstallationId, StringComparison.Ordinal)
            && string.Equals(profile.DataRootFingerprint, installation.DataRootFingerprint, StringComparison.Ordinal)
            && string.Equals(profile.LocalStateEncryptedKeySha256, installation.Binding.LocalStateEncryptedKeySha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 提供 history 覆蓋前的來源與目標檢視。
    /// </summary>
    /// <param name="CanProceed">來源與目標資料可安全處理。</param>
    /// <param name="SourceSessionCount">來源索引的 Session 數量。</param>
    /// <param name="TargetSessionCount">既有目標索引的 Session 數量。</param>
    /// <param name="Status">驗證狀態。</param>
    /// <param name="FailureCode">安全且不含本機路徑的失敗代碼。</param>
    public sealed record PreviewResult(
        bool CanProceed,
        int? SourceSessionCount,
        int? TargetSessionCount,
        IClaudeSessionHistoryStore.ValidationStatus Status,
        string? FailureCode
    );

    /// <summary>
    /// 提供已建立 staging 與目標備份的結果。
    /// </summary>
    /// <param name="Status">來源索引驗證狀態。</param>
    /// <param name="SourceSessionCount">已複製至 staging 的來源 Session 數量。</param>
    /// <param name="TargetSessionCount">備份的原目標 Session 數量。</param>
    /// <param name="FailureCode">安全且不含本機路徑的失敗代碼。</param>
    public sealed record PreparationResult(
        IClaudeSessionHistoryStore.ValidationStatus Status,
        int SourceSessionCount,
        int TargetSessionCount,
        string? FailureCode
    );
}
