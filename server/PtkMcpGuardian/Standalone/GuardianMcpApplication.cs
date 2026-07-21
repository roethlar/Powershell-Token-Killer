using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PtkMcpServer.Audit;
using PtkSharedContracts;

namespace PtkMcpGuardian.Standalone;

internal interface IGuardianToolDispatcher
{
    ValueTask<GuardianToolResult> DispatchAsync(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> arguments,
        GuardianAuditCall auditCall,
        CancellationToken cancellationToken);
}

internal sealed record GuardianToolResult
{
    internal GuardianToolResult(string text, bool isError)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        IsError = isError;
    }

    internal string Text { get; }
    internal bool IsError { get; }
}

/// <summary>
/// Owns one immutable public MCP contract and one public stream transport.
/// Replaceable private-host state is deliberately hidden behind the dispatcher.
/// </summary>
internal sealed class GuardianMcpApplication
{
    internal const string UnknownToolText =
        "The requested tool is not in the frozen PTK contract.";

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyArguments =
        new ReadOnlyDictionary<string, JsonElement>(
            new Dictionary<string, JsonElement>(StringComparer.Ordinal));
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly IGuardianToolDispatcher _dispatcher;
    private readonly IAuditAdmissionOwner _auditOwner;
    private readonly TimeSpan _defaultTimeout;
    private readonly TimeSpan _maximumTimeout;
    private readonly AuditOutputRequestProtector _outputProtector;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly PublicToolContractSnapshot _contract;
    private readonly IReadOnlySet<string> _toolNames;
    private int _runStarted;

    internal GuardianMcpApplication(
        IGuardianToolDispatcher dispatcher,
        IAuditAdmissionOwner auditOwner,
        TimeSpan defaultTimeout,
        TimeSpan maximumTimeout,
        AuditOutputRequestProtector outputProtector,
        Func<DateTimeOffset>? utcNow = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _auditOwner = auditOwner ?? throw new ArgumentNullException(nameof(auditOwner));
        _defaultTimeout = defaultTimeout;
        _maximumTimeout = maximumTimeout;
        _outputProtector = outputProtector ??
            throw new ArgumentNullException(nameof(outputProtector));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _contract = PublicToolContractResource.Parse();
        _toolNames = _contract.Tools
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    internal async Task RunAsync(
        Stream publicInput,
        Stream publicOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publicInput);
        ArgumentNullException.ThrowIfNull(publicOutput);
        if (!publicInput.CanRead)
            throw new ArgumentException("The public input stream must be readable.", nameof(publicInput));
        if (!publicOutput.CanWrite)
            throw new ArgumentException("The public output stream must be writable.", nameof(publicOutput));
        if (Interlocked.CompareExchange(ref _runStarted, 1, 0) != 0)
            throw new InvalidOperationException("A guardian MCP application can run only once.");

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        // stdout is the public JSON-RPC transport. Remove every inherited
        // provider before installing the one stderr-only logging path.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
            options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMcpServer(options =>
            {
                options.ScopeRequests = true;
                options.ServerInfo = new Implementation
                {
                    Name = _contract.ServerIdentity.Name,
                    Version = _contract.ServerIdentity.Version,
                };
                options.ServerInstructions = _contract.Instructions;
            })
            .WithStreamServerTransport(publicInput, publicOutput)
            .WithListToolsHandler(HandleListToolsAsync)
            .WithCallToolHandler(HandleCallToolAsync);

        using var host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<ListToolsResult> HandleListToolsAsync(
        RequestContext<ListToolsRequestParams> context,
        CancellationToken cancellationToken)
    {
        _ = context;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ListToolsResult
        {
            Tools = _contract.Tools.Select(CreateTool).ToArray(),
        });
    }

    private async ValueTask<CallToolResult> HandleCallToolAsync(
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Params ??
            throw new InvalidDataException("The MCP tool request has no parameters.");
        if (string.IsNullOrEmpty(request.Name) || !_toolNames.Contains(request.Name))
            return TextResult(UnknownToolText, isError: true);

        var result = await InvokeAuditedAsync(
                request,
                CaptureClient(context.Server),
                cancellationToken)
            .ConfigureAwait(false);
        return TextResult(result.Text, result.IsError);
    }

