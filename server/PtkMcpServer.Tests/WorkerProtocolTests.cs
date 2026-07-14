using System.Buffers;
using System.Text;
using System.Text.Json;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerProtocolTests
{
    private const int FrozenMaximumEncodedFrameBytes = 1_048_576;
    private const int FrozenMaximumJsonDepth = 32;
    private static readonly Guid BootId = Guid.Parse("15a6bcb5-4f5f-4dc9-b8a8-c117e52159c2");

    [Fact]
    public void Protocol_constants_match_frozen_v1_contract()
    {
        Assert.Equal(1, WorkerProtocol.Version);
        Assert.Equal(FrozenMaximumJsonDepth, WorkerProtocol.MaximumJsonDepth);
        Assert.Equal(FrozenMaximumEncodedFrameBytes, WorkerProtocol.MaximumEncodedFrameBytes);
    }

    [Theory]
    [InlineData((int)WorkerMessageKind.Initialize, null)]
    [InlineData((int)WorkerMessageKind.Prepare, 1L)]
    [InlineData((int)WorkerMessageKind.Commit, 2L)]
    [InlineData((int)WorkerMessageKind.Abort, 3L)]
    [InlineData((int)WorkerMessageKind.Request, 4L)]
    [InlineData((int)WorkerMessageKind.Cancel, 5L)]
    [InlineData((int)WorkerMessageKind.Event, null)]
    [InlineData((int)WorkerMessageKind.Response, 6L)]
    [InlineData((int)WorkerMessageKind.Shutdown, null)]
    public void Codec_round_trips_every_v1_kind(int kindValue, long? requestId)
    {
        var kind = (WorkerMessageKind)kindValue;
        var envelope = Envelope(kind, requestId, "round-trip");

        var decoded = WorkerProtocol.Decode(WorkerProtocol.Encode(envelope));

        Assert.Equal(WorkerProtocol.Version, decoded.ProtocolVersion);
        Assert.Equal(kind, decoded.Kind);
        Assert.Equal(BootId, decoded.WorkerBootId);
        Assert.Equal(requestId, decoded.RequestId);
        Assert.Equal("round-trip", decoded.Payload.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData((int)WorkerMessageKind.Initialize, "initialize")]
    [InlineData((int)WorkerMessageKind.Prepare, "prepare")]
    [InlineData((int)WorkerMessageKind.Commit, "commit")]
    [InlineData((int)WorkerMessageKind.Abort, "abort")]
    [InlineData((int)WorkerMessageKind.Request, "request")]
    [InlineData((int)WorkerMessageKind.Cancel, "cancel")]
    [InlineData((int)WorkerMessageKind.Event, "event")]
    [InlineData((int)WorkerMessageKind.Response, "response")]
    [InlineData((int)WorkerMessageKind.Shutdown, "shutdown")]
    public void Codec_emits_frozen_v1_wire_kind(int kindValue, string wireKind)
    {
        var encoded = WorkerProtocol.Encode(Envelope((WorkerMessageKind)kindValue, null, "wire"));
        using var document = JsonDocument.Parse(encoded);

        Assert.Equal(wireKind, document.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Reader_accepts_one_byte_fragmentation()
    {
        var frame = Terminate(WorkerProtocol.Encode(Envelope(WorkerMessageKind.Request, 17, "fragmented")));
        await using var input = new FragmentingReadStream(frame, maximumReadBytes: 1);
        var reader = new WorkerProtocolReader(input);

        var decoded = await reader.ReadAsync();
        var eof = await reader.ReadAsync();

        Assert.NotNull(decoded);
        Assert.Equal(17, decoded.RequestId);
        Assert.Equal("fragmented", decoded.Payload.GetProperty("value").GetString());
        Assert.Null(eof);
    }

    [Fact]
    public async Task Reader_preserves_coalesced_frames_for_sequential_reads()
    {
        var first = Terminate(WorkerProtocol.Encode(Envelope(WorkerMessageKind.Event, null, "first")));
        var second = Terminate(WorkerProtocol.Encode(Envelope(WorkerMessageKind.Response, 29, "second")));
        await using var input = new MemoryStream(first.Concat(second).ToArray());
        var reader = new WorkerProtocolReader(input);

        var decodedFirst = await reader.ReadAsync();
        var decodedSecond = await reader.ReadAsync();
        var eof = await reader.ReadAsync();

        Assert.Equal("first", decodedFirst!.Payload.GetProperty("value").GetString());
        Assert.Equal("second", decodedSecond!.Payload.GetProperty("value").GetString());
        Assert.Null(eof);
    }

    [Fact]
    public async Task Reader_accepts_exact_encoded_frame_limit()
    {
        var exact = BuildValidFrame(FrozenMaximumEncodedFrameBytes);
        await using var input = new MemoryStream(Terminate(exact));
        var reader = new WorkerProtocolReader(input);

        var decoded = await reader.ReadAsync();

        Assert.NotNull(decoded);
        Assert.Equal(
            FrozenMaximumEncodedFrameBytes,
            exact.Length);
    }

    [Fact]
    public async Task Reader_rejects_first_payload_byte_past_frame_limit()
    {
        var exact = BuildValidFrame(FrozenMaximumEncodedFrameBytes);
        var oversized = new byte[exact.Length + 2];
        exact.CopyTo(oversized, 0);
        oversized[^2] = (byte)'x';
        oversized[^1] = (byte)'\n';
        await using var input = new MemoryStream(oversized);
        var reader = new WorkerProtocolReader(input);

        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(
            async () => await reader.ReadAsync());

        Assert.Equal("frame_too_large", exception.DetailCode);
    }

    [Fact]
    public void Codec_rejects_encoded_output_past_frame_limit()
    {
        var envelope = Envelope(
            WorkerMessageKind.Event,
            null,
            new string('x', FrozenMaximumEncodedFrameBytes));

        var exception = Assert.Throws<WorkerProtocolException>(
            () => WorkerProtocol.Encode(envelope));

        Assert.Equal("frame_too_large", exception.DetailCode);
    }

    [Fact]
    public void Codec_emits_exact_encoded_frame_limit()
    {
        var emptyLength = WorkerProtocol.Encode(
            Envelope(WorkerMessageKind.Event, null, string.Empty)).Length;
        var envelope = Envelope(
            WorkerMessageKind.Event,
            null,
            new string('x', FrozenMaximumEncodedFrameBytes - emptyLength));

        var encoded = WorkerProtocol.Encode(envelope);

        Assert.Equal(FrozenMaximumEncodedFrameBytes, encoded.Length);
    }

    [Fact]
    public void Codec_accepts_exact_v1_json_depth()
    {
        var envelope = EnvelopeWithNestedPayload(nestedArrays: 30);

        var encoded = WorkerProtocol.Encode(envelope);
        var decoded = WorkerProtocol.Decode(encoded);

        Assert.Equal(WorkerMessageKind.Event, decoded.Kind);
    }

    [Fact]
    public void Codec_rejects_one_level_past_v1_json_depth()
    {
        var envelope = EnvelopeWithNestedPayload(nestedArrays: 31);

        var exception = Assert.Throws<WorkerProtocolException>(
            () => WorkerProtocol.Encode(envelope));

        Assert.Equal("invalid_json", exception.DetailCode);
    }

    [Fact]
    public void Codec_rejects_depth_before_recursing_into_deeper_payload()
    {
        const int nestedArrays = 30;
        var json = "{\"value\":" +
            new string('[', nestedArrays) +
            "{\"duplicate\":1,\"duplicate\":2}" +
            new string(']', nestedArrays) +
            "}";
        using var payload = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { MaxDepth = 128 });
        var envelope = new WorkerEnvelope(
            WorkerProtocol.Version,
            WorkerMessageKind.Event,
            BootId,
            null,
            payload.RootElement.Clone());

        var exception = Assert.Throws<WorkerProtocolException>(
            () => WorkerProtocol.Encode(envelope));

        Assert.Equal("invalid_json", exception.DetailCode);
    }

    [Fact]
    public async Task Reader_rejects_eof_before_lf()
    {
        await using var input = new MemoryStream(
            WorkerProtocol.Encode(Envelope(WorkerMessageKind.Event, null, "partial")));
        var reader = new WorkerProtocolReader(input);

        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(
            async () => await reader.ReadAsync());

        Assert.Equal("truncated_frame", exception.DetailCode);
    }

    [Fact]
    public async Task Reader_returns_sensitive_frame_buffer_with_clearing_required()
    {
        const string sentinel = "private-script-frame-sentinel";
        var pool = new ObservingFramePool(sentinel);
        await using var input = new MemoryStream(Terminate(
            WorkerProtocol.Encode(Envelope(WorkerMessageKind.Prepare, 33, sentinel))));
        var reader = new WorkerProtocolReader(input, pool);

        _ = await reader.ReadAsync();

        Assert.True(pool.SawSentinelBeforeReturn);
        Assert.True(pool.ClearRequested);
        Assert.True(pool.ReturnedArrayWasCleared);
    }

    [Fact]
    public async Task Writer_serializes_concurrent_frames_through_one_gate()
    {
        await using var output = new CoordinatedWriteStream();
        var writer = new WorkerProtocolWriter(output);

        var first = writer.WriteAsync(Envelope(WorkerMessageKind.Event, null, "first")).AsTask();
        await output.FirstWriterEntered;

        var second = writer.WriteAsync(Envelope(WorkerMessageKind.Response, 41, "second")).AsTask();

        // WriteAsync runs synchronously through gate acquisition. If the gate
        // is absent, the second stream writer has already entered here.
        Assert.False(output.SecondWriterEntered.IsCompleted);
        output.ReleaseFirstWriter();
        await Task.WhenAll(first, second);

        Assert.Equal(1, output.MaximumConcurrentWriters);
        await using var input = new MemoryStream(output.ToArray());
        var reader = new WorkerProtocolReader(input);
        Assert.Equal("first", (await reader.ReadAsync())!.Payload.GetProperty("value").GetString());
        Assert.Equal("second", (await reader.ReadAsync())!.Payload.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Writer_latches_transport_failure_and_refuses_later_frames()
    {
        await using var output = new PartialWriteFailingStream(bytesBeforeFailure: 17);
        var writer = new WorkerProtocolWriter(output);

        await Assert.ThrowsAsync<IOException>(async () =>
            await writer.WriteAsync(Envelope(WorkerMessageKind.Event, null, "first")));
        var bytesAfterFailure = output.BytesWritten;

        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await writer.WriteAsync(Envelope(WorkerMessageKind.Event, null, "second")));

        Assert.Equal("writer_faulted", exception.DetailCode);
        Assert.Equal(bytesAfterFailure, output.BytesWritten);
        Assert.Equal(1, output.WriteCalls);
    }

    [Theory]
    [MemberData(nameof(InvalidFrames))]
    public void Codec_rejects_invalid_or_ambiguous_frames(byte[] frame, string detailCode)
    {
        var exception = Assert.Throws<WorkerProtocolException>(
            () => WorkerProtocol.Decode(frame));

        Assert.Equal(detailCode, exception.DetailCode);
    }

    public static IEnumerable<object[]> InvalidFrames()
    {
        var valid = Encoding.UTF8.GetString(WorkerProtocol.Encode(
            Envelope(WorkerMessageKind.Event, null, "valid")));
        yield return new object[]
        {
            new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(valid)).ToArray(),
            "bom_forbidden",
        };
        yield return new object[]
        {
            Encoding.UTF8.GetBytes(valid.Replace(
                "\"protocolVersion\":1",
                "\"protocolVersion\":2",
                StringComparison.Ordinal)),
            "unknown_version",
        };
        yield return new object[]
        {
            Encoding.UTF8.GetBytes(valid.Replace(
                "\"kind\":\"event\"",
                "\"kind\":\"hello\"",
                StringComparison.Ordinal)),
            "unknown_kind",
        };
        yield return new object[]
        {
            Encoding.UTF8.GetBytes(valid.Replace(
                "\"payload\":",
                "\"extra\":true,\"payload\":",
                StringComparison.Ordinal)),
            "unknown_field",
        };
        yield return new object[]
        {
            Encoding.UTF8.GetBytes(valid.Replace(
                "\"kind\":\"event\"",
                "\"kind\":\"event\",\"k\\u0069nd\":\"event\"",
                StringComparison.Ordinal)),
            "duplicate_field",
        };
        yield return new object[]
        {
            Encoding.UTF8.GetBytes(valid.Replace(
                "\"value\":\"valid\"",
                "\"value\":\"valid\",\"value\":\"duplicate\"",
                StringComparison.Ordinal)),
            "duplicate_field",
        };
        yield return new object[]
        {
            BuildExcessDepthFrame(),
            "invalid_json",
        };
        yield return new object[]
        {
            new byte[] { (byte)'{', (byte)'\"', 0xff, (byte)'\"', (byte)':', (byte)'1', (byte)'}' },
            "invalid_json",
        };
        yield return new object[]
        {
            Encoding.UTF8.GetBytes("{}"),
            "missing_field",
        };
    }

    private static WorkerEnvelope Envelope(
        WorkerMessageKind kind,
        long? requestId,
        string value)
    {
        using var payload = JsonDocument.Parse($$"""{"value":{{JsonSerializer.Serialize(value)}}}""");
        return new WorkerEnvelope(
            WorkerProtocol.Version,
            kind,
            BootId,
            requestId,
            payload.RootElement.Clone());
    }

    private static WorkerEnvelope EnvelopeWithNestedPayload(int nestedArrays)
    {
        var json = "{\"value\":" +
            new string('[', nestedArrays) +
            "0" +
            new string(']', nestedArrays) +
            "}";
        using var payload = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { MaxDepth = 128 });
        return new WorkerEnvelope(
            WorkerProtocol.Version,
            WorkerMessageKind.Event,
            BootId,
            null,
            payload.RootElement.Clone());
    }

    private static byte[] Terminate(byte[] frame)
    {
        var terminated = new byte[frame.Length + 1];
        frame.CopyTo(terminated, 0);
        terminated[^1] = (byte)'\n';
        return terminated;
    }

    private static byte[] BuildValidFrame(int encodedBytes)
    {
        var prefix = Encoding.UTF8.GetBytes(
            $"{{\"protocolVersion\":1,\"kind\":\"event\",\"workerBootId\":\"{BootId:D}\",\"payload\":{{\"value\":\"");
        var suffix = Encoding.UTF8.GetBytes("\"}}");
        var fillerBytes = encodedBytes - prefix.Length - suffix.Length;
        Assert.True(fillerBytes >= 0);

        var frame = new byte[encodedBytes];
        prefix.CopyTo(frame, 0);
        frame.AsSpan(prefix.Length, fillerBytes).Fill((byte)'x');
        suffix.CopyTo(frame, prefix.Length + fillerBytes);
        return frame;
    }

    private static byte[] BuildExcessDepthFrame()
    {
        const int nesting = FrozenMaximumJsonDepth + 8;
        var prefix =
            $"{{\"protocolVersion\":1,\"kind\":\"event\",\"workerBootId\":\"{BootId:D}\",\"payload\":{{\"value\":";
        var json = prefix + new string('[', nesting) + "0" + new string(']', nesting) + "}}";
        return Encoding.UTF8.GetBytes(json);
    }

    private sealed class FragmentingReadStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _maximumReadBytes;

        internal FragmentingReadStream(byte[] bytes, int maximumReadBytes)
        {
            _inner = new MemoryStream(bytes);
            _maximumReadBytes = maximumReadBytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(
                buffer[..Math.Min(buffer.Length, _maximumReadBytes)],
                cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, _maximumReadBytes));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class CoordinatedWriteStream : Stream
    {
        private readonly MemoryStream _output = new();
        private readonly TaskCompletionSource _firstWriterEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondWriterEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstWriter =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writerCalls;
        private int _activeWriters;
        private int _maximumConcurrentWriters;

        internal Task FirstWriterEntered => _firstWriterEntered.Task;
        internal Task SecondWriterEntered => _secondWriterEntered.Task;
        internal int MaximumConcurrentWriters => Volatile.Read(ref _maximumConcurrentWriters);

        internal void ReleaseFirstWriter() => _releaseFirstWriter.TrySetResult();

        internal byte[] ToArray()
        {
            lock (_output) return _output.ToArray();
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _writerCalls);
            var active = Interlocked.Increment(ref _activeWriters);
            UpdateMaximum(active);
            try
            {
                if (call == 1)
                {
                    _firstWriterEntered.TrySetResult();
                    await _releaseFirstWriter.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    _secondWriterEntered.TrySetResult();
                }

                lock (_output) _output.Write(buffer.Span);
            }
            finally
            {
                Interlocked.Decrement(ref _activeWriters);
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrentWriters);
                if (candidate <= current) return;
                if (Interlocked.CompareExchange(
                        ref _maximumConcurrentWriters,
                        candidate,
                        current) == current)
                    return;
            }
        }

        public override async ValueTask DisposeAsync()
        {
            await _output.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _output.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ObservingFramePool : ArrayPool<byte>
    {
        private readonly byte[] _sentinel;

        internal ObservingFramePool(string sentinel)
        {
            _sentinel = Encoding.UTF8.GetBytes(sentinel);
        }

        internal bool SawSentinelBeforeReturn { get; private set; }
        internal bool ClearRequested { get; private set; }
        internal bool ReturnedArrayWasCleared { get; private set; }

        public override byte[] Rent(int minimumLength) => new byte[minimumLength];

        public override void Return(byte[] array, bool clearArray = false)
        {
            SawSentinelBeforeReturn = array.AsSpan().IndexOf(_sentinel) >= 0;
            ClearRequested = clearArray;
            if (clearArray) Array.Clear(array);
            ReturnedArrayWasCleared = array.All(value => value == 0);
        }
    }

    private sealed class PartialWriteFailingStream : Stream
    {
        private readonly MemoryStream _output = new();
        private readonly int _bytesBeforeFailure;

        internal PartialWriteFailingStream(int bytesBeforeFailure)
        {
            _bytesBeforeFailure = bytesBeforeFailure;
        }

        internal int WriteCalls { get; private set; }
        internal long BytesWritten => _output.Length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            _output.Write(buffer.Span[..Math.Min(buffer.Length, _bytesBeforeFailure)]);
            throw new IOException("injected partial worker-protocol write");
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await _output.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _output.Dispose();
            base.Dispose(disposing);
        }
    }
}
