using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PtkMcpServer.Audit;

internal enum AuditExportFailureClass
{
    Configuration,
    PartialRejection,
    Data,
    Protocol,
}

internal sealed class AuditExportBlockedRecord
{
    internal AuditExportBlockedRecord(
        AuditSpoolSegmentIdentity spool,
        long byteOffset,
        long sequence,
        Guid eventId,
        AuditExportFailureClass failureClass,
        string detailCode,
        string? responseDigest,
        DateTimeOffset firstFailureUtc,
        string exportConfigurationIdentity)
    {
        Spool = spool;
        ByteOffset = byteOffset;
        Sequence = sequence;
        EventId = eventId;
        FailureClass = failureClass;
        DetailCode = detailCode;
        ResponseDigest = responseDigest;
        FirstFailureUtc = firstFailureUtc;
        ExportConfigurationIdentity = exportConfigurationIdentity;
        AuditExportCheckpointCodec.ValidateBlockedRecord(this);
    }

    internal AuditSpoolSegmentIdentity Spool { get; }

    internal long ByteOffset { get; }

    internal long Sequence { get; }

    internal Guid EventId { get; }

    internal AuditExportFailureClass FailureClass { get; }

    internal string DetailCode { get; }

    internal string? ResponseDigest { get; }

    internal DateTimeOffset FirstFailureUtc { get; }

    internal string ExportConfigurationIdentity { get; }
}

internal sealed class AuditExportCheckpoint
{
    internal AuditExportCheckpoint(
        Guid supervisorBootId,
        bool chainComplete,
        AuditSpoolSegmentIdentity? spool,
        long byteOffset,
        long sequence,
        Guid? acknowledgedEventId,
        AuditExportBlockedRecord? blockedRecord)
    {
        SupervisorBootId = supervisorBootId;
        ChainComplete = chainComplete;
        Spool = spool;
        ByteOffset = byteOffset;
        Sequence = sequence;
        AcknowledgedEventId = acknowledgedEventId;
        BlockedRecord = blockedRecord;
        AuditExportCheckpointCodec.Validate(this);
    }

    internal Guid SupervisorBootId { get; }

    internal bool ChainComplete { get; }

    internal AuditSpoolSegmentIdentity? Spool { get; }

    internal long ByteOffset { get; }

    internal long Sequence { get; }

    internal Guid? AcknowledgedEventId { get; }

    internal AuditExportBlockedRecord? BlockedRecord { get; }

    internal static AuditExportCheckpoint Initial(Guid supervisorBootId) =>
        new(supervisorBootId, false, null, 0, 0, null, null);
}

/// <summary>
/// Canonical, strict codec for one per-supervisor export cursor. These bytes
/// are local control state, not a core audit event and never an OTLP payload.
/// </summary>
internal static partial class AuditExportCheckpointCodec
{
    internal const string SchemaVersion = "ptk.export-checkpoint/1";
    internal const int MaximumBytes = 16 * 1024;
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private const int MaximumDetailCodeCharacters = 128;
    private const string InvalidMessage = "The audit export checkpoint is invalid.";

