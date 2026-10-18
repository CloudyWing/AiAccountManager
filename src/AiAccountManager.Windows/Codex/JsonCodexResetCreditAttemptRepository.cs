using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Windows.Storage;

namespace CloudyWing.AiAccountManager.Windows.Codex;

/// <summary>以版本化 JSON 原子保存自動使用紀錄。</summary>
public sealed class JsonCodexResetCreditAttemptRepository : ICodexResetCreditAttemptRepository {
    private const string FilePath = "codex-reset-credit-attempts.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly AtomicJsonFileStore store;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>建立儲存庫。</summary>
    /// <param name="store">原子檔案儲存體。</param>
    public JsonCodexResetCreditAttemptRepository(AtomicJsonFileStore store) { this.store = store; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodexResetCreditAttempt>> LoadAsync(CancellationToken cancellationToken) {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await ReadAsync(cancellationToken).ConfigureAwait(false)).Items; }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<bool> TryBeginAsync(CodexResetCreditAttempt attempt, CancellationToken cancellationToken) {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            (List<CodexResetCreditAttempt> items, string? hash) = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (items.Any(item => item.AccountId == attempt.AccountId && item.CreditId == attempt.CreditId)) { return false; }
            items.Add(attempt);
            await WriteAsync(items, hash, cancellationToken).ConfigureAwait(false);
            return true;
        } finally { gate.Release(); }
    }

    /// <inheritdoc />
    public async Task CompleteAsync(CodexResetCreditAttempt attempt, CancellationToken cancellationToken) {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            (List<CodexResetCreditAttempt> items, string? hash) = await ReadAsync(cancellationToken).ConfigureAwait(false);
            int index = items.FindIndex(item => item.AccountId == attempt.AccountId && item.CreditId == attempt.CreditId
                && item.IdempotencyKey == attempt.IdempotencyKey);
            if (index < 0) { throw new InvalidDataException("找不到用券嘗試紀錄。"); }
            items[index] = attempt;
            await WriteAsync(items, hash, cancellationToken).ConfigureAwait(false);
        } finally { gate.Release(); }
    }

    private async Task<(List<CodexResetCreditAttempt> Items, string? Hash)> ReadAsync(CancellationToken token) {
        AtomicJsonFileStore.ReadResult file = await store.ReadAsync(FilePath, token).ConfigureAwait(false);
        if (!file.Exists) { return ([], null); }
        Document? document = JsonSerializer.Deserialize<Document>(file.Content, JsonOptions);
        if (document?.SchemaVersion != 1 || document.Attempts is null
            || document.Attempts.Any(item => item.AccountId == Guid.Empty || string.IsNullOrWhiteSpace(item.CreditId)
                || string.IsNullOrWhiteSpace(item.IdempotencyKey) || string.IsNullOrWhiteSpace(item.Result))
            || document.Attempts.Select(item => (item.AccountId, item.CreditId)).Distinct().Count() != document.Attempts.Count) {
            throw new InvalidDataException("自動使用紀錄檔無效。");
        }
        return (document.Attempts, file.Sha256);
    }

    private async Task WriteAsync(List<CodexResetCreditAttempt> items, string? hash, CancellationToken token) {
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new Document(1, items), JsonOptions);
        AtomicJsonFileStore.WriteResult result = await store.WriteAsync(FilePath, hash, content, _ => true, token).ConfigureAwait(false);
        if (!result.Saved) { throw new IOException("自動使用紀錄已變更，無法保存。"); }
    }

    private sealed record Document(int SchemaVersion, List<CodexResetCreditAttempt> Attempts);
}
