namespace CloudyWing.AiAccountManager.Windows.Runtime;

/// <summary>啟動要求的傳送結果。</summary>
public enum ActivationOutcome {
    /// <summary>伺服器已接受要求。</summary>
    Accepted,
    /// <summary>未取得有效回覆。</summary>
    NoResponse
}
