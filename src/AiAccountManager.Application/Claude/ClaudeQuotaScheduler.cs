using System.Threading.Channels;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Settings;

namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 依設定的刷新間隔，逐一查詢已保存 Claude profile 的額度。
/// </summary>
public sealed class ClaudeQuotaScheduler : IDisposable {
    private readonly SettingsService settingsService;
    private readonly IClaudeAccountRepository accountRepository;
    private readonly ClaudeQuotaService quotaService;
    private readonly Channel<bool> settingsChanges = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private int currentIntervalMinutes;
    private CancellationTokenSource? stoppingCancellation;
    private Task? loopTask;

    /// <summary>
    /// 建立 Claude 額度排程器。
    /// </summary>
    /// <param name="settingsService">提供刷新間隔的設定服務。</param>
    /// <param name="accountRepository">提供已保存 Claude profile 清單的 repository。</param>
    /// <param name="quotaService">執行單一 profile 額度查詢的服務。</param>
    public ClaudeQuotaScheduler(
        SettingsService settingsService,
        IClaudeAccountRepository accountRepository,
        ClaudeQuotaService quotaService
    ) {
        this.settingsService = settingsService;
        this.accountRepository = accountRepository;
        this.quotaService = quotaService;
        settingsService.StateChanged += OnSettingsChanged;
    }

    /// <summary>
    /// 取得目前生效的刷新間隔分鐘數。
    /// </summary>
    /// <value>尚未啟動時為 0。</value>
    public int CurrentIntervalMinutes => Volatile.Read(ref currentIntervalMinutes);

    /// <summary>
    /// 啟動背景排程迴圈。
    /// </summary>
    /// <returns>已啟動的工作。</returns>
    /// <exception cref="InvalidOperationException">排程器已在執行。</exception>
    public Task StartAsync() {
        if (loopTask is not null) {
            throw new InvalidOperationException("The Claude quota scheduler is already running.");
        }

        stoppingCancellation = new CancellationTokenSource();
        loopTask = ExecuteLoopAsync(stoppingCancellation.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止背景排程迴圈並等待目前查詢結束。
    /// </summary>
    /// <param name="cancellationToken">放棄等待的權杖。</param>
    public async Task StopAsync(CancellationToken cancellationToken) {
        CancellationTokenSource? cancellation = stoppingCancellation;
        if (cancellation is null || loopTask is null) {
            return;
        }

        cancellation.Cancel();
        try {
            await loopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException exception) when (loopTask.IsCanceled
            && exception.CancellationToken == cancellation.Token
            && !cancellationToken.IsCancellationRequested) {
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        settingsService.StateChanged -= OnSettingsChanged;
        settingsChanges.Writer.TryComplete();
        stoppingCancellation?.Cancel();
        stoppingCancellation?.Dispose();
    }

    private async Task ExecuteLoopAsync(CancellationToken stoppingToken) {
        try {
            await RefreshProfilesAsync(stoppingToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            return;
        }

        while (!stoppingToken.IsCancellationRequested) {
            int intervalMinutes = settingsService.Settings?.QuotaRefreshIntervalMinutes
                ?? AppSettings.Defaults.QuotaRefreshIntervalMinutes;
            if (intervalMinutes is not (1 or 5 or 15 or 30)) {
                intervalMinutes = AppSettings.Defaults.QuotaRefreshIntervalMinutes;
            }

            Volatile.Write(ref currentIntervalMinutes, intervalMinutes);
            using CancellationTokenSource waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            using PeriodicTimer timer = new(TimeSpan.FromMinutes(intervalMinutes));
            Task<bool> tick = timer.WaitForNextTickAsync(waitCancellation.Token).AsTask();
            Task<bool> settingsChanged = settingsChanges.Reader.WaitToReadAsync(waitCancellation.Token).AsTask();
            Task winner = await Task.WhenAny(tick, settingsChanged).ConfigureAwait(false);
            if (winner == settingsChanged) {
                waitCancellation.Cancel();
                try {
                    _ = await tick.ConfigureAwait(false);
                } catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested) {
                }

                while (settingsChanges.Reader.TryRead(out _)) {
                }

                continue;
            }

            bool hasTick;
            try {
                hasTick = await tick.ConfigureAwait(false);
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            }

            waitCancellation.Cancel();
            try {
                _ = await settingsChanged.ConfigureAwait(false);
            } catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested) {
            }

            if (!hasTick) {
                return;
            }

            try {
                await RefreshProfilesAsync(stoppingToken).ConfigureAwait(false);
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            }
        }
    }

    private async Task RefreshProfilesAsync(CancellationToken cancellationToken) {
        IClaudeAccountRepository.Snapshot catalog;
        try {
            catalog = await accountRepository.LoadProfilesAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return;
        }

        foreach (ClaudeAccountProfile profile in catalog.Profiles) {
            try {
                _ = await quotaService.RefreshAsync(profile.ProfileId, cancellationToken).ConfigureAwait(false);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (OperationCanceledException) {
                return;
            } catch (Exception) {
            }
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) {
        _ = settingsChanges.Writer.TryWrite(true);
    }
}
