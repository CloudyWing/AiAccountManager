using System.Diagnostics;
using System.Reflection;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.UiTests;

/// <summary>
/// 以隔離的資料目錄啟動桌面程式，並提供 FlaUI 操作入口。
/// </summary>
/// <remarks>
/// 程式透過 <c>--data-root</c> 與 <c>--startup-render-probe</c> 診斷參數啟動：
/// 帳號、設定與 WebView2 資料都寫入暫存目錄，不會動到使用者真正的資料；
/// Blazor 第一次渲染完成時會寫出 marker 檔，作為介面已就緒的訊號。
/// </remarks>
public sealed class DesktopAppSession : IDisposable {
    /// <summary>
    /// 主視窗標題，與 <c>AppIdentity.DisplayName</c> 一致。
    /// </summary>
    public const string WindowTitle = "AI 帳號管理工具";

    /// <summary>
    /// 可覆寫 Desktop 執行檔位置的環境變數名稱。
    /// </summary>
    public const string ExecutablePathVariable = "AIACCOUNTMANAGER_DESKTOP_EXE";

    private const string ProcessName = "CloudyWing.AiAccountManager.Desktop";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly string workingRoot;
    private bool disposed;

    private DesktopAppSession(string workingRoot, Application application, UIA3Automation automation, Window mainWindow) {
        this.workingRoot = workingRoot;
        Application = application;
        Automation = automation;
        MainWindow = mainWindow;
    }

    /// <summary>
    /// 受測程式。
    /// </summary>
    public Application Application { get; }

    /// <summary>
    /// UI Automation 實例。
    /// </summary>
    public UIA3Automation Automation { get; }

    /// <summary>
    /// 主視窗。
    /// </summary>
    public Window MainWindow { get; }

    /// <summary>
    /// 本次啟動使用的資料目錄。
    /// </summary>
    public string DataRoot => Path.Combine(workingRoot, "data");

    /// <summary>
    /// Blazor 第一次渲染時寫出的 marker 檔。
    /// </summary>
    public string MarkerPath => Path.Combine(workingRoot, "startup-render.json");

    /// <summary>
    /// 啟動探針記錄的診斷事件檔。
    /// </summary>
    public string DiagnosticsPath => MarkerPath + ".diagnostics.jsonl";

    /// <summary>
    /// 啟動桌面程式並等待 Blazor 介面完成第一次渲染。
    /// </summary>
    /// <returns>已就緒的工作階段。</returns>
    public static DesktopAppSession Launch() {
        string executablePath = ResolveExecutablePath();
        if (!File.Exists(executablePath)) {
            Assert.Ignore($"找不到 Desktop 執行檔：{executablePath}。請先建置方案，或以環境變數 {ExecutablePathVariable} 指定路徑。");
        }

        // 程式採單一執行個體；已有執行中的實例時，新程序只會喚醒舊視窗後結束，測試無法進行。
        if (Process.GetProcessesByName(ProcessName).Length > 0) {
            Assert.Ignore("偵測到 AI 帳號管理工具正在執行。請先結束它再執行 UI 測試。");
        }

        string workingRoot = Path.Combine(Path.GetTempPath(), "AiAccountManager.UiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingRoot);
        string dataRoot = Path.Combine(workingRoot, "data");
        string markerPath = Path.Combine(workingRoot, "startup-render.json");

        ProcessStartInfo startInfo = new(executablePath) {
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--data-root");
        startInfo.ArgumentList.Add(dataRoot);
        startInfo.ArgumentList.Add("--startup-render-probe");
        startInfo.ArgumentList.Add(markerPath);

        Application application = Application.Launch(startInfo);
        UIA3Automation automation = new();
        try {
            bool rendered = Retry.WhileFalse(
                () => File.Exists(markerPath) || application.HasExited,
                StartupTimeout,
                PollInterval
            ).Result;
            if (application.HasExited) {
                Assert.Fail($"Desktop 程式在介面就緒前結束（exit code {application.ExitCode}）。{ReadDiagnostics(markerPath)}");
            }

            if (!rendered || !File.Exists(markerPath)) {
                Assert.Fail($"等待 {StartupTimeout.TotalSeconds:0} 秒後 Blazor 介面仍未完成第一次渲染。{ReadDiagnostics(markerPath)}");
            }

            Window mainWindow = application.GetMainWindow(automation, StartupTimeout)
                ?? throw new InvalidOperationException("找不到 Desktop 主視窗。");
            return new DesktopAppSession(workingRoot, application, automation, mainWindow);
        } catch {
            ForceStop(application);
            automation.Dispose();
            TryDeleteDirectory(workingRoot);
            throw;
        }
    }

    /// <summary>
    /// 等待符合條件的 WebView2 內容元素出現。
    /// </summary>
    /// <param name="name">UI Automation 名稱。</param>
    /// <param name="controlType">控制項類型。</param>
    /// <param name="timeout">等待時間；未指定時為 20 秒。</param>
    /// <returns>找到的元素。</returns>
    public AutomationElement WaitForElement(string name, ControlType controlType, TimeSpan? timeout = null) {
        TimeSpan wait = timeout ?? TimeSpan.FromSeconds(20);
        AutomationElement? element = Retry.WhileNull(
            () => MainWindow.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(controlType))),
            wait,
            PollInterval,
            ignoreException: true
        ).Result;
        return element ?? throw new AssertionException($"等待 {wait.TotalSeconds:0} 秒後仍找不到 {controlType}「{name}」。");
    }

