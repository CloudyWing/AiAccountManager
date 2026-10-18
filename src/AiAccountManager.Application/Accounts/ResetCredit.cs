namespace CloudyWing.AiAccountManager.Application.Accounts;

/// <summary>表示一張 Codex 重置券。</summary>
/// <param name="Id">券識別碼。</param>
/// <param name="ResetType">重置類型。</param>
/// <param name="Status">券狀態。</param>
/// <param name="GrantedAtUtc">取得時間。</param>
/// <param name="ExpiresAtUtc">到期時間；無期限時為 null。</param>
/// <param name="Title">券名稱。</param>
/// <param name="Description">券說明。</param>
public sealed record ResetCredit(string Id, string ResetType, string Status, DateTimeOffset GrantedAtUtc,
    DateTimeOffset? ExpiresAtUtc, string? Title, string? Description
);
