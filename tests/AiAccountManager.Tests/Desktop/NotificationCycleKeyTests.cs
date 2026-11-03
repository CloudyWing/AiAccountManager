using CloudyWing.AiAccountManager.Application.Notifications;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class NotificationCycleKeyTests {
    [Test]
    public void Create_NormalizesOffsetAndSeconds() {
        Guid id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        DateTimeOffset reset = new(2026, 11, 1, 8, 30, 59, TimeSpan.FromHours(8));
        Assert.That(NotificationCycleKey.Create("codex", id, "weekly", reset),
            Is.EqualTo("codex/11111111111111111111111111111111/weekly/2026-11-01T00:30Z"));
        Assert.That(NotificationCycleKey.FormatCycle(null), Is.EqualTo("no-reset"));
    }

    [TestCase("codex/id/weekly/no-reset")]
    [TestCase("codex/id/weekly/2026-11-01T00:30Z")]
    [TestCase("invalid")]
    public void TryMigrateLegacy_NonLegacyPreservesKey(string key) {
        Assert.That(NotificationCycleKey.TryMigrateLegacy(key, out string migrated), Is.False);
        Assert.That(migrated, Is.EqualTo(key));
    }

    [Test]
    public void TryMigrateLegacy_RoundTripTimestamp_UsesUtcMinute() {
        Assert.That(NotificationCycleKey.TryMigrateLegacy("codex/id/weekly/2026-11-01T08:30:59.1234567+08:00", out string migrated), Is.True);
        Assert.That(migrated, Is.EqualTo("codex/id/weekly/2026-11-01T00:30Z"));
    }
}
