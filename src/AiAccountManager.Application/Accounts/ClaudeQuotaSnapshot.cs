namespace CloudyWing.AiAccountManager.Application.Accounts;

/// <summary>
/// 保存單一 Claude profile 的額度查詢快照。
/// </summary>
/// <param name="ProfileId">額度所屬 profile 識別碼。</param>
/// <param name="RetrievedAtUtc">API 回應擷取時間。</param>
/// <param name="Windows">API 提供的額度視窗。</param>
/// <param name="ResetGrants">符合資格的額外重置資料。</param>
public sealed record ClaudeQuotaSnapshot(
    Guid ProfileId,
    DateTimeOffset RetrievedAtUtc,
    IReadOnlyList<ClaudeQuotaSnapshot.Window> Windows,
    IReadOnlyList<ClaudeResetGrant>? ResetGrants = null
) {
    /// <summary>
    /// 表示 Claude 額度回應中的一個視窗。
    /// </summary>
    /// <param name="Key">視窗識別字。</param>
    /// <param name="DurationMinutes">視窗長度分鐘數。</param>
    /// <param name="UtilizationPercent">已使用比例，範圍為 0 到 100。</param>
    /// <param name="ResetsAtUtc">伺服器提供的重設時間。</param>
    public sealed record Window(
        string Key,
        int DurationMinutes,
        decimal UtilizationPercent,
        DateTimeOffset? ResetsAtUtc
    );

    /// <summary>
    /// 定義額度畫面可呈現的查詢狀態。
    /// </summary>
    public enum RefreshStatus {
        /// <summary>尚無有效查詢結果。</summary>
        Unknown,

        /// <summary>正在查詢額度。</summary>
        Refreshing,

        /// <summary>最近一次查詢取得完整額度資料。</summary>
        Fresh,

        /// <summary>有先前成功快照，但最近一次查詢失敗。</summary>
        Stale,

        /// <summary>需要重新驗證 Claude 帳號。</summary>
        ReauthenticationRequired,

        /// <summary>額度查詢發生錯誤。</summary>
        Error,

        /// <summary>回應缺少必要額度欄位。</summary>
        Incomplete
    }

    /// <summary>
    /// 提供指定 profile 的額度快照與狀態。
    /// </summary>
    /// <param name="ProfileId">額度所屬 profile 識別碼。</param>
    /// <param name="Snapshot">最近一次成功的額度快照。</param>
    /// <param name="Status">目前顯示狀態。</param>
    /// <param name="LastAttemptAtUtc">最近一次查詢嘗試時間。</param>
    /// <param name="Message">可供使用者採取行動的狀態說明。</param>
    /// <param name="RetryAfterUtc">伺服器要求最早可再次查詢的時間。</param>
    public sealed record View(
        Guid ProfileId,
        ClaudeQuotaSnapshot? Snapshot,
        RefreshStatus Status,
        DateTimeOffset? LastAttemptAtUtc,
        string? Message,
        DateTimeOffset? RetryAfterUtc = null
    );
}
