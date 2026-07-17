using PtkSharedContracts;

namespace PtkMcpGuardian.Lifecycle;

internal enum RecoveryCircuitState
{
    Inactive,
    Attempting,
    Backoff,
    CircuitOpen,
    HalfOpen,
    Ready,
    Stopped,
}

internal sealed class RecoveryAttemptLease
{
    internal RecoveryAttemptLease(
        long leaseSequence,
        long attemptOrdinal,
        bool isHalfOpen)
    {
        LeaseSequence = leaseSequence;
        AttemptOrdinal = attemptOrdinal;
        IsHalfOpen = isHalfOpen;
    }

    internal long LeaseSequence { get; }
    internal long AttemptOrdinal { get; }
    internal bool IsHalfOpen { get; }
}

internal readonly record struct RecoveryFailureTransition(
    bool Accepted,
    RecoveryAttemptLease? ImmediateAttempt,
    bool Exhausted = false);

internal sealed record RecoveryCircuitSnapshot(
    RecoveryCircuitState State,
    RecoveryPhase? RecoveryPhase,
    long RecoveryAttempt,
    long ConsecutiveFailedGenerations,
    int? RetryAfterMilliseconds,
    bool ReadyForEffects,
    bool AttemptDue,
    bool StabilityDue,
    bool StabilityReset);

