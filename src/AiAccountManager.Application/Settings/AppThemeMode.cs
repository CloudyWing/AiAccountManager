using System.Text.Json.Serialization;

namespace CloudyWing.AiAccountManager.Application.Settings;

/// <summary>
/// 應用程式的色彩主題模式。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppThemeMode>))]
public enum AppThemeMode {
    /// <summary>
    /// 跟隨 Windows 的淺色或深色設定。
    /// </summary>
    System,

    /// <summary>
    /// 固定使用淺色主題。
    /// </summary>
    Light,

    /// <summary>
    /// 固定使用深色主題。
    /// </summary>
    Dark
}
