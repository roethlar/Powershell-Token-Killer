using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PtkSiemReceiver.Security;

internal readonly record struct UnixPathMetadata(
    ProtectedPathIdentity Identity,
    bool IsRegularFile,
    bool IsDirectory,
    uint UserId,
    uint LinkCount,
    UnixFileMode Mode);

internal static class UnixProtectedPathNative
{
    private const int AtCurrentWorkingDirectory = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x000007ff;
    private const uint RequiredStatxFields =
        0x00000001 | // STATX_TYPE
        0x00000002 | // STATX_MODE
        0x00000004 | // STATX_NLINK
        0x00000008 | // STATX_UID
        0x00000100;  // STATX_INO
    private const ushort FileTypeMask = 0xf000;
    private const ushort RegularFile = 0x8000;
    private const ushort Directory = 0x4000;
    private const ushort PermissionMask = 0x0fff;

    internal static uint EffectiveUserId => geteuid();

    internal static SafeFileHandle OpenFileForRead(string path)
    {
        var flags = OperatingSystem.IsLinux()
            ? LinuxNoFollowFlag | 0x800 | 0x80000
            : OperatingSystem.IsMacOS()
                ? 0x4 | 0x100 | 0x1000000
                : throw new PlatformNotSupportedException();
        return Open(path, flags);
    }

    internal static SafeFileHandle OpenFileForReadAt(
        SafeFileHandle directory,
        string fileName)
    {
        if (string.IsNullOrEmpty(fileName) ||
            fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ProtectedPathException(ProtectedPathFailureKind.InvalidPath);
        }

        var flags = OperatingSystem.IsLinux()
            ? LinuxNoFollowFlag | 0x800 | 0x80000
            : OperatingSystem.IsMacOS()
                ? 0x4 | 0x100 | 0x1000000
                : throw new PlatformNotSupportedException();
        var retained = false;
        try
        {
            directory.DangerousAddRef(ref retained);
            var descriptor = openat(
                directory.DangerousGetHandle().ToInt32(),
                fileName,
                flags);
            if (descriptor < 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }
        finally
        {
            if (retained)
                directory.DangerousRelease();
        }
    }

    internal static SafeFileHandle OpenDirectory(string path)
    {
        var flags = OperatingSystem.IsLinux()
            ? LinuxDirectoryFlag | LinuxNoFollowFlag | 0x80000
            : OperatingSystem.IsMacOS()
                ? 0x100000 | 0x100 | 0x1000000
                : throw new PlatformNotSupportedException();
        return Open(path, flags);
    }

    internal static UnixPathMetadata GetPathMetadata(string path)
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
            return FromLinux(status);
        }

        if (OperatingSystem.IsMacOS())
        {
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? lstat_inode64(path, out var status)
                : lstat(path, out status);
            if (result != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return FromMac(status);
        }

        throw new PlatformNotSupportedException();
    }

