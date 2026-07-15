using System.Text;
using System.Text.Json;

namespace PtkMcpServer.Worker;

internal enum WorkerInvokeRoute
{
    Auto,
    Pwsh,
    Rtk,
}

internal abstract record WorkerSessionOperationArguments;

internal sealed record WorkerInvokeArguments(
    string Script,
    bool Raw,
    WorkerInvokeRoute Route) : WorkerSessionOperationArguments;

internal sealed record WorkerJobListArguments : WorkerSessionOperationArguments;

internal sealed record WorkerJobStatusArguments(long JobId) : WorkerSessionOperationArguments;

internal sealed record WorkerJobOutputArguments(
    long JobId,
    long Offset) : WorkerSessionOperationArguments;

internal sealed record WorkerJobKillArguments(long JobId) : WorkerSessionOperationArguments;

internal sealed record WorkerStateArguments(bool ListAvailable) : WorkerSessionOperationArguments;

internal abstract record WorkerSessionOperationResult;

internal sealed record WorkerInvokeResult(string Text) : WorkerSessionOperationResult;

internal sealed record WorkerJobListResult(string Text) : WorkerSessionOperationResult;

internal sealed record WorkerJobStatusResult(string Text) : WorkerSessionOperationResult;

internal sealed record WorkerJobOutputResult(string Text) : WorkerSessionOperationResult;

internal sealed record WorkerJobKillResult(string Text) : WorkerSessionOperationResult;

internal sealed record WorkerStateResult(string Text) : WorkerSessionOperationResult;

/// <summary>
/// Strict transport-neutral value codecs for the deliberately unwired Slice 7g
/// worker operations. These values are not assigned to an envelope kind and
/// cannot execute a session operation.
/// </summary>
internal static class WorkerSessionOperationCodec
{
    internal const string InvokeOperation = "invoke";
    internal const string JobListOperation = "job_list";
    internal const string JobStatusOperation = "job_status";
    internal const string JobOutputOperation = "job_output";
    internal const string JobKillOperation = "job_kill";
    internal const string StateOperation = "state";
    internal const int MaximumLogicalTextBytes = 131_072;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static WorkerSessionOperationArguments ParseArguments(
        string operation,
        JsonElement arguments) => operation switch
        {
            InvokeOperation => ParseInvokeArguments(arguments),
            JobListOperation => ParseJobListArguments(arguments),
            JobStatusOperation => ParseJobStatusArguments(arguments),
            JobOutputOperation => ParseJobOutputArguments(arguments),
            JobKillOperation => ParseJobKillArguments(arguments),
            StateOperation => ParseStateArguments(arguments),
            _ => throw UnsupportedOperation(),
        };

    internal static JsonElement CreateArguments(
        string operation,
        WorkerSessionOperationArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        EnsureSupportedOperation(operation);

        return (operation, arguments) switch
        {
            (InvokeOperation, WorkerInvokeArguments value) => CreateInvokeArguments(value),
            (JobListOperation, WorkerJobListArguments) =>
                JsonSerializer.SerializeToElement(new { }),
            (JobStatusOperation, WorkerJobStatusArguments value) =>
                JsonSerializer.SerializeToElement(new { jobId = PositiveJobId(value.JobId) }),
            (JobOutputOperation, WorkerJobOutputArguments value) =>
                JsonSerializer.SerializeToElement(new
                {
                    jobId = PositiveJobId(value.JobId),
                    offset = NonnegativeOffset(value.Offset),
                }),
            (JobKillOperation, WorkerJobKillArguments value) =>
                JsonSerializer.SerializeToElement(new { jobId = PositiveJobId(value.JobId) }),
            (StateOperation, WorkerStateArguments value) =>
                JsonSerializer.SerializeToElement(new { listAvailable = value.ListAvailable }),
            _ => throw ArgumentMismatch(),
        };
    }

    internal static WorkerSessionOperationResult ParseResult(
        string operation,
        JsonElement result)
    {
        EnsureSupportedOperation(operation);
        var text = ParseTextResult(result);
        return operation switch
        {
            InvokeOperation => new WorkerInvokeResult(text),
            JobListOperation => new WorkerJobListResult(text),
            JobStatusOperation => new WorkerJobStatusResult(text),
            JobOutputOperation => new WorkerJobOutputResult(text),
            JobKillOperation => new WorkerJobKillResult(text),
            StateOperation => new WorkerStateResult(text),
            _ => throw UnsupportedOperation(),
        };
    }

    internal static JsonElement CreateResult(
        string operation,
        WorkerSessionOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        EnsureSupportedOperation(operation);
        var text = (operation, result) switch
        {
            (InvokeOperation, WorkerInvokeResult value) => value.Text,
            (JobListOperation, WorkerJobListResult value) => value.Text,
            (JobStatusOperation, WorkerJobStatusResult value) => value.Text,
            (JobOutputOperation, WorkerJobOutputResult value) => value.Text,
            (JobKillOperation, WorkerJobKillResult value) => value.Text,
            (StateOperation, WorkerStateResult value) => value.Text,
            _ => throw ResultMismatch(),
        };
        text = LogicalText(text, "text", "operation_result_too_large");
        return JsonSerializer.SerializeToElement(new { text });
    }

    private static WorkerInvokeArguments ParseInvokeArguments(JsonElement arguments)
    {
        var fields = ClosedObject(arguments, "script", "raw", "route");
        var script = LogicalText(
            StringField(Required(fields, "script"), "script"),
            "script",
            "operation_script_too_large");
        var raw = BooleanField(Required(fields, "raw"), "raw");
        var route = RouteField(Required(fields, "route"));
        return new WorkerInvokeArguments(script, raw, route);
    }

