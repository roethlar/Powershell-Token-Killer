using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PtkSharedContracts;
using PtkResilienceTestFixture;

namespace PtkMcpServer.Tests;

public sealed class GuardianHostProtocolTests
{
    [Fact]
    public async Task Fixture_v1_codec_is_strict_bounded_directional_and_preserves_validated_raw_bytes()
    {
        Assert.Equal(1, GuardianHostRawProtocol.Version);
        Assert.Equal(1_048_576, GuardianHostRawProtocol.MaximumEncodedFrameBytes);
        Assert.Equal(32, GuardianHostRawProtocol.MaximumJsonDepth);

        var envelope = Hello();
        var encoded = GuardianHostRawProtocol.Encode(envelope, GuardianHostPeer.Host);
        var decoded = GuardianHostRawProtocol.Decode(encoded, GuardianHostPeer.Host);
        Assert.Equal(GuardianHostMessageKind.Hello, decoded.Kind);
        Assert.Equal(envelope.GuardianBootId, decoded.GuardianBootId);
        Assert.Equal(envelope.HostBootId, decoded.HostBootId);
        Assert.Equal(envelope.HostGeneration, decoded.HostGeneration);

        var exact = Encoding.UTF8.GetString(encoded);
        Assert.StartsWith(
            "{\"protocol_version\":1,\"kind\":\"hello\",\"guardian_boot_id\":",
            exact,
            StringComparison.Ordinal);
        Assert.DoesNotContain('\r', exact);
        Assert.DoesNotContain('\n', exact);

        AssertProtocolFailure("wrong_direction", () =>
            GuardianHostRawProtocol.Decode(encoded, GuardianHostPeer.Guardian));
        AssertProtocolFailure("bom_forbidden", () =>
            GuardianHostRawProtocol.Decode(
                new byte[] { 0xef, 0xbb, 0xbf }.Concat(encoded).ToArray(),
                GuardianHostPeer.Host));
        AssertProtocolFailure("invalid_utf8", () =>
            GuardianHostRawProtocol.Decode(new byte[] { 0xff }, GuardianHostPeer.Host));
        AssertProtocolFailure("invalid_framing", () =>
            GuardianHostRawProtocol.Decode(encoded.Append((byte)'\r').ToArray(), GuardianHostPeer.Host));
        AssertProtocolFailure("frame_too_large", () =>
            GuardianHostRawProtocol.Decode(
                new byte[GuardianHostRawProtocol.MaximumEncodedFrameBytes + 1],
                GuardianHostPeer.Host));

        var duplicate = exact.Replace(
            "\"host_pid\":4242",
            "\"host_pid\":4242,\"host_pid\":4242",
            StringComparison.Ordinal);
        Assert.NotEqual(exact, duplicate);
        AssertProtocolFailure("duplicate_field", () =>
            GuardianHostRawProtocol.Decode(Encoding.UTF8.GetBytes(duplicate), GuardianHostPeer.Host));

        var outOfOrder = exact.Replace(
            "{\"protocol_version\":1,\"kind\":\"hello\"",
            "{\"kind\":\"hello\",\"protocol_version\":1",
            StringComparison.Ordinal);
        Assert.NotEqual(exact, outOfOrder);
        AssertProtocolFailure("property_order", () =>
            GuardianHostRawProtocol.Decode(Encoding.UTF8.GetBytes(outOfOrder), GuardianHostPeer.Host));

        var unknownVersion = exact.Replace(
            "\"protocol_version\":1",
            "\"protocol_version\":2",
            StringComparison.Ordinal);
        AssertProtocolFailure("unknown_version", () =>
            GuardianHostRawProtocol.Decode(
                Encoding.UTF8.GetBytes(unknownVersion),
                GuardianHostPeer.Host));

        var unknownKind = exact.Replace(
            "\"kind\":\"hello\"",
            "\"kind\":\"unknown\"",
            StringComparison.Ordinal);
        AssertProtocolFailure("unknown_kind", () =>
            GuardianHostRawProtocol.Decode(
                Encoding.UTF8.GetBytes(unknownKind),
                GuardianHostPeer.Host));

        await using (var truncated = new MemoryStream(encoded, writable: false))
        {
            var failure = await Assert.ThrowsAsync<GuardianHostProtocolException>(async () =>
                await GuardianHostRawProtocol.ReadAsync(
                    truncated,
                    GuardianHostPeer.Host));
            Assert.Equal("truncated_frame", failure.DetailCode);
        }

        await using var output = new MemoryStream();
        var writer = new GuardianHostRawProtocolWriter(output, GuardianHostPeer.Host);
        await writer.WriteRawAsync(encoded);
        Assert.Equal([.. encoded, (byte)'\n'], output.ToArray());
    }

