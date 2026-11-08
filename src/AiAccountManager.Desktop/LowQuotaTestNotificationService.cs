using CloudyWing.AiAccountManager.Application.Notifications;
using CloudyWing.AiAccountManager.Application.Settings;
using Microsoft.Extensions.Logging;

namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>以範例值傳送與正式額度偏低通知同格式的測試氣泡，不讀寫去重紀錄，點擊氣泡也不導覽。</summary>
public sealed class LowQuotaTestNotificationService : ILowQuotaTestNotificationService {
    private const string SampleProvider = "codex";
    private const string SampleWindowLabel = "5 小時";
    private const decimal SampleRemainingPercent = 12m;
    private const string SampleRecommendation = "範例帳號（測試通知）";
    private static readonly TimeSpan DefaultVisibilityRestoreDelay = TimeSpan.FromSeconds(6);
    private static readonly Action<ILogger, Exception?> LogShowIconFailure = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, nameof(LogShowIconFailure)), "無法顯示系統匣圖示，測試通知未送出。"
    );
    private static readonly Action<ILogger, Exception?> LogRestoreFailure = LoggerMessage.Define(
        LogLevel.Warning, new EventId(2, nameof(LogRestoreFailure)), "無法還原系統匣圖示的顯示狀態。"
    );
    private readonly INotificationSink sink;
    private readonly ITrayService tray;
    private readonly SettingsService settings;
    private readonly ILogger<LowQuotaTestNotificationService>? logger;
    private readonly TimeSpan visibilityRestoreDelay;
    private readonly SemaphoreSlim visibilityGate = new(1, 1);
    private int generation;

    /// <summary>初始化測試通知服務。</summary>
    /// <param name="sink">送出氣泡的通知介面。</param>
    /// <param name="tray">共用的系統匣服務。</param>
    /// <param name="settings">提供目前設定的服務，用於還原圖示可見性。</param>
    /// <param name="logger">失敗時的日誌。</param>
    /// <param name="visibilityRestoreDelay">送出後還原圖示可見性前的等待時間；預設 6 秒。</param>
    public LowQuotaTestNotificationService(
        INotificationSink sink,
        ITrayService tray,
        SettingsService settings,
        ILogger<LowQuotaTestNotificationService>? logger = null,
        TimeSpan? visibilityRestoreDelay = null
    ) {
        this.sink = sink;
        this.tray = tray;
        this.settings = settings;
        this.logger = logger;
        this.visibilityRestoreDelay = visibilityRestoreDelay ?? DefaultVisibilityRestoreDelay;
    }

    /// <inheritdoc />
    public async Task<bool> SendAsync(CancellationToken cancellationToken) {
        // 與還原工作互斥，避免舊的還原在新一次送出顯示圖示後、氣泡顯示前把圖示隱藏
        await visibilityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            int token = Interlocked.Increment(ref generation);
            try {
                tray.SetVisible(true);
            } catch (Exception exception) {
                if (logger is not null) {
                    LogShowIconFailure(logger, exception);
                }
                return false;
            }
            string message = LowQuotaNotificationMessage.Compose(
                SampleProvider,
                SampleWindowLabel,
                SampleRemainingPercent,
                SampleRecommendation
            );
            NotificationRequest request = new(
                LowQuotaNotificationMessage.Title,
                message,
                LowQuotaNotificationMessage.Type,
                SampleProvider,
                Activatable: false
            );
            try {
                return await sink.SendAsync(request, cancellationToken).ConfigureAwait(false);
            } finally {
                _ = RestoreVisibilityAsync(token);
            }
        } finally {
            visibilityGate.Release();
        }
    }

    private async Task RestoreVisibilityAsync(int token) {
        try {
            await Task.Delay(visibilityRestoreDelay).ConfigureAwait(false);
            await visibilityGate.WaitAsync().ConfigureAwait(false);
            try {
                if (Volatile.Read(ref generation) != token) {
                    return;
                }
                AppSettings? current = settings.Settings;
                TrayPolicy.UpdateVisibility(
                    tray,
                    current?.MinimizeToTray == true,
                    current?.LowQuotaNotificationsEnabled == true,
                    windowVisible: true
                );
            } finally {
                visibilityGate.Release();
            }
        } catch (Exception exception) {
            if (logger is not null) {
                LogRestoreFailure(logger, exception);
            }
        }
    }
}
