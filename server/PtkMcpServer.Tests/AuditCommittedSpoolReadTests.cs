using System.Text;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditCommittedSpoolReadTests : IDisposable
{
    private static readonly Guid BootId =
        Guid.ParseExact("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "D");
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
    public void Live_reader_exposes_only_the_flush_committed_prefix()
    {
        var options = Options(NewRoot(), maxRecordBytes: 256, segmentBytes: 1024);
        using var sink = new FileAuditJournalSink(options, BootId);
        var identity = sink.CurrentSegmentIdentity;
        var first = Encoding.ASCII.GetBytes("{\"record\":1}\n");
        var second = Encoding.ASCII.GetBytes("{\"record\":2}\n");
        var buffer = new byte[options.MaxRecordBytes];

        sink.Append(first);
        Assert.Equal(
            AuditCommittedSpoolReadStatus.AtCommittedTail,
            sink.TryReadCommitted(identity, 0, buffer, out var beforeFlush, out var initialTail));
        Assert.Equal(0, beforeFlush);
        Assert.Equal(0, initialTail);

        sink.FlushToDisk();
        Assert.Equal(
            AuditCommittedSpoolReadStatus.Data,
            sink.TryReadCommitted(identity, 0, buffer, out var firstRead, out var firstTail));
        Assert.Equal(first, buffer.AsSpan(0, firstRead).ToArray());
        Assert.Equal(first.Length, firstTail);

        // A prefix read would move the authoritative writer position if this
        // used FileStream.Read instead of positional RandomAccess.Read.
        var oneByte = new byte[1];
        Assert.Equal(
            AuditCommittedSpoolReadStatus.Data,
            sink.TryReadCommitted(identity, 0, oneByte, out var prefixRead, out _));
        Assert.Equal(1, prefixRead);
        Assert.Equal(first[0], oneByte[0]);

        sink.Append(second);
        Assert.Equal(
            AuditCommittedSpoolReadStatus.AtCommittedTail,
            sink.TryReadCommitted(
                identity,
                firstTail,
                buffer,
                out var secondBeforeFlush,
                out var unchangedTail));
        Assert.Equal(0, secondBeforeFlush);
        Assert.Equal(firstTail, unchangedTail);

        sink.FlushToDisk();
        Assert.Equal(
            AuditCommittedSpoolReadStatus.Data,
            sink.TryReadCommitted(
                identity,
                firstTail,
                buffer,
                out var secondRead,
                out var finalTail));
        Assert.Equal(second, buffer.AsSpan(0, secondRead).ToArray());
        Assert.Equal(first.Length + second.Length, finalTail);
        Assert.Equal(
            AuditCommittedSpoolReadStatus.Data,
            sink.TryReadCommitted(identity, 0, buffer, out var allRead, out _));
        Assert.Equal(first.Concat(second), buffer.AsSpan(0, allRead).ToArray());
    }

    [Fact]
    public void Physically_flushed_bytes_remain_visible_after_a_later_boundary_failure()
    {
        var options = Options(NewRoot(), maxRecordBytes: 256, segmentBytes: 1024);
        using var sink = new FileAuditJournalSink(
            options,
            BootId,
            faultInjector: (point, attempt) =>
                point == FileAuditSinkFaultPoint.AfterPhysicalFlush && attempt == 1);
        var identity = sink.CurrentSegmentIdentity;
        var line = Encoding.ASCII.GetBytes("{\"ambiguous\":true}\n");
        var buffer = new byte[options.MaxRecordBytes];

        sink.Append(line);
        Assert.Throws<IOException>(sink.FlushToDisk);

        Assert.Equal(
            AuditCommittedSpoolReadStatus.Data,
            sink.TryReadCommitted(identity, 0, buffer, out var bytesRead, out var committedTail));
        Assert.Equal(line, buffer.AsSpan(0, bytesRead).ToArray());
        Assert.Equal(line.Length, committedTail);
        Assert.Equal(line.Length, new FileInfo(sink.CurrentSegmentPath).Length);
    }

    [Fact]
    public void Rotation_makes_the_prior_identity_closed_instead_of_live()
    {
        var options = Options(NewRoot(), maxRecordBytes: 256, segmentBytes: 1024);
        using var sink = new FileAuditJournalSink(options, BootId);
        var priorIdentity = sink.CurrentSegmentIdentity;
        var priorPath = sink.CurrentSegmentPath;
        var line = Enumerable.Repeat((byte)'x', 240).ToArray();
        line[^1] = (byte)'\n';
        sink.Append(line);
        sink.FlushToDisk();

        Assert.True(sink.CanReserve(800));

        Assert.Equal(1, sink.CurrentSegmentIdentity.Index);
        Assert.Equal(
            AuditCommittedSpoolReadStatus.NotCurrent,
            sink.TryReadCommitted(
                priorIdentity,
                0,
                new byte[256],
                out var bytesRead,
                out var committedTail));
        Assert.Equal(0, bytesRead);
        Assert.Equal(0, committedTail);
        using var closed = new FileStream(
            priorPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        Assert.Equal(line.Length, closed.Length);
    }

    [Fact]
    public void Journal_wrapper_reads_committed_bytes_and_reports_closed_or_other_segments()
    {
        var options = Options(NewRoot(), maxRecordBytes: 4096, segmentBytes: 16_384);
        var health = new AuditHealth(options);
        var sink = new FileAuditJournalSink(options, BootId);
        var identity = sink.CurrentSegmentIdentity;
        var journal = new AuditJournal(
            options,
            health,
            sink,
            "committed-read-test",
            binaryDigest: null,
            hostId: Guid.ParseExact("11111111-2222-4333-8444-555555555555", "D"),
            supervisorBootId: BootId);
        Assert.True(journal.TryReserve(1, out var reservation, out var failureClass));
        Assert.Null(failureClass);
        using (reservation)
        {
            var serialized = journal.Append(reservation!, Input("server.started"));

            var read = journal.ReadCommittedSpool(identity, 0, options.MaxRecordBytes);

            Assert.Equal(AuditCommittedSpoolReadStatus.Data, read.Status);
            Assert.Equal(serialized.Utf8Line.ToArray(), read.Bytes.ToArray());
            Assert.Equal(serialized.Utf8Line.Length, read.CommittedTail);
            var atTail = journal.ReadCommittedSpool(
                identity,
                read.CommittedTail,
                options.MaxRecordBytes);
            Assert.Equal(AuditCommittedSpoolReadStatus.AtCommittedTail, atTail.Status);
            Assert.Empty(atTail.Bytes.ToArray());
        }

        var otherIdentity = AuditSpoolSegmentIdentity.Create(
            Guid.ParseExact("22222222-3333-4444-8555-666666666666", "D"),
            0);
        Assert.Equal(
            AuditCommittedSpoolReadStatus.NotCurrent,
            journal.ReadCommittedSpool(otherIdentity, 0, options.MaxRecordBytes).Status);

        journal.Dispose();
        Assert.Equal(
            AuditCommittedSpoolReadStatus.NotCurrent,
            journal.ReadCommittedSpool(identity, 0, options.MaxRecordBytes).Status);
    }

    [Fact]
    public async Task Journal_gate_hides_a_flush_in_progress_then_publishes_one_complete_prefix()
    {
        var options = Options(NewRoot(), maxRecordBytes: 4096, segmentBytes: 16_384);
        var health = new AuditHealth(options);
        using var physicalFlushReached = new ManualResetEventSlim();
        using var releaseFlush = new ManualResetEventSlim();
        var sink = new FileAuditJournalSink(
            options,
            BootId,
            faultInjector: (point, _) =>
            {
                if (point != FileAuditSinkFaultPoint.AfterPhysicalFlush) return false;
                physicalFlushReached.Set();
                if (!releaseFlush.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The committed-read test did not release its flush barrier.");
                return false;
            });
        using var journal = new AuditJournal(
            options,
            health,
            sink,
            "committed-read-gate-test",
            binaryDigest: null,
            hostId: Guid.ParseExact("11111111-2222-4333-8444-555555555555", "D"),
            supervisorBootId: BootId);
        var identity = sink.CurrentSegmentIdentity;
        Assert.True(journal.TryReserve(1, out var reservation, out _));
        using (reservation)
        {
            var append = Task.Run(() => journal.Append(reservation!, Input("server.started")));
            Assert.True(physicalFlushReached.Wait(TimeSpan.FromSeconds(10)));
            var read = Task.Run(() =>
                journal.ReadCommittedSpool(identity, 0, options.MaxRecordBytes));
            try
            {
                var first = await Task.WhenAny(read, Task.Delay(TimeSpan.FromMilliseconds(250)));
                Assert.NotSame(read, first);
            }
            finally
            {
                releaseFlush.Set();
            }

            var serialized = await append.WaitAsync(TimeSpan.FromSeconds(10));
            var observed = await read.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(AuditCommittedSpoolReadStatus.Data, observed.Status);
            Assert.Equal(serialized.Utf8Line.ToArray(), observed.Bytes.ToArray());
            Assert.Equal(serialized.Utf8Line.Length, observed.CommittedTail);
        }
    }

    [Fact]
    public void Poison_after_physical_flush_keeps_the_durable_prefix_drainable()
    {
        var options = Options(NewRoot(), maxRecordBytes: 4096, segmentBytes: 16_384);
        var health = new AuditHealth(options);
        var sink = new FileAuditJournalSink(
            options,
            BootId,
            faultInjector: (point, attempt) =>
                point == FileAuditSinkFaultPoint.AfterPhysicalFlush && attempt == 1);
        using var journal = new AuditJournal(
            options,
            health,
            sink,
            "committed-read-poison-test",
            binaryDigest: null,
            hostId: Guid.ParseExact("11111111-2222-4333-8444-555555555555", "D"),
            supervisorBootId: BootId);
        var identity = sink.CurrentSegmentIdentity;
        Assert.True(journal.TryReserve(1, out var reservation, out _));
        using (reservation)
        {
            Assert.Throws<AuditUnavailableException>(() =>
                journal.Append(reservation!, Input("server.started")));
        }

        Assert.True(journal.IsPoisoned);
        var read = journal.ReadCommittedSpool(identity, 0, options.MaxRecordBytes);
        Assert.Equal(AuditCommittedSpoolReadStatus.Data, read.Status);
        Assert.Equal((long)read.Bytes.Length, read.CommittedTail);
        Assert.EndsWith("\n", Encoding.UTF8.GetString(read.Bytes.Span), StringComparison.Ordinal);
        Assert.Contains(
            "\"event_type\":\"server.started\"",
            Encoding.UTF8.GetString(read.Bytes.Span),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_bytes_flushed_during_close_remain_recoverable_ambiguous_evidence()
    {
        var options = Options(NewRoot(), maxRecordBytes: 4096, segmentBytes: 16_384);
        var health = new AuditHealth(options);
        var sink = new FileAuditJournalSink(
            options,
            BootId,
            faultInjector: (point, attempt) =>
                point == FileAuditSinkFaultPoint.BeforePhysicalFlush && attempt == 1);
        var segmentPath = sink.CurrentSegmentPath;
        var identity = sink.CurrentSegmentIdentity;
        var journal = new AuditJournal(
            options,
            health,
            sink,
            "committed-read-preflush-poison-test",
            binaryDigest: null,
            hostId: Guid.ParseExact("11111111-2222-4333-8444-555555555555", "D"),
            supervisorBootId: BootId);
        try
        {
            Assert.True(journal.TryReserve(1, out var reservation, out _));
            using (reservation)
            {
                Assert.Throws<AuditUnavailableException>(() =>
                    journal.Append(reservation!, Input("server.started")));
            }

            var live = journal.ReadCommittedSpool(identity, 0, options.MaxRecordBytes);
            Assert.Equal(AuditCommittedSpoolReadStatus.AtCommittedTail, live.Status);
            Assert.Equal(0, live.CommittedTail);

            journal.Dispose();
            var recoveredBytes = File.ReadAllBytes(segmentPath);
            Assert.NotEmpty(recoveredBytes);
            Assert.Equal((byte)'\n', recoveredBytes[^1]);
            Assert.Contains(
                "\"event_type\":\"server.started\"",
                Encoding.UTF8.GetString(recoveredBytes),
                StringComparison.Ordinal);

            // Startup accepts the complete hash-chained record. It remains an
            // unclosed prior-boot fact for lifecycle recovery, not data to
            // truncate merely because the old process missed its flush ACK.
            using var recovered = new FileAuditJournalSink(
                options,
                Guid.ParseExact("22222222-3333-4444-8555-666666666666", "D"));
        }
        finally
        {
            journal.Dispose();
        }
    }

    private AuditOptions Options(string root, int maxRecordBytes, long segmentBytes)
    {
        return AuditOptions.Create(
            root,
            maxRecordBytes: maxRecordBytes,
            segmentBytes: segmentBytes,
            aggregateBytes: segmentBytes * 4,
            emergencyReserveBytes: maxRecordBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: maxRecordBytes,
            evidenceAggregateBytes: maxRecordBytes,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
    }

    private string NewRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-committed-spool-read-tests-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
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
            ProtectionMode = "local-only",
            HealthState = "healthy",
        },
    };
}
