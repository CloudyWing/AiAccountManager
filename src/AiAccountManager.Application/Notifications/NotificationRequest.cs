namespace CloudyWing.AiAccountManager.Application.Notifications;

/// <summary>提供通知內容與受限的導覽目標。</summary>
/// <param name="Title">通知標題。</param>
/// <param name="Message">通知本文。</param>
/// <param name="Type">通知類型。</param>
/// <param name="Provider">導覽目標，僅支援 claude 或 codex。</param>
public sealed record NotificationRequest(string Title, string Message, string Type, string Provider);
