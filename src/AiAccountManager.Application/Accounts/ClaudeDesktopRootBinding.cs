using CloudyWing.AiAccountManager.Application.Claude;

namespace CloudyWing.AiAccountManager.Application.Accounts;

/// <summary>
/// 保存可重新驗證的 Desktop 資料來源與 Local State key 摘要。
/// </summary>
/// <param name="Source">執行中觀察取得的版本化來源證據。</param>
/// <param name="LocalStateEncryptedKeySha256">停止後從受驗證來源取得的 Local State key SHA-256 摘要。</param>
public sealed record ClaudeDesktopRootBinding(
    ClaudeDesktopRootBinding.Observation Source,
    string LocalStateEncryptedKeySha256
) {
    /// <summary>
    /// 保存同一程序中的 Desktop 執行中來源觀察。
    /// </summary>
    /// <param name="SchemaVersion">來源證據的結構版本。</param>
    /// <param name="BindingId">此觀察建立的隨機來源識別碼。</param>
    /// <param name="WindowsUserSid">持有資源的 Windows 使用者 SID。</param>
    /// <param name="InstallationId">安裝識別碼。</param>
    /// <param name="Kind">已依程序身分驗證的安裝方式。</param>
    /// <param name="AppUserModelId">MSIX 應用程式識別碼。</param>
    /// <param name="PackageFamilyName">MSIX package family。</param>
    /// <param name="PackageFullName">MSIX package 完整名稱。</param>
    /// <param name="DirectRegistrationSource">直接安裝的登錄來源；未登錄時為 null。</param>
    /// <param name="ExecutablePath">直接安裝的執行檔路徑。</param>
    /// <param name="ExecutableSha256">直接安裝執行檔的 SHA-256 摘要。</param>
    /// <param name="DataRoot">標準化資料根目錄。</param>
    /// <param name="CookieRelativePath">Cookie 資料庫相對於資料根的路徑。</param>
    /// <param name="RootVolumeSerialNumber">資料根所在磁碟區的識別碼。</param>
    /// <param name="RootFileId">資料根的檔案系統識別碼。</param>
    /// <param name="LayoutVersion">已驗證的資料配置版本。</param>
    /// <param name="Processes">觀察時逐一驗證的程序識別與建立時間。</param>
    public sealed record Observation(
        int SchemaVersion,
        Guid BindingId,
        string WindowsUserSid,
        string InstallationId,
        IClaudeDesktopInstallationLocator.InstallKind Kind,
        string? AppUserModelId,
        string? PackageFamilyName,
        string? PackageFullName,
        string? DirectRegistrationSource,
        string? ExecutablePath,
        string? ExecutableSha256,
        string DataRoot,
        string CookieRelativePath,
        ulong RootVolumeSerialNumber,
        string RootFileId,
        int LayoutVersion,
        IReadOnlyList<ObservedProcess> Processes
    );

    /// <summary>
    /// 記錄觀察當下驗證過的程序，建立時間用於辨識 PID 重用。
    /// </summary>
    /// <param name="ProcessId">程序識別碼。</param>
    /// <param name="CreatedAtUtc">程序建立時間。</param>
    public sealed record ObservedProcess(int ProcessId, DateTimeOffset CreatedAtUtc);
}
