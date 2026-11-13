using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Application.Operations;
using CloudyWing.AiAccountManager.Application.Runtime;
using CloudyWing.AiAccountManager.Application.Security;
using CloudyWing.AiAccountManager.Windows.Codex;
using CloudyWing.AiAccountManager.Windows.Storage;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Codex;

[TestFixture]
public sealed class CodexProfileServiceTests {
    internal static CodexProfileService CreateRenderingService() => new Fixture().Service;

    [Test]
    public async Task MoveAsync_AccountIdMovesRequestedProfileAndPreservesProfileData() {
        Fixture fixture = new();
        CodexAccountProfile first = CreateProfile(Guid.NewGuid(), "first", "first@example.test");
        CodexAccountProfile second = CreateProfile(Guid.NewGuid(), "second", "second@example.test");
        CodexAccountProfile third = CreateProfile(Guid.NewGuid(), "third", "third@example.test");
        fixture.Catalog.Seed(first, second, third);

        await fixture.Service.MoveAsync(third.AccountId, CodexAccountMoveDirection.Up, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { first, third, second }, fixture.Catalog.Accounts);
        using (Assert.EnterMultipleScope()) {
            Assert.That(fixture.Catalog.Accounts[1].AccountId, Is.EqualTo(third.AccountId));
            Assert.That(fixture.Catalog.Accounts[1].ProfileRevision, Is.EqualTo(third.ProfileRevision));
            Assert.That(fixture.Catalog.Accounts[1].IdentityFingerprint, Is.EqualTo(third.IdentityFingerprint));
        }
    }

    [Test]
    public async Task MoveAsync_AtBoundary_DoesNotWriteCatalog() {
        Fixture fixture = new();
        CodexAccountProfile first = CreateProfile(Guid.NewGuid(), "first", "first@example.test");
        CodexAccountProfile second = CreateProfile(Guid.NewGuid(), "second", "second@example.test");
        fixture.Catalog.Seed(first, second);
        string originalCatalogHash = fixture.Catalog.CurrentSha256;

        await fixture.Service.MoveAsync(first.AccountId, CodexAccountMoveDirection.Up, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { first, second }, fixture.Catalog.Accounts);
        Assert.That(fixture.Catalog.CurrentSha256, Is.EqualTo(originalCatalogHash));
    }

    [Test]
    public async Task MoveAsync_SaveConflict_DoesNotPersistTheRequestedOrder() {
        Fixture fixture = new();
        CodexAccountProfile first = CreateProfile(Guid.NewGuid(), "first", "first@example.test");
        CodexAccountProfile second = CreateProfile(Guid.NewGuid(), "second", "second@example.test");
        fixture.Catalog.Seed(first, second);
        fixture.Catalog.ConflictNextSave = true;

        CodexProfileOperationException? exception = await Assert.ThrowsAsync<CodexProfileOperationException>(
            async () => await fixture.Service.MoveAsync(second.AccountId, CodexAccountMoveDirection.Up, CancellationToken.None)
        );

        CollectionAssert.AreEqual(new[] { first, second }, fixture.Catalog.Accounts);
        Assert.That(exception?.Message, Does.Contain("未保存排序"));
    }

    [Test]
    public async Task OnlyCompletedIdentityBoundOAuthCommitsProfile() {
        Fixture fixture = new();

        CodexAccountProfile profile = await fixture.Service.AddAsync("  Work account  ", CancellationToken.None);

        Assert.That(fixture.Catalog.Accounts, Has.Count.EqualTo(1));
        Assert.That(profile.Alias, Is.EqualTo("Work account"));
        Assert.That(profile.AccountId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(profile.ExternalAccountId, Is.EqualTo("chatgpt-account-123"));
        Assert.That(profile.IdentityFingerprint, Is.EqualTo("fingerprint:chatgpt-account-123"));
        Assert.That(profile.MaskedIdentity, Is.EqualTo("p•••@example.test"));
        Assert.That(fixture.Storage.Homes, Does.Contain(profile.AccountId));
        Assert.That(fixture.Session.LoginStarted, Is.True);
        Assert.That(fixture.Session.LoggedOut, Is.False);
        Assert.That(fixture.Browser.OpenedUri?.Scheme, Is.EqualTo(Uri.UriSchemeHttps));
        Assert.That(fixture.Storage.Transactions, Is.Empty);
    }

    [Test]
    public async Task AddAsync_AppServerMissing_ShowsSpecificReason() {
        Fixture fixture = new();
        fixture.SessionFactory.Failure = new FileNotFoundException("Sensitive app-server path.");

        CodexProfileOperationException? exception = await Assert.ThrowsAsync<CodexProfileOperationException>(
            async () => await fixture.Service.AddAsync(null, CancellationToken.None)
        );

        Assert.That(
            exception?.Message,
            Is.EqualTo("找不到或無法啟動 Codex，帳號未保存，請確認已安裝 Codex。")
        );
    }

    [Test]
    public async Task AddAsync_UnknownAppServerFailure_ShowsGenericMessageWithoutExceptionType() {
        Fixture fixture = new();
        fixture.SessionFactory.Failure = new InvalidOperationException("Sensitive app-server response.");

        CodexProfileOperationException? exception = await Assert.ThrowsAsync<CodexProfileOperationException>(
            async () => await fixture.Service.AddAsync(null, CancellationToken.None)
        );

        Assert.That(exception?.Message, Is.EqualTo("無法完成 Codex 登入，帳號未保存，請重試。"));
        Assert.That(exception?.Message, Does.Not.Contain("InvalidOperationException"));
    }

    [Test]
    public async Task CanceledOAuthDoesNotCommitAndClearsPendingHomeAndKeyring() {
        using CancellationTokenSource cancellation = new();
        Fixture fixture = new();
        fixture.Browser.AfterOpen = cancellation.Cancel;

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await fixture.Service.AddAsync(
            null,
            cancellation.Token
        ));

        Assert.That(fixture.Catalog.Accounts, Is.Empty);
        Assert.That(fixture.Storage.Homes, Is.Empty);
        Assert.That(fixture.Session.LoginCancelled, Is.True);
        Assert.That(fixture.Session.LoggedOut, Is.True);
        Assert.That(fixture.Storage.Transactions, Is.Empty);
    }

