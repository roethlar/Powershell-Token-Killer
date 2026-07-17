using System.Text;
using PtkMcpGuardian.Lifecycle;
using PtkMcpServer.GuardianHost;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class SessionRecoveryStateMachineTests
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
    public void Eligible_template_waits_for_confirmed_death_then_restores_exact_frozen_bytes_once()
    {
        var sourceBytes = Encoding.UTF8.GetBytes("Set-Variable -Name planted -Value 'secret'");
        var expected = sourceBytes.ToArray();
        var harness = Harness.CreateTemplate(sourceBytes);
        harness.PrimeReady();
        Array.Fill(sourceBytes, (byte)0x5a);

        var loss = Assert.IsType<SessionRecoveryLossLease>(
            harness.Machine.BeginUnexpectedLoss(
                harness.OldIdentity,
                harness.Machine.FrozenBindingProof).Loss);

        Assert.Empty(harness.Generations.Allocations);
        var containing = harness.Machine.Snapshot().Session;
        Assert.Equal(PublicSessionState.Recovering, containing.State);
        Assert.Equal(RecoveryPhase.Containment, containing.RecoveryPhase);
        Assert.Equal(1, containing.RecoveryAttempt);
        Assert.False(containing.ReadyForEffects);
        Assert.True(containing.WarmStateLost);
        Assert.Equal(harness.OldIdentity.BootId, containing.WorkerBootId);

        var death = harness.Machine.ConfirmOldTreeDeath(loss);
        var attempt = Assert.IsType<SessionRecoveryAttemptLease>(death.Attempt);
        Assert.Equal(SessionRecoveryDeathDisposition.AttemptStarted, death.Disposition);
        Assert.Equal(2, attempt.WorkerIdentity.Generation.Value);
        Assert.Single(harness.Generations.Allocations);

        Assert.Equal(
            SessionRecoveryAttemptDisposition.Ready,
            harness.Machine.ExecuteBaseline(attempt).Disposition);
        Assert.Equal(expected, Assert.Single(harness.Factory.CallbackSnapshots));
        Assert.All(
            Assert.Single(harness.Factory.CallbackMemories).ToArray(),
            value => Assert.Equal(0, value));
        Assert.Equal(
            SessionRecoveryAttemptDisposition.StaleAttempt,
            harness.Machine.ExecuteBaseline(attempt).Disposition);
        Assert.Equal(1, harness.Factory.CallbackCount);

        var ready = harness.Machine.Snapshot().Session;
        Assert.Equal(PublicSessionState.Ready, ready.State);
        Assert.Equal(attempt.WorkerIdentity.BootId, ready.WorkerBootId);
        Assert.Equal(attempt.WorkerIdentity.Generation, ready.Generation);
        Assert.Equal(BootstrapState.Restored, ready.BootstrapState);
        Assert.True(ready.ReadyForEffects);
        Assert.True(ready.WarmStateLost);
        Assert.Equal(PublicRecoveryDetailCode.SessionRecovering, ready.LastFailureCode);
    }

    [Theory]
    [InlineData(RecoveryBindingKind.Default)]
    [InlineData(RecoveryBindingKind.Dynamic)]
    public void Default_and_dynamic_recovery_create_an_empty_baseline(
        RecoveryBindingKind bindingKind)
    {
        var harness = Harness.CreateNonTemplate(bindingKind);
        harness.PrimeReady();

        var attempt = BeginFirstAttempt(harness);
        Assert.Equal(
            SessionRecoveryAttemptDisposition.Ready,
            harness.Machine.ExecuteBaseline(attempt).Disposition);

        Assert.Empty(Assert.Single(harness.Factory.CallbackSnapshots));
        var snapshot = harness.Machine.Snapshot().Session;
        Assert.Equal(BootstrapState.NotApplicable, snapshot.BootstrapState);
        Assert.Equal(PublicSessionState.Ready, snapshot.State);
    }

    [Fact]
    public void Cold_never_ready_alias_is_not_speculatively_opened()
    {
        var harness = Harness.CreateTemplate();

        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);

        Assert.Equal(SessionRecoveryLossDisposition.NotEligible, loss.Disposition);
        Assert.Null(loss.Loss);
        Assert.Empty(harness.Generations.Allocations);
        Assert.Equal(PublicSessionState.Cold, harness.Machine.Snapshot().Session.State);
    }

    [Fact]
    public void Acknowledged_closed_alias_ignores_late_old_worker_loss()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var close = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Close));
        Assert.True(harness.Machine.MarkLifecycleDispatched(close));
        Assert.True(harness.Machine.CompleteLifecycleTerminal(
            close,
            PublicSessionState.Cold,
            null,
            close.ExpectedTransitionVersion,
            BootstrapState.Pending,
            oldWorkerDeathConfirmed: true));

        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);

        Assert.Equal(SessionRecoveryLossDisposition.NotEligible, loss.Disposition);
        Assert.Null(loss.Loss);
        Assert.Empty(harness.Generations.Allocations);
        var snapshot = harness.Machine.Snapshot().Session;
        Assert.Equal(DesiredSessionState.Cold, snapshot.DesiredState);
        Assert.Equal(PublicSessionState.Cold, snapshot.State);
        Assert.Null(snapshot.WorkerBootId);
    }

    [Fact]
    public void Explicitly_faulted_alias_is_not_automatically_opened()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        Assert.True(harness.Machine.RecordExplicitlyFaulted(new SessionTransitionVersion(2)));

        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);

        Assert.Equal(SessionRecoveryLossDisposition.NotEligible, loss.Disposition);
        Assert.NotNull(loss.Loss);
        Assert.Equal(SessionRecoveryDeathDisposition.Faulted,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).Disposition);
        Assert.Empty(harness.Generations.Allocations);
        var snapshot = harness.Machine.Snapshot().Session;
        Assert.Equal(PublicSessionState.Faulted, snapshot.State);
        Assert.False(snapshot.ReadyForEffects);
    }

    public static TheoryData<int> AmbiguousTransitions =>
        new()
        {
            (int)SessionRecoveryTransitionKind.Open,
            (int)SessionRecoveryTransitionKind.Close,
            (int)SessionRecoveryTransitionKind.Restart,
            (int)SessionRecoveryTransitionKind.Reset,
            (int)SessionRecoveryTransitionKind.Bootstrap,
        };

    [Theory]
    [MemberData(nameof(AmbiguousTransitions))]
    public void Dispatched_unacknowledged_lifecycle_is_recovery_unknown_and_never_replayed(
        int kindValue)
    {
        var kind = (SessionRecoveryTransitionKind)kindValue;
        var harness = Harness.CreateTemplate();
        GuardianHostWorkerIdentity lostIdentity;
        if (kind == SessionRecoveryTransitionKind.Open)
        {
            var transition = Assert.IsType<SessionRecoveryTransitionLease>(
                harness.Machine.BeginLifecycleTransition(kind));
            lostIdentity = WorkerIdentity(2, 52);
            Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
                transition,
                lostIdentity));
            Assert.True(harness.Machine.MarkLifecycleDispatched(transition));
        }
        else
        {
            harness.PrimeReady();
            var transition = Assert.IsType<SessionRecoveryTransitionLease>(
                harness.Machine.BeginLifecycleTransition(kind));
            if (kind is SessionRecoveryTransitionKind.Restart or
                SessionRecoveryTransitionKind.Reset)
            {
                Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
                    transition,
                    WorkerIdentity(2, 53 + kindValue)));
            }
            Assert.True(harness.Machine.MarkLifecycleDispatched(transition));
            lostIdentity = harness.OldIdentity;
        }

        var loss = harness.Machine.BeginUnexpectedLoss(
            lostIdentity,
            harness.Machine.FrozenBindingProof);

        Assert.Equal(SessionRecoveryLossDisposition.RecoveryUnknown, loss.Disposition);
        var lease = Assert.IsType<SessionRecoveryLossLease>(loss.Loss);
        Assert.Equal(
            SessionRecoveryDeathDisposition.NoRecovery,
            harness.Machine.ConfirmOldTreeDeath(lease).Disposition);
        harness.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(
            SessionRecoveryAttemptStartDisposition.NotDue,
            harness.Machine.TryStartDueAttempt().Disposition);
        Assert.Empty(harness.Generations.Allocations);

        var snapshot = harness.Machine.Snapshot().Session;
        Assert.Equal(PublicSessionState.RecoveryUnknown, snapshot.State);
        Assert.Equal(BootstrapState.Unknown, snapshot.BootstrapState);
        Assert.Equal(
            PublicRecoveryDetailCode.SessionRecoveryUnknown,
            snapshot.LastFailureCode);
        Assert.False(snapshot.ReadyForEffects);
    }

    [Fact]
    public void Dispatched_lifecycle_publishes_its_epoch_and_repair_advances_past_it()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var ambiguous = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Reset));
        var target = WorkerIdentity(2, 1890);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(ambiguous, target));
        Assert.True(harness.Machine.MarkLifecycleDispatched(ambiguous));

        Assert.Equal(
            ambiguous.ExpectedTransitionVersion,
            harness.Machine.Snapshot().Session.TransitionVersion);

        var sourceLoss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);
        Assert.Equal(SessionRecoveryLossDisposition.RecoveryUnknown, sourceLoss.Disposition);
        Assert.Equal(
            SessionRecoveryDeathDisposition.NoRecovery,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(sourceLoss.Loss)).Disposition);
        var targetLoss = harness.Machine.BeginUnexpectedLoss(
            target,
            harness.Machine.FrozenBindingProof);
        Assert.Equal(SessionRecoveryLossDisposition.RecoveryUnknown, targetLoss.Disposition);
        Assert.Equal(
            SessionRecoveryDeathDisposition.NoRecovery,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(targetLoss.Loss)).Disposition);

        var repair = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Restart,
                harness.Machine.FrozenBindingProof));
        Assert.True(
            repair.ExpectedTransitionVersion.Value >
            ambiguous.ExpectedTransitionVersion.Value);
    }

    [Fact]
    public void Undispatched_lifecycle_does_not_make_prior_ready_state_ambiguous()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        Assert.NotNull(harness.Machine.BeginLifecycleTransition(
            SessionRecoveryTransitionKind.Restart));

        var transition = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);

        Assert.Equal(
            SessionRecoveryLossDisposition.BeganContainment,
            transition.Disposition);
        Assert.NotNull(harness.Machine.ConfirmOldTreeDeath(
            Assert.IsType<SessionRecoveryLossLease>(transition.Loss)).Attempt);
    }

    [Fact]
    public void Undispatched_attached_restart_source_loss_recovers_after_consumed_generation_gap()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var transition = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Restart));
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            transition,
            WorkerIdentity(2, 192)));
        harness.Generations.SetHighWatermark(2);

        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);

        Assert.Equal(SessionRecoveryLossDisposition.BeganContainment,
            loss.Disposition);
        var attempt = Assert.IsType<SessionRecoveryAttemptLease>(
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).Attempt);
        Assert.Equal(3, attempt.WorkerIdentity.Generation.Value);
        Assert.Null(harness.Machine.Snapshot().ActiveTransition);
        harness.Machine.Shutdown();
    }

    [Theory]
    [InlineData((int)SessionRecoveryTransitionKind.Open)]
    [InlineData((int)SessionRecoveryTransitionKind.Restart)]
    public void Predispatch_attached_target_loss_is_refused_as_unlaunched(
        int kindValue)
    {
        var kind = (SessionRecoveryTransitionKind)kindValue;
        var harness = Harness.CreateTemplate();
        if (kind == SessionRecoveryTransitionKind.Restart)
            harness.PrimeReady();
        var transition = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(kind));
        var reservedIdentity = WorkerIdentity(2, 193 + kindValue);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            transition,
            reservedIdentity));

        var loss = harness.Machine.BeginUnexpectedLoss(
            reservedIdentity,
            harness.Machine.FrozenBindingProof);

        Assert.Equal(SessionRecoveryLossDisposition.StaleWorker, loss.Disposition);
        Assert.Null(loss.Loss);
        Assert.Equal(
            kind == SessionRecoveryTransitionKind.Open
                ? PublicSessionState.Cold
                : PublicSessionState.Ready,
            harness.Machine.Snapshot().Session.State);
        Assert.True(harness.Machine.AbandonUndispatchedLifecycleTransition(
            transition));
        Assert.Empty(harness.Generations.Allocations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Initial_acknowledgement_rejects_unearned_future_transition_version(
        int acknowledgementKind)
    {
        var harness = Harness.CreateTemplate();
        var future = new SessionTransitionVersion(2);

        var accepted = acknowledgementKind switch
        {
            0 => harness.Machine.RecordAcknowledgedReady(
                harness.OldIdentity,
                future,
                BootstrapState.Restored),
            1 => harness.Machine.RecordAcknowledgedCold(future),
            2 => harness.Machine.RecordExplicitlyFaulted(future),
            _ => throw new ArgumentOutOfRangeException(
                nameof(acknowledgementKind)),
        };

        Assert.False(accepted);
        var snapshot = harness.Machine.Snapshot().Session;
        Assert.Equal(PublicSessionState.Cold, snapshot.State);
        Assert.Equal(new SessionTransitionVersion(1), snapshot.TransitionVersion);
    }

    [Fact]
    public void Initial_acknowledgement_requires_frozen_desired_state_and_granted_generation()
    {
        var frozenCold = Harness.CreateTemplate(
            desiredState: DesiredSessionState.Cold);
        Assert.False(frozenCold.Machine.RecordAcknowledgedReady(
            frozenCold.OldIdentity,
            new SessionTransitionVersion(1),
            BootstrapState.Restored));
        Assert.True(frozenCold.Machine.RecordAcknowledgedCold(
            new SessionTransitionVersion(1)));

        var frozenReady = Harness.CreateTemplate();
        Assert.False(frozenReady.Machine.RecordAcknowledgedCold(
            new SessionTransitionVersion(1)));
        Assert.False(frozenReady.Machine.RecordAcknowledgedReady(
            WorkerIdentity(2, 1881),
            new SessionTransitionVersion(1),
            BootstrapState.Restored));
        Assert.True(frozenReady.Machine.RecordAcknowledgedReady(
            frozenReady.OldIdentity,
            new SessionTransitionVersion(1),
            BootstrapState.Restored));
    }

    [Fact]
    public void Initial_cold_acknowledgement_cannot_erase_an_explicit_fault()
    {
        var harness = Harness.CreateTemplate(
            desiredState: DesiredSessionState.Cold);
        var version = harness.Machine.Snapshot().Session.TransitionVersion;
        Assert.True(harness.Machine.RecordExplicitlyFaulted(version));

        Assert.False(harness.Machine.RecordAcknowledgedCold(version));
        var snapshot = harness.Machine.Snapshot().Session;
        Assert.Equal(PublicSessionState.Faulted, snapshot.State);
        Assert.Equal(BootstrapState.Failed, snapshot.BootstrapState);
        Assert.Equal(
            PublicRecoveryDetailCode.SessionBootstrapFailed,
            snapshot.LastFailureCode);
    }

    [Fact]
    public void Late_cold_acknowledgement_cannot_erase_ambiguous_open()
    {
        var harness = Harness.CreateTemplate(
            desiredState: DesiredSessionState.Cold);
        var initialVersion = harness.Machine.Snapshot().Session.TransitionVersion;
        var open = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Open));
        var target = WorkerIdentity(2, 1882);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(open, target));
        Assert.True(harness.Machine.MarkLifecycleDispatched(open));
        var loss = harness.Machine.BeginUnexpectedLoss(
            target,
            harness.Machine.FrozenBindingProof);
        Assert.Equal(SessionRecoveryLossDisposition.RecoveryUnknown, loss.Disposition);
        Assert.Equal(
            SessionRecoveryDeathDisposition.NoRecovery,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).Disposition);

        Assert.False(harness.Machine.RecordAcknowledgedCold(initialVersion));
        var snapshot = harness.Machine.Snapshot().Session;
        Assert.Equal(PublicSessionState.RecoveryUnknown, snapshot.State);
        Assert.Equal(BootstrapState.Unknown, snapshot.BootstrapState);
        Assert.Equal(
            PublicRecoveryDetailCode.SessionRecoveryUnknown,
            snapshot.LastFailureCode);
    }

    [Theory]
    [InlineData((int)SessionRecoveryTransitionKind.Restart, false)]
    [InlineData((int)SessionRecoveryTransitionKind.Restart, true)]
    [InlineData((int)SessionRecoveryTransitionKind.Reset, false)]
    [InlineData((int)SessionRecoveryTransitionKind.Reset, true)]
    public void Dispatched_attached_replacement_loss_is_unknown_for_source_or_target(
        int kindValue,
        bool loseTarget)
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var recovered = BeginFirstAttempt(harness);
        Assert.Equal(SessionRecoveryAttemptDisposition.Ready,
            harness.Machine.ExecuteBaseline(recovered).Disposition);
        var transition = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                (SessionRecoveryTransitionKind)kindValue));
        var target = WorkerIdentity(3, 196 + kindValue);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            transition,
            target));
        Assert.True(harness.Machine.MarkLifecycleDispatched(transition));

        var loss = harness.Machine.BeginUnexpectedLoss(
            loseTarget ? target : recovered.WorkerIdentity,
            harness.Machine.FrozenBindingProof);

        Assert.Equal(SessionRecoveryLossDisposition.RecoveryUnknown,
            loss.Disposition);
        Assert.Equal(SessionRecoveryDeathDisposition.NoRecovery,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).Disposition);
        Assert.Equal(PublicSessionState.RecoveryUnknown,
            harness.Machine.Snapshot().Session.State);
        Assert.Equal(1, harness.Factory.CallbackCount);
        if (!loseTarget)
        {
            Assert.Equal(1, harness.Factory.DisposeCount);
            Assert.Null(harness.Machine.Snapshot().Session.WorkerBootId);
            Assert.True(harness.Machine.Snapshot().AwaitingOldTreeDeath);
            Assert.Equal((SessionRecoveryTransitionKind)kindValue,
                harness.Machine.Snapshot().ActiveTransition);
            Assert.Null(harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Restart));
            var targetLoss = harness.Machine.BeginUnexpectedLoss(
                target,
                harness.Machine.FrozenBindingProof);
            Assert.Equal(SessionRecoveryLossDisposition.RecoveryUnknown,
                targetLoss.Disposition);
            Assert.Equal(SessionRecoveryDeathDisposition.NoRecovery,
                harness.Machine.ConfirmOldTreeDeath(
                    Assert.IsType<SessionRecoveryLossLease>(
                        targetLoss.Loss)).Disposition);
            Assert.Null(harness.Machine.Snapshot().ActiveTransition);
            Assert.False(harness.Machine.Snapshot().AwaitingOldTreeDeath);
            return;
        }

        Assert.Equal(0, harness.Factory.DisposeCount);
        Assert.Equal(recovered.WorkerIdentity.BootId,
            harness.Machine.Snapshot().Session.WorkerBootId);
        var close = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Close,
                harness.Machine.FrozenBindingProof));
        Assert.True(harness.Machine.MarkLifecycleDispatched(close));
        Assert.True(harness.Machine.CompleteLifecycleTerminal(
            close,
            PublicSessionState.Cold,
            null,
            close.ExpectedTransitionVersion,
            BootstrapState.Pending,
            oldWorkerDeathConfirmed: true));
        Assert.Equal(1, harness.Factory.DisposeCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Both_dispatched_restart_workers_can_report_loss_and_confirm_in_either_order(
        bool targetReportsFirst,
        bool targetConfirmsFirst)
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var recovered = BeginFirstAttempt(harness);
        Assert.Equal(SessionRecoveryAttemptDisposition.Ready,
            harness.Machine.ExecuteBaseline(recovered).Disposition);
        var transition = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Restart));
        var target = WorkerIdentity(3, 1981);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            transition,
            target));
        Assert.True(harness.Machine.MarkLifecycleDispatched(transition));

        var sourceIdentity = recovered.WorkerIdentity;
        var firstIdentity = targetReportsFirst ? target : sourceIdentity;
        var secondIdentity = targetReportsFirst ? sourceIdentity : target;
        var first = harness.Machine.BeginUnexpectedLoss(
            firstIdentity,
            harness.Machine.FrozenBindingProof);
        var second = harness.Machine.BeginUnexpectedLoss(
            secondIdentity,
            harness.Machine.FrozenBindingProof);

        Assert.Equal(SessionRecoveryLossDisposition.RecoveryUnknown,
            first.Disposition);
        Assert.Equal(SessionRecoveryLossDisposition.RecoveryUnknown,
            second.Disposition);
        var firstLoss = Assert.IsType<SessionRecoveryLossLease>(first.Loss);
        var secondLoss = Assert.IsType<SessionRecoveryLossLease>(second.Loss);
        Assert.Equal(SessionRecoveryLossDisposition.Duplicate,
            harness.Machine.BeginUnexpectedLoss(
                secondIdentity,
                harness.Machine.FrozenBindingProof).Disposition);
        Assert.True(harness.Machine.Snapshot().AwaitingOldTreeDeath);

        var targetLoss = targetReportsFirst ? firstLoss : secondLoss;
        var sourceLoss = targetReportsFirst ? secondLoss : firstLoss;
        var firstConfirmation = targetConfirmsFirst ? targetLoss : sourceLoss;
        var secondConfirmation = targetConfirmsFirst ? sourceLoss : targetLoss;
        Assert.Equal(SessionRecoveryDeathDisposition.NoRecovery,
            harness.Machine.ConfirmOldTreeDeath(firstConfirmation).Disposition);
        Assert.True(harness.Machine.Snapshot().AwaitingOldTreeDeath);
        Assert.NotNull(harness.Machine.Snapshot().ActiveTransition);
        Assert.Equal(SessionRecoveryDeathDisposition.NoRecovery,
            harness.Machine.ConfirmOldTreeDeath(secondConfirmation).Disposition);

        var settled = harness.Machine.Snapshot();
        Assert.Equal(PublicSessionState.RecoveryUnknown, settled.Session.State);
        Assert.False(settled.AwaitingOldTreeDeath);
        Assert.Null(settled.ActiveTransition);
        Assert.Equal(1, harness.Factory.DisposeCount);
        Assert.Equal(SessionRecoveryDeathDisposition.Duplicate,
            harness.Machine.ConfirmOldTreeDeath(firstConfirmation).Disposition);
    }

    [Fact]
    public void Dispatched_open_target_death_clears_transition_and_allows_explicit_repair()
    {
        var harness = Harness.CreateTemplate();
        var open = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Open));
        var target = WorkerIdentity(2, 1982);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(open, target));
        Assert.True(harness.Machine.MarkLifecycleDispatched(open));

        var loss = harness.Machine.BeginUnexpectedLoss(
            target,
            harness.Machine.FrozenBindingProof);
        Assert.Equal(SessionRecoveryLossDisposition.RecoveryUnknown,
            loss.Disposition);
        Assert.Equal(SessionRecoveryDeathDisposition.NoRecovery,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).Disposition);

        var settled = harness.Machine.Snapshot();
        Assert.Equal(PublicSessionState.RecoveryUnknown, settled.Session.State);
        Assert.False(settled.AwaitingOldTreeDeath);
        Assert.Null(settled.ActiveTransition);
        Assert.NotNull(harness.Machine.BeginLifecycleTransition(
            SessionRecoveryTransitionKind.Restart,
            harness.Machine.FrozenBindingProof));
    }

    [Fact]
    public void Abandoned_cold_open_preserves_state_fences_stale_leases_and_consumes_generation()
    {
        var harness = Harness.CreateTemplate();
        var abandoned = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Open));
        var consumedIdentity = WorkerIdentity(2, 202);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            abandoned,
            consumedIdentity));

        Assert.True(harness.Machine.AbandonUndispatchedLifecycleTransition(abandoned));
        Assert.False(harness.Machine.AbandonUndispatchedLifecycleTransition(abandoned));
        Assert.False(harness.Machine.MarkLifecycleDispatched(abandoned));
        Assert.Equal(PublicSessionState.Cold,
            harness.Machine.Snapshot().Session.State);

        var replacement = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Open));
        Assert.False(harness.Machine.AttachLifecycleWorkerIdentity(
            replacement,
            consumedIdentity));
        Assert.False(harness.Machine.CompleteLifecycleTerminal(
            replacement,
            PublicSessionState.Ready,
            consumedIdentity,
            new SessionTransitionVersion(2),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: false));

        var freshIdentity = WorkerIdentity(3, 203);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            replacement,
            freshIdentity));
        Assert.True(harness.Machine.MarkLifecycleDispatched(replacement));
        Assert.False(harness.Machine.AbandonUndispatchedLifecycleTransition(replacement));
        Assert.True(harness.Machine.CompleteLifecycleTerminal(
            replacement,
            PublicSessionState.Ready,
            freshIdentity,
            new SessionTransitionVersion(2),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: false));
        Assert.False(harness.Machine.AbandonUndispatchedLifecycleTransition(replacement));
        Assert.Equal(freshIdentity.Generation,
            harness.Machine.Snapshot().Session.Generation);
    }

    [Fact]
    public void Abandoned_ready_restart_preserves_old_worker_and_dispatched_restart_cannot_be_abandoned()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var abandoned = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Restart));
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            abandoned,
            WorkerIdentity(2, 212)));

        Assert.True(harness.Machine.AbandonUndispatchedLifecycleTransition(abandoned));
        var unchanged = harness.Machine.Snapshot().Session;
        Assert.Equal(PublicSessionState.Ready, unchanged.State);
        Assert.Equal(harness.OldIdentity.BootId, unchanged.WorkerBootId);

        var dispatched = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Restart));
        var replacement = WorkerIdentity(3, 213);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            dispatched,
            replacement));
        Assert.True(harness.Machine.MarkLifecycleDispatched(dispatched));
        Assert.False(harness.Machine.AbandonUndispatchedLifecycleTransition(dispatched));
        Assert.Null(harness.Machine.BeginLifecycleTransition(
            SessionRecoveryTransitionKind.Reset));
        Assert.True(harness.Machine.CompleteLifecycleTerminal(
            dispatched,
            PublicSessionState.Ready,
            replacement,
            new SessionTransitionVersion(2),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: true));
        Assert.False(harness.Machine.AbandonUndispatchedLifecycleTransition(dispatched));
        Assert.False(harness.Machine.AbandonUndispatchedLifecycleTransition(abandoned));
    }

    [Fact]
    public async Task Dispatch_and_abandon_race_has_exactly_one_winner()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var transition = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Restart));
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            transition,
            WorkerIdentity(2, 2131)));

        using var barrier = new Barrier(3);
        var dispatch = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return harness.Machine.MarkLifecycleDispatched(transition);
        });
        var abandon = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return harness.Machine.AbandonUndispatchedLifecycleTransition(transition);
        });
        barrier.SignalAndWait();

        var outcomes = await Task.WhenAll(dispatch, abandon);
        var dispatchWon = outcomes[0];
        var abandonWon = outcomes[1];

        Assert.True(dispatchWon ^ abandonWon);
        var snapshot = harness.Machine.Snapshot();
        if (dispatchWon)
        {
            Assert.Equal(PublicSessionState.Resetting, snapshot.Session.State);
            Assert.Equal(SessionRecoveryTransitionKind.Restart,
                snapshot.ActiveTransition);
            Assert.True(snapshot.ActiveTransitionDispatched);
        }
        else
        {
            Assert.Equal(PublicSessionState.Ready, snapshot.Session.State);
            Assert.Null(snapshot.ActiveTransition);
            Assert.False(snapshot.ActiveTransitionDispatched);
        }
    }

    [Theory]
    [InlineData((int)SessionRecoveryTransitionKind.Open)]
    [InlineData((int)SessionRecoveryTransitionKind.Restart)]
    public void Undispatched_transition_can_be_abandoned_before_identity_attachment(
        int kindValue)
    {
        var kind = (SessionRecoveryTransitionKind)kindValue;
        var harness = Harness.CreateTemplate();
        if (kind == SessionRecoveryTransitionKind.Restart)
            harness.PrimeReady();
        var transition = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(kind));

        Assert.False(harness.Machine.MarkLifecycleDispatched(transition));
        Assert.True(harness.Machine.AbandonUndispatchedLifecycleTransition(
            transition));
        Assert.NotNull(harness.Machine.BeginLifecycleTransition(kind));
    }

    [Theory]
    [MemberData(nameof(AmbiguousTransitions))]
    public void Undispatched_lifecycle_terminal_is_rejected_for_every_kind(
        int kindValue)
    {
        var kind = (SessionRecoveryTransitionKind)kindValue;
        var harness = Harness.CreateTemplate();
        if (kind != SessionRecoveryTransitionKind.Open)
            harness.PrimeReady();
        var transition = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(kind));
        GuardianHostWorkerIdentity? observedIdentity = harness.OldIdentity;
        var observedState = PublicSessionState.Ready;
        var oldWorkerDeathConfirmed = false;
        if (kind is SessionRecoveryTransitionKind.Open or
            SessionRecoveryTransitionKind.Restart or
            SessionRecoveryTransitionKind.Reset)
        {
            observedIdentity = WorkerIdentity(2, 214 + kindValue);
            Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
                transition,
                observedIdentity));
        }
        if (kind == SessionRecoveryTransitionKind.Close)
        {
            observedIdentity = null;
            observedState = PublicSessionState.Cold;
            oldWorkerDeathConfirmed = true;
        }
        else if (kind is SessionRecoveryTransitionKind.Restart or
            SessionRecoveryTransitionKind.Reset)
        {
            oldWorkerDeathConfirmed = true;
        }

        Assert.False(harness.Machine.CompleteLifecycleTerminal(
            transition,
            observedState,
            observedIdentity,
            transition.ExpectedTransitionVersion,
            observedState == PublicSessionState.Cold
                ? BootstrapState.Pending
                : BootstrapState.Restored,
            oldWorkerDeathConfirmed));
        Assert.True(harness.Machine.AbandonUndispatchedLifecycleTransition(
            transition));
        Assert.Equal(
            kind == SessionRecoveryTransitionKind.Open
                ? PublicSessionState.Cold
                : PublicSessionState.Ready,
            harness.Machine.Snapshot().Session.State);
    }

    [Theory]
    [MemberData(nameof(AmbiguousTransitions))]
    public void Dispatched_lifecycle_never_advertises_ready_for_effects(
        int kindValue)
    {
        var kind = (SessionRecoveryTransitionKind)kindValue;
        var harness = Harness.CreateTemplate();
        if (kind != SessionRecoveryTransitionKind.Open)
            harness.PrimeReady();
        var transition = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(kind));
        if (kind is SessionRecoveryTransitionKind.Open or
            SessionRecoveryTransitionKind.Restart or
            SessionRecoveryTransitionKind.Reset)
        {
            Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
                transition,
                WorkerIdentity(2, 224 + kindValue)));
        }

        Assert.True(harness.Machine.MarkLifecycleDispatched(transition));

        var snapshot = harness.Machine.Snapshot().Session;
        Assert.False(snapshot.ReadyForEffects);
        Assert.NotEqual(PublicSessionState.Ready, snapshot.State);
    }

    [Fact]
    public void Lifecycle_terminal_requires_exact_lease_transition_version()
    {
        var harness = Harness.CreateTemplate();
        var transition = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Open));
        var identity = WorkerIdentity(2, 229);
        Assert.Equal(new SessionTransitionVersion(2),
            transition.ExpectedTransitionVersion);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            transition,
            identity));
        Assert.True(harness.Machine.MarkLifecycleDispatched(transition));

        Assert.False(harness.Machine.CompleteLifecycleTerminal(
            transition,
            PublicSessionState.Ready,
            identity,
            new SessionTransitionVersion(3),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: false));
        Assert.False(harness.Machine.CompleteLifecycleTerminal(
            transition,
            PublicSessionState.Ready,
            identity,
            new SessionTransitionVersion(1),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: false));
        Assert.True(harness.Machine.CompleteLifecycleTerminal(
            transition,
            PublicSessionState.Ready,
            identity,
            new SessionTransitionVersion(2),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: false));
        Assert.Equal(new SessionTransitionVersion(2),
            harness.Machine.Snapshot().Session.TransitionVersion);
    }

    [Theory]
    [InlineData((int)SessionRecoveryTransitionKind.Open)]
    [InlineData((int)SessionRecoveryTransitionKind.Bootstrap)]
    public void Successful_open_or_bootstrap_rejects_contradictory_death_confirmation(
        int kindValue)
    {
        var kind = (SessionRecoveryTransitionKind)kindValue;
        var harness = Harness.CreateTemplate();
        if (kind == SessionRecoveryTransitionKind.Bootstrap)
            harness.PrimeReady();
        var transition = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(kind));
        var identity = harness.OldIdentity;
        if (kind == SessionRecoveryTransitionKind.Open)
        {
            identity = WorkerIdentity(2, 2291);
            Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
                transition,
                identity));
        }
        Assert.True(harness.Machine.MarkLifecycleDispatched(transition));

        Assert.False(harness.Machine.CompleteLifecycleTerminal(
            transition,
            PublicSessionState.Ready,
            identity,
            transition.ExpectedTransitionVersion,
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: true));
        Assert.True(harness.Machine.CompleteLifecycleTerminal(
            transition,
            PublicSessionState.Ready,
            identity,
            transition.ExpectedTransitionVersion,
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: false));
    }

    [Fact]
    public void Exhausted_transition_version_refuses_a_new_lifecycle_lease()
    {
        var harness = Harness.CreateTemplate(transitionVersion: long.MaxValue);

        Assert.Null(harness.Machine.BeginLifecycleTransition(
            SessionRecoveryTransitionKind.Open));
        Assert.Equal(PublicSessionState.Cold,
            harness.Machine.Snapshot().Session.State);
    }

    [Fact]
    public void Recovery_attempt_context_uses_effective_opened_state_and_version()
    {
        var harness = Harness.CreateTemplate(
            desiredState: DesiredSessionState.Cold);
        var open = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Open));
        var openedIdentity = WorkerIdentity(2, 239);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            open,
            openedIdentity));
        Assert.True(harness.Machine.MarkLifecycleDispatched(open));
        Assert.True(harness.Machine.CompleteLifecycleTerminal(
            open,
            PublicSessionState.Ready,
            openedIdentity,
            new SessionTransitionVersion(2),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: false));
        harness.Generations.SetHighWatermark(2);

        var loss = harness.Machine.BeginUnexpectedLoss(
            openedIdentity,
            harness.Machine.FrozenBindingProof);
        var attempt = Assert.IsType<SessionRecoveryAttemptLease>(
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).Attempt);
        var context = Assert.Single(harness.Factory.Contexts);

        Assert.Same(context, attempt.Context);
        Assert.Equal(DesiredSessionState.Ready, context.DesiredState);
        Assert.Equal(new SessionTransitionVersion(2), context.TransitionVersion);
        Assert.Equal(DesiredSessionState.Cold,
            context.FrozenBinding.DesiredState);
        Assert.Equal(new SessionTransitionVersion(1),
            context.FrozenBinding.TransitionVersion);
        harness.Machine.Shutdown();
    }

    [Fact]
    public void Acknowledged_lifecycle_terminal_clears_delivery_ambiguity()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var transition = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Bootstrap));
        Assert.False(harness.Machine.AttachLifecycleWorkerIdentity(
            transition,
            WorkerIdentity(2, 222)));
        Assert.True(harness.Machine.MarkLifecycleDispatched(transition));
        Assert.True(harness.Machine.CompleteLifecycleTerminal(
            transition,
            PublicSessionState.Ready,
            harness.OldIdentity,
            new SessionTransitionVersion(2),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: false));
        Assert.False(harness.Machine.CompleteLifecycleTerminal(
            transition,
            PublicSessionState.Ready,
            harness.OldIdentity,
            new SessionTransitionVersion(3),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: false));

        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);

        Assert.Equal(SessionRecoveryLossDisposition.BeganContainment, loss.Disposition);
    }

    [Theory]
    [InlineData((int)SessionRecoveryTransitionKind.Restart)]
    [InlineData((int)SessionRecoveryTransitionKind.Reset)]
    public void Restart_or_reset_ready_requires_attached_new_identity_and_confirmed_old_death(
        int kindValue)
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var transition = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                (SessionRecoveryTransitionKind)kindValue));
        var replacement = WorkerIdentity(2, 232 + kindValue);

        Assert.False(harness.Machine.AttachLifecycleWorkerIdentity(
            transition,
            harness.OldIdentity));
        Assert.False(harness.Machine.CompleteLifecycleTerminal(
            transition,
            PublicSessionState.Ready,
            replacement,
            new SessionTransitionVersion(2),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: true));
        Assert.False(harness.Machine.CompleteLifecycleTerminal(
            transition,
            PublicSessionState.Ready,
            harness.OldIdentity,
            new SessionTransitionVersion(2),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: true));

        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            transition,
            replacement));
        Assert.True(harness.Machine.MarkLifecycleDispatched(transition));
        Assert.False(harness.Machine.CompleteLifecycleTerminal(
            transition,
            PublicSessionState.Ready,
            replacement,
            new SessionTransitionVersion(2),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: false));
        Assert.False(harness.Machine.CompleteLifecycleTerminal(
            transition,
            PublicSessionState.Ready,
            harness.OldIdentity,
            new SessionTransitionVersion(2),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: true));
        Assert.True(harness.Machine.CompleteLifecycleTerminal(
            transition,
            PublicSessionState.Ready,
            replacement,
            new SessionTransitionVersion(2),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: true));
    }

    public static TheoryData<string> MismatchedProofFields =>
        new() { "catalog", "binding", "template", "bootstrap", "kind" };

    [Theory]
    [MemberData(nameof(MismatchedProofFields))]
    public void Frozen_binding_or_digest_mismatch_faults_without_recovery(string field)
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var valid = harness.Machine.FrozenBindingProof;
        var mismatch = field switch
        {
            "catalog" => new SessionRecoveryBindingProof(
                Digest("other-catalog"), valid.BindingKind, valid.BindingDigest,
                valid.TemplateDigest, valid.BootstrapDigest),
            "binding" => new SessionRecoveryBindingProof(
                valid.CatalogDigest, valid.BindingKind, Digest("other-binding"),
                valid.TemplateDigest, valid.BootstrapDigest),
            "template" => new SessionRecoveryBindingProof(
                valid.CatalogDigest, valid.BindingKind, valid.BindingDigest,
                Digest("other-template"), valid.BootstrapDigest),
            "bootstrap" => new SessionRecoveryBindingProof(
                valid.CatalogDigest, valid.BindingKind, valid.BindingDigest,
                valid.TemplateDigest, Digest("other-bootstrap")),
            "kind" => new SessionRecoveryBindingProof(
                valid.CatalogDigest, RecoveryBindingKind.Dynamic,
                valid.BindingDigest, null, null),
            _ => throw new InvalidOperationException(),
        };

        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            mismatch);

        Assert.Equal(SessionRecoveryLossDisposition.Faulted, loss.Disposition);
        Assert.Equal(
            SessionRecoveryDeathDisposition.Faulted,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).Disposition);
        Assert.Empty(harness.Generations.Allocations);
        var snapshot = harness.Machine.Snapshot().Session;
        Assert.Equal(PublicSessionState.Faulted, snapshot.State);
        Assert.Equal(BootstrapState.Failed, snapshot.BootstrapState);
        Assert.Equal(
            SessionRecoveryFailureReason.BindingMismatch,
            harness.Machine.Snapshot().LastFailureReason);
    }

    [Fact]
    public void Binding_fault_manual_repair_requires_exact_frozen_proof()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var valid = harness.Machine.FrozenBindingProof;
        var mismatched = new SessionRecoveryBindingProof(
            Digest("wrong-catalog"),
            valid.BindingKind,
            valid.BindingDigest,
            valid.TemplateDigest,
            valid.BootstrapDigest);
        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            mismatched);
        Assert.Equal(SessionRecoveryLossDisposition.Faulted, loss.Disposition);
        Assert.Equal(SessionRecoveryDeathDisposition.Faulted,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).Disposition);

        Assert.Null(harness.Machine.BeginLifecycleTransition(
            SessionRecoveryTransitionKind.Restart));
        Assert.Null(harness.Machine.BeginLifecycleTransition(
            SessionRecoveryTransitionKind.Restart,
            mismatched));
        var repair = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Restart,
                valid));
        var replacement = WorkerIdentity(2, 245);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            repair,
            replacement));
        Assert.True(harness.Machine.MarkLifecycleDispatched(repair));
        Assert.True(harness.Machine.CompleteLifecycleTerminal(
            repair,
            PublicSessionState.Ready,
            replacement,
            repair.ExpectedTransitionVersion,
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: true));
        Assert.True(harness.Machine.Snapshot().Session.ReadyForEffects);
    }

    [Fact]
    public void Dispatched_lifecycle_ambiguity_outranks_binding_mismatch()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var restart = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Restart));
        var target = WorkerIdentity(2, 241);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(restart, target));
        Assert.True(harness.Machine.MarkLifecycleDispatched(restart));
        var valid = harness.Machine.FrozenBindingProof;
        var mismatched = new SessionRecoveryBindingProof(
            Digest("wrong-catalog"),
            valid.BindingKind,
            valid.BindingDigest,
            valid.TemplateDigest,
            valid.BootstrapDigest);

        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            mismatched);

        Assert.Equal(SessionRecoveryLossDisposition.RecoveryUnknown,
            loss.Disposition);
        Assert.Equal(SessionRecoveryDeathDisposition.NoRecovery,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).Disposition);
        Assert.Equal(PublicRecoveryDetailCode.SessionRecoveryUnknown,
            harness.Machine.Snapshot().Session.LastFailureCode);
        var targetLoss = harness.Machine.BeginUnexpectedLoss(
            target,
            mismatched);
        Assert.Equal(SessionRecoveryLossDisposition.RecoveryUnknown,
            targetLoss.Disposition);
        Assert.Equal(SessionRecoveryDeathDisposition.NoRecovery,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(targetLoss.Loss)).Disposition);
    }

    [Fact]
    public async Task Inflight_bootstrap_ambiguity_outranks_binding_mismatch()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        harness.Factory.Baseline = (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return SessionRecoveryBaselineResult.Ready;
        };
        var attempt = BeginFirstAttempt(harness);
        var execution = Task.Run(() => harness.Machine.ExecuteBaseline(attempt));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var valid = harness.Machine.FrozenBindingProof;
        var mismatched = new SessionRecoveryBindingProof(
            valid.CatalogDigest,
            valid.BindingKind,
            Digest("wrong-binding"),
            valid.TemplateDigest,
            valid.BootstrapDigest);

        var loss = harness.Machine.BeginUnexpectedLoss(
            attempt.WorkerIdentity,
            mismatched);
        Assert.Equal(SessionRecoveryLossDisposition.RecoveryUnknown,
            loss.Disposition);
        release.Set();
        Assert.Equal(SessionRecoveryAttemptDisposition.StaleAttempt,
            (await execution.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        Assert.Equal(SessionRecoveryDeathDisposition.NoRecovery,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).Disposition);
        Assert.Equal(PublicRecoveryDetailCode.SessionRecoveryUnknown,
            harness.Machine.Snapshot().Session.LastFailureCode);
    }

    [Fact]
    public void Deterministic_bootstrap_failure_faults_once_without_retry()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        harness.Factory.Baseline = (_, _) =>
            SessionRecoveryBaselineResult.DeterministicConfigurationFailure;
        var attempt = BeginFirstAttempt(harness);

        var failure = harness.Machine.ExecuteBaseline(attempt);
        Assert.Equal(SessionRecoveryAttemptDisposition.Faulted,
            failure.Disposition);
        Assert.Equal(SessionRecoveryDeathDisposition.Faulted,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(failure.Loss)).Disposition);
        harness.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(
            SessionRecoveryAttemptStartDisposition.Faulted,
            harness.Machine.TryStartDueAttempt().Disposition);
        Assert.Equal(1, harness.Factory.CallbackCount);
        var snapshot = harness.Machine.Snapshot().Session;
        Assert.Equal(PublicSessionState.Faulted, snapshot.State);
        Assert.Equal(BootstrapState.Failed, snapshot.BootstrapState);
        Assert.Equal(
            PublicRecoveryDetailCode.SessionBootstrapFailed,
            snapshot.LastFailureCode);
    }

    [Fact]
    public void Retryable_attempt_failure_cannot_advance_before_confirmed_tree_death()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        harness.Factory.Baseline = (_, _) =>
            SessionRecoveryBaselineResult.RetryableFailure;
        var failure = harness.Machine.ExecuteBaseline(BeginFirstAttempt(harness));

        Assert.Equal(SessionRecoveryAttemptDisposition.ContainmentRequired,
            failure.Disposition);
        Assert.True(harness.Machine.Snapshot().AwaitingOldTreeDeath);
        Assert.Equal(2, harness.Machine.Snapshot().Session.RecoveryAttempt);
        Assert.Equal(1, harness.Factory.CallbackCount);
        Assert.Single(harness.Generations.Allocations);
        harness.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(
            SessionRecoveryAttemptStartDisposition.NotDue,
            harness.Machine.TryStartDueAttempt().Disposition);
        Assert.Single(harness.Generations.Allocations);

        var loss = Assert.IsType<SessionRecoveryLossLease>(failure.Loss);
        Assert.Equal(SessionRecoveryDeathDisposition.Scheduled,
            harness.Machine.ConfirmOldTreeDeath(loss).Disposition);
        Assert.False(harness.Machine.Snapshot().AwaitingOldTreeDeath);
        Assert.Equal(
            SessionRecoveryAttemptStartDisposition.NotDue,
            harness.Machine.TryStartDueAttempt().Disposition);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(
            SessionRecoveryAttemptStartDisposition.AttemptStarted,
            harness.Machine.TryStartDueAttempt().Disposition);
        harness.Machine.Shutdown();
    }

    [Fact]
    public void Retryable_failures_use_exact_backoffs_six_failure_circuit_and_one_half_open()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        harness.Factory.Baseline = (_, _) =>
            SessionRecoveryBaselineResult.RetryableFailure;
        var attempt = BeginFirstAttempt(harness);
        Assert.Equal(
            SessionRecoveryAttemptDisposition.ContainmentRequired,
            FailAndConfirm(harness, attempt).Disposition);

        for (var index = 0; index < ExactBackoffs.Length; index++)
        {
            var delay = ExactBackoffs[index];
            var snapshot = harness.Machine.Snapshot();
            Assert.Equal(PublicSessionState.Backoff, snapshot.Session.State);
            Assert.Equal(RecoveryPhase.Backoff, snapshot.Session.RecoveryPhase);
            Assert.Equal(checked((int)delay.TotalMilliseconds),
                snapshot.Session.RetryAfterMilliseconds);
            Assert.False(snapshot.Session.ReadyForEffects);

            harness.Clock.Advance(delay - TimeSpan.FromTicks(1));
            Assert.Equal(
                SessionRecoveryAttemptStartDisposition.NotDue,
                harness.Machine.TryStartDueAttempt().Disposition);
            harness.Clock.Advance(TimeSpan.FromTicks(1));
            var due = harness.Machine.TryStartDueAttempt();
            Assert.Equal(SessionRecoveryAttemptStartDisposition.AttemptStarted,
                due.Disposition);
            attempt = Assert.IsType<SessionRecoveryAttemptLease>(due.Attempt);
            Assert.Equal(index + 2, attempt.RecoveryLease.AttemptOrdinal);
            Assert.False(attempt.RecoveryLease.IsHalfOpen);
            Assert.Equal(
                SessionRecoveryAttemptStartDisposition.NotDue,
                harness.Machine.TryStartDueAttempt().Disposition);
            Assert.Equal(
                SessionRecoveryAttemptDisposition.ContainmentRequired,
                FailAndConfirm(harness, attempt).Disposition);
        }

        var open = harness.Machine.Snapshot();
        Assert.Equal(PublicSessionState.CircuitOpen, open.Session.State);
        Assert.Equal(RecoveryCircuitState.CircuitOpen, open.CircuitState);
        Assert.Equal(6, open.ConsecutiveFailedGenerations);
        Assert.Equal(60_000, open.Session.RetryAfterMilliseconds);

        harness.Clock.Advance(TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1));
        Assert.Equal(
            SessionRecoveryAttemptStartDisposition.NotDue,
            harness.Machine.TryStartDueAttempt().Disposition);
        harness.Clock.Advance(TimeSpan.FromTicks(1));
        var halfOpenStart = harness.Machine.TryStartDueAttempt();
        Assert.Equal(SessionRecoveryAttemptStartDisposition.AttemptStarted,
            halfOpenStart.Disposition);
        var halfOpen = Assert.IsType<SessionRecoveryAttemptLease>(
            halfOpenStart.Attempt);
        Assert.True(halfOpen.RecoveryLease.IsHalfOpen);
        Assert.Equal(
            SessionRecoveryAttemptStartDisposition.NotDue,
            harness.Machine.TryStartDueAttempt().Disposition);
        Assert.Equal(PublicSessionState.HalfOpen,
            harness.Machine.Snapshot().Session.State);
        Assert.Equal(
            SessionRecoveryAttemptDisposition.ContainmentRequired,
            FailAndConfirm(harness, halfOpen).Disposition);
        Assert.Equal(PublicSessionState.CircuitOpen,
            harness.Machine.Snapshot().Session.State);

        Assert.Equal(
            Enumerable.Range(2, 7).Select(value => (long)value),
            harness.Generations.Allocations.Select(value => value.Value));
    }

    [Fact]
    public void Sixty_seconds_ready_resets_history_and_next_confirmed_loss_is_attempt_one()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var first = BeginFirstAttempt(harness);
        Assert.Equal(SessionRecoveryAttemptDisposition.Ready,
            harness.Machine.ExecuteBaseline(first).Disposition);

        harness.Clock.Advance(TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1));
        Assert.False(harness.Machine.TryCompleteReadyStability());
        harness.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(harness.Machine.TryCompleteReadyStability());
        Assert.False(harness.Machine.TryCompleteReadyStability());

        var loss = harness.Machine.BeginUnexpectedLoss(
            first.WorkerIdentity,
            harness.Machine.FrozenBindingProof);
        Assert.Equal(1, harness.Machine.Snapshot().Session.RecoveryAttempt);
        var replacement = Assert.IsType<SessionRecoveryAttemptLease>(
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).Attempt);

        Assert.Equal(1, replacement.RecoveryLease.AttemptOrdinal);
        Assert.Equal(3, replacement.WorkerIdentity.Generation.Value);
        Assert.Equal(0, harness.Machine.Snapshot().ConsecutiveFailedGenerations);
    }

    [Fact]
    public void Pre_stability_ready_loss_counts_the_generation_and_waits_for_backoff()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var first = BeginFirstAttempt(harness);
        Assert.Equal(SessionRecoveryAttemptDisposition.Ready,
            harness.Machine.ExecuteBaseline(first).Disposition);

        var loss = harness.Machine.BeginUnexpectedLoss(
            first.WorkerIdentity,
            harness.Machine.FrozenBindingProof);
        Assert.Equal(2, harness.Machine.Snapshot().Session.RecoveryAttempt);
        var death = harness.Machine.ConfirmOldTreeDeath(
            Assert.IsType<SessionRecoveryLossLease>(loss.Loss));

        Assert.Equal(SessionRecoveryDeathDisposition.Scheduled, death.Disposition);
        Assert.Null(death.Attempt);
        var snapshot = harness.Machine.Snapshot();
        Assert.Equal(PublicSessionState.Backoff, snapshot.Session.State);
        Assert.Equal(1, snapshot.ConsecutiveFailedGenerations);
        Assert.Equal(250, snapshot.Session.RetryAfterMilliseconds);
        Assert.Equal(2, Assert.Single(harness.Generations.Allocations).Value);
    }

    [Fact]
    public void Close_after_automatic_recovery_disposes_old_ownership_and_stays_cold()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var recovered = BeginFirstAttempt(harness);
        Assert.Equal(SessionRecoveryAttemptDisposition.Ready,
            harness.Machine.ExecuteBaseline(recovered).Disposition);
        var close = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Close));
        Assert.True(harness.Machine.MarkLifecycleDispatched(close));

        Assert.True(harness.Machine.CompleteLifecycleTerminal(
            close,
            PublicSessionState.Cold,
            null,
            new SessionTransitionVersion(2),
            BootstrapState.Pending,
            oldWorkerDeathConfirmed: true));

        Assert.Equal(1, harness.Factory.DisposeCount);
        var snapshot = harness.Machine.Snapshot().Session;
        Assert.Equal(PublicSessionState.Cold, snapshot.State);
        Assert.Equal(DesiredSessionState.Cold, snapshot.DesiredState);
        Assert.Null(snapshot.WorkerBootId);
        Assert.Equal(SessionRecoveryLossDisposition.NotEligible,
            harness.Machine.BeginUnexpectedLoss(
                recovered.WorkerIdentity,
                harness.Machine.FrozenBindingProof).Disposition);
    }

    [Theory]
    [InlineData((int)SessionRecoveryTransitionKind.Restart)]
    [InlineData((int)SessionRecoveryTransitionKind.Reset)]
    public void Restart_or_reset_after_automatic_recovery_hands_off_ownership_atomically(
        int kindValue)
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var recovered = BeginFirstAttempt(harness);
        Assert.Equal(SessionRecoveryAttemptDisposition.Ready,
            harness.Machine.ExecuteBaseline(recovered).Disposition);
        var transition = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                (SessionRecoveryTransitionKind)kindValue));
        var replacement = WorkerIdentity(3, 303);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            transition,
            replacement));
        harness.Generations.SetHighWatermark(3);
        Assert.True(harness.Machine.MarkLifecycleDispatched(transition));

        Assert.True(harness.Machine.CompleteLifecycleTerminal(
            transition,
            PublicSessionState.Ready,
            replacement,
            new SessionTransitionVersion(2),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: true));

        Assert.Equal(1, harness.Factory.DisposeCount);
        var ready = harness.Machine.Snapshot().Session;
        Assert.Equal(replacement.BootId, ready.WorkerBootId);
        Assert.Equal(replacement.Generation, ready.Generation);
        Assert.Equal(new SessionTransitionVersion(2), ready.TransitionVersion);
        Assert.True(ready.ReadyForEffects);

        var loss = harness.Machine.BeginUnexpectedLoss(
            replacement,
            harness.Machine.FrozenBindingProof);
        var next = Assert.IsType<SessionRecoveryAttemptLease>(
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).Attempt);
        Assert.Equal(4, next.WorkerIdentity.Generation.Value);
        harness.Machine.Shutdown();
    }

    [Fact]
    public async Task Lifecycle_terminal_publishes_new_state_before_tracked_resource_cleanup()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var recovered = BeginFirstAttempt(harness);
        Assert.Equal(SessionRecoveryAttemptDisposition.Ready,
            harness.Machine.ExecuteBaseline(recovered).Disposition);
        var restart = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Restart));
        var replacement = WorkerIdentity(3, 313);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            restart,
            replacement));
        Assert.True(harness.Machine.MarkLifecycleDispatched(restart));
        using var disposeEntered = new ManualResetEventSlim();
        using var disposeRelease = new ManualResetEventSlim();
        harness.Factory.Disposing = _ =>
        {
            disposeEntered.Set();
            disposeRelease.Wait(TimeSpan.FromSeconds(10));
        };

        var completion = Task.Run(() => harness.Machine.CompleteLifecycleTerminal(
            restart,
            PublicSessionState.Ready,
            replacement,
            new SessionTransitionVersion(2),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: true));
        Assert.True(disposeEntered.Wait(TimeSpan.FromSeconds(10)));

        var duringCleanup = harness.Machine.Snapshot().Session;
        Assert.Equal(PublicSessionState.Ready, duringCleanup.State);
        Assert.True(duringCleanup.ReadyForEffects);
        Assert.Equal(replacement.BootId, duringCleanup.WorkerBootId);
        Assert.Equal(new SessionTransitionVersion(2), duringCleanup.TransitionVersion);
        Assert.Null(harness.Machine.BeginLifecycleTransition(
            SessionRecoveryTransitionKind.Reset));
        Assert.Equal(SessionRecoveryLossDisposition.StaleWorker,
            harness.Machine.BeginUnexpectedLoss(
                recovered.WorkerIdentity,
                harness.Machine.FrozenBindingProof).Disposition);

        disposeRelease.Set();
        Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, harness.Factory.DisposeCount);
    }

    [Fact]
    public async Task Confirmed_death_resource_cleanup_is_joined_by_concurrent_shutdown()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var recovered = BeginFirstAttempt(harness);
        Assert.Equal(SessionRecoveryAttemptDisposition.Ready,
            harness.Machine.ExecuteBaseline(recovered).Disposition);
        var loss = harness.Machine.BeginUnexpectedLoss(
            recovered.WorkerIdentity,
            harness.Machine.FrozenBindingProof);
        using var disposeEntered = new ManualResetEventSlim();
        using var disposeRelease = new ManualResetEventSlim();
        harness.Factory.Disposing = _ =>
        {
            disposeEntered.Set();
            disposeRelease.Wait(TimeSpan.FromSeconds(10));
        };

        var confirmation = Task.Run(() =>
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)));
        Assert.True(disposeEntered.Wait(TimeSpan.FromSeconds(10)));
        var duringHandoff = harness.Machine.Snapshot().Session;
        Assert.Equal(PublicSessionState.Recovering, duringHandoff.State);
        Assert.Equal(RecoveryPhase.Containment, duringHandoff.RecoveryPhase);
        Assert.Equal(2, duringHandoff.RecoveryAttempt);
        Assert.Equal(ContractLimits.MinimumRetryAfterMilliseconds,
            duringHandoff.RetryAfterMilliseconds);
        var shutdown = harness.Machine.ShutdownAsync().AsTask();

        await Task.Delay(20);
        Assert.False(shutdown.IsCompleted);
        Assert.True(harness.Machine.Snapshot().TerminalShutdown);
        disposeRelease.Set();
        Assert.Equal(SessionRecoveryDeathDisposition.Stopped,
            (await confirmation.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, harness.Factory.DisposeCount);
    }

    [Fact]
    public async Task Lifecycle_cleanup_failure_faults_and_remains_shutdown_visible()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var recovered = BeginFirstAttempt(harness);
        Assert.Equal(SessionRecoveryAttemptDisposition.Ready,
            harness.Machine.ExecuteBaseline(recovered).Disposition);
        var restart = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Restart));
        var replacement = WorkerIdentity(3, 318);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            restart,
            replacement));
        Assert.True(harness.Machine.MarkLifecycleDispatched(restart));
        harness.Factory.Disposing = _ =>
            throw new InvalidOperationException("injected dispose failure");

        Assert.False(harness.Machine.CompleteLifecycleTerminal(
            restart,
            PublicSessionState.Ready,
            replacement,
            restart.ExpectedTransitionVersion,
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: true));

        var snapshot = harness.Machine.Snapshot().Session;
        Assert.Equal(PublicSessionState.Faulted, snapshot.State);
        Assert.False(snapshot.ReadyForEffects);
        Assert.Null(harness.Machine.BeginLifecycleTransition(
            SessionRecoveryTransitionKind.Restart,
            harness.Machine.FrozenBindingProof));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Machine.ShutdownAsync().AsTask());
    }

    [Fact]
    public async Task Confirmed_death_cleanup_failure_faults_and_remains_shutdown_visible()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var recovered = BeginFirstAttempt(harness);
        Assert.Equal(SessionRecoveryAttemptDisposition.Ready,
            harness.Machine.ExecuteBaseline(recovered).Disposition);
        var loss = harness.Machine.BeginUnexpectedLoss(
            recovered.WorkerIdentity,
            harness.Machine.FrozenBindingProof);
        harness.Factory.Disposing = _ =>
            throw new InvalidOperationException("injected dispose failure");

        Assert.Equal(SessionRecoveryDeathDisposition.Faulted,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).Disposition);
        Assert.Equal(PublicSessionState.Faulted,
            harness.Machine.Snapshot().Session.State);
        Assert.False(harness.Machine.Snapshot().AwaitingOldTreeDeath);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Machine.ShutdownAsync().AsTask());
    }

    [Fact]
    public void Recovery_unknown_accepts_a_fresh_explicit_restart_without_replay()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var ambiguous = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Reset));
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(
            ambiguous,
            WorkerIdentity(2, 321)));
        Assert.True(harness.Machine.MarkLifecycleDispatched(ambiguous));
        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);
        Assert.Equal(SessionRecoveryDeathDisposition.NoRecovery,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).Disposition);
        Assert.Null(harness.Machine.BeginLifecycleTransition(
            SessionRecoveryTransitionKind.Restart));
        var targetLoss = harness.Machine.BeginUnexpectedLoss(
            WorkerIdentity(2, 321),
            harness.Machine.FrozenBindingProof);
        Assert.Equal(SessionRecoveryLossDisposition.RecoveryUnknown,
            targetLoss.Disposition);
        Assert.Equal(SessionRecoveryDeathDisposition.NoRecovery,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(targetLoss.Loss)).Disposition);

        var repair = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Restart,
                harness.Machine.FrozenBindingProof));
        var replacement = WorkerIdentity(3, 322);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(repair, replacement));
        Assert.True(harness.Machine.MarkLifecycleDispatched(repair));
        Assert.True(harness.Machine.CompleteLifecycleTerminal(
            repair,
            PublicSessionState.Ready,
            replacement,
            repair.ExpectedTransitionVersion,
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: true));
        Assert.Equal(PublicSessionState.Ready,
            harness.Machine.Snapshot().Session.State);
        Assert.Equal(0, harness.Factory.CallbackCount);
    }

    [Fact]
    public void Explicit_fault_accepts_manual_restart_but_never_auto_opens()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        Assert.True(harness.Machine.RecordExplicitlyFaulted(
            new SessionTransitionVersion(2)));
        var restart = Assert.IsType<SessionRecoveryTransitionLease>(
            harness.Machine.BeginLifecycleTransition(
                SessionRecoveryTransitionKind.Restart,
                harness.Machine.FrozenBindingProof));
        var replacement = WorkerIdentity(2, 323);
        Assert.True(harness.Machine.AttachLifecycleWorkerIdentity(restart, replacement));
        Assert.True(harness.Machine.MarkLifecycleDispatched(restart));

        Assert.True(harness.Machine.CompleteLifecycleTerminal(
            restart,
            PublicSessionState.Ready,
            replacement,
            new SessionTransitionVersion(3),
            BootstrapState.Restored,
            oldWorkerDeathConfirmed: true));
        var ready = harness.Machine.Snapshot().Session;
        Assert.Equal(PublicSessionState.Ready, ready.State);
        Assert.Equal(replacement.BootId, ready.WorkerBootId);
        Assert.Equal(0, harness.Factory.PrepareCount);
    }

    [Fact]
    public void Duplicate_and_stale_loss_or_death_signals_consume_no_generation()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var first = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);
        var loss = Assert.IsType<SessionRecoveryLossLease>(first.Loss);

        Assert.Equal(SessionRecoveryLossDisposition.Duplicate,
            harness.Machine.BeginUnexpectedLoss(
                harness.OldIdentity,
                harness.Machine.FrozenBindingProof).Disposition);
        Assert.Equal(SessionRecoveryLossDisposition.StaleWorker,
            harness.Machine.BeginUnexpectedLoss(
                WorkerIdentity(99, 99),
                harness.Machine.FrozenBindingProof).Disposition);
        Assert.Empty(harness.Generations.Allocations);

        var death = harness.Machine.ConfirmOldTreeDeath(loss);
        Assert.NotNull(death.Attempt);
        Assert.Equal(SessionRecoveryDeathDisposition.Duplicate,
            harness.Machine.ConfirmOldTreeDeath(loss).Disposition);
        Assert.Single(harness.Generations.Allocations);
        Assert.Equal(SessionRecoveryLossDisposition.StaleWorker,
            harness.Machine.BeginUnexpectedLoss(
                harness.OldIdentity,
                harness.Machine.FrozenBindingProof).Disposition);
        Assert.Single(harness.Generations.Allocations);
    }

    [Fact]
    public void Synchronous_death_confirmation_refuses_to_run_under_state_gate()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);
        var lease = Assert.IsType<SessionRecoveryLossLease>(loss.Loss);
        var field = typeof(SessionRecoveryStateMachine).GetField(
            "_gate",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        var gate = Assert.IsType<object>(field?.GetValue(harness.Machine));

        lock (gate)
        {
            Assert.Throws<InvalidOperationException>(() =>
                harness.Machine.ConfirmOldTreeDeath(lease));
        }

        Assert.Equal(SessionRecoveryDeathDisposition.AttemptStarted,
            harness.Machine.ConfirmOldTreeDeath(lease).Disposition);
        harness.Machine.Shutdown();
    }

    [Fact]
    public void Attempt_deadline_refuses_before_bootstrap_and_schedules_retry()
    {
        var harness = Harness.CreateTemplate(startupTimeoutSeconds: 5);
        harness.PrimeReady();
        var attempt = BeginFirstAttempt(harness);

        harness.Clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        Assert.Equal(SessionRecoveryAttemptDisposition.NotDue,
            harness.Machine.TryExpireAttempt(attempt).Disposition);
        harness.Clock.Advance(TimeSpan.FromTicks(1));
        var expiry = harness.Machine.ExecuteBaseline(attempt);
        Assert.Equal(SessionRecoveryAttemptDisposition.DeadlineExpired,
            expiry.Disposition);
        Assert.Equal(SessionRecoveryDeathDisposition.Scheduled,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(expiry.Loss)).Disposition);
        Assert.Equal(0, harness.Factory.CallbackCount);
        var snapshot = harness.Machine.Snapshot();
        Assert.Equal(PublicSessionState.Backoff, snapshot.Session.State);
        Assert.Equal(
            SessionRecoveryFailureReason.AttemptDeadlineExpired,
            snapshot.LastFailureReason);
    }

    [Theory]
    [InlineData(-1L, true)]
    [InlineData(0L, false)]
    [InlineData(1L, false)]
    public void Slow_baseline_return_rechecks_the_exact_deadline_boundary(
        long offsetTicks,
        bool expectedReady)
    {
        var harness = Harness.CreateTemplate(startupTimeoutSeconds: 5);
        harness.PrimeReady();
        harness.Factory.Baseline = (_, _) =>
        {
            harness.Clock.Advance(
                TimeSpan.FromSeconds(5) + TimeSpan.FromTicks(offsetTicks));
            return SessionRecoveryBaselineResult.Ready;
        };
        var result = harness.Machine.ExecuteBaseline(BeginFirstAttempt(harness));

        if (expectedReady)
        {
            Assert.Equal(SessionRecoveryAttemptDisposition.Ready,
                result.Disposition);
            Assert.Null(result.Loss);
            Assert.Equal(PublicSessionState.Ready,
                harness.Machine.Snapshot().Session.State);
            harness.Machine.Shutdown();
            return;
        }

        Assert.Equal(SessionRecoveryAttemptDisposition.DeadlineExpired,
            result.Disposition);
        Assert.Equal(PublicSessionState.Recovering,
            harness.Machine.Snapshot().Session.State);
        Assert.Equal(SessionRecoveryDeathDisposition.Scheduled,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(result.Loss)).Disposition);
    }

    [Fact]
    public void Deadline_elapsed_before_prepare_refuses_without_calling_factory()
    {
        var harness = Harness.CreateTemplate(startupTimeoutSeconds: 5);
        harness.PrimeReady();
        harness.BootIds.BeforeNext = () =>
            harness.Clock.Advance(TimeSpan.FromSeconds(5));
        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);

        var death = harness.Machine.ConfirmOldTreeDeath(
            Assert.IsType<SessionRecoveryLossLease>(loss.Loss));

        Assert.Equal(SessionRecoveryDeathDisposition.Scheduled, death.Disposition);
        Assert.Null(death.Attempt);
        Assert.Equal(0, harness.Factory.PrepareCount);
        Assert.Single(harness.Generations.Allocations);
        Assert.Equal(PublicSessionState.Backoff,
            harness.Machine.Snapshot().Session.State);
    }

    [Fact]
    public void Deadline_elapsed_during_prepare_requires_containment_before_retry()
    {
        var harness = Harness.CreateTemplate(startupTimeoutSeconds: 5);
        harness.PrimeReady();
        harness.Factory.Preparing = (_, _) =>
            harness.Clock.Advance(TimeSpan.FromSeconds(5));
        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);

        var death = harness.Machine.ConfirmOldTreeDeath(
            Assert.IsType<SessionRecoveryLossLease>(loss.Loss));

        Assert.Equal(SessionRecoveryDeathDisposition.ContainmentRequired,
            death.Disposition);
        Assert.Null(death.Attempt);
        Assert.Equal(0, harness.Factory.CallbackCount);
        Assert.True(harness.Machine.Snapshot().AwaitingOldTreeDeath);
        Assert.Equal(
            SessionRecoveryAttemptStartDisposition.NotDue,
            harness.Machine.TryStartDueAttempt().Disposition);
        Assert.Equal(SessionRecoveryDeathDisposition.Scheduled,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(death.Loss)).Disposition);
        Assert.Equal(1, harness.Factory.DisposeCount);
    }

    [Fact]
    public void Due_attempt_whose_prepare_crosses_deadline_surfaces_containment_lease()
    {
        var harness = Harness.CreateTemplate(startupTimeoutSeconds: 5);
        harness.PrimeReady();
        harness.Factory.Baseline = (_, _) =>
            SessionRecoveryBaselineResult.RetryableFailure;
        var firstFailure = harness.Machine.ExecuteBaseline(BeginFirstAttempt(harness));
        Assert.Equal(SessionRecoveryAttemptDisposition.ContainmentRequired,
            firstFailure.Disposition);
        Assert.Equal(SessionRecoveryDeathDisposition.Scheduled,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(firstFailure.Loss)).Disposition);
        harness.Factory.Preparing = (index, _) =>
        {
            if (index == 2)
                harness.Clock.Advance(TimeSpan.FromSeconds(5));
        };
        harness.Clock.Advance(TimeSpan.FromMilliseconds(250));

        var start = harness.Machine.TryStartDueAttempt();

        Assert.Equal(
            SessionRecoveryAttemptStartDisposition.ContainmentRequired,
            start.Disposition);
        Assert.Null(start.Attempt);
        var loss = Assert.IsType<SessionRecoveryLossLease>(start.Loss);
        Assert.Equal(3, harness.Machine.Snapshot().Session.RecoveryAttempt);
        Assert.Equal(1, harness.Factory.CallbackCount);
        Assert.Equal(SessionRecoveryDeathDisposition.Scheduled,
            harness.Machine.ConfirmOldTreeDeath(loss).Disposition);
        Assert.Equal(2, harness.Factory.DisposeCount);
    }

    [Fact]
    public async Task Loss_confirmation_joins_blocked_prepare_before_advancing_generation()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        using var prepareEntered = new ManualResetEventSlim();
        using var prepareRelease = new ManualResetEventSlim();
        harness.Factory.Preparing = (_, _) =>
        {
            prepareEntered.Set();
            prepareRelease.Wait(TimeSpan.FromSeconds(10));
        };
        var initialLoss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);
        var initialConfirmation = Task.Run(() =>
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(initialLoss.Loss)));
        Assert.True(prepareEntered.Wait(TimeSpan.FromSeconds(10)));
        var preparingIdentity = Assert.Single(harness.Factory.Identities);
        var prepareLoss = harness.Machine.BeginUnexpectedLoss(
            preparingIdentity,
            harness.Machine.FrozenBindingProof);
        var prepareLossLease = Assert.IsType<SessionRecoveryLossLease>(
            prepareLoss.Loss);
        Assert.Throws<InvalidOperationException>(() =>
            harness.Machine.ConfirmOldTreeDeath(prepareLossLease));
        var joinedConfirmation = harness.Machine.ConfirmOldTreeDeathAsync(
            prepareLossLease).AsTask();

        await Task.Delay(20);
        Assert.False(joinedConfirmation.IsCompleted);
        Assert.Single(harness.Generations.Allocations);
        Assert.Equal(1, harness.Factory.PrepareCount);
        prepareRelease.Set();

        _ = await initialConfirmation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionRecoveryDeathDisposition.Scheduled,
            (await joinedConfirmation.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        Assert.Single(harness.Generations.Allocations);
        Assert.Equal(1, harness.Factory.ContainmentCount);
        Assert.Equal(1, harness.Factory.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_late_prepare_cleanup_failure_never_advances_or_hangs(
        bool failDispose)
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        using var prepareEntered = new ManualResetEventSlim();
        using var prepareRelease = new ManualResetEventSlim();
        harness.Factory.Preparing = (_, _) =>
        {
            prepareEntered.Set();
            prepareRelease.Wait(TimeSpan.FromSeconds(10));
        };
        if (failDispose)
        {
            harness.Factory.Disposing = _ =>
                throw new InvalidOperationException("injected dispose failure");
        }
        else
        {
            harness.Factory.Containing = _ =>
                ValueTask.FromException(
                    new InvalidOperationException("injected containment failure"));
        }
        var initialLoss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);

        var initialConfirmation = harness.Machine.ConfirmOldTreeDeathAsync(
            Assert.IsType<SessionRecoveryLossLease>(initialLoss.Loss)).AsTask();
        Assert.False(initialConfirmation.IsCompleted);
        Assert.True(prepareEntered.Wait(TimeSpan.FromSeconds(10)));
        var preparingIdentity = Assert.Single(harness.Factory.Identities);
        var prepareLoss = harness.Machine.BeginUnexpectedLoss(
            preparingIdentity,
            harness.Machine.FrozenBindingProof);
        var lossConfirmation = harness.Machine.ConfirmOldTreeDeathAsync(
            Assert.IsType<SessionRecoveryLossLease>(prepareLoss.Loss)).AsTask();
        prepareRelease.Set();

        Assert.Equal(SessionRecoveryDeathDisposition.ContainmentRequired,
            (await initialConfirmation.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        Assert.Equal(SessionRecoveryDeathDisposition.Faulted,
            (await lossConfirmation.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        Assert.Single(harness.Generations.Allocations);
        Assert.True(harness.Machine.Snapshot().AwaitingOldTreeDeath);
        Assert.Equal(PublicSessionState.Faulted,
            harness.Machine.Snapshot().Session.State);
        Assert.Null(harness.Machine.BeginLifecycleTransition(
            SessionRecoveryTransitionKind.Restart,
            harness.Machine.FrozenBindingProof));
        if (failDispose)
            Assert.Equal(1, harness.Factory.ContainmentCount);
        else
            Assert.Equal(0, harness.Factory.DisposeCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Machine.ShutdownAsync().AsTask());
    }

    [Fact]
    public async Task Shutdown_during_initial_prepare_returns_stopped_and_joins_cleanup()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        using var prepareEntered = new ManualResetEventSlim();
        using var prepareRelease = new ManualResetEventSlim();
        harness.Factory.Preparing = (_, _) =>
        {
            prepareEntered.Set();
            prepareRelease.Wait(TimeSpan.FromSeconds(10));
        };
        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);

        var confirmation = harness.Machine.ConfirmOldTreeDeathAsync(
            Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).AsTask();
        Assert.False(confirmation.IsCompleted);
        Assert.True(prepareEntered.Wait(TimeSpan.FromSeconds(10)));
        var shutdown = harness.Machine.ShutdownAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        prepareRelease.Set();

        Assert.Equal(SessionRecoveryDeathDisposition.Stopped,
            (await confirmation.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, harness.Factory.ContainmentCount);
        Assert.Equal(1, harness.Factory.DisposeCount);
    }

    [Fact]
    public async Task Shutdown_during_due_prepare_returns_stopped_and_joins_cleanup()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        harness.Factory.Baseline = (_, _) =>
            SessionRecoveryBaselineResult.RetryableFailure;
        var firstFailure = harness.Machine.ExecuteBaseline(BeginFirstAttempt(harness));
        Assert.Equal(SessionRecoveryDeathDisposition.Scheduled,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(firstFailure.Loss)).Disposition);
        using var prepareEntered = new ManualResetEventSlim();
        using var prepareRelease = new ManualResetEventSlim();
        harness.Factory.Preparing = (index, _) =>
        {
            if (index != 2)
                return;
            prepareEntered.Set();
            prepareRelease.Wait(TimeSpan.FromSeconds(10));
        };
        harness.Clock.Advance(TimeSpan.FromMilliseconds(250));

        var due = Task.Run(() => harness.Machine.TryStartDueAttempt());
        Assert.True(prepareEntered.Wait(TimeSpan.FromSeconds(10)));
        var shutdown = harness.Machine.ShutdownAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        prepareRelease.Set();

        Assert.Equal(SessionRecoveryAttemptStartDisposition.Stopped,
            (await due.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, harness.Factory.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocked_identity_reservation_seam_keeps_state_prompt_and_is_joined(
        bool blockBootId)
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        using var seamEntered = new ManualResetEventSlim();
        using var seamRelease = new ManualResetEventSlim();
        Action barrier = () =>
        {
            seamEntered.Set();
            seamRelease.Wait(TimeSpan.FromSeconds(10));
        };
        if (blockBootId)
            harness.BootIds.BeforeNext = barrier;
        else
            harness.Generations.BeforeAllocate = barrier;
        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);
        var confirmation = harness.Machine.ConfirmOldTreeDeathAsync(
            Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).AsTask();
        Assert.True(seamEntered.Wait(TimeSpan.FromSeconds(10)));

        var snapshot = await Task.Run(() => harness.Machine.Snapshot())
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(PublicSessionState.Recovering, snapshot.Session.State);
        Assert.Equal(RecoveryPhase.Attempting, snapshot.Session.RecoveryPhase);
        Assert.Null(snapshot.Session.WorkerBootId);
        var shutdownCall = Task.Run(async () =>
            await harness.Machine.ShutdownAsync());
        await Task.Delay(20);
        Assert.False(shutdownCall.IsCompleted);
        Assert.True(harness.Machine.Snapshot().TerminalShutdown);
        seamRelease.Set();

        Assert.Equal(SessionRecoveryDeathDisposition.Stopped,
            (await confirmation.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        await shutdownCall.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, Assert.Single(harness.Generations.Allocations).Value);
        Assert.Equal(0, harness.Factory.PrepareCount);
    }

    [Fact]
    public async Task Deadline_wins_against_inflight_bootstrap_and_late_result_is_inert()
    {
        var harness = Harness.CreateTemplate(startupTimeoutSeconds: 5);
        harness.PrimeReady();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        harness.Factory.Baseline = (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return SessionRecoveryBaselineResult.Ready;
        };
        var attempt = BeginFirstAttempt(harness);

        var execution = Task.Run(() => harness.Machine.ExecuteBaseline(attempt));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        harness.Clock.Advance(TimeSpan.FromSeconds(5));
        var expiry = harness.Machine.TryExpireAttempt(attempt);
        Assert.Equal(SessionRecoveryAttemptDisposition.DeadlineExpired,
            expiry.Disposition);
        var expiryLoss = Assert.IsType<SessionRecoveryLossLease>(expiry.Loss);
        Assert.Throws<InvalidOperationException>(() =>
            harness.Machine.ConfirmOldTreeDeath(expiryLoss));
        var confirmation = harness.Machine.ConfirmOldTreeDeathAsync(
            expiryLoss).AsTask();

        await Task.Delay(20);
        Assert.False(confirmation.IsCompleted);
        Assert.Single(harness.Generations.Allocations);
        Assert.Equal(
            SessionRecoveryAttemptStartDisposition.NotDue,
            harness.Machine.TryStartDueAttempt().Disposition);
        release.Set();

        Assert.Equal(
            SessionRecoveryAttemptDisposition.StaleAttempt,
            (await execution.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        Assert.Equal(SessionRecoveryDeathDisposition.Scheduled,
            (await confirmation.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        Assert.Equal(PublicSessionState.Backoff,
            harness.Machine.Snapshot().Session.State);
        Assert.All(
            Assert.Single(harness.Factory.CallbackMemories).ToArray(),
            value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Shutdown_wins_against_inflight_bootstrap_and_disables_all_retries()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        harness.Factory.Baseline = (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return SessionRecoveryBaselineResult.Ready;
        };
        var attempt = BeginFirstAttempt(harness);

        var execution = Task.Run(() => harness.Machine.ExecuteBaseline(attempt));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var shutdown = harness.Machine.ShutdownAsync().AsTask();
        Assert.True(SpinWait.SpinUntil(
            () => harness.Factory.ContainmentCount == 1,
            TimeSpan.FromSeconds(10)));
        Assert.False(shutdown.IsCompleted);
        release.Set();

        Assert.Equal(
            SessionRecoveryAttemptDisposition.Stopped,
            (await execution.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, harness.Factory.DisposeCount);
        harness.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(
            SessionRecoveryAttemptStartDisposition.Stopped,
            harness.Machine.TryStartDueAttempt().Disposition);
        Assert.True(harness.Machine.Snapshot().TerminalShutdown);
        Assert.False(harness.Machine.Snapshot().Session.ReadyForEffects);
    }

    [Fact]
    public async Task First_shutdown_clears_guardian_lifetime_frozen_bootstrap_bytes()
    {
        var harness = Harness.CreateTemplate();
        var field = typeof(SessionRecoveryStateMachine).GetField(
            "_frozenBootstrapBytes",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        var frozen = Assert.IsType<byte[]>(field?.GetValue(harness.Machine));
        Assert.Contains(frozen, value => value != 0);

        await harness.Machine.ShutdownAsync();
        Assert.All(frozen, value => Assert.Equal(0, value));
        await harness.Machine.ShutdownAsync();
        Assert.All(frozen, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Shutdown_does_not_dispose_resource_when_containment_confirmation_fails()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        var recovered = BeginFirstAttempt(harness);
        Assert.Equal(SessionRecoveryAttemptDisposition.Ready,
            harness.Machine.ExecuteBaseline(recovered).Disposition);
        harness.Factory.Containing = _ => ValueTask.FromException(
            new InvalidOperationException("containment failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Machine.ShutdownAsync().AsTask());

        Assert.Equal(1, harness.Factory.ContainmentCount);
        Assert.Equal(0, harness.Factory.DisposeCount);
        Assert.True(harness.Machine.Snapshot().TerminalShutdown);
    }

    [Fact]
    public async Task Concurrent_execute_calls_invoke_bootstrap_at_most_once()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        harness.Factory.Baseline = (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return SessionRecoveryBaselineResult.Ready;
        };
        var attempt = BeginFirstAttempt(harness);

        var first = Task.Run(() => harness.Machine.ExecuteBaseline(attempt));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var duplicates = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => harness.Machine.ExecuteBaseline(attempt)))
            .ToArray();
        await Task.WhenAll(duplicates).WaitAsync(TimeSpan.FromSeconds(10));
        release.Set();

        Assert.All(duplicates, task =>
            Assert.Equal(SessionRecoveryAttemptDisposition.Duplicate,
                task.Result.Disposition));
        Assert.Equal(SessionRecoveryAttemptDisposition.Ready,
            (await first.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        Assert.Equal(1, harness.Factory.CallbackCount);
    }

    [Fact]
    public void Failed_prepare_consumes_generation_and_next_attempt_uses_a_gap()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        harness.Factory.ThrowOnPrepare.Add(1);

        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);
        var death = harness.Machine.ConfirmOldTreeDeath(
            Assert.IsType<SessionRecoveryLossLease>(loss.Loss));

        Assert.Equal(SessionRecoveryDeathDisposition.Scheduled, death.Disposition);
        Assert.Equal(2, Assert.Single(harness.Generations.Allocations).Value);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(250));
        var due = harness.Machine.TryStartDueAttempt();
        Assert.Equal(SessionRecoveryAttemptStartDisposition.AttemptStarted,
            due.Disposition);
        var next = Assert.IsType<SessionRecoveryAttemptLease>(due.Attempt);
        Assert.Equal(3, next.WorkerIdentity.Generation.Value);
        Assert.Equal(
            SessionRecoveryAttemptDisposition.Ready,
            harness.Machine.ExecuteBaseline(next).Disposition);
        Assert.Equal(new long[] { 2, 3 },
            harness.Generations.Allocations.Select(value => value.Value));
    }

    [Fact]
    public void Reused_generation_from_injected_allocator_fails_closed()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        harness.Generations.OverrideNext = new WorkerGeneration(1);

        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);
        var death = harness.Machine.ConfirmOldTreeDeath(
            Assert.IsType<SessionRecoveryLossLease>(loss.Loss));

        Assert.Equal(SessionRecoveryDeathDisposition.Faulted, death.Disposition);
        Assert.Null(death.Attempt);
        var snapshot = harness.Machine.Snapshot();
        Assert.Equal(PublicSessionState.Faulted, snapshot.Session.State);
        Assert.Equal(SessionRecoveryFailureReason.IdentityReused,
            snapshot.LastFailureReason);
        Assert.Equal(
            SessionRecoveryAttemptStartDisposition.Faulted,
            harness.Machine.TryStartDueAttempt().Disposition);
    }

    [Fact]
    public async Task Worker_loss_during_dispatched_bootstrap_is_unknown_and_callback_is_not_replayed()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        harness.Factory.Baseline = (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return SessionRecoveryBaselineResult.Ready;
        };
        var attempt = BeginFirstAttempt(harness);
        var execution = Task.Run(() => harness.Machine.ExecuteBaseline(attempt));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        var loss = harness.Machine.BeginUnexpectedLoss(
            attempt.WorkerIdentity,
            harness.Machine.FrozenBindingProof);
        Assert.Equal(SessionRecoveryLossDisposition.RecoveryUnknown, loss.Disposition);
        release.Set();
        Assert.Equal(SessionRecoveryAttemptDisposition.StaleAttempt,
            (await execution.WaitAsync(TimeSpan.FromSeconds(10))).Disposition);
        Assert.Equal(SessionRecoveryDeathDisposition.NoRecovery,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(loss.Loss)).Disposition);

        harness.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(
            SessionRecoveryAttemptStartDisposition.NotDue,
            harness.Machine.TryStartDueAttempt().Disposition);
        Assert.Equal(1, harness.Factory.CallbackCount);
        Assert.Equal(PublicSessionState.RecoveryUnknown,
            harness.Machine.Snapshot().Session.State);
        Assert.All(
            Assert.Single(harness.Factory.CallbackMemories).ToArray(),
            value => Assert.Equal(0, value));
    }

    [Fact]
    public void Repeated_snapshots_are_inert_and_preserve_exact_public_identity()
    {
        var harness = Harness.CreateTemplate();
        harness.PrimeReady();

        var before = harness.Machine.Snapshot();
        for (var index = 0; index < 1_000; index++)
            _ = harness.Machine.Snapshot();
        var after = harness.Machine.Snapshot();

        Assert.Equal(before, after);
        Assert.Equal(harness.OldIdentity.BootId, after.Session.WorkerBootId);
        Assert.Equal(harness.OldIdentity.Generation, after.Session.Generation);
        Assert.Equal(new SessionTransitionVersion(1), after.Session.TransitionVersion);
        Assert.True(after.Session.ReadyForEffects);
        Assert.False(after.Session.WarmStateLost);
        Assert.Equal(BootstrapState.Restored, after.Session.BootstrapState);
        Assert.Empty(harness.Generations.Allocations);
        Assert.Equal(0, harness.Factory.PrepareCount);
    }

    [Fact]
    public void One_alias_failure_does_not_change_another_alias_circuit_or_generation()
    {
        var first = Harness.CreateTemplate(alias: "alpha");
        var second = Harness.CreateTemplate(alias: "bravo");
        first.PrimeReady();
        second.PrimeReady();
        first.Factory.Baseline = (_, _) =>
            SessionRecoveryBaselineResult.RetryableFailure;

        Assert.Equal(SessionRecoveryAttemptDisposition.ContainmentRequired,
            FailAndConfirm(first, BeginFirstAttempt(first)).Disposition);

        Assert.Equal(PublicSessionState.Backoff,
            first.Machine.Snapshot().Session.State);
        Assert.Equal(PublicSessionState.Ready,
            second.Machine.Snapshot().Session.State);
        Assert.True(second.Machine.Snapshot().Session.ReadyForEffects);
        Assert.Empty(second.Generations.Allocations);
        Assert.Equal(0, second.Factory.PrepareCount);
    }

    private static SessionRecoveryAttemptTransition FailAndConfirm(
        Harness harness,
        SessionRecoveryAttemptLease attempt)
    {
        var failure = harness.Machine.ExecuteBaseline(attempt);
        Assert.Equal(SessionRecoveryAttemptDisposition.ContainmentRequired,
            failure.Disposition);
        Assert.Equal(SessionRecoveryDeathDisposition.Scheduled,
            harness.Machine.ConfirmOldTreeDeath(
                Assert.IsType<SessionRecoveryLossLease>(failure.Loss)).Disposition);
        return failure;
    }

    private static SessionRecoveryAttemptLease BeginFirstAttempt(Harness harness)
    {
        var loss = harness.Machine.BeginUnexpectedLoss(
            harness.OldIdentity,
            harness.Machine.FrozenBindingProof);
        Assert.Equal(SessionRecoveryLossDisposition.BeganContainment, loss.Disposition);
        var death = harness.Machine.ConfirmOldTreeDeath(
            Assert.IsType<SessionRecoveryLossLease>(loss.Loss));
        Assert.Equal(SessionRecoveryDeathDisposition.AttemptStarted, death.Disposition);
        return Assert.IsType<SessionRecoveryAttemptLease>(death.Attempt);
    }

    private static GuardianHostWorkerIdentity WorkerIdentity(long generation, int boot) =>
        new(new WorkerBootId(Uuid(boot)), new WorkerGeneration(generation));

    private static Guid Uuid(int value) =>
        Guid.Parse($"00000000-0000-4000-8000-{value:x12}");

    private static Sha256Digest Digest(string value) =>
        Sha256Digest.Compute(Encoding.UTF8.GetBytes(value));

    private sealed class Harness
    {
        private Harness(
            SessionRecoveryStateMachine machine,
            ManualTimeProvider clock,
            FakeGenerationAllocator generations,
            FakeWorkerBootIdSource bootIds,
            FakeAttemptFactory factory,
            GuardianHostWorkerIdentity oldIdentity,
            BootstrapState initialBootstrapState)
        {
            Machine = machine;
            Clock = clock;
            Generations = generations;
            BootIds = bootIds;
            Factory = factory;
            OldIdentity = oldIdentity;
            InitialBootstrapState = initialBootstrapState;
        }

        internal SessionRecoveryStateMachine Machine { get; }
        internal ManualTimeProvider Clock { get; }
        internal FakeGenerationAllocator Generations { get; }
        internal FakeWorkerBootIdSource BootIds { get; }
        internal FakeAttemptFactory Factory { get; }
        internal GuardianHostWorkerIdentity OldIdentity { get; }
        internal BootstrapState InitialBootstrapState { get; }

        internal void PrimeReady() =>
            Assert.True(Machine.RecordAcknowledgedReady(
                OldIdentity,
                Machine.Snapshot().Session.TransitionVersion,
                InitialBootstrapState));

        internal static Harness CreateTemplate(
            byte[]? bootstrapBytes = null,
            int startupTimeoutSeconds = 5,
            string alias = "alpha",
            DesiredSessionState desiredState = DesiredSessionState.Ready,
            long transitionVersion = 1)
        {
            bootstrapBytes ??= Encoding.UTF8.GetBytes("$global:baseline = 'frozen'");
            var templateName = new CanonicalAlias("profile");
            var templateDigest = Digest("template-digest");
            var bootstrapDigest = Sha256Digest.Compute(bootstrapBytes);
            var template = new RecoveryTemplate(
                templateName,
                "test profile",
                startupTimeoutSeconds,
                "local",
                "test",
                false,
                templateDigest,
                bootstrapDigest,
                bootstrapBytes);
            var binding = new RecoveryBinding(
                new CanonicalAlias(alias),
                RecoveryBindingKind.Template,
                templateName,
                templateDigest,
                bootstrapDigest,
                false,
                desiredState,
                new SessionTransitionVersion(transitionVersion),
                Digest($"binding-{alias}"));
            return Create(binding, template, BootstrapState.Restored);
        }

        internal static Harness CreateNonTemplate(RecoveryBindingKind kind)
        {
            if (kind is not (RecoveryBindingKind.Default or RecoveryBindingKind.Dynamic))
                throw new ArgumentOutOfRangeException(nameof(kind));
            var alias = kind == RecoveryBindingKind.Default ? "default" : "alpha";
            var binding = new RecoveryBinding(
                new CanonicalAlias(alias),
                kind,
                null,
                null,
                null,
                false,
                DesiredSessionState.Ready,
                new SessionTransitionVersion(1),
                Digest($"binding-{alias}"));
            return Create(binding, null, BootstrapState.NotApplicable);
        }

        private static Harness Create(
            RecoveryBinding binding,
            RecoveryTemplate? template,
            BootstrapState initialBootstrapState)
        {
            var clock = new ManualTimeProvider();
            var generations = new FakeGenerationAllocator(1);
            var bootIds = new FakeWorkerBootIdSource();
            var factory = new FakeAttemptFactory();
            var machine = new SessionRecoveryStateMachine(
                Digest("catalog"),
                binding,
                template,
                new WorkerGenerationHighWatermark(1),
                TimeSpan.FromSeconds(5),
                clock,
                generations,
                bootIds,
                factory);
            return new Harness(
                machine,
                clock,
                generations,
                bootIds,
                factory,
                WorkerIdentity(1, 1),
                initialBootstrapState);
        }
    }

    private sealed class FakeGenerationAllocator(long highWatermark)
        : IWorkerGenerationAllocator
    {
        private long _highWatermark = highWatermark;

        internal List<WorkerGeneration> Allocations { get; } = [];
        internal WorkerGeneration? OverrideNext { get; set; }
        internal Action? BeforeAllocate { get; set; }

        internal void SetHighWatermark(long value) => _highWatermark = value;

        public WorkerGeneration Allocate(CanonicalAlias alias)
        {
            _ = alias ?? throw new ArgumentNullException(nameof(alias));
            BeforeAllocate?.Invoke();
            var value = OverrideNext ?? new WorkerGeneration(++_highWatermark);
            OverrideNext = null;
            Allocations.Add(value);
            return value;
        }
    }

    private sealed class FakeWorkerBootIdSource : ISessionRecoveryWorkerBootIdSource
    {
        private int _next = 100;

        internal Action? BeforeNext { get; set; }

        public WorkerBootId Next()
        {
            BeforeNext?.Invoke();
            return new WorkerBootId(Uuid(Interlocked.Increment(ref _next)));
        }
    }

    private sealed class FakeAttemptFactory : ISessionRecoveryAttemptFactory
    {
        private readonly object _gate = new();

        internal Func<int, ReadOnlyMemory<byte>, SessionRecoveryBaselineResult> Baseline
            { get; set; } = (_, _) => SessionRecoveryBaselineResult.Ready;
        internal Action<int, SessionRecoveryAttemptDeadline>? Preparing { get; set; }
        internal Func<int, ValueTask>? Containing { get; set; }
        internal Action<int>? Disposing { get; set; }
        internal HashSet<int> ThrowOnPrepare { get; } = [];
        internal List<SessionRecoveryAttemptContext> Contexts { get; } = [];
        internal List<GuardianHostWorkerIdentity> Identities { get; } = [];
        internal List<SessionRecoveryAttemptDeadline> Deadlines { get; } = [];
        internal List<byte[]> CallbackSnapshots { get; } = [];
        internal List<ReadOnlyMemory<byte>> CallbackMemories { get; } = [];
        internal int PrepareCount { get; private set; }
        internal int CallbackCount { get; private set; }
        internal int ContainmentCount { get; private set; }
        internal int DisposeCount { get; private set; }

        public ISessionRecoveryPreparedAttempt Prepare(
            SessionRecoveryAttemptContext context,
            GuardianHostWorkerIdentity workerIdentity,
            SessionRecoveryAttemptDeadline deadline)
        {
            Assert.Equal(context.Alias, context.FrozenBinding.Alias);
            int index;
            lock (_gate)
            {
                index = ++PrepareCount;
                Contexts.Add(context);
                Identities.Add(workerIdentity);
                Deadlines.Add(deadline);
            }
            Preparing?.Invoke(index, deadline);
            if (ThrowOnPrepare.Contains(index))
                throw new InvalidOperationException("injected prepare failure");
            return new FakePreparedAttempt(this, index);
        }

        private sealed class FakePreparedAttempt(
            FakeAttemptFactory owner,
            int prepareIndex) : ISessionRecoveryPreparedAttempt
        {
            private int _disposed;

            public SessionRecoveryBaselineResult RestoreBaseline(
                ReadOnlyMemory<byte> exactBootstrapBytes)
            {
                lock (owner._gate)
                {
                    owner.CallbackCount++;
                    owner.CallbackSnapshots.Add(exactBootstrapBytes.ToArray());
                    owner.CallbackMemories.Add(exactBootstrapBytes);
                }
                return owner.Baseline(prepareIndex, exactBootstrapBytes);
            }

            public ValueTask ContainAndConfirmDeathAsync()
            {
                lock (owner._gate)
                    owner.ContainmentCount++;
                return owner.Containing?.Invoke(prepareIndex) ??
                    ValueTask.CompletedTask;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    throw new InvalidOperationException("attempt disposed twice");
                owner.Disposing?.Invoke(prepareIndex);
                lock (owner._gate)
                    owner.DisposeCount++;
            }
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
