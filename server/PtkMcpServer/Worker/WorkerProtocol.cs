using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PtkMcpServer.Worker;

internal enum WorkerMessageKind
{
    Initialize,
    Prepare,
    Commit,
    Abort,
    Request,
    Cancel,
    Event,
    Response,
    Shutdown,
}

internal sealed record WorkerEnvelope(
    int ProtocolVersion,
    WorkerMessageKind Kind,
    Guid WorkerBootId,
    long? RequestId,
    JsonElement Payload);

internal sealed class WorkerProtocolException : IOException
{
    internal WorkerProtocolException(string detailCode, string message)
        : base(message)
    {
        DetailCode = detailCode;
    }

    internal WorkerProtocolException(string detailCode, string message, Exception innerException)
        : base(message, innerException)
    {
        DetailCode = detailCode;
    }

    internal string DetailCode { get; }
}

/// <summary>
/// Frozen v1 worker-envelope codec. Frames are strict UTF-8 JSON terminated by
/// one LF. The terminator is outside the encoded-frame limit.
/// </summary>
internal static class WorkerProtocol
{
    internal const int Version = 1;
    internal const int MaximumJsonDepth = 32;
    internal const int MaximumEncodedFrameBytes = 1_048_576;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaximumJsonDepth,
    };

    internal static WorkerEnvelope Decode(ReadOnlyMemory<byte> encodedFrame)
    {
        var bytes = encodedFrame.Span;
        if (bytes.Length > MaximumEncodedFrameBytes)
        {
            throw new WorkerProtocolException(
                "frame_too_large",
                $"Worker protocol frame exceeds {MaximumEncodedFrameBytes} encoded bytes.");
        }

        if (bytes.Length >= 3 &&
            bytes[0] == 0xef &&
            bytes[1] == 0xbb &&
            bytes[2] == 0xbf)
        {
            throw new WorkerProtocolException(
                "bom_forbidden",
                "Worker protocol frames must be UTF-8 without a BOM.");
        }

        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new WorkerProtocolException(
                "invalid_json",
                "Worker protocol frame is not valid strict UTF-8 JSON.",
                exception);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(encodedFrame, DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new WorkerProtocolException(
                "invalid_json",
                "Worker protocol frame is not valid strict UTF-8 JSON.",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new WorkerProtocolException(
                    "invalid_envelope",
                    "Worker protocol envelope must be a JSON object.");
            }

            RejectDuplicateProperties(root, containerDepth: 1);

            int? protocolVersion = null;
            WorkerMessageKind? kind = null;
            Guid? workerBootId = null;
            long? requestId = null;
            var requestIdPresent = false;
            JsonElement? payload = null;

            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "protocolVersion":
                        if (property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetInt32(out var parsedVersion))
                        {
                            throw InvalidField("protocolVersion");
                        }
                        protocolVersion = parsedVersion;
                        break;
                    case "kind":
                        if (property.Value.ValueKind != JsonValueKind.String ||
                            !TryParseKind(property.Value.GetString(), out var parsedKind))
                        {
                            throw new WorkerProtocolException(
                                "unknown_kind",
                                "Worker protocol kind is missing or unknown.");
                        }
                        kind = parsedKind;
                        break;
                    case "workerBootId":
                        if (property.Value.ValueKind != JsonValueKind.String ||
                            !Guid.TryParseExact(property.Value.GetString(), "D", out var parsedBootId) ||
                            parsedBootId == Guid.Empty)
                        {
                            throw InvalidField("workerBootId");
                        }
                        workerBootId = parsedBootId;
                        break;
                    case "requestId":
                        requestIdPresent = true;
                        if (property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetInt64(out var parsedRequestId) ||
                            parsedRequestId <= 0)
                        {
                            throw InvalidField("requestId");
                        }
                        requestId = parsedRequestId;
                        break;
                    case "payload":
                        if (property.Value.ValueKind != JsonValueKind.Object)
                        {
                            throw InvalidField("payload");
                        }
                        payload = property.Value.Clone();
                        break;
                    default:
                        throw new WorkerProtocolException(
                            "unknown_field",
                            $"Worker protocol envelope contains unknown field '{property.Name}'.");
                }
            }

            if (protocolVersion is null || kind is null || workerBootId is null || payload is null)
            {
                throw new WorkerProtocolException(
                    "missing_field",
                    "Worker protocol envelope is missing a required field.");
            }

            if (protocolVersion != Version)
            {
                throw new WorkerProtocolException(
                    "unknown_version",
                    $"Worker protocol version {protocolVersion} is not supported.");
            }

            return new WorkerEnvelope(
                protocolVersion.Value,
                kind.Value,
                workerBootId.Value,
                requestIdPresent ? requestId : null,
                payload.Value);
        }
    }

    internal static byte[] Encode(WorkerEnvelope envelope)
    {
        ValidateEnvelope(envelope);

        using var buffer = new BoundedProtocolBuffer(MaximumEncodedFrameBytes);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", envelope.ProtocolVersion);
            writer.WriteString("kind", ToWireName(envelope.Kind));
            writer.WriteString("workerBootId", envelope.WorkerBootId.ToString("D"));
            if (envelope.RequestId is { } requestId)
                writer.WriteNumber("requestId", requestId);
            writer.WritePropertyName("payload");
            envelope.Payload.WriteTo(writer);
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > MaximumEncodedFrameBytes)
        {
            throw new WorkerProtocolException(
                "frame_too_large",
                $"Worker protocol frame exceeds {MaximumEncodedFrameBytes} encoded bytes.");
        }

        try
        {
            using var depthCheck = JsonDocument.Parse(buffer.WrittenMemory, DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new WorkerProtocolException(
                "invalid_json",
                $"Worker protocol envelope exceeds maximum JSON depth {MaximumJsonDepth}.",
                exception);
        }

        return buffer.ToArray();
    }

    private static void ValidateEnvelope(WorkerEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.ProtocolVersion != Version)
        {
            throw new WorkerProtocolException(
                "unknown_version",
                $"Worker protocol version {envelope.ProtocolVersion} is not supported.");
        }
        if (!Enum.IsDefined(envelope.Kind))
            throw new WorkerProtocolException("unknown_kind", "Worker protocol kind is unknown.");
        if (envelope.WorkerBootId == Guid.Empty)
            throw InvalidField("workerBootId");
        if (envelope.RequestId is <= 0)
            throw InvalidField("requestId");
        if (envelope.Payload.ValueKind != JsonValueKind.Object)
            throw InvalidField("payload");

        // The serialized envelope root is depth 1 and payload is depth 2.
        // Enforce the bound before recursive inspection so an already-built,
        // attacker-influenced JsonElement cannot overflow this process's
        // stack before the post-encode parser sees it.
        RejectDuplicateProperties(envelope.Payload, containerDepth: 2);
    }

    private static void RejectDuplicateProperties(JsonElement element, int containerDepth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                RejectExcessDepth(containerDepth);
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new WorkerProtocolException(
                            "duplicate_field",
                            $"Worker protocol JSON contains duplicate field '{property.Name}'.");
                    }
                    RejectDuplicateProperties(property.Value, containerDepth + 1);
                }
                break;
            case JsonValueKind.Array:
                RejectExcessDepth(containerDepth);
                foreach (var item in element.EnumerateArray())
                    RejectDuplicateProperties(item, containerDepth + 1);
                break;
        }
    }

    private static void RejectExcessDepth(int containerDepth)
    {
        if (containerDepth > MaximumJsonDepth)
        {
            throw new WorkerProtocolException(
                "invalid_json",
                $"Worker protocol envelope exceeds maximum JSON depth {MaximumJsonDepth}.");
        }
    }

    private static WorkerProtocolException InvalidField(string name) =>
        new("invalid_field", $"Worker protocol field '{name}' is invalid.");

    private static bool TryParseKind(string? value, out WorkerMessageKind kind)
    {
        kind = value switch
        {
            "initialize" => WorkerMessageKind.Initialize,
            "prepare" => WorkerMessageKind.Prepare,
            "commit" => WorkerMessageKind.Commit,
            "abort" => WorkerMessageKind.Abort,
            "request" => WorkerMessageKind.Request,
            "cancel" => WorkerMessageKind.Cancel,
            "event" => WorkerMessageKind.Event,
            "response" => WorkerMessageKind.Response,
            "shutdown" => WorkerMessageKind.Shutdown,
            _ => default,
        };
        return value is
            "initialize" or "prepare" or "commit" or "abort" or "request" or
            "cancel" or "event" or "response" or "shutdown";
    }

    private static string ToWireName(WorkerMessageKind kind) => kind switch
    {
        WorkerMessageKind.Initialize => "initialize",
        WorkerMessageKind.Prepare => "prepare",
        WorkerMessageKind.Commit => "commit",
        WorkerMessageKind.Abort => "abort",
        WorkerMessageKind.Request => "request",
        WorkerMessageKind.Cancel => "cancel",
        WorkerMessageKind.Event => "event",
        WorkerMessageKind.Response => "response",
        WorkerMessageKind.Shutdown => "shutdown",
        _ => throw new WorkerProtocolException("unknown_kind", "Worker protocol kind is unknown."),
    };

    private sealed class BoundedProtocolBuffer : IBufferWriter<byte>, IDisposable
    {
        private readonly int _maximumBytes;
        private byte[] _buffer;
        private int _written;
        private bool _disposed;

        internal BoundedProtocolBuffer(int maximumBytes)
        {
            _maximumBytes = maximumBytes;
            _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(256, maximumBytes));
        }

        internal int WrittenCount => _written;
        internal ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);
        internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Advance(int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (count < 0 || count > _maximumBytes - _written || count > _buffer.Length - _written)
                throw FrameTooLarge();
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(
                _written,
                Math.Min(_buffer.Length - _written, _maximumBytes - _written));
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(
                _written,
                Math.Min(_buffer.Length - _written, _maximumBytes - _written));
        }

        internal byte[] ToArray()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return WrittenSpan.ToArray();
        }

        private void EnsureCapacity(int sizeHint)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
            if (sizeHint == 0) sizeHint = 1;
            if (sizeHint > _maximumBytes - _written) throw FrameTooLarge();
            if (sizeHint <= _buffer.Length - _written) return;

            var required = checked(_written + sizeHint);
            var doubled = Math.Min(
                _maximumBytes,
                Math.Max(required, checked(_buffer.Length * 2)));
            var replacement = ArrayPool<byte>.Shared.Rent(doubled);
            _buffer.AsSpan(0, _written).CopyTo(replacement);
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = replacement;
        }

        private static WorkerProtocolException FrameTooLarge() =>
            new(
                "frame_too_large",
                $"Worker protocol frame exceeds {MaximumEncodedFrameBytes} encoded bytes.");

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = Array.Empty<byte>();
            _written = 0;
        }
    }
}

