using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PtkMcpServer.GuardianHost;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class PrivateHostServerTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly HostGeneration Generation = new(7);
    private static readonly ManifestId ManifestId = new(
        Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));
    private static readonly Sha256Digest ExecutableDigest = Digest('1');
    private static readonly Sha256Digest BuildDigest = Digest('2');
    private static readonly Sha256Digest ContractDigest = Digest('3');
    private static readonly Sha256Digest ConfigurationDigest = Digest('4');
    private static readonly Sha256Digest PackageDigest = Digest('5');
    private static readonly Sha256Digest CatalogDigest = Digest('6');
    private static readonly CanonicalAlias Alias = new("default");
    private static readonly SessionTransitionVersion Transition = new(1);
    private static readonly GuardianHostWorkerIdentity Worker = new(
        new WorkerBootId(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")),
        new WorkerGeneration(1));
    private static readonly CallId Call = new(
        Guid.Parse("eeeeeeee-eeee-7eee-8eee-eeeeeeeeeeee"));
    private static readonly CapabilityToken DispatchToken = Capability(1);

    [Fact]
    public async Task Hello_manifest_responses_and_ready_follow_the_exact_single_use_sequence()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        byte[]? capturedBuffer = null;
        var server = NewServer(
            guardianToHost,
            hostToGuardian,
            length => capturedBuffer = new byte[length]);
        var guardianWriter = new GuardianHostProtocolWriter(
            guardianToHost,
            GuardianHostPeer.Guardian);
        var guardianReader = new GuardianHostProtocolReader(
            hostToGuardian,
            GuardianHostPeer.Host);
        var manifest = NewManifest();
        var encodedManifest = RecoveryManifestCodec.Encode(manifest);
        var manifestDigest = Sha256Digest.Compute(encodedManifest);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var initialization = server.InitializeAsync(timeout.Token).AsTask();
        var hello = Assert.IsType<GuardianHostHello>(await guardianReader.ReadAsync());
        AssertIdentity(hello);
        Assert.Equal(4242, hello.HostPid);
        Assert.Equal(ExecutableDigest, hello.HostExecutableDigest);
        Assert.Equal(BuildDigest, hello.HostBuildDigest);
        Assert.Equal(ContractDigest, hello.PublicContractDigest);
        Assert.Equal(ConfigurationDigest, hello.ConfigurationDigest);
        Assert.Equal(PrivateHostServerState.HelloSent, server.State);

        await guardianWriter.WriteAsync(Initialize(requestId: 10));
        await guardianWriter.WriteAsync(new ManifestHeaderRequest(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(11),
            ManifestId,
            encodedManifest.Length,
            manifestDigest,
            aliasCount: 1,
            templateCount: 0));
        var headerAccepted = AssertSuccess<ManifestHeaderAccepted>(
            await guardianReader.ReadAsync(),
            requestId: 11);
        Assert.Equal(ManifestId, headerAccepted.ManifestId);
        Assert.Equal(PrivateHostServerState.ReceivingManifest, server.State);

        using (var chunk = new ManifestChunkRequest(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(12),
            ManifestId,
            chunkIndex: 0,
            encodedManifest))
        {
            await guardianWriter.WriteAsync(chunk);
        }
        var chunkAccepted = AssertSuccess<ManifestChunkAccepted>(
            await guardianReader.ReadAsync(),
            requestId: 12);
        Assert.Equal(encodedManifest.Length, chunkAccepted.NextOffset);

        await guardianWriter.WriteAsync(new ManifestSealRequest(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(13),
            ManifestId,
            encodedManifest.Length,
            manifestDigest));
        var sealedPayload = AssertSuccess<ManifestSealed>(
            await guardianReader.ReadAsync(),
            requestId: 13);
        Assert.Equal(manifestDigest, sealedPayload.ManifestDigest);
        var ready = Assert.IsType<GuardianHostReady>(await guardianReader.ReadAsync());
        AssertIdentity(ready);
        Assert.Equal(new PrivateRequestId(10), ready.InitializeRequestId);
        Assert.Equal(ManifestId, ready.ManifestId);
        Assert.Equal(manifestDigest, ready.ManifestDigest);
        Assert.Equal(4242, ready.HostPid);

        var result = await initialization;
        Assert.Equal(encodedManifest, RecoveryManifestCodec.Encode(result.Manifest));
        Assert.Equal(new PrivateRequestId(10), result.InitializeRequestId);
        Assert.Equal(ManifestId, result.ManifestId);
        Assert.Equal(manifestDigest, result.ManifestDigest);
        Assert.Equal(PrivateHostServerState.Ready, server.State);
        Assert.NotNull(capturedBuffer);
        Assert.All(capturedBuffer, value => Assert.Equal(0, value));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.InitializeAsync().AsTask());
        Assert.Equal(PrivateHostServerState.Ready, server.State);
    }

    [Fact]
    public async Task Identity_and_pin_mismatches_fail_the_generation_before_manifest_allocation()
    {
        await AssertInitializeFailure(
            new GuardianHostInitialize(
                new GuardianBootId(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")),
                Host,
                Generation,
                new PrivateRequestId(1),
                ExecutableDigest,
                BuildDigest,
                ContractDigest,
                ConfigurationDigest,
                PackageDigest),
            "identity_mismatch");

        await AssertInitializeFailure(
            new GuardianHostInitialize(
                Guardian,
                Host,
                Generation,
                new PrivateRequestId(1),
                Digest('f'),
                BuildDigest,
                ContractDigest,
                ConfigurationDigest,
                PackageDigest),
            "initialize_pin_mismatch");
    }

    [Fact]
    public async Task Request_ids_must_increase_strictly_across_initialize_and_manifest_frames()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var server = NewServer(guardianToHost, hostToGuardian);
        var writer = new GuardianHostProtocolWriter(
            guardianToHost,
            GuardianHostPeer.Guardian);
        var reader = new GuardianHostProtocolReader(
            hostToGuardian,
            GuardianHostPeer.Host);
        var manifest = RecoveryManifestCodec.Encode(NewManifest());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var initialization = server.InitializeAsync(timeout.Token).AsTask();
        _ = Assert.IsType<GuardianHostHello>(await reader.ReadAsync());
        await writer.WriteAsync(Initialize(requestId: 8));
        await writer.WriteAsync(new ManifestHeaderRequest(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(8),
            ManifestId,
            manifest.Length,
            Sha256Digest.Compute(manifest),
            aliasCount: 1,
            templateCount: 0));
        guardianToHost.CompleteWriting();

        var failure = await Assert.ThrowsAsync<GuardianHostProtocolException>(
            async () => await initialization);
        Assert.Equal("request_id_not_increasing", failure.DetailCode);
        Assert.Equal(PrivateHostServerState.Faulted, server.State);
    }

    [Fact]
    public async Task Wrong_chunk_order_fails_closed_and_zeroes_the_transfer_buffer()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        byte[]? capturedBuffer = null;
        var server = NewServer(
            guardianToHost,
            hostToGuardian,
            length => capturedBuffer = Enumerable.Repeat((byte)0xa5, length).ToArray());
        var writer = new GuardianHostProtocolWriter(
            guardianToHost,
            GuardianHostPeer.Guardian);
        var reader = new GuardianHostProtocolReader(
            hostToGuardian,
            GuardianHostPeer.Host);
        var manifest = RecoveryManifestCodec.Encode(NewManifest());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var initialization = server.InitializeAsync(timeout.Token).AsTask();
        _ = Assert.IsType<GuardianHostHello>(await reader.ReadAsync());
        await writer.WriteAsync(Initialize(requestId: 1));
        await writer.WriteAsync(new ManifestHeaderRequest(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(2),
            ManifestId,
            manifest.Length,
            Sha256Digest.Compute(manifest),
            aliasCount: 1,
            templateCount: 0));
        _ = AssertSuccess<ManifestHeaderAccepted>(await reader.ReadAsync(), requestId: 2);

        using (var wrongChunk = new ManifestChunkRequest(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(3),
            ManifestId,
            chunkIndex: 1,
            manifest))
        {
            await writer.WriteAsync(wrongChunk);
        }

        var failure = await Assert.ThrowsAsync<GuardianHostProtocolException>(
            async () => await initialization);
        Assert.Equal("manifest_chunk_mismatch", failure.DetailCode);
        Assert.Equal(PrivateHostServerState.Faulted, server.State);
        Assert.NotNull(capturedBuffer);
        Assert.All(capturedBuffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Seal_digest_mismatch_fails_closed_and_zeroes_completed_transfer_bytes()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        byte[]? capturedBuffer = null;
        var server = NewServer(
            guardianToHost,
            hostToGuardian,
            length => capturedBuffer = new byte[length]);
        var writer = new GuardianHostProtocolWriter(
            guardianToHost,
            GuardianHostPeer.Guardian);
        var reader = new GuardianHostProtocolReader(
            hostToGuardian,
            GuardianHostPeer.Host);
        var manifest = RecoveryManifestCodec.Encode(NewManifest());
        var manifestDigest = Sha256Digest.Compute(manifest);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var initialization = server.InitializeAsync(timeout.Token).AsTask();
        _ = Assert.IsType<GuardianHostHello>(await reader.ReadAsync(timeout.Token));
        await writer.WriteAsync(Initialize(requestId: 1), timeout.Token);
        await writer.WriteAsync(new ManifestHeaderRequest(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(2),
            ManifestId,
            manifest.Length,
            manifestDigest,
            aliasCount: 1,
            templateCount: 0), timeout.Token);
        _ = AssertSuccess<ManifestHeaderAccepted>(
            await reader.ReadAsync(timeout.Token),
            requestId: 2);
        using (var chunk = new ManifestChunkRequest(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(3),
            ManifestId,
            chunkIndex: 0,
            manifest))
        {
            await writer.WriteAsync(chunk, timeout.Token);
        }
        _ = AssertSuccess<ManifestChunkAccepted>(
            await reader.ReadAsync(timeout.Token),
            requestId: 3);

        await writer.WriteAsync(new ManifestSealRequest(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(4),
            ManifestId,
            manifest.Length,
            Digest('e')), timeout.Token);

        var failure = await Assert.ThrowsAsync<GuardianHostProtocolException>(
            async () => await initialization);
        Assert.Equal("manifest_seal_mismatch", failure.DetailCode);
        Assert.Equal(PrivateHostServerState.Faulted, server.State);
        Assert.NotNull(capturedBuffer);
        Assert.All(capturedBuffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Eof_and_hello_writer_failure_are_terminal_and_never_retry_initialization()
    {
        using (var guardianToHost = new ChannelStream())
        using (var hostToGuardian = new ChannelStream())
        {
            var server = NewServer(guardianToHost, hostToGuardian);
            var reader = new GuardianHostProtocolReader(
                hostToGuardian,
                GuardianHostPeer.Host);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var initialization = server.InitializeAsync(timeout.Token).AsTask();
            _ = Assert.IsType<GuardianHostHello>(await reader.ReadAsync());
            guardianToHost.CompleteWriting();

            var failure = await Assert.ThrowsAsync<GuardianHostProtocolException>(
                async () => await initialization);
            Assert.Equal("unexpected_eof", failure.DetailCode);
            Assert.Equal(PrivateHostServerState.Faulted, server.State);
        }

        using (var guardianToHost = new ChannelStream())
        using (var failingOutput = new ThrowingWriteStream())
        {
            var server = NewServer(guardianToHost, failingOutput);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<IOException>(() =>
                server.InitializeAsync(timeout.Token).AsTask());
            Assert.Equal(PrivateHostServerState.Faulted, server.State);
        }
    }

    [Fact]
    public void Rejected_raw_initialization_frames_are_disposed()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var server = NewServer(guardianToHost, hostToGuardian);
        var rawBytes = Enumerable.Repeat((byte)0xa5, 32).ToArray();
        var wrongStage = new ManifestChunkRequest(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(1),
            ManifestId,
            chunkIndex: 0,
            rawBytes);

        var wrongStageFailure = Assert.Throws<GuardianHostProtocolException>(() =>
            server.RequireMessage<ManifestHeaderRequest>(
                wrongStage,
                "manifest_header_required"));

        Assert.Equal("manifest_header_required", wrongStageFailure.DetailCode);
        Assert.Throws<ObjectDisposedException>(() => wrongStage.GetRawBytes());

        var wrongIdentity = new ManifestChunkRequest(
            new GuardianBootId(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")),
            Host,
            Generation,
            new PrivateRequestId(2),
            ManifestId,
            chunkIndex: 0,
            rawBytes);

        var wrongIdentityFailure = Assert.Throws<GuardianHostProtocolException>(() =>
            server.RequireMessage<ManifestChunkRequest>(
                wrongIdentity,
                "manifest_chunk_required"));

        Assert.Equal("identity_mismatch", wrongIdentityFailure.DetailCode);
        Assert.Throws<ObjectDisposedException>(() => wrongIdentity.GetRawBytes());
    }

    [Fact]
    public async Task Ready_is_withheld_until_runtime_initialization_completes()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var entered = NewSignal();
        var release = NewSignal();
        var runtime = new DelegatingPrivateHostRuntime(
            initialize: async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            });
        var server = NewServer(guardianToHost, hostToGuardian, runtime: runtime);
        var writer = new GuardianHostProtocolWriter(
            guardianToHost,
            GuardianHostPeer.Guardian);
        var reader = new GuardianHostProtocolReader(
            hostToGuardian,
            GuardianHostPeer.Host);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var initialization = server.InitializeAsync(timeout.Token).AsTask();
        await TransferInitializationThroughSealAsync(writer, reader, timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);

        Assert.Equal(PrivateHostServerState.InitializingRuntime, server.State);
        Assert.False(hostToGuardian.HasPendingWrite);

        release.TrySetResult();
        _ = Assert.IsType<GuardianHostReady>(await reader.ReadAsync(timeout.Token));
        _ = await initialization;
        Assert.Equal(PrivateHostServerState.Ready, server.State);
    }

    [Fact]
    public async Task Runtime_initialization_failure_is_generation_fatal_and_never_emits_ready()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var runtime = new DelegatingPrivateHostRuntime(
            initialize: (_, _) => ValueTask.FromException(
                new RuntimeInitializationTestException()));
        var server = NewServer(guardianToHost, hostToGuardian, runtime: runtime);
        var writer = new GuardianHostProtocolWriter(
            guardianToHost,
            GuardianHostPeer.Guardian);
        var reader = new GuardianHostProtocolReader(
            hostToGuardian,
            GuardianHostPeer.Host);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var initialization = server.InitializeAsync(timeout.Token).AsTask();
        await TransferInitializationThroughSealAsync(writer, reader, timeout.Token);

        await Assert.ThrowsAsync<RuntimeInitializationTestException>(() => initialization);
        Assert.False(hostToGuardian.HasPendingWrite);
        Assert.Equal(PrivateHostServerState.Faulted, server.State);
    }

    [Fact]
    public async Task Operational_loop_correlates_one_typed_terminal_then_shuts_down_cleanly()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var timeline = new List<string>();
        var runtime = new DelegatingPrivateHostRuntime(
            initialize: (_, _) =>
            {
                timeline.Add("initialize");
                return ValueTask.CompletedTask;
            },
            execute: (request, _) =>
            {
                timeline.Add($"execute:{request.RequestId.Value}");
                return ValueTask.FromResult(
                    PrivateHostOperationOutcome.Completed(new JobListResult("[1]")));
            },
            shutdown: (shutdown, _) =>
            {
                timeline.Add($"shutdown:{shutdown.RequestId.Value}");
                return ValueTask.CompletedTask;
            });
        var (server, writer, reader, run, timeout) = await StartOperationalServerAsync(
            guardianToHost,
            hostToGuardian,
            runtime);
        using (timeout)
        {
            await writer.WriteAsync(JobListRequest(requestId: 5), timeout.Token);
            var response = AssertSuccess<OperationCompleted>(
                await reader.ReadAsync(timeout.Token),
                requestId: 5);
            Assert.Equal("[1]", Assert.IsType<JobListResult>(response.Result).Text);

            await writer.WriteAsync(Shutdown(requestId: 6), timeout.Token);
            _ = AssertSuccess<ShutdownAccepted>(
                await reader.ReadAsync(timeout.Token),
                requestId: 6);
            await run.WaitAsync(timeout.Token);
        }

        Assert.Equal(["initialize", "execute:5", "shutdown:6"], timeline);
        Assert.Equal(PrivateHostServerState.Stopped, server.State);
    }

    [Fact]
    public async Task Cancel_targets_only_the_named_active_request_and_emits_no_cancel_response()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var entered = NewSignal();
        var runtime = new DelegatingPrivateHostRuntime(
            execute: async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Canceled execution unexpectedly resumed.");
            });
        var (_, writer, reader, run, timeout) = await StartOperationalServerAsync(
            guardianToHost,
            hostToGuardian,
            runtime);
        using (timeout)
        {
            await writer.WriteAsync(JobListRequest(requestId: 5), timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            await writer.WriteAsync(
                new GuardianHostCancel(
                    Guardian,
                    Host,
                    Generation,
                    new PrivateRequestId(6),
                    new PrivateRequestId(5),
                    GuardianHostCancelReason.CallerCanceled),
                timeout.Token);

            var canceled = AssertError(
                await reader.ReadAsync(timeout.Token),
                requestId: 5);
            Assert.Equal(GuardianHostPrivateDetailCode.RequestCanceled, canceled.DetailCode);

            await writer.WriteAsync(
                new GuardianHostCancel(
                    Guardian,
                    Host,
                    Generation,
                    new PrivateRequestId(7),
                    new PrivateRequestId(5),
                    GuardianHostCancelReason.CallerCanceled),
                timeout.Token);
            await writer.WriteAsync(Shutdown(requestId: 8), timeout.Token);
            _ = AssertSuccess<ShutdownAccepted>(
                await reader.ReadAsync(timeout.Token),
                requestId: 8);
            await run.WaitAsync(timeout.Token);
        }

        Assert.Equal(1, runtime.ExecuteCount);
    }

    [Fact]
    public async Task Successful_completion_after_owned_cancel_is_authoritative_and_late_cancel_is_a_noop()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var entered = NewSignal();
        var runtime = new DelegatingPrivateHostRuntime(
            execute: async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                var cancellationObserved = NewSignal();
                using var registration = cancellationToken.Register(
                    () => cancellationObserved.TrySetResult());
                await cancellationObserved.Task;
                return PrivateHostOperationOutcome.Completed(
                    new JobListResult("authoritative"));
            });
        var (_, writer, reader, run, timeout) = await StartOperationalServerAsync(
            guardianToHost,
            hostToGuardian,
            runtime);
        using (timeout)
        {
            await writer.WriteAsync(JobListRequest(requestId: 5), timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            await writer.WriteAsync(
                new GuardianHostCancel(
                    Guardian,
                    Host,
                    Generation,
                    new PrivateRequestId(6),
                    new PrivateRequestId(5),
                    GuardianHostCancelReason.CallerCanceled),
                timeout.Token);

            var completed = AssertSuccess<OperationCompleted>(
                await reader.ReadAsync(timeout.Token),
                requestId: 5);
            Assert.Equal(
                "authoritative",
                Assert.IsType<JobListResult>(completed.Result).Text);

            await writer.WriteAsync(
                new GuardianHostCancel(
                    Guardian,
                    Host,
                    Generation,
                    new PrivateRequestId(7),
                    new PrivateRequestId(5),
                    GuardianHostCancelReason.CallerCanceled),
                timeout.Token);
            await writer.WriteAsync(Shutdown(requestId: 8), timeout.Token);
            _ = AssertSuccess<ShutdownAccepted>(
                await reader.ReadAsync(timeout.Token),
                requestId: 8);
            await run.WaitAsync(timeout.Token);
        }

        Assert.Equal(1, runtime.ExecuteCount);
    }

    [Fact]
    public async Task Explicit_cancel_before_deadline_keeps_terminal_ownership_from_later_deadline()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var entered = NewSignal();
        var cancellationObserved = NewSignal();
        var runtimeRelease = NewSignal();
        var deadlineRelease = new TaskCompletionSource();
        var deadlineWaitCount = 0;
        var runtime = new DelegatingPrivateHostRuntime(
            execute: async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                using var registration = cancellationToken.Register(
                    () => cancellationObserved.TrySetResult());
                await cancellationObserved.Task;
                await runtimeRelease.Task;
                return PrivateHostOperationOutcome.Completed(
                    new JobListResult("explicit-cancel-owner"));
            });
        var (_, writer, reader, run, timeout) = await StartOperationalServerAsync(
            guardianToHost,
            hostToGuardian,
            runtime,
            unixTimeMilliseconds: () => 0,
            waitUntilDeadline: (_, cancellationToken) =>
                Interlocked.Increment(ref deadlineWaitCount) == 1
                    ? deadlineRelease.Task
                    : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        using (timeout)
        {
            await writer.WriteAsync(
                JobListRequest(requestId: 5, deadlineUnixTimeMilliseconds: 1_000),
                timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            await writer.WriteAsync(
                new GuardianHostCancel(
                    Guardian,
                    Host,
                    Generation,
                    new PrivateRequestId(6),
                    new PrivateRequestId(5),
                    GuardianHostCancelReason.CallerCanceled),
                timeout.Token);
            await cancellationObserved.Task.WaitAsync(timeout.Token);

            deadlineRelease.TrySetResult();
            runtimeRelease.TrySetResult();

            var completed = AssertSuccess<OperationCompleted>(
                await reader.ReadAsync(timeout.Token),
                requestId: 5);
            Assert.Equal(
                "explicit-cancel-owner",
                Assert.IsType<JobListResult>(completed.Result).Text);

            await writer.WriteAsync(Shutdown(requestId: 7), timeout.Token);
            _ = AssertSuccess<ShutdownAccepted>(
                await reader.ReadAsync(timeout.Token),
                requestId: 7);
            await run.WaitAsync(timeout.Token);
        }

        Assert.Equal(1, runtime.ExecuteCount);
    }

    [Fact]
    public async Task Concurrent_operations_may_finish_out_of_order_without_losing_correlation()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var enteredFive = NewSignal();
        var enteredSix = NewSignal();
        var completeFive = NewOutcomeSignal();
        var completeSix = NewOutcomeSignal();
        var runtime = new DelegatingPrivateHostRuntime(
            execute: async (request, cancellationToken) =>
            {
                var entered = request.RequestId.Value == 5 ? enteredFive : enteredSix;
                var completion = request.RequestId.Value == 5 ? completeFive : completeSix;
                entered.TrySetResult();
                return await completion.Task.WaitAsync(cancellationToken);
            });
        var (_, writer, reader, run, timeout) = await StartOperationalServerAsync(
            guardianToHost,
            hostToGuardian,
            runtime);
        using (timeout)
        {
            await writer.WriteAsync(JobListRequest(requestId: 5), timeout.Token);
            await writer.WriteAsync(JobListRequest(requestId: 6), timeout.Token);
            await Task.WhenAll(enteredFive.Task, enteredSix.Task).WaitAsync(timeout.Token);

            completeSix.TrySetResult(
                PrivateHostOperationOutcome.Completed(new JobListResult("six")));
            var second = AssertSuccess<OperationCompleted>(
                await reader.ReadAsync(timeout.Token),
                requestId: 6);
            Assert.Equal("six", Assert.IsType<JobListResult>(second.Result).Text);

            completeFive.TrySetResult(
                PrivateHostOperationOutcome.Completed(new JobListResult("five")));
            var first = AssertSuccess<OperationCompleted>(
                await reader.ReadAsync(timeout.Token),
                requestId: 5);
            Assert.Equal("five", Assert.IsType<JobListResult>(first.Result).Text);

            await writer.WriteAsync(Shutdown(requestId: 7), timeout.Token);
            _ = AssertSuccess<ShutdownAccepted>(
                await reader.ReadAsync(timeout.Token),
                requestId: 7);
            await run.WaitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task Sixty_fifth_outstanding_operation_is_generation_fatal_and_never_enters_runtime()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var allEntered = NewSignal();
        var allCanceled = NewSignal();
        var entered = 0;
        var canceled = 0;
        var runtime = new DelegatingPrivateHostRuntime(
            execute: async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref entered) ==
                    ContractLimits.MaximumOutstandingPrivateRequests)
                {
                    allEntered.TrySetResult();
                }

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("Capacity-test execution unexpectedly resumed.");
                }
                finally
                {
                    if (Interlocked.Increment(ref canceled) ==
                        ContractLimits.MaximumOutstandingPrivateRequests)
                    {
                        allCanceled.TrySetResult();
                    }
                }
            });
        var (server, writer, _, run, timeout) = await StartOperationalServerAsync(
            guardianToHost,
            hostToGuardian,
            runtime);
        using (timeout)
        {
            for (var index = 0;
                index < ContractLimits.MaximumOutstandingPrivateRequests;
                index++)
            {
                await writer.WriteAsync(
                    JobListRequest(requestId: 5 + index),
                    timeout.Token);
            }
            await allEntered.Task.WaitAsync(timeout.Token);

            await writer.WriteAsync(
                JobListRequest(
                    requestId: 5 + ContractLimits.MaximumOutstandingPrivateRequests),
                timeout.Token);
            guardianToHost.CompleteWriting();

            var failure = await Assert.ThrowsAsync<GuardianHostProtocolException>(() =>
                run.WaitAsync(timeout.Token));
            Assert.Equal("outstanding_request_limit_exceeded", failure.DetailCode);
            await allCanceled.Task.WaitAsync(timeout.Token);
        }

        Assert.Equal(
            ContractLimits.MaximumOutstandingPrivateRequests,
            runtime.ExecuteCount);
        Assert.Equal(
            ContractLimits.MaximumOutstandingPrivateRequests,
            Volatile.Read(ref entered));
        Assert.Equal(
            ContractLimits.MaximumOutstandingPrivateRequests,
            Volatile.Read(ref canceled));
        Assert.Equal(PrivateHostServerState.Faulted, server.State);
    }

    [Fact]
    public async Task Operational_identity_or_request_id_violation_fails_before_runtime_execution()
    {
        await AssertOperationalMessageFailure(
            JobListRequest(
                requestId: 5,
                guardianBootId: new GuardianBootId(
                    Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"))),
            "identity_mismatch");
        await AssertOperationalMessageFailure(
            JobListRequest(requestId: 4),
            "request_id_not_increasing");
        await AssertOperationalMessageFailure(
            new GuardianHostCancel(
                Guardian,
                Host,
                Generation,
                new PrivateRequestId(4),
                new PrivateRequestId(1),
                GuardianHostCancelReason.CallerCanceled),
            "request_id_not_increasing");
        await AssertOperationalMessageFailure(
            Shutdown(requestId: 4),
            "request_id_not_increasing");
        await AssertOperationalMessageFailure(
            new GuardianHostCancel(
                Guardian,
                Host,
                Generation,
                new PrivateRequestId(5),
                new PrivateRequestId(5),
                GuardianHostCancelReason.CallerCanceled),
            "cancel_target_invalid");
    }

    [Fact]
    public async Task Runtime_mismatch_and_unclassified_failure_each_receive_one_safe_terminal()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var runtime = new DelegatingPrivateHostRuntime(
            execute: (request, _) => request.RequestId.Value switch
            {
                5 => ValueTask.FromResult(
                    PrivateHostOperationOutcome.Completed(new JobStatusResult("{}"))),
                6 => ValueTask.FromException<PrivateHostOperationOutcome>(
                    new IOException("sensitive runtime failure")),
                _ => ValueTask.FromResult(
                    PrivateHostOperationOutcome.Failed(
                        GuardianHostPrivateDetailCode.SessionBusy)),
            });
        var (_, writer, reader, run, timeout) = await StartOperationalServerAsync(
            guardianToHost,
            hostToGuardian,
            runtime);
        using (timeout)
        {
            await writer.WriteAsync(JobListRequest(requestId: 5), timeout.Token);
            Assert.Equal(
                GuardianHostPrivateDetailCode.OperationResultMismatch,
                AssertError(await reader.ReadAsync(timeout.Token), requestId: 5).DetailCode);

            await writer.WriteAsync(JobListRequest(requestId: 6), timeout.Token);
            Assert.Equal(
                GuardianHostPrivateDetailCode.OutcomeUnknown,
                AssertError(await reader.ReadAsync(timeout.Token), requestId: 6).DetailCode);

            await writer.WriteAsync(JobListRequest(requestId: 7), timeout.Token);
            Assert.Equal(
                GuardianHostPrivateDetailCode.SessionBusy,
                AssertError(await reader.ReadAsync(timeout.Token), requestId: 7).DetailCode);

            await writer.WriteAsync(Shutdown(requestId: 8), timeout.Token);
            _ = AssertSuccess<ShutdownAccepted>(
                await reader.ReadAsync(timeout.Token),
                requestId: 8);
            await run.WaitAsync(timeout.Token);
        }

        Assert.Equal(3, runtime.ExecuteCount);
    }

    [Fact]
    public async Task Operational_eof_cancels_observes_and_terminals_active_work_before_faulting()
    {
        await AssertActiveOperationFaultDrains(
            (guardianToHost, _, _) =>
            {
                guardianToHost.CompleteWriting();
                return ValueTask.CompletedTask;
            },
            "unexpected_eof");
    }

    [Fact]
    public async Task Operational_protocol_fault_cancels_observes_and_terminals_active_work()
    {
        await AssertActiveOperationFaultDrains(
            async (_, writer, cancellationToken) =>
            {
                var encodedManifest = RecoveryManifestCodec.Encode(NewManifest());
                await writer.WriteAsync(new ManifestHeaderRequest(
                    Guardian,
                    Host,
                    Generation,
                    new PrivateRequestId(6),
                    ManifestId,
                    encodedManifest.Length,
                    Sha256Digest.Compute(encodedManifest),
                    aliasCount: 1,
                    templateCount: 0), cancellationToken);
            },
            "operational_message_invalid");
    }

    [Fact]
    public async Task Shutdown_cancels_and_observes_active_operations_before_runtime_shutdown_and_ack()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var timeline = new List<string>();
        var entered = NewSignal();
        var runtime = new DelegatingPrivateHostRuntime(
            execute: async (_, cancellationToken) =>
            {
                timeline.Add("execute");
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("Shutdown cancellation unexpectedly resumed.");
                }
                finally
                {
                    timeline.Add("execute-observed");
                }
            },
            shutdown: (_, _) =>
            {
                timeline.Add("shutdown");
                return ValueTask.CompletedTask;
            });
        var (server, writer, reader, run, timeout) = await StartOperationalServerAsync(
            guardianToHost,
            hostToGuardian,
            runtime);
        using (timeout)
        {
            await writer.WriteAsync(JobListRequest(requestId: 5), timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            await writer.WriteAsync(Shutdown(requestId: 6), timeout.Token);

            Assert.Equal(
                GuardianHostPrivateDetailCode.RequestCanceled,
                AssertError(await reader.ReadAsync(timeout.Token), requestId: 5).DetailCode);
            _ = AssertSuccess<ShutdownAccepted>(
                await reader.ReadAsync(timeout.Token),
                requestId: 6);
            await run.WaitAsync(timeout.Token);
        }

        Assert.Equal(["execute", "execute-observed", "shutdown"], timeline);
        Assert.Equal(PrivateHostServerState.Stopped, server.State);
    }

    [Fact]
    public async Task In_flight_deadline_owns_one_terminal_and_late_runtime_success_is_discarded()
    {
        using var guardianToHost = new ChannelStream();
        var timeline = new ConcurrentQueue<string>();
        using var hostToGuardian = new ChannelStream(
            () => timeline.Enqueue("host-write"));
        var executionEntered = NewSignal();
        var deadlineWaitEntered = NewSignal();
        var fireDeadline = NewSignal();
        var runtimeObservedCancellation = NewSignal();
        var releaseLateSuccess = NewSignal();
        var runtime = new DelegatingPrivateHostRuntime(
            execute: async (_, cancellationToken) =>
            {
                executionEntered.TrySetResult();
                using var registration = cancellationToken.Register(
                    () => runtimeObservedCancellation.TrySetResult());
                await runtimeObservedCancellation.Task;
                await releaseLateSuccess.Task;
                timeline.Enqueue("runtime-complete");
                return PrivateHostOperationOutcome.Completed(
                    new JobListResult("too-late"));
            });
        var (_, writer, reader, run, timeout) = await StartOperationalServerAsync(
            guardianToHost,
            hostToGuardian,
            runtime,
            unixTimeMilliseconds: () => 100,
            waitUntilDeadline: async (deadline, cancellationToken) =>
            {
                if (deadline != 200)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return;
                }
                deadlineWaitEntered.TrySetResult();
                await fireDeadline.Task.WaitAsync(cancellationToken);
            });
        using (timeout)
        {
            await writer.WriteAsync(
                JobListRequest(requestId: 5, deadlineUnixTimeMilliseconds: 200),
                timeout.Token);
            await Task.WhenAll(executionEntered.Task, deadlineWaitEntered.Task)
                .WaitAsync(timeout.Token);

            fireDeadline.TrySetResult();
            await runtimeObservedCancellation.Task.WaitAsync(timeout.Token);
            Assert.False(hostToGuardian.HasPendingWrite);

            releaseLateSuccess.TrySetResult();
            Assert.Equal(
                GuardianHostPrivateDetailCode.RequestDeadlineExpired,
                AssertError(await reader.ReadAsync(timeout.Token), requestId: 5).DetailCode);

            await writer.WriteAsync(
                new GuardianHostCancel(
                    Guardian,
                    Host,
                    Generation,
                    new PrivateRequestId(6),
                    new PrivateRequestId(5),
                    GuardianHostCancelReason.DeadlineExpired),
                timeout.Token);
            await writer.WriteAsync(Shutdown(requestId: 7, deadline: 1_000), timeout.Token);
            _ = AssertSuccess<ShutdownAccepted>(
                await reader.ReadAsync(timeout.Token),
                requestId: 7);
            await run.WaitAsync(timeout.Token);
        }

        Assert.Equal(1, runtime.ExecuteCount);
        var ordered = timeline.ToArray();
        var runtimeCompletion = Array.IndexOf(ordered, "runtime-complete");
        Assert.True(runtimeCompletion >= 0);
        Assert.Equal(
            ["host-write", "host-write"],
            ordered[(runtimeCompletion + 1)..]);
    }

    [Fact]
    public async Task Already_expired_operation_returns_one_deadline_terminal_without_runtime_execution()
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var runtime = new DelegatingPrivateHostRuntime();
        var (_, writer, reader, run, timeout) = await StartOperationalServerAsync(
            guardianToHost,
            hostToGuardian,
            runtime,
            unixTimeMilliseconds: () => 100);
        using (timeout)
        {
            await writer.WriteAsync(
                JobListRequest(requestId: 5, deadlineUnixTimeMilliseconds: 100),
                timeout.Token);
            Assert.Equal(
                GuardianHostPrivateDetailCode.RequestDeadlineExpired,
                AssertError(await reader.ReadAsync(timeout.Token), requestId: 5).DetailCode);

            await writer.WriteAsync(Shutdown(requestId: 6, deadline: 1_000), timeout.Token);
            _ = AssertSuccess<ShutdownAccepted>(
                await reader.ReadAsync(timeout.Token),
                requestId: 6);
            await run.WaitAsync(timeout.Token);
        }

        Assert.Equal(0, runtime.ExecuteCount);
    }

    [Fact]
    public void Deadline_owner_source_orders_runtime_quiescence_before_terminal_write()
    {
        var source = File.ReadAllText(PrivateHostServerSourcePath());
        var branchStart = source.IndexOf(
            "if (deadlineOwnsTerminal)",
            StringComparison.Ordinal);
        Assert.True(branchStart >= 0);
        var arbitrationStart = source.LastIndexOf(
            "Task.WhenAny(executionTask, active.DeadlineTask)",
            branchStart,
            StringComparison.Ordinal);
        Assert.True(arbitrationStart >= 0);
        var arbitration = source[arbitrationStart..branchStart];
        Assert.Contains(
            "active.DeadlineOwnsTerminal",
            arbitration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "executionTask.IsCompleted",
            arbitration,
            StringComparison.Ordinal);
        var branchEnd = source.IndexOf(
            "var outcome = await executionTask.ConfigureAwait(false);",
            branchStart,
            StringComparison.Ordinal);
        Assert.True(branchEnd > branchStart);
        var branch = source[branchStart..branchEnd];

        var runtimeObserved = branch.IndexOf(
            "_ = await executionTask.ConfigureAwait(false);",
            StringComparison.Ordinal);
        var quiescenceFinished = branch.IndexOf(
            "await FinishOperationAsync(active).ConfigureAwait(false);",
            StringComparison.Ordinal);
        var terminalWrite = branch.IndexOf(
            "await WriteOperationTerminalAsync(",
            StringComparison.Ordinal);
        var terminalReturn = branch.IndexOf(
            "return;",
            StringComparison.Ordinal);

        Assert.True(runtimeObserved >= 0);
        Assert.True(quiescenceFinished > runtimeObserved);
        Assert.True(terminalWrite > quiescenceFinished);
        Assert.True(terminalReturn > terminalWrite);
    }

    [Fact]
    public void Private_server_uses_only_the_narrow_runtime_boundary_and_remains_unwired_from_mcp()
    {
        var source = File.ReadAllText(PrivateHostServerSourcePath());
        Assert.Contains("IPrivateHostRuntime", source, StringComparison.Ordinal);
        foreach (var forbidden in new[]
        {
            "SessionRuntime",
            "DefaultSessionRuntimeFactory",
            "RunspaceHost",
            "PowerShell",
            "JobManager",
            "WorkerOperationScheduler",
            "InvokeTool",
            "ModelContextProtocol",
            "IServiceCollection",
            "AddSingleton",
            "ProcessStartInfo",
            "System.Diagnostics.Process",
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    private static async Task<(
        PrivateHostServer Server,
        GuardianHostProtocolWriter Writer,
        GuardianHostProtocolReader Reader,
        Task Run,
        CancellationTokenSource Timeout)> StartOperationalServerAsync(
        ChannelStream guardianToHost,
        ChannelStream hostToGuardian,
        IPrivateHostRuntime runtime,
        Func<long>? unixTimeMilliseconds = null,
        Func<long, CancellationToken, Task>? waitUntilDeadline = null)
    {
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var server = NewServer(
                guardianToHost,
                hostToGuardian,
                runtime: runtime,
                unixTimeMilliseconds: unixTimeMilliseconds,
                waitUntilDeadline: waitUntilDeadline);
            var writer = new GuardianHostProtocolWriter(
                guardianToHost,
                GuardianHostPeer.Guardian);
            var reader = new GuardianHostProtocolReader(
                hostToGuardian,
                GuardianHostPeer.Host);
            var run = server.RunAsync(timeout.Token);
            await TransferInitializationThroughSealAsync(
                writer,
                reader,
                timeout.Token);
            _ = Assert.IsType<GuardianHostReady>(await reader.ReadAsync(timeout.Token));
            Assert.Equal(PrivateHostServerState.Ready, server.State);
            return (server, writer, reader, run, timeout);
        }
        catch
        {
            timeout.Dispose();
            throw;
        }
    }

    private static async Task TransferInitializationThroughSealAsync(
        GuardianHostProtocolWriter writer,
        GuardianHostProtocolReader reader,
        CancellationToken cancellationToken)
    {
        _ = Assert.IsType<GuardianHostHello>(await reader.ReadAsync(cancellationToken));
        var encodedManifest = RecoveryManifestCodec.Encode(NewManifest());
        var manifestDigest = Sha256Digest.Compute(encodedManifest);

        await writer.WriteAsync(Initialize(requestId: 1), cancellationToken);
        await writer.WriteAsync(new ManifestHeaderRequest(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(2),
            ManifestId,
            encodedManifest.Length,
            manifestDigest,
            aliasCount: 1,
            templateCount: 0), cancellationToken);
        _ = AssertSuccess<ManifestHeaderAccepted>(
            await reader.ReadAsync(cancellationToken),
            requestId: 2);

        using (var chunk = new ManifestChunkRequest(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(3),
            ManifestId,
            chunkIndex: 0,
            encodedManifest))
        {
            await writer.WriteAsync(chunk, cancellationToken);
        }
        _ = AssertSuccess<ManifestChunkAccepted>(
            await reader.ReadAsync(cancellationToken),
            requestId: 3);

        await writer.WriteAsync(new ManifestSealRequest(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(4),
            ManifestId,
            encodedManifest.Length,
            manifestDigest), cancellationToken);
        _ = AssertSuccess<ManifestSealed>(
            await reader.ReadAsync(cancellationToken),
            requestId: 4);
    }

    private static async Task AssertOperationalMessageFailure(
        GuardianHostMessage message,
        string expectedDetailCode)
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var runtime = new DelegatingPrivateHostRuntime();
        var (server, writer, _, run, timeout) = await StartOperationalServerAsync(
            guardianToHost,
            hostToGuardian,
            runtime);
        using (timeout)
        {
            await writer.WriteAsync(message, timeout.Token);
            var failure = await Assert.ThrowsAsync<GuardianHostProtocolException>(() =>
                run.WaitAsync(timeout.Token));
            Assert.Equal(expectedDetailCode, failure.DetailCode);
        }

        Assert.Equal(0, runtime.ExecuteCount);
        Assert.Equal(PrivateHostServerState.Faulted, server.State);
        Assert.False(hostToGuardian.HasPendingWrite);
    }

    private static async Task AssertActiveOperationFaultDrains(
        Func<ChannelStream, GuardianHostProtocolWriter, CancellationToken, ValueTask> trigger,
        string expectedDetailCode)
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var entered = NewSignal();
        var observed = NewSignal();
        var runtime = new DelegatingPrivateHostRuntime(
            execute: async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("Fault cancellation unexpectedly resumed.");
                }
                finally
                {
                    observed.TrySetResult();
                }
            });
        var (server, writer, reader, run, timeout) = await StartOperationalServerAsync(
            guardianToHost,
            hostToGuardian,
            runtime);
        using (timeout)
        {
            await writer.WriteAsync(JobListRequest(requestId: 5), timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            await trigger(guardianToHost, writer, timeout.Token);

            Assert.Equal(
                GuardianHostPrivateDetailCode.OutcomeUnknown,
                AssertError(await reader.ReadAsync(timeout.Token), requestId: 5).DetailCode);
            var failure = await Assert.ThrowsAsync<GuardianHostProtocolException>(() =>
                run.WaitAsync(timeout.Token));
            Assert.Equal(expectedDetailCode, failure.DetailCode);
            Assert.True(observed.Task.IsCompletedSuccessfully);
        }

        Assert.Equal(1, runtime.ExecuteCount);
        Assert.Equal(PrivateHostServerState.Faulted, server.State);
    }

    private static OperationRequest JobListRequest(
        long requestId,
        GuardianBootId? guardianBootId = null,
        long? deadlineUnixTimeMilliseconds = null)
    {
        var deadline = deadlineUnixTimeMilliseconds ?? FutureDeadline();
        return new OperationRequest(
            guardianBootId ?? Guardian,
            Host,
            Generation,
            new PrivateRequestId(requestId),
            deadline,
            Alias,
            Transition,
            Worker,
            operationIdentity: null,
            new JobListOperation(
                Call,
                new DispatchCapability(DispatchToken, Call, deadline)));
    }

    private static GuardianHostShutdown Shutdown(
        long requestId,
        long? deadline = null) =>
        new(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(requestId),
            deadline ?? FutureDeadline(),
            GuardianHostShutdownReason.GuardianShutdown);

    private static GuardianHostPrivateError AssertError(
        GuardianHostMessage? message,
        long requestId)
    {
        var response = Assert.IsType<GuardianHostErrorResponse>(message);
        AssertIdentity(response);
        Assert.Equal(new PrivateRequestId(requestId), response.RequestId);
        return response.Error;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<PrivateHostOperationOutcome> NewOutcomeSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AssertInitializeFailure(
        GuardianHostInitialize initialize,
        string expectedDetailCode)
    {
        using var guardianToHost = new ChannelStream();
        using var hostToGuardian = new ChannelStream();
        var allocationCount = 0;
        var server = NewServer(
            guardianToHost,
            hostToGuardian,
            length =>
            {
                Interlocked.Increment(ref allocationCount);
                return new byte[length];
            });
        var writer = new GuardianHostProtocolWriter(
            guardianToHost,
            GuardianHostPeer.Guardian);
        var reader = new GuardianHostProtocolReader(
            hostToGuardian,
            GuardianHostPeer.Host);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var initialization = server.InitializeAsync(timeout.Token).AsTask();
        _ = Assert.IsType<GuardianHostHello>(await reader.ReadAsync());
        await writer.WriteAsync(initialize);
        guardianToHost.CompleteWriting();

        var failure = await Assert.ThrowsAsync<GuardianHostProtocolException>(
            async () => await initialization);
        Assert.Equal(expectedDetailCode, failure.DetailCode);
        Assert.Equal(0, Volatile.Read(ref allocationCount));
        Assert.Equal(PrivateHostServerState.Faulted, server.State);
    }

    private static PrivateHostServer NewServer(
        Stream guardianToHost,
        Stream hostToGuardian,
        Func<int, byte[]>? manifestBufferFactory = null,
        IPrivateHostRuntime? runtime = null,
        Func<long>? unixTimeMilliseconds = null,
        Func<long, CancellationToken, Task>? waitUntilDeadline = null) =>
        new(
            guardianToHost,
            hostToGuardian,
            new PrivateHostServerIdentity(Guardian, Host, Generation, hostPid: 4242),
            new PrivateHostServerPins(
                ExecutableDigest,
                BuildDigest,
                ContractDigest,
                ConfigurationDigest,
                PackageDigest),
            runtime ?? new ImmediatePrivateHostRuntime(),
            manifestBufferFactory,
            unixTimeMilliseconds,
            waitUntilDeadline);

    private static GuardianHostInitialize Initialize(long requestId) =>
        new(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(requestId),
            ExecutableDigest,
            BuildDigest,
            ContractDigest,
            ConfigurationDigest,
            PackageDigest);

    private static RecoveryManifest NewManifest()
    {
        var alias = new CanonicalAlias("default");
        return new RecoveryManifest(
            Guardian,
            Generation,
            CatalogDigest,
            ConfigurationDigest,
            Array.Empty<RecoveryTemplate>(),
            [
                new RecoveryBinding(
                    alias,
                    RecoveryBindingKind.Default,
                    templateName: null,
                    templateDigest: null,
                    bootstrapDigest: null,
                    allowColdBackground: false,
                    DesiredSessionState.Ready,
                    new SessionTransitionVersion(0),
                    Digest('7')),
            ],
            [
                new WorkerGenerationHighWatermarkEntry(
                    alias,
                    new WorkerGenerationHighWatermark(0)),
            ],
            Generation);
    }

    private static TPayload AssertSuccess<TPayload>(
        GuardianHostMessage? message,
        long requestId)
        where TPayload : GuardianHostSuccessPayload
    {
        var response = Assert.IsType<GuardianHostSuccessResponse>(message);
        AssertIdentity(response);
        Assert.Equal(new PrivateRequestId(requestId), response.RequestId);
        return Assert.IsType<TPayload>(response.Payload);
    }

    private static void AssertIdentity(GuardianHostMessage message)
    {
        Assert.Equal(Guardian, message.GuardianBootId);
        Assert.Equal(Host, message.HostBootId);
        Assert.Equal(Generation, message.HostGeneration);
    }

    private static Sha256Digest Digest(char value) => new(new string(value, 64));

    private static long FutureDeadline() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000;

    private static CapabilityToken Capability(byte value)
    {
        var bytes = Enumerable.Repeat(value, ContractLimits.CapabilityTokenBytes).ToArray();
        return new CapabilityToken(Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'));
    }

    private static string PrivateHostServerSourcePath(
        [CallerFilePath] string testSourcePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testSourcePath) ??
                throw new InvalidOperationException("Test source path is unavailable."),
            "..",
            "PtkMcpServer",
            "GuardianHost",
            "PrivateHostServer.cs"));

    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("Injected host writer failure.");
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Injected host writer failure."));
    }

    private sealed class ImmediatePrivateHostRuntime : IPrivateHostRuntime
    {
        public ValueTask InitializeAsync(
            PrivateHostInitialization initialization,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(initialization);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<PrivateHostOperationOutcome> ExecuteOperationAsync(
            OperationRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<PrivateHostOperationOutcome>(
                new InvalidOperationException("The initialization-only runtime cannot execute operations."));

        public ValueTask ShutdownAsync(
            GuardianHostShutdown shutdown,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(shutdown);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelegatingPrivateHostRuntime : IPrivateHostRuntime
    {
        private readonly Func<PrivateHostInitialization, CancellationToken, ValueTask> _initialize;
        private readonly Func<OperationRequest, CancellationToken,
            ValueTask<PrivateHostOperationOutcome>> _execute;
        private readonly Func<GuardianHostShutdown, CancellationToken, ValueTask> _shutdown;
        private int _executeCount;

        internal DelegatingPrivateHostRuntime(
            Func<PrivateHostInitialization, CancellationToken, ValueTask>? initialize = null,
            Func<OperationRequest, CancellationToken,
                ValueTask<PrivateHostOperationOutcome>>? execute = null,
            Func<GuardianHostShutdown, CancellationToken, ValueTask>? shutdown = null)
        {
            _initialize = initialize ?? (static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            });
            _execute = execute ?? (static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(
                    PrivateHostOperationOutcome.Completed(new JobListResult("[]")));
            });
            _shutdown = shutdown ?? (static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            });
        }

        internal int ExecuteCount => Volatile.Read(ref _executeCount);

        public ValueTask InitializeAsync(
            PrivateHostInitialization initialization,
            CancellationToken cancellationToken) =>
            _initialize(initialization, cancellationToken);

        public ValueTask<PrivateHostOperationOutcome> ExecuteOperationAsync(
            OperationRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executeCount);
            return _execute(request, cancellationToken);
        }

        public ValueTask ShutdownAsync(
            GuardianHostShutdown shutdown,
            CancellationToken cancellationToken) =>
            _shutdown(shutdown, cancellationToken);
    }

    private sealed class RuntimeInitializationTestException : Exception;

    private sealed class ChannelStream : Stream
    {
        private readonly Action? _writeObserver;
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        private byte[]? _current;
        private int _currentOffset;

        internal ChannelStream(Action? writeObserver = null) =>
            _writeObserver = writeObserver;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void CompleteWriting() => _channel.Writer.TryComplete();

        internal bool HasPendingWrite => _channel.Reader.TryPeek(out _);

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current is null || _currentOffset == _current.Length)
            {
                _current = null;
                _currentOffset = 0;
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    return 0;
                if (!_channel.Reader.TryRead(out _current))
                    continue;
            }

            var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
            _current.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _writeObserver?.Invoke();
            return _channel.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _channel.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }
}
