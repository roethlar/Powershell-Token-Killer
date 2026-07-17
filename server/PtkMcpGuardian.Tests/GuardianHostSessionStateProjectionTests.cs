using PtkMcpGuardian.Standalone;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class GuardianHostSessionStateProjectionTests
{
    private static readonly HostBootId HostBoot = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostGeneration HostGeneration = new(1);
    private static readonly CanonicalAlias Alias = new("default");
    private static readonly WorkerBootId WorkerBoot = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly WorkerGeneration WorkerGeneration = new(1);
    private static readonly SessionTransitionVersion Transition = new(4);

    public static TheoryData<
        PublicHostStateSnapshot,
        PublicSessionState,
        RecoveryPhase?,
        BootstrapState>
        UnavailableHosts => new()
        {
            {
                Host(PublicHostState.Starting, phase: null, attempt: 0, retry: null),
                PublicSessionState.Starting,
                null,
                BootstrapState.Pending
            },
            {
                Host(PublicHostState.Recovering, RecoveryPhase.Containment, 1, 250),
                PublicSessionState.Recovering,
                RecoveryPhase.Containment,
                BootstrapState.Pending
            },
            {
                Host(PublicHostState.Recovering, RecoveryPhase.Bootstrap, 1, 250),
                PublicSessionState.Recovering,
                RecoveryPhase.Attempting,
                BootstrapState.Pending
            },
            {
                Host(PublicHostState.Backoff, RecoveryPhase.Backoff, 2, 500),
                PublicSessionState.Backoff,
                RecoveryPhase.Backoff,
                BootstrapState.Pending
            },
            {
                Host(PublicHostState.CircuitOpen, RecoveryPhase.CircuitOpen, 6, 60_000),
                PublicSessionState.CircuitOpen,
                RecoveryPhase.CircuitOpen,
                BootstrapState.Pending
            },
            {
                Host(PublicHostState.HalfOpen, RecoveryPhase.HalfOpen, 7, 250),
                PublicSessionState.HalfOpen,
                RecoveryPhase.HalfOpen,
                BootstrapState.Pending
            },
            {
                Host(
                    PublicHostState.ContainmentUnconfirmed,
                    phase: null,
                    attempt: 2,
                    retry: null,
                PublicRecoveryDetailCode.HostContainmentUnconfirmed),
                PublicSessionState.RecoveryUnknown,
                null,
                BootstrapState.Unknown
            },
            {
                Host(
                    PublicHostState.Stopped,
                    phase: null,
                    attempt: 2,
                    retry: null,
                PublicRecoveryDetailCode.HostContractMismatch),
                PublicSessionState.Lost,
                null,
                BootstrapState.Unknown
            },
        };

    [Theory]
    [MemberData(nameof(UnavailableHosts))]
    public void Unavailable_host_removes_impossible_ready_session_claim(
        PublicHostStateSnapshot host,
        PublicSessionState expectedState,
        RecoveryPhase? expectedPhase,
        BootstrapState expectedBootstrap)
    {
        var source = ReadySession();

        var projected = Assert.Single(
            GuardianHostSessionStateProjection.Project(host, [source]));

        Assert.Equal(expectedState, projected.State);
        Assert.False(projected.ReadyForEffects);
        Assert.Null(projected.WorkerBootId);
        Assert.Null(projected.Generation);
        Assert.Equal(expectedPhase, projected.RecoveryPhase);
        Assert.Equal(host.RecoveryAttempt, projected.RecoveryAttempt);
        Assert.Equal(host.RetryAfterMilliseconds, projected.RetryAfterMilliseconds);
        Assert.Equal(host.LastFailureCode, projected.LastFailureCode);
        Assert.True(projected.WarmStateLost);
        Assert.Equal(expectedBootstrap, projected.BootstrapState);
    }

    [Fact]
    public void Ready_host_preserves_session_snapshot()
    {
        var source = ReadySession();
        var sessions = new[] { source };

        Assert.Same(
            sessions,
            GuardianHostSessionStateProjection.Project(
                Host(PublicHostState.Ready, phase: null, attempt: 0, retry: null),
                sessions));
    }

    [Fact]
    public void Nonready_session_truth_is_not_overwritten_by_host_recovery()
    {
        var source = new PublicSessionStateSnapshot(
            Alias,
            DesiredSessionState.Ready,
            PublicSessionState.Faulted,
            workerBootId: null,
            generation: null,
            Transition,
            recoveryPhase: null,
            recoveryAttempt: 0,
            retryAfterMilliseconds: null,
            readyForEffects: false,
            PublicRecoveryDetailCode.SessionBootstrapFailed,
            warmStateLost: false,
            BootstrapState.Failed);
        var sessions = new[] { source };

        Assert.Same(
            sessions,
            GuardianHostSessionStateProjection.Project(
                Host(PublicHostState.Backoff, RecoveryPhase.Backoff, 2, 500),
                sessions));
    }

    private static PublicSessionStateSnapshot ReadySession() => new(
        Alias,
        DesiredSessionState.Ready,
        PublicSessionState.Ready,
        WorkerBoot,
        WorkerGeneration,
        Transition,
        recoveryPhase: null,
        recoveryAttempt: 0,
        retryAfterMilliseconds: null,
        readyForEffects: true,
        lastFailureCode: null,
        warmStateLost: false,
        BootstrapState.Restored);

    private static PublicHostStateSnapshot Host(
        PublicHostState state,
        RecoveryPhase? phase,
        long attempt,
        int? retry,
        PublicRecoveryDetailCode? failure = null) => new(
            state is PublicHostState.Starting or PublicHostState.Ready or
                PublicHostState.Recovering or PublicHostState.ContainmentUnconfirmed or
                PublicHostState.HalfOpen
                ? HostBoot
                : null,
            state is PublicHostState.Starting or PublicHostState.Ready or
                PublicHostState.Recovering or PublicHostState.ContainmentUnconfirmed or
                PublicHostState.HalfOpen
                ? HostGeneration
                : null,
            state,
            phase,
            attempt,
            retry,
            readyForEffects: state == PublicHostState.Ready,
            failure);
}
