using System.Text;
using Google.Protobuf;
using PtkMcpServer.Audit;
using PtkMcpServer.Audit.OtlpWire;

namespace PtkMcpServer.Tests;

public sealed class AuditOtlpRecordMapperTests
{
    [Fact]
    public void Wire_field_numbers_match_the_stable_otlp_logs_contract()
    {
        Assert.Equal(1, ExportLogsServiceRequest.Descriptor.FindFieldByName("resource_logs").FieldNumber);
        Assert.Equal(1, ResourceLogs.Descriptor.FindFieldByName("resource").FieldNumber);
        Assert.Equal(2, ResourceLogs.Descriptor.FindFieldByName("scope_logs").FieldNumber);
        Assert.Equal(1, ScopeLogs.Descriptor.FindFieldByName("scope").FieldNumber);
        Assert.Equal(2, ScopeLogs.Descriptor.FindFieldByName("log_records").FieldNumber);
        Assert.Equal(1, LogRecord.Descriptor.FindFieldByName("time_unix_nano").FieldNumber);
        Assert.Equal(5, LogRecord.Descriptor.FindFieldByName("body").FieldNumber);
        Assert.Equal(6, LogRecord.Descriptor.FindFieldByName("attributes").FieldNumber);
        Assert.Equal(9, LogRecord.Descriptor.FindFieldByName("trace_id").FieldNumber);
        Assert.Equal(10, LogRecord.Descriptor.FindFieldByName("span_id").FieldNumber);
        Assert.Equal(11, LogRecord.Descriptor.FindFieldByName("observed_time_unix_nano").FieldNumber);
        Assert.Equal(12, LogRecord.Descriptor.FindFieldByName("event_name").FieldNumber);
        Assert.Equal(1, AnyValue.Descriptor.FindFieldByName("string_value").FieldNumber);
        Assert.Equal(3, AnyValue.Descriptor.FindFieldByName("int_value").FieldNumber);
        Assert.Equal(1, ExportLogsServiceResponse.Descriptor.FindFieldByName("partial_success").FieldNumber);
        Assert.Equal(1, ExportLogsPartialSuccess.Descriptor.FindFieldByName("rejected_log_records").FieldNumber);
    }

    [Fact]
    public void Map_preserves_exact_jsonl_body_and_emits_the_frozen_typed_shape()
    {
        var source = AuditOtlpTestRecord.Create();

        var mapped = AuditOtlpRecordMapper.Map(source.Utf8Line);
        var request = ExportLogsServiceRequest.Parser.ParseFrom(mapped.RequestBytes.Span);

        var resourceLogs = Assert.Single(request.ResourceLogs);
        var scopeLogs = Assert.Single(resourceLogs.ScopeLogs);
        var record = Assert.Single(scopeLogs.LogRecords);
        Assert.Equal(source.Utf8Line.Span[..^1], Encoding.UTF8.GetBytes(record.Body.StringValue));
        Assert.Equal(source.Utf8Line.Span[..^1], mapped.ExactJsonBody.Span);
        Assert.Equal(source.EventId, mapped.EventId);
        Assert.Equal(source.EventHash, mapped.EventHash);

        var resource = resourceLogs.Resource.Attributes.ToDictionary(value => value.Key, value => value.Value);
        Assert.Equal(5, resource.Count);
        Assert.Equal("ptk", resource["service.namespace"].StringValue);
        Assert.Equal("powershell-token-killer", resource["service.name"].StringValue);
        Assert.Equal("1.2.3-test", resource["service.version"].StringValue);
        Assert.Equal(AuditOtlpTestRecord.SupervisorBootId.ToString("D"), resource["service.instance.id"].StringValue);
        Assert.Equal(AuditOtlpTestRecord.HostId.ToString("D"), resource["host.id"].StringValue);
        Assert.Equal(0u, resourceLogs.Resource.DroppedAttributesCount);

        Assert.Equal("PtkMcpServer.Audit", scopeLogs.Scope.Name);
        Assert.Equal("1.2.3-test", scopeLogs.Scope.Version);
        Assert.Empty(scopeLogs.Scope.Attributes);
        Assert.Equal(0u, scopeLogs.Scope.DroppedAttributesCount);

        Assert.Equal(ToUnixNanoseconds(AuditOtlpTestRecord.Occurred), record.TimeUnixNano);
        Assert.Equal(ToUnixNanoseconds(AuditOtlpTestRecord.Observed), record.ObservedTimeUnixNano);
        Assert.Equal("ptk.audit.execution.planned", record.EventName);
        Assert.Equal(ByteString.CopyFrom(Convert.FromHexString(AuditOtlpTestRecord.TraceId)), record.TraceId);
        Assert.Equal(ByteString.Empty, record.SpanId);
        Assert.Equal(0, record.SeverityNumber);
        Assert.Equal(string.Empty, record.SeverityText);
        Assert.Equal(0u, record.DroppedAttributesCount);

        var attributes = record.Attributes.ToDictionary(value => value.Key, value => value.Value);
        Assert.Equal(12, attributes.Count);
        Assert.Equal("ptk.audit/1", attributes["ptk.audit.schema_version"].StringValue);
        Assert.Equal(source.EventId.ToString("D"), attributes["ptk.audit.event_id"].StringValue);
        Assert.Equal("execution.planned", attributes["ptk.audit.event_type"].StringValue);
        Assert.Equal(1, attributes["ptk.audit.sequence"].IntValue);
        Assert.False(attributes.ContainsKey("ptk.audit.previous_event_hash"));
        Assert.Equal(source.EventHash, attributes["ptk.audit.event_hash"].StringValue);
        Assert.Equal(AuditOtlpTestRecord.SupervisorBootId.ToString("D"), attributes["ptk.supervisor.boot_id"].StringValue);
        Assert.Equal(AuditOtlpTestRecord.WorkerBootId.ToString("D"), attributes["ptk.worker.boot_id"].StringValue);
        Assert.Equal("default", attributes["ptk.session.name"].StringValue);
        Assert.Equal(0, attributes["ptk.session.generation"].IntValue);
        Assert.Equal(AuditOtlpTestRecord.CallId.ToString("D"), attributes["ptk.call.id"].StringValue);
        Assert.Equal(7, attributes["ptk.job.id"].IntValue);
        Assert.False(attributes.ContainsKey("ptk.outcome.state"));
        Assert.Equal("not_applicable", attributes["ptk.termination.certainty"].StringValue);
    }

