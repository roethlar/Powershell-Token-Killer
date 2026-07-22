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
        _ = _runtime.TryAppendAutomaticTransition(CreateStartingEvent());

    private AuditEventInput CreateStartingEvent()
    {
        var health = _runtime.Health.Snapshot();
        var unhealthy = health.State is
            AuditHealthState.Degraded or AuditHealthState.Unavailable;
        return new AuditEventInput
        {
            EventType = "host.starting",
            Session = new AuditSession(),
            Actor = new AuditActor { AttributionStrength = "unknown" },
            Correlation = new AuditCorrelation(),
            Request = new AuditRequest(),
            Routing = new AuditRouting(),
            Outcome = new AuditOutcome
            {
                State = "starting",
                DetailCode = "host_starting",
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
}
