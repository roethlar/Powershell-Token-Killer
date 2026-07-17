using System.Buffers;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PtkSharedContracts;

public enum GuardianHostPeer
{
    Guardian,
    Host,
}

public enum GuardianHostMessageKind
{
    Hello,
    Initialize,
    Ready,
    Request,
    Cancel,
    Event,
    Response,
    Shutdown,
}

/// <summary>
/// A parsed guardian/host v1 envelope. Values contains exactly the fields that
/// follow the five common envelope fields; each JsonElement owns its data.
/// </summary>
internal sealed record GuardianHostRawEnvelope
{
    internal GuardianHostRawEnvelope(
        int protocolVersion,
        GuardianHostMessageKind kind,
        Guid guardianBootId,
        Guid hostBootId,
        long hostGeneration,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        ProtocolVersion = protocolVersion;
        Kind = kind;
        GuardianBootId = guardianBootId;
        HostBootId = hostBootId;
        HostGeneration = hostGeneration;
        Values = values;
    }

    public int ProtocolVersion { get; }
    public GuardianHostMessageKind Kind { get; }
    public Guid GuardianBootId { get; }
    public Guid HostBootId { get; }
    public long HostGeneration { get; }
    public IReadOnlyDictionary<string, JsonElement> Values { get; }

    public JsonElement Value(string name) =>
        Values.TryGetValue(name, out var value)
            ? value
            : throw new GuardianHostProtocolException(
                "missing_field",
                $"Private protocol field '{name}' is missing.");
}

public sealed class GuardianHostProtocolException : IOException
{
    internal GuardianHostProtocolException(string detailCode, string message)
        : base(message)
    {
        DetailCode = detailCode;
    }

    internal GuardianHostProtocolException(
        string detailCode,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        DetailCode = detailCode;
    }

    public string DetailCode { get; }
}

/// <summary>
/// Strict bounded framing/envelope codec for the complete frozen guardian/host
/// v1 union. Encode and Decode operate on a frame without its required LF; the
/// reader and writer own that NDJSON terminator.
/// </summary>
internal static class GuardianHostRawProtocol
{
    public const int Version = ContractLimits.GuardianHostProtocolVersion;
    public const int MaximumEncodedFrameBytes = ContractLimits.MaximumEncodedFrameBytes;
    public const int MaximumJsonDepth = ContractLimits.MaximumJsonDepth;

    private static readonly string[] CommonProperties =
    [
        "protocol_version",
        "kind",
        "guardian_boot_id",
        "host_boot_id",
        "host_generation",
    ];

    private static readonly string[] HelloProperties =
    [
        "host_pid",
        "host_executable_sha256",
        "host_build_sha256",
        "public_contract_sha256",
        "configuration_sha256",
        "request_channel_owned",
        "event_channel_owned",
    ];

    private static readonly string[] InitializeProperties =
    [
        "request_id",
        "guardian_protocol_version",
        "host_protocol_version",
        "host_executable_sha256",
        "host_build_sha256",
        "public_contract_sha256",
        "configuration_sha256",
        "package_manifest_sha256",
        "maximum_manifest_bytes",
        "maximum_manifest_chunk_raw_bytes",
        "maximum_aliases",
        "maximum_templates",
    ];

    private static readonly string[] ReadyProperties =
    [
        "initialize_request_id",
        "manifest_id",
        "manifest_sha256",
        "host_pid",
    ];

    private static readonly string[] RequestProperties =
    [
        "request_id",
        "method",
        "deadline_unix_time_milliseconds",
        "session_alias",
        "session_transition_version",
        "worker_boot_id",
        "worker_generation",
        "plan_id",
        "operation_id",
        "payload",
    ];

    private static readonly string[] CancelProperties =
    [
        "request_id",
        "target_request_id",
        "reason",
    ];

    private static readonly string[] EventProperties =
    [
        "event_sequence",
        "event_type",
        "request_id",
        "session_alias",
        "session_transition_version",
        "worker_boot_id",
        "worker_generation",
        "plan_id",
        "operation_id",
        "payload",
    ];

    private static readonly string[] ResponseProperties =
    [
        "request_id",
        "status",
        "payload",
        "error",
    ];

