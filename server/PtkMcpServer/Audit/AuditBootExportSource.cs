namespace PtkMcpServer.Audit;

internal enum AuditBootExportStepKind
{
    Advanced,
    Progressed,
    Idle,
    Complete,
    Retry,
    Blocked,
}

internal sealed record AuditBootExportStep(
    AuditBootExportStepKind Kind,
    Guid? EventId,
    string DetailCode,
    AuditExportFailureClass? FailureClass = null,
    TimeSpan? RetryAfter = null,
    bool HasHealthWarning = false);

internal enum AuditBootExportPhase
{
    Live,
    ClosedPrefix,
    WriterClosedChain,
    Complete,
}

/// <summary>
/// Coordinates export for one current supervisor boot without relinquishing
/// its checkpoint owner. Each call performs at most one remote attempt while
/// proof-bound readers handle live rotation and writer-closure transitions.
/// </summary>
internal sealed class AuditBootExportSource : IDisposable
{
    private readonly AuditOptions _options;
    private readonly Guid _supervisorBootId;
    private readonly AuditExportCheckpointStore _checkpointStore;
    private readonly IAuditOtlpExportTransport _transport;
    private readonly string _configurationIdentity;
    private readonly TimeProvider _timeProvider;
    private readonly IAuditExportAcknowledgmentObserver _acknowledgmentObserver;
    private readonly AuditLiveSpoolReader _live;

    private AuditBootExportPhase _phase = AuditBootExportPhase.Live;
    private AuditClosedSpoolChainReader? _closedReader;
    private AuditClosedSpoolExportPump? _closedPump;
    private IAuditLiveSpoolRotationPosition? _rotation;
    private AuditExportBlockedRecord? _liveBlocked;
    private bool _faulted;
    private int _lifecycle;

    internal AuditBootExportSource(
        AuditJournal journal,
        AuditExportCheckpointStore checkpointStore,
        IAuditOtlpExportTransport transport,
        TimeProvider? timeProvider = null)
        : this(
            journal,
            checkpointStore,
            transport,
            AuditExportAcknowledgmentObserver.None,
            timeProvider)
    {
    }

    internal AuditBootExportSource(
        AuditJournal journal,
        AuditExportCheckpointStore checkpointStore,
        IAuditOtlpExportTransport transport,
        IAuditExportAcknowledgmentObserver acknowledgmentObserver,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(acknowledgmentObserver);
        _options = journal.Options;
        _supervisorBootId = journal.SupervisorBootId;
        _checkpointStore = checkpointStore;
        _transport = transport;
        _configurationIdentity = transport.ConfigurationIdentity;
        if (!string.Equals(
                _options.ExportConfigurationIdentity,
                _configurationIdentity,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The current-boot export transport does not match the startup audit configuration.",
                nameof(transport));
        }
        _timeProvider = timeProvider ?? TimeProvider.System;
        _acknowledgmentObserver = acknowledgmentObserver;
        _live = new AuditLiveSpoolReader(journal, checkpointStore);
    }

    internal AuditBootExportPhase Phase => _phase;

    internal Guid SupervisorBootId => _supervisorBootId;

    internal bool UsesTransport(IAuditOtlpExportTransport transport) =>
        ReferenceEquals(_transport, transport);

    internal bool UsesOptions(AuditOptions options) =>
        ReferenceEquals(_options, options);

    internal bool UsesAcknowledgmentObserver(
        IAuditExportAcknowledgmentObserver acknowledgmentObserver) =>
        ReferenceEquals(_acknowledgmentObserver, acknowledgmentObserver);

