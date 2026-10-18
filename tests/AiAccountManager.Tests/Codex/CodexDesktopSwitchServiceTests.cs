using System.Text;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Runtime;
using CloudyWing.AiAccountManager.Application.Security;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Codex;

[TestFixture]
public sealed class CodexDesktopSwitchServiceTests {
    private static readonly CodexAccountProfile AccountA = CreateAccount("acct-a", "Alpha");
    private static readonly CodexAccountProfile AccountB = CreateAccount("acct-b", "Beta");

    internal static CodexDesktopSwitchService CreateRenderingService() => new Fixture(CodexDesktopProcessStatus.NotRunning).Service;

    [Test]
    public async Task SwitchAsync_RunningDesktop_SavesSourceWritesTargetAndRelaunches() {
        Fixture fixture = new(CodexDesktopProcessStatus.Running);
        fixture.AuthStore.Current = Auth("acct-a", "refreshed-a");
        fixture.Snapshots.Saved[AccountA.AccountId] = Auth("acct-a", "stale-a");
        fixture.Snapshots.Saved[AccountB.AccountId] = Auth("acct-b", "token-b");

        CodexDesktopSwitchService.OperationResult result = await fixture.Service.SwitchAsync(
            AccountB.AccountId,
            CancellationToken.None
        );

        Assert.Multiple(() => {
            Assert.That(result.Outcome, Is.EqualTo(CodexDesktopSwitchService.Outcome.Succeeded));
            Assert.That(Text(fixture.AuthStore.Current), Is.EqualTo("acct-b|token-b"));
            Assert.That(Text(fixture.Snapshots.Saved[AccountA.AccountId]), Is.EqualTo("acct-a|refreshed-a"));
            Assert.That(fixture.Snapshots.Journal, Is.Null);
            Assert.That(fixture.Process.CloseCallCount, Is.EqualTo(1));
            Assert.That(fixture.Process.Launches, Is.EqualTo(new[] { Fixture.AppUserModelId }));
        });
    }

    [Test]
    public async Task SwitchAsync_StoppedDesktop_SwitchesWithoutClosingOrLaunching() {
        Fixture fixture = new(CodexDesktopProcessStatus.NotRunning);
        fixture.AuthStore.Current = Auth("acct-a", "token-a");
        fixture.Snapshots.Saved[AccountB.AccountId] = Auth("acct-b", "token-b");

        CodexDesktopSwitchService.OperationResult result = await fixture.Service.SwitchAsync(
            AccountB.AccountId,
            CancellationToken.None
        );

        Assert.Multiple(() => {
            Assert.That(result.Outcome, Is.EqualTo(CodexDesktopSwitchService.Outcome.Succeeded));
            Assert.That(Text(fixture.AuthStore.Current), Is.EqualTo("acct-b|token-b"));
            Assert.That(fixture.Process.CloseCallCount, Is.Zero);
            Assert.That(fixture.Process.Launches, Is.Empty);
        });
    }

    [Test]
    public async Task SwitchAsync_DesktopDoesNotCloseInTime_LeavesLoginUnchanged() {
        Fixture fixture = new(CodexDesktopProcessStatus.Running);
        fixture.Process.CloseSucceeds = false;
        fixture.AuthStore.Current = Auth("acct-a", "token-a");
        fixture.Snapshots.Saved[AccountB.AccountId] = Auth("acct-b", "token-b");

        CodexDesktopSwitchService.OperationResult result = await fixture.Service.SwitchAsync(
            AccountB.AccountId,
            CancellationToken.None
        );

        Assert.Multiple(() => {
            Assert.That(result.Outcome, Is.EqualTo(CodexDesktopSwitchService.Outcome.DesktopNotClosed));
            Assert.That(Text(fixture.AuthStore.Current), Is.EqualTo("acct-a|token-a"));
            Assert.That(fixture.AuthStore.ReplaceCallCount, Is.Zero);
            Assert.That(fixture.Snapshots.Journal, Is.Null);
        });
    }

