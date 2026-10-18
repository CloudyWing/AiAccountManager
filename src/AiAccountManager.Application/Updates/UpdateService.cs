using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Operations;
using CloudyWing.AiAccountManager.Application.Settings;

namespace CloudyWing.AiAccountManager.Application.Updates;

/// <summary>
/// 管理更新檢查、下載與套用的狀態。
/// </summary>
public sealed class UpdateService {
    private static readonly TimeSpan ApplyWaitTimeout = TimeSpan.FromSeconds(30);
    private readonly ISettingsRepository settingsRepository;
    private readonly IUpdateAdapter updateAdapter;
    private readonly ExclusiveOperationCoordinator operationCoordinator;
    private readonly CodexQuotaScheduler quotaScheduler;
    private readonly SettingsService? settingsService;
    private readonly UpdateFeedApplyCoordinator? feedCoordinator;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private volatile UpdateServiceState state = new(UpdateStatus.NotConfigured, null, 0, "可檢查更新。", null);
    private string? downloadedFeedUri;
    private long checkedFeedVersion;

    /// <summary>
    /// 初始化更新服務。
    /// </summary>
    /// <param name="settingsRepository">更新來源設定的儲存庫。</param>
    /// <param name="updateAdapter">執行更新作業的介面。</param>
    /// <param name="operationCoordinator">協調互斥作業的物件。</param>
    /// <param name="quotaScheduler">可在套用更新期間暫停的額度排程器。</param>
    /// <param name="settingsService">提供更新來源變更序號的設定服務。</param>
    /// <param name="feedCoordinator">協調更新來源與套用交接的物件。</param>
    public UpdateService(
        ISettingsRepository settingsRepository,
        IUpdateAdapter updateAdapter,
        ExclusiveOperationCoordinator operationCoordinator,
        CodexQuotaScheduler quotaScheduler,
        SettingsService? settingsService = null,
        UpdateFeedApplyCoordinator? feedCoordinator = null
    ) {
        this.settingsRepository = settingsRepository;
        this.updateAdapter = updateAdapter;
        this.operationCoordinator = operationCoordinator;
        this.quotaScheduler = quotaScheduler;
        this.settingsService = settingsService;
        this.feedCoordinator = feedCoordinator;
        if (settingsService is not null) {
            settingsService.StateChanged += OnSettingsChanged;
        }
    }

    /// <summary>
    /// 更新狀態變更時發生。
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// 取得目前的更新狀態。
    /// </summary>
    public UpdateServiceState State => IsFeedVersionCurrent
        ? state
        : new UpdateServiceState(UpdateStatus.Error, null, 0, "更新來源已變更，請重新檢查並下載更新。", null);

    private bool IsFeedVersionCurrent => settingsService is null || settingsService.UpdateFeedVersion == Interlocked.Read(ref checkedFeedVersion);

    private void OnSettingsChanged(object? sender, EventArgs e) {
        if (!IsFeedVersionCurrent) {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 依設定的來源檢查更新並更新服務狀態。
    /// </summary>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>檢查後的更新狀態。</returns>
    public async Task<UpdateServiceState> CheckAsync(CancellationToken cancellationToken) {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            Uri feedUri;
            try {
                feedUri = await ReadFeedAsync(cancellationToken).ConfigureAwait(false);
            } catch (InvalidDataException) {
                return state;
            }
            Interlocked.Exchange(ref checkedFeedVersion, settingsService?.UpdateFeedVersion ?? 0);
            SetState(new UpdateServiceState(UpdateStatus.Checking, null, 0, "正在檢查更新。", feedUri.AbsoluteUri));
            try {
                UpdateCheckResult result = await updateAdapter.CheckAsync(feedUri, cancellationToken).ConfigureAwait(false);
                downloadedFeedUri = null;
                if (!IsFeedVersionCurrent) {
                    return State;
                }
                return SetState(result.UpdateAvailable
                    ? new UpdateServiceState(UpdateStatus.Available, result.Version, 0, "有可用更新。", feedUri.AbsoluteUri)
                    : new UpdateServiceState(UpdateStatus.UpToDate, null, 0, "目前已是最新版本。", feedUri.AbsoluteUri));
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                return SetState(new UpdateServiceState(UpdateStatus.Error, null, 0, "更新檢查已取消。", feedUri.AbsoluteUri));
            } catch (Exception) {
                downloadedFeedUri = null;
                return SetState(new UpdateServiceState(UpdateStatus.Error, null, 0, "無法檢查更新。請稍後重試。", feedUri.AbsoluteUri));
            }
        } finally {
            operationGate.Release();
        }
    }

    /// <summary>
    /// 下載目前來源已檢查到的更新並更新服務狀態。
    /// </summary>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>下載後的更新狀態。</returns>
    public async Task<UpdateServiceState> DownloadAsync(CancellationToken cancellationToken) {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            Uri feedUri;
            try {
                feedUri = await ReadFeedAsync(cancellationToken).ConfigureAwait(false);
            } catch (InvalidDataException) {
                return state;
            }
            if (!IsFeedVersionCurrent || state.Status != UpdateStatus.Available
                || !string.Equals(state.FeedUri, feedUri.AbsoluteUri, StringComparison.Ordinal)) {
                return SetState(new UpdateServiceState(UpdateStatus.Error, null, 0, "請先檢查目前的更新來源。", feedUri.AbsoluteUri));
            }

            SetState(state with { Status = UpdateStatus.Downloading, ProgressPercent = 0, Message = "正在下載更新。" });
            InlineProgress progress = new(value => {
                SetState(State with {
                    Status = UpdateStatus.Downloading,
                    ProgressPercent = Math.Clamp(value, 0, 100),
                    Message = "正在下載更新。"
                });
            });
            try {
                await updateAdapter.DownloadAsync(feedUri, progress, cancellationToken).ConfigureAwait(false);
                if (!IsFeedVersionCurrent) {
                    downloadedFeedUri = null;
                    return State;
                }
                downloadedFeedUri = feedUri.AbsoluteUri;
                return SetState(state with {
                    Status = UpdateStatus.ReadyToApply,
                    ProgressPercent = 100,
                    Message = "更新已下載並準備套用。"
                });
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                downloadedFeedUri = null;
                return SetState(new UpdateServiceState(UpdateStatus.Error, null, 0, "下載已取消；未套用更新。", feedUri.AbsoluteUri));
            } catch (Exception) {
                downloadedFeedUri = null;
                return SetState(new UpdateServiceState(UpdateStatus.Error, null, 0, "無法下載更新。請稍後重試。", feedUri.AbsoluteUri));
            }
        } finally {
            operationGate.Release();
        }
    }

