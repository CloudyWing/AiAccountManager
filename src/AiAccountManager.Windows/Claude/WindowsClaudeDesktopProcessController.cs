using System.Diagnostics;
using System.Runtime.InteropServices;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using ClaudeDesktopProcessEnumerationResult =
    CloudyWing.AiAccountManager.Windows.Claude.IClaudeDesktopProcessEnumerator.ClaudeDesktopProcessEnumerationResult;
using IClaudeDesktopProcessHandle =
    CloudyWing.AiAccountManager.Windows.Claude.IClaudeDesktopProcessEnumerator.IClaudeDesktopProcessHandle;

namespace CloudyWing.AiAccountManager.Windows.Claude;

/// <summary>
/// 依唯一 Claude Desktop 安裝描述查詢、正常關閉與重新啟動桌面程序。
/// </summary>
public sealed class WindowsClaudeDesktopProcessController : IClaudeDesktopProcessController {
    private const string DesktopExecutableName = "Claude.exe";
    private const int PollIntervalMilliseconds = 100;
    private readonly IClaudeDesktopProcessEnumerator processEnumerator;

    /// <summary>
    /// 建立使用 Windows 程序列舉器的 Claude Desktop 程序控制器。
    /// </summary>
    public WindowsClaudeDesktopProcessController()
        : this(new WindowsClaudeDesktopProcessEnumerator()) {
    }

    internal WindowsClaudeDesktopProcessController(
        IClaudeDesktopProcessEnumerator processEnumerator,
        TimeSpan? pollInterval = null
    ) {
        ArgumentNullException.ThrowIfNull(processEnumerator);
        this.processEnumerator = processEnumerator;
        PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(PollIntervalMilliseconds);
        if (PollInterval <= TimeSpan.Zero || PollInterval > TimeSpan.FromSeconds(5)) {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }
    }

    private TimeSpan PollInterval { get; }

    /// <inheritdoc />
    public Task<IClaudeDesktopProcessController.ProcessStatus> GetStatusAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        try {
            ValidateInstallation(installation);
        } catch (Exception failure) when (IsProcessAccessFailure(failure) || failure is IOException or ArgumentException) {
            return Task.FromResult(IClaudeDesktopProcessController.ProcessStatus.Unknown);
        }

        if (!HasSourceEvidence(installation)) {
            return Task.FromResult(IClaudeDesktopProcessController.ProcessStatus.Unknown);
        }