    [Test]
    public async Task SwitchAsync_UnknownProcessStatus_DoesNotCloseOrWrite() {
        Fixture fixture = new(CodexDesktopProcessStatus.Unknown);
        fixture.AuthStore.Current = Auth("acct-a", "token-a");
        fixture.Snapshots.Saved[AccountB.AccountId] = Auth("acct-b", "token-b");

        CodexDesktopSwitchService.OperationResult result = await fixture.Service.SwitchAsync(
            AccountB.AccountId,
            CancellationToken.None
        );

        Assert.Multiple(() => {
            Assert.That(result.Outcome, Is.EqualTo(CodexDesktopSwitchService.Outcome.DesktopNotClosed));
            Assert.That(fixture.Process.CloseCallCount, Is.Zero);
            Assert.That(fixture.AuthStore.ReplaceCallCount, Is.Zero);
        });
    }

    [Test]
    public async Task SwitchAsync_CurrentLoginNotSaved_RefusesToOverwriteIt() {
        Fixture fixture = new(CodexDesktopProcessStatus.NotRunning);
        fixture.AuthStore.Current = Auth("acct-unknown", "token-x");
        fixture.Snapshots.Saved[AccountB.AccountId] = Auth("acct-b", "token-b");

        CodexDesktopSwitchService.OperationResult result = await fixture.Service.SwitchAsync(
            AccountB.AccountId,
            CancellationToken.None
        );

        Assert.Multiple(() => {
            Assert.That(result.Outcome, Is.EqualTo(CodexDesktopSwitchService.Outcome.CurrentLoginNotSaved));
            Assert.That(Text(fixture.AuthStore.Current), Is.EqualTo("acct-unknown|token-x"));
        });
    }

    [Test]
    public async Task SwitchAsync_TargetWithoutSnapshot_DoesNotTouchDesktop() {
        Fixture fixture = new(CodexDesktopProcessStatus.Running);
        fixture.AuthStore.Current = Auth("acct-a", "token-a");

        CodexDesktopSwitchService.OperationResult result = await fixture.Service.SwitchAsync(
            AccountB.AccountId,
            CancellationToken.None
        );

        Assert.Multiple(() => {
            Assert.That(result.Outcome, Is.EqualTo(CodexDesktopSwitchService.Outcome.TargetUnavailable));
            Assert.That(fixture.Process.CloseCallCount, Is.Zero);
        });
    }

    [Test]
    public async Task SwitchAsync_WrittenIdentityMismatch_RestoresOriginalLogin() {
        Fixture fixture = new(CodexDesktopProcessStatus.Running);
        fixture.AuthStore.Current = Auth("acct-a", "token-a");
        fixture.AuthStore.CorruptNextWrite = true;
        fixture.Snapshots.Saved[AccountB.AccountId] = Auth("acct-b", "token-b");

        CodexDesktopSwitchService.OperationResult result = await fixture.Service.SwitchAsync(
            AccountB.AccountId,
            CancellationToken.None
        );

        Assert.Multiple(() => {
            Assert.That(result.Outcome, Is.EqualTo(CodexDesktopSwitchService.Outcome.RolledBack));
            Assert.That(Text(fixture.AuthStore.Current), Is.EqualTo("acct-a|token-a"));
            Assert.That(fixture.Snapshots.Journal, Is.Null);
            Assert.That(fixture.Process.Launches, Is.EqualTo(new[] { Fixture.AppUserModelId }));
        });
    }

    [Test]
    public async Task SwitchAsync_DesktopRestartsDuringWrite_KeepsJournalForRecovery() {
        Fixture fixture = new(CodexDesktopProcessStatus.Running);
        fixture.AuthStore.Current = Auth("acct-a", "token-a");
        fixture.AuthStore.AfterReplace = () => fixture.Process.Status = CodexDesktopProcessStatus.Running;
        fixture.Snapshots.Saved[AccountB.AccountId] = Auth("acct-b", "token-b");

        CodexDesktopSwitchService.OperationResult result = await fixture.Service.SwitchAsync(
            AccountB.AccountId,
            CancellationToken.None
        );

        Assert.Multiple(() => {
            Assert.That(result.Outcome, Is.EqualTo(CodexDesktopSwitchService.Outcome.RecoveryRequired));
            Assert.That(fixture.Snapshots.Journal, Is.Not.Null);
            Assert.That(Text(fixture.Snapshots.Journal!.OriginalAuthContent), Is.EqualTo("acct-a|token-a"));
            Assert.That(fixture.Process.Launches, Is.Empty);
        });
    }

