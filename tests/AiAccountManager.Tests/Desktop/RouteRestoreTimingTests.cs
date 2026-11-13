using CloudyWing.AiAccountManager.Desktop;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class RouteRestoreTimingTests {
    [TestCase(1000, 100)]
    [TestCase(100, 100)]
    [TestCase(40, 40)]
    [TestCase(1, 1)]
    public void GetRouteRetryDelay_RemainingTime_NeverExceedsRemaining(int remainingMilliseconds, int expectedMilliseconds) {
        TimeSpan? delay = MainWindow.GetRouteRetryDelay(TimeSpan.FromMilliseconds(remainingMilliseconds));

        Assert.That(delay, Is.EqualTo(TimeSpan.FromMilliseconds(expectedMilliseconds)));
    }

    [TestCase(0)]
    [TestCase(-50)]
    public void GetRouteRetryDelay_DeadlineReached_ReturnsNull(int remainingMilliseconds) {
        TimeSpan? delay = MainWindow.GetRouteRetryDelay(TimeSpan.FromMilliseconds(remainingMilliseconds));

        Assert.That(delay, Is.Null);
    }
}
