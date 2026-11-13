namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>
/// 重建 WebView 後，把頁面導回重建前的位置，派送與整體都有時間上限。
/// </summary>
/// <remarks>
/// 派送會排在新 renderer 前置工作之後，可能一直不完成；沒有上限時重建中旗標無法解除，
/// 之後的重建要求會全部停在 pending。
/// </remarks>
public static class WebViewRouteRestorer {
    /// <summary>
    /// 導回頁面的時間設定。
    /// </summary>
    /// <param name="Deadline">整體期限。</param>
    /// <param name="DispatchTimeout">單次派送的上限。</param>
    /// <param name="RetryDelay">派送未成功時，下一次嘗試前的等待時間上限。</param>
    /// <param name="MaxAttempts">最多嘗試次數。</param>
    public sealed record Timing(TimeSpan Deadline, TimeSpan DispatchTimeout, TimeSpan RetryDelay, int MaxAttempts);

    /// <summary>
    /// 反覆派送導覽，直到導覽完成、目標失效、期限已到或嘗試次數用完。
    /// </summary>
    /// <param name="canRestore">回報目標 WebView 是否仍可導覽。</param>
    /// <param name="tryDispatch">
    /// 把委派派送到 renderer 執行，回傳是否已派送。委派回傳 true 時，呼叫端必須執行導覽。
    /// </param>
    /// <param name="timing">時間設定。</param>
    /// <param name="log">寫入記錄。</param>
    public static async Task RestoreAsync(
        Func<bool> canRestore,
        Func<Func<bool>, Task<bool>> tryDispatch,
        Timing timing,
        Action<string> log
    ) {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timing.Deadline;
        bool navigated = false;

        // 逾時後委派仍可能稍後執行，導覽前再次確認目標仍有效且尚未導覽過。
        bool ShouldNavigate() {
            if (navigated || !canRestore()) {
                return false;
            }

            navigated = true;
            return true;
        }

        try {
            for (int attempt = 0; attempt < timing.MaxAttempts; attempt++) {
                if (!canRestore()) {
                    log("rebuild.route-restore-skipped");
                    return;
                }

                TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) {
                    log("rebuild.route-restore-failed:deadline-exceeded");
                    return;
                }

                TimeSpan timeout = remaining < timing.DispatchTimeout ? remaining : timing.DispatchTimeout;
                try {
                    bool dispatched = await tryDispatch(ShouldNavigate).WaitAsync(timeout);
                    if (dispatched) {
                        log(navigated ? "rebuild.route-restored" : "rebuild.route-restore-skipped");
                        return;
                    }
                } catch (TimeoutException) {
                    // 逾時是否已導覽由下方統一判定。
                }

                if (navigated) {
                    log("rebuild.route-restored");
                    return;
                }

                if (GetRetryDelay(deadline - DateTimeOffset.UtcNow, timing.RetryDelay) is not TimeSpan retryDelay) {
                    log("rebuild.route-restore-failed:deadline-exceeded");
                    return;
                }

                await Task.Delay(retryDelay);
            }

            log("rebuild.route-restore-failed:attempts-exhausted:" + timing.MaxAttempts);
        } catch (Exception exception) {
            log($"rebuild.route-restore-failed:{exception.GetType().FullName}:0x{exception.HResult:X8}");
        }
    }

    /// <summary>
    /// 依距離整體期限的剩餘時間，決定下一次重試前的等待時間。
    /// </summary>
    /// <param name="remaining">距離整體期限的剩餘時間。</param>
    /// <param name="retryDelay">等待時間上限。</param>
    /// <returns>等待時間，不超過剩餘時間；期限已到時為 null。</returns>
    public static TimeSpan? GetRetryDelay(TimeSpan remaining, TimeSpan retryDelay) {
        if (remaining <= TimeSpan.Zero) {
            return null;
        }

        return remaining < retryDelay ? remaining : retryDelay;
    }
}
