using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditJournalTests : IDisposable
{
    private readonly List<string> _roots = [];
    private static readonly DateTimeOffset BaseTime =
        new(2026, 7, 11, 15, 0, 0, TimeSpan.Zero);

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
                // A failed assertion must remain the authoritative failure.
            }
        }
    }

    [Fact]
    public void Append_writes_and_flushes_each_authoritative_line_once_and_chains_them()
    {
        using var fixture = CreateMemoryJournal(segmentSlots: 4);
        Assert.True(fixture.Journal.TryReserve(2, out var lease, out var failure));
        Assert.Null(failure);

        var first = fixture.Journal.Append(lease!, Input("call.accepted"));
        var second = fixture.Journal.Append(lease!, Input("call.completed"));
        lease!.Release();

        Assert.Equal(2, fixture.Sink.AppendCallCount);
        Assert.Equal(2, fixture.Sink.FlushCallCount);
        Assert.Equal(2, fixture.Sink.Lines.Count);
        Assert.Equal(first.Utf8Line.ToArray(), fixture.Sink.Lines[0]);
        Assert.Equal(second.Utf8Line.ToArray(), fixture.Sink.Lines[1]);

        using var firstDocument = Parse(fixture.Sink.Lines[0]);
        using var secondDocument = Parse(fixture.Sink.Lines[1]);
        Assert.Equal(1, firstDocument.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal(2, secondDocument.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal(
            first.EventHash,
            secondDocument.RootElement.GetProperty("previous_event_hash").GetString());
        Assert.Equal(second.EventId, fixture.Journal.LastEventId);
        Assert.Equal(2, fixture.Journal.Sequence);
        Assert.False(fixture.Journal.IsPoisoned);
    }

    [Fact]
    public async Task Concurrent_appends_have_unique_sequences_and_an_unbroken_chain()
    {
        const int eventCount = 20;
        using var fixture = CreateMemoryJournal(segmentSlots: eventCount + 2);
        Assert.True(fixture.Journal.TryReserve(eventCount, out var lease, out _));

        var tasks = Enumerable.Range(0, eventCount)
            .Select(_ => Task.Run(() => fixture.Journal.Append(lease!, Input("call.completed"))))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        lease!.Release();

        Assert.Equal(eventCount, fixture.Sink.Lines.Count);
        Assert.Equal(eventCount, results.Select(result => result.EventId).Distinct().Count());
        string? previousHash = null;
        for (var index = 0; index < fixture.Sink.Lines.Count; index++)
        {
            using var document = Parse(fixture.Sink.Lines[index]);
            var root = document.RootElement;
            Assert.Equal(index + 1, root.GetProperty("sequence").GetInt64());
            if (previousHash is null)
                Assert.Equal(JsonValueKind.Null, root.GetProperty("previous_event_hash").ValueKind);
            else
                Assert.Equal(previousHash, root.GetProperty("previous_event_hash").GetString());
            previousHash = root.GetProperty("event_hash").GetString();
        }
    }

    [Theory]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend)]
    [InlineData((int)AuditSinkFaultPoint.AfterAppend)]
    [InlineData((int)AuditSinkFaultPoint.Flush)]
    public void Any_append_or_flush_fault_poison_admission_and_returns_only_a_sanitized_error(
        int faultPointValue)
    {
        var faultPoint = (AuditSinkFaultPoint)faultPointValue;
        using var fixture = CreateMemoryJournal(
            segmentSlots: 2,
            faultInjector: (point, call) => point == faultPoint && call == 1);
        Assert.True(fixture.Journal.TryReserve(1, out var lease, out _));

        var error = Assert.Throws<AuditUnavailableException>(() =>
            fixture.Journal.Append(lease!, Input("call.accepted")));

        Assert.Equal("Required audit persistence failed.", error.Message);
        Assert.Null(error.InnerException);
        Assert.True(fixture.Journal.IsPoisoned);
        Assert.Equal(AuditHealthState.Unavailable, fixture.Journal.Health.Snapshot().State);
        Assert.False(fixture.Journal.TryReserve(1, out var refused, out var failure));
        Assert.Null(refused);
        Assert.Equal("journal.poisoned", failure);
        Assert.Equal(1, fixture.Sink.AppendCallCount);
        Assert.InRange(fixture.Sink.FlushCallCount, 0, 1);
    }

    [Fact]
    public async Task Racing_reservations_cannot_overbook_one_max_record_slot()
    {
        // One ordinary slot plus the separately held recovery slot.
        using var fixture = CreateMemoryJournal(segmentSlots: 2);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<AuditReservation?> Compete()
        {
            await start.Task;
            fixture.Journal.TryReserve(1, out var reservation, out _);
            return reservation;
        }

        var first = Compete();
        var second = Compete();
        start.SetResult();
        var reservations = await Task.WhenAll(first, second);

        var winner = Assert.Single(reservations, value => value is not null);
        Assert.Equal(fixture.Options.MaxRecordBytes, fixture.Journal.ReservedBytes);
        winner!.Release();
        Assert.Equal(0, fixture.Journal.ReservedBytes);
        Assert.Equal(AuditHealthState.Unavailable, fixture.Journal.Health.Snapshot().State);
    }

    [Fact]
    public void Admission_probe_failure_preserves_existing_terminal_append_obligations()
    {
        var failReservation = false;
        using var fixture = CreateMemoryJournal(
            segmentSlots: 6,
            faultInjector: (point, _) =>
                failReservation && point == AuditSinkFaultPoint.BeforeReserve);
        Assert.True(fixture.Journal.TryReserve(2, out var existing, out _));
        fixture.Journal.Append(existing!, Input("call.accepted"));

        failReservation = true;
        Assert.False(fixture.Journal.TryReserve(1, out var refused, out var failure));
        Assert.Null(refused);
        Assert.Equal("journal.storage", failure);
        Assert.False(fixture.Journal.IsPoisoned);
        Assert.Equal(AuditHealthState.Unavailable, fixture.Journal.Health.Snapshot().State);

        fixture.Journal.Append(existing!, Input("call.completed"));
        existing!.Release();
        Assert.False(fixture.Journal.IsPoisoned);
        Assert.Equal(
            ["call.accepted", "audit.degraded", "call.completed"],
            fixture.Sink.Lines.Select(line =>
            {
                using var document = Parse(line);
                return document.RootElement.GetProperty("event_type").GetString();
            }));
    }

    [Fact]
    public void Successful_append_consumes_one_slot_and_releases_its_unused_maximum()
    {
        // Four ordinary slots plus the separately held recovery slot.
        using var fixture = CreateMemoryJournal(segmentSlots: 5);
        Assert.True(fixture.Journal.TryReserve(4, out var lease, out _));

        fixture.Journal.Append(lease!, Input("call.accepted"));
        fixture.Journal.Append(lease!, Input("call.completed"));
        fixture.Journal.Append(lease!, Input("call.not_started"));

        Assert.Equal(1, lease!.RemainingSlots);
        Assert.True(fixture.Journal.CurrentSegmentBytes < fixture.Options.MaxRecordBytes * 3L);
        Assert.True(fixture.Journal.TryReserve(1, out var reclaimed, out _));
        reclaimed!.Release();
        lease.Release();
    }

    [Fact]
    public void A_transferred_terminal_slot_remains_reserved_after_the_call_lease_closes()
    {
        using var fixture = CreateMemoryJournal(segmentSlots: 3);
        Assert.True(fixture.Journal.TryReserve(2, out var callLease, out _));
        var terminalLease = callLease!.TransferSlot();

        Assert.Equal(1, callLease.RemainingSlots);
        Assert.Equal(1, terminalLease.RemainingSlots);
        fixture.Journal.Append(callLease, Input("job.started"));
        callLease.Release();

        Assert.Equal(fixture.Options.MaxRecordBytes, fixture.Journal.ReservedBytes);
        fixture.Journal.Append(terminalLease, Input("job.completed"));
        terminalLease.Release();
        Assert.Equal(0, fixture.Journal.ReservedBytes);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Journal.Append(callLease, Input("call.completed")));
    }

    [Fact]
    public void Uuid_v7_helper_returns_canonical_version_7_call_ids()
    {
        using var fixture = CreateMemoryJournal(segmentSlots: 1);
        var first = fixture.Journal.CreateCallId();
        var second = fixture.Journal.CreateCallId();

        Assert.NotEqual(first, second);
        Assert.Equal('7', first.ToString("D")[14]);
        Assert.Contains(first.ToString("D")[19], new[] { '8', '9', 'a', 'b' });
    }

    private MemoryFixture CreateMemoryJournal(
        int segmentSlots,
        Func<AuditSinkFaultPoint, int, bool>? faultInjector = null)
    {
        var options = Options(NewRoot(), segmentSlots, aggregateSegments: 1);
        var health = new AuditHealth(options, () => BaseTime);
        var sink = new InMemoryAuditJournalSink(
            options.SegmentBytes,
            options.AggregateBytes,
            options.ProtectionMode,
            options.RetentionAge,
            faultInjector);
        var journal = new AuditJournal(
            options,
            health,
            sink,
            "test-version",
            binaryDigest: null,
            hostId: Guid.Parse("12345678-1234-4abc-8def-0123456789ab"),
            supervisorBootId: Guid.Parse("22345678-1234-4abc-8def-0123456789ab"),
            utcNow: () => BaseTime);
        return new MemoryFixture(options, sink, journal);
    }

    private AuditOptions Options(
        string root,
        int segmentSlots,
        int aggregateSegments,
        TimeSpan? retentionAge = null)
    {
        const int maximumRecordBytes = 4096;
        return AuditOptions.Create(
            root,
            maxRecordBytes: maximumRecordBytes,
            segmentBytes: maximumRecordBytes * (long)(segmentSlots + 1),
            aggregateBytes: maximumRecordBytes * (long)(segmentSlots + 1) * aggregateSegments,
            emergencyReserveBytes: maximumRecordBytes * 2L,
            retentionAge: retentionAge ?? TimeSpan.FromMinutes(10),
            maxEvidenceBytes: maximumRecordBytes,
            evidenceAggregateBytes: maximumRecordBytes,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptk-audit-journal-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private static AuditEventInput Input(string eventType) => new()
    {
        EventType = eventType,
        Session = new AuditSession(),
        Actor = new AuditActor { AttributionStrength = "transport_only", Transport = "mcp_stdio" },
        Correlation = new AuditCorrelation(),
        Request = new AuditRequest(),
        Routing = new AuditRouting(),
        Outcome = new AuditOutcome { TerminationCertainty = "not_applicable" },
        Coverage = new AuditCoverage
        {
            PtkRequest = true,
            RootProcessObserved = "not_applicable",
            DescendantsObserved = "not_applicable",
            RemoteEffectObserved = "not_applicable"
        },
        Audit = new AuditEventHealth
        {
            ProtectionMode = "local-only",
            HealthState = "healthy"
        }
    };

    private static JsonDocument Parse(byte[] line) => JsonDocument.Parse(line.AsMemory(0, line.Length - 1));

    private sealed record MemoryFixture(
        AuditOptions Options,
        InMemoryAuditJournalSink Sink,
        AuditJournal Journal) : IDisposable
    {
        public void Dispose() => Journal.Dispose();
    }
}
