using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 讀取與保存各 Codex 帳號最近一次的額度快照。
/// </summary>
public interface IQuotaSnapshotRepository {
    /// <summary>
    /// 讀取指定帳號的額度快照。
    /// </summary>
    /// <param name="accountId">Codex 帳號識別碼。</param>
    /// <param name="cancellationToken">取消讀取的權杖。</param>
    /// <returns>額度快照與其檔案雜湊；尚未保存過時為 <see langword="null" />。</returns>
    Task<Snapshot?> LoadAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// 在快照仍為預期版本時保存額度快照。
    /// </summary>
    /// <param name="snapshot">要保存的額度快照。</param>
    /// <param name="expectedSha256">讀取時取得的檔案雜湊；首次保存時為 <see langword="null" />。</param>
    /// <param name="cancellationToken">取消保存的權杖。</param>
    /// <returns>保存結果；快照已被其他作業修改時回報衝突。</returns>
    Task<SaveResult> SaveAsync(
        QuotaSnapshotRecord snapshot,
        string? expectedSha256,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 額度快照與其檔案雜湊。
    /// </summary>
    /// <param name="Data">額度快照內容。</param>
    /// <param name="Sha256">快照檔內容的 SHA-256。</param>
    public sealed record Snapshot(QuotaSnapshotRecord Data, string Sha256);

    /// <summary>
    /// 額度快照保存結果。
    /// </summary>
    /// <param name="Saved">已寫入時為 <see langword="true" />。</param>
    /// <param name="Snapshot">寫入後的快照；未寫入時為 <see langword="null" />。</param>
    /// <param name="Conflict">快照已被其他作業修改時為 <see langword="true" />。</param>
    public sealed record SaveResult(bool Saved, Snapshot? Snapshot, bool Conflict);
}
