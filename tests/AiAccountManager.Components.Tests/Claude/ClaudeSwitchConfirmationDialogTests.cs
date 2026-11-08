using Bunit;
using MudBlazor;
using NUnit.Framework;
using CloudyWing.AiAccountManager.Desktop.Components.Claude;

namespace CloudyWing.AiAccountManager.Components.Tests.Claude;

[TestFixture]
public sealed class ClaudeSwitchConfirmationDialogTests : ComponentTestBase {
    [Test]
    public void PlacesTitleInTitleSlot() {
        IRenderedComponent<MudDialogProvider> provider = RenderDialogProvider();
        Context.Render<ClaudeSwitchConfirmationDialog>(parameters => parameters
            .Add(p => p.Visible, true)
            .Add(p => p.SourceDisplayName, "Alpha")
            .Add(p => p.SourceIdentity, "a***@example.test")
            .Add(p => p.TargetDisplayName, "Beta")
            .Add(p => p.TargetIdentity, "b***@example.test"));

        provider.WaitForAssertion(() => AssertTitleInTitleSlot(provider, "確認切換 Claude Desktop 帳號"));
    }
}
