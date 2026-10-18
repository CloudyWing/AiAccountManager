namespace CloudyWing.AiAccountManager.Application.Settings;

/// <summary>
/// 讀取與保存應用程式設定，並以內容雜湊偵測並行修改。
/// </summary>
public interface ISettingsRepository {
    /// <summary>
    /// 讀取目前的設定；設定檔不存在時建立預設設定。
    /// </summary>
    /// <param name="cancellationToken">取消讀取的權杖。</param>
    /// <returns>設定內容與其檔案雜湊。</returns>
    Task<Snapshot> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 在檔案仍為預期版本時保存設定。
    /// </summary>
    /// <param name="settings">要保存的設定。</param>
    /// <param name="expectedSha256">讀取時取得的檔案雜湊。</param>
    /// <param name="cancellationToken">取消保存的權杖。</param>
    /// <returns>保存結果；檔案已被其他作業修改時回報衝突。</returns>
    Task<SaveResult> SaveAsync(
        AppSettings settings,
        string expectedSha256,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 設定內容與其檔案雜湊。
    /// </summary>
    /// <param name="Settings">設定內容。</param>
    /// <param name="Sha256">設定檔內容的 SHA-256。</param>
    public sealed record Snapshot(AppSettings Settings, string Sha256);

    /// <summary>
    /// 設定保存結果。
    /// </summary>
    /// <param name="Saved">若已寫入則為 <see langword="true" />；否則為 <see langword="false" />。</param>
    /// <param name="Snapshot">寫入後的設定；未寫入時為 <see langword="null" />。</param>
    /// <param name="Conflict">若檔案已被其他作業修改則為 <see langword="true" />；否則為 <see langword="false" />。</param>
    public sealed record SaveResult(bool Saved, Snapshot? Snapshot, bool Conflict);
}
