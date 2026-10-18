namespace CloudyWing.AiAccountManager.Application.Accounts;

/// <summary>
/// 表示保存的 Codex 帳號資料。
/// </summary>
/// <param name="AccountId">本機帳號識別碼。</param>
/// <param name="Alias">帳號別名。</param>
/// <param name="MaskedIdentity">遮罩後的身分資訊。</param>
/// <param name="IdentityFingerprint">身分指紋。</param>
/// <param name="ProfileRevision">帳號資料版本。</param>
/// <param name="CreatedAt">建立時間。</param>
/// <param name="Status">帳號狀態。</param>
/// <param name="ExternalAccountId">外部帳號識別碼。</param>
/// <param name="FallbackIdentityFingerprint">備用身分指紋。</param>
public sealed record CodexAccountProfile(
    Guid AccountId,
    string? Alias,
    string MaskedIdentity,
    string IdentityFingerprint,
    long ProfileRevision,
    DateTimeOffset CreatedAt,
    string Status,
    string? ExternalAccountId = null,
    string? FallbackIdentityFingerprint = null
);

/// <summary>
/// 表示從 Codex app-server 讀取的帳號資訊。
/// </summary>
/// <param name="AccountType">帳號類型。</param>
/// <param name="AccountId">帳號識別碼。</param>
/// <param name="Email">電子郵件地址。</param>
/// <param name="PlanType">方案類型。</param>
/// <param name="RequiresOpenaiAuth">若需要 OpenAI 驗證則為 <see langword="true" />；否則為 <see langword="false" />。</param>
public sealed record CodexAccountRead(
    string AccountType,
    string? AccountId,
    string? Email,
    string? PlanType,
    bool RequiresOpenaiAuth
);

/// <summary>
/// 表示已啟動的登入作業。
/// </summary>
/// <param name="LoginId">登入作業識別碼。</param>
/// <param name="AuthUrl">驗證網址。</param>
public sealed record CodexLoginStart(string LoginId, Uri AuthUrl);

/// <summary>
/// 表示登入作業的完成結果。
/// </summary>
/// <param name="LoginId">登入作業識別碼。</param>
/// <param name="Succeeded">若登入成功則為 <see langword="true" />；否則為 <see langword="false" />。</param>
public sealed record CodexLoginCompletion(string LoginId, bool Succeeded);

/// <summary>
/// 表示 Codex app-server 的通知。
/// </summary>
/// <param name="Method">通知方法名稱。</param>
/// <param name="Parameters">通知參數。</param>
public sealed record CodexAppServerNotification(string Method, System.Text.Json.JsonElement Parameters);

/// <summary>
/// 表示 Codex 帳號資料交易的狀態。
/// </summary>
/// <param name="TransactionId">交易識別碼。</param>
/// <param name="AccountId">帳號識別碼。</param>
/// <param name="Operation">作業名稱。</param>
/// <param name="Stage">交易階段。</param>
/// <param name="ExpectedCatalogSha256">預期的目錄 SHA-256 雜湊值。</param>
/// <param name="UpdatedAtUtc">更新時間，使用 UTC。</param>
/// <param name="CleanupResult">清理結果。</param>
public sealed record CodexProfileTransaction(
    Guid TransactionId,
    Guid AccountId,
    string Operation,
    string Stage,
    string ExpectedCatalogSha256,
    DateTimeOffset UpdatedAtUtc,
    string? CleanupResult
);

/// <summary>
/// 表示 Codex app-server 的 RPC 要求失敗。
/// </summary>
public sealed class CodexAppServerRpcException : Exception {
    /// <summary>
    /// 初始化 RPC 錯誤資訊。
    /// </summary>
    /// <param name="errorCode">錯誤代碼。</param>
    /// <param name="unauthorized">若未經授權則為 <see langword="true" />；否則為 <see langword="false" />。</param>
    /// <param name="retryAfter">建議等待的重試時間。</param>
    public CodexAppServerRpcException(int errorCode, bool unauthorized, TimeSpan? retryAfter = null)
        : base("Codex app-server request failed.") {
        ErrorCode = errorCode;
        IsUnauthorized = unauthorized;
        IsRateLimited = errorCode == 429;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// 取得錯誤代碼。
    /// </summary>
    public int ErrorCode { get; }

    /// <summary>
    /// 取得一個值，用以指出是否未經授權。
    /// </summary>
    public bool IsUnauthorized { get; }

    /// <summary>
    /// 取得一個值，用以指出錯誤代碼是否為 429。
    /// </summary>
    public bool IsRateLimited { get; }

    /// <summary>
    /// 取得建議等待的重試時間。
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}
