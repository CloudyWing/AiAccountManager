using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Runtime;
using CloudyWing.AiAccountManager.Application.Security;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 讀取本機 Codex 登入資料並安全匹配已保存額度 profile。
/// </summary>
public sealed class CodexLocalIdentityService : IAsyncDisposable {
    private readonly IAccountCatalogRepository catalogRepository;
    private readonly ICodexLocalAuthIdentityReader authIdentityReader;
    private readonly ICodexLocalAuthFileChangeNotifier fileChangeNotifier;
    private readonly ICodexDesktopProcessStatusSource processStatusSource;
    private readonly IIdentityFingerprinter identityFingerprinter;
    private readonly IClock clock;
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private ReadCancellationSource? activeMonitoringReadCancellation;
    private CodexLocalIdentitySnapshot currentSnapshot = new(
        CodexLocalIdentityStatus.NotChecked,
        null,
        null,
        null,
        false,
        false
    );
    private CodexDesktopProcessStatusSnapshot processStatusSnapshot;
    private long monitoringReadGeneration;
    private bool isMonitoring;
    private bool isMonitoringAvailable;
    private bool isDisposed;

    /// <summary>
    /// 初始化本機身分服務與其唯讀相依元件。
    /// </summary>
    /// <param name="catalogRepository">已保存的 Codex 帳號清單。</param>
    /// <param name="authIdentityReader">唯讀的本機認證讀取器。</param>
    /// <param name="fileChangeNotifier">本機認證檔的變更監看器。</param>
    /// <param name="processStatusSource">獨立的 Desktop 程序狀態來源。</param>
    /// <param name="identityFingerprinter">帳號身分指紋產生器。</param>
    /// <param name="clock">提供狀態時間戳記的 UTC 時鐘。</param>
    public CodexLocalIdentityService(
        IAccountCatalogRepository catalogRepository,
        ICodexLocalAuthIdentityReader authIdentityReader,
        ICodexLocalAuthFileChangeNotifier fileChangeNotifier,
        ICodexDesktopProcessStatusSource processStatusSource,
        IIdentityFingerprinter identityFingerprinter,
        IClock clock
    ) {
        this.catalogRepository = catalogRepository;
        this.authIdentityReader = authIdentityReader;
        this.fileChangeNotifier = fileChangeNotifier;
        this.processStatusSource = processStatusSource;
        this.identityFingerprinter = identityFingerprinter;
        this.clock = clock;
        processStatusSnapshot = new(CodexDesktopProcessStatus.Unknown, clock.UtcNow);
    }

    /// <summary>
    /// 本機身分或 Desktop 程序狀態變更時發生。
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// 取得最新的本機身分快照，不含認證內容。
    /// </summary>
    public CodexLocalIdentitySnapshot CurrentSnapshot {
        get {
            lock (sync) {
                return currentSnapshot;
            }
        }
    }

    /// <summary>
    /// 取得最新的 Desktop 程序狀態快照。
    /// </summary>
    public CodexDesktopProcessStatusSnapshot ProcessStatusSnapshot {
        get {
            lock (sync) {
                return processStatusSnapshot;
            }
        }
    }

    /// <summary>
    /// 開始監看認證檔，並執行第一次本機身分讀取。
    /// </summary>
    /// <param name="cancellationToken">取消啟動的權杖。</param>
    /// <returns>第一次讀取或取代它的較新讀取完成後的最新快照。</returns>
    public async Task<CodexLocalIdentitySnapshot> StartMonitoringAsync(CancellationToken cancellationToken) {
        lock (sync) {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            if (isMonitoring) {
                return currentSnapshot;
            }

            isMonitoring = true;
            isMonitoringAvailable = false;
            fileChangeNotifier.FileChanged += OnFileChanged;
            fileChangeNotifier.MonitoringAvailabilityChanged += OnMonitoringAvailabilityChanged;
            currentSnapshot = currentSnapshot with {
                IsMonitoring = true,
                IsMonitoringAvailable = false
            };
        }

        RaiseStateChanged();
        try {
            await fileChangeNotifier.StartAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            await StopMonitoringAsync().ConfigureAwait(false);
            throw;
        } catch (Exception) {
            UpdateMonitoringAvailability(false);
        }

        try {
            return await ReadCurrentSnapshotAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && IsMonitoringActive()
        ) {
            return CurrentSnapshot;
        }
    }

