using CloudyWing.AiAccountManager.Application.Notifications;
using CloudyWing.AiAccountManager.Desktop;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class TrayNotificationSinkTests {
    [Test]
    public async Task SendAsync_ReplacementBalloon_ActivatesLatestProvider() {
        FakeTray tray = new();
        using TrayNotificationSink sink = new(tray);
        string? activated = null;
        sink.Activated += provider => activated = provider;

        await sink.SendAsync(new("one", "message", "test", "claude"), CancellationToken.None);
        await sink.SendAsync(new("two", "message", "test", "codex"), CancellationToken.None);
        tray.Click();

        Assert.That(activated, Is.EqualTo("codex"));
    }

    [Test]
    public async Task SendAsync_UnknownProvider_DoesNotShowBalloon() {
        FakeTray tray = new();
        using TrayNotificationSink sink = new(tray);

        bool sent = await sink.SendAsync(new NotificationRequest("title", "message", "test", "other"), CancellationToken.None);

        Assert.That(sent, Is.False);
        Assert.That(tray.SentCount, Is.Zero);
    }

    [Test]
    public async Task SendAsync_NonActivatableRequest_ShowsBalloonButClickDoesNotActivate() {
        FakeTray tray = new();
        using TrayNotificationSink sink = new(tray);
        string? activated = null;
        sink.Activated += provider => activated = provider;

        bool sent = await sink.SendAsync(new("額度偏低", "message", "low-quota", "codex", false), CancellationToken.None);
        tray.Click();

        using (Assert.EnterMultipleScope()) {
            Assert.That(sent, Is.True);
            Assert.That(tray.SentCount, Is.EqualTo(1));
            Assert.That(activated, Is.Null);
        }
    }

    [Test]
    public async Task SendAsync_NonActivatableAfterActivatable_ClickDoesNotActivate() {
        FakeTray tray = new();
        using TrayNotificationSink sink = new(tray);
        string? activated = null;
        sink.Activated += provider => activated = provider;

        await sink.SendAsync(new("one", "message", "test", "claude"), CancellationToken.None);
        await sink.SendAsync(new("two", "message", "test", "codex", false), CancellationToken.None);
        tray.Click();

        Assert.That(activated, Is.Null);
    }

    [Test]
    public async Task SendAsync_ActivatableAfterNonActivatable_ClickActivatesLatestProvider() {
        FakeTray tray = new();
        using TrayNotificationSink sink = new(tray);
        string? activated = null;
        sink.Activated += provider => activated = provider;

        await sink.SendAsync(new("one", "message", "test", "codex", false), CancellationToken.None);
        await sink.SendAsync(new("two", "message", "test", "claude"), CancellationToken.None);
        tray.Click();

        Assert.That(activated, Is.EqualTo("claude"));
    }

    [Test]
    public async Task SendAsync_NonActivatableUnknownProvider_StillRejected() {
        FakeTray tray = new();
        using TrayNotificationSink sink = new(tray);

        bool sent = await sink.SendAsync(new("title", "message", "test", "other", false), CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(sent, Is.False);
            Assert.That(tray.SentCount, Is.Zero);
        }
    }

    private sealed class FakeTray : ITrayService {
        public event EventHandler? OpenRequested { add { } remove { } }
        public event EventHandler? ExitRequested { add { } remove { } }
        public event EventHandler? BalloonClicked;
        public int SentCount { get; private set; }
        public void SetVisible(bool visible) { }
        public void ShowBalloon(string title, string message) => SentCount++;
        public void Dispose() { }
        public void Click() => BalloonClicked?.Invoke(this, EventArgs.Empty);
    }
}
