namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>持久保存重置券自動使用紀錄。</summary>
public interface ICodexResetCreditAttemptRepository {
    /// <summary>讀取所有嘗試紀錄。</summary>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>已保存的紀錄。</returns>
    Task<IReadOnlyList<CodexResetCreditAttempt>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>僅在券尚無紀錄時保存 pending。</summary>
    /// <param name="attempt">待保存的紀錄。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>成功建立紀錄時為 true。</returns>
    Task<bool> TryBeginAsync(CodexResetCreditAttempt attempt, CancellationToken cancellationToken);

    /// <summary>保存嘗試結果。</summary>
    /// <param name="attempt">已完成的紀錄。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>表示保存完成的工作。</returns>
    Task CompleteAsync(CodexResetCreditAttempt attempt, CancellationToken cancellationToken);
}
