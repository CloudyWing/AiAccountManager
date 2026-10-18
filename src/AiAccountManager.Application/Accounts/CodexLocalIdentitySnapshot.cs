namespace CloudyWing.AiAccountManager.Application.Accounts;

/// <summary>
/// 提供可安全呈現的本機登入資料匹配狀態。
/// </summary>
public sealed record CodexLocalIdentitySnapshot(
    CodexLocalIdentityStatus Status,
    Guid? MatchedProfileAccountId,
    string? MaskedIdentity,
    DateTimeOffset? CheckedAtUtc,
    bool IsMonitoring,
    bool IsMonitoringAvailable
);
