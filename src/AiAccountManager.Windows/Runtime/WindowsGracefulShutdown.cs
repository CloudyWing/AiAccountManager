using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CloudyWing.AiAccountManager.Windows.Runtime;

/// <summary>
/// 以 Restart Manager 對指定程序送出結束工作階段通知，與 Windows 登出時相同，不強制終止。
/// </summary>
/// <remarks>
/// Electron 應用程式關閉視窗時可能只縮到系統匣，因此不使用 `CloseMainWindow`。沒有視窗的子程序收到通知時不採取動作，會隨主程序結束。
/// 主程序已縮到系統匣而沒有可見視窗時，Restart Manager 不採取動作，此時改由 <see cref="SendEndSession(int, long)" /> 直接送出結束工作階段訊息。
/// </remarks>
internal static class WindowsGracefulShutdown {
    private const uint SnapshotProcessFlag = 0x00000002;
    private const uint ShutdownOnlyRegistered = 0x10;
    private const int SessionKeyLength = 32;
    private const uint QueryEndSessionMessage = 0x0011;
    private const uint EndSessionMessage = 0x0016;
    private const nint EndSessionCloseApplication = 0x1;
    private const uint SendAbortIfHung = 0x0002;
    private const uint MessageTimeoutMilliseconds = 5000;

    /// <summary>
    /// 對指定程序送出結束工作階段通知，並等待 Restart Manager 回報結果。
    /// </summary>
    /// <param name="processes">程序識別碼與以 FILETIME 表示的啟動時間。</param>
    /// <returns>Restart Manager 的 Win32 錯誤碼；`0` 表示已全部結束。</returns>
    internal static int RequestShutdown(IReadOnlyList<(int ProcessId, long StartFileTime)> processes) {
        ArgumentNullException.ThrowIfNull(processes);
        if (processes.Count == 0) {
            return 0;
        }

        UniqueProcess[] applications = processes
            .Select(process => new UniqueProcess(process.ProcessId, process.StartFileTime))
            .ToArray();
        int result = RmStartSession(out uint session, 0, new char[SessionKeyLength + 1]);
        if (result == 0) {
            try {
                result = RmRegisterResources(session, 0, null, (uint)applications.Length, applications, 0, null);
                result = result != 0
                    ? result
                    : RmShutdown(session, ShutdownOnlyRegistered, 0);
            } finally {
                _ = RmEndSession(session);
            }
        }

        if (result != 0) {
            // 縮到系統匣的程序沒有可見視窗，Restart Manager 不會通知；改直接送出相同的結束工作階段訊息。
            foreach ((int processId, long startFileTime) in processes) {
                _ = SendEndSession(processId, startFileTime);
            }
        }

        return result;
    }

    /// <summary>
    /// 對指定程序的所有頂層視窗（含隱藏視窗）送出結束工作階段訊息。
    /// </summary>
    /// <remarks>
    /// 訊息與 Restart Manager 關閉應用程式時相同，程序可先保存狀態再結束；沒有視窗的程序不受影響。
    /// 任一視窗拒絕或未在時限內回應結束詢問時，改送出工作階段未結束的通知，不要求程序結束。
    /// </remarks>
    /// <param name="processId">程序識別碼。</param>
    /// <param name="startFileTime">以 FILETIME 表示的程序啟動時間，用來排除已被其他程序重用的 PID。</param>
    /// <returns>所有視窗同意結束並已送出結束通知時為 <see langword="true" />；否則為 <see langword="false" />。</returns>
    internal static bool SendEndSession(int processId, long startFileTime) {
        // 全程持有程序 handle：handle 未關閉前 Windows 不會把此 PID 配給其他程序。
        using Process? process = OpenSameProcess(processId, startFileTime);
        if (process is null) {
            return false;
        }

        List<nint> windows = [];
        _ = EnumWindows((window, parameter) => {
            if (IsOwnedBy(window, processId)) {
                windows.Add(window);
            }

            return true;
        }, 0);

        if (windows.Count == 0) {
            return false;
        }

        bool agreed = true;
        List<nint> queried = [];
        foreach (nint window in windows) {
            // 視窗 handle 可能已關閉並被其他程序重用，每次送出前重新核對擁有者。
            if (!IsOwnedBy(window, processId)) {
                continue;
            }

            queried.Add(window);
            nint sent = SendMessageTimeout(
                window, QueryEndSessionMessage, 0, EndSessionCloseApplication, SendAbortIfHung, MessageTimeoutMilliseconds, out nint answer
            );
            if (sent == 0 || answer == 0) {
                agreed = false;
                break;
            }
        }

        agreed = agreed && queried.Count > 0 && !process.HasExited;
        nint sessionEnding = agreed ? 1 : 0;
        foreach (nint window in queried) {
            if (IsOwnedBy(window, processId)) {
                _ = SendMessageTimeout(
                    window, EndSessionMessage, sessionEnding, EndSessionCloseApplication, SendAbortIfHung, MessageTimeoutMilliseconds, out _
                );
            }
        }

        return agreed;
    }

    private static bool IsOwnedBy(nint window, int processId) {
        return GetWindowThreadProcessId(window, out uint owner) != 0 && owner == (uint)processId;
    }

    private static Process? OpenSameProcess(int processId, long startFileTime) {
        Process? process = null;
        try {
            process = Process.GetProcessById(processId);
            // 取得並保留程序 handle，之後的核對都作用於同一個程序物件。
            _ = process.Handle;
            if (!process.HasExited && process.StartTime.ToFileTime() == startFileTime) {
                return process;
            }
        } catch (Exception failure) when (failure is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException) {
        }

        process?.Dispose();
        return null;
    }

    /// <summary>
    /// 取得目前所有程序的父程序識別碼。
    /// </summary>
    /// <returns>以程序識別碼為鍵的父程序識別碼；無法建立快照時為空集合。</returns>
    internal static Dictionary<int, int> GetParentProcessIds() {
        Dictionary<int, int> parentProcessIds = [];
        using SafeSnapshotHandle snapshot = CreateToolhelp32Snapshot(SnapshotProcessFlag, 0);
        if (snapshot.IsInvalid) {
            return parentProcessIds;
        }

        ProcessEntry entry = new() { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        for (bool hasEntry = Process32FirstW(snapshot, ref entry); hasEntry; hasEntry = Process32NextW(snapshot, ref entry)) {
            parentProcessIds[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }

        return parentProcessIds;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, char[] sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        string[]? fileNames,
        uint applicationCount,
        UniqueProcess[] applications,
        uint serviceCount,
        string[]? serviceNames
    );

    [DllImport("rstrtmgr.dll")]
    private static extern int RmShutdown(uint sessionHandle, uint actionFlags, nint statusCallback);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32FirstW(SafeSnapshotHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32NextW(SafeSnapshotHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nint wordParameter,
        nint longParameter,
        uint flags,
        uint timeout,
        out nint result
    );

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    // 對應 RM_UNIQUE_PROCESS；FILETIME 為兩個 DWORD，以 4 位元組對齊。
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UniqueProcess(int processId, long startFileTime) {
        private readonly int processId = processId;
        private readonly uint startTimeLow = unchecked((uint)startFileTime);
        private readonly uint startTimeHigh = (uint)(startFileTime >> 32);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    private sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid {
        public SafeSnapshotHandle()
            : base(true) {
        }

        protected override bool ReleaseHandle() {
            return CloseHandle(handle);
        }
    }
}
