using System.Security.Cryptography;
using System.Text;

namespace PtkMcpServer.Tests;

internal static class AuditCoreSchemaTestRecords
{
    private const string V1 = "\"schema_version\":\"ptk.audit/1\"";
    private const string V2 = "\"schema_version\":\"ptk.audit/2\"";
    private const string DestinationFields =
        ",\"destination_kind\":null,\"destination_path\":null";
    private const string RetentionFields =
        ",\"evidence_subject_id\":null,\"evidence_subject_digest\":null," +
        "\"evidence_subject_bytes\":null,\"evidence_subject_state\":null," +
        "\"retention_reason\":null";
    private const string NullDisposition = ",\"operator_disposition\":null";
    private const string EventHashMarker = ",\"event_hash\":\"";

    internal static byte[] ToLegacyV1(ReadOnlyMemory<byte> v2Line)
    {
        var preHash = PreHashText(v2Line);
        preHash = ReplaceOnce(preHash, V2, V1);
        preHash = ReplaceOnce(preHash, DestinationFields, string.Empty);
        preHash = ReplaceOnce(preHash, RetentionFields, string.Empty);
        preHash = ReplaceOnce(preHash, NullDisposition, string.Empty);
        return WithRecomputedHash(preHash);
    }

    internal static byte[] RelabelV2AsV1WithoutShrinking(ReadOnlyMemory<byte> v2Line) =>
        WithRecomputedHash(ReplaceOnce(PreHashText(v2Line), V2, V1));

    internal static byte[] RelabelV1AsV2WithoutExpanding(ReadOnlyMemory<byte> v1Line) =>
        WithRecomputedHash(ReplaceOnce(PreHashText(v1Line), V1, V2));

    private static string PreHashText(ReadOnlyMemory<byte> line)
    {
        if (line.Length < 2 || line.Span[^1] != (byte)'\n')
            throw new ArgumentException("The test audit record is not JSONL.", nameof(line));
        var body = Encoding.UTF8.GetString(line.Span[..^1]);
        var marker = body.LastIndexOf(EventHashMarker, StringComparison.Ordinal);
        if (marker < 1 || body[^1] != '}')
            throw new ArgumentException("The test audit record has no final event hash.", nameof(line));
        return body[..marker] + '}';
    }

    private static byte[] WithRecomputedHash(string preHash)
    {
        if (preHash.Length < 2 || preHash[^1] != '}')
            throw new ArgumentException("The test audit pre-hash body is invalid.", nameof(preHash));
        var preHashBytes = Encoding.UTF8.GetBytes(preHash);
        var hash = Convert.ToHexString(SHA256.HashData(preHashBytes)).ToLowerInvariant();
        return Encoding.UTF8.GetBytes(
            preHash[..^1] + EventHashMarker + hash + "\"}\n");
    }

    private static string ReplaceOnce(string value, string oldValue, string newValue)
    {
        var first = value.IndexOf(oldValue, StringComparison.Ordinal);
        if (first < 0 ||
            value.IndexOf(oldValue, first + oldValue.Length, StringComparison.Ordinal) >= 0)
        {
            throw new ArgumentException("The test audit record does not have the expected v2 shape.");
        }
        return value[..first] + newValue + value[(first + oldValue.Length)..];
    }
}
