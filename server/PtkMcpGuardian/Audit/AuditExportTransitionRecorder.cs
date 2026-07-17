namespace PtkMcpServer.Audit;

internal sealed class AuditExportTransitionPersistenceException(Exception innerException)
    : IOException("Required audit exporter transition persistence failed.", innerException);

internal enum AuditBacklogTransition
{
    None,
    EnteredHighWater,
    Recovered,
}

/// <summary>
/// A state-only hysteresis gate over the journal's already bounded spool
/// metrics. It performs no I/O and reports only threshold crossings.
/// </summary>
internal sealed class AuditBacklogHysteresis
{
    private readonly long _enterWhenEffectiveFreeAtMostBytes;
    private readonly long _recoverWhenEffectiveFreeAtLeastBytes;
    private bool _highWater;

    internal AuditBacklogHysteresis(
        long enterWhenEffectiveFreeAtMostBytes,
        long recoverWhenEffectiveFreeAtLeastBytes)
    {
        if (enterWhenEffectiveFreeAtMostBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(enterWhenEffectiveFreeAtMostBytes));
        }
        if (recoverWhenEffectiveFreeAtLeastBytes <=
            enterWhenEffectiveFreeAtMostBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recoverWhenEffectiveFreeAtLeastBytes));
        }

        _enterWhenEffectiveFreeAtMostBytes =
            enterWhenEffectiveFreeAtMostBytes;
        _recoverWhenEffectiveFreeAtLeastBytes =
            recoverWhenEffectiveFreeAtLeastBytes;
    }

    /// <summary>
    /// Uses frozen journal bounds rather than a percentage: high water begins
    /// when only the emergency reserve plus one maximum record remains, and is
    /// cleared only after at least one complete segment of additional space is
    /// available. The distinct boundaries prevent per-byte event chatter.
    /// </summary>
    internal static AuditBacklogHysteresis CreateFor(AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var enter = checked(options.EmergencyReserveBytes + options.MaxRecordBytes);
        var recover = Math.Min(
            options.AggregateBytes,
            checked(enter + options.SegmentBytes));
        return new AuditBacklogHysteresis(enter, recover);
    }

    internal AuditBacklogTransition Observe(AuditHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var effectiveFreeBytes = snapshot.EffectiveFreeBytes;
        if (!_highWater &&
            effectiveFreeBytes <= _enterWhenEffectiveFreeAtMostBytes)
        {
            _highWater = true;
            return AuditBacklogTransition.EnteredHighWater;
        }

        if (_highWater &&
            effectiveFreeBytes >= _recoverWhenEffectiveFreeAtLeastBytes)
        {
            _highWater = false;
            return AuditBacklogTransition.Recovered;
        }

        return AuditBacklogTransition.None;
    }
}

/// <summary>
/// Converts the noisy export-loop observation stream into a bounded set of
/// durable, nonsecret lifecycle facts. It never records acknowledgments and
/// never derives detail from exception text or remote response bodies.
/// </summary>
internal sealed class AuditExportTransitionRecorder : IAuditExportHealthObserver
{
    private readonly object _gate = new();
    private readonly AuditJournal _journal;
    private readonly IAuditExportHealthObserver? _inner;
    private readonly AuditBacklogHysteresis? _backlog;
    private readonly HashSet<BlockedIdentity> _recordedBlocks = [];

    private bool _started;
    private bool _impeded;
    private bool _warningActive;
    private bool _terminal;
    private bool _recording;

    internal AuditExportTransitionRecorder(
        AuditJournal journal,
        IAuditExportHealthObserver? inner = null,
        AuditBacklogHysteresis? backlog = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        if (journal.Options.ProtectionMode != AuditProtectionMode.Anchored)
        {
            throw new ArgumentException(
                "Exporter transition recording requires anchored audit mode.",
                nameof(journal));
        }
        _inner = inner;
        _backlog = backlog;
    }

