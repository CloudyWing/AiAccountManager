using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Accounts;
using Microsoft.Win32.SafeHandles;

namespace CloudyWing.AiAccountManager.Windows.Claude;

internal static class WindowsClaudeHistoryCleanupFileSystem {
    private const uint DeleteAccess = 0x00010000;
    private const uint FileListDirectoryAccess = 0x00000001;
    private const uint FileReadDataAccess = 0x00000001;
    private const uint FileReadAttributesAccess = 0x00000080;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int FileDispositionInfoClass = 4;
    private const int FileAttributeTagInfoClass = 9;
    private const int FileStandardInfoClass = 1;
    private const int FileIdInfoClass = 18;
    private const uint FinalPathBufferLength = 32768;
    private const uint VolumeNameDos = 0;

    internal readonly record struct FileIdentity(ulong VolumeSerialNumber, ulong FileIdLow, ulong FileIdHigh);

    internal readonly record struct FileSnapshot(FileIdentity Identity, long Length, string Sha256);

    internal sealed record FileChange(string Path, byte[]? Content);

    internal sealed class DurableFileTransaction {
        private const string ManifestName = "file-transaction.json";
        private readonly string root;
        private readonly string directory;
        private readonly Guid transactionId;
        private readonly ClaudeDesktopRootBinding binding;
        private readonly Action<string, string>? observer;
        private readonly Func<CancellationToken, Task> guard;
        private readonly Action? cleanupLiveStateGuard;

        internal DurableFileTransaction(
            string root,
            string directory,
            Guid transactionId,
            ClaudeDesktopRootBinding binding,
            Func<CancellationToken, Task> guard,
            Action<string, string>? observer = null,
            Action? cleanupLiveStateGuard = null
        ) {
            this.root = NormalizePath(root);
            this.directory = NormalizePath(directory);
            this.transactionId = transactionId;
            this.binding = binding;
            this.guard = guard;
            this.observer = observer;
            this.cleanupLiveStateGuard = cleanupLiveStateGuard;
            ClaudeWindowsPathSafety.EnsureContained(this.root, this.directory);
            if (transactionId == Guid.Empty || binding.Source.SchemaVersion != 1 || binding.Source.BindingId == Guid.Empty) {
                throw new InvalidDataException("Claude file transaction provenance is incomplete.");
            }

            FileIdentity identity = CaptureEntryIdentity(this.root, this.root, true);
            if (!string.Equals(this.root, NormalizePath(binding.Source.DataRoot), StringComparison.OrdinalIgnoreCase)
                || identity.VolumeSerialNumber != binding.Source.RootVolumeSerialNumber
                || !string.Equals($"{identity.FileIdHigh:x16}{identity.FileIdLow:x16}", binding.Source.RootFileId, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException("Claude transaction root no longer matches its trusted binding.");
            }
        }

        internal void Prepare(IReadOnlyList<FileChange> changes, CancellationToken cancellationToken) {
            if (Directory.Exists(directory)) {
                TransactionManifest existing = Load();
                ValidateFiles(existing, requirePrepared: true);
                return;
            }

            ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, directory);
            List<Entry> entries = [];
            List<byte[]?> beforeBytes = [];
            try {
                foreach (FileChange change in changes.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)) {
                    cancellationToken.ThrowIfCancellationRequested();
                    string path = NormalizePath(change.Path);
                    ClaudeWindowsPathSafety.EnsureContained(root, path);
                    string parent = Path.GetDirectoryName(path)!;
                    FileSnapshot? before = null;
                    byte[]? bytes = null;
                    if (File.Exists(path)) {
                        using FileContentsSnapshot captured = ReadFileContentsSnapshot(root, path, 128 * 1024 * 1024);
                        before = captured.Snapshot;
                        bytes = captured.Content.ToArray();
                    } else if (Directory.Exists(path)) {
                        throw new IOException("A Claude transaction target is not a regular file.");
                    }

                    string name = entries.Count.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
                    string anchor = parent;
                    while (!Directory.Exists(anchor)) {
                        anchor = Path.GetDirectoryName(anchor) ?? throw new IOException("Claude target parent is unavailable.");
                        ClaudeWindowsPathSafety.EnsureContained(root, anchor);
                    }

                    entries.Add(new Entry(
                        Path.GetRelativePath(root, path),
                        Directory.Exists(parent) ? CaptureEntryIdentity(root, parent, expectDirectory: true) : null,
                        Path.GetRelativePath(root, anchor),
                        CaptureEntryIdentity(root, anchor, expectDirectory: true),
                        before,
                        change.Content is null ? null : new ContentDigest(change.Content.LongLength, Convert.ToHexString(SHA256.HashData(change.Content))),
                        name,
                        null,
                        null,
                        null,
                        RenameIntent.None
                    ));
                    beforeBytes.Add(bytes);
                }

                if (!CreateDirectoryW(directory, IntPtr.Zero)) {
                    ThrowWin32IOException("Claude file transaction directory could not be exclusively created.");
                }
                TransactionManifest manifest = new(1, transactionId, binding, CaptureEntryIdentity(root, root, true),
                    CaptureEntryIdentity(root, directory, true), entries, false);
                Save(manifest);
                observer?.Invoke("intent-published", directory);
                for (int index = 0; index < entries.Count; index++) {
                    cancellationToken.ThrowIfCancellationRequested();
                    Entry entry = entries[index];
                    if (beforeBytes[index] is byte[] original) {
                        WritePayload(Payload(entry, "backup"), original);
                        entry = entry with { Backup = CaptureFileSnapshot(root, Payload(entry, "backup"), 128 * 1024 * 1024) };
                    }

                    FileChange change = changes.Single(item => string.Equals(NormalizePath(item.Path), Live(entry), StringComparison.OrdinalIgnoreCase));
                    if (change.Content is byte[] replacement) {
                        WritePayload(Payload(entry, "apply"), replacement);
                        entry = entry with { Apply = CaptureFileSnapshot(root, Payload(entry, "apply"), 128 * 1024 * 1024) };
                    }

                    entries[index] = entry;
                    manifest = manifest with { Entries = entries.ToArray() };
                    Save(manifest);
                    observer?.Invoke("payload-written", directory);
                }

                manifest = manifest with { Prepared = true };
                Save(manifest);
                ValidateFiles(manifest, requirePrepared: true);
            } finally {
                foreach (byte[]? bytes in beforeBytes) {
                    if (bytes is not null) {
                        CryptographicOperations.ZeroMemory(bytes);
                    }
                }
            }
        }

