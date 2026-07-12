using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using PtkMcpServer.Audit.OtlpWire;

namespace PtkMcpServer.Audit;

internal sealed class AuditOtlpMappingException : Exception
{
    internal AuditOtlpMappingException(string failureCode)
        : base($"audit_otlp_mapping_failed: {failureCode}")
    {
        FailureCode = failureCode;
    }

    internal string FailureCode { get; }
}

internal sealed class AuditOtlpRecord
{
    private readonly byte[] _requestBytes;
    private readonly byte[] _exactJsonBody;

    internal AuditOtlpRecord(
        byte[] requestBytes,
        byte[] exactJsonBody,
        Guid eventId,
        string eventHash)
    {
        _requestBytes = requestBytes;
        _exactJsonBody = exactJsonBody;
        EventId = eventId;
        EventHash = eventHash;
    }

    internal ReadOnlyMemory<byte> RequestBytes => _requestBytes;

    internal ReadOnlyMemory<byte> ExactJsonBody => _exactJsonBody;

    internal Guid EventId { get; }

    internal string EventHash { get; }
}

/// <summary>
/// Maps one already-validated authoritative ptk.audit/1 or ptk.audit/2 JSONL record to one
/// deterministic OTLP/HTTP binary protobuf request. The JSON body is decoded
/// only to populate the frozen query attributes; it is never reserialized.
/// </summary>
internal static class AuditOtlpRecordMapper
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> OperatorDispositionProperties = new(
        [
            "disposition_id", "target_supervisor_boot_id", "target_spool_file",
            "target_start_offset", "target_next_offset", "target_sequence",
            "target_event_id", "failure_class", "detail_code", "response_digest",
            "first_failure_utc", "target_export_configuration_identity",
            "proof_kind", "verified_receipt_digest", "acknowledged_gap_reason",
        ],
        StringComparer.Ordinal);

    internal static AuditOtlpRecord Map(ReadOnlyMemory<byte> jsonlLine)
    {
        try
        {
            if (jsonlLine.Length is < 3 or > AuditEventSerializer.MaximumLineBytes ||
                jsonlLine.Span[^1] != (byte)'\n' ||
                jsonlLine.Span[^2] != (byte)'}' ||
                HasUtf8Bom(jsonlLine.Span))
            {
                Fail("jsonl_shape");
            }

            var exactBody = jsonlLine[..^1].ToArray();
            var bodyText = StrictUtf8.GetString(exactBody);
            using var document = JsonDocument.Parse(
                exactBody,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
            var root = RequiredObject(document.RootElement);
            EnsureUniqueProperties(root);

            var schemaVersion = RequiredString(root, "schema_version");
            if (!string.Equals(
                    schemaVersion,
                    AuditEventSerializer.LegacySchemaVersion,
                    StringComparison.Ordinal) &&
                !string.Equals(
                    schemaVersion,
                    AuditEventSerializer.CurrentSchemaVersion,
                    StringComparison.Ordinal))
            {
                Fail("schema_version");
            }
            var shape = AuditEvidenceSpoolScanner.ValidateExactEnvelopeShape(root);
            var isV2 = shape.Version == AuditCoreSchemaVersion.V2;
            var eventIdText = RequiredString(root, "event_id");
            var eventId = RequiredCanonicalUuid(eventIdText, version: 7);
            var eventType = RequiredNonemptyString(root, "event_type");
            var occurred = RequiredUtc(root, "occurred_utc");
            var observed = RequiredUtc(root, "observed_utc");
            var sequence = RequiredPositiveInt64(root, "sequence");
            var previousHash = NullableString(root, "previous_event_hash");
            if (sequence == 1 ? previousHash is not null : !IsLowerHex(previousHash, 64))
                Fail("previous_event_hash");
            var eventHash = RequiredString(root, "event_hash");
            if (!IsLowerHex(eventHash, 64)) Fail("event_hash");

            var producer = RequiredObject(root.GetProperty("producer"));
            var session = RequiredObject(root.GetProperty("session"));
            var correlation = RequiredObject(root.GetProperty("correlation"));
            var auditRequest = RequiredObject(root.GetProperty("request"));
            var outcome = RequiredObject(root.GetProperty("outcome"));
            EnsureUniqueProperties(producer);
            EnsureUniqueProperties(session);
            EnsureUniqueProperties(correlation);
            EnsureUniqueProperties(auditRequest);
            EnsureUniqueProperties(outcome);

            var hostId = RequiredString(producer, "host_id");
            _ = RequiredCanonicalUuid(hostId, version: 4);
            var supervisorBootId = RequiredString(producer, "supervisor_boot_id");
            _ = RequiredCanonicalUuid(supervisorBootId, version: 4);
            var workerBootId = NullableString(producer, "worker_boot_id");
            if (workerBootId is not null) _ = RequiredCanonicalUuid(workerBootId, version: 4);
            var producerVersion = RequiredNonemptyString(producer, "version");

            var sessionName = NullableString(session, "name");
            var sessionGeneration = NullableInt64(session, "generation");
            var callId = NullableString(correlation, "call_id");
            if (callId is not null) _ = RequiredCanonicalUuid(callId, version: 7);
            var jobId = NullableInt64(correlation, "job_id");
            var traceId = NullableString(correlation, "trace_id");
            if (traceId is not null &&
                (!IsLowerHex(traceId, 32) || traceId.All(character => character == '0')))
            {
                Fail("trace_id");
            }
            var outcomeState = NullableString(outcome, "state");
            var terminationCertainty = NullableString(outcome, "termination_certainty");

            var scriptEvidenceId = NullableCanonicalUuid(
                auditRequest,
                "script_evidence_id",
                version: 4);
            var originalScriptDigest = NullableString(
                auditRequest,
                "original_script_digest");
            Guid? evidenceSubjectId = null;
            string? evidenceSubjectDigest = null;
            long? evidenceSubjectBytes = null;
            string? evidenceSubjectState = null;
            string? retentionReason = null;
            if (isV2)
            {
                evidenceSubjectId = NullableCanonicalUuid(
                    auditRequest,
                    "evidence_subject_id",
                    version: 4);
                evidenceSubjectDigest = NullableString(
                    auditRequest,
                    "evidence_subject_digest");
                evidenceSubjectBytes = NullableInt64(
                    auditRequest,
                    "evidence_subject_bytes");
                evidenceSubjectState = NullableString(
                    auditRequest,
                    "evidence_subject_state");
                retentionReason = NullableString(
                    auditRequest,
                    "retention_reason");
                AuditEventSerializer.ValidateEvidenceRetentionRequestFacts(
                    eventType,
                    new AuditRequest
                    {
                        OriginalScriptDigest = originalScriptDigest,
                        ScriptEvidenceId = scriptEvidenceId,
                        EvidenceSubjectId = evidenceSubjectId,
                        EvidenceSubjectDigest = evidenceSubjectDigest,
                        EvidenceSubjectBytes = evidenceSubjectBytes,
                        EvidenceSubjectState = evidenceSubjectState,
                        RetentionReason = retentionReason,
                    });
            }

            AuditOperatorDispositionFacts? dispositionFacts = null;
            if (isV2 &&
                root.GetProperty("operator_disposition") is var dispositionElement &&
                dispositionElement.ValueKind != JsonValueKind.Null)
            {
                var disposition = RequiredObject(dispositionElement);
                RequireExactProperties(disposition, OperatorDispositionProperties);
                dispositionFacts = new AuditOperatorDispositionFacts
                {
                    DispositionId = NullableCanonicalUuid(
                        disposition,
                        "disposition_id",
                        version: 4),
                    TargetSupervisorBootId = RequiredCanonicalUuid(
                        RequiredString(disposition, "target_supervisor_boot_id"),
                        version: 4),
                    TargetSpoolFile = NullableString(disposition, "target_spool_file"),
                    TargetStartOffset = NullableInt64(disposition, "target_start_offset"),
                    TargetNextOffset = NullableInt64(disposition, "target_next_offset"),
                    TargetSequence = NullableInt64(disposition, "target_sequence"),
                    TargetEventId = RequiredCanonicalUuid(
                        RequiredString(disposition, "target_event_id"),
                        version: 7),
                    FailureClass = NullableString(disposition, "failure_class"),
                    DetailCode = NullableString(disposition, "detail_code"),
                    ResponseDigest = NullableString(disposition, "response_digest"),
                    FirstFailureUtc = NullableUtc(disposition, "first_failure_utc"),
                    TargetExportConfigurationIdentity = NullableString(
                        disposition,
                        "target_export_configuration_identity"),
                    ProofKind = RequiredString(disposition, "proof_kind"),
                    VerifiedReceiptDigest = NullableString(
                        disposition,
                        "verified_receipt_digest"),
                    AcknowledgedGapReason = NullableString(
                        disposition,
                        "acknowledged_gap_reason"),
                };
            }
            if (isV2)
            {
                AuditEventSerializer.ValidateOperatorDispositionFacts(
                    eventType,
                    dispositionFacts);
            }
            var dispositionId = dispositionFacts?.DispositionId?.ToString("D");
            var dispositionTargetBootId =
                dispositionFacts?.TargetSupervisorBootId.ToString("D");
            var dispositionTargetEventId = dispositionFacts?.TargetEventId.ToString("D");
            var dispositionProofKind = dispositionFacts?.ProofKind;
            var dispositionFailureClass = dispositionFacts?.FailureClass;
            var dispositionTargetExportIdentity =
                dispositionFacts?.TargetExportConfigurationIdentity;
            var dispositionVerifiedReceiptDigest =
                dispositionFacts?.VerifiedReceiptDigest;
            var dispositionAcknowledgedGapReason =
                dispositionFacts?.AcknowledgedGapReason;

            var resource = new OtlpResource();
            resource.Attributes.Add(StringAttribute("service.namespace", "ptk"));
            resource.Attributes.Add(StringAttribute("service.name", "powershell-token-killer"));
            resource.Attributes.Add(StringAttribute("service.version", producerVersion));
            resource.Attributes.Add(StringAttribute("service.instance.id", supervisorBootId));
            resource.Attributes.Add(StringAttribute("host.id", hostId));

            var scope = new InstrumentationScope
            {
                Name = "PtkMcpServer.Audit",
                Version = producerVersion,
            };
            var logRecord = new LogRecord
            {
                TimeUnixNano = ToUnixNanoseconds(occurred),
                ObservedTimeUnixNano = ToUnixNanoseconds(observed),
                EventName = $"ptk.audit.{eventType}",
                Body = new AnyValue { StringValue = bodyText },
            };
            if (traceId is not null)
                logRecord.TraceId = ByteString.CopyFrom(Convert.FromHexString(traceId));

            logRecord.Attributes.Add(StringAttribute("ptk.audit.schema_version", schemaVersion));
            logRecord.Attributes.Add(StringAttribute("ptk.audit.event_id", eventIdText));
            logRecord.Attributes.Add(StringAttribute("ptk.audit.event_type", eventType));
            logRecord.Attributes.Add(IntAttribute("ptk.audit.sequence", sequence));
            AddOptionalString(logRecord, "ptk.audit.previous_event_hash", previousHash);
            logRecord.Attributes.Add(StringAttribute("ptk.audit.event_hash", eventHash));
            logRecord.Attributes.Add(StringAttribute("ptk.supervisor.boot_id", supervisorBootId));
            AddOptionalString(logRecord, "ptk.worker.boot_id", workerBootId);
            AddOptionalString(logRecord, "ptk.session.name", sessionName);
            AddOptionalInt(logRecord, "ptk.session.generation", sessionGeneration);
            AddOptionalString(logRecord, "ptk.call.id", callId);
            AddOptionalInt(logRecord, "ptk.job.id", jobId);
            AddOptionalString(logRecord, "ptk.outcome.state", outcomeState);
            AddOptionalString(logRecord, "ptk.termination.certainty", terminationCertainty);
            AddOptionalString(
                logRecord,
                "ptk.evidence.subject.id",
                evidenceSubjectId?.ToString("D"));
            AddOptionalString(
                logRecord,
                "ptk.evidence.subject.digest",
                evidenceSubjectDigest);
            AddOptionalInt(
                logRecord,
                "ptk.evidence.subject.bytes",
                evidenceSubjectBytes);
            AddOptionalString(
                logRecord,
                "ptk.evidence.subject.state",
                evidenceSubjectState);
            AddOptionalString(
                logRecord,
                "ptk.evidence.retention.reason",
                retentionReason);
            AddOptionalString(logRecord, "ptk.disposition.id", dispositionId);
            AddOptionalString(
                logRecord,
                "ptk.disposition.target.boot_id",
                dispositionTargetBootId);
            AddOptionalString(
                logRecord,
                "ptk.disposition.target.event_id",
                dispositionTargetEventId);
            AddOptionalString(
                logRecord,
                "ptk.disposition.proof_kind",
                dispositionProofKind);
            AddOptionalString(
                logRecord,
                "ptk.disposition.failure_class",
                dispositionFailureClass);
            AddOptionalString(
                logRecord,
                "ptk.disposition.target.export_configuration_identity",
                dispositionTargetExportIdentity);
            AddOptionalString(
                logRecord,
                "ptk.disposition.verified_receipt_digest",
                dispositionVerifiedReceiptDigest);
            AddOptionalString(
                logRecord,
                "ptk.disposition.acknowledged_gap_reason",
                dispositionAcknowledgedGapReason);

            var scopeLogs = new ScopeLogs { Scope = scope };
            scopeLogs.LogRecords.Add(logRecord);
            var resourceLogs = new ResourceLogs { Resource = resource };
            resourceLogs.ScopeLogs.Add(scopeLogs);
            var request = new ExportLogsServiceRequest();
            request.ResourceLogs.Add(resourceLogs);
            return new AuditOtlpRecord(request.ToByteArray(), exactBody, eventId, eventHash);
        }
        catch (AuditOtlpMappingException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new AuditOtlpMappingException("invalid_record");
        }
    }

    private static void AddOptionalString(LogRecord record, string key, string? value)
    {
        if (value is not null) record.Attributes.Add(StringAttribute(key, value));
    }

    private static void AddOptionalInt(LogRecord record, string key, long? value)
    {
        if (value is not null) record.Attributes.Add(IntAttribute(key, value.Value));
    }

    private static KeyValue StringAttribute(string key, string value) => new()
    {
        Key = key,
        Value = new AnyValue { StringValue = value },
    };

    private static KeyValue IntAttribute(string key, long value) => new()
    {
        Key = key,
        Value = new AnyValue { IntValue = value },
    };

    private static JsonElement RequiredObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) Fail("object_kind");
        return element;
    }

    private static void EnsureUniqueProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) Fail("duplicate_property");
        }
    }

    private static void RequireExactProperties(
        JsonElement element,
        IReadOnlySet<string> expected)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!observed.Add(property.Name) || !expected.Contains(property.Name))
                Fail("disposition_shape");
        }
        if (!observed.SetEquals(expected))
            Fail("disposition_shape");
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        var element = root.GetProperty(propertyName);
        if (element.ValueKind != JsonValueKind.String) Fail("string_kind");
        return element.GetString() ?? throw new FormatException();
    }

    private static string RequiredNonemptyString(JsonElement root, string propertyName)
    {
        var value = RequiredString(root, propertyName);
        if (value.Length == 0) Fail("string_empty");
        return value;
    }

    private static string? NullableString(JsonElement root, string propertyName)
    {
        var element = root.GetProperty(propertyName);
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) Fail("nullable_string_kind");
        return element.GetString() ?? throw new FormatException();
    }

    private static long RequiredPositiveInt64(JsonElement root, string propertyName)
    {
        var value = RequiredInt64(root.GetProperty(propertyName));
        if (value < 1) Fail("positive_int64");
        return value;
    }

    private static long? NullableInt64(JsonElement root, string propertyName)
    {
        var element = root.GetProperty(propertyName);
        return element.ValueKind == JsonValueKind.Null ? null : RequiredInt64(element);
    }

    private static Guid? NullableCanonicalUuid(
        JsonElement root,
        string propertyName,
        int version)
    {
        var value = NullableString(root, propertyName);
        return value is null ? null : RequiredCanonicalUuid(value, version);
    }

    private static long RequiredInt64(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number) Fail("int64_kind");
        if (!element.TryGetInt64(out var value)) Fail("int64_kind");
        return value;
    }

    private static Guid RequiredCanonicalUuid(string value, int version)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal) ||
            value[14] != (char)('0' + version) ||
            value[19] is not ('8' or '9' or 'a' or 'b'))
        {
            Fail("uuid");
        }
        return parsed;
    }

    private static DateTimeOffset RequiredUtc(JsonElement root, string propertyName)
    {
        var value = RequiredString(root, propertyName);
        if (!DateTimeOffset.TryParseExact(
                value,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) ||
            !string.Equals(value, parsed.ToString(TimestampFormat, CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            Fail("timestamp");
        }
        return parsed;
    }

    private static DateTimeOffset? NullableUtc(JsonElement root, string propertyName)
    {
        var element = root.GetProperty(propertyName);
        return element.ValueKind == JsonValueKind.Null
            ? null
            : RequiredUtc(root, propertyName);
    }

    private static ulong ToUnixNanoseconds(DateTimeOffset value) =>
        checked((ulong)(value.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) * 100UL);

    private static bool IsLowerHex(string? value, int length) =>
        value is { Length: var actualLength } &&
        actualLength == length &&
        value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool HasUtf8Bom(ReadOnlySpan<byte> value) =>
        value.Length >= 3 && value[0] == 0xef && value[1] == 0xbb && value[2] == 0xbf;

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    [DoesNotReturn]
    private static void Fail(string failureCode) => throw new AuditOtlpMappingException(failureCode);
}
