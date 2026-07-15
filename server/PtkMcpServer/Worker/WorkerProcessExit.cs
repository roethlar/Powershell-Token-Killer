using System.Text;

namespace PtkMcpServer.Worker;

/// <summary>
/// Maps managed-worker terminal results to stable process exits and emits the
/// single bounded infrastructure diagnostic permitted on abnormal exit.
/// </summary>
internal static class WorkerProcessExit
{
    internal const int MaximumDiagnosticBytes = 256;

    private const int InvalidInvocationExitCode = 64;
    private const int BootstrapFailureExitCode = 80;
    private const int InitializeFailureExitCode = 81;
    private const int ProtocolFailureExitCode = 82;
    private const int TransportFailureExitCode = 83;
    private const int RuntimeFailureExitCode = 84;

    private const string RuntimeFailureKind = "runtime_failure";
    private const string RuntimeFailureDetail = "runtime_failure";
    private const string RuntimeFailureDiagnostic =
        "ptk_worker_exit kind=runtime_failure detail=runtime_failure\n";

    internal static int WriteInvocationFailure(Stream standardError) =>
        WriteFailure(
            InvalidInvocationExitCode,
            "invocation_error",
            "invalid_arguments",
            standardError);

    internal static int WriteBootstrapFailure(
        string? detailCode,
        Stream standardError) =>
        WriteFailure(
            BootstrapFailureExitCode,
            "bootstrap_failure",
            NormalizeBootstrapDetail(detailCode),
            standardError);

    internal static int MapCode(WorkerServerExit serverExit) =>
        serverExit.Kind switch
        {
            WorkerServerExitKind.Shutdown or
            WorkerServerExitKind.Eof or
            WorkerServerExitKind.Canceled => 0,
            WorkerServerExitKind.InitializeFailed => InitializeFailureExitCode,
            WorkerServerExitKind.ProtocolError => ProtocolFailureExitCode,
            WorkerServerExitKind.TransportFailure => TransportFailureExitCode,
            WorkerServerExitKind.RuntimeFailure => RuntimeFailureExitCode,
            _ => RuntimeFailureExitCode,
        };

    internal static int WriteServerExit(
        WorkerServerExit serverExit,
        Stream standardError) =>
        serverExit.Kind switch
        {
            WorkerServerExitKind.Shutdown or
            WorkerServerExitKind.Eof or
            WorkerServerExitKind.Canceled => 0,
            WorkerServerExitKind.InitializeFailed => WriteFailure(
                InitializeFailureExitCode,
                "initialize_failed",
                NormalizeInitializeDetail(serverExit.DetailCode),
                standardError),
            WorkerServerExitKind.ProtocolError => WriteFailure(
                ProtocolFailureExitCode,
                "protocol_error",
                NormalizeProtocolDetail(serverExit.DetailCode),
                standardError),
            WorkerServerExitKind.TransportFailure => WriteFailure(
                TransportFailureExitCode,
                "transport_failure",
                NormalizeTransportDetail(serverExit.DetailCode),
                standardError),
            WorkerServerExitKind.RuntimeFailure => WriteFailure(
                RuntimeFailureExitCode,
                RuntimeFailureKind,
                NormalizeRuntimeDetail(serverExit.DetailCode),
                standardError),
            _ => WriteFailure(
                RuntimeFailureExitCode,
                RuntimeFailureKind,
                RuntimeFailureDetail,
                standardError),
        };

    private static string NormalizeBootstrapDetail(string? detailCode) => detailCode switch
    {
        "platform_unsupported" => "platform_unsupported",
        "handle_missing" => "handle_missing",
        "handle_invalid" => "handle_invalid",
        "handle_alias" => "handle_alias",
        "handle_duplication_failed" => "handle_duplication_failed",
        "handle_not_pipe" => "handle_not_pipe",
        "handle_inheritable" => "handle_inheritable",
        "stream_creation_failed" => "stream_creation_failed",
        "environment_removal_failed" => "environment_removal_failed",
        "bootstrap_failure" => "bootstrap_failure",
        _ => "bootstrap_failure",
    };

    private static string NormalizeInitializeDetail(string? detailCode) => detailCode switch
    {
        "initialize_deadline_expired" => "initialize_deadline_expired",
        "initialize_canceled" => "initialize_canceled",
        "initialize_failed" => "initialize_failed",
        _ => "initialize_failed",
    };

    private static string NormalizeProtocolDetail(string? detailCode) => detailCode switch
    {
        "bom_forbidden" => "bom_forbidden",
        "duplicate_field" => "duplicate_field",
        "frame_too_large" => "frame_too_large",
        "initialize_required" => "initialize_required",
        "invalid_envelope" => "invalid_envelope",
        "invalid_field" => "invalid_field",
        "invalid_initialize_field" => "invalid_initialize_field",
        "invalid_json" => "invalid_json",
        "invalid_payload" => "invalid_payload",
        "message_before_ready" => "message_before_ready",
        "missing_field" => "missing_field",
        "missing_initialize_field" => "missing_initialize_field",
        "request_id_required" => "request_id_required",
        "truncated_frame" => "truncated_frame",
        "unknown_field" => "unknown_field",
        "unknown_initialize_field" => "unknown_initialize_field",
        "unknown_kind" => "unknown_kind",
        "unknown_version" => "unknown_version",
        "unsupported_message" => "unsupported_message",
        "worker_boot_mismatch" => "worker_boot_mismatch",
        _ => "protocol_error",
    };

    private static string NormalizeTransportDetail(string? detailCode) => detailCode switch
    {
        "request_transport_failure" => "request_transport_failure",
        "event_transport_failure" => "event_transport_failure",
        _ => "transport_failure",
    };

    private static string NormalizeRuntimeDetail(string? detailCode) => detailCode switch
    {
        "cleanup_failed" => "cleanup_failed",
        "initialize_cleanup_failed" => "initialize_cleanup_failed",
        "outbound_protocol_failure" => "outbound_protocol_failure",
        "runtime_failure" => "runtime_failure",
        "shutdown_failed" => "shutdown_failed",
        _ => RuntimeFailureDetail,
    };

    private static int WriteFailure(
        int exitCode,
        string kind,
        string detail,
        Stream standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);

        var line = $"ptk_worker_exit kind={kind} detail={detail}\n";
        if (line.Length > MaximumDiagnosticBytes || line.Any(character => character > 0x7f))
            line = RuntimeFailureDiagnostic;
        var diagnostic = Encoding.ASCII.GetBytes(line);

        try
        {
            standardError.Write(diagnostic, 0, diagnostic.Length);
        }
        catch (Exception)
        {
            // This is the sole best-effort write. A partial or failed
            // diagnostic must not retry or replace the selected exit code.
        }

        return exitCode;
    }
}
