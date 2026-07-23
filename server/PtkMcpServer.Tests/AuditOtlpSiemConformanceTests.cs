extern alias siem;

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PtkMcpServer.Audit;
using PtkMcpServer.Audit.OtlpWire;
using SiemCommitResult = siem::PtkSiemReceiver.Ingest.IngestCommitResult;
using SiemCommitter = siem::PtkSiemReceiver.Ingest.IIngestCommitter;
using SiemOptions = siem::PtkSiemReceiver.Configuration.SiemReceiverOptions;
using SiemOptionsLoader = siem::PtkSiemReceiver.Configuration.SiemReceiverConfigurationLoader;
using SiemProtectedPath = siem::PtkSiemReceiver.Security.SiemProtectedPath;
using SiemReceiverApplication = siem::PtkSiemReceiver.Ingest.ReceiverApplication;
using SiemValidatedRecord = siem::PtkSiemReceiver.Ingest.ValidatedOtlpRecord;

namespace PtkMcpServer.Tests;

[Collection(SiemOtlpConformanceCollection.Name)]
public sealed class AuditOtlpSiemConformanceTests
{
    private const string OverrideVariable = "PTK_SIEM_CONFORMANCE_MODE";

    public static IEnumerable<object[]> ReceiverCases()
    {
        var mode = Environment.GetEnvironmentVariable(OverrideVariable);
        if (mode is null)
        {
            yield return [false];
            yield break;
        }
        if (!string.Equals(mode, "in-process", StringComparison.Ordinal))
            throw new InvalidOperationException($"{OverrideVariable} must be exactly 'in-process' when set.");
        yield return [true];
    }

    public static IEnumerable<object[]> ProducerCorpusReceiverCases()
    {
        foreach (var receiverCase in ReceiverCases())
        {
            foreach (var recordName in new[]
                     {
                         "audit-v1",
                         "audit-v2",
                         "audit-v3-null",
                         "audit-v3-host",
                     })
            {
                yield return [receiverCase[0], recordName];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ProducerCorpusReceiverCases))]
    public async Task Producer_owned_corpus_is_byte_identical_through_fake_or_live_receiver(
        bool useSiemOverride,
        string recordName)
    {
        var jsonl = ReadCorpus(recordName, "jsonl");
        var expectedRequest = ReadCorpus(recordName, "otlp");
        var record = AuditOtlpRecordMapper.Map(jsonl);
        Assert.Equal(expectedRequest, record.RequestBytes.ToArray());

        if (!useSiemOverride)
        {
            var handler = new CapturingAckHandler();
            using var exporter = CreateExporter(
                new Uri("https://127.0.0.1:1/v1/logs"),
                [],
                [],
                clientCertificate: null,
                handler);

            var result = await ExportAsync(exporter, record);

            Assert.Equal(AuditExportAttemptKind.Acknowledged, result.Kind);
            Assert.Equal(expectedRequest, handler.LastRequestBody);
            return;
        }

        using var pki = SiemConformancePki.Create();
        await using var receiver = await SiemOtlpConformanceHost.StartAsync(pki);
        using var siemExporter = CreateExporter(
            receiver.Endpoint,
            [],
            [pki.CreateTrustedRoot()],
            pki.CreateClientCertificate(),
            handler: null);

        var siemResult = await ExportAsync(siemExporter, record);

        Assert.Equal(AuditExportAttemptKind.Acknowledged, siemResult.Kind);
        var committed = Assert.Single(receiver.Committer.Records);
        Assert.Equal(expectedRequest, committed.RawRequestBytes);
        Assert.Equal(record.ExactJsonBody.ToArray(), committed.ExactJsonBody);
    }

