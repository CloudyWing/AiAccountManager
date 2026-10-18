namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 定義 Claude Code account／organization index 驗證、覆蓋與交易復原契約。
/// 寫入、回復及清理使用經重驗的來源綁定，並保留無法確認歸屬的交易資料。
/// </summary>
public interface IClaudeSessionHistoryStore {
    /// <summary>
    /// 唯讀驗證指定帳號的 Code index 與 transcript 指標。
    /// </summary>
    /// <param name="installation">唯一定位的 Desktop 安裝。</param>
    /// <param name="accountUuid">帳號 UUID。</param>
    /// <param name="organizationUuid">組織 UUID。</param>
    /// <param name="cancellationToken">取消驗證作業的權杖。</param>
    /// <returns>來源目錄狀態與有效 Session 數量。</returns>
    Task<ValidationResult> ValidateAccountAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        string accountUuid,
        string organizationUuid,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 驗證來源後建立來源 staging 與目標回復備份，不套用目標資料。
    /// </summary>
    /// <param name="installation">唯一定位的 Desktop 安裝。</param>
    /// <param name="sourceAccountUuid">來源帳號 UUID。</param>
    /// <param name="sourceOrganizationUuid">來源組織 UUID。</param>
    /// <param name="targetAccountUuid">目標帳號 UUID。</param>
    /// <param name="targetOrganizationUuid">目標組織 UUID。</param>
    /// <param name="transactionId">所屬切換交易識別碼。</param>
    /// <param name="cancellationToken">取消 staging 作業的權杖。</param>
    /// <returns>來源驗證結果、Session 數量及目標備份狀態。</returns>
    Task<PreparationResult> PrepareReplacementAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        string sourceAccountUuid,
        string sourceOrganizationUuid,
        string targetAccountUuid,
        string targetOrganizationUuid,
        Guid transactionId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 原子套用指定交易已準備的來源 index 集合。
    /// </summary>
    /// <param name="installation">唯一定位的 Desktop 安裝。</param>
    /// <param name="targetAccountUuid">目標帳號 UUID。</param>
    /// <param name="targetOrganizationUuid">目標組織 UUID。</param>
    /// <param name="transactionId">所屬切換交易識別碼。</param>
    /// <param name="cancellationToken">取消套用作業的權杖。</param>
    Task ApplyReplacementAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        string targetAccountUuid,
        string targetOrganizationUuid,
        Guid transactionId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 依指定交易備份還原目標 index；重複呼叫必須維持冪等。
    /// </summary>
    /// <param name="installation">唯一定位的 Desktop 安裝。</param>
    /// <param name="targetAccountUuid">目標帳號 UUID。</param>
    /// <param name="targetOrganizationUuid">目標組織 UUID。</param>
    /// <param name="transactionId">所屬切換交易識別碼。</param>
    /// <param name="cancellationToken">取消還原作業的權杖。</param>
    Task RestoreTargetAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        string targetAccountUuid,
        string targetOrganizationUuid,
        Guid transactionId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 依已確認的 journal 清理意圖刪除指定交易的 staging 與 backup。
    /// </summary>
    /// <param name="installation">唯一定位的 Desktop 安裝。</param>
    /// <param name="transactionId">Application journal 中的交易識別碼。</param>
    /// <param name="cleanupIntent">依 Application journal 確認的清理意圖。</param>
    /// <param name="cancellationToken">取消清理作業的權杖。</param>
    Task CommitAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid transactionId,
        ClaudeSessionHistoryCleanupIntent cleanupIntent,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 定義 account／organization index 驗證結果。
    /// </summary>
    public enum ValidationStatus {
        /// <summary>索引與 transcript 指標有效，空索引也屬有效狀態。</summary>
        Valid,

        /// <summary>來源索引目錄不存在。</summary>
        MissingDirectory,

        /// <summary>索引或 transcript 無法讀取。</summary>
        Unreadable,

        /// <summary>索引格式或指標不符合支援結構。</summary>
        InvalidIndex,

        /// <summary>索引指向的 transcript 缺少或重複。</summary>
        InvalidTranscriptReference
    }

    /// <summary>
    /// 提供 account index 唯讀驗證結果。
    /// </summary>
    /// <param name="Status">驗證狀態。</param>
    /// <param name="SessionCount">索引內可接續的 Session 數量。</param>
    /// <param name="FailureCode">不包含資料內容或本機路徑的失敗代碼。</param>
    public sealed record ValidationResult(ValidationStatus Status, int SessionCount, string? FailureCode);

    /// <summary>
    /// 提供來源 staging 與目標備份結果。
    /// </summary>
    /// <param name="Status">來源驗證狀態。</param>
    /// <param name="SourceSessionCount">已 stage 的來源 Session 數量。</param>
    /// <param name="TargetSessionCount">原目標有效 Session 數量。</param>
    /// <param name="FailureCode">不包含資料內容或本機路徑的失敗代碼。</param>
    public sealed record PreparationResult(
        ValidationStatus Status,
        int SourceSessionCount,
        int TargetSessionCount,
        string? FailureCode
    );
}
