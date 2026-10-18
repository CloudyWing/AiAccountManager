using CloudyWing.AiAccountManager.Application.Accounts;

namespace CloudyWing.AiAccountManager.Application.Claude;

/// <summary>
/// 定義 Claude Desktop 唯一安裝與資料根目錄的定位契約。
/// </summary>
public interface IClaudeDesktopInstallationLocator {
    /// <summary>
    /// 觀察執行中 Desktop 的程序與資料資源，建立只在目前程序有效的來源記錄。
    /// </summary>
    /// <param name="cancellationToken">取消觀察作業的權杖。</param>
    /// <returns>唯一來源觀察或明確的失敗狀態。</returns>
    Task<ObservationResult> ObserveRunningAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 在 Desktop 停止後重驗首次保存使用的記憶體觀察。
    /// </summary>
    /// <param name="observation">執行中取得且尚未保存的來源觀察。</param>
    /// <param name="cancellationToken">取消重驗作業的權杖。</param>
    /// <returns>重驗後的安裝描述或明確的失敗狀態。</returns>
    Task<LocateResult> ResolveObservationAfterStopAsync(
        ClaudeDesktopRootBinding.Observation observation,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// 重驗已保存的完整來源綁定及停止後的 Local State key 摘要。
    /// </summary>
    /// <param name="binding">受保護快照或交易中保存的來源綁定。</param>
    /// <param name="cancellationToken">取消重驗作業的權杖。</param>
    /// <returns>重驗後的安裝描述或明確的失敗狀態。</returns>
    Task<LocateResult> ResolveBoundAsync(ClaudeDesktopRootBinding binding, CancellationToken cancellationToken);

    /// <summary>
    /// 定位唯一支援的 Claude Desktop 安裝。
    /// </summary>
    /// <param name="cancellationToken">取消定位作業的權杖。</param>
    /// <returns>唯一安裝描述或明確的定位失敗狀態。</returns>
    Task<LocateResult> LocateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 定義 Desktop 安裝封裝方式。
    /// </summary>
    public enum InstallKind {
        /// <summary>透過 MSIX 安裝。</summary>
        Msix,

        /// <summary>直接安裝。</summary>
        Direct
    }

    /// <summary>
    /// 定義安裝定位結果。
    /// </summary>
    public enum LocateStatus {
        /// <summary>找到唯一受支援的安裝。</summary>
        Found,

        /// <summary>目前安裝版本或型態不受支援。</summary>
        Unsupported,

        /// <summary>找到多個候選，無法安全選定。</summary>
        Ambiguous,

        /// <summary>來源證據或程序狀態不足以判定。</summary>
        Unknown
    }

    /// <summary>
    /// 描述已定位的 Claude Desktop 安裝。
    /// </summary>
    /// <param name="InstallationId">安裝識別碼。</param>
    /// <param name="Kind">安裝封裝方式。</param>
    /// <param name="DataRoot">Desktop 設定與 Cookie 的資料根目錄。</param>
    /// <param name="DataRootFingerprint">資料根目錄的穩定指紋。</param>
    /// <param name="AppUserModelId">MSIX 啟動識別碼。</param>
    /// <param name="ExecutablePath">直接安裝的執行檔路徑。</param>
    /// <param name="Observation">首次保存前經重驗的來源觀察。</param>
    /// <param name="Binding">已重驗的持久來源綁定。</param>
    public sealed record Installation(
        string InstallationId,
        InstallKind Kind,
        string DataRoot,
        string DataRootFingerprint,
        string? AppUserModelId,
        string? ExecutablePath,
        ClaudeDesktopRootBinding.Observation? Observation = null,
        ClaudeDesktopRootBinding? Binding = null
    );

    /// <summary>
    /// 回傳執行中觀察結果；未完成首次保存的觀察不得跨管理工具程序使用。
    /// </summary>
    /// <param name="Status">觀察狀態。</param>
    /// <param name="Observation">成功時的來源觀察。</param>
    /// <param name="FailureCode">不含本機路徑或機密的失敗代碼。</param>
    /// <param name="Installation">同次觀察產生的安裝描述，供執行中唯讀查詢與程序狀態使用。</param>
    public sealed record ObservationResult(
        LocateStatus Status,
        ClaudeDesktopRootBinding.Observation? Observation,
        string? FailureCode,
        Installation? Installation = null
    );

    /// <summary>
    /// 回傳安裝定位狀態與可用的安裝描述。
    /// </summary>
    /// <param name="Status">定位狀態。</param>
    /// <param name="Installation">成功時的安裝描述。</param>
    /// <param name="FailureCode">不含本機路徑或機密的失敗代碼。</param>
    public sealed record LocateResult(
        LocateStatus Status,
        Installation? Installation,
        string? FailureCode
    );
}
