using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.UiTests;

/// <summary>
/// 啟動一次桌面程式，驗證主視窗與頁面導覽。
/// </summary>
[TestFixture]
[Category(UiTestCategory.Name)]
[NonParallelizable]
public sealed class NavigationTests {
    private DesktopAppSession? session;
    private string? initialPageTitle;

    private DesktopAppSession Session => session ?? throw new InvalidOperationException("Desktop 程式尚未啟動。");

    [OneTimeSetUp]
    public void LaunchApplication() {
        session = DesktopAppSession.Launch();
        initialPageTitle = session.WaitForAnyPageTitle();
    }

    [OneTimeTearDown]
    public void CloseApplication() {
        session?.Dispose();
        session = null;
    }

    [Test]
    public void Launch_ShowsMainWindowWithApplicationTitle() {
        Assert.Multiple(() => {
            Assert.That(Session.MainWindow.Title, Is.EqualTo(DesktopAppSession.WindowTitle));
            Assert.That(Session.MainWindow.IsOffscreen, Is.False);
        });
    }

    [Test]
    public void Launch_OpensDashboardFirst() {
        Assert.That(initialPageTitle, Is.EqualTo("儀表板"));
    }

    [Test]
    public void Launch_StoresDataInIsolatedRoot() {
        Assert.Multiple(() => {
            Assert.That(File.Exists(Session.MarkerPath), Is.True);
            Assert.That(Directory.Exists(Path.Combine(Session.DataRoot, "webview2")), Is.True);
        });
    }

    [TestCase("Claude", "Claude 帳號")]
    [TestCase("Codex", "Codex 帳號")]
    [TestCase("設定", "設定")]
    [TestCase("儀表板", "儀表板")]
    public void NavMenu_OpensSelectedPage(string linkName, string expectedTitle) {
        AutomationElement link = Session.WaitForElement(linkName, ControlType.Hyperlink);

        DesktopAppSession.Activate(link);

        Session.WaitForPageTitle(expectedTitle);
    }

    [Test]
    public void SettingsPage_ShowsPreferenceControls() {
        DesktopAppSession.Activate(Session.WaitForElement("設定", ControlType.Hyperlink));
        Session.WaitForPageTitle("設定");

        Assert.Multiple(() => {
            Assert.That(Session.WaitForElement("檢查更新", ControlType.Button), Is.Not.Null);
            Assert.That(Session.WaitForElement("保存更新來源", ControlType.Button), Is.Not.Null);
        });
    }

    [Test]
    public void AppBar_ExposesNavigationToggle() {
        AutomationElement toggle = Session.WaitForElement("開啟或關閉導覽", ControlType.Button);

        Assert.That(toggle.IsEnabled, Is.True);
    }
}
