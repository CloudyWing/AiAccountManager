namespace CloudyWing.AiAccountManager.Application.Updates;

/// <summary>
/// 解析使用者設定或預設的更新來源。
/// </summary>
public static class UpdateFeedResolver {
    /// <summary>
    /// 取得預設更新來源。
    /// </summary>
    public const string DefaultFeedUrl = "https://github.com/CloudyWing/AiAccountManager/releases/latest/download";

    /// <summary>
    /// 解析有效的更新來源網址。
    /// </summary>
    /// <param name="value">使用者設定的來源；空值使用預設來源。</param>
    /// <returns>有效的 HTTPS 更新來源。</returns>
    /// <exception cref="InvalidDataException">來源不是不含帳密的絕對 HTTPS 網址。</exception>
    public static Uri Resolve(string? value) {
        string effectiveValue = string.IsNullOrWhiteSpace(value) ? DefaultFeedUrl : value;
        if (!Uri.TryCreate(effectiveValue, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)) {
            throw new InvalidDataException("更新來源必須是 HTTPS 網址，且不可包含帳號密碼。");
        }

        return uri;
    }
}
