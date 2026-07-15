using System.Net;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;

namespace PtkSiemReceiver.Tests;

[Collection(SiemReceiverProcessCollection.Name)]
public sealed class OtlpIngestIntegrationTests
{
    [Fact]
    public async Task Valid_mtls_request_returns_exact_nonrejecting_ack_after_committer_accepts()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();
        var committer = new RecordingIngestCommitter();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            committer);
        using var client = host.CreateClient(clientCertificate);

        using var response = await client.PostAsync(host.Endpoint, OtlpTestRequest.Content());
        var responseBody = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.ToString());
        Assert.Empty(responseBody);
        Assert.Null(PtkMcpServer.Audit.OtlpWire.ExportLogsServiceResponse.Parser
            .ParseFrom(responseBody).PartialSuccess);
        var committed = Assert.Single(committer.Records);
        Assert.Equal("ptk.audit/2", committed.SchemaVersion);
    }

    [Fact]
    public async Task Production_s2_committer_refuses_false_ack_with_retryable_503()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();
        await using var host = await SiemReceiverTestHost.StartAsync(server, [root]);
        using var client = host.CreateClient(clientCertificate);

        using var response = await client.PostAsync(host.Endpoint, OtlpTestRequest.Content());
        var status = ReadGoogleStatus(await response.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        Assert.Equal((14, "storage_not_ready"), status);
    }

    [Fact]
    public async Task Committer_permanent_and_transient_failures_use_frozen_response_rows()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();

        var permanent = new RecordingIngestCommitter(
            PtkSiemReceiver.Ingest.IngestCommitResult.Permanent("duplicate_mismatch"));
        await using (var host = await SiemReceiverTestHost.StartAsync(server, [root], permanent))
        using (var client = host.CreateClient(clientCertificate))
        using (var response = await client.PostAsync(host.Endpoint, OtlpTestRequest.Content()))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal((3, "duplicate_mismatch"), ReadGoogleStatus(await response.Content.ReadAsByteArrayAsync()));
        }

        var transient = new RecordingIngestCommitter(exception: new IOException("simulated"));
        await using (var host = await SiemReceiverTestHost.StartAsync(server, [root], transient))
        using (var client = host.CreateClient(clientCertificate))
        using (var response = await client.PostAsync(host.Endpoint, OtlpTestRequest.Content()))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal((14, "commit_failed"), ReadGoogleStatus(await response.Content.ReadAsByteArrayAsync()));
        }
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-protobuf; charset=utf-8")]
    public async Task Nonexact_content_type_is_permanently_rejected(string contentType)
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();
        var committer = new RecordingIngestCommitter();
        await using var host = await SiemReceiverTestHost.StartAsync(server, [root], committer);
        using var client = host.CreateClient(clientCertificate);

        using var response = await client.PostAsync(
            host.Endpoint,
            OtlpTestRequest.Content(contentType: contentType));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((3, "content_type"), ReadGoogleStatus(await response.Content.ReadAsByteArrayAsync()));
        Assert.Empty(committer.Records);
    }

    [Fact]
    public async Task Malformed_and_oversized_requests_are_permanently_rejected_before_commit()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();
        var committer = new RecordingIngestCommitter();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            committer,
            maximumRequestBytes: 128);
        using var client = host.CreateClient(clientCertificate);

        using var malformedContent = new ByteArrayContent([0x0a, 0xff]);
        malformedContent.Headers.TryAddWithoutValidation("Content-Type", "application/x-protobuf");
        using var malformed = await client.PostAsync(host.Endpoint, malformedContent);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal((3, "protobuf"), ReadGoogleStatus(await malformed.Content.ReadAsByteArrayAsync()));

        using var oversizedContent = new ByteArrayContent(new byte[129]);
        oversizedContent.Headers.TryAddWithoutValidation("Content-Type", "application/x-protobuf");
        using var oversized = await client.PostAsync(host.Endpoint, oversizedContent);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Equal((3, "request_too_large"), ReadGoogleStatus(await oversized.Content.ReadAsByteArrayAsync()));
        Assert.Empty(committer.Records);
    }

    [Fact]
    public async Task Missing_client_certificate_is_rejected_during_tls_handshake()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        await using var host = await SiemReceiverTestHost.StartAsync(server, [root], new RecordingIngestCommitter());
        using var client = host.CreateClient();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PostAsync(host.Endpoint, OtlpTestRequest.Content()));
    }

    [Fact]
    public async Task Client_from_wrong_ca_is_rejected_during_tls_handshake()
    {
        using var trustedAuthority = new TestCertificateAuthority();
        using var wrongAuthority = new TestCertificateAuthority();
        using var root = trustedAuthority.Root;
        using var server = trustedAuthority.IssueServer();
        using var wrongClient = wrongAuthority.IssueClient();
        await using var host = await SiemReceiverTestHost.StartAsync(server, [root], new RecordingIngestCommitter());
        using var client = host.CreateClient(wrongClient);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PostAsync(host.Endpoint, OtlpTestRequest.Content()));
    }

    [Fact]
    public async Task Expired_and_wrong_eku_client_certificates_are_rejected()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var expired = authority.IssueClient(
            DateTimeOffset.UtcNow.AddDays(-3),
            DateTimeOffset.UtcNow.AddDays(-2));
        using var wrongEku = authority.IssueClient(eku: "1.3.6.1.5.5.7.3.1");
        await using var host = await SiemReceiverTestHost.StartAsync(server, [root], new RecordingIngestCommitter());

        using (var client = host.CreateClient(expired))
        {
            await Assert.ThrowsAsync<HttpRequestException>(
                () => client.PostAsync(host.Endpoint, OtlpTestRequest.Content()));
        }
        using (var client = host.CreateClient(wrongEku))
        {
            await Assert.ThrowsAsync<HttpRequestException>(
                () => client.PostAsync(host.Endpoint, OtlpTestRequest.Content()));
        }
    }

    [Fact]
    public async Task Old_and_new_client_cas_are_both_accepted_during_rotation()
    {
        using var oldAuthority = new TestCertificateAuthority();
        using var newAuthority = new TestCertificateAuthority();
        using var oldRoot = oldAuthority.Root;
        using var newRoot = newAuthority.Root;
        using var server = oldAuthority.IssueServer();
        using var oldClient = oldAuthority.IssueClient();
        using var newClient = newAuthority.IssueClient();
        var committer = new RecordingIngestCommitter();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [oldRoot, newRoot],
            committer);

        using (var client = host.CreateClient(oldClient))
        using (var response = await client.PostAsync(host.Endpoint, OtlpTestRequest.Content()))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var client = host.CreateClient(newClient))
        using (var response = await client.PostAsync(host.Endpoint, OtlpTestRequest.Content()))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(2, committer.Records.Count);
    }

    [Fact]
    public async Task Online_revocation_policy_does_not_silently_fall_back_for_uncheckable_certificate()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            new RecordingIngestCommitter(),
            X509RevocationMode.Online);
        using var client = host.CreateClient(clientCertificate);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PostAsync(host.Endpoint, OtlpTestRequest.Content()));
    }

    private static (int Code, string Message) ReadGoogleStatus(byte[] body)
    {
        var input = new CodedInputStream(body);
        var code = 0;
        var message = string.Empty;
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
                default:
                    throw new InvalidDataException($"Unexpected google.rpc.Status tag {tag}.");
            }
        }
        return (code, message);
    }
}
