using CloudyWing.AiAccountManager.Application.Recommendations;

namespace CloudyWing.AiAccountManager.Desktop.Components.Dashboard;

/// <summary>
/// 定義分開讀取 Codex 與 Claude 儀表板資料的唯讀契約。
/// </summary>
public interface IDashboardOverviewReader {
    /// <summary>
    /// 讀取目前 Codex Desktop 身分及其已保存額度。
    /// </summary>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>不含原始認證資料的 Codex 額度投影。</returns>
    Task<DashboardOverviewSnapshot.Provider> ReadCodexAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 讀取目前 Claude Desktop 身分及其已保存額度。
    /// </summary>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>不含原始認證資料的 Claude 額度投影。</returns>
    Task<DashboardOverviewSnapshot.Provider> ReadClaudeAsync(CancellationToken cancellationToken);

    /// <summary>讀取 Codex 帳號建議。</summary>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>建議帳號。</returns>
    Task<RecommendationAccount?> ReadCodexRecommendationAsync(CancellationToken cancellationToken) => Task.FromResult<RecommendationAccount?>(null);

    /// <summary>讀取 Claude 帳號建議。</summary>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>建議帳號。</returns>
    Task<RecommendationAccount?> ReadClaudeRecommendationAsync(CancellationToken cancellationToken) => Task.FromResult<RecommendationAccount?>(null);
}
