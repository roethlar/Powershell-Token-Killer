namespace PtkSharedContracts;

public enum GuardianHostRequestMethod
{
    ManifestHeader,
    ManifestChunk,
    ManifestSeal,
    Operation,
    WorkerCreateCapabilityGrant,
    WorkerContainmentPendingAck,
    WorkerContainmentArmedAck,
    WorkerContainmentRemoveAck,
}

public enum GuardianHostOperationKind
{
    InvokeForeground,
    InvokeBackground,
    JobList,
    JobStatus,
    JobOutput,
    JobKill,
    Reset,
    SessionOpen,
    SessionClose,
    SessionRestart,
}

public enum GuardianHostEventType
{
    WorkerCreateCapabilityRequested,
    WorkerContainmentPending,
    WorkerContainmentArmed,
    WorkerContainmentRemoveRequested,
    OperationDelivery,
    SessionLifecycle,
    WorkerLost,
    WorkerDiagnosticChunk,
    WorkerDiagnosticTruncated,
    JobLifecycle,
    OutputChunk,
    OutputSeal,
}

public enum GuardianHostResponseType
{
    ManifestHeaderAccepted,
    ManifestChunkAccepted,
    ManifestSealed,
    OperationCompleted,
    ControlAcknowledged,
    ShutdownAccepted,
}

public enum GuardianHostPrivateMessageCode
{
    AmbiguousOutcome,
    Canceled,
    CapabilityFailure,
    ContainmentFailure,
    DeadlineExpired,
    InvalidRequest,
    ManifestFailure,
    PreparedOperationFailure,
    SessionUnavailable,
    StaleGeneration,
    UnsupportedRequest,
    WorkerFailure,
}

public enum GuardianHostPrivateDetailCode
{
    AuditCapabilityInvalid,
    BootstrapFailed,
    CapabilityInvalid,
    ContainmentUnconfirmed,
    ExpectedGenerationMismatch,
    InvalidOperationField,
    InvalidOperationResponse,
    InvalidPreparedField,
    JobCapabilityInvalid,
    ManifestBoundsExceeded,
    ManifestDigestMismatch,
    ManifestInvalid,
    MissingOperationField,
    MissingPreparedField,
    OperationArgumentMismatch,
    OperationKindMismatch,
    OperationNotDispatched,
    OperationResultMismatch,
    OperationResultTooLarge,
    OperationScriptTooLarge,
    OutcomeUnknown,
    OutputCapabilityInvalid,
    PreparedScriptDigestMismatch,
    ReplanRequired,
    RequestCanceled,
    RequestDeadlineExpired,
    SessionBusy,
    SessionFaulted,
    SessionNotFound,
    SessionQuarantined,
    UnknownOperationField,
    UnknownPreparedField,
    UnsupportedOperation,
    UnsupportedPreparedOperation,
    WorkerBootMismatch,
    WorkerGenerationMismatch,
    WorkerLost,
}

public readonly record struct GuardianHostPrivateError
{
    public GuardianHostPrivateError(GuardianHostPrivateDetailCode detailCode)
    {
        DetailCode = detailCode;
        MessageCode = MessageFor(detailCode);
    }

    public GuardianHostPrivateDetailCode DetailCode { get; }
    public GuardianHostPrivateMessageCode MessageCode { get; }

    public static GuardianHostPrivateMessageCode MessageFor(GuardianHostPrivateDetailCode detailCode) =>
        detailCode switch
        {
            GuardianHostPrivateDetailCode.OutcomeUnknown => GuardianHostPrivateMessageCode.AmbiguousOutcome,
            GuardianHostPrivateDetailCode.RequestCanceled => GuardianHostPrivateMessageCode.Canceled,
            GuardianHostPrivateDetailCode.AuditCapabilityInvalid or
            GuardianHostPrivateDetailCode.CapabilityInvalid or
            GuardianHostPrivateDetailCode.JobCapabilityInvalid or
            GuardianHostPrivateDetailCode.OutputCapabilityInvalid => GuardianHostPrivateMessageCode.CapabilityFailure,
            GuardianHostPrivateDetailCode.ContainmentUnconfirmed => GuardianHostPrivateMessageCode.ContainmentFailure,
            GuardianHostPrivateDetailCode.RequestDeadlineExpired => GuardianHostPrivateMessageCode.DeadlineExpired,
            GuardianHostPrivateDetailCode.InvalidOperationField or
            GuardianHostPrivateDetailCode.MissingOperationField or
            GuardianHostPrivateDetailCode.OperationArgumentMismatch or
            GuardianHostPrivateDetailCode.OperationKindMismatch or
            GuardianHostPrivateDetailCode.OperationResultMismatch or
            GuardianHostPrivateDetailCode.OperationResultTooLarge or
            GuardianHostPrivateDetailCode.OperationScriptTooLarge or
            GuardianHostPrivateDetailCode.UnknownOperationField => GuardianHostPrivateMessageCode.InvalidRequest,
            GuardianHostPrivateDetailCode.ManifestBoundsExceeded or
            GuardianHostPrivateDetailCode.ManifestDigestMismatch or
            GuardianHostPrivateDetailCode.ManifestInvalid => GuardianHostPrivateMessageCode.ManifestFailure,
            GuardianHostPrivateDetailCode.InvalidPreparedField or
            GuardianHostPrivateDetailCode.MissingPreparedField or
            GuardianHostPrivateDetailCode.PreparedScriptDigestMismatch or
            GuardianHostPrivateDetailCode.ReplanRequired or
            GuardianHostPrivateDetailCode.UnknownPreparedField => GuardianHostPrivateMessageCode.PreparedOperationFailure,
            GuardianHostPrivateDetailCode.BootstrapFailed or
            GuardianHostPrivateDetailCode.SessionBusy or
            GuardianHostPrivateDetailCode.SessionFaulted or
            GuardianHostPrivateDetailCode.SessionNotFound or
            GuardianHostPrivateDetailCode.SessionQuarantined => GuardianHostPrivateMessageCode.SessionUnavailable,
            GuardianHostPrivateDetailCode.ExpectedGenerationMismatch or
            GuardianHostPrivateDetailCode.WorkerBootMismatch or
            GuardianHostPrivateDetailCode.WorkerGenerationMismatch => GuardianHostPrivateMessageCode.StaleGeneration,
            GuardianHostPrivateDetailCode.UnsupportedOperation or
            GuardianHostPrivateDetailCode.UnsupportedPreparedOperation => GuardianHostPrivateMessageCode.UnsupportedRequest,
            GuardianHostPrivateDetailCode.InvalidOperationResponse or
            GuardianHostPrivateDetailCode.OperationNotDispatched or
            GuardianHostPrivateDetailCode.WorkerLost => GuardianHostPrivateMessageCode.WorkerFailure,
            _ => throw new ArgumentOutOfRangeException(nameof(detailCode)),
        };
}
