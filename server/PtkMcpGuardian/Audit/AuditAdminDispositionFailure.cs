namespace PtkMcpServer.Audit;

internal enum AuditAdminDispositionFailureKind
{
    TargetIdInvalid,
    ModeIneligible,
    TargetLive,
    TargetControlInvalid,
    BlockAbsent,
    BlockIneligible,
    ProofConflict,
    IntentControlInvalid,
    OutcomeControlInvalid,
    CheckpointAdvancedReceiptMissing,
    AuditOutcomeFailedAfterCheckpoint,
}

internal static class AuditAdminDispositionFailureDetailCode
{
    internal static string From(AuditAdminDispositionFailureKind kind) => kind switch
    {
        AuditAdminDispositionFailureKind.TargetIdInvalid =>
            "disposition.target_id_invalid",
        AuditAdminDispositionFailureKind.ModeIneligible =>
            "disposition.mode_ineligible",
        AuditAdminDispositionFailureKind.TargetLive =>
            "disposition.target_live",
        AuditAdminDispositionFailureKind.TargetControlInvalid =>
            "disposition.target_control_invalid",
        AuditAdminDispositionFailureKind.BlockAbsent =>
            "disposition.block_absent",
        AuditAdminDispositionFailureKind.BlockIneligible =>
            "disposition.block_ineligible",
        AuditAdminDispositionFailureKind.ProofConflict =>
            "disposition.proof_conflict",
        AuditAdminDispositionFailureKind.IntentControlInvalid =>
            "disposition.intent_control_invalid",
        AuditAdminDispositionFailureKind.OutcomeControlInvalid =>
            "disposition.outcome_control_invalid",
        AuditAdminDispositionFailureKind.CheckpointAdvancedReceiptMissing =>
            "disposition.checkpoint_advanced_receipt_missing",
        AuditAdminDispositionFailureKind.AuditOutcomeFailedAfterCheckpoint =>
            "audit.outcome_failed_after_checkpoint",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

internal sealed class AuditDispositionTargetLiveException()
    : IOException("The target audit export checkpoint is live.");

internal sealed class AuditDispositionModeIneligibleException()
    : InvalidOperationException("Export-block disposition requires anchored audit mode.");

internal sealed class AuditDispositionBlockAbsentException()
    : IOException("The target audit export block is absent or ambiguous.");

internal sealed class AuditDispositionBlockIneligibleException()
    : InvalidOperationException(
        "Only a permanent audit export block accepts operator disposition.");

internal enum AuditOperatorDispositionIntentFailureKind
{
    Conflict,
    Invalid,
}

internal sealed class AuditOperatorDispositionIntentException(
    AuditOperatorDispositionIntentFailureKind failureKind,
    string message,
    Exception? innerException = null)
    : IOException(message, innerException)
{
    internal AuditOperatorDispositionIntentFailureKind FailureKind { get; } = failureKind;
}

internal enum AuditOperatorDispositionOutcomeFailureKind
{
    Invalid,
    Incomplete,
}

internal sealed class AuditOperatorDispositionOutcomeException(
    AuditOperatorDispositionOutcomeFailureKind failureKind,
    string message,
    Exception? innerException = null)
    : IOException(message, innerException)
{
    internal AuditOperatorDispositionOutcomeFailureKind FailureKind { get; } = failureKind;
}
