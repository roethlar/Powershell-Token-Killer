using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PtkMcpGuardian.Standalone;
using PtkMcpGuardian.Standalone.Fake;
using PtkMcpServer.Audit;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class GuardianMcpApplicationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Dispatcher_contract_requires_one_admitted_guardian_audit_capability()
    {
        var method = Assert.Single(typeof(IGuardianToolDispatcher).GetMethods());
        Assert.Equal(
            [
                typeof(string),
                typeof(IReadOnlyDictionary<string, JsonElement>),
                typeof(GuardianAuditCall),
                typeof(CancellationToken),
            ],
            method.GetParameters().Select(parameter => parameter.ParameterType));
    }

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
        Assert.True(invocation.AuditCall.Accepted);
        Assert.True(invocation.AuditCall.TerminalWritten);
        Assert.Equal("ptk_state", invocation.AuditCall.Metadata.Request.Tool);
        Assert.Equal("default", invocation.AuditCall.Metadata.Request.SessionRequested);
        Assert.Equal(
            "guardian-apphost-test",
            invocation.AuditCall.Metadata.Actor.ClientName);

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
        Assert.Equal(
            ["call.accepted", "call.completed"],
            harness.AuditLines.Select(line =>
                ParseAudit(line).GetProperty("event_type").GetString()));

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
    public async Task Failed_audit_admission_never_reaches_the_dispatcher()
    {
        var dispatcher = new RecordingDispatcher(
            new GuardianToolResult("must-not-run", isError: false));
        await using var harness = await McpHarness.StartAsync(
            dispatcher,
            new RejectingAdmissionOwner());

        _ = await harness.RequestAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "guardian-admission-test", version = "1.0.0" },
        });
        await harness.NotifyAsync("notifications/initialized", new { });

        var call = await harness.RequestAsync("tools/call", new
        {
            name = "ptk_job",
            arguments = new { action = "list", session = "default" },
        });
        var result = call.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains(
            AuditCallLifecycle.NotStartedMessage,
            result.GetProperty("content")[0].GetProperty("text").GetString(),
            StringComparison.Ordinal);

        var state = await harness.RequestAsync("tools/call", new
        {
            name = "ptk_state",
            arguments = new { listAvailable = false, session = "default" },
        });
        var stateResult = state.GetProperty("result");
        Assert.False(stateResult.GetProperty("isError").GetBoolean());
        Assert.Contains(
            "unrecorded=true",
            stateResult.GetProperty("content")[0].GetProperty("text").GetString(),
            StringComparison.Ordinal);
        Assert.Empty(dispatcher.Invocations);
    }

    [Fact]
    public async Task Executable_rejects_every_mode_except_exact_fake_host_without_output()
    {
        IReadOnlyList<string>[] invocations =
        [
            [],
            ["--host"],
            ["unexpected"],
            ["--FAKE-HOST"],
            ["--fake-host", "extra"],
        ];

        foreach (var arguments in invocations)
        {
            using var input = new MemoryStream();
            using var output = new MemoryStream();
            using var error = new StringWriter();
            Assert.Equal(
                Program.UsageExitCode,
                await Program.RunAsync(arguments, input, output, error));
            Assert.Equal(
                Program.UnsupportedModeMessage + Environment.NewLine,
                error.ToString());
            Assert.Empty(output.ToArray());
        }
    }

    [Fact]
    public async Task Fake_host_startup_failure_is_stderr_only_and_never_opens_MCP()
    {
        var control = new R3FakeHostControl();
        control.EnqueueAttempt(new R3FakeHostAttemptPlan { FailPrepare = true });
        var composition = R3FakeGuardianComposition.Create(control);
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        using var error = new StringWriter();

        Assert.Equal(
            Program.SoftwareExitCode,
            await Program.RunAsync(
                ["--fake-host"],
                input,
                output,
                error,
                composition));
        Assert.Equal(
            Program.RuntimeFailureMessage + Environment.NewLine,
            error.ToString());
        Assert.Empty(output.ToArray());
        Assert.Equal(0, composition.Supervisor.OutstandingCallCount);
        Assert.Equal(0, composition.Supervisor.BackgroundTaskCount);
    }

    [Fact]
    public async Task One_application_instance_owns_exactly_one_public_transport()
    {
        using var audit = R3FakeGuardianAuditRuntime.Create(
            new GuardianBootId(Guid.NewGuid()),
            new GuardianAuditHostSnapshotSource());
        var application = new GuardianMcpApplication(
            new RecordingDispatcher(new GuardianToolResult("{}", isError: false)),
            audit.Runtime,
            R3FakeGuardianComposition.DefaultCallTimeout,
            R3FakeGuardianComposition.MaximumCallTimeout,
            audit.OutputProtector);
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

    private static JsonElement ParseAudit(byte[] line)
    {
        using var document = JsonDocument.Parse(line.AsMemory(0, line.Length - 1));
        return document.RootElement.Clone();
    }

    private sealed record DispatcherInvocation(
        string ToolName,
        IReadOnlyDictionary<string, JsonElement> Arguments,
        GuardianAuditCall AuditCall);

    private sealed class RecordingDispatcher(GuardianToolResult result) : IGuardianToolDispatcher
    {
        private readonly GuardianToolResult _result = result;
        private readonly List<DispatcherInvocation> _invocations = [];

        internal IReadOnlyList<DispatcherInvocation> Invocations => _invocations;

        public ValueTask<GuardianToolResult> DispatchAsync(
            string toolName,
            IReadOnlyDictionary<string, JsonElement> arguments,
            GuardianAuditCall auditCall,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _invocations.Add(new DispatcherInvocation(
                toolName,
                new Dictionary<string, JsonElement>(arguments, StringComparer.Ordinal),
                auditCall));
            return ValueTask.FromResult(_result);
        }
    }

    private sealed class RejectingAdmissionOwner : IAuditAdmissionOwner
    {
        internal RejectingAdmissionOwner()
        {
            var options = AuditOptions.Create(Path.Combine(
                Path.GetTempPath(),
                "ptk-rejecting-guardian-audit-" + Guid.NewGuid().ToString("N")));
            Health = new AuditHealth(options);
            Health.MarkUnavailable("journal.unavailable");
        }

        public AuditHealth Health { get; }

        public void Touch()
        {
        }

        public bool TryBeginCall(
            AuditCallMetadata metadata,
            string? exactSubmittedScript,
            out IAuditBoundaryCall? call,
            out IDisposable? callLease,
            out string? failureClass)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            _ = exactSubmittedScript;
            call = null;
            callLease = null;
            failureClass = "journal.unavailable";
            return false;
        }
    }

    private sealed class McpHarness : IAsyncDisposable
    {
        private readonly ChannelStream _input = new();
        private readonly ChannelStream _output = new();
        private readonly StreamWriter _writer;
        private readonly StreamReader _reader;
        private readonly R3FakeGuardianAuditRuntime _audit;
        private readonly Task _application;
        private readonly List<string> _publicLines = [];
        private int _nextRequestId;
        private bool _shutdown;

        private McpHarness(
            IGuardianToolDispatcher dispatcher,
            IAuditAdmissionOwner? auditOwner)
        {
            _audit = R3FakeGuardianAuditRuntime.Create(
                new GuardianBootId(Guid.NewGuid()),
                new GuardianAuditHostSnapshotSource());
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
            _application = new GuardianMcpApplication(
                    dispatcher,
                    auditOwner ?? _audit.Runtime,
                    R3FakeGuardianComposition.DefaultCallTimeout,
                    R3FakeGuardianComposition.MaximumCallTimeout,
                    _audit.OutputProtector)
                .RunAsync(_input, _output);
        }

        internal IReadOnlyList<string> PublicLines => _publicLines;

        internal IReadOnlyList<byte[]> AuditLines => _audit.Sink.Lines;

        internal static Task<McpHarness> StartAsync(
            IGuardianToolDispatcher dispatcher,
            IAuditAdmissionOwner? auditOwner = null) =>
            Task.FromResult(new McpHarness(dispatcher, auditOwner));

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
                _audit.Dispose();
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
