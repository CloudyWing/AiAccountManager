using AngleSharp.Dom;
using Bunit;
using NUnit.Framework;
using CloudyWing.AiAccountManager.Desktop.Components.Shared;

namespace CloudyWing.AiAccountManager.Components.Tests.Shared;

[TestFixture]
public sealed class QuotaBarTests : ComponentTestBase {
    [TestCase(100d, "healthy", "充足")]
    [TestCase(50d, "healthy", "充足")]
    [TestCase(49.9d, "low", "偏低")]
    [TestCase(20d, "low", "偏低")]
    [TestCase(19.9d, "critical", "不足")]
    [TestCase(0d, "critical", "不足")]
    public void Render_UsesThresholdsForLevelAndAccessibleName(double remaining, string expectedLevel, string expectedText) {
        IRenderedComponent<QuotaBar> cut = Context.Render<QuotaBar>(parameters => parameters
            .Add(p => p.Label, "5 小時")
            .Add(p => p.RemainingPercent, remaining));

        IElement bar = cut.Find(".quota-bar");
        Assert.Multiple(() => {
            Assert.That(bar.GetAttribute("data-level"), Is.EqualTo(expectedLevel));
            Assert.That(bar.GetAttribute("aria-label"), Does.StartWith("5 小時，"));
            Assert.That(bar.GetAttribute("aria-label"), Does.EndWith($"% 剩餘，{expectedText}"));
            Assert.That(cut.Find(".quota-bar-level").TextContent, Is.EqualTo(expectedText));
        });
    }

    [Test]
    public void Render_WithoutResetText_OmitsResetCaption() {
        IRenderedComponent<QuotaBar> cut = Context.Render<QuotaBar>(parameters => parameters
            .Add(p => p.Label, "每週")
            .Add(p => p.RemainingPercent, 80d));

        Assert.That(cut.Markup, Does.Not.Contain("重設："));
    }

    [Test]
    public void Render_WithResetText_ShowsResetCaption() {
        IRenderedComponent<QuotaBar> cut = Context.Render<QuotaBar>(parameters => parameters
            .Add(p => p.Label, "每週")
            .Add(p => p.RemainingPercent, 80d)
            .Add(p => p.ResetText, "2026/10/31 18:00"));

        Assert.That(cut.Markup, Does.Contain("重設：2026/10/31 18:00"));
    }

    [Test]
    public void SetParameters_UpdatesLevelWhenRemainingDrops() {
        IRenderedComponent<QuotaBar> cut = Context.Render<QuotaBar>(parameters => parameters
            .Add(p => p.Label, "5 小時")
            .Add(p => p.RemainingPercent, 75d));

        cut.Render(parameters => parameters.Add(p => p.RemainingPercent, 10d));

        Assert.That(cut.Find(".quota-bar").GetAttribute("data-level"), Is.EqualTo("critical"));
    }
}