    private static readonly string[] ShutdownProperties =
    [
        "request_id",
        "deadline_unix_time_milliseconds",
        "reason",
    ];

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaximumJsonDepth,
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        MaxDepth = MaximumJsonDepth,
    };

    internal static GuardianHostRawEnvelope Create(
        GuardianHostMessageKind kind,
        Guid guardianBootId,
        Guid hostBootId,
        long hostGeneration,
        params (string Name, object? Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (string.IsNullOrEmpty(name) || !fields.TryAdd(name, ToElement(value)))
            {
                throw new GuardianHostProtocolException(
                    "duplicate_field",
                    $"Private protocol field '{name}' is invalid or duplicated.");
            }
        }

        return new GuardianHostRawEnvelope(
            Version,
            kind,
            guardianBootId,
            hostBootId,
            hostGeneration,
            new ReadOnlyDictionary<string, JsonElement>(fields));
    }

    internal static byte[] Encode(GuardianHostRawEnvelope envelope, GuardianHostPeer sender)
    {
        ValidateEnvelope(envelope, sender);

        using var buffer = new BoundedProtocolBuffer(MaximumEncodedFrameBytes);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocol_version", envelope.ProtocolVersion);
            writer.WriteString("kind", ToWireName(envelope.Kind));
            writer.WriteString("guardian_boot_id", envelope.GuardianBootId.ToString("D"));
            writer.WriteString("host_boot_id", envelope.HostBootId.ToString("D"));
            writer.WriteNumber("host_generation", envelope.HostGeneration);
            foreach (var name in PropertiesFor(envelope.Kind))
            {
                writer.WritePropertyName(name);
                envelope.Values[name].WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        try
        {
            using var depthCheck = JsonDocument.Parse(buffer.WrittenMemory, DocumentOptions);
            GuardianHostProtocolSchema.Validate(depthCheck.RootElement);
        }
        catch (JsonException exception)
        {
            throw new GuardianHostProtocolException(
                "invalid_json",
                $"Private protocol envelope exceeds maximum JSON depth {MaximumJsonDepth}.",
                exception);
        }

        return buffer.ToArray();
    }

    internal static GuardianHostRawEnvelope Decode(
        ReadOnlyMemory<byte> encodedFrame,
        GuardianHostPeer sender)
    {
        var bytes = encodedFrame.Span;
        if (bytes.Length == 0)
            throw new GuardianHostProtocolException("empty_frame", "Private protocol frames cannot be empty.");
        if (bytes.Length > MaximumEncodedFrameBytes)
            throw FrameTooLarge();
        if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
        {
            throw new GuardianHostProtocolException(
                "bom_forbidden",
                "Private protocol frames must be UTF-8 without a BOM.");
        }
        if (bytes.IndexOf((byte)'\r') >= 0 || bytes.IndexOf((byte)'\n') >= 0)
        {
            throw new GuardianHostProtocolException(
                "invalid_framing",
                "A private protocol encoded frame cannot contain a raw CR or LF.");
        }

        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new GuardianHostProtocolException(
                "invalid_utf8",
                "Private protocol frame is not strict UTF-8.",
                exception);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(encodedFrame, DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new GuardianHostProtocolException(
                "invalid_json",
                "Private protocol frame is not valid JSON.",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new GuardianHostProtocolException(
                    "invalid_envelope",
                    "Private protocol envelope must be a JSON object.");
            }

            RejectDuplicateProperties(root, containerDepth: 1);

            var rootProperties = root.EnumerateObject().ToArray();
            if (rootProperties.Length < CommonProperties.Length ||
                !string.Equals(rootProperties[0].Name, CommonProperties[0], StringComparison.Ordinal) ||
                !string.Equals(rootProperties[1].Name, CommonProperties[1], StringComparison.Ordinal))
            {
                throw PropertyOrderError();
            }

            var kind = ParseKind(rootProperties[1].Value);
            var kindProperties = PropertiesFor(kind);
            var expectedCount = CommonProperties.Length + kindProperties.Length;
            if (rootProperties.Length != expectedCount)
                throw PropertyOrderError();

            for (var index = 0; index < rootProperties.Length; index++)
            {
                var expected = index < CommonProperties.Length
                    ? CommonProperties[index]
                    : kindProperties[index - CommonProperties.Length];
                if (!string.Equals(rootProperties[index].Name, expected, StringComparison.Ordinal))
                    throw PropertyOrderError();
            }

            var protocolVersion = RequireInt32(rootProperties[0].Value, "protocol_version");
            if (protocolVersion != Version)
            {
                throw new GuardianHostProtocolException(
                    "unknown_version",
                    $"Private protocol version {protocolVersion} is not supported.");
            }

            GuardianHostProtocolSchema.Validate(root);

            var fields = new Dictionary<string, JsonElement>(kindProperties.Length, StringComparer.Ordinal);
            for (var index = 0; index < kindProperties.Length; index++)
            {
                fields.Add(
                    kindProperties[index],
                    rootProperties[index + CommonProperties.Length].Value.Clone());
            }

            var envelope = new GuardianHostRawEnvelope(
                protocolVersion,
                kind,
                RequireUuid(rootProperties[2].Value, "guardian_boot_id"),
                RequireUuid(rootProperties[3].Value, "host_boot_id"),
                RequirePositiveInt64(rootProperties[4].Value, "host_generation"),
                new ReadOnlyDictionary<string, JsonElement>(fields));
            ValidateEnvelope(envelope, sender);
            return envelope;
        }
    }

    /// <summary>
    /// Stateless one-frame read that never consumes bytes after the LF. For a
    /// stream carrying multiple frames, GuardianHostRawProtocolReader is faster
    /// because it retains bounded transport read-ahead.
    /// </summary>
    internal static async ValueTask<GuardianHostRawEnvelope?> ReadAsync(
        Stream stream,
        GuardianHostPeer sender,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));

        var frame = ArrayPool<byte>.Shared.Rent(MaximumEncodedFrameBytes + 1);
        try
        {
            var length = 0;
            while (true)
            {
                var read = await stream.ReadAsync(
                    frame.AsMemory(length, 1),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (length == 0) return null;
                    throw TruncatedFrame();
                }

                if (frame[length] == (byte)'\n')
                    return Decode(new ReadOnlyMemory<byte>(frame, 0, length), sender);

                length++;
                if (length > MaximumEncodedFrameBytes)
                    throw FrameTooLarge();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame, clearArray: true);
        }
    }

    private static void ValidateEnvelope(GuardianHostRawEnvelope envelope, GuardianHostPeer sender)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!Enum.IsDefined(sender))
            throw new ArgumentOutOfRangeException(nameof(sender));
        if (envelope.ProtocolVersion != Version)
        {
            throw new GuardianHostProtocolException(
                "unknown_version",
                $"Private protocol version {envelope.ProtocolVersion} is not supported.");
        }
        if (!Enum.IsDefined(envelope.Kind))
            throw new GuardianHostProtocolException("unknown_kind", "Private protocol kind is unknown.");
        ValidateUuid(envelope.GuardianBootId, "guardian_boot_id");
        ValidateUuid(envelope.HostBootId, "host_boot_id");
        if (envelope.HostGeneration <= 0)
            throw InvalidField("host_generation");
        if (ExpectedSender(envelope.Kind) != sender)
        {
            throw new GuardianHostProtocolException(
                "wrong_direction",
                $"Private protocol kind '{ToWireName(envelope.Kind)}' cannot be sent by {sender}.");
        }

        ArgumentNullException.ThrowIfNull(envelope.Values);
        var expectedProperties = PropertiesFor(envelope.Kind);
        if (envelope.Values.Count != expectedProperties.Length ||
            expectedProperties.Any(name => !envelope.Values.ContainsKey(name)) ||
            envelope.Values.Keys.Any(name => !expectedProperties.Contains(name, StringComparer.Ordinal)))
        {
            throw new GuardianHostProtocolException(
                "invalid_fields",
                "Private protocol envelope fields do not exactly match its kind.");
        }

        foreach (var value in envelope.Values.Values)
            RejectDuplicateProperties(value, containerDepth: 2);

    }

    private static JsonElement ToElement(object? value)
    {
        if (value is JsonElement element) return element.Clone();
        return JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), SerializerOptions);
    }

    private static string[] PropertiesFor(GuardianHostMessageKind kind) => kind switch
    {
        GuardianHostMessageKind.Hello => HelloProperties,
        GuardianHostMessageKind.Initialize => InitializeProperties,
        GuardianHostMessageKind.Ready => ReadyProperties,
        GuardianHostMessageKind.Request => RequestProperties,
        GuardianHostMessageKind.Cancel => CancelProperties,
        GuardianHostMessageKind.Event => EventProperties,
        GuardianHostMessageKind.Response => ResponseProperties,
        GuardianHostMessageKind.Shutdown => ShutdownProperties,
        _ => throw new GuardianHostProtocolException("unknown_kind", "Private protocol kind is unknown."),
    };

    private static GuardianHostPeer ExpectedSender(GuardianHostMessageKind kind) => kind switch
    {
        GuardianHostMessageKind.Hello or
        GuardianHostMessageKind.Ready or
        GuardianHostMessageKind.Event or
        GuardianHostMessageKind.Response => GuardianHostPeer.Host,
        GuardianHostMessageKind.Initialize or
        GuardianHostMessageKind.Request or
        GuardianHostMessageKind.Cancel or
        GuardianHostMessageKind.Shutdown => GuardianHostPeer.Guardian,
        _ => throw new GuardianHostProtocolException("unknown_kind", "Private protocol kind is unknown."),
    };

    private static GuardianHostMessageKind ParseKind(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidField("kind");
        return value.GetString() switch
        {
            "hello" => GuardianHostMessageKind.Hello,
            "initialize" => GuardianHostMessageKind.Initialize,
            "ready" => GuardianHostMessageKind.Ready,
            "request" => GuardianHostMessageKind.Request,
            "cancel" => GuardianHostMessageKind.Cancel,
            "event" => GuardianHostMessageKind.Event,
            "response" => GuardianHostMessageKind.Response,
            "shutdown" => GuardianHostMessageKind.Shutdown,
            _ => throw new GuardianHostProtocolException(
                "unknown_kind",
                "Private protocol kind is missing or unknown."),
        };
    }

    private static string ToWireName(GuardianHostMessageKind kind) => kind switch
    {
        GuardianHostMessageKind.Hello => "hello",
        GuardianHostMessageKind.Initialize => "initialize",
        GuardianHostMessageKind.Ready => "ready",
        GuardianHostMessageKind.Request => "request",
        GuardianHostMessageKind.Cancel => "cancel",
        GuardianHostMessageKind.Event => "event",
        GuardianHostMessageKind.Response => "response",
        GuardianHostMessageKind.Shutdown => "shutdown",
        _ => throw new GuardianHostProtocolException("unknown_kind", "Private protocol kind is unknown."),
    };

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
                        throw new GuardianHostProtocolException(
                            "duplicate_field",
                            $"Private protocol JSON contains duplicate field '{property.Name}'.");
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

    private static void RejectExcessDepth(int depth)
    {
        if (depth > MaximumJsonDepth)
        {
            throw new GuardianHostProtocolException(
                "invalid_json",
                $"Private protocol envelope exceeds maximum JSON depth {MaximumJsonDepth}.");
        }
    }

    private static string RequireString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String) throw InvalidField(name);
        return value.GetString()!;
    }

    private static int RequireInt32(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out var parsed) ||
            parsed != decimal.Truncate(parsed) ||
            parsed < int.MinValue ||
            parsed > int.MaxValue)
            throw InvalidField(name);
        return decimal.ToInt32(parsed);
    }

    private static long RequirePositiveInt64(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out var parsed) ||
            parsed != decimal.Truncate(parsed) ||
            parsed <= 0 ||
            parsed > long.MaxValue)
        {
            throw InvalidField(name);
        }
        return decimal.ToInt64(parsed);
    }

    private static Guid RequireUuid(JsonElement value, string name)
    {
        var text = RequireString(value, name);
        if (!Guid.TryParseExact(text, "D", out var parsed) ||
            !string.Equals(text, parsed.ToString("D"), StringComparison.Ordinal) ||
            text[14] != '4' ||
            text[19] is not ('8' or '9' or 'a' or 'b'))
        {
            throw InvalidField(name);
        }
        return parsed;
    }

    private static void ValidateUuid(Guid value, string name)
    {
        var text = value.ToString("D");
        if (text[14] != '4' || text[19] is not ('8' or '9' or 'a' or 'b'))
            throw InvalidField(name);
    }

    private static GuardianHostProtocolException InvalidField(string name) =>
        new("invalid_field", $"Private protocol field '{name}' is invalid.");

    private static GuardianHostProtocolException PropertyOrderError() =>
        new(
            "property_order",
            "Private protocol fields are missing, unknown, or out of order.");

    private static GuardianHostProtocolException FrameTooLarge() =>
        new(
            "frame_too_large",
            $"Private protocol frame exceeds {MaximumEncodedFrameBytes} encoded bytes.");

    private static GuardianHostProtocolException TruncatedFrame() =>
        new(
            "truncated_frame",
            "Private protocol stream ended before the required LF terminator.");

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

        internal ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

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
            return _buffer.AsSpan(0, _written).ToArray();
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
/// Incremental bounded reader that preserves coalesced-frame read-ahead and
/// returns every pooled frame buffer with clearing enabled.
/// </summary>
internal sealed class GuardianHostRawProtocolReader
{
    private const int TransportBufferBytes = 16 * 1024;

