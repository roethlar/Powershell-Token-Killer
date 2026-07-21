using PtkMcpServer.GuardianHost;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class PrivateHostProcessRunnerTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb"));
    private static readonly HostGeneration Generation = new(17);
    private static readonly PrivateHostServerPins Pins = new(
        Digest('a'),
        Digest('b'),
        Digest('c'),
        Digest('d'),
        Digest('e'));

    [Fact]
    public async Task Captured_bootstrap_owns_exact_context_until_server_completion()
    {
        var request = new RecordingHandle();
        var events = new RecordingHandle();
        var bootstrap = Bootstrap(request, events);
        var runtime = new RecordingRuntime();
        var timeline = new List<string>();
        IPrivateHostEventSink? runtimeEventSink = null;
        using var cancellation = new CancellationTokenSource();

        var exitCode = await PrivateHostProcessRunner.RunAsync(
            bootstrap,
            (identity, pins, eventSink) =>
            {
                timeline.Add("runtime");
                runtimeEventSink = eventSink;
                AssertIdentity(identity, hostPid: 4242);
                Assert.Same(Pins, pins);
                Assert.Equal(1, request.OpenCalls);
                Assert.Equal(1, events.OpenCalls);
                Assert.False(request.Stream.Disposed);
                Assert.False(events.Stream.Disposed);
                return runtime;
            },
            (context, token) =>
            {
                timeline.Add("server");
                Assert.Same(request.Stream, context.RequestReadStream);
                Assert.Same(context.OutboundChannel, runtimeEventSink);
                AssertIdentity(context.Identity, hostPid: 4242);
                Assert.Same(Pins, context.Pins);
                Assert.Same(runtime, context.Runtime);
                Assert.Equal(cancellation.Token, token);
                Assert.False(request.Stream.Disposed);
                Assert.False(events.Stream.Disposed);
                return Task.CompletedTask;
            },
            hostProcessId: 4242,
            cancellation.Token);

        Assert.Equal(0, exitCode);
        Assert.Equal(["runtime", "server"], timeline);
        Assert.Equal(FileAccess.Read, request.Access);
        Assert.Equal(FileAccess.Write, events.Access);
        Assert.True(request.Stream.Disposed);
        Assert.True(events.Stream.Disposed);
        Assert.Equal(1, request.DisposeCalls);
        Assert.Equal(1, events.DisposeCalls);
    }

    [Fact]
    public async Task Server_failure_still_closes_both_transferred_streams()
    {
        var request = new RecordingHandle();
        var events = new RecordingHandle();
        var bootstrap = Bootstrap(request, events);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            PrivateHostProcessRunner.RunAsync(
                bootstrap,
                (_, _, _) => new RecordingRuntime(),
                (_, _) => throw new IOException("private protocol failed"),
                hostProcessId: 4242));

        Assert.Equal("private protocol failed", exception.Message);
        Assert.True(request.Stream.Disposed);
        Assert.True(events.Stream.Disposed);
        Assert.Equal(1, request.DisposeCalls);
        Assert.Equal(1, events.DisposeCalls);
    }

    [Fact]
    public async Task Runtime_factory_failure_closes_streams_before_propagating()
    {
        var request = new RecordingHandle();
        var events = new RecordingHandle();
        var bootstrap = Bootstrap(request, events);
        var serverCalls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PrivateHostProcessRunner.RunAsync(
                bootstrap,
                (_, _, _) => throw new InvalidOperationException("runtime unavailable"),
                (_, _) =>
                {
                    serverCalls++;
                    return Task.CompletedTask;
                },
                hostProcessId: 4242));

        Assert.Equal(0, serverCalls);
        Assert.True(request.Stream.Disposed);
        Assert.True(events.Stream.Disposed);
    }

    [Fact]
    public async Task Stream_open_failure_closes_every_owned_handle_and_never_builds_runtime()
    {
        var request = new RecordingHandle();
        var events = new RecordingHandle { ThrowOnOpen = true };
        var bootstrap = Bootstrap(request, events);
        var runtimeCalls = 0;

        var exception = await Assert.ThrowsAsync<PrivateHostBootstrapException>(() =>
            PrivateHostProcessRunner.RunAsync(
                bootstrap,
                (_, _, _) =>
                {
                    runtimeCalls++;
                    return new RecordingRuntime();
                },
                (_, _) => Task.CompletedTask,
                hostProcessId: 4242));

        Assert.Equal("stream_creation_failed", exception.DetailCode);
        Assert.Equal(0, runtimeCalls);
        Assert.True(request.Stream.Disposed);
        Assert.Equal(1, request.DisposeCalls);
        Assert.Equal(1, events.DisposeCalls);
    }

    private static PrivateHostBootstrapValues Bootstrap(
        RecordingHandle request,
        RecordingHandle events) =>
        new(request, events, Guardian, Host, Generation, Pins);

    private static void AssertIdentity(PrivateHostServerIdentity identity, int hostPid)
    {
        Assert.Equal(Guardian, identity.GuardianBootId);
        Assert.Equal(Host, identity.HostBootId);
        Assert.Equal(Generation, identity.HostGeneration);
        Assert.Equal(hostPid, identity.HostPid);
    }

    private static Sha256Digest Digest(char value) => new(new string(value, 64));

    private sealed class RecordingHandle : IPrivateHostBootstrapHandle
    {
        internal TrackingStream Stream { get; } = new();
        internal int OpenCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal FileAccess? Access { get; private set; }
        internal bool ThrowOnOpen { get; init; }

        public Stream OpenStream(FileAccess access)
        {
            OpenCalls++;
            Access = access;
            if (ThrowOnOpen)
                throw new IOException("stream unavailable");
            return Stream;
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class TrackingStream : MemoryStream
    {
        internal bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingRuntime : IPrivateHostRuntime
    {
        public ValueTask InitializeAsync(
            PrivateHostInitialization initialization,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<PrivateHostOperationOutcome> ExecuteOperationAsync(
            OperationRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(PrivateHostOperationOutcome.Failed(
                GuardianHostPrivateDetailCode.SessionNotFound));

        public ValueTask ShutdownAsync(
            GuardianHostShutdown shutdown,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
