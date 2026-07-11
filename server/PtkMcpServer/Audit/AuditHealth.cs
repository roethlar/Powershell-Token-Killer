using System.Text.RegularExpressions;

namespace PtkMcpServer.Audit;

public enum AuditHealthState
{
    Healthy,
    Degraded,
    Unavailable,
    Recovered,
}

public sealed record AuditRecoverySummary(
    string FailureClass,
    DateTimeOffset DegradedSinceUtc,
    long EmergencyProbeCount,
    DateTimeOffset? EmergencyProbeFirstUtc,
    DateTimeOffset? EmergencyProbeLastUtc);

public sealed record AuditHealthSnapshot(
    AuditHealthState State,
    AuditProtectionMode ProtectionMode,
    string? ExportConfigurationIdentity,
    string? FailureClass,
    DateTimeOffset? DegradedSinceUtc,
    long SpoolBytes,
    long SpoolCapacityBytes,
    long ReservedBytes,
    long EmergencyReserveBytes,
    long EmergencyReserveCapacityBytes,
    long EmergencyProbeCount,
    DateTimeOffset? EmergencyProbeFirstUtc,
    DateTimeOffset? EmergencyProbeLastUtc)
{
    public long EffectiveFreeBytes => Math.Max(0, SpoolCapacityBytes - SpoolBytes - ReservedBytes);
}

/// <summary>
/// Thread-safe, nonsecret audit availability and capacity state.
/// </summary>
public sealed class AuditHealth
{
    private static readonly Regex FailureClassPattern = new(
        "^[a-z][a-z0-9_.-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly object _gate = new();
    private readonly object _transitionGate = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly AuditProtectionMode _protectionMode;
    private readonly string? _exportConfigurationIdentity;
    private readonly long _spoolCapacityBytes;
    private readonly long _emergencyReserveCapacityBytes;

    private AuditHealthState _state = AuditHealthState.Healthy;
    private string? _failureClass;
    private DateTimeOffset? _degradedSinceUtc;
    private long _spoolBytes;
    private long _reservedBytes;
    private long _emergencyReserveBytes;
    private long _emergencyProbeCount;
    private DateTimeOffset? _emergencyProbeFirstUtc;
    private DateTimeOffset? _emergencyProbeLastUtc;
    private bool _recoveryInProgress;

    public AuditHealth(AuditOptions options, Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _protectionMode = options.ProtectionMode;
        _exportConfigurationIdentity = options.ExportConfigurationIdentity;
        _spoolCapacityBytes = options.AggregateBytes;
        _emergencyReserveCapacityBytes = options.EmergencyReserveBytes;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public AuditHealthSnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotLocked();
        }
    }

    public void UpdateStorageMetrics(long spoolBytes, long reservedBytes, long emergencyReserveBytes)
    {
        if (spoolBytes < 0 || spoolBytes > _spoolCapacityBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(spoolBytes));
        }

