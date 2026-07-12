using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditEventTests
{
    private static readonly Guid EventId = Guid.Parse("01890f3e-1234-7abc-8def-0123456789ab");
    private static readonly Guid CallId = Guid.Parse("01890f3e-5678-7abc-8def-0123456789ab");
    private static readonly Guid ParentEventId = Guid.Parse("01890f3e-9abc-7abc-8def-0123456789ab");
    private static readonly Guid HostId = Guid.Parse("12345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid SupervisorBootId = Guid.Parse("22345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid WorkerBootId = Guid.Parse("32345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid PlanId = Guid.Parse("42345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid EvidenceId = Guid.Parse("52345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid DispositionId = Guid.Parse("62345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid TargetEventId = Guid.Parse("01890f3e-abcd-7abc-8def-0123456789ab");
    private static readonly DateTimeOffset Occurred = new(2026, 7, 11, 12, 34, 56, 123, TimeSpan.Zero);
    private static readonly DateTimeOffset Observed = Occurred.AddTicks(4567);
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Serialize_emits_the_complete_ordered_v1_envelope_and_hashes_exact_pre_hash_bytes()
    {
        var serialized = Serialize(1, null, CompleteInput());
        var bytes = serialized.Utf8Line.ToArray();

        Assert.InRange(bytes.Length, 1, AuditEventSerializer.MaximumLineBytes);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\n', bytes[^2]);

        var json = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(": ", json);
        Assert.DoesNotContain("\r", json);
        Assert.Equal(1, json.Count(c => c == '\n'));

        using var document = JsonDocument.Parse(bytes.AsMemory(0, bytes.Length - 1));
        var root = document.RootElement;
        Assert.Equal(
            ["schema_version", "event_id", "event_type", "occurred_utc", "observed_utc",
             "producer", "sequence", "previous_event_hash", "session", "actor", "correlation",
             "request", "operator_disposition", "routing", "outcome", "coverage", "audit", "event_hash"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["host_id", "supervisor_boot_id", "worker_boot_id", "pid", "version", "binary_digest"],
            Names(root, "producer"));
        Assert.Equal(
            ["name", "generation", "binding_kind", "template_name", "template_digest",
             "bootstrap_digest", "declared_purpose", "declared_target", "declared_identity",
             "effective_identity", "allow_cold_background"],
            Names(root, "session"));
        Assert.Equal(
            ["transport", "client_name", "client_version", "client_session_id", "attribution_strength"],
            Names(root, "actor"));
        Assert.Equal(
            ["call_id", "job_id", "parent_event_id", "trace_id", "plan_id"],
            Names(root, "correlation"));
        Assert.Equal(
            ["tool", "action", "provided_fields", "session_requested", "cwd", "destination_kind",
             "destination_path", "timeout_ms", "deadline_utc", "route", "background", "raw",
             "list_available", "job_id", "offset",
             "expected_generation", "force", "template", "allow_cold_background", "max_bytes",
             "pattern_fingerprint", "output_handle_digest", "original_script_digest", "script_evidence_id"],
            Names(root, "request"));
        Assert.Equal(
            ["domain", "requested_route", "effective_route", "permitted_fallbacks", "rtk_version",
             "rtk_binary_digest", "provenance", "fallback_reason"],
            Names(root, "routing"));
        Assert.Equal(
            ["state", "detail_code", "exit_code", "duration_ms", "queue_ms", "bytes_returned",
             "next_offset", "warm_state_lost", "worker_replaced", "termination_certainty"],
            Names(root, "outcome"));
        Assert.Equal(
            ["ptk_request", "root_process_observed", "descendants_observed", "remote_effect_observed"],
            Names(root, "coverage"));
        Assert.Equal(
            ["protection_mode", "export_configuration_identity", "health_state", "failure_class",
             "degraded_since_utc", "emergency_probe_count", "emergency_probe_first_utc",
             "emergency_probe_last_utc"],
            Names(root, "audit"));

        Assert.Equal("ptk.audit/1", root.GetProperty("schema_version").GetString());
        Assert.Equal(EventId.ToString("D"), root.GetProperty("event_id").GetString());
        Assert.Equal("2026-07-11T12:34:56.1230000Z", root.GetProperty("occurred_utc").GetString());
        Assert.Equal("2026-07-11T12:34:56.1234567Z", root.GetProperty("observed_utc").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("sequence").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("previous_event_hash").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("session").GetProperty("template_name").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("outcome").GetProperty("exit_code").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("operator_disposition").ValueKind);
        Assert.Equal(["action", "raw"], Strings(root.GetProperty("request").GetProperty("provided_fields")));
        Assert.Equal(
            ["native_direct", "powershell_direct"],
            Strings(root.GetProperty("routing").GetProperty("permitted_fallbacks")));

        var eventHash = root.GetProperty("event_hash").GetString()!;
        Assert.Equal(serialized.EventHash, eventHash);
        Assert.Matches("^[0-9a-f]{64}$", eventHash);

        var lineWithoutLf = bytes.AsSpan(0, bytes.Length - 1);
        var finalSuffix = Encoding.ASCII.GetBytes($",\"event_hash\":\"{eventHash}\"}}");
        Assert.True(lineWithoutLf.EndsWith(finalSuffix));
        var preHashBytes = new byte[lineWithoutLf.Length - finalSuffix.Length + 1];
        lineWithoutLf[..^finalSuffix.Length].CopyTo(preHashBytes);
        preHashBytes[^1] = (byte)'}';
        var independentlyComputed = Convert.ToHexString(SHA256.HashData(preHashBytes)).ToLowerInvariant();
        Assert.Equal(independentlyComputed, eventHash);
    }

    [Fact]
    public void Serialize_enforces_sequence_and_previous_hash_link_shape()
    {
        Assert.Throws<AuditEventValidationException>(() => Serialize(0, null, CompleteInput()));
        Assert.Throws<AuditEventValidationException>(() => Serialize(1, HashA, CompleteInput()));
        Assert.Throws<AuditEventValidationException>(() => Serialize(2, null, CompleteInput()));
        Assert.Throws<AuditEventValidationException>(() => Serialize(2, HashA.ToUpperInvariant(), CompleteInput()));

        var linked = Serialize(2, HashA, CompleteInput());
        using var document = Parse(linked);
        Assert.Equal(HashA, document.RootElement.GetProperty("previous_event_hash").GetString());
    }

    [Fact]
    public void Serialize_rejects_wrong_uuid_versions_and_non_utc_timestamps()
    {
        var uuidV4 = Guid.Parse("62345678-1234-4abc-8def-0123456789ab");
        var uuidV7 = Guid.Parse("01890f3e-abcd-7abc-8def-0123456789ab");
        Assert.Throws<AuditEventValidationException>(() => AuditEventSerializer.Serialize(
            1, null, Producer(), CompleteInput(), uuidV4, Occurred, Observed));
        Assert.Throws<AuditEventValidationException>(() => AuditEventSerializer.Serialize(
            1, null, Producer() with { HostId = uuidV7 }, CompleteInput(), EventId, Occurred, Observed));
        Assert.Throws<AuditEventValidationException>(() => AuditEventSerializer.Serialize(
            1, null, Producer(), CompleteInput() with
            {
                Correlation = CompleteInput().Correlation with { PlanId = uuidV7 }
            }, EventId, Occurred, Observed));
        Assert.Throws<AuditEventValidationException>(() => AuditEventSerializer.Serialize(
            1, null, Producer(), CompleteInput(), EventId,
            Occurred.ToOffset(TimeSpan.FromHours(-4)), Observed));
    }

    [Fact]
    public void Serialize_rejects_invalid_scalar_bounds_names_enums_and_unicode()
    {
        Assert.Throws<AuditEventValidationException>(() => AuditEventSerializer.Serialize(
            1, null, Producer() with { Pid = 0 }, CompleteInput(), EventId, Occurred, Observed));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, CompleteInput() with { EventType = "missingdot" }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, CompleteInput() with
            {
                Session = CompleteInput().Session with { Name = "Uppercase" }
            }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, CompleteInput() with
            {
                Session = CompleteInput().Session with { DeclaredPurpose = "line\nbreak" }
            }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, CompleteInput() with
            {
                Session = CompleteInput().Session with { DeclaredPurpose = "\ud800" }
            }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, CompleteInput() with
            {
                Request = CompleteInput().Request with { Cwd = "has\0nul" }
            }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, CompleteInput() with
            {
                Request = CompleteInput().Request with { DestinationKind = "arbitrary" }
            }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, CompleteInput() with
            {
                Request = CompleteInput().Request with
                {
                    DestinationKind = "stdout",
                    DestinationPath = "/should-not-exist",
                }
            }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, CompleteInput() with
            {
                Routing = CompleteInput().Routing with { EffectiveRoute = "unknown_route" }
            }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, CompleteInput() with
            {
                Request = CompleteInput().Request with { TimeoutMs = -1 }
            }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, CompleteInput() with
            {
                Request = CompleteInput().Request with { PatternFingerprint = HashA.ToUpperInvariant() }
            }));
    }

    [Fact]
    public void Serialize_rejects_invalid_array_members_and_normalizes_valid_arrays_ordinally()
    {
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, CompleteInput() with
            {
                Request = CompleteInput().Request with
                {
                    ProvidedFields = Enumerable.Range(0, 65).Select(i => $"p{i}").ToArray()
                }
            }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, CompleteInput() with
            {
                Routing = CompleteInput().Routing with
                {
                    PermittedFallbacks = ["native_direct", "not_a_fallback"]
                }
            }));

        var serialized = Serialize(1, null, CompleteInput() with
        {
            Request = CompleteInput().Request with { ProvidedFields = ["z", "A", "z", "a"] },
            Routing = CompleteInput().Routing with
            {
                PermittedFallbacks = ["powershell_direct", "native_direct", "powershell_direct"]
            }
        });
        using var document = Parse(serialized);
        Assert.Equal(["A", "a", "z"], Strings(document.RootElement.GetProperty("request").GetProperty("provided_fields")));
        Assert.Equal(
            ["native_direct", "powershell_direct"],
            Strings(document.RootElement.GetProperty("routing").GetProperty("permitted_fallbacks")));
    }

    [Fact]
    public void Serialize_enforces_exact_operator_disposition_shapes_and_event_coupling()
    {
        var requestFacts = new AuditOperatorDispositionFacts
        {
            TargetSupervisorBootId = SupervisorBootId,
            TargetEventId = TargetEventId,
            ProofKind = "acknowledged_gap",
            AcknowledgedGapReason = "operator.accepted",
        };
        var request = CompleteInput() with
        {
            EventType = "export.disposition_intent",
            Correlation = CompleteInput().Correlation with { PlanId = null },
            Routing = CompleteInput().Routing with { PermittedFallbacks = [] },
            OperatorDisposition = requestFacts,
        };
        var serializedRequest = Serialize(1, null, request);
        using (var document = Parse(serializedRequest))
        {
            var facts = document.RootElement.GetProperty("operator_disposition");
            Assert.Equal(
                [
                    "disposition_id", "target_supervisor_boot_id", "target_spool_file",
                    "target_start_offset", "target_next_offset", "target_sequence",
                    "target_event_id", "failure_class", "detail_code", "response_digest",
                    "first_failure_utc", "target_export_configuration_identity",
                    "proof_kind", "verified_receipt_digest", "acknowledged_gap_reason",
                ],
                facts.EnumerateObject().Select(property => property.Name));
            Assert.Equal(JsonValueKind.Null, facts.GetProperty("disposition_id").ValueKind);
            Assert.Equal(
                SupervisorBootId.ToString("D"),
                facts.GetProperty("target_supervisor_boot_id").GetString());
            Assert.Equal(TargetEventId.ToString("D"), facts.GetProperty("target_event_id").GetString());
            Assert.Equal("acknowledged_gap", facts.GetProperty("proof_kind").GetString());
            Assert.Equal("operator.accepted", facts.GetProperty("acknowledged_gap_reason").GetString());
        }

        var authorizedFacts = requestFacts with
        {
            DispositionId = DispositionId,
            TargetSpoolFile = AuditSpoolSegmentIdentity.Create(SupervisorBootId, 3).FileName,
            TargetStartOffset = 10,
            TargetNextOffset = 20,
            TargetSequence = 7,
            FailureClass = "data",
            DetailCode = "otlp.bad_record",
            FirstFailureUtc = Occurred,
            TargetExportConfigurationIdentity = HashB,
            ProofKind = "verified_receipt",
            VerifiedReceiptDigest = HashA,
            AcknowledgedGapReason = null,
        };
        var authorized = request with
        {
            EventType = "export.disposition_authorized",
            OperatorDisposition = authorizedFacts,
        };
        Serialize(1, null, authorized);

        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1,
            null,
            request with { OperatorDisposition = null }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1,
            null,
            CompleteInput() with { OperatorDisposition = requestFacts }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1,
            null,
            request with
            {
                OperatorDisposition = requestFacts with { TargetSequence = 1 },
            }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1,
            null,
            authorized with
            {
                OperatorDisposition = authorizedFacts with { TargetSequence = null },
            }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1,
            null,
            request with
            {
                OperatorDisposition = requestFacts with
                {
                    AcknowledgedGapReason = "operator..accepted",
                },
            }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1,
            null,
            authorized with
            {
                OperatorDisposition = authorizedFacts with
                {
                    AcknowledgedGapReason = "unexpected",
                },
            }));
    }

    [Fact]
    public void Serialize_requires_plan_id_for_execution_events_and_empty_fallbacks_without_a_plan()
    {
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, CompleteInput() with
            {
                Correlation = CompleteInput().Correlation with { PlanId = null }
            }));

        var callInput = CompleteInput() with
        {
            EventType = "call.accepted",
            Correlation = CompleteInput().Correlation with { PlanId = null },
            Routing = CompleteInput().Routing with { PermittedFallbacks = [] }
        };
        Serialize(1, null, callInput);
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, callInput with
            {
                Routing = callInput.Routing with { PermittedFallbacks = ["native_direct"] }
            }));
    }

    [Fact]
    public void Serialize_enforces_audit_protection_and_recovery_gap_consistency()
    {
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, CompleteInput() with
            {
                Audit = CompleteInput().Audit with { ExportConfigurationIdentity = HashA }
            }));
        Assert.Throws<AuditEventValidationException>(() => Serialize(
            1, null, CompleteInput() with
            {
                Audit = CompleteInput().Audit with
                {
                    HealthState = "recovered",
                    FailureClass = "journal.io",
                    DegradedSinceUtc = Occurred,
                    EmergencyProbeCount = 1,
                    EmergencyProbeFirstUtc = null,
                    EmergencyProbeLastUtc = Observed
                }
            }));

        var recovered = CompleteInput() with
        {
            EventType = "audit.recovered",
            Correlation = CompleteInput().Correlation with { PlanId = null },
            Routing = CompleteInput().Routing with { PermittedFallbacks = [] },
            Audit = new AuditEventHealth
            {
                ProtectionMode = "anchored",
                ExportConfigurationIdentity = HashB,
                HealthState = "recovered",
                FailureClass = "journal.io",
                DegradedSinceUtc = Occurred,
                EmergencyProbeCount = 2,
                EmergencyProbeFirstUtc = Occurred,
                EmergencyProbeLastUtc = Observed
            }
        };
        Serialize(1, null, recovered);
    }

    [Fact]
    public void Serialize_refuses_a_valid_but_oversized_core_record()
    {
        const string scalar = "😀";
        var maxText256 = string.Concat(Enumerable.Repeat(scalar, 256));
        var maxText512 = string.Concat(Enumerable.Repeat(scalar, 512));
        var maxText128 = string.Concat(Enumerable.Repeat(scalar, 128));
        var maxPath = string.Concat(Enumerable.Repeat(scalar, 4096));
        var fields = Enumerable.Range(0, 64)
            .Select(i => $"p{i:D2}{new string('x', 61)}")
            .ToArray();

        var input = CompleteInput() with
        {
            Session = CompleteInput().Session with
            {
                DeclaredPurpose = maxText512,
                DeclaredTarget = maxText256,
                DeclaredIdentity = maxText256,
                EffectiveIdentity = maxText256
            },
            Actor = CompleteInput().Actor with
            {
                ClientName = maxText256,
                ClientVersion = maxText256,
                ClientSessionId = maxText256
            },
            Request = CompleteInput().Request with { Cwd = maxPath, ProvidedFields = fields },
            Routing = CompleteInput().Routing with { RtkVersion = maxText128 }
        };

        Assert.Throws<AuditEventValidationException>(() => AuditEventSerializer.Serialize(
            1, null, Producer() with { Version = maxText128 }, input,
            EventId, Occurred, Observed));
    }

    private static SerializedAuditEvent Serialize(long sequence, string? previousHash, AuditEventInput input) =>
        AuditEventSerializer.Serialize(sequence, previousHash, Producer(), input, EventId, Occurred, Observed);

    private static AuditProducerContext Producer() => new(
        HostId,
        SupervisorBootId,
        WorkerBootId,
        4321,
        "1.2.3-test",
        HashA);

    private static AuditEventInput CompleteInput() => new()
    {
        EventType = "execution.planned",
        Session = new AuditSession
        {
            Name = "default",
            Generation = 0,
            BindingKind = "default",
            TemplateName = null,
            TemplateDigest = null,
            BootstrapDigest = null,
            DeclaredPurpose = "test purpose",
            DeclaredTarget = "localhost",
            DeclaredIdentity = "test-user",
            EffectiveIdentity = "test-user",
            AllowColdBackground = false
        },
        Actor = new AuditActor
        {
            Transport = "mcp_stdio",
            ClientName = "test-client",
            ClientVersion = "1.0",
            ClientSessionId = "session-1",
            AttributionStrength = "client_asserted"
        },
        Correlation = new AuditCorrelation
        {
            CallId = CallId,
            JobId = 7,
            ParentEventId = ParentEventId,
            TraceId = "0123456789abcdef0123456789abcdef",
            PlanId = PlanId
        },
        Request = new AuditRequest
        {
            Tool = "ptk_invoke",
            Action = "invoke",
            ProvidedFields = ["raw", "action", "raw"],
            SessionRequested = "default",
            Cwd = "/tmp/work",
            TimeoutMs = 30_000,
            DeadlineUtc = Observed.AddMinutes(1),
            Route = "auto",
            Background = false,
            Raw = false,
            ListAvailable = null,
            JobId = null,
            Offset = null,
            ExpectedGeneration = 0,
            Force = false,
            Template = null,
            AllowColdBackground = false,
            MaxBytes = 65_536,
            PatternFingerprint = HashA,
            OutputHandleDigest = HashB,
            OriginalScriptDigest = HashA,
            ScriptEvidenceId = EvidenceId
        },
        Routing = new AuditRouting
        {
            Domain = "powershell",
            RequestedRoute = "auto",
            EffectiveRoute = "powershell_direct",
            PermittedFallbacks = ["powershell_direct", "native_direct", "native_direct"],
            RtkVersion = null,
            RtkBinaryDigest = null,
            Provenance = "powershell_objects",
            FallbackReason = null
        },
        Outcome = new AuditOutcome
        {
            State = null,
            DetailCode = null,
            ExitCode = null,
            DurationMs = null,
            QueueMs = 0,
            BytesReturned = null,
            NextOffset = null,
            WarmStateLost = false,
            WorkerReplaced = false,
            TerminationCertainty = "not_applicable"
        },
        Coverage = new AuditCoverage
        {
            PtkRequest = true,
            RootProcessObserved = "not_applicable",
            DescendantsObserved = "not_applicable",
            RemoteEffectObserved = "not_applicable"
        },
        Audit = new AuditEventHealth
        {
            ProtectionMode = "local-only",
            ExportConfigurationIdentity = null,
            HealthState = "healthy",
            FailureClass = null,
            DegradedSinceUtc = null,
            EmergencyProbeCount = null,
            EmergencyProbeFirstUtc = null,
            EmergencyProbeLastUtc = null
        }
    };

    private static string[] Names(JsonElement root, string propertyName) =>
        root.GetProperty(propertyName).EnumerateObject().Select(property => property.Name).ToArray();

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static JsonDocument Parse(SerializedAuditEvent serialized) =>
        JsonDocument.Parse(serialized.Utf8Line[..^1]);
}
