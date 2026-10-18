namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>一張重置券的自動使用紀錄。</summary>
/// <param name="AccountId">帳號識別碼。</param>
/// <param name="CreditId">券識別碼。</param>
/// <param name="AttemptedAtUtc">嘗試時間。</param>
/// <param name="IdempotencyKey">嘗試識別碼。</param>
/// <param name="Result">結果；pending 表示未確認。</param>
public sealed record CodexResetCreditAttempt(Guid AccountId, string CreditId, DateTimeOffset AttemptedAtUtc,
    string IdempotencyKey, string Result);
