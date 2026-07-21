using System.Collections;
using System.Reflection;
using System.Text;
using PtkMcpGuardian.Output;
using PtkMcpServer;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class GuardianOutputCapabilityRegistryTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly HostBootId OtherHost = new(
        Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));
    private static readonly HostGeneration Generation = new(7);
    private static readonly WorkerBootId WorkerBoot = new(
        Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"));
    private static readonly GuardianHostWorkerIdentity Worker = new(
        WorkerBoot,
        new WorkerGeneration(11));
    private static readonly GuardianHostOperationIdentity OperationIdentity = new(
        new PlanId(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")),
        new OperationId(Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff")));
    private static readonly CallId Call = new(
        Guid.Parse("77777777-7777-7777-8777-777777777777"));
    private static readonly CanonicalAlias Alias = new("default");
    private static readonly SessionTransitionVersion Transition = new(3);

    [Fact]
    public void Contiguous_chunks_publish_exact_utf8_with_no_invented_provenance()
    {
        using var harness = new Harness();
        var request = harness.ForegroundRequest(Token(1));
        Assert.True(harness.Registry.TryRegister(
            request,
            out var registration,
            out var failure));
        Assert.Null(failure);
        Assert.True(registration!.StorageAvailable);
        var bytes = Encoding.UTF8.GetBytes("AαB\nverbatim\n");

        using (var first = Chunk(request, 1, 0, 0, bytes.AsMemory(0, 2)))
        {
            Assert.True(harness.Registry.MatchesActiveEvent(first));
            Assert.Null(harness.Registry.AcceptEvent(first));
        }
        using (var second = Chunk(request, 2, 1, 2, bytes.AsMemory(2)))
        {
            Assert.Null(harness.Registry.AcceptEvent(second));
        }

        var seal = Seal(request, 3, bytes, GuardianHostOutputSealState.Complete);
        var terminal = Assert.IsType<GuardianOutputTerminal>(
            harness.Registry.AcceptEvent(seal));

        Assert.Equal(request.RequestId, terminal.RequestId);
        Assert.Equal(GuardianHostOperationKind.InvokeForeground, terminal.OperationKind);
        Assert.Null(terminal.PublicJobId);
        Assert.True(terminal.Recovery.Advertise);
        Assert.Equal(OutputArtifactState.Available, terminal.Recovery.State);
        Assert.Equal(bytes.Length, terminal.Recovery.Bytes);
        Assert.NotNull(terminal.Recovery.Handle);
        Assert.False(harness.Registry.MatchesActiveEvent(seal));
        Assert.Equal(0, harness.Registry.ActiveCount);
        var status = harness.Store.Status(terminal.Recovery.Handle!);
        Assert.Null(status.Provenance);
        var read = harness.Store.Read(
            terminal.Recovery.Handle!,
            0,
            OutputStore.MaximumReadBytes);
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(read.Text));
    }

    [Fact]
    public void Invalid_chunk_order_revokes_the_capability_and_releases_reservation()
    {
        using var harness = new Harness(aggregateBytes: 1024);
        var request = harness.ForegroundRequest(Token(2));
        Assert.True(harness.Registry.TryRegister(request, out _, out _));
        using var chunk = Chunk(
            request,
            sequence: 1,
            chunkIndex: 1,
            offset: 0,
            Encoding.UTF8.GetBytes("bad"));

        var error = Assert.Throws<GuardianOutputCapabilityException>(() =>
            harness.Registry.AcceptEvent(chunk));

        Assert.Equal(
            GuardianOutputCapabilityFailure.ChunkIndexInvalid,
            error.Failure);
        Assert.Equal(0, harness.Registry.ActiveCount);
        Assert.True(harness.Store.TryReserve(
            Alias.Value,
            out var replacement,
            out var failure));
        Assert.Null(failure);
        replacement!.Dispose();
    }

    [Fact]
    public void Retained_match_requires_the_exact_original_request_correlation()
    {
        using var harness = new Harness();
        var request = harness.BackgroundRequest(Token(3), requestId: 4);
        Assert.True(harness.Registry.TryRegister(request, out _, out _));
        using var foreign = Chunk(
            request,
            sequence: 1,
            chunkIndex: 0,
            offset: 0,
            Encoding.UTF8.GetBytes("x"),
            requestId: new PrivateRequestId(5));
        using var exact = Chunk(
            request,
            sequence: 2,
            chunkIndex: 0,
            offset: 0,
            Encoding.UTF8.GetBytes("x"));

        Assert.False(harness.Registry.MatchesActiveEvent(foreign));
        Assert.True(harness.Registry.MatchesActiveEvent(exact));
        Assert.Equal(1, harness.Registry.ActiveCount);
    }

    [Fact]
    public void Invalid_utf8_across_chunk_boundary_revokes_the_capability()
    {
        using var harness = new Harness();
        var request = harness.ForegroundRequest(Token(4));
        Assert.True(harness.Registry.TryRegister(request, out _, out _));
        using (var first = Chunk(
                   request,
                   sequence: 1,
                   chunkIndex: 0,
                   offset: 0,
                   new byte[] { 0xc3 }))
        {
            Assert.Null(harness.Registry.AcceptEvent(first));
        }
        using var second = Chunk(
            request,
            sequence: 2,
            chunkIndex: 1,
            offset: 1,
            new byte[] { 0x28 });

        var error = Assert.Throws<GuardianOutputCapabilityException>(() =>
            harness.Registry.AcceptEvent(second));

        Assert.Equal(GuardianOutputCapabilityFailure.InvalidUtf8, error.Failure);
        Assert.Equal(0, harness.Registry.ActiveCount);
    }

    [Fact]
    public void Seal_digest_mismatch_revokes_the_capability_without_a_handle()
    {
        using var harness = new Harness();
        var request = harness.ForegroundRequest(Token(5));
        Assert.True(harness.Registry.TryRegister(request, out _, out _));
        var bytes = Encoding.UTF8.GetBytes("exact");
        using (var chunk = Chunk(request, 1, 0, 0, bytes))
            Assert.Null(harness.Registry.AcceptEvent(chunk));
        var seal = new OutputSealEvent(
            request.GuardianBootId,
            request.HostBootId,
            request.HostGeneration,
            new HostEventSequence(2),
            request.RequestId,
            request.SessionAlias!,
            request.SessionTransitionVersion!,
            request.Worker!,
            request.OperationIdentity,
            request.Operation.OutputCapability!.Token,
            GuardianHostOutputSealState.Complete,
            bytes.Length,
            Sha256Digest.Compute(Encoding.UTF8.GetBytes("other")));

        var error = Assert.Throws<GuardianOutputCapabilityException>(() =>
            harness.Registry.AcceptEvent(seal));

        Assert.Equal(
            GuardianOutputCapabilityFailure.SealDigestMismatch,
            error.Failure);
        Assert.Equal(0, harness.Registry.ActiveCount);
    }

    [Fact]
    public void Capability_bound_is_enforced_before_any_chunk_bytes_are_retained()
    {
        using var harness = new Harness();
        var request = harness.ForegroundRequest(Token(10));
        Assert.True(harness.Registry.TryRegister(request, out _, out _));
        using var chunk = Chunk(
            request,
            sequence: 1,
            chunkIndex: 0,
            offset: 0,
            new byte[1025]);

        var error = Assert.Throws<GuardianOutputCapabilityException>(() =>
            harness.Registry.AcceptEvent(chunk));

        Assert.Equal(
            GuardianOutputCapabilityFailure.MaximumBytesExceeded,
            error.Failure);
        Assert.Equal(0, harness.Registry.ActiveCount);
    }

    [Fact]
    public void Seal_length_must_equal_the_contiguous_received_length()
    {
        using var harness = new Harness();
        var request = harness.ForegroundRequest(Token(11));
        Assert.True(harness.Registry.TryRegister(request, out _, out _));
        var bytes = Encoding.UTF8.GetBytes("length");
        using (var chunk = Chunk(request, 1, 0, 0, bytes))
            Assert.Null(harness.Registry.AcceptEvent(chunk));
        var seal = new OutputSealEvent(
            request.GuardianBootId,
            request.HostBootId,
            request.HostGeneration,
            new HostEventSequence(2),
            request.RequestId,
            request.SessionAlias!,
            request.SessionTransitionVersion!,
            request.Worker!,
            request.OperationIdentity,
            request.Operation.OutputCapability!.Token,
            GuardianHostOutputSealState.Complete,
            bytes.Length - 1,
            Sha256Digest.Compute(bytes));

        var error = Assert.Throws<GuardianOutputCapabilityException>(() =>
            harness.Registry.AcceptEvent(seal));

        Assert.Equal(
            GuardianOutputCapabilityFailure.SealLengthMismatch,
            error.Failure);
        Assert.Equal(0, harness.Registry.ActiveCount);
    }

    [Fact]
    public void Private_incomplete_seal_uses_only_the_protocol_state_as_detail()
    {
        using var harness = new Harness();
        var request = harness.ForegroundRequest(Token(12));
        Assert.True(harness.Registry.TryRegister(request, out _, out _));
        var bytes = Encoding.UTF8.GetBytes("bounded prefix");
        using (var chunk = Chunk(request, 1, 0, 0, bytes))
            Assert.Null(harness.Registry.AcceptEvent(chunk));

        var terminal = Assert.IsType<GuardianOutputTerminal>(
            harness.Registry.AcceptEvent(Seal(
                request,
                2,
                bytes,
                GuardianHostOutputSealState.Incomplete)));

        Assert.Equal(OutputArtifactState.Incomplete, terminal.Recovery.State);
        Assert.Equal(
            "private_host_reported_incomplete",
            terminal.Recovery.DetailCode);
        Assert.NotNull(terminal.Recovery.Handle);
        Assert.Null(harness.Store.Status(terminal.Recovery.Handle!).Provenance);
    }

    [Fact]
    public void Terminal_publication_zeroes_the_sensitive_accumulation_buffer()
    {
        using var harness = new Harness();
        var request = harness.ForegroundRequest(Token(13));
        Assert.True(harness.Registry.TryRegister(request, out _, out _));
        var bytes = Encoding.UTF8.GetBytes("sensitive value");
        using (var chunk = Chunk(request, 1, 0, 0, bytes))
            Assert.Null(harness.Registry.AcceptEvent(chunk));
        var retained = RetainedBuffer(harness.Registry);
        Assert.Contains(retained, value => value != 0);

        _ = harness.Registry.AcceptEvent(Seal(
            request,
            2,
            bytes,
            GuardianHostOutputSealState.Complete));

        Assert.All(retained, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void Store_capacity_failure_discards_but_validates_the_complete_stream()
    {
        using var harness = new Harness(aggregateBytes: 1024);
        Assert.True(harness.Store.TryReserve(
            Alias.Value,
            out var blocker,
            out _));
        using var ownedBlocker = blocker!;
        var request = harness.ForegroundRequest(Token(6));
        Assert.True(harness.Registry.TryRegister(
            request,
            out var registration,
            out var registrationFailure));
        Assert.Null(registrationFailure);
        Assert.False(registration!.StorageAvailable);
        Assert.Equal("output_store_capacity", registration.StorageFailure);
        var bytes = Encoding.UTF8.GetBytes("still validated");
        using (var chunk = Chunk(request, 1, 0, 0, bytes))
            Assert.Null(harness.Registry.AcceptEvent(chunk));

        var terminal = Assert.IsType<GuardianOutputTerminal>(
            harness.Registry.AcceptEvent(Seal(
                request,
                2,
                bytes,
                GuardianHostOutputSealState.Complete)));

        Assert.Null(terminal.Recovery.Handle);
        Assert.Equal(OutputArtifactState.NotFound, terminal.Recovery.State);
        Assert.Equal("output_store_capacity", terminal.Recovery.DetailCode);
        Assert.True(terminal.Recovery.Advertise);
    }

    [Fact]
    public void Host_loss_publishes_only_the_exact_nonempty_valid_prefix_as_incomplete()
    {
        using var harness = new Harness();
        var request = harness.BackgroundRequest(Token(7), requestId: 7);
        var otherRequest = harness.BackgroundRequest(
            Token(8),
            requestId: 8,
            hostBootId: OtherHost);
        Assert.True(harness.Registry.TryRegister(request, out _, out _));
        Assert.True(harness.Registry.TryRegister(
            otherRequest,
            out var otherRegistration,
            out _));
        var prefix = Encoding.UTF8.GetBytes("finished prefix\n");
        using (var chunk = Chunk(request, 1, 0, 0, prefix))
            Assert.Null(harness.Registry.AcceptEvent(chunk));

        var terminal = Assert.Single(harness.Registry.AbandonGeneration(
            Guardian,
            Host,
            Generation,
            "host_generation_lost"));

        Assert.Equal(new PublicJobId(73), terminal.PublicJobId);
        Assert.Equal(OutputArtifactState.Incomplete, terminal.Recovery.State);
        Assert.Equal("host_generation_lost", terminal.Recovery.DetailCode);
        Assert.NotNull(terminal.Recovery.Handle);
        var read = harness.Store.Read(
            terminal.Recovery.Handle!,
            0,
            OutputStore.MaximumReadBytes);
        Assert.Equal(prefix, Encoding.UTF8.GetBytes(read.Text));
        Assert.Equal(1, harness.Registry.ActiveCount);
        Assert.True(harness.Registry.TryCancel(otherRegistration!));
    }

    [Fact]
    public void Host_loss_never_alters_and_publishes_a_split_utf8_prefix()
    {
        using var harness = new Harness();
        var request = harness.BackgroundRequest(Token(14), requestId: 14);
        Assert.True(harness.Registry.TryRegister(request, out _, out _));
        using (var chunk = Chunk(
                   request,
                   sequence: 1,
                   chunkIndex: 0,
                   offset: 0,
                   new byte[] { 0xc3 }))
        {
            Assert.Null(harness.Registry.AcceptEvent(chunk));
        }

        var terminal = Assert.Single(harness.Registry.AbandonGeneration(
            Guardian,
            Host,
            Generation,
            "host_generation_lost"));

        Assert.Null(terminal.Recovery.Handle);
        Assert.Equal(OutputArtifactState.NotFound, terminal.Recovery.State);
        Assert.Equal("output_prefix_invalid_utf8", terminal.Recovery.DetailCode);
    }

    [Fact]
    public void Expired_empty_capability_is_unavailable_and_never_mints_a_handle()
    {
        using var harness = new Harness();
        var request = harness.ForegroundRequest(Token(9), expiresAfterMilliseconds: 5);
        Assert.True(harness.Registry.TryRegister(request, out _, out _));
        harness.Clock.Advance(TimeSpan.FromMilliseconds(5));
        var probe = Seal(
            request,
            sequence: 1,
            ReadOnlySpan<byte>.Empty,
            GuardianHostOutputSealState.Complete);

        Assert.False(harness.Registry.MatchesActiveEvent(probe));
        var terminal = Assert.Single(harness.Registry.SweepExpired());
        Assert.Null(terminal.Recovery.Handle);
        Assert.Equal("output_capability_expired", terminal.Recovery.DetailCode);
        Assert.Equal(0, harness.Registry.ActiveCount);
    }

    [Fact]
    public void Retained_capability_state_does_not_keep_the_script_bearing_request()
    {
        var entry = typeof(GuardianOutputCapabilityRegistry).GetNestedType(
            "Entry",
            BindingFlags.NonPublic);

        Assert.NotNull(entry);
        Assert.DoesNotContain(
            entry!.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(OperationRequest) ||
                typeof(GuardianHostOperation).IsAssignableFrom(field.FieldType));
    }

    private static OutputChunkEvent Chunk(
        OperationRequest request,
        long sequence,
        long chunkIndex,
        int offset,
        ReadOnlyMemory<byte> bytes,
        PrivateRequestId? requestId = null) => new(
            request.GuardianBootId,
            request.HostBootId,
            request.HostGeneration,
            new HostEventSequence(sequence),
            requestId ?? request.RequestId,
            request.SessionAlias!,
            request.SessionTransitionVersion!,
            request.Worker!,
            request.OperationIdentity,
            request.Operation.OutputCapability!.Token,
            chunkIndex,
            offset,
            bytes);

    private static OutputSealEvent Seal(
        OperationRequest request,
        long sequence,
        ReadOnlySpan<byte> bytes,
        GuardianHostOutputSealState state) => new(
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
            state,
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

    private static byte[] RetainedBuffer(
        GuardianOutputCapabilityRegistry registry)
    {
        var entriesField = typeof(GuardianOutputCapabilityRegistry).GetField(
            "_entries",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var entries = Assert.IsAssignableFrom<IDictionary>(
            entriesField!.GetValue(registry));
        var entry = Assert.Single(entries.Values.Cast<object>());
        var bufferField = entry.GetType().GetField(
            "_buffer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<byte[]>(bufferField!.GetValue(entry));
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"ptk-guardian-output-{Guid.NewGuid():N}");
        internal Harness(long aggregateBytes = 4096)
        {
            Clock = new MutableTimeProvider(
                DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
            Store = new OutputStore(new OutputStoreOptions(
                _root,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromHours(1),
                MaximumArtifactBytes: 1024,
                MaximumSessionBytes: aggregateBytes,
                MaximumAggregateBytes: aggregateBytes,
                UtcNow: Clock.GetUtcNow));
            Registry = new GuardianOutputCapabilityRegistry(Store, Clock);
        }

        internal MutableTimeProvider Clock { get; }
        internal OutputStore Store { get; }
        internal GuardianOutputCapabilityRegistry Registry { get; }

        internal OperationRequest ForegroundRequest(
            CapabilityToken outputToken,
            long requestId = 1,
            HostBootId? hostBootId = null,
            long expiresAfterMilliseconds = 10_000) =>
            Request(
                outputToken,
                requestId,
                hostBootId ?? Host,
                expiresAfterMilliseconds,
                background: false);

        internal OperationRequest BackgroundRequest(
            CapabilityToken outputToken,
            long requestId,
            HostBootId? hostBootId = null,
            long expiresAfterMilliseconds = 10_000) =>
            Request(
                outputToken,
                requestId,
                hostBootId ?? Host,
                expiresAfterMilliseconds,
                background: true);

        private OperationRequest Request(
            CapabilityToken outputToken,
            long requestId,
            HostBootId hostBootId,
            long expiresAfterMilliseconds,
            bool background)
        {
            var expires = Clock.GetUtcNow().ToUnixTimeMilliseconds() +
                expiresAfterMilliseconds;
            var dispatch = new DispatchCapability(Token(200), Call, expires);
            var output = new OutputCapability(outputToken, 1024, expires);
            GuardianHostOperation operation = background
                ? new InvokeBackgroundOperation(
                    Call,
                    dispatch,
                    output,
                    "Write-Output test",
                    raw: false,
                    GuardianHostInvokeRoute.Auto,
                    new PublicJobId(73))
                : new InvokeForegroundOperation(
                    Call,
                    dispatch,
                    output,
                    "Write-Output test",
                    raw: false,
                    GuardianHostInvokeRoute.Auto);
            return new OperationRequest(
                Guardian,
                hostBootId,
                Generation,
                new PrivateRequestId(requestId),
                expires,
                Alias,
                Transition,
                Worker,
                OperationIdentity,
                operation);
        }

        public void Dispose()
        {
            Registry.Dispose();
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
