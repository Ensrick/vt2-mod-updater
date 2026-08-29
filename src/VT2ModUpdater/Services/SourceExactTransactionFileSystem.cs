using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VT2ModUpdater.Services;

/// <summary>
/// Windows-only, fail-closed filesystem boundary for the disabled source-exact
/// transaction. Destructive work is performed through authenticated handles.
/// The contract covers process death on NTFS, not sudden power loss.
/// </summary>
internal static class SourceExactTransactionFileSystem
{
    private const uint FileListDirectory = 0x0001;
    private const uint FileAddFile = 0x0002;
    private const uint FileAddSubdirectory = 0x0004;
    private const uint FileReadAttributes = 0x0080;
    private const uint DeleteAccess = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint CreateNew = 1;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint NtFileWriteThrough = 0x00000002;
    private const uint NtFileSynchronousIoNonAlert = 0x00000020;
    private const uint NtFileNonDirectoryFile = 0x00000040;
    private const uint NtFileOpenReparsePoint = 0x00200000;
    private const uint NtFileCreate = 2;
    private const uint ObjCaseInsensitive = 0x00000040;
    private const int FileRenameInformation = 10;
    private const int FileIdInfo = 18;
    private const int FileBasicInfo = 0;
    private const int FileStandardInfo = 1;
    private const int FileDispositionInfo = 4;
    private const int FileCaseSensitiveInfo = 23;
    private const uint FileCsFlagCaseSensitiveDir = 0x00000001;
    private const int ErrorHandleEof = 38;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int FindStreamInfoStandard = 0;
    private const uint DriveFixed = 3;

    internal static DirectoryLease OpenDirectory(string path, bool protectWrites = false) =>
        DirectoryLease.Open(
            path, protectWrites, allowChildRename: false, allowChildCreate: false);

    internal static DirectoryLease OpenParentDirectory(string path) =>
        DirectoryLease.Open(
            path, protectWrites: false, allowChildRename: true, allowChildCreate: false);

    internal static ExactDirectoryGuard GuardDirectory(string path) =>
        ExactDirectoryGuard.Open(path, protectDeletes: false);

    internal static ExactDirectoryGuard PinDirectory(string path) =>
        ExactDirectoryGuard.Open(path, protectDeletes: true);

    internal static ExclusiveFileLock AcquireCrossSessionLock(
        string path,
        TimeSpan timeout)
    {
        var normalized = Normalize(path);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var handle = CreateFileW(
                normalized,
                GenericRead | GenericWrite | FileReadAttributes | Synchronize,
                FileShare.None,
                IntPtr.Zero,
                OpenAlways,
                FileFlagOpenReparsePoint | FileFlagWriteThrough,
                IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                try
                {
                    var basic = GetBasic(handle);
                    var standard = GetStandard(handle);
                    if ((basic.FileAttributes & ((uint)FileAttributes.Directory |
                         (uint)FileAttributes.ReparsePoint)) != 0 ||
                        standard.Directory != 0 || standard.NumberOfLinks != 1 ||
                        standard.EndOfFile != 0 ||
                        !SamePath(FinalPath(handle), normalized))
                    {
                        throw new InvalidDataException(
                            "source-exact lock path is not one empty unaliased regular file");
                    }
                    RequireNoAlternateStreams(normalized);
                    return new ExclusiveFileLock(handle, normalized);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is not (ErrorSharingViolation or ErrorLockViolation))
                throw Io("cannot open the source-exact cross-session lock", error);
            if (timer.Elapsed >= timeout)
                throw new SourceExactLockUnavailableException();
            Thread.Sleep(25);
        }
    }

