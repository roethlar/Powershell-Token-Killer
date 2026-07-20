using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PtkMcpGuardian.Package;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class MatchedPackageLoaderTests
{
    [Fact]
    public void Valid_current_platform_package_returns_only_immutable_matched_facts()
    {
        using var package = TestPackage.Create();

        var facts = MatchedPackageLoader.Load(package.Root, package.Rid);

        Assert.Equal(Path.GetFullPath(package.PathFor(MatchedPackageRole.HostAppHost)),
            facts.HostAppHostPath);
        Assert.Equal(package.FindEntry(MatchedPackageRole.HostAppHost).Digest,
            facts.HostExecutableDigest);
        Assert.Equal(package.HostBuildDigest, facts.HostBuildDigest);
        Assert.Equal(PublicToolContractResource.ComputeDigest(), facts.PublicContractDigest);
        Assert.Equal(package.ManifestDigest(), facts.PackageManifestDigest);
        Assert.Equal(package.Entries.Count, facts.RequiredArtifactPaths.Count);
        Assert.All(facts.RequiredArtifactPaths, artifact => Assert.True(Path.IsPathFullyQualified(artifact.AbsolutePath)));
        Assert.Equal(
            package.Entries.Select(entry => entry.Role),
            facts.RequiredArtifactPaths.Select(entry => entry.Role));

        var mutable = Assert.IsAssignableFrom<IList<MatchedPackageArtifactPath>>(
            facts.RequiredArtifactPaths);
        Assert.Throws<NotSupportedException>(() => mutable.RemoveAt(0));

        Assert.Equal(
            [
                "HostAppHostPath",
                "HostBuildDigest",
                "HostExecutableDigest",
                "PackageManifestDigest",
                "PublicContractDigest",
                "RequiredArtifactPaths",
            ],
            typeof(MatchedPackageFacts)
                .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Manifest_digest_covers_exact_raw_bytes_including_the_final_lf()
    {
        using var package = TestPackage.Create();
        var before = MatchedPackageLoader.Load(package.Root, package.Rid).PackageManifestDigest;

        var module = package.FindEntry(MatchedPackageRole.Module);
        File.AppendAllText(package.AbsolutePath(module), "metadata-change", Encoding.UTF8);
        package.RefreshFileMetadata(recomputeHostBuild: true);
        var after = MatchedPackageLoader.Load(package.Root, package.Rid).PackageManifestDigest;

        Assert.NotEqual(before, after);
        Assert.Equal(package.ManifestDigest(), after);
    }

    [Fact]
    public void Artifact_byte_tamper_is_rejected_by_exact_file_digest_guard()
    {
        using var package = TestPackage.Create();
        var artifact = package.FindEntry(MatchedPackageRole.Module);
        var bytes = File.ReadAllBytes(package.AbsolutePath(artifact));
        bytes[0] ^= 0x20;
        File.WriteAllBytes(package.AbsolutePath(artifact), bytes);

        AssertFailure(package, "artifact_digest_mismatch");
    }

    [Fact]
    public void Host_build_is_recomputed_from_verified_artifact_metadata()
    {
        using var package = TestPackage.Create();
        var originalBuild = package.HostBuildDigest;
        var artifact = package.FindEntry(MatchedPackageRole.HostManaged);
        File.AppendAllText(package.AbsolutePath(artifact), "changed", Encoding.UTF8);
        package.RefreshFileMetadata(recomputeHostBuild: false);

        Assert.Equal(originalBuild, package.HostBuildDigest);
        AssertFailure(package, "host_build_mismatch");
    }

    [Fact]
    public void Manifest_property_order_duplicate_and_unknown_fields_are_closed()
    {
        using var order = TestPackage.Create();
        order.ReplaceManifestText(text => text.Replace(
            $"{{\"schema_version\":\"ptk.package-manifest/1\",\"package_version\":\"{TestPackage.Version}\"",
            $"{{\"package_version\":\"{TestPackage.Version}\",\"schema_version\":\"ptk.package-manifest/1\"",
            StringComparison.Ordinal));
        AssertFailure(order, "manifest_property_set_invalid");

        using var duplicate = TestPackage.Create();
        duplicate.ReplaceManifestText(text => text.Replace(
            "{\"schema_version\":",
            "{\"schema_version\":\"ptk.package-manifest/1\",\"schema_version\":",
            StringComparison.Ordinal));
        AssertFailure(duplicate, "manifest_duplicate_property");

        using var unknown = TestPackage.Create();
        unknown.ReplaceManifestText(text => text.Replace(
            "{\"schema_version\":",
            "{\"unknown\":true,\"schema_version\":",
            StringComparison.Ordinal));
        AssertFailure(unknown, "manifest_property_set_invalid");
    }

    [Fact]
    public void Manifest_must_be_compact_strict_utf8_with_exactly_one_final_lf()
    {
        using var whitespace = TestPackage.Create();
        whitespace.ReplaceManifestBytes(bytes => [.. bytes.AsSpan(0, 1), (byte)' ', .. bytes.AsSpan(1)]);
        AssertFailure(whitespace, "manifest_not_compact");

        using var noLf = TestPackage.Create();
        noLf.ReplaceManifestBytes(bytes => bytes[..^1]);
        AssertFailure(noLf, "manifest_encoding_invalid");

        using var invalidUtf8 = TestPackage.Create();
        invalidUtf8.ReplaceManifestBytes(bytes => [0xff, .. bytes]);
        AssertFailure(invalidUtf8, "manifest_encoding_invalid");
    }

    [Fact]
    public void Paths_must_be_safe_unique_ordered_and_must_not_name_the_manifest()
    {
        using var invalid = TestPackage.Create();
        invalid.ReplaceManifestText(text => text.Replace(
            "\"path\":\"VERSION\"",
            "\"path\":\"../evil\"",
            StringComparison.Ordinal));
        AssertFailure(invalid, "artifact_path_invalid");

        using var duplicate = TestPackage.Create();
        var second = duplicate.Entries[1].RelativePath;
        duplicate.ReplaceManifestText(text => text.Replace(
            $"\"path\":\"{second}\"",
            "\"path\":\"VERSION\"",
            StringComparison.Ordinal));
        AssertFailure(duplicate, "artifact_path_order_invalid");

        using var unordered = TestPackage.Create();
        unordered.ReplaceManifestText(text => text.Replace(
            "\"path\":\"VERSION\"",
            "\"path\":\"zzz\"",
            StringComparison.Ordinal));
        AssertFailure(unordered, "artifact_path_order_invalid");

        using var self = TestPackage.Create();
        self.ReplaceManifestText(text => text.Replace(
            "\"path\":\"VERSION\"",
            "\"path\":\"bin/ptk-package-manifest.json\"",
            StringComparison.Ordinal));
        AssertFailure(self, "manifest_self_entry");
    }

    [Fact]
    public void Artifact_symlink_or_reparse_point_is_rejected_before_hashing()
    {
        using var package = TestPackage.Create();
        var artifact = package.FindEntry(MatchedPackageRole.Module);
        var path = package.AbsolutePath(artifact);
        var target = Path.Combine(package.Root, "unlisted-target");
        File.WriteAllBytes(target, File.ReadAllBytes(path));
        File.Delete(path);
        File.CreateSymbolicLink(path, target);

        AssertFailure(package, "artifact_path_unsafe");
    }

    [Fact]
    public void Package_root_and_manifest_reparse_points_are_rejected()
    {
        using var package = TestPackage.Create();
        var rootLink = package.Root + "-link";
        try
        {
            Directory.CreateSymbolicLink(rootLink, package.Root);
            var rootFailure = Assert.Throws<MatchedPackageValidationException>(() =>
                MatchedPackageLoader.Load(rootLink, package.Rid));
            Assert.Equal("package_root_invalid", rootFailure.DetailCode);

            var manifest = package.ManifestPath;
            var target = Path.Combine(package.Root, "manifest-target.json");
            File.Move(manifest, target);
            File.CreateSymbolicLink(manifest, target);
            AssertFailure(package, "manifest_path_unsafe");
        }
        finally
        {
            if (Directory.Exists(rootLink) || File.Exists(rootLink))
                Directory.Delete(rootLink);
        }
    }

    [Fact]
    public void Runtime_rid_protocol_and_public_contract_are_exact()
    {
        using var rid = TestPackage.Create();
        var differentRid = rid.Rid == "win-x64" ? "win-arm64" : "win-x64";
        var ridFailure = Assert.Throws<MatchedPackageValidationException>(() =>
            MatchedPackageLoader.Load(rid.Root, differentRid));
        Assert.Equal("runtime_rid_mismatch", ridFailure.DetailCode);

        using var protocol = TestPackage.Create();
        protocol.ReplaceManifestText(text => text.Replace(
            "\"private_protocol_version\":1",
            "\"private_protocol_version\":2",
            StringComparison.Ordinal));
        AssertFailure(protocol, "private_protocol_mismatch");

        using var contract = TestPackage.Create();
        contract.ReplaceManifestText(text => text.Replace(
            $"\"public_contract_sha256\":\"{contract.PublicContractDigest.Value}\"",
            $"\"public_contract_sha256\":\"{new string('0', 64)}\"",
            StringComparison.Ordinal));
        AssertFailure(contract, "public_contract_mismatch");
    }

    [Fact]
    public void Required_role_set_is_exact_for_the_selected_rid()
    {
        using var package = TestPackage.Create();
        package.ReplaceManifestText(text => text.Replace(
            "\"role\":\"audit_admin\"",
            "\"role\":\"host_apphost\"",
            StringComparison.Ordinal));

        AssertFailure(package, "artifact_role_set_invalid");
    }

    [Fact]
    public void Declared_and_actual_modes_follow_the_closed_platform_role_mapping()
    {
        using var package = TestPackage.Create();
        if (package.IsUnix)
        {
            var executable = package.FindEntry(MatchedPackageRole.HostAppHost);
            SetUnixMode(package.AbsolutePath(executable), (UnixFileMode)420);
            AssertFailure(package, "artifact_mode_mismatch");
        }
        else
        {
            package.ReplaceManifestText(text => text.Replace(
                "\"unix_mode\":null",
                "\"unix_mode\":420",
                StringComparison.Ordinal));
            AssertFailure(package, "artifact_mode_mismatch");
        }
    }

    [Fact]
    public void Version_bytes_and_all_declared_file_metadata_are_exact()
    {
        using var version = TestPackage.Create();
        version.WriteVersion(TestPackage.AlternateSameLengthVersion);
        version.RefreshFileMetadata(recomputeHostBuild: true);
        AssertFailure(version, "package_version_mismatch");

        using var length = TestPackage.Create();
        var artifact = length.FindEntry(MatchedPackageRole.Script);
        File.AppendAllText(length.AbsolutePath(artifact), "x", Encoding.UTF8);
        AssertFailure(length, "artifact_length_mismatch");

        using var digest = TestPackage.Create();
        var original = digest.FindEntry(MatchedPackageRole.Module).Digest.Value;
        digest.ReplaceManifestText(text => text.Replace(
            $"\"sha256\":\"{original}\"",
            $"\"sha256\":\"{new string('f', 64)}\"",
            StringComparison.Ordinal));
        AssertFailure(digest, "artifact_digest_mismatch");
    }

    [Fact]
    public void Failures_are_bounded_and_do_not_disclose_paths_or_inner_exceptions()
    {
        using var package = TestPackage.Create();
        File.Delete(package.PathFor(MatchedPackageRole.Script));

        var failure = Assert.Throws<MatchedPackageValidationException>(() =>
            MatchedPackageLoader.Load(package.Root, package.Rid));

        Assert.Equal("artifact_unavailable", failure.DetailCode);
        Assert.Equal("Matched package validation failed (artifact_unavailable).", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(package.Root, failure.ToString(), StringComparison.Ordinal);
    }

    private static void AssertFailure(TestPackage package, string expectedDetailCode)
    {
        var failure = Assert.Throws<MatchedPackageValidationException>(() =>
            MatchedPackageLoader.Load(package.Root, package.Rid));
        Assert.Equal(expectedDetailCode, failure.DetailCode);
        Assert.Null(failure.InnerException);
    }

    private static void SetUnixMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        File.SetUnixFileMode(path, mode);
    }

    private sealed class TestPackage : IDisposable
    {
        internal const string Version = "0.2.0-test";
        internal const string AlternateSameLengthVersion = "0.2.1-test";
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private static readonly HashSet<MatchedPackageRole> BuildRoles =
        [
            MatchedPackageRole.ContainmentHelper,
            MatchedPackageRole.GuardianHelper,
            MatchedPackageRole.HostAppHost,
            MatchedPackageRole.HostManaged,
            MatchedPackageRole.HostRuntime,
            MatchedPackageRole.SharedContract,
        ];
        private bool _disposed;

        private TestPackage(string root, string rid, bool isUnix, List<Entry> entries)
        {
            Root = root;
            Rid = rid;
            IsUnix = isUnix;
            Entries = entries;
            PublicContractDigest = PublicToolContractResource.ComputeDigest();
            HostBuildDigest = ComputeHostBuildDigest();
        }

        internal string Root { get; }

        internal string Rid { get; }

        internal bool IsUnix { get; }

        internal List<Entry> Entries { get; }

        internal Sha256Digest PublicContractDigest { get; }

        internal Sha256Digest HostBuildDigest { get; private set; }

        internal string ManifestPath => Path.Combine(Root, "bin", "ptk-package-manifest.json");

        internal static TestPackage Create()
        {
            var rid = CurrentRid();
            var unix = !OperatingSystem.IsWindows();
            var root = Path.Combine(Path.GetTempPath(), $"ptk-package-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = new (string Path, MatchedPackageRole Role, string RoleName)[]
            {
                ("VERSION", MatchedPackageRole.Version, "version"),
                (OperatingSystem.IsWindows() ? "bin/PtkAuditAdmin.exe" : "bin/PtkAuditAdmin", MatchedPackageRole.AuditAdmin, "audit_admin"),
                ("bin/PtkContainmentBroker", MatchedPackageRole.ContainmentHelper, "containment_helper"),
                ("bin/PtkGuardianBroker", MatchedPackageRole.GuardianHelper, "guardian_helper"),
                (OperatingSystem.IsWindows() ? "bin/PtkMcpGuardian.exe" : "bin/PtkMcpGuardian", MatchedPackageRole.GuardianAppHost, "guardian_apphost"),
                ("bin/PtkMcpGuardian.dll", MatchedPackageRole.GuardianManaged, "guardian_managed"),
                (OperatingSystem.IsWindows() ? "bin/PtkMcpServer.exe" : "bin/PtkMcpServer", MatchedPackageRole.HostAppHost, "host_apphost"),
                ("bin/PtkMcpServer.dll", MatchedPackageRole.HostManaged, "host_managed"),
                ("bin/PtkMcpServer.runtimeconfig.json", MatchedPackageRole.HostRuntime, "host_runtime"),
                ("bin/PtkSharedContracts.dll", MatchedPackageRole.SharedContract, "shared_contract"),
                ("scripts/ptk_init.ps1", MatchedPackageRole.Script, "script"),
                ("src/PwshTokenCompressor.psm1", MatchedPackageRole.Module, "module"),
            };
            if (!unix)
            {
                paths = paths.Where(value => value.Role is not
                    (MatchedPackageRole.ContainmentHelper or MatchedPackageRole.GuardianHelper)).ToArray();
            }

            var entries = new List<Entry>();
            foreach (var item in paths.OrderBy(value => value.Path, StringComparer.Ordinal))
            {
                var absolute = Path.Combine(root, item.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
                var content = item.Role == MatchedPackageRole.Version
                    ? Version
                    : $"fixture:{item.RoleName}:{rid}";
                File.WriteAllBytes(absolute, Utf8.GetBytes(content));
                int? mode = unix ? RequiredUnixMode(item.Role) : null;
                if (unix)
                    SetUnixMode(absolute, (UnixFileMode)mode!.Value);
                entries.Add(new Entry(
                    item.Path,
                    item.Role,
                    item.RoleName,
                    new FileInfo(absolute).Length,
                    Sha256Digest.Compute(File.ReadAllBytes(absolute)),
                    mode));
            }

            var package = new TestPackage(root, rid, unix, entries);
            package.WriteManifest();
            return package;
        }

        internal Entry FindEntry(MatchedPackageRole role) => Entries.Single(value => value.Role == role);

        internal string PathFor(MatchedPackageRole role) => AbsolutePath(FindEntry(role));

        internal string AbsolutePath(Entry entry) => Path.Combine(
            Root,
            entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        internal void WriteVersion(string version)
        {
            var entry = FindEntry(MatchedPackageRole.Version);
            File.WriteAllBytes(AbsolutePath(entry), Utf8.GetBytes(version));
            if (IsUnix)
                SetUnixMode(AbsolutePath(entry), (UnixFileMode)entry.UnixMode!.Value);
        }

        internal void RefreshFileMetadata(bool recomputeHostBuild)
        {
            for (var index = 0; index < Entries.Count; index++)
            {
                var entry = Entries[index];
                var path = AbsolutePath(entry);
                Entries[index] = entry with
                {
                    ByteLength = new FileInfo(path).Length,
                    Digest = Sha256Digest.Compute(File.ReadAllBytes(path)),
                };
            }
            if (recomputeHostBuild)
                HostBuildDigest = ComputeHostBuildDigest();
            WriteManifest();
        }

        internal void ReplaceManifestText(Func<string, string> mutate)
        {
            var text = Utf8.GetString(File.ReadAllBytes(ManifestPath));
            File.WriteAllBytes(ManifestPath, Utf8.GetBytes(mutate(text)));
            if (IsUnix)
                SetUnixMode(ManifestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        internal void ReplaceManifestBytes(Func<byte[], byte[]> mutate)
        {
            File.WriteAllBytes(ManifestPath, mutate(File.ReadAllBytes(ManifestPath)));
            if (IsUnix)
                SetUnixMode(ManifestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        internal Sha256Digest ManifestDigest()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData("ptk.package-manifest/1\0"u8);
            hash.AppendData(File.ReadAllBytes(ManifestPath));
            return new Sha256Digest(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private void WriteManifest()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("schema_version", "ptk.package-manifest/1");
                writer.WriteString("package_version", Version);
                writer.WriteString("rid", Rid);
                writer.WriteNumber("private_protocol_version", 1);
                writer.WriteString("public_contract_sha256", PublicContractDigest.Value);
                writer.WriteString("host_build_sha256", HostBuildDigest.Value);
                writer.WriteStartArray("files");
                foreach (var entry in Entries)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", entry.RelativePath);
                    writer.WriteString("role", entry.RoleName);
                    writer.WriteNumber("bytes", entry.ByteLength);
                    writer.WriteString("sha256", entry.Digest.Value);
                    if (entry.UnixMode is { } mode)
                        writer.WriteNumber("unix_mode", mode);
                    else
                        writer.WriteNull("unix_mode");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            stream.WriteByte((byte)'\n');
            File.WriteAllBytes(ManifestPath, stream.ToArray());
            if (IsUnix)
                SetUnixMode(ManifestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        private Sha256Digest ComputeHostBuildDigest()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData("ptk.host-build/1\0"u8);
            Span<byte> u32 = stackalloc byte[4];
            Span<byte> u64 = stackalloc byte[8];
            foreach (var entry in Entries
                         .Where(entry => BuildRoles.Contains(entry.Role))
                         .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal))
            {
                var pathBytes = Utf8.GetBytes(entry.RelativePath);
                BinaryPrimitives.WriteUInt32BigEndian(u32, checked((uint)pathBytes.Length));
                hash.AppendData(u32);
                hash.AppendData(pathBytes);
                BinaryPrimitives.WriteUInt64BigEndian(u64, checked((ulong)entry.ByteLength));
                hash.AppendData(u64);
                hash.AppendData(Convert.FromHexString(entry.Digest.Value));
            }
            return new Sha256Digest(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }

        private static int RequiredUnixMode(MatchedPackageRole role) => role is
            MatchedPackageRole.AuditAdmin or
            MatchedPackageRole.ContainmentHelper or
            MatchedPackageRole.GuardianAppHost or
            MatchedPackageRole.GuardianHelper or
            MatchedPackageRole.HostAppHost ? 493 : 420;

        private static string CurrentRid()
        {
            var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => "x64",
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                _ => throw new PlatformNotSupportedException("Test requires x64 or arm64."),
            };
            if (OperatingSystem.IsWindows())
                return $"win-{architecture}";
            if (OperatingSystem.IsLinux())
                return $"linux-{architecture}";
            if (OperatingSystem.IsMacOS() && architecture == "arm64")
                return "osx-arm64";
            throw new PlatformNotSupportedException("Test requires a frozen package RID.");
        }

        internal sealed record Entry(
            string RelativePath,
            MatchedPackageRole Role,
            string RoleName,
            long ByteLength,
            Sha256Digest Digest,
            int? UnixMode);
    }
}
