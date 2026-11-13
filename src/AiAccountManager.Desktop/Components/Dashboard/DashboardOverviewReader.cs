using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Recommendations;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Application.Notifications;

namespace CloudyWing.AiAccountManager.Desktop.Components.Dashboard;

/// <summary>
/// 將既有 Desktop 身分與保存額度轉成安全的儀表板資料。
/// </summary>
public sealed class DashboardOverviewReader : IDashboardOverviewReader {
    private const string CodexSourceLabel = "Codex Desktop 身分與已保存額度";
    private const string ClaudeSourceLabel = "Claude Desktop 身分與已保存額度";
    private const string FreePlanLabel = "免費方案";
    private readonly CodexLocalIdentityService codexIdentity;
    private readonly CodexQuotaService codexQuota;
    private readonly ClaudeAccountService claudeAccounts;
    private readonly ClaudeQuotaService claudeQuota;
    private readonly IAccountCatalogRepository? catalog;
    private readonly SettingsService? settings;
    private readonly AccountRecommendationService recommendations = new();

    /// <summary>
    /// 建立唯讀的儀表板資料讀取器。
    /// </summary>
    /// <param name="codexIdentity">Codex Desktop 身分讀取服務。</param>
    /// <param name="codexQuota">Codex 已保存額度讀取服務。</param>
    /// <param name="claudeAccounts">Claude Desktop 身分與 profile 讀取服務。</param>
    /// <param name="claudeQuota">Claude 已保存額度讀取服務。</param>
    /// <param name="catalog">Codex 帳號清單儲存庫。</param>
    /// <param name="settings">額度刷新間隔設定。</param>
    public DashboardOverviewReader(
        CodexLocalIdentityService codexIdentity,
        CodexQuotaService codexQuota,
        ClaudeAccountService claudeAccounts,
        ClaudeQuotaService claudeQuota,
        IAccountCatalogRepository? catalog = null,
        SettingsService? settings = null
    ) {
        this.codexIdentity = codexIdentity;
        this.codexQuota = codexQuota;
        this.claudeAccounts = claudeAccounts;
        this.claudeQuota = claudeQuota;
        this.catalog = catalog;
        this.settings = settings;
    }

