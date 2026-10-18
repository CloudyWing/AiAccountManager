namespace CloudyWing.AiAccountManager.Application.Accounts;

/// <summary>
/// 描述本機 Codex 認證檔的安全讀取結果。
/// </summary>
public enum CodexLocalAuthReadStatus {
    /// <summary>
    /// 已成功解析可供比對的帳號識別資料。
    /// </summary>
    Success,

    /// <summary>
    /// 找不到使用者 profile 下的本機認證檔。
    /// </summary>
    FileMissing,

    /// <summary>
    /// 認證檔不是有效 JSON。
    /// </summary>
    InvalidFormat,

    /// <summary>
    /// 認證模式或資料結構不受支援。
    /// </summary>
    UnsupportedAuth,

    /// <summary>
    /// 認證資料未提供可供比對的帳號識別資料。
    /// </summary>
    IdentityUnavailable,

    /// <summary>
    /// 認證資料含有互相矛盾的帳號識別值。
    /// </summary>
    ConflictingClaims,

    /// <summary>
    /// 認證檔超過允許的大小。
    /// </summary>
    FileTooLarge,

    /// <summary>
    /// 認證檔或其目錄指向不安全的路徑。
    /// </summary>
    UnsafePath,

    /// <summary>
    /// 認證檔持續受到 Windows 共用或鎖定限制。
    /// </summary>
    SharingViolation,

    /// <summary>
    /// 作業系統拒絕讀取認證檔。
    /// </summary>
    AccessDenied,

    /// <summary>
    /// 認證檔在讀取期間持續變動。
    /// </summary>
    ChangedDuringRead,

    /// <summary>
    /// 讀取或解析認證檔時發生未分類錯誤。
    /// </summary>
    Unavailable
}