    /// <summary>
    /// 等待 WebView2 文件標題（Blazor <c>PageTitle</c>）變成指定文字。
    /// </summary>
    /// <param name="title">預期的頁面標題。</param>
    public void WaitForPageTitle(string title) {
        string? lastTitle = null;
        bool matched = Retry.WhileFalse(
            () => {
                lastTitle = MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Document))?.Name;
                return string.Equals(lastTitle, title, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(20),
            PollInterval,
            ignoreException: true
        ).Result;
        if (!matched) {
            throw new AssertionException($"頁面標題預期為「{title}」，實際為「{lastTitle ?? "(找不到文件元素)"}」。");
        }
    }

    /// <summary>
    /// 等待 WebView2 文件出現非空白的頁面標題。
    /// </summary>
    /// <returns>目前的頁面標題。</returns>
    public string WaitForAnyPageTitle() {
        string? title = Retry.WhileNull(
            () => {
                string? name = MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Document))?.Name;
                // index.html 的預設標題與視窗同名；Blazor 套用 PageTitle 後才算頁面就緒。
                return string.IsNullOrWhiteSpace(name) || name == WindowTitle ? null : name;
            },
            TimeSpan.FromSeconds(20),
            PollInterval,
            ignoreException: true
        ).Result;
        return title ?? throw new AssertionException("等待 20 秒後 WebView2 仍未顯示頁面標題。");
    }

    /// <summary>
    /// 觸發元素的預設動作；支援 Invoke pattern 時不移動滑鼠。
    /// </summary>
    /// <param name="element">要觸發的元素。</param>
    public static void Activate(AutomationElement element) {
        if (element.Patterns.Invoke.TryGetPattern(out var invoke)) {
            invoke.Invoke();
            return;
        }

        element.Click();
    }

    /// <summary>
    /// 透過關閉主視窗結束程式，並等待程序結束。
    /// </summary>
    /// <returns>程序是否在時限內正常結束。</returns>
    public bool CloseAndWaitForExit() {
        if (Application.HasExited) {
            return true;
        }

        MainWindow.Close();
        return Retry.WhileFalse(() => Application.HasExited, ShutdownTimeout, PollInterval).Result;
    }

    /// <summary>
    /// 讀取啟動探針的診斷事件內容。
    /// </summary>
    /// <returns>診斷事件文字；檔案不存在時為空字串。</returns>
    public string ReadDiagnostics() => File.Exists(DiagnosticsPath) ? File.ReadAllText(DiagnosticsPath) : string.Empty;

    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        try {
            if (!CloseAndWaitForExit()) {
                ForceStop(Application);
            }
        } catch {
            ForceStop(Application);
        } finally {
            Application.Dispose();
            Automation.Dispose();
            TryDeleteDirectory(workingRoot);
        }
    }

    private static string ResolveExecutablePath() {
        string? overridePath = Environment.GetEnvironmentVariable(ExecutablePathVariable);
        if (!string.IsNullOrWhiteSpace(overridePath)) {
            return Path.GetFullPath(overridePath);
        }

        string? builtPath = typeof(DesktopAppSession).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "DesktopExecutablePath")?.Value;
        return builtPath ?? throw new InvalidOperationException("測試組件缺少 DesktopExecutablePath 中繼資料。");
    }

    private static string ReadDiagnostics(string markerPath) {
        string diagnosticsPath = markerPath + ".diagnostics.jsonl";
        return File.Exists(diagnosticsPath)
            ? Environment.NewLine + "啟動診斷：" + Environment.NewLine + File.ReadAllText(diagnosticsPath)
            : string.Empty;
    }

    private static void ForceStop(Application application) {
        try {
            if (!application.HasExited) {
                application.Kill();
            }
        } catch (InvalidOperationException) {
            // 程序已經結束。
        }
    }

    private static void TryDeleteDirectory(string path) {
        // WebView2 子程序可能在主程序結束後短暫持有檔案，稍候重試。
        for (int attempt = 0; attempt < 10 && Directory.Exists(path); attempt++) {
            try {
                Directory.Delete(path, recursive: true);
            } catch (IOException) {
                Thread.Sleep(500);
            } catch (UnauthorizedAccessException) {
                Thread.Sleep(500);
            }
        }
    }
}
