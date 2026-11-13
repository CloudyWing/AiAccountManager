using System.ComponentModel;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Operations;
using CloudyWing.AiAccountManager.Application.Runtime;
using CloudyWing.AiAccountManager.Application.Security;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 管理 Codex 帳號的登入、資料與移除作業。
/// </summary>
public sealed class CodexProfileService {
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(20);
    private readonly IAccountCatalogRepository catalogRepository;
    private readonly ICodexProfileStorage profileStorage;
    private readonly ICodexAppServerSessionFactory sessionFactory;
    private readonly IExternalBrowserLauncher browserLauncher;
    private readonly IIdentityFingerprinter identityFingerprinter;
    private readonly IClock clock;
    private readonly ExclusiveOperationCoordinator operationCoordinator;
    private readonly CodexQuotaService? quotaService;

    /// <summary>
    /// 初始化 Codex 帳號服務。
    /// </summary>
    /// <param name="catalogRepository">帳號目錄儲存庫。</param>
    /// <param name="profileStorage">隔離帳號資料儲存體。</param>
    /// <param name="sessionFactory">app-server 工作階段工廠。</param>
    /// <param name="browserLauncher">外部瀏覽器啟動器。</param>
    /// <param name="identityFingerprinter">身分指紋產生器。</param>
    /// <param name="clock">時間來源。</param>
    /// <param name="operationCoordinator">互斥作業協調器。</param>
    /// <param name="quotaService">用於取消額度更新的服務。</param>
    public CodexProfileService(
        IAccountCatalogRepository catalogRepository,
        ICodexProfileStorage profileStorage,
        ICodexAppServerSessionFactory sessionFactory,
        IExternalBrowserLauncher browserLauncher,
        IIdentityFingerprinter identityFingerprinter,
        IClock clock,
        ExclusiveOperationCoordinator operationCoordinator,
        CodexQuotaService? quotaService = null
    ) {
        this.catalogRepository = catalogRepository;
        this.profileStorage = profileStorage;
        this.sessionFactory = sessionFactory;
        this.browserLauncher = browserLauncher;
        this.identityFingerprinter = identityFingerprinter;
        this.clock = clock;
        this.operationCoordinator = operationCoordinator;
        this.quotaService = quotaService;
    }

    /// <summary>
    /// 帳號清單狀態變更時發生。
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// 透過瀏覽器登入並保存新的 Codex 帳號。
    /// </summary>
    /// <param name="alias">新帳號的別名。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>保存的帳號資料。</returns>
    /// <exception cref="CodexProfileOperationException">登入、身分確認、保存或清理失敗。</exception>
    public async Task<CodexAccountProfile> AddAsync(string? alias, CancellationToken cancellationToken) {
        await RetryPendingAddCleanupsAsync(cancellationToken).ConfigureAwait(false);

        Guid accountId = Guid.NewGuid();
        Guid transactionId = Guid.NewGuid();
        await using IOperationLease operationLease = await operationCoordinator
            .EnterProfileOrQuotaOperationAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        using CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationLease.CancellationToken);
        cancellationToken = operationCancellation.Token;

