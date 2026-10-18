using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CloudyWing.AiAccountManager.Windows.Claude;

internal static class ClaudeWindowsPathSafety {
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeReparsePoint = 0x400;
    private const int FileIdInfoClass = 18;
    private const int FileAttributeTagInfoClass = 9;
    private const int FileStandardInfoClass = 1;

    internal static IdentityLease OpenIdentity(string path, bool directory) {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string volumeRoot = Path.GetPathRoot(normalized)
            ?? throw new InvalidDataException("A Claude data path has no volume root.");
        EnsureNoReparsePoints(volumeRoot, normalized);
        SafeFileHandle handle = CreateFile(
            normalized,
            FileReadAttributes,
            FileShareAll,
            nint.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | (directory ? FileFlagBackupSemantics : 0),
            nint.Zero
        );
        if (handle.IsInvalid) {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        try {
            IdentityLease lease = new(normalized, directory, handle);
            lease.VerifyHandle();
            return lease;
        } catch {
            handle.Dispose();
            throw;
        }
    }

    internal static string ResolveContainedPath(string rootPath, string relativePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath)) {
            throw new InvalidDataException("A Claude data path must be relative to its verified root.");
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsureContained(root, fullPath);
        return fullPath;
    }

    internal static void EnsureContained(string rootPath, string targetPath) {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string target = Path.GetFullPath(targetPath);
        string relative = Path.GetRelativePath(root, target);
        if (Path.IsPathRooted(relative)
            || string.Equals(relative, "..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)) {
            throw new InvalidDataException("A Claude data path escaped its verified root.");
        }
    }

    internal static void EnsureNoReparsePoints(string rootPath, string targetPath) {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string target = Path.GetFullPath(targetPath);
        EnsureContained(root, target);
        CheckReparsePoint(root);

        string relative = Path.GetRelativePath(root, target);
        if (string.Equals(relative, ".", StringComparison.Ordinal)) {
            return;
        }

        string current = root;
        foreach (string segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries
        )) {
            if (string.Equals(segment, ".", StringComparison.Ordinal)
                || string.Equals(segment, "..", StringComparison.Ordinal)) {
                throw new InvalidDataException("A Claude data path contains an invalid segment.");
            }

            current = Path.Combine(current, segment);
            CheckReparsePoint(current);
        }
    }

    internal static byte[] ReadBoundedFile(string path, int maximumBytes) {
        if (maximumBytes < 0) {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan
        );
        if (stream.Length > maximumBytes) {
            throw new InvalidDataException("A Claude data file exceeded its supported size limit.");
        }

        byte[] content = new byte[checked((int)stream.Length)];
        int offset = 0;
        while (offset < content.Length) {
            int bytesRead = stream.Read(content, offset, content.Length - offset);
            if (bytesRead == 0) {
                CryptographicOperations.ZeroMemory(content);
                throw new EndOfStreamException("A Claude data file changed while it was being read.");
            }

            offset += bytesRead;
        }

        if (stream.ReadByte() != -1) {
            CryptographicOperations.ZeroMemory(content);
            throw new InvalidDataException("A Claude data file changed while it was being read.");
        }

        return content;
    }

