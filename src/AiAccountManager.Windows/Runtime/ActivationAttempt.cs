namespace CloudyWing.AiAccountManager.Windows.Runtime;

/// <summary>單次啟動要求的結果。</summary>
public sealed record ActivationAttempt(ActivationOutcome Outcome, int ServerProcessId);
