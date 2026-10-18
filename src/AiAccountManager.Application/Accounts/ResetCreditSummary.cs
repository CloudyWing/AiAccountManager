namespace CloudyWing.AiAccountManager.Application.Accounts;

/// <summary>表示可用重置券數量與可見明細。</summary>
/// <param name="AvailableCount">可用數量。</param>
/// <param name="Credits">可見明細；未知時為 null。</param>
public sealed record ResetCreditSummary(long AvailableCount, IReadOnlyList<ResetCredit>? Credits);