    internal static UnixPathMetadata GetRetainedMetadata(
        string path,
        SafeFileHandle handle)
    {
        var pathMetadata = GetPathMetadata(path);
        UnixPathMetadata handleMetadata;
        if (OperatingSystem.IsLinux())
        {
            var retained = false;
            try
            {
                handle.DangerousAddRef(ref retained);
                if (statx(
                        handle.DangerousGetHandle().ToInt32(),
                        string.Empty,
                        AtEmptyPath | AtSymlinkNoFollow,
                        StatxBasicStats,
                        out var status) != 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                handleMetadata = FromLinux(status);
            }
            finally
            {
                if (retained)
                    handle.DangerousRelease();
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? fstat_inode64(handle, out var status)
                : fstat(handle, out status);
            if (result != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            handleMetadata = FromMac(status);
        }
        else
        {
            throw new PlatformNotSupportedException();
        }

        if (pathMetadata.Identity != handleMetadata.Identity)
            throw new ProtectedPathException(ProtectedPathFailureKind.Changed);
        return handleMetadata;
    }

    internal static void SetMode(SafeFileHandle handle, UnixFileMode mode)
    {
        if (fchmod(handle, (uint)mode) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    internal static bool IsIdentityOpen(ProtectedPathIdentity expectedIdentity)
    {
        var descriptorDirectory = OperatingSystem.IsLinux()
            ? "/proc/self/fd"
            : OperatingSystem.IsMacOS()
                ? "/dev/fd"
                : throw new PlatformNotSupportedException();
        foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(descriptorDirectory))
        {
            if (!int.TryParse(Path.GetFileName(entry), out var descriptor))
                continue;
            try
            {
                if (GetDescriptorMetadata(descriptor).Identity == expectedIdentity)
                    return true;
            }
            catch (Win32Exception)
            {
                // Enumeration itself can close a transient descriptor before fstat.
            }
        }

        return false;
    }

    private static UnixPathMetadata GetDescriptorMetadata(int descriptor)
    {
        if (OperatingSystem.IsLinux())
        {
            if (statx(
                    descriptor,
                    string.Empty,
                    AtEmptyPath | AtSymlinkNoFollow,
                    StatxBasicStats,
                    out var status) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            return FromLinux(status);
        }

        if (OperatingSystem.IsMacOS())
        {
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? fstat_inode64_raw(descriptor, out var status)
                : fstat_raw(descriptor, out status);
            if (result != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return FromMac(status);
        }

        throw new PlatformNotSupportedException();
    }

    private static SafeFileHandle Open(string path, int flags)
    {
        var descriptor = open(path, flags);
        if (descriptor < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static UnixPathMetadata FromLinux(LinuxStatx status)
    {
        if ((status.Mask & RequiredStatxFields) != RequiredStatxFields)
            throw new IOException("Required file metadata is unavailable.");
        var device = ((ulong)status.DeviceMajor << 32) | status.DeviceMinor;
        var type = (ushort)(status.Mode & FileTypeMask);
        return new UnixPathMetadata(
            new ProtectedPathIdentity(device, 0, status.Inode),
            type == RegularFile,
            type == Directory,
            status.UserId,
            status.LinkCount,
            (UnixFileMode)(status.Mode & PermissionMask));
    }

    private static UnixPathMetadata FromMac(MacStat status)
    {
        var type = (ushort)(status.Mode & FileTypeMask);
        return new UnixPathMetadata(
            new ProtectedPathIdentity(unchecked((uint)status.Device), 0, status.Inode),
            type == RegularFile,
            type == Directory,
            status.UserId,
            status.LinkCount,
            (UnixFileMode)(status.Mode & PermissionMask));
    }

    private static bool UsesLegacyLinuxOpenFlags =>
        RuntimeInformation.ProcessArchitecture is
            Architecture.Arm or
            Architecture.Arm64 or
            Architecture.Armv6 or
            Architecture.Ppc64le;

    private static int LinuxDirectoryFlag => UsesLegacyLinuxOpenFlags ? 0x4000 : 0x10000;

    private static int LinuxNoFollowFlag => UsesLegacyLinuxOpenFlags ? 0x8000 : 0x20000;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint LinkCount;
        [FieldOffset(20)] internal uint UserId;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
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

    [DllImport("libc", SetLastError = true)]
    private static extern int open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int openat(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc")]
    private static extern uint geteuid();

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
    private static extern int fstat(SafeFileHandle file, out MacStat status);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int fstat_inode64(SafeFileHandle file, out MacStat status);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int fstat_raw(int file, out MacStat status);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int fstat_inode64_raw(int file, out MacStat status);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(SafeFileHandle file, uint mode);
}

internal static class MacProtectedPathNative
{
    private const int ExtendedAclType = 0x00000100;
    private const int NoEntryOrPath = 2;

    internal static void VerifyNoExtendedAcl(string path)
    {
        Marshal.SetLastPInvokeError(0);
        var acl = acl_get_file(path, ExtendedAclType);
        if (acl != IntPtr.Zero)
        {
            _ = acl_free(acl);
            throw new ProtectedPathException(ProtectedPathFailureKind.Protection);
        }

        if (Marshal.GetLastPInvokeError() != NoEntryOrPath)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    internal static void VerifyNoExtendedAcl(SafeFileHandle handle)
    {
        var retained = false;
        try
        {
            handle.DangerousAddRef(ref retained);
            Marshal.SetLastPInvokeError(0);
            var acl = acl_get_fd_np(
                handle.DangerousGetHandle().ToInt32(),
                ExtendedAclType);
            if (acl != IntPtr.Zero)
            {
                _ = acl_free(acl);
                throw new ProtectedPathException(ProtectedPathFailureKind.Protection);
            }

            if (Marshal.GetLastPInvokeError() != NoEntryOrPath)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            if (retained)
                handle.DangerousRelease();
        }
    }

    internal static void RemoveExtendedAcl(string path)
    {
        var emptyAcl = acl_init(0);
        if (emptyAcl == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            if (acl_set_file(path, ExtendedAclType, emptyAcl) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            _ = acl_free(emptyAcl);
        }
    }

    internal static void RemoveExtendedAcl(SafeFileHandle handle)
    {
        var emptyAcl = acl_init(0);
        if (emptyAcl == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var retained = false;
        try
        {
            handle.DangerousAddRef(ref retained);
            if (acl_set_fd_np(
                    handle.DangerousGetHandle().ToInt32(),
                    emptyAcl,
                    ExtendedAclType) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            if (retained)
                handle.DangerousRelease();
            _ = acl_free(emptyAcl);
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
    private static extern IntPtr acl_get_fd_np(int fileDescriptor, int type);

    [DllImport("libc", SetLastError = true)]
    private static extern int acl_set_fd_np(
        int fileDescriptor,
        IntPtr acl,
        int type);

    [DllImport("libc", SetLastError = true)]
    private static extern int acl_free(IntPtr value);
}
