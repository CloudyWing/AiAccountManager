using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Runtime;
using CloudyWing.AiAccountManager.Application.Security;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Application.Updates;
using CloudyWing.AiAccountManager.Application.Notifications;
using CloudyWing.AiAccountManager.Windows.Claude;
using CloudyWing.AiAccountManager.Windows.Codex;
using CloudyWing.AiAccountManager.Windows.Runtime;
using CloudyWing.AiAccountManager.Windows.Security;
using CloudyWing.AiAccountManager.Windows.Storage;
using CloudyWing.AiAccountManager.Windows.Updates;
using CloudyWing.AiAccountManager.Windows.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace CloudyWing.AiAccountManager.Windows.DependencyInjection;

/// <summary>
/// 提供 Windows 介面卡的服務註冊方法。
/// </summary>
public static class WindowsServiceCollectionExtensions {
    /// <summary>
    /// 註冊 Windows 儲存、執行環境及外部服務介面卡。
    /// </summary>
    /// <param name="services">要加入服務的集合。</param>
    /// <param name="dataRootOverride">自訂資料目錄。</param>
    /// <returns>傳入的服務集合。</returns>
    public static IServiceCollection AddWindowsAdapters(this IServiceCollection services, string? dataRootOverride) {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new CurrentUserDataDirectory(dataRootOverride));
        services.AddSingleton<AtomicJsonFileStore>();
        services.AddSingleton<INotificationDedupStore, JsonNotificationDedupStore>();
        services.AddSingleton<ISettingsRepository, JsonSettingsRepository>();
        services.AddSingleton<SettingsService.IStartupRegistration, WindowsStartupRegistration>();
        services.AddSingleton<IAccountCatalogRepository, JsonAccountCatalogRepository>();
        services.AddSingleton<IQuotaSnapshotRepository, JsonQuotaSnapshotRepository>();
        services.AddSingleton<IClaudeAccountRepository, WindowsClaudeAccountRepository>();
        services.AddSingleton<IClaudeQuotaRepository, JsonClaudeQuotaRepository>();
        services.AddSingleton<IClaudeDesktopInstallationLocator, WindowsClaudeDesktopInstallationLocator>();
        services.AddSingleton<IClaudeDesktopProfileStore, WindowsClaudeDesktopProfileStore>();
        services.AddSingleton<IClaudeDesktopProcessController, WindowsClaudeDesktopProcessController>();
        services.AddSingleton<IClaudeOAuthClient, WindowsClaudeOAuthClient>();
        services.AddSingleton<IClaudeSessionHistoryStore, WindowsClaudeSessionHistoryStore>();
        services.AddSingleton<ICodexProfileStorage, WindowsCodexProfileStorage>();
        services.AddSingleton(_ => new CodexAppServerBinaryVerifier());
        services.AddSingleton<ICodexAppServerSessionFactory, CodexAppServerSessionFactory>();
        services.AddSingleton<ICodexResetNewsClient, WindowsCodexResetNewsClient>();
        services.AddSingleton<ICodexResetCreditAttemptRepository, JsonCodexResetCreditAttemptRepository>();
        services.AddSingleton<ICodexResetCreditDelay, CodexResetCreditDelay>();
        services.AddSingleton<ICodexLocalAuthIdentityReader, WindowsCodexLocalAuthIdentityReader>();
        services.AddSingleton<ICodexLocalAuthFileChangeNotifier, WindowsCodexLocalAuthFileChangeNotifier>();
        services.AddSingleton<ICodexDesktopProcessStatusSource, WindowsCodexDesktopProcessStatusSource>();
        services.AddSingleton<ICodexDesktopAuthStore, WindowsCodexDesktopAuthStore>();
        services.AddSingleton<ICodexDesktopSnapshotRepository, WindowsCodexDesktopSnapshotRepository>();
        services.AddSingleton<ICodexDesktopProcessController, WindowsCodexDesktopProcessController>();
        services.AddSingleton<IUpdateAdapter, VelopackUpdateAdapter>();
        services.AddSingleton<IExternalBrowserLauncher, WindowsExternalBrowserLauncher>();
        services.AddSingleton<IIdentityFingerprinter>(provider => new WindowsIdentityFingerprinter(
            provider.GetRequiredService<CurrentUserDataDirectory>(),
            provider.GetRequiredService<DpapiSecretProtector>()
        ));
        services.AddSingleton<IClock, WindowsSystemClock>();
        services.AddSingleton<DpapiSecretProtector>();
        return services;
    }
}
