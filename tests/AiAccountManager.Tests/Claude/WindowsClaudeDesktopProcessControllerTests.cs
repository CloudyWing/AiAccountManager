using System.Diagnostics;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Windows.Claude;
using NUnit.Framework;
using PendingLaunchTracker =
    CloudyWing.AiAccountManager.Windows.Claude.WindowsClaudeDesktopProcessController.PendingLaunchTracker;
using ClaudeDesktopProcessEnumerationResult =
    CloudyWing.AiAccountManager.Windows.Claude.IClaudeDesktopProcessEnumerator.ClaudeDesktopProcessEnumerationResult;
using IClaudeDesktopProcessHandle =
    CloudyWing.AiAccountManager.Windows.Claude.IClaudeDesktopProcessEnumerator.IClaudeDesktopProcessHandle;
using ProcessIdentity = CloudyWing.AiAccountManager.Windows.Claude.WindowsClaudeDesktopProcessEnumerator.ProcessIdentity;

namespace CloudyWing.AiAccountManager.Tests.Claude;

[TestFixture]
public sealed class WindowsClaudeDesktopProcessControllerTests {
    private static readonly DateTime ProcessStart = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task CloseGracefullyAsync_ReenumeratesAndRequestsCloseForRestartedProcess() {
        using WindowsClaudeTestWorkspace workspace = new();
        FakeProcessHandle initial = new(101, ProcessStart);
        FakeProcessHandle restarted = new(202, ProcessStart.AddSeconds(3));
        restarted.OnClose = () => restarted.HasExitedValue = true;
        SequenceProcessEnumerator enumerator = new(call => {
            switch (call) {
                case 1:
                    return Result(initial);
                case 2:
                    initial.HasExitedValue = true;
                    return Result(initial, restarted);
                default:
                    restarted.HasExitedValue = true;
                    return Result();
            }
        });
        WindowsClaudeDesktopProcessController controller = new(enumerator, TimeSpan.FromMilliseconds(2));

        bool closed = await controller.CloseGracefullyAsync(
            ValidInstallation(workspace),
            TimeSpan.FromSeconds(1),
            CancellationToken.None
        );

        Assert.That(closed, Is.True);
        Assert.That(enumerator.CallCount, Is.EqualTo(3));
        Assert.That(initial.CloseRequestCount, Is.EqualTo(1));
        Assert.That(restarted.CloseRequestCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CloseGracefullyAsync_WaitsForChildWithoutMainWindowToExit() {
        using WindowsClaudeTestWorkspace workspace = new();
        FakeProcessHandle parent = new(101, ProcessStart);
        FakeProcessHandle childWithoutMainWindow = new(102, ProcessStart.AddSeconds(1));
        parent.OnClose = () => parent.HasExitedValue = true;
        SequenceProcessEnumerator enumerator = new(call => {
            switch (call) {
                case 1:
                case 2:
                    return Result(parent, childWithoutMainWindow);
                default:
                    childWithoutMainWindow.HasExitedValue = true;
                    return Result();
            }
        });
        WindowsClaudeDesktopProcessController controller = new(enumerator, TimeSpan.FromMilliseconds(2));

        bool closed = await controller.CloseGracefullyAsync(
            ValidInstallation(workspace),
            TimeSpan.FromSeconds(1),
            CancellationToken.None
        );

        Assert.That(closed, Is.True);
        Assert.That(enumerator.CallCount, Is.EqualTo(3));
        Assert.That(parent.CloseRequestCount, Is.EqualTo(1));
        Assert.That(childWithoutMainWindow.CloseRequestCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CloseGracefullyAsync_ReturnsFalseWhenFreshEnumerationIsUnknown() {
        using WindowsClaudeTestWorkspace workspace = new();
        FakeProcessHandle process = new(101, ProcessStart);
        SequenceProcessEnumerator enumerator = new(call => call == 1
            ? Result(process)
            : new ClaudeDesktopProcessEnumerationResult(false, [process]));
        WindowsClaudeDesktopProcessController controller = new(enumerator, TimeSpan.FromMilliseconds(2));

        bool closed = await controller.CloseGracefullyAsync(
            ValidInstallation(workspace),
            TimeSpan.FromSeconds(1),
            CancellationToken.None
        );

        Assert.That(closed, Is.False);
        Assert.That(enumerator.CallCount, Is.EqualTo(2));
        Assert.That(process.CloseRequestCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CloseGracefullyAsync_ReturnsFalseWhenProcessDoesNotExitBeforeTimeout() {
        using WindowsClaudeTestWorkspace workspace = new();
        FakeProcessHandle process = new(101, ProcessStart);
        SequenceProcessEnumerator enumerator = new(_ => Result(process));
        WindowsClaudeDesktopProcessController controller = new(enumerator, TimeSpan.FromMilliseconds(5));

        bool closed = await controller.CloseGracefullyAsync(
            ValidInstallation(workspace),
            TimeSpan.FromMilliseconds(40),
            CancellationToken.None
        );

        Assert.That(closed, Is.False);
        Assert.That(enumerator.CallCount, Is.GreaterThan(1));
        Assert.That(process.CloseRequestCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CloseGracefullyAsync_ReturnsFalseWhenEmptyEnumerationExceedsTimeout() {
        using WindowsClaudeTestWorkspace workspace = new();
        SequenceProcessEnumerator enumerator = new(_ => {
            Thread.Sleep(TimeSpan.FromMilliseconds(40));
            return Result();
        });
        WindowsClaudeDesktopProcessController controller = new(enumerator, TimeSpan.FromMilliseconds(2));

        bool closed = await controller.CloseGracefullyAsync(
            ValidInstallation(workspace),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None
        );

        Assert.That(closed, Is.False);
        Assert.That(enumerator.CallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task GetStatusAsync_ReturnsUnknownWhenEnumerationIsIncomplete() {
        using WindowsClaudeTestWorkspace workspace = new();
        FakeProcessHandle process = new(101, ProcessStart);
        SequenceProcessEnumerator enumerator = new(_ => new ClaudeDesktopProcessEnumerationResult(false, [process]));
        WindowsClaudeDesktopProcessController controller = new(enumerator, TimeSpan.FromMilliseconds(2));

        IClaudeDesktopProcessController.ProcessStatus status = await controller.GetStatusAsync(
            ValidInstallation(workspace),
            CancellationToken.None
        );

        Assert.That(status, Is.EqualTo(IClaudeDesktopProcessController.ProcessStatus.Unknown));
    }

    [TestCase("updated-version", IClaudeDesktopProcessController.ProcessStatus.Running)]
    [TestCase("different-family", IClaudeDesktopProcessController.ProcessStatus.Unknown)]
    [TestCase("wrong-aumid", IClaudeDesktopProcessController.ProcessStatus.Unknown)]
    [TestCase("full-name-family-mismatch", IClaudeDesktopProcessController.ProcessStatus.Unknown)]
    public async Task GetStatusAsync_TreatsOtherPackageVersionsAsRunningAndInvalidIdentityAsUnknown(
        string condition,
        IClaudeDesktopProcessController.ProcessStatus expectedStatus
    ) {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = ValidInstallation(workspace);
        ClaudeDesktopRootBinding.Observation expected = installation.Observation!;
        string packageFamilyName = expected.PackageFamilyName!;
        string appUserModelId = expected.AppUserModelId!;
        string packageFullName = "ClaudeFixture.Package_2.0.0.0_x64__abcd1234wxyz1";
        if (condition == "different-family") {
            packageFamilyName = "Other.Package_zxyw4321abcd1";
            appUserModelId = packageFamilyName + "!Claude";
            packageFullName = "Other.Package_2.0.0.0_x64__zxyw4321abcd1";
        } else if (condition == "wrong-aumid") {
            appUserModelId = packageFamilyName + "!Other";
        } else if (condition == "full-name-family-mismatch") {
            packageFullName = "Other.Package_2.0.0.0_x64__zxyw4321abcd1";
        }

        ProcessIdentity actual = new(
            101,
            ProcessStart,
            "S-1-5-21-100",
            "C:\\Synthetic\\Claude.exe",
            null,
            appUserModelId,
            packageFamilyName,
            packageFullName
        );
        FakeProcessHandle process = new(101, ProcessStart) {
            IdentityIsValid = _ => WindowsClaudeDesktopProcessEnumerator.MatchesMsixIdentity(expected, actual)
        };
        SequenceProcessEnumerator enumerator = new(_ => Result(process));
        WindowsClaudeDesktopProcessController controller = new(enumerator, TimeSpan.FromMilliseconds(2));

        IClaudeDesktopProcessController.ProcessStatus status = await controller.GetStatusAsync(
            installation,
            CancellationToken.None
        );

        Assert.That(status, Is.EqualTo(expectedStatus));
    }

    [Test]
    public async Task CloseGracefullyAsync_DoesNotCloseAnyProcessWhenOneIdentityIsUnknown() {
        using WindowsClaudeTestWorkspace workspace = new();
        FakeProcessHandle accepted = new(101, ProcessStart);
        FakeProcessHandle otherSid = new(102, ProcessStart.AddSeconds(1)) { IdentityIsValid = _ => false };
        SequenceProcessEnumerator enumerator = new(_ => Result(accepted, otherSid));
        WindowsClaudeDesktopProcessController controller = new(enumerator, TimeSpan.FromMilliseconds(2));

        bool closed = await controller.CloseGracefullyAsync(
            ValidInstallation(workspace), TimeSpan.FromSeconds(1), CancellationToken.None
        );

        Assert.That(closed, Is.False);
        Assert.That(accepted.CloseRequestCount, Is.Zero);
        Assert.That(otherSid.CloseRequestCount, Is.Zero);
    }

    [Test]
    public async Task CloseGracefullyAsync_RechecksHandleBeforeEveryCloseRequest() {
        using WindowsClaudeTestWorkspace workspace = new();
        FakeProcessHandle process = new(101, ProcessStart) { IdentityIsValid = count => count == 1 };
        SequenceProcessEnumerator enumerator = new(_ => Result(process));
        WindowsClaudeDesktopProcessController controller = new(enumerator, TimeSpan.FromMilliseconds(2));

        bool closed = await controller.CloseGracefullyAsync(
            ValidInstallation(workspace), TimeSpan.FromSeconds(1), CancellationToken.None
        );

        Assert.That(closed, Is.False);
        Assert.That(process.IdentityCheckCount, Is.EqualTo(2));
        Assert.That(process.CloseRequestCount, Is.Zero);
    }

    [Test]
    public async Task GetStatusAsync_ReturnsUnknownWhenSourceEvidenceIsMissing() {
        using WindowsClaudeTestWorkspace workspace = new();
        FakeProcessHandle process = new(101, ProcessStart);
        SequenceProcessEnumerator enumerator = new(_ => Result(process));
        WindowsClaudeDesktopProcessController controller = new(enumerator, TimeSpan.FromMilliseconds(2));
        IClaudeDesktopInstallationLocator.Installation legacy = ValidInstallation(workspace) with { Observation = null };

        IClaudeDesktopProcessController.ProcessStatus status =
            await controller.GetStatusAsync(legacy, CancellationToken.None);

        Assert.That(status, Is.EqualTo(IClaudeDesktopProcessController.ProcessStatus.Unknown));
        Assert.That(enumerator.CallCount, Is.Zero);
    }

    [Test]
    public async Task LaunchAsync_ReturnsWhenMsixActivationCompletesWithinDeadline() {
        using WindowsClaudeTestWorkspace workspace = new();
        string? activatedId = null;
        WindowsClaudeDesktopProcessController controller = new(
            new SequenceProcessEnumerator(_ => Result()),
            TimeSpan.FromMilliseconds(2),
            id => activatedId = id,
            TimeSpan.FromSeconds(30)
        );

        await controller.LaunchAsync(ValidInstallation(workspace), CancellationToken.None);

        Assert.That(activatedId, Is.EqualTo("ClaudeFixture.Package_abcd1234wxyz1!Claude"));
    }

    [Test]
    public async Task LaunchAsync_ThrowsTimeoutExceptionWhenMsixActivationStalls() {
        using WindowsClaudeTestWorkspace workspace = new();
        using ManualResetEventSlim stall = new(false);
        PendingLaunchTracker tracker = new();
        WindowsClaudeDesktopProcessController controller = new(
            new SequenceProcessEnumerator(_ => Result()),
            TimeSpan.FromMilliseconds(2),
            _ => stall.Wait(TimeSpan.FromSeconds(30)),
            TimeSpan.FromMilliseconds(200),
            tracker
        );
        Stopwatch stopwatch = Stopwatch.StartNew();

        try {
            // caller 傳入 CancellationToken.None，確認期限獨立於 caller 的 token。
            await Assert.ThrowsAsync<TimeoutException>(
                async () => await controller.LaunchAsync(ValidInstallation(workspace), CancellationToken.None)
            );
        } finally {
            ReleaseStall(stall, tracker);
        }

        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public async Task LaunchAsync_ThrowsOperationCanceledExceptionWhenCallerTokenIsCanceled() {
        using WindowsClaudeTestWorkspace workspace = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        int activationCount = 0;
        WindowsClaudeDesktopProcessController controller = new(
            new SequenceProcessEnumerator(_ => Result()),
            TimeSpan.FromMilliseconds(2),
            _ => activationCount++,
            TimeSpan.FromSeconds(30)
        );

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await controller.LaunchAsync(ValidInstallation(workspace), cancellation.Token)
        );

        Assert.That(activationCount, Is.Zero);
    }

    [Test]
    public async Task GetStatusAsync_ReturnsUnknownWhileStalledLaunchIsPending() {
        using WindowsClaudeTestWorkspace workspace = new();
        using ManualResetEventSlim stall = new(false);
        PendingLaunchTracker tracker = new();
        WindowsClaudeDesktopProcessController controller = CreateStallingController(stall, tracker);

        try {
            await Assert.ThrowsAsync<TimeoutException>(
                async () => await controller.LaunchAsync(ValidInstallation(workspace), CancellationToken.None)
            );

            IClaudeDesktopProcessController.ProcessStatus status =
                await controller.GetStatusAsync(ValidInstallation(workspace), CancellationToken.None);

            Assert.That(status, Is.EqualTo(IClaudeDesktopProcessController.ProcessStatus.Unknown));
        } finally {
            ReleaseStall(stall, tracker);
        }
    }

    [Test]
    public async Task GetStatusAsync_ReturnsNotRunningAfterStalledLaunchCompletes() {
        using WindowsClaudeTestWorkspace workspace = new();
        using ManualResetEventSlim stall = new(false);
        PendingLaunchTracker tracker = new();
        WindowsClaudeDesktopProcessController controller = CreateStallingController(stall, tracker);
        IClaudeDesktopProcessController.ProcessStatus status;

        try {
            await Assert.ThrowsAsync<TimeoutException>(
                async () => await controller.LaunchAsync(ValidInstallation(workspace), CancellationToken.None)
            );
        } finally {
            ReleaseStall(stall, tracker);
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        do {
            status = await controller.GetStatusAsync(ValidInstallation(workspace), CancellationToken.None);
            if (status != IClaudeDesktopProcessController.ProcessStatus.Unknown) {
                break;
            }

            await Task.Delay(20);
        } while (stopwatch.Elapsed < TimeSpan.FromSeconds(10));

        Assert.That(status, Is.EqualTo(IClaudeDesktopProcessController.ProcessStatus.NotRunning));
    }

    [Test]
    public async Task LaunchAsync_ThrowsInvalidOperationExceptionWhileStalledLaunchIsPending() {
        using WindowsClaudeTestWorkspace workspace = new();
        using ManualResetEventSlim stall = new(false);
        PendingLaunchTracker tracker = new();
        int activationCount = 0;
        WindowsClaudeDesktopProcessController controller = new(
            new SequenceProcessEnumerator(_ => Result()),
            TimeSpan.FromMilliseconds(2),
            _ => {
                Interlocked.Increment(ref activationCount);
                stall.Wait(TimeSpan.FromSeconds(30));
            },
            TimeSpan.FromMilliseconds(200),
            tracker
        );

        try {
            await Assert.ThrowsAsync<TimeoutException>(
                async () => await controller.LaunchAsync(ValidInstallation(workspace), CancellationToken.None)
            );

            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await controller.LaunchAsync(ValidInstallation(workspace), CancellationToken.None)
            );

            Assert.That(Volatile.Read(ref activationCount), Is.EqualTo(1));
        } finally {
            ReleaseStall(stall, tracker);
        }
    }

    [Test]
    public async Task LaunchAsync_ThrowsInvalidOperationExceptionWhileAnotherLaunchIsInFlight() {
        using WindowsClaudeTestWorkspace workspace = new();
        using ManualResetEventSlim stall = new(false);
        using ManualResetEventSlim entered = new(false);
        PendingLaunchTracker tracker = new();
        int activationCount = 0;
        WindowsClaudeDesktopProcessController controller = new(
            new SequenceProcessEnumerator(_ => Result()),
            TimeSpan.FromMilliseconds(2),
            _ => {
                Interlocked.Increment(ref activationCount);
                entered.Set();
                stall.Wait(TimeSpan.FromSeconds(30));
            },
            TimeSpan.FromSeconds(30),
            tracker
        );
        Task? firstLaunch = null;

        try {
            firstLaunch = controller.LaunchAsync(ValidInstallation(workspace), CancellationToken.None);

            Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);

            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await controller.LaunchAsync(ValidInstallation(workspace), CancellationToken.None)
            );

            Assert.That(Volatile.Read(ref activationCount), Is.EqualTo(1));
        } finally {
            stall.Set();
            if (firstLaunch is not null) {
                await firstLaunch;
            }
        }

        Assert.That(firstLaunch.IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public async Task CloseGracefullyAsync_ReturnsFalseWhileStalledLaunchIsPending() {
        using WindowsClaudeTestWorkspace workspace = new();
        using ManualResetEventSlim stall = new(false);
        PendingLaunchTracker tracker = new();
        FakeProcessHandle process = new(101, ProcessStart);
        SequenceProcessEnumerator enumerator = new(_ => Result(process));
        WindowsClaudeDesktopProcessController controller = new(
            enumerator,
            TimeSpan.FromMilliseconds(2),
            _ => stall.Wait(TimeSpan.FromSeconds(30)),
            TimeSpan.FromMilliseconds(200),
            tracker
        );

        try {
            await Assert.ThrowsAsync<TimeoutException>(
                async () => await controller.LaunchAsync(ValidInstallation(workspace), CancellationToken.None)
            );

            bool closed = await controller.CloseGracefullyAsync(
                ValidInstallation(workspace),
                TimeSpan.FromSeconds(1),
                CancellationToken.None
            );

            Assert.That(closed, Is.False);
            Assert.That(process.CloseRequestCount, Is.Zero);
            Assert.That(enumerator.CallCount, Is.Zero);
        } finally {
            ReleaseStall(stall, tracker);
        }
    }

    [Test]
    public async Task GetStatusAsync_ReturnsUnknownOnOtherControllerSharingTheTracker() {
        using WindowsClaudeTestWorkspace workspace = new();
        using ManualResetEventSlim stall = new(false);
        PendingLaunchTracker tracker = new();
        WindowsClaudeDesktopProcessController launcher = CreateStallingController(stall, tracker);
        WindowsClaudeDesktopProcessController observer = new(
            new SequenceProcessEnumerator(_ => Result()),
            TimeSpan.FromMilliseconds(2),
            null,
            null,
            tracker
        );

        try {
            await Assert.ThrowsAsync<TimeoutException>(
                async () => await launcher.LaunchAsync(ValidInstallation(workspace), CancellationToken.None)
            );

            IClaudeDesktopProcessController.ProcessStatus status =
                await observer.GetStatusAsync(ValidInstallation(workspace), CancellationToken.None);

            Assert.That(status, Is.EqualTo(IClaudeDesktopProcessController.ProcessStatus.Unknown));
        } finally {
            ReleaseStall(stall, tracker);
        }
    }

    [Test]
    public async Task LaunchAsync_PropagatesActivatorTimeoutExceptionAndDoesNotMarkLaunchPending() {
        using WindowsClaudeTestWorkspace workspace = new();
        PendingLaunchTracker tracker = new();
        WindowsClaudeDesktopProcessController controller = new(
            new SequenceProcessEnumerator(_ => Result()),
            TimeSpan.FromMilliseconds(2),
            _ => throw new TimeoutException("inner"),
            TimeSpan.FromSeconds(30),
            tracker
        );

        TimeoutException? failure = await Assert.ThrowsAsync<TimeoutException>(
            async () => await controller.LaunchAsync(ValidInstallation(workspace), CancellationToken.None)
        );
        IClaudeDesktopProcessController.ProcessStatus status =
            await controller.GetStatusAsync(ValidInstallation(workspace), CancellationToken.None);

        Assert.That(failure!.Message, Is.EqualTo("inner"));
        Assert.That(status, Is.Not.EqualTo(IClaudeDesktopProcessController.ProcessStatus.Unknown));
    }

    private static WindowsClaudeDesktopProcessController CreateStallingController(
        ManualResetEventSlim stall,
        PendingLaunchTracker tracker
    ) {
        return new WindowsClaudeDesktopProcessController(
            new SequenceProcessEnumerator(_ => Result()),
            TimeSpan.FromMilliseconds(2),
            _ => stall.Wait(TimeSpan.FromSeconds(30)),
            TimeSpan.FromMilliseconds(200),
            tracker
        );
    }

    private static void ReleaseStall(ManualResetEventSlim stall, PendingLaunchTracker tracker) {
        stall.Set();
        SpinWait.SpinUntil(() => !tracker.IsPending, TimeSpan.FromSeconds(10));
    }

    private static ClaudeDesktopProcessEnumerationResult Result(
        params IClaudeDesktopProcessHandle[] processes
    ) {
        return new ClaudeDesktopProcessEnumerationResult(true, processes);
    }

    private static IClaudeDesktopInstallationLocator.Installation ValidInstallation(
        WindowsClaudeTestWorkspace workspace
    ) {
        return new IClaudeDesktopInstallationLocator.Installation(
            "msix:ClaudeFixture.Package_abcd1234wxyz1",
            IClaudeDesktopInstallationLocator.InstallKind.Msix,
            workspace.DataRootPath,
            workspace.Installation.DataRootFingerprint,
            "ClaudeFixture.Package_abcd1234wxyz1!Claude",
            null,
            new ClaudeDesktopRootBinding.Observation(
                1, Guid.NewGuid(), "S-1-5-21-100", "msix:ClaudeFixture.Package_abcd1234wxyz1",
                IClaudeDesktopInstallationLocator.InstallKind.Msix, "ClaudeFixture.Package_abcd1234wxyz1!Claude",
                "ClaudeFixture.Package_abcd1234wxyz1", "ClaudeFixture.Package_1.0.0.0_x64__abcd1234wxyz1", null, null, null,
                workspace.DataRootPath, "Cookies", 1, "fixture", 1, []
            )
        );
    }

    private sealed class SequenceProcessEnumerator : IClaudeDesktopProcessEnumerator {
        private readonly Func<int, ClaudeDesktopProcessEnumerationResult> resultFactory;

        public SequenceProcessEnumerator(Func<int, ClaudeDesktopProcessEnumerationResult> resultFactory) {
            this.resultFactory = resultFactory;
        }

        public int CallCount { get; private set; }

        public ClaudeDesktopProcessEnumerationResult Enumerate(
            IClaudeDesktopInstallationLocator.Installation installation,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return resultFactory(CallCount);
        }
    }

    private sealed class FakeProcessHandle : IClaudeDesktopProcessHandle {
        public FakeProcessHandle(int id, DateTime startTimeUtc) {
            Id = id;
            StartTimeUtc = startTimeUtc;
        }

        public int Id { get; }

        public DateTime StartTimeUtc { get; }

        public bool HasExitedValue { get; set; }

        public bool HasExited => HasExitedValue;

        public int IdentityCheckCount { get; private set; }

        public Func<int, bool> IdentityIsValid { get; init; } = _ => true;

        public bool HasExpectedIdentity(IClaudeDesktopInstallationLocator.Installation installation) {
            IdentityCheckCount++;
            return IdentityIsValid(IdentityCheckCount);
        }

        public int CloseRequestCount { get; private set; }

        public Action? OnClose { get; set; }

        public void RequestGracefulClose() {
            CloseRequestCount++;
            OnClose?.Invoke();
        }

        public void Dispose() {
        }
    }
}
