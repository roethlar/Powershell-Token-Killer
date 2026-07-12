using System.Text;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditAnchoredWriterPreparationTests : IDisposable
{
    private const string ConfigurationIdentity =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
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
    public void Preparation_retains_exact_empty_segment_without_exposing_a_sink()
    {
        var options = Options(NewRoot());
        var bootId = NewBoot();

        using var prepared = FileAuditJournalSink.PrepareAnchored(options, bootId);

        Assert.DoesNotContain(
            typeof(IAuditJournalSink),
            prepared.GetType().GetInterfaces());
        Assert.Equal(
            AuditSpoolSegmentIdentity.Create(bootId, 0).FileName,
            Path.GetFileName(prepared.SegmentPath));
        Assert.Equal(0, new FileInfo(prepared.SegmentPath).Length);
        SecureAuditStorage.VerifyExternalProtectedFile(prepared.SegmentPath);
        Assert.Throws<IOException>(() => new FileStream(
            prepared.SegmentPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None));
        Assert.False(File.Exists(CheckpointPath(options, bootId)));
        Assert.False(File.Exists(LockPath(options, bootId)));
    }

    [Fact]
    public void Prepared_checkpoint_then_activation_is_the_only_production_order()
    {
        var options = Options(NewRoot());
        var bootId = NewBoot();
        using var prepared = FileAuditJournalSink.PrepareAnchored(options, bootId);
        using var store = AuditExportCheckpointStore.CreateForPreparedWriter(
            options,
            prepared);

        Assert.True(File.Exists(CheckpointPath(options, bootId)));
        Assert.True(File.Exists(LockPath(options, bootId)));
        Assert.Equal(bootId, store.Current.SupervisorBootId);
        Assert.Equal(0, store.Current.Sequence);
        Assert.False(store.Current.ChainComplete);

        using var sink = prepared.Activate(store);
        Assert.Equal(bootId, sink.CurrentSegmentIdentity.SupervisorBootId);
        Assert.True(sink.CanReserve(1));
        sink.Append(new byte[] { (byte)'\n' });
        sink.FlushToDisk();
    }

    [Fact]
    public void Startup_removes_only_an_abandoned_exact_bare_preparation()
    {
        var options = Options(NewRoot());
        var abandonedBoot = NewBoot();
        string abandonedPath;
        using (var abandoned = FileAuditJournalSink.PrepareAnchored(options, abandonedBoot))
            abandonedPath = abandoned.SegmentPath;
        Assert.True(File.Exists(abandonedPath));

        using var successor = FileAuditJournalSink.PrepareAnchored(options, NewBoot());

        Assert.False(File.Exists(abandonedPath));
        Assert.True(File.Exists(successor.SegmentPath));
    }

    [Fact]
    public void Startup_recovers_exact_lock_only_crash_with_initial_temporary()
    {
        var options = Options(NewRoot());
        InitializeStorage(options);
        var crashedBoot = NewBoot();
        var segment = CreateSegment(options, crashedBoot, 0, []);
        var persistentLock = LockPath(options, crashedBoot);
        WriteProtected(persistentLock, []);
        var temporary = Path.Combine(
            options.RootDirectory,
            AuditExportCheckpointStore.TemporaryFileName(crashedBoot, NewBoot()));
        WriteProtected(
            temporary,
            AuditExportCheckpointCodec.Serialize(
                AuditExportCheckpoint.Initial(crashedBoot)));

        using var successor = FileAuditJournalSink.PrepareAnchored(options, NewBoot());

        Assert.False(File.Exists(segment));
        Assert.False(File.Exists(persistentLock));
        Assert.False(File.Exists(temporary));
        Assert.True(File.Exists(successor.SegmentPath));
    }

    [Fact]
    public void Published_checkpoint_before_activation_remains_an_adoptable_chain()
    {
        var options = Options(NewRoot());
        var bootId = NewBoot();
        string segment;
        using (var prepared = FileAuditJournalSink.PrepareAnchored(options, bootId))
        {
            segment = prepared.SegmentPath;
            using var store = AuditExportCheckpointStore.CreateForPreparedWriter(options, prepared);
        }

        using var successor = FileAuditJournalSink.PrepareAnchored(options, NewBoot());
        Assert.True(File.Exists(segment));
        Assert.True(AuditExportCheckpointStore.TryAcquireExisting(
            options,
            bootId,
            out var adopted));
        adopted!.Dispose();
    }

    [Fact]
    public void Checkpoint_only_crash_fails_closed_without_removing_any_state()
    {
        var options = Options(NewRoot());
        InitializeStorage(options);
        var bootId = NewBoot();
        var segment = CreateSegment(options, bootId, 0, []);
        var checkpoint = CheckpointPath(options, bootId);
        WriteProtected(
            checkpoint,
            AuditExportCheckpointCodec.Serialize(AuditExportCheckpoint.Initial(bootId)));

        Assert.Throws<IOException>(() =>
            FileAuditJournalSink.PrepareAnchored(options, NewBoot()));
        Assert.True(File.Exists(segment));
        Assert.True(File.Exists(checkpoint));
    }

    [Fact]
    public void Lock_only_recovery_refuses_nonempty_segment_and_preserves_it()
    {
        var options = Options(NewRoot());
        InitializeStorage(options);
        var bootId = NewBoot();
        var bytes = Encoding.UTF8.GetBytes("evidence\n");
        var segment = CreateSegment(options, bootId, 0, bytes);
        var persistentLock = LockPath(options, bootId);
        WriteProtected(persistentLock, []);

        Assert.Throws<IOException>(() =>
            FileAuditJournalSink.PrepareAnchored(options, NewBoot()));
        Assert.Equal(bytes, File.ReadAllBytes(segment));
        Assert.True(File.Exists(persistentLock));
    }

    [Fact]
    public void Lock_only_recovery_refuses_a_live_lock_and_preserves_every_name()
    {
        var options = Options(NewRoot());
        InitializeStorage(options);
        var bootId = NewBoot();
        var segment = CreateSegment(options, bootId, 0, []);
        var persistentLock = LockPath(options, bootId);
        using var liveLock = SecureAuditStorage.CreateExclusiveFile(persistentLock);

        Assert.Throws<IOException>(() =>
            FileAuditJournalSink.PrepareAnchored(options, NewBoot()));
        Assert.True(File.Exists(segment));
        Assert.True(File.Exists(persistentLock));
    }

    [Fact]
    public void Live_bare_preparation_fails_closed_and_remains()
    {
        var options = Options(NewRoot());
        InitializeStorage(options);
        var bootId = NewBoot();
        var path = Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(bootId, 0).FileName);
        using var live = SecureAuditStorage.CreateExclusiveFile(
            path,
            options.SegmentBytes,
            FileShare.None,
            FileAccess.ReadWrite);

        Assert.Throws<IOException>(() =>
            FileAuditJournalSink.PrepareAnchored(options, NewBoot()));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Ambiguous_bare_topology_prevents_any_partial_cleanup()
    {
        var options = Options(NewRoot());
        InitializeStorage(options);
        var safe = CreateSegment(options, NewBoot(), 0, []);
        var unsafeBytes = Encoding.UTF8.GetBytes("do not delete\n");
        var unsafePath = CreateSegment(options, NewBoot(), 0, unsafeBytes);

        Assert.Throws<IOException>(() =>
            FileAuditJournalSink.PrepareAnchored(options, NewBoot()));
        Assert.True(File.Exists(safe));
        Assert.Equal(unsafeBytes, File.ReadAllBytes(unsafePath));
    }

    [Fact]
    public void Gapped_or_multiple_bare_boot_is_never_rewritten_as_a_preparation()
    {
        var options = Options(NewRoot());
        InitializeStorage(options);
        var bootId = NewBoot();
        var zero = CreateSegment(options, bootId, 0, []);
        var one = CreateSegment(options, bootId, 1, []);

        Assert.Throws<IOException>(() =>
            FileAuditJournalSink.PrepareAnchored(options, NewBoot()));
        Assert.True(File.Exists(zero));
        Assert.True(File.Exists(one));
    }

    [Fact]
    public void Unknown_spool_entry_blocks_cleanup_of_an_otherwise_safe_candidate()
    {
        var options = Options(NewRoot());
        InitializeStorage(options);
        var safe = CreateSegment(options, NewBoot(), 0, []);
        var unknown = Path.Combine(options.SpoolDirectory, "not-a-spool-entry");
        WriteProtected(unknown, []);

        Assert.Throws<IOException>(() =>
            FileAuditJournalSink.PrepareAnchored(options, NewBoot()));
        Assert.True(File.Exists(safe));
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public void Controlled_name_collision_never_deletes_the_preexisting_segment()
    {
        var options = Options(NewRoot());
        InitializeStorage(options);
        var bootId = NewBoot();
        var segment = CreateSegment(options, bootId, 0, []);
        WriteProtected(
            CheckpointPath(options, bootId),
            AuditExportCheckpointCodec.Serialize(AuditExportCheckpoint.Initial(bootId)));
        WriteProtected(LockPath(options, bootId), []);

        Assert.ThrowsAny<Exception>(() =>
            FileAuditJournalSink.PrepareAnchored(options, bootId));
        Assert.True(File.Exists(segment));
        Assert.Equal(0, new FileInfo(segment).Length);
    }

    [Fact]
    public void Preflight_inventory_is_bounded_including_the_quota_control()
    {
        var options = Options(NewRoot(), segmentBytes: 1024, aggregateBytes: 4096);
        InitializeStorage(options);
        for (var index = 0;
             index < AuditClosedSpoolChainReader.MaximumSpoolInventoryEntries;
             index++)
        {
            _ = CreateSegment(options, NewBoot(), 0, []);
        }

        var error = Assert.Throws<IOException>(() =>
            FileAuditJournalSink.PrepareAnchored(options, NewBoot()));
        Assert.Contains("bounded recovery inventory", error.Message, StringComparison.Ordinal);
        Assert.Equal(
            AuditClosedSpoolChainReader.MaximumSpoolInventoryEntries + 1,
            Directory.EnumerateFileSystemEntries(options.SpoolDirectory).Count());
    }

    [Fact]
    public void Anchored_capacity_reserves_one_segment_but_local_capacity_is_unchanged()
    {
        var root = NewRoot();
        Assert.Throws<ArgumentOutOfRangeException>(() => AuditOptions.Create(
            root,
            AuditProtectionMode.Anchored,
            ConfigurationIdentity,
            maxRecordBytes: 256,
            segmentBytes: 1024,
            aggregateBytes: 1024,
            emergencyReserveBytes: 512,
            retentionAge: TimeSpan.FromMinutes(1),
            maxEvidenceBytes: 256,
            evidenceAggregateBytes: 256,
            evidenceRetentionAge: TimeSpan.FromMinutes(1)));

        var local = AuditOptions.Create(
            root,
            maxRecordBytes: 256,
            segmentBytes: 1024,
            aggregateBytes: 1024,
            emergencyReserveBytes: 512,
            retentionAge: TimeSpan.FromMinutes(1),
            maxEvidenceBytes: 256,
            evidenceAggregateBytes: 256,
            evidenceRetentionAge: TimeSpan.FromMinutes(1));
        Assert.Equal(local.SegmentBytes, local.AggregateBytes);
    }

    [Fact]
    public void Near_full_anchored_record_capacity_still_allows_crash_restart_writer()
    {
        var options = LargeRecordOptions(NewRoot());
        var firstBoot = NewBoot();
        using (var prepared = FileAuditJournalSink.PrepareAnchored(options, firstBoot))
        using (var store = AuditExportCheckpointStore.CreateForPreparedWriter(options, prepared))
        {
            var sink = prepared.Activate(store);
            using var journal = new AuditJournal(
                options,
                new AuditHealth(options),
                sink,
                "restart-bound-test",
                binaryDigest: null,
                NewBoot(),
                firstBoot);
            var appended = 0;
            while (journal.TryReserve(1, out var reservation, out _))
            {
                using (reservation)
                    _ = journal.Append(reservation!, AnchoredInput("call.completed"));
                appended++;
                Assert.True(appended < 100);
            }
            Assert.True(appended > 0);
            Assert.True(sink.TotalBytes <= options.AggregateBytes - options.SegmentBytes);
        }

        var restartedBoot = NewBoot();
        using var restarted = FileAuditJournalSink.PrepareAnchored(options, restartedBoot);
        Assert.Equal(0, new FileInfo(restarted.SegmentPath).Length);
        using var restartedStore = AuditExportCheckpointStore.CreateForPreparedWriter(
            options,
            restarted);
        var restartedSink = restarted.Activate(restartedStore);
        using var restartedJournal = new AuditJournal(
            options,
            new AuditHealth(options),
            restartedSink,
            "restart-bound-test",
            binaryDigest: null,
            NewBoot(),
            restartedBoot);
        Assert.False(restartedJournal.TryReserve(1, out var refused, out _));
        Assert.Null(refused);
    }

    [Fact]
    public void Concurrent_fresh_writer_cannot_consume_the_restart_segment()
    {
        var options = Options(NewRoot(), segmentBytes: 4096, aggregateBytes: 8192);
        using var prepared = FileAuditJournalSink.PrepareAnchored(options, NewBoot());
        using var store = AuditExportCheckpointStore.CreateForPreparedWriter(options, prepared);
        using var sink = prepared.Activate(store);

        Assert.Throws<IOException>(() =>
            FileAuditJournalSink.PrepareAnchored(options, NewBoot()));
        Assert.Equal(0, new FileInfo(sink.CurrentSegmentPath).Length);
    }

    [Fact]
    public void Rotation_cannot_consume_the_last_physical_restart_segment()
    {
        var options = RotationHeadroomOptions(NewRoot());
        using var prepared = FileAuditJournalSink.PrepareAnchored(options, NewBoot());
        using var store = AuditExportCheckpointStore.CreateForPreparedWriter(options, prepared);
        using var sink = prepared.Activate(store);
        var line = new byte[6000];
        line[^1] = (byte)'\n';

        for (var index = 0; index < 4; index++)
        {
            Assert.True(sink.CanReserve(line.Length));
            sink.Append(line);
            sink.FlushToDisk();
        }

        Assert.Equal(1, sink.CurrentSegmentIdentity.Index);
        Assert.Throws<IOException>(() => sink.CanReserve(line.Length));
        Assert.False(File.Exists(Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(
                sink.CurrentSegmentIdentity.SupervisorBootId,
                2).FileName)));
        Assert.True(options.AggregateBytes - sink.TotalBytes >= options.SegmentBytes);
        Assert.True(
            options.AggregateBytes - sink.PhysicalCommittedBytesForTests >=
            options.SegmentBytes);
    }

    private AuditOptions Options(
        string root,
        long segmentBytes = 16_384,
        long aggregateBytes = 65_536)
    {
        return AuditOptions.Create(
            root,
            AuditProtectionMode.Anchored,
            ConfigurationIdentity,
            maxRecordBytes: 256,
            segmentBytes: segmentBytes,
            aggregateBytes: aggregateBytes,
            emergencyReserveBytes: 512,
            retentionAge: TimeSpan.FromMinutes(1),
            maxEvidenceBytes: 256,
            evidenceAggregateBytes: 256,
            evidenceRetentionAge: TimeSpan.FromMinutes(1));
    }

    private AuditOptions LargeRecordOptions(string root) => AuditOptions.Create(
        root,
        AuditProtectionMode.Anchored,
        ConfigurationIdentity,
        maxRecordBytes: 4096,
        segmentBytes: 16_384,
        aggregateBytes: 32_768,
        emergencyReserveBytes: 8192,
        retentionAge: TimeSpan.FromMinutes(1),
        maxEvidenceBytes: 4096,
        evidenceAggregateBytes: 4096,
        evidenceRetentionAge: TimeSpan.FromMinutes(1));

    private AuditOptions RotationHeadroomOptions(string root) => AuditOptions.Create(
        root,
        AuditProtectionMode.Anchored,
        ConfigurationIdentity,
        maxRecordBytes: 6000,
        segmentBytes: 16_384,
        aggregateBytes: 49_152,
        emergencyReserveBytes: 12_000,
        retentionAge: TimeSpan.FromMinutes(1),
        maxEvidenceBytes: 6000,
        evidenceAggregateBytes: 6000,
        evidenceRetentionAge: TimeSpan.FromMinutes(1));

    private static AuditEventInput AnchoredInput(string eventType) => new()
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

    private static void InitializeStorage(AuditOptions options)
    {
        _ = SecureAuditStorage.PrepareRoot(options.RootDirectory);
        _ = SecureAuditStorage.PrepareRoot(options.SpoolDirectory);
        using var quota = AuditSpoolQuotaLease.CreateControlAndAcquire(options.SpoolDirectory);
    }

    private static string CreateSegment(
        AuditOptions options,
        Guid bootId,
        int index,
        ReadOnlySpan<byte> bytes)
    {
        var path = Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(bootId, index).FileName);
        WriteProtected(path, bytes);
        return path;
    }

    private static void WriteProtected(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = SecureAuditStorage.CreateExclusiveFile(path);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static string CheckpointPath(AuditOptions options, Guid bootId) =>
        Path.Combine(
            options.RootDirectory,
            AuditExportCheckpointStore.CheckpointFileName(bootId));

    private static string LockPath(AuditOptions options, Guid bootId) =>
        Path.Combine(
            options.RootDirectory,
            AuditExportCheckpointStore.LockFileName(bootId));

    private string NewRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-anchored-writer-tests-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private static Guid NewBoot()
    {
        while (true)
        {
            var candidate = Guid.NewGuid();
            if (AuditSpoolSegmentIdentity.IsUuidV4(candidate))
                return candidate;
        }
    }
}
