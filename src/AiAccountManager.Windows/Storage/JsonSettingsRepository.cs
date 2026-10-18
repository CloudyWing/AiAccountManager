using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Settings;

namespace CloudyWing.AiAccountManager.Windows.Storage;

/// <summary>
/// 以 JSON 檔案保存應用程式設定。
/// </summary>
public sealed class JsonSettingsRepository : ISettingsRepository {
    private const string FilePath = "settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        WriteIndented = true
    };
    private readonly AtomicJsonFileStore fileStore;

    /// <summary>
    /// 初始化設定儲存庫。
    /// </summary>
    /// <param name="fileStore">原子檔案儲存體。</param>
    public JsonSettingsRepository(AtomicJsonFileStore fileStore) {
        this.fileStore = fileStore;
    }

    /// <inheritdoc />
    public async Task<ISettingsRepository.Snapshot> LoadAsync(CancellationToken cancellationToken) {
        AtomicJsonFileStore.ReadResult file = await fileStore
            .ReadAsync(FilePath, cancellationToken)
            .ConfigureAwait(false);
        if (!file.Exists) {
            byte[] defaults = JsonSerializer.SerializeToUtf8Bytes(AppSettings.Defaults, JsonOptions);
            AtomicJsonFileStore.WriteResult write = await fileStore
                .WriteAsync(FilePath, null, defaults, IsValidDocument, cancellationToken)
                .ConfigureAwait(false);
            if (write.Conflict) {
                return await LoadAsync(cancellationToken).ConfigureAwait(false);
            }

            return new ISettingsRepository.Snapshot(AppSettings.Defaults, write.Sha256);
        }

        AppSettings settings = DeserializeAndValidate(file.Content);
        return new ISettingsRepository.Snapshot(settings, file.Sha256);
    }

    /// <inheritdoc />
    public async Task<ISettingsRepository.SaveResult> SaveAsync(
        AppSettings settings,
        string expectedSha256,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid(out string validationError)) {
            throw new ArgumentException(validationError, nameof(settings));
        }

        byte[] content = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        AtomicJsonFileStore.WriteResult write = await fileStore
            .WriteAsync(FilePath, expectedSha256, content, IsValidDocument, cancellationToken)
            .ConfigureAwait(false);
        if (!write.Saved) {
            return new ISettingsRepository.SaveResult(false, null, write.Conflict);
        }

        return new ISettingsRepository.SaveResult(true, new ISettingsRepository.Snapshot(settings, write.Sha256), false);
    }

    private static AppSettings DeserializeAndValidate(byte[] content) {
        AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(content, JsonOptions);
        if (settings is null) {
            throw new InvalidDataException("設定檔內容無效。");
        }

        if (!settings.IsValid(out string error)) {
            throw new InvalidDataException(error);
        }

        return settings;
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
}
