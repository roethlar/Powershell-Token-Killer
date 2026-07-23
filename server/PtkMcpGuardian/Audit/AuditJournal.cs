namespace PtkMcpServer.Audit;

internal enum AuditSinkFaultPoint
{
    BeforeReserve,
    BeforeAppend,
    AfterAppend,
    Flush,
}

internal interface IAuditJournalSink : IDisposable
{
    long SegmentCapacityBytes { get; }

    long AggregateCapacityBytes { get; }

    long CurrentSegmentBytes { get; }

    long TotalBytes { get; }

    bool CanReserve(long reservedBytes);

    void Append(ReadOnlyMemory<byte> line);

    void FlushToDisk();
}

/// <summary>
/// Deterministic sink for journal tests. It preserves the exact byte array
/// handed to Append and can fail immediately before append, after the bytes
/// become visible, or during the following durable flush boundary.
/// </summary>
internal sealed class InMemoryAuditJournalSink : IAuditJournalSink
{
    private readonly object _gate = new();
    private readonly Func<AuditSinkFaultPoint, int, bool>? _faultInjector;
    private readonly List<byte[]> _lines = [];
    private long _totalBytes;
    private int _appendCallCount;
    private int _flushCallCount;
    private int _reserveCallCount;
    private bool _disposed;

    internal InMemoryAuditJournalSink(
        long segmentCapacityBytes,
        long aggregateCapacityBytes,
        AuditProtectionMode protectionMode,
        TimeSpan retentionAge,
        Func<AuditSinkFaultPoint, int, bool>? faultInjector = null)
    {
        if (segmentCapacityBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(segmentCapacityBytes));
        if (aggregateCapacityBytes < segmentCapacityBytes)
            throw new ArgumentOutOfRangeException(nameof(aggregateCapacityBytes));
        if (!Enum.IsDefined(protectionMode))
            throw new ArgumentOutOfRangeException(nameof(protectionMode));
        if (retentionAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retentionAge));

