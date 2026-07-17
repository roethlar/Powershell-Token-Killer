using PtkMcpGuardian.Lifecycle;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class GuardianHostLifecycleControllerTests
{
    private static readonly TimeSpan[] ExactBackoffs =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
    ];

    [Fact]
    public void Initial_attempt_publishes_identity_before_launch_and_becomes_ready_once()
    {
        var rig = new TestRig();
        GuardianHostLifecycleSnapshot? duringLaunch = null;
        rig.Factory.OnLaunch = _ => duringLaunch = rig.Controller.Snapshot();

        var start = rig.Controller.StartInitial();

        var attempt = Assert.IsType<GuardianHostAttemptLease>(start.Attempt);
        Assert.Equal(GuardianHostStartDisposition.Started, start.Disposition);
        Assert.Equal(1, attempt.Identity.HostGeneration.Value);
        Assert.Equal(rig.Deadlines.Issued[0], attempt.StartupDeadline);
        Assert.NotNull(duringLaunch);
        AssertHost(
            duringLaunch!.Host,
            PublicHostState.Starting,
            RecoveryPhase: null,
            attempt: 0,
            retryAfter: null,
            ready: false,
            generation: 1);
        Assert.True(rig.Controller.MarkBootstrapping(attempt));
        Assert.Equal(PublicHostState.Starting, rig.Controller.Snapshot().Host.State);
        Assert.True(rig.Controller.MarkReady(attempt));
        AssertHost(
            rig.Controller.Snapshot().Host,
            PublicHostState.Ready,
            RecoveryPhase: null,
            attempt: 0,
            retryAfter: null,
            ready: true,
            generation: 1);
        Assert.Equal(
            GuardianHostStartDisposition.Refused,
            rig.Controller.StartInitial().Disposition);
        Assert.Single(rig.Factory.Resources);
    }

    [Fact]
    public void Racing_loss_sources_begin_one_containment_with_one_exact_deadline()
    {
        var rig = new TestRig();
        var attempt = StartReady(rig);
        var reasons = new[]
        {
            GuardianHostLossReason.EndOfStream,
            GuardianHostLossReason.Exit,
            GuardianHostLossReason.ReaderFailure,
            GuardianHostLossReason.WriterFailure,
            GuardianHostLossReason.ProtocolFatal,
            GuardianHostLossReason.ContainmentNotification,
        };
        var results = new GuardianHostLifecycleLossDisposition[512];

        Parallel.For(0, results.Length, index =>
            results[index] = rig.Controller.ReportLoss(
                attempt,
                reasons[index % reasons.Length]));

        Assert.Equal(
            1,
            results.Count(value =>
                value == GuardianHostLifecycleLossDisposition.BeganContainment));
        Assert.All(
            results.Where(value =>
                value != GuardianHostLifecycleLossDisposition.BeganContainment),
            value => Assert.Equal(
                GuardianHostLifecycleLossDisposition.Duplicate,
                value));
        var resource = Assert.Single(rig.Factory.Resources);
        Assert.Equal(1, resource.CloseCount);
        Assert.Equal(1, resource.BeginContainmentCount);
        var deadline = Assert.IsType<GuardianHostContainmentDeadline>(resource.Deadline);
        Assert.Equal(0, deadline.StartedTimestamp);
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, deadline.AbsoluteTimestamp);
        AssertHost(
            rig.Controller.Snapshot().Host,
            PublicHostState.Recovering,
            RecoveryPhase.Containment,
            attempt: 1,
            retryAfter: 250,
            ready: false,
            generation: 1);
    }

    [Fact]
    public void Containment_deadline_is_exact_and_late_confirmation_starts_one_replacement()
    {
        var rig = new TestRig();
        var old = StartReady(rig);
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(old, GuardianHostLossReason.Exit));

        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.Duplicate,
            rig.Controller.ReportLoss(old, GuardianHostLossReason.WriterFailure));
        var unchangedDeadline = Assert.IsType<GuardianHostContainmentDeadline>(
            rig.Factory.Resources[0].Deadline);
        Assert.Equal(0, unchangedDeadline.StartedTimestamp);
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, unchangedDeadline.AbsoluteTimestamp);
        Assert.NotEqual(
            rig.Factory.Resources[0].StartupDeadline.AbsoluteTimestamp,
            unchangedDeadline.AbsoluteTimestamp);
        rig.Clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        Assert.Equal(
            GuardianHostContainmentDisposition.Pending,
            rig.Controller.ObserveContainmentDeadline(old).Disposition);
        Assert.Single(rig.Factory.Resources);
        Assert.Equal(PublicHostState.Recovering, rig.Controller.Snapshot().Host.State);

        rig.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(
            GuardianHostContainmentDisposition.MarkedUnconfirmed,
            rig.Controller.ObserveContainmentDeadline(old).Disposition);
        var uncertain = rig.Controller.Snapshot().Host;
        AssertHost(
            uncertain,
            PublicHostState.ContainmentUnconfirmed,
            RecoveryPhase: null,
            attempt: 0,
            retryAfter: null,
            ready: false,
            generation: 1);
        Assert.Equal(
            PublicRecoveryDetailCode.HostContainmentUnconfirmed,
            uncertain.LastFailureCode);
        Assert.Single(rig.Factory.Resources);

        var confirmation = rig.Controller.ConfirmContainment(old);

        var replacement = Assert.IsType<GuardianHostAttemptLease>(
            confirmation.StartedAttempt);
        Assert.Equal(GuardianHostContainmentDisposition.Confirmed, confirmation.Disposition);
        Assert.Equal(2, replacement.Identity.HostGeneration.Value);
        Assert.Equal(2, rig.Factory.Resources.Count);
        Assert.Equal(1, rig.Factory.Resources[0].DisposeCount);
        Assert.Equal(
            GuardianHostContainmentDisposition.StaleAttempt,
            rig.Controller.ConfirmContainment(old).Disposition);
        AssertHost(
            rig.Controller.Snapshot().Host,
            PublicHostState.Recovering,
            RecoveryPhase.Attempting,
            attempt: 1,
            retryAfter: 250,
            ready: false,
            generation: 2);
    }

    [Fact]
    public void Failed_recovery_backoff_begins_only_after_confirmed_death()
    {
        var rig = new TestRig();
        var initial = StartReady(rig);
        var firstRecovery = LoseAndConfirm(rig, initial);

        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(firstRecovery, GuardianHostLossReason.Exit));
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(
            GuardianHostStartDisposition.Refused,
            rig.Controller.TryStartDueRecovery().Disposition);

        var confirmation = rig.Controller.ConfirmContainment(firstRecovery);

        Assert.Null(confirmation.StartedAttempt);
        AssertHost(
            rig.Controller.Snapshot().Host,
            PublicHostState.Backoff,
            RecoveryPhase.Backoff,
            attempt: 2,
            retryAfter: 250,
            ready: false,
            generation: null);
        Assert.Equal(
            GuardianHostStartDisposition.Refused,
            rig.Controller.TryStartDueRecovery().Disposition);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(250) - TimeSpan.FromTicks(1));
        Assert.Equal(
            GuardianHostStartDisposition.Refused,
            rig.Controller.TryStartDueRecovery().Disposition);
        rig.Clock.Advance(TimeSpan.FromTicks(1));
        var second = rig.Controller.TryStartDueRecovery();
        Assert.Equal(GuardianHostStartDisposition.Started, second.Disposition);
        Assert.Equal(3, second.Attempt!.Identity.HostGeneration.Value);
    }

    [Fact]
    public void State_reads_and_clock_advance_do_not_consume_due_or_stability_edges()
    {
        var rig = new TestRig();
        var initial = StartReady(rig);
        var recovery = LoseAndConfirm(rig, initial);
        Assert.True(rig.Controller.MarkReady(recovery));

        rig.Clock.Advance(TimeSpan.FromSeconds(60));
        for (var index = 0; index < 100; index++)
        {
            var snapshot = rig.Controller.Snapshot().Host;
            Assert.Equal(PublicHostState.Ready, snapshot.State);
            Assert.Equal(1, snapshot.RecoveryAttempt);
        }
        Assert.True(rig.Controller.TryCompleteReadyStability(recovery));
        Assert.Equal(0, rig.Controller.Snapshot().Host.RecoveryAttempt);
        Assert.False(rig.Controller.TryCompleteReadyStability(recovery));

        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(recovery, GuardianHostLossReason.Exit));
        var fresh = Assert.IsType<GuardianHostAttemptLease>(
            rig.Controller.ConfirmContainment(recovery).StartedAttempt);
        Assert.Equal(1, fresh.RecoveryLease!.AttemptOrdinal);
    }

    [Fact]
    public void Intentional_recycle_preserves_history_and_restarts_stability_window()
    {
        var rig = new TestRig();
        var initial = StartReady(rig);
        var first = LoseAndConfirm(rig, initial);
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(first, GuardianHostLossReason.Exit));
        Assert.Null(rig.Controller.ConfirmContainment(first).StartedAttempt);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(250));
        var second = Assert.IsType<GuardianHostAttemptLease>(
            rig.Controller.TryStartDueRecovery().Attempt);
        Assert.True(rig.Controller.MarkReady(second));
        rig.Clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(second, GuardianHostLossReason.OperatorRecycle));
        var recycled = Assert.IsType<GuardianHostAttemptLease>(
            rig.Controller.ConfirmContainment(second).StartedAttempt);

        Assert.Equal(2, recycled.RecoveryLease!.AttemptOrdinal);
        Assert.True(rig.Controller.MarkReady(recycled));
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(rig.Controller.TryCompleteReadyStability(recycled));
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(rig.Controller.TryCompleteReadyStability(recycled));
    }

    [Fact]
    public void Intentional_recycle_does_not_count_the_healthy_generation_as_failed()
    {
        var rig = new TestRig();
        var initial = StartReady(rig);

        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(initial, GuardianHostLossReason.OperatorRecycle));
        var recycled = Assert.IsType<GuardianHostAttemptLease>(
            rig.Controller.ConfirmContainment(initial).StartedAttempt);
        Assert.Equal(1, recycled.RecoveryLease!.AttemptOrdinal);
        Assert.True(rig.Controller.MarkReady(recycled));
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(recycled, GuardianHostLossReason.Exit));
        Assert.Null(rig.Controller.ConfirmContainment(recycled).StartedAttempt);

        var snapshot = rig.Controller.Snapshot().Host;
        Assert.Equal(PublicHostState.Backoff, snapshot.State);
        Assert.Equal(2, snapshot.RecoveryAttempt);
        Assert.Equal(250, snapshot.RetryAfterMilliseconds);
    }

    [Fact]
    public void Pre_stability_loss_does_not_become_stable_during_containment()
    {
        var rig = new TestRig();
        var initial = StartReady(rig);
        var firstRecovery = LoseAndConfirm(rig, initial);
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(firstRecovery, GuardianHostLossReason.Exit));
        Assert.Null(rig.Controller.ConfirmContainment(firstRecovery).StartedAttempt);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(250));
        var secondRecovery = Assert.IsType<GuardianHostAttemptLease>(
            rig.Controller.TryStartDueRecovery().Attempt);
        Assert.True(rig.Controller.MarkReady(secondRecovery));

        rig.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(secondRecovery, GuardianHostLossReason.Exit));
        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        var confirmed = rig.Controller.ConfirmContainment(secondRecovery);
        Assert.Null(confirmed.StartedAttempt);
        var backoff = rig.Controller.Snapshot().Host;
        Assert.Equal(PublicHostState.Backoff, backoff.State);
        Assert.Equal(3, backoff.RecoveryAttempt);
        Assert.Equal(1_000, backoff.RetryAfterMilliseconds);
    }

    [Fact]
    public void Pre_stability_recycle_preserves_history_across_containment_time()
    {
        var rig = new TestRig();
        var initial = StartReady(rig);
        var firstRecovery = LoseAndConfirm(rig, initial);
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(firstRecovery, GuardianHostLossReason.Exit));
        Assert.Null(rig.Controller.ConfirmContainment(firstRecovery).StartedAttempt);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(250));
        var secondRecovery = Assert.IsType<GuardianHostAttemptLease>(
            rig.Controller.TryStartDueRecovery().Attempt);
        Assert.True(rig.Controller.MarkReady(secondRecovery));

        rig.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(
                secondRecovery,
                GuardianHostLossReason.OperatorRecycle));
        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        var replacement = Assert.IsType<GuardianHostAttemptLease>(
            rig.Controller.ConfirmContainment(secondRecovery).StartedAttempt);
        Assert.Equal(2, replacement.RecoveryLease!.AttemptOrdinal);
        Assert.Equal(2, rig.Controller.Snapshot().Host.RecoveryAttempt);
    }

    [Fact]
    public void Stable_recycle_publishes_attempt_one_from_the_containment_edge()
    {
        var rig = new TestRig();
        var initial = StartReady(rig);
        var firstRecovery = LoseAndConfirm(rig, initial);
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(firstRecovery, GuardianHostLossReason.Exit));
        Assert.Null(rig.Controller.ConfirmContainment(firstRecovery).StartedAttempt);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(250));
        var secondRecovery = Assert.IsType<GuardianHostAttemptLease>(
            rig.Controller.TryStartDueRecovery().Attempt);
        Assert.True(rig.Controller.MarkReady(secondRecovery));
        rig.Clock.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(
                secondRecovery,
                GuardianHostLossReason.OperatorRecycle));
        var containing = rig.Controller.Snapshot().Host;
        Assert.Equal(PublicHostState.Recovering, containing.State);
        Assert.Equal(RecoveryPhase.Containment, containing.RecoveryPhase);
        Assert.Equal(1, containing.RecoveryAttempt);

        var replacement = Assert.IsType<GuardianHostAttemptLease>(
            rig.Controller.ConfirmContainment(secondRecovery).StartedAttempt);
        Assert.Equal(1, replacement.RecoveryLease!.AttemptOrdinal);
    }

    [Fact]
    public void Pre_stability_half_open_loss_reopens_the_circuit_after_slow_containment()
    {
        var rig = new TestRig();
        var current = LoseAndConfirm(rig, StartReady(rig));
        for (var failure = 0; failure < 6; failure++)
        {
            Assert.Equal(
                GuardianHostLifecycleLossDisposition.BeganContainment,
                rig.Controller.ReportLoss(current, GuardianHostLossReason.Exit));
            Assert.Null(rig.Controller.ConfirmContainment(current).StartedAttempt);
            if (failure == 5) break;
            rig.Clock.Advance(ExactBackoffs[failure]);
            current = Assert.IsType<GuardianHostAttemptLease>(
                rig.Controller.TryStartDueRecovery().Attempt);
        }

        rig.Clock.Advance(TimeSpan.FromSeconds(60));
        var halfOpen = Assert.IsType<GuardianHostAttemptLease>(
            rig.Controller.TryStartDueRecovery().Attempt);
        Assert.True(halfOpen.RecoveryLease!.IsHalfOpen);
        Assert.True(rig.Controller.MarkReady(halfOpen));
        rig.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(halfOpen, GuardianHostLossReason.Exit));
        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Null(rig.Controller.ConfirmContainment(halfOpen).StartedAttempt);
        var reopened = rig.Controller.Snapshot().Host;
        Assert.Equal(PublicHostState.CircuitOpen, reopened.State);
        Assert.Equal(8, reopened.RecoveryAttempt);
        Assert.Equal(60_000, reopened.RetryAfterMilliseconds);
    }

    [Fact]
    public void Six_confirmed_failed_generations_open_one_circuit_and_one_half_open_probe()
    {
        var rig = new TestRig();
        var current = LoseAndConfirm(rig, StartReady(rig));

        for (var failure = 0; failure < 6; failure++)
        {
            Assert.Equal(
                GuardianHostLifecycleLossDisposition.BeganContainment,
                rig.Controller.ReportLoss(current, GuardianHostLossReason.Exit));
            Assert.Null(rig.Controller.ConfirmContainment(current).StartedAttempt);
            if (failure == 5) break;
            rig.Clock.Advance(ExactBackoffs[failure]);
            current = Assert.IsType<GuardianHostAttemptLease>(
                rig.Controller.TryStartDueRecovery().Attempt);
        }

        var open = rig.Controller.Snapshot().Host;
        Assert.Equal(PublicHostState.CircuitOpen, open.State);
        Assert.Equal(7, open.RecoveryAttempt);
        Assert.Equal(60_000, open.RetryAfterMilliseconds);
        rig.Clock.Advance(TimeSpan.FromSeconds(60));
        var results = new GuardianHostStartTransition[256];
        Parallel.For(0, results.Length, index =>
            results[index] = rig.Controller.TryStartDueRecovery());
        var probe = Assert.Single(
            results,
            value => value.Attempt is not null).Attempt!;
        Assert.True(probe.RecoveryLease!.IsHalfOpen);
        Assert.Equal(7, probe.RecoveryLease.AttemptOrdinal);
        Assert.Equal(PublicHostState.HalfOpen, rig.Controller.Snapshot().Host.State);
    }

    [Fact]
    public void Launch_proved_no_child_consumes_generation_and_never_reuses_it()
    {
        var rig = new TestRig();
        var initial = StartReady(rig);
        rig.Factory.NextOutcome = GuardianHostLaunchOutcome.ProvedNoChild;

        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(initial, GuardianHostLossReason.Exit));
        var confirmed = rig.Controller.ConfirmContainment(initial);

        Assert.Null(confirmed.StartedAttempt);
        Assert.Equal(PublicHostState.Backoff, rig.Controller.Snapshot().Host.State);
        Assert.Equal(2, rig.Factory.Resources[1].Identity.HostGeneration.Value);
        Assert.Equal(0, rig.Factory.Resources[1].BeginContainmentCount);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(250));
        var later = Assert.IsType<GuardianHostAttemptLease>(
            rig.Controller.TryStartDueRecovery().Attempt);
        Assert.Equal(3, later.Identity.HostGeneration.Value);
    }

    [Fact]
    public void Ambiguous_launch_failure_uses_prepared_containment_authority()
    {
        var rig = new TestRig();
        rig.Factory.ThrowOnNextLaunch = true;

        var start = rig.Controller.StartInitial();

        var attempt = Assert.IsType<GuardianHostAttemptLease>(start.Attempt);
        Assert.Equal(
            GuardianHostStartDisposition.ContainmentStarted,
            start.Disposition);
        var resource = Assert.Single(rig.Factory.Resources);
        Assert.Equal(1, resource.LaunchCount);
        Assert.Equal(1, resource.CloseCount);
        Assert.Equal(1, resource.BeginContainmentCount);
        Assert.Equal(GuardianHostAttemptStage.Containing, attempt.Stage);
        Assert.Equal(PublicHostState.Recovering, rig.Controller.Snapshot().Host.State);
        Assert.Equal(
            GuardianHostContainmentDisposition.Confirmed,
            rig.Controller.ConfirmContainment(attempt).Disposition);
        var stopped = rig.Controller.Snapshot();
        Assert.Equal(PublicHostState.Stopped, stopped.Host.State);
        Assert.Equal(
            GuardianHostPermanentStopReason.InitialStartFailed,
            stopped.PermanentStopReason);
        Assert.Equal(
            GuardianHostStartDisposition.PermanentlyStopped,
            rig.Controller.StartInitial().Disposition);
    }

    [Fact]
    public void Contract_mismatch_and_identity_exhaustion_are_internal_permanent_terminals()
    {
        var mismatchRig = new TestRig();
        var attempt = StartReady(mismatchRig);
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            mismatchRig.Controller.ReportLoss(
                attempt,
                GuardianHostLossReason.ContractMismatch));
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.Duplicate,
            mismatchRig.Controller.ReportLoss(attempt, GuardianHostLossReason.Exit));
        Assert.Equal(
            GuardianHostContainmentDisposition.Confirmed,
            mismatchRig.Controller.ConfirmContainment(attempt).Disposition);
        var mismatch = mismatchRig.Controller.Snapshot();
        Assert.Equal(PublicHostState.Stopped, mismatch.Host.State);
        Assert.Null(mismatch.Host.BootId);
        Assert.Equal(
            GuardianHostPermanentStopReason.ContractMismatch,
            mismatch.PermanentStopReason);
        mismatchRig.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(
            GuardianHostStartDisposition.PermanentlyStopped,
            mismatchRig.Controller.TryStartDueRecovery().Disposition);

        var exhaustedRig = new TestRig(long.MaxValue);
        var exhausted = exhaustedRig.Controller.StartInitial();
        Assert.Equal(GuardianHostStartDisposition.PermanentlyStopped, exhausted.Disposition);
        Assert.Empty(exhaustedRig.Factory.Resources);
        Assert.Equal(
            GuardianHostPermanentStopReason.IdentityExhausted,
            exhaustedRig.Controller.Snapshot().PermanentStopReason);
    }

    [Fact]
    public void Contract_mismatch_promotes_generic_or_unconfirmed_containment_to_stop()
    {
        var generic = new TestRig();
        var attempt = StartReady(generic);
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            generic.Controller.ReportLoss(attempt, GuardianHostLossReason.Exit));
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.Duplicate,
            generic.Controller.ReportLoss(
                attempt,
                GuardianHostLossReason.ContractMismatch));
        Assert.Equal(1, generic.Factory.Resources[0].CloseCount);
        Assert.Equal(1, generic.Factory.Resources[0].BeginContainmentCount);
        var promoted = generic.Controller.Snapshot();
        Assert.Equal(
            GuardianHostPermanentStopReason.ContractMismatch,
            promoted.PermanentStopReason);
        Assert.Equal(GuardianHostLossReason.ContractMismatch, promoted.LastLossReason);
        Assert.Equal(
            PublicRecoveryDetailCode.HostContractMismatch,
            promoted.Host.LastFailureCode);
        Assert.Null(generic.Controller.ConfirmContainment(attempt).StartedAttempt);
        Assert.Equal(PublicHostState.Stopped, generic.Controller.Snapshot().Host.State);
        Assert.Single(generic.Factory.Resources);

        var unconfirmed = new TestRig();
        var uncertainAttempt = StartReady(unconfirmed);
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            unconfirmed.Controller.ReportLoss(
                uncertainAttempt,
                GuardianHostLossReason.ReaderFailure));
        unconfirmed.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(
            GuardianHostContainmentDisposition.MarkedUnconfirmed,
            unconfirmed.Controller.ObserveContainmentDeadline(uncertainAttempt).Disposition);
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.Duplicate,
            unconfirmed.Controller.ReportLoss(
                uncertainAttempt,
                GuardianHostLossReason.ContractMismatch));
        Assert.Null(unconfirmed.Controller.ConfirmContainment(uncertainAttempt).StartedAttempt);
        Assert.Equal(PublicHostState.Stopped, unconfirmed.Controller.Snapshot().Host.State);
        Assert.Single(unconfirmed.Factory.Resources);
    }

    [Fact]
    public void Racing_contract_mismatch_and_exit_always_stop_one_containment()
    {
        for (var iteration = 0; iteration < 256; iteration++)
        {
            var rig = new TestRig();
            var attempt = StartReady(rig);
            GuardianHostLifecycleLossDisposition exit = default;
            GuardianHostLifecycleLossDisposition mismatch = default;

            Parallel.Invoke(
                () => exit = rig.Controller.ReportLoss(attempt, GuardianHostLossReason.Exit),
                () => mismatch = rig.Controller.ReportLoss(
                    attempt,
                    GuardianHostLossReason.ContractMismatch));

            Assert.Contains(
                GuardianHostLifecycleLossDisposition.BeganContainment,
                new[] { exit, mismatch });
            Assert.Contains(
                GuardianHostLifecycleLossDisposition.Duplicate,
                new[] { exit, mismatch });
            Assert.Null(rig.Controller.ConfirmContainment(attempt).StartedAttempt);
            Assert.Equal(PublicHostState.Stopped, rig.Controller.Snapshot().Host.State);
            Assert.Equal(1, rig.Factory.Resources[0].CloseCount);
            Assert.Equal(1, rig.Factory.Resources[0].BeginContainmentCount);
            Assert.Single(rig.Factory.Resources);
        }
    }

    [Fact]
    public void Startup_deadline_and_ready_transition_have_one_exact_gate_winner()
    {
        var before = new TestRig();
        var beforeAttempt = Assert.IsType<GuardianHostAttemptLease>(
            before.Controller.StartInitial().Attempt);
        before.Clock.Advance(TimeSpan.FromSeconds(37) - TimeSpan.FromTicks(1));
        Assert.True(before.Controller.MarkReady(beforeAttempt));
        before.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(
            GuardianHostStartupDeadlineDisposition.Duplicate,
            before.Controller.ObserveStartupDeadline(beforeAttempt));
        Assert.Equal(PublicHostState.Ready, before.Controller.Snapshot().Host.State);
        Assert.Equal(0, before.Factory.Resources[0].BeginContainmentCount);

        var exactReady = new TestRig();
        var exactReadyAttempt = Assert.IsType<GuardianHostAttemptLease>(
            exactReady.Controller.StartInitial().Attempt);
        exactReady.Clock.Advance(TimeSpan.FromSeconds(37));
        Assert.False(exactReady.Controller.MarkReady(exactReadyAttempt));
        Assert.Equal(GuardianHostAttemptStage.Containing, exactReadyAttempt.Stage);
        Assert.Equal(1, exactReady.Factory.Resources[0].BeginContainmentCount);
        Assert.Equal(
            GuardianHostStartupDeadlineDisposition.Stopped,
            exactReady.Controller.ObserveStartupDeadline(exactReadyAttempt));

        var exactTimer = new TestRig();
        var exactTimerAttempt = Assert.IsType<GuardianHostAttemptLease>(
            exactTimer.Controller.StartInitial().Attempt);
        Assert.Equal(
            GuardianHostStartupDeadlineDisposition.Pending,
            exactTimer.Controller.ObserveStartupDeadline(exactTimerAttempt));
        exactTimer.Clock.Advance(TimeSpan.FromSeconds(37));
        Assert.Equal(
            GuardianHostStartupDeadlineDisposition.BeganContainment,
            exactTimer.Controller.ObserveStartupDeadline(exactTimerAttempt));
        Assert.False(exactTimer.Controller.MarkReady(exactTimerAttempt));
        Assert.Equal(1, exactTimer.Factory.Resources[0].BeginContainmentCount);

        var afterBootstrap = new TestRig();
        var bootstrapAttempt = Assert.IsType<GuardianHostAttemptLease>(
            afterBootstrap.Controller.StartInitial().Attempt);
        Assert.True(afterBootstrap.Controller.MarkBootstrapping(bootstrapAttempt));
        afterBootstrap.Clock.Advance(TimeSpan.FromSeconds(38));
        Assert.False(afterBootstrap.Controller.MarkReady(bootstrapAttempt));
        Assert.Equal(GuardianHostAttemptStage.Containing, bootstrapAttempt.Stage);
        Assert.Equal(1, afterBootstrap.Factory.Resources[0].BeginContainmentCount);
    }

    [Fact]
    public void Startup_deadline_is_enforced_before_prepare_and_after_slow_launch()
    {
        var beforePrepare = new TestRig();
        beforePrepare.Deadlines.Offset = TimeSpan.Zero;

        var refused = beforePrepare.Controller.StartInitial();

        Assert.Equal(GuardianHostStartDisposition.ProvedNoChild, refused.Disposition);
        Assert.Null(refused.Attempt);
        Assert.Empty(beforePrepare.Factory.Resources);
        Assert.Equal(PublicHostState.Stopped, beforePrepare.Controller.Snapshot().Host.State);

        var slowLaunch = new TestRig();
        slowLaunch.Factory.OnLaunch = _ =>
            slowLaunch.Clock.Advance(TimeSpan.FromSeconds(37));

        var contained = slowLaunch.Controller.StartInitial();

        var attempt = Assert.IsType<GuardianHostAttemptLease>(contained.Attempt);
        Assert.Equal(GuardianHostStartDisposition.ContainmentStarted, contained.Disposition);
        Assert.Equal(GuardianHostAttemptStage.Containing, attempt.Stage);
        Assert.Equal(1, slowLaunch.Factory.Resources[0].LaunchCount);
        Assert.Equal(1, slowLaunch.Factory.Resources[0].BeginContainmentCount);
        Assert.False(slowLaunch.Controller.MarkReady(attempt));
    }

    [Fact]
    public void Racing_ready_and_expired_startup_timer_never_publish_ready()
    {
        for (var iteration = 0; iteration < 256; iteration++)
        {
            var rig = new TestRig();
            var attempt = Assert.IsType<GuardianHostAttemptLease>(
                rig.Controller.StartInitial().Attempt);
            rig.Clock.Advance(TimeSpan.FromSeconds(37));
            var ready = false;
            GuardianHostStartupDeadlineDisposition timer = default;

            Parallel.Invoke(
                () => ready = rig.Controller.MarkReady(attempt),
                () => timer = rig.Controller.ObserveStartupDeadline(attempt));

            Assert.False(ready);
            Assert.Contains(
                timer,
                new[]
                {
                    GuardianHostStartupDeadlineDisposition.BeganContainment,
                    GuardianHostStartupDeadlineDisposition.Duplicate,
                    GuardianHostStartupDeadlineDisposition.Stopped,
                });
            Assert.Equal(GuardianHostAttemptStage.Containing, attempt.Stage);
            Assert.Equal(PublicHostState.Recovering, rig.Controller.Snapshot().Host.State);
            Assert.Equal(1, rig.Factory.Resources[0].BeginContainmentCount);
        }
    }

    [Fact]
    public async Task First_write_and_loss_are_linearized_at_the_possibly_writing_callback()
    {
        var writeFirst = new TestRig();
        var attempt = StartReady(writeFirst);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var lossStarted = new ManualResetEventSlim();
        var callbackCount = 0;
        var write = Task.Run(() => writeFirst.Controller.BeginFirstWrite(
            attempt,
            _ =>
            {
                Interlocked.Increment(ref callbackCount);
                entered.Set();
                release.Wait();
            }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var loss = Task.Run(() =>
        {
            lossStarted.Set();
            return writeFirst.Controller.ReportLoss(
                attempt,
                GuardianHostLossReason.Exit);
        });
        Assert.True(lossStarted.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(loss.IsCompleted);
        release.Set();
        Assert.Equal(GuardianHostWriteDisposition.Began, await write);
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            await loss);
        Assert.Equal(1, callbackCount);

        var lossFirst = new TestRig();
        var lostAttempt = StartReady(lossFirst);
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            lossFirst.Controller.ReportLoss(
                lostAttempt,
                GuardianHostLossReason.Exit));
        Assert.Equal(
            GuardianHostWriteDisposition.NotReady,
            lossFirst.Controller.BeginFirstWrite(
                lostAttempt,
                _ => throw new InvalidOperationException("Must not write.")));
    }

    [Fact]
    public async Task Shutdown_waits_for_launch_boundary_then_prevents_every_restart_edge()
    {
        var rig = new TestRig();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var shutdownStarted = new ManualResetEventSlim();
        rig.Factory.OnLaunch = _ =>
        {
            entered.Set();
            release.Wait();
        };
        var startTask = Task.Run(rig.Controller.StartInitial);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var shutdownTask = Task.Run(async () =>
        {
            shutdownStarted.Set();
            await rig.Controller.ShutdownAsync();
        });
        Assert.True(shutdownStarted.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(shutdownTask.IsCompleted);
        release.Set();
        var start = await startTask;
        var attempt = Assert.IsType<GuardianHostAttemptLease>(start.Attempt);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(shutdownTask.IsCompleted);
        Assert.Equal(1, rig.Factory.Resources[0].BeginContainmentCount);
        Assert.True(rig.Controller.Snapshot().TerminalShutdown);
        Assert.False(rig.Controller.MarkReady(attempt));
        rig.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(
            GuardianHostStartDisposition.PermanentlyStopped,
            rig.Controller.TryStartDueRecovery().Disposition);
        Assert.Equal(
            GuardianHostContainmentDisposition.Confirmed,
            rig.Controller.ConfirmContainment(attempt).Disposition);
        await shutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PublicHostState.Stopped, rig.Controller.Snapshot().Host.State);
        Assert.Single(rig.Factory.Resources);
    }

    [Fact]
    public async Task Unconfirmed_terminal_shutdown_retains_identity_and_never_restarts()
    {
        var rig = new TestRig();
        var attempt = StartReady(rig);

        var shutdown = rig.Controller.ShutdownAsync();
        Assert.Same(shutdown, rig.Controller.ShutdownAsync());
        Assert.False(shutdown.IsCompleted);
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(
            GuardianHostContainmentDisposition.MarkedUnconfirmed,
            rig.Controller.ObserveContainmentDeadline(attempt).Disposition);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        var unconfirmed = rig.Controller.Snapshot();
        Assert.True(unconfirmed.TerminalShutdown);
        Assert.Equal(PublicHostState.ContainmentUnconfirmed, unconfirmed.Host.State);
        Assert.NotNull(unconfirmed.Host.BootId);
        Assert.Equal(1, unconfirmed.Host.Generation!.Value);
        Assert.Equal(
            GuardianHostStartDisposition.PermanentlyStopped,
            rig.Controller.TryStartDueRecovery().Disposition);
        Assert.Single(rig.Factory.Resources);

        Assert.Equal(
            GuardianHostContainmentDisposition.Confirmed,
            rig.Controller.ConfirmContainment(attempt).Disposition);
        Assert.Equal(PublicHostState.Stopped, rig.Controller.Snapshot().Host.State);
        Assert.Single(rig.Factory.Resources);
    }

    [Fact]
    public async Task Shutdown_without_an_owned_child_completes_once_and_refuses_initial_start()
    {
        var rig = new TestRig();

        var first = rig.Controller.ShutdownAsync();
        var second = rig.Controller.ShutdownAsync();

        Assert.Same(first, second);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PublicHostState.Stopped, rig.Controller.Snapshot().Host.State);
        Assert.Equal(
            GuardianHostStartDisposition.PermanentlyStopped,
            rig.Controller.StartInitial().Disposition);
        Assert.Empty(rig.Factory.Resources);
    }

    [Fact]
    public async Task Terminal_shutdown_reason_cannot_bypass_the_ordered_shutdown_edge()
    {
        var rig = new TestRig();
        var attempt = StartReady(rig);

        Assert.Throws<ArgumentException>(() => rig.Controller.ReportLoss(
            attempt,
            GuardianHostLossReason.TerminalShutdown));
        Assert.Equal(PublicHostState.Ready, rig.Controller.Snapshot().Host.State);
        Assert.Equal(0, rig.Factory.Resources[0].BeginContainmentCount);

        var shutdown = rig.Controller.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(
            GuardianHostContainmentDisposition.Confirmed,
            rig.Controller.ConfirmContainment(attempt).Disposition);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(rig.Controller.Snapshot().TerminalShutdown);
        Assert.Equal(PublicHostState.Stopped, rig.Controller.Snapshot().Host.State);
    }

    [Fact]
    public void Old_generation_callbacks_are_inert_after_replacement()
    {
        var rig = new TestRig();
        var old = StartReady(rig);
        var replacement = LoseAndConfirm(rig, old);

        Assert.False(rig.Controller.MarkBootstrapping(old));
        Assert.False(rig.Controller.MarkReady(old));
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.StaleAttempt,
            rig.Controller.ReportLoss(old, GuardianHostLossReason.Exit));
        Assert.Equal(
            GuardianHostContainmentDisposition.StaleAttempt,
            rig.Controller.ObserveContainmentDeadline(old).Disposition);
        Assert.Equal(
            GuardianHostWriteDisposition.StaleAttempt,
            rig.Controller.BeginFirstWrite(
                old,
                _ => throw new InvalidOperationException("Must not write.")));
        Assert.Equal(2, replacement.Identity.HostGeneration.Value);
        Assert.Equal(2, rig.Controller.Snapshot().Host.Generation!.Value);
    }

    private static GuardianHostAttemptLease StartReady(TestRig rig)
    {
        var start = rig.Controller.StartInitial();
        var attempt = Assert.IsType<GuardianHostAttemptLease>(start.Attempt);
        Assert.Equal(GuardianHostStartDisposition.Started, start.Disposition);
        Assert.True(rig.Controller.MarkReady(attempt));
        return attempt;
    }

    private static GuardianHostAttemptLease LoseAndConfirm(
        TestRig rig,
        GuardianHostAttemptLease attempt)
    {
        Assert.Equal(
            GuardianHostLifecycleLossDisposition.BeganContainment,
            rig.Controller.ReportLoss(attempt, GuardianHostLossReason.Exit));
        return Assert.IsType<GuardianHostAttemptLease>(
            rig.Controller.ConfirmContainment(attempt).StartedAttempt);
    }

    private static void AssertHost(
        PublicHostStateSnapshot snapshot,
        PublicHostState state,
        RecoveryPhase? RecoveryPhase,
        long attempt,
        int? retryAfter,
        bool ready,
        long? generation)
    {
        Assert.Equal(state, snapshot.State);
        Assert.Equal(RecoveryPhase, snapshot.RecoveryPhase);
        Assert.Equal(attempt, snapshot.RecoveryAttempt);
        Assert.Equal(retryAfter, snapshot.RetryAfterMilliseconds);
        Assert.Equal(ready, snapshot.ReadyForEffects);
        Assert.Equal(generation, snapshot.Generation?.Value);
        Assert.Equal(generation is not null, snapshot.BootId is not null);
    }

    private sealed class TestRig
    {
        internal TestRig(long initialGeneration = 0)
        {
            Clock = new ManualTimeProvider();
            Factory = new FakeAttemptFactory();
            Deadlines = new FakeStartupDeadlineSource(Clock);
            Controller = new GuardianHostLifecycleController(
                new GuardianBootId(Guid.Parse("10000000-0000-4000-8000-000000000001")),
                new MonotonicHostGenerationAllocator(initialGeneration),
                new SequentialBootIdSource(),
                Deadlines,
                Factory,
                Clock);
        }

        internal ManualTimeProvider Clock { get; }
        internal FakeAttemptFactory Factory { get; }
        internal FakeStartupDeadlineSource Deadlines { get; }
        internal GuardianHostLifecycleController Controller { get; }
    }

    private sealed class SequentialBootIdSource : IGuardianHostBootIdSource
    {
        private long _next;

        public HostBootId Next()
        {
            var ordinal = Interlocked.Increment(ref _next);
            return new HostBootId(Guid.Parse(
                $"20000000-0000-4000-8000-{ordinal:x12}"));
        }
    }

    private sealed class FakeStartupDeadlineSource : IGuardianHostStartupDeadlineSource
    {
        private readonly ManualTimeProvider _clock;

        internal FakeStartupDeadlineSource(ManualTimeProvider clock)
        {
            _clock = clock;
        }

        internal List<GuardianHostStartupDeadline> Issued { get; } = [];
        internal TimeSpan Offset { get; set; } = TimeSpan.FromSeconds(37);

        public GuardianHostStartupDeadline Next()
        {
            var deadline = new GuardianHostStartupDeadline(
                checked(_clock.GetTimestamp() + Offset.Ticks));
            Issued.Add(deadline);
            return deadline;
        }
    }

    private sealed class FakeAttemptFactory : IGuardianHostAttemptFactory
    {
        internal List<FakeAttemptResources> Resources { get; } = [];
        internal GuardianHostLaunchOutcome NextOutcome { get; set; } =
            GuardianHostLaunchOutcome.Started;
        internal bool ThrowOnNextLaunch { get; set; }
        internal bool ThrowOnNextPrepare { get; set; }
        internal Action<FakeAttemptResources>? OnLaunch { get; set; }

        public IGuardianHostAttemptResources Prepare(
            GuardianHostIdentity identity,
            GuardianHostStartupDeadline startupDeadline)
        {
            if (ThrowOnNextPrepare)
            {
                ThrowOnNextPrepare = false;
                throw new IOException("Injected proved-no-child preparation failure.");
            }

            var resource = new FakeAttemptResources(
                identity,
                startupDeadline,
                NextOutcome,
                ThrowOnNextLaunch,
                OnLaunch);
            NextOutcome = GuardianHostLaunchOutcome.Started;
            ThrowOnNextLaunch = false;
            Resources.Add(resource);
            return resource;
        }
    }

    private sealed class FakeAttemptResources : IGuardianHostAttemptResources
    {
        private readonly GuardianHostLaunchOutcome _outcome;
        private readonly bool _throwOnLaunch;
        private readonly Action<FakeAttemptResources>? _onLaunch;

        internal FakeAttemptResources(
            GuardianHostIdentity identity,
            GuardianHostStartupDeadline startupDeadline,
            GuardianHostLaunchOutcome outcome,
            bool throwOnLaunch,
            Action<FakeAttemptResources>? onLaunch)
        {
            Identity = identity;
            StartupDeadline = startupDeadline;
            _outcome = outcome;
            _throwOnLaunch = throwOnLaunch;
            _onLaunch = onLaunch;
        }

        internal GuardianHostIdentity Identity { get; }
        internal GuardianHostStartupDeadline StartupDeadline { get; }
        internal int LaunchCount { get; private set; }
        internal int CloseCount { get; private set; }
        internal int BeginContainmentCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal GuardianHostContainmentDeadline? Deadline { get; private set; }

        public GuardianHostLaunchOutcome Launch()
        {
            LaunchCount++;
            _onLaunch?.Invoke(this);
            if (_throwOnLaunch)
                throw new IOException("Injected ambiguous launch failure.");
            return _outcome;
        }

        public void CloseTransport()
        {
            CloseCount++;
        }

        public void BeginContainment(GuardianHostContainmentDeadline deadline)
        {
            BeginContainmentCount++;
            Deadline = deadline;
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());

        internal void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));
            Interlocked.Add(ref _timestamp, duration.Ticks);
        }
    }
}
