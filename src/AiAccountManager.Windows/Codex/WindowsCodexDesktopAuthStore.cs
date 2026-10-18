using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;

namespace CloudyWing.AiAccountManager.Windows.Codex;

/// <summary>
/// 以目前 Windows 使用者的 `.codex/auth.json` 提供 Codex Desktop 登入檔的安全讀取與原子取代。
/// </summary>
public sealed class WindowsCodexDesktopAuthStore : ICodexDesktopAuthStore {
    private const int MaximumAuthFileBytes = 1024 * 1024;
    private readonly WindowsCodexLocalAuthIdentityReader reader;
    private readonly string authFilePath;

    /// <summary>
    /// 初始化固定存取使用者 profile 下 `.codex/auth.json` 的 store。
    /// </summary>
    public WindowsCodexDesktopAuthStore()
        : this(new WindowsCodexLocalAuthIdentityReader()) {
    }

    internal WindowsCodexDesktopAuthStore(WindowsCodexLocalAuthIdentityReader reader) {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        authFilePath = reader.AuthFilePath;
    }

    /// <inheritdoc />
    public async Task<ICodexDesktopAuthStore.ReadResult> ReadAsync(CancellationToken cancellationToken) {
        (CodexLocalAuthReadResult result, byte[]? content) = await reader.ReadWithContentAsync(cancellationToken)
            .ConfigureAwait(false);
        return new(result.Status, content, result.Identity);
    }

    /// <inheritdoc />
    public CodexLocalAuthReadResult Parse(byte[] content) {
        ArgumentNullException.ThrowIfNull(content);
        return WindowsCodexLocalAuthIdentityReader.Parse(content);
    }

    /// <inheritdoc />
    public async Task ReplaceAsync(byte[] content, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0 || content.Length > MaximumAuthFileBytes) {
            throw new ArgumentException("Codex Desktop auth content size is invalid.", nameof(content));
        }

        string directoryPath = Path.GetDirectoryName(authFilePath)
            ?? throw new InvalidOperationException("Codex Desktop auth directory is unavailable.");
        EnsureNotReparsePoint(directoryPath);
        if (File.Exists(authFilePath)) {
            EnsureNotReparsePoint(authFilePath);
        }

        string temporaryPath = Path.Combine(directoryPath, $"auth.json.aam-{Guid.NewGuid():N}.tmp");
        try {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous
            )) {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(authFilePath)) {
                File.Replace(temporaryPath, authFilePath, null, ignoreMetadataErrors: true);
            } else {
                File.Move(temporaryPath, authFilePath);
            }
        } finally {
            if (File.Exists(temporaryPath)) {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void EnsureNotReparsePoint(string path) {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) {
            throw new IOException("Codex Desktop auth path must not be a reparse point.");
        }
    }
}
