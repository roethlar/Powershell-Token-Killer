using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditExportCheckpointStoreTests : IDisposable
{
    private static readonly Guid BootId =
        Guid.Parse("22345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid OtherBootId =
        Guid.Parse("32345678-1234-4abc-8def-0123456789ab");
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
    public void CreateForWriter_publishes_exact_initial_checkpoint_and_persistent_lock()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        string checkpointPath;
        string lockPath;

        using (var store = AuditExportCheckpointStore.CreateForWriter(options, BootId))
        {
            checkpointPath = store.CheckpointPath;
            lockPath = store.LockPath;
            Assert.Equal(
                $"export.checkpoint-{BootId:N}.json",
                Path.GetFileName(checkpointPath));
            Assert.Equal(
                $"export.checkpoint-{BootId:N}.lock",
                Path.GetFileName(lockPath));
            AssertCheckpoint(store.Current, sequence: 0, chainComplete: false);
            Assert.Equal(
                AuditExportCheckpointCodec.Serialize(AuditExportCheckpoint.Initial(BootId)),
                File.ReadAllBytes(checkpointPath));
            Assert.Equal(0, new FileInfo(lockPath).Length);
            SecureAuditStorage.VerifyExternalProtectedFile(checkpointPath);
            SecureAuditStorage.VerifyExternalProtectedFile(lockPath);
            CreateSameBootSegment(options, store);
        }

        Assert.True(File.Exists(lockPath));
        using var restarted = ReopenExisting(options);
        AssertCheckpoint(restarted.Current, sequence: 0, chainComplete: false);
        Assert.True(File.Exists(lockPath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(options.RootDirectory),
            path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Competing_lease_fails_until_owner_disposes_without_deleting_lock()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        var first = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var lockPath = first.LockPath;
        CreateSameBootSegment(options, first);
        try
        {
            Assert.False(AuditExportCheckpointStore.TryAcquireExisting(
                options,
                BootId,
                out var competing));
            Assert.Null(competing);
            Assert.True(File.Exists(lockPath));
        }
        finally
        {
            first.Dispose();
        }

        using var restarted = ReopenExisting(options);
        Assert.True(File.Exists(lockPath));
        AssertCheckpoint(restarted.Current, sequence: 0, chainComplete: false);
    }

    [Fact]
    public void Existing_chain_acquisition_reports_only_an_active_owner_as_busy()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var owner = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        using var sink = new FileAuditJournalSink(
            options,
            BootId,
            checkpointStore: owner);
        var checkpointBytes = File.ReadAllBytes(owner.CheckpointPath);

        Assert.False(AuditExportCheckpointStore.TryAcquireExisting(
            options,
            BootId,
            out var competing));
        Assert.Null(competing);
        Assert.Equal(checkpointBytes, File.ReadAllBytes(owner.CheckpointPath));
        Assert.Equal(0, new FileInfo(owner.LockPath).Length);
    }

    [Fact]
    public void Existing_chain_acquisition_reopens_without_creating_control_state()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        string checkpointPath;
        string lockPath;
        using (var owner = AuditExportCheckpointStore.CreateForWriter(options, BootId))
        {
            checkpointPath = owner.CheckpointPath;
            lockPath = owner.LockPath;
            using var sink = new FileAuditJournalSink(
                options,
                BootId,
                checkpointStore: owner);
        }

        var checkpointTimestamp = File.GetLastWriteTimeUtc(checkpointPath);
        var lockTimestamp = File.GetLastWriteTimeUtc(lockPath);
        Assert.True(AuditExportCheckpointStore.TryAcquireExisting(
            options,
            BootId,
            out var reopened));
        using (reopened)
        {
            Assert.NotNull(reopened);
            AssertCheckpoint(reopened.Current, sequence: 0, chainComplete: false);
        }
        Assert.Equal(checkpointTimestamp, File.GetLastWriteTimeUtc(checkpointPath));
        Assert.Equal(lockTimestamp, File.GetLastWriteTimeUtc(lockPath));
    }

    [Fact]
    public void Writer_creation_never_adopts_an_existing_boot_chain()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using (var owner = AuditExportCheckpointStore.CreateForWriter(options, BootId))
        {
            using var sink = new FileAuditJournalSink(
                options,
                BootId,
                checkpointStore: owner);
        }

        Assert.Throws<IOException>(() =>
            AuditExportCheckpointStore.CreateForWriter(options, BootId));
        Assert.True(AuditExportCheckpointStore.TryAcquireExisting(
            options,
            BootId,
            out var adopted));
        adopted!.Dispose();
    }

    [Fact]
    public void Existing_chain_acquisition_never_creates_missing_control_state()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        SecureAuditStorage.PrepareRoot(options.RootDirectory);
        SecureAuditStorage.PrepareRoot(options.SpoolDirectory);
        var segment = Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(BootId, 0).FileName);
        using (SecureAuditStorage.CreateExclusiveFile(segment))
        {
        }
        var checkpointPath = Path.Combine(
            options.RootDirectory,
            AuditExportCheckpointStore.CheckpointFileName(BootId));
        var lockPath = Path.Combine(
            options.RootDirectory,
            AuditExportCheckpointStore.LockFileName(BootId));
        var checkpointBytes = AuditExportCheckpointCodec.Serialize(
            AuditExportCheckpoint.Initial(BootId));
        WriteProtected(checkpointPath, checkpointBytes);

        Assert.Throws<IOException>(() =>
            AuditExportCheckpointStore.TryAcquireExisting(options, BootId, out _));
        Assert.Equal(checkpointBytes, File.ReadAllBytes(checkpointPath));
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void Existing_chain_acquisition_treats_corrupt_checkpoint_as_fault_not_busy()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        string checkpointPath;
        using (var owner = AuditExportCheckpointStore.CreateForWriter(options, BootId))
        {
            checkpointPath = owner.CheckpointPath;
            using var sink = new FileAuditJournalSink(
                options,
                BootId,
                checkpointStore: owner);
        }
        File.WriteAllText(checkpointPath, "not a checkpoint");

        Assert.Throws<IOException>(() =>
            AuditExportCheckpointStore.TryAcquireExisting(options, BootId, out _));
    }

    [Fact]
    public void Anchored_sink_refuses_to_publish_a_segment_before_fresh_checkpoint()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);

        Assert.Throws<InvalidOperationException>(() =>
            new FileAuditJournalSink(options, BootId));
        Assert.Empty(Directory.EnumerateFiles(options.SpoolDirectory, "*.jsonl"));

        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        Assert.True(File.Exists(store.CheckpointPath));
        Assert.Throws<InvalidOperationException>(() =>
            new FileAuditJournalSink(options, BootId));
        using var sink = new FileAuditJournalSink(
            options,
            BootId,
            checkpointStore: store);
        Assert.Equal(
            AuditSpoolSegmentIdentity.Create(BootId, 0).FileName,
            Path.GetFileName(sink.CurrentSegmentPath));
    }

    [Fact]
    public void Anchored_sink_retains_checkpoint_ownership_until_writer_closes()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var sink = new FileAuditJournalSink(
            options,
            BootId,
            checkpointStore: store);
        try
        {
            store.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _ = store.Current);
            Assert.False(AuditExportCheckpointStore.TryAcquireExisting(
                options,
                BootId,
                out var competing));
            Assert.Null(competing);
        }
        finally
        {
            sink.Dispose();
            store.Dispose();
        }

        using var adopted = ReopenExisting(options);
        Assert.Equal(BootId, adopted.SupervisorBootId);
    }

    [Fact]
    public void Disposed_checkpoint_owner_cannot_authorize_an_anchored_writer()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        store.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            new FileAuditJournalSink(
                options,
                BootId,
                checkpointStore: store));

        Assert.Empty(Directory.EnumerateFiles(options.SpoolDirectory, "*.jsonl"));
    }

    [Fact]
    public async Task Save_holds_the_instance_and_cross_process_lease_through_transition()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        CreateSameBootSegment(options, store);
        using var transitionReached = new ManualResetEventSlim();
        using var releaseTransition = new ManualResetEventSlim();
        var save = Task.Run(() => store.SaveForTests(
            Checkpoint(BootId, sequence: 1, byteOffset: 128),
            beforeAtomicReplaceForTests: () =>
            {
                transitionReached.Set();
                if (!releaseTransition.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The checkpoint transition test was not released.");
            }));
        Assert.True(transitionReached.Wait(TimeSpan.FromSeconds(10)));
        var dispose = Task.Run(store.Dispose);
        var observe = Task.Run(() => store.Current);
        var disposeCompletedEarly = false;
        var observeCompletedEarly = false;
        var competingOwnerAcquired = false;
        try
        {
            disposeCompletedEarly = ReferenceEquals(
                dispose,
                await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromMilliseconds(250))));
            observeCompletedEarly = ReferenceEquals(
                observe,
                await Task.WhenAny(observe, Task.Delay(TimeSpan.FromMilliseconds(250))));
            competingOwnerAcquired = AuditExportCheckpointStore.TryAcquireExisting(
                options,
                BootId,
                out var competing);
            competing?.Dispose();
        }
        finally
        {
            releaseTransition.Set();
        }

        await save.WaitAsync(TimeSpan.FromSeconds(10));
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            _ = await observe.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (ObjectDisposedException)
        {
            // Dispose may acquire the gate before the queued observer.
        }
        Assert.False(disposeCompletedEarly);
        Assert.False(observeCompletedEarly);
        Assert.False(competingOwnerAcquired);

        using var successor = ReopenExisting(options);
        Assert.Equal(1, successor.Current.Sequence);
        successor.SaveForTests(Checkpoint(BootId, sequence: 2, byteOffset: 256));
        Assert.Equal(2, successor.Current.Sequence);
    }

    [Fact]
    public void Missing_checkpoint_beside_same_boot_segments_fails_closed()
    {
        var root = NewRoot();
        var localOptions = Options(root, AuditProtectionMode.LocalOnly);
        string segmentPath;
        using (var sink = new FileAuditJournalSink(localOptions, BootId))
            segmentPath = sink.CurrentSegmentPath;
        var anchoredOptions = Options(root, AuditProtectionMode.Anchored);
        var checkpointPath = Path.Combine(
            root,
            AuditExportCheckpointStore.CheckpointFileName(BootId));

        Assert.Throws<IOException>(() =>
            AuditExportCheckpointStore.TryAcquireExisting(
                anchoredOptions,
                BootId,
                out _));

        Assert.False(File.Exists(checkpointPath));
        Assert.True(File.Exists(segmentPath));
        Assert.False(File.Exists(Path.Combine(
            root,
            AuditExportCheckpointStore.LockFileName(BootId))));
    }

    [Fact]
    public void Existing_checkpoint_tamper_is_rejected_without_rewriting_bytes()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        string checkpointPath;
        using (var store = AuditExportCheckpointStore.CreateForWriter(options, BootId))
        {
            checkpointPath = store.CheckpointPath;
            CreateSameBootSegment(options, store);
        }
        var tampered = "{\"not\":\"a checkpoint\"}\n"u8.ToArray();
        File.WriteAllBytes(checkpointPath, tampered);

        Assert.Throws<IOException>(() =>
            AuditExportCheckpointStore.ReadSnapshot(options, BootId));
        Assert.Throws<IOException>(() =>
            AuditExportCheckpointStore.TryAcquireExisting(options, BootId, out _));

        Assert.Equal(tampered, File.ReadAllBytes(checkpointPath));
    }

    [Fact]
    public void Live_store_never_overwrites_checkpoint_tampering()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var tampered = AuditExportCheckpointCodec.Serialize(
            Checkpoint(BootId, sequence: 2, byteOffset: 256));
        File.WriteAllBytes(store.CheckpointPath, tampered);

        Assert.Throws<IOException>(() => store.SaveForTests(
            Checkpoint(BootId, sequence: 1, byteOffset: 128)));

        Assert.Equal(tampered, File.ReadAllBytes(store.CheckpointPath));
        Assert.Throws<IOException>(() => _ = store.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Live_store_faults_after_a_strict_checkpoint_read_failure(bool deleteCheckpoint)
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var malformed = "{\"not\":\"a checkpoint\"}\n"u8.ToArray();
        if (deleteCheckpoint)
            File.Delete(store.CheckpointPath);
        else
            File.WriteAllBytes(store.CheckpointPath, malformed);

        Assert.Throws<IOException>(() => store.SaveForTests(
            Checkpoint(BootId, sequence: 1, byteOffset: 128)));

        Assert.Throws<IOException>(() => _ = store.Current);
        if (deleteCheckpoint)
            Assert.False(File.Exists(store.CheckpointPath));
        else
            Assert.Equal(malformed, File.ReadAllBytes(store.CheckpointPath));
    }

    [Fact]
    public void Live_store_faults_after_its_lease_name_disappears()
    {
        if (OperatingSystem.IsWindows()) return;
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        File.Delete(store.LockPath);

        Assert.Throws<IOException>(() => store.SaveForTests(
            Checkpoint(BootId, sequence: 1, byteOffset: 128)));

        Assert.Throws<IOException>(() => _ = store.Current);
        Assert.False(File.Exists(store.LockPath));
    }

    [Fact]
    public void Live_store_rejects_same_name_lease_replacement_before_checkpoint_mutation()
    {
        if (OperatingSystem.IsWindows()) return;
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var checkpointBefore = File.ReadAllBytes(store.CheckpointPath);
        ReplaceLeaseName(store);

        Assert.Throws<IOException>(() => store.SaveForTests(
            Checkpoint(BootId, sequence: 1, byteOffset: 128)));

        Assert.Equal(checkpointBefore, File.ReadAllBytes(store.CheckpointPath));
        Assert.Throws<IOException>(() => _ = store.Current);
    }

    [Fact]
    public void Lease_replacement_during_checkpoint_transition_faults_the_store()
    {
        if (OperatingSystem.IsWindows()) return;
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var intended = Checkpoint(BootId, sequence: 1, byteOffset: 128);

        Assert.Throws<IOException>(() => store.SaveForTests(
            intended,
            beforeAtomicReplaceForTests: () => ReplaceLeaseName(store)));

        Assert.Equal(
            AuditExportCheckpointCodec.Serialize(intended),
            File.ReadAllBytes(store.CheckpointPath));
        Assert.Throws<IOException>(() => _ = store.Current);
    }

    [Fact]
    public void Post_replace_strict_reload_failure_faults_the_live_store()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var malformed = "{\"not\":\"a checkpoint\"}\n"u8.ToArray();

        Assert.Throws<IOException>(() => store.SaveForTests(
            Checkpoint(BootId, sequence: 1, byteOffset: 128),
            afterAtomicReplaceForTests: () =>
                File.WriteAllBytes(store.CheckpointPath, malformed)));

        Assert.Equal(malformed, File.ReadAllBytes(store.CheckpointPath));
        Assert.Throws<IOException>(() => _ = store.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Protected_reads_reject_acl_or_mode_sabotage_without_repairing_it(
        bool sabotageParent)
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        string checkpointPath;
        using (var store = AuditExportCheckpointStore.CreateForWriter(options, BootId))
            checkpointPath = store.CheckpointPath;
        var sabotagedPath = sabotageParent ? options.RootDirectory : checkpointPath;
        AddUnprotectedAccess(sabotagedPath, sabotageParent);
        var protectionBefore = ProtectionFingerprint(sabotagedPath, sabotageParent);

        Assert.Throws<IOException>(() =>
            AuditExportCheckpointStore.ReadSnapshot(options, BootId));

        Assert.Equal(
            protectionBefore,
            ProtectionFingerprint(sabotagedPath, sabotageParent));
    }

    [Fact]
    public void Checkpoint_symlink_is_rejected_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        string checkpointPath;
        byte[] bytes;
        using (var store = AuditExportCheckpointStore.CreateForWriter(options, BootId))
        {
            checkpointPath = store.CheckpointPath;
            bytes = File.ReadAllBytes(checkpointPath);
        }

        var target = Path.Combine(options.RootDirectory, "target-checkpoint.json");
        WriteProtected(target, bytes);
        File.Delete(checkpointPath);
        File.CreateSymbolicLink(checkpointPath, target);

        Assert.ThrowsAny<IOException>(() =>
            AuditExportCheckpointStore.ReadSnapshot(options, BootId));
        Assert.True(File.ResolveLinkTarget(checkpointPath, returnFinalTarget: false) is not null);
    }

    [Fact]
    public void Save_rejects_cursor_regression_rewrite_and_cross_boot_transition()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var acknowledged = Checkpoint(BootId, sequence: 1, byteOffset: 128);
        store.SaveForTests(acknowledged);
        var exactBytes = File.ReadAllBytes(store.CheckpointPath);

        Assert.Throws<ArgumentException>(() =>
            store.SaveForTests(AuditExportCheckpoint.Initial(BootId)));
        Assert.Throws<ArgumentException>(() =>
            store.SaveForTests(AuditExportCheckpoint.Initial(OtherBootId)));
        Assert.Throws<ArgumentException>(() =>
            store.SaveForTests(Checkpoint(BootId, sequence: 1, byteOffset: 256)));

        Assert.Equal(exactBytes, File.ReadAllBytes(store.CheckpointPath));
        AssertCheckpoint(store.Current, sequence: 1, chainComplete: false);

        var complete = Checkpoint(
            BootId,
            sequence: 1,
            byteOffset: 128,
            acknowledgedEventId: acknowledged.AcknowledgedEventId,
            chainComplete: true);
        store.SaveForTests(complete);
        Assert.Throws<ArgumentException>(() => store.SaveForTests(acknowledged));
        AssertCheckpoint(store.Current, sequence: 1, chainComplete: true);
    }

    [Fact]
    public void Save_advances_exactly_one_record_without_skipping_segments()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        Assert.Throws<ArgumentException>(() =>
            store.SaveForTests(Checkpoint(BootId, sequence: 2, byteOffset: 256)));

        var first = Checkpoint(BootId, sequence: 1, byteOffset: 128);
        store.SaveForTests(first);
        Assert.Throws<ArgumentException>(() =>
            store.SaveForTests(Checkpoint(BootId, sequence: 3, byteOffset: 384)));
        Assert.Throws<ArgumentException>(() =>
            store.SaveForTests(new AuditExportCheckpoint(
                BootId,
                false,
                AuditSpoolSegmentIdentity.Create(BootId, 2),
                64,
                2,
                Guid.CreateVersion7(),
                null)));

        var secondSegment = new AuditExportCheckpoint(
            BootId,
            false,
            AuditSpoolSegmentIdentity.Create(BootId, 1),
            64,
            2,
            Guid.CreateVersion7(),
            null);
        store.SaveForTests(secondSegment);

        Assert.Equal(2, store.Current.Sequence);
        Assert.Equal(1, store.Current.Spool?.Index);
    }

    [Fact]
    public void Save_clears_a_block_only_by_advancing_its_exact_record()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var acknowledged = Checkpoint(BootId, sequence: 1, byteOffset: 128);
        store.SaveForTests(acknowledged);
        var blockedEventId = Guid.CreateVersion7();
        var blocked = new AuditExportBlockedRecord(
            AuditSpoolSegmentIdentity.Create(BootId, 0),
            128,
            2,
            blockedEventId,
            AuditExportFailureClass.Data,
            "http.400",
            responseDigest: null,
            new DateTimeOffset(2026, 7, 11, 12, 34, 56, TimeSpan.Zero),
            ConfigurationIdentity);
        var blockedCheckpoint = new AuditExportCheckpoint(
            BootId,
            false,
            acknowledged.Spool,
            acknowledged.ByteOffset,
            acknowledged.Sequence,
            acknowledged.AcknowledgedEventId,
            blocked);
        store.SaveForTests(blockedCheckpoint);

        Assert.Throws<ArgumentException>(() => store.SaveForTests(acknowledged));
        Assert.Throws<ArgumentException>(() => store.SaveForTests(new AuditExportCheckpoint(
            BootId,
            false,
            blocked.Spool,
            256,
            2,
            Guid.CreateVersion7(),
            null)));

        var disposition = new AuditExportCheckpoint(
            BootId,
            false,
            blocked.Spool,
            256,
            2,
            blocked.EventId,
            null);
        store.SaveForTests(disposition);

        Assert.Equal(2, store.Current.Sequence);
        Assert.Equal(blocked.EventId, store.Current.AcknowledgedEventId);
        Assert.Null(store.Current.BlockedRecord);
    }

    [Fact]
    public void Configuration_block_reclassifies_after_each_changed_identity_then_freezes()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var blockedEventId = Guid.CreateVersion7();
        var first = new AuditExportBlockedRecord(
            AuditSpoolSegmentIdentity.Create(BootId, 0),
            0,
            1,
            blockedEventId,
            AuditExportFailureClass.Configuration,
            "http.401",
            responseDigest: null,
            new DateTimeOffset(2026, 7, 11, 12, 34, 56, TimeSpan.Zero),
            ConfigurationIdentity);
        store.SaveForTests(new AuditExportCheckpoint(BootId, false, null, 0, 0, null, first));

        var changedIdentity = new string('b', 64);
        var retried = new AuditExportBlockedRecord(
            first.Spool,
            first.ByteOffset,
            first.Sequence,
            first.EventId,
            AuditExportFailureClass.Configuration,
            "tls.validation",
            responseDigest: null,
            first.FirstFailureUtc.AddMinutes(1),
            changedIdentity);
        store.SaveForTests(new AuditExportCheckpoint(BootId, false, null, 0, 0, null, retried));

        Assert.Equal(changedIdentity, store.Current.BlockedRecord?.ExportConfigurationIdentity);
        var sameIdentityPermanent = new AuditExportBlockedRecord(
            retried.Spool,
            retried.ByteOffset,
            retried.Sequence,
            retried.EventId,
            AuditExportFailureClass.Protocol,
            "http.500",
            responseDigest: null,
            retried.FirstFailureUtc,
            changedIdentity);
        Assert.Throws<ArgumentException>(() => store.SaveForTests(
            new AuditExportCheckpoint(
                BootId,
                false,
                null,
                0,
                0,
                null,
                sameIdentityPermanent)));

        var finalIdentity = new string('c', 64);
        var permanent = new AuditExportBlockedRecord(
            retried.Spool,
            retried.ByteOffset,
            retried.Sequence,
            retried.EventId,
            AuditExportFailureClass.Protocol,
            "http.500",
            responseDigest: null,
            retried.FirstFailureUtc,
            finalIdentity);
        store.SaveForTests(new AuditExportCheckpoint(
            BootId,
            false,
            null,
            0,
            0,
            null,
            permanent));

        Assert.Equal(AuditExportFailureClass.Protocol, store.Current.BlockedRecord?.FailureClass);
        Assert.Equal(finalIdentity, store.Current.BlockedRecord?.ExportConfigurationIdentity);
        var rewrittenPermanent = new AuditExportBlockedRecord(
            permanent.Spool,
            permanent.ByteOffset,
            permanent.Sequence,
            permanent.EventId,
            AuditExportFailureClass.Data,
            "http.400",
            responseDigest: null,
            permanent.FirstFailureUtc,
            new string('d', 64));
        Assert.Throws<ArgumentException>(() => store.SaveForTests(new AuditExportCheckpoint(
            BootId,
            false,
            null,
            0,
            0,
            null,
            rewrittenPermanent)));
    }

    [Fact]
    public void Failed_replace_that_left_exact_prior_state_rethrows_exact_failure()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var priorBytes = File.ReadAllBytes(store.CheckpointPath);
        var injected = new InjectedCheckpointException("before replace");

        var caught = Assert.Throws<InjectedCheckpointException>(() =>
            store.SaveForTests(
                Checkpoint(BootId, sequence: 1, byteOffset: 128),
                beforeAtomicReplaceForTests: () => throw injected));

        Assert.Same(injected, caught);
        Assert.Equal(priorBytes, File.ReadAllBytes(store.CheckpointPath));
        AssertCheckpoint(store.Current, sequence: 0, chainComplete: false);
    }

    [Fact]
    public void Post_replace_uncertainty_accepts_the_exact_intended_state()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var intended = Checkpoint(BootId, sequence: 1, byteOffset: 128);

        store.SaveForTests(
            intended,
            destinationReplacedForTests: () =>
                throw new InjectedCheckpointException("after replace"));

        Assert.Equal(
            AuditExportCheckpointCodec.Serialize(intended),
            File.ReadAllBytes(store.CheckpointPath));
        AssertCheckpoint(store.Current, sequence: 1, chainComplete: false);
    }

    [Fact]
    public void Lease_replacement_during_uncertain_replace_faults_the_store()
    {
        if (OperatingSystem.IsWindows()) return;
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var intended = Checkpoint(BootId, sequence: 1, byteOffset: 128);

        Assert.Throws<IOException>(() => store.SaveForTests(
            intended,
            destinationReplacedForTests: () =>
            {
                ReplaceLeaseName(store);
                throw new InjectedCheckpointException("after replace");
            }));

        Assert.Equal(
            AuditExportCheckpointCodec.Serialize(intended),
            File.ReadAllBytes(store.CheckpointPath));
        Assert.Throws<IOException>(() => _ = store.Current);
    }

    [Fact]
    public void Intended_bytes_without_a_confirmed_replace_fault_the_store()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var intended = Checkpoint(BootId, sequence: 1, byteOffset: 128);
        var intendedBytes = AuditExportCheckpointCodec.Serialize(intended);
        var injected = new InjectedCheckpointException("before replace");

        var exception = Assert.Throws<IOException>(() => store.SaveForTests(
            intended,
            beforeAtomicReplaceForTests: () =>
            {
                File.WriteAllBytes(store.CheckpointPath, intendedBytes);
                throw injected;
            }));

        Assert.Contains("without a confirmed atomic replacement", exception.Message);
        Assert.Equal(intendedBytes, File.ReadAllBytes(store.CheckpointPath));
        Assert.Throws<IOException>(() => _ = store.Current);
    }

    [Fact]
    public void Confirmed_replace_that_reloads_prior_bytes_faults_the_store()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var priorBytes = File.ReadAllBytes(store.CheckpointPath);
        var intended = Checkpoint(BootId, sequence: 1, byteOffset: 128);
        var injected = new InjectedCheckpointException("after replace");

        var exception = Assert.Throws<IOException>(() => store.SaveForTests(
            intended,
            destinationReplacedForTests: () =>
            {
                File.WriteAllBytes(store.CheckpointPath, priorBytes);
                throw injected;
            }));

        Assert.Contains("reverted after a confirmed atomic replacement", exception.Message);
        Assert.Equal(priorBytes, File.ReadAllBytes(store.CheckpointPath));
        Assert.Throws<IOException>(() => _ = store.Current);
    }

    [Fact]
    public void Failed_post_replace_durability_confirmation_faults_the_store()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var intended = Checkpoint(BootId, sequence: 1, byteOffset: 128);

        var exception = Assert.Throws<IOException>(() => store.SaveForTests(
            intended,
            destinationReplacedForTests: () =>
                throw new InjectedCheckpointException("after replace"),
            beforeDurabilityConfirmationForTests: () =>
                File.Delete(store.CheckpointPath)));

        Assert.Contains("durability could not be confirmed", exception.Message);
        Assert.False(File.Exists(store.CheckpointPath));
        Assert.Throws<IOException>(() => _ = store.Current);
    }

    [Fact]
    public void Failure_after_the_recovery_safe_replace_seam_faults_without_fsync_retry()
    {
        if (OperatingSystem.IsWindows()) return;
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var intended = Checkpoint(BootId, sequence: 1, byteOffset: 128);
        var intendedBytes = AuditExportCheckpointCodec.Serialize(intended);

        var exception = Assert.Throws<IOException>(() => store.SaveForTests(
            intended,
            directoryFlushStartingForTests: () =>
                throw new InjectedCheckpointException("directory fsync")));

        Assert.Contains("after its recovery-safe commit seam", exception.Message);
        Assert.Equal(intendedBytes, File.ReadAllBytes(store.CheckpointPath));
        Assert.Throws<IOException>(() => _ = store.Current);
    }

    [Fact]
    public void Uncertain_replace_with_neither_prior_nor_intended_state_faults_store()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var unexpected = Checkpoint(BootId, sequence: 2, byteOffset: 256);

        Assert.Throws<IOException>(() => store.SaveForTests(
            Checkpoint(BootId, sequence: 1, byteOffset: 128),
            destinationReplacedForTests: () =>
            {
                File.WriteAllBytes(
                    store.CheckpointPath,
                    AuditExportCheckpointCodec.Serialize(unexpected));
                throw new InjectedCheckpointException("after corrupt replacement");
            }));

        Assert.Throws<IOException>(() => _ = store.Current);
        Assert.Equal(
            AuditExportCheckpointCodec.Serialize(unexpected),
            File.ReadAllBytes(store.CheckpointPath));
    }

    [Fact]
    public async Task Concurrent_readers_observe_only_complete_atomic_checkpoints()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        var failures = new ConcurrentQueue<Exception>();
        var observations = 0;
        var firstObservation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var observed = AuditExportCheckpointStore.ReadSnapshot(options, BootId);
                    Assert.InRange(observed.Sequence, 0, 24);
                    Interlocked.Increment(ref observations);
                    firstObservation.TrySetResult();
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            }
        });

        try
        {
            await firstObservation.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (var sequence = 1; sequence <= 24; sequence++)
            {
                store.SaveForTests(Checkpoint(
                    BootId,
                    sequence,
                    checked(sequence * 128L)));
            }
        }
        finally
        {
            stop.Cancel();
            await reader.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Empty(failures);
        Assert.True(Volatile.Read(ref observations) > 24);
        AssertCheckpoint(store.Current, sequence: 24, chainComplete: false);
    }

    [Fact]
    public void Restart_removes_only_canonical_protected_stale_temporary_file()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        byte[] originalBytes;
        using (var first = AuditExportCheckpointStore.CreateForWriter(options, BootId))
        {
            originalBytes = File.ReadAllBytes(first.CheckpointPath);
            CreateSameBootSegment(options, first);
        }
        var staleName = AuditExportCheckpointStore.TemporaryFileName(
            BootId,
            Guid.Parse("42345678-1234-4abc-8def-0123456789ab"));
        var stalePath = Path.Combine(options.RootDirectory, staleName);
        WriteProtected(
            stalePath,
            AuditExportCheckpointCodec.Serialize(
                Checkpoint(BootId, sequence: 1, byteOffset: 128)));

        using var restarted = ReopenExisting(options);

        Assert.False(File.Exists(stalePath));
        Assert.Equal(originalBytes, File.ReadAllBytes(restarted.CheckpointPath));
        AssertCheckpoint(restarted.Current, sequence: 0, chainComplete: false);
    }

    [Fact]
    public void Sabotaged_persistent_lock_is_rejected_without_repair()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        string lockPath;
        using (var store = AuditExportCheckpointStore.CreateForWriter(options, BootId))
        {
            lockPath = store.LockPath;
            CreateSameBootSegment(options, store);
        }
        AddUnprotectedAccess(lockPath, isDirectory: false);
        var protectionBefore = ProtectionFingerprint(lockPath, isDirectory: false);

        Assert.Throws<IOException>(() =>
            AuditExportCheckpointStore.TryAcquireExisting(options, BootId, out _));
        Assert.Throws<IOException>(() =>
            AuditExportCheckpointStore.ReadSnapshot(options, BootId));

        Assert.Equal(
            protectionBefore,
            ProtectionFingerprint(lockPath, isDirectory: false));
        Assert.True(File.Exists(lockPath));
    }

    private static void CreateSameBootSegment(
        AuditOptions options,
        AuditExportCheckpointStore owner)
    {
        using var sink = new FileAuditJournalSink(
            options,
            BootId,
            checkpointStore: owner);
    }

    private static AuditExportCheckpointStore ReopenExisting(AuditOptions options)
    {
        Assert.True(AuditExportCheckpointStore.TryAcquireExisting(
            options,
            BootId,
            out var store));
        return Assert.IsType<AuditExportCheckpointStore>(store);
    }

    private AuditOptions Options(string root, AuditProtectionMode protectionMode)
    {
        const int maxRecordBytes = 4096;
        return AuditOptions.Create(
            root,
            protectionMode,
            protectionMode == AuditProtectionMode.Anchored
                ? ConfigurationIdentity
                : null,
            maxRecordBytes: maxRecordBytes,
            segmentBytes: maxRecordBytes * 4L,
            aggregateBytes: maxRecordBytes * 16L,
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
            ".ptk-checkpoint-store-tests-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private static AuditExportCheckpoint Checkpoint(
        Guid bootId,
        long sequence,
        long byteOffset,
        Guid? acknowledgedEventId = null,
        bool chainComplete = false)
    {
        return new AuditExportCheckpoint(
            bootId,
            chainComplete,
            AuditSpoolSegmentIdentity.Create(bootId, 0),
            byteOffset,
            sequence,
            acknowledgedEventId ?? Guid.CreateVersion7(),
            blockedRecord: null);
    }

    private static void AssertCheckpoint(
        AuditExportCheckpoint checkpoint,
        long sequence,
        bool chainComplete)
    {
        Assert.Equal(BootId, checkpoint.SupervisorBootId);
        Assert.Equal(sequence, checkpoint.Sequence);
        Assert.Equal(chainComplete, checkpoint.ChainComplete);
        if (sequence == 0)
        {
            Assert.Null(checkpoint.Spool);
            Assert.Equal(0, checkpoint.ByteOffset);
            Assert.Null(checkpoint.AcknowledgedEventId);
        }
    }

    private static void WriteProtected(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = SecureAuditStorage.CreateExclusiveFile(path);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void ReplaceLeaseName(AuditExportCheckpointStore store)
    {
        var displaced = store.LockPath + ".displaced-" + Guid.NewGuid().ToString("N");
        File.Move(store.LockPath, displaced);
        using var replacement = SecureAuditStorage.CreateExclusiveFile(store.LockPath);
        replacement.Flush(flushToDisk: true);
    }

    private static void AddUnprotectedAccess(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            AddWindowsWorldReadAccess(path, isDirectory);
            return;
        }

        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(
            path,
            mode | (isDirectory ? UnixFileMode.GroupExecute : UnixFileMode.GroupRead));
    }

    private static string ProtectionFingerprint(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return File.GetUnixFileMode(path).ToString();
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        return security.GetSecurityDescriptorSddlForm(
            AccessControlSections.Owner | AccessControlSections.Access);
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsWorldReadAccess(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            isDirectory ? FileSystemRights.ReadAndExecute : FileSystemRights.Read,
            AccessControlType.Allow));
        if (isDirectory)
        {
            FileSystemAclExtensions.SetAccessControl(
                new DirectoryInfo(path),
                (DirectorySecurity)security);
        }
        else
        {
            FileSystemAclExtensions.SetAccessControl(
                new FileInfo(path),
                (FileSecurity)security);
        }
    }

    private sealed class InjectedCheckpointException(string message) : Exception(message);
}