    [Fact]
    public void Map_omits_every_null_query_attribute_instead_of_emitting_an_empty_value()
    {
        var source = AuditOtlpTestRecord.Create(includeOptionalQueryValues: false);

        var request = ExportLogsServiceRequest.Parser.ParseFrom(
            AuditOtlpRecordMapper.Map(source.Utf8Line).RequestBytes.Span);
        var attributes = request.ResourceLogs[0].ScopeLogs[0].LogRecords[0]
            .Attributes.ToDictionary(value => value.Key, value => value.Value);

        Assert.Equal(6, attributes.Count);
        Assert.DoesNotContain("ptk.audit.previous_event_hash", attributes.Keys);
        Assert.DoesNotContain("ptk.worker.boot_id", attributes.Keys);
        Assert.DoesNotContain("ptk.session.name", attributes.Keys);
        Assert.DoesNotContain("ptk.session.generation", attributes.Keys);
        Assert.DoesNotContain("ptk.call.id", attributes.Keys);
        Assert.DoesNotContain("ptk.job.id", attributes.Keys);
        Assert.DoesNotContain("ptk.outcome.state", attributes.Keys);
        Assert.DoesNotContain("ptk.termination.certainty", attributes.Keys);
    }

    [Fact]
    public void Map_emits_nonnull_previous_hash_and_outcome_state_with_their_exact_types()
    {
        var source = AuditOtlpTestRecord.Create(
            sequence: 2,
            previousEventHash: AuditOtlpTestRecord.HashA,
            outcomeState: "succeeded");

        var request = ExportLogsServiceRequest.Parser.ParseFrom(
            AuditOtlpRecordMapper.Map(source.Utf8Line).RequestBytes.Span);
        var attributes = request.ResourceLogs[0].ScopeLogs[0].LogRecords[0]
            .Attributes.ToDictionary(value => value.Key, value => value.Value);

        Assert.Equal(2, attributes["ptk.audit.sequence"].IntValue);
        Assert.Equal(AuditOtlpTestRecord.HashA, attributes["ptk.audit.previous_event_hash"].StringValue);
        Assert.Equal("succeeded", attributes["ptk.outcome.state"].StringValue);
    }

    [Fact]
    public void Map_is_deterministic_for_the_same_authoritative_jsonl_bytes()
    {
        var source = AuditOtlpTestRecord.Create();

        var first = AuditOtlpRecordMapper.Map(source.Utf8Line);
        var second = AuditOtlpRecordMapper.Map(source.Utf8Line);

        Assert.Equal(first.RequestBytes.Span, second.RequestBytes.Span);
        Assert.Equal(first.ExactJsonBody.Span, second.ExactJsonBody.Span);
        Assert.Equal(first.EventId, second.EventId);
        Assert.Equal(first.EventHash, second.EventHash);
    }

    [Fact]
    public void Map_fails_closed_when_a_required_query_value_cannot_be_mapped_exactly()
    {
        var source = AuditOtlpTestRecord.Create();
        var text = Encoding.UTF8.GetString(source.Utf8Line.Span);
        var wrongSequenceType = Encoding.UTF8.GetBytes(
            text.Replace("\"sequence\":1", "\"sequence\":\"1\"", StringComparison.Ordinal));
        var invalidTrace = Encoding.UTF8.GetBytes(
            text.Replace(AuditOtlpTestRecord.TraceId, new string('z', 32), StringComparison.Ordinal));
        var zeroTrace = Encoding.UTF8.GetBytes(
            text.Replace(AuditOtlpTestRecord.TraceId, new string('0', 32), StringComparison.Ordinal));

        Assert.Throws<AuditOtlpMappingException>(() => AuditOtlpRecordMapper.Map(wrongSequenceType));
        Assert.Throws<AuditOtlpMappingException>(() => AuditOtlpRecordMapper.Map(invalidTrace));
        Assert.Throws<AuditOtlpMappingException>(() => AuditOtlpRecordMapper.Map(zeroTrace));
        Assert.Throws<AuditOtlpMappingException>(() => AuditOtlpRecordMapper.Map(source.Utf8Line[..^1]));
    }

