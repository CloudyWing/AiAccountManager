namespace CloudyWing.AiAccountManager.Application.Runtime;

/// <summary>
/// 提供目前時間與延遲等待，讓排程與逾時邏輯可在測試中控制時間。
/// </summary>
public interface IClock {
    /// <summary>
    /// 取得目前的 UTC 時間。
    /// </summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// 等待指定的時間間隔。
    /// </summary>
    /// <param name="delay">等待時間。</param>
    /// <param name="cancellationToken">取消等待的權杖。</param>
    /// <returns>等待完成的工作。</returns>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
