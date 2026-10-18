using System.Text.Json;

namespace CloudyWing.AiAccountManager.Application.Codex;

/// <summary>提供 Codex 重置消息的外部查詢。</summary>
public interface ICodexResetNewsClient {
    /// <summary>讀取重置消息。</summary>
    /// <param name="cancellationToken">取消查詢的權杖。</param>
    /// <returns>回應資料、限制時間或失敗狀態。</returns>
    Task<Result> GetAsync(CancellationToken cancellationToken);

    /// <summary>表示外部查詢結果。</summary>
    /// <param name="Payload">成功時的 JSON 資料。</param>
    /// <param name="IsRateLimited">是否遭到頻率限制。</param>
    /// <param name="RetryAfterUtc">最早可重試的時間。</param>
    public sealed record Result(JsonElement? Payload, bool IsRateLimited, DateTimeOffset? RetryAfterUtc);
}
