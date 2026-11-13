using System.Diagnostics;
using CloudyWing.AiAccountManager.Desktop;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class RouteRestoreTimingTests {
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    [TestCase(1000, 100)]
    [TestCase(100, 100)]
    [TestCase(40, 40)]
    [TestCase(1, 1)]
    public void GetRetryDelay_RemainingTime_NeverExceedsRemaining(int remainingMilliseconds, int expectedMilliseconds) {
        TimeSpan? delay = WebViewRouteRestorer.GetRetryDelay(TimeSpan.FromMilliseconds(remainingMilliseconds), RetryDelay);

        Assert.That(delay, Is.EqualTo(TimeSpan.FromMilliseconds(expectedMilliseconds)));
    }

    [TestCase(0)]
    [TestCase(-50)]
    public void GetRetryDelay_DeadlineReached_ReturnsNull(int remainingMilliseconds) {
        TimeSpan? delay = WebViewRouteRestorer.GetRetryDelay(TimeSpan.FromMilliseconds(remainingMilliseconds), RetryDelay);

        Assert.That(delay, Is.Null);
    }

    [Test]
    public async Task RestoreAsync_DispatchSucceeds_NavigatesOnce() {
        List<string> logs = [];
        int navigations = 0;
        int dispatches = 0;

        await WebViewRouteRestorer.RestoreAsync(
            () => true,
            shouldNavigate => {
                dispatches++;
                if (shouldNavigate()) {
                    navigations++;
                }

                return Task.FromResult(true);
            },
            CreateTiming(),
            logs.Add
        );

        Assert.Multiple(() => {
            Assert.That(logs, Is.EqualTo(new[] { "rebuild.route-restored" }));
            Assert.That(navigations, Is.EqualTo(1));
            Assert.That(dispatches, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RestoreAsync_TargetInvalid_SkipsWithoutDispatch() {
        List<string> logs = [];
        int dispatches = 0;

        await WebViewRouteRestorer.RestoreAsync(
            () => false,
            _ => {
                dispatches++;
                return Task.FromResult(true);
            },
            CreateTiming(),
            logs.Add
        );

        Assert.Multiple(() => {
            Assert.That(logs, Is.EqualTo(new[] { "rebuild.route-restore-skipped" }));
            Assert.That(dispatches, Is.Zero);
        });
    }

    [Test]
    public async Task RestoreAsync_TargetBecomesInvalidInsideDispatch_DoesNotNavigate() {
        List<string> logs = [];
        bool canRestore = true;
        int navigations = 0;

        await WebViewRouteRestorer.RestoreAsync(
            () => canRestore,
            shouldNavigate => {
                canRestore = false;
                if (shouldNavigate()) {
                    navigations++;
                }

                return Task.FromResult(true);
            },
            CreateTiming(),
            logs.Add
        );

        Assert.Multiple(() => {
            Assert.That(logs, Is.EqualTo(new[] { "rebuild.route-restore-skipped" }));
            Assert.That(navigations, Is.Zero);
        });
    }

    [Test]
    public async Task RestoreAsync_RendererNotReady_RetriesUntilAttemptsExhausted() {
        List<string> logs = [];
        int dispatches = 0;

        await WebViewRouteRestorer.RestoreAsync(
            () => true,
            _ => {
                dispatches++;
                return Task.FromResult(false);
            },
            CreateTiming(retryDelayMilliseconds: 1, maxAttempts: 3),
            logs.Add
        );

        Assert.Multiple(() => {
            Assert.That(logs, Is.EqualTo(new[] { "rebuild.route-restore-failed:attempts-exhausted:3" }));
            Assert.That(dispatches, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task RestoreAsync_RendererReadyOnLaterAttempt_Navigates() {
        List<string> logs = [];
        int dispatches = 0;
        int navigations = 0;

        await WebViewRouteRestorer.RestoreAsync(
            () => true,
            shouldNavigate => {
                dispatches++;
                if (dispatches < 3) {
                    return Task.FromResult(false);
                }

                if (shouldNavigate()) {
                    navigations++;
                }

                return Task.FromResult(true);
            },
            CreateTiming(retryDelayMilliseconds: 1),
            logs.Add
        );

        Assert.Multiple(() => {
            Assert.That(logs, Is.EqualTo(new[] { "rebuild.route-restored" }));
            Assert.That(dispatches, Is.EqualTo(3));
            Assert.That(navigations, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RestoreAsync_DispatchNeverCompletes_StopsAtDeadline() {
        List<string> logs = [];
        TimeSpan deadline = TimeSpan.FromMilliseconds(300);
        Stopwatch stopwatch = Stopwatch.StartNew();

        await WebViewRouteRestorer.RestoreAsync(
            () => true,
            _ => new TaskCompletionSource<bool>().Task,
            new WebViewRouteRestorer.Timing(deadline, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(100), 50),
            logs.Add
        );
        stopwatch.Stop();

        Assert.Multiple(() => {
            Assert.That(logs, Is.EqualTo(new[] { "rebuild.route-restore-failed:deadline-exceeded" }));
            Assert.That(stopwatch.Elapsed, Is.LessThan(deadline + TimeSpan.FromSeconds(1)));
        });
    }

    [Test]
    public async Task RestoreAsync_NavigatedButDispatchTimesOut_ReportsRestoredWithoutRetry() {
        List<string> logs = [];
        int dispatches = 0;
        int navigations = 0;

        await WebViewRouteRestorer.RestoreAsync(
            () => true,
            shouldNavigate => {
                dispatches++;
                if (shouldNavigate()) {
                    navigations++;
                }

                return new TaskCompletionSource<bool>().Task;
            },
            new WebViewRouteRestorer.Timing(
                TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), 50
            ),
            logs.Add
        );

        Assert.Multiple(() => {
            Assert.That(logs, Is.EqualTo(new[] { "rebuild.route-restored" }));
            Assert.That(dispatches, Is.EqualTo(1));
            Assert.That(navigations, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RestoreAsync_LateDelegateAfterRetry_NavigatesOnlyOnce() {
        List<string> logs = [];
        List<Func<bool>> pendingDelegates = [];
        int navigations = 0;

        await WebViewRouteRestorer.RestoreAsync(
            () => true,
            shouldNavigate => {
                pendingDelegates.Add(shouldNavigate);
                if (pendingDelegates.Count == 1) {
                    // 第一次派送逾時，委派留到之後才執行。
                    return new TaskCompletionSource<bool>().Task;
                }

                foreach (Func<bool> pending in pendingDelegates) {
                    if (pending()) {
                        navigations++;
                    }
                }

                return Task.FromResult(true);
            },
            new WebViewRouteRestorer.Timing(
                TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(1), 50
            ),
            logs.Add
        );

        Assert.Multiple(() => {
            Assert.That(logs, Is.EqualTo(new[] { "rebuild.route-restored" }));
            Assert.That(pendingDelegates, Has.Count.EqualTo(2));
            Assert.That(navigations, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RestoreAsync_DispatchThrows_LogsExceptionType() {
        List<string> logs = [];

        await WebViewRouteRestorer.RestoreAsync(
            () => true,
            _ => throw new InvalidOperationException(),
            CreateTiming(),
            logs.Add
        );

        Assert.That(logs, Has.Count.EqualTo(1));
        Assert.That(logs[0], Does.StartWith("rebuild.route-restore-failed:System.InvalidOperationException:0x"));
    }

    private static WebViewRouteRestorer.Timing CreateTiming(int retryDelayMilliseconds = 100, int maxAttempts = 50) {
        return new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(retryDelayMilliseconds), maxAttempts);
    }
}
