using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 定義 Codex Desktop 程序的狀態讀取、正常關閉與重新開啟契約。
/// </summary>
public interface ICodexDesktopProcessController {
    /// <summary>
    /// 讀取 Codex Desktop 程序狀態。
    /// </summary>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>程序狀態。</returns>
    Task<CodexDesktopProcessStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 對 Codex Desktop 送出正常關閉要求並等待所有程序結束，不強制結束程序。
    /// </summary>
    /// <param name="timeout">等待程序結束的上限。</param>
    /// <param name="cancellationToken">取消等待的權杖。</param>
    /// <returns>關閉結果與重新開啟所需的 AppUserModelId。</returns>
    Task<CloseResult> CloseGracefullyAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// 以 AppUserModelId 開啟 Codex Desktop。
    /// </summary>
    /// <param name="appUserModelId">Codex Desktop 的 AppUserModelId。</param>
    /// <param name="cancellationToken">取消開啟作業的權杖。</param>
    Task LaunchAsync(string appUserModelId, CancellationToken cancellationToken);

    /// <summary>
    /// 描述正常關閉的結果。
    /// </summary>
    /// <param name="Closed">所有 Codex Desktop 程序已在時限內結束時為 <see langword="true" />。</param>
    /// <param name="AppUserModelId">關閉前辨識到的 AppUserModelId；無法辨識時為 <see langword="null" />。</param>
    public sealed record CloseResult(bool Closed, string? AppUserModelId);
}
