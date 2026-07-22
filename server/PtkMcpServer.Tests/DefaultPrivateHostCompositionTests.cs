using System.Reflection;
using PtkMcpGuardian.Ownership;
using PtkMcpServer.GuardianHost;
using PtkMcpServer.Sessions;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class DefaultPrivateHostCompositionTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb"));
    private static readonly HostGeneration HostGeneration = new(17);
    private static readonly PrivateHostServerIdentity Identity = new(
        Guardian,
        Host,
        HostGeneration,
        hostPid: 4242);

    [Fact]
    public async Task Exact_ready_binding_forwards_frozen_inputs_without_public_id_authority()
    {
        var callTimeout = TimeSpan.FromSeconds(41);
        var maxCallTimeout = TimeSpan.FromSeconds(97);
        var executable = new JobPwshExecutable("frozen-pwsh");
        var session = new RecordingPrivateSession();
        RuntimeInputs? captured = null;
        var factory = new DefaultPrivateHostSessionFactory(
            callTimeout,
            maxCallTimeout,
            executable,
            (call, maximum, pwsh, allocator, token, allowColdBackground) =>
            {
                captured = new RuntimeInputs(
                    call,
                    maximum,
                    pwsh,
                    allocator,
                    token,
                    allowColdBackground);
                return session;
            });
        var initialization = Initialization(allowColdBackground: false);
        var binding = Assert.Single(initialization.Manifest.Bindings);
        using var cancellation = new CancellationTokenSource();

        var created = await factory.CreateAsync(
            initialization,
            binding,
            cancellation.Token);

        Assert.Same(session, created);
        var inputs = Assert.IsType<RuntimeInputs>(captured);
        Assert.Equal(callTimeout, inputs.CallTimeout);
        Assert.Equal(maxCallTimeout, inputs.MaxCallTimeout);
        Assert.Equal(executable, inputs.JobPwshExecutable);
        Assert.Equal(cancellation.Token, inputs.CancellationToken);
        Assert.False(inputs.AllowColdBackground);
        Assert.IsNotType<MonotonicPublicJobIdAllocator>(inputs.PublicJobIdAllocator);
        var failure = Assert.Throws<InvalidOperationException>(
            inputs.PublicJobIdAllocator.Allocate);
        Assert.Equal(
            "Private hosts cannot allocate public job identifiers; the guardian must reserve them.",
            failure.Message);
    }

    [Fact]
    public void Equal_but_foreign_binding_is_rejected_before_runtime_creation()
    {
        var initialization = Initialization(allowColdBackground: false);
        var binding = Assert.Single(initialization.Manifest.Bindings);
        var foreignBinding = new RecoveryBinding(
            binding.Alias,
            binding.BindingKind,
            binding.TemplateName,
            binding.TemplateDigest,
            binding.BootstrapDigest,
            binding.AllowColdBackground,
            binding.DesiredState,
            binding.TransitionVersion,
            binding.BindingDigest);
        Assert.Equal(binding, foreignBinding);
        Assert.NotSame(binding, foreignBinding);
        var createCalls = 0;
        var factory = Factory((_, _, _, _, _, _) =>
        {
            createCalls++;
            return new RecordingPrivateSession();
        });

        Assert.Throws<InvalidDataException>(() =>
        {
            _ = factory.CreateAsync(
                initialization,
                foreignBinding,
                CancellationToken.None);
        });

        Assert.Equal(0, createCalls);
    }

    [Fact]
    public void Nonready_exact_binding_is_rejected_before_runtime_creation()
    {
        var initialization = Initialization(
            allowColdBackground: true,
            desiredState: DesiredSessionState.Cold);
        var binding = Assert.Single(initialization.Manifest.Bindings);
        var createCalls = 0;
        var factory = Factory((_, _, _, _, _, _) =>
        {
            createCalls++;
            return new RecordingPrivateSession();
        });

        Assert.Throws<InvalidDataException>(() =>
        {
            _ = factory.CreateAsync(initialization, binding, CancellationToken.None);
        });

        Assert.Equal(0, createCalls);
    }

    [Fact]
    public void Cancellation_precedes_runtime_creation()
    {
        var initialization = Initialization();
        var binding = Assert.Single(initialization.Manifest.Bindings);
        var createCalls = 0;
        var factory = Factory((_, _, _, _, _, _) =>
        {
            createCalls++;
            return new RecordingPrivateSession();
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            _ = factory.CreateAsync(initialization, binding, cancellation.Token);
        });

        Assert.Equal(0, createCalls);
    }

    [Fact]
    public void Production_session_factory_targets_the_shared_default_runtime_constructor()
    {
        var factory = new DefaultPrivateHostSessionFactory(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            new JobPwshExecutable(null));
        var field = typeof(DefaultPrivateHostSessionFactory).GetField(
            "_runtimeFactory",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var runtimeFactory = Assert.IsType<PrivateSessionRuntimeFactory>(field.GetValue(factory));

        Assert.Equal(typeof(DefaultSessionRuntimeFactory), runtimeFactory.Method.DeclaringType);
        Assert.Equal("Create", runtimeFactory.Method.Name);
        Assert.Equal(
            [
                typeof(TimeSpan),
                typeof(TimeSpan),
                typeof(JobPwshExecutable),
                typeof(IPublicJobIdAllocator),
                typeof(CancellationToken),
                typeof(bool),
            ],
            runtimeFactory.Method.GetParameters()
                .Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void Production_composition_shares_one_event_sink_and_retains_no_pin_authority()
    {
        var events = new RecordingEventSink();

        var runtime = Assert.IsType<DefaultPrivateHostRuntime>(
            DefaultPrivateHostRuntimeFactory.Create(Identity, Pins(), events));
        var runtimeFields = typeof(DefaultPrivateHostRuntime).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        var runtimeEventSink = Assert.Single(
            runtimeFields,
            field => field.Name == "_eventSink");
        var outputTransferField = Assert.Single(
            runtimeFields,
            field => field.Name == "_outputTransfer");
        var sessionFactoryField = Assert.Single(
            runtimeFields,
            field => field.Name == "_sessionFactory");

        Assert.Same(events, runtimeEventSink.GetValue(runtime));
        Assert.IsType<DefaultPrivateHostSessionFactory>(sessionFactoryField.GetValue(runtime));
        var outputTransfer = Assert.IsType<EventPrivateHostOutputTransfer>(
            outputTransferField.GetValue(runtime));
        var transferEventSink = typeof(EventPrivateHostOutputTransfer).GetField(
            "_eventSink",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Same(events, transferEventSink.GetValue(outputTransfer));
        Assert.DoesNotContain(
            runtimeFields,
            field => field.FieldType == typeof(PrivateHostServerPins));
    }

    private static DefaultPrivateHostSessionFactory Factory(
        PrivateSessionRuntimeFactory runtimeFactory) =>
        new(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            new JobPwshExecutable(null),
            runtimeFactory);

    private static PrivateHostInitialization Initialization(
        bool allowColdBackground = true,
        DesiredSessionState desiredState = DesiredSessionState.Ready)
    {
        var binding = new RecoveryBinding(
            new CanonicalAlias("default"),
            RecoveryBindingKind.Default,
            templateName: null,
            templateDigest: null,
            bootstrapDigest: null,
            allowColdBackground,
            desiredState,
            new SessionTransitionVersion(3),
            Digest('5'));
        var manifest = new RecoveryManifest(
            Guardian,
            HostGeneration,
            Digest('3'),
            Digest('4'),
            [],
            [binding],
            [
                new WorkerGenerationHighWatermarkEntry(
                    binding.Alias,
                    new WorkerGenerationHighWatermark(9)),
            ],
            HostGeneration);
        return new PrivateHostInitialization(
            manifest,
            new PrivateRequestId(1),
            new ManifestId(Guid.Parse("11111111-1111-4111-8111-111111111111")),
            Sha256Digest.Compute(RecoveryManifestCodec.Encode(manifest)));
    }

    private static PrivateHostServerPins Pins() => new(
        Digest('1'),
        Digest('2'),
        Digest('3'),
        Digest('4'),
        Digest('5'));

    private static Sha256Digest Digest(char value) => new(new string(value, 64));

    private sealed record RuntimeInputs(
        TimeSpan CallTimeout,
        TimeSpan MaxCallTimeout,
        JobPwshExecutable JobPwshExecutable,
        IPublicJobIdAllocator PublicJobIdAllocator,
        CancellationToken CancellationToken,
        bool AllowColdBackground);

    private sealed class RecordingEventSink : IPrivateHostEventSink
    {
        public ValueTask WriteEventAsync(
            Func<HostEventSequence, GuardianHostEvent> createEvent,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class RecordingPrivateSession : IPrivateSessionOperations
    {
        public Task<string> InvokeAsync(
            SessionOperationAuthority operationAuthority,
            InvokeForegroundOperation operation,
            CancellationToken cancellationToken,
            IExecutionOutputCaptureOwner outputCaptureOwner) =>
            throw new NotSupportedException();

        public Task<string> InvokeAsync(
            SessionOperationAuthority operationAuthority,
            InvokeBackgroundOperation operation,
            CancellationToken cancellationToken,
            Func<JobSnapshot, Task> onTerminal,
            IExecutionOutputCaptureOwner outputCaptureOwner) =>
            throw new NotSupportedException();

        public Task<string> JobAsync(
            SessionOperationAuthority operationAuthority,
            JobListOperation operation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> JobAsync(
            SessionOperationAuthority operationAuthority,
            JobStatusOperation operation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> JobAsync(
            SessionOperationAuthority operationAuthority,
            JobOutputOperation operation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> JobAsync(
            SessionOperationAuthority operationAuthority,
            JobKillOperation operation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> ResetAsync(
            SessionOperationAuthority operationAuthority,
            ResetOperation operation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ShutdownAsync() => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
