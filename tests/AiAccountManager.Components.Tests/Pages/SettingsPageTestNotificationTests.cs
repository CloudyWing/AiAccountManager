using Bunit;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Desktop;
using CloudyWing.AiAccountManager.Desktop.Components.Pages;
using CloudyWing.AiAccountManager.Desktop.Components.Settings;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Components.Tests.Pages;

[TestFixture]
public sealed class SettingsPageTestNotificationTests : ComponentTestBase {
    private IRenderedComponent<MudSnackbarProvider> snackbars = null!;

    [Test]
    public async Task TestNotificationButton_NotificationsDisabled_IsEnabled() {
        FakeTestNotificationService service = new();

        IRenderedComponent<SettingsPage> cut = await RenderPageAsync(service, notificationsEnabled: false);

        Assert.That(cut.FindButton("傳送測試通知").IsDisabled(), Is.False);
    }

    [Test]
    public async Task TestNotificationButton_WhileSending_IsDisabledThenRestored() {
        FakeTestNotificationService service = new();
        IRenderedComponent<SettingsPage> cut = await RenderPageAsync(service, notificationsEnabled: true);

        Task click = cut.FindButton("傳送測試通知").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.That(cut.FindButton("傳送中…").IsDisabled(), Is.True));
        service.Complete(true);
        await click;

        cut.WaitForAssertion(() => Assert.That(cut.FindButton("傳送測試通知").IsDisabled(), Is.False));
        Assert.That(service.CallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task TestNotificationButton_Sent_ShowsSuccessSnackbar() {
        FakeTestNotificationService service = new();
        IRenderedComponent<SettingsPage> cut = await RenderPageAsync(service, notificationsEnabled: true);

        Task click = cut.FindButton("傳送測試通知").ClickAsync(new MouseEventArgs());
        service.Complete(true);
        await click;

        snackbars.WaitForAssertion(() => Assert.That(snackbars.Markup, Does.Contain("已傳送測試通知")));
    }

    [TestCase(false, "無法傳送測試通知")]
    [TestCase(true, "傳送測試通知時發生錯誤")]
    public async Task TestNotificationButton_FailedOrThrows_ShowsErrorSnackbar(bool throws, string expectedMessage) {
        FakeTestNotificationService service = new();
        IRenderedComponent<SettingsPage> cut = await RenderPageAsync(service, notificationsEnabled: true);

        Task click = cut.FindButton("傳送測試通知").ClickAsync(new MouseEventArgs());
        if (throws) {
            service.Fail(new InvalidOperationException("synthetic failure"));
        } else {
            service.Complete(false);
        }
        await click;

        snackbars.WaitForAssertion(() => Assert.That(snackbars.Markup, Does.Contain(expectedMessage)));
        cut.WaitForAssertion(() => Assert.That(cut.FindButton("傳送測試通知").IsDisabled(), Is.False));
    }

    private async Task<IRenderedComponent<SettingsPage>> RenderPageAsync(
        FakeTestNotificationService service,
        bool notificationsEnabled
    ) {
        SettingsService settings = new(
            new FakeSettingsRepository(AppSettings.Defaults with { LowQuotaNotificationsEnabled = notificationsEnabled }),
            new FakeCatalogRepository()
        );
        await settings.InitializeAsync(CancellationToken.None);
        Context.Services.AddSingleton(settings);
        Context.Services.AddSingleton<ILowQuotaTestNotificationService>(service);
        Context.ComponentFactories.AddStub<UpdateSettings>();
        Context.Render<MudPopoverProvider>();
        snackbars = Context.Render<MudSnackbarProvider>();
        return Context.Render<SettingsPage>();
    }

    private sealed class FakeTestNotificationService : ILowQuotaTestNotificationService {
        private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public Task<bool> SendAsync(CancellationToken cancellationToken) {
            CallCount++;
            return completion.Task;
        }

        public void Complete(bool result) => completion.SetResult(result);

        public void Fail(Exception exception) => completion.SetException(exception);
    }

    private sealed class FakeSettingsRepository(AppSettings settings) : ISettingsRepository {
        public Task<ISettingsRepository.Snapshot> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ISettingsRepository.Snapshot(settings, "settings-hash"));

        public Task<ISettingsRepository.SaveResult> SaveAsync(
            AppSettings newSettings,
            string expectedSha256,
            CancellationToken cancellationToken
        ) => Task.FromResult(new ISettingsRepository.SaveResult(false, null, false));
    }

    private sealed class FakeCatalogRepository : IAccountCatalogRepository {
        public Task<IAccountCatalogRepository.Snapshot> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new IAccountCatalogRepository.Snapshot([], "catalog-hash"));

        public Task<IAccountCatalogRepository.SaveResult> SaveAsync(
            IReadOnlyList<Application.Accounts.CodexAccountProfile> accounts,
            string expectedSha256,
            CancellationToken cancellationToken
        ) => Task.FromResult(new IAccountCatalogRepository.SaveResult(false, null, false));
    }
}
