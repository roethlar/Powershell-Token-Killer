namespace PtkMcpServer.Audit;

internal interface IAuditClosedSpoolRecordPosition
{
    AuditSpoolSegmentIdentity Spool { get; }

    long StartOffset { get; }

    long NextOffset { get; }

    long Sequence { get; }

    Guid EventId { get; }

    string? PreviousEventHash { get; }

    string EventHash { get; }

    ReadOnlyMemory<byte> ExactJsonlBytes { get; }
}

/// <summary>
/// Opaque proof that one exact checkpoint was observed at the end of a
/// retained, validated complete-chain snapshot.
/// </summary>
internal interface IAuditClosedSpoolChainEndPosition;

/// <summary>
/// Opaque proof that one exact checkpoint was observed at the end of the
/// retained closed prefix before one caller-owned exclusive live segment.
/// This proof cannot authorize chain completion.
/// </summary>
internal interface IAuditClosedSpoolPrefixEndPosition;

/// <summary>
/// Opaque, one-use proof that a changed export configuration durably consumed
/// its single retry attempt for one exact blocked record.
/// </summary>
internal interface IAuditClosedSpoolConfigurationRetryAuthorization;

internal sealed record AuditClosedSpoolPrefixTransitionState(
    AuditSpoolSegmentIdentity Boundary,
    long TailSequence,
    string? TailEventHash);

/// <summary>
/// Reads one immutable, closed anchored spool chain. This reader does not
/// discover live writer state or acquire an orphan lease; its caller must hold
/// the boot's exporter lease before selecting the chain.
/// </summary>
internal sealed class AuditClosedSpoolChainReader : IDisposable
{
    // Snapshot integrity requires retaining every selected segment handle.
    // These fixed ceilings bound descriptor and directory-walk work even if
    // same-user spool contents no longer reflect writer-created state.
    internal const int MaximumClosedChainSegments = 256;
    internal const int MaximumSpoolInventoryEntries = 1_024;
    private readonly AuditOptions _options;
    private readonly Guid _supervisorBootId;
    private readonly string _spoolRoot;
    private readonly object _gate = new();
    private readonly Action? _handlesAcquiredForTests;
    private readonly AuditExportCheckpointStore.ClosedChainReaderLease _checkpointLease;
    private SegmentHandle[] _segments = [];
    private object? _snapshotToken;
    private bool _exportPumpRetained;
    private bool _disposed;