    [Fact]
    public async Task Direct_and_incremental_framing_enforce_empty_lf_and_exact_one_mebibyte_boundaries()
    {
        var empty = Assert.Throws<GuardianHostProtocolException>(() =>
            GuardianHostRawProtocol.Decode(Array.Empty<byte>(), GuardianHostPeer.Host));
        Assert.Equal("empty_frame", empty.DetailCode);

        var rawLf = Assert.Throws<GuardianHostProtocolException>(() =>
            GuardianHostRawProtocol.Decode("{}\n"u8.ToArray(), GuardianHostPeer.Host));
        Assert.Equal("invalid_framing", rawLf.DetailCode);

        var exact = new byte[ContractLimits.MaximumEncodedFrameBytes];
        exact.AsSpan().Fill((byte)' ');
        exact[0] = (byte)'{';
        exact[1] = (byte)'}';
        var exactFailure = Assert.Throws<GuardianHostProtocolException>(() =>
            GuardianHostRawProtocol.Decode(exact, GuardianHostPeer.Host));
        Assert.NotEqual("frame_too_large", exactFailure.DetailCode);

        var over = Assert.Throws<GuardianHostProtocolException>(() =>
            GuardianHostRawProtocol.Decode(
                new byte[ContractLimits.MaximumEncodedFrameBytes + 1],
                GuardianHostPeer.Host));
        Assert.Equal("frame_too_large", over.DetailCode);

        await using (var emptyFrame = new MemoryStream([(byte)'\n'], writable: false))
        {
            var reader = new GuardianHostRawProtocolReader(emptyFrame, GuardianHostPeer.Host);
            var failure = await Assert.ThrowsAsync<GuardianHostProtocolException>(async () =>
                await reader.ReadAsync());
            Assert.Equal("empty_frame", failure.DetailCode);
        }

        await using (var exactFrame = new MemoryStream([.. exact, (byte)'\n'], writable: false))
        {
            var reader = new GuardianHostRawProtocolReader(exactFrame, GuardianHostPeer.Host);
            var failure = await Assert.ThrowsAsync<GuardianHostProtocolException>(async () =>
                await reader.ReadAsync());
            Assert.NotEqual("frame_too_large", failure.DetailCode);
        }

        await using (var overFrame = new MemoryStream(
            [.. new byte[ContractLimits.MaximumEncodedFrameBytes + 1], (byte)'\n'],
            writable: false))
        {
            var reader = new GuardianHostRawProtocolReader(overFrame, GuardianHostPeer.Host);
            var failure = await Assert.ThrowsAsync<GuardianHostProtocolException>(async () =>
                await reader.ReadAsync());
            Assert.Equal("frame_too_large", failure.DetailCode);
        }
    }

    [Fact]
    public void Encode_rejects_payload_beyond_the_frozen_json_depth()
    {
        const int nesting = GuardianHostRawProtocol.MaximumJsonDepth + 8;
        var deepJson = "{\"value\":" + new string('[', nesting) + "0" +
            new string(']', nesting) + "}";
        using var document = JsonDocument.Parse(
            deepJson,
            new JsonDocumentOptions { MaxDepth = nesting + 8 });
        var envelope = GuardianHostRawProtocol.Create(
            GuardianHostMessageKind.Response,
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            1,
            ("request_id", 1),
            ("status", "ok"),
            ("payload", document.RootElement),
            ("error", null));

        AssertProtocolFailure("invalid_json", () =>
            GuardianHostRawProtocol.Encode(envelope, GuardianHostPeer.Host));
    }