        ClaudeDesktopProcessEnumerationResult result = EnumerateProcesses(
            installation,
            cancellationToken
        );
        try {
            if (!result.IsComplete) {
                return Task.FromResult(IClaudeDesktopProcessController.ProcessStatus.Unknown);
            }

            foreach (IClaudeDesktopProcessHandle process in result.Processes) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!process.HasExited && !process.HasExpectedIdentity(installation)) {
                    return Task.FromResult(IClaudeDesktopProcessController.ProcessStatus.Unknown);
                }
            }

            foreach (IClaudeDesktopProcessHandle process in result.Processes) {
                if (!process.HasExited) {
                    return Task.FromResult(IClaudeDesktopProcessController.ProcessStatus.Running);
                }
            }

            return Task.FromResult(IClaudeDesktopProcessController.ProcessStatus.NotRunning);
        } catch (Exception failure) when (IsProcessAccessFailure(failure)) {
            return Task.FromResult(IClaudeDesktopProcessController.ProcessStatus.Unknown);
        } finally {
            DisposeHandles(result.Processes);
        }
    }

    /// <inheritdoc />
    public async Task<bool> CloseGracefullyAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        TimeSpan timeout,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(installation);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10)) {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try {
            ValidateInstallation(installation);
        } catch (Exception failure) when (IsProcessAccessFailure(failure) || failure is IOException or ArgumentException) {
            return false;
        }

        if (!HasSourceEvidence(installation)) {
            return false;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        HashSet<(int ProcessId, long StartTimeUtcTicks)> closeRequests = [];
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            ClaudeDesktopProcessEnumerationResult result = EnumerateProcesses(
                installation,
                cancellationToken
            );
            bool running = false;
            try {
                if (!result.IsComplete) {
                    return false;
                }

                foreach (IClaudeDesktopProcessHandle process in result.Processes) {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!process.HasExited && !process.HasExpectedIdentity(installation)) {
                        return false;
                    }
                }

                foreach (IClaudeDesktopProcessHandle process in result.Processes) {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (process.HasExited) {
                        continue;
                    }

                    running = true;
                    (int ProcessId, long StartTimeUtcTicks) processKey = (process.Id, process.StartTimeUtc.Ticks);
                    if (!closeRequests.Contains(processKey)) {
                        if (!process.HasExpectedIdentity(installation) || process.HasExited) {
                            return false;
                        }

                        process.RequestGracefulClose();
                        closeRequests.Add(processKey);
                    }
                }
            } catch (Exception failure) when (IsProcessAccessFailure(failure)) {
                return false;
            } finally {
                DisposeHandles(result.Processes);
            }

            if (!running) {
                return stopwatch.Elapsed < timeout;
            }

            TimeSpan remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero) {
                return false;
            }

            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task LaunchAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateInstallation(installation);
        if (!HasSourceEvidence(installation)) {
            throw new InvalidOperationException("Claude Desktop source evidence is unavailable.");
        }

        if (installation.Kind == IClaudeDesktopInstallationLocator.InstallKind.Direct) {
            ProcessStartInfo startInfo = new(installation.ExecutablePath!) {
                UseShellExecute = true
            };
            using Process? process = Process.Start(startInfo);
            if (process is null) {
                throw new InvalidOperationException("Claude Desktop could not be started.");
            }

            return Task.CompletedTask;
        }

        return ActivateMsixAsync(installation.AppUserModelId!, cancellationToken);
    }

    private static void ValidateInstallation(IClaudeDesktopInstallationLocator.Installation installation) {
        if (string.IsNullOrWhiteSpace(installation.InstallationId)
            || string.IsNullOrWhiteSpace(installation.DataRoot)
            || string.IsNullOrWhiteSpace(installation.DataRootFingerprint)
            || !string.Equals(
                ClaudeWindowsPathSafety.ComputePathFingerprint(installation.DataRoot),
                installation.DataRootFingerprint,
                StringComparison.OrdinalIgnoreCase
            )) {
            throw new ArgumentException("Claude Desktop installation metadata is invalid.", nameof(installation));
        }

        ClaudeWindowsPathSafety.EnsureNoReparsePoints(installation.DataRoot, installation.DataRoot);

        if (installation.Kind == IClaudeDesktopInstallationLocator.InstallKind.Direct) {
            if (string.IsNullOrWhiteSpace(installation.ExecutablePath)
                || !string.Equals(
                    Path.GetFileName(installation.ExecutablePath),
                    DesktopExecutableName,
                    StringComparison.OrdinalIgnoreCase
                )
                || !File.Exists(installation.ExecutablePath)
                || (File.GetAttributes(installation.ExecutablePath) & FileAttributes.ReparsePoint) != 0
                || !string.Equals(
                    installation.InstallationId,
                    "direct:" + ClaudeWindowsPathSafety.ComputePathFingerprint(installation.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase
                )) {
                throw new ArgumentException("Claude direct installation metadata is invalid.", nameof(installation));
            }

            return;
        }

        if (installation.Kind != IClaudeDesktopInstallationLocator.InstallKind.Msix
            || !IsValidAppUserModelId(installation.AppUserModelId)
            || !string.Equals(
                installation.InstallationId,
                "msix:" + installation.AppUserModelId!.Split('!')[0],
                StringComparison.OrdinalIgnoreCase
            )) {
            throw new ArgumentException("Claude MSIX installation metadata is invalid.", nameof(installation));
        }
    }

    private static bool IsValidAppUserModelId(string? appUserModelId) {
        if (string.IsNullOrWhiteSpace(appUserModelId)) {
            return false;
        }

        int separator = appUserModelId.IndexOf('!');
        return separator > 0
            && separator < appUserModelId.Length - 1
            && appUserModelId.Contains("Claude", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSourceEvidence(IClaudeDesktopInstallationLocator.Installation installation) {
        ClaudeDesktopRootBinding.Observation? source =
            installation.Observation ?? installation.Binding?.Source;
        return source is not null
            && source.Kind == installation.Kind
            && string.Equals(source.InstallationId, installation.InstallationId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.DataRoot, installation.DataRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.AppUserModelId, installation.AppUserModelId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.ExecutablePath, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(source.WindowsUserSid);
    }

    private static Task ActivateMsixAsync(string appUserModelId, CancellationToken cancellationToken) {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread activationThread = new(() => {
            try {
                cancellationToken.ThrowIfCancellationRequested();
                ActivateMsix(appUserModelId);
                completion.TrySetResult();
            } catch (OperationCanceledException failure) {
                completion.TrySetCanceled(failure.CancellationToken);
            } catch (Exception failure) {
                completion.TrySetException(failure);
            }
        }) {
            IsBackground = true,
            Name = "Claude Desktop activation"
        };
        activationThread.SetApartmentState(ApartmentState.STA);
        activationThread.Start();
        return completion.Task;
    }

    private static void ActivateMsix(string appUserModelId) {
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

    private static bool IsProcessAccessFailure(Exception failure) {
        return failure is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException
            or UnauthorizedAccessException
            or System.Security.SecurityException;
    }

    private ClaudeDesktopProcessEnumerationResult EnumerateProcesses(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    ) {
        try {
            return processEnumerator.Enumerate(installation, cancellationToken);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception failure) when (IsProcessAccessFailure(failure)) {
            return new ClaudeDesktopProcessEnumerationResult(false, []);
        }
    }

    private static void DisposeHandles(IEnumerable<IClaudeDesktopProcessHandle> processes) {
        foreach (IClaudeDesktopProcessHandle process in processes) {
            process.Dispose();
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
