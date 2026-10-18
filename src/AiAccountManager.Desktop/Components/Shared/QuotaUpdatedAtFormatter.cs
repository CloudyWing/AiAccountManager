using System.Globalization;

namespace CloudyWing.AiAccountManager.Desktop.Components.Shared;

public static class QuotaUpdatedAtFormatter {
    private static readonly TimeZoneInfo TaipeiTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Taipei Standard Time");

    public static string? Format(DateTimeOffset? updatedAt, DateTimeOffset now) {
        if (updatedAt is null) {
            return null;
        }

        DateTimeOffset localUpdatedAt = TimeZoneInfo.ConvertTime(updatedAt.Value, TaipeiTimeZone);
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(now, TaipeiTimeZone);
        string format = localUpdatedAt.Date == localNow.Date ? "HH:mm" : "MM/dd HH:mm";
        return $"更新於 {localUpdatedAt.ToString(format, CultureInfo.InvariantCulture)}";
    }
}
