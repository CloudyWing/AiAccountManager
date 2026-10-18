using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Windows.Claude;

internal sealed class WindowsClaudeRestartManagerProbe {
    private const int MaximumProcesses = 4096;
    private const int MaximumListAttempts = 4;
    private const int ErrorMoreData = 234;
    private const int SystemProcessId = 4;
    private readonly IRestartManagerNative native;
    private readonly int currentProcessId;

    internal WindowsClaudeRestartManagerProbe()
        : this(new RestartManagerNative()) {
    }

    internal WindowsClaudeRestartManagerProbe(IRestartManagerNative native)
        : this(native, Environment.ProcessId) {
    }

    internal WindowsClaudeRestartManagerProbe(IRestartManagerNative native, int currentProcessId) {
        ArgumentNullException.ThrowIfNull(native);
        this.native = native;
        this.currentProcessId = currentProcessId;
    }

    internal Result Observe(string resourcePath, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        if (cancellationToken.IsCancellationRequested) {
            return new Result(false, []);
        }

        uint session = 0;
        bool started = false;
        bool succeeded = false;
        IReadOnlyList<ClaudeDesktopRootBinding.ObservedProcess> processes = [];
        try {
            if (native.StartSession(out session) != 0) {
                return new Result(false, []);
            }

            started = true;
            if (cancellationToken.IsCancellationRequested || native.RegisterResource(session, resourcePath) != 0) {
                return new Result(false, []);
            }

            int capacity = 0;
            for (int attempt = 0; attempt < MaximumListAttempts; attempt++) {
                if (cancellationToken.IsCancellationRequested) {
                    break;
                }

                ListResult list = native.GetList(session, capacity);
                if (list.Status == 0) {
                    if (list.RequiredCount < 0 || list.RequiredCount > capacity
                        || list.Processes.Count != list.RequiredCount
                        || list.Processes.Any(process => process.ProcessId <= 0)) {
                        break;
                    }

                    // 辨識資料根目錄時本程序會開啟 identity lease，因此 Restart Manager 也會列出本程序。
                    // 系統掃描或檔案篩選驅動開啟檔案時會列出 System 程序，它不可能是 Claude Desktop。
                    processes = list.Processes
                        .Where(process => process.ProcessId != currentProcessId && process.ProcessId != SystemProcessId)
                        .ToList();
                    succeeded = true;
                    break;
                }

                if (list.Status != ErrorMoreData || list.RequiredCount <= capacity
                    || list.RequiredCount > MaximumProcesses) {
                    break;
                }

                capacity = list.RequiredCount;
            }
        } catch (Exception failure) when (failure is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or OverflowException
            or System.ComponentModel.Win32Exception) {
            succeeded = false;
        } finally {
            if (started) {
                try {
                    if (native.EndSession(session) != 0) {
                        succeeded = false;
                    }
                } catch (Exception failure) when (failure is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException
                    or System.ComponentModel.Win32Exception) {
                    succeeded = false;
                }
            }
        }

        bool isComplete = succeeded && !cancellationToken.IsCancellationRequested;
        return new Result(isComplete, isComplete ? processes : []);
    }

    internal sealed record Result(bool IsComplete, IReadOnlyList<ClaudeDesktopRootBinding.ObservedProcess> Processes);

    internal sealed record ListResult(
        int Status,
        int RequiredCount,
        IReadOnlyList<ClaudeDesktopRootBinding.ObservedProcess> Processes
    );

    internal interface IRestartManagerNative {
        int StartSession(out uint session);

        int RegisterResource(uint session, string resourcePath);

        ListResult GetList(uint session, int capacity);

        int EndSession(uint session);
    }

    private sealed class RestartManagerNative : IRestartManagerNative {
        public int StartSession(out uint session) {
            StringBuilder key = new(33);
            return checked((int)RmStartSession(out session, 0, key));
        }

        public int RegisterResource(uint session, string resourcePath) {
            return checked((int)RmRegisterResources(session, 1, [resourcePath], 0, null, 0, null));
        }

        public ListResult GetList(uint session, int capacity) {
            uint count = checked((uint)capacity);
            uint needed = 0;
            uint rebootReasons = 0;
            ProcessInfo[]? information = capacity == 0 ? null : new ProcessInfo[capacity];
            uint status = RmGetList(session, out needed, ref count, information, ref rebootReasons);
            if (status != 0) {
                return new ListResult(checked((int)status), checked((int)needed), []);
            }

            if (count > capacity || (count > 0 && information is null)) {
                return new ListResult(-1, checked((int)count), []);
            }

            List<ClaudeDesktopRootBinding.ObservedProcess> processes = [];
            for (int index = 0; index < count; index++) {
                UniqueProcess process = information![index].Process;
                long fileTime = ((long)(uint)process.StartTime.dwHighDateTime << 32)
                    | (uint)process.StartTime.dwLowDateTime;
                processes.Add(new ClaudeDesktopRootBinding.ObservedProcess(
                    checked((int)process.ProcessId),
                    new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime))
                ));
            }

            return new ListResult(0, checked((int)count), processes);
        }

        public int EndSession(uint session) {
            return checked((int)RmEndSession(session));
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern uint RmStartSession(out uint session, uint flags, StringBuilder sessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern uint RmRegisterResources(
            uint session,
            uint fileCount,
            string[] fileNames,
            uint applicationCount,
            UniqueProcess[]? applications,
            uint serviceCount,
            string[]? serviceNames
        );

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern uint RmGetList(
            uint session,
            out uint processInfoNeeded,
            ref uint processInfoCount,
            [In, Out] ProcessInfo[]? affectedApplications,
            ref uint rebootReasons
        );

        [DllImport("rstrtmgr.dll")]
        private static extern uint RmEndSession(uint session);

        [StructLayout(LayoutKind.Sequential)]
        private struct UniqueProcess {
            public uint ProcessId;
            public FILETIME StartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessInfo {
            public UniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string ApplicationName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string ServiceShortName;
            public uint ApplicationType;
            public uint ApplicationStatus;
            public uint TerminalServicesSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool Restartable;
        }
    }
}
