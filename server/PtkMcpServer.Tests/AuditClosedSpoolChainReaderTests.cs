using System.Runtime.InteropServices;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditClosedSpoolChainReaderTests : IDisposable
{
    private static readonly Guid BootId =
        Guid.Parse("12345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid HostId =
        Guid.Parse("22345678-1234-4abc-8def-0123456789ab");
    private static readonly DateTimeOffset BaseTime =
        DateTimeOffset.Parse("2026-07-11T12:34:56.1234567Z");
    private const string ConfigurationIdentity =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ChangedConfigurationIdentity =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string ThirdConfigurationIdentity =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private readonly List<string> _roots = [];

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

    [Fact]
    public void Resolves_exact_records_across_segments_and_produces_an_opaque_end_proof()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(3);
            WriteSegment(options, 0, records[0], records[1]);
            WriteSegment(options, 1, records[2]);
            WriteSegment(options, 2);
            using var reader = new AuditClosedSpoolChainReader(options, store);

            var recovery = reader.ResolveCheckpoint();
            var first = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                recovery).Position;
            AssertPosition(
                first,
                segment: 0,
                startOffset: 0,
                records[0]);

            var second = Assert.IsAssignableFrom<IAuditClosedSpoolRecordPosition>(
                reader.ReadNext(first));
            AssertPosition(
                second,
                segment: 0,
                startOffset: records[0].Utf8Line.Length,
                records[1]);
            var third = Assert.IsAssignableFrom<IAuditClosedSpoolRecordPosition>(
                reader.ReadNext(second));
            AssertPosition(third, segment: 1, startOffset: 0, records[2]);
            Assert.Null(reader.ReadNext(third));

            store.SaveForTests(CheckpointAfter(first));
            store.SaveForTests(CheckpointAfter(second));
            store.SaveForTests(CheckpointAfter(third));
            var endRecovery = reader.ResolveCheckpoint();
            var end = Assert.IsType<AuditClosedSpoolRecovery.ChainEnd>(
                endRecovery).Position;
            reader.MarkChainComplete(end);
            var complete = store.Current;
            Assert.True(complete.ChainComplete);
            Assert.Equal(third.Spool, complete.Spool);
            Assert.Equal(third.NextOffset, complete.ByteOffset);
            Assert.Equal(third.Sequence, complete.Sequence);
            Assert.Equal(third.EventId, complete.AcknowledgedEventId);
        }
    }

    [Fact]
    public void Closed_prefix_drains_while_the_live_boundary_remains_exclusive()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(3);
            var firstPath = WriteSegment(options, 0, records[0]);
            var secondPath = WriteSegment(options, 1, records[1]);
            var boundary = AuditSpoolSegmentIdentity.Create(BootId, 2);
            using var live = CreateLiveSegment(options, boundary.Index, records[2]);
            using var rotation = ObserveRotation(options, boundary);
            // A same-boot suffix is outside this snapshot even when its bytes
            // are not a valid closed record.
            WriteSegmentBytes(options, 3, [(byte)'x']);
            using var reader = new AuditClosedSpoolChainReader(options, store);

            var first = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                reader.ResolveClosedPrefix(rotation.Position)).Position;
            AssertPosition(first, 0, 0, records[0]);
            var second = Assert.IsAssignableFrom<IAuditClosedSpoolRecordPosition>(
                reader.ReadNext(first));
            AssertPosition(second, 1, 0, records[1]);
            Assert.Null(reader.ReadNext(second));

            reader.Acknowledge(first, ConfigurationIdentity);
            reader.Acknowledge(second, ConfigurationIdentity);
            var prefixEnd = Assert.IsType<AuditClosedSpoolRecovery.PrefixEnd>(
                reader.ResolveClosedPrefix(rotation.Position));

            Assert.IsAssignableFrom<IAuditClosedSpoolPrefixEndPosition>(
                prefixEnd.Position);
            Assert.False(store.Current.ChainComplete);
            Assert.Throws<IOException>(() => new FileStream(
                SegmentPath(options, boundary.Index),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read));
            Assert.Throws<IOException>(() => new FileStream(
                firstPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read));
            Assert.Throws<IOException>(() => new FileStream(
                secondPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read));
        }
    }

    [Fact]
    public void Prefix_end_cannot_complete_and_stale_chain_end_is_rejected_across_modes()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var record = Records(1)[0];
            WriteSegment(options, 0, record);
            using var reader = new AuditClosedSpoolChainReader(options, store);
            var position = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                reader.ResolveCheckpoint()).Position;
            reader.Acknowledge(position, ConfigurationIdentity);
            var staleChainEnd = Assert.IsType<AuditClosedSpoolRecovery.ChainEnd>(
                reader.ResolveCheckpoint()).Position;

            var boundary = AuditSpoolSegmentIdentity.Create(BootId, 1);
            using var live = CreateLiveSegment(options, boundary.Index);
            using var rotation = ObserveRotation(options, boundary);
            var prefixEnd = Assert.IsType<AuditClosedSpoolRecovery.PrefixEnd>(
                reader.ResolveClosedPrefix(rotation.Position)).Position;

            Assert.False(prefixEnd is IAuditClosedSpoolChainEndPosition);
            Assert.False(store.Current.ChainComplete);
            Assert.Throws<ArgumentException>(() =>
                reader.MarkChainComplete(staleChainEnd));
            Assert.False(store.Current.ChainComplete);
        }
    }

    [Fact]
    public void Corrupt_closed_prefix_faults_without_moving_the_checkpoint()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var record = Records(1)[0];
            WriteSegment(options, 0, record);
            WriteSegmentBytes(options, 1, [(byte)'{', (byte)'}', (byte)'\n']);
            var boundary = AuditSpoolSegmentIdentity.Create(BootId, 2);
            using var live = CreateLiveSegment(options, boundary.Index);
            using var rotation = ObserveRotation(options, boundary);
            store.SaveForTests(new AuditExportCheckpoint(
                BootId,
                false,
                AuditSpoolSegmentIdentity.Create(BootId, 0),
                record.Utf8Line.Length,
                record.Sequence,
                record.EventId,
                blockedRecord: null));
            var checkpointBefore = File.ReadAllBytes(store.CheckpointPath);
            using var reader = new AuditClosedSpoolChainReader(options, store);

            Assert.Throws<IOException>(() => reader.ResolveClosedPrefix(rotation.Position));

            Assert.Equal(checkpointBefore, File.ReadAllBytes(store.CheckpointPath));
            Assert.Equal(record.Sequence, store.Current.Sequence);
            Assert.False(store.Current.ChainComplete);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Closed_prefix_requires_every_pre_boundary_segment_nonempty(bool omitMiddle)
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var record = Records(1)[0];
            WriteSegment(options, 0, record);
            if (!omitMiddle)
                WriteSegment(options, 1);
            var boundary = AuditSpoolSegmentIdentity.Create(BootId, 2);
            using var live = CreateLiveSegment(options, boundary.Index);
            using var rotation = ObserveRotation(options, boundary);
            using var reader = new AuditClosedSpoolChainReader(options, store);

            Assert.Throws<IOException>(() => reader.ResolveClosedPrefix(rotation.Position));
            Assert.Equal(0, store.Current.Sequence);
        }
    }

    [Fact]
    public void Closed_prefix_rejects_forged_and_wrong_boot_rotation_capabilities()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            WriteSegment(options, 0, Records(1));
            var boundary = AuditSpoolSegmentIdentity.Create(BootId, 1);
            using var live = CreateLiveSegment(options, boundary.Index);
            using var reader = new AuditClosedSpoolChainReader(options, store);

            Assert.Throws<ArgumentException>(() =>
                reader.ResolveClosedPrefix(new ForgedRotationPosition()));

            var otherBoot = Guid.Parse("32345678-1234-4abc-8def-0123456789ab");
            using var wrongBoot = ObserveRotation(
                options,
                AuditSpoolSegmentIdentity.Create(otherBoot, 1),
                otherBoot);
            Assert.Throws<ArgumentException>(() =>
                reader.ResolveClosedPrefix(wrongBoot.Position));
        }
    }

    [Fact]
    public void Blocked_checkpoint_resolves_to_the_exact_next_record()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(2);
            WriteSegment(options, 0, records);
            var firstEnd = records[0].Utf8Line.Length;
            var next = AuditSpoolSegmentIdentity.Create(BootId, 0);
            store.SaveForTests(new AuditExportCheckpoint(
                BootId,
                false,
                next,
                firstEnd,
                1,
                records[0].EventId,
                Blocked(next, firstEnd, records[1])));

            using var reader = new AuditClosedSpoolChainReader(options, store);
            var resolved = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                reader.ResolveCheckpoint()).Position;

            AssertPosition(resolved, 0, firstEnd, records[1]);
        }
    }

    [Fact]
    public void Opaque_record_acknowledgment_advances_only_the_exact_next_record()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(2);
            WriteSegment(options, 0, records);
            using var reader = new AuditClosedSpoolChainReader(options, store);
            var first = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                reader.ResolveCheckpoint()).Position;
            var second = Assert.IsAssignableFrom<IAuditClosedSpoolRecordPosition>(
                reader.ReadNext(first));

            Assert.Throws<ArgumentException>(() =>
                reader.Acknowledge(second, ConfigurationIdentity));
            Assert.Equal(0, store.Current.Sequence);

            reader.Acknowledge(first, ConfigurationIdentity);
            Assert.Equal(first.Spool, store.Current.Spool);
            Assert.Equal(first.NextOffset, store.Current.ByteOffset);
            Assert.Equal(first.Sequence, store.Current.Sequence);
            Assert.Equal(first.EventId, store.Current.AcknowledgedEventId);

            reader.Acknowledge(second, ConfigurationIdentity);
            Assert.Equal(second.Spool, store.Current.Spool);
            Assert.Equal(second.NextOffset, store.Current.ByteOffset);
            Assert.Equal(second.Sequence, store.Current.Sequence);
            Assert.Equal(second.EventId, store.Current.AcknowledgedEventId);
        }
    }

    [Fact]
    public void Opaque_record_block_persists_and_clears_only_that_record()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var record = Records(1)[0];
            WriteSegment(options, 0, record);
            using var reader = new AuditClosedSpoolChainReader(options, store);
            var position = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                reader.ResolveCheckpoint()).Position;
            var responseDigest = new string('b', 64);

            reader.PersistBlock(
                position,
                AuditExportFailureClass.Data,
                "http.400",
                responseDigest,
                BaseTime,
                ConfigurationIdentity);

            var blocked = Assert.IsType<AuditExportBlockedRecord>(store.Current.BlockedRecord);
            Assert.Equal(position.Spool, blocked.Spool);
            Assert.Equal(position.StartOffset, blocked.ByteOffset);
            Assert.Equal(position.Sequence, blocked.Sequence);
            Assert.Equal(position.EventId, blocked.EventId);
            Assert.Equal(AuditExportFailureClass.Data, blocked.FailureClass);
            Assert.Equal("http.400", blocked.DetailCode);
            Assert.Equal(responseDigest, blocked.ResponseDigest);
            Assert.Equal(BaseTime, blocked.FirstFailureUtc);
            Assert.Equal(ConfigurationIdentity, blocked.ExportConfigurationIdentity);

            Assert.Throws<InvalidOperationException>(() =>
                reader.Acknowledge(position, ConfigurationIdentity));
            Assert.Same(blocked, store.Current.BlockedRecord);
            Assert.Equal(0, store.Current.Sequence);
        }
    }

    [Fact]
    public void Configuration_block_requires_a_one_use_authorization_to_acknowledge()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var record = Records(1)[0];
            WriteSegment(options, 0, record);
            using (var initialReader = new AuditClosedSpoolChainReader(options, store))
            {
                var initial = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                    initialReader.ResolveCheckpoint()).Position;
                initialReader.PersistBlock(
                    initial,
                    AuditExportFailureClass.Configuration,
                    "http.401",
                    responseDigest: null,
                    BaseTime,
                    ConfigurationIdentity);
            }

            using var reader = new AuditClosedSpoolChainReader(
                OptionsWithIdentity(options, ChangedConfigurationIdentity),
                store);
            var position = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                reader.ResolveCheckpoint()).Position;
            Assert.Throws<InvalidOperationException>(() =>
                reader.Acknowledge(position, ChangedConfigurationIdentity));

            var authorization = reader.AuthorizeConfigurationRetry(position);
            var authorizedBlock = Assert.IsType<AuditExportBlockedRecord>(
                store.Current.BlockedRecord);
            Assert.Equal(ChangedConfigurationIdentity, authorizedBlock.ExportConfigurationIdentity);
            Assert.Equal(BaseTime, authorizedBlock.FirstFailureUtc);

            reader.AcknowledgeConfigurationRetry(authorization);
            Assert.Null(store.Current.BlockedRecord);
            Assert.Equal(position.Sequence, store.Current.Sequence);
            Assert.Equal(position.EventId, store.Current.AcknowledgedEventId);
            Assert.Throws<InvalidOperationException>(() =>
                reader.AcknowledgeConfigurationRetry(authorization));
        }
    }

    [Fact]
    public void Configuration_retry_authorization_rejects_forged_stale_and_cross_record_proofs()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(2);
            WriteSegment(options, 0, records);
            using (var initialReader = new AuditClosedSpoolChainReader(options, store))
            {
                var initial = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                    initialReader.ResolveCheckpoint()).Position;
                initialReader.PersistBlock(
                    initial,
                    AuditExportFailureClass.Configuration,
                    "http.401",
                    responseDigest: null,
                    BaseTime,
                    ConfigurationIdentity);
            }

            using (var changedReader = new AuditClosedSpoolChainReader(
                       OptionsWithIdentity(options, ChangedConfigurationIdentity),
                       store))
            {
                var first = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                    changedReader.ResolveCheckpoint()).Position;
                var second = Assert.IsAssignableFrom<IAuditClosedSpoolRecordPosition>(
                    changedReader.ReadNext(first));

                Assert.Throws<ArgumentException>(() =>
                    changedReader.AuthorizeConfigurationRetry(second));
                Assert.Equal(
                    ConfigurationIdentity,
                    store.Current.BlockedRecord?.ExportConfigurationIdentity);
                Assert.Throws<ArgumentException>(() =>
                    changedReader.AcknowledgeConfigurationRetry(
                        new FakeConfigurationRetryAuthorization()));

                var stale = changedReader.AuthorizeConfigurationRetry(first);
                _ = changedReader.ResolveCheckpoint();
                Assert.Throws<ArgumentException>(() =>
                    changedReader.PersistConfigurationRetryBlock(
                        stale,
                        AuditExportFailureClass.Data,
                        "http.400",
                        responseDigest: null));
            }

            using var thirdReader = new AuditClosedSpoolChainReader(
                OptionsWithIdentity(options, ThirdConfigurationIdentity),
                store);
            var retried = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                thirdReader.ResolveCheckpoint()).Position;
            var authorization = thirdReader.AuthorizeConfigurationRetry(retried);
            thirdReader.PersistConfigurationRetryBlock(
                authorization,
                AuditExportFailureClass.Configuration,
                "retry.http.503",
                responseDigest: null);

            var blocked = Assert.IsType<AuditExportBlockedRecord>(store.Current.BlockedRecord);
            Assert.Equal(ThirdConfigurationIdentity, blocked.ExportConfigurationIdentity);
            Assert.Equal("retry.http.503", blocked.DetailCode);
            Assert.Equal(BaseTime, blocked.FirstFailureUtc);
            Assert.Throws<InvalidOperationException>(() =>
                thirdReader.PersistConfigurationRetryBlock(
                    authorization,
                    AuditExportFailureClass.Data,
                    "http.400",
                    responseDigest: null));
        }
    }

    [Fact]
    public void Configuration_retry_authorization_is_consumed_before_a_failed_settlement()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var record = Records(1)[0];
            WriteSegment(options, 0, record);
            using (var initialReader = new AuditClosedSpoolChainReader(options, store))
            {
                var initial = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                    initialReader.ResolveCheckpoint()).Position;
                initialReader.PersistBlock(
                    initial,
                    AuditExportFailureClass.Configuration,
                    "http.401",
                    responseDigest: null,
                    BaseTime,
                    ConfigurationIdentity);
            }

            using var reader = new AuditClosedSpoolChainReader(
                OptionsWithIdentity(options, ChangedConfigurationIdentity),
                store);
            var position = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                reader.ResolveCheckpoint()).Position;
            var authorization = reader.AuthorizeConfigurationRetry(position);

            Assert.Throws<ArgumentException>(() =>
                reader.PersistConfigurationRetryBlock(
                    authorization,
                    AuditExportFailureClass.Configuration,
                    "INVALID",
                    responseDigest: null));
            Assert.Throws<InvalidOperationException>(() =>
                reader.PersistConfigurationRetryBlock(
                    authorization,
                    AuditExportFailureClass.Configuration,
                    "retry.http.503",
                    responseDigest: null));

            var blocked = Assert.IsType<AuditExportBlockedRecord>(store.Current.BlockedRecord);
            Assert.Equal(ChangedConfigurationIdentity, blocked.ExportConfigurationIdentity);
            Assert.Equal("http.401", blocked.DetailCode);
            Assert.Equal(BaseTime, blocked.FirstFailureUtc);
        }
    }

    [Fact]
    public void Opaque_end_proof_is_the_only_reader_path_to_persist_completion()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            WriteSegment(options, 0);
            using var reader = new AuditClosedSpoolChainReader(options, store);
            var end = Assert.IsType<AuditClosedSpoolRecovery.ChainEnd>(
                reader.ResolveCheckpoint()).Position;

            reader.MarkChainComplete(end);

            Assert.True(store.Current.ChainComplete);
            Assert.Throws<ArgumentException>(() =>
                reader.MarkChainComplete(new FakeEndPosition()));
        }
    }

    [Fact]
    public void Blocked_checkpoint_with_a_different_next_event_is_rejected()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(2);
            WriteSegment(options, 0, records);
            var firstEnd = records[0].Utf8Line.Length;
            var spool = AuditSpoolSegmentIdentity.Create(BootId, 0);
            var wrong = Blocked(
                spool,
                firstEnd,
                records[1],
                Guid.CreateVersion7());
            store.SaveForTests(new AuditExportCheckpoint(
                BootId,
                false,
                spool,
                firstEnd,
                1,
                records[0].EventId,
                wrong));

            using var reader = new AuditClosedSpoolChainReader(options, store);
            Assert.Throws<IOException>(() => reader.ResolveCheckpoint());
        }
    }

    [Theory]
    [InlineData(CheckpointMismatch.MidRecordOffset)]
    [InlineData(CheckpointMismatch.WrongSequence)]
    [InlineData(CheckpointMismatch.WrongEventId)]
    public void Checkpoint_must_identify_an_exact_record_end(
        CheckpointMismatch mismatch)
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(2);
            WriteSegment(options, 0, records);
            var spool = AuditSpoolSegmentIdentity.Create(BootId, 0);
            var checkpoint = mismatch switch
            {
                CheckpointMismatch.MidRecordOffset => new AuditExportCheckpoint(
                    BootId,
                    false,
                    spool,
                    records[0].Utf8Line.Length - 1,
                    1,
                    records[0].EventId,
                    null),
                CheckpointMismatch.WrongSequence => new AuditExportCheckpoint(
                    BootId,
                    false,
                    spool,
                    records[0].Utf8Line.Length + records[1].Utf8Line.Length,
                    1,
                    records[1].EventId,
                    null),
                CheckpointMismatch.WrongEventId => new AuditExportCheckpoint(
                    BootId,
                    false,
                    spool,
                    records[0].Utf8Line.Length,
                    1,
                    Guid.CreateVersion7(),
                    null),
                _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
            };
            store.SaveForTests(checkpoint);

            using var reader = new AuditClosedSpoolChainReader(options, store);
            Assert.Throws<IOException>(() => reader.ResolveCheckpoint());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Requires_segment_zero_and_contiguous_indices(bool omitZero)
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(2);
            if (omitZero)
            {
                WriteSegment(options, 1, records[0]);
            }
            else
            {
                WriteSegment(options, 0, records[0]);
                WriteSegment(options, 2, records[1]);
            }

            using var reader = new AuditClosedSpoolChainReader(options, store);
            Assert.Throws<IOException>(() => reader.ResolveCheckpoint());
        }
    }

    [Fact]
    public void Rejects_cross_segment_sequence_or_hash_discontinuity()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var first = Record(1, null);
            var second = Record(2, new string('b', 64));
            WriteSegment(options, 0, first);
            WriteSegment(options, 1, second);

            using var reader = new AuditClosedSpoolChainReader(options, store);
            Assert.Throws<IOException>(() => reader.ResolveCheckpoint());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rejects_torn_and_oversized_records(bool oversized)
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var bytes = oversized
                ? Enumerable.Repeat((byte)'x', options.MaxRecordBytes).ToArray()
                : Records(1)[0].Utf8Line.Span[..^1].ToArray();
            WriteSegmentBytes(options, 0, bytes);

            using var reader = new AuditClosedSpoolChainReader(options, store);
            Assert.Throws<IOException>(() => reader.ResolveCheckpoint());
        }
    }

    [Fact]
    public void Rejects_an_empty_intermediate_segment()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            WriteSegment(options, 0);
            WriteSegment(options, 1, Records(1));

            using var reader = new AuditClosedSpoolChainReader(options, store);
            Assert.Throws<IOException>(() => reader.ResolveCheckpoint());
        }
    }

    [Fact]
    public void Rejects_a_chain_that_exceeds_the_configured_aggregate_bound()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var fullSegment = new byte[checked((int)options.SegmentBytes)];
            for (var index = 0; index < 5; index++)
                WriteSegmentBytes(options, index, fullSegment);

            using var reader = new AuditClosedSpoolChainReader(options, store);
            var exception = Assert.Throws<IOException>(() => reader.ResolveCheckpoint());
            Assert.Contains("configured aggregate bound", exception.Message);
        }
    }

    [Fact]
    public void Rejects_a_chain_before_its_retained_handle_count_is_unbounded()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            for (var index = 0;
                 index <= AuditClosedSpoolChainReader.MaximumClosedChainSegments;
                 index++)
            {
                WriteSegmentBytes(options, index, [(byte)'x']);
            }

            using var reader = new AuditClosedSpoolChainReader(options, store);
            var exception = Assert.Throws<IOException>(() => reader.ResolveCheckpoint());
            Assert.Contains("recovery segment bound", exception.Message);
        }
    }

    [Fact]
    public void Live_segment_rejection_releases_every_partially_acquired_handle()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(2);
            var firstPath = WriteSegment(options, 0, records[0]);
            var secondPath = WriteSegment(options, 1, records[1]);
            using var live = new FileStream(
                secondPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            using var reader = new AuditClosedSpoolChainReader(options, store);

            Assert.Throws<IOException>(() => reader.ResolveCheckpoint());
            using var firstProbe = new FileStream(
                firstPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
        }
    }

    [Fact]
    public void Retains_every_segment_handle_exclusively_until_dispose()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(2);
            var paths = new[]
            {
                WriteSegment(options, 0, records[0]),
                WriteSegment(options, 1, records[1]),
            };
            var reader = new AuditClosedSpoolChainReader(options, store);
            _ = reader.ResolveCheckpoint();

            foreach (var path in paths)
            {
                Assert.Throws<IOException>(() => new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read));
            }

            reader.Dispose();
            foreach (var path in paths)
            {
                using var probe = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);
            }
        }
    }

    [Fact]
    public async Task Snapshot_acquisition_waits_for_the_shared_spool_quota_lease()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var record = Records(1)[0];
            WriteSegment(options, 0, record);
            using var quota = AuditSpoolQuotaLease.AcquireExisting(
                options.SpoolDirectory);
            using var reader = new AuditClosedSpoolChainReader(options, store);
            var resolution = Task.Run(reader.ResolveCheckpoint);

            var early = await Task.WhenAny(
                resolution,
                Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.NotSame(resolution, early);

            quota.Dispose();
            var recovered = await resolution.WaitAsync(TimeSpan.FromSeconds(10));
            var position = Assert.IsType<AuditClosedSpoolRecovery.Record>(recovered);
            Assert.Equal(record.EventId, position.Position.EventId);
        }
    }

    [Fact]
    public void Path_replacement_after_acquisition_never_redirects_record_bytes()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(2);
            WriteSegment(options, 0, records[0]);
            var secondPath = WriteSegment(options, 1, records[1]);
            using var reader = new AuditClosedSpoolChainReader(options, store);
            var first = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                reader.ResolveCheckpoint()).Position;
            var replacement = ProtectedTemporary(
                options,
                records[1].Utf8Line.Length,
                fill: 0x78);

            if (OperatingSystem.IsWindows())
            {
                Assert.Throws<IOException>(() => File.Move(
                    replacement,
                    secondPath,
                    overwrite: true));
            }
            else
            {
                File.Move(replacement, secondPath, overwrite: true);
            }

            var second = Assert.IsAssignableFrom<IAuditClosedSpoolRecordPosition>(
                reader.ReadNext(first));
            Assert.Equal(records[1].Utf8Line.ToArray(), second.ExactJsonlBytes.ToArray());
        }
    }

    [Fact]
    public void Same_name_same_length_replacement_during_acquisition_is_rejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var (options, store) = OwnedFixture();
        using (store)
        {
            var record = Records(1)[0];
            var segmentPath = WriteSegment(options, 0, record);
            var replacement = ProtectedTemporary(
                options,
                record.Utf8Line.Length,
                fill: 0x78);
            using var reader = new AuditClosedSpoolChainReader(
                options,
                store,
                () => File.Move(replacement, segmentPath, overwrite: true));

            var exception = Assert.Throws<IOException>(() => reader.ResolveCheckpoint());
            Assert.Contains("retained audit segment", exception.Message, StringComparison.OrdinalIgnoreCase);
            using var probe = new FileStream(
                segmentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
        }
    }

    [Fact]
    public void Hard_linked_segment_names_are_rejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var (options, store) = OwnedFixture();
        using (store)
        {
            var firstPath = WriteSegment(options, 0, Records(1));
            var secondPath = SegmentPath(options, 1);
            Assert.Equal(0, CreateHardLink(firstPath, secondPath));

            using var reader = new AuditClosedSpoolChainReader(options, store);
            var exception = Assert.Throws<IOException>(() => reader.ResolveCheckpoint());
            Assert.Contains("link count", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Reader_is_mechanically_bound_to_matching_checkpoint_ownership()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var otherOptions = Options(NewRoot());
            Assert.Throws<ArgumentException>(() =>
                new AuditClosedSpoolChainReader(otherOptions, store));

            var localOptions = AuditOptions.Create(
                NewRoot(),
                maxRecordBytes: 4096,
                segmentBytes: 16_384,
                aggregateBytes: 65_536,
                emergencyReserveBytes: 8192,
                maxEvidenceBytes: 4096,
                evidenceAggregateBytes: 4096);
            Assert.Throws<ArgumentException>(() =>
                new AuditClosedSpoolChainReader(localOptions, store));
        }
    }

    [Fact]
    public void Reader_retains_the_checkpoint_lease_until_it_is_disposed()
    {
        var (options, store) = OwnedFixture();
        WriteSegment(options, 0);
        var reader = new AuditClosedSpoolChainReader(options, store);
        store.Dispose();

        Assert.False(AuditExportCheckpointStore.TryAcquireExisting(
            options,
            BootId,
            out var competing));
        Assert.Null(competing);
        reader.Dispose();
        Assert.True(AuditExportCheckpointStore.TryAcquireExisting(
            options,
            BootId,
            out var successor));
        using var successorOwner = Assert.IsType<AuditExportCheckpointStore>(successor);
    }

    [Fact]
    public void End_proof_is_unforgeable_and_invalidated_by_a_new_snapshot()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            WriteSegment(options, 0);
            using var reader = new AuditClosedSpoolChainReader(options, store);
            var firstEnd = Assert.IsType<AuditClosedSpoolRecovery.ChainEnd>(
                reader.ResolveCheckpoint()).Position;
            Assert.Throws<ArgumentException>(() =>
                reader.MarkChainComplete(new FakeEndPosition()));

            _ = reader.ResolveCheckpoint();
            Assert.Throws<ArgumentException>(() =>
                reader.MarkChainComplete(firstEnd));
        }
    }

    [Fact]
    public void Checkpoint_change_during_acquisition_fails_and_releases_segment_handles()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var record = Records(1)[0];
            var path = WriteSegment(options, 0, record);
            var spool = AuditSpoolSegmentIdentity.Create(BootId, 0);
            using var reader = new AuditClosedSpoolChainReader(
                options,
                store,
                () => store.SaveForTests(new AuditExportCheckpoint(
                    BootId,
                    false,
                    spool,
                    record.Utf8Line.Length,
                    1,
                    record.EventId,
                    null)));

            Assert.Throws<IOException>(() => reader.ResolveCheckpoint());
            using var probe = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
        }
    }

    [Fact]
    public void Acquisition_failure_cannot_mask_checkpoint_ownership_loss()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var record = Records(1)[0];
            var path = WriteSegment(options, 0, record);
            var spool = AuditSpoolSegmentIdentity.Create(BootId, 0);
            using var reader = new AuditClosedSpoolChainReader(
                options,
                store,
                () =>
                {
                    store.SaveForTests(new AuditExportCheckpoint(
                        BootId,
                        false,
                        spool,
                        record.Utf8Line.Length,
                        1,
                        record.EventId,
                        null));
                    throw new InjectedAcquisitionException();
                });

            var exception = Assert.Throws<IOException>(() => reader.ResolveCheckpoint());
            Assert.Contains(
                "while closed spool acquisition failed",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Throws<IOException>(() => _ = store.Current);
            using var probe = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
        }
    }

    private (AuditOptions Options, AuditExportCheckpointStore Store) OwnedFixture()
    {
        var options = Options(NewRoot());
        var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        SecureAuditStorage.PrepareRoot(options.SpoolDirectory);
        using (AuditSpoolQuotaLease.CreateControlAndAcquire(options.SpoolDirectory))
        {
        }
        return (options, store);
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

    private static AuditOptions OptionsWithIdentity(
        AuditOptions source,
        string configurationIdentity) => AuditOptions.Create(
        source.RootDirectory,
        AuditProtectionMode.Anchored,
        configurationIdentity,
        source.MaxRecordBytes,
        source.SegmentBytes,
        source.AggregateBytes,
        source.EmergencyReserveBytes,
        source.RetentionAge,
        source.MaxEvidenceBytes,
        source.EvidenceAggregateBytes,
        source.EvidenceRetentionAge);

    private string NewRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-closed-reader-tests-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private static SerializedAuditEvent[] Records(int count)
    {
        var records = new SerializedAuditEvent[count];
        string? previousHash = null;
        for (var index = 0; index < count; index++)
        {
            records[index] = Record(index + 1, previousHash);
            previousHash = records[index].EventHash;
        }
        return records;
    }

    private static SerializedAuditEvent Record(long sequence, string? previousHash) =>
        AuditEventSerializer.Serialize(
            sequence,
            previousHash,
            new AuditProducerContext(
                HostId,
                BootId,
                null,
                4321,
                "1.2.3-test",
                ConfigurationIdentity),
            Input(),
            Guid.CreateVersion7(),
            BaseTime,
            BaseTime);

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

    private static string WriteSegment(
        AuditOptions options,
        int index,
        params SerializedAuditEvent[] records)
    {
        var bytes = records.SelectMany(record => record.Utf8Line.ToArray()).ToArray();
        return WriteSegmentBytes(options, index, bytes);
    }

    private static string WriteSegmentBytes(
        AuditOptions options,
        int index,
        byte[] bytes)
    {
        var path = SegmentPath(options, index);
        using var stream = SecureAuditStorage.CreateExclusiveFile(path);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return path;
    }

    private static FileStream CreateLiveSegment(
        AuditOptions options,
        int index,
        params SerializedAuditEvent[] records)
    {
        var bytes = records.SelectMany(record => record.Utf8Line.ToArray()).ToArray();
        var stream = SecureAuditStorage.CreateExclusiveFile(SegmentPath(options, index));
        try
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static RotationObservation ObserveRotation(
        AuditOptions options,
        AuditSpoolSegmentIdentity boundary,
        Guid? supervisorBootId = null) =>
        new(options, supervisorBootId ?? BootId, boundary);

    private static string SegmentPath(AuditOptions options, int index) =>
        Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(BootId, index).FileName);

    private static string ProtectedTemporary(
        AuditOptions options,
        int length,
        byte fill)
    {
        var path = Path.Combine(options.RootDirectory, ".reader-replacement-" + Guid.NewGuid().ToString("N"));
        using var stream = SecureAuditStorage.CreateExclusiveFile(path);
        stream.Write(Enumerable.Repeat(fill, length).ToArray());
        stream.Flush(flushToDisk: true);
        return path;
    }

    private static AuditExportCheckpoint CheckpointAfter(
        IAuditClosedSpoolRecordPosition position) => new(
            BootId,
            false,
            position.Spool,
            position.NextOffset,
            position.Sequence,
            position.EventId,
            null);

    private static AuditExportBlockedRecord Blocked(
        AuditSpoolSegmentIdentity spool,
        long offset,
        SerializedAuditEvent record,
        Guid? eventId = null) => new(
            spool,
            offset,
            record.Sequence,
            eventId ?? record.EventId,
            AuditExportFailureClass.Configuration,
            "http.401",
            null,
            BaseTime,
            ConfigurationIdentity);

    private static void AssertPosition(
        IAuditClosedSpoolRecordPosition actual,
        int segment,
        long startOffset,
        SerializedAuditEvent expected)
    {
        Assert.Equal(AuditSpoolSegmentIdentity.Create(BootId, segment), actual.Spool);
        Assert.Equal(startOffset, actual.StartOffset);
        Assert.Equal(startOffset + expected.Utf8Line.Length, actual.NextOffset);
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Equal(expected.EventId, actual.EventId);
        Assert.Equal(expected.EventHash, actual.EventHash);
        Assert.Equal(expected.Utf8Line.ToArray(), actual.ExactJsonlBytes.ToArray());
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLink(string existingPath, string newPath);

    public enum CheckpointMismatch
    {
        MidRecordOffset,
        WrongSequence,
        WrongEventId,
    }

    private sealed class FakeEndPosition : IAuditClosedSpoolChainEndPosition;

    private sealed class ForgedRotationPosition : IAuditLiveSpoolRotationPosition;

    private sealed class RotationObservation : IDisposable
    {
        private readonly AuditJournal _journal;

        internal RotationObservation(
            AuditOptions options,
            Guid supervisorBootId,
            AuditSpoolSegmentIdentity boundary)
        {
            var sink = new RotationSourceSink(boundary);
            _journal = new AuditJournal(
                options,
                new AuditHealth(options),
                sink,
                "closed-prefix-rotation-test",
                binaryDigest: null,
                HostId,
                supervisorBootId);
            var reader = new AuditLiveSpoolReader(_journal);
            var first = reader.Poll();
            Position = Assert.IsAssignableFrom<IAuditLiveSpoolRotationPosition>(
                first.Rotation);
            var repeated = reader.Poll();
            Assert.Same(Position, repeated.Rotation);
        }

        internal IAuditLiveSpoolRotationPosition Position { get; }

        public void Dispose() => _journal.Dispose();
    }

    private sealed class RotationSourceSink(
        AuditSpoolSegmentIdentity boundary) :
        IAuditJournalSink,
        IAuditCommittedSpoolSource
    {
        public AuditSpoolSegmentIdentity CurrentSegmentIdentity { get; } = boundary;

        public long SegmentCapacityBytes => 16_384;

        public long AggregateCapacityBytes => 65_536;

        public long CurrentSegmentBytes => 0;

        public long TotalBytes => 0;

        public bool CanReserve(long reservedBytes) => true;

        public void Append(ReadOnlyMemory<byte> line) =>
            throw new InvalidOperationException("The rotation source is read-only.");

        public void FlushToDisk() =>
            throw new InvalidOperationException("The rotation source is read-only.");

        public AuditCommittedSpoolReadStatus TryReadCommitted(
            AuditSpoolSegmentIdentity identity,
            long offset,
            Span<byte> destination,
            out int bytesRead,
            out long committedTail)
        {
            bytesRead = 0;
            committedTail = 0;
            return AuditCommittedSpoolReadStatus.Rotated;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeConfigurationRetryAuthorization :
        IAuditClosedSpoolConfigurationRetryAuthorization;

    private sealed class InjectedAcquisitionException : Exception;
}
