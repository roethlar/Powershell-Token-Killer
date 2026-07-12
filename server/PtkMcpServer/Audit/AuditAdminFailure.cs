namespace PtkMcpServer.Audit;

internal enum AuditAdminFailureKind
{
    EvidenceIdInvalid,
    EvidencePathInvalid,
    EvidenceAbsent,
    EvidenceStorageFailed,
    EvidenceControlInvalid,
    EvidenceDestinationRefused,
    EvidenceDestinationExists,
    OperationFailedBeforeDisclosure,
    OperationDisclosureUnknown,
    OperationFlushFailedAfterDisclosure,
    AuditOutcomeFailedAfterDisclosure,
    AuditOutcomeFailedAfterPublish,
}

internal static class AuditAdminFailureDetailCode
{
    internal static string From(AuditAdminFailureKind kind) => kind switch
    {
        AuditAdminFailureKind.EvidenceIdInvalid => "evidence.id_invalid",
        AuditAdminFailureKind.EvidencePathInvalid => "evidence.path_invalid",
        AuditAdminFailureKind.EvidenceAbsent => "evidence.absent",
        AuditAdminFailureKind.EvidenceStorageFailed => "evidence.storage_failed",
        AuditAdminFailureKind.EvidenceControlInvalid => "evidence.control_invalid",
        AuditAdminFailureKind.EvidenceDestinationRefused => "evidence.destination_refused",
        AuditAdminFailureKind.EvidenceDestinationExists => "evidence.destination_exists",
        AuditAdminFailureKind.OperationFailedBeforeDisclosure =>
            "operation.failed_before_disclosure",
        AuditAdminFailureKind.OperationDisclosureUnknown =>
            "operation.disclosure_unknown",
        AuditAdminFailureKind.OperationFlushFailedAfterDisclosure =>
            "operation.flush_failed_after_disclosure",
        AuditAdminFailureKind.AuditOutcomeFailedAfterDisclosure =>
            "audit.outcome_failed_after_disclosure",
        AuditAdminFailureKind.AuditOutcomeFailedAfterPublish =>
            "audit.outcome_failed_after_publish",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

internal enum AuditEvidenceDestinationFailureKind
{
    InvalidPath,
    Exists,
    Refused,
    Storage,
}

internal sealed class AuditEvidenceDestinationException(
    AuditEvidenceDestinationFailureKind failureKind,
    Exception? innerException = null)
    : IOException("Protected evidence destination failed.", innerException)
{
    internal AuditEvidenceDestinationFailureKind FailureKind { get; } = failureKind;
}
