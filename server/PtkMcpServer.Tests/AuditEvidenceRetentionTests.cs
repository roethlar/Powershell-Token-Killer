using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditEvidenceRetentionTests : IDisposable
{
    private readonly string _parent = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ptk-audited-evidence-retention-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Age_retention_flushes_exact_intent_before_unlink_and_then_completed()
    {
        var options = Options("age", evidenceAggregateBytes: 512);
        using var fixture = JournalFixture.Create(options);
        string? retainedPath = null;
        var sawDurableIntent = false;
        var store = new ScriptEvidenceStore(
            options,
            retentionFaultInjector: point =>
            {
                if (point != AuditEvidenceRetentionFaultPoint.BeforeDelete) return;
                Assert.NotNull(retainedPath);
                Assert.True(File.Exists(retainedPath));
                var intent = Assert.Single(Events(fixture.Sink));
                Assert.Equal("evidence.retention_intent", EventType(intent));
                sawDurableIntent = true;
            });
        var reference = store.Store("expired evidence");
        retainedPath = EvidencePath(options, reference);
        File.SetLastWriteTimeUtc(retainedPath, DateTime.UtcNow.AddMinutes(-2));

        store.RetainEligible(fixture.Journal);

        Assert.True(sawDurableIntent);
        Assert.False(File.Exists(retainedPath));
        var events = Events(fixture.Sink);
        Assert.Equal(2, events.Length);
        AssertRetentionTuple(
            events[0],
            reference,
            "local_committed",
            "age_expired");
        AssertRetentionTuple(
            events[1],
            reference,
            "local_committed",
            "age_expired");
        Assert.Equal("evidence.retention_completed", EventType(events[1]));
        Assert.Equal("completed", OutcomeState(events[1]));
        Assert.Equal(
            events[0].GetProperty("event_id").GetString(),
            events[1].GetProperty("correlation").GetProperty("parent_event_id").GetString());

        var observer = new ScriptEvidenceAcknowledgmentObserver(
            new ScriptEvidenceStoreProvider(store));
        foreach (var line in fixture.Sink.Lines)
        {
            using var ignored = observer.ObserveAcknowledgment(line);
            ignored.CompleteAfterCheckpoint();
        }
    }

    [Fact]
    public void File_journal_exposes_committed_intent_before_exact_unlink()
    {
        var options = Options("file-durability", evidenceAggregateBytes: 512);
        var bootId = Guid.NewGuid();
        var sink = new FileAuditJournalSink(options, bootId);
        var identity = sink.CurrentSegmentIdentity;
        using var journal = new AuditJournal(
            options,
            new AuditHealth(options),
            sink,
            "evidence-retention-file-test",
            binaryDigest: null,
            Guid.NewGuid(),
            bootId);
        string? retainedPath = null;
        var store = new ScriptEvidenceStore(
            options,
            retentionFaultInjector: point =>
            {
                if (point != AuditEvidenceRetentionFaultPoint.BeforeDelete) return;
                Assert.NotNull(retainedPath);
                Assert.True(File.Exists(retainedPath));
                var committed = journal.ReadCommittedSpool(
                    identity,
                    offset: 0,
                    options.MaxRecordBytes);
                Assert.Equal(AuditCommittedSpoolReadStatus.Data, committed.Status);
                using var document = JsonDocument.Parse(committed.Bytes[..^1]);
                Assert.Equal(
                    "evidence.retention_intent",
                    document.RootElement.GetProperty("event_type").GetString());
                Assert.Equal(committed.Bytes.Length, committed.CommittedTail);
            });
        var reference = store.Store("file-backed expired evidence");
        retainedPath = EvidencePath(options, reference);
        File.SetLastWriteTimeUtc(retainedPath, DateTime.UtcNow.AddMinutes(-2));

        store.RetainEligible(journal);

        Assert.False(File.Exists(retainedPath));
        Assert.Equal(2, journal.Sequence);
    }

    [Theory]
    [InlineData((int)AuditEvidenceRetentionFaultPoint.BeforeDelete, true, "failed")]
    [InlineData((int)AuditEvidenceRetentionFaultPoint.AfterDelete, false, "outcome_unknown")]
    public void Retention_failure_records_truthful_exact_outcome(
        int faultPointValue,
        bool artifactRemains,
        string expectedState)
    {
        var faultPoint = (AuditEvidenceRetentionFaultPoint)faultPointValue;
        var options = Options("failure-" + faultPoint, evidenceAggregateBytes: 512);
        using var fixture = JournalFixture.Create(options);
        var store = new ScriptEvidenceStore(
            options,
            retentionFaultInjector: point =>
            {
                if (point == faultPoint) throw new IOException("injected retention failure");
            });
        var reference = store.Store("failed retention evidence");
        var path = EvidencePath(options, reference);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2));

        Assert.Throws<ScriptEvidenceStorageException>(() =>
            store.RetainEligible(fixture.Journal));

        Assert.Equal(artifactRemains, File.Exists(path));
        var events = Events(fixture.Sink);
        Assert.Equal(2, events.Length);
        Assert.Equal("evidence.retention_intent", EventType(events[0]));
        Assert.Equal("evidence.retention_failed", EventType(events[1]));
        Assert.Equal(expectedState, OutcomeState(events[1]));
        AssertRetentionTuple(
            events[1],
            reference,
            "local_committed",
            "age_expired");
    }

    [Fact]
    public void Multi_artifact_retention_records_completed_prefix_and_truthful_failed_item()
    {
        var options = Options("partial-failure", evidenceAggregateBytes: 768);
        using var fixture = JournalFixture.Create(options);
        var beforeDeleteCount = 0;
        var store = new ScriptEvidenceStore(
            options,
            retentionFaultInjector: point =>
            {
                if (point == AuditEvidenceRetentionFaultPoint.BeforeDelete &&
                    ++beforeDeleteCount == 2)
                {
                    throw new IOException("injected second retention failure");
                }
            });
        var first = store.Store("first expired evidence");
        var second = store.Store("second expired evidence");
        var firstPath = EvidencePath(options, first);
        var secondPath = EvidencePath(options, second);
        File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow.AddMinutes(-3));
        File.SetLastWriteTimeUtc(secondPath, DateTime.UtcNow.AddMinutes(-2));

        Assert.Throws<ScriptEvidenceStorageException>(() =>
            store.RetainEligible(fixture.Journal));

        Assert.False(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));
        var events = Events(fixture.Sink);
        Assert.Equal(
            [
                "evidence.retention_intent",
                "evidence.retention_completed",
                "evidence.retention_intent",
                "evidence.retention_failed",
            ],
            events.Select(EventType));
        AssertRetentionTuple(events[1], first, "local_committed", "age_expired");
        AssertRetentionTuple(events[3], second, "local_committed", "age_expired");
        Assert.Equal("failed", OutcomeState(events[3]));
    }

    [Fact]
    public void No_journal_never_deletes_for_capacity_but_journal_bound_publish_does()
    {
        var options = Options("capacity", evidenceAggregateBytes: 256);
        var store = new ScriptEvidenceStore(options);
        var first = store.Store("first capacity evidence");
        var firstPath = EvidencePath(options, first);

        Assert.Throws<ScriptEvidenceStorageException>(() =>
            store.Store("unaudited replacement"));
        Assert.True(File.Exists(firstPath));

        using var fixture = JournalFixture.Create(options);
        var replacement = store.Store("audited replacement", fixture.Journal);

        Assert.False(File.Exists(firstPath));
        Assert.True(File.Exists(EvidencePath(options, replacement)));
        var events = Events(fixture.Sink);
        Assert.Equal(2, events.Length);
        AssertRetentionTuple(
            events[0],
            first,
            "local_committed",
            "capacity_pressure");
    }

    [Fact]
    public void Call_context_routes_capacity_retention_through_its_audit_journal()
    {
        var options = Options("call-publication", evidenceAggregateBytes: 256);
        using var fixture = JournalFixture.Create(options);
        var store = new ScriptEvidenceStore(options);
        var retained = store.Store("retained before audited call");
        var retainedPath = EvidencePath(options, retained);
        var context = new AuditCallContext(
            fixture.Journal,
            new ScriptEvidenceStoreProvider(store));
        var metadata = CaptureInvokeMetadata();

        Assert.True(context.TryBegin(metadata, "replacement call script", out var failure), failure);
        context.CompleteCall("completed", "ok");

        Assert.False(File.Exists(retainedPath));
        var events = Events(fixture.Sink);
        Assert.Equal(
            [
                "evidence.retention_intent",
                "evidence.retention_completed",
                "call.accepted",
                "call.completed",
            ],
            events.Select(EventType));
        AssertRetentionTuple(events[0], retained, "local_committed", "capacity_pressure");
    }

    [Fact]
    public void Missing_two_slot_reservation_deletes_nothing_and_emits_nothing()
    {
        var options = Options(
            "reservation",
            evidenceAggregateBytes: 512,
            segmentBytes: 4096 * 3L);
        using var fixture = JournalFixture.Create(options);
        var store = new ScriptEvidenceStore(options);
        var reference = store.Store("reservation pinned evidence");
        var path = EvidencePath(options, reference);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2));

        store.RetainEligible(fixture.Journal);

        Assert.True(File.Exists(path));
        Assert.DoesNotContain(
            Events(fixture.Sink),
            value => EventType(value)?.StartsWith(
                "evidence.retention_",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Constructor_probe_and_prewriter_reconciliation_never_delete()
    {
        var options = Options("prewriter", evidenceAggregateBytes: 512);
        var first = new ScriptEvidenceStore(options);
        var reference = first.Store("prewriter retained evidence");
        var path = EvidencePath(options, reference);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2));

        var reopened = new ScriptEvidenceStore(options);
        reopened.Probe();
        var provider = new ScriptEvidenceStoreProvider(options);
        Assert.True(provider.ReconcileExistingAwaitingBeforeWriter());

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Periodic_reconciliation_routes_retention_through_the_live_journal()
    {
        var options = Options("periodic", evidenceAggregateBytes: 512);
        using var fixture = JournalFixture.Create(options);
        var store = new ScriptEvidenceStore(options);
        var reference = store.Store("periodically retained evidence");
        var path = EvidencePath(options, reference);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2));
        var provider = new ScriptEvidenceStoreProvider(store);

        Assert.True(provider.ReconcileExistingAwaiting(fixture.Journal));

        Assert.False(File.Exists(path));
        var events = Events(fixture.Sink);
        Assert.Equal(2, events.Length);
        AssertRetentionTuple(events[0], reference, "local_committed", "age_expired");
    }

    [Fact]
    public void Crash_left_temporary_is_retained_before_writer_then_audited()
    {
        var options = Options("temporary", evidenceAggregateBytes: 512);
        var evidenceRoot = SecureAuditStorage.PrepareRoot(options.EvidenceDirectory);
        var temporaryId = Guid.NewGuid();
        var temporaryPath = Path.Combine(evidenceRoot, $".{temporaryId:D}.tmp");
        var bytes = Encoding.UTF8.GetBytes("crash-left temporary evidence");
        using (var stream = SecureAuditStorage.CreateExclusiveFile(temporaryPath))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        var store = new ScriptEvidenceStore(options);
        store.Probe();
        Assert.True(File.Exists(temporaryPath));
        using var fixture = JournalFixture.Create(options);

        store.RetainEligible(fixture.Journal);

        Assert.False(File.Exists(temporaryPath));
        var intent = Events(fixture.Sink)[0];
        var request = intent.GetProperty("request");
        Assert.Equal(temporaryId.ToString("D"), request.GetProperty("evidence_subject_id").GetString());
        Assert.Equal(bytes.Length, request.GetProperty("evidence_subject_bytes").GetInt64());
        Assert.Equal("temporary", request.GetProperty("evidence_subject_state").GetString());
        Assert.Equal("crash_temporary", request.GetProperty("retention_reason").GetString());
    }

    [Fact]
    public async Task Runtime_startup_audits_retention_after_server_started()
    {
        var options = Options("runtime-startup", evidenceAggregateBytes: 512);
        var seed = new ScriptEvidenceStore(options);
        var reference = seed.Store("runtime startup retained evidence");
        var path = EvidencePath(options, reference);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2));
        var health = new AuditHealth(options);
        using var runtime = new AuditRuntimeGate(
            options,
            health,
            new ScriptEvidenceStoreProvider(options),
            "evidence-retention-runtime-test");

        await runtime.StartAsync(CancellationToken.None);

        Assert.False(File.Exists(path));
        await runtime.StopAsync(CancellationToken.None);
        runtime.Dispose();
        var events = SpoolEvents(options);
        Assert.Equal(
            [
                "server.started",
                "evidence.retention_intent",
                "evidence.retention_completed",
                "server.stopped",
            ],
            events.Select(EventType));
        AssertRetentionTuple(events[1], reference, "local_committed", "age_expired");
    }

    [Fact]
    public async Task Anchored_runtime_startup_audits_eligible_retention()
    {
        var options = Options(
            "anchored-runtime-startup",
            evidenceAggregateBytes: 512,
            protectionMode: AuditProtectionMode.Anchored);
        var reference = SeedExpiredAnchoredArtifact(options, "anchored runtime evidence");
        var path = EvidencePath(options, reference);
        var health = new AuditHealth(options);
        var evidence = new ScriptEvidenceStoreProvider(options);
        var transport = new AcknowledgingTransport(options.ExportConfigurationIdentity!);
        var runtime = new AuditRuntimeGate(
            options,
            health,
            evidence,
            "anchored-evidence-retention-runtime-test",
            openRuntime: () => AuditRuntimeResources.OpenAnchored(
                options,
                health,
                "anchored-evidence-retention-runtime-test",
                transport,
                evidence));

        await runtime.StartAsync(CancellationToken.None);

        Assert.False(File.Exists(path));
        await runtime.StopAsync(CancellationToken.None);
        runtime.Dispose();
    }

    [Fact]
    public void Admin_startup_audits_retention_before_returning_the_session()
    {
        var options = Options("admin-startup", evidenceAggregateBytes: 512);
        var seed = new ScriptEvidenceStore(options);
        var reference = seed.Store("admin startup retained evidence");
        var path = EvidencePath(options, reference);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2));

        using (AuditAdminJournalSession.Open(
                   options,
                   "evidence-retention-admin-test"))
        {
            Assert.False(File.Exists(path));
        }

        var events = SpoolEvents(options);
        Assert.Equal(
            ["evidence.retention_intent", "evidence.retention_completed"],
            events.Select(EventType));
        AssertRetentionTuple(events[0], reference, "local_committed", "age_expired");
    }

    [Fact]
    public void Anchored_admin_startup_audits_eligible_retention()
    {
        var options = Options(
            "anchored-admin-startup",
            evidenceAggregateBytes: 512,
            protectionMode: AuditProtectionMode.Anchored);
        var reference = SeedExpiredAnchoredArtifact(options, "anchored admin evidence");
        var path = EvidencePath(options, reference);

        using (AuditAdminJournalSession.Open(
                   options,
                   "anchored-evidence-retention-admin-test"))
        {
            Assert.False(File.Exists(path));
        }

        var events = SpoolEvents(options);
        Assert.Equal(
            ["evidence.retention_intent", "evidence.retention_completed"],
            events.Select(EventType));
        AssertRetentionTuple(events[0], reference, "anchored", "age_expired");
    }

    private AuditOptions Options(
        string name,
        long evidenceAggregateBytes,
        long? segmentBytes = null,
        AuditProtectionMode protectionMode = AuditProtectionMode.LocalOnly) =>
        AuditOptions.Create(
            Path.Combine(_parent, name),
            protectionMode,
            protectionMode == AuditProtectionMode.Anchored ? new string('9', 64) : null,
            maxRecordBytes: 4096,
            segmentBytes: segmentBytes ?? 65_536,
            aggregateBytes: 262_144,
            emergencyReserveBytes: 8192,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: 256,
            evidenceAggregateBytes: evidenceAggregateBytes,
            evidenceRetentionAge: TimeSpan.FromMinutes(1));

    private static ScriptEvidenceReference SeedExpiredAnchoredArtifact(
        AuditOptions options,
        string script)
    {
        var store = new ScriptEvidenceStore(options);
        var reference = store.Store(script);
        var awaiting = EvidencePath(options, reference);
        var anchored = Path.Combine(
            options.EvidenceDirectory,
            $"{reference.EvidenceId}.{reference.ScriptDigest}.anchored.script");
        File.Move(awaiting, anchored);
        File.SetLastWriteTimeUtc(anchored, DateTime.UtcNow.AddMinutes(-2));
        return reference;
    }

    private static string EvidencePath(
        AuditOptions options,
        ScriptEvidenceReference reference) =>
        Directory.GetFiles(options.EvidenceDirectory, $"{reference.EvidenceId}.{reference.ScriptDigest}*.script")
            .Single();

    private static JsonElement[] Events(InMemoryAuditJournalSink sink) =>
        sink.Lines.Select(line =>
        {
            using var document = JsonDocument.Parse(line.AsMemory(0, line.Length - 1));
            return document.RootElement.Clone();
        }).ToArray();

    private static JsonElement[] SpoolEvents(AuditOptions options) =>
        Directory.EnumerateFiles(options.SpoolDirectory, "*.jsonl")
            .Order(StringComparer.Ordinal)
            .SelectMany(File.ReadLines)
            .Where(line => line.Length > 0)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToArray();

    private static AuditCallMetadata CaptureInvokeMetadata()
    {
        Assert.True(AuditCallMetadataCapture.TryCapture(
            new CallToolRequestParams
            {
                Name = "ptk_invoke",
                Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["script"] = JsonSerializer.SerializeToElement("placeholder"),
                },
            },
            new AuditClientContext("retention-test", "1", "session"),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            DateTimeOffset.UtcNow,
            out var metadata,
            out _,
            out var failure),
            failure);
        return metadata!;
    }

    private static string? EventType(JsonElement value) =>
        value.GetProperty("event_type").GetString();

    private static string? OutcomeState(JsonElement value) =>
        value.GetProperty("outcome").GetProperty("state").GetString();

    private static void AssertRetentionTuple(
        JsonElement value,
        ScriptEvidenceReference reference,
        string state,
        string reason)
    {
        var request = value.GetProperty("request");
        Assert.Equal(reference.EvidenceId, request.GetProperty("evidence_subject_id").GetString());
        Assert.Equal(reference.ScriptDigest, request.GetProperty("evidence_subject_digest").GetString());
        Assert.Equal(reference.ByteLength, request.GetProperty("evidence_subject_bytes").GetInt64());
        Assert.Equal(state, request.GetProperty("evidence_subject_state").GetString());
        Assert.Equal(reason, request.GetProperty("retention_reason").GetString());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("script_evidence_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, request.GetProperty("original_script_digest").ValueKind);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_parent)) return;
        try { Directory.Delete(_parent, recursive: true); }
        catch { /* Preserve the assertion that prevented cleanup. */ }
    }

    private sealed record JournalFixture(
        AuditJournal Journal,
        InMemoryAuditJournalSink Sink) : IDisposable
    {
        internal static JournalFixture Create(AuditOptions options)
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
                "evidence-retention-test",
                binaryDigest: null,
                Guid.NewGuid(),
                Guid.NewGuid());
            return new JournalFixture(journal, sink);
        }

        public void Dispose() => Journal.Dispose();
    }

    private sealed class AcknowledgingTransport(string configurationIdentity)
        : IAuditOtlpExportTransport
    {
        public string ConfigurationIdentity { get; } = configurationIdentity;

        public Task<AuditExportAttemptResult> ExportAsync(
            AuditOtlpRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AuditExportAttemptResult.Acknowledged(
                new string('0', 64),
                warning: false));
        }
    }
}
