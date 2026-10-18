using System.Security.Cryptography;

namespace CloudyWing.AiAccountManager.Windows.Storage;

/// <summary>
/// 在使用者資料目錄中讀取及以雜湊檢查衝突後原子寫入檔案。
/// </summary>
public sealed class AtomicJsonFileStore {
    private readonly string rootPath;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>
    /// 初始化檔案儲存體並建立資料目錄。
    /// </summary>
    /// <param name="dataDirectory">目前使用者的資料目錄。</param>
    public AtomicJsonFileStore(CurrentUserDataDirectory dataDirectory) {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        rootPath = dataDirectory.EnsureCreated();
    }

    /// <summary>
    /// 讀取檔案內容及其 SHA-256 雜湊值。
    /// </summary>
    /// <param name="relativePath">相對於資料目錄的路徑。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>檔案存在狀態、內容與雜湊值。</returns>
    public async Task<ReadResult> ReadAsync(string relativePath, CancellationToken cancellationToken) {
        string path = ResolvePath(relativePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (!File.Exists(path)) {
                return new ReadResult(false, [], "");
            }

            byte[] content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return new ReadResult(true, content, Hash(content));
        } finally {
            gate.Release();
        }
    }

    /// <summary>
    /// 驗證內容並在預期雜湊相符時以暫存檔原子寫入。
    /// </summary>
    /// <param name="relativePath">相對於資料目錄的路徑。</param>
    /// <param name="expectedSha256">預期的現有雜湊值；新檔案使用 <see langword="null" />。</param>
    /// <param name="content">要寫入的內容。</param>
    /// <param name="validate">驗證內容的函式。</param>
    /// <param name="cancellationToken">取消作業的權杖。</param>
    /// <returns>寫入狀態、雜湊值及衝突狀態。</returns>
    public async Task<WriteResult> WriteAsync(
        string relativePath,
        string? expectedSha256,
        byte[] content,
        Func<byte[], bool> validate,
        CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(validate);

        string path = ResolvePath(relativePath);
        string? parentPath = Path.GetDirectoryName(path);
        if (parentPath is null) {
            throw new InvalidOperationException("資料檔案路徑沒有父目錄。");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool temporaryCreated = false;
        try {
            Directory.CreateDirectory(parentPath);
            bool exists = File.Exists(path);
            if (expectedSha256 is null) {
                if (exists) {
                    return new WriteResult(false, "", true);
                }
            } else if (!exists) {
                return new WriteResult(false, "", true);
            } else {
                byte[] currentContent = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(Hash(currentContent), expectedSha256, StringComparison.OrdinalIgnoreCase)) {
                    return new WriteResult(false, "", true);
                }
            }

            if (!validate(content)) {
                throw new InvalidDataException("新資料未通過 schema 驗證。");
            }

            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough
            )) {
                temporaryCreated = true;
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path)) {
                File.Replace(temporaryPath, path, null, ignoreMetadataErrors: true);
            } else {
                File.Move(temporaryPath, path);
            }

            temporaryCreated = false;
            return new WriteResult(true, Hash(content), false);
        } catch (Exception operationFailure) {
            if (temporaryCreated && File.Exists(temporaryPath)) {
                try {
                    File.Delete(temporaryPath);
                } catch (Exception cleanupFailure) {
                    throw new AggregateException(
                        "Atomic write failed and its staging file could not be removed.",
                        operationFailure,
                        cleanupFailure
                    );
                }
            }

            throw;
        } finally {
            gate.Release();
        }
    }

    /// <summary>
    /// 計算內容的 SHA-256 雜湊值。
    /// </summary>
    /// <param name="content">要計算雜湊的內容。</param>
    /// <returns>十六進位雜湊字串。</returns>
    public static string Hash(byte[] content) {
        return Convert.ToHexString(SHA256.HashData(content));
    }

    private string ResolvePath(string relativePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        string rootPrefix = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException("資料檔案路徑必須位於產品資料目錄內。", nameof(relativePath));
        }

        return fullPath;
    }

    /// <summary>
    /// 表示檔案讀取結果。
    /// </summary>
    /// <param name="Exists">若檔案存在則為 <see langword="true" />；否則為 <see langword="false" />。</param>
    /// <param name="Content">檔案內容。</param>
    /// <param name="Sha256">內容的 SHA-256 雜湊值。</param>
    public sealed record ReadResult(bool Exists, byte[] Content, string Sha256);

    /// <summary>
    /// 表示檔案寫入結果。
    /// </summary>
    /// <param name="Saved">若已寫入檔案則為 <see langword="true" />；否則為 <see langword="false" />。</param>
    /// <param name="Sha256">寫入內容的 SHA-256 雜湊值。</param>
    /// <param name="Conflict">若現有檔案與預期不符則為 <see langword="true" />；否則為 <see langword="false" />。</param>
    public sealed record WriteResult(bool Saved, string Sha256, bool Conflict);
}
