using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Standalone;

namespace PtkMcpServer.Audit;

/// <summary>
/// Translates supervisor-owned host lifecycle facts into bounded automatic
/// audit records. The journal captures the immutable v3 host snapshot at the
/// append edge; an unavailable journal closes admission without undoing a host
/// safety transition that already happened.
/// </summary>
internal sealed class GuardianHostLifecycleAudit(AuditRuntimeGate runtime) :
    IGuardianHostLifecycleAudit
{
    private readonly AuditRuntimeGate _runtime = runtime ??
        throw new ArgumentNullException(nameof(runtime));

    public void RecordStarting() =>
        Record(
            "host.starting",
            outcomeState: "starting",
            detailCode: "host_starting",
            warmStateLost: null);

    public void RecordReady(bool recovered) =>
        Record(
            recovered ? "host.recovered" : "host.ready",
            outcomeState: "completed",
            detailCode: recovered ? "host_recovered" : "host_ready",
            warmStateLost: recovered);

    public void RecordLost(GuardianHostLossReason reason, bool warmStateLost)
    {
        if (!Enum.IsDefined(reason) ||
            reason == GuardianHostLossReason.TerminalShutdown)
            throw new ArgumentOutOfRangeException(nameof(reason));

        Record(
            "host.lost",
            outcomeState: "lost",
            detailCode: DetailCodeFor(reason),
            warmStateLost,
            terminationCertainty: "unconfirmed",
            rootProcessObserved: reason == GuardianHostLossReason.Exit
                ? "complete"
                : "unknown",
            descendantsObserved: "unknown");
    }

    private void Record(
        string eventType,
        string outcomeState,
        string detailCode,
        bool? warmStateLost,
        string terminationCertainty = "not_applicable",
        string rootProcessObserved = "not_applicable",
        string descendantsObserved = "not_applicable")
    {
        _ = _runtime.TryAppendAutomaticTransition(CreateEvent(
            eventType,
            outcomeState,
            detailCode,
            warmStateLost,
            terminationCertainty,
            rootProcessObserved,
            descendantsObserved));
    }

    private AuditEventInput CreateEvent(
        string eventType,
        string outcomeState,
        string detailCode,
        bool? warmStateLost,
        string terminationCertainty,
        string rootProcessObserved,
        string descendantsObserved)
    {
        var health = _runtime.Health.Snapshot();
        var unhealthy = health.State is
            AuditHealthState.Degraded or AuditHealthState.Unavailable;
        return new AuditEventInput
        {
            EventType = eventType,
            Session = new AuditSession(),
            Actor = new AuditActor { AttributionStrength = "unknown" },
            Correlation = new AuditCorrelation(),
            Request = new AuditRequest(),
            Routing = new AuditRouting(),
            Outcome = new AuditOutcome
            {
                State = outcomeState,
                DetailCode = detailCode,
                WarmStateLost = warmStateLost,
                TerminationCertainty = terminationCertainty,
            },
            Coverage = new AuditCoverage
            {
                PtkRequest = false,
                RootProcessObserved = rootProcessObserved,
                DescendantsObserved = descendantsObserved,
                RemoteEffectObserved = "not_applicable",
            },
            Audit = new AuditEventHealth
            {
                ProtectionMode = health.ProtectionMode == AuditProtectionMode.LocalOnly
                    ? "local-only"
                    : "anchored",
                ExportConfigurationIdentity = health.ExportConfigurationIdentity,
                HealthState = unhealthy ? "degraded" : "healthy",
                FailureClass = unhealthy ? health.FailureClass : null,
                DegradedSinceUtc = unhealthy ? health.DegradedSinceUtc : null,
            },
        };
    }

    private static string DetailCodeFor(GuardianHostLossReason reason) => reason switch
    {
        GuardianHostLossReason.EndOfStream => "host_end_of_stream",
        GuardianHostLossReason.Exit => "host_exited",
        GuardianHostLossReason.ReaderFailure => "host_reader_failure",
        GuardianHostLossReason.WriterFailure => "host_writer_failure",
        GuardianHostLossReason.ProtocolFatal => "host_protocol_fatal",
        GuardianHostLossReason.ContractMismatch => "host_contract_mismatch",
        GuardianHostLossReason.ContainmentNotification =>
            "host_containment_notification",
        GuardianHostLossReason.InitializationFailure =>
            "host_initialization_failed",
        GuardianHostLossReason.OperatorRecycle => "host_operator_recycle",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
