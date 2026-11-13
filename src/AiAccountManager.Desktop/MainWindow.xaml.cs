using System.ComponentModel;
using System.IO;
using System.Windows;
using CloudyWing.AiAccountManager.Application.Operations;
using CloudyWing.AiAccountManager.Application.Settings;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace CloudyWing.AiAccountManager.Desktop;

public partial class MainWindow : Window {
    private static readonly TimeSpan WebViewDisposeTimeout = TimeSpan.FromSeconds(5);
    private static readonly WebViewRouteRestorer.Timing RouteRestoreTiming = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(100),
        50
    );

    private readonly IHost host;
    private readonly IServiceProvider services;
    private readonly ExclusiveOperationCoordinator operationCoordinator;
    private readonly StartupRenderProbe renderProbe;
    private readonly string webViewDataDirectory;
    private readonly SettingsService settings;
    private readonly ITrayService tray;
    private readonly WebViewRecoveryLog recoveryLog;
    private readonly WebViewRecoveryPolicy recoveryPolicy;
    private readonly WebViewRebuildScheduler rebuildScheduler;
    private readonly DispatcherTimer rebuildTimer;
    private BlazorWebView currentWebView;
    private bool closeAllowed;
    private bool closeInProgress;
    private bool exitRequested;
    private bool rebuilding;
    private string? pendingRebuildReason;
    private bool windowClosed;

    public MainWindow(IHost host, IServiceProvider services, string webViewDataDirectory) {
        this.host = host;
        ArgumentException.ThrowIfNullOrWhiteSpace(webViewDataDirectory);
        this.webViewDataDirectory = Path.GetFullPath(webViewDataDirectory);
        this.services = services;
        operationCoordinator = services.GetRequiredService<ExclusiveOperationCoordinator>();
        renderProbe = services.GetRequiredService<StartupRenderProbe>();
        settings = services.GetRequiredService<SettingsService>();
        tray = services.GetRequiredService<ITrayService>();
        recoveryLog = new WebViewRecoveryLog(ResolveDataRoot(this.webViewDataDirectory), probe: renderProbe);
        recoveryPolicy = new WebViewRecoveryPolicy(() => DateTimeOffset.UtcNow);
        rebuildTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        rebuildTimer.Tick += OnRebuildTimerTick;
        rebuildScheduler = new WebViewRebuildScheduler(
            () => DateTimeOffset.UtcNow,
            ScheduleRebuildTick,
            () => operationCoordinator.HasActiveOperations,
            reason => _ = RebuildWebViewAsync(reason),
            recoveryLog.Write
        );
        InitializeComponent();
        if (renderProbe.IsEnabled) {
            SourceInitialized += OnSourceInitialized;
            Closed += OnClosed;
        }
        Closed += OnWindowClosed;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
#if DEBUG
        PreviewKeyDown += OnDebugPreviewKeyDown;
#endif
        currentWebView = CreateWebView();
        webViewHost.Children.Add(currentWebView);
    }

    /// <summary>
    /// 透過正常關閉流程結束應用程式。
    /// </summary>
    public void RequestExit() {
        exitRequested = true;
        Close();
    }

    /// <summary>
    /// 登記一次 WebView 重建要求，短時間內的重複要求會合併，且等待獨占作業結束後才執行。
    /// </summary>
    /// <param name="reason">觸發原因，寫入本機記錄。</param>
    internal void RequestWebViewRecovery(string reason) {
        if (!Dispatcher.CheckAccess()) {
            _ = Dispatcher.BeginInvoke(new Action(() => RequestWebViewRecovery(reason)));
            return;
        }

        if (windowClosed || closeInProgress || closeAllowed) {
            return;
        }

        rebuildScheduler.Trigger(reason);
    }

    /// <summary>
    /// 判斷 Dispatcher 未處理例外是否為可吞下的 WebView2 繪圖復原例外，可吞下時排程重建。
    /// </summary>
    /// <param name="exception">未處理例外。</param>
    /// <returns>可吞下時為 <see langword="true"/>。</returns>
    internal bool TrySwallowRenderException(Exception exception) {
        bool swallow = recoveryPolicy.ShouldSwallow(exception, out string reason);
        string typeName = exception.GetType().FullName ?? "unknown";
        if (swallow) {
            recoveryLog.Write($"exception.swallowed:{typeName}:0x{exception.HResult:X8}");
            RequestWebViewRecovery("render-exception");
        } else {
            recoveryLog.Write($"exception.not-swallowed:{reason}:{typeName}:0x{exception.HResult:X8}");
        }

        return swallow;
    }

    /// <summary>開啟指定 provider 的管理頁。</summary>
    /// <param name="provider">已驗證的 provider。</param>
    /// <returns>導覽工作。</returns>
    public async Task NavigateToProviderAsync(string provider) {
        if (provider is not ("claude" or "codex")) {
            return;
        }
        for (int attempt = 0; attempt < 50 && !closeAllowed; attempt++) {
            bool dispatched = await currentWebView.TryDispatchAsync(serviceProvider => {
                serviceProvider.GetRequiredService<NavigationManager>().NavigateTo($"/{provider}");
            });
            if (dispatched) {
                return;
            }
            await Task.Delay(100);
        }
    }

    private static string ResolveDataRoot(string webViewDirectory) {
        string trimmed = Path.TrimEndingDirectorySeparator(webViewDirectory);
        return Path.GetDirectoryName(trimmed) ?? trimmed;
    }

    private BlazorWebView CreateWebView() {
        BlazorWebView view = new() {
            Name = "blazorWebView",
            HostPage = "wwwroot/index.html"
        };
        view.RootComponents.Add(new RootComponent {
            Selector = "#app",
            ComponentType = typeof(Components.App)
        });
        view.BlazorWebViewInitializing += OnBlazorWebViewInitializing;
        view.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
        view.Services = services;
        return view;
    }

    private void ScheduleRebuildTick(TimeSpan delay) {
        rebuildTimer.Stop();
        rebuildTimer.Interval = delay;
        rebuildTimer.Start();
    }

    private void OnRebuildTimerTick(object? sender, EventArgs e) {
        rebuildTimer.Stop();
        rebuildScheduler.OnTick();
    }

    /// <summary>
    /// 以新的 BlazorWebView 取代目前的控制項，並嘗試回到重建前的頁面。
    /// </summary>
    /// <param name="reason">觸發原因，寫入本機記錄。</param>
    /// <returns>表示重建完成的工作。</returns>
    internal async Task RebuildWebViewAsync(string reason) {
        if (windowClosed || closeInProgress || closeAllowed) {
            recoveryLog.Write("rebuild.skipped:closing");
            return;
        }

        if (rebuilding) {
            // 重建涵蓋舊控制項釋放與路由還原，期間的觸發延到完成後再排程，避免遺失尚未還原的頁面位置。
            pendingRebuildReason = reason;
            recoveryLog.Write("rebuild.queued:in-progress");
            return;
        }

        rebuilding = true;
        try {
            await RebuildWebViewCoreAsync(reason);
        } finally {
            rebuilding = false;
            if (pendingRebuildReason is string pendingReason) {
                pendingRebuildReason = null;
                RequestWebViewRecovery(pendingReason);
            }
        }
    }

    private async Task RebuildWebViewCoreAsync(string reason) {
        BlazorWebView previous = currentWebView;
        BlazorWebView? replacement = null;
        string? route = null;
        try {
            recoveryLog.Write("rebuild.started:" + reason);
            route = await CaptureRouteAsync(previous);
            if (windowClosed || closeInProgress || closeAllowed) {
                recoveryLog.Write("rebuild.skipped:closing");
                return;
            }

            replacement = CreateWebView();
            webViewHost.Children.Add(replacement);
            // 舊控制項留在視覺樹內直到釋放完成，BlazorWebView 的釋放需要頁面仍可回應。
            previous.Visibility = Visibility.Collapsed;
            currentWebView = replacement;
        } catch (Exception exception) {
            recoveryLog.Write($"rebuild.failed:{exception.GetType().FullName}:0x{exception.HResult:X8}");
            if (replacement is not null && !ReferenceEquals(currentWebView, replacement)) {
                webViewHost.Children.Remove(replacement);
            }
            return;
        }

        await ReleaseWebViewAsync(previous);
        recoveryLog.Write("rebuild.completed:" + reason);
        if (route is not null) {
            await RestoreRouteAsync(replacement, route);
        }
    }

    private static async Task<string?> CaptureRouteAsync(BlazorWebView view) {
        string? route = null;
        try {
            bool dispatched = await view.TryDispatchAsync(serviceProvider => {
                NavigationManager navigation = serviceProvider.GetRequiredService<NavigationManager>();
                route = navigation.ToBaseRelativePath(navigation.Uri);
            }).WaitAsync(TimeSpan.FromSeconds(1));
            if (dispatched && !string.IsNullOrEmpty(route) && !route.Contains("://", StringComparison.Ordinal)) {
                return route;
            }
        } catch (Exception) {
            // 舊控制項可能已失去回應，取不到頁面位置時改回首頁即可。
        }

        return null;
    }

    private Task RestoreRouteAsync(BlazorWebView target, string route) {
        return WebViewRouteRestorer.RestoreAsync(
            () => CanRestoreRoute(target),
            shouldNavigate => target.TryDispatchAsync(serviceProvider => {
                if (shouldNavigate()) {
                    serviceProvider.GetRequiredService<NavigationManager>().NavigateTo(route);
                }
            }),
            RouteRestoreTiming,
            recoveryLog.Write
        );
    }

    private bool CanRestoreRoute(BlazorWebView target) {
        return ReferenceEquals(currentWebView, target) && !windowClosed && !closeInProgress && !closeAllowed;
    }

    private async Task ReleaseWebViewAsync(BlazorWebView view) {
        string? failure = null;
        try {
            await view.DisposeAsync().AsTask().WaitAsync(WebViewDisposeTimeout);
            recoveryLog.Write("rebuild.disposed");
        } catch (Exception exception) {
            failure = $"{exception.GetType().FullName}:0x{exception.HResult:X8}";
            recoveryLog.Write("rebuild.dispose-failed:" + failure);
        }

        // BlazorWebView 的釋放會先等待 WebView 管理員，卡住時內部的 WebView2 控制項不會被釋放，
        // 舊的 renderer 程序因此殘留。必須在控制項仍位於視覺樹時直接釋放 WebView2 控制項。
        if (failure is not null) {
            ForceDisposeWebView(view, failure);
        }

        webViewHost.Children.Remove(view);
    }

    private void ForceDisposeWebView(BlazorWebView view, string cause) {
        try {
            WebView2CompositionControl? control = FindVisualChild<WebView2CompositionControl>(view);
            if (control is null) {
                recoveryLog.Write("rebuild.webview-force-dispose-failed:control-not-found");
                return;
            }

            control.Dispose();
            recoveryLog.Write("rebuild.webview-force-disposed:" + cause);
        } catch (Exception exception) {
            recoveryLog.Write($"rebuild.webview-force-dispose-failed:{exception.GetType().FullName}:0x{exception.HResult:X8}");
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++) {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) {
                return match;
            }

            T? nested = FindVisualChild<T>(child);
            if (nested is not null) {
                return nested;
            }
        }

        return null;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect
            or SessionSwitchReason.ConsoleConnect
        ) {
            RequestWebViewRecovery("session-" + e.Reason);
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) {
        if (e.Mode == PowerModes.Resume) {
            RequestWebViewRecovery("power-resume");
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) {
        RequestWebViewRecovery("display-settings-changed");
    }

    private void OnWindowClosed(object? sender, EventArgs e) {
        windowClosed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        rebuildScheduler.Cancel();
        rebuildTimer.Stop();
    }

#if DEBUG
    private void OnDebugPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
        if (e.Key == System.Windows.Input.Key.F12
            && System.Windows.Input.Keyboard.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift)
        ) {
            e.Handled = true;
            _ = RebuildWebViewAsync("debug-hotkey");
        }
    }
