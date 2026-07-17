using PtkMcpGuardian.Ownership;
using PtkSharedContracts;

namespace PtkMcpGuardian.Lifecycle;

/// <summary>
/// Deterministic, unwired owner of one guardian boot's host lifecycle. Long
/// waits and clock advancement are explicit inputs; snapshots never advance
/// recovery. Launch and first-write boundary callbacks execute while the one
/// lifecycle gate still owns their permission.
/// </summary>
internal sealed class GuardianHostLifecycleController : IOrderedOwnedLifetime
{
    internal static readonly TimeSpan HostContainmentGrace = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly GuardianBootId _guardianBootId;
    private readonly IHostGenerationAllocator _generationAllocator;
    private readonly IGuardianHostBootIdSource _bootIdSource;
    private readonly IGuardianHostStartupDeadlineSource _startupDeadlineSource;
    private readonly IGuardianHostAttemptFactory _attemptFactory;
    private readonly TimeProvider _timeProvider;
    private readonly RecoveryCircuitMachine _recoveryCircuit;

    private GuardianHostAttemptLease? _current;
    private PublicHostState _state = PublicHostState.Absent;
    private RecoveryPhase? _phase;
    private PendingContainmentAction _pendingContainmentAction;
    private long _containmentRecoveryAttempt;
    private bool _initialAttemptStarted;
    private bool _terminalShutdown;
    private GuardianHostPermanentStopReason? _permanentStopReason;
    private GuardianHostLossReason? _lastLossReason;
    private PublicRecoveryDetailCode? _lastFailureCode;

    internal GuardianHostLifecycleController(
        GuardianBootId guardianBootId,
        IHostGenerationAllocator generationAllocator,
        IGuardianHostBootIdSource bootIdSource,
        IGuardianHostStartupDeadlineSource startupDeadlineSource,
        IGuardianHostAttemptFactory attemptFactory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(guardianBootId);
        ArgumentNullException.ThrowIfNull(generationAllocator);
        ArgumentNullException.ThrowIfNull(bootIdSource);
        ArgumentNullException.ThrowIfNull(startupDeadlineSource);
        ArgumentNullException.ThrowIfNull(attemptFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _guardianBootId = guardianBootId;
        _generationAllocator = generationAllocator;
        _bootIdSource = bootIdSource;
        _startupDeadlineSource = startupDeadlineSource;
        _attemptFactory = attemptFactory;
        _timeProvider = timeProvider;
        _recoveryCircuit = new RecoveryCircuitMachine(timeProvider);
    }

    internal GuardianHostStartTransition StartInitial()
    {
        lock (_gate)
        {
            if (_initialAttemptStarted || _terminalShutdown ||
                _permanentStopReason is not null ||
                _state != PublicHostState.Absent)
            {
                return RefusedStartLocked();
            }

            _initialAttemptStarted = true;
            return StartAttemptLocked(recoveryLease: null, isInitialAttempt: true);
        }
    }

    /// <summary>
    /// Explicit scheduler edge. Merely advancing the injected clock or reading
    /// state never acquires a backoff or half-open attempt.
    /// </summary>
    internal GuardianHostStartTransition TryStartDueRecovery()
    {
        lock (_gate)
        {
            if (_terminalShutdown || _permanentStopReason is not null ||
                _current is not null)
            {
                return RefusedStartLocked();
            }

            var recoveryLease = _recoveryCircuit.TryAcquireDueAttempt();
            return recoveryLease is null
                ? RefusedStartLocked()
                : StartAttemptLocked(recoveryLease, isInitialAttempt: false);
        }
    }

    internal bool MarkBootstrapping(GuardianHostAttemptLease attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate)
        {
            if (!ReferenceEquals(_current, attempt) ||
                attempt.Stage != GuardianHostAttemptStage.Launching ||
                _terminalShutdown)
            {
                return false;
            }

            attempt.Stage = GuardianHostAttemptStage.Bootstrapping;
            if (attempt.IsInitialAttempt)
            {
                _state = PublicHostState.Starting;
                _phase = null;
            }
            else if (attempt.RecoveryLease?.IsHalfOpen == true)
            {
                _state = PublicHostState.HalfOpen;
                _phase = RecoveryPhase.HalfOpen;
            }
            else
            {
                _state = PublicHostState.Recovering;
                _phase = RecoveryPhase.Bootstrap;
            }
            return true;
        }
    }

