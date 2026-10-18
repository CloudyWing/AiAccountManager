using System.Globalization;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 解析 Codex 額度回應中的五小時與每週額度。
/// </summary>
public sealed class QuotaResponseParser {
    /// <summary>
    /// 解析額度回應並檢查時間範圍與數值。
    /// </summary>
    /// <param name="response">額度回應的 JSON 物件。</param>
    /// <returns>完整、不完整或無效的解析結果。</returns>
    public QuotaParseResult Parse(JsonElement response) {
        if (response.ValueKind != JsonValueKind.Object) {
            return QuotaParseResult.Invalid("invalid-response");
        }

        JsonElement selected;
        if (response.TryGetProperty("rateLimitsByLimitId", out JsonElement buckets)
            && buckets.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) {
            if (buckets.ValueKind != JsonValueKind.Object) {
                return QuotaParseResult.Invalid("invalid-buckets");
            }

            if (buckets.EnumerateObject().Any()) {
                if (!buckets.TryGetProperty("codex", out selected)) {
                    return QuotaParseResult.Incomplete(["5 小時", "每週"], "codex-bucket-missing");
                }
            } else if (!response.TryGetProperty("rateLimits", out selected)) {
                return QuotaParseResult.Incomplete(["5 小時", "每週"], "legacy-bucket-missing");
            }
        } else if (!response.TryGetProperty("rateLimits", out selected)) {
            return QuotaParseResult.Incomplete(["5 小時", "每週"], "rate-limits-missing");
        }

        if (selected.ValueKind != JsonValueKind.Object) {
            return QuotaParseResult.Incomplete(["5 小時", "每週"], "rate-limits-empty");
        }

        if (selected.TryGetProperty("limitId", out JsonElement limitId)
            && limitId.ValueKind != JsonValueKind.Null) {
            if (limitId.ValueKind != JsonValueKind.String
                || !string.Equals(limitId.GetString(), "codex", StringComparison.Ordinal)) {
                return QuotaParseResult.Invalid("limit-id-mismatch");
            }
        }

        Dictionary<int, QuotaWindow> windows = [];
        List<string> missing = [];
        bool unidentifiableWindow = false;
        foreach (string propertyName in new[] { "primary", "secondary" }) {
            if (!selected.TryGetProperty(propertyName, out JsonElement window)
                || window.ValueKind == JsonValueKind.Null) {
                unidentifiableWindow = true;
                continue;
            }

            if (window.ValueKind != JsonValueKind.Object
                || !window.TryGetProperty("windowDurationMins", out JsonElement durationElement)
                || durationElement.ValueKind != JsonValueKind.Number
                || !durationElement.TryGetInt32(out int duration)) {
                unidentifiableWindow = true;
                continue;
            }

            if (duration is not (300 or 10080)) {
                unidentifiableWindow = true;
                continue;
            }

            if (!window.TryGetProperty("usedPercent", out JsonElement usedElement)
                || usedElement.ValueKind != JsonValueKind.Number
                || !usedElement.TryGetInt32(out int usedPercent)
                || usedPercent is < 0 or > 100) {
                return QuotaParseResult.Invalid("invalid-used-percent");
            }

            DateTimeOffset? resetsAt = null;
            if (window.TryGetProperty("resetsAt", out JsonElement resetsElement)
                && resetsElement.ValueKind != JsonValueKind.Null) {
                if (resetsElement.ValueKind != JsonValueKind.Number
                    || !resetsElement.TryGetInt64(out long unixSeconds)) {
                    return QuotaParseResult.Invalid("invalid-reset-time");
                }

                try {
                    resetsAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                } catch (ArgumentOutOfRangeException) {
                    return QuotaParseResult.Invalid("invalid-reset-time");
                }
            }

            if (!windows.TryAdd(duration, new QuotaWindow(duration, 100 - usedPercent, resetsAt))) {
                return QuotaParseResult.Incomplete([NameForDuration(duration)], "duplicate-window");
            }
        }

        if (!windows.ContainsKey(300)) {
            missing.Add("5 小時");
        }

        if (!windows.ContainsKey(10080)) {
            missing.Add("每週");
        }

        if (unidentifiableWindow && missing.Count == 0) {
            missing.Add("無法識別的額度區間");
        }

        if (missing.Count > 0) {
            return QuotaParseResult.Incomplete(missing, "quota-window-incomplete");
        }

        return QuotaParseResult.Success(windows[300], windows[10080]) with { ResetCredits = ParseResetCredits(response) };
    }

