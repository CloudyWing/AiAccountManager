using System.Net;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Codex;

namespace CloudyWing.AiAccountManager.Windows.Codex;

/// <summary>以固定 HTTPS 端點讀取 Codex 重置消息。</summary>
public sealed class WindowsCodexResetNewsClient : ICodexResetNewsClient, IDisposable {
    private readonly HttpClient httpClient = new(new SocketsHttpHandler {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(20) };

    /// <inheritdoc />
    public async Task<ICodexResetNewsClient.Result> GetAsync(CancellationToken cancellationToken) {
        using HttpResponseMessage response = await httpClient.GetAsync(
            "https://codex-resets.com/api/v1/status", HttpCompletionOption.ResponseHeadersRead, cancellationToken
        ).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) {
            DateTimeOffset? retry = response.Headers.RetryAfter?.Date;
            if (response.Headers.RetryAfter?.Delta is TimeSpan delay && delay >= TimeSpan.Zero) {
                retry = DateTimeOffset.UtcNow + delay;
            }
            return new(null, true, retry);
        }
        if (!response.IsSuccessStatusCode) {
            return new(null, false, null);
        }
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(json.RootElement.Clone(), false, null);
    }

    /// <summary>釋放 HTTP 用戶端。</summary>
    public void Dispose() => httpClient.Dispose();
}
