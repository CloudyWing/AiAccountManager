using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 定義唯讀檢查 Codex Desktop 程序中繼資料的契約。
/// </summary>
public interface ICodexDesktopProcessStatusSource {
    /// <summary>
    /// 取得目前符合 Codex Desktop 套件路徑的程序狀態。
    /// </summary>
    /// <param name="cancellationToken">取消檢查的權杖。</param>
    /// <returns>獨立於本機認證資料的程序狀態與檢查時間。</returns>
    Task<CodexDesktopProcessStatusSnapshot> ReadStatusAsync(CancellationToken cancellationToken);
}
