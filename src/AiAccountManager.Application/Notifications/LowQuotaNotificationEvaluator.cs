namespace CloudyWing.AiAccountManager.Application.Notifications;

/// <summary>序列化評估額度通知並記錄已送出的週期。</summary>
public sealed class LowQuotaNotificationEvaluator {
    private readonly INotificationSink sink;
    private readonly INotificationDedupStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private HashSet<string>? sent;
    private bool dirty;

    /// <summary>初始化額度通知評估器。</summary>
    public LowQuotaNotificationEvaluator(INotificationSink sink, INotificationDedupStore store) {
        this.sink = sink;
        this.store = store;
    }

    /// <summary>評估所有快照並最多送出一則通知。</summary>
    public async Task<IReadOnlyList<NotificationDecision>> EvaluateAsync(
        IReadOnlyList<NotificationSnapshot> snapshots, bool enabled, int threshold, CancellationToken cancellationToken
    ) {
        if (!enabled || threshold is < 5 or > 50 || snapshots.Count == 0) {
            return [];
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (sent is null) {
                IReadOnlyCollection<string> keys = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                sent = new HashSet<string>(StringComparer.Ordinal);
                foreach (string key in keys) {
                    if (NotificationCycleKey.TryMigrateLegacy(key, out string migrated)) {
                        dirty = true;
                    }
                    sent.Add(migrated);
                }
            }
            if (dirty) {
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            List<Candidate> candidates = [];
            foreach (NotificationSnapshot snapshot in snapshots) {
                if (snapshot.AccountId == Guid.Empty || snapshot.Provider is not ("claude" or "codex")) {
                    continue;
                }
                foreach (NotificationWindow window in snapshot.Windows) {
                    if (window.Key is not ("five-hour" or "weekly") || window.RemainingPercent is not decimal remaining
                        || remaining is < 0m or > 100m) {
                        continue;
                    }
                    string key = NotificationCycleKey.Create(snapshot.Provider, snapshot.AccountId, window.Key, window.ResetsAtUtc);
                    if (remaining >= threshold) {
                        if (window.ResetsAtUtc is null && sent.Remove(key)) {
                            dirty = true;
                            await SaveAsync(cancellationToken).ConfigureAwait(false);
                        }
                        continue;
                    }
                    candidates.Add(new(snapshot, window, key, remaining, sent.Contains(key)));
                }
            }
            Candidate? selected = candidates.Where(candidate => !candidate.DedupHit)
                .OrderBy(candidate => candidate.Remaining)
                .ThenBy(candidate => candidate.Snapshot.Provider == "codex" ? 0 : 1)
                .ThenBy(candidate => candidate.Window.Key == "five-hour" ? 0 : 1)
                .FirstOrDefault();
            bool success = false;
            if (selected is not null) {
                string message = LowQuotaNotificationMessage.Compose(
                    selected.Snapshot.Provider,
                    selected.Window.Label,
                    selected.Remaining,
                    selected.Snapshot.Recommendation
                );
                success = await sink.SendAsync(
                    new(LowQuotaNotificationMessage.Title, message, LowQuotaNotificationMessage.Type, selected.Snapshot.Provider),
                    cancellationToken
                ).ConfigureAwait(false);
                if (success) {
                    sent.Add(selected.Key);
                    dirty = true;
                    await SaveAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            return candidates.Select(candidate => {
                bool isSelected = ReferenceEquals(candidate, selected);
                NotificationDecisionOutcome outcome = candidate.DedupHit ? NotificationDecisionOutcome.Deduplicated
                    : isSelected ? (success ? NotificationDecisionOutcome.Sent : NotificationDecisionOutcome.SendFailed)
                    : NotificationDecisionOutcome.Superseded;
                return new NotificationDecision(candidate.Snapshot.Provider, candidate.Window.Key,
                    NotificationCycleKey.FormatCycle(candidate.Window.ResetsAtUtc), candidate.DedupHit, isSelected && success, outcome);
            }).ToArray();
        } finally {
            gate.Release();
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken) {
        await store.SaveAsync(sent!.ToArray(), cancellationToken).ConfigureAwait(false);
        dirty = false;
    }

    private sealed record Candidate(NotificationSnapshot Snapshot, NotificationWindow Window, string Key, decimal Remaining, bool DedupHit);
}
