using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpGuardian.Tests;

public sealed class AuditCallLifecycleTests
{
    [Fact]
    public void Guardian_safe_lifecycle_owns_admission_chain_and_terminal_reservation()
    {
        var options = AuditOptions.Create(
            Path.Combine(Path.GetTempPath(), "ptk-guardian-audit-" + Guid.NewGuid().ToString("N")),
            maxRecordBytes: AuditEventSerializer.MaximumLineBytes,
            segmentBytes: AuditEventSerializer.MaximumLineBytes * 16L,
            aggregateBytes: AuditEventSerializer.MaximumLineBytes * 16L,
            emergencyReserveBytes: AuditEventSerializer.MaximumLineBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: ScriptEvidenceStore.MaximumScriptBytes,
            evidenceAggregateBytes: ScriptEvidenceStore.MaximumScriptBytes * 2L,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
        var health = new AuditHealth(options);
        var sink = new InMemoryAuditJournalSink(
            options.SegmentBytes,
            options.AggregateBytes,
            options.ProtectionMode,
            options.RetentionAge);
        using var journal = new AuditJournal(
            options,
            health,
            sink,
            "guardian-audit-test",
            binaryDigest: null,
            hostId: Guid.Parse("12345678-1234-4abc-8def-0123456789ab"),
            supervisorBootId: Guid.Parse("22345678-1234-4abc-8def-0123456789ab"));
        var call = new AuditCallLifecycle(
            journal,
            new ScriptEvidenceStore(options.EvidenceDirectory));
        var metadata = new AuditCallMetadata(
            new AuditActor
            {
                Transport = "mcp_stdio",
                ClientName = "guardian-test",
                ClientVersion = "1",
                ClientSessionId = "session",
                AttributionStrength = "client_asserted",
            },
            new AuditRequest
            {
                Tool = "ptk_state",
                Action = "state",
                SessionRequested = "default",
                ProvidedFields = [],
                ListAvailable = false,
            },
            new AuditOperationProfile(
                MaximumCallRecordSlots: 5,
                PersistentJobTerminalSlots: 0,
                RequiresScriptEvidence: false,
                MayHaveSideEffects: true));

        Assert.True(call.TryBegin(metadata, exactSubmittedScript: null, out var failure), failure);
        Assert.True(call.Accepted);
        Assert.Equal(4L * options.MaxRecordBytes, journal.ReservedBytes);

        ((IAuditBoundaryCall)call).CompleteFromFilter("completed", bytesReturned: 17);

        Assert.True(call.TerminalWritten);
        Assert.Equal(0, journal.ReservedBytes);
        var events = sink.Lines.Select(Parse).ToArray();
        Assert.Equal(["call.accepted", "call.completed"], events.Select(EventType));
        var callId = events[0].GetProperty("correlation").GetProperty("call_id").GetGuid();
        Assert.Equal(callId, events[1].GetProperty("correlation").GetProperty("call_id").GetGuid());
        Assert.Equal(
            events[0].GetProperty("event_id").GetGuid(),
            events[1].GetProperty("correlation").GetProperty("parent_event_id").GetGuid());
        Assert.Equal(
            17,
            events[1].GetProperty("outcome").GetProperty("bytes_returned").GetInt64());
    }

    private static JsonElement Parse(byte[] line)
    {
        using var document = JsonDocument.Parse(line.AsMemory(0, line.Length - 1));
        return document.RootElement.Clone();
    }

    private static string EventType(JsonElement value) =>
        value.GetProperty("event_type").GetString()!;
}
