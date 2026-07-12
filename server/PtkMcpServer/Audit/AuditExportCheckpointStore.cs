using System.Runtime.ExceptionServices;

namespace PtkMcpServer.Audit;

/// <summary>
/// Owns the exclusive exporter lease and durable cursor for one supervisor
/// boot. The empty lock file is persistent control state: disposing a store
/// releases its open handle but never removes the name.
/// </summary>
internal sealed class AuditExportCheckpointStore : IDisposable
{
    private const string FilePrefix = "export.checkpoint-";
    private const string CheckpointSuffix = ".json";
    private const string LockSuffix = ".lock";
    private const string TemporarySuffix = ".tmp";
    private readonly Guid _supervisorBootId;
    private readonly string _root;
    private readonly string _checkpointPath;
    private readonly string _lockPath;
    private FileStream? _lease;
    private AuditExportCheckpoint _current;
    private byte[] _currentBytes;
    private bool _faulted;

    private AuditExportCheckpointStore(
        Guid supervisorBootId,
        string root,
        string checkpointPath,
        string lockPath,
        FileStream lease,
        AuditExportCheckpoint current,
        byte[] currentBytes)
    {
        _supervisorBootId = supervisorBootId;
        _root = root;
        _checkpointPath = checkpointPath;
        _lockPath = lockPath;
        _lease = lease;
        _current = current;
        _currentBytes = currentBytes;
    }

    internal AuditExportCheckpoint Current
    {
        get
        {
            ThrowIfUnavailable();
            return _current;
        }
    }

    internal string CheckpointPath => _checkpointPath;

    internal string LockPath => _lockPath;

