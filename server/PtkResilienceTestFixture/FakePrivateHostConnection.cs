using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PtkSharedContracts;

namespace PtkResilienceTestFixture;

/// <summary>
/// Guardian-side connection to one disposable fake host generation. The
/// synchronous initialization handshake owns stdout until Ready; afterward
/// exactly one background reader dispatches operational responses.
/// </summary>
internal sealed class FakePrivateHostConnection : IDisposable
{
    private const int MaximumManifestBytes = 25_165_824;
    private const int MaximumManifestChunkRawBytes = 524_288;
    private const int MaximumAliases = 128;
    private const int MaximumTemplates = 128;
    private const int MaximumDiagnosticBytes = 65_536;

    private static readonly UTF8Encoding DiagnosticEncoding = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    private readonly Guid _guardianBootId;
    private readonly StreamWriter _standardInput;
    private readonly StreamReader _standardOutput;
    private readonly StreamReader _standardError;
    private readonly GuardianHostRawProtocolReader _reader;
    private readonly GuardianHostRawProtocolWriter _writer;
    private readonly object _responseSync = new();
    private readonly Dictionary<long, TaskCompletionSource<GuardianHostRawEnvelope>> _pendingResponses = [];
    private readonly object _diagnosticSync = new();

    private Guid _hostBootId;
    private long _lastGuardianRequestId;
    private Exception? _generationFailure;
    private string _diagnostic = string.Empty;
    private string _diagnosticStatus = "draining";
    private int _initializeStarted;
    private int _initializeCompleted;
    private int _disposed;

    internal FakePrivateHostConnection(
        Process process,
        long generation,
        Guid guardianBootId)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (guardianBootId == Guid.Empty) throw new ArgumentException(
            "Guardian boot identity cannot be empty.",
            nameof(guardianBootId));

        Process = process;
        Generation = generation;
        GuardianBootId = guardianBootId;
        _guardianBootId = guardianBootId;
        StartTimeUtc = process.StartTime.ToUniversalTime();

        _standardInput = process.StandardInput;
        _standardOutput = process.StandardOutput;
        _standardError = process.StandardError;
        _reader = new GuardianHostRawProtocolReader(
            _standardOutput.BaseStream,
            GuardianHostPeer.Host);
        _writer = new GuardianHostRawProtocolWriter(
            _standardInput.BaseStream,
            GuardianHostPeer.Guardian);

