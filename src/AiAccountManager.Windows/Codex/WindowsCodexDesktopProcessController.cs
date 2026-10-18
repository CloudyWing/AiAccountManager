using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;
using CloudyWing.AiAccountManager.Windows.Runtime;

namespace CloudyWing.AiAccountManager.Windows.Codex;

/// <summary>
/// 只針對 `WindowsApps\OpenAI.Codex_*` 套件中的 `ChatGPT.exe` 進行正常關閉與重新開啟。
/// </summary>
public sealed class WindowsCodexDesktopProcessController : ICodexDesktopProcessController {
    private const string DesktopProcessName = "ChatGPT";
    private const string PackageDirectoryPrefix = "OpenAI.Codex_";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private readonly string windowsAppsDirectory;

    /// <summary>
    /// 建立 Codex Desktop 程序控制器。
    /// </summary>
    public WindowsCodexDesktopProcessController() {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        windowsAppsDirectory = string.IsNullOrWhiteSpace(programFiles)
            ? ""
            : Path.GetFullPath(Path.Combine(programFiles, "WindowsApps"));
    }

    /// <inheritdoc />
    public Task<CodexDesktopProcessStatus> GetStatusAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(windowsAppsDirectory)) {
            return Task.FromResult(CodexDesktopProcessStatus.Unknown);
        }

        Process[] processes;
        try {
            processes = Process.GetProcessesByName(DesktopProcessName);
        } catch (Exception failure) when (IsProcessAccessFailure(failure)) {
            return Task.FromResult(CodexDesktopProcessStatus.Unknown);
        }

        try {
            // 同名的其他 OpenAI 程式（例如 ChatGPT）不視為 Codex Desktop；無法讀取路徑的程序則無法排除，視為未知。
            bool hasUnknown = false;
            foreach (Process process in processes) {
                cancellationToken.ThrowIfCancellationRequested();
                try {
                    if (process.HasExited) {
                        continue;
                    }

                    string? executablePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executablePath)) {
                        hasUnknown = true;
                    } else if (GetPackageDirectory(executablePath) is not null) {
                        return Task.FromResult(CodexDesktopProcessStatus.Running);
                    }
                } catch (Exception failure) when (IsProcessAccessFailure(failure)) {
                    hasUnknown = true;
                }
            }

            return Task.FromResult(hasUnknown ? CodexDesktopProcessStatus.Unknown : CodexDesktopProcessStatus.NotRunning);
        } finally {
            foreach (Process process in processes) {
                process.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public async Task<ICodexDesktopProcessController.CloseResult> CloseGracefullyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken
    ) {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (string.IsNullOrWhiteSpace(windowsAppsDirectory)) {
            return new(false, null);
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        HashSet<(int ProcessId, long StartFileTime)> closeRequests = [];
        string? appUserModelId = null;
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            Process[] processes;
            try {
                processes = Process.GetProcessesByName(DesktopProcessName);
            } catch (Exception failure) when (IsProcessAccessFailure(failure)) {
                return new(false, appUserModelId);
            }

            List<(int ProcessId, long StartFileTime)> desktopProcesses = [];
            try {
                foreach (Process process in processes) {
                    string? packageDirectory;
                    long startFileTime;
                    try {
                        if (process.HasExited) {
                            continue;
                        }

                        packageDirectory = GetPackageDirectory(process.MainModule?.FileName);
                        startFileTime = process.StartTime.ToFileTime();
                    } catch (Exception failure) when (IsProcessAccessFailure(failure)) {
                        return new(false, appUserModelId);
                    }

                    if (packageDirectory is null) {
                        continue;
                    }

                    desktopProcesses.Add((process.Id, startFileTime));
                    appUserModelId ??= TryReadAppUserModelId(packageDirectory);
                }
            } finally {
                foreach (Process process in processes) {
                    process.Dispose();
                }
            }

            if (desktopProcesses.Count == 0) {
                return new(true, appUserModelId);
            }

            TimeSpan remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero) {
                return new(false, appUserModelId);
            }

            // 關閉視窗只會縮到系統匣，因此由 Restart Manager 對主程序送出結束工作階段通知。
            (int ProcessId, long StartFileTime)[] shutdownTargets = GetRootProcesses(desktopProcesses)
                .Where(closeRequests.Add)
                .ToArray();
            if (shutdownTargets.Length > 0) {
                try {
                    await Task.Run(() => WindowsGracefulShutdown.RequestShutdown(shutdownTargets), CancellationToken.None)
                        .WaitAsync(remaining, cancellationToken)
                        .ConfigureAwait(false);
                } catch (TimeoutException) {
                }

                continue;
            }

            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task LaunchAsync(string appUserModelId, CancellationToken cancellationToken) {
        if (!IsValidAppUserModelId(appUserModelId)) {
            throw new ArgumentException("Codex Desktop AppUserModelId is invalid.", nameof(appUserModelId));
        }

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread activationThread = new(() => {
            try {
                cancellationToken.ThrowIfCancellationRequested();
                Activate(appUserModelId);
                completion.TrySetResult();
            } catch (OperationCanceledException failure) {
                completion.TrySetCanceled(failure.CancellationToken);
            } catch (Exception failure) {
                completion.TrySetException(failure);
            }
        }) {
            IsBackground = true,
            Name = "Codex Desktop activation"
        };
        activationThread.SetApartmentState(ApartmentState.STA);
        activationThread.Start();
        return completion.Task;
    }

    internal static string? GetAppUserModelId(string packageDirectoryName, string applicationId) {
        // 套件目錄名稱格式為 `<Name>_<Version>_<Architecture>_<ResourceId>_<PublisherId>`，Family Name 為 `<Name>_<PublisherId>`。
        string[] segments = packageDirectoryName.Split('_');
        if (segments.Length < 5
            || !packageDirectoryName.StartsWith(PackageDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(segments[^1])
            || string.IsNullOrWhiteSpace(applicationId)) {
            return null;
        }

        return $"{segments[0]}_{segments[^1]}!{applicationId}";
    }

    private string? GetPackageDirectory(string? executablePath) {
        if (string.IsNullOrWhiteSpace(executablePath)) {
            return null;
        }

        string fullPath = Path.GetFullPath(executablePath);
        string packageRootPrefix = Path.TrimEndingDirectorySeparator(windowsAppsDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(packageRootPrefix, StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        string? packageDirectoryName = Path.GetRelativePath(windowsAppsDirectory, fullPath)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return packageDirectoryName is not null
            && packageDirectoryName.StartsWith(PackageDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(windowsAppsDirectory, packageDirectoryName)
                : null;
    }

    private static string? TryReadAppUserModelId(string packageDirectory) {
        try {
            string manifestPath = Path.Combine(packageDirectory, "AppxManifest.xml");
            XmlReaderSettings settings = new() {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true
            };
            using XmlReader reader = XmlReader.Create(manifestPath, settings);
            while (reader.Read()) {
                // 套件另含 `codex-command-runner.exe` 等 Application，只取執行檔為 `ChatGPT.exe` 者。
                if (reader.NodeType == XmlNodeType.Element
                    && string.Equals(reader.LocalName, "Application", StringComparison.Ordinal)
                    && reader.GetAttribute("Id") is { Length: > 0 } applicationId
                    && string.Equals(
                        Path.GetFileName(reader.GetAttribute("Executable")),
                        DesktopProcessName + ".exe",
                        StringComparison.OrdinalIgnoreCase
                    )) {
                    return GetAppUserModelId(Path.GetFileName(packageDirectory), applicationId);
                }
            }
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or XmlException) {
        }

        return null;
    }

    private static bool IsValidAppUserModelId(string? appUserModelId) {
        if (string.IsNullOrWhiteSpace(appUserModelId)) {
            return false;
        }

        int separator = appUserModelId.IndexOf('!');
        return separator > 0
            && separator < appUserModelId.Length - 1
            && appUserModelId.StartsWith(PackageDirectoryPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProcessAccessFailure(Exception failure) {
        return failure is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException
            or UnauthorizedAccessException;
    }

    private static IEnumerable<(int ProcessId, long StartFileTime)> GetRootProcesses(
        IReadOnlyList<(int ProcessId, long StartFileTime)> desktopProcesses
    ) {
        // Codex Desktop 為多程序架構，只通知父程序不是 Codex Desktop 的主程序；子程序隨主程序結束。
        Dictionary<int, int> parentProcessIds = WindowsGracefulShutdown.GetParentProcessIds();
        HashSet<int> desktopProcessIds = desktopProcesses.Select(process => process.ProcessId).ToHashSet();
        return desktopProcesses.Where(process =>
            !parentProcessIds.TryGetValue(process.ProcessId, out int parentProcessId)
            || !desktopProcessIds.Contains(parentProcessId)
        );
    }

    private static void Activate(string appUserModelId) {
        IApplicationActivationManager? activationManager = null;
        try {
            activationManager = (IApplicationActivationManager)new ApplicationActivationManager();
            int result = activationManager.ActivateApplication(appUserModelId, null, 0, out _);
            Marshal.ThrowExceptionForHR(result);
        } finally {
            if (activationManager is not null && Marshal.IsComObject(activationManager)) {
                Marshal.FinalReleaseComObject(activationManager);
            }
        }
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    [ClassInterface(ClassInterfaceType.None)]
    private class ApplicationActivationManager {
    }

    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            uint options,
            out uint processId
        );

        [PreserveSig]
        int ActivateForFile(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            nint itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string verb,
            out uint processId
        );

        [PreserveSig]
        int ActivateForProtocol(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            nint itemArray,
            out uint processId
        );
    }
}
