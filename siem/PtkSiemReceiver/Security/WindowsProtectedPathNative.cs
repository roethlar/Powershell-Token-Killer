using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PtkSiemReceiver.Security;

[SupportedOSPlatform("windows")]
internal static class WindowsProtectedPathNative
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
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint CreateNew = 1;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    internal static SafeFileHandle OpenFileForRead(string path) =>
        Open(
            path,
            GenericRead | FileReadAttributes,
            ShareRead,
            FileFlagOpenReparsePoint);

    internal static SafeFileHandle CreateOwnerOnlyFile(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ??
                          throw new ProtectedPathException(ProtectedPathFailureKind.Protection);
        var dacl = new RawAcl(GenericAcl.AclRevision, 1);
        dacl.InsertAce(
            0,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                unchecked((int)FileAllAccess),
                currentUser,
                isCallback: false,
                opaque: null));
        var descriptor = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent |
            ControlFlags.DiscretionaryAclProtected,
            currentUser,
            group: null,
            systemAcl: null,
            discretionaryAcl: dacl);
        var descriptorBytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(descriptorBytes, 0);
        var pinnedDescriptor = GCHandle.Alloc(descriptorBytes, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = pinnedDescriptor.AddrOfPinnedObject(),
                InheritHandle = false,
            };
            var handle = CreateFileWithSecurity(
                path,
                GenericRead | GenericWrite,
                shareMode: 0,
                ref attributes,
                CreateNew,
                FileAttributeNormal | FileFlagWriteThrough,
                IntPtr.Zero);
            if (!handle.IsInvalid)
                return handle;

            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is 80 or 183)
                throw new ProtectedPathException(ProtectedPathFailureKind.AlreadyExists);
            throw new Win32Exception(error);
        }
        finally
        {
            pinnedDescriptor.Free();
        }
    }

    internal static SafeFileHandle OpenDirectoryForRetention(string path) =>
        Open(
            path,
            FileReadAttributes,
            ShareRead | ShareWrite,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics);

    internal static SafeFileHandle OpenPathForIdentity(string path, bool directory) =>
        Open(
            path,
            FileReadAttributes,
            ShareRead | ShareWrite | ShareDelete,
            FileFlagOpenReparsePoint |
            (directory ? FileFlagBackupSemantics : 0));

    internal static ProtectedPathIdentity GetIdentity(
        string expectedPath,
        SafeFileHandle handle,
        bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfoClass,
                out var fileId,
                (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        var isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
        if (isDirectory != directory ||
            (information.FileAttributes & FileAttributeReparsePoint) != 0 ||
            (!directory && information.NumberOfLinks != 1))
        {
            throw new ProtectedPathException(ProtectedPathFailureKind.Protection);
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
            throw new ProtectedPathException(ProtectedPathFailureKind.Changed);
        }

        return new ProtectedPathIdentity(
            fileId.VolumeSerialNumber,
            fileId.FileId.High,
            fileId.FileId.Low);
    }

    internal static void SetCurrentUserOwnerOnlyAcl(string path)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        IntPtr tokenUserBuffer = IntPtr.Zero;
        IntPtr acl = IntPtr.Zero;
        try
        {
            _ = GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var required);
            if (required <= 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            tokenUserBuffer = Marshal.AllocHGlobal(required);
            if (!GetTokenInformation(token, TokenUser, tokenUserBuffer, required, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError());

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
                throw new Win32Exception(unchecked((int)aclResult));

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
                throw new Win32Exception(unchecked((int)securityResult));
        }
        finally
        {
            if (acl != IntPtr.Zero)
                _ = LocalFree(acl);
            if (tokenUserBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(tokenUserBuffer);
            _ = CloseHandle(token);
        }
    }

    private static SafeFileHandle Open(
        string path,
        uint desiredAccess,
        uint shareMode,
        uint flags)
    {
        var handle = CreateFile(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return handle;
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

    private const int FileIdInfoClass = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        internal ulong Low;
        internal ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        internal ulong VolumeSerialNumber;
        internal FileId128 FileId;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

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

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileWithSecurity(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileIdInformation information,
        uint bufferSize);

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
