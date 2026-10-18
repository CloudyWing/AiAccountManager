using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Claude;

[TestFixture]
public sealed class ClaudeSessionHistorySyncServiceTests {
    [Test]
    public async Task PreviewReplacementAsync_AcceptsValidEmptySourceAndReportsTargetCount() {
        ClaudeTestHistoryStore store = new();
        string organization = "org-a";
        store.SetIndex("account-a", organization, []);
        store.SetIndex("account-b", organization, ["session-old-1", "session-old-2"]);
        store.SetTranscript("session-old-1", 3);
        store.SetTranscript("session-old-2", 5);
        ClaudeSessionHistorySyncService service = new(store);

        ClaudeSessionHistorySyncService.PreviewResult result = await service.PreviewReplacementAsync(
            ClaudeTestData.Installation,
            ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-a", organization),
            ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-b", organization),
            CancellationToken.None
        );

        Assert.That(result.CanProceed, Is.True);
        Assert.That(result.SourceSessionCount, Is.EqualTo(0));
        Assert.That(result.TargetSessionCount, Is.EqualTo(2));
        Assert.That(result.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
    }

    [TestCase(IClaudeSessionHistoryStore.ValidationStatus.MissingDirectory)]
    [TestCase(IClaudeSessionHistoryStore.ValidationStatus.InvalidTranscriptReference)]
    public async Task PreviewReplacementAsync_RejectsMissingOrInvalidSource(
        IClaudeSessionHistoryStore.ValidationStatus expectedStatus
    ) {
        ClaudeTestHistoryStore store = new();
        if (expectedStatus == IClaudeSessionHistoryStore.ValidationStatus.InvalidTranscriptReference) {
            store.SetIndex("account-a", "org-a", ["missing-transcript"]);
        }

        store.SetIndex("account-b", "org-a", []);
        ClaudeSessionHistorySyncService service = new(store);

        ClaudeSessionHistorySyncService.PreviewResult result = await service.PreviewReplacementAsync(
            ClaudeTestData.Installation,
            ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-a", "org-a"),
            ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-b", "org-a"),
            CancellationToken.None
        );

        Assert.That(result.CanProceed, Is.False);
        Assert.That(result.Status, Is.EqualTo(expectedStatus));
    }

    [Test]
    public async Task PrepareApplyAndReturn_PreservesSharedTranscriptAcrossA10B20A20() {
        Guid profileAId = Guid.NewGuid();
        Guid profileBId = Guid.NewGuid();
        ClaudeAccountProfile profileA = ClaudeTestData.CreateProfile(profileAId, "account-a", "org-a");
        ClaudeAccountProfile profileB = ClaudeTestData.CreateProfile(profileBId, "account-b", "org-b");
        ClaudeTestHistoryStore store = new();
        byte[] originalAtTenTurns = "synthetic A transcript at turn 10"u8.ToArray();
        byte[] oldTargetTranscript = "synthetic old B transcript"u8.ToArray();
        store.SetIndex(profileA.AccountUuid, profileA.OrganizationUuid, ["shared-session"]);
        store.SetIndex(profileB.AccountUuid, profileB.OrganizationUuid, ["old-b-session"]);
        store.SetTranscript("shared-session", 10, originalAtTenTurns);
        store.SetTranscript("old-b-session", 20, oldTargetTranscript);
        ClaudeSessionHistorySyncService service = new(store);

        ClaudeSessionHistorySyncService.PreviewResult firstPreview = await service.PreviewReplacementAsync(
            ClaudeTestData.Installation,
            profileA,
            profileB,
            CancellationToken.None
        );
        Guid firstTransaction = Guid.NewGuid();
        ClaudeSessionHistorySyncService.PreparationResult firstPreparation = await service.PrepareReplacementAsync(
            ClaudeTestData.Installation,
            profileA,
            profileB,
            firstTransaction,
            CancellationToken.None
        );
        Assert.That(firstPreview.CanProceed, Is.True);
        Assert.That(firstPreparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        await service.ApplyReplacementAsync(ClaudeTestData.Installation, profileB, firstTransaction, CancellationToken.None);

        Assert.That(store.GetIndex(profileB.AccountUuid, profileB.OrganizationUuid), Is.EqualTo(new[] { "shared-session" }));
        Assert.That(store.TranscriptTurns["shared-session"], Is.EqualTo(10));
        Assert.That(store.TranscriptBytes["shared-session"], Is.EqualTo(originalAtTenTurns));
        Assert.That(store.GetIndex(profileB.AccountUuid, profileB.OrganizationUuid), Does.Not.Contain("old-b-session"));
        await service.CommitAsync(
            ClaudeTestData.Installation,
            firstTransaction,
            ClaudeSessionHistoryCleanupIntent.Committed,
            CancellationToken.None
        );
        Assert.That(store.CommitIntents[^1], Is.EqualTo(ClaudeSessionHistoryCleanupIntent.Committed));

        store.ContinueTranscript("shared-session", 20);
        byte[] transcriptAtTwentyTurns = store.TranscriptBytes["shared-session"].ToArray();
        Guid secondTransaction = Guid.NewGuid();
        ClaudeSessionHistorySyncService.PreparationResult secondPreparation = await service.PrepareReplacementAsync(
            ClaudeTestData.Installation,
            profileB,
            profileA,
            secondTransaction,
            CancellationToken.None
        );
        Assert.That(secondPreparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        await service.ApplyReplacementAsync(ClaudeTestData.Installation, profileA, secondTransaction, CancellationToken.None);

        Assert.That(store.GetIndex(profileA.AccountUuid, profileA.OrganizationUuid), Is.EqualTo(new[] { "shared-session" }));
        Assert.That(store.TranscriptTurns["shared-session"], Is.EqualTo(20));
        Assert.That(store.TranscriptBytes["shared-session"], Is.EqualTo(transcriptAtTwentyTurns));
        await service.CommitAsync(
            ClaudeTestData.Installation,
            secondTransaction,
            ClaudeSessionHistoryCleanupIntent.Committed,
            CancellationToken.None
        );
        Assert.That(store.CommitIntents[^1], Is.EqualTo(ClaudeSessionHistoryCleanupIntent.Committed));

        store.ContinueTranscript("shared-session", 21);
        Assert.That(store.GetIndex(profileA.AccountUuid, profileA.OrganizationUuid), Is.EqualTo(new[] { "shared-session" }));
        Assert.That(store.GetIndex(profileB.AccountUuid, profileB.OrganizationUuid), Is.EqualTo(new[] { "shared-session" }));
        Assert.That(store.TranscriptTurns["shared-session"], Is.EqualTo(21));
        Assert.That(store.TranscriptBytes["old-b-session"], Is.EqualTo(oldTargetTranscript));
    }

    [Test]
    public async Task RestoreTargetAsync_RestoresOriginalIndexAfterPartialApply() {
        ClaudeAccountProfile source = ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-a", "org-a");
        ClaudeAccountProfile target = ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-b", "org-b");
        ClaudeTestHistoryStore store = new();
        store.SetIndex(source.AccountUuid, source.OrganizationUuid, ["source-session"]);
        store.SetIndex(target.AccountUuid, target.OrganizationUuid, ["target-session"]);
        store.SetTranscript("source-session", 10);
        store.SetTranscript("target-session", 20);
        ClaudeSessionHistorySyncService service = new(store);
        Guid transactionId = Guid.NewGuid();
        await service.PrepareReplacementAsync(
            ClaudeTestData.Installation,
            source,
            target,
            transactionId,
            CancellationToken.None
        );
        store.ThrowOnApplyAfterWrite = true;
        try {
            await service.ApplyReplacementAsync(ClaudeTestData.Installation, target, transactionId, CancellationToken.None);
        } catch (IOException) {
        }

        await service.RestoreTargetAsync(ClaudeTestData.Installation, target, transactionId, CancellationToken.None);

        Assert.That(store.GetIndex(target.AccountUuid, target.OrganizationUuid), Is.EqualTo(new[] { "target-session" }));
    }
}
