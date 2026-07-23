using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PtkSiemReceiver.Configuration;
using PtkSiemReceiver.Ingest;
using PtkSiemReceiver.Security;
using PtkSiemReceiver.Storage;

namespace PtkSiemReceiver.Tests;

[Collection(SiemReceiverProcessCollection.Name)]
public sealed class ReceiverStartupProtectionTests
{
    public static TheoryData<string, string> TlsRoles => new()
    {
        { "server_certificate", "tls_protection" },
        { "server_key", "tls_protection" },
        { "client_ca_one", "tls_protection" },
        { "client_ca_two", "tls_protection" },
        { "operator_certificate", "operator_https_material" },
        { "operator_key", "operator_https_material" },
    };

    [Theory]
    [InlineData("server_certificate", "tls_protection")]
    [InlineData("server_key", "tls_protection")]
    [InlineData("client_ca_one", "tls_protection")]
    [InlineData("client_ca_two", "tls_protection")]
    [InlineData("operator_certificate", "operator_https_material")]
    [InlineData("operator_key", "operator_https_material")]
    public void Every_configured_tls_role_rejects_permissive_protection_without_repair(
        string role,
        string expectedFailureCode)
    {
        using var files = new ReceiverFiles(includeOperatorPair: role.StartsWith("operator_"));
        var path = files.PathFor(role);
        if (!role.StartsWith("operator_", StringComparison.Ordinal))
            File.WriteAllText(path, "malformed PEM must lose to protection");
        Broaden(path);
        var before = ProtectionSnapshot(path);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                files.Options,
                committer: new RecordingIngestCommitter()));

        Assert.Equal(expectedFailureCode, exception.FailureCode);
        Assert.Equal(before, ProtectionSnapshot(path));
        _ = SiemProtectedPath.ProtectCreatedFile(path);
    }

    [Theory]
    [MemberData(nameof(TlsRoles))]
    public void Every_tls_parent_rejects_permissive_protection_without_repair(
        string role,
        string expectedFailureCode)
    {
        using var files = new ReceiverFiles(includeOperatorPair: role.StartsWith("operator_"));
        var parent = Path.GetDirectoryName(files.PathFor(role))!;
        Broaden(parent, isDirectory: true);
        var before = ProtectionSnapshot(parent, isDirectory: true);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                files.Options,
                committer: new RecordingIngestCommitter()));

        Assert.Equal(expectedFailureCode, exception.FailureCode);
        Assert.Equal(before, ProtectionSnapshot(parent, isDirectory: true));
        _ = SiemProtectedPath.ProtectCreatedDirectory(parent);
    }

    [Theory]
    [MemberData(nameof(TlsRoles))]
    public void Every_tls_role_rejects_missing_input(
        string role,
        string expectedFailureCode)
    {
        using var files = new ReceiverFiles(includeOperatorPair: role.StartsWith("operator_"));
        File.Delete(files.PathFor(role));

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                files.Options,
                committer: new RecordingIngestCommitter()));

        Assert.Equal(expectedFailureCode, exception.FailureCode);
    }

    [Theory]
    [MemberData(nameof(TlsRoles))]
    public void Every_tls_role_rejects_wrong_kind(
        string role,
        string expectedFailureCode)
    {
        using var files = new ReceiverFiles(includeOperatorPair: role.StartsWith("operator_"));
        var path = files.PathFor(role);
        File.Delete(path);
        Directory.CreateDirectory(path);
        _ = SiemProtectedPath.ProtectCreatedDirectory(path);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                files.Options,
                committer: new RecordingIngestCommitter()));

        Assert.Equal(expectedFailureCode, exception.FailureCode);
    }

    [Theory]
    [MemberData(nameof(TlsRoles))]
    public void Every_tls_role_rejects_oversized_input(
        string role,
        string expectedFailureCode)
    {
        using var files = new ReceiverFiles(includeOperatorPair: role.StartsWith("operator_"));
        File.WriteAllBytes(
            files.PathFor(role),
            new byte[ReceiverApplication.MaximumTlsMaterialBytes + 1]);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                files.Options,
                committer: new RecordingIngestCommitter()));

        Assert.Equal(expectedFailureCode, exception.FailureCode);
    }

    [Theory]
    [MemberData(nameof(TlsRoles))]
    public void Every_tls_role_rejects_empty_input(
        string role,
        string _)
    {
        using var files = new ReceiverFiles(includeOperatorPair: role.StartsWith("operator_"));
        File.WriteAllBytes(files.PathFor(role), []);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                files.Options,
                committer: new RecordingIngestCommitter()));

        Assert.Equal(role switch
        {
            "server_certificate" or "server_key" => "server_certificate",
            "client_ca_one" or "client_ca_two" => "client_ca_bundle",
            _ => "operator_https_material",
        }, exception.FailureCode);
    }

    [Theory]
    [MemberData(nameof(TlsRoles))]
    public void Every_tls_role_rejects_leaf_redirect(
        string role,
        string expectedFailureCode)
    {
        using var files = new ReceiverFiles(includeOperatorPair: role.StartsWith("operator_"));
        var path = files.PathFor(role);
        var contents = File.ReadAllBytes(path);
        var target = SiemTestFileSystem.WriteProtectedBytes(
            Path.GetDirectoryName(path)!,
            "redirect-target-" + Guid.NewGuid().ToString("N"),
            contents);
        File.Delete(path);
        File.CreateSymbolicLink(path, target);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                files.Options,
                committer: new RecordingIngestCommitter()));

        Assert.Equal(expectedFailureCode, exception.FailureCode);
    }

    [Theory]
    [MemberData(nameof(TlsRoles))]
    public void Every_tls_role_rejects_ancestor_redirect(
        string role,
        string expectedFailureCode)
    {
        using var files = new ReceiverFiles(includeOperatorPair: role.StartsWith("operator_"));
        var linkContainer = SiemTestFileSystem.CreateProtectedRoot("ptk-siem-tls-link");
        try
        {
            var redirect = Path.Combine(linkContainer, "redirect");
            Directory.CreateSymbolicLink(redirect, Path.GetDirectoryName(files.PathFor(role))!);
            var redirectedPath = Path.Combine(redirect, Path.GetFileName(files.PathFor(role)));

            var exception = Assert.Throws<SiemReceiverStartupException>(() =>
                ReceiverApplication.Build(
                    files.OptionsWithRolePath(role, redirectedPath),
                    committer: new RecordingIngestCommitter()));

            Assert.Equal(expectedFailureCode, exception.FailureCode);
        }
        finally
        {
            Directory.Delete(linkContainer, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(TlsRoles))]
    public void Every_tls_file_role_has_an_independent_wrong_owner_guard(
        string role,
        string expectedFailureCode)
    {
        using var files = new ReceiverFiles(includeOperatorPair: role.StartsWith("operator_"));
        var target = files.PathFor(role);
        var hooks = WrongOwnerHooks(target, directory: false);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                files.Options,
                committer: new RecordingIngestCommitter(),
                protectedPathTestHooks: hooks));

        Assert.Equal(expectedFailureCode, exception.FailureCode);
    }

    [Theory]
    [MemberData(nameof(TlsRoles))]
    public void Every_tls_parent_role_has_an_independent_wrong_owner_guard(
        string role,
        string expectedFailureCode)
    {
        using var files = new ReceiverFiles(includeOperatorPair: role.StartsWith("operator_"));
        var target = Path.GetDirectoryName(files.PathFor(role))!;
        var hooks = WrongOwnerHooks(target, directory: true);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                files.Options,
                committer: new RecordingIngestCommitter(),
                protectedPathTestHooks: hooks));

        Assert.Equal(expectedFailureCode, exception.FailureCode);
    }

    [Theory]
    [MemberData(nameof(TlsRoles))]
    public void Every_tls_role_rechecks_identity_after_retained_read(
        string role,
        string expectedFailureCode)
    {
        using var files = new ReceiverFiles(includeOperatorPair: role.StartsWith("operator_"));
        var target = files.PathFor(role);
        var replacement = SiemTestFileSystem.WriteProtectedText(
            Path.GetDirectoryName(target)!,
            "replacement-" + Guid.NewGuid().ToString("N"),
            "malformed replacement");
        var replaced = false;

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                files.Options,
                committer: new RecordingIngestCommitter(),
                protectedPathTestHooks: new ProtectedPathTestHooks(
                    AfterInitialFileIdentity: candidate =>
                    {
                        if (replaced || !PathsEqual(candidate, target)) return;
                        replaced = true;
                        File.Move(replacement, target, overwrite: true);
                    })));

        Assert.True(replaced);
        Assert.Equal(expectedFailureCode, exception.FailureCode);
    }

    [Theory]
    [MemberData(nameof(TlsRoles))]
    public void External_tls_roles_cannot_alias_mutable_sqlite_storage(
        string role,
        string _)
    {
        using var files = new ReceiverFiles(includeOperatorPair: role.StartsWith("operator_"));

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                files.OptionsWithRolePath(role, files.Options.SqlitePath),
                committer: new RecordingIngestCommitter()));

        Assert.Equal("protected_path_collision", exception.FailureCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void Manifest_rejects_every_mutable_sqlite_name(string suffix)
    {
        using var files = new ReceiverFiles(includeOperatorPair: false);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                files.OptionsWithRolePath(
                    "server_certificate",
                    files.Options.SqlitePath + suffix),
                committer: new RecordingIngestCommitter()));

        Assert.Equal("protected_path_collision", exception.FailureCode);
    }

    [Fact]
    public void Mac_case_variant_tls_alias_is_rejected_by_verified_identity()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var files = new ReceiverFiles(includeOperatorPair: false);
        var storageName = files.Options.SqlitePath + "-wal";
        var alias = files.Options.SqlitePath + "-WAL";
        var certificateBytes = File.ReadAllBytes(files.ServerCertificatePath);
        File.WriteAllBytes(storageName, certificateBytes);
        _ = SiemProtectedPath.ProtectCreatedFile(storageName);
        Assert.True(File.Exists(alias));

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                files.OptionsWithRolePath("server_certificate", alias)));

        Assert.Equal("protected_path_collision", exception.FailureCode);
        Assert.Equal(certificateBytes, File.ReadAllBytes(storageName));
    }

    [Fact]
    public void Mac_case_variant_config_alias_is_rejected_by_verified_identity()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var files = new ReceiverFiles(includeOperatorPair: false);
        var storageName = files.Options.SqlitePath + "-wal";
        var alias = files.Options.SqlitePath + "-WAL";
        var configurationBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ingest = new
            {
                bindAddress = files.Options.IngestBindAddress.ToString(),
                port = 4318,
                serverCertificatePath = files.Options.ServerCertificatePath,
                serverCertificateKeyPath = files.Options.ServerCertificateKeyPath,
                clientCaBundlePaths = files.Options.ClientCaBundlePaths,
                revocationCheckMode = files.Options.RevocationCheckMode.ToString(),
                maxRequestBytes = files.Options.MaxRequestBytes,
            },
            @operator = new
            {
                bindAddress = files.Options.OperatorBindAddress.ToString(),
                port = files.Options.OperatorPort,
                token = files.Options.OperatorToken,
            },
            storage = new
            {
                sqlitePath = files.Options.SqlitePath,
            },
        });
        File.WriteAllBytes(storageName, configurationBytes);
        _ = SiemProtectedPath.ProtectCreatedFile(storageName);
        Assert.True(File.Exists(alias));
        var options = SiemReceiverConfigurationLoader.Load(alias);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(options));

        Assert.Equal("protected_path_collision", exception.FailureCode);
        Assert.Equal(configurationBytes, File.ReadAllBytes(storageName));
    }

    [Fact]
    public void Startup_protection_failure_does_not_disclose_path_or_material()
    {
        using var files = new ReceiverFiles(includeOperatorPair: false);
        var original = files.ServerKeyPath;
        var canary = "PATH-CANARY-" + Guid.NewGuid().ToString("N");
        var renamed = Path.Combine(Path.GetDirectoryName(original)!, canary);
        File.Move(original, renamed);
        File.WriteAllText(renamed, "MATERIAL-CANARY");
        Broaden(renamed);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                files.OptionsWithRolePath("server_key", renamed),
                committer: new RecordingIngestCommitter()));

        Assert.Equal("tls_protection", exception.FailureCode);
        Assert.DoesNotContain(canary, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("MATERIAL-CANARY", exception.ToString(), StringComparison.Ordinal);
        _ = SiemProtectedPath.ProtectCreatedFile(renamed);
    }

    [Fact]
    public void Configured_operator_material_is_read_even_before_operator_listener_exists()
    {
        using var files = new ReceiverFiles(includeOperatorPair: true);
        File.WriteAllBytes(files.OperatorKeyPath!, []);
        _ = SiemProtectedPath.ProtectCreatedFile(files.OperatorKeyPath!);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            ReceiverApplication.Build(
                files.Options,
                committer: new RecordingIngestCommitter()));

        Assert.Equal("operator_https_material", exception.FailureCode);
    }

    [Fact]
    public async Task Certificate_construction_uses_verified_bytes_not_reopened_paths()
    {
        using var files = new ReceiverFiles(includeOperatorPair: false);
        using var alternate = new ReceiverFiles(
            includeOperatorPair: false,
            alternateMaterial: true);

        await using var application = ReceiverApplication.Build(
            files.Options,
            committer: new RecordingIngestCommitter(),
            tlsMaterialAcquiredForTests: () =>
            {
                File.Move(alternate.ServerCertificatePath, files.ServerCertificatePath, true);
                File.Move(alternate.ServerKeyPath, files.ServerKeyPath, true);
                File.Move(alternate.ClientCaPath, files.ClientCaPath, true);
                File.Move(alternate.SecondClientCaPath, files.SecondClientCaPath, true);
            });

        var loadedServer = application.Services.GetRequiredService<X509Certificate2>();
        var loadedTrust = application.Services.GetRequiredService<ReceiverTrustStore>();
        Assert.Equal(files.ServerThumbprint, loadedServer.Thumbprint);
        Assert.Equal(
            [files.ClientCaThumbprint, files.SecondClientCaThumbprint],
            loadedTrust.Authorities.Select(authority => authority.Thumbprint));
        Assert.NotEqual(alternate.ServerThumbprint, loadedServer.Thumbprint);
        Assert.DoesNotContain(
            loadedTrust.Authorities,
            authority => authority.Thumbprint == alternate.ClientCaThumbprint ||
                         authority.Thumbprint == alternate.SecondClientCaThumbprint);
    }

    [Fact]
    public async Task Storage_protection_finishes_before_listener_can_bind()
    {
        using var files = new ReceiverFiles(includeOperatorPair: false);
        using var sabotage = new BlockingStartupSabotage();
        var port = ReservePort();
        var startup = Task.Run(async () =>
        {
            await using var application = ReceiverApplication.Build(
                files.OptionsWithIngestPort(port),
                storageFaultInjector: sabotage);
            await application.StartAsync();
            await application.StopAsync();
        });
        Assert.True(sabotage.Entered.Wait(TimeSpan.FromSeconds(10)));

        using var client = new TcpClient();
        var connect = Record.ExceptionAsync(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        });
        Assert.NotNull(await connect);

        sabotage.Release.Set();
        var exception = await Assert.ThrowsAsync<SiemReceiverStartupException>(() => startup);
        Assert.Equal("storage_protection", exception.FailureCode);
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

        var foreignSid = SiemTestFileSystem.ForeignWindowsOwnerSid();
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

    private static void Broaden(string path, bool isDirectory = false)
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

    private static string ProtectionSnapshot(string path, bool isDirectory = false)
    {
        if (!OperatingSystem.IsWindows())
            return File.GetUnixFileMode(path).ToString();
        return SnapshotWindowsAcl(path, isDirectory);
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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

    private sealed class ReceiverFiles : IDisposable
    {
        private static readonly Lazy<ReceiverMaterial> PrimaryMaterial =
            new(CreateMaterial);
        private static readonly Lazy<ReceiverMaterial> AlternateMaterial =
            new(CreateMaterial);
        private readonly string _root;

        internal ReceiverFiles(bool includeOperatorPair, bool alternateMaterial = false)
        {
            var material = (alternateMaterial ? AlternateMaterial : PrimaryMaterial).Value;
            _root = SiemTestFileSystem.CreateProtectedRoot("ptk-siem-startup");
            ServerThumbprint = material.ServerThumbprint;
            ClientCaThumbprint = material.ClientCaOneThumbprint;
            SecondClientCaThumbprint = material.ClientCaTwoThumbprint;
            ServerCertificatePath = WriteRoleFile(
                "server-certificate-parent",
                "server.pem",
                material.ServerCertificatePem);
            ServerKeyPath = WriteRoleFile(
                "server-key-parent",
                "server.key",
                material.ServerKeyPem);
            ClientCaPath = WriteRoleFile(
                "client-ca-one-parent",
                "client-ca-one.pem",
                material.ClientCaOnePem);
            SecondClientCaPath = WriteRoleFile(
                "client-ca-two-parent",
                "client-ca-two.pem",
                material.ClientCaTwoPem);

            if (includeOperatorPair)
            {
                OperatorCertificatePath = WriteRoleFile(
                    "operator-certificate-parent",
                    "operator.pem",
                    material.ServerCertificatePem);
                OperatorKeyPath = WriteRoleFile(
                    "operator-key-parent",
                    "operator.key",
                    material.ServerKeyPem);
            }

            DataRoot = Path.Combine(_root, "data-parent");
            Directory.CreateDirectory(DataRoot);
            _ = SiemProtectedPath.ProtectCreatedDirectory(DataRoot);
            Options = CreateOptions(ingestPort: 0);
        }

        internal SiemReceiverOptions Options { get; }

        internal string ServerCertificatePath { get; }

        internal string ServerKeyPath { get; }

        internal string ClientCaPath { get; }

        internal string SecondClientCaPath { get; }

        internal string? OperatorCertificatePath { get; }

        internal string? OperatorKeyPath { get; }

        internal string ServerThumbprint { get; }

        internal string ClientCaThumbprint { get; }

        internal string SecondClientCaThumbprint { get; }

        internal string DataRoot { get; }

        internal SiemReceiverOptions OptionsWithIngestPort(int port) => CreateOptions(port);

        internal SiemReceiverOptions OptionsWithRolePath(string role, string path)
        {
            var authorities = new[] { ClientCaPath, SecondClientCaPath };
            var serverCertificate = ServerCertificatePath;
            var serverKey = ServerKeyPath;
            var operatorCertificate = OperatorCertificatePath;
            var operatorKey = OperatorKeyPath;
            switch (role)
            {
                case "server_certificate":
                    serverCertificate = path;
                    break;
                case "server_key":
                    serverKey = path;
                    break;
                case "client_ca_one":
                    authorities[0] = path;
                    break;
                case "client_ca_two":
                    authorities[1] = path;
                    break;
                case "operator_certificate":
                    operatorCertificate = path;
                    break;
                case "operator_key":
                    operatorKey = path;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role));
            }

            return CreateOptions(
                ingestPort: 0,
                serverCertificate,
                serverKey,
                authorities,
                operatorCertificate,
                operatorKey);
        }

        internal string PathFor(string role) => role switch
        {
            "server_certificate" => ServerCertificatePath,
            "server_key" => ServerKeyPath,
            "client_ca_one" => ClientCaPath,
            "client_ca_two" => SecondClientCaPath,
            "operator_certificate" => OperatorCertificatePath!,
            "operator_key" => OperatorKeyPath!,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

        public void Dispose()
        {
            Directory.Delete(_root, recursive: true);
        }

        private string WriteRoleFile(string parentName, string fileName, string contents)
        {
            var parent = Path.Combine(_root, parentName);
            Directory.CreateDirectory(parent);
            _ = SiemProtectedPath.ProtectCreatedDirectory(parent);
            return SiemTestFileSystem.WriteProtectedText(parent, fileName, contents);
        }

        private SiemReceiverOptions CreateOptions(
            int ingestPort,
            string? serverCertificate = null,
            string? serverKey = null,
            IReadOnlyList<string>? authorities = null,
            string? operatorCertificate = null,
            string? operatorKey = null) => new(
            IPAddress.Loopback,
            ingestPort,
            serverCertificate ?? ServerCertificatePath,
            serverKey ?? ServerKeyPath,
            authorities ?? [ClientCaPath, SecondClientCaPath],
            X509RevocationMode.NoCheck,
            1024 * 1024,
            SiemReceiverConfigurationLoader.DefaultMaxConcurrentRequests,
            IPAddress.Loopback,
            9,
            new string('t', 32),
            operatorCertificate ?? OperatorCertificatePath,
            operatorKey ?? OperatorKeyPath,
            Path.Combine(DataRoot, "siem.db"),
            null,
            null);

        private static ReceiverMaterial CreateMaterial()
        {
            using var primaryAuthority = new TestCertificateAuthority();
            using var secondaryAuthority = new TestCertificateAuthority();
            using var server = primaryAuthority.IssueServer();
            using var primaryRoot = primaryAuthority.Root;
            using var secondaryRoot = secondaryAuthority.Root;
            using var key = server.GetRSAPrivateKey() ??
                            throw new InvalidOperationException("Missing server key.");
            return new ReceiverMaterial(
                server.ExportCertificatePem(),
                key.ExportPkcs8PrivateKeyPem(),
                server.Thumbprint,
                primaryRoot.ExportCertificatePem(),
                primaryRoot.Thumbprint,
                secondaryRoot.ExportCertificatePem(),
                secondaryRoot.Thumbprint);
        }

        private sealed record ReceiverMaterial(
            string ServerCertificatePem,
            string ServerKeyPem,
            string ServerThumbprint,
            string ClientCaOnePem,
            string ClientCaOneThumbprint,
            string ClientCaTwoPem,
            string ClientCaTwoThumbprint);
    }

    private sealed class BlockingStartupSabotage : ISqliteIngestFaultInjector, IDisposable
    {
        internal ManualResetEventSlim Entered { get; } = new(false);

        internal ManualResetEventSlim Release { get; } = new(false);

        public void BeforeCommit(SqliteIngestWriteKind writeKind)
        {
        }

        public void AfterStartupProtectionForTests(string databasePath)
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Storage sabotage barrier was not released.");
            Broaden(databasePath + "-shm");
        }

        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
