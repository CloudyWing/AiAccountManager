namespace CloudyWing.AiAccountManager.Application.Updates;

/// <summary>
/// 檢查、下載與套用應用程式更新。
/// </summary>
public interface IUpdateAdapter {
    /// <summary>
    /// 檢查更新來源是否有較新的版本。
    /// </summary>
    /// <param name="feedUri">更新來源網址。</param>
    /// <param name="cancellationToken">取消檢查的權杖。</param>
    /// <returns>是否有可用更新及其版本。</returns>
    Task<UpdateCheckResult> CheckAsync(Uri feedUri, CancellationToken cancellationToken);

    /// <summary>
    /// 下載可用的更新。
    /// </summary>
    /// <param name="feedUri">更新來源網址。</param>
    /// <param name="progress">回報下載進度百分比。</param>
    /// <param name="cancellationToken">取消下載的權杖。</param>
    /// <returns>下載完成的工作。</returns>
    Task DownloadAsync(Uri feedUri, IProgress<int> progress, CancellationToken cancellationToken);

    /// <summary>
    /// 套用已下載的更新並重新啟動應用程式。
    /// </summary>
    /// <param name="feedUri">更新來源網址。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>套用要求完成的工作。</returns>
    Task ApplyAndRestartAsync(Uri feedUri, CancellationToken cancellationToken);
}

/// <summary>
/// 更新檢查結果。
/// </summary>
/// <param name="UpdateAvailable">若有較新版本則為 <see langword="true" />；否則為 <see langword="false" />。</param>
/// <param name="Version">可用的版本；沒有更新時為 <see langword="null" />。</param>
public sealed record UpdateCheckResult(bool UpdateAvailable, string? Version);

/// <summary>
/// 更新流程的目前狀態。
/// </summary>
public enum UpdateStatus {
    /// <summary>
    /// 尚未設定更新來源。
    /// </summary>
    NotConfigured,

    /// <summary>
    /// 已設定來源，尚未檢查。
    /// </summary>
    Idle,

    /// <summary>
    /// 正在檢查更新。
    /// </summary>
    Checking,

    /// <summary>
    /// 目前已是最新版本。
    /// </summary>
    UpToDate,

    /// <summary>
    /// 有可下載的更新。
    /// </summary>
    Available,

    /// <summary>
    /// 正在下載更新。
    /// </summary>
    Downloading,

    /// <summary>
    /// 更新已下載，可以套用。
    /// </summary>
    ReadyToApply,

    /// <summary>
    /// 正在套用更新並重新啟動。
    /// </summary>
    Applying,

    /// <summary>
    /// 更新已套用。
    /// </summary>
    Applied,

    /// <summary>
    /// 更新流程失敗。
    /// </summary>
    Error
}

/// <summary>
/// 提供給畫面顯示的更新服務狀態。
/// </summary>
/// <param name="Status">目前狀態。</param>
/// <param name="Version">相關的版本；沒有時為 <see langword="null" />。</param>
/// <param name="ProgressPercent">下載進度百分比。</param>
/// <param name="Message">給使用者的訊息；沒有時為 <see langword="null" />。</param>
/// <param name="FeedUri">目前使用的更新來源網址；未設定時為 <see langword="null" />。</param>
public sealed record UpdateServiceState(
    UpdateStatus Status,
    string? Version,
    int ProgressPercent,
    string? Message,
    string? FeedUri
);