    /// <summary>
    /// 停止監看認證檔並取消進行中的頁面身分讀取，不釋放單例服務。
    /// </summary>
    /// <returns>監看器停止後完成的工作。</returns>
    public async Task StopMonitoringAsync() {
        ReadCancellationSource? readCancellation;
        lock (sync) {
            if (isDisposed || !isMonitoring) {
                return;
            }

            isMonitoring = false;
            isMonitoringAvailable = false;
            bool preserveActiveRead = activeMonitoringReadCancellation is not null
                && !activeMonitoringReadCancellation.CancelOnMonitoringStop
                && !activeMonitoringReadCancellation.Token.IsCancellationRequested;
            if (!preserveActiveRead) {
                monitoringReadGeneration++;
            }

            readCancellation = activeMonitoringReadCancellation?.CancelOnMonitoringStop == true
                ? activeMonitoringReadCancellation
                : null;
            fileChangeNotifier.FileChanged -= OnFileChanged;
            fileChangeNotifier.MonitoringAvailabilityChanged -= OnMonitoringAvailabilityChanged;
            currentSnapshot = new(
                CodexLocalIdentityStatus.NotChecked,
                null,
                null,
                null,
                false,
                false
            );
        }

        readCancellation?.Cancel();
        RaiseStateChanged();
        try {
            await fileChangeNotifier.StopAsync().ConfigureAwait(false);
        } catch (ObjectDisposedException) {
        }
    }

    /// <summary>
    /// 讀取一次目前的本機認證並比對已保存帳號。
    /// </summary>
    /// <param name="cancellationToken">取消讀取的權杖。</param>
    /// <returns>不含認證權杖與未遮罩身分的快照。</returns>
    public Task<CodexLocalIdentitySnapshot> ReadCurrentSnapshotAsync(CancellationToken cancellationToken) {
        return ReadCurrentSnapshotAsync(cancellationToken, true);
    }

    /// <summary>
    /// 讀取一次目前的本機認證並比對已保存帳號，並可指定停止監看時是否取消讀取。
    /// </summary>
    /// <param name="cancellationToken">取消讀取的權杖。</param>
    /// <param name="cancelOnMonitoringStop">若要在停止監看時取消讀取則為 <see langword="true" />；否則為 <see langword="false" />。</param>
    /// <returns>不含認證權杖與未遮罩身分的快照。</returns>
    public Task<CodexLocalIdentitySnapshot> ReadCurrentSnapshotAsync(
        CancellationToken cancellationToken,
        bool cancelOnMonitoringStop
    ) {
        return ReadCurrentSnapshotAsync(cancellationToken, cancelOnMonitoringStop, false);
    }

    /// <summary>
    /// 為儀表板讀取本機身分快照，不加入管理頁監看讀取的生命週期。
    /// </summary>
    /// <param name="cancellationToken">取消儀表板讀取的權杖。</param>
    /// <returns>不含認證權杖與未遮罩身分的快照。</returns>
    public async Task<CodexLocalIdentitySnapshot> ReadDashboardSnapshotAsync(CancellationToken cancellationToken) {
        ReadCancellationSource requestCancellation;
        lock (sync) {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            requestCancellation = new(lifetimeCancellation.Token, cancellationToken, false);
        }

        bool readGateAcquired = false;
        try {
            await readGate.WaitAsync(requestCancellation.Token).ConfigureAwait(false);
            readGateAcquired = true;
            return await ReadSnapshotUnderGateAsync(requestCancellation.Token).ConfigureAwait(false);
        } finally {
            if (readGateAcquired) {
                readGate.Release();
            }

            requestCancellation.Dispose();
        }
    }

