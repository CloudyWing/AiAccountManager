using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Operations;
using CloudyWing.AiAccountManager.Application.Runtime;
using CloudyWing.AiAccountManager.Application.Security;
using CloudyWing.AiAccountManager.Application.Settings;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 提供 Codex 額度快照的查詢、更新與取消功能。
/// </summary>
public sealed class CodexQuotaService {
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly IAccountCatalogRepository catalogRepository;
    private readonly IQuotaSnapshotRepository snapshotRepository;
    private readonly ISettingsRepository settingsRepository;
    private readonly ICodexAppServerSessionFactory sessionFactory;
    private readonly QuotaResponseParser parser;
    private readonly IIdentityFingerprinter identityFingerprinter;
    private readonly IClock clock;
    private readonly ExclusiveOperationCoordinator operationCoordinator;
    private readonly Lock flightSync = new();
    private readonly Dictionary<Guid, RefreshFlight> flights = [];
    private readonly HashSet<Guid> redeemingAccounts = [];

    /// <summary>
    /// 建立 <see cref="CodexQuotaService" /> 的新執行個體。
    /// </summary>
    /// <param name="catalogRepository">帳號目錄資料存放區。</param>
    /// <param name="snapshotRepository">額度快照資料存放區。</param>
    /// <param name="settingsRepository">應用程式設定資料存放區。</param>
    /// <param name="sessionFactory">Codex app-server 工作階段工廠。</param>
    /// <param name="parser">額度回應剖析器。</param>
    /// <param name="identityFingerprinter">帳號身分指紋產生器。</param>
    /// <param name="clock">系統時鐘。</param>
    /// <param name="operationCoordinator">帳號操作協調器。</param>
    public CodexQuotaService(
        IAccountCatalogRepository catalogRepository,
        IQuotaSnapshotRepository snapshotRepository,
        ISettingsRepository settingsRepository,
        ICodexAppServerSessionFactory sessionFactory,
        QuotaResponseParser parser,
        IIdentityFingerprinter identityFingerprinter,
        IClock clock,
        ExclusiveOperationCoordinator operationCoordinator
    ) {
        this.catalogRepository = catalogRepository;
        this.snapshotRepository = snapshotRepository;
        this.settingsRepository = settingsRepository;
        this.sessionFactory = sessionFactory;
        this.parser = parser;
        this.identityFingerprinter = identityFingerprinter;
        this.clock = clock;
        this.operationCoordinator = operationCoordinator;
    }

    /// <summary>
    /// 當帳號額度狀態變更時發生。
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>確認帳號身分後使用一張重置券，成功時刷新額度。</summary>
    /// <param name="accountId">帳號識別碼。</param>
    /// <param name="profileRevision">確認畫面所使用的帳號版本。</param>
    /// <param name="creditId">可見明細中選定的券識別碼。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>伺服器回傳的結果。</returns>
    public Task<string> ConsumeResetCreditAsync(Guid accountId, long profileRevision, string? creditId, CancellationToken cancellationToken) {
        return ConsumeResetCreditCoreAsync(accountId, profileRevision, creditId, Guid.NewGuid().ToString(), null, cancellationToken);
    }

    /// <summary>使用已持久保存識別碼的自動用券作業。</summary>
    /// <param name="accountId">帳號識別碼。</param>
    /// <param name="profileRevision">帳號版本。</param>
    /// <param name="creditId">重置券識別碼。</param>
    /// <param name="idempotencyKey">此嘗試的識別碼。</param>
    /// <param name="beforeSend">取得帳號操作權後執行的檢查與持久保存。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>用券結果。</returns>
    public Task<string> ConsumeResetCreditAutomaticallyAsync(Guid accountId, long profileRevision, string creditId,
        string idempotencyKey, Func<CancellationToken, Task<bool>> beforeSend, CancellationToken cancellationToken) {
        return ConsumeResetCreditCoreAsync(accountId, profileRevision, creditId, idempotencyKey, beforeSend, cancellationToken);
    }

