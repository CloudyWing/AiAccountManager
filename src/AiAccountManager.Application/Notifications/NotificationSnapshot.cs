namespace CloudyWing.AiAccountManager.Application.Notifications;

/// <summary>提供目前帳號的安全額度資料。</summary>
/// <param name="Provider">來源 provider。</param>
/// <param name="AccountId">唯一匹配的帳號識別碼。</param>
/// <param name="Recommendation">安全顯示的建議帳號名稱。</param>
/// <param name="Windows">有效的額度視窗。</param>
public sealed record NotificationSnapshot(string Provider, Guid AccountId, string? Recommendation, IReadOnlyList<NotificationWindow> Windows);
