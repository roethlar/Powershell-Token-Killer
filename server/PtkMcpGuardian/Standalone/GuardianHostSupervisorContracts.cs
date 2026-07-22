using System.Diagnostics.CodeAnalysis;
using PtkMcpGuardian.Lifecycle;
using PtkMcpServer.Audit;
using PtkSharedContracts;

namespace PtkMcpGuardian.Standalone;

/// <summary>
/// A launched host generation whose already-contained private transport is
/// connected to the guardian. Process creation and containment remain owned by
/// the lifecycle resource; the supervisor never discovers or opens a transport.
/// </summary>
internal interface IGuardianHostConnectedAttemptResources :
    IGuardianHostAttemptResources
{
    Stream RequestStream { get; }

    Stream EventStream { get; }

    int HostProcessId { get; }

    Task HostExited { get; }

    Task ContainmentConfirmed { get; }
}

/// <summary>
/// Rebuilds only the generation-scoped envelope around guardian-frozen
/// recovery data. Implementations must not reread mutable configuration.
/// </summary>
internal interface IGuardianHostRecoveryManifestSource
{
    RecoveryManifest Create(GuardianHostIdentity identity);
}

/// <summary>
/// The one injected timer boundary. Snapshot reads never call this interface;
/// only explicit startup, containment, stability, and retry loops do.
/// </summary>
internal interface IGuardianHostSupervisorScheduler
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// Durable audit edge for supervisor-owned host lifecycle transitions. Methods
/// are invoked only after the lifecycle controller has published the matching
/// immutable host snapshot.
/// </summary>
internal interface IGuardianHostLifecycleAudit
{
    void RecordStarting();

    void RecordReady(bool recovered);

    void RecordLost(GuardianHostLossReason reason, bool warmStateLost);

    void RecordContainmentUnconfirmed(bool warmStateLost);

    void RecordRecoveryFailed(bool processStarted);

    void RecordRecoveryScheduled();

    void RecordCircuitOpen();

    void RecordCircuitHalfOpen();

    void RecordStopped(bool? warmStateLost, bool containedAttempt);
}

internal sealed record GuardianHostJobListTarget
{
    internal GuardianHostJobListTarget(
        CanonicalAlias alias,
        SessionTransitionVersion transitionVersion,
        GuardianHostWorkerIdentity workerIdentity,
        GuardianAuditSession auditSession,
        bool readyForEffects)
    {
        Alias = alias ?? throw new ArgumentNullException(nameof(alias));
        TransitionVersion = transitionVersion ??
            throw new ArgumentNullException(nameof(transitionVersion));
        WorkerIdentity = workerIdentity ??
            throw new ArgumentNullException(nameof(workerIdentity));
        AuditSession = auditSession ??
            throw new ArgumentNullException(nameof(auditSession));
        if (!StringComparer.Ordinal.Equals(auditSession.Session.Name, alias.Value) ||
            auditSession.Session.Generation != workerIdentity.Generation.Value)
        {
            throw new ArgumentException(
                "The audit session must match the exact dispatch alias and worker generation.",
                nameof(auditSession));
        }
        ReadyForEffects = readyForEffects;
    }

    internal CanonicalAlias Alias { get; }

    internal SessionTransitionVersion TransitionVersion { get; }

    internal GuardianHostWorkerIdentity WorkerIdentity { get; }

    internal GuardianAuditSession AuditSession { get; }

    internal bool ReadyForEffects { get; }

    internal bool SameDispatchIdentity(GuardianHostJobListTarget other) =>
        Alias == other.Alias &&
        TransitionVersion == other.TransitionVersion &&
        WorkerIdentity.BootId == other.WorkerIdentity.BootId &&
        WorkerIdentity.Generation == other.WorkerIdentity.Generation &&
        AuditSession.Session == other.AuditSession.Session &&
        ReadyForEffects == other.ReadyForEffects;
}

/// <summary>
/// Frozen automatic-recovery facts captured by the session owner in the same
/// transition that invalidated an exact dispatch target. A later ready target
/// cannot reconstruct these facts from its current snapshot.
/// </summary>
internal sealed record GuardianHostJobListTargetInvalidation
{
    internal GuardianHostJobListTargetInvalidation(
        GuardianHostJobListTarget invalidatedTarget,
        PublicSessionStateSnapshot recoverySnapshot)
    {
        InvalidatedTarget = invalidatedTarget ??
            throw new ArgumentNullException(nameof(invalidatedTarget));
        RecoverySnapshot = recoverySnapshot ??
            throw new ArgumentNullException(nameof(recoverySnapshot));
        if (!invalidatedTarget.ReadyForEffects)
            throw new ArgumentException(
                "Only a ready dispatch target can be invalidated.",
                nameof(invalidatedTarget));
        if (recoverySnapshot.Alias != invalidatedTarget.Alias ||
            recoverySnapshot.ReadyForEffects ||
            recoverySnapshot.RecoveryPhase is null ||
            recoverySnapshot.RecoveryAttempt <= 0 ||
            recoverySnapshot.RetryAfterMilliseconds is null)
        {
            throw new ArgumentException(
                "Session invalidation evidence must be one exact automatic-recovery snapshot.",
                nameof(recoverySnapshot));
        }
    }

