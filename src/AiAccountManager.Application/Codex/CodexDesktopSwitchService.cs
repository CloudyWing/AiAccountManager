using System.Security.Cryptography;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Runtime;
using CloudyWing.AiAccountManager.Application.Security;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 保存 Codex Desktop 登入快照，並以「正常關閉、取代 `auth.json`、重新開啟」切換 Desktop 帳號。
/// </summary>
public sealed class CodexDesktopSwitchService : IDisposable {
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(20);
    private readonly IAccountCatalogRepository catalogRepository;
    private readonly ICodexDesktopSnapshotRepository snapshotRepository;
    private readonly ICodexDesktopAuthStore authStore;
    private readonly ICodexDesktopProcessController processController;
    private readonly IIdentityFingerprinter identityFingerprinter;
    private readonly IClock clock;
    private readonly SemaphoreSlim operationGate = new(1, 1);

    /// <summary>
    /// 建立 Codex Desktop 切換服務。
    /// </summary>
    /// <param name="catalogRepository">Codex 帳號 catalog。</param>
    /// <param name="snapshotRepository">Desktop 快照與交易紀錄存放區。</param>
    /// <param name="authStore">Codex Desktop `auth.json` 存取 adapter。</param>
    /// <param name="processController">Codex Desktop 程序控制 adapter。</param>
    /// <param name="identityFingerprinter">產生帳號身分指紋的服務。</param>
    /// <param name="clock">目前 UTC 時間來源。</param>
    public CodexDesktopSwitchService(
        IAccountCatalogRepository catalogRepository,
        ICodexDesktopSnapshotRepository snapshotRepository,
        ICodexDesktopAuthStore authStore,
        ICodexDesktopProcessController processController,
        IIdentityFingerprinter identityFingerprinter,
        IClock clock
    ) {
        this.catalogRepository = catalogRepository;
        this.snapshotRepository = snapshotRepository;
        this.authStore = authStore;
        this.processController = processController;
        this.identityFingerprinter = identityFingerprinter;
        this.clock = clock;
    }

