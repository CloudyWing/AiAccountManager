namespace CloudyWing.AiAccountManager.Application.Security;

/// <summary>
/// 產生帳號身分指紋與遮罩後的電子郵件，讓畫面與儲存檔不保存可直接辨識的帳號資料。
/// </summary>
public interface IIdentityFingerprinter {
    /// <summary>
    /// 建立帳號身分的不可逆指紋。
    /// </summary>
    /// <param name="accountId">帳號識別碼；沒有時為 <see langword="null" />。</param>
    /// <param name="email">帳號電子郵件；沒有時為 <see langword="null" />。</param>
    /// <param name="authMode">登入方式，用來區分相同帳號的不同認證來源。</param>
    /// <returns>以本機保護金鑰計算的身分指紋。</returns>
    string CreateFingerprint(string? accountId, string? email, string authMode);

    /// <summary>
    /// 遮罩電子郵件，只保留可供使用者辨識的少量字元。
    /// </summary>
    /// <param name="email">原始電子郵件；沒有時為 <see langword="null" />。</param>
    /// <returns>遮罩後的電子郵件；沒有電子郵件時為空字串。</returns>
    string MaskEmail(string? email);
}
