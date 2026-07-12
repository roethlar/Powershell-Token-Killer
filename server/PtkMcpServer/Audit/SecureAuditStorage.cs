using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PtkMcpServer.Audit;

internal readonly record struct ProtectedFileIdentity(
    ulong Device,
    ulong InodeHigh,
    ulong InodeLow);

/// <summary>
/// Named fault boundaries used by the audit-storage tests. The callback is
/// invoked immediately before the corresponding irreversible operation.
/// </summary>
public enum SecureAuditStorageFaultStage
{
    Write,
    Flush,
    Publish,
}

/// <summary>
/// Security-sensitive filesystem primitives shared by audit stores. Every
/// exception from this type is caught and sanitized at the public store
/// boundary; consequently these helpers may use the platform APIs directly.
/// </summary>
internal static class SecureAuditStorage
{
    internal const UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode OwnerFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static string PrepareRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("The protected storage root must be an absolute path.", nameof(rootPath));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (string.Equals(root, Path.GetPathRoot(root), PathComparison))
        {
            throw new ArgumentException("The protected storage root cannot be a filesystem root.", nameof(rootPath));
        }

        RefuseLinkedPathComponents(root);
        CreateProtectedDirectoryTree(root);
        RefuseLinkedPathComponents(root);

        if (!Directory.Exists(root) || File.Exists(root))
        {
            throw new IOException("The protected storage root is not a directory.");
        }