    [Fact]
    public void Fake_manifest_vector_matches_the_frozen_recovery_manifest_schema()
    {
        var guardianBootId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        const long generation = 42;
        var encoded = FakePrivateHostConnection.BuildManifestVector(guardianBootId, generation);
        var secondEncoding = FakePrivateHostConnection.BuildManifestVector(
            guardianBootId,
            generation);
        Assert.Equal(encoded, secondEncoding);
        Assert.NotEqual(0xef, encoded[0]);
        Assert.DoesNotContain((byte)'\r', encoded);
        Assert.DoesNotContain((byte)'\n', encoded);

        using var manifest = JsonDocument.Parse(encoded);
        using var schema = JsonDocument.Parse(File.ReadAllBytes(ContractPath(
            "recovery-manifest.schema.json")));
        AssertPropertyOrder(
            schema.RootElement.GetProperty("x-ptk-property-order"),
            manifest.RootElement);

        var root = manifest.RootElement;
        Assert.Equal("ptk.recovery-manifest/1", root.GetProperty("schema_version").GetString());
        Assert.Equal(guardianBootId, root.GetProperty("guardian_boot_id").GetGuid());
        Assert.Equal(generation, root.GetProperty("host_generation").GetInt64());
        Assert.Equal(generation, root.GetProperty("host_generation_high_watermark").GetInt64());
        Assert.Empty(root.GetProperty("templates").EnumerateArray());

        var binding = Assert.Single(root.GetProperty("bindings").EnumerateArray());
        AssertPropertyOrder(
            schema.RootElement.GetProperty("$defs").GetProperty("binding")
                .GetProperty("x-ptk-property-order"),
            binding);
        Assert.Equal("default", binding.GetProperty("alias").GetString());
        Assert.Equal("default", binding.GetProperty("binding_kind").GetString());
        Assert.Equal(JsonValueKind.Null, binding.GetProperty("template_name").ValueKind);
        Assert.Equal(JsonValueKind.Null, binding.GetProperty("template_digest").ValueKind);
        Assert.Equal(JsonValueKind.Null, binding.GetProperty("bootstrap_digest").ValueKind);
        Assert.False(binding.GetProperty("allow_cold_background").GetBoolean());
        Assert.Equal("ready", binding.GetProperty("desired_state").GetString());
        Assert.Equal(0, binding.GetProperty("transition_version").GetInt64());
        Assert.Equal(
            FakePrivateFixtureIdentity.DefaultBindingSha256,
            binding.GetProperty("binding_digest").GetString());

        var highWatermark = Assert.Single(
            root.GetProperty("worker_generation_high_watermarks").EnumerateArray());
        AssertPropertyOrder(
            schema.RootElement.GetProperty("$defs")
                .GetProperty("worker_generation_high_watermark")
                .GetProperty("x-ptk-property-order"),
            highWatermark);
        Assert.Equal("default", highWatermark.GetProperty("alias").GetString());
        Assert.Equal(generation - 1, highWatermark.GetProperty("generation").GetInt64());
    }

    [Fact]
    public void Every_fixture_envelope_kind_has_one_exact_direction_and_round_trips()
    {
        foreach (var (envelope, sender) in AllEnvelopeKinds())
        {
            var encoded = GuardianHostRawProtocol.Encode(envelope, sender);
            var decoded = GuardianHostRawProtocol.Decode(encoded, sender);
            Assert.Equal(envelope.Kind, decoded.Kind);
            Assert.Equal(envelope.GuardianBootId, decoded.GuardianBootId);
            Assert.Equal(envelope.HostBootId, decoded.HostBootId);
            Assert.Equal(envelope.HostGeneration, decoded.HostGeneration);

            var wrongSender = sender == GuardianHostPeer.Guardian
                ? GuardianHostPeer.Host
                : GuardianHostPeer.Guardian;
            AssertProtocolFailure("wrong_direction", () =>
                GuardianHostRawProtocol.Decode(encoded, wrongSender));
        }
    }

