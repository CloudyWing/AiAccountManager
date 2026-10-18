using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 管理每個 Codex 帳號專用的 Codex home 目錄，以及新增與移除帳號的清理交易。
/// </summary>
public interface ICodexProfileStorage {
    /// <summary>
    /// 取得指定帳號的 Codex home 目錄路徑。
    /// </summary>
    /// <param name="accountId">Codex 帳號識別碼。</param>
    /// <returns>Codex home 目錄的絕對路徑。</returns>
    string GetHomePath(Guid accountId);

    /// <summary>
    /// 建立指定帳號的 Codex home 目錄。
    /// </summary>
    /// <param name="accountId">Codex 帳號識別碼。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>建立完成的工作。</returns>
    Task CreateHomeAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// 刪除帳號 home 中不應存在的 <c>auth.json</c>，例如登入流程中途失敗時留下的認證檔。
    /// </summary>
    /// <param name="accountId">Codex 帳號識別碼。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>有認證檔被刪除時為 <see langword="true" />；否則為 <see langword="false" />。</returns>
    Task<bool> RemoveUnexpectedAuthFileAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// 刪除指定帳號的 Codex home 目錄；目錄不存在時不做任何事。
    /// </summary>
    /// <param name="accountId">Codex 帳號識別碼。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>刪除完成的工作。</returns>
    Task RemoveHomeAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// 載入可安全重試的已保存新增帳號清理交易。
    /// </summary>
    /// <param name="cancellationToken">取消要求。</param>
    /// <returns>可安全重試的新增帳號清理交易。</returns>
    Task<IReadOnlyList<CodexProfileTransaction>> LoadPendingAddCleanupTransactionsAsync(
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 載入指定帳號尚未完成的移除交易。
    /// </summary>
    /// <param name="accountId">Codex 帳號識別碼。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>未完成的移除交易；沒有時為 <see langword="null" />。</returns>
    Task<CodexProfileTransaction?> LoadPendingRemovalTransactionAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// 保存帳號新增或移除交易，供中斷後重試。
    /// </summary>
    /// <param name="transaction">要保存的交易。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>保存完成的工作。</returns>
    Task SaveTransactionAsync(CodexProfileTransaction transaction, CancellationToken cancellationToken);

    /// <summary>
    /// 刪除已完成的交易紀錄。
    /// </summary>
    /// <param name="transactionId">交易識別碼。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>刪除完成的工作。</returns>
    Task DeleteTransactionAsync(Guid transactionId, CancellationToken cancellationToken);
}
