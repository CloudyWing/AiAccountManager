using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Windows.Claude;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Claude;

[TestFixture]
public sealed class WindowsClaudeSessionHistoryStoreTests {
    private static readonly Guid SourceAccount = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid SourceOrganization = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid TargetAccount = new("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly Guid TargetOrganization = new("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    private static readonly Guid TransactionId = new("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");
    private const string CliSessionId = "22222222-2222-4222-8222-222222222222";

    [TestCase(false)]
    [TestCase(true)]
    public async Task PreviewAndPrepare_TargetWithUnmatchedSessions_CountsMatch(bool legacyManifest) {
        using WindowsClaudeTestWorkspace workspace = new();
        (_, string target, _, _) = CreateHistoryInputs(workspace);
        for (int i = 0; i < 16; i++) {
            string path = Path.Combine(target, $"local_{Guid.NewGuid():D}.json");
            if (i < 13) {
                File.Copy(Fixture("session-index-source.json"), path);
            } else {
                File.WriteAllText(path, "{\"cliSessionId\":\"55555555-5555-4555-8555-555555555555\"}");
            }
        }
        Dictionary<string, byte[]> original = Directory.GetFiles(target)
            .ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes);
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);

        IClaudeSessionHistoryStore.ValidationResult preview = await store.ValidateAccountAsync(
            workspace.Installation, TargetAccount.ToString("D"), TargetOrganization.ToString("D"), CancellationToken.None
        );
        IClaudeSessionHistoryStore.PreparationResult preparation = await store.PrepareReplacementAsync(
            workspace.Installation, SourceAccount.ToString("D"), SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId, CancellationToken.None
        );

        Assert.That(preview.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(preparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(preview.SessionCount, Is.EqualTo(13));
        Assert.That(preparation.TargetSessionCount, Is.EqualTo(preview.SessionCount));
        await store.ApplyReplacementAsync(
            workspace.Installation, TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId, CancellationToken.None
        );
        if (legacyManifest) {
            string manifestPath = Path.Combine(GetTransactionPath(workspace.DataRootPath, TransactionId), "manifest.json");
            JsonNode manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            manifest["targetSessionCount"] = 16;
            File.WriteAllText(manifestPath, manifest.ToJsonString());
        }
        await store.RestoreTargetAsync(
            workspace.Installation, TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId, CancellationToken.None
        );
        Assert.That(Directory.GetFiles(target).Select(Path.GetFileName), Is.EquivalentTo(original.Keys));
        foreach (KeyValuePair<string, byte[]> file in original) {
            Assert.That(File.ReadAllBytes(Path.Combine(target, file.Key)), Is.EqualTo(file.Value));
        }
    }

    [Test]
    public async Task PrepareApplyRestore_FullMirror_ReplacesFilesAndPreservesDirectories() {
        using WindowsClaudeTestWorkspace workspace = new();
        string source = GetIndexPath(workspace.DataRootPath, SourceAccount, SourceOrganization);
        string target = GetIndexPath(workspace.DataRootPath, TargetAccount, TargetOrganization);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        string sourceSession = "local_11111111-1111-4111-8111-111111111111.json";
        string targetSession = "local_33333333-3333-4333-8333-333333333333.json";
        File.Copy(Fixture("session-index-source.json"), Path.Combine(source, sourceSession));
        File.Copy(Fixture("session-index-source.json"), Path.Combine(target, targetSession));
        CreateTranscript(workspace.UserProfilePath, CliSessionId, "project-a");
        foreach (string name in new[] { "scheduled-tasks.json", "archived-sessions.idx", "other.json" }) {
            File.WriteAllText(Path.Combine(source, name), "source " + name);
            File.WriteAllText(Path.Combine(target, name), "target " + name);
        }
        File.WriteAllText(Path.Combine(source, "deleted_source"), "source deleted");
        File.WriteAllText(Path.Combine(target, "deleted_target"), "target deleted");
        Directory.CreateDirectory(Path.Combine(source, "waiting-input"));
        Directory.CreateDirectory(Path.Combine(target, "waiting-input"));
        File.WriteAllText(Path.Combine(source, "waiting-input", "source.txt"), "source");
        File.WriteAllText(Path.Combine(target, "waiting-input", "target.txt"), "target");
        Dictionary<string, byte[]> original = Directory.GetFiles(target).ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes);
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);

