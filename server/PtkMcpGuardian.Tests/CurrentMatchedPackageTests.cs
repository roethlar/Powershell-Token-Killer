using PtkMcpGuardian.Package;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class CurrentMatchedPackageTests
{
    [Fact]
    public void Package_root_is_the_parent_of_one_exact_bin_directory()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ptk-current-package"));
        var bin = Path.Combine(root, "bin") + Path.DirectorySeparatorChar;

        Assert.Equal(root, CurrentMatchedPackage.ResolvePackageRoot(bin));

        var failure = Assert.Throws<MatchedPackageValidationException>(() =>
            CurrentMatchedPackage.ResolvePackageRoot(Path.Combine(root, "publish")));
        Assert.Equal("guardian_package_layout_invalid", failure.DetailCode);
        Assert.DoesNotContain(root, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Running_apphost_and_managed_assembly_must_be_the_verified_artifacts()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ptk-running-package"));
        var appHost = Path.Combine(root, "bin", AppHostName());
        var managed = Path.Combine(root, "bin", "PtkMcpGuardian.dll");
        var package = Package(appHost, managed);

        CurrentMatchedPackage.VerifyRunningArtifacts(package, appHost, managed);

        AssertFailure(
            package,
            Path.Combine(root, "other", AppHostName()),
            managed,
            "guardian_apphost_identity_mismatch");
        AssertFailure(
            package,
            appHost,
            Path.Combine(root, "other", "PtkMcpGuardian.dll"),
            "guardian_managed_identity_mismatch");
        AssertFailure(
            package,
            processPath: null,
            managed,
            "guardian_apphost_identity_unavailable");
    }

    [Fact]
    public void Current_runtime_identifier_is_in_the_closed_package_set()
    {
        Assert.Contains(
            CurrentMatchedPackage.CurrentRuntimeIdentifier(),
            new[]
            {
                "win-x64",
                "win-arm64",
                "linux-x64",
                "linux-arm64",
                "osx-arm64",
            });
    }

    private static void AssertFailure(
        MatchedPackageFacts package,
        string? processPath,
        string assemblyPath,
        string detailCode)
    {
        var failure = Assert.Throws<MatchedPackageValidationException>(() =>
            CurrentMatchedPackage.VerifyRunningArtifacts(
                package,
                processPath,
                assemblyPath));
        Assert.Equal(detailCode, failure.DetailCode);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(assemblyPath, failure.ToString(), StringComparison.Ordinal);
    }

    private static MatchedPackageFacts Package(string appHost, string managed) => new(
        Path.Combine(Path.GetDirectoryName(appHost)!, HostAppHostName()),
        Digest('1'),
        Digest('2'),
        Digest('3'),
        Digest('4'),
        [
            new MatchedPackageArtifactPath(
                MatchedPackageRole.GuardianAppHost,
                appHost),
            new MatchedPackageArtifactPath(
                MatchedPackageRole.GuardianManaged,
                managed),
        ]);

    private static string AppHostName() => OperatingSystem.IsWindows()
        ? "PtkMcpGuardian.exe"
        : "PtkMcpGuardian";

    private static string HostAppHostName() => OperatingSystem.IsWindows()
        ? "PtkMcpServer.exe"
        : "PtkMcpServer";

    private static Sha256Digest Digest(char value) => new(new string(value, 64));
}