    /// <summary>
    /// 等待互斥作業結束，套用已下載的更新並要求重新啟動。
    /// </summary>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>套用要求後的更新狀態。</returns>
    public async Task<UpdateServiceState> ApplyAsync(CancellationToken cancellationToken) {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            Uri feedUri;
            try {
                feedUri = await ReadFeedAsync(cancellationToken).ConfigureAwait(false);
            } catch (InvalidDataException) {
                return state;
            }
            if (!IsFeedVersionCurrent || state.Status != UpdateStatus.ReadyToApply
                || !string.Equals(downloadedFeedUri, feedUri.AbsoluteUri, StringComparison.Ordinal)) {
                return SetState(new UpdateServiceState(UpdateStatus.Error, null, 0, "尚無已驗證的更新可套用。", feedUri.AbsoluteUri));
            }

            using IDisposable schedulerPause = quotaScheduler.Pause();
            using CancellationTokenSource applyWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            applyWait.CancelAfter(ApplyWaitTimeout);
            bool handoffStarted = false;
            try {
                await using IAsyncDisposable updateLease = await operationCoordinator
                    .EnterUpdateApplyAsync(applyWait.Token)
                    .ConfigureAwait(false);
                applyWait.Token.ThrowIfCancellationRequested();
                using (IDisposable? feedLease = feedCoordinator is null
                    ? null
                    : await feedCoordinator.EnterAsync(applyWait.Token).ConfigureAwait(false)) {
                    applyWait.Token.ThrowIfCancellationRequested();
                    if (!IsFeedVersionCurrent || state.Status != UpdateStatus.ReadyToApply
                        || !string.Equals(downloadedFeedUri, feedUri.AbsoluteUri, StringComparison.Ordinal)
                        || feedCoordinator?.IsApplying == true) {
                        downloadedFeedUri = null;
                        return SetState(new UpdateServiceState(UpdateStatus.Error, null, 0, "更新來源已變更，請重新檢查並下載更新。", feedUri.AbsoluteUri));
                    }
                    feedCoordinator?.BeginApply();
                    handoffStarted = feedCoordinator is not null;
                }
                SetState(state with { Status = UpdateStatus.Applying, Message = "正在安全套用更新並重新啟動。" });
                await updateAdapter.ApplyAndRestartAsync(feedUri, applyWait.Token).ConfigureAwait(false);
                handoffStarted = false;
                downloadedFeedUri = null;
                return SetState(state with { Status = UpdateStatus.Applied, Message = "更新已交給 Velopack 套用。" });
            } catch (OperationCanceledException) when (applyWait.IsCancellationRequested) {
                if (handoffStarted) {
                    await feedCoordinator!.CancelApplyAsync().ConfigureAwait(false);
                }
                downloadedFeedUri = null;
                return SetState(new UpdateServiceState(
                    UpdateStatus.Error,
                    null,
                    0,
                    cancellationToken.IsCancellationRequested
                        ? "更新套用已取消。"
                        : "等待進行中的帳號操作超過 30 秒；已取消更新套用。",
                    feedUri.AbsoluteUri
                ));
            } catch (Exception) {
                if (handoffStarted) {
                    await feedCoordinator!.CancelApplyAsync().ConfigureAwait(false);
                }
                downloadedFeedUri = null;
                return SetState(new UpdateServiceState(UpdateStatus.Error, null, 0, "無法安全套用更新；目前資料已保留。", feedUri.AbsoluteUri));
            }
        } finally {
            operationGate.Release();
        }
    }

    private async Task<Uri> ReadFeedAsync(CancellationToken cancellationToken) {
        ISettingsRepository.Snapshot settings = await settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        try {
            return UpdateFeedResolver.Resolve(settings.Settings.UpdateFeedUrl);
        } catch (InvalidDataException) {
            SetState(new UpdateServiceState(UpdateStatus.Error, null, 0, "更新來源必須是未含帳密的絕對 HTTPS 網址。", null));
            throw;
        }
    }

    private sealed class InlineProgress(Action<int> report) : IProgress<int> {
        public void Report(int value) => report(value);
    }
    private UpdateServiceState SetState(UpdateServiceState next) {
        state = next;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return next;
    }
}