        IClaudeSessionHistoryStore.ValidationResult sourceValidation = await store.ValidateAccountAsync(
            workspace.Installation, SourceAccount.ToString("D"), SourceOrganization.ToString("D"), CancellationToken.None
        );
        IClaudeSessionHistoryStore.ValidationResult targetValidation = await store.ValidateAccountAsync(
            workspace.Installation, TargetAccount.ToString("D"), TargetOrganization.ToString("D"), CancellationToken.None
        );
        IClaudeSessionHistoryStore.PreparationResult preparation = await store.PrepareReplacementAsync(
            workspace.Installation, SourceAccount.ToString("D"), SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId, CancellationToken.None
        );
        Assert.That(sourceValidation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(targetValidation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(sourceValidation.SessionCount, Is.EqualTo(1));
        Assert.That(targetValidation.SessionCount, Is.EqualTo(1));
        Assert.That(preparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        await store.ApplyReplacementAsync(workspace.Installation, TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId, CancellationToken.None);
        Assert.That(Directory.GetFiles(target).Select(Path.GetFileName), Is.EquivalentTo(Directory.GetFiles(source).Select(Path.GetFileName)));
        foreach (string file in Directory.GetFiles(source)) {
            Assert.That(File.ReadAllBytes(Path.Combine(target, Path.GetFileName(file))), Is.EqualTo(File.ReadAllBytes(file)));
        }
        Assert.That(File.Exists(Path.Combine(target, "waiting-input", "source.txt")), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(target, "waiting-input", "target.txt")), Is.EqualTo("target"));
        await store.RestoreTargetAsync(workspace.Installation, TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId, CancellationToken.None);
        Assert.That(Directory.GetFiles(target).Select(Path.GetFileName), Is.EquivalentTo(original.Keys));
        foreach (KeyValuePair<string, byte[]> file in original) {
            Assert.That(File.ReadAllBytes(Path.Combine(target, file.Key)), Is.EqualTo(file.Value));
        }
        await CommitHistoryAsync(store, workspace.Installation, ClaudeSessionHistoryCleanupIntent.Preparation);
    }

    [Test]
    public async Task PrepareReplacement_ExcludedSession_LeavesSourceAndOmitsInvalidFiles() {
        using WindowsClaudeTestWorkspace workspace = new();
        string source = GetIndexPath(workspace.DataRootPath, SourceAccount, SourceOrganization);
        string target = GetIndexPath(workspace.DataRootPath, TargetAccount, TargetOrganization);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        string valid1 = "local_11111111-1111-4111-8111-111111111111.json";
        string valid2 = "local_22222222-2222-4222-8222-222222222222.json";
        string missing = "local_33333333-3333-4333-8333-333333333333.json";
        string invalid = "local_44444444-4444-4444-8444-444444444444.json";
        File.Copy(Fixture("session-index-source.json"), Path.Combine(source, valid1));
        File.Copy(Fixture("session-index-source.json"), Path.Combine(source, valid2));
        File.WriteAllText(Path.Combine(source, missing), "{\"cliSessionId\":\"55555555-5555-4555-8555-555555555555\"}");
        File.WriteAllText(Path.Combine(source, invalid), "{}");
        File.WriteAllText(Path.Combine(source, "other.json"), "not json");
        CreateTranscript(workspace.UserProfilePath, CliSessionId, "project-a");
        byte[] before = File.ReadAllBytes(Path.Combine(source, missing));
        DateTime timestamp = File.GetLastWriteTimeUtc(Path.Combine(source, missing));
        byte[] invalidBefore = File.ReadAllBytes(Path.Combine(source, invalid));
        DateTime invalidTimestamp = File.GetLastWriteTimeUtc(Path.Combine(source, invalid));
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);

        IClaudeSessionHistoryStore.ValidationResult result = await store.ValidateAccountAsync(
            workspace.Installation, SourceAccount.ToString("D"), SourceOrganization.ToString("D"), CancellationToken.None
        );
        IClaudeSessionHistoryStore.PreparationResult preparation = await store.PrepareReplacementAsync(
            workspace.Installation, SourceAccount.ToString("D"), SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId, CancellationToken.None
        );
        Assert.That(result.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(result.SessionCount, Is.EqualTo(2));
        Assert.That(preparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(preparation.SourceSessionCount, Is.EqualTo(2));
        await store.ApplyReplacementAsync(workspace.Installation, TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId, CancellationToken.None);
        Assert.That(File.Exists(Path.Combine(target, missing)), Is.False);
        Assert.That(File.Exists(Path.Combine(target, invalid)), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(target, "other.json")), Is.EqualTo("not json"));
        Assert.That(File.ReadAllBytes(Path.Combine(source, missing)), Is.EqualTo(before));
        Assert.That(File.GetLastWriteTimeUtc(Path.Combine(source, missing)), Is.EqualTo(timestamp));
        Assert.That(File.ReadAllBytes(Path.Combine(source, invalid)), Is.EqualTo(invalidBefore));
        Assert.That(File.GetLastWriteTimeUtc(Path.Combine(source, invalid)), Is.EqualTo(invalidTimestamp));
    }

    [Test]
    public async Task ValidateAccount_TargetSessionWithoutTranscript_IsValid() {
        using WindowsClaudeTestWorkspace workspace = new();
        string source = GetIndexPath(workspace.DataRootPath, SourceAccount, SourceOrganization);
        string target = GetIndexPath(workspace.DataRootPath, TargetAccount, TargetOrganization);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.Copy(Fixture("session-index-source.json"), Path.Combine(target, "local_33333333-3333-4333-8333-333333333333.json"));
        Directory.CreateDirectory(Path.Combine(workspace.UserProfilePath, ".claude", "projects"));
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        ClaudeDesktopRootBinding binding = installation.Binding!;
        ClaudeAccountProfile sourceProfile = ClaudeTestData.CreateProfile(Guid.NewGuid(), SourceAccount.ToString("D"), SourceOrganization.ToString("D")) with {
            InstallationId = installation.InstallationId,
            DataRootFingerprint = installation.DataRootFingerprint,
            WindowsUserSid = binding.Source.WindowsUserSid,
            LocalStateEncryptedKeySha256 = binding.LocalStateEncryptedKeySha256,
            BindingId = binding.Source.BindingId,
            ProvenanceSchemaVersion = 1
        };
        ClaudeAccountProfile targetProfile = sourceProfile with {
            ProfileId = Guid.NewGuid(),
            AccountUuid = TargetAccount.ToString("D"),
            OrganizationUuid = TargetOrganization.ToString("D")
        };

        ClaudeSessionHistorySyncService.PreviewResult result = await new ClaudeSessionHistorySyncService(
            CreateStore(workspace.UserProfilePath)
        ).PreviewReplacementAsync(
            installation, sourceProfile, targetProfile, CancellationToken.None
        );

        Assert.That(result.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(result.CanProceed, Is.True);
        Assert.That(result.TargetSessionCount, Is.Zero);
    }

    [Test]
    public async Task PrepareApplyRestore_ScheduledTasks_ReplacesAndRestoresTarget() {
        using WindowsClaudeTestWorkspace workspace = new();
        (string source, string target, _, _) = CreateHistoryInputs(workspace);
        string sourceTasks = Path.Combine(source, "scheduled-tasks.json");
        string targetTasks = Path.Combine(target, "scheduled-tasks.json");
        File.WriteAllText(sourceTasks, "source tasks");
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);

        IClaudeSessionHistoryStore.ValidationResult validation = await store.ValidateAccountAsync(
            workspace.Installation, SourceAccount.ToString("D"), SourceOrganization.ToString("D"), CancellationToken.None
        );
        Assert.That(validation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(validation.SessionCount, Is.Zero);
        IClaudeSessionHistoryStore.PreparationResult preparation = await store.PrepareReplacementAsync(
            workspace.Installation, SourceAccount.ToString("D"), SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId, CancellationToken.None
        );
        Assert.That(preparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        await store.ApplyReplacementAsync(
            workspace.Installation, TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId,
            CancellationToken.None
        );
        Assert.That(File.ReadAllText(targetTasks), Is.EqualTo("source tasks"));
        await store.RestoreTargetAsync(
            workspace.Installation, TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId,
            CancellationToken.None
        );
        await CommitHistoryAsync(store, workspace.Installation, ClaudeSessionHistoryCleanupIntent.Preparation);

        File.WriteAllText(targetTasks, "target tasks");
        preparation = await store.PrepareReplacementAsync(
            workspace.Installation, SourceAccount.ToString("D"), SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId, CancellationToken.None
        );
        Assert.That(preparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        await store.ApplyReplacementAsync(
            workspace.Installation, TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId,
            CancellationToken.None
        );
        Assert.That(File.ReadAllText(targetTasks), Is.EqualTo("source tasks"));
        await store.RestoreTargetAsync(
            workspace.Installation, TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId,
            CancellationToken.None
        );
        Assert.That(File.ReadAllText(targetTasks), Is.EqualTo("target tasks"));
        await CommitHistoryAsync(store, workspace.Installation, ClaudeSessionHistoryCleanupIntent.Preparation);
    }

    [Test]
    public async Task ValidateAccount_ScheduledTasksOtherJsonWithoutSessionId_IsAccepted() {
        using WindowsClaudeTestWorkspace workspace = new();
        string source = GetIndexPath(workspace.DataRootPath, SourceAccount, SourceOrganization);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "other-tasks.json"), "{}");

        IClaudeSessionHistoryStore.ValidationResult result = await CreateStore(workspace.UserProfilePath).ValidateAccountAsync(
            workspace.Installation, SourceAccount.ToString("D"), SourceOrganization.ToString("D"), CancellationToken.None
        );

        Assert.That(result.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(result.SessionCount, Is.Zero);
    }

    [Test]
    public async Task PrepareApplyRestore_DesktopArtifacts_ReplacesFilesAndPreservesDirectories() {
        using WindowsClaudeTestWorkspace workspace = new();
        (string source, string target, string targetFile, byte[] originalTarget) = CreateHistoryInputs(workspace);
        string sourceDeleted = "deleted_11111111-1111-4111-8111-111111111111";
        string targetDeleted = "deleted_33333333-3333-4333-8333-333333333333";
        File.WriteAllText(Path.Combine(source, sourceDeleted), "source");
        Directory.CreateDirectory(Path.Combine(source, "waiting-input"));
        File.WriteAllText(Path.Combine(source, "waiting-input", "source.txt"), "source");
        File.WriteAllText(Path.Combine(target, targetDeleted), "target");
        Directory.CreateDirectory(Path.Combine(target, "waiting-input"));
        File.WriteAllText(Path.Combine(target, "waiting-input", "target.txt"), "target");
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);

        IClaudeSessionHistoryStore.ValidationResult validation = await store.ValidateAccountAsync(
            workspace.Installation, SourceAccount.ToString("D"), SourceOrganization.ToString("D"), CancellationToken.None
        );
        Assert.That(validation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(validation.SessionCount, Is.Zero);
        IClaudeSessionHistoryStore.PreparationResult preparation = await store.PrepareReplacementAsync(
            workspace.Installation, SourceAccount.ToString("D"), SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId, CancellationToken.None
        );
        Assert.That(preparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(preparation.SourceSessionCount, Is.Zero);
        Assert.That(preparation.TargetSessionCount, Is.Zero);
        await store.ApplyReplacementAsync(
            workspace.Installation, TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId,
            CancellationToken.None
        );

        Assert.That(File.ReadAllText(Path.Combine(target, sourceDeleted)), Is.EqualTo("source"));
        Assert.That(File.Exists(Path.Combine(target, "waiting-input", "source.txt")), Is.False);
        Assert.That(File.Exists(Path.Combine(target, targetDeleted)), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(target, "waiting-input", "target.txt")), Is.EqualTo("target"));
        await store.RestoreTargetAsync(
            workspace.Installation, TargetAccount.ToString("D"), TargetOrganization.ToString("D"), TransactionId,
            CancellationToken.None
        );
        Assert.That(File.ReadAllBytes(targetFile), Is.EqualTo(originalTarget));
        Assert.That(File.Exists(Path.Combine(target, "source-app-session.json")), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(target, targetDeleted)), Is.EqualTo("target"));
        Assert.That(File.ReadAllText(Path.Combine(target, "waiting-input", "target.txt")), Is.EqualTo("target"));
        await CommitHistoryAsync(store, workspace.Installation, ClaudeSessionHistoryCleanupIntent.Preparation);
    }

    [TestCase("notes.txt", false)]
    [TestCase("deleted_not-a-uuid", false)]
    [TestCase("other-dir", true)]
    public async Task ValidateAccount_DesktopArtifactsOtherEntries_AreAccepted(string name, bool isDirectory) {
        using WindowsClaudeTestWorkspace workspace = new();
        string source = GetIndexPath(workspace.DataRootPath, SourceAccount, SourceOrganization);
        Directory.CreateDirectory(source);
        if (isDirectory) {
            Directory.CreateDirectory(Path.Combine(source, name));
        } else {
            File.WriteAllText(Path.Combine(source, name), "invalid");
        }

        IClaudeSessionHistoryStore.ValidationResult result = await CreateStore(workspace.UserProfilePath).ValidateAccountAsync(
            workspace.Installation, SourceAccount.ToString("D"), SourceOrganization.ToString("D"), CancellationToken.None
        );

        Assert.That(result.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(result.SessionCount, Is.Zero);
    }

    [Test]
    public async Task PrepareApplyRestore_OverwritesOnlyTargetIndexAndPreservesSharedTranscript() {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);
        string sourceIndexPath = GetIndexPath(workspace.DataRootPath, SourceAccount, SourceOrganization);
        string targetIndexPath = GetIndexPath(workspace.DataRootPath, TargetAccount, TargetOrganization);
        Directory.CreateDirectory(sourceIndexPath);
        Directory.CreateDirectory(targetIndexPath);
        File.Copy(Fixture("session-index-source.json"), Path.Combine(sourceIndexPath, "source-app-session.json"));
        File.Copy(Fixture("session-index-target.json"), Path.Combine(targetIndexPath, "target-app-session.json"));

        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        List<string> renameTrace = [];
        store = CreateStore(
            workspace.UserProfilePath,
            cleanupObserver: (operation, path) => {
                if (operation.StartsWith("before-apply-", StringComparison.Ordinal)
                    || operation.StartsWith("after-apply-", StringComparison.Ordinal)) {
                    string liveFiles = string.Join(",", Directory.GetFiles(targetIndexPath).Select(Path.GetFileName));
                    string transactionFiles = Directory.Exists(transactionPath)
                        ? string.Join(",", Directory.GetFiles(transactionPath, "*", SearchOption.AllDirectories)
                            .Select(file => Path.GetRelativePath(transactionPath, file)))
                        : "<missing transaction>";
                    renameTrace.Add(
                        $"{operation} path={Path.GetRelativePath(workspace.DataRootPath, path)} "
                        + $"exists={File.Exists(path)} target=[{liveFiles}] transaction=[{transactionFiles}]"
                    );
                }
            }
        );

        string transcriptPath = CreateTranscript(workspace.UserProfilePath, CliSessionId, "project-a");
        byte[] originalSource = File.ReadAllBytes(Path.Combine(sourceIndexPath, "source-app-session.json"));
        byte[] originalTarget = File.ReadAllBytes(Path.Combine(targetIndexPath, "target-app-session.json"));
        byte[] originalTranscript = File.ReadAllBytes(transcriptPath);

        IClaudeSessionHistoryStore.ValidationResult validation = await store.ValidateAccountAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            CancellationToken.None
        );
        IClaudeSessionHistoryStore.PreparationResult preparation = await store.PrepareReplacementAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );

        Assert.That(validation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(validation.SessionCount, Is.Zero);
        Assert.That(preparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(preparation.SourceSessionCount, Is.Zero);
        Assert.That(preparation.TargetSessionCount, Is.Zero);
        Assert.That(File.ReadAllBytes(Path.Combine(targetIndexPath, "target-app-session.json")), Is.EqualTo(originalTarget));

        await store.ApplyReplacementAsync(
            installation,
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );

        string replacedEntry = Path.Combine(targetIndexPath, "source-app-session.json");
        Assert.That(
            Directory.GetFiles(targetIndexPath, "*.json"),
            Has.Length.EqualTo(1),
            string.Join(Environment.NewLine, renameTrace)
        );
        Assert.That(File.ReadAllBytes(replacedEntry), Is.EqualTo(originalSource));
        Assert.That(File.Exists(Path.Combine(targetIndexPath, "target-app-session.json")), Is.False);
        Assert.That(File.ReadAllBytes(Path.Combine(sourceIndexPath, "source-app-session.json")), Is.EqualTo(originalSource));
        Assert.That(File.ReadAllBytes(transcriptPath), Is.EqualTo(originalTranscript));
        Assert.That(File.Exists(Path.Combine(transactionPath, "live-files", "000001.displaced")), Is.True);

        try {
            await store.RestoreTargetAsync(
                installation,
                TargetAccount.ToString("D"),
                TargetOrganization.ToString("D"),
                TransactionId,
                CancellationToken.None
            );
        } catch (Exception failure) {
            string fileTransactionPath = Path.Combine(transactionPath, "live-files", "file-transaction.json");
            string transactionFiles = string.Join(
                Environment.NewLine,
                Directory.GetFiles(transactionPath, "*", SearchOption.AllDirectories)
                    .Select(file => $"{Path.GetRelativePath(transactionPath, file)} length={new FileInfo(file).Length}")
            );
            Assert.Fail(
                $"History restore failed: {failure}\n"
                + $"Apply rename trace:{Environment.NewLine}{string.Join(Environment.NewLine, renameTrace)}\n"
                + $"Transaction files:{Environment.NewLine}{transactionFiles}\n"
                + $"File transaction manifest:{Environment.NewLine}{File.ReadAllText(fileTransactionPath)}"
            );
        }
        await store.RestoreTargetAsync(
            installation,
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
        Assert.That(File.ReadAllBytes(Path.Combine(targetIndexPath, "target-app-session.json")), Is.EqualTo(originalTarget));

        await CommitHistoryAsync(store, installation, ClaudeSessionHistoryCleanupIntent.Preparation);
        Assert.That(Directory.Exists(GetTransactionPath(workspace.DataRootPath, TransactionId)), Is.False);
        Assert.That(File.ReadAllBytes(transcriptPath), Is.EqualTo(originalTranscript));
    }

    [TestCase("before-apply-displace", false)]
    [TestCase("after-apply-displace", false)]
    [TestCase("before-apply-publish", false)]
    [TestCase("after-apply-publish", false)]
    [TestCase("before-restore-displace", true)]
    [TestCase("after-restore-displace", true)]
    [TestCase("before-restore-publish", true)]
    [TestCase("after-restore-publish", true)]
    public async Task ApplyAndRestore_RestartAfterEveryRenameInterruptionRestoresOriginalState(
        string interruption,
        bool interruptDuringRestore
    ) {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        (string sourceIndexPath, string targetIndexPath, string targetFilePath, byte[] targetContent) = CreateHistoryInputs(workspace);
        string sourceFilePath = Path.Combine(sourceIndexPath, "source-app-session.json");
        byte[] sourceContent = File.ReadAllBytes(sourceFilePath);
        string transcriptPath = GetTranscriptPath(workspace.UserProfilePath, CliSessionId, "project-a");
        byte[] transcriptContent = File.ReadAllBytes(transcriptPath);
        await CreateStore(workspace.UserProfilePath).PrepareReplacementAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
        WindowsClaudeSessionHistoryStore interrupted = CreateStore(
            workspace.UserProfilePath,
            cleanupObserver: (operation, _) => {
                if (operation == interruption) {
                    throw new IOException("Synthetic interruption at a durable non-replace rename boundary.");
                }
            }
        );

        if (interruptDuringRestore) {
            await CreateStore(workspace.UserProfilePath).ApplyReplacementAsync(
                installation,
                TargetAccount.ToString("D"),
                TargetOrganization.ToString("D"),
                TransactionId,
                CancellationToken.None
            );
            await Assert.CatchAsync<IOException>(() => interrupted.RestoreTargetAsync(
                installation,
                TargetAccount.ToString("D"),
                TargetOrganization.ToString("D"),
                TransactionId,
                CancellationToken.None
            ));
        } else {
            await Assert.CatchAsync<IOException>(() => interrupted.ApplyReplacementAsync(
                installation,
                TargetAccount.ToString("D"),
                TargetOrganization.ToString("D"),
                TransactionId,
                CancellationToken.None
            ));
        }

        WindowsClaudeSessionHistoryStore restarted = CreateStore(workspace.UserProfilePath);
        await restarted.RestoreTargetAsync(
            installation,
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );

        Assert.That(Directory.GetFiles(targetIndexPath).Select(Path.GetFileName), Is.EquivalentTo(new[] { "target-app-session.json" }));
        Assert.That(File.ReadAllBytes(targetFilePath), Is.EqualTo(targetContent));
        Assert.That(File.ReadAllBytes(sourceFilePath), Is.EqualTo(sourceContent));
        Assert.That(File.ReadAllBytes(transcriptPath), Is.EqualTo(transcriptContent));
        await CommitHistoryAsync(restarted, installation, ClaudeSessionHistoryCleanupIntent.Preparation);
        Assert.That(Directory.Exists(GetTransactionPath(workspace.DataRootPath, TransactionId)), Is.False);
    }

    [Test]
    public async Task CommitAsync_CommittedCleanupWhileRunning_AllowsChangedLiveTarget() {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        _ = CreateHistoryInputs(workspace);
        string targetIndexPath = GetIndexPath(workspace.DataRootPath, TargetAccount, TargetOrganization);
        HistoryTestProcessController controller = new();
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath, controller);
        await PrepareAndApply(store, installation);
        controller.Status = IClaudeDesktopProcessController.ProcessStatus.Running;
        string appliedFilePath = Path.Combine(targetIndexPath, "source-app-session.json");
        byte[] externalContent = [101, 102, 103];
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        File.WriteAllBytes(appliedFilePath, externalContent);

        try {
            await CommitHistoryAsync(store, installation, ClaudeSessionHistoryCleanupIntent.Committed);

            Assert.That(File.ReadAllBytes(appliedFilePath), Is.EqualTo(externalContent));
            Assert.That(Directory.Exists(transactionPath), Is.False);
        } finally {
            CryptographicOperations.ZeroMemory(externalContent);
        }
    }

    [Test]
    public async Task CommitAsync_RetriesAfterPartialCleanupAndRetainsManifestUntilBackupsAreGone() {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);
        string sourceIndexPath = GetIndexPath(workspace.DataRootPath, SourceAccount, SourceOrganization);
        string targetIndexPath = GetIndexPath(workspace.DataRootPath, TargetAccount, TargetOrganization);
        Directory.CreateDirectory(sourceIndexPath);
        Directory.CreateDirectory(targetIndexPath);
        File.Copy(Fixture("session-index-source.json"), Path.Combine(sourceIndexPath, "source-app-session.json"));
        File.Copy(Fixture("session-index-target.json"), Path.Combine(targetIndexPath, "target-app-session.json"));
        CreateTranscript(workspace.UserProfilePath, CliSessionId, "project-a");
        await PrepareAndApply(store, installation);

        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string manifestPath = Path.Combine(transactionPath, "manifest.json");
        string backupPath = Path.Combine(transactionPath, "target-backup");
        string displacedPath = Path.Combine(transactionPath, "target-displaced");
        WindowsClaudeSessionHistoryStore interruptedStore = new(
            workspace.UserProfilePath,
            new HistoryTestProcessController(),
            (operation, path) => {
                if (operation == "after-payload-delete"
                    && path.Contains(Path.Combine("target-backup", "target-app-session.json"), StringComparison.OrdinalIgnoreCase)) {
                    throw new IOException("Synthetic cleanup interruption.");
                }
            }
        );

        await Assert.ThrowsAsync<IOException>(async () => await CommitHistoryAsync(
            interruptedStore,
            installation,
            ClaudeSessionHistoryCleanupIntent.Committed
        ));

        Assert.That(File.Exists(manifestPath), Is.True);
        Assert.That(File.Exists(Path.Combine(transactionPath, "cleanup-started")), Is.True);
        Assert.That(Directory.Exists(backupPath), Is.True);
        Assert.That(File.Exists(Path.Combine(backupPath, "target-app-session.json")), Is.False);
        Assert.That(Directory.Exists(displacedPath), Is.False);
        Assert.That(File.Exists(Path.Combine(targetIndexPath, "source-app-session.json")), Is.True);

        await CommitHistoryAsync(store, installation, ClaudeSessionHistoryCleanupIntent.Committed);
        await CommitHistoryAsync(store, installation, ClaudeSessionHistoryCleanupIntent.Committed);

        Assert.That(Directory.Exists(transactionPath), Is.False);
        Assert.That(File.Exists(Path.Combine(targetIndexPath, "source-app-session.json")), Is.True);
    }

    [Test]
    public async Task CommitAsync_CleansPreparedTransactionWithoutChangingTarget() {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);
        string sourceIndexPath = GetIndexPath(workspace.DataRootPath, SourceAccount, SourceOrganization);
        string targetIndexPath = GetIndexPath(workspace.DataRootPath, TargetAccount, TargetOrganization);
        Directory.CreateDirectory(sourceIndexPath);
        Directory.CreateDirectory(targetIndexPath);
        File.Copy(Fixture("session-index-source.json"), Path.Combine(sourceIndexPath, "source-app-session.json"));
        File.Copy(Fixture("session-index-target.json"), Path.Combine(targetIndexPath, "target-app-session.json"));
        CreateTranscript(workspace.UserProfilePath, CliSessionId, "project-a");
        byte[] originalTarget = File.ReadAllBytes(Path.Combine(targetIndexPath, "target-app-session.json"));

        IClaudeSessionHistoryStore.PreparationResult preparation = await store.PrepareReplacementAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        Assert.That(preparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(Directory.Exists(Path.Combine(transactionPath, "source-staging")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(transactionPath, "target-backup")), Is.True);

        await CommitHistoryAsync(store, installation, ClaudeSessionHistoryCleanupIntent.Preparation);

        Assert.That(Directory.Exists(transactionPath), Is.False);
        Assert.That(File.ReadAllBytes(Path.Combine(targetIndexPath, "target-app-session.json")), Is.EqualTo(originalTarget));
    }

    [Test]
    public async Task PrepareReplacement_CancellationAfterFirstPayload_NewStoreCleansIntentAndPreservesTarget() {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        (string sourceIndexPath, string targetIndexPath, string targetFilePath, byte[] targetContent) =
            CreateHistoryInputs(workspace);
        byte[] targetHash = SHA256.HashData(targetContent);
        using CancellationTokenSource cancellation = new();
        WindowsClaudeSessionHistoryStore store = new(
            workspace.UserProfilePath,
            new HistoryTestProcessController(),
            null,
            cancellation.Cancel
        );

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await store.PrepareReplacementAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            cancellation.Token
        ));

        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string stagingPath = Path.Combine(transactionPath, "source-staging");
        string manifestPath = Path.Combine(transactionPath, "manifest.json");
        Assert.That(File.Exists(manifestPath), Is.True);
        Assert.That(File.ReadAllText(manifestPath), Does.Contain("\"preparationState\": \"preparing\""));
        Assert.That(File.Exists(Path.Combine(stagingPath, "source-app-session.json")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(transactionPath, "target-backup.partial")), Is.False);
        Assert.That(Directory.Exists(Path.Combine(transactionPath, "target-backup")), Is.False);
        Assert.That(SHA256.HashData(File.ReadAllBytes(targetFilePath)), Is.EqualTo(targetHash));

