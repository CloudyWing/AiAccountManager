using System.Threading.Channels;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Notifications;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Desktop.Components.Dashboard;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>序列化額度狀態事件並評估目前帳號通知。</summary>
public sealed class QuotaNotificationCoordinator : BackgroundService {
    private static readonly Action<ILogger, Exception?> LogEvaluationFailure = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, nameof(LogEvaluationFailure)), "無法評估額度通知，將在下次狀態更新時重試。"
    );
    private static readonly Action<ILogger, string, string, string, bool, bool, NotificationDecisionOutcome, Exception?> LogDecision =
        LoggerMessage.Define<string, string, string, bool, bool, NotificationDecisionOutcome>(LogLevel.Information,
            new EventId(2, nameof(LogDecision)),
            "額度通知決策：Provider={Provider} Window={Window} Cycle={Cycle} DedupHit={DedupHit} Sent={Sent} Outcome={Outcome}");
    private readonly Channel<bool> queue = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });
    private readonly AsyncLocal<bool> reading = new();
    private readonly CodexQuotaService codex;
    private readonly ClaudeQuotaService claude;
    private readonly SettingsService settings;
    private readonly DashboardOverviewReader reader;
    private readonly LowQuotaNotificationEvaluator evaluator;
    private readonly ILogger<QuotaNotificationCoordinator> logger;

    /// <summary>初始化額度通知生命週期。</summary>
    /// <param name="codex">Codex 額度服務。</param>
    /// <param name="claude">Claude 額度服務。</param>
    /// <param name="settings">設定服務。</param>
    /// <param name="reader">安全快照讀取器。</param>
    /// <param name="evaluator">通知評估器。</param>
    /// <param name="logger">日誌。</param>
    public QuotaNotificationCoordinator(CodexQuotaService codex, ClaudeQuotaService claude, SettingsService settings,
        DashboardOverviewReader reader, LowQuotaNotificationEvaluator evaluator, ILogger<QuotaNotificationCoordinator> logger) {
        this.codex = codex;
        this.claude = claude;
        this.settings = settings;
        this.reader = reader;
        this.evaluator = evaluator;
        this.logger = logger;
    }

    /// <summary>要求重新評估目前額度。</summary>
    public void RequestEvaluation() => queue.Writer.TryWrite(true);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        codex.StateChanged += OnStateChanged;
        claude.StateChanged += OnStateChanged;
        settings.StateChanged += OnStateChanged;
        RequestEvaluation();
        try {
            await foreach (bool _ in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false)) {
                try {
                    AppSettings? current = settings.Settings;
                    if (current?.LowQuotaNotificationsEnabled != true) {
                        continue;
                    }
                    reading.Value = true;
                    NotificationSnapshot? codexSnapshot = await ReadSnapshotAsync(reader.ReadCodexNotificationAsync, stoppingToken).ConfigureAwait(false);
                    NotificationSnapshot? claudeSnapshot = await ReadSnapshotAsync(reader.ReadClaudeNotificationAsync, stoppingToken).ConfigureAwait(false);
                    List<NotificationSnapshot> snapshots = [];
                    if (codexSnapshot is not null) { snapshots.Add(codexSnapshot); }
                    if (claudeSnapshot is not null) { snapshots.Add(claudeSnapshot); }
                    current = settings.Settings;
                    IReadOnlyList<NotificationDecision> decisions = await evaluator.EvaluateAsync(
                        snapshots, current?.LowQuotaNotificationsEnabled == true, current?.LowQuotaThresholdPercent ?? 0, stoppingToken
                    ).ConfigureAwait(false);
                    foreach (NotificationDecision decision in decisions) {
                        LogDecision(logger, decision.Provider, decision.WindowKey, decision.Cycle, decision.DedupHit,
                            decision.Sent, decision.Outcome, null);
                    }
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    break;
                } catch (Exception exception) {
                    LogEvaluationFailure(logger, exception);
                } finally {
                    reading.Value = false;
                }
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
        } finally {
            codex.StateChanged -= OnStateChanged;
            claude.StateChanged -= OnStateChanged;
            settings.StateChanged -= OnStateChanged;
        }
    }

    private async Task<NotificationSnapshot?> ReadSnapshotAsync(Func<CancellationToken, Task<NotificationSnapshot?>> read, CancellationToken token) {
        try {
            return await read(token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            throw;
        } catch (Exception exception) {
            LogEvaluationFailure(logger, exception);
            return null;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e) {
        if (!reading.Value) {
            RequestEvaluation();
        }
    }
}
