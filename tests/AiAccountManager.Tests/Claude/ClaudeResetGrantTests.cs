using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Claude;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Claude;

[TestFixture]
public sealed class ClaudeResetGrantTests {
    [Test]
    public void EligibleGrantIsParsedWithQuota() {
        using JsonDocument json = JsonDocument.Parse("""
            {"five_hour":{"utilization":20},"seven_day":{"utilization":30},
             "cedar_ember":{"eligible":true,"grants":[{"id":"one","label":"額外重置",
             "resets_total":3,"resets_left":2,"starts_at":null,"ends_at":"2026-10-28T06:29:38Z",
             "usable_now":true,"use_requires_limit":false,"paused":false,"type":"session"}]}}
            """);
        ClaudeQuotaResponseParser.ParseResult result = ClaudeQuotaResponseParser.Parse(json.RootElement, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.That(result.IsComplete, Is.True);
        Assert.That(result.Snapshot?.ResetGrants, Has.Count.EqualTo(1));
        Assert.That(result.Snapshot?.ResetGrants?[0].ResetsLeft, Is.EqualTo(2));
        Assert.That(result.Snapshot?.ResetGrants?[0].EndsAtUtc, Is.EqualTo(DateTimeOffset.Parse("2026-10-28T06:29:38Z")));
    }

    [TestCase("null")]
    [TestCase("{\"eligible\":false,\"grants\":[]}")]
    [TestCase("{\"eligible\":true,\"grants\":[{}]}")]
    public void InvalidOrIneligibleGrantsDoNotAffectQuota(string cedar) {
        using JsonDocument json = JsonDocument.Parse("""{"five_hour":{"utilization":20},"seven_day":{"utilization":30},"cedar_ember": """ + cedar + "}");
        ClaudeQuotaResponseParser.ParseResult result = ClaudeQuotaResponseParser.Parse(json.RootElement, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.That(result.IsComplete, Is.True);
        Assert.That(result.Snapshot?.ResetGrants, Is.Null);
        Assert.That(result.Snapshot?.Windows, Has.Count.EqualTo(2));
    }
}