    internal bool MarkReady(GuardianHostAttemptLease attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate)
        {
            if (!ReferenceEquals(_current, attempt) ||
                attempt.Stage is not (
                    GuardianHostAttemptStage.Launching or
                    GuardianHostAttemptStage.Bootstrapping) ||
                _terminalShutdown)
            {
                return false;
            }

            if (attempt.RecoveryLease is { } recoveryLease &&
                !_recoveryCircuit.MarkReady(recoveryLease))
            {
                return false;
            }

            attempt.Stage = GuardianHostAttemptStage.Ready;
            attempt.EverReady = true;
            _state = PublicHostState.Ready;
            _phase = null;
            return true;
        }
    }

    internal bool TryCompleteReadyStability(GuardianHostAttemptLease attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate)
        {
            return ReferenceEquals(_current, attempt) &&
                attempt.Stage == GuardianHostAttemptStage.Ready &&
                attempt.RecoveryLease is { } recoveryLease &&
                !_terminalShutdown &&
                _recoveryCircuit.TryCompleteReadyStability(recoveryLease);
        }
    }

    internal GuardianHostWriteDisposition BeginFirstWrite(
        GuardianHostAttemptLease attempt,
        Action<GuardianHostIdentity> firstPossiblyWriting)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(firstPossiblyWriting);
        lock (_gate)
        {
            if (_terminalShutdown || _permanentStopReason is not null)
                return GuardianHostWriteDisposition.Stopped;
            if (!ReferenceEquals(_current, attempt))
                return GuardianHostWriteDisposition.StaleAttempt;
            if (_state != PublicHostState.Ready ||
                attempt.Stage != GuardianHostAttemptStage.Ready)
            {
                return GuardianHostWriteDisposition.NotReady;
            }

            firstPossiblyWriting(attempt.Identity);
            return GuardianHostWriteDisposition.Began;
        }
    }

    internal GuardianHostLifecycleLossDisposition ReportLoss(
        GuardianHostAttemptLease attempt,
        GuardianHostLossReason reason)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));

        lock (_gate)
        {
            if (!ReferenceEquals(_current, attempt))
                return GuardianHostLifecycleLossDisposition.StaleAttempt;
            if (attempt.Stage is GuardianHostAttemptStage.Containing or
                GuardianHostAttemptStage.ContainmentUnconfirmed or
                GuardianHostAttemptStage.DeathConfirmed)
            {
                return GuardianHostLifecycleLossDisposition.Duplicate;
            }
            if (_state == PublicHostState.Stopped)
                return GuardianHostLifecycleLossDisposition.Stopped;
            if (reason == GuardianHostLossReason.OperatorRecycle &&
                attempt.Stage != GuardianHostAttemptStage.Ready)
            {
                return GuardianHostLifecycleLossDisposition.StaleAttempt;
            }

            BeginContainmentLocked(attempt, reason);
            return GuardianHostLifecycleLossDisposition.BeganContainment;
        }
    }

    internal GuardianHostContainmentTransition ObserveContainmentDeadline(
        GuardianHostAttemptLease attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate)
        {
            if (!ReferenceEquals(_current, attempt))
            {
                return new(
                    GuardianHostContainmentDisposition.StaleAttempt,
                    StartedAttempt: null);
            }
            if (attempt.Stage == GuardianHostAttemptStage.ContainmentUnconfirmed)
            {
                return new(
                    GuardianHostContainmentDisposition.Duplicate,
                    StartedAttempt: null);
            }
            if (attempt.Stage != GuardianHostAttemptStage.Containing ||
                attempt.ContainmentDeadline is not { } deadline)
            {
                return new(
                    GuardianHostContainmentDisposition.StaleAttempt,
                    StartedAttempt: null);
            }
            if (_timeProvider.GetElapsedTime(
                    deadline.StartedTimestamp,
                    _timeProvider.GetTimestamp()) < HostContainmentGrace)
            {
                return new(
                    GuardianHostContainmentDisposition.Pending,
                    StartedAttempt: null);
            }

            attempt.Stage = GuardianHostAttemptStage.ContainmentUnconfirmed;
            _state = PublicHostState.ContainmentUnconfirmed;
            _phase = null;
            _lastFailureCode = PublicRecoveryDetailCode.HostContainmentUnconfirmed;
            return new(
                GuardianHostContainmentDisposition.MarkedUnconfirmed,
                StartedAttempt: null);
        }
    }

    internal GuardianHostContainmentTransition ConfirmContainment(
        GuardianHostAttemptLease attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate)
        {
            if (!ReferenceEquals(_current, attempt))
            {
                return new(
                    GuardianHostContainmentDisposition.StaleAttempt,
                    StartedAttempt: null);
            }
            if (attempt.Stage == GuardianHostAttemptStage.DeathConfirmed)
            {
                return new(
                    GuardianHostContainmentDisposition.Duplicate,
                    StartedAttempt: null);
            }
            if (attempt.Stage is not (
                    GuardianHostAttemptStage.Containing or
                    GuardianHostAttemptStage.ContainmentUnconfirmed))
            {
                return new(
                    GuardianHostContainmentDisposition.StaleAttempt,
                    StartedAttempt: null);
            }

            attempt.Stage = GuardianHostAttemptStage.DeathConfirmed;
            ReleaseAttemptLocked(attempt);
            _current = null;
            var pending = _pendingContainmentAction;
            _pendingContainmentAction = PendingContainmentAction.None;
            _containmentRecoveryAttempt = 0;

            if (_terminalShutdown ||
                pending == PendingContainmentAction.Stop ||
                _permanentStopReason is not null)
            {
                StopPermanentlyLocked(
                    _permanentStopReason ?? GuardianHostPermanentStopReason.TerminalShutdown);
                return new(
                    GuardianHostContainmentDisposition.Confirmed,
                    StartedAttempt: null);
            }

            GuardianHostStartTransition start;
            if (pending == PendingContainmentAction.Recycle)
            {
                var replacementLease = attempt.RecoveryLease is null
                    ? _recoveryCircuit.BeginRecovery()
                    : _recoveryCircuit.BeginIntentionalReplacementAfterFrozenStability(
                        attempt.RecoveryLease);
                start = replacementLease is null
                    ? StopForRecoveryExhaustionLocked()
                    : StartAttemptLocked(replacementLease, isInitialAttempt: false);
            }
            else
            {
                start = ContinueAfterFailedGenerationLocked(
                    attempt.RecoveryLease,
                    recoveryStabilityFrozen: true);
            }

            return new(
                GuardianHostContainmentDisposition.Confirmed,
                start.Attempt);
        }
    }

    internal GuardianHostLifecycleSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new(
                CreatePublicSnapshotLocked(),
                _terminalShutdown,
                _permanentStopReason,
                _lastLossReason);
        }
    }

    public Task ShutdownAsync()
    {
        lock (_gate)
        {
            if (_terminalShutdown)
                return Task.CompletedTask;

            _terminalShutdown = true;
            _permanentStopReason = GuardianHostPermanentStopReason.TerminalShutdown;
            _recoveryCircuit.Stop();

            if (_current is { } attempt)
            {
                _pendingContainmentAction = PendingContainmentAction.Stop;
                _lastLossReason = GuardianHostLossReason.TerminalShutdown;
                if (attempt.Stage is not (
                        GuardianHostAttemptStage.Containing or
                        GuardianHostAttemptStage.ContainmentUnconfirmed))
                {
                    BeginContainmentLocked(
                        attempt,
                        GuardianHostLossReason.TerminalShutdown);
                }
            }
            else
            {
                StopPermanentlyLocked(GuardianHostPermanentStopReason.TerminalShutdown);
            }
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _ = ShutdownAsync();
    }

    private GuardianHostStartTransition StartAttemptLocked(
        RecoveryAttemptLease? recoveryLease,
        bool isInitialAttempt)
    {
        HostGeneration generation;
        try
        {
            generation = _generationAllocator.Allocate();
        }
        catch (GuardianIdentityExhaustedException exception)
            when (exception.DetailKind == GuardianIdentityExhaustionKind.HostGeneration)
        {
            return StopForRecoveryExhaustionLocked();
        }

        GuardianHostIdentity identity;
        GuardianHostStartupDeadline startupDeadline;
        IGuardianHostAttemptResources resources;
        try
        {
            var hostBootId = _bootIdSource.Next() ??
                throw new InvalidOperationException("The host boot-ID source returned null.");
            identity = new GuardianHostIdentity(
                _guardianBootId,
                hostBootId,
                generation);
            startupDeadline = _startupDeadlineSource.Next();
            resources = _attemptFactory.Prepare(identity, startupDeadline) ??
                throw new InvalidOperationException("The host attempt factory returned null.");
        }
        catch
        {
            _lastFailureCode = PublicRecoveryDetailCode.HostStartFailed;
            return isInitialAttempt
                ? StopInitialStartLocked(GuardianHostStartDisposition.ProvedNoChild)
                : ContinueAfterFailedGenerationLocked(recoveryLease);
        }

        var attempt = new GuardianHostAttemptLease(
            identity,
            startupDeadline,
            resources,
            recoveryLease,
            isInitialAttempt);
        _current = attempt;
        _state = isInitialAttempt
            ? PublicHostState.Starting
            : recoveryLease?.IsHalfOpen == true
                ? PublicHostState.HalfOpen
                : PublicHostState.Recovering;
        _phase = isInitialAttempt
            ? null
            : recoveryLease?.IsHalfOpen == true
                ? RecoveryPhase.HalfOpen
                : RecoveryPhase.Attempting;

        GuardianHostLaunchOutcome launchOutcome;
        try
        {
            launchOutcome = resources.Launch();
            if (!Enum.IsDefined(launchOutcome))
                throw new InvalidOperationException("The host launch outcome is invalid.");
        }
        catch
        {
            _lastFailureCode = PublicRecoveryDetailCode.HostStartFailed;
            BeginContainmentLocked(
                attempt,
                GuardianHostLossReason.InitializationFailure);
            return new(
                GuardianHostStartDisposition.ContainmentStarted,
                attempt);
        }

        if (attempt.Stage is GuardianHostAttemptStage.Containing or
            GuardianHostAttemptStage.ContainmentUnconfirmed)
        {
            return new(
                GuardianHostStartDisposition.ContainmentStarted,
                attempt);
        }

        if (launchOutcome == GuardianHostLaunchOutcome.ProvedNoChild)
        {
            _current = null;
            ReleaseAttemptLocked(attempt);
            _lastFailureCode = PublicRecoveryDetailCode.HostStartFailed;
            return isInitialAttempt
                ? StopInitialStartLocked(GuardianHostStartDisposition.ProvedNoChild)
                : ContinueAfterFailedGenerationLocked(recoveryLease);
        }

        return new(GuardianHostStartDisposition.Started, attempt);
    }

    private GuardianHostStartTransition ContinueAfterFailedGenerationLocked(
        RecoveryAttemptLease? failedLease,
        bool recoveryStabilityFrozen = false)
    {
        if (failedLease is null)
        {
            var immediate = _recoveryCircuit.BeginRecovery();
            return immediate is null
                ? StopForRecoveryExhaustionLocked()
                : StartAttemptLocked(immediate, isInitialAttempt: false);
        }

        var failure = recoveryStabilityFrozen
            ? _recoveryCircuit.ReportGenerationFailureAfterFrozenStability(failedLease)
            : _recoveryCircuit.ReportGenerationFailure(failedLease);
        if (!failure.Accepted || failure.Exhausted)
            return StopForRecoveryExhaustionLocked();
        if (failure.ImmediateAttempt is { } immediateAttempt)
            return StartAttemptLocked(immediateAttempt, isInitialAttempt: false);

        ApplyScheduledRecoveryStateLocked();
        return new(GuardianHostStartDisposition.ProvedNoChild, Attempt: null);
    }

    private void BeginContainmentLocked(
        GuardianHostAttemptLease attempt,
        GuardianHostLossReason reason)
    {
        if (attempt.RecoveryLease is { } recoveryLease)
            _recoveryCircuit.FreezeReadyStability(recoveryLease);

        _lastLossReason = reason;
        attempt.Stage = GuardianHostAttemptStage.Containing;
        _pendingContainmentAction = PendingActionForLocked(attempt, reason);
        _containmentRecoveryAttempt = ContainmentAttemptForLocked(attempt, reason);
        _state = PublicHostState.Recovering;
        _phase = RecoveryPhase.Containment;

        if (reason == GuardianHostLossReason.ContractMismatch)
        {
            _permanentStopReason = GuardianHostPermanentStopReason.ContractMismatch;
            _lastFailureCode = PublicRecoveryDetailCode.HostContractMismatch;
        }
        else
        {
            if (reason == GuardianHostLossReason.InitializationFailure)
                _lastFailureCode = PublicRecoveryDetailCode.HostStartFailed;
            if (attempt.IsInitialAttempt && !attempt.EverReady)
            {
                _permanentStopReason = GuardianHostPermanentStopReason.InitialStartFailed;
                _lastFailureCode = PublicRecoveryDetailCode.HostStartFailed;
            }
        }

        var started = _timeProvider.GetTimestamp();
        var deadline = new GuardianHostContainmentDeadline(
            started,
            AddTimestamp(started, HostContainmentGrace));
        attempt.ContainmentDeadline = deadline;

        try
        {
            attempt.Resources.CloseTransport();
        }
        catch
        {
            // Closing failure never prevents the authoritative containment edge.
        }

        try
        {
            attempt.Resources.BeginContainment(deadline);
        }
        catch
        {
            // Deadline observation will publish uncertainty if no proof arrives.
        }
    }

    private PendingContainmentAction PendingActionForLocked(
        GuardianHostAttemptLease attempt,
        GuardianHostLossReason reason)
    {
        if (_terminalShutdown || reason == GuardianHostLossReason.TerminalShutdown ||
            reason == GuardianHostLossReason.ContractMismatch ||
            attempt.IsInitialAttempt && !attempt.EverReady)
        {
            return PendingContainmentAction.Stop;
        }
        return reason == GuardianHostLossReason.OperatorRecycle
            ? PendingContainmentAction.Recycle
            : PendingContainmentAction.Recover;
    }

    private long ContainmentAttemptForLocked(
        GuardianHostAttemptLease attempt,
        GuardianHostLossReason reason)
    {
        if (reason == GuardianHostLossReason.OperatorRecycle)
        {
            var current = _recoveryCircuit.Snapshot().RecoveryAttempt;
            return Math.Max(1, current);
        }
        if (attempt.RecoveryLease is null)
            return 1;

        var circuit = _recoveryCircuit.Snapshot();
        if (circuit.State == RecoveryCircuitState.Ready &&
            (circuit.StabilityDue || circuit.StabilityReset))
        {
            return 1;
        }

        try
        {
            return checked(attempt.RecoveryLease.AttemptOrdinal + 1);
        }
        catch (OverflowException)
        {
            _permanentStopReason = GuardianHostPermanentStopReason.IdentityExhausted;
            _pendingContainmentAction = PendingContainmentAction.Stop;
            return long.MaxValue;
        }
    }

    private void ApplyScheduledRecoveryStateLocked()
    {
        var circuit = _recoveryCircuit.Snapshot();
        _current = null;
        _phase = circuit.RecoveryPhase;
        _state = circuit.State switch
        {
            RecoveryCircuitState.Backoff => PublicHostState.Backoff,
            RecoveryCircuitState.CircuitOpen => PublicHostState.CircuitOpen,
            RecoveryCircuitState.Stopped => PublicHostState.Stopped,
            _ => throw new InvalidOperationException(
                "A failed host generation did not enter a scheduled state."),
        };
    }

    private PublicHostStateSnapshot CreatePublicSnapshotLocked()
    {
        var circuit = _recoveryCircuit.Snapshot();
        var identity = _current?.Identity;
        var recoveryAttempt = _state switch
        {
            PublicHostState.Starting or PublicHostState.Absent or PublicHostState.Stopped => 0,
            PublicHostState.Recovering when _phase == RecoveryPhase.Containment =>
                _containmentRecoveryAttempt,
            PublicHostState.ContainmentUnconfirmed => 0,
            PublicHostState.Ready when _current?.RecoveryLease is null => 0,
            _ => circuit.RecoveryAttempt,
        };
        var retryAfter = _state switch
        {
            PublicHostState.Recovering or PublicHostState.HalfOpen =>
                ContractLimits.MinimumRetryAfterMilliseconds,
            PublicHostState.Backoff or PublicHostState.CircuitOpen =>
                circuit.RetryAfterMilliseconds,
            _ => null,
        };

        return new PublicHostStateSnapshot(
            identity?.HostBootId,
            identity?.HostGeneration,
            _state,
            _phase,
            recoveryAttempt,
            retryAfter,
            readyForEffects: _state == PublicHostState.Ready,
            _lastFailureCode);
    }

    private GuardianHostStartTransition StopInitialStartLocked(
        GuardianHostStartDisposition disposition)
    {
        StopPermanentlyLocked(GuardianHostPermanentStopReason.InitialStartFailed);
        return new(disposition, Attempt: null);
    }

    private GuardianHostStartTransition StopForRecoveryExhaustionLocked()
    {
        StopPermanentlyLocked(GuardianHostPermanentStopReason.IdentityExhausted);
        return new(GuardianHostStartDisposition.PermanentlyStopped, Attempt: null);
    }

    private void StopPermanentlyLocked(GuardianHostPermanentStopReason reason)
    {
        _permanentStopReason = reason;
        _recoveryCircuit.Stop();
        _current = null;
        _state = PublicHostState.Stopped;
        _phase = null;
        _pendingContainmentAction = PendingContainmentAction.None;
        _containmentRecoveryAttempt = 0;
    }

    private GuardianHostStartTransition RefusedStartLocked() => new(
        _permanentStopReason is null
            ? GuardianHostStartDisposition.Refused
            : GuardianHostStartDisposition.PermanentlyStopped,
        Attempt: null);

    private long AddTimestamp(long start, TimeSpan duration)
    {
        var timestampTicks = checked((long)Math.Ceiling(
            duration.TotalSeconds * _timeProvider.TimestampFrequency));
        return checked(start + timestampTicks);
    }

    private static void ReleaseAttemptLocked(GuardianHostAttemptLease attempt)
    {
        try
        {
            attempt.Resources.Dispose();
        }
        catch
        {
            // Identity confirmation already owns safety; disposal is best effort.
        }
    }

    private enum PendingContainmentAction
    {
        None,
        Recover,
        Recycle,
        Stop,
    }
}
