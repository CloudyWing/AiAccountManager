namespace CloudyWing.AiAccountManager.Application.Recommendations;

/// <summary>
/// 依有效的額度快照選擇建議帳號。
/// </summary>
public sealed class AccountRecommendationService {
    /// <summary>
    /// 選擇較目前帳號具有更多最低剩餘額度的帳號。
    /// </summary>
    /// <param name="currentAccountId">唯一匹配的目前帳號識別碼。</param>
    /// <param name="accounts">相同 provider 的安全帳號快照。</param>
    /// <returns>符合條件的最佳候選；無候選時為 null。</returns>
    public RecommendationAccount? Recommend(Guid currentAccountId, IReadOnlyList<RecommendationAccount> accounts) {
        RecommendationAccount[] matches = accounts.Where(account => account.AccountId == currentAccountId).ToArray();
        if (matches.Length != 1 || !IsUsable(matches[0])) {
            return null;
        }

        decimal currentMinimum = Minimum(matches[0]);
        if (currentMinimum >= 50m) {
            return null;
        }

        return accounts
            .Where(account => account.AccountId != currentAccountId && IsUsable(account))
            .Where(account => Minimum(account) > currentMinimum)
            .OrderByDescending(Minimum)
            .ThenBy(account => account.FiveHourResetsAtUtc is null)
            .ThenBy(account => account.FiveHourResetsAtUtc)
            .ThenBy(account => account.AccountId.ToString("N"), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool IsUsable(RecommendationAccount account) {
        return account.IsValid
            && account.FiveHourRemaining is >= 0m and <= 100m
            && account.WeeklyRemaining is >= 0m and <= 100m;
    }

    private static decimal Minimum(RecommendationAccount account) {
        return Math.Min(account.FiveHourRemaining!.Value, account.WeeklyRemaining!.Value);
    }
}
