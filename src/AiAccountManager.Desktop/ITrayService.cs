namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>
/// 提供系統匣操作及氣泡通知。
/// </summary>
public interface ITrayService : IDisposable {
    /// <summary>使用者要求開啟視窗時發生。</summary>
    event EventHandler? OpenRequested;

    /// <summary>使用者要求結束程式時發生。</summary>
    event EventHandler? ExitRequested;

    /// <summary>使用者點擊氣泡時發生。</summary>
    event EventHandler? BalloonClicked;

    /// <summary>設定系統匣圖示是否可見。</summary>
    /// <param name="visible">是否顯示圖示。</param>
    void SetVisible(bool visible);

    /// <summary>顯示通知氣泡。</summary>
    /// <param name="title">通知標題。</param>
    /// <param name="message">通知內容。</param>
    void ShowBalloon(string title, string message);
}
