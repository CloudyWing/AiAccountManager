using AngleSharp.Dom;
using Bunit;
using NUnit.Framework;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Desktop.Components.Claude;

namespace CloudyWing.AiAccountManager.Components.Tests.Claude;

[TestFixture]
public sealed class ClaudeAccountCardTests : ComponentTestBase {
    [Test]
    public void RefreshQuota_WhenAllowed_InvokesCallback() {
        int refreshCount = 0;
        IRenderedComponent<ClaudeAccountCard> cut = RenderCard(parameters => parameters
            .Add(p => p.CanRefreshQuota, true)
            .Add(p => p.OnRefreshQuota, () => refreshCount++));

        cut.FindButton("查詢額度").Click();

        Assert.That(refreshCount, Is.EqualTo(1));
    }

    [Test]
    public void RefreshQuota_WhenBusy_DisablesButtonAndIgnoresCallback() {
        int refreshCount = 0;
        IRenderedComponent<ClaudeAccountCard> cut = RenderCard(parameters => parameters
            .Add(p => p.CanRefreshQuota, true)
            .Add(p => p.IsBusy, true)
            .Add(p => p.OnRefreshQuota, () => refreshCount++));

        IElement button = cut.FindButton("查詢額度");

        Assert.Multiple(() => {
            Assert.That(button.IsDisabled(), Is.True);
            Assert.That(refreshCount, Is.Zero);
        });
    }

    [Test]
    public void RequestSwitch_WhenAllowed_InvokesCallback() {
        int switchCount = 0;
        IRenderedComponent<ClaudeAccountCard> cut = RenderCard(parameters => parameters
            .Add(p => p.CanRequestSwitch, true)
            .Add(p => p.OnRequestSwitch, () => switchCount++));

        cut.FindButton("切換至此帳號").Click();

        Assert.That(switchCount, Is.EqualTo(1));
    }

    [Test]
    public void RequestSwitch_WhenNotAllowed_ShowsReasonAndDisablesButton() {
        IRenderedComponent<ClaudeAccountCard> cut = RenderCard(parameters => parameters
            .Add(p => p.CanRequestSwitch, false)
            .Add(p => p.SwitchDisabledReason, "Claude Desktop 正在執行。"));

        Assert.Multiple(() => {
            Assert.That(cut.Markup, Does.Contain("Claude Desktop 正在執行。"));
            Assert.That(cut.FindButton("切換至此帳號").IsDisabled(), Is.True);
        });
    }

    [Test]
    public void SelectedForSwitch_ShowsTargetChipAndReviewAction() {
        IRenderedComponent<ClaudeAccountCard> cut = RenderCard(parameters => parameters
            .Add(p => p.IsSelectedForSwitch, true)
            .Add(p => p.CanRequestSwitch, true));

        Assert.Multiple(() => {
            Assert.That(cut.Markup, Does.Contain("切換目標"));
            Assert.That(cut.HasButton("檢視切換內容"), Is.True);
            Assert.That(cut.HasButton("切換至此帳號"), Is.False);
        });
    }

    [Test]
    public void ToggleExpanded_ShowsAndHidesDetails() {
        IRenderedComponent<ClaudeAccountCard> cut = RenderCard(parameters => parameters
            .Add(p => p.OrganizationDisplayName, "org-1234…abcd"));

        Assert.Multiple(() => {
            Assert.That(cut.FindAll(".account-card-details"), Is.Empty);
            Assert.That(cut.FindByLabel("展開 Alpha 詳細資料").GetAttribute("aria-expanded"), Is.EqualTo("false"));
        });

        cut.FindByLabel("展開 Alpha 詳細資料").Click();

        Assert.Multiple(() => {
            Assert.That(cut.FindAll(".account-card-details"), Has.Count.EqualTo(1));
            Assert.That(cut.Markup, Does.Contain("org-1234…abcd"));
            Assert.That(cut.FindByLabel("收合 Alpha 詳細資料").GetAttribute("aria-expanded"), Is.EqualTo("true"));
        });

        cut.FindByLabel("收合 Alpha 詳細資料").Click();

        Assert.That(cut.FindAll(".account-card-details"), Is.Empty);
    }

    [Test]
    public void SaveAlias_SendsEditedDraft() {
        List<string?> savedAliases = [];
        IRenderedComponent<ClaudeAccountCard> cut = RenderCard(parameters => parameters
            .Add(p => p.Alias, "Alpha")
            .Add(p => p.OnSaveAlias, alias => savedAliases.Add(alias)));
        cut.FindByLabel("展開 Alpha 詳細資料").Click();

        cut.Find(".account-alias-row input").Input("工作帳號");
        cut.FindButton("保存別名").Click();

        Assert.That(savedAliases, Is.EqualTo(new[] { "工作帳號" }));
    }

