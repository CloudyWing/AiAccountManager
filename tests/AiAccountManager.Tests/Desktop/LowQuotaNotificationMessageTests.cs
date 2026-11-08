using System.Globalization;
using CloudyWing.AiAccountManager.Application.Notifications;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class LowQuotaNotificationMessageTests {
    [Test]
    public void Compose_CodexWithRecommendation_AppendsRecommendation() {
        string message = LowQuotaNotificationMessage.Compose("codex", "5 小時", 12m, "備用帳號");

        Assert.That(message, Is.EqualTo("Codex 5 小時額度剩 12%，建議切換至 備用帳號"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Compose_ClaudeWithoutRecommendation_OmitsSuffix(string? recommendation) {
        string message = LowQuotaNotificationMessage.Compose("claude", "每週", 15m, recommendation);

        Assert.That(message, Is.EqualTo("Claude 每週額度剩 15%"));
    }

    [Test]
    public void Compose_FractionalPercent_UsesInvariantCulture() {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try {
            string message = LowQuotaNotificationMessage.Compose("codex", "5 小時", 12.5m, null);

            Assert.That(message, Is.EqualTo("Codex 5 小時額度剩 12.5%"));
        } finally {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Test]
    public void Constants_MatchFormalNotification() {
        using (Assert.EnterMultipleScope()) {
            Assert.That(LowQuotaNotificationMessage.Title, Is.EqualTo("額度偏低"));
            Assert.That(LowQuotaNotificationMessage.Type, Is.EqualTo("low-quota"));
        }
    }
}
