namespace PtkSharedContracts;

/// <summary>
/// Exact inherited launch fields shared by the guardian launcher and the
/// private host's first-action bootstrap capture.
/// </summary>
internal static class PrivateHostBootstrapEnvironment
{
    internal const string RequestReadHandle = "PTK_HOST_REQUEST_READ_HANDLE";
    internal const string EventWriteHandle = "PTK_HOST_EVENT_WRITE_HANDLE";
    internal const string GuardianBootId = "PTK_HOST_GUARDIAN_BOOT_ID";
    internal const string HostBootId = "PTK_HOST_BOOT_ID";
    internal const string HostGeneration = "PTK_HOST_GENERATION";
    internal const string HostExecutableDigest = "PTK_HOST_EXECUTABLE_SHA256";
    internal const string HostBuildDigest = "PTK_HOST_BUILD_SHA256";
    internal const string PublicContractDigest = "PTK_HOST_PUBLIC_CONTRACT_SHA256";
    internal const string ConfigurationDigest = "PTK_HOST_CONFIGURATION_SHA256";
    internal const string PackageManifestDigest = "PTK_HOST_PACKAGE_MANIFEST_SHA256";

    internal static IReadOnlyList<string> VariablesInCaptureOrder { get; } =
        Array.AsReadOnly(
        [
            RequestReadHandle,
            EventWriteHandle,
            GuardianBootId,
            HostBootId,
            HostGeneration,
            HostExecutableDigest,
            HostBuildDigest,
            PublicContractDigest,
            ConfigurationDigest,
            PackageManifestDigest,
        ]);

    internal static bool IsReserved(string variable) =>
        VariablesInCaptureOrder.Contains(variable, StringComparer.OrdinalIgnoreCase);
}
