using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using PtkMcpGuardian.Host;
using PtkMcpGuardian.Lifecycle;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class GuardianHostClientTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly HostBootId OtherHost = new(
        Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));
    private static readonly ManifestId Manifest = new(
        Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"));
    private static readonly WorkerBootId WorkerBoot = new(
        Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"));
    private static readonly WorkerBootId ReplacementWorkerBoot = new(
        Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly PlanId Plan = new(
        Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"));
    private static readonly OperationId Operation = new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly CallId Call = new(
        Guid.Parse("77777777-7777-7777-8777-777777777777"));
    private static readonly HostGeneration Generation = new(1);
    private static readonly CanonicalAlias Alias = new("default");
    private static readonly SessionTransitionVersion Transition = new(1);
    private static readonly GuardianHostWorkerIdentity Worker = new(
        WorkerBoot,
        new WorkerGeneration(1));
    private static readonly GuardianHostWorkerIdentity ReplacementWorker = new(
        ReplacementWorkerBoot,
        new WorkerGeneration(2));
    private static readonly Sha256Digest ExecutableDigest = Digest('1');
    private static readonly Sha256Digest BuildDigest = Digest('2');
    private static readonly Sha256Digest PublicContractDigest = Digest('3');
    private static readonly Sha256Digest ConfigurationDigest = Digest('4');
    private static readonly Sha256Digest CatalogDigest = Digest('5');
    private static readonly Sha256Digest PackageManifestDigest = Digest('6');
    private static readonly Sha256Digest BindingDigest = Digest('7');
    private static readonly CapabilityToken DispatchToken = Capability(1);
    private static readonly CapabilityToken OutputToken = Capability(2);
    private static readonly CapabilityToken JobToken = Capability(3);
    private static readonly GuardianHostOperationIdentity OperationIdentity = new(Plan, Operation);

    [Fact]
    public async Task Initialize_enforces_the_frozen_sequence_and_enters_ready_once()
    {
        await using var harness = new HostHarness();

        var transcript = await harness.InitializeAsync();

        Assert.Equal(GuardianHostClientState.Ready, harness.Client.State);
        Assert.Equal(Host, harness.Client.HostBootId);
        Assert.Equal([1L, 2L, 3L, 4L], transcript.GuardianRequestIds);
        Assert.Equal(RecoveryManifestCodec.Encode(harness.Manifest), transcript.ManifestBytes);
        Assert.Equal(Sha256Digest.Compute(transcript.ManifestBytes), transcript.ManifestDigest);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Client.InitializeAsync(harness.Manifest));
        Assert.Equal(GuardianHostClientState.Ready, harness.Client.State);
    }

    [Fact]
    public async Task Dispose_joins_a_cancellation_ignoring_handshake_write()
    {
        await using var harness = new HostHarness();
        var blocked = harness.RequestTransport.BlockNextWrite(ignoreCancellation: true);
        var initialize = harness.Client.InitializeAsync(harness.Manifest);
        await harness.HostWriter.WriteAsync(harness.CreateHello());
        await blocked.WaitAsync(TestTimeout);

        var dispose = harness.Client.DisposeAsync().AsTask();
        var duplicateDispose = harness.Client.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);
        Assert.False(duplicateDispose.IsCompleted);

        harness.RequestTransport.ReleaseBlockedWrite();
        await duplicateDispose.WaitAsync(TestTimeout);
        await dispose.WaitAsync(TestTimeout);
        await Assert.ThrowsAsync<GuardianHostClientException>(() => initialize);
        Assert.Equal(GuardianHostClientState.Stopped, harness.Client.State);
        Assert.Equal(1, harness.RequestTransport.CompletedWriteCount);
        _ = Assert.IsType<GuardianHostInitialize>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        using var noMoreFrames = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.GuardianReader.ReadAsync(noMoreFrames.Token).AsTask());
    }

    [Theory]
    [InlineData("guardian")]
    [InlineData("host_boot")]
    [InlineData("generation")]
    [InlineData("pid")]
    [InlineData("executable")]
    [InlineData("build")]
    [InlineData("public_contract")]
    [InlineData("configuration")]
    public async Task Initialize_fails_closed_on_each_hello_pin_mismatch(string mismatch)
    {
        await using var harness = new HostHarness();
        var hello = harness.CreateHello(mismatch);

        var initialize = harness.Client.InitializeAsync(harness.Manifest);
        await harness.HostWriter.WriteAsync(hello);

        var failure = await Assert.ThrowsAsync<GuardianHostClientException>(() =>
            initialize.WaitAsync(TestTimeout));
        Assert.Equal(GuardianHostClientFailureKind.ContractMismatch, failure.DetailKind);
        Assert.Equal("host_contract_mismatch", failure.DetailCode);
        Assert.Equal(GuardianHostClientState.Faulted, harness.Client.State);
        Assert.Equal(failure, await harness.Client.Fatal.WaitAsync(TestTimeout));
        Assert.Equal(0, harness.RequestTransport.WriteCount);
    }

    [Theory]
    [InlineData("guardian")]
    [InlineData("generation")]
    [InlineData("catalog")]
    [InlineData("configuration")]
    public async Task Initialize_rejects_manifest_pin_mismatch_before_read_or_write(string mismatch)
    {
        await using var harness = new HostHarness();
        var manifest = CreateManifest(
            guardian: mismatch == "guardian"
                ? new GuardianBootId(Guid.Parse("99999999-9999-4999-8999-999999999999"))
                : Guardian,
            generation: mismatch == "generation" ? new HostGeneration(2) : Generation,
            catalog: mismatch == "catalog" ? Digest('8') : CatalogDigest,
            configuration: mismatch == "configuration" ? Digest('9') : ConfigurationDigest);

        var failure = await Assert.ThrowsAsync<GuardianHostClientException>(() =>
            harness.Client.InitializeAsync(manifest));

        Assert.Equal(GuardianHostClientFailureKind.ContractMismatch, failure.DetailKind);
        Assert.Equal(0, harness.RequestTransport.WriteCount);
        Assert.Equal(0, harness.EventTransport.ReadCount);
    }

    [Theory]
    [InlineData("eof_hello", "UnexpectedEof")]
    [InlineData("wrong_kind_hello", "ProtocolViolation")]
    [InlineData("eof_header", "UnexpectedEof")]
    [InlineData("header_request_id", "ResponseCorrelationMismatch")]
    [InlineData("header_manifest", "ResponseCorrelationMismatch")]
    [InlineData("eof_chunk", "UnexpectedEof")]
    [InlineData("chunk_request_id", "ResponseCorrelationMismatch")]
    [InlineData("chunk_manifest", "ResponseCorrelationMismatch")]
    [InlineData("chunk_index", "ResponseCorrelationMismatch")]
    [InlineData("chunk_offset", "ResponseCorrelationMismatch")]
    [InlineData("eof_seal", "UnexpectedEof")]
    [InlineData("seal_request_id", "ResponseCorrelationMismatch")]
    [InlineData("seal_manifest", "ResponseCorrelationMismatch")]
    [InlineData("seal_digest", "ResponseCorrelationMismatch")]
    [InlineData("seal_size", "ResponseCorrelationMismatch")]
    [InlineData("eof_ready", "UnexpectedEof")]
    [InlineData("wrong_kind_ready", "ProtocolViolation")]
    [InlineData("ready_initialize_id", "ResponseCorrelationMismatch")]
    [InlineData("ready_manifest", "ResponseCorrelationMismatch")]
    [InlineData("ready_digest", "ResponseCorrelationMismatch")]
    [InlineData("ready_pid", "ResponseCorrelationMismatch")]
    public async Task Hostile_handshake_stage_fails_closed(
        string scenario,
        string expectedFailure)
    {
        await using var harness = new HostHarness();
        var initialize = harness.Client.InitializeAsync(harness.Manifest);

        await DriveHostileHandshakeAsync(harness, scenario);

        var failure = await Assert.ThrowsAsync<GuardianHostClientException>(() =>
            initialize.WaitAsync(TestTimeout));
        Assert.Equal(expectedFailure, failure.DetailKind.ToString());
        Assert.Same(failure, await harness.Client.Fatal.WaitAsync(TestTimeout));
        Assert.Equal(GuardianHostClientState.Faulted, harness.Client.State);
    }

    [Fact]
    public async Task Source_guard_keeps_manifest_buffer_zeroization()
    {
        var source = await File.ReadAllTextAsync(FindGuardianHostClientSource());
        Assert.Contains(
            "CryptographicOperations.ZeroMemory(manifestBytes);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Source_guard_keeps_wrong_kind_raw_message_disposal()
    {
        var source = await File.ReadAllTextAsync(FindGuardianHostClientSource());
        Assert.Contains(
            "(message as IDisposable)?.Dispose();",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operational_response_and_correlated_event_complete_the_original_request()
    {
        var observed = new ConcurrentQueue<GuardianHostEvent>();
        await using var harness = new HostHarness((value, _) =>
        {
            observed.Enqueue(value);
            return ValueTask.CompletedTask;
        });
        await harness.InitializeAsync();

        var completion = harness.Client.SendRequestAsync(CreateJobListRequest);
        var request = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        Assert.Equal(5, request.RequestId.Value);

        await harness.HostWriter.WriteAsync(new OperationDeliveryEvent(
            Guardian,
            Host,
            Generation,
            new HostEventSequence(3),
            request.RequestId,
            Alias,
            Transition,
            Worker,
            null,
            DispatchToken,
            GuardianHostDeliveryState.NotDispatched,
            null));
        var response = Success(request.RequestId, new OperationCompleted(new JobListResult("[]")));
        await harness.HostWriter.WriteAsync(response);

        var completed = Assert.IsType<OperationCompleted>(Assert.IsType<GuardianHostSuccessResponse>(
            await completion.WaitAsync(TestTimeout)).Payload);
        Assert.Equal("[]", Assert.IsType<JobListResult>(completed.Result).Text);
        var delivery = Assert.IsType<OperationDeliveryEvent>(Assert.Single(observed));
        Assert.Equal(request.RequestId, delivery.RequestId);
        Assert.Equal(3, harness.Client.LastHostEventSequence);
        Assert.Equal(0, harness.Client.OutstandingRequestCount);
    }

    [Theory]
    [InlineData("guardian")]
    [InlineData("host_boot")]
    [InlineData("generation")]
    public async Task Wrong_operational_identity_cannot_mutate_or_complete_pending_request(
        string mismatch)
    {
        var handled = 0;
        await using var harness = new HostHarness((_, _) =>
        {
            Interlocked.Increment(ref handled);
            return ValueTask.CompletedTask;
        });
        await harness.InitializeAsync();

        var completion = harness.Client.SendRequestAsync(CreateJobListRequest);
        var request = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        await harness.HostWriter.WriteAsync(new OperationDeliveryEvent(
            mismatch == "guardian"
                ? new GuardianBootId(Guid.Parse("99999999-9999-4999-8999-999999999999"))
                : Guardian,
            mismatch == "host_boot" ? OtherHost : Host,
            mismatch == "generation" ? new HostGeneration(2) : Generation,
            new HostEventSequence(1),
            request.RequestId,
            Alias,
            Transition,
            Worker,
            null,
            DispatchToken,
            GuardianHostDeliveryState.NotDispatched,
            null));

        var fatal = await harness.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientFailureKind.ContractMismatch, fatal.DetailKind);
        var pendingFailure = await Assert.ThrowsAsync<GuardianHostClientException>(() => completion);
        Assert.Same(fatal, pendingFailure);
        Assert.Equal(0, harness.Client.OutstandingRequestCount);
        Assert.Equal(0, Volatile.Read(ref handled));
        Assert.Equal(0, harness.Client.LastHostEventSequence);
    }

    [Fact]
    public async Task Duplicate_response_is_generation_fatal_but_never_replaces_the_first_terminal()
    {
        await using var harness = new HostHarness();
        await harness.InitializeAsync();

        var completion = harness.Client.SendRequestAsync(CreateJobListRequest);
        var request = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        var response = Success(request.RequestId, new OperationCompleted(new JobListResult("first")));
        await harness.HostWriter.WriteAsync(response);
        var terminal = Assert.IsType<GuardianHostSuccessResponse>(await completion.WaitAsync(TestTimeout));
        Assert.Equal("first", Assert.IsType<JobListResult>(
            Assert.IsType<OperationCompleted>(terminal.Payload).Result).Text);

        await harness.HostWriter.WriteAsync(response);
        var fatal = await harness.Client.Fatal.WaitAsync(TestTimeout);

        Assert.Equal(GuardianHostClientFailureKind.DuplicateOrStaleResponse, fatal.DetailKind);
        Assert.Equal("first", Assert.IsType<JobListResult>(
            Assert.IsType<OperationCompleted>(terminal.Payload).Result).Text);
    }

    [Fact]
    public async Task Unknown_future_response_and_mismatched_operation_result_fail_closed()
    {
        await using (var unknown = new HostHarness())
        {
            await unknown.InitializeAsync();
            await unknown.HostWriter.WriteAsync(Success(
                new PrivateRequestId(999),
                new OperationCompleted(new JobListResult("unknown"))));
            Assert.Equal(
                GuardianHostClientFailureKind.UnknownResponse,
                (await unknown.Client.Fatal.WaitAsync(TestTimeout)).DetailKind);
        }

        await using var mismatched = new HostHarness();
        await mismatched.InitializeAsync();
        var completion = mismatched.Client.SendRequestAsync(CreateJobListRequest);
        var request = Assert.IsType<OperationRequest>(
            await mismatched.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        await mismatched.HostWriter.WriteAsync(Success(
            request.RequestId,
            new OperationCompleted(new JobStatusResult("{}"))));

        var fatal = await mismatched.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientFailureKind.ResponseCorrelationMismatch, fatal.DetailKind);
        Assert.Same(fatal, await Assert.ThrowsAsync<GuardianHostClientException>(() => completion));
    }

    [Fact]
    public async Task Event_sequence_is_monotonic_not_contiguous_and_duplicate_is_never_delivered()
    {
        var observed = new ConcurrentQueue<long>();
        await using var harness = new HostHarness((value, _) =>
        {
            observed.Enqueue(value.EventSequence.Value);
            return ValueTask.CompletedTask;
        });
        await harness.InitializeAsync();

        await harness.HostWriter.WriteAsync(CreateCapabilityEvent(sequence: 3));
        await WaitUntilAsync(() => observed.Count == 1);
        await harness.HostWriter.WriteAsync(CreateCapabilityEvent(sequence: 3));

        var fatal = await harness.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientFailureKind.EventSequenceInvalid, fatal.DetailKind);
        Assert.Equal([3L], observed);
    }

    [Fact]
    public async Task Forged_operation_event_never_reaches_the_handler_or_mutates_sequence_state()
    {
        var handled = 0;
        await using var harness = new HostHarness((_, _) =>
        {
            Interlocked.Increment(ref handled);
            return ValueTask.CompletedTask;
        });
        await harness.InitializeAsync();
        var completion = harness.Client.SendRequestAsync(CreateJobListRequest);
        var request = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));

        await harness.HostWriter.WriteAsync(new OperationDeliveryEvent(
            Guardian,
            Host,
            Generation,
            new HostEventSequence(1),
            request.RequestId,
            Alias,
            Transition,
            Worker,
            null,
            Capability(2),
            GuardianHostDeliveryState.NotDispatched,
            null));

        var fatal = await harness.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientFailureKind.EventCorrelationMismatch, fatal.DetailKind);
        Assert.Equal(0, Volatile.Read(ref handled));
        Assert.Equal(0, harness.Client.LastHostEventSequence);
        Assert.Same(fatal, await Assert.ThrowsAsync<GuardianHostClientException>(() => completion));
    }

    [Fact]
    public async Task Control_request_must_match_the_full_source_envelope_and_claims_once()
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new HostHarness((_, _) =>
        {
            seen.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await harness.InitializeAsync();
        await harness.HostWriter.WriteAsync(CreateCapabilityEvent(sequence: 1));
        await seen.Task.WaitAsync(TestTimeout);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Client.SendRequestAsync(
            (guardian, host, generation, requestId) => new WorkerCreateCapabilityGrantRequest(
                guardian,
                host,
                generation,
                requestId,
                101,
                Alias,
                Transition,
                new WorkerGeneration(2),
                new HostEventSequence(1),
                Capability(3))));
        Assert.Equal(GuardianHostClientState.Ready, harness.Client.State);
        Assert.Equal(0, harness.Client.OutstandingRequestCount);

        var completion = harness.Client.SendRequestAsync(
            (guardian, host, generation, requestId) => new WorkerCreateCapabilityGrantRequest(
                guardian,
                host,
                generation,
                requestId,
                100,
                Alias,
                Transition,
                new WorkerGeneration(2),
                new HostEventSequence(1),
                Capability(3)));
        var grant = Assert.IsType<WorkerCreateCapabilityGrantRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        Assert.Equal(6, grant.RequestId.Value);
        await harness.HostWriter.WriteAsync(Success(
            grant.RequestId,
            new ControlAcknowledged(grant.SourceEventSequence)));
        Assert.IsType<GuardianHostSuccessResponse>(await completion.WaitAsync(TestTimeout));

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Client.SendRequestAsync(
            (guardian, host, generation, requestId) => new WorkerCreateCapabilityGrantRequest(
                guardian,
                host,
                generation,
                requestId,
                100,
                Alias,
                Transition,
                new WorkerGeneration(3),
                new HostEventSequence(1),
                Capability(4))));
    }

    [Fact]
    public async Task Eof_and_malformed_frames_are_bounded_generation_fatals()
    {
        await using (var eof = new HostHarness())
        {
            await eof.InitializeAsync();
            var completion = eof.Client.SendRequestAsync(CreateJobListRequest);
            _ = await eof.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout);
            eof.EventTransport.CompleteWriting();

            var fatal = await eof.Client.Fatal.WaitAsync(TestTimeout);
            Assert.Equal(GuardianHostClientFailureKind.UnexpectedEof, fatal.DetailKind);
            Assert.Same(fatal, await Assert.ThrowsAsync<GuardianHostClientException>(() => completion));
        }

        await using var malformed = new HostHarness();
        await malformed.InitializeAsync();
        await malformed.EventTransport.WriteAsync("{}\n"u8.ToArray());
        var malformedFatal = await malformed.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientFailureKind.ProtocolViolation, malformedFatal.DetailKind);
        Assert.DoesNotContain("{}", malformedFatal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delivery_callback_waits_for_prior_transport_write_to_finish()
    {
        await using var harness = new HostHarness();
        await harness.InitializeAsync();
        var firstCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedWriteStarted = harness.RequestTransport.BlockNextWrite();

        var first = harness.Client.SendRequestAsync(
            CreateJobListRequest,
            () => firstCallback.TrySetResult());
        await firstCallback.Task.WaitAsync(TestTimeout);
        await blockedWriteStarted.WaitAsync(TestTimeout);

        var second = harness.Client.SendRequestAsync(
            CreateJobListRequest,
            () => secondCallback.TrySetResult());
        await Assert.ThrowsAsync<TimeoutException>(() =>
            secondCallback.Task.WaitAsync(TimeSpan.FromMilliseconds(100)));

        harness.RequestTransport.ReleaseBlockedWrite();
        var firstRequest = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        await secondCallback.Task.WaitAsync(TestTimeout);
        var secondRequest = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        Assert.True(secondRequest.RequestId.Value > firstRequest.RequestId.Value);

        await harness.HostWriter.WriteAsync(Success(
            secondRequest.RequestId,
            new OperationCompleted(new JobListResult("second"))));
        await harness.HostWriter.WriteAsync(Success(
            firstRequest.RequestId,
            new OperationCompleted(new JobListResult("first"))));
        await Task.WhenAll(first, second).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Concurrent_requests_keep_global_ids_monotonic_and_allow_out_of_order_terminals()
    {
        await using var harness = new HostHarness();
        await harness.InitializeAsync();
        var sends = Enumerable.Range(0, 16)
            .Select(_ => harness.Client.SendRequestAsync(CreateJobListRequest))
            .ToArray();
        var requests = new List<OperationRequest>();
        for (var index = 0; index < sends.Length; index++)
        {
            requests.Add(Assert.IsType<OperationRequest>(
                await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout)));
        }

        Assert.Equal(
            Enumerable.Range(5, sends.Length).Select(value => (long)value),
            requests.Select(value => value.RequestId.Value));
        foreach (var request in requests.AsEnumerable().Reverse())
        {
            await harness.HostWriter.WriteAsync(Success(
                request.RequestId,
                new OperationCompleted(new JobListResult(request.RequestId.Value.ToString()))));
        }
        await Task.WhenAll(sends).WaitAsync(TestTimeout);
        Assert.Equal(0, harness.Client.OutstandingRequestCount);
    }

    [Fact]
    public async Task Reentrant_event_send_fails_fast_instead_of_deadlocking_the_ordered_reader()
    {
        GuardianHostClient? client = null;
        await using var harness = new HostHarness(async (hostEvent, cancellationToken) =>
        {
            var created = Assert.IsType<WorkerCreateCapabilityRequestedEvent>(hostEvent);
            await client!.SendRequestAsync(
                (guardian, host, generation, requestId) =>
                    new WorkerCreateCapabilityGrantRequest(
                        guardian,
                        host,
                        generation,
                        requestId,
                        created.StartupDeadlineUnixTimeMilliseconds,
                        created.SessionAlias,
                        created.SessionTransitionVersion,
                        new WorkerGeneration(2),
                        created.EventSequence,
                        Capability(8)),
                cancellationToken: cancellationToken);
        });
        client = harness.Client;
        await harness.InitializeAsync();

        await harness.HostWriter.WriteAsync(CreateCapabilityEvent(sequence: 1));
        var fatal = await harness.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientFailureKind.EventHandlerFailed, fatal.DetailKind);
        Assert.Equal(4, harness.RequestTransport.WriteCount);
    }

    [Fact]
    public async Task Dispose_waits_for_event_handler_and_releases_its_generation_lease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaseDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new HostHarness(
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationSeen.TrySetResult();
                }
            },
            _ => new CallbackLease(() => leaseDisposed.TrySetResult()));
        await harness.InitializeAsync();
        await harness.HostWriter.WriteAsync(CreateCapabilityEvent(sequence: 1));
        await entered.Task.WaitAsync(TestTimeout);

        var dispose = harness.DisposeAsync().AsTask();
        await cancellationSeen.Task.WaitAsync(TestTimeout);
        await leaseDisposed.Task.WaitAsync(TestTimeout);
        await dispose.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientState.Stopped, harness.Client.State);
    }

    [Fact]
    public async Task Worker_diagnostic_raw_bytes_are_zeroed_after_the_ordered_handler_returns()
    {
        var observed = new TaskCompletionSource<(WorkerDiagnosticChunkEvent Event, byte[] OwnedBytes)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new HostHarness((hostEvent, _) =>
        {
            var chunk = Assert.IsType<WorkerDiagnosticChunkEvent>(hostEvent);
            Assert.Equal([0x41, 0x00, 0x7f, 0xff], chunk.GetRawBytes());
            observed.TrySetResult((chunk, GetOwnedRawBytes(chunk)));
            return ValueTask.CompletedTask;
        });
        await harness.InitializeAsync();

        using (var outbound = new WorkerDiagnosticChunkEvent(
            Guardian,
            Host,
            Generation,
            new HostEventSequence(1),
            null,
            Alias,
            Transition,
            Worker,
            null,
            GuardianHostDiagnosticStream.Stderr,
            0,
            0,
            new byte[] { 0x41, 0x00, 0x7f, 0xff },
            true))
        {
            await harness.HostWriter.WriteAsync(outbound);
        }

        var captured = await observed.Task.WaitAsync(TestTimeout);
        await WaitUntilAsync(() => RawBytesAreDisposed(captured.Event));
        Assert.All(captured.OwnedBytes, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Output_raw_bytes_are_zeroed_after_the_ordered_handler_returns()
    {
        var observed = new TaskCompletionSource<(OutputChunkEvent Event, byte[] OwnedBytes)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new HostHarness((hostEvent, _) =>
        {
            var chunk = Assert.IsType<OutputChunkEvent>(hostEvent);
            Assert.Equal([0xde, 0xad, 0xbe, 0xef], chunk.GetRawBytes());
            observed.TrySetResult((chunk, GetOwnedRawBytes(chunk)));
            return ValueTask.CompletedTask;
        });
        await harness.InitializeAsync();
        var completion = harness.Client.SendRequestAsync(CreateForegroundRequest);
        var request = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));

        using (var outbound = new OutputChunkEvent(
            Guardian,
            Host,
            Generation,
            new HostEventSequence(1),
            request.RequestId,
            Alias,
            Transition,
            Worker,
            OperationIdentity,
            OutputToken,
            0,
            0,
            new byte[] { 0xde, 0xad, 0xbe, 0xef }))
        {
            await harness.HostWriter.WriteAsync(outbound);
        }

        var captured = await observed.Task.WaitAsync(TestTimeout);
        await WaitUntilAsync(() => RawBytesAreDisposed(captured.Event));
        Assert.All(captured.OwnedBytes, value => Assert.Equal(0, value));

        await harness.HostWriter.WriteAsync(Success(
            request.RequestId,
            new OperationCompleted(new InvokeForegroundResult("complete"))));
        await completion.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Dispose_waits_for_an_in_flight_writer_and_its_generation_lease()
    {
        var leaseDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new HostHarness(
            writeLeaseFactory: () => new CallbackLease(() => leaseDisposed.TrySetResult()));
        await harness.InitializeAsync();
        var blocked = harness.RequestTransport.BlockNextWrite(ignoreCancellation: true);
        var send = harness.Client.SendRequestAsync(CreateJobListRequest);
        await blocked.WaitAsync(TestTimeout);

        var dispose = harness.Client.DisposeAsync().AsTask();
        var duplicateDispose = harness.Client.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);
        Assert.False(duplicateDispose.IsCompleted);
        Assert.False(leaseDisposed.Task.IsCompleted);

        harness.RequestTransport.ReleaseBlockedWrite();
        await duplicateDispose.WaitAsync(TestTimeout);
        await dispose.WaitAsync(TestTimeout);
        await leaseDisposed.Task.WaitAsync(TestTimeout);
        var failure = await Assert.ThrowsAsync<GuardianHostClientException>(() => send);
        Assert.Equal(GuardianHostClientFailureKind.Stopped, failure.DetailKind);
        Assert.Equal(GuardianHostClientState.Stopped, harness.Client.State);
    }

    [Fact]
    public async Task Event_authority_rejection_prevents_handler_mutation()
    {
        var handled = 0;
        await using var harness = new HostHarness(
            (_, _) =>
            {
                Interlocked.Increment(ref handled);
                return ValueTask.CompletedTask;
            },
            _ => null);
        await harness.InitializeAsync();

        await harness.HostWriter.WriteAsync(CreateCapabilityEvent(sequence: 1));
        var fatal = await harness.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientFailureKind.EventCorrelationMismatch, fatal.DetailKind);
        Assert.Equal(0, Volatile.Read(ref handled));
        Assert.Equal(0, harness.Client.LastHostEventSequence);
    }

    [Fact]
    public async Task Unauthorised_control_event_is_never_published_or_claimable()
    {
        var authorityEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthority = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisitions = 0;
        var handled = 0;
        await using var harness = new HostHarness(
            (_, _) =>
            {
                Interlocked.Increment(ref handled);
                return ValueTask.CompletedTask;
            },
            _ =>
            {
                if (Interlocked.Increment(ref acquisitions) != 1)
                    return new CallbackLease(static () => { });
                authorityEntered.TrySetResult();
                releaseAuthority.Task.GetAwaiter().GetResult();
                return null;
            });
        await harness.InitializeAsync();
        await harness.HostWriter.WriteAsync(CreateCapabilityEvent(sequence: 1));
        await authorityEntered.Task.WaitAsync(TestTimeout);

        var grant = harness.Client.SendRequestAsync(
            (guardian, host, generation, requestId) =>
                new WorkerCreateCapabilityGrantRequest(
                    guardian,
                    host,
                    generation,
                    requestId,
                    100,
                    Alias,
                    Transition,
                    new WorkerGeneration(2),
                    new HostEventSequence(1),
                    Capability(9)));
        await Task.Delay(50);
        releaseAuthority.TrySetResult();

        await Assert.ThrowsAsync<ArgumentException>(() => grant.WaitAsync(TestTimeout));
        var fatal = await harness.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientFailureKind.EventCorrelationMismatch, fatal.DetailKind);
        Assert.Equal(1, Volatile.Read(ref acquisitions));
        Assert.Equal(0, Volatile.Read(ref handled));
        Assert.Equal(0, harness.Client.LastHostEventSequence);
        Assert.Equal(4, harness.RequestTransport.WriteCount);
    }

    [Fact]
    public async Task Dispose_during_event_authority_acquisition_cannot_reserve_the_event()
    {
        var authorityEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthority = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = 0;
        await using var harness = new HostHarness(
            (_, _) =>
            {
                Interlocked.Increment(ref handled);
                return ValueTask.CompletedTask;
            },
            _ =>
            {
                authorityEntered.TrySetResult();
                releaseAuthority.Task.GetAwaiter().GetResult();
                return new CallbackLease(static () => { });
            });
        await harness.InitializeAsync();
        await harness.HostWriter.WriteAsync(CreateCapabilityEvent(sequence: 1));
        await authorityEntered.Task.WaitAsync(TestTimeout);

        var dispose = harness.Client.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);
        releaseAuthority.TrySetResult();
        await dispose.WaitAsync(TestTimeout);

        Assert.Equal(0, Volatile.Read(ref handled));
        Assert.Equal(0, harness.Client.LastHostEventSequence);
        Assert.False(harness.Client.Fatal.IsCompleted);
        Assert.Equal(GuardianHostClientState.Stopped, harness.Client.State);
    }

    [Fact]
    public async Task Ordered_handler_finishes_before_a_later_wire_terminal_completes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        await using var harness = new HostHarness(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            finished = true;
        });
        await harness.InitializeAsync();
        var completion = harness.Client.SendRequestAsync(CreateJobListRequest);
        var request = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        await harness.HostWriter.WriteAsync(new OperationDeliveryEvent(
            Guardian, Host, Generation, new HostEventSequence(1), request.RequestId,
            Alias, Transition, Worker, null, DispatchToken,
            GuardianHostDeliveryState.NotDispatched, null));
        await entered.Task.WaitAsync(TestTimeout);
        await harness.HostWriter.WriteAsync(Success(
            request.RequestId,
            new OperationCompleted(new JobListResult("terminal"))));

        Assert.False(completion.IsCompleted);
        release.TrySetResult();
        await completion.WaitAsync(TestTimeout);
        Assert.True(finished);
    }

    [Fact]
    public async Task Operational_request_id_exhaustion_latches_the_generation_fatal()
    {
        await using var harness = new HostHarness(
            requestIds: new MonotonicPrivateRequestIdAllocator(long.MaxValue - 4));
        await harness.InitializeAsync();

        var failure = await Assert.ThrowsAsync<GuardianHostClientException>(() =>
            harness.Client.SendRequestAsync(CreateJobListRequest));

        Assert.Equal(GuardianHostClientFailureKind.RequestIdExhausted, failure.DetailKind);
        Assert.Same(failure, await harness.Client.Fatal.WaitAsync(TestTimeout));
        Assert.Equal(GuardianHostClientState.Faulted, harness.Client.State);
    }

    [Fact]
    public async Task Shutdown_reservation_closes_queued_operation_admission_before_its_write()
    {
        await using var harness = new HostHarness();
        await harness.InitializeAsync();
        var firstWrite = harness.RequestTransport.BlockNextWrite();
        var first = harness.Client.SendRequestAsync(CreateJobListRequest);
        await firstWrite.WaitAsync(TestTimeout);
        var secondWriteStarting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var second = harness.Client.SendRequestAsync(
            CreateJobListRequest,
            () => secondWriteStarting.TrySetResult());

        var shutdown = harness.Client.ShutdownAsync(
            FutureDeadline(),
            GuardianHostShutdownReason.GuardianShutdown);
        Assert.Equal(GuardianHostClientState.Stopping, harness.Client.State);
        harness.RequestTransport.ReleaseBlockedWrite();

        var firstRequest = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        var refused = await Assert.ThrowsAsync<GuardianHostClientException>(() => second);
        Assert.Equal(GuardianHostClientFailureKind.Stopped, refused.DetailKind);
        Assert.False(secondWriteStarting.Task.IsCompleted);
        var shutdownRequest = Assert.IsType<GuardianHostShutdown>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        Assert.Equal(6, harness.RequestTransport.WriteCount);

        await harness.HostWriter.WriteAsync(Success(
            firstRequest.RequestId,
            new OperationCompleted(new JobListResult("before-shutdown"))));
        await first.WaitAsync(TestTimeout);
        await harness.HostWriter.WriteAsync(Success(
            shutdownRequest.RequestId,
            new ShutdownAccepted()));
        harness.EventTransport.CompleteWriting();
        await shutdown.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Shutdown_deadline_while_waiting_for_the_write_gate_is_fatal()
    {
        await using var harness = new HostHarness();
        await harness.InitializeAsync();
        var blockedWrite = harness.RequestTransport.BlockNextWrite();
        var operation = harness.Client.SendRequestAsync(CreateJobListRequest);
        await blockedWrite.WaitAsync(TestTimeout);
        var deadline = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds() + 250;

        var shutdown = harness.Client.ShutdownAsync(
            deadline,
            GuardianHostShutdownReason.GuardianShutdown);
        var failure = await Assert.ThrowsAsync<GuardianHostClientException>(() =>
            shutdown.WaitAsync(TestTimeout));

        Assert.Equal(GuardianHostClientFailureKind.ShutdownIncomplete, failure.DetailKind);
        Assert.Same(failure, await harness.Client.Fatal.WaitAsync(TestTimeout));
        Assert.Same(
            failure,
            await Assert.ThrowsAsync<GuardianHostClientException>(() => operation));
        Assert.Equal(GuardianHostClientState.Faulted, harness.Client.State);
        Assert.Equal(5, harness.RequestTransport.WriteCount);
        Assert.Equal(4, harness.RequestTransport.CompletedWriteCount);
        Assert.Same(
            failure,
            await Assert.ThrowsAsync<GuardianHostClientException>(() =>
                harness.Client.SendRequestAsync(CreateJobListRequest)));
    }

    [Fact]
    public async Task Accepted_shutdown_requires_eof_and_fails_remaining_operations_deterministically()
    {
        await using var harness = new HostHarness();
        await harness.InitializeAsync();
        var operation = harness.Client.SendRequestAsync(CreateJobListRequest);
        _ = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        var shutdown = harness.Client.ShutdownAsync(
            FutureDeadline(),
            GuardianHostShutdownReason.GuardianShutdown);
        var frame = Assert.IsType<GuardianHostShutdown>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        Assert.Equal(GuardianHostClientState.Stopping, harness.Client.State);

        var refused = await Assert.ThrowsAsync<GuardianHostClientException>(() =>
            harness.Client.SendRequestAsync(CreateJobListRequest));
        Assert.Equal(GuardianHostClientFailureKind.Stopped, refused.DetailKind);
        await harness.HostWriter.WriteAsync(Success(frame.RequestId, new ShutdownAccepted()));
        harness.EventTransport.CompleteWriting();

        await shutdown.WaitAsync(TestTimeout);
        var stopped = await Assert.ThrowsAsync<GuardianHostClientException>(() => operation);
        Assert.Equal(GuardianHostClientFailureKind.Stopped, stopped.DetailKind);
        Assert.Equal(GuardianHostClientState.Stopped, harness.Client.State);
        Assert.True(harness.Client.ReaderCompletion.IsCompletedSuccessfully);
        Assert.False(harness.Client.Fatal.IsCompleted);
    }

    [Fact]
    public async Task Shutdown_error_is_never_treated_as_acceptance_or_expected_eof()
    {
        var responseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new HostHarness(
            responseHandler: async (_, _, _) =>
            {
                responseEntered.TrySetResult();
                await releaseResponse.Task;
            });
        await harness.InitializeAsync();
        var shutdown = harness.Client.ShutdownAsync(
            FutureDeadline(),
            GuardianHostShutdownReason.HostRecycle);
        var frame = Assert.IsType<GuardianHostShutdown>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        await harness.HostWriter.WriteAsync(new GuardianHostErrorResponse(
            Guardian,
            Host,
            Generation,
            frame.RequestId,
            new GuardianHostPrivateError(
                GuardianHostPrivateDetailCode.RequestDeadlineExpired)));
        await responseEntered.Task.WaitAsync(TestTimeout);
        harness.EventTransport.CompleteWriting();
        releaseResponse.TrySetResult();

        var failure = await Assert.ThrowsAsync<GuardianHostClientException>(() => shutdown);
        Assert.Equal(GuardianHostClientFailureKind.ShutdownRejected, failure.DetailKind);
        Assert.Same(failure, await harness.Client.Fatal.WaitAsync(TestTimeout));
        Assert.Equal(GuardianHostClientState.Faulted, harness.Client.State);
    }

    [Fact]
    public async Task Shutdown_rejection_is_reserved_on_the_ordered_reader_path()
    {
        await using var harness = new HostHarness();
        await harness.InitializeAsync();
        var shutdown = harness.Client.ShutdownAsync(
            FutureDeadline(),
            GuardianHostShutdownReason.HostRecycle);
        var frame = Assert.IsType<GuardianHostShutdown>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        var response = new GuardianHostErrorResponse(
            Guardian,
            Host,
            Generation,
            frame.RequestId,
            new GuardianHostPrivateError(
                GuardianHostPrivateDetailCode.RequestDeadlineExpired));
        var completeResponse = typeof(GuardianHostClient).GetMethod(
            "CompleteResponseAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var orderedDispatch = Assert.IsAssignableFrom<Task>(completeResponse.Invoke(
            harness.Client,
            [response]));

        var reserved = await Assert.ThrowsAsync<GuardianHostClientException>(() =>
            orderedDispatch.WaitAsync(TestTimeout));
        Assert.Equal(GuardianHostClientFailureKind.ShutdownRejected, reserved.DetailKind);
        Assert.Same(reserved, await Assert.ThrowsAsync<GuardianHostClientException>(() => shutdown));
        Assert.Same(reserved, await harness.Client.Fatal.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Shutdown_wait_cancellation_is_a_bounded_generation_failure_after_write()
    {
        await using var harness = new HostHarness();
        await harness.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        var shutdown = harness.Client.ShutdownAsync(
            FutureDeadline(),
            GuardianHostShutdownReason.GuardianEof,
            cancellation.Token);
        _ = Assert.IsType<GuardianHostShutdown>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        cancellation.Cancel();

        var failure = await Assert.ThrowsAsync<GuardianHostClientException>(() => shutdown);
        Assert.Equal(GuardianHostClientFailureKind.ShutdownIncomplete, failure.DetailKind);
        Assert.Same(failure, await harness.Client.Fatal.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Terminal_handler_finishes_before_a_later_wire_event_is_dispatched()
    {
        var terminalEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTerminal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var eventEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalApplied = false;
        await using var harness = new HostHarness(
            (_, _) =>
            {
                Assert.True(terminalApplied);
                eventEntered.TrySetResult();
                return ValueTask.CompletedTask;
            },
            responseHandler: async (_, _, _) =>
            {
                terminalEntered.TrySetResult();
                await releaseTerminal.Task;
                terminalApplied = true;
            });
        await harness.InitializeAsync();
        var completion = harness.Client.SendRequestAsync(CreateJobListRequest);
        var request = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));

        await harness.HostWriter.WriteAsync(Success(
            request.RequestId,
            new OperationCompleted(new JobListResult("terminal"))));
        await terminalEntered.Task.WaitAsync(TestTimeout);
        await harness.HostWriter.WriteAsync(CreateCapabilityEvent(sequence: 1));

        Assert.False(completion.IsCompleted);
        Assert.False(eventEntered.Task.IsCompleted);
        releaseTerminal.TrySetResult();
        await completion.WaitAsync(TestTimeout);
        await eventEntered.Task.WaitAsync(TestTimeout);
        Assert.True(terminalApplied);
    }

    [Fact]
    public async Task Reserved_terminal_remains_authoritative_when_a_later_write_faults_the_host()
    {
        var terminalEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTerminal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new HostHarness(
            responseHandler: async (_, _, _) =>
            {
                terminalEntered.TrySetResult();
                await releaseTerminal.Task;
            });
        await harness.InitializeAsync();
        var authoritative = harness.Client.SendRequestAsync(CreateJobListRequest);
        var request = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        await harness.HostWriter.WriteAsync(Success(
            request.RequestId,
            new OperationCompleted(new JobListResult("authoritative"))));
        await terminalEntered.Task.WaitAsync(TestTimeout);

        harness.RequestTransport.RejectWrites();
        var later = harness.Client.SendRequestAsync(CreateJobListRequest);
        var fatal = await harness.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientFailureKind.TransportFailure, fatal.DetailKind);
        Assert.Same(fatal, await Assert.ThrowsAsync<GuardianHostClientException>(() => later));

        releaseTerminal.TrySetResult();
        var response = Assert.IsType<GuardianHostSuccessResponse>(
            await authoritative.WaitAsync(TestTimeout));
        Assert.Equal("authoritative", Assert.IsType<JobListResult>(
            Assert.IsType<OperationCompleted>(response.Payload).Result).Text);
    }

    [Fact]
    public async Task Authorized_terminal_candidate_survives_a_fault_before_dictionary_removal()
    {
        var authorityEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthority = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new HostHarness(
            responseLeaseFactory: (_, _) =>
            {
                authorityEntered.TrySetResult();
                releaseAuthority.Task.GetAwaiter().GetResult();
                return new CallbackLease(static () => { });
            });
        await harness.InitializeAsync();
        var authoritative = harness.Client.SendRequestAsync(CreateJobListRequest);
        var request = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        await harness.HostWriter.WriteAsync(Success(
            request.RequestId,
            new OperationCompleted(new JobListResult("authorized-before-fault"))));
        await authorityEntered.Task.WaitAsync(TestTimeout);

        harness.RequestTransport.RejectWrites();
        var later = harness.Client.SendRequestAsync(CreateJobListRequest);
        var fatal = await harness.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientFailureKind.TransportFailure, fatal.DetailKind);
        Assert.Same(fatal, await Assert.ThrowsAsync<GuardianHostClientException>(() => later));

        releaseAuthority.TrySetResult();
        var response = Assert.IsType<GuardianHostSuccessResponse>(
            await authoritative.WaitAsync(TestTimeout));
        Assert.Equal("authorized-before-fault", Assert.IsType<JobListResult>(
            Assert.IsType<OperationCompleted>(response.Payload).Result).Text);
        Assert.Equal(0, harness.Client.OutstandingRequestCount);
    }

    [Fact]
    public async Task Guardian_owned_job_and_session_fields_are_response_correlations()
    {
        await using (var background = new HostHarness())
        {
            await background.InitializeAsync();
            var completion = background.Client.SendRequestAsync(CreateBackgroundRequest);
            var request = Assert.IsType<OperationRequest>(
                await background.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
            await background.HostWriter.WriteAsync(Success(
                request.RequestId,
                new OperationCompleted(new InvokeBackgroundResult(
                    new PublicJobId(2),
                    JobToken))));
            var fatal = await background.Client.Fatal.WaitAsync(TestTimeout);
            Assert.Equal(GuardianHostClientFailureKind.ResponseCorrelationMismatch, fatal.DetailKind);
            Assert.Same(fatal, await Assert.ThrowsAsync<GuardianHostClientException>(() => completion));
        }

        await using var session = new HostHarness();
        await session.InitializeAsync();
        var reset = session.Client.SendRequestAsync(CreateResetRequest);
        var resetRequest = Assert.IsType<OperationRequest>(
            await session.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        await session.HostWriter.WriteAsync(Success(
            resetRequest.RequestId,
            new OperationCompleted(new ResetResult(
                new CanonicalAlias("other"),
                PublicSessionState.Ready,
                Worker,
                Transition,
                true,
                false,
                BootstrapState.Restored))));
        var sessionFatal = await session.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(
            GuardianHostClientFailureKind.ResponseCorrelationMismatch,
            sessionFatal.DetailKind);
        Assert.Same(sessionFatal, await Assert.ThrowsAsync<GuardianHostClientException>(() => reset));
    }

    [Fact]
    public async Task Missing_write_authority_refuses_dispatch_without_faulting_the_host()
    {
        await using var harness = new HostHarness(writeLeaseFactory: () => null);
        await harness.InitializeAsync();
        var writesBefore = harness.RequestTransport.WriteCount;

        var failure = await Assert.ThrowsAsync<GuardianHostClientException>(() =>
            harness.Client.SendRequestAsync(CreateJobListRequest));

        Assert.Equal(GuardianHostClientFailureKind.WriteAuthorityRejected, failure.DetailKind);
        Assert.Equal(writesBefore, harness.RequestTransport.WriteCount);
        Assert.Equal(GuardianHostClientState.Ready, harness.Client.State);
        Assert.False(harness.Client.Fatal.IsCompleted);
    }

    [Fact]
    public async Task Caller_cancellation_while_request_factory_is_blocked_prevents_dispatch()
    {
        var factoryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writeStarting = 0;
        await using var harness = new HostHarness();
        await harness.InitializeAsync();
        using var cancellation = new CancellationTokenSource();

        var send = Task.Run(() => harness.Client.SendRequestAsync(
            (guardian, host, generation, requestId) =>
            {
                factoryEntered.TrySetResult();
                releaseFactory.Task.GetAwaiter().GetResult();
                return CreateJobListRequest(guardian, host, generation, requestId);
            },
            () => Interlocked.Increment(ref writeStarting),
            cancellation.Token));
        await factoryEntered.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        releaseFactory.TrySetResult();
        await WaitUntilAsync(() => send.IsCompleted || harness.RequestTransport.WriteCount > 4);

        Assert.True(send.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.Equal(0, Volatile.Read(ref writeStarting));
        Assert.Equal(4, harness.RequestTransport.WriteCount);
        Assert.Equal(0, harness.Client.OutstandingRequestCount);
        Assert.Equal(GuardianHostClientState.Ready, harness.Client.State);
        Assert.False(harness.Client.Fatal.IsCompleted);
    }

    [Fact]
    public async Task Caller_cancellation_before_cancel_dispatch_has_zero_effect_and_no_host_fault()
    {
        await using var harness = new HostHarness();
        await harness.InitializeAsync();
        var completion = harness.Client.SendRequestAsync(CreateJobListRequest);
        var request = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        var writesBefore = harness.RequestTransport.WriteCount;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await harness.Client.TrySendCancelAsync(
                request.RequestId,
                GuardianHostCancelReason.CallerCanceled,
                cancellation.Token));
        Assert.Equal(writesBefore, harness.RequestTransport.WriteCount);
        Assert.Equal(GuardianHostClientState.Ready, harness.Client.State);
        Assert.False(harness.Client.Fatal.IsCompleted);

        await harness.HostWriter.WriteAsync(Success(
            request.RequestId,
            new OperationCompleted(new JobListResult("done"))));
        await completion.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Caller_cancellation_while_cancel_id_allocation_is_blocked_prevents_dispatch()
    {
        var requestIds = new BlockingPrivateRequestIdAllocator(blockAt: 6);
        await using var harness = new HostHarness(requestIds: requestIds);
        await harness.InitializeAsync();
        var completion = harness.Client.SendRequestAsync(CreateJobListRequest);
        var request = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        using var cancellation = new CancellationTokenSource();

        var cancel = Task.Run(async () => await harness.Client.TrySendCancelAsync(
            request.RequestId,
            GuardianHostCancelReason.CallerCanceled,
            cancellation.Token));
        await requestIds.Blocked.WaitAsync(TestTimeout);
        cancellation.Cancel();
        requestIds.Release();
        await WaitUntilAsync(() => cancel.IsCompleted || harness.RequestTransport.WriteCount > 5);

        Assert.True(cancel.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancel);
        Assert.Equal(5, harness.RequestTransport.WriteCount);
        Assert.Equal(1, harness.Client.OutstandingRequestCount);
        Assert.Equal(GuardianHostClientState.Ready, harness.Client.State);
        Assert.False(harness.Client.Fatal.IsCompleted);

        await harness.HostWriter.WriteAsync(Success(
            request.RequestId,
            new OperationCompleted(new JobListResult("done"))));
        await completion.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Control_source_authority_is_reacquired_and_held_through_the_write()
    {
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLeaseDisposed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisitions = 0;
        await using var harness = new HostHarness(
            (_, _) =>
            {
                handled.TrySetResult();
                return ValueTask.CompletedTask;
            },
            _ => Interlocked.Increment(ref acquisitions) == 2
                ? new CallbackLease(() => secondLeaseDisposed.TrySetResult())
                : new CallbackLease(static () => { }));
        await harness.InitializeAsync();
        await harness.HostWriter.WriteAsync(CreateCapabilityEvent(sequence: 1));
        await handled.Task.WaitAsync(TestTimeout);
        var blockedWrite = harness.RequestTransport.BlockNextWrite();

        var completion = harness.Client.SendRequestAsync(
            (guardian, host, generation, requestId) => new WorkerCreateCapabilityGrantRequest(
                guardian, host, generation, requestId, 100, Alias, Transition,
                new WorkerGeneration(2), new HostEventSequence(1), Capability(9)));
        await blockedWrite.WaitAsync(TestTimeout);
        Assert.Equal(2, Volatile.Read(ref acquisitions));
        Assert.False(secondLeaseDisposed.Task.IsCompleted);

        harness.RequestTransport.ReleaseBlockedWrite();
        var grant = Assert.IsType<WorkerCreateCapabilityGrantRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        await secondLeaseDisposed.Task.WaitAsync(TestTimeout);
        await harness.HostWriter.WriteAsync(Success(
            grant.RequestId,
            new ControlAcknowledged(grant.SourceEventSequence)));
        await completion.WaitAsync(TestTimeout);
    }

    [Theory]
    [InlineData(GuardianHostOperationKind.Reset)]
    [InlineData(GuardianHostOperationKind.SessionOpen)]
    [InlineData(GuardianHostOperationKind.SessionClose)]
    [InlineData(GuardianHostOperationKind.SessionRestart)]
    public async Task Session_change_events_accept_the_authorized_result_worker(
        GuardianHostOperationKind operationKind)
    {
        var expectedEventWorker = operationKind == GuardianHostOperationKind.SessionClose
            ? null
            : ReplacementWorker;
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new HostHarness(
            (hostEvent, _) =>
            {
                var lifecycle = Assert.IsType<SessionLifecycleEvent>(hostEvent);
                Assert.True(WorkerIdentitiesEqual(expectedEventWorker, lifecycle.WorkerIdentity));
                handled.TrySetResult();
                return ValueTask.CompletedTask;
            },
            hostEvent => hostEvent is SessionLifecycleEvent lifecycle &&
                    WorkerIdentitiesEqual(expectedEventWorker, lifecycle.WorkerIdentity)
                ? new CallbackLease(static () => { })
                : null);
        await harness.InitializeAsync();
        var completion = harness.Client.SendRequestAsync(
            (guardian, host, generation, requestId) => CreateSessionRequest(
                operationKind, guardian, host, generation, requestId));
        var request = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));

        await harness.HostWriter.WriteAsync(new SessionLifecycleEvent(
            Guardian,
            Host,
            Generation,
            new HostEventSequence(1),
            request.RequestId,
            Alias,
            Transition,
            expectedEventWorker,
            null,
            operationKind == GuardianHostOperationKind.SessionClose
                ? PublicSessionState.Cold
                : PublicSessionState.Ready,
            RequestedReason(operationKind),
            operationKind != GuardianHostOperationKind.SessionClose,
            operationKind is GuardianHostOperationKind.Reset or
                GuardianHostOperationKind.SessionRestart,
            operationKind == GuardianHostOperationKind.SessionClose
                ? BootstrapState.NotApplicable
                : BootstrapState.Restored));
        await handled.Task.WaitAsync(TestTimeout);
        await harness.HostWriter.WriteAsync(Success(
            request.RequestId,
            new OperationCompleted(CreateSessionResult(operationKind, expectedEventWorker))));

        await completion.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientState.Ready, harness.Client.State);
    }

    [Fact]
    public async Task Requested_lifecycle_reason_cannot_satisfy_a_different_session_operation()
    {
        var handled = 0;
        await using var harness = new HostHarness((_, _) =>
        {
            Interlocked.Increment(ref handled);
            return ValueTask.CompletedTask;
        });
        await harness.InitializeAsync();
        var completion = harness.Client.SendRequestAsync(
            (guardian, host, generation, requestId) => CreateSessionRequest(
                GuardianHostOperationKind.SessionOpen,
                guardian,
                host,
                generation,
                requestId));
        var request = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));

        await harness.HostWriter.WriteAsync(new SessionLifecycleEvent(
            Guardian, Host, Generation, new HostEventSequence(1), request.RequestId,
            Alias, Transition, ReplacementWorker, null, PublicSessionState.Ready,
            GuardianHostSessionLifecycleReason.RequestedReset,
            true, false, BootstrapState.Restored));

        var fatal = await harness.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientFailureKind.EventCorrelationMismatch, fatal.DetailKind);
        Assert.Equal(0, Volatile.Read(ref handled));
        Assert.Same(fatal, await Assert.ThrowsAsync<GuardianHostClientException>(() => completion));
    }

    [Fact]
    public async Task Truthful_failure_reason_during_invoke_reaches_the_authority_handler()
    {
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new HostHarness((hostEvent, _) =>
        {
            var lifecycle = Assert.IsType<SessionLifecycleEvent>(hostEvent);
            Assert.Equal(GuardianHostSessionLifecycleReason.ExecutionTimeout, lifecycle.Reason);
            handled.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await harness.InitializeAsync();
        var completion = harness.Client.SendRequestAsync(CreateForegroundRequest);
        var request = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));

        await harness.HostWriter.WriteAsync(new SessionLifecycleEvent(
            Guardian, Host, Generation, new HostEventSequence(1), request.RequestId,
            Alias, Transition, Worker, PublicSessionState.Ready, PublicSessionState.Faulted,
            GuardianHostSessionLifecycleReason.ExecutionTimeout,
            false, true, BootstrapState.Unknown));
        await handled.Task.WaitAsync(TestTimeout);
        await harness.HostWriter.WriteAsync(Success(
            request.RequestId,
            new OperationCompleted(new InvokeForegroundResult("timed out"))));

        await completion.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientState.Ready, harness.Client.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invoke_lifecycle_event_must_name_the_request_worker(bool nullWorker)
    {
        var handled = 0;
        await using var harness = new HostHarness((_, _) =>
        {
            Interlocked.Increment(ref handled);
            return ValueTask.CompletedTask;
        });
        await harness.InitializeAsync();
        var completion = harness.Client.SendRequestAsync(CreateForegroundRequest);
        var request = Assert.IsType<OperationRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));

        await harness.HostWriter.WriteAsync(new SessionLifecycleEvent(
            Guardian,
            Host,
            Generation,
            new HostEventSequence(1),
            request.RequestId,
            Alias,
            Transition,
            nullWorker ? null : ReplacementWorker,
            PublicSessionState.Ready,
            PublicSessionState.Faulted,
            GuardianHostSessionLifecycleReason.ExecutionTimeout,
            false,
            true,
            BootstrapState.Unknown));

        var fatal = await harness.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientFailureKind.EventCorrelationMismatch, fatal.DetailKind);
        Assert.Equal(0, Volatile.Read(ref handled));
        Assert.Equal(0, harness.Client.LastHostEventSequence);
        Assert.Same(fatal, await Assert.ThrowsAsync<GuardianHostClientException>(() => completion));
    }

    [Fact]
    public async Task Response_authority_rejection_and_handler_failure_are_bounded_fatals()
    {
        await using (var rejected = new HostHarness(
            responseLeaseFactory: (_, _) => null))
        {
            await rejected.InitializeAsync();
            var completion = rejected.Client.SendRequestAsync(CreateJobListRequest);
            var request = Assert.IsType<OperationRequest>(
                await rejected.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
            await rejected.HostWriter.WriteAsync(Success(
                request.RequestId,
                new OperationCompleted(new JobListResult("unleased"))));
            var fatal = await rejected.Client.Fatal.WaitAsync(TestTimeout);
            Assert.Equal(
                GuardianHostClientFailureKind.ResponseCorrelationMismatch,
                fatal.DetailKind);
            Assert.Same(fatal, await Assert.ThrowsAsync<GuardianHostClientException>(() => completion));
        }

        await using var failedHandler = new HostHarness(
            responseHandler: (_, _, _) =>
                ValueTask.FromException(new InvalidOperationException("sensitive raw detail")));
        await failedHandler.InitializeAsync();
        var failedCompletion = failedHandler.Client.SendRequestAsync(CreateJobListRequest);
        var failedRequest = Assert.IsType<OperationRequest>(
            await failedHandler.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        await failedHandler.HostWriter.WriteAsync(Success(
            failedRequest.RequestId,
            new OperationCompleted(new JobListResult("decoded"))));
        var handlerFatal = await failedHandler.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientFailureKind.ResponseHandlerFailed, handlerFatal.DetailKind);
        Assert.DoesNotContain("sensitive", handlerFatal.Message, StringComparison.Ordinal);
        Assert.Same(
            handlerFatal,
            await Assert.ThrowsAsync<GuardianHostClientException>(() => failedCompletion));
    }

    [Fact]
    public async Task Eof_before_shutdown_acceptance_is_unexpected_and_fails_the_shutdown_owner()
    {
        await using var harness = new HostHarness();
        await harness.InitializeAsync();
        var shutdown = harness.Client.ShutdownAsync(
            FutureDeadline(),
            GuardianHostShutdownReason.GuardianShutdown);
        _ = Assert.IsType<GuardianHostShutdown>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        harness.EventTransport.CompleteWriting();

        var fatal = await harness.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientFailureKind.UnexpectedEof, fatal.DetailKind);
        Assert.Same(fatal, await Assert.ThrowsAsync<GuardianHostClientException>(() => shutdown));
    }

    [Fact]
    public async Task Any_frame_after_shutdown_acceptance_is_protocol_fatal()
    {
        await using var harness = new HostHarness();
        await harness.InitializeAsync();
        var shutdown = harness.Client.ShutdownAsync(
            FutureDeadline(),
            GuardianHostShutdownReason.HostRecycle);
        var frame = Assert.IsType<GuardianHostShutdown>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        await harness.HostWriter.WriteAsync(Success(frame.RequestId, new ShutdownAccepted()));
        await WaitUntilAsync(() => harness.Client.OutstandingRequestCount == 0);
        await harness.HostWriter.WriteAsync(CreateCapabilityEvent(sequence: 1));

        var fatal = await harness.Client.Fatal.WaitAsync(TestTimeout);
        Assert.Equal(GuardianHostClientFailureKind.ProtocolViolation, fatal.DetailKind);
        Assert.Same(fatal, await Assert.ThrowsAsync<GuardianHostClientException>(() => shutdown));
    }

    [Fact]
    public async Task Shutdown_uses_its_reserved_slot_when_operational_capacity_is_full()
    {
        await using var harness = new HostHarness();
        await harness.InitializeAsync();
        var operations = Enumerable.Range(0, GuardianHostClient.MaximumOutstandingRequests)
            .Select(_ => harness.Client.SendRequestAsync(CreateJobListRequest))
            .ToArray();
        for (var index = 0; index < operations.Length; index++)
        {
            _ = Assert.IsType<OperationRequest>(
                await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        }
        Assert.Equal(
            GuardianHostClient.MaximumOutstandingRequests,
            harness.Client.OutstandingRequestCount);

        var shutdown = harness.Client.ShutdownAsync(
            FutureDeadline(),
            GuardianHostShutdownReason.GuardianShutdown);
        var frame = Assert.IsType<GuardianHostShutdown>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        await harness.HostWriter.WriteAsync(Success(frame.RequestId, new ShutdownAccepted()));
        harness.EventTransport.CompleteWriting();
        await shutdown.WaitAsync(TestTimeout);

        foreach (var operation in operations)
        {
            var stopped = await Assert.ThrowsAsync<GuardianHostClientException>(() => operation);
            Assert.Equal(GuardianHostClientFailureKind.Stopped, stopped.DetailKind);
        }
        Assert.Equal(GuardianHostClientState.Stopped, harness.Client.State);
    }

    [Fact]
    public async Task Sixty_fifth_operational_request_is_refused_without_a_wire_write_or_fatal()
    {
        await using var harness = new HostHarness();
        await harness.InitializeAsync();
        var completions = Enumerable.Range(0, GuardianHostClient.MaximumOutstandingRequests)
            .Select(_ => harness.Client.SendRequestAsync(CreateJobListRequest))
            .ToArray();
        var requests = new OperationRequest[completions.Length];
        for (var index = 0; index < requests.Length; index++)
        {
            requests[index] = Assert.IsType<OperationRequest>(
                await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        }
        var writesAtCapacity = harness.RequestTransport.WriteCount;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Client.SendRequestAsync(CreateJobListRequest));
        Assert.Equal(writesAtCapacity, harness.RequestTransport.WriteCount);
        Assert.Equal(
            GuardianHostClient.MaximumOutstandingRequests,
            harness.Client.OutstandingRequestCount);
        Assert.Equal(GuardianHostClientState.Ready, harness.Client.State);
        Assert.False(harness.Client.Fatal.IsCompleted);

        foreach (var request in requests)
        {
            await harness.HostWriter.WriteAsync(Success(
                request.RequestId,
                new OperationCompleted(new JobListResult("complete"))));
        }
        await Task.WhenAll(completions).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Sixty_fifth_unclaimed_control_event_is_a_bounded_fatal()
    {
        var handled = 0;
        await using var harness = new HostHarness((_, _) =>
        {
            Interlocked.Increment(ref handled);
            return ValueTask.CompletedTask;
        });
        await harness.InitializeAsync();
        for (var sequence = 1; sequence <= GuardianHostClient.MaximumUnacknowledgedControlEvents; sequence++)
            await harness.HostWriter.WriteAsync(CreateCapabilityEvent(sequence));
        await WaitUntilAsync(() =>
            harness.Client.LastHostEventSequence ==
                GuardianHostClient.MaximumUnacknowledgedControlEvents);

        await harness.HostWriter.WriteAsync(CreateCapabilityEvent(
            GuardianHostClient.MaximumUnacknowledgedControlEvents + 1L));
        var fatal = await harness.Client.Fatal.WaitAsync(TestTimeout);

        Assert.Equal(GuardianHostClientFailureKind.EventCorrelationMismatch, fatal.DetailKind);
        Assert.Equal(
            GuardianHostClient.MaximumUnacknowledgedControlEvents,
            Volatile.Read(ref handled));
        Assert.Equal(
            GuardianHostClient.MaximumUnacknowledgedControlEvents,
            harness.Client.LastHostEventSequence);
    }

    private static async Task DriveHostileHandshakeAsync(
        HostHarness harness,
        string scenario)
    {
        var otherManifest = new ManifestId(
            Guid.Parse("33333333-3333-4333-8333-333333333333"));
        if (scenario == "eof_hello")
        {
            harness.EventTransport.CompleteWriting();
            return;
        }
        if (scenario == "wrong_kind_hello")
        {
            await WriteRawDiagnosticAsync(harness);
            return;
        }

        await harness.HostWriter.WriteAsync(harness.CreateHello());
        var initialize = Assert.IsType<GuardianHostInitialize>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        var header = Assert.IsType<ManifestHeaderRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        if (scenario == "eof_header")
        {
            harness.EventTransport.CompleteWriting();
            return;
        }
        if (scenario == "header_request_id")
        {
            await harness.HostWriter.WriteAsync(Success(
                new PrivateRequestId(header.RequestId.Value + 100),
                new ManifestHeaderAccepted(header.ManifestId)));
            return;
        }
        if (scenario == "header_manifest")
        {
            await harness.HostWriter.WriteAsync(Success(
                header.RequestId,
                new ManifestHeaderAccepted(otherManifest)));
            return;
        }
        await harness.HostWriter.WriteAsync(Success(
            header.RequestId,
            new ManifestHeaderAccepted(header.ManifestId)));

        using var transferred = new MemoryStream(header.TotalBytes);
        for (var index = 0; index < header.ChunkCount; index++)
        {
            using var chunk = Assert.IsType<ManifestChunkRequest>(
                await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
            var rawBytes = chunk.GetRawBytes();
            var nextOffset = checked((int)transferred.Length + rawBytes.Length);
            if (index == 0 && scenario == "eof_chunk")
            {
                harness.EventTransport.CompleteWriting();
                return;
            }
            if (index == 0 && scenario == "chunk_request_id")
            {
                await harness.HostWriter.WriteAsync(Success(
                    new PrivateRequestId(chunk.RequestId.Value + 100),
                    new ManifestChunkAccepted(chunk.ManifestId, chunk.ChunkIndex, nextOffset)));
                return;
            }
            if (index == 0 && scenario == "chunk_manifest")
            {
                await harness.HostWriter.WriteAsync(Success(
                    chunk.RequestId,
                    new ManifestChunkAccepted(otherManifest, chunk.ChunkIndex, nextOffset)));
                return;
            }
            if (index == 0 && scenario == "chunk_index")
            {
                await harness.HostWriter.WriteAsync(Success(
                    chunk.RequestId,
                    new ManifestChunkAccepted(
                        chunk.ManifestId,
                        chunk.ChunkIndex + 1,
                        nextOffset)));
                return;
            }
            if (index == 0 && scenario == "chunk_offset")
            {
                await harness.HostWriter.WriteAsync(Success(
                    chunk.RequestId,
                    new ManifestChunkAccepted(
                        chunk.ManifestId,
                        chunk.ChunkIndex,
                        nextOffset + 1)));
                return;
            }
            await transferred.WriteAsync(rawBytes);
            await harness.HostWriter.WriteAsync(Success(
                chunk.RequestId,
                new ManifestChunkAccepted(chunk.ManifestId, chunk.ChunkIndex, nextOffset)));
        }

        var seal = Assert.IsType<ManifestSealRequest>(
            await harness.GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
        if (scenario == "eof_seal")
        {
            harness.EventTransport.CompleteWriting();
            return;
        }
        var transferredBytes = transferred.ToArray();
        var digest = Sha256Digest.Compute(transferredBytes);
        if (scenario == "seal_request_id")
        {
            await harness.HostWriter.WriteAsync(Success(
                new PrivateRequestId(seal.RequestId.Value + 100),
                new ManifestSealed(seal.ManifestId, digest, transferredBytes.Length)));
            return;
        }
        if (scenario == "seal_manifest")
        {
            await harness.HostWriter.WriteAsync(Success(
                seal.RequestId,
                new ManifestSealed(otherManifest, digest, transferredBytes.Length)));
            return;
        }
        if (scenario == "seal_digest")
        {
            await harness.HostWriter.WriteAsync(Success(
                seal.RequestId,
                new ManifestSealed(seal.ManifestId, Digest('8'), transferredBytes.Length)));
            return;
        }
        if (scenario == "seal_size")
        {
            await harness.HostWriter.WriteAsync(Success(
                seal.RequestId,
                new ManifestSealed(seal.ManifestId, digest, transferredBytes.Length + 1)));
            return;
        }
        await harness.HostWriter.WriteAsync(Success(
            seal.RequestId,
            new ManifestSealed(seal.ManifestId, digest, transferredBytes.Length)));

        if (scenario == "eof_ready")
        {
            harness.EventTransport.CompleteWriting();
            return;
        }
        if (scenario == "wrong_kind_ready")
        {
            await WriteRawDiagnosticAsync(harness);
            return;
        }
        await harness.HostWriter.WriteAsync(new GuardianHostReady(
            Guardian,
            Host,
            Generation,
            scenario == "ready_initialize_id"
                ? new PrivateRequestId(initialize.RequestId.Value + 100)
                : initialize.RequestId,
            scenario == "ready_manifest" ? otherManifest : seal.ManifestId,
            scenario == "ready_digest" ? Digest('8') : digest,
            scenario == "ready_pid" ? 43 : 42));
    }

    private static async Task WriteRawDiagnosticAsync(HostHarness harness)
    {
        using var rawEvent = new WorkerDiagnosticChunkEvent(
            Guardian,
            Host,
            Generation,
            new HostEventSequence(1),
            null,
            Alias,
            Transition,
            Worker,
            null,
            GuardianHostDiagnosticStream.Stderr,
            0,
            0,
            new byte[] { 0x73, 0x65, 0x63, 0x72, 0x65, 0x74 },
            true);
        await harness.HostWriter.WriteAsync(rawEvent);
    }

    private static string FindGuardianHostClientSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "PtkMcpGuardian",
                "Host",
                "GuardianHostClient.cs");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("GuardianHostClient.cs was not found from the test output tree.");
    }

    private static OperationRequest CreateJobListRequest(
        GuardianBootId guardian,
        HostBootId host,
        HostGeneration generation,
        PrivateRequestId requestId) => new(
            guardian,
            host,
            generation,
            requestId,
            100,
            Alias,
            Transition,
            Worker,
            null,
            new JobListOperation(Call, new DispatchCapability(DispatchToken, Call, 100)));

    private static OperationRequest CreateBackgroundRequest(
        GuardianBootId guardian,
        HostBootId host,
        HostGeneration generation,
        PrivateRequestId requestId) => new(
            guardian,
            host,
            generation,
            requestId,
            100,
            Alias,
            Transition,
            Worker,
            OperationIdentity,
            new InvokeBackgroundOperation(
                Call,
                new DispatchCapability(DispatchToken, Call, 100),
                new OutputCapability(OutputToken, 1_024, 100),
                "Get-Date",
                false,
                GuardianHostInvokeRoute.Pwsh,
                new PublicJobId(1)));

    private static OperationRequest CreateResetRequest(
        GuardianBootId guardian,
        HostBootId host,
        HostGeneration generation,
        PrivateRequestId requestId) => new(
            guardian,
            host,
            generation,
            requestId,
            100,
            Alias,
            Transition,
            Worker,
            null,
            new ResetOperation(
                Call,
                new DispatchCapability(DispatchToken, Call, 100),
                1,
                false));

    private static OperationRequest CreateForegroundRequest(
        GuardianBootId guardian,
        HostBootId host,
        HostGeneration generation,
        PrivateRequestId requestId) => new(
            guardian,
            host,
            generation,
            requestId,
            100,
            Alias,
            Transition,
            Worker,
            OperationIdentity,
            new InvokeForegroundOperation(
                Call,
                new DispatchCapability(DispatchToken, Call, 100),
                new OutputCapability(OutputToken, 1_024, 100),
                "Get-Date",
                false,
                GuardianHostInvokeRoute.Pwsh));

    private static OperationRequest CreateSessionRequest(
        GuardianHostOperationKind operationKind,
        GuardianBootId guardian,
        HostBootId host,
        HostGeneration generation,
        PrivateRequestId requestId)
    {
        GuardianHostOperation operation = operationKind switch
        {
            GuardianHostOperationKind.Reset => new ResetOperation(
                Call, new DispatchCapability(DispatchToken, Call, 100), 1, false),
            GuardianHostOperationKind.SessionOpen => new SessionOpenOperation(
                Call, new DispatchCapability(DispatchToken, Call, 100), null, false),
            GuardianHostOperationKind.SessionClose => new SessionCloseOperation(
                Call, new DispatchCapability(DispatchToken, Call, 100), 1, false),
            GuardianHostOperationKind.SessionRestart => new SessionRestartOperation(
                Call, new DispatchCapability(DispatchToken, Call, 100), 1, false),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind)),
        };
        return new OperationRequest(
            guardian, host, generation, requestId, 100, Alias, Transition,
            operationKind == GuardianHostOperationKind.SessionOpen ? null : Worker,
            null,
            operation);
    }

    private static GuardianHostOperationResult CreateSessionResult(
        GuardianHostOperationKind operationKind,
        GuardianHostWorkerIdentity? workerIdentity)
    {
        var state = operationKind == GuardianHostOperationKind.SessionClose
            ? PublicSessionState.Cold
            : PublicSessionState.Ready;
        var ready = operationKind != GuardianHostOperationKind.SessionClose;
        var bootstrap = ready ? BootstrapState.Restored : BootstrapState.NotApplicable;
        return operationKind switch
        {
            GuardianHostOperationKind.Reset => new ResetResult(
                Alias, state, workerIdentity, Transition, ready, true, bootstrap),
            GuardianHostOperationKind.SessionOpen => new SessionOpenResult(
                Alias, state, workerIdentity, Transition, ready, false, bootstrap),
            GuardianHostOperationKind.SessionClose => new SessionCloseResult(
                Alias, state, workerIdentity, Transition, ready, false, bootstrap),
            GuardianHostOperationKind.SessionRestart => new SessionRestartResult(
                Alias, state, workerIdentity, Transition, ready, true, bootstrap),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind)),
        };
    }

    private static GuardianHostSessionLifecycleReason RequestedReason(
        GuardianHostOperationKind operationKind) => operationKind switch
        {
            GuardianHostOperationKind.Reset => GuardianHostSessionLifecycleReason.RequestedReset,
            GuardianHostOperationKind.SessionOpen => GuardianHostSessionLifecycleReason.RequestedOpen,
            GuardianHostOperationKind.SessionClose => GuardianHostSessionLifecycleReason.RequestedClose,
            GuardianHostOperationKind.SessionRestart => GuardianHostSessionLifecycleReason.RequestedRestart,
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind)),
        };

    private static bool WorkerIdentitiesEqual(
        GuardianHostWorkerIdentity? left,
        GuardianHostWorkerIdentity? right) =>
        left is null && right is null ||
        left is not null && right is not null &&
        left.BootId == right.BootId &&
        left.Generation == right.Generation;

    private static WorkerCreateCapabilityRequestedEvent CreateCapabilityEvent(long sequence) => new(
        Guardian,
        Host,
        Generation,
        new HostEventSequence(sequence),
        Alias,
        Transition,
        BindingDigest,
        100);

    private static SessionLifecycleEvent CreateSessionEvent(long sequence) => new(
        Guardian,
        Host,
        Generation,
        new HostEventSequence(sequence),
        null,
        Alias,
        Transition,
        null,
        PublicSessionState.Cold,
        PublicSessionState.Starting,
        GuardianHostSessionLifecycleReason.RequestedOpen,
        false,
        false,
        BootstrapState.Pending);

    private static GuardianHostSuccessResponse Success(
        PrivateRequestId requestId,
        GuardianHostSuccessPayload payload) => new(
            Guardian,
            Host,
            Generation,
            requestId,
            payload);

    private static RecoveryManifest CreateManifest(
        GuardianBootId? guardian = null,
        HostGeneration? generation = null,
        Sha256Digest? catalog = null,
        Sha256Digest? configuration = null)
    {
        guardian ??= Guardian;
        generation ??= Generation;
        return new RecoveryManifest(
            guardian,
            generation,
            catalog ?? CatalogDigest,
            configuration ?? ConfigurationDigest,
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
            [new WorkerGenerationHighWatermarkEntry(Alias, new WorkerGenerationHighWatermark(0))],
            generation);
    }

    private static GuardianHostClientPins CreatePins() => new(
        Guardian,
        Host,
        Generation,
        42,
        ExecutableDigest,
        BuildDigest,
        PublicContractDigest,
        ConfigurationDigest,
        CatalogDigest,
        PackageManifestDigest);

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

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        while (!predicate())
            await Task.Delay(10, cancellation.Token);
    }

    private static byte[] GetOwnedRawBytes(object value) =>
        Assert.IsType<byte[]>(value.GetType()
            .GetField("_rawBytes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(value));

    private static bool RawBytesAreDisposed(WorkerDiagnosticChunkEvent value)
    {
        try
        {
            _ = value.GetRawBytes();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static bool RawBytesAreDisposed(OutputChunkEvent value)
    {
        try
        {
            _ = value.GetRawBytes();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private sealed record HandshakeTranscript(
        IReadOnlyList<long> GuardianRequestIds,
        byte[] ManifestBytes,
        Sha256Digest ManifestDigest);

    private sealed class HostHarness : IAsyncDisposable
    {
        internal HostHarness(
            Func<GuardianHostEvent, CancellationToken, ValueTask>? eventHandler = null,
            Func<GuardianHostEvent, IDisposable?>? eventLeaseFactory = null,
            Func<GuardianHostMessage, GuardianHostResponse, IDisposable?>?
                responseLeaseFactory = null,
            Func<GuardianHostMessage, GuardianHostResponse, CancellationToken, ValueTask>?
                responseHandler = null,
            IPrivateRequestIdAllocator? requestIds = null,
            Func<IDisposable?>? writeLeaseFactory = null)
        {
            Manifest = CreateManifest();
            Client = new GuardianHostClient(
                RequestTransport,
                EventTransport,
                CreatePins(),
                requestIds ?? new MonotonicPrivateRequestIdAllocator(),
                TimeProvider.System,
                writeLeaseFactory ?? (() => new CallbackLease(static () => { })),
                eventLeaseFactory ?? AcquireEventLease,
                responseLeaseFactory ?? ((_, _) => new CallbackLease(static () => { })),
                eventHandler ?? ((_, _) => ValueTask.CompletedTask),
                responseHandler ?? ((_, _, _) => ValueTask.CompletedTask),
                () => GuardianHostClientTests.Manifest);
            GuardianReader = new GuardianHostProtocolReader(
                RequestTransport,
                GuardianHostPeer.Guardian);
            HostWriter = new GuardianHostProtocolWriter(EventTransport, GuardianHostPeer.Host);
        }

        internal TestTransportStream RequestTransport { get; } = new();
        internal TestTransportStream EventTransport { get; } = new();
        internal GuardianHostClient Client { get; }
        internal GuardianHostProtocolReader GuardianReader { get; }
        internal GuardianHostProtocolWriter HostWriter { get; }
        internal RecoveryManifest Manifest { get; }

        internal async Task<HandshakeTranscript> InitializeAsync()
        {
            var host = CompleteHandshakeAsync();
            var guardian = Client.InitializeAsync(Manifest);
            await Task.WhenAll(host, guardian).WaitAsync(TestTimeout);
            return await host;
        }

        internal GuardianHostHello CreateHello(string? mismatch = null) => new(
            mismatch == "guardian"
                ? new GuardianBootId(Guid.Parse("99999999-9999-4999-8999-999999999999"))
                : Guardian,
            mismatch == "host_boot" ? OtherHost : Host,
            mismatch == "generation" ? new HostGeneration(2) : Generation,
            mismatch == "pid" ? 43 : 42,
            mismatch == "executable" ? Digest('8') : ExecutableDigest,
            mismatch == "build" ? Digest('8') : BuildDigest,
            mismatch == "public_contract" ? Digest('8') : PublicContractDigest,
            mismatch == "configuration" ? Digest('8') : ConfigurationDigest);

        private async Task<HandshakeTranscript> CompleteHandshakeAsync()
        {
            await HostWriter.WriteAsync(CreateHello());
            var requestIds = new List<long>();
            var initialize = Assert.IsType<GuardianHostInitialize>(
                await GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
            requestIds.Add(initialize.RequestId.Value);
            Assert.Equal(Guardian, initialize.GuardianBootId);
            Assert.Equal(Host, initialize.HostBootId);
            Assert.Equal(Generation, initialize.HostGeneration);
            Assert.Equal(ExecutableDigest, initialize.HostExecutableDigest);
            Assert.Equal(BuildDigest, initialize.HostBuildDigest);
            Assert.Equal(PublicContractDigest, initialize.PublicContractDigest);
            Assert.Equal(ConfigurationDigest, initialize.ConfigurationDigest);
            Assert.Equal(PackageManifestDigest, initialize.PackageManifestDigest);

            var header = Assert.IsType<ManifestHeaderRequest>(
                await GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
            requestIds.Add(header.RequestId.Value);
            Assert.Equal(GuardianHostClientTests.Manifest, header.ManifestId);
            await HostWriter.WriteAsync(Success(
                header.RequestId,
                new ManifestHeaderAccepted(header.ManifestId)));

            var transferred = new MemoryStream(header.TotalBytes);
            for (var index = 0; index < header.ChunkCount; index++)
            {
                using var chunk = Assert.IsType<ManifestChunkRequest>(
                    await GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
                requestIds.Add(chunk.RequestId.Value);
                Assert.Equal(header.ManifestId, chunk.ManifestId);
                Assert.Equal(index, chunk.ChunkIndex);
                var bytes = chunk.GetRawBytes();
                await transferred.WriteAsync(bytes);
                await HostWriter.WriteAsync(Success(
                    chunk.RequestId,
                    new ManifestChunkAccepted(
                        chunk.ManifestId,
                        chunk.ChunkIndex,
                        checked((int)transferred.Length))));
            }

            var seal = Assert.IsType<ManifestSealRequest>(
                await GuardianReader.ReadAsync().AsTask().WaitAsync(TestTimeout));
            requestIds.Add(seal.RequestId.Value);
            var transferredBytes = transferred.ToArray();
            var digest = Sha256Digest.Compute(transferredBytes);
            Assert.Equal(header.TotalBytes, transferredBytes.Length);
            Assert.Equal(header.ManifestDigest, digest);
            Assert.Equal(header.ManifestId, seal.ManifestId);
            Assert.Equal(header.TotalBytes, seal.TotalBytes);
            Assert.Equal(digest, seal.ManifestDigest);
            await HostWriter.WriteAsync(Success(
                seal.RequestId,
                new ManifestSealed(seal.ManifestId, digest, transferredBytes.Length)));
            await HostWriter.WriteAsync(new GuardianHostReady(
                Guardian,
                Host,
                Generation,
                initialize.RequestId,
                seal.ManifestId,
                digest,
                42));
            return new HandshakeTranscript(requestIds, transferredBytes, digest);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            RequestTransport.Dispose();
            EventTransport.Dispose();
        }
    }

    private sealed class TestTransportStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly object _sync = new();
        private byte[]? _current;
        private int _currentOffset;
        private TaskCompletionSource? _blockedWriteStarted;
        private TaskCompletionSource? _releaseBlockedWrite;
        private bool _blockedWriteIgnoresCancellation;
        private int _disposed;
        private int _rejectWrites;
        private int _writeCount;
        private int _completedWriteCount;
        private int _readCount;

        internal int WriteCount => Volatile.Read(ref _writeCount);
        internal int CompletedWriteCount => Volatile.Read(ref _completedWriteCount);
        internal int ReadCount => Volatile.Read(ref _readCount);
        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => Volatile.Read(ref _disposed) == 0;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal Task BlockNextWrite(bool ignoreCancellation = false)
        {
            lock (_sync)
            {
                if (_blockedWriteStarted is not null)
                    throw new InvalidOperationException("A write is already blocked.");
                _blockedWriteStarted = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _releaseBlockedWrite = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _blockedWriteIgnoresCancellation = ignoreCancellation;
                return _blockedWriteStarted.Task;
            }
        }

        internal void ReleaseBlockedWrite()
        {
            TaskCompletionSource release;
            lock (_sync)
            {
                release = _releaseBlockedWrite ??
                    throw new InvalidOperationException("No write is blocked.");
                _releaseBlockedWrite = null;
            }
            release.TrySetResult();
        }

        internal void CompleteWriting() => _chunks.Writer.TryComplete();
        internal void RejectWrites() => Volatile.Write(ref _rejectWrites, 1);

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
                    _current = await _chunks.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
            _current.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            Interlocked.Increment(ref _readCount);
            return count;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Volatile.Read(ref _rejectWrites) != 0)
                throw new IOException("Injected private transport failure.");
            Interlocked.Increment(ref _writeCount);
            Task? blocked = null;
            var blockedWriteIgnoresCancellation = false;
            lock (_sync)
            {
                if (_blockedWriteStarted is not null && _releaseBlockedWrite is not null)
                {
                    _blockedWriteStarted.TrySetResult();
                    blocked = _releaseBlockedWrite.Task;
                    blockedWriteIgnoresCancellation = _blockedWriteIgnoresCancellation;
                    _blockedWriteIgnoresCancellation = false;
                    _blockedWriteStarted = null;
                }
            }
            if (blocked is not null)
            {
                if (blockedWriteIgnoresCancellation)
                    await blocked.ConfigureAwait(false);
                else
                    await blocked.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            await _chunks.Writer.WriteAsync(
                buffer.ToArray(),
                blockedWriteIgnoresCancellation ? CancellationToken.None : cancellationToken)
                .ConfigureAwait(false);
            Interlocked.Increment(ref _completedWriteCount);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _chunks.Writer.TryComplete();
            lock (_sync) _releaseBlockedWrite?.TrySetCanceled();
            base.Dispose(disposing);
        }
    }

    private static IDisposable? AcquireEventLease(GuardianHostEvent hostEvent)
    {
        if (hostEvent.SessionAlias != Alias ||
            hostEvent.SessionTransitionVersion != Transition ||
            hostEvent is WorkerCreateCapabilityRequestedEvent created &&
                created.BindingDigest != BindingDigest ||
            hostEvent is GuardianHostContainmentEvent containment &&
                !Equals(containment.WorkerIdentity, Worker))
            return null;
        return new CallbackLease(static () => { });
    }

    private sealed class CallbackLease(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;
        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    private sealed class BlockingPrivateRequestIdAllocator(int blockAt)
        : IPrivateRequestIdAllocator
    {
        private readonly MonotonicPrivateRequestIdAllocator _inner = new();
        private readonly TaskCompletionSource _blocked = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Blocked => _blocked.Task;

        public PrivateRequestId Allocate()
        {
            var requestId = _inner.Allocate();
            if (requestId.Value == blockAt)
            {
                _blocked.TrySetResult();
                _release.Task.GetAwaiter().GetResult();
            }
            return requestId;
        }

        internal void Release() => _release.TrySetResult();
    }
}