/// <summary>
/// Deterministic retry and circuit policy for one independently recoverable
/// scope. Reading a snapshot never advances a due attempt or completes the
/// ready-stability window; callers must explicitly acquire or complete those
/// transitions.
/// </summary>
internal sealed class RecoveryCircuitMachine
{
    internal static readonly TimeSpan ReadyStabilityWindow = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan CircuitOpenWindow = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan[] FailureBackoffs =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
    ];

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;

    private RecoveryCircuitState _state;
    private RecoveryAttemptLease? _activeLease;
    private long _leaseSequence;
    private long _attemptOrdinal;
    private long _consecutiveFailedGenerations;
    private long _phaseStartedTimestamp;
    private TimeSpan _scheduledDelay;
    private bool _activeAttemptWasHalfOpen;
    private bool _stabilityReset;

    internal RecoveryCircuitMachine(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _state = RecoveryCircuitState.Inactive;
    }

    /// <summary>
    /// Starts a fresh recovery cycle. Its first attempt is immediate and has
    /// ordinal one. A live, scheduled, ready, or stopped machine refuses a
    /// second cycle.
    /// </summary>
    internal RecoveryAttemptLease? BeginRecovery()
    {
        lock (_sync)
        {
            if (_state != RecoveryCircuitState.Inactive)
                return null;

            _attemptOrdinal = 1;
            _consecutiveFailedGenerations = 0;
            return StartAttemptLocked(isHalfOpen: false);
        }
    }

    /// <summary>
    /// Acquires a scheduled backoff or half-open attempt only after its
    /// monotonic delay. Concurrent callers can acquire it at most once.
    /// </summary>
    internal RecoveryAttemptLease? TryAcquireDueAttempt()
    {
        lock (_sync)
        {
            if (_state is not (RecoveryCircuitState.Backoff or RecoveryCircuitState.CircuitOpen) ||
                !DelayElapsedLocked())
            {
                return null;
            }

            return StartAttemptLocked(_state == RecoveryCircuitState.CircuitOpen);
        }
    }

    /// <summary>
    /// Makes the active generation immediately dispatchable, while retaining
    /// its failure history and attempt ordinal until the full stability window
    /// is explicitly completed.
    /// </summary>
    internal bool MarkReady(RecoveryAttemptLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_sync)
        {
            if (!OwnsActiveLeaseLocked(lease) ||
                _state is not (RecoveryCircuitState.Attempting or RecoveryCircuitState.HalfOpen))
            {
                return false;
            }

            _activeAttemptWasHalfOpen = _state == RecoveryCircuitState.HalfOpen;
            _state = RecoveryCircuitState.Ready;
            _phaseStartedTimestamp = _timeProvider.GetTimestamp();
            _scheduledDelay = ReadyStabilityWindow;
            _stabilityReset = false;
            return true;
        }
    }

    /// <summary>
    /// Resets failure history only after one uninterrupted ready generation
    /// has remained ready for exactly the stability window. Snapshot reads do
    /// not call or emulate this transition.
    /// </summary>
    internal bool TryCompleteReadyStability(RecoveryAttemptLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_sync)
        {
            if (_state != RecoveryCircuitState.Ready ||
                _stabilityReset ||
                !OwnsActiveLeaseLocked(lease) ||
                !DelayElapsedLocked())
            {
                return false;
            }

            ResetStabilityHistoryLocked();
            return true;
        }
    }

    /// <summary>
    /// Records failure or pre-stability loss of exactly the leased generation.
    /// A stable ready generation begins a new cycle and returns its immediate
    /// attempt-one lease. Stale and duplicate reports are inert.
    /// </summary>
    internal RecoveryFailureTransition ReportGenerationFailure(
        RecoveryAttemptLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_sync)
        {
            if (!OwnsActiveLeaseLocked(lease) ||
                _state is not (
                    RecoveryCircuitState.Attempting or
                    RecoveryCircuitState.HalfOpen or
                    RecoveryCircuitState.Ready))
            {
                return new RecoveryFailureTransition(false, null);
            }

            if (_state == RecoveryCircuitState.Ready &&
                !_stabilityReset &&
                DelayElapsedLocked())
            {
                ResetStabilityHistoryLocked();
            }

            if (_state == RecoveryCircuitState.Ready && _stabilityReset)
            {
                _attemptOrdinal = 1;
                var immediate = StartAttemptLocked(isHalfOpen: false);
                return new RecoveryFailureTransition(
                    Accepted: true,
                    ImmediateAttempt: immediate,
                    Exhausted: immediate is null);
            }

            var failedHalfOpen = _state == RecoveryCircuitState.HalfOpen ||
                _state == RecoveryCircuitState.Ready && _activeAttemptWasHalfOpen;
            _consecutiveFailedGenerations = checked(_consecutiveFailedGenerations + 1);
            if (!TryAdvanceAttemptOrdinalLocked())
                return new RecoveryFailureTransition(true, null, Exhausted: true);

            _activeLease = null;
            _stabilityReset = false;
            _activeAttemptWasHalfOpen = false;
            _phaseStartedTimestamp = _timeProvider.GetTimestamp();

            if (failedHalfOpen || _consecutiveFailedGenerations >= 6)
            {
                _state = RecoveryCircuitState.CircuitOpen;
                _scheduledDelay = CircuitOpenWindow;
            }
            else
            {
                _state = RecoveryCircuitState.Backoff;
                _scheduledDelay = FailureBackoffs[
                    checked((int)_consecutiveFailedGenerations - 1)];
            }

            return new RecoveryFailureTransition(true, null);
        }
    }

    internal RecoveryCircuitSnapshot Snapshot()
    {
        lock (_sync)
        {
            var scheduled = _state is RecoveryCircuitState.Backoff or
                RecoveryCircuitState.CircuitOpen;
            var stabilizing = _state == RecoveryCircuitState.Ready && !_stabilityReset;
            return new RecoveryCircuitSnapshot(
                _state,
                PhaseLocked(),
                _state is RecoveryCircuitState.Inactive or RecoveryCircuitState.Stopped
                    ? 0
                    : _attemptOrdinal,
                _consecutiveFailedGenerations,
                RetryAfterMillisecondsLocked(),
                _state == RecoveryCircuitState.Ready,
                scheduled && DelayElapsedLocked(),
                stabilizing && DelayElapsedLocked(),
                _state == RecoveryCircuitState.Ready && _stabilityReset);
        }
    }

    internal void Stop()
    {
        lock (_sync)
        {
            if (_state == RecoveryCircuitState.Stopped)
                return;

            _state = RecoveryCircuitState.Stopped;
            _activeLease = null;
            _attemptOrdinal = 0;
            _phaseStartedTimestamp = 0;
            _scheduledDelay = TimeSpan.Zero;
            _activeAttemptWasHalfOpen = false;
            _stabilityReset = false;
        }
    }

    private RecoveryAttemptLease? StartAttemptLocked(bool isHalfOpen)
    {
        if (_state == RecoveryCircuitState.Stopped)
            return null;

        try
        {
            _leaseSequence = checked(_leaseSequence + 1);
        }
        catch (OverflowException)
        {
            StopForExhaustionLocked();
            return null;
        }

        var lease = new RecoveryAttemptLease(
            _leaseSequence,
            _attemptOrdinal,
            isHalfOpen);
        _activeLease = lease;
        _state = isHalfOpen
            ? RecoveryCircuitState.HalfOpen
            : RecoveryCircuitState.Attempting;
        _phaseStartedTimestamp = _timeProvider.GetTimestamp();
        _scheduledDelay = TimeSpan.Zero;
        _activeAttemptWasHalfOpen = isHalfOpen;
        _stabilityReset = false;
        return lease;
    }

    private bool TryAdvanceAttemptOrdinalLocked()
    {
        try
        {
            _attemptOrdinal = checked(_attemptOrdinal + 1);
            return true;
        }
        catch (OverflowException)
        {
            StopForExhaustionLocked();
            return false;
        }
    }

    private void ResetStabilityHistoryLocked()
    {
        _attemptOrdinal = 0;
        _consecutiveFailedGenerations = 0;
        _activeAttemptWasHalfOpen = false;
        _stabilityReset = true;
        _scheduledDelay = TimeSpan.Zero;
    }

    private void StopForExhaustionLocked()
    {
        _state = RecoveryCircuitState.Stopped;
        _activeLease = null;
        _attemptOrdinal = 0;
        _phaseStartedTimestamp = 0;
        _scheduledDelay = TimeSpan.Zero;
        _activeAttemptWasHalfOpen = false;
        _stabilityReset = false;
    }

    private bool OwnsActiveLeaseLocked(RecoveryAttemptLease lease) =>
        ReferenceEquals(_activeLease, lease);

    private RecoveryPhase? PhaseLocked() => _state switch
    {
        RecoveryCircuitState.Attempting => RecoveryPhase.Attempting,
        RecoveryCircuitState.Backoff => RecoveryPhase.Backoff,
        RecoveryCircuitState.CircuitOpen => RecoveryPhase.CircuitOpen,
        RecoveryCircuitState.HalfOpen => RecoveryPhase.HalfOpen,
        _ => null,
    };

    private int? RetryAfterMillisecondsLocked() => _state switch
    {
        RecoveryCircuitState.Attempting or RecoveryCircuitState.HalfOpen =>
            ContractLimits.MinimumRetryAfterMilliseconds,
        RecoveryCircuitState.Backoff or RecoveryCircuitState.CircuitOpen =>
            RemainingPollMillisecondsLocked(),
        _ => null,
    };

    private int RemainingPollMillisecondsLocked()
    {
        var remaining = _scheduledDelay - ElapsedLocked();
        var ceilingMilliseconds = (long)Math.Ceiling(remaining.TotalMilliseconds);
        return checked((int)Math.Clamp(
            ceilingMilliseconds,
            ContractLimits.MinimumRetryAfterMilliseconds,
            ContractLimits.MaximumRetryAfterMilliseconds));
    }

    private bool DelayElapsedLocked() => ElapsedLocked() >= _scheduledDelay;

    private TimeSpan ElapsedLocked() =>
        _timeProvider.GetElapsedTime(
            _phaseStartedTimestamp,
            _timeProvider.GetTimestamp());
}
