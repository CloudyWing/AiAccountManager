using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CloudyWing.AiAccountManager.Windows.Claude;

internal sealed class ClaudeDesktopSnapshotFileSystem {
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private readonly Action<Operation, string>? observer;

    internal ClaudeDesktopSnapshotFileSystem(Action<Operation, string>? observer = null) {
        this.observer = observer;
    }

    internal enum Operation {
        BeforePreflight,
        AfterPreflight,
        BeforeLock,
        HandleLocked,
        AllHandlesLocked,
        BeforeRead,
        AfterRead,
        BeforeValidation,
        BeforeRestoreMutation,
        AfterRestoreMutation,
        BeforeRollbackMutation,
        AfterRollbackMutation
    }

    internal readonly record struct FileMetadata(
        uint VolumeSerialNumber,
        uint FileIndexHigh,
        uint FileIndexLow,
        uint FileSizeHigh,
        uint FileSizeLow,
        uint CreationTimeHigh,
        uint CreationTimeLow,
        uint LastWriteTimeHigh,
        uint LastWriteTimeLow,
        uint Attributes
    ) {
        internal long Length => ((long)FileSizeHigh << 32) | FileSizeLow;

        internal bool IsSameFileAndVersion(FileMetadata other) {
            return VolumeSerialNumber == other.VolumeSerialNumber
                && FileIndexHigh == other.FileIndexHigh
                && FileIndexLow == other.FileIndexLow
                && FileSizeHigh == other.FileSizeHigh
                && FileSizeLow == other.FileSizeLow
                && CreationTimeHigh == other.CreationTimeHigh
                && CreationTimeLow == other.CreationTimeLow
                && LastWriteTimeHigh == other.LastWriteTimeHigh
                && LastWriteTimeLow == other.LastWriteTimeLow
                && Attributes == other.Attributes;
        }
    }

    internal sealed class SnapshotFile : IDisposable {
        private readonly ClaudeDesktopSnapshotFileSystem owner;
        private readonly string rootPath;
        private readonly FileStream stream;

        internal SnapshotFile(
            ClaudeDesktopSnapshotFileSystem owner,
            string rootPath,
            string path,
            FileMetadata metadata,
            FileStream stream
        ) {
            this.owner = owner;
            this.rootPath = rootPath;
            Path = path;
            Metadata = metadata;
            this.stream = stream;
        }

        internal string Path { get; }

        internal FileMetadata Metadata { get; }

        internal async Task<byte[]> ReadBoundedAsync(int maximumBytes, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            if (Metadata.Length < 0 || Metadata.Length > maximumBytes || Metadata.Length > int.MaxValue) {
                throw new InvalidDataException("A Claude Desktop profile file exceeded its supported size limit.");
            }

            EnsureHandleMatchesPath(rootPath, Path, stream.SafeFileHandle, Metadata);
            byte[] content = new byte[checked((int)Metadata.Length)];
            byte[] extraByte = new byte[1];
            try {
                stream.Position = 0;
                int offset = 0;
                while (offset < content.Length) {
                    int bytesRead = await stream.ReadAsync(
                        content.AsMemory(offset, content.Length - offset),
                        cancellationToken
                    ).ConfigureAwait(false);
                    if (bytesRead == 0) {
                        throw new EndOfStreamException("A Claude Desktop profile file changed while it was being read.");
                    }

                    offset += bytesRead;
                }

                if (await stream.ReadAsync(extraByte.AsMemory(), cancellationToken).ConfigureAwait(false) != 0) {
                    throw new InvalidDataException("A Claude Desktop profile file changed while it was being read.");
                }

                FileMetadata afterRead = GetFileMetadata(stream.SafeFileHandle);
                if (!Metadata.IsSameFileAndVersion(afterRead)) {
                    throw new IOException("A Claude Desktop profile file changed while it was being read.");
                }

                return content;
            } catch {
                CryptographicOperations.ZeroMemory(content);
                throw;
            } finally {
                CryptographicOperations.ZeroMemory(extraByte);
            }
        }

        internal bool IsStable() {
            ClaudeWindowsPathSafety.EnsureNoReparsePoints(rootPath, Path);
            EnsureHandleMatchesPath(rootPath, Path, stream.SafeFileHandle, Metadata);
            FileMetadata currentPathMetadata = owner.CaptureMetadata(rootPath, Path);
            return Metadata.IsSameFileAndVersion(currentPathMetadata);
        }

        public void Dispose() {
            stream.Dispose();
        }
    }

    internal void Observe(Operation operation, string path) {
        observer?.Invoke(operation, path);
    }

    internal FileMetadata CaptureMetadata(string rootPath, string path) {
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(rootPath, path);
        using SafeFileHandle handle = OpenReadHandle(path, FileShareRead | FileShareWrite | FileShareDelete, asynchronous: false);
        return EnsureHandleMatchesPath(rootPath, path, handle);
    }

