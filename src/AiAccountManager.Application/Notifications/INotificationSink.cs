namespace CloudyWing.AiAccountManager.Application.Notifications;

/// <summary>傳送通知並回報使用者啟用的導覽目標。</summary>
public interface INotificationSink {
    /// <summary>通知啟用時發生，提供經驗證的 provider。</summary>
    event Action<string>? Activated;

    /// <summary>傳送通知。</summary>
    /// <param name="request">通知內容。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>成功送出時為 true。</returns>
    Task<bool> SendAsync(NotificationRequest request, CancellationToken cancellationToken);
}
