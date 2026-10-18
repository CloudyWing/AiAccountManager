using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 與單一 Codex 帳號的 app-server 程序通訊，處理登入、帳號讀取與額度查詢。
/// </summary>
public interface ICodexAppServerSession : IAsyncDisposable {
    /// <summary>
    /// 取得此工作階段所屬的 Codex 帳號識別碼。
    /// </summary>
    Guid AccountId { get; }

    /// <summary>
    /// 取得此工作階段使用的帳號 profile 版次。
    /// </summary>
    long ProfileRevision { get; }

    /// <summary>
    /// 讀取 app-server 送出的通知。
    /// </summary>
    /// <param name="cancellationToken">停止讀取的權杖。</param>
    /// <returns>依接收順序排列的通知。</returns>
    IAsyncEnumerable<CodexAppServerNotification> ReadNotificationsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 完成 app-server 的初始化交握，其他要求都必須在此之後送出。
    /// </summary>
    /// <param name="cancellationToken">取消初始化的權杖。</param>
    /// <returns>初始化完成的工作。</returns>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 開始 ChatGPT 帳號登入流程。
    /// </summary>
    /// <param name="cancellationToken">取消要求的權杖。</param>
    /// <returns>登入識別碼與使用者需開啟的登入網址。</returns>
    Task<CodexLoginStart> StartChatGptLoginAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 等待指定的登入流程完成。
    /// </summary>
    /// <param name="loginId">登入識別碼。</param>
    /// <param name="timeout">最長等待時間。</param>
    /// <param name="cancellationToken">取消等待的權杖。</param>
    /// <returns>登入結果。</returns>
    Task<CodexLoginCompletion> WaitForLoginCompletionAsync(
        string loginId,
        TimeSpan timeout,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 取消尚未完成的 ChatGPT 登入流程。
    /// </summary>
    /// <param name="loginId">登入識別碼。</param>
    /// <param name="cancellationToken">取消要求的權杖。</param>
    /// <returns>登入流程已取消時為 <see langword="true" />；否則為 <see langword="false" />。</returns>
    Task<bool> CancelChatGptLoginAsync(string loginId, CancellationToken cancellationToken);

    /// <summary>
    /// 讀取目前登入的帳號資訊。
    /// </summary>
    /// <param name="refreshToken">若要先更新存取權杖再讀取則為 <see langword="true" />；否則為 <see langword="false" />。</param>
    /// <param name="cancellationToken">取消要求的權杖。</param>
    /// <returns>帳號類型、識別碼與方案等資訊。</returns>
    Task<CodexAccountRead> ReadAccountAsync(bool refreshToken, CancellationToken cancellationToken);

    /// <summary>
    /// 讀取目前帳號的額度限制。
    /// </summary>
    /// <param name="cancellationToken">取消要求的權杖。</param>
    /// <returns>app-server 回傳的原始額度資料，由 <see cref="QuotaResponseParser" /> 解析。</returns>
    Task<JsonElement> ReadRateLimitsAsync(CancellationToken cancellationToken);

    /// <summary>使用重置券重置額度。</summary>
    /// <param name="idempotencyKey">本次操作的冪等識別碼。</param>
    /// <param name="creditId">指定券識別碼；null 由伺服器挑選。</param>
    /// <param name="cancellationToken">取消要求的權杖。</param>
    /// <returns>伺服器回傳的操作結果。</returns>
    Task<string> ConsumeResetCreditAsync(string idempotencyKey, string? creditId, CancellationToken cancellationToken);

    /// <summary>
    /// 登出目前的帳號。
    /// </summary>
    /// <param name="cancellationToken">取消要求的權杖。</param>
    /// <returns>登出完成的工作。</returns>
    Task LogoutAsync(CancellationToken cancellationToken);
}
