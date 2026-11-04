namespace CloudyWing.AiAccountManager.Windows.Runtime;

/// <summary>單一執行個體協商結果。</summary>
public enum NegotiationOutcome {
    /// <summary>取得主要執行個體使用權。</summary>
    BecamePrimary,
    /// <summary>已通知現有執行個體。</summary>
    Signaled,
    /// <summary>協商逾時。</summary>
    TimedOut
}
