namespace CloudyWing.AiAccountManager.Application.Notifications;

/// <summary>提供單一有效額度視窗。</summary>
/// <param name="Key">視窗識別字。</param>
/// <param name="Label">顯示名稱。</param>
/// <param name="RemainingPercent">剩餘百分比；未知時為 null。</param>
/// <param name="ResetsAtUtc">週期重設時間。</param>
public sealed record NotificationWindow(string Key, string Label, decimal? RemainingPercent, DateTimeOffset? ResetsAtUtc);
