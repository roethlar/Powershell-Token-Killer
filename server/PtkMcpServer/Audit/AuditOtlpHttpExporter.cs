using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using PtkMcpServer.Audit.OtlpWire;

namespace PtkMcpServer.Audit;

internal enum AuditExportAttemptKind
{
    Acknowledged,
    Retry,
    Blocked,
}

internal sealed class AuditExportAttemptResult
{
    private AuditExportAttemptResult(
        AuditExportAttemptKind kind,
        AuditExportFailureClass? failureClass,
        string detailCode,
        string? responseDigest,
        TimeSpan? retryAfter,
        bool hasHealthWarning)
    {
        Kind = kind;
        FailureClass = failureClass;
        DetailCode = detailCode;
        ResponseDigest = responseDigest;
        RetryAfter = retryAfter;
        HasHealthWarning = hasHealthWarning;
    }

    internal AuditExportAttemptKind Kind { get; }

    internal AuditExportFailureClass? FailureClass { get; }

    internal string DetailCode { get; }

    internal string? ResponseDigest { get; }

    internal TimeSpan? RetryAfter { get; }

    internal bool HasHealthWarning { get; }

    internal static AuditExportAttemptResult Acknowledged(string responseDigest, bool warning) =>
        new(
            AuditExportAttemptKind.Acknowledged,
            null,
            warning ? "otlp.acknowledged_warning" : "otlp.acknowledged",
            responseDigest,
            null,
            warning);

    internal static AuditExportAttemptResult Retry(
        string detailCode,
        string? responseDigest = null,
        TimeSpan? retryAfter = null) =>
        new(AuditExportAttemptKind.Retry, null, detailCode, responseDigest, retryAfter, false);

    internal static AuditExportAttemptResult Blocked(
        AuditExportFailureClass failureClass,
        string detailCode,
        string? responseDigest = null) =>
        new(AuditExportAttemptKind.Blocked, failureClass, detailCode, responseDigest, null, false);
}

internal interface IAuditOtlpExportTransport
{
    string ConfigurationIdentity { get; }

    Task<AuditExportAttemptResult> ExportAsync(
        AuditOtlpRecord record,
        CancellationToken cancellationToken);
}

/// <summary>
/// Performs one bounded OTLP/HTTP attempt for one immutable mapped record.
/// Retry scheduling, checkpointing, retention, and operator disposition live
/// above this transport and are intentionally not coupled to it.
/// </summary>
internal sealed class AuditOtlpHttpExporter : IAuditOtlpExportTransport, IDisposable
{
    internal const int MaximumResponseBytes = 64 * 1024;

    private const string ProtobufMediaType = "application/x-protobuf";
    private readonly AuditExportOptions _options;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly string _userAgent;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    internal AuditOtlpHttpExporter(
        AuditExportOptions options,
        string applicationVersion,
        HttpMessageHandler handler,
        TimeSpan? requestTimeout = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        if (!IsHttpToken(applicationVersion))
            throw new ArgumentException("The exporter application version is invalid.", nameof(applicationVersion));
        if (requestTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));

        _options = options;
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _userAgent = $"PowerShell-Token-Killer/{applicationVersion} OTLP-HTTP-dotnet/1";
        _requestTimeout = requestTimeout ?? AuditExportOptions.RequestTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal static AuditOtlpHttpExporter Create(
        AuditExportOptions options,
        string applicationVersion) =>
        new(options, applicationVersion, AuditExportHttpHandlerFactory.Create(options));

