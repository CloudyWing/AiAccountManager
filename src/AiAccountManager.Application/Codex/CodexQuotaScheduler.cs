using System.Threading.Channels;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Settings;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 依設定的間隔排程帳號額度更新。
/// </summary>
public sealed class CodexQuotaScheduler : IDisposable {
    private readonly SettingsService settingsService;
    private readonly IAccountCatalogRepository catalogRepository;
    private readonly CodexQuotaService quotaService;
    private readonly CodexResetNewsService? resetNews;
    private readonly Channel<bool> settingsChanges = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private int pauseCount;
    private int currentIntervalMinutes;
    private CancellationTokenSource? stoppingCancellation;
    private Task? loopTask;

    /// <summary>
    /// 初始化額度更新排程器並訂閱設定變更。
    /// </summary>
    /// <param name="settingsService">設定服務。</param>
    /// <param name="catalogRepository">帳號目錄儲存庫。</param>
    /// <param name="quotaService">額度更新服務。</param>
    /// <param name="resetNews">重置消息服務。</param>
    public CodexQuotaScheduler(
        SettingsService settingsService,
        IAccountCatalogRepository catalogRepository,
        CodexQuotaService quotaService,
        CodexResetNewsService? resetNews = null
    ) {
        this.settingsService = settingsService;
        this.catalogRepository = catalogRepository;
        this.quotaService = quotaService;
        this.resetNews = resetNews;
        settingsService.StateChanged += OnSettingsChanged;
    }

    /// <summary>
    /// 取得目前的更新間隔，單位為分鐘。
    /// </summary>
    public int CurrentIntervalMinutes => Volatile.Read(ref currentIntervalMinutes);

    /// <summary>
    /// 取得一個值，用以指出排程器是否暫停。
    /// </summary>
    public bool IsPaused => Volatile.Read(ref pauseCount) > 0;

    /// <summary>
    /// 暫停額度更新，直到回傳的使用權釋放。
    /// </summary>
    /// <returns>解除一次暫停的使用權。</returns>
    public IDisposable Pause() {
        Interlocked.Increment(ref pauseCount);
        return new PauseLease(this);
    }

    /// <summary>
    /// 啟動額度更新排程迴圈。
    /// </summary>
    /// <returns>表示啟動作業的工作。</returns>
    /// <exception cref="InvalidOperationException">排程迴圈已啟動。</exception>
    public Task StartAsync() {
        if (loopTask is not null) {
            throw new InvalidOperationException("The Codex quota scheduler is already running.");
        }

        stoppingCancellation = new CancellationTokenSource();
        loopTask = ExecuteLoopAsync(stoppingCancellation.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 要求排程迴圈停止並等待結束。
    /// </summary>
    /// <param name="cancellationToken">取消等待的權杖。</param>
    /// <returns>表示停止作業的工作。</returns>
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

    private async Task ExecuteLoopAsync(CancellationToken stoppingToken) {
        if (Volatile.Read(ref pauseCount) == 0) {
            try {
                Task newsTask = resetNews?.RefreshAsync(stoppingToken) ?? Task.CompletedTask;
                await RefreshCatalogAsync(stoppingToken).ConfigureAwait(false);
                await newsTask.ConfigureAwait(false);
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            }
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

            if (Volatile.Read(ref pauseCount) > 0) {
                continue;
            }

            try {
                Task newsTask = resetNews?.RefreshAsync(stoppingToken) ?? Task.CompletedTask;
                await RefreshCatalogAsync(stoppingToken).ConfigureAwait(false);
                await newsTask.ConfigureAwait(false);
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            }
        }
    }

    /// <summary>
    /// 取消排程並釋放設定訂閱與取消資源。
    /// </summary>
    public void Dispose() {
        settingsService.StateChanged -= OnSettingsChanged;
        settingsChanges.Writer.TryComplete();
        stoppingCancellation?.Cancel();
        stoppingCancellation?.Dispose();
    }

    private async Task RefreshCatalogAsync(CancellationToken cancellationToken) {
        IAccountCatalogRepository.Snapshot catalog;
        try {
            catalog = await catalogRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return;
        }

        foreach (CodexAccountProfile profile in catalog.Accounts) {
            if (Volatile.Read(ref pauseCount) > 0) {
                return;
            }

            try {
                _ = await quotaService.RefreshAsync(profile.AccountId, cancellationToken).ConfigureAwait(false);
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

    private void ReleasePause() {
        Interlocked.Decrement(ref pauseCount);
    }

    private sealed class PauseLease(CodexQuotaScheduler owner) : IDisposable {
        private CodexQuotaScheduler? owner = owner;

        public void Dispose() {
            Interlocked.Exchange(ref owner, null)?.ReleasePause();
        }
    }
}