        internal async Task ApplyAsync(CancellationToken cancellationToken) {
            TransactionManifest manifest = Load();
            ValidateFiles(manifest, requirePrepared: true);
            manifest = await EnsureParentsAsync(manifest, cancellationToken).ConfigureAwait(false);
            for (int index = 0; index < manifest.Entries.Count; index++) {
                Entry entry = manifest.Entries[index];
                if (entry.Intent is RenameIntent.RestoreDisplace or RenameIntent.RestorePublish or RenameIntent.Restored) {
                    throw new IOException("A Claude file transaction has already entered rollback.");
                }

                if (entry.Before is FileSnapshot before && Matches(Live(entry), before)) {
                    manifest = Update(manifest, index, entry with { Intent = RenameIntent.ApplyDisplace });
                    entry = manifest.Entries[index];
                    await RenameAsync(manifest, Live(entry), Payload(entry, "displaced"), before, "apply-displace", cancellationToken)
                        .ConfigureAwait(false);
                }

                if (entry.Apply is FileSnapshot after && !Matches(Live(entry), after)) {
                    if (Exists(Live(entry))) {
                        throw new IOException("A Claude transaction live file was externally replaced.");
                    }

                    manifest = Update(manifest, index, entry with { Intent = RenameIntent.ApplyPublish });
                    entry = manifest.Entries[index];
                    await RenameAsync(manifest, Payload(entry, "apply"), Live(entry), after, "apply-publish", cancellationToken)
                        .ConfigureAwait(false);
                }

                manifest = Update(manifest, index, entry with { Intent = RenameIntent.Applied });
            }
        }