        ResponseReaderTask = Task.CompletedTask;
        DiagnosticTask = DrainStandardErrorAsync();
    }

    internal Process Process { get; }

    internal long Generation { get; }

    internal DateTime StartTimeUtc { get; }

    internal Guid GuardianBootId { get; }

    internal Guid HostBootId
    {
        get
        {
            if (_hostBootId == Guid.Empty)
                throw new InvalidOperationException("The private host has not completed hello.");
            return _hostBootId;
        }
    }

    /// <summary>The guardian's process-lifecycle observer for this generation.</summary>
    internal Task? Observer { get; set; }

    internal Task ResponseReaderTask { get; private set; }

    internal Task ResponseReader => ResponseReaderTask;

    internal Task DiagnosticTask { get; }

    internal string Diagnostic
    {
        get
        {
            lock (_diagnosticSync)
                return _diagnostic;
        }
    }

    internal string DiagnosticStatus
    {
        get
        {
            lock (_diagnosticSync)
                return _diagnosticStatus;
        }
    }

    internal Exception? GenerationFailure
    {
        get
        {
            lock (_responseSync)
                return _generationFailure;
        }
    }

    internal async Task InitializeAsync(
        Func<long> nextGuardianRequestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nextGuardianRequestId);
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _initializeStarted, 1, 0) != 0)
            throw new InvalidOperationException("The private host connection may initialize only once.");

        byte[]? manifest = null;
        try
        {
            var hello = await ReadRequiredAsync("hello", cancellationToken).ConfigureAwait(false);
            ValidateHello(hello);
            _hostBootId = hello.HostBootId;

            var initializeRequestId = NextRequestId(nextGuardianRequestId);
            await _writer.WriteAsync(GuardianHostRawProtocol.Create(
                GuardianHostMessageKind.Initialize,
                _guardianBootId,
                _hostBootId,
                Generation,
                ("request_id", initializeRequestId),
                ("guardian_protocol_version", GuardianHostRawProtocol.Version),
                ("host_protocol_version", GuardianHostRawProtocol.Version),
                ("host_executable_sha256", FakePrivateFixtureIdentity.HostExecutableSha256),
                ("host_build_sha256", FakePrivateFixtureIdentity.HostBuildSha256),
                ("public_contract_sha256", FakePrivateFixtureIdentity.PublicContractSha256),
                ("configuration_sha256", FakePrivateFixtureIdentity.ConfigurationSha256),
                ("package_manifest_sha256", FakePrivateFixtureIdentity.PackageManifestSha256),
                ("maximum_manifest_bytes", MaximumManifestBytes),
                ("maximum_manifest_chunk_raw_bytes", MaximumManifestChunkRawBytes),
                ("maximum_aliases", MaximumAliases),
                ("maximum_templates", MaximumTemplates)), cancellationToken).ConfigureAwait(false);

            manifest = BuildManifest();
            if (manifest.Length is <= 0 or > MaximumManifestChunkRawBytes)
            {
                throw ProtocolFailure(
                    "invalid_fixture_manifest_size",
                    "The fake recovery manifest must fit in exactly one nonempty raw chunk.");
            }
            var manifestSha256 = Sha256Hex(manifest);
            var manifestId = Guid.NewGuid();

            var headerRequestId = NextRequestId(nextGuardianRequestId);
            await WriteManifestRequestAsync(
                headerRequestId,
                "manifest_header",
                CreateObject(writer =>
                {
                    writer.WriteString("manifest_id", manifestId.ToString("D"));
                    writer.WriteNumber("total_bytes", manifest.Length);
                    writer.WriteNumber("chunk_count", 1);
                    writer.WriteString("manifest_sha256", manifestSha256);
                    writer.WriteNumber("alias_count", 1);
                    writer.WriteNumber("template_count", 0);
                }),
                cancellationToken).ConfigureAwait(false);
            var headerAccepted = await ReadExpectedOkPayloadAsync(
                headerRequestId,
                cancellationToken).ConfigureAwait(false);
            if (headerAccepted.GetProperty("response_type").GetString() !=
                    "manifest_header_accepted" ||
                headerAccepted.GetProperty("manifest_id").GetGuid() != manifestId ||
                headerAccepted.GetProperty("next_chunk_index").GetInt32() != 0 ||
                headerAccepted.GetProperty("next_offset").GetInt32() != 0)
            {
                throw ProtocolFailure(
                    "invalid_handshake_response",
                    "The private host did not accept the exact manifest header.");
            }

            var chunkRequestId = NextRequestId(nextGuardianRequestId);
            await WriteManifestRequestAsync(
                chunkRequestId,
                "manifest_chunk",
                CreateObject(writer =>
                {
                    writer.WriteString("manifest_id", manifestId.ToString("D"));
                    writer.WriteNumber("chunk_index", 0);
                    writer.WriteNumber("offset", 0);
                    writer.WriteNumber("raw_bytes", manifest.Length);
                    writer.WriteString("raw_base64", Convert.ToBase64String(manifest));
                    writer.WriteString("raw_sha256", manifestSha256);
                }),
                cancellationToken).ConfigureAwait(false);
            var chunkAccepted = await ReadExpectedOkPayloadAsync(
                chunkRequestId,
                cancellationToken).ConfigureAwait(false);
            if (chunkAccepted.GetProperty("response_type").GetString() !=
                    "manifest_chunk_accepted" ||
                chunkAccepted.GetProperty("manifest_id").GetGuid() != manifestId ||
                chunkAccepted.GetProperty("chunk_index").GetInt32() != 0 ||
                chunkAccepted.GetProperty("next_chunk_index").GetInt32() != 1 ||
                chunkAccepted.GetProperty("next_offset").GetInt32() != manifest.Length)
            {
                throw ProtocolFailure(
                    "invalid_handshake_response",
                    "The private host did not accept the exact manifest chunk.");
            }

            var sealRequestId = NextRequestId(nextGuardianRequestId);
            await WriteManifestRequestAsync(
                sealRequestId,
                "manifest_seal",
                CreateObject(writer =>
                {
                    writer.WriteString("manifest_id", manifestId.ToString("D"));
                    writer.WriteNumber("total_bytes", manifest.Length);
                    writer.WriteNumber("chunk_count", 1);
                    writer.WriteString("manifest_sha256", manifestSha256);
                }),
                cancellationToken).ConfigureAwait(false);
            var sealedManifest = await ReadExpectedOkPayloadAsync(
                sealRequestId,
                cancellationToken).ConfigureAwait(false);
            if (sealedManifest.GetProperty("response_type").GetString() != "manifest_sealed" ||
                sealedManifest.GetProperty("manifest_id").GetGuid() != manifestId ||
                !string.Equals(
                    sealedManifest.GetProperty("manifest_sha256").GetString(),
                    manifestSha256,
                    StringComparison.Ordinal) ||
                sealedManifest.GetProperty("total_bytes").GetInt32() != manifest.Length)
            {
                throw ProtocolFailure(
                    "invalid_handshake_response",
                    "The private host did not seal the exact recovery manifest.");
            }

            var ready = await ReadRequiredAsync("ready", cancellationToken).ConfigureAwait(false);
            ValidateIdentity(ready);
            if (ready.Kind != GuardianHostMessageKind.Ready ||
                ready.Value("initialize_request_id").GetInt64() != initializeRequestId ||
                ready.Value("manifest_id").GetGuid() != manifestId ||
                !string.Equals(
                    ready.Value("manifest_sha256").GetString(),
                    manifestSha256,
                    StringComparison.Ordinal) ||
                ready.Value("host_pid").GetInt32() != Process.Id)
            {
                throw ProtocolFailure(
                    "invalid_ready",
                    "The private host ready frame did not commit the exact initialized generation.");
            }

            ResponseReaderTask = ReadOperationalResponsesAsync();
            Volatile.Write(ref _initializeCompleted, 1);
        }
        catch (Exception exception)
        {
            FailGeneration(exception);
            throw;
        }
        finally
        {
            if (manifest is not null)
                CryptographicOperations.ZeroMemory(manifest);
        }
    }

    internal Task<GuardianHostRawEnvelope> RegisterResponse(long requestId)
    {
        ThrowIfDisposed();
        if (requestId <= 0) throw new ArgumentOutOfRangeException(nameof(requestId));

        lock (_responseSync)
        {
            if (_generationFailure is { } failure)
                return Task.FromException<GuardianHostRawEnvelope>(failure);
            if (Volatile.Read(ref _initializeCompleted) == 0)
            {
                throw new InvalidOperationException("The private host is not initialized.");
            }
            if (requestId <= _lastGuardianRequestId ||
                _pendingResponses.ContainsKey(requestId))
            {
                throw new InvalidOperationException(
                    "Operational private request identifiers must be unique and monotonically increasing.");
            }

            _lastGuardianRequestId = requestId;
            var completion = new TaskCompletionSource<GuardianHostRawEnvelope>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingResponses.Add(requestId, completion);
            return completion.Task;
        }
    }

    internal bool CancelResponse(long requestId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (requestId <= 0) throw new ArgumentOutOfRangeException(nameof(requestId));

        TaskCompletionSource<GuardianHostRawEnvelope>? completion;
        lock (_responseSync)
        {
            if (!_pendingResponses.Remove(requestId, out completion))
                return false;
        }

        return completion.TrySetCanceled(cancellationToken);
    }

    /// <summary>
    /// Writes one strict guardian frame. Callers register its response and
    /// advance their delivery truth before invoking this first possibly-writing API.
    /// </summary>
    internal async ValueTask WriteAsync(
        GuardianHostRawEnvelope envelope,
        bool injectWriterFailure = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _initializeCompleted) == 0)
            throw new InvalidOperationException("The private host is not initialized.");
        if (envelope.GuardianBootId != _guardianBootId ||
            envelope.HostBootId != _hostBootId ||
            envelope.HostGeneration != Generation)
        {
            throw ProtocolFailure(
                "wrong_generation",
                "An outbound private frame did not match this exact host generation.");
        }
        lock (_responseSync)
        {
            if (_generationFailure is { } failure)
            {
                throw ProtocolFailure(
                    "generation_faulted",
                    "The private host generation is already unusable.",
                    failure);
            }
            if (envelope.Kind == GuardianHostMessageKind.Request &&
                !_pendingResponses.ContainsKey(envelope.Value("request_id").GetInt64()))
            {
                throw new InvalidOperationException(
                    "Register the operational response before the first API that may write a private byte.");
            }
        }
        try
        {
            if (injectWriterFailure)
            {
                await _writer.WritePrefixThenFailAsync(envelope, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // Any failure from the first possibly-writing API makes this
            // transport and generation unusable. The shared loss transition
            // observes the resulting process death exactly once.
            FailGeneration(exception);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

        if (!ResponseReaderTask.IsCompleted ||
            !DiagnosticTask.IsCompleted ||
            Observer is { IsCompleted: false } ||
            !Process.HasExited)
        {
            Volatile.Write(ref _disposed, 0);
            throw new InvalidOperationException(
                "Await the response reader, diagnostic drain, and process observer before disposing the host connection.");
        }

        _standardInput.Dispose();
        _standardOutput.Dispose();
        _standardError.Dispose();
        Process.Dispose();
    }

    private async Task WriteManifestRequestAsync(
        long requestId,
        string method,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        await _writer.WriteAsync(GuardianHostRawProtocol.Create(
            GuardianHostMessageKind.Request,
            _guardianBootId,
            _hostBootId,
            Generation,
            ("request_id", requestId),
            ("method", method),
            ("deadline_unix_time_milliseconds", null),
            ("session_alias", null),
            ("session_transition_version", null),
            ("worker_boot_id", null),
            ("worker_generation", null),
            ("plan_id", null),
            ("operation_id", null),
            ("payload", payload)), cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> ReadExpectedOkPayloadAsync(
        long requestId,
        CancellationToken cancellationToken)
    {
        var response = await ReadRequiredAsync("response", cancellationToken).ConfigureAwait(false);
        ValidateIdentity(response);
        if (response.Kind != GuardianHostMessageKind.Response ||
            response.Value("request_id").GetInt64() != requestId ||
            response.Value("status").GetString() != "ok" ||
            response.Value("error").ValueKind != JsonValueKind.Null ||
            response.Value("payload").ValueKind != JsonValueKind.Object)
        {
            throw ProtocolFailure(
                "invalid_handshake_response",
                "The private host did not acknowledge the exact manifest request.");
        }
        return response.Value("payload");
    }

    private async Task<GuardianHostRawEnvelope> ReadRequiredAsync(
        string expectedKind,
        CancellationToken cancellationToken)
    {
        var envelope = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return envelope ?? throw ProtocolFailure(
            "unexpected_eof",
            $"The private host closed before its required {expectedKind} frame.");
    }

    private void ValidateHello(GuardianHostRawEnvelope hello)
    {
        if (hello.Kind != GuardianHostMessageKind.Hello ||
            hello.GuardianBootId != _guardianBootId ||
            hello.HostGeneration != Generation ||
            hello.HostBootId == Guid.Empty ||
            hello.Value("host_pid").GetInt32() != Process.Id ||
            hello.Value("host_executable_sha256").GetString() !=
                FakePrivateFixtureIdentity.HostExecutableSha256 ||
            hello.Value("host_build_sha256").GetString() !=
                FakePrivateFixtureIdentity.HostBuildSha256 ||
            hello.Value("public_contract_sha256").GetString() !=
                FakePrivateFixtureIdentity.PublicContractSha256 ||
            hello.Value("configuration_sha256").GetString() !=
                FakePrivateFixtureIdentity.ConfigurationSha256 ||
            !hello.Value("request_channel_owned").GetBoolean() ||
            !hello.Value("event_channel_owned").GetBoolean())
        {
            throw ProtocolFailure(
                "invalid_hello",
                "The private host hello did not prove the expected executable, contract, configuration, and generation.");
        }
    }

    private void ValidateIdentity(GuardianHostRawEnvelope envelope)
    {
        if (envelope.GuardianBootId != _guardianBootId ||
            envelope.HostBootId != _hostBootId ||
            envelope.HostGeneration != Generation)
        {
            throw ProtocolFailure(
                "wrong_generation",
                "A private host frame did not match this exact boot and generation identity.");
        }
    }

    private async Task ReadOperationalResponsesAsync()
    {
        try
        {
            while (true)
            {
                var response = await _reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                if (response is null)
                {
                    throw ProtocolFailure(
                        "unexpected_eof",
                        "The private host response stream ended.");
                }

                ValidateIdentity(response);
                if (response.Kind != GuardianHostMessageKind.Response)
                {
                    throw ProtocolFailure(
                        "unexpected_kind",
                        "The operational private stream accepted only response frames.");
                }

                var requestId = response.Value("request_id").GetInt64();
                TaskCompletionSource<GuardianHostRawEnvelope>? completion;
                lock (_responseSync)
                {
                    if (!_pendingResponses.Remove(requestId, out completion))
                    {
                        var detailCode = requestId <= _lastGuardianRequestId
                            ? "duplicate_or_stale_response"
                            : "unknown_response_id";
                        throw ProtocolFailure(
                            detailCode,
                            "The private host returned an unregistered, stale, or duplicate request identifier.");
                    }
                }

                // A fully decoded and identity-checked response is terminal for
                // its caller even if the process exits immediately afterward.
                completion.TrySetResult(response);
            }
        }
        catch (Exception exception)
        {
            FailGeneration(exception);
            throw;
        }
    }

    private void FailGeneration(Exception exception)
    {
        TaskCompletionSource<GuardianHostRawEnvelope>[] pending;
        Exception failure;
        lock (_responseSync)
        {
            if (_generationFailure is null)
            {
                _generationFailure = exception is GuardianHostProtocolException
                    ? exception
                    : ProtocolFailure(
                        "generation_failed",
                        "The private host generation failed.",
                        exception);
            }
            failure = _generationFailure;
            pending = _pendingResponses.Values.ToArray();
            _pendingResponses.Clear();
        }

        foreach (var completion in pending)
            completion.TrySetException(failure);

        if (!Process.HasExited)
        {
            try
            {
                Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The generation exited between the identity check and kill.
            }
        }
    }

    private long NextRequestId(Func<long> nextGuardianRequestId)
    {
        var requestId = nextGuardianRequestId();
        if (requestId <= _lastGuardianRequestId || requestId <= 0)
        {
            throw ProtocolFailure(
                "non_monotonic_request_id",
                "Guardian request identifiers must be positive and strictly monotonic.");
        }
        _lastGuardianRequestId = requestId;
        return requestId;
    }

    private byte[] BuildManifest() => BuildManifestVector(_guardianBootId, Generation);

    internal static byte[] BuildManifestVector(Guid guardianBootId, long generation)
    {
        if (guardianBootId == Guid.Empty) throw new ArgumentException(
            "Guardian boot identity cannot be empty.",
            nameof(guardianBootId));
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", "ptk.recovery-manifest/1");
            writer.WriteString("guardian_boot_id", guardianBootId.ToString("D"));
            writer.WriteNumber("host_generation", generation);
            writer.WriteString("catalog_digest", Sha256Hex("[]"u8));
            writer.WriteString(
                "configuration_sha256",
                FakePrivateFixtureIdentity.ConfigurationSha256);
            writer.WriteStartArray("templates");
            writer.WriteEndArray();
            writer.WriteStartArray("bindings");
            writer.WriteStartObject();
            writer.WriteString("alias", "default");
            writer.WriteString("binding_kind", "default");
            writer.WriteNull("template_name");
            writer.WriteNull("template_digest");
            writer.WriteNull("bootstrap_digest");
            writer.WriteBoolean("allow_cold_background", false);
            writer.WriteString("desired_state", "ready");
            writer.WriteNumber("transition_version", 0);
            writer.WriteString(
                "binding_digest",
                FakePrivateFixtureIdentity.DefaultBindingSha256);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartArray("worker_generation_high_watermarks");
            writer.WriteStartObject();
            writer.WriteString("alias", "default");
            writer.WriteNumber("generation", generation - 1);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteNumber("host_generation_high_watermark", generation);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static JsonElement CreateObject(Action<Utf8JsonWriter> writeProperties)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        var encoded = Convert.ToHexString(digest).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(digest);
        return encoded;
    }

    private async Task DrainStandardErrorAsync()
    {
        var retained = GC.AllocateUninitializedArray<byte>(MaximumDiagnosticBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        var retainedLength = 0;
        long totalLength = 0;
        string status;
        try
        {
            while (true)
            {
                var read = await _standardError.BaseStream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) break;

                totalLength = totalLength > long.MaxValue - read
                    ? long.MaxValue
                    : totalLength + read;
                var copyLength = Math.Min(read, MaximumDiagnosticBytes - retainedLength);
                if (copyLength > 0)
                {
                    buffer.AsSpan(0, copyLength).CopyTo(retained.AsSpan(retainedLength));
                    retainedLength += copyLength;
                }
            }
            status = totalLength > MaximumDiagnosticBytes ? "truncated" : "complete";
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            status = string.Concat("read_failed:", exception.GetType().Name);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        var diagnostic = DiagnosticEncoding.GetString(retained, 0, retainedLength);
        CryptographicOperations.ZeroMemory(retained);
        lock (_diagnosticSync)
        {
            _diagnostic = diagnostic;
            _diagnosticStatus = status;
        }
    }

    private static GuardianHostProtocolException ProtocolFailure(
        string detailCode,
        string message) => new(detailCode, message);

    private static GuardianHostProtocolException ProtocolFailure(
        string detailCode,
        string message,
        Exception innerException) => new(detailCode, message, innerException);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0,
        this);
}
