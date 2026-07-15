using System.Text;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerProcessExitTests
{
    public static TheoryData<int, string, int, string?> ServerExitCases =>
        new()
        {
            { (int)WorkerServerExitKind.Shutdown, "shutdown", 0, null },
            { (int)WorkerServerExitKind.Eof, "eof_before_initialize", 0, null },
            { (int)WorkerServerExitKind.Eof, "eof_during_initialize", 0, null },
            { (int)WorkerServerExitKind.Eof, "eof_after_initialize", 0, null },
            { (int)WorkerServerExitKind.Eof, "eof_after_ready", 0, null },
            { (int)WorkerServerExitKind.Canceled, "canceled", 0, null },
            { (int)WorkerServerExitKind.InitializeFailed, "initialize_deadline_expired", 81,
                "ptk_worker_exit kind=initialize_failed detail=initialize_deadline_expired\n" },
            { (int)WorkerServerExitKind.InitializeFailed, "initialize_canceled", 81,
                "ptk_worker_exit kind=initialize_failed detail=initialize_canceled\n" },
            { (int)WorkerServerExitKind.InitializeFailed, "initialize_failed", 81,
                "ptk_worker_exit kind=initialize_failed detail=initialize_failed\n" },
            { (int)WorkerServerExitKind.ProtocolError, "bom_forbidden", 82,
                "ptk_worker_exit kind=protocol_error detail=bom_forbidden\n" },
            { (int)WorkerServerExitKind.ProtocolError, "duplicate_field", 82,
                "ptk_worker_exit kind=protocol_error detail=duplicate_field\n" },
            { (int)WorkerServerExitKind.ProtocolError, "frame_too_large", 82,
                "ptk_worker_exit kind=protocol_error detail=frame_too_large\n" },
            { (int)WorkerServerExitKind.ProtocolError, "initialize_required", 82,
                "ptk_worker_exit kind=protocol_error detail=initialize_required\n" },
            { (int)WorkerServerExitKind.ProtocolError, "invalid_envelope", 82,
                "ptk_worker_exit kind=protocol_error detail=invalid_envelope\n" },
            { (int)WorkerServerExitKind.ProtocolError, "invalid_field", 82,
                "ptk_worker_exit kind=protocol_error detail=invalid_field\n" },
            { (int)WorkerServerExitKind.ProtocolError, "invalid_initialize_field", 82,
                "ptk_worker_exit kind=protocol_error detail=invalid_initialize_field\n" },
            { (int)WorkerServerExitKind.ProtocolError, "invalid_json", 82,
                "ptk_worker_exit kind=protocol_error detail=invalid_json\n" },
            { (int)WorkerServerExitKind.ProtocolError, "invalid_payload", 82,
                "ptk_worker_exit kind=protocol_error detail=invalid_payload\n" },
            { (int)WorkerServerExitKind.ProtocolError, "message_before_ready", 82,
                "ptk_worker_exit kind=protocol_error detail=message_before_ready\n" },
            { (int)WorkerServerExitKind.ProtocolError, "missing_field", 82,
                "ptk_worker_exit kind=protocol_error detail=missing_field\n" },
            { (int)WorkerServerExitKind.ProtocolError, "missing_initialize_field", 82,
                "ptk_worker_exit kind=protocol_error detail=missing_initialize_field\n" },
            { (int)WorkerServerExitKind.ProtocolError, "request_id_required", 82,
                "ptk_worker_exit kind=protocol_error detail=request_id_required\n" },
            { (int)WorkerServerExitKind.ProtocolError, "truncated_frame", 82,
                "ptk_worker_exit kind=protocol_error detail=truncated_frame\n" },
            { (int)WorkerServerExitKind.ProtocolError, "unknown_field", 82,
                "ptk_worker_exit kind=protocol_error detail=unknown_field\n" },
            { (int)WorkerServerExitKind.ProtocolError, "unknown_initialize_field", 82,
                "ptk_worker_exit kind=protocol_error detail=unknown_initialize_field\n" },
            { (int)WorkerServerExitKind.ProtocolError, "unknown_kind", 82,
                "ptk_worker_exit kind=protocol_error detail=unknown_kind\n" },
            { (int)WorkerServerExitKind.ProtocolError, "unknown_version", 82,
                "ptk_worker_exit kind=protocol_error detail=unknown_version\n" },
            { (int)WorkerServerExitKind.ProtocolError, "unsupported_message", 82,
                "ptk_worker_exit kind=protocol_error detail=unsupported_message\n" },
            { (int)WorkerServerExitKind.ProtocolError, "worker_boot_mismatch", 82,
                "ptk_worker_exit kind=protocol_error detail=worker_boot_mismatch\n" },
            { (int)WorkerServerExitKind.TransportFailure, "request_transport_failure", 83,
                "ptk_worker_exit kind=transport_failure detail=request_transport_failure\n" },
            { (int)WorkerServerExitKind.TransportFailure, "event_transport_failure", 83,
                "ptk_worker_exit kind=transport_failure detail=event_transport_failure\n" },
            { (int)WorkerServerExitKind.RuntimeFailure, "cleanup_failed", 84,
                "ptk_worker_exit kind=runtime_failure detail=cleanup_failed\n" },
            { (int)WorkerServerExitKind.RuntimeFailure, "initialize_cleanup_failed", 84,
                "ptk_worker_exit kind=runtime_failure detail=initialize_cleanup_failed\n" },
            { (int)WorkerServerExitKind.RuntimeFailure, "outbound_protocol_failure", 84,
                "ptk_worker_exit kind=runtime_failure detail=outbound_protocol_failure\n" },
            { (int)WorkerServerExitKind.RuntimeFailure, "runtime_failure", 84,
                "ptk_worker_exit kind=runtime_failure detail=runtime_failure\n" },
            { (int)WorkerServerExitKind.RuntimeFailure, "shutdown_failed", 84,
                "ptk_worker_exit kind=runtime_failure detail=shutdown_failed\n" },
        };

    public static TheoryData<string> BootstrapDetails =>
        new()
        {
            "platform_unsupported",
            "handle_missing",
            "handle_invalid",
            "handle_alias",
            "handle_duplication_failed",
            "handle_not_pipe",
            "handle_inheritable",
            "stream_creation_failed",
            "environment_removal_failed",
            "bootstrap_failure",
        };

    [Fact]
    public void Invocation_failure_is_one_exact_bounded_ascii_write()
    {
        var stream = new RecordingWriteStream();

        var exitCode = WorkerProcessExit.WriteInvocationFailure(stream);

        Assert.Equal(64, exitCode);
        AssertExactDiagnostic(
            stream,
            "ptk_worker_exit kind=invocation_error detail=invalid_arguments\n");
    }

    [Theory]
    [MemberData(nameof(BootstrapDetails))]
    public void Bootstrap_failure_preserves_only_allow_listed_details(string detailCode)
    {
        var stream = new RecordingWriteStream();

        var exitCode = WorkerProcessExit.WriteBootstrapFailure(detailCode, stream);

        Assert.Equal(80, exitCode);
        AssertExactDiagnostic(
            stream,
            $"ptk_worker_exit kind=bootstrap_failure detail={detailCode}\n");
    }

    [Theory]
    [MemberData(nameof(ServerExitCases))]
    public void Server_exit_mapping_is_exact_and_bounded(
        int kind,
        string detailCode,
        int expectedExitCode,
        string? expectedDiagnostic)
    {
        var stream = new RecordingWriteStream();

        var exitCode = WorkerProcessExit.WriteServerExit(
            new WorkerServerExit((WorkerServerExitKind)kind, detailCode),
            stream);

        Assert.Equal(expectedExitCode, exitCode);
        if (expectedDiagnostic is null)
        {
            Assert.Equal(0, stream.WriteCalls);
            Assert.Empty(stream.Bytes);
            Assert.Equal(0, stream.FlushCalls);
            Assert.Equal(0, stream.AsyncWriteCalls);
        }
        else
        {
            AssertExactDiagnostic(stream, expectedDiagnostic);
        }
    }

    [Theory]
    [InlineData((int)WorkerServerExitKind.InitializeFailed, 81, "initialize_failed", "initialize_failed")]
    [InlineData((int)WorkerServerExitKind.ProtocolError, 82, "protocol_error", "protocol_error")]
    [InlineData((int)WorkerServerExitKind.TransportFailure, 83, "transport_failure", "transport_failure")]
    [InlineData((int)WorkerServerExitKind.RuntimeFailure, 84, "runtime_failure", "runtime_failure")]
    public void Unknown_server_detail_is_generic_and_cannot_inject_data(
        int kind,
        int expectedExitCode,
        string expectedKind,
        string expectedDetail)
    {
        const string hostile = "secret_path=C:\\sensitive\r\nptk_worker_exit kind=forged detail=é";
        var stream = new RecordingWriteStream();

        var exitCode = WorkerProcessExit.WriteServerExit(
            new WorkerServerExit((WorkerServerExitKind)kind, hostile),
            stream);

        Assert.Equal(expectedExitCode, exitCode);
        AssertExactDiagnostic(
            stream,
            $"ptk_worker_exit kind={expectedKind} detail={expectedDetail}\n");
        Assert.DoesNotContain("secret", Encoding.ASCII.GetString(stream.Bytes));
    }

    [Fact]
    public void Unknown_bootstrap_detail_is_generic_and_cannot_inject_data()
    {
        const string hostile = "handle=18446744073709551615\nsecret=é";
        var stream = new RecordingWriteStream();

        var exitCode = WorkerProcessExit.WriteBootstrapFailure(hostile, stream);

        Assert.Equal(80, exitCode);
        AssertExactDiagnostic(
            stream,
            "ptk_worker_exit kind=bootstrap_failure detail=bootstrap_failure\n");
        Assert.DoesNotContain("18446744073709551615", Encoding.ASCII.GetString(stream.Bytes));
    }

    [Fact]
    public void Unknown_server_kind_is_generic_runtime_failure()
    {
        var stream = new RecordingWriteStream();

        var exitCode = WorkerProcessExit.WriteServerExit(
            new WorkerServerExit((WorkerServerExitKind)int.MaxValue, "secret_detail"),
            stream);

        Assert.Equal(84, exitCode);
        AssertExactDiagnostic(
            stream,
            "ptk_worker_exit kind=runtime_failure detail=runtime_failure\n");
    }

    [Theory]
    [InlineData((int)WorkerServerExitKind.Shutdown)]
    [InlineData((int)WorkerServerExitKind.Eof)]
    [InlineData((int)WorkerServerExitKind.Canceled)]
    public void Zero_exit_never_touches_standard_error(int kind)
    {
        var exitCode = WorkerProcessExit.WriteServerExit(
            new WorkerServerExit((WorkerServerExitKind)kind, "hostile_detail"),
            new TouchForbiddenStream());

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData(FailureMode.ThrowBeforeWrite)]
    [InlineData(FailureMode.WritePrefixThenThrow)]
    public void Diagnostic_write_failure_never_retries_or_changes_exit_code(FailureMode mode)
    {
        var stream = new RecordingWriteStream(mode);

        var exitCode = WorkerProcessExit.WriteServerExit(
            new WorkerServerExit(WorkerServerExitKind.ProtocolError, "invalid_json"),
            stream);

        Assert.Equal(82, exitCode);
        Assert.Equal(1, stream.WriteCalls);
        Assert.Equal(0, stream.AsyncWriteCalls);
        Assert.Equal(0, stream.FlushCalls);
        if (mode == FailureMode.ThrowBeforeWrite)
            Assert.Empty(stream.Bytes);
        else
            Assert.NotEmpty(stream.Bytes);
    }

    private static void AssertExactDiagnostic(
        RecordingWriteStream stream,
        string expected)
    {
        Assert.Equal(1, stream.WriteCalls);
        Assert.Equal(0, stream.AsyncWriteCalls);
        Assert.Equal(0, stream.FlushCalls);
        Assert.Equal(Encoding.ASCII.GetBytes(expected), stream.Bytes);
        Assert.InRange(stream.Bytes.Length, 1, WorkerProcessExit.MaximumDiagnosticBytes);
        Assert.All(stream.Bytes, value => Assert.InRange(value, (byte)0, (byte)0x7f));
        Assert.Equal((byte)'\n', stream.Bytes[^1]);
        Assert.Equal(1, stream.Bytes.Count(value => value == (byte)'\n'));
        Assert.DoesNotContain((byte)'\r', stream.Bytes);
    }

    public enum FailureMode
    {
        None,
        ThrowBeforeWrite,
        WritePrefixThenThrow,
    }

    private sealed class RecordingWriteStream(FailureMode failureMode = FailureMode.None) : Stream
    {
        private readonly MemoryStream _bytes = new();

        internal int WriteCalls { get; private set; }
        internal int AsyncWriteCalls { get; private set; }
        internal int FlushCalls { get; private set; }
        internal byte[] Bytes => _bytes.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _bytes.Length;
        public override long Position
        {
            get => _bytes.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => FlushCalls++;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCalls++;
            if (failureMode == FailureMode.ThrowBeforeWrite)
                throw new IOException("simulated diagnostic failure");
            if (failureMode == FailureMode.WritePrefixThenThrow)
            {
                _bytes.Write(buffer, offset, Math.Min(7, count));
                throw new IOException("simulated partial diagnostic failure");
            }
            _bytes.Write(buffer, offset, count);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            AsyncWriteCalls++;
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            AsyncWriteCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TouchForbiddenStream : Stream
    {
        public override bool CanRead => throw Touched();
        public override bool CanSeek => throw Touched();
        public override bool CanWrite => throw Touched();
        public override long Length => throw Touched();
        public override long Position
        {
            get => throw Touched();
            set => throw Touched();
        }

        public override void Flush() => throw Touched();
        public override int Read(byte[] buffer, int offset, int count) => throw Touched();
        public override long Seek(long offset, SeekOrigin origin) => throw Touched();
        public override void SetLength(long value) => throw Touched();
        public override void Write(byte[] buffer, int offset, int count) => throw Touched();

        private static InvalidOperationException Touched() =>
            new("Zero exit touched standard error.");
    }
}
