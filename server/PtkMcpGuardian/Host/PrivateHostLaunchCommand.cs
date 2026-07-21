using System.Collections.ObjectModel;
using System.Globalization;
using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Package;
using PtkMcpGuardian.Standalone;
using PtkSharedContracts;

namespace PtkMcpGuardian.Host;

/// <summary>
/// Immutable launch authority for one verified private-host generation. The
/// platform containment launcher may inherit only the two listed handles and
/// must apply the exact bootstrap environment before releasing the child.
/// </summary>
internal sealed class PrivateHostLaunchCommand
{
    internal PrivateHostLaunchCommand(
        MatchedPackageFacts package,
        GuardianHostSupervisorPins pins,
        GuardianHostIdentity identity,
        nuint requestReadHandle,
        nuint eventWriteHandle)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentNullException.ThrowIfNull(identity);
        if (package.HostExecutableDigest != pins.HostExecutableDigest ||
            package.HostBuildDigest != pins.HostBuildDigest ||
            package.PublicContractDigest != pins.PublicContractDigest ||
            package.PackageManifestDigest != pins.PackageManifestDigest)
        {
            throw Failure("package_pin_mismatch");
        }

        var workingDirectory = ValidateHostPath(package.HostAppHostPath);
        ValidateHandle(requestReadHandle);
        ValidateHandle(eventWriteHandle);
        if (requestReadHandle == eventWriteHandle)
            throw Failure("handle_alias");

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PrivateHostBootstrapEnvironment.RequestReadHandle] =
                FormatHandle(requestReadHandle),
            [PrivateHostBootstrapEnvironment.EventWriteHandle] =
                FormatHandle(eventWriteHandle),
            [PrivateHostBootstrapEnvironment.GuardianBootId] =
                identity.GuardianBootId.ToString(),
            [PrivateHostBootstrapEnvironment.HostBootId] =
                identity.HostBootId.ToString(),
            [PrivateHostBootstrapEnvironment.HostGeneration] =
                identity.HostGeneration.Value.ToString(CultureInfo.InvariantCulture),
            [PrivateHostBootstrapEnvironment.HostExecutableDigest] =
                pins.HostExecutableDigest.Value,
            [PrivateHostBootstrapEnvironment.HostBuildDigest] =
                pins.HostBuildDigest.Value,
            [PrivateHostBootstrapEnvironment.PublicContractDigest] =
                pins.PublicContractDigest.Value,
            [PrivateHostBootstrapEnvironment.ConfigurationDigest] =
                pins.ConfigurationDigest.Value,
            [PrivateHostBootstrapEnvironment.PackageManifestDigest] =
                pins.PackageManifestDigest.Value,
        };
        if (!environment.Keys.SequenceEqual(
                PrivateHostBootstrapEnvironment.VariablesInCaptureOrder,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Private host launch fields do not match the shared contract.");
        }

        ExecutablePath = package.HostAppHostPath;
        WorkingDirectory = workingDirectory;
        Arguments = Array.AsReadOnly(["--host"]);
        InheritedHandles = Array.AsReadOnly([requestReadHandle, eventWriteHandle]);
        BootstrapEnvironment = new ReadOnlyDictionary<string, string>(environment);
    }

    internal string ExecutablePath { get; }

    internal string WorkingDirectory { get; }

    internal IReadOnlyList<string> Arguments { get; }

    internal IReadOnlyList<nuint> InheritedHandles { get; }

    internal IReadOnlyDictionary<string, string> BootstrapEnvironment { get; }

    private static string ValidateHostPath(string path)
    {
        try
        {
            if (path.Contains('\0') || !Path.IsPathFullyQualified(path))
                throw Failure("host_path_invalid");
            var workingDirectory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(workingDirectory) ||
                !Path.IsPathFullyQualified(workingDirectory))
            {
                throw Failure("host_path_invalid");
            }
            return workingDirectory;
        }
        catch (PrivateHostLaunchCommandException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                           NotSupportedException or
                                           PathTooLongException)
        {
            throw Failure("host_path_invalid");
        }
    }

    private static void ValidateHandle(nuint handle)
    {
        if (handle == 0 || handle == nuint.MaxValue)
            throw Failure("handle_invalid");
    }

    private static string FormatHandle(nuint handle) =>
        ((ulong)handle).ToString(CultureInfo.InvariantCulture);

    private static PrivateHostLaunchCommandException Failure(string detailCode) =>
        new(detailCode);
}

internal sealed class PrivateHostLaunchCommandException : Exception
{
    internal PrivateHostLaunchCommandException(string detailCode)
        : base("Private host launch command failed.")
    {
        DetailCode = detailCode;
    }

    internal string DetailCode { get; }
}
