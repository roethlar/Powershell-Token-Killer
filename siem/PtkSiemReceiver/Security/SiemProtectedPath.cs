using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PtkSiemReceiver.Security;

internal enum ProtectedPathFailureKind
{
    InvalidPath,
    Missing,
    AlreadyExists,
    TooLarge,
    Protection,
    Changed,
}

internal sealed class ProtectedPathException : Exception
{
    internal ProtectedPathException(ProtectedPathFailureKind failureKind)
        : base($"siem protected path failure: {FailureCode(failureKind)}")
    {
        FailureKind = failureKind;
    }

    internal ProtectedPathFailureKind FailureKind { get; }

    private static string FailureCode(ProtectedPathFailureKind failureKind) =>
        failureKind switch
        {
            ProtectedPathFailureKind.InvalidPath => "invalid_path",
            ProtectedPathFailureKind.Missing => "missing",
            ProtectedPathFailureKind.AlreadyExists => "already_exists",
            ProtectedPathFailureKind.TooLarge => "too_large",
            ProtectedPathFailureKind.Protection => "protection",
            ProtectedPathFailureKind.Changed => "changed",
            _ => "unknown",
        };
}

internal sealed record ProtectedPathTestHooks(
    Action<string>? AfterInitialFileIdentity = null,
    Action<string>? AfterInitialDirectoryIdentity = null,
    uint? ExpectedUnixFileUserId = null,
    uint? ExpectedUnixDirectoryUserId = null,
    string? ExpectedWindowsFileOwnerSid = null,
    string? ExpectedWindowsDirectoryOwnerSid = null,
    Func<string, bool, uint?>? ExpectedUnixUserIdForPath = null,
    Func<string, bool, string?>? ExpectedWindowsOwnerSidForPath = null);

internal readonly record struct ProtectedPathIdentity(
    ulong DeviceOrVolume,
    ulong FileIdHigh,
    ulong FileIdLow);

internal readonly record struct ProtectedFileRead(
    byte[] Bytes,
    ProtectedPathIdentity Identity);

internal sealed class ProtectedDirectoryLease(
    string path,
    SafeFileHandle handle,
    ProtectedPathIdentity identity) : IDisposable
{
    internal string Path { get; } = path;

    internal SafeFileHandle Handle { get; } = handle;

    internal ProtectedPathIdentity Identity { get; } = identity;

    public void Dispose() => Handle.Dispose();
}

