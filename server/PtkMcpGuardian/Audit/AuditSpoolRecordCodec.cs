using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PtkMcpServer.Audit;

internal readonly record struct AuditSpoolRecord(
    Guid EventId,
    long Sequence,
    string? PreviousEventHash,
    string EventHash);

/// <summary>
/// Validates the integrity and chain identity fields of one authoritative
/// supported ptk.audit core JSON object. The caller owns JSONL framing and removes the LF
/// before parsing; the exact input bytes remain authoritative for export.
/// </summary>
internal static class AuditSpoolRecordCodec
{
    private static readonly byte[] EventHashMarker =
        Encoding.ASCII.GetBytes(",\"event_hash\":\"");

    internal static AuditSpoolRecord Parse(
        ReadOnlySpan<byte> utf8Json,
        Guid expectedSupervisorBootId)
    {
        if (!AuditSpoolSegmentIdentity.IsUuidV4(expectedSupervisorBootId))
        {
            throw new ArgumentException(
                "A spool record requires an expected UUIDv4 supervisor boot identity.",
                nameof(expectedSupervisorBootId));
        }
        if (utf8Json.Length < EventHashMarker.Length + 66)
            throw new IOException("An audit spool record is truncated.");

        var markerOffset = utf8Json.Length - EventHashMarker.Length - 66;
        if (!utf8Json.Slice(markerOffset, EventHashMarker.Length).SequenceEqual(EventHashMarker) ||
            utf8Json[^2] != (byte)'"' || utf8Json[^1] != (byte)'}')
        {
            throw new IOException("An audit spool record does not end with event_hash.");
        }

        var hashText = Encoding.ASCII.GetString(
            utf8Json.Slice(markerOffset + EventHashMarker.Length, 64));
        if (!IsLowerHex(hashText, 64))
            throw new IOException("An audit spool event hash is invalid.");

        var preHash = new byte[markerOffset + 1];
        utf8Json[..markerOffset].CopyTo(preHash);
        preHash[^1] = (byte)'}';
        var computed = Convert.ToHexString(SHA256.HashData(preHash)).ToLowerInvariant();
        if (!string.Equals(computed, hashText, StringComparison.Ordinal))
            throw new IOException("An audit spool event hash does not match its bytes.");

        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            var root = document.RootElement;
            _ = AuditEvidenceSpoolScanner.ValidateExactEnvelopeShape(root);
            var supervisorBootIdText = root
                .GetProperty("producer")
                .GetProperty("supervisor_boot_id")
                .GetString();
            if (root.GetProperty("event_hash").GetString() != hashText ||
                !string.Equals(
                    supervisorBootIdText,
                    expectedSupervisorBootId.ToString("D"),
                    StringComparison.Ordinal))
            {
                throw new IOException("An audit spool record has invalid identity metadata.");
            }

            var eventIdText = root.GetProperty("event_id").GetString();
            if (eventIdText is null ||
                !Guid.TryParseExact(eventIdText, "D", out var eventId) ||
                !string.Equals(eventIdText, eventId.ToString("D"), StringComparison.Ordinal) ||
                !IsUuidV7(eventId))
            {
                throw new IOException("An audit spool event ID is not a canonical UUIDv7.");
            }

            var sequence = root.GetProperty("sequence").GetInt64();
            if (sequence < 1)
                throw new IOException("An audit spool sequence is invalid.");
            var previousElement = root.GetProperty("previous_event_hash");
            var previousHash = previousElement.ValueKind == JsonValueKind.Null
                ? null
                : previousElement.GetString();
            if (sequence == 1
                    ? previousHash is not null
                    : !IsLowerHex(previousHash, 64))
            {
                throw new IOException("An audit spool previous hash is invalid.");
            }

            return new AuditSpoolRecord(
                eventId,
                sequence,
                previousHash,
                hashText);
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
                InvalidOperationException or
                KeyNotFoundException or
                FormatException)
        {
            throw new IOException("An audit spool record is invalid.");
        }
    }

    private static bool IsLowerHex(string? value, int length) =>
        value is { Length: var actualLength } &&
        actualLength == length &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsUuidV7(Guid value)
    {
        var text = value.ToString("D");
        return text[14] == '7' && text[19] is '8' or '9' or 'a' or 'b';
    }
}
