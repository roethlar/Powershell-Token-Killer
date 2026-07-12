using System.Text;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditExportAcknowledgmentObserverTests : IDisposable
{
    private static readonly Guid BootId =
        Guid.Parse("12345678-1234-4abc-8def-0123456789ab");
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ptk-evidence-observer-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Valid_v1_and_v2_reference_is_marked_before_checkpoint_and_finalized_only_afterward(
        bool legacyV1)
    {
        var options = Options();
        using var checkpoint = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var store = new ScriptEvidenceStore(options);
        var reference = store.Store("exact secret-bearing script bytes");
        var eventId = Guid.CreateVersion7();
        var observer = new ScriptEvidenceAcknowledgmentObserver(
            new ScriptEvidenceStoreProvider(store));
        var line = Line(
            eventId,
            reference.EvidenceId,
            reference.ScriptDigest,
            legacyV1);

        using var anchor = observer.ObserveAcknowledgment(line);

        Assert.Single(Directory.GetFiles(
            options.EvidenceDirectory,
            "*.anchoring.*.script"));
        Assert.Empty(Directory.GetFiles(options.EvidenceDirectory, "*.anchored.script"));

        checkpoint.SaveForTests(new AuditExportCheckpoint(
            BootId,
            chainComplete: false,
            AuditSpoolSegmentIdentity.Create(BootId, 0),
            byteOffset: 128,
            sequence: 1,
            acknowledgedEventId: eventId,
            blockedRecord: null));
        anchor.CompleteAfterCheckpoint();

        Assert.Empty(Directory.GetFiles(
            options.EvidenceDirectory,
            "*.anchoring.*.script"));
        Assert.Single(Directory.GetFiles(options.EvidenceDirectory, "*.anchored.script"));
    }

    [Fact]
    public void Null_pair_is_a_noop_but_partial_or_duplicate_identity_fails_closed()
    {
        var options = Options();
        var observer = new ScriptEvidenceAcknowledgmentObserver(
            new ScriptEvidenceStoreProvider(new ScriptEvidenceStore(options)));
        var eventId = Guid.CreateVersion7();

        using (var none = observer.ObserveAcknowledgment(Line(eventId, null, null)))
            none.CompleteAfterCheckpoint();

        Assert.Throws<IOException>(() => observer.ObserveAcknowledgment(
            Line(eventId, Guid.NewGuid().ToString("D"), null)));

        var valid = Encoding.UTF8.GetString(Line(eventId, null, null));
        var duplicate = Encoding.UTF8.GetBytes(valid.Replace(
            "\"script_evidence_id\":null",
            "\"script_evidence_id\":null,\"script_evidence_id\":null",
            StringComparison.Ordinal));
        Assert.Throws<IOException>(() => observer.ObserveAcknowledgment(duplicate));
    }

    [Fact]
    public void Version_shape_hybrids_are_rejected_before_evidence_is_anchored()
    {
        var options = Options();
        var store = new ScriptEvidenceStore(options);
        var reference = store.Store("hybrid schema evidence");
        var observer = new ScriptEvidenceAcknowledgmentObserver(
            new ScriptEvidenceStoreProvider(store));
        var v2 = Line(Guid.CreateVersion7(), reference.EvidenceId, reference.ScriptDigest);
        var v1 = AuditCoreSchemaTestRecords.ToLegacyV1(v2);

        Assert.Throws<IOException>(() => observer.ObserveAcknowledgment(
            AuditCoreSchemaTestRecords.RelabelV2AsV1WithoutShrinking(v2)));
        Assert.Throws<IOException>(() => observer.ObserveAcknowledgment(
            AuditCoreSchemaTestRecords.RelabelV1AsV2WithoutExpanding(v1)));
        Assert.Single(Directory.GetFiles(options.EvidenceDirectory, "*.script"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AuditOptions Options() => AuditOptions.Create(
        _root,
        AuditProtectionMode.Anchored,
        new string('a', 64),
        maxRecordBytes: 4096,
        segmentBytes: 16_384,
        aggregateBytes: 65_536,
        emergencyReserveBytes: 8192,
        retentionAge: TimeSpan.FromMinutes(10),
        maxEvidenceBytes: 4096,
        evidenceAggregateBytes: 4096,
        evidenceRetentionAge: TimeSpan.FromMinutes(10));

    private static byte[] Line(
        Guid eventId,
        string? evidenceId,
        string? digest,
        bool legacyV1 = false)
    {
        var serialized = AuditEventSerializer.Serialize(
            1,
            previousEventHash: null,
            new AuditProducerContext(
                Guid.Parse("22345678-1234-4abc-8def-0123456789ab"),
                BootId,
                WorkerBootId: null,
                Environment.ProcessId,
                "acknowledgment-observer-test",
                BinaryDigest: null),
            new AuditEventInput
            {
                EventType = "execution.planned",
                Session = new AuditSession { DeclaredPurpose = "test" },
                Actor = new AuditActor { AttributionStrength = "unknown" },
                Correlation = new AuditCorrelation
                {
                    PlanId = Guid.Parse("32345678-1234-4abc-8def-0123456789ab"),
                },
                Request = new AuditRequest
                {
                    OriginalScriptDigest = digest,
                    ScriptEvidenceId = evidenceId is null ? null : Guid.Parse(evidenceId),
                },
                Routing = new AuditRouting(),
                Outcome = new AuditOutcome { TerminationCertainty = "not_applicable" },
                Coverage = new AuditCoverage
                {
                    PtkRequest = false,
                    RootProcessObserved = "not_applicable",
                    DescendantsObserved = "not_applicable",
                    RemoteEffectObserved = "not_applicable",
                },
                Audit = new AuditEventHealth
                {
                    ProtectionMode = "anchored",
                    ExportConfigurationIdentity = new string('a', 64),
                    HealthState = "healthy",
                },
            },
            eventId,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        return legacyV1
            ? AuditCoreSchemaTestRecords.ToLegacyV1(serialized.Utf8Line)
            : serialized.Utf8Line.ToArray();
    }
}
