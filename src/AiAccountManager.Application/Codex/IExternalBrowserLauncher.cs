namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 以系統預設瀏覽器開啟外部網址，例如 Codex 登入頁。
/// </summary>
public interface IExternalBrowserLauncher {
    /// <summary>
    /// 以系統預設瀏覽器開啟指定網址。
    /// </summary>
    /// <param name="uri">要開啟的網址。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>開啟要求完成的工作。</returns>
    Task OpenAsync(Uri uri, CancellationToken cancellationToken);
}
