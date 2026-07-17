using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Diagnostics.CodeAnalysis;
using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Standalone;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class GuardianHostSupervisorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Loss_before_write_authorization_is_safe_and_never_dispatched()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];
        var blocked = rig.Observer.BlockNextAuthorization();

        var dispatch = rig.DispatchJobListAsync();
        await blocked.WaitAsync(TestTimeout);
        old.Crash();
        await WaitUntilAsync(() =>
            rig.Factory.Resources.Count == 2 &&
            rig.Supervisor.SnapshotState().Host is
            {
                State: PublicHostState.Ready,
                Generation.Value: 2,
            });
        rig.Observer.ReleaseAuthorization();

        var result = await dispatch.WaitAsync(TestTimeout);
        var error = DecodeRecovery(result);
        Assert.Equal(PublicRecoveryDetailCode.BackendLostBeforeDispatch, error.DetailCode);
        Assert.True(error.Retryable);
        var gate = Assert.IsType<SessionReadyGate>(error.RetryGate);
        Assert.Equal(TestRig.Alias, gate.Alias);
        Assert.Equal(RecoveryPhase.Containment, error.RecoveryPhase);
        Assert.Equal(0, old.OperationCount);
        Assert.Equal(0, rig.Factory.Resources[1].OperationCount);
    }

    [Fact]
    public async Task Host_recovery_refusal_uses_the_selected_session_gate()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond, AutoConfirmContainment: false),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];

        old.Crash();
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host is
            {
                State: PublicHostState.Recovering,
                RecoveryPhase: RecoveryPhase.Containment,
            });

        var error = DecodeRecovery(
            await rig.DispatchJobListAsync().WaitAsync(TestTimeout));
        Assert.Equal(PublicRecoveryDetailCode.HostRecovering, error.DetailCode);
        Assert.True(error.Retryable);
        var gate = Assert.IsType<SessionReadyGate>(error.RetryGate);
        Assert.Equal(TestRig.Alias, gate.Alias);
        Assert.Equal(0, old.OperationCount);

        old.ConfirmContainment();
    }

    [Fact]
    public async Task Contract_mismatch_during_recovery_is_permanent()
    {
        await using var rig = new TestRig(
            new AttemptPlan(
                HostBehavior.ContractMismatchHello,
                AutoConfirmContainment: false));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.StartAsync());
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host is
            {
                State: PublicHostState.Recovering,
                LastFailureCode: PublicRecoveryDetailCode.HostContractMismatch,
            });

        var error = DecodeRecovery(
            await rig.DispatchJobListAsync().WaitAsync(TestTimeout));
        Assert.Equal(PublicRecoveryDetailCode.HostContractMismatch, error.DetailCode);
        Assert.False(error.Retryable);
        Assert.Null(error.RetryAfterMilliseconds);
        Assert.Null(error.RecoveryPhase);
        Assert.Null(error.RecoveryAttempt);
        Assert.Null(error.RetryGate);
    }

    [Fact]
    public async Task Contract_mismatch_during_prewrite_loss_beats_retry_guidance()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond, AutoConfirmContainment: false));
        await rig.StartAsync();
        var current = rig.Factory.Resources[0];
        var blocked = rig.Observer.BlockNextAuthorization();

        var dispatch = rig.DispatchJobListAsync();
        await blocked.WaitAsync(TestTimeout);
        await current.InjectContractMismatchAsync();
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host is
            {
                State: PublicHostState.Recovering,
                LastFailureCode: PublicRecoveryDetailCode.HostContractMismatch,
            });
        rig.Observer.ReleaseAuthorization();

        var error = DecodeRecovery(await dispatch.WaitAsync(TestTimeout));
        Assert.Equal(PublicRecoveryDetailCode.HostContractMismatch, error.DetailCode);
        Assert.False(error.Retryable);
        Assert.Null(error.RetryAfterMilliseconds);
        Assert.Null(error.RecoveryPhase);
        Assert.Null(error.RecoveryAttempt);
        Assert.Null(error.RetryGate);
        Assert.Equal(0, current.OperationCount);
    }

    [Fact]
    public async Task Loss_after_private_write_is_outcome_unknown_and_never_replayed()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.CrashAfterRequest, AutoConfirmContainment: false),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];

        var result = await rig.DispatchJobListAsync().WaitAsync(TestTimeout);

        var error = DecodeRecovery(result);
        Assert.Equal(PublicRecoveryDetailCode.OutcomeUnknown, error.DetailCode);
        Assert.False(error.Retryable);
        Assert.Null(error.RetryAfterMilliseconds);
        Assert.Null(error.RecoveryPhase);
        Assert.Null(error.RecoveryAttempt);
        Assert.Null(error.RetryGate);
        Assert.Equal(1, old.OperationCount);
        Assert.Single(rig.Factory.Resources);

        old.ConfirmContainment();
        await WaitUntilAsync(() => rig.Factory.Resources.Count == 2);
        Assert.Equal(0, rig.Factory.Resources[1].OperationCount);
    }

    [Fact]
    public async Task Decoded_terminal_wins_over_immediately_following_host_loss()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond, AutoConfirmContainment: false),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];
        rig.Observer.OnDecoded = old.Crash;

        var result = await rig.DispatchJobListAsync().WaitAsync(TestTimeout);
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State == PublicHostState.Recovering);

        Assert.False(result.IsError);
        Assert.Equal("jobs-generation-1", result.Text);
        Assert.Equal(1, old.OperationCount);
        Assert.Single(rig.Factory.Resources);
        old.ConfirmContainment();
    }

    [Fact]
    public async Task Private_request_ids_remain_monotonic_across_replacement()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var first = rig.Factory.Resources[0];

        Assert.False((await rig.DispatchJobListAsync()).IsError);
        first.Crash();
        await WaitUntilAsync(() =>
            rig.Factory.Resources.Count == 2 &&
            rig.Supervisor.SnapshotState().Host is
            {
                State: PublicHostState.Ready,
                Generation.Value: 2,
            });
        Assert.False((await rig.DispatchJobListAsync()).IsError);

        var requestIds = rig.Factory.Resources
            .SelectMany(resource => resource.RequestIds)
            .ToArray();
        Assert.True(requestIds.Length >= 10);
        Assert.All(
            requestIds.Zip(requestIds.Skip(1)),
            pair => Assert.True(pair.First < pair.Second));
        Assert.Equal(1, first.OperationCount);
        Assert.Equal(1, rig.Factory.Resources[1].OperationCount);
    }

    [Fact]
    public async Task State_polling_is_guardian_local_and_scheduler_inert()
    {
        await using var rig = new TestRig(new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var scheduleCount = rig.Scheduler.ScheduleCount;
        var attemptCount = rig.Factory.Resources.Count;
        var empty = EmptyArguments();

        for (var index = 0; index < 100; index++)
        {
            var result = await rig.Supervisor.DispatchAsync(
                "ptk_state",
                empty,
                CancellationToken.None);
            Assert.False(result.IsError);
            var snapshot = PublicStateCodec.Decode(Encoding.UTF8.GetBytes(result.Text));
            Assert.Equal(PublicHostState.Ready, snapshot.Host.State);
            Assert.True(snapshot.Host.ReadyForEffects);
        }

        Assert.Equal(scheduleCount, rig.Scheduler.ScheduleCount);
        Assert.Equal(attemptCount, rig.Factory.Resources.Count);
        Assert.Equal(0, rig.Factory.Resources[0].OperationCount);
    }

    [Fact]
    public async Task Session_target_identity_is_re_read_under_authority_before_first_write()
    {
        await using var rig = new TestRig(new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var blocked = rig.Observer.BlockNextAuthorization();

        var dispatch = rig.DispatchJobListAsync();
        await blocked.WaitAsync(TestTimeout);
        rig.Sessions.WorkerGeneration = 2;
        rig.Observer.ReleaseAuthorization();

        var error = DecodeRecovery(await dispatch.WaitAsync(TestTimeout));
        Assert.Equal(PublicRecoveryDetailCode.SessionBootstrapFailed, error.DetailCode);
        Assert.False(error.Retryable);
        Assert.Equal(0, rig.Factory.Resources[0].OperationCount);
        rig.Sessions.WorkerGeneration = 1;
    }

    [Fact]
    public async Task Unconfirmed_containment_blocks_every_replacement()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond, AutoConfirmContainment: false),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];

        old.Crash();
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State == PublicHostState.Recovering);
        Assert.Single(rig.Factory.Resources);

        await rig.Scheduler.AdvanceAndCompleteAsync(
            GuardianHostLifecycleController.HostContainmentGrace);
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State ==
                PublicHostState.ContainmentUnconfirmed);
        Assert.Single(rig.Factory.Resources);

        old.ConfirmContainment();
        await WaitUntilAsync(() => rig.Factory.Resources.Count == 2);
        Assert.Equal(2, rig.Factory.Resources[1].Identity.HostGeneration.Value);
    }

    [Fact]
    public async Task Shutdown_claim_during_containment_disposal_prevents_replacement()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond, AutoConfirmContainment: false),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];

        old.Crash();
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State == PublicHostState.Recovering);
        var disposeEntered = old.BlockDispose();
        old.ConfirmContainment();
        await disposeEntered.WaitAsync(TestTimeout);

        var shutdown = rig.Supervisor.ShutdownAsync();
        old.ReleaseDispose();
        await shutdown.WaitAsync(TestTimeout);

        Assert.Equal(PublicHostState.Stopped, rig.Supervisor.SnapshotState().Host.State);
        Assert.Single(rig.Factory.Resources);
    }

    [Fact]
    public async Task Failed_generations_open_a_bounded_circuit_without_poll_driven_probes()
    {
        var plans = new List<AttemptPlan>
        {
            new(HostBehavior.Respond),
        };
        plans.AddRange(Enumerable.Repeat(
            new AttemptPlan(HostBehavior.ProvedNoChild),
            6));
        await using var rig = new TestRig(plans.ToArray());
        await rig.StartAsync();

        rig.Factory.Resources[0].Crash();
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State == PublicHostState.Backoff);
        var backoffs = new[]
        {
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
        };
        for (var index = 0; index < backoffs.Length; index++)
        {
            await rig.Scheduler.AdvanceAndCompleteAllAsync(backoffs[index]);
            var expectedAttempts = index + 3;
            await WaitUntilAsync(() => rig.Factory.Resources.Count >= expectedAttempts);
        }

        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State == PublicHostState.CircuitOpen);
        Assert.Equal(7, rig.Factory.Resources.Count);
        var countBeforePolls = rig.Factory.Resources.Count;
        for (var index = 0; index < 100; index++)
            _ = rig.Supervisor.SnapshotState();
        Assert.Equal(countBeforePolls, rig.Factory.Resources.Count);
        var circuit = rig.Supervisor.SnapshotState().Host;
        Assert.Equal(RecoveryPhase.CircuitOpen, circuit.RecoveryPhase);
        Assert.Equal(7, circuit.RecoveryAttempt);
        Assert.False(circuit.ReadyForEffects);
    }

    [Fact]
    public async Task Registry_is_bounded_and_shutdown_drains_every_written_call()
    {
        await using var rig = new TestRig(new AttemptPlan(HostBehavior.Hold));
        await rig.StartAsync();

        var calls = Enumerable.Range(0, GuardianHostSupervisor.MaximumOutstandingCalls)
            .Select(_ => rig.DispatchJobListAsync())
            .ToArray();
        await WaitUntilAsync(() =>
            rig.Supervisor.OutstandingCallCount ==
                GuardianHostSupervisor.MaximumOutstandingCalls &&
            rig.Factory.Resources[0].OperationCount ==
                GuardianHostSupervisor.MaximumOutstandingCalls);

        var refused = await rig.DispatchJobListAsync().WaitAsync(TestTimeout);
        Assert.True(refused.IsError);
        Assert.Contains("registry is full", refused.Text, StringComparison.Ordinal);

        var shutdown = rig.Supervisor.ShutdownAsync();
        var results = await Task.WhenAll(calls).WaitAsync(TestTimeout);
        await shutdown.WaitAsync(TestTimeout);

        Assert.All(results, result =>
        {
            var error = DecodeRecovery(result);
            Assert.Equal(PublicRecoveryDetailCode.OutcomeUnknown, error.DetailCode);
            Assert.False(error.Retryable);
        });
        Assert.Equal(0, rig.Supervisor.OutstandingCallCount);
        Assert.Equal(0, rig.Supervisor.SnapshotState().Host.RecoveryAttempt);
        Assert.Equal(PublicHostState.Stopped, rig.Supervisor.SnapshotState().Host.State);
    }

    private static PublicRecoveryError DecodeRecovery(GuardianToolResult result)
    {
        Assert.True(result.IsError);
        return PublicRecoveryCodec.Decode(Encoding.UTF8.GetBytes(result.Text));
    }

    private static IReadOnlyDictionary<string, JsonElement> JobListArguments()
    {
        using var document = JsonDocument.Parse("{\"action\":\"list\"}");
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["action"] = document.RootElement.GetProperty("action").Clone(),
        };
    }

    private static IReadOnlyDictionary<string, JsonElement> EmptyArguments() =>
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        while (!predicate())
            await Task.Delay(5, cancellation.Token);
    }

    private sealed class TestRig : IAsyncDisposable
    {
        internal static readonly GuardianBootId Guardian = new(
            Guid.Parse("11111111-1111-4111-8111-111111111111"));
        internal static readonly CanonicalAlias Alias = new("default");
        internal static readonly SessionTransitionVersion Transition = new(1);
        internal static readonly GuardianHostWorkerIdentity Worker = new(
            new WorkerBootId(Guid.Parse("22222222-2222-4222-8222-222222222222")),
            new WorkerGeneration(1));
        internal static readonly Sha256Digest ExecutableDigest = Digest('1');
        internal static readonly Sha256Digest BuildDigest = Digest('2');
        internal static readonly Sha256Digest ContractDigest = Digest('3');
        internal static readonly Sha256Digest ConfigurationDigest = Digest('4');
        internal static readonly Sha256Digest CatalogDigest = Digest('5');
        internal static readonly Sha256Digest PackageDigest = Digest('6');
        internal static readonly Sha256Digest BindingDigest = Digest('7');

        internal TestRig(params AttemptPlan[] plans)
        {
            Clock = new ManualTimeProvider();
            Scheduler = new ManualScheduler(Clock);
            Observer = new BlockingDispatchObserver();
            Sessions = new StaticSessionSource();
            Factory = new FakeAttemptFactory(plans, CreatePins());
            var lifecycle = new GuardianHostLifecycleController(
                Guardian,
                new MonotonicHostGenerationAllocator(),
                new RandomBootIdSource(),
                new FixedDeadlineSource(Clock),
                Factory,
                Clock);
            Supervisor = new GuardianHostSupervisor(
                Guardian,
                lifecycle,
                new FrozenManifestSource(),
                new MonotonicPrivateRequestIdAllocator(),
                Clock,
                Scheduler,
                Sessions,
                Observer,
                CreatePins());
        }

        internal ManualTimeProvider Clock { get; }
        internal ManualScheduler Scheduler { get; }
        internal BlockingDispatchObserver Observer { get; }
        internal ITestSessionControl Sessions { get; }
        internal FakeAttemptFactory Factory { get; }
        internal GuardianHostSupervisor Supervisor { get; }

        internal Task StartAsync() => Supervisor.StartAsync();

        internal Task<GuardianToolResult> DispatchJobListAsync() =>
            Supervisor.DispatchAsync(
                    "ptk_job",
                    JobListArguments(),
                    CancellationToken.None)
                .AsTask();

        public async ValueTask DisposeAsync()
        {
            foreach (var resource in Factory.Resources)
                resource.ConfirmContainment();
            try
            {
                await Supervisor.ShutdownAsync().WaitAsync(TestTimeout);
            }
            catch (TimeoutException exception)
            {
                var state = Supervisor.SnapshotState().Host;
                throw new InvalidOperationException(
                    $"shutdown timed out in {state.State}/{state.RecoveryPhase}; " +
                    $"calls={Supervisor.OutstandingCallCount}; " +
                    $"background_tasks={Supervisor.BackgroundTaskCount}; " +
                    $"background_names={Supervisor.BackgroundTaskNames}; " +
                    $"background_failure={Supervisor.BackgroundFailure}",
                    exception);
            }
            await Supervisor.DisposeAsync();
        }

        private static GuardianHostSupervisorPins CreatePins() => new(
            ExecutableDigest,
            BuildDigest,
            ContractDigest,
            ConfigurationDigest,
            CatalogDigest,
            PackageDigest);

        private static Sha256Digest Digest(char value) => new(new string(value, 64));

        private sealed class FrozenManifestSource : IGuardianHostRecoveryManifestSource
        {
            public RecoveryManifest Create(GuardianHostIdentity identity) => new(
                Guardian,
                identity.HostGeneration,
                CatalogDigest,
                ConfigurationDigest,
                [],
                [
                    new RecoveryBinding(
                        Alias,
                        RecoveryBindingKind.Default,
                        null,
                        null,
                        null,
                        false,
                        DesiredSessionState.Ready,
                        Transition,
                        BindingDigest),
                ],
                [
                    new WorkerGenerationHighWatermarkEntry(
                        Alias,
                        new WorkerGenerationHighWatermark(1)),
                ],
                identity.HostGeneration);
        }

        internal interface ITestSessionControl : IGuardianHostSupervisorSessionSource
        {
            bool Ready { get; set; }
            long WorkerGeneration { get; set; }
        }

        private sealed class StaticSessionSource : ITestSessionControl
        {
            public bool Ready { get; set; } = true;
            public long WorkerGeneration { get; set; } = 1;

            public IReadOnlyList<PublicSessionStateSnapshot> SnapshotSessions() =>
            [
                new PublicSessionStateSnapshot(
                    Alias,
                    DesiredSessionState.Ready,
                    Ready ? PublicSessionState.Ready : PublicSessionState.RecoveryUnknown,
                    Ready ? Worker.BootId : null,
                    Ready ? new WorkerGeneration(WorkerGeneration) : null,
                    Transition,
                    recoveryPhase: null,
                    recoveryAttempt: 0,
                    retryAfterMilliseconds: null,
                    readyForEffects: Ready,
                    lastFailureCode: Ready
                        ? null
                        : PublicRecoveryDetailCode.SessionRecoveryUnknown,
                    warmStateLost: false,
                    bootstrapState: Ready
                        ? BootstrapState.Restored
                        : BootstrapState.Unknown),
            ];

            public bool TryGetJobListTarget(
                [NotNullWhen(true)] out GuardianHostJobListTarget? target)
            {
                target = new GuardianHostJobListTarget(
                    Alias,
                    Transition,
                    new GuardianHostWorkerIdentity(
                        Worker.BootId,
                        new WorkerGeneration(WorkerGeneration)),
                    Ready);
                return true;
            }
        }
    }

    private sealed class RandomBootIdSource : IGuardianHostBootIdSource
    {
        public HostBootId Next() => new(Guid.NewGuid());
    }

    private sealed class FixedDeadlineSource(ManualTimeProvider clock) :
        IGuardianHostStartupDeadlineSource
    {
        public GuardianHostStartupDeadline Next() => new(
            checked(clock.GetTimestamp() + TimeSpan.FromSeconds(30).Ticks));
    }

    private sealed class BlockingDispatchObserver :
        IGuardianHostSupervisorDispatchObserver
    {
        private readonly object _sync = new();
        private TaskCompletionSource<bool>? _entered;
        private TaskCompletionSource<bool>? _release;

        internal Action? OnDecoded { get; set; }

        internal Task BlockNextAuthorization()
        {
            lock (_sync)
            {
                _entered = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _release = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return _entered.Task;
            }
        }

        internal void ReleaseAuthorization()
        {
            lock (_sync)
                (_release ?? throw new InvalidOperationException()).TrySetResult(true);
        }

        public async ValueTask BeforeWriteAuthorizationAsync(
            GuardianHostDispatchObservation observation,
            CancellationToken cancellationToken)
        {
            Task? release = null;
            lock (_sync)
            {
                if (_entered is not null && _release is not null)
                {
                    _entered.TrySetResult(true);
                    release = _release.Task;
                    _entered = null;
                }
            }
            if (release is not null)
                await release.WaitAsync(cancellationToken);
            _ = observation;
        }

        public void OnWriteStarting(GuardianHostDispatchObservation observation) =>
            _ = observation;

        public void OnTerminalDecoded(GuardianHostDispatchObservation observation)
        {
            _ = observation;
            OnDecoded?.Invoke();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utc = DateTimeOffset.UnixEpoch.AddDays(1);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow() => _utc;

        internal void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref _timestamp, duration.Ticks);
            _utc = _utc.Add(duration);
        }
    }

    private sealed class ManualScheduler(ManualTimeProvider clock) :
        IGuardianHostSupervisorScheduler
    {
        private readonly object _sync = new();
        private readonly List<ScheduledDelay> _delays = [];
        private int _scheduleCount;

        internal int ScheduleCount => Volatile.Read(ref _scheduleCount);

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var scheduled = new ScheduledDelay(delay, cancellationToken);
            lock (_sync) _delays.Add(scheduled);
            Interlocked.Increment(ref _scheduleCount);
            return new ValueTask(scheduled.Task);
        }

        internal async Task AdvanceAndCompleteAsync(TimeSpan delay)
        {
            await WaitUntilAsync(() => HasPending(delay));
            clock.Advance(delay);
            ScheduledDelay selected;
            lock (_sync)
            {
                selected = _delays.First(value =>
                    value.Delay == delay && !value.Task.IsCompleted);
            }
            selected.Complete();
        }

        internal async Task AdvanceAndCompleteAllAsync(TimeSpan delay)
        {
            await WaitUntilAsync(() => HasPending(delay));
            clock.Advance(delay);
            ScheduledDelay[] selected;
            lock (_sync)
            {
                selected = _delays.Where(value =>
                    value.Delay == delay && !value.Task.IsCompleted).ToArray();
            }
            foreach (var value in selected) value.Complete();
        }

        private bool HasPending(TimeSpan delay)
        {
            lock (_sync)
                return _delays.Any(value =>
                    value.Delay == delay && !value.Task.IsCompleted);
        }

        private sealed class ScheduledDelay
        {
            private readonly TaskCompletionSource<bool> _completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenRegistration _registration;

            internal ScheduledDelay(TimeSpan delay, CancellationToken cancellationToken)
            {
                Delay = delay;
                _registration = cancellationToken.Register(() =>
                    _completion.TrySetCanceled(cancellationToken));
            }

            internal TimeSpan Delay { get; }
            internal Task Task => _completion.Task;

            internal void Complete()
            {
                _registration.Dispose();
                _completion.TrySetResult(true);
            }
        }
    }

    private enum HostBehavior
    {
        Respond,
        ContractMismatchHello,
        CrashAfterRequest,
        Hold,
        ProvedNoChild,
    }

    private sealed record AttemptPlan(
        HostBehavior Behavior,
        bool AutoConfirmContainment = true);

    private sealed class FakeAttemptFactory : IGuardianHostAttemptFactory
    {
        private readonly object _sync = new();
        private readonly Queue<AttemptPlan> _plans;
        private readonly GuardianHostSupervisorPins _pins;
        private readonly List<FakeConnectedResources> _resources = [];

        internal FakeAttemptFactory(
            IEnumerable<AttemptPlan> plans,
            GuardianHostSupervisorPins pins)
        {
            _plans = new Queue<AttemptPlan>(plans);
            _pins = pins;
        }

        internal IReadOnlyList<FakeConnectedResources> Resources
        {
            get { lock (_sync) return _resources.ToArray(); }
        }

        public IGuardianHostAttemptResources Prepare(
            GuardianHostIdentity identity,
            GuardianHostStartupDeadline startupDeadline)
        {
            AttemptPlan plan;
            lock (_sync)
            {
                plan = _plans.Count == 0
                    ? new AttemptPlan(HostBehavior.Respond)
                    : _plans.Dequeue();
                var resources = new FakeConnectedResources(
                    identity,
                    plan,
                    _pins,
                    checked(100 + (int)identity.HostGeneration.Value));
                _resources.Add(resources);
                _ = startupDeadline;
                return resources;
            }
        }
    }

    private sealed class FakeConnectedResources :
        IGuardianHostConnectedAttemptResources
    {
        private readonly AttemptPlan _plan;
        private readonly TestTransportStream _requests = new();
        private readonly TestTransportStream _events = new();
        private readonly TaskCompletionSource<bool> _hostExited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _containmentConfirmed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _disposeEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly FakeHostPeer _peer;
        private ManualResetEventSlim? _disposeRelease;
        private int _launched;
        private int _closed;
        private int _disposed;

        internal FakeConnectedResources(
            GuardianHostIdentity identity,
            AttemptPlan plan,
            GuardianHostSupervisorPins pins,
            int hostProcessId)
        {
            Identity = identity;
            _plan = plan;
            HostProcessId = hostProcessId;
            _peer = new FakeHostPeer(this, _requests, _events, pins);
        }

        internal GuardianHostIdentity Identity { get; }
        internal int OperationCount => _peer.OperationCount;
        internal IReadOnlyList<long> RequestIds => _peer.RequestIds;

        internal Task InjectContractMismatchAsync() =>
            _peer.InjectContractMismatchAsync();

        public Stream RequestStream => _requests;
        public Stream EventStream => _events;
        public int HostProcessId { get; }
        public Task HostExited => _hostExited.Task;
        public Task ContainmentConfirmed => _containmentConfirmed.Task;

        public GuardianHostLaunchOutcome Launch()
        {
            if (Interlocked.Exchange(ref _launched, 1) != 0)
                throw new InvalidOperationException("Attempt launched twice.");
            if (_plan.Behavior == HostBehavior.ProvedNoChild)
                return GuardianHostLaunchOutcome.ProvedNoChild;
            _peer.Start(_plan.Behavior);
            return GuardianHostLaunchOutcome.Started;
        }

        public void CloseTransport()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _requests.CompleteWriting();
            _events.CompleteWriting();
            _hostExited.TrySetResult(true);
        }

        public void BeginContainment(GuardianHostContainmentDeadline deadline)
        {
            _ = deadline;
            if (_plan.AutoConfirmContainment)
                _containmentConfirmed.TrySetResult(true);
        }

        internal void Crash()
        {
            _events.CompleteWriting();
            _hostExited.TrySetResult(true);
        }

        internal void ConfirmContainment() =>
            _containmentConfirmed.TrySetResult(true);

        internal Task BlockDispose()
        {
            _disposeRelease = new ManualResetEventSlim();
            return _disposeEntered.Task;
        }

        internal void ReleaseDispose() => _disposeRelease?.Set();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _disposeEntered.TrySetResult(true);
            _disposeRelease?.Wait();
            _requests.Dispose();
            _events.Dispose();
            _hostExited.TrySetResult(true);
            _containmentConfirmed.TrySetResult(true);
        }
    }

    private sealed class FakeHostPeer
    {
        private readonly object _sync = new();
        private readonly FakeConnectedResources _owner;
        private readonly GuardianHostProtocolReader _reader;
        private readonly GuardianHostProtocolWriter _writer;
        private readonly GuardianHostSupervisorPins _pins;
        private readonly List<long> _requestIds = [];
        private int _operationCount;

        internal FakeHostPeer(
            FakeConnectedResources owner,
            Stream requests,
            Stream events,
            GuardianHostSupervisorPins pins)
        {
            _owner = owner;
            _reader = new GuardianHostProtocolReader(requests, GuardianHostPeer.Guardian);
            _writer = new GuardianHostProtocolWriter(events, GuardianHostPeer.Host);
            _pins = pins;
        }

        internal int OperationCount => Volatile.Read(ref _operationCount);
        internal IReadOnlyList<long> RequestIds
        {
            get { lock (_sync) return _requestIds.ToArray(); }
        }

        internal void Start(HostBehavior behavior) =>
            _ = Task.Run(() => RunAsync(behavior));

        internal Task InjectContractMismatchAsync() =>
            _writer.WriteAsync(new GuardianHostHello(
                new GuardianBootId(Guid.Parse("99999999-9999-4999-8999-999999999999")),
                _owner.Identity.HostBootId,
                _owner.Identity.HostGeneration,
                _owner.HostProcessId,
                _pins.HostExecutableDigest,
                _pins.HostBuildDigest,
                _pins.PublicContractDigest,
                _pins.ConfigurationDigest)).AsTask();

        private async Task RunAsync(HostBehavior behavior)
        {
            try
            {
                var identity = _owner.Identity;
                await _writer.WriteAsync(new GuardianHostHello(
                    identity.GuardianBootId,
                    identity.HostBootId,
                    identity.HostGeneration,
                    _owner.HostProcessId,
                    _pins.HostExecutableDigest,
                    _pins.HostBuildDigest,
                    behavior == HostBehavior.ContractMismatchHello
                        ? new Sha256Digest(new string('8', 64))
                        : _pins.PublicContractDigest,
                    _pins.ConfigurationDigest));
                if (behavior == HostBehavior.ContractMismatchHello)
                    return;

                var initialize = Assert.IsType<GuardianHostInitialize>(await ReadAsync());
                Record(initialize.RequestId);
                var header = Assert.IsType<ManifestHeaderRequest>(await ReadAsync());
                Record(header.RequestId);
                await _writer.WriteAsync(Success(
                    header.RequestId,
                    new ManifestHeaderAccepted(header.ManifestId)));

                using var transferred = new MemoryStream();
                for (var index = 0; index < header.ChunkCount; index++)
                {
                    using var chunk = Assert.IsType<ManifestChunkRequest>(await ReadAsync());
                    Record(chunk.RequestId);
                    var bytes = chunk.GetRawBytes();
                    await transferred.WriteAsync(bytes);
                    await _writer.WriteAsync(Success(
                        chunk.RequestId,
                        new ManifestChunkAccepted(
                            chunk.ManifestId,
                            chunk.ChunkIndex,
                            checked((int)transferred.Length))));
                }

                var seal = Assert.IsType<ManifestSealRequest>(await ReadAsync());
                Record(seal.RequestId);
                var transferredBytes = transferred.ToArray();
                var digest = Sha256Digest.Compute(transferredBytes);
                await _writer.WriteAsync(Success(
                    seal.RequestId,
                    new ManifestSealed(seal.ManifestId, digest, transferredBytes.Length)));
                await _writer.WriteAsync(new GuardianHostReady(
                    identity.GuardianBootId,
                    identity.HostBootId,
                    identity.HostGeneration,
                    initialize.RequestId,
                    seal.ManifestId,
                    digest,
                    _owner.HostProcessId));

                while (await ReadAsync() is { } message)
                {
                    if (message is not OperationRequest operation)
                        continue;
                    Record(operation.RequestId);
                    Interlocked.Increment(ref _operationCount);
                    if (behavior == HostBehavior.Hold)
                        continue;
                    if (behavior == HostBehavior.CrashAfterRequest)
                    {
                        _owner.Crash();
                        return;
                    }

                    await _writer.WriteAsync(Success(
                        operation.RequestId,
                        new OperationCompleted(new JobListResult(
                            $"jobs-generation-{identity.HostGeneration.Value}"))));
                }
            }
            catch (Exception exception) when (exception is
                IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }

        private async Task<GuardianHostMessage?> ReadAsync() =>
            await _reader.ReadAsync().ConfigureAwait(false);

        private GuardianHostSuccessResponse Success(
            PrivateRequestId requestId,
            GuardianHostSuccessPayload payload) => new(
                _owner.Identity.GuardianBootId,
                _owner.Identity.HostBootId,
                _owner.Identity.HostGeneration,
                requestId,
                payload);

        private void Record(PrivateRequestId requestId)
        {
            lock (_sync) _requestIds.Add(requestId.Value);
        }
    }

    private sealed class TestTransportStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
        private byte[]? _current;
        private int _currentOffset;
        private int _disposed;

        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => Volatile.Read(ref _disposed) == 0;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void CompleteWriting() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            while (_current is null || _currentOffset == _current.Length)
            {
                _current = null;
                _currentOffset = 0;
                try
                {
                    _current = await _chunks.Reader.ReadAsync(cancellationToken);
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
            _current.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            return count;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _chunks.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _chunks.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }
}
