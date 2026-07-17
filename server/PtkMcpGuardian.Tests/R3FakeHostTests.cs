using System.Collections.Concurrent;
using System.Security.Cryptography;
using PtkMcpGuardian.Host;
using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Standalone;
using PtkMcpGuardian.Standalone.Fake;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class R3FakeHostTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly GuardianBootId OtherGuardian = new(
        Guid.Parse("99999999-9999-4999-8999-999999999999"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly HostBootId OtherHost = new(
        Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));
    private static readonly HostGeneration Generation = new(1);
    private static readonly ManifestId Manifest = new(
        Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"));
    private static readonly CanonicalAlias Alias = new("default");
    private static readonly SessionTransitionVersion Transition = new(1);
    private static readonly GuardianHostWorkerIdentity Worker = new(
        new WorkerBootId(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")),
        new WorkerGeneration(1));
    private static readonly CallId Call = new(
        Guid.Parse("77777777-7777-7777-8777-777777777777"));
    private static readonly Sha256Digest ExecutableDigest = Digest('1');
    private static readonly Sha256Digest BuildDigest = Digest('2');
    private static readonly Sha256Digest PublicContractDigest = Digest('3');
    private static readonly Sha256Digest ConfigurationDigest = Digest('4');
    private static readonly Sha256Digest CatalogDigest = Digest('5');
    private static readonly Sha256Digest PackageManifestDigest = Digest('6');
    private static readonly Sha256Digest BindingDigest = Digest('7');
    private static readonly CapabilityToken DispatchToken = Capability(1);

    [Fact]
    public async Task Bounded_stream_never_buffers_more_than_its_capacity()
    {
        using var stream = new R3BoundedOneWayStream(capacity: 1);
        await stream.WriteAsync(new byte[] { 1, 2, 3 });

        var secondWrite = stream.WriteAsync(new byte[] { 4, 5, 6 }).AsTask();
        await Task.Delay(50);

        Assert.False(secondWrite.IsCompleted);
        Assert.Equal(1, stream.BufferedChunkCount);
        var first = new byte[3];
        Assert.Equal(3, await stream.ReadAsync(first).AsTask().WaitAsync(TestTimeout));
        await secondWrite.WaitAsync(TestTimeout);
        Assert.Equal([1, 2, 3], first);

        var second = new byte[3];
        Assert.Equal(3, await stream.ReadAsync(second).AsTask().WaitAsync(TestTimeout));
        Assert.Equal([4, 5, 6], second);
        Assert.Equal(0, stream.BufferedChunkCount);
        Assert.Equal(1, stream.MaximumBufferedChunkCount);
    }

    [Fact]
    public async Task Bounded_stream_zeroes_owned_buffers_on_consume_and_dispose()
    {
        var retired = new ConcurrentQueue<byte[]>();
        using var stream = new R3BoundedOneWayStream(
            capacity: 2,
            buffer => retired.Enqueue(buffer.ToArray()));

        await stream.WriteAsync(new byte[] { 1, 2, 3, 4 });
        var consumed = new byte[4];
        Assert.Equal(4, await stream.ReadAsync(consumed));
        await stream.WriteAsync(new byte[] { 5, 6, 7, 8 });
        stream.Dispose();

        Assert.Equal([1, 2, 3, 4], consumed);
        Assert.Equal(2, retired.Count);
        Assert.All(retired, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
        Assert.True(stream.IsDisposed);
        Assert.Equal(0, stream.BufferedChunkCount);
    }

    [Fact]
    public async Task Armed_write_barrier_can_fail_before_any_bytes_are_accepted()
    {
        using var stream = new R3BoundedOneWayStream(capacity: 1);
        var barrier = stream.ArmNextWrite(failAfterRelease: true);

        var write = stream.WriteAsync(new byte[] { 1 }).AsTask();
        await barrier.Reached.WaitAsync(TestTimeout);
        Assert.False(write.IsCompleted);
        Assert.Equal(0, stream.BufferedChunkCount);

        barrier.Release();
        await Assert.ThrowsAsync<IOException>(() => write);
        Assert.Equal(0, stream.BufferedChunkCount);
    }

    [Fact]
    public async Task Fake_peer_completes_strict_handshake_job_list_and_shutdown()
    {
        var operation = new R3FakeHostOperationPlan { ResponseText = "[\"job-1\"]" };
        var control = new R3FakeHostControl();
        control.EnqueueOperation(operation);
        var (_, resources) = CreateAttempt(control: control);
        Assert.Equal(GuardianHostLaunchOutcome.Started, resources.Launch());
        await using var client = CreateClient(resources);

        await client.InitializeAsync(CreateManifest()).WaitAsync(TestTimeout);
        var response = await client.SendRequestAsync(CreateJobListRequest)
            .WaitAsync(TestTimeout);
        var completed = Assert.IsType<OperationCompleted>(
            Assert.IsType<GuardianHostSuccessResponse>(response).Payload);
        Assert.Equal("[\"job-1\"]", Assert.IsType<JobListResult>(completed.Result).Text);
        Assert.Equal(1, resources.Peer.JobListEffectCount);
        Assert.True(operation.Received.IsReached);
        Assert.True(operation.ResponseSent.IsReached);

        await client.ShutdownAsync(
            FutureDeadline(),
            GuardianHostShutdownReason.GuardianShutdown).WaitAsync(TestTimeout);
        await resources.HostExited.WaitAsync(TestTimeout);
        Assert.Null(resources.PeerFailure);
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], resources.Peer.ReceivedRequestIds);
        resources.Dispose();
    }

    [Fact]
    public async Task Operation_barrier_makes_post_receive_crash_deterministic_and_single_effect()
    {
        var operation = new R3FakeHostOperationPlan
        {
            Behavior = R3FakeHostOperationBehavior.HoldBeforeResponse,
        };
        var control = new R3FakeHostControl();
        control.EnqueueOperation(operation);
        var (_, resources) = CreateAttempt(control: control);
        Assert.Equal(GuardianHostLaunchOutcome.Started, resources.Launch());
        await using var client = CreateClient(resources);
        await client.InitializeAsync(CreateManifest()).WaitAsync(TestTimeout);

        var pending = client.SendRequestAsync(CreateJobListRequest);
        await operation.Received.Reached.WaitAsync(TestTimeout);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, resources.Peer.JobListEffectCount);
        Assert.False(operation.ResponseSent.IsReached);

        resources.Crash();
        await resources.HostExited.WaitAsync(TestTimeout);
        await Assert.ThrowsAnyAsync<IOException>(() => pending.WaitAsync(TestTimeout));
        Assert.Equal(1, resources.Peer.JobListEffectCount);
        Assert.False(operation.ResponseSent.IsReached);

        resources.BeginContainment(new GuardianHostContainmentDeadline(1, 2));
        await resources.ContainmentConfirmed.WaitAsync(TestTimeout);
        operation.BeforeResponse.Release();
        resources.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Fake_peer_rejects_every_wrong_job_list_dispatch_identity_before_effect(
        int mutation)
    {
        var (_, resources) = CreateAttempt();
        Assert.Equal(GuardianHostLaunchOutcome.Started, resources.Launch());
        await using var client = CreateClient(resources);
        await client.InitializeAsync(CreateManifest()).WaitAsync(TestTimeout);

        var request = client.SendRequestAsync((guardian, host, generation, requestId) =>
        {
            var transition = mutation == 0
                ? new SessionTransitionVersion(Transition.Value + 1)
                : Transition;
            var worker = mutation switch
            {
                1 => new GuardianHostWorkerIdentity(
                    new WorkerBootId(Guid.Parse(
                        "12345678-1234-4234-8234-123456789abc")),
                    Worker.Generation),
                2 => new GuardianHostWorkerIdentity(
                    Worker.BootId,
                    new WorkerGeneration(Worker.Generation.Value + 1)),
                _ => Worker,
            };
            return CreateJobListRequest(
                guardian,
                host,
                generation,
                requestId,
                transition,
                worker);
        });

        await Assert.ThrowsAnyAsync<IOException>(() =>
            request.WaitAsync(TestTimeout));
        Assert.Equal(0, resources.Peer.JobListEffectCount);
        resources.BeginContainment(new GuardianHostContainmentDeadline(1, 2));
        await resources.ContainmentConfirmed.WaitAsync(TestTimeout);
        resources.Dispose();
    }

    [Fact]
    public async Task Attempt_control_distinguishes_no_child_from_ambiguous_launch()
    {
        var control = new R3FakeHostControl();
        var factory = new R3FakeHostAttemptFactory(CreateSupervisorPins(), control);
        control.EnqueueAttempt(new R3FakeHostAttemptPlan { FailPrepare = true });

        Assert.Throws<IOException>(() => factory.Prepare(
            new GuardianHostIdentity(Guardian, Host, Generation),
            new GuardianHostStartupDeadline(1)));
        Assert.Empty(factory.Attempts);

        control.EnqueueAttempt(new R3FakeHostAttemptPlan
        {
            LaunchOutcome = GuardianHostLaunchOutcome.ProvedNoChild,
        });
        var noChild = Assert.IsType<R3FakeHostAttemptResources>(factory.Prepare(
            new GuardianHostIdentity(Guardian, Host, Generation),
            new GuardianHostStartupDeadline(2)));
        Assert.Equal(GuardianHostLaunchOutcome.ProvedNoChild, noChild.Launch());
        await noChild.HostExited.WaitAsync(TestTimeout);
        Assert.Null(noChild.PeerFailure);
        noChild.BeginContainment(new GuardianHostContainmentDeadline(1, 2));
        await noChild.ContainmentConfirmed.WaitAsync(TestTimeout);
        noChild.Dispose();

        control.EnqueueAttempt(new R3FakeHostAttemptPlan { ThrowOnLaunch = true });
        var ambiguous = Assert.IsType<R3FakeHostAttemptResources>(factory.Prepare(
            new GuardianHostIdentity(
                Guardian,
                new HostBootId(Guid.Parse("12345678-1234-4234-8234-123456789abc")),
                new HostGeneration(2)),
            new GuardianHostStartupDeadline(3)));
        Assert.Throws<IOException>(() => ambiguous.Launch());
        ambiguous.BeginContainment(new GuardianHostContainmentDeadline(1, 2));
        await ambiguous.HostExited.WaitAsync(TestTimeout);
        await ambiguous.ContainmentConfirmed.WaitAsync(TestTimeout);
        Assert.Equal(1, ambiguous.TransportCloseCount);
        ambiguous.Dispose();

        Assert.Equal(2, factory.Attempts.Count);
        Assert.NotEqual(noChild.HostProcessId, ambiguous.HostProcessId);
    }

    [Fact]
    public async Task Fake_peer_rejects_initialize_with_wrong_outer_identity()
    {
        var (_, resources) = CreateAttempt();
        Assert.Equal(GuardianHostLaunchOutcome.Started, resources.Launch());
        var reader = new GuardianHostProtocolReader(
            resources.EventStream,
            GuardianHostPeer.Host);
        var writer = new GuardianHostProtocolWriter(
            resources.RequestStream,
            GuardianHostPeer.Guardian);
        _ = Assert.IsType<GuardianHostHello>(
            await reader.ReadAsync().AsTask().WaitAsync(TestTimeout));

        await writer.WriteAsync(new GuardianHostInitialize(
            Guardian,
            OtherHost,
            Generation,
            new PrivateRequestId(1),
            ExecutableDigest,
            BuildDigest,
            PublicContractDigest,
            ConfigurationDigest,
            PackageManifestDigest));

        await resources.HostExited.WaitAsync(TestTimeout);
        Assert.IsType<InvalidDataException>(resources.PeerFailure);
        Assert.Empty(resources.Peer.ReceivedRequestIds);
        resources.Dispose();
    }

    [Fact]
    public async Task Fake_peer_rejects_manifest_content_that_does_not_match_attempt_identity()
    {
        var (_, resources) = CreateAttempt();
        Assert.Equal(GuardianHostLaunchOutcome.Started, resources.Launch());
        var reader = new GuardianHostProtocolReader(
            resources.EventStream,
            GuardianHostPeer.Host);
        var writer = new GuardianHostProtocolWriter(
            resources.RequestStream,
            GuardianHostPeer.Guardian);
        _ = Assert.IsType<GuardianHostHello>(
            await reader.ReadAsync().AsTask().WaitAsync(TestTimeout));

        await writer.WriteAsync(CreateInitialize(new PrivateRequestId(1)));
        var wrongManifest = CreateManifest(guardian: OtherGuardian);
        await SendManifestThroughSealAsync(writer, reader, wrongManifest);

        await resources.HostExited.WaitAsync(TestTimeout);
        Assert.IsType<InvalidDataException>(resources.PeerFailure);
        resources.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Fake_peer_rejects_every_mutable_default_manifest_profile_field(
        int mutation)
    {
        var (_, resources) = CreateAttempt();
        Assert.Equal(GuardianHostLaunchOutcome.Started, resources.Launch());
        await using var client = CreateClient(resources);
        var manifest = CreateManifest(
            allowColdBackground: mutation == 0,
            desiredState: mutation == 1
                ? DesiredSessionState.Cold
                : DesiredSessionState.Ready,
            transition: mutation == 2
                ? new SessionTransitionVersion(Transition.Value + 1)
                : Transition,
            bindingDigest: mutation == 3 ? Digest('8') : BindingDigest,
            watermark: mutation == 4
                ? new WorkerGenerationHighWatermark(Worker.Generation.Value + 1)
                : new WorkerGenerationHighWatermark(Worker.Generation.Value));

        await Assert.ThrowsAnyAsync<IOException>(() =>
            client.InitializeAsync(manifest).WaitAsync(TestTimeout));
        await resources.HostExited.WaitAsync(TestTimeout);
        Assert.IsType<InvalidDataException>(resources.PeerFailure);
        resources.Dispose();
    }

    [Fact]
    public async Task Containment_is_idempotent_and_proof_waits_for_exit_zeroing_and_barrier()
    {
        var retired = new ConcurrentQueue<byte[]>();
        var control = new R3FakeHostControl();
        control.EnqueueAttempt(new R3FakeHostAttemptPlan { HoldContainmentProof = true });
        var (_, resources) = CreateAttempt(
            control,
            buffer => retired.Enqueue(buffer.ToArray()));
        Assert.Equal(GuardianHostLaunchOutcome.Started, resources.Launch());

        resources.Crash();
        resources.Crash();
        await resources.HostExited.WaitAsync(TestTimeout);
        resources.BeginContainment(new GuardianHostContainmentDeadline(1, 2));
        resources.BeginContainment(new GuardianHostContainmentDeadline(1, 2));
        await resources.ContainmentProofBarrier.Reached.WaitAsync(TestTimeout);

        Assert.False(resources.ContainmentConfirmed.IsCompleted);
        Assert.True(resources.RequestTransport.IsDisposed);
        Assert.True(resources.EventTransport.IsDisposed);
        Assert.NotEmpty(retired);
        Assert.All(retired, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
        Assert.Equal(1, resources.CrashCount);
        Assert.Equal(1, resources.TransportCloseCount);
        Assert.Equal(1, resources.ContainmentStartCount);

        resources.ContainmentProofBarrier.Release();
        await resources.ContainmentConfirmed.WaitAsync(TestTimeout);
        resources.Dispose();
        resources.Dispose();
        Assert.Equal(1, resources.DisposeCount);
    }

    private static async Task SendManifestThroughSealAsync(
        GuardianHostProtocolWriter writer,
        GuardianHostProtocolReader reader,
        RecoveryManifest manifest)
    {
        var bytes = RecoveryManifestCodec.Encode(manifest);
        try
        {
            var digest = Sha256Digest.Compute(bytes);
            var requestId = 2L;
            var header = new ManifestHeaderRequest(
                Guardian,
                Host,
                Generation,
                new PrivateRequestId(requestId++),
                Manifest,
                bytes.Length,
                digest,
                manifest.Bindings.Count,
                manifest.Templates.Count);
            await writer.WriteAsync(header);
            var headerResponse = Assert.IsType<GuardianHostSuccessResponse>(
                await reader.ReadAsync().AsTask().WaitAsync(TestTimeout));
            Assert.IsType<ManifestHeaderAccepted>(headerResponse.Payload);

            var offset = 0;
            var index = 0;
            while (offset < bytes.Length)
            {
                var length = Math.Min(
                    ContractLimits.MaximumManifestChunkBytes,
                    bytes.Length - offset);
                using var chunk = new ManifestChunkRequest(
                    Guardian,
                    Host,
                    Generation,
                    new PrivateRequestId(requestId++),
                    Manifest,
                    index,
                    bytes.AsMemory(offset, length));
                await writer.WriteAsync(chunk);
                var chunkResponse = Assert.IsType<GuardianHostSuccessResponse>(
                    await reader.ReadAsync().AsTask().WaitAsync(TestTimeout));
                Assert.IsType<ManifestChunkAccepted>(chunkResponse.Payload);
                offset += length;
                index++;
            }

            await writer.WriteAsync(new ManifestSealRequest(
                Guardian,
                Host,
                Generation,
                new PrivateRequestId(requestId),
                Manifest,
                bytes.Length,
                digest));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static GuardianHostClient CreateClient(R3FakeHostAttemptResources resources) => new(
        resources.RequestStream,
        resources.EventStream,
        new GuardianHostClientPins(
            Guardian,
            Host,
            Generation,
            resources.HostProcessId,
            ExecutableDigest,
            BuildDigest,
            PublicContractDigest,
            ConfigurationDigest,
            CatalogDigest,
            PackageManifestDigest),
        new MonotonicPrivateRequestIdAllocator(),
        TimeProvider.System,
        static () => NoopLease.Instance,
        static _ => NoopLease.Instance,
        static (_, _) => NoopLease.Instance,
        static (_, _) => ValueTask.CompletedTask,
        static (_, _, _) => ValueTask.CompletedTask,
        static () => Manifest);

    private static OperationRequest CreateJobListRequest(
        GuardianBootId guardian,
        HostBootId host,
        HostGeneration generation,
        PrivateRequestId requestId) => CreateJobListRequest(
            guardian,
            host,
            generation,
            requestId,
            Transition,
            Worker);

    private static OperationRequest CreateJobListRequest(
        GuardianBootId guardian,
        HostBootId host,
        HostGeneration generation,
        PrivateRequestId requestId,
        SessionTransitionVersion transition,
        GuardianHostWorkerIdentity workerIdentity) => new(
            guardian,
            host,
            generation,
            requestId,
            FutureDeadline(),
            Alias,
            transition,
            workerIdentity,
            null,
            new JobListOperation(
                Call,
                new DispatchCapability(DispatchToken, Call, FutureDeadline())));

    private static GuardianHostInitialize CreateInitialize(PrivateRequestId requestId) => new(
        Guardian,
        Host,
        Generation,
        requestId,
        ExecutableDigest,
        BuildDigest,
        PublicContractDigest,
        ConfigurationDigest,
        PackageManifestDigest);

    private static (
        R3FakeHostAttemptFactory Factory,
        R3FakeHostAttemptResources Resources) CreateAttempt(
            R3FakeHostControl? control = null,
            Action<byte[]>? retiredBufferObserver = null)
    {
        var factory = new R3FakeHostAttemptFactory(
            CreateSupervisorPins(),
            control,
            retiredBufferObserver: retiredBufferObserver);
        var resources = Assert.IsType<R3FakeHostAttemptResources>(factory.Prepare(
            new GuardianHostIdentity(Guardian, Host, Generation),
            new GuardianHostStartupDeadline(1)));
        return (factory, resources);
    }

    private static GuardianHostSupervisorPins CreateSupervisorPins() => new(
        ExecutableDigest,
        BuildDigest,
        PublicContractDigest,
        ConfigurationDigest,
        CatalogDigest,
        PackageManifestDigest);

    private static RecoveryManifest CreateManifest(
        GuardianBootId? guardian = null,
        bool allowColdBackground = false,
        DesiredSessionState desiredState = DesiredSessionState.Ready,
        SessionTransitionVersion? transition = null,
        Sha256Digest? bindingDigest = null,
        WorkerGenerationHighWatermark? watermark = null) => new(
        guardian ?? Guardian,
        Generation,
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
                allowColdBackground,
                desiredState,
                transition ?? Transition,
                bindingDigest ?? BindingDigest),
        ],
        [new WorkerGenerationHighWatermarkEntry(
            Alias,
            watermark ?? new WorkerGenerationHighWatermark(Worker.Generation.Value))],
        Generation);

    private static Sha256Digest Digest(char value) => new(new string(value, 64));

    private static long FutureDeadline() =>
        TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds() + 5_000;

    private static CapabilityToken Capability(byte value)
    {
        var bytes = Enumerable.Repeat(value, ContractLimits.CapabilityTokenBytes).ToArray();
        return new CapabilityToken(Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'));
    }

    private sealed class NoopLease : IDisposable
    {
        internal static readonly NoopLease Instance = new();

        public void Dispose() { }
    }
}
