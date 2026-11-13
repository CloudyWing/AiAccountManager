using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 解析 Claude OAuth usage endpoint 回傳的新舊 quota window 格式。
/// </summary>
public static class ClaudeQuotaResponseParser {
    private const int SessionWindowMinutes = 5 * 60;
    private const int WeeklyWindowMinutes = 7 * 24 * 60;

    /// <summary>
    /// 將 endpoint payload 轉為明確列出的 quota windows。
    /// </summary>
    /// <param name="payload">OAuth usage endpoint 回應。</param>
    /// <param name="profileId">額度所屬 profile 識別碼。</param>
    /// <param name="retrievedAtUtc">呼叫端取得回應的 UTC 時間。</param>
    /// <returns>完整 snapshot 或標記為 incomplete 的部分解析結果。</returns>
    public static ParseResult Parse(JsonElement payload, Guid profileId, DateTimeOffset retrievedAtUtc) {
        if (payload.ValueKind != JsonValueKind.Object) {
            return ParseResult.Incomplete("額度資料格式無法辨識，請稍後再試。");
        }

        Dictionary<string, ClaudeQuotaSnapshot.Window> windows = new(StringComparer.Ordinal);
        bool complete = true;

        if (payload.TryGetProperty("limits", out JsonElement limits)) {
            if (limits.ValueKind != JsonValueKind.Array) {
                complete = false;
            } else {
                foreach (JsonElement limit in limits.EnumerateArray()) {
                    if (!TryParseLimit(limit, out ClaudeQuotaSnapshot.Window? window)) {
                        complete = false;
                        continue;
                    }

                    windows[window.Key] = window;
                }
            }
        }

        ParseLegacyWindow(payload, "five_hour", "session", SessionWindowMinutes, windows, ref complete);
        ParseLegacyWindow(payload, "seven_day", "weekly_all", WeeklyWindowMinutes, windows, ref complete);
        ParseLegacyWindow(payload, "seven_day_opus", "weekly_opus", WeeklyWindowMinutes, windows, ref complete);
        ParseLegacyWindow(payload, "seven_day_sonnet", "weekly_sonnet", WeeklyWindowMinutes, windows, ref complete);

        if (!windows.ContainsKey("session") || !windows.ContainsKey("weekly_all")) {
            complete = false;
        }

        if (!complete) {
            return new(false, null, windows.Values.OrderBy(window => window.DurationMinutes).ThenBy(window => window.Key).ToArray(),
                "額度資料格式無法辨識，請稍後再試。");
        }

        ClaudeQuotaSnapshot snapshot = new(
            profileId,
            retrievedAtUtc,
            windows.Values.OrderBy(window => window.DurationMinutes).ThenBy(window => window.Key).ToArray(),
            ParseGrants(payload)
        );
        return new(true, snapshot, snapshot.Windows, null);
    }

    private static IReadOnlyList<ClaudeResetGrant>? ParseGrants(JsonElement payload) {
        if (!payload.TryGetProperty("cedar_ember", out JsonElement cedar) || cedar.ValueKind != JsonValueKind.Object
            || !cedar.TryGetProperty("eligible", out JsonElement eligible) || eligible.ValueKind != JsonValueKind.True
            || !cedar.TryGetProperty("grants", out JsonElement grants) || grants.ValueKind != JsonValueKind.Array
        ) {
            return null;
        }

        List<ClaudeResetGrant> result = [];
        foreach (JsonElement grant in grants.EnumerateArray()) {
            if (grant.ValueKind != JsonValueKind.Object
                || !TryReadString(grant, "id", out string? id) || !TryReadString(grant, "label", out string? label)
                || !TryReadString(grant, "type", out string? type)
                || !grant.TryGetProperty("resets_total", out JsonElement total) || total.ValueKind != JsonValueKind.Number
                || !total.TryGetInt64(out long totalCount) || totalCount < 0
                || !grant.TryGetProperty("resets_left", out JsonElement left) || left.ValueKind != JsonValueKind.Number
                || !left.TryGetInt64(out long leftCount) || leftCount < 0 || leftCount > totalCount
                || !TryGrantTime(grant, "starts_at", out DateTimeOffset? starts)
                || !TryGrantTime(grant, "ends_at", out DateTimeOffset? ends)
                || !TryGrantBool(grant, "usable_now", out bool usable)
                || !TryGrantBool(grant, "use_requires_limit", out bool requiresLimit)
                || !TryGrantBool(grant, "paused", out bool paused)
            ) {
                return null;
            }
            result.Add(new ClaudeResetGrant(id, label, totalCount, leftCount, starts, ends, usable, requiresLimit, paused, type));
        }
        return result;
    }

