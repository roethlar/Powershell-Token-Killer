using System.Runtime.InteropServices;

namespace PtkMcpGuardian.Package;

/// <summary>
/// Resolves the package containing the running guardian and proves that the
/// verified guardian artifacts are the exact process/assembly paths in use.
/// </summary>
internal static class CurrentMatchedPackage
{
    internal static MatchedPackageFacts Load()
    {
        var packageRoot = ResolvePackageRoot(AppContext.BaseDirectory);
        var package = MatchedPackageLoader.Load(
            packageRoot,
            CurrentRuntimeIdentifier());
        VerifyRunningArtifacts(
            package,
            Environment.ProcessPath,
            typeof(CurrentMatchedPackage).Assembly.Location);
        return package;
    }

    internal static string ResolvePackageRoot(string appBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(appBaseDirectory))
            throw Failure("guardian_base_directory_invalid");

        string bin;
        try
        {
            bin = Path.GetFullPath(appBaseDirectory);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or IOException)
        {
            throw Failure("guardian_base_directory_invalid");
        }

        var trimmed = Path.TrimEndingDirectorySeparator(bin);
        var leaf = Path.GetFileName(trimmed);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(leaf, "bin", pathComparison))
            throw Failure("guardian_package_layout_invalid");

        var parent = Directory.GetParent(trimmed);
        if (parent is null)
            throw Failure("guardian_package_layout_invalid");
        return parent.FullName;
    }

    internal static string CurrentRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw Failure("runtime_platform_unsupported"),
        };

        if (OperatingSystem.IsWindows())
            return $"win-{architecture}";
        if (OperatingSystem.IsLinux())
            return $"linux-{architecture}";
        if (OperatingSystem.IsMacOS() && architecture == "arm64")
            return "osx-arm64";
        throw Failure("runtime_platform_unsupported");
    }

    internal static void VerifyRunningArtifacts(
        MatchedPackageFacts package,
        string? processPath,
        string assemblyPath)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(processPath))
            throw Failure("guardian_apphost_identity_unavailable");
        if (string.IsNullOrWhiteSpace(assemblyPath))
            throw Failure("guardian_managed_identity_unavailable");

        string actualAppHost;
        string actualManaged;
        try
        {
            actualAppHost = Path.GetFullPath(processPath);
            actualManaged = Path.GetFullPath(assemblyPath);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or IOException)
        {
            throw Failure("guardian_running_identity_invalid");
        }

        var expectedAppHost = package.RequiredArtifactPaths.Single(
            artifact => artifact.Role == MatchedPackageRole.GuardianAppHost);
        var expectedManaged = package.RequiredArtifactPaths.Single(
            artifact => artifact.Role == MatchedPackageRole.GuardianManaged);
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        if (!comparer.Equals(expectedAppHost.AbsolutePath, actualAppHost))
            throw Failure("guardian_apphost_identity_mismatch");
        if (!comparer.Equals(expectedManaged.AbsolutePath, actualManaged))
            throw Failure("guardian_managed_identity_mismatch");
    }

    private static MatchedPackageValidationException Failure(string detailCode) => new(detailCode);
}
