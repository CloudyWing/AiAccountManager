using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>
/// 使用 Windows 通知區管理圖示與氣泡。
/// </summary>
public sealed class WindowsTrayService : ITrayService {
    private readonly Dispatcher dispatcher;
    private Forms.NotifyIcon? icon;
    private Icon? applicationIcon;

    /// <summary>
    /// 建立系統匣圖示與選單。
    /// </summary>
    public WindowsTrayService() {
        dispatcher = System.Windows.Application.Current.Dispatcher;
    }

    private Forms.NotifyIcon CreateIcon() {
        System.Windows.Resources.StreamResourceInfo resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/ai-account-manager.ico")
        ) ?? throw new InvalidOperationException("無法讀取系統匣圖示。");
        using (resource.Stream) {
            using Icon loaded = new(resource.Stream);
            applicationIcon = (Icon)loaded.Clone();
        }
        Forms.ContextMenuStrip menu = new();
        menu.Items.Add("開啟", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("結束", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        Forms.NotifyIcon result = new() { Icon = applicationIcon, Text = AppIdentity.DisplayName, ContextMenuStrip = menu };
        result.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        result.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <inheritdoc />
    public event EventHandler? OpenRequested;

    /// <inheritdoc />
    public event EventHandler? ExitRequested;

    /// <inheritdoc />
    public event EventHandler? BalloonClicked;

    /// <inheritdoc />
    public void SetVisible(bool visible) => dispatcher.Invoke(() => {
        if (visible) {
            icon ??= CreateIcon();
        }
        if (icon is not null) {
            icon.Visible = visible;
        }
    });

    /// <inheritdoc />
    public void ShowBalloon(string title, string message) => dispatcher.Invoke(() => {
        (icon ?? throw new InvalidOperationException("系統匣圖示尚未顯示。")).ShowBalloonTip(5000, title, message, Forms.ToolTipIcon.Info);
    });

    /// <inheritdoc />
    public void Dispose() => dispatcher.Invoke(() => {
        if (icon is not null) {
            icon.Visible = false;
            icon.ContextMenuStrip?.Dispose();
            icon.Dispose();
        }
        applicationIcon?.Dispose();
    });
}