        if (reservedBytes < 0 || reservedBytes > _spoolCapacityBytes - spoolBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(reservedBytes));
        }

        if (emergencyReserveBytes < 0 || emergencyReserveBytes > _emergencyReserveCapacityBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(emergencyReserveBytes));
        }

        lock (_gate)
        {
            _spoolBytes = spoolBytes;
            _reservedBytes = reservedBytes;
            _emergencyReserveBytes = emergencyReserveBytes;
        }
    }

    public void MarkDegraded(string failureClass) => MarkUnhealthy(AuditHealthState.Degraded, failureClass);

    public void MarkUnavailable(string failureClass) => MarkUnhealthy(AuditHealthState.Unavailable, failureClass);

    public void MarkHealthy()
    {
        lock (_transitionGate)
        {
            WaitForRecoveryLocked();
            lock (_gate)
            {
                if (_state is AuditHealthState.Degraded or AuditHealthState.Unavailable)
                {
                    throw new InvalidOperationException(
                        "An unhealthy audit interval must be closed by a successfully persisted recovery event.");
                }

                _state = AuditHealthState.Healthy;
                _failureClass = null;
                _degradedSinceUtc = null;
            }
        }
    }

    /// <summary>
    /// Records the only unrecorded diagnostic permitted while audit is
    /// unavailable. Probe timestamps and count are kept solely in memory.
    /// </summary>
    public AuditHealthSnapshot RecordEmergencyStateProbe()
    {
        if (TryRecordEmergencyStateProbe(out var snapshot)) return snapshot;
        throw new InvalidOperationException(
            "Emergency state probe raced audit recovery or requires unavailable audit state.");
    }

    /// <summary>
    /// Nonblocking emergency-probe admission. Recovery persistence deliberately
    /// runs without the transition lock; a state request that intersects it
    /// receives the current snapshot immediately instead of waiting on storage
    /// or reopening a stale outage after recovery wins.
    /// </summary>
    public bool TryRecordEmergencyStateProbe(out AuditHealthSnapshot snapshot)
    {
        lock (_transitionGate)
        {
            if (_recoveryInProgress)
            {
                lock (_gate)
                {
                    snapshot = SnapshotLocked();
                    return false;
                }
            }

            lock (_gate)
            {
                if (_state != AuditHealthState.Unavailable)
                {
                    snapshot = SnapshotLocked();
                    return false;
                }

                var now = GetUtcNow();
                _emergencyProbeCount = checked(_emergencyProbeCount + 1);
                _emergencyProbeFirstUtc ??= now;
                _emergencyProbeLastUtc = now;
                snapshot = SnapshotLocked();
                return true;
            }
        }
    }

    /// <summary>
    /// Serializes recovery against emergency probes and state transitions and
    /// consumes the gap only when the supplied durable append succeeds. The
    /// callback runs without either health lock, so it may snapshot health or
    /// update capacity metrics while persisting the event.
    /// </summary>
    public bool TryRecover(
        Func<AuditRecoverySummary, bool> tryPersistRecovery,
        out AuditRecoverySummary? summary)
    {
        ArgumentNullException.ThrowIfNull(tryPersistRecovery);

        lock (_transitionGate)
        {
            WaitForRecoveryLocked();
            _recoveryInProgress = true;
        }

        try
        {
            AuditRecoverySummary candidate;
            lock (_gate)
            {
                if (_state is not (AuditHealthState.Degraded or AuditHealthState.Unavailable))
                {
                    summary = null;
                    return false;
                }

                candidate = new AuditRecoverySummary(
                    _failureClass!,
                    _degradedSinceUtc!.Value,
                    _emergencyProbeCount,
                    _emergencyProbeFirstUtc,
                    _emergencyProbeLastUtc);
            }

            if (!tryPersistRecovery(candidate))
            {
                summary = null;
                return false;
            }

            lock (_gate)
            {
                _state = AuditHealthState.Recovered;
                _emergencyProbeCount = 0;
                _emergencyProbeFirstUtc = null;
                _emergencyProbeLastUtc = null;
                summary = candidate;
                return true;
            }
        }
        finally
        {
            lock (_transitionGate)
            {
                _recoveryInProgress = false;
                Monitor.PulseAll(_transitionGate);
            }
        }
    }

    private void MarkUnhealthy(AuditHealthState state, string failureClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureClass);
        if (!FailureClassPattern.IsMatch(failureClass))
        {
            throw new ArgumentException(
                "Failure class must be a lowercase machine code of at most 64 characters.",
                nameof(failureClass));
        }

        lock (_transitionGate)
        {
            WaitForRecoveryLocked();
            lock (_gate)
            {
                if (_state is AuditHealthState.Healthy or AuditHealthState.Recovered)
                {
                    _degradedSinceUtc = GetUtcNow();
                }

                _state = state;
                _failureClass = failureClass;
            }
        }
    }

    private AuditHealthSnapshot SnapshotLocked() => new(
        _state,
        _protectionMode,
        _exportConfigurationIdentity,
        _failureClass,
        _degradedSinceUtc,
        _spoolBytes,
        _spoolCapacityBytes,
        _reservedBytes,
        _emergencyReserveBytes,
        _emergencyReserveCapacityBytes,
        _emergencyProbeCount,
        _emergencyProbeFirstUtc,
        _emergencyProbeLastUtc);

    private DateTimeOffset GetUtcNow()
    {
        var now = _utcNow();
        if (now.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The audit clock must return UTC timestamps.");
        }

        return now;
    }

    private void WaitForRecoveryLocked()
    {
        while (_recoveryInProgress)
        {
            Monitor.Wait(_transitionGate);
        }
    }
}
