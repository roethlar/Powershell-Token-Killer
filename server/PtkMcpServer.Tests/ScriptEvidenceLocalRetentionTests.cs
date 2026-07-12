using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class ScriptEvidenceLocalRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "ptk-evidence-local-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Local_completion_publishes_distinct_retention_eligible_state()
    {
        var options = Options();
        var store = new ScriptEvidenceStore(options);

        var first = store.Store("Get-SecretValue");
        var firstPath = Assert.Single(ArtifactPaths(options));
        Assert.EndsWith(
            $"{first.EvidenceId}.{first.ScriptDigest}.local-committed.script",
            firstPath,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".anchored.script", firstPath, StringComparison.Ordinal);

        File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow - TimeSpan.FromMinutes(2));
        var second = store.Store("Get-OtherValue");

        var retained = Assert.Single(ArtifactPaths(options));
        Assert.DoesNotContain(first.EvidenceId, retained, StringComparison.Ordinal);
        Assert.Contains(second.EvidenceId, retained, StringComparison.Ordinal);
        Assert.EndsWith(".local-committed.script", retained, StringComparison.Ordinal);
    }

    [Fact]
    public void Anchored_completion_never_mislabels_evidence_as_local_or_anchored()
    {
        var options = Options(
            AuditProtectionMode.Anchored,
            new string('a', 64),
            evidenceAggregateBytes: 512);
        var store = new ScriptEvidenceStore(options);

        var reference = store.Store("Get-AnchoredPending");

        var path = Assert.Single(ArtifactPaths(options));
        Assert.EndsWith(
            $"{reference.EvidenceId}.{reference.ScriptDigest}.script",
            path,
            StringComparison.Ordinal);
        Assert.DoesNotContain("local-committed", path, StringComparison.Ordinal);
        Assert.DoesNotContain(".anchored.", path, StringComparison.Ordinal);
    }

    private AuditOptions Options(
        AuditProtectionMode mode = AuditProtectionMode.LocalOnly,
        string? identity = null,
        long evidenceAggregateBytes = 256) =>
        AuditOptions.Create(
            _root,
            mode,
            identity,
            maxEvidenceBytes: 256,
            evidenceAggregateBytes: evidenceAggregateBytes,
            evidenceRetentionAge: TimeSpan.FromMinutes(1));

    private static string[] ArtifactPaths(AuditOptions options) =>
        Directory.EnumerateFiles(options.EvidenceDirectory, "*.script")
            .Order(StringComparer.Ordinal)
            .ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
