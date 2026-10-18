using CloudyWing.AiAccountManager.Desktop;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class TrayLifecycleCoordinatorTests {
    [Test]
    public async Task TrayLifecycle_MinimizedOpenAndExit_RunsSafeShutdown() {
        FakeTray tray = new();
        List<string> steps = [];
        Task? exitTask = null;
        using TrayLifecycleCoordinator lifecycle = new(tray, () => steps.Add("open"), () => {
            exitTask = SafeShutdownSequence.RunAsync(
                () => steps.Add("stop-accepting"),
                () => { steps.Add("stop-host"); return Task.CompletedTask; },
                () => { steps.Add("drain"); return Task.CompletedTask; },
                () => steps.Add("close"),
                _ => steps.Add("warning")
            );
        });

        lifecycle.Start(true, () => steps.Add("show"));
        Assert.That(steps, Is.Empty);
        tray.Open();
        tray.Exit();
        await (exitTask ?? throw new AssertionException("Exit was not requested."));

        Assert.That(steps, Is.EqualTo(new[] { "open", "stop-accepting", "stop-host", "drain", "close" }));
    }

    [Test]
    public async Task TrayLifecycle_ShutdownFailure_ShowsWarningAndAllowsRetry() {
        FakeTray tray = new();
        List<string> steps = [];
        Task? exitTask = null;
        int attempts = 0;
        using TrayLifecycleCoordinator lifecycle = new(tray, () => steps.Add("open"), () => {
            exitTask = SafeShutdownSequence.RunAsync(
                () => steps.Add("stop-accepting"),
                () => ++attempts == 1 ? Task.FromException(new OperationCanceledException()) : Task.CompletedTask,
                () => { steps.Add("drain"); return Task.CompletedTask; },
                () => steps.Add("close"),
                exception => SafeShutdownSequence.HandleFailure(exception, () => steps.Add("show"),
                    () => steps.Add("warning"), _ => steps.Add("error"))
            );
        });

        tray.Exit();
        await (exitTask ?? throw new AssertionException("Exit was not requested."));
        tray.Exit();
        await (exitTask ?? throw new AssertionException("Retry was not requested."));

        Assert.That(steps, Is.EqualTo(new[] { "stop-accepting", "show", "warning", "stop-accepting", "drain", "close" }));
    }

    private sealed class FakeTray : ITrayService {
        public event EventHandler? OpenRequested;
        public event EventHandler? ExitRequested;
        public event EventHandler? BalloonClicked { add { } remove { } }
        public void SetVisible(bool visible) { }
        public void ShowBalloon(string title, string message) { }
        public void Dispose() { }
        public void Open() => OpenRequested?.Invoke(this, EventArgs.Empty);
        public void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}
