using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditExportCoordinatorTests : IDisposable
{
    private static readonly Guid HostId =
        Guid.Parse("22345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid CurrentBoot =
        Guid.Parse("12345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid OrphanBoot =
        Guid.Parse("32345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid SecondCurrentBoot =
        Guid.Parse("42345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid SecondOrphanBoot =
        Guid.Parse("52345678-1234-4abc-8def-0123456789ab");
    private const string ConfigurationIdentity =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly List<string> _roots = [];

    [Fact]
    public async Task Coordinator_adopts_and_completes_an_orphan_before_idle_current_boot()
    {
        var options = Options(NewRoot());
        var orphan = WriteClosedBoot(options, OrphanBoot);
        var transport = new CapturingTransport();
        using var current = new CurrentFixture(options, transport);
        using var coordinator = new AuditExportCoordinator(
            options,
            current.Source,
            transport);

        var step = await coordinator.ExportNextAsync(CancellationToken.None);

        Assert.Equal(AuditExportCoordinatorStepKind.Advanced, step.Kind);
        Assert.Equal(OrphanBoot, step.SupervisorBootId);
        Assert.False(step.IsCurrentBoot);
        Assert.Equal(orphan.EventId, step.EventId);
        Assert.Equal([orphan.EventId], transport.EventIds);
        Assert.True(AuditExportCheckpointStore.ReadSnapshot(
            options,
            OrphanBoot).ChainComplete);
        Assert.Equal(0, current.Store.Current.Sequence);
    }

    [Fact]
    public async Task Coordinator_carries_the_current_evidence_observer_into_adopted_orphans()
    {
        var options = Options(NewRoot());
        var orphan = WriteClosedBoot(options, OrphanBoot);
        var transport = new CapturingTransport();
        var observer = new CountingObserver();
        using var current = new CurrentFixture(
            options,
            transport,
            acknowledgmentObserver: observer);
        using var coordinator = new AuditExportCoordinator(
            options,
            current.Source,
            transport,
            observer);

        var step = await coordinator.ExportNextAsync(CancellationToken.None);

        Assert.Equal(AuditExportCoordinatorStepKind.Advanced, step.Kind);
        Assert.Equal(OrphanBoot, step.SupervisorBootId);
        Assert.Equal(orphan.EventId, step.EventId);
        Assert.Equal(1, observer.ObserveCalls);
        Assert.Equal(1, observer.CompleteCalls);
    }

    [Fact]
    public async Task Coordinator_never_creates_missing_orphan_checkpoint_controls()
    {
        var options = Options(NewRoot());
        var transport = new CapturingTransport();
        using var current = new CurrentFixture(options, transport);
        var orphanPath = Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(OrphanBoot, 0).FileName);
        using (var stream = SecureAuditStorage.CreateExclusiveFile(orphanPath))
            stream.Flush(flushToDisk: true);
        using var coordinator = new AuditExportCoordinator(
            options,
            current.Source,
            transport);

        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.ExportNextAsync(CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(
            options.RootDirectory,
            AuditExportCheckpointStore.CheckpointFileName(OrphanBoot))));
        Assert.False(File.Exists(Path.Combine(
            options.RootDirectory,
            AuditExportCheckpointStore.LockFileName(OrphanBoot))));
        Assert.Empty(transport.EventIds);
        Assert.Equal(0, current.Store.Current.Sequence);
    }

    [Fact]
    public async Task Segment_contention_unwinds_adoption_and_a_later_attempt_succeeds()
    {
        var options = Options(NewRoot());
        var orphan = WriteClosedBoot(options, OrphanBoot);
        var orphanPath = Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(OrphanBoot, 0).FileName);
        var contention = new FileStream(
            orphanPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        var transport = new CapturingTransport();
        using var current = new CurrentFixture(options, transport);
        using var coordinator = new AuditExportCoordinator(
            options,
            current.Source,
            transport);

        var busy = await coordinator.ExportNextAsync(CancellationToken.None);
        Assert.Equal(AuditExportCoordinatorStepKind.Idle, busy.Kind);
        Assert.Empty(transport.EventIds);
        contention.Dispose();

        var adopted = await coordinator.ExportNextAsync(CancellationToken.None);

        Assert.Equal(AuditExportCoordinatorStepKind.Advanced, adopted.Kind);
        Assert.Equal(orphan.EventId, adopted.EventId);
        Assert.Equal([orphan.EventId], transport.EventIds);
    }

    [Fact]
    public async Task Two_coordinators_racing_one_orphan_export_it_once()
    {
        var options = Options(NewRoot());
        var orphan = WriteClosedBoot(options, OrphanBoot);
        var transport = new CapturingTransport();
        using var firstCurrent = new CurrentFixture(
            options,
            transport,
            CurrentBoot);
        using var secondCurrent = new CurrentFixture(
            options,
            transport,
            SecondCurrentBoot);
        using var first = new AuditExportCoordinator(
            options,
            firstCurrent.Source,
            transport);
        using var second = new AuditExportCoordinator(
            options,
            secondCurrent.Source,
            transport);

        var results = await Task.WhenAll(
            first.ExportNextAsync(CancellationToken.None),
            second.ExportNextAsync(CancellationToken.None));

        Assert.Single(results, result =>
            result.SupervisorBootId == OrphanBoot &&
            result.Kind == AuditExportCoordinatorStepKind.Advanced);
        Assert.Equal([orphan.EventId], transport.EventIds);
        Assert.True(AuditExportCheckpointStore.ReadSnapshot(
            options,
            OrphanBoot).ChainComplete);
    }

    [Fact]
    public async Task Permanently_blocked_orphan_is_parked_while_another_boot_exports()
    {
        var options = Options(NewRoot());
        var firstOrphan = WriteClosedBoot(options, OrphanBoot);
        var secondOrphan = WriteClosedBoot(options, SecondOrphanBoot);
        var transport = new CapturingTransport(
            AuditExportAttemptResult.Blocked(
                AuditExportFailureClass.Configuration,
                "http.401"),
            AuditExportAttemptResult.Acknowledged(
                new string('b', 64),
                warning: false));
        using var current = new CurrentFixture(options, transport);
        using var coordinator = new AuditExportCoordinator(
            options,
            current.Source,
            transport);

        var blocked = await coordinator.ExportNextAsync(CancellationToken.None);
        var advanced = await coordinator.ExportNextAsync(CancellationToken.None);

        Assert.Equal(AuditExportCoordinatorStepKind.Blocked, blocked.Kind);
        Assert.Equal(OrphanBoot, blocked.SupervisorBootId);
        Assert.Equal(AuditExportCoordinatorStepKind.Advanced, advanced.Kind);
        Assert.Equal(SecondOrphanBoot, advanced.SupervisorBootId);
        Assert.Equal(
            [firstOrphan.EventId, secondOrphan.EventId],
            transport.EventIds);
        Assert.False(AuditExportCheckpointStore.ReadSnapshot(
            options,
            OrphanBoot).ChainComplete);
        Assert.True(AuditExportCheckpointStore.ReadSnapshot(
            options,
            SecondOrphanBoot).ChainComplete);
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // A failed assertion remains authoritative.
            }
        }
    }

    private string NewRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-export-coordinator-tests-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private static AuditOptions Options(string root) => AuditOptions.Create(
        root,
        AuditProtectionMode.Anchored,
        ConfigurationIdentity,
        maxRecordBytes: 4096,
        segmentBytes: 16_384,
        aggregateBytes: 65_536,
        emergencyReserveBytes: 8192,
        retentionAge: TimeSpan.FromMinutes(10),
        maxEvidenceBytes: 4096,
        evidenceAggregateBytes: 4096,
        evidenceRetentionAge: TimeSpan.FromMinutes(10));

    private static SerializedAuditEvent WriteClosedBoot(
        AuditOptions options,
        Guid supervisorBootId)
    {
        using var store = AuditExportCheckpointStore.CreateForWriter(
            options,
            supervisorBootId);
        using var sink = new FileAuditJournalSink(
            options,
            supervisorBootId,
            checkpointStore: store);
        var record = Record(supervisorBootId);
        sink.Append(record.Utf8Line);
        sink.FlushToDisk();
        return record;
    }

    private static SerializedAuditEvent Record(Guid supervisorBootId) =>
        AuditEventSerializer.Serialize(
            1,
            previousEventHash: null,
            new AuditProducerContext(
                HostId,
                supervisorBootId,
                null,
                4321,
                "1.2.3-test",
                ConfigurationIdentity),
            Input(),
            Guid.CreateVersion7(),
            DateTimeOffset.Parse("2026-07-11T12:34:56Z"),
            DateTimeOffset.Parse("2026-07-11T12:34:56Z"));

    private static AuditEventInput Input() => new()
    {
        EventType = "call.accepted",
        Session = new AuditSession(),
        Actor = new AuditActor
        {
            AttributionStrength = "transport_only",
            Transport = "mcp_stdio",
        },
        Correlation = new AuditCorrelation(),
        Request = new AuditRequest(),
        Routing = new AuditRouting(),
        Outcome = new AuditOutcome { TerminationCertainty = "not_applicable" },
        Coverage = new AuditCoverage
        {
            PtkRequest = true,
            RootProcessObserved = "not_applicable",
            DescendantsObserved = "not_applicable",
            RemoteEffectObserved = "not_applicable",
        },
        Audit = new AuditEventHealth
        {
            ProtectionMode = "anchored",
            ExportConfigurationIdentity = ConfigurationIdentity,
            HealthState = "healthy",
        },
    };

    private sealed class CurrentFixture : IDisposable
    {
        private readonly FileAuditJournalSink _sink;
        private readonly AuditJournal _journal;

        internal CurrentFixture(
            AuditOptions options,
            IAuditOtlpExportTransport transport,
            Guid? supervisorBootId = null,
            IAuditExportAcknowledgmentObserver? acknowledgmentObserver = null)
        {
            var bootId = supervisorBootId ?? CurrentBoot;
            Store = AuditExportCheckpointStore.CreateForWriter(options, bootId);
            _sink = new FileAuditJournalSink(
                options,
                bootId,
                checkpointStore: Store);
            _journal = new AuditJournal(
                options,
                new AuditHealth(options),
                _sink,
                "coordinator-test",
                binaryDigest: null,
                HostId,
                bootId);
            Source = new AuditBootExportSource(
                _journal,
                Store,
                transport,
                acknowledgmentObserver ?? AuditExportAcknowledgmentObserver.None);
        }

        internal AuditExportCheckpointStore Store { get; }

        internal AuditBootExportSource Source { get; }

        public void Dispose()
        {
            Source.Dispose();
            _journal.Dispose();
            Store.Dispose();
        }
    }

    private sealed class CapturingTransport : IAuditOtlpExportTransport
    {
        private readonly object _gate = new();
        private readonly List<Guid> _eventIds = [];
        private readonly Queue<AuditExportAttemptResult> _results;

        internal CapturingTransport(params AuditExportAttemptResult[] results)
        {
            _results = new Queue<AuditExportAttemptResult>(results);
        }

        public string ConfigurationIdentity =>
            AuditExportCoordinatorTests.ConfigurationIdentity;

        internal IReadOnlyList<Guid> EventIds
        {
            get
            {
                lock (_gate)
                    return _eventIds.ToArray();
            }
        }

        public Task<AuditExportAttemptResult> ExportAsync(
            AuditOtlpRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _eventIds.Add(record.EventId);
                return Task.FromResult(_results.Count == 0
                    ? AuditExportAttemptResult.Acknowledged(
                        new string('b', 64),
                        warning: false)
                    : _results.Dequeue());
            }
        }
    }

    private sealed class CountingObserver : IAuditExportAcknowledgmentObserver
    {
        internal int ObserveCalls { get; private set; }
        internal int CompleteCalls { get; private set; }

        public IAuditEvidenceAnchorLease ObserveAcknowledgment(
            ReadOnlyMemory<byte> exactJsonlBytes)
        {
            ObserveCalls++;
            return new Lease(this);
        }

        private sealed class Lease(CountingObserver owner) : IAuditEvidenceAnchorLease
        {
            public void CompleteAfterCheckpoint() => owner.CompleteCalls++;

            public void Dispose()
            {
            }
        }
    }
}
