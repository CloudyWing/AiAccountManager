using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Updates;

namespace CloudyWing.AiAccountManager.Application.Settings;

/// <summary>
/// 載入與儲存設定，並載入 Codex 帳號清單。
/// </summary>
public sealed class SettingsService {
    private const string SettingsLoadFailure = "無法讀取設定，請重試。";
    private const string CatalogLoadFailure = "無法讀取 Codex 額度帳號，請重試。";
    private readonly ISettingsRepository settingsRepository;
    private readonly IAccountCatalogRepository accountCatalogRepository;
    private readonly IStartupRegistration? startupRegistration;
    private readonly UpdateFeedApplyCoordinator? feedCoordinator;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ISettingsRepository.Snapshot? settingsSnapshot;
    private IAccountCatalogRepository.Snapshot? accountSnapshot;
    private long updateFeedVersion;
    private bool hasPublishedFeed;
    private string? publishedFeedUrl;

    /// <summary>
    /// 初始化設定服務。
    /// </summary>
    /// <param name="settingsRepository">設定儲存庫。</param>
    /// <param name="accountCatalogRepository">帳號清單儲存庫。</param>
    /// <param name="startupRegistration">目前使用者的啟動項介面。</param>
    /// <param name="feedCoordinator">協調更新來源與套用交接的物件。</param>
    public SettingsService(
        ISettingsRepository settingsRepository,
        IAccountCatalogRepository accountCatalogRepository,
        IStartupRegistration? startupRegistration = null,
        UpdateFeedApplyCoordinator? feedCoordinator = null
    ) {
        this.settingsRepository = settingsRepository;
        this.accountCatalogRepository = accountCatalogRepository;
        this.startupRegistration = startupRegistration;
        this.feedCoordinator = feedCoordinator;
    }

    /// <summary>
    /// 載入或儲存作業更新狀態時發生。
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// 取得載入的設定；尚未載入時為 <see langword="null" />。
    /// </summary>
    public AppSettings? Settings => settingsSnapshot?.Settings;

    /// <summary>
    /// 取得更新來源的變更序號。
    /// </summary>
    public long UpdateFeedVersion => Interlocked.Read(ref updateFeedVersion);

    /// <summary>
    /// 取得載入的帳號清單；尚未載入時為空集合。
    /// </summary>
    public IReadOnlyList<CodexAccountProfile> Accounts
        => accountSnapshot?.Accounts ?? [];

    /// <summary>
    /// 取得設定載入或儲存的錯誤訊息。
    /// </summary>
    public string? SettingsError { get; private set; }

    /// <summary>
    /// 取得帳號清單載入的錯誤訊息。
    /// </summary>
    public string? CatalogError { get; private set; }

    /// <summary>
    /// 取得目前使用者啟動項的讀取錯誤。
    /// </summary>
    public string? StartupError { get; private set; }

    /// <summary>
    /// 取得目前使用者啟動項是否存在；無法查詢時為 null。
    /// </summary>
    public bool? IsStartupRegistered { get; private set; }

