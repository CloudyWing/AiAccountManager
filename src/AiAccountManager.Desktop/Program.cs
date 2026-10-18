using System.IO;
using System.Windows;
using CloudyWing.AiAccountManager.Application.Operations;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Application.Updates;
using CloudyWing.AiAccountManager.Application.Notifications;
using CloudyWing.AiAccountManager.Desktop.Components;
using CloudyWing.AiAccountManager.Desktop.Components.Dashboard;
using CloudyWing.AiAccountManager.Windows.DependencyInjection;
using CloudyWing.AiAccountManager.Windows.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using Velopack;

namespace CloudyWing.AiAccountManager.Desktop;

public static class Program {
    [STAThread]
    public static void Main(string[] args) {
        VelopackApp.Build().Run();

        if (!WindowsSingleInstance.TryCreate(out WindowsSingleInstance? singleInstance)) {
            return;
        }

        using (singleInstance) {
            StartupArguments startupArguments;
            try {
                startupArguments = StartupArguments.Parse(args);
            } catch (ArgumentException) {
                MessageBox.Show(
                    "診斷啟動參數必須同時提供 --data-root 與 --startup-render-probe。",
                    "AI 帳號管理工具",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                return;
            }

            startupArguments.RenderProbe.RecordEvent("program.arguments.parsed", "success");

            IHost host;
            try {
                startupArguments.RenderProbe.RecordEvent("host.build.started");
                host = Host.CreateDefaultBuilder()
                    .UseContentRoot(AppContext.BaseDirectory)
                    .ConfigureLogging(logging => {
                        if (startupArguments.RenderProbe.IsEnabled) {
                            logging.ClearProviders();
                            logging.AddProvider(startupArguments.RenderProbe.CreateLoggerProvider());
                        }
                    })
                    .ConfigureServices((_, services) => {
                        services.AddSingleton<ExclusiveOperationCoordinator>();
                        services.AddSingleton<ClaudeOperationGate>();
                        services.AddSingleton<UpdateFeedApplyCoordinator>();
                        services.AddSingleton<SettingsService>();
                        services.AddSingleton<CodexProfileService>();
                        services.AddSingleton<CodexLocalIdentityService>();
                        services.AddSingleton<QuotaResponseParser>();
                        services.AddSingleton<CodexQuotaService>();
                        services.AddSingleton<CodexResetNewsService>();
                        services.AddSingleton<CodexResetCreditAutoUseService>();
                        services.AddSingleton<CodexQuotaScheduler>();
                        services.AddSingleton<CodexDesktopSwitchService>();
                        services.AddSingleton<ClaudeAccountService>();
                        services.AddSingleton<ClaudeQuotaService>();
                        services.AddSingleton<ClaudeQuotaScheduler>();
                        services.AddSingleton<DashboardOverviewReader>();
                        services.AddSingleton<IDashboardOverviewReader>(provider => provider.GetRequiredService<DashboardOverviewReader>());
                        services.AddSingleton<ClaudeSessionHistorySyncService>();
                        services.AddSingleton<ClaudeDesktopSwitchService>();
                        services.AddSingleton<UpdateService>();
                        services.AddSingleton<ITrayService, WindowsTrayService>();
                        services.AddSingleton<INotificationSink, TrayNotificationSink>();
                        services.AddSingleton<LowQuotaNotificationEvaluator>();
                        services.AddSingleton<QuotaNotificationCoordinator>();
                        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<QuotaNotificationCoordinator>());
                        services.AddSingleton<IHostedService, QuotaSchedulerHostedService>();
                        services.AddSingleton<IHostedService, ResetCreditAutoUseHostedService>();
                        services.AddSingleton<IHostedService, ClaudeQuotaSchedulerHostedService>();
                        services.AddWindowsAdapters(startupArguments.DataRoot);
                        services.AddSingleton(startupArguments.RenderProbe);
                        services.AddWpfBlazorWebView();
                        services.AddMudServices();
                    })
                    .Build();
                startupArguments.RenderProbe.RecordEvent("host.build.completed", "success");
            } catch (Exception exception) {
                startupArguments.RenderProbe.RecordEvent("host.build.failed", "failure", exception: exception);
                throw;
            }

            using (host) {
                App application = new(host, startupArguments.WebViewDataDirectory, args.Contains("--start-minimized", StringComparer.Ordinal));
                startupArguments.RenderProbe.RecordEvent("application.run.started");
                try {
                    application.Run();
                    startupArguments.RenderProbe.RecordEvent("application.run.completed", "success");
                } catch (Exception exception) {
                    startupArguments.RenderProbe.RecordEvent("application.run.failed", "failure", exception: exception);
                    throw;
                }
            }
        }
    }

    private sealed class QuotaSchedulerHostedService(CodexQuotaScheduler scheduler) : IHostedService {
        public Task StartAsync(CancellationToken cancellationToken) => scheduler.StartAsync();

        public Task StopAsync(CancellationToken cancellationToken) => scheduler.StopAsync(cancellationToken);
    }

    private sealed class ResetCreditAutoUseHostedService(CodexResetCreditAutoUseService service) : IHostedService {
        public Task StartAsync(CancellationToken cancellationToken) => service.StartAsync();

        public Task StopAsync(CancellationToken cancellationToken) => service.StopAsync(cancellationToken);
    }

    private sealed class ClaudeQuotaSchedulerHostedService(ClaudeQuotaScheduler scheduler) : IHostedService {
        public Task StartAsync(CancellationToken cancellationToken) => scheduler.StartAsync();

        public Task StopAsync(CancellationToken cancellationToken) => scheduler.StopAsync(cancellationToken);
    }

    private sealed record StartupArguments(string? DataRoot, StartupRenderProbe RenderProbe) {
        public string WebViewDataDirectory {
            get {
                string dataRoot = DataRoot
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AiAccountManager",
                        "data"
                    );
                return Path.Combine(dataRoot, "webview2");
            }
        }
        public static StartupArguments Parse(string[] args) {
            ArgumentNullException.ThrowIfNull(args);
            string? dataRoot = ReadOption(args, "--data-root");
            string? markerPath = ReadOption(args, "--startup-render-probe");
            if ((dataRoot is null) != (markerPath is null)) {
                throw new ArgumentException("診斷參數必須成對提供。", nameof(args));
            }

            if (dataRoot is null) {
                return new StartupArguments(null, new StartupRenderProbe(null, null));
            }

            string normalizedDataRoot = Path.GetFullPath(dataRoot);
            string normalizedMarkerPath = Path.GetFullPath(markerPath ?? throw new ArgumentException("啟動探針路徑缺少值。", nameof(args)));
            if (File.Exists(normalizedMarkerPath)) {
                throw new ArgumentException("啟動探針 marker 必須尚未存在。", nameof(args));
            }

            return new StartupArguments(
                normalizedDataRoot,
                new StartupRenderProbe(normalizedMarkerPath, normalizedDataRoot)
            );
        }

        private static string? ReadOption(string[] args, string optionName) {
            int index = Array.FindIndex(args, value => string.Equals(value, optionName, StringComparison.Ordinal));
            if (index < 0) {
                return null;
            }

            if (Array.FindLastIndex(args, value => string.Equals(value, optionName, StringComparison.Ordinal)) != index
                || index + 1 >= args.Length
                || string.IsNullOrWhiteSpace(args[index + 1])
                || args[index + 1].StartsWith("--", StringComparison.Ordinal)) {
                throw new ArgumentException("啟動診斷參數缺少唯一的值。", nameof(args));
            }

            return args[index + 1];
        }
    }
}


