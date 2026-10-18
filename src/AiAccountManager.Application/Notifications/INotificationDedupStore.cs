namespace CloudyWing.AiAccountManager.Application.Notifications;

/// <summary>保存已送出的通知週期。</summary>
public interface INotificationDedupStore {
    /// <summary>載入已通知的週期鍵。</summary>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>已保存的週期鍵。</returns>
    Task<IReadOnlyCollection<string>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>原子保存已通知的週期鍵。</summary>
    /// <param name="keys">已通知的週期鍵。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>保存工作。</returns>
    Task SaveAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken);
}
