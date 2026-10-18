namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>
/// 判定視窗關閉與系統匣圖示的顯示方式。
/// </summary>
public static class TrayPolicy {
    /// <summary>判定關閉視窗是否改為隱藏。</summary>
    /// <param name="minimizeToTray">是否啟用縮至系統匣。</param>
    /// <param name="exitRequested">是否由系統匣要求結束。</param>
    /// <returns>應隱藏視窗時為 true。</returns>
    public static bool ShouldHideOnClose(bool minimizeToTray, bool exitRequested) => minimizeToTray && !exitRequested;

    /// <summary>判定系統匣圖示是否應顯示。</summary>
    /// <param name="minimizeToTray">是否啟用縮至系統匣。</param>
    /// <param name="notificationsEnabled">是否啟用通知。</param>
    /// <param name="windowVisible">主視窗是否可見。</param>
    /// <returns>應顯示圖示時為 true。</returns>
    public static bool ShouldShowIcon(bool minimizeToTray, bool notificationsEnabled, bool windowVisible)
        => minimizeToTray || notificationsEnabled || !windowVisible;

    /// <summary>依設定更新系統匣圖示。</summary>
    /// <param name="tray">系統匣服務。</param>
    /// <param name="minimizeToTray">是否啟用縮至系統匣。</param>
    /// <param name="notificationsEnabled">是否啟用通知。</param>
    /// <param name="windowVisible">主視窗是否可見。</param>
    public static void UpdateVisibility(ITrayService tray, bool minimizeToTray, bool notificationsEnabled, bool windowVisible) {
        tray.SetVisible(ShouldShowIcon(minimizeToTray, notificationsEnabled, windowVisible));
    }
}
