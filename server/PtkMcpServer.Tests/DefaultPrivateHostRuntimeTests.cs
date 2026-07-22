using PtkMcpServer.GuardianHost;
using PtkMcpServer.Sessions;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class DefaultPrivateHostRuntimeTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb"));
    private static readonly HostGeneration HostGeneration = new(17);
    private static readonly CanonicalAlias DefaultAlias = new("default");
    private static readonly SessionTransitionVersion Transition = new(3);
    private static readonly GuardianHostWorkerIdentity Worker = new(
        new WorkerBootId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")),
        new WorkerGeneration(9));
    private static readonly CapabilityToken BackgroundCapability = Token(0x44);
    private static readonly CallId Call = new(
        Guid.Parse("77777777-7777-7777-8777-777777777777"));
    private static readonly GuardianHostOperationIdentity OperationIdentity = new(
        new PlanId(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")),
        new OperationId(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")));
    private static readonly PrivateHostServerIdentity Identity = new(
        Guardian,
        Host,
        HostGeneration,
        hostPid: 4242);

    [Fact]
    public async Task Initialization_creates_the_exact_ready_default_once_and_shutdown_owns_it()
    {
        var session = new RecordingSession();
        var factory = new RecordingSessionFactory(session);
        var runtime = Runtime(factory, new RecordingEventSink(), new RecordingOutputTransfer());
        var initialization = Initialization();

        await runtime.InitializeAsync(initialization, CancellationToken.None);

        Assert.Equal(DefaultPrivateHostRuntimeState.Ready, runtime.State);
        Assert.Same(initialization, factory.Initialization);
        Assert.Equal(DefaultAlias, factory.Binding?.Alias);
        Assert.Equal(1, factory.CreateCalls);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.InitializeAsync(initialization, CancellationToken.None));

        await runtime.ShutdownAsync(Shutdown(), CancellationToken.None);
        await runtime.ShutdownAsync(Shutdown(), CancellationToken.None);

        Assert.Equal(DefaultPrivateHostRuntimeState.Stopped, runtime.State);
        Assert.Equal(1, session.ShutdownCalls);
        Assert.Equal(1, session.DisposeCalls);
    }

    [Fact]
    public async Task Cancellation_after_session_creation_orders_shutdown_before_disposal()
    {
        using var canceled = new CancellationTokenSource();
        var session = new RecordingSession();
        var factory = new RecordingSessionFactory(session)
        {
            AfterCreate = canceled.Cancel,
        };
        var runtime = Runtime(
            factory,
            new RecordingEventSink(),
            new RecordingOutputTransfer());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await runtime.InitializeAsync(Initialization(), canceled.Token));

        Assert.Equal(DefaultPrivateHostRuntimeState.Faulted, runtime.State);
        Assert.Equal(["shutdown", "dispose"], session.LifetimeCalls);
    }

    [Fact]
    public async Task Every_current_default_operation_uses_exact_authority_and_typed_results()
    {
        var session = new RecordingSession();
        var events = new RecordingEventSink();
        var output = new RecordingOutputTransfer();
        var runtime = Runtime(new RecordingSessionFactory(session), events, output);
        await runtime.InitializeAsync(Initialization(), CancellationToken.None);

        OperationRequest[] requests =
        [
            Request(10, new InvokeForegroundOperation(
                Call, Dispatch(), Output(), "Get-Date", false, GuardianHostInvokeRoute.Auto)),
            Request(11, new InvokeBackgroundOperation(
                Call, Dispatch(), Output(), "Get-Process", false,
                GuardianHostInvokeRoute.Pwsh, new PublicJobId(71))),
            Request(12, new JobListOperation(Call, Dispatch())),
            Request(13, new JobStatusOperation(
                Call, Dispatch(), new PublicJobId(71), Token(0x71))),
            Request(14, new JobOutputOperation(
                Call, Dispatch(), Output(), new PublicJobId(71), Token(0x71), 5)),
            Request(15, new JobKillOperation(
                Call, Dispatch(), new PublicJobId(71), Token(0x71))),
            Request(16, new ResetOperation(Call, Dispatch(), expectedGeneration: 9, force: false)),
        ];

        var outcomes = new List<PrivateHostOperationOutcome>();
        foreach (var request in requests)
            outcomes.Add(await runtime.ExecuteOperationAsync(request, CancellationToken.None));

        Assert.Equal("foreground", Assert.IsType<InvokeForegroundResult>(outcomes[0].Result).Text);
        var background = Assert.IsType<InvokeBackgroundResult>(outcomes[1].Result);
        Assert.Equal(new PublicJobId(71), background.PublicJobId);
        Assert.Equal(BackgroundCapability, background.JobCapability);
        Assert.Equal("jobs", Assert.IsType<JobListResult>(outcomes[2].Result).Text);
        Assert.Equal("status", Assert.IsType<JobStatusResult>(outcomes[3].Result).Text);
        Assert.Equal("output", Assert.IsType<JobOutputResult>(outcomes[4].Result).Text);
        Assert.Equal("kill", Assert.IsType<PtkSharedContracts.JobKillResult>(outcomes[5].Result).Text);
        var reset = Assert.IsType<ResetResult>(outcomes[6].Result);
        Assert.Equal(DefaultAlias, reset.Alias);
        Assert.Equal(Transition, reset.TransitionVersion);
        Assert.Same(Worker, reset.WorkerIdentity);
        Assert.True(reset.ReadyForEffects);
        Assert.True(reset.WarmStateLost);
        Assert.Equal(BootstrapState.Restored, reset.BootstrapState);

        Assert.Equal(
            ["invoke_foreground", "invoke_background", "job_list", "job_status",
             "job_output", "job_kill", "reset"],
            session.Calls);
        Assert.All(session.Authorities, authority =>
            Assert.Equal(10_000, authority.AbsoluteDeadlineUnixTimeMilliseconds));
        var backgroundAuthority = session.Authorities[1].BackgroundJob;
        Assert.Equal(new PublicJobId(71), backgroundAuthority?.PublicJobId);
        Assert.Equal(BackgroundCapability, backgroundAuthority?.JobCapability);

        Assert.Equal(2, output.ExecutionRequests.Count);
        Assert.Equal([requests[0], requests[1]], output.ExecutionRequests);
        var transferred = Assert.Single(output.TextTransfers);
        Assert.Same(requests[4], transferred.Request);
        Assert.Equal("output", transferred.Text);

        Assert.Equal(requests.Length * 2, events.Events.Count);
        for (var index = 0; index < requests.Length; index++)
        {
            var started = Assert.IsType<OperationDeliveryEvent>(events.Events[index * 2]);
            var terminal = Assert.IsType<OperationDeliveryEvent>(events.Events[index * 2 + 1]);
            AssertDelivery(started, requests[index], GuardianHostDeliveryState.WriteStarted);
            AssertDelivery(terminal, requests[index], GuardianHostDeliveryState.TerminalDecoded);
        }
    }

    [Fact]
    public async Task Delivery_write_started_precedes_the_effect_and_terminal_follows_it()
    {
        var events = new RecordingEventSink();
        var session = new RecordingSession
        {
            OnEffect = () =>
            {
                var started = Assert.IsType<OperationDeliveryEvent>(Assert.Single(events.Events));
                Assert.Equal(GuardianHostDeliveryState.WriteStarted, started.DeliveryState);
            },
        };
        var runtime = Runtime(
            new RecordingSessionFactory(session),
            events,
            new RecordingOutputTransfer());
        await runtime.InitializeAsync(Initialization(), CancellationToken.None);
        var request = Request(20, new JobListOperation(Call, Dispatch()));

        var outcome = await runtime.ExecuteOperationAsync(request, CancellationToken.None);

        Assert.IsType<JobListResult>(outcome.Result);
        Assert.Equal(2, events.Events.Count);
        Assert.Equal(
            GuardianHostDeliveryState.TerminalDecoded,
            Assert.IsType<OperationDeliveryEvent>(events.Events[1]).DeliveryState);
    }

    [Fact]
    public async Task Stale_identity_and_expired_capabilities_are_not_dispatched()
    {
        var session = new RecordingSession();
        var events = new RecordingEventSink();
        var output = new RecordingOutputTransfer();
        var runtime = Runtime(new RecordingSessionFactory(session), events, output);
        await runtime.InitializeAsync(Initialization(), CancellationToken.None);
        _ = await runtime.ExecuteOperationAsync(
            Request(30, new JobListOperation(Call, Dispatch())),
            CancellationToken.None);
        session.ClearCalls();
        events.Events.Clear();

        var otherWorker = new GuardianHostWorkerIdentity(
            new WorkerBootId(Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff")),
            Worker.Generation);
        var wrongGeneration = new GuardianHostWorkerIdentity(
            Worker.BootId,
            new WorkerGeneration(10));
        var cases = new (OperationRequest Request, GuardianHostPrivateDetailCode Error)[]
        {
            (Request(31, new JobListOperation(Call, Dispatch()), alias: new CanonicalAlias("other")),
                GuardianHostPrivateDetailCode.SessionNotFound),
            (Request(32, new JobListOperation(Call, Dispatch()), transition: new SessionTransitionVersion(4)),
                GuardianHostPrivateDetailCode.ExpectedGenerationMismatch),
            (Request(33, new JobListOperation(Call, Dispatch()), worker: wrongGeneration),
                GuardianHostPrivateDetailCode.WorkerGenerationMismatch),
            (Request(34, new JobListOperation(Call, Dispatch()), worker: otherWorker),
                GuardianHostPrivateDetailCode.WorkerBootMismatch),
            (Request(35, new JobListOperation(Call, Dispatch(expires: 1_000))),
                GuardianHostPrivateDetailCode.CapabilityInvalid),
            (Request(36, new InvokeForegroundOperation(
                    Call, Dispatch(), Output(expires: 1_000), "1", false,
                    GuardianHostInvokeRoute.Auto)),
                GuardianHostPrivateDetailCode.OutputCapabilityInvalid),
            (Request(37, new ResetOperation(Call, Dispatch(), expectedGeneration: 10, force: false)),
                GuardianHostPrivateDetailCode.ExpectedGenerationMismatch),
        };

        foreach (var item in cases)
        {
            var outcome = await runtime.ExecuteOperationAsync(
                item.Request,
                CancellationToken.None);
            Assert.Null(outcome.Result);
            Assert.Equal(item.Error, outcome.Error?.DetailCode);
        }

        Assert.Empty(session.Calls);
        Assert.Empty(output.ExecutionRequests);
        Assert.Equal(cases.Length, events.Events.Count);
        Assert.All(events.Events, message =>
        {
            var delivery = Assert.IsType<OperationDeliveryEvent>(message);
            Assert.Equal(GuardianHostDeliveryState.NotDispatched, delivery.DeliveryState);
            Assert.Null(delivery.WorkerRequestId);
        });
    }

    [Fact]
    public async Task Expired_capability_cannot_bind_the_logical_worker_boot()
    {
        var session = new RecordingSession();
        var runtime = Runtime(
            new RecordingSessionFactory(session),
            new RecordingEventSink(),
            new RecordingOutputTransfer());
        await runtime.InitializeAsync(Initialization(), CancellationToken.None);
        var expiredWorker = new GuardianHostWorkerIdentity(
            new WorkerBootId(Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff")),
            Worker.Generation);

        var refused = await runtime.ExecuteOperationAsync(
            Request(40, new JobListOperation(Call, Dispatch(expires: 1_000)), worker: expiredWorker),
            CancellationToken.None);
        var accepted = await runtime.ExecuteOperationAsync(
            Request(41, new JobListOperation(Call, Dispatch()), worker: Worker),
            CancellationToken.None);

        Assert.Equal(GuardianHostPrivateDetailCode.CapabilityInvalid, refused.Error?.DetailCode);
        Assert.IsType<JobListResult>(accepted.Result);
        Assert.Single(session.Calls);
    }

    [Fact]
    public async Task Output_capture_factory_failure_is_refused_before_session_effect()
    {
        var session = new RecordingSession();
        var events = new RecordingEventSink();
        var output = new RecordingOutputTransfer
        {
            CreateFailure = new InvalidOperationException("capture unavailable"),
        };
        var runtime = Runtime(new RecordingSessionFactory(session), events, output);
        await runtime.InitializeAsync(Initialization(), CancellationToken.None);
        var request = Request(42, new InvokeForegroundOperation(
            Call, Dispatch(), Output(), "1", false, GuardianHostInvokeRoute.Auto));

        var outcome = await runtime.ExecuteOperationAsync(request, CancellationToken.None);

        Assert.Equal(
            GuardianHostPrivateDetailCode.OutputCapabilityInvalid,
            outcome.Error?.DetailCode);
        Assert.Null(outcome.Result);
        Assert.Empty(session.Calls);
        Assert.Equal([request], output.ExecutionRequests);
        var delivery = Assert.IsType<OperationDeliveryEvent>(Assert.Single(events.Events));
        Assert.Equal(GuardianHostDeliveryState.NotDispatched, delivery.DeliveryState);
        Assert.Null(delivery.WorkerRequestId);
    }

    [Fact]
    public async Task Background_capability_source_failure_is_refused_before_session_effect()
    {
        var session = new RecordingSession();
        var events = new RecordingEventSink();
        var output = new RecordingOutputTransfer();
        var runtime = Runtime(
            new RecordingSessionFactory(session),
            events,
            output,
            createJobCapability: () => throw new InvalidOperationException(
                "capability unavailable"));
        await runtime.InitializeAsync(Initialization(), CancellationToken.None);
        var request = Request(43, new InvokeBackgroundOperation(
            Call,
            Dispatch(),
            Output(),
            "1",
            false,
            GuardianHostInvokeRoute.Auto,
            new PublicJobId(72)));

        var outcome = await runtime.ExecuteOperationAsync(request, CancellationToken.None);

        Assert.Equal(
            GuardianHostPrivateDetailCode.CapabilityInvalid,
            outcome.Error?.DetailCode);
        Assert.Null(outcome.Result);
        Assert.Empty(session.Calls);
        Assert.Empty(output.ExecutionRequests);
        var delivery = Assert.IsType<OperationDeliveryEvent>(Assert.Single(events.Events));
        Assert.Equal(GuardianHostDeliveryState.NotDispatched, delivery.DeliveryState);
        Assert.Null(delivery.WorkerRequestId);
    }

    [Fact]
    public async Task Background_terminal_is_emitted_with_exact_private_identity()
    {
        var session = new RecordingSession();
        var events = new RecordingEventSink();
        var runtime = Runtime(
            new RecordingSessionFactory(session),
            events,
            new RecordingOutputTransfer());
        await runtime.InitializeAsync(Initialization(), CancellationToken.None);
        var request = Request(43, new InvokeBackgroundOperation(
            Call,
            Dispatch(),
            Output(),
            "1",
            false,
            GuardianHostInvokeRoute.Pwsh,
            new PublicJobId(72)));

        var outcome = await runtime.ExecuteOperationAsync(
            request,
            CancellationToken.None);
        Assert.NotNull(outcome.Result);
        Assert.NotNull(session.BackgroundTerminal);
        await session.BackgroundTerminal!(new JobSnapshot(
            Id: 72,
            Pid: 7002,
            Running: false,
            ExitCode: 0,
            StartedUtc: DateTimeOffset.UnixEpoch,
            Script: "1",
            OutputPath: "private")
        {
            RootTerminationConfirmed = true,
            OutputCaptureComplete = false,
            OutputRecoveryFinalized = true,
            OutputRecovery = new OutputRecoverySummary(
                Handle: null,
                OutputArtifactState.Incomplete,
                Bytes: 17,
                DetailCode: "capture_incomplete",
                Advertise: false),
        });

        var terminal = Assert.IsType<JobLifecycleEvent>(events.Events[^1]);
        Assert.Null(terminal.RequestId);
        Assert.Equal(DefaultAlias, terminal.SessionAlias);
        Assert.Equal(Transition, terminal.SessionTransitionVersion);
        Assert.Same(Worker, terminal.WorkerIdentity);
        Assert.Equal(OperationIdentity, terminal.OperationIdentity);
        Assert.Equal(new PublicJobId(72), terminal.PublicJobId);
        Assert.Equal(GuardianHostJobState.Completed, terminal.State);
        Assert.Equal(0, terminal.ExitCode);
        Assert.Equal(GuardianHostOutputState.SealedIncomplete, terminal.OutputState);
        Assert.Equal(17, terminal.OutputBytes);
        Assert.Null(terminal.OutputDigest);
    }

    [Fact]
    public async Task Invalid_or_oversized_text_is_rejected_after_a_terminal_delivery()
    {
        var session = new RecordingSession();
        session.Results.Enqueue("\ud800");
        session.Results.Enqueue(new string('x', ContractLimits.MaximumTextResultBytes + 1));
        var events = new RecordingEventSink();
        var output = new RecordingOutputTransfer();
        var runtime = Runtime(new RecordingSessionFactory(session), events, output);
        await runtime.InitializeAsync(Initialization(), CancellationToken.None);
        OperationRequest[] requests =
        [
            Request(44, new JobOutputOperation(
                Call, Dispatch(), Output(), new PublicJobId(71), Token(0x71), 0)),
            Request(45, new JobOutputOperation(
                Call, Dispatch(), Output(), new PublicJobId(71), Token(0x71), 0)),
        ];

        var invalid = await runtime.ExecuteOperationAsync(requests[0], CancellationToken.None);
        var oversized = await runtime.ExecuteOperationAsync(requests[1], CancellationToken.None);

        Assert.Equal(
            GuardianHostPrivateDetailCode.InvalidOperationResponse,
            invalid.Error?.DetailCode);
        Assert.Equal(
            GuardianHostPrivateDetailCode.OperationResultTooLarge,
            oversized.Error?.DetailCode);
        Assert.Empty(output.TextTransfers);
        Assert.Equal(4, events.Events.Count);
        for (var index = 0; index < requests.Length; index++)
        {
            Assert.Equal(
                GuardianHostDeliveryState.WriteStarted,
                Assert.IsType<OperationDeliveryEvent>(events.Events[index * 2]).DeliveryState);
            Assert.Equal(
                GuardianHostDeliveryState.TerminalDecoded,
                Assert.IsType<OperationDeliveryEvent>(events.Events[index * 2 + 1]).DeliveryState);
        }
    }

    [Fact]
    public async Task Known_job_capability_failure_still_reaches_terminal_delivery()
    {
        var session = new RecordingSession
        {
            EffectFailure = new JobCapabilityException(),
        };
        var events = new RecordingEventSink();
        var runtime = Runtime(
            new RecordingSessionFactory(session),
            events,
            new RecordingOutputTransfer());
        await runtime.InitializeAsync(Initialization(), CancellationToken.None);

        var outcome = await runtime.ExecuteOperationAsync(
            Request(46, new JobStatusOperation(
                Call, Dispatch(), new PublicJobId(71), Token(0x71))),
            CancellationToken.None);

        Assert.Equal(
            GuardianHostPrivateDetailCode.JobCapabilityInvalid,
            outcome.Error?.DetailCode);
        Assert.Equal(["job_status"], session.Calls);
        Assert.Equal(2, events.Events.Count);
        Assert.Equal(
            GuardianHostDeliveryState.WriteStarted,
            Assert.IsType<OperationDeliveryEvent>(events.Events[0]).DeliveryState);
        Assert.Equal(
            GuardianHostDeliveryState.TerminalDecoded,
            Assert.IsType<OperationDeliveryEvent>(events.Events[1]).DeliveryState);
    }

    [Fact]
    public async Task Session_lifecycle_operations_remain_explicitly_unsupported_in_R4_default_mode()
    {
        var session = new RecordingSession();
        var events = new RecordingEventSink();
        var runtime = Runtime(
            new RecordingSessionFactory(session),
            events,
            new RecordingOutputTransfer());
        await runtime.InitializeAsync(Initialization(), CancellationToken.None);

        OperationRequest[] requests =
        [
            Request(50, new SessionOpenOperation(Call, Dispatch(), template: null,
                allowColdBackground: false), worker: null),
            Request(51, new SessionCloseOperation(Call, Dispatch(), expectedGeneration: 9,
                force: false)),
            Request(52, new SessionRestartOperation(Call, Dispatch(), expectedGeneration: 9,
                force: false)),
        ];

        foreach (var request in requests)
        {
            var outcome = await runtime.ExecuteOperationAsync(request, CancellationToken.None);
            Assert.Equal(GuardianHostPrivateDetailCode.UnsupportedOperation, outcome.Error?.DetailCode);
        }

        Assert.Empty(session.Calls);
        Assert.Equal(2, events.Events.Count);
        Assert.All(events.Events, message => Assert.Equal(
            GuardianHostDeliveryState.NotDispatched,
            Assert.IsType<OperationDeliveryEvent>(message).DeliveryState));
    }

    [Fact]
    public async Task Nonready_manifest_fails_before_session_creation_and_cannot_retry()
    {
        var session = new RecordingSession();
        var factory = new RecordingSessionFactory(session);
        var runtime = Runtime(factory, new RecordingEventSink(), new RecordingOutputTransfer());

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await runtime.InitializeAsync(
                Initialization(DesiredSessionState.Cold),
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.InitializeAsync(Initialization(), CancellationToken.None));

        Assert.Equal(DefaultPrivateHostRuntimeState.Faulted, runtime.State);
        Assert.Equal(0, factory.CreateCalls);
        Assert.Equal(0, session.DisposeCalls);
    }

    private static DefaultPrivateHostRuntime Runtime(
        IPrivateHostSessionFactory sessionFactory,
        IPrivateHostEventSink events,
        IPrivateHostOutputTransfer output,
        Func<CapabilityToken>? createJobCapability = null) =>
        new(
            Identity,
            events,
            sessionFactory,
            output,
            createJobCapability: createJobCapability ?? (() => BackgroundCapability),
            unixTimeMilliseconds: () => 1_000);

    private static PrivateHostInitialization Initialization(
        DesiredSessionState desiredState = DesiredSessionState.Ready)
    {
        var configuration = Digest('4');
        var manifest = new RecoveryManifest(
            Guardian,
            HostGeneration,
            Digest('3'),
            configuration,
            [],
            [
                new RecoveryBinding(
                    DefaultAlias,
                    RecoveryBindingKind.Default,
                    templateName: null,
                    templateDigest: null,
                    bootstrapDigest: null,
                    allowColdBackground: true,
                    desiredState,
                    Transition,
                    Digest('5')),
            ],
            [
                new WorkerGenerationHighWatermarkEntry(
                    DefaultAlias,
                    new WorkerGenerationHighWatermark(Worker.Generation.Value)),
            ],
            HostGeneration);
        return new PrivateHostInitialization(
            manifest,
            new PrivateRequestId(1),
            new ManifestId(Guid.Parse("11111111-1111-4111-8111-111111111111")),
            Sha256Digest.Compute(RecoveryManifestCodec.Encode(manifest)));
    }

    private static OperationRequest Request(
        long requestId,
        GuardianHostOperation operation,
        CanonicalAlias? alias = null,
        SessionTransitionVersion? transition = null,
        GuardianHostWorkerIdentity? worker = null)
    {
        var isOpen = operation is SessionOpenOperation;
        var selectedWorker = isOpen ? null : worker ?? Worker;
        var needsOperationIdentity = operation is
            InvokeForegroundOperation or InvokeBackgroundOperation;
        return new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(requestId),
            deadlineUnixTimeMilliseconds: 10_000,
            alias ?? DefaultAlias,
            transition ?? Transition,
            selectedWorker,
            needsOperationIdentity ? OperationIdentity : null,
            operation);
    }

    private static DispatchCapability Dispatch(long expires = 5_000) =>
        new(Token(0x22), Call, expires);

    private static OutputCapability Output(long expires = 5_000) =>
        new(Token(0x33), maximumBytes: 1024, expires);

    private static GuardianHostShutdown Shutdown() => new(
        Guardian,
        Host,
        HostGeneration,
        new PrivateRequestId(100),
        deadlineUnixTimeMilliseconds: 10_000,
        GuardianHostShutdownReason.GuardianShutdown);

    private static void AssertDelivery(
        OperationDeliveryEvent delivery,
        OperationRequest request,
        GuardianHostDeliveryState state)
    {
        Assert.Equal(request.RequestId, delivery.RequestId);
        Assert.Equal(request.SessionAlias, delivery.SessionAlias);
        Assert.Equal(request.SessionTransitionVersion, delivery.SessionTransitionVersion);
        Assert.Equal(request.Worker?.BootId, delivery.WorkerIdentity?.BootId);
        Assert.Equal(request.Worker?.Generation, delivery.WorkerIdentity?.Generation);
        Assert.Equal(request.OperationIdentity, delivery.OperationIdentity);
        Assert.Equal(request.Operation.DispatchCapability.Token, delivery.DispatchToken);
        Assert.Equal(state, delivery.DeliveryState);
        Assert.Equal(request.RequestId, delivery.WorkerRequestId);
    }

    private static Sha256Digest Digest(char value) => new(new string(value, 64));

    private static CapabilityToken Token(byte value)
    {
        var bytes = Enumerable.Repeat(value, ContractLimits.CapabilityTokenBytes).ToArray();
        return new CapabilityToken(Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'));
    }

    private sealed class RecordingSessionFactory(IPrivateSessionOperations session) :
        IPrivateHostSessionFactory
    {
        internal int CreateCalls { get; private set; }
        internal PrivateHostInitialization? Initialization { get; private set; }
        internal RecoveryBinding? Binding { get; private set; }
        internal Action? AfterCreate { get; init; }

        public ValueTask<IPrivateSessionOperations> CreateAsync(
            PrivateHostInitialization initialization,
            RecoveryBinding defaultBinding,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            Initialization = initialization;
            Binding = defaultBinding;
            AfterCreate?.Invoke();
            return ValueTask.FromResult(session);
        }
    }

    private sealed class RecordingEventSink : IPrivateHostEventSink
    {
        private long _sequence;

        internal List<GuardianHostEvent> Events { get; } = [];

        public ValueTask WriteEventAsync(
            Func<HostEventSequence, GuardianHostEvent> createEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(createEvent(new HostEventSequence(++_sequence)));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingOutputTransfer : IPrivateHostOutputTransfer
    {
        internal List<OperationRequest> ExecutionRequests { get; } = [];
        internal List<(OperationRequest Request, string Text)> TextTransfers { get; } = [];
        internal Exception? CreateFailure { get; init; }

        public IExecutionOutputCaptureOwner CreateExecutionCapture(OperationRequest request)
        {
            ExecutionRequests.Add(request);
            if (CreateFailure is not null)
                throw CreateFailure;
            return new RecordingCapture();
        }

        public ValueTask TransferTextAsync(
            OperationRequest request,
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TextTransfers.Add((request, text));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCapture : IExecutionOutputCaptureOwner
    {
        public long MaximumArtifactBytes => 1024;

        public Task<OutputCapturePreparation> PrepareAsync(
            DateTimeOffset absoluteDeadlineUtc,
            TimeSpan maximumWait,
            CancellationToken cancellationToken) =>
            Task.FromResult(OutputCapturePreparation.Pending());

        public Task<OutputRecoverySummary> SealAsync(
            OutputArtifactContent content,
            TimeSpan maximumWait) =>
            Task.FromResult(OutputRecoverySummary.Unavailable("remote_capture"));

        public Task<OutputRecoverySummary> SealIncompleteAsync(
            OutputArtifactContent content,
            string reason,
            TimeSpan maximumWait) =>
            Task.FromResult(OutputRecoverySummary.Unavailable("remote_capture"));

        public bool TryTransferToBackground(out IExecutionOutputCapture? capture)
        {
            capture = new RecordingCapture();
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingSession : IPrivateSessionOperations
    {
        internal List<string> Calls { get; } = [];
        internal List<SessionOperationAuthority> Authorities { get; } = [];
        internal Queue<string?> Results { get; } = [];
        internal List<string> LifetimeCalls { get; } = [];
        internal Action? OnEffect { get; init; }
        internal Exception? EffectFailure { get; init; }
        internal Func<JobSnapshot, Task>? BackgroundTerminal { get; private set; }
        internal int ShutdownCalls { get; private set; }
        internal int DisposeCalls { get; private set; }

        public Task<string> InvokeAsync(
            SessionOperationAuthority operationAuthority,
            InvokeForegroundOperation operation,
            CancellationToken cancellationToken,
            IExecutionOutputCaptureOwner outputCaptureOwner) =>
            Complete("invoke_foreground", "foreground", operationAuthority, operation,
                cancellationToken, outputCaptureOwner);

        public Task<string> InvokeAsync(
            SessionOperationAuthority operationAuthority,
            InvokeBackgroundOperation operation,
            CancellationToken cancellationToken,
            Func<JobSnapshot, Task> onTerminal,
            IExecutionOutputCaptureOwner outputCaptureOwner)
        {
            Assert.Equal(operation.PublicJobId, operationAuthority.BackgroundJob?.PublicJobId);
            Assert.Equal(BackgroundCapability, operationAuthority.BackgroundJob?.JobCapability);
            BackgroundTerminal = onTerminal;
            return Complete("invoke_background", "background", operationAuthority, operation,
                cancellationToken, outputCaptureOwner);
        }

        public Task<string> JobAsync(
            SessionOperationAuthority operationAuthority,
            JobListOperation operation,
            CancellationToken cancellationToken) =>
            Complete("job_list", "jobs", operationAuthority, operation, cancellationToken);

        public Task<string> JobAsync(
            SessionOperationAuthority operationAuthority,
            JobStatusOperation operation,
            CancellationToken cancellationToken) =>
            Complete("job_status", "status", operationAuthority, operation, cancellationToken);

        public Task<string> JobAsync(
            SessionOperationAuthority operationAuthority,
            JobOutputOperation operation,
            CancellationToken cancellationToken) =>
            Complete("job_output", "output", operationAuthority, operation, cancellationToken);

        public Task<string> JobAsync(
            SessionOperationAuthority operationAuthority,
            JobKillOperation operation,
            CancellationToken cancellationToken) =>
            Complete("job_kill", "kill", operationAuthority, operation, cancellationToken);

        public Task<string> ResetAsync(
            SessionOperationAuthority operationAuthority,
            ResetOperation operation,
            CancellationToken cancellationToken) =>
            Complete("reset", "reset", operationAuthority, operation, cancellationToken);

        public Task ShutdownAsync()
        {
            ShutdownCalls++;
            LifetimeCalls.Add("shutdown");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            DisposeCalls++;
            LifetimeCalls.Add("dispose");
        }

        internal void ClearCalls()
        {
            Calls.Clear();
            Authorities.Clear();
        }

        private Task<string> Complete<TOperation>(
            string name,
            string text,
            SessionOperationAuthority authority,
            TOperation operation,
            CancellationToken cancellationToken,
            IExecutionOutputCaptureOwner? capture = null)
            where TOperation : GuardianHostOperation
        {
            cancellationToken.ThrowIfCancellationRequested();
            authority.RequireOperation(operation);
            OnEffect?.Invoke();
            Calls.Add(name);
            Authorities.Add(authority);
            if (EffectFailure is not null)
                throw EffectFailure;
            capture?.Dispose();
            return Task.FromResult(Results.TryDequeue(out var result) ? result! : text);
        }
    }
}
