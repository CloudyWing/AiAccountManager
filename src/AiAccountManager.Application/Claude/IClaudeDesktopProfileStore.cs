using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 定義 Claude Desktop 身分讀取、穩定快照擷取及安全還原契約。
/// </summary>
public interface IClaudeDesktopProfileStore {
    /// <summary>
    /// 讀取目前 Desktop auth 身分與可供 OAuth 驗證的 token 候選，不讀取 Cookie 或 Local State。
    /// </summary>
    /// <param name="installation">唯一定位的 Desktop 安裝。</param>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>本機 account UUID 與 token 候選。</returns>
    Task<ReadResult> ReadCurrentAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 鎖定捕捉當下存在的完整認證與 Cookie 檔案集合，並從同一組 handles 擷取穩定快照與 Local State key 摘要。
    /// </summary>
    /// <param name="installation">Desktop 停止後重驗的來源觀察或持久綁定。</param>
    /// <param name="cancellationToken">取消擷取作業的權杖。</param>
    /// <returns>持有檔案 handles 的快照 lease。</returns>
    Task<StableSnapshotLease> CaptureStableSnapshotAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 從已保存的 Desktop snapshot 解析 OAuth token 候選，不執行檔案或網路 I/O。
    /// </summary>
    /// <param name="snapshot">已保存的 Desktop auth 快照。</param>
    /// <returns>從 tokenCache 欄位解析出的 Bearer token 候選。</returns>
    IReadOnlyList<TokenCandidate> GetTokenCandidates(ClaudeAccountProfile.DesktopSnapshot snapshot);

    /// <summary>
    /// 在任何 Desktop 檔案寫入前核對完整來源綁定及交易狀態，再還原 auth、Cookie 資料庫及已捕捉的 sidecar。
    /// </summary>
    /// <param name="installation">包含重驗後持久來源綁定的 Desktop 安裝。</param>
    /// <param name="snapshot">已保存的目標 profile snapshot。</param>
    /// <param name="cancellationToken">取消還原作業的權杖。</param>
    Task RestoreAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        ClaudeAccountProfile.DesktopSnapshot snapshot,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 依固定來源綁定建立交易的目標 staging、來源備份與 durable intent，不修改 Desktop live 檔案。
    /// </summary>
    /// <param name="installation">包含重驗後來源綁定的 Desktop 安裝。</param>
    /// <param name="targetSnapshot">交易固定的目標 profile 快照。</param>
    /// <param name="transactionId">應用程式 journal 的交易識別碼。</param>
    /// <param name="cancellationToken">取消準備作業的權杖。</param>
    Task PrepareRestoreAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        ClaudeAccountProfile.DesktopSnapshot targetSnapshot,
        Guid transactionId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 依 durable intent 逐檔套用目標 auth 與 Cookie，並於每次 live mutation 前重查程序狀態。
    /// </summary>
    /// <param name="installation">包含重驗後來源綁定的 Desktop 安裝。</param>
    /// <param name="transactionId">應用程式 journal 的交易識別碼。</param>
    /// <param name="cancellationToken">取消套用作業的權杖。</param>
    Task ApplyRestoreAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid transactionId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 依可信的交易證據與前後狀態冪等還原來源 auth 與 Cookie；競爭或缺證時保留交易資料。
    /// </summary>
    /// <param name="installation">包含重驗後來源綁定的 Desktop 安裝。</param>
    /// <param name="transactionId">應用程式 journal 的交易識別碼。</param>
    /// <param name="cancellationToken">取消回復作業的權杖。</param>
    Task RestoreTransactionAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid transactionId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 依固定來源綁定與精確 manifest 清理指定交易資料，保留未知或不符的項目。
    /// </summary>
    /// <param name="installation">包含重驗後來源綁定的 Desktop 安裝。</param>
    /// <param name="transactionId">應用程式 journal 的交易識別碼。</param>
    /// <param name="cancellationToken">取消清理作業的權杖。</param>
    Task CommitTransactionAsync(
        IClaudeDesktopInstallationLocator.Installation installation,
        Guid transactionId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 提供 lockless identity 讀取結果及僅供記憶體使用的 OAuth token 候選。
    /// </summary>
    /// <param name="LastKnownAccountUuid">Desktop 設定中的最近帳號 UUID。</param>
    /// <param name="TokenCandidates">從 Desktop tokenCache 欄位解析出的 Bearer token 候選。</param>
    public sealed record ReadResult(
        string? LastKnownAccountUuid,
        IReadOnlyList<TokenCandidate> TokenCandidates
    );

    /// <summary>
    /// 持有穩定 Desktop snapshot 與檔案 handles 的非同步 lease。
    /// </summary>
    public interface StableSnapshotLease : IAsyncDisposable {
        /// <summary>
        /// 取得從鎖定 handles 擷取的完整 Desktop snapshot。
        /// </summary>
        /// <value>包含 auth、Cookie、Cookie journal 與捕捉時存在之 WAL／SHM 的快照。</value>
        ClaudeAccountProfile.DesktopSnapshot Snapshot { get; }

        /// <summary>
        /// 取得與快照同一組 config bytes 解析出的 token 候選。
        /// </summary>
        /// <value>不可寫入 log 或使用者畫面的 Bearer token 候選。</value>
        IReadOnlyList<TokenCandidate> TokenCandidates { get; }

        /// <summary>
        /// 在 profile repository 保存前確認檔案集合與 handles 仍符合捕捉時的 identity 與 metadata。
        /// </summary>
        /// <param name="cancellationToken">取消驗證作業的權杖。</param>
        /// <returns>快照仍穩定時為 true，否則為 false。</returns>
        Task<bool> ValidateAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// 保存 Desktop tokenCache 中解析出的單一 Bearer token 候選。
    /// </summary>
    public sealed class TokenCandidate {
        /// <summary>
        /// 建立一個 Desktop OAuth token 候選。
        /// </summary>
        /// <param name="accessToken">供 OAuth API 呼叫使用的 Bearer token。</param>
        /// <param name="sourceField">候選 token 所屬的 Desktop auth 欄位名稱。</param>
        public TokenCandidate(string accessToken, string sourceField) {
            ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceField);
            AccessToken = accessToken;
            SourceField = sourceField;
        }

        /// <summary>
        /// 取得 Bearer token。
        /// </summary>
        /// <value>不得寫入 log 或使用者畫面的認證字串。</value>
        public string AccessToken { get; }

        /// <summary>
        /// 取得 tokenCache 欄位名稱。
        /// </summary>
        /// <value>Desktop auth 欄位名稱。</value>
        public string SourceField { get; }

        /// <summary>
        /// 以遮蔽文字呈現 token 候選。
        /// </summary>
        /// <returns>不包含 Bearer token 的描述。</returns>
        public override string ToString() {
            return $"Claude Desktop token candidate from {SourceField} (redacted).";
        }
    }
}
