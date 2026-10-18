namespace CloudyWing.AiAccountManager.Application.Accounts;

/// <summary>
/// 封裝獨立於本機認證資料的 Codex Desktop 程序狀態。
/// </summary>
public sealed record CodexDesktopProcessStatusSnapshot(
    CodexDesktopProcessStatus Status,
    DateTimeOffset CheckedAtUtc
);
