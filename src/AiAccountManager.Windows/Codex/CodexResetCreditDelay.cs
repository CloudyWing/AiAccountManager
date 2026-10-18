using CloudyWing.AiAccountManager.Application.Codex;

namespace CloudyWing.AiAccountManager.Windows.Codex;

/// <summary>以系統計時器等待券的期限。</summary>
public sealed class CodexResetCreditDelay : ICodexResetCreditDelay {
    /// <inheritdoc />
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
