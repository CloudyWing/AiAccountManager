using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Runtime;

namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 依指定 Claude profile 手動驗證帳號並查詢 usage quota。
/// </summary>
public sealed class ClaudeQuotaService {
    private readonly IClaudeAccountRepository accountRepository;
    private readonly IClaudeDesktopProfileStore profileStore;
    private readonly IClaudeOAuthClient oauthClient;
    private readonly IClaudeQuotaRepository quotaRepository;
    private readonly ClaudeOperationGate operationGate;
    private readonly IClock clock;
    private readonly object stateLock = new();
    private readonly Dictionary<Guid, ClaudeQuotaSnapshot.View> currentViews = [];
    private readonly Dictionary<Guid, long> writeVersions = [];
    private readonly Dictionary<Guid, long> readSequences = [];
    private readonly Dictionary<Guid, long> publishedReadSequences = [];

    /// <summary>
    /// 建立 Claude quota 查詢服務。
    /// </summary>
    /// <param name="accountRepository">保存 Claude profiles 與 Desktop snapshots 的 repository。</param>
    /// <param name="profileStore">從已保存 snapshot 解析 Desktop token 的 adapter。</param>
    /// <param name="oauthClient">Claude OAuth profile 與 usage client。</param>
    /// <param name="quotaRepository">逐 profile 保存 quota snapshot 的 repository。</param>
    /// <param name="operationGate">Claude 作業閘門。</param>
    /// <param name="clock">目前 UTC 時間來源。</param>
    public ClaudeQuotaService(
        IClaudeAccountRepository accountRepository,
        IClaudeDesktopProfileStore profileStore,
        IClaudeOAuthClient oauthClient,
        IClaudeQuotaRepository quotaRepository,
        ClaudeOperationGate operationGate,
        IClock clock
    ) {
        this.accountRepository = accountRepository;
        this.profileStore = profileStore;
        this.oauthClient = oauthClient;
        this.quotaRepository = quotaRepository;
        this.operationGate = operationGate;
        this.clock = clock;
    }

    /// <summary>
    /// 於指定 profile 的 quota view 更新時發生。
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// 取得指定 profile 在記憶體中的最新 quota view，不進入 Claude 作業閘門。
    /// </summary>
    /// <param name="profileId">quota 所屬的 Claude profile 識別碼。</param>
    /// <returns>本次執行期間載入或查詢過的 view；尚未載入時為 <see langword="null" />。</returns>
    public ClaudeQuotaSnapshot.View? GetCurrentView(Guid profileId) {
        lock (stateLock) {
            return currentViews.TryGetValue(profileId, out ClaudeQuotaSnapshot.View? view) ? view : null;
        }
    }