    [Theory]
    [MemberData(nameof(ReceiverCases))]
    public async Task Exporter_fixture_parameter_uses_default_transport_or_live_siem_when_overridden(
        bool useSiemOverride)
    {
        var record = AuditOtlpRecordMapper.Map(SiemConformanceAuditRecord.Create().Utf8Line);

        if (!useSiemOverride)
        {
            var handler = new CapturingAckHandler();
            using var exporter = CreateExporter(
                new Uri("https://127.0.0.1:1/v1/logs"),
                [],
                [],
                clientCertificate: null,
                handler);

            var result = await ExportAsync(exporter, record);

            Assert.Equal(AuditExportAttemptKind.Acknowledged, result.Kind);
            Assert.Equal(record.RequestBytes.ToArray(), handler.LastRequestBody);
            return;
        }

        using var pki = SiemConformancePki.Create();
        await using var receiverOverride = await SiemOtlpConformanceHost.StartAsync(pki);
        using var siemExporter = CreateExporter(
            receiverOverride.Endpoint,
            [],
            [pki.CreateTrustedRoot()],
            pki.CreateClientCertificate(),
            handler: null);

        var siemResult = await ExportAsync(siemExporter, record);

        Assert.Equal(AuditExportAttemptKind.Acknowledged, siemResult.Kind);
        var committed = Assert.Single(receiverOverride.Committer.Records);
        Assert.Equal(record.RequestBytes.ToArray(), committed.RawRequestBytes);
        Assert.Equal(record.ExactJsonBody.ToArray(), committed.ExactJsonBody);
    }

    [Fact]
    public void Producer_fixture_serializer_emits_exact_current_v1_v2_and_v3_request_bytes()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ptk-siem-golden-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var v2 = SiemConformanceAuditRecord.Create().Utf8Line;
            var v1 = AuditCoreSchemaTestRecords.ToLegacyV1(v2);
            var v3 = ReadCorpus("audit-v3-host", "jsonl");

            ProducerGoldenOtlpFixtureSerializer.Write(
                root,
                new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
                {
                    ["v1"] = v1,
                    ["v2"] = v2,
                    ["v3"] = v3,
                });

            Assert.Equal(
                AuditOtlpRecordMapper.Map(v1).RequestBytes.ToArray(),
                File.ReadAllBytes(Path.Combine(root, "v1.otlp.bin")));
            Assert.Equal(
                AuditOtlpRecordMapper.Map(v2).RequestBytes.ToArray(),
                File.ReadAllBytes(Path.Combine(root, "v2.otlp.bin")));
            Assert.Equal(
                AuditOtlpRecordMapper.Map(v3).RequestBytes.ToArray(),
                File.ReadAllBytes(Path.Combine(root, "v3.otlp.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<AuditExportAttemptResult> ExportAsync(
        AuditOtlpHttpExporter exporter,
        AuditOtlpRecord record)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await exporter.ExportAsync(record, timeout.Token);
    }

    private static AuditOtlpHttpExporter CreateExporter(
        Uri endpoint,
        IReadOnlyList<AuditExportHeader> headers,
        IReadOnlyList<X509Certificate2> authorities,
        X509Certificate2? clientCertificate,
        HttpMessageHandler? handler)
    {
        var options = new AuditExportOptions(
            endpoint.AbsoluteUri,
            endpoint,
            headers,
            authorities,
            clientCertificate,
            X509RevocationMode.NoCheck,
            new string('a', 64));
        return handler is null
            ? AuditOtlpHttpExporter.Create(options, "9.8.7")
            : new AuditOtlpHttpExporter(options, "9.8.7", handler);
    }

    private static byte[] ReadCorpus(
        string recordName,
        string kind,
        [CallerFilePath] string sourcePath = "")
    {
        if (recordName is not (
                "audit-v1" or "audit-v2" or "audit-v3-null" or "audit-v3-host") ||
            kind is not ("jsonl" or "otlp"))
        {
            throw new ArgumentException("The producer corpus key is invalid.");
        }

        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            "..",
            "Contracts",
            "SiemConformance",
            $"{recordName}.{kind}.base64"));
        var file = File.ReadAllBytes(path);
        Assert.NotEmpty(file);
        Assert.Equal((byte)'\n', file[^1]);
        Assert.Equal(1, file.Count(value => value == (byte)'\n'));
        Assert.DoesNotContain((byte)'\r', file);
        var encoded = System.Text.Encoding.ASCII.GetString(file.AsSpan()[..^1]);
        var decoded = Convert.FromBase64String(encoded);
        Assert.Equal(encoded, Convert.ToBase64String(decoded));
        return decoded;
    }
}

