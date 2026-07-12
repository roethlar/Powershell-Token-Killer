using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace PtkMcpServer.Audit;

internal sealed record AuditClientContext(
    string? ClientName = null,
    string? ClientVersion = null,
    string? ClientSessionId = null);

internal sealed record AuditOperationProfile(
    int MaximumCallRecordSlots,
    int PersistentJobTerminalSlots,
    bool RequiresScriptEvidence,
    bool MayHaveSideEffects)
{
    internal int MaximumRecordSlots => checked(MaximumCallRecordSlots + PersistentJobTerminalSlots);

    internal long MaximumReservationBytes(int maximumRecordBytes)
    {
        if (maximumRecordBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumRecordBytes));
        return checked((long)MaximumRecordSlots * maximumRecordBytes);
    }
}

internal sealed record AuditCallMetadata(
    AuditActor Actor,
    AuditRequest Request,
    AuditOperationProfile OperationProfile);

/// <summary>
/// Pure validation and normalization at the MCP call boundary. It never
/// persists, executes, logs, or includes submitted script text in core-event
/// metadata or failure text.
/// </summary>
internal static class AuditCallMetadataCapture
{
    private const int MaximumScriptUtf8Bytes = 131_072;
    private const int MaximumClientScalars = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly HashSet<string> InvokeFields =
        new(["script", "raw", "route", "background", "timeoutSeconds"], StringComparer.Ordinal);
    private static readonly HashSet<string> JobFields =
        new(["action", "id", "offset"], StringComparer.Ordinal);
    private static readonly HashSet<string> StateFields =
        new(["listAvailable"], StringComparer.Ordinal);
    private static readonly HashSet<string> NoFields = new(StringComparer.Ordinal);

    internal static bool TryCapture(
        CallToolRequestParams call,
        AuditClientContext client,
        TimeSpan defaultTimeout,
        TimeSpan maximumTimeout,
        DateTimeOffset utcNow,
        out AuditCallMetadata? metadata,
        out string? exactSubmittedScript,
        out string? sanitizedFailure)
    {
        metadata = null;
        exactSubmittedScript = null;
        sanitizedFailure = null;

        if (call is null)
            return Fail("audit_boundary_invalid: request is missing", out sanitizedFailure);
        if (client is null)
            return Fail("audit_boundary_invalid: client context is missing", out sanitizedFailure);
        if (!IsUtc(utcNow))
            return Fail("audit_boundary_invalid: boundary clock is not UTC", out sanitizedFailure);
        if (!TryValidateTimeout(defaultTimeout, "default timeout", out sanitizedFailure) ||
            !TryValidateTimeout(maximumTimeout, "maximum timeout", out sanitizedFailure))
        {
            return false;
        }

        if (!TryCaptureActor(client, out var actor, out sanitizedFailure))
            return false;

        var arguments = call.Arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var providedFields = arguments.Keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (providedFields.Length > 64)
            return Fail("audit_boundary_invalid: too many argument fields", out sanitizedFailure);

        switch (call.Name)
        {
            case "ptk_invoke":
                if (!TryCaptureInvoke(
                        arguments,
                        providedFields,
                        defaultTimeout,
                        maximumTimeout,
                        utcNow,
                        actor,
                        out metadata,
                        out exactSubmittedScript,
                        out sanitizedFailure))
                {
                    exactSubmittedScript = null;
                    return false;
                }
                return true;

            case "ptk_job":
                return TryCaptureJob(arguments, providedFields, actor, out metadata, out sanitizedFailure);

            case "ptk_state":
                return TryCaptureState(arguments, providedFields, actor, out metadata, out sanitizedFailure);

            case "ptk_reset":
                if (!TryRejectUnknownFields(arguments, NoFields, "ptk_reset", out sanitizedFailure))
                    return false;
                metadata = new AuditCallMetadata(
                    actor,
                    BaseRequest("ptk_reset", "reset", providedFields),
                    new AuditOperationProfile(4, 0, RequiresScriptEvidence: false, MayHaveSideEffects: true));
                return true;

            default:
                return Fail("audit_boundary_invalid: unknown tool", out sanitizedFailure);
        }
    }

