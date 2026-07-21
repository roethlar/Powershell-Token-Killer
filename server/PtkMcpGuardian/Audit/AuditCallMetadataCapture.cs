using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace PtkMcpServer.Audit;

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
        new(["script", "raw", "route", "background", "timeoutSeconds", "session"], StringComparer.Ordinal);
    private static readonly HashSet<string> JobFields =
        new(["action", "id", "offset", "session"], StringComparer.Ordinal);
    private static readonly HashSet<string> OutputFields =
        new(["handle", "action", "offset", "maxBytes", "pattern"], StringComparer.Ordinal);
    private static readonly HashSet<string> StateFields =
        new(["listAvailable", "session"], StringComparer.Ordinal);
    private static readonly HashSet<string> ResetFields =
        new(["session", "expectedGeneration", "force", "timeoutSeconds"], StringComparer.Ordinal);
    private static readonly HashSet<string> SessionListFields =
        new(["action"], StringComparer.Ordinal);
    private static readonly HashSet<string> SessionOpenFields =
        new(["action", "name", "template", "allowColdBackground", "timeoutSeconds"], StringComparer.Ordinal);
    private static readonly HashSet<string> SessionLifecycleFields =
        new(["action", "name", "expectedGeneration", "force", "timeoutSeconds"], StringComparer.Ordinal);

    internal static bool TryCapture(
        CallToolRequestParams call,
        AuditClientContext client,
        TimeSpan defaultTimeout,
        TimeSpan maximumTimeout,
        DateTimeOffset utcNow,
        out AuditCallMetadata? metadata,
        out string? exactSubmittedScript,
        out string? sanitizedFailure,
        AuditOutputRequestProtector? outputProtector = null)
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

            case "ptk_output":
                return TryCaptureOutput(
                    arguments,
                    providedFields,
                    actor,
                    outputProtector,
                    out metadata,
                    out sanitizedFailure);

            case "ptk_state":
                return TryCaptureState(arguments, providedFields, actor, out metadata, out sanitizedFailure);

            case "ptk_reset":
                return TryCaptureReset(
                    arguments,
                    providedFields,
                    defaultTimeout,
                    maximumTimeout,
                    utcNow,
                    actor,
                    out metadata,
                    out sanitizedFailure);

            case "ptk_session":
                return TryCaptureSession(
                    arguments,
                    providedFields,
                    defaultTimeout,
                    maximumTimeout,
                    utcNow,
                    actor,
                    out metadata,
                    out sanitizedFailure);

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
            !TryOptionalSessionName(arguments, "ptk_invoke", "session", out var session, out failure) ||
            !TryEffectiveTimeout(
                arguments,
                defaultTimeout,
                maximumTimeout,
                utcNow,
                out var timeoutMilliseconds,
                out var deadlineUtc,
                out failure))
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

        var request = BaseRequest("ptk_invoke", "invoke", providedFields) with
        {
            SessionRequested = session,
            TimeoutMs = timeoutMilliseconds,
            DeadlineUtc = deadlineUtc,
            Route = route,
            Background = background,
            Raw = raw,
        };
        var profile = new AuditOperationProfile(
            MaximumCallRecordSlots: 11,
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
        if (!TryOptionalSessionName(
                arguments,
                "ptk_job",
                "session",
                out var session,
                out failure))
        {
            return false;
        }
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
            SessionRequested = session,
            JobId = action is "status" or "output" or "kill" ? jobId : null,
            Offset = action == "output" ? offset : null,
        };
        metadata = new AuditCallMetadata(
            actor,
            request,
            new AuditOperationProfile(
                action switch
                {
                    "output" => 6,
                    "kill" => 4,
                    "list" or "status" => 4,
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
            !TryOptionalBoolean(arguments, "listAvailable", false, out var listAvailable, out failure) ||
            !TryOptionalSessionName(
                arguments,
                "ptk_state",
                "session",
                out var session,
                out failure))
        {
            return false;
        }

        metadata = new AuditCallMetadata(
            actor,
            BaseRequest("ptk_state", "state", providedFields) with
            {
                SessionRequested = session,
                ListAvailable = listAvailable,
            },
            new AuditOperationProfile(5, 0, RequiresScriptEvidence: false, MayHaveSideEffects: true));
        return true;
    }

    private static bool TryCaptureReset(
        IDictionary<string, JsonElement> arguments,
        string[] providedFields,
        TimeSpan defaultTimeout,
        TimeSpan maximumTimeout,
        DateTimeOffset utcNow,
        AuditActor actor,
        out AuditCallMetadata? metadata,
        out string? failure)
    {
        metadata = null;
        failure = null;
        if (!TryRejectUnknownFields(arguments, ResetFields, "ptk_reset", out failure) ||
            !TryOptionalSessionName(
                arguments,
                "ptk_reset",
                "session",
                out var session,
                out failure) ||
            !TryOptionalNonNegativeInt64(
                arguments,
                "expectedGeneration",
                defaultValue: 0,
                out var expectedGeneration,
                out failure) ||
            !TryOptionalBoolean(arguments, "force", false, out var force, out failure) ||
            !TryEffectiveTimeout(
                arguments,
                defaultTimeout,
                maximumTimeout,
                utcNow,
                out var timeoutMilliseconds,
                out var deadlineUtc,
                out failure))
        {
            return false;
        }

        metadata = new AuditCallMetadata(
            actor,
            BaseRequest("ptk_reset", "reset", providedFields) with
            {
                SessionRequested = session,
                ExpectedGeneration = expectedGeneration,
                Force = force,
                TimeoutMs = timeoutMilliseconds,
                DeadlineUtc = deadlineUtc,
            },
            new AuditOperationProfile(
                MaximumCallRecordSlots: 4,
                PersistentJobTerminalSlots: 0,
                RequiresScriptEvidence: false,
                MayHaveSideEffects: true));
        return true;
    }

    private static bool TryCaptureSession(
        IDictionary<string, JsonElement> arguments,
        string[] providedFields,
        TimeSpan defaultTimeout,
        TimeSpan maximumTimeout,
        DateTimeOffset utcNow,
        AuditActor actor,
        out AuditCallMetadata? metadata,
        out string? failure)
    {
        metadata = null;
        failure = null;
        if (!TryRequiredString(arguments, "action", out var requestedAction, out failure))
            return false;

        var action = requestedAction.ToLowerInvariant();
        if (action == "list")
        {
            if (!TryRejectUnknownFields(arguments, SessionListFields, "ptk_session", out failure))
                return false;
            metadata = new AuditCallMetadata(
                actor,
                BaseRequest("ptk_session", action, providedFields) with
                {
                    SessionRequested = null,
                },
                new AuditOperationProfile(
                    MaximumCallRecordSlots: 3,
                    PersistentJobTerminalSlots: 0,
                    RequiresScriptEvidence: false,
                    MayHaveSideEffects: false));
            return true;
        }

        if (action == "open")
        {
            if (!TryRejectUnknownFields(arguments, SessionOpenFields, "ptk_session", out failure) ||
                !TryRequiredSessionName(
                    arguments,
                    "ptk_session",
                    "name",
                    out var session,
                    out failure) ||
                !TryOptionalNamedValue(
                    arguments,
                    "ptk_session",
                    "template",
                    out var template,
                    out failure) ||
                !TryOptionalBoolean(
                    arguments,
                    "allowColdBackground",
                    out var allowColdBackground,
                    out failure) ||
                !TryEffectiveTimeout(
                    arguments,
                    defaultTimeout,
                    maximumTimeout,
                    utcNow,
                    out var timeoutMilliseconds,
                    out var deadlineUtc,
                    out failure))
            {
                return false;
            }

            metadata = new AuditCallMetadata(
                actor,
                BaseRequest("ptk_session", action, providedFields) with
                {
                    SessionRequested = session,
                    Template = template,
                    AllowColdBackground = allowColdBackground,
                    TimeoutMs = timeoutMilliseconds,
                    DeadlineUtc = deadlineUtc,
                },
                new AuditOperationProfile(
                    MaximumCallRecordSlots: 4,
                    PersistentJobTerminalSlots: 0,
                    RequiresScriptEvidence: false,
                    MayHaveSideEffects: true));
            return true;
        }

        if (action is not ("close" or "restart"))
            return Fail("audit_boundary_invalid: ptk_session has an unsupported action", out failure);

        if (!TryRejectUnknownFields(arguments, SessionLifecycleFields, "ptk_session", out failure) ||
            !TryRequiredSessionName(
                arguments,
                "ptk_session",
                "name",
                out var lifecycleSession,
                out failure) ||
            !TryOptionalNonNegativeInt64(
                arguments,
                "expectedGeneration",
                defaultValue: 0,
                out var expectedGeneration,
                out failure) ||
            !TryOptionalBoolean(arguments, "force", false, out var force, out failure) ||
            !TryEffectiveTimeout(
                arguments,
                defaultTimeout,
                maximumTimeout,
                utcNow,
                out var lifecycleTimeoutMilliseconds,
                out var lifecycleDeadlineUtc,
                out failure))
        {
            return false;
        }

        metadata = new AuditCallMetadata(
            actor,
            BaseRequest("ptk_session", action, providedFields) with
            {
                SessionRequested = lifecycleSession,
                ExpectedGeneration = expectedGeneration,
                Force = force,
                TimeoutMs = lifecycleTimeoutMilliseconds,
                DeadlineUtc = lifecycleDeadlineUtc,
            },
            new AuditOperationProfile(
                MaximumCallRecordSlots: 4,
                PersistentJobTerminalSlots: 0,
                RequiresScriptEvidence: false,
                MayHaveSideEffects: true));
        return true;
    }

    private static bool TryCaptureOutput(
        IDictionary<string, JsonElement> arguments,
        string[] providedFields,
        AuditActor actor,
        AuditOutputRequestProtector? protector,
        out AuditCallMetadata? metadata,
        out string? failure)
    {
        metadata = null;
        failure = null;
        if (protector is null)
            return Fail("audit_boundary_invalid: output request protection is unavailable", out failure);
        if (!TryRejectUnknownFields(arguments, OutputFields, "ptk_output", out failure) ||
            !TryRequiredString(arguments, "handle", out var handle, out failure) ||
            !TryStrictUtf8Length(handle, 256))
        {
            return failure is not null
                ? false
                : Fail("audit_boundary_invalid: ptk_output.arguments.handle is not representable", out failure);
        }

        var action = "read";
        if (arguments.TryGetValue("action", out var actionElement))
        {
            if (actionElement.ValueKind == JsonValueKind.String)
                action = actionElement.GetString()!.ToLowerInvariant();
            else if (actionElement.ValueKind != JsonValueKind.Null)
                return Fail("audit_boundary_invalid: ptk_output.arguments.action has the wrong JSON kind", out failure);
        }
        if (action is not ("read" or "search" or "status"))
            return Fail("audit_boundary_invalid: ptk_output.arguments.action is unsupported", out failure);

        long offset = 0;
        if (arguments.TryGetValue("offset", out var offsetElement))
        {
            if (offsetElement.ValueKind != JsonValueKind.Number ||
                !offsetElement.TryGetInt64(out offset) ||
                offset < 0)
            {
                return Fail("audit_boundary_invalid: ptk_output.arguments.offset must be a nonnegative int64", out failure);
            }
        }

        var maximumBytes = OutputStore.DefaultReadBytes;
        if (arguments.TryGetValue("maxBytes", out var maximumElement))
        {
            if (maximumElement.ValueKind != JsonValueKind.Number ||
                !maximumElement.TryGetInt32(out maximumBytes) ||
                maximumBytes is < 1 or > OutputStore.MaximumReadBytes)
            {
                return Fail(
                    $"audit_boundary_invalid: ptk_output.arguments.maxBytes must be 1..{OutputStore.MaximumReadBytes}",
                    out failure);
            }
        }

        string? pattern = null;
        if (arguments.TryGetValue("pattern", out var patternElement))
        {
            if (patternElement.ValueKind == JsonValueKind.String)
                pattern = patternElement.GetString();
            else if (patternElement.ValueKind != JsonValueKind.Null)
                return Fail("audit_boundary_invalid: ptk_output.arguments.pattern has the wrong JSON kind", out failure);
        }

        if (action == "status")
        {
            if (arguments.ContainsKey("offset") ||
                arguments.ContainsKey("maxBytes") ||
                arguments.ContainsKey("pattern"))
            {
                return Fail("audit_boundary_invalid: ptk_output status contains an inapplicable argument", out failure);
            }
        }
        else if (action == "read")
        {
            if (arguments.ContainsKey("pattern"))
                return Fail("audit_boundary_invalid: ptk_output read contains an inapplicable argument", out failure);
        }
        else
        {
            if (pattern is null || pattern.Length == 0 ||
                !TryStrictUtf8Length(pattern, OutputStore.MaximumPatternBytes))
            {
                return Fail("audit_boundary_invalid: ptk_output search requires a bounded pattern", out failure);
            }
            if (StrictUtf8.GetByteCount(pattern) > maximumBytes)
            {
                return Fail(
                    "audit_boundary_invalid: ptk_output search maxBytes cannot contain its pattern",
                    out failure);
            }
        }

        string handleDigest;
        string? patternFingerprint;
        try
        {
            handleDigest = protector.HandleDigest(handle);
            patternFingerprint = pattern is null
                ? null
                : protector.PatternFingerprint(pattern);
        }
        catch (Exception exception) when (exception is EncoderFallbackException or ObjectDisposedException)
        {
            return Fail("audit_boundary_invalid: ptk_output sensitive fields are not representable", out failure);
        }

        var request = BaseRequest("ptk_output", action, providedFields) with
        {
            Offset = action == "status" ? null : offset,
            MaxBytes = action == "status" ? null : maximumBytes,
            PatternFingerprint = patternFingerprint,
            OutputHandleDigest = handleDigest,
        };
        metadata = new AuditCallMetadata(
            actor,
            request,
            new AuditOperationProfile(
                MaximumCallRecordSlots: 3,
                PersistentJobTerminalSlots: 0,
                RequiresScriptEvidence: false,
                MayHaveSideEffects: false));
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

    private static bool TryOptionalBoolean(
        IDictionary<string, JsonElement> arguments,
        string name,
        out bool? value,
        out string? failure)
    {
        if (!arguments.TryGetValue(name, out var element))
        {
            value = null;
            failure = null;
            return true;
        }
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            value = null;
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

    private static bool TryOptionalNonNegativeInt64(
        IDictionary<string, JsonElement> arguments,
        string name,
        long defaultValue,
        out long value,
        out string? failure)
    {
        if (!arguments.TryGetValue(name, out var element))
        {
            value = defaultValue;
            failure = null;
            return true;
        }
        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt64(out value) ||
            value < 0)
        {
            value = default;
            return Fail(
                $"audit_boundary_invalid: argument {name} must be a nonnegative int64",
                out failure);
        }
        failure = null;
        return true;
    }

    private static bool TryOptionalSessionName(
        IDictionary<string, JsonElement> arguments,
        string tool,
        string name,
        out string value,
        out string? failure)
    {
        if (!arguments.ContainsKey(name))
        {
            value = "default";
            failure = null;
            return true;
        }
        return TryRequiredSessionName(arguments, tool, name, out value, out failure);
    }

    private static bool TryRequiredSessionName(
        IDictionary<string, JsonElement> arguments,
        string tool,
        string name,
        out string value,
        out string? failure)
    {
        if (!TryRequiredString(arguments, name, out value, out failure))
            return false;
        if (!IsMachineName(value))
        {
            value = string.Empty;
            return Fail(
                $"audit_boundary_invalid: {tool}.arguments.{name} is not representable",
                out failure);
        }
        return true;
    }

    private static bool TryOptionalNamedValue(
        IDictionary<string, JsonElement> arguments,
        string tool,
        string name,
        out string? value,
        out string? failure)
    {
        if (!arguments.ContainsKey(name))
        {
            value = null;
            failure = null;
            return true;
        }
        if (!TryRequiredSessionName(arguments, tool, name, out var required, out failure))
        {
            value = null;
            return false;
        }
        value = required;
        return true;
    }

    private static bool TryEffectiveTimeout(
        IDictionary<string, JsonElement> arguments,
        TimeSpan defaultTimeout,
        TimeSpan maximumTimeout,
        DateTimeOffset utcNow,
        out long timeoutMilliseconds,
        out DateTimeOffset deadlineUtc,
        out string? failure)
    {
        timeoutMilliseconds = 0;
        deadlineUtc = default;
        if (!TryOptionalInt32(
                arguments,
                "timeoutSeconds",
                defaultValue: 0,
                out var timeoutSeconds,
                out failure))
        {
            return false;
        }

        var budget = timeoutSeconds > 0
            ? Min(TimeSpan.FromSeconds(timeoutSeconds), maximumTimeout)
            : defaultTimeout;
        if (!TryMilliseconds(budget, out timeoutMilliseconds) ||
            !TryAdd(utcNow, budget, out deadlineUtc))
        {
            return Fail("audit_boundary_invalid: timeout is not representable", out failure);
        }

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
