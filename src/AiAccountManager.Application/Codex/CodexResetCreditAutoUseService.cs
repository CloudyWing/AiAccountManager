using System.Threading.Channels;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Notifications;
using CloudyWing.AiAccountManager.Application.Runtime;
using CloudyWing.AiAccountManager.Application.Settings;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>依重置券期限排程自動使用。</summary>
public sealed class CodexResetCreditAutoUseService : IDisposable {
    private readonly SettingsService settings;
    private readonly CodexQuotaService quotas;
    private readonly ICodexResetCreditAttemptRepository repository;
    private readonly ICodexResetCreditDelay delay;
    private readonly IClock clock;
    private readonly INotificationSink? notifications;
    private readonly Channel<bool> signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });
    private readonly Lock sync = new();
    private IReadOnlyList<CodexResetCreditAttempt> attempts = [];
    private CancellationTokenSource? stop;
    private CancellationTokenSource? enabledWork;
    private Task? loop;

    /// <summary>建立自動使用服務。</summary>
    /// <param name="settings">設定與帳號目錄。</param>
    /// <param name="quotas">額度與用券服務。</param>
    /// <param name="repository">嘗試紀錄儲存庫。</param>
    /// <param name="delay">期限等待。</param>
    /// <param name="clock">時鐘。</param>
    /// <param name="notifications">自動使用結果的通知；為 null 時不通知。</param>
    public CodexResetCreditAutoUseService(SettingsService settings, CodexQuotaService quotas,
        ICodexResetCreditAttemptRepository repository, ICodexResetCreditDelay delay, IClock clock,
        INotificationSink? notifications = null) {
        this.settings = settings;
        this.quotas = quotas;
        this.repository = repository;
        this.delay = delay;
        this.clock = clock;
        this.notifications = notifications;
        settings.StateChanged += OnChanged;
        quotas.StateChanged += OnChanged;
    }

    /// <summary>紀錄更新時發生。</summary>
    public event EventHandler? StateChanged;

    /// <summary>取得帳號最近的自動使用紀錄。</summary>
    /// <param name="accountId">帳號識別碼。</param>
    /// <returns>最近紀錄，沒有紀錄時為 null。</returns>
    public CodexResetCreditAttempt? GetLatest(Guid accountId) {
        lock (sync) {
            return attempts.Where(item => item.AccountId == accountId).OrderByDescending(item => item.AttemptedAtUtc).FirstOrDefault();
        }
    }

    /// <summary>啟動排程。</summary>
    /// <returns>啟動工作。</returns>
    public Task StartAsync() {
        if (loop is not null) {
            throw new InvalidOperationException("自動使用排程已啟動。");
        }
        stop = new CancellationTokenSource();
        loop = RunAsync(stop.Token);
        return Task.CompletedTask;
    }

    /// <summary>停止排程。</summary>
    /// <param name="cancellationToken">取消等待的權杖。</param>
    /// <returns>停止工作。</returns>
    public async Task StopAsync(CancellationToken cancellationToken) {
        stop?.Cancel();
        if (loop is not null) {
            try { await loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested) { }
        }
    }

    private void OnChanged(object? sender, EventArgs e) {
        if (settings.Settings?.AutoUseResetCredits != true) {
            lock (sync) { enabledWork?.Cancel(); }
        }
        signals.Writer.TryWrite(true);
    }

    private async Task RunAsync(CancellationToken token) {
        bool wasEnabled = false;
        while (!token.IsCancellationRequested) {
            try {
                while (signals.Reader.TryRead(out _)) { }
                await ReloadAttemptsAsync(token).ConfigureAwait(false);
                bool enabled = settings.Settings?.AutoUseResetCredits == true;
                if (enabled && !wasEnabled) {
                    foreach (CodexAccountProfile account in settings.Accounts.ToArray()) {
                        if (settings.Settings?.AutoUseResetCredits != true) { break; }
                        await quotas.RefreshAsync(account.AccountId, token).ConfigureAwait(false);
                    }
                }
                wasEnabled = enabled;
                DateTimeOffset? next = enabled ? await ProcessAsync(token).ConfigureAwait(false) : null;
                using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task signal = signals.Reader.WaitToReadAsync(wait.Token).AsTask();
                Task timer = next.HasValue
                    ? delay.WaitAsync(next.Value > clock.UtcNow ? next.Value - clock.UtcNow : TimeSpan.FromSeconds(1), wait.Token)
                    : delay.WaitAsync(Timeout.InfiniteTimeSpan, wait.Token);
                await Task.WhenAny(signal, timer).ConfigureAwait(false);
                wait.Cancel();
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                return;
            } catch (Exception) {
                await delay.WaitAsync(TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
            }
        }
    }

    private async Task<DateTimeOffset?> ProcessAsync(CancellationToken token) {
        int minutes = settings.Settings?.AutoUseResetCreditsMinutes ?? 30;
        List<(CodexAccountProfile Account, ResetCredit Credit)> candidates = [];
        foreach (CodexAccountProfile account in settings.Accounts.ToArray()) {
            QuotaSnapshotView view = await quotas.GetViewAsync(account.AccountId, token).ConfigureAwait(false);
            if (view.Snapshot?.ProfileRevision != account.ProfileRevision) { continue; }
            foreach (ResetCredit credit in view.Snapshot.LastSuccessfulSnapshot?.ResetCredits?.Credits ?? []) {
                if (credit.Status == "available" && !string.IsNullOrWhiteSpace(credit.Id)
                    && credit.ExpiresAtUtc is DateTimeOffset expiry && expiry > clock.UtcNow
                    && !attempts.Any(item => item.AccountId == account.AccountId && item.CreditId == credit.Id)) {
                    candidates.Add((account, credit));
                }
            }
        }
        foreach ((CodexAccountProfile account, ResetCredit credit) in candidates.OrderBy(x => x.Credit.ExpiresAtUtc).ThenBy(x => x.Credit.Id, StringComparer.Ordinal)) {
            DateTimeOffset due = credit.ExpiresAtUtc!.Value.AddMinutes(-minutes);
            if (due > clock.UtcNow) { return due; }
            if (settings.Settings?.AutoUseResetCredits != true) { return null; }
            await TryUseAsync(account, credit, token).ConfigureAwait(false);
            if (!attempts.Any(item => item.AccountId == account.AccountId && item.CreditId == credit.Id)) {
                return clock.UtcNow.AddSeconds(1);
            }
        }
        return null;
    }

    private async Task TryUseAsync(CodexAccountProfile account, ResetCredit credit, CancellationToken token) {
        using CancellationTokenSource work = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (sync) { enabledWork = work; }
        CodexResetCreditAttempt attempt = new(account.AccountId, credit.Id, clock.UtcNow, Guid.NewGuid().ToString(), "pending");
        bool recorded = false;
        try {
            string result = await quotas.ConsumeResetCreditAutomaticallyAsync(account.AccountId, account.ProfileRevision,
                credit.Id, attempt.IdempotencyKey, async cancellation => {
                    AppSettings? current = settings.Settings;
                    if (current?.AutoUseResetCredits != true) { return false; }
                    QuotaSnapshotView view = await quotas.GetViewAsync(account.AccountId, cancellation).ConfigureAwait(false);
                    ResetCredit? latest = view.Snapshot?.LastSuccessfulSnapshot?.ResetCredits?.Credits?
                        .FirstOrDefault(item => item.Id == credit.Id && item.Status == "available");
                    if (view.Snapshot?.ProfileRevision != account.ProfileRevision || latest?.ExpiresAtUtc is not DateTimeOffset expiry
                        || expiry <= clock.UtcNow || expiry.AddMinutes(-current.AutoUseResetCreditsMinutes) > clock.UtcNow) { return false; }
                    recorded = await repository.TryBeginAsync(attempt, cancellation).ConfigureAwait(false);
                    if (recorded) {
                        lock (sync) {
                            attempts = attempts.Append(attempt).ToArray();
                        }
                    }
                    return recorded && settings.Settings?.AutoUseResetCredits == true && !cancellation.IsCancellationRequested;
                }, work.Token).ConfigureAwait(false);
            if (recorded) {
                await repository.CompleteAsync(attempt with { Result = result }, CancellationToken.None).ConfigureAwait(false);
                await ReloadAttemptsAsync(CancellationToken.None).ConfigureAwait(false);
                await NotifyAsync(account, result).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (recorded) {
            try {
                await repository.CompleteAsync(attempt with { Result = "unconfirmed" }, CancellationToken.None).ConfigureAwait(false);
            } finally {
                await ReloadAttemptsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await NotifyAsync(account, "unconfirmed").ConfigureAwait(false);
        } catch (CodexAppServerRpcException) when (recorded) {
            try {
                await repository.CompleteAsync(attempt with { Result = "error" }, CancellationToken.None).ConfigureAwait(false);
            } finally {
                await ReloadAttemptsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await NotifyAsync(account, "error").ConfigureAwait(false);
        } catch (Exception) {
            if (recorded) { await ReloadAttemptsAsync(CancellationToken.None).ConfigureAwait(false); }
        } finally {
            lock (sync) { if (ReferenceEquals(enabledWork, work)) { enabledWork = null; } }
        }
    }

    private async Task NotifyAsync(CodexAccountProfile account, string result) {
        // 使用者關閉功能而未送出時，沒有用掉重置券，不打擾使用者。
        if (notifications is null || result == "cancelled") { return; }
        string name = string.IsNullOrWhiteSpace(account.Alias) ? account.MaskedIdentity : account.Alias;
        try {
            await notifications.SendAsync(new NotificationRequest("重置券自動使用", $"{name}：{ResultText(result)}",
                "reset-credit-auto-use", "codex"), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception) {
            // 通知失敗不影響已保存的使用紀錄，卡片仍會顯示結果。
        }
    }

    private static string ResultText(string result) => result switch {
        "reset" => "重置成功",
        "resetRefreshFailed" => "重置成功，額度刷新失敗",
        "nothingToReset" => "目前沒有可重置的視窗",
        "noCredit" => "沒有可用重置券",
        "alreadyRedeemed" => "此操作已完成重置",
        "pending" or "unconfirmed" => "結果未確認",
        _ => "使用失敗"
    };

    private async Task ReloadAttemptsAsync(CancellationToken token) {
        IReadOnlyList<CodexResetCreditAttempt> loaded = await repository.LoadAsync(token).ConfigureAwait(false);
        lock (sync) { attempts = loaded; }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>解除事件訂閱並停止工作。</summary>
    public void Dispose() {
        settings.StateChanged -= OnChanged;
        quotas.StateChanged -= OnChanged;
        stop?.Cancel();
        stop?.Dispose();
    }
}
