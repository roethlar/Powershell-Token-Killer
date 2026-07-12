namespace PtkMcpServer.Audit;

internal interface IAuditLiveSpoolRecordPosition
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
/// Opaque proof that the authoritative live journal observed one exact
/// from/to segment rotation. It remains pending until a matching validated
/// closed-prefix proof consumes it.
/// </summary>
internal interface IAuditLiveSpoolRotationPosition;

internal enum AuditLiveSpoolPollKind
{
    Record,
    AtCommittedTail,
    Rotated,
    WriterClosed,
}

internal sealed record AuditLiveSpoolPoll(
    AuditLiveSpoolPollKind Kind,
    AuditSpoolSegmentIdentity ObservedCurrentSegment,
    IAuditLiveSpoolRecordPosition? Record = null,
    IAuditLiveSpoolRotationPosition? Rotation = null);

/// <summary>
/// Reads only the authoritative writer's published durable prefix. It never
/// interprets a live tail as completion. Rotation and writer closure are
/// explicit transitions for the higher-level source to reconcile through a
/// retained closed-prefix or full-chain snapshot.
/// </summary>
internal sealed class AuditLiveSpoolReader
{
    private readonly AuditJournal _journal;
    private readonly Guid _supervisorBootId;
    private readonly int _maximumRecordBytes;
    private readonly object _gate = new();
    private readonly object _generation = new();
    private AuditSpoolSegmentIdentity _currentSegment;
    private long _offset;
    private long _expectedSequence = 1;
    private string? _previousHash;
    private RecordPosition? _pending;
    private object _rotationGeneration = new();
    private RotationPosition? _pendingRotation;

    internal AuditLiveSpoolReader(AuditJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var options = journal.Options;
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
        {
            throw new ArgumentException(
                "A live export spool reader requires anchored audit protection.",
                nameof(journal));
        }

        _journal = journal;
        _supervisorBootId = journal.SupervisorBootId;
        _maximumRecordBytes = options.MaxRecordBytes;
        _currentSegment = AuditSpoolSegmentIdentity.Create(_supervisorBootId, 0);
    }

    internal AuditLiveSpoolPoll Poll()
    {
        lock (_gate)
        {
            if (_pending is { } pending)
            {
                return new AuditLiveSpoolPoll(
                    AuditLiveSpoolPollKind.Record,
                    _currentSegment,
                    pending);
            }
            if (_pendingRotation is { } pendingRotation)
            {
                return new AuditLiveSpoolPoll(
                    AuditLiveSpoolPollKind.Rotated,
                    pendingRotation.To,
                    Rotation: pendingRotation);
            }

            var read = _journal.ReadCommittedSpool(
                _currentSegment,
                _offset,
                _maximumRecordBytes);
            var observed = read.CurrentSegment ?? throw new IOException(
                "The audit journal has no committed-spool source identity.");
            if (observed.SupervisorBootId != _supervisorBootId)
                throw new IOException("The live audit spool source changed supervisor identity.");

            return read.Status switch
            {
                AuditCommittedSpoolReadStatus.Data => ReadRecord(read, observed),
                AuditCommittedSpoolReadStatus.AtCommittedTail => AtTail(read, observed),
                AuditCommittedSpoolReadStatus.Rotated => Rotated(read, observed),
                AuditCommittedSpoolReadStatus.WriterClosed => WriterClosed(read, observed),
                AuditCommittedSpoolReadStatus.NotCurrent => throw new IOException(
                    "The live audit spool source no longer recognizes its exact position."),
                _ => throw new IOException("The live audit spool source returned an unknown status."),
            };
        }
    }

    /// <summary>
    /// Advances the in-memory live cursor only after its caller has durably
    /// persisted remote acknowledgment through the checkpoint capability.
    /// </summary>
    internal void AdvanceAfterDurableAcknowledgment(
        IAuditLiveSpoolRecordPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        lock (_gate)
        {
            if (position is not RecordPosition owned ||
                !ReferenceEquals(owned.Owner, this) ||
                !ReferenceEquals(owned.Generation, _generation) ||
                !ReferenceEquals(owned, _pending))
            {
                throw new ArgumentException(
                    "The live audit spool position is not the exact pending record.",
                    nameof(position));
            }

            _currentSegment = owned.Spool;
            _offset = owned.NextOffset;
            _expectedSequence = CheckedNext(owned.Sequence);
            _previousHash = owned.EventHash;
            _pending = null;
        }
    }