    private static bool TryCaptureInvoke(
        IDictionary<string, JsonElement> arguments,
        string[] providedFields,
        TimeSpan defaultTimeout,
        TimeSpan maximumTimeout,
        DateTimeOffset utcNow,
        AuditActor actor,
        out AuditCallMetadata? metadata,
        out string? exactSubmittedScript,
        out string? failure)
    {
        metadata = null;
        exactSubmittedScript = null;
        failure = null;

        if (!TryRejectUnknownFields(arguments, InvokeFields, "ptk_invoke", out failure))
            return false;
        if (!TryRequiredString(arguments, "script", out var script, out failure))
            return false;
        if (!TryStrictUtf8Length(script, MaximumScriptUtf8Bytes))
            return Fail("audit_boundary_invalid: ptk_invoke.arguments.script is not representable", out failure);
        if (!TryOptionalBoolean(arguments, "raw", defaultValue: false, out var raw, out failure) ||
            !TryOptionalBoolean(arguments, "background", defaultValue: false, out var background, out failure) ||
            !TryOptionalInt32(arguments, "timeoutSeconds", defaultValue: 0, out var timeoutSeconds, out failure))
        {
            return false;
        }

        string route;
        if (arguments.TryGetValue("route", out var routeElement))
        {
            if (routeElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return Fail("audit_boundary_invalid: ptk_invoke.arguments.route has the wrong JSON kind", out failure);
            route = NormalizeRoute(routeElement.ValueKind == JsonValueKind.Null ? null : routeElement.GetString());
        }
        else
        {
            route = "auto";
        }

        var budget = timeoutSeconds > 0
            ? Min(TimeSpan.FromSeconds(timeoutSeconds), maximumTimeout)
            : defaultTimeout;
        if (!TryMilliseconds(budget, out var timeoutMilliseconds) ||
            !TryAdd(utcNow, budget, out var deadlineUtc))
        {
            return Fail("audit_boundary_invalid: ptk_invoke timeout is not representable", out failure);
        }

        var request = BaseRequest("ptk_invoke", "invoke", providedFields) with
        {
            TimeoutMs = timeoutMilliseconds,
            DeadlineUtc = deadlineUtc,
            Route = route,
            Background = background,
            Raw = raw,
        };
        var profile = new AuditOperationProfile(
            MaximumCallRecordSlots: 9,
            PersistentJobTerminalSlots: background ? 1 : 0,
            RequiresScriptEvidence: true,
            MayHaveSideEffects: true);

        exactSubmittedScript = script;
        metadata = new AuditCallMetadata(actor, request, profile);
        return true;
    }

    private static bool TryCaptureJob(
        IDictionary<string, JsonElement> arguments,
        string[] providedFields,
        AuditActor actor,
        out AuditCallMetadata? metadata,
        out string? failure)
    {
        metadata = null;
        failure = null;
        if (!TryRejectUnknownFields(arguments, JobFields, "ptk_job", out failure))
            return false;
        string? action = null;
        if (arguments.TryGetValue("action", out var actionElement))
        {
            if (actionElement.ValueKind == JsonValueKind.String)
            {
                action = actionElement.GetString()!.ToLowerInvariant();
                if (!IsMachineName(action))
                    return Fail("audit_boundary_invalid: ptk_job.arguments.action is not representable", out failure);
            }
            else if (actionElement.ValueKind != JsonValueKind.Null)
            {
                return Fail("audit_boundary_invalid: ptk_job.arguments.action has the wrong JSON kind", out failure);
            }
        }

        long? jobId = null;
        if (arguments.TryGetValue("id", out var idElement))
        {
            if (idElement.ValueKind != JsonValueKind.Number ||
                !idElement.TryGetInt64(out var value) ||
                value < 1)
            {
                return Fail("audit_boundary_invalid: ptk_job.arguments.id must be a positive int64", out failure);
            }
            jobId = value;
        }
        long? offset = null;
        if (arguments.TryGetValue("offset", out var offsetElement))
        {
            if (offsetElement.ValueKind != JsonValueKind.Number ||
                !offsetElement.TryGetInt64(out var value) ||
                value < 0)
            {
                return Fail("audit_boundary_invalid: ptk_job.arguments.offset must be a nonnegative int64", out failure);
            }
            offset = value;
        }
        else if (action == "output")
        {
            offset = 0;
        }

        if (action is "status" or "output" or "kill" && !jobId.HasValue)
        {
            return Fail(
                "audit_boundary_invalid: ptk_job.arguments.id is required for this action",
                out failure);
        }

        var request = BaseRequest("ptk_job", action, providedFields) with
        {
            JobId = action is "status" or "output" or "kill" ? jobId : null,
            Offset = action == "output" ? offset : null,
        };
        metadata = new AuditCallMetadata(
            actor,
            request,
            new AuditOperationProfile(
                action switch
                {
                    "output" => 5,
                    "kill" => 4,
                    "list" or "status" => 3,
                    _ => 2,
                },
                0,
                RequiresScriptEvidence: false,
                MayHaveSideEffects: action is "kill" or "output"));
        return true;
    }

    private static bool TryCaptureState(
        IDictionary<string, JsonElement> arguments,
        string[] providedFields,
        AuditActor actor,
        out AuditCallMetadata? metadata,
        out string? failure)
    {
        metadata = null;
        failure = null;
        if (!TryRejectUnknownFields(arguments, StateFields, "ptk_state", out failure) ||
            !TryOptionalBoolean(arguments, "listAvailable", false, out var listAvailable, out failure))
        {
            return false;
        }

        metadata = new AuditCallMetadata(
            actor,
            BaseRequest("ptk_state", "state", providedFields) with { ListAvailable = listAvailable },
            new AuditOperationProfile(5, 0, RequiresScriptEvidence: false, MayHaveSideEffects: true));
        return true;
    }

    private static AuditRequest BaseRequest(string tool, string? action, IReadOnlyList<string> providedFields) => new()
    {
        Tool = tool,
        Action = action,
        ProvidedFields = providedFields,
        SessionRequested = "default",
    };

    private static bool TryCaptureActor(
        AuditClientContext client,
        out AuditActor actor,
        out string? failure)
    {
        actor = null!;
        failure = null;
        if (!TryClientText(client.ClientName, "client name", out failure) ||
            !TryClientText(client.ClientVersion, "client version", out failure) ||
            !TryClientText(client.ClientSessionId, "client session id", out failure))
        {
            return false;
        }

        var asserted = client.ClientName is not null ||
                       client.ClientVersion is not null ||
                       client.ClientSessionId is not null;
        actor = new AuditActor
        {
            Transport = "mcp_stdio",
            ClientName = client.ClientName,
            ClientVersion = client.ClientVersion,
            ClientSessionId = client.ClientSessionId,
            AttributionStrength = asserted ? "client_asserted" : "transport_only",
        };
        return true;
    }

    private static bool TryRejectUnknownFields(
        IDictionary<string, JsonElement> arguments,
        HashSet<string> allowed,
        string tool,
        out string? failure)
    {
        foreach (var key in arguments.Keys)
        {
            if (!allowed.Contains(key))
                return Fail($"audit_boundary_invalid: {tool} contains an unknown argument field", out failure);
        }
        failure = null;
        return true;
    }

    private static bool TryRequiredString(
        IDictionary<string, JsonElement> arguments,
        string name,
        out string value,
        out string? failure)
    {
        value = string.Empty;
        if (!arguments.TryGetValue(name, out var element))
            return Fail($"audit_boundary_invalid: required argument {name} is missing", out failure);
        if (element.ValueKind != JsonValueKind.String)
            return Fail($"audit_boundary_invalid: argument {name} has the wrong JSON kind", out failure);
        value = element.GetString()!;
        failure = null;
        return true;
    }

    private static bool TryOptionalBoolean(
        IDictionary<string, JsonElement> arguments,
        string name,
        bool defaultValue,
        out bool value,
        out string? failure)
    {
        if (!arguments.TryGetValue(name, out var element))
        {
            value = defaultValue;
            failure = null;
            return true;
        }
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            value = default;
            return Fail($"audit_boundary_invalid: argument {name} has the wrong JSON kind", out failure);
        }
        value = element.GetBoolean();
        failure = null;
        return true;
    }

