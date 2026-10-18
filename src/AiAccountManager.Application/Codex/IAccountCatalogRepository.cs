using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 讀取與保存 Codex 帳號清單，並以內容雜湊偵測並行修改。
/// </summary>
public interface IAccountCatalogRepository {
    /// <summary>
    /// 讀取目前的帳號清單；清單不存在時建立空清單。
    /// </summary>
    /// <param name="cancellationToken">取消讀取的權杖。</param>
    /// <returns>帳號清單與其檔案雜湊。</returns>
    Task<Snapshot> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 在清單仍為預期版本時，以指定順序保存帳號清單。
    /// </summary>
    /// <param name="accounts">要保存的帳號，順序即畫面顯示順序。</param>
    /// <param name="expectedSha256">讀取時取得的檔案雜湊。</param>
    /// <param name="cancellationToken">取消保存的權杖。</param>
    /// <returns>保存結果；清單已被其他作業修改時回報衝突。</returns>
    Task<SaveResult> SaveAsync(
        IReadOnlyList<CodexAccountProfile> accounts,
        string expectedSha256,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 帳號清單與其檔案雜湊。
    /// </summary>
    /// <param name="Accounts">依顯示順序排列的帳號。</param>
    /// <param name="Sha256">清單檔內容的 SHA-256。</param>
    public sealed record Snapshot(IReadOnlyList<CodexAccountProfile> Accounts, string Sha256);

    /// <summary>
    /// 帳號清單保存結果。
    /// </summary>
    /// <param name="Saved">已寫入時為 <see langword="true" />。</param>
    /// <param name="Snapshot">寫入後的清單；未寫入時為 <see langword="null" />。</param>
    /// <param name="Conflict">清單已被其他作業修改時為 <see langword="true" />。</param>
    public sealed record SaveResult(bool Saved, Snapshot? Snapshot, bool Conflict);
}
