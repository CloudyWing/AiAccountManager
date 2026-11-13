using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Claude;

[TestFixture]
public sealed class ClaudeDesktopSwitchServiceTests {
    [TestCase("source-directory-missing")]
    [TestCase("index-path-is-file")]
    [TestCase("source-index-too-large")]
    [TestCase("source-index-entry-unsupported")]
    [TestCase("source-index-json-invalid")]
    [TestCase("source-index-session-id-invalid")]
    [TestCase("source-path-unsafe")]
    [TestCase("source-directory-unreadable")]
    [TestCase("source-unreadable")]
    [TestCase("transcript-root-unavailable")]
    [TestCase("transcript-root-missing")]
    [TestCase("transcript-missing")]
    [TestCase("transcript-not-unique")]
    [TestCase("transcript-unreadable")]
    [TestCase("transcript-root-unreadable")]
    [TestCase("history-source-binding-unknown")]
    [TestCase("unrecognized-code")]
    [TestCase(null)]
    [TestCase(" ")]
    public async Task PreviewSwitchAsync_HistoryFailureCode_ShowsSingleMessageWithoutCode(string? failureCode) {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        fixture.HistoryStore!.ValidationOverride = new(
            IClaudeSessionHistoryStore.ValidationStatus.MissingDirectory, 0, failureCode
        );

        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);