    private async Task<CodexLocalIdentitySnapshot> ReadCurrentSnapshotAsync(
        CancellationToken cancellationToken,
        bool cancelOnMonitoringStop,
        bool requireMonitoring
    ) {
        ReadCancellationSource requestCancellation;
        ReadCancellationSource? previousCancellation;
        long generation;
        lock (sync) {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            if (requireMonitoring && !isMonitoring) {
                return currentSnapshot;
            }

            generation = ++monitoringReadGeneration;
            previousCancellation = activeMonitoringReadCancellation;
            requestCancellation = new(
                lifetimeCancellation.Token,
                cancellationToken,
                cancelOnMonitoringStop
            );
            activeMonitoringReadCancellation = requestCancellation;
            currentSnapshot = CreateSnapshot(
                CodexLocalIdentityStatus.Loading,
                null,
                null,
                clock.UtcNow
            );
        }

        bool readGateAcquired = false;
        try {
            previousCancellation?.Cancel();
            RaiseStateChanged();
            try {
                await readGate.WaitAsync(requestCancellation.Token).ConfigureAwait(false);
                readGateAcquired = true;
                if (!IsCurrentGeneration(generation)) {
                    return CurrentSnapshot;
                }

                CodexLocalIdentitySnapshot result = await ReadSnapshotUnderGateAsync(requestCancellation.Token)
                    .ConfigureAwait(false);
                return PublishIfCurrent(generation, result);
            } catch (OperationCanceledException) when (requestCancellation.Token.IsCancellationRequested) {
                throw;
            } catch (Exception) {
                return PublishIfCurrent(
                    generation,
                    CreateSnapshot(CodexLocalIdentityStatus.Unavailable, null, null, clock.UtcNow)
                );
            }
        } finally {
            if (readGateAcquired) {
                readGate.Release();
            }

            lock (sync) {
                if (ReferenceEquals(activeMonitoringReadCancellation, requestCancellation)) {
                    activeMonitoringReadCancellation = null;
                }
            }

            requestCancellation.Dispose();
        }
    }

    private async Task<CodexLocalIdentitySnapshot> ReadSnapshotUnderGateAsync(CancellationToken cancellationToken) {
        CodexLocalAuthIdentity? identity = null;
        try {
            CodexLocalAuthReadResult readResult = await authIdentityReader
                .ReadCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
            identity = readResult.Identity;
            if (readResult.Status != CodexLocalAuthReadStatus.Success) {
                return CreateSnapshot(MapReadStatus(readResult.Status), null, null, clock.UtcNow);
            }

            if (identity is null || (identity.AccountId is null && identity.Email is null)) {
                return CreateSnapshot(CodexLocalIdentityStatus.IdentityUnavailable, null, null, clock.UtcNow);
            }

            return await MatchProfileAsync(identity, cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return CreateSnapshot(CodexLocalIdentityStatus.Unavailable, null, null, clock.UtcNow);
        } finally {
            identity?.Clear();
        }
    }

    /// <summary>
    /// 重新檢查 Codex Desktop 的程序狀態。
    /// </summary>
    /// <param name="cancellationToken">取消程序檢查的權杖。</param>
    /// <returns>最新的程序狀態與檢查時間。</returns>
    public async Task<CodexDesktopProcessStatusSnapshot> ReadProcessStatusAsync(CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        CodexDesktopProcessStatusSnapshot result;
        try {
            result = await processStatusSource.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            result = new(CodexDesktopProcessStatus.Unknown, clock.UtcNow);
        }

        lock (sync) {
            if (!isDisposed) {
                processStatusSnapshot = result;
            }
        }

        RaiseStateChanged();
        return result;
    }

    /// <summary>
    /// 應用程式關閉時停止進行中的作業，並釋放監看器與同步資源。
    /// </summary>
    /// <returns>所有資源釋放後完成的工作。</returns>
    public async ValueTask DisposeAsync() {
        lock (sync) {
            if (isDisposed) {
                return;
            }

            isDisposed = true;
        }

        lifetimeCancellation.Cancel();
        await StopMonitoringAfterDisposeAsync().ConfigureAwait(false);
        await readGate.WaitAsync().ConfigureAwait(false);
        readGate.Release();
        await fileChangeNotifier.DisposeAsync().ConfigureAwait(false);
        readGate.Dispose();
        lifetimeCancellation.Dispose();
    }

    private async Task<CodexLocalIdentitySnapshot> MatchProfileAsync(
        CodexLocalAuthIdentity identity,
        CancellationToken cancellationToken
    ) {
        string fingerprint = identityFingerprinter.CreateFingerprint(
            identity.AccountId,
            identity.Email,
            "chatgpt"
        );
        string maskedIdentity = !string.IsNullOrWhiteSpace(identity.Email)
            ? identityFingerprinter.MaskEmail(identity.Email)
            : MaskAccountId(identity.AccountId);

        IAccountCatalogRepository.Snapshot catalog;
        try {
            catalog = await catalogRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return CreateSnapshot(CodexLocalIdentityStatus.CatalogUnavailable, null, maskedIdentity, clock.UtcNow);
        }

        for (int attempt = 0; attempt < 2; attempt++) {
            Guid[] matches = FindMatches(catalog.Accounts, identity.AccountId, fingerprint);
            IAccountCatalogRepository.Snapshot verifiedCatalog;
            try {
                verifiedCatalog = await catalogRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception) {
                return CreateSnapshot(CodexLocalIdentityStatus.CatalogUnavailable, null, maskedIdentity, clock.UtcNow);
            }

            if (string.Equals(catalog.Sha256, verifiedCatalog.Sha256, StringComparison.Ordinal)) {
                return matches.Length switch {
                    1 => CreateSnapshot(
                        CodexLocalIdentityStatus.Matched,
                        matches[0],
                        maskedIdentity,
                        clock.UtcNow
                    ),
                    0 => CreateSnapshot(CodexLocalIdentityStatus.Unmatched, null, maskedIdentity, clock.UtcNow),
                    _ => CreateSnapshot(CodexLocalIdentityStatus.Ambiguous, null, maskedIdentity, clock.UtcNow)
                };
            }

            if (attempt == 1) {
                return CreateSnapshot(CodexLocalIdentityStatus.CatalogUnavailable, null, maskedIdentity, clock.UtcNow);
            }

            catalog = verifiedCatalog;
        }

        return CreateSnapshot(CodexLocalIdentityStatus.CatalogUnavailable, null, maskedIdentity, clock.UtcNow);
    }

    private string MaskAccountId(string? accountId) {
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length < 4) {
            return "••••";
        }

        return "••••" + accountId[^4..];
    }

