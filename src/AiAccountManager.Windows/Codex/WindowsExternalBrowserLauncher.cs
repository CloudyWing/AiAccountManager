using System.Diagnostics;
using CloudyWing.AiAccountManager.Application.Codex;

namespace CloudyWing.AiAccountManager.Windows.Codex;

/// <summary>
/// 使用系統瀏覽器開啟 HTTPS 驗證網址。
/// </summary>
public sealed class WindowsExternalBrowserLauncher : IExternalBrowserLauncher {
    /// <inheritdoc />
    public Task OpenAsync(Uri uri, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)) {
            throw new ArgumentException("Only a validated absolute HTTPS OAuth URL can be opened.", nameof(uri));
        }

        ProcessStartInfo startInfo = new(uri.AbsoluteUri) { UseShellExecute = true };
        using Process? browser = Process.Start(startInfo);
        if (browser is null) {
            throw new InvalidOperationException("The system browser could not be started.");
        }

        return Task.CompletedTask;
    }
}