        Assert.That(preview.CanProceed, Is.False);
        Assert.That(preview.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.PreviewOutcome.HistoryUnavailable));
        Assert.That(preview.Message, Is.EqualTo("無法讀取來源帳號的 Code Session 資料，未開始切換。"));
        Assert.That(preview.Message, Does.Not.Contain("原因代碼"));
        if (!string.IsNullOrWhiteSpace(failureCode)) {
            Assert.That(preview.Message, Does.Not.Contain(failureCode));
        }
    }

    [Test]
    public async Task PreviewSwitchAsync_AndSwitchAsync_CompleteAuthOnlyTransaction() {
        SwitchFixture fixture = CreateFixture(includeHistory: false);

        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);
        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches
            .SwitchAsync(preview, CancellationToken.None);

        Assert.That(preview.CanProceed, Is.True);
        Assert.That(preview.RequiresHistoryOverwrite, Is.False);
        Assert.That(preview.SourceSessionCount, Is.Null);
        Assert.That(preview.TargetSessionCount, Is.Null);
        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.Succeeded));
        Assert.That(result.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.Committed));
        Assert.That(fixture.ProfileStore.CurrentSnapshot!.LastKnownAccountUuid, Is.EqualTo("account-b"));
        Assert.That(fixture.Process.CloseCallCount, Is.EqualTo(1));
        Assert.That(fixture.Process.LaunchCallCount, Is.EqualTo(1));
        Assert.That(fixture.Process.Operations.IndexOf("close"), Is.LessThan(fixture.Process.Operations.IndexOf("launch")));
        Assert.That(fixture.Repository.Profiles.Single(profile => profile.ProfileId == fixture.ProfileA.ProfileId).ProfileRevision,
            Is.EqualTo(2));
    }

    [Test]
    public async Task PreviewAndSwitchAsync_AcceptsVerifiedMsixSourceAfterPackageVersionUpdate() {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        ClaudeDesktopRootBinding.Observation currentObservation = ClaudeTestData.Observation with {
            PackageFullName = "Claude.Package_2.0.0.0_x64__abcd1234wxyz1"
        };
        IClaudeDesktopInstallationLocator.Installation currentInstallation = ClaudeTestData.Installation with {
            Observation = currentObservation,
            Binding = null
        };
        fixture.Locator.Result = new(
            IClaudeDesktopInstallationLocator.LocateStatus.Found,
            currentInstallation,
            null
        );
        fixture.Locator.ObservationResult = new(
            IClaudeDesktopInstallationLocator.LocateStatus.Found,
            currentObservation,
            null,
            currentInstallation
        );

        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);
        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches
            .SwitchAsync(preview, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(preview.CanProceed, Is.True);
            Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.Succeeded));
            Assert.That(fixture.ProfileStore.CurrentSnapshot!.LastKnownAccountUuid, Is.EqualTo("account-b"));
            Assert.That(fixture.Process.CloseCallCount, Is.EqualTo(1));
            Assert.That(fixture.Process.LaunchCallCount, Is.EqualTo(1));
            Assert.That(fixture.Locator.ResolveBoundCallCount, Is.GreaterThan(1));
        });
    }

    [Test]
    public async Task SwitchAsync_StopsBeforeRestoreWhenPackageVersionChangesAfterPreview() {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        ClaudeDesktopRootBinding.Observation versionTwo = ClaudeTestData.Observation with {
            PackageFullName = "Claude.Package_2.0.0.0_x64__abcd1234wxyz1"
        };
        IClaudeDesktopInstallationLocator.Installation currentInstallation = ClaudeTestData.Installation with {
            Observation = versionTwo,
            Binding = null
        };
        fixture.Locator.Result = new(
            IClaudeDesktopInstallationLocator.LocateStatus.Found,
            currentInstallation,
            null
        );
        fixture.Locator.ObservationResult = new(
            IClaudeDesktopInstallationLocator.LocateStatus.Found,
            versionTwo,
            null,
            currentInstallation
        );
        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);
        fixture.Locator.OnResolveBound = () => {
            if (fixture.Locator.ResolveBoundCallCount == 1) {
                ClaudeDesktopRootBinding.Observation versionThree = versionTwo with {
                    PackageFullName = "Claude.Package_3.0.0.0_x64__abcd1234wxyz1"
                };
                fixture.Locator.Result = new(
                    IClaudeDesktopInstallationLocator.LocateStatus.Found,
                    currentInstallation with { Observation = versionThree },
                    null
                );
            }
        };

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches
            .SwitchAsync(preview, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(preview.CanProceed, Is.True);
            Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.FailedRolledBack));
            Assert.That(fixture.ProfileStore.Operations, Does.Not.Contain("apply"));
            Assert.That(fixture.ProfileStore.RestoreCallCount, Is.Zero);
        });
    }

    [Test]
    public async Task PreviewSwitchAsync_F002_RejectsLegacySnapshotWithoutCookieSidecarExistenceEvidence() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        fixture.Repository.AddSnapshot(
            fixture.ProfileB.ProfileId,
            fixture.SnapshotB with {
                CookieWriteAheadLog = null,
                CookieSharedMemory = null,
                CookieSidecarSnapshotVersion = 0
            }
        );

        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);

        Assert.That(preview.CanProceed, Is.False);
        Assert.That(preview.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.PreviewOutcome.TargetBindingMismatch));
        Assert.That(preview.Message, Does.Contain("請重新保存此帳號"));
        Assert.That(fixture.Process.CloseCallCount, Is.Zero);
        Assert.That(fixture.HistoryStore!.Operations, Is.Empty);
    }

    [Test]
    public async Task SwitchAsync_T303_PersistsHistoryPreparationStartedBeforeTheHistoryAdapterRuns() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        ClaudeSwitchTransaction.Stage? stageAtPrepare = null;
        fixture.HistoryStore!.BeforePrepare = () => stageAtPrepare = fixture.Repository.Transaction!.CurrentStage;
        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches
            .SwitchAsync(preview, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.Succeeded));
        Assert.That(stageAtPrepare, Is.EqualTo(ClaudeSwitchTransaction.Stage.HistoryPreparationStarted));
        Assert.That(fixture.Repository.SaveTransactionCallCount, Is.GreaterThan(0));
        Assert.That(fixture.HistoryStore.Operations.IndexOf("prepare"), Is.GreaterThan(0));
        Assert.That(
            fixture.HistoryStore.CommitIntents,
            Is.EqualTo(new[] { ClaudeSessionHistoryCleanupIntent.Committed })
        );
    }

    [Test]
    public async Task RecoverPendingSwitchAsync_F005T303_CleansPartialHistoryPreparationAfterCancellationAndRestart() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        using CancellationTokenSource cancellation = new();
        ClaudeSwitchTransaction.Stage? stageAtPayload = null;
        fixture.HistoryStore!.ThrowOnCommit = true;
        fixture.HistoryStore.AfterPreparationPayload = _ => {
            stageAtPayload = fixture.Repository.Transaction!.CurrentStage;
            cancellation.Cancel();
        };
        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);

        ClaudeDesktopSwitchService.SwitchResult interrupted = await fixture.Switches
            .SwitchAsync(preview, cancellation.Token);

        Assert.That(interrupted.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.RecoveryRequired));
        Assert.That(stageAtPayload, Is.EqualTo(ClaudeSwitchTransaction.Stage.HistoryPreparationStarted));
        Assert.That(interrupted.Transaction!.RecoveryFromStage,
            Is.EqualTo(ClaudeSwitchTransaction.Stage.HistoryPreparationStarted));
        Assert.That(fixture.HistoryStore.GetIndex("account-b", "org-b"), Is.EqualTo(new[] { "old-b-session" }));
        Assert.That(fixture.HistoryStore.Operations, Does.Not.Contain("apply"));
        Assert.That(fixture.HistoryStore.Operations, Does.Not.Contain("restore-history"));
        Assert.That(fixture.ProfileStore.RestoreCallCount, Is.Zero);

        fixture.HistoryStore.ThrowOnCommit = false;
        ClaudeDesktopSwitchService restartedService = CreateSwitchService(fixture);
        ClaudeDesktopSwitchService.SwitchResult recovered = await restartedService
            .RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(recovered.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.FailedRolledBack));
        Assert.That(recovered.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RolledBack));
        Assert.That(fixture.HistoryStore.CommitCallCount, Is.EqualTo(2));
        Assert.That(
            fixture.HistoryStore.CommitIntents,
            Is.EqualTo(new[] {
                ClaudeSessionHistoryCleanupIntent.Preparation,
                ClaudeSessionHistoryCleanupIntent.Preparation
            })
        );
        Assert.That(fixture.HistoryStore.GetIndex("account-b", "org-b"), Is.EqualTo(new[] { "old-b-session" }));
        Assert.That(fixture.ProfileStore.RestoreCallCount, Is.Zero);
        Assert.That(fixture.Process.LaunchCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task PreviewSwitchAsync_BlocksWhenRecoveryJournalIsPending() {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        fixture.Repository.Transaction = new(
            Guid.NewGuid(),
            fixture.ProfileA.ProfileId,
            fixture.ProfileB.ProfileId,
            fixture.ProfileA.ProfileRevision,
            fixture.ProfileB.ProfileRevision,
            ClaudeTestData.Installation.InstallationId,
            ClaudeSwitchTransaction.Stage.RecoveryRequired,
            ClaudeTestData.Now.AddMinutes(-2),
            ClaudeTestData.Now,
            "synthetic_failure",
            ClaudeSwitchTransaction.Stage.TargetAuthApplyStarted
        );

        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);

        Assert.That(preview.CanProceed, Is.False);
        Assert.That(preview.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.PreviewOutcome.RecoveryRequired));
        Assert.That(fixture.ProfileStore.ReadCurrentCallCount, Is.Zero);
        Assert.That(fixture.Process.CloseCallCount, Is.Zero);
    }

    [Test]
    public async Task PreviewSwitchAsync_DoesNotPrepareHistoryWhenCurrentOrganizationCandidatesAreAmbiguous() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        fixture.ProfileStore.CurrentCandidates = [
            new("token-org-a", "oauth:tokenCache"),
            new("token-org-b", "oauth:tokenCacheV2")
        ];
        fixture.OAuth.ProfileResults["token-org-a"] = ClaudeTestData.VerifiedProfile("account-a", "org-a");
        fixture.OAuth.ProfileResults["token-org-b"] = ClaudeTestData.VerifiedProfile("account-a", "org-b");

        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);

        Assert.That(preview.CanProceed, Is.False);
        Assert.That(preview.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.PreviewOutcome.IdentityUnavailable));
        Assert.That(fixture.HistoryStore!.Operations, Is.Empty);
        Assert.That(fixture.Process.CloseCallCount, Is.Zero);
        Assert.That(fixture.ProfileStore.RestoreCallCount, Is.Zero);
    }

    [Test]
    public async Task SwitchAsync_RejectsPreviewAfterProfileRevisionChanges() {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);
        ClaudeAccountProfile updatedTarget = fixture.Repository.Profiles.Single(
            profile => profile.ProfileId == fixture.ProfileB.ProfileId
        ) with { Alias = "Updated", ProfileRevision = fixture.ProfileB.ProfileRevision + 1 };
        fixture.Repository.Profiles[fixture.Repository.Profiles.FindIndex(
            profile => profile.ProfileId == fixture.ProfileB.ProfileId
        )] = updatedTarget;

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches
            .SwitchAsync(preview, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.Rejected));
        Assert.That(fixture.Process.CloseCallCount, Is.Zero);
        Assert.That(fixture.ProfileStore.RestoreCallCount, Is.Zero);
    }

    [Test]
    public async Task SwitchAsync_DoesNotWriteTargetWhenDesktopCannotCloseGracefully() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);
        fixture.Process.CloseResult = false;

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches
            .SwitchAsync(preview, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.FailedRolledBack));
        Assert.That(result.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RolledBack));
        Assert.That(fixture.ProfileStore.RestoreCallCount, Is.Zero);
        Assert.That(fixture.ProfileStore.CurrentSnapshot, Is.SameAs(fixture.SnapshotA));
        Assert.That(fixture.HistoryStore!.Operations, Does.Not.Contain("prepare"));
    }

    [Test]
    public async Task SwitchAsync_RechecksProcessAfterCloseAndClosesUnexpectedNewProcessBeforeRollback() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);
        fixture.Process.SetStatusSequence(
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.Running
        );

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches
            .SwitchAsync(preview, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.FailedRolledBack));
        Assert.That(fixture.Process.CloseCallCount, Is.EqualTo(2));
        Assert.That(fixture.HistoryStore!.Operations, Does.Not.Contain("apply"));
        Assert.That(fixture.ProfileStore.CurrentSnapshot!.LastKnownAccountUuid, Is.EqualTo("account-a"));
        Assert.That(fixture.Process.Operations.Count(operation => operation == "process-status"), Is.GreaterThanOrEqualTo(6));
    }

    [Test]
    public async Task SwitchAsync_StopsBeforeHistoryWriteWhenProcessStatusBecomesUnknown() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);
        fixture.Process.SetStatusSequence(
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.Unknown
        );

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches
            .SwitchAsync(preview, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.RecoveryRequired));
        Assert.That(result.Transaction!.RecoveryFromStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.HistoryApplyStarted));
        Assert.That(fixture.HistoryStore!.Operations, Does.Not.Contain("apply"));
        Assert.That(fixture.ProfileStore.RestoreCallCount, Is.Zero);
        Assert.That(fixture.Repository.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RecoveryRequired));
    }

    [Test]
    public async Task SwitchAsync_DoesNotWriteRollbackDataWhenUnexpectedProcessCannotClose() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);
        fixture.Process.SetStatusSequence(
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.Running
        );
        fixture.Process.SetCloseResultSequence(true, false);

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches
            .SwitchAsync(preview, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.RecoveryRequired));
        Assert.That(result.Transaction!.FailureCode, Is.EqualTo("rollback:rollback_close_timeout"));
        Assert.That(result.Message, Is.EqualTo("復原未完成：Claude Desktop 仍在執行或無法確認已結束，請先正常結束 Claude Desktop 後按「重試復原」。"));
        Assert.That(fixture.Process.CloseCallCount, Is.EqualTo(2));
        Assert.That(fixture.HistoryStore!.Operations, Does.Not.Contain("restore-history"));
        Assert.That(fixture.ProfileStore.RestoreCallCount, Is.Zero);
        Assert.That(fixture.Repository.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RecoveryRequired));
    }

    [Test]
    public async Task SwitchAsync_RestoresTargetHistoryWhenApplyFailsAfterWrite() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);
        fixture.HistoryStore!.ThrowOnApplyAfterWrite = true;

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches
            .SwitchAsync(preview, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.FailedRolledBack));
        Assert.That(result.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RolledBack));
        Assert.That(fixture.HistoryStore.GetIndex("account-b", "org-b"), Is.EqualTo(new[] { "old-b-session" }));
        Assert.That(fixture.ProfileStore.CurrentSnapshot!.LastKnownAccountUuid, Is.EqualTo("account-a"));
        Assert.That(fixture.HistoryStore.Operations, Does.Contain("restore-history"));
        Assert.That(
            fixture.HistoryStore.CommitIntents,
            Is.EqualTo(new[] { ClaudeSessionHistoryCleanupIntent.Preparation })
        );
    }

    [Test]
    public async Task SwitchAsync_PreservesRecoveryJournalWhenHistoryRollbackFails() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);
        fixture.HistoryStore!.ThrowOnApplyAfterWrite = true;
        fixture.HistoryStore.ThrowOnRestore = true;

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches
            .SwitchAsync(preview, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.RecoveryRequired));
        Assert.That(result.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RecoveryRequired));
        Assert.That(result.Transaction.RecoveryFromStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.HistoryApplyStarted));
        Assert.That(fixture.Repository.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RecoveryRequired));
    }

    [Test]
    public async Task SwitchAsync_RollsBackSourceWhenTargetIdentityCannotBeVerifiedAfterLaunch() {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        fixture.OAuth.SetProfileSequence(
            "token-b",
            ClaudeTestData.VerifiedProfile("account-b", "org-b"),
            ClaudeTestData.VerifiedProfile("account-b", "org-b"),
            ClaudeTestData.VerifiedProfile("account-other", "org-other")
        );
        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches
            .SwitchAsync(preview, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.FailedRolledBack));
        Assert.That(result.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RolledBack));
        Assert.That(fixture.ProfileStore.CurrentSnapshot!.LastKnownAccountUuid, Is.EqualTo("account-a"));
        Assert.That(fixture.Process.CloseCallCount, Is.EqualTo(2));
        Assert.That(fixture.Process.LaunchCallCount, Is.EqualTo(2));
    }

    [Test]
    public async Task SwitchAsync_PersistsRecoveryJournalWhenSourceRestoreFails() {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        fixture.ProfileStore.ThrowOnRestore = true;
        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches
            .SwitchAsync(preview, CancellationToken.None);
        ClaudeAccountService accountService = CreateAccountService(fixture);
        ClaudeAccountService.PageState pageState = await accountService.GetPageStateAsync(CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.RecoveryRequired));
        Assert.That(result.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RecoveryRequired));
        Assert.That(result.Transaction.RecoveryFromStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.TargetAuthApplyStarted));
        Assert.That(pageState.RecoveryRequired, Is.True);
        Assert.That(pageState.CurrentIdentity.Status, Is.EqualTo(ClaudeAccountService.IdentityStatus.RecoveryRequired));
    }

    [TestCase("desktop-not-stopped", "Claude Desktop 尚未完全結束，請先正常結束 Claude Desktop 後按「重試復原」。")]
    [TestCase("running-installation-mismatch", "Claude Desktop 尚未完全結束，請先正常結束 Claude Desktop 後按「重試復原」。")]
    [TestCase("source-changed", "Claude Desktop 尚未完全結束，請先正常結束 Claude Desktop 後按「重試復原」。")]
    [TestCase("multiple-data-roots", "找到多份 Claude Desktop 安裝或資料，請確認只保留一份後按「重試復原」。")]
    [TestCase("installation-id-mismatch", "找到多份 Claude Desktop 安裝或資料，請確認只保留一份後按「重試復原」。")]
    [TestCase("data-layout-unknown", "Claude Desktop 的資料已變動，無法自動復原，原資料已保留。")]
    [TestCase("local-state-changed", "Claude Desktop 的資料已變動，無法自動復原，原資料已保留。")]
    [TestCase("data-root-changed", "Claude Desktop 的資料已變動，無法自動復原，原資料已保留。")]
    [TestCase("binding-invalid", "Claude Desktop 的資料已變動，無法自動復原，原資料已保留。")]
    [TestCase(
        "source-identity-unknown",
        "無法確認原 Claude Desktop 的來源，請確認使用切換時的 Windows 使用者登入，稍後按「重試復原」。"
    )]
    [TestCase("unrecognized-code", "無法確認原 Claude Desktop 安裝，請稍後按「重試復原」。")]
    [TestCase(null, "無法確認原 Claude Desktop 安裝，請稍後按「重試復原」。")]
    public async Task RecoverPendingSwitchAsync_LocatorFails_PreservesFailureCodeAndShowsActionableMessage(
        string? locatorCode, string expectedMessage
    ) {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        fixture.Repository.Transaction = CreatePendingRecoveryTransaction(fixture);
        fixture.Locator.Result = new(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, null, locatorCode);

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches.RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(result.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RecoveryRequired));
        Assert.That(result.Transaction.FailureCode, Is.EqualTo($"installation_unavailable:{locatorCode ?? "unknown"}"));
        Assert.That(result.Message, Is.EqualTo(expectedMessage));
        if (locatorCode is not null) {
            Assert.That(result.Message, Does.Not.Contain(locatorCode));
        }
    }

    [TestCase("close-timeout", "rollback_close_timeout")]
    [TestCase("still-running", "rollback_process_still_running")]
    [TestCase("status-unknown", "process_status_unknown")]
    public async Task RecoverPendingSwitchAsync_DesktopCannotBeStopped_ShowsCloseDesktopMessage(
        string scenario, string expectedFailureCode
    ) {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        fixture.Repository.Transaction = CreatePendingRecoveryTransaction(fixture);
        switch (scenario) {
            case "close-timeout":
                fixture.Process.SetStatusSequence(IClaudeDesktopProcessController.ProcessStatus.Running);
                fixture.Process.CloseResult = false;
                break;
            case "still-running":
                fixture.Process.SetStatusSequence(
                    IClaudeDesktopProcessController.ProcessStatus.Running,
                    IClaudeDesktopProcessController.ProcessStatus.Running
                );
                break;
            default:
                fixture.Process.SetStatusSequence(IClaudeDesktopProcessController.ProcessStatus.Unknown);
                break;
        }

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches.RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.RecoveryRequired));
        Assert.That(result.Transaction!.FailureCode, Is.EqualTo(expectedFailureCode));
        Assert.That(result.Message, Is.EqualTo("復原未完成：Claude Desktop 仍在執行或無法確認已結束，請先正常結束 Claude Desktop 後按「重試復原」。"));
        Assert.That(result.Message, Does.Not.Contain(expectedFailureCode));
    }

    [Test]
    public async Task RecoverPendingSwitchAsync_AcceptsUpdatedPackageVersionForSameBoundFamily() {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        ClaudeSwitchTransaction pending = CreatePendingRecoveryTransaction(fixture);
        fixture.Repository.Transaction = pending;
        fixture.ProfileStore.SeedTransaction(pending.TransactionId, fixture.SnapshotA, fixture.SnapshotB);
        fixture.ProfileStore.CurrentSnapshot = fixture.SnapshotB;
        fixture.ProfileStore.CurrentCandidates = [new("token-b", "oauth:tokenCacheV2")];
        ClaudeDesktopRootBinding.Observation currentObservation = ClaudeTestData.Observation with {
            PackageFullName = "Claude.Package_2.0.0.0_x64__abcd1234wxyz1"
        };
        fixture.Locator.Result = new(
            IClaudeDesktopInstallationLocator.LocateStatus.Found,
            ClaudeTestData.Installation with { Observation = currentObservation, Binding = null },
            null
        );

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches
            .RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.FailedRolledBack));
            Assert.That(result.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RolledBack));
            Assert.That(fixture.ProfileStore.CurrentSnapshot!.LastKnownAccountUuid, Is.EqualTo("account-a"));
            Assert.That(fixture.Process.LaunchCallCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RecoverPendingSwitchAsync_SameInstallationRunning_ClosesBeforeResolvingAndRestores() {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        ClaudeSwitchTransaction pending = CreatePendingRecoveryTransaction(fixture);
        fixture.Repository.Transaction = pending;
        fixture.ProfileStore.SeedTransaction(pending.TransactionId, fixture.SnapshotA, fixture.SnapshotB);
        fixture.ProfileStore.CurrentSnapshot = fixture.SnapshotB;
        fixture.ProfileStore.CurrentCandidates = [new("token-b", "oauth:tokenCacheV2")];
        int closeCountAtResolve = -1;
        fixture.Locator.OnResolveBound = () => closeCountAtResolve = fixture.Process.CloseCallCount;

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches.RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(closeCountAtResolve, Is.EqualTo(1));
        Assert.That(result.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RolledBack));
    }

    [Test]
    public async Task RecoverPendingSwitchAsync_DifferentInstallationRunning_DoesNotCloseOrRestore() {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        fixture.Repository.Transaction = CreatePendingRecoveryTransaction(fixture);
        ClaudeDesktopRootBinding.Observation other = ClaudeTestData.Observation with { InstallationId = "other" };
        fixture.Locator.ObservationResult = new(
            IClaudeDesktopInstallationLocator.LocateStatus.Found,
            other,
            null,
            ClaudeTestData.Installation with { InstallationId = "other", Observation = other }
        );

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches.RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(fixture.Process.CloseCallCount, Is.Zero);
        Assert.That(fixture.ProfileStore.RestoreCallCount, Is.Zero);
        Assert.That(result.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RecoveryRequired));
        Assert.That(result.Transaction.FailureCode, Is.EqualTo("installation_unavailable:running-installation-mismatch"));
    }

    [Test]
    public async Task RecoverPendingSwitchAsync_RestoresSourceAndTerminatesJournal() {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        ClaudeSwitchTransaction pending = new(
            Guid.NewGuid(),
            fixture.ProfileA.ProfileId,
            fixture.ProfileB.ProfileId,
            fixture.ProfileA.ProfileRevision,
            fixture.ProfileB.ProfileRevision,
            ClaudeTestData.Installation.InstallationId,
            ClaudeSwitchTransaction.Stage.RecoveryRequired,
            ClaudeTestData.Now.AddMinutes(-2),
            ClaudeTestData.Now,
            "target_identity_unverified",
            ClaudeSwitchTransaction.Stage.TargetAuthApplyStarted,
            ProvenanceSchemaVersion: 1,
            RootBinding: ClaudeTestData.Binding
        );
        fixture.Repository.Transaction = pending;
        // 前一個程序已準備的逐檔交易證據保存在磁碟上，重啟後仍可用來回復來源帳號。
        fixture.ProfileStore.SeedTransaction(pending.TransactionId, fixture.SnapshotA, fixture.SnapshotB);
        fixture.ProfileStore.CurrentSnapshot = fixture.SnapshotB;
        fixture.ProfileStore.CurrentCandidates = [new("token-b", "oauth:tokenCacheV2")];

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches
            .RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.FailedRolledBack));
        Assert.That(result.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.RolledBack));
        Assert.That(fixture.ProfileStore.CurrentSnapshot!.LastKnownAccountUuid, Is.EqualTo("account-a"));
        Assert.That(fixture.Process.LaunchCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task RecoverPendingSwitchAsync_CommittedCleanupWhileRunning_CommitsMatchingInstallation() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        fixture.Repository.Transaction = CreateCommittedCleanupPendingTransaction(fixture);
        fixture.Locator.Result = new(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, null, "desktop-not-stopped");
        fixture.Locator.ObservationResult = new(IClaudeDesktopInstallationLocator.LocateStatus.Found,
            ClaudeTestData.Observation, null, ClaudeTestData.Installation with { Binding = null });

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches.RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.Succeeded));
        Assert.That(result.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.Committed));
    }

    [Test]
    public async Task RecoverPendingSwitchAsync_CommittedCleanupWhileRunning_RejectsDifferentInstallation() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        fixture.Repository.Transaction = CreateCommittedCleanupPendingTransaction(fixture);
        fixture.Locator.Result = new(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, null, "desktop-not-stopped");
        fixture.Locator.ObservationResult = new(IClaudeDesktopInstallationLocator.LocateStatus.Found,
            ClaudeTestData.Observation with { InstallationId = "different" }, null, ClaudeTestData.Installation);

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches.RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.CleanupPending));
        Assert.That(result.Message, Does.Contain("無法確認原 Claude Desktop 安裝"));
        Assert.That(result.Message, Does.Not.Contain("cleanup:"));
    }

    [Test]
    public async Task SwitchAsync_CommittedCleanupWhileRunning_CommitsAfterLaunch() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches.PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches.SwitchAsync(preview, CancellationToken.None);

        Assert.That(fixture.Process.Status, Is.EqualTo(IClaudeDesktopProcessController.ProcessStatus.Running));
        Assert.That(result.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.Committed));
    }

    [Test]
    public async Task PreviewSwitchAsync_RetriesCommittedCleanupBeforeAllowingNewSwitch() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        fixture.Repository.Transaction = CreateCommittedCleanupPendingTransaction(fixture);
        fixture.ProfileStore.CurrentSnapshot = fixture.SnapshotB;
        fixture.ProfileStore.CurrentCandidates = [new("token-b", "oauth:tokenCacheV2")];
        fixture.HistoryStore!.ThrowOnCommit = true;

        ClaudeDesktopSwitchService.SwitchPreview blockedPreview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileA.ProfileId, CancellationToken.None);

        Assert.That(blockedPreview.CanProceed, Is.False);
        Assert.That(blockedPreview.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.PreviewOutcome.CleanupPending));
        Assert.That(fixture.Repository.Transaction!.CurrentStage,
            Is.EqualTo(ClaudeSwitchTransaction.Stage.CommittedCleanupPending));
        Assert.That(fixture.ProfileStore.CurrentSnapshot, Is.SameAs(fixture.SnapshotB));
        Assert.That(fixture.Process.CloseCallCount, Is.Zero);

        fixture.HistoryStore.ThrowOnCommit = false;
        ClaudeDesktopSwitchService.SwitchPreview readyPreview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileA.ProfileId, CancellationToken.None);

        Assert.That(readyPreview.CanProceed, Is.True);
        Assert.That(fixture.Repository.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.Committed));
        Assert.That(fixture.HistoryStore.Operations.Count(operation => operation == "commit-history"), Is.EqualTo(2));
        Assert.That(
            fixture.HistoryStore.CommitIntents,
            Is.EqualTo(new[] {
                ClaudeSessionHistoryCleanupIntent.Committed,
                ClaudeSessionHistoryCleanupIntent.Committed
            })
        );
    }

    [Test]
    public async Task RecoverPendingSwitchAsync_RetriesCleanupWithoutRollingBackCommittedTarget() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        fixture.Repository.Transaction = CreateCommittedCleanupPendingTransaction(fixture);
        fixture.ProfileStore.CurrentSnapshot = fixture.SnapshotB;
        fixture.ProfileStore.CurrentCandidates = [new("token-b", "oauth:tokenCacheV2")];
        fixture.HistoryStore!.ThrowOnCommit = true;

        ClaudeDesktopSwitchService.SwitchResult pendingResult = await fixture.Switches
            .RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(pendingResult.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.CleanupPending));
        Assert.That(fixture.Repository.Transaction!.CurrentStage,
            Is.EqualTo(ClaudeSwitchTransaction.Stage.CommittedCleanupPending));
        Assert.That(fixture.ProfileStore.CurrentSnapshot, Is.SameAs(fixture.SnapshotB));
        Assert.That(fixture.ProfileStore.RestoreCallCount, Is.Zero);
        Assert.That(fixture.Process.CloseCallCount, Is.Zero);
        Assert.That(
            fixture.HistoryStore.CommitIntents,
            Is.EqualTo(new[] { ClaudeSessionHistoryCleanupIntent.Committed })
        );

        fixture.HistoryStore.ThrowOnCommit = false;
        ClaudeDesktopSwitchService.SwitchResult committedResult = await fixture.Switches
            .RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(committedResult.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.Succeeded));
        Assert.That(committedResult.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.Committed));
        Assert.That(fixture.ProfileStore.CurrentSnapshot, Is.SameAs(fixture.SnapshotB));
        Assert.That(fixture.ProfileStore.RestoreCallCount, Is.Zero);
        Assert.That(fixture.Process.CloseCallCount, Is.Zero);
        Assert.That(
            fixture.HistoryStore.CommitIntents,
            Is.EqualTo(new[] {
                ClaudeSessionHistoryCleanupIntent.Committed,
                ClaudeSessionHistoryCleanupIntent.Committed
            })
        );
    }

    [Test]
    public async Task RecoverPendingSwitchAsync_RetriesHistoryCleanupWhenTerminalJournalSaveFailed() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        fixture.Repository.Transaction = CreateCommittedCleanupPendingTransaction(fixture);
        fixture.Repository.ThrowOnCommittedTransactionSave = true;

        ClaudeDesktopSwitchService.SwitchResult firstResult = await fixture.Switches
            .RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(firstResult.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.CleanupPending));
        Assert.That(fixture.Repository.Transaction!.CurrentStage,
            Is.EqualTo(ClaudeSwitchTransaction.Stage.CommittedCleanupPending));
        Assert.That(fixture.HistoryStore!.CommitCallCount, Is.EqualTo(1));

        fixture.Repository.ThrowOnCommittedTransactionSave = false;
        ClaudeDesktopSwitchService.SwitchResult retryResult = await fixture.Switches
            .RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(retryResult.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.Succeeded));
        Assert.That(retryResult.Transaction!.CurrentStage, Is.EqualTo(ClaudeSwitchTransaction.Stage.Committed));
        Assert.That(fixture.HistoryStore.CommitCallCount, Is.EqualTo(2));
        Assert.That(
            fixture.HistoryStore.CommitIntents,
            Is.EqualTo(new[] {
                ClaudeSessionHistoryCleanupIntent.Committed,
                ClaudeSessionHistoryCleanupIntent.Committed
            })
        );
    }

    [Test]
    public async Task SwitchAsync_OverwritesHistoryAndPreservesSharedTranscriptForA10B20A20() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        ClaudeDesktopSwitchService.SwitchPreview toB = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);

        Assert.That(toB.RequiresHistoryOverwrite, Is.True);
        Assert.That(toB.SourceSessionCount, Is.EqualTo(1));
        Assert.That(toB.TargetSessionCount, Is.EqualTo(1));
        ClaudeDesktopSwitchService.SwitchResult firstResult = await fixture.Switches
            .SwitchAsync(toB, CancellationToken.None);

        Assert.That(firstResult.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.Succeeded));
        Assert.That(fixture.HistoryStore!.GetIndex("account-b", "org-b"), Is.EqualTo(new[] { "shared-session" }));
        Assert.That(fixture.HistoryStore.TranscriptTurns["shared-session"], Is.EqualTo(10));
        Assert.That(fixture.HistoryStore.TranscriptBytes["shared-session"], Is.EqualTo(fixture.TranscriptAtTenTurns));

        fixture.HistoryStore.ContinueTranscript("shared-session", 20);
        byte[] transcriptAtTwentyTurns = fixture.HistoryStore.TranscriptBytes["shared-session"].ToArray();
        ClaudeDesktopSwitchService.SwitchPreview toA = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileA.ProfileId, CancellationToken.None);
        ClaudeDesktopSwitchService.SwitchResult secondResult = await fixture.Switches
            .SwitchAsync(toA, CancellationToken.None);

        Assert.That(secondResult.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.Succeeded));
        Assert.That(fixture.HistoryStore.GetIndex("account-a", "org-a"), Is.EqualTo(new[] { "shared-session" }));
        Assert.That(fixture.HistoryStore.GetIndex("account-b", "org-b"), Is.EqualTo(new[] { "shared-session" }));
        Assert.That(fixture.HistoryStore.TranscriptTurns["shared-session"], Is.EqualTo(20));
        Assert.That(fixture.HistoryStore.TranscriptBytes["shared-session"], Is.EqualTo(transcriptAtTwentyTurns));

        fixture.HistoryStore.ContinueTranscript("shared-session", 21);
        Assert.That(fixture.HistoryStore.TranscriptTurns["shared-session"], Is.EqualTo(21));
        Assert.That(fixture.HistoryStore.GetIndex("account-a", "org-a"), Is.EqualTo(new[] { "shared-session" }));
        Assert.That(fixture.HistoryStore.GetIndex("account-b", "org-b"), Is.EqualTo(new[] { "shared-session" }));
        Assert.That(fixture.HistoryStore.TranscriptBytes["old-b-session"], Is.EqualTo(fixture.OldTargetTranscript));
    }

    [Test]
    public async Task RecoverPendingSwitchAsync_RollbackStepCode_RecordsBindingRevalidationFailure() {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        fixture.Repository.Transaction = CreatePendingRecoveryTransaction(fixture) with { FailureCode = "installation_unavailable" };
        int resolveCount = 0;
        fixture.Locator.OnResolveBound = () => {
            if (++resolveCount == 2) {
                fixture.Locator.Result = new(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, null, null);
            }
        };

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches.RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(result.Transaction!.FailureCode, Is.EqualTo("rollback:rollback_binding_revalidation_failed"));
        Assert.That(fixture.Repository.Transaction!.FailureCode, Is.EqualTo(result.Transaction.FailureCode));
        Assert.That(result.Message, Is.EqualTo("復原未完成，請稍後重試；仍失敗時請保留 Claude Desktop 資料。"));
        Assert.That(result.Message, Does.Not.Contain("rollback"));
    }

    [Test]
    public async Task RecoverPendingSwitchAsync_RollbackStepCode_RecordsExceptionTypeWithoutMessage() {
        SwitchFixture fixture = CreateFixture(includeHistory: true);
        fixture.Repository.Transaction = CreatePendingRecoveryTransaction(fixture) with {
            FailureCode = "installation_unavailable",
            RecoveryFromStage = ClaudeSwitchTransaction.Stage.HistoryPreparationStarted
        };
        fixture.HistoryStore!.ThrowOnCommit = true;

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches.RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(result.Transaction!.FailureCode, Is.EqualTo("rollback:IOException"));
        Assert.That(result.Transaction.FailureCode, Does.Not.Contain("Synthetic"));
        Assert.That(fixture.Repository.Transaction!.FailureCode, Is.EqualTo(result.Transaction.FailureCode));
        Assert.That(result.Message, Does.Not.Contain("IOException"));
        Assert.That(result.Message, Is.EqualTo("復原未完成，請稍後重試；仍失敗時請保留 Claude Desktop 資料。"));
    }

    [TestCase("source-revision", "rollback:source_profile_revision_changed")]
    [TestCase("target-revision", "rollback:target_profile_revision_changed")]
    [TestCase("source-binding", "rollback:source_profile_binding_changed")]
    [TestCase("target-binding", "rollback:target_profile_binding_changed")]
    public async Task RollbackProfileCheck_ChangedProfile_RecordsSpecificFailure(string change, string expected) {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        fixture.Repository.Transaction = CreatePendingRecoveryTransaction(fixture);
        bool source = change.StartsWith("source", StringComparison.Ordinal);
        int index = fixture.Repository.Profiles.FindIndex(profile => profile.ProfileId ==
            (source ? fixture.ProfileA.ProfileId : fixture.ProfileB.ProfileId));
        ClaudeAccountProfile profile = fixture.Repository.Profiles[index];
        fixture.Repository.Profiles[index] = change.EndsWith("revision", StringComparison.Ordinal)
            ? profile with { ProfileRevision = profile.ProfileRevision + 1 }
            : profile with { BindingId = Guid.NewGuid() };

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches.RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(result.Transaction!.FailureCode, Is.EqualTo(expected));
    }

    [Test]
    public async Task SwitchAsync_SourceCaptureRevision_PersistsCapturedVersion() {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        ClaudeDesktopSwitchService.SwitchPreview preview = await fixture.Switches
            .PreviewSwitchAsync(fixture.ProfileB.ProfileId, CancellationToken.None);

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches.SwitchAsync(preview, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.Succeeded));
        Assert.That(result.Transaction!.SourceProfileRevision, Is.EqualTo(fixture.ProfileA.ProfileRevision + 1));
        Assert.That(fixture.Repository.Profiles.Single(p => p.ProfileId == fixture.ProfileA.ProfileId).ProfileRevision,
            Is.EqualTo(result.Transaction.SourceProfileRevision));
    }

    [Test]
    public async Task RecoverPendingSwitchAsync_SourceCaptureRevision_UnchangedVersionRollsBack() {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        fixture.Repository.Transaction = CreatePendingRecoveryTransaction(fixture) with {
            RecoveryFromStage = ClaudeSwitchTransaction.Stage.DesktopClosed
        };

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches.RecoverPendingSwitchAsync(CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.FailedRolledBack));
    }

    [TestCase(ClaudeSwitchTransaction.Stage.DesktopClosed, 1, true)]
    [TestCase(ClaudeSwitchTransaction.Stage.DesktopClosed, 2, false)]
    [TestCase(ClaudeSwitchTransaction.Stage.SourceCaptured, 1, false)]
    [TestCase(ClaudeSwitchTransaction.Stage.TargetAuthApplyStarted, 1, false)]
    public async Task RecoverPendingSwitchAsync_SourceCaptureRevision_RestrictsRecovery(
        ClaudeSwitchTransaction.Stage recoveryStage, int increment, bool allowed
    ) {
        SwitchFixture fixture = CreateFixture(includeHistory: false);
        fixture.Repository.Transaction = CreatePendingRecoveryTransaction(fixture) with { RecoveryFromStage = recoveryStage };
        int index = fixture.Repository.Profiles.FindIndex(p => p.ProfileId == fixture.ProfileA.ProfileId);
        fixture.Repository.Profiles[index] = fixture.ProfileA with { ProfileRevision = fixture.ProfileA.ProfileRevision + increment };

        ClaudeDesktopSwitchService.SwitchResult result = await fixture.Switches.RecoverPendingSwitchAsync(CancellationToken.None);

        if (allowed) {
            Assert.That(result.Outcome, Is.EqualTo(ClaudeDesktopSwitchService.SwitchOutcome.FailedRolledBack));
        } else {
            Assert.That(result.Transaction!.FailureCode, Is.EqualTo("rollback:source_profile_revision_changed"));
        }
    }

    private static ClaudeSwitchTransaction CreatePendingRecoveryTransaction(SwitchFixture fixture) {
        return new(
            Guid.NewGuid(), fixture.ProfileA.ProfileId, fixture.ProfileB.ProfileId,
            fixture.ProfileA.ProfileRevision, fixture.ProfileB.ProfileRevision,
            ClaudeTestData.Installation.InstallationId, ClaudeSwitchTransaction.Stage.RecoveryRequired,
            ClaudeTestData.Now.AddMinutes(-2), ClaudeTestData.Now, "target_identity_unverified",
            ClaudeSwitchTransaction.Stage.TargetAuthApplyStarted, ProvenanceSchemaVersion: 1,
            RootBinding: ClaudeTestData.Binding
        );
    }

    private static SwitchFixture CreateFixture(bool includeHistory) {
        Guid profileAId = Guid.NewGuid();
        Guid profileBId = Guid.NewGuid();
        ClaudeAccountProfile profileA = ClaudeTestData.CreateProfile(profileAId, "account-a", "org-a");
        ClaudeAccountProfile profileB = ClaudeTestData.CreateProfile(profileBId, "account-b", "org-b");
        ClaudeAccountProfile.DesktopSnapshot snapshotA = ClaudeTestData.CreateSnapshot("account-a", "token-a");
        ClaudeAccountProfile.DesktopSnapshot snapshotB = ClaudeTestData.CreateSnapshot("account-b", "token-b");
        ClaudeTestAccountRepository repository = new(profileA, profileB);
        repository.AddSnapshot(profileAId, snapshotA);
        repository.AddSnapshot(profileBId, snapshotB);
        ClaudeTestInstallationLocator locator = new();
        ClaudeTestProfileStore profileStore = new() {
            CurrentSnapshot = snapshotA,
            CurrentCandidates = [new("token-a", "oauth:tokenCacheV2")]
        };
        profileStore.SetCandidates(snapshotA, "token-a");
        profileStore.SetCandidates(snapshotB, "token-b");
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["token-a"] = ClaudeTestData.VerifiedProfile("account-a", "org-a");
        oauth.ProfileResults["token-b"] = ClaudeTestData.VerifiedProfile("account-b", "org-b");
        ClaudeTestProcessController process = new();
        ClaudeTestHistoryStore? historyStore = null;
        ClaudeSessionHistorySyncService? historyService = null;
        byte[] transcriptAtTenTurns = "synthetic shared transcript at turn 10"u8.ToArray();
        byte[] oldTargetTranscript = "synthetic old target transcript"u8.ToArray();
        if (includeHistory) {
            historyStore = new();
            historyStore.SetIndex("account-a", "org-a", ["shared-session"]);
            historyStore.SetIndex("account-b", "org-b", ["old-b-session"]);
            historyStore.SetTranscript("shared-session", 10, transcriptAtTenTurns);
            historyStore.SetTranscript("old-b-session", 20, oldTargetTranscript);
            historyService = new(historyStore);
        }

        ClaudeDesktopSwitchService switchService = new(
            repository,
            locator,
            profileStore,
            process,
            oauth,
            new ClaudeTestFingerprinter(),
            historyService,
            ClaudeTestData.CreateOperationGate(),
            new ClaudeTestClock()
        );
        return new(
            profileA,
            profileB,
            snapshotA,
            snapshotB,
            repository,
            locator,
            profileStore,
            oauth,
            process,
            historyStore,
            switchService,
            transcriptAtTenTurns,
            oldTargetTranscript
        );
    }

    private static ClaudeAccountService CreateAccountService(SwitchFixture fixture) {
        return new(
            fixture.Repository,
            fixture.Locator,
            fixture.Process,
            fixture.ProfileStore,
            fixture.OAuth,
            new ClaudeTestFingerprinter(),
            ClaudeTestData.CreateOperationGate(),
            new ClaudeTestClock()
        );
    }

    private static ClaudeDesktopSwitchService CreateSwitchService(SwitchFixture fixture) {
        return new(
            fixture.Repository,
            fixture.Locator,
            fixture.ProfileStore,
            fixture.Process,
            fixture.OAuth,
            new ClaudeTestFingerprinter(),
            fixture.HistoryStore is null ? null : new ClaudeSessionHistorySyncService(fixture.HistoryStore),
            ClaudeTestData.CreateOperationGate(),
            new ClaudeTestClock()
        );
    }

    private static ClaudeSwitchTransaction CreateCommittedCleanupPendingTransaction(SwitchFixture fixture) {
        return new(
            Guid.NewGuid(),
            fixture.ProfileA.ProfileId,
            fixture.ProfileB.ProfileId,
            fixture.ProfileA.ProfileRevision,
            fixture.ProfileB.ProfileRevision,
            ClaudeTestData.Installation.InstallationId,
            ClaudeSwitchTransaction.Stage.CommittedCleanupPending,
            ClaudeTestData.Now.AddMinutes(-2),
            ClaudeTestData.Now,
            null,
            ProvenanceSchemaVersion: 1,
            RootBinding: ClaudeTestData.Binding
        );
    }

    private sealed record SwitchFixture(
        ClaudeAccountProfile ProfileA,
        ClaudeAccountProfile ProfileB,
        ClaudeAccountProfile.DesktopSnapshot SnapshotA,
        ClaudeAccountProfile.DesktopSnapshot SnapshotB,
        ClaudeTestAccountRepository Repository,
        ClaudeTestInstallationLocator Locator,
        ClaudeTestProfileStore ProfileStore,
        ClaudeTestOAuthClient OAuth,
        ClaudeTestProcessController Process,
        ClaudeTestHistoryStore? HistoryStore,
        ClaudeDesktopSwitchService Switches,
        byte[] TranscriptAtTenTurns,
        byte[] OldTargetTranscript
    );
}
