using System.Security.Principal;

namespace CloudyWing.AiAccountManager.Windows.Runtime;

/// <summary>
/// 以目前 Windows 使用者的具名 Mutex 控制單一執行個體。
/// </summary>
public sealed class WindowsSingleInstance : IDisposable {
    private readonly Mutex mutex;
    private readonly bool ownsMutex;

    private WindowsSingleInstance(Mutex mutex, bool ownsMutex) {
        this.mutex = mutex;
        this.ownsMutex = ownsMutex;
    }

    /// <summary>
    /// 嘗試取得目前使用者的單一執行個體使用權。
    /// </summary>
    /// <param name="instance">成功時取得的使用權；失敗時為 <see langword="null" />。</param>
    /// <returns>若取得使用權則為 <see langword="true" />；否則為 <see langword="false" />。</returns>
    /// <exception cref="InvalidOperationException">無法取得目前使用者的 SID。</exception>
    public static bool TryCreate(out WindowsSingleInstance? instance) {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value
            ?? throw new InvalidOperationException("無法取得目前 Windows 使用者 SID。");
        string mutexName = "Local\\AiAccountManager-" + sid;
        Mutex mutex = new(initiallyOwned: true, mutexName, out bool createdNew);
        if (!createdNew) {
            mutex.Dispose();
            instance = null;
            return false;
        }

        instance = new WindowsSingleInstance(mutex, true);
        return true;
    }

    /// <summary>
    /// 釋放持有的 Mutex 及其資源。
    /// </summary>
    public void Dispose() {
        if (ownsMutex) {
            mutex.ReleaseMutex();
        }

        mutex.Dispose();
    }
}
