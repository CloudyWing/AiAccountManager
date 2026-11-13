using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using NUnit.Framework;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Operations;
using CloudyWing.AiAccountManager.Application.Runtime;
using CloudyWing.AiAccountManager.Application.Security;
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Desktop;
using CloudyWing.AiAccountManager.Desktop.Components.Claude;
using CloudyWing.AiAccountManager.Desktop.Components.Codex;
using CloudyWing.AiAccountManager.Desktop.Components.Pages;
using CloudyWing.AiAccountManager.Tests.Codex;

namespace CloudyWing.AiAccountManager.Tests.Claude;

[TestFixture]
public sealed class ClaudeComponentRenderTests {
    [Test]
    public async Task CodexCard_ShowsLastAutoUseInTaiwanTime() {
        QuotaTestContext context = new();
        CodexResetCreditAttempt attempt = new(context.Account.AccountId, "credit-1", DateTimeOffset.Parse("2026-10-28T06:29:38Z"), Guid.NewGuid().ToString(), "pending");
        string html = WebUtility.HtmlDecode(await RenderAsync<CodexAccountCard>(new Dictionary<string, object?> {
            [nameof(CodexAccountCard.Account)] = context.Account,
            [nameof(CodexAccountCard.LastAutoUse)] = attempt
        }));
        Assert.That(html, Does.Contain("上次自動使用：10/28 14:29・結果未確認"));
    }

    [Test]
    public async Task AccountCard_ShowsResetGrantInTaiwanTime() {
        string html = await RenderAsync<ClaudeAccountCard>(new Dictionary<string, object?> {
            [nameof(ClaudeAccountCard.DisplayName)] = "Alpha",
            [nameof(ClaudeAccountCard.MaskedIdentity)] = "a***@example.test",
            [nameof(ClaudeAccountCard.ResetGrants)] = new[] {
                new ClaudeResetGrant("one", "額外重置", 3, 2, null,
                    DateTimeOffset.Parse("2026-10-28T06:29:38Z"), true, false, false, "session")
            }
        });
        Assert.That(html, Does.Contain("額外重置"));
        Assert.That(html, Does.Contain("10/28 14:29"));
        Assert.That(html, Does.Contain("未指定"));
        Assert.That(html, Does.Contain("可使用"));
    }

    [Test]
    public async Task AccountCard_ShowsCurrentDesktopIdentityAndQuotaValues() {
        string html = await RenderAsync<ClaudeAccountCard>(new Dictionary<string, object?> {
            [nameof(ClaudeAccountCard.DisplayName)] = "Alpha",
            [nameof(ClaudeAccountCard.MaskedIdentity)] = "a***@example.test",
            [nameof(ClaudeAccountCard.OrganizationDisplayName)] = "org-12345678…abcdef",
            [nameof(ClaudeAccountCard.IdentityStatusText)] = "已驗證",
            [nameof(ClaudeAccountCard.IsCurrentDesktopProfile)] = true,
            [nameof(ClaudeAccountCard.QuotaStatusText)] = "最新",
            [nameof(ClaudeAccountCard.QuotaWindows)] = new[] {
                new ClaudeQuotaSnapshot.Window("session", 300, 24m, null),
                new ClaudeQuotaSnapshot.Window(
                    "weekly_all",
                    10080,
                    61.5m,
                    new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.Zero)
                ),
                new ClaudeQuotaSnapshot.Window(
                    "weekly_scoped:Fable",
                    10080,
                    8m,
                    new DateTimeOffset(2026, 10, 31, 10, 0, 1, TimeSpan.Zero)
                )
            },
            [nameof(ClaudeAccountCard.QuotaCheckedAtText)] = "2026/10/26 09:00",
            [nameof(ClaudeAccountCard.CanRefreshQuota)] = true,
            [nameof(ClaudeAccountCard.CanRequestSwitch)] = true
        });