    internal static void WriteFileAtomically(string path, byte[] content) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("A Claude data file has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        bool temporaryCreated = false;
        try {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough
            )) {
                temporaryCreated = true;
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path)) {
                File.Replace(temporaryPath, path, null, ignoreMetadataErrors: true);
            } else {
                File.Move(temporaryPath, path);
            }

            temporaryCreated = false;
        } finally {
            if (temporaryCreated && File.Exists(temporaryPath)) {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static IReadOnlyList<string> EnumerateFilesSafely(string rootPath) {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        EnsureNoReparsePoints(root, root);
        if (!Directory.Exists(root)) {
            throw new DirectoryNotFoundException("A Claude data directory is missing.");
        }

        List<string> files = [];
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0) {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) {
                CheckReparsePoint(entry);
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0) {
                    pending.Push(entry);
                } else {
                    files.Add(entry);
                }
            }
        }

        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static void CopyDirectoryContents(string sourcePath, string destinationPath) {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationPath));
        if (IsSameOrDescendant(source, destination) || IsSameOrDescendant(destination, source)) {
            throw new InvalidDataException("Claude directory copy paths must be separate trees.");
        }

        EnsureNoReparsePoints(source, source);
        if (!Directory.Exists(source)) {
            throw new DirectoryNotFoundException("A Claude source directory is missing.");
        }

        if (Directory.Exists(destination) || File.Exists(destination)) {
            throw new IOException("A Claude staging destination already exists.");
        }

        bool destinationCreated = false;
        try {
            Directory.CreateDirectory(destination);
            destinationCreated = true;
            Stack<(string Source, string Destination)> pending = new();
            pending.Push((source, destination));
            long copiedBytes = 0;
            byte[] buffer = new byte[8192];
            while (pending.Count > 0) {
                (string currentSource, string currentDestination) = pending.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(currentSource)) {
                    CheckReparsePoint(entry);
                    string target = Path.Combine(currentDestination, Path.GetFileName(entry));
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.Directory) != 0) {
                        Directory.CreateDirectory(target);
                        pending.Push((entry, target));
                    } else {
                        using FileStream input = new(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using FileStream output = new(
                            target,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            4096,
                            FileOptions.WriteThrough
                        );
                        int bytesRead;
                        while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0) {
                            copiedBytes = checked(copiedBytes + bytesRead);
                            if (copiedBytes > 1024L * 1024 * 1024) {
                                throw new InvalidDataException("A Claude history copy exceeded its supported size limit.");
                            }

                            output.Write(buffer, 0, bytesRead);
                        }

                        output.Flush(flushToDisk: true);
                    }
                }
            }
        } catch {
            if (destinationCreated && Directory.Exists(destination)) {
                Directory.Delete(destination, recursive: true);
            }

            throw;
        }
    }

    internal static void DeleteDirectoryContents(string rootPath, string directoryPath) {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        EnsureNoReparsePoints(root, directory);
        if (string.Equals(root, directory, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException("A Claude transaction cleanup cannot remove its containing root.");
        }

        if (Directory.Exists(directory)) {
            _ = EnumerateFilesSafely(directory);
            Directory.Delete(directory, recursive: true);
        } else if (File.Exists(directory)) {
            throw new IOException("A Claude transaction directory was replaced by a file.");
        }
    }

    internal static string ComputeSha256(ReadOnlySpan<byte> content) {
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    internal static string ComputePathFingerprint(string path) {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
        return ComputeSha256(System.Text.Encoding.UTF8.GetBytes(normalized));
    }

    private static bool IsSameOrDescendant(string rootPath, string targetPath) {
        string relative = Path.GetRelativePath(rootPath, targetPath);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void CheckReparsePoint(string path) {
        try {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) {
                throw new InvalidDataException("A Claude data path contains a reparse point.");
            }
        } catch (FileNotFoundException) {
        } catch (DirectoryNotFoundException) {
        }
    }

    internal readonly record struct FileIdentity(ulong VolumeSerialNumber, ulong FileIdLow, ulong FileIdHigh) {
        internal string FileId => $"{FileIdHigh:x16}{FileIdLow:x16}";
    }

    internal sealed class IdentityLease : IDisposable {
        private readonly string path;
        private readonly bool directory;
        private readonly SafeFileHandle handle;
        private readonly FileIdentity identity;

        internal IdentityLease(string path, bool directory, SafeFileHandle handle) {
            this.path = path;
            this.directory = directory;
            this.handle = handle;
            identity = ReadIdentity(handle, directory);
        }

        internal FileIdentity Identity => identity;

        internal void VerifyHandle() {
            if (ReadIdentity(handle, directory) != identity) {
                throw new IOException("A Claude data entry identity changed.");
            }

            StringBuilder finalPath = new(32768);
            uint length = GetFinalPathNameByHandle(handle, finalPath, checked((uint)finalPath.Capacity), 0);
            if (length == 0 || length >= finalPath.Capacity) {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            string actualPath = finalPath.ToString();
            if (actualPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) {
                actualPath = @"\\" + actualPath[8..];
            } else if (actualPath.StartsWith(@"\\?\", StringComparison.Ordinal)) {
                actualPath = actualPath[4..];
            }

            if (!string.Equals(Path.TrimEndingDirectorySeparator(actualPath), path, StringComparison.OrdinalIgnoreCase)) {
                throw new IOException("A Claude data entry resolved to a different path.");
            }
        }

        internal void VerifyPath() {
            VerifyHandle();
            using IdentityLease current = OpenIdentity(path, directory);
            if (current.Identity != identity) {
                throw new IOException("A Claude data entry was replaced.");
            }
        }

        public void Dispose() {
            handle.Dispose();
        }
    }

    private static FileIdentity ReadIdentity(SafeFileHandle handle, bool directory) {
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, out FileIdInfo id, (uint)Marshal.SizeOf<FileIdInfo>())
            || !GetFileInformationByHandleEx(
                handle, FileAttributeTagInfoClass, out FileAttributeTagInfo attributes,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()
            )
            || !GetFileInformationByHandleEx(
                handle, FileStandardInfoClass, out FileStandardInfo standard, (uint)Marshal.SizeOf<FileStandardInfo>()
            )) {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if ((attributes.FileAttributes & FileAttributeReparsePoint) != 0
            || (standard.Directory != 0) != directory
            || standard.DeletePending != 0
            || standard.NumberOfLinks == 0
            || (!directory && standard.NumberOfLinks != 1)) {
            throw new InvalidDataException("A Claude data entry is not a safe ordinary file or directory.");
        }

        return new FileIdentity(id.VolumeSerialNumber, id.FileIdLow, id.FileIdHigh);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string path, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile
    );

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass, out FileIdInfo information, uint size
    );

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass, out FileAttributeTagInfo information, uint size
    );

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass, out FileStandardInfo information, uint size
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }
}
