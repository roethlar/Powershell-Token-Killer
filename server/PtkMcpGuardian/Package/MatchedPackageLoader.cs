using System.Buffers;
using System.Buffers.Binary;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PtkSharedContracts;

namespace PtkMcpGuardian.Package;

/// <summary>
/// Loads one frozen ptk.package-manifest/1 package and validates every byte of
/// its closed runtime artifact set before returning any launch authority.
/// </summary>
internal static class MatchedPackageLoader
{
    private const string ManifestRelativePath = "bin/ptk-package-manifest.json";
    private const int MaximumManifestBytes = ContractLimits.MaximumManifestBytes;
    private const int MaximumManifestFiles = 4096;
    private const int MaximumPathCharacters = 4096;
    private const int MaximumPackageVersionCharacters = 128;
    private const int PrivateProtocolVersion = ContractLimits.GuardianHostProtocolVersion;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] PackageManifestDomain = "ptk.package-manifest/1\0"u8.ToArray();
    private static readonly byte[] HostBuildDomain = "ptk.host-build/1\0"u8.ToArray();
    private static readonly IReadOnlyDictionary<string, MatchedPackageRole> RoleNames =
        new Dictionary<string, MatchedPackageRole>(StringComparer.Ordinal)
        {
            ["audit_admin"] = MatchedPackageRole.AuditAdmin,
            ["containment_helper"] = MatchedPackageRole.ContainmentHelper,
            ["guardian_apphost"] = MatchedPackageRole.GuardianAppHost,
            ["guardian_helper"] = MatchedPackageRole.GuardianHelper,
            ["guardian_managed"] = MatchedPackageRole.GuardianManaged,
            ["host_apphost"] = MatchedPackageRole.HostAppHost,
            ["host_managed"] = MatchedPackageRole.HostManaged,
            ["host_runtime"] = MatchedPackageRole.HostRuntime,
            ["module"] = MatchedPackageRole.Module,
            ["script"] = MatchedPackageRole.Script,
            ["shared_contract"] = MatchedPackageRole.SharedContract,
            ["version"] = MatchedPackageRole.Version,
        };
    private static readonly IReadOnlyDictionary<MatchedPackageRole, int> UnixModes =
        new Dictionary<MatchedPackageRole, int>
        {
            [MatchedPackageRole.AuditAdmin] = 493,
            [MatchedPackageRole.ContainmentHelper] = 493,
            [MatchedPackageRole.GuardianAppHost] = 493,
            [MatchedPackageRole.GuardianHelper] = 493,
            [MatchedPackageRole.HostAppHost] = 493,
            [MatchedPackageRole.GuardianManaged] = 420,
            [MatchedPackageRole.HostManaged] = 420,
            [MatchedPackageRole.HostRuntime] = 420,
            [MatchedPackageRole.Module] = 420,
            [MatchedPackageRole.Script] = 420,
            [MatchedPackageRole.SharedContract] = 420,
            [MatchedPackageRole.Version] = 420,
        };
    private static readonly HashSet<MatchedPackageRole> HostBuildRoles =
    [
        MatchedPackageRole.ContainmentHelper,
        MatchedPackageRole.GuardianHelper,
        MatchedPackageRole.HostAppHost,
        MatchedPackageRole.HostManaged,
        MatchedPackageRole.HostRuntime,
        MatchedPackageRole.SharedContract,
    ];

    internal static MatchedPackageFacts Load(
        string packageRoot,
        string expectedRuntimeIdentifier)
    {
        try
        {
            return LoadCore(packageRoot, expectedRuntimeIdentifier);
        }
        catch (MatchedPackageValidationException)
        {
            throw;
        }
        catch (Exception exception) when (IsNormalizedFailure(exception))
        {
            throw Failure("package_io_failed");
        }
    }

    private static MatchedPackageFacts LoadCore(
        string packageRoot,
        string expectedRuntimeIdentifier)
    {
        if (!IsKnownRuntimeIdentifier(expectedRuntimeIdentifier))
            throw Failure("runtime_rid_invalid");
        if (string.IsNullOrWhiteSpace(packageRoot))
            throw Failure("package_root_invalid");

        string root;
        try
        {
            root = Path.GetFullPath(packageRoot);
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw Failure("package_root_invalid");
        }

        RequireSafeDirectory(root, "package_root_invalid");
        var manifestPath = ResolveSafeArtifactPath(root, ManifestRelativePath, isManifest: true);
        var manifestBytes = ReadBoundedManifest(manifestPath);
        var manifest = ParseManifest(manifestBytes, expectedRuntimeIdentifier);

        var expectedPublicContract = PublicToolContractResource.ComputeDigest();
        if (manifest.PublicContractDigest != expectedPublicContract)
            throw Failure("public_contract_mismatch");

        var packageManifestDigest = DomainHash(PackageManifestDomain, manifestBytes);
        var verified = new List<VerifiedArtifact>(manifest.Files.Count);
        foreach (var entry in manifest.Files)
        {
            var absolutePath = ResolveSafeArtifactPath(root, entry.RelativePath, isManifest: false);
            VerifyMode(entry, absolutePath, manifest.IsUnixPackage);
            var actualDigest = HashAndVerifyLength(absolutePath, entry.ByteLength);
            if (actualDigest != entry.Digest)
                throw Failure("artifact_digest_mismatch");
            verified.Add(new VerifiedArtifact(entry, absolutePath, actualDigest));
        }

        var version = verified.Single(value => value.Entry.Role == MatchedPackageRole.Version);
        VerifyVersion(version.AbsolutePath, manifest.PackageVersion, version.Entry.ByteLength);

        var hostBuildDigest = ComputeHostBuildDigest(verified);
        if (hostBuildDigest != manifest.HostBuildDigest)
            throw Failure("host_build_mismatch");

        var host = verified.Single(value => value.Entry.Role == MatchedPackageRole.HostAppHost);
        return new MatchedPackageFacts(
            host.AbsolutePath,
            host.ActualDigest,
            hostBuildDigest,
            expectedPublicContract,
            packageManifestDigest,
            verified.Select(value => new MatchedPackageArtifactPath(
                value.Entry.Role,
                value.AbsolutePath)));
    }

    private static ParsedManifest ParseManifest(
        byte[] bytes,
        string expectedRuntimeIdentifier)
    {
        ValidateCompactJsonLine(bytes);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                bytes.AsMemory(0, bytes.Length - 1),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = ContractLimits.MaximumJsonDepth,
                });
        }
        catch (JsonException)
        {
            throw Failure("manifest_json_invalid");
        }

        using (document)
        {
            var root = document.RootElement;
            RejectDuplicateProperties(root);
            RequirePropertyOrder(root,
                "schema_version",
                "package_version",
                "rid",
                "private_protocol_version",
                "public_contract_sha256",
                "host_build_sha256",
                "files");

            if (RequiredString(root, "schema_version") != "ptk.package-manifest/1")
                throw Failure("manifest_schema_version_mismatch");

            var packageVersion = RequiredString(root, "package_version");
            if (packageVersion.Length is < 1 or > MaximumPackageVersionCharacters)
                throw Failure("package_version_invalid");

            var rid = RequiredString(root, "rid");
            if (!IsKnownRuntimeIdentifier(rid))
                throw Failure("manifest_rid_invalid");
            if (!string.Equals(rid, expectedRuntimeIdentifier, StringComparison.Ordinal))
                throw Failure("runtime_rid_mismatch");

            if (!TryGetInt64(root.GetProperty("private_protocol_version"), out var protocol) ||
                protocol != PrivateProtocolVersion)
                throw Failure("private_protocol_mismatch");

            var publicDigest = RequiredDigest(root, "public_contract_sha256");
            var hostBuildDigest = RequiredDigest(root, "host_build_sha256");
            var isUnix = IsUnixRuntimeIdentifier(rid);
            var filesElement = root.GetProperty("files");
            if (filesElement.ValueKind != JsonValueKind.Array ||
                filesElement.GetArrayLength() is < 1 or > MaximumManifestFiles)
                throw Failure("manifest_files_invalid");

            var files = new List<ManifestArtifact>(filesElement.GetArrayLength());
            string? previousPath = null;
            var seenRoles = new HashSet<MatchedPackageRole>();
            foreach (var element in filesElement.EnumerateArray())
            {
                RequirePropertyOrder(element, "path", "role", "bytes", "sha256", "unix_mode");
                var relativePath = RequiredString(element, "path");
                if (!IsSafeManifestRelativePath(relativePath))
                    throw Failure("artifact_path_invalid");
                if (string.Equals(relativePath, ManifestRelativePath, StringComparison.OrdinalIgnoreCase))
                    throw Failure("manifest_self_entry");
                if (previousPath is not null &&
                    StringComparer.Ordinal.Compare(previousPath, relativePath) >= 0)
                    throw Failure("artifact_path_order_invalid");
                previousPath = relativePath;

                var roleName = RequiredString(element, "role");
                if (!RoleNames.TryGetValue(roleName, out var role) || !seenRoles.Add(role))
                    throw Failure("artifact_role_set_invalid");
                if (!TryGetInt64(element.GetProperty("bytes"), out var byteLength) || byteLength < 0)
                    throw Failure("artifact_length_invalid");
                var digest = RequiredDigest(element, "sha256");
                var unixMode = ReadUnixMode(element.GetProperty("unix_mode"));
                RequireDeclaredMode(role, unixMode, isUnix);
                files.Add(new ManifestArtifact(relativePath, role, byteLength, digest, unixMode));
            }

            var requiredRoles = RequiredRoles(isUnix);
            if (files.Count != requiredRoles.Count || !seenRoles.SetEquals(requiredRoles))
                throw Failure("artifact_role_set_invalid");
            var version = files.Single(value => value.Role == MatchedPackageRole.Version);
            if (!string.Equals(version.RelativePath, "VERSION", StringComparison.Ordinal))
                throw Failure("package_version_path_invalid");

            return new ParsedManifest(
                packageVersion,
                isUnix,
                publicDigest,
                hostBuildDigest,
                files.AsReadOnly());
        }
    }

    private static void ValidateCompactJsonLine(byte[] bytes)
    {
        if (bytes.Length is < 3 or > MaximumManifestBytes ||
            bytes[0..Math.Min(3, bytes.Length)].AsSpan().SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }) ||
            bytes[^1] != (byte)'\n')
            throw Failure("manifest_encoding_invalid");
        if (bytes.AsSpan(0, bytes.Length - 1).IndexOf((byte)'\n') >= 0 ||
            bytes.AsSpan().IndexOf((byte)'\r') >= 0)
            throw Failure("manifest_encoding_invalid");
        try
        {
            _ = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Failure("manifest_encoding_invalid");
        }

        var inString = false;
        var escaped = false;
        for (var index = 0; index < bytes.Length - 1; index++)
        {
            var value = bytes[index];
            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (value == (byte)'\\')
                    escaped = true;
                else if (value == (byte)'"')
                    inString = false;
            }
            else if (value == (byte)'"')
            {
                inString = true;
            }
            else if (value is (byte)' ' or (byte)'\t')
            {
                throw Failure("manifest_not_compact");
            }
        }
    }

    private static byte[] ReadBoundedManifest(string manifestPath)
    {
        try
        {
            using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length is < 1 or > MaximumManifestBytes)
                throw Failure("manifest_size_invalid");
            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
                throw Failure("manifest_size_invalid");
            return bytes;
        }
        catch (MatchedPackageValidationException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            throw Failure("manifest_unavailable");
        }
    }

    private static Sha256Digest HashAndVerifyLength(string absolutePath, long expectedLength)
    {
        try
        {
            using var stream = new FileStream(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65_536,
                FileOptions.SequentialScan);
            if (stream.Length != expectedLength)
                throw Failure("artifact_length_mismatch");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(65_536);
            long total = 0;
            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    total = checked(total + read);
                    hash.AppendData(buffer, 0, read);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (total != expectedLength || stream.Length != expectedLength)
                throw Failure("artifact_length_mismatch");
            return new Sha256Digest(
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch (MatchedPackageValidationException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            throw Failure("artifact_unavailable");
        }
    }

    private static void VerifyVersion(
        string absolutePath,
        string packageVersion,
        long expectedLength)
    {
        var expected = StrictUtf8.GetBytes(packageVersion);
        if (expected.LongLength != expectedLength)
            throw Failure("package_version_mismatch");
        try
        {
            var actual = File.ReadAllBytes(absolutePath);
            if (!actual.AsSpan().SequenceEqual(expected))
                throw Failure("package_version_mismatch");
        }
        catch (MatchedPackageValidationException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            throw Failure("artifact_unavailable");
        }
    }

    private static void VerifyMode(
        ManifestArtifact entry,
        string absolutePath,
        bool isUnixPackage)
    {
        if (!isUnixPackage)
            return;
        if (OperatingSystem.IsWindows())
            throw Failure("runtime_platform_mismatch");
        UnixFileMode actual;
        try
        {
            actual = File.GetUnixFileMode(absolutePath);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            throw Failure("artifact_unavailable");
        }
        if ((int)actual != entry.UnixMode)
            throw Failure("artifact_mode_mismatch");
    }

    private static Sha256Digest ComputeHostBuildDigest(
        IEnumerable<VerifiedArtifact> artifacts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(HostBuildDomain);
        Span<byte> u32 = stackalloc byte[sizeof(uint)];
        Span<byte> u64 = stackalloc byte[sizeof(ulong)];
        foreach (var artifact in artifacts
                     .Where(value => HostBuildRoles.Contains(value.Entry.Role))
                     .OrderBy(value => value.Entry.RelativePath, StringComparer.Ordinal))
        {
            var pathBytes = StrictUtf8.GetBytes(artifact.Entry.RelativePath);
            BinaryPrimitives.WriteUInt32BigEndian(u32, checked((uint)pathBytes.Length));
            hash.AppendData(u32);
            hash.AppendData(pathBytes);
            BinaryPrimitives.WriteUInt64BigEndian(u64, checked((ulong)artifact.Entry.ByteLength));
            hash.AppendData(u64);
            hash.AppendData(Convert.FromHexString(artifact.ActualDigest.Value));
        }
        return new Sha256Digest(
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static Sha256Digest DomainHash(ReadOnlySpan<byte> domain, ReadOnlySpan<byte> payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(domain);
        hash.AppendData(payload);
        return new Sha256Digest(
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static string ResolveSafeArtifactPath(
        string root,
        string relativePath,
        bool isManifest)
    {
        string candidate;
        try
        {
            candidate = Path.GetFullPath(
                relativePath.Replace('/', Path.DirectorySeparatorChar),
                root);
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            throw Failure(isManifest ? "manifest_unavailable" : "artifact_path_unsafe");
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, comparison))
            throw Failure(isManifest ? "manifest_unavailable" : "artifact_path_unsafe");

        var current = root;
        var segments = relativePath.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                throw Failure(isManifest ? "manifest_unavailable" : "artifact_unavailable");
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw Failure(isManifest ? "manifest_path_unsafe" : "artifact_path_unsafe");
            var isLast = index == segments.Length - 1;
            if (isLast == attributes.HasFlag(FileAttributes.Directory))
                throw Failure(isManifest ? "manifest_unavailable" : "artifact_unavailable");
        }
        return candidate;
    }

    private static void RequireSafeDirectory(string path, string detailCode)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (!attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
                throw Failure(detailCode);
        }
        catch (MatchedPackageValidationException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            throw Failure(detailCode);
        }
    }

    private static bool IsSafeManifestRelativePath(string path)
    {
        if (path.Length is < 1 or > MaximumPathCharacters ||
            path[0] == '/' || path[^1] == '/' || path.Contains("//", StringComparison.Ordinal))
            return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment is "." or ".." || segment.Length == 0)
                return false;
        }
        return path.All(value =>
            value is >= 'A' and <= 'Z' or
                >= 'a' and <= 'z' or
                >= '0' and <= '9' or
                '.' or '_' or '+' or '-' or '/');
    }

    private static void RequireDeclaredMode(
        MatchedPackageRole role,
        int? declared,
        bool isUnixPackage)
    {
        if (!isUnixPackage)
        {
            if (declared is not null)
                throw Failure("artifact_mode_mismatch");
            return;
        }
        if (!UnixModes.TryGetValue(role, out var required) || declared != required)
            throw Failure("artifact_mode_mismatch");
    }

    private static int? ReadUnixMode(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (!TryGetInt64(value, out var parsed) || parsed is not (420 or 493))
            throw Failure("artifact_mode_invalid");
        return checked((int)parsed);
    }

    private static HashSet<MatchedPackageRole> RequiredRoles(bool isUnix)
    {
        var roles = new HashSet<MatchedPackageRole>
        {
            MatchedPackageRole.AuditAdmin,
            MatchedPackageRole.GuardianAppHost,
            MatchedPackageRole.GuardianManaged,
            MatchedPackageRole.HostAppHost,
            MatchedPackageRole.HostManaged,
            MatchedPackageRole.HostRuntime,
            MatchedPackageRole.Module,
            MatchedPackageRole.Script,
            MatchedPackageRole.SharedContract,
            MatchedPackageRole.Version,
        };
        if (isUnix)
        {
            roles.Add(MatchedPackageRole.ContainmentHelper);
            roles.Add(MatchedPackageRole.GuardianHelper);
        }
        return roles;
    }

    private static Sha256Digest RequiredDigest(JsonElement value, string propertyName)
    {
        var text = RequiredString(value, propertyName);
        try
        {
            return new Sha256Digest(text);
        }
        catch (ArgumentException)
        {
            throw Failure("digest_encoding_invalid");
        }
    }

    private static string RequiredString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } text)
            throw Failure("manifest_schema_invalid");
        return text;
    }

    private static void RequirePropertyOrder(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.EnumerateObject().Select(property => property.Name)
                .SequenceEqual(expected, StringComparer.Ordinal))
            throw Failure("manifest_property_set_invalid");
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Failure("manifest_duplicate_property");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray())
                RejectDuplicateProperties(element);
        }
    }

    private static bool TryGetInt64(JsonElement value, out long parsed)
    {
        parsed = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out parsed);
    }

    private static bool IsKnownRuntimeIdentifier(string? value) => value is
        "win-x64" or "win-arm64" or "linux-x64" or "linux-arm64" or "osx-arm64";

    private static bool IsUnixRuntimeIdentifier(string value) => value is
        "linux-x64" or "linux-arm64" or "osx-arm64";

    private static bool IsNormalizedFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or SecurityException or
        JsonException or DecoderFallbackException or ArgumentException or
        NotSupportedException;

    private static bool IsPathFailure(Exception exception) => exception is
        ArgumentException or NotSupportedException or SecurityException or IOException;

    private static bool IsFileFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or SecurityException or
        NotSupportedException or ArgumentException;

    private static MatchedPackageValidationException Failure(string detailCode) => new(detailCode);

    private sealed record ManifestArtifact(
        string RelativePath,
        MatchedPackageRole Role,
        long ByteLength,
        Sha256Digest Digest,
        int? UnixMode);

    private sealed record ParsedManifest(
        string PackageVersion,
        bool IsUnixPackage,
        Sha256Digest PublicContractDigest,
        Sha256Digest HostBuildDigest,
        IReadOnlyList<ManifestArtifact> Files);

    private sealed record VerifiedArtifact(
        ManifestArtifact Entry,
        string AbsolutePath,
        Sha256Digest ActualDigest);
}
