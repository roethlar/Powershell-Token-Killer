using System.Diagnostics.CodeAnalysis;
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
    private readonly object _lifetimeGate = new();
    private FileStream? _lease;
    private AuditExportCheckpoint _current;
    private byte[] _currentBytes;
    private bool _faulted;
    private bool _disposeRequested;
    private int _journalWriterLeases;
    private int _closedReaderLeases;

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
            lock (_lifetimeGate)
            {
                ThrowIfUnavailable();
                return _current;
            }
        }
    }

    internal string CheckpointPath => _checkpointPath;

    internal string LockPath => _lockPath;

    internal Guid SupervisorBootId => _supervisorBootId;

    /// <summary>
    /// Creates control state for a fresh writer boot. Existing checkpoint,
    /// lease, or spool state is never reopened as a writer generation.
    /// </summary>
    internal static AuditExportCheckpointStore CreateForWriter(
        AuditOptions options,
        Guid supervisorBootId)
    {
        return CreateForWriterCore(options, supervisorBootId, preparation: null);
    }

    /// <summary>
    /// Creates control state only after the opaque production preparation has
    /// durably published and retained its exact empty segment zero.
    /// </summary>
    internal static AuditExportCheckpointStore CreateForPreparedWriter(
        AuditOptions options,
        AuditAnchoredWriterPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        preparation.VerifyForCheckpointCreation(options);
        var store = CreateForWriterCore(
            options,
            preparation.SupervisorBootId,
            preparation);
        try
        {
            preparation.BindCheckpointStore(store);
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    private static AuditExportCheckpointStore CreateForWriterCore(
        AuditOptions options,
        Guid supervisorBootId,
        AuditAnchoredWriterPreparation? preparation)
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
        if (EntryExists(checkpointPath) || EntryExists(lockPath))
        {
            throw new IOException("A fresh audit writer boot already has persistent state.");
        }
        VerifyWriterSegmentState(options, supervisorBootId, preparation);

        FileStream? lease = null;
        try
        {
            lease = SecureAuditStorage.CreateExclusiveFile(lockPath);
            VerifyLease(lease, root, lockPath);
            if (EntryExists(checkpointPath))
            {
                throw new IOException("A fresh audit writer boot raced existing state.");
            }
            VerifyWriterSegmentState(options, supervisorBootId, preparation);

            var initial = AuditExportCheckpoint.Initial(supervisorBootId);
            var initialBytes = AuditExportCheckpointCodec.Serialize(initial);
            PublishInitial(root, checkpointPath, supervisorBootId, initialBytes);
            var published = ReadSnapshotWithBytes(
                root,
                checkpointPath,
                supervisorBootId);
            if (!published.Bytes.AsSpan().SequenceEqual(initialBytes))
                throw new IOException("The initial audit export checkpoint was not published exactly.");
            VerifyLease(lease, root, lockPath);
            VerifyWriterSegmentState(options, supervisorBootId, preparation);

            var store = new AuditExportCheckpointStore(
                supervisorBootId,
                root,
                checkpointPath,
                lockPath,
                lease,
                published.Checkpoint,
                published.Bytes);
            lease = null;
            return store;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private static void VerifyWriterSegmentState(
        AuditOptions options,
        Guid supervisorBootId,
        AuditAnchoredWriterPreparation? preparation)
    {
        if (preparation is null)
        {
            if (HasSameBootSegment(options.SpoolDirectory, supervisorBootId))
            {
                throw new IOException(
                    "A fresh audit writer boot already has persistent state.");
            }
            return;
        }

        if (preparation.SupervisorBootId != supervisorBootId)
            throw new IOException("The prepared audit writer boot identity changed.");
        preparation.VerifyForCheckpointCreation(options);
    }

    /// <summary>
    /// Non-creating acquisition for a discovered closed boot chain. False
    /// means only that another process owns the exact persistent lease; all
    /// missing, malformed, misprotected, or replaced state fails closed.
    /// </summary>
    internal static bool TryAcquireExisting(
        AuditOptions options,
        Guid supervisorBootId,
        out AuditExportCheckpointStore? store)
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

        store = null;
        var root = VerifyExistingRoot(options.RootDirectory);
        var checkpointPath = Path.Combine(root, CheckpointFileName(supervisorBootId));
        var lockPath = Path.Combine(root, LockFileName(supervisorBootId));
        if (!EntryExists(checkpointPath) || !EntryExists(lockPath))
            throw new IOException("An existing audit export chain is missing control state.");
        SecureAuditStorage.VerifyExternalProtectedFile(checkpointPath);
        VerifyPersistentLeaseFile(root, lockPath);
        if (!HasSameBootSegment(options.SpoolDirectory, supervisorBootId))
            throw new IOException("The existing audit export chain has no spool segment.");

        FileStream? lease = null;
        try
        {
            if (!TryOpenExistingPersistentLease(lockPath, out lease))
                return false;
            VerifyLease(lease, root, lockPath);

            CleanStaleTemporaryFiles(root, supervisorBootId);
            if (!HasSameBootSegment(options.SpoolDirectory, supervisorBootId))
                throw new IOException("The existing audit export chain lost its spool segment.");
            var persisted = ReadSnapshotWithBytes(
                root,
                checkpointPath,
                supervisorBootId);
            VerifyLease(lease, root, lockPath);
            if (!HasSameBootSegment(options.SpoolDirectory, supervisorBootId))
                throw new IOException("The existing audit export chain lost its spool segment.");

            store = new AuditExportCheckpointStore(
                supervisorBootId,
                root,
                checkpointPath,
                lockPath,
                lease,
                persisted.Checkpoint,
                persisted.Bytes);
            lease = null;
            return true;
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

    /// <summary>
    /// Storage fault seam for checkpoint-store tests. Product export code must
    /// persist through reader/live-source opaque acknowledgment, block, and
    /// completion proofs instead of constructing cursor offsets directly.
    /// </summary>
    internal void SaveForTests(
        AuditExportCheckpoint next,
        Action? beforeAtomicReplaceForTests = null,
        Action? destinationReplacedForTests = null,
        Action? afterAtomicReplaceForTests = null,
        Action? beforeDurabilityConfirmationForTests = null,
        Action? directoryFlushStartingForTests = null)
    {
        lock (_lifetimeGate)
        {
            SaveLocked(
                next,
                beforeAtomicReplaceForTests,
                destinationReplacedForTests,
                afterAtomicReplaceForTests,
                beforeDurabilityConfirmationForTests,
                directoryFlushStartingForTests);
        }
    }

    private void SaveLocked(
        AuditExportCheckpoint next,
        Action? beforeAtomicReplaceForTests,
        Action? destinationReplacedForTests,
        Action? afterAtomicReplaceForTests,
        Action? beforeDurabilityConfirmationForTests,
        Action? directoryFlushStartingForTests)
    {
        ThrowIfUnavailable();
        ArgumentNullException.ThrowIfNull(next);
        AuditExportCheckpointCodec.Validate(next);
        ValidateTransition(_current, next, _supervisorBootId);

        PersistValidatedCheckpointLocked(
            next,
            beforeAtomicReplaceForTests,
            destinationReplacedForTests,
            afterAtomicReplaceForTests,
            beforeDurabilityConfirmationForTests,
            directoryFlushStartingForTests);
    }

    private void PersistValidatedCheckpointLocked(
        AuditExportCheckpoint next,
        Action? beforeAtomicReplaceForTests,
        Action? destinationReplacedForTests,
        Action? afterAtomicReplaceForTests,
        Action? beforeDurabilityConfirmationForTests,
        Action? directoryFlushStartingForTests)
    {
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
            var destinationReplaced = false;
            var destinationCallbackCompleted = false;
            try
            {
                beforeAtomicReplaceForTests?.Invoke();
                SecureAuditStorage.ReplaceAtomically(
                    temporaryPath,
                    _checkpointPath,
                    _root,
                    () =>
                    {
                        destinationReplaced = true;
                        destinationReplacedForTests?.Invoke();
                        destinationCallbackCompleted = true;
                    },
                    directoryFlushStartingForTests);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                ResolveUncertainReplacement(
                    next,
                    intendedBytes,
                    exception,
                    destinationReplaced,
                    destinationCallbackCompleted,
                    beforeDurabilityConfirmationForTests);
                return;
            }

            afterAtomicReplaceForTests?.Invoke();
            var persisted = ReadOwnedSnapshotOrFault();
            if (!persisted.Bytes.AsSpan().SequenceEqual(intendedBytes))
            {
                _faulted = true;
                throw new IOException(
                    "The audit export checkpoint replacement did not persist the intended state.");
            }

            VerifyLeasePath();
            _current = persisted.Checkpoint;
            _currentBytes = persisted.Bytes;
        }
        finally
        {
            SecureAuditStorage.TryDelete(temporaryPath);
        }
    }

    internal IDisposable RetainInitialJournalWriter(
        AuditOptions options,
        Guid supervisorBootId)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_lifetimeGate)
        {
            ThrowIfUnavailable();
            if (options.ProtectionMode != AuditProtectionMode.Anchored ||
                supervisorBootId != _supervisorBootId ||
                !PathsEqual(options.RootDirectory, _root))
            {
                throw new ArgumentException(
                    "The audit checkpoint owner does not match this journal writer.",
                    nameof(options));
            }

            VerifyLeasePath();
            VerifyCurrentPersistedState();
            var initial = AuditExportCheckpointCodec.Serialize(
                AuditExportCheckpoint.Initial(supervisorBootId));
            if (!_currentBytes.AsSpan().SequenceEqual(initial))
            {
                throw new IOException(
                    "A journal writer requires its exact initial export checkpoint.");
            }

            _journalWriterLeases = checked(_journalWriterLeases + 1);
            return new JournalWriterLease(this);
        }
    }

    internal ClosedChainReaderLease RetainExportReader(AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_lifetimeGate)
        {
            ThrowIfUnavailable();
            if (options.ProtectionMode != AuditProtectionMode.Anchored ||
                !PathsEqual(options.RootDirectory, _root))
            {
                throw new ArgumentException(
                    "The audit checkpoint owner does not match this export reader.",
                    nameof(options));
            }

            VerifyLeasePath();
            VerifyCurrentPersistedState();
            _closedReaderLeases = checked(_closedReaderLeases + 1);
            return new ClosedChainReaderLease(this);
        }
    }

    private T WithOwnedCheckpoint<T>(
        ClosedChainReaderLease reader,
        Func<AuditExportCheckpoint, T> action)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(action);
        lock (_lifetimeGate)
        {
            ThrowIfUnavailable();
            if (!reader.IsOwnedBy(this) || _closedReaderLeases < 1)
                throw new ObjectDisposedException(nameof(ClosedChainReaderLease));
            VerifyLeasePath();
            VerifyCurrentPersistedState();
            var expectedBytes = _currentBytes.ToArray();
            try
            {
                var result = action(_current);
                VerifyUnchangedOwnedCheckpoint(expectedBytes);
                return result;
            }
            catch (Exception acquisitionException) when (!IsFatal(acquisitionException))
            {
                try
                {
                    VerifyUnchangedOwnedCheckpoint(expectedBytes);
                }
                catch (Exception verificationException) when (!IsFatal(verificationException))
                {
                    _faulted = true;
                    throw new IOException(
                        "Audit checkpoint ownership changed while closed spool acquisition failed.",
                        new AggregateException(
                            acquisitionException,
                            verificationException));
                }
                throw;
            }
        }
    }

    private void AcknowledgeClosedRecord(
        ClosedChainReaderLease reader,
        AuditSpoolSegmentIdentity spool,
        long nextOffset,
        long sequence,
        Guid eventId,
        string exportConfigurationIdentity)
    {
        lock (_lifetimeGate)
        {
            EnsureClosedReaderLease(reader);
            RequireExportConfigurationIdentity(exportConfigurationIdentity);
            if (_current.BlockedRecord is not null)
            {
                throw new InvalidOperationException(
                    "A persisted audit export block requires its exact disposition capability.");
            }
            SaveLocked(
                new AuditExportCheckpoint(
                    _supervisorBootId,
                    chainComplete: false,
                    spool,
                    nextOffset,
                    sequence,
                    eventId,
                    blockedRecord: null),
                null,
                null,
                null,
                null,
                null);
        }
    }

    private ClosedConfigurationRetryAuthorization AuthorizeConfigurationRetry(
        ClosedChainReaderLease reader,
        AuditSpoolSegmentIdentity spool,
        long startOffset,
        long nextOffset,
        long sequence,
        Guid eventId,
        string exportConfigurationIdentity)
    {
        lock (_lifetimeGate)
        {
            EnsureClosedReaderLease(reader);
            RequireExportConfigurationIdentity(exportConfigurationIdentity);
            var blocked = _current.BlockedRecord;
            if (blocked is null ||
                blocked.Spool != spool ||
                blocked.ByteOffset != startOffset ||
                blocked.Sequence != sequence ||
                blocked.EventId != eventId)
            {
                throw new ArgumentException(
                    "The configuration retry proof does not identify the exact blocked record.",
                    nameof(spool));
            }
            if (blocked.FailureClass != AuditExportFailureClass.Configuration ||
                string.Equals(
                    blocked.ExportConfigurationIdentity,
                    exportConfigurationIdentity,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The persisted audit export block does not authorize this configuration retry.");
            }

            var authorizedBlock = new AuditExportBlockedRecord(
                blocked.Spool,
                blocked.ByteOffset,
                blocked.Sequence,
                blocked.EventId,
                blocked.FailureClass,
                blocked.DetailCode,
                blocked.ResponseDigest,
                blocked.FirstFailureUtc,
                exportConfigurationIdentity);
            var next = new AuditExportCheckpoint(
                _supervisorBootId,
                _current.ChainComplete,
                _current.Spool,
                _current.ByteOffset,
                _current.Sequence,
                _current.AcknowledgedEventId,
                authorizedBlock);
            AuditExportCheckpointCodec.Validate(next);
            PersistValidatedCheckpointLocked(
                next,
                null,
                null,
                null,
                null,
                null);
            return new ClosedConfigurationRetryAuthorization(
                this,
                reader,
                _currentBytes.ToArray(),
                spool,
                startOffset,
                nextOffset,
                sequence,
                eventId);
        }
    }

    private void AcknowledgeAuthorizedConfigurationRetry(
        ClosedChainReaderLease reader,
        ClosedConfigurationRetryAuthorization authorization)
    {
        lock (_lifetimeGate)
        {
            EnsureClosedReaderLease(reader);
            authorization.Consume(this, reader);
            VerifyAuthorizedConfigurationRetry(authorization);
            SaveLocked(
                new AuditExportCheckpoint(
                    _supervisorBootId,
                    chainComplete: false,
                    authorization.Spool,
                    authorization.NextOffset,
                    authorization.Sequence,
                    authorization.EventId,
                    blockedRecord: null),
                null,
                null,
                null,
                null,
                null);
        }
    }

    private void BlockAuthorizedConfigurationRetry(
        ClosedChainReaderLease reader,
        ClosedConfigurationRetryAuthorization authorization,
        AuditExportFailureClass failureClass,
        string detailCode,
        string? responseDigest)
    {
        lock (_lifetimeGate)
        {
            EnsureClosedReaderLease(reader);
            authorization.Consume(this, reader);
            var authorizedBlock = VerifyAuthorizedConfigurationRetry(authorization);
            var blocked = new AuditExportBlockedRecord(
                authorizedBlock.Spool,
                authorizedBlock.ByteOffset,
                authorizedBlock.Sequence,
                authorizedBlock.EventId,
                failureClass,
                detailCode,
                responseDigest,
                authorizedBlock.FirstFailureUtc,
                authorizedBlock.ExportConfigurationIdentity);
            var next = new AuditExportCheckpoint(
                _supervisorBootId,
                _current.ChainComplete,
                _current.Spool,
                _current.ByteOffset,
                _current.Sequence,
                _current.AcknowledgedEventId,
                blocked);
            AuditExportCheckpointCodec.Validate(next);
            PersistValidatedCheckpointLocked(
                next,
                null,
                null,
                null,
                null,
                null);
        }
    }

    private AuditExportBlockedRecord VerifyAuthorizedConfigurationRetry(
        ClosedConfigurationRetryAuthorization authorization)
    {
        if (!authorization.MatchesCheckpoint(_currentBytes) ||
            _current.BlockedRecord is not { } blocked ||
            blocked.Spool != authorization.Spool ||
            blocked.ByteOffset != authorization.StartOffset ||
            blocked.Sequence != authorization.Sequence ||
            blocked.EventId != authorization.EventId ||
            blocked.FailureClass != AuditExportFailureClass.Configuration)
        {
            throw new InvalidOperationException(
                "The configuration retry authorization is stale or belongs to another record.");
        }
        return blocked;
    }

    private void BlockClosedRecord(
        ClosedChainReaderLease reader,
        AuditSpoolSegmentIdentity spool,
        long startOffset,
        long sequence,
        Guid eventId,
        AuditExportFailureClass failureClass,
        string detailCode,
        string? responseDigest,
        DateTimeOffset firstFailureUtc,
        string exportConfigurationIdentity)
    {
        lock (_lifetimeGate)
        {
            EnsureClosedReaderLease(reader);
            if (_current.BlockedRecord is { } currentBlock &&
                currentBlock.Spool == spool &&
                currentBlock.ByteOffset == startOffset &&
                currentBlock.Sequence == sequence &&
                currentBlock.EventId == eventId)
            {
                firstFailureUtc = currentBlock.FirstFailureUtc;
            }

            var blocked = new AuditExportBlockedRecord(
                spool,
                startOffset,
                sequence,
                eventId,
                failureClass,
                detailCode,
                responseDigest,
                firstFailureUtc,
                exportConfigurationIdentity);
            SaveLocked(
                new AuditExportCheckpoint(
                    _supervisorBootId,
                    _current.ChainComplete,
                    _current.Spool,
                    _current.ByteOffset,
                    _current.Sequence,
                    _current.AcknowledgedEventId,
                    blocked),
                null,
                null,
                null,
                null,
                null);
        }
    }

    private void ApplyPermanentBlockDisposition(
        ClosedChainReaderLease reader,
        AuditSpoolSegmentIdentity spool,
        long startOffset,
        long nextOffset,
        long sequence,
        Guid eventId,
        AuditOperatorDispositionIntent intent)
    {
        lock (_lifetimeGate)
        {
            EnsureClosedReaderLease(reader);
            ArgumentNullException.ThrowIfNull(intent);
            var blocked = _current.BlockedRecord;
            if (blocked is null ||
                blocked.Spool != spool ||
                blocked.ByteOffset != startOffset ||
                blocked.Sequence != sequence ||
                blocked.EventId != eventId)
            {
                throw new ArgumentException(
                    "The operator disposition does not identify the exact blocked record.",
                    nameof(intent));
            }
            if (blocked.FailureClass is not (
                    AuditExportFailureClass.PartialRejection or
                    AuditExportFailureClass.Data or
                    AuditExportFailureClass.Protocol))
            {
                throw new InvalidOperationException(
                    "Only a permanent audit export block accepts operator disposition.");
            }

            intent.ConsumeForCheckpointAdvance(
                _supervisorBootId,
                spool,
                startOffset,
                nextOffset,
                sequence,
                eventId,
                blocked.FailureClass);
            SaveLocked(
                new AuditExportCheckpoint(
                    _supervisorBootId,
                    chainComplete: false,
                    spool,
                    nextOffset,
                    sequence,
                    eventId,
                    blockedRecord: null),
                null,
                null,
                null,
                null,
                null);
        }
    }

    private void CompleteClosedChain(
        ClosedChainReaderLease reader,
        AuditExportCheckpoint expectedOpenCheckpoint)
    {
        lock (_lifetimeGate)
        {
            EnsureClosedReaderLease(reader);
            var expectedBytes = AuditExportCheckpointCodec.Serialize(expectedOpenCheckpoint);
            if (!expectedBytes.AsSpan().SequenceEqual(_currentBytes))
            {
                throw new ArgumentException(
                    "The closed-chain end proof does not match the current checkpoint.",
                    nameof(expectedOpenCheckpoint));
            }

            SaveLocked(
                expectedOpenCheckpoint.ChainComplete
                    ? expectedOpenCheckpoint
                    : new AuditExportCheckpoint(
                        expectedOpenCheckpoint.SupervisorBootId,
                        chainComplete: true,
                        expectedOpenCheckpoint.Spool,
                        expectedOpenCheckpoint.ByteOffset,
                        expectedOpenCheckpoint.Sequence,
                        expectedOpenCheckpoint.AcknowledgedEventId,
                        blockedRecord: null),
                null,
                null,
                null,
                null,
                null);
        }
    }

    private void EnsureClosedReaderLease(ClosedChainReaderLease reader)
    {
        ThrowIfUnavailable();
        if (!reader.IsOwnedBy(this) || _closedReaderLeases < 1)
            throw new ObjectDisposedException(nameof(ClosedChainReaderLease));
        VerifyLeasePath();
        VerifyCurrentPersistedState();
    }

    private static void RequireExportConfigurationIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value.Length != 64 ||
            value.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The audit export configuration identity is invalid.",
                nameof(value));
        }
    }

    private void VerifyUnchangedOwnedCheckpoint(ReadOnlySpan<byte> expectedBytes)
    {
        VerifyLeasePath();
        VerifyCurrentPersistedState();
        if (expectedBytes.SequenceEqual(_currentBytes)) return;
        _faulted = true;
        throw new IOException(
            "The audit export checkpoint changed during closed spool acquisition.");
    }

    public void Dispose()
    {
        FileStream? lease = null;
        lock (_lifetimeGate)
        {
            if (_disposeRequested) return;
            _disposeRequested = true;
            if (_journalWriterLeases == 0 && _closedReaderLeases == 0)
            {
                lease = _lease;
                _lease = null;
            }
        }
        lease?.Dispose();
    }

    private void ReleaseJournalWriter()
    {
        FileStream? lease = null;
        lock (_lifetimeGate)
        {
            if (_journalWriterLeases < 1)
                throw new InvalidOperationException("The audit journal writer lease is unbalanced.");
            _journalWriterLeases--;
            if (_disposeRequested &&
                _journalWriterLeases == 0 &&
                _closedReaderLeases == 0)
            {
                lease = _lease;
                _lease = null;
            }
        }
        lease?.Dispose();
    }

    private void ReleaseClosedReader()
    {
        FileStream? lease = null;
        lock (_lifetimeGate)
        {
            if (_closedReaderLeases < 1)
                throw new InvalidOperationException("The closed spool reader lease is unbalanced.");
            _closedReaderLeases--;
            if (_disposeRequested &&
                _journalWriterLeases == 0 &&
                _closedReaderLeases == 0)
            {
                lease = _lease;
                _lease = null;
            }
        }
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

    internal static bool TryParseControlFileName(
        string fileName,
        out Guid supervisorBootId,
        out bool isCheckpoint)
    {
        supervisorBootId = default;
        isCheckpoint = false;
        if (!fileName.StartsWith(FilePrefix, StringComparison.Ordinal))
            return false;

        string suffix;
        if (fileName.EndsWith(CheckpointSuffix, StringComparison.Ordinal))
        {
            suffix = CheckpointSuffix;
            isCheckpoint = true;
        }
        else if (fileName.EndsWith(LockSuffix, StringComparison.Ordinal))
        {
            suffix = LockSuffix;
        }
        else
        {
            return false;
        }

        var id = fileName.AsSpan(
            FilePrefix.Length,
            fileName.Length - FilePrefix.Length - suffix.Length);
        return id.Length == 32 &&
               Guid.TryParseExact(id, "N", out supervisorBootId) &&
               AuditSpoolSegmentIdentity.IsUuidV4(supervisorBootId) &&
               supervisorBootId.ToString("N").AsSpan().SequenceEqual(id);
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

    private static string VerifyExistingRoot(string rootPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        return root;
    }

    private static bool TryOpenExistingPersistentLease(
        string lockPath,
        [NotNullWhen(true)] out FileStream? lease)
    {
        lease = null;
        try
        {
            lease = new FileStream(
                lockPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 1,
                    Options = FileOptions.WriteThrough,
                });
            return true;
        }
        catch (IOException exception) when (IsLeaseSharingViolation(exception))
        {
            return false;
        }
    }

    private static bool IsLeaseSharingViolation(IOException exception)
    {
        var nativeCode = exception.HResult & 0xffff;
        if (OperatingSystem.IsWindows())
            return nativeCode is 32 or 33;
        if (OperatingSystem.IsLinux())
            return exception.HResult == 11 || nativeCode == 11;
        if (OperatingSystem.IsMacOS())
            return exception.HResult == 35 || nativeCode == 35;
        return false;
    }

    private static void VerifyLease(FileStream lease, string root, string lockPath)
    {
        if (lease.Length != 0)
            throw new IOException("The audit export lease file is invalid.");
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        SecureAuditStorage.VerifyExternalProtectedFile(lockPath);
        _ = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
            lockPath,
            lease.SafeFileHandle);
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
        try
        {
            var lease = _lease ?? throw new ObjectDisposedException(
                nameof(AuditExportCheckpointStore));
            VerifyLease(lease, _root, _lockPath);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            _faulted = true;
            throw;
        }
    }

    private void VerifyCurrentPersistedState()
    {
        var persisted = ReadOwnedSnapshotOrFault();
        if (persisted.Bytes.AsSpan().SequenceEqual(_currentBytes))
            return;

        _faulted = true;
        throw new IOException(
            "The audit export checkpoint changed outside its owning lease.");
    }

    private (
        AuditExportCheckpoint Checkpoint,
        byte[] Bytes) ReadOwnedSnapshotOrFault()
    {
        try
        {
            return ReadSnapshotWithBytes(
                _root,
                _checkpointPath,
                _supervisorBootId);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            _faulted = true;
            throw;
        }
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
        Exception originalException,
        bool destinationReplaced,
        bool destinationCallbackCompleted,
        Action? beforeDurabilityConfirmationForTests)
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
        VerifyLeasePath();

        if (persisted.Bytes.AsSpan().SequenceEqual(intendedBytes))
        {
            if (!destinationReplaced)
            {
                _faulted = true;
                throw new IOException(
                    "The intended audit export checkpoint appeared without a confirmed atomic replacement.",
                    originalException);
            }

            if (destinationCallbackCompleted)
            {
                _faulted = true;
                throw new IOException(
                    "The audit export checkpoint replacement failed after its recovery-safe commit seam.",
                    originalException);
            }

            try
            {
                beforeDurabilityConfirmationForTests?.Invoke();
                SecureAuditStorage.ConfirmAtomicReplacementDurability(
                    _root,
                    _checkpointPath);
            }
            catch (Exception durabilityException) when (!IsFatal(durabilityException))
            {
                _faulted = true;
                throw new IOException(
                    "The audit export checkpoint replacement durability could not be confirmed.",
                    durabilityException);
            }

            VerifyLeasePath();
            _current = intended;
            _currentBytes = persisted.Bytes;
            return;
        }

        if (persisted.Bytes.AsSpan().SequenceEqual(_currentBytes))
        {
            if (!destinationReplaced)
                ExceptionDispatchInfo.Capture(originalException).Throw();

            _faulted = true;
            throw new IOException(
                "The audit export checkpoint reverted after a confirmed atomic replacement.",
                originalException);
        }

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

    internal static bool IsCanonicalTemporaryName(string name, Guid supervisorBootId)
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

        throw new ArgumentException(
            "A persisted audit export block cannot be rewritten without its exact disposition capability.",
            nameof(checkpoint));
    }

    private void ThrowIfUnavailable()
    {
        if (_disposeRequested || _lease is null)
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

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class JournalWriterLease(AuditExportCheckpointStore owner) : IDisposable
    {
        private AuditExportCheckpointStore? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseJournalWriter();
        }
    }

    internal sealed class ClosedChainReaderLease(
        AuditExportCheckpointStore owner) : IDisposable
    {
        private AuditExportCheckpointStore? _owner = owner;

        internal T WithOwnedCheckpoint<T>(Func<AuditExportCheckpoint, T> action)
        {
            var currentOwner = Volatile.Read(ref _owner)
                ?? throw new ObjectDisposedException(nameof(ClosedChainReaderLease));
            return currentOwner.WithOwnedCheckpoint(this, action);
        }

        internal bool IsOwnedBy(AuditExportCheckpointStore owner) =>
            ReferenceEquals(Volatile.Read(ref _owner), owner);

        internal void Acknowledge(
            AuditSpoolSegmentIdentity spool,
            long nextOffset,
            long sequence,
            Guid eventId,
            string exportConfigurationIdentity)
        {
            var currentOwner = Volatile.Read(ref _owner)
                ?? throw new ObjectDisposedException(nameof(ClosedChainReaderLease));
            currentOwner.AcknowledgeClosedRecord(
                this,
                spool,
                nextOffset,
                sequence,
                eventId,
                exportConfigurationIdentity);
        }

        internal ClosedConfigurationRetryAuthorization AuthorizeConfigurationRetry(
            AuditSpoolSegmentIdentity spool,
            long startOffset,
            long nextOffset,
            long sequence,
            Guid eventId,
            string exportConfigurationIdentity)
        {
            var currentOwner = Volatile.Read(ref _owner)
                ?? throw new ObjectDisposedException(nameof(ClosedChainReaderLease));
            return currentOwner.AuthorizeConfigurationRetry(
                this,
                spool,
                startOffset,
                nextOffset,
                sequence,
                eventId,
                exportConfigurationIdentity);
        }

        internal void AcknowledgeConfigurationRetry(
            ClosedConfigurationRetryAuthorization authorization)
        {
            var currentOwner = Volatile.Read(ref _owner)
                ?? throw new ObjectDisposedException(nameof(ClosedChainReaderLease));
            currentOwner.AcknowledgeAuthorizedConfigurationRetry(
                this,
                authorization);
        }

        internal void PersistConfigurationRetryBlock(
            ClosedConfigurationRetryAuthorization authorization,
            AuditExportFailureClass failureClass,
            string detailCode,
            string? responseDigest)
        {
            var currentOwner = Volatile.Read(ref _owner)
                ?? throw new ObjectDisposedException(nameof(ClosedChainReaderLease));
            currentOwner.BlockAuthorizedConfigurationRetry(
                this,
                authorization,
                failureClass,
                detailCode,
                responseDigest);
        }

        internal void PersistBlock(
            AuditSpoolSegmentIdentity spool,
            long startOffset,
            long sequence,
            Guid eventId,
            AuditExportFailureClass failureClass,
            string detailCode,
            string? responseDigest,
            DateTimeOffset firstFailureUtc,
            string exportConfigurationIdentity)
        {
            var currentOwner = Volatile.Read(ref _owner)
                ?? throw new ObjectDisposedException(nameof(ClosedChainReaderLease));
            currentOwner.BlockClosedRecord(
                this,
                spool,
                startOffset,
                sequence,
                eventId,
                failureClass,
                detailCode,
                responseDigest,
                firstFailureUtc,
                exportConfigurationIdentity);
        }

        internal void ApplyPermanentBlockDisposition(
            AuditSpoolSegmentIdentity spool,
            long startOffset,
            long nextOffset,
            long sequence,
            Guid eventId,
            AuditOperatorDispositionIntent intent)
        {
            var currentOwner = Volatile.Read(ref _owner)
                ?? throw new ObjectDisposedException(nameof(ClosedChainReaderLease));
            currentOwner.ApplyPermanentBlockDisposition(
                this,
                spool,
                startOffset,
                nextOffset,
                sequence,
                eventId,
                intent);
        }

        internal void MarkChainComplete(AuditExportCheckpoint expectedOpenCheckpoint)
        {
            var currentOwner = Volatile.Read(ref _owner)
                ?? throw new ObjectDisposedException(nameof(ClosedChainReaderLease));
            currentOwner.CompleteClosedChain(this, expectedOpenCheckpoint);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseClosedReader();
        }
    }

    internal sealed class ClosedConfigurationRetryAuthorization
    {
        private readonly AuditExportCheckpointStore _owner;
        private readonly ClosedChainReaderLease _reader;
        private readonly byte[] _expectedCheckpointBytes;
        private int _consumed;

        internal ClosedConfigurationRetryAuthorization(
            AuditExportCheckpointStore owner,
            ClosedChainReaderLease reader,
            byte[] expectedCheckpointBytes,
            AuditSpoolSegmentIdentity spool,
            long startOffset,
            long nextOffset,
            long sequence,
            Guid eventId)
        {
            _owner = owner;
            _reader = reader;
            _expectedCheckpointBytes = expectedCheckpointBytes;
            Spool = spool;
            StartOffset = startOffset;
            NextOffset = nextOffset;
            Sequence = sequence;
            EventId = eventId;
        }

        internal AuditSpoolSegmentIdentity Spool { get; }

        internal long StartOffset { get; }

        internal long NextOffset { get; }

        internal long Sequence { get; }

        internal Guid EventId { get; }

        internal bool MatchesCheckpoint(ReadOnlySpan<byte> checkpointBytes) =>
            _expectedCheckpointBytes.AsSpan().SequenceEqual(checkpointBytes);

        internal void Consume(
            AuditExportCheckpointStore owner,
            ClosedChainReaderLease reader)
        {
            if (!ReferenceEquals(_owner, owner) || !ReferenceEquals(_reader, reader))
            {
                throw new ArgumentException(
                    "The configuration retry authorization belongs to another checkpoint owner.");
            }
            if (Interlocked.Exchange(ref _consumed, 1) != 0)
            {
                throw new InvalidOperationException(
                    "The configuration retry authorization has already been consumed.");
            }
        }
    }
}
