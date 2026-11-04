using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
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
using CloudyWing.AiAccountManager.Application.Settings;
using CloudyWing.AiAccountManager.Application.Recommendations;
using CloudyWing.AiAccountManager.Application.Notifications;
using CloudyWing.AiAccountManager.Desktop;
using CloudyWing.AiAccountManager.Desktop.Components.Dashboard;
using CloudyWing.AiAccountManager.Desktop.Components.Pages;
using CloudyWing.AiAccountManager.Desktop.Components.Shared;
using CloudyWing.AiAccountManager.Tests.Claude;
using CloudyWing.AiAccountManager.Tests.Codex;

namespace CloudyWing.AiAccountManager.Tests.Desktop;

[TestFixture]
public sealed class DashboardComponentRenderTests {
    [Test]
    public async Task CodexNotification_CandidateReadFails_NotifiesCurrentWithoutRecommendation() {
        await using DashboardReaderFixture fixture = new(useCurrentTime: true);
        CodexAccountProfile candidate = CreateCodexProfile(Guid.NewGuid(), "codex-other", fixture.CodexFingerprinter);
        fixture.CodexCatalog.SetAccounts([fixture.CodexProfile, candidate]);
        fixture.CodexQuotaRepository.Seed(CreateCodexQuotaRecord(fixture.CodexProfile, fixture.Now, 15, 80,
            fixture.Now.AddHours(1), fixture.Now.AddDays(1)));
        fixture.CodexQuotaRepository.FailAccountId = candidate.AccountId;
        NotificationSnapshot? snapshot = await fixture.Reader.ReadCodexNotificationAsync(CancellationToken.None);
        RecordingNotificationSink sink = new();

        Assert.That(snapshot, Is.Not.Null);
        await new LowQuotaNotificationEvaluator(sink, new MemoryNotificationStore()).EvaluateAsync([snapshot!], true, 20, CancellationToken.None);

        Assert.That(snapshot!.AccountId, Is.EqualTo(fixture.CodexProfile.AccountId));
        Assert.That(snapshot.Recommendation, Is.Null);
        Assert.That(sink.Messages, Has.Count.EqualTo(1));
        Assert.That(sink.Messages[0].Message, Does.Not.Contain("建議切換"));
    }

    [Test]
    public async Task ClaudeNotification_CandidateReadFails_NotifiesCurrentWithoutRecommendation() {
        await using DashboardReaderFixture fixture = new(useCurrentTime: true);
        ClaudeAccountProfile candidate = ClaudeTestData.CreateProfile(Guid.NewGuid(), "claude-other", "org-other", alias: "Other");
        fixture.ClaudeAccountRepository.Profiles.Add(candidate);
        fixture.ClaudeQuotaRepository.Seed(new(fixture.ClaudeProfile.ProfileId,
            new(fixture.ClaudeProfile.ProfileId, fixture.Now, [new("session", 300, 85m, fixture.Now.AddHours(1)),
                new("weekly_all", 10080, 20m, fixture.Now.AddDays(1))]),
            ClaudeQuotaSnapshot.RefreshStatus.Fresh, null, null));
        fixture.ClaudeQuotaRepository.FailProfileId = candidate.ProfileId;
        NotificationSnapshot? snapshot = await fixture.Reader.ReadClaudeNotificationAsync(CancellationToken.None);
        RecordingNotificationSink sink = new();

        Assert.That(snapshot, Is.Not.Null);
        await new LowQuotaNotificationEvaluator(sink, new MemoryNotificationStore()).EvaluateAsync([snapshot!], true, 20, CancellationToken.None);

        Assert.That(snapshot!.AccountId, Is.EqualTo(fixture.ClaudeProfile.ProfileId));
        Assert.That(snapshot.Recommendation, Is.Null);
        Assert.That(sink.Messages, Has.Count.EqualTo(1));
        Assert.That(sink.Messages[0].Message, Does.Not.Contain("建議切換"));
    }

