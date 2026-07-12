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
        var reader = new AuditLiveSpoolReader(fixture.Journal);
        var otherReader = new AuditLiveSpoolReader(fixture.Journal);
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
            otherReader.AdvanceAfterDurableAcknowledgment(first));

        reader.AdvanceAfterDurableAcknowledgment(first);
        Assert.Throws<ArgumentException>(() =>
            reader.AdvanceAfterDurableAcknowledgment(first));

        var second = Assert.IsAssignableFrom<IAuditLiveSpoolRecordPosition>(
            reader.Poll().Record);
        Assert.Equal(secondExpected.Utf8Line.ToArray(), second.ExactJsonlBytes.ToArray());
        Assert.Equal(2, second.Sequence);
        Assert.Equal(first.EventHash, second.PreviousEventHash);
        reader.AdvanceAfterDurableAcknowledgment(second);

        var tail = reader.Poll();
        Assert.Equal(AuditLiveSpoolPollKind.AtCommittedTail, tail.Kind);
        Assert.Null(tail.Record);
        Assert.Equal(second.NextOffset, fixture.Sink.CurrentSegmentBytes);
        Assert.Equal(0, fixture.Store.Current.Sequence);
        Assert.False(fixture.Store.Current.ChainComplete);
    }

    [Fact]
    public void Rotation_is_an_explicit_repeatable_transition_not_a_live_tail_or_completion()
    {
        using var fixture = new LiveFixture();
        var reader = new AuditLiveSpoolReader(fixture.Journal);
        _ = fixture.Append("call.accepted");
        var record = Assert.IsAssignableFrom<IAuditLiveSpoolRecordPosition>(
            reader.Poll().Record);
        reader.AdvanceAfterDurableAcknowledgment(record);
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
        var reader = new AuditLiveSpoolReader(fixture.Journal);
        _ = fixture.Append("call.accepted");
        var record = Assert.IsAssignableFrom<IAuditLiveSpoolRecordPosition>(
            reader.Poll().Record);
        reader.AdvanceAfterDurableAcknowledgment(record);
        Assert.True(fixture.Sink.CanReserve(16_000));
        var finalIdentity = fixture.Sink.CurrentSegmentIdentity;
        fixture.Journal.Dispose();

        var closed = reader.Poll();

        Assert.Equal(AuditLiveSpoolPollKind.WriterClosed, closed.Kind);
        Assert.Equal(finalIdentity, closed.ObservedCurrentSegment);
        Assert.Null(closed.Record);
        Assert.Null(closed.Rotation);
        Assert.False(fixture.Store.Current.ChainComplete);
    }

    [Fact]
    public void Closed_prefix_proof_resumes_the_live_reader_with_exact_hash_state()
    {
        using var fixture = new LiveFixture();
        var reader = new AuditLiveSpoolReader(fixture.Journal);
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
        var reader = new AuditLiveSpoolReader(fixture.Journal);
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
}
