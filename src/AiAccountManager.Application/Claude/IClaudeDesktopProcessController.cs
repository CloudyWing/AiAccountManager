namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 定義 Claude Desktop 程序狀態、正常關閉與啟動契約。
/// </summary>
public interface IClaudeDesktopProcessController {
    /// <summary>
    /// 取得指定安裝的 Desktop 程序狀態。
    /// </summary>
    /// <param name="installation">唯一定位的 Desktop 安裝。</param>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>目前程序狀態。</returns>
    Task<ProcessStatus> GetStatusAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 核對同一程序 handle 的 SID、建立時間與安裝身分後要求正常關閉，並等待程序離開。
    /// </summary>
    /// <param name="installation">唯一定位的 Desktop 安裝。</param>
    /// <param name="timeout">等待正常關閉的上限。</param>
    /// <param name="cancellationToken">取消關閉作業的權杖。</param>
    /// <returns>程序已關閉時為 <see langword="true" />；逾時仍執行時為 <see langword="false" />。</returns>
    Task<bool> CloseGracefullyAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        TimeSpan timeout,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 使用安裝描述重新啟動 Claude Desktop。
    /// </summary>
    /// <param name="installation">唯一定位的 Desktop 安裝。</param>
    /// <param name="cancellationToken">取消啟動作業的權杖。</param>
    Task LaunchAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 定義 Claude Desktop 程序狀態。
    /// </summary>
    public enum ProcessStatus {
        /// <summary>程序目前執行中。</summary>
        Running,

        /// <summary>程序目前未執行。</summary>
        NotRunning,

        /// <summary>安裝型態或程序狀態無法辨識。</summary>
        Unknown
    }
}