    [Test]
    public async Task CodexNotification_OnlyOtherAccountIsLow_DoesNotNotify() {
        await using DashboardReaderFixture fixture = new(useCurrentTime: true);
        CodexAccountProfile other = CreateCodexProfile(Guid.NewGuid(), "codex-other", fixture.CodexFingerprinter);
        fixture.CodexCatalog.SetAccounts([fixture.CodexProfile, other]);
        fixture.CodexQuotaRepository.Seed(CreateCodexQuotaRecord(fixture.CodexProfile, fixture.Now, 80, 80,
            fixture.Now.AddHours(1), fixture.Now.AddDays(1)));
        fixture.CodexQuotaRepository.Seed(CreateCodexQuotaRecord(other, fixture.Now, 1, 1,
            fixture.Now.AddHours(1), fixture.Now.AddDays(1)));
        NotificationSnapshot? snapshot = await fixture.Reader.ReadCodexNotificationAsync(CancellationToken.None);
        RecordingNotificationSink sink = new();

        Assert.That(snapshot, Is.Not.Null);
        await new LowQuotaNotificationEvaluator(sink, new MemoryNotificationStore()).EvaluateAsync([snapshot!], true, 20, CancellationToken.None);

        Assert.That(snapshot!.AccountId, Is.EqualTo(fixture.CodexProfile.AccountId));
        Assert.That(sink.Messages, Is.Empty);
    }
    [TestCase("codex")]
    [TestCase("claude")]
    public async Task Dashboard_Recommendation_RendersTargetAccountLinkWithoutSwitching(string provider) {
        Guid accountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        RecommendationAccount candidate = new(accountId, "備用帳號", 80, 75, null, true);
        SyntheticDashboardReader reader = new() {
            CodexHandler = _ => Task.FromResult(CreateProvider(DashboardOverviewSnapshot.ProviderKind.Codex,
                DashboardOverviewSnapshot.IdentityStatus.Matched, DashboardOverviewSnapshot.QuotaStatus.Fresh,
                "current", "test")),
            ClaudeHandler = _ => Task.FromResult(CreateProvider(DashboardOverviewSnapshot.ProviderKind.Claude,
                DashboardOverviewSnapshot.IdentityStatus.Matched, DashboardOverviewSnapshot.QuotaStatus.Fresh,
                "current", "test")),
            CodexRecommendation = provider == "codex" ? candidate : null,
            ClaudeRecommendation = provider == "claude" ? candidate : null
        };

        string html = await RenderDashboardAsync(reader);

        Assert.That(html, Does.Contain("建議切換：備用帳號"));
        Assert.That(html, Does.Contain($"href=\"/{provider}?account={accountId:D}\""));
        Assert.That(AccountNavigation.ElementId(provider, accountId), Is.EqualTo($"{provider}-account-{accountId:N}"));
    }
    [Test]
    public async Task DashboardOverviewReader_CodexIdentityMatchesOneSavedAccount_ReadsOnlyItsSavedQuota() {
        await using DashboardReaderFixture fixture = new();
        CodexAccountProfile otherProfile = CreateCodexProfile(
            Guid.NewGuid(),
            "codex-account-other",
            fixture.CodexFingerprinter
        );
        fixture.CodexCatalog.SetAccounts([fixture.CodexProfile, otherProfile]);
        fixture.CodexQuotaRepository.Seed(CreateCodexQuotaRecord(
            otherProfile,
            fixture.Now,
            11,
            12,
            fixture.Now.AddHours(1),
            fixture.Now.AddDays(2)
        ));

        DashboardOverviewSnapshot.Provider provider = await fixture.Reader.ReadCodexAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(provider.IdentityStatus, Is.EqualTo(DashboardOverviewSnapshot.IdentityStatus.Matched));
            Assert.That(provider.MaskedIdentity, Is.EqualTo("p•••@example.test"));
            Assert.That(provider.QuotaStatus, Is.EqualTo(DashboardOverviewSnapshot.QuotaStatus.Fresh));
            Assert.That(fixture.CodexQuotaRepository.LoadedAccountIds, Is.EqualTo(new[] { fixture.CodexProfile.AccountId }));
            Assert.That(provider.Windows.Select(window => window.Label), Is.EqualTo(new[] { "5 小時", "每週" }));
            Assert.That(provider.Windows.Select(window => window.RemainingPercent), Is.EqualTo(new decimal?[] { 68m, 41m }));
            Assert.That(provider.Windows[0].ResetsAtUtc, Is.EqualTo(fixture.Now.AddHours(3)));
            Assert.That(provider.Windows[1].ResetsAtUtc, Is.EqualTo(fixture.Now.AddDays(5)));
            Assert.That(provider.RetrievedAtUtc, Is.EqualTo(fixture.Now.AddMinutes(-2)));
            Assert.That(fixture.CodexSessionFactory.CreateCallCount, Is.Zero);
            Assert.That(fixture.CodexQuotaRepository.SaveCallCount, Is.Zero);
        }
    }

    [Test]
    public async Task DashboardOverviewReader_UnmatchedAndUnavailableIdentities_DoNotLoadAnySavedQuota() {
        await using DashboardReaderFixture fixture = new();
        CodexAccountProfile otherCodexProfile = CreateCodexProfile(
            Guid.NewGuid(),
            "codex-account-other",
            fixture.CodexFingerprinter
        );
        fixture.CodexCatalog.SetAccounts([fixture.CodexProfile, otherCodexProfile]);
        fixture.CodexAuthReader.Result = new(
            CodexLocalAuthReadStatus.Success,
            new CodexLocalAuthIdentity("codex-account-unmatched", "private-codex@example.test")
        );
        fixture.CodexQuotaRepository.Seed(CreateCodexQuotaRecord(
            otherCodexProfile,
            fixture.Now,
            1,
            2,
            fixture.Now.AddHours(1),
            fixture.Now.AddDays(2)
        ));
        fixture.ClaudeAccountRepository.Profiles.Add(ClaudeTestData.CreateProfile(
            Guid.NewGuid(),
            "claude-account-other",
            "claude-organization-other",
            alias: "Other saved Claude profile"
        ));
        fixture.ClaudeOAuthClient.ProfileResults[DashboardReaderFixture.ClaudeToken] =
            ClaudeTestData.VerifiedProfile(fixture.ClaudeProfile.AccountUuid, "different-organization");

        DashboardOverviewSnapshot.Provider codexUnmatched = await fixture.Reader.ReadCodexAsync(CancellationToken.None);
        DashboardOverviewSnapshot.Provider claudeUnmatched = await fixture.Reader.ReadClaudeAsync(CancellationToken.None);

        fixture.CodexAuthReader.Result = new(CodexLocalAuthReadStatus.Unavailable, null);
        fixture.ClaudeOAuthClient.ThrowOnProfile.Add(DashboardReaderFixture.ClaudeToken);
        DashboardOverviewSnapshot.Provider codexUnavailable = await fixture.Reader.ReadCodexAsync(CancellationToken.None);
        DashboardOverviewSnapshot.Provider claudeUnavailable = await fixture.Reader.ReadClaudeAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(codexUnmatched.IdentityStatus, Is.EqualTo(DashboardOverviewSnapshot.IdentityStatus.Unmatched));
            Assert.That(claudeUnmatched.IdentityStatus, Is.EqualTo(DashboardOverviewSnapshot.IdentityStatus.Unmatched));
            Assert.That(codexUnavailable.IdentityStatus, Is.EqualTo(DashboardOverviewSnapshot.IdentityStatus.Error));
            Assert.That(claudeUnavailable.IdentityStatus, Is.EqualTo(DashboardOverviewSnapshot.IdentityStatus.Unknown));
            Assert.That(fixture.CodexQuotaRepository.LoadedAccountIds, Is.Empty);
            Assert.That(fixture.ClaudeQuotaRepository.LoadedProfileIds, Is.Empty);
        }
    }

    [Test]
    public async Task DashboardOverviewReader_MatchedClaudeIdentityMapsEveryWindowAndUsesReadOnlyPorts() {
        await using DashboardReaderFixture fixture = new();
        ClaudeAccountProfile otherProfile = ClaudeTestData.CreateProfile(
            Guid.NewGuid(),
            "claude-account-other",
            "claude-organization-other",
            alias: "Other saved profile"
        );
        fixture.ClaudeAccountRepository.Profiles.Add(otherProfile);
        fixture.ClaudeQuotaRepository.Seed(new(
            otherProfile.ProfileId,
            null,
            ClaudeQuotaSnapshot.RefreshStatus.Unknown,
            null,
            "No synthetic quota"
        ));

        DashboardOverviewSnapshot.Provider provider = await fixture.Reader.ReadClaudeAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(provider.IdentityStatus, Is.EqualTo(DashboardOverviewSnapshot.IdentityStatus.Matched));
            Assert.That(provider.MaskedIdentity, Is.EqualTo("Claude Primary"));
            Assert.That(provider.QuotaStatus, Is.EqualTo(DashboardOverviewSnapshot.QuotaStatus.Fresh));
            Assert.That(fixture.ClaudeQuotaRepository.LoadedProfileIds, Is.EqualTo(new[] { fixture.ClaudeProfile.ProfileId }));
            Assert.That(provider.Windows.Select(window => window.Key), Is.EqualTo(new[] {
                "session",
                "weekly_all",
                "weekly_opus",
                "weekly_sonnet",
                "weekly_scoped:Long Model"
            }));
            Assert.That(provider.Windows.Select(window => window.Label), Is.EqualTo(new[] {
                "工作階段",
                "每週",
                "每週",
                "每週",
                "每週"
            }));
            Assert.That(provider.Windows.Select(window => window.Scope), Is.EqualTo(new string?[] {
                null,
                "所有模型",
                "Opus",
                "Sonnet",
                "Long Model"
            }));
            Assert.That(provider.Windows.Select(window => window.RemainingPercent), Is.EqualTo(new decimal?[] {
                68m,
                68m,
                68m,
                68m,
                68m
            }));
            Assert.That(provider.Windows.Select(window => window.ResetsAtUtc), Is.EqualTo(new DateTimeOffset?[] {
                fixture.Now.AddHours(2),
                fixture.Now.AddDays(1),
                fixture.Now.AddDays(2),
                fixture.Now.AddDays(3),
                fixture.Now.AddDays(4)
            }));
            Assert.That(provider.RetrievedAtUtc, Is.EqualTo(fixture.Now.AddMinutes(-3)));
            Assert.That(fixture.ClaudeOAuthClient.ProfileTokens, Is.EqualTo(new[] { DashboardReaderFixture.ClaudeToken }));
            Assert.That(fixture.ClaudeOAuthClient.UsageTokens, Is.Empty);
            Assert.That(fixture.ClaudeAccountRepository.SaveProfileCallCount, Is.Zero);
            Assert.That(fixture.ClaudeAccountRepository.SaveTransactionCallCount, Is.Zero);
            Assert.That(fixture.ClaudeQuotaRepository.SaveCallCount, Is.Zero);
            Assert.That(fixture.ClaudeProfileStore.Operations, Does.Not.Contain("capture-stable-snapshot"));
            Assert.That(fixture.ClaudeProfileStore.Operations, Does.Not.Contain("restore"));
            Assert.That(fixture.CodexSessionFactory.CreateCallCount, Is.Zero);
            Assert.That(fixture.CodexQuotaRepository.SaveCallCount, Is.Zero);
        }
    }

    [Test]
    public async Task DashboardPage_RendersEachProviderDataAndKeepsManagementLinksReachable() {
        SyntheticDashboardReader reader = new() {
            CodexHandler = _ => Task.FromResult(CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Codex,
                DashboardOverviewSnapshot.IdentityStatus.Matched,
                DashboardOverviewSnapshot.QuotaStatus.Fresh,
                "codex-mask@example.test",
                "Synthetic Codex source",
                checkedAtUtc: new(2026, 10, 26, 11, 0, 0, TimeSpan.Zero),
                retrievedAtUtc: new(2026, 10, 26, 11, 5, 0, TimeSpan.Zero),
                windows: [new("five-hour", "5 小時", null, 68m, new(2026, 10, 26, 14, 0, 0, TimeSpan.Zero))]
            )),
            ClaudeHandler = _ => Task.FromResult(CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Claude,
                DashboardOverviewSnapshot.IdentityStatus.Matched,
                DashboardOverviewSnapshot.QuotaStatus.Stale,
                "Claude Primary",
                "Synthetic Claude source",
                quotaMessage: "保留最近一次成功快照。",
                checkedAtUtc: new(2026, 10, 26, 11, 1, 0, TimeSpan.Zero),
                retrievedAtUtc: new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero),
                windows: [new("weekly_opus", "每週", "Opus", 42m, new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero))],
                isStale: true
            ))
        };

        string html = await RenderDashboardAsync(reader);

        using (Assert.EnterMultipleScope()) {
            Assert.That(html, Does.Contain("account-avatar-icon-only"));
            Assert.That(html, Does.Contain(Icons.Material.Filled.Flare));
            Assert.That(html, Does.Contain(Icons.Material.Filled.Hive));
            Assert.That(html, Does.Not.Match("account-avatar-icon-only[^>]*>\\s*C"));
            Assert.That(html, Does.Contain("codex-mask@example.test"));
            Assert.That(html, Does.Contain("Claude Primary"));
            Assert.That(html, Does.Contain("5 小時"));
            Assert.That(html, Does.Contain("Opus"));
            Assert.That(html, Does.Contain("68%"));
            Assert.That(html, Does.Contain("42%"));
            Assert.That(html, Does.Contain("Synthetic Codex source"));
            Assert.That(html, Does.Contain("Synthetic Claude source"));
            Assert.That(html, Does.Contain("保留最近一次成功快照。"));
            Assert.That(html, Does.Contain("資料已過期，顯示最近一次成功快照"));
            Assert.That(html, Does.Contain("href=\"/codex\""));
            Assert.That(html, Does.Contain("href=\"/claude\""));
            Assert.That(html.IndexOf("data-provider=\"claude\"", StringComparison.Ordinal),
                Is.LessThan(html.IndexOf("data-provider=\"codex\"", StringComparison.Ordinal)));
            Assert.That(html, Does.Contain("診斷明細"));
            Assert.That(html, Does.Contain("class=\"dashboard-diagnostics-scroll\""));
            Assert.That(html, Does.Not.Contain("unmasked@example.test"));
            Assert.That(html, Does.Not.Contain("synthetic-claude-token"));
        }
    }

    [TestCase("claude", Icons.Material.Filled.Flare)]
    [TestCase("codex", Icons.Material.Filled.Hive)]
    public async Task AccountAvatar_DefaultMode_RendersInitialAndBrandBadge(string provider, string icon) {
        await using ServiceProvider services = CreateRenderingServices(new SyntheticDashboardReader());
        Dictionary<string, object?> parameters = new() {
            [nameof(AccountAvatar.DisplayName)] = "Alice",
            [nameof(AccountAvatar.Provider)] = provider
        };

        string html = await RenderComponentAsync<AccountAvatar>(services, parameters);

        Assert.That(html, Does.Contain(">A"));
        Assert.That(html, Does.Contain("account-avatar-brand"));
        Assert.That(html, Does.Contain(icon));
    }

    [Test]
    public async Task DashboardPage_UnknownQuotaDoesNotRenderAZeroPercentRing() {
        SyntheticDashboardReader reader = new() {
            CodexHandler = _ => Task.FromResult(CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Codex,
                DashboardOverviewSnapshot.IdentityStatus.Unmatched,
                DashboardOverviewSnapshot.QuotaStatus.Unknown,
                "codex-mask@example.test",
                "Codex source",
                identityMessage: "目前身分未匹配。",
                windows: [new("codex-unknown", "5 小時", null, null, null)]
            )),
            ClaudeHandler = _ => Task.FromResult(CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Claude,
                DashboardOverviewSnapshot.IdentityStatus.Unknown,
                DashboardOverviewSnapshot.QuotaStatus.Unknown,
                null,
                "Claude source"
            ))
        };

        string html = await RenderDashboardAsync(reader);

        using (Assert.EnterMultipleScope()) {
            Assert.That(html, Does.Contain("額度未知"));
            Assert.That(html, Does.Contain("目前身分未匹配。"));
            Assert.That(html, Does.Not.Contain("%"));
            Assert.That(html, Does.Not.Contain("0%"));
        }
    }

    [Test]
    public async Task DashboardPage_ZeroAndHundredPercentAreValidQuotaValues() {
        SyntheticDashboardReader reader = new() {
            CodexHandler = _ => Task.FromResult(CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Codex,
                DashboardOverviewSnapshot.IdentityStatus.Matched,
                DashboardOverviewSnapshot.QuotaStatus.Fresh,
                "codex-mask@example.test",
                "Codex source",
                windows: [new("empty", "5 小時", null, 0m, null)]
            )),
            ClaudeHandler = _ => Task.FromResult(CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Claude,
                DashboardOverviewSnapshot.IdentityStatus.Matched,
                DashboardOverviewSnapshot.QuotaStatus.Fresh,
                "Claude Primary",
                "Claude source",
                windows: [new("full", "每週", "所有模型", 100m, null)]
            ))
        };

        string html = await RenderDashboardAsync(reader);

        using (Assert.EnterMultipleScope()) {
            Assert.That(html, Does.Contain("mud-progress-circular-percentage\">0%"));
            Assert.That(html, Does.Contain("mud-progress-circular-percentage\">100%"));
            Assert.That(html, Does.Contain("0% 剩餘"));
            Assert.That(html, Does.Contain("100% 剩餘"));
        }
    }

    [Test]
    public async Task DashboardPage_WhenClaudeReaderThrows_KeepsCodexAndHidesExceptionDetails() {
        SyntheticDashboardReader reader = new() {
            CodexHandler = _ => Task.FromResult(CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Codex,
                DashboardOverviewSnapshot.IdentityStatus.Matched,
                DashboardOverviewSnapshot.QuotaStatus.Fresh,
                "codex-mask@example.test",
                "Codex source",
                windows: [new("weekly", "每週", null, 55m, null)]
            )),
            ClaudeHandler = _ => Task.FromException<DashboardOverviewSnapshot.Provider>(
                new InvalidOperationException("synthetic-claude-token private@example.test")
            )
        };

        string html = await RenderDashboardAsync(reader);

        using (Assert.EnterMultipleScope()) {
            Assert.That(html, Does.Contain("codex-mask@example.test"));
            Assert.That(html, Does.Contain("55%"));
            Assert.That(html, Does.Contain("身分讀取失敗"));
            Assert.That(html, Does.Contain("無法讀取 Claude Desktop 身分或已保存額度。"));
            Assert.That(html, Does.Not.Contain("synthetic-claude-token"));
            Assert.That(html, Does.Not.Contain("private@example.test"));
        }
    }

    [Test]
    public async Task DashboardPage_PendingProviderReads_RendersLoadingUntilBothComplete() {
        TaskCompletionSource<DashboardOverviewSnapshot.Provider> codexCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource<DashboardOverviewSnapshot.Provider> claudeCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        SyntheticDashboardReader reader = new() {
            CodexHandler = _ => codexCompletion.Task,
            ClaudeHandler = _ => claudeCompletion.Task
        };
        await using ServiceProvider services = CreateRenderingServices(reader);
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        HtmlRootComponent root = await renderer.Dispatcher.InvokeAsync(
            () => renderer.BeginRenderingComponent<DashboardPage>()
        );
        string loadingHtml = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));

        codexCompletion.SetResult(CreateProvider(
            DashboardOverviewSnapshot.ProviderKind.Codex,
            DashboardOverviewSnapshot.IdentityStatus.Matched,
            DashboardOverviewSnapshot.QuotaStatus.Fresh,
            "codex-loading-test@example.test",
            "Codex source"
        ));
        string partialHtml = WebUtility.HtmlDecode(await WaitForHtmlAsync(renderer, root, "codex-loading-test@example.test"));

        claudeCompletion.SetResult(CreateProvider(
            DashboardOverviewSnapshot.ProviderKind.Claude,
            DashboardOverviewSnapshot.IdentityStatus.Matched,
            DashboardOverviewSnapshot.QuotaStatus.Fresh,
            "Claude loading test",
            "Claude source"
        ));
        await root.QuiescenceTask;
        string completedHtml = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));

        using (Assert.EnterMultipleScope()) {
            Assert.That(loadingHtml, Does.Contain("正在分別讀取 Claude 與 Codex 額度。"));
            Assert.That(loadingHtml, Does.Contain("dashboard-skeleton-card"));
            Assert.That(partialHtml, Does.Contain("codex-loading-test@example.test"));
            Assert.That(partialHtml, Does.Contain("dashboard-skeleton-card"));
            Assert.That(partialHtml, Does.Not.Contain("Claude loading test"));
            Assert.That(completedHtml, Does.Contain("codex-loading-test@example.test"));
            Assert.That(completedHtml, Does.Contain("Claude loading test"));
            Assert.That(completedHtml, Does.Not.Contain("正在分別讀取 Claude 與 Codex 額度。"));
        }
    }

    [Test]
    public async Task DashboardPage_QuotaStateChanged_ReloadsUntilDisposed() {
        int codexReads = 0;
        int claudeReads = 0;
        SyntheticDashboardReader reader = new() {
            CodexHandler = _ => {
                codexReads++;
                return Task.FromResult(CreateProvider(
                    DashboardOverviewSnapshot.ProviderKind.Codex,
                    DashboardOverviewSnapshot.IdentityStatus.Unknown,
                    DashboardOverviewSnapshot.QuotaStatus.Unknown,
                    null,
                    "Codex source"
                ));
            },
            ClaudeHandler = _ => {
                claudeReads++;
                return Task.FromResult(CreateProvider(
                    DashboardOverviewSnapshot.ProviderKind.Claude,
                    DashboardOverviewSnapshot.IdentityStatus.Unknown,
                    DashboardOverviewSnapshot.QuotaStatus.Unknown,
                    null,
                    "Claude source"
                ));
            }
        };
        await using ServiceProvider services = CreateRenderingServices(reader);
        DashboardReaderFixture fixture = services.GetRequiredService<DashboardReaderFixture>();
        HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<DashboardPage>());
        int initialCodexReads = codexReads;
        int initialClaudeReads = claudeReads;
        reader.ClaudeHandler = async cancellationToken => {
            claudeReads++;
            if (claudeReads <= initialClaudeReads + 4) {
                await fixture.ClaudeQuota.GetViewAsync(fixture.ClaudeProfile.ProfileId, cancellationToken);
            }

            return CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Claude,
                DashboardOverviewSnapshot.IdentityStatus.Unknown,
                DashboardOverviewSnapshot.QuotaStatus.Unknown,
                null,
                "Claude source"
            );
        };

        await renderer.Dispatcher.InvokeAsync(() => fixture.ClaudeQuota.GetViewAsync(
            fixture.ClaudeProfile.ProfileId, CancellationToken.None
        ));
        for (int i = 0; i < 10; i++) {
            await Task.Delay(10);
            await renderer.Dispatcher.InvokeAsync(() => { });
        }
        int afterEventCodexReads = codexReads;
        int afterEventClaudeReads = claudeReads;
        await renderer.DisposeAsync();
        await fixture.ClaudeQuota.GetViewAsync(fixture.ClaudeProfile.ProfileId, CancellationToken.None);

        using (Assert.EnterMultipleScope()) {
            Assert.That(afterEventCodexReads, Is.EqualTo(initialCodexReads + 1));
            Assert.That(afterEventClaudeReads, Is.EqualTo(initialClaudeReads + 1));
            Assert.That(codexReads, Is.EqualTo(afterEventCodexReads));
            Assert.That(claudeReads, Is.EqualTo(afterEventClaudeReads));
        }
    }

    [Test]
    public async Task DashboardPage_DisposeCancelsPendingReadsWithoutPublishingAnError() {
        SyntheticDashboardReader reader = new() {
            CodexHandler = WaitForCancellationAsync,
            ClaudeHandler = WaitForCancellationAsync
        };
        await using ServiceProvider services = CreateRenderingServices(reader);
        HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        HtmlRootComponent root = await renderer.Dispatcher.InvokeAsync(
            () => renderer.BeginRenderingComponent<DashboardPage>()
        );
        string loadingHtml = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        CancellationToken codexToken = reader.CodexToken;
        CancellationToken claudeToken = reader.ClaudeToken;

        await renderer.DisposeAsync();
        OperationCanceledException? cancellation = await Assert.CatchAsync<OperationCanceledException>(
            async () => await Task.WhenAll(reader.CodexTask!, reader.ClaudeTask!)
        );

        using (Assert.EnterMultipleScope()) {
            Assert.That(loadingHtml, Does.Contain("正在分別讀取 Claude 與 Codex 額度。"));
            Assert.That(cancellation, Is.Not.Null);
            Assert.That(codexToken.IsCancellationRequested, Is.True);
            Assert.That(claudeToken.IsCancellationRequested, Is.True);
            Assert.That(reader.CodexTask!.IsCanceled, Is.True);
            Assert.That(reader.ClaudeTask!.IsCanceled, Is.True);
        }
    }

    [TestCase("/", nameof(DashboardPage))]
    [TestCase("/claude", nameof(ClaudePage))]
    [TestCase("/codex", nameof(CodexPage))]
    [TestCase("/settings", nameof(SettingsPage))]
    public async Task Router_ResolvesManagementPathsToExpectedPageTypeWithoutRenderingPages(
        string location,
        string expectedPageType
    ) {
        string html = await RenderRouterAsync(location);

        Assert.That(html, Is.EqualTo(expectedPageType));
    }

    [Test]
    public async Task DashboardPage_LongIdentityScopeAndDiagnosticContentRemainsComplete() {
        string longIdentity = "long-identity-" + new string('x', 1400);
        string longScope = "model-scope-" + new string('y', 900);
        string longDiagnostic = "diagnostic-" + new string('z', 2400);
        SyntheticDashboardReader reader = new() {
            CodexHandler = _ => Task.FromResult(CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Codex,
                DashboardOverviewSnapshot.IdentityStatus.Matched,
                DashboardOverviewSnapshot.QuotaStatus.Fresh,
                longIdentity,
                "Codex source",
                windows: [new("codex-window", "每週", longScope, 25m, null)],
                identityMessage: longDiagnostic
            )),
            ClaudeHandler = _ => Task.FromResult(CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Claude,
                DashboardOverviewSnapshot.IdentityStatus.Unknown,
                DashboardOverviewSnapshot.QuotaStatus.Unknown,
                null,
                "Claude source"
            ))
        };

        string html = await RenderDashboardAsync(reader);

        using (Assert.EnterMultipleScope()) {
            Assert.That(html, Does.Contain(longIdentity));
            Assert.That(html, Does.Contain(longScope));
            Assert.That(html, Does.Contain(longDiagnostic));
            Assert.That(html, Does.Contain("dashboard-service-identity"));
            Assert.That(html, Does.Contain("dashboard-diagnostics-scroll"));
        }
    }

    private static DashboardOverviewSnapshot.Provider CreateProvider(
        DashboardOverviewSnapshot.ProviderKind kind,
        DashboardOverviewSnapshot.IdentityStatus identityStatus,
        DashboardOverviewSnapshot.QuotaStatus quotaStatus,
        string? maskedIdentity,
        string sourceLabel,
        string? identityMessage = null,
        string? quotaMessage = null,
        DateTimeOffset? checkedAtUtc = null,
        DateTimeOffset? retrievedAtUtc = null,
        IReadOnlyList<DashboardOverviewSnapshot.QuotaWindow>? windows = null,
        bool isStale = false
    ) {
        return new(
            kind,
            identityStatus,
            quotaStatus,
            maskedIdentity,
            sourceLabel,
            identityMessage,
            quotaMessage,
            checkedAtUtc,
            retrievedAtUtc,
            windows ?? Array.Empty<DashboardOverviewSnapshot.QuotaWindow>(),
            isStale
        );
    }

    private static CodexAccountProfile CreateCodexProfile(
        Guid accountId,
        string externalAccountId,
        FakeIdentityFingerprinter fingerprinter
    ) {
        return new(
            accountId,
            "Synthetic Codex profile",
            "c•••@example.test",
            fingerprinter.CreateFingerprint(externalAccountId, null, "chatgpt"),
            3,
            DashboardReaderFixture.FixedNow.AddDays(-1),
            "connected",
            externalAccountId
        );
    }

    private static QuotaSnapshotRecord CreateCodexQuotaRecord(
        CodexAccountProfile profile,
        DateTimeOffset now,
        int fiveHourRemaining,
        int weeklyRemaining,
        DateTimeOffset fiveHourReset,
        DateTimeOffset weeklyReset
    ) {
        return new(
            profile.AccountId,
            profile.IdentityFingerprint,
            profile.ProfileRevision,
            new(
                now.AddMinutes(-2),
                new(300, fiveHourRemaining, fiveHourReset),
                new(10080, weeklyRemaining, weeklyReset)
            ),
            null
        );
    }

    private static async Task<string> RenderDashboardAsync(IDashboardOverviewReader reader) {
        await using ServiceProvider services = CreateRenderingServices(reader);
        return await RenderComponentAsync<DashboardPage>(services);
    }

    private static async Task<string> RenderComponentAsync<TComponent>(
        IServiceProvider services,
        IDictionary<string, object?>? parameters = null
    ) where TComponent : IComponent {
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () => {
            HtmlRootComponent themeProvider = await renderer.RenderComponentAsync<MudThemeProvider>();
            HtmlRootComponent popoverProvider = await renderer.RenderComponentAsync<MudPopoverProvider>();
            HtmlRootComponent dialogProvider = await renderer.RenderComponentAsync<MudDialogProvider>();
            HtmlRootComponent snackbarProvider = await renderer.RenderComponentAsync<MudSnackbarProvider>();
            HtmlRootComponent component = await renderer.RenderComponentAsync<TComponent>(
                parameters is null ? ParameterView.Empty : ParameterView.FromDictionary(parameters)
            );
            return WebUtility.HtmlDecode(string.Concat(
                themeProvider.ToHtmlString(),
                popoverProvider.ToHtmlString(),
                dialogProvider.ToHtmlString(),
                snackbarProvider.ToHtmlString(),
                component.ToHtmlString()
            ));
        });
    }

    private static async Task<string> RenderRouterAsync(string location) {
        ServiceCollection serviceCollection = new();
        await using ServiceProvider services = serviceCollection
            .AddLogging()
            .AddSingleton<NavigationManager>(new StaticRenderNavigationManager(location))
            .AddSingleton<INavigationInterception, StaticNavigationInterception>()
            .AddSingleton<IScrollToLocationHash, StaticScrollToLocationHash>()
            .BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        RenderFragment<RouteData> found = routeData => builder => builder.AddContent(0, routeData.PageType.Name);
        IDictionary<string, object?> parameters = new Dictionary<string, object?> {
            [nameof(Router.AppAssembly)] = typeof(DashboardPage).Assembly,
            [nameof(Router.Found)] = found
        };

        return await renderer.Dispatcher.InvokeAsync(async () => {
            HtmlRootComponent component = await renderer.RenderComponentAsync<Router>(
                ParameterView.FromDictionary(parameters)
            );
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }

    private static async Task<string> WaitForHtmlAsync(
        HtmlRenderer renderer,
        HtmlRootComponent root,
        string expectedContent
    ) {
        for (int attempt = 0; attempt < 100; attempt++) {
            string html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
            if (html.Contains(expectedContent, StringComparison.Ordinal)) {
                return html;
            }

            await Task.Delay(10);
        }

        return await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
    }

    private static ServiceProvider CreateRenderingServices(
        IDashboardOverviewReader? reader,
        string location = "/"
    ) {
        ServiceCollection services = new();
        services
            .AddLogging()
            .AddMudServices()
            .AddSingleton<NavigationManager>(new StaticRenderNavigationManager(location))
            .AddSingleton<IJSRuntime, StaticRenderJsRuntime>();
        if (reader is not null) {
            services.AddSingleton<IDashboardOverviewReader>(reader);
            services.AddSingleton<DashboardReaderFixture>();
            services.AddSingleton(provider => provider.GetRequiredService<DashboardReaderFixture>().ClaudeQuota);
            services.AddSingleton(provider => provider.GetRequiredService<DashboardReaderFixture>().CodexQuota);
        }

        return services.BuildServiceProvider();
    }

    private static async Task<DashboardOverviewSnapshot.Provider> WaitForCancellationAsync(
        CancellationToken cancellationToken
    ) {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return CreateProvider(
            DashboardOverviewSnapshot.ProviderKind.Codex,
            DashboardOverviewSnapshot.IdentityStatus.Unknown,
            DashboardOverviewSnapshot.QuotaStatus.Unknown,
            null,
            "Synthetic source"
        );
    }

    private sealed class DashboardReaderFixture : IAsyncDisposable {
        public static readonly DateTimeOffset FixedNow = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        public const string ClaudeToken = "synthetic-claude-token";

        private readonly CodexLocalIdentityService codexIdentity;

        public DashboardReaderFixture(bool useCurrentTime = false) {
            Now = useCurrentTime ? DateTimeOffset.UtcNow : FixedNow;
            CodexFingerprinter = new();
            CodexProfile = CreateCodexProfile(Guid.NewGuid(), "codex-account-current", CodexFingerprinter);
            CodexCatalog = new();
            CodexCatalog.SetAccounts([CodexProfile]);
            CodexAuthReader = new() {
                Result = new(
                    CodexLocalAuthReadStatus.Success,
                    new CodexLocalAuthIdentity("codex-account-current", "private-codex@example.test")
                )
            };
            CodexQuotaRepository = new();
            CodexQuotaRepository.Seed(CreateCodexQuotaRecord(
                CodexProfile,
                Now,
                68,
                41,
                Now.AddHours(3),
                Now.AddDays(5)
            ));
            DashboardTestClock clock = new(Now);
            DashboardCodexNotifier notifier = new();
            codexIdentity = new(
                CodexCatalog,
                CodexAuthReader,
                notifier,
                new DashboardCodexProcessSource(Now),
                CodexFingerprinter,
                clock
            );
            CodexSessionFactory = new();
            CodexQuota = new(
                CodexCatalog,
                CodexQuotaRepository,
                new FakeSettingsRepository(AppSettings.Defaults),
                CodexSessionFactory,
                new QuotaResponseParser(),
                CodexFingerprinter,
                clock,
                new ExclusiveOperationCoordinator()
            );

            ClaudeProfile = ClaudeTestData.CreateProfile(
                Guid.NewGuid(),
                "claude-account-current",
                "claude-organization-current",
                alias: "Claude Primary"
            );
            ClaudeAccountRepository = new(ClaudeProfile);
            ClaudeProfileStore = new() {
                CurrentSnapshot = ClaudeTestData.CreateSnapshot(
                    ClaudeProfile.AccountUuid,
                    "synthetic-current-snapshot",
                    ClaudeProfile.LocalStateEncryptedKeySha256
                ),
                CurrentCandidates = [
                    new IClaudeDesktopProfileStore.TokenCandidate(ClaudeToken, "oauth:tokenCacheV2")
                ]
            };
            ClaudeAccountRepository.AddSnapshot(
                ClaudeProfile.ProfileId,
                ClaudeTestData.CreateSnapshot(
                    ClaudeProfile.AccountUuid,
                    "synthetic-saved-snapshot",
                    ClaudeProfile.LocalStateEncryptedKeySha256
                )
            );
            ClaudeInstallationLocator = new();
            ClaudeProcessController = new();
            ClaudeOAuthClient = new();
            ClaudeOAuthClient.ProfileResults[ClaudeToken] = ClaudeTestData.VerifiedProfile(
                ClaudeProfile.AccountUuid,
                ClaudeProfile.OrganizationUuid,
                "private-claude@example.test"
            );
            ClaudeQuotaRepository = new();
            ClaudeQuotaRepository.Seed(new(
                ClaudeProfile.ProfileId,
                new(
                    ClaudeProfile.ProfileId,
                    Now.AddMinutes(-3),
                    [
                        new("session", 300, 32m, Now.AddHours(2)),
                        new("weekly_all", 10080, 32m, Now.AddDays(1)),
                        new("weekly_opus", 10080, 32m, Now.AddDays(2)),
                        new("weekly_sonnet", 10080, 32m, Now.AddDays(3)),
                        new("weekly_scoped:Long Model", 10080, 32m, Now.AddDays(4))
                    ]
                ),
                ClaudeQuotaSnapshot.RefreshStatus.Fresh,
                null,
                null
            ));
            ClaudeTestClock claudeClock = new() { UtcNow = Now };
            ClaudeOperationGate claudeOperationGate = new(new ExclusiveOperationCoordinator());
            ClaudeAccountService claudeAccounts = new(
                ClaudeAccountRepository,
                ClaudeInstallationLocator,
                ClaudeProcessController,
                ClaudeProfileStore,
                ClaudeOAuthClient,
                new ClaudeTestFingerprinter(),
                claudeOperationGate,
                claudeClock
            );
            ClaudeQuota = new(
                ClaudeAccountRepository,
                ClaudeProfileStore,
                ClaudeOAuthClient,
                ClaudeQuotaRepository,
                claudeOperationGate,
                claudeClock
            );
            Reader = new(codexIdentity, CodexQuota, claudeAccounts, ClaudeQuota, CodexCatalog);
        }

        public DateTimeOffset Now { get; }

        public FakeIdentityFingerprinter CodexFingerprinter { get; }

        public CodexAccountProfile CodexProfile { get; }

        public FakeAccountCatalogRepository CodexCatalog { get; }

        public DashboardCodexAuthReader CodexAuthReader { get; }

        public DashboardQuotaSnapshotRepository CodexQuotaRepository { get; }

        public DashboardCodexSessionFactory CodexSessionFactory { get; }

        public CodexQuotaService CodexQuota { get; }

        public ClaudeQuotaService ClaudeQuota { get; }

        public ClaudeAccountProfile ClaudeProfile { get; }

        public ClaudeTestAccountRepository ClaudeAccountRepository { get; }

        public ClaudeTestProfileStore ClaudeProfileStore { get; }

        public ClaudeTestInstallationLocator ClaudeInstallationLocator { get; }

        public ClaudeTestProcessController ClaudeProcessController { get; }

        public ClaudeTestOAuthClient ClaudeOAuthClient { get; }

        public DashboardClaudeQuotaRepository ClaudeQuotaRepository { get; }

        public DashboardOverviewReader Reader { get; }

        public ValueTask DisposeAsync() {
            return codexIdentity.DisposeAsync();
        }
    }

    private sealed class SyntheticDashboardReader : IDashboardOverviewReader {
        public RecommendationAccount? CodexRecommendation { get; init; }

        public RecommendationAccount? ClaudeRecommendation { get; init; }

        public Task<RecommendationAccount?> ReadCodexRecommendationAsync(CancellationToken cancellationToken)
            => Task.FromResult(CodexRecommendation);

        public Task<RecommendationAccount?> ReadClaudeRecommendationAsync(CancellationToken cancellationToken)
            => Task.FromResult(ClaudeRecommendation);
        public Func<CancellationToken, Task<DashboardOverviewSnapshot.Provider>> CodexHandler { get; set; } = _ =>
            Task.FromResult(CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Codex,
                DashboardOverviewSnapshot.IdentityStatus.Unknown,
                DashboardOverviewSnapshot.QuotaStatus.Unknown,
                null,
                "Synthetic Codex source"
            ));

        public Func<CancellationToken, Task<DashboardOverviewSnapshot.Provider>> ClaudeHandler { get; set; } = _ =>
            Task.FromResult(CreateProvider(
                DashboardOverviewSnapshot.ProviderKind.Claude,
                DashboardOverviewSnapshot.IdentityStatus.Unknown,
                DashboardOverviewSnapshot.QuotaStatus.Unknown,
                null,
                "Synthetic Claude source"
            ));

        public CancellationToken CodexToken { get; private set; }

        public CancellationToken ClaudeToken { get; private set; }

        public Task<DashboardOverviewSnapshot.Provider>? CodexTask { get; private set; }

        public Task<DashboardOverviewSnapshot.Provider>? ClaudeTask { get; private set; }

        public Task<DashboardOverviewSnapshot.Provider> ReadCodexAsync(CancellationToken cancellationToken) {
            CodexToken = cancellationToken;
            CodexTask = CodexHandler(cancellationToken);
            return CodexTask;
        }

        public Task<DashboardOverviewSnapshot.Provider> ReadClaudeAsync(CancellationToken cancellationToken) {
            ClaudeToken = cancellationToken;
            ClaudeTask = ClaudeHandler(cancellationToken);
            return ClaudeTask;
        }
    }

    private sealed class DashboardCodexAuthReader : ICodexLocalAuthIdentityReader {
        public CodexLocalAuthReadResult Result { get; set; } = new(CodexLocalAuthReadStatus.FileMissing, null);

        public Task<CodexLocalAuthReadResult> ReadCurrentAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            CodexLocalAuthReadResult result = Result;
            CodexLocalAuthIdentity? identity = result.Identity is null
                ? null
                : new(result.Identity.AccountId, result.Identity.Email);
            return Task.FromResult(new CodexLocalAuthReadResult(result.Status, identity));
        }
    }

    private sealed class DashboardCodexNotifier : ICodexLocalAuthFileChangeNotifier {
        private Action? fileChanged;
        private Action<bool>? monitoringAvailabilityChanged;

        public event Action? FileChanged {
            add => fileChanged += value;
            remove => fileChanged -= value;
        }

        public event Action<bool>? MonitoringAvailabilityChanged {
            add => monitoringAvailabilityChanged += value;
            remove => monitoringAvailabilityChanged -= value;
        }

        public Task StartAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            monitoringAvailabilityChanged?.Invoke(true);
            return Task.CompletedTask;
        }

        public Task StopAsync() {
            fileChanged = null;
            monitoringAvailabilityChanged = null;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() {
            fileChanged = null;
            monitoringAvailabilityChanged = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DashboardCodexProcessSource(DateTimeOffset now) : ICodexDesktopProcessStatusSource {
        public Task<CodexDesktopProcessStatusSnapshot> ReadStatusAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CodexDesktopProcessStatusSnapshot(CodexDesktopProcessStatus.Unknown, now));
        }
    }

    private sealed class DashboardQuotaSnapshotRepository : IQuotaSnapshotRepository {
        private readonly Dictionary<Guid, IQuotaSnapshotRepository.Snapshot> snapshots = [];

        public List<Guid> LoadedAccountIds { get; } = [];

        public int SaveCallCount { get; private set; }

        public Guid? FailAccountId { get; set; }

        public void Seed(QuotaSnapshotRecord record) {
            snapshots[record.AccountId] = new(record, $"quota-{record.AccountId:N}");
        }

        public Task<IQuotaSnapshotRepository.Snapshot?> LoadAsync(
            Guid accountId,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            if (accountId == FailAccountId) {
                throw new InvalidOperationException("Synthetic candidate read failure.");
            }
            LoadedAccountIds.Add(accountId);
            snapshots.TryGetValue(accountId, out IQuotaSnapshotRepository.Snapshot? snapshot);
            return Task.FromResult(snapshot);
        }

        public Task<IQuotaSnapshotRepository.SaveResult> SaveAsync(
            QuotaSnapshotRecord snapshot,
            string? expectedSha256,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCallCount++;
            return Task.FromException<IQuotaSnapshotRepository.SaveResult>(
                new InvalidOperationException("Dashboard quota reads must not save snapshots.")
            );
        }
    }

    private sealed class DashboardCodexSessionFactory : ICodexAppServerSessionFactory {
        public int CreateCallCount { get; private set; }

        public Task<ICodexAppServerSession> CreateAsync(
            Guid accountId,
            long profileRevision,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCallCount++;
            return Task.FromException<ICodexAppServerSession>(
                new InvalidOperationException("Dashboard quota reads must not create app-server sessions.")
            );
        }
    }

    private sealed class DashboardClaudeQuotaRepository : IClaudeQuotaRepository {
        private readonly Dictionary<Guid, IClaudeQuotaRepository.Snapshot> snapshots = [];

        public List<Guid> LoadedProfileIds { get; } = [];

        public int SaveCallCount { get; private set; }

        public Guid? FailProfileId { get; set; }

        public void Seed(ClaudeQuotaSnapshot.View view) {
            snapshots[view.ProfileId] = new(view, $"claude-quota-{view.ProfileId:N}");
        }

        public Task<IClaudeQuotaRepository.Snapshot?> LoadAsync(
            Guid profileId,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            if (profileId == FailProfileId) {
                throw new InvalidOperationException("Synthetic candidate read failure.");
            }
            LoadedProfileIds.Add(profileId);
            snapshots.TryGetValue(profileId, out IClaudeQuotaRepository.Snapshot? snapshot);
            return Task.FromResult(snapshot);
        }

        public Task<IClaudeQuotaRepository.SaveResult> SaveAsync(
            ClaudeQuotaSnapshot.View view,
            string? expectedSha256,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCallCount++;
            return Task.FromException<IClaudeQuotaRepository.SaveResult>(
                new InvalidOperationException("Dashboard quota reads must not save snapshots.")
            );
        }
    }

    private sealed class RecordingNotificationSink : INotificationSink {
        public event Action<string>? Activated { add { } remove { } }
        public List<NotificationRequest> Messages { get; } = [];
        public Task<bool> SendAsync(NotificationRequest request, CancellationToken cancellationToken) {
            Messages.Add(request);
            return Task.FromResult(true);
        }
    }

    private sealed class MemoryNotificationStore : INotificationDedupStore {
        public Task<IReadOnlyCollection<string>> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<string>>([]);
        public Task SaveAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DashboardTestClock(DateTimeOffset utcNow) : IClock {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) {
            return Task.Delay(delay, cancellationToken);
        }
    }

    private sealed class StaticRenderJsRuntime : IJSRuntime {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) {
            return InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args
        ) {
            if (identifier == "mudThemeProvider.stopWatchingDarkMode") {
                return ValueTask.FromResult(default(TValue)!);
            }

            return ValueTask.FromException<TValue>(new InvalidOperationException(
                $"JavaScript interop is unavailable during static component rendering: {identifier}."
            ));
        }
    }

    private sealed class StaticRenderNavigationManager : NavigationManager {
        public StaticRenderNavigationManager(string location = "/") {
            Initialize("http://localhost/", new Uri(new Uri("http://localhost/"), location).ToString());
        }

        protected override void NavigateToCore(string uri, bool forceLoad) {
            throw new InvalidOperationException("Navigation is unavailable during static component rendering.");
        }
    }

    private sealed class StaticNavigationInterception : INavigationInterception {
        public Task EnableNavigationInterceptionAsync() {
            return Task.CompletedTask;
        }
    }

    private sealed class StaticScrollToLocationHash : IScrollToLocationHash {
        public Task RefreshScrollPositionForHash(string locationAbsolute) {
            return Task.CompletedTask;
        }
    }
}
