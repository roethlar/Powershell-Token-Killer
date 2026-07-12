using System.Text;
using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditAdminEvidenceAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "ptk-admin-evidence-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Read_commits_intent_before_store_inventory_and_records_sanitized_outcome()
    {
        var options = Options();
        var stored = new ScriptEvidenceStore(options).Store("Get-HighlySensitiveValue");
        using var fixture = Journal(options);
        var factoryCalled = false;
        var operations = new AuditAdminOperations(
            options,
            fixture.Journal,
            () =>
            {
                factoryCalled = true;
                Assert.Equal(1, fixture.Journal.Sequence);
                Assert.Equal("evidence.read_intent", EventType(fixture.Sink.Lines[0]));
                return new ScriptEvidenceStore(options);
            });
        using var output = new MemoryStream();

        var read = operations.ReadEvidence(stored.EvidenceId, output);

        Assert.True(factoryCalled);
        Assert.Equal(stored, read);
        Assert.Equal("Get-HighlySensitiveValue", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(2, fixture.Sink.Lines.Count);
        Assert.Equal("evidence.read_completed", EventType(fixture.Sink.Lines[1]));
        var auditText = string.Concat(
            fixture.Sink.Lines.Select(line => Encoding.UTF8.GetString(line)));
        Assert.DoesNotContain("HighlySensitive", auditText, StringComparison.Ordinal);
        Assert.DoesNotContain(options.EvidenceDirectory, auditText, StringComparison.Ordinal);
        using var outcome = JsonDocument.Parse(fixture.Sink.Lines[1]);
        Assert.Equal(
            stored.ScriptDigest,
            outcome.RootElement.GetProperty("request").GetProperty("original_script_digest").GetString());
        Assert.Equal(
            stored.ByteLength,
            outcome.RootElement.GetProperty("outcome").GetProperty("bytes_returned").GetInt64());
    }

    [Fact]
    public void Missing_evidence_records_failure_after_intent_without_writing_bytes()
    {
        var options = Options();
        using var fixture = Journal(options);
        var operations = new AuditAdminOperations(options, fixture.Journal);
        using var output = new MemoryStream();
        var missing = Guid.NewGuid().ToString("D");

        Assert.Throws<AuditAdminOperationException>(() =>
            operations.ReadEvidence(missing, output));

        Assert.Empty(output.ToArray());
        Assert.Equal(
            ["evidence.read_intent", "evidence.read_failed"],
            fixture.Sink.Lines.Select(EventType).ToArray());
    }

    [Fact]
    public void Export_creates_one_protected_file_and_never_audits_its_path()
    {
        var options = Options();
        var stored = new ScriptEvidenceStore(options).Store("Get-ExportedSecret");
        var outputRoot = SecureAuditStorage.PrepareRoot(Path.Combine(_root, "operator-output"));
        var outputPath = Path.Combine(outputRoot, "evidence.bin");
        using var fixture = Journal(options);
        var operations = new AuditAdminOperations(options, fixture.Journal);

        operations.ExportEvidence(stored.EvidenceId, outputPath);

        Assert.Equal("Get-ExportedSecret", File.ReadAllText(outputPath));
        SecureAuditStorage.VerifyExternalProtectedFile(outputPath);
        Assert.Equal(
            ["evidence.export_intent", "evidence.export_completed"],
            fixture.Sink.Lines.Select(EventType).ToArray());
        var auditText = string.Concat(
            fixture.Sink.Lines.Select(line => Encoding.UTF8.GetString(line)));
        Assert.DoesNotContain(outputPath, auditText, StringComparison.Ordinal);
        Assert.DoesNotContain("ExportedSecret", auditText, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_refuses_existing_link_after_intent_and_records_failure()
    {
        if (OperatingSystem.IsWindows()) return;
        var options = Options();
        var stored = new ScriptEvidenceStore(options).Store("Get-LinkTarget");
        var outputRoot = SecureAuditStorage.PrepareRoot(Path.Combine(_root, "operator-link-output"));
        var target = Path.Combine(outputRoot, "target.bin");
        File.WriteAllText(target, "unchanged");
        File.SetUnixFileMode(target, SecureAuditStorage.OwnerFileMode);
        var link = Path.Combine(outputRoot, "evidence.bin");
        File.CreateSymbolicLink(link, target);
        using var fixture = Journal(options);
        var operations = new AuditAdminOperations(options, fixture.Journal);

        Assert.Throws<AuditAdminOperationException>(() =>
            operations.ExportEvidence(stored.EvidenceId, link));

        Assert.Equal("unchanged", File.ReadAllText(target));
        Assert.True(File.ResolveLinkTarget(link, returnFinalTarget: false) is not null);
        Assert.Equal(
            ["evidence.export_intent", "evidence.export_failed"],
            fixture.Sink.Lines.Select(EventType).ToArray());
    }

    private AuditOptions Options() => AuditOptions.Create(
        _root,
        maxEvidenceBytes: 1024,
        evidenceAggregateBytes: 16 * 1024,
        evidenceRetentionAge: TimeSpan.FromMinutes(10));

    private static JournalFixture Journal(AuditOptions options)
    {
        var sink = new InMemoryAuditJournalSink(
            options.SegmentBytes,
            options.AggregateBytes,
            options.ProtectionMode,
            options.RetentionAge);
        var journal = new AuditJournal(
            options,
            new AuditHealth(options),
            sink,
            "test",
            binaryDigest: null,
            Guid.NewGuid(),
            Guid.NewGuid());
        return new JournalFixture(journal, sink);
    }

    private static string EventType(byte[] line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("event_type").GetString()!;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record JournalFixture(
        AuditJournal Journal,
        InMemoryAuditJournalSink Sink) : IDisposable
    {
        public void Dispose() => Journal.Dispose();
    }
}
