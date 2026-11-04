using Bunit;
using NUnit.Framework;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Desktop.Components.Codex;

namespace CloudyWing.AiAccountManager.Components.Tests.Codex;

[TestFixture]
public sealed class CodexAccountCardTests : ComponentTestBase {
    private static readonly CodexAccountProfile Account = new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        "Work",
        "w***@example.test",
        "fingerprint",
        1,
        DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
        "active"
    );

    [Test]
    public void DisplayName_FallsBackToMaskedIdentityWithoutAlias() {
        IRenderedComponent<CodexAccountCard> cut = RenderCard(Account with { Alias = null });

        Assert.That(cut.FindAll("[aria-label='展開 w***@example.test 詳細資料']"), Has.Count.EqualTo(1));
    }

    [Test]
    public void Switch_WhenAllowed_SendsAccountId() {
        List<Guid> switched = [];
        IRenderedComponent<CodexAccountCard> cut = RenderCard(Account, parameters => parameters
            .Add(p => p.CanSwitch, true)
            .Add(p => p.OnSwitch, id => switched.Add(id)));

        cut.FindButton("切換至此帳號").Click();

        Assert.That(switched, Is.EqualTo(new[] { Account.AccountId }));
    }

    [Test]
    public void LocallyMatched_ShowsCurrentDesktopLabelAndHidesDisabledReason() {
        IRenderedComponent<CodexAccountCard> cut = RenderCard(Account, parameters => parameters
            .Add(p => p.IsLocallyMatched, true)
            .Add(p => p.CanSwitch, false)
            .Add(p => p.SwitchDisabledReason, "已是目前帳號。"));

        Assert.Multiple(() => {
            Assert.That(cut.Markup, Does.Contain("符合本機登入資料"));
            Assert.That(cut.FindButton("目前 Desktop 帳號").IsDisabled(), Is.True);
            Assert.That(cut.Markup, Does.Not.Contain("已是目前帳號。"));
        });
    }

    [Test]
    public void Switching_ShowsProgressText() {
        IRenderedComponent<CodexAccountCard> cut = RenderCard(Account, parameters => parameters
            .Add(p => p.IsSwitching, true)
            .Add(p => p.Busy, true));

        Assert.That(cut.FindButton("正在切換…").IsDisabled(), Is.True);
    }

    [Test]
    public void Refresh_SendsAccountId() {
        List<Guid> refreshed = [];
        IRenderedComponent<CodexAccountCard> cut = RenderCard(Account, parameters => parameters
            .Add(p => p.OnRefresh, id => refreshed.Add(id)));

        cut.FindByLabel("刷新 Work 額度").Click();

        Assert.That(refreshed, Is.EqualTo(new[] { Account.AccountId }));
    }

    [Test]
    public void Refreshing_DisablesRefreshAndShowsProgress() {
        IRenderedComponent<CodexAccountCard> cut = RenderCard(Account, parameters => parameters
            .Add(p => p.Quota, new QuotaSnapshotView(Account.AccountId, null, QuotaDisplayStatus.Refreshing, false, null)));

        Assert.Multiple(() => {
            Assert.That(cut.FindByLabel("刷新 Work 額度").IsDisabled(), Is.True);
            Assert.That(cut.Markup, Does.Contain("刷新中"));
            Assert.That(cut.Markup, Does.Contain("正在讀取額度；完成後會更新此摘要。"));
        });
    }

    [TestCase(QuotaDisplayStatus.Fresh, "最新")]
    [TestCase(QuotaDisplayStatus.Stale, "舊值")]
    [TestCase(QuotaDisplayStatus.ReauthenticationRequired, "需要重新登入")]
    [TestCase(QuotaDisplayStatus.Error, "讀取失敗")]
    [TestCase(QuotaDisplayStatus.Incomplete, "回應不完整")]
    [TestCase(QuotaDisplayStatus.Unknown, "尚未讀取")]
    public void Status_ShowsLocalizedChip(QuotaDisplayStatus status, string expected) {
        IRenderedComponent<CodexAccountCard> cut = RenderCard(Account, parameters => parameters
            .Add(p => p.Quota, new QuotaSnapshotView(Account.AccountId, null, status, false, null)));

        Assert.That(cut.Find(".mud-chip").TextContent.Trim(), Is.EqualTo(expected));
    }

    [Test]
    public void NoSuccessfulSnapshot_DoesNotInventPercentages() {
        IRenderedComponent<CodexAccountCard> cut = RenderCard(Account);

        Assert.Multiple(() => {
            Assert.That(cut.FindAll(".quota-bar"), Is.Empty);
            Assert.That(cut.Markup, Does.Contain("尚無成功額度快照"));
            Assert.That(cut.FindButton("使用重置券").IsDisabled(), Is.True);
        });
    }

    [Test]
    public void SuccessfulSnapshot_ShowsBothWindowsAndUsesCredit() {
        List<Guid> used = [];
        IRenderedComponent<CodexAccountCard> cut = RenderCard(Account, parameters => parameters
            .Add(p => p.Quota, CreateView(availableCredits: 2))
            .Add(p => p.OnUseCredit, id => used.Add(id)));

        cut.FindButton("使用重置券").Click();

        Assert.Multiple(() => {
            Assert.That(cut.FindAll(".quota-bar"), Has.Count.EqualTo(2));
            Assert.That(cut.Markup, Does.Contain("重置券 2 張"));
            Assert.That(used, Is.EqualTo(new[] { Account.AccountId }));
        });
    }

    [Test]
    public void CreditRefreshRequired_BlocksCreditUseWithHint() {
        IRenderedComponent<CodexAccountCard> cut = RenderCard(Account, parameters => parameters
            .Add(p => p.Quota, CreateView(availableCredits: 1))
            .Add(p => p.CreditRefreshRequired, true));

        Assert.Multiple(() => {
            Assert.That(cut.FindButton("使用重置券").IsDisabled(), Is.True);
            Assert.That(cut.Markup, Does.Contain("請先刷新額度，再決定是否使用重置券。"));
        });
    }

    [Test]
    public void NoAvailableCredit_DisablesCreditUse() {
        IRenderedComponent<CodexAccountCard> cut = RenderCard(Account, parameters => parameters
            .Add(p => p.Quota, CreateView(availableCredits: 0)));

        Assert.Multiple(() => {
            Assert.That(cut.FindButton("使用重置券").IsDisabled(), Is.True);
            Assert.That(cut.Markup, Does.Contain("沒有可用重置券"));
        });
    }

    [Test]
    public void ExpandedDetails_ForwardAliasEditsSaveAndRemove() {
        List<string?> aliasChanges = [];
        List<Guid> saved = [];
        List<Guid> removed = [];
        IRenderedComponent<CodexAccountCard> cut = RenderCard(Account, parameters => parameters
            .Add(p => p.AliasValue, "Work")
            .Add(p => p.AliasValueChanged, value => aliasChanges.Add(value))
            .Add(p => p.OnSaveAlias, id => saved.Add(id))
            .Add(p => p.OnRemove, id => removed.Add(id)));

        cut.FindByLabel("展開 Work 詳細資料").Click();
        cut.Find(".account-alias-row input").Input("Personal");
        cut.FindButton("保存別名").Click();
        cut.FindButton("移除此帳號").Click();

        Assert.Multiple(() => {
            Assert.That(aliasChanges, Is.EqualTo(new[] { "Personal" }));
            Assert.That(saved, Is.EqualTo(new[] { Account.AccountId }));
            Assert.That(removed, Is.EqualTo(new[] { Account.AccountId }));
        });
    }

    [Test]
    public void ToggleExpanded_ReportsStateToAssistiveTechnology() {
        IRenderedComponent<CodexAccountCard> cut = RenderCard(Account);

        Assert.That(cut.FindByLabel("展開 Work 詳細資料").GetAttribute("aria-expanded"), Is.EqualTo("false"));

        cut.FindByLabel("展開 Work 詳細資料").Click();

        Assert.Multiple(() => {
            Assert.That(cut.FindByLabel("收合 Work 詳細資料").GetAttribute("aria-expanded"), Is.EqualTo("true"));
            Assert.That(cut.FindAll(".account-card-details"), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void MoveButtons_SendAccountId() {
        List<Guid> movedUp = [];
        List<Guid> movedDown = [];
        IRenderedComponent<CodexAccountCard> cut = RenderCard(Account, parameters => parameters
            .Add(p => p.CanMoveUp, true)
            .Add(p => p.CanMoveDown, false)
            .Add(p => p.OnMoveUp, id => movedUp.Add(id))
            .Add(p => p.OnMoveDown, id => movedDown.Add(id)));

        cut.FindByLabel("上移 Work").Click();

        Assert.Multiple(() => {
            Assert.That(movedUp, Is.EqualTo(new[] { Account.AccountId }));
            Assert.That(cut.FindByLabel("下移 Work").IsDisabled(), Is.True);
            Assert.That(movedDown, Is.Empty);
        });
    }

    private static QuotaSnapshotView CreateView(long availableCredits) {
        QuotaSuccessfulSnapshot success = new(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            new QuotaWindow(300, 70, DateTimeOffset.UtcNow.AddHours(2)),
            new QuotaWindow(10080, 40, DateTimeOffset.UtcNow.AddDays(3)),
            new ResetCreditSummary(availableCredits, [])
        );
        QuotaSnapshotRecord record = new(Account.AccountId, Account.IdentityFingerprint, Account.ProfileRevision, success, null);
        return new QuotaSnapshotView(Account.AccountId, record, QuotaDisplayStatus.Fresh, false, null);
    }

    private IRenderedComponent<CodexAccountCard> RenderCard(
        CodexAccountProfile account,
        Action<ComponentParameterCollectionBuilder<CodexAccountCard>>? configure = null
    ) {
        return Context.Render<CodexAccountCard>(parameters => {
            parameters.Add(p => p.Account, account);
            configure?.Invoke(parameters);
        });
    }
}