    private static ResetCreditSummary? ParseResetCredits(JsonElement response) {
        if (!response.TryGetProperty("rateLimitResetCredits", out JsonElement summary)
            || summary.ValueKind != JsonValueKind.Object
            || !summary.TryGetProperty("availableCount", out JsonElement count)
            || count.ValueKind != JsonValueKind.Number
            || !count.TryGetInt64(out long availableCount) || availableCount < 0
            || !summary.TryGetProperty("credits", out JsonElement credits)) {
            return null;
        }

        if (credits.ValueKind == JsonValueKind.Null) {
            return new ResetCreditSummary(availableCount, null);
        }

        if (credits.ValueKind != JsonValueKind.Array) {
            return null;
        }

        List<ResetCredit> parsed = [];
        try {
            foreach (JsonElement credit in credits.EnumerateArray()) {
                if (credit.ValueKind != JsonValueKind.Object
                    || !credit.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(id.GetString())
                    || !credit.TryGetProperty("resetType", out JsonElement type) || type.ValueKind != JsonValueKind.String
                    || !credit.TryGetProperty("status", out JsonElement status) || status.ValueKind != JsonValueKind.String
                    || !credit.TryGetProperty("grantedAt", out JsonElement granted) || granted.ValueKind != JsonValueKind.Number
                    || !granted.TryGetInt64(out long grantedAt)
                    || !credit.TryGetProperty("expiresAt", out JsonElement expires)
                    || (expires.ValueKind != JsonValueKind.Null
                        && (expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt64(out _)))
                    || !TryOptionalString(credit, "title", out string? title)
                    || !TryOptionalString(credit, "description", out string? description)) {
                    return null;
                }

                DateTimeOffset? expiresAt = expires.ValueKind == JsonValueKind.Null
                    ? null : DateTimeOffset.FromUnixTimeSeconds(expires.GetInt64());
                parsed.Add(new ResetCredit(id.GetString()!, type.GetString()!, status.GetString()!,
                    DateTimeOffset.FromUnixTimeSeconds(grantedAt), expiresAt, title, description));
            }
        } catch (ArgumentOutOfRangeException) {
            return null;
        } catch (InvalidOperationException) {
            return null;
        }

        return new ResetCreditSummary(availableCount, parsed);
    }

    private static bool TryOptionalString(JsonElement element, string name, out string? value) {
        value = null;
        if (!element.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null) {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String) {
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static string NameForDuration(int duration) {
        return duration switch {
            300 => "5 小時",
            10080 => "每週",
            _ => duration.ToString(CultureInfo.InvariantCulture) + " 分鐘"
        };
    }
}

/// <summary>
/// 指出額度回應的解析結果。
/// </summary>
public enum QuotaParseStatus {
    /// <summary>
    /// 兩個額度時間範圍皆已取得。
    /// </summary>
    Complete,
    /// <summary>
    /// 額度時間範圍不完整。
    /// </summary>
    Incomplete,
    /// <summary>
    /// 回應包含無效資料。
    /// </summary>
    Invalid
}

/// <summary>
/// 表示額度回應的解析狀態與資料。
/// </summary>
/// <param name="Status">解析狀態。</param>
/// <param name="FiveHour">五小時額度範圍。</param>
/// <param name="Weekly">每週額度範圍。</param>
/// <param name="MissingWindows">缺少的額度時間範圍。</param>
/// <param name="FailureCode">失敗代碼。</param>
public sealed record QuotaParseResult(
    QuotaParseStatus Status,
    QuotaWindow? FiveHour,
    QuotaWindow? Weekly,
    IReadOnlyList<string> MissingWindows,
    string? FailureCode
) {
    /// <summary>取得解析後的重置券資料。</summary>
    public ResetCreditSummary? ResetCredits { get; init; }
    /// <summary>
    /// 建立完整的解析結果。
    /// </summary>
    /// <param name="fiveHour">五小時額度範圍。</param>
    /// <param name="weekly">每週額度範圍。</param>
    /// <returns>包含兩個額度範圍的結果。</returns>
    public static QuotaParseResult Success(QuotaWindow fiveHour, QuotaWindow weekly) {
        return new QuotaParseResult(QuotaParseStatus.Complete, fiveHour, weekly, [], null);
    }

    /// <summary>
    /// 建立不完整的解析結果。
    /// </summary>
    /// <param name="missing">缺少的額度時間範圍。</param>
    /// <param name="failureCode">失敗代碼。</param>
    /// <returns>包含缺少範圍與失敗代碼的結果。</returns>
    public static QuotaParseResult Incomplete(IReadOnlyList<string> missing, string failureCode) {
        return new QuotaParseResult(QuotaParseStatus.Incomplete, null, null, missing, failureCode);
    }

    /// <summary>
    /// 建立無效的解析結果。
    /// </summary>
    /// <param name="failureCode">失敗代碼。</param>
    /// <returns>包含失敗代碼的結果。</returns>
    public static QuotaParseResult Invalid(string failureCode) {
        return new QuotaParseResult(QuotaParseStatus.Invalid, null, null, [], failureCode);
    }
}
