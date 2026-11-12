using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Application.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CloudyWing.AiAccountManager.Desktop;

public partial class App : System.Windows.Application {
    private readonly IHost? host;
    private readonly string? webViewDataDirectory;
    private readonly bool startMinimized;
    private readonly ActivationRelay? activationRelay;
    private ITrayService? tray;
    private SettingsService? settings;
    private INotificationSink? notifications;
    private TrayLifecycleCoordinator? trayLifecycle;

    private App() {
        InitializeComponent();
    }

    public App(IHost host, string webViewDataDirectory, bool startMinimized = false, ActivationRelay? activationRelay = null) {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentException.ThrowIfNullOrWhiteSpace(webViewDataDirectory);
        this.webViewDataDirectory = Path.GetFullPath(webViewDataDirectory);
        this.startMinimized = startMinimized;
        this.activationRelay = activationRelay;
        InitializeComponent();
    }

    protected override async void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        StartupRenderProbe? renderProbe = host?.Services.GetService<StartupRenderProbe>();
        renderProbe?.RecordEvent("application.startup.entered");
        if (renderProbe?.IsEnabled == true) {
            RegisterUnhandledExceptionDiagnostics(renderProbe);
            Dispatcher.ShutdownStarted += (_, _) => renderProbe.RecordEvent("dispatcher.shutdown.started");
            Dispatcher.ShutdownFinished += (_, _) => renderProbe.RecordEvent("dispatcher.shutdown.finished");
        }
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            renderProbe?.RecordEvent("webview2.runtime.available", "success");
        } catch (WebView2RuntimeNotFoundException exception) {
            renderProbe?.RecordEvent("webview2.runtime.missing", "failure", exception: exception);
            ShowWebView2RuntimeFailure();
            Shutdown(-1);
            return;
        }
        try {
            IHost applicationHost = host ?? throw new InvalidOperationException("桌面應用程式尚未建立 Host。");
            using CancellationTokenSource startupTimeout = new(TimeSpan.FromSeconds(30));
            renderProbe?.RecordEvent("host.start.started");
            await applicationHost.StartAsync(startupTimeout.Token);
            renderProbe?.RecordEvent("host.start.completed", "success");
            SettingsService settingsService = applicationHost.Services.GetRequiredService<SettingsService>();
            renderProbe?.RecordEvent("settings.initialize.started");
            await settingsService.InitializeAsync(startupTimeout.Token);
            renderProbe?.RecordEvent("settings.initialize.completed", "success");

            ShutdownMode = ShutdownMode.OnMainWindowClose;
            string profileDirectory = webViewDataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiAccountManager", "data", "webview2");
            renderProbe?.RecordEvent("main-window.create.started");
            MainWindow mainWindow = new(applicationHost, applicationHost.Services, profileDirectory);
            MainWindow = mainWindow;
            settings = settingsService;
            tray = applicationHost.Services.GetRequiredService<ITrayService>();
            trayLifecycle = new(tray, () => OnTrayOpen(this, EventArgs.Empty), () => OnTrayExit(this, EventArgs.Empty));
            notifications = applicationHost.Services.GetRequiredService<INotificationSink>();
            notifications.Activated += OnNotificationActivated;
            settings.StateChanged += OnSettingsChanged;
            trayLifecycle.Start(startMinimized, mainWindow.Show);
            activationRelay?.Attach(OnActivationRequested);
            try {
                UpdateTrayVisibility();
                applicationHost.Services.GetRequiredService<QuotaNotificationCoordinator>().RequestEvaluation();
            } catch (Exception) {
                mainWindow.Show();
                MessageBox.Show(mainWindow, "無法顯示系統匣圖示，視窗仍保持開啟。", AppIdentity.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            renderProbe?.RecordEvent("main-window.show.completed", "success");
        } catch (OperationCanceledException exception) {
            renderProbe?.RecordEvent("application.startup.cancelled", "failure", exception: exception);
            ShowStartupFailure();
            Shutdown(-1);
        } catch (Exception exception) {
            renderProbe?.RecordEvent("application.startup.failed", "failure", exception: exception);
            ShowStartupFailure();
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e) {
        activationRelay?.Detach();
        StartupRenderProbe? renderProbe = host?.Services.GetService<StartupRenderProbe>();
        renderProbe?.RecordEvent("application.exit.entered");
        if (settings is not null) {
            settings.StateChanged -= OnSettingsChanged;
        }
        trayLifecycle?.Dispose();
        if (notifications is not null) {
            notifications.Activated -= OnNotificationActivated;
        }
        base.OnExit(e);
        renderProbe?.RecordEvent("application.exit.completed");
    }

    private void OnActivationRequested() {
        _ = Dispatcher.BeginInvoke(new Action(() => {
            OnTrayOpen(this, EventArgs.Empty);
            if (MainWindow is not null) {
                WindowForeground.Bring(MainWindow);
            }
        }));
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) {
        // 只吞下 WebView2 繪圖裝置遺失後的已知例外並改為重建畫面，其餘例外維持既有處理。
        if (!e.Handled && MainWindow is MainWindow window && window.TrySwallowRenderException(e.Exception)) {
            e.Handled = true;
        }
    }

    private void OnTrayOpen(object? sender, EventArgs e) {
        Dispatcher.Invoke(() => {
            if (MainWindow is MainWindow hiddenWindow && !hiddenWindow.IsVisible) {
                hiddenWindow.RequestWebViewRecovery("tray-show");
            }
            MainWindow?.Show();
            if (MainWindow is not null) {
                MainWindow.WindowState = WindowState.Normal;
                MainWindow.Activate();
            }
            UpdateTrayVisibility();
        });
    }

    private void OnTrayExit(object? sender, EventArgs e) {
        Dispatcher.Invoke(() => (MainWindow as MainWindow)?.RequestExit());
    }

    private void OnNotificationActivated(string provider) {
        _ = Dispatcher.BeginInvoke(new Action(async () => {
            try {
                OnTrayOpen(this, EventArgs.Empty);
                if (MainWindow is MainWindow window) {
                    await window.NavigateToProviderAsync(provider);
                }
            } catch (Exception) {
                MessageBox.Show("無法開啟通知對應的帳號管理頁。", AppIdentity.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }));
    }

    private void OnSettingsChanged(object? sender, EventArgs e) {
        _ = Dispatcher.BeginInvoke(new Action(() => {
            try {
                UpdateTrayVisibility();
            } catch (Exception) {
                MainWindow?.Show();
                MessageBox.Show(MainWindow, "無法顯示系統匣圖示，視窗仍保持開啟。", AppIdentity.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }));
    }

    private void UpdateTrayVisibility() {
        if (tray is not null) {
            TrayPolicy.UpdateVisibility(tray, settings?.Settings?.MinimizeToTray == true,
                settings?.Settings?.LowQuotaNotificationsEnabled == true, MainWindow?.IsVisible == true);
        }
    }

    private void RegisterUnhandledExceptionDiagnostics(StartupRenderProbe renderProbe) {
        DispatcherUnhandledException += (_, eventArgs) => {
            renderProbe.RecordEvent("runtime.dispatcher.unhandled", "failure", exception: eventArgs.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => {
            Exception? exception = eventArgs.ExceptionObject as Exception;
            renderProbe.RecordEvent(
                "runtime.domain.unhandled",
                eventArgs.IsTerminating ? "terminating" : "continuing",
                exception: exception
            );
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) => {
            renderProbe.RecordEvent("runtime.task.unobserved", "failure", exception: eventArgs.Exception);
        };
    }

    private static void ShowWebView2RuntimeFailure() {
        MessageBox.Show(
            "找不到 Microsoft Edge WebView2 Evergreen Runtime。請先安裝官方 Runtime，再重新啟動應用程式。\n\n官方下載頁：https://developer.microsoft.com/microsoft-edge/webview2/",
            "缺少 WebView2 Runtime",
            MessageBoxButton.OK,
            MessageBoxImage.Error
        );
    }
    private static void ShowStartupFailure() {
        MessageBox.Show(
            "無法啟動桌面畫面。若此電腦尚未安裝 Microsoft Edge WebView2 Evergreen Runtime，請依官方安裝指引完成安裝後重試。",
            AppIdentity.DisplayName,
            MessageBoxButton.OK,
            MessageBoxImage.Error
        );
    }
}