internal static class ProducerGoldenOtlpFixtureSerializer
{
    private static readonly HashSet<string> AllowedNames = new(
        ["v1", "v2", "v3"],
        StringComparer.Ordinal);

    internal static void Write(
        string outputDirectory,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> records)
    {
        if (!Path.IsPathFullyQualified(outputDirectory) ||
            records.Count == 0 ||
            records.Keys.Any(name => !AllowedNames.Contains(name)))
        {
            throw new ArgumentException("The golden OTLP fixture request is invalid.");
        }

        Directory.CreateDirectory(outputDirectory);
        foreach (var pair in records.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var mapped = AuditOtlpRecordMapper.Map(pair.Value);
            File.WriteAllBytes(
                Path.Combine(outputDirectory, $"{pair.Key}.otlp.bin"),
                mapped.RequestBytes.ToArray());
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SiemOtlpConformanceCollection
{
    public const string Name = "siem otlp conformance";
}

internal sealed class SiemOtlpConformanceHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly string _root;

    private SiemOtlpConformanceHost(
        WebApplication application,
        string root,
        Uri endpoint,
        SiemConformanceCommitter committer)
    {
        _application = application;
        _root = root;
        Endpoint = endpoint;
        Committer = committer;
    }

    internal Uri Endpoint { get; }

    internal SiemConformanceCommitter Committer { get; }

    internal static async Task<SiemOtlpConformanceHost> StartAsync(SiemConformancePki pki)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ptk-siem-conformance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _ = SiemProtectedPath.ProtectCreatedDirectory(root);
        var certificatePath = Path.Combine(root, "server-cert.pem");
        var keyPath = Path.Combine(root, "server-key.pem");
        var authorityPath = Path.Combine(root, "client-root.pem");
        _ = SiemProtectedPath.CreateProtectedFile(certificatePath);
        _ = SiemProtectedPath.CreateProtectedFile(keyPath);
        _ = SiemProtectedPath.CreateProtectedFile(authorityPath);
        await File.WriteAllTextAsync(certificatePath, pki.ServerCertificate.ExportCertificatePem());
        using (var key = pki.ServerCertificate.GetRSAPrivateKey() ??
                         throw new InvalidOperationException("The conformance server certificate has no RSA key."))
        {
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
        }
        await File.WriteAllTextAsync(authorityPath, pki.CertificateAuthority.ExportCertificatePem());

        var options = new SiemOptions(
            IPAddress.Loopback,
            0,
            certificatePath,
            keyPath,
            [authorityPath],
            X509RevocationMode.NoCheck,
            1024 * 1024,
            SiemOptionsLoader.DefaultMaxConcurrentRequests,
            IPAddress.Loopback,
            9,
            new string('t', 32),
            null,
            null,
            Path.Combine(root, "siem.db"),
            null,
            null);
        var committer = new SiemConformanceCommitter();
        WebApplication? application = null;
        try
        {
            application = SiemReceiverApplication.Build(options, committer: committer);
            await application.StartAsync();
            var server = application.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ??
                            throw new InvalidOperationException("Kestrel did not publish an address.");
            var endpoint = new Uri(new Uri(Assert.Single(addresses)), "/v1/logs");
            return new SiemOtlpConformanceHost(application, root, endpoint, committer);
        }
        catch
        {
            if (application is not null) await application.DisposeAsync();
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync();
        await _application.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}

internal sealed class SiemConformanceCommitter : SiemCommitter
{
    private readonly List<SiemValidatedRecord> _records = [];

    internal IReadOnlyList<SiemValidatedRecord> Records => _records;

    public Task<SiemCommitResult> CommitAsync(
        SiemValidatedRecord record,
        CancellationToken cancellationToken)
    {
        _records.Add(record);
        return Task.FromResult(SiemCommitResult.Accepted());
    }
}

internal sealed class CapturingAckHandler : HttpMessageHandler
{
    internal byte[]? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var content = new ByteArrayContent(new ExportLogsServiceResponse().ToByteArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

internal sealed class SiemConformancePki : IDisposable
{
    private SiemConformancePki(
        X509Certificate2 certificateAuthority,
        X509Certificate2 serverCertificate)
    {
        CertificateAuthority = certificateAuthority;
        ServerCertificate = serverCertificate;
    }

    internal X509Certificate2 CertificateAuthority { get; }

    internal X509Certificate2 ServerCertificate { get; }

    internal static SiemConformancePki Create()
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest(
            new X500DistinguishedName($"CN=ptk-siem-conformance-root-{Guid.NewGuid():N}"),
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));

        try
        {
            var server = IssueCertificate(
                root,
                "ptk-siem-conformance-server",
                "1.3.6.1.5.5.7.3.1",
                addLoopbackSan: true);
            return new SiemConformancePki(root, server);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    internal X509Certificate2 CreateTrustedRoot() =>
        X509CertificateLoader.LoadCertificate(CertificateAuthority.RawData);

    internal X509Certificate2 CreateClientCertificate() =>
        IssueCertificate(
            CertificateAuthority,
            "ptk-siem-conformance-client",
            "1.3.6.1.5.5.7.3.2",
            addLoopbackSan: false);

    public void Dispose()
    {
        ServerCertificate.Dispose();
        CertificateAuthority.Dispose();
    }

    private static X509Certificate2 IssueCertificate(
        X509Certificate2 authority,
        string commonName,
        string eku,
        bool addLoopbackSan)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={commonName}-{Guid.NewGuid():N}"),
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(eku) },
            true));
        if (addLoopbackSan)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        using var issued = request.Create(
            authority,
            now.AddMinutes(-5),
            now.AddDays(1),
            RandomNumberGenerator.GetBytes(16));
        using var withPrivateKey = issued.CopyWithPrivateKey(key);
        var pkcs12 = withPrivateKey.Export(X509ContentType.Pkcs12, string.Empty);
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pkcs12,
                string.Empty,
                X509KeyStorageFlags.DefaultKeySet | X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }
}

internal static class SiemConformanceAuditRecord
{
    private static readonly Guid EventId = Guid.Parse("01890f3e-1234-7abc-8def-0123456789ab");
    private static readonly Guid HostId = Guid.Parse("12345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid SupervisorBootId = Guid.Parse("22345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid PlanId = Guid.Parse("42345678-1234-4abc-8def-0123456789ab");
    private static readonly DateTimeOffset Occurred =
        new(2026, 7, 11, 12, 34, 56, 123, TimeSpan.Zero);
    private static readonly DateTimeOffset Observed = Occurred.AddTicks(4567);
    private const string Hash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    internal static SerializedAuditEvent Create() =>
        AuditEventSerializer.Serialize(
            1,
            null,
            new AuditProducerContext(
                HostId,
                SupervisorBootId,
                null,
                4321,
                "1.2.3-test",
                Hash),
            new AuditEventInput
            {
                EventType = "execution.planned",
                Session = new AuditSession
                {
                    DeclaredPurpose = "conformance",
                    DeclaredTarget = "localhost",
                    DeclaredIdentity = "test-user",
                    EffectiveIdentity = "test-user",
                    AllowColdBackground = false,
                },
                Actor = new AuditActor
                {
                    AttributionStrength = "client_asserted",
                },
                Correlation = new AuditCorrelation
                {
                    PlanId = PlanId,
                },
                Request = new AuditRequest
                {
                    Tool = "ptk_invoke",
                    Action = "invoke",
                    ProvidedFields = [],
                    Background = false,
                    Raw = false,
                    Force = false,
                    AllowColdBackground = false,
                },
                Routing = new AuditRouting
                {
                    PermittedFallbacks = [],
                },
                Outcome = new AuditOutcome
                {
                    QueueMs = 0,
                    WarmStateLost = false,
                    WorkerReplaced = false,
                },
                Coverage = new AuditCoverage
                {
                    PtkRequest = true,
                    RootProcessObserved = "not_applicable",
                    DescendantsObserved = "not_applicable",
                    RemoteEffectObserved = "not_applicable",
                },
                Audit = new AuditEventHealth
                {
                    ProtectionMode = "local-only",
                },
            },
            EventId,
            Occurred,
            Observed);
}
