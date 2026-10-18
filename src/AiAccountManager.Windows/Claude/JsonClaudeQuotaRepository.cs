using System.Text.Json;
using System.Text.Json.Serialization;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Windows.Storage;

namespace CloudyWing.AiAccountManager.Windows.Claude;

/// <summary>
/// 以 profile 為單位保存 Claude 額度查詢快照。
/// </summary>
public sealed class JsonClaudeQuotaRepository : IClaudeQuotaRepository {
    private const int MaximumSnapshotBytes = 1024 * 1024;
    private const int MaximumWindows = 32;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly AtomicJsonFileStore fileStore;

    /// <summary>
    /// 建立 Claude 額度 JSON repository。
    /// </summary>
    /// <param name="fileStore">提供原子 JSON 寫入的檔案 store。</param>
    public JsonClaudeQuotaRepository(AtomicJsonFileStore fileStore) {
        this.fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
    }

    /// <inheritdoc />
    public async Task<IClaudeQuotaRepository.Snapshot?> LoadAsync(
        Guid profileId,
        CancellationToken cancellationToken
    ) {
        ValidateProfileId(profileId);
        string relativePath = GetRelativePath(profileId);
        AtomicJsonFileStore.ReadResult file = await fileStore.ReadAsync(relativePath, cancellationToken)
            .ConfigureAwait(false);
        if (!file.Exists) {
            return null;
        }

        if (file.Content.Length > MaximumSnapshotBytes) {
            throw new InvalidDataException("Claude quota snapshot exceeded its supported size limit.");
        }

        QuotaDocument document = Deserialize(file.Content);
        if (document.SchemaVersion != 1
            || document.View is null
            || document.View.ProfileId != profileId
            || !IsValidView(document.View)) {
            throw new InvalidDataException("Claude quota snapshot content or schema is invalid.");
        }

        return new IClaudeQuotaRepository.Snapshot(document.View, file.Sha256);
    }

    /// <inheritdoc />
    public async Task<IClaudeQuotaRepository.SaveResult> SaveAsync(
        ClaudeQuotaSnapshot.View view,
        string? expectedSha256,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(view);
        ValidateProfileId(view.ProfileId);
        if (expectedSha256 is not null && !IsSha256(expectedSha256)) {
            throw new ArgumentException("The Claude quota snapshot hash is invalid.", nameof(expectedSha256));
        }

        if (!IsValidView(view)) {
            throw new ArgumentException("Claude quota snapshot is invalid.", nameof(view));
        }

        string relativePath = GetRelativePath(view.ProfileId);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new QuotaDocument(1, view), JsonOptions);
        AtomicJsonFileStore.WriteResult write = await fileStore.WriteAsync(
            relativePath,
            expectedSha256,
            content,
            IsValidDocument,
            cancellationToken
        ).ConfigureAwait(false);
        if (!write.Saved) {
            return new IClaudeQuotaRepository.SaveResult(false, null, true);
        }

        return new IClaudeQuotaRepository.SaveResult(
            true,
            new IClaudeQuotaRepository.Snapshot(view, write.Sha256),
            false
        );
    }

    private static JsonSerializerOptions CreateJsonOptions() {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter<ClaudeQuotaSnapshot.RefreshStatus>(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static bool IsValidDocument(byte[] content) {
        try {
            QuotaDocument document = Deserialize(content);
            return document.SchemaVersion == 1 && document.View is not null && IsValidView(document.View);
        } catch (JsonException) {
            return false;
        } catch (InvalidDataException) {
            return false;
        }
    }

    private static bool IsValidView(ClaudeQuotaSnapshot.View view) {
        if (view.ProfileId == Guid.Empty
            || !Enum.IsDefined(view.Status)
            || (view.LastAttemptAtUtc is DateTimeOffset attempt && !IsUtc(attempt))
            || (view.Message is not null && view.Message.Length > 500)) {
            return false;
        }

        if (view.Snapshot is null) {
            return view.Status is not (ClaudeQuotaSnapshot.RefreshStatus.Fresh or ClaudeQuotaSnapshot.RefreshStatus.Stale);
        }

        if (view.Snapshot.ProfileId != view.ProfileId
            || !IsUtc(view.Snapshot.RetrievedAtUtc)
            || view.Snapshot.Windows is null
            || view.Snapshot.Windows.Count is < 1 or > MaximumWindows) {
            return false;
        }

        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (ClaudeQuotaSnapshot.Window? window in view.Snapshot.Windows) {
            if (window is null
                || string.IsNullOrWhiteSpace(window.Key)
                || window.Key.Length > 100
                || !keys.Add(window.Key)
                || window.DurationMinutes is < 1 or > 525_600
                || window.UtilizationPercent is < 0 or > 100
                || (window.ResetsAtUtc is DateTimeOffset reset && !IsUtc(reset))) {
                return false;
            }
        }

        return true;
    }

    private static QuotaDocument Deserialize(byte[] content) {
        try {
            return JsonSerializer.Deserialize<QuotaDocument>(content, JsonOptions)
                ?? throw new InvalidDataException("Claude quota snapshot is empty.");
        } catch (JsonException failure) {
            throw new InvalidDataException("Claude quota snapshot is malformed.", failure);
        }
    }

    private static string GetRelativePath(Guid profileId) {
        ValidateProfileId(profileId);
        return Path.Combine("claude-quota", "quota-" + profileId.ToString("N") + ".json");
    }

    private static void ValidateProfileId(Guid profileId) {
        if (profileId == Guid.Empty) {
            throw new ArgumentException("Claude profile ID must not be empty.", nameof(profileId));
        }
    }

    private static bool IsSha256(string value) {
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private sealed record QuotaDocument(int SchemaVersion, ClaudeQuotaSnapshot.View? View);
}
