using System.Security.Cryptography;
using System.Text.Json;

namespace CloudyWing.AiAccountManager.Windows.Codex;

/// <summary>
/// 驗證固定版本的 Codex app-server 套件及執行檔雜湊。
/// </summary>
public sealed class CodexAppServerBinaryVerifier {
    /// <summary>
    /// 固定的 app-server 版本。
    /// </summary>
    public const string AppServerVersion = "rust-v0.162.1";
    /// <summary>
    /// 固定套件壓縮檔的 SHA-256 雜湊值。
    /// </summary>
    public const string ExpectedArchiveSha256 = "7E4B9F8D0622BDF946B5D8BFE6DDE873F90F5C8FDF69E450489FD4F52B7FFCEA";
    /// <summary>
    /// 固定執行檔的 SHA-256 雜湊值。
    /// </summary>
    public const string ExpectedExecutableSha256 = "9526F00E454EDBA5942822D89674C9857F18D7322F788F3C75D2256F692DEB46";
    private const string ExecutableName = "codex-app-server-x86_64-pc-windows-msvc.exe";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string baseDirectory;
    private readonly string expectedArchiveSha256;
    private readonly string expectedExecutableSha256;

    /// <summary>
    /// 初始化執行檔驗證器。
    /// </summary>
    /// <param name="baseDirectory">套件所在的基底目錄；未指定時使用應用程式基底目錄。</param>
    /// <param name="expectedArchiveSha256">預期壓縮檔雜湊值。</param>
    /// <param name="expectedExecutableSha256">預期執行檔雜湊值。</param>
    public CodexAppServerBinaryVerifier(
        string? baseDirectory = null,
        string expectedArchiveSha256 = ExpectedArchiveSha256,
        string expectedExecutableSha256 = ExpectedExecutableSha256
    ) {
        this.baseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        this.expectedArchiveSha256 = NormalizeHash(expectedArchiveSha256);
        this.expectedExecutableSha256 = NormalizeHash(expectedExecutableSha256);
    }

    /// <summary>
    /// 驗證套件資訊與執行檔雜湊，並取得執行檔路徑。
    /// </summary>
    /// <returns>已驗證的執行檔路徑。</returns>
    /// <exception cref="FileNotFoundException">執行檔或套件資訊檔不存在。</exception>
    /// <exception cref="InvalidDataException">套件資訊或執行檔雜湊無效。</exception>
    public string VerifyAndGetExecutablePath() {
        string versionDirectory = Path.Combine(
            baseDirectory,
            "tools",
            "codex-app-server",
            "0.162.1"
        );
        string executablePath = Path.Combine(versionDirectory, ExecutableName);
        string manifestPath = Path.Combine(versionDirectory, "codex-app-server.manifest.json");
        if (!File.Exists(executablePath) || !File.Exists(manifestPath)) {
            throw new FileNotFoundException("The pinned Codex app-server binary package is missing.");
        }

        CodexAppServerBinaryManifest? manifest;
        try {
            manifest = JsonSerializer.Deserialize<CodexAppServerBinaryManifest>(
                File.ReadAllBytes(manifestPath),
                JsonOptions
            );
        } catch (JsonException exception) {
            throw new InvalidDataException("The Codex app-server binary manifest is invalid.", exception);
        }

        if (manifest is null
            || !string.Equals(manifest.Schema, "ai-account-manager.codex-app-server-binary.v1", StringComparison.Ordinal)
            || !string.Equals(manifest.Version, AppServerVersion, StringComparison.Ordinal)
            || !string.Equals(NormalizeHash(manifest.ArchiveSha256), expectedArchiveSha256, StringComparison.Ordinal)
            || !string.Equals(NormalizeHash(manifest.ExecutableSha256), expectedExecutableSha256, StringComparison.Ordinal)) {
            throw new InvalidDataException("The Codex app-server binary manifest does not match the pinned release.");
        }

        using FileStream stream = new(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string actualHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actualHash, expectedExecutableSha256, StringComparison.Ordinal)) {
            throw new InvalidDataException("The Codex app-server executable hash does not match the pinned release.");
        }

        return executablePath;
    }

    private static string NormalizeHash(string value) {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit)) {
            return "";
        }

        return value.ToUpperInvariant();
    }
}

/// <summary>
/// 表示 Codex app-server 套件資訊。
/// </summary>
/// <param name="Schema">套件資訊格式識別值。</param>
/// <param name="Version">app-server 版本。</param>
/// <param name="ArchiveSha256">壓縮檔 SHA-256 雜湊值。</param>
/// <param name="ExecutableSha256">執行檔 SHA-256 雜湊值。</param>
public sealed record CodexAppServerBinaryManifest(
    string Schema,
    string Version,
    string ArchiveSha256,
    string ExecutableSha256
);
