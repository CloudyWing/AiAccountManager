using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Claude;

[TestFixture]
public sealed class ClaudeAccountServiceTests {
    [Test]
    public async Task GetPageStateAsync_VerifiesCurrentIdentityFromDesktopTokenAndProfileApi() {
        Guid profileId = Guid.NewGuid();
        ClaudeAccountProfile profile = ClaudeTestData.CreateProfile(profileId, "account-a", "org-a");
        ClaudeAccountProfile.DesktopSnapshot snapshot = ClaudeTestData.CreateSnapshot("account-a", "token-a");
        ClaudeTestAccountRepository repository = new(profile);
        repository.AddSnapshot(profileId, snapshot);
        ClaudeTestProfileStore profileStore = new() {
            CurrentSnapshot = snapshot,
            CurrentCandidates = [new("token-a", "oauth:tokenCacheV2")]
        };
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["token-a"] = ClaudeTestData.VerifiedProfile("account-a", "org-a");
        ClaudeTestProcessController process = new() {
            Status = IClaudeDesktopProcessController.ProcessStatus.Running
        };
        ClaudeAccountService service = CreateService(repository, new(), profileStore, oauth, process);

        ClaudeAccountService.PageState state = await service.GetPageStateAsync(CancellationToken.None);

        Assert.That(state.CurrentIdentity.Status, Is.EqualTo(ClaudeAccountService.IdentityStatus.Verified));
        Assert.That(state.CurrentIdentity.MatchedProfile, Is.SameAs(profile));
        Assert.That(state.CurrentIdentity.AccountUuid, Is.EqualTo("account-a"));
        Assert.That(state.ProcessStatus, Is.EqualTo(IClaudeDesktopProcessController.ProcessStatus.Running));
        Assert.That(oauth.ProfileTokens, Is.EqualTo(new[] { "token-a" }));
    }

    [Test]
    public async Task GetPageStateAsync_MatchesCurrentProfileAfterMsixPackageVersionUpdate() {
        Guid profileId = Guid.NewGuid();
        ClaudeAccountProfile profile = ClaudeTestData.CreateProfile(profileId, "account-a", "org-a");
        ClaudeAccountProfile.DesktopSnapshot snapshot = ClaudeTestData.CreateSnapshot("account-a", "token-a");
        ClaudeTestAccountRepository repository = new(profile);
        repository.AddSnapshot(profileId, snapshot);
        ClaudeTestProfileStore profileStore = new() {
            CurrentSnapshot = snapshot,
            CurrentCandidates = [new("token-a", "oauth:tokenCacheV2")]
        };
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["token-a"] = ClaudeTestData.VerifiedProfile("account-a", "org-a");
        ClaudeDesktopRootBinding.Observation currentObservation = ClaudeTestData.Observation with {
            PackageFullName = "Claude.Package_2.0.0.0_x64__abcd1234wxyz1"
        };
        IClaudeDesktopInstallationLocator.Installation currentInstallation = ClaudeTestData.Installation with {
            Observation = currentObservation,
            Binding = null
        };
        ClaudeTestInstallationLocator locator = new() {
            Result = new(IClaudeDesktopInstallationLocator.LocateStatus.Found, currentInstallation, null),
            ObservationResult = new(
                IClaudeDesktopInstallationLocator.LocateStatus.Found,
                currentObservation,
                null,
                currentInstallation
            )
        };
        ClaudeTestProcessController process = new() {
            Status = IClaudeDesktopProcessController.ProcessStatus.Running
        };
        ClaudeAccountService service = CreateService(repository, locator, profileStore, oauth, process);

        ClaudeAccountService.PageState state = await service.GetPageStateAsync(CancellationToken.None);

        Assert.That(state.CurrentIdentity.Status, Is.EqualTo(ClaudeAccountService.IdentityStatus.Verified));
        Assert.That(state.CurrentIdentity.MatchedProfile, Is.SameAs(profile));
    }