/// <summary>
/// SIEM-local owner-only filesystem boundary. Operator-provisioned paths are
/// verified without mutation; only files/directories known to have been
/// created by the receiver (or a test fixture) may be protected here.
/// </summary>
internal static class SiemProtectedPath
{
    internal const UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode OwnerFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static string NormalizeAbsolute(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new ProtectedPathException(ProtectedPathFailureKind.InvalidPath);
            return Path.GetFullPath(path);
        }
        catch (ProtectedPathException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new ProtectedPathException(ProtectedPathFailureKind.InvalidPath);
        }
    }

    internal static byte[] ReadExternalFile(
        string path,
        int maximumBytes,
        ProtectedPathTestHooks? testHooks = null) =>
        ReadExternalFileWithIdentity(path, maximumBytes, testHooks).Bytes;

    internal static ProtectedFileRead ReadExternalFileWithIdentity(
        string path,
        int maximumBytes,
        ProtectedPathTestHooks? testHooks = null)
    {
        if (maximumBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        return Sanitize(() => ReadExternalFileCore(
            NormalizeAbsolute(path),
            maximumBytes,
            testHooks));
    }

    internal static ProtectedPathIdentity VerifyExternalDirectory(
        string path,
        ProtectedPathTestHooks? testHooks = null) =>
        Sanitize(() => VerifyExternalDirectoryCore(NormalizeAbsolute(path), testHooks));

    internal static ProtectedDirectoryLease RetainExternalDirectory(
        string path,
        ProtectedPathTestHooks? testHooks = null) =>
        Sanitize(() => RetainExternalDirectoryCore(NormalizeAbsolute(path), testHooks));

    internal static void VerifyRetainedDirectory(
        ProtectedDirectoryLease lease,
        ProtectedPathTestHooks? testHooks = null) =>
        Sanitize(() =>
        {
            ArgumentNullException.ThrowIfNull(lease);
            RequireSameIdentity(
                VerifyRetainedDirectory(lease.Path, lease.Handle, testHooks),
                lease.Identity);
            return true;
        });

    internal static ProtectedPathIdentity VerifyExternalFile(
        string path,
        ProtectedPathTestHooks? testHooks = null) =>
        Sanitize(() => VerifyExternalFileCore(NormalizeAbsolute(path), testHooks));

    /// <summary>
    /// Inspects a SQLite artifact without retaining a descriptor. After SQLite
    /// opens on POSIX, closing another descriptor for the same inode can release
    /// process-wide fcntl locks, so storage startup uses this path-only boundary.
    /// </summary>
    internal static ProtectedPathIdentity? InspectSqliteFileOrMissing(
        string path,
        ProtectedPathTestHooks? testHooks = null) =>
        Sanitize(() => InspectSqliteFileOrMissingCore(
            NormalizeAbsolute(path),
            testHooks));

    internal static ProtectedPathIdentity InspectCreatedSqliteFile(string path) =>
        Sanitize(() => InspectCreatedSqliteFileCore(NormalizeAbsolute(path)));

    internal static ProtectedPathIdentity ProtectCreatedSqliteFile(
        string path,
        ProtectedPathIdentity expectedIdentity) =>
        Sanitize(() => ProtectCreatedSqliteFileCore(
            NormalizeAbsolute(path),
            expectedIdentity));

    internal static ProtectedPathIdentity VerifySqliteFile(
        string path,
        ProtectedPathIdentity expectedIdentity,
        ProtectedPathTestHooks? testHooks = null) =>
        Sanitize(() =>
        {
            var normalized = NormalizeAbsolute(path);
            var identity = InspectSqliteFileOrMissingCore(normalized, testHooks) ??
                           throw new ProtectedPathException(ProtectedPathFailureKind.Missing);
            RequireSameIdentity(identity, expectedIdentity);
            return identity;
        });

    internal static void VerifySqliteFileIsOpen(ProtectedPathIdentity expectedIdentity) =>
        Sanitize(() =>
        {
            if (!OperatingSystem.IsWindows() &&
                !UnixProtectedPathNative.IsIdentityOpen(expectedIdentity))
            {
                throw new ProtectedPathException(ProtectedPathFailureKind.Changed);
            }
            return true;
        });

    internal static ProtectedPathIdentity CreateProtectedFile(string path) =>
        Sanitize(() => CreateProtectedFileCore(NormalizeAbsolute(path)));

    internal static ProtectedPathIdentity ProtectCreatedFile(string path) =>
        Sanitize(() =>
        {
            var normalized = NormalizeAbsolute(path);
            var identity = InspectCreatedSqliteFileCore(normalized);
            return ProtectCreatedSqliteFileCore(normalized, identity);
        });

    internal static ProtectedPathIdentity ProtectCreatedDirectory(string path) =>
        Sanitize(() => ProtectCreatedDirectoryCore(NormalizeAbsolute(path)));

    private static ProtectedFileRead ReadExternalFileCore(
        string path,
        int maximumBytes,
        ProtectedPathTestHooks? testHooks)
    {
        RefuseLinkedPathComponents(path);
        var parent = Path.GetDirectoryName(path) ??
                     throw new ProtectedPathException(ProtectedPathFailureKind.InvalidPath);
        using var parentHandle = OpenDirectoryNoFollow(parent);
        var parentIdentity = VerifyRetainedDirectory(parent, parentHandle, testHooks);
        testHooks?.AfterInitialDirectoryIdentity?.Invoke(parent);

        if (Directory.Exists(path))
            throw new ProtectedPathException(ProtectedPathFailureKind.Protection);
        if (!File.Exists(path))
            throw new ProtectedPathException(ProtectedPathFailureKind.Missing);

        VerifyFileKindBeforeOpen(path, testHooks);
        using var fileHandle = OpenFileNoFollowForRead(parentHandle, path);
        var initialIdentity = VerifyRetainedFile(path, fileHandle, testHooks);
        testHooks?.AfterInitialFileIdentity?.Invoke(path);

        using var stream = new FileStream(
            fileHandle,
            FileAccess.Read,
            bufferSize: 16 * 1024,
            isAsync: false);
        if (stream.Length < 0 || stream.Length > maximumBytes)
            throw new ProtectedPathException(ProtectedPathFailureKind.TooLarge);

        var bytes = new byte[checked((int)stream.Length)];
        try
        {
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
                throw new ProtectedPathException(ProtectedPathFailureKind.Changed);

            RequireSameIdentity(
                VerifyRetainedFile(path, fileHandle, testHooks),
                initialIdentity);
            RequireSameIdentity(
                VerifyRetainedDirectory(parent, parentHandle, testHooks),
                parentIdentity);
            RefuseLinkedPathComponents(path);
            return new ProtectedFileRead(bytes, initialIdentity);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static ProtectedPathIdentity VerifyExternalDirectoryCore(
        string path,
        ProtectedPathTestHooks? testHooks)
    {
        RefuseLinkedPathComponents(path);
        if (File.Exists(path))
            throw new ProtectedPathException(ProtectedPathFailureKind.Protection);
        if (!Directory.Exists(path))
            throw new ProtectedPathException(ProtectedPathFailureKind.Missing);
        using var handle = OpenDirectoryNoFollow(path);
        return VerifyRetainedDirectory(path, handle, testHooks);
    }

    private static ProtectedDirectoryLease RetainExternalDirectoryCore(
        string path,
        ProtectedPathTestHooks? testHooks)
    {
        RefuseLinkedPathComponents(path);
        if (File.Exists(path))
            throw new ProtectedPathException(ProtectedPathFailureKind.Protection);
        if (!Directory.Exists(path))
            throw new ProtectedPathException(ProtectedPathFailureKind.Missing);
        var handle = OpenDirectoryNoFollow(path);
        try
        {
            return new ProtectedDirectoryLease(
                path,
                handle,
                VerifyRetainedDirectory(path, handle, testHooks));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static ProtectedPathIdentity VerifyExternalFileCore(
        string path,
        ProtectedPathTestHooks? testHooks)
    {
        RefuseLinkedPathComponents(path);
        var parent = Path.GetDirectoryName(path) ??
                     throw new ProtectedPathException(ProtectedPathFailureKind.InvalidPath);
        using var parentHandle = OpenDirectoryNoFollow(parent);
        _ = VerifyRetainedDirectory(parent, parentHandle, testHooks);
        if (Directory.Exists(path))
            throw new ProtectedPathException(ProtectedPathFailureKind.Protection);
        if (!File.Exists(path))
            throw new ProtectedPathException(ProtectedPathFailureKind.Missing);
        VerifyFileKindBeforeOpen(path, testHooks);
        using var handle = OpenFileNoFollowForRead(parentHandle, path);
        return VerifyRetainedFile(path, handle, testHooks);
    }

    private static ProtectedPathIdentity? InspectSqliteFileOrMissingCore(
        string path,
        ProtectedPathTestHooks? testHooks)
    {
        RefuseLinkedPathComponents(path);
        if (!File.Exists(path) && !Directory.Exists(path))
            return null;
        if (!File.Exists(path) || Directory.Exists(path))
            throw new ProtectedPathException(ProtectedPathFailureKind.Protection);

        if (OperatingSystem.IsWindows())
        {
            using var handle = WindowsProtectedPathNative.OpenPathForIdentity(path, directory: false);
            var identity = WindowsProtectedPathNative.GetIdentity(path, handle, directory: false);
            VerifyWindowsOwnerOnlyAcl(path, isDirectory: false, testHooks);
            return identity;
        }

        var metadata = UnixProtectedPathNative.GetPathMetadata(path);
        ValidateUnixMetadata(
            metadata,
            directory: false,
            requireExactMode: true,
            expectedUserId: ExpectedUnixUserId(testHooks, path, directory: false));
        VerifyMacNoExtendedAcl(path);
        return metadata.Identity;
    }

    private static ProtectedPathIdentity InspectCreatedSqliteFileCore(string path)
    {
        RefuseLinkedPathComponents(path);
        if (!File.Exists(path) || Directory.Exists(path))
            throw new ProtectedPathException(ProtectedPathFailureKind.Missing);

        if (OperatingSystem.IsWindows())
        {
            using var handle = WindowsProtectedPathNative.OpenPathForIdentity(path, directory: false);
            return WindowsProtectedPathNative.GetIdentity(path, handle, directory: false);
        }

        var metadata = UnixProtectedPathNative.GetPathMetadata(path);
        ValidateUnixMetadata(
            metadata,
            directory: false,
            requireExactMode: false,
            expectedUserId: UnixProtectedPathNative.EffectiveUserId);
        return metadata.Identity;
    }

    private static ProtectedPathIdentity ProtectCreatedSqliteFileCore(
        string path,
        ProtectedPathIdentity expectedIdentity)
    {
        RequireSameIdentity(InspectCreatedSqliteFileCore(path), expectedIdentity);
        if (OperatingSystem.IsWindows())
        {
            WindowsProtectedPathNative.SetCurrentUserOwnerOnlyAcl(path);
        }
        else
        {
            File.SetUnixFileMode(path, OwnerFileMode);
            if (OperatingSystem.IsMacOS())
                MacProtectedPathNative.RemoveExtendedAcl(path);
        }

        var protectedIdentity = InspectSqliteFileOrMissingCore(path, testHooks: null) ??
                                throw new ProtectedPathException(ProtectedPathFailureKind.Missing);
        RequireSameIdentity(protectedIdentity, expectedIdentity);
        return protectedIdentity;
    }

    private static ProtectedPathIdentity CreateProtectedFileCore(string path)
    {
        var parent = Path.GetDirectoryName(path) ??
                     throw new ProtectedPathException(ProtectedPathFailureKind.InvalidPath);
        _ = VerifyExternalDirectoryCore(parent, testHooks: null);
        RefuseLinkedPathComponents(path);
        if (PathExistsWithoutFollowing(path) || File.Exists(path) || Directory.Exists(path))
            throw new ProtectedPathException(ProtectedPathFailureKind.AlreadyExists);

        using var stream = CreateOwnerOnlyFileStream(path);
        if (OperatingSystem.IsMacOS())
            MacProtectedPathNative.RemoveExtendedAcl(stream.SafeFileHandle);

        var identity = VerifyRetainedFile(path, stream.SafeFileHandle, testHooks: null);
        stream.Flush(flushToDisk: true);
        RequireSameIdentity(
            VerifyRetainedFile(path, stream.SafeFileHandle, testHooks: null),
            identity);
        return identity;
    }

    private static ProtectedPathIdentity ProtectCreatedDirectoryCore(string path)
    {
        RefuseLinkedPathComponents(path);
        if (!Directory.Exists(path) || File.Exists(path))
            throw new ProtectedPathException(ProtectedPathFailureKind.Missing);

        using var handle = OpenDirectoryNoFollow(path);
        ProtectedPathIdentity identity;
        if (OperatingSystem.IsWindows())
        {
            identity = WindowsProtectedPathNative.GetIdentity(path, handle, directory: true);
            WindowsProtectedPathNative.SetCurrentUserOwnerOnlyAcl(path);
        }
        else
        {
            var metadata = UnixProtectedPathNative.GetRetainedMetadata(path, handle);
            ValidateUnixMetadata(
                metadata,
                directory: true,
                requireExactMode: false,
                expectedUserId: UnixProtectedPathNative.EffectiveUserId);
            identity = metadata.Identity;
            UnixProtectedPathNative.SetMode(handle, OwnerDirectoryMode);
            if (OperatingSystem.IsMacOS())
                MacProtectedPathNative.RemoveExtendedAcl(handle);
        }

        RequireSameIdentity(
            VerifyRetainedDirectory(path, handle, testHooks: null),
            identity);
        return identity;
    }

    private static ProtectedPathIdentity VerifyRetainedFile(
        string path,
        SafeFileHandle handle,
        ProtectedPathTestHooks? testHooks)
    {
        RefuseLinkedPathComponents(path);
        if (OperatingSystem.IsWindows())
        {
            var identity = WindowsProtectedPathNative.GetIdentity(path, handle, directory: false);
            VerifyWindowsOwnerOnlyAcl(path, isDirectory: false, testHooks);
            return identity;
        }

        var metadata = UnixProtectedPathNative.GetRetainedMetadata(path, handle);
        ValidateUnixMetadata(
            metadata,
            directory: false,
            requireExactMode: true,
            expectedUserId: ExpectedUnixUserId(testHooks, path, directory: false));
        VerifyMacNoExtendedAcl(path, handle);
        return metadata.Identity;
    }

    private static ProtectedPathIdentity VerifyRetainedDirectory(
        string path,
        SafeFileHandle handle,
        ProtectedPathTestHooks? testHooks)
    {
        RefuseLinkedPathComponents(path);
        if (OperatingSystem.IsWindows())
        {
            var identity = WindowsProtectedPathNative.GetIdentity(path, handle, directory: true);
            VerifyWindowsOwnerOnlyAcl(path, isDirectory: true, testHooks);
            return identity;
        }

        var metadata = UnixProtectedPathNative.GetRetainedMetadata(path, handle);
        ValidateUnixMetadata(
            metadata,
            directory: true,
            requireExactMode: true,
            expectedUserId: ExpectedUnixUserId(testHooks, path, directory: true));
        VerifyMacNoExtendedAcl(path, handle);
        return metadata.Identity;
    }

    private static SafeFileHandle OpenDirectoryNoFollow(string path) =>
        OperatingSystem.IsWindows()
            ? WindowsProtectedPathNative.OpenDirectoryForRetention(path)
            : UnixProtectedPathNative.OpenDirectory(path);

    private static SafeFileHandle OpenFileNoFollowForRead(
        SafeFileHandle parentHandle,
        string path) =>
        OperatingSystem.IsWindows()
            ? WindowsProtectedPathNative.OpenFileForRead(path)
            : UnixProtectedPathNative.OpenFileForReadAt(
                parentHandle,
                Path.GetFileName(path));

    private static void VerifyFileKindBeforeOpen(
        string path,
        ProtectedPathTestHooks? testHooks)
    {
        if (OperatingSystem.IsWindows())
            return;

        ValidateUnixMetadata(
            UnixProtectedPathNative.GetPathMetadata(path),
            directory: false,
            requireExactMode: true,
            expectedUserId: ExpectedUnixUserId(testHooks, path, directory: false));
    }

    private static FileStream CreateOwnerOnlyFileStream(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = WindowsProtectedPathNative.CreateOwnerOnlyFile(path);
            try
            {
                return new FileStream(
                    handle,
                    FileAccess.ReadWrite,
                    bufferSize: 1,
                    isAsync: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 1,
            Options = FileOptions.WriteThrough,
            UnixCreateMode = OwnerFileMode,
        });
    }

    private static void ValidateUnixMetadata(
        UnixPathMetadata metadata,
        bool directory,
        bool requireExactMode,
        uint expectedUserId)
    {
        var expectedMode = directory ? OwnerDirectoryMode : OwnerFileMode;
        if (metadata.IsDirectory != directory ||
            metadata.IsRegularFile == directory ||
            metadata.UserId != expectedUserId ||
            (!directory && metadata.LinkCount != 1) ||
            (requireExactMode && metadata.Mode != expectedMode))
        {
            throw new ProtectedPathException(ProtectedPathFailureKind.Protection);
        }
    }

    private static uint ExpectedUnixUserId(
        ProtectedPathTestHooks? testHooks,
        string path,
        bool directory) =>
        testHooks?.ExpectedUnixUserIdForPath?.Invoke(path, directory) ??
        (directory
            ? testHooks?.ExpectedUnixDirectoryUserId
            : testHooks?.ExpectedUnixFileUserId) ??
        UnixProtectedPathNative.EffectiveUserId;

    private static void VerifyMacNoExtendedAcl(
        string path,
        SafeFileHandle? retainedHandle = null)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        if (retainedHandle is null)
            MacProtectedPathNative.VerifyNoExtendedAcl(path);
        else
            MacProtectedPathNative.VerifyNoExtendedAcl(retainedHandle);
        // acl_get_file reports the same errno for no ACL and disappearance.
        _ = UnixProtectedPathNative.GetPathMetadata(path);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsOwnerOnlyAcl(
        string path,
        bool isDirectory,
        ProtectedPathTestHooks? testHooks)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        using var identity = WindowsIdentity.GetCurrent();
        var expectedSid = testHooks?.ExpectedWindowsOwnerSidForPath?.Invoke(path, isDirectory) ??
                          (isDirectory
                              ? testHooks?.ExpectedWindowsDirectoryOwnerSid
                              : testHooks?.ExpectedWindowsFileOwnerSid);
        var currentUser = expectedSid is not null
            ? new SecurityIdentifier(expectedSid)
            : identity.User ??
              throw new ProtectedPathException(ProtectedPathFailureKind.Protection);
        var descriptor = new RawSecurityDescriptor(
            security.GetSecurityDescriptorBinaryForm(),
            0);
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
            throw new ProtectedPathException(ProtectedPathFailureKind.Protection);
        }
    }

    private static void RefuseLinkedPathComponents(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ??
                   throw new ProtectedPathException(ProtectedPathFailureKind.InvalidPath);
        var relative = fullPath[root.Length..];
        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        RefuseLinkOrReparsePoint(current);
        foreach (var component in components)
        {
            current = Path.Combine(current, component);
            RefuseLinkOrReparsePoint(current);
        }

    }

    private static void RefuseLinkOrReparsePoint(string path)
    {
        if (PathExistsWithoutFollowing(path))
            throw new ProtectedPathException(ProtectedPathFailureKind.Protection);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ProtectedPathException(ProtectedPathFailureKind.Protection);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Missing ordinary components are handled by the caller's role check.
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

    private static void RequireSameIdentity(
        ProtectedPathIdentity actual,
        ProtectedPathIdentity expected)
    {
        if (actual != expected)
            throw new ProtectedPathException(ProtectedPathFailureKind.Changed);
    }

    private static T Sanitize<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (ProtectedPathException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new ProtectedPathException(
                exception is EndOfStreamException
                    ? ProtectedPathFailureKind.Changed
                    : ProtectedPathFailureKind.Protection);
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
