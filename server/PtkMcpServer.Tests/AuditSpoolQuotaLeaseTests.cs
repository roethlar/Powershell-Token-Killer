using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditSpoolQuotaLeaseTests : IDisposable
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
    public void Writer_creation_publishes_the_exact_protected_one_byte_control()
    {
        var root = NewProtectedRoot();
        var path = ControlPath(root);

        using (AuditSpoolQuotaLease.CreateControlAndAcquire(root))
        {
            Assert.True(File.Exists(path));
        }

        Assert.Equal(new byte[] { 0x50 }, File.ReadAllBytes(path));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(SecureAuditStorage.OwnerFileMode, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Existing_probe_does_not_create_a_missing_control()
    {
        var root = NewProtectedRoot();
        var path = ControlPath(root);

        Assert.Throws<IOException>(() =>
            AuditSpoolQuotaLease.TryAcquireExisting(root, out _));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void Existing_probe_does_not_create_a_missing_root()
    {
        var root = NewAbsentRoot();

        Assert.Throws<IOException>(() =>
            AuditSpoolQuotaLease.TryAcquireExisting(root, out _));

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Existing_probe_returns_false_only_while_the_control_is_owned()
    {
        var root = NewProtectedRoot();
        var held = AuditSpoolQuotaLease.CreateControlAndAcquire(root);
        try
        {
            Assert.False(AuditSpoolQuotaLease.TryAcquireExisting(root, out var contender));
            Assert.Null(contender);
        }
        finally
        {
            held.Dispose();
        }

        Assert.True(AuditSpoolQuotaLease.TryAcquireExisting(root, out var acquired));
        using (acquired)
            acquired.VerifyOwnership();
    }

    [Fact]
    public async Task Existing_blocking_acquisition_waits_for_the_current_owner()
    {
        var root = NewProtectedRoot();
        var held = AuditSpoolQuotaLease.CreateControlAndAcquire(root);
        var contender = Task.Run(() => AuditSpoolQuotaLease.AcquireExisting(root));
        try
        {
            await Task.Delay(150);
            Assert.False(contender.IsCompleted);
            held.Dispose();
            using var acquired = await contender.WaitAsync(TimeSpan.FromSeconds(5));
            acquired.VerifyOwnership();
        }
        finally
        {
            held.Dispose();
            try
            {
                using var cleanup = await contender.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Preserve the primary assertion or acquisition failure.
            }
        }
    }

    [Theory]
    [MemberData(nameof(MalformedControls))]
    public void Existing_probe_rejects_a_malformed_control(byte[] bytes)
    {
        var root = NewProtectedRoot();
        WriteControl(root, bytes);

        Assert.Throws<IOException>(() =>
            AuditSpoolQuotaLease.TryAcquireExisting(root, out _));
    }

    [Fact]
    public void Existing_probe_rejects_broadened_control_permissions_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = NewProtectedRoot();
        var path = WriteControl(root, [0x50]);
        File.SetUnixFileMode(
            path,
            SecureAuditStorage.OwnerFileMode | UnixFileMode.GroupRead);

        Assert.Throws<IOException>(() =>
            AuditSpoolQuotaLease.TryAcquireExisting(root, out _));
    }

    [Fact]
    public void Retained_lease_detects_path_replacement_or_windows_denies_it()
    {
        var root = NewProtectedRoot();
        var path = ControlPath(root);
        var displaced = path + ".displaced";
        var lease = AuditSpoolQuotaLease.CreateControlAndAcquire(root);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                Assert.ThrowsAny<IOException>(() => File.Move(path, displaced));
            }
            finally
            {
                lease.Dispose();
            }
            return;
        }

        File.Move(path, displaced);
        WriteControl(root, [0x50]);
        Assert.Throws<IOException>(lease.Dispose);
    }

    public static TheoryData<byte[]> MalformedControls => new()
    {
        Array.Empty<byte>(),
        new byte[] { 0x51 },
        new byte[] { 0x50, 0x00 },
    };

    private string NewProtectedRoot()
    {
        var root = NewAbsentRoot();
        return SecureAuditStorage.PrepareRoot(root);
    }

    private string NewAbsentRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-audit-quota-tests-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private static string WriteControl(string root, ReadOnlySpan<byte> bytes)
    {
        var path = ControlPath(root);
        using var stream = SecureAuditStorage.CreateExclusiveFile(path);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return path;
    }

    private static string ControlPath(string root) =>
        Path.Combine(root, AuditSpoolQuotaLease.ControlFileName);
}
