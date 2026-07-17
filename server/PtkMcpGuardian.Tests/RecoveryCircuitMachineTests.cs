using PtkMcpGuardian.Lifecycle;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class RecoveryCircuitMachineTests
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
    public void Failure_table_schedules_exact_attempts_then_opens_the_circuit()
    {
        var clock = new ManualTimeProvider();
        var machine = new RecoveryCircuitMachine(clock);
        var lease = Assert.IsType<RecoveryAttemptLease>(machine.BeginRecovery());

        Assert.Equal(1, lease.AttemptOrdinal);
        AssertSnapshot(
            machine.Snapshot(),
            RecoveryCircuitState.Attempting,
            RecoveryPhase.Attempting,
            attempt: 1,
            failures: 0,
            retryAfter: 250,
            ready: false);

        for (var failure = 1; failure <= ExactBackoffs.Length; failure++)
        {
            var transition = machine.ReportGenerationFailure(lease);
            Assert.True(transition.Accepted);
            Assert.Null(transition.ImmediateAttempt);
            Assert.False(transition.Exhausted);

            var delay = ExactBackoffs[failure - 1];
            AssertSnapshot(
                machine.Snapshot(),
                RecoveryCircuitState.Backoff,
                RecoveryPhase.Backoff,
                attempt: failure + 1,
                failures: failure,
                retryAfter: checked((int)delay.TotalMilliseconds),
                ready: false);
            Assert.Null(machine.TryAcquireDueAttempt());

            clock.Advance(delay - TimeSpan.FromTicks(1));
            Assert.False(machine.Snapshot().AttemptDue);
            Assert.Null(machine.TryAcquireDueAttempt());
            clock.Advance(TimeSpan.FromTicks(1));
            Assert.True(machine.Snapshot().AttemptDue);

            lease = Assert.IsType<RecoveryAttemptLease>(machine.TryAcquireDueAttempt());
            Assert.Equal(failure + 1, lease.AttemptOrdinal);
            Assert.False(lease.IsHalfOpen);
        }

        var sixthFailure = machine.ReportGenerationFailure(lease);
        Assert.True(sixthFailure.Accepted);
        AssertSnapshot(
            machine.Snapshot(),
            RecoveryCircuitState.CircuitOpen,
            RecoveryPhase.CircuitOpen,
            attempt: 7,
            failures: 6,
            retryAfter: 60_000,
            ready: false);

        clock.Advance(TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1));
        Assert.Null(machine.TryAcquireDueAttempt());
        clock.Advance(TimeSpan.FromTicks(1));
        var halfOpen = Assert.IsType<RecoveryAttemptLease>(machine.TryAcquireDueAttempt());
        Assert.Equal(7, halfOpen.AttemptOrdinal);
        Assert.True(halfOpen.IsHalfOpen);
        AssertSnapshot(
            machine.Snapshot(),
            RecoveryCircuitState.HalfOpen,
            RecoveryPhase.HalfOpen,
            attempt: 7,
            failures: 6,
            retryAfter: 250,
            ready: false);
    }

    [Fact]
    public void Half_open_failure_reopens_for_sixty_seconds_with_next_ordinal()
    {
        var (clock, machine, halfOpen) = ReachHalfOpen();

        var failure = machine.ReportGenerationFailure(halfOpen);
        Assert.True(failure.Accepted);
        AssertSnapshot(
            machine.Snapshot(),
            RecoveryCircuitState.CircuitOpen,
            RecoveryPhase.CircuitOpen,
            attempt: 8,
            failures: 7,
            retryAfter: 60_000,
            ready: false);

        clock.Advance(TimeSpan.FromSeconds(60));
        var next = Assert.IsType<RecoveryAttemptLease>(machine.TryAcquireDueAttempt());
        Assert.Equal(8, next.AttemptOrdinal);
        Assert.True(next.IsHalfOpen);
    }

    [Fact]
    public void Concurrent_due_callers_receive_exactly_one_half_open_lease()
    {
        var (clock, machine, _) = ReachCircuitOpen();
        clock.Advance(TimeSpan.FromSeconds(60));

        var results = new RecoveryAttemptLease?[64];
        Parallel.For(0, results.Length, index =>
            results[index] = machine.TryAcquireDueAttempt());

        var lease = Assert.Single(results.OfType<RecoveryAttemptLease>());
        Assert.Equal(7, lease.AttemptOrdinal);
        Assert.True(lease.IsHalfOpen);
        Assert.Equal(RecoveryCircuitState.HalfOpen, machine.Snapshot().State);
    }

    [Fact]
    public void Ready_is_immediate_but_pre_stability_loss_counts_as_failure()
    {
        var clock = new ManualTimeProvider();
        var machine = new RecoveryCircuitMachine(clock);
        var lease = Assert.IsType<RecoveryAttemptLease>(machine.BeginRecovery());

        Assert.True(machine.MarkReady(lease));
        AssertSnapshot(
            machine.Snapshot(),
            RecoveryCircuitState.Ready,
            phase: null,
            attempt: 1,
            failures: 0,
            retryAfter: null,
            ready: true);

        clock.Advance(TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1));
        Assert.False(machine.TryCompleteReadyStability(lease));
        var loss = machine.ReportGenerationFailure(lease);
        Assert.True(loss.Accepted);
        Assert.Null(loss.ImmediateAttempt);
        AssertSnapshot(
            machine.Snapshot(),
            RecoveryCircuitState.Backoff,
            RecoveryPhase.Backoff,
            attempt: 2,
            failures: 1,
            retryAfter: 250,
            ready: false);
    }

    [Fact]
    public void Explicit_stability_reset_makes_later_loss_a_fresh_immediate_attempt_one()
    {
        var clock = new ManualTimeProvider();
        var machine = new RecoveryCircuitMachine(clock);
        var first = Assert.IsType<RecoveryAttemptLease>(machine.BeginRecovery());
        Assert.True(machine.MarkReady(first));

        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(machine.Snapshot().StabilityDue);
        Assert.True(machine.TryCompleteReadyStability(first));
        Assert.False(machine.TryCompleteReadyStability(first));
        AssertSnapshot(
            machine.Snapshot(),
            RecoveryCircuitState.Ready,
            phase: null,
            attempt: 0,
            failures: 0,
            retryAfter: null,
            ready: true,
            stabilityReset: true);

        var loss = machine.ReportGenerationFailure(first);
        Assert.True(loss.Accepted);
        var replacement = Assert.IsType<RecoveryAttemptLease>(loss.ImmediateAttempt);
        Assert.Equal(1, replacement.AttemptOrdinal);
        Assert.False(replacement.IsHalfOpen);
        AssertSnapshot(
            machine.Snapshot(),
            RecoveryCircuitState.Attempting,
            RecoveryPhase.Attempting,
            attempt: 1,
            failures: 0,
            retryAfter: 250,
            ready: false);
    }

    [Fact]
    public void Intentional_replacement_preserves_unstable_failure_history_without_counting_failure()
    {
        var clock = new ManualTimeProvider();
        var machine = new RecoveryCircuitMachine(clock);
        var first = Assert.IsType<RecoveryAttemptLease>(machine.BeginRecovery());
        Assert.True(machine.ReportGenerationFailure(first).Accepted);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        var second = Assert.IsType<RecoveryAttemptLease>(machine.TryAcquireDueAttempt());
        Assert.True(machine.MarkReady(second));

        var replacement = Assert.IsType<RecoveryAttemptLease>(
            machine.BeginIntentionalReplacement(second));

        Assert.NotSame(second, replacement);
        Assert.Equal(2, replacement.AttemptOrdinal);
        Assert.False(replacement.IsHalfOpen);
        AssertSnapshot(
            machine.Snapshot(),
            RecoveryCircuitState.Attempting,
            RecoveryPhase.Attempting,
            attempt: 2,
            failures: 1,
            retryAfter: 250,
            ready: false);
        Assert.Null(machine.BeginIntentionalReplacement(second));
    }

    [Fact]
    public void Intentional_replacement_at_stability_boundary_starts_fresh_attempt_one()
    {
        var clock = new ManualTimeProvider();
        var machine = new RecoveryCircuitMachine(clock);
        var ready = Assert.IsType<RecoveryAttemptLease>(machine.BeginRecovery());
        Assert.True(machine.MarkReady(ready));
        clock.Advance(TimeSpan.FromSeconds(60));

        var replacement = Assert.IsType<RecoveryAttemptLease>(
            machine.BeginIntentionalReplacement(ready));

        Assert.Equal(1, replacement.AttemptOrdinal);
        AssertSnapshot(
            machine.Snapshot(),
            RecoveryCircuitState.Attempting,
            RecoveryPhase.Attempting,
            attempt: 1,
            failures: 0,
            retryAfter: 250,
            ready: false);
    }

    [Fact]
    public void Concurrent_intentional_replacement_has_exactly_one_ready_lease_winner()
    {
        var machine = new RecoveryCircuitMachine(new ManualTimeProvider());
        var ready = Assert.IsType<RecoveryAttemptLease>(machine.BeginRecovery());
        Assert.True(machine.MarkReady(ready));
        var results = new RecoveryAttemptLease?[512];

        Parallel.For(0, results.Length, index =>
            results[index] = machine.BeginIntentionalReplacement(ready));

        var replacement = Assert.Single(results.OfType<RecoveryAttemptLease>());
        Assert.Equal(1, replacement.AttemptOrdinal);
        Assert.Equal(RecoveryCircuitState.Attempting, machine.Snapshot().State);
        machine.Stop();
        Assert.Null(machine.BeginIntentionalReplacement(replacement));
    }

    [Fact]
    public void Intentional_replacement_and_failure_race_have_one_transition_owner()
    {
        for (var iteration = 0; iteration < 256; iteration++)
        {
            var machine = new RecoveryCircuitMachine(new ManualTimeProvider());
            var ready = Assert.IsType<RecoveryAttemptLease>(machine.BeginRecovery());
            Assert.True(machine.MarkReady(ready));
            RecoveryAttemptLease? replacement = null;
            RecoveryFailureTransition failure = default;

            Parallel.Invoke(
                () => replacement = machine.BeginIntentionalReplacement(ready),
                () => failure = machine.ReportGenerationFailure(ready));

            Assert.NotEqual(replacement is not null, failure.Accepted);
            Assert.Contains(
                machine.Snapshot().State,
                new[]
                {
                    RecoveryCircuitState.Attempting,
                    RecoveryCircuitState.Backoff,
                });
        }
    }

    [Fact]
    public void Loss_at_stability_boundary_is_fresh_without_a_prior_completion_poll()
    {
        var clock = new ManualTimeProvider();
        var machine = new RecoveryCircuitMachine(clock);
        var first = Assert.IsType<RecoveryAttemptLease>(machine.BeginRecovery());
        Assert.True(machine.MarkReady(first));
        clock.Advance(TimeSpan.FromSeconds(60));

        var loss = machine.ReportGenerationFailure(first);

        Assert.True(loss.Accepted);
        var replacement = Assert.IsType<RecoveryAttemptLease>(loss.ImmediateAttempt);
        Assert.Equal(1, replacement.AttemptOrdinal);
        Assert.False(replacement.IsHalfOpen);
        AssertSnapshot(
            machine.Snapshot(),
            RecoveryCircuitState.Attempting,
            RecoveryPhase.Attempting,
            attempt: 1,
            failures: 0,
            retryAfter: 250,
            ready: false);
    }

    [Fact]
    public void Half_open_loss_at_stability_boundary_starts_a_fresh_cycle()
    {
        var (clock, machine, halfOpen) = ReachHalfOpen();
        Assert.True(machine.MarkReady(halfOpen));
        clock.Advance(TimeSpan.FromSeconds(60));

        var loss = machine.ReportGenerationFailure(halfOpen);

        Assert.True(loss.Accepted);
        var replacement = Assert.IsType<RecoveryAttemptLease>(loss.ImmediateAttempt);
        Assert.Equal(1, replacement.AttemptOrdinal);
        Assert.False(replacement.IsHalfOpen);
        AssertSnapshot(
            machine.Snapshot(),
            RecoveryCircuitState.Attempting,
            RecoveryPhase.Attempting,
            attempt: 1,
            failures: 0,
            retryAfter: 250,
            ready: false);
    }

    [Fact]
    public async Task Stability_completion_and_boundary_loss_always_produce_one_fresh_cycle()
    {
        var clock = new ManualTimeProvider();
        var machine = new RecoveryCircuitMachine(clock);
        var first = Assert.IsType<RecoveryAttemptLease>(machine.BeginRecovery());
        Assert.True(machine.MarkReady(first));
        clock.Advance(TimeSpan.FromSeconds(60));
        using var gate = new ManualResetEventSlim(false);

        var completion = Task.Run(() =>
        {
            gate.Wait();
            return machine.TryCompleteReadyStability(first);
        });
        var failure = Task.Run(() =>
        {
            gate.Wait();
            return machine.ReportGenerationFailure(first);
        });
        gate.Set();

        _ = await completion;
        var transition = await failure;
        Assert.True(transition.Accepted);
        var replacement = Assert.IsType<RecoveryAttemptLease>(transition.ImmediateAttempt);
        Assert.Equal(1, replacement.AttemptOrdinal);
        Assert.Equal(0, machine.Snapshot().ConsecutiveFailedGenerations);
        Assert.Equal(RecoveryCircuitState.Attempting, machine.Snapshot().State);
    }

    [Fact]
    public void Half_open_ready_loss_before_stability_reopens_instead_of_using_backoff()
    {
        var (clock, machine, halfOpen) = ReachHalfOpen();
        Assert.True(machine.MarkReady(halfOpen));
        Assert.True(machine.Snapshot().ReadyForEffects);

        clock.Advance(TimeSpan.FromSeconds(59));
        var loss = machine.ReportGenerationFailure(halfOpen);
        Assert.True(loss.Accepted);
        Assert.Null(loss.ImmediateAttempt);
        AssertSnapshot(
            machine.Snapshot(),
            RecoveryCircuitState.CircuitOpen,
            RecoveryPhase.CircuitOpen,
            attempt: 8,
            failures: 7,
            retryAfter: 60_000,
            ready: false);
    }

    [Fact]
    public void Snapshot_reads_never_consume_a_due_attempt_or_stability_transition()
    {
        var clock = new ManualTimeProvider();
        var machine = new RecoveryCircuitMachine(clock);
        var first = Assert.IsType<RecoveryAttemptLease>(machine.BeginRecovery());
        Assert.True(machine.ReportGenerationFailure(first).Accepted);
        clock.Advance(TimeSpan.FromMilliseconds(250));

        var original = machine.Snapshot();
        Assert.True(original.AttemptDue);
        for (var index = 0; index < 100; index++)
            Assert.Equal(original, machine.Snapshot());

        var second = Assert.IsType<RecoveryAttemptLease>(machine.TryAcquireDueAttempt());
        Assert.Null(machine.TryAcquireDueAttempt());
        Assert.True(machine.MarkReady(second));
        clock.Advance(TimeSpan.FromSeconds(60));

        var ready = machine.Snapshot();
        Assert.True(ready.StabilityDue);
        for (var index = 0; index < 100; index++)
            Assert.Equal(ready, machine.Snapshot());
        Assert.Equal(2, machine.Snapshot().RecoveryAttempt);
        Assert.True(machine.TryCompleteReadyStability(second));
    }

    [Fact]
    public void Retry_after_uses_monotonic_ceiling_and_contract_clamp()
    {
        var clock = new ManualTimeProvider();
        var machine = new RecoveryCircuitMachine(clock);
        var lease = Assert.IsType<RecoveryAttemptLease>(machine.BeginRecovery());
        Assert.True(machine.ReportGenerationFailure(lease).Accepted);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        lease = Assert.IsType<RecoveryAttemptLease>(machine.TryAcquireDueAttempt());
        Assert.True(machine.ReportGenerationFailure(lease).Accepted);

        clock.Advance(TimeSpan.FromMilliseconds(749) + TimeSpan.FromTicks(1));
        Assert.Equal(251, machine.Snapshot().RetryAfterMilliseconds);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(250, machine.Snapshot().RetryAfterMilliseconds);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(250, machine.Snapshot().RetryAfterMilliseconds);

        var (_, circuit, _) = ReachCircuitOpen();
        Assert.Equal(60_000, circuit.Snapshot().RetryAfterMilliseconds);
    }

    [Fact]
    public void Duplicate_and_stale_failure_reports_are_inert()
    {
        var clock = new ManualTimeProvider();
        var machine = new RecoveryCircuitMachine(clock);
        var first = Assert.IsType<RecoveryAttemptLease>(machine.BeginRecovery());

        var forged = new RecoveryAttemptLease(
            first.LeaseSequence,
            first.AttemptOrdinal,
            first.IsHalfOpen);
        Assert.False(machine.ReportGenerationFailure(forged).Accepted);
        Assert.Equal(RecoveryCircuitState.Attempting, machine.Snapshot().State);

        Assert.True(machine.ReportGenerationFailure(first).Accepted);
        var afterFirst = machine.Snapshot();
        Assert.False(machine.ReportGenerationFailure(first).Accepted);
        Assert.Equal(afterFirst, machine.Snapshot());

        clock.Advance(TimeSpan.FromMilliseconds(250));
        var second = Assert.IsType<RecoveryAttemptLease>(machine.TryAcquireDueAttempt());
        Assert.False(machine.MarkReady(first));
        Assert.True(machine.MarkReady(second));
        Assert.False(machine.MarkReady(second));
    }

    [Fact]
    public void Concurrent_duplicate_failure_reports_accept_exactly_one_death()
    {
        var machine = new RecoveryCircuitMachine(new ManualTimeProvider());
        var lease = Assert.IsType<RecoveryAttemptLease>(machine.BeginRecovery());
        var transitions = new RecoveryFailureTransition[64];

        Parallel.For(0, transitions.Length, index =>
            transitions[index] = machine.ReportGenerationFailure(lease));

        Assert.Single(transitions, transition => transition.Accepted);
        Assert.All(
            transitions.Where(transition => !transition.Accepted),
            transition => Assert.Null(transition.ImmediateAttempt));
        AssertSnapshot(
            machine.Snapshot(),
            RecoveryCircuitState.Backoff,
            RecoveryPhase.Backoff,
            attempt: 2,
            failures: 1,
            retryAfter: 250,
            ready: false);
    }

    [Fact]
    public void Stop_makes_active_pending_and_due_transitions_inert()
    {
        var clock = new ManualTimeProvider();
        var machine = new RecoveryCircuitMachine(clock);
        var first = Assert.IsType<RecoveryAttemptLease>(machine.BeginRecovery());
        Assert.True(machine.ReportGenerationFailure(first).Accepted);
        clock.Advance(TimeSpan.FromDays(1));

        machine.Stop();
        machine.Stop();
        AssertSnapshot(
            machine.Snapshot(),
            RecoveryCircuitState.Stopped,
            phase: null,
            attempt: 0,
            failures: 1,
            retryAfter: null,
            ready: false);
        Assert.Null(machine.TryAcquireDueAttempt());
        Assert.Null(machine.BeginRecovery());
        Assert.False(machine.MarkReady(first));
        Assert.False(machine.TryCompleteReadyStability(first));
        Assert.False(machine.ReportGenerationFailure(first).Accepted);
    }

    [Fact]
    public void Concurrent_cycle_start_grants_only_one_immediate_attempt()
    {
        var machine = new RecoveryCircuitMachine(new ManualTimeProvider());
        var results = new RecoveryAttemptLease?[64];
        Parallel.For(0, results.Length, index => results[index] = machine.BeginRecovery());

        var lease = Assert.Single(results.OfType<RecoveryAttemptLease>());
        Assert.Equal(1, lease.AttemptOrdinal);
        Assert.Equal(RecoveryCircuitState.Attempting, machine.Snapshot().State);
    }

    private static (ManualTimeProvider Clock, RecoveryCircuitMachine Machine, RecoveryAttemptLease Lease)
        ReachCircuitOpen()
    {
        var clock = new ManualTimeProvider();
        var machine = new RecoveryCircuitMachine(clock);
        var lease = Assert.IsType<RecoveryAttemptLease>(machine.BeginRecovery());
        foreach (var delay in ExactBackoffs)
        {
            Assert.True(machine.ReportGenerationFailure(lease).Accepted);
            clock.Advance(delay);
            lease = Assert.IsType<RecoveryAttemptLease>(machine.TryAcquireDueAttempt());
        }
        Assert.True(machine.ReportGenerationFailure(lease).Accepted);
        return (clock, machine, lease);
    }

    private static (ManualTimeProvider Clock, RecoveryCircuitMachine Machine, RecoveryAttemptLease Lease)
        ReachHalfOpen()
    {
        var (clock, machine, _) = ReachCircuitOpen();
        clock.Advance(TimeSpan.FromSeconds(60));
        var lease = Assert.IsType<RecoveryAttemptLease>(machine.TryAcquireDueAttempt());
        return (clock, machine, lease);
    }

    private static void AssertSnapshot(
        RecoveryCircuitSnapshot snapshot,
        RecoveryCircuitState state,
        RecoveryPhase? phase,
        long attempt,
        long failures,
        int? retryAfter,
        bool ready,
        bool stabilityReset = false)
    {
        Assert.Equal(state, snapshot.State);
        Assert.Equal(phase, snapshot.RecoveryPhase);
        Assert.Equal(attempt, snapshot.RecoveryAttempt);
        Assert.Equal(failures, snapshot.ConsecutiveFailedGenerations);
        Assert.Equal(retryAfter, snapshot.RetryAfterMilliseconds);
        Assert.Equal(ready, snapshot.ReadyForEffects);
        Assert.Equal(stabilityReset, snapshot.StabilityReset);
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
