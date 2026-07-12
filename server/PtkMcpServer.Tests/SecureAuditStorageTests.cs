using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
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

    [Fact]
    public void Atomic_replace_publishes_one_complete_protected_file()
    {
        var root = SecureAuditStorage.PrepareRoot(NewRoot());
        var published = Path.Combine(root, "checkpoint.json");
        var temporary = Path.Combine(root, ".checkpoint.replace.tmp");
        WriteProtected(published, "old-state");
        WriteProtected(temporary, "new-state");

        SecureAuditStorage.ReplaceAtomically(temporary, published, root);

        Assert.Equal("new-state", File.ReadAllText(published, Encoding.UTF8));
        Assert.False(File.Exists(temporary));
        SecureAuditStorage.VerifyProtectedFile(published);
    }

    [Fact]
    public void Atomic_replace_requires_the_caller_to_publish_the_initial_file_first()
    {
        var root = SecureAuditStorage.PrepareRoot(NewRoot());
        var published = Path.Combine(root, "checkpoint.json");
        var temporary = Path.Combine(root, ".checkpoint.replace.tmp");
        WriteProtected(temporary, "first-state");

        Assert.Throws<IOException>(() =>
            SecureAuditStorage.ReplaceAtomically(temporary, published, root));

        Assert.False(File.Exists(published));
        Assert.Equal("first-state", File.ReadAllText(temporary, Encoding.UTF8));
    }

    [Fact]
    public void Atomic_replace_post_commit_failure_preserves_the_complete_new_file()
    {
        var root = SecureAuditStorage.PrepareRoot(NewRoot());
        var published = Path.Combine(root, "checkpoint.json");
        var temporary = Path.Combine(root, ".checkpoint.replace.tmp");
        WriteProtected(published, "old-state");
        WriteProtected(temporary, "new-state");

        Assert.Throws<InvalidOperationException>(() =>
            SecureAuditStorage.ReplaceAtomically(
                temporary,
                published,
                root,
                () => throw new InvalidOperationException("injected post-replace failure")));

        Assert.Equal("new-state", File.ReadAllText(published, Encoding.UTF8));
        Assert.False(File.Exists(temporary));
        SecureAuditStorage.VerifyProtectedFile(published);
    }

    [Fact]
    public void Atomic_replace_rejects_a_file_outside_the_protected_parent()
    {
        var root = SecureAuditStorage.PrepareRoot(NewRoot());
        var otherRoot = SecureAuditStorage.PrepareRoot(NewRoot());
        var published = Path.Combine(root, "checkpoint.json");
        var temporary = Path.Combine(otherRoot, ".checkpoint.replace.tmp");
        WriteProtected(published, "old-state");
        WriteProtected(temporary, "new-state");

        Assert.Throws<IOException>(() =>
            SecureAuditStorage.ReplaceAtomically(temporary, published, root));

        Assert.Equal("old-state", File.ReadAllText(published, Encoding.UTF8));
        Assert.Equal("new-state", File.ReadAllText(temporary, Encoding.UTF8));
    }

    [Fact]
    public void Atomic_replace_rejects_a_published_file_outside_the_protected_parent()
    {
        var root = SecureAuditStorage.PrepareRoot(NewRoot());
        var otherRoot = SecureAuditStorage.PrepareRoot(NewRoot());
        var published = Path.Combine(otherRoot, "checkpoint.json");
        var temporary = Path.Combine(root, ".checkpoint.replace.tmp");
        WriteProtected(published, "old-state");
        WriteProtected(temporary, "new-state");

        Assert.Throws<IOException>(() =>
            SecureAuditStorage.ReplaceAtomically(temporary, published, root));

        Assert.Equal("old-state", File.ReadAllText(published, Encoding.UTF8));
        Assert.Equal("new-state", File.ReadAllText(temporary, Encoding.UTF8));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Atomic_replace_rejects_unprotected_input_files_without_modifying_them(
        bool sabotageTemporary)
    {
        var root = SecureAuditStorage.PrepareRoot(NewRoot());
        var published = Path.Combine(root, "checkpoint.json");
        var temporary = Path.Combine(root, ".checkpoint.replace.tmp");
        WriteProtected(published, "old-state");
        WriteProtected(temporary, "new-state");
        AddUnprotectedAccess(sabotageTemporary ? temporary : published, isDirectory: false);

        Assert.Throws<IOException>(() =>
            SecureAuditStorage.ReplaceAtomically(temporary, published, root));

        Assert.Equal("old-state", File.ReadAllText(published, Encoding.UTF8));
        Assert.Equal("new-state", File.ReadAllText(temporary, Encoding.UTF8));
        RestoreProtection(published, isDirectory: false);
        RestoreProtection(temporary, isDirectory: false);
    }

    [Fact]
    public void Atomic_replace_rejects_an_unprotected_root_without_modifying_files()
    {
        var root = SecureAuditStorage.PrepareRoot(NewRoot());
        var published = Path.Combine(root, "checkpoint.json");
        var temporary = Path.Combine(root, ".checkpoint.replace.tmp");
        WriteProtected(published, "old-state");
        WriteProtected(temporary, "new-state");
        AddUnprotectedAccess(root, isDirectory: true);

        Assert.Throws<IOException>(() =>
            SecureAuditStorage.ReplaceAtomically(temporary, published, root));

        Assert.Equal("old-state", File.ReadAllText(published, Encoding.UTF8));
        Assert.Equal("new-state", File.ReadAllText(temporary, Encoding.UTF8));
        RestoreProtection(root, isDirectory: true);
    }

    [Fact]
    public async Task Atomic_replace_is_never_observed_missing_or_partially_written()
    {
        var root = SecureAuditStorage.PrepareRoot(NewRoot());
        var published = Path.Combine(root, "checkpoint.json");
        var first = new string('a', 4096);
        var second = new string('b', 4096);
        WriteProtected(published, first);
        var failures = new ConcurrentQueue<string>();
        var firstObservation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observationCount = 0;
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var stream = new FileStream(
                        published,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var text = new StreamReader(
                        stream,
                        Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: false,
                        bufferSize: 1024,
                        leaveOpen: false);
                    var observed = text.ReadToEnd();
                    if (!string.Equals(observed, first, StringComparison.Ordinal) &&
                        !string.Equals(observed, second, StringComparison.Ordinal))
                    {
                        failures.Enqueue($"unexpected length {observed.Length}");
                    }
                    else
                    {
                        Interlocked.Increment(ref observationCount);
                        firstObservation.TrySetResult(true);
                    }
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception.GetType().Name);
                }
            }
        });

        var observationsBeforeReplacements = 0;
        try
        {
            await firstObservation.Task.WaitAsync(TimeSpan.FromSeconds(10));
            observationsBeforeReplacements = Volatile.Read(ref observationCount);
            for (var index = 0; index < 32; index++)
            {
                var temporary = Path.Combine(root, $".checkpoint.{index:D2}.tmp");
                WriteProtected(temporary, index % 2 == 0 ? second : first);
                SecureAuditStorage.ReplaceAtomically(temporary, published, root);
                if (index == 0)
                {
                    Assert.True(SpinWait.SpinUntil(
                        () => Volatile.Read(ref observationCount) > observationsBeforeReplacements,
                        TimeSpan.FromSeconds(10)));
                }
            }
        }
        finally
        {
            stop.Cancel();
            await reader.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Empty(failures);
        Assert.True(Volatile.Read(ref observationCount) > observationsBeforeReplacements);
    }

    [Fact]
    public void Windows_atomic_replace_flushes_new_bytes_before_commit_and_preserves_open_reader()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = SecureAuditStorage.PrepareRoot(NewRoot());
        var published = Path.Combine(root, "checkpoint.json");
        var temporary = Path.Combine(root, ".checkpoint.tmp");
        const string oldState = "old-state";
        const string newState = "new-state";
        WriteProtected(published, oldState);
        WriteProtected(temporary, newState);
        using var retained = new FileStream(
            published,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var flushObserved = false;
        var destinationCallbackObserved = false;

        SecureAuditStorage.ReplaceAtomically(
            temporary,
            published,
            root,
            destinationReplacedForTests: () =>
            {
                Assert.True(flushObserved);
                destinationCallbackObserved = true;
            },
            windowsFileFlushedForTests: () =>
            {
                Assert.False(destinationCallbackObserved);
                Assert.True(File.Exists(published));
                Assert.False(File.Exists(temporary));
                flushObserved = true;
            });

        var heldBytes = new byte[Encoding.UTF8.GetByteCount(oldState)];
        retained.ReadExactly(heldBytes);
        Assert.True(flushObserved);
        Assert.True(destinationCallbackObserved);
        Assert.Equal(oldState, Encoding.UTF8.GetString(heldBytes));
        Assert.Equal(newState, File.ReadAllText(published, Encoding.UTF8));
        Assert.False(File.Exists(temporary));
        SecureAuditStorage.VerifyExternalProtectedFile(published);
    }

    private string NewRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-secure-storage-tests-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private static void WriteProtected(string path, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            using var stream = SecureAuditStorage.CreateExclusiveFile(path);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    private static void AddUnprotectedAccess(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            AddWindowsWorldReadAccess(path, isDirectory);
            return;
        }

        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(
            path,
            mode | (isDirectory ? UnixFileMode.GroupExecute : UnixFileMode.GroupRead));
    }

    private static void RestoreProtection(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            _ = SecureAuditStorage.PrepareRoot(path);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            SecureAuditStorage.VerifyProtectedFile(path);
            return;
        }

        File.SetUnixFileMode(path, SecureAuditStorage.OwnerFileMode);
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsWorldReadAccess(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            isDirectory ? FileSystemRights.ReadAndExecute : FileSystemRights.Read,
            AccessControlType.Allow));
        if (isDirectory)
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), (DirectorySecurity)security);
        else
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), (FileSecurity)security);
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
