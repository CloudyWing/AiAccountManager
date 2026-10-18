namespace CloudyWing.AiAccountManager.Application.Accounts;

/// <summary>表示 Claude 額外重置額度。</summary>
/// <param name="Id">識別碼。</param>
/// <param name="Label">顯示名稱。</param>
/// <param name="ResetsTotal">總次數。</param>
/// <param name="ResetsLeft">剩餘次數。</param>
/// <param name="StartsAtUtc">開始時間。</param>
/// <param name="EndsAtUtc">結束時間。</param>
/// <param name="IsUsableNow">目前是否可使用。</param>
/// <param name="UseRequiresLimit">使用是否需要達到上限。</param>
/// <param name="IsPaused">是否暫停。</param>
/// <param name="Type">重置類型。</param>
public sealed record ClaudeResetGrant(string Id, string Label, long ResetsTotal, long ResetsLeft,
    DateTimeOffset? StartsAtUtc, DateTimeOffset? EndsAtUtc, bool IsUsableNow, bool UseRequiresLimit,
    bool IsPaused, string Type
);
