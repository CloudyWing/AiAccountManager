using System.Globalization;

namespace CloudyWing.AiAccountManager.Desktop.Components;

/// <summary>提供台灣時間的顯示格式。</summary>
public static class TaiwanTimeUtils {
    /// <summary>將時間轉換為台灣時間。</summary>
    /// <param name="value">要轉換的時間。</param>
    /// <returns>月日及時分。</returns>
    public static string Format(DateTimeOffset value) {
        return value.ToOffset(TimeSpan.FromHours(8)).ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
    }
}
