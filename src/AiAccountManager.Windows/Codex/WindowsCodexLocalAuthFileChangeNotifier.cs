using CloudyWing.AiAccountManager.Application.Codex;

namespace CloudyWing.AiAccountManager.Windows.Codex;

/// <summary>
/// 監看目前 Windows 使用者 Codex 認證檔，並在變更後執行 quiet-period debounce。
/// </summary>
public sealed class WindowsCodexLocalAuthFileChangeNotifier : ICodexLocalAuthFileChangeNotifier {
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(300);
    private readonly object sync = new();
    private readonly string userProfilePath;
    private readonly string codexDirectoryPath;
    private FileSystemWatcher? watcher;
    private CancellationTokenSource? debounceCancellation;
    private bool monitoring;
    private bool monitoringAvailable;
    private bool recoveryInProgress;
    private bool disposed;

    /// <summary>
    /// 初始化固定監看使用者 profile 下 `.codex/auth.json` 的通知器。
    /// </summary>
    public WindowsCodexLocalAuthFileChangeNotifier()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) {
    }

    internal WindowsCodexLocalAuthFileChangeNotifier(string userProfilePath) {
        this.userProfilePath = string.IsNullOrWhiteSpace(userProfilePath)
            ? ""
            : Path.GetFullPath(userProfilePath);
        codexDirectoryPath = Path.Combine(this.userProfilePath, ".codex");
    }

    /// <inheritdoc />
    public event Action? FileChanged;

    /// <inheritdoc />
    public event Action<bool>? MonitoringAvailabilityChanged;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        bool? availability = null;
        lock (sync) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (monitoring) {
                return Task.CompletedTask;
            }

            monitoring = true;
            recoveryInProgress = false;
            monitoringAvailable = TryCreateWatcherLocked();
            availability = monitoringAvailable;
        }

        MonitoringAvailabilityChanged?.Invoke(availability.Value);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync() {
        bool notifyUnavailable;
        lock (sync) {
            if (!monitoring) {
                return Task.CompletedTask;
            }

            monitoring = false;
            recoveryInProgress = false;
            debounceCancellation?.Cancel();
            debounceCancellation?.Dispose();
            debounceCancellation = null;
            DisposeWatcherLocked();
            notifyUnavailable = monitoringAvailable;
            monitoringAvailable = false;
        }

        if (notifyUnavailable) {
            MonitoringAvailabilityChanged?.Invoke(false);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        lock (sync) {
            if (disposed) {
                return;
            }

            disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
        FileChanged = null;
        MonitoringAvailabilityChanged = null;
    }

    private bool TryCreateWatcherLocked() {
        DisposeWatcherLocked();
        if (string.IsNullOrWhiteSpace(userProfilePath)) {
            return false;
        }

        string directoryPath = Directory.Exists(codexDirectoryPath)
            ? codexDirectoryPath
            : userProfilePath;
        string filter = string.Equals(directoryPath, codexDirectoryPath, StringComparison.OrdinalIgnoreCase)
            ? "auth.json"
            : ".codex";
        NotifyFilters notifyFilters = filter == "auth.json"
            ? NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            : NotifyFilters.DirectoryName | NotifyFilters.CreationTime;

        try {
            FileSystemWatcher nextWatcher = new(directoryPath, filter) {
                IncludeSubdirectories = false,
                NotifyFilter = notifyFilters,
                EnableRaisingEvents = false
            };
            nextWatcher.Changed += OnChanged;
            nextWatcher.Created += OnChanged;
            nextWatcher.Deleted += OnChanged;
            nextWatcher.Renamed += OnRenamed;
            nextWatcher.Error += OnError;
            nextWatcher.EnableRaisingEvents = true;
            watcher = nextWatcher;
            return true;
        } catch (Exception) {
            DisposeWatcherLocked();
            return false;
        }
    }

    private void DisposeWatcherLocked() {
        FileSystemWatcher? currentWatcher = watcher;
        watcher = null;
        if (currentWatcher is null) {
            return;
        }

        currentWatcher.EnableRaisingEvents = false;
        currentWatcher.Changed -= OnChanged;
        currentWatcher.Created -= OnChanged;
        currentWatcher.Deleted -= OnChanged;
        currentWatcher.Renamed -= OnRenamed;
        currentWatcher.Error -= OnError;
        currentWatcher.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs eventArgs) {
        string eventName = Path.GetFileName(eventArgs.Name ?? eventArgs.FullPath);
        if (string.Equals(eventName, "auth.json", StringComparison.OrdinalIgnoreCase)) {
            ScheduleFileChanged();
            return;
        }

        if (string.Equals(eventName, ".codex", StringComparison.OrdinalIgnoreCase)) {
            RebuildForCodexDirectoryChange();
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs eventArgs) {
        string eventName = Path.GetFileName(eventArgs.Name ?? eventArgs.FullPath);
        string oldName = Path.GetFileName(eventArgs.OldName ?? eventArgs.OldFullPath);
        if (string.Equals(eventName, "auth.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(oldName, "auth.json", StringComparison.OrdinalIgnoreCase)) {
            ScheduleFileChanged();
            return;
        }

        if (string.Equals(eventName, ".codex", StringComparison.OrdinalIgnoreCase)
            || string.Equals(oldName, ".codex", StringComparison.OrdinalIgnoreCase)) {
            RebuildForCodexDirectoryChange();
        }
    }

    private void RebuildForCodexDirectoryChange() {
        bool? availability = null;
        lock (sync) {
            if (!monitoring || disposed) {
                return;
            }

            bool previousAvailability = monitoringAvailable;
            monitoringAvailable = TryCreateWatcherLocked();
            if (previousAvailability != monitoringAvailable) {
                availability = monitoringAvailable;
            }
        }

        if (availability.HasValue) {
            MonitoringAvailabilityChanged?.Invoke(availability.Value);
        }

        ScheduleFileChanged();
    }

    private void OnError(object sender, ErrorEventArgs eventArgs) {
        lock (sync) {
            if (!monitoring || disposed || recoveryInProgress) {
                return;
            }

            recoveryInProgress = true;
        }

        _ = RecoverWatcherAsync();
    }

    private Task RecoverWatcherAsync() {
        bool previousAvailability;
        bool available;
        lock (sync) {
            if (!monitoring || disposed) {
                recoveryInProgress = false;
                return Task.CompletedTask;
            }

            previousAvailability = monitoringAvailable;
            monitoringAvailable = false;
            available = TryCreateWatcherLocked();
            monitoringAvailable = available;
            recoveryInProgress = false;
        }

        if (previousAvailability) {
            MonitoringAvailabilityChanged?.Invoke(false);
        }

        if (available) {
            MonitoringAvailabilityChanged?.Invoke(true);
            ScheduleFileChanged();
        }

        return Task.CompletedTask;
    }

    private void ScheduleFileChanged() {
        CancellationTokenSource cancellation;
        lock (sync) {
            if (!monitoring || disposed) {
                return;
            }

            debounceCancellation?.Cancel();
            debounceCancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            debounceCancellation = cancellation;
        }

        _ = NotifyAfterDebounceAsync(cancellation);
    }

    private async Task NotifyAfterDebounceAsync(CancellationTokenSource cancellation) {
        try {
            await Task.Delay(DebounceInterval, cancellation.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            cancellation.Dispose();
            return;
        }

        Action? handler = null;
        lock (sync) {
            if (monitoring && !disposed && ReferenceEquals(debounceCancellation, cancellation)) {
                debounceCancellation = null;
                handler = FileChanged;
            }
        }

        cancellation.Dispose();
        handler?.Invoke();
    }
}
