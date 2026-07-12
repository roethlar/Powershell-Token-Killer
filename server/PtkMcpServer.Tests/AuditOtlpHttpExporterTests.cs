using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using PtkMcpServer.Audit;
using PtkMcpServer.Audit.OtlpWire;

namespace PtkMcpServer.Tests;

public sealed class AuditOtlpHttpExporterTests
{
    [Fact]
    public async Task Valid_200_acknowledges_one_exact_request_with_fixed_protocol_headers()
    {
        byte[]? postedBody = null;
        Uri? postedUri = null;
        string? authorization = null;
        string? userAgent = null;
        using var exporter = CreateExporter(async (request, cancellationToken) =>
        {
            postedUri = request.RequestUri;
            authorization = request.Headers.Authorization?.ToString();
            userAgent = request.Headers.UserAgent.ToString();
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/x-protobuf", request.Content?.Headers.ContentType?.MediaType);
            Assert.Null(request.Content?.Headers.ContentEncoding.SingleOrDefault());
            postedBody = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return ProtobufResponse(HttpStatusCode.OK, new ExportLogsServiceResponse().ToByteArray());
        });
        var record = AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line);

        var result = await exporter.ExportAsync(record, CancellationToken.None);

        Assert.Equal(AuditExportAttemptKind.Acknowledged, result.Kind);
        Assert.Null(result.FailureClass);
        Assert.Equal("https://collector.example:4318/custom-anchor", postedUri?.AbsoluteUri);
        Assert.Equal("Bearer test-secret", authorization);
        Assert.Equal("PowerShell-Token-Killer/9.8.7 OTLP-HTTP-dotnet/1", userAgent);
        Assert.NotNull(postedBody);
        Assert.Equal(record.RequestBytes.Span, postedBody);
    }

    [Fact]
    public async Task Zero_rejection_warning_acknowledges_without_exposing_receiver_text()
    {
        var response = new ExportLogsServiceResponse
        {
            PartialSuccess = new ExportLogsPartialSuccess
            {
                RejectedLogRecords = 0,
                ErrorMessage = "receiver warning that must not become a detail code",
            },
        };
        using var exporter = CreateExporter((_, _) => Task.FromResult(
            ProtobufResponse(HttpStatusCode.OK, response.ToByteArray())));

        var result = await exporter.ExportAsync(
            AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line),
            CancellationToken.None);

        Assert.Equal(AuditExportAttemptKind.Acknowledged, result.Kind);
        Assert.True(result.HasHealthWarning);
        Assert.Equal("otlp.acknowledged_warning", result.DetailCode);
        Assert.DoesNotContain("receiver", result.DetailCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partial_rejection_pins_the_record_without_retrying()
    {
        var response = new ExportLogsServiceResponse
        {
            PartialSuccess = new ExportLogsPartialSuccess
            {
                RejectedLogRecords = 1,
                ErrorMessage = "bad record",
            },
        };
        var calls = 0;
        using var exporter = CreateExporter((_, _) =>
        {
            calls++;
            return Task.FromResult(ProtobufResponse(HttpStatusCode.OK, response.ToByteArray()));
        });

        var result = await exporter.ExportAsync(
            AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line),
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(AuditExportAttemptKind.Blocked, result.Kind);
        Assert.Equal(AuditExportFailureClass.PartialRejection, result.FailureClass);
        Assert.Equal("otlp.partial_rejection", result.DetailCode);
        Assert.NotNull(result.ResponseDigest);
    }

    [Theory]
    [InlineData("malformed", "application/x-protobuf")]
    [InlineData("empty", "application/json")]
    [InlineData("rejected-too-large", "application/x-protobuf")]
    [InlineData("rejected-negative", "application/x-protobuf")]
    public async Task Unknown_200_acknowledgment_is_retryable_with_the_same_record(
        string responseKind,
        string contentType)
    {
        var body = responseKind switch
        {
            "malformed" => new byte[] { 0x0a, 0x02, 0x01 },
            "rejected-too-large" => new ExportLogsServiceResponse
            {
                PartialSuccess = new ExportLogsPartialSuccess { RejectedLogRecords = 2 },
            }.ToByteArray(),
            "rejected-negative" => new ExportLogsServiceResponse
            {
                PartialSuccess = new ExportLogsPartialSuccess { RejectedLogRecords = -1 },
            }.ToByteArray(),
            _ => Array.Empty<byte>(),
        };
        using var exporter = CreateExporter((_, _) => Task.FromResult(
            Response(HttpStatusCode.OK, body, contentType)));

        var result = await exporter.ExportAsync(
            AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line),
            CancellationToken.None);

        Assert.Equal(AuditExportAttemptKind.Retry, result.Kind);
        Assert.Null(result.FailureClass);
        Assert.StartsWith("http.200.", result.DetailCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_content_type_is_safely_an_unknown_acknowledgment()
    {
        using var exporter = CreateExporter((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "not a media type");
            return Task.FromResult(response);
        });

        var result = await exporter.ExportAsync(
            AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line),
            CancellationToken.None);

        Assert.Equal(AuditExportAttemptKind.Retry, result.Kind);
        Assert.Equal("http.200.content_type", result.DetailCode);
    }

    [Theory]
    [InlineData(201, 2, 0)]
    [InlineData(202, 2, 0)]
    [InlineData(204, 2, 0)]
    [InlineData(307, 2, 0)]
    [InlineData(400, 2, 2)]
    [InlineData(401, 2, 0)]
    [InlineData(403, 2, 0)]
    [InlineData(404, 2, 0)]
    [InlineData(413, 2, 2)]
    [InlineData(418, 2, 3)]
    [InlineData(429, 1, -1)]
    [InlineData(500, 2, 3)]
    [InlineData(502, 1, -1)]
    [InlineData(503, 1, -1)]
    [InlineData(504, 1, -1)]
    [InlineData(599, 2, 3)]
    public async Task Status_matrix_is_closed_and_deterministic(
        int statusCode,
        int expectedKind,
        int expectedFailureClass)
    {
        using var exporter = CreateExporter((_, _) => Task.FromResult(
            ProtobufResponse((HttpStatusCode)statusCode, Array.Empty<byte>())));

        var result = await exporter.ExportAsync(
            AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line),
            CancellationToken.None);

        Assert.Equal((AuditExportAttemptKind)expectedKind, result.Kind);
        if (expectedFailureClass < 0) Assert.Null(result.FailureClass);
        else Assert.Equal((AuditExportFailureClass)expectedFailureClass, result.FailureClass);
        Assert.Equal($"http.{statusCode}", result.DetailCode);
    }

    [Fact]
    public async Task Retry_after_delta_seconds_is_parsed_for_retryable_status()
    {
        using var exporter = CreateExporter((_, _) =>
        {
            var response = ProtobufResponse(HttpStatusCode.ServiceUnavailable, Array.Empty<byte>());
            response.Headers.TryAddWithoutValidation("Retry-After", "17");
            return Task.FromResult(response);
        });

        var result = await exporter.ExportAsync(
            AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line),
            CancellationToken.None);

        Assert.Equal(AuditExportAttemptKind.Retry, result.Kind);
        Assert.Equal(TimeSpan.FromSeconds(17), result.RetryAfter);
    }

    [Fact]
    public async Task Retry_after_http_date_is_relative_to_the_attempt_clock_and_invalid_values_are_ignored()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var responses = 0;
        using var exporter = new AuditOtlpHttpExporter(
            Options("Bearer test-secret"),
            "9.8.7",
            new ScriptedHandler((_, _) =>
            {
                var response = ProtobufResponse(HttpStatusCode.ServiceUnavailable, Array.Empty<byte>());
                if (responses++ == 0)
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(31));
                else
                    response.Headers.TryAddWithoutValidation("Retry-After", "not-a-delay");
                return Task.FromResult(response);
            }),
            timeProvider: new FixedTimeProvider(now));
        var record = AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line);

        var dated = await exporter.ExportAsync(record, CancellationToken.None);
        var invalid = await exporter.ExportAsync(record, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(31), dated.RetryAfter);
        Assert.Null(invalid.RetryAfter);
    }

    [Fact]
    public async Task Oversized_receiver_body_is_not_buffered_or_digested_and_cannot_be_an_acknowledgment()
    {
        var stream = new CountingStream(AuditOtlpHttpExporter.MaximumResponseBytes * 8L);
        using var exporter = CreateExporter((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
            return Task.FromResult(response);
        });

        var result = await exporter.ExportAsync(
            AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line),
            CancellationToken.None);

        Assert.Equal(AuditExportAttemptKind.Retry, result.Kind);
        Assert.Equal("http.200.response_too_large", result.DetailCode);
        Assert.Null(result.ResponseDigest);
        Assert.InRange(
            stream.BytesRead,
            AuditOtlpHttpExporter.MaximumResponseBytes + 1L,
            AuditOtlpHttpExporter.MaximumResponseBytes + 8192L);
    }

    [Fact]
    public async Task Non_200_status_is_classified_without_reading_an_untrusted_error_body()
    {
        var stream = new CountingStream(AuditOtlpHttpExporter.MaximumResponseBytes * 8L);
        using var exporter = CreateExporter((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StreamContent(stream),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
            return Task.FromResult(response);
        });

        var result = await exporter.ExportAsync(
            AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line),
            CancellationToken.None);

        Assert.Equal(AuditExportAttemptKind.Blocked, result.Kind);
        Assert.Equal(AuditExportFailureClass.Configuration, result.FailureClass);
        Assert.Equal("http.401", result.DetailCode);
        Assert.Equal(0, stream.BytesRead);
        Assert.Null(result.ResponseDigest);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_while_the_internal_timeout_is_a_retryable_outcome()
    {
        var callerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerExporter = CreateExporter(async (_, cancellationToken) =>
        {
            callerStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        });
        using var callerCancellation = new CancellationTokenSource();
        var record = AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line);
        var callerAttempt = callerExporter.ExportAsync(record, callerCancellation.Token);
        await callerStarted.Task;
        callerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callerAttempt);

        using var timeoutExporter = new AuditOtlpHttpExporter(
            Options("Bearer test-secret"),
            "9.8.7",
            new ScriptedHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new UnreachableException();
            }),
            requestTimeout: TimeSpan.FromMilliseconds(20));
        var timeout = await timeoutExporter.ExportAsync(record, CancellationToken.None);
        Assert.Equal(AuditExportAttemptKind.Retry, timeout.Kind);
        Assert.Equal("transport.timeout", timeout.DetailCode);
    }

    [Fact]
    public async Task Redirect_is_not_followed_and_is_a_configuration_block()
    {
        var calls = 0;
        using var exporter = CreateExporter((_, _) =>
        {
            calls++;
            var response = ProtobufResponse(HttpStatusCode.TemporaryRedirect, Array.Empty<byte>());
            response.Headers.Location = new Uri("https://redirect-target.example/stolen");
            return Task.FromResult(response);
        });

        var result = await exporter.ExportAsync(
            AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line),
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(AuditExportAttemptKind.Blocked, result.Kind);
        Assert.Equal(AuditExportFailureClass.Configuration, result.FailureClass);
        Assert.Equal("http.307", result.DetailCode);
    }

    [Fact]
    public async Task Wrong_auth_is_sent_only_to_the_configured_anchor_and_classified_nonretryable()
    {
        var calls = 0;
        using var exporter = CreateExporter((request, _) =>
        {
            calls++;
            var status = request.Headers.Authorization?.Parameter == "expected-token"
                ? HttpStatusCode.OK
                : HttpStatusCode.Unauthorized;
            return Task.FromResult(ProtobufResponse(status, Array.Empty<byte>()));
        }, authorizationValue: "Bearer wrong-token");

        var result = await exporter.ExportAsync(
            AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line),
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(AuditExportAttemptKind.Blocked, result.Kind);
        Assert.Equal(AuditExportFailureClass.Configuration, result.FailureClass);
        Assert.Equal("http.401", result.DetailCode);
    }

    [Fact]
    public async Task Tls_authentication_failure_blocks_but_secure_handshake_transport_failure_retries()
    {
        using var tlsExporter = CreateExporter((_, _) => throw new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            "must not escape",
            new AuthenticationException("wrong ca")));
        using var handshakeTransportExporter = CreateExporter((_, _) => throw new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            "must not escape",
            new IOException("peer aborted during TLS handshake")));
        using var connectionExporter = CreateExporter((_, _) => throw new HttpRequestException("dns"));
        var record = AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line);

        var tls = await tlsExporter.ExportAsync(record, CancellationToken.None);
        var handshakeTransport = await handshakeTransportExporter.ExportAsync(
            record,
            CancellationToken.None);
        var connection = await connectionExporter.ExportAsync(record, CancellationToken.None);

        Assert.Equal(AuditExportAttemptKind.Blocked, tls.Kind);
        Assert.Equal(AuditExportFailureClass.Configuration, tls.FailureClass);
        Assert.Equal("tls.validation", tls.DetailCode);
        Assert.Equal(AuditExportAttemptKind.Retry, handshakeTransport.Kind);
        Assert.Null(handshakeTransport.FailureClass);
        Assert.Equal("transport.connection", handshakeTransport.DetailCode);
        Assert.Equal(AuditExportAttemptKind.Retry, connection.Kind);
        Assert.Null(connection.FailureClass);
        Assert.Equal("transport.connection", connection.DetailCode);
    }

    [Fact]
    public async Task Concurrent_callers_never_create_more_than_one_request_in_flight()
    {
        var active = 0;
        var maximum = 0;
        using var exporter = CreateExporter(async (_, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            try
            {
                await Task.Delay(25, cancellationToken);
                return ProtobufResponse(HttpStatusCode.OK, Array.Empty<byte>());
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        var record = AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line);

        await Task.WhenAll(
            exporter.ExportAsync(record, CancellationToken.None),
            exporter.ExportAsync(record, CancellationToken.None));

        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task Reissuing_the_same_record_after_retry_uses_identical_protobuf_bytes()
    {
        var bodies = new List<byte[]>();
        using var exporter = CreateExporter(async (request, cancellationToken) =>
        {
            bodies.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            return ProtobufResponse(
                bodies.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK,
                Array.Empty<byte>());
        });
        var record = AuditOtlpRecordMapper.Map(AuditOtlpTestRecord.Create().Utf8Line);

        var first = await exporter.ExportAsync(record, CancellationToken.None);
        var second = await exporter.ExportAsync(record, CancellationToken.None);

        Assert.Equal(AuditExportAttemptKind.Retry, first.Kind);
        Assert.Equal(AuditExportAttemptKind.Acknowledged, second.Kind);
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);
    }

    [Fact]
    public void Production_handler_disables_redirects_decompression_and_automatic_client_certificate_selection()
    {
        using var options = Options("Bearer test-secret");
        using var handler = AuditExportHttpHandlerFactory.Create(options);

        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(ClientCertificateOption.Manual, handler.ClientCertificateOptions);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void Custom_ca_is_a_replacement_trust_store_and_never_waives_name_validation()
    {
        using var trustedRoot = CreateCertificateAuthority("trusted-root");
        using var untrustedRoot = CreateCertificateAuthority("untrusted-root");
        using var trustedLeaf = CreateServerCertificate(trustedRoot, "collector.example");
        using var trustedOptions = OptionsWithCustomRoot(trustedRoot);
        using var wrongOptions = OptionsWithCustomRoot(untrustedRoot);
        using var trustedHandler = AuditExportHttpHandlerFactory.Create(trustedOptions);
        using var wrongHandler = AuditExportHttpHandlerFactory.Create(wrongOptions);
        using var policyProbe = new X509Chain();
        AuditExportHttpHandlerFactory.ConfigureCustomTrustPolicy(
            policyProbe.ChainPolicy,
            trustedOptions.CustomCertificateAuthorities,
            providedChain: null);
        var trustedValidation = Assert.IsType<Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>>(
            trustedHandler.ServerCertificateCustomValidationCallback);
        var wrongValidation = Assert.IsType<Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>>(
            wrongHandler.ServerCertificateCustomValidationCallback);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://collector.example/v1/logs");

        Assert.True(policyProbe.ChainPolicy.DisableCertificateDownloads);
        Assert.Equal(X509ChainTrustMode.CustomRootTrust, policyProbe.ChainPolicy.TrustMode);
        Assert.True(trustedValidation(
            request,
            trustedLeaf,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(trustedValidation(
            request,
            trustedLeaf,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(wrongValidation(
            request,
            trustedLeaf,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Production_handler_presents_the_startup_loaded_mtls_certificate_explicitly()
    {
        var clientCertificate = CreateCertificateAuthority("client-certificate");
        using var options = new AuditExportOptions(
            "https://collector.example:4318/custom-anchor",
            new Uri("https://collector.example:4318/custom-anchor"),
            Array.Empty<AuditExportHeader>(),
            Array.Empty<X509Certificate2>(),
            clientCertificate,
            new string('a', 64));
        using var handler = AuditExportHttpHandlerFactory.Create(options);

        var configured = Assert.Single(handler.ClientCertificates.Cast<X509Certificate2>());
        Assert.Equal(clientCertificate.Thumbprint, configured.Thumbprint);
        Assert.True(configured.HasPrivateKey);
        Assert.Equal(ClientCertificateOption.Manual, handler.ClientCertificateOptions);
    }

    private static AuditOtlpHttpExporter CreateExporter(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response,
        string authorizationValue = "Bearer test-secret") =>
        new(Options(authorizationValue), "9.8.7", new ScriptedHandler(response));

    private static AuditExportOptions Options(string authorizationValue) => new(
        "https://collector.example:4318/custom-anchor",
        new Uri("https://collector.example:4318/custom-anchor"),
        [new AuditExportHeader("Authorization", authorizationValue)],
        Array.Empty<System.Security.Cryptography.X509Certificates.X509Certificate2>(),
        null,
        new string('a', 64));

    private static AuditExportOptions OptionsWithCustomRoot(X509Certificate2 root) => new(
        "https://collector.example:4318/custom-anchor",
        new Uri("https://collector.example:4318/custom-anchor"),
        [new AuditExportHeader("Authorization", "Bearer test-secret")],
        [new X509Certificate2(root)],
        null,
        new string('a', 64));

    private static X509Certificate2 CreateCertificateAuthority(string commonName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
    }

    private static X509Certificate2 CreateServerCertificate(X509Certificate2 issuer, string dnsName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={dnsName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(dnsName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        return request.Create(
            issuer,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            serial);
    }

    private static HttpResponseMessage ProtobufResponse(HttpStatusCode status, byte[] body) =>
        Response(status, body, "application/x-protobuf");

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] body, string contentType)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(body),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => response(request, cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CountingStream : Stream
    {
        private readonly long _length;
        private long _remaining;

        internal CountingStream(long length)
        {
            _length = length;
            _remaining = length;
        }

        internal long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, _remaining);
            Array.Fill<byte>(buffer, 0x5a, offset, read);
            _remaining -= read;
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..read].Fill(0x5a);
            _remaining -= read;
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
