using CloudyWing.AiAccountManager.Application.Settings;
using Microsoft.Win32;

namespace CloudyWing.AiAccountManager.Windows.Runtime;

/// <summary>
/// 管理目前使用者的 Windows 開機啟動項。
/// </summary>
public sealed class WindowsStartupRegistration : SettingsService.IStartupRegistration {
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AiAccountManager";

    /// <inheritdoc />
    public string? Read() {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) as string;
    }

    /// <inheritdoc />
    public void SetEnabled(bool enabled) {
        string? path = Environment.ProcessPath;
        if (enabled && string.IsNullOrWhiteSpace(path)) {
            throw new InvalidOperationException("無法取得程式執行檔路徑。");
        }
        Restore(enabled ? $"\"{path}\" --start-minimized" : null);
    }

    /// <inheritdoc />
    public void Restore(string? value) {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (value is null) {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        } else {
            key.SetValue(ValueName, value, RegistryValueKind.String);
        }
    }
}
