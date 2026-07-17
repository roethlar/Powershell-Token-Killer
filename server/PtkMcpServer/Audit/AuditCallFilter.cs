using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PtkMcpServer.Audit;

/// <summary>
/// The outermost tools/call boundary. It captures the request before the MCP
/// tool handler resolves any of its dependencies and installs the admitted
/// audit scope for the duration of dispatch.
/// </summary>
internal static class AuditCallFilter
{
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(
        TimeSpan defaultTimeout,
        TimeSpan maximumTimeout,
        AuditOutputRequestProtector outputProtector,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(outputProtector);
        var clock = utcNow ?? (() => DateTimeOffset.UtcNow);
        return next => (request, cancellationToken) => InvokeAsync(
            request.Params,
            CaptureClient(request.Server),
            request.Services,
            token => next(request, token),
            defaultTimeout,
            maximumTimeout,
            clock,
            cancellationToken,
            outputProtector);
    }

    /// <summary>
    /// Kept independent of the concrete MCP server so the fail-closed boundary
    /// can be tested without constructing a transport. The adapter above is
    /// deliberately limited to attribution capture and forwarding.
    /// </summary>
    internal static async ValueTask<CallToolResult> InvokeAsync(
        CallToolRequestParams call,
        AuditClientContext client,
        IServiceProvider? requestServices,
        Func<CancellationToken, ValueTask<CallToolResult>> next,
        TimeSpan defaultTimeout,
        TimeSpan maximumTimeout,
        Func<DateTimeOffset> utcNow,
        CancellationToken cancellationToken,
        AuditOutputRequestProtector? outputProtector = null)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(utcNow);

        if (requestServices is null)
            return Refusal(null);

