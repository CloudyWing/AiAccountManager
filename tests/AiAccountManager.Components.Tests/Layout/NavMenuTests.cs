using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using CloudyWing.AiAccountManager.Desktop.Components.Layout;

namespace CloudyWing.AiAccountManager.Components.Tests.Layout;

[TestFixture]
public sealed class NavMenuTests : ComponentTestBase {
    [Test]
    public void Render_ListsEveryManagementPage() {
        IRenderedComponent<NavMenu> cut = Context.Render<NavMenu>();

        IReadOnlyList<(string? Href, string Text)> links = cut.FindAll("a")
            .Select(link => (link.GetAttribute("href"), link.TextContent.Trim()))
            .ToArray();

        Assert.That(links, Is.EqualTo(new[] {
            ("/", "儀表板"),
            ("/claude", "Claude"),
            ("/codex", "Codex"),
            ("/settings", "設定")
        }));
    }

    [TestCase("/", "儀表板")]
    [TestCase("/claude", "Claude")]
    [TestCase("/codex", "Codex")]
    [TestCase("/settings", "設定")]
    public void Navigate_MarksOnlyCurrentPageActive(string path, string expectedText) {
        IRenderedComponent<NavMenu> cut = Context.Render<NavMenu>();

        Context.Services.GetRequiredService<NavigationManager>().NavigateTo(path);

        cut.WaitForAssertion(() => {
            IElement[] active = cut.FindAll("a.active").ToArray();
            Assert.That(active.Select(link => link.TextContent.Trim()), Is.EqualTo(new[] { expectedText }));
        });
    }
}
