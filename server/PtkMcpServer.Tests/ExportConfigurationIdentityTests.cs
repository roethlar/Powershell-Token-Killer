using System.Security.Cryptography;
using System.Text;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class ExportConfigurationIdentityTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* Preserve the assertion failure that prevented cleanup. */ }
        }
    }

    [Fact]
    public void Identity_has_a_frozen_cross_implementation_vector()
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var material = Material(
            headers:
            [
                new("X-API-Key", "bravo"),
                new("Authorization", "Bearer alpha"),
            ],
            ca: [1, 2],
            certificate: [3],
            privateKey: [4, 5]);

        var identity = ExportConfigurationIdentity.Compute(key, material);

        Assert.Equal("4b9686d742964ff6f1e20b4d2bbe3cdf4764a8bf0258a41c92600d8de15f0cfb", identity);
    }

    [Fact]
    public void Header_order_and_name_case_do_not_change_identity()
    {
        var key = Enumerable.Repeat((byte)0x5a, 32).ToArray();
        var left = Material(headers:
        [
            new("Authorization", "Bearer alpha"),
            new("X-API-Key", "bravo"),
        ]);
        var right = Material(headers:
        [
            new("x-api-key", "bravo"),
            new("AUTHORIZATION", "Bearer alpha"),
        ]);

        Assert.Equal(
            ExportConfigurationIdentity.Compute(key, left),
            ExportConfigurationIdentity.Compute(key, right));
    }

    [Fact]
    public void Revocation_check_mode_is_covered_by_the_identity()
    {
        var key = Enumerable.Repeat((byte)0x5a, 32).ToArray();

        Assert.NotEqual(
            ExportConfigurationIdentity.Compute(key, Material(revocationCheckMode: "Online")),
            ExportConfigurationIdentity.Compute(key, Material(revocationCheckMode: "NoCheck")));
    }

    [Fact]
    public void Persistent_hmac_key_is_part_of_the_identity_boundary()
    {
        var material = Material();

        Assert.NotEqual(
            ExportConfigurationIdentity.Compute(new byte[32], material),
            ExportConfigurationIdentity.Compute(Enumerable.Repeat((byte)1, 32).ToArray(), material));
    }

    [Fact]
    public void Every_effective_configuration_field_changes_identity()
    {
        var key = Enumerable.Repeat((byte)0x33, 32).ToArray();
        var baseline = Material();
        var baselineIdentity = ExportConfigurationIdentity.Compute(key, baseline);
        AuditExportConfigurationMaterial[] variants =
        [
            baseline with { Endpoint = "https://collector.example:4318/other" },
            baseline with { Headers = [new("Authorization", "Bearer changed")] },
            baseline with { CustomCaBytes = new byte[] { 9 } },
            baseline with { ClientCertificateBytes = new byte[] { 8 } },
            baseline with { ClientPrivateKeyBytes = new byte[] { 7 } },
            baseline with { Protocol = "future/protocol" },
            baseline with { TimeoutMilliseconds = 10_001 },
        ];

        Assert.All(variants, variant =>
            Assert.NotEqual(baselineIdentity, ExportConfigurationIdentity.Compute(key, variant)));
    }

    [Fact]
    public void Duplicate_header_names_are_rejected_case_insensitively()
    {
        var key = new byte[32];
        var material = Material(headers:
        [
            new("Authorization", "one"),
            new("authorization", "two"),
        ]);

        Assert.Throws<ArgumentException>(() => ExportConfigurationIdentity.Compute(key, material));
    }

    [Fact]
    public void Invalid_utf16_is_rejected_instead_of_collapsing_distinct_values()
    {
        var key = new byte[32];

        Assert.Throws<EncoderFallbackException>(() =>
            ExportConfigurationIdentity.Compute(key, Material() with { Endpoint = "https://example.test/\ud800" }));
        Assert.Throws<EncoderFallbackException>(() =>
            ExportConfigurationIdentity.Compute(key, Material() with { Endpoint = "https://example.test/\ud801" }));
    }

    [Fact]
    public void Persistent_key_is_exactly_32_bytes_and_reused()
    {
        var root = NewRoot();
        var first = ExportConfigurationKeyStore.LoadOrCreate(root);
        var second = ExportConfigurationKeyStore.LoadOrCreate(root);
        try
        {
            Assert.Equal(ExportConfigurationKeyStore.KeyBytes, first.Length);
            Assert.Equal(first, second);
            var keyPath = Path.Combine(root, ExportConfigurationKeyStore.FileName);
            Assert.Equal(ExportConfigurationKeyStore.KeyBytes, new FileInfo(keyPath).Length);
            SecureAuditStorage.VerifyProtectedFile(keyPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(first);
            CryptographicOperations.ZeroMemory(second);
        }
    }

    [Fact]
    public async Task Concurrent_first_creation_converges_on_one_key()
    {
        var root = NewRoot();
        const int contenderCount = 8;
        using var start = new ManualResetEventSlim(initialState: false);
        using var destinationChecked = new Barrier(contenderCount);
        var tasks = Enumerable.Range(0, contenderCount)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.Wait();
                    return ExportConfigurationKeyStore.LoadOrCreate(
                        root,
                        () =>
                        {
                            if (!destinationChecked.SignalAndWait(TimeSpan.FromSeconds(10)))
                                throw new TimeoutException("Concurrent key publishers did not rendezvous.");
                        });
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
        start.Set();
        var keys = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            Assert.All(keys, key => Assert.Equal(keys[0], key));
            Assert.Equal(ExportConfigurationKeyStore.KeyBytes, new FileInfo(
                Path.Combine(root, ExportConfigurationKeyStore.FileName)).Length);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(root),
                path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var key in keys) CryptographicOperations.ZeroMemory(key);
        }
    }

    [Fact]
    public void Corrupt_existing_key_is_never_regenerated()
    {
        var root = NewRoot();
        WriteProtected(root, ExportConfigurationKeyStore.FileName, new byte[31]);

        Assert.Throws<IOException>(() => ExportConfigurationKeyStore.LoadOrCreate(root));
        Assert.Equal(31, new FileInfo(Path.Combine(root, ExportConfigurationKeyStore.FileName)).Length);
    }

    [Fact]
    public void Existing_checkpoint_without_key_fails_closed()
    {
        var root = NewRoot();
        WriteProtected(root, "export.checkpoint.test.json", "{}"u8.ToArray());

        Assert.Throws<IOException>(() => ExportConfigurationKeyStore.LoadOrCreate(root));
        Assert.False(File.Exists(Path.Combine(root, ExportConfigurationKeyStore.FileName)));
    }

    [Fact]
    public void Crash_left_temporary_key_is_ignored_not_promoted()
    {
        var root = NewRoot();
        var staleName = $".{ExportConfigurationKeyStore.FileName}.stale.tmp";
        var stale = Enumerable.Repeat((byte)0x77, ExportConfigurationKeyStore.KeyBytes).ToArray();
        WriteProtected(root, staleName, stale);

        var key = ExportConfigurationKeyStore.LoadOrCreate(root);
        try
        {
            Assert.NotEqual(stale, key);
            Assert.Equal(stale, File.ReadAllBytes(Path.Combine(root, staleName)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(stale);
        }
    }

    [Fact]
    public void Key_store_refuses_a_symlinked_key_and_linked_root_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = NewRoot();
        var targetKey = Path.Combine(root, "target.key");
        WriteProtected(root, Path.GetFileName(targetKey), new byte[ExportConfigurationKeyStore.KeyBytes]);
        File.CreateSymbolicLink(
            Path.Combine(root, ExportConfigurationKeyStore.FileName),
            targetKey);
        Assert.ThrowsAny<IOException>(() => ExportConfigurationKeyStore.LoadOrCreate(root));

        var realRoot = NewRoot();
        var linkedRoot = Path.Combine(root, "linked-root");
        Directory.CreateSymbolicLink(linkedRoot, realRoot);
        Assert.ThrowsAny<IOException>(() => ExportConfigurationKeyStore.LoadOrCreate(linkedRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(realRoot));
    }

    private string NewRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "test-export-identity-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return SecureAuditStorage.PrepareRoot(root);
    }

    private static void WriteProtected(string root, string name, ReadOnlySpan<byte> bytes)
    {
        var path = Path.Combine(root, name);
        using var stream = SecureAuditStorage.CreateExclusiveFile(path);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static AuditExportConfigurationMaterial Material(
        IReadOnlyList<AuditExportHeaderMaterial>? headers = null,
        byte[]? ca = null,
        byte[]? certificate = null,
        byte[]? privateKey = null,
        string revocationCheckMode = "Online") =>
        new(
            "https://collector.example:4318/v1/logs",
            headers ?? [new("Authorization", "Bearer alpha")],
            ca ?? Array.Empty<byte>(),
            certificate ?? Array.Empty<byte>(),
            privateKey ?? Array.Empty<byte>(),
            "http/protobuf",
            10_000,
            revocationCheckMode);
}