    [Fact]
    public void Manifest_chunk_requires_raw_bytes_and_exact_decoded_length()
    {
        var raw = "fixture manifest chunk"u8.ToArray();
        var valid = ManifestChunk(raw, raw.Length);
        var encoded = GuardianHostRawProtocol.Encode(valid, GuardianHostPeer.Guardian);
        var decoded = GuardianHostRawProtocol.Decode(encoded, GuardianHostPeer.Guardian);
        var payload = decoded.Value("payload");
        Assert.Equal(raw.Length, payload.GetProperty("raw_bytes").GetInt32());

        using var schema = JsonDocument.Parse(File.ReadAllBytes(ContractPath(
            "guardian-host-protocol.schema.json")));
        AssertPropertyOrder(
            schema.RootElement.GetProperty("$defs").GetProperty("manifest_chunk")
                .GetProperty("x-ptk-property-order"),
            payload);

        AssertProtocolFailure("invalid_field", () => GuardianHostRawProtocol.Encode(
            ManifestChunk(raw, raw.Length - 1),
            GuardianHostPeer.Guardian));

        var missingRawBytes = Request(
            "manifest_chunk",
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["manifest_id"] = "33333333-3333-4333-8333-333333333333",
                ["chunk_index"] = 0,
                ["offset"] = 0,
                ["raw_base64"] = Convert.ToBase64String(raw),
                ["raw_sha256"] = Sha256Hex(raw),
            }));
        AssertProtocolFailure("invalid_field", () => GuardianHostRawProtocol.Encode(
            missingRawBytes,
            GuardianHostPeer.Guardian));
    }

    [Fact]
    public void Handshake_and_operation_frames_use_the_frozen_v1_payload_shapes()
    {
        using var schema = JsonDocument.Parse(File.ReadAllBytes(ContractPath(
            "guardian-host-protocol.schema.json")));

        var manifestId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var handshakePayloads = new[]
        {
            ("manifest_header_accepted", JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>
                {
                    ["response_type"] = "manifest_header_accepted",
                    ["manifest_id"] = manifestId,
                    ["next_chunk_index"] = 0,
                    ["next_offset"] = 0,
                })),
            ("manifest_chunk_accepted", JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>
                {
                    ["response_type"] = "manifest_chunk_accepted",
                    ["manifest_id"] = manifestId,
                    ["chunk_index"] = 0,
                    ["next_chunk_index"] = 1,
                    ["next_offset"] = 17,
                })),
            ("manifest_sealed", JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>
                {
                    ["response_type"] = "manifest_sealed",
                    ["manifest_id"] = manifestId,
                    ["manifest_sha256"] = new string('a', 64),
                    ["total_bytes"] = 17,
                })),
        };
        var requestId = 1L;
        foreach (var (definition, payload) in handshakePayloads)
        {
            _ = GuardianHostRawProtocol.Encode(
                Response(requestId++, payload),
                GuardianHostPeer.Host);
            AssertPropertyOrder(
                schema.RootElement.GetProperty("$defs").GetProperty(definition)
                    .GetProperty("x-ptk-property-order"),
                payload);
        }

        var operationRequest = OperationRequest(requestId++);
        _ = GuardianHostRawProtocol.Encode(operationRequest, GuardianHostPeer.Guardian);
        AssertPropertyOrder(
            schema.RootElement.GetProperty("$defs").GetProperty("operation_request")
                .GetProperty("x-ptk-property-order"),
            operationRequest.Value("payload"));

        var operationResponse = Response(requestId++);
        _ = GuardianHostRawProtocol.Encode(operationResponse, GuardianHostPeer.Host);
        AssertPropertyOrder(
            schema.RootElement.GetProperty("$defs").GetProperty("operation_completed")
                .GetProperty("x-ptk-property-order"),
            operationResponse.Value("payload"));

        var oldOperation = GuardianHostRawProtocol.Create(
            GuardianHostMessageKind.Request,
            GuardianBootId,
            HostBootId,
            1,
            ("request_id", requestId++),
            ("method", "operation"),
            ("deadline_unix_time_milliseconds", 2_000_000_000_000L),
            ("session_alias", "default"),
            ("session_transition_version", 1L),
            ("worker_boot_id", Guid.Parse("44444444-4444-4444-8444-444444444444")),
            ("worker_generation", 1L),
            ("plan_id", null),
            ("operation_id", null),
            ("payload", JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["barrier"] = "normal",
                ["token"] = "fixture",
            })));
        AssertProtocolFailure("invalid_field", () => GuardianHostRawProtocol.Encode(
            oldOperation,
            GuardianHostPeer.Guardian));

        var oldEmptyResponse = Response(
            requestId,
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>()));
        AssertProtocolFailure("invalid_field", () => GuardianHostRawProtocol.Encode(
            oldEmptyResponse,
            GuardianHostPeer.Host));
    }

    [Fact]
    public async Task Incremental_reader_preserves_fragmented_and_coalesced_frames_and_clears_pool_buffers()
    {
        var first = GuardianHostRawProtocol.Encode(Hello(), GuardianHostPeer.Host);
        var second = GuardianHostRawProtocol.Encode(Hello(), GuardianHostPeer.Host);
        var combined = first.Concat([(byte)'\n']).Concat(second).Concat([(byte)'\n']).ToArray();

        var coalescedPool = new TrackingArrayPool();
        await using (var coalesced = new MemoryStream(combined, writable: false))
        {
            var reader = new GuardianHostRawProtocolReader(
                coalesced,
                GuardianHostPeer.Host,
                coalescedPool);
            Assert.Equal(GuardianHostMessageKind.Hello, (await reader.ReadAsync())!.Kind);
            Assert.Equal(GuardianHostMessageKind.Hello, (await reader.ReadAsync())!.Kind);
            Assert.Null(await reader.ReadAsync());
        }
        Assert.Equal(3, coalescedPool.ReturnCount);
        Assert.True(coalescedPool.EveryReturnRequestedClearing);

        var fragmentedPool = new TrackingArrayPool();
        await using (var fragmented = new FragmentingReadStream(combined, maximumChunkBytes: 3))
        {
            var reader = new GuardianHostRawProtocolReader(
                fragmented,
                GuardianHostPeer.Host,
                fragmentedPool);
            Assert.Equal(GuardianHostMessageKind.Hello, (await reader.ReadAsync())!.Kind);
            Assert.Equal(GuardianHostMessageKind.Hello, (await reader.ReadAsync())!.Kind);
            Assert.Null(await reader.ReadAsync());
        }
        Assert.Equal(3, fragmentedPool.ReturnCount);
        Assert.True(fragmentedPool.EveryReturnRequestedClearing);
    }

    [Fact]
    public async Task Writer_serializes_concurrent_frames_and_latches_ambiguous_failure()
    {
        var interleaving = new InterleavingWriteStream();
        var writer = new GuardianHostRawProtocolWriter(interleaving, GuardianHostPeer.Host);
        var writes = Enumerable.Range(1, 16)
            .Select(requestId => writer.WriteAsync(Response(requestId)).AsTask())
            .ToArray();
        await Task.WhenAll(writes);

        var lines = Encoding.UTF8.GetString(interleaving.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(16, lines.Length);
        Assert.Equal(
            Enumerable.Range(1, 16).Select(value => (long)value),
            lines.Select(line => GuardianHostRawProtocol.Decode(
                    Encoding.UTF8.GetBytes(line),
                    GuardianHostPeer.Host)
                .Value("request_id").GetInt64())
                .Order());

        var failing = new FailingWriteStream();
        var faultingWriter = new GuardianHostRawProtocolWriter(failing, GuardianHostPeer.Host);
        await Assert.ThrowsAsync<IOException>(async () =>
            await faultingWriter.WriteAsync(Response(1)));
        var latched = await Assert.ThrowsAsync<GuardianHostProtocolException>(async () =>
            await faultingWriter.WriteAsync(Response(2)));
        Assert.Equal("writer_faulted", latched.DetailCode);
        Assert.Equal(1, failing.WriteCount);
    }

    private static readonly Guid GuardianBootId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid HostBootId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static GuardianHostRawEnvelope Hello() => GuardianHostRawProtocol.Create(
        GuardianHostMessageKind.Hello,
        GuardianBootId,
        HostBootId,
        1,
        ("host_pid", 4242),
        ("host_executable_sha256", new string('1', 64)),
        ("host_build_sha256", new string('2', 64)),
        ("public_contract_sha256", new string('3', 64)),
        ("configuration_sha256", new string('4', 64)),
        ("request_channel_owned", true),
        ("event_channel_owned", true));

    private static GuardianHostRawEnvelope Response(long requestId) => Response(
        requestId,
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["response_type"] = "operation_completed",
            ["operation"] = "job_list",
            ["result"] = new Dictionary<string, object?>
            {
                ["text"] = "{}",
            },
        }));

    private static GuardianHostRawEnvelope Response(long requestId, JsonElement payload) =>
        GuardianHostRawProtocol.Create(
        GuardianHostMessageKind.Response,
        GuardianBootId,
        HostBootId,
        1,
        ("request_id", requestId),
        ("status", "ok"),
        ("payload", payload),
        ("error", null));

    private static GuardianHostRawEnvelope OperationRequest(long requestId)
    {
        var callId = "01890f2e-9b5a-7cc1-98b7-5e510d65e4d2";
        return GuardianHostRawProtocol.Create(
            GuardianHostMessageKind.Request,
            GuardianBootId,
            HostBootId,
            1,
            ("request_id", requestId),
            ("method", "operation"),
            ("deadline_unix_time_milliseconds", 2_000_000_000_000L),
            ("session_alias", "default"),
            ("session_transition_version", 1L),
            ("worker_boot_id", Guid.Parse("44444444-4444-4444-8444-444444444444")),
            ("worker_generation", 1L),
            ("plan_id", null),
            ("operation_id", null),
            ("payload", JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["operation"] = "job_list",
                ["call_id"] = callId,
                ["dispatch_capability"] = new Dictionary<string, object?>
                {
                    ["token"] = new string('A', 43),
                    ["call_id"] = callId,
                    ["expires_unix_time_milliseconds"] = 2_000_000_000_000L,
                },
                ["output_capability"] = null,
                ["arguments"] = new Dictionary<string, object?>(),
            })));
    }

    private static GuardianHostRawEnvelope ManifestChunk(byte[] raw, int rawBytes) => Request(
        "manifest_chunk",
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["manifest_id"] = "33333333-3333-4333-8333-333333333333",
            ["chunk_index"] = 0,
            ["offset"] = 0,
            ["raw_bytes"] = rawBytes,
            ["raw_base64"] = Convert.ToBase64String(raw),
            ["raw_sha256"] = Sha256Hex(raw),
        }));

    private static GuardianHostRawEnvelope Request(string method, JsonElement payload) =>
        GuardianHostRawProtocol.Create(
            GuardianHostMessageKind.Request,
            GuardianBootId,
            HostBootId,
            1,
            ("request_id", 2L),
            ("method", method),
            ("deadline_unix_time_milliseconds", null),
            ("session_alias", null),
            ("session_transition_version", null),
            ("worker_boot_id", null),
            ("worker_generation", null),
            ("plan_id", null),
            ("operation_id", null),
            ("payload", payload));

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static (GuardianHostRawEnvelope Envelope, GuardianHostPeer Sender)[] AllEnvelopeKinds()
    {
        var guardian = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var host = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var manifest = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var digest = new string('a', 64);
        var eventPayload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["binding_digest"] = digest,
            ["startup_deadline_unix_time_milliseconds"] = 1L,
        });

        return
        [
            (Hello(), GuardianHostPeer.Host),
            (GuardianHostRawProtocol.Create(
                GuardianHostMessageKind.Initialize,
                guardian,
                host,
                1,
                ("request_id", 1L),
                ("guardian_protocol_version", 1),
                ("host_protocol_version", 1),
                ("host_executable_sha256", digest),
                ("host_build_sha256", digest),
                ("public_contract_sha256", digest),
                ("configuration_sha256", digest),
                ("package_manifest_sha256", digest),
                ("maximum_manifest_bytes", 25_165_824),
                ("maximum_manifest_chunk_raw_bytes", 524_288),
                ("maximum_aliases", 128),
                ("maximum_templates", 128)), GuardianHostPeer.Guardian),
            (GuardianHostRawProtocol.Create(
                GuardianHostMessageKind.Ready,
                guardian,
                host,
                1,
                ("initialize_request_id", 1L),
                ("manifest_id", manifest),
                ("manifest_sha256", digest),
                ("host_pid", 4242)), GuardianHostPeer.Host),
            (OperationRequest(2L), GuardianHostPeer.Guardian),
            (GuardianHostRawProtocol.Create(
                GuardianHostMessageKind.Cancel,
                guardian,
                host,
                1,
                ("request_id", 3L),
                ("target_request_id", 2L),
                ("reason", "caller_canceled")), GuardianHostPeer.Guardian),
            (GuardianHostRawProtocol.Create(
                GuardianHostMessageKind.Event,
                guardian,
                host,
                1,
                ("event_sequence", 1L),
                ("event_type", "worker_create_capability_requested"),
                ("request_id", null),
                ("session_alias", "default"),
                ("session_transition_version", 1L),
                ("worker_boot_id", null),
                ("worker_generation", null),
                ("plan_id", null),
                ("operation_id", null),
                ("payload", eventPayload)), GuardianHostPeer.Host),
            (Response(4), GuardianHostPeer.Host),
            (GuardianHostRawProtocol.Create(
                GuardianHostMessageKind.Shutdown,
                guardian,
                host,
                1,
                ("request_id", 5L),
                ("deadline_unix_time_milliseconds", 1L),
                ("reason", "guardian_shutdown")), GuardianHostPeer.Guardian),
        ];
    }

    private static void AssertProtocolFailure(string detailCode, Action action)
    {
        var failure = Assert.Throws<GuardianHostProtocolException>(action);
        Assert.Equal(detailCode, failure.DetailCode);
    }

    private static void AssertPropertyOrder(JsonElement expectedOrder, JsonElement actual)
    {
        Assert.Equal(
            expectedOrder.EnumerateArray().Select(value => value.GetString()),
            actual.EnumerateObject().Select(property => property.Name));
    }

    private static string ContractPath(string fileName)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "server",
                "Contracts",
                "ResilienceR0",
                fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate frozen R0 contract '{fileName}'.");
    }

    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        internal int ReturnCount { get; private set; }
        internal bool EveryReturnRequestedClearing { get; private set; } = true;

        public override byte[] Rent(int minimumLength) => new byte[minimumLength];

        public override void Return(byte[] array, bool clearArray = false)
        {
            ReturnCount++;
            EveryReturnRequestedClearing &= clearArray;
            if (clearArray) Array.Clear(array);
        }
    }

    private sealed class FragmentingReadStream(byte[] bytes, int maximumChunkBytes) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var length = Math.Min(Math.Min(count, maximumChunkBytes), bytes.Length - _offset);
            if (length <= 0) return 0;
            bytes.AsSpan(_offset, length).CopyTo(buffer.AsSpan(offset, length));
            _offset += length;
            return length;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(
                Math.Min(buffer.Length, maximumChunkBytes),
                bytes.Length - _offset);
            if (length <= 0) return ValueTask.FromResult(0);
            bytes.AsMemory(_offset, length).CopyTo(buffer);
            _offset += length;
            return ValueTask.FromResult(length);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class InterleavingWriteStream : Stream
    {
        private readonly MemoryStream _buffer = new();
        private readonly object _sync = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length { get { lock (_sync) return _buffer.Length; } }
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var midpoint = Math.Max(1, buffer.Length / 2);
            lock (_sync) _buffer.Write(buffer.Span[..midpoint]);
            await Task.Yield();
            lock (_sync) _buffer.Write(buffer.Span[midpoint..]);
        }

        internal byte[] ToArray()
        {
            lock (_sync) return _buffer.ToArray();
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_sync) _buffer.Write(buffer, offset, count);
        }
    }

    private sealed class FailingWriteStream : Stream
    {
        internal int WriteCount { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return ValueTask.FromException(new IOException("Injected writer failure."));
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
