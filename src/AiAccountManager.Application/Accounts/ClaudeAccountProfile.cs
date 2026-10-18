namespace CloudyWing.AiAccountManager.Application.Accounts;

/// <summary>
/// 表示一個已驗證或待驗證的 Claude Desktop profile。
/// </summary>
/// <param name="ProfileId">應用程式內部識別碼。</param>
/// <param name="Alias">使用者設定的帳號別名。</param>
/// <param name="MaskedEmail">遮蔽後的 email。</param>
/// <param name="AccountUuid">Anthropic 帳號 UUID。</param>
/// <param name="OrganizationUuid">Anthropic 組織 UUID。</param>
/// <param name="InstallationId">Claude Desktop 安裝識別碼。</param>
/// <param name="DataRootFingerprint">Claude Desktop 資料根指紋。</param>
/// <param name="WindowsUserSid">捕捉 profile 時的 Windows 使用者 SID。</param>
/// <param name="LocalStateEncryptedKeySha256">Local State 加密 key 字串的 SHA-256 摘要。</param>
/// <param name="ProfileRevision">用於切換前一致性檢查的遞增版次。</param>
/// <param name="CreatedAtUtc">profile 建立時間。</param>
/// <param name="LastVerifiedAtUtc">最近一次由 Desktop OAuth profile API 驗證的時間。</param>
/// <param name="Status">profile 驗證狀態。</param>
/// <param name="BindingId">受保護快照中的來源綁定識別碼；舊資料為 null。</param>
/// <param name="ProvenanceSchemaVersion">來源證據版本；0 表示來源未知。</param>
public sealed record ClaudeAccountProfile(
    Guid ProfileId,
    string? Alias,
    string MaskedEmail,
    string AccountUuid,
    string OrganizationUuid,
    string InstallationId,
    string DataRootFingerprint,
    string WindowsUserSid,
    string LocalStateEncryptedKeySha256,
    long ProfileRevision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastVerifiedAtUtc,
    ClaudeAccountProfile.VerificationStatus Status,
    Guid? BindingId = null,
    int ProvenanceSchemaVersion = 0
) {
    /// <summary>
    /// 定義 Claude profile 的驗證狀態。
    /// </summary>
    public enum VerificationStatus {
        /// <summary>profile 最近一次驗證成功。</summary>
        Verified,

        /// <summary>profile 需要使用者重新登入後再捕捉。</summary>
        ReauthenticationRequired,

        /// <summary>目前無法判定 profile 狀態。</summary>
        Unknown
    }

    /// <summary>
    /// 保存供還原 Desktop 使用的 auth、Cookie 與捕捉時存在之 sidecar 快照。
    /// </summary>
    /// <param name="TokenCacheJson">`oauth:tokenCache` 欄位的 JSON 原文。</param>
    /// <param name="TokenCacheV2Json">`oauth:tokenCacheV2` 欄位的 JSON 原文。</param>
    /// <param name="LastKnownAccountUuid">Desktop 設定中的最近帳號 UUID。</param>
    /// <param name="Cookies">Cookie 資料庫位元組。</param>
    /// <param name="CookieJournal">捕捉時存在的 Cookie journal 位元組；新版 sidecar snapshot 中的 null 表示不存在。</param>
    /// <param name="CookieRelativePath">Cookie 資料庫相對於資料根的路徑。</param>
    /// <param name="WindowsUserSid">捕捉快照時的 Windows 使用者 SID。</param>
    /// <param name="InstallationId">捕捉快照時的 Desktop 安裝識別碼。</param>
    /// <param name="DataRootFingerprint">捕捉快照時的資料根指紋。</param>
    /// <param name="LocalStateEncryptedKeySha256">捕捉快照時的 Local State key 字串 SHA-256 摘要。</param>
    /// <param name="CapturedAtUtc">快照捕捉時間。</param>
    /// <param name="CookieWriteAheadLog">捕捉時存在的 Cookies WAL 位元組；null 僅在新版 sidecar snapshot 明確表示不存在。</param>
    /// <param name="CookieSharedMemory">捕捉時存在的 Cookies SHM 位元組；null 僅在新版 sidecar snapshot 明確表示不存在。</param>
    /// <param name="CookieSidecarSnapshotVersion">Cookie sidecar 集合快照版本；0 表示舊資料沒有 sidecar 存在性證據，1 表示受支援 sidecar 的 bytes 或明確不存在狀態已捕捉。</param>
    /// <param name="ProvenanceSchemaVersion">快照來源證據版本；0 表示來源未知。</param>
    /// <param name="RootBinding">與快照一同受保護的來源綁定；舊資料為 null。</param>
    public sealed record DesktopSnapshot(
        string? TokenCacheJson,
        string? TokenCacheV2Json,
        string? LastKnownAccountUuid,
        byte[] Cookies,
        byte[]? CookieJournal,
        string CookieRelativePath,
        string WindowsUserSid,
        string InstallationId,
        string DataRootFingerprint,
        string LocalStateEncryptedKeySha256,
        DateTimeOffset CapturedAtUtc,
        byte[]? CookieWriteAheadLog = null,
        byte[]? CookieSharedMemory = null,
        int CookieSidecarSnapshotVersion = 0,
        int ProvenanceSchemaVersion = 0,
        ClaudeDesktopRootBinding? RootBinding = null
    ) {
        /// <summary>
        /// 以遮蔽文字呈現快照，避免除錯字串洩漏認證資料。
        /// </summary>
        /// <returns>不包含 auth 或 Cookie 內容的描述。</returns>
        public override string ToString() {
            return $"Claude Desktop snapshot for installation {InstallationId} (sensitive values redacted).";
        }
    }
}
