using NUnit.Framework;

namespace CloudyWing.AiAccountManager.UiTests;

/// <summary>
/// 驗證視窗關閉時會走完正常的結束流程。
/// </summary>
[TestFixture]
[Category(UiTestCategory.Name)]
[NonParallelizable]
public sealed class LifecycleTests {
    [Test]
    public void CloseMainWindow_ShutsDownGracefully() {
        using DesktopAppSession session = DesktopAppSession.Launch();

        bool exited = session.CloseAndWaitForExit();
        string diagnostics = session.ReadDiagnostics();

        Assert.Multiple(() => {
            Assert.That(exited, Is.True, "關閉主視窗後程式沒有在時限內結束。");
            Assert.That(session.ExitCode, Is.Zero);
            Assert.That(diagnostics, Does.Contain("\"main-window.closed\""));
            Assert.That(diagnostics, Does.Contain("\"application.run.completed\""));
        });
    }
}