#endif

    private void OnBlazorWebViewInitializing(object? sender, BlazorWebViewInitializingEventArgs e) {
        renderProbe.RecordEvent("blazor-webview.initializing.started");
        try {
            Directory.CreateDirectory(webViewDataDirectory);
            e.UserDataFolder = webViewDataDirectory;
            renderProbe.RecordEvent("blazor-webview.initializing.completed", "success");
        } catch (Exception exception) {
            renderProbe.RecordEvent("blazor-webview.initializing.failed", "failure", exception: exception);
            throw;
        }
    }

    private void OnBlazorWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e) {
        renderProbe.RecordEvent("blazor-webview.initialized");

        if (e.WebView.CoreWebView2 is not CoreWebView2 coreWebView) {
            renderProbe.RecordEvent("webview2.initialization.completed", "failure", "core-webview2-missing");
            return;
        }

        renderProbe.RecordEvent("webview2.initialization.completed", "success");
        coreWebView.NavigationStarting += OnNavigationStarting;
        coreWebView.NavigationCompleted += OnNavigationCompleted;
        coreWebView.ProcessFailed += OnWebView2ProcessFailed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e) {
        string code = ReferenceEquals(System.Windows.Application.Current?.MainWindow, this)
            ? "application-main-window"
            : "other-window";
        renderProbe.RecordEvent("main-window.source-initialized", code: code);
    }

    private void OnClosed(object? sender, EventArgs e) {
        renderProbe.RecordEvent("main-window.closed", "success");
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) {
        renderProbe.RecordEvent("webview2.navigation.started");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) {
        if (e.IsSuccess) {
            recoveryPolicy.MarkRendered();
        }
        renderProbe.RecordEvent(
            "webview2.navigation.completed",
            e.IsSuccess ? "success" : "failure",
            e.IsSuccess ? null : e.WebErrorStatus.ToString()
        );
    }

    private void OnWebView2ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e) {
        renderProbe.RecordEvent(
            "webview2.process.failed",
            "failure",
            $"{e.ProcessFailedKind}:{e.Reason}"
        );
        recoveryLog.Write("process-failed:" + e.ProcessFailedKind);
        // GPU 程序結束時 WebView2 會自行重啟，只記錄不重建。
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
            or CoreWebView2ProcessFailedKind.BrowserProcessExited
        ) {
            RequestWebViewRecovery("process-failed:" + e.ProcessFailedKind);
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e) {
        string closeState = closeAllowed ? "allowed" : "shutdown-pending";
        if (closeInProgress && !closeAllowed) {
            closeState = "already-in-progress";
        }
        renderProbe.RecordEvent(
            "main-window.closing.entered",
            closeState
        );
        if (closeAllowed) {
            return;
        }

        if (TrayPolicy.ShouldHideOnClose(settings.Settings?.MinimizeToTray == true, exitRequested)) {
            e.Cancel = true;
            try {
                tray.SetVisible(true);
                Hide();
            } catch (Exception) {
                Show();
                MessageBox.Show(this, "無法顯示系統匣圖示，視窗仍保持開啟。", AppIdentity.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        e.Cancel = true;
        renderProbe.RecordEvent("main-window.closing.cancelled", "shutdown-pending");
        if (closeInProgress) {
            return;
        }

        closeInProgress = true;
        using CancellationTokenSource shutdownTimeout = new(TimeSpan.FromSeconds(30));
        await SafeShutdownSequence.RunAsync(
            () => {
                operationCoordinator.StopAccepting();
                renderProbe.RecordEvent("operations.stop-accepting.completed", "success");
                renderProbe.RecordEvent("host.stop.started");
            },
            async () => {
                await host.StopAsync(shutdownTimeout.Token);
                renderProbe.RecordEvent("host.stop.completed", "success");
                renderProbe.RecordEvent("operations.drain.started");
            },
            async () => {
                await operationCoordinator.WaitForActiveOperationsAsync(shutdownTimeout.Token);
                renderProbe.RecordEvent("operations.drain.completed", "success");
            },
            () => {
                closeAllowed = true;
                renderProbe.RecordEvent("main-window.close.retry.queued", "success");
                _ = Dispatcher.BeginInvoke(new Action(RetryClose));
            },
            OnShutdownFailure
        );
    }

    private void OnShutdownFailure(Exception exception) {
        if (exception is OperationCanceledException) {
            renderProbe.RecordEvent(
                "main-window.close.cancelled",
                "failure",
                exception.InnerException?.GetType().FullName ?? "no-inner-exception",
                exception
            );
            closeInProgress = false;
            exitRequested = false;
        }
        SafeShutdownSequence.HandleFailure(exception, Show, () => MessageBox.Show(
            this,
            "背景工作尚未安全完成。資料仍保留，請稍後再試一次關閉。",
            AppIdentity.DisplayName,
            MessageBoxButton.OK,
            MessageBoxImage.Warning
        ), ShowShutdownFailure);
    }

    private void RetryClose() {
        renderProbe.RecordEvent("main-window.close.retry.started");
        try {
            Close();
        } catch (Exception exception) {
            ShowShutdownFailure(exception);
        }
    }

    private void ShowShutdownFailure(Exception exception) {
        renderProbe.RecordEvent(
            "main-window.close.failed",
            "failure",
            exception.InnerException?.GetType().FullName ?? "no-inner-exception",
            exception
        );
        closeInProgress = false;
        exitRequested = false;
        Show();
        MessageBox.Show(
            this,
            "無法安全完成背景工作關閉。資料仍保留，請稍後再試一次。",
            AppIdentity.DisplayName,
            MessageBoxButton.OK,
            MessageBoxImage.Error
        );
    }
}