    private static bool TryGrantBool(JsonElement element, string name, out bool value) {
        value = false;
        if (!element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
        ) {
            return false;
        }
        value = property.GetBoolean();
        return true;
    }

    private static bool TryGrantTime(JsonElement element, string name, out DateTimeOffset? value) {
        value = null;
        if (!element.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null) {
            return true;
        }
        if (property.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(
            property.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed
        )
        ) {
            return false;
        }
        value = parsed;
        return true;
    }

    private static bool TryParseLimit(
        JsonElement limit,
        [NotNullWhen(true)] out ClaudeQuotaSnapshot.Window? window
    ) {
        window = null;
        if (limit.ValueKind != JsonValueKind.Object
            || !TryReadString(limit, "kind", out string? kind)
            || !TryReadDecimal(limit, "percent", out decimal utilization)
            || utilization is < 0 or > 100
            || !TryReadReset(limit, out DateTimeOffset? resetAtUtc)) {
            return false;
        }

        switch (kind) {
            case "session":
                window = new("session", SessionWindowMinutes, utilization, resetAtUtc);
                return true;
            case "weekly_all":
                window = new("weekly_all", WeeklyWindowMinutes, utilization, resetAtUtc);
                return true;
            case "weekly_scoped":
                if (!limit.TryGetProperty("scope", out JsonElement scope)
                    || scope.ValueKind != JsonValueKind.Object
                    || !scope.TryGetProperty("model", out JsonElement model)
                    || model.ValueKind != JsonValueKind.Object
                    || !TryReadString(model, "display_name", out string? displayName)
                    || !TryReadString(limit, "group", out string? group)
                    || !group.StartsWith("weekly", StringComparison.Ordinal)) {
                    return false;
                }

                string normalizedModelName = displayName.Trim();
                if (normalizedModelName.Length == 0) {
                    return false;
                }

                window = new(
                    $"weekly_scoped:{normalizedModelName}",
                    WeeklyWindowMinutes,
                    utilization,
                    resetAtUtc
                );
                return true;
            default:
                return false;
        }
    }

    private static void ParseLegacyWindow(
        JsonElement payload,
        string propertyName,
        string key,
        int durationMinutes,
        IDictionary<string, ClaudeQuotaSnapshot.Window> windows,
        ref bool complete
    ) {
        if (!payload.TryGetProperty(propertyName, out JsonElement element) || element.ValueKind == JsonValueKind.Null) {
            return;
        }

        if (element.ValueKind != JsonValueKind.Object
            || !TryReadDecimal(element, "utilization", out decimal utilization)
            || utilization is < 0 or > 100
            || !TryReadReset(element, out DateTimeOffset? resetAtUtc)) {
            complete = false;
            return;
        }

        if (!windows.ContainsKey(key)) {
            windows.Add(key, new(key, durationMinutes, utilization, resetAtUtc));
        }
    }

    private static bool TryReadString(
        JsonElement element,
        string propertyName,
        [NotNullWhen(true)] out string? value
    ) {
        value = null;
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String) {
            return false;
        }

        string? candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate)) {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool TryReadDecimal(JsonElement element, string propertyName, out decimal value) {
        value = default;
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDecimal(out value);
    }

    private static bool TryReadReset(JsonElement element, out DateTimeOffset? resetAtUtc) {
        resetAtUtc = null;
        if (!element.TryGetProperty("resets_at", out JsonElement reset)) {
            return true;
        }

        if (reset.ValueKind == JsonValueKind.Null) {
            return true;
        }

        if (reset.ValueKind != JsonValueKind.String) {
            return false;
        }

        string? value = reset.GetString();
        if (value is null) {
            return false;
        }

        if (!DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed
        )) {
            return false;
        }

        resetAtUtc = parsed;
        return true;
    }

    /// <summary>
    /// 提供解析完成度、quota snapshot 與供診斷使用的非敏感視窗資料。
    /// </summary>
    /// <param name="IsComplete">必要 session 與 weekly window 都已解析。</param>
    /// <param name="Snapshot">格式完整時建立的 quota snapshot。</param>
    /// <param name="Windows">已識別的視窗。</param>
    /// <param name="Message">不含回應本文的安全說明。</param>
    public sealed record ParseResult(
        bool IsComplete,
        ClaudeQuotaSnapshot? Snapshot,
        IReadOnlyList<ClaudeQuotaSnapshot.Window> Windows,
        string? Message
    ) {
        internal static ParseResult Incomplete(string message) {
            return new(false, null, Array.Empty<ClaudeQuotaSnapshot.Window>(), message);
        }
    }
}