        ProtectDirectory(root);
        RefuseLinkedPathComponents(root);
        return root;
    }

    private static void CreateProtectedDirectoryTree(string root)
    {
        var missing = new Stack<string>();
        for (var component = new DirectoryInfo(root); !component.Exists; component = component.Parent!)
        {
            RefuseLinkOrReparsePoint(component.FullName);
            missing.Push(component.FullName);
            if (component.Parent is null)
                throw new IOException("The protected storage parent is unavailable.");
        }

        while (missing.TryPop(out var directory))
        {
            Directory.CreateDirectory(directory);
            RefuseLinkOrReparsePoint(directory);
            ProtectDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                // Persist both the new directory inode/mode and its name in
                // the parent before any evidence/journal file can authorize
                // user effects beneath it.
                FlushUnixDirectory(directory);
                FlushUnixDirectory(Path.GetDirectoryName(directory)!);
            }
        }
    }

    internal static FileStream CreateExclusiveFile(
        string path,
        long preallocationSize = 0,
        FileShare share = FileShare.None)
    {
        RefuseExistingPath(path);
        if (preallocationSize < 0)
            throw new ArgumentOutOfRangeException(nameof(preallocationSize));

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = share,
            BufferSize = 16 * 1024,
            Options = FileOptions.WriteThrough,
            PreallocationSize = preallocationSize,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerFileMode;
        }

        var stream = new FileStream(path, options);
        try
        {
            ProtectFile(path);
            RefuseLinkOrReparsePoint(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            TryDelete(path);
            throw;
        }
    }

    internal static void PublishAtomically(
        string temporaryPath,
        string publishedPath,
        string root,
        Action? destinationCheckedForTests = null)
    {
        RefuseLinkedPathComponents(root);
        RefuseLinkOrReparsePoint(temporaryPath);
        RefuseExistingPath(publishedPath);
        destinationCheckedForTests?.Invoke();

        if (OperatingSystem.IsWindows())
        {
            if (!WindowsNative.MoveFileEx(
                    temporaryPath,
                    publishedPath,
                    WindowsNative.MoveFileFlags.WriteThrough))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        else
        {
            // POSIX rename may replace a destination created after the
            // precheck. Creating a hard link is the portable, atomic
            // no-replace publish primitive for two names in this one root.
            if (UnixNative.CreateHardLink(temporaryPath, publishedPath) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                File.Delete(temporaryPath);
                FlushUnixDirectory(root);
            }
            catch
            {
                // A name that was not durably published must never escape as
                // a usable reference. Remove it and best-effort persist that
                // removal before reporting the generic storage failure.
                TryDelete(publishedPath);
                TryFlushUnixDirectory(root);
                throw;
            }
        }

        try
        {
            RefuseLinkedPathComponents(root);
            RefuseLinkOrReparsePoint(publishedPath);
            VerifyFileProtection(publishedPath);
        }
        catch
        {
            TryDelete(publishedPath);
            if (!OperatingSystem.IsWindows())
            {
                TryFlushUnixDirectory(root);
            }

            throw;
        }
    }

    internal static void ReplaceAtomically(
        string temporaryPath,
        string publishedPath,
        string root,
        Action? destinationReplacedForTests = null,
        Action? directoryFlushStartingForTests = null)
    {
        // The caller must close and durably flush the protected temporary file
        // before entering this primitive. Replacement makes those exact bytes
        // visible; it does not attempt to stabilize a still-open writer.
        var protectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        VerifyExternalProtectedDirectory(protectedRoot);
        temporaryPath = RequireDirectChild(protectedRoot, temporaryPath);
        publishedPath = RequireDirectChild(protectedRoot, publishedPath);
        if (string.Equals(temporaryPath, publishedPath, PathComparison))
            throw new IOException("Atomic replacement requires two distinct protected files.");
        VerifyExternalProtectedFile(temporaryPath);
        VerifyExternalProtectedFile(publishedPath);

        if (OperatingSystem.IsWindows())
        {
            if (!WindowsNative.MoveFileEx(
                    temporaryPath,
                    publishedPath,
                    WindowsNative.MoveFileFlags.ReplaceExisting |
                    WindowsNative.MoveFileFlags.WriteThrough))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        else
        {
            if (UnixNative.ReplaceFile(temporaryPath, publishedPath) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        // This seam intentionally precedes the Unix directory fsync. It proves
        // that every failure after the OS has replaced the name preserves the
        // complete new file. The caller reloads it to distinguish an old-or-
        // new outcome; deleting it here would turn uncertainty into state loss.
        destinationReplacedForTests?.Invoke();
        if (!OperatingSystem.IsWindows())
        {
            directoryFlushStartingForTests?.Invoke();
            FlushUnixDirectory(protectedRoot);
        }
        VerifyExternalProtectedDirectory(protectedRoot);
        VerifyExternalProtectedFile(publishedPath);
        if (File.Exists(temporaryPath))
            throw new IOException("The atomic replacement left its temporary file published.");
    }

    internal static void ConfirmAtomicReplacementDurability(
        string root,
        string publishedPath)
    {
        var protectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        VerifyExternalProtectedDirectory(protectedRoot);
        publishedPath = RequireDirectChild(protectedRoot, publishedPath);
        VerifyExternalProtectedFile(publishedPath);
        if (!OperatingSystem.IsWindows())
            FlushUnixDirectory(protectedRoot);
        VerifyExternalProtectedDirectory(protectedRoot);
        VerifyExternalProtectedFile(publishedPath);
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // The original, sanitized store failure remains authoritative.
        }
    }

    internal static void VerifyProtectedFile(string path)
    {
        RefuseLinkedPathComponents(path);
        RefuseLinkOrReparsePoint(path);
        if (!File.Exists(path))
            throw new IOException("The protected file is missing.");
        VerifyFileProtection(path);
    }

    internal static void ProtectExistingFile(string path)
    {
        RefuseLinkedPathComponents(path);
        RefuseLinkOrReparsePoint(path);
        if (!File.Exists(path) || Directory.Exists(path))
            throw new IOException("The file to protect is missing.");
        ProtectFile(path);
        RefuseLinkedPathComponents(path);
        RefuseLinkOrReparsePoint(path);
        VerifyFileProtection(path);
    }

    internal static (
        long LogicalBytes,
        long AllocatedBytes,
        long AllocationUnitBytes) GetMacFileAllocation(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("macOS file allocation is only available on macOS.");
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
            throw new IOException("The file whose allocation was requested is not open.");
        var allocation = UnixNative.GetMacFileAllocation(handle);
        if (allocation.LogicalBytes < 0 ||
            allocation.AllocatedBlocks < 0 ||
            allocation.AllocationUnitBytes <= 0)
        {
            throw new IOException("The macOS file allocation metadata is invalid.");
        }

        return (
            allocation.LogicalBytes,
            checked(allocation.AllocatedBlocks * 512L),
            allocation.AllocationUnitBytes);
    }

    internal static byte[] ReadProtectedFile(
        string path,
        int maximumBytes,
        bool requireProtectedParent = false,
        bool verifyWithoutMutation = false,
        FileShare share = FileShare.Read)
    {
        if (maximumBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if ((share & ~(FileShare.Read | FileShare.Delete)) != 0)
            throw new ArgumentOutOfRangeException(nameof(share));

        var parent = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new IOException("The protected file parent is unavailable.");
        if (requireProtectedParent)
        {
            if (verifyWithoutMutation) VerifyExternalProtectedDirectory(parent);
            else VerifyProtectedDirectory(parent);
        }
        if (verifyWithoutMutation) VerifyExternalProtectedFile(path);
        else VerifyProtectedFile(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            share,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length < 0 || stream.Length > maximumBytes)
            throw new IOException("The protected file exceeds its configured bound.");

        var bytes = new byte[checked((int)stream.Length)];
        try
        {
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
                throw new IOException("The protected file changed while it was read.");

            if (verifyWithoutMutation) VerifyExternalProtectedFile(path);
            else VerifyProtectedFile(path);
            if (requireProtectedParent)
            {
                if (verifyWithoutMutation) VerifyExternalProtectedDirectory(parent);
                else VerifyProtectedDirectory(parent);
            }
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    internal static void VerifyExternalProtectedFile(string path)
    {
        RefuseLinkedPathComponents(path);
        RefuseLinkOrReparsePoint(path);
        if (!File.Exists(path) || Directory.Exists(path))
            throw new IOException("The protected external file is missing.");
        if (OperatingSystem.IsWindows())
        {
            VerifyWindowsOwnerOnlyAcl(path, isDirectory: false);
            return;
        }
        VerifyUnixProtection(path, OwnerFileMode, "file");
    }

    internal static void VerifyExternalProtectedDirectory(string path)
    {
        RefuseLinkedPathComponents(path);
        RefuseLinkOrReparsePoint(path);
        if (!Directory.Exists(path) || File.Exists(path))
            throw new IOException("The protected external directory is missing.");
        if (OperatingSystem.IsWindows())
        {
            VerifyWindowsOwnerOnlyAcl(path, isDirectory: true);
            return;
        }
        VerifyUnixProtection(path, OwnerDirectoryMode, "directory");
    }

    /// <summary>
    /// Proves that a retained no-share handle still names the protected path
    /// that was inventoried. A single link is required so two segment names
    /// cannot alias one inode/file ID.
    /// </summary>
    internal static ProtectedFileIdentity VerifyRetainedProtectedFileIdentity(
        string path,
        SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
            throw new IOException("The protected file identity handle is unavailable.");
        if (OperatingSystem.IsWindows())
            return WindowsNative.GetProtectedFileIdentity(path, handle);
        VerifyExternalProtectedFile(path);
        return UnixNative.GetProtectedFileIdentity(path, handle);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsOwnerOnlyAcl(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User
            ?? throw new IOException("The current Windows user SID is unavailable.");
        var descriptorBytes = security.GetSecurityDescriptorBinaryForm();
        var descriptor = new RawSecurityDescriptor(descriptorBytes, 0);
        if (descriptor.Owner is null ||
            !currentUser.Equals(descriptor.Owner) ||
            !descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected) ||
            descriptor.DiscretionaryAcl is not { Count: 1 } dacl ||
            dacl[0] is not CommonAce ace ||
            ace.AceQualifier != AceQualifier.AccessAllowed ||
            ace.IsCallback ||
            ace.AceFlags != AceFlags.None ||
            !currentUser.Equals(ace.SecurityIdentifier) ||
            ace.AccessMask != (int)FileSystemRights.FullControl)
        {
            throw new IOException("The protected external path owner or DACL is invalid.");
        }
    }

    internal static void VerifyProtectedDirectory(string path)
    {
        RefuseLinkedPathComponents(path);
        if (!Directory.Exists(path) || File.Exists(path))
            throw new IOException("The protected directory is missing.");
        if (OperatingSystem.IsWindows())
        {
            WindowsNative.SetCurrentUserOwnerOnlyAcl(path);
            return;
        }

        VerifyUnixProtection(path, OwnerDirectoryMode, "directory");
    }

    internal static void ProbeWritableDirectory(string root)
    {
        VerifyProtectedDirectory(root);
        var path = Path.Combine(root, $".ptk-audit-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = CreateExclusiveFile(path))
            {
                stream.WriteByte(0x50);
                stream.Flush(flushToDisk: true);
            }
            File.Delete(path);
            if (!OperatingSystem.IsWindows()) FlushUnixDirectory(root);
        }
        catch
        {
            TryDelete(path);
            if (!OperatingSystem.IsWindows()) TryFlushUnixDirectory(root);
            throw;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string RequireDirectChild(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new IOException("The protected file parent is unavailable.");
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(parent),
                root,
                PathComparison))
        {
            throw new IOException("Atomic protected files must share one direct parent.");
        }

        return fullPath;
    }

    private static void ProtectDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsNative.SetCurrentUserOwnerOnlyAcl(path);
            return;
        }

        VerifyCurrentUnixOwner(path);
        if (OperatingSystem.IsMacOS())
        {
            MacNative.RemoveExtendedAcl(path);
        }

        File.SetUnixFileMode(path, OwnerDirectoryMode);
        VerifyUnixProtection(path, OwnerDirectoryMode, "directory");
    }

    private static void ProtectFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsNative.SetCurrentUserOwnerOnlyAcl(path);
            return;
        }

        VerifyCurrentUnixOwner(path);
        if (OperatingSystem.IsMacOS())
        {
            MacNative.RemoveExtendedAcl(path);
        }

        File.SetUnixFileMode(path, OwnerFileMode);
        VerifyFileProtection(path);
    }

    private static void VerifyFileProtection(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // SetNamedSecurityInfo is the fail-closed Windows verification
            // boundary: it either makes the current token user the owner and
            // installs the protected one-user DACL, or returns an error.
            // Re-apply here to close a replacement race.
            WindowsNative.SetCurrentUserOwnerOnlyAcl(path);
            return;
        }

        VerifyUnixProtection(path, OwnerFileMode, "file");
    }

    private static void VerifyUnixProtection(
        string path,
        UnixFileMode expectedMode,
        string kind)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        VerifyCurrentUnixOwner(path);
        if (File.GetUnixFileMode(path) != expectedMode)
        {
            throw new IOException($"The protected {kind} permissions could not be verified.");
        }

        if (OperatingSystem.IsMacOS())
        {
            MacNative.VerifyNoExtendedAcl(path);
            // acl_get_file reports a missing ACL and a missing path with the
            // same Darwin errno. Re-check ownership so disappearance or
            // replacement cannot be accepted as "no ACL".
            VerifyCurrentUnixOwner(path);
        }
    }

    private static void VerifyCurrentUnixOwner(string path) =>
        VerifyUnixOwner(path, UnixNative.geteuid());

    internal static void VerifyUnixOwner(string path, uint expectedOwnerId)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var actualOwnerId = UnixNative.GetOwnerId(path);
        if (actualOwnerId != expectedOwnerId)
        {
            throw new IOException("The protected path is not owned by the current effective user.");
        }
    }

    private static void RefuseExistingPath(string path)
    {
        if (PathExistsWithoutFollowing(path) || File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("The protected destination already exists.");
        }
    }

    private static void RefuseLinkOrReparsePoint(string path)
    {
        if (IsLinkOrReparsePoint(path))
        {
            throw new IOException("A protected storage path cannot be a link or reparse point.");
        }
    }

    private static void RefuseLinkedPathComponents(string path)
    {
        // A clean final component is insufficient: ~/.ptk may itself be a
        // symlink or Windows junction that redirects the entire audit tree.
        // Walk lexically rather than resolving so every redirect remains
        // visible and is rejected before protected content is written.
        DirectoryInfo? component = new(path);
        while (component is not null)
        {
            RefuseLinkOrReparsePoint(component.FullName);
            component = component.Parent;
        }
    }

    private static bool PathExistsWithoutFollowing(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null ||
                   new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool IsLinkOrReparsePoint(string path)
    {
        if (PathExistsWithoutFollowing(path))
        {
            return true;
        }

        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void FlushUnixDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var flags = OperatingSystem.IsLinux()
            ? UnixNative.LinuxOpenDirectoryFlags
            : OperatingSystem.IsMacOS()
                ? UnixNative.MacOpenDirectoryFlags
                : throw new PlatformNotSupportedException(
                    "Durable protected storage is not implemented on this Unix platform.");

        var descriptor = UnixNative.open(directory, flags);
        if (descriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            if (UnixNative.fsync(descriptor) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            _ = UnixNative.close(descriptor);
        }
    }

    private static void TryFlushUnixDirectory(string directory)
    {
        try
        {
            FlushUnixDirectory(directory);
        }
        catch
        {
            // Cleanup durability is best effort after the primary failure.
        }
    }

    private static class UnixNative
    {
        private const int AtCurrentWorkingDirectory = -100;
        private const int AtSymlinkNoFollow = 0x100;
        private const int AtEmptyPath = 0x1000;
        private const uint StatxBasicStats = 0x000007ff;
        private const uint RequiredRetainedStatxFields =
            0x00000001 | // STATX_TYPE
            0x00000002 | // STATX_MODE
            0x00000004 | // STATX_NLINK
            0x00000008 | // STATX_UID
            0x00000100;  // STATX_INO
        private const ushort FileTypeMask = 0xf000;
        private const ushort RegularFile = 0x8000;
        private const ushort PermissionMask = 0x01ff;
        private const ushort OwnerReadWrite = 0x0180;

        // Linux preserves legacy open(2) flag values on ARM/ARM64 and
        // PowerPC. The other .NET-supported Linux architectures use the
        // asm-generic values. Passing the generic O_DIRECTORY/O_NOFOLLOW
        // bits on ARM64 makes open fail with EINVAL before audit storage can
        // start.
        internal static int LinuxOpenDirectoryFlags =>
            (RuntimeInformation.ProcessArchitecture is
                Architecture.Arm or
                Architecture.Arm64 or
                Architecture.Armv6 or
                Architecture.Ppc64le
                    ? 0x4000 | 0x8000
                    : 0x10000 | 0x20000)
            | 0x80000;
        internal const int MacOpenDirectoryFlags = 0x100000 | 0x100 | 0x1000000;

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct LinuxStatx
        {
            [FieldOffset(0)]
            internal uint Mask;
            [FieldOffset(16)]
            internal uint LinkCount;
            [FieldOffset(20)]
            internal uint UserId;
            [FieldOffset(28)]
            internal ushort Mode;
            [FieldOffset(32)]
            internal ulong Inode;
            [FieldOffset(136)]
            internal uint DeviceMajor;
            [FieldOffset(140)]
            internal uint DeviceMinor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MacTimespec
        {
            internal long Seconds;
            internal long Nanoseconds;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MacStat
        {
            internal int Device;
            internal ushort Mode;
            internal ushort LinkCount;
            internal ulong Inode;
            internal uint UserId;
            internal uint GroupId;
            internal int SpecialDevice;
            internal MacTimespec AccessTime;
            internal MacTimespec ModificationTime;
            internal MacTimespec ChangeTime;
            internal MacTimespec BirthTime;
            internal long Size;
            internal long Blocks;
            internal int BlockSize;
            internal uint Flags;
            internal uint Generation;
            internal int Spare;
            internal long Reserved1;
            internal long Reserved2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MacStatVfs
        {
            internal ulong BlockSize;
            internal ulong FundamentalBlockSize;
            internal uint TotalBlocks;
            internal uint FreeBlocks;
            internal uint AvailableBlocks;
            internal uint TotalFiles;
            internal uint FreeFiles;
            internal uint AvailableFiles;
            internal ulong FileSystemId;
            internal ulong Flags;
            internal ulong MaximumNameLength;
        }

        internal static uint GetOwnerId(string path)
        {
            if (OperatingSystem.IsLinux())
            {
                if (statx(
                        AtCurrentWorkingDirectory,
                        path,
                        AtSymlinkNoFollow,
                        StatxBasicStats,
                        out var status) != 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                return status.UserId;
            }

            if (OperatingSystem.IsMacOS())
            {
                MacStat status;
                var statResult = RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? lstat_inode64(path, out status)
                    : lstat(path, out status);
                if (statResult != 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                return status.UserId;
            }

            throw new PlatformNotSupportedException(
                "Protected storage ownership verification is not implemented on this Unix platform.");
        }

        internal static ProtectedFileIdentity GetProtectedFileIdentity(
            string path,
            SafeFileHandle handle)
        {
            if (OperatingSystem.IsLinux())
            {
                if (statx(
                        AtCurrentWorkingDirectory,
                        path,
                        AtSymlinkNoFollow,
                        StatxBasicStats,
                        out var pathStatus) != 0 ||
                    statx(
                        handle.DangerousGetHandle().ToInt32(),
                        string.Empty,
                        AtEmptyPath | AtSymlinkNoFollow,
                        StatxBasicStats,
                        out var handleStatus) != 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                ValidateLinuxRetainedFile(pathStatus);
                ValidateLinuxRetainedFile(handleStatus);
                var pathDevice = ((ulong)pathStatus.DeviceMajor << 32) |
                                 pathStatus.DeviceMinor;
                var handleDevice = ((ulong)handleStatus.DeviceMajor << 32) |
                                   handleStatus.DeviceMinor;
                if (pathDevice != handleDevice || pathStatus.Inode != handleStatus.Inode)
                {
                    throw new IOException(
                        "The protected path no longer names its retained file handle.");
                }
                return new ProtectedFileIdentity(pathDevice, 0, pathStatus.Inode);
            }

            if (OperatingSystem.IsMacOS())
            {
                MacStat pathStatus;
                MacStat handleStatus;
                var pathResult = RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? lstat_inode64(path, out pathStatus)
                    : lstat(path, out pathStatus);
                var handleResult = RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? fstat_inode64(handle, out handleStatus)
                    : fstat(handle, out handleStatus);
                if (pathResult != 0 || handleResult != 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());

                ValidateMacRetainedFile(pathStatus);
                ValidateMacRetainedFile(handleStatus);
                if (pathStatus.Device != handleStatus.Device ||
                    pathStatus.Inode != handleStatus.Inode)
                {
                    throw new IOException(
                        "The protected path no longer names its retained file handle.");
                }
                return new ProtectedFileIdentity(
                    unchecked((uint)pathStatus.Device),
                    0,
                    pathStatus.Inode);
            }

            throw new PlatformNotSupportedException(
                "Protected file identity verification is not implemented on this Unix platform.");
        }

        private static void ValidateLinuxRetainedFile(LinuxStatx status)
        {
            if ((status.Mask & RequiredRetainedStatxFields) != RequiredRetainedStatxFields ||
                (status.Mode & FileTypeMask) != RegularFile ||
                (status.Mode & PermissionMask) != OwnerReadWrite ||
                status.UserId != geteuid() ||
                status.LinkCount != 1)
            {
                throw new IOException(
                    "The retained audit segment type, owner, mode, or link count is invalid.");
            }
        }

        private static void ValidateMacRetainedFile(MacStat status)
        {
            if ((status.Mode & FileTypeMask) != RegularFile ||
                (status.Mode & PermissionMask) != OwnerReadWrite ||
                status.UserId != geteuid() ||
                status.LinkCount != 1)
            {
                throw new IOException(
                    "The retained audit segment type, owner, mode, or link count is invalid.");
            }
        }

        internal static (
            long LogicalBytes,
            long AllocatedBlocks,
            long AllocationUnitBytes) GetMacFileAllocation(SafeFileHandle handle)
        {
            MacStat status;
            var statResult = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? fstat_inode64(handle, out status)
                : fstat(handle, out status);
            if (statResult != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (fstatvfs(handle, out var volume) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var allocationUnit = volume.FundamentalBlockSize != 0
                ? volume.FundamentalBlockSize
                : volume.BlockSize;
            if (allocationUnit > long.MaxValue)
                throw new IOException("The macOS allocation unit is too large.");
            return (status.Size, status.Blocks, checked((long)allocationUnit));
        }

        [DllImport("libc", SetLastError = true)]
        internal static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        internal static extern uint geteuid();

        [DllImport("libc", SetLastError = true)]
        private static extern int statx(
            int directoryFileDescriptor,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int flags,
            uint mask,
            out LinuxStatx status);

        [DllImport("libc", SetLastError = true)]
        private static extern int lstat(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            out MacStat status);

        [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
        private static extern int lstat_inode64(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            out MacStat status);

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int fstat(
            SafeFileHandle file,
            out MacStat status);

        [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
        private static extern int fstat_inode64(
            SafeFileHandle file,
            out MacStat status);

        [DllImport("libc", EntryPoint = "fstatvfs", SetLastError = true)]
        private static extern int fstatvfs(
            SafeFileHandle file,
            out MacStatVfs status);

        [DllImport("libc", SetLastError = true)]
        internal static extern int fsync(int fileDescriptor);

        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        internal static extern int CreateHardLink(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

        [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
        internal static extern int ReplaceFile(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

        [DllImport("libc", SetLastError = true)]
        internal static extern int close(int fileDescriptor);
    }

    private static class MacNative
    {
        private const int ExtendedAclType = 0x00000100;
        private const int NoEntryOrPath = 2;

        internal static void RemoveExtendedAcl(string path)
        {
            var emptyAcl = acl_init(0);
            if (emptyAcl == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                if (acl_set_file(path, ExtendedAclType, emptyAcl) != 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            finally
            {
                _ = acl_free(emptyAcl);
            }
        }

        internal static void VerifyNoExtendedAcl(string path)
        {
            Marshal.SetLastPInvokeError(0);
            var acl = acl_get_file(path, ExtendedAclType);
            if (acl != IntPtr.Zero)
            {
                _ = acl_free(acl);
                throw new IOException("The protected path has an extended access-control list.");
            }

            var error = Marshal.GetLastPInvokeError();
            if (error != NoEntryOrPath)
            {
                throw new Win32Exception(error);
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr acl_init(int count);

        [DllImport("libc", SetLastError = true)]
        private static extern int acl_set_file(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int type,
            IntPtr acl);

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr acl_get_file(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int type);

        [DllImport("libc", SetLastError = true)]
        private static extern int acl_free(IntPtr value);
    }

    private static class WindowsNative
    {
        private const uint TokenQuery = 0x0008;
        private const int TokenUser = 1;
        private const uint FileAllAccess = 0x001F01FF;
        private const uint OwnerSecurityInformation = 0x00000001;
        private const uint DaclSecurityInformation = 0x00000004;
        private const uint ProtectedDaclSecurityInformation = 0x80000000;
        private const int SetAccess = 2;
        private const int TrusteeIsSid = 0;
        private const int TrusteeIsUser = 1;
        private const int SeFileObject = 1;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeReparsePoint = 0x00000400;

        [Flags]
        internal enum MoveFileFlags : uint
        {
            ReplaceExisting = 0x00000001,
            WriteThrough = 0x00000008,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Trustee
        {
            internal IntPtr MultipleTrustee;
            internal int MultipleTrusteeOperation;
            internal int TrusteeForm;
            internal int TrusteeType;
            internal IntPtr Name;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExplicitAccess
        {
            internal uint AccessPermissions;
            internal int AccessMode;
            internal uint Inheritance;
            internal Trustee Trustee;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            internal uint Low;
            internal uint High;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal NativeFileTime CreationTime;
            internal NativeFileTime LastAccessTime;
            internal NativeFileTime LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        internal static ProtectedFileIdentity GetProtectedFileIdentity(
            string expectedPath,
            SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var information))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if ((information.FileAttributes &
                    (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 ||
                information.NumberOfLinks != 1)
            {
                throw new IOException(
                    "The retained audit segment type or link count is invalid.");
            }

            var pathBuffer = new StringBuilder(32_768);
            var length = GetFinalPathNameByHandle(
                handle,
                pathBuffer,
                pathBuffer.Capacity,
                flags: 0);
            if (length == 0 || length >= pathBuffer.Capacity)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var finalPath = NormalizeFinalPath(pathBuffer.ToString());
            if (!string.Equals(
                    Path.GetFullPath(expectedPath),
                    Path.GetFullPath(finalPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "The protected path no longer names its retained file handle.");
            }

            return new ProtectedFileIdentity(
                information.VolumeSerialNumber,
                information.FileIndexHigh,
                information.FileIndexLow);
        }

        private static string NormalizeFinalPath(string path)
        {
            const string uncPrefix = @"\\?\UNC\";
            const string localPrefix = @"\\?\";
            if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
                return @"\\" + path[uncPrefix.Length..];
            return path.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase)
                ? path[localPrefix.Length..]
                : path;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SidAndAttributes
        {
            internal IntPtr Sid;
            internal uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenUserData
        {
            internal SidAndAttributes User;
        }

        internal static void SetCurrentUserOwnerOnlyAcl(string path)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            IntPtr tokenUserBuffer = IntPtr.Zero;
            IntPtr acl = IntPtr.Zero;
            try
            {
                _ = GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var required);
                if (required <= 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                tokenUserBuffer = Marshal.AllocHGlobal(required);
                if (!GetTokenInformation(token, TokenUser, tokenUserBuffer, required, out _))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                var tokenUser = Marshal.PtrToStructure<TokenUserData>(tokenUserBuffer);
                var access = new ExplicitAccess
                {
                    AccessPermissions = FileAllAccess,
                    AccessMode = SetAccess,
                    Inheritance = 0,
                    Trustee = new Trustee
                    {
                        MultipleTrustee = IntPtr.Zero,
                        MultipleTrusteeOperation = 0,
                        TrusteeForm = TrusteeIsSid,
                        TrusteeType = TrusteeIsUser,
                        Name = tokenUser.User.Sid,
                    },
                };

                var aclResult = SetEntriesInAcl(1, ref access, IntPtr.Zero, out acl);
                if (aclResult != 0)
                {
                    throw new Win32Exception(unchecked((int)aclResult));
                }

                var securityResult = SetNamedSecurityInfo(
                    path,
                    SeFileObject,
                    OwnerSecurityInformation |
                    DaclSecurityInformation |
                    ProtectedDaclSecurityInformation,
                    tokenUser.User.Sid,
                    IntPtr.Zero,
                    acl,
                    IntPtr.Zero);
                if (securityResult != 0)
                {
                    throw new Win32Exception(unchecked((int)securityResult));
                }
            }
            finally
            {
                if (acl != IntPtr.Zero)
                {
                    _ = LocalFree(acl);
                }

                if (tokenUserBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(tokenUserBuffer);
                }

                _ = CloseHandle(token);
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            IntPtr tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint SetEntriesInAcl(
            int countOfExplicitEntries,
            ref ExplicitAccess explicitEntry,
            IntPtr oldAcl,
            out IntPtr newAcl);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint SetNamedSecurityInfo(
            string objectName,
            int objectType,
            uint securityInfo,
            IntPtr owner,
            IntPtr group,
            IntPtr dacl,
            IntPtr sacl);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MoveFileEx(string existingFileName, string newFileName, MoveFileFlags flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetFinalPathNameByHandleW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder path,
            int pathCharacters,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
