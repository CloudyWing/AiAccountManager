using System.Text;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Windows.Storage;

namespace CloudyWing.AiAccountManager.Windows.Codex;

/// <summary>
/// 管理隔離的 Codex 帳號目錄與交易紀錄。
/// </summary>
public sealed class WindowsCodexProfileStorage : ICodexProfileStorage {
    private const string HomeRelativePath = "CODEX_HOME";
    private const string ProfileRelativeRoot = "codex-profiles";
    private const string TransactionRelativeRoot = "transactions";
    private static readonly byte[] KeyringConfig = Encoding.UTF8.GetBytes("cli_auth_credentials_store = \"keyring\"\n");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        WriteIndented = true
    };
    private readonly CurrentUserDataDirectory dataDirectory;
    private readonly AtomicJsonFileStore fileStore;

    /// <summary>
    /// 初始化 Codex 帳號資料儲存體。
    /// </summary>
    /// <param name="dataDirectory">使用者資料目錄。</param>
    /// <param name="fileStore">交易紀錄的原子檔案儲存體。</param>
    public WindowsCodexProfileStorage(
        CurrentUserDataDirectory dataDirectory,
        AtomicJsonFileStore fileStore
    ) {
        this.dataDirectory = dataDirectory;
        this.fileStore = fileStore;
    }

    /// <inheritdoc />
    public string GetHomePath(Guid accountId) {
        ValidateAccountId(accountId);
        string root = dataDirectory.EnsureCreated();
        return Path.GetFullPath(Path.Combine(root, ProfileRelativeRoot, accountId.ToString("D"), HomeRelativePath));
    }

    /// <inheritdoc />
    public async Task CreateHomeAsync(Guid accountId, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        string homePath = GetHomePath(accountId);
        if (Directory.Exists(homePath) || File.Exists(homePath)) {
            throw new IOException("The isolated Codex home already exists.");
        }

        Directory.CreateDirectory(homePath);
        string configPath = Path.Combine(homePath, "config.toml");
        await using FileStream stream = new(
            configPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough
        );
        await stream.WriteAsync(KeyringConfig, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveUnexpectedAuthFileAsync(Guid accountId, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        string authPath = Path.Combine(GetHomePath(accountId), "auth.json");
        if (!File.Exists(authPath)) {
            return false;
        }

        await Task.Run(() => File.Delete(authPath), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task RemoveHomeAsync(Guid accountId, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        string homePath = GetHomePath(accountId);
        if (!Directory.Exists(homePath)) {
            return;
        }

        await Task.Run(
            () => RemoveReadOnlyAttributesAndDeleteHome(homePath),
            cancellationToken
        ).ConfigureAwait(false);
    }

    private static void RemoveReadOnlyAttributesAndDeleteHome(string homePath) {
        DirectoryInfo homeDirectory = new(homePath);
        if ((homeDirectory.Attributes & FileAttributes.ReparsePoint) != 0) {
            Directory.Delete(homePath);
            return;
        }

        EnumerationOptions enumerationOptions = new() {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        foreach (string filePath in Directory.EnumerateFiles(homePath, "*", enumerationOptions)) {
            FileAttributes attributes = File.GetAttributes(filePath);
            if ((attributes & FileAttributes.ReadOnly) != 0) {
                File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(homePath, recursive: true);
    }

    /// <inheritdoc />
    public async Task<CodexProfileTransaction?> LoadPendingRemovalTransactionAsync(
        Guid accountId,
        CancellationToken cancellationToken
    ) {
        ValidateAccountId(accountId);
        string transactionRoot = Path.Combine(dataDirectory.EnsureCreated(), TransactionRelativeRoot);
        if (!Directory.Exists(transactionRoot)) {
            return null;
        }

        CodexProfileTransaction? pendingRemoval = null;
        foreach (string path in Directory.EnumerateFiles(transactionRoot, "*.json").Order(StringComparer.Ordinal)) {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(path);
            string transactionIdText = Path.GetFileNameWithoutExtension(fileName);
            if (!Guid.TryParseExact(transactionIdText, "N", out Guid fileTransactionId)) {
                throw new InvalidDataException("A profile transaction journal has an invalid file name.");
            }

            string relativePath = Path.Combine(TransactionRelativeRoot, fileName);
            AtomicJsonFileStore.ReadResult read = await fileStore.ReadAsync(relativePath, cancellationToken)
                .ConfigureAwait(false);
            if (!read.Exists) {
                continue;
            }

            CodexProfileTransaction transaction;
            try {
                transaction = DeserializeTransaction(read.Content);
            } catch (JsonException failure) {
                throw new InvalidDataException("A profile transaction journal is malformed.", failure);
            }

            if (transaction.TransactionId != fileTransactionId) {
                throw new InvalidDataException("A profile transaction journal does not match its schema.");
            }

            if (!IsKnownTransactionState(transaction)) {
                throw new InvalidDataException("A profile transaction journal has an unknown operation or stage.");
            }

            if (transaction.AccountId != accountId) {
                continue;
            }

            if (string.Equals(transaction.Operation, "add", StringComparison.Ordinal)) {
                continue;
            }

            if (pendingRemoval is not null) {
                throw new InvalidDataException("Multiple pending profile removal journals exist for the account.");
            }

            pendingRemoval = transaction;
        }

        return pendingRemoval;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodexProfileTransaction>> LoadPendingAddCleanupTransactionsAsync(
        CancellationToken cancellationToken
    ) {
        string transactionRoot = Path.Combine(dataDirectory.EnsureCreated(), TransactionRelativeRoot);
        if (!Directory.Exists(transactionRoot)) {
            return [];
        }

        List<CodexProfileTransaction> pendingTransactions = [];
        foreach (string path in Directory.EnumerateFiles(transactionRoot, "*.json").Order(StringComparer.Ordinal)) {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(path);
            string transactionIdText = Path.GetFileNameWithoutExtension(fileName);
            if (!Guid.TryParseExact(transactionIdText, "N", out Guid fileTransactionId)) {
                throw new InvalidDataException("A profile transaction journal has an invalid file name.");
            }

            string relativePath = Path.Combine(TransactionRelativeRoot, fileName);
            AtomicJsonFileStore.ReadResult read = await fileStore.ReadAsync(relativePath, cancellationToken)
                .ConfigureAwait(false);
            if (!read.Exists) {
                continue;
            }

            CodexProfileTransaction transaction;
            try {
                transaction = DeserializeTransaction(read.Content);
            } catch (JsonException failure) {
                throw new InvalidDataException("A profile transaction journal is malformed.", failure);
            }

            if (transaction.TransactionId != fileTransactionId || !IsKnownTransactionState(transaction)) {
                throw new InvalidDataException("A profile transaction journal does not match its schema.");
            }

            if (string.Equals(transaction.Operation, "add", StringComparison.Ordinal)
                && transaction.Stage is "started" or "home-created" or "identity-confirmed" or "catalog-committed"
                    or "cleanup-pending" or "keyring-cleared" or "home-removed") {
                pendingTransactions.Add(transaction);
            }
        }

        IGrouping<Guid, CodexProfileTransaction>? duplicateAccount = pendingTransactions
            .GroupBy(transaction => transaction.AccountId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateAccount is not null) {
            throw new InvalidDataException("Multiple pending add cleanup journals exist for one account.");
        }

        return pendingTransactions;
    }

    /// <inheritdoc />
    public async Task SaveTransactionAsync(
        CodexProfileTransaction transaction,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.TransactionId == Guid.Empty
            || transaction.AccountId == Guid.Empty
            || transaction.UpdatedAtUtc.Offset != TimeSpan.Zero
            || string.IsNullOrWhiteSpace(transaction.ExpectedCatalogSha256)) {
            throw new ArgumentException("Profile transaction metadata is invalid.", nameof(transaction));
        }

        string relativePath = GetTransactionRelativePath(transaction.TransactionId);
        AtomicJsonFileStore.ReadResult existing = await fileStore
            .ReadAsync(relativePath, cancellationToken)
            .ConfigureAwait(false);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(transaction, JsonOptions);
        AtomicJsonFileStore.WriteResult write = await fileStore.WriteAsync(
            relativePath,
            existing.Exists ? existing.Sha256 : null,
            content,
            IsValidTransaction,
            cancellationToken
        ).ConfigureAwait(false);
        if (!write.Saved) {
            throw new IOException("Profile transaction journal changed while it was being updated.");
        }
    }

    /// <inheritdoc />
    public async Task DeleteTransactionAsync(Guid transactionId, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        string relativePath = GetTransactionRelativePath(transactionId);
        AtomicJsonFileStore.ReadResult existing = await fileStore
            .ReadAsync(relativePath, cancellationToken)
            .ConfigureAwait(false);
        if (!existing.Exists) {
            return;
        }

        string path = Path.Combine(dataDirectory.EnsureCreated(), relativePath);
        await Task.Run(() => File.Delete(path), cancellationToken).ConfigureAwait(false);
    }

    private static string GetTransactionRelativePath(Guid transactionId) {
        if (transactionId == Guid.Empty) {
            throw new ArgumentException("Transaction ID must not be empty.", nameof(transactionId));
        }

        return Path.Combine(TransactionRelativeRoot, transactionId.ToString("N") + ".json");
    }

    private static void ValidateAccountId(Guid accountId) {
        if (accountId == Guid.Empty) {
            throw new ArgumentException("Account ID must not be empty.", nameof(accountId));
        }
    }

    private static bool IsValidTransaction(byte[] content) {
        try {
            CodexProfileTransaction transaction = DeserializeTransaction(content);
            return IsKnownTransactionState(transaction);
        } catch (JsonException) {
            return false;
        } catch (InvalidDataException) {
            return false;
        }
    }

    private static bool IsKnownTransactionState(CodexProfileTransaction transaction) {
        return transaction.Operation switch {
            "add" => transaction.Stage is "started" or "home-created" or "identity-confirmed" or "catalog-committed"
                or "cleanup-pending" or "keyring-cleared" or "home-removed",
            "remove" => transaction.Stage is "started" or "keyring-cleared" or "home-removed" or "catalog-conflict"
                or "cleanup-pending",
            _ => false
        };
    }

    private static CodexProfileTransaction DeserializeTransaction(byte[] content) {
        using JsonDocument document = JsonDocument.Parse(content);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("transactionId", out JsonElement transactionIdElement)
            || !transactionIdElement.TryGetGuid(out Guid transactionId)
            || transactionId == Guid.Empty
            || !root.TryGetProperty("accountId", out JsonElement accountIdElement)
            || !accountIdElement.TryGetGuid(out Guid accountId)
            || accountId == Guid.Empty
            || !root.TryGetProperty("operation", out JsonElement operationElement)
            || operationElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(operationElement.GetString())
            || !root.TryGetProperty("stage", out JsonElement stageElement)
            || stageElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(stageElement.GetString())
            || !root.TryGetProperty("expectedCatalogSha256", out JsonElement catalogHashElement)
            || catalogHashElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(catalogHashElement.GetString())
            || !root.TryGetProperty("updatedAtUtc", out JsonElement updatedAtElement)
            || !updatedAtElement.TryGetDateTimeOffset(out DateTimeOffset updatedAtUtc)
            || updatedAtUtc.Offset != TimeSpan.Zero
            || !root.TryGetProperty("cleanupResult", out JsonElement cleanupResultElement)
            || cleanupResultElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) {
            throw new InvalidDataException("Profile transaction journal fields do not match the existing schema.");
        }

        return JsonSerializer.Deserialize<CodexProfileTransaction>(content, JsonOptions)
            ?? throw new InvalidDataException("Profile transaction journal is empty.");
    }
}