    private static readonly HashSet<string> CheckpointProperties = new(
        [
            "schema_version",
            "supervisor_boot_id",
            "chain_complete",
            "spool_file",
            "byte_offset",
            "sequence",
            "acknowledged_event_id",
            "blocked_record",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> BlockedRecordProperties = new(
        [
            "spool_file",
            "byte_offset",
            "sequence",
            "event_id",
            "failure_class",
            "detail_code",
            "response_digest",
            "first_failure_utc",
            "export_configuration_identity",
        ],
        StringComparer.Ordinal);

    [GeneratedRegex(
        "^[a-z0-9]+(?:[._-][a-z0-9]+)*\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DetailCodePattern();

    [GeneratedRegex(
        "^[0-9a-f]{64}\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex LowerHexDigestPattern();

    internal static byte[] Serialize(AuditExportCheckpoint checkpoint)
    {
        Validate(checkpoint);
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.Default,
                       Indented = false,
                       SkipValidation = false,
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", SchemaVersion);
            writer.WriteString(
                "supervisor_boot_id",
                checkpoint.SupervisorBootId.ToString("D"));
            writer.WriteBoolean("chain_complete", checkpoint.ChainComplete);
            WriteSpool(writer, "spool_file", checkpoint.Spool);
            writer.WriteNumber("byte_offset", checkpoint.ByteOffset);
            writer.WriteNumber("sequence", checkpoint.Sequence);
            WriteGuid(writer, "acknowledged_event_id", checkpoint.AcknowledgedEventId);
            writer.WritePropertyName("blocked_record");
            if (checkpoint.BlockedRecord is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                WriteBlockedRecord(writer, checkpoint.BlockedRecord);
            }
            writer.WriteEndObject();
            writer.Flush();
        }

        if (buffer.WrittenCount >= MaximumBytes)
            throw new ArgumentException(InvalidMessage, nameof(checkpoint));
        var bytes = new byte[checked(buffer.WrittenCount + 1)];
        buffer.WrittenSpan.CopyTo(bytes);
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    internal static AuditExportCheckpoint Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            if (bytes.Length is < 2 or > MaximumBytes ||
                bytes.Span[^1] != (byte)'\n' ||
                HasUtf8Bom(bytes.Span))
            {
                throw new FormatException();
            }

            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            var root = document.RootElement;
            RequireObjectWithProperties(root, CheckpointProperties);
            if (!string.Equals(
                    RequiredString(root, "schema_version"),
                    SchemaVersion,
                    StringComparison.Ordinal))
            {
                throw new FormatException();
            }

            var checkpoint = new AuditExportCheckpoint(
                RequiredUuid(root, "supervisor_boot_id", version: 4),
                RequiredBoolean(root, "chain_complete"),
                NullableSpool(root, "spool_file"),
                RequiredInt64(root, "byte_offset"),
                RequiredInt64(root, "sequence"),
                NullableUuid(root, "acknowledged_event_id", version: 7),
                NullableBlockedRecord(root, "blocked_record"));
            var canonical = Serialize(checkpoint);
            if (!bytes.Span.SequenceEqual(canonical))
                throw new FormatException();
            return checkpoint;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new IOException(InvalidMessage);
        }
    }

    internal static void Validate(AuditExportCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        AuditSpoolSegmentIdentity.RequireUuidV4(
            checkpoint.SupervisorBootId,
            nameof(checkpoint.SupervisorBootId));
        if (checkpoint.Sequence < 0 || checkpoint.ByteOffset < 0)
            throw new ArgumentException(InvalidMessage, nameof(checkpoint));

        if (checkpoint.Sequence == 0)
        {
            if (checkpoint.Spool is not null ||
                checkpoint.ByteOffset != 0 ||
                checkpoint.AcknowledgedEventId is not null)
            {
                throw new ArgumentException(InvalidMessage, nameof(checkpoint));
            }
        }
        else
        {
            if (checkpoint.Spool is not { } spool ||
                spool.SupervisorBootId != checkpoint.SupervisorBootId ||
                checkpoint.ByteOffset < 1 ||
                checkpoint.AcknowledgedEventId is not { } eventId ||
                !IsUuidVersion(eventId, 7))
            {
                throw new ArgumentException(InvalidMessage, nameof(checkpoint));
            }
        }

        if (checkpoint.ChainComplete && checkpoint.BlockedRecord is not null)
            throw new ArgumentException(InvalidMessage, nameof(checkpoint));
        if (checkpoint.BlockedRecord is { } blocked)
        {
            ValidateBlockedRecord(blocked);
            if (blocked.Spool.SupervisorBootId != checkpoint.SupervisorBootId ||
                checkpoint.Sequence == long.MaxValue ||
                blocked.Sequence != checkpoint.Sequence + 1)
            {
                throw new ArgumentException(InvalidMessage, nameof(checkpoint));
            }

            if (checkpoint.Spool is null)
            {
                if (blocked.Spool.Index != 0 || blocked.ByteOffset != 0)
                    throw new ArgumentException(InvalidMessage, nameof(checkpoint));
            }
            else if (blocked.Spool.Index == checkpoint.Spool.Value.Index)
            {
                if (blocked.ByteOffset != checkpoint.ByteOffset)
                    throw new ArgumentException(InvalidMessage, nameof(checkpoint));
            }
            else if (blocked.Spool.Index != checkpoint.Spool.Value.Index + 1 ||
                     blocked.ByteOffset != 0)
            {
                throw new ArgumentException(InvalidMessage, nameof(checkpoint));
            }
        }
    }

    internal static void ValidateBlockedRecord(AuditExportBlockedRecord blocked)
    {
        ArgumentNullException.ThrowIfNull(blocked);
        if (!AuditSpoolSegmentIdentity.IsUuidV4(blocked.Spool.SupervisorBootId) ||
            blocked.ByteOffset < 0 ||
            blocked.Sequence < 1 ||
            !IsUuidVersion(blocked.EventId, 7) ||
            !Enum.IsDefined(blocked.FailureClass) ||
            string.IsNullOrEmpty(blocked.DetailCode) ||
            blocked.DetailCode.Length > MaximumDetailCodeCharacters ||
            !DetailCodePattern().IsMatch(blocked.DetailCode) ||
            !IsLowerHexDigest(blocked.ResponseDigest, nullable: true) ||
            blocked.FirstFailureUtc.Offset != TimeSpan.Zero ||
            !IsLowerHexDigest(blocked.ExportConfigurationIdentity, nullable: false))
        {
            throw new ArgumentException(InvalidMessage, nameof(blocked));
        }
    }

    private static void WriteBlockedRecord(
        Utf8JsonWriter writer,
        AuditExportBlockedRecord blocked)
    {
        writer.WriteStartObject();
        writer.WriteString("spool_file", blocked.Spool.FileName);
        writer.WriteNumber("byte_offset", blocked.ByteOffset);
        writer.WriteNumber("sequence", blocked.Sequence);
        writer.WriteString("event_id", blocked.EventId.ToString("D"));
        writer.WriteString("failure_class", FailureClassText(blocked.FailureClass));
        writer.WriteString("detail_code", blocked.DetailCode);
        if (blocked.ResponseDigest is null) writer.WriteNull("response_digest");
        else writer.WriteString("response_digest", blocked.ResponseDigest);
        writer.WriteString(
            "first_failure_utc",
            blocked.FirstFailureUtc.ToUniversalTime().ToString(
                TimestampFormat,
                CultureInfo.InvariantCulture));
        writer.WriteString(
            "export_configuration_identity",
            blocked.ExportConfigurationIdentity);
        writer.WriteEndObject();
    }

    private static AuditExportBlockedRecord? NullableBlockedRecord(
        JsonElement root,
        string propertyName)
    {
        var element = root.GetProperty(propertyName);
        if (element.ValueKind == JsonValueKind.Null) return null;
        RequireObjectWithProperties(element, BlockedRecordProperties);
        return new AuditExportBlockedRecord(
            RequiredSpool(element, "spool_file"),
            RequiredInt64(element, "byte_offset"),
            RequiredInt64(element, "sequence"),
            RequiredUuid(element, "event_id", version: 7),
            ParseFailureClass(RequiredString(element, "failure_class")),
            RequiredString(element, "detail_code"),
            NullableString(element, "response_digest"),
            RequiredUtc(element, "first_failure_utc"),
            RequiredString(element, "export_configuration_identity"));
    }

    private static void RequireObjectWithProperties(
        JsonElement element,
        HashSet<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
                throw new FormatException();
        }
        if (!seen.SetEquals(expected)) throw new FormatException();
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        var element = root.GetProperty(propertyName);
        if (element.ValueKind != JsonValueKind.String)
            throw new FormatException();
        return element.GetString()!;
    }

    private static string? NullableString(JsonElement root, string propertyName)
    {
        var element = root.GetProperty(propertyName);
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String)
            throw new FormatException();
        return element.GetString();
    }

    private static bool RequiredBoolean(JsonElement root, string propertyName)
    {
        var element = root.GetProperty(propertyName);
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException(),
        };
    }

    private static long RequiredInt64(JsonElement root, string propertyName)
    {
        var element = root.GetProperty(propertyName);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
            throw new FormatException();
        return value;
    }

    private static Guid RequiredUuid(JsonElement root, string propertyName, int version)
    {
        var value = RequiredString(root, propertyName);
        if (!Guid.TryParseExact(value, "D", out var parsed) ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal) ||
            !IsUuidVersion(parsed, version))
        {
            throw new FormatException();
        }
        return parsed;
    }