    private static JsonElement CreateInvokeArguments(WorkerInvokeArguments arguments)
    {
        var script = LogicalText(
            arguments.Script,
            "script",
            "operation_script_too_large");
        var route = RouteName(arguments.Route);
        return JsonSerializer.SerializeToElement(new
        {
            script,
            raw = arguments.Raw,
            route,
        });
    }

    private static WorkerJobListArguments ParseJobListArguments(JsonElement arguments)
    {
        _ = ClosedObject(arguments);
        return new WorkerJobListArguments();
    }

    private static WorkerJobStatusArguments ParseJobStatusArguments(JsonElement arguments)
    {
        var fields = ClosedObject(arguments, "jobId");
        return new WorkerJobStatusArguments(
            PositiveJobId(Required(fields, "jobId")));
    }

    private static WorkerJobOutputArguments ParseJobOutputArguments(JsonElement arguments)
    {
        var fields = ClosedObject(arguments, "jobId", "offset");
        return new WorkerJobOutputArguments(
            PositiveJobId(Required(fields, "jobId")),
            NonnegativeOffset(Required(fields, "offset")));
    }

    private static WorkerJobKillArguments ParseJobKillArguments(JsonElement arguments)
    {
        var fields = ClosedObject(arguments, "jobId");
        return new WorkerJobKillArguments(
            PositiveJobId(Required(fields, "jobId")));
    }

    private static WorkerStateArguments ParseStateArguments(JsonElement arguments)
    {
        var fields = ClosedObject(arguments, "listAvailable");
        return new WorkerStateArguments(
            BooleanField(Required(fields, "listAvailable"), "listAvailable"));
    }

    private static string ParseTextResult(JsonElement result)
    {
        var fields = ClosedObject(result, "text");
        return LogicalText(
            StringField(Required(fields, "text"), "text"),
            "text",
            "operation_result_too_large");
    }

    private static Dictionary<string, JsonElement> ClosedObject(
        JsonElement value,
        params string[] allowedFields)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw InvalidField("object");

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, property.Value))
            {
                throw new WorkerProtocolException(
                    "duplicate_field",
                    "Worker session operation object contains a duplicate field.");
            }
            if (!allowedFields.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new WorkerProtocolException(
                    "unknown_operation_field",
                    "Worker session operation object contains an unknown field.");
            }
        }
        return fields;
    }

    private static JsonElement Required(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value))
        {
            throw new WorkerProtocolException(
                "missing_operation_field",
                "Worker session operation object is missing a required field.");
        }
        return value;
    }

    private static string StringField(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidField(name);
        try
        {
            return value.GetString() ?? throw InvalidField(name);
        }
        catch (InvalidOperationException)
        {
            throw new WorkerProtocolException(
                "invalid_operation_field",
                "Worker session operation string is not valid logical text.");
        }
    }

    private static bool BooleanField(JsonElement value, string name)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InvalidField(name);
        return value.GetBoolean();
    }

    private static long PositiveJobId(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var parsed) || parsed <= 0)
        {
            throw InvalidField("jobId");
        }
        return parsed;
    }

    private static long PositiveJobId(long value)
    {
        if (value <= 0) throw InvalidField("jobId");
        return value;
    }

    private static long NonnegativeOffset(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var parsed) || parsed < 0)
        {
            throw InvalidField("offset");
        }
        return parsed;
    }

    private static long NonnegativeOffset(long value)
    {
        if (value < 0) throw InvalidField("offset");
        return value;
    }

    private static WorkerInvokeRoute RouteField(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidField("route");
        return value.GetString() switch
        {
            "auto" => WorkerInvokeRoute.Auto,
            "pwsh" => WorkerInvokeRoute.Pwsh,
            "rtk" => WorkerInvokeRoute.Rtk,
            _ => throw InvalidField("route"),
        };
    }

    private static string RouteName(WorkerInvokeRoute route) => route switch
    {
        WorkerInvokeRoute.Auto => "auto",
        WorkerInvokeRoute.Pwsh => "pwsh",
        WorkerInvokeRoute.Rtk => "rtk",
        _ => throw InvalidField("route"),
    };

    private static string LogicalText(
        string? value,
        string field,
        string tooLargeDetailCode)
    {
        if (value is null) throw InvalidField(field);

        int bytes;
        try
        {
            bytes = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new WorkerProtocolException(
                "invalid_operation_field",
                "Worker session operation text is not valid logical UTF-8.");
        }
        if (bytes > MaximumLogicalTextBytes)
        {
            throw new WorkerProtocolException(
                tooLargeDetailCode,
                "Worker session operation text exceeds its logical UTF-8 byte limit.");
        }
        return value;
    }

    private static void EnsureSupportedOperation(string operation)
    {
        if (operation is not (
            InvokeOperation or
            JobListOperation or
            JobStatusOperation or
            JobOutputOperation or
            JobKillOperation or
            StateOperation))
        {
            throw UnsupportedOperation();
        }
    }

    private static WorkerProtocolException UnsupportedOperation() =>
        new(
            "unsupported_operation",
            "Worker session operation is not supported.");

    private static WorkerProtocolException ArgumentMismatch() =>
        new(
            "operation_argument_mismatch",
            "Worker session operation arguments do not match the operation.");

    private static WorkerProtocolException ResultMismatch() =>
        new(
            "operation_result_mismatch",
            "Worker session operation result does not match the operation.");

    private static WorkerProtocolException InvalidField(string field) =>
        new(
            "invalid_operation_field",
            $"Worker session operation field '{field}' is invalid.");
}
