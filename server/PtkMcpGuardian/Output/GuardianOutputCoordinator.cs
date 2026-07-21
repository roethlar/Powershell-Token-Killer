using PtkMcpServer;
using PtkSharedContracts;

namespace PtkMcpGuardian.Output;

/// <summary>
/// Owns request/job lifetime around the lower-level capability registry. It
/// deliberately retains only correlation values, never an OperationRequest or
/// script-bearing operation. Foreground and job-output terminals wait for their
/// response; a successful background-start response instead leaves its exact
/// capability alive until a later seal, expiry, or host-generation loss.
/// </summary>
internal sealed class GuardianOutputCoordinator : IDisposable
{
    internal const int DefaultMaximumJobRecoveries = 4096;

    private readonly object _gate = new();
    private readonly GuardianOutputCapabilityRegistry _registry;
    private readonly int _maximumJobRecoveries;
    private readonly Dictionary<long, TrackedCapability> _tracked = [];
    private readonly Dictionary<long, OutputRecoverySummary> _jobRecoveries = [];
    private bool _disposed;

    internal GuardianOutputCoordinator(
        GuardianOutputCapabilityRegistry registry,
        int maximumJobRecoveries = DefaultMaximumJobRecoveries)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (maximumJobRecoveries is < 1 or > DefaultMaximumJobRecoveries)
            throw new ArgumentOutOfRangeException(nameof(maximumJobRecoveries));
        _maximumJobRecoveries = maximumJobRecoveries;
    }

    internal int TrackedCount
    {
        get { lock (_gate) return _tracked.Count; }
    }

    internal int ActiveCapabilityCount => _registry.ActiveCount;

    internal int MaximumCaptureBytes => _registry.MaximumCaptureBytes;

    internal int JobRecoveryCount
    {
        get { lock (_gate) return _jobRecoveries.Count; }
    }

    internal bool TryRegister(
        OperationRequest request,
        out GuardianOutputRegistration? registration,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(request);
        registration = null;
        failure = null;
        var correlatedJobId = request.Operation switch
        {
            InvokeBackgroundOperation background => background.PublicJobId,
            GuardianHostJobIdentityOperation job => job.PublicJobId,
            _ => null,
        };
        var backgroundJobId = request.Operation is InvokeBackgroundOperation
            ? correlatedJobId
            : null;

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_tracked.ContainsKey(request.RequestId.Value))
            {
                failure = "output_request_duplicate";
                return false;
            }
            if (backgroundJobId is { } jobId)
            {
                if (_jobRecoveries.ContainsKey(jobId.Value) ||
                    _tracked.Values.Any(value => value.BackgroundJobId == jobId))
                {
                    failure = "output_job_duplicate";
                    return false;
                }
                if (_jobRecoveries.Count +
                    _tracked.Values.Count(value => value.BackgroundJobId is not null) >=
                    _maximumJobRecoveries)
                {
                    failure = "output_job_capacity";
                    return false;
                }
            }

            if (!_registry.TryRegister(request, out registration, out failure))
                return false;

            var tracked = new TrackedCapability(
                registration!,
                request.Operation.Kind,
                correlatedJobId,
                backgroundJobId);
            try
            {
                _tracked.Add(request.RequestId.Value, tracked);
                return true;
            }
            catch
            {
                _registry.TryCancel(registration!);
                registration = null;
                throw;
            }
        }
    }

    internal bool MatchesActiveEvent(GuardianHostEvent hostEvent) =>
        _registry.MatchesActiveEvent(hostEvent);

    internal ValueTask HandleEventAsync(
        GuardianHostEvent hostEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HandleEvent(hostEvent);
        return ValueTask.CompletedTask;
    }

    internal void HandleEvent(GuardianHostEvent hostEvent)
    {
        ArgumentNullException.ThrowIfNull(hostEvent);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            GuardianOutputTerminal? terminal;
            try
            {
                terminal = _registry.AcceptEvent(hostEvent);
            }
            catch (GuardianOutputCapabilityException)
            {
                MarkRejectedCapabilityClosedLocked(hostEvent);
                throw;
            }
            if (terminal is not null)
                RecordTerminalLocked(terminal);
        }
    }

    /// <summary>
    /// Resolves the output side of one decoded private response. A successful
    /// background start retains an unsealed capability; every other response
    /// removes request tracking and cancels an unused capability.
    /// </summary>
    internal OutputRecoverySummary? ResolveResponse(
        PrivateRequestId requestId,
        bool succeeded)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (!_tracked.TryGetValue(requestId.Value, out var tracked))
                return null;

            if (tracked.OperationKind == GuardianHostOperationKind.InvokeBackground &&
                succeeded)
            {
                tracked.BackgroundResponseSucceeded = true;
                if (tracked.Terminal is { } backgroundTerminal)
                {
                    PublishJobRecoveryLocked(tracked, backgroundTerminal.Recovery);
                    _tracked.Remove(requestId.Value);
                }
                return null;
            }

            if (tracked.CapabilityActive)
                CancelActiveLocked(tracked);
            _tracked.Remove(requestId.Value);
            if (tracked.Terminal is { } terminal)
                return terminal.Recovery;
            return succeeded
                ? OutputRecoverySummary.Unavailable(
                    "output_not_transferred",
                    advertise: true)
                : null;
        }
    }

    /// <summary>Removes a call that ended without a decoded host response.</summary>
    internal void AbandonCall(PrivateRequestId requestId)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        lock (_gate)
        {
            if (_disposed || !_tracked.TryGetValue(requestId.Value, out var tracked))
                return;
            if (tracked.CapabilityActive)
                CancelActiveLocked(tracked);
            _tracked.Remove(requestId.Value);
        }
    }

    internal IReadOnlyList<GuardianOutputTerminal> AbandonGeneration(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        string reason)
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            var terminals = _registry.AbandonGeneration(
                guardianBootId,
                hostBootId,
                hostGeneration,
                reason);
            foreach (var terminal in terminals)
                RecordTerminalLocked(terminal);
            return terminals;
        }
    }

    internal IReadOnlyList<GuardianOutputTerminal> SweepExpired()
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            var terminals = _registry.SweepExpired();
            foreach (var terminal in terminals)
                RecordTerminalLocked(terminal);
            return terminals;
        }
    }

    internal bool TryGetJobRecovery(
        PublicJobId publicJobId,
        out OutputRecoverySummary? recovery)
    {
        ArgumentNullException.ThrowIfNull(publicJobId);
        lock (_gate)
        {
            if (_disposed)
            {
                recovery = null;
                return false;
            }
            return _jobRecoveries.TryGetValue(publicJobId.Value, out recovery);
        }
    }

    internal bool RemoveJobRecovery(PublicJobId publicJobId)
    {
        ArgumentNullException.ThrowIfNull(publicJobId);
        lock (_gate)
            return !_disposed && _jobRecoveries.Remove(publicJobId.Value);
    }

    internal bool TryCancel(GuardianOutputRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            if (_disposed ||
                !_tracked.TryGetValue(registration.RequestId.Value, out var tracked) ||
                tracked.Registration.RegistrationId != registration.RegistrationId ||
                tracked.Registration.Token != registration.Token ||
                !tracked.CapabilityActive)
            {
                return false;
            }
            CancelActiveLocked(tracked);
            _tracked.Remove(registration.RequestId.Value);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _tracked.Clear();
            _jobRecoveries.Clear();
            _registry.Dispose();
        }
    }

    private void RecordTerminalLocked(GuardianOutputTerminal terminal)
    {
        if (!_tracked.TryGetValue(terminal.RequestId.Value, out var tracked) ||
            tracked.Registration.Token != terminal.Token ||
            tracked.OperationKind != terminal.OperationKind ||
            tracked.CorrelatedJobId != terminal.PublicJobId)
        {
            throw new InvalidOperationException(
                "The output terminal does not match coordinator ownership.");
        }
        if (!tracked.CapabilityActive || tracked.Terminal is not null)
        {
            throw new InvalidOperationException(
                "The output capability already has a terminal.");
        }

        tracked.CapabilityActive = false;
        tracked.Terminal = terminal;
        if (tracked.OperationKind == GuardianHostOperationKind.InvokeBackground &&
            tracked.BackgroundResponseSucceeded)
        {
            PublishJobRecoveryLocked(tracked, terminal.Recovery);
            _tracked.Remove(terminal.RequestId.Value);
        }
    }

    private void MarkRejectedCapabilityClosedLocked(GuardianHostEvent hostEvent)
    {
        var token = hostEvent switch
        {
            OutputChunkEvent chunk => chunk.OutputCapabilityToken,
            OutputSealEvent seal => seal.OutputCapabilityToken,
            _ => null,
        };
        if (token is null) return;
        var tracked = _tracked.Values.SingleOrDefault(value =>
            value.Registration.Token == token);
        if (tracked is not null)
            tracked.CapabilityActive = false;
    }

    private void CancelActiveLocked(TrackedCapability tracked)
    {
        if (!tracked.CapabilityActive) return;
        if (!_registry.TryCancel(tracked.Registration))
        {
            throw new InvalidOperationException(
                "The coordinator lost an active output capability.");
        }
        tracked.CapabilityActive = false;
    }

    private void PublishJobRecoveryLocked(
        TrackedCapability tracked,
        OutputRecoverySummary recovery)
    {
        var jobId = tracked.BackgroundJobId ??
            throw new InvalidOperationException(
                "A non-background capability cannot publish job recovery.");
        if (_jobRecoveries.Count >= _maximumJobRecoveries ||
            !_jobRecoveries.TryAdd(jobId.Value, recovery))
        {
            throw new InvalidOperationException(
                "The guardian job recovery table is inconsistent.");
        }
    }

    private void ThrowIfDisposedLocked()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GuardianOutputCoordinator));
    }

    private sealed class TrackedCapability(
        GuardianOutputRegistration registration,
        GuardianHostOperationKind operationKind,
        PublicJobId? correlatedJobId,
        PublicJobId? backgroundJobId)
    {
        internal GuardianOutputRegistration Registration { get; } = registration;
        internal GuardianHostOperationKind OperationKind { get; } = operationKind;
        internal PublicJobId? CorrelatedJobId { get; } = correlatedJobId;
        internal PublicJobId? BackgroundJobId { get; } = backgroundJobId;
        internal bool CapabilityActive { get; set; } = true;
        internal bool BackgroundResponseSucceeded { get; set; }
        internal GuardianOutputTerminal? Terminal { get; set; }
    }
}
