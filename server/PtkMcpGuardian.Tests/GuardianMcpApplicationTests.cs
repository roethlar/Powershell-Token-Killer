using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PtkMcpGuardian.Standalone;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class GuardianMcpApplicationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Real_stream_transport_uses_only_the_frozen_contract_and_dispatcher()
    {
        var contract = PublicToolContractResource.Parse();
        var dispatcher = new RecordingDispatcher(
            new GuardianToolResult("{\"source\":\"guardian-dispatcher\"}", isError: false));
        await using var harness = await McpHarness.StartAsync(dispatcher);

        var initialize = await harness.RequestAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "guardian-apphost-test", version = "1.0.0" },
        });
        var initializeResult = initialize.GetProperty("result");
        var serverInfo = initializeResult.GetProperty("serverInfo");
        Assert.Equal(contract.ServerIdentity.Name, serverInfo.GetProperty("name").GetString());
        Assert.Equal(contract.ServerIdentity.Version, serverInfo.GetProperty("version").GetString());
        Assert.Equal(contract.Instructions, initializeResult.GetProperty("instructions").GetString());
        Assert.True(initializeResult.GetProperty("capabilities").TryGetProperty("tools", out _));
        await harness.NotifyAsync("notifications/initialized", new { });

        var listed = await harness.RequestAsync("tools/list", new { });
        Assert.True(listed.TryGetProperty("result", out var listResult), listed.GetRawText());
        var tools = listResult.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(contract.Tools.Count, tools.Length);
        for (var index = 0; index < tools.Length; index++)
        {
            var actual = tools[index];
            var expected = contract.Tools[index];
            Assert.Equal(
                ["name", "description", "inputSchema"],
                actual.EnumerateObject().Select(property => property.Name));
            Assert.Equal(expected.Name, actual.GetProperty("name").GetString());
            Assert.Equal(expected.Description, actual.GetProperty("description").GetString());
            Assert.Equal(
                JsonSerializer.Serialize(expected.InputSchema),
                actual.GetProperty("inputSchema").GetRawText());
        }

        var call = await harness.RequestAsync("tools/call", new
        {
            name = "ptk_state",
            arguments = new { listAvailable = true, session = "default" },
        });
        AssertToolResult(
            call,
            expectedText: "{\"source\":\"guardian-dispatcher\"}",
            expectedError: false);
        var invocation = Assert.Single(dispatcher.Invocations);
        Assert.Equal("ptk_state", invocation.ToolName);
        Assert.True(invocation.Arguments["listAvailable"].GetBoolean());
        Assert.Equal("default", invocation.Arguments["session"].GetString());

        var unknown = await harness.RequestAsync("tools/call", new
        {
            name = "not_a_ptk_tool",
            arguments = new { },
        });
        AssertToolResult(
            unknown,
            GuardianMcpApplication.UnknownToolText,
            expectedError: true);
        Assert.Single(dispatcher.Invocations);

        await harness.ShutdownAsync();
        Assert.Equal(4, harness.PublicLines.Count);
        Assert.All(harness.PublicLines, line =>
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
        });
    }

    [Fact]
    public void Executable_rejects_every_mode_without_opening_a_public_transport()
    {
        IReadOnlyList<string>[] invocations =
        [
            [],
            ["--fake-host"],
            ["--host"],
            ["unexpected"],
        ];

        foreach (var arguments in invocations)
        {
            using var error = new StringWriter();
            Assert.Equal(Program.UsageExitCode, Program.Run(arguments, error));
            Assert.Equal(
                Program.UnsupportedModeMessage + Environment.NewLine,
                error.ToString());
        }
    }

    [Fact]
    public async Task One_application_instance_owns_exactly_one_public_transport()
    {
        var application = new GuardianMcpApplication(new RecordingDispatcher(
            new GuardianToolResult("{}", isError: false)));
        using var firstInput = new ChannelStream();
        using var firstOutput = new ChannelStream();
        firstInput.CompleteWriting();
        await application.RunAsync(firstInput, firstOutput).WaitAsync(TestTimeout);

        using var secondInput = new ChannelStream();
        using var secondOutput = new ChannelStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            application.RunAsync(secondInput, secondOutput));
    }

    private static void AssertToolResult(
        JsonElement response,
        string expectedText,
        bool expectedError)
    {
        var result = response.GetProperty("result");
        Assert.Equal(expectedError, result.GetProperty("isError").GetBoolean());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal(["type", "text"], content.EnumerateObject().Select(property => property.Name));
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.Equal(expectedText, content.GetProperty("text").GetString());
        Assert.False(result.TryGetProperty("structuredContent", out _));
    }

    private sealed record DispatcherInvocation(
        string ToolName,
        IReadOnlyDictionary<string, JsonElement> Arguments);

    private sealed class RecordingDispatcher(GuardianToolResult result) : IGuardianToolDispatcher
    {
        private readonly GuardianToolResult _result = result;
        private readonly List<DispatcherInvocation> _invocations = [];

        internal IReadOnlyList<DispatcherInvocation> Invocations => _invocations;

        public ValueTask<GuardianToolResult> DispatchAsync(
            string toolName,
            IReadOnlyDictionary<string, JsonElement> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _invocations.Add(new DispatcherInvocation(
                toolName,
                new Dictionary<string, JsonElement>(arguments, StringComparer.Ordinal)));
            return ValueTask.FromResult(_result);
        }
    }

    private sealed class McpHarness : IAsyncDisposable
    {
        private readonly ChannelStream _input = new();
        private readonly ChannelStream _output = new();
        private readonly StreamWriter _writer;
        private readonly StreamReader _reader;
        private readonly Task _application;
        private readonly List<string> _publicLines = [];
        private int _nextRequestId;
        private bool _shutdown;

        private McpHarness(IGuardianToolDispatcher dispatcher)
        {
            _writer = new StreamWriter(
                _input,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            _reader = new StreamReader(
                _output,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            _application = new GuardianMcpApplication(dispatcher).RunAsync(_input, _output);
        }

        internal IReadOnlyList<string> PublicLines => _publicLines;

        internal static Task<McpHarness> StartAsync(IGuardianToolDispatcher dispatcher) =>
            Task.FromResult(new McpHarness(dispatcher));

        internal async Task<JsonElement> RequestAsync(string method, object parameters)
        {
            var id = Interlocked.Increment(ref _nextRequestId);
            await WriteAsync(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            });

            using var cancellation = new CancellationTokenSource(TestTimeout);
            while (true)
            {
                var line = await _reader.ReadLineAsync(cancellation.Token);
                Assert.NotNull(line);
                _publicLines.Add(line);
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.TryGetProperty("id", out var responseId) &&
                    responseId.ValueKind == JsonValueKind.Number &&
                    responseId.GetInt32() == id)
                {
                    return message.Clone();
                }
            }
        }

        internal Task NotifyAsync(string method, object parameters) =>
            WriteAsync(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters,
            });

        internal async Task ShutdownAsync()
        {
            if (_shutdown) return;
            _input.CompleteWriting();
            await _application.WaitAsync(TestTimeout);
            _shutdown = true;
        }

        private async Task WriteAsync(object message)
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(message));
            await _writer.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await ShutdownAsync();
            }
            finally
            {
                _writer.Dispose();
                _reader.Dispose();
                _input.Dispose();
                _output.Dispose();
            }
        }
    }

    private sealed class ChannelStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private byte[]? _current;
        private int _currentOffset;
        private int _disposed;

        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => Volatile.Read(ref _disposed) == 0;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void CompleteWriting() => _chunks.Writer.TryComplete();

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
                    _current = await _chunks.Reader.ReadAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
            _current.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            return count;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _chunks.Writer.WriteAsync(buffer.ToArray(), cancellationToken)
                .ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _chunks.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }
}