    private readonly Stream _stream;
    private readonly GuardianHostPeer _sender;
    private readonly ArrayPool<byte> _framePool;
    private readonly byte[] _transportBuffer = new byte[TransportBufferBytes];
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private int _transportOffset;
    private int _transportLength;

    internal GuardianHostRawProtocolReader(
        Stream stream,
        GuardianHostPeer sender,
        ArrayPool<byte>? framePool = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        if (!Enum.IsDefined(sender)) throw new ArgumentOutOfRangeException(nameof(sender));
        _stream = stream;
        _sender = sender;
        _framePool = framePool ?? ArrayPool<byte>.Shared;
    }

    internal async ValueTask<GuardianHostRawEnvelope?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var frame = _framePool.Rent(GuardianHostRawProtocol.MaximumEncodedFrameBytes);
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
                        throw new GuardianHostProtocolException(
                            "truncated_frame",
                            "Private protocol stream ended before the required LF terminator.");
                    }
                }

                var available = _transportBuffer.AsSpan(
                    _transportOffset,
                    _transportLength - _transportOffset);
                var newlineOffset = available.IndexOf((byte)'\n');
                var payloadBytes = newlineOffset >= 0 ? newlineOffset : available.Length;
                if (payloadBytes > GuardianHostRawProtocol.MaximumEncodedFrameBytes - frameLength)
                {
                    throw new GuardianHostProtocolException(
                        "frame_too_large",
                        $"Private protocol frame exceeds {GuardianHostRawProtocol.MaximumEncodedFrameBytes} encoded bytes.");
                }

                available[..payloadBytes].CopyTo(frame.AsSpan(frameLength));
                frameLength += payloadBytes;
                _transportOffset += payloadBytes;
                if (newlineOffset < 0) continue;

                _transportOffset++;
                return GuardianHostRawProtocol.Decode(
                    new ReadOnlyMemory<byte>(frame, 0, frameLength),
                    _sender);
            }
        }
        finally
        {
            _framePool.Return(frame, clearArray: true);
            _readGate.Release();
        }
    }
}

