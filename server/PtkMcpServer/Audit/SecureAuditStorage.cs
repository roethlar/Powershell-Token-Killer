using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PtkMcpServer.Audit;

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

    internal static byte[] ReadProtectedFile(string path, int maximumBytes)
    {
        if (maximumBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        VerifyProtectedFile(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
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

            VerifyProtectedFile(path);
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
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
        private const uint StatxBasicStats = 0x000007ff;

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
            [FieldOffset(20)]
            internal uint UserId;
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
                if (lstat(path, out var status) != 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                return status.UserId;
            }

            throw new PlatformNotSupportedException(
                "Protected storage ownership verification is not implemented on this Unix platform.");
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

        [DllImport("libc", SetLastError = true)]
        internal static extern int fsync(int fileDescriptor);

        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        internal static extern int CreateHardLink(
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

        [Flags]
        internal enum MoveFileFlags : uint
        {
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
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