        WindowsClaudeSessionHistoryStore restartedStore = CreateStore(workspace.UserProfilePath);
        await CommitHistoryAsync(restartedStore, installation, ClaudeSessionHistoryCleanupIntent.Preparation);
        await CommitHistoryAsync(restartedStore, installation, ClaudeSessionHistoryCleanupIntent.Preparation);

        Assert.That(Directory.Exists(transactionPath), Is.False);
        Assert.That(Directory.Exists(targetIndexPath), Is.True);
        Assert.That(SHA256.HashData(File.ReadAllBytes(targetFilePath)), Is.EqualTo(targetHash));
        Assert.That(Directory.Exists(sourceIndexPath), Is.True);
    }

    [Test]
    public async Task PrepareReplacement_CancellationBeforeIntentPublication_CleansOnlyIntentTemporaryAndPreservesTarget() {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string intentTemporaryPath = Path.Combine(
            transactionPath,
            ".manifest.json." + Guid.NewGuid().ToString("N") + ".tmp"
        );
        byte[] targetHash = SHA256.HashData(history.TargetContent);
        using CancellationTokenSource cancellation = new();
        WindowsClaudeSessionHistoryStore store = new(
            workspace.UserProfilePath,
            new HistoryTestProcessController(),
            null,
            null,
            () => {
                File.WriteAllText(intentTemporaryPath, "partial intent");
                cancellation.Cancel();
            }
        );

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await store.PrepareReplacementAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            cancellation.Token
        ));

        Assert.That(File.Exists(Path.Combine(transactionPath, "manifest.json")), Is.False);
        Assert.That(File.Exists(intentTemporaryPath), Is.True);
        Assert.That(Directory.Exists(Path.Combine(transactionPath, "source-staging")), Is.False);
        Assert.That(Directory.Exists(Path.Combine(transactionPath, "target-backup")), Is.False);
        Assert.That(Directory.Exists(Path.Combine(transactionPath, "target-backup.partial")), Is.False);
        Assert.That(Directory.GetFileSystemEntries(transactionPath), Is.EqualTo(new[] { intentTemporaryPath }));
        Assert.That(SHA256.HashData(File.ReadAllBytes(history.TargetFilePath)), Is.EqualTo(targetHash));

        WindowsClaudeSessionHistoryStore restartedStore = CreateStore(workspace.UserProfilePath);
        await CommitHistoryAsync(restartedStore, installation, ClaudeSessionHistoryCleanupIntent.Preparation);

        Assert.That(Directory.Exists(transactionPath), Is.False);
        Assert.That(Directory.Exists(history.TargetIndexPath), Is.True);
        Assert.That(SHA256.HashData(File.ReadAllBytes(history.TargetFilePath)), Is.EqualTo(targetHash));
        Assert.That(File.Exists(Path.Combine(history.SourceIndexPath, "source-app-session.json")), Is.True);
    }

    [Test]
    public async Task CommitAsync_RetriesPartialPreparationCleanupAfterRestart() {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        string targetIndexPath = history.TargetIndexPath;
        string targetFilePath = history.TargetFilePath;
        byte[] targetContent = history.TargetContent;
        byte[] targetHash = SHA256.HashData(targetContent);
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);
        IClaudeSessionHistoryStore.PreparationResult preparation = await store.PrepareReplacementAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
        Assert.That(preparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));

        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string manifestPath = Path.Combine(transactionPath, "manifest.json");
        string stagingPath = Path.Combine(transactionPath, "source-staging");
        string backupPath = Path.Combine(transactionPath, "target-backup");
        WindowsClaudeSessionHistoryStore interruptedStore = new(
            workspace.UserProfilePath,
            new HistoryTestProcessController(),
            (operation, path) => {
                if (operation == "after-payload-delete"
                    && path.Contains(Path.Combine("source-staging", "source-app-session.json"), StringComparison.OrdinalIgnoreCase)) {
                    throw new IOException("Synthetic preparation cleanup interruption.");
                }
            }
        );

        await Assert.ThrowsAsync<IOException>(async () => await CommitHistoryAsync(
            interruptedStore,
            installation,
            ClaudeSessionHistoryCleanupIntent.Preparation
        ));

        Assert.That(File.Exists(manifestPath), Is.True);
        Assert.That(File.Exists(Path.Combine(transactionPath, "cleanup-started")), Is.True);
        Assert.That(Directory.Exists(stagingPath), Is.True);
        Assert.That(File.Exists(Path.Combine(stagingPath, "source-app-session.json")), Is.False);
        Assert.That(Directory.Exists(backupPath), Is.True);
        Assert.That(SHA256.HashData(File.ReadAllBytes(targetFilePath)), Is.EqualTo(targetHash));

        WindowsClaudeSessionHistoryStore restartedStore = CreateStore(workspace.UserProfilePath);
        await CommitHistoryAsync(restartedStore, installation, ClaudeSessionHistoryCleanupIntent.Preparation);

        Assert.That(Directory.Exists(transactionPath), Is.False);
        Assert.That(Directory.Exists(targetIndexPath), Is.True);
        Assert.That(SHA256.HashData(File.ReadAllBytes(targetFilePath)), Is.EqualTo(targetHash));
    }

    [Test]
    public async Task CommitAsync_RetriesAfterFailureAtEveryCommittedCleanupDeletePoint() {
        List<(string Operation, string RelativePath)> deletePoints = [];
        using (WindowsClaudeTestWorkspace workspace = new()) {
            (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
                CreateHistoryInputs(workspace);
            string transcriptPath = GetTranscriptPath(workspace.UserProfilePath, CliSessionId, "project-a");
            byte[] sourceContent = File.ReadAllBytes(Path.Combine(history.SourceIndexPath, "source-app-session.json"));
            byte[] transcriptContent = File.ReadAllBytes(transcriptPath);
            WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);
            await PrepareAndApply(store, workspace.Installation);
            string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
            WindowsClaudeSessionHistoryStore observingStore = CreateStore(
                workspace.UserProfilePath,
                cleanupObserver: (operation, path) => {
                    if (operation.StartsWith("after-", StringComparison.Ordinal)) {
                        deletePoints.Add((operation, Path.GetRelativePath(transactionPath, path)));
                    }
                }
            );

            await CommitHistoryAsync(
                observingStore,
                workspace.Installation,
                ClaudeSessionHistoryCleanupIntent.Committed
            );

            Assert.That(deletePoints, Is.Not.Empty);
            Assert.That(deletePoints.Any(point => point.Operation == "after-manifest-delete"), Is.True);
            Assert.That(deletePoints.Any(point => point.Operation == "after-transaction-directory-delete"), Is.True);
        }

        for (int failureIndex = 0; failureIndex < deletePoints.Count; failureIndex++) {
            using WindowsClaudeTestWorkspace workspace = new();
            (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
                CreateHistoryInputs(workspace);
            string transcriptPath = GetTranscriptPath(workspace.UserProfilePath, CliSessionId, "project-a");
            byte[] sourceContent = File.ReadAllBytes(Path.Combine(history.SourceIndexPath, "source-app-session.json"));
            byte[] transcriptContent = File.ReadAllBytes(transcriptPath);
            string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
            await PrepareAndApply(CreateStore(workspace.UserProfilePath), workspace.Installation);
            (string Operation, string RelativePath) failurePoint = deletePoints[failureIndex];
            bool injected = false;
            WindowsClaudeSessionHistoryStore interruptedStore = CreateStore(
                workspace.UserProfilePath,
                cleanupObserver: (operation, path) => {
                    if (!injected
                        && operation == failurePoint.Operation
                        && Path.GetRelativePath(transactionPath, path).Equals(
                            failurePoint.RelativePath,
                            StringComparison.OrdinalIgnoreCase
                        )) {
                        injected = true;
                        throw new IOException("Synthetic cleanup interruption.");
                    }
                }
            );

            await Assert.ThrowsAsync<IOException>(async () => await CommitHistoryAsync(
                interruptedStore,
                workspace.Installation,
                ClaudeSessionHistoryCleanupIntent.Committed
            ));

            Assert.That(injected, Is.True, failurePoint.Operation + ":" + failurePoint.RelativePath);
            Assert.That(Directory.Exists(history.TargetIndexPath), Is.True);
            Assert.That(File.Exists(Path.Combine(history.TargetIndexPath, "source-app-session.json")), Is.True);
            Assert.That(File.Exists(history.TargetFilePath), Is.False);
            Assert.That(File.ReadAllBytes(Path.Combine(history.SourceIndexPath, "source-app-session.json")), Is.EqualTo(sourceContent));
            Assert.That(File.ReadAllBytes(transcriptPath), Is.EqualTo(transcriptContent));

            WindowsClaudeSessionHistoryStore restartedStore = CreateStore(workspace.UserProfilePath);
            await CommitHistoryAsync(
                restartedStore,
                workspace.Installation,
                ClaudeSessionHistoryCleanupIntent.Committed
            );

            Assert.That(Directory.Exists(transactionPath), Is.False);
            Assert.That(File.Exists(Path.Combine(history.TargetIndexPath, "source-app-session.json")), Is.True);
            Assert.That(File.ReadAllBytes(transcriptPath), Is.EqualTo(transcriptContent));
        }
    }

    [Test]
    public async Task CommitAsync_RetriesAfterFailureAtEveryPreparationCleanupDeletePoint() {
        List<(string Operation, string RelativePath)> deletePoints = [];
        using (WindowsClaudeTestWorkspace workspace = new()) {
            (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
                CreateHistoryInputs(workspace);
            string transcriptPath = GetTranscriptPath(workspace.UserProfilePath, CliSessionId, "project-a");
            byte[] transcriptContent = File.ReadAllBytes(transcriptPath);
            await CreateStore(workspace.UserProfilePath).PrepareReplacementAsync(
                workspace.Installation,
                SourceAccount.ToString("D"),
                SourceOrganization.ToString("D"),
                TargetAccount.ToString("D"),
                TargetOrganization.ToString("D"),
                TransactionId,
                CancellationToken.None
            );
            string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
            WindowsClaudeSessionHistoryStore observingStore = CreateStore(
                workspace.UserProfilePath,
                cleanupObserver: (operation, path) => {
                    if (operation.StartsWith("after-", StringComparison.Ordinal)) {
                        deletePoints.Add((operation, Path.GetRelativePath(transactionPath, path)));
                    }
                }
            );

            await CommitHistoryAsync(
                observingStore,
                workspace.Installation,
                ClaudeSessionHistoryCleanupIntent.Preparation
            );

            Assert.That(deletePoints, Is.Not.Empty);
            Assert.That(deletePoints.Any(point => point.Operation == "after-cleanup-marker-delete"), Is.True);
            Assert.That(deletePoints.Any(point => point.Operation == "after-manifest-delete"), Is.True);
            Assert.That(deletePoints.Any(point => point.Operation == "after-transaction-directory-delete"), Is.True);
        }

        for (int failureIndex = 0; failureIndex < deletePoints.Count; failureIndex++) {
            using WindowsClaudeTestWorkspace workspace = new();
            (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
                CreateHistoryInputs(workspace);
            string transcriptPath = GetTranscriptPath(workspace.UserProfilePath, CliSessionId, "project-a");
            byte[] transcriptContent = File.ReadAllBytes(transcriptPath);
            string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
            await CreateStore(workspace.UserProfilePath).PrepareReplacementAsync(
                workspace.Installation,
                SourceAccount.ToString("D"),
                SourceOrganization.ToString("D"),
                TargetAccount.ToString("D"),
                TargetOrganization.ToString("D"),
                TransactionId,
                CancellationToken.None
            );
            (string Operation, string RelativePath) failurePoint = deletePoints[failureIndex];
            bool injected = false;
            WindowsClaudeSessionHistoryStore interruptedStore = CreateStore(
                workspace.UserProfilePath,
                cleanupObserver: (operation, path) => {
                    if (!injected
                        && operation == failurePoint.Operation
                        && Path.GetRelativePath(transactionPath, path).Equals(
                            failurePoint.RelativePath,
                            StringComparison.OrdinalIgnoreCase
                        )) {
                        injected = true;
                        throw new IOException("Synthetic preparation cleanup interruption.");
                    }
                }
            );

            await Assert.ThrowsAsync<IOException>(async () => await CommitHistoryAsync(
                interruptedStore,
                workspace.Installation,
                ClaudeSessionHistoryCleanupIntent.Preparation
            ));

            Assert.That(injected, Is.True, failurePoint.Operation + ":" + failurePoint.RelativePath);
            Assert.That(File.Exists(history.TargetFilePath), Is.True);
            Assert.That(File.ReadAllBytes(history.TargetFilePath), Is.EqualTo(history.TargetContent));
            Assert.That(File.ReadAllBytes(transcriptPath), Is.EqualTo(transcriptContent));

            await CommitHistoryAsync(
                CreateStore(workspace.UserProfilePath),
                workspace.Installation,
                ClaudeSessionHistoryCleanupIntent.Preparation
            );

            Assert.That(Directory.Exists(transactionPath), Is.False);
            Assert.That(File.ReadAllBytes(history.TargetFilePath), Is.EqualTo(history.TargetContent));
            Assert.That(File.ReadAllBytes(transcriptPath), Is.EqualTo(transcriptContent));
        }
    }

    [Test]
    public async Task CommitAsync_CommittedCleanupWhileRunning_AllowsTargetAppend() {
        using WindowsClaudeTestWorkspace workspace = new();
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        await PrepareAndApply(CreateStore(workspace.UserProfilePath), workspace.Installation);
        string appliedTargetPath = Path.Combine(history.TargetIndexPath, "source-app-session.json");
        byte[] targetBeforeAppend = File.ReadAllBytes(appliedTargetPath);
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        byte[] targetAfterAppend;
        try {
            File.AppendAllText(appliedTargetPath, " ");
            targetAfterAppend = File.ReadAllBytes(appliedTargetPath);
            Assert.That(targetAfterAppend, Is.Not.EqualTo(targetBeforeAppend));

            await CommitHistoryAsync(
                CreateStore(workspace.UserProfilePath),
                workspace.Installation,
                ClaudeSessionHistoryCleanupIntent.Committed
            );

            Assert.That(Directory.Exists(transactionPath), Is.False);
            Assert.That(File.ReadAllBytes(appliedTargetPath), Is.EqualTo(targetAfterAppend));
            CryptographicOperations.ZeroMemory(targetAfterAppend);
        } finally {
            CryptographicOperations.ZeroMemory(targetBeforeAppend);
        }
    }

    [Test]
    public async Task CommitAsync_CommittedCleanupWhileRunning_AllowsMutationDuringCleanup() {
        using WindowsClaudeTestWorkspace workspace = new();
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        await PrepareAndApply(CreateStore(workspace.UserProfilePath), workspace.Installation);

        string appliedTargetPath = Path.Combine(history.TargetIndexPath, "source-app-session.json");
        byte[] committedContent = File.ReadAllBytes(appliedTargetPath);
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string targetBackupPath = Path.Combine(transactionPath, "target-backup", "target-app-session.json");
        bool injected = false;
        byte[]? externalContent = null;
        WindowsClaudeSessionHistoryStore observingStore = CreateStore(
            workspace.UserProfilePath,
            cleanupObserver: (operation, path) => {
                if (!injected
                    && operation == "after-payload-delete"
                    && string.Equals(path, targetBackupPath, StringComparison.OrdinalIgnoreCase)) {
                    injected = true;
                    File.AppendAllText(appliedTargetPath, "external mutation");
                    externalContent = File.ReadAllBytes(appliedTargetPath);
                }
            }
        );

        try {
            await CommitHistoryAsync(observingStore, workspace.Installation, ClaudeSessionHistoryCleanupIntent.Committed);

            Assert.That(injected, Is.True);
            Assert.That(Directory.Exists(transactionPath), Is.False);
            Assert.That(File.ReadAllBytes(appliedTargetPath), Is.EqualTo(externalContent));
        } finally {
            CryptographicOperations.ZeroMemory(committedContent);
            if (externalContent is not null) {
                CryptographicOperations.ZeroMemory(externalContent);
            }
        }
    }

    [Test]
    public async Task CommitAsync_CommittedCleanupWhileRunning_PreparationRejectsChangedTarget() {
        using WindowsClaudeTestWorkspace workspace = new();
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        await PrepareAndApply(CreateStore(workspace.UserProfilePath), workspace.Installation);
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string manifestPath = Path.Combine(transactionPath, "manifest.json");
        string appliedTargetPath = Path.Combine(history.TargetIndexPath, "source-app-session.json");
        byte[] appliedTarget = File.ReadAllBytes(appliedTargetPath);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await CommitHistoryAsync(
            CreateStore(workspace.UserProfilePath),
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Preparation
        ));

        Assert.That(Directory.Exists(transactionPath), Is.True);
        Assert.That(File.Exists(manifestPath), Is.True);
        Assert.That(File.Exists(Path.Combine(transactionPath, "cleanup-started")), Is.False);
        Assert.That(File.ReadAllBytes(appliedTargetPath), Is.EqualTo(appliedTarget));
    }

    [Test]
    public async Task CommitAsync_CommittedCleanupWhileRunning_RetryAllowsChangedTarget() {
        using WindowsClaudeTestWorkspace workspace = new();
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        await PrepareAndApply(CreateStore(workspace.UserProfilePath), workspace.Installation);
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string manifestPath = Path.Combine(transactionPath, "manifest.json");
        string cleanupMarkerPath = Path.Combine(transactionPath, "cleanup-started");
        string appliedTargetPath = Path.Combine(history.TargetIndexPath, "source-app-session.json");
        WindowsClaudeSessionHistoryStore interruptedStore = CreateStore(
            workspace.UserProfilePath,
            cleanupObserver: (operation, _) => {
                if (operation == "after-cleanup-marker-delete") {
                    throw new IOException("Synthetic interruption after cleanup marker deletion.");
                }
            }
        );

        await Assert.ThrowsAsync<IOException>(async () => await CommitHistoryAsync(
            interruptedStore,
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Committed
        ));

        Assert.That(File.Exists(cleanupMarkerPath), Is.False);
        Assert.That(File.Exists(manifestPath), Is.True);
        byte[] targetBeforeAppend = File.ReadAllBytes(appliedTargetPath);
        byte[]? targetAfterAppend = null;
        try {
            File.AppendAllText(appliedTargetPath, " ");
            targetAfterAppend = File.ReadAllBytes(appliedTargetPath);
            Assert.That(targetAfterAppend, Is.Not.EqualTo(targetBeforeAppend));

            await CommitHistoryAsync(
                CreateStore(workspace.UserProfilePath),
                workspace.Installation,
                ClaudeSessionHistoryCleanupIntent.Committed
            );

            Assert.That(Directory.Exists(transactionPath), Is.False);
            Assert.That(File.ReadAllBytes(appliedTargetPath), Is.EqualTo(targetAfterAppend));
        } finally {
            CryptographicOperations.ZeroMemory(targetBeforeAppend);
            if (targetAfterAppend is not null) {
                CryptographicOperations.ZeroMemory(targetAfterAppend);
            }
        }
    }

    [TestCase(ClaudeSessionHistoryCleanupIntent.Unspecified)]
    [TestCase((ClaudeSessionHistoryCleanupIntent)999)]
    public async Task CommitAsync_RejectsUnknownCleanupIntent(
        ClaudeSessionHistoryCleanupIntent cleanupIntent
    ) {
        using WindowsClaudeTestWorkspace workspace = new();
        CreateHistoryInputs(workspace);
        await CreateStore(workspace.UserProfilePath).PrepareReplacementAsync(
            workspace.Installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string manifestPath = Path.Combine(transactionPath, "manifest.json");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await CommitHistoryAsync(
            CreateStore(workspace.UserProfilePath),
            workspace.Installation,
            cleanupIntent
        ));

        Assert.That(Directory.Exists(transactionPath), Is.True);
        Assert.That(File.Exists(manifestPath), Is.True);
    }

    [Test]
    public async Task CommitAsync_RetriesAfterManifestDeletionLeavesEmptyTransactionDirectory() {
        using WindowsClaudeTestWorkspace workspace = new();
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        string transcriptPath = GetTranscriptPath(workspace.UserProfilePath, CliSessionId, "project-a");
        byte[] transcriptContent = File.ReadAllBytes(transcriptPath);
        await PrepareAndApply(CreateStore(workspace.UserProfilePath), workspace.Installation);
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string manifestPath = Path.Combine(transactionPath, "manifest.json");
        WindowsClaudeSessionHistoryStore interruptedStore = CreateStore(
            workspace.UserProfilePath,
            cleanupObserver: (operation, _) => {
                if (operation == "after-manifest-delete") {
                    throw new IOException("Synthetic interruption after the final transaction file was deleted.");
                }
            }
        );

        await Assert.ThrowsAsync<IOException>(async () => await CommitHistoryAsync(
            interruptedStore,
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Committed
        ));

        Assert.That(File.Exists(manifestPath), Is.False);
        Assert.That(Directory.Exists(transactionPath), Is.True);
        Assert.That(Directory.GetFileSystemEntries(transactionPath), Is.Empty);
        Assert.That(File.Exists(Path.Combine(history.TargetIndexPath, "source-app-session.json")), Is.True);

        await CommitHistoryAsync(
            CreateStore(workspace.UserProfilePath),
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Committed
        );

        Assert.That(Directory.Exists(transactionPath), Is.False);
        Assert.That(File.Exists(Path.Combine(history.TargetIndexPath, "source-app-session.json")), Is.True);
        Assert.That(File.ReadAllBytes(transcriptPath), Is.EqualTo(transcriptContent));
    }

    [Test]
    public async Task CommitAsync_UnknownInsertedAfterInventoryIsPreservedAndRetryable() {
        using WindowsClaudeTestWorkspace workspace = new();
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        string transcriptPath = GetTranscriptPath(workspace.UserProfilePath, CliSessionId, "project-a");
        byte[] transcriptContent = File.ReadAllBytes(transcriptPath);
        await PrepareAndApply(CreateStore(workspace.UserProfilePath), workspace.Installation);
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string manifestPath = Path.Combine(transactionPath, "manifest.json");
        string unknownPath = Path.Combine(transactionPath, "inserted-after-inventory.bin");
        WindowsClaudeSessionHistoryStore interruptedStore = CreateStore(
            workspace.UserProfilePath,
            cleanupObserver: (operation, _) => {
                if (operation == "inventory-captured") {
                    File.WriteAllText(unknownPath, "preserve");
                }
            }
        );

        await Assert.ThrowsAsync<InvalidDataException>(async () => await CommitHistoryAsync(
            interruptedStore,
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Committed
        ));

        Assert.That(File.Exists(unknownPath), Is.True);
        Assert.That(File.Exists(manifestPath), Is.True);
        Assert.That(File.Exists(Path.Combine(history.TargetIndexPath, "source-app-session.json")), Is.True);
        Assert.That(File.ReadAllBytes(transcriptPath), Is.EqualTo(transcriptContent));

        File.Delete(unknownPath);
        await CommitHistoryAsync(
            CreateStore(workspace.UserProfilePath),
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Committed
        );

        Assert.That(Directory.Exists(transactionPath), Is.False);
        Assert.That(File.Exists(Path.Combine(history.TargetIndexPath, "source-app-session.json")), Is.True);
    }

    [Test]
    public async Task CommitAsync_UnknownInsertedAfterPayloadDeletionIsPreservedAndRetryable() {
        using WindowsClaudeTestWorkspace workspace = new();
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        string transcriptPath = GetTranscriptPath(workspace.UserProfilePath, CliSessionId, "project-a");
        byte[] transcriptContent = File.ReadAllBytes(transcriptPath);
        await CreateStore(workspace.UserProfilePath).PrepareReplacementAsync(
            workspace.Installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string unknownPath = Path.Combine(transactionPath, "inserted-after-payload-delete.bin");
        bool inserted = false;
        WindowsClaudeSessionHistoryStore interruptedStore = CreateStore(
            workspace.UserProfilePath,
            cleanupObserver: (operation, _) => {
                if (!inserted && operation == "after-payload-delete") {
                    inserted = true;
                    File.WriteAllText(unknownPath, "preserve");
                }
            }
        );

        await Assert.ThrowsAsync<InvalidDataException>(async () => await CommitHistoryAsync(
            interruptedStore,
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Preparation
        ));

        Assert.That(inserted, Is.True);
        Assert.That(File.Exists(unknownPath), Is.True);
        Assert.That(File.Exists(Path.Combine(transactionPath, "manifest.json")), Is.True);
        Assert.That(File.ReadAllBytes(history.TargetFilePath), Is.EqualTo(history.TargetContent));
        Assert.That(File.ReadAllBytes(transcriptPath), Is.EqualTo(transcriptContent));

        File.Delete(unknownPath);
        await CommitHistoryAsync(
            CreateStore(workspace.UserProfilePath),
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Preparation
        );

        Assert.That(Directory.Exists(transactionPath), Is.False);
        Assert.That(File.ReadAllBytes(history.TargetFilePath), Is.EqualTo(history.TargetContent));
    }

    [Test]
    public async Task CommitAsync_SameNameReplacementAfterInventoryIsPreservedUntilRestored() {
        using WindowsClaudeTestWorkspace workspace = new();
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        string transcriptPath = GetTranscriptPath(workspace.UserProfilePath, CliSessionId, "project-a");
        byte[] transcriptContent = File.ReadAllBytes(transcriptPath);
        await CreateStore(workspace.UserProfilePath).PrepareReplacementAsync(
            workspace.Installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string manifestPath = Path.Combine(transactionPath, "manifest.json");
        string stagedPayloadPath = Path.Combine(transactionPath, "source-staging", "source-app-session.json");
        byte[] expectedPayload = File.ReadAllBytes(stagedPayloadPath);
        byte[] replacementPayload = Encoding.UTF8.GetBytes("replacement payload");
        WindowsClaudeSessionHistoryStore interruptedStore = CreateStore(
            workspace.UserProfilePath,
            cleanupObserver: (operation, _) => {
                if (operation == "inventory-captured") {
                    File.Delete(stagedPayloadPath);
                    File.WriteAllBytes(stagedPayloadPath, replacementPayload);
                }
            }
        );

        await Assert.ThrowsAsync<InvalidDataException>(async () => await CommitHistoryAsync(
            interruptedStore,
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Preparation
        ));

        Assert.That(File.Exists(manifestPath), Is.True);
        Assert.That(File.ReadAllBytes(stagedPayloadPath), Is.EqualTo(replacementPayload));
        Assert.That(File.Exists(Path.Combine(transactionPath, "target-backup", "target-app-session.json")), Is.True);
        Assert.That(File.ReadAllBytes(history.TargetFilePath), Is.EqualTo(history.TargetContent));
        Assert.That(File.ReadAllBytes(transcriptPath), Is.EqualTo(transcriptContent));

        WindowsClaudeSessionHistoryStore retryWithReplacement = CreateStore(workspace.UserProfilePath);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await CommitHistoryAsync(
            retryWithReplacement,
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Preparation
        ));

        File.WriteAllBytes(stagedPayloadPath, expectedPayload);
        await CommitHistoryAsync(
            CreateStore(workspace.UserProfilePath),
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Preparation
        );

        Assert.That(Directory.Exists(transactionPath), Is.False);
        Assert.That(File.ReadAllBytes(history.TargetFilePath), Is.EqualTo(history.TargetContent));
    }

    [Test]
    public async Task CommitAsync_NewFileInsertedBeforeEmptyDirectoryDeleteIsPreservedAndRetryable() {
        using WindowsClaudeTestWorkspace workspace = new();
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        string transcriptPath = GetTranscriptPath(workspace.UserProfilePath, CliSessionId, "project-a");
        byte[] transcriptContent = File.ReadAllBytes(transcriptPath);
        await CreateStore(workspace.UserProfilePath).PrepareReplacementAsync(
            workspace.Installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string manifestPath = Path.Combine(transactionPath, "manifest.json");
        string backupDirectory = Path.Combine(transactionPath, "target-backup");
        string insertedPath = Path.Combine(backupDirectory, "inserted-after-payload-delete.bin");
        WindowsClaudeSessionHistoryStore interruptedStore = CreateStore(
            workspace.UserProfilePath,
            cleanupObserver: (operation, path) => {
                if (operation == "before-directory-delete"
                    && string.Equals(path, backupDirectory, StringComparison.OrdinalIgnoreCase)) {
                    File.WriteAllText(insertedPath, "preserve");
                }
            }
        );

        await Assert.ThrowsAsync<InvalidDataException>(async () => await CommitHistoryAsync(
            interruptedStore,
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Preparation
        ));

        Assert.That(File.Exists(insertedPath), Is.True);
        Assert.That(File.Exists(manifestPath), Is.True);
        Assert.That(File.ReadAllBytes(history.TargetFilePath), Is.EqualTo(history.TargetContent));
        Assert.That(File.ReadAllBytes(transcriptPath), Is.EqualTo(transcriptContent));

        File.Delete(insertedPath);
        await CommitHistoryAsync(
            CreateStore(workspace.UserProfilePath),
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Preparation
        );

        Assert.That(Directory.Exists(transactionPath), Is.False);
        Assert.That(File.ReadAllBytes(history.TargetFilePath), Is.EqualTo(history.TargetContent));
    }

    [Test]
    public async Task CommitAsync_LegacyCleanupMarkerOnlyTransactionIsRemoved() {
        using WindowsClaudeTestWorkspace workspace = new();
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        Directory.CreateDirectory(transactionPath);
        File.WriteAllBytes(Path.Combine(transactionPath, "cleanup-started"), "cleanup-started"u8.ToArray());

        await CommitHistoryAsync(
            CreateStore(workspace.UserProfilePath),
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Committed
        );

        Assert.That(Directory.Exists(transactionPath), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CommitAsync_LegacyCleanupMarkerOnlyRejectsInvalidOrNonUniqueMarker(bool addUnknownEntry) {
        using WindowsClaudeTestWorkspace workspace = new();
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        Directory.CreateDirectory(transactionPath);
        string markerPath = Path.Combine(transactionPath, "cleanup-started");
        File.WriteAllBytes(markerPath, addUnknownEntry ? "cleanup-started"u8.ToArray() : "invalid"u8.ToArray());
        string? unknownPath = null;
        if (addUnknownEntry) {
            unknownPath = Path.Combine(transactionPath, "unknown.bin");
            File.WriteAllText(unknownPath, "preserve");
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () => await CommitHistoryAsync(
            CreateStore(workspace.UserProfilePath),
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Committed
        ));

        Assert.That(Directory.Exists(transactionPath), Is.True);
        Assert.That(File.Exists(markerPath), Is.True);
        if (unknownPath is not null) {
            Assert.That(File.Exists(unknownPath), Is.True);
        }
    }

    [Test]
    public async Task CommitAsync_WithoutManifestDeletesOnlyExactManifestTemporaryFile() {
        using WindowsClaudeTestWorkspace workspace = new();
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        Directory.CreateDirectory(transactionPath);
        string intentTemporaryPath = Path.Combine(
            transactionPath,
            ".manifest.json." + Guid.NewGuid().ToString("N") + ".tmp"
        );
        File.WriteAllText(intentTemporaryPath, "partial intent");

        await CommitHistoryAsync(store, workspace.Installation, ClaudeSessionHistoryCleanupIntent.Preparation);

        Assert.That(File.Exists(intentTemporaryPath), Is.False);
        Assert.That(Directory.Exists(transactionPath), Is.False);
    }

    [Test]
    public async Task CommitAsync_WhenPreparationJournalExistsBeforeStoreIntent_IsIdempotentNoOp() {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        byte[] targetHash = SHA256.HashData(history.TargetContent);
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);

        await CommitHistoryAsync(store, installation, ClaudeSessionHistoryCleanupIntent.Preparation);
        await CommitHistoryAsync(store, installation, ClaudeSessionHistoryCleanupIntent.Preparation);

        Assert.That(Directory.Exists(transactionPath), Is.False);
        Assert.That(SHA256.HashData(File.ReadAllBytes(history.TargetFilePath)), Is.EqualTo(targetHash));
        Assert.That(File.Exists(Path.Combine(history.SourceIndexPath, "source-app-session.json")), Is.True);
    }

    [Test]
    public async Task CommitAsync_WithoutManifestPreservesUnknownEntriesAndIntentTemporaryFile() {
        using WindowsClaudeTestWorkspace workspace = new();
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        Directory.CreateDirectory(transactionPath);
        string intentTemporaryPath = Path.Combine(
            transactionPath,
            ".manifest.json." + Guid.NewGuid().ToString("N") + ".tmp"
        );
        string unknownPath = Path.Combine(transactionPath, "unknown.bin");
        File.WriteAllText(intentTemporaryPath, "partial intent");
        File.WriteAllText(unknownPath, "preserve");

        await Assert.ThrowsAsync<InvalidDataException>(async () => await CommitHistoryAsync(
            store,
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Preparation
        ));

        Assert.That(File.Exists(intentTemporaryPath), Is.True);
        Assert.That(File.Exists(unknownPath), Is.True);
        Assert.That(Directory.Exists(transactionPath), Is.True);
    }

    [Test]
    public async Task CommitAsync_IncompletePreparationManifestPreservesPartialPayload() {
        using WindowsClaudeTestWorkspace workspace = new();
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string stagingPath = Path.Combine(transactionPath, "source-staging");
        Directory.CreateDirectory(stagingPath);
        string manifestPath = Path.Combine(transactionPath, "manifest.json");
        string partialPayloadPath = Path.Combine(stagingPath, "source-app-session.json");
        File.WriteAllText(manifestPath, "{}");
        File.WriteAllText(partialPayloadPath, "partial");

        await Assert.ThrowsAsync<InvalidDataException>(async () => await CommitHistoryAsync(
            store,
            workspace.Installation,
            ClaudeSessionHistoryCleanupIntent.Preparation
        ));

        Assert.That(File.Exists(manifestPath), Is.True);
        Assert.That(File.Exists(partialPayloadPath), Is.True);
        Assert.That(Directory.Exists(transactionPath), Is.True);
    }

    [Test]
    public async Task CommitAsync_MismatchedPreparationTransactionIdPreservesPartialPayload() {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        string targetIndexPath = history.TargetIndexPath;
        string targetFilePath = history.TargetFilePath;
        byte[] targetContent = history.TargetContent;
        using CancellationTokenSource cancellation = new();
        WindowsClaudeSessionHistoryStore store = new(
            workspace.UserProfilePath,
            new HistoryTestProcessController(),
            null,
            cancellation.Cancel
        );
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await store.PrepareReplacementAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            cancellation.Token
        ));

        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string manifestPath = Path.Combine(transactionPath, "manifest.json");
        string alteredManifest = File.ReadAllText(manifestPath).Replace(
            TransactionId.ToString("D"),
            Guid.NewGuid().ToString("D"),
            StringComparison.Ordinal
        );
        File.WriteAllText(manifestPath, alteredManifest);
        WindowsClaudeSessionHistoryStore restartedStore = CreateStore(workspace.UserProfilePath);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await CommitHistoryAsync(
            restartedStore,
            installation,
            ClaudeSessionHistoryCleanupIntent.Preparation
        ));

        Assert.That(File.Exists(manifestPath), Is.True);
        Assert.That(File.Exists(Path.Combine(transactionPath, "source-staging", "source-app-session.json")), Is.True);
        Assert.That(SHA256.HashData(File.ReadAllBytes(targetFilePath)), Is.EqualTo(SHA256.HashData(targetContent)));
        Assert.That(Directory.Exists(targetIndexPath), Is.True);
    }

    [Test]
    public async Task CommitAsync_MismatchedPreparationTargetPathPreservesPartialPayload() {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        string targetIndexPath = history.TargetIndexPath;
        string targetFilePath = history.TargetFilePath;
        byte[] targetContent = history.TargetContent;
        using CancellationTokenSource cancellation = new();
        WindowsClaudeSessionHistoryStore store = new(
            workspace.UserProfilePath,
            new HistoryTestProcessController(),
            null,
            cancellation.Cancel
        );
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await store.PrepareReplacementAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            cancellation.Token
        ));

        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string manifestPath = Path.Combine(transactionPath, "manifest.json");
        string targetPathReplacement = Path.Combine(
            workspace.DataRootPath,
            "claude-code-sessions",
            TargetAccount.ToString("D"),
            "ffffffff-ffff-4fff-8fff-ffffffffffff"
        );
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        Dictionary<string, JsonElement> manifestProperties = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        manifestProperties["targetIndexPath"] = JsonSerializer.SerializeToElement(targetPathReplacement);
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifestProperties));
        WindowsClaudeSessionHistoryStore restartedStore = CreateStore(workspace.UserProfilePath);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await CommitHistoryAsync(
            restartedStore,
            installation,
            ClaudeSessionHistoryCleanupIntent.Preparation
        ));

        Assert.That(File.Exists(manifestPath), Is.True);
        Assert.That(File.Exists(Path.Combine(transactionPath, "source-staging", "source-app-session.json")), Is.True);
        Assert.That(SHA256.HashData(File.ReadAllBytes(targetFilePath)), Is.EqualTo(SHA256.HashData(targetContent)));
        Assert.That(Directory.Exists(targetIndexPath), Is.True);
    }

    [TestCase(IClaudeDesktopProcessController.ProcessStatus.Running)]
    [TestCase(IClaudeDesktopProcessController.ProcessStatus.Unknown)]
    public async Task ApplyReplacement_ProcessStateChangesBetweenMutations_PreservesRecoveryData(
        IClaudeDesktopProcessController.ProcessStatus changedStatus
    ) {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        string targetIndexPath = history.TargetIndexPath;
        byte[] targetContent = history.TargetContent;
        string appliedSourcePath = Path.Combine(targetIndexPath, "source-app-session.json");
        string originalSourcePath = Path.Combine(history.SourceIndexPath, "source-app-session.json");
        HistoryTestProcessController processController = new();
        processController.SetStatusSequence(
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            changedStatus
        );
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath, processController);
        IClaudeSessionHistoryStore.PreparationResult preparation = await store.PrepareReplacementAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
        Assert.That(preparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string displacedPath = Path.Combine(transactionPath, "target-displaced");
        byte[] targetHash = SHA256.HashData(targetContent);

        await Assert.CatchAsync<IOException>(async () => await store.ApplyReplacementAsync(
            installation,
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        ));

        Assert.That(processController.StatusCallCount, Is.EqualTo(2));
        Assert.That(Directory.Exists(targetIndexPath), Is.True);
        Assert.That(
            Directory.GetFiles(targetIndexPath).Select(Path.GetFileName),
            Is.EquivalentTo(new[] { "source-app-session.json", "target-app-session.json" })
        );
        Assert.That(File.ReadAllBytes(appliedSourcePath), Is.EqualTo(File.ReadAllBytes(originalSourcePath)));
        Assert.That(File.ReadAllBytes(history.TargetFilePath), Is.EqualTo(targetContent));
        Assert.That(File.Exists(Path.Combine(displacedPath, "target-app-session.json")), Is.False);
        Assert.That(File.Exists(Path.Combine(transactionPath, "manifest.json")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(transactionPath, "source-staging")), Is.True);
        Assert.That(File.Exists(Path.Combine(transactionPath, "target-backup", "target-app-session.json")), Is.True);
    }

    [TestCase(IClaudeDesktopProcessController.ProcessStatus.Running)]
    [TestCase(IClaudeDesktopProcessController.ProcessStatus.Unknown)]
    public async Task ApplyReplacement_UnsafeProcessBeforeFirstMutationPreservesPreparationEvidence(
        IClaudeDesktopProcessController.ProcessStatus changedStatus
    ) {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        string targetIndexPath = history.TargetIndexPath;
        byte[] targetContent = history.TargetContent;
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        HistoryTestProcessController processController = new();
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath, processController);
        IClaudeSessionHistoryStore.PreparationResult preparation = await store.PrepareReplacementAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
        Assert.That(preparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        string displacedPath = Path.Combine(transactionPath, "target-displaced");
        string stagingPath = Path.Combine(transactionPath, "source-staging");
        processController.Status = changedStatus;
        int statusCallCountBeforeApply = processController.StatusCallCount;

        await Assert.CatchAsync<IOException>(async () => await store.ApplyReplacementAsync(
            installation,
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        ));

        Assert.That(processController.StatusCallCount - statusCallCountBeforeApply, Is.EqualTo(1));
        Assert.That(Directory.Exists(targetIndexPath), Is.True);
        Assert.That(Directory.GetFiles(targetIndexPath).Select(Path.GetFileName), Is.EquivalentTo(new[] { "target-app-session.json" }));
        Assert.That(File.ReadAllBytes(Path.Combine(targetIndexPath, "target-app-session.json")), Is.EqualTo(targetContent));
        Assert.That(File.Exists(Path.Combine(displacedPath, "target-app-session.json")), Is.False);
        Assert.That(File.Exists(Path.Combine(stagingPath, "source-app-session.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(transactionPath, "target-backup", "target-app-session.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(transactionPath, "manifest.json")), Is.True);
    }

    [TestCase(IClaudeDesktopProcessController.ProcessStatus.Running)]
    [TestCase(IClaudeDesktopProcessController.ProcessStatus.Unknown)]
    public async Task RestoreTarget_ProcessStateChangesBetweenMutations_PreservesRollbackData(
        IClaudeDesktopProcessController.ProcessStatus changedStatus
    ) {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent) history =
            CreateHistoryInputs(workspace);
        string targetIndexPath = history.TargetIndexPath;
        string targetFilePath = history.TargetFilePath;
        HistoryTestProcessController processController = new();
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath, processController);
        await PrepareAndApply(store, installation);
        int statusCallCountBeforeRestore = processController.StatusCallCount;
        processController.SetStatusSequence(
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            changedStatus
        );
        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string displacedPath = Path.Combine(transactionPath, "target-rollback-displaced");
        string rollbackStagingPath = Path.Combine(transactionPath, "target-rollback-staging");

        await Assert.CatchAsync<IOException>(async () => await store.RestoreTargetAsync(
            installation,
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        ));

        Assert.That(processController.StatusCallCount - statusCallCountBeforeRestore, Is.EqualTo(2));
        Assert.That(Directory.Exists(targetIndexPath), Is.True);
        Assert.That(
            Directory.GetFiles(targetIndexPath).Select(Path.GetFileName),
            Is.EquivalentTo(new[] { "source-app-session.json", "target-app-session.json" })
        );
        Assert.That(File.ReadAllBytes(targetFilePath), Is.EqualTo(history.TargetContent));
        Assert.That(File.Exists(Path.Combine(displacedPath, "source-app-session.json")), Is.False);
        Assert.That(File.Exists(Path.Combine(rollbackStagingPath, "target-app-session.json")), Is.False);
        Assert.That(File.Exists(Path.Combine(transactionPath, "manifest.json")), Is.True);
    }

    [Test]
    public async Task CommitAsync_LeavesUnknownTransactionDataAndBackupsForSafeRetry() {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);
        string sourceIndexPath = GetIndexPath(workspace.DataRootPath, SourceAccount, SourceOrganization);
        string targetIndexPath = GetIndexPath(workspace.DataRootPath, TargetAccount, TargetOrganization);
        Directory.CreateDirectory(sourceIndexPath);
        Directory.CreateDirectory(targetIndexPath);
        File.Copy(Fixture("session-index-source.json"), Path.Combine(sourceIndexPath, "source-app-session.json"));
        File.Copy(Fixture("session-index-target.json"), Path.Combine(targetIndexPath, "target-app-session.json"));
        CreateTranscript(workspace.UserProfilePath, CliSessionId, "project-a");
        await PrepareAndApply(store, installation);

        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string unexpectedPath = Path.Combine(transactionPath, "unexpected-data.txt");
        File.WriteAllText(unexpectedPath, "preserve");
        string backupPath = Path.Combine(transactionPath, "target-backup");

        await Assert.ThrowsAsync<InvalidDataException>(async () => await CommitHistoryAsync(
            store,
            installation,
            ClaudeSessionHistoryCleanupIntent.Committed
        ));

        Assert.That(File.Exists(unexpectedPath), Is.True);
        Assert.That(Directory.Exists(backupPath), Is.True);
        Assert.That(File.Exists(Path.Combine(transactionPath, "manifest.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(transactionPath, "cleanup-started")), Is.False);

        File.Delete(unexpectedPath);
        await CommitHistoryAsync(store, installation, ClaudeSessionHistoryCleanupIntent.Committed);

        Assert.That(Directory.Exists(transactionPath), Is.False);
    }

    [Test]
    public async Task CommitAsync_LeavesUnexpectedNestedDirectoryForSafeRetry() {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);
        string sourceIndexPath = GetIndexPath(workspace.DataRootPath, SourceAccount, SourceOrganization);
        string targetIndexPath = GetIndexPath(workspace.DataRootPath, TargetAccount, TargetOrganization);
        Directory.CreateDirectory(sourceIndexPath);
        Directory.CreateDirectory(targetIndexPath);
        File.Copy(Fixture("session-index-source.json"), Path.Combine(sourceIndexPath, "source-app-session.json"));
        File.Copy(Fixture("session-index-target.json"), Path.Combine(targetIndexPath, "target-app-session.json"));
        CreateTranscript(workspace.UserProfilePath, CliSessionId, "project-a");
        await PrepareAndApply(store, installation);

        string transactionPath = GetTransactionPath(workspace.DataRootPath, TransactionId);
        string unexpectedDirectory = Path.Combine(transactionPath, "target-backup", "unexpected-empty-directory");
        Directory.CreateDirectory(unexpectedDirectory);
        string manifestPath = Path.Combine(transactionPath, "manifest.json");

        await Assert.ThrowsAsync<InvalidDataException>(async () => await CommitHistoryAsync(
            store,
            installation,
            ClaudeSessionHistoryCleanupIntent.Committed
        ));

        Assert.That(Directory.Exists(unexpectedDirectory), Is.True);
        Assert.That(Directory.Exists(Path.Combine(transactionPath, "target-backup")), Is.True);
        Assert.That(File.Exists(manifestPath), Is.True);
        Assert.That(File.Exists(Path.Combine(transactionPath, "cleanup-started")), Is.True);

        Directory.Delete(unexpectedDirectory);
        await CommitHistoryAsync(store, installation, ClaudeSessionHistoryCleanupIntent.Committed);

        Assert.That(Directory.Exists(transactionPath), Is.False);
    }

    [Test]
    public async Task PrepareReplacement_AcceptsEmptySourceAndCanRestoreExistingTarget() {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);
        string sourceIndexPath = GetIndexPath(workspace.DataRootPath, SourceAccount, SourceOrganization);
        string targetIndexPath = GetIndexPath(workspace.DataRootPath, TargetAccount, TargetOrganization);
        Directory.CreateDirectory(sourceIndexPath);
        Directory.CreateDirectory(targetIndexPath);
        File.Copy(Fixture("session-index-target.json"), Path.Combine(targetIndexPath, "target-app-session.json"));
        byte[] targetBefore = File.ReadAllBytes(Path.Combine(targetIndexPath, "target-app-session.json"));

        IClaudeSessionHistoryStore.ValidationResult validation = await store.ValidateAccountAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            CancellationToken.None
        );
        IClaudeSessionHistoryStore.PreparationResult preparation = await store.PrepareReplacementAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );

        Assert.That(validation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(validation.SessionCount, Is.Zero);
        Assert.That(preparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(preparation.SourceSessionCount, Is.Zero);

        await store.ApplyReplacementAsync(
            installation,
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
        Assert.That(Directory.GetFiles(targetIndexPath), Is.Empty);

        await store.RestoreTargetAsync(
            installation,
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
        Assert.That(File.ReadAllBytes(Path.Combine(targetIndexPath, "target-app-session.json")), Is.EqualTo(targetBefore));
        await CommitHistoryAsync(store, installation, ClaudeSessionHistoryCleanupIntent.Preparation);
    }

    [Test]
    public async Task ApplyReplacement_CreatesMissingTargetPathAndRollbackRemovesCreatedDirectories() {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);
        string sourceIndexPath = GetIndexPath(workspace.DataRootPath, SourceAccount, SourceOrganization);
        string targetIndexPath = GetIndexPath(workspace.DataRootPath, TargetAccount, TargetOrganization);
        Directory.CreateDirectory(sourceIndexPath);
        File.Copy(Fixture("session-index-source.json"), Path.Combine(sourceIndexPath, "source-app-session.json"));
        CreateTranscript(workspace.UserProfilePath, CliSessionId, "project-a");

        IClaudeSessionHistoryStore.PreparationResult preparation = await store.PrepareReplacementAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
        Assert.That(preparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(Directory.Exists(targetIndexPath), Is.False);

        await store.ApplyReplacementAsync(
            installation,
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
        Assert.That(File.Exists(Path.Combine(targetIndexPath, "source-app-session.json")), Is.True);

        await store.RestoreTargetAsync(
            installation,
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );

        Assert.That(Directory.Exists(targetIndexPath), Is.False);
        Assert.That(Directory.Exists(Path.GetDirectoryName(targetIndexPath)!), Is.False);
        Assert.That(Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(targetIndexPath)!)!), Is.True);
        Assert.That(File.Exists(Path.Combine(sourceIndexPath, "source-app-session.json")), Is.True);
        await CommitHistoryAsync(store, installation, ClaudeSessionHistoryCleanupIntent.Preparation);
    }

    [Test]
    public async Task PrepareReplacement_RejectsMissingTranscriptRootBeforeCreatingTargetTransaction() {
        using WindowsClaudeTestWorkspace workspace = new();
        IClaudeDesktopInstallationLocator.Installation installation = workspace.Installation;
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);
        string sourceIndexPath = GetIndexPath(workspace.DataRootPath, SourceAccount, SourceOrganization);
        string targetIndexPath = GetIndexPath(workspace.DataRootPath, TargetAccount, TargetOrganization);
        Directory.CreateDirectory(sourceIndexPath);
        Directory.CreateDirectory(targetIndexPath);
        File.Copy(Fixture("session-index-source.json"), Path.Combine(sourceIndexPath, "local_11111111-1111-4111-8111-111111111111.json"));
        File.Copy(Fixture("session-index-target.json"), Path.Combine(targetIndexPath, "target-app-session.json"));
        byte[] originalTarget = File.ReadAllBytes(Path.Combine(targetIndexPath, "target-app-session.json"));

        IClaudeSessionHistoryStore.ValidationResult validation = await store.ValidateAccountAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            CancellationToken.None
        );
        IClaudeSessionHistoryStore.PreparationResult preparation = await store.PrepareReplacementAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );

        Assert.That(validation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.InvalidTranscriptReference));
        Assert.That(preparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.InvalidTranscriptReference));
        Assert.That(File.ReadAllBytes(Path.Combine(targetIndexPath, "target-app-session.json")), Is.EqualTo(originalTarget));
        Assert.That(Directory.Exists(GetTransactionPath(workspace.DataRootPath, TransactionId)), Is.False);
    }

    [Test]
    public async Task ValidateAccount_ExcludesDuplicateTranscriptReferences() {
        using WindowsClaudeTestWorkspace workspace = new();
        WindowsClaudeSessionHistoryStore store = CreateStore(workspace.UserProfilePath);
        string sourceIndexPath = GetIndexPath(workspace.DataRootPath, SourceAccount, SourceOrganization);
        Directory.CreateDirectory(sourceIndexPath);
        File.Copy(Fixture("session-index-source.json"), Path.Combine(sourceIndexPath, "local_11111111-1111-4111-8111-111111111111.json"));
        CreateTranscript(workspace.UserProfilePath, CliSessionId, "project-a");
        CreateTranscript(workspace.UserProfilePath, CliSessionId, "project-b");

        IClaudeSessionHistoryStore.ValidationResult validation = await store.ValidateAccountAsync(
            workspace.Installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            CancellationToken.None
        );

        Assert.That(validation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        Assert.That(validation.SessionCount, Is.Zero);
    }

    private static WindowsClaudeSessionHistoryStore CreateStore(
        string userProfilePath,
        HistoryTestProcessController? processController = null,
        Action<string, string>? cleanupObserver = null
    ) {
        return new WindowsClaudeSessionHistoryStore(
            userProfilePath,
            processController ?? new HistoryTestProcessController(),
            cleanupObserver
        );
    }

    private static Task CommitHistoryAsync(
        WindowsClaudeSessionHistoryStore store,
        IClaudeDesktopInstallationLocator.Installation installation,
        ClaudeSessionHistoryCleanupIntent cleanupIntent
    ) {
        return store.CommitAsync(installation, TransactionId, cleanupIntent, CancellationToken.None);
    }

    private static async Task PrepareAndApply(
        WindowsClaudeSessionHistoryStore store,
        IClaudeDesktopInstallationLocator.Installation installation
    ) {
        IClaudeSessionHistoryStore.PreparationResult preparation = await store.PrepareReplacementAsync(
            installation,
            SourceAccount.ToString("D"),
            SourceOrganization.ToString("D"),
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
        Assert.That(preparation.Status, Is.EqualTo(IClaudeSessionHistoryStore.ValidationStatus.Valid));
        await store.ApplyReplacementAsync(
            installation,
            TargetAccount.ToString("D"),
            TargetOrganization.ToString("D"),
            TransactionId,
            CancellationToken.None
        );
    }

    private static string GetIndexPath(string dataRootPath, Guid account, Guid organization) {
        return Path.Combine(dataRootPath, "claude-code-sessions", account.ToString("D"), organization.ToString("D"));
    }

    private static (string SourceIndexPath, string TargetIndexPath, string TargetFilePath, byte[] TargetContent)
        CreateHistoryInputs(WindowsClaudeTestWorkspace workspace) {
        string sourceIndexPath = GetIndexPath(workspace.DataRootPath, SourceAccount, SourceOrganization);
        string targetIndexPath = GetIndexPath(workspace.DataRootPath, TargetAccount, TargetOrganization);
        Directory.CreateDirectory(sourceIndexPath);
        Directory.CreateDirectory(targetIndexPath);
        File.Copy(Fixture("session-index-source.json"), Path.Combine(sourceIndexPath, "source-app-session.json"));
        string targetFilePath = Path.Combine(targetIndexPath, "target-app-session.json");
        File.Copy(Fixture("session-index-target.json"), targetFilePath);
        CreateTranscript(workspace.UserProfilePath, CliSessionId, "project-a");
        return (sourceIndexPath, targetIndexPath, targetFilePath, File.ReadAllBytes(targetFilePath));
    }

    private static string GetTransactionPath(string dataRootPath, Guid transactionId) {
        return Path.Combine(
            dataRootPath,
            "claude-code-sessions",
            ".ai-account-manager-transactions",
            transactionId.ToString("N")
        );
    }

    private static string CreateTranscript(string userProfilePath, string cliSessionId, string projectName) {
        string projectPath = Path.Combine(userProfilePath, ".claude", "projects", projectName);
        Directory.CreateDirectory(projectPath);
        string transcriptPath = Path.Combine(projectPath, cliSessionId + ".jsonl");
        File.Copy(Fixture("session-transcript.jsonl"), transcriptPath);
        return transcriptPath;
    }

    private static string GetTranscriptPath(string userProfilePath, string cliSessionId, string projectName) {
        return Path.Combine(
            userProfilePath,
            ".claude",
            "projects",
            projectName,
            cliSessionId + ".jsonl"
        );
    }

    private static string Fixture(string fileName) {
        return Path.Combine(TestContext.CurrentContext.TestDirectory, "Claude", "Fixtures", fileName);
    }

    private sealed class HistoryTestProcessController : IClaudeDesktopProcessController {
        private readonly Queue<IClaudeDesktopProcessController.ProcessStatus> statusSequence = new();

        internal IClaudeDesktopProcessController.ProcessStatus Status { get; set; } =
            IClaudeDesktopProcessController.ProcessStatus.NotRunning;

        internal Action<int>? OnStatusRequest { get; set; }

        internal int StatusCallCount { get; private set; }

        internal void SetStatusSequence(params IClaudeDesktopProcessController.ProcessStatus[] statuses) {
            statusSequence.Clear();
            foreach (IClaudeDesktopProcessController.ProcessStatus status in statuses) {
                statusSequence.Enqueue(status);
            }
        }

        public Task<IClaudeDesktopProcessController.ProcessStatus> GetStatusAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            StatusCallCount++;
            OnStatusRequest?.Invoke(StatusCallCount);
            if (statusSequence.TryDequeue(out IClaudeDesktopProcessController.ProcessStatus status)) {
                Status = status;
            }

            return Task.FromResult(Status);
        }

        public Task<bool> CloseGracefullyAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            TimeSpan timeout,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Status = IClaudeDesktopProcessController.ProcessStatus.NotRunning;
            return Task.FromResult(true);
        }

        public Task LaunchAsync(
            IClaudeDesktopInstallationLocator.Installation installation,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Status = IClaudeDesktopProcessController.ProcessStatus.Running;
            return Task.CompletedTask;
        }
    }
}

