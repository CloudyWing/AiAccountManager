using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Components.Tests;

/// <summary>
/// 為每個測試建立獨立的 bUnit 環境，並註冊 MudBlazor 所需服務。
/// </summary>
public abstract class ComponentTestBase {
    private BunitContext? context;

    /// <summary>
    /// 目前測試使用的 bUnit 環境。
    /// </summary>
    protected BunitContext Context => context ?? throw new InvalidOperationException("bUnit 環境尚未建立。");

    [SetUp]
    public void SetUpContext() {
        context = new BunitContext();
        // MudBlazor 元件會呼叫多個 JS interop；元件測試只驗證 Blazor 端行為，因此採寬鬆模式。
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices(options => {
            options.PopoverOptions.CheckForPopoverProvider = false;
        });
    }

    [TearDown]
    public async Task TearDownContextAsync() {
        if (context is not null) {
            await context.DisposeAsync();
            context = null;
        }
    }

    /// <summary>
    /// 渲染 MudBlazor 的對話框容器，供以 <c>Visible</c> 控制的內嵌對話框顯示內容。
    /// </summary>
    /// <returns>對話框容器。</returns>
    protected IRenderedComponent<MudDialogProvider> RenderDialogProvider() {
        Context.Render<MudPopoverProvider>();
        return Context.Render<MudDialogProvider>();
    }

    protected static void AssertTitleInTitleSlot(IRenderedComponent<MudDialogProvider> provider, string expectedTitle) {
        string? titleId = provider.Find("[role='dialog']").GetAttribute("aria-labelledby");
        Assert.That(titleId, Is.Not.Null.And.Not.Empty);
        IElement title = provider.Find($"[id='{titleId}']");
        IElement content = provider.Find(".aam-dialog-content");

        Assert.Multiple(() => {
            Assert.That(title.TextContent.Trim(), Is.EqualTo(expectedTitle));
            Assert.That(title.ClassList, Does.Contain("aam-dialog-title"));
            Assert.That(title.QuerySelectorAll("h6"), Has.Count.EqualTo(1));
            Assert.That(content.QuerySelectorAll("h6"), Is.Empty);
            Assert.That(content.TextContent, Does.Not.Contain(expectedTitle));
        });
    }
}
