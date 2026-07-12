using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditLiveSpoolReaderTests
{
    private static readonly Guid HostId =
        Guid.Parse("22345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid BootId =
        Guid.Parse("12345678-1234-4abc-8def-0123456789ab");
    private const string ConfigurationIdentity =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Reads_exact_records_and_advances_only_the_opaque_pending_position()
    {
        using var fixture = new LiveFixture();
        using var reader = new AuditLiveSpoolReader(fixture.Journal, fixture.Store);
        using var otherReader = new AuditLiveSpoolReader(fixture.Journal, fixture.Store);
        var firstExpected = fixture.Append("call.accepted");
        var secondExpected = fixture.Append("call.completed");

        var firstPoll = reader.Poll();
        Assert.Equal(AuditLiveSpoolPollKind.Record, firstPoll.Kind);
        var first = Assert.IsAssignableFrom<IAuditLiveSpoolRecordPosition>(
            firstPoll.Record);
        Assert.Equal(firstExpected.Utf8Line.ToArray(), first.ExactJsonlBytes.ToArray());
        Assert.Equal(firstExpected.EventId, first.EventId);
        Assert.Equal(1, first.Sequence);
        Assert.Null(first.PreviousEventHash);

        var repeated = reader.Poll();
        Assert.Same(first, repeated.Record);
        Assert.Throws<ArgumentException>(() =>
            otherReader.Acknowledge(first, ConfigurationIdentity));

        reader.Acknowledge(first, ConfigurationIdentity);
        Assert.Throws<ArgumentException>(() =>
            reader.Acknowledge(first, ConfigurationIdentity));

        var second = Assert.IsAssignableFrom<IAuditLiveSpoolRecordPosition>(
            reader.Poll().Record);
        Assert.Equal(secondExpected.Utf8Line.ToArray(), second.ExactJsonlBytes.ToArray());
        Assert.Equal(2, second.Sequence);
        Assert.Equal(first.EventHash, second.PreviousEventHash);
        reader.Acknowledge(second, ConfigurationIdentity);

        var tail = reader.Poll();
        Assert.Equal(AuditLiveSpoolPollKind.AtCommittedTail, tail.Kind);
        Assert.Null(tail.Record);
        Assert.Equal(second.NextOffset, fixture.Sink.CurrentSegmentBytes);
        Assert.Equal(2, fixture.Store.Current.Sequence);
        Assert.False(fixture.Store.Current.ChainComplete);
    }

    [Fact]
    public void Durable_checkpoint_failure_leaves_the_exact_live_record_pending()
    {
        using var fixture = new LiveFixture();
        using var reader = new AuditLiveSpoolReader(fixture.Journal, fixture.Store);
        _ = fixture.Append("call.accepted");
        var pending = Assert.IsAssignableFrom<IAuditLiveSpoolRecordPosition>(
            reader.Poll().Record);
        fixture.Store.SaveForTests(new AuditExportCheckpoint(
            BootId,
            chainComplete: true,
            spool: null,
            byteOffset: 0,
            sequence: 0,
            acknowledgedEventId: null,
            blockedRecord: null));

        Assert.Throws<ArgumentException>(() =>
            reader.Acknowledge(pending, ConfigurationIdentity));
        Assert.Same(pending, reader.Poll().Record);
    }

    [Fact]
    public void Durable_live_block_pins_the_pending_record_and_checkpoint()
    {
        using var fixture = new LiveFixture();
        using var reader = new AuditLiveSpoolReader(fixture.Journal, fixture.Store);
        _ = fixture.Append("call.accepted");
        var pending = Assert.IsAssignableFrom<IAuditLiveSpoolRecordPosition>(
            reader.Poll().Record);
        var firstFailure = DateTimeOffset.Parse("2026-07-11T12:34:56Z");

        reader.PersistBlock(
            pending,
            AuditExportFailureClass.Configuration,
            "http.401",
            responseDigest: null,
            firstFailure,
            ConfigurationIdentity);

        var blocked = Assert.IsType<AuditExportBlockedRecord>(
            fixture.Store.Current.BlockedRecord);
        Assert.Equal(pending.EventId, blocked.EventId);
        Assert.Equal(firstFailure, blocked.FirstFailureUtc);
        Assert.Same(pending, reader.Poll().Record);
        Assert.Throws<InvalidOperationException>(() =>
            reader.Acknowledge(pending, ConfigurationIdentity));
        Assert.Equal(0, fixture.Store.Current.Sequence);
    }

    [Fact]
    public void Rotation_is_an_explicit_repeatable_transition_not_a_live_tail_or_completion()
    {
        using var fixture = new LiveFixture();
        using var reader = new AuditLiveSpoolReader(fixture.Journal, fixture.Store);
        _ = fixture.Append("call.accepted");
        var record = Assert.IsAssignableFrom<IAuditLiveSpoolRecordPosition>(
            reader.Poll().Record);
        reader.Acknowledge(record, ConfigurationIdentity);
        Assert.True(fixture.Sink.CanReserve(16_000));
        var boundary = fixture.Sink.CurrentSegmentIdentity;

        var rotated = reader.Poll();
        Assert.Equal(AuditLiveSpoolPollKind.Rotated, rotated.Kind);
        Assert.Equal(boundary, rotated.ObservedCurrentSegment);
        Assert.Null(rotated.Record);
        Assert.NotNull(rotated.Rotation);
        var repeated = reader.Poll();
        Assert.Equal(AuditLiveSpoolPollKind.Rotated, repeated.Kind);
        Assert.Equal(boundary, repeated.ObservedCurrentSegment);
        Assert.Same(rotated.Rotation, repeated.Rotation);
        Assert.False(fixture.Store.Current.ChainComplete);
    }

    [Fact]
    public void Writer_closure_dominates_an_unseen_rotation_and_reports_the_final_segment()
    {
        using var fixture = new LiveFixture();
        using var reader = new AuditLiveSpoolReader(fixture.Journal, fixture.Store);
        _ = fixture.Append("call.accepted");
        var record = Assert.IsAssignableFrom<IAuditLiveSpoolRecordPosition>(
            reader.Poll().Record);
        reader.Acknowledge(record, ConfigurationIdentity);
        Assert.True(fixture.Sink.CanReserve(16_000));
        var finalIdentity = fixture.Sink.CurrentSegmentIdentity;
        fixture.Journal.Dispose();

        var closed = reader.Poll();

        Assert.Equal(AuditLiveSpoolPollKind.WriterClosed, closed.Kind);
        Assert.Equal(finalIdentity, closed.ObservedCurrentSegment);
        Assert.Null(closed.Record);
        Assert.Null(closed.Rotation);
        Assert.NotNull(closed.WriterClosed);
        Assert.Same(closed.WriterClosed, reader.Poll().WriterClosed);
        Assert.False(fixture.Store.Current.ChainComplete);
    }

    [Fact]
    public void Writer_closed_proof_promotes_the_exact_final_chain_after_unseen_rotation()
    {
        using var fixture = new LiveFixture();
        using var live = new AuditLiveSpoolReader(fixture.Journal, fixture.Store);
        var firstExpected = fixture.Append("call.accepted");
        Assert.True(fixture.Sink.CanReserve(16_000));
        var secondExpected = fixture.Append("call.completed");
        var finalIdentity = fixture.Sink.CurrentSegmentIdentity;
        fixture.Journal.Dispose();
        var closure = Assert.IsAssignableFrom<IAuditLiveSpoolWriterClosedPosition>(
            live.Poll().WriterClosed);
        using var closed = new AuditClosedSpoolChainReader(
            fixture.Options,
            fixture.Store);

        Assert.Throws<ArgumentException>(() =>
            closed.ResolveAfterWriterClosed(new ForgedWriterClosedPosition()));
        var first = Assert.IsType<AuditClosedSpoolRecovery.Record>(
            closed.ResolveAfterWriterClosed(closure)).Position;
        Assert.Equal(firstExpected.EventId, first.EventId);
        closed.Acknowledge(first, ConfigurationIdentity);
        var second = Assert.IsAssignableFrom<IAuditClosedSpoolRecordPosition>(
            closed.ReadNext(first));
        Assert.Equal(secondExpected.EventId, second.EventId);
        Assert.Equal(finalIdentity, second.Spool);
        closed.Acknowledge(second, ConfigurationIdentity);
        var end = Assert.IsType<AuditClosedSpoolRecovery.ChainEnd>(
            closed.ResolveAfterWriterClosed(closure)).Position;

        closed.MarkChainComplete(end);

        Assert.True(fixture.Store.Current.ChainComplete);
        Assert.Equal(second.EventId, fixture.Store.Current.AcknowledgedEventId);
    }

    [Fact]
    public void Writer_closed_proof_rejects_a_same_boot_suffix_after_observed_final_segment()
    {
        using var fixture = new LiveFixture();
        using var live = new AuditLiveSpoolReader(fixture.Journal, fixture.Store);
        _ = fixture.Append("call.accepted");
        fixture.Journal.Dispose();
        var closure = Assert.IsAssignableFrom<IAuditLiveSpoolWriterClosedPosition>(
            live.Poll().WriterClosed);
        var suffix = Path.Combine(
            fixture.Options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(BootId, 1).FileName);
        using (var stream = SecureAuditStorage.CreateExclusiveFile(suffix))
            stream.Flush(flushToDisk: true);
        using var closed = new AuditClosedSpoolChainReader(
            fixture.Options,
            fixture.Store);

        Assert.Throws<IOException>(() =>
            closed.ResolveAfterWriterClosed(closure));
        Assert.Equal(0, fixture.Store.Current.Sequence);
    }

    [Fact]
    public void Closed_prefix_proof_resumes_the_live_reader_with_exact_hash_state()
    {
        using var fixture = new LiveFixture();
        using var reader = new AuditLiveSpoolReader(fixture.Journal, fixture.Store);
        var firstExpected = fixture.Append("call.accepted");
        var secondExpected = fixture.Append("call.completed");
        Assert.True(fixture.Sink.CanReserve(16_000));

        var rotation = Assert.IsAssignableFrom<IAuditLiveSpoolRotationPosition>(
            reader.Poll().Rotation);
        using var closed = new AuditClosedSpoolChainReader(
            fixture.Options,
            fixture.Store);
        var first = Assert.IsType<AuditClosedSpoolRecovery.Record>(
            closed.ResolveClosedPrefix(rotation)).Position;
        Assert.Equal(firstExpected.EventId, first.EventId);
        closed.Acknowledge(first, ConfigurationIdentity);
        var second = Assert.IsAssignableFrom<IAuditClosedSpoolRecordPosition>(
            closed.ReadNext(first));
        Assert.Equal(secondExpected.EventId, second.EventId);
        closed.Acknowledge(second, ConfigurationIdentity);
        var prefixEnd = Assert.IsType<AuditClosedSpoolRecovery.PrefixEnd>(
            closed.ResolveClosedPrefix(rotation)).Position;

        reader.AdvanceAfterClosedPrefix(rotation, prefixEnd);
        Assert.Throws<ArgumentException>(() =>
            reader.AdvanceAfterClosedPrefix(rotation, prefixEnd));
        using (var releasedPrefix = new FileStream(
                   Path.Combine(
                       fixture.Options.SpoolDirectory,
                       AuditSpoolSegmentIdentity.Create(BootId, 0).FileName),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            Assert.True(releasedPrefix.Length > 0);
        }

        var thirdExpected = fixture.Append("call.failed");
        var third = Assert.IsAssignableFrom<IAuditLiveSpoolRecordPosition>(
            reader.Poll().Record);
        Assert.Equal(3, third.Sequence);
        Assert.Equal(secondExpected.EventHash, third.PreviousEventHash);
        Assert.Equal(thirdExpected.EventHash, third.EventHash);
    }

    [Fact]
    public void Closed_prefix_proof_cannot_move_live_state_after_checkpoint_change()
    {
        using var fixture = new LiveFixture();
        using var reader = new AuditLiveSpoolReader(fixture.Journal, fixture.Store);
        _ = fixture.Append("call.accepted");
        Assert.True(fixture.Sink.CanReserve(16_000));
        var rotation = Assert.IsAssignableFrom<IAuditLiveSpoolRotationPosition>(
            reader.Poll().Rotation);
        using var closed = new AuditClosedSpoolChainReader(
            fixture.Options,
            fixture.Store);
        var record = Assert.IsType<AuditClosedSpoolRecovery.Record>(
            closed.ResolveClosedPrefix(rotation)).Position;
        closed.Acknowledge(record, ConfigurationIdentity);
        var prefixEnd = Assert.IsType<AuditClosedSpoolRecovery.PrefixEnd>(
            closed.ResolveClosedPrefix(rotation)).Position;
        var checkpoint = fixture.Store.Current;
        fixture.Store.SaveForTests(new AuditExportCheckpoint(
            checkpoint.SupervisorBootId,
            chainComplete: true,
            checkpoint.Spool,
            checkpoint.ByteOffset,
            checkpoint.Sequence,
            checkpoint.AcknowledgedEventId,
            blockedRecord: null));

        Assert.Throws<IOException>(() =>
            reader.AdvanceAfterClosedPrefix(rotation, prefixEnd));
        Assert.Same(rotation, reader.Poll().Rotation);
    }

    [Fact]
    public async Task Closed_prefix_pump_returns_a_proof_without_completing_the_chain()
    {
        using var fixture = new LiveFixture();
        using var live = new AuditLiveSpoolReader(fixture.Journal, fixture.Store);
        _ = fixture.Append("call.accepted");
        _ = fixture.Append("call.completed");
        Assert.True(fixture.Sink.CanReserve(16_000));
        var rotation = Assert.IsAssignableFrom<IAuditLiveSpoolRotationPosition>(
            live.Poll().Rotation);
        using var closed = new AuditClosedSpoolChainReader(
            fixture.Options,
            fixture.Store);
        var transport = new AcknowledgingTransport();
        using var pump = AuditClosedSpoolExportPump.ForClosedPrefix(
            closed,
            rotation,
            transport);

        Assert.Equal(
            AuditClosedSpoolExportStepKind.Advanced,
            (await pump.ExportNextAsync(CancellationToken.None)).Kind);
        var complete = await pump.ExportNextAsync(CancellationToken.None);

        Assert.Equal(AuditClosedSpoolExportStepKind.PrefixComplete, complete.Kind);
        Assert.NotNull(complete.PrefixEnd);
        Assert.Equal(2, transport.Calls);
        Assert.False(fixture.Store.Current.ChainComplete);
        live.AdvanceAfterClosedPrefix(rotation, complete.PrefixEnd!);
        Assert.Equal(AuditLiveSpoolPollKind.AtCommittedTail, live.Poll().Kind);
    }

    [Fact]
    public async Task Writer_closed_pump_completes_only_the_observed_final_chain()
    {
        using var fixture = new LiveFixture();
        using var live = new AuditLiveSpoolReader(fixture.Journal, fixture.Store);
        _ = fixture.Append("call.accepted");
        fixture.Journal.Dispose();
        var closure = Assert.IsAssignableFrom<IAuditLiveSpoolWriterClosedPosition>(
            live.Poll().WriterClosed);
        using var closed = new AuditClosedSpoolChainReader(
            fixture.Options,
            fixture.Store);
        var transport = new AcknowledgingTransport();
        using var pump = AuditClosedSpoolExportPump.AfterWriterClosed(
            closed,
            closure,
            transport);

        var complete = await pump.ExportNextAsync(CancellationToken.None);

        Assert.Equal(AuditClosedSpoolExportStepKind.ChainComplete, complete.Kind);
        Assert.Equal(1, transport.Calls);
        Assert.True(fixture.Store.Current.ChainComplete);
    }

    private sealed class LiveFixture : IDisposable
    {
        private readonly string _root;

        internal LiveFixture()
        {
            _root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ptk-live-spool-reader-tests-" + Guid.NewGuid().ToString("N"));
            Options = AuditOptions.Create(
                _root,
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
            Store = AuditExportCheckpointStore.CreateForWriter(Options, BootId);
            Sink = new FileAuditJournalSink(
                Options,
                BootId,
                checkpointStore: Store);
            Journal = new AuditJournal(
                Options,
                new AuditHealth(Options),
                Sink,
                "live-reader-test",
                binaryDigest: null,
                hostId: HostId,
                supervisorBootId: BootId);
        }

        internal AuditOptions Options { get; }

        internal AuditExportCheckpointStore Store { get; }

        internal FileAuditJournalSink Sink { get; }

        internal AuditJournal Journal { get; }

        internal SerializedAuditEvent Append(string eventType)
        {
            Assert.True(Journal.TryReserve(1, out var reservation, out var failure));
            Assert.Null(failure);
            using (reservation)
                return Journal.Append(reservation!, Input(eventType));
        }

        public void Dispose()
        {
            Journal.Dispose();
            Store.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static AuditEventInput Input(string eventType) => new()
    {
        EventType = eventType,
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

    private sealed class ForgedWriterClosedPosition :
        IAuditLiveSpoolWriterClosedPosition;

    private sealed class AcknowledgingTransport : IAuditOtlpExportTransport
    {
        internal int Calls { get; private set; }

        public string ConfigurationIdentity =>
            AuditLiveSpoolReaderTests.ConfigurationIdentity;

        public Task<AuditExportAttemptResult> ExportAsync(
            AuditOtlpRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(AuditExportAttemptResult.Acknowledged(
                new string('b', 64),
                warning: false));
        }
    }
}