    [Test]
    public async Task RecoverAsync_StoppedDesktop_RestoresOriginalAndClearsJournal() {
        Fixture fixture = new(CodexDesktopProcessStatus.NotRunning);
        fixture.AuthStore.Current = Auth("acct-b", "token-b");
        fixture.Snapshots.Journal = new(
            Guid.NewGuid(),
            AccountA.AccountId,
            AccountB.AccountId,
            Auth("acct-a", "token-a"),
            true,
            Fixture.AppUserModelId,
            DateTimeOffset.Parse("2026-10-28T00:00:00Z")
        );

        CodexDesktopSwitchService.OperationResult result = await fixture.Service.RecoverAsync(CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(result.Outcome, Is.EqualTo(CodexDesktopSwitchService.Outcome.Succeeded));
            Assert.That(Text(fixture.AuthStore.Current), Is.EqualTo("acct-a|token-a"));
            Assert.That(fixture.Snapshots.Journal, Is.Null);
        });
    }

    [Test]
    public async Task RecoverAsync_RunningDesktop_KeepsJournal() {
        Fixture fixture = new(CodexDesktopProcessStatus.Running);
        fixture.AuthStore.Current = Auth("acct-b", "token-b");
        fixture.Snapshots.Journal = new(
            Guid.NewGuid(),
            AccountA.AccountId,
            AccountB.AccountId,
            Auth("acct-a", "token-a"),
            false,
            null,
            DateTimeOffset.Parse("2026-10-28T00:00:00Z")
        );

        CodexDesktopSwitchService.OperationResult result = await fixture.Service.RecoverAsync(CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(result.Outcome, Is.EqualTo(CodexDesktopSwitchService.Outcome.DesktopNotClosed));
            Assert.That(Text(fixture.AuthStore.Current), Is.EqualTo("acct-b|token-b"));
            Assert.That(fixture.Snapshots.Journal, Is.Not.Null);
        });
    }

    [Test]
    public async Task SwitchAsync_PendingJournal_RequiresRecoveryFirst() {
        Fixture fixture = new(CodexDesktopProcessStatus.NotRunning);
        fixture.AuthStore.Current = Auth("acct-a", "token-a");
        fixture.Snapshots.Saved[AccountB.AccountId] = Auth("acct-b", "token-b");
        fixture.Snapshots.Journal = new(
            Guid.NewGuid(),
            AccountA.AccountId,
            AccountB.AccountId,
            Auth("acct-a", "token-a"),
            false,
            null,
            DateTimeOffset.Parse("2026-10-28T00:00:00Z")
        );

        CodexDesktopSwitchService.OperationResult result = await fixture.Service.SwitchAsync(
            AccountB.AccountId,
            CancellationToken.None
        );

        Assert.That(result.Outcome, Is.EqualTo(CodexDesktopSwitchService.Outcome.RecoveryRequired));
        Assert.That(fixture.AuthStore.ReplaceCallCount, Is.Zero);
    }

    [Test]
    public async Task CaptureCurrentAsync_MatchedLogin_SavesSnapshotForThatAccount() {
        Fixture fixture = new(CodexDesktopProcessStatus.Running);
        fixture.AuthStore.Current = Auth("acct-b", "token-b");

        CodexDesktopSwitchService.OperationResult result = await fixture.Service.CaptureCurrentAsync(CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(result.Outcome, Is.EqualTo(CodexDesktopSwitchService.Outcome.Succeeded));
            Assert.That(result.AccountId, Is.EqualTo(AccountB.AccountId));
            Assert.That(Text(fixture.Snapshots.Saved[AccountB.AccountId]), Is.EqualTo("acct-b|token-b"));
        });
    }

    [Test]
    public async Task CaptureCurrentAsync_UnmatchedLogin_DoesNotSave() {
        Fixture fixture = new(CodexDesktopProcessStatus.NotRunning);
        fixture.AuthStore.Current = Auth("acct-unknown", "token-x");

        CodexDesktopSwitchService.OperationResult result = await fixture.Service.CaptureCurrentAsync(CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(result.Outcome, Is.EqualTo(CodexDesktopSwitchService.Outcome.CurrentLoginNotSaved));
            Assert.That(fixture.Snapshots.Saved, Is.Empty);
        });
    }

    [Test]
    public async Task GetStateAsync_RemovesSnapshotsOfAccountsNoLongerInCatalog() {
        Fixture fixture = new(CodexDesktopProcessStatus.NotRunning);
        Guid removedAccountId = Guid.NewGuid();
        fixture.Snapshots.Saved[AccountA.AccountId] = Auth("acct-a", "token-a");
        fixture.Snapshots.Saved[removedAccountId] = Auth("acct-removed", "token-r");

        CodexDesktopSwitchService.SwitchState state = await fixture.Service.GetStateAsync(CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(state.AccountsWithSnapshot, Is.EquivalentTo(new[] { AccountA.AccountId }));
            Assert.That(state.RecoveryRequired, Is.False);
            Assert.That(fixture.Snapshots.Saved.ContainsKey(removedAccountId), Is.False);
        });
    }

    private static CodexAccountProfile CreateAccount(string externalAccountId, string alias) {
        return new(
            Guid.NewGuid(),
            alias,
            alias.ToLowerInvariant() + "@example.test",
            "id:" + externalAccountId,
            1,
            DateTimeOffset.Parse("2026-10-20T00:00:00Z"),
            "active",
            externalAccountId
        );
    }

    private static byte[] Auth(string accountId, string token) {
        return Encoding.UTF8.GetBytes(accountId + "|" + token);
    }

    private static string? Text(byte[]? content) {
        return content is null ? null : Encoding.UTF8.GetString(content);
    }

    private sealed class Fixture {
        public const string AppUserModelId = "OpenAI.Codex_fixture!App";

        public Fixture(CodexDesktopProcessStatus status) {
            Process = new() { Status = status };
            Service = new(
                new FakeCatalog(AccountA, AccountB),
                Snapshots,
                AuthStore,
                Process,
                new FakeFingerprinter(),
                new FakeClock()
            );
        }

        public FakeSnapshots Snapshots { get; } = new();

        public FakeAuthStore AuthStore { get; } = new();

        public FakeProcessController Process { get; }

        public CodexDesktopSwitchService Service { get; }
    }

    private sealed class FakeCatalog(params CodexAccountProfile[] accounts) : IAccountCatalogRepository {
        public Task<IAccountCatalogRepository.Snapshot> LoadAsync(CancellationToken cancellationToken) {
            return Task.FromResult(new IAccountCatalogRepository.Snapshot(accounts, new string('a', 64)));
        }

        public Task<IAccountCatalogRepository.SaveResult> SaveAsync(
            IReadOnlyList<CodexAccountProfile> updatedAccounts,
            string expectedSha256,
            CancellationToken cancellationToken
        ) {
            throw new NotSupportedException("The switch service does not modify the Codex catalog.");
        }
    }

    private sealed class FakeSnapshots : ICodexDesktopSnapshotRepository {
        public Dictionary<Guid, byte[]> Saved { get; } = [];

        public ICodexDesktopSnapshotRepository.SwitchJournal? Journal { get; set; }

        public Task<IReadOnlySet<Guid>> ListAsync(CancellationToken cancellationToken) {
            return Task.FromResult<IReadOnlySet<Guid>>(Saved.Keys.ToHashSet());
        }

        public Task<byte[]?> LoadAsync(Guid accountId, CancellationToken cancellationToken) {
            return Task.FromResult(Saved.TryGetValue(accountId, out byte[]? content) ? content.ToArray() : null);
        }

        public Task SaveAsync(Guid accountId, byte[] content, CancellationToken cancellationToken) {
            Saved[accountId] = content.ToArray();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid accountId, CancellationToken cancellationToken) {
            Saved.Remove(accountId);
            return Task.CompletedTask;
        }

        public Task<ICodexDesktopSnapshotRepository.SwitchJournal?> LoadJournalAsync(CancellationToken cancellationToken) {
            return Task.FromResult(Journal is null ? null : Journal with {
                OriginalAuthContent = Journal.OriginalAuthContent.ToArray()
            });
        }

        public Task SaveJournalAsync(ICodexDesktopSnapshotRepository.SwitchJournal journal, CancellationToken cancellationToken) {
            Journal = journal with { OriginalAuthContent = journal.OriginalAuthContent.ToArray() };
            return Task.CompletedTask;
        }

        public Task DeleteJournalAsync(CancellationToken cancellationToken) {
            Journal = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuthStore : ICodexDesktopAuthStore {
        public byte[]? Current { get; set; }

        public bool CorruptNextWrite { get; set; }

        public Action? AfterReplace { get; set; }

        public int ReplaceCallCount { get; private set; }

        public Task<ICodexDesktopAuthStore.ReadResult> ReadAsync(CancellationToken cancellationToken) {
            if (Current is null) {
                return Task.FromResult(new ICodexDesktopAuthStore.ReadResult(CodexLocalAuthReadStatus.FileMissing, null, null));
            }

            CodexLocalAuthReadResult parsed = Parse(Current);
            return Task.FromResult(new ICodexDesktopAuthStore.ReadResult(parsed.Status, Current.ToArray(), parsed.Identity));
        }

        public CodexLocalAuthReadResult Parse(byte[] content) {
            string[] parts = Encoding.UTF8.GetString(content).Split('|');
            return parts.Length == 2
                ? new(CodexLocalAuthReadStatus.Success, new CodexLocalAuthIdentity(parts[0], null))
                : new(CodexLocalAuthReadStatus.InvalidFormat, null);
        }

        public Task ReplaceAsync(byte[] content, CancellationToken cancellationToken) {
            ReplaceCallCount++;
            Current = CorruptNextWrite ? Auth("acct-other", "token-o") : content.ToArray();
            CorruptNextWrite = false;
            Action? afterReplace = AfterReplace;
            AfterReplace = null;
            afterReplace?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProcessController : ICodexDesktopProcessController {
        public CodexDesktopProcessStatus Status { get; set; }

        public bool CloseSucceeds { get; set; } = true;

        public int CloseCallCount { get; private set; }

        public List<string> Launches { get; } = [];

        public Task<CodexDesktopProcessStatus> GetStatusAsync(CancellationToken cancellationToken) {
            return Task.FromResult(Status);
        }

        public Task<ICodexDesktopProcessController.CloseResult> CloseGracefullyAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken
        ) {
            CloseCallCount++;
            if (CloseSucceeds) {
                Status = CodexDesktopProcessStatus.NotRunning;
            }

            return Task.FromResult(new ICodexDesktopProcessController.CloseResult(CloseSucceeds, Fixture.AppUserModelId));
        }

        public Task LaunchAsync(string appUserModelId, CancellationToken cancellationToken) {
            Launches.Add(appUserModelId);
            Status = CodexDesktopProcessStatus.Running;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFingerprinter : IIdentityFingerprinter {
        public string CreateFingerprint(string? accountId, string? email, string authMode) {
            return !string.IsNullOrWhiteSpace(accountId)
                ? "id:" + accountId.Trim()
                : "email:" + (email ?? "").Trim().ToLowerInvariant();
        }

        public string MaskEmail(string? email) {
            return "masked@example.test";
        }
    }

    private sealed class FakeClock : IClock {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.Parse("2026-10-28T00:00:00Z");

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) {
            return Task.Delay(delay, cancellationToken);
        }
    }
}