/// <summary>Serializes complete LF-terminated frames and latches ambiguous write failure.</summary>
internal sealed class GuardianHostRawProtocolWriter
{
    private readonly Stream _stream;
    private readonly GuardianHostPeer _sender;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Exception? _terminalFailure;

    internal GuardianHostRawProtocolWriter(Stream stream, GuardianHostPeer sender)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
        if (!Enum.IsDefined(sender)) throw new ArgumentOutOfRangeException(nameof(sender));
        _stream = stream;
        _sender = sender;
    }

    internal async ValueTask WriteAsync(
        GuardianHostRawEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        await WriteCoreAsync(
            envelope,
            encodedFrame: null,
            failAfterFirstByte: false,
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask WritePrefixThenFailAsync(
        GuardianHostRawEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        await WriteCoreAsync(
            envelope,
            encodedFrame: null,
            failAfterFirstByte: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates a complete encoded frame, then preserves its exact bytes and
    /// appends only the contract LF. This is used by the fixture to prove that
    /// a noncanonical but valid response payload survives decoding unchanged.
    /// </summary>
    internal async ValueTask WriteRawAsync(
        ReadOnlyMemory<byte> encodedFrame,
        CancellationToken cancellationToken = default)
    {
        var ownedFrame = encodedFrame.ToArray();
        await WriteCoreAsync(
            envelope: null,
            ownedFrame,
            failAfterFirstByte: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteCoreAsync(
        GuardianHostRawEnvelope? envelope,
        byte[]? encodedFrame,
        bool failAfterFirstByte,
        CancellationToken cancellationToken)
    {
        var gateAcquired = false;
        byte[]? encoded = encodedFrame;
        byte[]? frame = null;
        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
            if (_terminalFailure is { } terminalFailure)
            {
                throw new GuardianHostProtocolException(
                    "writer_faulted",
                    "Private protocol writer is unusable after a prior transport failure.",
                    terminalFailure);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (encoded is null)
            {
                encoded = GuardianHostRawProtocol.Encode(
                    envelope ?? throw new ArgumentNullException(nameof(envelope)),
                    _sender);
            }
            else
            {
                _ = GuardianHostRawProtocol.Decode(encoded, _sender);
            }
            frame = GC.AllocateUninitializedArray<byte>(encoded.Length + 1);
            encoded.CopyTo(frame, 0);
            frame[^1] = (byte)'\n';

            try
            {
                if (failAfterFirstByte)
                {
                    await _stream.WriteAsync(frame.AsMemory(0, 1), cancellationToken)
                        .ConfigureAwait(false);
                    await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    throw new IOException(
                        "Injected ambiguous private writer failure after one byte.");
                }
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