        SegmentCapacityBytes = segmentCapacityBytes;
        AggregateCapacityBytes = aggregateCapacityBytes;
        _faultInjector = faultInjector;
    }

    internal IReadOnlyList<byte[]> Lines
    {
        get
        {
            lock (_gate)
            {
                return _lines.ToArray();
            }
        }
    }

    internal int AppendCallCount
    {
        get
        {
            lock (_gate)
            {
                return _appendCallCount;
            }
        }
    }

    internal int FlushCallCount
    {
        get
        {
            lock (_gate)
            {
                return _flushCallCount;
            }
        }
    }

    public long SegmentCapacityBytes { get; }

    public long AggregateCapacityBytes { get; }

    public long CurrentSegmentBytes
    {
        get
        {
            lock (_gate)
            {
                return _totalBytes;
            }
        }
    }

    public long TotalBytes
    {
        get
        {
            lock (_gate)
            {
                return _totalBytes;
            }
        }
    }

    public bool CanReserve(long reservedBytes)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var call = checked(++_reserveCallCount);
            if (_faultInjector?.Invoke(AuditSinkFaultPoint.BeforeReserve, call) == true)
                throw new IOException("Injected reservation fault.");
            return reservedBytes >= 0 &&
                   _totalBytes <= SegmentCapacityBytes - reservedBytes &&
                   _totalBytes <= AggregateCapacityBytes - reservedBytes;
        }
    }

    public void Append(ReadOnlyMemory<byte> line)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var call = checked(++_appendCallCount);
            if (_faultInjector?.Invoke(AuditSinkFaultPoint.BeforeAppend, call) == true)
                throw new IOException("Injected append fault.");

            var copy = line.ToArray();
            _lines.Add(copy);
            _totalBytes = checked(_totalBytes + copy.Length);

            if (_faultInjector?.Invoke(AuditSinkFaultPoint.AfterAppend, call) == true)
                throw new IOException("Injected ambiguous append fault.");
        }
    }

    public void FlushToDisk()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var call = checked(++_flushCallCount);
            if (_faultInjector?.Invoke(AuditSinkFaultPoint.Flush, call) == true)
                throw new IOException("Injected flush fault.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class AuditUnavailableException : IOException
{
    internal AuditUnavailableException()
        : base("Required audit persistence failed.")
    {
    }
}

/// <summary>
/// A transferable claim on worst-case record capacity. Every append consumes
/// exactly one slot. Releasing the lease returns only its still-unused slots;
/// a transferred slot remains charged independently for a later job/worker
/// terminal fact.
/// </summary>
internal sealed class AuditReservation : IDisposable
{
    private readonly AuditJournal _owner;
    private int _remainingSlots;
    private bool _released;

    internal AuditReservation(AuditJournal owner, int remainingSlots)
    {
        _owner = owner;
        _remainingSlots = remainingSlots;
    }

    internal int RemainingSlots
    {
        get
        {
            lock (_owner.SyncRoot)
            {
                return _remainingSlots;
            }
        }
    }

    internal AuditReservation TransferSlot() => _owner.TransferSlot(this);

    internal void Release() => _owner.Release(this);

    public void Dispose() => Release();

    internal void EnsureUsableLocked(AuditJournal owner)
    {
        if (!ReferenceEquals(owner, _owner))
            throw new InvalidOperationException("The audit reservation belongs to another journal.");
        if (_released || _remainingSlots < 1)
            throw new InvalidOperationException("The audit reservation has no remaining slots.");
    }

    internal void ConsumeOneLocked()
    {
        _remainingSlots--;
        if (_remainingSlots == 0)
            _released = true;
    }

    internal void TransferOneLocked()
    {
        _remainingSlots--;
        if (_remainingSlots == 0)
            _released = true;
    }

    internal int ReleaseAllLocked()
    {
        if (_released)
            return 0;
        var released = _remainingSlots;
        _remainingSlots = 0;
        _released = true;
        return released;
    }
}

/// <summary>
/// Supervisor-owned, single-writer audit journal. Admission reserves complete
/// maximum-record obligations atomically; a successful exact-byte append and
/// flush advances sequence/hash state and converts one maximum reservation to
/// the record's actual byte length. Any ambiguous sink failure permanently
/// poisons this journal instance.
/// </summary>
internal sealed class AuditJournal : IDisposable
{
    private readonly object _gate = new();
    private readonly AuditOptions _options;
    private readonly IAuditJournalSink _sink;
    private readonly string _producerVersion;
    private readonly string? _binaryDigest;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<DateTimeOffset, Guid> _uuidV7Factory;
    private readonly IAuditHostSnapshotSource? _hostSnapshots;
    private long _sequence;
    private string? _previousEventHash;
    private Guid? _lastEventId;
    private long _reservedSlots;
    private bool _poisoned;
    private bool _disposed;
    private bool _recoveryCallbackActive;
    private string? _deferredRecoveryFailureClass;
    private long _lastKnownTotalBytes;

    internal AuditJournal(
        AuditOptions options,
        AuditHealth health,
        IAuditJournalSink sink,
        string producerVersion,
        string? binaryDigest,
        Guid hostId,
        Guid supervisorBootId,
        Func<DateTimeOffset>? utcNow = null,
        Func<DateTimeOffset, Guid>? uuidV7Factory = null,
        IAuditHostSnapshotSource? hostSnapshots = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);
        if (sink.SegmentCapacityBytes != options.SegmentBytes ||
            sink.AggregateCapacityBytes != options.AggregateBytes)
        {
            throw new ArgumentException("Audit sink capacity does not match the frozen audit options.", nameof(sink));
        }

        _options = options;
        Health = health;
        _sink = sink;
        _producerVersion = producerVersion;
        _binaryDigest = binaryDigest;
        HostId = hostId;
        SupervisorBootId = supervisorBootId;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _uuidV7Factory = uuidV7Factory ?? Guid.CreateVersion7;
        _hostSnapshots = hostSnapshots;
        _lastKnownTotalBytes = _sink.TotalBytes;
        UpdateHealthMetricsLocked();
    }

    internal object SyncRoot => _gate;

    internal AuditHealth Health { get; }

    internal Guid HostId { get; }

    internal Guid SupervisorBootId { get; }

    internal AuditOptions Options => _options;

    internal long Sequence
    {
        get
        {
            lock (_gate)
                return _sequence;
        }
    }

    internal Guid? LastEventId
    {
        get
        {
            lock (_gate)
                return _lastEventId;
        }
    }

    internal bool IsPoisoned
    {
        get
        {
            lock (_gate)
                return _poisoned;
        }
    }

    internal long ReservedBytes
    {
        get
        {
            lock (_gate)
                return ReservedBytesLocked();
        }
    }

    internal long CurrentSegmentBytes
    {
        get
        {
            lock (_gate)
                return _sink.CurrentSegmentBytes;
        }
    }

    /// <summary>
    /// Copies only the current segment prefix whose flush-to-disk boundary
    /// completed. The journal gate spans the writer identity, durable-tail,
    /// and positional read checks; parsing and network work happen later. A
    /// later successful close flush, or bytes surviving a hard crash, can make
    /// a complete record beyond this live watermark recoverable evidence. The
    /// closed-segment reader retains such records and unclosed-event recovery
    /// supplies their outcome ambiguity; audit bytes are never truncated just
    /// because the original writer did not observe its flush completing.
    /// </summary>
    internal AuditCommittedSpoolRead ReadCommittedSpool(
        AuditSpoolSegmentIdentity identity,
        long offset,
        int maximumBytes)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (maximumBytes < 1 || maximumBytes > _options.MaxRecordBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        var buffer = new byte[maximumBytes];
        AuditCommittedSpoolReadStatus status;
        AuditSpoolSegmentIdentity? currentSegment;
        int bytesRead;
        long committedTail;
        lock (_gate)
        {
            if (_sink is not IAuditCommittedSpoolSource source)
            {
                return new AuditCommittedSpoolRead(
                    AuditCommittedSpoolReadStatus.NotCurrent,
                    ReadOnlyMemory<byte>.Empty,
                    0,
                    CurrentSegment: null);
            }

            currentSegment = source.CurrentSegmentIdentity;
            status = source.TryReadCommitted(
                identity,
                offset,
                buffer,
                out bytesRead,
                out committedTail);
        }
        if (bytesRead == 0)
        {
            return new AuditCommittedSpoolRead(
                status,
                ReadOnlyMemory<byte>.Empty,
                committedTail,
                currentSegment);
        }
        if (bytesRead != buffer.Length) Array.Resize(ref buffer, bytesRead);
        return new AuditCommittedSpoolRead(
            status,
            buffer,
            committedTail,
            currentSegment);
    }

    /// <summary>
    /// Captures a complete retained-journal evidence-reference proof while
    /// holding the authoritative writer gate. Callers must already own the
    /// evidence publication/quota lease; the scanner then acquires global
    /// spool topology, establishing evidence -&gt; journal -&gt; spool order.
    /// </summary>
    internal AuditEvidenceReferenceScan ScanRetainedEvidenceReferences(
        IReadOnlySet<AuditEvidenceIdentity> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_gate)
        {
            if (_disposed ||
                _sink is not IAuditCommittedSpoolSource source)
            {
                return AuditEvidenceReferenceScan.Incomplete;
            }
            return AuditEvidenceSpoolScanner.CaptureUnderJournalGate(
                _options,
                source,
                candidates);
        }
    }

    internal bool TryReserve(
        int maxRecordSlots,
        out AuditReservation? reservation,
        out string? failureClass)
    {
        if (maxRecordSlots < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRecordSlots));

        lock (_gate)
        {
            if (_disposed)
                return Refuse(out reservation, out failureClass, "journal.closed");
            if (_poisoned)
                return Refuse(out reservation, out failureClass, "journal.poisoned");
            var health = Health.Snapshot();
            if (health.State == AuditHealthState.Unavailable)
            {
                if (!string.Equals(health.FailureClass, "journal.capacity", StringComparison.Ordinal) ||
                    !TryRecoverLocked(maxRecordSlots))
                {
                    return Refuse(
                        out reservation,
                        out failureClass,
                        health.FailureClass ?? "journal.unavailable");
                }
            }

            long requestedBytes;
            long reservedAfter;
            try
            {
                requestedBytes = checked((long)maxRecordSlots * _options.MaxRecordBytes);
                reservedAfter = checked(
                    ReservedBytesLocked() + requestedBytes + _options.EmergencyReserveBytes);
            }
            catch (OverflowException)
            {
                EnterUnavailableLocked("journal.capacity");
                return Refuse(out reservation, out failureClass, "journal.capacity");
            }

            try
            {
                if (!_sink.CanReserve(reservedAfter))
                {
                    EnterUnavailableLocked("journal.capacity");
                    return Refuse(out reservation, out failureClass, "journal.capacity");
                }
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // Reservation/quota/rotation work is admission-only. A
                // failure here must close new admission without destroying
                // the already-preallocated append path held by existing call
                // and job leases; their own Append will poison the journal if
                // the authoritative writer is actually unusable.
                EnterUnavailableLocked("journal.storage");
                return Refuse(out reservation, out failureClass, "journal.storage");
            }

            _reservedSlots = checked(_reservedSlots + maxRecordSlots);
            reservation = new AuditReservation(this, maxRecordSlots);
            failureClass = null;
            UpdateHealthMetricsLocked();
            return true;
        }
    }

    /// <summary>
    /// Closes an unavailable interval caused by a dependency that has already
    /// passed its own recovery probe. Recovery is admitted only for the exact
    /// current failure class so a stale caller cannot clear a newer outage.
    /// </summary>
    internal bool TryRecoverExternal(string expectedFailureClass, int upcomingRecordSlots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFailureClass);
        if (upcomingRecordSlots < 1)
            throw new ArgumentOutOfRangeException(nameof(upcomingRecordSlots));

        lock (_gate)
        {
            var health = Health.Snapshot();
            if (health.State is not (AuditHealthState.Degraded or AuditHealthState.Unavailable) ||
                !string.Equals(
                    health.FailureClass,
                    expectedFailureClass,
                    StringComparison.Ordinal))
            {
                return false;
            }

            return TryRecoverLocked(upcomingRecordSlots);
        }
    }

    /// <summary>
    /// Persists the degraded transition for a supervisor dependency while the
    /// journal itself is still writable, then closes admission. The dependency
    /// failure never receives an ordinary call reservation or script data.
    /// </summary>
    internal void EnterExternalUnavailable(string failureClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureClass);
        lock (_gate)
        {
            if (_disposed || _poisoned) return;
            EnterUnavailableLocked(failureClass);
        }
    }

    internal SerializedAuditEvent Append(AuditReservation reservation, AuditEventInput input)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(input);

        lock (_gate)
        {
            ThrowIfClosedOrPoisonedLocked();
            reservation.EnsureUsableLocked(this);

            var occurredUtc = GetUtcNowLocked();
            var observedUtc = GetUtcNowLocked();
            var eventId = CreateUuidV7Locked(observedUtc);
            var nextSequence = checked(_sequence + 1);
            var producer = new AuditProducerContext(
                HostId,
                SupervisorBootId,
                WorkerBootId: null,
                Pid: Environment.ProcessId,
                _producerVersion,
                _binaryDigest);
            var serialized = _hostSnapshots is null
                ? AuditEventSerializer.Serialize(
                    nextSequence,
                    _previousEventHash,
                    producer,
                    input,
                    eventId,
                    occurredUtc,
                    observedUtc)
                : AuditEventSerializer.SerializeVersion3(
                    nextSequence,
                    _previousEventHash,
                    producer,
                    _hostSnapshots.Capture() ?? throw new AuditEventValidationException(
                        "The guardian audit host snapshot source returned null."),
                    input,
                    eventId,
                    occurredUtc,
                    observedUtc);

            if (serialized.Utf8Line.Length > _options.MaxRecordBytes)
            {
                throw new AuditEventValidationException(
                    $"Serialized audit record exceeds configured maximum {_options.MaxRecordBytes} bytes.");
            }

            try
            {
                _sink.Append(serialized.Utf8Line);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                PoisonLocked("journal.append");
                throw new AuditUnavailableException();
            }

            try
            {
                _sink.FlushToDisk();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                PoisonLocked("journal.flush");
                throw new AuditUnavailableException();
            }

            reservation.ConsumeOneLocked();
            _reservedSlots--;
            _sequence = nextSequence;
            _previousEventHash = serialized.EventHash;
            _lastEventId = serialized.EventId;
            UpdateHealthMetricsLocked();
            return serialized;
        }
    }

    /// <summary>
    /// Persists one bounded supervisor-generated transition from the emergency
    /// capacity excluded from every ordinary reservation. This path must be
    /// used only for automatic containment/health facts: it deliberately does
    /// not consult admission or rotation quota again, because those facts must
    /// remain writable at high water. Any failure closes this journal instance.
    /// </summary>
    internal SerializedAuditEvent AppendAutomaticTransition(AuditEventInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        lock (_gate)
        {
            ThrowIfClosedOrPoisonedLocked();
            _reservedSlots = checked(_reservedSlots + 1);
            var transition = new AuditReservation(this, 1);
            try
            {
                return Append(transition, input);
            }
            catch (AuditUnavailableException)
            {
                // Append already recorded the authoritative append/flush
                // failure and poisoned this journal.
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                PoisonLocked("journal.transition");
                throw new AuditUnavailableException();
            }
            finally
            {
                transition.Release();
            }
        }
    }

    internal Guid CreateCallId()
    {
        lock (_gate)
        {
            ThrowIfClosedOrPoisonedLocked();
            return CreateUuidV7Locked(GetUtcNowLocked());
        }
    }

    internal AuditReservation TransferSlot(AuditReservation source)
    {
        lock (_gate)
        {
            ThrowIfClosedOrPoisonedLocked();
            source.EnsureUsableLocked(this);
            source.TransferOneLocked();
            return new AuditReservation(this, 1);
        }
    }

    internal void Release(AuditReservation reservation)
    {
        lock (_gate)
        {
            var released = reservation.ReleaseAllLocked();
            if (released == 0)
                return;
            _reservedSlots -= released;
            if (!_disposed)
                UpdateHealthMetricsLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _sink.Dispose();
        }
    }

    private Guid CreateUuidV7Locked(DateTimeOffset timestamp)
    {
        var value = _uuidV7Factory(timestamp);
        var text = value.ToString("D");
        if (text[14] != '7' || text[19] is not ('8' or '9' or 'a' or 'b'))
            throw new InvalidOperationException("The audit UUID factory must return RFC 4122 UUIDv7 values.");
        return value;
    }

    private DateTimeOffset GetUtcNowLocked()
    {
        var now = _utcNow();
        if (now.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("The audit clock must return UTC timestamps.");
        return now;
    }

    private void PoisonLocked(string failureClass)
    {
        _poisoned = true;
        if (_recoveryCallbackActive)
            _deferredRecoveryFailureClass ??= failureClass;
        else
            Health.MarkUnavailable(failureClass);
    }

    private void EnterUnavailableLocked(string failureClass)
    {
        var snapshot = Health.Snapshot();
        if (snapshot.State is AuditHealthState.Healthy or AuditHealthState.Recovered)
        {
            Health.MarkDegraded(failureClass);
            var degraded = Health.Snapshot();
            try
            {
                // Every ordinary reservation excluded the frozen emergency
                // reserve. Consume one of those preallocated slots directly;
                // consulting quota/rotation again could strand existing
                // obligations on a contended allocation lock.
                _reservedSlots = checked(_reservedSlots + 1);
                var transition = new AuditReservation(this, 1);
                try
                {
                    Append(transition, DegradedEvent(degraded));
                }
                finally
                {
                    transition.Release();
                }
            }
            catch (AuditUnavailableException)
            {
                // PoisonLocked recorded the authoritative persistence failure.
                return;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                PoisonLocked("journal.storage");
                return;
            }
        }
        Health.MarkUnavailable(failureClass);
    }

    private void UpdateHealthMetricsLocked()
    {
        try { _lastKnownTotalBytes = _sink.TotalBytes; }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Metrics are diagnostic, not the authoritative append. A failed
            // quota-directory scan must not retroactively invalidate a record
            // that was already appended and flushed through the held writer.
        }
        Health.UpdateStorageMetrics(
            _lastKnownTotalBytes,
            ReservedBytesLocked(),
            emergencyReserveBytes: _options.EmergencyReserveBytes);
    }

    /// <summary>
    /// Recovery consumes the separately held emergency record slot, persists
    /// the exact unavailable interval/probe summary, and only then reopens
    /// admission. The upcoming reservation is included in the same capacity
    /// check so recovery cannot immediately overbook its next call.
    /// </summary>
    private bool TryRecoverLocked(int upcomingRecordSlots)
    {
        if (_disposed || _poisoned) return false;

        bool recovered;
        _recoveryCallbackActive = true;
        _deferredRecoveryFailureClass = null;
        try
        {
            recovered = Health.TryRecover(
                summary =>
                {
                long requiredBytes;
                try
                {
                    requiredBytes = checked(
                        ReservedBytesLocked() +
                        ((long)upcomingRecordSlots + 1) * _options.MaxRecordBytes +
                        _options.EmergencyReserveBytes);
                }
                catch (OverflowException)
                {
                    return false;
                }

                try
                {
                    if (!_sink.CanReserve(requiredBytes)) return false;
                }
                    catch (Exception exception) when (!IsFatal(exception))
                    {
                        // Recovery stays closed, but a quota/rotation probe is
                        // not evidence that existing reserved terminals lost
                        // their append path.
                        return false;
                    }

                    _reservedSlots = checked(_reservedSlots + 1);
                    var recoveryReservation = new AuditReservation(this, 1);
                    try
                    {
                        Append(recoveryReservation, RecoveryEvent(summary));
                        return true;
                    }
                    catch (Exception exception) when (!IsFatal(exception))
                    {
                        recoveryReservation.Release();
                        return false;
                    }
                },
                out _);
        }
        finally
        {
            _recoveryCallbackActive = false;
        }

        if (_deferredRecoveryFailureClass is string recoveryFailure)
        {
            Health.MarkUnavailable(recoveryFailure);
            return false;
        }

        if (!recovered) return false;
        Health.MarkHealthy();
        return true;
    }

    private AuditEventInput RecoveryEvent(AuditRecoverySummary summary) => new()
    {
        EventType = "audit.recovered",
        Session = new AuditSession(),
        Actor = new AuditActor { AttributionStrength = "unknown" },
        Correlation = new AuditCorrelation { ParentEventId = _lastEventId },
        Request = new AuditRequest(),
        Routing = new AuditRouting(),
        Outcome = new AuditOutcome
        {
            State = "recovered",
            DetailCode = summary.FailureClass,
            TerminationCertainty = "not_applicable",
        },
        Coverage = new AuditCoverage
        {
            PtkRequest = false,
            RootProcessObserved = "not_applicable",
            DescendantsObserved = "not_applicable",
            RemoteEffectObserved = "not_applicable",
        },
        Audit = new AuditEventHealth
        {
            ProtectionMode = _options.ProtectionMode == AuditProtectionMode.LocalOnly
                ? "local-only"
                : "anchored",
            ExportConfigurationIdentity = _options.ExportConfigurationIdentity,
            HealthState = "recovered",
            FailureClass = summary.FailureClass,
            DegradedSinceUtc = summary.DegradedSinceUtc,
            EmergencyProbeCount = summary.EmergencyProbeCount,
            EmergencyProbeFirstUtc = summary.EmergencyProbeFirstUtc,
            EmergencyProbeLastUtc = summary.EmergencyProbeLastUtc,
        },
    };

    private AuditEventInput DegradedEvent(AuditHealthSnapshot snapshot) => new()
    {
        EventType = "audit.degraded",
        Session = new AuditSession(),
        Actor = new AuditActor { AttributionStrength = "unknown" },
        Correlation = new AuditCorrelation { ParentEventId = _lastEventId },
        Request = new AuditRequest(),
        Routing = new AuditRouting(),
        Outcome = new AuditOutcome
        {
            State = "degraded",
            DetailCode = snapshot.FailureClass,
            TerminationCertainty = "not_applicable",
        },
        Coverage = new AuditCoverage
        {
            PtkRequest = false,
            RootProcessObserved = "not_applicable",
            DescendantsObserved = "not_applicable",
            RemoteEffectObserved = "not_applicable",
        },
        Audit = new AuditEventHealth
        {
            ProtectionMode = _options.ProtectionMode == AuditProtectionMode.LocalOnly
                ? "local-only"
                : "anchored",
            ExportConfigurationIdentity = _options.ExportConfigurationIdentity,
            HealthState = "degraded",
            FailureClass = snapshot.FailureClass,
            DegradedSinceUtc = snapshot.DegradedSinceUtc,
        },
    };

    private long ReservedBytesLocked() => checked(_reservedSlots * _options.MaxRecordBytes);

    private void ThrowIfClosedOrPoisonedLocked()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AuditJournal));
        if (_poisoned)
            throw new AuditUnavailableException();
    }

    private static bool Refuse(
        out AuditReservation? reservation,
        out string? failureClass,
        string value)
    {
        reservation = null;
        failureClass = value;
        return false;
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