    [TestCase("package-family")]
    [TestCase("app-user-model-id")]
    [TestCase("data-root")]
    public async Task GetPageStateAsync_RejectsMsixProfileWhenStableSourceIdentityChanges(string mismatch) {
        Guid profileId = Guid.NewGuid();
        ClaudeAccountProfile profile = ClaudeTestData.CreateProfile(profileId, "account-a", "org-a");
        ClaudeAccountProfile.DesktopSnapshot snapshot = ClaudeTestData.CreateSnapshot("account-a", "token-a");
        ClaudeTestAccountRepository repository = new(profile);
        repository.AddSnapshot(profileId, snapshot);
        ClaudeTestProfileStore profileStore = new() {
            CurrentSnapshot = snapshot,
            CurrentCandidates = [new("token-a", "oauth:tokenCacheV2")]
        };
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["token-a"] = ClaudeTestData.VerifiedProfile("account-a", "org-a");
        ClaudeDesktopRootBinding.Observation currentObservation = ClaudeTestData.Observation with {
            PackageFullName = "Claude.Package_2.0.0.0_x64__abcd1234wxyz1"
        };
        currentObservation = mismatch switch {
            "package-family" => currentObservation with {
                InstallationId = "msix:Other.Package_zxyw4321abcd1",
                PackageFamilyName = "Other.Package_zxyw4321abcd1",
                PackageFullName = "Other.Package_2.0.0.0_x64__zxyw4321abcd1",
                AppUserModelId = "Other.Package_zxyw4321abcd1!Claude"
            },
            "app-user-model-id" => currentObservation with { AppUserModelId = "Claude.Package_abcd1234wxyz1!Other" },
            "data-root" => currentObservation with { DataRoot = "C:\\Synthetic\\OtherClaudeData" },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        IClaudeDesktopInstallationLocator.Installation currentInstallation = ClaudeTestData.Installation with {
            InstallationId = currentObservation.InstallationId,
            DataRoot = currentObservation.DataRoot,
            DataRootFingerprint = mismatch == "data-root" ? "different-root-fingerprint" : ClaudeTestData.Installation.DataRootFingerprint,
            AppUserModelId = currentObservation.AppUserModelId,
            Observation = currentObservation,
            Binding = null
        };
        ClaudeTestInstallationLocator locator = new() {
            Result = new(IClaudeDesktopInstallationLocator.LocateStatus.Found, currentInstallation, null),
            ObservationResult = new(
                IClaudeDesktopInstallationLocator.LocateStatus.Found,
                currentObservation,
                null,
                currentInstallation
            )
        };
        ClaudeTestProcessController process = new() {
            Status = IClaudeDesktopProcessController.ProcessStatus.Running
        };
        ClaudeAccountService service = CreateService(repository, locator, profileStore, oauth, process);

        ClaudeAccountService.PageState state = await service.GetPageStateAsync(CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(state.CurrentIdentity.Status, Is.EqualTo(ClaudeAccountService.IdentityStatus.Verified));
            Assert.That(state.CurrentIdentity.MatchedProfile, Is.Null);
        });
    }

