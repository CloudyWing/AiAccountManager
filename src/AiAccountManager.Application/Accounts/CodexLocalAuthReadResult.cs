namespace CloudyWing.AiAccountManager.Application.Accounts;

/// <summary>
/// 封裝本機 Codex 認證檔的讀取狀態與暫存帳號識別資料。
/// </summary>
public sealed class CodexLocalAuthReadResult {
    /// <summary>
    /// 初始化本機認證檔讀取結果。
    /// </summary>
    /// <param name="status">安全讀取狀態。</param>
    /// <param name="identity">成功解析時暫存的帳號識別資料。</param>
    public CodexLocalAuthReadResult(CodexLocalAuthReadStatus status, CodexLocalAuthIdentity? identity) {
        if ((status == CodexLocalAuthReadStatus.Success) != (identity is not null)) {
            throw new ArgumentException("成功狀態必須包含帳號識別資料，其他狀態不得包含識別資料。", nameof(identity));
        }

        Status = status;
        Identity = identity;
    }

    /// <summary>
    /// 取得本機認證檔的讀取狀態。
    /// </summary>
    public CodexLocalAuthReadStatus Status { get; }

    /// <summary>
    /// 取得成功解析時的暫存帳號識別資料。
    /// </summary>
    public CodexLocalAuthIdentity? Identity { get; }

    /// <summary>
    /// 取得不包含認證或帳號識別資料的文字表示。
    /// </summary>
    /// <returns>只包含讀取狀態的文字。</returns>
    public override string ToString() {
        return $"CodexLocalAuthReadResult {{ Status = {Status}, Identity = [redacted] }}";
    }
}
