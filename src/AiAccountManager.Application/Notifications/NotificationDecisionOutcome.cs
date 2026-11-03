namespace CloudyWing.AiAccountManager.Application.Notifications;

/// <summary>額度通知的評估結果。</summary>
public enum NotificationDecisionOutcome {
    /// <summary>已送出通知。</summary>
    Sent,
    /// <summary>通知傳送失敗。</summary>
    SendFailed,
    /// <summary>此週期已通知。</summary>
    Deduplicated,
    /// <summary>同輪有其他優先通知。</summary>
    Superseded
}
