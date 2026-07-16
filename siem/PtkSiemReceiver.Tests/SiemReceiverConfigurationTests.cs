using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using PtkSiemReceiver.Configuration;

namespace PtkSiemReceiver.Tests;

public sealed class SiemReceiverConfigurationTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("ptk-siem-config-").FullName;

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
        var options = SiemReceiverConfigurationLoader.Load(
            WriteConfiguration(ValidConfiguration()));

        Assert.Equal(IPAddress.Parse("10.0.0.5"), options.IngestBindAddress);
        Assert.Equal(4318, options.IngestPort);
        Assert.Equal("/etc/ptk-siem/server.pem", options.ServerCertificatePath);
        Assert.Equal("/etc/ptk-siem/server.key", options.ServerCertificateKeyPath);
        Assert.Equal(["/etc/ptk-siem/client-ca.pem"], options.ClientCaBundlePaths);
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
        Assert.Equal("/var/lib/ptk-siem/events.db", options.SqlitePath);
        Assert.Null(options.RetentionMaxAgeDays);
        Assert.Null(options.RetentionMaxTotalBytes);
    }

    [Fact]
    public void Valid_full_configuration_loads_optional_fields()
    {
        var configuration = ValidConfiguration();
        configuration.Ingest["maxRequestBytes"] = 4 * 1024 * 1024;
        configuration.Operator["bindAddress"] = "10.0.0.6";
        configuration.Operator["httpsCertificatePath"] = "/etc/ptk-siem/operator.pem";
        configuration.Operator["httpsCertificateKeyPath"] = "/etc/ptk-siem/operator.key";
        configuration.Ingest["clientCaBundlePaths"] =
            new JsonArray("/etc/ptk-siem/ca-old.pem", "/etc/ptk-siem/ca-new.pem");
        configuration.Storage["retention"] = new JsonObject
        {
            ["maxAgeDays"] = 30,
            ["maxTotalBytes"] = 1_073_741_824L,
        };

        var options = SiemReceiverConfigurationLoader.Load(
            WriteConfiguration(configuration));

        Assert.Equal(4 * 1024 * 1024, options.MaxRequestBytes);
        Assert.Equal(IPAddress.Parse("10.0.0.6"), options.OperatorBindAddress);
        Assert.Equal("/etc/ptk-siem/operator.pem", options.OperatorHttpsCertificatePath);
        Assert.Equal("/etc/ptk-siem/operator.key", options.OperatorHttpsCertificateKeyPath);
        Assert.Equal(
            ["/etc/ptk-siem/ca-old.pem", "/etc/ptk-siem/ca-new.pem"],
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
        Assert.Equal("config_json", FailureCodeOf(path));
    }

    [Fact]
    public void Invalid_utf8_is_rejected()
    {
        var path = Path.Combine(_root, "invalid-utf8.json");
        File.WriteAllBytes(path, [0x7B, 0xFF, 0xFE, 0x7D]);
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

    private static ConfigurationUnderTest ValidConfiguration() => new(new JsonObject
    {
        ["ingest"] = new JsonObject
        {
            ["bindAddress"] = "10.0.0.5",
            ["port"] = 4318,
            ["serverCertificatePath"] = "/etc/ptk-siem/server.pem",
            ["serverCertificateKeyPath"] = "/etc/ptk-siem/server.key",
            ["clientCaBundlePaths"] = new JsonArray("/etc/ptk-siem/client-ca.pem"),
            ["revocationCheckMode"] = "Offline",
        },
        ["operator"] = new JsonObject
        {
            ["port"] = 9443,
            ["token"] = "operator-token-for-tests",
        },
        ["storage"] = new JsonObject
        {
            ["sqlitePath"] = "/var/lib/ptk-siem/events.db",
        },
    });

    private string WriteConfiguration(ConfigurationUnderTest configuration) =>
        WriteConfiguration(configuration.Node.ToJsonString());

    private string WriteConfiguration(string text)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("n") + ".json");
        File.WriteAllText(path, text);
        return path;
    }

    private static string FailureCodeOf(string path)
    {
        var exception = Assert.Throws<SiemReceiverConfigurationException>(
            () => SiemReceiverConfigurationLoader.Load(path));
        return exception.FailureCode;
    }

    private sealed record ConfigurationUnderTest(JsonObject Node)
    {
        internal JsonObject Ingest => (JsonObject)Node["ingest"]!;

        internal JsonObject Operator => (JsonObject)Node["operator"]!;

        internal JsonObject Storage => (JsonObject)Node["storage"]!;
    }
}
