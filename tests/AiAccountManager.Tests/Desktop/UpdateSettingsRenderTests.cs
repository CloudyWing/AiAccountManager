using System.Net;
using System.Text.RegularExpressions;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Application.Updates;
using CloudyWing.AiAccountManager.Desktop.Components.Settings;
using CloudyWing.AiAccountManager.Tests.Updates;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class UpdateSettingsRenderTests {
    [Test]
    public async Task Render_FeedChangesAwayAndBack_DisablesApply() {
        const string feed = "https://updates.example.test/a";
        UpdateHarness harness = await UpdateHarness.CreateAsync(feed);
        using (harness.Scheduler) {
            await harness.Service.CheckAsync(CancellationToken.None);
            await harness.Service.DownloadAsync(CancellationToken.None);
            await harness.Settings.SaveAsync(harness.Settings.Settings! with { UpdateFeedUrl = "https://updates.example.test/b" }, CancellationToken.None);
            await harness.Settings.SaveAsync(harness.Settings.Settings! with { UpdateFeedUrl = feed }, CancellationToken.None);
            await using ServiceProvider services = new ServiceCollection()
                .AddLogging()
                .AddMudServices()
                .AddSingleton<IJSRuntime, FakeJsRuntime>()
                .AddSingleton<NavigationManager, FakeNavigationManager>()
                .AddSingleton<SettingsService>(harness.Settings)
                .AddSingleton<UpdateService>(harness.Service)
                .BuildServiceProvider();
            await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());

            string html = await renderer.Dispatcher.InvokeAsync(async () => {
                HtmlRootComponent component = await renderer.RenderComponentAsync<UpdateSettings>();
                return WebUtility.HtmlDecode(component.ToHtmlString());
            });

            Match apply = Regex.Matches(html, "<button\\b[^>]*>.*?</button>", RegexOptions.Singleline)
                .Cast<Match>().Single(match => match.Value.Contains("套用並重新啟動", StringComparison.Ordinal));
            Assert.That(apply.Value, Does.Contain("disabled"));
        }
    }

    [TestCase(null, "預設", UpdateFeedResolver.DefaultFeedUrl)]
    [TestCase("https://updates.example.test/custom", "自訂", "https://updates.example.test/custom")]
    public async Task Render_SavedFeed_ShowsEffectiveSource(string? feed, string label, string expected) {
        UpdateHarness harness = await UpdateHarness.CreateAsync(feed);
        using (harness.Scheduler) {
            await using ServiceProvider services = new ServiceCollection()
                .AddLogging()
                .AddMudServices()
                .AddSingleton<IJSRuntime, FakeJsRuntime>()
                .AddSingleton<NavigationManager, FakeNavigationManager>()
                .AddSingleton<SettingsService>(harness.Settings)
                .AddSingleton<UpdateService>(harness.Service)
                .BuildServiceProvider();
            await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());

            string html = await renderer.Dispatcher.InvokeAsync(async () => {
                HtmlRootComponent component = await renderer.RenderComponentAsync<UpdateSettings>();
                return WebUtility.HtmlDecode(component.ToHtmlString());
            });

            Assert.That(html, Does.Contain(label));
            Assert.That(html, Does.Contain(expected));
            Assert.That(html, Does.Not.Contain("尚未設定更新來源"));
        }
    }

    private sealed class FakeNavigationManager : NavigationManager {
        public FakeNavigationManager() {
            Initialize("http://localhost/", "http://localhost/settings");
        }

        protected override void NavigateToCore(string uri, bool forceLoad) {
            throw new InvalidOperationException("Navigation is unavailable during static rendering.");
        }
    }

    private sealed class FakeJsRuntime : IJSRuntime {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) {
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) {
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