    private async ValueTask<GuardianToolResult> InvokeAuditedAsync(
        CallToolRequestParams request,
        AuditClientContext client,
        CancellationToken cancellationToken)
    {
        AuditCallMetadata? metadata;
        string? submittedScript;
        string? captureFailure;
        try
        {
            if (!AuditCallMetadataCapture.TryCapture(
                    request,
                    client,
                    _defaultTimeout,
                    _maximumTimeout,
                    _utcNow(),
                    out metadata,
                    out submittedScript,
                    out captureFailure,
                    _outputProtector))
            {
                return Refusal(captureFailure);
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return Refusal("audit_boundary_invalid");
        }

        IAuditBoundaryCall? admittedBoundary = null;
        IDisposable? callLease = null;
        AuditHealth? health = null;
        try
        {
            _auditOwner.Touch();
            health = _auditOwner.Health;
            if (!_auditOwner.TryBeginCall(
                    metadata!,
                    submittedScript,
                    out admittedBoundary,
                    out callLease,
                    out var admissionFailure))
            {
                if (admittedBoundary is not null || callLease is not null)
                {
                    CompleteInvalidBoundary(admittedBoundary);
                    DisposeInvalidLease(callLease);
                    return Refusal("audit_boundary_invalid");
                }
                if (string.Equals(admissionFailure, "evidence.limit", StringComparison.Ordinal))
                    return Refusal("audit_boundary_invalid");
                if (string.Equals(admissionFailure, "journal.closed", StringComparison.Ordinal))
                    return Refusal(null);
                return request.Name == "ptk_state"
                    ? EmergencyState(health)
                    : Refusal(null);
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            CompleteInvalidBoundary(admittedBoundary);
            DisposeInvalidLease(callLease);
            if (health is not null)
                MarkUnavailable(health, "audit.boundary");
            return request.Name == "ptk_state" && health is not null
                ? EmergencyState(health)
                : Refusal(null);
        }

        if (admittedBoundary is not GuardianAuditCall auditCall || callLease is null)
        {
            CompleteInvalidBoundary(admittedBoundary);
            DisposeInvalidLease(callLease);
            return Refusal("audit_boundary_invalid");
        }

        using var activeCall = callLease;
        var arguments = request.Arguments is null
            ? EmptyArguments
            : new ReadOnlyDictionary<string, JsonElement>(request.Arguments);
        try
        {
            var result = await _dispatcher.DispatchAsync(
                    request.Name,
                    arguments,
                    auditCall,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is null)
            {
                admittedBoundary.CompleteFromFilter("failed", 0);
                throw new InvalidOperationException("The guardian tool dispatcher returned null.");
            }

            var authorizationRefused =
                admittedBoundary.AuthorizationPersistenceFailed &&
                !admittedBoundary.UserExecutionStarted;
            if (!admittedBoundary.TerminalWritten)
            {
                admittedBoundary.CompleteFromFilter(
                    authorizationRefused || result.IsError ? "failed" : "completed",
                    authorizationRefused ? 0 : Utf8.GetByteCount(result.Text));
            }

            return authorizationRefused ? Refusal(null) : result;
        }
        catch (OperationCanceledException)
        {
            if (!admittedBoundary.TerminalWritten)
                admittedBoundary.CompleteFromFilter("canceled", 0);
            throw;
        }
        catch
        {
            if (!admittedBoundary.TerminalWritten)
                admittedBoundary.CompleteFromFilter("failed", 0);
            throw;
        }
    }

    private static AuditClientContext CaptureClient(McpServer server)
    {
        var client = server.ClientInfo;
        return new AuditClientContext(client?.Name, client?.Version, server.SessionId);
    }

    private static void MarkUnavailable(AuditHealth health, string? failureClass)
    {
        try
        {
            if (health.Snapshot().State != AuditHealthState.Unavailable)
                health.MarkUnavailable(NormalizeFailureClass(failureClass));
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Admission still fails closed. The emergency diagnostic below may
            // collapse to the generic refusal when health cannot be maintained.
        }
    }

    private static GuardianToolResult EmergencyState(AuditHealth health)
    {
        try
        {
            _ = health.TryRecordEmergencyStateProbe(out var snapshot);
            var protectionMode = snapshot.ProtectionMode == AuditProtectionMode.LocalOnly
                ? "local-only"
                : "anchored";
            var degradedSince = snapshot.DegradedSinceUtc?.ToString("O", CultureInfo.InvariantCulture)
                ?? "unknown";
            var lines = new List<string>
            {
                $"audit={snapshot.State.ToString().ToLowerInvariant()}",
                "unrecorded=true",
                $"failure_class={snapshot.FailureClass ?? "unknown"}",
                $"degraded_since_utc={degradedSince}",
                $"protection_mode={protectionMode}",
                $"export_configuration_identity={snapshot.ExportConfigurationIdentity ?? "none"}",
                $"spool_bytes={snapshot.SpoolBytes.ToString(CultureInfo.InvariantCulture)}",
                $"spool_capacity_bytes={snapshot.SpoolCapacityBytes.ToString(CultureInfo.InvariantCulture)}",
                $"reserved_bytes={snapshot.ReservedBytes.ToString(CultureInfo.InvariantCulture)}",
                $"emergency_reserve_bytes={snapshot.EmergencyReserveBytes.ToString(CultureInfo.InvariantCulture)}",
                $"emergency_reserve_capacity_bytes={snapshot.EmergencyReserveCapacityBytes.ToString(CultureInfo.InvariantCulture)}",
            };
            lines.AddRange(AuditExporterHealthText.FormatEmergency(snapshot.Exporter));
            return new GuardianToolResult(string.Join('\n', lines), isError: false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return Refusal(null);
        }
    }

    private static GuardianToolResult Refusal(string? sanitizedFailure)
    {
        var prefix = string.IsNullOrWhiteSpace(sanitizedFailure)
            ? string.Empty
            : sanitizedFailure + ": ";
        return new GuardianToolResult(
            prefix + AuditCallLifecycle.NotStartedMessage,
            isError: true);
    }

    private static void CompleteInvalidBoundary(IAuditBoundaryCall? call)
    {
        if (call is null) return;
        try
        {
            call.CompleteFromFilter("failed", 0);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // The unusable boundary cannot replace the stable refusal.
        }
    }

    private static void DisposeInvalidLease(IDisposable? callLease)
    {
        if (callLease is null) return;
        try
        {
            callLease.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // The call was never dispatched; retain the stable refusal.
        }
    }

    private static string NormalizeFailureClass(string? failureClass)
    {
        if (string.IsNullOrWhiteSpace(failureClass)) return "audit.unavailable";
        if (failureClass.Length > 64 || failureClass[0] is < 'a' or > 'z')
            return "audit.unavailable";
        foreach (var character in failureClass.AsSpan(1))
        {
            if ((character < 'a' || character > 'z') &&
                (character < '0' || character > '9') &&
                character is not ('.' or '_' or '-'))
            {
                return "audit.unavailable";
            }
        }
        return failureClass;
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static Tool CreateTool(PublicToolDefinition definition) => new()
    {
        Name = definition.Name,
        Description = definition.Description,
        InputSchema = definition.InputSchema.Clone(),
    };

    private static CallToolResult TextResult(string text, bool isError) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError,
    };
}