    internal static void RequireNtfs(string path)
    {
        var volume = new StringBuilder(1024);
        if (!GetVolumePathNameW(Normalize(path), volume, volume.Capacity))
            throw Io("cannot resolve source-exact transaction volume");
        var driveType = GetDriveTypeW(volume.ToString());
        if (!IsLocalFixedDriveType(driveType))
            throw new InvalidDataException(
                $"source-exact directory transactions require a local fixed volume; " +
                $"drive type {driveType} is unsupported");
        var fileSystem = new StringBuilder(64);
        if (!GetVolumeInformationW(
                volume.ToString(), null, 0, out _, out _, out _, fileSystem, fileSystem.Capacity))
            throw Io("cannot identify source-exact transaction filesystem");
        if (!string.Equals(fileSystem.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"source-exact directory transactions require NTFS; '{fileSystem}' is unsupported");
    }

    internal static bool IsLocalFixedDriveType(uint driveType) => driveType == DriveFixed;

    internal static ExactDirectorySnapshot Snapshot(DirectoryLease directory)
    {
        directory.RequireCurrentPath();
        using var guard = ExactDirectoryGuard.Open(
            directory.CurrentPath, protectDeletes: false);
        return guard.Snapshot;
    }

    internal static ExactSnapshotMatch InspectSnapshot(
        string path,
        ExactDirectorySnapshot expected)
    {
        if (!Directory.Exists(path))
            return File.Exists(path) ? ExactSnapshotMatch.Foreign : ExactSnapshotMatch.Absent;
        try
        {
            using var guard = ExactDirectoryGuard.Open(path, protectDeletes: false);
            if (guard.Snapshot.Identity != expected.Identity)
                return ExactSnapshotMatch.Foreign;
            if (guard.Snapshot.Files.SequenceEqual(expected.Files))
                return ExactSnapshotMatch.Exact;
            if (guard.Snapshot.Files.All(actual => expected.Files.Any(row => row == actual)))
                return ExactSnapshotMatch.ExactSubset;
            return ExactSnapshotMatch.Foreign;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ExactSnapshotMatch.Foreign;
        }
    }

    internal static void VerifySnapshot(string path, ExactDirectorySnapshot expected)
    {
        using var guard = ExactDirectoryGuard.Open(path, protectDeletes: false);
        guard.RequireExact(expected);
    }

    internal static void DeleteExactDirectoryRestartable(
        string path,
        ExactDirectorySnapshot expected,
        bool allowPartial,
        string checkpointPrefix,
        Action<string>? checkpoint = null)
    {
        if (!Directory.Exists(path))
        {
            if (File.Exists(path))
                throw new InvalidDataException("refusing to delete a file in place of an exact directory");
            return;
        }
        using var guard = ExactDirectoryGuard.Open(path, protectDeletes: false);
        guard.RequireExpected(expected, allowPartial);
        guard.DeleteAll(checkpointPrefix, checkpoint);
    }

    internal static void DeleteOwnedExactDirectory(
        DirectoryLease ownership,
        ExactDirectorySnapshot expected,
        string checkpointPrefix,
        Action<string>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ownership.RequireCurrentPath();
        if (ownership.Identity != expected.Identity)
            throw new InvalidDataException(
                "source-exact stage lease does not own the recorded directory identity");
        using var guard = ExactDirectoryGuard.Open(
            ownership.CurrentPath, protectDeletes: false);
        guard.RequireExact(expected);
        // The exact child and directory handles in guard now own the deletion
        // proof. Release the original Phase 3 lease so the directory can leave
        // the namespace after its last exact child is deleted.
        ownership.Dispose();
        guard.DeleteAll(checkpointPrefix, checkpoint);
    }

    internal static FileWitness CreateDurableAtomicFile(
        string parentPath,
        string destinationPath,
        ReadOnlySpan<byte> bytes,
        string tempCheckpoint,
        string publishedCheckpoint,
        Action<string>? checkpoint = null)
    {
        using var parent = OpenParentDirectory(parentPath);
        var destination = Normalize(destinationPath);
        if (!string.Equals(Path.GetDirectoryName(destination), parent.CurrentPath,
                StringComparison.OrdinalIgnoreCase) ||
            !SafeLeaf(Path.GetFileName(destination)))
            throw new InvalidDataException("atomic file destination is outside its pinned parent");
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("atomic file destination is occupied");

        var temp = destination + ".partial-" +
            RandomNumberGenerator.GetHexString(16).ToLowerInvariant();
        if (!SafeLeaf(Path.GetFileName(temp)))
            throw new InvalidDataException("atomic file temporary leaf is unsafe");

        FileStream? stream = null;
        try
        {
            var tempHandle = CreateFileW(
                temp,
                GenericRead | GenericWrite | DeleteAccess | FileReadAttributes | Synchronize,
                FileShare.Read | FileShare.Delete,
                IntPtr.Zero,
                CreateNew,
                FileFlagOpenReparsePoint | FileFlagWriteThrough,
                IntPtr.Zero);
            if (tempHandle.IsInvalid)
            {
                tempHandle.Dispose();
                throw Io("cannot create the atomic witness temporary file");
            }
            stream = new FileStream(
                tempHandle,
                FileAccess.ReadWrite,
                4096,
                isAsync: false);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            stream.Position = 0;
            checkpoint?.Invoke(tempCheckpoint);
            RenameRelative(stream.SafeFileHandle, parent.Handle, Path.GetFileName(destination));
            if (!SamePath(FinalPath(stream.SafeFileHandle), destination))
                throw new IOException("atomic witness rename did not reach its pinned destination");
            checkpoint?.Invoke(publishedCheckpoint);
            var identity = GetIdentity(stream.SafeFileHandle, destination);
            stream.Dispose();
            stream = null;
            return new FileWitness(destination, bytes.ToArray(), identity);
        }
        catch
        {
            if (stream is not null)
            {
                try { MarkDelete(stream.SafeFileHandle); }
                catch { }
                stream.Dispose();
            }
            throw;
        }
    }

    internal static FileStream CreateDurableNewFile(
        DirectoryLease directory,
        string leaf,
        ReadOnlySpan<byte> bytes,
        string pinnedCheckpoint,
        Action<string>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (!SafeLeaf(leaf))
            throw new InvalidDataException("relative source-exact leaf is unsafe");
        directory.RequireCurrentPath();
        using var creator = DirectoryLease.Open(
            directory.CurrentPath,
            protectWrites: false,
            allowChildRename: false,
            allowChildCreate: true);
        if (creator.Identity != directory.Identity)
            throw new InvalidDataException(
                "relative source-exact create lease differs from its Stage 3 owner");
        checkpoint?.Invoke(pinnedCheckpoint);
        var path = Path.Combine(directory.CurrentPath, leaf);
        var stream = CreateRelativeNewFile(creator.Handle, leaf);
        try
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            stream.Position = 0;
            directory.RequireCurrentPath();
            creator.RequireCurrentPath();
            if (!SamePath(FinalPath(stream.SafeFileHandle), path))
                throw new InvalidDataException(
                    "relative source-exact file escaped its pinned directory");
            var basic = GetBasic(stream.SafeFileHandle);
            var standard = GetStandard(stream.SafeFileHandle);
            if ((basic.FileAttributes & ((uint)FileAttributes.Directory |
                 (uint)FileAttributes.ReparsePoint)) != 0 ||
                standard.Directory != 0 || standard.NumberOfLinks != 1)
                throw new InvalidDataException(
                    "relative source-exact leaf is not one plain regular file");
            RequireNoAlternateStreams(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static FileStream CreateRelativeNewFile(
        SafeFileHandle directory,
        string leaf)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(leaf);
        var unicodePointer = IntPtr.Zero;
        var attributesPointer = IntPtr.Zero;
        IntPtr rawHandle = IntPtr.Zero;
        var directoryPinned = false;
        try
        {
            directory.DangerousAddRef(ref directoryPinned);
            var nameLength = checked((ushort)Encoding.Unicode.GetByteCount(leaf));
            var unicode = new UnicodeString
            {
                Length = nameLength,
                MaximumLength = checked((ushort)(nameLength + sizeof(char))),
                Buffer = nameBuffer
            };
            unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode, unicodePointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                RootDirectory = directory.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = ObjCaseInsensitive
            };
            attributesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<ObjectAttributes>());
            Marshal.StructureToPtr(attributes, attributesPointer, fDeleteOld: false);
            var status = NtCreateFile(
                out rawHandle,
                GenericRead | GenericWrite | FileReadAttributes | Synchronize,
                attributesPointer,
                out _,
                IntPtr.Zero,
                FileAttributeNormal,
                (uint)FileShare.Read,
                NtFileCreate,
                NtFileWriteThrough | NtFileSynchronousIoNonAlert |
                    NtFileNonDirectoryFile | NtFileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status < 0 || rawHandle == IntPtr.Zero || rawHandle == new IntPtr(-1))
                throw new IOException(
                    $"handle-relative source-exact create failed (NTSTATUS 0x{status:x8})");
            var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
            rawHandle = IntPtr.Zero;
            try
            {
                return new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            if (rawHandle != IntPtr.Zero && rawHandle != new IntPtr(-1))
                new SafeFileHandle(rawHandle, ownsHandle: true).Dispose();
            if (attributesPointer != IntPtr.Zero) Marshal.FreeHGlobal(attributesPointer);
            if (unicodePointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodePointer);
            Marshal.FreeHGlobal(nameBuffer);
            if (directoryPinned) directory.DangerousRelease();
        }
    }

    internal static BoundedFile ReadBoundedExactFile(
        string path,
        int maximumBytes,
        bool allowEmpty = false)
    {
        using var file = OpenRegularFile(
            path,
            FileShare.Read | FileShare.Delete,
            GenericRead | FileReadAttributes | Synchronize);
        RequireNoAlternateStreams(path);
        if ((!allowEmpty && file.Length == 0) || file.Length < 0 || file.Length > maximumBytes)
            throw new InvalidDataException("bounded exact file has an invalid byte length");
        var buffer = new byte[checked((int)file.Length + 1)];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = file.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total != file.Length)
            throw new InvalidDataException("bounded exact file changed or exceeded its byte proof");
        return new BoundedFile(
            path,
            buffer.AsSpan(0, total).ToArray(),
            GetIdentity(file.SafeFileHandle, path));
    }

