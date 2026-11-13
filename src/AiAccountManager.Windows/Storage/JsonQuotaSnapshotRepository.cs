using System.Text.Json;
using System.Text.Json.Serialization;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;

namespace CloudyWing.AiAccountManager.Windows.Storage;

/// <summary>
/// 以 JSON 檔案保存各帳號的額度快照。
/// </summary>
public sealed class JsonQuotaSnapshotRepository : IQuotaSnapshotRepository {
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly AtomicJsonFileStore fileStore;

    /// <summary>
    /// 初始化額度快照儲存庫。
    /// </summary>
    /// <param name="fileStore">原子檔案儲存體。</param>
    public JsonQuotaSnapshotRepository(AtomicJsonFileStore fileStore) {
        this.fileStore = fileStore;
    }

    /// <inheritdoc />
    public async Task<IQuotaSnapshotRepository.Snapshot?> LoadAsync(
        Guid accountId,
        CancellationToken cancellationToken
    ) {
        ValidateAccountId(accountId);
        string relativePath = GetRelativePath(accountId);
        AtomicJsonFileStore.ReadResult file = await fileStore
            .ReadAsync(relativePath, cancellationToken)
            .ConfigureAwait(false);
        if (!file.Exists) {
            return null;
        }

        QuotaSnapshotDocument? document = JsonSerializer.Deserialize<QuotaSnapshotDocument>(file.Content, JsonOptions);
        if (document is null || document.SchemaVersion != 1 || document.Snapshot is null
            || document.Snapshot.AccountId != accountId || !IsValid(document.Snapshot)) {
            throw new InvalidDataException("Codex quota snapshot content or schema is invalid.");
        }

        return new IQuotaSnapshotRepository.Snapshot(document.Snapshot, file.Sha256);
    }

    /// <inheritdoc />
    public async Task<IQuotaSnapshotRepository.SaveResult> SaveAsync(
        QuotaSnapshotRecord snapshot,
        string? expectedSha256,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateAccountId(snapshot.AccountId);
        if (!IsValid(snapshot)) {
            throw new ArgumentException("Codex quota snapshot is invalid.", nameof(snapshot));
        }

        string relativePath = GetRelativePath(snapshot.AccountId);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new QuotaSnapshotDocument(1, snapshot), JsonOptions);
        AtomicJsonFileStore.WriteResult write = await fileStore
            .WriteAsync(relativePath, expectedSha256, content, IsValidDocument, cancellationToken)
            .ConfigureAwait(false);
        if (!write.Saved) {
            return new IQuotaSnapshotRepository.SaveResult(false, null, write.Conflict);
        }

        return new IQuotaSnapshotRepository.SaveResult(
            true,
            new IQuotaSnapshotRepository.Snapshot(snapshot, write.Sha256),
            false
        );
    }

    private static JsonSerializerOptions CreateJsonOptions() {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter<QuotaRefreshStatus>(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static bool IsValidDocument(byte[] content) {
        try {
            QuotaSnapshotDocument? document = JsonSerializer.Deserialize<QuotaSnapshotDocument>(content, JsonOptions);
            return document is { SchemaVersion: 1, Snapshot: not null } && IsValid(document.Snapshot);
        } catch (JsonException) {
            return false;
        }
    }

    private static bool IsValid(QuotaSnapshotRecord snapshot) {
        if (snapshot.AccountId == Guid.Empty
            || string.IsNullOrWhiteSpace(snapshot.IdentityFingerprint)
            || snapshot.ProfileRevision < 1) {
            return false;
        }

        if (snapshot.LastSuccessfulSnapshot is QuotaSuccessfulSnapshot success
            && (!IsUtc(success.RetrievedAtUtc) || !HasValidWindows(success))) {
            return false;
        }

        if (snapshot.LastAttempt is QuotaRefreshAttempt attempt
            && (!Enum.IsDefined(attempt.Status)
                || !IsUtc(attempt.AttemptedAtUtc)
                || attempt.FailureCount is < 0 or > 30
                || (attempt.RetryAtUtc is DateTimeOffset retryAt && !IsUtc(retryAt))
                || attempt.MissingWindows is null
                || attempt.MissingWindows.Any(string.IsNullOrWhiteSpace))) {
            return false;
        }

        return true;
    }

    private static bool HasValidWindows(QuotaSuccessfulSnapshot success) {
        if (success.FreePlanWindow is not null) {
            return success.FiveHour is null
                && success.Weekly is null
                && IsValidWindow(success.FreePlanWindow, QuotaWindowLabels.FreePlanMinutes);
        }

        return IsValidWindow(success.FiveHour, QuotaWindowLabels.FiveHourMinutes)
            && IsValidWindow(success.Weekly, QuotaWindowLabels.WeeklyMinutes);
    }

    private static bool IsValidWindow(QuotaWindow? window, int expectedDuration) {
        return window is not null
            && window.DurationMinutes == expectedDuration
            && window.RemainingPercent is >= 0 and <= 100
            && (window.ResetsAtUtc is null || IsUtc(window.ResetsAtUtc.Value));
    }

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private static string GetRelativePath(Guid accountId) {
        ValidateAccountId(accountId);
        return Path.Combine("quota", "quota-" + accountId.ToString("N") + ".json");
    }

    private static void ValidateAccountId(Guid accountId) {
        if (accountId == Guid.Empty) {
            throw new ArgumentException("Account ID must not be empty.", nameof(accountId));
        }
    }

    private sealed record QuotaSnapshotDocument(int SchemaVersion, QuotaSnapshotRecord? Snapshot);
}
