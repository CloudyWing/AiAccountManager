using System.Security.AccessControl;
using System.Security.Principal;

namespace CloudyWing.AiAccountManager.Windows.Storage;

/// <summary>
/// 建立僅供目前 Windows 使用者存取的產品資料目錄。
/// </summary>
public sealed class CurrentUserDataDirectory {
    private readonly string? dataRootOverride;

    /// <summary>
    /// 初始化資料目錄位置。
    /// </summary>
    /// <param name="dataRootOverride">自訂資料目錄；空白時使用預設位置。</param>
    public CurrentUserDataDirectory(string? dataRootOverride) {
        this.dataRootOverride = string.IsNullOrWhiteSpace(dataRootOverride)
            ? null
            : Path.GetFullPath(dataRootOverride);
    }

    /// <summary>
    /// 建立資料目錄並設定目前使用者的完整控制權限。
    /// </summary>
    /// <returns>資料目錄的完整路徑。</returns>
    /// <exception cref="InvalidOperationException">無法取得目前使用者的 SID。</exception>
    public string EnsureCreated() {
        string path = dataRootOverride
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AiAccountManager",
                "data"
            );
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(path);

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier userSid = identity.User
            ?? throw new InvalidOperationException("無法取得目前 Windows 使用者 SID。");
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            userSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow
        ));
        new DirectoryInfo(path).SetAccessControl(security);
        return path;
    }
}