/// <summary>
/// Incremental bounded frame reader. It retains transport read-ahead so
/// coalesced frames are decoded sequentially without an unbounded line read.
/// </summary>
internal sealed class WorkerProtocolReader
{
    private const int TransportBufferBytes = 16 * 1024;

    private readonly Stream _stream;
    private readonly ArrayPool<byte> _framePool;
    private readonly byte[] _transportBuffer = new byte[TransportBufferBytes];
    private int _transportOffset;
    private int _transportLength;

    internal WorkerProtocolReader(Stream stream, ArrayPool<byte>? framePool = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        _stream = stream;
        _framePool = framePool ?? ArrayPool<byte>.Shared;
    }

    internal async ValueTask<WorkerEnvelope?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var frame = _framePool.Rent(WorkerProtocol.MaximumEncodedFrameBytes);
        try
        {
            var frameLength = 0;
            while (true)
            {
                if (_transportOffset == _transportLength)
                {
                    _transportLength = await _stream.ReadAsync(
                        _transportBuffer.AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    _transportOffset = 0;
                    if (_transportLength == 0)
                    {
                        if (frameLength == 0) return null;
                        throw new WorkerProtocolException(
                            "truncated_frame",
                            "Worker protocol stream ended before the LF frame terminator.");
                    }
                }

                var available = _transportBuffer.AsSpan(
                    _transportOffset,
                    _transportLength - _transportOffset);
                var newlineOffset = available.IndexOf((byte)'\n');
                var payloadBytes = newlineOffset >= 0 ? newlineOffset : available.Length;

                if (payloadBytes > WorkerProtocol.MaximumEncodedFrameBytes - frameLength)
                {
                    throw new WorkerProtocolException(
                        "frame_too_large",
                        $"Worker protocol frame exceeds {WorkerProtocol.MaximumEncodedFrameBytes} encoded bytes.");
                }

                available[..payloadBytes].CopyTo(frame.AsSpan(frameLength));
                frameLength += payloadBytes;
                _transportOffset += payloadBytes;

                if (newlineOffset < 0) continue;

                // Consume exactly the LF. Any coalesced bytes stay in the
                // transport buffer for the next ReadAsync call.
                _transportOffset++;
                return WorkerProtocol.Decode(new ReadOnlyMemory<byte>(frame, 0, frameLength));
            }
        }
        finally
        {
            // Frames may contain exact scripts or result data. FullLanguage
            // PowerShell in this process can rent from the shared pool, so a
            // returned protocol buffer must not expose the previous frame.
            _framePool.Return(frame, clearArray: true);
        }
    }
}