    internal SnapshotFile OpenLockedFile(string rootPath, string path, FileMetadata expectedMetadata) {
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(rootPath, path);
        SafeFileHandle handle = OpenReadHandle(path, FileShareRead, asynchronous: true);
        try {
            FileMetadata metadata = EnsureHandleMatchesPath(rootPath, path, handle);
            if (!expectedMetadata.IsSameFileAndVersion(metadata)) {
                throw new IOException("A Claude Desktop profile file was replaced before it could be locked.");
            }

            FileStream stream = new(handle, FileAccess.Read, 4096, isAsync: true);
            handle = null!;
            return new SnapshotFile(this, rootPath, path, metadata, stream);
        } finally {
            handle?.Dispose();
        }
    }

    internal async Task<byte[]> ReadSharedBoundedAsync(
        string rootPath,
        string path,
        int maximumBytes,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(rootPath, path);
        SafeFileHandle handle = OpenReadHandle(path, FileShareRead | FileShareWrite | FileShareDelete, asynchronous: true);
        FileStream stream;
        try {
            stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: true);
            handle = null!;
        } finally {
            handle?.Dispose();
        }

        using (stream) {
            FileMetadata before = EnsureHandleMatchesPath(rootPath, path, stream.SafeFileHandle);
            if (before.Length < 0 || before.Length > maximumBytes || before.Length > int.MaxValue) {
                throw new InvalidDataException("A Claude Desktop profile file exceeded its supported size limit.");
            }

            byte[] content = new byte[checked((int)before.Length)];
            byte[] extraByte = new byte[1];
            try {
                int offset = 0;
                while (offset < content.Length) {
                    int bytesRead = await stream.ReadAsync(
                        content.AsMemory(offset, content.Length - offset),
                        cancellationToken
                    ).ConfigureAwait(false);
                    if (bytesRead == 0) {
                        throw new EndOfStreamException("A Claude Desktop profile file changed while it was being read.");
                    }

                    offset += bytesRead;
                }

                if (await stream.ReadAsync(extraByte.AsMemory(), cancellationToken).ConfigureAwait(false) != 0) {
                    throw new InvalidDataException("A Claude Desktop profile file changed while it was being read.");
                }

                FileMetadata after = GetFileMetadata(stream.SafeFileHandle);
                FileMetadata pathMetadata = CaptureMetadata(rootPath, path);
                if (!before.IsSameFileAndVersion(after) || !before.IsSameFileAndVersion(pathMetadata)) {
                    throw new IOException("A Claude Desktop profile file changed while it was being read.");
                }

                return content;
            } catch {
                CryptographicOperations.ZeroMemory(content);
                throw;
            } finally {
                CryptographicOperations.ZeroMemory(extraByte);
            }
        }
    }

    private static FileMetadata EnsureHandleMatchesPath(
        string rootPath,
        string path,
        SafeFileHandle handle,
        FileMetadata? expectedMetadata = null
    ) {
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(rootPath, path);
        FileMetadata metadata = GetFileMetadata(handle);
        string finalPath = GetFinalPath(handle);
        if (!PathsEqual(finalPath, path)) {
            throw new InvalidDataException("A Claude Desktop profile handle resolved outside its verified path.");
        }

        if (expectedMetadata is not null && !expectedMetadata.Value.IsSameFileAndVersion(metadata)) {
            throw new IOException("A Claude Desktop profile handle changed identity or metadata.");
        }

        return metadata;
    }

    private static FileMetadata GetFileMetadata(SafeFileHandle handle) {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information)) {
            throw CreateFailure(Marshal.GetLastWin32Error(), "Could not inspect a Claude Desktop profile file.");
        }

        if ((information.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0) {
            throw new InvalidDataException("A Claude Desktop profile path is not a regular file.");
        }

        return new FileMetadata(
            information.VolumeSerialNumber,
            information.FileIndexHigh,
            information.FileIndexLow,
            information.FileSizeHigh,
            information.FileSizeLow,
            information.CreationTimeHigh,
            information.CreationTimeLow,
            information.LastWriteTimeHigh,
            information.LastWriteTimeLow,
            information.FileAttributes
        );
    }

    private static SafeFileHandle OpenReadHandle(string path, uint shareMode, bool asynchronous) {
        uint flags = FileFlagOpenReparsePoint | FileFlagSequentialScan;
        if (asynchronous) {
            flags |= FileFlagOverlapped;
        }

        SafeFileHandle handle = CreateFile(
            ToExtendedPath(path),
            GenericRead,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero
        );
        if (!handle.IsInvalid) {
            return handle;
        }

        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw CreateFailure(error, "Could not open a Claude Desktop profile file.");
    }

    private static string GetFinalPath(SafeFileHandle handle) {
        StringBuilder buffer = new(32768);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) {
            throw CreateFailure(Marshal.GetLastWin32Error(), "Could not resolve a Claude Desktop profile file path.");
        }

        return NormalizeFinalPath(buffer.ToString());
    }

    private static string NormalizeFinalPath(string path) {
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) {
            return "\\\\" + path[8..];
        }

        return path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
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

    private static bool PathsEqual(string actualPath, string expectedPath) {
        string fullExpectedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath));
        return string.Equals(actualPath, fullExpectedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static Exception CreateFailure(int error, string message) {
        Win32Exception inner = new(error);
        return error switch {
            2 => new FileNotFoundException(message, inner),
            3 => new DirectoryNotFoundException(message, inner),
            5 => new UnauthorizedAccessException(message, inner),
            _ => new IOException(message, inner)
        };
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
