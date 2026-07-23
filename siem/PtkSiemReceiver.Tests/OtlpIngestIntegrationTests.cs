using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PtkSiemReceiver.Ingest;
using PtkSiemReceiver.Storage;

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
    public async Task Production_s3_committer_persists_exact_evidence_before_success_ack()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();
        await using var host = await SiemReceiverTestHost.StartAsync(server, [root]);
        using var client = host.CreateClient(clientCertificate);
        var request = OtlpTestRequest.Create();
        var requestBytes = request.ToByteArray();

        using var response = await client.PostAsync(host.Endpoint, OtlpTestRequest.Content(request));
        var responseBody = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.ToString());
        Assert.Empty(responseBody);
        Assert.Equal(requestBytes, DatabaseBytes(host.DatabasePath, "SELECT raw_request FROM events;"));
        Assert.Equal(1L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM events;"));
        Assert.Equal(1L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM custody;"));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(clientCertificate.RawData)).ToLowerInvariant(),
            DatabaseString(host.DatabasePath, "SELECT client_certificate_thumbprint FROM custody;"));
        Assert.StartsWith(
            "127.0.0.1:",
            DatabaseString(host.DatabasePath, "SELECT remote_endpoint FROM custody;"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strict_validation_failure_is_quarantined_before_permanent_response()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();
        await using var host = await SiemReceiverTestHost.StartAsync(server, [root]);
        using var client = host.CreateClient(clientCertificate);
        var request = OtlpTestRequest.Create();
        request.ResourceLogs[0].ScopeLogs[0].LogRecords[0].Attributes
            .Single(attribute => attribute.Key == "ptk.audit.event_type")
            .Value.StringValue = "tool.rejected";
        var requestBytes = request.ToByteArray();

        using var response = await client.PostAsync(host.Endpoint, OtlpTestRequest.Content(request));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((3, "attributes"), ReadGoogleStatus(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal(requestBytes, DatabaseBytes(host.DatabasePath, "SELECT raw_request FROM quarantine;"));
        Assert.Equal("attributes", DatabaseString(host.DatabasePath, "SELECT failure_code FROM quarantine;"));
        Assert.Equal(
            "quarantine:attributes",
            DatabaseString(host.DatabasePath, "SELECT disposition FROM custody;"));
        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM events;"));
    }

    [Fact]
    public async Task Sqlite_full_during_commit_returns_retryable_503_without_persisted_rows()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            storageFaultInjector: new ThrowingStorageFault(
                SqliteIngestWriteKind.Event,
                new SqliteException("database or disk is full", 13, 13)));
        using var client = host.CreateClient(clientCertificate);

        using var response = await client.PostAsync(host.Endpoint, OtlpTestRequest.Content());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        Assert.Equal((14, "commit_failed"), ReadGoogleStatus(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM events;"));
        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM chains;"));
        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM custody;"));
    }

    [Fact]
    public async Task Quarantine_commit_failure_returns_503_instead_of_false_permanent_rejection()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            storageFaultInjector: new ThrowingStorageFault(
                SqliteIngestWriteKind.Quarantine,
                new IOException("interrupted")));
        using var client = host.CreateClient(clientCertificate);
        var request = OtlpTestRequest.Create();
        request.ResourceLogs.Clear();

        using var response = await client.PostAsync(host.Endpoint, OtlpTestRequest.Content(request));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal((14, "commit_failed"), ReadGoogleStatus(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM quarantine;"));
        Assert.Equal(0L, DatabaseInt64(host.DatabasePath, "SELECT COUNT(*) FROM custody;"));
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
        var quarantine = Assert.Single(committer.Quarantines);
        Assert.Equal("protobuf", quarantine.FailureCode);
        Assert.Equal(new byte[] { 0x0a, 0xff }, quarantine.RawRequestBytes);
    }

    [Fact]
    public async Task Body_beyond_kestrel_backstop_is_rejected_at_transport_before_endpoint()
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

        // rbc-10: the transport bound must sit exactly one byte above the
        // application bound. 129 bytes (bound + 1) reaches the endpoint and
        // yields the deterministic request_too_large response (pinned by
        // Malformed_and_oversized_requests_are_permanently_rejected_before_commit);
        // 130 bytes (bound + 2) exceeds the Kestrel backstop and must be
        // refused by the transport itself, never reaching commit/quarantine.
        //
        // NOTE: ByteArrayContent sends a *declared* Content-Length, so this
        // exercises Kestrel's header-based refusal (rejected before the body
        // is read). The streamed/undeclared path is pinned separately by
        // Chunked_body_beyond_kestrel_backstop_fails_closed_mid_stream below.
        using var content = new ByteArrayContent(new byte[130]);
        content.Headers.TryAddWithoutValidation("Content-Type", "application/x-protobuf");
        using var response = await client.PostAsync(host.Endpoint, content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(committer.Records);
        Assert.Empty(committer.Quarantines);
    }

    [Fact]
    public async Task Chunked_body_beyond_kestrel_backstop_fails_closed_mid_stream()
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

        // rbc-10 (streamed path): with chunked transfer encoding there is no
        // declared Content-Length, so Kestrel cannot refuse up front — the
        // backstop must fail closed *while reading* once bound + 2 bytes have
        // been consumed. Either surface is acceptable — a 413 response or an
        // aborted request stream — but the body must never reach
        // commit/quarantine.
        using var content = new StreamContent(new MemoryStream(new byte[130]));
        content.Headers.TryAddWithoutValidation("Content-Type", "application/x-protobuf");
        using var request = new HttpRequestMessage(HttpMethod.Post, host.Endpoint)
        {
            Content = content,
        };
        request.Headers.TransferEncodingChunked = true;

        try
        {
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        catch (HttpRequestException)
        {
            // Connection abort while writing the oversized chunked body is a
            // valid fail-closed outcome.
        }

        Assert.Empty(committer.Records);
        Assert.Empty(committer.Quarantines);
    }

    [Fact]
    public async Task Saturated_admission_gate_refuses_with_transient_503_and_recovers()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        using var clientCertificate = authority.IssueClient();
        var committer = new BlockingIngestCommitter();
        await using var host = await SiemReceiverTestHost.StartAsync(
            server,
            [root],
            committer,
            maximumConcurrentRequests: 1);
        using var client = host.CreateClient(clientCertificate);

        // Park the single admission slot inside the committer, then prove a
        // concurrent request is refused (rbc-12) without queueing: transient
        // 503, Retry-After, admission_capacity detail, no extra commit.
        var parked = client.PostAsync(host.Endpoint, OtlpTestRequest.Content());
        await committer.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        using (var refused = await client.PostAsync(host.Endpoint, OtlpTestRequest.Content()))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal("1", refused.Headers.RetryAfter?.ToString());
            Assert.Equal(
                (14, "admission_capacity"),
                ReadGoogleStatus(await refused.Content.ReadAsByteArrayAsync()));
        }

        committer.Release();
        using (var admitted = await parked)
        {
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        }

        // The slot must be returned after completion: capacity recovers.
        using var reopened = await client.PostAsync(host.Endpoint, OtlpTestRequest.Content());
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        Assert.Equal(2, committer.Commits);
    }

    private sealed class BlockingIngestCommitter : IIngestCommitter
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _commits;

        internal Task Entered => _entered.Task;

        internal int Commits => Volatile.Read(ref _commits);

        internal void Release() => _release.TrySetResult();

        public async Task<IngestCommitResult> CommitAsync(
            ValidatedOtlpRecord record,
            IngestReceiptContext receipt,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            Interlocked.Increment(ref _commits);
            return IngestCommitResult.Accepted();
        }
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

    private static byte[] DatabaseBytes(string path, string sql) =>
        Assert.IsType<byte[]>(DatabaseScalar(path, sql));

    private static string DatabaseString(string path, string sql) =>
        Assert.IsType<string>(DatabaseScalar(path, sql));

    private static long DatabaseInt64(string path, string sql) =>
        Assert.IsType<long>(DatabaseScalar(path, sql));

    private static object DatabaseScalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() ?? DBNull.Value;
    }

    private sealed class ThrowingStorageFault(
        SqliteIngestWriteKind target,
        Exception exception) : ISqliteIngestFaultInjector
    {
        public void BeforeCommit(SqliteIngestWriteKind writeKind)
        {
            if (writeKind == target) throw exception;
        }
    }
}
