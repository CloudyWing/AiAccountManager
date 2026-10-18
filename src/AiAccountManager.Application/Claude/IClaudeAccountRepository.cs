using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;

namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 定義 Claude profile、受保護 Desktop snapshot 與切換 journal 的保存契約。
/// </summary>
public interface IClaudeAccountRepository {
    /// <summary>
    /// 載入已保存的 Claude profile catalog。
    /// </summary>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>profile 清單及 catalog 版本雜湊。</returns>
    Task<Snapshot> LoadProfilesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 載入指定 profile 的受保護 Desktop snapshot。
    /// </summary>
    /// <param name="profileId">profile 識別碼。</param>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>已解密至記憶體的 snapshot；找不到時為 <see langword="null" />。</returns>
    Task<ClaudeAccountProfile.DesktopSnapshot?> LoadDesktopSnapshotAsync(
        Guid profileId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 以 catalog hash 執行 optimistic concurrency，先原子發布包含來源綁定的受保護 snapshot，再發布 catalog 引用。
    /// </summary>
    /// <param name="profile">要保存的 profile。</param>
    /// <param name="desktopSnapshot">要保護並保存的 Desktop snapshot。</param>
    /// <param name="expectedSha256">讀取 catalog 時取得的 SHA-256。</param>
    /// <param name="cancellationToken">取消寫入作業的權杖。</param>
    /// <returns>保存結果及更新後的 catalog snapshot。</returns>
    Task<SaveResult> SaveProfileAsync(
        ClaudeAccountProfile profile,
        ClaudeAccountProfile.DesktopSnapshot desktopSnapshot,
        string expectedSha256,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 以 catalog hash 執行 optimistic concurrency，保存 profile 的排序、別名與移除結果，不改寫任何 Desktop snapshot。
    /// </summary>
    /// <param name="profiles">新的 profile 順序；每筆須為既有 profile，除 <see cref="ClaudeAccountProfile.Alias" /> 外內容不變，未列出的 profile 視為移除。</param>
    /// <param name="expectedSha256">讀取 catalog 時取得的 SHA-256。</param>
    /// <param name="cancellationToken">取消寫入作業的權杖。</param>
    /// <returns>保存結果及更新後的 catalog snapshot。</returns>
    Task<SaveResult> SaveCatalogMetadataAsync(
        IReadOnlyList<ClaudeAccountProfile> profiles,
        string expectedSha256,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 載入唯一可能未終止的切換 journal，保留沒有來源證據的舊記錄。
    /// </summary>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>最近一筆切換 journal；尚無交易時為 <see langword="null" />。</returns>
    Task<ClaudeSwitchTransaction?> LoadSwitchTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 原子保存切換 journal，保護完整來源綁定並維持交易識別與 profile 版次。
    /// </summary>
    /// <param name="transaction">切換交易資料。</param>
    /// <param name="cancellationToken">取消寫入作業的權杖。</param>
    Task SaveSwitchTransactionAsync(
        ClaudeSwitchTransaction transaction,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 載入 profile catalog 的不可變檢視。
    /// </summary>
    /// <param name="Profiles">已保存的 profile。</param>
    /// <param name="Sha256">catalog 內容的 SHA-256。</param>
    public sealed record Snapshot(IReadOnlyList<ClaudeAccountProfile> Profiles, string Sha256);

    /// <summary>
    /// 描述 profile catalog 寫入是否成功。
    /// </summary>
    /// <param name="Saved">本次寫入是否成功。</param>
    /// <param name="Snapshot">成功時的新版 catalog。</param>
    /// <param name="Conflict">catalog 已被其他操作更新時為 <see langword="true" />。</param>
    public sealed record SaveResult(bool Saved, Snapshot? Snapshot, bool Conflict);
}
