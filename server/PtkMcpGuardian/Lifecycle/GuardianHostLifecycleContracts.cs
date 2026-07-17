using PtkSharedContracts;

namespace PtkMcpGuardian.Lifecycle;

internal enum GuardianHostLossReason
{
    EndOfStream,
    Exit,
    ReaderFailure,
    WriterFailure,
    ProtocolFatal,
    ContractMismatch,
    ContainmentNotification,
    InitializationFailure,
    OperatorRecycle,
    TerminalShutdown,
}

internal enum GuardianHostAttemptStage
{
    Launching,
    Bootstrapping,
    Ready,
    Containing,
    ContainmentUnconfirmed,
    DeathConfirmed,
}

internal enum GuardianHostPermanentStopReason
{
    InitialStartFailed,
    ContractMismatch,
    IdentityExhausted,
    TerminalShutdown,
}

internal enum GuardianHostLaunchOutcome
{
    Started,
    ProvedNoChild,
}

internal enum GuardianHostStartDisposition
{
    Started,
    ContainmentStarted,
    ProvedNoChild,
    Refused,
    PermanentlyStopped,
}

internal enum GuardianHostLifecycleLossDisposition
{
    BeganContainment,
    Duplicate,
    StaleAttempt,
    Stopped,
}

internal enum GuardianHostContainmentDisposition
{
    Pending,
    MarkedUnconfirmed,
    Confirmed,
    Duplicate,
    StaleAttempt,
}

internal enum GuardianHostStartupDeadlineDisposition
{
    Pending,
    BeganContainment,
    Duplicate,
    StaleAttempt,
    Stopped,
}

internal enum GuardianHostWriteDisposition
{
    Began,
    NotReady,
    StaleAttempt,
    Stopped,
}

/// <summary>
/// An absolute deadline supplied by startup policy. It is deliberately a
/// distinct type from outer-host and worker containment deadlines.
/// </summary>
internal readonly record struct GuardianHostStartupDeadline(long AbsoluteTimestamp);

/// <summary>
/// The one monotonic deadline for an outer-host containment transition.
/// Both timestamps use the controller's injected TimeProvider domain.
/// </summary>
internal sealed record GuardianHostContainmentDeadline(
    long StartedTimestamp,
    long AbsoluteTimestamp);

internal sealed record GuardianHostIdentity
{
    internal GuardianHostIdentity(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration)
    {
        ArgumentNullException.ThrowIfNull(guardianBootId);
        ArgumentNullException.ThrowIfNull(hostBootId);
        ArgumentNullException.ThrowIfNull(hostGeneration);
        GuardianBootId = guardianBootId;
        HostBootId = hostBootId;
        HostGeneration = hostGeneration;
    }

    internal GuardianBootId GuardianBootId { get; }
    internal HostBootId HostBootId { get; }
    internal HostGeneration HostGeneration { get; }
}

internal interface IGuardianHostBootIdSource
{
    HostBootId Next();
}

internal interface IGuardianHostStartupDeadlineSource
{
    GuardianHostStartupDeadline Next();
}

/// <summary>
/// Produces the opaque authority for one generation. Throwing from Prepare
/// must prove that no child was created. Once creation may have happened, the
/// returned resource remains the containment authority even when Launch fails.
/// </summary>
internal interface IGuardianHostAttemptFactory
{
    IGuardianHostAttemptResources Prepare(
        GuardianHostIdentity identity,
        GuardianHostStartupDeadline startupDeadline);
}

/// <summary>
/// Injected launch, private-transport, and containment boundaries. Each method
/// is a short non-reentrant boundary callback; lifecycle waiting reports back
/// through the generation-scoped controller lease.
/// </summary>
internal interface IGuardianHostAttemptResources : IDisposable
{
    GuardianHostLaunchOutcome Launch();

    void CloseTransport();

    void BeginContainment(GuardianHostContainmentDeadline deadline);
}

internal sealed class GuardianHostAttemptLease
{
    internal GuardianHostAttemptLease(
        GuardianHostIdentity identity,
        GuardianHostStartupDeadline startupDeadline,
        IGuardianHostAttemptResources resources,
        RecoveryAttemptLease? recoveryLease,
        bool isInitialAttempt)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(resources);
        Identity = identity;
        StartupDeadline = startupDeadline;
        Resources = resources;
        RecoveryLease = recoveryLease;
        IsInitialAttempt = isInitialAttempt;
        Stage = GuardianHostAttemptStage.Launching;
    }

    internal GuardianHostIdentity Identity { get; }
    internal GuardianHostStartupDeadline StartupDeadline { get; }
    internal RecoveryAttemptLease? RecoveryLease { get; }
    internal bool IsInitialAttempt { get; }
    internal GuardianHostAttemptStage Stage { get; set; }
    internal GuardianHostContainmentDeadline? ContainmentDeadline { get; set; }
    internal IGuardianHostAttemptResources Resources { get; }
    internal bool EverReady { get; set; }
}

internal readonly record struct GuardianHostStartTransition(
    GuardianHostStartDisposition Disposition,
    GuardianHostAttemptLease? Attempt);

internal readonly record struct GuardianHostContainmentTransition(
    GuardianHostContainmentDisposition Disposition,
    GuardianHostAttemptLease? StartedAttempt);

internal sealed record GuardianHostLifecycleSnapshot(
    PublicHostStateSnapshot Host,
    bool TerminalShutdown,
    GuardianHostPermanentStopReason? PermanentStopReason,
    GuardianHostLossReason? LastLossReason);
