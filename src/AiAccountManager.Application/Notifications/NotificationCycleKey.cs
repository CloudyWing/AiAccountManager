using System.Globalization;

namespace CloudyWing.AiAccountManager.Application.Notifications;

/// <summary>建立及遷移通知週期識別字。</summary>
public static class NotificationCycleKey {
    /// <summary>建立通知週期識別字。</summary>
    public static string Create(string provider, Guid accountId, string windowKey, DateTimeOffset? resetsAtUtc) {
        return $"{provider}/{accountId:N}/{windowKey}/{FormatCycle(resetsAtUtc)}";
    }

    /// <summary>以 UTC 分鐘格式表示週期。</summary>
    public static string FormatCycle(DateTimeOffset? resetsAtUtc) {
        return resetsAtUtc?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm'Z'", CultureInfo.InvariantCulture) ?? "no-reset";
    }

    /// <summary>將舊格式週期遷移為分鐘精度。</summary>
    public static bool TryMigrateLegacy(string key, out string migrated) {
        migrated = key;
        int separator = key.LastIndexOf('/');
        if (separator < 0 || !DateTimeOffset.TryParseExact(key[(separator + 1)..], "O", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTimeOffset reset)) {
            return false;
        }
        migrated = key[..(separator + 1)] + FormatCycle(reset);
        return true;
    }
}
