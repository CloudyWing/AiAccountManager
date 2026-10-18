using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 定義依 Claude profile 識別碼保存 quota snapshot 的契約。
/// </summary>
public interface IClaudeQuotaRepository {
    /// <summary>
    /// 載入指定 profile 的最近一次額度結果。
    /// </summary>
    /// <param name="profileId">額度所屬 profile 識別碼。</param>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>額度資料及其版本；沒有快照時為 <see langword="null" />。</returns>
    Task<Snapshot?> LoadAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>
    /// 以 expected hash 保存指定 profile 的額度快照。
    /// </summary>
    /// <param name="view">額度資料與最後一次查詢狀態。</param>
    /// <param name="expectedSha256">讀取額度資料時取得的版本雜湊。</param>
    /// <param name="cancellationToken">取消寫入作業的權杖。</param>
    /// <returns>保存結果及更新後的資料。</returns>
    Task<SaveResult> SaveAsync(
        ClaudeQuotaSnapshot.View view,
        string? expectedSha256,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 載入結果及內容雜湊。
    /// </summary>
    /// <param name="View">額度檢視。</param>
    /// <param name="Sha256">檔案內容的 SHA-256。</param>
    public sealed record Snapshot(ClaudeQuotaSnapshot.View View, string Sha256);

    /// <summary>
    /// 描述額度資料保存結果。
    /// </summary>
    /// <param name="Saved">本次寫入是否成功。</param>
    /// <param name="Snapshot">成功時更新後的資料。</param>
    /// <param name="Conflict">資料已被其他操作更新時為 <see langword="true" />。</param>
    public sealed record SaveResult(bool Saved, Snapshot? Snapshot, bool Conflict);
}