    [Test]
    public void AliasParameterChange_ReplacesDraft() {
        List<string?> savedAliases = [];
        IRenderedComponent<ClaudeAccountCard> cut = RenderCard(parameters => parameters
            .Add(p => p.Alias, "Alpha")
            .Add(p => p.OnSaveAlias, alias => savedAliases.Add(alias)));
        cut.FindByLabel("展開 Alpha 詳細資料").Click();
        cut.Find(".account-alias-row input").Input("尚未保存的草稿");

        cut.Render(parameters => parameters.Add(p => p.Alias, "已由頁面更新"));
        cut.FindButton("保存別名").Click();

        Assert.That(savedAliases, Is.EqualTo(new[] { "已由頁面更新" }));
    }

    [Test]
    public void Remove_InvokesCallback() {
        int removeCount = 0;
        IRenderedComponent<ClaudeAccountCard> cut = RenderCard(parameters => parameters
            .Add(p => p.OnRemove, () => removeCount++));
        cut.FindByLabel("展開 Alpha 詳細資料").Click();

        cut.FindButton("移除此帳號").Click();

        Assert.That(removeCount, Is.EqualTo(1));
    }

    [Test]
    public void CatalogLocked_DisablesEditingAndShowsReason() {
        IRenderedComponent<ClaudeAccountCard> cut = RenderCard(parameters => parameters
            .Add(p => p.CanEditCatalog, false)
            .Add(p => p.CanMoveUp, true)
            .Add(p => p.CanMoveDown, true)
            .Add(p => p.CatalogDisabledReason, "切換進行中，暫時無法修改帳號清單。"));
        cut.FindByLabel("展開 Alpha 詳細資料").Click();

        Assert.Multiple(() => {
            Assert.That(cut.Markup, Does.Contain("切換進行中，暫時無法修改帳號清單。"));
            Assert.That(cut.FindButton("保存別名").IsDisabled(), Is.True);
            Assert.That(cut.FindButton("移除此帳號").IsDisabled(), Is.True);
            Assert.That(cut.FindByLabel("上移 Alpha").IsDisabled(), Is.True);
            Assert.That(cut.FindByLabel("下移 Alpha").IsDisabled(), Is.True);
        });
    }

    [Test]
    public void MoveButtons_FollowPositionAndInvokeCallbacks() {
        int moveUpCount = 0;
        int moveDownCount = 0;
        IRenderedComponent<ClaudeAccountCard> cut = RenderCard(parameters => parameters
            .Add(p => p.CanMoveUp, false)
            .Add(p => p.CanMoveDown, true)
            .Add(p => p.OnMoveUp, () => moveUpCount++)
            .Add(p => p.OnMoveDown, () => moveDownCount++));

        IElement moveUp = cut.FindByLabel("上移 Alpha");
        cut.FindByLabel("下移 Alpha").Click();

        Assert.Multiple(() => {
            Assert.That(moveUp.IsDisabled(), Is.True);
            Assert.That(moveUp.GetAttribute("title"), Is.EqualTo("目前已是第一個帳號，無法上移。"));
            Assert.That(moveUpCount, Is.Zero);
            Assert.That(moveDownCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void QuotaWindows_RenderOneBarPerWindowWithClampedRemaining() {
        IRenderedComponent<ClaudeAccountCard> cut = RenderCard(parameters => parameters
            .Add(p => p.QuotaWindows, new[] {
                new ClaudeQuotaSnapshot.Window("session", 300, 110m, null),
                new ClaudeQuotaSnapshot.Window("weekly_all", 10080, 30m, null),
                new ClaudeQuotaSnapshot.Window("custom", 60, -5m, null)
            }));

        string[] labels = cut.FindAll(".quota-bar")
            .Select(bar => bar.GetAttribute("aria-label") ?? string.Empty)
            .ToArray();

        Assert.Multiple(() => {
            Assert.That(labels, Has.Length.EqualTo(3));
            Assert.That(labels[0], Does.StartWith("5 小時，0% 剩餘"));
            Assert.That(labels[1], Does.StartWith("每週（全部），70% 剩餘"));
            Assert.That(labels[2], Does.StartWith("額度視窗（custom），100% 剩餘"));
            Assert.That(cut.Markup, Does.Contain("重設：未知"));
            Assert.That(cut.Markup, Does.Not.Contain("尚未取得此帳號的額度快照。"));
        });
    }

    [Test]
    public void NoQuotaWindows_ShowsEmptySnapshotHint() {
        IRenderedComponent<ClaudeAccountCard> cut = RenderCard();

        Assert.Multiple(() => {
            Assert.That(cut.Markup, Does.Contain("尚未取得此帳號的額度快照。"));
            Assert.That(cut.FindAll(".quota-bar"), Is.Empty);
        });
    }

    private IRenderedComponent<ClaudeAccountCard> RenderCard(
        Action<ComponentParameterCollectionBuilder<ClaudeAccountCard>>? configure = null
    ) {
        return Context.Render<ClaudeAccountCard>(parameters => {
            parameters
                .Add(p => p.DisplayName, "Alpha")
                .Add(p => p.MaskedIdentity, "a***@example.test");
            configure?.Invoke(parameters);
        });
    }
}
