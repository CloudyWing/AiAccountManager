namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>傳送與正式額度偏低通知同格式的測試氣泡。</summary>
public interface ILowQuotaTestNotificationService {
    /// <summary>傳送測試通知。</summary>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>氣泡已交給系統匣顯示時為 true。</returns>
    Task<bool> SendAsync(CancellationToken cancellationToken);
}
