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
