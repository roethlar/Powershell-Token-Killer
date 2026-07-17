using System.Globalization;
using System.Text.RegularExpressions;

namespace PtkMcpServer.Audit;

public enum AuditHealthState
{
    Healthy,
    Degraded,
    Unavailable,
    Recovered,
}

internal static class AuditExporterHealthText
{
    internal static string FormatNormal(AuditExporterHealthSnapshot exporter)
    {
        var state = Machine(exporter.State);
        var first = exporter.State == AuditExporterState.Disabled
            ? $"audit exporter: {state} (local-only), warning {Bool(exporter.HasHealthWarning)}"
            : $"audit exporter: {state}, warning {Bool(exporter.HasHealthWarning)}";
        var lines = new List<string> { first };
        if (exporter.StateChangedUtc is { } stateChangedUtc)
            lines[0] += $", since {Utc(stateChangedUtc)}";
        if (exporter.NextActionUtc is { } nextActionUtc)
            lines[0] += $", next action {Utc(nextActionUtc)}";
        if (exporter.ScheduledDelay is { } scheduledDelay)
        {
            lines[0] += $", delay {scheduledDelay.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}ms";
        }

        if (exporter.Blocked is { } blocked)
        {
            lines.Add(
                $"audit exporter block: supervisor boot {blocked.SupervisorBootId:D}, " +
                $"current {Bool(blocked.IsCurrentBoot)}, event {GuidValue(blocked.EventId)}, " +
                $"detail {blocked.DetailCode}, failure {blocked.FailureClass ?? "unknown"}");
        }
        if (exporter.LastProgress is { } progress)
            lines.Add(FormatActivity("progress", progress));
        if (exporter.LastAcknowledgment is { } acknowledgment)
            lines.Add(FormatActivity("acknowledgment", acknowledgment));
        return string.Join('\n', lines);
    }

    internal static IEnumerable<string> FormatEmergency(
        AuditExporterHealthSnapshot exporter)
    {
        yield return $"exporter_state={Machine(exporter.State)}";
        yield return $"exporter_warning={Bool(exporter.HasHealthWarning)}";
        if (exporter.StateChangedUtc is { } stateChangedUtc)
            yield return $"exporter_state_changed_utc={Utc(stateChangedUtc)}";
        if (exporter.ScheduledDelay is { } scheduledDelay)
        {
            yield return "exporter_scheduled_delay_ms=" +
                scheduledDelay.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);
        }
        if (exporter.NextActionUtc is { } nextActionUtc)
            yield return $"exporter_next_action_utc={Utc(nextActionUtc)}";

        if (exporter.Blocked is { } blocked)
        {
            yield return $"exporter_blocked_supervisor_boot_id={blocked.SupervisorBootId:D}";
            yield return $"exporter_blocked_is_current_boot={Bool(blocked.IsCurrentBoot)}";
            yield return $"exporter_blocked_event_id={GuidValue(blocked.EventId)}";
            yield return $"exporter_blocked_detail={blocked.DetailCode}";
            yield return $"exporter_blocked_failure_class={blocked.FailureClass ?? "unknown"}";
        }
        if (exporter.LastProgress is { } progress)
        {
            foreach (var line in FormatEmergencyActivity("last_progress", progress))
                yield return line;
        }
        if (exporter.LastAcknowledgment is { } acknowledgment)
        {
            foreach (var line in FormatEmergencyActivity("last_acknowledgment", acknowledgment))
                yield return line;
        }
    }

    private static string FormatActivity(
        string label,
        AuditExporterActivitySnapshot activity) =>
        $"audit exporter {label}: {Utc(activity.ObservedUtc)}, " +
        $"supervisor boot {activity.SupervisorBootId:D}, " +
        $"current {Bool(activity.IsCurrentBoot)}, event {GuidValue(activity.EventId)}, " +
        $"detail {activity.DetailCode}, warning {Bool(activity.HasHealthWarning)}";

    private static IEnumerable<string> FormatEmergencyActivity(
        string prefix,
        AuditExporterActivitySnapshot activity)
    {
        yield return $"exporter_{prefix}_utc={Utc(activity.ObservedUtc)}";
        yield return $"exporter_{prefix}_supervisor_boot_id={activity.SupervisorBootId:D}";
        yield return $"exporter_{prefix}_is_current_boot={Bool(activity.IsCurrentBoot)}";
        yield return $"exporter_{prefix}_event_id={GuidValue(activity.EventId)}";
        yield return $"exporter_{prefix}_detail={activity.DetailCode}";
        yield return $"exporter_{prefix}_warning={Bool(activity.HasHealthWarning)}";
    }

    private static string Machine(AuditExporterState state) =>
        state.ToString().ToLowerInvariant();

    private static string Bool(bool value) => value ? "true" : "false";

    private static string GuidValue(Guid? value) =>
        value?.ToString("D") ?? "none";

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

