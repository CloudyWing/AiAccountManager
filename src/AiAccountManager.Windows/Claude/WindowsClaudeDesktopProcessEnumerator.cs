using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Windows.Runtime;
using ClaudeDesktopProcessEnumerationResult =
    CloudyWing.AiAccountManager.Windows.Claude.IClaudeDesktopProcessEnumerator.ClaudeDesktopProcessEnumerationResult;
using IClaudeDesktopProcessHandle =
    CloudyWing.AiAccountManager.Windows.Claude.IClaudeDesktopProcessEnumerator.IClaudeDesktopProcessHandle;

namespace CloudyWing.AiAccountManager.Windows.Claude;

internal sealed class WindowsClaudeDesktopProcessEnumerator : IClaudeDesktopProcessEnumerator {
    private const string DesktopProcessName = "Claude";
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;
    private const uint TokenQuery = 0x0008;

    public ClaudeDesktopProcessEnumerationResult Enumerate(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();

        Process[] processes;
        try {
            processes = Process.GetProcessesByName(DesktopProcessName);
        } catch (Exception failure) when (IsProcessAccessFailure(failure)) {
            return new ClaudeDesktopProcessEnumerationResult(false, []);
        }

        List<IClaudeDesktopProcessHandle> matching = [];
        HashSet<Process> matchingProcesses = [];
        bool complete = true;
        bool completedEnumeration = false;
        try {
            foreach (Process process in processes) {
                cancellationToken.ThrowIfCancellationRequested();
                try {
                    if (process.HasExited || IsClaudeCodeCli(process)) {
                        continue;
                    }

                    WindowsClaudeDesktopProcessHandle handle = new(process);
                    if (!handle.HasExpectedIdentity(installation)) {
                        complete = false;
                    } else {
                        matching.Add(handle);
                        matchingProcesses.Add(process);
                    }
                } catch (Exception failure) when (IsProcessAccessFailure(failure)) {
                    complete = false;
                }
            }

            completedEnumeration = true;
            return new ClaudeDesktopProcessEnumerationResult(complete, matching);
        } finally {
            if (!completedEnumeration) {
                DisposeHandles(matching);
            }

            foreach (Process process in processes) {
                if (!matchingProcesses.Contains(process)) {
                    process.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// 判斷程序是否為 Claude Code CLI。
    /// </summary>
    /// <remarks>
    /// Claude Code CLI 的執行檔同樣名為 `claude.exe`，但不使用 Claude Desktop 的資料根目錄，因此不列入 Desktop 程序集合。
    /// </remarks>
    /// <param name="process">要判斷的程序。</param>
    /// <returns>執行檔產品名稱為 `Claude Code` 且由 Anthropic 發行時為 <see langword="true" />。</returns>
    internal static bool IsClaudeCodeCli(Process process) {
        uint pathLength = 32768;
        StringBuilder path = new(checked((int)pathLength));
        if (!QueryFullProcessImageName(process.Handle, 0, path, ref pathLength)) {
            return false;
        }

        FileVersionInfo version = FileVersionInfo.GetVersionInfo(path.ToString());
        return IsClaudeCodeProduct(version.ProductName, version.CompanyName);
    }

    /// <summary>
    /// 依執行檔版本資訊判斷是否為 Claude Code CLI。
    /// </summary>
    /// <param name="productName">執行檔產品名稱。</param>
    /// <param name="companyName">執行檔發行者名稱。</param>
    /// <returns>產品名稱為 `Claude Code` 且發行者含 `Anthropic` 時為 <see langword="true" />。</returns>
    internal static bool IsClaudeCodeProduct(string? productName, string? companyName) {
        return string.Equals(productName?.Trim(), "Claude Code", StringComparison.OrdinalIgnoreCase)
            && companyName?.Contains("Anthropic", StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static bool MatchesMsixIdentity(
        ClaudeDesktopRootBinding.Observation expected,
        ProcessIdentity actual
    ) {
        if (expected.Kind != IClaudeDesktopInstallationLocator.InstallKind.Msix
            || string.IsNullOrWhiteSpace(expected.PackageFamilyName)
            || string.IsNullOrWhiteSpace(expected.PackageFullName)
            || string.IsNullOrWhiteSpace(expected.AppUserModelId)
            || string.IsNullOrWhiteSpace(actual.PackageFamilyName)
            || string.IsNullOrWhiteSpace(actual.PackageFullName)
            || string.IsNullOrWhiteSpace(actual.AppUserModelId)
            || !string.Equals(actual.AppUserModelId, expected.AppUserModelId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(actual.PackageFamilyName, expected.PackageFamilyName, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        return WindowsClaudeDesktopInstallationLocator.IsPackageFullNameFromFamily(
                expected.PackageFullName,
                expected.PackageFamilyName
            )
            && WindowsClaudeDesktopInstallationLocator.IsPackageFullNameFromFamily(
                actual.PackageFullName,
                actual.PackageFamilyName
            );
    }

    private static string? ReadPackageProperty(nint processHandle, PackagePropertyReader reader, bool allowNoPackage) {
        uint bufferLength = 0;
        int result = reader(processHandle, ref bufferLength, null);
        if (allowNoPackage && result == AppModelErrorNoPackage) {
            return null;
        }

        if (result != ErrorInsufficientBuffer || bufferLength is 0 or > 1024) {
            throw new InvalidOperationException("Claude Desktop process package identity is unavailable.");
        }

        StringBuilder buffer = new(checked((int)bufferLength));
        result = reader(processHandle, ref bufferLength, buffer);
        if (result != 0 || buffer.Length == 0) {
            throw new InvalidOperationException("Claude Desktop process package identity changed.");
        }

        return buffer.ToString();
    }

    private static bool IsProcessAccessFailure(Exception failure) {
        return failure is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException
            or ArgumentException;
    }

    private static void DisposeHandles(IEnumerable<IClaudeDesktopProcessHandle> handles) {
        foreach (IClaudeDesktopProcessHandle handle in handles) {
            handle.Dispose();
        }
    }

    private delegate int PackagePropertyReader(nint processHandle, ref uint length, StringBuilder? value);

    [DllImport("kernel32.dll", EntryPoint = "GetApplicationUserModelId", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(nint processHandle, ref uint length, StringBuilder? value);

    [DllImport("kernel32.dll", EntryPoint = "GetPackageFullName", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(nint processHandle, ref uint length, StringBuilder? value);

    [DllImport("kernel32.dll", EntryPoint = "GetPackageFamilyName", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(nint processHandle, ref uint length, StringBuilder? value);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        nint processHandle,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint processHandle, uint flags, StringBuilder path, ref uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out SafeAccessTokenHandle token);

    internal sealed record ProcessIdentity(
        int ProcessId,
        DateTime StartTimeUtc,
        string WindowsUserSid,
        string ExecutablePath,
        string? ExecutableSha256,
        string? AppUserModelId,
        string? PackageFamilyName,
        string? PackageFullName
    );

    internal sealed class WindowsClaudeDesktopProcessHandle : IClaudeDesktopProcessHandle {
        private readonly Process process;

        public WindowsClaudeDesktopProcessHandle(Process process) {
            this.process = process;
            StartTimeUtc = ReadCreationTime(process.Handle);
        }

        public int Id => process.Id;

        public DateTime StartTimeUtc { get; }

        public bool HasExited => process.HasExited;

        public bool HasExpectedIdentity(IClaudeDesktopInstallationLocator.Installation installation) {
            ClaudeDesktopRootBinding.Observation? source = installation.Observation ?? installation.Binding?.Source;
            if (source is null) {
                return false;
            }

            try {
                ProcessIdentity identity = ReadIdentity();
                if (identity.StartTimeUtc != StartTimeUtc
                    || !string.Equals(identity.WindowsUserSid, source.WindowsUserSid, StringComparison.OrdinalIgnoreCase)) {
                    return false;
                }

                if (source.Kind == IClaudeDesktopInstallationLocator.InstallKind.Direct) {
                    return identity.PackageFullName is null
                        && string.Equals(identity.ExecutablePath, source.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(identity.ExecutableSha256, source.ExecutableSha256, StringComparison.OrdinalIgnoreCase);
                }

                return MatchesMsixIdentity(source, identity);
            } catch (Exception failure) when (IsProcessAccessFailure(failure)) {
                return false;
            }
        }

        internal ProcessIdentity ReadIdentity() {
            nint processHandle = process.Handle;
            DateTime created = ReadCreationTime(processHandle);
            if (!OpenProcessToken(processHandle, TokenQuery, out SafeAccessTokenHandle token)) {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            string sid;
            using (token)
            using (WindowsIdentity identity = new(token.DangerousGetHandle())) {
                sid = identity.User?.Value ?? throw new InvalidOperationException("A process SID is unavailable.");
            }

            uint pathLength = 32768;
            StringBuilder path = new(checked((int)pathLength));
            if (!QueryFullProcessImageName(processHandle, 0, path, ref pathLength)) {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            string executablePath = Path.GetFullPath(path.ToString());
            string? packageFullName = ReadPackageProperty(processHandle, GetPackageFullName, allowNoPackage: true);
            string? packageFamilyName = null;
            string? appUserModelId = null;
            string? executableSha256 = null;
            if (packageFullName is null) {
                string pathRoot = Path.GetPathRoot(executablePath)
                    ?? throw new InvalidDataException("A process executable path has no root.");
                ClaudeWindowsPathSafety.EnsureNoReparsePoints(pathRoot, executablePath);
                using FileStream stream = new(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                executableSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            } else {
                packageFamilyName = ReadPackageProperty(processHandle, GetPackageFamilyName, allowNoPackage: false);
                appUserModelId = ReadPackageProperty(processHandle, GetApplicationUserModelId, allowNoPackage: false);
            }

            if (ReadCreationTime(processHandle) != created || process.HasExited) {
                throw new InvalidOperationException("A Claude Desktop process changed during identification.");
            }

            return new ProcessIdentity(
                process.Id, created, sid, executablePath, executableSha256,
                appUserModelId, packageFamilyName, packageFullName
            );
        }

        private static DateTime ReadCreationTime(nint processHandle) {
            if (!GetProcessTimes(processHandle, out long created, out _, out _, out _)) {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            return DateTime.FromFileTimeUtc(created);
        }

        public void RequestGracefulClose() {
            _ = WindowsGracefulShutdown.RequestShutdown([(process.Id, StartTimeUtc.ToFileTimeUtc())]);
        }

        public void Dispose() {
            process.Dispose();
        }
    }
}
