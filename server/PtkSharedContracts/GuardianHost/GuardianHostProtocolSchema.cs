using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PtkSharedContracts;

/// <summary>
/// Evaluates the deliberately small, frozen JSON-Schema vocabulary used by
/// the guardian/host v1 contract. Keeping the schema as the branch authority
/// prevents the executable codec and the published contract from drifting.
/// Cross-frame invariants remain lifecycle-state-machine responsibilities.
/// </summary>
internal static class GuardianHostProtocolSchema
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocument SchemaDocument = LoadSchema();
    private static readonly JsonElement SchemaRoot = SchemaDocument.RootElement;

    internal static void Validate(JsonElement value)
    {
        if (!Matches(value, SchemaRoot))
            throw new GuardianHostProtocolException(
                "invalid_field",
                "Private protocol frame does not match the frozen guardian/host v1 schema.");
    }

    private static bool Matches(JsonElement value, JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out var reference) &&
            !Matches(value, Resolve(reference.GetString()!)))
            return false;

        if (schema.TryGetProperty("type", out var type) && !MatchesType(value, type.GetString()!))
            return false;

        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(value, constant))
            return false;

        if (schema.TryGetProperty("enum", out var enumeration) &&
            !enumeration.EnumerateArray().Any(candidate => JsonElement.DeepEquals(value, candidate)))
            return false;

        if (schema.TryGetProperty("not", out var not) && Matches(value, not))
            return false;

        if (schema.TryGetProperty("allOf", out var allOf) &&
            allOf.EnumerateArray().Any(candidate => !Matches(value, candidate)))
            return false;

        if (schema.TryGetProperty("oneOf", out var oneOf) &&
            oneOf.EnumerateArray().Count(candidate => Matches(value, candidate)) != 1)
            return false;

        if (schema.TryGetProperty("if", out var condition) && Matches(value, condition))
        {
            if (schema.TryGetProperty("then", out var then) && !Matches(value, then)) return false;
        }
        else if (schema.TryGetProperty("else", out var otherwise) && !Matches(value, otherwise))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object && !MatchesObject(value, schema))
            return false;
        if (value.ValueKind == JsonValueKind.Array && !MatchesArray(value, schema))
            return false;
        if (value.ValueKind == JsonValueKind.String && !MatchesString(value, schema))
            return false;
        if (value.ValueKind == JsonValueKind.Number && !MatchesNumber(value, schema))
            return false;

        return MatchesLocalInvariant(value, schema);
    }

    private static bool MatchesObject(JsonElement value, JsonElement schema)
    {
        if (schema.TryGetProperty("required", out var required) &&
            required.EnumerateArray().Any(name => !value.TryGetProperty(name.GetString()!, out _)))
            return false;

        if (schema.TryGetProperty("x-ptk-property-order", out var order))
        {
            var actual = value.EnumerateObject().Select(property => property.Name);
            var expected = order.EnumerateArray().Select(item => item.GetString()!);
            if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) return false;
        }

        if (!schema.TryGetProperty("properties", out var properties)) return true;

        foreach (var property in value.EnumerateObject())
        {
            if (properties.TryGetProperty(property.Name, out var propertySchema))
            {
                if (!Matches(property.Value, propertySchema)) return false;
            }
            else if (schema.TryGetProperty("additionalProperties", out var additional) &&
                     additional.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesArray(JsonElement value, JsonElement schema)
    {
        var count = value.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var minimum) && count < minimum.GetInt32()) return false;
        if (schema.TryGetProperty("maxItems", out var maximum) && count > maximum.GetInt32()) return false;
        if (schema.TryGetProperty("items", out var items) &&
            value.EnumerateArray().Any(item => !Matches(item, items)))
            return false;
        return true;
    }

    private static bool MatchesString(JsonElement value, JsonElement schema)
    {
        var text = value.GetString()!;
        if (!TryCountScalars(text, out var scalarCount)) return false;
        if (schema.TryGetProperty("minLength", out var minimum) && scalarCount < minimum.GetInt32()) return false;
        if (schema.TryGetProperty("maxLength", out var maximum) && scalarCount > maximum.GetInt32()) return false;
        if (schema.TryGetProperty("pattern", out var pattern))
        {
            var patternText = pattern.GetString()!;
            var match = Regex.Match(
                text,
                patternText,
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromSeconds(1));
            if (!match.Success || match.Index != 0 || match.Length != text.Length) return false;
            if (patternText == "^[A-Za-z0-9_-]{43}$" && !ContractValidation.IsCapabilityToken(text))
                return false;
        }
        if (schema.TryGetProperty("x-ptk-maximum-strict-utf8-bytes", out var byteMaximum))
        {
            try
            {
                if (StrictUtf8.GetByteCount(text) > byteMaximum.GetInt32()) return false;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }
        return true;
    }

    private static bool MatchesNumber(JsonElement value, JsonElement schema)
    {
        if (!value.TryGetDecimal(out var parsed) || parsed != decimal.Truncate(parsed))
            return !schema.TryGetProperty("type", out _);
        if (schema.TryGetProperty("minimum", out var minimum) &&
            (!minimum.TryGetDecimal(out var minimumValue) || parsed < minimumValue)) return false;
        if (schema.TryGetProperty("maximum", out var maximum) &&
            (!maximum.TryGetDecimal(out var maximumValue) || parsed > maximumValue)) return false;
        return true;
    }

    private static bool MatchesLocalInvariant(JsonElement value, JsonElement schema)
    {
        if (value.ValueKind != JsonValueKind.Object) return true;

        if (schema.TryGetProperty("x-ptk-invariant", out var invariant))
        {
            switch (invariant.GetString())
            {
                case "call_id=dispatch_capability.call_id":
                    if (value.GetProperty("call_id").GetString() !=
                        value.GetProperty("dispatch_capability").GetProperty("call_id").GetString())
                        return false;
                    break;
                case "intended_pgid=worker_pid":
                    if (value.GetProperty("intended_pgid").GetInt32() !=
                        value.GetProperty("worker_pid").GetInt32())
                        return false;
                    break;
                case "pgid=worker_pid":
                    if (value.GetProperty("pgid").GetInt32() != value.GetProperty("worker_pid").GetInt32())
                        return false;
                    break;
            }
        }

        if (value.TryGetProperty("raw_bytes", out var rawBytes) &&
            value.TryGetProperty("raw_base64", out var rawBase64) &&
            value.TryGetProperty("raw_sha256", out var rawSha256) &&
            !MatchesRawPayload(rawBytes, rawBase64, rawSha256))
            return false;

        if (value.TryGetProperty("total_bytes", out var totalBytes) &&
            value.TryGetProperty("chunk_count", out var chunkCount) &&
            value.TryGetProperty("alias_count", out _))
        {
            var expected = checked((totalBytes.GetInt32() + ContractLimits.MaximumManifestChunkBytes - 1) /
                ContractLimits.MaximumManifestChunkBytes);
            if (chunkCount.GetInt32() != expected) return false;
        }

        if (value.TryGetProperty("chunk_index", out var acceptedChunk) &&
            value.TryGetProperty("next_chunk_index", out var nextChunk) &&
            nextChunk.GetInt32() != acceptedChunk.GetInt32() + 1)
            return false;

        if (value.TryGetProperty("manifest_id", out _) &&
            value.TryGetProperty("chunk_index", out var manifestChunkIndex) &&
            value.TryGetProperty("offset", out var manifestOffset) &&
            manifestOffset.GetInt32() != checked(
                manifestChunkIndex.GetInt32() * ContractLimits.MaximumManifestChunkBytes))
            return false;

        return true;
    }

    private static bool MatchesRawPayload(JsonElement rawBytes, JsonElement rawBase64, JsonElement rawSha256)
    {
        if (!rawBytes.TryGetInt32(out var expectedBytes) || rawBase64.ValueKind != JsonValueKind.String ||
            rawSha256.ValueKind != JsonValueKind.String)
            return false;
        var encoded = rawBase64.GetString()!;
        var maximumDecodedBytes = checked(encoded.Length / 4 * 3);
        var decoded = ArrayPool<byte>.Shared.Rent(Math.Max(1, maximumDecodedBytes));
        try
        {
            if (!Convert.TryFromBase64String(encoded, decoded, out var decodedLength) ||
                decodedLength != expectedBytes ||
                Convert.ToBase64String(decoded, 0, decodedLength) != encoded)
                return false;

            Span<byte> actualDigest = stackalloc byte[32];
            SHA256.HashData(decoded.AsSpan(0, decodedLength), actualDigest);
            return Convert.ToHexString(actualDigest).Equals(
                rawSha256.GetString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(decoded, clearArray: true);
        }
    }

    private static bool MatchesType(JsonElement value, string type) => type switch
    {
        "null" => value.ValueKind == JsonValueKind.Null,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) &&
            number == decimal.Truncate(number),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => false,
    };

    private static JsonElement Resolve(string reference)
    {
        const string prefix = "#/$defs/";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported guardian/host schema reference '{reference}'.");
        return SchemaRoot.GetProperty("$defs").GetProperty(reference[prefix.Length..]);
    }

    private static bool TryCountScalars(string value, out int count)
    {
        count = 0;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out _, out var consumed);
            if (status != System.Buffers.OperationStatus.Done) return false;
            count++;
            remaining = remaining[consumed..];
        }
        return true;
    }

    private static JsonDocument LoadSchema()
    {
        var bytes = ContractResources.ReadExact("guardian-host-protocol.schema.json");
        return JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 128,
        });
    }
}