    /// <summary>
    /// 載入指定 profile 最近一次保存的 quota view。此讀取不進入 Claude 作業閘門，
    /// 因此不會被進行中的網路作業阻擋；與寫入作業的競態由 profile 寫入版本檢查處理。
    /// </summary>
    /// <param name="profileId">quota 所屬的 Claude profile 識別碼。</param>
    /// <param name="cancellationToken">取消載入作業的權杖。</param>
    /// <returns>最近一次保存的額度資料。</returns>
    public async Task<ClaudeQuotaSnapshot.View> GetViewAsync(Guid profileId, CancellationToken cancellationToken) {
        long observedVersion;
        long readTicket;
        lock (stateLock) {
            if (currentViews.TryGetValue(profileId, out ClaudeQuotaSnapshot.View? current)
                && current.Status == ClaudeQuotaSnapshot.RefreshStatus.Refreshing) {
                return current;
            }

            observedVersion = writeVersions.GetValueOrDefault(profileId);
            readTicket = readSequences.GetValueOrDefault(profileId) + 1;
            readSequences[profileId] = readTicket;
        }

        IClaudeQuotaRepository.Snapshot? snapshot = await quotaRepository
            .LoadAsync(profileId, cancellationToken)
            .ConfigureAwait(false);
        ClaudeQuotaSnapshot.View view = snapshot?.View ?? new(
            profileId,
            null,
            ClaudeQuotaSnapshot.RefreshStatus.Unknown,
            null,
            "尚未查詢此 Claude 帳號的額度。"
        );
        if (view.Status == ClaudeQuotaSnapshot.RefreshStatus.Refreshing) {
            view = view with {
                Status = view.Snapshot is null
                    ? ClaudeQuotaSnapshot.RefreshStatus.Unknown
                    : ClaudeQuotaSnapshot.RefreshStatus.Stale,
                Message = "上一次額度查詢未完成，顯示的是上次成功的結果。"
            };
        }

        bool changed;
        lock (stateLock) {
            // 讀取期間若有寫入作業更新同一 profile，該結果比本次載入的快照新，不得被覆蓋。
            if (writeVersions.GetValueOrDefault(profileId) != observedVersion
                && currentViews.TryGetValue(profileId, out ClaudeQuotaSnapshot.View? latest)) {
                return latest;
            }

            // 較新的讀取已經發佈時，本次較舊的讀取結果不得蓋回。判定依據是已發佈的最大序號，
            // 不是已配發的最大序號，較新的讀取尚未完成時，較舊的讀取仍可發佈。
            if (readTicket < publishedReadSequences.GetValueOrDefault(profileId)
                && currentViews.TryGetValue(profileId, out ClaudeQuotaSnapshot.View? newerPublished)) {
                return newerPublished;
            }

            publishedReadSequences[profileId] = readTicket;
            changed = !currentViews.TryGetValue(profileId, out ClaudeQuotaSnapshot.View? previous)
                || !HasSameContent(previous, view);
            currentViews[profileId] = view;
        }

        if (changed) {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return view;
    }

    /// <summary>
    /// 對指定已保存 profile 手動執行一次 `/profile` 身分核對與 `/usage` 查詢。
    /// </summary>
    /// <param name="profileId">quota 所屬的 Claude profile 識別碼。</param>
    /// <param name="cancellationToken">取消尚未提交的查詢作業之權杖。</param>
    /// <returns>最新查詢結果；失敗時保留該 profile 最近一次成功的 snapshot。</returns>
    public async Task<ClaudeQuotaSnapshot.View> RefreshAsync(Guid profileId, CancellationToken cancellationToken) {
        await using ClaudeOperationGate.Lease lease = await operationGate
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        CancellationToken operationCancellationToken = lease.CancellationToken;
        IClaudeQuotaRepository.Snapshot? stored = await quotaRepository
            .LoadAsync(profileId, operationCancellationToken)
            .ConfigureAwait(false);
        ClaudeQuotaSnapshot? previousSnapshot = stored?.View.Snapshot;
        ClaudeQuotaSnapshot.View refreshing = new(
            profileId,
            previousSnapshot,
            ClaudeQuotaSnapshot.RefreshStatus.Refreshing,
            clock.UtcNow,
            null
        );
        SetView(refreshing);

        ClaudeQuotaSnapshot.View result;
        try {
            IClaudeAccountRepository.Snapshot catalog = await accountRepository
                .LoadProfilesAsync(operationCancellationToken)
                .ConfigureAwait(false);
            ClaudeAccountProfile? profile = catalog.Profiles.FirstOrDefault(
                candidate => candidate.ProfileId == profileId
            );
            if (profile is null) {
                result = CreateFailure(
                    profileId,
                    previousSnapshot,
                    ClaudeQuotaSnapshot.RefreshStatus.Error,
                    "找不到指定的 Claude 帳號。"
                );
                SetView(result);
                return result;
            }

            ClaudeAccountProfile.DesktopSnapshot? desktopSnapshot = await accountRepository
                .LoadDesktopSnapshotAsync(profileId, operationCancellationToken)
                .ConfigureAwait(false);
            if (desktopSnapshot is null
                || !IsProfileSnapshot(profile, desktopSnapshot)
                || string.IsNullOrWhiteSpace(desktopSnapshot.LastKnownAccountUuid)
                || !string.Equals(
                    desktopSnapshot.LastKnownAccountUuid,
                    profile.AccountUuid,
                    StringComparison.OrdinalIgnoreCase
                )) {
                result = CreateFailure(
                    profileId,
                    previousSnapshot,
                    ClaudeQuotaSnapshot.RefreshStatus.ReauthenticationRequired,
                    "此帳號的登入資料已遺失或失效，請在 Claude Desktop 重新登入並重新保存帳號。"
                );
                return await SaveResultAsync(result, stored?.Sha256, operationCancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> tokenCandidates = profileStore
                .GetTokenCandidates(desktopSnapshot);
            if (tokenCandidates.Count == 0) {
                result = CreateFailure(
                    profileId,
                    previousSnapshot,
                    ClaudeQuotaSnapshot.RefreshStatus.ReauthenticationRequired,
                    "登入已失效，請在 Claude Desktop 重新登入並重新保存帳號。"
                );
                return await SaveResultAsync(result, stored?.Sha256, operationCancellationToken).ConfigureAwait(false);
            }

            IClaudeDesktopProfileStore.TokenCandidate? verifiedToken = null;
            bool unauthorized = false;
            bool forbidden = false;
            bool rateLimited = false;
            TimeSpan? profileRetryAfter = null;
            foreach (IClaudeDesktopProfileStore.TokenCandidate tokenCandidate in tokenCandidates) {
                IClaudeOAuthClient.ProfileResult profileResult;
                try {
                    profileResult = await oauthClient
                        .GetProfileAsync(tokenCandidate.AccessToken, operationCancellationToken)
                        .ConfigureAwait(false);
                } catch (OperationCanceledException) when (operationCancellationToken.IsCancellationRequested) {
                    throw;
                } catch (Exception) {
                    continue;
                }

                switch (profileResult.Outcome) {
                    case IClaudeOAuthClient.Outcome.Unauthorized:
                        unauthorized = true;
                        continue;
                    case IClaudeOAuthClient.Outcome.Forbidden:
                        forbidden = true;
                        continue;
                    case IClaudeOAuthClient.Outcome.RateLimited:
                        rateLimited = true;
                        profileRetryAfter = profileResult.RetryAfter;
                        continue;
                    case IClaudeOAuthClient.Outcome.Error:
                        continue;
                }

                if (string.Equals(profile.AccountUuid, profileResult.AccountUuid, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(profile.OrganizationUuid, profileResult.OrganizationUuid, StringComparison.OrdinalIgnoreCase)) {
                    verifiedToken = tokenCandidate;
                    break;
                }
            }

            if (verifiedToken is null) {
                ClaudeQuotaSnapshot.RefreshStatus status = unauthorized || forbidden
                    ? ClaudeQuotaSnapshot.RefreshStatus.ReauthenticationRequired
                    : rateLimited
                        ? previousSnapshot is null
                            ? ClaudeQuotaSnapshot.RefreshStatus.Error
                            : ClaudeQuotaSnapshot.RefreshStatus.Stale
                        : ClaudeQuotaSnapshot.RefreshStatus.Incomplete;
                result = CreateFailure(
                    profileId,
                    previousSnapshot,
                    status,
                    status == ClaudeQuotaSnapshot.RefreshStatus.ReauthenticationRequired
                        ? "登入已失效，請在 Claude Desktop 重新登入並重新保存帳號。"
                        : status == ClaudeQuotaSnapshot.RefreshStatus.Stale
                            ? "查詢過於頻繁，顯示的是上次成功的結果。"
                            : status == ClaudeQuotaSnapshot.RefreshStatus.Error
                                ? "查詢過於頻繁，尚無可顯示的額度，請稍後再試。"
                                : "目前無法確認登入帳號與已保存帳號相符。"
                ) with {
                    RetryAfterUtc = rateLimited && profileRetryAfter is TimeSpan retryAfter
                        ? clock.UtcNow + retryAfter
                        : null
                };
                return await SaveResultAsync(result, stored?.Sha256, operationCancellationToken).ConfigureAwait(false);
            }

            IClaudeOAuthClient.UsageResult usageResult;
            try {
                usageResult = await oauthClient
                    .GetUsageAsync(verifiedToken.AccessToken, operationCancellationToken)
                    .ConfigureAwait(false);
            } catch (OperationCanceledException) when (operationCancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception) {
                result = CreateFailure(
                    profileId,
                    previousSnapshot,
                    previousSnapshot is null ? ClaudeQuotaSnapshot.RefreshStatus.Error : ClaudeQuotaSnapshot.RefreshStatus.Stale,
                    "目前無法查詢額度，顯示的是上次成功的結果。"
                );
                return await SaveResultAsync(result, stored?.Sha256, operationCancellationToken).ConfigureAwait(false);
            }

            result = usageResult.Outcome switch {
                IClaudeOAuthClient.Outcome.Success when usageResult.Payload is JsonElement payload =>
                    CreateParsedView(profileId, payload, previousSnapshot),
                IClaudeOAuthClient.Outcome.Unauthorized or IClaudeOAuthClient.Outcome.Forbidden =>
                    CreateFailure(
                        profileId,
                        previousSnapshot,
                        ClaudeQuotaSnapshot.RefreshStatus.ReauthenticationRequired,
                        "登入已失效，請在 Claude Desktop 重新登入並重新保存帳號。"
                    ),
                IClaudeOAuthClient.Outcome.RateLimited => CreateRateLimitedView(
                    profileId,
                    previousSnapshot,
                    usageResult.RetryAfter
                ),
                _ => CreateFailure(
                    profileId,
                    previousSnapshot,
                    previousSnapshot is null ? ClaudeQuotaSnapshot.RefreshStatus.Error : ClaudeQuotaSnapshot.RefreshStatus.Stale,
                    "目前無法查詢額度，顯示的是上次成功的結果。"
                )
            };
        } catch (OperationCanceledException) when (operationCancellationToken.IsCancellationRequested) {
            result = CreateFailure(
                profileId,
                previousSnapshot,
                previousSnapshot is null ? ClaudeQuotaSnapshot.RefreshStatus.Error : ClaudeQuotaSnapshot.RefreshStatus.Stale,
                "額度查詢已取消，顯示的是上次成功的結果。"
            );
        } catch (Exception) {
            result = CreateFailure(
                profileId,
                previousSnapshot,
                previousSnapshot is null ? ClaudeQuotaSnapshot.RefreshStatus.Error : ClaudeQuotaSnapshot.RefreshStatus.Stale,
                "目前無法查詢額度，顯示的是上次成功的結果。"
            );
        }

        return await SaveResultAsync(result, stored?.Sha256, operationCancellationToken).ConfigureAwait(false);
    }

    private ClaudeQuotaSnapshot.View CreateParsedView(
        Guid profileId,
        System.Text.Json.JsonElement payload,
        ClaudeQuotaSnapshot? previousSnapshot
    ) {
        ClaudeQuotaResponseParser.ParseResult parsed = ClaudeQuotaResponseParser.Parse(payload, profileId, clock.UtcNow);
        if (!parsed.IsComplete || parsed.Snapshot is null) {
            return CreateFailure(
                profileId,
                previousSnapshot,
                ClaudeQuotaSnapshot.RefreshStatus.Incomplete,
                parsed.Message ?? "額度資料格式無法辨識，請稍後再試。"
            );
        }

        return new(
            profileId,
            parsed.Snapshot,
            ClaudeQuotaSnapshot.RefreshStatus.Fresh,
            clock.UtcNow,
            null
        );
    }

    private ClaudeQuotaSnapshot.View CreateRateLimitedView(
        Guid profileId,
        ClaudeQuotaSnapshot? previousSnapshot,
        TimeSpan? retryAfter
    ) {
        DateTimeOffset? retryAfterUtc = retryAfter is TimeSpan delay && delay >= TimeSpan.Zero
            ? clock.UtcNow + delay
            : null;
        return CreateFailure(
            profileId,
            previousSnapshot,
            previousSnapshot is null ? ClaudeQuotaSnapshot.RefreshStatus.Error : ClaudeQuotaSnapshot.RefreshStatus.Stale,
            "查詢過於頻繁，請稍後再試。"
        ) with { RetryAfterUtc = retryAfterUtc };
    }

    private ClaudeQuotaSnapshot.View CreateFailure(
        Guid profileId,
        ClaudeQuotaSnapshot? snapshot,
        ClaudeQuotaSnapshot.RefreshStatus status,
        string message
    ) {
        return new(profileId, snapshot, status, clock.UtcNow, message);
    }

    private async Task<ClaudeQuotaSnapshot.View> SaveResultAsync(
        ClaudeQuotaSnapshot.View view,
        string? expectedSha256,
        CancellationToken cancellationToken
    ) {
        try {
            IClaudeQuotaRepository.SaveResult save = await quotaRepository
                .SaveAsync(view, expectedSha256, cancellationToken)
                .ConfigureAwait(false);
            if (save.Saved && save.Snapshot is not null) {
                view = save.Snapshot.View;
            } else if (save.Conflict) {
                IClaudeQuotaRepository.Snapshot? latest = await quotaRepository
                    .LoadAsync(view.ProfileId, cancellationToken)
                    .ConfigureAwait(false);
                view = new(
                    view.ProfileId,
                    latest?.View.Snapshot ?? view.Snapshot,
                    latest?.View.Snapshot is null
                        ? ClaudeQuotaSnapshot.RefreshStatus.Error
                        : ClaudeQuotaSnapshot.RefreshStatus.Stale,
                    view.LastAttemptAtUtc,
                    "額度資料已在其他作業中更新，顯示最新保存的資料。"
                );
            } else {
                view = view with {
                    Status = view.Snapshot is null
                        ? ClaudeQuotaSnapshot.RefreshStatus.Error
                        : ClaudeQuotaSnapshot.RefreshStatus.Stale,
                    Message = "無法保存額度查詢結果，顯示的是上次成功的結果。"
                };
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            view = view with {
                Status = view.Snapshot is null
                    ? ClaudeQuotaSnapshot.RefreshStatus.Error
                    : ClaudeQuotaSnapshot.RefreshStatus.Stale,
                Message = "額度查詢結果尚未保存，顯示的是上次成功的結果。"
            };
        } catch (Exception) {
            view = view with {
                Status = view.Snapshot is null
                    ? ClaudeQuotaSnapshot.RefreshStatus.Error
                    : ClaudeQuotaSnapshot.RefreshStatus.Stale,
                Message = "無法保存額度查詢結果，顯示的是上次成功的結果。"
            };
        }

        SetView(view);
        return view;
    }

    private void SetView(ClaudeQuotaSnapshot.View view) {
        lock (stateLock) {
            currentViews[view.ProfileId] = view;
            writeVersions[view.ProfileId] = writeVersions.GetValueOrDefault(view.ProfileId) + 1;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool HasSameContent(ClaudeQuotaSnapshot.View left, ClaudeQuotaSnapshot.View right) {
        if (left.ProfileId != right.ProfileId
            || left.Status != right.Status
            || left.LastAttemptAtUtc != right.LastAttemptAtUtc
            || left.Message != right.Message
            || left.RetryAfterUtc != right.RetryAfterUtc) {
            return false;
        }

        if (left.Snapshot is null || right.Snapshot is null) {
            return left.Snapshot is null && right.Snapshot is null;
        }

        return left.Snapshot.ProfileId == right.Snapshot.ProfileId
            && left.Snapshot.RetrievedAtUtc == right.Snapshot.RetrievedAtUtc
            && left.Snapshot.Windows.SequenceEqual(right.Snapshot.Windows)
            && SequenceEqualOrBothNull(left.Snapshot.ResetGrants, right.Snapshot.ResetGrants);
    }

    private static bool SequenceEqualOrBothNull<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right) {
        return left is null || right is null ? left is null && right is null : left.SequenceEqual(right);
    }

    private static bool IsProfileSnapshot(
        ClaudeAccountProfile profile,
        ClaudeAccountProfile.DesktopSnapshot snapshot
    ) {
        return profile.ProvenanceSchemaVersion == 1
            && snapshot.ProvenanceSchemaVersion == 1
            && profile.BindingId is Guid bindingId
            && bindingId != Guid.Empty
            && snapshot.RootBinding is not null
            && snapshot.RootBinding.Source.BindingId == bindingId
            && string.Equals(profile.WindowsUserSid, snapshot.WindowsUserSid, StringComparison.Ordinal)
            && string.Equals(profile.InstallationId, snapshot.InstallationId, StringComparison.Ordinal)
            && string.Equals(profile.DataRootFingerprint, snapshot.DataRootFingerprint, StringComparison.Ordinal)
            && string.Equals(
                profile.LocalStateEncryptedKeySha256,
                snapshot.LocalStateEncryptedKeySha256,
                StringComparison.Ordinal
            )
            && IsSha256(snapshot.LocalStateEncryptedKeySha256);
    }

    private static bool IsSha256(string? value) {
        if (value is null || value.Length != 64) {
            return false;
        }

        foreach (char character in value) {
            if (!char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)) {
                return false;
            }
        }

        return true;
    }
}
