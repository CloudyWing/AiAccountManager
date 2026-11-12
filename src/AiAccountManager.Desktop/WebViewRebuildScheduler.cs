namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>
/// 彙整重建 WebView 的觸發，以最後一次觸發起算延遲，並在獨占作業進行中時延後。
/// </summary>
/// <remarks>
/// 本類別不含執行緒同步，必須只在 UI 執行緒使用。時鐘與計時由呼叫端注入，單元測試不需真實等待。
/// </remarks>
public sealed class WebViewRebuildScheduler {
    /// <summary>最後一次觸發後等待多久才重建。</summary>
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(3);

    /// <summary>有獨占作業進行中時，下一次檢查的間隔。</summary>
    public static readonly TimeSpan RecheckDelay = TimeSpan.FromSeconds(2);

    private readonly Func<DateTimeOffset> now;
    private readonly Action<TimeSpan> scheduleTick;
    private readonly Func<bool> hasActiveOperations;
    private readonly Action<string> rebuild;
    private readonly Action<string> log;
    private DateTimeOffset dueAt;
    private string lastReason = "";
    private bool pending;
    private bool deferredLogged;

    /// <summary>
    /// 建立重建排程。
    /// </summary>
    /// <param name="now">取得目前時間。</param>
    /// <param name="scheduleTick">要求在指定延遲後呼叫 <see cref="OnTick"/>，並取代先前尚未到期的要求。</param>
    /// <param name="hasActiveOperations">回報是否有獨占作業進行中。</param>
    /// <param name="rebuild">執行重建，參數為最後一次觸發的原因。</param>
    /// <param name="log">寫入記錄。</param>
    public WebViewRebuildScheduler(
        Func<DateTimeOffset> now,
        Action<TimeSpan> scheduleTick,
        Func<bool> hasActiveOperations,
        Action<string> rebuild,
        Action<string> log
    ) {
        this.now = now ?? throw new ArgumentNullException(nameof(now));
        this.scheduleTick = scheduleTick ?? throw new ArgumentNullException(nameof(scheduleTick));
        this.hasActiveOperations = hasActiveOperations ?? throw new ArgumentNullException(nameof(hasActiveOperations));
        this.rebuild = rebuild ?? throw new ArgumentNullException(nameof(rebuild));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>取得是否有尚未執行的重建要求。</summary>
    public bool IsPending => pending;

    /// <summary>
    /// 登記一次重建觸發，重複觸發會重新起算延遲。
    /// </summary>
    /// <param name="reason">觸發原因。</param>
    public void Trigger(string reason) {
        lastReason = reason;
        log("trigger:" + reason);
        dueAt = now() + DebounceDelay;
        pending = true;
        deferredLogged = false;
        scheduleTick(DebounceDelay);
    }

    /// <summary>
    /// 計時到期時呼叫，依目前狀態決定重新等待、延後或執行重建。
    /// </summary>
    public void OnTick() {
        if (!pending) {
            return;
        }

        DateTimeOffset current = now();
        if (current < dueAt) {
            scheduleTick(dueAt - current);
            return;
        }

        if (hasActiveOperations()) {
            if (!deferredLogged) {
                deferredLogged = true;
                log("rebuild.deferred");
            }
            dueAt = current + RecheckDelay;
            scheduleTick(RecheckDelay);
            return;
        }

        pending = false;
        deferredLogged = false;
        try {
            rebuild(lastReason);
        } catch (Exception exception) {
            log($"rebuild.failed:{exception.GetType().FullName}:0x{exception.HResult:X8}");
        }
    }

    /// <summary>
    /// 取消尚未執行的重建要求。
    /// </summary>
    public void Cancel() {
        pending = false;
        deferredLogged = false;
    }
}