    /// <summary>
    /// 於快照或交易狀態變更時發生。
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// 讀取已保存快照的帳號與未完成交易，並清除已不在 catalog 的孤立快照。
    /// </summary>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>切換狀態。</returns>
    public async Task<SwitchState> GetStateAsync(CancellationToken cancellationToken) {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            IAccountCatalogRepository.Snapshot catalog = await catalogRepository.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            IReadOnlySet<Guid> snapshotAccounts = await snapshotRepository.ListAsync(cancellationToken)
                .ConfigureAwait(false);
            HashSet<Guid> catalogAccounts = catalog.Accounts.Select(account => account.AccountId).ToHashSet();
            JournalReference? journal = await LoadJournalSummaryAsync(cancellationToken).ConfigureAwait(false);
            HashSet<Guid> retained = [];
            foreach (Guid accountId in snapshotAccounts) {
                bool referencedByJournal = journal is not null
                    && (journal.SourceAccountId == accountId || journal.TargetAccountId == accountId);
                if (catalogAccounts.Contains(accountId)) {
                    retained.Add(accountId);
                } else if (!referencedByJournal) {
                    await snapshotRepository.DeleteAsync(accountId, cancellationToken).ConfigureAwait(false);
                }
            }

            return new(retained, journal is not null);
        } finally {
            operationGate.Release();
        }
    }

    /// <summary>
    /// 將 Codex Desktop 目前的登入保存為唯一對應帳號的快照。
    /// </summary>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>保存結果。</returns>
    public async Task<OperationResult> CaptureCurrentAsync(CancellationToken cancellationToken) {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (await snapshotRepository.LoadJournalAsync(cancellationToken).ConfigureAwait(false) is { } journal) {
                ClearJournal(journal);
                return Failed(Outcome.RecoveryRequired, "有尚未完成的 Codex Desktop 切換，請先還原 Desktop 登入。");
            }

            IAccountCatalogRepository.Snapshot catalog = await catalogRepository.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            ICodexDesktopAuthStore.ReadResult current = await authStore.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            try {
                if (current.Status != CodexLocalAuthReadStatus.Success || current.Content is null || current.Identity is null) {
                    return Failed(Outcome.CurrentLoginUnavailable, ReadFailureMessage(current.Status));
                }

                CodexAccountProfile? account = FindUniqueAccount(catalog.Accounts, current.Identity);
                if (account is null) {
                    return Failed(
                        Outcome.CurrentLoginNotSaved,
                        "Codex Desktop 目前登入的帳號沒有唯一對應的已保存帳號。請先以同一帳號加入額度帳號。"
                    );
                }

                await snapshotRepository.SaveAsync(account.AccountId, current.Content, cancellationToken)
                    .ConfigureAwait(false);
                StateChanged?.Invoke(this, EventArgs.Empty);
                return new(Outcome.Succeeded, account.AccountId, $"已保存 {DisplayName(account)} 的 Codex Desktop 登入。");
            } finally {
                Clear(current.Content);
                current.Identity?.Clear();
            }
        } finally {
            operationGate.Release();
        }
    }

    /// <summary>
    /// 將 Codex Desktop 切換為指定帳號，失敗時還原原本的登入。
    /// </summary>
    /// <param name="targetAccountId">目標 Codex 帳號識別碼。</param>
    /// <param name="cancellationToken">取消尚未寫入前作業的權杖。</param>
    /// <returns>切換結果。</returns>
    public async Task<OperationResult> SwitchAsync(Guid targetAccountId, CancellationToken cancellationToken) {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            return await SwitchCoreAsync(targetAccountId, cancellationToken).ConfigureAwait(false);
        } finally {
            operationGate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 以交易紀錄中的原始內容還原 `auth.json`，並刪除交易紀錄。
    /// </summary>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>還原結果。</returns>
    public async Task<OperationResult> RecoverAsync(CancellationToken cancellationToken) {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            ICodexDesktopSnapshotRepository.SwitchJournal? journal = await snapshotRepository
                .LoadJournalAsync(cancellationToken)
                .ConfigureAwait(false);
            if (journal is null) {
                return new(Outcome.Succeeded, null, "沒有需要還原的 Codex Desktop 切換。");
            }

            try {
                CodexDesktopProcessStatus status = await processController.GetStatusAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (status != CodexDesktopProcessStatus.NotRunning) {
                    return Failed(
                        Outcome.DesktopNotClosed,
                        "請先完全結束 Codex Desktop（含系統匣），再還原 Desktop 登入。"
                    );
                }

                await authStore.ReplaceAsync(journal.OriginalAuthContent, CancellationToken.None).ConfigureAwait(false);
                await snapshotRepository.DeleteJournalAsync(CancellationToken.None).ConfigureAwait(false);
                return new(Outcome.Succeeded, journal.SourceAccountId, "已還原切換前的 Codex Desktop 登入。");
            } finally {
                ClearJournal(journal);
            }
        } finally {
            operationGate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 刪除指定帳號的 Desktop 快照，用於移除帳號後的清理。
    /// </summary>
    /// <param name="accountId">Codex 帳號識別碼。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    public async Task ForgetAsync(Guid accountId, CancellationToken cancellationToken) {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            await snapshotRepository.DeleteAsync(accountId, cancellationToken).ConfigureAwait(false);
        } finally {
            operationGate.Release();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose() {
        operationGate.Dispose();
    }

    private async Task<OperationResult> SwitchCoreAsync(Guid targetAccountId, CancellationToken cancellationToken) {
        if (await snapshotRepository.LoadJournalAsync(cancellationToken).ConfigureAwait(false) is { } pending) {
            ClearJournal(pending);
            return Failed(Outcome.RecoveryRequired, "有尚未完成的 Codex Desktop 切換，請先還原 Desktop 登入。");
        }

        IAccountCatalogRepository.Snapshot catalog = await catalogRepository.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        CodexAccountProfile? target = catalog.Accounts.FirstOrDefault(account => account.AccountId == targetAccountId);
        if (target is null) {
            return Failed(Outcome.TargetUnavailable, "找不到要切換的 Codex 帳號，請重新載入。");
        }

        byte[]? targetContent = await snapshotRepository.LoadAsync(targetAccountId, cancellationToken).ConfigureAwait(false);
        byte[]? originalContent = null;
        try {
            if (targetContent is null) {
                return Failed(Outcome.TargetUnavailable, $"{DisplayName(target)} 尚未保存 Codex Desktop 登入。");
            }

            if (!IdentityMatches(authStore.Parse(targetContent), target)) {
                return Failed(Outcome.TargetUnavailable, $"{DisplayName(target)} 的 Desktop 快照與帳號身分不符，請重新保存。");
            }

            ICodexDesktopAuthStore.ReadResult before = await authStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            CodexAccountProfile? source;
            try {
                if (before.Status != CodexLocalAuthReadStatus.Success || before.Identity is null) {
                    return Failed(Outcome.CurrentLoginUnavailable, ReadFailureMessage(before.Status));
                }

                source = FindUniqueAccount(catalog.Accounts, before.Identity);
            } finally {
                Clear(before.Content);
                before.Identity?.Clear();
            }

            if (source is null) {
                return Failed(
                    Outcome.CurrentLoginNotSaved,
                    "Codex Desktop 目前登入的帳號尚未加入本工具。切換會覆蓋這個登入，請先以同一帳號加入額度帳號並保存 Desktop 登入。"
                );
            }

            if (source.AccountId == target.AccountId) {
                return new(Outcome.Succeeded, target.AccountId, $"Codex Desktop 已在使用 {DisplayName(target)}。");
            }

            CodexDesktopProcessStatus status = await processController.GetStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            if (status == CodexDesktopProcessStatus.Unknown) {
                return Failed(Outcome.DesktopNotClosed, "無法確認 Codex Desktop 的程序狀態，未進行切換。");
            }

            bool relaunch = status == CodexDesktopProcessStatus.Running;
            string? appUserModelId = null;
            if (relaunch) {
                ICodexDesktopProcessController.CloseResult close = await processController
                    .CloseGracefullyAsync(CloseTimeout, cancellationToken)
                    .ConfigureAwait(false);
                appUserModelId = close.AppUserModelId;
                if (!close.Closed) {
                    return Failed(
                        Outcome.DesktopNotClosed,
                        "Codex Desktop 未在時限內結束，未進行切換。請從視窗或系統匣完全結束 Codex Desktop 後重試。"
                    );
                }
            }

            if (!await IsStoppedAsync(cancellationToken).ConfigureAwait(false)) {
                return Failed(Outcome.DesktopNotClosed, "Codex Desktop 在關閉後又重新啟動，未進行切換。");
            }

            ICodexDesktopAuthStore.ReadResult closed = await authStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            try {
                if (closed.Status != CodexLocalAuthReadStatus.Success || closed.Content is null || closed.Identity is null
                    || !IdentityMatches(closed.Identity, source)) {
                    return Failed(Outcome.CurrentLoginUnavailable, "關閉 Codex Desktop 後登入資料已改變，未進行切換。");
                }

                originalContent = closed.Content.ToArray();
            } finally {
                Clear(closed.Content);
                closed.Identity?.Clear();
            }

            await snapshotRepository.SaveAsync(source.AccountId, originalContent, cancellationToken).ConfigureAwait(false);
            ICodexDesktopSnapshotRepository.SwitchJournal journal = new(
                Guid.NewGuid(),
                source.AccountId,
                target.AccountId,
                originalContent.ToArray(),
                relaunch,
                appUserModelId,
                clock.UtcNow
            );
            try {
                await snapshotRepository.SaveJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            } finally {
                ClearJournal(journal);
            }

            string? failure = await ApplyTargetAsync(target, targetContent).ConfigureAwait(false);
            if (failure is not null) {
                (bool restored, string rollbackMessage) = await RollBackAsync(originalContent).ConfigureAwait(false);
                if (restored) {
                    await RelaunchAsync(relaunch, appUserModelId).ConfigureAwait(false);
                }

                return Failed(restored ? Outcome.RolledBack : Outcome.RecoveryRequired, $"{failure}{rollbackMessage}");
            }

            await snapshotRepository.DeleteJournalAsync(CancellationToken.None).ConfigureAwait(false);
            string message = $"Codex Desktop 已切換為 {DisplayName(target)}。";
            if (relaunch && !await RelaunchAsync(relaunch, appUserModelId).ConfigureAwait(false)) {
                message += "無法自動重新開啟 Codex Desktop，請手動開啟。";
            }

            return new(Outcome.Succeeded, target.AccountId, message);
        } finally {
            Clear(targetContent);
            Clear(originalContent);
        }
    }

    private async Task<string?> ApplyTargetAsync(CodexAccountProfile target, byte[] targetContent) {
        // 已寫入交易紀錄後不再接受取消，避免停在 `auth.json` 與紀錄不一致的狀態。
        if (!await IsStoppedAsync(CancellationToken.None).ConfigureAwait(false)) {
            return "寫入前偵測到 Codex Desktop 重新啟動，已停止切換。";
        }

        try {
            await authStore.ReplaceAsync(targetContent, CancellationToken.None).ConfigureAwait(false);
        } catch (Exception failure) when (failure is not OperationCanceledException) {
            return "無法寫入 Codex Desktop 登入檔。";
        }

        ICodexDesktopAuthStore.ReadResult written = await authStore.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        try {
            if (written.Status != CodexLocalAuthReadStatus.Success || written.Identity is null
                || !IdentityMatches(written.Identity, target)) {
                return "寫入後的 Codex Desktop 登入身分與目標帳號不符。";
            }
        } finally {
            Clear(written.Content);
            written.Identity?.Clear();
        }

        if (!await IsStoppedAsync(CancellationToken.None).ConfigureAwait(false)) {
            return "寫入期間偵測到 Codex Desktop 重新啟動。";
        }

        return null;
    }

    private async Task<(bool Restored, string Message)> RollBackAsync(byte[] originalContent) {
        if (!await IsStoppedAsync(CancellationToken.None).ConfigureAwait(false)) {
            return (false, "Codex Desktop 正在執行，無法自動還原；請完全結束 Codex Desktop 後使用「還原 Desktop 登入」。");
        }

        try {
            await authStore.ReplaceAsync(originalContent, CancellationToken.None).ConfigureAwait(false);
            await snapshotRepository.DeleteJournalAsync(CancellationToken.None).ConfigureAwait(false);
            return (true, "已還原切換前的登入。");
        } catch (Exception failure) when (failure is not OperationCanceledException) {
            return (false, "自動還原失敗，交易紀錄已保留；請使用「還原 Desktop 登入」重試。");
        }
    }

    private async Task<bool> RelaunchAsync(bool relaunch, string? appUserModelId) {
        if (!relaunch) {
            return true;
        }

        if (string.IsNullOrWhiteSpace(appUserModelId)) {
            return false;
        }

        try {
            await processController.LaunchAsync(appUserModelId, CancellationToken.None).ConfigureAwait(false);
            return true;
        } catch (Exception failure) when (failure is not OperationCanceledException) {
            return false;
        }
    }

    private async Task<bool> IsStoppedAsync(CancellationToken cancellationToken) {
        return await processController.GetStatusAsync(cancellationToken).ConfigureAwait(false)
            == CodexDesktopProcessStatus.NotRunning;
    }

    private async Task<JournalReference?> LoadJournalSummaryAsync(CancellationToken cancellationToken) {
        ICodexDesktopSnapshotRepository.SwitchJournal? journal = await snapshotRepository
            .LoadJournalAsync(cancellationToken)
            .ConfigureAwait(false);
        if (journal is null) {
            return null;
        }

        ClearJournal(journal);
        return new JournalReference(journal.SourceAccountId, journal.TargetAccountId);
    }

    private CodexAccountProfile? FindUniqueAccount(
        IReadOnlyList<CodexAccountProfile> accounts,
        CodexLocalAuthIdentity identity
    ) {
        CodexAccountProfile[] matches = accounts.Where(account => IdentityMatches(identity, account)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private bool IdentityMatches(CodexLocalAuthReadResult result, CodexAccountProfile account) {
        try {
            return result.Status == CodexLocalAuthReadStatus.Success
                && result.Identity is not null
                && IdentityMatches(result.Identity, account);
        } finally {
            result.Identity?.Clear();
        }
    }

    private bool IdentityMatches(CodexLocalAuthIdentity identity, CodexAccountProfile account) {
        string fingerprint = identityFingerprinter.CreateFingerprint(identity.AccountId, identity.Email, "chatgpt");
        return identity.AccountId is not null
            ? string.Equals(account.IdentityFingerprint, fingerprint, StringComparison.Ordinal)
            : string.Equals(account.FallbackIdentityFingerprint, fingerprint, StringComparison.Ordinal);
    }

    private static string ReadFailureMessage(CodexLocalAuthReadStatus status) {
        return status switch {
            CodexLocalAuthReadStatus.FileMissing => "Codex Desktop 尚未登入，找不到本機登入資料。",
            CodexLocalAuthReadStatus.UnsupportedAuth => "Codex Desktop 目前使用不支援的登入方式（例如 API key）。",
            CodexLocalAuthReadStatus.SharingViolation or CodexLocalAuthReadStatus.ChangedDuringRead
                => "Codex Desktop 正在更新登入資料，請稍後重試。",
            _ => "無法讀取 Codex Desktop 的本機登入資料。"
        };
    }

    private static string DisplayName(CodexAccountProfile account) {
        return string.IsNullOrWhiteSpace(account.Alias) ? account.MaskedIdentity : account.Alias;
    }

    private static OperationResult Failed(Outcome outcome, string message) {
        return new(outcome, null, message);
    }

    private static void Clear(byte[]? content) {
        if (content is not null) {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static void ClearJournal(ICodexDesktopSnapshotRepository.SwitchJournal journal) {
        CryptographicOperations.ZeroMemory(journal.OriginalAuthContent);
    }

    /// <summary>
    /// 定義 Codex Desktop 保存與切換作業的結果。
    /// </summary>
    public enum Outcome {
        /// <summary>作業完成。</summary>
        Succeeded,

        /// <summary>有未完成的切換交易需要還原。</summary>
        RecoveryRequired,

        /// <summary>無法讀取目前的 Desktop 登入。</summary>
        CurrentLoginUnavailable,

        /// <summary>目前的 Desktop 登入沒有唯一對應的已保存帳號。</summary>
        CurrentLoginNotSaved,

        /// <summary>目標帳號不存在或沒有可用的 Desktop 快照。</summary>
        TargetUnavailable,

        /// <summary>Codex Desktop 未關閉或狀態無法確認。</summary>
        DesktopNotClosed,

        /// <summary>寫入後失敗並已嘗試還原。</summary>
        RolledBack
    }

    /// <summary>
    /// 回傳 Codex Desktop 保存或切換作業的結果。
    /// </summary>
    /// <param name="Outcome">作業結果。</param>
    /// <param name="AccountId">相關的 Codex 帳號識別碼；失敗時可能為 <see langword="null" />。</param>
    /// <param name="Message">可供畫面顯示的結果訊息。</param>
    public sealed record OperationResult(Outcome Outcome, Guid? AccountId, string Message);

    /// <summary>
    /// 描述 Codex Desktop 切換的頁面狀態。
    /// </summary>
    /// <param name="AccountsWithSnapshot">已保存 Desktop 快照的帳號。</param>
    /// <param name="RecoveryRequired">有未完成交易需要還原時為 <see langword="true" />。</param>
    public sealed record SwitchState(IReadOnlySet<Guid> AccountsWithSnapshot, bool RecoveryRequired);

    private sealed record JournalReference(Guid SourceAccountId, Guid TargetAccountId);
}
