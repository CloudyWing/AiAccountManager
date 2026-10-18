namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 定義由 Application journal 確認的 Claude history 清理意圖。
/// </summary>
public enum ClaudeSessionHistoryCleanupIntent {
    /// <summary>
    /// 尚未確認清理交易階段，不允許執行清理。
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// 清理已知 preparation 或已完成 rollback 的 history 交易。
    /// </summary>
    Preparation,

    /// <summary>
    /// 清理 journal 階段為 CommittedCleanupPending 的 history 交易。
    /// </summary>
    Committed
}
