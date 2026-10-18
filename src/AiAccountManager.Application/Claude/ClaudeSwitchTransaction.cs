using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 記錄 Claude Desktop 帳號切換及其可復原階段。
/// </summary>
/// <param name="TransactionId">交易識別碼。</param>
/// <param name="SourceProfileId">切換來源 profile 識別碼。</param>
/// <param name="TargetProfileId">切換目標 profile 識別碼。</param>
/// <param name="SourceProfileRevision">切換開始時的來源 profile 版次。</param>
/// <param name="TargetProfileRevision">切換開始時的目標 profile 版次。</param>
/// <param name="InstallationId">交易所屬 Desktop 安裝識別碼。</param>
/// <param name="CurrentStage">目前交易階段。</param>
/// <param name="StartedAtUtc">交易開始時間。</param>
/// <param name="UpdatedAtUtc">交易狀態最近更新時間。</param>
/// <param name="FailureCode">安全且穩定的失敗代碼。</param>
/// <param name="RecoveryFromStage">進入待復原前最後完成或開始的階段。</param>
/// <param name="ProvenanceSchemaVersion">交易來源證據版本；0 表示舊交易未提供來源證據。</param>
/// <param name="RootBinding">交易固定的受保護來源綁定；舊交易為 null。</param>
public sealed record ClaudeSwitchTransaction(
    Guid TransactionId,
    Guid SourceProfileId,
    Guid TargetProfileId,
    long SourceProfileRevision,
    long TargetProfileRevision,
    string InstallationId,
    ClaudeSwitchTransaction.Stage CurrentStage,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? FailureCode,
    ClaudeSwitchTransaction.Stage? RecoveryFromStage = null,
    int ProvenanceSchemaVersion = 0,
    ClaudeDesktopRootBinding? RootBinding = null
) {
    /// <summary>
    /// 定義 durable switch journal 的交易階段。
    /// </summary>
    public enum Stage {
        /// <summary>已寫入交易紀錄，尚未提交任何 Desktop 檔案。</summary>
        Prepared,

        /// <summary>Desktop 已正常關閉。</summary>
        DesktopClosed,

        /// <summary>來源認證快照已在關閉後更新。</summary>
        SourceCaptured,

        /// <summary>目標 auth 與 Cookie 回復材料已備妥。</summary>
        TargetBackedUp,

        /// <summary>Application journal 已保存 history preparation transaction ID，尚未開始 live apply。</summary>
        HistoryPreparationStarted,

        /// <summary>目標 Code index 已備份並完成 staging。</summary>
        HistoryPrepared,

        /// <summary>已寫入 journal，正在套用目標 Code index。</summary>
        HistoryApplyStarted,

        /// <summary>目標 Code index 已套用來源集合。</summary>
        HistoryApplied,

        /// <summary>已寫入 journal，正在套用目標 auth 與 Cookie。</summary>
        TargetAuthApplyStarted,

        /// <summary>目標 auth 與 Cookie 已套用。</summary>
        TargetAuthApplied,

        /// <summary>已寫入 journal，正在啟動目標 Claude Desktop。</summary>
        TargetLaunchStarted,

        /// <summary>Desktop 已重新啟動。</summary>
        TargetLaunched,

        /// <summary>目標 Desktop 身分已重新驗證。</summary>
        TargetVerified,

        /// <summary>目標身份已提交，只待清理本交易的 history 暫存。</summary>
        CommittedCleanupPending,

        /// <summary>需要完成或重試復原。</summary>
        RecoveryRequired,

        /// <summary>交易已提交。</summary>
        Committed,

        /// <summary>交易已回復至來源 profile。</summary>
        RolledBack
    }
}