public enum AuditExporterState
{
    Disabled,
    Created,
    Running,
    Idle,
    Retrying,
    Stalled,
    Completed,
    Stopped,
    Faulted,
}

public sealed record AuditExporterActivitySnapshot(
    DateTimeOffset ObservedUtc,
    Guid SupervisorBootId,
    bool IsCurrentBoot,
    Guid? EventId,
    string DetailCode,
    bool HasHealthWarning);

public sealed record AuditExporterBlockSnapshot(
    Guid SupervisorBootId,
    bool IsCurrentBoot,
    Guid? EventId,
    string DetailCode,
    string? FailureClass);

public sealed record AuditExporterHealthSnapshot(
    AuditExporterState State,
    DateTimeOffset? StateChangedUtc,
    TimeSpan? ScheduledDelay,
    DateTimeOffset? NextActionUtc,
    AuditExporterBlockSnapshot? Blocked,
    AuditExporterActivitySnapshot? LastProgress,
    AuditExporterActivitySnapshot? LastAcknowledgment,
    bool HasHealthWarning);

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
    DateTimeOffset? EmergencyProbeLastUtc,
    AuditExporterHealthSnapshot Exporter)
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
    private static readonly HashSet<string> ExportDetailCodes = new(
        [
            "chain.complete",
            "http.200.content_type",
            "http.200.protobuf",
            "http.200.rejected_count",
            "http.200.response_too_large",
            "live.tail",
            "mapping.identity",
            "otlp.acknowledged",
            "otlp.acknowledged_warning",
            "otlp.partial_rejection",
            "prefix.complete",
            "tls.validation",
            "transport.connection",
            "transport.timeout",
        ],
        StringComparer.Ordinal);

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
    private AuditExporterHealthSnapshot _exporter;

    public AuditHealth(AuditOptions options, Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _protectionMode = options.ProtectionMode;
        _exportConfigurationIdentity = options.ExportConfigurationIdentity;
        _spoolCapacityBytes = options.AggregateBytes;
        _emergencyReserveCapacityBytes = options.EmergencyReserveBytes;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _exporter = new AuditExporterHealthSnapshot(
            options.ProtectionMode == AuditProtectionMode.LocalOnly
                ? AuditExporterState.Disabled
                : AuditExporterState.Created,
            StateChangedUtc: null,
            ScheduledDelay: null,
            NextActionUtc: null,
            Blocked: null,
            LastProgress: null,
            LastAcknowledgment: null,
            HasHealthWarning: false);
        ExportObserver = new ExportHealthObserver(this);
    }

    /// <summary>
    /// A write-only in-memory observer for the export loop. It cannot append
    /// audit records or change journal availability, which prevents exporter
    /// telemetry from creating an audit/export feedback loop.
    /// </summary>
    internal IAuditExportHealthObserver ExportObserver { get; }

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
        _emergencyProbeLastUtc,
        _exporter);

    private void ObserveExport(AuditExportHealthObservation observation)
    {
        if (_protectionMode == AuditProtectionMode.LocalOnly)
            return;

        var observedUtc = observation.ObservedAtUtc.ToUniversalTime();
        lock (_gate)
        {
            var nextState = MapExporterState(observation.Snapshot);
            var stateChangedUtc = nextState == _exporter.State
                ? _exporter.StateChangedUtc
                : observedUtc;
            var scheduledDelay = nextState is AuditExporterState.Idle or
                AuditExporterState.Retrying or AuditExporterState.Stalled
                    ? observation.Snapshot.ScheduledDelay
                    : null;
            var nextActionUtc = AddDelay(observedUtc, scheduledDelay);
            var blocked = _exporter.Blocked;
            var lastProgress = _exporter.LastProgress;
            var lastAcknowledgment = _exporter.LastAcknowledgment;
            var hasHealthWarning = _exporter.HasHealthWarning;

            if (observation.ObservedStep is { } step)
            {
                var detailCode = SafeDetailCode(step.DetailCode);
                if (step.Kind == AuditExportCoordinatorStepKind.Blocked)
                {
                    blocked = new AuditExporterBlockSnapshot(
                        step.SupervisorBootId,
                        step.IsCurrentBoot,
                        step.EventId,
                        detailCode,
                        FailureClass(step.FailureClass));
                }

                if (step.Kind is AuditExportCoordinatorStepKind.Advanced or
                    AuditExportCoordinatorStepKind.Progressed or
                    AuditExportCoordinatorStepKind.Complete)
                {
                    lastProgress = new AuditExporterActivitySnapshot(
                        observedUtc,
                        step.SupervisorBootId,
                        step.IsCurrentBoot,
                        step.EventId,
                        detailCode,
                        step.HasHealthWarning);
                    if (step.EventId is not null &&
                        step.Kind is AuditExportCoordinatorStepKind.Advanced or
                            AuditExportCoordinatorStepKind.Complete)
                    {
                        lastAcknowledgment = lastProgress;
                        hasHealthWarning = step.HasHealthWarning;
                    }
                }
            }

            _exporter = new AuditExporterHealthSnapshot(
                nextState,
                stateChangedUtc,
                scheduledDelay,
                nextActionUtc,
                blocked,
                lastProgress,
                lastAcknowledgment,
                hasHealthWarning);
        }
    }

    private static AuditExporterState MapExporterState(
        AuditExportLoopSnapshot snapshot) => snapshot.State switch
    {
        AuditExportLoopState.Created => AuditExporterState.Created,
        AuditExportLoopState.Running => AuditExporterState.Running,
        AuditExportLoopState.WaitingForWork
            when snapshot.LastStep?.Kind == AuditExportCoordinatorStepKind.Blocked =>
                AuditExporterState.Stalled,
        AuditExportLoopState.WaitingForWork => AuditExporterState.Idle,
        AuditExportLoopState.WaitingToRetry => AuditExporterState.Retrying,
        AuditExportLoopState.Completed => AuditExporterState.Completed,
        AuditExportLoopState.Stopped or AuditExportLoopState.Disposed =>
            AuditExporterState.Stopped,
        AuditExportLoopState.Faulted => AuditExporterState.Faulted,
        _ => AuditExporterState.Faulted,
    };

    private static DateTimeOffset? AddDelay(
        DateTimeOffset observedUtc,
        TimeSpan? delay)
    {
        if (delay is null)
            return null;
        try
        {
            return observedUtc.Add(delay.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string SafeDetailCode(string? detailCode)
    {
        if (detailCode is null)
            return "unknown";
        return IsSafeDetailCode(detailCode) ||
               detailCode.StartsWith("retry.", StringComparison.Ordinal) &&
               IsSafeDetailCode(detailCode["retry.".Length..])
            ? detailCode
            : "unknown";
    }

    private static bool IsSafeDetailCode(string detailCode)
    {
        if (ExportDetailCodes.Contains(detailCode))
            return true;
        return detailCode.Length == 8 &&
               detailCode.StartsWith("http.", StringComparison.Ordinal) &&
               detailCode[5] is >= '1' and <= '5' &&
               detailCode[6] is >= '0' and <= '9' &&
               detailCode[7] is >= '0' and <= '9';
    }

    private static string? FailureClass(AuditExportFailureClass? failureClass) =>
        failureClass switch
        {
            AuditExportFailureClass.Configuration => "configuration",
            AuditExportFailureClass.PartialRejection => "partial_rejection",
            AuditExportFailureClass.Data => "data",
            AuditExportFailureClass.Protocol => "protocol",
            null => null,
            _ => "unknown",
        };

    private sealed class ExportHealthObserver(AuditHealth owner) : IAuditExportHealthObserver
    {
        public void Observe(AuditExportHealthObservation observation) =>
            owner.ObserveExport(observation);
    }

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