    [Test]
    public async Task IdentityMismatchDoesNotCommitProfile() {
        Fixture fixture = new();
        fixture.Session.RateLimitAccountId = "different-chatgpt-account";

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.AddAsync(
            null,
            CancellationToken.None
        ));

        Assert.That(fixture.Catalog.Accounts, Is.Empty);
        Assert.That(fixture.Storage.Homes, Is.Empty);
        Assert.That(fixture.Session.LoggedOut, Is.True);
        Assert.That(fixture.Storage.Transactions, Is.Empty);
    }

    [Test]
    public async Task KeyringCleanupFailureLeavesHomeAndPendingJournalForRetry() {
        Fixture fixture = new();
        fixture.Session.RateLimitAccountId = "different-chatgpt-account";
        fixture.Session.FailLogout = true;

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.AddAsync(
            null,
            CancellationToken.None
        ));

        Assert.That(fixture.Catalog.Accounts, Is.Empty);
        Assert.That(fixture.Storage.Homes, Has.Count.EqualTo(1));
        Assert.That(fixture.Storage.Transactions, Has.Count.EqualTo(1));
        Assert.That(fixture.Storage.Transactions.Values.Single().Stage, Is.EqualTo("cleanup-pending"));
    }

    [Test]
    public async Task FailedAddCleanupIsRetriedBeforeStartingAnotherOAuth() {
        Fixture fixture = new();
        fixture.Session.RateLimitAccountId = "different-chatgpt-account";
        fixture.Session.FailLogout = true;

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.AddAsync(
            null,
            CancellationToken.None
        ));

        Guid abandonedAccountId = fixture.Storage.Transactions.Values.Single().AccountId;
        fixture.Session.FailLogout = false;
        fixture.Session.RateLimitAccountId = "chatgpt-account-123";
        CodexAccountProfile profile = await fixture.Service.AddAsync("Next account", CancellationToken.None);

        Assert.That(fixture.Catalog.Accounts, Is.EqualTo(new[] { profile }));
        Assert.That(fixture.Storage.Homes, Does.Not.Contain(abandonedAccountId));
        Assert.That(fixture.Storage.Transactions.Values.Any(transaction => transaction.AccountId == abandonedAccountId), Is.False);
        Assert.That(fixture.SessionFactory.CreatedAccountIds, Is.EqualTo(new[] { abandonedAccountId, abandonedAccountId, profile.AccountId }));
        Assert.That(fixture.Session.LoginStartCount, Is.EqualTo(2));
        Assert.That(fixture.Session.LogoutAttempts, Is.EqualTo(2));
    }

    [Test]
    public async Task FailedPendingAddCleanupKeepsJournalAndDoesNotStartAnotherOAuth() {
        Fixture fixture = new();
        fixture.Session.RateLimitAccountId = "different-chatgpt-account";
        fixture.Session.FailLogout = true;

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.AddAsync(
            null,
            CancellationToken.None
        ));
        Guid abandonedAccountId = fixture.Storage.Transactions.Values.Single().AccountId;

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.AddAsync(
            null,
            CancellationToken.None
        ));

        Assert.That(fixture.Catalog.Accounts, Is.Empty);
        Assert.That(fixture.Storage.Homes, Does.Contain(abandonedAccountId));
        Assert.That(fixture.Storage.Transactions.Values.Single().Stage, Is.EqualTo("cleanup-pending"));
        Assert.That(fixture.SessionFactory.CreatedAccountIds, Is.EqualTo(new[] { abandonedAccountId, abandonedAccountId }));
        Assert.That(fixture.Session.LoginStartCount, Is.EqualTo(1));
    }

    [Test]
    public async Task PendingAddCleanupRetriesHomeRemovalWithoutLoggingOutAgain() {
        Fixture fixture = new();
        fixture.Session.RateLimitAccountId = "different-chatgpt-account";
        fixture.Session.FailLogout = true;

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.AddAsync(
            null,
            CancellationToken.None
        ));
        Guid abandonedAccountId = fixture.Storage.Transactions.Values.Single().AccountId;
        fixture.Session.FailLogout = false;
        fixture.Storage.FailNextRemoveHome = true;

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.AddAsync(
            null,
            CancellationToken.None
        ));

        Assert.That(fixture.Storage.Homes, Does.Contain(abandonedAccountId));
        Assert.That(fixture.Storage.Transactions.Values.Single().Stage, Is.EqualTo("keyring-cleared"));

        fixture.Session.RateLimitAccountId = "chatgpt-account-123";
        CodexAccountProfile profile = await fixture.Service.AddAsync(null, CancellationToken.None);

        Assert.That(fixture.Storage.Homes, Does.Not.Contain(abandonedAccountId));
        Assert.That(fixture.Storage.Transactions.Values.Any(transaction => transaction.AccountId == abandonedAccountId), Is.False);
        Assert.That(fixture.SessionFactory.CreatedAccountIds, Is.EqualTo(new[] { abandonedAccountId, abandonedAccountId, profile.AccountId }));
        Assert.That(fixture.Session.LoginStartCount, Is.EqualTo(2));
        Assert.That(fixture.Session.LogoutAttempts, Is.EqualTo(2));
    }

    [Test]
    public async Task PendingAddCleanupRetriesAfterHomeRemovedJournalWriteFails() {
        Fixture fixture = new();
        fixture.Session.RateLimitAccountId = "different-chatgpt-account";
        fixture.Session.FailLogout = true;

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.AddAsync(
            null,
            CancellationToken.None
        ));
        Guid abandonedAccountId = fixture.Storage.Transactions.Values.Single().AccountId;
        fixture.Session.FailLogout = false;
        fixture.Storage.FailNextStageWrite = "home-removed";

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.AddAsync(
            null,
            CancellationToken.None
        ));

        Assert.That(fixture.Storage.Homes, Does.Not.Contain(abandonedAccountId));
        Assert.That(fixture.Storage.Transactions.Values.Single().Stage, Is.EqualTo("keyring-cleared"));

        fixture.Session.RateLimitAccountId = "chatgpt-account-123";
        CodexAccountProfile profile = await fixture.Service.AddAsync(null, CancellationToken.None);

        Assert.That(fixture.Storage.Homes, Does.Not.Contain(abandonedAccountId));
        Assert.That(fixture.Storage.Transactions.Values.Any(transaction => transaction.AccountId == abandonedAccountId), Is.False);
        Assert.That(fixture.SessionFactory.CreatedAccountIds, Is.EqualTo(new[] { abandonedAccountId, abandonedAccountId, profile.AccountId }));
        Assert.That(fixture.Session.LoginStartCount, Is.EqualTo(2));
        Assert.That(fixture.Session.LogoutAttempts, Is.EqualTo(2));
    }

    [Test]
    public async Task PendingAddCleanupRetriesAfterJournalDeletionFails() {
        Fixture fixture = new();
        fixture.Session.RateLimitAccountId = "different-chatgpt-account";
        fixture.Session.FailLogout = true;

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.AddAsync(
            null,
            CancellationToken.None
        ));
        Guid abandonedAccountId = fixture.Storage.Transactions.Values.Single().AccountId;
        fixture.Session.FailLogout = false;
        fixture.Storage.FailNextTransactionDelete = true;

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.AddAsync(
            null,
            CancellationToken.None
        ));

        Assert.That(fixture.Storage.Homes, Does.Not.Contain(abandonedAccountId));
        Assert.That(fixture.Storage.Transactions.Values.Single().Stage, Is.EqualTo("home-removed"));

        fixture.Session.RateLimitAccountId = "chatgpt-account-123";
        CodexAccountProfile profile = await fixture.Service.AddAsync(null, CancellationToken.None);

        Assert.That(fixture.Storage.Transactions.Values.Any(transaction => transaction.AccountId == abandonedAccountId), Is.False);
        Assert.That(fixture.SessionFactory.CreatedAccountIds, Is.EqualTo(new[] { abandonedAccountId, abandonedAccountId, profile.AccountId }));
        Assert.That(fixture.Session.LoginStartCount, Is.EqualTo(2));
        Assert.That(fixture.Session.LogoutAttempts, Is.EqualTo(2));
    }

    [TestCase("started", false, false)]
    [TestCase("started", true, false)]
    [TestCase("home-created", true, true)]
    [TestCase("identity-confirmed", true, true)]
    public async Task PendingAddRecoveryUsesStageBeforeStartingLogout(
        string stage,
        bool homeExists,
        bool expectsLogout
    ) {
        Fixture fixture = new();
        Guid abandonedAccountId = Guid.NewGuid();
        if (homeExists) {
            fixture.Storage.Homes.Add(abandonedAccountId);
        }

        CodexProfileTransaction transaction = new(
            Guid.NewGuid(),
            abandonedAccountId,
            "add",
            stage,
            fixture.Catalog.CurrentSha256,
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
            null
        );
        fixture.Storage.Transactions.Add(transaction.TransactionId, transaction);

        CodexAccountProfile profile = await fixture.Service.AddAsync(null, CancellationToken.None);

        Assert.That(fixture.Catalog.Accounts, Is.EqualTo(new[] { profile }));
        Assert.That(fixture.Storage.Homes, Does.Not.Contain(abandonedAccountId));
        Assert.That(fixture.Storage.Transactions, Does.Not.ContainKey(transaction.TransactionId));
        Assert.That(fixture.SessionFactory.CreatedAccountIds.Contains(abandonedAccountId), Is.EqualTo(expectsLogout));
        Assert.That(fixture.Session.LogoutAttempts, Is.EqualTo(expectsLogout ? 1 : 0));
        Assert.That(fixture.Session.LoginStartCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CatalogCommittedAddRecoveryRetiresOnlyJournalForExistingProfile() {
        Fixture fixture = new();
        CodexAccountProfile existingProfile = CreateProfile();
        fixture.Catalog.Seed(existingProfile);
        fixture.Storage.Homes.Add(existingProfile.AccountId);
        fixture.Storage.AuthFiles.Add(existingProfile.AccountId);
        CodexProfileTransaction transaction = new(
            Guid.NewGuid(),
            existingProfile.AccountId,
            "add",
            "catalog-committed",
            fixture.Catalog.CurrentSha256,
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
            null
        );
        fixture.Storage.Transactions.Add(transaction.TransactionId, transaction);

        CodexAccountProfile addedProfile = await fixture.Service.AddAsync(null, CancellationToken.None);

        Assert.That(fixture.Catalog.Accounts, Is.EquivalentTo(new[] { existingProfile, addedProfile }));
        Assert.That(fixture.Storage.Homes, Does.Contain(existingProfile.AccountId));
        Assert.That(fixture.Storage.AuthFiles, Does.Contain(existingProfile.AccountId));
        Assert.That(fixture.Storage.Transactions, Does.Not.ContainKey(transaction.TransactionId));
        Assert.That(fixture.SessionFactory.CreatedAccountIds, Is.EqualTo(new[] { addedProfile.AccountId }));
        Assert.That(fixture.Session.LogoutAttempts, Is.Zero);
    }

    [Test]
    public async Task CatalogCommittedAddJournalWithoutCatalogProfileFailsClosed() {
        Fixture fixture = new();
        Guid accountId = Guid.NewGuid();
        fixture.Storage.Homes.Add(accountId);
        fixture.Storage.AuthFiles.Add(accountId);
        CodexProfileTransaction transaction = new(
            Guid.NewGuid(),
            accountId,
            "add",
            "catalog-committed",
            fixture.Catalog.CurrentSha256,
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
            null
        );
        fixture.Storage.Transactions.Add(transaction.TransactionId, transaction);

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.AddAsync(
            null,
            CancellationToken.None
        ));

        Assert.That(fixture.Catalog.Accounts, Is.Empty);
        Assert.That(fixture.Storage.Homes, Does.Contain(accountId));
        Assert.That(fixture.Storage.AuthFiles, Does.Contain(accountId));
        Assert.That(fixture.Storage.Transactions, Contains.Key(transaction.TransactionId));
        Assert.That(fixture.SessionFactory.CreateCalls, Is.Zero);
    }

    [Test]
    public async Task PendingAddCleanupDoesNotRemoveAccountAlreadyInCatalog() {
        Fixture fixture = new();
        CodexAccountProfile profile = CreateProfile();
        fixture.Catalog.Seed(profile);
        fixture.Storage.Homes.Add(profile.AccountId);
        CodexProfileTransaction transaction = new(
            Guid.NewGuid(),
            profile.AccountId,
            "add",
            "cleanup-pending",
            fixture.Catalog.CurrentSha256,
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
            "IOException"
        );
        fixture.Storage.Transactions.Add(transaction.TransactionId, transaction);

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.AddAsync(
            null,
            CancellationToken.None
        ));

        Assert.That(fixture.Catalog.Accounts, Is.EqualTo(new[] { profile }));
        Assert.That(fixture.Storage.Homes, Does.Contain(profile.AccountId));
        Assert.That(fixture.Storage.Transactions, Contains.Key(transaction.TransactionId));
        Assert.That(fixture.SessionFactory.CreateCalls, Is.Zero);
        Assert.That(fixture.Session.LoginStartCount, Is.Zero);
    }

    [Test]
    public async Task CatalogConflictAfterHomeRemovalCanBeRetriedFromTrustedJournal() {
        Fixture fixture = new();
        CodexAccountProfile profile = CreateProfile();
        fixture.Catalog.Seed(profile);
        fixture.Storage.Homes.Add(profile.AccountId);
        fixture.Catalog.ConflictNextSave = true;

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.RemoveAsync(
            profile.AccountId,
            CancellationToken.None
        ));

        Assert.That(fixture.Catalog.Accounts, Has.Count.EqualTo(1));
        Assert.That(fixture.Storage.Homes, Does.Not.Contain(profile.AccountId));
        Assert.That(fixture.Storage.Transactions.Values.Single().Stage, Is.EqualTo("catalog-conflict"));
        Assert.That(fixture.SessionFactory.CreateCalls, Is.EqualTo(1));

        await fixture.Service.RemoveAsync(profile.AccountId, CancellationToken.None);

        Assert.That(fixture.Catalog.Accounts, Is.Empty);
        Assert.That(fixture.Storage.Transactions, Is.Empty);
        Assert.That(fixture.SessionFactory.CreateCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task MissingProfileWithHomeRemovedJournalDeletesOnlyJournal() {
        Fixture fixture = new();
        Guid accountId = Guid.NewGuid();
        CodexProfileTransaction transaction = new(
            Guid.NewGuid(),
            accountId,
            "remove",
            "home-removed",
            fixture.Catalog.CurrentSha256,
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
            null
        );
        fixture.Storage.Transactions.Add(transaction.TransactionId, transaction);

        await fixture.Service.RemoveAsync(accountId, CancellationToken.None);

        Assert.That(fixture.Storage.Transactions, Does.Not.ContainKey(transaction.TransactionId));
        Assert.That(fixture.Storage.RemoveHomeCalls, Is.Zero);
        Assert.That(fixture.Storage.RemoveAuthFileCalls, Is.Zero);
        Assert.That(fixture.SessionFactory.CreateCalls, Is.Zero);
    }

    [Test]
    public async Task MissingProfileWithEarlierRemovalJournalStagePreservesJournalAndHome() {
        Fixture fixture = new();
        Guid accountId = Guid.NewGuid();
        fixture.Storage.Homes.Add(accountId);
        CodexProfileTransaction transaction = new(
            Guid.NewGuid(),
            accountId,
            "remove",
            "cleanup-pending",
            fixture.Catalog.CurrentSha256,
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
            null
        );
        fixture.Storage.Transactions.Add(transaction.TransactionId, transaction);

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.RemoveAsync(
            accountId,
            CancellationToken.None
        ));

        Assert.That(fixture.Storage.Transactions, Contains.Key(transaction.TransactionId));
        Assert.That(fixture.Storage.Homes, Does.Contain(accountId));
        Assert.That(fixture.Storage.RemoveHomeCalls, Is.Zero);
        Assert.That(fixture.SessionFactory.CreateCalls, Is.Zero);
    }

    [Test]
    public async Task FailedHomeRemovedJournalWriteCanBeRetriedFromKeyringClearedStage() {
        Fixture fixture = new();
        CodexAccountProfile profile = CreateProfile();
        fixture.Catalog.Seed(profile);
        fixture.Storage.Homes.Add(profile.AccountId);
        fixture.Storage.FailNextStageWrite = "home-removed";

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.RemoveAsync(
            profile.AccountId,
            CancellationToken.None
        ));

        Assert.That(fixture.Catalog.Accounts, Has.Count.EqualTo(1));
        Assert.That(fixture.Storage.Homes, Does.Not.Contain(profile.AccountId));
        Assert.That(fixture.Storage.Transactions.Values.Single().Stage, Is.EqualTo("keyring-cleared"));

        await fixture.Service.RemoveAsync(profile.AccountId, CancellationToken.None);

        Assert.That(fixture.Catalog.Accounts, Is.Empty);
        Assert.That(fixture.Storage.Transactions, Is.Empty);
        Assert.That(fixture.SessionFactory.CreateCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task KeyringLogoutFailureKeepsHomeAndCatalog() {
        Fixture fixture = new();
        CodexAccountProfile profile = CreateProfile();
        fixture.Catalog.Seed(profile);
        fixture.Storage.Homes.Add(profile.AccountId);
        fixture.Session.FailLogout = true;

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.RemoveAsync(
            profile.AccountId,
            CancellationToken.None
        ));

        Assert.That(fixture.Catalog.Accounts, Has.Count.EqualTo(1));
        Assert.That(fixture.Storage.Homes, Does.Contain(profile.AccountId));
        Assert.That(fixture.Storage.Transactions.Values.Single().Stage, Is.EqualTo("cleanup-pending"));
    }

    [Test]
    public async Task StartedRemovalJournalDoesNotSkipLogout() {
        Fixture fixture = new();
        CodexAccountProfile profile = CreateProfile();
        fixture.Catalog.Seed(profile);
        fixture.Storage.Homes.Add(profile.AccountId);
        CodexProfileTransaction transaction = new(
            Guid.NewGuid(),
            profile.AccountId,
            "remove",
            "started",
            fixture.Catalog.CurrentSha256,
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
            null
        );
        fixture.Storage.Transactions.Add(transaction.TransactionId, transaction);
        fixture.Session.FailLogout = true;

        await Assert.ThrowsAsync<CodexProfileOperationException>(async () => await fixture.Service.RemoveAsync(
            profile.AccountId,
            CancellationToken.None
        ));

        Assert.That(fixture.SessionFactory.CreateCalls, Is.EqualTo(1));
        Assert.That(fixture.Session.LoggedOut, Is.False);
        Assert.That(fixture.Catalog.Accounts, Has.Count.EqualTo(1));
        Assert.That(fixture.Storage.Homes, Does.Contain(profile.AccountId));
    }

    [Test]
    public async Task UnknownRemovalJournalStageFailsBeforeCleanup() {
        Fixture fixture = new();
        CodexAccountProfile profile = CreateProfile();
        fixture.Catalog.Seed(profile);
        fixture.Storage.Homes.Add(profile.AccountId);
        CodexProfileTransaction transaction = new(
            Guid.NewGuid(),
            profile.AccountId,
            "remove",
            "unrecognized-stage",
            fixture.Catalog.CurrentSha256,
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
            null
        );
        fixture.Storage.Transactions.Add(transaction.TransactionId, transaction);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Service.RemoveAsync(
            profile.AccountId,
            CancellationToken.None
        ));

        Assert.That(fixture.SessionFactory.CreateCalls, Is.Zero);
        Assert.That(fixture.Catalog.Accounts, Has.Count.EqualTo(1));
        Assert.That(fixture.Storage.Homes, Does.Contain(profile.AccountId));
    }

    [Test]
    public async Task WindowsStorageRoundTripsExistingProfileTransactionSchema() {
        string dataPath = Path.Combine(Path.GetTempPath(), "AiAccountManagerTests", Guid.NewGuid().ToString("N"));
        CurrentUserDataDirectory dataDirectory = new(dataPath);
        WindowsCodexProfileStorage storage = new(dataDirectory, new AtomicJsonFileStore(dataDirectory));
        CodexProfileTransaction transaction = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "remove",
            "keyring-cleared",
            "catalog-sha256",
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
            null
        );

        try {
            await storage.SaveTransactionAsync(transaction, CancellationToken.None);
            CodexProfileTransaction? restored = await storage.LoadPendingRemovalTransactionAsync(
                transaction.AccountId,
                CancellationToken.None
            );
            string jsonPath = Path.Combine(dataPath, "transactions", transaction.TransactionId.ToString("N") + ".json");
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllBytesAsync(jsonPath));

            Assert.That(restored, Is.EqualTo(transaction));
            Assert.That(document.RootElement.TryGetProperty("transactionId", out _), Is.True);
            Assert.That(document.RootElement.TryGetProperty("accountId", out _), Is.True);
            Assert.That(document.RootElement.TryGetProperty("operation", out _), Is.True);
            Assert.That(document.RootElement.TryGetProperty("stage", out _), Is.True);
            Assert.That(document.RootElement.TryGetProperty("expectedCatalogSha256", out _), Is.True);
            Assert.That(document.RootElement.TryGetProperty("updatedAtUtc", out _), Is.True);
            Assert.That(document.RootElement.TryGetProperty("cleanupResult", out _), Is.True);

            CodexProfileTransaction unknownStage = transaction with {
                TransactionId = Guid.NewGuid(),
                Stage = "unrecognized-stage"
            };
            string unknownPath = Path.Combine(
                dataPath,
                "transactions",
                unknownStage.TransactionId.ToString("N") + ".json"
            );
            byte[] unknownContent = JsonSerializer.SerializeToUtf8Bytes(
                unknownStage,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            );
            await File.WriteAllBytesAsync(unknownPath, unknownContent);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await storage.LoadPendingRemovalTransactionAsync(
                transaction.AccountId,
                CancellationToken.None
            ));
        } finally {
            if (Directory.Exists(dataPath)) {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    [Test]
    public async Task WindowsStorage_RemoveHomeAsync_DeletesNestedReadOnlyFilesWithinSelectedHome() {
        string dataPath = Path.Combine(Path.GetTempPath(), "AiAccountManagerTests", Guid.NewGuid().ToString("N"));
        CurrentUserDataDirectory dataDirectory = new(dataPath);
        WindowsCodexProfileStorage storage = new(dataDirectory, new AtomicJsonFileStore(dataDirectory));
        Guid selectedAccountId = Guid.NewGuid();
        Guid otherAccountId = Guid.NewGuid();
        string selectedHomePath = storage.GetHomePath(selectedAccountId);
        string otherHomePath = storage.GetHomePath(otherAccountId);
        string selectedReadOnlyFilePath = Path.Combine(
            selectedHomePath,
            "plugins",
            "clone",
            ".git",
            "objects",
            "pack",
            "tmp_pack_selected"
        );
        string otherProfileReadOnlyFilePath = Path.Combine(
            otherHomePath,
            "plugins",
            "clone",
            ".git",
            "objects",
            "pack",
            "tmp_pack_other"
        );
        string outsideReadOnlyFilePath = Path.Combine(dataPath, "outside-profile.tmp");
        string[] readOnlyFilePaths = [
            selectedReadOnlyFilePath,
            otherProfileReadOnlyFilePath,
            outsideReadOnlyFilePath
        ];

        try {
            await storage.CreateHomeAsync(selectedAccountId, CancellationToken.None);
            await storage.CreateHomeAsync(otherAccountId, CancellationToken.None);
            foreach (string filePath in readOnlyFilePaths) {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                await File.WriteAllTextAsync(filePath, "temporary profile cleanup data");
                FileAttributes attributes = File.GetAttributes(filePath);
                File.SetAttributes(filePath, attributes | FileAttributes.ReadOnly);
            }

            FileAttributes otherProfileAttributes = File.GetAttributes(otherProfileReadOnlyFilePath);
            FileAttributes outsideAttributes = File.GetAttributes(outsideReadOnlyFilePath);
            await storage.RemoveHomeAsync(selectedAccountId, CancellationToken.None);

            Assert.That(Directory.Exists(selectedHomePath), Is.False);
            Assert.That(File.Exists(otherProfileReadOnlyFilePath), Is.True);
            Assert.That(File.GetAttributes(otherProfileReadOnlyFilePath), Is.EqualTo(otherProfileAttributes));
            Assert.That(File.Exists(outsideReadOnlyFilePath), Is.True);
            Assert.That(File.GetAttributes(outsideReadOnlyFilePath), Is.EqualTo(outsideAttributes));
        } finally {
            foreach (string filePath in readOnlyFilePaths) {
                if (File.Exists(filePath)) {
                    FileAttributes attributes = File.GetAttributes(filePath);
                    File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
                }
            }

            if (Directory.Exists(dataPath)) {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    [Test]
    public async Task WindowsStorage_RemoveHomeAsync_CanceledBeforeStartPreservesHomeAndAttributes() {
        string dataPath = Path.Combine(Path.GetTempPath(), "AiAccountManagerTests", Guid.NewGuid().ToString("N"));
        CurrentUserDataDirectory dataDirectory = new(dataPath);
        WindowsCodexProfileStorage storage = new(dataDirectory, new AtomicJsonFileStore(dataDirectory));
        Guid accountId = Guid.NewGuid();
        string homePath = storage.GetHomePath(accountId);
        string readOnlyFilePath = Path.Combine(homePath, "plugins", ".git", "objects", "pack", "tmp_pack_cancelled");

        try {
            await storage.CreateHomeAsync(accountId, CancellationToken.None);
            Directory.CreateDirectory(Path.GetDirectoryName(readOnlyFilePath)!);
            await File.WriteAllTextAsync(readOnlyFilePath, "temporary profile cleanup data");
            FileAttributes attributes = File.GetAttributes(readOnlyFilePath);
            File.SetAttributes(readOnlyFilePath, attributes | FileAttributes.ReadOnly);
            attributes = File.GetAttributes(readOnlyFilePath);
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await storage.RemoveHomeAsync(accountId, cancellation.Token)
            );

            Assert.That(Directory.Exists(homePath), Is.True);
            Assert.That(File.GetAttributes(readOnlyFilePath), Is.EqualTo(attributes));
        } finally {
            if (File.Exists(readOnlyFilePath)) {
                FileAttributes attributes = File.GetAttributes(readOnlyFilePath);
                File.SetAttributes(readOnlyFilePath, attributes & ~FileAttributes.ReadOnly);
            }

            if (Directory.Exists(dataPath)) {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    [Test]
    public async Task WindowsStorageLoadsPendingAddCleanupJournal() {
        string dataPath = Path.Combine(Path.GetTempPath(), "AiAccountManagerTests", Guid.NewGuid().ToString("N"));
        CurrentUserDataDirectory dataDirectory = new(dataPath);
        WindowsCodexProfileStorage storage = new(dataDirectory, new AtomicJsonFileStore(dataDirectory));
        string[] stages = [
            "started",
            "home-created",
            "identity-confirmed",
            "catalog-committed",
            "cleanup-pending",
            "keyring-cleared",
            "home-removed"
        ];
        CodexProfileTransaction[] transactions = stages.Select(stage => new CodexProfileTransaction(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "add",
            stage,
            "catalog-sha256",
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
            "IOException"
        )).ToArray();

        try {
            foreach (CodexProfileTransaction transaction in transactions) {
                await storage.SaveTransactionAsync(transaction, CancellationToken.None);
            }

            IReadOnlyList<CodexProfileTransaction> pending = await storage.LoadPendingAddCleanupTransactionsAsync(
                CancellationToken.None
            );

            Assert.That(pending, Is.EquivalentTo(transactions));
        } finally {
            if (Directory.Exists(dataPath)) {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    [Test]
    public async Task WindowsStorageRejectsUnknownAddCleanupJournalStage() {
        string dataPath = Path.Combine(Path.GetTempPath(), "AiAccountManagerTests", Guid.NewGuid().ToString("N"));
        CurrentUserDataDirectory dataDirectory = new(dataPath);
        WindowsCodexProfileStorage storage = new(dataDirectory, new AtomicJsonFileStore(dataDirectory));
        CodexProfileTransaction transaction = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "add",
            "unrecognized-stage",
            "catalog-sha256",
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
            null
        );

        try {
            string transactionDirectory = Path.Combine(dataPath, "transactions");
            Directory.CreateDirectory(transactionDirectory);
            string path = Path.Combine(transactionDirectory, transaction.TransactionId.ToString("N") + ".json");
            byte[] content = JsonSerializer.SerializeToUtf8Bytes(
                transaction,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            );
            await File.WriteAllBytesAsync(path, content);

            await Assert.ThrowsAsync<InvalidDataException>(async () => await storage.LoadPendingAddCleanupTransactionsAsync(
                CancellationToken.None
            ));
        } finally {
            if (Directory.Exists(dataPath)) {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    private static CodexAccountProfile CreateProfile() {
        return new CodexAccountProfile(
            Guid.NewGuid(),
            "Work",
            "p•••@example.test",
            "fingerprint:chatgpt-account-123",
            1,
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
            "active",
            "chatgpt-account-123"
        );
    }

    private sealed class Fixture {
        public Fixture() {
            Catalog = new InMemoryCatalogRepository();
            Storage = new FakeProfileStorage();
            Session = new FakeSession();
            Browser = new FakeBrowserLauncher();
            SessionFactory = new FakeSessionFactory(Session);
            Service = new CodexProfileService(
                Catalog,
                Storage,
                SessionFactory,
                Browser,
                new FakeFingerprinter(),
                new FakeClock(),
                new ExclusiveOperationCoordinator()
            );
        }

        public InMemoryCatalogRepository Catalog { get; }

        public FakeProfileStorage Storage { get; }

        public FakeSession Session { get; }

        public FakeBrowserLauncher Browser { get; }

        public FakeSessionFactory SessionFactory { get; }

        public CodexProfileService Service { get; }

    }

    private sealed class InMemoryCatalogRepository : IAccountCatalogRepository {
        private long revision;

        public IReadOnlyList<CodexAccountProfile> Accounts { get; private set; } = [];

        public bool ConflictNextSave { get; set; }

        public string CurrentSha256 => Sha256;

        private string Sha256 => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision.ToString())));

        public Task<IAccountCatalogRepository.Snapshot> LoadAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IAccountCatalogRepository.Snapshot(Accounts.ToArray(), Sha256));
        }

        public Task<IAccountCatalogRepository.SaveResult> SaveAsync(
            IReadOnlyList<CodexAccountProfile> accounts,
            string expectedSha256,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            if (ConflictNextSave) {
                ConflictNextSave = false;
                revision++;
                return Task.FromResult(new IAccountCatalogRepository.SaveResult(false, null, true));
            }

            if (!string.Equals(expectedSha256, Sha256, StringComparison.Ordinal)) {
                return Task.FromResult(new IAccountCatalogRepository.SaveResult(false, null, true));
            }

            Accounts = accounts.ToArray();
            revision++;
            return Task.FromResult(new IAccountCatalogRepository.SaveResult(true, new IAccountCatalogRepository.Snapshot(Accounts, Sha256), false));
        }

        public void Seed(params CodexAccountProfile[] profiles) {
            Accounts = profiles;
            revision++;
        }
    }

    private static CodexAccountProfile CreateProfile(Guid accountId, string fingerprint, string maskedIdentity) {
        return new(
            accountId,
            null,
            maskedIdentity,
            fingerprint,
            1,
            DateTimeOffset.Parse("2026-10-25T00:00:00Z"),
            "connected",
            fingerprint,
            null
        );
    }

    private sealed class FakeProfileStorage : ICodexProfileStorage {
        public HashSet<Guid> Homes { get; } = [];

        public Dictionary<Guid, CodexProfileTransaction> Transactions { get; } = [];

        public HashSet<Guid> AuthFiles { get; } = [];

        public string? FailNextStageWrite { get; set; }

        public bool FailNextRemoveHome { get; set; }

        public bool FailNextTransactionDelete { get; set; }

        public int RemoveHomeCalls { get; private set; }

        public int RemoveAuthFileCalls { get; private set; }

        public string GetHomePath(Guid accountId) => Path.Combine(Path.GetTempPath(), "FakeCodex", accountId.ToString("N"));

        public Task CreateHomeAsync(Guid accountId, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            Homes.Add(accountId);
            return Task.CompletedTask;
        }

        public Task<bool> RemoveUnexpectedAuthFileAsync(Guid accountId, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveAuthFileCalls++;
            return Task.FromResult(AuthFiles.Remove(accountId));
        }

        public Task RemoveHomeAsync(Guid accountId, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveHomeCalls++;
            if (FailNextRemoveHome) {
                FailNextRemoveHome = false;
                throw new IOException("Synthetic profile home removal failure.");
            }

            Homes.Remove(accountId);
            return Task.CompletedTask;
        }

        public Task SaveTransactionAsync(CodexProfileTransaction transaction, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(FailNextStageWrite, transaction.Stage, StringComparison.Ordinal)) {
                FailNextStageWrite = null;
                throw new IOException("Synthetic profile journal write failure.");
            }

            Transactions[transaction.TransactionId] = transaction;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CodexProfileTransaction>> LoadPendingAddCleanupTransactionsAsync(
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (CodexProfileTransaction transaction in Transactions.Values) {
                bool known = transaction.Operation switch {
                    "add" => transaction.Stage is "started" or "home-created" or "identity-confirmed" or "catalog-committed"
                        or "cleanup-pending" or "keyring-cleared" or "home-removed",
                    "remove" => transaction.Stage is "started" or "keyring-cleared" or "home-removed" or "catalog-conflict"
                        or "cleanup-pending",
                    _ => false
                };
                if (!known) {
                    throw new InvalidDataException("A profile transaction journal has an unknown operation or stage.");
                }
            }

            CodexProfileTransaction[] matches = Transactions.Values
                .Where(transaction => transaction.Operation == "add"
                    && transaction.Stage is "started" or "home-created" or "identity-confirmed" or "catalog-committed"
                        or "cleanup-pending" or "keyring-cleared" or "home-removed")
                .OrderBy(transaction => transaction.TransactionId)
                .ToArray();
            if (matches.GroupBy(transaction => transaction.AccountId).Any(group => group.Count() > 1)) {
                throw new InvalidDataException("Multiple pending add cleanup journals exist for one account.");
            }

            return Task.FromResult<IReadOnlyList<CodexProfileTransaction>>(matches);
        }

        public Task<CodexProfileTransaction?> LoadPendingRemovalTransactionAsync(
            Guid accountId,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            CodexProfileTransaction[] matches = Transactions.Values
                .Where(transaction => transaction.AccountId == accountId && transaction.Operation == "remove")
                .ToArray();
            if (matches.Any(transaction => transaction.Stage is not ("started" or "keyring-cleared" or "home-removed" or "catalog-conflict" or "cleanup-pending"))) {
                throw new InvalidDataException("A profile removal journal has an unknown stage.");
            }

            if (matches.Length > 1) {
                throw new InvalidDataException("Multiple pending profile removal journals exist for the account.");
            }

            return Task.FromResult(matches.SingleOrDefault());
        }

        public Task DeleteTransactionAsync(Guid transactionId, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextTransactionDelete) {
                FailNextTransactionDelete = false;
                throw new IOException("Synthetic profile transaction deletion failure.");
            }

            Transactions.Remove(transactionId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSessionFactory(FakeSession session) : ICodexAppServerSessionFactory {
        public int CreateCalls { get; private set; }

        public List<Guid> CreatedAccountIds { get; } = [];

        public Exception? Failure { get; set; }

        public Task<ICodexAppServerSession> CreateAsync(
            Guid accountId,
            long profileRevision,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            CreatedAccountIds.Add(accountId);
            if (Failure is not null) {
                return Task.FromException<ICodexAppServerSession>(Failure);
            }

            session.AccountId = accountId;
            session.ProfileRevision = profileRevision;
            return Task.FromResult<ICodexAppServerSession>(session);
        }
    }

    private sealed class FakeSession : ICodexAppServerSession {
        public Guid AccountId { get; set; }
        public long ProfileRevision { get; set; }
        public string AccountReadId { get; set; } = "chatgpt-account-123";
        public string RateLimitAccountId { get; set; } = "chatgpt-account-123";
        public bool LoginStarted { get; private set; }
        public int LoginStartCount { get; private set; }
        public bool LoginCancelled { get; private set; }
        public bool LoggedOut { get; private set; }
        public bool FailLogout { get; set; }
        public int LogoutAttempts { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CodexLoginStart> StartChatGptLoginAsync(CancellationToken cancellationToken) {
            LoginStarted = true;
            LoginStartCount++;
            return Task.FromResult(new CodexLoginStart("login-1", new Uri("https://auth.example.test/authorize?state=synthetic")));
        }

        public async Task<CodexLoginCompletion> WaitForLoginCompletionAsync(
            string loginId,
            TimeSpan timeout,
            CancellationToken cancellationToken
        ) {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return new CodexLoginCompletion(loginId, true);
        }

        public Task<bool> CancelChatGptLoginAsync(string loginId, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            LoginCancelled = true;
            return Task.FromResult(true);
        }

        public Task<CodexAccountRead> ReadAccountAsync(bool refreshToken, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CodexAccountRead("chatgpt", AccountReadId, "person@example.test", "plus", true));
        }

        public Task<JsonElement> ReadRateLimitsAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(new { accountId = RateLimitAccountId }));
            return Task.FromResult(document.RootElement.Clone());
        }

        public Task<string> ConsumeResetCreditAsync(string idempotencyKey, string? creditId, CancellationToken cancellationToken)
            => Task.FromResult("reset");

        public Task LogoutAsync(CancellationToken cancellationToken) {
            LogoutAttempts++;
            if (FailLogout) {
                throw new IOException("Synthetic keyring deletion failure.");
            }

            LoggedOut = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async IAsyncEnumerable<CodexAppServerNotification> ReadNotificationsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken
        ) {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class FakeBrowserLauncher : IExternalBrowserLauncher {
        public Uri? OpenedUri { get; private set; }
        public Action? AfterOpen { get; set; }

        public Task OpenAsync(Uri uri, CancellationToken cancellationToken) {
            OpenedUri = uri;
            AfterOpen?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFingerprinter : IIdentityFingerprinter {
        public string CreateFingerprint(string? accountId, string? email, string authMode)
            => accountId is not null ? "fingerprint:" + accountId : "fingerprint:" + email + ":" + authMode;

        public string MaskEmail(string? email) => "p•••@example.test";
    }

    private sealed class FakeClock : IClock {
        public DateTimeOffset UtcNow => new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    }
}