    private static Guid[] FindMatches(
        IReadOnlyList<CodexAccountProfile> profiles,
        string? accountId,
        string fingerprint
    ) {
        return profiles
            .Where(profile => accountId is not null
                ? string.Equals(profile.IdentityFingerprint, fingerprint, StringComparison.Ordinal)
                : string.Equals(profile.FallbackIdentityFingerprint, fingerprint, StringComparison.Ordinal))
            .Select(profile => profile.AccountId)
            .ToArray();
    }

    private static CodexLocalIdentityStatus MapReadStatus(CodexLocalAuthReadStatus status) {
        return status switch {
            CodexLocalAuthReadStatus.FileMissing => CodexLocalIdentityStatus.FileMissing,
            CodexLocalAuthReadStatus.InvalidFormat => CodexLocalIdentityStatus.InvalidFormat,
            CodexLocalAuthReadStatus.UnsupportedAuth => CodexLocalIdentityStatus.UnsupportedAuth,
            CodexLocalAuthReadStatus.IdentityUnavailable => CodexLocalIdentityStatus.IdentityUnavailable,
            CodexLocalAuthReadStatus.ConflictingClaims => CodexLocalIdentityStatus.ConflictingClaims,
            CodexLocalAuthReadStatus.FileTooLarge => CodexLocalIdentityStatus.FileTooLarge,
            CodexLocalAuthReadStatus.UnsafePath => CodexLocalIdentityStatus.UnsafePath,
            CodexLocalAuthReadStatus.SharingViolation => CodexLocalIdentityStatus.SharingViolation,
            CodexLocalAuthReadStatus.AccessDenied => CodexLocalIdentityStatus.AccessDenied,
            CodexLocalAuthReadStatus.ChangedDuringRead => CodexLocalIdentityStatus.ChangedDuringRead,
            CodexLocalAuthReadStatus.Unavailable => CodexLocalIdentityStatus.Unavailable,
            _ => CodexLocalIdentityStatus.IdentityUnavailable
        };
    }

    private CodexLocalIdentitySnapshot CreateSnapshot(
        CodexLocalIdentityStatus status,
        Guid? matchedProfileAccountId,
        string? maskedIdentity,
        DateTimeOffset? checkedAtUtc
    ) {
        lock (sync) {
            return new(
                status,
                matchedProfileAccountId,
                maskedIdentity,
                checkedAtUtc,
                isMonitoring,
                isMonitoring && isMonitoringAvailable
            );
        }
    }