    internal AuditClosedSpoolChainReader(
        AuditOptions options,
        AuditExportCheckpointStore checkpointStore,
        Action? handlesAcquiredForTests = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
        {
            throw new ArgumentException(
                "A closed export spool reader requires anchored audit protection.",
                nameof(options));
        }

        _options = options;
        _supervisorBootId = checkpointStore.SupervisorBootId;
        _spoolRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(options.SpoolDirectory));
        _handlesAcquiredForTests = handlesAcquiredForTests;
        _checkpointLease = checkpointStore.RetainExportReader(options);
    }

    internal string ExportConfigurationIdentity =>
        _options.ExportConfigurationIdentity ?? throw new IOException(
            "The anchored audit reader has no export configuration identity.");

    internal IDisposable RetainExportPump()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_exportPumpRetained)
            {
                throw new InvalidOperationException(
                    "The closed audit spool reader already has an export pump.");
            }
            _exportPumpRetained = true;
            return new ExportPumpLease(this);
        }
    }

    /// <summary>
    /// Acquires every selected segment exclusively, validates the complete
    /// chain through those handles, and resolves the durable cursor to its
    /// exact following record. A null next record is a verified closed-chain
    /// end candidate; the caller may then persist chain_complete.
    /// </summary>
    internal AuditClosedSpoolRecovery ResolveCheckpoint()
    {
        return ResolveCheckpointCore(
            exclusiveLiveBoundary: null,
            rotation: null,
            expectedFinalSegment: null);
    }

    /// <summary>
    /// Acquires and validates exactly the closed segments preceding the
    /// authoritative live reader's opaque rotation boundary. The boundary and
    /// any newer segments are observed by name only and are never opened by
    /// this reader.
    /// </summary>
    internal AuditClosedSpoolRecovery ResolveClosedPrefix(
        IAuditLiveSpoolRotationPosition rotation)
    {
        var exclusiveLiveBoundary = AuditLiveSpoolReader.RequirePendingRotation(
            rotation,
            _supervisorBootId);
        if (exclusiveLiveBoundary.Index > MaximumClosedChainSegments)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rotation),
                "The closed audit spool prefix exceeds its recovery segment bound.");
        }

        return ResolveCheckpointCore(
            exclusiveLiveBoundary,
            rotation,
            expectedFinalSegment: null);
    }

    /// <summary>
    /// Promotes an authoritative writer-closed observation to one retained
    /// complete-chain snapshot whose final segment must match that observation.
    /// </summary>
    internal AuditClosedSpoolRecovery ResolveAfterWriterClosed(
        IAuditLiveSpoolWriterClosedPosition writerClosed)
    {
        var expectedFinalSegment = AuditLiveSpoolReader.RequirePendingWriterClosed(
            writerClosed,
            _supervisorBootId);
        if (expectedFinalSegment.Index >= MaximumClosedChainSegments)
        {
            throw new ArgumentOutOfRangeException(
                nameof(writerClosed),
                "The closed audit spool chain exceeds its recovery segment bound.");
        }
        return ResolveCheckpointCore(
            exclusiveLiveBoundary: null,
            rotation: null,
            expectedFinalSegment);
    }

    private AuditClosedSpoolRecovery ResolveCheckpointCore(
        AuditSpoolSegmentIdentity? exclusiveLiveBoundary,
        IAuditLiveSpoolRotationPosition? rotation,
        AuditSpoolSegmentIdentity? expectedFinalSegment)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ReleaseSnapshot();
            PendingResolution? pending = null;
            try
            {
                using var quotaLease = AuditSpoolQuotaLease.AcquireExisting(_spoolRoot);
                _checkpointLease.WithOwnedCheckpoint(checkpoint =>
                {
                    pending = ResolveOwnedCheckpoint(
                        checkpoint,
                        exclusiveLiveBoundary,
                        rotation,
                        expectedFinalSegment,
                        quotaLease);
                    return true;
                });
                var resolved = pending ?? throw new IOException(
                    "The owned checkpoint did not produce a closed spool resolution.");
                _segments = resolved.Segments;
                _snapshotToken = resolved.SnapshotToken;
                pending = null;
                return resolved.Recovery;
            }
            finally
            {
                pending?.Dispose();
            }
        }
    }

    private PendingResolution ResolveOwnedCheckpoint(
        AuditExportCheckpoint checkpoint,
        AuditSpoolSegmentIdentity? exclusiveLiveBoundary,
        IAuditLiveSpoolRotationPosition? rotation,
        AuditSpoolSegmentIdentity? expectedFinalSegment,
        AuditSpoolQuotaLease quotaLease)
    {
        SegmentHandle[] acquired = [];
        try
        {
            try
            {
                AuditExportCheckpointCodec.Validate(checkpoint);
                if (checkpoint.SupervisorBootId != _supervisorBootId)
                {
                    throw new IOException(
                        "The owned audit export checkpoint belongs to a different spool chain.");
                }

                if (exclusiveLiveBoundary is not null && checkpoint.ChainComplete)
                {
                    throw new IOException(
                        "A complete audit export checkpoint cannot be resolved as a live prefix.");
                }

                var inventory = InventoryClosedChain(exclusiveLiveBoundary);
                if (expectedFinalSegment is { } finalSegment &&
                    (inventory.Length == 0 ||
                     inventory[^1].Identity != finalSegment))
                {
                    throw new IOException(
                        "The closed audit spool chain does not end at the observed writer closure.");
                }
                acquired = AcquireClosedChain(inventory);
                // The complete handle set is already held FileShare.None.
                // This second inventory rejects a name-set race before any
                // checkpoint decision is based on the retained inodes.
                _handlesAcquiredForTests?.Invoke();
                VerifyStableInventory(inventory, exclusiveLiveBoundary);
                VerifyRetainedIdentities(acquired);
                quotaLease.VerifyOwnership();
            }
            finally
            {
                // Retained no-share segment handles now freeze the selected
                // snapshot; do not hold the global quota during record parsing
                // or network work.
                quotaLease.Dispose();
            }

                var snapshotToken = new object();
                RecordPosition? nextRecord = null;
                var acknowledged = checkpoint.Sequence == 0;
                var captureNext = acknowledged;
                long? previousSequence = null;
                string? previousHash = null;

                foreach (var segment in acquired)
                {
                    var offset = 0L;
                    while (offset < segment.Descriptor.Length)
                    {
                        var record = ReadRecord(
                            segment,
                            offset,
                            snapshotToken,
                            previousSequence is null
                                ? 1
                                : CheckedNext(previousSequence.Value),
                            previousHash);
                        offset = record.NextOffset;

                        if (captureNext && nextRecord is null)
                        {
                            nextRecord = record;
                            captureNext = false;
                        }

                        if (checkpoint.Sequence != 0 &&
                            checkpoint.Spool == record.Spool &&
                            checkpoint.ByteOffset == record.NextOffset)
                        {
                            if (acknowledged ||
                                checkpoint.Sequence != record.Sequence ||
                                checkpoint.AcknowledgedEventId != record.EventId)
                            {
                                throw new IOException(
                                    "The audit export checkpoint does not identify an exact spool record.");
                            }

                            acknowledged = true;
                            captureNext = true;
                        }

                        previousSequence = record.Sequence;
                        previousHash = record.EventHash;
                    }

                    if (offset != segment.Descriptor.Length ||
                        segment.Stream.Length != segment.Descriptor.Length)
                    {
                        throw new IOException(
                            "A closed audit segment changed while it was read.");
                    }
                }

                VerifyRetainedIdentities(acquired);

                if (!acknowledged)
                {
                    throw new IOException(
                        "The audit export checkpoint does not identify an exact spool record end.");
                }

                if (checkpoint.BlockedRecord is { } blocked &&
                    (nextRecord is null ||
                     blocked.Spool != nextRecord.Spool ||
                     blocked.ByteOffset != nextRecord.StartOffset ||
                     blocked.Sequence != nextRecord.Sequence ||
                     blocked.EventId != nextRecord.EventId))
                {
                    throw new IOException(
                        "The blocked audit export checkpoint does not identify the exact next record.");
                }

                if (checkpoint.ChainComplete && nextRecord is not null)
                {
                    throw new IOException(
                        "A complete audit export checkpoint still has an unacknowledged record.");
                }

                AuditClosedSpoolRecovery recovery;
                if (nextRecord is not null)
                {
                    recovery = new AuditClosedSpoolRecovery.Record(
                        nextRecord,
                        checkpoint.BlockedRecord);
                }
                else if (exclusiveLiveBoundary is { } boundary)
                {
                    if (rotation is null)
                    {
                        throw new IOException(
                            "The closed audit spool prefix lost its live rotation proof.");
                    }
                    var tailSequence = previousSequence ?? 0;
                    if (checkpoint.Sequence != tailSequence ||
                        (tailSequence == 0) != (previousHash is null))
                    {
                        throw new IOException(
                            "The closed audit spool prefix tail does not match its checkpoint.");
                    }
                    recovery = new AuditClosedSpoolRecovery.PrefixEnd(
                        new PrefixEndPosition(
                            this,
                            snapshotToken,
                            rotation,
                            boundary,
                            checkpoint,
                            tailSequence,
                            previousHash));
                }
                else
                {
                    recovery = new AuditClosedSpoolRecovery.ChainEnd(
                        new ChainEndPosition(
                            this,
                            snapshotToken,
                            checkpoint));
                }
                var resolution = new PendingResolution(
                    acquired,
                    snapshotToken,
                    recovery);
                acquired = [];
                return resolution;
        }
        finally
        {
            DisposeSegments(acquired);
        }
    }

    /// <summary>
    /// Returns the record immediately following a position created by this
    /// reader's current validated snapshot. No pathname is reopened.
    /// </summary>
    internal IAuditClosedSpoolRecordPosition? ReadNext(
        IAuditClosedSpoolRecordPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        lock (_gate)
        {
            ThrowIfDisposed();
            _ = RequireRecordPositionLocked(position, nameof(position));
            var snapshotToken = _snapshotToken
                ?? throw new IOException("The audit spool reader snapshot is unavailable.");

            var segmentNumber = position.Spool.Index;
            if (segmentNumber < 0 || segmentNumber >= _segments.Length ||
                _segments[segmentNumber].Descriptor.Identity != position.Spool)
            {
                throw new IOException(
                    "The audit spool position has no segment in this snapshot.");
            }

            var segment = _segments[segmentNumber];
            var offset = position.NextOffset;
            if (offset > segment.Descriptor.Length)
                throw new IOException("The audit spool position exceeds its closed segment.");
            if (offset == segment.Descriptor.Length)
            {
                segmentNumber++;
                if (segmentNumber == _segments.Length)
                    return null;
                segment = _segments[segmentNumber];
                offset = 0;
                if (segment.Descriptor.Length == 0)
                    return null;
            }

            return ReadRecord(
                segment,
                offset,
                snapshotToken,
                CheckedNext(position.Sequence),
                position.EventHash);
        }
    }

    /// <summary>
    /// Persists remote acknowledgment only from an exact record position in
    /// this reader's current retained snapshot.
    /// </summary>
    internal void Acknowledge(
        IAuditClosedSpoolRecordPosition position,
        string exportConfigurationIdentity)
    {
        ArgumentNullException.ThrowIfNull(position);
        lock (_gate)
        {
            ThrowIfDisposed();
            var owned = RequireRecordPositionLocked(position, nameof(position));
            _checkpointLease.Acknowledge(
                owned.Spool,
                owned.NextOffset,
                owned.Sequence,
                owned.EventId,
                exportConfigurationIdentity);
        }
    }

    /// <summary>
    /// Durably consumes the one retry granted by a changed configuration and
    /// returns the only capability that may settle that exact attempt.
    /// </summary>
    internal IAuditClosedSpoolConfigurationRetryAuthorization AuthorizeConfigurationRetry(
        IAuditClosedSpoolRecordPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        lock (_gate)
        {
            ThrowIfDisposed();
            var owned = RequireRecordPositionLocked(position, nameof(position));
            var storeAuthorization = _checkpointLease.AuthorizeConfigurationRetry(
                owned.Spool,
                owned.StartOffset,
                owned.NextOffset,
                owned.Sequence,
                owned.EventId,
                ExportConfigurationIdentity);
            return new ConfigurationRetryAuthorization(
                this,
                owned.SnapshotToken,
                owned,
                storeAuthorization);
        }
    }

    internal void AcknowledgeConfigurationRetry(
        IAuditClosedSpoolConfigurationRetryAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        lock (_gate)
        {
            ThrowIfDisposed();
            var owned = RequireConfigurationRetryAuthorizationLocked(
                authorization,
                nameof(authorization));
            _checkpointLease.AcknowledgeConfigurationRetry(
                owned.ConsumeStoreAuthorization());
        }
    }

    internal void PersistConfigurationRetryBlock(
        IAuditClosedSpoolConfigurationRetryAuthorization authorization,
        AuditExportFailureClass failureClass,
        string detailCode,
        string? responseDigest)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        lock (_gate)
        {
            ThrowIfDisposed();
            var owned = RequireConfigurationRetryAuthorizationLocked(
                authorization,
                nameof(authorization));
            _checkpointLease.PersistConfigurationRetryBlock(
                owned.ConsumeStoreAuthorization(),
                failureClass,
                detailCode,
                responseDigest);
        }
    }

    /// <summary>
    /// Persists a non-retryable transport outcome against only the exact next
    /// record represented by this reader's current snapshot.
    /// </summary>
    internal void PersistBlock(
        IAuditClosedSpoolRecordPosition position,
        AuditExportFailureClass failureClass,
        string detailCode,
        string? responseDigest,
        DateTimeOffset firstFailureUtc,
        string exportConfigurationIdentity)
    {
        ArgumentNullException.ThrowIfNull(position);
        lock (_gate)
        {
            ThrowIfDisposed();
            var owned = RequireRecordPositionLocked(position, nameof(position));
            _checkpointLease.PersistBlock(
                owned.Spool,
                owned.StartOffset,
                owned.Sequence,
                owned.EventId,
                failureClass,
                detailCode,
                responseDigest,
                firstFailureUtc,
                exportConfigurationIdentity);
        }
    }

    /// <summary>
    /// Persists completion only from this reader's live opaque end proof.
    /// </summary>
    internal void MarkChainComplete(IAuditClosedSpoolChainEndPosition endPosition)
    {
        ArgumentNullException.ThrowIfNull(endPosition);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (endPosition is not ChainEndPosition ownedEnd ||
                !ReferenceEquals(ownedEnd.Owner, this) ||
                _snapshotToken is null ||
                !ReferenceEquals(ownedEnd.SnapshotToken, _snapshotToken))
            {
                throw new ArgumentException(
                    "The closed-chain end proof does not belong to this reader snapshot.",
                    nameof(endPosition));
            }

            _checkpointLease.MarkChainComplete(ownedEnd.Checkpoint);
        }
    }

    private RecordPosition RequireRecordPositionLocked(
        IAuditClosedSpoolRecordPosition position,
        string parameterName)
    {
        if (position is not RecordPosition ownedPosition ||
            !ReferenceEquals(ownedPosition.Owner, this) ||
            _snapshotToken is null ||
            !ReferenceEquals(ownedPosition.SnapshotToken, _snapshotToken))
        {
            throw new ArgumentException(
                "The audit spool position does not belong to this reader snapshot.",
                parameterName);
        }
        return ownedPosition;
    }

    internal static AuditClosedSpoolPrefixTransitionState ConsumeCurrentPrefixEnd(
        IAuditClosedSpoolPrefixEndPosition position,
        IAuditLiveSpoolRotationPosition rotation,
        Guid expectedSupervisorBootId)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(rotation);
        var boundary = AuditLiveSpoolReader.RequirePendingRotation(
            rotation,
            expectedSupervisorBootId);
        if (position is not PrefixEndPosition owned)
        {
            throw new ArgumentException(
                "The closed audit spool prefix-end capability is not authentic.",
                nameof(position));
        }
        return owned.Owner.ConsumeCurrentPrefixEndLocked(
            owned,
            rotation,
            boundary);
    }

    private AuditClosedSpoolPrefixTransitionState ConsumeCurrentPrefixEndLocked(
        PrefixEndPosition position,
        IAuditLiveSpoolRotationPosition rotation,
        AuditSpoolSegmentIdentity boundary)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(position.Owner, this) ||
                _snapshotToken is null ||
                !ReferenceEquals(position.SnapshotToken, _snapshotToken) ||
                !ReferenceEquals(position.Rotation, rotation) ||
                position.ExclusiveLiveBoundary != boundary)
            {
                throw new ArgumentException(
                    "The closed audit spool prefix-end capability is stale or belongs to another transition.",
                    nameof(position));
            }

            var transition = _checkpointLease.WithOwnedCheckpoint(checkpoint =>
            {
                if (!ReferenceEquals(checkpoint, position.Checkpoint))
                {
                    throw new IOException(
                        "The audit export checkpoint changed after the closed prefix ended.");
                }
                return new AuditClosedSpoolPrefixTransitionState(
                    boundary,
                    position.TailSequence,
                    position.TailEventHash);
            });
            ReleaseSnapshot();
            return transition;
        }
    }

    private ConfigurationRetryAuthorization RequireConfigurationRetryAuthorizationLocked(
        IAuditClosedSpoolConfigurationRetryAuthorization authorization,
        string parameterName)
    {
        if (authorization is not ConfigurationRetryAuthorization owned ||
            !ReferenceEquals(owned.Owner, this) ||
            _snapshotToken is null ||
            !ReferenceEquals(owned.SnapshotToken, _snapshotToken) ||
            !ReferenceEquals(owned.Position.Owner, this) ||
            !ReferenceEquals(owned.Position.SnapshotToken, _snapshotToken))
        {
            throw new ArgumentException(
                "The configuration retry authorization does not belong to this reader snapshot.",
                parameterName);
        }
        return owned;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseSnapshot();
            _checkpointLease.Dispose();
        }
    }

    private void ReleaseExportPump()
    {
        lock (_gate)
            _exportPumpRetained = false;
    }

    private SegmentDescriptor[] InventoryClosedChain(
        AuditSpoolSegmentIdentity? exclusiveLiveBoundary,
        bool verifySegmentProtection = true)
    {
        SecureAuditStorage.VerifyExternalProtectedDirectory(_spoolRoot);
        var segments = new List<SegmentDescriptor>();
        var inventoryEntries = 0;
        var chainBytes = 0L;
        var boundaryObserved = false;
        foreach (var entry in new DirectoryInfo(_spoolRoot)
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            inventoryEntries = checked(inventoryEntries + 1);
            if (inventoryEntries > MaximumSpoolInventoryEntries)
            {
                throw new IOException(
                    "The audit spool inventory exceeds its recovery entry bound.");
            }

            if (entry is FileInfo quotaLock &&
                string.Equals(
                    quotaLock.Name,
                    AuditSpoolQuotaLease.ControlFileName,
                    StringComparison.Ordinal))
            {
                SecureAuditStorage.VerifyExternalProtectedFile(quotaLock.FullName);
                continue;
            }

            if (entry is not FileInfo file ||
                !AuditSpoolSegmentIdentity.TryParse(file.Name, out var identity))
            {
                throw new IOException("The audit spool contains an unknown entry.");
            }

            var isSelected = identity.SupervisorBootId == _supervisorBootId &&
                             (exclusiveLiveBoundary is null ||
                              identity.Index < exclusiveLiveBoundary.Value.Index);
            if (exclusiveLiveBoundary is { } boundary && identity == boundary)
                boundaryObserved = true;

            // A live boundary is intentionally held FileShare.None by its
            // writer. Do not open it, or any suffix segment, from this path.
            if (!isSelected &&
                exclusiveLiveBoundary is not null &&
                identity.SupervisorBootId == _supervisorBootId)
            {
                continue;
            }

            if (verifySegmentProtection)
                SecureAuditStorage.VerifyExternalProtectedFile(file.FullName);
            file.Refresh();
            if (isSelected)
            {
                if (segments.Count == MaximumClosedChainSegments)
                {
                    throw new IOException(
                        "The closed audit spool chain exceeds its recovery segment bound.");
                }
                if (file.Length < 0 || file.Length > _options.SegmentBytes)
                {
                    throw new IOException(
                        "A closed audit segment exceeds its configured bound.");
                }
                chainBytes = checked(chainBytes + file.Length);
                if (chainBytes > _options.AggregateBytes)
                {
                    throw new IOException(
                        "The closed audit spool chain exceeds its configured aggregate bound.");
                }
                segments.Add(new SegmentDescriptor(
                    identity,
                    file.FullName,
                    file.Length));
            }
        }

        var ordered = segments.OrderBy(segment => segment.Identity.Index).ToArray();
        if (exclusiveLiveBoundary is { } liveBoundary && !boundaryObserved)
        {
            throw new IOException(
                "The exclusive live audit segment boundary is absent from the spool inventory.");
        }
        if (ordered.Length == 0 &&
            (exclusiveLiveBoundary is null || exclusiveLiveBoundary.Value.Index != 0))
        {
            throw new IOException("A closed audit spool chain must begin at segment zero.");
        }
        if (ordered.Length > 0 && ordered[0].Identity.Index != 0)
            throw new IOException("A closed audit spool chain must begin at segment zero.");
        if (exclusiveLiveBoundary is { } prefixBoundary &&
            ordered.Length != prefixBoundary.Index)
        {
            throw new IOException(
                "The closed audit spool prefix has a missing segment.");
        }
        for (var index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].Identity.Index != index)
                throw new IOException("A closed audit spool chain has a missing segment.");
            if (ordered[index].Length == 0 &&
                (exclusiveLiveBoundary is not null || index != ordered.Length - 1))
            {
                throw new IOException(
                    exclusiveLiveBoundary is null
                        ? "Only the trailing closed audit segment may be empty."
                        : "A closed audit spool prefix contains an empty segment.");
            }
        }
        return ordered;
    }

    private SegmentHandle[] AcquireClosedChain(SegmentDescriptor[] inventory)
    {
        var acquired = new List<SegmentHandle>(inventory.Length);
        var identities = new HashSet<ProtectedFileIdentity>();
        try
        {
            foreach (var segment in inventory)
            {
                FileStream stream;
                try
                {
                    stream = new FileStream(
                        segment.Path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.RandomAccess);
                }
                catch (IOException exception)
                {
                    throw new IOException(
                        "The selected audit spool chain has a live or unavailable segment.",
                        exception);
                }

                try
                {
                    if (stream.Length != segment.Length)
                        throw new IOException("A closed audit segment changed length.");
                    var identity = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
                        segment.Path,
                        stream.SafeFileHandle);
                    if (!identities.Add(identity))
                    {
                        throw new IOException(
                            "Two closed audit segment names identify the same file.");
                    }
                    acquired.Add(new SegmentHandle(segment, stream, identity));
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            return acquired.ToArray();
        }
        catch
        {
            DisposeSegments(acquired);
            throw;
        }
    }

    private static void VerifyRetainedIdentities(SegmentHandle[] segments)
    {
        var identities = new HashSet<ProtectedFileIdentity>();
        foreach (var segment in segments)
        {
            var observed = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
                segment.Descriptor.Path,
                segment.Stream.SafeFileHandle);
            if (observed != segment.Identity || !identities.Add(observed))
            {
                throw new IOException(
                    "A closed audit segment path changed identity while it was acquired.");
            }
        }
    }

    private RecordPosition ReadRecord(
        SegmentHandle segment,
        long startOffset,
        object snapshotToken,
        long expectedSequence,
        string? expectedPreviousHash)
    {
        if (startOffset < 0 || startOffset >= segment.Descriptor.Length)
            throw new IOException("The audit spool record offset is invalid.");
        if (segment.Stream.Length != segment.Descriptor.Length)
            throw new IOException("A closed audit segment changed length.");

        var available = checked((int)Math.Min(
            _options.MaxRecordBytes,
            segment.Descriptor.Length - startOffset));
        var buffer = new byte[_options.MaxRecordBytes];
        var length = 0;
        while (length < available)
        {
            var read = RandomAccess.Read(
                segment.Stream.SafeFileHandle,
                buffer.AsSpan(length, available - length),
                startOffset + length);
            if (read == 0)
                break;

            var lfIndex = buffer.AsSpan(length, read).IndexOf((byte)'\n');
            if (lfIndex >= 0)
            {
                length += lfIndex + 1;
                var parsed = AuditSpoolRecordCodec.Parse(
                    buffer.AsSpan(0, length - 1),
                    _supervisorBootId);
                if (parsed.Sequence != expectedSequence ||
                    !string.Equals(
                        parsed.PreviousEventHash,
                        expectedPreviousHash,
                        StringComparison.Ordinal))
                {
                    throw new IOException(
                        "The closed audit spool hash chain is discontinuous.");
                }

                var exactLine = new byte[length];
                buffer.AsSpan(0, length).CopyTo(exactLine);
                return new RecordPosition(
                    this,
                    snapshotToken,
                    segment.Descriptor.Identity,
                    startOffset,
                    checked(startOffset + length),
                    parsed,
                    exactLine);
            }
            length += read;
        }

        if (length == _options.MaxRecordBytes)
        {
            throw new IOException(
                "A closed audit record has no LF within its configured bound.");
        }
        throw new IOException("A closed audit segment has a torn record.");
    }

    private void VerifyStableInventory(
        SegmentDescriptor[] expected,
        AuditSpoolSegmentIdentity? exclusiveLiveBoundary)
    {
        // Windows no-share handles pin each segment name but may also prevent
        // a second ACL open. The initial inventory verified the ACL before
        // acquisition; retained handle metadata verifies the pinned file.
        var observed = InventoryClosedChain(
            exclusiveLiveBoundary,
            verifySegmentProtection: !OperatingSystem.IsWindows());
        if (expected.Length != observed.Length)
            throw new IOException("The closed audit spool inventory changed while it was acquired.");
        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index].Identity != observed[index].Identity ||
                expected[index].Length != observed[index].Length ||
                !PathsEqual(expected[index].Path, observed[index].Path))
            {
                throw new IOException(
                    "The closed audit spool inventory changed while it was acquired.");
            }
        }
    }

    private void ReleaseSnapshot()
    {
        _snapshotToken = null;
        var segments = _segments;
        _segments = [];
        DisposeSegments(segments);
    }

    private static void DisposeSegments(IEnumerable<SegmentHandle> segments)
    {
        foreach (var segment in segments)
            segment.Stream.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AuditClosedSpoolChainReader));
    }

    private static long CheckedNext(long sequence)
    {
        if (sequence == long.MaxValue)
            throw new IOException("The closed audit spool sequence overflows.");
        return sequence + 1;
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private readonly record struct SegmentDescriptor(
        AuditSpoolSegmentIdentity Identity,
        string Path,
        long Length);

    private sealed record SegmentHandle(
        SegmentDescriptor Descriptor,
        FileStream Stream,
        ProtectedFileIdentity Identity);

    private sealed class PendingResolution(
        SegmentHandle[] segments,
        object snapshotToken,
        AuditClosedSpoolRecovery recovery) : IDisposable
    {
        private SegmentHandle[] _segments = segments;

        internal SegmentHandle[] Segments => _segments;

        internal object SnapshotToken { get; } = snapshotToken;

        internal AuditClosedSpoolRecovery Recovery { get; } = recovery;

        public void Dispose()
        {
            var current = _segments;
            _segments = [];
            DisposeSegments(current);
        }
    }

    /// <summary>
    /// An opaque position in the reader's current validated snapshot.
    /// ExactJsonlBytes includes the authoritative trailing LF.
    /// </summary>
    private sealed class RecordPosition : IAuditClosedSpoolRecordPosition
    {
        private readonly byte[] _exactJsonlBytes;

        internal RecordPosition(
            AuditClosedSpoolChainReader owner,
            object snapshotToken,
            AuditSpoolSegmentIdentity spool,
            long startOffset,
            long nextOffset,
            AuditSpoolRecord record,
            byte[] exactJsonlBytes)
        {
            Owner = owner;
            SnapshotToken = snapshotToken;
            _exactJsonlBytes = exactJsonlBytes;
            Spool = spool;
            StartOffset = startOffset;
            NextOffset = nextOffset;
            Sequence = record.Sequence;
            EventId = record.EventId;
            PreviousEventHash = record.PreviousEventHash;
            EventHash = record.EventHash;
        }

        internal AuditClosedSpoolChainReader Owner { get; }

        internal object SnapshotToken { get; }

        public AuditSpoolSegmentIdentity Spool { get; }

        public long StartOffset { get; }

        public long NextOffset { get; }

        public long Sequence { get; }

        public Guid EventId { get; }

        public string? PreviousEventHash { get; }

        public string EventHash { get; }

        public ReadOnlyMemory<byte> ExactJsonlBytes => _exactJsonlBytes;
    }

    private sealed class ChainEndPosition : IAuditClosedSpoolChainEndPosition
    {
        internal ChainEndPosition(
            AuditClosedSpoolChainReader owner,
            object snapshotToken,
            AuditExportCheckpoint checkpoint)
        {
            Owner = owner;
            SnapshotToken = snapshotToken;
            Checkpoint = checkpoint;
        }

        internal AuditClosedSpoolChainReader Owner { get; }

        internal object SnapshotToken { get; }

        internal AuditExportCheckpoint Checkpoint { get; }
    }

    private sealed class ConfigurationRetryAuthorization :
        IAuditClosedSpoolConfigurationRetryAuthorization
    {
        private AuditExportCheckpointStore.ClosedConfigurationRetryAuthorization?
            _storeAuthorization;

        internal ConfigurationRetryAuthorization(
            AuditClosedSpoolChainReader owner,
            object snapshotToken,
            RecordPosition position,
            AuditExportCheckpointStore.ClosedConfigurationRetryAuthorization storeAuthorization)
        {
            Owner = owner;
            SnapshotToken = snapshotToken;
            Position = position;
            _storeAuthorization = storeAuthorization;
        }

        internal AuditClosedSpoolChainReader Owner { get; }

        internal object SnapshotToken { get; }

        internal RecordPosition Position { get; }

        internal AuditExportCheckpointStore.ClosedConfigurationRetryAuthorization
            ConsumeStoreAuthorization() =>
            Interlocked.Exchange(ref _storeAuthorization, null)
            ?? throw new InvalidOperationException(
                "The configuration retry authorization has already been consumed.");
    }

    private sealed class ExportPumpLease(
        AuditClosedSpoolChainReader owner) : IDisposable
    {
        private AuditClosedSpoolChainReader? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseExportPump();
        }
    }

    private sealed class PrefixEndPosition : IAuditClosedSpoolPrefixEndPosition
    {
        internal PrefixEndPosition(
            AuditClosedSpoolChainReader owner,
            object snapshotToken,
            IAuditLiveSpoolRotationPosition rotation,
            AuditSpoolSegmentIdentity exclusiveLiveBoundary,
            AuditExportCheckpoint checkpoint,
            long tailSequence,
            string? tailEventHash)
        {
            Owner = owner;
            SnapshotToken = snapshotToken;
            Rotation = rotation;
            ExclusiveLiveBoundary = exclusiveLiveBoundary;
            Checkpoint = checkpoint;
            TailSequence = tailSequence;
            TailEventHash = tailEventHash;
        }

        internal AuditClosedSpoolChainReader Owner { get; }

        internal object SnapshotToken { get; }

        internal IAuditLiveSpoolRotationPosition Rotation { get; }

        internal AuditSpoolSegmentIdentity ExclusiveLiveBoundary { get; }

        internal AuditExportCheckpoint Checkpoint { get; }

        internal long TailSequence { get; }

        internal string? TailEventHash { get; }
    }
}

internal abstract class AuditClosedSpoolRecovery
{
    private AuditClosedSpoolRecovery()
    {
    }

    internal sealed class Record : AuditClosedSpoolRecovery
    {
        internal Record(
            IAuditClosedSpoolRecordPosition position,
            AuditExportBlockedRecord? blockedRecord)
        {
            ArgumentNullException.ThrowIfNull(position);
            Position = position;
            BlockedRecord = blockedRecord;
        }

        internal IAuditClosedSpoolRecordPosition Position { get; }

        internal AuditExportBlockedRecord? BlockedRecord { get; }
    }

    internal sealed class PrefixEnd : AuditClosedSpoolRecovery
    {
        internal PrefixEnd(IAuditClosedSpoolPrefixEndPosition position)
        {
            ArgumentNullException.ThrowIfNull(position);
            Position = position;
        }

        internal IAuditClosedSpoolPrefixEndPosition Position { get; }
    }

    internal sealed class ChainEnd : AuditClosedSpoolRecovery
    {
        internal ChainEnd(IAuditClosedSpoolChainEndPosition position)
        {
            ArgumentNullException.ThrowIfNull(position);
            Position = position;
        }

        internal IAuditClosedSpoolChainEndPosition Position { get; }
    }
}
