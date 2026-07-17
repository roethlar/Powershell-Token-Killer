using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using PtkSiemReceiver.Configuration;
using PtkSiemReceiver.Ingest;
using PtkSiemReceiver.Security;

namespace PtkSiemReceiver.Tests;

public sealed class SiemReceiverConfigurationTests : IDisposable
{
    private readonly string _root =
        SiemTestFileSystem.CreateProtectedRoot("ptk-siem-config");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup only.
        }
    }

    // ---- valid configurations -------------------------------------------

    [Fact]
    public void Valid_minimal_configuration_loads_typed_values_and_defaults()
    {
        var configurationPath = WriteConfiguration(ValidConfiguration());
        var options = SiemReceiverConfigurationLoader.Load(configurationPath);

        Assert.Equal(IPAddress.Parse("10.0.0.5"), options.IngestBindAddress);
        Assert.Equal(4318, options.IngestPort);
        Assert.Equal(Path.Combine(_root, "server.pem"), options.ServerCertificatePath);
        Assert.Equal(Path.Combine(_root, "server.key"), options.ServerCertificateKeyPath);
        Assert.Equal([Path.Combine(_root, "client-ca.pem")], options.ClientCaBundlePaths);
        Assert.Equal(X509RevocationMode.Offline, options.RevocationCheckMode);

        // Documented non-security defaults.
        Assert.Equal(
            SiemReceiverConfigurationLoader.DefaultMaxRequestBytes,
            options.MaxRequestBytes);
        Assert.Equal(IPAddress.Loopback, options.OperatorBindAddress);

        Assert.Equal(9443, options.OperatorPort);
        Assert.Equal("operator-token-for-tests", options.OperatorToken);
        Assert.Null(options.OperatorHttpsCertificatePath);
        Assert.Null(options.OperatorHttpsCertificateKeyPath);
        Assert.Equal(Path.Combine(_root, "events.db"), options.SqlitePath);
        Assert.Null(options.RetentionMaxAgeDays);
        Assert.Null(options.RetentionMaxTotalBytes);
        Assert.Equal(configurationPath, options.ConfigurationPath);
    }

    [Fact]
    public void Valid_full_configuration_loads_optional_fields()
    {
        var configuration = ValidConfiguration();
        configuration.Ingest["maxRequestBytes"] = 4 * 1024 * 1024;
        configuration.Operator["bindAddress"] = "10.0.0.6";
        configuration.Operator["httpsCertificatePath"] = Path.Combine(_root, "operator.pem");
        configuration.Operator["httpsCertificateKeyPath"] = Path.Combine(_root, "operator.key");
        configuration.Ingest["clientCaBundlePaths"] =
            new JsonArray(
                Path.Combine(_root, "ca-old.pem"),
                Path.Combine(_root, "ca-new.pem"));
        configuration.Storage["retention"] = new JsonObject
        {
            ["maxAgeDays"] = 30,
            ["maxTotalBytes"] = 1_073_741_824L,
        };

        var options = SiemReceiverConfigurationLoader.Load(
            WriteConfiguration(configuration));

        Assert.Equal(4 * 1024 * 1024, options.MaxRequestBytes);
        Assert.Equal(IPAddress.Parse("10.0.0.6"), options.OperatorBindAddress);
        Assert.Equal(
            Path.Combine(_root, "operator.pem"),
            options.OperatorHttpsCertificatePath);
        Assert.Equal(
            Path.Combine(_root, "operator.key"),
            options.OperatorHttpsCertificateKeyPath);
        Assert.Equal(
            [Path.Combine(_root, "ca-old.pem"), Path.Combine(_root, "ca-new.pem")],
            options.ClientCaBundlePaths);
        Assert.Equal(30, options.RetentionMaxAgeDays);
        Assert.Equal(1_073_741_824L, options.RetentionMaxTotalBytes);
    }

    [Fact]
    public void Retention_with_single_bound_loads()
    {
        var configuration = ValidConfiguration();
        configuration.Storage["retention"] = new JsonObject { ["maxAgeDays"] = 7 };

        var options = SiemReceiverConfigurationLoader.Load(
            WriteConfiguration(configuration));

        Assert.Equal(7, options.RetentionMaxAgeDays);
        Assert.Null(options.RetentionMaxTotalBytes);
    }

    [Fact]
    public void Loopback_operator_bind_without_https_certificate_loads()
    {
        var configuration = ValidConfiguration();
        configuration.Operator["bindAddress"] = "127.0.0.1";

        var options = SiemReceiverConfigurationLoader.Load(
            WriteConfiguration(configuration));

        Assert.Equal(IPAddress.Loopback, options.OperatorBindAddress);
    }

    // ---- path / bytes / encoding ----------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/config.json")]
    public void Non_fully_qualified_configuration_path_is_rejected(string path)
    {
        var exception = Assert.Throws<SiemReceiverConfigurationException>(
            () => SiemReceiverConfigurationLoader.Load(path));
        Assert.Equal("config_path", exception.FailureCode);
    }

    [Fact]
    public void Missing_configuration_file_is_rejected()
    {
        var path = Path.Combine(_root, "does-not-exist.json");
        Assert.Equal("config_read", FailureCodeOf(path));
    }

    [Fact]
    public void Insecure_configuration_is_rejected_before_parse_without_repair()
    {
        var path = WriteConfiguration("{ malformed-before-protection");
        Broaden(path, isDirectory: false);
        var before = ProtectionSnapshot(path, isDirectory: false);

        Assert.Equal("config_protection", FailureCodeOf(path));
        Assert.Equal(before, ProtectionSnapshot(path, isDirectory: false));
        _ = SiemProtectedPath.ProtectCreatedFile(path);
    }

    [Fact]
    public void Insecure_configuration_parent_is_rejected_before_parse_without_repair()
    {
        var path = WriteConfiguration("{ malformed-before-parent-protection");
        Broaden(_root, isDirectory: true);
        var before = ProtectionSnapshot(_root, isDirectory: true);

        Assert.Equal("config_protection", FailureCodeOf(path));
        Assert.Equal(before, ProtectionSnapshot(_root, isDirectory: true));
        _ = SiemProtectedPath.ProtectCreatedDirectory(_root);
    }

    [Fact]
    public void Wrong_kind_configuration_is_rejected_as_protection_failure()
    {
        var path = Path.Combine(_root, "config-directory");
        Directory.CreateDirectory(path);
        _ = SiemProtectedPath.ProtectCreatedDirectory(path);

        Assert.Equal("config_protection", FailureCodeOf(path));
    }

    [Fact]
    public void Linked_configuration_is_rejected_before_parse()
    {
        var target = WriteConfiguration(ValidConfiguration());
        var linked = Path.Combine(_root, "linked-config.json");
        File.CreateSymbolicLink(linked, target);

        Assert.Equal("config_protection", FailureCodeOf(linked));
    }

    [Fact]
    public void Configuration_ancestor_redirect_is_rejected_before_parse()
    {
        var target = WriteConfiguration(ValidConfiguration());
        var linkRoot = SiemTestFileSystem.CreateProtectedRoot("ptk-siem-config-link");
        try
        {
            var redirect = Path.Combine(linkRoot, "redirect");
            Directory.CreateSymbolicLink(redirect, _root);
            var linked = Path.Combine(redirect, Path.GetFileName(target));

            Assert.Equal("config_protection", FailureCodeOf(linked));
        }
        finally
        {
            Directory.Delete(linkRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configuration_file_and_parent_have_independent_wrong_owner_guards(bool parent)
    {
        var path = WriteConfiguration(ValidConfiguration());
        var target = parent ? _root : path;
        var hooks = WrongOwnerHooks(target, parent);

        var exception = Assert.Throws<SiemReceiverConfigurationException>(() =>
            SiemReceiverConfigurationLoader.Load(path, hooks));

        Assert.Equal("config_protection", exception.FailureCode);
    }

    [Fact]
    public void Configuration_parent_replacement_barrier_rejects_namespace_swap()
    {
        var originalParent = Path.Combine(_root, "original-parent");
        var replacementParent = Path.Combine(_root, "replacement-parent");
        Directory.CreateDirectory(originalParent);
        Directory.CreateDirectory(replacementParent);
        _ = SiemProtectedPath.ProtectCreatedDirectory(originalParent);
        _ = SiemProtectedPath.ProtectCreatedDirectory(replacementParent);
        var original = SiemTestFileSystem.WriteProtectedText(
            originalParent,
            "config.json",
            ValidConfiguration().Node.ToJsonString());
        _ = SiemTestFileSystem.WriteProtectedText(
            replacementParent,
            "config.json",
            "{ replacement must never parse");
        var displaced = Path.Combine(_root, "displaced-parent");

        var exception = Assert.Throws<SiemReceiverConfigurationException>(() =>
            SiemReceiverConfigurationLoader.Load(
                original,
                new ProtectedPathTestHooks(AfterInitialDirectoryIdentity: _ =>
                {
                    Directory.Move(originalParent, displaced);
                    Directory.Move(replacementParent, originalParent);
                })));

        Assert.Equal("config_protection", exception.FailureCode);
    }

    [Fact]
    public void Configuration_file_cannot_alias_mutable_sqlite_storage()
    {
        var path = Path.Combine(_root, "self-alias.db");
        var configuration = ValidConfiguration();
        configuration.Storage["sqlitePath"] = path;
        File.WriteAllText(path, configuration.Node.ToJsonString());
        _ = SiemProtectedPath.ProtectCreatedFile(path);
        var options = SiemReceiverConfigurationLoader.Load(path);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                options,
                committer: new RecordingIngestCommitter()));

        Assert.Equal("protected_path_collision", exception.FailureCode);
    }

    [Fact]
    public void Configuration_replacement_barrier_never_accepts_replacement_token()
    {
        var originalConfiguration = ValidConfiguration();
        originalConfiguration.Operator["token"] = "original-token";
        var replacementConfiguration = ValidConfiguration();
        replacementConfiguration.Operator["token"] = "replacement-token";
        var original = WriteConfiguration(originalConfiguration);
        var replacement = WriteConfiguration(replacementConfiguration);
        SiemReceiverOptions? observed = null;

        var error = Record.Exception(() =>
        {
            observed = SiemReceiverConfigurationLoader.Load(
                original,
                new ProtectedPathTestHooks(AfterInitialFileIdentity: _ =>
                    File.Move(replacement, original, overwrite: true)));
        });

        if (error is null)
            Assert.Equal("original-token", observed!.OperatorToken);
        else
            Assert.Equal(
                "config_protection",
                Assert.IsType<SiemReceiverConfigurationException>(error).FailureCode);
        Assert.NotEqual("replacement-token", observed?.OperatorToken);
    }

    [Theory]
    [InlineData("serverCertificatePath", "server_certificate_path")]
    [InlineData("serverCertificateKeyPath", "server_certificate_key_path")]
    [InlineData("clientCaBundlePaths", "client_ca_bundle")]
    [InlineData("operatorHttpsCertificatePath", "operator_https_pair")]
    [InlineData("operatorHttpsCertificateKeyPath", "operator_https_pair")]
    [InlineData("sqlitePath", "storage_sqlite_path")]
    public void Every_configured_filesystem_path_must_be_absolute(
        string role,
        string expectedFailureCode)
    {
        var configuration = ValidConfiguration();
        switch (role)
        {
            case "serverCertificatePath":
            case "serverCertificateKeyPath":
                configuration.Ingest[role] = "relative.pem";
                break;
            case "clientCaBundlePaths":
                configuration.Ingest[role] = new JsonArray("relative-ca.pem");
                break;
            case "operatorHttpsCertificatePath":
                configuration.Operator["httpsCertificatePath"] = "relative-operator.pem";
                configuration.Operator["httpsCertificateKeyPath"] =
                    Path.Combine(_root, "operator.key");
                break;
            case "operatorHttpsCertificateKeyPath":
                configuration.Operator["httpsCertificatePath"] =
                    Path.Combine(_root, "operator.pem");
                configuration.Operator["httpsCertificateKeyPath"] = "relative-operator.key";
                break;
            case "sqlitePath":
                configuration.Storage[role] = "relative.db";
                break;
            default:
                throw new InvalidOperationException(role);
        }

        Assert.Equal(
            expectedFailureCode,
            FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Oversized_configuration_is_rejected()
    {
        var path = WriteConfiguration(new string(
            'x',
            SiemReceiverConfigurationLoader.MaximumConfigurationBytes + 1));
        Assert.Equal("config_bytes", FailureCodeOf(path));
    }

    [Fact]
    public void Empty_configuration_is_rejected()
    {
        Assert.Equal("config_json", FailureCodeOf(WriteConfiguration(string.Empty)));
    }

    [Fact]
    public void Utf8_bom_is_rejected()
    {
        var path = Path.Combine(_root, "bom.json");
        var body = Encoding.UTF8.GetBytes(ValidConfiguration().Node.ToJsonString());
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. body]);
        _ = SiemProtectedPath.ProtectCreatedFile(path);
        Assert.Equal("config_json", FailureCodeOf(path));
    }

    [Fact]
    public void Invalid_utf8_is_rejected()
    {
        var path = Path.Combine(_root, "invalid-utf8.json");
        File.WriteAllBytes(path, [0x7B, 0xFF, 0xFE, 0x7D]);
        _ = SiemProtectedPath.ProtectCreatedFile(path);
        Assert.Equal("config_json", FailureCodeOf(path));
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        Assert.Equal("config_json", FailureCodeOf(WriteConfiguration("{ not json")));
    }

    [Fact]
    public void Non_object_root_is_rejected()
    {
        Assert.Equal("config_root", FailureCodeOf(WriteConfiguration("[]")));
    }

    // ---- strict unknown-property rejection -------------------------------

    [Fact]
    public void Unknown_root_property_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Node["extra"] = 1;
        Assert.Equal("unknown_property", FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Theory]
    [InlineData("ingest")]
    [InlineData("operator")]
    [InlineData("storage")]
    public void Unknown_section_property_is_rejected(string section)
    {
        var configuration = ValidConfiguration();
        ((JsonObject)configuration.Node[section]!)["extra"] = 1;
        Assert.Equal("unknown_property", FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Unknown_retention_property_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Storage["retention"] = new JsonObject
        {
            ["maxAgeDays"] = 7,
            ["extra"] = 1,
        };
        Assert.Equal("unknown_property", FailureCodeOf(WriteConfiguration(configuration)));
    }

    // ---- section presence -------------------------------------------------

    [Theory]
    [InlineData("ingest", "ingest_section")]
    [InlineData("operator", "operator_section")]
    [InlineData("storage", "storage_section")]
    public void Missing_section_is_rejected(string section, string failureCode)
    {
        var configuration = ValidConfiguration();
        configuration.Node.Remove(section);
        Assert.Equal(failureCode, FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Non_object_section_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Node["ingest"] = 5;
        Assert.Equal("ingest_section", FailureCodeOf(WriteConfiguration(configuration)));
    }

    // ---- ingest fields ----------------------------------------------------

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.5:4318")]
    [InlineData("")]
    public void Invalid_ingest_bind_address_is_rejected(string bindAddress)
    {
        var configuration = ValidConfiguration();
        configuration.Ingest["bindAddress"] = bindAddress;
        Assert.Equal(
            "ingest_bind_address",
            FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Missing_ingest_bind_address_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Ingest.Remove("bindAddress");
        Assert.Equal(
            "ingest_bind_address",
            FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Out_of_range_ingest_port_is_rejected(int port)
    {
        var configuration = ValidConfiguration();
        configuration.Ingest["port"] = port;
        Assert.Equal("ingest_port", FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Non_numeric_ingest_port_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Ingest["port"] = "4318";
        Assert.Equal("ingest_port", FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Theory]
    [InlineData("serverCertificatePath", "server_certificate_path")]
    [InlineData("serverCertificateKeyPath", "server_certificate_key_path")]
    public void Missing_or_empty_server_certificate_material_is_rejected(
        string property, string failureCode)
    {
        var missing = ValidConfiguration();
        missing.Ingest.Remove(property);
        Assert.Equal(failureCode, FailureCodeOf(WriteConfiguration(missing)));

        var empty = ValidConfiguration();
        empty.Ingest[property] = "  ";
        Assert.Equal(failureCode, FailureCodeOf(WriteConfiguration(empty)));
    }

    [Fact]
    public void Missing_client_ca_bundle_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Ingest.Remove("clientCaBundlePaths");
        Assert.Equal("client_ca_bundle", FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Empty_client_ca_bundle_array_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Ingest["clientCaBundlePaths"] = new JsonArray();
        Assert.Equal("client_ca_bundle", FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Blank_client_ca_bundle_entry_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Ingest["clientCaBundlePaths"] = new JsonArray("/etc/ca.pem", " ");
        Assert.Equal("client_ca_bundle", FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Missing_revocation_check_mode_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Ingest.Remove("revocationCheckMode");
        Assert.Equal(
            "revocation_check_mode",
            FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("ONLINE")]
    [InlineData("Everything")]
    public void Unrecognized_revocation_check_mode_is_rejected(string mode)
    {
        var configuration = ValidConfiguration();
        configuration.Ingest["revocationCheckMode"] = mode;
        Assert.Equal(
            "revocation_check_mode",
            FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_max_request_bytes_is_rejected(int value)
    {
        var configuration = ValidConfiguration();
        configuration.Ingest["maxRequestBytes"] = value;
        Assert.Equal(
            "max_request_bytes",
            FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Fractional_max_request_bytes_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Ingest["maxRequestBytes"] = 1.5;
        Assert.Equal(
            "max_request_bytes",
            FailureCodeOf(WriteConfiguration(configuration)));
    }

    // ---- operator fields --------------------------------------------------

    [Fact]
    public void Missing_operator_token_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Operator.Remove("token");
        Assert.Equal("operator_token", FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Whitespace_operator_token_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Operator["token"] = "   ";
        Assert.Equal("operator_token", FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Theory]
    [InlineData("httpsCertificatePath")]
    [InlineData("httpsCertificateKeyPath")]
    public void Half_configured_operator_https_pair_is_rejected(string onlyProperty)
    {
        var configuration = ValidConfiguration();
        configuration.Operator[onlyProperty] = "/etc/ptk-siem/operator.pem";
        Assert.Equal(
            "operator_https_pair",
            FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Non_loopback_operator_bind_without_https_is_a_startup_failure()
    {
        var configuration = ValidConfiguration();
        configuration.Operator["bindAddress"] = "10.0.0.6";
        Assert.Equal(
            "operator_https_required",
            FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Operator_port_equal_to_ingest_port_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Operator["port"] = 4318;
        Assert.Equal(
            "operator_port_conflict",
            FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Missing_operator_port_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Operator.Remove("port");
        Assert.Equal("operator_port", FailureCodeOf(WriteConfiguration(configuration)));
    }

    // ---- storage / retention ----------------------------------------------

    [Fact]
    public void Missing_sqlite_path_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Storage.Remove("sqlitePath");
        Assert.Equal(
            "storage_sqlite_path",
            FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Retention_without_any_bound_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Storage["retention"] = new JsonObject();
        Assert.Equal("retention_bounds", FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Non_positive_retention_max_age_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Storage["retention"] = new JsonObject { ["maxAgeDays"] = 0 };
        Assert.Equal(
            "retention_max_age_days",
            FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Non_positive_retention_max_total_bytes_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Storage["retention"] = new JsonObject { ["maxTotalBytes"] = -1 };
        Assert.Equal(
            "retention_max_total_bytes",
            FailureCodeOf(WriteConfiguration(configuration)));
    }

    [Fact]
    public void Non_object_retention_is_rejected()
    {
        var configuration = ValidConfiguration();
        configuration.Storage["retention"] = 30;
        Assert.Equal(
            "retention_section",
            FailureCodeOf(WriteConfiguration(configuration)));
    }

    // ---- helpers ------------------------------------------------------------

    private ConfigurationUnderTest ValidConfiguration() => new(new JsonObject
    {
        ["ingest"] = new JsonObject
        {
            ["bindAddress"] = "10.0.0.5",
            ["port"] = 4318,
            ["serverCertificatePath"] = Path.Combine(_root, "server.pem"),
            ["serverCertificateKeyPath"] = Path.Combine(_root, "server.key"),
            ["clientCaBundlePaths"] = new JsonArray(Path.Combine(_root, "client-ca.pem")),
            ["revocationCheckMode"] = "Offline",
        },
        ["operator"] = new JsonObject
        {
            ["port"] = 9443,
            ["token"] = "operator-token-for-tests",
        },
        ["storage"] = new JsonObject
        {
            ["sqlitePath"] = Path.Combine(_root, "events.db"),
        },
    });

    private string WriteConfiguration(ConfigurationUnderTest configuration) =>
        WriteConfiguration(configuration.Node.ToJsonString());

    private string WriteConfiguration(string text)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("n") + ".json");
        File.WriteAllText(path, text);
        _ = SiemProtectedPath.ProtectCreatedFile(path);
        return path;
    }

    private static string FailureCodeOf(string path)
    {
        var exception = Assert.Throws<SiemReceiverConfigurationException>(
            () => SiemReceiverConfigurationLoader.Load(path));
        return exception.FailureCode;
    }

    private static ProtectedPathTestHooks WrongOwnerHooks(string target, bool directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            var effective = UnixProtectedPathNative.EffectiveUserId;
            var different = effective == uint.MaxValue ? effective - 1 : effective + 1;
            return new ProtectedPathTestHooks(
                ExpectedUnixUserIdForPath: (candidate, candidateIsDirectory) =>
                    candidateIsDirectory == directory && PathsEqual(candidate, target)
                        ? different
                        : null);
        }

        var foreignSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        return new ProtectedPathTestHooks(
            ExpectedWindowsOwnerSidForPath: (candidate, candidateIsDirectory) =>
                candidateIsDirectory == directory && PathsEqual(candidate, target)
                    ? foreignSid
                    : null);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static void Broaden(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                isDirectory
                    ? SiemProtectedPath.OwnerDirectoryMode |
                      UnixFileMode.GroupRead |
                      UnixFileMode.GroupExecute
                    : SiemProtectedPath.OwnerFileMode | UnixFileMode.GroupRead);
            return;
        }

        AddWindowsWorldRead(path, isDirectory);
    }

    private static string ProtectionSnapshot(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return File.GetUnixFileMode(path).ToString();
        return SnapshotWindowsAcl(path, isDirectory);
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsWorldRead(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read,
            AccessControlType.Allow));
        if (isDirectory)
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), (DirectorySecurity)security);
        else
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), (FileSecurity)security);
    }

    [SupportedOSPlatform("windows")]
    private static string SnapshotWindowsAcl(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        return security.GetSecurityDescriptorSddlForm(
            AccessControlSections.Owner | AccessControlSections.Access);
    }

    private sealed record ConfigurationUnderTest(JsonObject Node)
    {
        internal JsonObject Ingest => (JsonObject)Node["ingest"]!;

        internal JsonObject Operator => (JsonObject)Node["operator"]!;

        internal JsonObject Storage => (JsonObject)Node["storage"]!;
    }
}
