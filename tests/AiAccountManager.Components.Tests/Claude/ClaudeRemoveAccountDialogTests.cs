using Bunit;
using MudBlazor;
using NUnit.Framework;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Desktop.Components.Claude;

namespace CloudyWing.AiAccountManager.Components.Tests.Claude;

[TestFixture]
public sealed class ClaudeRemoveAccountDialogTests : ComponentTestBase {
    private static readonly ClaudeAccountProfile Profile = new(
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        "工作",
        "w***@example.test",
        "account-uuid",
        "organization-uuid",
        "fixture-installation",
        "fixture-data-root",
        "S-1-5-21-fixture",
        new string('a', 64),
        1,
        DateTimeOffset.Parse("2026-10-26T00:00:00Z"),
        DateTimeOffset.Parse("2026-10-26T00:00:00Z"),
        ClaudeAccountProfile.VerificationStatus.Verified
    );

    [Test]
    public void Visible_ShowsAliasIdentityAndWarning() {
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        RenderDialog(Profile);

        provider.WaitForAssertion(() => {
            Assert.That(provider.Markup, Does.Contain("確認移除 Claude 帳號"));
            Assert.That(provider.Markup, Does.Contain("工作"));
            Assert.That(provider.Markup, Does.Contain("身分：w***@example.test"));
            Assert.That(provider.Markup, Does.Contain("Claude Desktop 目前的登入狀態與 Code Session 不受影響"));
        });
    }

    [Test]
    public void Visible_WithoutAlias_UsesMaskedEmailAsTitle() {
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        RenderDialog(Profile with { Alias = " " });

        provider.WaitForAssertion(() => Assert.That(
            provider.FindAll(".mud-typography-subtitle1").Select(element => element.TextContent.Trim()),
            Does.Contain("w***@example.test")));
    }

    [Test]
    public void Confirm_SendsProfileId() {
        List<Guid> confirmed = [];
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        RenderDialog(Profile, parameters => parameters.Add(p => p.OnConfirm, id => confirmed.Add(id)));

        provider.WaitForAssertion(() => provider.FindButton("確認移除"));
        provider.FindButton("確認移除").Click();

        Assert.That(confirmed, Is.EqualTo(new[] { Profile.ProfileId }));
    }

    [Test]
    public void Cancel_InvokesCancelAndClosesDialog() {
        int cancelCount = 0;
        List<bool> visibleChanges = [];
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        RenderDialog(Profile, parameters => parameters
            .Add(p => p.OnCancel, () => cancelCount++)
            .Add(p => p.VisibleChanged, visible => visibleChanges.Add(visible)));

        provider.WaitForAssertion(() => provider.FindButton("取消"));
        provider.FindButton("取消").Click();

        Assert.Multiple(() => {
            Assert.That(cancelCount, Is.EqualTo(1));
            Assert.That(visibleChanges, Is.EqualTo(new[] { false }));
        });
    }

    [Test]
    public void Busy_DisablesBothActions() {
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        RenderDialog(Profile, parameters => parameters.Add(p => p.Busy, true));

        provider.WaitForAssertion(() => Assert.Multiple(() => {
            Assert.That(provider.FindButton("正在移除…").IsDisabled(), Is.True);
            Assert.That(provider.FindButton("取消").IsDisabled(), Is.True);
        }));
    }

    private void RenderDialog(
        ClaudeAccountProfile profile,
        Action<ComponentParameterCollectionBuilder<ClaudeRemoveAccountDialog>>? configure = null
    ) {
        Context.Render<ClaudeRemoveAccountDialog>(parameters => {
            parameters
                .Add(p => p.Visible, true)
                .Add(p => p.Profile, profile);
            configure?.Invoke(parameters);
        });
    }
}
