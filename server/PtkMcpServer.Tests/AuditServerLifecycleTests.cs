using System.Text.Json;
using Microsoft.Extensions.Hosting;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditServerLifecycleTests : IDisposable
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 7, 11, 18, 0, 0, TimeSpan.Zero);
    private readonly List<string> _roots = [];

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
                // Preserve the test failure that prevented ordinary cleanup.
            }
        }
    }

    [Fact]
    public void Downstream_start_runs_only_after_server_started_is_durably_flushed()
    {
        using var fixture = CreateFixture(segmentSlots: 3);
        var downstreamCalls = 0;

        fixture.Lifecycle.RunAfterStarted(() =>
        {
            downstreamCalls++;
            Assert.Single(fixture.Sink.Lines);
            Assert.Equal(1, fixture.Sink.FlushCallCount);
        });

        Assert.Equal(1, downstreamCalls);
        Assert.True(fixture.Lifecycle.IsStarted);
        Assert.Equal(fixture.Options.MaxRecordBytes, fixture.Journal.ReservedBytes);
        Assert.False(fixture.Journal.TryReserve(1, out _, out var failure));
        Assert.Equal("journal.capacity", failure);

        fixture.Lifecycle.Stop();

        Assert.False(fixture.Lifecycle.IsStarted);
        Assert.Equal(0, fixture.Journal.ReservedBytes);
        Assert.Equal(3, fixture.Sink.FlushCallCount);
        Assert.Equal(3, fixture.Sink.Lines.Count);

        using var started = Parse(fixture.Sink.Lines[0]);
        using var degraded = Parse(fixture.Sink.Lines[1]);
        using var stopped = Parse(fixture.Sink.Lines[2]);
        AssertLifecycleEnvelope(started.RootElement, "server.started", "started");
        Assert.Equal("audit.degraded", degraded.RootElement.GetProperty("event_type").GetString());
        AssertLifecycleEnvelope(
            stopped.RootElement,
            "server.stopped",
            "stopped",
            healthState: "degraded");
        Assert.Equal(
            started.RootElement.GetProperty("event_id").GetString(),
            stopped.RootElement
                .GetProperty("correlation")
                .GetProperty("parent_event_id")
                .GetString());
        Assert.Equal(
            degraded.RootElement.GetProperty("event_hash").GetString(),
            stopped.RootElement.GetProperty("previous_event_hash").GetString());
    }

    [Theory]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend)]
    [InlineData((int)AuditSinkFaultPoint.AfterAppend)]
    [InlineData((int)AuditSinkFaultPoint.Flush)]
    public void Start_persistence_failure_never_invokes_downstream_start(int faultPointValue)
    {
        var faultPoint = (AuditSinkFaultPoint)faultPointValue;
        using var fixture = CreateFixture(
            segmentSlots: 3,
            faultInjector: (point, call) => point == faultPoint && call == 1);
        var downstreamCalls = 0;

        var error = Assert.Throws<AuditUnavailableException>(() =>
            fixture.Lifecycle.RunAfterStarted(() => downstreamCalls++));

        Assert.Equal("Required audit persistence failed.", error.Message);
        Assert.Equal(0, downstreamCalls);
        Assert.False(fixture.Lifecycle.IsStarted);
        Assert.Equal(AuditHealthState.Unavailable, fixture.Journal.Health.Snapshot().State);
        Assert.Equal(0, fixture.Journal.ReservedBytes);
    }

    [Fact]
    public void Missing_two_slot_obligation_fails_start_before_downstream_start()
    {
        using var fixture = CreateFixture(segmentSlots: 1);
        var downstreamCalls = 0;

        Assert.Throws<AuditUnavailableException>(() =>
            fixture.Lifecycle.RunAfterStarted(() => downstreamCalls++));

        Assert.Equal(0, downstreamCalls);
        Assert.Single(fixture.Sink.Lines);
        Assert.Equal(1, fixture.Sink.FlushCallCount);
        using var degraded = Parse(fixture.Sink.Lines[0]);
        Assert.Equal("audit.degraded", degraded.RootElement.GetProperty("event_type").GetString());
        Assert.Equal(AuditHealthState.Unavailable, fixture.Journal.Health.Snapshot().State);
        Assert.Equal(0, fixture.Journal.ReservedBytes);
    }

    [Fact]
    public async Task Hosted_start_and_stop_are_idempotent_and_stop_ignores_host_cancellation()
    {
        using var fixture = CreateFixture(segmentSlots: 3);
        IHostedService hosted = fixture.Lifecycle;

        await hosted.StartAsync(CancellationToken.None);
        await hosted.StartAsync(new CancellationToken(canceled: true));
        await hosted.StopAsync(new CancellationToken(canceled: true));
        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(2, fixture.Sink.Lines.Count);
        Assert.Equal(2, fixture.Sink.FlushCallCount);
        Assert.Equal(0, fixture.Journal.ReservedBytes);
    }

    private Fixture CreateFixture(
        int segmentSlots,
        Func<AuditSinkFaultPoint, int, bool>? faultInjector = null)
    {
        var options = Options(NewRoot(), segmentSlots);
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
        return new Fixture(options, sink, journal, new AuditServerLifecycle(journal, options));
    }

    private AuditOptions Options(string root, int segmentSlots)
    {
        const int maxRecordBytes = 4096;
        return AuditOptions.Create(
            root,
            maxRecordBytes: maxRecordBytes,
            segmentBytes: maxRecordBytes * (long)(segmentSlots + 1),
            aggregateBytes: maxRecordBytes * (long)(segmentSlots + 1),
            emergencyReserveBytes: maxRecordBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: maxRecordBytes,
            evidenceAggregateBytes: maxRecordBytes,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptk-audit-lifecycle-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private static void AssertLifecycleEnvelope(
        JsonElement root,
        string eventType,
        string outcomeState,
        string healthState = "healthy")
    {
        Assert.Equal("ptk.audit/1", root.GetProperty("schema_version").GetString());
        Assert.Equal(eventType, root.GetProperty("event_type").GetString());
        Assert.Equal(outcomeState, root.GetProperty("outcome").GetProperty("state").GetString());
        Assert.False(root.GetProperty("coverage").GetProperty("ptk_request").GetBoolean());
        Assert.Equal(
            "not_applicable",
            root.GetProperty("coverage").GetProperty("root_process_observed").GetString());
        Assert.Equal("unknown", root.GetProperty("actor").GetProperty("attribution_strength").GetString());
        Assert.Equal(healthState, root.GetProperty("audit").GetProperty("health_state").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("request").GetProperty("tool").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("routing").GetProperty("permitted_fallbacks").ValueKind);
        Assert.Equal(64, root.GetProperty("event_hash").GetString()!.Length);
    }

    private static JsonDocument Parse(byte[] line) =>
        JsonDocument.Parse(line.AsMemory(0, line.Length - 1));

    private sealed record Fixture(
        AuditOptions Options,
        InMemoryAuditJournalSink Sink,
        AuditJournal Journal,
        AuditServerLifecycle Lifecycle) : IDisposable
    {
        public void Dispose()
        {
            Lifecycle.Dispose();
            Journal.Dispose();
        }
    }
}
