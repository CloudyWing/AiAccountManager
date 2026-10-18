using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Codex;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Codex;

[TestFixture]
public sealed class QuotaResponseParserTests {
    private readonly QuotaResponseParser parser = new();

    [TestCase("{\"availableCount\":2,\"credits\":null}", 2)]
    [TestCase("{\"availableCount\":0,\"credits\":[]}", 0)]
    public void ParsesCreditCountWithoutInventingDetails(string summary, long expectedCount) {
        using JsonDocument document = JsonDocument.Parse(
            """{"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":300},"secondary":{"usedPercent":20,"windowDurationMins":10080}},"rateLimitResetCredits": """
            + summary + "}");
        QuotaParseResult result = parser.Parse(document.RootElement);
        Assert.That(result.Status, Is.EqualTo(QuotaParseStatus.Complete));
        Assert.That(result.ResetCredits?.AvailableCount, Is.EqualTo(expectedCount));
    }

    [Test]
    public void InvalidCreditDataDoesNotInvalidateQuotaWindows() {
        using JsonDocument document = JsonDocument.Parse("""
            {"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":300},
             "secondary":{"usedPercent":20,"windowDurationMins":10080}},
             "rateLimitResetCredits":{"availableCount":-1,"credits":[]}}
            """);
        QuotaParseResult result = parser.Parse(document.RootElement);
        Assert.That(result.Status, Is.EqualTo(QuotaParseStatus.Complete));
        Assert.That(result.ResetCredits, Is.Null);
    }

    [Test]
    public void ParsesVisibleCreditAndNullableExpiry() {
        using JsonDocument document = JsonDocument.Parse("""
            {"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":300},
             "secondary":{"usedPercent":20,"windowDurationMins":10080}},
             "rateLimitResetCredits":{"availableCount":2,"credits":[
               {"id":"a","resetType":"codexRateLimits","status":"available","grantedAt":1900000000,
                "expiresAt":null,"title":"券","description":null}]}}
            """);
        QuotaParseResult result = parser.Parse(document.RootElement);
        Assert.That(result.ResetCredits?.Credits, Has.Count.EqualTo(1));
        Assert.That(result.ResetCredits?.Credits?[0].ExpiresAtUtc, Is.Null);
        Assert.That(result.ResetCredits?.AvailableCount, Is.EqualTo(2));
    }

    [Test]
    public void LegacyNullLimitIdMapsDurationsAndAllowsUnknownResetTime() {
        QuotaParseResult result = ParseFixture("legacy-null-limit-id.json");

        Assert.That(result.Status, Is.EqualTo(QuotaParseStatus.Complete));
        Assert.That(result.FiveHour?.RemainingPercent, Is.EqualTo(75));
        Assert.That(result.FiveHour?.ResetsAtUtc, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1900000000)));
        Assert.That(result.Weekly?.RemainingPercent, Is.EqualTo(60));
        Assert.That(result.Weekly?.ResetsAtUtc, Is.Null);
    }

    [Test]
    public void NonemptyBucketMapUsesOnlyCodexAndDoesNotJoinTopLevelWindows() {
        QuotaParseResult result = ParseFixture("two-buckets.json");

        Assert.That(result.Status, Is.EqualTo(QuotaParseStatus.Complete));
        Assert.That(result.FiveHour?.RemainingPercent, Is.EqualTo(65));
        Assert.That(result.Weekly?.RemainingPercent, Is.EqualTo(85));
    }

    [Test]
    public void NonemptyBucketMapWithoutCodexDoesNotFallbackToValidLegacySnapshot() {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "rateLimits": {
                "limitId": null,
                "primary": { "usedPercent": 10, "windowDurationMins": 300 },
                "secondary": { "usedPercent": 20, "windowDurationMins": 10080 }
              },
              "rateLimitsByLimitId": {
                "cwa": { "limitId": "cwa" }
              }
            }
            """);

        QuotaParseResult result = parser.Parse(document.RootElement);

        Assert.That(result.Status, Is.EqualTo(QuotaParseStatus.Incomplete));
        Assert.That(result.MissingWindows, Is.EquivalentTo(new[] { "5 小時", "每週" }));
    }

    [Test]
    public void MissingWeeklyWindowIsIncomplete() {
        QuotaParseResult result = ParseFixture("missing-weekly-window.json");

        Assert.That(result.Status, Is.EqualTo(QuotaParseStatus.Incomplete));
        Assert.That(result.MissingWindows, Does.Contain("每週"));
        Assert.That(result.FiveHour, Is.Null);
        Assert.That(result.Weekly, Is.Null);
    }

    [Test]
    public void UsedPercentOutsideIntegerRangeRejectsWholeSnapshot() {
        QuotaParseResult result = ParseFixture("invalid-used-percent.json");

        Assert.That(result.Status, Is.EqualTo(QuotaParseStatus.Invalid));
        Assert.That(result.FailureCode, Is.EqualTo("invalid-used-percent"));
    }

    [Test]
    public void DuplicateKnownDurationIsIncompleteInsteadOfChoosingOne() {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "rateLimits": {
                "limitId": "codex",
                "primary": { "usedPercent": 10, "windowDurationMins": 300 },
                "secondary": { "usedPercent": 20, "windowDurationMins": 300 }
              }
            }
            """);

        QuotaParseResult result = parser.Parse(document.RootElement);

        Assert.That(result.Status, Is.EqualTo(QuotaParseStatus.Incomplete));
        Assert.That(result.MissingWindows, Does.Contain("5 小時"));
    }

    [Test]
    public void UnknownDurationDoesNotBecomeOneOfTheKnownWindows() {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "rateLimits": {
                "limitId": null,
                "primary": { "usedPercent": 10, "windowDurationMins": 300 },
                "secondary": { "usedPercent": 20, "windowDurationMins": 1440 }
              }
            }
            """);

        QuotaParseResult result = parser.Parse(document.RootElement);

        Assert.That(result.Status, Is.EqualTo(QuotaParseStatus.Incomplete));
        Assert.That(result.MissingWindows, Does.Contain("每週"));
    }

    [Test]
    public void SelectedBucketWithDifferentLimitIdIsInvalid() {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "rateLimitsByLimitId": {
                "codex": {
                  "limitId": "cwa",
                  "primary": { "usedPercent": 10, "windowDurationMins": 300 },
                  "secondary": { "usedPercent": 20, "windowDurationMins": 10080 }
                }
              }
            }
            """);

        QuotaParseResult result = parser.Parse(document.RootElement);

        Assert.That(result.Status, Is.EqualTo(QuotaParseStatus.Invalid));
        Assert.That(result.FailureCode, Is.EqualTo("limit-id-mismatch"));
    }

    private QuotaParseResult ParseFixture(string fileName) {
        string path = Path.Combine(AppContext.BaseDirectory, "Codex", "Fixtures", "RateLimits", fileName);
        string fixtureJson = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(fixtureJson);
        return parser.Parse(document.RootElement);
    }
}