    private async Task<string> ConsumeResetCreditCoreAsync(Guid accountId, long profileRevision, string? creditId,
        string key, Func<CancellationToken, Task<bool>>? beforeSend, CancellationToken cancellationToken) {
        lock (flightSync) {
            if (!redeemingAccounts.Add(accountId)) {
                return "inFlight";
            }
        }

        try {
            string outcome;
            await using (IOperationLease lease = await operationCoordinator
                .EnterProfileOrQuotaOperationAsync(accountId, cancellationToken).ConfigureAwait(false)) {
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.CancellationToken);
                CancellationToken token = linked.Token;
                IAccountCatalogRepository.Snapshot catalog = await catalogRepository.LoadAsync(token).ConfigureAwait(false);
                CodexAccountProfile? profile = catalog.Accounts.FirstOrDefault(account => account.AccountId == accountId);
                if (profile is null || profile.ProfileRevision != profileRevision) {
                    return "profileChanged";
                }

                await using ICodexAppServerSession session = await sessionFactory
                    .CreateAsync(accountId, profile.ProfileRevision, token).ConfigureAwait(false);
                await session.InitializeAsync(token).ConfigureAwait(false);
                CodexAccountRead account = await ReadWithTimeoutAsync(t => session.ReadAccountAsync(false, t), token).ConfigureAwait(false);
                if (!string.Equals(account.AccountType, "chatgpt", StringComparison.Ordinal)
                    || !IsIdentityMatch(profile, account.AccountId, account.Email)) {
                    return "profileChanged";
                }

                IAccountCatalogRepository.Snapshot current = await catalogRepository.LoadAsync(token).ConfigureAwait(false);
                CodexAccountProfile? latest = current.Accounts.FirstOrDefault(candidate => candidate.AccountId == accountId);
                if (latest is null || latest.ProfileRevision != profile.ProfileRevision
                    || !string.Equals(latest.IdentityFingerprint, profile.IdentityFingerprint, StringComparison.Ordinal)) {
                    return "profileChanged";
                }

                if (beforeSend is not null && !await beforeSend(token).ConfigureAwait(false)) {
                    return "cancelled";
                }
                if (beforeSend is not null && token.IsCancellationRequested) { return "cancelled"; }
                token.ThrowIfCancellationRequested();
                outcome = await ReadWithTimeoutAsync(t => session.ConsumeResetCreditAsync(key, creditId, t), token).ConfigureAwait(false);
            }

            if (outcome == "reset") {
                try {
                    QuotaSnapshotView refreshed = await RefreshCoreAsync(accountId, CancellationToken.None, afterReset: true).ConfigureAwait(false);
                    if (refreshed.Status != QuotaDisplayStatus.Fresh) {
                        return "resetRefreshFailed";
                    }
                } catch (Exception) {
                    return "resetRefreshFailed";
                }
            }

            return outcome;
        } finally {
            lock (flightSync) {
                redeemingAccounts.Remove(accountId);
            }
        }
    }

    /// <summary>
    /// 取得指定帳號的額度狀態檢視。
    /// </summary>
    /// <param name="accountId">Codex 帳號識別碼。</param>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>指定帳號的額度檢視；帳號不存在時為未知狀態。</returns>
    /// <exception cref="ArgumentException">指定的 <paramref name="accountId" /> 為 <see cref="Guid.Empty" /> 時。</exception>
    /// <exception cref="OperationCanceledException">讀取作業遭到取消時。</exception>
    public async Task<QuotaSnapshotView> GetViewAsync(Guid accountId, CancellationToken cancellationToken) {
        if (accountId == Guid.Empty) {
            throw new ArgumentException("Account ID must not be empty.", nameof(accountId));
        }

        IAccountCatalogRepository.Snapshot catalog = await catalogRepository
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        CodexAccountProfile? profile = catalog.Accounts.FirstOrDefault(account => account.AccountId == accountId);
        if (profile is null) {
            return new QuotaSnapshotView(accountId, null, QuotaDisplayStatus.Unknown, false, "找不到 Codex 額度帳號。");
        }

        IQuotaSnapshotRepository.Snapshot? stored;
        try {
            stored = await snapshotRepository
                .LoadAsync(accountId, cancellationToken)
                .ConfigureAwait(false);
        } catch (JsonException) {
            return CreateUnreadableSnapshotView(accountId);
        } catch (InvalidDataException) {
            return CreateUnreadableSnapshotView(accountId);
        } catch (IOException) {
            return CreateUnreadableSnapshotView(accountId);
        } catch (UnauthorizedAccessException) {
            return CreateUnreadableSnapshotView(accountId);
        }

        QuotaSnapshotRecord? snapshot = stored?.Data;
        if (snapshot is not null && !string.Equals(
            snapshot.IdentityFingerprint,
            profile.IdentityFingerprint,
            StringComparison.Ordinal
        )) {
            snapshot = null;
        }

        bool isRefreshing;
        lock (flightSync) {
            isRefreshing = flights.ContainsKey(accountId);
        }

        AppSettings settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        return CreateView(accountId, snapshot, settings.QuotaRefreshIntervalMinutes, isRefreshing);
    }

    /// <summary>
    /// 重新整理指定帳號的額度，並共用該帳號的進行中工作。
    /// </summary>
    /// <param name="accountId">Codex 帳號識別碼。</param>
    /// <param name="cancellationToken">取消此呼叫等待的權杖。</param>
    /// <returns>額度刷新工作完成後的狀態檢視。</returns>
    /// <remarks>
    /// 同帳號的呼叫會共用一個刷新工作。呼叫端取消時只會移除該呼叫的等待者。
    /// 若它是最後一位等待者，且工作尚未退休或完成，服務會取消共用工作。
    /// 已退休、完成或取消的工作不再接受等待者，之後的呼叫會建立新的刷新工作。
    /// 呼叫 <see cref="CancelRefresh" /> 可主動取消目前刷新。
    /// </remarks>
    /// <exception cref="ArgumentException">指定的 <paramref name="accountId" /> 為 <see cref="Guid.Empty" /> 時。</exception>
    /// <exception cref="OperationCanceledException">此呼叫的等待或共用刷新工作遭到取消時。</exception>
    /// <exception cref="AggregateException">取消最後一位等待者時，底層取消回呼發生未處理例外。</exception>
    public async Task<QuotaSnapshotView> RefreshAsync(Guid accountId, CancellationToken cancellationToken) {
        if (accountId == Guid.Empty) {
            throw new ArgumentException("Account ID must not be empty.", nameof(accountId));
        }

        RefreshFlight flight;
        bool startFlight = false;
        lock (flightSync) {
            if (!flights.TryGetValue(accountId, out flight!) || !flight.TryAddWaiter()) {
                flight = new RefreshFlight();
                flight.AddWaiter();
                flights[accountId] = flight;
                startFlight = true;
            }
        }

        if (startFlight) {
            _ = RunFlightAsync(accountId, flight);
        }

        try {
            return await flight.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        } finally {
            ReleaseFlightWaiter(accountId, flight);
        }
    }

    /// <summary>
    /// 取消指定帳號目前可取消的額度刷新。
    /// </summary>
    /// <param name="accountId">Codex 帳號識別碼。</param>
    /// <remarks>
    /// 沒有可取消的刷新工作時不執行動作。
    /// </remarks>
    /// <exception cref="AggregateException">取消回呼發生未處理例外時。</exception>
    public void CancelRefresh(Guid accountId) {
        RefreshFlight? flightToCancel = null;
        lock (flightSync) {
            if (flights.TryGetValue(accountId, out RefreshFlight? flight) && flight.TryRetire()) {
                if (flights.TryGetValue(accountId, out RefreshFlight? current)
                    && ReferenceEquals(current, flight)) {
                    flights.Remove(accountId);
                }

                flightToCancel = flight;
            }
        }

        flightToCancel?.Cancel();
    }

    private void ReleaseFlightWaiter(Guid accountId, RefreshFlight flight) {
        bool cancel;
        lock (flightSync) {
            cancel = flight.ReleaseWaiter();
            if (cancel
                && flights.TryGetValue(accountId, out RefreshFlight? current)
                && ReferenceEquals(current, flight)) {
                flights.Remove(accountId);
            }
        }

        if (cancel) {
            flight.Cancel();
        }
    }

    private async Task RunFlightAsync(Guid accountId, RefreshFlight flight) {
        QuotaSnapshotView? result = null;
        bool canceled = false;
        CancellationToken flightToken = flight.Cancellation.Token;
        try {
            result = await RefreshCoreAsync(accountId, flightToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (flightToken.IsCancellationRequested) {
            canceled = true;
        } catch (Exception) {
            try {
                result = await GetViewAsync(accountId, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception) {
                result = new QuotaSnapshotView(
                    accountId,
                    null,
                    QuotaDisplayStatus.Error,
                    true,
                    "無法讀取 Codex 額度狀態。"
                );
            }
        } finally {
            lock (flightSync) {
                flight.MarkCompleted();
                if (flights.TryGetValue(accountId, out RefreshFlight? current)
                    && ReferenceEquals(current, flight)) {
                    flights.Remove(accountId);
                }
            }

            if (canceled) {
                flight.Completion.TrySetCanceled(flightToken);
            } else {
                flight.Completion.TrySetResult(result!);
            }

            flight.Dispose();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<QuotaSnapshotView> RefreshCoreAsync(Guid accountId, CancellationToken cancellationToken, bool afterReset = false) {
        await using IOperationLease operationLease = await operationCoordinator
            .EnterProfileOrQuotaOperationAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        using CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            operationLease.CancellationToken
        );
        CancellationToken operationToken = operationCancellation.Token;
        IAccountCatalogRepository.Snapshot initialCatalog = await catalogRepository
            .LoadAsync(operationToken)
            .ConfigureAwait(false);
        CodexAccountProfile? profile = initialCatalog.Accounts.FirstOrDefault(account => account.AccountId == accountId);
        if (profile is null) {
            return new QuotaSnapshotView(accountId, null, QuotaDisplayStatus.Unknown, false, "找不到 Codex 額度帳號。");
        }
        CodexAccountProfile initialProfile = profile;

        IQuotaSnapshotRepository.Snapshot? stored = await snapshotRepository
            .LoadAsync(accountId, operationToken)
            .ConfigureAwait(false);
        QuotaSnapshotRecord? previous = stored?.Data;
        if (previous is not null && !string.Equals(
            previous.IdentityFingerprint,
            profile.IdentityFingerprint,
            StringComparison.Ordinal
        )) {
            previous = null;
        }

        DateTimeOffset now = clock.UtcNow;
        if (!afterReset && previous?.LastAttempt is QuotaRefreshAttempt lastAttempt) {
            if (lastAttempt.Status == QuotaRefreshStatus.ReauthenticationRequired) {
                return CreateView(accountId, previous, (await ReadSettingsAsync(operationToken).ConfigureAwait(false)).QuotaRefreshIntervalMinutes, false);
            }

            if ((lastAttempt.Status is QuotaRefreshStatus.Error or QuotaRefreshStatus.Incomplete)
                && lastAttempt.RetryAtUtc is DateTimeOffset retryAt
                && retryAt > now) {
                return CreateView(accountId, previous, (await ReadSettingsAsync(operationToken).ConfigureAwait(false)).QuotaRefreshIntervalMinutes, false);
            }
        }

        int failureCount = previous?.LastAttempt?.FailureCount ?? 0;
        QuotaSnapshotRecord refreshing = new(
            accountId,
            profile.IdentityFingerprint,
            profile.ProfileRevision,
            previous?.LastSuccessfulSnapshot,
            new QuotaRefreshAttempt(QuotaRefreshStatus.Refreshing, now, afterReset ? "after-reset" : null, failureCount, null, [])
        );
        IQuotaSnapshotRepository.SaveResult refreshSave = await snapshotRepository
            .SaveAsync(refreshing, stored?.Sha256, operationToken)
            .ConfigureAwait(false);
        if ((!refreshSave.Saved || refreshSave.Snapshot is null) && afterReset) {
            stored = await snapshotRepository.LoadAsync(accountId, operationToken).ConfigureAwait(false);
            previous = stored?.Data;
            if (previous is not null && !string.Equals(
                previous.IdentityFingerprint, profile.IdentityFingerprint, StringComparison.Ordinal
            )) {
                previous = null;
            }
            failureCount = previous?.LastAttempt?.FailureCount ?? 0;
            refreshing = new QuotaSnapshotRecord(
                accountId, profile.IdentityFingerprint, profile.ProfileRevision,
                previous?.LastSuccessfulSnapshot,
                new QuotaRefreshAttempt(QuotaRefreshStatus.Refreshing, now, "after-reset", failureCount, null, [])
            );
            refreshSave = await snapshotRepository.SaveAsync(refreshing, stored?.Sha256, operationToken).ConfigureAwait(false);
        }
        if (!refreshSave.Saved || refreshSave.Snapshot is null) {
            if (afterReset) {
                return new QuotaSnapshotView(accountId, previous, QuotaDisplayStatus.Error, true, "重置後額度刷新失敗。");
            }
            return await GetViewAsync(accountId, operationToken).ConfigureAwait(false);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        IQuotaSnapshotRepository.Snapshot currentStored = refreshSave.Snapshot;
        try {
            await using ICodexAppServerSession session = await sessionFactory
                .CreateAsync(accountId, profile.ProfileRevision, operationToken)
                .ConfigureAwait(false);
            await session.InitializeAsync(operationToken).ConfigureAwait(false);

            using CancellationTokenSource notificationCancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
            Channel<string> notificationQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(1) {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
            int accountUpdatedSignal = 0;
            Task notificationPump = PumpNotificationsAsync(
                session,
                notificationQueue.Writer,
                () => Interlocked.Exchange(ref accountUpdatedSignal, 1),
                notificationCancellation.Token
            );
            QuotaReadResult read = await ReadQuotaAsync(session, operationToken).ConfigureAwait(false);
            int notificationPasses = 0;
            while (notificationPasses < 2 && notificationQueue.Reader.TryRead(out _)) {
                notificationPasses++;
                read = await ReadQuotaAsync(session, operationToken).ConfigureAwait(false);
            }

            notificationCancellation.Cancel();
            try {
                await notificationPump.ConfigureAwait(false);
            } catch (OperationCanceledException) when (notificationCancellation.IsCancellationRequested) {
            }

            if (notificationQueue.Reader.TryRead(out _)) {
                read = await ReadQuotaAsync(session, operationToken).ConfigureAwait(false);
            }

            bool accountUpdated = Volatile.Read(ref accountUpdatedSignal) != 0;
            if (!string.Equals(read.Account.AccountType, "chatgpt", StringComparison.Ordinal)) {
                return await SaveFailureAsync(
                    profile,
                    currentStored,
                    QuotaRefreshStatus.ReauthenticationRequired,
                    "account-type-invalid",
                    operationToken,
                    retryAtUtc: null
                ).ConfigureAwait(false);
            }

            string? rateAccountId = ReadOptionalString(read.RateLimits, "accountId");
            if (rateAccountId is not null
                && read.Account.AccountId is not null
                && !string.Equals(rateAccountId, read.Account.AccountId, StringComparison.Ordinal)) {
                return await SaveFailureAsync(
                    profile,
                    currentStored,
                    QuotaRefreshStatus.ReauthenticationRequired,
                    "account-identity-conflict",
                    operationToken,
                    retryAtUtc: null
                ).ConfigureAwait(false);
            }

            bool identityMatches = IsIdentityMatch(profile, rateAccountId ?? read.Account.AccountId, read.Account.Email);
            if (!identityMatches && accountUpdated && HasAccountIdentity(rateAccountId ?? read.Account.AccountId, read.Account.Email)) {
                profile = await CommitUpdatedIdentityAsync(
                    profile,
                    initialCatalog,
                    rateAccountId ?? read.Account.AccountId,
                    read.Account.Email,
                    operationToken
                ).ConfigureAwait(false);
                if (profile is null) {
                    return await GetViewAsync(accountId, operationToken).ConfigureAwait(false);
                }

                previous = null;
            } else if (!identityMatches) {
                return await SaveFailureAsync(
                    profile,
                    currentStored,
                    QuotaRefreshStatus.ReauthenticationRequired,
                    "account-identity-unconfirmed",
                    operationToken,
                    retryAtUtc: null
                ).ConfigureAwait(false);
            }

            QuotaParseResult parsed = parser.Parse(read.RateLimits);
            if (parsed.Status == QuotaParseStatus.Incomplete) {
                return await SaveFailureAsync(
                    profile,
                    currentStored,
                    QuotaRefreshStatus.Incomplete,
                    parsed.FailureCode ?? "quota-incomplete",
                    operationToken,
                    parsed.MissingWindows,
                    NextRetryAt(failureCount + 1, null)
                ).ConfigureAwait(false);
            }

            bool hasStandardWindows = parsed.FiveHour is not null && parsed.Weekly is not null;
            bool hasFreePlanWindow = parsed.FreePlanWindow is not null;
            if (parsed.Status != QuotaParseStatus.Complete || hasStandardWindows == hasFreePlanWindow) {
                throw new InvalidDataException("Codex rate-limit response was invalid.");
            }

            IAccountCatalogRepository.Snapshot finalCatalog = await catalogRepository
                .LoadAsync(operationToken)
                .ConfigureAwait(false);
            CodexAccountProfile? currentProfile = finalCatalog.Accounts.FirstOrDefault(account => account.AccountId == accountId);
            if (currentProfile is null
                || currentProfile.ProfileRevision != profile.ProfileRevision
                || !string.Equals(currentProfile.IdentityFingerprint, profile.IdentityFingerprint, StringComparison.Ordinal)) {
                return await GetViewAsync(accountId, operationToken).ConfigureAwait(false);
            }

            QuotaSnapshotRecord successful = new(
                accountId,
                profile.IdentityFingerprint,
                profile.ProfileRevision,
                new QuotaSuccessfulSnapshot(
                    clock.UtcNow,
                    parsed.FiveHour,
                    parsed.Weekly,
                    parsed.ResetCredits,
                    parsed.FreePlanWindow
                ),
                new QuotaRefreshAttempt(QuotaRefreshStatus.Unknown, clock.UtcNow, null, 0, null, [])
            );
            IQuotaSnapshotRepository.SaveResult saved = await snapshotRepository
                .SaveAsync(successful, currentStored.Sha256, operationToken)
                .ConfigureAwait(false);
            if (!saved.Saved) {
                if (afterReset) {
                    return new QuotaSnapshotView(
                        accountId, previous, QuotaDisplayStatus.Error,
                        previous?.LastSuccessfulSnapshot is not null, "重置後額度刷新失敗。"
                    );
                }
                return await GetViewAsync(accountId, operationToken).ConfigureAwait(false);
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            AppSettings settings = await ReadSettingsAsync(operationToken).ConfigureAwait(false);
            return CreateView(accountId, successful, settings.QuotaRefreshIntervalMinutes, false);
        } catch (OperationCanceledException) when (operationToken.IsCancellationRequested) {
            await TrySaveCanceledAsync(currentStored).ConfigureAwait(false);
            throw;
        } catch (CodexAppServerRpcException failure) when (failure.IsUnauthorized) {
            return await SaveFailureAsync(
                profile ?? initialProfile,
                currentStored,
                QuotaRefreshStatus.ReauthenticationRequired,
                "authentication-expired",
                CancellationToken.None,
                retryAtUtc: null
            ).ConfigureAwait(false);
        } catch (Exception failure) {
            TimeSpan? retryAfter = failure is CodexAppServerRpcException { IsRateLimited: true } rateLimited
                ? rateLimited.RetryAfter
                : null;
            return await SaveFailureAsync(
                profile ?? initialProfile,
                currentStored,
                QuotaRefreshStatus.Error,
                ClassifyFailure(failure),
                CancellationToken.None,
                retryAtUtc: NextRetryAt(failureCount + 1, retryAfter)
            ).ConfigureAwait(false);
        }
    }

    private async Task<CodexAccountProfile?> CommitUpdatedIdentityAsync(
        CodexAccountProfile profile,
        IAccountCatalogRepository.Snapshot expectedCatalog,
        string? externalAccountId,
        string? email,
        CancellationToken cancellationToken
    ) {
        if (!HasAccountIdentity(externalAccountId, email)) {
            return null;
        }

        string fingerprint = identityFingerprinter.CreateFingerprint(externalAccountId, email, "chatgpt");
        CodexAccountProfile updated = profile with {
            MaskedIdentity = identityFingerprinter.MaskEmail(email),
            IdentityFingerprint = fingerprint,
            ExternalAccountId = string.IsNullOrWhiteSpace(externalAccountId) ? null : externalAccountId,
            FallbackIdentityFingerprint = string.IsNullOrWhiteSpace(email)
                ? null
                : identityFingerprinter.CreateFingerprint(null, email, "chatgpt"),
            ProfileRevision = checked(profile.ProfileRevision + 1)
        };
        CodexAccountProfile[] accounts = expectedCatalog.Accounts.ToArray();
        int profileIndex = Array.FindIndex(accounts, account => account.AccountId == profile.AccountId);
        if (profileIndex < 0
            || accounts[profileIndex].ProfileRevision != profile.ProfileRevision
            || !string.Equals(accounts[profileIndex].IdentityFingerprint, profile.IdentityFingerprint, StringComparison.Ordinal)) {
            return null;
        }

        accounts[profileIndex] = updated;
        IAccountCatalogRepository.SaveResult save = await catalogRepository
            .SaveAsync(accounts, expectedCatalog.Sha256, cancellationToken)
            .ConfigureAwait(false);
        if (!save.Saved) {
            return null;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    private async Task<QuotaReadResult> ReadQuotaAsync(
        ICodexAppServerSession session,
        CancellationToken cancellationToken
    ) {
        CodexAccountRead account = await ReadWithTimeoutAsync(
            token => session.ReadAccountAsync(refreshToken: true, token),
            cancellationToken
        ).ConfigureAwait(false);
        JsonElement rateLimits = await ReadWithTimeoutAsync(
            session.ReadRateLimitsAsync,
            cancellationToken
        ).ConfigureAwait(false);
        return new QuotaReadResult(account, rateLimits);
    }

    private static async Task<T> ReadWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> read,
        CancellationToken cancellationToken
    ) {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try {
            return await read(timeout.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (timeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested) {
            throw new TimeoutException("Codex quota request timed out.");
        }
    }

    private static async Task PumpNotificationsAsync(
        ICodexAppServerSession session,
        ChannelWriter<string> writer,
        Action accountUpdatedSignal,
        CancellationToken cancellationToken
    ) {
        try {
            await foreach (CodexAppServerNotification notification in session
                .ReadNotificationsAsync(cancellationToken)
                .ConfigureAwait(false)) {
                if (notification.Method is "account/updated" or "account/rateLimits/updated") {
                    if (notification.Method == "account/updated") {
                        accountUpdatedSignal();
                    }

                    writer.TryWrite(notification.Method);
                }
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        } catch (ChannelClosedException) {
        } catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested) {
        }
    }

    private async Task<QuotaSnapshotView> SaveFailureAsync(
        CodexAccountProfile profile,
        IQuotaSnapshotRepository.Snapshot stored,
        QuotaRefreshStatus status,
        string failureCode,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? missingWindows = null,
        DateTimeOffset? retryAtUtc = null
    ) {
        QuotaSnapshotRecord current = stored.Data;
        if (!string.Equals(current.IdentityFingerprint, profile.IdentityFingerprint, StringComparison.Ordinal)) {
            current = new QuotaSnapshotRecord(
                profile.AccountId,
                profile.IdentityFingerprint,
                profile.ProfileRevision,
                null,
                current.LastAttempt
            );
        }

        bool afterReset = current.LastAttempt?.FailureCode == "after-reset";
        if (afterReset) { failureCode = "after-reset:" + failureCode; }

        int failureCount = status == QuotaRefreshStatus.ReauthenticationRequired
            ? Math.Max(1, current.LastAttempt?.FailureCount ?? 0)
            : Math.Min(30, (current.LastAttempt?.FailureCount ?? 0) + 1);
        QuotaSnapshotRecord failed = current with {
            ProfileRevision = profile.ProfileRevision,
            LastAttempt = new QuotaRefreshAttempt(
                status,
                clock.UtcNow,
                failureCode,
                failureCount,
                retryAtUtc,
                missingWindows ?? []
            )
        };
        IQuotaSnapshotRepository.SaveResult save;
        try {
            save = await snapshotRepository
                .SaveAsync(failed, stored.Sha256, cancellationToken)
                .ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return CreateUnsavedFailureView(profile.AccountId, current, status);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        if (!save.Saved) {
            // 重置後的刷新不可回讀舊快照，否則可能以舊的 Fresh 誤判刷新成功。
            return afterReset
                ? CreateUnsavedFailureView(profile.AccountId, current, status)
                : await GetViewAsync(profile.AccountId, cancellationToken).ConfigureAwait(false);
        }

        int interval = (await ReadSettingsAsync(cancellationToken).ConfigureAwait(false)).QuotaRefreshIntervalMinutes;
        return CreateView(profile.AccountId, failed, interval, false);
    }

    private static QuotaSnapshotView CreateUnsavedFailureView(
        Guid accountId,
        QuotaSnapshotRecord current,
        QuotaRefreshStatus status
    ) {
        return new QuotaSnapshotView(
            accountId,
            current,
            StatusFor(status),
            current.LastSuccessfulSnapshot is not null,
            UserMessage(status)
        );
    }

    private async Task TrySaveCanceledAsync(IQuotaSnapshotRepository.Snapshot stored) {
        try {
            QuotaSnapshotRecord current = stored.Data;
            QuotaSnapshotRecord canceled = current with {
                LastAttempt = new QuotaRefreshAttempt(
                    QuotaRefreshStatus.Unknown,
                    clock.UtcNow,
                    "cancelled",
                    current.LastAttempt?.FailureCount ?? 0,
                    null,
                    []
                )
            };
            _ = await snapshotRepository.SaveAsync(canceled, stored.Sha256, CancellationToken.None).ConfigureAwait(false);
        } catch (Exception) {
        }
    }

    private bool IsIdentityMatch(CodexAccountProfile profile, string? suppliedAccountId, string? email) {
        if (!string.IsNullOrWhiteSpace(profile.ExternalAccountId)) {
            if (!string.IsNullOrWhiteSpace(suppliedAccountId)) {
                return string.Equals(profile.ExternalAccountId, suppliedAccountId, StringComparison.Ordinal);
            }

            return MatchesEmail(profile.FallbackIdentityFingerprint, email);
        }

        if (!string.IsNullOrWhiteSpace(email)
            && MatchesEmail(profile.FallbackIdentityFingerprint ?? profile.IdentityFingerprint, email)) {
            return true;
        }

        if (string.IsNullOrWhiteSpace(suppliedAccountId)) {
            return false;
        }

        string fingerprint = identityFingerprinter.CreateFingerprint(suppliedAccountId, null, "chatgpt");
        return FixedTimeEqualsHex(profile.IdentityFingerprint, fingerprint);
    }

    private bool MatchesEmail(string? expectedFingerprint, string? email) {
        if (string.IsNullOrWhiteSpace(expectedFingerprint) || string.IsNullOrWhiteSpace(email)) {
            return false;
        }

        string fingerprint = identityFingerprinter.CreateFingerprint(null, email, "chatgpt");
        return FixedTimeEqualsHex(expectedFingerprint, fingerprint);
    }

    private static bool FixedTimeEqualsHex(string left, string right) {
        try {
            byte[] leftBytes = Convert.FromHexString(left);
            byte[] rightBytes = Convert.FromHexString(right);
            try {
                return leftBytes.Length == rightBytes.Length
                    && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
            } finally {
                CryptographicOperations.ZeroMemory(leftBytes);
                CryptographicOperations.ZeroMemory(rightBytes);
            }
        } catch (FormatException) {
            return false;
        }
    }

    private static bool HasAccountIdentity(string? accountId, string? email) {
        return !string.IsNullOrWhiteSpace(accountId) || !string.IsNullOrWhiteSpace(email);
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null) {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException("Codex rate-limit identity is invalid.");
        }

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private DateTimeOffset NextRetryAt(int failureCount, TimeSpan? retryAfter) {
        if (retryAfter is TimeSpan delay && delay > TimeSpan.Zero) {
            return clock.UtcNow + delay;
        }

        int exponent = Math.Clamp(failureCount - 1, 0, 5);
        double baseMinutes = Math.Min(30, Math.Pow(2, exponent));
        double jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        return clock.UtcNow + TimeSpan.FromMinutes(baseMinutes * jitter);
    }

    private static string ClassifyFailure(Exception failure) {
        return failure switch {
            TimeoutException => "timeout",
            InvalidDataException => "invalid-response",
            CodexAppServerRpcException rpcFailure => "rpc-" + rpcFailure.ErrorCode.ToString(CultureInfo.InvariantCulture),
            EndOfStreamException => "app-server-closed",
            _ => "refresh-failed"
        };
    }

    private static QuotaSnapshotView CreateUnreadableSnapshotView(Guid accountId) {
        return new QuotaSnapshotView(
            accountId,
            null,
            QuotaDisplayStatus.Error,
            false,
            "Codex 額度資料無法讀取，請重新整理額度。"
        );
    }

    private async Task<AppSettings> ReadSettingsAsync(CancellationToken cancellationToken) {
        try {
            return (await settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false)).Settings;
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return AppSettings.Defaults;
        }
    }

    private QuotaSnapshotView CreateView(
        Guid accountId,
        QuotaSnapshotRecord? snapshot,
        int refreshIntervalMinutes,
        bool isRefreshing
    ) {
        QuotaSuccessfulSnapshot? success = snapshot?.LastSuccessfulSnapshot;
        DateTimeOffset now = clock.UtcNow;
        bool hasOldValue = success is not null;
        bool stale = success is not null
            && now - success.RetrievedAtUtc > TimeSpan.FromMinutes(refreshIntervalMinutes * 2);
        if (isRefreshing) {
            return new QuotaSnapshotView(accountId, snapshot, QuotaDisplayStatus.Refreshing, stale, null);
        }

        QuotaRefreshAttempt? attempt = snapshot?.LastAttempt;
        if (attempt?.Status == QuotaRefreshStatus.ReauthenticationRequired) {
            return new QuotaSnapshotView(
                accountId,
                snapshot,
                QuotaDisplayStatus.ReauthenticationRequired,
                hasOldValue,
                "Codex 登入需要重新確認；舊額度仍保留並標示為過期。"
            );
        }

        if (attempt?.Status == QuotaRefreshStatus.Error && attempt.FailureCode?.StartsWith("after-reset:", StringComparison.Ordinal) == true) {
            return new QuotaSnapshotView(accountId, snapshot, QuotaDisplayStatus.Error, hasOldValue, "重置成功，額度刷新失敗。");
        }

        if (attempt?.Status == QuotaRefreshStatus.Incomplete) {
            string missing = attempt.MissingWindows.Count == 0 ? "" : " 缺少：" + string.Join("、", attempt.MissingWindows) + "。";
            return new QuotaSnapshotView(
                accountId,
                snapshot,
                QuotaDisplayStatus.Incomplete,
                hasOldValue,
                "Codex 額度回應不完整；已保留上一份完整值。" + missing
            );
        }

        if (success is null) {
            return attempt?.Status == QuotaRefreshStatus.Error
                ? new QuotaSnapshotView(accountId, snapshot, QuotaDisplayStatus.Error, false, UserMessage(QuotaRefreshStatus.Error))
                : new QuotaSnapshotView(accountId, snapshot, QuotaDisplayStatus.Unknown, false, null);
        }

        string? message = attempt?.Status == QuotaRefreshStatus.Error
            ? UserMessage(QuotaRefreshStatus.Error)
            : null;
        return new QuotaSnapshotView(
            accountId,
            snapshot,
            stale ? QuotaDisplayStatus.Stale : QuotaDisplayStatus.Fresh,
            stale,
            message
        );
    }

    private static QuotaDisplayStatus StatusFor(QuotaRefreshStatus status) {
        return status switch {
            QuotaRefreshStatus.Refreshing => QuotaDisplayStatus.Refreshing,
            QuotaRefreshStatus.Incomplete => QuotaDisplayStatus.Incomplete,
            QuotaRefreshStatus.ReauthenticationRequired => QuotaDisplayStatus.ReauthenticationRequired,
            QuotaRefreshStatus.Error => QuotaDisplayStatus.Error,
            _ => QuotaDisplayStatus.Unknown
        };
    }

    private static string UserMessage(QuotaRefreshStatus status) {
        return status switch {
            QuotaRefreshStatus.ReauthenticationRequired => "Codex 登入需要重新確認；舊額度仍保留並標示為過期。",
            QuotaRefreshStatus.Incomplete => "Codex 額度回應不完整；已保留上一份完整值。",
            _ => "無法更新 Codex 額度，請稍後重試。"
        };
    }

    private sealed record QuotaReadResult(CodexAccountRead Account, JsonElement RateLimits);

    private sealed class RefreshFlight : IDisposable {
        private readonly Lock sync = new();
        private int waiters;
        private bool acceptingWaiters = true;
        private bool retiring;
        private bool completed;

        /// <summary>
        /// 建立新的額度刷新工作。
        /// </summary>
        public RefreshFlight() {
            Cancellation = new CancellationTokenSource();
            Completion = new TaskCompletionSource<QuotaSnapshotView>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// 取得此共用刷新工作使用的取消來源。
        /// </summary>
        /// <value>取消來源；呼叫 <see cref="Cancel" /> 時會發出取消通知。</value>
        public CancellationTokenSource Cancellation { get; }

        /// <summary>
        /// 取得此共用刷新工作的完成來源。
        /// </summary>
        /// <value>提供共用刷新結果的完成來源。</value>
        public TaskCompletionSource<QuotaSnapshotView> Completion { get; }

        /// <summary>
        /// 加入一位等待此刷新工作的呼叫者。
        /// </summary>
        public void AddWaiter() {
            lock (sync) {
                waiters++;
            }
        }

        /// <summary>
        /// 嘗試將一位呼叫者加入此刷新工作。
        /// </summary>
        /// <returns>成功加入時為 <see langword="true" />；工作已退休、完成或取消時為 <see langword="false" />。</returns>
        public bool TryAddWaiter() {
            lock (sync) {
                if (!acceptingWaiters || retiring || completed || Cancellation.IsCancellationRequested) {
                    return false;
                }

                waiters++;
                return true;
            }
        }

        /// <summary>
        /// 移除一位已停止等待的呼叫者。
        /// </summary>
        /// <returns>最後一位等待者退出並使工作進入退休狀態時為 <see langword="true" />；否則為 <see langword="false" />。</returns>
        public bool ReleaseWaiter() {
            lock (sync) {
                waiters--;
                if (waiters == 0 && acceptingWaiters && !retiring && !completed) {
                    acceptingWaiters = false;
                    retiring = true;
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// 停止接受新的等待者，並將此刷新工作標記為退休。
        /// </summary>
        /// <returns>成功將工作標記為退休時為 <see langword="true" />；工作已退休或完成時為 <see langword="false" />。</returns>
        public bool TryRetire() {
            lock (sync) {
                if (retiring || completed) {
                    return false;
                }

                acceptingWaiters = false;
                retiring = true;
                return true;
            }
        }

        /// <summary>
        /// 將此刷新工作標記為完成並停止接受新的等待者。
        /// </summary>
        public void MarkCompleted() {
            lock (sync) {
                acceptingWaiters = false;
                retiring = true;
                completed = true;
            }
        }

        /// <summary>
        /// 要求取消此刷新工作的底層作業。
        /// </summary>
        /// <exception cref="AggregateException">取消回呼發生未處理例外時。</exception>
        public void Cancel() {
            try {
                Cancellation.Cancel();
            } catch (ObjectDisposedException) {
            }
        }

        /// <summary>
        /// 標記此刷新工作已完成，並釋放其取消來源。
        /// </summary>
        public void Dispose() {
            lock (sync) {
                acceptingWaiters = false;
                retiring = true;
                completed = true;
            }

            Cancellation.Dispose();
        }
    }
}