    /// <summary>
    /// 依已保存的額度資料取得 Codex 帳號建議。
    /// </summary>
    /// <param name="cancellationToken">取消讀取的權杖。</param>
    /// <returns>符合條件的安全帳號建議。</returns>
    public async Task<RecommendationAccount?> ReadCodexRecommendationAsync(CancellationToken cancellationToken) {
        if (catalog is null) {
            return null;
        }
        CodexLocalIdentitySnapshot identity = await codexIdentity.ReadDashboardSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (identity.Status != CodexLocalIdentityStatus.Matched || identity.MatchedProfileAccountId is not Guid currentId) {
            return null;
        }

        return await ReadCodexRecommendationForAsync(currentId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RecommendationAccount?> ReadCodexRecommendationForAsync(Guid currentId, CancellationToken cancellationToken) {
        if (catalog is null) {
            return null;
        }
        IAccountCatalogRepository.Snapshot snapshot = await catalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        List<RecommendationAccount> accounts = [];
        foreach (CodexAccountProfile profile in snapshot.Accounts) {
            QuotaSnapshotView view = await codexQuota.GetViewAsync(profile.AccountId, cancellationToken).ConfigureAwait(false);
            QuotaSuccessfulSnapshot? quota = view.Snapshot?.LastSuccessfulSnapshot;
            string name = !string.IsNullOrWhiteSpace(profile.Alias) && !profile.Alias.Contains('@')
                ? profile.Alias : profile.MaskedIdentity;
            accounts.Add(new(profile.AccountId, name, quota?.FiveHour?.RemainingPercent, quota?.Weekly?.RemainingPercent,
                quota?.FiveHour?.ResetsAtUtc, view.AccountId == profile.AccountId
                    && view.Snapshot?.AccountId == profile.AccountId
                    && view.Status == QuotaDisplayStatus.Fresh && !view.IsStale));
        }
        return recommendations.Recommend(currentId, accounts);
    }

    /// <summary>
    /// 依已保存的額度資料取得 Claude 帳號建議。
    /// </summary>
    /// <param name="cancellationToken">取消讀取的權杖。</param>
    /// <returns>符合條件的安全帳號建議。</returns>
    public async Task<RecommendationAccount?> ReadClaudeRecommendationAsync(CancellationToken cancellationToken) {
        ClaudeAccountService.PageState state = await claudeAccounts.GetPageStateAsync(cancellationToken).ConfigureAwait(false);
        if (state.RecoveryRequired || state.CurrentIdentity.Status != ClaudeAccountService.IdentityStatus.Verified
            || state.CurrentIdentity.MatchedProfile is not ClaudeAccountProfile current) {
            return null;
        }

        return await ReadClaudeRecommendationForAsync(state, current.ProfileId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RecommendationAccount?> ReadClaudeRecommendationForAsync(
        ClaudeAccountService.PageState state, Guid currentId, CancellationToken cancellationToken
    ) {
        List<RecommendationAccount> accounts = [];
        foreach (ClaudeAccountProfile profile in state.Profiles) {
            ClaudeQuotaSnapshot.View view = await claudeQuota.GetViewAsync(profile.ProfileId, cancellationToken).ConfigureAwait(false);
            ClaudeQuotaSnapshot.Window? session = view.Snapshot?.Windows.FirstOrDefault(window => window.Key == "session");
            ClaudeQuotaSnapshot.Window? weekly = view.Snapshot?.Windows.FirstOrDefault(window => window.Key == "weekly_all");
            string name = DisplayableClaudeIdentity(profile, null) ?? "未知帳號";
            accounts.Add(new(profile.ProfileId, name, session is null ? null : 100m - session.UtilizationPercent,
                weekly is null ? null : 100m - weekly.UtilizationPercent, session?.ResetsAtUtc,
                profile.Status == ClaudeAccountProfile.VerificationStatus.Verified
                    && view.ProfileId == profile.ProfileId && view.Snapshot?.ProfileId == profile.ProfileId
                    && view.Status == ClaudeQuotaSnapshot.RefreshStatus.Fresh
                    && view.Snapshot?.RetrievedAtUtc >= DateTimeOffset.UtcNow.AddMinutes(
                        -(settings?.Settings?.QuotaRefreshIntervalMinutes ?? AppSettings.Defaults.QuotaRefreshIntervalMinutes)
                    )));
        }
        return recommendations.Recommend(currentId, accounts);
    }

    /// <summary>讀取目前 Codex 帳號的通知資料。</summary>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>有效的通知快照；無法確認時為 null。</returns>
    public async Task<NotificationSnapshot?> ReadCodexNotificationAsync(CancellationToken cancellationToken) {
        CodexLocalIdentitySnapshot identity = await codexIdentity.ReadDashboardSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (identity.Status != CodexLocalIdentityStatus.Matched || identity.MatchedProfileAccountId is not Guid accountId) {
            return null;
        }
        QuotaSnapshotView view = await codexQuota.GetViewAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (view.AccountId != accountId || view.Snapshot?.AccountId != accountId
            || view.Status != QuotaDisplayStatus.Fresh || view.IsStale || view.Snapshot?.LastSuccessfulSnapshot is not QuotaSuccessfulSnapshot quota) {
            return null;
        }
        if (quota.FiveHour is null || quota.Weekly is null) {
            return null;
        }
        RecommendationAccount? recommendation = null;
        try {
            recommendation = await ReadCodexRecommendationForAsync(accountId, cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            recommendation = null;
        }
        return new("codex", accountId, recommendation?.DisplayName, [
            new("five-hour", "5 小時", quota.FiveHour.RemainingPercent, quota.FiveHour.ResetsAtUtc),
            new("weekly", "每週", quota.Weekly.RemainingPercent, quota.Weekly.ResetsAtUtc)
        ]);
    }

    /// <summary>讀取目前 Claude 帳號的通知資料。</summary>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>有效的通知快照；無法確認時為 null。</returns>
    public async Task<NotificationSnapshot?> ReadClaudeNotificationAsync(CancellationToken cancellationToken) {
        ClaudeAccountService.PageState state = await claudeAccounts.GetPageStateAsync(cancellationToken).ConfigureAwait(false);
        if (state.RecoveryRequired || state.CurrentIdentity.Status != ClaudeAccountService.IdentityStatus.Verified
            || state.CurrentIdentity.MatchedProfile is not ClaudeAccountProfile profile) {
            return null;
        }
        ClaudeQuotaSnapshot.View view = await claudeQuota.GetViewAsync(profile.ProfileId, cancellationToken).ConfigureAwait(false);
        int interval = settings?.Settings?.QuotaRefreshIntervalMinutes ?? AppSettings.Defaults.QuotaRefreshIntervalMinutes;
        if (view.ProfileId != profile.ProfileId || view.Status != ClaudeQuotaSnapshot.RefreshStatus.Fresh
            || view.Snapshot is not ClaudeQuotaSnapshot quota || quota.ProfileId != profile.ProfileId
            || quota.RetrievedAtUtc < DateTimeOffset.UtcNow.AddMinutes(-interval)) {
            return null;
        }
        List<NotificationWindow> windows = [];
        foreach (ClaudeQuotaSnapshot.Window window in quota.Windows) {
            if (window.Key == "session") {
                windows.Add(new("five-hour", "5 小時", 100m - window.UtilizationPercent, window.ResetsAtUtc));
            } else if (window.Key == "weekly_all") {
                windows.Add(new("weekly", "每週", 100m - window.UtilizationPercent, window.ResetsAtUtc));
            }
        }
        RecommendationAccount? recommendation = null;
        try {
            recommendation = await ReadClaudeRecommendationForAsync(state, profile.ProfileId, cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            recommendation = null;
        }
        return new("claude", profile.ProfileId, recommendation?.DisplayName, windows);
    }

    /// <inheritdoc />
    public async Task<DashboardOverviewSnapshot.Provider> ReadCodexAsync(CancellationToken cancellationToken) {
        try {
            CodexLocalIdentitySnapshot identity = await codexIdentity
                .ReadDashboardSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
            if (identity.Status != CodexLocalIdentityStatus.Matched
                || identity.MatchedProfileAccountId is not Guid accountId) {
                DashboardOverviewSnapshot.IdentityStatus identityStatus = identity.Status == CodexLocalIdentityStatus.Matched
                    ? DashboardOverviewSnapshot.IdentityStatus.Error
                    : MapCodexIdentityStatus(identity.Status);
                string? identityMessage = identity.Status == CodexLocalIdentityStatus.Matched
                    ? "無法確認目前登入的 Codex 帳號，未讀取額度。"
                    : CodexIdentityMessage(identity.Status);
                return CreateProvider(
                    DashboardOverviewSnapshot.ProviderKind.Codex,
                    identityStatus,
                    DashboardOverviewSnapshot.QuotaStatus.Unknown,
                    identity.MaskedIdentity,
                    CodexSourceLabel,
                    identityMessage,
                    null,
                    identity.CheckedAtUtc,
                    null,
                    [],
                    false
                );
            }

            QuotaSnapshotView view = await codexQuota
                .GetViewAsync(accountId, cancellationToken)
                .ConfigureAwait(false);
            if (view.AccountId != accountId
                || (view.Snapshot is QuotaSnapshotRecord snapshot && snapshot.AccountId != accountId)) {
                return CreateProvider(
                    DashboardOverviewSnapshot.ProviderKind.Codex,
                    DashboardOverviewSnapshot.IdentityStatus.Matched,
                    DashboardOverviewSnapshot.QuotaStatus.Error,
                    identity.MaskedIdentity,
                    CodexSourceLabel,
                    null,
                    "額度資料與目前登入的帳號不符，請重新整理額度。",
                    identity.CheckedAtUtc,
                    null,
                    [],
                    false
                );
            }

            QuotaSuccessfulSnapshot? success = view.Snapshot?.LastSuccessfulSnapshot;
            bool hasInvalidValue = success is not null
                && (IsInvalidPercent(success.FiveHour)
                    || IsInvalidPercent(success.Weekly)
                    || IsInvalidPercent(success.FreePlanWindow));
            List<DashboardOverviewSnapshot.QuotaWindow> windows = [];
            if (success is not null) {
                AddCodexWindow(windows, "codex-five-hour", success.FiveHour);
                AddCodexWindow(windows, "codex-weekly", success.Weekly);
                AddCodexWindow(windows, "codex-free-plan", success.FreePlanWindow);
            }
            DashboardOverviewSnapshot.QuotaStatus quotaStatus = hasInvalidValue
                ? DashboardOverviewSnapshot.QuotaStatus.Incomplete
                : MapCodexQuotaStatus(view.Status);
            string? quotaMessage = hasInvalidValue
                ? "部分額度資料不完整，無法顯示。"
                : view.Message;

            return CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Codex,
                DashboardOverviewSnapshot.IdentityStatus.Matched,
                quotaStatus,
                identity.MaskedIdentity,
                CodexSourceLabel,
                null,
                quotaMessage,
                identity.CheckedAtUtc,
                success?.RetrievedAtUtc,
                windows,
                view.IsStale,
                success?.IsFreePlan == true ? FreePlanLabel : null
            );
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Codex,
                DashboardOverviewSnapshot.IdentityStatus.Error,
                DashboardOverviewSnapshot.QuotaStatus.Unknown,
                null,
                CodexSourceLabel,
                "無法讀取 Codex Desktop 身分或已保存額度。",
                null,
                null,
                null,
                [],
                false
            );
        }
    }

    /// <inheritdoc />
    public async Task<DashboardOverviewSnapshot.Provider> ReadClaudeAsync(CancellationToken cancellationToken) {
        try {
            ClaudeAccountService.PageState pageState = await claudeAccounts
                .GetPageStateAsync(cancellationToken)
                .ConfigureAwait(false);
            ClaudeAccountService.CurrentIdentity identity = pageState.CurrentIdentity;
            ClaudeAccountProfile? profile = identity.MatchedProfile;
            DashboardOverviewSnapshot.IdentityStatus identityStatus = MapClaudeIdentityStatus(
                identity.Status,
                profile is not null
            );
            string? maskedIdentity = profile is null
                ? identity.MaskedEmail
                : DisplayableClaudeIdentity(profile, identity.MaskedEmail);
            if (pageState.RecoveryRequired || identityStatus != DashboardOverviewSnapshot.IdentityStatus.Matched || profile is null) {
                if (pageState.RecoveryRequired) {
                    identityStatus = DashboardOverviewSnapshot.IdentityStatus.RecoveryRequired;
                }

                string? identityMessage = pageState.RecoveryRequired
                    ? "Claude Desktop 有尚未完成的切換復原，完成復原前不讀取額度。"
                    : identity.Message ?? "目前登入的帳號未對應已保存帳號，請前往管理頁確認。";
                return CreateProvider(
                    DashboardOverviewSnapshot.ProviderKind.Claude,
                    identityStatus,
                    DashboardOverviewSnapshot.QuotaStatus.Unknown,
                    maskedIdentity,
                    ClaudeSourceLabel,
                    identityMessage,
                    null,
                    identity.VerifiedAtUtc,
                    null,
                    [],
                    false
                );
            }

            ClaudeQuotaSnapshot.View view = await claudeQuota
                .GetViewAsync(profile.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (view.ProfileId != profile.ProfileId
                || (view.Snapshot is ClaudeQuotaSnapshot snapshot && snapshot.ProfileId != profile.ProfileId)) {
                return CreateProvider(
                    DashboardOverviewSnapshot.ProviderKind.Claude,
                    DashboardOverviewSnapshot.IdentityStatus.Matched,
                    DashboardOverviewSnapshot.QuotaStatus.Error,
                    maskedIdentity,
                    ClaudeSourceLabel,
                    null,
                    "額度資料與目前登入的帳號不符，請重新整理額度。",
                    identity.VerifiedAtUtc,
                    null,
                    [],
                    false
                );
            }

            ClaudeQuotaSnapshot? success = view.Snapshot;
            List<DashboardOverviewSnapshot.QuotaWindow> windows = [];
            bool hasInvalidValue = false;
            if (success is not null) {
                foreach (ClaudeQuotaSnapshot.Window window in success.Windows) {
                    bool isValid = window.UtilizationPercent is >= 0m and <= 100m;
                    hasInvalidValue |= !isValid;
                    (string label, string? scope) = DescribeClaudeWindow(window.Key);
                    windows.Add(new(
                        window.Key,
                        label,
                        scope,
                        isValid ? 100m - window.UtilizationPercent : null,
                        window.ResetsAtUtc
                    ));
                }

                hasInvalidValue |= success.Windows.Count == 0;
            }

            DashboardOverviewSnapshot.QuotaStatus quotaStatus = hasInvalidValue
                ? DashboardOverviewSnapshot.QuotaStatus.Incomplete
                : MapClaudeQuotaStatus(view.Status);
            string? quotaMessage = hasInvalidValue
                ? "部分額度資料不完整，無法顯示。"
                : view.Message;

            return CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Claude,
                DashboardOverviewSnapshot.IdentityStatus.Matched,
                quotaStatus,
                maskedIdentity,
                ClaudeSourceLabel,
                identity.Message,
                quotaMessage,
                identity.VerifiedAtUtc,
                success?.RetrievedAtUtc,
                windows,
                view.Status == ClaudeQuotaSnapshot.RefreshStatus.Stale
            );
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Claude,
                DashboardOverviewSnapshot.IdentityStatus.Error,
                DashboardOverviewSnapshot.QuotaStatus.Unknown,
                null,
                ClaudeSourceLabel,
                "無法讀取 Claude Desktop 身分或已保存額度。",
                null,
                null,
                null,
                [],
                false
            );
        }
    }

    private static DashboardOverviewSnapshot.Provider CreateProvider(
        DashboardOverviewSnapshot.ProviderKind kind,
        DashboardOverviewSnapshot.IdentityStatus identityStatus,
        DashboardOverviewSnapshot.QuotaStatus quotaStatus,
        string? maskedIdentity,
        string sourceLabel,
        string? identityMessage,
        string? quotaMessage,
        DateTimeOffset? checkedAtUtc,
        DateTimeOffset? retrievedAtUtc,
        IReadOnlyList<DashboardOverviewSnapshot.QuotaWindow> windows,
        bool isStale,
        string? planLabel = null
    ) {
        return new(
            kind,
            identityStatus,
            quotaStatus,
            maskedIdentity,
            sourceLabel,
            identityMessage,
            quotaMessage,
            checkedAtUtc,
            retrievedAtUtc,
            windows,
            isStale,
            planLabel
        );
    }

    private static DashboardOverviewSnapshot.IdentityStatus MapCodexIdentityStatus(CodexLocalIdentityStatus status) {
        return status switch {
            CodexLocalIdentityStatus.Matched => DashboardOverviewSnapshot.IdentityStatus.Matched,
            CodexLocalIdentityStatus.Unmatched => DashboardOverviewSnapshot.IdentityStatus.Unmatched,
            CodexLocalIdentityStatus.Ambiguous => DashboardOverviewSnapshot.IdentityStatus.Ambiguous,
            CodexLocalIdentityStatus.UnsupportedAuth => DashboardOverviewSnapshot.IdentityStatus.Unsupported,
            CodexLocalIdentityStatus.FileMissing
                or CodexLocalIdentityStatus.IdentityUnavailable
                or CodexLocalIdentityStatus.NotChecked
                or CodexLocalIdentityStatus.Loading => DashboardOverviewSnapshot.IdentityStatus.Unknown,
            _ => DashboardOverviewSnapshot.IdentityStatus.Error
        };
    }

    private static string? CodexIdentityMessage(CodexLocalIdentityStatus status) {
        return status switch {
            CodexLocalIdentityStatus.Matched => null,
            CodexLocalIdentityStatus.Unmatched => "目前 Codex Desktop 身分未對應已保存帳號，請前往 Codex 管理頁確認。",
            CodexLocalIdentityStatus.Ambiguous => "目前 Codex Desktop 身分對應到多筆已保存帳號，請前往 Codex 管理頁確認。",
            CodexLocalIdentityStatus.FileMissing => "找不到 Codex Desktop 本機身分，請確認已登入並前往管理頁處理。",
            CodexLocalIdentityStatus.UnsupportedAuth => "目前 Codex 登入方式不受支援，請前往 Codex 管理頁確認。",
            CodexLocalIdentityStatus.CatalogUnavailable => "無法讀取已保存的 Codex 帳號清單。",
            _ => "無法確認 Codex Desktop 身分，請前往 Codex 管理頁查看。"
        };
    }

    private static DashboardOverviewSnapshot.QuotaStatus MapCodexQuotaStatus(QuotaDisplayStatus status) {
        return status switch {
            QuotaDisplayStatus.Refreshing => DashboardOverviewSnapshot.QuotaStatus.Refreshing,
            QuotaDisplayStatus.Fresh => DashboardOverviewSnapshot.QuotaStatus.Fresh,
            QuotaDisplayStatus.Stale => DashboardOverviewSnapshot.QuotaStatus.Stale,
            QuotaDisplayStatus.ReauthenticationRequired => DashboardOverviewSnapshot.QuotaStatus.ReauthenticationRequired,
            QuotaDisplayStatus.Error => DashboardOverviewSnapshot.QuotaStatus.Error,
            QuotaDisplayStatus.Incomplete => DashboardOverviewSnapshot.QuotaStatus.Incomplete,
            _ => DashboardOverviewSnapshot.QuotaStatus.Unknown
        };
    }

    private static DashboardOverviewSnapshot.IdentityStatus MapClaudeIdentityStatus(
        ClaudeAccountService.IdentityStatus status,
        bool hasMatchedProfile
    ) {
        return status switch {
            ClaudeAccountService.IdentityStatus.Verified when hasMatchedProfile
                => DashboardOverviewSnapshot.IdentityStatus.Matched,
            ClaudeAccountService.IdentityStatus.Verified => DashboardOverviewSnapshot.IdentityStatus.Unmatched,
            ClaudeAccountService.IdentityStatus.Mismatch => DashboardOverviewSnapshot.IdentityStatus.Unmatched,
            ClaudeAccountService.IdentityStatus.ReauthenticationRequired
                => DashboardOverviewSnapshot.IdentityStatus.ReauthenticationRequired,
            ClaudeAccountService.IdentityStatus.RecoveryRequired
                => DashboardOverviewSnapshot.IdentityStatus.RecoveryRequired,
            ClaudeAccountService.IdentityStatus.UnsupportedInstallation
                => DashboardOverviewSnapshot.IdentityStatus.Unsupported,
            ClaudeAccountService.IdentityStatus.AmbiguousInstallation
                => DashboardOverviewSnapshot.IdentityStatus.Ambiguous,
            _ => DashboardOverviewSnapshot.IdentityStatus.Unknown
        };
    }

    private static DashboardOverviewSnapshot.QuotaStatus MapClaudeQuotaStatus(
        ClaudeQuotaSnapshot.RefreshStatus status
    ) {
        return status switch {
            ClaudeQuotaSnapshot.RefreshStatus.Refreshing => DashboardOverviewSnapshot.QuotaStatus.Refreshing,
            ClaudeQuotaSnapshot.RefreshStatus.Fresh => DashboardOverviewSnapshot.QuotaStatus.Fresh,
            ClaudeQuotaSnapshot.RefreshStatus.Stale => DashboardOverviewSnapshot.QuotaStatus.Stale,
            ClaudeQuotaSnapshot.RefreshStatus.ReauthenticationRequired
                => DashboardOverviewSnapshot.QuotaStatus.ReauthenticationRequired,
            ClaudeQuotaSnapshot.RefreshStatus.Error => DashboardOverviewSnapshot.QuotaStatus.Error,
            ClaudeQuotaSnapshot.RefreshStatus.Incomplete => DashboardOverviewSnapshot.QuotaStatus.Incomplete,
            _ => DashboardOverviewSnapshot.QuotaStatus.Unknown
        };
    }

    private static bool IsInvalidPercent(QuotaWindow? window) {
        return window is not null && window.RemainingPercent is < 0 or > 100;
    }

    private static void AddCodexWindow(
        List<DashboardOverviewSnapshot.QuotaWindow> windows,
        string key,
        QuotaWindow? window
    ) {
        if (window is null) {
            return;
        }

        windows.Add(new(
            key,
            QuotaWindowLabels.ForDuration(window.DurationMinutes),
            null,
            ToRemainingPercent(window.RemainingPercent),
            window.ResetsAtUtc
        ));
    }

    private static decimal? ToRemainingPercent(int value) {
        return value is >= 0 and <= 100 ? value : null;
    }

    private static string? DisplayableClaudeIdentity(ClaudeAccountProfile profile, string? verifiedMaskedEmail) {
        return !string.IsNullOrWhiteSpace(profile.Alias) && !profile.Alias.Contains('@')
            ? profile.Alias
            : !string.IsNullOrWhiteSpace(verifiedMaskedEmail)
                ? verifiedMaskedEmail
                : profile.MaskedEmail;
    }

    private static (string Label, string? Scope) DescribeClaudeWindow(string key) {
        return key switch {
            "session" => ("工作階段", null),
            "weekly_all" => ("每週", "所有模型"),
            "weekly_opus" => ("每週", "Opus"),
            "weekly_sonnet" => ("每週", "Sonnet"),
            _ when key.StartsWith("weekly_scoped:", StringComparison.Ordinal)
                => ("每週", key["weekly_scoped:".Length..]),
            _ => (key, null)
        };
    }
}
