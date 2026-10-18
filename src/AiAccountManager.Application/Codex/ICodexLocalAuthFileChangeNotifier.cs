namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 定義本機 Codex 認證檔變更監看與可用狀態通知的契約。
/// </summary>
public interface ICodexLocalAuthFileChangeNotifier : IAsyncDisposable {
    /// <summary>
    /// 在認證檔發生已 debounce 的變更時通知訂閱者。
    /// </summary>
    event Action? FileChanged;

    /// <summary>
    /// 在檔案監看的可用狀態改變時通知訂閱者。
    /// </summary>
    event Action<bool>? MonitoringAvailabilityChanged;

    /// <summary>
    /// 啟動本機認證檔變更監看。
    /// </summary>
    /// <param name="cancellationToken">取消啟動的權杖。</param>
    /// <returns>代表監看啟動工作的工作。</returns>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 停止本機認證檔變更監看。
    /// </summary>
    /// <returns>代表監看停止工作的工作。</returns>
    Task StopAsync();
}
