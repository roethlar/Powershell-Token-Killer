using System.Diagnostics.CodeAnalysis;
using PtkMcpGuardian.Lifecycle;
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

internal sealed record GuardianHostJobListTarget
{
    internal GuardianHostJobListTarget(
        CanonicalAlias alias,
        SessionTransitionVersion transitionVersion,
        GuardianHostWorkerIdentity workerIdentity,
        bool readyForEffects)
    {
        Alias = alias ?? throw new ArgumentNullException(nameof(alias));
        TransitionVersion = transitionVersion ??
            throw new ArgumentNullException(nameof(transitionVersion));
        WorkerIdentity = workerIdentity ??
            throw new ArgumentNullException(nameof(workerIdentity));
        ReadyForEffects = readyForEffects;
    }

    internal CanonicalAlias Alias { get; }

    internal SessionTransitionVersion TransitionVersion { get; }

    internal GuardianHostWorkerIdentity WorkerIdentity { get; }

    internal bool ReadyForEffects { get; }

    internal bool SameDispatchIdentity(GuardianHostJobListTarget other) =>
        Alias == other.Alias &&
        TransitionVersion == other.TransitionVersion &&
        WorkerIdentity.BootId == other.WorkerIdentity.BootId &&
        WorkerIdentity.Generation == other.WorkerIdentity.Generation &&
        ReadyForEffects == other.ReadyForEffects;
}

/// <summary>
/// Guardian-local last-known session state. Both members are synchronous so a
/// state poll can neither wait on nor enter the replaceable host.
/// </summary>
internal interface IGuardianHostSupervisorSessionSource
{
    IReadOnlyList<PublicSessionStateSnapshot> SnapshotSessions();

    bool TryGetJobListTarget(
        [NotNullWhen(true)] out GuardianHostJobListTarget? target);
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
    internal GuardianHostSupervisorTerminal(string text, bool isError)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        IsError = isError;
    }

    internal string Text { get; }

    internal bool IsError { get; }
}
