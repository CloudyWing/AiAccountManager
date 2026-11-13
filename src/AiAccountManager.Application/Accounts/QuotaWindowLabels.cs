namespace CloudyWing.AiAccountManager.Application.Accounts;

/// <summary>
/// 提供額度時間範圍的長度常數與顯示名稱。
/// </summary>
public static class QuotaWindowLabels {
    /// <summary>
    /// 五小時額度範圍的長度，單位為分鐘。
    /// </summary>
    public const int FiveHourMinutes = 300;

    /// <summary>
    /// 每週額度範圍的長度，單位為分鐘。
    /// </summary>
    public const int WeeklyMinutes = 10080;

    /// <summary>
    /// 免費方案額度範圍的長度，單位為分鐘。
    /// </summary>
    public const int FreePlanMinutes = 43200;

    /// <summary>
    /// 依額度時間範圍的長度取得顯示名稱。
    /// </summary>
    /// <param name="durationMinutes">時間範圍的長度，單位為分鐘。</param>
    /// <returns>可供畫面顯示的名稱。</returns>
    public static string ForDuration(int durationMinutes) {
        return durationMinutes switch {
            FiveHourMinutes => "5 小時",
            WeeklyMinutes => "每週",
            FreePlanMinutes => "30 天",
            _ => durationMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 分鐘"
        };
    }
}
