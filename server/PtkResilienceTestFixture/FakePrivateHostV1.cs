using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PtkSharedContracts;

namespace PtkResilienceTestFixture;

internal static class FakePrivateFixtureIdentity
{
    internal const string HostExecutableSha256 =
        "1111111111111111111111111111111111111111111111111111111111111111";
    internal const string HostBuildSha256 =
        "2222222222222222222222222222222222222222222222222222222222222222";
    internal const string PublicContractSha256 =
        "3333333333333333333333333333333333333333333333333333333333333333";
    internal const string ConfigurationSha256 =
        "4444444444444444444444444444444444444444444444444444444444444444";
    internal const string PackageManifestSha256 =
        "5555555555555555555555555555555555555555555555555555555555555555";
    internal const string DefaultBindingSha256 =
        "6666666666666666666666666666666666666666666666666666666666666666";
}

/// <summary>
/// Disposable host peer for the R0 guardian fixture. This intentionally has
/// no product references and exists only to exercise the R0 handshake,
/// manifest-transfer, delivery, and guardian generation-failure subset.
/// </summary>
internal static class FakePrivateHostV1
{
    private const int ProtocolViolationExitCode = 65;
    private const int DeliberateStartFailureExitCode = 68;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonDocumentOptions ManifestDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = GuardianHostRawProtocol.MaximumJsonDepth,
    };

    internal static async Task<int> RunAsync(
        string controlRoot,
        long generation,
        Guid guardianBootId)
    {
        var failNextStart = Path.Combine(controlRoot, "fail-next-host-start.json");
        if (File.Exists(failNextStart))
        {
            File.Delete(failNextStart);
            return DeliberateStartFailureExitCode;
        }

        try
        {
            return await RunCoreAsync(controlRoot, generation, guardianBootId)
                .ConfigureAwait(false);
        }
        catch (GuardianHostProtocolException)
        {
            return ProtocolViolationExitCode;
        }
        catch (InvalidDataException)
        {
            return ProtocolViolationExitCode;
        }
        catch (JsonException)
        {
            return ProtocolViolationExitCode;
        }
        catch (DecoderFallbackException)
        {
            return ProtocolViolationExitCode;
        }
        catch (IOException)
        {
            // Closing the private pipes is a normal guardian-owned shutdown.
            return 0;
        }
    }

    private static async Task<int> RunCoreAsync(
        string controlRoot,
        long generation,
        Guid guardianBootId)
    {
        if (generation <= 0)
            return ProtocolViolationExitCode;

        var hostBootId = Guid.NewGuid();
        await using var input = Console.OpenStandardInput();
        await using var output = Console.OpenStandardOutput();
        var reader = new GuardianHostRawProtocolReader(input, GuardianHostPeer.Guardian);
        var writer = new GuardianHostRawProtocolWriter(output, GuardianHostPeer.Host);

        await writer.WriteAsync(GuardianHostRawProtocol.Create(
            GuardianHostMessageKind.Hello,
            guardianBootId,
            hostBootId,
            generation,
            ("host_pid", Environment.ProcessId),
            ("host_executable_sha256", FakePrivateFixtureIdentity.HostExecutableSha256),
            ("host_build_sha256", FakePrivateFixtureIdentity.HostBuildSha256),
            ("public_contract_sha256", FakePrivateFixtureIdentity.PublicContractSha256),
            ("configuration_sha256", FakePrivateFixtureIdentity.ConfigurationSha256),
            ("request_channel_owned", true),
            ("event_channel_owned", true))).ConfigureAwait(false);

        var initialize = await reader.ReadAsync().ConfigureAwait(false);
        if (initialize is null) return 0;
        if (!MatchesIdentity(initialize, guardianBootId, hostBootId, generation) ||
            initialize.Kind != GuardianHostMessageKind.Initialize ||
            !MatchesInitialize(initialize))
        {
            return ProtocolViolationExitCode;
        }

        var initializeRequestId = initialize.Value("request_id").GetInt64();
        var lastGuardianRequestId = initializeRequestId;
        byte[]? manifestBuffer = null;
        try
        {
            var header = await ReadManifestRequestAsync(
                reader,
                guardianBootId,
                hostBootId,
                generation,
                "manifest_header",
                lastGuardianRequestId).ConfigureAwait(false);
            if (header is null) return 0;
            lastGuardianRequestId = header.Value("request_id").GetInt64();

            var headerPayload = header.Value("payload");
            var manifestId = headerPayload.GetProperty("manifest_id").GetGuid();
            var totalBytes = headerPayload.GetProperty("total_bytes").GetInt32();
            var chunkCount = headerPayload.GetProperty("chunk_count").GetInt32();
            var manifestSha256 = headerPayload.GetProperty("manifest_sha256").GetString()!;
            var aliasCount = headerPayload.GetProperty("alias_count").GetInt32();
            var templateCount = headerPayload.GetProperty("template_count").GetInt32();

            manifestBuffer = ArrayPool<byte>.Shared.Rent(totalBytes);
            await WriteOkResponseAsync(
                writer,
                guardianBootId,
                hostBootId,
                generation,
                lastGuardianRequestId,
                CreateObject(payloadWriter =>
                {
                    payloadWriter.WriteString("response_type", "manifest_header_accepted");
                    payloadWriter.WriteString("manifest_id", manifestId.ToString("D"));
                    payloadWriter.WriteNumber("next_chunk_index", 0);
                    payloadWriter.WriteNumber("next_offset", 0);
                })).ConfigureAwait(false);

            var receivedBytes = 0;
            for (var expectedChunkIndex = 0;
                 expectedChunkIndex < chunkCount;
                 expectedChunkIndex++)
            {
                var chunk = await ReadManifestRequestAsync(
                    reader,
                    guardianBootId,
                    hostBootId,
                    generation,
                    "manifest_chunk",
                    lastGuardianRequestId).ConfigureAwait(false);
                if (chunk is null) return 0;
                lastGuardianRequestId = chunk.Value("request_id").GetInt64();

                var payload = chunk.Value("payload");
                var rawBytes = payload.GetProperty("raw_bytes").GetInt32();
                var expectedRawBytes = Math.Min(524_288, totalBytes - receivedBytes);
                if (payload.GetProperty("manifest_id").GetGuid() != manifestId ||
                    payload.GetProperty("chunk_index").GetInt32() != expectedChunkIndex ||
                    payload.GetProperty("offset").GetInt32() != receivedBytes ||
                    rawBytes != expectedRawBytes)
                {
                    return ProtocolViolationExitCode;
                }

                var encodedChunk = payload.GetProperty("raw_base64").GetString()!;
                if (!Convert.TryFromBase64String(
                        encodedChunk,
                        manifestBuffer.AsSpan(receivedBytes, totalBytes - receivedBytes),
                        out var decodedLength) ||
                    decodedLength != rawBytes)
                {
                    return ProtocolViolationExitCode;
                }
                receivedBytes = checked(receivedBytes + decodedLength);

                await WriteOkResponseAsync(
                    writer,
                    guardianBootId,
                    hostBootId,
                    generation,
                    lastGuardianRequestId,
                    CreateObject(payloadWriter =>
                    {
                        payloadWriter.WriteString("response_type", "manifest_chunk_accepted");
                        payloadWriter.WriteString("manifest_id", manifestId.ToString("D"));
                        payloadWriter.WriteNumber("chunk_index", expectedChunkIndex);
                        payloadWriter.WriteNumber("next_chunk_index", expectedChunkIndex + 1);
                        payloadWriter.WriteNumber("next_offset", receivedBytes);
                    })).ConfigureAwait(false);
            }

            var seal = await ReadManifestRequestAsync(
                reader,
                guardianBootId,
                hostBootId,
                generation,
                "manifest_seal",
                lastGuardianRequestId).ConfigureAwait(false);
            if (seal is null) return 0;
            lastGuardianRequestId = seal.Value("request_id").GetInt64();
            var sealPayload = seal.Value("payload");
            if (sealPayload.GetProperty("manifest_id").GetGuid() != manifestId ||
                sealPayload.GetProperty("total_bytes").GetInt32() != totalBytes ||
                sealPayload.GetProperty("chunk_count").GetInt32() != chunkCount ||
                !string.Equals(
                    sealPayload.GetProperty("manifest_sha256").GetString(),
                    manifestSha256,
                    StringComparison.Ordinal) ||
                receivedBytes != totalBytes)
            {
                return ProtocolViolationExitCode;
            }

            Span<byte> actualManifestHash = stackalloc byte[32];
            SHA256.HashData(manifestBuffer.AsSpan(0, totalBytes), actualManifestHash);
            var actualManifestSha256 = Convert.ToHexString(actualManifestHash).ToLowerInvariant();
            CryptographicOperations.ZeroMemory(actualManifestHash);
            if (!string.Equals(actualManifestSha256, manifestSha256, StringComparison.Ordinal) ||
                !ValidateManifestDocument(
                    manifestBuffer.AsMemory(0, totalBytes),
                    guardianBootId,
                    generation,
                    aliasCount,
                    templateCount))
            {
                return ProtocolViolationExitCode;
            }

            await WriteOkResponseAsync(
                writer,
                guardianBootId,
                hostBootId,
                generation,
                lastGuardianRequestId,
                CreateObject(payloadWriter =>
                {
                    payloadWriter.WriteString("response_type", "manifest_sealed");
                    payloadWriter.WriteString("manifest_id", manifestId.ToString("D"));
                    payloadWriter.WriteString("manifest_sha256", manifestSha256);
                    payloadWriter.WriteNumber("total_bytes", totalBytes);
                })).ConfigureAwait(false);

            await writer.WriteAsync(GuardianHostRawProtocol.Create(
                GuardianHostMessageKind.Ready,
                guardianBootId,
                hostBootId,
                generation,
                ("initialize_request_id", initializeRequestId),
                ("manifest_id", manifestId),
                ("manifest_sha256", manifestSha256),
                ("host_pid", Environment.ProcessId))).ConfigureAwait(false);
        }
        finally
        {
            if (manifestBuffer is not null)
            {
                CryptographicOperations.ZeroMemory(manifestBuffer);
                ArrayPool<byte>.Shared.Return(manifestBuffer, clearArray: true);
            }
        }

        return await RunOperationalLoopAsync(
            controlRoot,
            generation,
            guardianBootId,
            hostBootId,
            lastGuardianRequestId,
            reader,
            writer,
            output).ConfigureAwait(false);
    }

    private static async Task<int> RunOperationalLoopAsync(
        string controlRoot,
        long generation,
        Guid guardianBootId,
        Guid hostBootId,
        long lastGuardianRequestId,
        GuardianHostRawProtocolReader reader,
        GuardianHostRawProtocolWriter writer,
        Stream output)
    {
        Guid? expectedWorkerBootId = null;
        while (true)
        {
            var request = await reader.ReadAsync().ConfigureAwait(false);
            if (request is null) return 0;
            if (!MatchesIdentity(request, guardianBootId, hostBootId, generation) ||
                request.Kind != GuardianHostMessageKind.Request ||
                !MatchesFixtureOperationCorrelations(request, generation) ||
                request.Value("method").GetString() != "operation")
            {
                return ProtocolViolationExitCode;
            }

            var requestId = request.Value("request_id").GetInt64();
            if (requestId <= lastGuardianRequestId)
                return ProtocolViolationExitCode;
            lastGuardianRequestId = requestId;

            var requestWorkerBootId = request.Value("worker_boot_id").GetGuid();
            if (expectedWorkerBootId is null)
                expectedWorkerBootId = requestWorkerBootId;
            else if (expectedWorkerBootId.Value != requestWorkerBootId)
                return ProtocolViolationExitCode;

            var payload = request.Value("payload");
            if (payload.GetProperty("operation").GetString() != "job_list")
                return ProtocolViolationExitCode;

            var (barrier, token) = ReadOperationControl(controlRoot, requestId);
            if (barrier is not (
                    "write_started" or
                    "terminal_decoded" or
                    "public_terminal_sent" or
                    "normal" or
                    "malformed_response" or
                    "wrong_generation_response" or
                    "duplicate_response"))
            {
                return ProtocolViolationExitCode;
            }

            var encodedRequest = GuardianHostRawProtocol.Encode(request, GuardianHostPeer.Guardian);
            try
            {
                Program.WriteControlFile(
                    Program.ControlPath(controlRoot, "received", token, generation),
                    StrictUtf8.GetString(encodedRequest));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encodedRequest);
            }

            if (barrier == "write_started")
            {
                Program.WriteControlFile(
                    Program.ControlPath(controlRoot, "barrier-write_started", token),
                    "{\"reached\":true}");
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            }

            if (barrier == "malformed_response")
            {
                var malformed = BuildMalformedResponse(
                    guardianBootId,
                    hostBootId,
                    generation,
                    requestId);
                try
                {
                    await WriteUncheckedFrameAsync(output, malformed).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(malformed);
                }
                continue;
            }

            if (barrier == "wrong_generation_response")
            {
                var wrongGeneration = generation == long.MaxValue ? generation - 1 : generation + 1;
                await WriteOkResponseAsync(
                    writer,
                    guardianBootId,
                    hostBootId,
                    wrongGeneration,
                    requestId,
                    CreateOperationCompletedPayload("{}"))
                    .ConfigureAwait(false);
                continue;
            }

            if (barrier == "duplicate_response")
            {
                var duplicate = CreateOkResponse(
                    guardianBootId,
                    hostBootId,
                    generation,
                    requestId,
                    CreateOperationCompletedPayload("{}"));
                await writer.WriteAsync(duplicate).ConfigureAwait(false);
                await writer.WriteAsync(duplicate).ConfigureAwait(false);
                continue;
            }

            var exactPayload = CreateExactTerminalPayload(token, generation);
            Program.WriteControlFile(
                Program.ControlPath(controlRoot, "terminal", token),
                exactPayload);
            var rawResponse = BuildRawOkResponse(
                guardianBootId,
                hostBootId,
                generation,
                requestId,
                exactPayload);
            try
            {
                await writer.WriteRawAsync(rawResponse).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(rawResponse);
            }

            if (barrier == "terminal_decoded")
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        }
    }

    private static async Task<GuardianHostRawEnvelope?> ReadManifestRequestAsync(
        GuardianHostRawProtocolReader reader,
        Guid guardianBootId,
        Guid hostBootId,
        long generation,
        string expectedMethod,
        long lastGuardianRequestId)
    {
        var request = await reader.ReadAsync().ConfigureAwait(false);
        if (request is null) return null;
        if (!MatchesIdentity(request, guardianBootId, hostBootId, generation) ||
            request.Kind != GuardianHostMessageKind.Request ||
            !HasNullRequestCorrelations(request) ||
            request.Value("method").GetString() != expectedMethod ||
            request.Value("request_id").GetInt64() <= lastGuardianRequestId)
        {
            throw new InvalidDataException("The recovery manifest request sequence is invalid.");
        }
        return request;
    }

    private static bool MatchesInitialize(GuardianHostRawEnvelope initialize) =>
        initialize.Value("guardian_protocol_version").GetInt32() == GuardianHostRawProtocol.Version &&
        initialize.Value("host_protocol_version").GetInt32() == GuardianHostRawProtocol.Version &&
        initialize.Value("host_executable_sha256").GetString() ==
            FakePrivateFixtureIdentity.HostExecutableSha256 &&
        initialize.Value("host_build_sha256").GetString() ==
            FakePrivateFixtureIdentity.HostBuildSha256 &&
        initialize.Value("public_contract_sha256").GetString() ==
            FakePrivateFixtureIdentity.PublicContractSha256 &&
        initialize.Value("configuration_sha256").GetString() ==
            FakePrivateFixtureIdentity.ConfigurationSha256 &&
        initialize.Value("package_manifest_sha256").GetString() ==
            FakePrivateFixtureIdentity.PackageManifestSha256;

    private static bool MatchesIdentity(
        GuardianHostRawEnvelope envelope,
        Guid guardianBootId,
        Guid hostBootId,
        long generation) =>
        envelope.GuardianBootId == guardianBootId &&
        envelope.HostBootId == hostBootId &&
        envelope.HostGeneration == generation;

    private static bool HasNullRequestCorrelations(GuardianHostRawEnvelope request) =>
        request.Value("deadline_unix_time_milliseconds").ValueKind == JsonValueKind.Null &&
        request.Value("session_alias").ValueKind == JsonValueKind.Null &&
        request.Value("session_transition_version").ValueKind == JsonValueKind.Null &&
        request.Value("worker_boot_id").ValueKind == JsonValueKind.Null &&
        request.Value("worker_generation").ValueKind == JsonValueKind.Null &&
        request.Value("plan_id").ValueKind == JsonValueKind.Null &&
        request.Value("operation_id").ValueKind == JsonValueKind.Null;

    private static bool MatchesFixtureOperationCorrelations(
        GuardianHostRawEnvelope request,
        long generation) =>
        request.Value("deadline_unix_time_milliseconds").ValueKind == JsonValueKind.Number &&
        request.Value("deadline_unix_time_milliseconds").TryGetInt64(out var deadline) &&
        deadline > 0 &&
        request.Value("session_alias").ValueKind == JsonValueKind.String &&
        request.Value("session_alias").GetString() == "default" &&
        request.Value("session_transition_version").ValueKind == JsonValueKind.Number &&
        request.Value("session_transition_version").TryGetInt64(out var transitionVersion) &&
        transitionVersion == 1 &&
        request.Value("worker_boot_id").ValueKind == JsonValueKind.String &&
        request.Value("worker_boot_id").TryGetGuid(out _) &&
        request.Value("worker_generation").ValueKind == JsonValueKind.Number &&
        request.Value("worker_generation").TryGetInt64(out var workerGeneration) &&
        workerGeneration == generation &&
        request.Value("plan_id").ValueKind == JsonValueKind.Null &&
        request.Value("operation_id").ValueKind == JsonValueKind.Null;

    private static (string Barrier, string Token) ReadOperationControl(
        string controlRoot,
        long requestId)
    {
        var path = Program.ControlPath(
            controlRoot,
            "operation",
            requestId.ToString(CultureInfo.InvariantCulture));
        var encoded = File.ReadAllBytes(path);
        _ = StrictUtf8.GetCharCount(encoded);
        using var document = JsonDocument.Parse(encoded, ManifestDocumentOptions);
        var properties = document.RootElement.EnumerateObject().ToArray();
        if (properties.Length != 2 ||
            properties[0].Name != "barrier" ||
            properties[0].Value.ValueKind != JsonValueKind.String ||
            properties[1].Name != "token" ||
            properties[1].Value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("The fixture operation control record is invalid.");
        }

        var barrier = properties[0].Value.GetString()!;
        var token = properties[1].Value.GetString()!;
        _ = Program.ControlPath(controlRoot, "validate", token);
        return (barrier, token);
    }

    private static bool ValidateManifestDocument(
        ReadOnlyMemory<byte> encodedManifest,
        Guid guardianBootId,
        long generation,
        int aliasCount,
        int templateCount)
    {
        _ = StrictUtf8.GetCharCount(encodedManifest.Span);
        using var document = JsonDocument.Parse(encodedManifest, ManifestDocumentOptions);
        var root = document.RootElement;
        RejectDuplicateManifestProperties(root);
        if (root.ValueKind != JsonValueKind.Object) return false;

        string[] expectedProperties =
        [
            "schema_version",
            "guardian_boot_id",
            "host_generation",
            "catalog_digest",
            "configuration_sha256",
            "templates",
            "bindings",
            "worker_generation_high_watermarks",
            "host_generation_high_watermark",
        ];
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != expectedProperties.Length)
            return false;
        for (var index = 0; index < expectedProperties.Length; index++)
        {
            if (!string.Equals(properties[index].Name, expectedProperties[index], StringComparison.Ordinal))
                return false;
        }

        return root.GetProperty("schema_version").ValueKind == JsonValueKind.String &&
            root.GetProperty("schema_version").GetString() == "ptk.recovery-manifest/1" &&
            root.GetProperty("guardian_boot_id").ValueKind == JsonValueKind.String &&
            root.GetProperty("guardian_boot_id").GetString() == guardianBootId.ToString("D") &&
            root.GetProperty("host_generation").ValueKind == JsonValueKind.Number &&
            root.GetProperty("host_generation").TryGetInt64(out var manifestGeneration) &&
            manifestGeneration == generation &&
            IsExactSha256(root.GetProperty("catalog_digest"), "[]"u8) &&
            root.GetProperty("configuration_sha256").ValueKind == JsonValueKind.String &&
            root.GetProperty("configuration_sha256").GetString() ==
                FakePrivateFixtureIdentity.ConfigurationSha256 &&
            root.GetProperty("templates").ValueKind == JsonValueKind.Array &&
            root.GetProperty("templates").GetArrayLength() == templateCount &&
            ValidateDefaultBinding(root.GetProperty("bindings"), aliasCount) &&
            ValidateWorkerHighWatermark(
                root.GetProperty("worker_generation_high_watermarks"),
                aliasCount,
                generation - 1) &&
            root.GetProperty("host_generation_high_watermark").ValueKind == JsonValueKind.Number &&
            root.GetProperty("host_generation_high_watermark").TryGetInt64(out var highWatermark) &&
            highWatermark == generation;
    }

    private static bool ValidateDefaultBinding(JsonElement bindings, int aliasCount)
    {
        if (bindings.ValueKind != JsonValueKind.Array ||
            bindings.GetArrayLength() != aliasCount ||
            aliasCount != 1)
        {
            return false;
        }

        var binding = bindings[0];
        string[] expected =
        [
            "alias",
            "binding_kind",
            "template_name",
            "template_digest",
            "bootstrap_digest",
            "allow_cold_background",
            "desired_state",
            "transition_version",
            "binding_digest",
        ];
        return HasExactProperties(binding, expected) &&
            binding.GetProperty("alias").GetString() == "default" &&
            binding.GetProperty("binding_kind").GetString() == "default" &&
            binding.GetProperty("template_name").ValueKind == JsonValueKind.Null &&
            binding.GetProperty("template_digest").ValueKind == JsonValueKind.Null &&
            binding.GetProperty("bootstrap_digest").ValueKind == JsonValueKind.Null &&
            !binding.GetProperty("allow_cold_background").GetBoolean() &&
            binding.GetProperty("desired_state").GetString() == "ready" &&
            binding.GetProperty("transition_version").TryGetInt64(out var transitionVersion) &&
            transitionVersion == 0 &&
            binding.GetProperty("binding_digest").GetString() ==
                FakePrivateFixtureIdentity.DefaultBindingSha256;
    }

    private static bool ValidateWorkerHighWatermark(
        JsonElement highWatermarks,
        int aliasCount,
        long expectedGeneration)
    {
        if (highWatermarks.ValueKind != JsonValueKind.Array ||
            highWatermarks.GetArrayLength() != aliasCount ||
            aliasCount != 1)
        {
            return false;
        }

        var highWatermark = highWatermarks[0];
        return HasExactProperties(highWatermark, ["alias", "generation"]) &&
            highWatermark.GetProperty("alias").GetString() == "default" &&
            highWatermark.GetProperty("generation").TryGetInt64(out var generation) &&
            generation == expectedGeneration;
    }

    private static bool HasExactProperties(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length != expected.Length) return false;
        for (var index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(properties[index].Name, expected[index], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool IsExactSha256(JsonElement value, ReadOnlySpan<byte> bytes)
    {
        if (!IsSha256(value)) return false;
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        return string.Equals(
            value.GetString(),
            Convert.ToHexString(digest).ToLowerInvariant(),
            StringComparison.Ordinal);
    }

    private static void RejectDuplicateManifestProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("The recovery manifest contains a duplicate property.");
                RejectDuplicateManifestProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                RejectDuplicateManifestProperties(item);
        }
    }

    private static bool IsSha256(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString()!;
        return text.Length == 64 && text.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static async ValueTask WriteOkResponseAsync(
        GuardianHostRawProtocolWriter writer,
        Guid guardianBootId,
        Guid hostBootId,
        long generation,
        long requestId,
        JsonElement payload)
    {
        await writer.WriteAsync(CreateOkResponse(
            guardianBootId,
            hostBootId,
            generation,
            requestId,
            payload)).ConfigureAwait(false);
    }

    private static GuardianHostRawEnvelope CreateOkResponse(
        Guid guardianBootId,
        Guid hostBootId,
        long generation,
        long requestId,
        JsonElement payload) =>
        GuardianHostRawProtocol.Create(
            GuardianHostMessageKind.Response,
            guardianBootId,
            hostBootId,
            generation,
            ("request_id", requestId),
            ("status", "ok"),
            ("payload", payload),
            ("error", null));

    private static string CreateExactTerminalPayload(string token, long generation)
    {
        var encodedToken = JsonSerializer.Serialize(token);
        return string.Concat(
            "{ \"unicode\" : \"\\u03bb—🚀\" , \"host_generation\" : ",
            generation.ToString(CultureInfo.InvariantCulture),
            " , \"call_id\" : ",
            encodedToken,
            " , \"status\" : \"completed\" }");
    }

    private static byte[] BuildRawOkResponse(
        Guid guardianBootId,
        Guid hostBootId,
        long generation,
        long requestId,
        string exactPayload)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            CreateOperationCompletedPayload(exactPayload));
        try
        {
            return BuildResponseFrame(
                guardianBootId,
                hostBootId,
                generation,
                requestId,
                payload,
                duplicateStatus: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static byte[] BuildMalformedResponse(
        Guid guardianBootId,
        Guid hostBootId,
        long generation,
        long requestId)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            CreateOperationCompletedPayload("{}"));
        try
        {
            return BuildResponseFrame(
                guardianBootId,
                hostBootId,
                generation,
                requestId,
                payload,
                duplicateStatus: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static JsonElement CreateOperationCompletedPayload(string text) =>
        CreateObject(writer =>
        {
            writer.WriteString("response_type", "operation_completed");
            writer.WriteString("operation", "job_list");
            writer.WriteStartObject("result");
            writer.WriteString("text", text);
            writer.WriteEndObject();
        });

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

    private static byte[] BuildResponseFrame(
        Guid guardianBootId,
        Guid hostBootId,
        long generation,
        long requestId,
        ReadOnlySpan<byte> payload,
        bool duplicateStatus)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("protocol_version", GuardianHostRawProtocol.Version);
            json.WriteString("kind", "response");
            json.WriteString("guardian_boot_id", guardianBootId.ToString("D"));
            json.WriteString("host_boot_id", hostBootId.ToString("D"));
            json.WriteNumber("host_generation", generation);
            json.WriteNumber("request_id", requestId);
            json.WriteString("status", "ok");
            if (duplicateStatus)
                json.WriteString("status", "ok");
            json.WritePropertyName("payload");
            json.WriteRawValue(payload, skipInputValidation: false);
            json.WriteNull("error");
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static async ValueTask WriteUncheckedFrameAsync(
        Stream output,
        ReadOnlyMemory<byte> encodedFrame)
    {
        if (encodedFrame.Length > GuardianHostRawProtocol.MaximumEncodedFrameBytes)
            throw new InvalidDataException("The deliberate malformed frame exceeded the transport bound.");

        var frame = GC.AllocateUninitializedArray<byte>(encodedFrame.Length + 1);
        try
        {
            encodedFrame.CopyTo(frame);
            frame[^1] = (byte)'\n';
            await output.WriteAsync(frame).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
        }
    }
}
