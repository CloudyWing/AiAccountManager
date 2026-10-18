using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 定義 Codex Desktop 登入檔 `auth.json` 的讀取、解析與原子取代契約。
/// </summary>
public interface ICodexDesktopAuthStore {
    /// <summary>
    /// 讀取目前的 `auth.json` 內容並解析身分。
    /// </summary>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>讀取狀態；成功時包含檔案內容與身分，呼叫端用畢須清除內容。</returns>
    Task<ReadResult> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 解析一份 `auth.json` 內容的身分，不讀寫檔案。
    /// </summary>
    /// <param name="content">`auth.json` 位元組。</param>
    /// <returns>解析結果。</returns>
    CodexLocalAuthReadResult Parse(byte[] content);

    /// <summary>
    /// 以原子方式將 `auth.json` 取代為指定內容。
    /// </summary>
    /// <param name="content">新的 `auth.json` 位元組。</param>
    /// <param name="cancellationToken">取消寫入作業的權杖。</param>
    Task ReplaceAsync(byte[] content, CancellationToken cancellationToken);

    /// <summary>
    /// 描述 `auth.json` 讀取結果。
    /// </summary>
    /// <param name="Status">讀取與解析狀態。</param>
    /// <param name="Content">成功時的檔案內容；其他狀態為 <see langword="null" />。</param>
    /// <param name="Identity">成功時解析出的身分；其他狀態為 <see langword="null" />。</param>
    public sealed record ReadResult(
        CodexLocalAuthReadStatus Status,
        byte[]? Content,
        CodexLocalAuthIdentity? Identity
    ) {
        /// <summary>
        /// 以遮蔽文字呈現讀取結果，避免除錯字串洩漏認證資料。
        /// </summary>
        /// <returns>不含檔案內容與身分的描述。</returns>
        public override string ToString() {
            return $"Codex Desktop auth read result {Status} (sensitive values redacted).";
        }
    }
}
