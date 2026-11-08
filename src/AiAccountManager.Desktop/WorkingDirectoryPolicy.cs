using System.IO;
using System.Security;

namespace CloudyWing.AiAccountManager.Desktop;

/// <summary>
/// 判定本程式的工作目錄是否位於應用程式目錄內，並在需要時移到安裝目錄之外。
/// </summary>
/// <remarks>
/// 未指定工作目錄的子程序會繼承本程式的工作目錄。工作目錄位於安裝目錄時，子程序常駐會鎖住該目錄，
/// 造成解除安裝後安裝根目錄殘留。
/// </remarks>
public static class WorkingDirectoryPolicy {
    /// <summary>
    /// 判定目前工作目錄是否需要移出應用程式目錄，並回傳替代的工作目錄。
    /// </summary>
    /// <param name="currentDirectory">目前的工作目錄。</param>
    /// <param name="applicationDirectory">應用程式所在目錄。</param>
    /// <param name="fallbackDirectory">需要移出時改用的目錄。</param>
    /// <returns>
    /// 目前工作目錄等於或位於應用程式目錄內，且替代目錄存在時為替代目錄；否則為 <see langword="null" />。
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="currentDirectory" /> 或 <paramref name="applicationDirectory" /> 為 <see langword="null" /> 時。
    /// </exception>
    public static string? ResolveReplacement(
        string currentDirectory,
        string applicationDirectory,
        string? fallbackDirectory
    ) {
        ArgumentNullException.ThrowIfNull(currentDirectory);
        ArgumentNullException.ThrowIfNull(applicationDirectory);

        if (string.IsNullOrWhiteSpace(fallbackDirectory) || !Directory.Exists(fallbackDirectory)) {
            return null;
        }

        string current = Normalize(currentDirectory);
        string application = Normalize(applicationDirectory);
        // 根目錄已帶結尾分隔字元，不重複附加；其餘以分隔字元收尾，避免 current2 被誤判為 current 的子目錄
        string applicationPrefix = Path.EndsInDirectorySeparator(application)
            ? application
            : application + Path.DirectorySeparatorChar;
        bool isInsideApplication = string.Equals(current, application, StringComparison.OrdinalIgnoreCase)
            || current.StartsWith(applicationPrefix, StringComparison.OrdinalIgnoreCase);

        return isInsideApplication ? fallbackDirectory : null;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// 工作目錄位於應用程式目錄內時，改為使用者設定檔目錄。
    /// </summary>
    /// <remarks>
    /// 判定或設定失敗時維持原工作目錄，不中斷啟動。
    /// </remarks>
    public static void Apply() {
        try {
            string? replacement = ResolveReplacement(
                Environment.CurrentDirectory,
                AppContext.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            );
            if (replacement is not null) {
                Environment.CurrentDirectory = replacement;
            }
        } catch (ArgumentException) {
            // 維持原工作目錄，不中斷啟動
        } catch (NotSupportedException) {
            // 維持原工作目錄，不中斷啟動
        } catch (IOException) {
            // 維持原工作目錄，不中斷啟動
        } catch (UnauthorizedAccessException) {
            // 維持原工作目錄，不中斷啟動
        } catch (SecurityException) {
            // 維持原工作目錄，不中斷啟動
        }
    }
}
