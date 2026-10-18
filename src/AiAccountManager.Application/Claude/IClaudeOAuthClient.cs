using System.Text.Json;

namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 定義 Claude OAuth profile 與 usage 固定 HTTPS GET 的 typed client 契約。
/// </summary>
public interface IClaudeOAuthClient {
    /// <summary>
    /// 使用 Desktop token 驗證 Claude account 與 organization 身分。
    /// </summary>
    /// <param name="bearerToken">僅供此次呼叫使用的 Desktop Bearer token。</param>
    /// <param name="cancellationToken">取消 API 呼叫的權杖。</param>
    /// <returns>安全解析後的 profile 結果。</returns>
    Task<ProfileResult> GetProfileAsync(string bearerToken, CancellationToken cancellationToken);

    /// <summary>
    /// 使用已驗證的 Desktop token 查詢額度 usage。
    /// </summary>
    /// <param name="bearerToken">僅供此次呼叫使用的 Desktop Bearer token。</param>
    /// <param name="cancellationToken">取消 API 呼叫的權杖。</param>
    /// <returns>usage JSON payload 或安全的 HTTP 狀態。</returns>
    Task<UsageResult> GetUsageAsync(string bearerToken, CancellationToken cancellationToken);

    /// <summary>
    /// 定義固定 OAuth endpoint 的回應狀態。
    /// </summary>
    public enum Outcome {
        /// <summary>HTTP 200 且回應格式有效。</summary>
        Success,

        /// <summary>HTTP 401，Desktop token 需要更新。</summary>
        Unauthorized,

        /// <summary>HTTP 403，帳號無權存取端點。</summary>
        Forbidden,

        /// <summary>HTTP 429，端點要求稍後重試。</summary>
        RateLimited,

        /// <summary>其他 HTTP 或回應格式錯誤。</summary>
        Error
    }

    /// <summary>
    /// 提供 OAuth profile endpoint 的安全解析結果。
    /// </summary>
    /// <param name="Outcome">HTTP 與格式驗證結果。</param>
    /// <param name="AccountUuid">回應中的 account UUID。</param>
    /// <param name="OrganizationUuid">回應中的 organization UUID。</param>
    /// <param name="Email">僅供呼叫端遮蔽後保存的 email。</param>
    /// <param name="RetryAfter">伺服器提供的重試間隔。</param>
    /// <param name="FailureCode">不含回應本文的穩定失敗代碼。</param>
    public sealed record ProfileResult(
        Outcome Outcome,
        string? AccountUuid,
        string? OrganizationUuid,
        string? Email,
        TimeSpan? RetryAfter,
        string? FailureCode
    );

    /// <summary>
    /// 提供 OAuth usage endpoint 的安全解析結果。
    /// </summary>
    /// <param name="Outcome">HTTP 與格式驗證結果。</param>
    /// <param name="Payload">成功時複製至獨立 JsonDocument 的 response JSON。</param>
    /// <param name="RetryAfter">伺服器提供的重試間隔。</param>
    /// <param name="FailureCode">不含回應本文的穩定失敗代碼。</param>
    public sealed record UsageResult(
        Outcome Outcome,
        JsonElement? Payload,
        TimeSpan? RetryAfter,
        string? FailureCode
    );
}
