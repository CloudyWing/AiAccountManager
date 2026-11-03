using System.Runtime.InteropServices;

namespace CloudyWing.AiAccountManager.Windows.Runtime;

/// <summary>允許指定程序設定前景視窗。</summary>
public static class ForegroundPermission {
    /// <summary>授權指定程序設定前景視窗。</summary>
    public static void Allow(int processId) => _ = AllowSetForegroundWindow(processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
