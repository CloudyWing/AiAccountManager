using System.Globalization;

namespace CloudyWing.AiAccountManager.Application.Notifications;

/// <summary>序列化評估額度通知並記錄已送出的週期。</summary>
public sealed class LowQuotaNotificationEvaluator {
    private readonly INotificationSink sink;
    private readonly INotificationDedupStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private HashSet<string>? sent;
    private bool dirty;

    /// <summary>初始化額度通知評估器。</summary>
    /// <param name="sink">通知傳送介面。</param>
    /// <param name="store">週期記錄儲存介面。</param>
    public LowQuotaNotificationEvaluator(INotificationSink sink, INotificationDedupStore store) {
        this.sink = sink;
        this.store = store;
    }

    /// <summary>評估目前帳號的有效額度視窗。</summary>
    /// <param name="snapshot">目前帳號與額度。</param>
    /// <param name="enabled">是否啟用通知。</param>
    /// <param name="threshold">低於此百分比時通知。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>評估工作。</returns>
    public async Task EvaluateAsync(NotificationSnapshot snapshot, bool enabled, int threshold, CancellationToken cancellationToken) {
        if (!enabled || threshold is < 5 or > 50 || snapshot.AccountId == Guid.Empty
            || snapshot.Provider is not ("claude" or "codex")) {
            return;
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            sent ??= new HashSet<string>(await store.LoadAsync(cancellationToken).ConfigureAwait(false), StringComparer.Ordinal);
            if (dirty) {
                await store.SaveAsync(sent.ToArray(), cancellationToken).ConfigureAwait(false);
                dirty = false;
            }
            foreach (NotificationWindow window in snapshot.Windows) {
                if (window.Key is not ("five-hour" or "weekly") || window.RemainingPercent is not decimal remaining
                    || remaining is < 0m or > 100m) {
                    continue;
                }
                string prefix = $"{snapshot.Provider}/{snapshot.AccountId:N}/{window.Key}/";
                string key = prefix + (window.ResetsAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "no-reset");
                if (remaining >= threshold) {
                    if (window.ResetsAtUtc is null && sent.Remove(key)) {
                        dirty = true;
                        await store.SaveAsync(sent.ToArray(), cancellationToken).ConfigureAwait(false);
                        dirty = false;
                    }
                    continue;
                }
                if (sent.Contains(key)) {
                    continue;
                }
                string provider = snapshot.Provider == "codex" ? "Codex" : "Claude";
                string message = $"{provider} {window.Label}額度剩 {remaining.ToString("0.##", CultureInfo.InvariantCulture)}%";
                if (!string.IsNullOrWhiteSpace(snapshot.Recommendation)) {
                    message += $"，建議切換至 {snapshot.Recommendation}";
                }
                bool success = await sink.SendAsync(new("額度偏低", message, "low-quota", snapshot.Provider), cancellationToken)
                    .ConfigureAwait(false);
                if (!success) {
                    continue;
                }
                sent.Add(key);
                dirty = true;
                await store.SaveAsync(sent.ToArray(), cancellationToken).ConfigureAwait(false);
                dirty = false;
            }
        } finally {
            gate.Release();
        }
    }
}
