using System.Reflection;
using System.Text;
using PtkMcpGuardian.Output;
using PtkMcpServer;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class GuardianOutputCoordinatorTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly HostGeneration Generation = new(9);
    private static readonly GuardianHostWorkerIdentity Worker = new(
        new WorkerBootId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")),
        new WorkerGeneration(4));
    private static readonly GuardianHostOperationIdentity OperationIdentity = new(
        new PlanId(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")),
        new OperationId(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")));
    private static readonly CallId Call = new(
        Guid.Parse("77777777-7777-7777-8777-777777777777"));
    private static readonly CanonicalAlias Alias = new("default");
    private static readonly SessionTransitionVersion Transition = new(5);

    [Fact]
    public void Foreground_terminal_waits_for_response_then_resolves_once()
    {
        using var harness = new Harness();
        var request = harness.ForegroundRequest(Token(1), requestId: 1);
        Assert.True(harness.Coordinator.TryRegister(request, out _, out _));
        var bytes = Encoding.UTF8.GetBytes("foreground exact");

        harness.Complete(request, bytes);

        Assert.Equal(1, harness.Coordinator.TrackedCount);
        Assert.Equal(0, harness.Coordinator.ActiveCapabilityCount);
        var recovery = harness.Coordinator.ResolveResponse(
            request.RequestId,
            succeeded: true);
        Assert.NotNull(recovery);
        Assert.NotNull(recovery!.Handle);
        Assert.Equal(OutputArtifactState.Available, recovery.State);
        Assert.Equal(0, harness.Coordinator.TrackedCount);
        Assert.Null(harness.Coordinator.ResolveResponse(
            request.RequestId,
            succeeded: true));
    }

    [Fact]
    public void Successful_background_response_retains_capability_until_late_seal()
    {
        using var harness = new Harness();
        var request = harness.BackgroundRequest(Token(2), requestId: 2, jobId: 21);
        Assert.True(harness.Coordinator.TryRegister(request, out _, out _));

        Assert.Null(harness.Coordinator.ResolveResponse(
            request.RequestId,
            succeeded: true));
        Assert.Equal(1, harness.Coordinator.TrackedCount);
        Assert.Equal(1, harness.Coordinator.ActiveCapabilityCount);
        var bytes = Encoding.UTF8.GetBytes("late background");
        harness.Complete(request, bytes);

        Assert.Equal(0, harness.Coordinator.TrackedCount);
        Assert.Equal(0, harness.Coordinator.ActiveCapabilityCount);
        Assert.True(harness.Coordinator.TryGetJobRecovery(
            new PublicJobId(21),
            out var recovery));
        Assert.NotNull(recovery!.Handle);
        Assert.Equal(bytes.Length, recovery.Bytes);
    }

    [Fact]
    public void Background_seal_before_response_is_not_published_until_start_succeeds()
    {
        using var harness = new Harness();
        var request = harness.BackgroundRequest(Token(3), requestId: 3, jobId: 22);
        Assert.True(harness.Coordinator.TryRegister(request, out _, out _));
        harness.Complete(request, Encoding.UTF8.GetBytes("already complete"));

        Assert.False(harness.Coordinator.TryGetJobRecovery(
            new PublicJobId(22),
            out _));
        Assert.Equal(1, harness.Coordinator.TrackedCount);

        Assert.Null(harness.Coordinator.ResolveResponse(
            request.RequestId,
            succeeded: true));

        Assert.True(harness.Coordinator.TryGetJobRecovery(
            new PublicJobId(22),
            out var recovery));
        Assert.NotNull(recovery!.Handle);
        Assert.Equal(0, harness.Coordinator.TrackedCount);
    }

    [Fact]
    public void Failed_background_response_revokes_unused_capability_and_job_mapping()
    {
        using var harness = new Harness();
        var request = harness.BackgroundRequest(Token(4), requestId: 4, jobId: 23);
        Assert.True(harness.Coordinator.TryRegister(request, out _, out _));

        Assert.Null(harness.Coordinator.ResolveResponse(
            request.RequestId,
            succeeded: false));

        Assert.Equal(0, harness.Coordinator.TrackedCount);
        Assert.Equal(0, harness.Coordinator.ActiveCapabilityCount);
        Assert.False(harness.Coordinator.TryGetJobRecovery(
            new PublicJobId(23),
            out _));
        using var late = Chunk(
            request,
            sequence: 1,
            Encoding.UTF8.GetBytes("late"));
        Assert.False(harness.Coordinator.MatchesActiveEvent(late));
    }

    [Fact]
    public void Successful_foreground_without_seal_is_truthfully_unavailable()
    {
        using var harness = new Harness();
        var request = harness.ForegroundRequest(Token(5), requestId: 5);
        Assert.True(harness.Coordinator.TryRegister(request, out _, out _));

        var recovery = harness.Coordinator.ResolveResponse(
            request.RequestId,
            succeeded: true);

        Assert.Null(recovery!.Handle);
        Assert.Equal("output_not_transferred", recovery.DetailCode);
        Assert.True(recovery.Advertise);
        Assert.Equal(0, harness.Coordinator.ActiveCapabilityCount);
        Assert.Equal(0, harness.Coordinator.TrackedCount);
    }

    [Fact]
    public void Host_loss_after_background_response_maps_exact_prefix_to_job()
    {
        using var harness = new Harness();
        var request = harness.BackgroundRequest(Token(6), requestId: 6, jobId: 24);
        Assert.True(harness.Coordinator.TryRegister(request, out _, out _));
        Assert.Null(harness.Coordinator.ResolveResponse(
            request.RequestId,
            succeeded: true));
        var prefix = Encoding.UTF8.GetBytes("surviving prefix\n");
        using (var chunk = Chunk(request, sequence: 1, prefix))
            harness.Coordinator.HandleEvent(chunk);

        var terminal = Assert.Single(harness.Coordinator.AbandonGeneration(
            Guardian,
            Host,
            Generation,
            "host_generation_lost"));

        Assert.Equal(OutputArtifactState.Incomplete, terminal.Recovery.State);
        Assert.True(harness.Coordinator.TryGetJobRecovery(
            new PublicJobId(24),
            out var recovery));
        var read = harness.Store.Read(
            recovery!.Handle!,
            0,
            OutputStore.MaximumReadBytes);
        Assert.Equal(prefix, Encoding.UTF8.GetBytes(read.Text));
    }

    [Fact]
    public void Host_loss_before_background_response_never_publishes_job_recovery()
    {
        using var harness = new Harness();
        var request = harness.BackgroundRequest(Token(7), requestId: 7, jobId: 25);
        Assert.True(harness.Coordinator.TryRegister(request, out _, out _));
        using (var chunk = Chunk(
                   request,
                   sequence: 1,
                   Encoding.UTF8.GetBytes("orphan prefix")))
        {
            harness.Coordinator.HandleEvent(chunk);
        }

        var terminal = Assert.Single(harness.Coordinator.AbandonGeneration(
            Guardian,
            Host,
            Generation,
            "host_generation_lost"));
        Assert.NotNull(terminal.Recovery.Handle);
        Assert.False(harness.Coordinator.TryGetJobRecovery(
            new PublicJobId(25),
            out _));

        harness.Coordinator.AbandonCall(request.RequestId);
        Assert.Equal(0, harness.Coordinator.TrackedCount);
        Assert.False(harness.Coordinator.TryGetJobRecovery(
            new PublicJobId(25),
            out _));
    }

    [Fact]
    public void Expiry_after_background_response_publishes_unavailable_job_state()
    {
        using var harness = new Harness();
        var request = harness.BackgroundRequest(
            Token(8),
            requestId: 8,
            jobId: 26,
            expiresAfterMilliseconds: 5);
        Assert.True(harness.Coordinator.TryRegister(request, out _, out _));
        Assert.Null(harness.Coordinator.ResolveResponse(
            request.RequestId,
            succeeded: true));
        harness.Clock.Advance(TimeSpan.FromMilliseconds(5));

        _ = Assert.Single(harness.Coordinator.SweepExpired());

        Assert.True(harness.Coordinator.TryGetJobRecovery(
            new PublicJobId(26),
            out var recovery));
        Assert.Null(recovery!.Handle);
        Assert.Equal("output_capability_expired", recovery.DetailCode);
    }

    [Fact]
    public void Job_output_terminal_resolves_its_response_without_replacing_job_recovery()
    {
        using var harness = new Harness();
        var request = harness.JobOutputRequest(Token(9), requestId: 9, jobId: 27);
        Assert.True(harness.Coordinator.TryRegister(request, out _, out _));
        harness.Complete(request, Encoding.UTF8.GetBytes("job page"));

        var recovery = harness.Coordinator.ResolveResponse(
            request.RequestId,
            succeeded: true);

        Assert.NotNull(recovery!.Handle);
        Assert.False(harness.Coordinator.TryGetJobRecovery(
            new PublicJobId(27),
            out _));
    }

    [Fact]
    public void Bounded_job_table_refuses_a_second_background_before_effect()
    {
        using var harness = new Harness(maximumJobRecoveries: 1);
        var first = harness.BackgroundRequest(Token(10), requestId: 10, jobId: 28);
        var second = harness.BackgroundRequest(Token(11), requestId: 11, jobId: 29);
        Assert.True(harness.Coordinator.TryRegister(
            first,
            out var firstRegistration,
            out _));

        Assert.False(harness.Coordinator.TryRegister(
            second,
            out var secondRegistration,
            out var failure));

        Assert.Null(secondRegistration);
        Assert.Equal("output_job_capacity", failure);
        Assert.True(harness.Coordinator.TryCancel(firstRegistration!));
    }

    [Fact]
    public void Rejected_event_closes_registry_authority_and_call_cleanup_stays_consistent()
    {
        using var harness = new Harness();
        var request = harness.ForegroundRequest(Token(12), requestId: 12);
        Assert.True(harness.Coordinator.TryRegister(request, out _, out _));
        using var invalid = new OutputChunkEvent(
            request.GuardianBootId,
            request.HostBootId,
            request.HostGeneration,
            new HostEventSequence(1),
            new PrivateRequestId(99),
            request.SessionAlias!,
            request.SessionTransitionVersion!,
            request.Worker!,
            request.OperationIdentity,
            request.Operation.OutputCapability!.Token,
            chunkIndex: 0,
            offset: 0,
            Encoding.UTF8.GetBytes("invalid order"));

        Assert.Throws<GuardianOutputCapabilityException>(() =>
            harness.Coordinator.HandleEvent(invalid));
        Assert.Equal(0, harness.Coordinator.ActiveCapabilityCount);
        Assert.Equal(1, harness.Coordinator.TrackedCount);

        harness.Coordinator.AbandonCall(request.RequestId);
        Assert.Equal(0, harness.Coordinator.TrackedCount);
    }

    [Fact]
    public void Prewrite_cancel_cannot_erase_an_already_received_terminal()
    {
        using var harness = new Harness();
        var request = harness.ForegroundRequest(Token(13), requestId: 13);
        Assert.True(harness.Coordinator.TryRegister(
            request,
            out var registration,
            out _));
        harness.Complete(request, Encoding.UTF8.GetBytes("terminal"));

        Assert.False(harness.Coordinator.TryCancel(registration!));
        Assert.NotNull(harness.Coordinator.ResolveResponse(
            request.RequestId,
            succeeded: true)!.Handle);
    }

    [Fact]
    public void Tracked_coordinator_state_never_retains_script_bearing_requests()
    {
        var tracked = typeof(GuardianOutputCoordinator).GetNestedType(
            "TrackedCapability",
            BindingFlags.NonPublic);

        Assert.NotNull(tracked);
        Assert.DoesNotContain(
            tracked!.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(OperationRequest) ||
                typeof(GuardianHostOperation).IsAssignableFrom(field.FieldType));
    }

    private static OutputChunkEvent Chunk(
        OperationRequest request,
        long sequence,
        ReadOnlyMemory<byte> bytes) => new(
            request.GuardianBootId,
            request.HostBootId,
            request.HostGeneration,
            new HostEventSequence(sequence),
            request.RequestId,
            request.SessionAlias!,
            request.SessionTransitionVersion!,
            request.Worker!,
            request.OperationIdentity,
            request.Operation.OutputCapability!.Token,
            chunkIndex: 0,
            offset: 0,
            bytes);

    private static OutputSealEvent Seal(
        OperationRequest request,
        long sequence,
        ReadOnlySpan<byte> bytes) => new(
            request.GuardianBootId,
            request.HostBootId,
            request.HostGeneration,
            new HostEventSequence(sequence),
            request.RequestId,
            request.SessionAlias!,
            request.SessionTransitionVersion!,
            request.Worker!,
            request.OperationIdentity,
            request.Operation.OutputCapability!.Token,
            GuardianHostOutputSealState.Complete,
            bytes.Length,
            Sha256Digest.Compute(bytes));

    private static CapabilityToken Token(byte marker)
    {
        Span<byte> bytes = stackalloc byte[ContractLimits.CapabilityTokenBytes];
        bytes.Fill(marker);
        return new CapabilityToken(Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'));
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"ptk-output-coordinator-{Guid.NewGuid():N}");

        internal Harness(int maximumJobRecoveries = 64)
        {
            Clock = new MutableTimeProvider(
                DateTimeOffset.FromUnixTimeMilliseconds(2_000_000));
            Store = new OutputStore(new OutputStoreOptions(
                _root,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromHours(1),
                MaximumArtifactBytes: 1024,
                MaximumSessionBytes: 8192,
                MaximumAggregateBytes: 8192,
                UtcNow: Clock.GetUtcNow));
            Coordinator = new GuardianOutputCoordinator(
                new GuardianOutputCapabilityRegistry(Store, Clock),
                maximumJobRecoveries);
        }

        internal MutableTimeProvider Clock { get; }
        internal OutputStore Store { get; }
        internal GuardianOutputCoordinator Coordinator { get; }

        internal OperationRequest ForegroundRequest(
            CapabilityToken outputToken,
            long requestId,
            long expiresAfterMilliseconds = 10_000) =>
            Request(requestId, expiresAfterMilliseconds,
                new InvokeForegroundOperation(
                    Call,
                    Dispatch(expiresAfterMilliseconds),
                    Output(outputToken, expiresAfterMilliseconds),
                    "Write-Output secret",
                    raw: false,
                    GuardianHostInvokeRoute.Auto),
                OperationIdentity);

        internal OperationRequest BackgroundRequest(
            CapabilityToken outputToken,
            long requestId,
            long jobId,
            long expiresAfterMilliseconds = 10_000) =>
            Request(requestId, expiresAfterMilliseconds,
                new InvokeBackgroundOperation(
                    Call,
                    Dispatch(expiresAfterMilliseconds),
                    Output(outputToken, expiresAfterMilliseconds),
                    "Write-Output secret",
                    raw: false,
                    GuardianHostInvokeRoute.Auto,
                    new PublicJobId(jobId)),
                OperationIdentity);

        internal OperationRequest JobOutputRequest(
            CapabilityToken outputToken,
            long requestId,
            long jobId,
            long expiresAfterMilliseconds = 10_000) =>
            Request(requestId, expiresAfterMilliseconds,
                new JobOutputOperation(
                    Call,
                    Dispatch(expiresAfterMilliseconds),
                    Output(outputToken, expiresAfterMilliseconds),
                    new PublicJobId(jobId),
                    Token(240),
                    offset: 0),
                operationIdentity: null);

        internal void Complete(OperationRequest request, byte[] bytes)
        {
            using (var chunk = Chunk(request, sequence: 1, bytes))
                Coordinator.HandleEvent(chunk);
            Coordinator.HandleEvent(Seal(request, sequence: 2, bytes));
        }

        private OperationRequest Request(
            long requestId,
            long expiresAfterMilliseconds,
            GuardianHostOperation operation,
            GuardianHostOperationIdentity? operationIdentity)
        {
            var expires = Clock.GetUtcNow().ToUnixTimeMilliseconds() +
                expiresAfterMilliseconds;
            return new OperationRequest(
                Guardian,
                Host,
                Generation,
                new PrivateRequestId(requestId),
                expires,
                Alias,
                Transition,
                Worker,
                operationIdentity,
                operation);
        }

        private DispatchCapability Dispatch(long expiresAfterMilliseconds) => new(
            Token(200),
            Call,
            Clock.GetUtcNow().ToUnixTimeMilliseconds() + expiresAfterMilliseconds);

        private OutputCapability Output(
            CapabilityToken token,
            long expiresAfterMilliseconds) => new(
            token,
            maximumBytes: 1024,
            Clock.GetUtcNow().ToUnixTimeMilliseconds() + expiresAfterMilliseconds);

        public void Dispose()
        {
            Coordinator.Dispose();
            Store.Dispose();
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
