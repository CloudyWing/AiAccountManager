using System.Text.Json.Serialization;

namespace CloudyWing.AiAccountManager.Application.Accounts;

/// <summary>
/// 指出額度更新作業的狀態。
/// </summary>
public enum QuotaRefreshStatus {
    /// <summary>
    /// 尚未得知更新狀態。
    /// </summary>
    Unknown,
    /// <summary>
    /// 正在更新額度。
    /// </summary>
    Refreshing,
    /// <summary>
    /// 更新結果不完整。
    /// </summary>
    Incomplete,
    /// <summary>
    /// 更新發生錯誤。
    /// </summary>
    Error,
    /// <summary>
    /// 需要重新驗證。
    /// </summary>
    ReauthenticationRequired
}

/// <summary>
/// 指出額度資料的顯示狀態。
/// </summary>
public enum QuotaDisplayStatus {
    /// <summary>
    /// 尚未得知額度狀態。
    /// </summary>
    Unknown,
    /// <summary>
    /// 正在更新額度。
    /// </summary>
    Refreshing,
    /// <summary>
    /// 額度資料為最新狀態。
    /// </summary>
    Fresh,
    /// <summary>
    /// 額度資料已過時。
    /// </summary>
    Stale,
    /// <summary>
    /// 需要重新驗證。
    /// </summary>
    ReauthenticationRequired,
    /// <summary>
    /// 更新發生錯誤。
    /// </summary>
    Error,
    /// <summary>
    /// 額度資料不完整。
    /// </summary>
    Incomplete
}

/// <summary>
/// 表示一個額度時間範圍。
/// </summary>
/// <param name="DurationMinutes">時間範圍的長度，單位為分鐘。</param>
/// <param name="RemainingPercent">剩餘額度百分比。</param>
/// <param name="ResetsAtUtc">額度重設時間，使用 UTC。</param>
public sealed record QuotaWindow(
    int DurationMinutes,
    int RemainingPercent,
    DateTimeOffset? ResetsAtUtc
);

/// <summary>
/// 表示成功取得的額度快照。
/// </summary>
/// <param name="RetrievedAtUtc">取得時間，使用 UTC。</param>
/// <param name="FiveHour">五小時額度範圍；免費方案為 null。</param>
/// <param name="Weekly">每週額度範圍；免費方案為 null。</param>
/// <param name="ResetCredits">重置券資料；未知時為 null。</param>
/// <param name="FreePlanWindow">免費方案的單一額度範圍；非免費方案為 null。</param>
public sealed record QuotaSuccessfulSnapshot(
    DateTimeOffset RetrievedAtUtc,
    QuotaWindow? FiveHour,
    QuotaWindow? Weekly,
    ResetCreditSummary? ResetCredits = null,
    QuotaWindow? FreePlanWindow = null
) {
    /// <summary>
    /// 取得一個值，用以指出快照是否屬於免費方案。
    /// </summary>
    [JsonIgnore]
    public bool IsFreePlan => FreePlanWindow is not null;
}

/// <summary>
/// 表示一次額度更新的結果。
/// </summary>
/// <param name="Status">更新狀態。</param>
/// <param name="AttemptedAtUtc">嘗試更新的時間，使用 UTC。</param>
/// <param name="FailureCode">失敗代碼。</param>
/// <param name="FailureCount">失敗次數。</param>
/// <param name="RetryAtUtc">重試時間，使用 UTC。</param>
/// <param name="MissingWindows">缺少的額度時間範圍。</param>
public sealed record QuotaRefreshAttempt(
    QuotaRefreshStatus Status,
    DateTimeOffset AttemptedAtUtc,
    string? FailureCode,
    int FailureCount,
    DateTimeOffset? RetryAtUtc,
    IReadOnlyList<string> MissingWindows
);

/// <summary>
/// 表示帳號保存的額度快照及更新紀錄。
/// </summary>
/// <param name="AccountId">帳號識別碼。</param>
/// <param name="IdentityFingerprint">身分指紋。</param>
/// <param name="ProfileRevision">帳號資料版本。</param>
/// <param name="LastSuccessfulSnapshot">最後一次成功取得的快照。</param>
/// <param name="LastAttempt">最後一次更新紀錄。</param>
public sealed record QuotaSnapshotRecord(
    Guid AccountId,
    string IdentityFingerprint,
    long ProfileRevision,
    QuotaSuccessfulSnapshot? LastSuccessfulSnapshot,
    QuotaRefreshAttempt? LastAttempt
);

/// <summary>
/// 表示帳號額度的顯示資料。
/// </summary>
/// <param name="AccountId">帳號識別碼。</param>
/// <param name="Snapshot">保存的額度快照。</param>
/// <param name="Status">顯示狀態。</param>
/// <param name="IsStale">若資料已過時則為 <see langword="true" />；否則為 <see langword="false" />。</param>
/// <param name="Message">顯示訊息。</param>
public sealed record QuotaSnapshotView(
    Guid AccountId,
    QuotaSnapshotRecord? Snapshot,
    QuotaDisplayStatus Status,
    bool IsStale,
    string? Message
);