    internal async Task<AuditExportAttemptResult> ExportAsync(
        AuditOtlpRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var request = CreateRequest(record);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            try
            {
                using var response = await _client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token)
                    .ConfigureAwait(false);
                if ((int)response.StatusCode != 200)
                {
                    // The frozen disposition is determined entirely by a
                    // non-200 status. Do not let an untrusted error body delay
                    // or alter that disposition, and do not buffer it merely
                    // to populate the checkpoint's optional response digest.
                    return ClassifyStatus(response, responseDigest: null);
                }
                var responseBody = await ReadBoundedAsync(response.Content, timeout.Token)
                    .ConfigureAwait(false);
                if (responseBody.TooLarge)
                {
                    return AuditExportAttemptResult.Retry("http.200.response_too_large");
                }

                var digest = LowerSha256(responseBody.Bytes);
                return ClassifyOk(response, responseBody.Bytes, digest);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return AuditExportAttemptResult.Retry("transport.timeout");
            }
            catch (HttpRequestException exception) when (HasAuthenticationFailure(exception))
            {
                return AuditExportAttemptResult.Blocked(
                    AuditExportFailureClass.Configuration,
                    "tls.validation");
            }
            catch (AuthenticationException)
            {
                return AuditExportAttemptResult.Blocked(
                    AuditExportFailureClass.Configuration,
                    "tls.validation");
            }
            catch (HttpRequestException)
            {
                return AuditExportAttemptResult.Retry("transport.connection");
            }
            catch (IOException)
            {
                return AuditExportAttemptResult.Retry("transport.connection");
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    Task<AuditExportAttemptResult> IAuditOtlpExportTransport.ExportAsync(
        AuditOtlpRecord record,
        CancellationToken cancellationToken) =>
        ExportAsync(record, cancellationToken);

    string IAuditOtlpExportTransport.ConfigurationIdentity =>
        _options.ConfigurationIdentity;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _client.Dispose();
        _options.Dispose();
    }

    private HttpRequestMessage CreateRequest(AuditOtlpRecord record)
    {
        var content = new ByteArrayContent(record.RequestBytes.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue(ProtobufMediaType);
        var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = content,
        };
        if (!request.Headers.TryAddWithoutValidation("User-Agent", _userAgent))
        {
            request.Dispose();
            throw new InvalidOperationException("The fixed audit exporter user agent is invalid.");
        }

        foreach (var header in _options.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value) &&
                !content.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                request.Dispose();
                throw new InvalidOperationException("A validated audit exporter header could not be represented.");
            }
        }
        return request;
    }

    private static AuditExportAttemptResult ClassifyOk(
        HttpResponseMessage response,
        byte[] responseBody,
        string responseDigest)
    {
        if (!HasExactProtobufContentType(response.Content.Headers))
        {
            return AuditExportAttemptResult.Retry("http.200.content_type", responseDigest);
        }

        ExportLogsServiceResponse decoded;
        try
        {
            decoded = ExportLogsServiceResponse.Parser.ParseFrom(responseBody);
        }
        catch (InvalidProtocolBufferException)
        {
            return AuditExportAttemptResult.Retry("http.200.protobuf", responseDigest);
        }

        var partial = decoded.PartialSuccess;
        if (partial is null)
            return AuditExportAttemptResult.Acknowledged(responseDigest, warning: false);
        return partial.RejectedLogRecords switch
        {
            0 => AuditExportAttemptResult.Acknowledged(
                responseDigest,
                warning: partial.ErrorMessage.Length != 0),
            1 => AuditExportAttemptResult.Blocked(
                AuditExportFailureClass.PartialRejection,
                "otlp.partial_rejection",
                responseDigest),
            _ => AuditExportAttemptResult.Retry("http.200.rejected_count", responseDigest),
        };
    }

    private AuditExportAttemptResult ClassifyStatus(
        HttpResponseMessage response,
        string? responseDigest)
    {
        var statusCode = (int)response.StatusCode;
        var detailCode = $"http.{statusCode.ToString(CultureInfo.InvariantCulture)}";
        if (statusCode is 429 or 502 or 503 or 504)
        {
            return AuditExportAttemptResult.Retry(
                detailCode,
                responseDigest,
                ParseRetryAfter(response.Headers));
        }
        if (statusCode is 401 or 403 or 404 || statusCode is >= 200 and <= 399)
        {
            return AuditExportAttemptResult.Blocked(
                AuditExportFailureClass.Configuration,
                detailCode,
                responseDigest);
        }
        if (statusCode is 400 or 413)
        {
            return AuditExportAttemptResult.Blocked(
                AuditExportFailureClass.Data,
                detailCode,
                responseDigest);
        }
        return AuditExportAttemptResult.Blocked(
            AuditExportFailureClass.Protocol,
            detailCode,
            responseDigest);
    }

    private TimeSpan? ParseRetryAfter(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Retry-After", out var values)) return null;
        var materialized = values.Take(2).ToArray();
        if (materialized.Length != 1 ||
            !RetryConditionHeaderValue.TryParse(materialized[0], out var retryAfter))
        {
            return null;
        }
        if (retryAfter?.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retryAfter?.Date is not { } date) return null;
        var delay = date - _timeProvider.GetUtcNow();
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    private static bool HasExactProtobufContentType(HttpContentHeaders headers)
    {
        if (!headers.TryGetValues("Content-Type", out var values)) return false;
        var materialized = values.Take(2).ToArray();
        return materialized.Length == 1 &&
               MediaTypeHeaderValue.TryParse(materialized[0], out var parsed) &&
               string.Equals(parsed.MediaType, ProtobufMediaType, StringComparison.OrdinalIgnoreCase) &&
               parsed.Parameters.Count == 0;
    }

    private static async Task<BoundedResponse> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(MaximumResponseBytes, 16 * 1024));
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) return new BoundedResponse(buffer.ToArray(), TooLarge: false);
                if (buffer.Length + read > MaximumResponseBytes)
                    return new BoundedResponse(Array.Empty<byte>(), TooLarge: true);
                buffer.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static bool HasAuthenticationFailure(HttpRequestException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException) return true;
        }
        return false;
    }

    private static string LowerSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsHttpToken(string value) =>
        value.Length != 0 && value.All(character =>
            character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') ||
            character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~');

    private readonly record struct BoundedResponse(byte[] Bytes, bool TooLarge);
}

