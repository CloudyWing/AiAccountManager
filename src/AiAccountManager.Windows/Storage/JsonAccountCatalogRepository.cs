using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;

namespace CloudyWing.AiAccountManager.Windows.Storage;

/// <summary>
/// 以 JSON 檔案保存 Codex 帳號目錄。
/// </summary>
public sealed class JsonAccountCatalogRepository : IAccountCatalogRepository {
    private const string FilePath = "catalog.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        WriteIndented = true
    };
    private readonly AtomicJsonFileStore fileStore;

    /// <summary>
    /// 初始化帳號目錄儲存庫。
    /// </summary>
    /// <param name="fileStore">原子檔案儲存體。</param>
    public JsonAccountCatalogRepository(AtomicJsonFileStore fileStore) {
        this.fileStore = fileStore;
    }

    /// <inheritdoc />
    public async Task<IAccountCatalogRepository.Snapshot> LoadAsync(CancellationToken cancellationToken) {
        AtomicJsonFileStore.ReadResult file = await fileStore
            .ReadAsync(FilePath, cancellationToken)
            .ConfigureAwait(false);
        if (!file.Exists) {
            byte[] emptyCatalog = JsonSerializer.SerializeToUtf8Bytes(new CatalogDocument(1, []), JsonOptions);
            AtomicJsonFileStore.WriteResult write = await fileStore
                .WriteAsync(FilePath, null, emptyCatalog, IsValidDocument, cancellationToken)
                .ConfigureAwait(false);
            if (write.Conflict) {
                return await LoadAsync(cancellationToken).ConfigureAwait(false);
            }

            return new IAccountCatalogRepository.Snapshot([], write.Sha256);
        }

        CatalogDocument document = DeserializeAndValidate(file.Content);
        return new IAccountCatalogRepository.Snapshot(document.Accounts, file.Sha256);
    }

    /// <inheritdoc />
    public async Task<IAccountCatalogRepository.SaveResult> SaveAsync(
        IReadOnlyList<CodexAccountProfile> accounts,
        string expectedSha256,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(accounts);
        CatalogDocument document = new(1, accounts);
        if (!IsValid(document)) {
            throw new ArgumentException("Codex 額度帳號資料不符合 catalog schema。", nameof(accounts));
        }

        byte[] content = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        AtomicJsonFileStore.WriteResult write = await fileStore
            .WriteAsync(FilePath, expectedSha256, content, IsValidDocument, cancellationToken)
            .ConfigureAwait(false);
        if (!write.Saved) {
            return new IAccountCatalogRepository.SaveResult(false, null, write.Conflict);
        }

        return new IAccountCatalogRepository.SaveResult(true, new IAccountCatalogRepository.Snapshot(accounts.ToArray(), write.Sha256), false);
    }

    private static CatalogDocument DeserializeAndValidate(byte[] content) {
        CatalogDocument? document = JsonSerializer.Deserialize<CatalogDocument>(content, JsonOptions);
        if (document is null || !IsValid(document)) {
            throw new InvalidDataException("Codex catalog 內容或 schema 無效。");
        }

        return document;
    }

    private static bool IsValidDocument(byte[] content) {
        try {
            _ = DeserializeAndValidate(content);
            return true;
        } catch (JsonException) {
            return false;
        } catch (InvalidDataException) {
            return false;
        }
    }

    private static bool IsValid(CatalogDocument document) {
        if (document.SchemaVersion != 1 || document.Accounts is null) {
            return false;
        }

        HashSet<Guid> accountIds = [];
        foreach (CodexAccountProfile account in document.Accounts) {
            if (account.AccountId == Guid.Empty
                || !accountIds.Add(account.AccountId)
                || string.IsNullOrWhiteSpace(account.MaskedIdentity)
                || string.IsNullOrWhiteSpace(account.IdentityFingerprint)
                || (account.ExternalAccountId is not null && string.IsNullOrWhiteSpace(account.ExternalAccountId))
                || (account.FallbackIdentityFingerprint is not null && string.IsNullOrWhiteSpace(account.FallbackIdentityFingerprint))
                || account.ProfileRevision < 1
                || account.CreatedAt.Offset != TimeSpan.Zero
                || !string.Equals(account.Status, "connected", StringComparison.Ordinal)) {
                return false;
            }

            if (account.Alias is not null && string.IsNullOrWhiteSpace(account.Alias)) {
                return false;
            }
        }

        return true;
    }

    private sealed record CatalogDocument(int SchemaVersion, IReadOnlyList<CodexAccountProfile> Accounts);
}