    private CodexLocalIdentitySnapshot PublishIfCurrent(
        long generation,
        CodexLocalIdentitySnapshot snapshot
    ) {
        lock (sync) {
            if (isDisposed || generation != monitoringReadGeneration) {
                return currentSnapshot;
            }

            currentSnapshot = snapshot with {
                IsMonitoring = isMonitoring,
                IsMonitoringAvailable = isMonitoring && isMonitoringAvailable
            };
        }

        RaiseStateChanged();
        return CurrentSnapshot;
    }

    private bool IsCurrentGeneration(long generation) {
        lock (sync) {
            return !isDisposed && generation == monitoringReadGeneration;
        }
    }

    private bool IsMonitoringActive() {
        lock (sync) {
            return isMonitoring && !isDisposed;
        }
    }

    private void OnFileChanged() {
        _ = ReadAfterFileChangeAsync();
    }

    private async Task ReadAfterFileChangeAsync() {
        try {
            await ReadCurrentSnapshotAsync(lifetimeCancellation.Token, true, true).ConfigureAwait(false);
        } catch (OperationCanceledException) {
        } catch (ObjectDisposedException) {
        }
    }

    private void OnMonitoringAvailabilityChanged(bool isAvailable) {
        UpdateMonitoringAvailability(isAvailable);
    }

    private void UpdateMonitoringAvailability(bool isAvailable) {
        lock (sync) {
            if (isDisposed) {
                return;
            }

            isMonitoringAvailable = isAvailable;
            currentSnapshot = currentSnapshot with {
                IsMonitoringAvailable = isMonitoring && isAvailable
            };
        }

        RaiseStateChanged();
    }

    private async Task StopMonitoringAfterDisposeAsync() {
        ReadCancellationSource? readCancellation;
        lock (sync) {
            monitoringReadGeneration++;
            isMonitoring = false;
            isMonitoringAvailable = false;
            readCancellation = activeMonitoringReadCancellation;
            fileChangeNotifier.FileChanged -= OnFileChanged;
            fileChangeNotifier.MonitoringAvailabilityChanged -= OnMonitoringAvailabilityChanged;
        }

        readCancellation?.Cancel();
        try {
            await fileChangeNotifier.StopAsync().ConfigureAwait(false);
        } catch (ObjectDisposedException) {
        }
    }

    private void RaiseStateChanged() {
        EventHandler? handler;
        lock (sync) {
            if (isDisposed) {
                return;
            }

            handler = StateChanged;
        }

        handler?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ReadCancellationSource : IDisposable {
        private readonly object sync = new();
        private readonly CancellationTokenSource source;
        private int activeCancellationCount;
        private bool isOwnerDisposed;
        private bool isSourceDisposed;

        public ReadCancellationSource(
            CancellationToken lifetimeToken,
            CancellationToken requestToken,
            bool cancelOnMonitoringStop
        ) {
            source = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, requestToken);
            Token = source.Token;
            CancelOnMonitoringStop = cancelOnMonitoringStop;
        }

        public CancellationToken Token { get; }

        public bool CancelOnMonitoringStop { get; }

        public void Cancel() {
            lock (sync) {
                if (isOwnerDisposed) {
                    return;
                }

                activeCancellationCount++;
            }

            // Cancellation callbacks can re-enter the service, so run them outside this lock.
            try {
                source.Cancel();
            } finally {
                ReleaseCancellation();
            }
        }

        public void Dispose() {
            bool disposeSource = false;
            lock (sync) {
                isOwnerDisposed = true;
                if (activeCancellationCount == 0 && !isSourceDisposed) {
                    isSourceDisposed = true;
                    disposeSource = true;
                }
            }

            if (disposeSource) {
                source.Dispose();
            }
        }

        private void ReleaseCancellation() {
            bool disposeSource = false;
            lock (sync) {
                activeCancellationCount--;
                if (isOwnerDisposed && activeCancellationCount == 0 && !isSourceDisposed) {
                    isSourceDisposed = true;
                    disposeSource = true;
                }
            }

            if (disposeSource) {
                source.Dispose();
            }
        }
    }
}
