namespace CloudyWing.AiAccountManager.Application.Notifications;

/// <summary>不含帳號識別資訊的通知決策。</summary>
/// <param name="Provider">來源。</param>
/// <param name="WindowKey">額度視窗。</param>
/// <param name="Cycle">正規化週期。</param>
/// <param name="DedupHit">是否命中去重。</param>
/// <param name="Sent">是否送出。</param>
/// <param name="Outcome">評估結果。</param>
public sealed record NotificationDecision(string Provider, string WindowKey, string Cycle, bool DedupHit, bool Sent, NotificationDecisionOutcome Outcome);
