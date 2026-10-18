using System.Security.Cryptography;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Windows.Security;
using CloudyWing.AiAccountManager.Windows.Storage;

namespace CloudyWing.AiAccountManager.Windows.Codex;

/// <summary>
/// 以目前 Windows 使用者的 DPAPI 保護 Codex Desktop `auth.json` 快照與切換交易紀錄。
/// </summary>
public sealed class WindowsCodexDesktopSnapshotRepository : ICodexDesktopSnapshotRepository {
    private const string SnapshotRelativeRoot = "codex-desktop/snapshots";
    private const string JournalRelativePath = "codex-desktop/switch-journal.json";
    private const int SchemaVersion = 1;
    private const int MaximumDocumentBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        WriteIndented = true
    };
    private readonly AtomicJsonFileStore fileStore;
    private readonly DpapiSecretProtector secretProtector = new();
    private readonly string dataRootPath;

    /// <summary>
    /// 建立受目前使用者 ACL 與 DPAPI 保護的 Codex Desktop 快照存放區。
    /// </summary>
    /// <param name="dataDirectory">應用程式資料目錄。</param>
    /// <param name="fileStore">提供原子 JSON 寫入的檔案 store。</param>
    public WindowsCodexDesktopSnapshotRepository(
        CurrentUserDataDirectory dataDirectory,
        AtomicJsonFileStore fileStore
    ) {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        this.fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        dataRootPath = dataDirectory.EnsureCreated();
    }

    /// <inheritdoc />
    public Task<IReadOnlySet<Guid>> ListAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        HashSet<Guid> accounts = [];
        string directory = Path.Combine(dataRootPath, SnapshotRelativeRoot);
        if (Directory.Exists(directory)) {
            foreach (string path in Directory.EnumerateFiles(directory, "*.json")) {
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid accountId)
                    && accountId != Guid.Empty) {
                    accounts.Add(accountId);
                }
            }
        }

        return Task.FromResult<IReadOnlySet<Guid>>(accounts);
    }

    /// <inheritdoc />
    public async Task<byte[]?> LoadAsync(Guid accountId, CancellationToken cancellationToken) {
        AtomicJsonFileStore.ReadResult file = await fileStore
            .ReadAsync(GetSnapshotRelativePath(accountId), cancellationToken)
            .ConfigureAwait(false);
        if (!file.Exists) {
            return null;
        }

        SnapshotDocument document = Deserialize<SnapshotDocument>(file.Content);
        if (document.SchemaVersion != SchemaVersion || document.AccountId != accountId) {
            throw new InvalidDataException("Codex Desktop snapshot schema or account binding is invalid.");
        }

        return Unprotect(document.ProtectedAuthBase64);
    }

    /// <inheritdoc />
    public async Task SaveAsync(Guid accountId, byte[] content, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(content);
        string relativePath = GetSnapshotRelativePath(accountId);
        SnapshotDocument document = new(SchemaVersion, accountId, Protect(content));
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        await WriteReplacingAsync(relativePath, serialized, IsValidSnapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteAsync(Guid accountId, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteIfExists(GetSnapshotRelativePath(accountId));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<ICodexDesktopSnapshotRepository.SwitchJournal?> LoadJournalAsync(CancellationToken cancellationToken) {
        AtomicJsonFileStore.ReadResult file = await fileStore.ReadAsync(JournalRelativePath, cancellationToken)
            .ConfigureAwait(false);
        if (!file.Exists) {
            return null;
        }

        JournalDocument document = Deserialize<JournalDocument>(file.Content);
        if (document.SchemaVersion != SchemaVersion
            || document.TransactionId == Guid.Empty
            || document.SourceAccountId == Guid.Empty
            || document.TargetAccountId == Guid.Empty) {
            throw new InvalidDataException("Codex Desktop switch journal is invalid.");
        }

        return new(
            document.TransactionId,
            document.SourceAccountId,
            document.TargetAccountId,
            Unprotect(document.ProtectedOriginalAuthBase64),
            document.RelaunchDesktop,
            document.AppUserModelId,
            document.UpdatedAtUtc
        );
    }

    /// <inheritdoc />
    public async Task SaveJournalAsync(
        ICodexDesktopSnapshotRepository.SwitchJournal journal,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(journal);
        JournalDocument document = new(
            SchemaVersion,
            journal.TransactionId,
            journal.SourceAccountId,
            journal.TargetAccountId,
            Protect(journal.OriginalAuthContent),
            journal.RelaunchDesktop,
            journal.AppUserModelId,
            journal.UpdatedAtUtc
        );
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        await WriteReplacingAsync(JournalRelativePath, serialized, IsValidJournal, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteJournalAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteIfExists(JournalRelativePath);
        return Task.CompletedTask;
    }

    private async Task WriteReplacingAsync(
        string relativePath,
        byte[] content,
        Func<byte[], bool> validate,
        CancellationToken cancellationToken
    ) {
        AtomicJsonFileStore.ReadResult existing = await fileStore.ReadAsync(relativePath, cancellationToken)
            .ConfigureAwait(false);
        AtomicJsonFileStore.WriteResult write = await fileStore.WriteAsync(
            relativePath,
            existing.Exists ? existing.Sha256 : null,
            content,
            validate,
            cancellationToken
        ).ConfigureAwait(false);
        if (!write.Saved) {
            throw new IOException("Codex Desktop snapshot data was changed by another operation.");
        }
    }

    private string Protect(byte[] content) {
        byte[] protectedContent = secretProtector.Protect(content);
        try {
            return Convert.ToBase64String(protectedContent);
        } finally {
            CryptographicOperations.ZeroMemory(protectedContent);
        }
    }

    private byte[] Unprotect(string protectedBase64) {
        byte[] protectedContent = Convert.FromBase64String(protectedBase64);
        try {
            return secretProtector.Unprotect(protectedContent);
        } finally {
            CryptographicOperations.ZeroMemory(protectedContent);
        }
    }

    private void DeleteIfExists(string relativePath) {
        string path = Path.Combine(dataRootPath, relativePath);
        if (File.Exists(path)) {
            File.Delete(path);
        }
    }

    private static string GetSnapshotRelativePath(Guid accountId) {
        if (accountId == Guid.Empty) {
            throw new ArgumentException("Codex account ID must not be empty.", nameof(accountId));
        }

        return Path.Combine(SnapshotRelativeRoot, accountId.ToString("N") + ".json");
    }

    private static bool IsValidSnapshot(byte[] content) {
        try {
            SnapshotDocument document = Deserialize<SnapshotDocument>(content);
            return document.SchemaVersion == SchemaVersion
                && document.AccountId != Guid.Empty
                && !string.IsNullOrWhiteSpace(document.ProtectedAuthBase64);
        } catch (InvalidDataException) {
            return false;
        }
    }

    private static bool IsValidJournal(byte[] content) {
        try {
            JournalDocument document = Deserialize<JournalDocument>(content);
            return document.SchemaVersion == SchemaVersion
                && document.TransactionId != Guid.Empty
                && !string.IsNullOrWhiteSpace(document.ProtectedOriginalAuthBase64);
        } catch (InvalidDataException) {
            return false;
        }
    }

    private static T Deserialize<T>(byte[] content) where T : class {
        if (content.Length > MaximumDocumentBytes) {
            throw new InvalidDataException("Codex Desktop snapshot data exceeded its supported size limit.");
        }

        try {
            return JsonSerializer.Deserialize<T>(content, JsonOptions)
                ?? throw new InvalidDataException("Codex Desktop snapshot data is empty.");
        } catch (JsonException failure) {
            throw new InvalidDataException("Codex Desktop snapshot data is not valid JSON.", failure);
        }
    }

    private sealed record SnapshotDocument(int SchemaVersion, Guid AccountId, string ProtectedAuthBase64);

    private sealed record JournalDocument(
        int SchemaVersion,
        Guid TransactionId,
        Guid SourceAccountId,
        Guid TargetAccountId,
        string ProtectedOriginalAuthBase64,
        bool RelaunchDesktop,
        string? AppUserModelId,
        DateTimeOffset UpdatedAtUtc
    );
}
