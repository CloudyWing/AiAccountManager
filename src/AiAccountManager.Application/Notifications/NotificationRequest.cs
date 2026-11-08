namespace CloudyWing.AiAccountManager.Application.Notifications;

/// <summary>提供通知內容與受限的導覽目標。</summary>
/// <param name="Title">通知標題。</param>
/// <param name="Message">通知本文。</param>
/// <param name="Type">通知類型。</param>
/// <param name="Provider">導覽目標，僅支援 claude 或 codex。</param>
/// <param name="Activatable">是否在使用者點擊氣泡時觸發導覽；false 時點擊氣泡不觸發 <c>INotificationSink.Activated</c>，Provider 仍須為 claude 或 codex。</param>
public sealed record NotificationRequest(string Title, string Message, string Type, string Provider, bool Activatable = true);
