using System.IO;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Notifications;
using CloudyWing.AiAccountManager.Windows.Storage;

namespace CloudyWing.AiAccountManager.Windows.Notifications;

/// <summary>以版本化 JSON 保存已通知的額度週期。</summary>
public sealed class JsonNotificationDedupStore : INotificationDedupStore {
    private const string FileName = "notification-dedup.json";
    private readonly AtomicJsonFileStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? sha256;

    /// <summary>初始化通知週期儲存。</summary>
    /// <param name="store">原子檔案儲存介面。</param>
    public JsonNotificationDedupStore(AtomicJsonFileStore store) {
        this.store = store;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<string>> LoadAsync(CancellationToken cancellationToken) {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            AtomicJsonFileStore.ReadResult result = await store.ReadAsync(FileName, cancellationToken).ConfigureAwait(false);
            sha256 = result.Exists ? result.Sha256 : null;
            if (!result.Exists) {
                return [];
            }
            Document? document = JsonSerializer.Deserialize<Document>(result.Content);
            if (document is null || document.SchemaVersion != 1 || document.Keys is null) {
                throw new InvalidDataException("通知週期記錄格式無效。");
            }
            return document.Keys;
        } finally {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken) {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            byte[] content = JsonSerializer.SerializeToUtf8Bytes(new Document(1, keys.ToArray()));
            AtomicJsonFileStore.WriteResult result = await store.WriteAsync(
                FileName, sha256, content, bytes => JsonSerializer.Deserialize<Document>(bytes)?.SchemaVersion == 1, cancellationToken
            ).ConfigureAwait(false);
            if (!result.Saved) {
                throw new IOException("通知週期記錄已由其他程序修改。");
            }
            sha256 = result.Sha256;
        } finally {
            gate.Release();
        }
    }

    private sealed record Document(int SchemaVersion, string[] Keys);
}