        IAccountCatalogRepository.Snapshot catalog = await catalogRepository
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        CodexProfileTransaction transaction = CreateTransaction(
            transactionId,
            accountId,
            "add",
            "started",
            catalog.Sha256
        );
        await profileStorage.SaveTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);

        bool homeCreationAttempted = false;
        bool catalogCommitted = false;
        bool loginStarted = false;
        bool loginCompleted = false;
        string? loginId = null;
        ICodexAppServerSession? session = null;
        try {
            homeCreationAttempted = true;
            await profileStorage.CreateHomeAsync(accountId, cancellationToken).ConfigureAwait(false);
            transaction = await SaveStageAsync(transaction, "home-created", cancellationToken).ConfigureAwait(false);

            session = await sessionFactory.CreateAsync(accountId, 1, cancellationToken).ConfigureAwait(false);
            await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            CodexLoginStart login = await session.StartChatGptLoginAsync(cancellationToken).ConfigureAwait(false);
            loginId = login.LoginId;
            loginStarted = true;
            await browserLauncher.OpenAsync(login.AuthUrl, cancellationToken).ConfigureAwait(false);

            CodexLoginCompletion completion = await session
                .WaitForLoginCompletionAsync(login.LoginId, LoginTimeout, cancellationToken)
                .ConfigureAwait(false);
            loginCompleted = completion.Succeeded;
            if (!completion.Succeeded) {
                throw new CodexProfileOperationException("Codex 登入未完成，帳號沒有保存，請重試。", null);
            }

            CodexAccountRead account = await session.ReadAccountAsync(refreshToken: true, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(account.AccountType, "chatgpt", StringComparison.Ordinal)) {
                throw new CodexProfileOperationException("Codex 回傳的帳號類型不是 ChatGPT，帳號沒有保存。", null);
            }

            JsonElement rateLimits = await session.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
            string? rateLimitAccountId = ReadOptionalString(rateLimits, "accountId");
            if (account.AccountId is not null
                && rateLimitAccountId is not null
                && !string.Equals(account.AccountId, rateLimitAccountId, StringComparison.Ordinal)) {
                throw new CodexProfileOperationException("Codex 帳號身分不一致，帳號沒有保存。", null);
            }

            string? externalAccountId = rateLimitAccountId ?? account.AccountId;
            if (string.IsNullOrWhiteSpace(externalAccountId) && string.IsNullOrWhiteSpace(account.Email)) {
                throw new CodexProfileOperationException("Codex 沒有提供可確認的帳號身分，帳號沒有保存。", null);
            }

            if (await profileStorage.RemoveUnexpectedAuthFileAsync(accountId, cancellationToken).ConfigureAwait(false)) {
                throw new CodexProfileOperationException(
                    "登入資料處理失敗，帳號未保存，請重試。",
                    null
                );
            }

            string fingerprint = identityFingerprinter.CreateFingerprint(
                externalAccountId,
                account.Email,
                "chatgpt"
            );
            CodexAccountProfile profile = new(
                accountId,
                NormalizeAlias(alias),
                identityFingerprinter.MaskEmail(account.Email),
                fingerprint,
                1,
                clock.UtcNow,
                "connected",
                externalAccountId,
                string.IsNullOrWhiteSpace(account.Email)
                    ? null
                    : identityFingerprinter.CreateFingerprint(null, account.Email, "chatgpt")
            );

            transaction = await SaveStageAsync(transaction, "identity-confirmed", cancellationToken)
                .ConfigureAwait(false);
            IAccountCatalogRepository.SaveResult save = await catalogRepository
                .SaveAsync(catalog.Accounts.Append(profile).ToArray(), catalog.Sha256, cancellationToken)
                .ConfigureAwait(false);
            if (!save.Saved || save.Snapshot is null) {
                throw new CodexProfileOperationException(
                    save.Conflict
                        ? "Codex 帳號清單已在其他位置更新。未保存新帳號，請重新載入後重試。"
                        : "無法保存 Codex 帳號。未保存新帳號，請重試。",
                    null
                );
            }

            catalogCommitted = true;
            transaction = await SaveStageAsync(transaction, "catalog-committed", CancellationToken.None)
                .ConfigureAwait(false);
            await profileStorage.DeleteTransactionAsync(transaction.TransactionId, CancellationToken.None).ConfigureAwait(false);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return profile;
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            Exception? cleanupFailure = await CleanupFailedAddAsync(
                accountId,
                transaction,
                homeCreationAttempted,
                loginStarted,
                loginCompleted,
                loginId,
                session
            ).ConfigureAwait(false);
            if (cleanupFailure is not null) {
                throw new CodexProfileOperationException(
                    "Codex 登入已取消，帳號未保存，請重新載入後重試。",
                    cleanupFailure
                );
            }

            throw;
        } catch (CodexProfileOperationException failure) {
            Exception? cleanupFailure = await CleanupFailedAddAsync(
                accountId,
                transaction,
                homeCreationAttempted,
                loginStarted,
                loginCompleted,
                loginId,
                session
            ).ConfigureAwait(false);
            if (cleanupFailure is not null) {
                throw new CodexProfileOperationException(
                    failure.Message,
                    cleanupFailure
                );
            }

            throw;
        } catch (Exception failure) {
            if (catalogCommitted) {
                StateChanged?.Invoke(this, EventArgs.Empty);
                throw new CodexProfileOperationException(
                    "Codex 帳號已保存，但後續處理未完成。請重新載入帳號清單。",
                    failure
                );
            }
            Exception? cleanupFailure = await CleanupFailedAddAsync(
                accountId,
                transaction,
                homeCreationAttempted,
                loginStarted,
                loginCompleted,
                loginId,
                session
            ).ConfigureAwait(false);
            throw new CodexProfileOperationException(GetAddFailureMessage(failure), cleanupFailure ?? failure);
        } finally {
            if (session is not null) {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string GetAddFailureMessage(Exception failure) {
        return failure switch {
            FileNotFoundException or InvalidDataException or Win32Exception
                => "找不到或無法啟動 Codex，帳號未保存，請確認已安裝 Codex。",
            TimeoutException => "等待 Codex 回應逾時，帳號未保存，請重試。",
            _ => "無法完成 Codex 登入，帳號未保存，請重試。"
        };
    }

    /// <summary>
    /// 更新帳號別名並遞增資料版本。
    /// </summary>
    /// <param name="accountId">帳號識別碼。</param>
    /// <param name="alias">新別名；空白值會清除別名。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>更新後的帳號資料。</returns>
    /// <exception cref="CodexProfileOperationException">找不到帳號或保存失敗。</exception>
    public async Task<CodexAccountProfile> UpdateAliasAsync(
        Guid accountId,
        string? alias,
        CancellationToken cancellationToken
    ) {
        await using IOperationLease operationLease = await operationCoordinator
            .EnterProfileOrQuotaOperationAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        using CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationLease.CancellationToken);
        cancellationToken = operationCancellation.Token;
        IAccountCatalogRepository.Snapshot catalog = await catalogRepository
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        int index = FindAccountIndex(catalog.Accounts, accountId);
        if (index < 0) {
            throw new CodexProfileOperationException("找不到要修改的 Codex 帳號。", null);
        }

        CodexAccountProfile updated = catalog.Accounts[index] with {
            Alias = NormalizeAlias(alias),
            ProfileRevision = checked(catalog.Accounts[index].ProfileRevision + 1)
        };
        CodexAccountProfile[] accounts = catalog.Accounts.ToArray();
        accounts[index] = updated;
        IAccountCatalogRepository.SaveResult save = await catalogRepository
            .SaveAsync(accounts, catalog.Sha256, cancellationToken)
            .ConfigureAwait(false);
        if (!save.Saved) {
            throw new CodexProfileOperationException(
                save.Conflict
                    ? "Codex 帳號清單已在其他位置更新。請重新載入後重試。"
                    : "無法保存 Codex 帳號別名。請重試。",
                null
            );
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    /// <summary>
    /// 將已保存的 Codex 帳號在清單中移動一個位置。
    /// </summary>
    /// <param name="accountId">要移動的帳號識別碼。</param>
    /// <param name="direction">移動方向。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>新順序保存後完成的工作。</returns>
    /// <exception cref="ArgumentOutOfRangeException">移動方向不是支援的值時。</exception>
    /// <exception cref="CodexProfileOperationException">找不到帳號或無法保存清單時。</exception>
    public async Task MoveAsync(
        Guid accountId,
        CodexAccountMoveDirection direction,
        CancellationToken cancellationToken
    ) {
        if (!Enum.IsDefined(direction)) {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        await using IOperationLease operationLease = await operationCoordinator
            .EnterProfileOrQuotaOperationAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        using CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            operationLease.CancellationToken
        );
        CancellationToken operationToken = operationCancellation.Token;
        IAccountCatalogRepository.Snapshot catalog = await catalogRepository
            .LoadAsync(operationToken)
            .ConfigureAwait(false);
        int sourceIndex = FindAccountIndex(catalog.Accounts, accountId);
        if (sourceIndex < 0) {
            throw new CodexProfileOperationException("找不到要移動的 Codex 帳號；清單沒有變更，請重新載入。", null);
        }

        int targetIndex = direction == CodexAccountMoveDirection.Up ? sourceIndex - 1 : sourceIndex + 1;
        if (targetIndex < 0 || targetIndex >= catalog.Accounts.Count) {
            return;
        }

        CodexAccountProfile[] reorderedAccounts = catalog.Accounts.ToArray();
        (reorderedAccounts[sourceIndex], reorderedAccounts[targetIndex]) =
            (reorderedAccounts[targetIndex], reorderedAccounts[sourceIndex]);
        IAccountCatalogRepository.SaveResult save = await catalogRepository
            .SaveAsync(reorderedAccounts, catalog.Sha256, operationToken)
            .ConfigureAwait(false);
        if (!save.Saved || save.Snapshot is null) {
            throw new CodexProfileOperationException(
                save.Conflict
                    ? "Codex 帳號清單已在其他位置更新。未保存排序，已保留最新清單，請重試。"
                    : "無法保存 Codex 帳號排序；清單沒有變更，請重試。",
                null
            );
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 清除帳號的登入資料與隔離目錄，並從目錄移除帳號。
    /// </summary>
    /// <param name="accountId">帳號識別碼。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>表示移除作業的工作。</returns>
    /// <exception cref="CodexProfileOperationException">無法安全清理資料或保存移除結果。</exception>
    public async Task RemoveAsync(Guid accountId, CancellationToken cancellationToken) {
        quotaService?.CancelRefresh(accountId);
        await using IOperationLease operationLease = await operationCoordinator
            .EnterProfileOrQuotaOperationAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        using CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationLease.CancellationToken);
        cancellationToken = operationCancellation.Token;
        IAccountCatalogRepository.Snapshot catalog = await catalogRepository
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        CodexAccountProfile? profile = catalog.Accounts.FirstOrDefault(account => account.AccountId == accountId);
        CodexProfileTransaction? pendingRemoval = await profileStorage
            .LoadPendingRemovalTransactionAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null) {
            if (pendingRemoval is null) {
                return;
            }

            if (pendingRemoval.Stage != "home-removed") {
                throw new CodexProfileOperationException(
                    "帳號未移除，請重新載入後重試。",
                    null
                );
            }

            await profileStorage.DeleteTransactionAsync(pendingRemoval.TransactionId, cancellationToken)
                .ConfigureAwait(false);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        CodexProfileTransaction transaction = pendingRemoval ?? CreateTransaction(
            Guid.NewGuid(),
            accountId,
            "remove",
            "started",
            catalog.Sha256
        );
        if (pendingRemoval is null || !string.Equals(transaction.ExpectedCatalogSha256, catalog.Sha256, StringComparison.Ordinal)) {
            transaction = transaction with { ExpectedCatalogSha256 = catalog.Sha256, UpdatedAtUtc = clock.UtcNow };
            await profileStorage.SaveTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        }

        bool keyringClearConfirmed = transaction.Stage is "keyring-cleared" or "home-removed" or "catalog-conflict";
        bool homeRemoved = false;
        try {
            if (!keyringClearConfirmed) {
                await using ICodexAppServerSession session = await sessionFactory
                    .CreateAsync(profile.AccountId, profile.ProfileRevision, cancellationToken)
                    .ConfigureAwait(false);
                await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await session.LogoutAsync(cancellationToken).ConfigureAwait(false);
                if (await profileStorage.RemoveUnexpectedAuthFileAsync(accountId, cancellationToken).ConfigureAwait(false)) {
                    throw new CodexProfileOperationException(
                        "登入資料處理失敗，帳號未移除，請重試。",
                        null
                    );
                }

                transaction = await SaveStageAsync(transaction, "keyring-cleared", cancellationToken)
                    .ConfigureAwait(false);
                keyringClearConfirmed = true;
            }

            await profileStorage.RemoveHomeAsync(accountId, cancellationToken).ConfigureAwait(false);
            homeRemoved = true;
            transaction = await SaveStageAsync(transaction, "home-removed", CancellationToken.None)
                .ConfigureAwait(false);

            CodexAccountProfile[] remaining = catalog.Accounts
                .Where(account => account.AccountId != accountId)
                .ToArray();
            IAccountCatalogRepository.SaveResult save = await catalogRepository
                .SaveAsync(remaining, catalog.Sha256, CancellationToken.None)
                .ConfigureAwait(false);
            if (!save.Saved) {
                transaction = transaction with {
                    Stage = "catalog-conflict",
                    CleanupResult = "home-removed-catalog-preserved",
                    UpdatedAtUtc = clock.UtcNow
                };
                await profileStorage.SaveTransactionAsync(transaction, CancellationToken.None).ConfigureAwait(false);
                throw new CodexProfileOperationException(
                    "帳號未移除，帳號清單有外部更新，請重新載入後重試。",
                    null
                );
            }

            await profileStorage.DeleteTransactionAsync(transaction.TransactionId, CancellationToken.None).ConfigureAwait(false);
            StateChanged?.Invoke(this, EventArgs.Empty);
        } catch (Exception failure) {

            if (!homeRemoved && !keyringClearConfirmed) {
                transaction = transaction with {
                    Stage = "cleanup-pending",
                    CleanupResult = "profile-retained",
                    UpdatedAtUtc = clock.UtcNow
                };
            }

            try {
                await profileStorage.SaveTransactionAsync(transaction, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception journalFailure) {
                if (failure is CodexProfileOperationException) {
                    throw new CodexProfileOperationException(
                        failure.Message,
                        new AggregateException(failure, journalFailure)
                    );
                }

                throw new CodexProfileOperationException(
                    "帳號未移除，請重新載入後重試。",
                    new AggregateException(failure, journalFailure)
                );
            }

            if (failure is CodexProfileOperationException) {
                throw;
            }

            throw new CodexProfileOperationException("帳號未移除，請重新載入後重試。", failure);
        }
    }

    private async Task<Exception?> CleanupFailedAddAsync(
        Guid accountId,
        CodexProfileTransaction transaction,
        bool homeCreationAttempted,
        bool loginStarted,
        bool loginCompleted,
        string? loginId,
        ICodexAppServerSession? session
    ) {
        try {
            using CancellationTokenSource timeout = new(CleanupTimeout);
            if (session is not null) {
                if (loginStarted && !loginCompleted && loginId is not null) {
                    _ = await session.CancelChatGptLoginAsync(loginId, timeout.Token).ConfigureAwait(false);
                }

                await session.LogoutAsync(timeout.Token).ConfigureAwait(false);
            }

            transaction = await SaveStageAsync(transaction, "keyring-cleared", CancellationToken.None)
                .ConfigureAwait(false);
            if (homeCreationAttempted) {
                _ = await profileStorage.RemoveUnexpectedAuthFileAsync(accountId, timeout.Token).ConfigureAwait(false);
                await profileStorage.RemoveHomeAsync(accountId, timeout.Token).ConfigureAwait(false);
            }

            transaction = await SaveStageAsync(transaction, "home-removed", CancellationToken.None)
                .ConfigureAwait(false);
            await profileStorage.DeleteTransactionAsync(transaction.TransactionId, timeout.Token).ConfigureAwait(false);
            return null;
        } catch (Exception cleanupFailure) {
            if (transaction.Stage is not ("keyring-cleared" or "home-removed")) {
                CodexProfileTransaction pending = transaction with {
                    Stage = session is null ? transaction.Stage : "cleanup-pending",
                    CleanupResult = cleanupFailure.GetType().Name,
                    UpdatedAtUtc = clock.UtcNow
                };
                try {
                    await profileStorage.SaveTransactionAsync(pending, CancellationToken.None).ConfigureAwait(false);
                } catch (Exception journalFailure) {
                    return new AggregateException("Profile cleanup and journal update failed.", cleanupFailure, journalFailure);
                }
            }

            return cleanupFailure;
        }
    }

    private async Task RetryPendingAddCleanupsAsync(CancellationToken cancellationToken) {
        IReadOnlyList<CodexProfileTransaction> pendingTransactions = await profileStorage
            .LoadPendingAddCleanupTransactionsAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (CodexProfileTransaction pending in pendingTransactions) {
            await using IOperationLease cleanupLease = await operationCoordinator
                .EnterProfileOrQuotaOperationAsync(pending.AccountId, cancellationToken)
                .ConfigureAwait(false);
            using CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                cleanupLease.CancellationToken
            );
            CancellationToken operationToken = operationCancellation.Token;
            IReadOnlyList<CodexProfileTransaction> currentTransactions = await profileStorage
                .LoadPendingAddCleanupTransactionsAsync(operationToken)
                .ConfigureAwait(false);
            CodexProfileTransaction? current = currentTransactions.FirstOrDefault(
                transaction => transaction.TransactionId == pending.TransactionId
            );
            if (current is null) {
                continue;
            }

            IAccountCatalogRepository.Snapshot catalog = await catalogRepository
                .LoadAsync(operationToken)
                .ConfigureAwait(false);
            try {
                bool catalogContainsAccount = catalog.Accounts.Any(profile => profile.AccountId == current.AccountId);
                if (catalogContainsAccount) {
                    if (current.Stage != "catalog-committed") {
                        throw new CodexProfileOperationException(
                            "帳號未保存，請重新載入後重試。",
                            null
                        );
                    }

                    await profileStorage.DeleteTransactionAsync(current.TransactionId, operationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (current.Stage == "catalog-committed") {
                    throw new CodexProfileOperationException(
                        "帳號未保存，請重新載入後重試。",
                        null
                    );
                }

                await RetryPendingAddCleanupAsync(current, operationToken).ConfigureAwait(false);
            } catch (OperationCanceledException) when (operationToken.IsCancellationRequested) {
                throw;
            } catch (Exception failure) {
                throw new CodexProfileOperationException(
                    "帳號未保存，請重新載入後重試。",
                    failure
                );
            }
        }
    }

    private async Task RetryPendingAddCleanupAsync(
        CodexProfileTransaction transaction,
        CancellationToken cancellationToken
    ) {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CleanupTimeout);
        CancellationToken cleanupToken = timeout.Token;
        ICodexAppServerSession? session = null;
        try {
            if (transaction.Stage is "home-created" or "identity-confirmed" or "cleanup-pending") {
                session = await sessionFactory.CreateAsync(transaction.AccountId, 1, cleanupToken).ConfigureAwait(false);
                await session.InitializeAsync(cleanupToken).ConfigureAwait(false);
                await session.LogoutAsync(cleanupToken).ConfigureAwait(false);
                transaction = await SaveStageAsync(transaction, "keyring-cleared", cleanupToken)
                    .ConfigureAwait(false);
            } else if (transaction.Stage == "started") {
                transaction = await SaveStageAsync(transaction, "keyring-cleared", cleanupToken)
                    .ConfigureAwait(false);
            }

            if (transaction.Stage == "keyring-cleared") {
                _ = await profileStorage.RemoveUnexpectedAuthFileAsync(transaction.AccountId, cleanupToken)
                    .ConfigureAwait(false);
                await profileStorage.RemoveHomeAsync(transaction.AccountId, cleanupToken).ConfigureAwait(false);
                transaction = await SaveStageAsync(transaction, "home-removed", cleanupToken)
                    .ConfigureAwait(false);
            }

            if (transaction.Stage != "home-removed") {
                throw new InvalidDataException("The add-profile cleanup journal has an unsupported recovery stage.");
            }

            await profileStorage.DeleteTransactionAsync(transaction.TransactionId, cleanupToken).ConfigureAwait(false);
        } finally {
            if (session is not null) {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<CodexProfileTransaction> SaveStageAsync(
        CodexProfileTransaction transaction,
        string stage,
        CancellationToken cancellationToken
    ) {
        CodexProfileTransaction updated = transaction with {
            Stage = stage,
            CleanupResult = null,
            UpdatedAtUtc = clock.UtcNow
        };
        await profileStorage.SaveTransactionAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private CodexProfileTransaction CreateTransaction(
        Guid transactionId,
        Guid accountId,
        string operation,
        string stage,
        string expectedCatalogSha256
    ) {
        return new CodexProfileTransaction(
            transactionId,
            accountId,
            operation,
            stage,
            expectedCatalogSha256,
            clock.UtcNow,
            null
        );
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null) {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String) {
            throw new CodexProfileOperationException("Codex 回傳的帳號識別格式無效。", null);
        }

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? NormalizeAlias(string? alias) {
        return string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
    }

    private static int FindAccountIndex(IReadOnlyList<CodexAccountProfile> accounts, Guid accountId) {
        for (int index = 0; index < accounts.Count; index++) {
            if (accounts[index].AccountId == accountId) {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>
/// 表示 Codex 帳號作業失敗。
/// </summary>
public sealed class CodexProfileOperationException : Exception {
    /// <summary>
    /// 初始化帳號作業例外。
    /// </summary>
    /// <param name="message">錯誤訊息。</param>
    /// <param name="innerException">造成失敗的內部例外。</param>
    public CodexProfileOperationException(string message, Exception? innerException)
        : base(message, innerException) {
    }
}






