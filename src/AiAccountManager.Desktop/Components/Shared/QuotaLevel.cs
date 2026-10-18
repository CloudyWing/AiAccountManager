using MudBlazor;

namespace CloudyWing.AiAccountManager.Desktop.Components.Shared;

/// <summary>
/// 依剩餘額度百分比決定 Claude 與 Codex 共用的狀態色。
/// </summary>
public static class QuotaLevel {
    /// <summary>
    /// 剩餘額度達此百分比以上視為充足。
    /// </summary>
    public const double HealthyThreshold = 50d;

    /// <summary>
    /// 剩餘額度低於此百分比視為不足。
    /// </summary>
    public const double LowThreshold = 20d;

    /// <summary>
    /// 取得剩餘額度對應的 MudBlazor 狀態色。
    /// </summary>
    /// <param name="remainingPercent">0 至 100 的剩餘百分比。</param>
    /// <returns>充足為 Success、偏低為 Warning、不足為 Error。</returns>
    public static Color ForRemaining(double remainingPercent) {
        return remainingPercent switch {
            >= HealthyThreshold => Color.Success,
            >= LowThreshold => Color.Warning,
            _ => Color.Error
        };
    }

    /// <summary>
    /// 取得剩餘額度對應的簡短文字，供無法辨識顏色的使用者辨認狀態。
    /// </summary>
    /// <param name="remainingPercent">0 至 100 的剩餘百分比。</param>
    /// <returns>「充足」、「偏低」或「不足」。</returns>
    public static string TextForRemaining(double remainingPercent) {
        return remainingPercent switch {
            >= HealthyThreshold => "充足",
            >= LowThreshold => "偏低",
            _ => "不足"
        };
    }
}
