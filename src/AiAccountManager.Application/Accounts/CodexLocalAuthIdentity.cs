namespace CloudyWing.AiAccountManager.Application.Accounts;

/// <summary>
/// 暫存本機 Codex 認證檔中的帳號識別資料。
/// </summary>
public sealed class CodexLocalAuthIdentity {
    private string? accountId;
    private string? email;

    /// <summary>
    /// 初始化本機帳號識別資料。
    /// </summary>
    /// <param name="accountId">本機認證檔中的帳號識別碼。</param>
    /// <param name="email">本機認證檔中的電子郵件地址。</param>
    public CodexLocalAuthIdentity(string? accountId, string? email) {
        this.accountId = Normalize(accountId);
        this.email = Normalize(email);
    }

    /// <summary>
    /// 取得本機帳號識別碼。
    /// </summary>
    public string? AccountId => accountId;

    /// <summary>
    /// 取得本機帳號電子郵件地址。
    /// </summary>
    public string? Email => email;

    /// <summary>
    /// 清除此暫存物件保留的識別字串參考。
    /// </summary>
    public void Clear() {
        accountId = null;
        email = null;
    }

    /// <summary>
    /// 取得不包含帳號識別資料的文字表示。
    /// </summary>
    /// <returns>遮蔽本機帳號識別資料的固定文字。</returns>
    public override string ToString() {
        return "CodexLocalAuthIdentity { AccountId = [redacted], Email = [redacted] }";
    }

    private static string? Normalize(string? value) {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
