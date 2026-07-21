using PtkMcpGuardian.Host;
using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Package;
using PtkMcpGuardian.Standalone;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class PrivateHostLaunchCommandTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb"));
    private static readonly HostGeneration Generation = new(17);
    private static readonly Sha256Digest ExecutableDigest = Digest('a');
    private static readonly Sha256Digest BuildDigest = Digest('b');
    private static readonly Sha256Digest PublicDigest = Digest('c');
    private static readonly Sha256Digest ConfigurationDigest = Digest('d');
    private static readonly Sha256Digest CatalogDigest = Digest('e');
    private static readonly Sha256Digest PackageDigest = Digest('f');
    private static readonly Sha256Digest DifferentDigest = Digest('0');

    [Fact]
    public void Verified_package_identity_produces_one_exact_private_host_launch()
    {
        var path = AbsoluteHostPath();
        var command = new PrivateHostLaunchCommand(
            Package(path),
            Pins(),
            new GuardianHostIdentity(Guardian, Host, Generation),
            requestReadHandle: 101,
            eventWriteHandle: 202);

        Assert.Equal(path, command.ExecutablePath);
        Assert.Equal(Path.GetDirectoryName(path), command.WorkingDirectory);
        Assert.Equal(["--host"], command.Arguments);
        Assert.Equal([(nuint)101, (nuint)202], command.InheritedHandles);
        Assert.Equal(
            [
                "PTK_HOST_REQUEST_READ_HANDLE",
                "PTK_HOST_EVENT_WRITE_HANDLE",
                "PTK_HOST_GUARDIAN_BOOT_ID",
                "PTK_HOST_BOOT_ID",
                "PTK_HOST_GENERATION",
                "PTK_HOST_EXECUTABLE_SHA256",
                "PTK_HOST_BUILD_SHA256",
                "PTK_HOST_PUBLIC_CONTRACT_SHA256",
                "PTK_HOST_CONFIGURATION_SHA256",
                "PTK_HOST_PACKAGE_MANIFEST_SHA256",
            ],
            command.BootstrapEnvironment.Keys);
        Assert.Equal("101", command.BootstrapEnvironment["PTK_HOST_REQUEST_READ_HANDLE"]);
        Assert.Equal("202", command.BootstrapEnvironment["PTK_HOST_EVENT_WRITE_HANDLE"]);
        Assert.Equal(Guardian.ToString(), command.BootstrapEnvironment["PTK_HOST_GUARDIAN_BOOT_ID"]);
        Assert.Equal(Host.ToString(), command.BootstrapEnvironment["PTK_HOST_BOOT_ID"]);
        Assert.Equal("17", command.BootstrapEnvironment["PTK_HOST_GENERATION"]);
        Assert.Equal(ExecutableDigest.Value,
            command.BootstrapEnvironment["PTK_HOST_EXECUTABLE_SHA256"]);
        Assert.Equal(BuildDigest.Value,
            command.BootstrapEnvironment["PTK_HOST_BUILD_SHA256"]);
        Assert.Equal(PublicDigest.Value,
            command.BootstrapEnvironment["PTK_HOST_PUBLIC_CONTRACT_SHA256"]);
        Assert.Equal(ConfigurationDigest.Value,
            command.BootstrapEnvironment["PTK_HOST_CONFIGURATION_SHA256"]);
        Assert.Equal(PackageDigest.Value,
            command.BootstrapEnvironment["PTK_HOST_PACKAGE_MANIFEST_SHA256"]);
        Assert.DoesNotContain(CatalogDigest.Value, command.BootstrapEnvironment.Values);
    }

    [Fact]
    public void Launch_collections_are_frozen_after_construction()
    {
        var command = Command();

        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<string>>(command.Arguments).Add("extra"));
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<nuint>>(command.InheritedHandles).Add(303));
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IDictionary<string, string>>(
                command.BootstrapEnvironment).Add("AMBIENT", "value"));
    }

    [Theory]
    [InlineData("executable")]
    [InlineData("build")]
    [InlineData("public")]
    [InlineData("package")]
    public void Package_and_supervisor_pin_mismatch_never_yields_launch_authority(
        string mismatch)
    {
        var pins = Pins(
            executable: mismatch == "executable" ? DifferentDigest : ExecutableDigest,
            build: mismatch == "build" ? DifferentDigest : BuildDigest,
            @public: mismatch == "public" ? DifferentDigest : PublicDigest,
            package: mismatch == "package" ? DifferentDigest : PackageDigest);

        var exception = Assert.Throws<PrivateHostLaunchCommandException>(() =>
            new PrivateHostLaunchCommand(
                Package(AbsoluteHostPath()),
                pins,
                new GuardianHostIdentity(Guardian, Host, Generation),
                101,
                202));

        Assert.Equal("package_pin_mismatch", exception.DetailCode);
        AssertNormalized(exception);
    }

    [Fact]
    public void Invalid_or_aliased_inherited_handles_are_rejected()
    {
        AssertFailure("handle_invalid", request: 0, events: 202);
        AssertFailure("handle_invalid", request: 101, events: nuint.MaxValue);
        AssertFailure("handle_alias", request: 101, events: 101);
    }

    [Fact]
    public void Nonabsolute_host_path_is_rejected_without_disclosing_it()
    {
        const string secretPath = "relative/private-host-secret";

        var exception = Assert.Throws<PrivateHostLaunchCommandException>(() =>
            new PrivateHostLaunchCommand(
                Package(secretPath),
                Pins(),
                new GuardianHostIdentity(Guardian, Host, Generation),
                101,
                202));

        Assert.Equal("host_path_invalid", exception.DetailCode);
        Assert.DoesNotContain(secretPath, exception.ToString(), StringComparison.Ordinal);
        AssertNormalized(exception);
    }

    private static void AssertFailure(string detailCode, nuint request, nuint events)
    {
        var exception = Assert.Throws<PrivateHostLaunchCommandException>(() =>
            new PrivateHostLaunchCommand(
                Package(AbsoluteHostPath()),
                Pins(),
                new GuardianHostIdentity(Guardian, Host, Generation),
                request,
                events));
        Assert.Equal(detailCode, exception.DetailCode);
        AssertNormalized(exception);
    }

    private static PrivateHostLaunchCommand Command() =>
        new(
            Package(AbsoluteHostPath()),
            Pins(),
            new GuardianHostIdentity(Guardian, Host, Generation),
            101,
            202);

    private static MatchedPackageFacts Package(string hostPath) =>
        new(
            hostPath,
            ExecutableDigest,
            BuildDigest,
            PublicDigest,
            PackageDigest,
            Array.Empty<MatchedPackageArtifactPath>());

    private static GuardianHostSupervisorPins Pins(
        Sha256Digest? executable = null,
        Sha256Digest? build = null,
        Sha256Digest? @public = null,
        Sha256Digest? package = null) =>
        new(
            executable ?? ExecutableDigest,
            build ?? BuildDigest,
            @public ?? PublicDigest,
            ConfigurationDigest,
            CatalogDigest,
            package ?? PackageDigest);

    private static string AbsoluteHostPath() => Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "ptk-private-host-package",
        "bin",
        OperatingSystem.IsWindows() ? "PtkMcpServer.exe" : "PtkMcpServer"));

    private static Sha256Digest Digest(char value) => new(new string(value, 64));

    private static void AssertNormalized(PrivateHostLaunchCommandException exception)
    {
        Assert.Equal("Private host launch command failed.", exception.Message);
        Assert.Null(exception.InnerException);
    }
}
