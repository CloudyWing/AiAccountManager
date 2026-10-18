using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 定義唯讀取得目前使用者本機 Codex 帳號識別資料的契約。
/// </summary>
public interface ICodexLocalAuthIdentityReader {
    /// <summary>
    /// 從固定的使用者 profile 認證檔讀取暫存帳號識別資料。
    /// </summary>
    /// <param name="cancellationToken">取消讀取的權杖。</param>
    /// <returns>不含 token 或原始錯誤訊息的安全讀取結果。</returns>
    Task<CodexLocalAuthReadResult> ReadCurrentAsync(CancellationToken cancellationToken);
}
