using System.Globalization;
using System.Text.Json;

namespace PtkMcpServer.Audit;

/// <summary>
/// Immutable guardian-owned host identity and recovery state captured at the
/// instant an audit event is created. This is deliberately independent of the
/// private host so audit admission never needs a live backend.
/// </summary>
internal sealed record AuditHostSnapshot(
    Guid? BootId,
    long? Generation,
    string State,
    long RecoveryAttempt);

internal static class AuditHostSnapshotCodec
{
    internal const string SchemaVersion = "ptk.audit/3";

    private static readonly HashSet<string> Properties = new(
        ["boot_id", "generation", "state", "recovery_attempt"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> NullIdentityStates = new(
        ["absent", "backoff", "circuit_open", "stopped"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> LiveIdentityStates = new(
        ["starting", "ready", "recovering", "containment_unconfirmed", "half_open"],
        StringComparer.Ordinal);

    internal static void ValidateForSerialization(AuditHostSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TryValidate(snapshot, out var failure))
            throw new AuditEventValidationException($"host: {failure}");
    }

    internal static AuditHostSnapshot ReadExact(JsonElement element)
    {
        try
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw InvalidRead();

            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!observed.Add(property.Name) || !Properties.Contains(property.Name))
                    throw InvalidRead();
            }
            if (!observed.SetEquals(Properties))
                throw InvalidRead();

            var bootId = ReadNullableUuidV4(element.GetProperty("boot_id"));
            var generation = ReadNullableInt64(element.GetProperty("generation"));
            var stateElement = element.GetProperty("state");
            if (stateElement.ValueKind != JsonValueKind.String)
                throw InvalidRead();
            var state = stateElement.GetString();
            var attemptElement = element.GetProperty("recovery_attempt");
            if (attemptElement.ValueKind != JsonValueKind.Number ||
                !attemptElement.TryGetInt64(out var recoveryAttempt))
            {
                throw InvalidRead();
            }

            var snapshot = new AuditHostSnapshot(
                bootId,
                generation,
                state ?? throw InvalidRead(),
                recoveryAttempt);
            if (!TryValidate(snapshot, out _))
                throw InvalidRead();
            return snapshot;
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw InvalidRead();
        }
    }

    internal static void Write(Utf8JsonWriter writer, AuditHostSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ValidateForSerialization(snapshot);

        writer.WriteStartObject("host");
        if (snapshot.BootId is { } bootId)
            writer.WriteString("boot_id", bootId.ToString("D", CultureInfo.InvariantCulture));
        else
            writer.WriteNull("boot_id");
        if (snapshot.Generation is { } generation)
            writer.WriteNumber("generation", generation);
        else
            writer.WriteNull("generation");
        writer.WriteString("state", snapshot.State);
        writer.WriteNumber("recovery_attempt", snapshot.RecoveryAttempt);
        writer.WriteEndObject();
    }

    private static bool TryValidate(AuditHostSnapshot snapshot, out string failure)
    {
        if (snapshot.RecoveryAttempt < 0)
        {
            failure = "recovery_attempt must be in the range 0..Int64.MaxValue";
            return false;
        }

        var hasBootId = snapshot.BootId is not null;
        var hasGeneration = snapshot.Generation is not null;
        if (snapshot.BootId is { } bootId)
        {
            var canonical = bootId.ToString("D", CultureInfo.InvariantCulture);
            if (canonical[14] != '4' || canonical[19] is not ('8' or '9' or 'a' or 'b'))
            {
                failure = "boot_id must be a UUIDv4";
                return false;
            }
        }
        if (hasBootId != hasGeneration)
        {
            failure = "boot_id and generation must be null or nonnull together";
            return false;
        }
        if (snapshot.Generation is <= 0)
        {
            failure = "generation must be null or in the range 1..Int64.MaxValue";
            return false;
        }

        if (NullIdentityStates.Contains(snapshot.State))
        {
            if (hasBootId)
            {
                failure = $"{snapshot.State} requires null boot_id and generation";
                return false;
            }
        }
        else if (LiveIdentityStates.Contains(snapshot.State))
        {
            if (!hasBootId)
            {
                failure = $"{snapshot.State} requires nonnull boot_id and generation";
                return false;
            }
        }
        else
        {
            failure = "state is not a closed ptk.audit/3 host state";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static Guid? ReadNullableUuidV4(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw InvalidRead();
        var value = element.GetString();
        if (value is null ||
            !Guid.TryParseExact(value, "D", out var parsed) ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal) ||
            value[14] != '4' ||
            value[19] is not ('8' or '9' or 'a' or 'b'))
        {
            throw InvalidRead();
        }
        return parsed;
    }

    private static long? ReadNullableInt64(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
            throw InvalidRead();
        return value;
    }

    private static IOException InvalidRead() =>
        new("The ptk.audit/3 host snapshot is invalid.");
}
