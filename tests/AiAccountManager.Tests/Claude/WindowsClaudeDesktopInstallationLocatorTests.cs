using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Windows.Claude;
using NUnit.Framework;
using ProcessIdentity = CloudyWing.AiAccountManager.Windows.Claude.WindowsClaudeDesktopProcessEnumerator.ProcessIdentity;

namespace CloudyWing.AiAccountManager.Tests.Claude;

[TestFixture]
public sealed class WindowsClaudeDesktopInstallationLocatorTests {
    private static readonly DateTime Started = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Sid = "S-1-5-21-100";
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PackageFamilyName = "ClaudeFixture.Package_abcd1234wxyz1";

    [Test]
    public void MissingCandidate_DoesNotRaiseMissingPathExceptions() {
        string parent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        int missingExceptions = 0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> handler = (_, args) => {
            if (args.Exception is FileNotFoundException or DirectoryNotFoundException) {
                Interlocked.Increment(ref missingExceptions);
            }
        };
        AppDomain.CurrentDomain.FirstChanceException += handler;
        try {
            bool found = WindowsClaudeDesktopInstallationLocator.CandidateExists(
                parent, Path.Combine("LocalCache", "Roaming", "Claude"), true
            );

            Assert.That(found, Is.False);
            Assert.That(missingExceptions, Is.Zero);
        } finally {
            AppDomain.CurrentDomain.FirstChanceException -= handler;
            Directory.Delete(parent);
        }
    }

    [Test]
    public void IsPackageFullNameFromFamily_AcceptsPackageVersionUpdateForSameFamily() {
        bool versionOneMatches = WindowsClaudeDesktopInstallationLocator.IsPackageFullNameFromFamily(
            "Claude.Package_1.0.0.0_x64__abcd1234wxyz1",
            "Claude.Package_abcd1234wxyz1"
        );
        bool versionTwoMatches = WindowsClaudeDesktopInstallationLocator.IsPackageFullNameFromFamily(
            "Claude.Package_2.0.0.0_x64__abcd1234wxyz1",
            "Claude.Package_abcd1234wxyz1"
        );

        Assert.Multiple(() => {
            Assert.That(versionOneMatches, Is.True);
            Assert.That(versionTwoMatches, Is.True);
        });
    }

    [TestCase("Other.Package_2.0.0.0_x64__zxyw4321abcd1", "Claude.Package_abcd1234wxyz1")]
    [TestCase("Claude.Package_2.0.0.0_x64__abcd1234wxyz1", "Other.Package_zxyw4321abcd1")]
    [TestCase("not-a-package-full-name", "Claude.Package_abcd1234wxyz1")]
    public void IsPackageFullNameFromFamily_RejectsDifferentOrMalformedIdentity(string packageFullName, string packageFamilyName) {
        bool matches = WindowsClaudeDesktopInstallationLocator.IsPackageFullNameFromFamily(
            packageFullName,
            packageFamilyName
        );

        Assert.That(matches, Is.False);
    }

    [TestCase("Claude-Data/claude_PKG", true)]
    [TestCase("Claude-Data/ClaudeFixture.Package", true)]
    [TestCase("Packages/ClaudeFixture.Package/LocalCache/Roaming/Claude", true)]
    [TestCase("Roaming/Claude", true)]
    [TestCase("Roaming/Claude", false)]
    public async Task ObserveRunning_UsesProcessIdentityForEverySupportedCandidate(string relativeRoot, bool packaged) {
        FakeProbe probe = new() { RootPath = FixturePath(relativeRoot), Packaged = packaged };
        probe.AddProcess(101);
        probe.AddProcess(102);
        WindowsClaudeDesktopInstallationLocator locator = CreateLocator(probe);

        IClaudeDesktopInstallationLocator.ObservationResult result =
            await locator.ObserveRunningAsync(CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(IClaudeDesktopInstallationLocator.LocateStatus.Found));
        Assert.That(result.Observation, Is.Not.Null);
        Assert.That(result.Installation, Is.Not.Null);
        Assert.That(result.Installation!.Observation, Is.SameAs(result.Observation));
        Assert.That(result.Observation!.Processes.Select(process => process.ProcessId), Is.EquivalentTo(new[] { 101, 102 }));
        Assert.That(result.Observation.Kind, Is.EqualTo(packaged
            ? IClaudeDesktopInstallationLocator.InstallKind.Msix
            : IClaudeDesktopInstallationLocator.InstallKind.Direct));
        Assert.That(probe.ProcessReadCounts.Values, Is.All.EqualTo(2));
        Assert.That(probe.ProcessDisposeCount, Is.EqualTo(2));
        Assert.That(probe.ObservedResources, Is.EqualTo(new[] { Path.Combine(probe.RootPath, "Cookies") }));
    }

