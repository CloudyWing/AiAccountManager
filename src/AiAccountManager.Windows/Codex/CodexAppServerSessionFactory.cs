using System.Diagnostics;
using CloudyWing.AiAccountManager.Application.Codex;

namespace CloudyWing.AiAccountManager.Windows.Codex;

/// <summary>
/// 建立使用隔離帳號目錄的 Codex app-server 工作階段。
/// </summary>
public sealed class CodexAppServerSessionFactory : ICodexAppServerSessionFactory {
    private readonly ICodexProfileStorage profileStorage;
    private readonly CodexAppServerBinaryVerifier binaryVerifier;

    /// <summary>
    /// 初始化工作階段工廠。
    /// </summary>
    /// <param name="profileStorage">帳號資料儲存體。</param>
    /// <param name="binaryVerifier">執行檔驗證器。</param>
    public CodexAppServerSessionFactory(
        ICodexProfileStorage profileStorage,
        CodexAppServerBinaryVerifier binaryVerifier
    ) {
        this.profileStorage = profileStorage;
        this.binaryVerifier = binaryVerifier;
    }

    /// <inheritdoc />
    public async Task<ICodexAppServerSession> CreateAsync(
        Guid accountId,
        long profileRevision,
        CancellationToken cancellationToken
    ) {
        if (accountId == Guid.Empty || profileRevision < 1) {
            throw new ArgumentException("Codex session identity is invalid.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string homePath = profileStorage.GetHomePath(accountId);
        if (!Directory.Exists(homePath)) {
            throw new DirectoryNotFoundException("The isolated Codex profile home does not exist.");
        }

        string configPath = Path.Combine(homePath, "config.toml");
        if (!File.Exists(configPath)
            || !string.Equals(
                await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false),
                "cli_auth_credentials_store = \"keyring\"\n",
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException("The isolated Codex profile keyring configuration is missing or changed.");
        }

        if (await profileStorage.RemoveUnexpectedAuthFileAsync(accountId, cancellationToken).ConfigureAwait(false)) {
            throw new InvalidDataException("An isolated Codex profile contains auth.json; the plaintext file was removed.");
        }

        string executablePath = binaryVerifier.VerifyAndGetExecutablePath();
        ProcessStartInfo startInfo = new(executablePath) {
            WorkingDirectory = homePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");
        startInfo.Environment["CODEX_HOME"] = homePath;
        startInfo.Environment.Remove("CODEX_API_KEY");
        startInfo.Environment.Remove("OPENAI_API_KEY");
        startInfo.Environment.Remove("CODEX_ACCESS_TOKEN");
        startInfo.Environment.Remove("CODEX_REFRESH_TOKEN");
        startInfo.Environment.Remove("CODEX_APP_SERVER_LOGIN_ISSUER");
        startInfo.Environment.Remove("CODEX_APP_SERVER_LOGIN_CLIENT_ID");
        startInfo.Environment.Remove("CODEX_APP_SERVER_DEV_OPEN_APP_URL");
        startInfo.Environment.Remove("CODEX_REFRESH_TOKEN_URL_OVERRIDE");
        startInfo.Environment.Remove("CODEX_REVOKE_TOKEN_URL_OVERRIDE");

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        try {
            if (!process.Start()) {
                throw new InvalidOperationException("The Codex app-server process did not start.");
            }

            return new CodexAppServerSession(process, accountId, profileRevision);
        } catch {
            process.Dispose();
            throw;
        }
    }
}
