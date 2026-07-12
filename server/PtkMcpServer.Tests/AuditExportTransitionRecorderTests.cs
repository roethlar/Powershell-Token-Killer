using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditExportTransitionRecorderTests
{
    private static readonly Guid BootId =
        Guid.Parse("12345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid OtherBootId =
        Guid.Parse("22345678-1234-4abc-8def-0123456789ab");

    [Fact]
    public void Recorder_emits_only_bounded_meaningful_transitions_and_stops_permanently()
    {
        using var fixture = new JournalFixture();
        var recorder = new AuditExportTransitionRecorder(
            fixture.Journal,
            fixture.Health.ExportObserver,
            backlog: null);
        var eventA = Guid.CreateVersion7();
        var eventB = Guid.CreateVersion7();

        recorder.Observe(Observation(AuditExportLoopState.Created));
        Assert.Empty(fixture.Events());

        recorder.Observe(Observation(AuditExportLoopState.Running));
        recorder.Observe(Observation(AuditExportLoopState.Running));
        recorder.Observe(Observation(
            AuditExportLoopState.WaitingToRetry,
            Step(AuditExportCoordinatorStepKind.Retry, eventA)));
        recorder.Observe(Observation(
            AuditExportLoopState.WaitingToRetry,
            Step(AuditExportCoordinatorStepKind.Retry, eventA)));
        recorder.Observe(Observation(
            AuditExportLoopState.Running,
            Step(AuditExportCoordinatorStepKind.Progressed, eventA)));
        recorder.Observe(Observation(
            AuditExportLoopState.Running,
            Step(AuditExportCoordinatorStepKind.Advanced, eventA)));

        var warning = Step(
            AuditExportCoordinatorStepKind.Advanced,
            eventA,
            hasHealthWarning: true);
        recorder.Observe(Observation(AuditExportLoopState.Running, warning));
        recorder.Observe(Observation(AuditExportLoopState.Running, warning));
        recorder.Observe(Observation(
            AuditExportLoopState.Running,
            Step(AuditExportCoordinatorStepKind.Advanced, eventA)));
        recorder.Observe(Observation(AuditExportLoopState.Running, warning));

        var blockedA = Step(
            AuditExportCoordinatorStepKind.Blocked,
            eventB,
            AuditExportFailureClass.Configuration);
        recorder.Observe(Observation(AuditExportLoopState.Running, blockedA));
        recorder.Observe(Observation(AuditExportLoopState.WaitingForWork, blockedA));
        recorder.Observe(Observation(
            AuditExportLoopState.Running,
            blockedA with { SupervisorBootId = OtherBootId }));
        recorder.Observe(Observation(
            AuditExportLoopState.Running,
            Step(AuditExportCoordinatorStepKind.Progressed, eventB)));

        recorder.Observe(Observation(AuditExportLoopState.Stopped));
        recorder.Observe(Observation(
            AuditExportLoopState.WaitingToRetry,
            Step(AuditExportCoordinatorStepKind.Retry, eventB)));
        recorder.Observe(Observation(AuditExportLoopState.Faulted));

        var events = fixture.Events();
        Assert.Equal(
            [
                "export.started",
                "audit.export_stalled",
                "audit.export_recovered",
                "export.warning",
                "export.warning",
                "audit.export_stalled",
                "audit.export_stalled",
                "audit.export_recovered",
            ],
            events.Select(EventType));
        Assert.Equal(8, fixture.Sink.AppendCallCount);

        var blocks = events
            .Where(value =>
                EventType(value) == "audit.export_stalled" &&
                value.RootElement.GetProperty("outcome").GetProperty("state").GetString() == "blocked")
            .ToArray();
        Assert.Equal(eventB.ToString("D"), ParentEventId(blocks[0]));
        Assert.Equal("orphan:12345678-1234-4abc-8def-0123456789ab", Target(blocks[0]));
        Assert.Equal("orphan:22345678-1234-4abc-8def-0123456789ab", Target(blocks[1]));
        Assert.All(
            events,
            value => Assert.DoesNotContain(
                "secret",
                value.RootElement.GetRawText(),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Export_loop_does_not_swallow_transition_persistence_failure()
    {
        using var fixture = new JournalFixture(
            (point, call) => point == AuditSinkFaultPoint.Flush && call == 1);
        var recorder = new AuditExportTransitionRecorder(
            fixture.Journal,
            fixture.Health.ExportObserver,
            backlog: null);
        await using var loop = new AuditExportLoop(
            new CompleteSource(),
            healthObserver: recorder);

        var failure = Record.Exception(() =>
        {
            _ = loop.Start();
        });
        Assert.IsType<AuditExportTransitionPersistenceException>(failure);

        Assert.True(fixture.Journal.IsPoisoned);
        Assert.Equal(AuditHealthState.Unavailable, fixture.Health.Snapshot().State);
        Assert.Equal("journal.flush", fixture.Health.Snapshot().FailureClass);
    }

    [Fact]
    public async Task Export_loop_fault_records_one_nonsecret_terminal_fact()
    {
        using var fixture = new JournalFixture();
        var recorder = new AuditExportTransitionRecorder(
            fixture.Journal,
            fixture.Health.ExportObserver,
            backlog: null);
        var loop = new AuditExportLoop(
            new FaultingSource(),
            healthObserver: recorder);

        var completion = loop.Start();
        await Assert.ThrowsAsync<IOException>(() => completion);

        Assert.Equal(
            ["export.started", "audit.export_stalled"],
            fixture.Events().Select(EventType));
        Assert.Equal(AuditExporterState.Faulted, fixture.Health.Snapshot().Exporter.State);
        Assert.DoesNotContain(
            "secret",
            string.Join('\n', fixture.Events().Select(value => value.RootElement.GetRawText())),
            StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<IOException>(async () => await loop.DisposeAsync());
        Assert.Equal(2, fixture.Sink.AppendCallCount);
    }

    [Fact]
    public void Automatic_transition_uses_reserved_capacity_without_reconsulting_admission()
    {
        using var fixture = new JournalFixture(
            (point, _) => point == AuditSinkFaultPoint.BeforeReserve);
        var recorder = new AuditExportTransitionRecorder(
            fixture.Journal,
            fixture.Health.ExportObserver,
            backlog: null);

        recorder.Observe(Observation(AuditExportLoopState.Running));

        Assert.Equal(["export.started"], fixture.Events().Select(EventType));
        Assert.Equal(AuditHealthState.Healthy, fixture.Health.Snapshot().State);
    }

    [Fact]
    public void Recorder_emits_hysteretic_backlog_crossings_without_per_byte_noise()
    {
        using var fixture = new JournalFixture();
        var backlog = new AuditBacklogHysteresis(
            enterWhenEffectiveFreeAtMostBytes: 900_000,
            recoverWhenEffectiveFreeAtLeastBytes: 1_100_000);
        var recorder = new AuditExportTransitionRecorder(
            fixture.Journal,
            fixture.Health.ExportObserver,
            backlog);

        fixture.Health.UpdateStorageMetrics(
            spoolBytes: 1_200_000,
            reservedBytes: 0,
            emergencyReserveBytes: 0);
        recorder.Observe(Observation(AuditExportLoopState.Running));
        recorder.Observe(Observation(AuditExportLoopState.WaitingForWork));

        fixture.Health.UpdateStorageMetrics(
            spoolBytes: 1_000_000,
            reservedBytes: 0,
            emergencyReserveBytes: 0);
        recorder.Observe(Observation(AuditExportLoopState.WaitingForWork));
        fixture.Health.UpdateStorageMetrics(
            spoolBytes: 900_000,
            reservedBytes: 0,
            emergencyReserveBytes: 0);
        recorder.Observe(Observation(AuditExportLoopState.WaitingForWork));

        Assert.Equal(
            ["export.started", "audit.backlog_high", "audit.backlog_recovered"],
            fixture.Events().Select(EventType));
    }

    [Fact]
    public void Backlog_hysteresis_has_inclusive_distinct_crossing_boundaries()
    {
        var hysteresis = new AuditBacklogHysteresis(
            enterWhenEffectiveFreeAtMostBytes: 200,
            recoverWhenEffectiveFreeAtLeastBytes: 400);

        Assert.Equal(
            AuditBacklogTransition.None,
            hysteresis.Observe(Snapshot(effectiveFreeBytes: 201)));
        Assert.Equal(
            AuditBacklogTransition.EnteredHighWater,
            hysteresis.Observe(Snapshot(effectiveFreeBytes: 200)));
        Assert.Equal(
            AuditBacklogTransition.None,
            hysteresis.Observe(Snapshot(effectiveFreeBytes: 399)));
        Assert.Equal(
            AuditBacklogTransition.Recovered,
            hysteresis.Observe(Snapshot(effectiveFreeBytes: 400)));
        Assert.Equal(
            AuditBacklogTransition.None,
            hysteresis.Observe(Snapshot(effectiveFreeBytes: 2000)));
    }

    private static AuditExportHealthObservation Observation(
        AuditExportLoopState state,
        AuditExportCoordinatorStep? step = null) =>
        new(
            new AuditExportLoopSnapshot(state, step, ScheduledDelay: null),
            step,
            new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero));

    private static AuditExportCoordinatorStep Step(
        AuditExportCoordinatorStepKind kind,
        Guid eventId,
        AuditExportFailureClass? failureClass = null,
        bool hasHealthWarning = false) =>
        new(
            kind,
            BootId,
            IsCurrentBoot: false,
            eventId,
            DetailCode: "secret.detail.must.not.escape",
            failureClass,
            HasHealthWarning: hasHealthWarning);

    private static AuditHealthSnapshot Snapshot(long effectiveFreeBytes)
    {
        const long capacity = 2_000;
        return new AuditHealthSnapshot(
            AuditHealthState.Healthy,
            AuditProtectionMode.Anchored,
            new string('a', 64),
            FailureClass: null,
            DegradedSinceUtc: null,
            SpoolBytes: capacity - effectiveFreeBytes,
            SpoolCapacityBytes: capacity,
            ReservedBytes: 0,
            EmergencyReserveBytes: 0,
            EmergencyReserveCapacityBytes: 512,
            EmergencyProbeCount: 0,
            EmergencyProbeFirstUtc: null,
            EmergencyProbeLastUtc: null,
            Exporter: new AuditExporterHealthSnapshot(
                AuditExporterState.Running,
                StateChangedUtc: null,
                ScheduledDelay: null,
                NextActionUtc: null,
                Blocked: null,
                LastProgress: null,
                LastAcknowledgment: null,
                HasHealthWarning: false));
    }

    private static string EventType(JsonDocument value) =>
        value.RootElement.GetProperty("event_type").GetString()!;

    private static string? ParentEventId(JsonDocument value) =>
        value.RootElement.GetProperty("correlation").GetProperty("parent_event_id").GetString();

    private static string? Target(JsonDocument value) =>
        value.RootElement.GetProperty("session").GetProperty("declared_target").GetString();

    private sealed class CompleteSource : IAuditExportStepSource
    {
        public Task<AuditExportCoordinatorStep> ExportNextAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new AuditExportCoordinatorStep(
                AuditExportCoordinatorStepKind.Complete,
                BootId,
                IsCurrentBoot: true,
                EventId: null,
                DetailCode: "chain.complete"));

        public void Dispose()
        {
        }
    }

    private sealed class FaultingSource : IAuditExportStepSource
    {
        public Task<AuditExportCoordinatorStep> ExportNextAsync(
            CancellationToken cancellationToken) =>
            Task.FromException<AuditExportCoordinatorStep>(
                new IOException("secret transport response"));

        public void Dispose()
        {
        }
    }

    private sealed class JournalFixture : IDisposable
    {
        internal JournalFixture(
            Func<AuditSinkFaultPoint, int, bool>? faultInjector = null)
        {
            Options = AuditOptions.Create(
                Path.Combine(Path.GetTempPath(), "ptk-export-events-" + Guid.NewGuid().ToString("N")),
                AuditProtectionMode.Anchored,
                new string('a', 64),
                maxRecordBytes: 4096,
                segmentBytes: 1_048_576,
                aggregateBytes: 2_097_152,
                emergencyReserveBytes: 8192);
            Health = new AuditHealth(Options);
            Sink = new InMemoryAuditJournalSink(
                Options.SegmentBytes,
                Options.AggregateBytes,
                Options.ProtectionMode,
                Options.RetentionAge,
                faultInjector);
            Journal = new AuditJournal(
                Options,
                Health,
                Sink,
                "test",
                binaryDigest: null,
                Guid.Parse("32345678-1234-4abc-8def-0123456789ab"),
                Guid.Parse("42345678-1234-4abc-8def-0123456789ab"));
        }

        internal AuditOptions Options { get; }
        internal AuditHealth Health { get; }
        internal InMemoryAuditJournalSink Sink { get; }
        internal AuditJournal Journal { get; }

        internal IReadOnlyList<JsonDocument> Events() =>
            Sink.Lines.Select(line => JsonDocument.Parse(line)).ToArray();

        public void Dispose() => Journal.Dispose();
    }
}
