using CloudyWing.AiAccountManager.Desktop.Components.Shared;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class QuotaUpdatedAtFormatterTests {
    [TestCase("2026-10-28T14:20:00Z", "更新於 22:20")]
    [TestCase("2026-10-27T14:20:00Z", "更新於 10/27 22:20")]
    [TestCase(null, null)]
    public void QuotaUpdatedAt_FormatsTaipeiDateOrReturnsNull(string? updatedAt, string? expected) {
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-28T15:00:00Z");
        DateTimeOffset? value = updatedAt is null ? null : DateTimeOffset.Parse(updatedAt);

        string? actual = QuotaUpdatedAtFormatter.Format(value, now);

        Assert.That(actual, Is.EqualTo(expected));
    }
}
