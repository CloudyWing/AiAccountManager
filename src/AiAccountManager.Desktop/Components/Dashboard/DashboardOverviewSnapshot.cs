namespace CloudyWing.AiAccountManager.Desktop.Components.Dashboard;

/// <summary>
/// 定義首頁可以安全顯示的 provider 額度資料。
/// </summary>
public sealed record DashboardOverviewSnapshot {
    /// <summary>
    /// 提供單一 provider 的身分、額度與更新狀態。
    /// </summary>
    /// <param name="Kind">額度來源。</param>
    /// <param name="IdentityStatus">目前 Desktop 身分的辨識狀態。</param>
    /// <param name="QuotaStatus">已保存額度資料的狀態。</param>
    /// <param name="MaskedIdentity">可安全顯示的遮蔽身分或 profile 別名。</param>
    /// <param name="SourceLabel">不含本機路徑的穩定資料來源標籤。</param>
    /// <param name="IdentityMessage">身分辨識結果的安全訊息。</param>
    /// <param name="QuotaMessage">額度結果的安全訊息。</param>
    /// <param name="CheckedAtUtc">最近一次身分檢查時間。</param>
    /// <param name="RetrievedAtUtc">最近一次成功取得額度的時間。</param>
    /// <param name="Windows">該身分的額度視窗。</param>
    /// <param name="IsStale">額度資料是否為舊快照。</param>
    /// <param name="PlanLabel">帳號方案的顯示名稱；沒有需要標示的方案時為 null。</param>
    public sealed record Provider(
        ProviderKind Kind,
        IdentityStatus IdentityStatus,
        QuotaStatus QuotaStatus,
        string? MaskedIdentity,
        string SourceLabel,
        string? IdentityMessage,
        string? QuotaMessage,
        DateTimeOffset? CheckedAtUtc,
        DateTimeOffset? RetrievedAtUtc,
        IReadOnlyList<QuotaWindow> Windows,
        bool IsStale,
        string? PlanLabel = null
    );

    /// <summary>
    /// 定義額度來源種類。
    /// </summary>
    public enum ProviderKind {
        /// <summary>Codex Desktop。</summary>
        Codex,

        /// <summary>Claude Desktop。</summary>
        Claude
    }

    /// <summary>
    /// 定義 Desktop 身分的安全辨識狀態。
    /// </summary>
    public enum IdentityStatus {
        /// <summary>目前身分唯一匹配已保存 profile。</summary>
        Matched,

        /// <summary>目前身分沒有匹配已保存 profile。</summary>
        Unmatched,

        /// <summary>目前身分匹配到多個候選或安裝項目。</summary>
        Ambiguous,

        /// <summary>目前無法取得或確認 Desktop 身分。</summary>
        Unknown,

        /// <summary>讀取身分或已保存 profile 時發生錯誤。</summary>
        Error,

        /// <summary>Desktop 登入需要重新驗證。</summary>
        ReauthenticationRequired,

        /// <summary>有尚未完成的 Claude Desktop 復原交易。</summary>
        RecoveryRequired,

        /// <summary>目前的 Desktop 安裝不受支援。</summary>
        Unsupported
    }

    /// <summary>
    /// 定義已保存額度資料的安全顯示狀態。
    /// </summary>
    public enum QuotaStatus {
        /// <summary>尚無成功額度快照。</summary>
        Unknown,

        /// <summary>額度快照仍在更新。</summary>
        Refreshing,

        /// <summary>最近一次額度快照有效。</summary>
        Fresh,

        /// <summary>顯示最近一次成功額度快照，且資料已過期。</summary>
        Stale,

        /// <summary>額度資料需要重新登入才能更新。</summary>
        ReauthenticationRequired,

        /// <summary>額度資料無法讀取。</summary>
        Error,

        /// <summary>額度資料不完整或含無效數值。</summary>
        Incomplete
    }

    /// <summary>
    /// 提供一個額度視窗的顯示標籤、範圍、剩餘比例與重設時間。
    /// </summary>
    /// <param name="Key">來源回傳的視窗識別字。</param>
    /// <param name="Label">可供畫面顯示的視窗名稱。</param>
    /// <param name="Scope">可供畫面顯示的模型或視窗範圍。</param>
    /// <param name="RemainingPercent">已知的剩餘比例；無效或未知時為 null。</param>
    /// <param name="ResetsAtUtc">來源回傳的重設時間。</param>
    public sealed record QuotaWindow(
        string Key,
        string Label,
        string? Scope,
        decimal? RemainingPercent,
        DateTimeOffset? ResetsAtUtc
    );
}
