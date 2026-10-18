using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;

namespace CloudyWing.AiAccountManager.Windows.Claude;

internal interface IClaudeDesktopRootProbe {
    string GetCurrentUserSid();

    RootScan OpenCandidates(CancellationToken cancellationToken);

    WindowsClaudeRestartManagerProbe.Result ObserveResource(string path, CancellationToken cancellationToken);

    IObservedProcess OpenProcess(ClaudeDesktopRootBinding.ObservedProcess observed);

    bool VerifyInstallation(ClaudeDesktopRootBinding.Observation observation);

    string? GetRegisteredPackageFullName(ClaudeDesktopRootBinding.Observation observation);

    bool VerifyRunningProcesses(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    );

    string? FindDirectRegistration(string executablePath);

    internal sealed record RootScan(bool IsComplete, bool IsAmbiguous, IReadOnlyList<IRootCandidate> Candidates);

    internal interface IRootCandidate : IDisposable {
        string RootPath { get; }

        string CookieRelativePath { get; }

        ulong VolumeSerialNumber { get; }

        string FileId { get; }

        void VerifyUnchanged();
    }

    internal interface IObservedProcess : IDisposable {
        WindowsClaudeDesktopProcessEnumerator.ProcessIdentity ReadIdentity();
    }
}
