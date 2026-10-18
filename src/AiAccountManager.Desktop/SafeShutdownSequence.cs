namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>執行安全結束的停止及等待步驟。</summary>
public static class SafeShutdownSequence {
    /// <summary>停止接受作業、停止 Host 並等待作業完成。</summary>
    /// <param name="stopAccepting">停止接受作業的動作。</param>
    /// <param name="stopHost">停止 Host 的工作。</param>
    /// <param name="drain">等待作業完成的工作。</param>
    /// <param name="success">成功後的動作。</param>
    /// <param name="failure">失敗後的動作。</param>
    /// <returns>結束作業。</returns>
    public static async Task RunAsync(Action stopAccepting, Func<Task> stopHost, Func<Task> drain, Action success, Action<Exception> failure) {
        try {
            stopAccepting();
            await stopHost();
            await drain();
            success();
        } catch (Exception exception) {
            failure(exception);
        }
    }

    /// <summary>依失敗類型恢復視窗並顯示對應訊息。</summary>
    /// <param name="exception">結束失敗的例外。</param>
    /// <param name="show">顯示視窗的動作。</param>
    /// <param name="warning">顯示等待逾時訊息的動作。</param>
    /// <param name="error">處理其他錯誤的動作。</param>
    public static void HandleFailure(Exception exception, Action show, Action warning, Action<Exception> error) {
        if (exception is OperationCanceledException) {
            show();
            warning();
        } else {
            error(exception);
        }
    }
}
