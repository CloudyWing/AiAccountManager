using System.Globalization;

namespace CloudyWing.AiAccountManager.Application.Settings;

/// <summary>
/// 應用程式設定。
/// </summary>
/// <param name="SchemaVersion">設定檔版本。</param>
/// <param name="QuotaRefreshIntervalMinutes">額度定期刷新間隔（分鐘）。</param>
/// <param name="UpdateFeedUrl">更新來源網址；未設定時使用預設來源。</param>
/// <param name="ThemeMode">色彩主題模式；舊設定檔缺少此欄位時為跟隨系統。</param>
/// <param name="MinimizeToTray">關閉視窗時是否縮至系統匣。</param>
/// <param name="StartWithWindows">是否在目前使用者登入時啟動。</param>
/// <param name="LowQuotaNotificationsEnabled">是否啟用額度偏低通知。</param>
/// <param name="LowQuotaThresholdPercent">通知門檻百分比。</param>
/// <param name="IsCodexResetNewsEnabled">是否啟用 Codex 重置消息。</param>
/// <param name="AutoUseResetCredits">是否自動使用即將到期的重置券。</param>
/// <param name="AutoUseResetCreditsMinutes">到期前自動使用的分鐘數。</param>
public sealed record AppSettings(
    int SchemaVersion,
    int QuotaRefreshIntervalMinutes,
    string? UpdateFeedUrl,
    AppThemeMode ThemeMode = AppThemeMode.System,
    bool MinimizeToTray = false,
    bool StartWithWindows = false,
    bool LowQuotaNotificationsEnabled = true,
    int LowQuotaThresholdPercent = 20,
    bool IsCodexResetNewsEnabled = true,
    bool AutoUseResetCredits = false,
    int AutoUseResetCreditsMinutes = 30
) {
    /// <summary>
    /// 目前支援的設定檔版本。
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// 取得預設設定，包含五分鐘的額度刷新間隔。
    /// </summary>
    public static AppSettings Defaults => new(CurrentSchemaVersion, 5, null);

    /// <summary>
    /// 驗證設定版本、主題、刷新間隔與更新來源網址。
    /// </summary>
    /// <param name="error">驗證失敗時的訊息；通過時為空字串。</param>
    /// <returns>若設定有效則為 <see langword="true" />；否則為 <see langword="false" />。</returns>
    public bool IsValid(out string error) {
        if (SchemaVersion != CurrentSchemaVersion) {
            error = "不支援的設定版本。";
            return false;
        }

        if (!Enum.IsDefined(ThemeMode)) {
            error = "不支援的主題模式。";
            return false;
        }

        if (QuotaRefreshIntervalMinutes is not (1 or 5 or 15 or 30)) {
            error = "額度刷新間隔只接受 1、5、15 或 30 分鐘。";
            return false;
        }

        if (LowQuotaThresholdPercent is < 5 or > 50) {
            error = "額度通知門檻必須介於 5% 至 50%。";
            return false;
        }

        if (AutoUseResetCreditsMinutes is < 5 or > 1440) {
            error = "自動使用重置券的分鐘數必須介於 5 至 1440。";
            return false;
        }

        if (UpdateFeedUrl is not null
            && (!Uri.TryCreate(UpdateFeedUrl, UriKind.Absolute, out Uri? feedUri)
                || !string.Equals(feedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(feedUri.Host)
                || !string.IsNullOrEmpty(feedUri.UserInfo))) {
            error = "更新來源必須是 HTTPS 網址，且不可包含帳號密碼。";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    /// 以不受目前文化影響的格式表示刷新間隔。
    /// </summary>
    /// <param name="minutes">刷新間隔的分鐘數。</param>
    /// <returns>分鐘數的字串表示。</returns>
    public static string FormatRefreshInterval(int minutes) {
        return minutes.ToString(CultureInfo.InvariantCulture);
    }
}