    private static ulong ToUnixNanoseconds(DateTimeOffset value) =>
        checked((ulong)(value.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) * 100UL);
}

internal static class AuditOtlpTestRecord
{
    internal static readonly Guid EventId = Guid.Parse("01890f3e-1234-7abc-8def-0123456789ab");
    internal static readonly Guid CallId = Guid.Parse("01890f3e-5678-7abc-8def-0123456789ab");
    internal static readonly Guid ParentEventId = Guid.Parse("01890f3e-9abc-7abc-8def-0123456789ab");
    internal static readonly Guid HostId = Guid.Parse("12345678-1234-4abc-8def-0123456789ab");
    internal static readonly Guid SupervisorBootId = Guid.Parse("22345678-1234-4abc-8def-0123456789ab");
    internal static readonly Guid WorkerBootId = Guid.Parse("32345678-1234-4abc-8def-0123456789ab");
    internal static readonly Guid PlanId = Guid.Parse("42345678-1234-4abc-8def-0123456789ab");
    internal static readonly Guid EvidenceId = Guid.Parse("52345678-1234-4abc-8def-0123456789ab");
    internal static readonly DateTimeOffset Occurred = new(2026, 7, 11, 12, 34, 56, 123, TimeSpan.Zero);
    internal static readonly DateTimeOffset Observed = Occurred.AddTicks(4567);
    internal const string TraceId = "0123456789abcdef0123456789abcdef";
    internal const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    internal static SerializedAuditEvent Create(
        bool includeOptionalQueryValues = true,
        long sequence = 1,
        string? previousEventHash = null,
        string? outcomeState = null) =>
        AuditEventSerializer.Serialize(
            sequence,
            previousEventHash,
            new AuditProducerContext(
                HostId,
                SupervisorBootId,
                includeOptionalQueryValues ? WorkerBootId : null,
                4321,
                "1.2.3-test",
                HashA),
            CompleteInput(includeOptionalQueryValues, outcomeState),
            EventId,
            Occurred,
            Observed);

    private static AuditEventInput CompleteInput(
        bool includeOptionalQueryValues,
        string? outcomeState) => new()
    {
        EventType = "execution.planned",
        Session = new AuditSession
        {
            Name = includeOptionalQueryValues ? "default" : null,
            Generation = includeOptionalQueryValues ? 0 : null,
            BindingKind = includeOptionalQueryValues ? "default" : null,
            DeclaredPurpose = "test purpose",
            DeclaredTarget = "localhost",
            DeclaredIdentity = "test-user",
            EffectiveIdentity = "test-user",
            AllowColdBackground = false,
        },
        Actor = new AuditActor
        {
            Transport = "mcp_stdio",
            ClientName = "test-client",
            ClientVersion = "1.0",
            ClientSessionId = "session-1",
            AttributionStrength = "client_asserted",
        },
        Correlation = new AuditCorrelation
        {
            CallId = includeOptionalQueryValues ? CallId : null,
            JobId = includeOptionalQueryValues ? 7 : null,
            ParentEventId = ParentEventId,
            TraceId = includeOptionalQueryValues ? TraceId : null,
            PlanId = PlanId,
        },
        Request = new AuditRequest
        {
            Tool = "ptk_invoke",
            Action = "invoke",
            ProvidedFields = ["action", "raw"],
            SessionRequested = "default",
            Cwd = "/tmp/work",
            TimeoutMs = 30_000,
            DeadlineUtc = Observed.AddMinutes(1),
            Route = "auto",
            Background = false,
            Raw = false,
            ExpectedGeneration = 0,
            Force = false,
            AllowColdBackground = false,
            MaxBytes = 65_536,
            PatternFingerprint = HashA,
            OutputHandleDigest = HashB,
            OriginalScriptDigest = HashA,
            ScriptEvidenceId = EvidenceId,
        },
        Routing = new AuditRouting
        {
            Domain = "powershell",
            RequestedRoute = "auto",
            EffectiveRoute = "powershell_direct",
            PermittedFallbacks = ["powershell_direct", "native_direct"],
            Provenance = "powershell_objects",
        },
        Outcome = new AuditOutcome
        {
            State = outcomeState,
            QueueMs = 0,
            WarmStateLost = false,
            WorkerReplaced = false,
            TerminationCertainty = includeOptionalQueryValues ? "not_applicable" : null,
        },
        Coverage = new AuditCoverage
        {
            PtkRequest = true,
            RootProcessObserved = "not_applicable",
            DescendantsObserved = "not_applicable",
            RemoteEffectObserved = "not_applicable",
        },
        Audit = new AuditEventHealth
        {
            ProtectionMode = "local-only",
            HealthState = "healthy",
        },
    };
}
