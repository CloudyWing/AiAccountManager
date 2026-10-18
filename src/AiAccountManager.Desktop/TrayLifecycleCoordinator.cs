namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>協調系統匣的啟動、開啟與結束事件。</summary>
public sealed class TrayLifecycleCoordinator : IDisposable {
    private readonly ITrayService tray;
    private readonly Action open;
    private readonly Action exit;

    /// <summary>初始化系統匣生命週期。</summary>
    /// <param name="tray">系統匣服務。</param>
    /// <param name="open">開啟視窗的動作。</param>
    /// <param name="exit">要求安全結束的動作。</param>
    public TrayLifecycleCoordinator(ITrayService tray, Action open, Action exit) {
        this.tray = tray;
        this.open = open;
        this.exit = exit;
        tray.OpenRequested += OnOpen;
        tray.ExitRequested += OnExit;
    }

    /// <summary>依啟動參數顯示視窗。</summary>
    /// <param name="startMinimized">是否以隱藏視窗啟動。</param>
    /// <param name="show">顯示主視窗的動作。</param>
    public void Start(bool startMinimized, Action show) {
        if (!startMinimized) {
            show();
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        tray.OpenRequested -= OnOpen;
        tray.ExitRequested -= OnExit;
    }

    private void OnOpen(object? sender, EventArgs e) => open();

    private void OnExit(object? sender, EventArgs e) => exit();
}
