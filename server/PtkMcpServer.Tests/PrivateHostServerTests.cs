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
    public void Private_initialization_server_remains_unwired_from_runtime_and_mcp_execution()
    {
        var source = File.ReadAllText(PrivateHostServerSourcePath());
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
        Func<int, byte[]>? manifestBufferFactory = null) =>
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
            manifestBufferFactory);

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

    private sealed class ChannelStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        private byte[]? _current;
        private int _currentOffset;

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
            CancellationToken cancellationToken = default) =>
            _channel.Writer.WriteAsync(buffer.ToArray(), cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _channel.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }
}
