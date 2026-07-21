using System.Text;
using PtkMcpServer.GuardianHost;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class EventPrivateHostOutputTransferTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb"));
    private static readonly HostGeneration HostGeneration = new(17);
    private static readonly CanonicalAlias Alias = new("default");
    private static readonly SessionTransitionVersion Transition = new(3);
    private static readonly GuardianHostWorkerIdentity Worker = new(
        new WorkerBootId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")),
        new WorkerGeneration(9));
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
    public async Task Foreground_capture_emits_the_canonical_artifact_and_exact_seal()
    {
        using var events = new RecordingEventSink();
        var transfer = Transfer(events);
        var request = ForegroundRequest(10, maximumBytes: 4096);
        using var capture = transfer.CreateExecutionCapture(request);

        var preparation = await capture.PrepareAsync(
            DateTimeOffset.FromUnixTimeMilliseconds(60_000),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        var summary = await capture.SealAsync(
            new OutputArtifactContent(
                "stdout",
                ["stderr"],
                ["error"],
                ["warning"],
                ExitCode: 2,
                OutputProvenance.DirectText),
            TimeSpan.FromSeconds(5));

        var expectedText = string.Join(
            Environment.NewLine,
            "stdout",
            "[exit] 2",
            "[stderr]",
            "stderr",
            "[errors]",
            "error",
            "[warnings]",
            "warning");
        var expected = Encoding.UTF8.GetBytes(expectedText);
        Assert.True(preparation.Available);
        Assert.Equal(4096, capture.MaximumArtifactBytes);
        Assert.Null(summary.Handle);
        Assert.Equal(OutputArtifactState.Available, summary.State);
        Assert.Equal(expected.Length, summary.Bytes);
        Assert.Null(summary.DetailCode);
        Assert.False(summary.Advertise);

        var chunk = Assert.IsType<OutputChunkEvent>(events.Events[0]);
        var seal = Assert.IsType<OutputSealEvent>(events.Events[1]);
        AssertEventCorrelation(chunk, request, sequence: 1);
        AssertEventCorrelation(seal, request, sequence: 2);
        Assert.Equal(0, chunk.ChunkIndex);
        Assert.Equal(0, chunk.Offset);
        Assert.Equal(expected, chunk.GetRawBytes());
        Assert.Equal(Sha256Digest.Compute(expected), chunk.RawDigest);
        Assert.Equal(GuardianHostOutputSealState.Complete, seal.State);
        Assert.Equal(expected.Length, seal.TotalBytes);
        Assert.Equal(Sha256Digest.Compute(expected), seal.OutputDigest);
    }

    [Fact]
    public async Task Payload_is_split_into_contiguous_maximum_bounded_chunks()
    {
        using var events = new RecordingEventSink();
        var transfer = Transfer(events);
        var length = ContractLimits.MaximumOutputChunkBytes * 2 + 17;
        var request = ForegroundRequest(20, maximumBytes: length);
        using var capture = transfer.CreateExecutionCapture(request);
        _ = await PrepareAsync(capture);

        var summary = await capture.SealAsync(
            Content(new string('x', length)),
            TimeSpan.FromSeconds(5));

        var chunks = events.Events.OfType<OutputChunkEvent>().ToArray();
        Assert.Equal(3, chunks.Length);
        Assert.Equal([0L, 1L, 2L], chunks.Select(chunk => chunk.ChunkIndex));
        Assert.Equal(
            [0, ContractLimits.MaximumOutputChunkBytes,
             ContractLimits.MaximumOutputChunkBytes * 2],
            chunks.Select(chunk => chunk.Offset));
        Assert.Equal(
            [ContractLimits.MaximumOutputChunkBytes,
             ContractLimits.MaximumOutputChunkBytes, 17],
            chunks.Select(chunk => chunk.RawByteCount));
        var payload = chunks.SelectMany(chunk => chunk.GetRawBytes()).ToArray();
        Assert.Equal(length, payload.Length);
        Assert.All(payload, value => Assert.Equal((byte)'x', value));

        var seal = Assert.IsType<OutputSealEvent>(events.Events[^1]);
        Assert.Equal(GuardianHostOutputSealState.Complete, seal.State);
        Assert.Equal(length, seal.TotalBytes);
        Assert.Equal(Sha256Digest.Compute(payload), seal.OutputDigest);
        Assert.Equal(length, summary.Bytes);
    }

    [Fact]
    public async Task Capability_cap_truncates_only_at_a_utf8_boundary_and_seals_incomplete()
    {
        using var events = new RecordingEventSink();
        var transfer = Transfer(events);
        var request = ForegroundRequest(30, maximumBytes: 5);
        using var capture = transfer.CreateExecutionCapture(request);
        _ = await PrepareAsync(capture);

        var summary = await capture.SealAsync(
            Content("abc😀"),
            TimeSpan.FromSeconds(5));

        var chunk = Assert.IsType<OutputChunkEvent>(events.Events[0]);
        Assert.Equal(Encoding.UTF8.GetBytes("abc"), chunk.GetRawBytes());
        var seal = Assert.IsType<OutputSealEvent>(events.Events[1]);
        Assert.Equal(GuardianHostOutputSealState.Incomplete, seal.State);
        Assert.Equal(3, seal.TotalBytes);
        Assert.Equal(Sha256Digest.Compute(Encoding.UTF8.GetBytes("abc")), seal.OutputDigest);
        Assert.Equal(OutputArtifactState.Incomplete, summary.State);
        Assert.Equal("artifact_cap_exceeded", summary.DetailCode);
        Assert.Equal(3, summary.Bytes);
    }

    [Fact]
    public async Task Empty_capture_emits_only_the_complete_empty_digest_seal()
    {
        using var events = new RecordingEventSink();
        var transfer = Transfer(events);
        var request = ForegroundRequest(40, maximumBytes: 1024);
        using var capture = transfer.CreateExecutionCapture(request);
        _ = await PrepareAsync(capture);

        var summary = await capture.SealAsync(
            Content(string.Empty),
            TimeSpan.FromSeconds(5));

        var seal = Assert.IsType<OutputSealEvent>(Assert.Single(events.Events));
        Assert.Equal(GuardianHostOutputSealState.Complete, seal.State);
        Assert.Equal(0, seal.TotalBytes);
        Assert.Equal(Sha256Digest.Compute([]), seal.OutputDigest);
        Assert.Equal(0, summary.Bytes);
    }

    [Fact]
    public async Task Background_transfer_detaches_exactly_one_terminal_capture()
    {
        using var events = new RecordingEventSink();
        var transfer = Transfer(events);
        var request = BackgroundRequest(50, maximumBytes: 1024);
        using var owner = transfer.CreateExecutionCapture(request);
        _ = await PrepareAsync(owner);

        Assert.True(owner.TryTransferToBackground(out var background));
        Assert.NotNull(background);
        Assert.False(owner.TryTransferToBackground(out var duplicate));
        Assert.Null(duplicate);
        owner.Dispose();

        using (background)
        {
            var summary = await background.SealIncompleteAsync(
                Content("prefix"),
                "job_terminated",
                TimeSpan.FromSeconds(5));
            Assert.Equal(OutputArtifactState.Incomplete, summary.State);
            Assert.Equal("job_terminated", summary.DetailCode);

            var repeated = await background.SealAsync(
                Content("duplicate"),
                TimeSpan.FromSeconds(5));
            Assert.Equal("capture_already_terminal", repeated.DetailCode);
        }

        Assert.Equal(2, events.Events.Count);
        Assert.Equal(
            GuardianHostOutputSealState.Incomplete,
            Assert.IsType<OutputSealEvent>(events.Events[^1]).State);
    }

    [Fact]
    public async Task Expiry_after_preparation_emits_no_output_event()
    {
        long now = 1_000;
        using var events = new RecordingEventSink();
        var transfer = Transfer(events, () => now);
        var request = ForegroundRequest(
            60,
            maximumBytes: 1024,
            outputExpires: 5_000);
        using var capture = transfer.CreateExecutionCapture(request);
        var preparation = await capture.PrepareAsync(
            DateTimeOffset.FromUnixTimeMilliseconds(60_000),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        Assert.True(preparation.Available);
        now = 5_000;

        var summary = await capture.SealAsync(
            Content("must not transfer"),
            TimeSpan.FromSeconds(5));

        Assert.Equal("output_capability_expired", summary.DetailCode);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task Job_output_text_uses_its_exact_capability_and_truthful_truncated_seal()
    {
        using var events = new RecordingEventSink();
        var transfer = Transfer(events);
        var request = JobOutputRequest(70, maximumBytes: 5);

        await transfer.TransferTextAsync(
            request,
            "abc😀",
            CancellationToken.None);

        var chunk = Assert.IsType<OutputChunkEvent>(events.Events[0]);
        AssertEventCorrelation(chunk, request, sequence: 1);
        Assert.Null(chunk.OperationIdentity);
        Assert.Equal(Encoding.UTF8.GetBytes("abc"), chunk.GetRawBytes());
        var seal = Assert.IsType<OutputSealEvent>(events.Events[1]);
        Assert.Equal(GuardianHostOutputSealState.Incomplete, seal.State);
        Assert.Equal(3, seal.TotalBytes);
    }

    [Fact]
    public async Task Invalid_artifact_text_fails_before_any_event()
    {
        using var events = new RecordingEventSink();
        var transfer = Transfer(events);
        using var capture = transfer.CreateExecutionCapture(
            ForegroundRequest(80, maximumBytes: 1024));
        _ = await PrepareAsync(capture);

        var summary = await capture.SealAsync(
            Content("\ud800"),
            TimeSpan.FromSeconds(5));

        Assert.Equal("output_render_invalid", summary.DetailCode);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task Event_write_failure_is_terminal_and_is_never_replayed()
    {
        var events = new FailingEventSink();
        var transfer = Transfer(events);
        using var capture = transfer.CreateExecutionCapture(
            ForegroundRequest(90, maximumBytes: 1024));
        _ = await PrepareAsync(capture);

        await Assert.ThrowsAsync<IOException>(() => capture.SealAsync(
            Content("effect output"),
            TimeSpan.FromSeconds(5)));
        var repeated = await capture.SealAsync(
            Content("must not retry"),
            TimeSpan.FromSeconds(5));

        Assert.Equal("capture_already_terminal", repeated.DetailCode);
        Assert.Equal(1, events.WriteCalls);
    }

    private static EventPrivateHostOutputTransfer Transfer(
        IPrivateHostEventSink eventSink,
        Func<long>? now = null) =>
        new(Identity, eventSink, now ?? (() => 1_000));

    private static Task<OutputCapturePreparation> PrepareAsync(
        IExecutionOutputCapture capture) =>
        capture.PrepareAsync(
            DateTimeOffset.FromUnixTimeMilliseconds(60_000),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

    private static OutputArtifactContent Content(string text) =>
        new(
            text,
            [],
            [],
            [],
            ExitCode: null,
            OutputProvenance.DirectText);

    private static OperationRequest ForegroundRequest(
        long requestId,
        int maximumBytes,
        long outputExpires = 50_000) =>
        Request(
            requestId,
            new InvokeForegroundOperation(
                Call,
                Dispatch(),
                Output(maximumBytes, outputExpires),
                "1",
                raw: false,
                GuardianHostInvokeRoute.Auto));

    private static OperationRequest BackgroundRequest(
        long requestId,
        int maximumBytes) =>
        Request(
            requestId,
            new InvokeBackgroundOperation(
                Call,
                Dispatch(),
                Output(maximumBytes),
                "1",
                raw: false,
                GuardianHostInvokeRoute.Auto,
                new PublicJobId(71)));

    private static OperationRequest JobOutputRequest(
        long requestId,
        int maximumBytes) =>
        Request(
            requestId,
            new JobOutputOperation(
                Call,
                Dispatch(),
                Output(maximumBytes),
                new PublicJobId(71),
                Token(0x71),
                offset: 0));

    private static OperationRequest Request(
        long requestId,
        GuardianHostOperation operation) =>
        new(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(requestId),
            deadlineUnixTimeMilliseconds: 60_000,
            Alias,
            Transition,
            Worker,
            operation.Kind is GuardianHostOperationKind.InvokeForeground or
                GuardianHostOperationKind.InvokeBackground
                ? OperationIdentity
                : null,
            operation);

    private static DispatchCapability Dispatch() =>
        new(Token(0x22), Call, expiresUnixTimeMilliseconds: 50_000);

    private static OutputCapability Output(
        int maximumBytes,
        long expires = 50_000) =>
        new(Token(0x33), maximumBytes, expires);

    private static CapabilityToken Token(byte value)
    {
        var bytes = Enumerable.Repeat(
            value,
            ContractLimits.CapabilityTokenBytes).ToArray();
        return new CapabilityToken(Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'));
    }

    private static void AssertEventCorrelation(
        GuardianHostEvent message,
        OperationRequest request,
        long sequence)
    {
        Assert.Equal(Guardian, message.GuardianBootId);
        Assert.Equal(Host, message.HostBootId);
        Assert.Equal(HostGeneration, message.HostGeneration);
        Assert.Equal(new HostEventSequence(sequence), message.EventSequence);
        Assert.Equal(request.RequestId, message.RequestId);
        Assert.Equal(Alias, message.SessionAlias);
        Assert.Equal(Transition, message.SessionTransitionVersion);
        Assert.Same(Worker, message.WorkerIdentity);
        Assert.Equal(request.OperationIdentity, message.OperationIdentity);
        switch (message)
        {
            case OutputChunkEvent chunk:
                Assert.Equal(request.Operation.OutputCapability?.Token,
                    chunk.OutputCapabilityToken);
                break;
            case OutputSealEvent seal:
                Assert.Equal(request.Operation.OutputCapability?.Token,
                    seal.OutputCapabilityToken);
                break;
            default:
                throw new Xunit.Sdk.XunitException(
                    $"Unexpected event type {message.GetType().Name}.");
        }
    }

    private sealed class RecordingEventSink :
        IPrivateHostEventSink,
        IDisposable
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

        public void Dispose()
        {
            foreach (var message in Events.OfType<IDisposable>())
                message.Dispose();
            Events.Clear();
        }
    }

    private sealed class FailingEventSink : IPrivateHostEventSink
    {
        internal int WriteCalls { get; private set; }

        public ValueTask WriteEventAsync(
            Func<HostEventSequence, GuardianHostEvent> createEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCalls++;
            using var message = createEvent(new HostEventSequence(WriteCalls)) as IDisposable;
            throw new IOException("ambiguous output event write");
        }
    }
}
