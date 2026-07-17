using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PtkSiemReceiver.Security;

namespace PtkSiemReceiver.Tests;

public sealed class SiemProtectedPathTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Preserve the test failure that prevented ordinary cleanup.
            }
        }
    }

    [Fact]
    public void Protected_file_read_uses_exact_owner_only_file_and_parent()
    {
        var root = NewRoot();
        var path = SiemTestFileSystem.WriteProtectedText(root, "config.json", "protected");

        var bytes = SiemProtectedPath.ReadExternalFile(path, 128);

        Assert.Equal("protected", Encoding.UTF8.GetString(bytes));
        AssertExactProtection(root, isDirectory: true);
        AssertExactProtection(path, isDirectory: false);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixTestNative.geteuid(), UnixProtectedPathNative.GetPathMetadata(root).UserId);
            Assert.Equal(UnixTestNative.geteuid(), UnixProtectedPathNative.GetPathMetadata(path).UserId);
        }
    }

    [Fact]
    public void Missing_external_file_is_distinct_from_wrong_kind()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "missing.pem");

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(path, 128));

        Assert.Equal(ProtectedPathFailureKind.Missing, exception.FailureKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wrong_kind_file_or_parent_is_rejected(bool parent)
    {
        var root = NewRoot();
        string path;
        if (parent)
        {
            var wrongParent = SiemTestFileSystem.WriteProtectedText(root, "not-a-directory", "x");
            path = Path.Combine(wrongParent, "secret.pem");
        }
        else
        {
            path = Path.Combine(root, "not-a-file.pem");
            Directory.CreateDirectory(path);
            _ = SiemProtectedPath.ProtectCreatedDirectory(path);
        }

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(path, 128));

        Assert.Equal(ProtectedPathFailureKind.Protection, exception.FailureKind);
    }

    [Fact]
    public void Oversized_external_file_is_rejected()
    {
        var root = NewRoot();
        var path = SiemTestFileSystem.WriteProtectedText(root, "large.pem", "too-large");

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(path, 3));

        Assert.Equal(ProtectedPathFailureKind.TooLarge, exception.FailureKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Posix_permissive_file_or_parent_is_rejected_without_repair(bool parent)
    {
        if (OperatingSystem.IsWindows()) return;

        var root = NewRoot();
        var path = SiemTestFileSystem.WriteProtectedText(root, "secret.pem", "secret");
        var target = parent ? root : path;
        var broadened = parent
            ? SiemProtectedPath.OwnerDirectoryMode |
              UnixFileMode.GroupRead |
              UnixFileMode.GroupExecute
            : SiemProtectedPath.OwnerFileMode | UnixFileMode.GroupRead;
        File.SetUnixFileMode(target, broadened);

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(path, 128));

        Assert.Equal(ProtectedPathFailureKind.Protection, exception.FailureKind);
        Assert.Equal(broadened, File.GetUnixFileMode(target));
        File.SetUnixFileMode(
            target,
            parent ? SiemProtectedPath.OwnerDirectoryMode : SiemProtectedPath.OwnerFileMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Posix_special_mode_bits_are_rejected_as_nonexact(bool parent)
    {
        if (OperatingSystem.IsWindows()) return;

        var root = NewRoot();
        var path = SiemTestFileSystem.WriteProtectedText(root, "secret.pem", "secret");
        var target = parent ? root : path;
        var broadened = (parent
            ? SiemProtectedPath.OwnerDirectoryMode
            : SiemProtectedPath.OwnerFileMode) | UnixFileMode.SetUser;
        File.SetUnixFileMode(target, broadened);

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(path, 128));

        Assert.Equal(ProtectedPathFailureKind.Protection, exception.FailureKind);
        Assert.Equal(broadened, File.GetUnixFileMode(target));
        File.SetUnixFileMode(
            target,
            parent ? SiemProtectedPath.OwnerDirectoryMode : SiemProtectedPath.OwnerFileMode);
    }

    [Fact]
    public void Unix_file_owner_check_uses_numeric_effective_identity()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = NewRoot();
        var path = SiemTestFileSystem.WriteProtectedText(root, "secret.pem", "secret");
        var effective = UnixTestNative.geteuid();
        var different = effective == uint.MaxValue ? effective - 1 : effective + 1;

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(
                path,
                128,
                new ProtectedPathTestHooks(ExpectedUnixFileUserId: different)));

        Assert.Equal(ProtectedPathFailureKind.Protection, exception.FailureKind);
    }

    [Fact]
    public void Unix_directory_owner_check_uses_numeric_effective_identity()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = NewRoot();
        var effective = UnixTestNative.geteuid();
        var different = effective == uint.MaxValue ? effective - 1 : effective + 1;

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.VerifyExternalDirectory(
                root,
                new ProtectedPathTestHooks(ExpectedUnixDirectoryUserId: different)));

        Assert.Equal(ProtectedPathFailureKind.Protection, exception.FailureKind);
    }

    [Fact]
    public void Posix_nonregular_file_is_rejected_without_opening_it()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = NewRoot();
        var path = Path.Combine(root, "fifo.pem");
        if (CreateFifo(path, (uint)SiemProtectedPath.OwnerFileMode) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(path, 128));

        Assert.Equal(ProtectedPathFailureKind.Protection, exception.FailureKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mac_extended_acl_is_rejected_for_file_or_parent(bool parent)
    {
        if (!OperatingSystem.IsMacOS()) return;

        var root = NewRoot();
        var path = SiemTestFileSystem.WriteProtectedText(root, "secret.pem", "secret");
        var target = parent ? root : path;
        AddMacExtendedAcl(target);

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(path, 128));

        Assert.Equal(ProtectedPathFailureKind.Protection, exception.FailureKind);
        var second = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(path, 128));
        Assert.Equal(ProtectedPathFailureKind.Protection, second.FailureKind);
        _ = parent
            ? SiemProtectedPath.ProtectCreatedDirectory(target)
            : SiemProtectedPath.ProtectCreatedFile(target);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Windows_nonexact_dacl_is_rejected_without_mutation(
        bool parent,
        bool denyAce)
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = NewRoot();
        var path = SiemTestFileSystem.WriteProtectedText(root, "secret.pem", "secret");
        var target = parent ? root : path;
        AddWindowsRule(target, parent, denyAce);
        var before = SnapshotWindowsAcl(target, parent);

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(path, 128));

        Assert.Equal(ProtectedPathFailureKind.Protection, exception.FailureKind);
        Assert.Equal(before, SnapshotWindowsAcl(target, parent));
        _ = parent
            ? SiemProtectedPath.ProtectCreatedDirectory(target)
            : SiemProtectedPath.ProtectCreatedFile(target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Windows_unprotected_dacl_is_rejected_without_mutation(bool parent)
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = NewRoot();
        var path = SiemTestFileSystem.WriteProtectedText(root, "secret.pem", "secret");
        var target = parent ? root : path;
        FileSystemSecurity security = parent
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(target))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(target));
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        if (parent)
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(target), (DirectorySecurity)security);
        else
            FileSystemAclExtensions.SetAccessControl(new FileInfo(target), (FileSecurity)security);
        var before = SnapshotWindowsAcl(target, parent);

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(path, 128));

        Assert.Equal(ProtectedPathFailureKind.Protection, exception.FailureKind);
        Assert.Equal(before, SnapshotWindowsAcl(target, parent));
        _ = parent
            ? SiemProtectedPath.ProtectCreatedDirectory(target)
            : SiemProtectedPath.ProtectCreatedFile(target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Windows_owner_check_uses_expected_sid_seam(bool parent)
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = NewRoot();
        var path = SiemTestFileSystem.WriteProtectedText(root, "secret.pem", "secret");
        var foreignSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        var hooks = parent
            ? new ProtectedPathTestHooks(ExpectedWindowsDirectoryOwnerSid: foreignSid)
            : new ProtectedPathTestHooks(ExpectedWindowsFileOwnerSid: foreignSid);

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(path, 128, hooks));

        Assert.Equal(ProtectedPathFailureKind.Protection, exception.FailureKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Windows_one_ace_with_wrong_rights_or_flags_is_rejected(bool inheritanceFlags)
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = NewRoot();
        var path = SiemTestFileSystem.WriteProtectedText(root, "secret.pem", "secret");
        var target = inheritanceFlags ? root : path;
        FileSystemSecurity security = inheritanceFlags
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(target))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(target));
        using var identity = WindowsIdentity.GetCurrent();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var existing = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        foreach (var rule in existing)
            security.RemoveAccessRuleSpecific(rule);
        security.AddAccessRule(inheritanceFlags
            ? new FileSystemAccessRule(
                identity.User!,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow)
            : new FileSystemAccessRule(
                identity.User!,
                FileSystemRights.Read,
                AccessControlType.Allow));
        if (inheritanceFlags)
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(target), (DirectorySecurity)security);
        else
            FileSystemAclExtensions.SetAccessControl(new FileInfo(target), (FileSecurity)security);

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(path, 128));

        Assert.Equal(ProtectedPathFailureKind.Protection, exception.FailureKind);
        RestoreWindowsOwnerOnlyDacl(target, inheritanceFlags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Leaf_file_or_ancestor_directory_redirect_is_rejected(bool ancestor)
    {
        var targetRoot = NewRoot();
        var linkRoot = NewRoot();
        string candidate;
        if (ancestor)
        {
            var nested = Path.Combine(targetRoot, "nested");
            Directory.CreateDirectory(nested);
            _ = SiemProtectedPath.ProtectCreatedDirectory(nested);
            _ = SiemTestFileSystem.WriteProtectedText(nested, "secret.pem", "secret");
            var link = Path.Combine(linkRoot, "redirected");
            Directory.CreateSymbolicLink(link, targetRoot);
            candidate = Path.Combine(link, "nested", "secret.pem");
        }
        else
        {
            var target = SiemTestFileSystem.WriteProtectedText(
                targetRoot,
                "secret.pem",
                "secret");
            candidate = Path.Combine(linkRoot, "secret-link.pem");
            File.CreateSymbolicLink(candidate, target);
        }

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(candidate, 128));

        Assert.Equal(ProtectedPathFailureKind.Protection, exception.FailureKind);
    }

    [Fact]
    public void Dangling_redirect_is_rejected_as_protection_failure()
    {
        var root = NewRoot();
        var candidate = Path.Combine(root, "dangling.pem");
        File.CreateSymbolicLink(candidate, Path.Combine(root, "missing.pem"));

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(candidate, 128));

        Assert.Equal(ProtectedPathFailureKind.Protection, exception.FailureKind);
    }

    [Fact]
    public void Replacement_barrier_never_accepts_replacement_bytes()
    {
        var root = NewRoot();
        var original = SiemTestFileSystem.WriteProtectedText(root, "original.pem", "original");
        var replacement = SiemTestFileSystem.WriteProtectedText(root, "replacement.pem", "replacement");
        byte[]? observed = null;

        var error = Record.Exception(() =>
        {
            observed = SiemProtectedPath.ReadExternalFile(
                original,
                128,
                new ProtectedPathTestHooks(AfterInitialFileIdentity: _ =>
                    File.Move(replacement, original, overwrite: true)));
        });

        if (error is null)
            Assert.Equal("original", Encoding.UTF8.GetString(observed!));
        else
            Assert.IsType<ProtectedPathException>(error);
        Assert.NotEqual("replacement", observed is null ? null : Encoding.UTF8.GetString(observed));
    }

    [Fact]
    public void Parent_replacement_barrier_never_reads_through_replacement_directory()
    {
        var container = NewRoot();
        var originalParent = Path.Combine(container, "original-parent");
        var replacementParent = Path.Combine(container, "replacement-parent");
        Directory.CreateDirectory(originalParent);
        Directory.CreateDirectory(replacementParent);
        _ = SiemProtectedPath.ProtectCreatedDirectory(originalParent);
        _ = SiemProtectedPath.ProtectCreatedDirectory(replacementParent);
        var original = SiemTestFileSystem.WriteProtectedText(
            originalParent,
            "secret.pem",
            "original");
        _ = SiemTestFileSystem.WriteProtectedText(
            replacementParent,
            "secret.pem",
            "replacement");
        var displacedParent = Path.Combine(container, "displaced-parent");
        byte[]? observed = null;

        var error = Record.Exception(() =>
        {
            observed = SiemProtectedPath.ReadExternalFile(
                original,
                128,
                new ProtectedPathTestHooks(AfterInitialDirectoryIdentity: _ =>
                {
                    Directory.Move(originalParent, displacedParent);
                    Directory.Move(replacementParent, originalParent);
                }));
        });

        if (error is null)
            Assert.Equal("original", Encoding.UTF8.GetString(observed!));
        else
            Assert.IsType<ProtectedPathException>(error);
        Assert.NotEqual("replacement", observed is null ? null : Encoding.UTF8.GetString(observed));
    }

    [Fact]
    public void Hard_link_alias_is_rejected()
    {
        var root = NewRoot();
        var original = SiemTestFileSystem.WriteProtectedText(root, "original.pem", "secret");
        var alias = Path.Combine(root, "alias.pem");
        CreateHardLink(original, alias);

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(alias, 128));

        Assert.Equal(ProtectedPathFailureKind.Protection, exception.FailureKind);
    }

    [Fact]
    public void Sanitized_failure_does_not_disclose_path()
    {
        var root = NewRoot();
        var canary = "PATH-CANARY-" + Guid.NewGuid().ToString("N");
        var path = SiemTestFileSystem.WriteProtectedText(root, canary, "secret");
        if (OperatingSystem.IsWindows())
            AddWindowsRule(path, isDirectory: false, denyAce: false);
        else
            File.SetUnixFileMode(path, SiemProtectedPath.OwnerFileMode | UnixFileMode.GroupRead);

        var exception = Assert.Throws<ProtectedPathException>(() =>
            SiemProtectedPath.ReadExternalFile(path, 128));

        Assert.DoesNotContain(canary, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        _ = SiemProtectedPath.ProtectCreatedFile(path);
    }

    private string NewRoot()
    {
        var root = SiemTestFileSystem.CreateProtectedRoot("ptk-siem-path");
        _roots.Add(root);
        return root;
    }

    private static void AssertExactProtection(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            var snapshot = SnapshotWindowsAcl(path, isDirectory);
            using var identity = WindowsIdentity.GetCurrent();
            Assert.Equal(identity.User!.Value, snapshot.Owner);
            Assert.True(snapshot.Protected);
            var security = isDirectory
                ? (FileSystemSecurity)FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
                : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
            Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            return;
        }

        Assert.Equal(
            isDirectory
                ? SiemProtectedPath.OwnerDirectoryMode
                : SiemProtectedPath.OwnerFileMode,
            File.GetUnixFileMode(path));
    }

    private static void AddMacExtendedAcl(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/chmod",
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("+a");
        startInfo.ArgumentList.Add("everyone allow read");
        startInfo.ArgumentList.Add(path);
        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("Could not start macOS chmod.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new Win32Exception(process.ExitCode, error);
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkWindows(newPath, existingPath, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return;
        }

        if (CreateHardLinkUnix(existingPath, newPath) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreWindowsOwnerOnlyDacl(string path, bool isDirectory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        FileSystemSecurity security = isDirectory
            ? new DirectorySecurity()
            : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity.User!,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        if (isDirectory)
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), (DirectorySecurity)security);
        else
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), (FileSecurity)security);
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsRule(string path, bool isDirectory, bool denyAce)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(
                denyAce ? WellKnownSidType.LocalSystemSid : WellKnownSidType.WorldSid,
                null),
            FileSystemRights.Read,
            denyAce ? AccessControlType.Deny : AccessControlType.Allow));
        if (isDirectory)
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), (DirectorySecurity)security);
        else
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), (FileSecurity)security);
    }

    [SupportedOSPlatform("windows")]
    private static WindowsAclSnapshot SnapshotWindowsAcl(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        var owner = Assert.IsType<SecurityIdentifier>(
            security.GetOwner(typeof(SecurityIdentifier)));
        return new WindowsAclSnapshot(
            owner.Value,
            security.AreAccessRulesProtected,
            security.GetSecurityDescriptorSddlForm(
                AccessControlSections.Owner | AccessControlSections.Access));
    }

    private sealed record WindowsAclSnapshot(string Owner, bool Protected, string Sddl);

    private static class UnixTestNative
    {
        [DllImport("libc")]
        internal static extern uint geteuid();
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int CreateFifo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string newFileName,
        string existingFileName,
        IntPtr securityAttributes);
}
