using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>將視窗帶到前景。</summary>
public static class WindowForeground {
    /// <summary>還原視窗並嘗試取得前景焦點。</summary>
    public static void Bring(Window window) {
        if (window.WindowState == WindowState.Minimized) {
            window.WindowState = WindowState.Normal;
        }
        nint handle = new WindowInteropHelper(window).Handle;
        if (!SetForegroundWindow(handle)) {
            bool topmost = window.Topmost;
            window.Topmost = true;
            window.Topmost = topmost;
            if (!window.Activate()) {
                _ = FlashWindow(handle, true);
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindow(nint handle, [MarshalAs(UnmanagedType.Bool)] bool invert);
}
