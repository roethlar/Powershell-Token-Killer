using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PtkMcpServer.Tests;

public sealed class McpResilienceR0ContractTests
{
    private const string ContractSha256 = "cd6aff30c64c23e5c1723250cb89935c0704edab3a70fb465a6cdb7c42fc0963";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex LowerSha256 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    [Fact]
    public void Public_recovery_and_end_state_tool_contract_are_exact()
    {
        Assert.Equal(
            ContractSha256,
            LowerHex(SHA256.HashData(File.ReadAllBytes(PathOf("contract.json")))));
        using var contract = ReadStrictJson("contract.json");
        var recovery = contract.RootElement.GetProperty("public_recovery");
        Assert.Equal(
            [
                "detail_code", "retryable", "retry_after_ms", "recovery_phase",
                "recovery_attempt", "retry_gate",
            ],
            Strings(recovery.GetProperty("property_order")));
        Assert.Equal(4096, recovery.GetProperty("maximum_utf8_bytes").GetInt32());
        Assert.True(recovery.GetProperty("call_tool_result_is_error").GetBoolean());
        Assert.False(recovery.GetProperty("structured_content").GetBoolean());
        Assert.False(recovery.GetProperty("public_delivery_state").GetBoolean());
        Assert.Equal(250, recovery.GetProperty("retry_after_ms").GetProperty("minimum").GetInt32());
        Assert.Equal(60_000, recovery.GetProperty("retry_after_ms").GetProperty("maximum").GetInt32());
        Assert.Equal(
            [
                "backend_lost_before_dispatch", "host_circuit_open",
                "host_recovering", "session_recovering",
            ],
            Strings(recovery.GetProperty("retryable_detail_codes")));
        var publicState = contract.RootElement.GetProperty("public_state");
        Assert.Equal(
            [
                "cold", "starting", "ready", "resetting", "closing", "faulted", "lost",
                "quarantined", "recovering", "backoff", "bootstrapping", "circuit_open",
                "half_open", "recovery_unknown",
            ],
            Strings(publicState.GetProperty("session_states")));
        Assert.Equal(
            ["absent", "backoff", "circuit_open", "stopped"],
            Strings(publicState.GetProperty("host_identity_by_state").GetProperty("null")));
        Assert.Equal(
            ["starting", "ready", "recovering", "containment_unconfirmed", "half_open"],
            Strings(publicState.GetProperty("host_identity_by_state").GetProperty("nonnull")));
        Assert.Equal(["cold"], Strings(publicState.GetProperty("session_identity_by_state").GetProperty("null")));
        Assert.Equal(
            ["ready", "bootstrapping", "quarantined"],
            Strings(publicState.GetProperty("session_identity_by_state").GetProperty("nonnull")));
        Assert.Equal(
            [
                "starting", "resetting", "closing", "faulted", "lost", "recovering",
                "backoff", "circuit_open", "half_open", "recovery_unknown",
            ],
            Strings(publicState.GetProperty("session_identity_by_state").GetProperty("paired_but_nullable")));

        var compactError = SerializeRecoveryVector();
        Assert.InRange(compactError.Length, 1, 4096);
        using var error = JsonDocument.Parse(compactError);
        AssertPropertyOrder(error.RootElement, Strings(recovery.GetProperty("property_order")));
        Assert.False(error.RootElement.TryGetProperty("delivery_state", out _));
        Assert.True(error.RootElement.GetProperty("retryable").GetBoolean());
        Assert.Equal("host_ready", error.RootElement.GetProperty("retry_gate").GetProperty("kind").GetString());

        using var publicContract = ReadStrictJson("public-tool-contract.json");
        Assert.Equal("ptk.public-contract/1", publicContract.RootElement.GetProperty("schema_version").GetString());
        var serverIdentity = publicContract.RootElement.GetProperty("server_identity");
        AssertPropertyOrder(serverIdentity, ["name", "version"]);
        Assert.Equal("ptk", serverIdentity.GetProperty("name").GetString());
        Assert.Equal("0.2.0", serverIdentity.GetProperty("version").GetString());
        Assert.Contains("Delay expiry alone never authorizes resubmission", publicContract.RootElement.GetProperty("instructions").GetString(), StringComparison.Ordinal);
        Assert.Equal(
            ["ptk_invoke", "ptk_job", "ptk_output", "ptk_reset", "ptk_session", "ptk_state"],
            publicContract.RootElement.GetProperty("tools_list").GetProperty("tools")
                .EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));

