using Microsoft.Extensions.Hosting;
using PtkMcpServer.Sessions;

namespace PtkMcpServer.Audit;

/// <summary>
/// Nonthrowing supervisor startup gate. The MCP transport may start in a
/// diagnostic-only mode when protected audit storage is unavailable, while
/// every runtime dependency remains unreachable until server.started is
/// durable. A later request may repair first-start failure under this gate;
/// an already-published journal is never hot-swapped.
/// </summary>
internal sealed class AuditRuntimeGate : IHostedService, IDisposable
{
    private readonly object _gate = new();
    private readonly object _admissionGate = new();
    private readonly object _constructionGate = new();
    private readonly AuditOptions _options;
    private readonly AuditHealth _health;
    private readonly ScriptEvidenceStoreProvider _evidence;
    private readonly string _producerVersion;
    private readonly Func<AuditRuntimeResources> _openRuntime;

    private AuditRuntimeResources? _resources;
    private AuditJournal? _journal;
    private AuditServerLifecycle? _lifecycle;
    private ISessionLifetime? _sessionLifetime;
    private Task? _stopTask;
    private TaskCompletionSource<bool>? _activeCallsDrained;
    private int _activeCalls;
    private bool _testOperational;
    private bool _stopping;
    private bool _disposed;
    private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;

    internal AuditRuntimeGate(
        AuditOptions options,
        AuditHealth health,
        ScriptEvidenceStoreProvider evidence,
        string producerVersion,
        Func<AuditJournal>? openJournal = null,
        Func<AuditRuntimeResources>? openRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);