        AuditCallMetadata? metadata;
        string? submittedScript;
        string? captureFailure;
        try
        {
            if (!AuditCallMetadataCapture.TryCapture(
                    call,
                    client,
                    defaultTimeout,
                    maximumTimeout,
                    utcNow(),
                    out metadata,
                    out submittedScript,
                    out captureFailure,
                    outputProtector))
            {
                return Refusal(captureFailure);
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return Refusal("audit_boundary_invalid");
        }

        IAuditBoundaryCall? audit = null;
        IDisposable? callLease = null;
        AuditHealth health;
        IAuditAdmissionOwner admissionOwner;
        AuditCallContextAccessor accessor;
        try
        {
            // Resolve only the guardian-safe audit owner. Tool/runtime
            // dependencies and the concrete supervisor gate remain unresolved
            // until audit admission succeeds.
            admissionOwner = requestServices.GetRequiredService<IAuditAdmissionOwner>();
            admissionOwner.Touch();
            health = admissionOwner.Health;
            accessor = requestServices.GetRequiredService<AuditCallContextAccessor>();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return Refusal(null);
        }

        string? admissionFailure;
        try
        {
            if (!admissionOwner.TryBeginCall(
                    metadata!,
                    submittedScript,
                    out audit,
                    out callLease,
                    out admissionFailure))
            {
                if (audit is not null || callLease is not null)
                {
                    CompleteInvalidBoundary(audit);
                    DisposeInvalidLease(callLease);
                    return Refusal("audit_boundary_invalid");
                }
                if (string.Equals(admissionFailure, "evidence.limit", StringComparison.Ordinal))
                    return Refusal("audit_boundary_invalid");
                if (string.Equals(admissionFailure, "journal.closed", StringComparison.Ordinal))
                    return Refusal(null);
                return call.Name == "ptk_state"
                    ? EmergencyState(health)
                    : Refusal(null);
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            CompleteInvalidBoundary(audit);
            DisposeInvalidLease(callLease);
            MarkUnavailable(health, "audit.boundary");
            return call.Name == "ptk_state"
                ? EmergencyState(health)
                : Refusal(null);
        }

        var admittedBoundary = audit;
        if (admittedBoundary is not AuditCallContext admittedAudit || callLease is null)
        {
            CompleteInvalidBoundary(admittedBoundary);
            DisposeInvalidLease(callLease);
            return Refusal("audit_boundary_invalid");
        }

        using var activeCall = callLease;
        if (accessor.Current is not null)
        {
            admittedBoundary.CompleteFromFilter("failed", 0);
            return Refusal("audit_boundary_invalid");
        }
        accessor.Current = admittedAudit;
        try
        {
            var result = await next(cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                admittedBoundary.CompleteFromFilter("failed", 0);
                throw new InvalidOperationException("The MCP tool returned no result.");
            }

            var authorizationRefused =
                admittedBoundary.AuthorizationPersistenceFailed &&
                !admittedBoundary.UserExecutionStarted;
            if (!admittedBoundary.TerminalWritten)
            {
                var terminal = ResolveFallbackTerminal(result, authorizationRefused);
                admittedBoundary.CompleteFromFilter(terminal.State, terminal.BytesReturned);
            }

            if (authorizationRefused)
                return Refusal(null);

            return result;
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
        finally
        {
            accessor.Current = null;
        }
    }

    internal static (string State, long BytesReturned) ResolveFallbackTerminal(
        CallToolResult result,
        bool authorizationRefused)
    {
        ArgumentNullException.ThrowIfNull(result);
        return (
            authorizationRefused || result.IsError == true ? "failed" : "completed",
            authorizationRefused ? 0 : ReturnedTextBytes(result));
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
            // The request still fails closed. The diagnostic below is allowed
            // to collapse to the generic refusal when health cannot be read.
        }
    }

    private static CallToolResult EmergencyState(AuditHealth health)
    {
        try
        {
            _ = health.TryRecordEmergencyStateProbe(out var snapshot);
            var protectionMode = snapshot.ProtectionMode == AuditProtectionMode.LocalOnly
                ? "local-only"
                : "anchored";
            var degradedSince = snapshot.DegradedSinceUtc?.ToString("O", CultureInfo.InvariantCulture)
                ?? "unknown";
            var exportIdentity = snapshot.ExportConfigurationIdentity ?? "none";
            var state = snapshot.State.ToString().ToLowerInvariant();
            var lines = new List<string>
            {
                $"audit={state}",
                "unrecorded=true",
                $"failure_class={snapshot.FailureClass ?? "unknown"}",
                $"degraded_since_utc={degradedSince}",
                $"protection_mode={protectionMode}",
                $"export_configuration_identity={exportIdentity}",
                $"spool_bytes={snapshot.SpoolBytes.ToString(CultureInfo.InvariantCulture)}",
                $"spool_capacity_bytes={snapshot.SpoolCapacityBytes.ToString(CultureInfo.InvariantCulture)}",
                $"reserved_bytes={snapshot.ReservedBytes.ToString(CultureInfo.InvariantCulture)}",
                $"emergency_reserve_bytes={snapshot.EmergencyReserveBytes.ToString(CultureInfo.InvariantCulture)}",
                $"emergency_reserve_capacity_bytes={snapshot.EmergencyReserveCapacityBytes.ToString(CultureInfo.InvariantCulture)}",
            };
            lines.AddRange(AuditExporterHealthText.FormatEmergency(snapshot.Exporter));
            var text = string.Join('\n', lines);
            return TextResult(text, isError: false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return Refusal(null);
        }
    }

    private static long ReturnedTextBytes(CallToolResult result)
    {
        long bytes = 0;
        foreach (var block in result.Content ?? [])
        {
            if (block is TextContentBlock text)
                bytes = checked(bytes + Utf8.GetByteCount(text.Text ?? string.Empty));
        }
        return bytes;
    }

    private static CallToolResult Refusal(string? sanitizedFailure)
    {
        var prefix = string.IsNullOrWhiteSpace(sanitizedFailure)
            ? string.Empty
            : sanitizedFailure + ": ";
        return TextResult(prefix + AuditCallContext.NotStartedMessage, isError: true);
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
            // Admission returned an unusable boundary. Keep the stable refusal
            // and still release any lease rather than trusting another member.
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
            // The call was never dispatched. Its invalid ownership boundary
            // cannot replace the stable fail-closed response.
        }
    }

    private static CallToolResult TextResult(string text, bool isError) => new()
    {
        IsError = isError,
        Content = [new TextContentBlock { Text = text }],
    };

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
}