    [Test]
    public async Task CaptureCurrentProfileAsync_SavesOnlyApiVerifiedDesktopIdentity() {
        ClaudeAccountProfile.DesktopSnapshot snapshot = ClaudeTestData.CreateSnapshot("account-a", "token-a");
        ClaudeTestAccountRepository repository = new();
        ClaudeTestProfileStore profileStore = new() {
            CurrentSnapshot = snapshot,
            CurrentCandidates = [new("token-a", "oauth:tokenCacheV2")]
        };
        profileStore.SetCandidates(snapshot, "token-a");
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["token-a"] = ClaudeTestData.VerifiedProfile("account-a", "org-a");
        ClaudeTestProcessController process = new() {
            Status = IClaudeDesktopProcessController.ProcessStatus.NotRunning
        };
        ClaudeAccountService service = CreateService(repository, new(), profileStore, oauth, process);

        ClaudeAccountService.CaptureResult result = await service.CaptureCurrentProfileAsync("Work", CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeAccountService.CaptureOutcome.Saved));
        Assert.That(result.Profile, Is.Not.Null);
        Assert.That(result.Profile!.Alias, Is.EqualTo("Work"));
        Assert.That(result.Profile.AccountUuid, Is.EqualTo("account-a"));
        Assert.That(result.Profile.OrganizationUuid, Is.EqualTo("org-a"));
        Assert.That(result.Profile.MaskedEmail, Is.EqualTo("masked@example.test"));
        Assert.That(repository.SaveProfileCallCount, Is.EqualTo(1));
        ClaudeAccountProfile.DesktopSnapshot? savedSnapshot = await repository.LoadDesktopSnapshotAsync(
            result.Profile.ProfileId,
            CancellationToken.None
        );
        Assert.That(savedSnapshot, Is.Not.Null);
        Assert.That(savedSnapshot!.TokenCacheJson, Is.EqualTo(snapshot.TokenCacheJson));
        Assert.That(savedSnapshot.Cookies, Is.EqualTo(snapshot.Cookies));
        Assert.That(savedSnapshot.ProvenanceSchemaVersion, Is.EqualTo(1));
        Assert.That(savedSnapshot.RootBinding, Is.Not.Null);
        Assert.That(oauth.ProfileTokens, Is.EqualTo(new[] { "token-a" }));
        Assert.That(profileStore.ReadCurrentCallCount, Is.EqualTo(1));
        Assert.That(profileStore.LastSnapshotLease!.ValidateCallCount, Is.EqualTo(2));
        Assert.That(profileStore.LastSnapshotLease.DisposeCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task GetPageStateAsync_F002_UsesLocklessIdentityReadWithoutStableSnapshotWhileDesktopIsRunning() {
        (ClaudeAccountService service, ClaudeTestAccountRepository _, ClaudeTestProfileStore profileStore,
            ClaudeTestProcessController _) = CreateCaptureService(IClaudeDesktopProcessController.ProcessStatus.Running);

        ClaudeAccountService.PageState state = await service.GetPageStateAsync(CancellationToken.None);

        Assert.That(state.CurrentIdentity.Status, Is.EqualTo(ClaudeAccountService.IdentityStatus.Verified));
        Assert.That(profileStore.ReadCurrentCallCount, Is.EqualTo(1));
        Assert.That(profileStore.Operations, Does.Not.Contain("capture-stable-snapshot"));
        Assert.That(profileStore.LastSnapshotLease, Is.Null);
    }

    [Test]
    public async Task CaptureCurrentProfileAsync_F002_RejectsTokenCandidatesChangedBetweenIdentityReadAndStableCapture() {
        (ClaudeAccountService service, ClaudeTestAccountRepository repository, ClaudeTestProfileStore profileStore,
            ClaudeTestProcessController _) = CreateCaptureService(IClaudeDesktopProcessController.ProcessStatus.NotRunning);
        profileStore.StableSnapshotCandidates = [new("changed-token", "oauth:tokenCacheV2")];

        ClaudeAccountService.CaptureResult result = await service.CaptureCurrentProfileAsync(null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeAccountService.CaptureOutcome.IdentityUnavailable));
        Assert.That(repository.SaveProfileCallCount, Is.Zero);
        Assert.That(profileStore.LastSnapshotLease!.DisposeCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CaptureCurrentProfileAsync_F002_RejectsUnstableLeaseAndDisposesHandles() {
        (ClaudeAccountService service, ClaudeTestAccountRepository repository, ClaudeTestProfileStore profileStore,
            ClaudeTestProcessController _) = CreateCaptureService(IClaudeDesktopProcessController.ProcessStatus.NotRunning);
        profileStore.SnapshotLeaseValidationResult = false;

        ClaudeAccountService.CaptureResult result = await service.CaptureCurrentProfileAsync(null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeAccountService.CaptureOutcome.BindingMismatch));
        Assert.That(repository.SaveProfileCallCount, Is.Zero);
        Assert.That(profileStore.LastSnapshotLease!.DisposeCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CaptureCurrentProfileAsync_F002_HoldsStableSnapshotLeaseUntilRepositorySaveCompletes() {
        (ClaudeAccountService service, ClaudeTestAccountRepository repository, ClaudeTestProfileStore profileStore,
            ClaudeTestProcessController _) = CreateCaptureService(IClaudeDesktopProcessController.ProcessStatus.NotRunning);
        TaskCompletionSource<bool> saveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> allowSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.BeforeSaveProfileAsync = async cancellationToken => {
            saveStarted.TrySetResult(true);
            await allowSave.Task.WaitAsync(cancellationToken);
        };

        Task<ClaudeAccountService.CaptureResult> captureTask = service.CaptureCurrentProfileAsync(null, CancellationToken.None);
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(profileStore.LastSnapshotLease!.DisposeCallCount, Is.Zero);
        allowSave.SetResult(true);
        ClaudeAccountService.CaptureResult result = await captureTask;

        Assert.That(result.Outcome, Is.EqualTo(ClaudeAccountService.CaptureOutcome.Saved));
        Assert.That(profileStore.LastSnapshotLease.DisposeCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CaptureCurrentProfileAsync_F002_DisposesStableSnapshotLeaseWhenRepositorySaveFails() {
        (ClaudeAccountService service, ClaudeTestAccountRepository repository, ClaudeTestProfileStore profileStore,
            ClaudeTestProcessController _) = CreateCaptureService(IClaudeDesktopProcessController.ProcessStatus.NotRunning);
        repository.BeforeSaveProfileAsync = _ => throw new IOException("Synthetic repository save failure.");

        ClaudeAccountService.CaptureResult result = await service.CaptureCurrentProfileAsync(null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeAccountService.CaptureOutcome.Failed));
        Assert.That(repository.SaveProfileCallCount, Is.EqualTo(1));
        Assert.That(profileStore.LastSnapshotLease!.DisposeCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CaptureCurrentProfileAsync_F002_DisposesStableSnapshotLeaseWhenSaveIsCancelled() {
        (ClaudeAccountService service, ClaudeTestAccountRepository repository, ClaudeTestProfileStore profileStore,
            ClaudeTestProcessController _) = CreateCaptureService(IClaudeDesktopProcessController.ProcessStatus.NotRunning);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource<bool> saveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.BeforeSaveProfileAsync = async cancellationToken => {
            saveStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };

        Task<ClaudeAccountService.CaptureResult> captureTask = service.CaptureCurrentProfileAsync(null, cancellation.Token);
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.CatchAsync<OperationCanceledException>(async () => {
            await captureTask;
        });
        Assert.That(repository.SaveProfileCallCount, Is.EqualTo(1));
        Assert.That(profileStore.LastSnapshotLease!.DisposeCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CaptureCurrentProfileAsync_RejectsChangedLocalStateKeyBindingWithoutSaving() {
        Guid profileId = Guid.NewGuid();
        ClaudeAccountProfile profile = ClaudeTestData.CreateProfile(profileId, "account-a", "org-a");
        ClaudeAccountProfile.DesktopSnapshot savedSnapshot = ClaudeTestData.CreateSnapshot("account-a", "old-token");
        ClaudeAccountProfile.DesktopSnapshot liveSnapshot = ClaudeTestData.CreateSnapshot(
            "account-a",
            "live-token",
            new string('b', 64)
        );
        ClaudeTestAccountRepository repository = new(profile);
        repository.AddSnapshot(profileId, savedSnapshot);
        ClaudeTestProfileStore profileStore = new() {
            CurrentSnapshot = liveSnapshot,
            CurrentCandidates = [new("live-token", "oauth:tokenCacheV2")]
        };
        profileStore.SetCandidates(liveSnapshot, "live-token");
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["live-token"] = ClaudeTestData.VerifiedProfile("account-a", "org-a");
        ClaudeTestProcessController process = new() {
            Status = IClaudeDesktopProcessController.ProcessStatus.NotRunning
        };
        ClaudeAccountService service = CreateService(repository, new(), profileStore, oauth, process);

        ClaudeAccountService.CaptureResult result = await service.CaptureCurrentProfileAsync(null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeAccountService.CaptureOutcome.BindingMismatch));
        Assert.That(repository.SaveProfileCallCount, Is.Zero);
        Assert.That((await repository.LoadDesktopSnapshotAsync(profileId, CancellationToken.None))!.TokenCacheJson,
            Is.EqualTo(savedSnapshot.TokenCacheJson));
    }

    [Test]
    public async Task CaptureCurrentProfileAsync_BlocksWhenStartupFoundPendingJournal() {
        ClaudeTestAccountRepository repository = new();
        repository.Transaction = CreatePendingTransaction();
        ClaudeTestProfileStore profileStore = new();
        ClaudeAccountService service = CreateService(repository, new(), profileStore, new(), new());

        ClaudeAccountService.CaptureResult result = await service.CaptureCurrentProfileAsync(null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeAccountService.CaptureOutcome.RecoveryRequired));
        Assert.That(repository.SaveProfileCallCount, Is.Zero);
        Assert.That(profileStore.ReadCurrentCallCount, Is.Zero);
        Assert.That(service.CurrentPageState.CurrentIdentity.Status, Is.EqualTo(ClaudeAccountService.IdentityStatus.RecoveryRequired));
    }

    [Test]
    public async Task GetPageStateAsync_ReportsMismatchWhenLocalUuidDiffersFromVerifiedToken() {
        ClaudeAccountProfile.DesktopSnapshot snapshot = ClaudeTestData.CreateSnapshot("account-a", "token-a");
        ClaudeTestProfileStore profileStore = new() {
            CurrentSnapshot = snapshot,
            CurrentCandidates = [new("token-a", "oauth:tokenCacheV2")]
        };
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["token-a"] = ClaudeTestData.VerifiedProfile("account-b", "org-b");
        ClaudeAccountService service = CreateService(new(), new(), profileStore, oauth, new());

        ClaudeAccountService.PageState state = await service.GetPageStateAsync(CancellationToken.None);

        Assert.That(state.CurrentIdentity.Status, Is.EqualTo(ClaudeAccountService.IdentityStatus.Mismatch));
        Assert.That(state.CurrentIdentity.MatchedProfile, Is.Null);
    }

    [Test]
    public async Task GetPageStateAsync_DistinguishesAmbiguousInstallation() {
        ClaudeTestInstallationLocator locator = new() {
            Result = new(IClaudeDesktopInstallationLocator.LocateStatus.Ambiguous, null, "ambiguous")
        };
        ClaudeAccountService service = CreateService(new(), locator, new(), new(), new());

        ClaudeAccountService.PageState state = await service.GetPageStateAsync(CancellationToken.None);

        Assert.That(state.CurrentIdentity.Status, Is.EqualTo(ClaudeAccountService.IdentityStatus.AmbiguousInstallation));
        Assert.That(state.ProcessStatus, Is.EqualTo(IClaudeDesktopProcessController.ProcessStatus.Unknown));
    }

    [TestCase("desktop-config-multiple-orgs.json")]
    [TestCase("desktop-config-multiple-orgs-reversed.json")]
    public async Task GetPageStateAsync_RejectsMultipleVerifiedOrganizationsRegardlessOfTokenOrder(string fixtureName) {
        JsonElement profileA = ClaudeTestData.ReadFixture("oauth-profile-org-a.json");
        JsonElement profileB = ClaudeTestData.ReadFixture("oauth-profile-org-b.json");
        string accountUuid = profileA.GetProperty("account").GetProperty("uuid").GetString()!;
        string organizationA = profileA.GetProperty("organization").GetProperty("uuid").GetString()!;
        string organizationB = profileB.GetProperty("organization").GetProperty("uuid").GetString()!;
        string fixturePath = Path.Combine(TestContext.CurrentContext.TestDirectory, "Claude", "Fixtures", fixtureName);
        string configJson = await File.ReadAllTextAsync(fixturePath);
        using JsonDocument config = JsonDocument.Parse(configJson);
        List<string> tokens = ReadAccessTokens(config.RootElement);
        ClaudeAccountProfile.DesktopSnapshot snapshot = ClaudeTestData.CreateSnapshot(accountUuid, tokens[0]) with {
            TokenCacheJson = configJson,
            TokenCacheV2Json = null
        };
        ClaudeTestProfileStore profileStore = new() {
            CurrentSnapshot = snapshot,
            CurrentCandidates = tokens.Select(token => new IClaudeDesktopProfileStore.TokenCandidate(token, "fixture"))
                .ToArray()
        };
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["token-org-a"] = new(
            IClaudeOAuthClient.Outcome.Success,
            accountUuid,
            organizationA,
            "org-a@example.test",
            null,
            null
        );
        oauth.ProfileResults["token-org-b"] = new(
            IClaudeOAuthClient.Outcome.Success,
            accountUuid,
            organizationB,
            "org-b@example.test",
            null,
            null
        );
        ClaudeAccountService service = CreateService(new(), new(), profileStore, oauth, new());

        ClaudeAccountService.PageState state = await service.GetPageStateAsync(CancellationToken.None);

        Assert.That(state.CurrentIdentity.Status, Is.EqualTo(ClaudeAccountService.IdentityStatus.Unknown));
        Assert.That(state.CurrentIdentity.OrganizationUuid, Is.Null);
        Assert.That(oauth.ProfileTokens, Is.EquivalentTo(new[] { "token-org-a", "token-org-b" }));
    }

    [Test]
    public async Task CaptureCurrentProfileAsync_DoesNotSaveWhenOrganizationCandidatesAreAmbiguous() {
        JsonElement profileA = ClaudeTestData.ReadFixture("oauth-profile-org-a.json");
        JsonElement profileB = ClaudeTestData.ReadFixture("oauth-profile-org-b.json");
        string accountUuid = profileA.GetProperty("account").GetProperty("uuid").GetString()!;
        string organizationA = profileA.GetProperty("organization").GetProperty("uuid").GetString()!;
        string organizationB = profileB.GetProperty("organization").GetProperty("uuid").GetString()!;
        string fixturePath = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Claude",
            "Fixtures",
            "desktop-config-multiple-orgs.json"
        );
        string configJson = await File.ReadAllTextAsync(fixturePath);
        using JsonDocument config = JsonDocument.Parse(configJson);
        List<string> tokens = ReadAccessTokens(config.RootElement);
        ClaudeAccountProfile.DesktopSnapshot snapshot = ClaudeTestData.CreateSnapshot(accountUuid, tokens[0]) with {
            TokenCacheJson = configJson,
            TokenCacheV2Json = null
        };
        ClaudeTestAccountRepository repository = new();
        ClaudeTestProfileStore profileStore = new() {
            CurrentSnapshot = snapshot,
            CurrentCandidates = tokens.Select(token => new IClaudeDesktopProfileStore.TokenCandidate(token, "fixture"))
                .ToArray()
        };
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["token-org-a"] = ClaudeTestData.VerifiedProfile(accountUuid, organizationA);
        oauth.ProfileResults["token-org-b"] = ClaudeTestData.VerifiedProfile(accountUuid, organizationB);
        ClaudeTestProcessController process = new() {
            Status = IClaudeDesktopProcessController.ProcessStatus.NotRunning
        };
        ClaudeAccountService service = CreateService(repository, new(), profileStore, oauth, process);

        ClaudeAccountService.CaptureResult result = await service.CaptureCurrentProfileAsync(null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ClaudeAccountService.CaptureOutcome.IdentityUnavailable));
        Assert.That(repository.SaveProfileCallCount, Is.Zero);
    }

    [Test]
    public async Task GetPageStateAsync_DeduplicatesCandidatesThatVerifyTheSameIdentity() {
        ClaudeAccountProfile.DesktopSnapshot snapshot = ClaudeTestData.CreateSnapshot("account-a", "token-a");
        ClaudeTestProfileStore profileStore = new() {
            CurrentSnapshot = snapshot,
            CurrentCandidates = [
                new("token-a", "oauth:tokenCache"),
                new("token-a", "oauth:tokenCacheV2"),
                new("token-a-alias", "oauth:tokenCacheV2.alias")
            ]
        };
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["token-a"] = ClaudeTestData.VerifiedProfile("account-a", "org-a");
        oauth.ProfileResults["token-a-alias"] = ClaudeTestData.VerifiedProfile("account-a", "org-a");
        ClaudeAccountService service = CreateService(new(), new(), profileStore, oauth, new());

        ClaudeAccountService.PageState state = await service.GetPageStateAsync(CancellationToken.None);

        Assert.That(state.CurrentIdentity.Status, Is.EqualTo(ClaudeAccountService.IdentityStatus.Verified));
        Assert.That(state.CurrentIdentity.OrganizationUuid, Is.EqualTo("org-a"));
        Assert.That(oauth.ProfileTokens, Is.EqualTo(new[] { "token-a", "token-a-alias" }));
    }

    [TestCase(IClaudeOAuthClient.Outcome.Forbidden)]
    [TestCase(IClaudeOAuthClient.Outcome.RateLimited)]
    [TestCase(IClaudeOAuthClient.Outcome.Error)]
    public async Task GetPageStateAsync_LeavesIdentityUnknownWhenValidCandidateHasUnresolvedPeer(
        IClaudeOAuthClient.Outcome unresolvedOutcome
    ) {
        ClaudeAccountProfile.DesktopSnapshot snapshot = ClaudeTestData.CreateSnapshot("account-a", "valid-token") with {
            TokenCacheV2Json = "{\"accessToken\":\"unresolved-token\"}"
        };
        ClaudeTestProfileStore profileStore = new() {
            CurrentSnapshot = snapshot,
            CurrentCandidates = [
                new("valid-token", "oauth:tokenCache"),
                new("unresolved-token", "oauth:tokenCacheV2")
            ]
        };
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["valid-token"] = ClaudeTestData.VerifiedProfile("account-a", "org-a");
        oauth.ProfileResults["unresolved-token"] = new(unresolvedOutcome, null, null, null, null, "synthetic_unresolved");
        ClaudeAccountService service = CreateService(new(), new(), profileStore, oauth, new());

        ClaudeAccountService.PageState state = await service.GetPageStateAsync(CancellationToken.None);

        Assert.That(state.CurrentIdentity.Status, Is.EqualTo(ClaudeAccountService.IdentityStatus.Unknown));
        Assert.That(state.CurrentIdentity.AccountUuid, Is.Null);
        Assert.That(oauth.ProfileTokens, Is.EqualTo(new[] { "valid-token", "unresolved-token" }));
    }

    [Test]
    public async Task GetPageStateAsync_LeavesIdentityUnknownWhenValidCandidateHasTimeoutPeer() {
        ClaudeAccountProfile.DesktopSnapshot snapshot = ClaudeTestData.CreateSnapshot("account-a", "valid-token");
        ClaudeTestProfileStore profileStore = new() {
            CurrentSnapshot = snapshot,
            CurrentCandidates = [
                new("valid-token", "oauth:tokenCache"),
                new("timeout-token", "oauth:tokenCacheV2")
            ]
        };
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["valid-token"] = ClaudeTestData.VerifiedProfile("account-a", "org-a");
        oauth.ThrowOnProfile.Add("timeout-token");
        ClaudeAccountService service = CreateService(new(), new(), profileStore, oauth, new());

        ClaudeAccountService.PageState state = await service.GetPageStateAsync(CancellationToken.None);

        Assert.That(state.CurrentIdentity.Status, Is.EqualTo(ClaudeAccountService.IdentityStatus.Unknown));
        Assert.That(state.CurrentIdentity.OrganizationUuid, Is.Null);
    }

    [TestCase(IClaudeDesktopProcessController.ProcessStatus.Running)]
    [TestCase(IClaudeDesktopProcessController.ProcessStatus.Unknown)]
    public async Task CaptureCurrentProfileAsync_RequiresStoppedDesktopBeforeSnapshotRead(
        IClaudeDesktopProcessController.ProcessStatus status
    ) {
        (ClaudeAccountService service, ClaudeTestAccountRepository repository, ClaudeTestProfileStore profileStore,
            ClaudeTestProcessController process) =
            CreateCaptureService(status);

        ClaudeAccountService.CaptureResult result = await service.CaptureCurrentProfileAsync(null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(status == IClaudeDesktopProcessController.ProcessStatus.Running
            ? ClaudeAccountService.CaptureOutcome.DesktopRunning
            : ClaudeAccountService.CaptureOutcome.ProcessStatusUnknown));
        Assert.That(repository.SaveProfileCallCount, Is.Zero);
        Assert.That(process.CloseCallCount, Is.Zero);
        Assert.That(process.Operations[0], Is.EqualTo("process-status"));
        Assert.That(profileStore.ReadCurrentCallCount, Is.EqualTo(1));
    }

    [TestCase(IClaudeDesktopProcessController.ProcessStatus.Running)]
    [TestCase(IClaudeDesktopProcessController.ProcessStatus.Unknown)]
    public async Task CaptureCurrentProfileAsync_RejectsDesktopRestartAfterSnapshotRead(
        IClaudeDesktopProcessController.ProcessStatus status
    ) {
        (ClaudeAccountService service, ClaudeTestAccountRepository repository, ClaudeTestProfileStore profileStore,
            ClaudeTestProcessController process) =
            CreateCaptureService(IClaudeDesktopProcessController.ProcessStatus.NotRunning);
        // 第一次為觀察執行中來源後的狀態檢查，第二次為讀取快照前，第三次為讀取快照後。
        process.SetStatusSequence(
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            status
        );

        ClaudeAccountService.CaptureResult result = await service.CaptureCurrentProfileAsync(null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(status == IClaudeDesktopProcessController.ProcessStatus.Running
            ? ClaudeAccountService.CaptureOutcome.DesktopRunning
            : ClaudeAccountService.CaptureOutcome.ProcessStatusUnknown));
        Assert.That(repository.SaveProfileCallCount, Is.Zero);
        Assert.That(process.CloseCallCount, Is.Zero);
        Assert.That(process.Operations.Take(3), Is.All.EqualTo("process-status"));
        Assert.That(profileStore.ReadCurrentCallCount, Is.EqualTo(2));
    }

    [TestCase(IClaudeDesktopProcessController.ProcessStatus.Running)]
    [TestCase(IClaudeDesktopProcessController.ProcessStatus.Unknown)]
    public async Task CaptureCurrentProfileAsync_RejectsDesktopRestartImmediatelyBeforeSave(
        IClaudeDesktopProcessController.ProcessStatus status
    ) {
        (ClaudeAccountService service, ClaudeTestAccountRepository repository, ClaudeTestProfileStore profileStore,
            ClaudeTestProcessController process) =
            CreateCaptureService(IClaudeDesktopProcessController.ProcessStatus.NotRunning);
        // 依序為觀察後、讀取前、讀取後與保存前的狀態檢查。
        process.SetStatusSequence(
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            IClaudeDesktopProcessController.ProcessStatus.NotRunning,
            status
        );

        ClaudeAccountService.CaptureResult result = await service.CaptureCurrentProfileAsync(null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(status == IClaudeDesktopProcessController.ProcessStatus.Running
            ? ClaudeAccountService.CaptureOutcome.DesktopRunning
            : ClaudeAccountService.CaptureOutcome.ProcessStatusUnknown));
        Assert.That(repository.SaveProfileCallCount, Is.Zero);
        Assert.That(process.CloseCallCount, Is.Zero);
        Assert.That(process.Operations.Take(4), Is.All.EqualTo("process-status"));
        Assert.That(profileStore.ReadCurrentCallCount, Is.EqualTo(2));
    }

    private static (
        ClaudeAccountService Service,
        ClaudeTestAccountRepository Repository,
        ClaudeTestProfileStore ProfileStore,
        ClaudeTestProcessController Process
    ) CreateCaptureService(IClaudeDesktopProcessController.ProcessStatus initialStatus) {
        ClaudeAccountProfile.DesktopSnapshot snapshot = ClaudeTestData.CreateSnapshot("account-a", "token-a");
        ClaudeTestAccountRepository repository = new();
        ClaudeTestProfileStore profileStore = new() {
            CurrentSnapshot = snapshot,
            CurrentCandidates = [new("token-a", "oauth:tokenCacheV2")]
        };
        profileStore.SetCandidates(snapshot, "token-a");
        ClaudeTestOAuthClient oauth = new();
        oauth.ProfileResults["token-a"] = ClaudeTestData.VerifiedProfile("account-a", "org-a");
        ClaudeTestProcessController process = new() { Status = initialStatus };
        return (CreateService(repository, new(), profileStore, oauth, process), repository, profileStore, process);
    }

    private static List<string> ReadAccessTokens(JsonElement element) {
        List<string> tokens = [];
        AddAccessTokens(element, tokens);
        return tokens;
    }

    private static void AddAccessTokens(JsonElement element, ICollection<string> tokens) {
        if (element.ValueKind == JsonValueKind.Object) {
            foreach (JsonProperty property in element.EnumerateObject()) {
                if (string.Equals(property.Name, "accessToken", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "access_token", StringComparison.OrdinalIgnoreCase)) {
                    string? token = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(token)) {
                        tokens.Add(token);
                    }
                } else {
                    AddAccessTokens(property.Value, tokens);
                }
            }
        } else if (element.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement item in element.EnumerateArray()) {
                AddAccessTokens(item, tokens);
            }
        }
    }

    [Test]
    public async Task UpdateAliasAsync_SavesTrimmedAliasWithoutChangingProfileRevision() {
        ClaudeAccountProfile profile = ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-a", "org-a");
        ClaudeTestAccountRepository repository = new(profile);
        ClaudeAccountService service = CreateService(repository, new(), new(), new(), new());

        ClaudeAccountService.MetadataResult result = await service.UpdateAliasAsync(
            profile.ProfileId,
            "  Work  ",
            CancellationToken.None
        );

        Assert.Multiple(() => {
            Assert.That(result.Saved, Is.True);
            Assert.That(repository.Profiles.Single().Alias, Is.EqualTo("Work"));
            Assert.That(repository.Profiles.Single().ProfileRevision, Is.EqualTo(profile.ProfileRevision));
            Assert.That(service.CurrentPageState.Profiles.Single().Alias, Is.EqualTo("Work"));
        });
    }

    [Test]
    public async Task UpdateAliasAsync_WhitespaceAliasClearsAlias() {
        ClaudeAccountProfile profile = ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-a", "org-a") with {
            Alias = "Old"
        };
        ClaudeTestAccountRepository repository = new(profile);
        ClaudeAccountService service = CreateService(repository, new(), new(), new(), new());

        ClaudeAccountService.MetadataResult result = await service.UpdateAliasAsync(
            profile.ProfileId,
            "   ",
            CancellationToken.None
        );

        Assert.That(result.Saved, Is.True);
        Assert.That(repository.Profiles.Single().Alias, Is.Null);
    }

    [TestCase(ClaudeAccountService.MoveDirection.Down, 0, new[] { 1, 0, 2 })]
    [TestCase(ClaudeAccountService.MoveDirection.Up, 2, new[] { 0, 2, 1 })]
    [TestCase(ClaudeAccountService.MoveDirection.Up, 0, new[] { 0, 1, 2 })]
    [TestCase(ClaudeAccountService.MoveDirection.Down, 2, new[] { 0, 1, 2 })]
    public async Task MoveAsync_SwapsWithNeighborOrKeepsOrderAtBoundary(
        ClaudeAccountService.MoveDirection direction,
        int sourceIndex,
        int[] expectedOrder
    ) {
        ClaudeAccountProfile[] profiles = [
            ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-a", "org-a"),
            ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-b", "org-b"),
            ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-c", "org-c")
        ];
        ClaudeTestAccountRepository repository = new(profiles);
        ClaudeAccountService service = CreateService(repository, new(), new(), new(), new());

        ClaudeAccountService.MetadataResult result = await service.MoveAsync(
            profiles[sourceIndex].ProfileId,
            direction,
            CancellationToken.None
        );

        Assert.That(result.Saved, Is.True);
        Assert.That(
            repository.Profiles.Select(profile => profile.ProfileId),
            Is.EqualTo(expectedOrder.Select(index => profiles[index].ProfileId))
        );
    }

    [Test]
    public async Task RemoveAsync_RemovesProfileAndItsSavedSnapshot() {
        ClaudeAccountProfile removed = ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-a", "org-a");
        ClaudeAccountProfile kept = ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-b", "org-b");
        ClaudeTestAccountRepository repository = new(removed, kept);
        repository.AddSnapshot(removed.ProfileId, ClaudeTestData.CreateSnapshot("account-a", "token-a"));
        ClaudeAccountService service = CreateService(repository, new(), new(), new(), new());

        ClaudeAccountService.MetadataResult result = await service.RemoveAsync(removed.ProfileId, CancellationToken.None);
        ClaudeAccountProfile.DesktopSnapshot? snapshot = await repository.LoadDesktopSnapshotAsync(
            removed.ProfileId,
            CancellationToken.None
        );

        Assert.Multiple(() => {
            Assert.That(result.Saved, Is.True);
            Assert.That(repository.Profiles, Is.EqualTo(new[] { kept }));
            Assert.That(snapshot, Is.Null);
        });
    }

    [Test]
    public async Task RemoveAsync_PendingSwitchTransactionLeavesCatalogUnchanged() {
        ClaudeAccountProfile profile = ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-a", "org-a");
        ClaudeTestAccountRepository repository = new(profile) {
            Transaction = CreatePendingTransaction()
        };
        ClaudeAccountService service = CreateService(repository, new(), new(), new(), new());

        ClaudeAccountService.MetadataResult result = await service.RemoveAsync(profile.ProfileId, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(result.Saved, Is.False);
            Assert.That(result.Message, Does.Contain("尚未完成的 Claude Desktop 切換交易"));
            Assert.That(repository.Profiles, Is.EqualTo(new[] { profile }));
        });
    }