    [TestCase("Network/Cookies")]
    [TestCase("Cookies")]
    public async Task ObserveRunning_UsesEachSupportedCookieLocation(string cookieRelativePath) {
        FakeProbe probe = new() { CookieRelativePath = cookieRelativePath.Replace('/', Path.DirectorySeparatorChar) };
        probe.AddProcess(101);
        WindowsClaudeDesktopInstallationLocator locator = CreateLocator(probe);

        IClaudeDesktopInstallationLocator.ObservationResult result =
            await locator.ObserveRunningAsync(CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(IClaudeDesktopInstallationLocator.LocateStatus.Found));
        Assert.That(result.Observation!.CookieRelativePath, Is.EqualTo(probe.CookieRelativePath));
    }

    [Test]
    public async Task ObserveRunning_RejectsTwoInstallationsSharingOneCookie() {
        FakeProbe probe = new();
        probe.AddProcess(101);
        probe.AddProcess(102, family: "OtherClaude.Package_zxyw4321abcd1");
        WindowsClaudeDesktopInstallationLocator locator = CreateLocator(probe);

        IClaudeDesktopInstallationLocator.ObservationResult result =
            await locator.ObserveRunningAsync(CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(IClaudeDesktopInstallationLocator.LocateStatus.Ambiguous));
        Assert.That(result.Observation, Is.Null);
        Assert.That(probe.ProcessDisposeCount, Is.EqualTo(2));
    }

    [TestCase("other-sid")]
    [TestCase("reused-pid")]
    [TestCase("unregistered")]
    [TestCase("resource-failure")]
    [TestCase("candidate-changed")]
    [TestCase("incomplete")]
    [TestCase("competing-process")]
    public async Task ObserveRunning_RejectsUnknownEvidence(string condition) {
        FakeProbe probe = new() {
            CurrentSid = Sid,
            IsRegistered = condition != "unregistered",
            IsResourceComplete = condition != "resource-failure",
            IsScanComplete = condition != "incomplete",
            CandidateChanged = condition == "candidate-changed",
            ProcessesAreConsistent = condition != "competing-process"
        };
        probe.AddProcess(101, sid: condition == "other-sid" ? "S-1-5-21-200" : Sid);
        if (condition == "reused-pid") {
            probe.Observed[0] = new ClaudeDesktopRootBinding.ObservedProcess(101, new DateTimeOffset(Started.AddSeconds(-1)));
        }

        WindowsClaudeDesktopInstallationLocator locator = CreateLocator(probe);

        IClaudeDesktopInstallationLocator.ObservationResult result =
            await locator.ObserveRunningAsync(CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(IClaudeDesktopInstallationLocator.LocateStatus.Unknown));
        Assert.That(result.Observation, Is.Null);
    }

    [Test]
    public async Task ObserveRunning_RejectsMultipleCandidateRootsOrCookieLocations() {
        FakeProbe probe = new() { IsAmbiguous = true };
        probe.AddProcess(101);
        WindowsClaudeDesktopInstallationLocator locator = CreateLocator(probe);

        IClaudeDesktopInstallationLocator.ObservationResult result =
            await locator.ObserveRunningAsync(CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(IClaudeDesktopInstallationLocator.LocateStatus.Ambiguous));
        Assert.That(probe.ObservedResources, Is.Empty);
    }