    internal GuardianHostJobListTarget InvalidatedTarget { get; }

    internal PublicSessionStateSnapshot RecoverySnapshot { get; }

    internal bool AppliesTo(GuardianHostJobListTarget target) =>
        InvalidatedTarget.SameDispatchIdentity(target);
}

/// <summary>
/// Guardian-local last-known session state. Both members are synchronous so a
/// state poll can neither wait on nor enter the replaceable host.
/// </summary>
internal interface IGuardianHostSupervisorSessionSource
{
    IReadOnlyList<PublicSessionStateSnapshot> SnapshotSessions();

    /// <summary>
    /// Commits the session-state consequence of one host generation becoming
    /// ready. The callback runs under supervisor authority and must remain
    /// bounded and non-reentrant.
    /// </summary>
    void ObserveHostReady(GuardianHostIdentity identity, bool recovered);

    bool TryGetJobListTarget(
        CanonicalAlias alias,
        [NotNullWhen(true)] out GuardianHostJobListTarget? target);

    /// <summary>
    /// Returns evidence only when it was atomically captured while the exact
    /// target was invalidated. Implementations must return false instead of
    /// synthesizing recovery metadata from a later target.
    /// </summary>
    bool TryGetJobListTargetInvalidation(
        GuardianHostJobListTarget target,
        [NotNullWhen(true)] out GuardianHostJobListTargetInvalidation? invalidation);
}

internal sealed record GuardianHostDispatchObservation(
    GuardianHostIdentity HostIdentity,
    GuardianHostJobListTarget Target,
    PrivateRequestId? PrivateRequestId);

/// <summary>
/// Deterministic test/telemetry seam around dispatch. The asynchronous edge is
/// before authority acquisition; the synchronous edge runs under the same
/// authority lease as the first possibly-writing private API.
/// </summary>
internal interface IGuardianHostSupervisorDispatchObserver
{
    ValueTask BeforeWriteAuthorizationAsync(
        GuardianHostDispatchObservation observation,
        CancellationToken cancellationToken);

    void OnWriteStarting(GuardianHostDispatchObservation observation);

    void OnTerminalDecoded(GuardianHostDispatchObservation observation);
}

internal sealed record GuardianHostSupervisorPins
{
    internal GuardianHostSupervisorPins(
        Sha256Digest hostExecutableDigest,
        Sha256Digest hostBuildDigest,
        Sha256Digest publicContractDigest,
        Sha256Digest configurationDigest,
        Sha256Digest catalogDigest,
        Sha256Digest packageManifestDigest)
    {
        HostExecutableDigest = hostExecutableDigest ??
            throw new ArgumentNullException(nameof(hostExecutableDigest));
        HostBuildDigest = hostBuildDigest ??
            throw new ArgumentNullException(nameof(hostBuildDigest));
        PublicContractDigest = publicContractDigest ??
            throw new ArgumentNullException(nameof(publicContractDigest));
        ConfigurationDigest = configurationDigest ??
            throw new ArgumentNullException(nameof(configurationDigest));
        CatalogDigest = catalogDigest ??
            throw new ArgumentNullException(nameof(catalogDigest));
        PackageManifestDigest = packageManifestDigest ??
            throw new ArgumentNullException(nameof(packageManifestDigest));
    }

    internal Sha256Digest HostExecutableDigest { get; }

    internal Sha256Digest HostBuildDigest { get; }

    internal Sha256Digest PublicContractDigest { get; }

    internal Sha256Digest ConfigurationDigest { get; }

    internal Sha256Digest CatalogDigest { get; }

    internal Sha256Digest PackageManifestDigest { get; }
}

internal sealed record GuardianHostSupervisorTerminal
{
    internal GuardianHostSupervisorTerminal(
        string text,
        bool isError,
        string? auditDetailCode = null)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        IsError = isError;
        AuditDetailCode = auditDetailCode;
    }

    internal string Text { get; }

    internal bool IsError { get; }

    internal string? AuditDetailCode { get; }
}
