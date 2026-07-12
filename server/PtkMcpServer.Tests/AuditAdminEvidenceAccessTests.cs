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
            "stdout",
            outcome.RootElement.GetProperty("request").GetProperty("destination_kind").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            outcome.RootElement.GetProperty("request").GetProperty("destination_path").ValueKind);
        Assert.Equal(
            stored.ScriptDigest,
            outcome.RootElement.GetProperty("request").GetProperty("original_script_digest").GetString());
        Assert.Equal(
            stored.ByteLength,
            outcome.RootElement.GetProperty("outcome").GetProperty("bytes_returned").GetInt64());
        AssertEffectiveAdminIdentity(outcome.RootElement);
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
        using var failure = JsonDocument.Parse(fixture.Sink.Lines[1]);
        Assert.Equal(
            "evidence.absent",
            failure.RootElement.GetProperty("outcome").GetProperty("detail_code").GetString());
        Assert.Equal(
            0,
            failure.RootElement.GetProperty("outcome").GetProperty("bytes_returned").GetInt64());
    }

    [Fact]
    public void Invalid_evidence_id_records_specific_failure_after_intent()
    {
        var options = Options();
        using var fixture = Journal(options);
        var operations = new AuditAdminOperations(options, fixture.Journal);
        using var output = new MemoryStream();

        Assert.Throws<AuditAdminOperationException>(() =>
            operations.ReadEvidence("NOT-A-CANONICAL-ID", output));

        Assert.Equal(
            ["evidence.read_intent", "evidence.read_failed"],
            fixture.Sink.Lines.Select(EventType).ToArray());
        Assert.Equal("evidence.id_invalid", DetailCode(fixture.Sink.Lines[1]));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public void Invalid_evidence_control_records_specific_failure_without_disclosure()
    {
        var options = Options();
        var stored = new ScriptEvidenceStore(options).Store("Get-ControlledSecret");
        var artifact = Directory.GetFiles(
            options.EvidenceDirectory,
            $"{stored.EvidenceId}.*.script").Single();
        File.WriteAllText(artifact, "tampered");
        using var fixture = Journal(options);
        var operations = new AuditAdminOperations(options, fixture.Journal);
        using var output = new MemoryStream();

        Assert.Throws<AuditAdminOperationException>(() =>
            operations.ReadEvidence(stored.EvidenceId, output));

        Assert.Equal("evidence.control_invalid", DetailCode(fixture.Sink.Lines[1]));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public void Evidence_storage_failure_records_specific_failure_without_disclosure()
    {
        var options = Options();
        using var fixture = Journal(options);
        var operations = new AuditAdminOperations(
            options,
            fixture.Journal,
            () => throw new ScriptEvidenceStorageException(
                ScriptEvidenceStorageFailureKind.Storage));
        using var output = new MemoryStream();

        Assert.Throws<AuditAdminOperationException>(() =>
            operations.ReadEvidence(Guid.NewGuid().ToString("D"), output));

        Assert.Equal("evidence.storage_failed", DetailCode(fixture.Sink.Lines[1]));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public void Read_flush_failure_records_full_disclosure_after_write_returned()
    {
        var options = Options();
        var stored = new ScriptEvidenceStore(options).Store("Get-FlushedSecret");
        using var fixture = Journal(options);
        var operations = new AuditAdminOperations(options, fixture.Journal);
        using var output = new FlushFailingStream();

        Assert.Throws<AuditAdminOperationException>(() =>
            operations.ReadEvidence(stored.EvidenceId, output));

        Assert.Equal("Get-FlushedSecret", Encoding.UTF8.GetString(output.Bytes));
        using var failure = JsonDocument.Parse(fixture.Sink.Lines[1]);
        var request = failure.RootElement.GetProperty("request");
        var outcome = failure.RootElement.GetProperty("outcome");
        Assert.Equal(stored.ScriptDigest, request.GetProperty("original_script_digest").GetString());
        Assert.Equal(
            "operation.flush_failed_after_disclosure",
            outcome.GetProperty("detail_code").GetString());
        Assert.Equal(stored.ByteLength, outcome.GetProperty("bytes_returned").GetInt64());
    }

    [Fact]
    public void Read_write_failure_records_unknown_partial_disclosure()
    {
        var options = Options();
        var stored = new ScriptEvidenceStore(options).Store("Get-PartialSecret");
        using var fixture = Journal(options);
        var operations = new AuditAdminOperations(options, fixture.Journal);
        using var output = new PartialWriteFailingStream();

        Assert.Throws<AuditAdminOperationException>(() =>
            operations.ReadEvidence(stored.EvidenceId, output));

        Assert.NotEmpty(output.Bytes);
        using var failure = JsonDocument.Parse(fixture.Sink.Lines[1]);
        var request = failure.RootElement.GetProperty("request");
        var outcome = failure.RootElement.GetProperty("outcome");
        Assert.Equal(stored.ScriptDigest, request.GetProperty("original_script_digest").GetString());
        Assert.Equal(
            "operation.disclosure_unknown",
            outcome.GetProperty("detail_code").GetString());
        Assert.Equal(JsonValueKind.Null, outcome.GetProperty("bytes_returned").ValueKind);
    }

    [Fact]
    public void Read_failure_after_flush_records_failed_audit_outcome_after_disclosure()
    {
        var options = Options();
        var stored = new ScriptEvidenceStore(options).Store("Get-DisclosedSecret");
        using var fixture = Journal(options);
        var operations = new AuditAdminOperations(
            options,
            fixture.Journal,
            beforeEvidenceOutcomeAppendForTests: () =>
                throw new IOException("injected post-disclosure failure"));
        using var output = new MemoryStream();

        Assert.Throws<AuditAdminOperationException>(() =>
            operations.ReadEvidence(stored.EvidenceId, output));

        Assert.Equal("Get-DisclosedSecret", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(
            "audit.outcome_failed_after_disclosure",
            DetailCode(fixture.Sink.Lines[1]));
        using var failure = JsonDocument.Parse(fixture.Sink.Lines[1]);
        Assert.Equal(
            stored.ByteLength,
            failure.RootElement.GetProperty("outcome").GetProperty("bytes_returned").GetInt64());
    }

    [Fact]
    public void Export_records_its_protected_destination_without_evidence_bytes()
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
        Assert.DoesNotContain("ExportedSecret", auditText, StringComparison.Ordinal);
        using var outcome = JsonDocument.Parse(fixture.Sink.Lines[1]);
        Assert.Equal(
            "protected_file",
            outcome.RootElement.GetProperty("request").GetProperty("destination_kind").GetString());
        Assert.Equal(
            Path.GetFullPath(outputPath),
            outcome.RootElement.GetProperty("request").GetProperty("destination_path").GetString());
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
        Assert.Equal("evidence.destination_exists", DetailCode(fixture.Sink.Lines[1]));
    }

    [Fact]
    public void Export_refuses_relative_path_with_specific_failure()
    {
        var options = Options();
        var stored = new ScriptEvidenceStore(options).Store("Get-RelativeSecret");
        using var fixture = Journal(options);
        var operations = new AuditAdminOperations(options, fixture.Journal);

        Assert.Throws<AuditAdminOperationException>(() =>
            operations.ExportEvidence(stored.EvidenceId, "relative-evidence.bin"));

        Assert.Equal("evidence.path_invalid", DetailCode(fixture.Sink.Lines[1]));
    }

    [Fact]
    public void Export_refuses_unprotected_parent_with_specific_failure()
    {
        if (OperatingSystem.IsWindows()) return;
        var options = Options();
        var stored = new ScriptEvidenceStore(options).Store("Get-RefusedSecret");
        var outputRoot = Path.Combine(_root, "unprotected-output");
        Directory.CreateDirectory(outputRoot);
        File.SetUnixFileMode(
            outputRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        using var fixture = Journal(options);
        var operations = new AuditAdminOperations(options, fixture.Journal);

        Assert.Throws<AuditAdminOperationException>(() =>
            operations.ExportEvidence(stored.EvidenceId, Path.Combine(outputRoot, "evidence.bin")));

        Assert.Equal("evidence.destination_refused", DetailCode(fixture.Sink.Lines[1]));
    }

    [Fact]
    public void Export_refuses_existing_file_with_specific_failure()
    {
        var options = Options();
        var stored = new ScriptEvidenceStore(options).Store("Get-ExistingSecret");
        var outputRoot = SecureAuditStorage.PrepareRoot(Path.Combine(_root, "existing-output"));
        var outputPath = Path.Combine(outputRoot, "evidence.bin");
        File.WriteAllText(outputPath, "unchanged");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(outputPath, SecureAuditStorage.OwnerFileMode);
        using var fixture = Journal(options);
        var operations = new AuditAdminOperations(options, fixture.Journal);

        Assert.Throws<AuditAdminOperationException>(() =>
            operations.ExportEvidence(stored.EvidenceId, outputPath));

        Assert.Equal("unchanged", File.ReadAllText(outputPath));
        Assert.Equal("evidence.destination_exists", DetailCode(fixture.Sink.Lines[1]));
    }

    [Fact]
    public void Export_failure_after_publish_records_failed_audit_outcome_and_retracts_file()
    {
        var options = Options();
        var stored = new ScriptEvidenceStore(options).Store("Get-PublishedSecret");
        var outputRoot = SecureAuditStorage.PrepareRoot(Path.Combine(_root, "published-output"));
        var outputPath = Path.Combine(outputRoot, "evidence.bin");
        using var fixture = Journal(options);
        var operations = new AuditAdminOperations(
            options,
            fixture.Journal,
            beforeEvidenceOutcomeAppendForTests: () =>
                throw new IOException("injected post-publication failure"));

        Assert.Throws<AuditAdminOperationException>(() =>
            operations.ExportEvidence(stored.EvidenceId, outputPath));

        Assert.False(File.Exists(outputPath));
        Assert.Equal(
            "audit.outcome_failed_after_publish",
            DetailCode(fixture.Sink.Lines[1]));
        using var failure = JsonDocument.Parse(fixture.Sink.Lines[1]);
        Assert.Equal(
            stored.ScriptDigest,
            failure.RootElement.GetProperty("request").GetProperty("original_script_digest").GetString());
        Assert.Equal(
            stored.ByteLength,
            failure.RootElement.GetProperty("outcome").GetProperty("bytes_returned").GetInt64());
    }

    [Fact]
    public void Export_outcome_persistence_failure_removes_the_exact_final_file()
    {
        var options = Options();
        var stored = new ScriptEvidenceStore(options).Store("Get-UncommittedExportSecret");
        var outputRoot = SecureAuditStorage.PrepareRoot(
            Path.Combine(_root, "operator-fault-output"));
        var outputPath = Path.Combine(outputRoot, "evidence.bin");
        using var fixture = Journal(
            options,
            (point, call) => point == AuditSinkFaultPoint.BeforeAppend && call == 2);
        var operations = new AuditAdminOperations(options, fixture.Journal);

        Assert.Throws<AuditAdminOperationException>(() =>
            operations.ExportEvidence(stored.EvidenceId, outputPath));

        Assert.False(File.Exists(outputPath));
        Assert.Equal(
            ["evidence.export_intent"],
            fixture.Sink.Lines.Select(EventType).ToArray());
    }

    [Fact]
    public async Task Export_final_path_appears_only_after_complete_staged_bytes_are_flushed()
    {
        var options = Options();
        var script = new string('s', 900);
        var stored = new ScriptEvidenceStore(options).Store(script);
        var outputRoot = SecureAuditStorage.PrepareRoot(
            Path.Combine(_root, "operator-staged-output"));
        var outputPath = Path.Combine(outputRoot, "evidence.bin");
        using var fixture = Journal(options);
        using var staged = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var operations = new AuditAdminOperations(
            options,
            fixture.Journal,
            beforeEvidenceExportPublishForTests: () =>
            {
                staged.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            });

        var export = Task.Run(() =>
            operations.ExportEvidence(stored.EvidenceId, outputPath));
        Assert.True(staged.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            Assert.False(File.Exists(outputPath));
            Assert.Single(Directory.GetFiles(
                outputRoot,
                ".ptk-evidence-export-*.tmp"));
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(stored, await export.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(script, File.ReadAllText(outputPath));
        Assert.Empty(Directory.GetFiles(
            outputRoot,
            ".ptk-evidence-export-*.tmp"));
    }

    private AuditOptions Options() => AuditOptions.Create(
        _root,
        maxEvidenceBytes: 1024,
        evidenceAggregateBytes: 16 * 1024,
        evidenceRetentionAge: TimeSpan.FromMinutes(10));

    private static JournalFixture Journal(
        AuditOptions options,
        Func<AuditSinkFaultPoint, int, bool>? faultInjector = null)
    {
        var sink = new InMemoryAuditJournalSink(
            options.SegmentBytes,
            options.AggregateBytes,
            options.ProtectionMode,
            options.RetentionAge,
            faultInjector);
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

    private static string DetailCode(byte[] line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("outcome").GetProperty("detail_code").GetString()!;
    }

    private static void AssertEffectiveAdminIdentity(JsonElement root)
    {
        var identity = root.GetProperty("session")
            .GetProperty("effective_identity").GetString();
        Assert.NotNull(identity);
        if (OperatingSystem.IsWindows())
            Assert.StartsWith("windows-sid:S-", identity, StringComparison.Ordinal);
        else
            Assert.Matches("^unix-euid:[0-9]+$", identity);
        Assert.Equal(
            "unknown",
            root.GetProperty("actor").GetProperty("attribution_strength").GetString());
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

    private sealed class FlushFailingStream : MemoryStream
    {
        internal byte[] Bytes => ToArray();

        public override void Flush() => throw new IOException("injected flush failure");
    }

    private sealed class PartialWriteFailingStream : MemoryStream
    {
        internal byte[] Bytes => ToArray();

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            base.Write(buffer[..Math.Max(1, buffer.Length / 2)]);
            throw new IOException("injected partial write failure");
        }
    }
}
