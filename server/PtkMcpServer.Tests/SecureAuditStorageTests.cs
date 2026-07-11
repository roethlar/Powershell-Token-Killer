using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class SecureAuditStorageTests : IDisposable
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
    public void Unix_owner_verification_uses_numeric_effective_user_identity()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = SecureAuditStorage.PrepareRoot(NewRoot());
        var effectiveUserId = UnixTestNative.geteuid();

        SecureAuditStorage.VerifyUnixOwner(root, effectiveUserId);

        var differentUserId = effectiveUserId == uint.MaxValue
            ? effectiveUserId - 1
            : effectiveUserId + 1;
        var exception = Assert.Throws<IOException>(() =>
            SecureAuditStorage.VerifyUnixOwner(root, differentUserId));
        Assert.Contains("current effective user", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Protected_file_rejects_a_different_unix_owner_before_mode_checks()
    {
        if (OperatingSystem.IsWindows() || UnixTestNative.geteuid() == 0)
            return;

        var rootOwnedFile = OperatingSystem.IsMacOS()
            ? "/System/Library/CoreServices/SystemVersion.plist"
            : "/etc/passwd";
        Assert.True(File.Exists(rootOwnedFile), $"Expected system fixture {rootOwnedFile} to exist.");

        var exception = Assert.Throws<IOException>(() =>
            SecureAuditStorage.VerifyProtectedFile(rootOwnedFile));
        Assert.Contains("not owned", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareRoot_strips_an_existing_macos_extended_acl()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var root = NewRoot();
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, SecureAuditStorage.OwnerDirectoryMode);
        AddMacExtendedAcl(root);
        Assert.True(MacTestNative.HasExtendedAcl(root));
        Assert.Equal(SecureAuditStorage.OwnerDirectoryMode, File.GetUnixFileMode(root));

        Assert.Equal(root, SecureAuditStorage.PrepareRoot(root));

        Assert.False(MacTestNative.HasExtendedAcl(root));
        SecureAuditStorage.VerifyProtectedDirectory(root);
    }

    [Fact]
    public void Protected_directory_fails_closed_after_macos_acl_sabotage()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var root = SecureAuditStorage.PrepareRoot(NewRoot());
        AddMacExtendedAcl(root);
        Assert.True(MacTestNative.HasExtendedAcl(root));
        Assert.Equal(SecureAuditStorage.OwnerDirectoryMode, File.GetUnixFileMode(root));

        var exception = Assert.Throws<IOException>(() =>
            SecureAuditStorage.VerifyProtectedDirectory(root));
        Assert.Contains("extended access-control list", exception.Message, StringComparison.Ordinal);

        // Restore owner-only protection so teardown is independent of the
        // deliberately sabotaged ACL.
        _ = SecureAuditStorage.PrepareRoot(root);
    }

    [Fact]
    public void Protected_file_fails_closed_after_macos_acl_sabotage()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var root = SecureAuditStorage.PrepareRoot(NewRoot());
        var path = Path.Combine(root, "audit.jsonl");
        using (SecureAuditStorage.CreateExclusiveFile(path))
        {
        }

        AddMacExtendedAcl(path);
        Assert.True(MacTestNative.HasExtendedAcl(path));
        Assert.Equal(SecureAuditStorage.OwnerFileMode, File.GetUnixFileMode(path));

        var exception = Assert.Throws<IOException>(() =>
            SecureAuditStorage.VerifyProtectedFile(path));
        Assert.Contains("extended access-control list", exception.Message, StringComparison.Ordinal);
    }

    private string NewRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-secure-storage-tests-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start macOS chmod.");
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new Win32Exception(process.ExitCode, standardError);
        }
    }

    private static class UnixTestNative
    {
        [DllImport("libc")]
        internal static extern uint geteuid();
    }

    private static class MacTestNative
    {
        private const int ExtendedAclType = 0x00000100;

        internal static bool HasExtendedAcl(string path)
        {
            var acl = acl_get_file(path, ExtendedAclType);
            if (acl == IntPtr.Zero)
                return false;

            _ = acl_free(acl);
            return true;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr acl_get_file(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int type);

        [DllImport("libc", SetLastError = true)]
        private static extern int acl_free(IntPtr value);
    }
}
