namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>
/// 為指定的 Codex 帳號建立 app-server 工作階段。
/// </summary>
public interface ICodexAppServerSessionFactory {
    /// <summary>
    /// 以指定帳號的隔離 profile 啟動 app-server 工作階段。
    /// </summary>
    /// <param name="accountId">Codex 帳號識別碼。</param>
    /// <param name="profileRevision">帳號 profile 的版次，用來確認使用的是目前版本的認證資料。</param>
    /// <param name="cancellationToken">取消啟動的權杖。</param>
    /// <returns>已啟動的工作階段；呼叫端負責釋放。</returns>
    Task<ICodexAppServerSession> CreateAsync(
        Guid accountId,
        long profileRevision,
        CancellationToken cancellationToken
    );
}
