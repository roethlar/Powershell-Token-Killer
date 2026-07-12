using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditExportConfigurationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* Preserve the assertion failure that prevented cleanup. */ }
        }
    }

    [Fact]
    public void Header_authenticated_configuration_is_frozen_without_secret_diagnostics()
    {
        var root = NewRoot();
        var auditRoot = Path.Combine(root, "audit");
        const string canary = "Bearer PTK-SECRET-CANARY";
        var configPath = WriteConfiguration(
            root,
            Json(headers: new Dictionary<string, string> { ["Authorization"] = canary }));

        using var options = AuditExportConfigurationLoader.Load(configPath, auditRoot, Now);

        Assert.Equal("https://collector.example:4318/v1/logs", options.EndpointText);
        Assert.Equal(options.EndpointText, options.Endpoint.OriginalString);
        var header = Assert.Single(options.Headers);
        Assert.Equal("Authorization", header.Name);
        Assert.Equal(canary, header.Value);
        Assert.Matches("^[0-9a-f]{64}$", options.ConfigurationIdentity);
        Assert.DoesNotContain(canary, options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(canary, header.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(options.EndpointText, options.ToString(), StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<AuditExportHeader>>(options.Headers)
                .Add(new AuditExportHeader("X-Late", "mutation")));
        Assert.Equal(AuditExportOptions.ProtocolName, "http/protobuf");
        Assert.Equal(TimeSpan.FromSeconds(10), AuditExportOptions.RequestTimeout);

        var auditOptions = AuditOptions.Create(
            auditRoot,
            AuditProtectionMode.Anchored,
            options.ConfigurationIdentity);
        Assert.Equal(options.ConfigurationIdentity, auditOptions.ExportConfigurationIdentity);
    }

    [Fact]
    public void Bare_https_origin_is_used_verbatim_and_never_gets_a_signal_path_appended()
    {
        var root = NewRoot();
        var configPath = WriteConfiguration(root, Json(endpoint: "https://collector.example:4318"));

        using var options = AuditExportConfigurationLoader.Load(
            configPath,
            Path.Combine(root, "audit"),
            Now);

        Assert.Equal("https://collector.example:4318", options.EndpointText);
        Assert.Equal("/", options.Endpoint.AbsolutePath);
    }

    [Theory]
    [InlineData("http://collector.example/v1/logs")]
    [InlineData("https://collector.example/v1/logs?tenant=secret")]
    [InlineData("https://collector.example/v1/logs#fragment")]
    [InlineData("https://user@collector.example/v1/logs")]
    [InlineData("https://collector.example\\v1\\logs")]
    [InlineData("https://collector.example/a/../b")]
    [InlineData("https://collector.example/a/%2e%2e/b")]
    [InlineData("https://collector.example/a/./b")]
    [InlineData("https://collector.example/%7euser")]
    [InlineData(" https://collector.example/v1/logs")]
    [InlineData("https://collector.example/v1/logs ")]
    [InlineData("")]
    public void Non_anchor_endpoints_are_rejected_without_creating_identity_state(string endpoint)
    {
        var root = NewRoot();
        var auditRoot = Path.Combine(root, "audit");
        var configPath = WriteConfiguration(root, Json(endpoint: endpoint));

        var error = Assert.Throws<AuditExportConfigurationException>(() =>
            AuditExportConfigurationLoader.Load(configPath, auditRoot, Now));

        Assert.Equal("endpoint", error.FailureCode);
        Assert.False(File.Exists(Path.Combine(auditRoot, ExportConfigurationKeyStore.FileName)));
    }

    [Fact]
    public void Strict_root_schema_rejects_unknown_duplicate_wrong_case_missing_comments_and_trailing_data()
    {
        var valid = Json();
        var cases = new[]
        {
            valid[..^1] + ",\"future\":true}",
            valid.Replace("\"endpoint\":", "\"Endpoint\":", StringComparison.Ordinal),
            valid[..^1] + ",\"endpoint\":\"https://duplicate.example\"}",
            valid.Replace("\"ca_file\":null,", string.Empty, StringComparison.Ordinal),
            valid[..^1] + ",}",
            "/*not allowed*/" + valid,
            valid + "{}",
            valid.Replace("ptk.export-config/1", "ptk.export-config/2", StringComparison.Ordinal),
            valid.Replace("\"anchored\"", "\"local-only\"", StringComparison.Ordinal),
        };

        foreach (var json in cases)
        {
            var root = NewRoot();
            var configPath = WriteConfiguration(root, json);
            var error = Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(configPath, Path.Combine(root, "audit"), Now));
            Assert.StartsWith("audit_export_configuration_invalid: ", error.Message, StringComparison.Ordinal);
            Assert.Null(error.InnerException);
        }
    }

    [Fact]
    public void Json_value_kinds_are_exact()
    {
        var valid = Json();
        var cases = new (string Json, string Failure)[]
        {
            ("[]", "config_json"),
            (valid.Replace(
                "\"headers\":{\"Authorization\":\"Bearer alpha\"}",
                "\"headers\":[]",
                StringComparison.Ordinal), "headers_kind"),
            (valid.Replace(
                "\"Authorization\":\"Bearer alpha\"",
                "\"Authorization\":42",
                StringComparison.Ordinal), "header_value_kind"),
            (valid.Replace("\"ca_file\":null", "\"ca_file\":42", StringComparison.Ordinal), "config_value_kind"),
        };

        foreach (var item in cases)
        {
            var root = NewRoot();
            var configPath = WriteConfiguration(root, item.Json);
            Assert.Equal(
                item.Failure,
                Assert.Throws<AuditExportConfigurationException>(() =>
                    AuditExportConfigurationLoader.Load(
                        configPath,
                        Path.Combine(root, "audit"),
                        Now)).FailureCode);
        }
    }

    [Fact]
    public void Invalid_json_surrogate_escapes_are_rejected_before_identity_computation()
    {
        var valid = Json();
        var cases = new[]
        {
            valid.Replace("v1/logs", "v1/\\uD800", StringComparison.Ordinal),
            valid.Replace("Bearer alpha", "Bearer \\uD801", StringComparison.Ordinal),
        };
        foreach (var json in cases)
        {
            var root = NewRoot();
            var configPath = WriteConfiguration(root, json);
            var auditRoot = Path.Combine(root, "audit");
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(configPath, auditRoot, Now));
            Assert.False(Directory.Exists(auditRoot));
            Assert.False(File.Exists(Path.Combine(auditRoot, ExportConfigurationKeyStore.FileName)));
        }
    }

    [Fact]
    public void Configuration_path_must_be_absolute_present_and_protected()
    {
        var root = NewRoot();
        Assert.Equal(
            "config_path",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load("relative.json", Path.Combine(root, "audit-relative"), Now)).FailureCode);
        Assert.Equal(
            "load_failed",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(
                    Path.Combine(root, "missing.json"),
                    Path.Combine(root, "audit-missing"),
                    Now)).FailureCode);
    }

    [Fact]
    public void Utf8_bom_and_oversized_configuration_are_rejected()
    {
        var root = NewRoot();
        var bomPath = Path.Combine(root, "bom.json");
        var jsonBytes = Encoding.UTF8.GetBytes(Json());
        var withBom = new byte[jsonBytes.Length + 3];
        new byte[] { 0xef, 0xbb, 0xbf }.CopyTo(withBom, 0);
        jsonBytes.CopyTo(withBom, 3);
        WriteProtected(root, Path.GetFileName(bomPath), withBom);
        Assert.Equal(
            "config_json",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(bomPath, Path.Combine(root, "audit-bom"), Now)).FailureCode);

        var oversizedPath = Path.Combine(root, "oversized.json");
        WriteProtected(
            root,
            Path.GetFileName(oversizedPath),
            new byte[AuditExportConfigurationLoader.MaximumConfigurationBytes + 1]);
        Assert.Equal(
            "load_failed",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(
                    oversizedPath,
                    Path.Combine(root, "audit-oversized"),
                    Now)).FailureCode);
        CryptographicOperations.ZeroMemory(jsonBytes);
        CryptographicOperations.ZeroMemory(withBom);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad header")]
    [InlineData("NönAscii")]
    [InlineData("Host")]
    [InlineData("Content-Type")]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Connection")]
    [InlineData("User-Agent")]
    [InlineData("Content-Encoding")]
    [InlineData("Accept-Encoding")]
    public void Invalid_or_protocol_owned_header_names_are_rejected(string headerName)
    {
        var root = NewRoot();
        var configPath = WriteConfiguration(
            root,
            Json(headers: new Dictionary<string, string> { [headerName] = "secret" }));

        var error = Assert.Throws<AuditExportConfigurationException>(() =>
            AuditExportConfigurationLoader.Load(configPath, Path.Combine(root, "audit"), Now));

        Assert.Contains(error.FailureCode, new[] { "header_name", "header_forbidden" });
    }

    [Theory]
    [InlineData("")]
    [InlineData("secret\rforged")]
    [InlineData("secret\nforged")]
    [InlineData("secret\0forged")]
    public void Empty_or_injectable_header_values_are_rejected(string value)
    {
        var root = NewRoot();
        var configPath = WriteConfiguration(
            root,
            Json(headers: new Dictionary<string, string> { ["Authorization"] = value }));

        Assert.Equal(
            "header_value",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(configPath, Path.Combine(root, "audit"), Now)).FailureCode);
    }

    [Fact]
    public void Header_names_are_unique_case_insensitively_and_header_count_is_bounded()
    {
        var duplicateRoot = NewRoot();
        var duplicateJson = Json().Replace(
            "\"headers\":{\"Authorization\":\"Bearer alpha\"}",
            "\"headers\":{\"Authorization\":\"one\",\"authorization\":\"two\"}",
            StringComparison.Ordinal);
        var duplicatePath = WriteConfiguration(duplicateRoot, duplicateJson);
        Assert.Equal(
            "header_duplicate",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(
                    duplicatePath,
                    Path.Combine(duplicateRoot, "audit"),
                    Now)).FailureCode);

        var limitRoot = NewRoot();
        var headers = Enumerable.Range(0, AuditExportConfigurationLoader.MaximumHeaders + 1)
            .ToDictionary(index => $"X-Key-{index}", index => $"value-{index}", StringComparer.Ordinal);
        var limitPath = WriteConfiguration(limitRoot, Json(headers: headers));
        Assert.Equal(
            "headers_limit",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(limitPath, Path.Combine(limitRoot, "audit"), Now)).FailureCode);
    }

    [Fact]
    public void Endpoint_and_header_value_utf8_bounds_are_enforced()
    {
        var endpointRoot = NewRoot();
        var endpointPath = WriteConfiguration(
            endpointRoot,
            Json(endpoint: "https://collector.example/" + new string('a', AuditExportConfigurationLoader.MaximumEndpointBytes)));
        Assert.Equal(
            "endpoint",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(
                    endpointPath,
                    Path.Combine(endpointRoot, "audit"),
                    Now)).FailureCode);

        var headerRoot = NewRoot();
        var headerPath = WriteConfiguration(
            headerRoot,
            Json(headers: new Dictionary<string, string>
            {
                ["Authorization"] = new string('x', AuditExportConfigurationLoader.MaximumHeaderValueBytes + 1),
            }));
        Assert.Equal(
            "header_value",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(
                    headerPath,
                    Path.Combine(headerRoot, "audit"),
                    Now)).FailureCode);
    }

    [Fact]
    public void Empty_headers_without_a_complete_mtls_pair_are_rejected()
    {
        var root = NewRoot();
        var emptyPath = WriteConfiguration(root, Json(headers: new Dictionary<string, string>()));
        Assert.Equal(
            "authentication_required",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(emptyPath, Path.Combine(root, "audit-empty"), Now)).FailureCode);

        var certificateOnlyPath = WriteConfiguration(
            root,
            Json(
                headers: new Dictionary<string, string>(),
                clientCertificateFile: Path.Combine(root, "client.pem")));
        Assert.Equal(
            "client_pair",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(
                    certificateOnlyPath,
                    Path.Combine(root, "audit-pair"),
                    Now)).FailureCode);
    }

    [Fact]
    public void Custom_ca_bundle_must_be_valid_ca_pem_and_is_loaded_once()
    {
        var root = NewRoot();
        using var ca = CreateCertificate(isCa: true, ekuOid: null, Now.AddDays(-1), Now.AddDays(30));
        var caPath = WriteText(root, "ca.pem", ca.Certificate.ExportCertificatePem());
        var configPath = WriteConfiguration(root, Json(caFile: caPath));

        using var options = AuditExportConfigurationLoader.Load(
            configPath,
            Path.Combine(root, "audit"),
            Now);

        File.Delete(caPath);
        Assert.False(File.Exists(caPath));
        var loaded = Assert.Single(options.CustomCertificateAuthorities);
        Assert.Equal(ca.Certificate.Thumbprint, loaded.Thumbprint);

        var badRoot = NewRoot();
        var badCaPath = WriteText(badRoot, "bad-ca.pem", "not a certificate");
        var badConfig = WriteConfiguration(badRoot, Json(caFile: badCaPath));
        Assert.Equal(
            "ca_pem",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(badConfig, Path.Combine(badRoot, "audit"), Now)).FailureCode);

        using var leaf = CreateCertificate(isCa: false, ekuOid: null, Now.AddDays(-1), Now.AddDays(30));
        var leafRoot = NewRoot();
        var leafPath = WriteText(leafRoot, "leaf.pem", leaf.Certificate.ExportCertificatePem());
        var leafConfig = WriteConfiguration(leafRoot, Json(caFile: leafPath));
        Assert.Equal(
            "ca_not_authority",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(leafConfig, Path.Combine(leafRoot, "audit"), Now)).FailureCode);
    }

    [Fact]
    public void Matching_current_client_certificate_with_client_eku_supports_mtls_only_authentication()
    {
        var root = NewRoot();
        using var client = CreateCertificate(
            isCa: false,
            ekuOid: "1.3.6.1.5.5.7.3.2",
            Now.AddDays(-1),
            Now.AddDays(30));
        var certificatePath = WriteText(root, "client.pem", client.Certificate.ExportCertificatePem());
        var keyPath = WriteText(root, "client.key", client.PrivateKey.ExportPkcs8PrivateKeyPem());
        var configPath = WriteConfiguration(
            root,
            Json(
                headers: new Dictionary<string, string>(),
                clientCertificateFile: certificatePath,
                clientPrivateKeyFile: keyPath));

        using var options = AuditExportConfigurationLoader.Load(
            configPath,
            Path.Combine(root, "audit"),
            Now);

        Assert.Empty(options.Headers);
        Assert.NotNull(options.ClientCertificate);
        Assert.True(options.ClientCertificate.HasPrivateKey);
        Assert.Equal(client.Certificate.Thumbprint, options.ClientCertificate.Thumbprint);
    }

    [Fact]
    public async Task Windows_loaded_client_certificate_completes_a_schannel_mtls_handshake()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = NewRoot();
        var actualNow = DateTimeOffset.UtcNow;
        using var server = CreateCertificate(
            isCa: false,
            ekuOid: "1.3.6.1.5.5.7.3.1",
            actualNow.AddDays(-1),
            actualNow.AddDays(30));
        using var client = CreateCertificate(
            isCa: false,
            ekuOid: "1.3.6.1.5.5.7.3.2",
            actualNow.AddDays(-1),
            actualNow.AddDays(30));
        var certificatePath = WriteText(root, "schannel-client.pem", client.Certificate.ExportCertificatePem());
        var keyPath = WriteText(root, "schannel-client.key", client.PrivateKey.ExportPkcs8PrivateKeyPem());
        var configPath = WriteConfiguration(
            root,
            Json(
                headers: new Dictionary<string, string>(),
                clientCertificateFile: certificatePath,
                clientPrivateKeyFile: keyPath));
        using var options = AuditExportConfigurationLoader.Load(
            configPath,
            Path.Combine(root, "audit-schannel"),
            actualNow);
        var loadedClient = Assert.IsType<X509Certificate2>(options.ClientCertificate);
        using var schannelServer = ReloadForWindowsSchannel(server.Certificate);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = Assert.IsType<IPEndPoint>(listener.LocalEndpoint);
            var serverTask = AuthenticateServerAsync(
                listener,
                schannelServer,
                cancellation.Token);

            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(IPAddress.Loopback, endpoint.Port, cancellation.Token);
            await using var clientStream = new SslStream(tcpClient.GetStream(), leaveInnerStreamOpen: false);
            var clientTask = clientStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ClientCertificates = new X509CertificateCollection { loadedClient },
                    LocalCertificateSelectionCallback = (_, _, _, _, _) => loadedClient,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    EnabledSslProtocols = SslProtocols.Tls12,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                cancellation.Token);

            await Task.WhenAll(serverTask, clientTask);
            Assert.Equal(loadedClient.GetCertHashString(), await serverTask);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Mismatched_expired_future_and_server_only_client_certificates_fail_closed()
    {
        using var valid = CreateCertificate(
            isCa: false,
            ekuOid: "1.3.6.1.5.5.7.3.2",
            Now.AddDays(-1),
            Now.AddDays(30));
        using var other = CreateCertificate(
            isCa: false,
            ekuOid: "1.3.6.1.5.5.7.3.2",
            Now.AddDays(-1),
            Now.AddDays(30));
        AssertClientFailure(valid, other.PrivateKey, Now, "client_certificate_pem");

        using var expired = CreateCertificate(
            isCa: false,
            ekuOid: "1.3.6.1.5.5.7.3.2",
            Now.AddDays(-30),
            Now.AddDays(-1));
        AssertClientFailure(expired, expired.PrivateKey, Now, "client_certificate_time");

        using var future = CreateCertificate(
            isCa: false,
            ekuOid: "1.3.6.1.5.5.7.3.2",
            Now.AddDays(1),
            Now.AddDays(30));
        AssertClientFailure(future, future.PrivateKey, Now, "client_certificate_time");

        using var serverOnly = CreateCertificate(
            isCa: false,
            ekuOid: "1.3.6.1.5.5.7.3.1",
            Now.AddDays(-1),
            Now.AddDays(30));
        AssertClientFailure(serverOnly, serverOnly.PrivateKey, Now, "client_certificate_eku");
    }

    [Fact]
    public void Client_certificate_without_eku_is_unrestricted()
    {
        var root = NewRoot();
        using var client = CreateCertificate(
            isCa: false,
            ekuOid: null,
            Now.AddDays(-1),
            Now.AddDays(30));
        var certificatePath = WriteText(root, "client.pem", client.Certificate.ExportCertificatePem());
        var keyPath = WriteText(root, "client.key", client.PrivateKey.ExportPkcs8PrivateKeyPem());
        var configPath = WriteConfiguration(
            root,
            Json(clientCertificateFile: certificatePath, clientPrivateKeyFile: keyPath));

        using var options = AuditExportConfigurationLoader.Load(
            configPath,
            Path.Combine(root, "audit"),
            Now);

        Assert.NotNull(options.ClientCertificate);
    }

    [Fact]
    public void Client_certificate_with_any_eku_is_unrestricted()
    {
        var root = NewRoot();
        using var client = CreateCertificate(
            isCa: false,
            ekuOid: "2.5.29.37.0",
            Now.AddDays(-1),
            Now.AddDays(30));
        var certificatePath = WriteText(root, "client.pem", client.Certificate.ExportCertificatePem());
        var keyPath = WriteText(root, "client.key", client.PrivateKey.ExportPkcs8PrivateKeyPem());
        var configPath = WriteConfiguration(
            root,
            Json(
                headers: new Dictionary<string, string>(),
                clientCertificateFile: certificatePath,
                clientPrivateKeyFile: keyPath));

        using var options = AuditExportConfigurationLoader.Load(
            configPath,
            Path.Combine(root, "audit"),
            Now);
        Assert.NotNull(options.ClientCertificate);
    }

    [Fact]
    public void Encrypted_private_keys_are_not_accepted_by_v1()
    {
        var root = NewRoot();
        using var client = CreateCertificate(
            isCa: false,
            ekuOid: "1.3.6.1.5.5.7.3.2",
            Now.AddDays(-1),
            Now.AddDays(30));
        var certificatePath = WriteText(root, "client.pem", client.Certificate.ExportCertificatePem());
        var encrypted = client.PrivateKey.ExportEncryptedPkcs8PrivateKeyPem(
            "not-configured",
            new PbeParameters(
                PbeEncryptionAlgorithm.Aes256Cbc,
                HashAlgorithmName.SHA256,
                10_000));
        var keyPath = WriteText(root, "client.key", encrypted);
        var configPath = WriteConfiguration(
            root,
            Json(clientCertificateFile: certificatePath, clientPrivateKeyFile: keyPath));

        Assert.Equal(
            "client_certificate_pem",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(configPath, Path.Combine(root, "audit"), Now)).FailureCode);
    }

    [Fact]
    public void Configuration_and_assets_must_be_absolute_protected_nonlinked_files()
    {
        var root = NewRoot();
        var relativeAssetConfig = WriteConfiguration(root, Json(caFile: "relative-ca.pem"));
        Assert.Equal(
            "asset_path",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(
                    relativeAssetConfig,
                    Path.Combine(root, "audit-relative"),
                    Now)).FailureCode);

        if (OperatingSystem.IsWindows()) return;

        var modeRoot = NewRoot();
        var modeConfig = WriteConfiguration(modeRoot, Json());
        File.SetUnixFileMode(
            modeConfig,
            SecureAuditStorage.OwnerFileMode | UnixFileMode.GroupRead);
        try
        {
            Assert.Equal(
                "load_failed",
                Assert.Throws<AuditExportConfigurationException>(() =>
                    AuditExportConfigurationLoader.Load(
                        modeConfig,
                        Path.Combine(modeRoot, "audit-mode"),
                        Now)).FailureCode);
        }
        finally
        {
            File.SetUnixFileMode(modeConfig, SecureAuditStorage.OwnerFileMode);
        }

        var parentModeRoot = NewRoot();
        var parentModeConfig = WriteConfiguration(parentModeRoot, Json());
        File.SetUnixFileMode(
            parentModeRoot,
            SecureAuditStorage.OwnerDirectoryMode |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute);
        try
        {
            Assert.Equal(
                "load_failed",
                Assert.Throws<AuditExportConfigurationException>(() =>
                    AuditExportConfigurationLoader.Load(
                        parentModeConfig,
                        Path.Combine(parentModeRoot, "audit-parent-mode"),
                        Now)).FailureCode);
        }
        finally
        {
            File.SetUnixFileMode(parentModeRoot, SecureAuditStorage.OwnerDirectoryMode);
        }

        using var assetCa = CreateCertificate(
            isCa: true,
            ekuOid: null,
            Now.AddDays(-1),
            Now.AddDays(30));
        var assetModeRoot = NewRoot();
        var assetModePath = WriteText(
            assetModeRoot,
            "ca.pem",
            assetCa.Certificate.ExportCertificatePem());
        var assetConfigRoot = NewRoot();
        var assetModeConfig = WriteConfiguration(assetConfigRoot, Json(caFile: assetModePath));
        File.SetUnixFileMode(
            assetModePath,
            SecureAuditStorage.OwnerFileMode | UnixFileMode.GroupRead);
        try
        {
            Assert.Equal(
                "load_failed",
                Assert.Throws<AuditExportConfigurationException>(() =>
                    AuditExportConfigurationLoader.Load(
                        assetModeConfig,
                        Path.Combine(assetConfigRoot, "audit-asset-mode"),
                        Now)).FailureCode);
        }
        finally
        {
            File.SetUnixFileMode(assetModePath, SecureAuditStorage.OwnerFileMode);
        }

        File.SetUnixFileMode(
            assetModeRoot,
            SecureAuditStorage.OwnerDirectoryMode |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute);
        try
        {
            Assert.Equal(
                "load_failed",
                Assert.Throws<AuditExportConfigurationException>(() =>
                    AuditExportConfigurationLoader.Load(
                        assetModeConfig,
                        Path.Combine(assetConfigRoot, "audit-asset-parent-mode"),
                        Now)).FailureCode);
        }
        finally
        {
            File.SetUnixFileMode(assetModeRoot, SecureAuditStorage.OwnerDirectoryMode);
        }

        var assetLinkRoot = NewRoot();
        var assetLinkTarget = WriteText(
            assetLinkRoot,
            "target-ca.pem",
            assetCa.Certificate.ExportCertificatePem());
        var linkedAsset = Path.Combine(assetLinkRoot, "linked-ca.pem");
        File.CreateSymbolicLink(linkedAsset, assetLinkTarget);
        var assetLinkConfigRoot = NewRoot();
        var assetLinkConfig = WriteConfiguration(
            assetLinkConfigRoot,
            Json(caFile: linkedAsset));
        Assert.Equal(
            "load_failed",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(
                    assetLinkConfig,
                    Path.Combine(assetLinkConfigRoot, "audit-asset-link"),
                    Now)).FailureCode);

        var linkRoot = NewRoot();
        var target = WriteConfiguration(linkRoot, Json());
        var linked = Path.Combine(linkRoot, "linked.json");
        File.CreateSymbolicLink(linked, target);
        Assert.Equal(
            "load_failed",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(
                    linked,
                    Path.Combine(linkRoot, "audit-link"),
                    Now)).FailureCode);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void Windows_rejects_overbroad_external_acls_without_mutating_them(
        bool broadenParent,
        bool targetAsset,
        bool useForeignDenyAce)
    {
        if (!OperatingSystem.IsWindows()) return;

        var configRoot = NewRoot();
        using var ca = CreateCertificate(
            isCa: true,
            ekuOid: null,
            Now.AddDays(-1),
            Now.AddDays(30));
        string? assetRoot = null;
        string? assetPath = null;
        if (targetAsset)
        {
            assetRoot = NewRoot();
            assetPath = WriteText(assetRoot, "ca.pem", ca.Certificate.ExportCertificatePem());
        }
        var configPath = WriteConfiguration(configRoot, Json(caFile: assetPath));
        var target = targetAsset
            ? broadenParent ? assetRoot! : assetPath!
            : broadenParent ? configRoot : configPath;
        if (useForeignDenyAce)
            AddForeignDenyAccess(target, isDirectory: broadenParent);
        else
            AddWorldReadAccess(target, isDirectory: broadenParent);
        var before = SnapshotWindowsAcl(target, isDirectory: broadenParent);
        var auditRoot = Path.Combine(configRoot, "audit-overbroad");

        var error = Record.Exception(() =>
        {
            using var unexpected = AuditExportConfigurationLoader.Load(
                configPath,
                auditRoot,
                Now);
        });
        var after = SnapshotWindowsAcl(target, isDirectory: broadenParent);

        var configurationError = Assert.IsType<AuditExportConfigurationException>(error);
        Assert.Equal("load_failed", configurationError.FailureCode);
        Assert.Equal(before, after);
        Assert.False(Directory.Exists(auditRoot));
        Assert.False(File.Exists(Path.Combine(auditRoot, ExportConfigurationKeyStore.FileName)));
    }

    [Fact]
    public void Oversized_pem_assets_are_rejected_before_parsing()
    {
        var root = NewRoot();
        var assetPath = Path.Combine(root, "oversized-ca.pem");
        WriteProtected(
            root,
            Path.GetFileName(assetPath),
            new byte[AuditExportConfigurationLoader.MaximumAssetBytes + 1]);
        var configPath = WriteConfiguration(root, Json(caFile: assetPath));

        Assert.Equal(
            "load_failed",
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(configPath, Path.Combine(root, "audit"), Now)).FailureCode);
    }

    [Fact]
    public void Same_persistent_key_and_config_reuse_identity_while_effective_change_rotates_it()
    {
        var root = NewRoot();
        var auditRoot = Path.Combine(root, "audit");
        var firstPath = WriteConfiguration(root, Json(), "first.json");
        var changedPath = WriteConfiguration(
            root,
            Json(headers: new Dictionary<string, string> { ["Authorization"] = "Bearer changed" }),
            "changed.json");

        string firstIdentity;
        using (var first = AuditExportConfigurationLoader.Load(firstPath, auditRoot, Now))
            firstIdentity = first.ConfigurationIdentity;
        using var repeated = AuditExportConfigurationLoader.Load(firstPath, auditRoot, Now);
        using var changed = AuditExportConfigurationLoader.Load(changedPath, auditRoot, Now);

        Assert.Equal(firstIdentity, repeated.ConfigurationIdentity);
        Assert.NotEqual(firstIdentity, changed.ConfigurationIdentity);
        Assert.Equal(
            ExportConfigurationKeyStore.KeyBytes,
            new FileInfo(Path.Combine(auditRoot, ExportConfigurationKeyStore.FileName)).Length);
    }

    [Fact]
    public void Sanitized_failures_never_echo_secret_values_paths_or_inner_exceptions()
    {
        var root = NewRoot();
        const string canary = "PTK-SECRET-ERROR-CANARY";
        var configPath = WriteConfiguration(
            root,
            Json(headers: new Dictionary<string, string> { ["Authorization"] = canary + "\nforged" }));

        var error = Assert.Throws<AuditExportConfigurationException>(() =>
            AuditExportConfigurationLoader.Load(configPath, Path.Combine(root, "audit"), Now));

        Assert.DoesNotContain(canary, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(root, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);

        var missingAssetPath = Path.Combine(root, canary + "-missing-ca.pem");
        var missingAssetConfig = WriteConfiguration(
            root,
            Json(caFile: missingAssetPath),
            "missing-asset.json");
        var wrappedError = Assert.Throws<AuditExportConfigurationException>(() =>
            AuditExportConfigurationLoader.Load(
                missingAssetConfig,
                Path.Combine(root, "audit-missing-asset"),
                Now));
        Assert.Equal("load_failed", wrappedError.FailureCode);
        Assert.DoesNotContain(canary, wrappedError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(root, wrappedError.ToString(), StringComparison.Ordinal);
        Assert.Null(wrappedError.InnerException);
    }

    private void AssertClientFailure(
        CertificateFixture certificate,
        RSA privateKey,
        DateTimeOffset now,
        string failureCode)
    {
        var root = NewRoot();
        var certificatePath = WriteText(root, "client.pem", certificate.Certificate.ExportCertificatePem());
        var keyPath = WriteText(root, "client.key", privateKey.ExportPkcs8PrivateKeyPem());
        var configPath = WriteConfiguration(
            root,
            Json(clientCertificateFile: certificatePath, clientPrivateKeyFile: keyPath));
        Assert.Equal(
            failureCode,
            Assert.Throws<AuditExportConfigurationException>(() =>
                AuditExportConfigurationLoader.Load(configPath, Path.Combine(root, "audit"), now)).FailureCode);
    }

    private string NewRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "test-export-config-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return SecureAuditStorage.PrepareRoot(root);
    }

    private static string WriteConfiguration(
        string root,
        string json,
        string? name = null) =>
        WriteText(root, name ?? $"config-{Guid.NewGuid():N}.json", json);

    private static string WriteText(string root, string name, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            WriteProtected(root, name, bytes);
            return Path.Combine(root, name);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void WriteProtected(string root, string name, ReadOnlySpan<byte> bytes)
    {
        using var stream = SecureAuditStorage.CreateExclusiveFile(Path.Combine(root, name));
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static string Json(
        string endpoint = "https://collector.example:4318/v1/logs",
        IReadOnlyDictionary<string, string>? headers = null,
        string? caFile = null,
        string? clientCertificateFile = null,
        string? clientPrivateKeyFile = null)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema_version"] = "ptk.export-config/1",
            ["protection_mode"] = "anchored",
            ["endpoint"] = endpoint,
            ["headers"] = headers ?? new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer alpha",
            },
            ["ca_file"] = caFile,
            ["client_certificate_file"] = clientCertificateFile,
            ["client_private_key_file"] = clientPrivateKeyFile,
        };
        return JsonSerializer.Serialize(values);
    }

    private static CertificateFixture CreateCertificate(
        bool isCa,
        string? ekuOid,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN=ptk-test-{Guid.NewGuid():N}"),
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(isCa, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (isCa)
        {
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true));
        }
        if (ekuOid is not null)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(ekuOid) },
                true));
        }
        var certificate = request.CreateSelfSigned(notBefore, notAfter);
        return new CertificateFixture(certificate, key);
    }

    private static async Task<string> AuthenticateServerAsync(
        TcpListener listener,
        X509Certificate2 serverCertificate,
        CancellationToken cancellationToken)
    {
        using var tcpClient = await listener.AcceptTcpClientAsync(cancellationToken);
        string? clientHash = null;
        await using var stream = new SslStream(
            tcpClient.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) =>
            {
                clientHash = certificate?.GetCertHashString();
                return certificate is not null;
            });
        await stream.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            },
            cancellationToken);
        return Assert.IsType<string>(clientHash);
    }

    private static X509Certificate2 ReloadForWindowsSchannel(X509Certificate2 certificate)
    {
        var pkcs12 = certificate.Export(X509ContentType.Pkcs12, string.Empty);
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pkcs12,
                string.Empty,
                X509KeyStorageFlags.DefaultKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddWorldReadAccess(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            isDirectory ? FileSystemRights.ReadAndExecute : FileSystemRights.Read,
            AccessControlType.Allow));
        if (isDirectory)
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), (DirectorySecurity)security);
        else
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), (FileSecurity)security);
    }

    [SupportedOSPlatform("windows")]
    private static void AddForeignDenyAccess(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.Read,
            AccessControlType.Deny));
        if (isDirectory)
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), (DirectorySecurity)security);
        else
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), (FileSecurity)security);
    }

    [SupportedOSPlatform("windows")]
    private static WindowsAclSnapshot SnapshotWindowsAcl(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        var owner = Assert.IsType<SecurityIdentifier>(security.GetOwner(typeof(SecurityIdentifier)));
        return new WindowsAclSnapshot(
            owner.Value,
            security.AreAccessRulesProtected,
            security.GetSecurityDescriptorSddlForm(
                AccessControlSections.Owner | AccessControlSections.Access));
    }

    private sealed record WindowsAclSnapshot(string Owner, bool Protected, string Sddl);

    private sealed record CertificateFixture(X509Certificate2 Certificate, RSA PrivateKey) : IDisposable
    {
        public void Dispose()
        {
            Certificate.Dispose();
            PrivateKey.Dispose();
        }
    }
}
