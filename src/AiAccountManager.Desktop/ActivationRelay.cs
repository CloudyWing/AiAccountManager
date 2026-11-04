namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>在視窗建立前暫存喚醒要求。</summary>
public sealed class ActivationRelay {
    private readonly object gate = new();
    private Action? handler;
    private bool pending;

    /// <summary>提交喚醒要求。</summary>
    public void Request() {
        Action? current;
        lock (gate) {
            current = handler;
            if (current is null) {
                pending = true;
            }
        }
        current?.Invoke();
    }

    /// <summary>附加處理程序並送出暫存的要求。</summary>
    public void Attach(Action handler) {
        ArgumentNullException.ThrowIfNull(handler);
        bool deliver;
        lock (gate) {
            this.handler = handler;
            deliver = pending;
            pending = false;
        }
        if (deliver) {
            handler();
        }
    }

    /// <summary>移除處理程序及暫存要求。</summary>
    public void Detach() {
        lock (gate) {
            handler = null;
            pending = false;
        }
    }
}
