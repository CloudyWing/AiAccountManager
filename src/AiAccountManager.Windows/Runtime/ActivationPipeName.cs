using System.Security.Principal;

namespace CloudyWing.AiAccountManager.Windows.Runtime;

/// <summary>提供目前使用者的啟動管道名稱。</summary>
public static class ActivationPipeName {
    /// <summary>建立指定 SID 的管道名稱。</summary>
    public static string Create(string sid) => "AiAccountManager-" + sid;

    /// <summary>取得目前使用者的管道名稱。</summary>
    public static string ForCurrentUser() {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return Create(identity.User?.Value ?? throw new InvalidOperationException("無法取得目前 Windows 使用者 SID。"));
    }
}