    public void Observe(AuditExportHealthObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            if (_recording) return;

            try
            {
                _inner?.Observe(observation);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // The pre-existing in-memory health projection is diagnostic.
                // Durable transition persistence below remains authoritative.
            }

            if (_terminal || observation.Snapshot.State == AuditExportLoopState.Created)
                return;

            if (observation.Snapshot.State is
                AuditExportLoopState.Stopped or AuditExportLoopState.Disposed)
            {
                _terminal = true;
                return;
            }

            if (observation.Snapshot.State == AuditExportLoopState.Faulted)
            {
                if (_started)
                {
                    Record(
                        "audit.export_stalled",
                        "failed",
                        "loop.fault",
                        target: null);
                }
                _terminal = true;
                return;
            }

            // Snapshot the backlog crossing before writing this observer's own
            // event. Journal metric updates caused by Record never re-enter the
            // observer and therefore cannot manufacture recursive crossings.
            var backlogTransition = _backlog?.Observe(_journal.Health.Snapshot())
                ?? AuditBacklogTransition.None;

            if (!_started)
            {
                if (observation.Snapshot.State != AuditExportLoopState.Running ||
                    observation.ObservedStep is not null)
                {
                    return;
                }

                Record(
                    "export.started",
                    "started",
                    "loop.started",
                    target: null);
                _started = true;
            }

            if (backlogTransition == AuditBacklogTransition.EnteredHighWater)
            {
                Record(
                    "audit.backlog_high",
                    "degraded",
                    "spool.high_water",
                    target: null);
            }
            else if (backlogTransition == AuditBacklogTransition.Recovered)
            {
                Record(
                    "audit.backlog_recovered",
                    "recovered",
                    "spool.recovered",
                    target: null);
            }

            if (observation.ObservedStep is { } step)
                ObserveStep(step);

            if (observation.Snapshot.State == AuditExportLoopState.Completed)
                _terminal = true;
        }
    }

    private void ObserveStep(AuditExportCoordinatorStep step)
    {
        switch (step.Kind)
        {
            case AuditExportCoordinatorStepKind.Retry when !_impeded:
                Record(
                    "audit.export_stalled",
                    "retrying",
                    "retry.pending",
                    step);
                _impeded = true;
                break;

            case AuditExportCoordinatorStepKind.Blocked:
            {
                var identity = BlockedIdentity.From(step);
                if (_recordedBlocks.Add(identity))
                {
                    Record(
                        "audit.export_stalled",
                        "blocked",
                        BlockDetail(step.FailureClass),
                        step);
                }
                _impeded = true;
                break;
            }

            case AuditExportCoordinatorStepKind.Advanced:
            case AuditExportCoordinatorStepKind.Progressed:
            case AuditExportCoordinatorStepKind.Complete:
                if (_impeded)
                {
                    Record(
                        "audit.export_recovered",
                        "recovered",
                        "progress.observed",
                        step);
                    _impeded = false;
                }
                break;
        }

        if (step.EventId is not null &&
            step.Kind is AuditExportCoordinatorStepKind.Advanced or
                AuditExportCoordinatorStepKind.Complete)
        {
            if (step.HasHealthWarning)
            {
                if (!_warningActive)
                {
                    Record(
                        "export.warning",
                        "warning",
                        "ack.zero_rejection_warning",
                        step);
                    _warningActive = true;
                }
            }
            else
            {
                _warningActive = false;
            }
        }
    }

    private void Record(
        string eventType,
        string outcomeState,
        string detailCode,
        AuditExportCoordinatorStep? target)
    {
        _recording = true;
        try
        {
            _journal.AppendAutomaticTransition(CreateEvent(
                eventType,
                outcomeState,
                detailCode,
                target));
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            _terminal = true;
            throw new AuditExportTransitionPersistenceException(exception);
        }
        finally
        {
            _recording = false;
        }
    }

    private AuditEventInput CreateEvent(
        string eventType,
        string outcomeState,
        string detailCode,
        AuditExportCoordinatorStep? target)
    {
        var health = _journal.Health.Snapshot();
        var unhealthy = health.State is
            AuditHealthState.Degraded or AuditHealthState.Unavailable;
        return new AuditEventInput
        {
            EventType = eventType,
            Session = new AuditSession
            {
                DeclaredPurpose = "audit_export",
                DeclaredTarget = target is null
                    ? null
                    : $"{(target.IsCurrentBoot ? "current" : "orphan")}:" +
                      target.SupervisorBootId.ToString("D"),
            },
            Actor = new AuditActor { AttributionStrength = "unknown" },
            Correlation = new AuditCorrelation
            {
                ParentEventId = target?.EventId ?? _journal.LastEventId,
            },
            Request = new AuditRequest
            {
                Tool = "audit_export",
                Action = eventType[(eventType.IndexOf('.') + 1)..]
                    .Replace('.', '_'),
            },
            Routing = new AuditRouting(),
            Outcome = new AuditOutcome
            {
                State = outcomeState,
                DetailCode = detailCode,
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
                ProtectionMode = "anchored",
                ExportConfigurationIdentity = _journal.Options.ExportConfigurationIdentity,
                HealthState = unhealthy ? "degraded" : "healthy",
                FailureClass = unhealthy ? health.FailureClass : null,
                DegradedSinceUtc = unhealthy ? health.DegradedSinceUtc : null,
            },
        };
    }

    private static string BlockDetail(AuditExportFailureClass? failureClass) =>
        failureClass switch
        {
            AuditExportFailureClass.Configuration => "block.configuration",
            AuditExportFailureClass.PartialRejection => "block.partial_rejection",
            AuditExportFailureClass.Data => "block.data",
            AuditExportFailureClass.Protocol => "block.protocol",
            _ => "block.unknown",
        };

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private readonly record struct BlockedIdentity(
        Guid SupervisorBootId,
        Guid? EventId)
    {
        internal static BlockedIdentity From(AuditExportCoordinatorStep step) =>
            new(step.SupervisorBootId, step.EventId);
    }
}