    [Test]
    public async Task ResolveObservationAfterStop_AcceptsOnlyOriginalObservationInSameLocator() {
        FakeProbe probe = new();
        probe.AddProcess(101);
        FakeController controller = new();
        WindowsClaudeDesktopInstallationLocator locator = CreateLocator(probe, controller);
        IClaudeDesktopInstallationLocator.ObservationResult observed =
            await locator.ObserveRunningAsync(CancellationToken.None);
        ClaudeDesktopRootBinding.Observation source = observed.Observation!;

        IClaudeDesktopInstallationLocator.LocateResult result =
            await locator.ResolveObservationAfterStopAsync(source, CancellationToken.None);
        IClaudeDesktopInstallationLocator.LocateResult cloned =
            await locator.ResolveObservationAfterStopAsync(source with { }, CancellationToken.None);
        IClaudeDesktopInstallationLocator.LocateResult restarted =
            await CreateLocator(probe, controller).ResolveObservationAfterStopAsync(source, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(IClaudeDesktopInstallationLocator.LocateStatus.Found));
        Assert.That(result.Installation!.Observation, Is.SameAs(source));
        Assert.That(cloned.Status, Is.EqualTo(IClaudeDesktopInstallationLocator.LocateStatus.Unknown));
        Assert.That(restarted.Status, Is.EqualTo(IClaudeDesktopInstallationLocator.LocateStatus.Unknown));
    }

    [Test]
    public async Task ResolveBound_RevalidatesAfterRecreatingLocator() {
        FakeProbe probe = new();
        probe.AddProcess(101);
        WindowsClaudeDesktopInstallationLocator locator = CreateLocator(probe);
        ClaudeDesktopRootBinding.Observation source =
            (await locator.ObserveRunningAsync(CancellationToken.None)).Observation!;
        ClaudeDesktopRootBinding binding = new(source, Digest);
        int reads = 0;
        WindowsClaudeDesktopInstallationLocator restarted = CreateLocator(probe, readDigest: (_, _) => {
            reads++;
            return Task.FromResult(Digest);
        });

        IClaudeDesktopInstallationLocator.LocateResult result =
            await restarted.ResolveBoundAsync(binding, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(IClaudeDesktopInstallationLocator.LocateStatus.Found));
        Assert.That(result.Installation!.Binding, Is.SameAs(binding));
        Assert.That(reads, Is.EqualTo(1));
    }

