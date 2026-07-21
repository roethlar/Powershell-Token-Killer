using System.Text;

namespace PtkMcpServer.GuardianHost;

/// <summary>
/// Maps private-host bootstrap/runtime failure to bounded infrastructure-only
/// process diagnostics. Exception text, paths, environment, and user content
/// never cross this boundary.
/// </summary>
internal static class PrivateHostProcessExit
{
    internal const int BootstrapFailureExitCode = 80;
    internal const int RuntimeFailureExitCode = 84;
    internal const int MaximumDiagnosticBytes = 256;

    private const string RuntimeDiagnostic =
        "ptk_host_exit kind=runtime_failure detail=runtime_failure\n";

    internal static int CompleteBootstrapFailure(
        string? detailCode,
        Func<Stream>? standardErrorFactory = null) =>
        Complete(
            BootstrapFailureExitCode,
            "bootstrap_failure",
            NormalizeBootstrapDetail(detailCode),
            standardErrorFactory);

    internal static int CompleteRuntimeFailure(
        Func<Stream>? standardErrorFactory = null) =>
        Complete(
            RuntimeFailureExitCode,
            "runtime_failure",
            "runtime_failure",
            standardErrorFactory);

    private static int Complete(
        int exitCode,
        string kind,
        string detail,
        Func<Stream>? standardErrorFactory)
    {
        var line = $"ptk_host_exit kind={kind} detail={detail}\n";
        if (line.Length > MaximumDiagnosticBytes ||
            line.Any(character => character > 0x7f))
        {
            line = RuntimeDiagnostic;
        }
        var diagnostic = Encoding.ASCII.GetBytes(line);

        try
        {
            var standardError = (standardErrorFactory ?? Console.OpenStandardError)();
            standardError.Write(diagnostic, 0, diagnostic.Length);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // This is the sole best-effort write. Its failure cannot change
            // the selected process exit or trigger a second diagnostic.
        }
        return exitCode;
    }

    private static string NormalizeBootstrapDetail(string? detailCode) =>
        detailCode switch
        {
            "bootstrap_consumed" => "bootstrap_consumed",
            "configuration_digest_invalid" => "configuration_digest_invalid",
            "environment_capture_failed" => "environment_capture_failed",
            "environment_removal_failed" => "environment_removal_failed",
            "guardian_boot_id_invalid" => "guardian_boot_id_invalid",
            "handle_alias" => "handle_alias",
            "handle_cleanup_failed" => "handle_cleanup_failed",
            "handle_invalid" => "handle_invalid",
            "handle_missing" => "handle_missing",
            "handle_ownership_failed" => "handle_ownership_failed",
            "host_boot_id_invalid" => "host_boot_id_invalid",
            "host_build_digest_invalid" => "host_build_digest_invalid",
            "host_executable_digest_invalid" => "host_executable_digest_invalid",
            "host_generation_invalid" => "host_generation_invalid",
            "package_manifest_digest_invalid" => "package_manifest_digest_invalid",
            "public_contract_digest_invalid" => "public_contract_digest_invalid",
            "stream_cleanup_failed" => "stream_cleanup_failed",
            "stream_creation_failed" => "stream_creation_failed",
            _ => "bootstrap_failure",
        };

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;
}
