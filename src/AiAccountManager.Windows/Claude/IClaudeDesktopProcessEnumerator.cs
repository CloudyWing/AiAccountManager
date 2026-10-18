using CloudyWing.AiAccountManager.Application.Claude;

namespace CloudyWing.AiAccountManager.Windows.Claude;

internal interface IClaudeDesktopProcessEnumerator {
    ClaudeDesktopProcessEnumerationResult Enumerate(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    );

    public sealed record ClaudeDesktopProcessEnumerationResult(
        bool IsComplete,
        IReadOnlyList<IClaudeDesktopProcessHandle> Processes
    );

    public interface IClaudeDesktopProcessHandle : IDisposable {
        int Id { get; }

        DateTime StartTimeUtc { get; }

        bool HasExited { get; }

        bool HasExpectedIdentity(IClaudeDesktopInstallationLocator.Installation installation);

        void RequestGracefulClose();
    }
}
