using System.Globalization;
using System.IO;
using System.Security;
using System.Text;

namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>
/// 將 WebView 重建相關事件寫入本機記錄檔，供事後追查畫面空白問題。
/// </summary>
/// <remarks>
/// 只記錄事件代碼、例外型別與 HResult，不寫入訊息內文、token、帳號或個資。寫入失敗一律吞下，不影響畫面。
/// </remarks>
public sealed class WebViewRecoveryLog {
    /// <summary>單一記錄檔的大小上限，超過時改名為 <c>.1</c> 並重新開始。</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private readonly Func<DateTimeOffset> now;
    private readonly StartupRenderProbe? probe;
    private readonly Lock sync = new();

    /// <summary>
    /// 建立記錄器。
    /// </summary>
    /// <param name="dataRoot">資料根目錄，記錄檔位於其下的 <c>logs</c> 資料夾。</param>
    /// <param name="now">取得目前時間，未提供時使用系統時鐘。</param>
    /// <param name="probe">診斷探針，提供時同步寫入探針事件。</param>
    public WebViewRecoveryLog(string dataRoot, Func<DateTimeOffset>? now = null, StartupRenderProbe? probe = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        FilePath = Path.Combine(Path.GetFullPath(dataRoot), "logs", "webview-recovery.log");
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.probe = probe;
    }

    /// <summary>取得記錄檔的完整路徑。</summary>
    public string FilePath { get; }

    /// <summary>
    /// 寫入一筆事件。
    /// </summary>
    /// <param name="eventText">事件代碼，例如 <c>rebuild.started:session-unlock</c>。</param>
    public void Write(string eventText) {
        string text = eventText.ReplaceLineEndings(" ");
        probe?.RecordEvent("webview.recovery", code: text);
        string line = now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
            + " " + text + Environment.NewLine;
        try {
            lock (sync) {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                FileInfo info = new(FilePath);
                if (info.Exists && info.Length > MaxFileBytes) {
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                }

                File.AppendAllText(FilePath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        } catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException
                or ArgumentException or NotSupportedException
        ) {
        }
    }
}