internal sealed class WindowsClaudeTestWorkspace : IDisposable {
    private readonly Guid bindingId = Guid.NewGuid();
    internal WindowsClaudeTestWorkspace() {
        RootPath = Path.Combine(Path.GetTempPath(), "AiAccountManagerTests", Guid.NewGuid().ToString("N"));
        DataRootPath = Path.Combine(RootPath, "claude-data");
        UserProfilePath = Path.Combine(RootPath, "synthetic-user");
        Directory.CreateDirectory(DataRootPath);
        Directory.CreateDirectory(UserProfilePath);
    }

    internal string RootPath { get; }

    internal string DataRootPath { get; }

    internal string UserProfilePath { get; }

    internal IClaudeDesktopInstallationLocator.Installation Installation {
        get {
            string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DataRootPath)).ToUpperInvariant();
            byte[] pathBytes = Encoding.UTF8.GetBytes(normalizedPath);
            byte[] digest = SHA256.HashData(pathBytes);
            try {
                using ClaudeWindowsPathSafety.IdentityLease root = ClaudeWindowsPathSafety.OpenIdentity(DataRootPath, directory: true);
                ClaudeDesktopRootBinding.Observation observation = new(
                    1, bindingId, "S-1-5-21-100-200-300-1001", "fixture-claude-installation",
                    IClaudeDesktopInstallationLocator.InstallKind.Msix, "Fixture.Package!App", "Fixture.Package_Family",
                    "Fixture.Package_1.0.0.0_x64__Publisher", null, null, null, DataRootPath,
                    File.Exists(Path.Combine(DataRootPath, "Cookies")) ? "Cookies" : "Network/Cookies",
                    root.Identity.VolumeSerialNumber, root.Identity.FileId, 1,
                    [new ClaudeDesktopRootBinding.ObservedProcess(100, ClaudeTestData.Now)]
                );
                string keyDigest = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes("RFBBUEktZml4dHVyZS1rZXk="))
                ).ToLowerInvariant();
                ClaudeDesktopRootBinding binding = new(observation, keyDigest);
                return new IClaudeDesktopInstallationLocator.Installation(
                    "fixture-claude-installation",
                    IClaudeDesktopInstallationLocator.InstallKind.Msix,
                    DataRootPath,
                    Convert.ToHexString(digest).ToLowerInvariant(),
                    "Fixture.Package!App",
                    null,
                    observation,
                    binding
                );
            } finally {
                CryptographicOperations.ZeroMemory(pathBytes);
                CryptographicOperations.ZeroMemory(digest);
            }
        }
    }

    public void Dispose() {
        if (Directory.Exists(RootPath)) {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
