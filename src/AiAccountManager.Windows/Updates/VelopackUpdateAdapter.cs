using CloudyWing.AiAccountManager.Application.Updates;
using Velopack;

namespace CloudyWing.AiAccountManager.Windows.Updates;

/// <summary>
/// 使用 Velopack 檢查、下載與套用更新。
/// </summary>
public sealed class VelopackUpdateAdapter : IUpdateAdapter {
    private readonly SemaphoreSlim gate = new(1, 1);
    private UpdateManager? manager;
    private UpdateInfo? updateInfo;
    private string? activeFeed;
    private bool downloaded;

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckAsync(Uri feedUri, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(feedUri);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            UpdateManager currentManager = GetManager(feedUri);
            UpdateInfo? result = await currentManager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            updateInfo = result;
            downloaded = false;
            return result is null
                ? new UpdateCheckResult(false, null)
                : new UpdateCheckResult(true, result.TargetFullRelease.Version.ToString());
        } finally {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DownloadAsync(Uri feedUri, IProgress<int> progress, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(feedUri);
        ArgumentNullException.ThrowIfNull(progress);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            UpdateManager currentManager = GetManager(feedUri);
            UpdateInfo currentUpdate = updateInfo
                ?? throw new InvalidOperationException("No verified Velopack update is available.");
            await currentManager.DownloadUpdatesAsync(
                currentUpdate,
                percentage => progress.Report(percentage),
                cancellationToken
            ).ConfigureAwait(false);
            downloaded = true;
        } finally {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ApplyAndRestartAsync(Uri feedUri, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(feedUri);
        cancellationToken.ThrowIfCancellationRequested();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            UpdateManager currentManager = GetManager(feedUri);
            if (!downloaded || updateInfo is null) {
                throw new InvalidOperationException("No downloaded Velopack update is ready to apply.");
            }

            currentManager.ApplyUpdatesAndRestart(updateInfo.TargetFullRelease, []);
            downloaded = false;
        } finally {
            gate.Release();
        }
    }

    private UpdateManager GetManager(Uri feedUri) {
        string normalized = feedUri.AbsoluteUri;
        if (!string.Equals(activeFeed, normalized, StringComparison.Ordinal)) {
            manager = new UpdateManager(normalized);
            updateInfo = null;
            downloaded = false;
            activeFeed = normalized;
        }

        return manager ?? throw new InvalidOperationException("Velopack UpdateManager was not created.");
    }
}
