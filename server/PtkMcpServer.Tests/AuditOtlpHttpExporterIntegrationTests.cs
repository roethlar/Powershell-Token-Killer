using System.Security.Cryptography;
using Google.Protobuf;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditOtlpHttpExporterIntegrationTests
{
    [Fact]
    public void Siem_endpoint_override_is_opt_in_during_the_ordinary_battery()
    {
        Assert.Null(Environment.GetEnvironmentVariable("PTK_SIEM_CONFORMANCE_MODE"));
    }

    [Fact]
    public async Task Real_https_success_durably_flushes_exact_body_and_event_id_before_acknowledgment()
    {
        using var pki = FakeOtlpPki.Create();
        await using var receiver = new FakeOtlpHttpsReceiver(
            pki,
            blockReceiptFlush: true);
        using var exporter = CreateExporter(receiver, pki);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var record = AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line);

        var export = exporter.ExportAsync(record, timeout.Token);
        await receiver.WaitForReceiptFlushPendingAsync(timeout.Token);
        try
        {
            Assert.False(receiver.ResponseStarted);
        }
        finally
        {
            receiver.ReleaseReceiptFlush();
        }
        var result = await export;

        Assert.Equal(AuditExportAttemptKind.Acknowledged, result.Kind);
        Assert.Equal("/v1/logs", receiver.LastRequestTarget);
        Assert.Equal(record.RequestBytes.ToArray(), receiver.LastRequestBody);
        var receipt = Assert.Single(receiver.ReadDurableReceipts());
        Assert.Equal(record.ExactJsonBody.ToArray(), receipt.ExactJsonBody);
        Assert.Equal(record.EventId.ToString("D"), receipt.EventId);
        Assert.Equal(200, receiver.LastResponse?.StatusCode);
    }

    [Fact]
    public async Task Real_https_wrong_auth_returns_google_status_and_persists_nothing()
    {
        using var pki = FakeOtlpPki.Create();
        await using var receiver = new FakeOtlpHttpsReceiver(pki);
        var wrongAuthorization =
            $"Bearer {Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        using var exporter = CreateExporter(receiver, pki, authorizationValue: wrongAuthorization);

        var result = await ExportAsync(exporter);

        Assert.Equal(AuditExportAttemptKind.Blocked, result.Kind);
        Assert.Equal(AuditExportFailureClass.Configuration, result.FailureClass);
        Assert.Equal("http.401", result.DetailCode);
        Assert.Equal(1, receiver.RequestCount);
        Assert.Empty(receiver.ReadDurableReceipts());
        var response = Assert.IsType<FakeOtlpResponseSnapshot>(receiver.LastResponse);
        Assert.Equal(401, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.ContentType);
        Assert.Equal("Bearer", response.Headers["WWW-Authenticate"]);
        AssertGoogleStatus(response.Body, "unauthenticated");
    }

    [Fact]
    public async Task Real_https_503_returns_retry_after_and_google_status_without_persistence()
    {
        using var pki = FakeOtlpPki.Create();
        await using var receiver = new FakeOtlpHttpsReceiver(
            pki,
            FakeOtlpResponseMode.ServiceUnavailable);
        using var exporter = CreateExporter(receiver, pki);

        var result = await ExportAsync(exporter);

        Assert.Equal(AuditExportAttemptKind.Retry, result.Kind);
        Assert.Null(result.FailureClass);
        Assert.Equal("http.503", result.DetailCode);
        Assert.Equal(TimeSpan.FromSeconds(1), result.RetryAfter);
        Assert.Empty(receiver.ReadDurableReceipts());
        var response = Assert.IsType<FakeOtlpResponseSnapshot>(receiver.LastResponse);
        Assert.Equal(503, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.ContentType);
        Assert.Equal("1", response.Headers["Retry-After"]);
        AssertGoogleStatus(response.Body, "unavailable");
    }

    [Fact]
    public async Task Real_https_wrong_ca_is_a_tls_configuration_block_with_no_http_request()
    {
        using var serverPki = FakeOtlpPki.Create();
        using var wrongPki = FakeOtlpPki.Create();
        await using var receiver = new FakeOtlpHttpsReceiver(serverPki);
        using var exporter = CreateExporter(receiver, wrongPki);

        var result = await ExportAsync(exporter);

        Assert.Equal(AuditExportAttemptKind.Blocked, result.Kind);
        Assert.Equal(AuditExportFailureClass.Configuration, result.FailureClass);
        Assert.Equal("tls.validation", result.DetailCode);
        Assert.Equal(0, receiver.RequestCount);
        Assert.Empty(receiver.ReadDurableReceipts());
    }

    [Fact]
    public async Task Real_https_wrong_hostname_is_rejected_even_with_the_right_custom_ca()
    {
        using var pki = FakeOtlpPki.Create();
        await using var receiver = new FakeOtlpHttpsReceiver(pki);
        var endpoint = new UriBuilder(receiver.Endpoint) { Host = "localhost" }.Uri;
        using var exporter = CreateExporter(receiver, pki, endpoint: endpoint);

        var result = await ExportAsync(exporter);

        Assert.Equal(AuditExportAttemptKind.Blocked, result.Kind);
        Assert.Equal(AuditExportFailureClass.Configuration, result.FailureClass);
        Assert.Equal("tls.validation", result.DetailCode);
        Assert.Equal(0, receiver.RequestCount);
        Assert.Empty(receiver.ReadDurableReceipts());
    }

    [Fact]
    public async Task Real_https_307_is_not_followed_and_redirect_target_receives_zero_requests()
    {
        using var pki = FakeOtlpPki.Create();
        await using var target = new FakeOtlpHttpsReceiver(pki);
        await using var receiver = new FakeOtlpHttpsReceiver(
            pki,
            FakeOtlpResponseMode.Redirect,
            target.Endpoint);
        using var exporter = CreateExporter(receiver, pki);

        var result = await ExportAsync(exporter);

        Assert.Equal(AuditExportAttemptKind.Blocked, result.Kind);
        Assert.Equal(AuditExportFailureClass.Configuration, result.FailureClass);
        Assert.Equal("http.307", result.DetailCode);
        Assert.Equal(1, receiver.RequestCount);
        Assert.Equal(0, target.RequestCount);
        Assert.Empty(receiver.ReadDurableReceipts());
        Assert.Empty(target.ReadDurableReceipts());
        var response = Assert.IsType<FakeOtlpResponseSnapshot>(receiver.LastResponse);
        Assert.Equal(target.Endpoint.AbsoluteUri, response.Headers["Location"]);
    }

    private static async Task<AuditExportAttemptResult> ExportAsync(AuditOtlpHttpExporter exporter)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await exporter.ExportAsync(
            AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line),
            timeout.Token);
    }

    private static AuditOtlpHttpExporter CreateExporter(
        FakeOtlpHttpsReceiver receiver,
        FakeOtlpPki trustedPki,
        string? authorizationValue = null,
        Uri? endpoint = null)
    {
        var actualEndpoint = endpoint ?? receiver.Endpoint;
        var options = new AuditExportOptions(
            actualEndpoint.AbsoluteUri,
            actualEndpoint,
            [new AuditExportHeader("Authorization", authorizationValue ?? receiver.AuthorizationValue)],
            [trustedPki.CreateTrustedRoot()],
            null,
            new string('a', 64));
        return AuditOtlpHttpExporter.Create(options, "9.8.7");
    }

    private static void AssertGoogleStatus(byte[] body, string expectedMessage)
    {
        var input = new CodedInputStream(body);
        var code = 0;
        var message = string.Empty;
        var details = 0;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 8:
                    code = input.ReadInt32();
                    break;
                case 18:
                    message = input.ReadString();
                    break;
                case 26:
                    _ = input.ReadBytes();
                    details++;
                    break;
                default:
                    throw new InvalidDataException($"Unexpected google.rpc.Status tag {tag}.");
            }
        }

        Assert.Equal(0, code);
        Assert.Equal(expectedMessage, message);
        Assert.Equal(0, details);
    }
}
