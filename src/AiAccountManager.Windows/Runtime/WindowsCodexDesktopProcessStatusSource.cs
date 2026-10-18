using System.Diagnostics;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;

namespace CloudyWing.AiAccountManager.Windows.Runtime;

/// <summary>
/// 依 Codex Desktop 套件路徑檢查 `ChatGPT.exe` 程序中繼資料。
/// </summary>
public sealed class WindowsCodexDesktopProcessStatusSource : ICodexDesktopProcessStatusSource {
    private const string DesktopExecutableName = "ChatGPT.exe";
    private const string PackageDirectoryPrefix = "OpenAI.Codex_";
    private readonly string windowsAppsDirectory;

    /// <summary>
    /// 初始化 Codex Desktop 程序狀態來源。
    /// </summary>
    public WindowsCodexDesktopProcessStatusSource() {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        windowsAppsDirectory = string.IsNullOrWhiteSpace(programFiles)
            ? ""
            : Path.GetFullPath(Path.Combine(programFiles, "WindowsApps"));
    }

    /// <inheritdoc />
    public Task<CodexDesktopProcessStatusSnapshot> ReadStatusAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(windowsAppsDirectory)) {
            return Task.FromResult(Snapshot(CodexDesktopProcessStatus.Unknown));
        }

        Process[] processes;
        try {
            processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(DesktopExecutableName));
        } catch (Exception) {
            return Task.FromResult(Snapshot(CodexDesktopProcessStatus.Unknown));
        }

        if (processes.Length == 0) {
            return Task.FromResult(Snapshot(CodexDesktopProcessStatus.NotRunning));
        }

        bool hasUnknownProcess = false;
        bool hasPackagedProcess = false;
        try {
            foreach (Process process in processes) {
                cancellationToken.ThrowIfCancellationRequested();
                try {
                    string? executablePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executablePath)) {
                        hasUnknownProcess = true;
                    } else if (IsPackagedCodexExecutable(executablePath)) {
                        hasPackagedProcess = true;
                    } else {
                        hasUnknownProcess = true;
                    }
                } catch (Exception) {
                    hasUnknownProcess = true;
                }
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } finally {
            foreach (Process process in processes) {
                process.Dispose();
            }
        }

        return Task.FromResult(Snapshot(
            hasPackagedProcess
                ? CodexDesktopProcessStatus.Running
                : hasUnknownProcess
                    ? CodexDesktopProcessStatus.Unknown
                    : CodexDesktopProcessStatus.NotRunning
        ));
    }

    private bool IsPackagedCodexExecutable(string executablePath) {
        string fullPath = Path.GetFullPath(executablePath);
        if (!string.Equals(
            Path.GetFileName(fullPath),
            DesktopExecutableName,
            StringComparison.OrdinalIgnoreCase
        )) {
            return false;
        }

        string packageRootPrefix = Path.TrimEndingDirectorySeparator(windowsAppsDirectory)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(packageRootPrefix, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        string relativePath = Path.GetRelativePath(windowsAppsDirectory, fullPath);
        string? packageDirectoryName = relativePath
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries
            )
            .FirstOrDefault();
        return packageDirectoryName is not null && packageDirectoryName.StartsWith(
            PackageDirectoryPrefix,
            StringComparison.OrdinalIgnoreCase
        );
    }

    private static CodexDesktopProcessStatusSnapshot Snapshot(CodexDesktopProcessStatus status) {
        return new CodexDesktopProcessStatusSnapshot(status, DateTimeOffset.UtcNow);
    }
}
