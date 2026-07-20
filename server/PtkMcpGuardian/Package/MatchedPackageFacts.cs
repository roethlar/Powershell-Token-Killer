using PtkSharedContracts;

namespace PtkMcpGuardian.Package;

internal enum MatchedPackageRole
{
    AuditAdmin,
    ContainmentHelper,
    GuardianAppHost,
    GuardianHelper,
    GuardianManaged,
    HostAppHost,
    HostManaged,
    HostRuntime,
    Module,
    Script,
    SharedContract,
    Version,
}

internal sealed record MatchedPackageArtifactPath(
    MatchedPackageRole Role,
    string AbsolutePath);

/// <summary>
/// Immutable, fully verified identity of one matched runtime package. This is
/// deliberately narrower than guardian composition: configuration and catalog
/// identity are selected after package validation.
/// </summary>
internal sealed class MatchedPackageFacts
{
    private readonly MatchedPackageArtifactPath[] _requiredArtifactPaths;

    internal MatchedPackageFacts(
        string hostAppHostPath,
        Sha256Digest hostExecutableDigest,
        Sha256Digest hostBuildDigest,
        Sha256Digest publicContractDigest,
        Sha256Digest packageManifestDigest,
        IEnumerable<MatchedPackageArtifactPath> requiredArtifactPaths)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostAppHostPath);
        ArgumentNullException.ThrowIfNull(hostExecutableDigest);
        ArgumentNullException.ThrowIfNull(hostBuildDigest);
        ArgumentNullException.ThrowIfNull(publicContractDigest);
        ArgumentNullException.ThrowIfNull(packageManifestDigest);
        ArgumentNullException.ThrowIfNull(requiredArtifactPaths);

        HostAppHostPath = hostAppHostPath;
        HostExecutableDigest = hostExecutableDigest;
        HostBuildDigest = hostBuildDigest;
        PublicContractDigest = publicContractDigest;
        PackageManifestDigest = packageManifestDigest;
        _requiredArtifactPaths = requiredArtifactPaths.ToArray();
        RequiredArtifactPaths = Array.AsReadOnly(_requiredArtifactPaths);
    }

    internal string HostAppHostPath { get; }

    internal Sha256Digest HostExecutableDigest { get; }

    internal Sha256Digest HostBuildDigest { get; }

    internal Sha256Digest PublicContractDigest { get; }

    internal Sha256Digest PackageManifestDigest { get; }

    internal IReadOnlyList<MatchedPackageArtifactPath> RequiredArtifactPaths { get; }
}

internal sealed class MatchedPackageValidationException : Exception
{
    internal MatchedPackageValidationException(string detailCode)
        : base($"Matched package validation failed ({detailCode}).")
    {
        DetailCode = detailCode;
    }

    internal string DetailCode { get; }
}