    /// <summary>
    /// Consumes one exact pending rotation only after the retained closed
    /// prefix proves that its durable checkpoint reached the validated tail.
    /// </summary>
    internal void AdvanceAfterClosedPrefix(
        IAuditLiveSpoolRotationPosition rotation,
        IAuditClosedSpoolPrefixEndPosition prefixEnd)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        ArgumentNullException.ThrowIfNull(prefixEnd);
        _ = RequirePendingRotation(rotation, _supervisorBootId);
        var transition = AuditClosedSpoolChainReader.RequireCurrentPrefixEnd(
            prefixEnd,
            rotation,
            _supervisorBootId);
        var nextSequence = transition.TailSequence == 0
            ? 1
            : CheckedNext(transition.TailSequence);
        if ((transition.TailSequence == 0) != (transition.TailEventHash is null))
        {
            throw new IOException(
                "The closed audit spool prefix supplied an invalid hash-chain tail.");
        }

        lock (_gate)
        {
            if (rotation is not RotationPosition owned ||
                !ReferenceEquals(owned.Owner, this) ||
                !ReferenceEquals(owned, _pendingRotation) ||
                !ReferenceEquals(owned.Generation, _rotationGeneration) ||
                owned.From != _currentSegment ||
                owned.To != transition.Boundary ||
                _pending is not null)
            {
                throw new ArgumentException(
                    "The live audit spool rotation is no longer the exact pending transition.",
                    nameof(rotation));
            }

            _currentSegment = transition.Boundary;
            _offset = 0;
            _expectedSequence = nextSequence;
            _previousHash = transition.TailEventHash;
            _pendingRotation = null;
            _rotationGeneration = new object();
        }
    }

    internal static AuditSpoolSegmentIdentity RequirePendingRotation(
        IAuditLiveSpoolRotationPosition position,
        Guid expectedSupervisorBootId)
    {
        ArgumentNullException.ThrowIfNull(position);
        AuditSpoolSegmentIdentity.RequireUuidV4(
            expectedSupervisorBootId,
            nameof(expectedSupervisorBootId));
        if (position is not RotationPosition owned)
        {
            throw new ArgumentException(
                "The live audit spool rotation capability is not authentic.",
                nameof(position));
        }
        return owned.Owner.RequirePendingRotation(owned, expectedSupervisorBootId);
    }

    private AuditSpoolSegmentIdentity RequirePendingRotation(
        RotationPosition position,
        Guid expectedSupervisorBootId)
    {
        lock (_gate)
        {
            if (expectedSupervisorBootId != _supervisorBootId ||
                !ReferenceEquals(position.Owner, this) ||
                !ReferenceEquals(position, _pendingRotation) ||
                !ReferenceEquals(position.Generation, _rotationGeneration) ||
                position.From != _currentSegment ||
                position.To.SupervisorBootId != _supervisorBootId ||
                position.To.Index <= position.From.Index)
            {
                throw new ArgumentException(
                    "The live audit spool rotation capability is not the exact pending transition.",
                    nameof(position));
            }
            return position.To;
        }
    }

    private AuditLiveSpoolPoll ReadRecord(
        AuditCommittedSpoolRead read,
        AuditSpoolSegmentIdentity observed)
    {
        if (observed != _currentSegment ||
            read.Bytes.IsEmpty ||
            read.CommittedTail < checked(_offset + read.Bytes.Length))
        {
            throw new IOException("The live audit spool returned an invalid committed prefix.");
        }

        var lfIndex = read.Bytes.Span.IndexOf((byte)'\n');
        if (lfIndex < 0)
        {
            throw new IOException(
                read.Bytes.Length == _maximumRecordBytes
                    ? "A live audit record has no LF within its configured bound."
                    : "The live audit spool exposed a torn committed record.");
        }

        var length = lfIndex + 1;
        var exactLine = read.Bytes.Span[..length].ToArray();
        var parsed = AuditSpoolRecordCodec.Parse(
            exactLine.AsSpan(0, length - 1),
            _supervisorBootId);
        if (parsed.Sequence != _expectedSequence ||
            !string.Equals(
                parsed.PreviousEventHash,
                _previousHash,
                StringComparison.Ordinal))
        {
            throw new IOException("The live audit spool hash chain is discontinuous.");
        }

        var position = new RecordPosition(
            this,
            _generation,
            _currentSegment,
            _offset,
            checked(_offset + length),
            parsed,
            exactLine);
        _pending = position;
        return new AuditLiveSpoolPoll(
            AuditLiveSpoolPollKind.Record,
            observed,
            position);
    }

    private AuditLiveSpoolPoll AtTail(
        AuditCommittedSpoolRead read,
        AuditSpoolSegmentIdentity observed)
    {
        if (!read.Bytes.IsEmpty ||
            observed != _currentSegment ||
            read.CommittedTail != _offset)
        {
            throw new IOException("The live audit spool tail does not match its exact cursor.");
        }
        return new AuditLiveSpoolPoll(
            AuditLiveSpoolPollKind.AtCommittedTail,
            observed);
    }

    private AuditLiveSpoolPoll Rotated(
        AuditCommittedSpoolRead read,
        AuditSpoolSegmentIdentity observed)
    {
        if (!read.Bytes.IsEmpty || read.CommittedTail != 0 ||
            observed.Index <= _currentSegment.Index)
        {
            throw new IOException("The live audit spool returned an invalid rotation.");
        }
        var rotation = new RotationPosition(
            this,
            _rotationGeneration,
            _currentSegment,
            observed);
        _pendingRotation = rotation;
        return new AuditLiveSpoolPoll(
            AuditLiveSpoolPollKind.Rotated,
            observed,
            Rotation: rotation);
    }

    private AuditLiveSpoolPoll WriterClosed(
        AuditCommittedSpoolRead read,
        AuditSpoolSegmentIdentity observed)
    {
        if (!read.Bytes.IsEmpty || read.CommittedTail != 0 ||
            observed.Index < _currentSegment.Index)
        {
            throw new IOException("The closed audit writer returned an invalid final identity.");
        }
        _pendingRotation = null;
        _rotationGeneration = new object();
        return new AuditLiveSpoolPoll(AuditLiveSpoolPollKind.WriterClosed, observed);
    }

    private static long CheckedNext(long sequence)
    {
        if (sequence == long.MaxValue)
            throw new IOException("The live audit spool sequence overflows.");
        return sequence + 1;
    }

    private sealed class RecordPosition(
        AuditLiveSpoolReader owner,
        object generation,
        AuditSpoolSegmentIdentity spool,
        long startOffset,
        long nextOffset,
        AuditSpoolRecord parsed,
        byte[] exactJsonlBytes) : IAuditLiveSpoolRecordPosition
    {
        internal AuditLiveSpoolReader Owner { get; } = owner;

        internal object Generation { get; } = generation;

        public AuditSpoolSegmentIdentity Spool { get; } = spool;

        public long StartOffset { get; } = startOffset;

        public long NextOffset { get; } = nextOffset;

        public long Sequence { get; } = parsed.Sequence;

        public Guid EventId { get; } = parsed.EventId;

        public string? PreviousEventHash { get; } = parsed.PreviousEventHash;

        public string EventHash { get; } = parsed.EventHash;

        public ReadOnlyMemory<byte> ExactJsonlBytes { get; } = exactJsonlBytes;
    }

    private sealed class RotationPosition(
        AuditLiveSpoolReader owner,
        object generation,
        AuditSpoolSegmentIdentity from,
        AuditSpoolSegmentIdentity to) : IAuditLiveSpoolRotationPosition
    {
        internal AuditLiveSpoolReader Owner { get; } = owner;

        internal object Generation { get; } = generation;

        internal AuditSpoolSegmentIdentity From { get; } = from;

        internal AuditSpoolSegmentIdentity To { get; } = to;
    }
}
