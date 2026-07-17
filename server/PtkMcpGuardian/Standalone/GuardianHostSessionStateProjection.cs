using PtkSharedContracts;

namespace PtkMcpGuardian.Standalone;

/// <summary>
/// Removes an impossible ready claim from a last-known session snapshot while
/// its owning host generation is unavailable. Non-ready session truth remains
/// authoritative, including cold, faulted, and recovery-unknown aliases.
/// </summary>
internal static class GuardianHostSessionStateProjection
{
    internal static IReadOnlyList<PublicSessionStateSnapshot> Project(
        PublicHostStateSnapshot host,
        IReadOnlyList<PublicSessionStateSnapshot> sessions)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(sessions);

        if (host.ReadyForEffects || sessions.All(static session => !session.ReadyForEffects))
            return sessions;

        return sessions.Select(session =>
                session.ReadyForEffects ? ProjectReadySession(host, session) : session)
            .ToArray();
    }

    private static PublicSessionStateSnapshot ProjectReadySession(
        PublicHostStateSnapshot host,
        PublicSessionStateSnapshot session)
    {
        var state = host.State switch
        {
            PublicHostState.Starting when host.RecoveryPhase is null =>
                PublicSessionState.Starting,
            PublicHostState.Starting => PublicSessionState.Recovering,
            PublicHostState.Recovering => PublicSessionState.Recovering,
            PublicHostState.Backoff => PublicSessionState.Backoff,
            PublicHostState.CircuitOpen => PublicSessionState.CircuitOpen,
            PublicHostState.HalfOpen => PublicSessionState.HalfOpen,
            PublicHostState.ContainmentUnconfirmed => PublicSessionState.RecoveryUnknown,
            PublicHostState.Absent or PublicHostState.Stopped => PublicSessionState.Lost,
            _ => throw new InvalidOperationException(
                "A ready host cannot require session availability projection."),
        };
        var automatic = state is PublicSessionState.Recovering or
            PublicSessionState.Backoff or PublicSessionState.CircuitOpen or
            PublicSessionState.HalfOpen;
        var sessionPhase = host.RecoveryPhase == RecoveryPhase.Bootstrap
            ? RecoveryPhase.Attempting
            : host.RecoveryPhase;

        return new PublicSessionStateSnapshot(
            session.Alias,
            session.DesiredState,
            state,
            workerBootId: null,
            generation: null,
            session.TransitionVersion,
            automatic ? sessionPhase : null,
            host.RecoveryAttempt,
            automatic ? host.RetryAfterMilliseconds : null,
            readyForEffects: false,
            host.LastFailureCode,
            warmStateLost: true,
            automatic || state == PublicSessionState.Starting
                ? BootstrapState.Pending
                : BootstrapState.Unknown);
    }
}
