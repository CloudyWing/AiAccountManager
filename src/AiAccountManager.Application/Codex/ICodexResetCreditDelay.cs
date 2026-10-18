namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>提供可替換的期限等待。</summary>
public interface ICodexResetCreditDelay {
    /// <summary>等待指定時間。</summary>
    /// <param name="delay">等待時間。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>等待完成的工作。</returns>
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}
