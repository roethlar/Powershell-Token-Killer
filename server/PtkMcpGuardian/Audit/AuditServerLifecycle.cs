using Microsoft.Extensions.Hosting;

namespace PtkMcpServer.Audit;

/// <summary>
/// Owns the supervisor process's fixed audit obligation. The start record and
/// the future graceful-stop record are reserved together, before the first
/// start record is appended. Runtime factories can use <see cref="RunAfterStarted(Action)"/>
/// to make durable startup ordering explicit even when DI resolves them before
/// hosted services are started.
/// </summary>
internal sealed class AuditServerLifecycle : IHostedService, IDisposable
{
    internal const int RequiredRecordSlots = 2;

    private readonly object _gate = new();
    private readonly AuditJournal _journal;
    private readonly AuditOptions _options;
    private AuditReservation? _reservation;
    private Guid? _startedEventId;
    private LifecycleState _state;

    internal AuditServerLifecycle(AuditJournal journal, AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(options);
        _journal = journal;
        _options = options;
    }

    internal bool IsStarted
    {
        get
        {
            lock (_gate)
                return _state == LifecycleState.Started;
        }
    }

    /// <summary>
    /// Durably records server startup before invoking a downstream factory.
    /// A reservation or persistence failure prevents the factory from running.
    /// </summary>
    internal T RunAfterStarted<T>(Func<T> downstreamFactory)
    {
        ArgumentNullException.ThrowIfNull(downstreamFactory);
        EnsureStarted();
        return downstreamFactory();
    }

    /// <inheritdoc cref="RunAfterStarted{T}(Func{T})"/>
    internal void RunAfterStarted(Action downstreamStart)
    {
        ArgumentNullException.ThrowIfNull(downstreamStart);
        EnsureStarted();
        downstreamStart();
    }

    internal void EnsureStarted()
    {
        lock (_gate)
        {
            switch (_state)
            {
                case LifecycleState.Started:
                    return;
                case LifecycleState.Stopped:
                    throw new InvalidOperationException("The audited server lifecycle has already stopped.");
                case LifecycleState.Faulted:
                    throw new AuditUnavailableException();
            }

            _state = LifecycleState.Starting;
            if (!_journal.TryReserve(
                    RequiredRecordSlots,
                    out var reservation,
                    out var failureClass))
            {
                FailLocked(failureClass ?? "journal.lifecycle");
                throw new AuditUnavailableException();
            }

            _reservation = reservation;
            try
            {
                var started = _journal.Append(
                    reservation!,
                    CreateEvent(
                        "server.started",
                        parentEventId: null,
                        outcomeState: "started"));
                _startedEventId = started.EventId;
                _state = LifecycleState.Started;
            }
            catch (AuditEventValidationException)
            {
                FailLocked("journal.schema");
                throw new AuditUnavailableException();
            }
            catch (AuditUnavailableException)
            {
                FailLocked(null);
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                FailLocked("journal.lifecycle");
                throw new AuditUnavailableException();
            }
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            if (_state is LifecycleState.Stopped or LifecycleState.Faulted)
                return;
            if (_state == LifecycleState.Created)
            {
                _state = LifecycleState.Stopped;
                return;
            }
            if (_state != LifecycleState.Started)
                throw new InvalidOperationException("The audited server lifecycle cannot stop during startup.");

            _state = LifecycleState.Stopping;
            try
            {
                _journal.Append(
                    _reservation!,
                    CreateEvent(
                        "server.stopped",
                        _startedEventId,
                        outcomeState: "stopped"));
                _reservation!.Release();
                _reservation = null;
                _state = LifecycleState.Stopped;
            }
            catch (AuditEventValidationException)
            {
                FailLocked("journal.schema");
                throw new AuditUnavailableException();
            }
            catch (AuditUnavailableException)
            {
                FailLocked(null);
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                FailLocked("journal.lifecycle");
                throw new AuditUnavailableException();
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureStarted();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // A graceful shutdown must consume the already-reserved terminal slot;
        // host cancellation cannot turn that durable obligation into best effort.
        Stop();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _reservation?.Release();
            _reservation = null;
        }
    }

    /// <summary>
    /// Releases the reserved stop obligation without claiming server.stopped.
    /// Used only when ordered runtime drain failed and shutdown can no longer
    /// make the lifecycle assertion truthfully.
    /// </summary>
    internal void AbandonWithoutStop()
    {
        lock (_gate)
        {
            _reservation?.Release();
            _reservation = null;
            _state = LifecycleState.Faulted;
        }
    }

    private AuditEventInput CreateEvent(
        string eventType,
        Guid? parentEventId,
        string outcomeState)
    {
        var health = _journal.Health.Snapshot();
        var unhealthy = health.State is AuditHealthState.Degraded or AuditHealthState.Unavailable;
        return new AuditEventInput
        {
        EventType = eventType,
        Session = new AuditSession(),
        Actor = new AuditActor { AttributionStrength = "unknown" },
        Correlation = new AuditCorrelation { ParentEventId = parentEventId },
        Request = new AuditRequest(),
        Routing = new AuditRouting(),
        Outcome = new AuditOutcome
        {
            State = outcomeState,
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
            HealthState = unhealthy ? "degraded" : "healthy",
            FailureClass = unhealthy ? health.FailureClass : null,
            DegradedSinceUtc = unhealthy ? health.DegradedSinceUtc : null,
        },
        };
    }

    private void FailLocked(string? failureClass)
    {
        _reservation?.Release();
        _reservation = null;
        _state = LifecycleState.Faulted;
        if (failureClass is not null)
            _journal.Health.MarkUnavailable(failureClass);
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private enum LifecycleState
    {
        Created,
        Starting,
        Started,
        Stopping,
        Stopped,
        Faulted,
    }
}
