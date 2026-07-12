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
    public void Acquire_publishes_exact_initial_checkpoint_and_persistent_lock()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        string checkpointPath;
        string lockPath;

        using (var store = AuditExportCheckpointStore.Acquire(options, BootId))
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
        }

        Assert.True(File.Exists(lockPath));
        using var restarted = AuditExportCheckpointStore.Acquire(options, BootId);
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
        var first = AuditExportCheckpointStore.Acquire(options, BootId);
        var lockPath = first.LockPath;
        try
        {
            Assert.ThrowsAny<IOException>(() =>
                AuditExportCheckpointStore.Acquire(options, BootId));
            Assert.True(File.Exists(lockPath));
        }
        finally
        {
            first.Dispose();
        }

        using var restarted = AuditExportCheckpointStore.Acquire(options, BootId);
        Assert.True(File.Exists(lockPath));
        AssertCheckpoint(restarted.Current, sequence: 0, chainComplete: false);
    }

    [Fact]
    public void Anchored_sink_refuses_to_publish_a_segment_before_fresh_checkpoint()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);

        Assert.ThrowsAny<IOException>(() =>
            new FileAuditJournalSink(options, BootId));
        Assert.Empty(Directory.EnumerateFiles(options.SpoolDirectory, "*.jsonl"));

        using var store = AuditExportCheckpointStore.Acquire(options, BootId);
        Assert.True(File.Exists(store.CheckpointPath));
        using var sink = new FileAuditJournalSink(options, BootId);
        Assert.Equal(
            AuditSpoolSegmentIdentity.Create(BootId, 0).FileName,
            Path.GetFileName(sink.CurrentSegmentPath));
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
            AuditExportCheckpointStore.Acquire(anchoredOptions, BootId));

        Assert.False(File.Exists(checkpointPath));
        Assert.True(File.Exists(segmentPath));
        Assert.True(File.Exists(Path.Combine(
            root,
            AuditExportCheckpointStore.LockFileName(BootId))));
    }

    [Fact]
    public void Existing_checkpoint_tamper_is_rejected_without_rewriting_bytes()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        string checkpointPath;
        using (var store = AuditExportCheckpointStore.Acquire(options, BootId))
            checkpointPath = store.CheckpointPath;
        var tampered = "{\"not\":\"a checkpoint\"}\n"u8.ToArray();
        File.WriteAllBytes(checkpointPath, tampered);

        Assert.Throws<IOException>(() =>
            AuditExportCheckpointStore.ReadSnapshot(options, BootId));
        Assert.Throws<IOException>(() =>
            AuditExportCheckpointStore.Acquire(options, BootId));

        Assert.Equal(tampered, File.ReadAllBytes(checkpointPath));
    }

    [Fact]
    public void Live_store_never_overwrites_checkpoint_tampering()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.Acquire(options, BootId);
        var tampered = AuditExportCheckpointCodec.Serialize(
            Checkpoint(BootId, sequence: 2, byteOffset: 256));
        File.WriteAllBytes(store.CheckpointPath, tampered);

        Assert.Throws<IOException>(() => store.Save(
            Checkpoint(BootId, sequence: 1, byteOffset: 128)));

        Assert.Equal(tampered, File.ReadAllBytes(store.CheckpointPath));
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
        using (var store = AuditExportCheckpointStore.Acquire(options, BootId))
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
        using (var store = AuditExportCheckpointStore.Acquire(options, BootId))
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
        using var store = AuditExportCheckpointStore.Acquire(options, BootId);
        var acknowledged = Checkpoint(BootId, sequence: 1, byteOffset: 128);
        store.Save(acknowledged);
        var exactBytes = File.ReadAllBytes(store.CheckpointPath);

        Assert.Throws<ArgumentException>(() =>
            store.Save(AuditExportCheckpoint.Initial(BootId)));
        Assert.Throws<ArgumentException>(() =>
            store.Save(AuditExportCheckpoint.Initial(OtherBootId)));
        Assert.Throws<ArgumentException>(() =>
            store.Save(Checkpoint(BootId, sequence: 1, byteOffset: 256)));

        Assert.Equal(exactBytes, File.ReadAllBytes(store.CheckpointPath));
        AssertCheckpoint(store.Current, sequence: 1, chainComplete: false);

        var complete = Checkpoint(
            BootId,
            sequence: 1,
            byteOffset: 128,
            acknowledgedEventId: acknowledged.AcknowledgedEventId,
            chainComplete: true);
        store.Save(complete);
        Assert.Throws<ArgumentException>(() => store.Save(acknowledged));
        AssertCheckpoint(store.Current, sequence: 1, chainComplete: true);
    }

    [Fact]
    public void Save_advances_exactly_one_record_without_skipping_segments()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.Acquire(options, BootId);
        Assert.Throws<ArgumentException>(() =>
            store.Save(Checkpoint(BootId, sequence: 2, byteOffset: 256)));

        var first = Checkpoint(BootId, sequence: 1, byteOffset: 128);
        store.Save(first);
        Assert.Throws<ArgumentException>(() =>
            store.Save(Checkpoint(BootId, sequence: 3, byteOffset: 384)));
        Assert.Throws<ArgumentException>(() =>
            store.Save(new AuditExportCheckpoint(
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
        store.Save(secondSegment);

        Assert.Equal(2, store.Current.Sequence);
        Assert.Equal(1, store.Current.Spool?.Index);
    }

    [Fact]
    public void Save_clears_a_block_only_by_advancing_its_exact_record()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.Acquire(options, BootId);
        var acknowledged = Checkpoint(BootId, sequence: 1, byteOffset: 128);
        store.Save(acknowledged);
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
        store.Save(blockedCheckpoint);

        Assert.Throws<ArgumentException>(() => store.Save(acknowledged));
        Assert.Throws<ArgumentException>(() => store.Save(new AuditExportCheckpoint(
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
        store.Save(disposition);

        Assert.Equal(2, store.Current.Sequence);
        Assert.Equal(blocked.EventId, store.Current.AcknowledgedEventId);
        Assert.Null(store.Current.BlockedRecord);
    }

    [Fact]
    public void Configuration_block_can_be_reclassified_only_for_a_changed_identity()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.Acquire(options, BootId);
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
        store.Save(new AuditExportCheckpoint(BootId, false, null, 0, 0, null, first));

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
        store.Save(new AuditExportCheckpoint(BootId, false, null, 0, 0, null, retried));

        Assert.Equal(changedIdentity, store.Current.BlockedRecord?.ExportConfigurationIdentity);
        Assert.Throws<ArgumentException>(() => store.Save(new AuditExportCheckpoint(
            BootId,
            false,
            null,
            0,
            0,
            null,
            new AuditExportBlockedRecord(
                retried.Spool,
                retried.ByteOffset,
                retried.Sequence,
                retried.EventId,
                AuditExportFailureClass.Protocol,
                "http.500",
                responseDigest: null,
                retried.FirstFailureUtc,
                changedIdentity))));
    }

    [Fact]
    public void Failed_replace_that_left_exact_prior_state_rethrows_exact_failure()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.Acquire(options, BootId);
        var priorBytes = File.ReadAllBytes(store.CheckpointPath);
        var injected = new InjectedCheckpointException("before replace");

        var caught = Assert.Throws<InjectedCheckpointException>(() =>
            store.Save(
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
        using var store = AuditExportCheckpointStore.Acquire(options, BootId);
        var intended = Checkpoint(BootId, sequence: 1, byteOffset: 128);

        store.Save(
            intended,
            destinationReplacedForTests: () =>
                throw new InjectedCheckpointException("after replace"));

        Assert.Equal(
            AuditExportCheckpointCodec.Serialize(intended),
            File.ReadAllBytes(store.CheckpointPath));
        AssertCheckpoint(store.Current, sequence: 1, chainComplete: false);
    }

    [Fact]
    public void Uncertain_replace_with_neither_prior_nor_intended_state_faults_store()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        using var store = AuditExportCheckpointStore.Acquire(options, BootId);
        var unexpected = Checkpoint(BootId, sequence: 2, byteOffset: 256);

        Assert.Throws<IOException>(() => store.Save(
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
        using var store = AuditExportCheckpointStore.Acquire(options, BootId);
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
                store.Save(Checkpoint(
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
        using (var first = AuditExportCheckpointStore.Acquire(options, BootId))
            originalBytes = File.ReadAllBytes(first.CheckpointPath);
        var staleName = AuditExportCheckpointStore.TemporaryFileName(
            BootId,
            Guid.Parse("42345678-1234-4abc-8def-0123456789ab"));
        var stalePath = Path.Combine(options.RootDirectory, staleName);
        WriteProtected(
            stalePath,
            AuditExportCheckpointCodec.Serialize(
                Checkpoint(BootId, sequence: 1, byteOffset: 128)));

        using var restarted = AuditExportCheckpointStore.Acquire(options, BootId);

        Assert.False(File.Exists(stalePath));
        Assert.Equal(originalBytes, File.ReadAllBytes(restarted.CheckpointPath));
        AssertCheckpoint(restarted.Current, sequence: 0, chainComplete: false);
    }

    [Fact]
    public void Sabotaged_persistent_lock_is_rejected_without_repair()
    {
        var options = Options(NewRoot(), AuditProtectionMode.Anchored);
        string lockPath;
        using (var store = AuditExportCheckpointStore.Acquire(options, BootId))
            lockPath = store.LockPath;
        AddUnprotectedAccess(lockPath, isDirectory: false);
        var protectionBefore = ProtectionFingerprint(lockPath, isDirectory: false);

        Assert.Throws<IOException>(() =>
            AuditExportCheckpointStore.Acquire(options, BootId));
        Assert.Throws<IOException>(() =>
            AuditExportCheckpointStore.ReadSnapshot(options, BootId));

        Assert.Equal(
            protectionBefore,
            ProtectionFingerprint(lockPath, isDirectory: false));
        Assert.True(File.Exists(lockPath));
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
