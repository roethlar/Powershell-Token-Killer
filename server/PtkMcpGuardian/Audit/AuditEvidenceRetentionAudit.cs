using System.Runtime.ExceptionServices;

namespace PtkMcpServer.Audit;

internal enum AuditEvidenceRetentionReason
{
    AgeExpired,
    CapacityPressure,
    CrashTemporary,
}

internal enum AuditEvidenceRetentionFaultPoint
{
    BeforeDelete,
    AfterDelete,
}

internal readonly record struct AuditEvidenceRetentionSubject(
    Guid EvidenceId,
    string Digest,
    long Bytes,
    string State,
    AuditEvidenceRetentionReason Reason);

/// <summary>
/// Bounded normalized attribution copied from the triggering call. It carries
/// no submitted script, response, credential, or other variable payload.
/// </summary>
internal sealed record AuditEvidenceRetentionContext(
    Guid CallId,
    AuditSession Session,
    AuditActor Actor);

/// <summary>
/// Persists one bounded evidence-retention attempt. The two-slot reservation
/// exists before the durable intent, and the exact unlink cannot run before
/// that intent has flushed. A hard death may leave intent-only evidence but
/// can never leave an unaudited automatic deletion.
/// </summary>
internal static class AuditEvidenceRetentionAudit
{
    internal static bool TryDelete(
        AuditJournal journal,
        AuditEvidenceRetentionSubject subject,
        Action deleteExactArtifact,
        Func<bool> exactArtifactStillNamed,
        AuditEvidenceRetentionContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(deleteExactArtifact);
        ArgumentNullException.ThrowIfNull(exactArtifactStillNamed);

        if (!journal.TryReserve(2, out var reservation, out _) || reservation is null)
            return false;

        using (reservation)
        {
            var intent = journal.Append(
                reservation,
                CreateEvent(
                    journal,
                    subject,
                    "evidence.retention_intent",
                    "accepted",
                    "retention." + ReasonText(subject.Reason),
                    parentEventId: null,
                    context));

            try
            {
                deleteExactArtifact();
            }
            catch (Exception deletionFailure) when (!IsFatal(deletionFailure))
            {
                bool definitelyRetained;
                try
                {
                    definitelyRetained = exactArtifactStillNamed();
                }
                catch (Exception proofFailure) when (!IsFatal(proofFailure))
                {
                    definitelyRetained = false;
                }

                _ = journal.Append(
                    reservation,
                    CreateEvent(
                        journal,
                        subject,
                        "evidence.retention_failed",
                        definitelyRetained ? "failed" : "outcome_unknown",
                        definitelyRetained
                            ? "retention.delete_failed"
                            : "retention.delete_outcome_unknown",
                        intent.EventId,
                        context));
                ExceptionDispatchInfo.Capture(deletionFailure).Throw();
                throw;
            }

            _ = journal.Append(
                reservation,
                CreateEvent(
                    journal,
                    subject,
                    "evidence.retention_completed",
                    "completed",
                    "retention." + ReasonText(subject.Reason),
                    intent.EventId,
                    context));
        }
        return true;
    }

    private static AuditEventInput CreateEvent(
        AuditJournal journal,
        AuditEvidenceRetentionSubject subject,
        string eventType,
        string outcomeState,
        string detailCode,
        Guid? parentEventId,
        AuditEvidenceRetentionContext? context)
    {
        var health = journal.Health.Snapshot();
        var unhealthy = health.State is AuditHealthState.Degraded or AuditHealthState.Unavailable;
        return new AuditEventInput
        {
            EventType = eventType,
            Session = context?.Session ?? new AuditSession
            {
                DeclaredPurpose = "evidence_retention",
            },
            Actor = context?.Actor ?? new AuditActor
            {
                AttributionStrength = "unknown",
            },
            Correlation = new AuditCorrelation
            {
                CallId = context?.CallId,
                ParentEventId = parentEventId,
            },
            Request = new AuditRequest
            {
                Action = "retention",
                EvidenceSubjectId = subject.EvidenceId,
                EvidenceSubjectDigest = subject.Digest,
                EvidenceSubjectBytes = subject.Bytes,
                EvidenceSubjectState = subject.State,
                RetentionReason = ReasonText(subject.Reason),
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
                ProtectionMode = journal.Options.ProtectionMode == AuditProtectionMode.Anchored
                    ? "anchored"
                    : "local-only",
                ExportConfigurationIdentity = journal.Options.ExportConfigurationIdentity,
                HealthState = unhealthy ? "degraded" : "healthy",
                FailureClass = unhealthy ? health.FailureClass : null,
                DegradedSinceUtc = unhealthy ? health.DegradedSinceUtc : null,
            },
        };
    }

    internal static string ReasonText(AuditEvidenceRetentionReason reason) => reason switch
    {
        AuditEvidenceRetentionReason.AgeExpired => "age_expired",
        AuditEvidenceRetentionReason.CapacityPressure => "capacity_pressure",
        AuditEvidenceRetentionReason.CrashTemporary => "crash_temporary",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
