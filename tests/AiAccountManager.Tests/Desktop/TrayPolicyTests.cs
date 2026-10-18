using CloudyWing.AiAccountManager.Desktop;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class TrayPolicyTests {
    [TestCase(false, false, false)]
    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    public void ShouldHideOnClose_UsesConfiguredCloseAndExitState(bool minimize, bool exit, bool expected) {
        Assert.That(TrayPolicy.ShouldHideOnClose(minimize, exit), Is.EqualTo(expected));
    }

    [TestCase(false, false, true, false)]
    [TestCase(false, true, true, true)]
    [TestCase(true, false, true, true)]
    [TestCase(false, false, false, true)]
    public void ShouldShowIcon_UsesSettingsAndWindowVisibility(bool minimize, bool notifications, bool visible, bool expected) {
        Assert.That(TrayPolicy.ShouldShowIcon(minimize, notifications, visible), Is.EqualTo(expected));
    }

    [Test]
    public void UpdateVisibility_NotificationsEnabled_ShowsFakeTray() {
        FakeTray tray = new();

        TrayPolicy.UpdateVisibility(tray, false, true, true);

        Assert.That(tray.Visible, Is.True);
    }

    private sealed class FakeTray : ITrayService {
        public event EventHandler? OpenRequested { add { } remove { } }
        public event EventHandler? ExitRequested { add { } remove { } }
        public event EventHandler? BalloonClicked { add { } remove { } }
        public bool Visible { get; private set; }
        public void SetVisible(bool visible) => Visible = visible;
        public void ShowBalloon(string title, string message) { }
        public void Dispose() { }
    }
}
