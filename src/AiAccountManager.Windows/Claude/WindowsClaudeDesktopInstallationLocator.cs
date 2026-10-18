using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;

namespace CloudyWing.AiAccountManager.Windows.Claude;

/// <summary>
/// 以程序、檔案 identity 與安裝登錄驗證 Claude Desktop 資料來源。
/// </summary>
public sealed class WindowsClaudeDesktopInstallationLocator : IClaudeDesktopInstallationLocator {
    private const int SourceSchemaVersion = 1;
    private const int LayoutVersion = 1;
    private const int ErrorInsufficientBuffer = 122;
    private const string DesktopApplicationId = "Claude";
    private readonly IClaudeDesktopRootProbe rootProbe;
    private readonly IClaudeDesktopProcessController processController;
    private readonly Func<IClaudeDesktopInstallationLocator.Installation, CancellationToken, Task<string>> readKeyDigest;
    private readonly ConcurrentDictionary<Guid, ClaudeDesktopRootBinding.Observation> observations = new();

    /// <summary>
    /// 建立使用 Windows 實體檔案及程序探針的來源定位器。
    /// </summary>
    public WindowsClaudeDesktopInstallationLocator()
        : this(
            new WindowsRootProbe(),
            new WindowsClaudeDesktopProcessController(),
            WindowsClaudeDesktopProfileStore.ReadLocalStateKeyDigestAsync
        ) {
    }

    internal WindowsClaudeDesktopInstallationLocator(
        IClaudeDesktopRootProbe rootProbe,
        IClaudeDesktopProcessController processController,
        Func<IClaudeDesktopInstallationLocator.Installation, CancellationToken, Task<string>> readKeyDigest
    ) {
        ArgumentNullException.ThrowIfNull(rootProbe);
        ArgumentNullException.ThrowIfNull(processController);
        ArgumentNullException.ThrowIfNull(readKeyDigest);
        this.rootProbe = rootProbe;
        this.processController = processController;
        this.readKeyDigest = readKeyDigest;
    }

