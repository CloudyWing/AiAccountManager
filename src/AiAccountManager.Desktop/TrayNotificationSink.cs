using CloudyWing.AiAccountManager.Application.Notifications;
using Microsoft.Extensions.Logging;

namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>透過共用系統匣顯示氣泡通知。</summary>
public sealed class TrayNotificationSink : INotificationSink, IDisposable {
    private static readonly Action<ILogger, Exception?> LogSendFailure = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, nameof(LogSendFailure)), "無法顯示通知氣泡，將在下次刷新時重試。"
    );
    private readonly ITrayService tray;
    private readonly ILogger<TrayNotificationSink>? logger;
    private readonly object sync = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private string? currentProvider;

    /// <summary>初始化氣泡通知介面。</summary>
    /// <param name="tray">共用的系統匣服務。</param>
    /// <param name="logger">通知失敗的日誌。</param>
    public TrayNotificationSink(ITrayService tray, ILogger<TrayNotificationSink>? logger = null) {
        this.tray = tray;
        this.logger = logger;
        tray.BalloonClicked += OnBalloonClicked;
    }

    /// <inheritdoc />
    public event Action<string>? Activated;

    /// <inheritdoc />
    public async Task<bool> SendAsync(NotificationRequest request, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Provider is not ("claude" or "codex") || string.IsNullOrWhiteSpace(request.Type)
            || string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Message)) {
            return false;
        }
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            lock (sync) {
                currentProvider = request.Provider;
            }
            tray.ShowBalloon(request.Title, request.Message);
            return true;
        } catch (Exception exception) {
            if (logger is not null) {
                LogSendFailure(logger, exception);
            }
            lock (sync) {
                currentProvider = null;
            }
            return false;
        } finally {
            sendGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => tray.BalloonClicked -= OnBalloonClicked;

    private void OnBalloonClicked(object? sender, EventArgs e) {
        string? provider;
        lock (sync) {
            provider = currentProvider;
            currentProvider = null;
        }
        if (provider is "claude" or "codex") {
            Activated?.Invoke(provider);
        }
    }
}