internal static class AuditExportHttpHandlerFactory
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    internal static HttpClientHandler Create(AuditExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ClientCertificateOptions = ClientCertificateOption.Manual,
            UseCookies = false,
        };
        if (options.ClientCertificate is not null)
            handler.ClientCertificates.Add(options.ClientCertificate);
        if (options.CustomCertificateAuthorities.Count != 0)
        {
            handler.ServerCertificateCustomValidationCallback = (_, certificate, providedChain, errors) =>
                ValidateWithCustomTrust(options.CustomCertificateAuthorities, certificate, providedChain, errors);
        }
        return handler;
    }

    private static bool ValidateWithCustomTrust(
        IReadOnlyList<X509Certificate2> customRoots,
        X509Certificate2? certificate,
        X509Chain? providedChain,
        SslPolicyErrors errors)
    {
        if (certificate is null ||
            (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var chain = new X509Chain();
        ConfigureCustomTrustPolicy(chain.ChainPolicy, customRoots, providedChain);
        return chain.Build(certificate);
    }

    internal static void ConfigureCustomTrustPolicy(
        X509ChainPolicy policy,
        IReadOnlyList<X509Certificate2> customRoots,
        X509Chain? providedChain)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(customRoots);
        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        policy.RevocationMode = X509RevocationMode.NoCheck;
        policy.VerificationFlags = X509VerificationFlags.NoFlag;
        policy.DisableCertificateDownloads = true;
        policy.ApplicationPolicy.Add(new Oid(ServerAuthenticationOid));
        foreach (var root in customRoots) policy.CustomTrustStore.Add(root);
        if (providedChain is not null)
        {
            foreach (var element in providedChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                policy.ExtraStore.Add(element.Certificate);
        }
    }
}
