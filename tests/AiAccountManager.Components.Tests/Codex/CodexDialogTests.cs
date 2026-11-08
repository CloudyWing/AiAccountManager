using Bunit;
using MudBlazor;
using NUnit.Framework;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Desktop.Components.Codex;

namespace CloudyWing.AiAccountManager.Components.Tests.Codex;

[TestFixture]
public sealed class CodexDialogTests : ComponentTestBase {
    [TestCase(true, "會先正常關閉 Codex Desktop")]
    [TestCase(false, "Codex Desktop 未執行。")]
    public void SwitchDialog_ExplainsWhatHappensToRunningDesktop(bool desktopRunning, string expected) {
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        Context.Render<CodexSwitchConfirmationDialog>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.SourceDisplayName, "Work")
            .Add(p => p.TargetDisplayName, "Personal")
            .Add(p => p.DesktopRunning, desktopRunning));

        provider.WaitForAssertion(() => Assert.Multiple(() => {
            Assert.That(provider.Markup, Does.Contain("Work"));
            Assert.That(provider.Markup, Does.Contain("Personal"));
            Assert.That(provider.Markup, Does.Contain(expected));
        }));
    }

    [Test]
    public void SwitchDialog_ConfirmAndCancelInvokeCallbacks() {
        int confirmCount = 0;
        int cancelCount = 0;
        List<bool> visibleChanges = [];
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        Context.Render<CodexSwitchConfirmationDialog>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.SourceDisplayName, "Work")
            .Add(p => p.TargetDisplayName, "Personal")
            .Add(p => p.OnConfirm, () => confirmCount++)
            .Add(p => p.OnCancel, () => cancelCount++)
            .Add(p => p.VisibleChanged, visible => visibleChanges.Add(visible)));

        provider.WaitForAssertion(() => provider.FindButton("確認切換"));
        provider.FindButton("確認切換").Click();
        provider.FindButton("取消").Click();

        Assert.Multiple(() => {
            Assert.That(confirmCount, Is.EqualTo(1));
            Assert.That(cancelCount, Is.EqualTo(1));
            Assert.That(visibleChanges, Is.EqualTo(new[] { false }));
        });
    }

    [Test]
    public void SwitchDialog_BusyShowsProgressAndBlocksActions() {
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        Context.Render<CodexSwitchConfirmationDialog>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.SourceDisplayName, "Work")
            .Add(p => p.TargetDisplayName, "Personal")
            .Add(p => p.Busy, true));

        provider.WaitForAssertion(() => Assert.Multiple(() => {
            Assert.That(provider.FindButton("正在切換…").IsDisabled(), Is.True);
            Assert.That(provider.FindButton("取消").IsDisabled(), Is.True);
            Assert.That(provider.FindAll(".mud-progress-linear"), Is.Not.Empty);
        }));
    }

    [Test]
    public void LoginDialog_CancelInvokesCancelAndClosesDialog() {
        int cancelCount = 0;
        List<bool> visibleChanges = [];
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        Context.Render<CodexLoginDialog>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.OnCancel, () => cancelCount++)
            .Add(p => p.VisibleChanged, visible => visibleChanges.Add(visible)));

        provider.WaitForAssertion(() => provider.FindButton("取消登入"));
        provider.FindButton("取消登入").Click();

        Assert.Multiple(() => {
            Assert.That(cancelCount, Is.EqualTo(1));
            Assert.That(visibleChanges, Is.EqualTo(new[] { false }));
        });
    }

    [Test]
    public void ResetCreditDialog_DescribesWindowsAndSystemPickedCredit() {
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        QuotaSuccessfulSnapshot snapshot = new(
            DateTimeOffset.Parse("2026-10-28T00:00:00Z"),
            new QuotaWindow(300, 25, DateTimeOffset.Parse("2026-10-28T06:29:38Z")),
            new QuotaWindow(10080, 60, null)
        );
        Context.Render<ResetCreditDialog>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.Snapshot, snapshot));

        provider.WaitForAssertion(() => Assert.Multiple(() => {
            Assert.That(provider.Markup, Does.Contain("5 小時：已用 75%・原重置時間 10/28 14:29"));
            Assert.That(provider.Markup, Does.Contain("每週：已用 40%・原重置時間 未知"));
            Assert.That(provider.Markup, Does.Contain("重置券由系統挑選"));
        }));
    }

    [Test]
    public void ResetCreditDialog_WithSpecificCredit_HidesSystemPickedHint() {
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        Context.Render<ResetCreditDialog>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.CreditId, "credit-1"));

        provider.WaitForAssertion(() => Assert.Multiple(() => {
            Assert.That(provider.Markup, Does.Contain("5 小時：未知"));
            Assert.That(provider.Markup, Does.Not.Contain("重置券由系統挑選"));
        }));
    }

    [Test]
    public void ResetCreditDialog_ConfirmInvokesCallback() {
        int confirmCount = 0;
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        Context.Render<ResetCreditDialog>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.OnConfirm, () => confirmCount++));

        provider.WaitForAssertion(() => provider.FindButton("確認使用"));
        provider.FindButton("確認使用").Click();

        Assert.That(confirmCount, Is.EqualTo(1));
    }

    [Test]
    public void SwitchDialog_PlacesTitleInTitleSlot() {
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        Context.Render<CodexSwitchConfirmationDialog>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.SourceDisplayName, "Work")
            .Add(p => p.TargetDisplayName, "Personal"));

        provider.WaitForAssertion(() => AssertTitleInTitleSlot(provider, "切換 Codex Desktop 帳號"));
    }

    [Test]
    public void LoginDialog_PlacesTitleInTitleSlot() {
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        Context.Render<CodexLoginDialog>(parameters => parameters.Add(p => p.Visible, true));

        provider.WaitForAssertion(() => AssertTitleInTitleSlot(provider, "連結 Codex 額度帳號"));
    }

    [Test]
    public void ResetCreditDialog_PlacesTitleInTitleSlot() {
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        Context.Render<ResetCreditDialog>(parameters => parameters.Add(p => p.Visible, true));

        provider.WaitForAssertion(() => AssertTitleInTitleSlot(provider, "確認使用重置券"));
    }

    [Test]
    public void RemoveDialog_PlacesTitleInTitleSlot() {
        CodexAccountProfile account = new(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "Work",
            "w***@example.test",
            "fingerprint",
            1,
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            "active"
        );
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        Context.Render<CodexRemoveAccountDialog>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.Account, account));

        provider.WaitForAssertion(() => AssertTitleInTitleSlot(provider, "確認移除 Codex 額度帳號"));
    }
}