        _options = options;
        _health = health;
        _evidence = evidence;
        _producerVersion = producerVersion;
        if (openJournal is not null && openRuntime is not null)
        {
            throw new ArgumentException(
                "Specify either a journal factory or a runtime-resource factory, not both.");
        }
        _openRuntime = openRuntime ?? (openJournal is not null
            ? () => new AuditRuntimeResources(openJournal())
            : () => AuditRuntimeResources.OpenLocal(
                _options,
                _health,
                _producerVersion,
                _evidence));
    }

    private AuditRuntimeGate(
        AuditOptions options,
        AuditHealth health,
        AuditJournal journal,
        ScriptEvidenceStoreProvider evidence)
    {
        _options = options;
        _health = health;
        _evidence = evidence;
        _producerVersion = "test";
        _openRuntime = () => new AuditRuntimeResources(journal, ownsJournal: false);
        _resources = new AuditRuntimeResources(journal, ownsJournal: false);
        _journal = journal;
        _testOperational = true;
    }

    internal static AuditRuntimeGate CreateOperationalForTests(
        AuditOptions options,
        AuditHealth health,
        AuditJournal journal,
        ScriptEvidenceStore evidence) =>
        new(options, health, journal, new ScriptEvidenceStoreProvider(evidence));

    internal AuditHealth Health => _health;

    internal DateTimeOffset LastActivityUtc
    {
        get
        {
            lock (_gate)
                return _lastActivityUtc;
        }
    }

    internal void Touch()
    {
        lock (_gate)
            _lastActivityUtc = DateTimeOffset.UtcNow;
    }

    internal bool TryCreateCallContext(
        int upcomingRecordSlots,
        out AuditCallContext? context)
    {
        if (upcomingRecordSlots < 1)
            throw new ArgumentOutOfRangeException(nameof(upcomingRecordSlots));

        lock (_admissionGate)
        {
            lock (_gate)
            {
                _lastActivityUtc = DateTimeOffset.UtcNow;
                if (_disposed || _stopping)
                {
                    context = null;
                    return false;
                }
            }

            if (_journal is null &&
                !TryInitializeSerialized(upcomingRecordSlots))
            {
                context = null;
                return false;
            }

            lock (_gate)
            {
                if (_disposed || _stopping ||
                    (!_testOperational && _lifecycle?.IsStarted != true))
                {
                    context = null;
                    return false;
                }

                context = new AuditCallContext(_journal!, _evidence);
                return true;
            }
        }
    }

    /// <summary>
    /// Serializes the complete acceptance transaction (startup recovery,
    /// evidence persistence, journal recovery/reservation, and call.accepted)
    /// and returns an active-call lease. Shutdown cannot write server.stopped
    /// until every returned lease has been released after its terminal path.
    /// </summary>
    internal bool TryBeginCall(
        AuditCallMetadata metadata,
        string? exactSubmittedScript,
        out AuditCallContext? context,
        out AuditRuntimeCallLease? callLease,
        out string? failureClass)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        lock (_gate)
        {
            _lastActivityUtc = DateTimeOffset.UtcNow;
            if (_disposed || _stopping)
            {
                context = null;
                callLease = null;
                failureClass = "journal.closed";
                return false;
            }
        }

        context = null;
        callLease = null;
        failureClass = null;
        var initialHealth = _health.Snapshot();
        if (metadata.Request.Tool == "ptk_state" &&
            initialHealth.State is AuditHealthState.Degraded or AuditHealthState.Unavailable)
        {
            failureClass = initialHealth.FailureClass ?? "journal.unavailable";
            return false;
        }

        lock (_admissionGate)
        {
            lock (_gate)
            {
                if (_disposed || _stopping)
                {
                    failureClass = "journal.closed";
                    return false;
                }
            }
            if (_journal is null)
            {
                var health = _health.Snapshot();
                // The emergency state path must remain prompt and must not
                // hammer a known-broken filesystem or wait on its quota mutex.
                if (metadata.Request.Tool == "ptk_state" &&
                    health.State is AuditHealthState.Degraded or AuditHealthState.Unavailable)
                {
                    failureClass = health.FailureClass ?? "journal.startup";
                    return false;
                }

                if (!TryInitializeSerialized(metadata.OperationProfile.MaximumRecordSlots))
                {
                    failureClass = _health.Snapshot().FailureClass ?? "journal.startup";
                    return false;
                }
            }

            lock (_gate)
            {
                if (_disposed || _stopping ||
                    (!_testOperational && _lifecycle?.IsStarted != true))
                {
                    failureClass = _stopping ? "journal.closed" : "journal.lifecycle";
                    return false;
                }
            }

            var candidate = new AuditCallContext(_journal!, _evidence);
            if (!candidate.TryBegin(metadata, exactSubmittedScript, out failureClass))
                return false;

            lock (_gate)
            {
                if (_activeCalls == 0)
                {
                    _activeCallsDrained = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }
                _activeCalls = checked(_activeCalls + 1);
            }
            context = candidate;
            callLease = new AuditRuntimeCallLease(ReleaseActiveCall);
            return true;
        }
    }

    internal T RunAfterStarted<T>(Func<T> downstreamFactory)
        => RunAfterStarted(downstreamFactory, publish: null);

    private T RunAfterStarted<T>(Func<T> downstreamFactory, Action<T>? publish)
    {
        ArgumentNullException.ThrowIfNull(downstreamFactory);
        lock (_constructionGate)
        {
            lock (_gate)
            {
                if (!CanConstructRuntimeLocked())
                    throw new AuditUnavailableException();
            }

            // Slow runspace/module construction must not hold the health gate;
            // emergency state remains observable while this factory runs.
            var value = downstreamFactory();
            lock (_gate)
            {
                if (CanConstructRuntimeLocked())
                {
                    publish?.Invoke(value);
                    return value;
                }
            }
            if (value is IDisposable disposable) disposable.Dispose();
            throw new AuditUnavailableException();
        }
    }

    internal TSession RunSessionAfterStarted<TSession>(
        Func<TSession> downstreamFactory)
        where TSession : class, ISessionLifetime
        => RunAfterStarted(downstreamFactory, lifetime => _sessionLifetime = lifetime);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_admissionGate)
        {
            bool initialize;
            lock (_gate)
                initialize = !_disposed && !_stopping && _journal is null;
            if (initialize)
                _ = TryInitializeSerialized(upcomingRecordSlots: 1);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_admissionGate)
        {
            lock (_constructionGate)
            {
                lock (_gate)
                {
                    if (_stopTask is not null) return _stopTask;
                    _stopping = true;
                    var activeCalls = _activeCalls == 0
                        ? Task.CompletedTask
                        : _activeCallsDrained!.Task;
                    _stopTask = StopCoreAsync(
                        activeCalls,
                        _sessionLifetime,
                        _lifecycle,
                        _resources);
                    return _stopTask;
                }
            }
        }
    }

    public void Dispose()
    {
        Exception? stopFailure = null;
        try
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            stopFailure = exception;
        }

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopping = true;
            try
            {
                if (stopFailure is null)
                    _lifecycle?.Dispose();
                else
                    _lifecycle?.AbandonWithoutStop();
            }
            finally
            {
                _resources?.Dispose();
                _resources = null;
                _lifecycle = null;
                _journal = null;
            }
        }
        if (stopFailure is not null) throw stopFailure;
    }

    private async Task StopCoreAsync(
        Task activeCalls,
        ISessionLifetime? sessionLifetime,
        AuditServerLifecycle? lifecycle,
        AuditRuntimeResources? resources)
    {
        await activeCalls.ConfigureAwait(false);
        // Session drain is operational cleanup, not an audit-integrity seam: a
        // stuck job or runspace must not forfeit the server.stopped terminal
        // record, or an orderly shutdown becomes indistinguishable from a
        // crash on replay. The degradation is marked before Stop() so the
        // terminal event's health snapshot durably carries the drain failure.
        // Exporter stop stays fail-closed: a faulted export pipeline is an
        // audit-integrity failure that must not be papered over with a clean
        // terminal record.
        try
        {
            if (sessionLifetime is not null)
                await sessionLifetime.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            MarkDrainDegraded("session.shutdown");
        }
        if (resources is not null)
            await resources.StopExporterAsync().ConfigureAwait(false);
        lifecycle?.Stop();
    }

    private void MarkDrainDegraded(string failureClass)
    {
        try
        {
            // Never soften an existing unhealthy record: its failure class is
            // the recovery key consumed by TryRecoverExternal on restart.
            if (_health.Snapshot().State is AuditHealthState.Healthy or AuditHealthState.Recovered)
                _health.MarkDegraded(failureClass);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Keep shutdown on the recorded path even if the health surface
            // itself is failing; Stop() still captures the current snapshot.
        }
    }

    private void ReleaseActiveCall()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_gate)
        {
            if (_activeCalls < 1) return;
            _activeCalls--;
            if (_activeCalls == 0)
            {
                drained = _activeCallsDrained;
                _activeCallsDrained = null;
            }
        }
        drained?.TrySetResult(true);
    }

    private bool CanConstructRuntimeLocked()
    {
        if (_disposed || _stopping ||
            (!_testOperational && _lifecycle?.IsStarted != true))
        {
            return false;
        }
        return _health.Snapshot().State is AuditHealthState.Healthy or AuditHealthState.Recovered;
    }

    private bool TryInitializeSerialized(int upcomingRecordSlots)
    {
        AuditRuntimeResources? candidateResources = null;
        AuditServerLifecycle? candidateLifecycle = null;
        try
        {
            candidateResources = _openRuntime();
            var candidateJournal = candidateResources.Journal;
            var health = _health.Snapshot();
            if (health.State is AuditHealthState.Degraded or AuditHealthState.Unavailable)
            {
                if (health.FailureClass is null ||
                    !candidateJournal.TryRecoverExternal(
                        health.FailureClass,
                        checked(
                            upcomingRecordSlots +
                            AuditServerLifecycle.RequiredRecordSlots)))
                {
                    throw new AuditUnavailableException();
                }
            }

            candidateLifecycle = new AuditServerLifecycle(candidateJournal, _options);
            candidateLifecycle.EnsureStarted();
            try
            {
                _evidence.RetainEligible(candidateJournal);
            }
            catch (ScriptEvidenceStorageException)
            {
                candidateJournal.EnterExternalUnavailable("evidence.storage");
                throw;
            }
            candidateResources.StartExporter();

            lock (_gate)
            {
                _resources = candidateResources;
                _journal = candidateJournal;
                _lifecycle = candidateLifecycle;
            }
            candidateResources = null;
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            try { candidateLifecycle?.Dispose(); } catch { /* keep startup diagnostic-only */ }
            try { candidateResources?.Dispose(); } catch { /* keep startup diagnostic-only */ }
            MarkStartupUnavailable();
            return false;
        }
    }

    private void MarkStartupUnavailable()
    {
        try
        {
            if (_health.Snapshot().State != AuditHealthState.Unavailable)
                _health.MarkUnavailable("journal.startup");
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // The filter will collapse to the generic fail-closed response if
            // even the nonsecret health snapshot cannot be maintained.
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}

internal sealed class AuditRuntimeCallLease(Action release) : IDisposable
{
    private Action? _release = release;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