        string expectedResetTime = new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.Zero)
            .ToLocalTime()
            .ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture);

        Assert.Multiple(() => {
            Assert.That(html, Does.Contain("Alpha"));
            Assert.That(html, Does.Contain("a***@example.test"));
            Assert.That(html, Does.Contain("目前 Desktop 帳號"));
            Assert.That(html, Does.Contain("76% 剩餘"));
            Assert.That(html, Does.Contain("每週（全部）"));
            Assert.That(html, Does.Contain("38.5% 剩餘"));
            Assert.That(html, Does.Contain("每週（Fable）"));
            Assert.That(html, Does.Contain("92% 剩餘"));
            Assert.That(html, Does.Contain($"重設：{expectedResetTime}"));
            Assert.That(html, Does.Contain("查詢額度"));
        });
    }

    [Test]
    public async Task AccountCard_UnknownQuotaShowsReasonWithoutInventedPercentages() {
        string html = await RenderAsync<ClaudeAccountCard>(new Dictionary<string, object?> {
            [nameof(ClaudeAccountCard.DisplayName)] = "Beta",
            [nameof(ClaudeAccountCard.MaskedIdentity)] = "b***@example.test",
            [nameof(ClaudeAccountCard.QuotaStatusText)] = "未知",
            [nameof(ClaudeAccountCard.QuotaMessage)] = "尚未查詢此 Claude 帳號的額度。",
            [nameof(ClaudeAccountCard.QuotaWindows)] = Array.Empty<ClaudeQuotaSnapshot.Window>(),
            [nameof(ClaudeAccountCard.CanRefreshQuota)] = true
        });

        Assert.Multiple(() => {
            Assert.That(html, Does.Contain("未知"));
            Assert.That(html, Does.Contain("尚未查詢此 Claude 帳號的額度。"));
            Assert.That(html, Does.Not.Contain("% 剩餘"));
        });
    }

    [Test]
    public async Task AccountCard_BusyStateDisablesActionsAndShowsProgress() {
        string html = await RenderAsync<ClaudeAccountCard>(new Dictionary<string, object?> {
            [nameof(ClaudeAccountCard.DisplayName)] = "Alpha",
            [nameof(ClaudeAccountCard.MaskedIdentity)] = "a***@example.test",
            [nameof(ClaudeAccountCard.IsBusy)] = true,
            [nameof(ClaudeAccountCard.IsQuotaLoading)] = true,
            [nameof(ClaudeAccountCard.CanRefreshQuota)] = true,
            [nameof(ClaudeAccountCard.CanRequestSwitch)] = true
        });

        Assert.Multiple(() => {
            Assert.That(html, Does.Contain("正在查詢此帳號的額度。"));
            Assert.That(html, Does.Contain("disabled"));
            Assert.That(html, Does.Contain("正在查詢…"));
        });
    }

    [Test]
    public async Task SwitchDialog_ShowsFullHistoryReplacementAndCancelAction() {
        string html = await RenderDialogAsync(new Dictionary<string, object?> {
            [nameof(ClaudeSwitchConfirmationDialog.Visible)] = true,
            [nameof(ClaudeSwitchConfirmationDialog.SourceDisplayName)] = "Alpha",
            [nameof(ClaudeSwitchConfirmationDialog.SourceIdentity)] = "a***@example.test",
            [nameof(ClaudeSwitchConfirmationDialog.TargetDisplayName)] = "Beta",
            [nameof(ClaudeSwitchConfirmationDialog.TargetIdentity)] = "b***@example.test",
            [nameof(ClaudeSwitchConfirmationDialog.CanConfirm)] = true,
            [nameof(ClaudeSwitchConfirmationDialog.RequiresHistoryOverwrite)] = true,
            [nameof(ClaudeSwitchConfirmationDialog.SourceSessionCount)] = 0,
            [nameof(ClaudeSwitchConfirmationDialog.TargetSessionCount)] = 20,
            [nameof(ClaudeSwitchConfirmationDialog.PreviewMessage)] = "來源與目標帳號已完成預檢。"
        });

        Assert.Multiple(() => {
            Assert.That(html, Does.Contain("目前來源"));
            Assert.That(html, Does.Contain("切換目標"));
            Assert.That(html, Does.Contain("來源帳號的完整 Code Session 清單覆蓋目標帳號的清單"));
            Assert.That(html, Does.Contain("來源清單 0 個 Session，目標目前有 20 個 Session。"));
            Assert.That(html, Does.Contain("目標帳號的 Session 清單會被完整取代"));
            Assert.That(html, Does.Contain("確認切換並同步"));
            Assert.That(html, Does.Contain("取消"));
        });
    }

    [Test]
    public async Task SwitchDialog_RecoveryStateKeepsRecoveryActionReachableAndBlocksSwitch() {
        string html = await RenderDialogAsync(new Dictionary<string, object?> {
            [nameof(ClaudeSwitchConfirmationDialog.Visible)] = true,
            [nameof(ClaudeSwitchConfirmationDialog.SourceDisplayName)] = "Alpha",
            [nameof(ClaudeSwitchConfirmationDialog.SourceIdentity)] = "a***@example.test",
            [nameof(ClaudeSwitchConfirmationDialog.TargetDisplayName)] = "Beta",
            [nameof(ClaudeSwitchConfirmationDialog.TargetIdentity)] = "b***@example.test",
            [nameof(ClaudeSwitchConfirmationDialog.CanConfirm)] = false,
            [nameof(ClaudeSwitchConfirmationDialog.IsRecoveryRequired)] = true,
            [nameof(ClaudeSwitchConfirmationDialog.RecoveryMessage)] = "復原失敗，請稍後重試。",
            [nameof(ClaudeSwitchConfirmationDialog.OnRecover)] = EventCallback.Factory.Create(this, () => Task.CompletedTask)
        });

        Assert.Multiple(() => {
            Assert.That(html, Does.Contain("復原失敗，請稍後重試。"));
            Assert.That(html, Does.Contain("嘗試復原"));
            Assert.That(html, Does.Contain("確認切換"));
            Assert.That(html, Does.Contain("disabled"));
        });
    }

    [Test]
    public async Task ClaudePage_WithNoSavedProfiles_ShowsVerifiedIdentityAndEmptyState() {
        await using ServiceProvider services = CreatePageServices(new FakeClaudeAccountRepository());
        string html = await RenderAsync<ClaudePage>(services);

        Assert.Multiple(() => {
            Assert.That(html, Does.Contain("目前 Claude Desktop 身分"));
            Assert.That(html, Does.Contain("已驗證"));
            Assert.That(html, Does.Contain("a***@example.test"));
            Assert.That(html, Does.Contain("尚未保存 Claude Desktop 帳號"));
            Assert.That(html, Does.Contain("重新讀取身分"));
        });
    }

    [Test]
    public async Task ClaudePage_ShowsSavedProfilesAndMarksTheCurrentDesktopAccount() {
        FakeClaudeAccountRepository repository = new() {
            Profiles = [
                CreateProfile("Alpha", "account-alpha", "organization-alpha"),
                CreateProfile("Beta", "account-beta", "organization-beta")
            ]
        };
        await using ServiceProvider services = CreatePageServices(repository);

        string html = await RenderAsync<ClaudePage>(services);

        Assert.Multiple(() => {
            Assert.That(html, Does.Contain("已保存帳號"));
            Assert.That(html, Does.Contain("2 個"));
            Assert.That(html, Does.Contain("目前 Desktop 帳號"));
            Assert.That(html, Does.Contain("Alpha"));
            Assert.That(html, Does.Contain("Beta"));
            Assert.That(html, Does.Contain("尚未查詢此 Claude 帳號的額度。"));
        });
    }

    [Test]
    public async Task ClaudePage_RendersBeforeLoad_WhenGateIsOccupied() {
        FakeClaudeAccountRepository repository = new() {
            Profiles = [CreateProfile("Alpha", "account-alpha", "organization-alpha")]
        };
        await using ServiceProvider services = CreatePageServices(repository);
        ClaudeAccountService accounts = services.GetRequiredService<ClaudeAccountService>();
        await accounts.GetPageStateAsync(CancellationToken.None);
        ClaudeOperationGate gate = services.GetRequiredService<ClaudeOperationGate>();
        ClaudeOperationGate.Lease lease = await gate.EnterAsync(CancellationToken.None);
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());

        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudThemeProvider>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudPopoverProvider>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudDialogProvider>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudSnackbarProvider>());
        HtmlRootComponent root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ClaudePage>());
        string loading = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        await lease.DisposeAsync();
        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(5));
        string loaded = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));

        Assert.That(loading, Does.Contain("Alpha"));
        Assert.That(loading, Does.Contain("正在讀取最新 Claude Desktop 狀態"));
        Assert.That(loading, Does.Contain("disabled"));
        Assert.That(loaded, Does.Not.Contain("正在讀取最新 Claude Desktop 狀態"));
        Assert.That(loaded.Split("disabled", StringSplitOptions.None).Length,
            Is.LessThan(loading.Split("disabled", StringSplitOptions.None).Length));
    }

    [Test]
    public async Task ClaudePage_ShowsRefreshingState_WhileStateLoadIsPending() {
        FakeClaudeAccountRepository repository = new() {
            Profiles = [CreateProfile("Alpha", "account-alpha", "organization-alpha")]
        };
        FakeInstallationLocator installationLocator = new();
        await using ServiceProvider services = CreatePageServices(repository, installationLocator: installationLocator);
        ClaudeAccountService accounts = services.GetRequiredService<ClaudeAccountService>();
        await accounts.GetPageStateAsync(CancellationToken.None);
        TaskCompletionSource<bool> observeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        installationLocator.ObserveRelease = observeRelease;
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());

        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudThemeProvider>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudPopoverProvider>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudDialogProvider>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudSnackbarProvider>());
        HtmlRootComponent root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ClaudePage>());
        string loading = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        observeRelease.TrySetResult(true);
        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(5));
        string loaded = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));

        Assert.That(loading, Does.Contain("Alpha"));
        Assert.That(loading, Does.Contain("正在讀取最新 Claude Desktop 狀態"));
        Assert.That(loading, Does.Contain("disabled"));
        Assert.That(loaded, Does.Not.Contain("正在讀取最新 Claude Desktop 狀態"));
        Assert.That(loaded.Split("disabled", StringSplitOptions.None).Length,
            Is.LessThan(loading.Split("disabled", StringSplitOptions.None).Length));
    }

    [Test]
    public async Task ClaudePage_StaleOnEntry_RefreshesOnlyExpiredAfterRendering() {
        ClaudeAccountProfile stale = CreateProfile("Alpha", "account-alpha", "organization-alpha");
        ClaudeAccountProfile fresh = CreateProfile("Beta", "account-beta", "organization-beta");
        FakeClaudeAccountRepository repository = new() { Profiles = [fresh, stale] };
        FakeQuotaRepository quotaRepository = new() { BlockSave = true };
        quotaRepository.Seed(stale.ProfileId, DateTimeOffset.UtcNow.AddHours(-1));
        quotaRepository.Seed(fresh.ProfileId, DateTimeOffset.UtcNow);
        await using ServiceProvider services = CreatePageServices(repository, quotaRepository: quotaRepository);
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());

        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudThemeProvider>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudPopoverProvider>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudDialogProvider>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudSnackbarProvider>());
        HtmlRootComponent root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<ClaudePage>());
        string html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        IHandleAfterRender page = services.GetRequiredService<RecordingComponentActivator>().GetPage<ClaudePage>();
        Assert.That(html, Does.Contain("Alpha"));
        Assert.That(html, Does.Contain("Beta"));
        Assert.That(quotaRepository.SaveStarted.Task.IsCompleted, Is.False);
        await renderer.Dispatcher.InvokeAsync(page.OnAfterRenderAsync);
        await quotaRepository.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try {
            Assert.That(quotaRepository.SavedProfileIds, Is.EqualTo(new[] { stale.ProfileId }));
            Assert.That(quotaRepository.SaveRelease.Task.IsCompleted, Is.False);
        } finally {
            quotaRepository.SaveRelease.TrySetResult(true);
        }
    }

    [Test]
    public async Task ClaudePage_AfterRenderRunsAfterDisposal_DoesNotThrow() {
        ClaudeAccountProfile profile = CreateProfile("Alpha", "account-alpha", "organization-alpha");
        FakeClaudeAccountRepository repository = new() { Profiles = [profile] };
        await using ServiceProvider services = CreatePageServices(repository);
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<ClaudePage>());
        ClaudePage page = (ClaudePage)services.GetRequiredService<RecordingComponentActivator>().GetPage<ClaudePage>();
        page.AccountQuery = profile.ProfileId.ToString();

        await renderer.Dispatcher.InvokeAsync(async () => await page.DisposeAsync());
        await renderer.Dispatcher.InvokeAsync(((IHandleAfterRender)page).OnAfterRenderAsync);
    }

    [Test]
    public async Task ClaudePage_LoadCompletesAfterDisposal_DoesNotShowErrorOrThrow() {
        FakeInstallationLocator locator = new() {
            ObserveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using ServiceProvider services = CreatePageServices(new FakeClaudeAccountRepository(), installationLocator: locator);
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        HtmlRootComponent root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ClaudePage>());
        await locator.ObserveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ClaudePage page = (ClaudePage)services.GetRequiredService<RecordingComponentActivator>().GetPage<ClaudePage>();
        Assert.That(root.QuiescenceTask.IsCompleted, Is.False);

        try {
            await renderer.Dispatcher.InvokeAsync(async () => await page.DisposeAsync());
        } finally {
            locator.ObserveRelease.TrySetResult(true);
        }

        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(5));
        string html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.That(html, Does.Not.Contain("無法載入 Claude Desktop 帳號與身分狀態"));
    }

    [Test]
    public async Task CodexPage_InitializationCompletesAfterDisposal_DoesNotThrow() {
        QuotaTestContext context = new();
        SettingsService settings = new(context.SettingsRepository, context.Catalog);
        await settings.InitializeAsync(CancellationToken.None);
        TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CodexDesktopSwitchService desktopSwitch = CodexDesktopSwitchServiceTests.CreateBlockingRenderingService(started, release);
        await using ServiceProvider services = CreateCodexPageServices(context, settings, new TestResetNewsClient(), desktopSwitch);
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        HtmlRootComponent root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<CodexPage>());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        CodexPage page = (CodexPage)services.GetRequiredService<RecordingComponentActivator>().GetPage<CodexPage>();
        Assert.That(root.QuiescenceTask.IsCompleted, Is.False);

        try {
            await renderer.Dispatcher.InvokeAsync(async () => await page.DisposeAsync());
        } finally {
            release.TrySetResult(true);
        }
        await root.QuiescenceTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task CodexPage_AfterRenderRunsAfterDisposal_DoesNotThrow() {
        QuotaTestContext context = new();
        SettingsService settings = new(context.SettingsRepository, context.Catalog);
        await settings.InitializeAsync(CancellationToken.None);
        await using ServiceProvider services = CreateCodexPageServices(context, settings, new TestResetNewsClient());
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CodexPage>());
        IHandleAfterRender page = services.GetRequiredService<RecordingComponentActivator>().GetPage<CodexPage>();

        await renderer.Dispatcher.InvokeAsync(async () => await ((IAsyncDisposable)page).DisposeAsync());
        await renderer.Dispatcher.InvokeAsync(page.OnAfterRenderAsync);
    }

    [Test]
    public async Task CodexPage_StaleOnEntry_RendersBeforeRefreshAndSkipsFreshAccount() {
        QuotaTestContext context = new();
        context.Clock.UtcNow = DateTimeOffset.UtcNow;
        CodexAccountProfile stale = context.Account;
        CodexAccountProfile fresh = stale with { AccountId = Guid.NewGuid() };
        context.Catalog.SetAccounts([fresh, stale]);
        await context.SeedQuotaAsync(context.CreateSnapshot(context.Clock.UtcNow.AddHours(-1)));
        await context.SnapshotRepository.SeedAsync(new QuotaSnapshotRecord(
            fresh.AccountId, fresh.IdentityFingerprint, fresh.ProfileRevision,
            context.CreateSnapshot(context.Clock.UtcNow), null
        ));
        SettingsService settings = new(context.SettingsRepository, context.Catalog);
        await settings.InitializeAsync(CancellationToken.None);
        CodexLocalIdentityService identity = CodexLocalIdentityServiceTests.CreateRenderingService([fresh, stale]);
        TaskCompletionSource<bool> releaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Session.AccountReadGate = releaseRead;
        await using ServiceProvider services = AddClaudeComponentRenderingServices(new ServiceCollection())
            .AddSingleton(settings)
            .AddSingleton(context.Service)
            .AddSingleton<IClock>(context.Clock)
            .AddSingleton<ICodexResetNewsClient, TestResetNewsClient>()
            .AddSingleton<CodexResetNewsService>()
            .AddSingleton<ICodexResetCreditAttemptRepository, TestAttemptRepository>()
            .AddSingleton<ICodexResetCreditDelay, TestCreditDelay>()
            .AddSingleton<CodexResetCreditAutoUseService>()
            .AddSingleton(identity)
            .AddSingleton(CodexProfileServiceTests.CreateRenderingService())
            .AddSingleton(CodexDesktopSwitchServiceTests.CreateRenderingService())
            .BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());

        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudThemeProvider>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudPopoverProvider>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudDialogProvider>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudSnackbarProvider>());
        HtmlRootComponent root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CodexPage>());
        string html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        IHandleAfterRender page = services.GetRequiredService<RecordingComponentActivator>().GetPage<CodexPage>();
        Assert.That(html, Does.Contain(stale.MaskedIdentity));
        Assert.That(context.Session.AccountReadStarted.Task.IsCompleted, Is.False);
        await renderer.Dispatcher.InvokeAsync(page.OnAfterRenderAsync);
        await context.Session.AccountReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try {
            Assert.That(context.Factory.CreatedAccountIds, Is.EqualTo(new[] { stale.AccountId }));
            Assert.That(releaseRead.Task.IsCompleted, Is.False);
        } finally {
            releaseRead.TrySetResult(true);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ClaudePage_RecommendationNavigation_LocatesExistingAccountOnly(bool exists) {
        Guid accountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        ClaudeAccountProfile profile = CreateProfile("Alpha", "account-alpha", "organization-alpha") with { ProfileId = accountId };
        FakeClaudeAccountRepository repository = new() { Profiles = [profile] };
        FakeQuotaRepository quotaRepository = new();
        quotaRepository.Seed(accountId, DateTimeOffset.UtcNow);
        await using ServiceProvider services = CreatePageServices(repository, quotaRepository: quotaRepository);
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        Guid requestedId = exists ? accountId : Guid.Parse("22222222-2222-2222-2222-222222222222");
        string query = AccountNavigation.Href("claude", requestedId).Split('=', 2)[1];
        HtmlRootComponent root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<ClaudePage>());
        string html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        ClaudeAccountService accounts = services.GetRequiredService<ClaudeAccountService>();
        ClaudeAccountService.CurrentIdentity current = accounts.CurrentPageState.CurrentIdentity;
        IHandleAfterRender page = services.GetRequiredService<RecordingComponentActivator>().GetPage<ClaudePage>();
        ((ClaudePage)page).AccountQuery = Uri.UnescapeDataString(query);
        await renderer.Dispatcher.InvokeAsync(page.OnAfterRenderAsync);

        string elementId = AccountNavigation.ElementId("claude", accountId);
        StaticRenderJsRuntime js = (StaticRenderJsRuntime)services.GetRequiredService<IJSRuntime>();
        Assert.Multiple(() => {
            Assert.That(html, Does.Contain($"id=\"{elementId}\""));
            Assert.That(js.ScrollElementIds, Is.EqualTo(exists ? new[] { elementId } : Array.Empty<string>()));
            Assert.That(accounts.CurrentPageState.CurrentIdentity, Is.EqualTo(current));
            Assert.That(repository.Transaction, Is.Null);
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task CodexPage_RecommendationNavigation_LocatesExistingAccountOnly(bool exists) {
        Guid accountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        QuotaTestContext context = new();
        context.Clock.UtcNow = DateTimeOffset.UtcNow;
        CodexAccountProfile profile = context.Account with { AccountId = accountId };
        context.Catalog.SetAccounts([profile]);
        await context.SnapshotRepository.SeedAsync(new QuotaSnapshotRecord(
            accountId, profile.IdentityFingerprint, profile.ProfileRevision,
            context.CreateSnapshot(context.Clock.UtcNow), null
        ));
        SettingsService settings = new(context.SettingsRepository, context.Catalog);
        await settings.InitializeAsync(CancellationToken.None);
        CodexLocalIdentityService identity = CodexLocalIdentityServiceTests.CreateRenderingService([profile]);
        await using ServiceProvider services = AddClaudeComponentRenderingServices(new ServiceCollection())
            .AddSingleton(settings)
            .AddSingleton(context.Service)
            .AddSingleton<IClock>(context.Clock)
            .AddSingleton<ICodexResetNewsClient, TestResetNewsClient>()
            .AddSingleton<CodexResetNewsService>()
            .AddSingleton<ICodexResetCreditAttemptRepository, TestAttemptRepository>()
            .AddSingleton<ICodexResetCreditDelay, TestCreditDelay>()
            .AddSingleton<CodexResetCreditAutoUseService>()
            .AddSingleton(identity)
            .AddSingleton(CodexProfileServiceTests.CreateRenderingService())
            .AddSingleton(CodexDesktopSwitchServiceTests.CreateRenderingService())
            .BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        Guid requestedId = exists ? accountId : Guid.Parse("22222222-2222-2222-2222-222222222222");
        string query = AccountNavigation.Href("codex", requestedId).Split('=', 2)[1];
        HtmlRootComponent root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CodexPage>());
        string html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Guid? currentId = identity.CurrentSnapshot.MatchedProfileAccountId;
        IHandleAfterRender page = services.GetRequiredService<RecordingComponentActivator>().GetPage<CodexPage>();
        ((CodexPage)page).AccountQuery = Uri.UnescapeDataString(query);
        await renderer.Dispatcher.InvokeAsync(page.OnAfterRenderAsync);

        string elementId = AccountNavigation.ElementId("codex", accountId);
        StaticRenderJsRuntime js = (StaticRenderJsRuntime)services.GetRequiredService<IJSRuntime>();
        Assert.Multiple(() => {
            Assert.That(html, Does.Contain($"id=\"{elementId}\""));
            Assert.That(js.ScrollElementIds, Is.EqualTo(exists ? new[] { elementId } : Array.Empty<string>()));
            Assert.That(identity.CurrentSnapshot.MatchedProfileAccountId, Is.EqualTo(currentId));
        });
    }

    [Test]
    public async Task ClaudePage_ShowsReadOnlyIdentityWhileDesktopRunsAndDisablesCapture() {
        ClaudeAccountProfile profile = CreateProfile("Alpha", "account-alpha", "organization-alpha");
        FakeClaudeAccountRepository repository = new() {
            Profiles = [profile]
        };
        await using ServiceProvider services = CreatePageServices(
            repository,
            processStatus: IClaudeDesktopProcessController.ProcessStatus.Running
        );

        string html = await RenderAsync<ClaudePage>(services);

        Assert.Multiple(() => {
            Assert.That(html, Does.Contain("目前 Claude Desktop 身分"));
            Assert.That(html, Does.Contain("已驗證"));
            Assert.That(html, Does.Contain("程序：執行中"));
            Assert.That(html, Does.Contain("請先正常結束 Claude Desktop，再保存目前帳號。"));
        });
    }

    [Test]
    public async Task ClaudePage_ExplainsVerifiedIdentityThatHasNoSavedProfileMatch() {
        ClaudeAccountProfile differentAccount = CreateProfile("Beta", "account-beta", "organization-beta");
        FakeClaudeAccountRepository repository = new() {
            Profiles = [differentAccount]
        };
        await using ServiceProvider services = CreatePageServices(repository);

        string html = await RenderAsync<ClaudePage>(services);

        Assert.That(
            html,
            Does.Contain("目前 Claude Desktop 帳號已驗證，但無法與已保存帳號安全配對，因此不能切換。")
        );
    }

    [Test]
    public async Task ClaudePage_PendingTransactionShowsRecoveryAndBlocksCaptureAndSwitch() {
        ClaudeAccountProfile source = CreateProfile("Alpha", "account-alpha", "organization-alpha");
        FakeClaudeAccountRepository repository = new() {
            Profiles = [source],
            Transaction = new(
                Guid.NewGuid(),
                source.ProfileId,
                Guid.NewGuid(),
                source.ProfileRevision,
                1,
                "fixture-installation",
                ClaudeSwitchTransaction.Stage.HistoryApplied,
                DateTimeOffset.Parse("2026-10-26T00:00:00Z"),
                DateTimeOffset.Parse("2026-10-26T00:00:00Z"),
                "FixtureRecovery"
            )
        };
        await using ServiceProvider services = CreatePageServices(repository);

        string html = await RenderAsync<ClaudePage>(services);

        Assert.Multiple(() => {
            Assert.That(html, Does.Contain("偵測到尚未完成的帳號切換"));
            Assert.That(html, Does.Contain("重試復原"));
            Assert.That(html, Does.Contain("請先完成待復原的切換"));
            Assert.That(html, Does.Contain("disabled"));
            Assert.That(html, Does.Contain("查詢額度"));
        });
    }

    [Test]
    public async Task ClaudePage_CommittedCleanupPendingShowsRetryWithoutRollbackState() {
        ClaudeAccountProfile profile = CreateProfile("Alpha", "account-alpha", "organization-alpha");
        FakeClaudeAccountRepository repository = new() {
            Profiles = [profile],
            Transaction = CreateCleanupPendingTransaction(profile)
        };
        await using ServiceProvider services = CreatePageServices(repository, throwOnCleanup: true);

        string html = await RenderAsync<ClaudePage>(services);

        Assert.Multiple(() => {
            Assert.That(html, Does.Contain("帳號切換已完成，尚有暫存資料待清理"));
            Assert.That(html, Does.Contain("重試暫存清理"));
            Assert.That(html, Does.Not.Contain("重試復原"));
            Assert.That(html, Does.Not.Contain("需要復原"));
            Assert.That(repository.Transaction!.CurrentStage,
                Is.EqualTo(ClaudeSwitchTransaction.Stage.CommittedCleanupPending));
        });
    }

    [Test]
    public async Task ClaudePage_RetriesCommittedCleanupOnLoadAndKeepsVerifiedTarget() {
        ClaudeAccountProfile profile = CreateProfile("Alpha", "account-alpha", "organization-alpha");
        FakeClaudeAccountRepository repository = new() {
            Profiles = [profile],
            Transaction = CreateCleanupPendingTransaction(profile)
        };
        await using ServiceProvider services = CreatePageServices(repository);

        string html = await RenderAsync<ClaudePage>(services);

        Assert.Multiple(() => {
            Assert.That(repository.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.Committed));
            Assert.That(html, Does.Contain("暫存資料已清理"));
            Assert.That(html, Does.Not.Contain("重試復原"));
            Assert.That(html, Does.Contain("目前 Claude Desktop 身分"));
        });
    }

    [Test]
    public async Task ClaudePage_LoadFailureShowsVisibleRetryMessage() {
        FakeClaudeAccountRepository repository = new() {
            ThrowOnCatalogLoad = true
        };
        await using ServiceProvider services = CreatePageServices(repository);

        string html = await RenderAsync<ClaudePage>(services);

        Assert.Multiple(() => {
            Assert.That(html, Does.Contain("無法載入 Claude Desktop 帳號與身分狀態"));
            Assert.That(html, Does.Contain("重新載入"));
        });
    }

    [TestCase("cancel")]
    [TestCase("selected")]
    [TestCase("missing")]
    public async Task CodexPage_ManualCreditUsesConfirmedSelection(string scenario) {
        QuotaTestContext context = new();
        context.Clock.UtcNow = DateTimeOffset.UtcNow;
        DateTimeOffset now = context.Clock.UtcNow;
        ResetCredit[] credits = [
            new("late", "codexRateLimits", "available", now, now.AddDays(3), null, null),
            new("early", "codexRateLimits", "available", now, now.AddDays(1), null, null)
        ];
        QuotaSuccessfulSnapshot snapshot = context.CreateSnapshot(now) with {
            ResetCredits = new ResetCreditSummary(2, scenario == "missing" ? null : credits)
        };
        await context.SeedQuotaAsync(snapshot);
        context.Session.ConsumeOutcome = "noCredit";
        SettingsService settings = new(context.SettingsRepository, context.Catalog);
        await settings.InitializeAsync(CancellationToken.None);
        await using ServiceProvider services = CreateCodexPageServices(context, settings, new TestResetNewsClient());
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        HtmlRootComponent dialogProvider = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<MudDialogProvider>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CodexPage>());
        CodexPage page = (CodexPage)services.GetRequiredService<RecordingComponentActivator>().GetPage<CodexPage>();
        Type type = typeof(CodexPage);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        await renderer.Dispatcher.InvokeAsync(() => type.GetMethod("BeginUseCredit", flags)!.Invoke(page, [context.Account.AccountId]));
        string? selected = (string?)type.GetField("creditId", flags)!.GetValue(page);
        Assert.That(selected, Is.EqualTo(scenario == "missing" ? null : "early"));
        if (scenario == "missing") {
            IDialogService dialogs = services.GetRequiredService<IDialogService>();
            await renderer.Dispatcher.InvokeAsync(() => dialogs.ShowAsync<ResetCreditDialog>(null, new DialogParameters {
                [nameof(ResetCreditDialog.Visible)] = true,
                [nameof(ResetCreditDialog.Snapshot)] = snapshot,
                [nameof(ResetCreditDialog.CreditId)] = selected
            }));
            string dialogHtml = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(dialogProvider.ToHtmlString));

            Assert.That(dialogHtml, Does.Contain("由系統挑選"));
        }
        if (scenario == "cancel") {
            await renderer.Dispatcher.InvokeAsync(() => type.GetMethod("CancelCredit", flags)!.Invoke(page, null));
            await renderer.Dispatcher.InvokeAsync(async () => await (Task)type.GetMethod("ConfirmCreditAsync", flags)!.Invoke(page, null)!);
            Assert.That(context.Session.ConsumeCount, Is.Zero);
        } else {
            await renderer.Dispatcher.InvokeAsync(async () => await (Task)type.GetMethod("ConfirmCreditAsync", flags)!.Invoke(page, null)!);
            Assert.That(context.Session.ConsumeCount, Is.EqualTo(1));
            Assert.That(context.Session.ConsumedCreditId, Is.EqualTo(scenario == "missing" ? null : "early"));
        }
    }

    [TestCase("scheduled", "預定下次重置：10/28 14:29")]
    [TestCase("none", "預定下次重置：目前沒有預定")]
    [TestCase("failure", "暫時無法取得")]
    public async Task CodexPage_ResetNewsRendersState(string scenario, string expected) {
        QuotaTestContext context = new();
        SettingsService settings = new(context.SettingsRepository, context.Catalog);
        await settings.InitializeAsync(CancellationToken.None);
        string payload = scenario == "scheduled"
            ? "{\"data\":{\"scheduled_reset\":{\"scheduled_for\":\"2026-10-28T06:29:38Z\"},\"latest_reset\":null}}"
            : "{\"data\":{\"scheduled_reset\":null,\"latest_reset\":null}}";
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(payload);
        TestResetNewsClient client = new() {
            Response = new(scenario == "failure" ? null : document.RootElement.Clone(), false, null)
        };
        await using ServiceProvider services = CreateCodexPageServices(context, settings, client);
        await services.GetRequiredService<CodexResetNewsService>().RefreshAsync(CancellationToken.None);
        string html = await RenderAsync<CodexPage>(services);
        Assert.That(html, Does.Contain(expected));
        if (scenario != "failure") { Assert.That(html, Does.Contain("最近一次重置：尚無最近一次重置")); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ClaudePage_HidesResetGrantsWhenMissingOrRefreshFailed(bool failed) {
        ClaudeAccountProfile profile = ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-alpha", "organization-alpha", alias: "Alpha");
        FakeClaudeAccountRepository repository = new() {
            Profiles = [profile],
            DesktopSnapshot = ClaudeTestData.CreateSnapshot("account-alpha", "fixture-token")
        };
        FakeQuotaRepository quotaRepository = new();
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(
            "{\"five_hour\":{\"utilization\":20},\"seven_day\":{\"utilization\":30}}"
        );
        ClaudeQuotaSnapshot snapshot = ClaudeQuotaResponseParser.Parse(json.RootElement, profile.ProfileId, DateTimeOffset.UtcNow).Snapshot!;
        if (failed) {
            snapshot = snapshot with { ResetGrants = [new ClaudeResetGrant("one", "額外重置", 3, 2, null, null, true, false, false, "session")] };
        }
        quotaRepository.SeedView(new ClaudeQuotaSnapshot.View(
            profile.ProfileId, snapshot, ClaudeQuotaSnapshot.RefreshStatus.Fresh,
            DateTimeOffset.UtcNow, null
        ));
        await using ServiceProvider services = CreatePageServices(repository, quotaRepository: quotaRepository);
        if (failed) {
            ClaudeQuotaSnapshot.View failedView = await services.GetRequiredService<ClaudeQuotaService>().RefreshAsync(profile.ProfileId, CancellationToken.None);
            Assert.That(failedView.Status, Is.EqualTo(ClaudeQuotaSnapshot.RefreshStatus.Stale));
            Assert.That(services.GetRequiredService<FakeOAuthClient>().UsageCallCount, Is.EqualTo(1));
        }
        string html = await RenderAsync<ClaudePage>(services);
        Assert.That(html, Does.Not.Contain("額外重置"));
        Assert.That(html, Does.Contain("80% 剩餘"));
        Assert.That(html, Does.Contain("70% 剩餘"));
    }

    private static ServiceProvider CreateCodexPageServices(
        QuotaTestContext context, SettingsService settings, TestResetNewsClient news,
        CodexDesktopSwitchService? desktopSwitch = null
    ) {
        return AddClaudeComponentRenderingServices(new ServiceCollection())
            .AddSingleton(settings)
            .AddSingleton(context.Service)
            .AddSingleton<IClock>(context.Clock)
            .AddSingleton<ICodexResetNewsClient>(news)
            .AddSingleton<CodexResetNewsService>()
            .AddSingleton<ICodexResetCreditAttemptRepository, TestAttemptRepository>()
            .AddSingleton<ICodexResetCreditDelay, TestCreditDelay>()
            .AddSingleton<CodexResetCreditAutoUseService>()
            .AddSingleton(CodexLocalIdentityServiceTests.CreateRenderingService([context.Account]))
            .AddSingleton(CodexProfileServiceTests.CreateRenderingService())
            .AddSingleton(desktopSwitch ?? CodexDesktopSwitchServiceTests.CreateRenderingService())
            .BuildServiceProvider();
    }
    private static async Task<string> RenderAsync<TComponent>(IDictionary<string, object?> parameters)
        where TComponent : IComponent {
        await using ServiceProvider serviceProvider = AddClaudeComponentRenderingServices(new ServiceCollection())
            .BuildServiceProvider();

        return await RenderAsync<TComponent>(serviceProvider, parameters);
    }

    private static async Task<string> RenderDialogAsync(IDictionary<string, object?> parameters) {
        await using ServiceProvider serviceProvider = AddClaudeComponentRenderingServices(new ServiceCollection())
            .BuildServiceProvider();
        await using HtmlRenderer renderer = new(serviceProvider, serviceProvider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () => {
            HtmlRootComponent themeProvider = await renderer.RenderComponentAsync<MudThemeProvider>();
            HtmlRootComponent popoverProvider = await renderer.RenderComponentAsync<MudPopoverProvider>();
            HtmlRootComponent dialogProvider = await renderer.RenderComponentAsync<MudDialogProvider>();
            HtmlRootComponent snackbarProvider = await renderer.RenderComponentAsync<MudSnackbarProvider>();
            DialogParameters dialogParameters = new();
            foreach ((string name, object? value) in parameters) {
                dialogParameters.Add(name, value);
            }

            await serviceProvider.GetRequiredService<IDialogService>()
                .ShowAsync<ClaudeSwitchConfirmationDialog>(null, dialogParameters);

            string html = string.Concat(
                themeProvider.ToHtmlString(),
                popoverProvider.ToHtmlString(),
                dialogProvider.ToHtmlString(),
                snackbarProvider.ToHtmlString()
            );
            return WebUtility.HtmlDecode(html);
        });
    }

    private static async Task<string> RenderAsync<TComponent>(
        IServiceProvider serviceProvider,
        IDictionary<string, object?>? parameters = null
    ) where TComponent : IComponent {
        await using HtmlRenderer renderer = new(serviceProvider, serviceProvider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () => {
            HtmlRootComponent themeProvider = await renderer.RenderComponentAsync<MudThemeProvider>();
            HtmlRootComponent popoverProvider = await renderer.RenderComponentAsync<MudPopoverProvider>();
            HtmlRootComponent dialogProvider = await renderer.RenderComponentAsync<MudDialogProvider>();
            HtmlRootComponent snackbarProvider = await renderer.RenderComponentAsync<MudSnackbarProvider>();
            HtmlRootComponent component = await renderer.RenderComponentAsync<TComponent>(
                parameters is null ? ParameterView.Empty : ParameterView.FromDictionary(parameters)
            );
            string html = string.Concat(
                themeProvider.ToHtmlString(),
                popoverProvider.ToHtmlString(),
                dialogProvider.ToHtmlString(),
                snackbarProvider.ToHtmlString(),
                component.ToHtmlString()
            );
            return WebUtility.HtmlDecode(html);
        });
    }

    private static ServiceProvider CreatePageServices(
        FakeClaudeAccountRepository accountRepository,
        bool throwOnCleanup = false,
        IClaudeDesktopProcessController.ProcessStatus processStatus = IClaudeDesktopProcessController.ProcessStatus.NotRunning,
        FakeQuotaRepository? quotaRepository = null,
        FakeInstallationLocator? installationLocator = null
    ) {
        TestClock clock = new();
        installationLocator ??= new();
        FakeDesktopProfileStore profileStore = new();
        FakeProcessController processController = new(processStatus);
        FakeOAuthClient oauthClient = new();
        FakeIdentityFingerprinter identityFingerprinter = new();
        ClaudeOperationGate operationGate = new(new ExclusiveOperationCoordinator());
        quotaRepository ??= new();
        ClaudeSessionHistorySyncService historyService = new(new FakeSessionHistoryStore(throwOnCleanup));

        ClaudeAccountService accounts = new(
            accountRepository,
            installationLocator,
            processController,
            profileStore,
            oauthClient,
            identityFingerprinter,
            operationGate,
            clock
        );
        ClaudeDesktopSwitchService switches = new(
            accountRepository,
            installationLocator,
            profileStore,
            processController,
            oauthClient,
            identityFingerprinter,
            historyService,
            operationGate,
            clock
        );
        ClaudeQuotaService quotas = new(
            accountRepository,
            profileStore,
            oauthClient,
            quotaRepository,
            operationGate,
            clock
        );

        return AddClaudeComponentRenderingServices(new ServiceCollection())
            .AddSingleton(oauthClient)
            .AddSingleton(accounts)
            .AddSingleton(switches)
            .AddSingleton(quotas)
            .AddSingleton(operationGate)
            .AddSingleton(new SettingsService(new FakeSettingsRepository(AppSettings.Defaults), new FakeAccountCatalogRepository()))
            .BuildServiceProvider();
    }

    private static IServiceCollection AddClaudeComponentRenderingServices(IServiceCollection services) {
        return services
            .AddLogging()
            .AddMudServices()
            .AddSingleton<RecordingComponentActivator>()
            .AddSingleton<IComponentActivator>(provider => provider.GetRequiredService<RecordingComponentActivator>())
            .AddSingleton<NavigationManager, StaticRenderNavigationManager>()
            .AddSingleton<IJSRuntime, StaticRenderJsRuntime>();
    }

    private static ClaudeAccountProfile CreateProfile(string alias, string accountUuid, string organizationUuid) {
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-26T00:00:00Z");
        return new(
            Guid.NewGuid(),
            alias,
            $"{alias.ToLowerInvariant()[..1]}***@example.test",
            accountUuid,
            organizationUuid,
            "fixture-installation",
            "fixture-data-root",
            "S-1-5-21-fixture",
            new string('a', 64),
            1,
            now,
            now,
            ClaudeAccountProfile.VerificationStatus.Verified
        );
    }

    private static ClaudeSwitchTransaction CreateCleanupPendingTransaction(ClaudeAccountProfile targetProfile) {
        return new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            targetProfile.ProfileId,
            1,
            targetProfile.ProfileRevision,
            ClaudeTestData.Installation.InstallationId,
            ClaudeSwitchTransaction.Stage.CommittedCleanupPending,
            DateTimeOffset.Parse("2026-10-26T00:00:00Z"),
            DateTimeOffset.Parse("2026-10-26T00:00:00Z"),
            null,
            ProvenanceSchemaVersion: 1,
            RootBinding: ClaudeTestData.Binding
        );
    }

    private sealed class FakeClaudeAccountRepository : IClaudeAccountRepository {
        public IReadOnlyList<ClaudeAccountProfile> Profiles { get; init; } = [];

        public ClaudeSwitchTransaction? Transaction { get; set; }

        public bool ThrowOnCatalogLoad { get; init; }
        public ClaudeAccountProfile.DesktopSnapshot? DesktopSnapshot { get; init; }

        public Task<IClaudeAccountRepository.Snapshot> LoadProfilesAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnCatalogLoad) {
                throw new IOException("Synthetic fixture repository failure.");
            }

            return Task.FromResult(new IClaudeAccountRepository.Snapshot(Profiles, new string('a', 64)));
        }

        public Task<ClaudeAccountProfile.DesktopSnapshot?> LoadDesktopSnapshotAsync(
            Guid profileId,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DesktopSnapshot);
        }

        public Task<IClaudeAccountRepository.SaveResult> SaveProfileAsync(
            ClaudeAccountProfile profile,
            ClaudeAccountProfile.DesktopSnapshot desktopSnapshot,
            string expectedSha256,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ClaudeAccountProfile> updatedProfiles = Profiles
                .Where(candidate => candidate.ProfileId != profile.ProfileId)
                .Append(profile)
                .ToArray();
            return Task.FromResult(new IClaudeAccountRepository.SaveResult(
                true,
                new IClaudeAccountRepository.Snapshot(updatedProfiles, new string('b', 64)),
                false
            ));
        }

        public Task<IClaudeAccountRepository.SaveResult> SaveCatalogMetadataAsync(
            IReadOnlyList<ClaudeAccountProfile> profiles,
            string expectedSha256,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IClaudeAccountRepository.SaveResult(
                true,
                new IClaudeAccountRepository.Snapshot(profiles, new string('b', 64)),
                false
            ));
        }

        public Task<ClaudeSwitchTransaction?> LoadSwitchTransactionAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Transaction);
        }

        public Task SaveSwitchTransactionAsync(ClaudeSwitchTransaction transaction, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            Transaction = transaction;
            return Task.CompletedTask;
        }
    }

    private sealed class StaticRenderJsRuntime : IJSRuntime {
        public List<string> ScrollElementIds { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) {
            return InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args
        ) {
            if (identifier == "accountNavigation.scrollTo") {
                ScrollElementIds.Add((string)args![0]!);
                return ValueTask.FromResult(default(TValue)!);
            }

            if (identifier == "mudThemeProvider.stopWatchingDarkMode") {
                return ValueTask.FromResult(default(TValue)!);
            }

            return ValueTask.FromException<TValue>(new InvalidOperationException(
                $"JavaScript interop is unavailable during static component rendering: {identifier}."
            ));
        }
    }

    private sealed class StaticRenderNavigationManager : NavigationManager {
        public StaticRenderNavigationManager() {
            Initialize("http://localhost/", "http://localhost/");
        }

        protected override void NavigateToCore(string uri, bool forceLoad) {
            throw new InvalidOperationException("Navigation is unavailable during static component rendering.");
        }

        protected override void NavigateToCore(string uri, NavigationOptions options) {
            throw new InvalidOperationException("Navigation is unavailable during static component rendering.");
        }
    }

    private sealed class FakeInstallationLocator : IClaudeDesktopInstallationLocator {
        public IClaudeDesktopInstallationLocator.Installation Installation { get; } = ClaudeTestData.Installation;

        public Task<IClaudeDesktopInstallationLocator.LocateResult> LocateAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IClaudeDesktopInstallationLocator.LocateResult(
                IClaudeDesktopInstallationLocator.LocateStatus.Found,
                Installation,
                null
            ));
        }

        public TaskCompletionSource<bool> ObserveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool>? ObserveRelease { get; set; }

        public async Task<IClaudeDesktopInstallationLocator.ObservationResult> ObserveRunningAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveStarted.TrySetResult(true);
            if (ObserveRelease is not null) {
                await ObserveRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new IClaudeDesktopInstallationLocator.ObservationResult(
                IClaudeDesktopInstallationLocator.LocateStatus.Found,
                Installation.Observation,
                null,
                Installation
            );
        }

        public Task<IClaudeDesktopInstallationLocator.LocateResult> ResolveObservationAfterStopAsync(
            ClaudeDesktopRootBinding.Observation observation,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IClaudeDesktopInstallationLocator.LocateResult(
                IClaudeDesktopInstallationLocator.LocateStatus.Found,
                Installation with { Observation = observation, Binding = null },
                null
            ));
        }

        public Task<IClaudeDesktopInstallationLocator.LocateResult> ResolveBoundAsync(
            ClaudeDesktopRootBinding binding,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IClaudeDesktopInstallationLocator.LocateResult(
                IClaudeDesktopInstallationLocator.LocateStatus.Found,
                Installation with { Binding = binding },
                null
            ));
        }
    }

    private sealed class FakeDesktopProfileStore : IClaudeDesktopProfileStore {
        private readonly Dictionary<Guid, ClaudeAccountProfile.DesktopSnapshot> prepared = [];
        private readonly ClaudeAccountProfile.DesktopSnapshot currentSnapshot = new(
            "{}",
            "{}",
            "account-alpha",
            [],
            null,
            "fixture-cookies",
            "S-1-5-21-fixture",
            "fixture-installation",
            "fixture-data-root",
            new string('a', 64),
            DateTimeOffset.Parse("2026-10-26T00:00:00Z"),
            null,
            null,
            1
        );
        private readonly IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> tokenCandidates = [
            new("fixture-token", "oauth:tokenCache")
        ];

        public Task<IClaudeDesktopProfileStore.ReadResult> ReadCurrentAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IClaudeDesktopProfileStore.ReadResult(
                currentSnapshot.LastKnownAccountUuid,
                tokenCandidates
            ));
        }

        public Task<IClaudeDesktopProfileStore.StableSnapshotLease> CaptureStableSnapshotAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IClaudeDesktopProfileStore.StableSnapshotLease>(
                new FakeSnapshotLease(currentSnapshot, tokenCandidates)
            );
        }

        public IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> GetTokenCandidates(
            ClaudeAccountProfile.DesktopSnapshot snapshot
        ) {
            return tokenCandidates;
        }

        public Task RestoreAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            ClaudeAccountProfile.DesktopSnapshot snapshot,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PrepareRestoreAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            ClaudeAccountProfile.DesktopSnapshot targetSnapshot,
            Guid transactionId,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            prepared.Add(transactionId, targetSnapshot);
            return Task.CompletedTask;
        }

        public Task ApplyRestoreAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            Guid transactionId,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            _ = prepared[transactionId];
            return Task.CompletedTask;
        }

        public Task RestoreTransactionAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            Guid transactionId,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            _ = prepared[transactionId];
            return Task.CompletedTask;
        }

        public Task CommitTransactionAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            Guid transactionId,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            // 與實際交易相同，沒有暫存證據時視為已清理完成。
            prepared.Remove(transactionId);
            return Task.CompletedTask;
        }

        private sealed class FakeSnapshotLease : IClaudeDesktopProfileStore.StableSnapshotLease {
            public FakeSnapshotLease(
                ClaudeAccountProfile.DesktopSnapshot snapshot,
                IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> candidates
            ) {
                Snapshot = snapshot;
                TokenCandidates = candidates;
            }

            public ClaudeAccountProfile.DesktopSnapshot Snapshot { get; }

            public IReadOnlyList<IClaudeDesktopProfileStore.TokenCandidate> TokenCandidates { get; }

            public Task<bool> ValidateAsync(CancellationToken cancellationToken) {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(true);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeProcessController : IClaudeDesktopProcessController {
        private IClaudeDesktopProcessController.ProcessStatus status;

        public FakeProcessController(IClaudeDesktopProcessController.ProcessStatus initialStatus) {
            status = initialStatus;
        }

        public Task<IClaudeDesktopProcessController.ProcessStatus> GetStatusAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(status);
        }

        public Task<bool> CloseGracefullyAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            TimeSpan timeout,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            status = IClaudeDesktopProcessController.ProcessStatus.NotRunning;
            return Task.FromResult(true);
        }

        public Task LaunchAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            status = IClaudeDesktopProcessController.ProcessStatus.Running;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOAuthClient : IClaudeOAuthClient {
        public int UsageCallCount { get; private set; }
        public Task<IClaudeOAuthClient.ProfileResult> GetProfileAsync(
            string bearerToken,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IClaudeOAuthClient.ProfileResult(
                IClaudeOAuthClient.Outcome.Success,
                "account-alpha",
                "organization-alpha",
                "alpha@example.test",
                null,
                null
            ));
        }

        public Task<IClaudeOAuthClient.UsageResult> GetUsageAsync(
            string bearerToken,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            UsageCallCount++;
            return Task.FromResult(new IClaudeOAuthClient.UsageResult(
                IClaudeOAuthClient.Outcome.Error,
                null,
                null,
                "UnusedByRenderTest"
            ));
        }
    }

    private sealed class FakeIdentityFingerprinter : IIdentityFingerprinter {
        public string CreateFingerprint(string? accountId, string? email, string authMode) {
            return "fixture-identity-fingerprint";
        }

        public string MaskEmail(string? email) {
            return "a***@example.test";
        }
    }

    private sealed class FakeQuotaRepository : IClaudeQuotaRepository {
        private readonly Dictionary<Guid, ClaudeQuotaSnapshot.View> views = [];
        public bool BlockSave { get; init; }
        public List<Guid> SavedProfileIds { get; } = [];
        public TaskCompletionSource<bool> SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SaveRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SeedView(ClaudeQuotaSnapshot.View view) => views[view.ProfileId] = view;

        public void Seed(Guid profileId, DateTimeOffset retrievedAt) {
            views[profileId] = new(
                profileId,
                new ClaudeQuotaSnapshot(profileId, retrievedAt, []),
                ClaudeQuotaSnapshot.RefreshStatus.Fresh,
                retrievedAt,
                null
            );
        }

        public Task<IClaudeQuotaRepository.Snapshot?> LoadAsync(Guid profileId, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            IClaudeQuotaRepository.Snapshot? snapshot = views.TryGetValue(profileId, out ClaudeQuotaSnapshot.View? view)
                ? new IClaudeQuotaRepository.Snapshot(view, new string('c', 64))
                : null;
            return Task.FromResult(snapshot);
        }

        public async Task<IClaudeQuotaRepository.SaveResult> SaveAsync(
            ClaudeQuotaSnapshot.View view,
            string? expectedSha256,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            SavedProfileIds.Add(view.ProfileId);
            SaveStarted.TrySetResult(true);
            if (BlockSave) {
                await SaveRelease.Task.WaitAsync(cancellationToken);
            }

            views[view.ProfileId] = view;
            return new IClaudeQuotaRepository.SaveResult(
                true,
                new IClaudeQuotaRepository.Snapshot(view, new string('c', 64)),
                false
            );
        }
    }

    private sealed class RecordingComponentActivator(IServiceProvider services) : IComponentActivator {
        private IComponent? page;

        public IComponent CreateInstance(Type componentType) {
            IComponent component = (IComponent)ActivatorUtilities.CreateInstance(services, componentType);
            if (component is ClaudePage or CodexPage) {
                page = component;
            }

            return component;
        }

        public IHandleAfterRender GetPage<TComponent>() where TComponent : IComponent {
            return page is TComponent component && component is IHandleAfterRender afterRender
                ? afterRender
                : throw new InvalidOperationException("The requested page was not rendered.");
        }
    }

    private sealed class FakeSessionHistoryStore : IClaudeSessionHistoryStore {
        private readonly bool throwOnCleanup;

        public FakeSessionHistoryStore(bool throwOnCleanup = false) {
            this.throwOnCleanup = throwOnCleanup;
        }

        public Task<IClaudeSessionHistoryStore.ValidationResult> ValidateAccountAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            string accountUuid,
            string organizationUuid,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IClaudeSessionHistoryStore.ValidationResult(
                IClaudeSessionHistoryStore.ValidationStatus.Valid,
                0,
                null
            ));
        }

        public Task<IClaudeSessionHistoryStore.PreparationResult> PrepareReplacementAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            string sourceAccountUuid,
            string sourceOrganizationUuid,
            string targetAccountUuid,
            string targetOrganizationUuid,
            Guid transactionId,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IClaudeSessionHistoryStore.PreparationResult(
                IClaudeSessionHistoryStore.ValidationStatus.Valid,
                0,
                0,
                null
            ));
        }

        public Task ApplyReplacementAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            string targetAccountUuid,
            string targetOrganizationUuid,
            Guid transactionId,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RestoreTargetAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            string targetAccountUuid,
            string targetOrganizationUuid,
            Guid transactionId,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task CommitAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            Guid transactionId,
            ClaudeSessionHistoryCleanupIntent cleanupIntent,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            if (throwOnCleanup) {
                throw new IOException("Synthetic history cleanup failure.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestAttemptRepository : ICodexResetCreditAttemptRepository {
        public Task<IReadOnlyList<CodexResetCreditAttempt>> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CodexResetCreditAttempt>>([]);

        public Task<bool> TryBeginAsync(CodexResetCreditAttempt attempt, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task CompleteAsync(CodexResetCreditAttempt attempt, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class TestCreditDelay : ICodexResetCreditDelay {
        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    }

    private sealed class TestResetNewsClient : ICodexResetNewsClient {
        public ICodexResetNewsClient.Result Response { get; init; } = new(null, false, null);

        public Task<ICodexResetNewsClient.Result> GetAsync(CancellationToken cancellationToken)
            => Task.FromResult(Response);
    }

    private sealed class TestClock : IClock {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-10-26T00:00:00Z");

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) {
            return Task.Delay(delay, cancellationToken);
        }
    }
}