    internal async Task<AuditBootExportStep> ExportNextAsync(
        CancellationToken cancellationToken)
    {
        var prior = Interlocked.CompareExchange(ref _lifecycle, 1, 0);
        if (prior == 2)
            throw new ObjectDisposedException(nameof(AuditBootExportSource));
        if (prior != 0)
        {
            throw new InvalidOperationException(
                "A current-boot audit export step is already running.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_faulted)
            {
                throw new IOException(
                    "The current-boot audit export source is faulted and requires checkpoint recovery.");
            }
            return _phase switch
            {
                AuditBootExportPhase.Live =>
                    await ExportLiveAsync(cancellationToken).ConfigureAwait(false),
                AuditBootExportPhase.ClosedPrefix or
                    AuditBootExportPhase.WriterClosedChain =>
                    await ExportClosedAsync(cancellationToken).ConfigureAwait(false),
                AuditBootExportPhase.Complete =>
                    Complete(eventId: null, "chain.complete"),
                _ => throw new IOException("The current-boot audit export phase is invalid."),
            };
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _lifecycle, 0, 1) != 1)
            {
                throw new InvalidOperationException(
                    "The current-boot audit export lifecycle is invalid.");
            }
        }
    }

    private async Task<AuditBootExportStep> ExportLiveAsync(
        CancellationToken cancellationToken)
    {
        var poll = _live.Poll();
        switch (poll.Kind)
        {
            case AuditLiveSpoolPollKind.AtCommittedTail:
                return new AuditBootExportStep(
                    AuditBootExportStepKind.Idle,
                    EventId: null,
                    "live.tail");
            case AuditLiveSpoolPollKind.Rotated:
                StartClosedPrefix(
                    poll.Rotation ?? throw new IOException(
                        "The live audit rotation has no transition capability."));
                return await ExportClosedAsync(cancellationToken).ConfigureAwait(false);
            case AuditLiveSpoolPollKind.WriterClosed:
                StartWriterClosedChain(
                    poll.WriterClosed ?? throw new IOException(
                        "The closed audit writer has no transition capability."));
                return await ExportClosedAsync(cancellationToken).ConfigureAwait(false);
            case AuditLiveSpoolPollKind.Record:
                return await ExportLiveRecordAsync(
                    poll.Record ?? throw new IOException(
                        "The live audit record poll has no exact position."),
                    cancellationToken).ConfigureAwait(false);
            default:
                throw new IOException("The live audit spool returned an unknown transition.");
        }
    }

    private async Task<AuditBootExportStep> ExportLiveRecordAsync(
        IAuditLiveSpoolRecordPosition position,
        CancellationToken cancellationToken)
    {
        if (_liveBlocked is { } blocked)
        {
            return new AuditBootExportStep(
                AuditBootExportStepKind.Blocked,
                position.EventId,
                blocked.DetailCode,
                blocked.FailureClass);
        }

        AuditOtlpRecord mapped;
        try
        {
            mapped = AuditOtlpRecordMapper.Map(position.ExactJsonlBytes);
        }
        catch (AuditOtlpMappingException exception)
        {
            return PersistLiveBlock(
                position,
                AuditExportFailureClass.Data,
                $"mapping.{exception.FailureCode}",
                responseDigest: null);
        }

        if (mapped.EventId != position.EventId ||
            !string.Equals(mapped.EventHash, position.EventHash, StringComparison.Ordinal))
        {
            return PersistLiveBlock(
                position,
                AuditExportFailureClass.Protocol,
                "mapping.identity",
                responseDigest: null);
        }

        var attempt = await _transport.ExportAsync(mapped, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return attempt.Kind switch
            {
                AuditExportAttemptKind.Acknowledged =>
                    AcknowledgeLive(position, attempt),
                AuditExportAttemptKind.Retry => new AuditBootExportStep(
                    AuditBootExportStepKind.Retry,
                    position.EventId,
                    attempt.DetailCode,
                    RetryAfter: attempt.RetryAfter),
                AuditExportAttemptKind.Blocked => PersistLiveBlock(
                    position,
                    attempt.FailureClass ?? throw new InvalidOperationException(
                        "A blocked live audit export result has no failure class."),
                    attempt.DetailCode,
                    attempt.ResponseDigest),
                _ => throw new InvalidOperationException(
                    "The live audit export transport returned an unknown result."),
            };
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            _faulted = true;
            throw;
        }
    }

    private AuditBootExportStep AcknowledgeLive(
        IAuditLiveSpoolRecordPosition position,
        AuditExportAttemptResult attempt)
    {
        using var evidenceAnchor = _acknowledgmentObserver.ObserveAcknowledgment(
            position.ExactJsonlBytes);
        _live.Acknowledge(position, _configurationIdentity);
        evidenceAnchor.CompleteAfterCheckpoint();
        return new AuditBootExportStep(
            AuditBootExportStepKind.Advanced,
            position.EventId,
            attempt.DetailCode,
            HasHealthWarning: attempt.HasHealthWarning);
    }

    private AuditBootExportStep PersistLiveBlock(
        IAuditLiveSpoolRecordPosition position,
        AuditExportFailureClass failureClass,
        string detailCode,
        string? responseDigest)
    {
        try
        {
            var firstFailureUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            _live.PersistBlock(
                position,
                failureClass,
                detailCode,
                responseDigest,
                firstFailureUtc,
                _configurationIdentity);
            _liveBlocked = new AuditExportBlockedRecord(
                position.Spool,
                position.StartOffset,
                position.Sequence,
                position.EventId,
                failureClass,
                detailCode,
                responseDigest,
                firstFailureUtc,
                _configurationIdentity);
            return new AuditBootExportStep(
                AuditBootExportStepKind.Blocked,
                position.EventId,
                detailCode,
                failureClass);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            _faulted = true;
            throw;
        }
    }

    private void StartClosedPrefix(IAuditLiveSpoolRotationPosition rotation)
    {
        if (_closedReader is not null || _closedPump is not null)
            throw new IOException("A closed audit spool transition is already active.");
        var reader = new AuditClosedSpoolChainReader(
            _options,
            _checkpointStore);
        try
        {
            var pump = AuditClosedSpoolExportPump.ForClosedPrefix(
                reader,
                rotation,
                _transport,
                _acknowledgmentObserver,
                _timeProvider);
            _rotation = rotation;
            _closedReader = reader;
            _closedPump = pump;
            _phase = AuditBootExportPhase.ClosedPrefix;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private void StartWriterClosedChain(
        IAuditLiveSpoolWriterClosedPosition writerClosed)
    {
        if (_closedReader is not null || _closedPump is not null)
            throw new IOException("A closed audit spool transition is already active.");
        var reader = new AuditClosedSpoolChainReader(
            _options,
            _checkpointStore);
        try
        {
            var pump = AuditClosedSpoolExportPump.AfterWriterClosed(
                reader,
                writerClosed,
                _transport,
                _acknowledgmentObserver,
                _timeProvider);
            _closedReader = reader;
            _closedPump = pump;
            _phase = AuditBootExportPhase.WriterClosedChain;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private async Task<AuditBootExportStep> ExportClosedAsync(
        CancellationToken cancellationToken)
    {
        var pump = _closedPump ?? throw new IOException(
            "The current-boot exporter lost its closed spool pump.");
        var step = await pump.ExportNextAsync(cancellationToken).ConfigureAwait(false);
        return step.Kind switch
        {
            AuditClosedSpoolExportStepKind.Advanced => new AuditBootExportStep(
                AuditBootExportStepKind.Advanced,
                step.EventId,
                step.DetailCode,
                HasHealthWarning: step.HasHealthWarning),
            AuditClosedSpoolExportStepKind.Retry => new AuditBootExportStep(
                AuditBootExportStepKind.Retry,
                step.EventId,
                step.DetailCode,
                RetryAfter: step.RetryAfter),
            AuditClosedSpoolExportStepKind.Blocked => new AuditBootExportStep(
                AuditBootExportStepKind.Blocked,
                step.EventId,
                step.DetailCode,
                step.FailureClass),
            AuditClosedSpoolExportStepKind.PrefixComplete =>
                CompletePrefix(step),
            AuditClosedSpoolExportStepKind.ChainComplete =>
                CompleteWriterClosedChain(step),
            _ => throw new IOException("The closed audit spool pump returned an unknown step."),
        };
    }

    private AuditBootExportStep CompletePrefix(AuditClosedSpoolExportStep step)
    {
        if (_phase != AuditBootExportPhase.ClosedPrefix ||
            _rotation is null ||
            step.PrefixEnd is null)
        {
            throw new IOException("The closed-prefix exporter lost its transition proof.");
        }
        try
        {
            _live.AdvanceAfterClosedPrefix(_rotation, step.PrefixEnd);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            _faulted = true;
            throw;
        }
        ClearClosedTransition();
        _phase = AuditBootExportPhase.Live;
        return new AuditBootExportStep(
            step.EventId is null
                ? AuditBootExportStepKind.Progressed
                : AuditBootExportStepKind.Advanced,
            step.EventId,
            step.DetailCode,
            HasHealthWarning: step.HasHealthWarning);
    }

    private AuditBootExportStep CompleteWriterClosedChain(
        AuditClosedSpoolExportStep step)
    {
        if (_phase != AuditBootExportPhase.WriterClosedChain)
            throw new IOException("The writer-closed exporter completed in another phase.");
        ClearClosedTransition();
        _phase = AuditBootExportPhase.Complete;
        return Complete(step.EventId, step.DetailCode, step.HasHealthWarning);
    }

    private void ClearClosedTransition()
    {
        var pump = _closedPump;
        var reader = _closedReader;
        _closedPump = null;
        _closedReader = null;
        _rotation = null;
        pump?.Dispose();
        reader?.Dispose();
    }

    public void Dispose()
    {
        var prior = Interlocked.CompareExchange(ref _lifecycle, 2, 0);
        if (prior == 2) return;
        if (prior == 1)
        {
            throw new InvalidOperationException(
                "A running current-boot audit export step must finish before disposal.");
        }
        try
        {
            ClearClosedTransition();
        }
        finally
        {
            _live.Dispose();
        }
    }

    private static AuditBootExportStep Complete(
        Guid? eventId,
        string detailCode,
        bool hasHealthWarning = false) =>
        new(
            AuditBootExportStepKind.Complete,
            eventId,
            detailCode,
            HasHealthWarning: hasHealthWarning);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;
}
