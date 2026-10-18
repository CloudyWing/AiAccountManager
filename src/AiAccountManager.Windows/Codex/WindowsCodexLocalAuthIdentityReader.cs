using System.Buffers;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using CloudyWing.AiAccountManager.Application.Codex;
using Microsoft.Win32.SafeHandles;

namespace CloudyWing.AiAccountManager.Windows.Codex;

/// <summary>
/// 唯讀解析目前 Windows 使用者 Codex 認證檔中的必要帳號識別資料。
/// </summary>
public sealed class WindowsCodexLocalAuthIdentityReader : ICodexLocalAuthIdentityReader {
    private const int MaximumAuthFileBytes = 1024 * 1024;
    private const int MaximumJwtPayloadBytes = 1024 * 1024;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private const int AccessDenied = 5;
    private const int FileNotFound = 2;
    private const int PathNotFound = 3;
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private readonly string userProfilePath;
    private readonly string codexDirectoryPath;
    private readonly string authFilePath;
    private readonly Action? beforePathIdentityRecheck;

    /// <summary>
    /// 初始化固定讀取使用者 profile 下 `.codex/auth.json` 的讀取器。
    /// </summary>
    public WindowsCodexLocalAuthIdentityReader()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) {
    }

    internal WindowsCodexLocalAuthIdentityReader(
        string userProfilePath,
        Action? beforePathIdentityRecheck = null
    ) {
        this.userProfilePath = string.IsNullOrWhiteSpace(userProfilePath)
            ? ""
            : Path.GetFullPath(userProfilePath);
        this.beforePathIdentityRecheck = beforePathIdentityRecheck;
        codexDirectoryPath = Path.Combine(this.userProfilePath, ".codex");
        authFilePath = Path.Combine(codexDirectoryPath, "auth.json");
    }

    /// <inheritdoc />
    public async Task<CodexLocalAuthReadResult> ReadCurrentAsync(CancellationToken cancellationToken) {
        return (await ReadWithRetryAsync(captureContent: false, cancellationToken).ConfigureAwait(false)).Result;
    }

    /// <summary>
    /// 取得固定的 `.codex/auth.json` 完整路徑。
    /// </summary>
    internal string AuthFilePath => authFilePath;

    /// <summary>
    /// 以與 <see cref="ReadCurrentAsync" /> 相同的安全檢查讀取 `auth.json`，並一併回傳檔案內容。
    /// </summary>
    /// <param name="cancellationToken">取消讀取作業的權杖。</param>
    /// <returns>解析結果；成功時另含檔案內容複本，呼叫端用畢須清除。</returns>
    internal Task<(CodexLocalAuthReadResult Result, byte[]? Content)> ReadWithContentAsync(
        CancellationToken cancellationToken
    ) {
        return ReadWithRetryAsync(captureContent: true, cancellationToken);
    }

    /// <summary>
    /// 解析一份 `auth.json` 內容的身分，不讀寫檔案。
    /// </summary>
    /// <param name="content">`auth.json` 位元組。</param>
    /// <returns>解析結果。</returns>
    internal static CodexLocalAuthReadResult Parse(ReadOnlySpan<byte> content) {
        if (content.Length > MaximumAuthFileBytes) {
            return Failure(CodexLocalAuthReadStatus.FileTooLarge);
        }

        try {
            return ParseAuthJson(content);
        } catch (ReaderFailure failure) {
            return Failure(failure.Status);
        } catch (Exception failure) when (failure is JsonException or FormatException or ArgumentException) {
            return Failure(CodexLocalAuthReadStatus.InvalidFormat);
        }
    }

    private async Task<(CodexLocalAuthReadResult Result, byte[]? Content)> ReadWithRetryAsync(
        bool captureContent,
        CancellationToken cancellationToken
    ) {
        for (int attempt = 0; attempt < 2; attempt++) {
            try {
                return await ReadOnceAsync(captureContent, cancellationToken).ConfigureAwait(false);
            } catch (ReaderFailure failure) when (failure.IsRetryable && attempt == 0) {
                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
            } catch (ReaderFailure failure) {
                return (Failure(failure.Status), null);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (IOException failure) when (IsSharingViolation(failure)) {
                if (attempt == 0) {
                    await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
                } else {
                    return (Failure(CodexLocalAuthReadStatus.SharingViolation), null);
                }
            } catch (Exception failure) {
                return (Failure(MapFailure(failure)), null);
            }
        }

        return (Failure(CodexLocalAuthReadStatus.Unavailable), null);
    }

    private async Task<(CodexLocalAuthReadResult Result, byte[]? Content)> ReadOnceAsync(
        bool captureContent,
        CancellationToken cancellationToken
    ) {
        EnsurePathIsSafe();
        using SafeFileHandle handle = OpenReadHandle(authFilePath);
        using FileStream stream = new(handle, FileAccess.Read, 4096, isAsync: false);
        FileIdentity before = GetFileIdentity(handle);
        string finalPath = GetFinalPath(handle);
        if (!PathsEqual(finalPath, authFilePath) || (before.Attributes & FileAttributeReparsePoint) != 0) {
            throw new ReaderFailure(CodexLocalAuthReadStatus.UnsafePath);
        }

        if (before.Length > MaximumAuthFileBytes) {
            throw new ReaderFailure(CodexLocalAuthReadStatus.FileTooLarge);
        }

        byte[] authBytes = new byte[MaximumAuthFileBytes + 1];
        try {
            int bytesRead = await ReadBoundedAsync(stream, authBytes, cancellationToken).ConfigureAwait(false);
            FileIdentity after = GetFileIdentity(handle);
            try {
                EnsurePathIsSafe();
            } catch (ReaderFailure failure) when (failure.Status == CodexLocalAuthReadStatus.FileMissing) {
                throw new ReaderFailure(CodexLocalAuthReadStatus.ChangedDuringRead, isRetryable: true);
            }

            beforePathIdentityRecheck?.Invoke();
            FileIdentity pathIdentity = GetPathIdentity(authFilePath);
            if (!before.IsSameFileAndVersion(after) || !before.IsSameFileAndVersion(pathIdentity)) {
                throw new ReaderFailure(CodexLocalAuthReadStatus.ChangedDuringRead, isRetryable: true);
            }

            if (bytesRead > MaximumAuthFileBytes) {
                throw new ReaderFailure(CodexLocalAuthReadStatus.FileTooLarge);
            }

            CodexLocalAuthReadResult result = ParseAuthJson(authBytes.AsSpan(0, bytesRead));
            byte[]? content = captureContent && result.Status == CodexLocalAuthReadStatus.Success
                ? authBytes.AsSpan(0, bytesRead).ToArray()
                : null;
            return (result, content);
        } finally {
            CryptographicOperations.ZeroMemory(authBytes);
        }
    }

    private void EnsurePathIsSafe() {
        if (string.IsNullOrWhiteSpace(userProfilePath)) {
            throw new ReaderFailure(CodexLocalAuthReadStatus.Unavailable);
        }

        FileAttributes directoryAttributes = GetAttributes(codexDirectoryPath);
        FileAttributes fileAttributes = GetAttributes(authFilePath);
        if ((directoryAttributes & FileAttributes.ReparsePoint) != 0
            || (fileAttributes & FileAttributes.ReparsePoint) != 0) {
            throw new ReaderFailure(CodexLocalAuthReadStatus.UnsafePath);
        }
    }

    private static FileAttributes GetAttributes(string path) {
        uint attributes = GetFileAttributes(ToExtendedPath(path));
        if (attributes == uint.MaxValue) {
            throw CreateFailure(Marshal.GetLastWin32Error());
        }

        return (FileAttributes)attributes;
    }

    private static SafeFileHandle OpenReadHandle(string path) {
        SafeFileHandle handle = CreateFile(
            ToExtendedPath(path),
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagSequentialScan,
            IntPtr.Zero
        );
        if (handle.IsInvalid) {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw CreateFailure(error);
        }

        return handle;
    }

    private static FileIdentity GetPathIdentity(string path) {
        using SafeFileHandle handle = OpenReadHandle(path);
        string finalPath = GetFinalPath(handle);
        FileIdentity identity = GetFileIdentity(handle);
        if (!PathsEqual(finalPath, path) || (identity.Attributes & FileAttributeReparsePoint) != 0) {
            throw new ReaderFailure(CodexLocalAuthReadStatus.UnsafePath);
        }

        return identity;
    }

    private static FileIdentity GetFileIdentity(SafeFileHandle handle) {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information)) {
            throw CreateFailure(Marshal.GetLastWin32Error());
        }

        long length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        return new FileIdentity(
            information.VolumeSerialNumber,
            information.FileIndexHigh,
            information.FileIndexLow,
            information.FileSizeHigh,
            information.FileSizeLow,
            information.LastWriteTimeHigh,
            information.LastWriteTimeLow,
            information.FileAttributes,
            length
        );
    }

    private static string GetFinalPath(SafeFileHandle handle) {
        StringBuilder buffer = new(32768);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) {
            throw CreateFailure(Marshal.GetLastWin32Error());
        }

        return NormalizeFinalPath(buffer.ToString());
    }

    private static async Task<int> ReadBoundedAsync(
        FileStream stream,
        byte[] buffer,
        CancellationToken cancellationToken
    ) {
        int total = 0;
        while (total < buffer.Length) {
            int count = await stream.ReadAsync(
                buffer.AsMemory(total, buffer.Length - total),
                cancellationToken
            ).ConfigureAwait(false);
            if (count == 0) {
                break;
            }

            total += count;
        }

        return total;
    }

    private static CodexLocalAuthReadResult ParseAuthJson(ReadOnlySpan<byte> jsonBytes) {
        try {
            JsonReaderOptions options = new() {
                MaxDepth = 32,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            };
            Utf8JsonReader reader = new(jsonBytes, options);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) {
                return Failure(CodexLocalAuthReadStatus.InvalidFormat);
            }

            string?[] propertyNames = new string?[34];
            List<string> accountIds = [];
            List<string> emails = [];
            string? authMode = null;
            bool hasAuthMode = false;
            while (reader.Read()) {
                if (reader.TokenType == JsonTokenType.PropertyName) {
                    int depth = reader.CurrentDepth;
                    if (depth >= propertyNames.Length) {
                        return Failure(CodexLocalAuthReadStatus.InvalidFormat);
                    }

                    propertyNames[depth] = reader.GetString();
                    continue;
                }

                if (reader.TokenType == JsonTokenType.EndObject || reader.TokenType == JsonTokenType.EndArray) {
                    continue;
                }

                int valueDepth = reader.CurrentDepth;
                string? propertyName = valueDepth < propertyNames.Length ? propertyNames[valueDepth] : null;
                bool isAuthField = valueDepth == 1
                    || (valueDepth == 2
                        && string.Equals(propertyNames[1], "tokens", StringComparison.OrdinalIgnoreCase));
                if (valueDepth == 1 && string.Equals(propertyName, "auth_mode", StringComparison.OrdinalIgnoreCase)) {
                    if (reader.TokenType != JsonTokenType.String) {
                        return Failure(CodexLocalAuthReadStatus.UnsupportedAuth);
                    }

                    string? value = reader.GetString();
                    if (hasAuthMode && !string.Equals(authMode, value, StringComparison.Ordinal)) {
                        return Failure(CodexLocalAuthReadStatus.UnsupportedAuth);
                    }

                    hasAuthMode = true;
                    authMode = value;
                    continue;
                }

                if (valueDepth == 1 && string.Equals(propertyName, "tokens", StringComparison.OrdinalIgnoreCase)) {
                    if (reader.TokenType != JsonTokenType.StartObject) {
                        return Failure(CodexLocalAuthReadStatus.UnsupportedAuth);
                    }

                    continue;
                }

                if (reader.TokenType == JsonTokenType.String) {
                    if (isAuthField && IsAccountIdClaim(propertyName)) {
                        AddClaim(accountIds, reader.GetString());
                    } else if (isAuthField && IsEmailClaim(propertyName)) {
                        AddClaim(emails, reader.GetString());
                    } else if (valueDepth == 2
                        && string.Equals(propertyNames[1], "tokens", StringComparison.OrdinalIgnoreCase)
                        && IsJwtTokenProperty(propertyName)) {
                        if (reader.ValueIsEscaped) {
                            return Failure(CodexLocalAuthReadStatus.InvalidFormat);
                        }

                        CodexLocalAuthReadStatus? tokenStatus = ReadJwtClaims(
                            reader.ValueSpan,
                            accountIds,
                            emails
                        );
                        if (tokenStatus is CodexLocalAuthReadStatus status) {
                            return Failure(status);
                        }
                    }
                } else if (isAuthField && (IsAccountIdClaim(propertyName) || IsEmailClaim(propertyName))) {
                    if (reader.TokenType != JsonTokenType.Null) {
                        return Failure(CodexLocalAuthReadStatus.InvalidFormat);
                    }
                }
            }

            if (!hasAuthMode || !string.Equals(authMode, "chatgpt", StringComparison.OrdinalIgnoreCase)) {
                return Failure(CodexLocalAuthReadStatus.UnsupportedAuth);
            }

            if (HasConflictingValues(accountIds) || HasConflictingValues(emails, StringComparison.OrdinalIgnoreCase)) {
                return Failure(CodexLocalAuthReadStatus.ConflictingClaims);
            }

            string? accountId = accountIds.FirstOrDefault();
            string? email = emails.FirstOrDefault();
            if (accountId is null && email is null) {
                return Failure(CodexLocalAuthReadStatus.IdentityUnavailable);
            }

            return new CodexLocalAuthReadResult(
                CodexLocalAuthReadStatus.Success,
                new CodexLocalAuthIdentity(accountId, email)
            );
        } catch (JsonException) {
            return Failure(CodexLocalAuthReadStatus.InvalidFormat);
        } catch (DecoderFallbackException) {
            return Failure(CodexLocalAuthReadStatus.InvalidFormat);
        }
    }

    private static CodexLocalAuthReadStatus? ReadJwtClaims(
        ReadOnlySpan<byte> token,
        ICollection<string> accountIds,
        ICollection<string> emails
    ) {
        int firstSeparator = token.IndexOf((byte)'.');
        if (firstSeparator <= 0 || firstSeparator == token.Length - 1) {
            return CodexLocalAuthReadStatus.InvalidFormat;
        }

        ReadOnlySpan<byte> remainder = token[(firstSeparator + 1)..];
        int secondSeparator = remainder.IndexOf((byte)'.');
        if (secondSeparator <= 0 || secondSeparator == remainder.Length - 1
            || remainder[(secondSeparator + 1)..].IndexOf((byte)'.') >= 0) {
            return CodexLocalAuthReadStatus.InvalidFormat;
        }

        byte[]? encodedPayload = null;
        byte[]? payload = null;
        try {
            encodedPayload = EncodeBase64UrlSegment(remainder[..secondSeparator]);
            int maximumPayloadLength = checked((encodedPayload.Length / 4) * 3);
            if (maximumPayloadLength > MaximumJwtPayloadBytes) {
                return CodexLocalAuthReadStatus.FileTooLarge;
            }

            payload = new byte[maximumPayloadLength];
            OperationStatus decodeStatus = Base64.DecodeFromUtf8(
                encodedPayload,
                payload,
                out int consumed,
                out int written
            );
            if (decodeStatus != OperationStatus.Done || consumed != encodedPayload.Length) {
                return CodexLocalAuthReadStatus.InvalidFormat;
            }

            return ReadJwtPayload(payload.AsSpan(0, written), accountIds, emails);
        } catch (JsonException) {
            return CodexLocalAuthReadStatus.InvalidFormat;
        } catch (FormatException) {
            return CodexLocalAuthReadStatus.InvalidFormat;
        } catch (OverflowException) {
            return CodexLocalAuthReadStatus.InvalidFormat;
        } finally {
            if (encodedPayload is not null) {
                CryptographicOperations.ZeroMemory(encodedPayload);
            }

            if (payload is not null) {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    private static byte[] EncodeBase64UrlSegment(ReadOnlySpan<byte> segment) {
        int remainder = segment.Length % 4;
        if (remainder == 1) {
            throw new FormatException();
        }

        int paddingLength = (4 - remainder) % 4;
        byte[] encoded = new byte[segment.Length + paddingLength];
        for (int index = 0; index < segment.Length; index++) {
            byte value = segment[index];
            encoded[index] = value switch {
                >= (byte)'A' and <= (byte)'Z' => value,
                >= (byte)'a' and <= (byte)'z' => value,
                >= (byte)'0' and <= (byte)'9' => value,
                (byte)'-' => (byte)'+',
                (byte)'_' => (byte)'/',
                _ => 0
            };
            if (encoded[index] == 0) {
                CryptographicOperations.ZeroMemory(encoded);
                throw new FormatException();
            }
        }

        for (int index = segment.Length; index < encoded.Length; index++) {
            encoded[index] = (byte)'=';
        }

        return encoded;
    }

    private static CodexLocalAuthReadStatus? ReadJwtPayload(
        ReadOnlySpan<byte> payload,
        ICollection<string> accountIds,
        ICollection<string> emails
    ) {
        Utf8JsonReader reader = new(payload, new JsonReaderOptions { MaxDepth = 32 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) {
            return CodexLocalAuthReadStatus.InvalidFormat;
        }

        string?[] propertyNames = new string?[34];
        while (reader.Read()) {
            if (reader.TokenType == JsonTokenType.PropertyName) {
                int depth = reader.CurrentDepth;
                if (depth >= propertyNames.Length) {
                    return CodexLocalAuthReadStatus.InvalidFormat;
                }

                propertyNames[depth] = reader.GetString();
                continue;
            }

            if (reader.TokenType != JsonTokenType.String) {
                continue;
            }

            int valueDepth = reader.CurrentDepth;
            string? propertyName = valueDepth < propertyNames.Length ? propertyNames[valueDepth] : null;
            string? parentName = valueDepth > 1 ? propertyNames[valueDepth - 1] : null;
            if (IsAccountIdClaim(propertyName)
                || (string.Equals(parentName, "https://api.openai.com/auth", StringComparison.Ordinal)
                    && IsAccountIdClaim(propertyName))) {
                AddClaim(accountIds, reader.GetString());
            } else if (IsEmailClaim(propertyName)
                && (valueDepth == 1
                    || string.Equals(parentName, "https://api.openai.com/profile", StringComparison.Ordinal))) {
                AddClaim(emails, reader.GetString());
            }
        }

        return null;
    }

    private static bool IsAccountIdClaim(string? propertyName) {
        return propertyName is "account_id" or "accountId" or "chatgpt_account_id" or "chatgptAccountId";
    }

    private static bool IsEmailClaim(string? propertyName) {
        return string.Equals(propertyName, "email", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJwtTokenProperty(string? propertyName) {
        return propertyName is "access_token" or "id_token";
    }

    private static bool IsSharingViolation(Exception exception) {
        int errorCode = exception.HResult & 0xFFFF;
        return errorCode is SharingViolation or LockViolation;
    }

    private static void AddClaim(ICollection<string> claims, string? value) {
        if (!string.IsNullOrWhiteSpace(value)) {
            claims.Add(value.Trim());
        }
    }

    private static bool HasConflictingValues(
        IEnumerable<string> values,
        StringComparison comparison = StringComparison.Ordinal
    ) {
        string? expected = null;
        foreach (string value in values) {
            if (expected is null) {
                expected = value;
            } else if (!string.Equals(expected, value, comparison)) {
                return true;
            }
        }

        return false;
    }

    private static string ToExtendedPath(string path) {
        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal)) {
            return fullPath;
        }

        return fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + fullPath[2..]
            : "\\\\?\\" + fullPath;
    }

    private static string NormalizeFinalPath(string path) {
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) {
            return "\\\\" + path[8..];
        }

        return path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
    }

    private static bool PathsEqual(string first, string second) {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase
        );
    }

    private static ReaderFailure CreateFailure(int errorCode) {
        CodexLocalAuthReadStatus status = errorCode switch {
            FileNotFound or PathNotFound => CodexLocalAuthReadStatus.FileMissing,
            AccessDenied => CodexLocalAuthReadStatus.AccessDenied,
            SharingViolation or LockViolation => CodexLocalAuthReadStatus.SharingViolation,
            _ => CodexLocalAuthReadStatus.Unavailable
        };
        return new ReaderFailure(status, isRetryable: status == CodexLocalAuthReadStatus.SharingViolation);
    }

    private static CodexLocalAuthReadStatus MapFailure(Exception exception) {
        if (exception is UnauthorizedAccessException or SecurityException) {
            return CodexLocalAuthReadStatus.AccessDenied;
        }

        if (exception is FileNotFoundException or DirectoryNotFoundException) {
            return CodexLocalAuthReadStatus.FileMissing;
        }

        int errorCode = exception.HResult & 0xFFFF;
        return errorCode switch {
            FileNotFound or PathNotFound => CodexLocalAuthReadStatus.FileMissing,
            AccessDenied => CodexLocalAuthReadStatus.AccessDenied,
            SharingViolation or LockViolation => CodexLocalAuthReadStatus.SharingViolation,
            _ => CodexLocalAuthReadStatus.Unavailable
        };
    }

    private static CodexLocalAuthReadResult Failure(CodexLocalAuthReadStatus status) {
        return new CodexLocalAuthReadResult(status, null);
    }

    private sealed class ReaderFailure : Exception {
        public ReaderFailure(CodexLocalAuthReadStatus status, bool isRetryable = false)
            : base("本機登入資料讀取失敗。") {
            Status = status;
            IsRetryable = isRetryable;
        }

        public CodexLocalAuthReadStatus Status { get; }

        public bool IsRetryable { get; }
    }

    private readonly record struct FileIdentity(
        uint VolumeSerialNumber,
        uint FileIndexHigh,
        uint FileIndexLow,
        uint FileSizeHigh,
        uint FileSizeLow,
        uint LastWriteTimeHigh,
        uint LastWriteTimeLow,
        uint Attributes,
        long Length
    ) {
        public bool IsSameFileAndVersion(FileIdentity other) {
            return VolumeSerialNumber == other.VolumeSerialNumber
                && FileIndexHigh == other.FileIndexHigh
                && FileIndexLow == other.FileIndexLow
                && FileSizeHigh == other.FileSizeHigh
                && FileSizeLow == other.FileSizeLow
                && LastWriteTimeHigh == other.LastWriteTimeHigh
                && LastWriteTimeLow == other.LastWriteTimeLow;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFileAttributesW", SetLastError = true)]
    private static extern uint GetFileAttributes(string fileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle fileHandle,
        StringBuilder filePath,
        uint filePathSize,
        uint flags
    );
}