    internal static AuditExportCheckpointStore Acquire(
        AuditOptions options,
        Guid supervisorBootId)
    {
        ArgumentNullException.ThrowIfNull(options);
        AuditSpoolSegmentIdentity.RequireUuidV4(
            supervisorBootId,
            nameof(supervisorBootId));
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
        {
            throw new ArgumentException(
                "Audit export checkpoints require anchored protection mode.",
                nameof(options));
        }

        var root = PrepareOrVerifyRoot(options.RootDirectory);
        var checkpointPath = Path.Combine(root, CheckpointFileName(supervisorBootId));
        var lockPath = Path.Combine(root, LockFileName(supervisorBootId));
        FileStream? lease = null;
        try
        {
            lease = AcquirePersistentLease(root, lockPath);
            CleanStaleTemporaryFiles(root, supervisorBootId);

            AuditExportCheckpoint checkpoint;
            byte[] checkpointBytes;
            if (EntryExists(checkpointPath))
            {
                (checkpoint, checkpointBytes) = ReadSnapshotWithBytes(
                    root,
                    checkpointPath,
                    supervisorBootId);
            }
            else
            {
                if (HasSameBootSegment(options.SpoolDirectory, supervisorBootId))
                {
                    throw new IOException(
                        "An audit export checkpoint is missing for an existing supervisor spool.");
                }

                checkpoint = AuditExportCheckpoint.Initial(supervisorBootId);
                checkpointBytes = AuditExportCheckpointCodec.Serialize(checkpoint);
                PublishInitial(root, checkpointPath, supervisorBootId, checkpointBytes);
                var published = ReadSnapshotWithBytes(root, checkpointPath, supervisorBootId);
                if (!published.Bytes.AsSpan().SequenceEqual(checkpointBytes))
                {
                    throw new IOException(
                        "The initial audit export checkpoint was not published exactly.");
                }

                checkpoint = published.Checkpoint;
                checkpointBytes = published.Bytes;
            }

            var store = new AuditExportCheckpointStore(
                supervisorBootId,
                root,
                checkpointPath,
                lockPath,
                lease,
                checkpoint,
                checkpointBytes);
            lease = null;
            return store;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>
    /// Strict read-only snapshot used by the journal's anchored-mode ordering
    /// guard and by non-owner observers. FileShare.Delete permits a Windows
    /// atomic rename while the reader still holds the old inode.
    /// </summary>
    internal static AuditExportCheckpoint ReadSnapshot(
        AuditOptions options,
        Guid supervisorBootId)
    {
        ArgumentNullException.ThrowIfNull(options);
        AuditSpoolSegmentIdentity.RequireUuidV4(
            supervisorBootId,
            nameof(supervisorBootId));
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
        {
            throw new ArgumentException(
                "Audit export checkpoints require anchored protection mode.",
                nameof(options));
        }
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(options.RootDirectory));
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        VerifyPersistentLeaseFile(
            root,
            Path.Combine(root, LockFileName(supervisorBootId)));
        return ReadSnapshotWithBytes(
            root,
            Path.Combine(root, CheckpointFileName(supervisorBootId)),
            supervisorBootId).Checkpoint;
    }

    internal void Save(
        AuditExportCheckpoint next,
        Action? beforeAtomicReplaceForTests = null,
        Action? destinationReplacedForTests = null)
    {
        ThrowIfUnavailable();
        ArgumentNullException.ThrowIfNull(next);
        AuditExportCheckpointCodec.Validate(next);
        ValidateTransition(_current, next, _supervisorBootId);

        var intendedBytes = AuditExportCheckpointCodec.Serialize(next);
        VerifyLeasePath();
        VerifyCurrentPersistedState();
        if (intendedBytes.AsSpan().SequenceEqual(_currentBytes))
            return;

        var temporaryPath = Path.Combine(
            _root,
            TemporaryFileName(_supervisorBootId, Guid.NewGuid()));
        try
        {
            WriteDurableTemporary(temporaryPath, intendedBytes);
            try
            {
                beforeAtomicReplaceForTests?.Invoke();
                SecureAuditStorage.ReplaceAtomically(
                    temporaryPath,
                    _checkpointPath,
                    _root,
                    destinationReplacedForTests);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                ResolveUncertainReplacement(next, intendedBytes, exception);
                return;
            }

            var persisted = ReadSnapshotWithBytes(
                _root,
                _checkpointPath,
                _supervisorBootId);
            if (!persisted.Bytes.AsSpan().SequenceEqual(intendedBytes))
            {
                _faulted = true;
                throw new IOException(
                    "The audit export checkpoint replacement did not persist the intended state.");
            }

            _current = persisted.Checkpoint;
            _currentBytes = persisted.Bytes;
        }
        finally
        {
            SecureAuditStorage.TryDelete(temporaryPath);
        }
    }

    public void Dispose()
    {
        var lease = Interlocked.Exchange(ref _lease, null);
        lease?.Dispose();
    }

    internal static string CheckpointFileName(Guid supervisorBootId)
    {
        AuditSpoolSegmentIdentity.RequireUuidV4(
            supervisorBootId,
            nameof(supervisorBootId));
        return FilePrefix + supervisorBootId.ToString("N") + CheckpointSuffix;
    }

    internal static string LockFileName(Guid supervisorBootId)
    {
        AuditSpoolSegmentIdentity.RequireUuidV4(
            supervisorBootId,
            nameof(supervisorBootId));
        return FilePrefix + supervisorBootId.ToString("N") + LockSuffix;
    }

    internal static string TemporaryFileName(Guid supervisorBootId, Guid temporaryId)
    {
        AuditSpoolSegmentIdentity.RequireUuidV4(
            supervisorBootId,
            nameof(supervisorBootId));
        AuditSpoolSegmentIdentity.RequireUuidV4(temporaryId, nameof(temporaryId));
        return "." + CheckpointFileName(supervisorBootId) +
               "." + temporaryId.ToString("N") + TemporarySuffix;
    }

    private static string PrepareOrVerifyRoot(string rootPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var directory = new DirectoryInfo(root);
        directory.Refresh();
        if (directory.Exists || directory.LinkTarget is not null || File.Exists(root))
        {
            SecureAuditStorage.VerifyExternalProtectedDirectory(root);
            return root;
        }

        return SecureAuditStorage.PrepareRoot(root);
    }

    private static FileStream AcquirePersistentLease(string root, string lockPath)
    {
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        if (!EntryExists(lockPath))
        {
            FileStream? created = null;
            try
            {
                created = SecureAuditStorage.CreateExclusiveFile(lockPath);
            }
            catch (Exception exception) when (
                !IsFatal(exception) && EntryExists(lockPath))
            {
                // Another contender published the permanent lock name. Its
                // still-open FileShare.None handle decides which process owns
                // the lease; the loser fails when opening below.
            }

            if (created is not null)
            {
                try
                {
                    VerifyLease(created, root, lockPath);
                    return created;
                }
                catch
                {
                    created.Dispose();
                    throw;
                }
            }
        }

        SecureAuditStorage.VerifyExternalProtectedFile(lockPath);
        var lease = new FileStream(
            lockPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 1,
                Options = FileOptions.WriteThrough,
            });
        try
        {
            VerifyLease(lease, root, lockPath);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static void VerifyLease(FileStream lease, string root, string lockPath)
    {
        if (lease.Length != 0)
            throw new IOException("The audit export lease file is invalid.");
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        SecureAuditStorage.VerifyExternalProtectedFile(lockPath);
    }

    private static void VerifyPersistentLeaseFile(string root, string lockPath)
    {
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        SecureAuditStorage.VerifyExternalProtectedFile(lockPath);
        if (new FileInfo(lockPath).Length != 0)
            throw new IOException("The audit export lease file is invalid.");
    }

    private void VerifyLeasePath()
    {
        var lease = _lease ?? throw new ObjectDisposedException(
            nameof(AuditExportCheckpointStore));
        VerifyLease(lease, _root, _lockPath);
    }

    private void VerifyCurrentPersistedState()
    {
        var persisted = ReadSnapshotWithBytes(
            _root,
            _checkpointPath,
            _supervisorBootId);
        if (persisted.Bytes.AsSpan().SequenceEqual(_currentBytes))
            return;

        _faulted = true;
        throw new IOException(
            "The audit export checkpoint changed outside its owning lease.");
    }

    private static void PublishInitial(
        string root,
        string checkpointPath,
        Guid supervisorBootId,
        ReadOnlySpan<byte> bytes)
    {
        var temporaryPath = Path.Combine(
            root,
            TemporaryFileName(supervisorBootId, Guid.NewGuid()));
        try
        {
            WriteDurableTemporary(temporaryPath, bytes);
            SecureAuditStorage.PublishAtomically(temporaryPath, checkpointPath, root);
        }
        finally
        {
            SecureAuditStorage.TryDelete(temporaryPath);
        }
    }

    private static void WriteDurableTemporary(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = SecureAuditStorage.CreateExclusiveFile(path);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private void ResolveUncertainReplacement(
        AuditExportCheckpoint intended,
        ReadOnlySpan<byte> intendedBytes,
        Exception originalException)
    {
        (AuditExportCheckpoint Checkpoint, byte[] Bytes) persisted;
        try
        {
            persisted = ReadSnapshotWithBytes(
                _root,
                _checkpointPath,
                _supervisorBootId);
        }
        catch (Exception reloadException) when (!IsFatal(reloadException))
        {
            _faulted = true;
            throw new IOException(
                "The audit export checkpoint replacement outcome could not be established.",
                reloadException);
        }

        if (persisted.Bytes.AsSpan().SequenceEqual(intendedBytes))
        {
            _current = intended;
            _currentBytes = persisted.Bytes;
            return;
        }

        if (persisted.Bytes.AsSpan().SequenceEqual(_currentBytes))
            ExceptionDispatchInfo.Capture(originalException).Throw();

        _faulted = true;
        throw new IOException(
            "The audit export checkpoint replacement produced an unexpected state.",
            originalException);
    }

    private static (
        AuditExportCheckpoint Checkpoint,
        byte[] Bytes) ReadSnapshotWithBytes(
        string root,
        string checkpointPath,
        Guid supervisorBootId)
    {
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        var bytes = SecureAuditStorage.ReadProtectedFile(
            checkpointPath,
            AuditExportCheckpointCodec.MaximumBytes,
            requireProtectedParent: true,
            verifyWithoutMutation: true,
            share: FileShare.Read | FileShare.Delete);
        var checkpoint = AuditExportCheckpointCodec.Parse(bytes);
        if (checkpoint.SupervisorBootId != supervisorBootId)
            throw new IOException("The audit export checkpoint boot identity is invalid.");
        return (checkpoint, bytes);
    }

    private static bool HasSameBootSegment(string spoolPath, Guid supervisorBootId)
    {
        var spool = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spoolPath));
        var directory = new DirectoryInfo(spool);
        directory.Refresh();
        if (!directory.Exists && directory.LinkTarget is null && !File.Exists(spool))
            return false;

        SecureAuditStorage.VerifyExternalProtectedDirectory(spool);
        foreach (var entry in directory.EnumerateFileSystemInfos(
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            if (!AuditSpoolSegmentIdentity.TryParse(entry.Name, out var identity) ||
                identity.SupervisorBootId != supervisorBootId)
            {
                continue;
            }

            SecureAuditStorage.VerifyExternalProtectedFile(entry.FullName);
            return true;
        }

        return false;
    }

    private static void CleanStaleTemporaryFiles(string root, Guid supervisorBootId)
    {
        var prefix = "." + CheckpointFileName(supervisorBootId) + ".";
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos(
                     prefix + "*" + TemporarySuffix,
                     SearchOption.TopDirectoryOnly))
        {
            if (!IsCanonicalTemporaryName(entry.Name, supervisorBootId))
                continue;
            SecureAuditStorage.VerifyExternalProtectedFile(entry.FullName);
            File.Delete(entry.FullName);
            if (EntryExists(entry.FullName))
                throw new IOException("A stale audit export checkpoint file could not be removed.");
        }

        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
    }

    private static bool IsCanonicalTemporaryName(string name, Guid supervisorBootId)
    {
        var prefix = "." + CheckpointFileName(supervisorBootId) + ".";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(TemporarySuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var id = name.AsSpan(
            prefix.Length,
            name.Length - prefix.Length - TemporarySuffix.Length);
        return id.Length == 32 &&
               Guid.TryParseExact(id, "N", out var parsed) &&
               AuditSpoolSegmentIdentity.IsUuidV4(parsed) &&
               parsed.ToString("N").AsSpan().SequenceEqual(id);
    }

    private static void ValidateTransition(
        AuditExportCheckpoint current,
        AuditExportCheckpoint next,
        Guid supervisorBootId)
    {
        if (next.SupervisorBootId != supervisorBootId)
            throw new ArgumentException(
                "An audit export checkpoint cannot change supervisor boot identity.",
                nameof(next));
        if (next.Sequence < current.Sequence ||
            current.ChainComplete && !next.ChainComplete)
        {
            throw new ArgumentException(
                "An audit export checkpoint cannot regress.",
                nameof(next));
        }

        if (next.Sequence == current.Sequence)
        {
            if (next.Spool != current.Spool ||
                next.ByteOffset != current.ByteOffset ||
                next.AcknowledgedEventId != current.AcknowledgedEventId)
            {
                throw new ArgumentException(
                    "An audit export checkpoint cannot rewrite an acknowledged position.",
                    nameof(next));
            }

            ValidateBlockedTransition(current.BlockedRecord, next.BlockedRecord, next);
            return;
        }

        if (current.ChainComplete ||
            current.Sequence == long.MaxValue ||
            next.Sequence != current.Sequence + 1 ||
            next.Spool is not { } nextSpool)
        {
            throw new ArgumentException(
                "An audit export checkpoint must advance exactly one open-chain record.",
                nameof(next));
        }

        if (current.Spool is not { } currentSpool)
        {
            if (nextSpool.Index != 0)
            {
                throw new ArgumentException(
                    "The first acknowledged audit record must be in segment zero.",
                    nameof(next));
            }
        }
        else if (nextSpool.Index == currentSpool.Index)
        {
            if (next.ByteOffset <= current.ByteOffset)
            {
                throw new ArgumentException(
                    "An audit export checkpoint cannot move its spool cursor backward.",
                    nameof(next));
            }
        }
        else if (currentSpool.Index == AuditSpoolSegmentIdentity.MaximumIndex ||
                 nextSpool.Index != currentSpool.Index + 1)
        {
            throw new ArgumentException(
                "An audit export checkpoint cannot skip spool segments.",
                nameof(next));
        }

        if (current.BlockedRecord is { } blocked &&
            (nextSpool != blocked.Spool ||
             next.AcknowledgedEventId != blocked.EventId ||
             next.ByteOffset <= blocked.ByteOffset))
        {
            throw new ArgumentException(
                "An audit export checkpoint cannot advance past a different blocked record.",
                nameof(next));
        }
    }

    private static void ValidateBlockedTransition(
        AuditExportBlockedRecord? current,
        AuditExportBlockedRecord? next,
        AuditExportCheckpoint checkpoint)
    {
        if (current is null) return;
        if (next is null)
        {
            throw new ArgumentException(
                "An audit export block can clear only by advancing its exact record.",
                nameof(checkpoint));
        }

        var sameRecord = current.Spool == next.Spool &&
                         current.ByteOffset == next.ByteOffset &&
                         current.Sequence == next.Sequence &&
                         current.EventId == next.EventId;
        var exactBlock = sameRecord &&
                         current.FailureClass == next.FailureClass &&
                         string.Equals(current.DetailCode, next.DetailCode, StringComparison.Ordinal) &&
                         string.Equals(current.ResponseDigest, next.ResponseDigest, StringComparison.Ordinal) &&
                         current.FirstFailureUtc == next.FirstFailureUtc &&
                         string.Equals(
                             current.ExportConfigurationIdentity,
                             next.ExportConfigurationIdentity,
                             StringComparison.Ordinal);
        if (exactBlock) return;

        if (!sameRecord ||
            current.FailureClass != AuditExportFailureClass.Configuration ||
            next.FailureClass != AuditExportFailureClass.Configuration ||
            string.Equals(
                current.ExportConfigurationIdentity,
                next.ExportConfigurationIdentity,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A persisted audit export block cannot be rewritten.",
                nameof(checkpoint));
        }
    }

    private void ThrowIfUnavailable()
    {
        if (_lease is null)
            throw new ObjectDisposedException(nameof(AuditExportCheckpointStore));
        if (_faulted)
            throw new IOException("The audit export checkpoint store is faulted.");
    }

    private static bool EntryExists(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        return file.Exists || file.LinkTarget is not null || Directory.Exists(path);
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;
}