        internal async Task RestoreAsync(CancellationToken cancellationToken) {
            if (!Directory.Exists(directory)) {
                throw new IOException("Claude rollback evidence is missing.");
            }

            TransactionManifest manifest = Load();
            ValidateFiles(manifest, requirePrepared: true);
            for (int index = manifest.Entries.Count - 1; index >= 0; index--) {
                Entry entry = manifest.Entries[index];
                if (IsBefore(entry)) {
                    manifest = Update(manifest, index, entry with { Intent = RenameIntent.Restored });
                    continue;
                }

                if (entry.Before is FileSnapshot original && entry.Restore is null) {
                    using FileContentsSnapshot backup = ReadFileContentsSnapshot(root, Payload(entry, "backup"), 128 * 1024 * 1024);
                    if (backup.Snapshot != entry.Backup || !SameContent(backup.Snapshot, original)) {
                        throw new IOException("Claude rollback backup changed.");
                    }

                    string restorePath = Payload(entry, "restore");
                    if (!Exists(restorePath)) {
                        WritePayload(restorePath, backup.Content);
                    }

                    FileSnapshot restored = CaptureFileSnapshot(root, restorePath, 128 * 1024 * 1024);
                    if (!SameContent(restored, original)) {
                        throw new IOException("Claude rollback staging changed.");
                    }

                    entry = entry with { Restore = restored };
                    manifest = Update(manifest, index, entry);
                }

                if (entry.Apply is FileSnapshot after && Matches(Live(entry), after)) {
                    manifest = Update(manifest, index, entry with { Intent = RenameIntent.RestoreDisplace });
                    entry = manifest.Entries[index];
                    await RenameAsync(manifest, Live(entry), Payload(entry, "rollback-displaced"), after, "restore-displace", cancellationToken)
                        .ConfigureAwait(false);
                }

                if (entry.Restore is FileSnapshot staged && !Matches(Live(entry), staged)) {
                    if (Exists(Live(entry))) {
                        throw new IOException("A Claude rollback live file was externally created.");
                    }

                    manifest = Update(manifest, index, entry with { Intent = RenameIntent.RestorePublish });
                    entry = manifest.Entries[index];
                    await RenameAsync(manifest, Payload(entry, "restore"), Live(entry), staged, "restore-publish", cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!IsBefore(entry)) {
                    throw new IOException("Claude rollback could not establish the original state.");
                }

                manifest = Update(manifest, index, entry with { Intent = RenameIntent.Restored });
            }
        }

        internal FileIdentity? GetParentIdentity(string liveDirectoryPath) {
            string expectedPath = NormalizePath(liveDirectoryPath);
            Entry[] matchingEntries = Load().Entries
                .Where(entry => string.Equals(Path.GetDirectoryName(Live(entry)), expectedPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matchingEntries.Length == 0) {
                return null;
            }

            FileIdentity? parentIdentity = matchingEntries[0].ParentIdentity;
            if (parentIdentity is null || matchingEntries.Any(entry => entry.ParentIdentity != parentIdentity)) {
                throw new IOException("Claude rollback target directory identity is unavailable or inconsistent.");
            }

            return parentIdentity;
        }

        internal void Cleanup(CancellationToken cancellationToken, bool verifyLiveState = true) {
            if (!Directory.Exists(directory)) {
                return;
            }

            if (!Directory.EnumerateFileSystemEntries(directory).Any()) {
                DeleteDirectoryIfEmpty(root, directory, CaptureEntryIdentity(root, directory, true));
                return;
            }

            string manifestPath = Path.Combine(directory, ManifestName);
            if (!File.Exists(manifestPath)) {
                CleanupUnpublishedManifest(cancellationToken);
                return;
            }

            TransactionManifest manifest = Load();
            EnsureCleanupLiveState(manifest, verifyLiveState);
            Dictionary<string, FileSnapshot> inventory = new(StringComparer.OrdinalIgnoreCase);
            foreach (Entry entry in manifest.Entries) {
                AddPayload(inventory, entry, "backup", entry.Backup, entry.Before);
                AddPayload(inventory, entry, "apply", entry.Apply, entry.Apply);
                AddPayload(inventory, entry, "displaced", entry.Before, entry.Before);
                AddPayload(inventory, entry, "restore", entry.Restore, entry.Before);
                AddPayload(inventory, entry, "rollback-displaced", entry.Apply, entry.Apply);
            }

            FileSnapshot manifestSnapshot = CaptureFileSnapshot(root, manifestPath, 16 * 1024 * 1024);
            EnsureOwned(inventory.Keys.Append(manifestPath));
            foreach (KeyValuePair<string, FileSnapshot> item in inventory) {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(item.Key)) {
                    EnsureCleanupLiveState(manifest, verifyLiveState);
                    DeleteFileIfUnchanged(root, item.Key, item.Value, 128 * 1024 * 1024);
                    observer?.Invoke("cleanup-file", item.Key);
                    EnsureOwned(inventory.Keys.Append(manifestPath));
                    EnsureCleanupLiveState(manifest, verifyLiveState);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureCleanupLiveState(manifest, verifyLiveState);
            DeleteFileIfUnchanged(root, manifestPath, manifestSnapshot, 16 * 1024 * 1024);
            observer?.Invoke("cleanup-manifest", manifestPath);
            DeleteDirectoryIfEmpty(root, directory, manifest.DirectoryIdentity);
        }

        private void EnsureCleanupLiveState(TransactionManifest manifest, bool verifyLiveState) {
            if (verifyLiveState) {
                EnsureTerminalLiveState(manifest);
                cleanupLiveStateGuard?.Invoke();
            }
        }

        private void AddPayload(Dictionary<string, FileSnapshot> inventory, Entry entry, string role, FileSnapshot? expected, FileSnapshot? content) {
            string path = Payload(entry, role);
            if (!Exists(path)) {
                return;
            }

            FileSnapshot actual = CaptureFileSnapshot(root, path, 128 * 1024 * 1024);
            if (expected is FileSnapshot known) {
                if (actual != known) {
                    throw new IOException("Claude transaction cleanup found replaced evidence.");
                }
            } else if (content is FileSnapshot digest) {
                if (!SameContent(actual, digest)) {
                    throw new IOException("Claude transaction cleanup found incomplete evidence.");
                }
            } else if (role == "apply" && entry.After is ContentDigest after && actual.Length == after.Length
                && string.Equals(actual.Sha256, after.Sha256, StringComparison.Ordinal)) {
            } else {
                throw new IOException("Claude transaction cleanup found unknown evidence.");
            }

            inventory.Add(path, actual);
        }

        private void ValidateFiles(TransactionManifest manifest, bool requirePrepared) {
            if (requirePrepared && !manifest.Prepared) {
                throw new IOException("Claude file transaction preparation did not complete.");
            }

            foreach (Entry entry in manifest.Entries) {
                string live = Live(entry);
                string anchor = entry.AnchorPath == "." ? root : ClaudeWindowsPathSafety.ResolveContainedPath(root, entry.AnchorPath);
                if (CaptureEntryIdentity(root, anchor, true) != entry.AnchorIdentity
                    || entry.ParentIdentity is FileIdentity parentIdentity
                        && CaptureEntryIdentity(root, Path.GetDirectoryName(live)!, true) != parentIdentity
                    || entry.ParentIdentity is null && Directory.Exists(Path.GetDirectoryName(live)!)) {
                    throw new IOException("A Claude live parent directory was replaced.");
                }

                if (entry.Before is FileSnapshot before && (!Matches(Payload(entry, "backup"), entry.Backup)
                    || entry.Backup is not FileSnapshot backup || !SameContent(backup, before))) {
                    throw new IOException("Claude file transaction backup is missing or changed.");
                }

                if (entry.Apply is FileSnapshot apply && !Matches(Payload(entry, "apply"), apply)
                    && !Matches(live, apply) && !Matches(Payload(entry, "rollback-displaced"), apply)) {
                    throw new IOException("Claude file transaction after evidence is missing or changed.");
                }

                bool hasBefore = IsBefore(entry);
                bool hasAfter = entry.Apply is FileSnapshot after && Matches(live, after);
                bool absent = !Exists(live) && (entry.Before is null
                    || entry.Intent != RenameIntent.None && Matches(Payload(entry, "displaced"), entry.Before));
                if (!hasBefore && !hasAfter && !absent) {
                    throw new IOException("A Claude live file has an external or unproven state.");
                }

                foreach ((string role, FileSnapshot? expected) in new[] {
                    ("displaced", entry.Before), ("restore", entry.Restore), ("rollback-displaced", entry.Apply)
                }) {
                    string path = Payload(entry, role);
                    if (Exists(path) && (expected is null || !Matches(path, expected))) {
                        throw new IOException("Claude transaction rename evidence changed.");
                    }
                }
            }
        }

        private bool IsBefore(Entry entry) {
            return entry.Before is null ? !Exists(Live(entry))
                : Matches(Live(entry), entry.Before) || entry.Restore is FileSnapshot restored && Matches(Live(entry), restored);
        }

        private async Task<TransactionManifest> EnsureParentsAsync(TransactionManifest manifest, CancellationToken cancellationToken) {
            foreach (string parent in manifest.Entries.Where(item => item.ParentIdentity is null)
                .Select(item => Path.GetDirectoryName(Live(item))!).Distinct(StringComparer.OrdinalIgnoreCase)) {
                Stack<string> missing = new();
                string path = parent;
                while (!Directory.Exists(path)) {
                    missing.Push(path);
                    path = Path.GetDirectoryName(path) ?? throw new IOException("Claude live parent is unavailable.");
                    ClaudeWindowsPathSafety.EnsureContained(root, path);
                }

                while (missing.TryPop(out string? next)) {
                    await guard(cancellationToken).ConfigureAwait(false);
                    ClaudeWindowsPathSafety.EnsureNoReparsePoints(root, next);
                    if (!CreateDirectoryW(next, IntPtr.Zero)) {
                        ThrowWin32IOException("Claude target directory could not be exclusively created.");
                    }
                }

                FileIdentity identity = CaptureEntryIdentity(root, parent, true);
                Entry[] entries = manifest.Entries.Select(item => string.Equals(Path.GetDirectoryName(Live(item)), parent,
                    StringComparison.OrdinalIgnoreCase) ? item with { ParentIdentity = identity } : item).ToArray();
                manifest = manifest with { Entries = entries };
                Save(manifest);
            }

            return manifest;
        }

        private async Task RenameAsync(TransactionManifest manifest, string source, string destination, FileSnapshot expected,
            string operation, CancellationToken cancellationToken) {
            List<SafeFileHandle> directories = [];
            try {
                // The rename API opens parent directories internally while these identity handles remain held.
                foreach (string path in ParentChain(source).Concat(ParentChain(destination)).Distinct(StringComparer.OrdinalIgnoreCase)) {
                    SafeFileHandle handle = OpenEntry(root, path, true, false, false, allowDirectoryWrites: true);
                    EnsureEntryType(GetFileEntryInfo(handle), path, true);
                    directories.Add(handle);
                }

                if (CaptureEntryIdentity(root, root, true) != manifest.RootIdentity
                    || CaptureEntryIdentity(root, directory, true) != manifest.DirectoryIdentity) {
                    throw new IOException("Claude transaction directories changed before rename.");
                }

                foreach (Entry entry in manifest.Entries) {
                    if ((string.Equals(Live(entry), source, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Live(entry), destination, StringComparison.OrdinalIgnoreCase))
                        && CaptureEntryIdentity(root, Path.GetDirectoryName(Live(entry))!, true) != entry.ParentIdentity) {
                        throw new IOException("Claude live parent changed before rename.");
                    }
                }

                using SafeFileHandle file = OpenEntry(root, source, false, true, true);
                if (GetFileEntryInfo(file).NumberOfLinks != 1
                    || CaptureFileSnapshot(file, source, 128 * 1024 * 1024, null) != expected || Exists(destination)) {
                    throw new IOException("Claude transaction rename source or destination changed.");
                }

                await guard(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                string observedPath = source.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    ? destination : source;
                observer?.Invoke("before-" + operation, observedPath);
                RenameHandle(file, destination);
                observer?.Invoke("after-" + operation, observedPath);
            } finally {
                foreach (SafeFileHandle handle in directories) {
                    handle.Dispose();
                }
            }
        }

        private IEnumerable<string> ParentChain(string path) {
            string? parent = Path.GetDirectoryName(path);
            while (parent is not null) {
                ClaudeWindowsPathSafety.EnsureContained(root, parent);
                yield return parent;
                if (string.Equals(parent, root, StringComparison.OrdinalIgnoreCase)) {
                    yield break;
                }

                parent = Path.GetDirectoryName(parent);
            }
        }

        private TransactionManifest Update(TransactionManifest manifest, int index, Entry entry) {
            Entry[] entries = manifest.Entries.ToArray();
            entries[index] = entry;
            TransactionManifest updated = manifest with { Entries = entries };
            Save(updated);
            return updated;
        }

        private TransactionManifest Load() {
            return ReadManifest(Path.Combine(directory, ManifestName));
        }

        private TransactionManifest ReadManifest(string path) {
            using FileContentsSnapshot file = ReadFileContentsSnapshot(root, path, 16 * 1024 * 1024);
            TransactionManifest manifest = JsonSerializer.Deserialize<TransactionManifest>(file.Content)
                ?? throw new InvalidDataException("Claude file transaction manifest is missing.");
            if (manifest.SchemaVersion != 1 || manifest.TransactionId != transactionId
                || !BindingsEqual(manifest.Binding, binding)
                || manifest.RootIdentity != CaptureEntryIdentity(root, root, true)
                || manifest.DirectoryIdentity != CaptureEntryIdentity(root, directory, true)
                || manifest.Entries.Count > 10000
                || manifest.Entries.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Entries.Count) {
                throw new InvalidDataException("Claude file transaction provenance changed.");
            }

            foreach (Entry entry in manifest.Entries) {
                if (entry.Name.Length != 6 || !entry.Name.All(char.IsAsciiDigit)) {
                    throw new InvalidDataException("Claude file transaction payload name is invalid.");
                }

                _ = Live(entry);
            }

            return manifest;
        }

        private void CleanupUnpublishedManifest(CancellationToken cancellationToken) {
            string pendingPath = Path.Combine(directory, ManifestName + ".pending");
            string[] entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            if (entries.Length != 1
                || !string.Equals(entries[0], pendingPath, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(pendingPath)
                || Directory.Exists(pendingPath)) {
                throw new IOException("Claude file transaction has an unpublished manifest and unknown contents.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            using FileContentsSnapshot pending = ReadFileContentsSnapshot(root, pendingPath, 16 * 1024 * 1024);
            TransactionManifest manifest = JsonSerializer.Deserialize<TransactionManifest>(pending.Content)
                ?? throw new InvalidDataException("Claude pending file transaction manifest is invalid.");
            _ = ValidateManifest(manifest);
            if (manifest.Prepared
                || manifest.Entries.Any(entry => entry.Intent != RenameIntent.None
                    || entry.Backup is not null
                    || entry.Apply is not null
                    || entry.Restore is not null)) {
                throw new InvalidDataException("Claude pending file transaction manifest is not an initial preparation snapshot.");
            }

            DeleteFileIfUnchanged(root, pendingPath, pending.Snapshot, 16 * 1024 * 1024);
            observer?.Invoke("cleanup-pending-manifest", pendingPath);
            DeleteDirectoryIfEmpty(root, directory, manifest.DirectoryIdentity);
        }

        private TransactionManifest ValidateManifest(TransactionManifest manifest) {
            if (manifest.SchemaVersion != 1 || manifest.TransactionId != transactionId
                || !BindingsEqual(manifest.Binding, binding)
                || manifest.RootIdentity != CaptureEntryIdentity(root, root, true)
                || manifest.DirectoryIdentity != CaptureEntryIdentity(root, directory, true)
                || manifest.Entries.Count > 10000
                || manifest.Entries.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Entries.Count) {
                throw new InvalidDataException("Claude file transaction provenance changed.");
            }

            foreach (Entry entry in manifest.Entries) {
                if (entry.Name.Length != 6 || !entry.Name.All(char.IsAsciiDigit)) {
                    throw new InvalidDataException("Claude file transaction payload name is invalid.");
                }

                _ = Live(entry);
            }

            return manifest;
        }

        private void EnsureTerminalLiveState(TransactionManifest manifest) {
            foreach (Entry entry in manifest.Entries) {
                string live = Live(entry);
                string anchor = entry.AnchorPath == "." ? root : ClaudeWindowsPathSafety.ResolveContainedPath(root, entry.AnchorPath);
                string parent = Path.GetDirectoryName(live)!;
                bool parentExists = Exists(parent);
                bool parentMatches = entry.ParentIdentity is FileIdentity parentIdentity
                    ? parentExists && CaptureEntryIdentity(root, parent, true) == parentIdentity
                    : !parentExists;
                bool restoredAbsentParent = entry.Intent == RenameIntent.Restored
                    && entry.Before is null
                    && !parentExists;
                if (CaptureEntryIdentity(root, anchor, true) != entry.AnchorIdentity
                    || !parentMatches && !restoredAbsentParent) {
                    throw new IOException("Claude file transaction live parent changed before cleanup.");
                }

                bool terminal = entry.Intent switch {
                    RenameIntent.None or RenameIntent.Restored => IsBefore(entry),
                    RenameIntent.Applied => IsAfter(entry),
                    _ => false
                };
                if (!terminal) {
                    throw new IOException("Claude file transaction live state changed before recovery evidence cleanup.");
                }
            }
        }

        private bool IsAfter(Entry entry) {
            return entry.Apply is FileSnapshot after ? Matches(Live(entry), after) : !Exists(Live(entry));
        }

        private void Save(TransactionManifest manifest) {
            string path = Path.Combine(directory, ManifestName);
            string pending = path + ".pending";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            try {
                // 發布前中斷會留下未發布的 pending；已發布的 manifest 才是交易狀態，確認殘留檔屬於本交易後才捨棄重寫。
                if (File.Exists(path) && File.Exists(pending)) {
                    DeleteStalePendingManifest(pending);
                }

                WritePayload(pending, bytes);
                observer?.Invoke("before-manifest-publish", pending);
                File.Move(pending, path, overwrite: true);
                observer?.Invoke("after-manifest-publish", path);
            } finally {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        private void DeleteStalePendingManifest(string pendingPath) {
            if (Directory.Exists(pendingPath)) {
                throw new IOException("Claude file transaction pending manifest has unknown contents.");
            }

            using FileContentsSnapshot pending = ReadFileContentsSnapshot(root, pendingPath, 16 * 1024 * 1024);
            TransactionManifest manifest;
            try {
                manifest = JsonSerializer.Deserialize<TransactionManifest>(pending.Content)
                    ?? throw new InvalidDataException("Claude pending file transaction manifest is invalid.");
            } catch (JsonException failure) {
                throw new IOException("Claude file transaction pending manifest has unknown contents.", failure);
            }

            // 只刪除屬於同一交易、且讀取後未被改動的 pending；其他內容保留供人工處置。
            _ = ValidateManifest(manifest);
            DeleteFileIfUnchanged(root, pendingPath, pending.Snapshot, 16 * 1024 * 1024);
        }

        private void EnsureOwned(IEnumerable<string> paths) {
            HashSet<string> expected = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Directory.EnumerateFileSystemEntries(directory).Any(path => !expected.Contains(path))) {
                throw new IOException("Claude file transaction contains an unknown entry.");
            }
        }

        private bool Matches(string path, FileSnapshot? snapshot) {
            return snapshot is FileSnapshot expected && File.Exists(path)
                && CaptureFileSnapshot(root, path, 128 * 1024 * 1024) == expected;
        }

        private string Live(Entry entry) => ClaudeWindowsPathSafety.ResolveContainedPath(root, entry.RelativePath);

        private string Payload(Entry entry, string role) => Path.Combine(directory, entry.Name + "." + role);

        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        private static bool SameContent(FileSnapshot left, FileSnapshot right)
            => left.Length == right.Length && string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal);

        private static bool BindingsEqual(ClaudeDesktopRootBinding left, ClaudeDesktopRootBinding right)
            => string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right), StringComparison.Ordinal);

        private static void WritePayload(string path, byte[] bytes) {
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        private sealed record ContentDigest(long Length, string Sha256);

        private sealed record Entry(string RelativePath, FileIdentity? ParentIdentity, string AnchorPath, FileIdentity AnchorIdentity,
            FileSnapshot? Before, ContentDigest? After,
            string Name, FileSnapshot? Backup, FileSnapshot? Apply, FileSnapshot? Restore, RenameIntent Intent);

        private sealed record TransactionManifest(int SchemaVersion, Guid TransactionId, ClaudeDesktopRootBinding Binding,
            FileIdentity RootIdentity, FileIdentity DirectoryIdentity, IReadOnlyList<Entry> Entries, bool Prepared);

        private enum RenameIntent { None, ApplyDisplace, ApplyPublish, Applied, RestoreDisplace, RestorePublish, Restored }
    }

    private static void RenameHandle(SafeFileHandle handle, string destination) {
        byte[] name = Encoding.Unicode.GetBytes(NormalizePath(destination));
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(uint);
        int bufferLength = checked(nameOffset + name.Length + sizeof(char));
        IntPtr buffer = Marshal.AllocHGlobal(bufferLength);
        try {
            Marshal.Copy(new byte[bufferLength], 0, buffer, bufferLength);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            if (!SetFileInformationByHandle(handle, 3, buffer, checked((uint)bufferLength))) {
                ThrowWin32IOException("Claude non-replace handle rename failed.");
            }
        } finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int fileInformationClass, IntPtr information, uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    internal sealed class FileContentsSnapshot : IDisposable {
        internal FileContentsSnapshot(FileSnapshot snapshot, byte[] content) {
            Snapshot = snapshot;
            Content = content;
        }

        internal FileSnapshot Snapshot { get; }

        internal byte[] Content { get; }

        public void Dispose() {
            CryptographicOperations.ZeroMemory(Content);
        }
    }

    internal static FileIdentity CaptureEntryIdentity(string rootPath, string expectedPath, bool expectDirectory) {
        using SafeFileHandle handle = OpenEntry(
            rootPath,
            expectedPath,
            expectDirectory,
            includeDeleteAccess: false,
            includeReadData: false
        );
        FileEntryInfo info = GetFileEntryInfo(handle);
        EnsureEntryType(info, expectedPath, expectDirectory);
        EnsureHandlePathMatches(handle, expectedPath);
        return info.Identity;
    }

    internal static FileSnapshot CaptureFileSnapshot(string rootPath, string expectedPath, long maximumBytes) {
        using SafeFileHandle handle = OpenEntry(
            rootPath,
            expectedPath,
            expectDirectory: false,
            includeDeleteAccess: false,
            includeReadData: true
        );
        return CaptureFileSnapshot(handle, expectedPath, maximumBytes, content: null);
    }

    internal static FileContentsSnapshot ReadFileContentsSnapshot(
        string rootPath,
        string expectedPath,
        int maximumBytes
    ) {
        using SafeFileHandle handle = OpenEntry(
            rootPath,
            expectedPath,
            expectDirectory: false,
            includeDeleteAccess: false,
            includeReadData: true
        );
        FileEntryInfo before = GetFileEntryInfo(handle);
        EnsureEntryType(before, expectedPath, expectDirectory: false);
        EnsureHandlePathMatches(handle, expectedPath);
        if (before.Length > maximumBytes) {
            throw new InvalidDataException("A Claude history transaction file exceeded its supported size limit.");
        }

        byte[] content = new byte[checked((int)before.Length)];
        try {
            FileSnapshot snapshot = CaptureFileSnapshot(handle, expectedPath, maximumBytes, content);
            return new FileContentsSnapshot(snapshot, content);
        } catch {
            CryptographicOperations.ZeroMemory(content);
            throw;
        }
    }

    internal static void DeleteFileIfUnchanged(
        string rootPath,
        string expectedPath,
        FileSnapshot expectedSnapshot,
        long maximumBytes
    ) {
        using SafeFileHandle handle = OpenEntry(
            rootPath,
            expectedPath,
            expectDirectory: false,
            includeDeleteAccess: true,
            includeReadData: true
        );
        FileSnapshot actualSnapshot = CaptureFileSnapshot(handle, expectedPath, maximumBytes, content: null);
        if (actualSnapshot != expectedSnapshot) {
            throw new InvalidDataException("A Claude history transaction file changed after inventory capture.");
        }

        SetFileDispositionForDeletion(handle);
    }

    internal static void DeleteDirectoryIfEmpty(
        string rootPath,
        string expectedPath,
        FileIdentity expectedIdentity
    ) {
        using SafeFileHandle handle = OpenEntry(
            rootPath,
            expectedPath,
            expectDirectory: true,
            includeDeleteAccess: true,
            includeReadData: false
        );
        FileEntryInfo before = GetFileEntryInfo(handle);
        EnsureEntryType(before, expectedPath, expectDirectory: true);
        EnsureHandlePathMatches(handle, expectedPath);
        if (before.Identity != expectedIdentity) {
            throw new InvalidDataException("A Claude history transaction directory changed after inventory capture.");
        }

        if (Directory.EnumerateFileSystemEntries(expectedPath).Any()) {
            throw new IOException("A Claude history transaction directory is not empty.");
        }

        FileEntryInfo after = GetFileEntryInfo(handle);
        EnsureEntryType(after, expectedPath, expectDirectory: true);
        EnsureHandlePathMatches(handle, expectedPath);
        if (after.Identity != expectedIdentity) {
            throw new InvalidDataException("A Claude history transaction directory changed during cleanup.");
        }

        SetFileDispositionForDeletion(handle);
    }

    private static SafeFileHandle OpenEntry(
        string rootPath,
        string expectedPath,
        bool expectDirectory,
        bool includeDeleteAccess,
        bool includeReadData,
        bool allowDirectoryWrites = false
    ) {
        string fullRootPath = NormalizePath(rootPath);
        string fullExpectedPath = NormalizePath(expectedPath);
        ClaudeWindowsPathSafety.EnsureContained(fullRootPath, fullExpectedPath);
        ClaudeWindowsPathSafety.EnsureNoReparsePoints(fullRootPath, fullExpectedPath);

        uint desiredAccess = FileReadAttributesAccess;
        if (expectDirectory) {
            desiredAccess |= FileListDirectoryAccess;
        } else if (includeReadData) {
            desiredAccess |= FileReadDataAccess;
        }

        if (includeDeleteAccess) {
            desiredAccess |= DeleteAccess;
        }

        uint shareMode = ShareRead | (expectDirectory && allowDirectoryWrites ? ShareWrite : 0);
        uint flags = FileFlagOpenReparsePoint;
        if (expectDirectory) {
            flags |= FileFlagBackupSemantics;
        }

        SafeFileHandle handle = CreateFileW(
            fullExpectedPath,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero
        );
        if (handle.IsInvalid) {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                "A Claude history transaction entry could not be opened without following reparse points.",
                new Win32Exception(error)
            );
        }

        try {
            EnsureHandlePathMatches(handle, fullExpectedPath);
            return handle;
        } catch {
            handle.Dispose();
            throw;
        }
    }

    private static FileSnapshot CaptureFileSnapshot(
        SafeFileHandle handle,
        string expectedPath,
        long maximumBytes,
        byte[]? content
    ) {
        FileEntryInfo before = GetFileEntryInfo(handle);
        EnsureEntryType(before, expectedPath, expectDirectory: false);
        EnsureHandlePathMatches(handle, expectedPath);
        if (before.Length < 0 || before.Length > maximumBytes) {
            throw new InvalidDataException("A Claude history transaction file exceeded its supported size limit.");
        }

        if (content is not null && content.LongLength != before.Length) {
            throw new IOException("A Claude history transaction file changed while its contents were being captured.");
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long offset = 0;
        try {
            while (offset < before.Length) {
                int count = checked((int)Math.Min(buffer.Length, before.Length - offset));
                int bytesRead = RandomAccess.Read(handle, buffer.AsSpan(0, count), offset);
                if (bytesRead == 0) {
                    throw new EndOfStreamException("A Claude history transaction file changed while being read.");
                }

                hash.AppendData(buffer, 0, bytesRead);
                if (content is not null) {
                    buffer.AsSpan(0, bytesRead).CopyTo(content.AsSpan(checked((int)offset), bytesRead));
                }

                offset += bytesRead;
            }

            if (RandomAccess.Read(handle, buffer.AsSpan(0, 1), before.Length) != 0) {
                throw new IOException("A Claude history transaction file changed while being read.");
            }

            FileEntryInfo after = GetFileEntryInfo(handle);
            EnsureEntryType(after, expectedPath, expectDirectory: false);
            EnsureHandlePathMatches(handle, expectedPath);
            if (after.Identity != before.Identity || after.Length != before.Length) {
                throw new IOException("A Claude history transaction file changed while being read.");
            }

            return new FileSnapshot(before.Identity, before.Length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        } finally {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static FileEntryInfo GetFileEntryInfo(SafeFileHandle handle) {
        if (!GetFileInformationByHandleEx(
            handle,
            FileIdInfoClass,
            out FileIdInfo idInfo,
            checked((uint)Marshal.SizeOf<FileIdInfo>())
        )) {
            ThrowWin32IOException("A Claude history transaction entry identity could not be read.");
        }

        if (!GetFileInformationByHandleEx(
            handle,
            FileAttributeTagInfoClass,
            out FileAttributeTagInfo attributeInfo,
            checked((uint)Marshal.SizeOf<FileAttributeTagInfo>())
        )) {
            ThrowWin32IOException("A Claude history transaction entry attributes could not be read.");
        }

        if (!GetFileInformationByHandleEx(
            handle,
            FileStandardInfoClass,
            out FileStandardInfo standardInfo,
            checked((uint)Marshal.SizeOf<FileStandardInfo>())
        )) {
            ThrowWin32IOException("A Claude history transaction entry size could not be read.");
        }

        return new FileEntryInfo(
            new FileIdentity(idInfo.VolumeSerialNumber, idInfo.FileIdLow, idInfo.FileIdHigh),
            standardInfo.EndOfFile,
            attributeInfo.FileAttributes,
            standardInfo.NumberOfLinks,
            standardInfo.DeletePending != 0,
            standardInfo.Directory != 0
        );
    }

    private static void EnsureEntryType(FileEntryInfo info, string path, bool expectDirectory) {
        if ((info.Attributes & FileAttributeReparsePoint) != 0) {
            throw new InvalidDataException("A Claude history transaction entry is a reparse point.");
        }

        bool isDirectory = (info.Attributes & FileAttributeDirectory) != 0;
        if (isDirectory != expectDirectory
            || info.IsDirectory != expectDirectory
            || info.DeletePending
            || info.NumberOfLinks == 0 || !expectDirectory && info.NumberOfLinks != 1) {
            throw new InvalidDataException($"Claude history transaction entry '{path}' has an unexpected type.");
        }
    }

    private static void EnsureHandlePathMatches(SafeFileHandle handle, string expectedPath) {
        StringBuilder pathBuffer = new(checked((int)FinalPathBufferLength));
        uint length = GetFinalPathNameByHandleW(handle, pathBuffer, FinalPathBufferLength, VolumeNameDos);
        if (length == 0) {
            ThrowWin32IOException("A Claude history transaction handle path could not be verified.");
        }

        if (length >= FinalPathBufferLength) {
            throw new PathTooLongException("A Claude history transaction handle path is too long to verify.");
        }

        string finalPath = NormalizePath(pathBuffer.ToString());
        string normalizedExpectedPath = NormalizePath(expectedPath);
        if (!string.Equals(finalPath, normalizedExpectedPath, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException("A Claude history transaction handle does not refer to the expected path.");
        }
    }

    private static string NormalizePath(string path) {
        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) {
            fullPath = "\\\\" + fullPath[8..];
        } else if (fullPath.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase)) {
            fullPath = fullPath[4..];
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static void SetFileDispositionForDeletion(SafeFileHandle handle) {
        FileDispositionInfo disposition = new() { DeleteFile = 1 };
        if (!SetFileInformationByHandle(
            handle,
            FileDispositionInfoClass,
            ref disposition,
            checked((uint)Marshal.SizeOf<FileDispositionInfo>())
        )) {
            ThrowWin32IOException("A Claude history transaction entry could not be marked for deletion.");
        }

    }

    private static void ThrowWin32IOException(string message) {
        throw new IOException(message, new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile
    );

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileIdInfo fileInformation,
        uint bufferSize
    );

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize
    );

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileStandardInfo fileInformation,
        uint bufferSize
    );

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle fileHandle,
        StringBuilder filePath,
        uint filePathLength,
        uint flags
    );

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo {
        public byte DeleteFile;
    }

    private readonly record struct FileEntryInfo(
        FileIdentity Identity,
        long Length,
        uint Attributes,
        uint NumberOfLinks,
        bool DeletePending,
        bool IsDirectory
    );
}