        foreach (var tool in publicContract.RootElement.GetProperty("tools_list").GetProperty("tools").EnumerateArray())
        {
            Assert.True(tool.GetProperty("inputSchema").ValueKind == JsonValueKind.Object);
        }
        var sessionSchema = publicContract.RootElement.GetProperty("tools_list").GetProperty("tools")
            .EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "ptk_session")
            .GetProperty("inputSchema").GetProperty("oneOf");
        Assert.Equal(["list", "open", "close", "restart"], sessionSchema.EnumerateArray()
            .Select(branch => branch.GetProperty("properties").GetProperty("action")
                .GetProperty("const").GetString()));

        var resetDescription = publicContract.RootElement.GetProperty("tools_list").GetProperty("tools")
            .EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "ptk_reset")
            .GetProperty("description").GetString()!;
        var sessionDescription = publicContract.RootElement.GetProperty("tools_list").GetProperty("tools")
            .EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "ptk_session")
            .GetProperty("description").GetString()!;
        foreach (var description in new[] { resetDescription, sessionDescription })
        {
            Assert.Contains("timeoutSeconds=0 uses the operator-configured call default", description, StringComparison.Ordinal);
            Assert.Contains("positive override is capped by the server maximum", description, StringComparison.Ordinal);
            Assert.Contains("earlier frozen template startup ceiling", description, StringComparison.Ordinal);
            Assert.Contains("selected deadline plus 10 seconds", description, StringComparison.Ordinal);
        }

        using var recoverySchema = ReadStrictJson("public-recovery.schema.json");
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", recoverySchema.RootElement.GetProperty("$schema").GetString());
        Assert.False(recoverySchema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            Strings(recovery.GetProperty("detail_codes")),
            Strings(recoverySchema.RootElement.GetProperty("$defs").GetProperty("detail_code").GetProperty("enum")));
        Assert.Equal(4096, recoverySchema.RootElement.GetProperty("x-ptk-maximum-compact-utf8-bytes").GetInt32());
        var sessionRecoveryGuard = recoverySchema.RootElement.GetProperty("allOf").EnumerateArray()
            .Single(guard => guard.GetProperty("if").GetProperty("properties")
                .GetProperty("detail_code").GetProperty("const").GetString() == "session_recovering");
        Assert.Equal("session_recovering", sessionRecoveryGuard.GetProperty("if").GetProperty("properties")
            .GetProperty("detail_code").GetProperty("const").GetString());
        Assert.Equal("#/$defs/session_ready_gate", sessionRecoveryGuard.GetProperty("then")
            .GetProperty("properties").GetProperty("retry_gate").GetProperty("$ref").GetString());

        using var stateSchema = ReadStrictJson("public-state.schema.json");
        Assert.False(stateSchema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(128, stateSchema.RootElement.GetProperty("properties").GetProperty("sessions")
            .GetProperty("maxItems").GetInt32());
        Assert.Equal(
            Strings(contract.RootElement.GetProperty("public_state").GetProperty("session_states")),
            Strings(stateSchema.RootElement.GetProperty("$defs").GetProperty("session")
                .GetProperty("properties").GetProperty("state").GetProperty("enum")));
        AssertObjectSchemaClosed(stateSchema.RootElement.GetProperty("$defs").GetProperty("host"));
        AssertObjectSchemaClosed(stateSchema.RootElement.GetProperty("$defs").GetProperty("session"));
    }

    [Fact]
    public void Public_recovery_schema_closes_detail_phase_pairs()
    {
        using var schema = ReadStrictJson("public-recovery.schema.json");
        var phaseContract = schema.RootElement.GetProperty("x-ptk-detail-code-contract");
        var allPhases = new[] { "attempting", "backoff", "bootstrap", "circuit_open", "containment", "half_open" };
        Assert.Equal(allPhases, Strings(phaseContract.GetProperty("backend_lost_before_dispatch")
            .GetProperty("allowed_recovery_phases")));
        Assert.Equal(["circuit_open"], Strings(phaseContract.GetProperty("host_circuit_open")
            .GetProperty("allowed_recovery_phases")));
        var activeHostPhases = new[] { "attempting", "backoff", "bootstrap", "containment", "half_open" };
        Assert.Equal(activeHostPhases, Strings(phaseContract.GetProperty("host_recovering")
            .GetProperty("allowed_recovery_phases")));
        Assert.Equal(allPhases, Strings(phaseContract.GetProperty("session_recovering")
            .GetProperty("allowed_recovery_phases")));

        var guards = schema.RootElement.GetProperty("allOf").EnumerateArray().ToArray();
        Assert.Equal(3, guards.Length);
        var circuit = guards.Single(guard => GuardDetailCode(guard) == "host_circuit_open");
        Assert.Equal("circuit_open", circuit.GetProperty("then").GetProperty("properties")
            .GetProperty("recovery_phase").GetProperty("const").GetString());
        var host = guards.Single(guard => GuardDetailCode(guard) == "host_recovering");
        Assert.Equal(activeHostPhases, Strings(host.GetProperty("then").GetProperty("properties")
            .GetProperty("recovery_phase").GetProperty("enum")));
    }

    [Fact]
    public void Public_state_schema_closes_state_phase_and_readiness_combinations()
    {
        using var stateSchema = ReadStrictJson("public-state.schema.json");
        var definitions = stateSchema.RootElement.GetProperty("$defs");

        var hostRules = StateRules(definitions.GetProperty("host"));
        Assert.Equal(8, hostRules.Length);
        AssertStateRule(hostRules[0], ["absent", "containment_unconfirmed", "stopped"], null, false);
        AssertStateRule(hostRules[1], ["starting"], null, false, attemptIsZero: true);
        AssertStateRule(hostRules[2], ["starting"], ["attempting"], false, automatic: true);
        AssertStateRule(hostRules[3], ["ready"], null, true);
        AssertStateRule(hostRules[4], ["recovering"], ["containment", "attempting", "bootstrap"], false, automatic: true);
        AssertStateRule(hostRules[5], ["backoff"], ["backoff"], false, automatic: true);
        AssertStateRule(hostRules[6], ["circuit_open"], ["circuit_open"], false, automatic: true);
        AssertStateRule(hostRules[7], ["half_open"], ["half_open"], false, automatic: true);

        var sessionRules = StateRules(definitions.GetProperty("session"));
        Assert.Equal(7, sessionRules.Length);
        AssertStateRule(
            sessionRules[0],
            ["cold", "starting", "resetting", "closing", "faulted", "lost", "quarantined", "recovery_unknown"],
            null,
            false);
        AssertStateRule(sessionRules[1], ["ready"], null, true);
        AssertStateRule(sessionRules[2], ["recovering"], ["containment", "attempting"], false, automatic: true);
        AssertStateRule(sessionRules[3], ["backoff"], ["backoff"], false, automatic: true);
        AssertStateRule(sessionRules[4], ["bootstrapping"], ["bootstrap"], false, automatic: true);
        AssertStateRule(sessionRules[5], ["circuit_open"], ["circuit_open"], false, automatic: true);
        AssertStateRule(sessionRules[6], ["half_open"], ["half_open"], false, automatic: true);
    }

    [Fact]
    public void Public_state_schema_closes_identity_state_combinations()
    {
        using var stateSchema = ReadStrictJson("public-state.schema.json");
        var definitions = stateSchema.RootElement.GetProperty("$defs");

        var hostRules = IdentityRules(definitions.GetProperty("host"));
        Assert.Equal(2, hostRules.Length);
        AssertIdentityRule(hostRules[0], ["absent", "backoff", "circuit_open", "stopped"], "boot_id", false);
        AssertIdentityRule(
            hostRules[1],
            ["starting", "ready", "recovering", "containment_unconfirmed", "half_open"],
            "boot_id",
            true);

        var sessionRules = IdentityRules(definitions.GetProperty("session"));
        Assert.Equal(3, sessionRules.Length);
        AssertIdentityRule(sessionRules[0], ["cold"], "worker_boot_id", false);
        AssertIdentityRule(sessionRules[1], ["ready", "bootstrapping", "quarantined"], "worker_boot_id", true);
        AssertIdentityRule(
            sessionRules[2],
            [
                "starting", "resetting", "closing", "faulted", "lost", "recovering",
                "backoff", "circuit_open", "half_open", "recovery_unknown",
            ],
            "worker_boot_id",
            null);
    }

    [Fact]
    public void Public_state_sessions_require_unique_strictly_ordered_aliases()
    {
        const string order = "alias_ordinal_utf8_strictly_increasing_unique";
        using var contract = ReadStrictJson("contract.json");
        Assert.Equal(order, contract.RootElement.GetProperty("public_state")
            .GetProperty("alias_order").GetString());

        using var schema = ReadStrictJson("public-state.schema.json");
        Assert.Equal(order, schema.RootElement.GetProperty("properties")
            .GetProperty("sessions").GetProperty("x-ptk-order").GetString());
        Assert.True(SessionAliasesStrictlyIncrease(["a", "b"]));
        Assert.False(SessionAliasesStrictlyIncrease(["a", "a"]));
        Assert.False(SessionAliasesStrictlyIncrease(["b", "a"]));
    }

    [Fact]
    public void Splunk_fixture_disables_transport_compression_for_exact_wire_vector()
    {
        var fixture = File.ReadAllText(PathOf("splunk-hec-fixture.yaml"), StrictUtf8);
        var setting = Assert.Single(
            fixture.Split('\n'),
            line => line.TrimStart().StartsWith("disable_compression:", StringComparison.Ordinal));
        Assert.Equal("    disable_compression: true", setting);
    }

    [Fact]
    public void Invoke_description_discloses_one_absolute_timeout_and_containment_bound()
    {
        using var publicContract = ReadStrictJson("public-tool-contract.json");
        var description = publicContract.RootElement.GetProperty("tools_list").GetProperty("tools")
            .EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "ptk_invoke")
            .GetProperty("description").GetString()!;

        Assert.Contains("timeoutSeconds=0 uses the operator-configured call default", description, StringComparison.Ordinal);
        Assert.Contains("a positive override is capped by the server maximum", description, StringComparison.Ordinal);
        Assert.Contains(
            "One absolute deadline covers lazy session startup/bootstrap, queueing, routing, foreground execution or background start, and result shaping",
            description,
            StringComparison.Ordinal);
        Assert.Contains("earlier frozen template startup ceiling", description, StringComparison.Ordinal);
        Assert.Contains("containment may extend the response by at most 10 seconds", description, StringComparison.Ordinal);
        Assert.Contains("maximum wall time is the selected deadline plus 10 seconds", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Private_v1_cross_frame_capability_and_terminal_ownership_are_closed()
    {
        using var schema = ReadStrictJson("guardian-host-protocol.schema.json");
        var responseMap = schema.RootElement.GetProperty("x-ptk-response-type-by-request");
        Assert.Equal(
            "no_response; target_request_id original request owns the sole terminal",
            responseMap.GetProperty("cancel").GetString());

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["operation_call_id"] = "operation_request.call_id=operation_request.dispatch_capability.call_id",
            ["cancel_request_id"] = "anti_replay_and_ordering_only; never response-owning",
            ["cancel_terminal_owner"] = "target_request_id original request owns the sole terminal",
            ["operation_response"] = "operation_completed.operation=originating_operation_request.operation",
            ["delivery_event"] = "operation_delivery.dispatch_token=originating_operation_request.dispatch_capability.token",
            ["control_ack"] = "control_acknowledged.source_event_sequence=originating_guardian_request.payload.source_event_sequence",
        };
        var schemaInvariants = schema.RootElement.GetProperty("x-ptk-cross-frame-invariants");
        Assert.Equal(expected.Keys, schemaInvariants.EnumerateObject().Select(property => property.Name));
        Assert.All(expected, pair => Assert.Equal(pair.Value, schemaInvariants.GetProperty(pair.Key).GetString()));

        var definitions = schema.RootElement.GetProperty("$defs");
        var cancel = definitions.GetProperty("cancel");
        Assert.Equal(expected["cancel_request_id"], cancel.GetProperty("x-ptk-request-id-purpose").GetString());
        Assert.Equal(expected["cancel_terminal_owner"], cancel.GetProperty("x-ptk-terminal-owner").GetString());
        Assert.Equal("call_id=dispatch_capability.call_id", definitions.GetProperty("operation_request")
            .GetProperty("x-ptk-invariant").GetString());
        Assert.Equal("dispatch_token=originating_operation_request.dispatch_capability.token",
            definitions.GetProperty("operation_delivery").GetProperty("x-ptk-cross-frame-invariant").GetString());
        Assert.Equal("operation=originating_operation_request.operation",
            definitions.GetProperty("operation_completed").GetProperty("x-ptk-cross-frame-invariant").GetString());
        Assert.Equal("source_event_sequence=originating_guardian_request.payload.source_event_sequence",
            definitions.GetProperty("control_acknowledged").GetProperty("x-ptk-cross-frame-invariant").GetString());

        using var protocol = ReadStrictJson("guardian-host-protocol.json");
        var protocolInvariants = protocol.RootElement.GetProperty("cross_frame_correlation");
        Assert.Equal(expected.Keys, protocolInvariants.EnumerateObject().Select(property => property.Name));
        Assert.All(expected, pair => Assert.Equal(pair.Value, protocolInvariants.GetProperty(pair.Key).GetString()));
        var cancelRules = protocol.RootElement.GetProperty("kinds").EnumerateArray()
            .Single(kind => kind.GetProperty("kind").GetString() == "cancel").GetProperty("rules");
        Assert.Equal("anti_replay_and_ordering_only_no_response", cancelRules.GetProperty("request_id").GetString());
        Assert.Equal("target_request_id_original_request_only", cancelRules.GetProperty("terminal_owner").GetString());
    }

    [Fact]
    public void Private_v1_protocol_freezes_handshake_correlation_and_bounded_manifest_transfer()
    {
        using var protocol = ReadStrictJson("guardian-host-protocol.json");
        var root = protocol.RootElement;
        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        var transport = root.GetProperty("transport");
        Assert.Equal("strict_utf8", transport.GetProperty("encoding").GetString());
        Assert.False(transport.GetProperty("bom").GetBoolean());
        Assert.Equal("object_only", transport.GetProperty("json_root").GetString());
        Assert.Equal("ndjson_exactly_one_lf", transport.GetProperty("framing").GetString());
        Assert.Equal("reject", transport.GetProperty("carriage_return").GetString());
        Assert.Equal("reject", transport.GetProperty("empty_frame").GetString());
        Assert.Equal("reject", transport.GetProperty("unterminated_final_frame").GetString());
        Assert.Equal(1_048_576, transport.GetProperty("maximum_encoded_frame_bytes_excluding_lf").GetInt32());
        Assert.Equal(32, transport.GetProperty("maximum_json_depth").GetInt32());
        Assert.Equal(65_536, transport.GetProperty("host_stdout_maximum_bytes_per_boot").GetInt32());
        Assert.Equal(65_536, transport.GetProperty("host_stderr_maximum_bytes_per_boot").GetInt32());

        Assert.Equal(
            [
                "host_to_guardian:hello",
                "guardian_to_host:initialize",
                "guardian_to_host:request:manifest_header",
                "host_to_guardian:response:manifest_header_accepted",
                "guardian_to_host:request:manifest_chunk",
                "host_to_guardian:response:manifest_chunk_accepted",
                "repeat_manifest_chunk_request_then_acceptance_until_chunk_count",
                "guardian_to_host:request:manifest_seal",
                "host_to_guardian:response:manifest_sealed",
                "host_to_guardian:ready",
            ],
            Strings(root.GetProperty("handshake").GetProperty("sequence")));
        Assert.Equal("generation_fatal", root.GetProperty("handshake")
            .GetProperty("operational_request_before_ready").GetString());

        var kinds = root.GetProperty("kinds").EnumerateArray().ToArray();
        Assert.Equal(
            ["hello", "initialize", "ready", "request", "cancel", "event", "response", "shutdown"],
            kinds.Select(kind => kind.GetProperty("kind").GetString()));
        Assert.Equal("host_to_guardian", kinds[0].GetProperty("direction").GetString());
        Assert.Equal("guardian_to_host", kinds[1].GetProperty("direction").GetString());
        Assert.Equal(
            [
                "host_to_guardian", "guardian_to_host", "host_to_guardian",
                "guardian_to_host", "guardian_to_host", "host_to_guardian",
                "host_to_guardian", "guardian_to_host",
            ],
            kinds.Select(kind => kind.GetProperty("direction").GetString()));
        foreach (var kind in kinds)
        {
            var fields = Strings(kind.GetProperty("property_order_after_common"));
            Assert.Equal(fields.Length, fields.Distinct(StringComparer.Ordinal).Count());
        }

        var manifest = root.GetProperty("manifest_transfer");
        Assert.Equal(25_165_824, manifest.GetProperty("maximum_total_raw_bytes").GetInt32());
        Assert.Equal(524_288, manifest.GetProperty("maximum_chunk_raw_bytes").GetInt32());
        Assert.Equal(48, manifest.GetProperty("maximum_chunks").GetInt32());
        Assert.Equal(128, manifest.GetProperty("maximum_aliases").GetInt32());
        Assert.Equal(128, manifest.GetProperty("maximum_templates").GetInt32());
        var maximumBase64Bytes = 4 * ((524_288 + 2) / 3);
        Assert.True(maximumBase64Bytes < 1_048_576);
        Assert.Equal("manifest_seal_response_ok_then_ready", manifest.GetProperty("commit").GetString());
        Assert.Equal("recovery-manifest.schema.json", manifest.GetProperty("machine_schema").GetString());

        var requestMethods = root.GetProperty("request_methods").EnumerateArray().ToArray();
        Assert.Equal(
            [
                "manifest_header", "manifest_chunk", "manifest_seal", "operation",
                "worker_create_capability_grant", "worker_containment_pending_ack",
                "worker_containment_armed_ack", "worker_containment_remove_ack",
            ],
            requestMethods.Select(method => method.GetProperty("name").GetString()));
        Assert.All(requestMethods, method =>
            Assert.Equal("guardian_to_host", method.GetProperty("direction").GetString()));
        Assert.Equal(
            ["manifest_id", "chunk_index", "offset", "raw_bytes", "raw_base64", "raw_sha256"],
            Strings(requestMethods[1].GetProperty("payload_property_order")));
        var operationMethod = requestMethods.Single(method => method.GetProperty("name").GetString() == "operation");
        Assert.Equal(
            [
                "invoke_foreground", "invoke_background", "job_list", "job_status", "job_output",
                "job_kill", "reset", "session_open", "session_close", "session_restart",
            ],
            Strings(operationMethod.GetProperty("operations")));
        Assert.Equal(
            [
                "ptk_output:read|search|status", "ptk_state", "ptk_session:list",
                "ptk_job:status|output|list:sealed_or_tombstoned",
            ],
            Strings(operationMethod.GetProperty("guardian_local_public_operations")));

        var controlEvents = root.GetProperty("host_control_events").EnumerateArray().ToArray();
        Assert.Equal(
            [
                "worker_create_capability_requested", "worker_containment_pending",
                "worker_containment_armed", "worker_containment_remove_requested",
            ],
            controlEvents.Select(hostEvent => hostEvent.GetProperty("name").GetString()));
        Assert.All(controlEvents, hostEvent =>
            Assert.Equal("host_to_guardian", hostEvent.GetProperty("direction").GetString()));
        Assert.Equal(
            ["binding_digest", "startup_deadline_unix_time_milliseconds"],
            Strings(controlEvents[0].GetProperty("payload_property_order")));
        Assert.Equal(
            [
                "broker_pid", "broker_start_identity_high", "broker_start_identity_low",
                "worker_pid", "worker_start_identity_high", "worker_start_identity_low",
                "intended_pgid",
            ],
            Strings(controlEvents[1].GetProperty("payload_property_order")));
        Assert.Equal(
            [
                "broker_pid", "broker_start_identity_high", "broker_start_identity_low",
                "worker_pid", "worker_start_identity_high", "worker_start_identity_low", "pgid",
            ],
            Strings(controlEvents[2].GetProperty("payload_property_order")));
        Assert.Equal(
            Strings(controlEvents[2].GetProperty("payload_property_order")),
            Strings(controlEvents[3].GetProperty("payload_property_order")));

        var correlation = root.GetProperty("event_request_correlation");
        Assert.Equal("guardian_originated_only", correlation.GetProperty("request_response_id_domain").GetString());
        var pairs = correlation.GetProperty("pairs").EnumerateArray().ToArray();
        Assert.Equal(controlEvents.Length, pairs.Length);
        Assert.Equal(
            controlEvents.Select(hostEvent => hostEvent.GetProperty("name").GetString()),
            pairs.Select(pair => pair.GetProperty("event").GetString()));
        Assert.Equal(
            requestMethods.Skip(4).Select(method => method.GetProperty("name").GetString()),
            pairs.Select(pair => pair.GetProperty("request").GetString()));
        Assert.All(requestMethods.Skip(4), method =>
            Assert.Equal("source_event_sequence", Strings(method.GetProperty("payload_property_order"))[0]));
        Assert.Equal(
            ["source_event_sequence", "token", "worker_generation"],
            Strings(requestMethods[4].GetProperty("payload_property_order")));
        Assert.All(requestMethods.Skip(5), method =>
            Assert.Equal(["source_event_sequence"], Strings(method.GetProperty("payload_property_order"))));
        Assert.Equal(
            [
                "operation_delivery", "session_lifecycle", "worker_lost", "worker_diagnostic_chunk",
                "worker_diagnostic_truncated", "job_lifecycle", "output_chunk", "output_seal",
            ],
            Strings(root.GetProperty("host_operational_events")));
        Assert.Equal(
            ["not_dispatched", "write_started", "terminal_decoded"],
            Strings(root.GetProperty("operation_delivery_states")));
        Assert.Equal(
            [
                "manifest_header_accepted", "manifest_chunk_accepted", "manifest_sealed",
                "operation_completed", "control_acknowledged", "shutdown_accepted",
            ],
            Strings(root.GetProperty("response_payloads")));

        var identity = root.GetProperty("identity");
        Assert.Equal(1, identity.GetProperty("request_id_minimum").GetInt64());
        Assert.Equal(long.MaxValue, identity.GetProperty("request_id_maximum").GetInt64());
        Assert.Equal("generation_fatal_without_wraparound", identity.GetProperty("exhaustion").GetString());
        Assert.Equal(
            [
                "guardian_boot_id", "host_boot_id", "host_generation", "request_id",
                "session_transition_version", "worker_boot_id", "worker_generation",
                "plan_id", "operation_id",
            ],
            Strings(root.GetProperty("late_frame_validation_dimensions")));

        Assert.Equal("guardian-host-protocol.schema.json", root.GetProperty("machine_schema").GetString());
        using var protocolSchema = ReadStrictJson("guardian-host-protocol.schema.json");
        var definitions = protocolSchema.RootElement.GetProperty("$defs");
        Assert.Equal(8, protocolSchema.RootElement.GetProperty("oneOf").GetArrayLength());
        Assert.Equal(
            requestMethods.Select(method => method.GetProperty("name").GetString()),
            Strings(definitions.GetProperty("request").GetProperty("properties").GetProperty("method").GetProperty("enum")));
        Assert.Equal(
            controlEvents.Select(hostEvent => hostEvent.GetProperty("name").GetString())
                .Concat(Strings(root.GetProperty("host_operational_events"))),
            Strings(definitions.GetProperty("event").GetProperty("properties").GetProperty("event_type").GetProperty("enum")));
        Assert.Equal(
            Strings(operationMethod.GetProperty("operations")),
            Strings(definitions.GetProperty("operation_request").GetProperty("properties")
                .GetProperty("operation").GetProperty("enum")));
        Assert.Equal(524_288, definitions.GetProperty("manifest_chunk").GetProperty("properties")
            .GetProperty("raw_bytes").GetProperty("maximum").GetInt32());
        Assert.Contains("every_nonfinal_chunk_has_raw_bytes=524288",
            Strings(definitions.GetProperty("manifest_chunk").GetProperty("x-ptk-invariants")));
        Assert.Equal(16_384, definitions.GetProperty("worker_diagnostic_chunk").GetProperty("properties")
            .GetProperty("raw_bytes").GetProperty("maximum").GetInt32());
        Assert.Equal(65_536, definitions.GetProperty("output_chunk").GetProperty("properties")
            .GetProperty("raw_bytes").GetProperty("maximum").GetInt32());
        Assert.Equal(
            ["not_dispatched", "write_started", "terminal_decoded"],
            Strings(definitions.GetProperty("operation_delivery").GetProperty("properties")
                .GetProperty("delivery_state").GetProperty("enum")));
        Assert.Equal(37, definitions.GetProperty("private_error").GetProperty("properties")
            .GetProperty("detail_code").GetProperty("enum").GetArrayLength());
        var mappedPrivateDetails = definitions.GetProperty("private_error").GetProperty("oneOf")
            .EnumerateArray().SelectMany(branch =>
            {
                var detail = branch.GetProperty("properties").GetProperty("detail_code");
                return detail.TryGetProperty("const", out var one)
                    ? [one.GetString()!]
                    : Strings(detail.GetProperty("enum"));
            }).ToArray();
        Assert.Equal(
            Strings(definitions.GetProperty("private_error").GetProperty("properties")
                .GetProperty("detail_code").GetProperty("enum")).Order(StringComparer.Ordinal),
            mappedPrivateDetails.Order(StringComparer.Ordinal));
        foreach (var definition in definitions.EnumerateObject()
                     .Where(definition => definition.Value.TryGetProperty("type", out var type) &&
                                          type.GetString() == "object"))
        {
            AssertObjectSchemaClosed(definition.Value);
        }

        using var recoveryManifestSchema = ReadStrictJson("recovery-manifest.schema.json");
        using var recoveryManifest = ReadStrictJson("recovery-manifest.example.json");
        AssertObjectSchemaClosed(recoveryManifestSchema.RootElement);
        AssertPropertyOrder(recoveryManifest.RootElement,
            Strings(recoveryManifestSchema.RootElement.GetProperty("x-ptk-property-order")));
        var manifestDefinitions = recoveryManifestSchema.RootElement.GetProperty("$defs");
        var templateOrder = Strings(manifestDefinitions.GetProperty("template").GetProperty("x-ptk-property-order"));
        Assert.Equal(
            [
                "name", "description", "startup_timeout_seconds", "declared_target", "declared_identity",
                "allow_cold_background", "template_digest", "bootstrap_digest", "bootstrap_raw_base64",
            ],
            templateOrder);
        var bindingOrder = Strings(manifestDefinitions.GetProperty("binding").GetProperty("x-ptk-property-order"));
        Assert.Equal(
            [
                "alias", "binding_kind", "template_name", "template_digest", "bootstrap_digest",
                "allow_cold_background", "desired_state", "transition_version", "binding_digest",
            ],
            bindingOrder);
        Assert.Equal(["alias", "generation"], Strings(manifestDefinitions
            .GetProperty("worker_generation_high_watermark").GetProperty("x-ptk-property-order")));
        var defaultBinding = Assert.Single(recoveryManifest.RootElement.GetProperty("bindings").EnumerateArray());
        AssertPropertyOrder(defaultBinding, bindingOrder);
        Assert.Equal("default", defaultBinding.GetProperty("alias").GetString());
        Assert.Equal("default", defaultBinding.GetProperty("binding_kind").GetString());
        Assert.Equal(JsonValueKind.Null, defaultBinding.GetProperty("template_name").ValueKind);
    }

    [Fact]
    public void Audit_v3_vectors_have_exact_shape_hash_and_host_semantics()
    {
        using var contract = ReadStrictJson("contract.json");
        var audit = contract.RootElement.GetProperty("audit_v3");
        Assert.Equal("stopped", audit.GetProperty("permanent_failure_without_live_host_state").GetString());
        Assert.Equal(
            ["boot_id", "generation", "state", "recovery_attempt"],
            Strings(audit.GetProperty("host_property_order")));

        var nullVector = ReadAuditVector("audit-v3-null.jsonl");
        var liveVector = ReadAuditVector("audit-v3-host.jsonl");
        using (nullVector.Document)
        using (liveVector.Document)
        {
            AssertAuditEnvelope(nullVector);
            AssertAuditEnvelope(liveVector);
            var addedEventTypes = Strings(audit.GetProperty("added_event_types"));
            Assert.Contains(nullVector.Document.RootElement.GetProperty("event_type").GetString(), addedEventTypes);
            Assert.Contains(liveVector.Document.RootElement.GetProperty("event_type").GetString(), addedEventTypes);

            var absentHost = nullVector.Document.RootElement.GetProperty("host");
            Assert.Equal(JsonValueKind.Null, absentHost.GetProperty("boot_id").ValueKind);
            Assert.Equal(JsonValueKind.Null, absentHost.GetProperty("generation").ValueKind);
            Assert.Equal("stopped", absentHost.GetProperty("state").GetString());
            Assert.Equal(0, absentHost.GetProperty("recovery_attempt").GetInt64());

            var liveHost = liveVector.Document.RootElement.GetProperty("host");
            Assert.Equal(Guid.Parse("33333333-3333-4333-8333-333333333333"), liveHost.GetProperty("boot_id").GetGuid());
            Assert.Equal(long.MaxValue, liveHost.GetProperty("generation").GetInt64());
            Assert.Equal(long.MaxValue, liveHost.GetProperty("recovery_attempt").GetInt64());
            Assert.Equal("λ", liveVector.Document.RootElement.GetProperty("actor")
                .GetProperty("client_name").GetString()![^1..]);
        }
    }

    [Fact]
    public void Package_manifest_digests_cover_the_exact_host_closure()
    {
        using var contract = ReadStrictJson("contract.json");
        using var manifest = ReadStrictJson("package-manifest.example.json");
        using var schema = ReadStrictJson("package-manifest.schema.json");
        var root = manifest.RootElement;

        Assert.Equal("ptk.package-manifest/1", root.GetProperty("schema_version").GetString());
        Assert.Equal(1, root.GetProperty("private_protocol_version").GetInt32());
        Assert.Equal("ptk.package-manifest/1", schema.RootElement.GetProperty("properties")
            .GetProperty("schema_version").GetProperty("const").GetString());

        var digestContract = contract.RootElement.GetProperty("digests").GetProperty("package_manifest");
        Assert.Equal("package-manifest.schema.json#/x-ptk-runtime-invariants",
            digestContract.GetProperty("runtime_invariants_schema").GetString());
        Assert.True(digestContract.GetProperty("runtime_validation_required_before_activation").GetBoolean());
        AssertPropertyOrder(root, Strings(digestContract.GetProperty("property_order")));
        var exactManifest = File.ReadAllBytes(PathOf("package-manifest.example.json"));
        _ = StrictUtf8.GetString(exactManifest);
        Assert.Equal((byte)'\n', exactManifest[^1]);
        Assert.Equal(1, exactManifest.Count(value => value == (byte)'\n'));
        Assert.DoesNotContain((byte)'\r', exactManifest);
        Assert.Equal(digestContract.GetProperty("example_raw_utf8_bytes").GetInt32(), exactManifest.Length);
        Assert.Equal("exact_raw_manifest_file_bytes_including_final_lf", digestContract.GetProperty("input").GetString());
        Assert.Equal("one_compact_strict_utf8_json_object_then_exactly_one_lf", digestContract.GetProperty("serialization").GetString());
        Assert.False(digestContract.GetProperty("parse_and_reserialize_before_hash").GetBoolean());
        Assert.Equal(
            digestContract.GetProperty("example_digest").GetString(),
            DomainHash("ptk.package-manifest/1", exactManifest));
        var files = root.GetProperty("files").EnumerateArray().ToArray();
        Assert.All(files, file => AssertPropertyOrder(
            file,
            Strings(digestContract.GetProperty("file_property_order"))));
        var paths = files.Select(file => file.GetProperty("path").GetString()!).ToArray();
        var pathPattern = new Regex(
            schema.RootElement.GetProperty("$defs").GetProperty("file").GetProperty("properties")
                .GetProperty("path").GetProperty("pattern").GetString()!,
            RegexOptions.CultureInvariant);
        Assert.Equal(paths.Order(StringComparer.Ordinal), paths);
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
        Assert.All(paths, path => Assert.Matches(pathPattern, path));
        Assert.All(
            new[] { "/absolute", "../escape", "bin/../escape", "bin//file", "bin/", ".", "..", "C:\\file" },
            path => Assert.DoesNotMatch(pathPattern, path));
        foreach (var file in files)
        {
            var path = file.GetProperty("path").GetString()!;
            Assert.False(Path.IsPathFullyQualified(path));
            Assert.DoesNotContain('\\', path);
            Assert.DoesNotContain("//", path, StringComparison.Ordinal);
            Assert.DoesNotContain(path.Split('/'), part => part is "." or "..");
            Assert.Matches(LowerSha256, file.GetProperty("sha256").GetString()!);
        }

        var expectedPublicDigest = root.GetProperty("public_contract_sha256").GetString()!;
        Assert.Equal(expectedPublicDigest, DomainHash("ptk.public-contract/1", ReadWithoutFinalLf("public-tool-contract.json")));

        var build = contract.RootElement.GetProperty("digests").GetProperty("host_build");
        var roles = Strings(build.GetProperty("roles")).ToHashSet(StringComparer.Ordinal);
        var expectedHostDigest = root.GetProperty("host_build_sha256").GetString();
        Assert.Equal(expectedHostDigest, ComputeHostBuildDigest(files, roles));
        Assert.Contains(files, file => file.GetProperty("role").GetString() == "host_managed");
        Assert.Contains(files, file => file.GetProperty("role").GetString() == "shared_contract");
        Assert.Contains(files, file => file.GetProperty("role").GetString() == "guardian_helper");
        Assert.Contains(files, file => file.GetProperty("role").GetString() == "containment_helper");

        var layout = contract.RootElement.GetProperty("published_layout");
        Assert.Equal("bin/ptk-package-manifest.json", layout.GetProperty("package_manifest").GetString());
        Assert.True(layout.GetProperty("manifest_is_single_binary_identity_authority").GetBoolean());
        Assert.False(layout.GetProperty("separate_containment_helper_manifest").GetBoolean());
        Assert.Equal("package-manifest.schema.json#/x-ptk-required-roles-by-rid",
            layout.GetProperty("required_roles").GetString());
    }

    [Fact]
    public void Package_manifest_schema_closes_required_runtime_identity()
    {
        using var schema = ReadStrictJson("package-manifest.schema.json");
        var required = schema.RootElement.GetProperty("x-ptk-required-roles-by-rid");
        var allRoles = new[]
        {
            "audit_admin", "guardian_apphost", "guardian_managed", "host_apphost", "host_managed",
            "host_runtime", "module", "script", "shared_contract", "version",
        };
        var unixRoles = new[] { "containment_helper", "guardian_helper" };
        Assert.Equal(allRoles, Strings(required.GetProperty("all")));
        Assert.Equal(unixRoles, Strings(required.GetProperty("unix")));
        Assert.Equal(
            [
                "files_paths_are_strictly_increasing_ordinal_utf8_and_unique",
                "selected_rid_required_roles_each_appear_exactly_once",
                "VERSION_role_path_is_VERSION_and_exact_strict_utf8_file_bytes_equal_package_version_without_bom_or_terminator",
                "public_contract_sha256=recompute_using_contract.json#/digests/public_contract",
                "host_build_sha256=recompute_using_contract.json#/digests/host_build",
            ],
            Strings(schema.RootElement.GetProperty("x-ptk-runtime-invariants")));
        var filesSchema = schema.RootElement.GetProperty("properties").GetProperty("files");
        Assert.True(filesSchema.GetProperty("uniqueItems").GetBoolean());
        Assert.Equal("path_ordinal_utf8_strictly_increasing_unique", filesSchema.GetProperty("x-ptk-order").GetString());

        var closure = schema.RootElement.GetProperty("allOf").EnumerateArray().ToArray();
        Assert.Equal(allRoles.Length + 1, closure.Length);
        Assert.Equal(allRoles, closure.Take(allRoles.Length).Select(RequiredRole));
        var versionContains = closure[allRoles.Length - 1].GetProperty("properties").GetProperty("files")
            .GetProperty("contains").GetProperty("properties");
        Assert.Equal("VERSION", versionContains.GetProperty("path").GetProperty("const").GetString());

        var unix = closure[^1];
        Assert.Equal(["linux-x64", "linux-arm64", "osx-arm64"], Strings(unix.GetProperty("if")
            .GetProperty("properties").GetProperty("rid").GetProperty("enum")));
        Assert.Equal(unixRoles, unix.GetProperty("then").GetProperty("allOf")
            .EnumerateArray().Select(RequiredRole));

        using var example = ReadStrictJson("package-manifest.example.json");
        var exampleFiles = example.RootElement.GetProperty("files");
        var exampleRoles = exampleFiles.EnumerateArray()
            .Select(file => file.GetProperty("role").GetString()!).ToArray();
        Assert.Equal(allRoles.Concat(unixRoles).Order(StringComparer.Ordinal),
            exampleRoles.Order(StringComparer.Ordinal));
        Assert.True(PackagePathsStrictlyIncrease(exampleFiles.EnumerateArray()
            .Select(file => file.GetProperty("path").GetString()!)));
        Assert.False(PackagePathsStrictlyIncrease(["VERSION", "VERSION"]));
        Assert.False(PackagePathsStrictlyIncrease(["bin/z", "bin/a"]));
    }

    [Fact]
    public void Sentinel_static_vector_derives_every_column_from_raw_event()
    {
        using var validation = ReadStrictJson("adapter-live-validation.json");
        var projection = validation.RootElement.GetProperty("sentinel_static_projection_validation");
        Assert.Equal("passed", projection.GetProperty("status").GetString());
        Assert.Equal("static_semantic_projection", projection.GetProperty("evidence_kind").GetString());
        Assert.Equal(
            "resolve_each_frozen_column_source_from_the_exact_RawEvent_and_compare_types_and_values",
            projection.GetProperty("method").GetString());
        Assert.Equal("all_19_DCR_columns_equal_the_frozen_source_mapping",
            projection.GetProperty("result").GetString());

        using var dcr = ReadStrictJson("sentinel-dcr.json");
        using var table = ReadStrictJson("sentinel-table.json");
        var dcrColumns = dcr.RootElement.GetProperty("properties").GetProperty("streamDeclarations")
            .GetProperty("Custom-PtkAudit").GetProperty("columns").EnumerateArray().ToArray();
        var tableColumns = table.RootElement.GetProperty("properties").GetProperty("schema")
            .GetProperty("columns").EnumerateArray().ToArray();
        Assert.Equal(19, dcrColumns.Length);
        var columnNames = dcrColumns.Select(column => column.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(columnNames, tableColumns.Select(column => column.GetProperty("name").GetString()));
        Assert.Equal(
            [
                "datetime", "datetime", "string", "string", "string", "string", "string", "string",
                "string", "string", "long", "string", "long", "string", "long", "string", "string",
                "string", "long",
            ],
            dcrColumns.Select(column => column.GetProperty("type").GetString()));
        Assert.Equal(
            [
                "dateTime", "dateTime", "string", "string", "string", "string", "string", "string",
                "string", "string", "long", "string", "long", "string", "long", "string", "string",
                "string", "long",
            ],
            tableColumns.Select(column => column.GetProperty("type").GetString()));

        var columnSources = projection.GetProperty("column_sources");
        AssertPropertyOrder(columnSources, columnNames);
        using var sentinel = ReadStrictJson("sentinel-event.json");
        var mapped = Assert.Single(sentinel.RootElement.EnumerateArray());
        AssertPropertyOrder(mapped, columnNames);

        var exactAuditBytes = ReadWithoutFinalLf("audit-v3-host.jsonl");
        var mappedRawEvent = mapped.GetProperty("RawEvent");
        Assert.Equal(JsonValueKind.String, mappedRawEvent.ValueKind);
        var mappedRawBytes = StrictUtf8.GetBytes(mappedRawEvent.GetString()!);
        Assert.Equal(exactAuditBytes, mappedRawBytes);
        using var rawEvent = JsonDocument.Parse(
            mappedRawBytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        AssertNoDuplicateProperties(rawEvent.RootElement);
        Assert.Equal("contract-vector-λ", rawEvent.RootElement.GetProperty("actor")
            .GetProperty("client_name").GetString());

        foreach (var column in dcrColumns)
        {
            var name = column.GetProperty("name").GetString()!;
            var type = column.GetProperty("type").GetString()!;
            var destination = mapped.GetProperty(name);
            var sourcePointer = columnSources.GetProperty(name).GetString()!;
            if (sourcePointer == "$exact_audit_body_utf8_without_final_lf")
            {
                Assert.Equal("RawEvent", name);
                Assert.Equal(JsonValueKind.String, destination.ValueKind);
                continue;
            }

            var source = ResolveJsonPointer(rawEvent.RootElement, sourcePointer);
            if (type is "string" or "datetime")
            {
                Assert.Equal(JsonValueKind.String, source.ValueKind);
                Assert.Equal(JsonValueKind.String, destination.ValueKind);
                Assert.Equal(source.GetString(), destination.GetString());
                if (type == "datetime") Assert.True(destination.TryGetDateTimeOffset(out _));
                continue;
            }

            Assert.Equal("long", type);
            Assert.Equal(JsonValueKind.Number, source.ValueKind);
            Assert.Equal(JsonValueKind.Number, destination.ValueKind);
            Assert.True(source.TryGetInt64(out var sourceValue));
            Assert.True(destination.TryGetInt64(out var destinationValue));
            Assert.Equal(sourceValue, destinationValue);
            Assert.Equal(source.GetRawText(), destination.GetRawText());
        }

        var live = validation.RootElement.GetProperty("sentinel_live_validation");
        Assert.True(live.GetProperty("required_for_release").GetBoolean());
        Assert.False(live.GetProperty("required_in_ordinary_offline_ci").GetBoolean());
        Assert.Equal("not_run_no_azure_validation_tenant", live.GetProperty("last_status").GetString());
        using var pins = ReadStrictJson("adapter-pins.json");
        var sentinelPins = pins.RootElement.GetProperty("sentinel");
        Assert.Contains($"api-version={sentinelPins.GetProperty("table_management_api_version").GetString()}",
            live.GetProperty("table_put").GetString(), StringComparison.Ordinal);
        Assert.Contains($"api-version={sentinelPins.GetProperty("dcr_management_api_version").GetString()}",
            live.GetProperty("dcr_put").GetString(), StringComparison.Ordinal);
        Assert.Contains($"api-version={sentinelPins.GetProperty("logs_ingestion_api_version").GetString()}",
            live.GetProperty("ingestion_post").GetString(), StringComparison.Ordinal);
        Assert.Contains(sentinelPins.GetProperty("stream").GetString()!,
            live.GetProperty("ingestion_post").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Containment_native_and_adapter_pins_are_closed()
    {
        using var contract = ReadStrictJson("contract.json");
        var artifacts = Strings(contract.RootElement.GetProperty("artifacts"));
        var artifactDigests = contract.RootElement.GetProperty("artifact_sha256");
        Assert.Equal(artifacts, artifactDigests.EnumerateObject().Select(property => property.Name));
        foreach (var artifact in artifactDigests.EnumerateObject())
        {
            Assert.Matches(LowerSha256, artifact.Value.GetString()!);
            Assert.Equal(
                artifact.Value.GetString(),
                LowerHex(SHA256.HashData(File.ReadAllBytes(PathOf(artifact.Name)))));
        }

        var containment = contract.RootElement.GetProperty("containment");
        Assert.Equal(10_000, containment.GetProperty("host_containment_grace_ms").GetInt32());
        Assert.Equal(10_000, containment.GetProperty("timeout_containment_grace_ms").GetInt32());
        Assert.Equal(2_000, containment.GetProperty("unix_kill_at_ms").GetInt32());
        Assert.Equal(25, containment.GetProperty("identity_poll_ms").GetInt32());
        Assert.True(containment.GetProperty("single_absolute_deadline").GetBoolean());
        Assert.False(containment.GetProperty("windows_terminate_job_object").GetBoolean());
        Assert.False(containment.GetProperty("unix_nonchild_waitpid").GetBoolean());

        var stages = contract.RootElement.GetProperty("worker_broker_start_failed_stages").EnumerateArray().ToArray();
        Assert.Equal(Enumerable.Range(1, 7), stages.Select(stage => stage.GetProperty("value").GetInt32()));
        Assert.Equal(
            ["fork", "child_setup", "identity_capture", "group_arm", "group_validate", "gate_release", "exec"],
            stages.Select(stage => stage.GetProperty("name").GetString()));
        Assert.Equal(74, contract.RootElement.GetProperty("worker_broker_unconfirmed_teardown_exit_code").GetInt32());

        var native = contract.RootElement.GetProperty("native_build");
        Assert.Equal("C17", native.GetProperty("language").GetString());
        Assert.Equal("-std=c17", Strings(native.GetProperty("common_flags"))[0]);
        var linux = native.GetProperty("linux");
        Assert.Equal("native-linux-acquisition.json", linux.GetProperty("acquisition_lock").GetString());
        Assert.Equal("exact_revision_and_sha256_online_acquisition_from_mutable_signed_repository", linux.GetProperty("acquisition_claim").GetString());
        Assert.False(linux.GetProperty("network_free_reproducibility_claim").GetBoolean());
        Assert.Equal("static_pie", linux.GetProperty("linkage").GetString());
        Assert.Equal(["-static-pie"], Strings(linux.GetProperty("link_flags")));
        using var linuxLock = ReadStrictJson("native-linux-acquisition.json");
        var linuxLockRoot = linuxLock.RootElement;
        Assert.Equal(linux.GetProperty("acquisition_claim").GetString(), linuxLockRoot.GetProperty("claim").GetString());
        var baseImage = linuxLockRoot.GetProperty("base_image");
        Assert.Equal("docker.io/library/alpine:3.21.3", baseImage.GetProperty("reference").GetString());
        Assert.Equal("sha256:a8560b36e8b8210634f77d9f7f9efd7ffa463e380b75e2e74aff4511df3ef88c", baseImage.GetProperty("oci_index_digest").GetString());
        Assert.Equal(2, baseImage.GetProperty("platforms").EnumerateObject().Count());
        Assert.All(baseImage.GetProperty("platforms").EnumerateObject(), platform =>
        {
            Assert.StartsWith("sha256:", platform.Value.GetProperty("manifest_digest").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("sha256:", platform.Value.GetProperty("config_digest").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("sha256:", platform.Value.GetProperty("layer_digest").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("sha256:", platform.Value.GetProperty("layer_diff_id").GetString(), StringComparison.Ordinal);
        });
        Assert.False(linuxLockRoot.GetProperty("apk_provenance").GetProperty("repository_is_immutable").GetBoolean());
        var overlay = linuxLockRoot.GetProperty("overlay_packages").EnumerateArray().ToArray();
        Assert.Equal(15, overlay.Length);
        Assert.Equal(15, overlay.Select(package => package.GetProperty("name").GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(63_260_152, overlay.Sum(package => package.GetProperty("x86_64").GetProperty("bytes").GetInt32()));
        Assert.Equal(59_395_428, overlay.Sum(package => package.GetProperty("aarch64").GetProperty("bytes").GetInt32()));
        Assert.All(overlay, package =>
        {
            Assert.Equal($"{package.GetProperty("name").GetString()}-{package.GetProperty("version").GetString()}.apk", package.GetProperty("filename").GetString());
            Assert.Matches(LowerSha256, package.GetProperty("x86_64").GetProperty("sha256").GetString()!);
            Assert.Matches(LowerSha256, package.GetProperty("aarch64").GetProperty("sha256").GetString()!);
        });
        var offline = linuxLockRoot.GetProperty("offline_rebuild");
        Assert.False(offline.GetProperty("bundle_materialized_in_repository").GetBoolean());
        Assert.False(offline.GetProperty("network_free_reproducibility_claim").GetBoolean());
        Assert.Contains("networking disabled", offline.GetProperty("release_guard").GetString(), StringComparison.Ordinal);
        var macos = native.GetProperty("macos");
        Assert.Equal("16.4", macos.GetProperty("xcode").GetString());
        Assert.Equal("15.0", macos.GetProperty("deployment_target").GetString());
        Assert.Equal(["/usr/lib/libSystem.B.dylib"], Strings(macos.GetProperty("required_dynamic_libraries")));

        using var pins = ReadStrictJson("adapter-pins.json");
        var collector = pins.RootElement.GetProperty("otelcol_contrib");
        Assert.Equal("v0.156.0", collector.GetProperty("version").GetString());
        Assert.Equal("aa158b23c8f89d795b21a05a49b3978565dfebd4", collector.GetProperty("release_commit").GetString());
        Assert.Equal("41e24cd516dd69a5b4277465cdb2ff4ef0676f49", collector.GetProperty("contrib_source_commit").GetString());
        var archives = collector.GetProperty("archives");
        Assert.Equal(6, archives.EnumerateObject().Count());
        Assert.All(archives.EnumerateObject(), property =>
        {
            AssertPropertyOrder(property.Value, ["url", "sha256"]);
            Assert.StartsWith("https://github.com/open-telemetry/opentelemetry-collector-releases/releases/download/v0.156.0/", property.Value.GetProperty("url").GetString(), StringComparison.Ordinal);
            Assert.Matches(LowerSha256, property.Value.GetProperty("sha256").GetString()!);
        });
        Assert.All(
            pins.RootElement.GetProperty("splunk").EnumerateObject()
                .Where(property => property.Name.EndsWith("source", StringComparison.Ordinal) || property.Name.EndsWith("documentation", StringComparison.Ordinal)),
            property => Assert.StartsWith("https://", property.Value.GetString(), StringComparison.Ordinal));

        var splunk = File.ReadAllText(PathOf("splunk-hec-fixture.yaml"), StrictUtf8);
        Assert.Contains("endpoint: https://splunk.example.invalid:8088/services/collector/event", splunk, StringComparison.Ordinal);
        Assert.Contains("host: host.id", splunk, StringComparison.Ordinal);
        Assert.Contains("not PTK's anchor", splunk, StringComparison.Ordinal);

        var splunkVector = ReadJsonLine("splunk-hec-event.jsonl");
        using (splunkVector.Document)
        {
            var hec = splunkVector.Document.RootElement;
            AssertPropertyOrder(hec, ["event", "fields", "host", "source", "sourcetype", "index", "time"]);
            Assert.Equal(StrictUtf8.GetString(ReadWithoutFinalLf("audit-v3-host.jsonl")), hec.GetProperty("event").GetString());
            Assert.Equal("11111111-1111-4111-8111-111111111111", hec.GetProperty("host").GetString());
            Assert.Equal("ptk", hec.GetProperty("source").GetString());
            Assert.Equal("ptk:audit", hec.GetProperty("sourcetype").GetString());
            Assert.Equal("ptk", hec.GetProperty("index").GetString());
            Assert.Equal("1784118897.1234567", hec.GetProperty("time").GetRawText());
            var fields = hec.GetProperty("fields");
            Assert.Equal("ptk.audit.host.ready", fields.GetProperty("otel.log.name").GetString());
            Assert.Equal("ptk.audit/3", fields.GetProperty("ptk.audit.schema_version").GetString());
            Assert.Equal(long.MaxValue, fields.GetProperty("ptk.host.generation").GetInt64());
            Assert.Equal(long.MaxValue, fields.GetProperty("ptk.host.recovery_attempt").GetInt64());
            Assert.Equal("33333333-3333-4333-8333-333333333333", fields.GetProperty("ptk.host.boot_id").GetString());
            Assert.Equal("ready", fields.GetProperty("ptk.host.state").GetString());
            Assert.Equal("completed", fields.GetProperty("ptk.outcome.state").GetString());
            Assert.Equal("confirmed", fields.GetProperty("ptk.termination.certainty").GetString());
            Assert.Equal("0.2.0-contract-vector", fields.GetProperty("service.version").GetString());
        }

        using var validation = ReadStrictJson("adapter-live-validation.json");
        Assert.Equal("passed", validation.RootElement.GetProperty("splunk_translator_validation").GetProperty("status").GetString());
        Assert.Equal("v0.156.0", validation.RootElement.GetProperty("splunk_translator_validation")
            .GetProperty("collector_version").GetString());
        Assert.Equal("exact_expected_body_match_including_final_lf", validation.RootElement
            .GetProperty("splunk_translator_validation").GetProperty("result").GetString());
        Assert.True(validation.RootElement.GetProperty("pinned_collector_live_validation")
            .GetProperty("required_for_release").GetBoolean());
        Assert.Equal("not_run_contract_slice_has_no_live_collector_harness", validation.RootElement
            .GetProperty("pinned_collector_live_validation").GetProperty("last_status").GetString());

        using var dcr = ReadStrictJson("sentinel-dcr.json");
        Assert.Equal("Direct", dcr.RootElement.GetProperty("kind").GetString());
        Assert.False(dcr.RootElement.GetProperty("properties").TryGetProperty("dataCollectionEndpointId", out _));
        var flow = Assert.Single(dcr.RootElement.GetProperty("properties").GetProperty("dataFlows").EnumerateArray());
        Assert.Equal("source", flow.GetProperty("transformKql").GetString());
        Assert.Equal("Custom-PtkAudit_CL", flow.GetProperty("outputStream").GetString());

        using var table = ReadStrictJson("sentinel-table.json");
        Assert.Equal("PtkAudit_CL", table.RootElement.GetProperty("properties").GetProperty("schema")
            .GetProperty("name").GetString());
        var dcrColumns = dcr.RootElement.GetProperty("properties").GetProperty("streamDeclarations")
            .GetProperty("Custom-PtkAudit").GetProperty("columns").EnumerateArray().ToArray();
        var tableColumns = table.RootElement.GetProperty("properties").GetProperty("schema")
            .GetProperty("columns").EnumerateArray().ToArray();
        Assert.Equal(dcrColumns.Select(column => column.GetProperty("name").GetString()),
            tableColumns.Select(column => column.GetProperty("name").GetString()));
        Assert.Equal(
            dcrColumns.Select(column => NormalizeSentinelType(column.GetProperty("type").GetString()!)),
            tableColumns.Select(column => NormalizeSentinelType(column.GetProperty("type").GetString()!)));

        using var sentinel = ReadStrictJson("sentinel-event.json");
        var mapped = Assert.Single(sentinel.RootElement.EnumerateArray());
        AssertPropertyOrder(mapped, dcrColumns.Select(column => column.GetProperty("name").GetString()!));
        var exactAuditBody = StrictUtf8.GetString(ReadWithoutFinalLf("audit-v3-host.jsonl"));
        Assert.Equal(exactAuditBody, mapped.GetProperty("RawEvent").GetString());
        Assert.Contains("contract-vector-λ", mapped.GetProperty("RawEvent").GetString(), StringComparison.Ordinal);
        Assert.Equal("2026-07-15T12:34:57.1234567Z", mapped.GetProperty("TimeGenerated").GetString());
        Assert.Equal("2026-07-15T12:34:57.7654321Z", mapped.GetProperty("ObservedUtc").GetString());
        Assert.Equal(long.MaxValue, mapped.GetProperty("HostGeneration").GetInt64());
        Assert.Equal(long.MaxValue, mapped.GetProperty("HostRecoveryAttempt").GetInt64());
        Assert.Equal("default", mapped.GetProperty("SessionName").GetString());
        Assert.Equal(long.MaxValue, mapped.GetProperty("SessionGeneration").GetInt64());
        Assert.Equal("completed", mapped.GetProperty("OutcomeState").GetString());
        Assert.Equal("host_ready", mapped.GetProperty("OutcomeDetailCode").GetString());
        Assert.Equal("confirmed", mapped.GetProperty("TerminationCertainty").GetString());
        Assert.Equal("ptk.audit/3", mapped.GetProperty("SchemaVersion").GetString());
        Assert.Equal("2025-07-01", pins.RootElement.GetProperty("sentinel")
            .GetProperty("table_management_api_version").GetString());
        Assert.Equal("Direct", pins.RootElement.GetProperty("sentinel").GetProperty("dcr_kind").GetString());
        Assert.False(pins.RootElement.GetProperty("sentinel")
            .GetProperty("external_data_collection_endpoint_required").GetBoolean());
    }

    private static byte[] SerializeRecoveryVector()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("detail_code", "host_recovering");
            writer.WriteBoolean("retryable", true);
            writer.WriteNumber("retry_after_ms", 250);
            writer.WriteString("recovery_phase", "backoff");
            writer.WriteNumber("recovery_attempt", 1);
            writer.WriteStartObject("retry_gate");
            writer.WriteString("kind", "host_ready");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static JsonElement[] StateRules(JsonElement definition)
    {
        var allOf = definition.GetProperty("allOf").EnumerateArray().ToArray();
        Assert.True(allOf.Length >= 2);
        return allOf[1].GetProperty("oneOf").EnumerateArray().ToArray();
    }

    private static string GuardDetailCode(JsonElement guard) => guard.GetProperty("if")
        .GetProperty("properties").GetProperty("detail_code").GetProperty("const").GetString()!;

    private static JsonElement[] IdentityRules(JsonElement definition)
    {
        var allOf = definition.GetProperty("allOf").EnumerateArray().ToArray();
        Assert.Equal(3, allOf.Length);
        return allOf[2].GetProperty("oneOf").EnumerateArray().ToArray();
    }

    private static void AssertIdentityRule(
        JsonElement rule,
        string[] states,
        string identityProperty,
        bool? nonnull)
    {
        Assert.Equal(["properties"], rule.EnumerateObject().Select(property => property.Name));
        var properties = rule.GetProperty("properties");
        Assert.Equal(
            nonnull.HasValue ? new[] { "state", identityProperty, "generation" } : ["state"],
            properties.EnumerateObject().Select(property => property.Name));
        AssertExactStringSet(properties.GetProperty("state"), states);

        if (!nonnull.HasValue) return;

        var identity = properties.GetProperty(identityProperty);
        var generation = properties.GetProperty("generation");
        if (nonnull.Value)
        {
            AssertPropertyOrder(identity, ["$ref"]);
            Assert.Equal("#/$defs/uuidv4", identity.GetProperty("$ref").GetString());
            AssertPropertyOrder(generation, ["type", "minimum"]);
            Assert.Equal("integer", generation.GetProperty("type").GetString());
            Assert.Equal(1, generation.GetProperty("minimum").GetInt64());
        }
        else
        {
            AssertPropertyOrder(identity, ["type"]);
            Assert.Equal("null", identity.GetProperty("type").GetString());
            AssertPropertyOrder(generation, ["type"]);
            Assert.Equal("null", generation.GetProperty("type").GetString());
        }
    }

    private static void AssertStateRule(
        JsonElement rule,
        string[] states,
        string[]? phases,
        bool ready,
        bool automatic = false,
        bool attemptIsZero = false)
    {
        Assert.Equal(["properties"], rule.EnumerateObject().Select(property => property.Name));
        var properties = rule.GetProperty("properties");
        var expectedProperties = automatic || attemptIsZero
            ? new[] { "state", "recovery_phase", "recovery_attempt", "retry_after_ms", "ready_for_effects" }
            : ["state", "recovery_phase", "retry_after_ms", "ready_for_effects"];
        Assert.Equal(expectedProperties, properties.EnumerateObject().Select(property => property.Name));

        AssertExactStringSet(properties.GetProperty("state"), states);
        var phase = properties.GetProperty("recovery_phase");
        if (phases is null)
        {
            AssertPropertyOrder(phase, ["type"]);
            Assert.Equal("null", phase.GetProperty("type").GetString());
        }
        else
        {
            AssertExactStringSet(phase, phases);
        }

        if (automatic)
        {
            var attempt = properties.GetProperty("recovery_attempt");
            AssertPropertyOrder(attempt, ["type", "minimum"]);
            Assert.Equal("integer", attempt.GetProperty("type").GetString());
            Assert.Equal(1, attempt.GetProperty("minimum").GetInt64());

            var retry = properties.GetProperty("retry_after_ms");
            AssertPropertyOrder(retry, ["type", "minimum", "maximum"]);
            Assert.Equal("integer", retry.GetProperty("type").GetString());
            Assert.Equal(250, retry.GetProperty("minimum").GetInt32());
            Assert.Equal(60_000, retry.GetProperty("maximum").GetInt32());
        }
        else
        {
            if (attemptIsZero)
            {
                var attempt = properties.GetProperty("recovery_attempt");
                AssertPropertyOrder(attempt, ["const"]);
                Assert.Equal(0, attempt.GetProperty("const").GetInt64());
            }

            var retry = properties.GetProperty("retry_after_ms");
            AssertPropertyOrder(retry, ["type"]);
            Assert.Equal("null", retry.GetProperty("type").GetString());
        }

        var readiness = properties.GetProperty("ready_for_effects");
        AssertPropertyOrder(readiness, ["const"]);
        Assert.Equal(ready, readiness.GetProperty("const").GetBoolean());
    }

    private static void AssertExactStringSet(JsonElement schema, string[] values)
    {
        if (values.Length == 1)
        {
            AssertPropertyOrder(schema, ["const"]);
            Assert.Equal(values[0], schema.GetProperty("const").GetString());
        }
        else
        {
            AssertPropertyOrder(schema, ["enum"]);
            Assert.Equal(values, Strings(schema.GetProperty("enum")));
        }
    }

    private static void AssertAuditEnvelope(AuditVector vector)
    {
        Assert.InRange(vector.ExactLine.Length, 3, 65_536);
        Assert.Equal((byte)'\n', vector.ExactLine[^1]);
        Assert.False(vector.ExactLine.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        var root = vector.Document.RootElement;
        AssertPropertyOrder(
            root,
            [
                "schema_version", "event_id", "event_type", "occurred_utc", "observed_utc",
                "producer", "host", "sequence", "previous_event_hash", "session", "actor",
                "correlation", "request", "operator_disposition", "routing", "outcome",
                "coverage", "audit", "event_hash",
            ]);
        AssertPropertyOrder(root.GetProperty("host"), ["boot_id", "generation", "state", "recovery_attempt"]);
        Assert.Equal("ptk.audit/3", root.GetProperty("schema_version").GetString());

        var body = vector.ExactLine.AsSpan(0, vector.ExactLine.Length - 1);
        var eventHash = root.GetProperty("event_hash").GetString()!;
        Assert.Matches(LowerSha256, eventHash);
        var suffix = StrictUtf8.GetBytes($",\"event_hash\":\"{eventHash}\"}}");
        Assert.True(body.EndsWith(suffix));
        var preHash = new byte[body.Length - suffix.Length + 1];
        body[..^suffix.Length].CopyTo(preHash);
        preHash[^1] = (byte)'}';
        Assert.Equal(eventHash, LowerHex(SHA256.HashData(preHash)));
    }

    private static string ComputeHostBuildDigest(JsonElement[] files, IReadOnlySet<string> roles)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes("ptk.host-build/1\0"));
        Span<byte> u32 = stackalloc byte[4];
        Span<byte> u64 = stackalloc byte[8];
        foreach (var file in files
                     .Where(file => roles.Contains(file.GetProperty("role").GetString()!))
                     .OrderBy(file => file.GetProperty("path").GetString(), StringComparer.Ordinal))
        {
            var path = StrictUtf8.GetBytes(file.GetProperty("path").GetString()!);
            BinaryPrimitives.WriteUInt32BigEndian(u32, checked((uint)path.Length));
            hash.AppendData(u32);
            hash.AppendData(path);
            BinaryPrimitives.WriteUInt64BigEndian(u64, checked((ulong)file.GetProperty("bytes").GetInt64()));
            hash.AppendData(u64);
            hash.AppendData(Convert.FromHexString(file.GetProperty("sha256").GetString()!));
        }
        return LowerHex(hash.GetHashAndReset());
    }

    private static string DomainHash(string domain, byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(domain + "\0"));
        hash.AppendData(bytes);
        return LowerHex(hash.GetHashAndReset());
    }

    private static AuditVector ReadAuditVector(string fileName)
    {
        var vector = ReadJsonLine(fileName);
        Assert.InRange(vector.ExactLine.Length, 3, 65_536);
        return vector;
    }

    private static AuditVector ReadJsonLine(string fileName)
    {
        var bytes = File.ReadAllBytes(PathOf(fileName));
        _ = StrictUtf8.GetString(bytes);
        Assert.Equal(1, bytes.Count(value => value == (byte)'\n'));
        Assert.Equal((byte)'\n', bytes[^1]);
        var document = JsonDocument.Parse(
            bytes.AsMemory(0, bytes.Length - 1),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        AssertNoDuplicateProperties(document.RootElement);
        return new AuditVector(bytes, document);
    }

    private static JsonDocument ReadStrictJson(string fileName)
    {
        var bytes = File.ReadAllBytes(PathOf(fileName));
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        _ = StrictUtf8.GetString(bytes);
        var document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        AssertNoDuplicateProperties(document.RootElement);
        return document;
    }

    private static byte[] ReadWithoutFinalLf(string fileName)
    {
        var bytes = File.ReadAllBytes(PathOf(fileName));
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\n', bytes[^2]);
        return bytes[..^1];
    }

    private static void AssertNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Assert.True(names.Add(property.Name), $"Duplicate JSON property '{property.Name}'.");
                AssertNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) AssertNoDuplicateProperties(item);
        }
    }

    private static void AssertPropertyOrder(JsonElement element, IEnumerable<string> expected) =>
        Assert.Equal(expected, element.EnumerateObject().Select(property => property.Name));

    private static void AssertObjectSchemaClosed(JsonElement schema)
    {
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    private static string NormalizeSentinelType(string type) => type.ToLowerInvariant();

    private static string RequiredRole(JsonElement branch) => branch.GetProperty("properties")
        .GetProperty("files").GetProperty("contains").GetProperty("properties")
        .GetProperty("role").GetProperty("const").GetString()!;

    private static bool PackagePathsStrictlyIncrease(IEnumerable<string> paths)
    {
        string? previous = null;
        foreach (var path in paths)
        {
            if (previous is not null && StringComparer.Ordinal.Compare(previous, path) >= 0) return false;
            previous = path;
        }
        return true;
    }

    private static bool SessionAliasesStrictlyIncrease(IEnumerable<string> aliases)
    {
        string? previous = null;
        foreach (var alias in aliases)
        {
            if (previous is not null && StringComparer.Ordinal.Compare(previous, alias) >= 0) return false;
            previous = alias;
        }
        return true;
    }

    private static JsonElement ResolveJsonPointer(JsonElement root, string pointer)
    {
        Assert.StartsWith("/", pointer, StringComparison.Ordinal);
        var current = root;
        foreach (var token in pointer.Split('/').Skip(1))
        {
            var property = token.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            current = current.GetProperty(property);
        }
        return current;
    }

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static string LowerHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string PathOf(string fileName, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourcePath)!, "..", "Contracts", "ResilienceR0", fileName));

    private sealed record AuditVector(byte[] ExactLine, JsonDocument Document);
}
