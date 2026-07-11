using System.Security.Cryptography;
using System.Text;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class ScriptEvidenceStoreTests : IDisposable
{
    private readonly string _parent = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ptk-script-evidence-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_parent))
        {
            Directory.Delete(_parent, recursive: true);
        }
    }

    [Fact]
    public void Store_preserves_exact_strict_utf8_bytes_and_returns_only_opaque_metadata()
    {
        var root = Path.Combine(_parent, "evidence");
        var script = "Write-Output \"päss🔐\"\r\n$x = '  exact  '\n";
        var expected = new UTF8Encoding(false, true).GetBytes(script);
        var store = new ScriptEvidenceStore(root);

        var reference = store.Store(script);

        var published = Assert.Single(Directory.GetFiles(root, "*.script"));
        Assert.Equal(expected, File.ReadAllBytes(published));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(),
            reference.ScriptDigest);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", reference.EvidenceId);
        Assert.Equal(
            reference.EvidenceId + "." + reference.ScriptDigest + ".script",
            Path.GetFileName(published));
        Assert.DoesNotContain(script, reference.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public void Store_accepts_the_exact_byte_limit_and_rejects_one_byte_more_without_an_artifact()
    {
        var root = Path.Combine(_parent, "evidence");
        var store = new ScriptEvidenceStore(root);

        var acceptedScript = new string('é', ScriptEvidenceStore.MaximumScriptBytes / 2);
        var accepted = store.Store(acceptedScript);
        Assert.True(File.Exists(EvidencePath(root, accepted)));
        Assert.Equal(
            ScriptEvidenceStore.MaximumScriptBytes,
            new FileInfo(EvidencePath(root, accepted)).Length);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Store(acceptedScript + "a"));
        Assert.Single(Directory.GetFiles(root, "*.script"));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public void Store_rejects_an_unpaired_surrogate_without_an_artifact_or_script_in_the_error()
    {
        var root = Path.Combine(_parent, "evidence");
        var store = new ScriptEvidenceStore(root);
        var script = "secret-before-\ud800-secret-after";

        var error = Assert.Throws<ArgumentException>(() => store.Store(script));

        Assert.DoesNotContain("secret-before", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(root, "*.script"));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Theory]
    [InlineData(SecureAuditStorageFaultStage.Write)]
    [InlineData(SecureAuditStorageFaultStage.Flush)]
    [InlineData(SecureAuditStorageFaultStage.Publish)]
    public void Every_fault_boundary_fails_without_a_published_reference_or_sensitive_error(
        SecureAuditStorageFaultStage failingStage)
    {
        var root = Path.Combine(_parent, "evidence");
        const string script = "Invoke-Thing -Token top-secret-value";
        var secretFault = new IOException("top-secret-value at " + root);
        var store = new ScriptEvidenceStore(
            root,
            stage =>
            {
                if (stage == failingStage)
                {
                    throw secretFault;
                }
            });

        var error = Assert.Throws<ScriptEvidenceStorageException>(() => store.Store(script));

        Assert.Equal("Protected script evidence storage failed.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("top-secret-value", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(root, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(root, "*.script"));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public void Store_uses_owner_only_unix_permissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(_parent, "evidence");
        var reference = new ScriptEvidenceStore(root).Store("'permission probe'");

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(root));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(EvidencePath(root, reference)));
    }

    [Fact]
    public void Store_refuses_same_length_corruption_of_a_published_evidence_artifact()
    {
        var root = Path.Combine(_parent, "evidence-integrity");
        var store = new ScriptEvidenceStore(root);
        var reference = store.Store("abc");
        File.WriteAllText(EvidencePath(root, reference), "abd");

        var error = Assert.Throws<ScriptEvidenceStorageException>(() =>
            store.Store("next"));

        Assert.Equal("Protected script evidence storage failed.", error.Message);
        Assert.Single(Directory.GetFiles(root, "*.script"));
    }

    [Fact]
    public void Store_refuses_a_broadened_evidence_directory()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(_parent, "evidence");
        var store = new ScriptEvidenceStore(root);
        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        var error = Assert.Throws<ScriptEvidenceStorageException>(() => store.Store("'blocked'"));

        Assert.Equal("Protected script evidence storage failed.", error.Message);
        Assert.Empty(Directory.GetFiles(root, "*.script"));
    }

    [Fact]
    public void Constructor_refuses_a_symlinked_evidence_root_without_disclosing_its_path()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_parent);
        var realRoot = Path.Combine(_parent, "real");
        var linkedRoot = Path.Combine(_parent, "linked");
        Directory.CreateDirectory(realRoot);
        Directory.CreateSymbolicLink(linkedRoot, realRoot);

        var error = Assert.Throws<ScriptEvidenceStorageException>(() =>
            new ScriptEvidenceStore(linkedRoot));

        Assert.DoesNotContain(linkedRoot, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(realRoot));
    }

    [Fact]
    public void Constructor_refuses_a_symlinked_ancestor_even_when_the_root_does_not_exist()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_parent);
        var realParent = Path.Combine(_parent, "real-parent");
        var linkedParent = Path.Combine(_parent, "linked-parent");
        Directory.CreateDirectory(realParent);
        Directory.CreateSymbolicLink(linkedParent, realParent);
        var redirectedRoot = Path.Combine(linkedParent, "evidence");

        var error = Assert.Throws<ScriptEvidenceStorageException>(() =>
            new ScriptEvidenceStore(redirectedRoot));

        Assert.DoesNotContain(redirectedRoot, error.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(realParent, "evidence")));
    }

    [Fact]
    public void Local_only_store_refuses_capacity_without_deleting_referenced_evidence()
    {
        var options = EvidenceOptions(
            Path.Combine(_parent, "quota-audit"),
            AuditProtectionMode.LocalOnly,
            aggregateBytes: 512);
        var store = new ScriptEvidenceStore(options);
        var first = store.Store(new string('a', 256));
        File.SetLastWriteTimeUtc(
            EvidencePath(options.EvidenceDirectory, first),
            DateTime.UtcNow.AddSeconds(-5));
        var second = store.Store(new string('b', 256));
        var error = Assert.Throws<ScriptEvidenceStorageException>(() =>
            store.Store(new string('c', 256)));

        Assert.Equal("Protected script evidence storage failed.", error.Message);
        Assert.True(File.Exists(EvidencePath(options.EvidenceDirectory, first)));
        Assert.True(File.Exists(EvidencePath(options.EvidenceDirectory, second)));
        Assert.Equal(2, Directory.GetFiles(options.EvidenceDirectory, "*.script").Length);
        Assert.Equal(512, Directory.GetFiles(options.EvidenceDirectory, "*.script").Sum(path => new FileInfo(path).Length));
    }

    [Fact]
    public void Anchored_store_refuses_capacity_exhaustion_without_deleting_evidence()
    {
        var options = EvidenceOptions(
            Path.Combine(_parent, "anchored-audit"),
            AuditProtectionMode.Anchored,
            aggregateBytes: 512);
        var store = new ScriptEvidenceStore(options);
        var first = store.Store(new string('a', 256));
        var second = store.Store(new string('b', 256));

        var error = Assert.Throws<ScriptEvidenceStorageException>(() =>
            store.Store(new string('c', 256)));

        Assert.Equal("Protected script evidence storage failed.", error.Message);
        Assert.True(File.Exists(EvidencePath(options.EvidenceDirectory, first)));
        Assert.True(File.Exists(EvidencePath(options.EvidenceDirectory, second)));
        Assert.Equal(2, Directory.GetFiles(options.EvidenceDirectory, "*.script").Length);
    }

    [Fact]
    public async Task Case_aliases_of_one_evidence_root_share_the_filesystem_quota_lock()
    {
        var root = Path.Combine(_parent, "case-alias-evidence");
        var first = new ScriptEvidenceStore(root);
        var alias = FindCaseAlias(root);
        if (alias is null) return;
        var second = new ScriptEvidenceStore(alias);

        var held = first.AcquireQuotaLockForTests();
        var contender = Task.Run(second.AcquireQuotaLockForTests);
        try
        {
            await Task.Delay(150);
            Assert.False(contender.IsCompleted, "case alias bypassed the evidence quota lock");
            held.Dispose();
            using var acquired = await contender.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            held.Dispose();
            try
            {
                using var cleanup = await contender.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { /* preserve primary failure */ }
        }
    }

    [Fact]
    public void Local_only_store_does_not_age_out_evidence_without_journal_aware_gc()
    {
        var options = EvidenceOptions(
            Path.Combine(_parent, "retention-audit"),
            AuditProtectionMode.LocalOnly,
            aggregateBytes: 1024);
        var store = new ScriptEvidenceStore(options);
        var expired = store.Store("old");
        var expiredPath = EvidencePath(options.EvidenceDirectory, expired);
        File.SetLastWriteTimeUtc(expiredPath, DateTime.UtcNow.AddMinutes(-2));

        var current = store.Store("new");

        Assert.True(File.Exists(expiredPath));
        Assert.True(File.Exists(EvidencePath(options.EvidenceDirectory, current)));
    }

    private static AuditOptions EvidenceOptions(
        string root,
        AuditProtectionMode protectionMode,
        long aggregateBytes)
    {
        const int recordBytes = 512;
        return AuditOptions.Create(
            root,
            protectionMode,
            protectionMode == AuditProtectionMode.Anchored ? new string('a', 64) : null,
            maxRecordBytes: recordBytes,
            segmentBytes: recordBytes * 32L,
            aggregateBytes: recordBytes * 32L,
            emergencyReserveBytes: recordBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: 256,
            evidenceAggregateBytes: aggregateBytes,
            evidenceRetentionAge: TimeSpan.FromMinutes(1));
    }

    private static string EvidencePath(string root, ScriptEvidenceReference reference) =>
        Path.Combine(root, reference.EvidenceId + "." + reference.ScriptDigest + ".script");

    private static string? FindCaseAlias(string path)
    {
        for (var index = 0; index < path.Length; index++)
        {
            if (!char.IsLetter(path[index])) continue;
            var characters = path.ToCharArray();
            characters[index] = char.IsUpper(characters[index])
                ? char.ToLowerInvariant(characters[index])
                : char.ToUpperInvariant(characters[index]);
            var candidate = new string(characters);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
