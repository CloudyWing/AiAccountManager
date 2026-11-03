namespace CloudyWing.AiAccountManager.Windows.Runtime;

/// <summary>單一執行個體協商的結果。</summary>
public sealed record NegotiationResult(NegotiationOutcome Outcome, IDisposable? Instance, int ServerProcessId);
