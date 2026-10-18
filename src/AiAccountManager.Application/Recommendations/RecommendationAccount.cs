namespace CloudyWing.AiAccountManager.Application.Recommendations;

/// <summary>
/// 提供推薦排序所需的安全帳號與額度快照。
/// </summary>
/// <param name="AccountId">帳號識別碼。</param>
/// <param name="DisplayName">可安全顯示的名稱。</param>
/// <param name="FiveHourRemaining">五小時剩餘比例。</param>
/// <param name="WeeklyRemaining">每週剩餘比例。</param>
/// <param name="FiveHourResetsAtUtc">五小時重設時間。</param>
/// <param name="IsValid">是否為有效且未過期的快照。</param>
public sealed record RecommendationAccount(
    Guid AccountId,
    string DisplayName,
    decimal? FiveHourRemaining,
    decimal? WeeklyRemaining,
    DateTimeOffset? FiveHourResetsAtUtc,
    bool IsValid
);