    /// <summary>
    /// 判斷套件內 Application Id 為 `Claude` 的 AUMID 是否唯一，且與程序的 AUMID 相同。
    /// </summary>
    /// <remarks>
    /// 套件另含 `SshAskpass`、`SshProxy`、`MakeDataFolder` 等輔助 Application，其 AUMID 同樣以套件家族名稱開頭，因此只比對 `!` 後的 Application Id。
    /// </remarks>
    /// <param name="packageAppUserModelIds">套件登錄的全部 AUMID。</param>
    /// <param name="appUserModelId">Claude Desktop 程序的 AUMID。</param>
    /// <returns>唯一的 Desktop AUMID 與程序相同時為 <see langword="true" />。</returns>
    internal static bool MatchesDesktopApplication(IEnumerable<string> packageAppUserModelIds, string appUserModelId) {
        ArgumentNullException.ThrowIfNull(packageAppUserModelIds);
        List<string> desktopIds = packageAppUserModelIds
            .Where(IsDesktopApplicationId)
            .ToList();

        return desktopIds.Count == 1
            && string.Equals(desktopIds[0], appUserModelId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDesktopApplicationId(string appUserModelId) {
        int separator = appUserModelId.IndexOf('!');
        return separator > 0
            && string.Equals(appUserModelId[(separator + 1)..], DesktopApplicationId, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public Task<IClaudeDesktopInstallationLocator.ObservationResult> ObserveRunningAsync(
        CancellationToken cancellationToken
    ) {
        return Task.Run(() => ObserveRunning(cancellationToken), CancellationToken.None);
    }

    private IClaudeDesktopInstallationLocator.ObservationResult ObserveRunning(CancellationToken cancellationToken) {
        IClaudeDesktopRootProbe.RootScan? scan = null;
        List<IClaudeDesktopRootProbe.IObservedProcess> heldProcesses = [];
        try {
            if (cancellationToken.IsCancellationRequested) {
                return ObservationFailure(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, "observation-cancelled");
            }

            string sid = rootProbe.GetCurrentUserSid();
            scan = rootProbe.OpenCandidates(cancellationToken);
            if (scan.IsAmbiguous || scan.Candidates.Count > 1) {
                return ObservationFailure(IClaudeDesktopInstallationLocator.LocateStatus.Ambiguous, "multiple-data-roots");
            }

            if (!scan.IsComplete) {
                return ObservationFailure(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, "data-layout-unknown");
            }

            if (scan.Candidates.Count == 0) {
                return ObservationFailure(IClaudeDesktopInstallationLocator.LocateStatus.Unsupported, "data-root-unavailable");
            }

            IClaudeDesktopRootProbe.IRootCandidate root = scan.Candidates[0];
            WindowsClaudeRestartManagerProbe.Result resource = rootProbe.ObserveResource(
                Path.Combine(root.RootPath, root.CookieRelativePath), cancellationToken
            );
            if (!resource.IsComplete || resource.Processes.Count == 0) {
                return ObservationFailure(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, "resource-observation-unknown");
            }

            ClaudeDesktopRootBinding.Observation? observation = null;
            List<ClaudeDesktopRootBinding.ObservedProcess> observedProcesses = [];
            HashSet<int> processIds = [];
            foreach (ClaudeDesktopRootBinding.ObservedProcess observed in resource.Processes) {
                if (cancellationToken.IsCancellationRequested || !processIds.Add(observed.ProcessId)) {
                    return ObservationFailure(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, "process-observation-unknown");
                }

                IClaudeDesktopRootProbe.IObservedProcess held = rootProbe.OpenProcess(observed);
                heldProcesses.Add(held);
                WindowsClaudeDesktopProcessEnumerator.ProcessIdentity identity = held.ReadIdentity();
                if (identity.ProcessId != observed.ProcessId
                    || identity.StartTimeUtc != observed.CreatedAtUtc.UtcDateTime
                    || !string.Equals(identity.WindowsUserSid, sid, StringComparison.OrdinalIgnoreCase)) {
                    return ObservationFailure(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, "process-identity-unknown");
                }

                ClaudeDesktopRootBinding.Observation next = CreateObservation(root, identity, observed);
                if (!IsValidSource(next) || !rootProbe.VerifyInstallation(next)) {
                    return ObservationFailure(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, "installation-identity-unknown");
                }

                if (observation is not null && !SameInstallation(observation, next)) {
                    return ObservationFailure(IClaudeDesktopInstallationLocator.LocateStatus.Ambiguous, "multiple-installations");
                }

                observation ??= next;
                observedProcesses.Add(observed);
            }

            if (observation is null) {
                return ObservationFailure(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, "process-observation-unknown");
            }

            for (int index = 0; index < heldProcesses.Count; index++) {
                WindowsClaudeDesktopProcessEnumerator.ProcessIdentity current = heldProcesses[index].ReadIdentity();
                ClaudeDesktopRootBinding.Observation next = CreateObservation(root, current, observedProcesses[index]);
                if (current.ProcessId != observedProcesses[index].ProcessId
                    || current.StartTimeUtc != observedProcesses[index].CreatedAtUtc.UtcDateTime
                    || !SameInstallation(observation, next)
                    || !rootProbe.VerifyInstallation(next)) {
                    return ObservationFailure(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, "process-changed");
                }
            }

            observation = observation with { Processes = observedProcesses.ToArray() };
            if (!rootProbe.VerifyRunningProcesses(CreateInstallation(observation, null), cancellationToken)) {
                return ObservationFailure(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, "process-set-unknown");
            }

            root.VerifyUnchanged();
            if (cancellationToken.IsCancellationRequested || !CandidatesStillUnique(observation, cancellationToken)) {
                return ObservationFailure(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, "observation-cancelled");
            }

            if (!observations.TryAdd(observation.BindingId, observation)) {
                return ObservationFailure(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, "observation-unknown");
            }

            IClaudeDesktopInstallationLocator.Installation installation = CreateInstallation(observation, null);
            return new IClaudeDesktopInstallationLocator.ObservationResult(
                IClaudeDesktopInstallationLocator.LocateStatus.Found, observation, null, installation
            );
        } catch (Exception failure) when (IsObservationFailure(failure) || failure is OperationCanceledException) {
            return ObservationFailure(IClaudeDesktopInstallationLocator.LocateStatus.Unknown, "observation-unknown");
        } finally {
            foreach (IClaudeDesktopRootProbe.IObservedProcess process in heldProcesses) {
                process.Dispose();
            }

            DisposeCandidates(scan);
        }
    }

    private ClaudeDesktopRootBinding.Observation CreateObservation(
        IClaudeDesktopRootProbe.IRootCandidate root,
        WindowsClaudeDesktopProcessEnumerator.ProcessIdentity process,
        ClaudeDesktopRootBinding.ObservedProcess observed
    ) {
        bool isMsix = process.PackageFullName is not null;
        string installationId = isMsix
            ? "msix:" + process.PackageFamilyName
            : "direct:" + ClaudeWindowsPathSafety.ComputePathFingerprint(process.ExecutablePath);
        return new ClaudeDesktopRootBinding.Observation(
            SourceSchemaVersion,
            Guid.NewGuid(),
            process.WindowsUserSid,
            installationId,
            isMsix ? IClaudeDesktopInstallationLocator.InstallKind.Msix : IClaudeDesktopInstallationLocator.InstallKind.Direct,
            process.AppUserModelId,
            process.PackageFamilyName,
            process.PackageFullName,
            isMsix ? null : rootProbe.FindDirectRegistration(process.ExecutablePath),
            isMsix ? null : process.ExecutablePath,
            isMsix ? null : process.ExecutableSha256,
            root.RootPath,
            root.CookieRelativePath,
            root.VolumeSerialNumber,
            root.FileId,
            LayoutVersion,
            [observed]
        );
    }

    /// <inheritdoc />
    public async Task<IClaudeDesktopInstallationLocator.LocateResult> ResolveObservationAfterStopAsync(
        ClaudeDesktopRootBinding.Observation observation,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(observation);
        if (!observations.TryGetValue(observation.BindingId, out ClaudeDesktopRootBinding.Observation? original)
            || !ReferenceEquals(original, observation)) {
            return Failure("observation-not-current");
        }

        return await ResolveSourceAsync(observation, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IClaudeDesktopInstallationLocator.LocateResult> ResolveBoundAsync(
        ClaudeDesktopRootBinding binding,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.Source is null) {
            return Task.FromResult(Failure("binding-invalid"));
        }

        return ResolveSourceAsync(binding.Source, binding, cancellationToken);
    }

    private async Task<IClaudeDesktopInstallationLocator.LocateResult> ResolveSourceAsync(
        ClaudeDesktopRootBinding.Observation source,
        ClaudeDesktopRootBinding? binding,
        CancellationToken cancellationToken
    ) {
        IClaudeDesktopRootProbe.RootScan? scan = null;
        try {
            if (cancellationToken.IsCancellationRequested || !IsValidSource(source)
                || !string.Equals(rootProbe.GetCurrentUserSid(), source.WindowsUserSid, StringComparison.OrdinalIgnoreCase)) {
                return Failure("source-identity-unknown");
            }

            string? registeredPackageFullNameAtStart = null;
            if (source.Kind == IClaudeDesktopInstallationLocator.InstallKind.Msix) {
                registeredPackageFullNameAtStart = rootProbe.GetRegisteredPackageFullName(source);
                if (registeredPackageFullNameAtStart is null) {
                    return Failure("source-identity-unknown");
                }
            } else if (!rootProbe.VerifyInstallation(source)) {
                return Failure("source-identity-unknown");
            }

            scan = rootProbe.OpenCandidates(cancellationToken);
            if (scan.IsAmbiguous || scan.Candidates.Count > 1) {
                return new IClaudeDesktopInstallationLocator.LocateResult(
                    IClaudeDesktopInstallationLocator.LocateStatus.Ambiguous, null, "multiple-data-roots"
                );
            }

            if (!scan.IsComplete) {
                return Failure("data-layout-unknown");
            }

            if (scan.Candidates.Count != 1 || !SameRoot(scan.Candidates[0], source)) {
                return Failure("data-root-changed");
            }

            IClaudeDesktopRootProbe.IRootCandidate root = scan.Candidates[0];
            root.VerifyUnchanged();
            IClaudeDesktopInstallationLocator.Installation installation = CreateInstallation(source, binding);
            if (await processController.GetStatusAsync(installation, cancellationToken).ConfigureAwait(false)
                != IClaudeDesktopProcessController.ProcessStatus.NotRunning) {
                return Failure("desktop-not-stopped");
            }

            if (binding is not null) {
                if (!IsSha256(binding.LocalStateEncryptedKeySha256)) {
                    return Failure("binding-invalid");
                }

                string digest = await readKeyDigest(installation, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(digest, binding.LocalStateEncryptedKeySha256, StringComparison.OrdinalIgnoreCase)) {
                    return Failure("local-state-changed");
                }
            }

            root.VerifyUnchanged();
            string? registeredPackageFullNameAtEnd = source.Kind == IClaudeDesktopInstallationLocator.InstallKind.Msix
                ? rootProbe.GetRegisteredPackageFullName(source)
                : null;
            bool packageChanged = source.Kind == IClaudeDesktopInstallationLocator.InstallKind.Msix
                && (registeredPackageFullNameAtEnd is null
                    || !string.Equals(
                        registeredPackageFullNameAtStart,
                        registeredPackageFullNameAtEnd,
                        StringComparison.OrdinalIgnoreCase
                    ));
            if (cancellationToken.IsCancellationRequested || packageChanged
                || (source.Kind == IClaudeDesktopInstallationLocator.InstallKind.Direct && !rootProbe.VerifyInstallation(source))
                || !CandidatesStillUnique(source, cancellationToken)) {
                return Failure("source-changed");
            }

            ClaudeDesktopRootBinding.Observation resolvedSource = source.Kind == IClaudeDesktopInstallationLocator.InstallKind.Msix
                && !string.Equals(source.PackageFullName, registeredPackageFullNameAtEnd, StringComparison.OrdinalIgnoreCase)
                    ? source with { PackageFullName = registeredPackageFullNameAtEnd! }
                    : source;
            installation = CreateInstallation(resolvedSource, binding);
            if (!ReferenceEquals(resolvedSource, source)) {
                installation = installation with { Observation = resolvedSource };
            }

            if (await processController.GetStatusAsync(installation, cancellationToken).ConfigureAwait(false)
                != IClaudeDesktopProcessController.ProcessStatus.NotRunning) {
                return Failure("source-changed");
            }

            return new IClaudeDesktopInstallationLocator.LocateResult(
                IClaudeDesktopInstallationLocator.LocateStatus.Found, installation, null
            );
        } catch (Exception failure) when (IsObservationFailure(failure) || failure is OperationCanceledException) {
            return Failure("source-identity-unknown");
        } finally {
            DisposeCandidates(scan);
        }
    }

    /// <inheritdoc />
    public Task<IClaudeDesktopInstallationLocator.LocateResult> LocateAsync(CancellationToken cancellationToken) {
        return Task.FromResult(Failure("source-observation-required"));
    }

    private bool CandidatesStillUnique(ClaudeDesktopRootBinding.Observation source, CancellationToken cancellationToken) {
        IClaudeDesktopRootProbe.RootScan? scan = null;
        try {
            scan = rootProbe.OpenCandidates(cancellationToken);
            if (!scan.IsComplete || scan.IsAmbiguous || scan.Candidates.Count != 1
                || !SameRoot(scan.Candidates[0], source)) {
                return false;
            }

            scan.Candidates[0].VerifyUnchanged();
            return true;
        } finally {
            DisposeCandidates(scan);
        }
    }

    private static bool IsValidSource(ClaudeDesktopRootBinding.Observation source) {
        return source.SchemaVersion == SourceSchemaVersion
            && source.LayoutVersion == LayoutVersion
            && source.BindingId != Guid.Empty
            && !string.IsNullOrWhiteSpace(source.WindowsUserSid)
            && !string.IsNullOrWhiteSpace(source.InstallationId)
            && !string.IsNullOrWhiteSpace(source.DataRoot)
            && !string.IsNullOrWhiteSpace(source.RootFileId)
            && (string.Equals(source.CookieRelativePath, "Cookies", StringComparison.OrdinalIgnoreCase)
                || string.Equals(source.CookieRelativePath, Path.Combine("Network", "Cookies"), StringComparison.OrdinalIgnoreCase))
            && source.Processes is not null
            && source.Processes.Count > 0
            && source.Processes.All(process => process.ProcessId > 0)
            && (source.Kind == IClaudeDesktopInstallationLocator.InstallKind.Direct
                ? !string.IsNullOrWhiteSpace(source.ExecutablePath) && IsSha256(source.ExecutableSha256)
                : source.Kind == IClaudeDesktopInstallationLocator.InstallKind.Msix
                    && !string.IsNullOrWhiteSpace(source.AppUserModelId)
                    && !string.IsNullOrWhiteSpace(source.PackageFamilyName)
                    && !string.IsNullOrWhiteSpace(source.PackageFullName));
    }

    private static bool IsSha256(string? value) {
        return value is { Length: 64 } && value.All(Uri.IsHexDigit);
    }

    internal static bool IsPackageFullNameFromFamily(string packageFullName, string packageFamilyName) {
        if (string.IsNullOrWhiteSpace(packageFullName) || string.IsNullOrWhiteSpace(packageFamilyName)) {
            return false;
        }

        uint bufferLength = 0;
        if (PackageFamilyNameFromFullName(packageFullName, ref bufferLength, nint.Zero) != ErrorInsufficientBuffer
            || bufferLength is 0 or > 32768) {
            return false;
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)bufferLength * sizeof(char)));
        try {
            if (PackageFamilyNameFromFullName(packageFullName, ref bufferLength, buffer) != 0) {
                return false;
            }

            string? actualFamilyName = Marshal.PtrToStringUni(buffer);
            return string.Equals(actualFamilyName, packageFamilyName, StringComparison.OrdinalIgnoreCase);
        } finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int PackageFamilyNameFromFullName(
        string packageFullName,
        ref uint packageFamilyNameLength,
        nint packageFamilyName
    );

    private static bool SameRoot(IClaudeDesktopRootProbe.IRootCandidate root, ClaudeDesktopRootBinding.Observation source) {
        return string.Equals(root.RootPath, source.DataRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(root.CookieRelativePath, source.CookieRelativePath, StringComparison.OrdinalIgnoreCase)
            && root.VolumeSerialNumber == source.RootVolumeSerialNumber
            && string.Equals(root.FileId, source.RootFileId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameInstallation(
        ClaudeDesktopRootBinding.Observation left,
        ClaudeDesktopRootBinding.Observation right
    ) {
        return left.Kind == right.Kind
            && string.Equals(left.WindowsUserSid, right.WindowsUserSid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.InstallationId, right.InstallationId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.AppUserModelId, right.AppUserModelId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.PackageFamilyName, right.PackageFamilyName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.PackageFullName, right.PackageFullName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.DirectRegistrationSource, right.DirectRegistrationSource, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ExecutableSha256, right.ExecutableSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.DataRoot, right.DataRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.CookieRelativePath, right.CookieRelativePath, StringComparison.OrdinalIgnoreCase)
            && left.RootVolumeSerialNumber == right.RootVolumeSerialNumber
            && string.Equals(left.RootFileId, right.RootFileId, StringComparison.OrdinalIgnoreCase);
    }

    private static IClaudeDesktopInstallationLocator.Installation CreateInstallation(
        ClaudeDesktopRootBinding.Observation source,
        ClaudeDesktopRootBinding? binding
    ) {
        return new IClaudeDesktopInstallationLocator.Installation(
            source.InstallationId, source.Kind, source.DataRoot,
            ClaudeWindowsPathSafety.ComputePathFingerprint(source.DataRoot),
            source.AppUserModelId, source.ExecutablePath, binding is null ? source : null, binding
        );
    }

    private static IClaudeDesktopInstallationLocator.ObservationResult ObservationFailure(
        IClaudeDesktopInstallationLocator.LocateStatus status,
        string failureCode
    ) {
        return new IClaudeDesktopInstallationLocator.ObservationResult(status, null, failureCode);
    }

    private static IClaudeDesktopInstallationLocator.LocateResult Failure(string failureCode) {
        return new IClaudeDesktopInstallationLocator.LocateResult(
            IClaudeDesktopInstallationLocator.LocateStatus.Unknown, null, failureCode
        );
    }

    private static bool IsObservationFailure(Exception failure) {
        return failure is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.ComponentModel.Win32Exception or System.Security.SecurityException
            or NotSupportedException;
    }

    private static void DisposeCandidates(IClaudeDesktopRootProbe.RootScan? scan) {
        if (scan is not null) {
            foreach (IClaudeDesktopRootProbe.IRootCandidate candidate in scan.Candidates) {
                candidate.Dispose();
            }
        }
    }

    internal static bool CandidateExists(string parent, string relativePath, bool requireDirectory) {
        string[] components = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string current = parent;
        try {
            for (int index = 0; index < components.Length; index++) {
                string component = components[index];
                string? entry = Directory.EnumerateFileSystemEntries(current, component)
                    .FirstOrDefault(path => string.Equals(Path.GetFileName(path), component, StringComparison.OrdinalIgnoreCase));
                if (entry is null) {
                    return false;
                }

                FileAttributes attributes = File.GetAttributes(entry);
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (index < components.Length - 1 && !isDirectory) {
                    return false;
                }

                if (index == components.Length - 1 && requireDirectory && !isDirectory) {
                    throw new InvalidDataException("A Claude candidate directory is not a directory.");
                }

                current = entry;
            }

            return true;
        } catch (FileNotFoundException) {
            return false;
        } catch (DirectoryNotFoundException) {
            return false;
        }
    }

    private sealed class WindowsRootProbe : IClaudeDesktopRootProbe {
        private readonly string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private readonly string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private readonly string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        private readonly WindowsClaudeRestartManagerProbe restartManager = new();

        public string GetCurrentUserSid() {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new InvalidOperationException("The current Windows SID is unavailable.");
        }

        public IClaudeDesktopRootProbe.RootScan OpenCandidates(CancellationToken cancellationToken) {
            if (string.IsNullOrWhiteSpace(localAppData) || string.IsNullOrWhiteSpace(roamingAppData)) {
                return new IClaudeDesktopRootProbe.RootScan(false, false, []);
            }

            List<(string Parent, string RelativePath)> possibleRoots = [];
            string claudeData = Path.Combine(localAppData, "Claude-Data");
            if (DirectoryExists(claudeData)) {
                ClaudeWindowsPathSafety.EnsureNoReparsePoints(Path.GetPathRoot(claudeData)!, claudeData);
                possibleRoots.AddRange(Directory.EnumerateDirectories(claudeData)
                    .Select(path => (claudeData, Path.GetFileName(path))));
            }

            string packages = Path.Combine(localAppData, "Packages");
            if (DirectoryExists(packages)) {
                ClaudeWindowsPathSafety.EnsureNoReparsePoints(Path.GetPathRoot(packages)!, packages);
                foreach (string package in Directory.EnumerateDirectories(packages)) {
                    cancellationToken.ThrowIfCancellationRequested();
                    possibleRoots.Add((package, Path.Combine("LocalCache", "Roaming", "Claude")));
                }
            }

            possibleRoots.Add((roamingAppData, "Claude"));
            List<IClaudeDesktopRootProbe.IRootCandidate> candidates = [];
            bool complete = true;
            bool ambiguous = false;
            int rootsWithData = 0;
            try {
                HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
                foreach ((string parent, string relativePath) in possibleRoots) {
                    cancellationToken.ThrowIfCancellationRequested();
                    string path = Path.Combine(parent, relativePath);
                    if (!visited.Add(path) || !CandidateExists(parent, relativePath, true)) {
                        continue;
                    }

                    string[] relativeFiles = ["config.json", "Local State", "Cookies", Path.Combine("Network", "Cookies")];
                    bool[] present = relativeFiles.Select(name => CandidateExists(path, name, false)).ToArray();
                    if (!present.Any(value => value)) {
                        continue;
                    }

                    rootsWithData++;
                    if (present[2] && present[3]) {
                        ambiguous = true;
                        continue;
                    }

                    if (!present[0] || !present[1] || (!present[2] && !present[3])) {
                        complete = false;
                        continue;
                    }

                    candidates.Add(new RootCandidate(path, present[2] ? relativeFiles[2] : relativeFiles[3]));
                }

                IClaudeDesktopRootProbe.IRootCandidate[] distinct = candidates
                    .GroupBy(item => (item.VolumeSerialNumber, item.FileId))
                    .Select(group => group.First())
                    .ToArray();
                foreach (IClaudeDesktopRootProbe.IRootCandidate candidate in candidates.Except(distinct)) {
                    candidate.Dispose();
                }

                return new IClaudeDesktopRootProbe.RootScan(
                    complete, ambiguous || (rootsWithData > 1 && (!complete || distinct.Length > 1)), distinct
                );
            } catch {
                foreach (IClaudeDesktopRootProbe.IRootCandidate candidate in candidates) {
                    candidate.Dispose();
                }

                throw;
            }
        }

        private static bool DirectoryExists(string path) {
            try {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) == 0) {
                    throw new InvalidDataException("A Claude candidate directory is not a directory.");
                }

                return true;
            } catch (FileNotFoundException) {
                return false;
            } catch (DirectoryNotFoundException) {
                return false;
            }
        }

        public WindowsClaudeRestartManagerProbe.Result ObserveResource(string path, CancellationToken cancellationToken) {
            return restartManager.Observe(path, cancellationToken);
        }

        public IClaudeDesktopRootProbe.IObservedProcess OpenProcess(ClaudeDesktopRootBinding.ObservedProcess observed) {
            Process process = Process.GetProcessById(observed.ProcessId);
            try {
                return new ObservedProcess(new WindowsClaudeDesktopProcessEnumerator.WindowsClaudeDesktopProcessHandle(process));
            } catch {
                process.Dispose();
                throw;
            }
        }

        public bool VerifyRunningProcesses(
            IClaudeDesktopInstallationLocator.Installation installation,
            CancellationToken cancellationToken
        ) {
            IClaudeDesktopProcessEnumerator.ClaudeDesktopProcessEnumerationResult result =
                new WindowsClaudeDesktopProcessEnumerator().Enumerate(installation, cancellationToken);
            try {
                ClaudeDesktopRootBinding.Observation? source = installation.Observation;
                if (!result.IsComplete || source is null || result.Processes.Count == 0) {
                    return false;
                }

                foreach (IClaudeDesktopProcessEnumerator.IClaudeDesktopProcessHandle process in result.Processes) {
                    if (process.HasExited || !process.HasExpectedIdentity(installation)) {
                        return false;
                    }
                }

                return source.Processes.All(observed => result.Processes.Any(process =>
                    process.Id == observed.ProcessId && process.StartTimeUtc == observed.CreatedAtUtc.UtcDateTime
                ));
            } finally {
                foreach (IClaudeDesktopProcessEnumerator.IClaudeDesktopProcessHandle process in result.Processes) {
                    process.Dispose();
                }
            }
        }

        public bool VerifyInstallation(ClaudeDesktopRootBinding.Observation observation) {
            if (observation.Kind == IClaudeDesktopInstallationLocator.InstallKind.Direct) {
                if (string.IsNullOrWhiteSpace(observation.ExecutablePath)
                    || !string.Equals(Path.GetFileName(observation.ExecutablePath), "Claude.exe", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        observation.InstallationId,
                        "direct:" + ClaudeWindowsPathSafety.ComputePathFingerprint(observation.ExecutablePath),
                        StringComparison.OrdinalIgnoreCase
                    )) {
                    return false;
                }

                string? registration = FindDirectRegistration(observation.ExecutablePath);
                if (string.Equals(registration, "conflict", StringComparison.Ordinal)
                    || !string.Equals(registration, observation.DirectRegistrationSource, StringComparison.OrdinalIgnoreCase)) {
                    return false;
                }

                if (registration is null && !IsKnownDirectPath(observation.ExecutablePath)) {
                    return false;
                }

                string pathRoot = Path.GetPathRoot(observation.ExecutablePath)
                    ?? throw new InvalidDataException("An executable has no volume root.");
                ClaudeWindowsPathSafety.EnsureNoReparsePoints(pathRoot, observation.ExecutablePath);
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(observation.ExecutablePath);
                if (version.ProductName?.Contains("Claude", StringComparison.OrdinalIgnoreCase) != true
                    || version.CompanyName?.Contains("Anthropic", StringComparison.OrdinalIgnoreCase) != true) {
                    return false;
                }

                using FileStream stream = new(observation.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
                return string.Equals(digest, observation.ExecutableSha256, StringComparison.OrdinalIgnoreCase);
            }

            if (observation.Kind != IClaudeDesktopInstallationLocator.InstallKind.Msix
                || string.IsNullOrWhiteSpace(observation.PackageFamilyName)
                || string.IsNullOrWhiteSpace(observation.PackageFullName)
                || string.IsNullOrWhiteSpace(observation.AppUserModelId)
                || !string.Equals(observation.InstallationId, "msix:" + observation.PackageFamilyName, StringComparison.OrdinalIgnoreCase)
                || !observation.AppUserModelId.StartsWith(observation.PackageFamilyName + "!", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }

            return GetRegisteredPackageFullName(observation) is not null;
        }

        public string? GetRegisteredPackageFullName(ClaudeDesktopRootBinding.Observation observation) {
            if (observation.Kind != IClaudeDesktopInstallationLocator.InstallKind.Msix
                || string.IsNullOrWhiteSpace(observation.PackageFamilyName)
                || string.IsNullOrWhiteSpace(observation.PackageFullName)
                || string.IsNullOrWhiteSpace(observation.AppUserModelId)
                || !string.Equals(observation.InstallationId, "msix:" + observation.PackageFamilyName, StringComparison.OrdinalIgnoreCase)
                || !observation.AppUserModelId.StartsWith(observation.PackageFamilyName + "!", StringComparison.OrdinalIgnoreCase)) {
                return null;
            }

            return VerifyRegisteredPackage(
                observation.PackageFamilyName,
                observation.PackageFullName,
                observation.AppUserModelId
            );
        }

        public string? FindDirectRegistration(string executablePath) {
            List<(string Path, string Source)> registrations = [];
            const string appPaths = @"Software\Microsoft\Windows\CurrentVersion\App Paths\Claude.exe";
            const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine }) {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 }) {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? app = baseKey.OpenSubKey(appPaths);
                    AddRegistration(registrations, app?.GetValue(null) as string, $"{hive}/{view}/AppPaths");
                    using RegistryKey? uninstallRoot = baseKey.OpenSubKey(uninstall);
                    if (uninstallRoot is null) {
                        continue;
                    }

                    foreach (string keyName in uninstallRoot.GetSubKeyNames()) {
                        using RegistryKey? product = uninstallRoot.OpenSubKey(keyName);
                        string? name = product?.GetValue("DisplayName") as string;
                        if (name?.Contains("Claude", StringComparison.OrdinalIgnoreCase) == true) {
                            AddRegistration(registrations, product?.GetValue("InstallLocation") as string, $"{hive}/{view}/{keyName}");
                        }
                    }
                }
            }

            if (registrations.Any(item => !string.Equals(item.Path, executablePath, StringComparison.OrdinalIgnoreCase))) {
                return "conflict";
            }

            return registrations.Select(item => item.Source).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        private static void AddRegistration(List<(string Path, string Source)> registrations, string? value, string source) {
            if (string.IsNullOrWhiteSpace(value)) {
                return;
            }

            string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (!string.Equals(Path.GetFileName(path), "Claude.exe", StringComparison.OrdinalIgnoreCase)) {
                path = Path.Combine(path, "Claude.exe");
            }

            registrations.Add((Path.GetFullPath(path), source));
        }

        private bool IsKnownDirectPath(string executablePath) {
            return string.Equals(executablePath, Path.Combine(localAppData, "Programs", "Claude", "Claude.exe"), StringComparison.OrdinalIgnoreCase)
                || string.Equals(executablePath, Path.Combine(programFiles, "Claude", "Claude.exe"), StringComparison.OrdinalIgnoreCase);
        }

        private static string? VerifyRegisteredPackage(string family, string fullName, string appUserModelId) {
            if (!IsPackageFullNameFromFamily(fullName, family)) {
                return null;
            }

            uint count = 0;
            uint length = 0;
            int result = GetPackagesByPackageFamily(family, ref count, nint.Zero, ref length, nint.Zero);
            if (result != 122 || count != 1 || length is 0 or > 32768) {
                return null;
            }

            nint pointers = Marshal.AllocHGlobal(checked((int)count * nint.Size));
            nint buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
            try {
                if (GetPackagesByPackageFamily(family, ref count, pointers, ref length, buffer) != 0 || count != 1) {
                    return null;
                }

                string? registeredPackageFullName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointers));
                if (string.IsNullOrWhiteSpace(registeredPackageFullName)
                    || OpenPackageInfoByFullName(registeredPackageFullName, 0, out nint package) != 0) {
                    return null;
                }

                try {
                    uint bytes = 0;
                    uint appCount;
                    if (GetPackageApplicationIds(package, ref bytes, nint.Zero, out appCount) != 122
                        || bytes is 0 or > 131072 || appCount is 0 or > 1024) {
                        return null;
                    }

                    nint appBuffer = Marshal.AllocHGlobal(checked((int)bytes));
                    try {
                        if (GetPackageApplicationIds(package, ref bytes, appBuffer, out appCount) != 0) {
                            return null;
                        }

                        List<string> appIds = [];
                        for (int index = 0; index < appCount; index++) {
                            string? appId = Marshal.PtrToStringUni(Marshal.ReadIntPtr(appBuffer, index * nint.Size));
                            if (appId is not null) {
                                appIds.Add(appId);
                            }
                        }

                        return MatchesDesktopApplication(appIds, appUserModelId)
                            ? registeredPackageFullName
                            : null;
                    } finally {
                        Marshal.FreeHGlobal(appBuffer);
                    }
                } finally {
                    _ = ClosePackageInfo(package);
                }
            } finally {
                Marshal.FreeHGlobal(buffer);
                Marshal.FreeHGlobal(pointers);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPackagesByPackageFamily(
            string family, ref uint count, nint packageFullNames, ref uint bufferLength, nint buffer
        );

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int OpenPackageInfoByFullName(string fullName, uint reserved, out nint package);

        [DllImport("kernel32.dll")]
        private static extern int GetPackageApplicationIds(nint package, ref uint bufferLength, nint buffer, out uint count);

        [DllImport("kernel32.dll")]
        private static extern int ClosePackageInfo(nint package);

        private sealed class RootCandidate : IClaudeDesktopRootProbe.IRootCandidate {
            private readonly List<ClaudeWindowsPathSafety.IdentityLease> entries = [];
            private readonly IReadOnlyList<(string Path, bool IsDirectory)> entryPaths;

            internal RootCandidate(string path, string cookieRelativePath) {
                RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                CookieRelativePath = cookieRelativePath;
                entryPaths = CapturePaths();
                try {
                    foreach ((string entryPath, bool isDirectory) in entryPaths) {
                        entries.Add(ClaudeWindowsPathSafety.OpenIdentity(entryPath, isDirectory));
                    }
                } catch {
                    Dispose();
                    throw;
                }
            }

            public string RootPath { get; }

            public string CookieRelativePath { get; }

            public ulong VolumeSerialNumber => entries[0].Identity.VolumeSerialNumber;

            public string FileId => entries[0].Identity.FileId;

            public void VerifyUnchanged() {
                IReadOnlyList<(string Path, bool IsDirectory)> currentPaths = CapturePaths();
                if (!entryPaths.SequenceEqual(currentPaths)) {
                    throw new IOException("The Claude data file set changed.");
                }

                foreach (ClaudeWindowsPathSafety.IdentityLease entry in entries) {
                    entry.VerifyPath();
                }
            }

            private IReadOnlyList<(string Path, bool IsDirectory)> CapturePaths() {
                List<(string Path, bool IsDirectory)> paths = [
                    (RootPath, true),
                    (Path.Combine(RootPath, "config.json"), false),
                    (Path.Combine(RootPath, "Local State"), false)
                ];
                string cookiePath = Path.Combine(RootPath, CookieRelativePath);
                string alternateCookie = string.Equals(CookieRelativePath, "Cookies", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(RootPath, "Network", "Cookies")
                    : Path.Combine(RootPath, "Cookies");
                if (CandidateExists(RootPath, Path.GetRelativePath(RootPath, alternateCookie), false)) {
                    throw new InvalidDataException("More than one Claude Cookie location exists.");
                }

                string cookieDirectory = Path.GetDirectoryName(cookiePath)
                    ?? throw new InvalidDataException("A Claude Cookie path has no directory.");
                if (!string.Equals(cookieDirectory, RootPath, StringComparison.OrdinalIgnoreCase)) {
                    paths.Add((cookieDirectory, true));
                }

                paths.Add((cookiePath, false));
                foreach (string entry in Directory.EnumerateFileSystemEntries(cookieDirectory)) {
                    string name = Path.GetFileName(entry);
                    if (!name.StartsWith("Cookies", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "Cookies", StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    if (!string.Equals(name, "Cookies-journal", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(name, "Cookies-wal", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(name, "Cookies-shm", StringComparison.OrdinalIgnoreCase)) {
                        throw new InvalidDataException("A Claude Cookie sidecar is unsupported.");
                    }

                    paths.Add((entry, false));
                }

                string sessions = Path.Combine(RootPath, "claude-code-sessions");
                if (CandidateExists(RootPath, "claude-code-sessions", true)) {
                    Stack<string> pending = new();
                    pending.Push(sessions);
                    while (pending.Count > 0) {
                        string directory = pending.Pop();
                        paths.Add((directory, true));
                        foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) {
                            FileAttributes attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) {
                                throw new InvalidDataException("A Claude Code entry is a reparse point.");
                            }

                            if ((attributes & FileAttributes.Directory) != 0) {
                                pending.Push(entry);
                            } else {
                                paths.Add((entry, false));
                            }

                            if (paths.Count + pending.Count > 16384) {
                                throw new InvalidDataException("The Claude Code file set exceeds the supported limit.");
                            }
                        }
                    }
                }

                return paths.Take(1).Concat(paths.Skip(1).OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)).ToArray();
            }

            public void Dispose() {
                foreach (ClaudeWindowsPathSafety.IdentityLease entry in entries) {
                    entry.Dispose();
                }
            }
        }

        private sealed class ObservedProcess : IClaudeDesktopRootProbe.IObservedProcess {
            private readonly WindowsClaudeDesktopProcessEnumerator.WindowsClaudeDesktopProcessHandle handle;

            internal ObservedProcess(WindowsClaudeDesktopProcessEnumerator.WindowsClaudeDesktopProcessHandle handle) {
                this.handle = handle;
            }

            public WindowsClaudeDesktopProcessEnumerator.ProcessIdentity ReadIdentity() {
                return handle.ReadIdentity();
            }

            public void Dispose() {
                handle.Dispose();
            }
        }
    }
}