    [Test]
    public async Task ResolveBound_AcceptsUpdatedMsixVersionInSameRegisteredPackageFamily() {
        FakeProbe probe = new();
        probe.AddProcess(101);
        WindowsClaudeDesktopInstallationLocator locator = CreateLocator(probe);
        ClaudeDesktopRootBinding.Observation source =
            (await locator.ObserveRunningAsync(CancellationToken.None)).Observation!;
        int readsBeforeResolve = probe.RegisteredPackageFullNameReadCount;
        probe.RegisteredPackageFullName = "ClaudeFixture.Package_2.0.0.0_x64__abcd1234wxyz1";

        IClaudeDesktopInstallationLocator.LocateResult result = await CreateLocator(probe)
            .ResolveBoundAsync(new ClaudeDesktopRootBinding(source, Digest), CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(IClaudeDesktopInstallationLocator.LocateStatus.Found), result.FailureCode);
        Assert.Multiple(() => {
            Assert.That(result.Installation?.Observation?.PackageFullName, Is.EqualTo(probe.RegisteredPackageFullName));
            Assert.That(probe.RegisteredPackageFullNameReadCount - readsBeforeResolve, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task ResolveBound_RejectsPackageReplacementDuringResolution() {
        FakeProbe probe = new();
        probe.AddProcess(101);
        WindowsClaudeDesktopInstallationLocator locator = CreateLocator(probe);
        ClaudeDesktopRootBinding.Observation source =
            (await locator.ObserveRunningAsync(CancellationToken.None)).Observation!;
        int readsBeforeResolve = probe.RegisteredPackageFullNameReadCount;
        probe.RegisteredPackageFullNameSequence.Enqueue("ClaudeFixture.Package_2.0.0.0_x64__abcd1234wxyz1");
        probe.RegisteredPackageFullNameSequence.Enqueue("ClaudeFixture.Package_3.0.0.0_x64__abcd1234wxyz1");

        IClaudeDesktopInstallationLocator.LocateResult result = await CreateLocator(probe)
            .ResolveBoundAsync(new ClaudeDesktopRootBinding(source, Digest), CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(result.Status, Is.EqualTo(IClaudeDesktopInstallationLocator.LocateStatus.Unknown));
            Assert.That(result.FailureCode, Is.EqualTo("source-changed"));
            Assert.That(probe.RegisteredPackageFullNameReadCount - readsBeforeResolve, Is.EqualTo(2));
        });
    }

    [TestCase("running")]
    [TestCase("sid")]
    [TestCase("root")]
    [TestCase("installation")]
    [TestCase("key")]
    [TestCase("package-family")]
    [TestCase("app-user-model-id")]
    [TestCase("multiple-packages")]
    public async Task ResolveBound_RejectsChangedOrUncertainEvidence(string condition) {
        FakeProbe probe = new();
        probe.AddProcess(101);
        WindowsClaudeDesktopInstallationLocator locator = CreateLocator(probe);
        ClaudeDesktopRootBinding.Observation source =
            (await locator.ObserveRunningAsync(CancellationToken.None)).Observation!;
        FakeController controller = new() {
            Status = condition == "running"
                ? IClaudeDesktopProcessController.ProcessStatus.Running
                : IClaudeDesktopProcessController.ProcessStatus.NotRunning
        };
        probe.CurrentSid = condition == "sid" ? "S-1-5-21-200" : Sid;
        probe.FileId = condition == "root" ? "changed" : "root-id";
        probe.IsRegistered = condition != "installation";
        probe.RegisteredPackageFamilyName = condition == "package-family"
            ? "Other.Package_zxyw4321abcd1"
            : source.PackageFamilyName;
        probe.RegisteredAppUserModelId = condition == "app-user-model-id"
            ? source.PackageFamilyName + "!Other"
            : source.AppUserModelId;
        probe.RegisteredPackageCount = condition == "multiple-packages" ? 2 : 1;
        int reads = 0;
        WindowsClaudeDesktopInstallationLocator restarted = CreateLocator(probe, controller, (_, _) => {
            reads++;
            return Task.FromResult(condition == "key" ? new string('b', 64) : Digest);
        });

        IClaudeDesktopInstallationLocator.LocateResult result =
            await restarted.ResolveBoundAsync(new ClaudeDesktopRootBinding(source, Digest), CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(IClaudeDesktopInstallationLocator.LocateStatus.Unknown));
        if (condition != "key") {
            Assert.That(reads, Is.Zero);
        }
    }

    [Test]
    public async Task ObserveRunning_CancellationAndLegacyLocateDoNotProduceTrustedSource() {
        FakeProbe probe = new();
        probe.AddProcess(101);
        WindowsClaudeDesktopInstallationLocator locator = CreateLocator(probe);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        IClaudeDesktopInstallationLocator.ObservationResult cancelled =
            await locator.ObserveRunningAsync(cancellation.Token);
        IClaudeDesktopInstallationLocator.LocateResult legacy = await locator.LocateAsync(CancellationToken.None);

        Assert.That(cancelled.Status, Is.EqualTo(IClaudeDesktopInstallationLocator.LocateStatus.Unknown));
        Assert.That(legacy.Status, Is.EqualTo(IClaudeDesktopInstallationLocator.LocateStatus.Unknown));
        Assert.That(cancelled.Observation, Is.Null);
        Assert.That(legacy.Installation, Is.Null);
    }

    [Test]
    public void PathIdentity_RejectsReplacementWhileOriginalHandleIsHeld() {
        using WindowsClaudeTestWorkspace workspace = new();
        string filePath = Path.Combine(workspace.RootPath, "fixture-identity");
        File.WriteAllText(filePath, "first");
        using ClaudeWindowsPathSafety.IdentityLease lease = ClaudeWindowsPathSafety.OpenIdentity(filePath, directory: false);
        File.Move(filePath, filePath + ".displaced");
        File.WriteAllText(filePath, "second");

        Assert.Throws<IOException>(() => lease.VerifyPath());
    }

    [Test]
    public void PathIdentity_RejectsReparsePoint() {
        using WindowsClaudeTestWorkspace workspace = new();
        string target = Path.Combine(workspace.RootPath, "fixture-target");
        string link = Path.Combine(workspace.RootPath, "fixture-link");
        Directory.CreateDirectory(target);
        try {
            Directory.CreateSymbolicLink(link, target);
        } catch (Exception failure) when (failure is UnauthorizedAccessException or IOException) {
            Assert.Ignore("This test requires permission to create a fixture symbolic link.");
        }

        Assert.Throws<InvalidDataException>(() => ClaudeWindowsPathSafety.OpenIdentity(link, directory: true));
    }

    [TestCase("ClaudeFixture_pkg!Claude", true)]
    [TestCase("ClaudeFixture_pkg!SshProxy", false)]
    [TestCase("OtherFixture_pkg!Claude", false)]
    public void MatchesDesktopApplication_SelectsOnlyDesktopApplicationAmongHelpers(string processAppUserModelId, bool expected) {
        string[] packageAppUserModelIds = [
            "ClaudeFixture_pkg!Claude",
            "ClaudeFixture_pkg!SshAskpass",
            "ClaudeFixture_pkg!SshProxy",
            "ClaudeFixture_pkg!MakeDataFolder"
        ];

        bool matched = WindowsClaudeDesktopInstallationLocator.MatchesDesktopApplication(
            packageAppUserModelIds, processAppUserModelId
        );

        Assert.That(matched, Is.EqualTo(expected));
    }

    [TestCase("ClaudeFixture_pkg!Claude", "ClaudeFixture_pkg!claude")]
    [TestCase("ClaudeFixture_pkg!SshProxy", "ClaudeFixture_pkg!MakeDataFolder")]
    [TestCase("Claude", "ClaudeFixture_pkg!SshProxy")]
    public void MatchesDesktopApplication_RejectsMissingOrDuplicateDesktopApplication(string first, string second) {
        bool matched = WindowsClaudeDesktopInstallationLocator.MatchesDesktopApplication(
            [first, second], "ClaudeFixture_pkg!Claude"
        );

        Assert.That(matched, Is.False);
    }

    [TestCase("Claude Code", "Anthropic PBC", true)]
    [TestCase("Claude", "Anthropic PBC", false)]
    [TestCase("Claude Code", "Other Vendor", false)]
    [TestCase(null, null, false)]
    public void IsClaudeCodeProduct_SeparatesCliFromDesktop(string? productName, string? companyName, bool expected) {
        bool isCli = WindowsClaudeDesktopProcessEnumerator.IsClaudeCodeProduct(productName, companyName);

        Assert.That(isCli, Is.EqualTo(expected));
    }

    private static WindowsClaudeDesktopInstallationLocator CreateLocator(
        FakeProbe probe,
        FakeController? controller = null,
        Func<IClaudeDesktopInstallationLocator.Installation, CancellationToken, Task<string>>? readDigest = null
    ) {
        return new WindowsClaudeDesktopInstallationLocator(
            probe, controller ?? new FakeController(), readDigest ?? ((_, _) => Task.FromResult(Digest))
        );
    }

    private static string FixturePath(string relative) {
        return Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claude-root-fixtures", relative));
    }

    private sealed class FakeProbe : IClaudeDesktopRootProbe {
        public string RootPath { get; init; } = FixturePath("Roaming/Claude");

        public string CookieRelativePath { get; init; } = "Cookies";

        public string CurrentSid { get; set; } = Sid;

        public string FileId { get; set; } = "root-id";

        public bool Packaged { get; init; } = true;

        public bool IsRegistered { get; set; } = true;

        public int RegisteredPackageCount { get; set; } = 1;

        public string? RegisteredPackageFullName { get; set; }

        public Queue<string?> RegisteredPackageFullNameSequence { get; } = new();

        public string? RegisteredPackageFamilyName { get; set; }

        public string? RegisteredAppUserModelId { get; set; }

        public int RegisteredPackageFullNameReadCount { get; private set; }

        public bool IsResourceComplete { get; init; } = true;

        public bool IsScanComplete { get; init; } = true;

        public bool IsAmbiguous { get; init; }

        public bool CandidateChanged { get; init; }

        public bool ProcessesAreConsistent { get; init; } = true;

        public List<ClaudeDesktopRootBinding.ObservedProcess> Observed { get; } = [];

        public Dictionary<int, ProcessIdentity> Identities { get; } = [];

        public Dictionary<int, int> ProcessReadCounts { get; } = [];

        public List<string> ObservedResources { get; } = [];

        public int ProcessDisposeCount { get; private set; }

        public void AddProcess(int id, string sid = Sid, string family = PackageFamilyName) {
            DateTime start = Started.AddSeconds(id);
            Observed.Add(new ClaudeDesktopRootBinding.ObservedProcess(id, new DateTimeOffset(start)));
            int separator = family.LastIndexOf('_');
            string packageName = separator > 0 ? family[..separator] : family;
            string publisherId = separator > 0 ? family[(separator + 1)..] : "publisher123456";
            Identities[id] = new ProcessIdentity(
                id, start, sid, FixturePath("Programs/Claude/Claude.exe"), Packaged ? null : Digest,
                Packaged ? family + "!Claude" : null, Packaged ? family : null,
                Packaged ? $"{packageName}_1.0.0.0_x64__{publisherId}" : null
            );
            ProcessReadCounts[id] = 0;
        }

        public string GetCurrentUserSid() => CurrentSid;

        public IClaudeDesktopRootProbe.RootScan OpenCandidates(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            return new IClaudeDesktopRootProbe.RootScan(IsScanComplete, IsAmbiguous, [new Candidate(this)]);
        }

        public WindowsClaudeRestartManagerProbe.Result ObserveResource(string path, CancellationToken cancellationToken) {
            ObservedResources.Add(path);
            return new WindowsClaudeRestartManagerProbe.Result(IsResourceComplete, Observed.ToArray());
        }

        public IClaudeDesktopRootProbe.IObservedProcess OpenProcess(ClaudeDesktopRootBinding.ObservedProcess observed) {
            return new ProcessLease(this, observed.ProcessId);
        }

        public bool VerifyInstallation(ClaudeDesktopRootBinding.Observation observation) =>
            observation.Kind == IClaudeDesktopInstallationLocator.InstallKind.Msix
                ? GetRegisteredPackageFullName(observation) is not null
                : IsRegistered;

        public string? GetRegisteredPackageFullName(ClaudeDesktopRootBinding.Observation observation) {
            RegisteredPackageFullNameReadCount++;
            if (observation.Kind != IClaudeDesktopInstallationLocator.InstallKind.Msix
                || !IsRegistered
                || RegisteredPackageCount != 1
                || !string.Equals(
                    RegisteredPackageFamilyName ?? observation.PackageFamilyName,
                    observation.PackageFamilyName,
                    StringComparison.OrdinalIgnoreCase
                )
                || !string.Equals(
                    RegisteredAppUserModelId ?? observation.AppUserModelId,
                    observation.AppUserModelId,
                    StringComparison.OrdinalIgnoreCase
                )
                || !string.Equals(
                    observation.AppUserModelId,
                    observation.PackageFamilyName + "!Claude",
                    StringComparison.OrdinalIgnoreCase
                )) {
                return null;
            }

            if (RegisteredPackageFullNameSequence.TryDequeue(out string? queuedPackageFullName)) {
                return queuedPackageFullName;
            }

            return RegisteredPackageFullName ?? observation.PackageFullName;
        }

        public bool VerifyRunningProcesses(
            IClaudeDesktopInstallationLocator.Installation installation, CancellationToken cancellationToken
        ) => ProcessesAreConsistent;

        public string? FindDirectRegistration(string executablePath) => "fixture-registration";

        private sealed class Candidate : IClaudeDesktopRootProbe.IRootCandidate {
            private readonly FakeProbe owner;

            internal Candidate(FakeProbe owner) {
                this.owner = owner;
            }

            public string RootPath => owner.RootPath;

            public string CookieRelativePath => owner.CookieRelativePath;

            public ulong VolumeSerialNumber => 7;

            public string FileId => owner.FileId;

            public void VerifyUnchanged() {
                if (owner.CandidateChanged) {
                    throw new IOException("fixture changed");
                }
            }

            public void Dispose() {
            }
        }

        private sealed class ProcessLease : IClaudeDesktopRootProbe.IObservedProcess {
            private readonly FakeProbe owner;
            private readonly int id;

            internal ProcessLease(FakeProbe owner, int id) {
                this.owner = owner;
                this.id = id;
            }

            public ProcessIdentity ReadIdentity() {
                owner.ProcessReadCounts[id]++;
                return owner.Identities[id];
            }

            public void Dispose() {
                owner.ProcessDisposeCount++;
            }
        }
    }

    private sealed class FakeController : IClaudeDesktopProcessController {
        public IClaudeDesktopProcessController.ProcessStatus Status { get; init; } =
            IClaudeDesktopProcessController.ProcessStatus.NotRunning;

        public Task<IClaudeDesktopProcessController.ProcessStatus> GetStatusAsync(
            IClaudeDesktopInstallationLocator.Installation installation, CancellationToken cancellationToken
        ) => Task.FromResult(Status);

        public Task<bool> CloseGracefullyAsync(
            IClaudeDesktopInstallationLocator.Installation installation, TimeSpan timeout, CancellationToken cancellationToken
        ) => Task.FromResult(false);

        public Task LaunchAsync(IClaudeDesktopInstallationLocator.Installation installation, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