    /// <summary>
    /// 載入設定與帳號清單，並通知狀態變更。
    /// </summary>
    /// <param name="cancellationToken">取消載入的權杖。</param>
    /// <returns>載入完成的工作。</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken) {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            await LoadSettingsCoreAsync(cancellationToken).ConfigureAwait(false);
            await LoadCatalogCoreAsync(cancellationToken).ConfigureAwait(false);
            ReadStartupRegistration();
        } finally {
            gate.Release();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 重新載入設定與帳號清單。
    /// </summary>
    /// <param name="cancellationToken">取消載入的權杖。</param>
    /// <returns>重新載入完成的工作。</returns>
    public async Task RetryLoadAsync(CancellationToken cancellationToken) {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 驗證並以已載入的版本儲存設定。
    /// </summary>
    /// <param name="settings">要儲存的設定。</param>
    /// <param name="cancellationToken">取消儲存的權杖。</param>
    /// <returns>若設定已儲存則為 <see langword="true" />；否則為 <see langword="false" />。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings" /> 為 <see langword="null" />。</exception>
    public async Task<bool> SaveAsync(AppSettings settings, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid(out string validationError)) {
            SettingsError = validationError;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (settingsSnapshot is null) {
                await LoadSettingsCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            if (settingsSnapshot is null) {
                return false;
            }

            ISettingsRepository.Snapshot currentSnapshot = settingsSnapshot;
            return await PublishSettingsAsync(
                settings,
                () => SaveSnapshotCoreAsync(settings, currentSnapshot, cancellationToken),
                "正在套用更新，暫時無法變更更新來源。",
                cancellationToken
            ).ConfigureAwait(false);
        } finally {
            gate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<ISettingsRepository.Snapshot?> SaveSnapshotCoreAsync(
        AppSettings settings,
        ISettingsRepository.Snapshot currentSnapshot,
        CancellationToken cancellationToken
    ) {
        string? previousStartup = null;
        bool startupChanged = startupRegistration is not null && settings.StartWithWindows != currentSnapshot.Settings.StartWithWindows;
        if (startupChanged) {
            try {
                previousStartup = startupRegistration!.Read();
            } catch (Exception) {
                StartupError = "無法讀取 Windows 開機啟動狀態。";
                ReadStartupRegistration(preserveError: true);
                return null;
            }
            try {
                startupRegistration.SetEnabled(settings.StartWithWindows);
            } catch (Exception) {
                StartupError = "無法更新 Windows 開機啟動設定。";
                RestoreStartupRegistration(previousStartup);
                return null;
            }
        }

        ISettingsRepository.SaveResult result;
        try {
            result = await settingsRepository.SaveAsync(settings, currentSnapshot.Sha256, cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            if (startupChanged) {
                RestoreStartupRegistration(previousStartup);
            }
            throw;
        } catch (Exception) {
            if (startupChanged) {
                RestoreStartupRegistration(previousStartup);
            }
            SettingsError = SettingsLoadFailure;
            return null;
        }
        if (!result.Saved || result.Snapshot is null) {
            if (startupChanged) {
                RestoreStartupRegistration(previousStartup);
            }
            SettingsError = result.Conflict
                ? "設定檔已在其他位置更新。請重新讀取後再儲存。"
                : SettingsLoadFailure;
            return null;
        }
        ReadStartupRegistration();
        return result.Snapshot;
    }

    private void RestoreStartupRegistration(string? previousValue) {
        try {
            startupRegistration!.Restore(previousValue);
        } catch (Exception) {
            StartupError = "設定保存失敗，且無法還原 Windows 開機啟動項目。請檢查實際狀態。";
        }
        ReadStartupRegistration(preserveError: true);
    }

    private void ReadStartupRegistration(bool preserveError = false) {
        if (startupRegistration is null) {
            return;
        }
        try {
            IsStartupRegistered = startupRegistration.Read() is not null;
            if (!preserveError) {
                StartupError = null;
            }
        } catch (Exception) {
            IsStartupRegistered = null;
            StartupError = "無法讀取 Windows 開機啟動狀態。";
        }
    }

    /// <summary>
    /// 提供目前使用者開機啟動項的查詢、設定與回復。
    /// </summary>
    public interface IStartupRegistration {
        /// <summary>讀取原始啟動值。</summary>
        /// <returns>原始值；不存在時為 null。</returns>
        string? Read();

        /// <summary>設定啟動項是否啟用。</summary>
        /// <param name="enabled">是否啟用。</param>
        void SetEnabled(bool enabled);

        /// <summary>回復先前的啟動值。</summary>
        /// <param name="value">先前的值；null 表示移除。</param>
        void Restore(string? value);
    }

    private async Task LoadSettingsCoreAsync(CancellationToken cancellationToken) {
        try {
            ISettingsRepository.Snapshot loaded = await settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
            await PublishSettingsAsync(
                loaded.Settings,
                () => Task.FromResult<ISettingsRepository.Snapshot?>(loaded),
                "正在套用更新，暫時無法載入變更後的更新來源。",
                cancellationToken
            ).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            settingsSnapshot = null;
            SettingsError = SettingsLoadFailure;
        }
    }

    private async Task<bool> PublishSettingsAsync(
        AppSettings candidate,
        Func<Task<ISettingsRepository.Snapshot?>> createSnapshot,
        string applyingError,
        CancellationToken cancellationToken
    ) {
        bool feedChanged = hasPublishedFeed
            && !string.Equals(publishedFeedUrl, candidate.UpdateFeedUrl, StringComparison.Ordinal);
        using IDisposable? feedLease = feedChanged && feedCoordinator is not null
            ? await feedCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false)
            : null;
        if (feedChanged && feedCoordinator?.IsApplying == true) {
            SettingsError = applyingError;
            return false;
        }
        ISettingsRepository.Snapshot? snapshot = await createSnapshot().ConfigureAwait(false);
        if (snapshot is null) {
            return false;
        }
        if (feedChanged) {
            Interlocked.Increment(ref updateFeedVersion);
        }
        settingsSnapshot = snapshot;
        publishedFeedUrl = snapshot.Settings.UpdateFeedUrl;
        hasPublishedFeed = true;
        SettingsError = null;
        return true;
    }

    private async Task LoadCatalogCoreAsync(CancellationToken cancellationToken) {
        try {
            accountSnapshot = await accountCatalogRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
            CatalogError = null;
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            accountSnapshot = null;
            CatalogError = CatalogLoadFailure;
        }
    }
}