    internal static void DeleteExactFile(FileWitness witness)
    {
        if (!File.Exists(witness.Path))
        {
            if (Directory.Exists(witness.Path))
                throw new InvalidDataException("refusing to delete a directory in place of a witness");
            return;
        }
        using var file = OpenRegularFile(
            witness.Path,
            FileShare.Read | FileShare.Delete,
            DeleteAccess | GenericRead | FileReadAttributes | Synchronize);
        if (GetIdentity(file.SafeFileHandle, witness.Path) != witness.Identity ||
            file.Length != witness.Bytes.Length)
            throw new InvalidDataException("refusing to delete a replaced transaction witness");
        RequireNoAlternateStreams(witness.Path);
        var actual = SHA256.HashData(file);
        var expected = SHA256.HashData(witness.Bytes);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException("refusing to delete a changed transaction witness");
        MarkDelete(file.SafeFileHandle);
    }

    internal static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        while (full.Length > root.Length && Path.EndsInDirectorySeparator(full))
            full = full[..^1];
        return full;
    }

    internal static bool SamePath(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    internal static bool SafeLeaf(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 ||
            value != Path.GetFileName(value) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains(':') || value is "." or ".." ||
            value.EndsWith(' ') || value.EndsWith('.'))
            return false;

        var stem = value.Split('.')[0];
        return !stem.Equals("CON", StringComparison.OrdinalIgnoreCase) &&
               !stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) &&
               !stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) &&
               !stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) &&
               !stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) &&
               !stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) &&
               !stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) &&
               !IsNumberedDevice(stem, "COM") &&
               !IsNumberedDevice(stem, "LPT");
    }

    internal static bool SafeTargetLeaf(string value) =>
        SafeLeaf(value) &&
        !value.StartsWith(".vt2-source-exact-", StringComparison.OrdinalIgnoreCase);

    private static bool IsNumberedDevice(string value, string prefix)
    {
        if (value.Length != prefix.Length + 1 ||
            !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        return value[^1] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3';
    }

    internal sealed class DirectoryLease : IDisposable
    {
        private SafeFileHandle? _handle;

        private DirectoryLease(string path, SafeFileHandle handle, ExactIdentity identity)
        {
            CurrentPath = path;
            _handle = handle;
            Identity = identity;
        }

        internal string CurrentPath { get; private set; }
        internal ExactIdentity Identity { get; }
        internal SafeFileHandle Handle => _handle ??
            throw new ObjectDisposedException(nameof(DirectoryLease));

        internal static DirectoryLease Open(
            string path,
            bool protectWrites,
            bool allowChildRename,
            bool allowChildCreate,
            bool protectDeletes = false)
        {
            var normalized = Normalize(path);
            var share = protectDeletes
                ? FileShare.Read
                : protectWrites
                ? FileShare.Read | FileShare.Delete
                : FileShare.Read | FileShare.Write | FileShare.Delete;
            var handle = CreateFileW(
                normalized,
                FileListDirectory | FileReadAttributes | DeleteAccess | Synchronize |
                    (allowChildRename ? FileAddSubdirectory : 0) |
                    (allowChildCreate ? FileAddFile : 0),
                share,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw Io($"cannot open source-exact directory '{normalized}'");
            }
            try
            {
                var basic = GetBasic(handle);
                if ((basic.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
                    (basic.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("source-exact path is not a plain directory");
                var caseInfo = GetInformation<FileCaseSensitiveInformation>(
                    handle, FileCaseSensitiveInfo, "case-sensitivity metadata");
                if ((caseInfo.Flags & FileCsFlagCaseSensitiveDir) != 0)
                    throw new InvalidDataException(
                        "source-exact transactions reject case-sensitive NTFS directories");
                var final = FinalPath(handle);
                if (!SamePath(final, normalized))
                    throw new InvalidDataException("source-exact directory resolves through an alias");
                RequireNoAlternateStreams(normalized);
                return new DirectoryLease(normalized, handle, GetIdentity(handle, normalized));
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        internal void RequireCurrentPath()
        {
            if (!SamePath(FinalPath(Handle), CurrentPath) ||
                GetIdentity(Handle, CurrentPath) != Identity)
                throw new InvalidDataException(
                    "source-exact directory lease no longer owns its recorded path");
            RequireNoAlternateStreams(CurrentPath);
        }

        internal void RenameTo(DirectoryLease parent, string destination)
        {
            RequireCurrentPath();
            parent.RequireCurrentPath();
            var normalized = Normalize(destination);
            if (!string.Equals(Path.GetDirectoryName(normalized), parent.CurrentPath,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "source-exact rename destination is outside the pinned parent");
            var leaf = Path.GetFileName(normalized);
            if (!SafeLeaf(leaf) || File.Exists(normalized) || Directory.Exists(normalized))
                throw new IOException("source-exact rename destination is occupied or unsafe");
            RenameRelative(Handle, parent.Handle, leaf);
            CurrentPath = normalized;
            RequireCurrentPath();
        }

        internal void DeleteWhenEmpty()
        {
            RequireCurrentPath();
            if (Directory.EnumerateFileSystemEntries(CurrentPath).Any())
                throw new IOException("refusing to delete a nonempty source-exact directory");
            MarkDelete(Handle);
            Dispose();
            if (Directory.Exists(CurrentPath) || File.Exists(CurrentPath))
                throw new IOException(
                    "source-exact directory remained after handle-bound deletion");
        }

        public void Dispose()
        {
            _handle?.Dispose();
            _handle = null;
        }
    }

    internal sealed class ExactDirectoryGuard : IDisposable
    {
        private readonly List<ExactFileLease> _files;
        private DirectoryLease? _directory;

        private ExactDirectoryGuard(
            DirectoryLease directory,
            List<ExactFileLease> files,
            ExactDirectorySnapshot snapshot)
        {
            _directory = directory;
            _files = files;
            Snapshot = snapshot;
        }

        internal ExactDirectorySnapshot Snapshot { get; }
        internal DirectoryLease Directory => _directory ??
            throw new ObjectDisposedException(nameof(ExactDirectoryGuard));

        internal static ExactDirectoryGuard Open(string path, bool protectDeletes)
        {
            var directory = DirectoryLease.Open(
                path,
                protectWrites: true,
                allowChildRename: false,
                allowChildCreate: false,
                protectDeletes: protectDeletes);
            var files = new List<ExactFileLease>();
            try
            {
                var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long aggregate = 0;
                foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(
                             directory.CurrentPath, "*", SearchOption.TopDirectoryOnly))
                {
                    if (files.Count >= SourceExactZipStager.MaximumEntries + 1)
                        throw new InvalidDataException(
                            "source-exact directory exceeds its file-count bound");
                    var name = Path.GetFileName(entry);
                    if (!SafeLeaf(name) || !insensitive.Add(name))
                        throw new InvalidDataException(
                            "source-exact directory contains an unsafe or colliding leaf");
                    var file = ExactFileLease.Open(entry, name, protectDeletes);
                    files.Add(file);
                    if (file.Length > SourceExactZipStager.MaximumOutputBytes ||
                        aggregate > SourceExactZipStager.MaximumAggregateOutputBytes +
                            SourceExactInstalledState.MaximumBytes - file.Length)
                        throw new InvalidDataException(
                            "source-exact directory exceeds its byte bound");
                    aggregate += file.Length;
                }
                files.Sort((left, right) =>
                    StringComparer.Ordinal.Compare(left.Name, right.Name));
                var rows = files.Select(file => file.Snapshot(directory.CurrentPath)).ToArray();
                return new ExactDirectoryGuard(
                    directory,
                    files,
                    new ExactDirectorySnapshot(directory.Identity, Array.AsReadOnly(rows)));
            }
            catch
            {
                foreach (var file in files) file.Dispose();
                directory.Dispose();
                throw;
            }
        }

        internal void RequireExact(ExactDirectorySnapshot expected) =>
            RequireExpected(expected, allowPartial: false);

        internal void RequireExpected(ExactDirectorySnapshot expected, bool allowPartial)
        {
            Directory.RequireCurrentPath();
            var current = CurrentSnapshot();
            if (current.Identity != expected.Identity)
                throw new InvalidDataException("source-exact directory physical identity changed");
            if (!allowPartial && !current.Files.SequenceEqual(expected.Files))
                throw new InvalidDataException("source-exact directory membership or bytes changed");
            if (allowPartial && !current.Files.All(actual => expected.Files.Any(row => row == actual)))
                throw new InvalidDataException(
                    "source-exact cleanup directory is not an exact snapshot subset");
        }

        internal void RenameTo(DirectoryLease parent, string destination)
        {
            var expected = CurrentSnapshot();
            // NTFS refuses a directory rename while child handles are retained
            // on some builds even when those handles share DELETE. Keep the
            // write-denying directory lease pinned, close the proved children,
            // perform the atomic rename, then immediately reacquire every child
            // and require the same physical identities and bytes.
            foreach (var file in _files) file.Dispose();
            _files.Clear();
            try
            {
                var directory = Directory;
                directory.RenameTo(parent, destination);
                // The renamed directory is now the accepted namespace. Reopen
                // it without external DELETE sharing so neither the directory
                // nor any proved child can be removed or renamed while backup
                // and journal authority are released.
                directory.Dispose();
                _directory = null;
                using var reacquired = Open(destination, protectDeletes: true);
                reacquired.RequireExact(expected);
                _directory = reacquired._directory;
                reacquired._directory = null;
                _files.AddRange(reacquired._files);
                reacquired._files.Clear();
            }
            catch
            {
                var currentPath = _directory?.CurrentPath ?? destination;
                if (System.IO.Directory.Exists(currentPath))
                {
                    using var reacquired = Open(currentPath, protectDeletes: false);
                    reacquired.RequireExact(expected);
                    _directory?.Dispose();
                    _directory = reacquired._directory;
                    reacquired._directory = null;
                    _files.AddRange(reacquired._files);
                    reacquired._files.Clear();
                }
                throw;
            }
            _ = CurrentSnapshot();
        }

        internal ExactDirectorySnapshot CurrentSnapshot()
        {
            Directory.RequireCurrentPath();
            var names = System.IO.Directory.EnumerateFileSystemEntries(
                    Directory.CurrentPath, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            var expectedNames = _files.Select(file => file.Name).ToArray();
            if (!names.SequenceEqual(expectedNames, StringComparer.Ordinal))
                throw new InvalidDataException(
                    "source-exact directory membership changed while guarded");
            var rows = _files.Select(file => file.Snapshot(Directory.CurrentPath)).ToArray();
            return new ExactDirectorySnapshot(Directory.Identity, Array.AsReadOnly(rows));
        }

        internal void DeleteAll(string checkpointPrefix, Action<string>? checkpoint)
        {
            _ = CurrentSnapshot();
            for (var index = 0; index < _files.Count; index++)
            {
                _files[index].DeleteAndClose();
                checkpoint?.Invoke($"cleanup-{checkpointPrefix}-file-{index}");
            }
            _files.Clear();
            if (System.IO.Directory.EnumerateFileSystemEntries(Directory.CurrentPath).Any())
                throw new IOException(
                    "source-exact directory did not become empty after exact deletion");
            Directory.DeleteWhenEmpty();
            _directory = null;
            checkpoint?.Invoke($"cleanup-{checkpointPrefix}-directory");
        }

        public void Dispose()
        {
            foreach (var file in _files) file.Dispose();
            _files.Clear();
            _directory?.Dispose();
            _directory = null;
        }
    }

    internal sealed class ExclusiveFileLock : IDisposable
    {
        private SafeFileHandle? _handle;
        internal ExclusiveFileLock(SafeFileHandle handle, string path)
        {
            _handle = handle;
            Path = path;
        }
        internal string Path { get; }
        public void Dispose()
        {
            _handle?.Dispose();
            _handle = null;
        }
    }

    private sealed class ExactFileLease : IDisposable
    {
        private FileStream? _stream;
        private readonly ExactIdentity _identity;

        private ExactFileLease(
            string name,
            FileStream stream,
            ExactIdentity identity,
            long length)
        {
            Name = name;
            _stream = stream;
            _identity = identity;
            Length = length;
        }

        internal string Name { get; }
        internal long Length { get; }

        internal static ExactFileLease Open(
            string path,
            string name,
            bool protectDeletes)
        {
            var stream = OpenRegularFile(
                path,
                protectDeletes ? FileShare.Read : FileShare.Read | FileShare.Delete,
                DeleteAccess | GenericRead | FileReadAttributes | Synchronize);
            try
            {
                var identity = GetIdentity(stream.SafeFileHandle, path);
                var basic = GetBasic(stream.SafeFileHandle);
                var standard = GetStandard(stream.SafeFileHandle);
                if ((basic.FileAttributes & ((uint)FileAttributes.Directory |
                     (uint)FileAttributes.ReparsePoint)) != 0 ||
                    (basic.FileAttributes & (uint)FileAttributes.ReadOnly) != 0 ||
                    standard.NumberOfLinks != 1 || standard.Directory != 0 ||
                    standard.EndOfFile < 0 ||
                    !SamePath(FinalPath(stream.SafeFileHandle), path))
                    throw new InvalidDataException(
                        $"source-exact leaf is not one writable, unaliased regular file: {name}");
                RequireNoAlternateStreams(path);
                return new ExactFileLease(name, stream, identity, standard.EndOfFile);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        internal ExactFileSnapshot Snapshot(string directoryPath)
        {
            var stream = _stream ?? throw new ObjectDisposedException(nameof(ExactFileLease));
            var path = Path.Combine(directoryPath, Name);
            if (GetIdentity(stream.SafeFileHandle, path) != _identity ||
                !SamePath(FinalPath(stream.SafeFileHandle), path))
                throw new InvalidDataException($"source-exact guarded leaf moved: {Name}");
            RequireNoAlternateStreams(path);
            stream.Position = 0;
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (stream.Length != Length)
                throw new InvalidDataException(
                    $"source-exact guarded leaf length changed: {Name}");
            return new ExactFileSnapshot(Name, Length, hash, _identity);
        }

        internal void DeleteAndClose()
        {
            var stream = _stream ?? throw new ObjectDisposedException(nameof(ExactFileLease));
            MarkDelete(stream.SafeFileHandle);
            stream.Dispose();
            _stream = null;
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private static FileStream OpenRegularFile(
        string path,
        FileShare share,
        uint access)
    {
        var normalized = Normalize(path);
        var handle = CreateFileW(
            normalized,
            access,
            share,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw Io($"cannot open source-exact file '{normalized}'");
        }
        try
        {
            return new FileStream(handle, FileAccess.Read, 128 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void RequireNoAlternateStreams(string path)
    {
        var handle = FindFirstStreamW(path, FindStreamInfoStandard, out var data, 0);
        if (handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorHandleEof) return;
            throw Io($"cannot enumerate NTFS streams for '{path}'", error);
        }
        try
        {
            var count = 0;
            while (true)
            {
                count++;
                if (count > 16)
                    throw new InvalidDataException(
                        "source-exact object has an excessive NTFS stream set");
                if (!string.Equals(data.StreamName, "::$DATA", StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"source-exact object carries a named NTFS stream: {path}");
                if (!FindNextStreamW(handle, out data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorHandleEof) break;
                    throw Io($"cannot continue NTFS stream enumeration for '{path}'", error);
                }
            }
        }
        finally { FindClose(handle); }
    }

    private static ExactIdentity GetIdentity(SafeFileHandle handle, string context)
    {
        var size = Marshal.SizeOf<FileIdInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetFileInformationByHandleEx(handle, FileIdInfo, buffer, (uint)size))
                throw Io($"cannot read physical identity for '{context}'");
            var value = Marshal.PtrToStructure<FileIdInformation>(buffer);
            return new ExactIdentity(
                value.VolumeSerialNumber,
                value.FileIdLow,
                value.FileIdHigh);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static FileBasicInformation GetBasic(SafeFileHandle handle) =>
        GetInformation<FileBasicInformation>(handle, FileBasicInfo, "basic metadata");

    private static FileStandardInformation GetStandard(SafeFileHandle handle) =>
        GetInformation<FileStandardInformation>(handle, FileStandardInfo, "standard metadata");

    private static T GetInformation<T>(SafeFileHandle handle, int kind, string context)
        where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetFileInformationByHandleEx(handle, kind, buffer, (uint)size))
                throw Io($"cannot read source-exact {context}");
            return Marshal.PtrToStructure<T>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw Io("cannot resolve source-exact final path");
        var value = buffer.ToString();
        return value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)
            ? "\\\\" + value[8..]
            : value.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase)
                ? value[4..]
                : value;
    }

    private static void RenameRelative(
        SafeFileHandle source,
        SafeFileHandle destinationParent,
        string destinationLeaf)
    {
        var name = Encoding.Unicode.GetBytes(destinationLeaf);
        var destinationPinned = false;
        destinationParent.DangerousAddRef(ref destinationPinned);
        try
        {
            var header = new FileRenameInformationHeader
            {
                ReplaceIfExists = 0,
                RootDirectory = destinationParent.DangerousGetHandle(),
                FileNameLength = checked((uint)name.Length),
                FileName = 0
            };
            var fileNameOffset = checked((int)Marshal.OffsetOf<FileRenameInformationHeader>(
                nameof(FileRenameInformationHeader.FileName)));
            // FileName is an inline variable-length field. StructureToPtr writes the
            // complete marshalled header (including native tail padding), while NT
            // consumes FileNameLength bytes from the runtime-derived field offset.
            // The allocation must satisfy both bounds, including a one-character leaf.
            var size = checked(Math.Max(
                Marshal.SizeOf<FileRenameInformationHeader>(),
                fileNameOffset + name.Length));
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (var index = 0; index < size; index++)
                    Marshal.WriteByte(buffer, index, 0);
                Marshal.StructureToPtr(header, buffer, fDeleteOld: false);
                Marshal.Copy(name, 0, IntPtr.Add(buffer, fileNameOffset), name.Length);
                var status = NtSetInformationFile(
                    source,
                    out _,
                    buffer,
                    checked((uint)size),
                    FileRenameInformation);
                if (status < 0)
                    throw new IOException(
                        $"handle-bound source-exact rename failed (NTSTATUS 0x{status:x8})");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally
        {
            if (destinationPinned) destinationParent.DangerousRelease();
        }
    }

    private static void MarkDelete(SafeFileHandle handle)
    {
        var value = new FileDispositionInformation { DeleteFile = 1 };
        if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref value,
                (uint)Marshal.SizeOf<FileDispositionInformation>()))
            throw Io("handle-bound source-exact deletion failed");
    }

    private static IOException Io(string message, int? error = null) =>
        new($"{message} (Win32 {error ?? Marshal.GetLastWin32Error()})");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file, int fileInformationClass, IntPtr fileInformation, uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file, StringBuilder path, uint characterCount, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file, int fileInformationClass,
        ref FileDispositionInformation fileInformation, uint bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(
        SafeFileHandle file,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        IntPtr objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(
        string fileName, int infoLevel, out Win32FindStreamData data, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr findStream, out Win32FindStreamData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(
        string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string rootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName, StringBuilder? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength,
        out uint fileSystemFlags, StringBuilder fileSystemNameBuffer, int fileSystemNameSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        internal ulong VolumeSerialNumber;
        internal ulong FileIdLow;
        internal ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending;
        internal byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileCaseSensitiveInformation { internal uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation { internal byte DeleteFile; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal IntPtr Status;
        internal IntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal uint Length;
        internal IntPtr RootDirectory;
        internal IntPtr ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInformationHeader
    {
        internal byte ReplaceIfExists;
        internal IntPtr RootDirectory;
        internal uint FileNameLength;
        internal ushort FileName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        internal long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        internal string StreamName;
    }
}

internal sealed record ExactDirectorySnapshot(
    ExactIdentity Identity,
    IReadOnlyList<ExactFileSnapshot> Files)
{
    internal bool EqualsByValue(ExactDirectorySnapshot other) =>
        Identity == other.Identity && Files.SequenceEqual(other.Files);
}

internal sealed record ExactFileSnapshot(
    string Name,
    long Length,
    string Sha256,
    ExactIdentity Identity);

internal readonly record struct ExactIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh);

internal enum ExactSnapshotMatch { Absent, Exact, ExactSubset, Foreign }

internal sealed record FileWitness(string Path, byte[] Bytes, ExactIdentity Identity);
internal sealed record BoundedFile(string Path, byte[] Bytes, ExactIdentity Identity);

internal sealed class SourceExactLockUnavailableException : IOException;
