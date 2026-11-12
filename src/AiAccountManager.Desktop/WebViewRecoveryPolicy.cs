using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>
/// 判斷 Dispatcher 未處理例外是否屬於 WebView2 繪圖裝置遺失後的已知例外，可吞下並改為重建畫面。
/// </summary>
/// <remarks>
/// WebView2CompositionControl 在 D3D 裝置遺失後不會自行重建畫面，其復原流程可能在 UI 執行緒拋出未處理例外
/// （WebView2Feedback #5792）。判斷只比對例外型別與堆疊 frame 的宣告型別名稱，不讀取任何私有成員。
/// </remarks>
public sealed class WebViewRecoveryPolicy {
    /// <summary>吞下例外的計數視窗。</summary>
    public static readonly TimeSpan SwallowWindow = TimeSpan.FromSeconds(60);

    /// <summary>計數視窗內最多吞下的次數。</summary>
    public const int MaxSwallowsInWindow = 3;

    private const string MarshalTypeName = "System.Runtime.InteropServices.Marshal";
    private const string GraphicsItemTypeName = "Microsoft.Web.WebView2.Wpf.GraphicsItemD3DImage";
    private const string Direct3DHelperTypeName = "Microsoft.Web.WebView2.Wpf.Direct3DHelper";

    private readonly Func<DateTimeOffset> now;
    private readonly Func<Exception, IReadOnlyList<string?>> frameTypeNames;
    private readonly Lock sync = new();
    private readonly Queue<DateTimeOffset> swallowedAt = new();
    private bool hasRendered;

    /// <summary>
    /// 建立判斷策略。
    /// </summary>
    /// <param name="now">取得目前時間。</param>
    /// <param name="frameTypeNames">取得例外堆疊由內往外的 frame 宣告型別名稱，未提供時讀取例外本身的堆疊。</param>
    public WebViewRecoveryPolicy(
        Func<DateTimeOffset> now,
        Func<Exception, IReadOnlyList<string?>>? frameTypeNames = null
    ) {
        this.now = now ?? throw new ArgumentNullException(nameof(now));
        this.frameTypeNames = frameTypeNames ?? GetFrameTypeNames;
    }

    /// <summary>取得畫面是否曾經成功渲染。</summary>
    public bool HasRendered {
        get {
            lock (sync) {
                return hasRendered;
            }
        }
    }

    /// <summary>
    /// 記錄畫面已成功渲染，之後才允許吞下例外。
    /// </summary>
    public void MarkRendered() {
        lock (sync) {
            hasRendered = true;
        }
    }

    /// <summary>
    /// 判斷例外是否可吞下，可吞下時同時計入頻率限制。
    /// </summary>
    /// <param name="exception">Dispatcher 未處理例外。</param>
    /// <param name="reason">判斷結果的簡短代碼，供記錄使用。</param>
    /// <returns>可吞下時為 <see langword="true"/>。</returns>
    public bool ShouldSwallow(Exception exception, out string reason) {
        ArgumentNullException.ThrowIfNull(exception);
        if (!HasRendered) {
            reason = "not-rendered";
            return false;
        }

        Exception? matched = null;
        for (Exception? current = exception; current is not null; current = current.InnerException) {
            if (current is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException) {
                reason = "load-failure";
                return false;
            }

            if (matched is null && current is ArgumentException or COMException) {
                matched = current;
            }
        }

        if (matched is null) {
            reason = "exception-type-mismatch";
            return false;
        }

        if (!IsRenderFrameSequence(frameTypeNames(matched))) {
            reason = "frame-mismatch";
            return false;
        }

        lock (sync) {
            DateTimeOffset current = now();
            while (swallowedAt.Count > 0 && current - swallowedAt.Peek() >= SwallowWindow) {
                swallowedAt.Dequeue();
            }

            if (swallowedAt.Count >= MaxSwallowsInWindow) {
                reason = "rate-limit";
                return false;
            }

            swallowedAt.Enqueue(current);
        }

        reason = "swallowed";
        return true;
    }

    /// <summary>
    /// 判斷由內往外的 frame 宣告型別名稱是否指向 WebView2 的繪圖復原流程。
    /// </summary>
    /// <param name="declaringTypeNames">由最內層開始的 frame 宣告型別完整名稱，無法取得型別的 frame 以 <see langword="null"/> 表示。</param>
    /// <returns>略過 Marshal frame 後，第一個 frame 的最外層宣告型別為指定的 WebView2 型別時為 <see langword="true"/>。</returns>
    public static bool IsRenderFrameSequence(IEnumerable<string?> declaringTypeNames) {
        ArgumentNullException.ThrowIfNull(declaringTypeNames);
        foreach (string? name in declaringTypeNames) {
            if (name is null) {
                return false;
            }

            // 巢狀與編譯器產生的型別以 '+' 連接，取最外層宣告型別比對。
            int nested = name.IndexOf('+', StringComparison.Ordinal);
            string outermost = nested < 0 ? name : name[..nested];
            if (outermost == MarshalTypeName) {
                continue;
            }

            return outermost is GraphicsItemTypeName or Direct3DHelperTypeName;
        }

        return false;
    }

    /// <summary>
    /// 取得例外堆疊由內往外的 frame 最外層宣告型別完整名稱。
    /// </summary>
    /// <param name="exception">要檢查的例外。</param>
    /// <returns>無法取得方法或宣告型別的 frame 以 <see langword="null"/> 表示。</returns>
    public static IReadOnlyList<string?> GetFrameTypeNames(Exception exception) {
        ArgumentNullException.ThrowIfNull(exception);
        StackFrame[] frames = new StackTrace(exception, false).GetFrames();
        List<string?> names = new(frames.Length);
        foreach (StackFrame frame in frames) {
            Type? type = frame.GetMethod()?.DeclaringType;
            while (type?.DeclaringType is Type outer) {
                type = outer;
            }

            names.Add(type?.FullName);
        }

        return names;
    }
}
