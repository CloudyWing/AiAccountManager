namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>產生安全的帳號頁導覽與卡片識別碼。</summary>
public static class AccountNavigation {
    /// <summary>取得帳號頁路徑。</summary>
    /// <param name="provider">帳號來源。</param>
    /// <param name="accountId">帳號識別碼。</param>
    /// <returns>含帳號查詢參數的路徑。</returns>
    public static string Href(string provider, Guid accountId) => $"/{provider}?account={Uri.EscapeDataString(accountId.ToString("D"))}";

    /// <summary>取得帳號卡片識別碼。</summary>
    /// <param name="provider">帳號來源。</param>
    /// <param name="accountId">帳號識別碼。</param>
    /// <returns>卡片識別碼。</returns>
    public static string ElementId(string provider, Guid accountId) => $"{provider}-account-{accountId:N}";
}
