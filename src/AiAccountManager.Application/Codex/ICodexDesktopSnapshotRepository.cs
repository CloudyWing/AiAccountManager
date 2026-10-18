namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 定義 Codex Desktop 登入快照與切換交易紀錄的受保護保存契約。
/// </summary>
public interface ICodexDesktopSnapshotRepository {
    /// <summary>
    /// 列出已保存 Desktop 快照的 Codex 帳號。
    /// </summary>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>有快照的帳號識別碼。</returns>
    Task<IReadOnlySet<Guid>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 載入指定帳號的 `auth.json` 快照。
    /// </summary>
    /// <param name="accountId">Codex 帳號識別碼。</param>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>已解密的快照內容；不存在時為 <see langword="null" />。呼叫端用畢須清除內容。</returns>
    Task<byte[]?> LoadAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// 保存或覆寫指定帳號的 `auth.json` 快照。
    /// </summary>
    /// <param name="accountId">Codex 帳號識別碼。</param>
    /// <param name="content">`auth.json` 位元組。</param>
    /// <param name="cancellationToken">取消寫入作業的權杖。</param>
    Task SaveAsync(Guid accountId, byte[] content, CancellationToken cancellationToken);

    /// <summary>
    /// 刪除指定帳號的快照；不存在時不做任何事。
    /// </summary>
    /// <param name="accountId">Codex 帳號識別碼。</param>
    /// <param name="cancellationToken">取消刪除作業的權杖。</param>
    Task DeleteAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// 載入尚未完成的切換交易紀錄。
    /// </summary>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>交易紀錄；不存在時為 <see langword="null" />。</returns>
    Task<SwitchJournal?> LoadJournalAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 保存切換交易紀錄。
    /// </summary>
    /// <param name="journal">交易紀錄。</param>
    /// <param name="cancellationToken">取消寫入作業的權杖。</param>
    Task SaveJournalAsync(SwitchJournal journal, CancellationToken cancellationToken);

    /// <summary>
    /// 刪除切換交易紀錄；不存在時不做任何事。
    /// </summary>
    /// <param name="cancellationToken">取消刪除作業的權杖。</param>
    Task DeleteJournalAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 描述一筆尚未完成的 Codex Desktop 切換交易。
    /// </summary>
    /// <param name="TransactionId">交易識別碼。</param>
    /// <param name="SourceAccountId">來源帳號識別碼。</param>
    /// <param name="TargetAccountId">目標帳號識別碼。</param>
    /// <param name="OriginalAuthContent">切換前的 `auth.json` 內容，供還原使用。</param>
    /// <param name="RelaunchDesktop">完成或還原後是否需要重新開啟 Codex Desktop。</param>
    /// <param name="AppUserModelId">重新開啟 Codex Desktop 用的 AppUserModelId；未知時為 <see langword="null" />。</param>
    /// <param name="UpdatedAtUtc">紀錄更新時間。</param>
    public sealed record SwitchJournal(
        Guid TransactionId,
        Guid SourceAccountId,
        Guid TargetAccountId,
        byte[] OriginalAuthContent,
        bool RelaunchDesktop,
        string? AppUserModelId,
        DateTimeOffset UpdatedAtUtc
    ) {
        /// <summary>
        /// 以遮蔽文字呈現交易紀錄，避免除錯字串洩漏認證資料。
        /// </summary>
        /// <returns>不含認證內容的描述。</returns>
        public override string ToString() {
            return $"Codex Desktop switch journal {TransactionId:N} (sensitive values redacted).";
        }
    }
}