    private static Guid? NullableUuid(JsonElement root, string propertyName, int version)
    {
        var element = root.GetProperty(propertyName);
        return element.ValueKind == JsonValueKind.Null
            ? null
            : RequiredUuid(root, propertyName, version);
    }

    private static AuditSpoolSegmentIdentity RequiredSpool(
        JsonElement root,
        string propertyName)
    {
        var value = RequiredString(root, propertyName);
        if (!AuditSpoolSegmentIdentity.TryParse(value, out var spool))
            throw new FormatException();
        return spool;
    }

    private static AuditSpoolSegmentIdentity? NullableSpool(
        JsonElement root,
        string propertyName)
    {
        var element = root.GetProperty(propertyName);
        return element.ValueKind == JsonValueKind.Null
            ? null
            : RequiredSpool(root, propertyName);
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
            !string.Equals(
                value,
                parsed.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new FormatException();
        }
        return parsed;
    }

    private static void WriteSpool(
        Utf8JsonWriter writer,
        string propertyName,
        AuditSpoolSegmentIdentity? spool)
    {
        if (spool is null) writer.WriteNull(propertyName);
        else writer.WriteString(propertyName, spool.Value.FileName);
    }

    private static void WriteGuid(
        Utf8JsonWriter writer,
        string propertyName,
        Guid? value)
    {
        if (value is null) writer.WriteNull(propertyName);
        else writer.WriteString(propertyName, value.Value.ToString("D"));
    }

    private static string FailureClassText(AuditExportFailureClass value) => value switch
    {
        AuditExportFailureClass.Configuration => "configuration",
        AuditExportFailureClass.PartialRejection => "partial_rejection",
        AuditExportFailureClass.Data => "data",
        AuditExportFailureClass.Protocol => "protocol",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static AuditExportFailureClass ParseFailureClass(string value) => value switch
    {
        "configuration" => AuditExportFailureClass.Configuration,
        "partial_rejection" => AuditExportFailureClass.PartialRejection,
        "data" => AuditExportFailureClass.Data,
        "protocol" => AuditExportFailureClass.Protocol,
        _ => throw new FormatException(),
    };

    private static bool IsLowerHexDigest(string? value, bool nullable) =>
        value is null ? nullable : LowerHexDigestPattern().IsMatch(value);

    private static bool IsUuidVersion(Guid value, int version)
    {
        var text = value.ToString("D");
        return text[14] == (char)('0' + version) &&
               text[19] is '8' or '9' or 'a' or 'b';
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> value) =>
        value.Length >= 3 &&
        value[0] == 0xef &&
        value[1] == 0xbb &&
        value[2] == 0xbf;

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
