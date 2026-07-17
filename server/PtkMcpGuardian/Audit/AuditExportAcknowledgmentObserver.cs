using System.Text.Json;

namespace PtkMcpServer.Audit;

/// <summary>
/// Observes one already validated authoritative JSONL record after its remote
/// acknowledgment and before the corresponding durable checkpoint advances.
/// Implementations receive no script-evidence payload and must never pass
/// local evidence bytes to the export transport.
/// </summary>
internal interface IAuditExportAcknowledgmentObserver
{
    IAuditEvidenceAnchorLease ObserveAcknowledgment(
        ReadOnlyMemory<byte> exactJsonlBytes);
}

internal static class AuditExportAcknowledgmentObserver
{
    internal static IAuditExportAcknowledgmentObserver None { get; } =
        new NoOpAcknowledgmentObserver();

    private sealed class NoOpAcknowledgmentObserver :
        IAuditExportAcknowledgmentObserver
    {
        public IAuditEvidenceAnchorLease ObserveAcknowledgment(
            ReadOnlyMemory<byte> exactJsonlBytes) => NoOpAnchorLease.Instance;
    }

    private sealed class NoOpAnchorLease : IAuditEvidenceAnchorLease
    {
        internal static IAuditEvidenceAnchorLease Instance { get; } =
            new NoOpAnchorLease();

        public void CompleteAfterCheckpoint()
        {
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Extracts only the protected evidence identity from the acknowledged core
/// event and durably marks that exact local artifact as externally anchored.
/// </summary>
internal sealed class ScriptEvidenceAcknowledgmentObserver(
    ScriptEvidenceStoreProvider evidence) : IAuditExportAcknowledgmentObserver
{
    private readonly ScriptEvidenceStoreProvider _evidence =
        evidence ?? throw new ArgumentNullException(nameof(evidence));

    public IAuditEvidenceAnchorLease ObserveAcknowledgment(
        ReadOnlyMemory<byte> exactJsonlBytes)
    {
        try
        {
            if (exactJsonlBytes.Length is < 3 or > AuditEventSerializer.MaximumLineBytes ||
                exactJsonlBytes.Span[^1] != (byte)'\n' ||
                exactJsonlBytes.Span[^2] != (byte)'}' ||
                HasUtf8Bom(exactJsonlBytes.Span))
            {
                throw new IOException("The acknowledged audit JSONL framing is invalid.");
            }

            using var document = JsonDocument.Parse(
                exactJsonlBytes[..^1],
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
            var root = RequireObject(document.RootElement);
            EnsureUniqueProperties(root);
            var schemaVersion = RequiredString(root, "schema_version");
            if (!string.Equals(
                    schemaVersion,
                    AuditEventSerializer.LegacySchemaVersion,
                    StringComparison.Ordinal) &&
                !string.Equals(
                    schemaVersion,
                    AuditEventSerializer.CurrentSchemaVersion,
                    StringComparison.Ordinal) &&
                !string.Equals(
                    schemaVersion,
                    AuditEventSerializer.ResilientSchemaVersion,
                    StringComparison.Ordinal))
            {
                throw new IOException("The acknowledged audit schema is invalid.");
            }
            _ = AuditEvidenceSpoolScanner.ValidateExactEnvelopeShape(root);
            var eventIdText = RequiredString(root, "event_id");
            var eventId = RequiredCanonicalUuid(eventIdText, version: 7);
            var sequence = root.GetProperty("sequence").GetInt64();
            if (sequence < 1)
                throw new IOException("The acknowledged audit sequence is invalid.");
            var producer = RequireObject(root.GetProperty("producer"));
            EnsureUniqueProperties(producer);
            var supervisorBootId = RequiredCanonicalUuid(
                RequiredString(producer, "supervisor_boot_id"),
                version: 4);
            var request = RequireObject(root.GetProperty("request"));
            EnsureUniqueProperties(request);

            var evidenceId = NullableString(request, "script_evidence_id");
            var digest = NullableString(request, "original_script_digest");
            if (evidenceId is null && digest is null)
                return AuditExportAcknowledgmentObserver.None
                    .ObserveAcknowledgment(exactJsonlBytes);
            if (evidenceId is null || digest is null ||
                !Guid.TryParseExact(evidenceId, "D", out var parsedId) ||
                !string.Equals(evidenceId, parsedId.ToString("D"), StringComparison.Ordinal) ||
                evidenceId[14] != '4' ||
                evidenceId[19] is not ('8' or '9' or 'a' or 'b') ||
                !IsLowerHex(digest, 64))
            {
                throw new IOException("The acknowledged audit evidence identity is invalid.");
            }

            return _evidence.MarkAnchored(new AuditEvidenceAcknowledgmentPosition(
                supervisorBootId,
                sequence,
                eventId,
                evidenceId,
                digest));
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or
                KeyNotFoundException or FormatException)
        {
            throw new IOException("The acknowledged audit evidence reference is invalid.");
        }
    }

    private static JsonElement RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new IOException("The acknowledged audit evidence container is invalid.");
        return element;
    }

    private static void EnsureUniqueProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new IOException("The acknowledged audit JSON contains duplicate properties.");
        }
    }

    private static string? NullableString(JsonElement root, string propertyName)
    {
        var element = root.GetProperty(propertyName);
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String)
            throw new IOException("The acknowledged audit evidence field is invalid.");
        return element.GetString()
            ?? throw new IOException("The acknowledged audit evidence field is invalid.");
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        var element = root.GetProperty(propertyName);
        if (element.ValueKind != JsonValueKind.String)
            throw new IOException("The acknowledged audit identity field is invalid.");
        return element.GetString()
            ?? throw new IOException("The acknowledged audit identity field is invalid.");
    }

    private static Guid RequiredCanonicalUuid(string value, int version)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal) ||
            value[14] != (char)('0' + version) ||
            value[19] is not ('8' or '9' or 'a' or 'b'))
        {
            throw new IOException("The acknowledged audit UUID is invalid.");
        }
        return parsed;
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool HasUtf8Bom(ReadOnlySpan<byte> value) =>
        value.Length >= 3 &&
        value[0] == 0xef && value[1] == 0xbb && value[2] == 0xbf;
}