    private static bool TryOptionalInt32(
        IDictionary<string, JsonElement> arguments,
        string name,
        int defaultValue,
        out int value,
        out string? failure)
    {
        if (!arguments.TryGetValue(name, out var element))
        {
            value = defaultValue;
            failure = null;
            return true;
        }
        value = default;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
            return Fail($"audit_boundary_invalid: argument {name} must be an int32", out failure);
        failure = null;
        return true;
    }

    private static bool TryClientText(string? value, string field, out string? failure)
    {
        if (value is null)
        {
            failure = null;
            return true;
        }
        if (value.Length == 0 || !TryScalarCount(value, out var scalars) || scalars > MaximumClientScalars)
            return Fail($"audit_boundary_invalid: {field} is not representable", out failure);
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
                return Fail($"audit_boundary_invalid: {field} is not representable", out failure);
        }
        failure = null;
        return true;
    }

    private static bool TryStrictUtf8Length(string value, int maximumBytes)
    {
        try
        {
            return StrictUtf8.GetByteCount(value) <= maximumBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryScalarCount(string value, out int count)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
            count = value.EnumerateRunes().Count();
            return true;
        }
        catch (EncoderFallbackException)
        {
            count = 0;
            return false;
        }
    }

    private static string NormalizeRoute(string? route) => route?.ToLowerInvariant() switch
    {
        "pwsh" => "pwsh",
        "rtk" => "rtk",
        _ => "auto",
    };

    private static bool IsMachineName(string value)
    {
        if (value.Length is < 1 or > 64 || value[0] is < 'a' or > 'z')
            return false;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if ((character < 'a' || character > 'z') &&
                (character < '0' || character > '9') &&
                character is not ('_' or '-' or '.'))
            {
                return false;
            }
        }
        return true;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static bool TryValidateTimeout(TimeSpan value, string name, out string? failure)
    {
        if (value <= TimeSpan.Zero || !TryMilliseconds(value, out _))
            return Fail($"audit_boundary_invalid: {name} is not representable", out failure);
        failure = null;
        return true;
    }

    private static bool TryMilliseconds(TimeSpan value, out long milliseconds)
    {
        if (value.Ticks < 0 || value.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            milliseconds = 0;
            return false;
        }
        milliseconds = value.Ticks / TimeSpan.TicksPerMillisecond;
        return true;
    }

    private static bool TryAdd(DateTimeOffset value, TimeSpan duration, out DateTimeOffset result)
    {
        try
        {
            result = value + duration;
            return IsUtc(result);
        }
        catch (ArgumentOutOfRangeException)
        {
            result = default;
            return false;
        }
    }

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private static bool Fail(string message, out string? failure)
    {
        failure = message;
        return false;
    }
}
