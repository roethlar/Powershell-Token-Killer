using System.Text;
using System.Text.Json;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class GuardianHostUnionContractTests
{
    private static readonly Guid GuardianId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid HostId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid WorkerId = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly Guid PlanId = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    private static readonly Guid OperationId = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");
    private static readonly Guid CallId = Guid.Parse("77777777-7777-7777-8777-777777777777");
    private const string Token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly string Digest = new('a', 64);

    [Fact]
    public void Every_request_method_and_operation_branch_round_trips()
    {
        Assert.Equal(8, Enum.GetValues<GuardianHostRequestMethod>().Length);
        Assert.Equal(10, Enum.GetValues<GuardianHostOperationKind>().Length);

        var manifestId = Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff");
        var raw = "manifest"u8.ToArray();
        var requests = new List<GuardianHostRawEnvelope>
        {
            Request("manifest_header", null, null, null, null, null, null, null,
                Object(
                    ("manifest_id", manifestId),
                    ("total_bytes", raw.Length),
                    ("chunk_count", 1),
                    ("manifest_sha256", Sha256(raw)),
                    ("alias_count", 1),
                    ("template_count", 0))),
            Request("manifest_chunk", null, null, null, null, null, null, null,
                Object(
                    ("manifest_id", manifestId),
                    ("chunk_index", 0),
                    ("offset", 0),
                    ("raw_bytes", raw.Length),
                    ("raw_base64", Convert.ToBase64String(raw)),
                    ("raw_sha256", Sha256(raw)))),
            Request("manifest_seal", null, null, null, null, null, null, null,
                Object(
                    ("manifest_id", manifestId),
                    ("total_bytes", raw.Length),
                    ("chunk_count", 1),
                    ("manifest_sha256", Sha256(raw)))),
            Request("worker_create_capability_grant", 100, "default", 1, null, 2, null, null,
                Object(("source_event_sequence", 1), ("token", Token), ("worker_generation", 2))),
            Request("worker_containment_pending_ack", 100, "default", 1, WorkerId, 2, null, null,
                Object(("source_event_sequence", 2))),
            Request("worker_containment_armed_ack", 100, "default", 1, WorkerId, 2, null, null,
                Object(("source_event_sequence", 3))),
            Request("worker_containment_remove_ack", 100, "default", 1, WorkerId, 2, null, null,
                Object(("source_event_sequence", 4))),
        };

        foreach (var (operation, arguments, outputCapability, worker, plan) in OperationArguments())
        {
            var payload = Object(
                ("operation", operation),
                ("call_id", CallId),
                ("dispatch_capability", DispatchCapability(CallId)),
                ("output_capability", outputCapability ? OutputCapability() : null),
                ("arguments", arguments));
            requests.Add(Request(
                "operation",
                100,
                "default",
                1,
                worker ? WorkerId : null,
                worker ? 2 : null,
                plan ? PlanId : null,
                plan ? OperationId : null,
                payload));
        }

        Assert.Equal(17, requests.Count);
        foreach (var request in requests)
        {
            var encoded = GuardianHostRawProtocol.Encode(request, GuardianHostPeer.Guardian);
            Assert.Equal(request.Values["method"].GetString(),
                GuardianHostRawProtocol.Decode(encoded, GuardianHostPeer.Guardian).Value("method").GetString());
        }
    }

    [Fact]
    public void Every_host_event_branch_round_trips()
    {
        Assert.Equal(12, Enum.GetValues<GuardianHostEventType>().Length);
        var raw = "x"u8.ToArray();
        var pending = Object(
            ("broker_pid", 11),
            ("broker_start_identity_high", 0UL),
            ("broker_start_identity_low", 1UL),
            ("worker_pid", 22),
            ("worker_start_identity_high", 0UL),
            ("worker_start_identity_low", 2UL),
            ("intended_pgid", 22));
        var armed = Object(
            ("broker_pid", 11),
            ("broker_start_identity_high", 0UL),
            ("broker_start_identity_low", 1UL),
            ("worker_pid", 22),
            ("worker_start_identity_high", 0UL),
            ("worker_start_identity_low", 2UL),
            ("pgid", 22));

        var events = new[]
        {
            Event("worker_create_capability_requested", null, "default", 1, null, null, null, null,
                Object(("binding_digest", Digest), ("startup_deadline_unix_time_milliseconds", 100))),
            Event("worker_containment_pending", null, "default", 1, WorkerId, 2, null, null, pending),
            Event("worker_containment_armed", null, "default", 1, WorkerId, 2, null, null, armed),
            Event("worker_containment_remove_requested", null, "default", 1, WorkerId, 2, null, null, armed),
            Event("operation_delivery", 9, "default", 1, WorkerId, 2, PlanId, OperationId,
                Object(("dispatch_token", Token), ("delivery_state", "write_started"), ("worker_request_id", 7))),
            Event("session_lifecycle", null, "default", 1, null, null, null, null,
                Object(
                    ("previous_state", "cold"),
                    ("state", "starting"),
                    ("reason_code", "requested_open"),
                    ("ready_for_effects", false),
                    ("warm_state_lost", false),
                    ("bootstrap_state", "pending"))),
            Event("worker_lost", null, "default", 1, WorkerId, 2, null, null,
                Object(
                    ("reason_code", "process_exit"),
                    ("exit_code", 1),
                    ("termination_certainty", "confirmed"),
                    ("effects_state", "terminal_known"))),
            Event("worker_diagnostic_chunk", null, "default", 1, WorkerId, 2, null, null,
                Object(
                    ("stream", "stderr"),
                    ("chunk_index", 0),
                    ("offset", 0),
                    ("raw_bytes", raw.Length),
                    ("raw_base64", Convert.ToBase64String(raw)),
                    ("raw_sha256", Sha256(raw)),
                    ("end_of_stream", true))),
            Event("worker_diagnostic_truncated", null, "default", 1, WorkerId, 2, null, null,
                Object(("stream", "stdout"), ("captured_bytes", 65_536), ("discarded_bytes", 1))),
            Event("job_lifecycle", null, "default", 1, WorkerId, 2, null, null,
                Object(
                    ("public_job_id", 5),
                    ("state", "running"),
                    ("exit_code", null),
                    ("output_state", "streaming"),
                    ("output_bytes", 0),
                    ("output_sha256", null))),
            Event("output_chunk", 9, "default", 1, WorkerId, 2, null, null,
                Object(
                    ("output_capability_token", Token),
                    ("chunk_index", 0),
                    ("offset", 0),
                    ("raw_bytes", raw.Length),
                    ("raw_base64", Convert.ToBase64String(raw)),
                    ("raw_sha256", Sha256(raw)))),
            Event("output_seal", 9, "default", 1, WorkerId, 2, null, null,
                Object(
                    ("output_capability_token", Token),
                    ("state", "complete"),
                    ("total_bytes", raw.Length),
                    ("output_sha256", Sha256(raw)))),
        };

        foreach (var value in events)
        {
            var encoded = GuardianHostRawProtocol.Encode(value, GuardianHostPeer.Host);
            Assert.Equal(value.Value("event_type").GetString(),
                GuardianHostRawProtocol.Decode(encoded, GuardianHostPeer.Host).Value("event_type").GetString());
        }
    }

    [Fact]
    public void Every_success_response_and_operation_result_branch_round_trips()
    {
        Assert.Equal(6, Enum.GetValues<GuardianHostResponseType>().Length);
        var manifestId = Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff");
        var payloads = new List<JsonElement>
        {
            Object(
                ("response_type", "manifest_header_accepted"),
                ("manifest_id", manifestId),
                ("next_chunk_index", 0),
                ("next_offset", 0)),
            Object(
                ("response_type", "manifest_chunk_accepted"),
                ("manifest_id", manifestId),
                ("chunk_index", 0),
                ("next_chunk_index", 1),
                ("next_offset", 1)),
            Object(
                ("response_type", "manifest_sealed"),
                ("manifest_id", manifestId),
                ("manifest_sha256", Digest),
                ("total_bytes", 1)),
            Object(("response_type", "control_acknowledged"), ("source_event_sequence", 1)),
            Object(("response_type", "shutdown_accepted")),
        };

        foreach (var operation in Enum.GetValues<GuardianHostOperationKind>())
        {
            var wire = SnakeCase(operation.ToString());
            payloads.Add(Object(
                ("response_type", "operation_completed"),
                ("operation", wire),
                ("result", OperationResult(wire))));
        }

        foreach (var payload in payloads)
        {
            var response = ResponseOk(payload);
            var encoded = GuardianHostRawProtocol.Encode(response, GuardianHostPeer.Host);
            Assert.Equal(payload.GetProperty("response_type").GetString(),
                GuardianHostRawProtocol.Decode(encoded, GuardianHostPeer.Host)
                    .Value("payload").GetProperty("response_type").GetString());
        }
    }

    [Fact]
    public void Every_private_failure_detail_has_one_normalized_message_code()
    {
        Assert.Equal(37, Enum.GetValues<GuardianHostPrivateDetailCode>().Length);
        Assert.Equal(12, Enum.GetValues<GuardianHostPrivateMessageCode>().Length);

        foreach (var detail in Enum.GetValues<GuardianHostPrivateDetailCode>())
        {
            var error = new GuardianHostPrivateError(detail);
            var response = ResponseError(Object(
                ("detail_code", SnakeCase(error.DetailCode.ToString())),
                ("message_code", SnakeCase(error.MessageCode.ToString()))));
            _ = GuardianHostRawProtocol.Decode(
                GuardianHostRawProtocol.Encode(response, GuardianHostPeer.Host),
                GuardianHostPeer.Host);
        }

        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            ResponseError(Object(
                ("detail_code", "outcome_unknown"),
                ("message_code", "worker_failure"))),
            GuardianHostPeer.Host));
    }

    [Fact]
    public void Closed_unions_and_local_invariants_fail_closed()
    {
        var valid = OperationEnvelope("job_list", Object(), outputCapability: false, worker: true, plan: false);
        _ = GuardianHostRawProtocol.Encode(valid, GuardianHostPeer.Guardian);

        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            OperationEnvelope("future_operation", Object(), false, true, false),
            GuardianHostPeer.Guardian));

        var mismatchedCall = Object(
            ("operation", "job_list"),
            ("call_id", CallId),
            ("dispatch_capability", DispatchCapability(Guid.Parse("76666666-6666-7666-8666-666666666666"))),
            ("output_capability", null),
            ("arguments", Object()));
        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            Request("operation", 100, "default", 1, WorkerId, 2, null, null, mismatchedCall),
            GuardianHostPeer.Guardian));

        var invalidDelivery = Event("operation_delivery", 9, "default", 1, WorkerId, 2, null, null,
            Object(("dispatch_token", Token), ("delivery_state", "not_dispatched"), ("worker_request_id", 7)));
        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(invalidDelivery, GuardianHostPeer.Host));

        var invalidContainment = Event("worker_containment_pending", null, "default", 1, WorkerId, 2, null, null,
            Object(
                ("broker_pid", 11),
                ("broker_start_identity_high", 0UL),
                ("broker_start_identity_low", 1UL),
                ("worker_pid", 22),
                ("worker_start_identity_high", 0UL),
                ("worker_start_identity_low", 2UL),
                ("intended_pgid", 23)));
        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(invalidContainment, GuardianHostPeer.Host));

        var encoded = Encoding.UTF8.GetString(GuardianHostRawProtocol.Encode(
            ResponseOk(Object(("response_type", "shutdown_accepted"))),
            GuardianHostPeer.Host));
        var uppercase = encoded.Replace(
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA",
            StringComparison.Ordinal);
        AssertProtocolFailure(() => GuardianHostRawProtocol.Decode(
            Encoding.UTF8.GetBytes(uppercase),
            GuardianHostPeer.Host));

        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            Hello(Digest + "\n"),
            GuardianHostPeer.Host));

        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            Request("operation", 100, "default\n", 1, WorkerId, 2, null, null,
                Object(
                    ("operation", "job_list"),
                    ("call_id", CallId),
                    ("dispatch_capability", DispatchCapability(CallId)),
                    ("output_capability", null),
                    ("arguments", Object()))),
            GuardianHostPeer.Guardian));

        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            Request("operation", 100, "default", 1, WorkerId, 2, null, null,
                Object(
                    ("operation", "job_list"),
                    ("call_id", CallId.ToString("D") + "\n"),
                    ("dispatch_capability", DispatchCapability(CallId)),
                    ("output_capability", null),
                    ("arguments", Object()))),
            GuardianHostPeer.Guardian));

        var noncanonicalToken = new string('A', 42) + "B";
        Assert.Throws<ArgumentException>(() => new CapabilityToken(noncanonicalToken));
        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            Request("operation", 100, "default", 1, WorkerId, 2, null, null,
                Object(
                    ("operation", "job_list"),
                    ("call_id", CallId),
                    ("dispatch_capability", Object(
                        ("token", noncanonicalToken),
                        ("call_id", CallId),
                        ("expires_unix_time_milliseconds", 100))),
                    ("output_capability", null),
                    ("arguments", Object()))),
            GuardianHostPeer.Guardian));
    }

    [Fact]
    public void Every_frozen_numeric_bound_family_fails_on_both_invalid_sides()
    {
        var raw = "x"u8.ToArray();
        var manifestHeader = Request("manifest_header", null, null, null, null, null, null, null,
            Object(
                ("manifest_id", Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff")),
                ("total_bytes", 1), ("chunk_count", 1), ("manifest_sha256", Sha256(raw)),
                ("alias_count", 1), ("template_count", 0)));
        var manifestChunk = Request("manifest_chunk", null, null, null, null, null, null, null,
            Object(
                ("manifest_id", Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff")),
                ("chunk_index", 0), ("offset", 0), ("raw_bytes", 1),
                ("raw_base64", Convert.ToBase64String(raw)), ("raw_sha256", Sha256(raw))));
        var reset = OperationEnvelope(
            "reset", Object(("expectedGeneration", 2), ("force", false)),
            outputCapability: false, worker: true, plan: false);
        var jobOutput = OperationEnvelope(
            "job_output", Object(("public_job_id", 5), ("job_capability", Token), ("offset", 0)),
            outputCapability: true, worker: true, plan: false);
        var containment = Event("worker_containment_pending", null, "default", 1, WorkerId, 2, null, null,
            Object(
                ("broker_pid", 11), ("broker_start_identity_high", 0UL),
                ("broker_start_identity_low", 1UL), ("worker_pid", 22),
                ("worker_start_identity_high", 0UL), ("worker_start_identity_low", 2UL),
                ("intended_pgid", 22)));
        var diagnostic = Event("worker_diagnostic_chunk", null, "default", 1, WorkerId, 2, null, null,
            Object(
                ("stream", "stderr"), ("chunk_index", 0), ("offset", 0),
                ("raw_bytes", 1), ("raw_base64", Convert.ToBase64String(raw)),
                ("raw_sha256", Sha256(raw)), ("end_of_stream", true)));
        var outputChunk = Event("output_chunk", 9, "default", 1, WorkerId, 2, null, null,
            Object(
                ("output_capability_token", Token), ("chunk_index", 0), ("offset", 0),
                ("raw_bytes", 1), ("raw_base64", Convert.ToBase64String(raw)),
                ("raw_sha256", Sha256(raw))));
        var outputSeal = Event("output_seal", 9, "default", 1, WorkerId, 2, null, null,
            Object(
                ("output_capability_token", Token), ("state", "complete"),
                ("total_bytes", 1), ("output_sha256", Sha256(raw))));
        var jobLifecycle = Event("job_lifecycle", null, "default", 1, WorkerId, 2, null, null,
            Object(
                ("public_job_id", 5), ("state", "failed"), ("exit_code", 1),
                ("output_state", "sealed"), ("output_bytes", 1), ("output_sha256", Sha256(raw))));

        var cases = new (GuardianHostRawEnvelope Frame, GuardianHostPeer Sender, string Old, string Low, string High)[]
        {
            (jobLifecycle, GuardianHostPeer.Host, "\"exit_code\":1", "\"exit_code\":-2147483649", "\"exit_code\":2147483648"),
            (manifestChunk, GuardianHostPeer.Guardian, "\"chunk_index\":0", "\"chunk_index\":-1", "\"chunk_index\":48"),
            (manifestHeader, GuardianHostPeer.Guardian, "\"template_count\":0", "\"template_count\":-1", "\"template_count\":129"),
            (diagnostic, GuardianHostPeer.Host, "\"offset\":0", "\"offset\":-1", "\"offset\":65536"),
            (outputChunk, GuardianHostPeer.Host, "\"offset\":0", "\"offset\":-1", "\"offset\":8388608"),
            (outputSeal, GuardianHostPeer.Host, "\"total_bytes\":1", "\"total_bytes\":-1", "\"total_bytes\":8388609"),
            (manifestChunk, GuardianHostPeer.Guardian, "\"offset\":0", "\"offset\":-1", "\"offset\":25165824"),
            (reset, GuardianHostPeer.Guardian, "\"expectedGeneration\":2", "\"expectedGeneration\":-1", "\"expectedGeneration\":9223372036854775808"),
            (containment, GuardianHostPeer.Host, "\"broker_start_identity_high\":0", "\"broker_start_identity_high\":-1", "\"broker_start_identity_high\":18446744073709551616"),
            (manifestHeader, GuardianHostPeer.Guardian, "\"chunk_count\":1", "\"chunk_count\":0", "\"chunk_count\":49"),
            (manifestHeader, GuardianHostPeer.Guardian, "\"alias_count\":1", "\"alias_count\":0", "\"alias_count\":129"),
            (diagnostic, GuardianHostPeer.Host, "\"raw_bytes\":1", "\"raw_bytes\":0", "\"raw_bytes\":16385"),
            (outputChunk, GuardianHostPeer.Host, "\"raw_bytes\":1", "\"raw_bytes\":0", "\"raw_bytes\":65537"),
            (manifestChunk, GuardianHostPeer.Guardian, "\"raw_bytes\":1", "\"raw_bytes\":0", "\"raw_bytes\":524289"),
            (jobOutput, GuardianHostPeer.Guardian, "\"maximum_bytes\":1024", "\"maximum_bytes\":0", "\"maximum_bytes\":8388609"),
            (manifestHeader, GuardianHostPeer.Guardian, "\"total_bytes\":1", "\"total_bytes\":0", "\"total_bytes\":25165825"),
            (Hello(Digest), GuardianHostPeer.Host, "\"host_pid\":42", "\"host_pid\":0", "\"host_pid\":2147483648"),
            (containment, GuardianHostPeer.Host, "\"broker_pid\":11", "\"broker_pid\":0", "\"broker_pid\":4294967296"),
            (Hello(Digest), GuardianHostPeer.Host, "\"host_generation\":1", "\"host_generation\":0", "\"host_generation\":9223372036854775808"),
        };

        Assert.Equal(19, cases.Length);
        foreach (var (frame, sender, oldValue, low, high) in cases)
        {
            AssertWireMutationFails(frame, sender, oldValue, low);
            AssertWireMutationFails(frame, sender, oldValue, high);
        }
    }

    [Fact]
    public void Scalar_utf8_response_and_correlation_unions_fail_closed()
    {
        var dispatch = DispatchCapability(CallId);
        var output = OutputCapability();

        var maximumAscii = new string('a', ContractLimits.MaximumScriptBytes);
        _ = GuardianHostRawProtocol.Encode(
            Request("operation", 100, "default", 1, WorkerId, 2, PlanId, OperationId,
                Object(
                    ("operation", "invoke_foreground"), ("call_id", CallId),
                    ("dispatch_capability", dispatch), ("output_capability", output),
                    ("arguments", Object(("script", maximumAscii), ("raw", false), ("route", "pwsh"))))),
            GuardianHostPeer.Guardian);
        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            Request("operation", 100, "default", 1, WorkerId, 2, PlanId, OperationId,
                Object(
                    ("operation", "invoke_foreground"), ("call_id", CallId),
                    ("dispatch_capability", dispatch), ("output_capability", output),
                    ("arguments", Object(("script", maximumAscii + "a"), ("raw", false), ("route", "pwsh"))))),
            GuardianHostPeer.Guardian));

        var maximumUtf8 = string.Concat(Enumerable.Repeat("😀", ContractLimits.MaximumScriptBytes / 4));
        _ = GuardianHostRawProtocol.Encode(
            Request("operation", 100, "default", 1, WorkerId, 2, PlanId, OperationId,
                Object(
                    ("operation", "invoke_foreground"), ("call_id", CallId),
                    ("dispatch_capability", dispatch), ("output_capability", output),
                    ("arguments", Object(("script", maximumUtf8), ("raw", false), ("route", "pwsh"))))),
            GuardianHostPeer.Guardian);
        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            Request("operation", 100, "default", 1, WorkerId, 2, PlanId, OperationId,
                Object(
                    ("operation", "invoke_foreground"), ("call_id", CallId),
                    ("dispatch_capability", dispatch), ("output_capability", output),
                    ("arguments", Object(("script", maximumUtf8 + "😀"), ("raw", false), ("route", "pwsh"))))),
            GuardianHostPeer.Guardian));

        var maximumAlias = new string('a', 64);
        _ = GuardianHostRawProtocol.Encode(
            Request("operation", 100, maximumAlias, 1, WorkerId, 2, null, null,
                Object(
                    ("operation", "job_list"), ("call_id", CallId),
                    ("dispatch_capability", dispatch), ("output_capability", null),
                    ("arguments", Object()))),
            GuardianHostPeer.Guardian);
        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            Request("operation", 100, maximumAlias + "a", 1, WorkerId, 2, null, null,
                Object(
                    ("operation", "job_list"), ("call_id", CallId),
                    ("dispatch_capability", dispatch), ("output_capability", null),
                    ("arguments", Object()))),
            GuardianHostPeer.Guardian));

        var okPayload = Object(("response_type", "shutdown_accepted"));
        var validError = Object(("detail_code", "outcome_unknown"), ("message_code", "ambiguous_outcome"));
        GuardianHostRawEnvelope[] invalidResponses =
        [
            RawResponse("future", okPayload, null),
            RawResponse("ok", null, null),
            RawResponse("ok", okPayload, validError),
            RawResponse("error", okPayload, validError),
            RawResponse("error", null, null),
        ];
        foreach (var response in invalidResponses)
            AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(response, GuardianHostPeer.Host));

        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            OperationEnvelope("job_list", Object(), false, worker: false, plan: false),
            GuardianHostPeer.Guardian));
        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            OperationEnvelope("invoke_foreground",
                Object(("script", "x"), ("raw", false), ("route", "pwsh")),
                outputCapability: true, worker: true, plan: false),
            GuardianHostPeer.Guardian));
        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            OperationEnvelope("session_open",
                Object(("template", null), ("allowColdBackground", false)),
                outputCapability: false, worker: true, plan: false),
            GuardianHostPeer.Guardian));

        var invalidReadyResult = Object(
            ("response_type", "operation_completed"), ("operation", "reset"),
            ("result", Object(
                ("alias", "default"), ("state", "cold"),
                ("worker_boot_id", null), ("worker_generation", null),
                ("transition_version", 1), ("ready_for_effects", true),
                ("warm_state_lost", false), ("bootstrap_state", "not_applicable"))));
        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            ResponseOk(invalidReadyResult), GuardianHostPeer.Host));
    }

    [Fact]
    public void Raw_base64_length_canonicality_and_hash_are_all_enforced()
    {
        var raw = new byte[] { 0xff };
        GuardianHostRawEnvelope Frame(string base64, int count, string digest) =>
            Request("manifest_chunk", null, null, null, null, null, null, null,
                Object(
                    ("manifest_id", Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff")),
                    ("chunk_index", 0), ("offset", 0), ("raw_bytes", count),
                    ("raw_base64", base64), ("raw_sha256", digest)));

        _ = GuardianHostRawProtocol.Encode(
            Frame(Convert.ToBase64String(raw), 1, Sha256(raw)), GuardianHostPeer.Guardian);
        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            Frame("%%%", 1, Sha256(raw)), GuardianHostPeer.Guardian));
        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            Frame("/x==", 1, Sha256(raw)), GuardianHostPeer.Guardian));
        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            Frame(Convert.ToBase64String(raw), 2, Sha256(raw)), GuardianHostPeer.Guardian));
        AssertProtocolFailure(() => GuardianHostRawProtocol.Encode(
            Frame(Convert.ToBase64String(raw), 1, new string('b', 64)), GuardianHostPeer.Guardian));
    }

    private static IEnumerable<(string Operation, JsonElement Arguments, bool Output, bool Worker, bool Plan)>
        OperationArguments()
    {
        yield return ("invoke_foreground", Object(("script", "Get-Date"), ("raw", false), ("route", "pwsh")), true, true, true);
        yield return ("invoke_background", Object(("script", "Get-Date"), ("raw", true), ("route", "auto"), ("public_job_id", 5)), true, true, true);
        yield return ("job_list", Object(), false, true, false);
        yield return ("job_status", Object(("public_job_id", 5), ("job_capability", Token)), false, true, false);
        yield return ("job_output", Object(("public_job_id", 5), ("job_capability", Token), ("offset", 0)), true, true, false);
        yield return ("job_kill", Object(("public_job_id", 5), ("job_capability", Token)), false, true, false);
        yield return ("reset", Object(("expectedGeneration", 2), ("force", false)), false, true, false);
        yield return ("session_open", Object(("template", null), ("allowColdBackground", false)), false, false, false);
        yield return ("session_close", Object(("expectedGeneration", 2), ("force", false)), false, true, false);
        yield return ("session_restart", Object(("expectedGeneration", 2), ("force", true)), false, true, false);
    }

    private static GuardianHostRawEnvelope OperationEnvelope(
        string operation,
        JsonElement arguments,
        bool outputCapability,
        bool worker,
        bool plan)
    {
        var payload = Object(
            ("operation", operation),
            ("call_id", CallId),
            ("dispatch_capability", DispatchCapability(CallId)),
            ("output_capability", outputCapability ? OutputCapability() : null),
            ("arguments", arguments));
        return Request(
            "operation", 100, "default", 1,
            worker ? WorkerId : null, worker ? 2 : null,
            plan ? PlanId : null, plan ? OperationId : null,
            payload);
    }

    private static JsonElement OperationResult(string operation) => operation switch
    {
        "invoke_background" => Object(("public_job_id", 5), ("job_capability", Token)),
        "reset" or "session_open" or "session_close" or "session_restart" => Object(
            ("alias", "default"),
            ("state", "ready"),
            ("worker_boot_id", WorkerId),
            ("worker_generation", 2),
            ("transition_version", 1),
            ("ready_for_effects", true),
            ("warm_state_lost", false),
            ("bootstrap_state", "restored")),
        _ => Object(("text", "ok")),
    };

    private static JsonElement DispatchCapability(Guid callId) => Object(
        ("token", Token),
        ("call_id", callId),
        ("expires_unix_time_milliseconds", 100));

    private static JsonElement OutputCapability() => Object(
        ("token", Token),
        ("maximum_bytes", 1024),
        ("expires_unix_time_milliseconds", 100));

    private static GuardianHostRawEnvelope Request(
        string method,
        long? deadline,
        string? alias,
        long? transition,
        Guid? workerBootId,
        long? workerGeneration,
        Guid? planId,
        Guid? operationId,
        JsonElement payload) => GuardianHostRawProtocol.Create(
            GuardianHostMessageKind.Request,
            GuardianId,
            HostId,
            1,
            ("request_id", 1),
            ("method", method),
            ("deadline_unix_time_milliseconds", deadline),
            ("session_alias", alias),
            ("session_transition_version", transition),
            ("worker_boot_id", workerBootId),
            ("worker_generation", workerGeneration),
            ("plan_id", planId),
            ("operation_id", operationId),
            ("payload", payload));

    private static GuardianHostRawEnvelope Event(
        string eventType,
        long? requestId,
        string? alias,
        long? transition,
        Guid? workerBootId,
        long? workerGeneration,
        Guid? planId,
        Guid? operationId,
        JsonElement payload) => GuardianHostRawProtocol.Create(
            GuardianHostMessageKind.Event,
            GuardianId,
            HostId,
            1,
            ("event_sequence", 1),
            ("event_type", eventType),
            ("request_id", requestId),
            ("session_alias", alias),
            ("session_transition_version", transition),
            ("worker_boot_id", workerBootId),
            ("worker_generation", workerGeneration),
            ("plan_id", planId),
            ("operation_id", operationId),
            ("payload", payload));

    private static GuardianHostRawEnvelope ResponseOk(JsonElement payload) => GuardianHostRawProtocol.Create(
        GuardianHostMessageKind.Response,
        GuardianId,
        HostId,
        1,
        ("request_id", 1),
        ("status", "ok"),
        ("payload", payload),
        ("error", null));

    private static GuardianHostRawEnvelope ResponseError(JsonElement error) => GuardianHostRawProtocol.Create(
        GuardianHostMessageKind.Response,
        GuardianId,
        HostId,
        1,
        ("request_id", 1),
        ("status", "error"),
        ("payload", null),
        ("error", error));

    private static GuardianHostRawEnvelope RawResponse(
        string status,
        JsonElement? payload,
        JsonElement? error) => GuardianHostRawProtocol.Create(
        GuardianHostMessageKind.Response,
        GuardianId,
        HostId,
        1,
        ("request_id", 1),
        ("status", status),
        ("payload", payload),
        ("error", error));

    private static GuardianHostRawEnvelope Hello(string executableDigest) => GuardianHostRawProtocol.Create(
        GuardianHostMessageKind.Hello,
        GuardianId,
        HostId,
        1,
        ("host_pid", 42),
        ("host_executable_sha256", executableDigest),
        ("host_build_sha256", Digest),
        ("public_contract_sha256", Digest),
        ("configuration_sha256", Digest),
        ("request_channel_owned", true),
        ("event_channel_owned", true));

    private static JsonElement Object(params (string Name, object? Value)[] fields)
    {
        var ordered = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in fields) ordered.Add(name, value);
        return JsonSerializer.SerializeToElement(ordered);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Sha256Digest.Compute(bytes).Value;

    private static string SnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            if (char.IsUpper(character) && builder.Length > 0) builder.Append('_');
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static void AssertProtocolFailure(Action action)
    {
        var error = Assert.Throws<GuardianHostProtocolException>(action);
        Assert.Equal("invalid_field", error.DetailCode);
    }

    private static void AssertWireMutationFails(
        GuardianHostRawEnvelope frame,
        GuardianHostPeer sender,
        string oldValue,
        string replacement)
    {
        var encoded = Encoding.UTF8.GetString(GuardianHostRawProtocol.Encode(frame, sender));
        Assert.Equal(1, Count(encoded, oldValue));
        var mutated = encoded.Replace(oldValue, replacement, StringComparison.Ordinal);
        AssertProtocolFailure(() => GuardianHostRawProtocol.Decode(
            Encoding.UTF8.GetBytes(mutated), sender));
    }

    private static int Count(string value, string fragment)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }
        return count;
    }
}