/// <summary>Serializes frames through one gate so concurrent writes cannot interleave.</summary>
internal sealed class WorkerProtocolWriter
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Exception? _terminalFailure;

    internal WorkerProtocolWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
        _stream = stream;
    }

    internal async ValueTask WriteAsync(
        WorkerEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var gateAcquired = false;
        byte[]? encoded = null;
        byte[]? frame = null;
        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;

            if (_terminalFailure is { } terminalFailure)
            {
                throw new WorkerProtocolException(
                    "writer_faulted",
                    "Worker protocol writer is unusable after a prior transport failure.",
                    terminalFailure);
            }

            cancellationToken.ThrowIfCancellationRequested();
            encoded = WorkerProtocol.Encode(envelope);
            frame = GC.AllocateUninitializedArray<byte>(encoded.Length + 1);
            encoded.CopyTo(frame, 0);
            frame[^1] = (byte)'\n';

            try
            {
                // Once this call begins the stream may contain a frame prefix
                // even when it later throws or observes cancellation. Latch
                // the writer terminal so no later frame can be concatenated
                // onto ambiguous transport state.
                await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _terminalFailure = exception;
                throw;
            }
        }
        finally
        {
            if (gateAcquired) _writeGate.Release();
            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
            if (frame is not null) CryptographicOperations.ZeroMemory(frame);
        }
    }
}
