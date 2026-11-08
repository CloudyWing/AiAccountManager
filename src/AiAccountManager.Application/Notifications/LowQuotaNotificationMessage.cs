using System.Globalization;

namespace CloudyWing.AiAccountManager.Application.Notifications;

/// <summary>提供額度偏低通知的標題、類型與本文組字，正式通知與測試通知共用。</summary>
public static class LowQuotaNotificationMessage {
    /// <summary>額度偏低通知的標題。</summary>
    public const string Title = "額度偏低";

    /// <summary>額度偏低通知的類型。</summary>
    public const string Type = "low-quota";

    /// <summary>組出額度偏低通知的本文。</summary>
    /// <param name="provider">供應商識別，codex 顯示為 Codex，其餘顯示為 Claude。</param>
    /// <param name="windowLabel">額度窗口的顯示標籤。</param>
    /// <param name="remainingPercent">剩餘額度百分比。</param>
    /// <param name="recommendation">建議切換的帳號；空白時不附加建議。</param>
    /// <returns>通知本文。</returns>
    public static string Compose(string provider, string windowLabel, decimal remainingPercent, string? recommendation) {
        string displayName = provider == "codex" ? "Codex" : "Claude";
        string message = $"{displayName} {windowLabel}額度剩 {remainingPercent.ToString("0.##", CultureInfo.InvariantCulture)}%";
        if (!string.IsNullOrWhiteSpace(recommendation)) {
            message += $"，建議切換至 {recommendation}";
        }
        return message;
    }
}
