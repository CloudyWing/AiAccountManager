using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Runtime;
using CloudyWing.AiAccountManager.Application.Settings;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>管理 Codex 重置消息、節流與設定變更。</summary>
public sealed class CodexResetNewsService : IDisposable {
    private readonly SettingsService settings;
    private readonly ICodexResetNewsClient client;
    private readonly IClock clock;
    private readonly Lock sync = new();
    private CancellationTokenSource? active;
    private DateTimeOffset nextAllowedUtc;
    private long generation;
    private bool enabled;
    private State current = new(false, false, false, null, null, null, null, null);

    /// <summary>建立重置消息服務。</summary>
    /// <param name="settings">設定服務。</param>
    /// <param name="client">外部查詢介面。</param>
    /// <param name="clock">時鐘。</param>
    public CodexResetNewsService(SettingsService settings, ICodexResetNewsClient client, IClock clock) {
        this.settings = settings;
        this.client = client;
        this.clock = clock;
        enabled = settings.Settings?.IsCodexResetNewsEnabled ?? true;
        settings.StateChanged += OnSettingsChanged;
    }

    /// <summary>當消息狀態變更時發生。</summary>
    public event EventHandler? StateChanged;

    /// <summary>取得目前的消息狀態。</summary>
    public State Current { get { lock (sync) { return current; } } }

    /// <summary>依設定與頻率限制嘗試更新消息。</summary>
    /// <param name="cancellationToken">取消查詢的權杖。</param>
    /// <returns>表示查詢完成的工作。</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken) {
        CancellationTokenSource request;
        long requestGeneration;
        lock (sync) {
            if (!enabled || active is not null || clock.UtcNow < nextAllowedUtc) {
                return;
            }
            nextAllowedUtc = clock.UtcNow + TimeSpan.FromMinutes(15);
            request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            active = request;
            requestGeneration = generation;
            current = current with { IsLoading = true };
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        try {
            ICodexResetNewsClient.Result result = await client.GetAsync(request.Token).ConfigureAwait(false);
            lock (sync) {
                if (requestGeneration != generation || !enabled) {
                    return;
                }
                if (result.IsRateLimited && result.RetryAfterUtc is DateTimeOffset retry && retry > nextAllowedUtc) {
                    nextAllowedUtc = retry;
                }
                current = result.Payload is JsonElement payload && TryParse(payload, out State? parsed)
                    ? parsed : new State(false, true, false, null, null, null, null, null);
            }
        } catch (OperationCanceledException) when (request.IsCancellationRequested) {
            lock (sync) {
                if (requestGeneration == generation && enabled) {
                    current = new State(false, true, false, null, null, null, null, null);
                }
            }
        } catch (Exception) {
            lock (sync) {
                if (requestGeneration == generation && enabled) {
                    current = new State(false, true, false, null, null, null, null, null);
                }
            }
        } finally {
            lock (sync) {
                if (ReferenceEquals(active, request)) {
                    active = null;
                }
            }
            request.Dispose();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) {
        CancellationTokenSource? cancel = null;
        lock (sync) {
            bool newEnabled = settings.Settings?.IsCodexResetNewsEnabled ?? true;
            if (enabled == newEnabled) {
                return;
            }
            enabled = newEnabled;
            generation++;
            if (!enabled) {
                cancel = active;
                current = new State(false, false, false, null, null, null, null, null);
            }
        }
        try { cancel?.Cancel(); } catch (ObjectDisposedException) { }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool TryParse(JsonElement root, [NotNullWhen(true)] out State? state) {
        state = null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("scheduled_reset", out JsonElement scheduled)
            || scheduled.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object)) {
            return false;
        }
        DateTimeOffset? scheduledAt = null;
        Uri? scheduledUrl = null;
        if (scheduled.ValueKind == JsonValueKind.Object) {
            if (!TryTime(scheduled, "scheduled_for", out scheduledAt)) { return false; }
            scheduledUrl = ReadUrl(scheduled);
        }
        DateTimeOffset? latestAt = null;
        Uri? latestUrl = null;
        if (data.TryGetProperty("latest_reset", out JsonElement latest) && latest.ValueKind != JsonValueKind.Null) {
            if (latest.ValueKind != JsonValueKind.Object || !TryTime(latest, "announced_at", out latestAt) || !latestAt.HasValue) {
                return false;
            }
            latestUrl = ReadUrl(latest);
        }
        double? average = null;
        if (data.TryGetProperty("stats", out JsonElement stats) && stats.ValueKind == JsonValueKind.Object
            && stats.TryGetProperty("avg_interval_days", out JsonElement avg) && avg.ValueKind == JsonValueKind.Number
            && avg.TryGetDouble(out double number) && double.IsFinite(number) && number >= 0) {
            average = number;
        }
        state = new State(false, false, scheduled.ValueKind == JsonValueKind.Object, scheduledAt, scheduledUrl, latestAt, latestUrl, average);
        return true;
    }

    private static bool TryTime(JsonElement obj, string name, out DateTimeOffset? value) {
        value = null;
        if (!obj.TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null) { return true; }
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not string text
            || !text.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed)) { return false; }
        value = parsed;
        return true;
    }

    private static Uri? ReadUrl(JsonElement obj) {
        if (!obj.TryGetProperty("source", out JsonElement source) || source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty("url", out JsonElement url) || url.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? parsed)
            || !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) { return null; }
        return parsed;
    }

    /// <summary>停止接收設定事件並取消進行中的查詢。</summary>
    public void Dispose() {
        settings.StateChanged -= OnSettingsChanged;
        CancellationTokenSource? cancel;
        lock (sync) { enabled = false; generation++; cancel = active; }
        try { cancel?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>表示消息畫面狀態。</summary>
    /// <param name="IsLoading">是否載入中。</param>
    /// <param name="IsUnavailable">是否無法取得。</param>
    /// <param name="HasScheduled">是否已宣布預定重置。</param>
    /// <param name="ScheduledAtUtc">預定時間。</param>
    /// <param name="ScheduledUrl">預定消息來源。</param>
    /// <param name="LatestAtUtc">最近重置時間。</param>
    /// <param name="LatestUrl">最近消息來源。</param>
    /// <param name="AverageDays">平均間隔天數。</param>
    public sealed record State(bool IsLoading, bool IsUnavailable, bool HasScheduled, DateTimeOffset? ScheduledAtUtc,
        Uri? ScheduledUrl, DateTimeOffset? LatestAtUtc, Uri? LatestUrl, double? AverageDays);
}