    [Test]
    public async Task UpdateAliasAsync_UnknownProfileReportsFailureWithoutSaving() {
        ClaudeAccountProfile profile = ClaudeTestData.CreateProfile(Guid.NewGuid(), "account-a", "org-a");
        ClaudeTestAccountRepository repository = new(profile);
        string hashBefore = repository.CatalogHash;
        ClaudeAccountService service = CreateService(repository, new(), new(), new(), new());

        ClaudeAccountService.MetadataResult result = await service.UpdateAliasAsync(
            Guid.NewGuid(),
            "Work",
            CancellationToken.None
        );

        Assert.That(result.Saved, Is.False);
        Assert.That(repository.CatalogHash, Is.EqualTo(hashBefore));
    }

    private static ClaudeAccountService CreateService(
        ClaudeTestAccountRepository repository,
        ClaudeTestInstallationLocator locator,
        ClaudeTestProfileStore profileStore,
        ClaudeTestOAuthClient oauth,
        ClaudeTestProcessController process
    ) {
        return new(
            repository,
            locator,
            process,
            profileStore,
            oauth,
            new ClaudeTestFingerprinter(),
            ClaudeTestData.CreateOperationGate(),
            new ClaudeTestClock()
        );
    }

    private static ClaudeSwitchTransaction CreatePendingTransaction() {
        return new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            1,
            ClaudeTestData.Installation.InstallationId,
            ClaudeSwitchTransaction.Stage.Prepared,
            ClaudeTestData.Now.AddMinutes(-1),
            ClaudeTestData.Now,
            null
        );
    }
}
