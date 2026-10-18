namespace CloudyWing.AiAccountManager.Application.Accounts;

/// <summary>
/// 描述 Codex Desktop 程序中繼資料的檢查結果。
/// </summary>
public enum CodexDesktopProcessStatus {
    /// <summary>
    /// 找到位於 Codex Desktop 套件目錄的程序。
    /// </summary>
    Running,

    /// <summary>
    /// 已成功檢查且沒有符合的 Codex Desktop 程序。
    /// </summary>
    NotRunning,

    /// <summary>
    /// 無法確認程序是否屬於 Codex Desktop。
    /// </summary>
    Unknown
}
