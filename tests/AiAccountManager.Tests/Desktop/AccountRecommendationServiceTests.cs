using CloudyWing.AiAccountManager.Application.Recommendations;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class AccountRecommendationServiceTests {
    [Test]
    public void Recommend_LowCurrentQuota_UsesLowestWindowAndResetTime() {
        Guid currentId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid expectedId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        RecommendationAccount[] accounts = [
            new(currentId, "current", 40, 80, null, true),
            new(expectedId, "earlier", 75, 70, now.AddHours(1), true),
            new(Guid.NewGuid(), "later", 70, 90, now.AddHours(2), true),
            new(Guid.NewGuid(), "unknown reset", 70, 70, null, true),
            new(Guid.NewGuid(), "stale", 95, 95, now, false)
        ];

        RecommendationAccount? result = new AccountRecommendationService().Recommend(currentId, accounts);

        Assert.That(result?.AccountId, Is.EqualTo(expectedId));
    }

    [TestCase(60, 60)]
    [TestCase(50, 80)]
    public void Recommend_CurrentQuotaNotLow_ReturnsNull(int fiveHour, int weekly) {
        Guid currentId = Guid.NewGuid();
        RecommendationAccount[] accounts = [
            new(currentId, "current", fiveHour, weekly, null, true),
            new(Guid.NewGuid(), "candidate", 100, 100, null, true)
        ];

        RecommendationAccount? result = new AccountRecommendationService().Recommend(currentId, accounts);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Recommend_InvalidOrNotBetterCandidates_ReturnsNull() {
        Guid currentId = Guid.NewGuid();
        RecommendationAccount[] accounts = [
            new(currentId, "current", 40, 80, null, true),
            new(Guid.NewGuid(), "stale", 99, 99, null, false),
            new(Guid.NewGuid(), "missing", null, 99, null, true),
            new(Guid.NewGuid(), "invalid", 101, 99, null, true),
            new(Guid.NewGuid(), "equal", 40, 90, null, true)
        ];

        RecommendationAccount? result = new AccountRecommendationService().Recommend(currentId, accounts);

        Assert.That(result, Is.Null);
    }
}
