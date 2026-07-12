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

    [Fact]
    public void Valid_reference_is_marked_before_checkpoint_and_finalized_only_afterward()
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
            reference.ScriptDigest);

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

        var duplicate = Encoding.UTF8.GetBytes(
            "{\"schema_version\":\"ptk.audit/1\",\"event_id\":\"" +
            eventId.ToString("D") +
            "\",\"sequence\":1,\"producer\":{\"supervisor_boot_id\":\"" +
            BootId.ToString("D") +
            "\"},\"request\":{\"original_script_digest\":null," +
            "\"script_evidence_id\":null,\"script_evidence_id\":null}}\n");
        Assert.Throws<IOException>(() => observer.ObserveAcknowledgment(duplicate));
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
        string? digest)
    {
        static string JsonString(string? value) =>
            value is null ? "null" : "\"" + value + "\"";
        return Encoding.UTF8.GetBytes(
            "{\"schema_version\":\"ptk.audit/1\",\"event_id\":\"" +
            eventId.ToString("D") +
            "\",\"sequence\":1,\"producer\":{\"supervisor_boot_id\":\"" +
            BootId.ToString("D") +
            "\"},\"request\":{\"original_script_digest\":" +
            JsonString(digest) +
            ",\"script_evidence_id\":" +
            JsonString(evidenceId) +
            "}}\n");
    }
}
