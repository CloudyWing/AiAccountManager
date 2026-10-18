using System.ComponentModel;
using System.IO;
using System.Windows;
using CloudyWing.AiAccountManager.Application.Operations;
using CloudyWing.AiAccountManager.Application.Settings;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Web.WebView2.Core;

namespace CloudyWing.AiAccountManager.Desktop;

public partial class MainWindow : Window {
    private readonly IHost host;
    private readonly ExclusiveOperationCoordinator operationCoordinator;
    private readonly StartupRenderProbe renderProbe;
    private readonly string webViewDataDirectory;
    private readonly SettingsService settings;
    private readonly ITrayService tray;
    private bool closeAllowed;
    private bool closeInProgress;
    private bool exitRequested;

    public MainWindow(IHost host, IServiceProvider services, string webViewDataDirectory) {
        this.host = host;
        ArgumentException.ThrowIfNullOrWhiteSpace(webViewDataDirectory);
        this.webViewDataDirectory = Path.GetFullPath(webViewDataDirectory);
        operationCoordinator = services.GetRequiredService<ExclusiveOperationCoordinator>();
        renderProbe = services.GetRequiredService<StartupRenderProbe>();
        settings = services.GetRequiredService<SettingsService>();
        tray = services.GetRequiredService<ITrayService>();
        InitializeComponent();
        if (renderProbe.IsEnabled) {
            SourceInitialized += OnSourceInitialized;
            Closed += OnClosed;
        }
        blazorWebView.BlazorWebViewInitializing += OnBlazorWebViewInitializing;
        blazorWebView.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
        blazorWebView.Services = services;
    }

    /// <summary>
    /// 透過正常關閉流程結束應用程式。
    /// </summary>
    public void RequestExit() {
        exitRequested = true;
        Close();
    }

    /// <summary>開啟指定 provider 的管理頁。</summary>
    /// <param name="provider">已驗證的 provider。</param>
    /// <returns>導覽工作。</returns>
    public async Task NavigateToProviderAsync(string provider) {
        if (provider is not ("claude" or "codex")) {
            return;
        }
        for (int attempt = 0; attempt < 50 && !closeAllowed; attempt++) {
            bool dispatched = await blazorWebView.TryDispatchAsync(services => {
                services.GetRequiredService<NavigationManager>().NavigateTo($"/{provider}");
            });
            if (dispatched) {
                return;
            }
            await Task.Delay(100);
        }
    }

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
                MessageBox.Show(this, "無法顯示系統匣圖示，視窗仍保持開啟。", "AI 帳號管理工具", MessageBoxButton.OK, MessageBoxImage.Error);
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
            "AI 帳號管理工具",
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
            "AI 帳號管理工具",
            MessageBoxButton.OK,
            MessageBoxImage.Error
        );
    }
}

