using Bunit;
using NUnit.Framework;
using CloudyWing.AiAccountManager.Desktop.Components.Shared;

namespace CloudyWing.AiAccountManager.Components.Tests.Shared;

[TestFixture]
public sealed class AccountAvatarTests : ComponentTestBase {
    [TestCase("alpha", "A")]
    [TestCase("  beta  ", "B")]
    [TestCase("小明", "小")]
    [TestCase("   ", "?")]
    public void Render_ShowsFirstTextElementAsInitial(string displayName, string expectedInitial) {
        IRenderedComponent<AccountAvatar> cut = Context.Render<AccountAvatar>(parameters => parameters
            .Add(p => p.DisplayName, displayName)
            .Add(p => p.Provider, "other"));

        Assert.That(cut.Find(".account-avatar").TextContent.Trim(), Is.EqualTo(expectedInitial));
    }

    [Test]
    public void Render_KeepsSurrogatePairInitialIntact() {
        IRenderedComponent<AccountAvatar> cut = Context.Render<AccountAvatar>(parameters => parameters
            .Add(p => p.DisplayName, "😀 smile")
            .Add(p => p.Provider, "other"));

        Assert.That(cut.Find(".account-avatar").TextContent.Trim(), Is.EqualTo("😀"));
    }

    [TestCase("claude", ".account-avatar-brand-claude")]
    [TestCase("codex", ".account-avatar-brand-codex")]
    public void Render_ShowsProviderBrandBadge(string provider, string badgeSelector) {
        IRenderedComponent<AccountAvatar> cut = Context.Render<AccountAvatar>(parameters => parameters
            .Add(p => p.DisplayName, "Alpha")
            .Add(p => p.Provider, provider));

        Assert.Multiple(() => {
            Assert.That(cut.Find(".account-avatar").GetAttribute("data-provider"), Is.EqualTo(provider));
            Assert.That(cut.FindAll(badgeSelector), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Render_IconOnly_HidesInitialAndBadge() {
        IRenderedComponent<AccountAvatar> cut = Context.Render<AccountAvatar>(parameters => parameters
            .Add(p => p.DisplayName, "Alpha")
            .Add(p => p.Provider, "claude")
            .Add(p => p.IconOnly, true));

        Assert.Multiple(() => {
            Assert.That(cut.Find(".account-avatar").ClassList, Does.Contain("account-avatar-icon-only"));
            Assert.That(cut.Find(".account-avatar").TextContent.Trim(), Is.Empty);
            Assert.That(cut.FindAll(".account-avatar-brand"), Is.Empty);
        });
    }
}
