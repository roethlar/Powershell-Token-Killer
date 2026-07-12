using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using PtkMcpServer.Audit.OtlpWire;

namespace PtkMcpServer.Tests;

internal enum FakeOtlpResponseMode
{
    Success,
    ServiceUnavailable,
    Redirect,
    PersistThenDisconnect,
    PartialRejection,
    BadRequest,
    ZeroRejectionWarning,
}

internal sealed record FakeOtlpReceipt(byte[] ExactJsonBody, string EventId);

internal sealed record FakeOtlpRequestSnapshot(string Target, byte[] Body);

internal sealed record FakeOtlpResponseSnapshot(
    int StatusCode,
    string ContentType,
    byte[] Body,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Test-only CA and IP-address leaf certificate. The leaf deliberately has no
/// DNS SAN so the integration suite can exercise real hostname validation.
/// </summary>
internal sealed class FakeOtlpPki : IDisposable
{
    private FakeOtlpPki(X509Certificate2 certificateAuthority, X509Certificate2 serverCertificate)
    {
        CertificateAuthority = certificateAuthority;
        ServerCertificate = serverCertificate;
    }

    internal X509Certificate2 CertificateAuthority { get; }

    internal X509Certificate2 ServerCertificate { get; }

    internal static FakeOtlpPki Create()
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest(
            new X500DistinguishedName($"CN=ptk-fake-otlp-root-{Guid.NewGuid():N}"),
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        rootRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        var root = rootRequest.CreateSelfSigned(now.AddHours(-1), now.AddDays(2));

        try
        {
            using var leafKey = RSA.Create(2048);
            var leafRequest = new CertificateRequest(
                new X500DistinguishedName("CN=ptk-fake-otlp-receiver"),
                leafKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
            leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") },
                true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            leafRequest.CertificateExtensions.Add(names.Build());
            leafRequest.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, false));

            using var issued = leafRequest.Create(
                root,
                now.AddMinutes(-5),
                now.AddDays(1),
                RandomNumberGenerator.GetBytes(16));
            using var withPrivateKey = issued.CopyWithPrivateKey(leafKey);
            var pkcs12 = withPrivateKey.Export(X509ContentType.Pkcs12, string.Empty);
            try
            {
                var server = X509CertificateLoader.LoadPkcs12(
                    pkcs12,
                    string.Empty,
                    X509KeyStorageFlags.DefaultKeySet);
                return new FakeOtlpPki(root, server);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    internal X509Certificate2 CreateTrustedRoot() =>
        X509CertificateLoader.LoadCertificate(CertificateAuthority.RawData);

    public void Dispose()
    {
        ServerCertificate.Dispose();
        CertificateAuthority.Dispose();
    }
}

/// <summary>
/// A deliberately small, bounded HTTPS/1.1 OTLP receiver used to exercise the
/// production exporter and production TLS handler over a real loopback socket.
/// </summary>
internal sealed class FakeOtlpHttpsReceiver : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 32 * 1024;
    private const int MaximumHeaders = 64;
    private const int MaximumRequestBytes = 256 * 1024;
    private const int MaximumJsonBodyBytes = 65_535;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] ReceiptMagic = "PTKR"u8.ToArray();

    private readonly TcpListener _listener;
    private readonly X509Certificate2 _serverCertificate;
    private readonly FakeOtlpResponseMode[] _responseModes;
    private readonly Uri? _redirectLocation;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private readonly string _receiptRoot;
    private readonly TaskCompletionSource _receiptFlushPending =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _receiptFlushRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _responseStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _blockReceiptFlush;
    private readonly object _requestGate = new();
    private readonly List<FakeOtlpRequestSnapshot> _requests = [];
    private int _requestCount;
    private int _responseModeIndex;
    private int _disposed;

    internal FakeOtlpHttpsReceiver(
        FakeOtlpPki pki,
        FakeOtlpResponseMode responseMode = FakeOtlpResponseMode.Success,
        Uri? redirectLocation = null,
        bool blockReceiptFlush = false)
        : this(pki, [responseMode], redirectLocation, blockReceiptFlush)
    {
    }

    internal FakeOtlpHttpsReceiver(
        FakeOtlpPki pki,
        IReadOnlyList<FakeOtlpResponseMode> responseModes,
        bool blockReceiptFlush = false)
        : this(pki, responseModes, redirectLocation: null, blockReceiptFlush)
    {
    }

    private FakeOtlpHttpsReceiver(
        FakeOtlpPki pki,
        IReadOnlyList<FakeOtlpResponseMode> responseModes,
        Uri? redirectLocation,
        bool blockReceiptFlush)
    {
        ArgumentNullException.ThrowIfNull(pki);
        ArgumentNullException.ThrowIfNull(responseModes);
        if (responseModes.Count == 0)
            throw new ArgumentException("At least one fake OTLP response mode is required.", nameof(responseModes));
        if (responseModes.Any(mode => !Enum.IsDefined(mode)))
            throw new ArgumentException("A fake OTLP response mode is invalid.", nameof(responseModes));
        if ((responseModes.Contains(FakeOtlpResponseMode.Redirect)) != (redirectLocation is not null))
            throw new ArgumentException("Redirect mode requires exactly one redirect location.");
        if (redirectLocation is not null && redirectLocation.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The fake redirect target must use HTTPS.", nameof(redirectLocation));

        _serverCertificate = pki.ServerCertificate;
        _responseModes = responseModes.ToArray();
        _redirectLocation = redirectLocation;
        _blockReceiptFlush = blockReceiptFlush;
        AuthorizationValue = $"Bearer {Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        _receiptRoot = Path.Combine(Path.GetTempPath(), $"ptk-fake-otlp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_receiptRoot);
        ReceiptPath = Path.Combine(_receiptRoot, "durable-receipts.bin");
        using (var empty = new FileStream(
                   ReceiptPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.Read,
                   1,
                   FileOptions.WriteThrough))
        {
            empty.Flush(flushToDisk: true);
        }

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(8);
        var local = (IPEndPoint)_listener.LocalEndpoint;
        Endpoint = new Uri($"https://127.0.0.1:{local.Port.ToString(CultureInfo.InvariantCulture)}/v1/logs");
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    internal Uri Endpoint { get; }

    internal string AuthorizationValue { get; }

    internal string ReceiptPath { get; }

    internal int RequestCount => Volatile.Read(ref _requestCount);

    internal IReadOnlyList<FakeOtlpRequestSnapshot> Requests
    {
        get
        {
            lock (_requestGate)
            {
                return _requests
                    .Select(request => new FakeOtlpRequestSnapshot(
                        request.Target,
                        request.Body.ToArray()))
                    .ToArray();
            }
        }
    }

    internal string? LastRequestTarget { get; private set; }

    internal byte[]? LastRequestBody { get; private set; }

    internal FakeOtlpResponseSnapshot? LastResponse { get; private set; }

    internal bool ResponseStarted => _responseStarted.Task.IsCompleted;

    internal Task WaitForReceiptFlushPendingAsync(CancellationToken cancellationToken) =>
        _receiptFlushPending.Task.WaitAsync(cancellationToken);

    internal void ReleaseReceiptFlush() => _receiptFlushRelease.TrySetResult();

    internal IReadOnlyList<FakeOtlpReceipt> ReadDurableReceipts()
    {
        using var stream = new FileStream(
            ReceiptPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        var receipts = new List<FakeOtlpReceipt>();
        while (stream.Position != stream.Length)
        {
            var magic = reader.ReadBytes(ReceiptMagic.Length);
            if (!magic.AsSpan().SequenceEqual(ReceiptMagic))
                throw new InvalidDataException("The fake OTLP receipt magic is invalid.");
            if (reader.ReadByte() != 1)
                throw new InvalidDataException("The fake OTLP receipt version is invalid.");
            var lengthBytes = reader.ReadBytes(sizeof(uint));
            if (lengthBytes.Length != sizeof(uint))
                throw new EndOfStreamException();
            var jsonLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(lengthBytes));
            if (jsonLength is < 1 or > MaximumJsonBodyBytes)
                throw new InvalidDataException("The fake OTLP receipt JSON length is invalid.");
            var json = reader.ReadBytes(jsonLength);
            if (json.Length != jsonLength)
                throw new EndOfStreamException();
            var eventIdLength = reader.ReadByte();
            if (eventIdLength != 36)
                throw new InvalidDataException("The fake OTLP receipt event ID length is invalid.");
            var eventIdBytes = reader.ReadBytes(eventIdLength);
            if (eventIdBytes.Length != eventIdLength)
                throw new EndOfStreamException();
            receipts.Add(new FakeOtlpReceipt(json, StrictUtf8.GetString(eventIdBytes)));
        }
        return receipts;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _receiptFlushRelease.TrySetResult();
        _shutdown.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            _shutdown.Dispose();
            try
            {
                Directory.Delete(_receiptRoot, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }

            using (client)
            {
                await HandleClientAsync(client, _shutdown.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        await using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            await stream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = _serverCertificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AuthenticationException)
        {
            return;
        }
        catch (IOException) when (!cancellationToken.IsCancellationRequested)
        {
            // A client that rejects the CA or hostname aborts its side of the
            // handshake. That is an expected integration outcome, not a fake
            // receiver fault.
            return;
        }

        FakeHttpRequest request;
        try
        {
            request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (FakeHttpProtocolException)
        {
            // A client that rejects our certificate can complete the server
            // side of TLS and then close before sending any HTTP bytes. Such a
            // connection is not an observed request and cannot receive a 400.
            try
            {
                await SendFailureAsync(stream, 400, "bad data", cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            return;
        }

        Interlocked.Increment(ref _requestCount);
        LastRequestTarget = request.Target;
        LastRequestBody = request.Body.ToArray();
        lock (_requestGate)
        {
            _requests.Add(new FakeOtlpRequestSnapshot(
                request.Target,
                request.Body.ToArray()));
        }
        if (!string.Equals(request.Target, "/v1/logs", StringComparison.Ordinal))
        {
            await SendFailureAsync(stream, 404, "not found", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!HasExpectedAuthorization(request.Authorization))
        {
            await SendFailureAsync(
                stream,
                401,
                "unauthenticated",
                cancellationToken,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["WWW-Authenticate"] = "Bearer",
                }).ConfigureAwait(false);
            return;
        }

        var responseMode = NextResponseMode();
        if (responseMode == FakeOtlpResponseMode.ServiceUnavailable)
        {
            await SendFailureAsync(
                stream,
                503,
                "unavailable",
                cancellationToken,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Retry-After"] = "1",
                }).ConfigureAwait(false);
            return;
        }
        if (responseMode == FakeOtlpResponseMode.Redirect)
        {
            await SendAsync(
                stream,
                307,
                Array.Empty<byte>(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Location"] = _redirectLocation!.AbsoluteUri,
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }
        if (responseMode == FakeOtlpResponseMode.BadRequest)
        {
            await SendFailureAsync(
                stream,
                400,
                "bad data",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        FakeOtlpReceipt receipt;
        try
        {
            receipt = DecodeSingleRecord(request.Body);
        }
        catch (Exception exception) when (
            exception is InvalidProtocolBufferException or
                InvalidDataException or
                JsonException or
                DecoderFallbackException)
        {
            await SendFailureAsync(stream, 400, "bad data", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (responseMode == FakeOtlpResponseMode.PartialRejection)
        {
            await SendAsync(
                stream,
                200,
                new ExportLogsServiceResponse
                {
                    PartialSuccess = new ExportLogsPartialSuccess
                    {
                        RejectedLogRecords = 1,
                        ErrorMessage = "bad record",
                    },
                }.ToByteArray(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await PersistReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
        if (responseMode == FakeOtlpResponseMode.PersistThenDisconnect)
            return;

        var response = responseMode == FakeOtlpResponseMode.ZeroRejectionWarning
            ? new ExportLogsServiceResponse
            {
                PartialSuccess = new ExportLogsPartialSuccess
                {
                    RejectedLogRecords = 0,
                    ErrorMessage = "receiver warning",
                },
            }
            : new ExportLogsServiceResponse();
        await SendAsync(
            stream,
            200,
            response.ToByteArray(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);
    }

    private FakeOtlpResponseMode NextResponseMode()
    {
        var index = Interlocked.Increment(ref _responseModeIndex) - 1;
        return _responseModes[Math.Min(index, _responseModes.Length - 1)];
    }

    private bool HasExpectedAuthorization(string? actual)
    {
        if (actual is null) return false;
        var expectedBytes = Encoding.ASCII.GetBytes(AuthorizationValue);
        var actualBytes = Encoding.ASCII.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private async Task PersistReceiptAsync(FakeOtlpReceipt receipt, CancellationToken cancellationToken)
    {
        var eventIdBytes = StrictUtf8.GetBytes(receipt.EventId);
        if (eventIdBytes.Length != 36)
            throw new InvalidDataException("The fake OTLP event ID is not canonical.");
        var prefix = new byte[ReceiptMagic.Length + 1 + sizeof(uint)];
        ReceiptMagic.CopyTo(prefix, 0);
        prefix[ReceiptMagic.Length] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(
            prefix.AsSpan(ReceiptMagic.Length + 1),
            checked((uint)receipt.ExactJsonBody.Length));

        await using var output = new FileStream(
            ReceiptPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(receipt.ExactJsonBody, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(new byte[] { (byte)eventIdBytes.Length }, cancellationToken)
            .ConfigureAwait(false);
        await output.WriteAsync(eventIdBytes, cancellationToken).ConfigureAwait(false);

        if (_blockReceiptFlush)
        {
            _receiptFlushPending.TrySetResult();
            await _receiptFlushRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        // The success response is deliberately below this synchronous durable
        // flush. A 200 from this fake therefore means the exact decoded JSON
        // body and event ID have reached its receipt boundary first.
        output.Flush(flushToDisk: true);
    }

    private static FakeOtlpReceipt DecodeSingleRecord(ReadOnlyMemory<byte> body)
    {
        var request = ExportLogsServiceRequest.Parser.ParseFrom(body.Span);
        if (request.ResourceLogs.Count != 1 ||
            request.ResourceLogs[0].ScopeLogs.Count != 1 ||
            request.ResourceLogs[0].ScopeLogs[0].LogRecords.Count != 1)
        {
            throw new InvalidDataException("The fake receiver requires exactly one OTLP log record.");
        }

        var record = request.ResourceLogs[0].ScopeLogs[0].LogRecords[0];
        if (record.Body.ValueCase != AnyValue.ValueOneofCase.StringValue)
            throw new InvalidDataException("The fake receiver requires a string log body.");
        var jsonBytes = StrictUtf8.GetBytes(record.Body.StringValue);
        if (jsonBytes.Length is < 1 or > MaximumJsonBodyBytes)
            throw new InvalidDataException("The fake receiver JSON body is outside its bound.");

        var eventIdAttributes = record.Attributes
            .Where(attribute => string.Equals(
                attribute.Key,
                "ptk.audit.event_id",
                StringComparison.Ordinal))
            .ToArray();
        if (eventIdAttributes.Length != 1 ||
            eventIdAttributes[0].Value.ValueCase != AnyValue.ValueOneofCase.StringValue)
        {
            throw new InvalidDataException("The fake receiver requires one string event ID attribute.");
        }
        var eventId = eventIdAttributes[0].Value.StringValue;
        if (!Guid.TryParseExact(eventId, "D", out var parsedEventId) ||
            !string.Equals(eventId, parsedEventId.ToString("D"), StringComparison.Ordinal) ||
            eventId[14] != '7' ||
            eventId[19] is not ('8' or '9' or 'a' or 'b'))
        {
            throw new InvalidDataException("The fake receiver event ID is not a canonical UUIDv7.");
        }

        using var document = JsonDocument.Parse(
            jsonBytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The fake receiver JSON body is not an object.");
        var bodyEventIds = document.RootElement.EnumerateObject()
            .Where(property => property.NameEquals("event_id"))
            .ToArray();
        if (bodyEventIds.Length != 1 ||
            bodyEventIds[0].Value.ValueKind != JsonValueKind.String ||
            !string.Equals(bodyEventIds[0].Value.GetString(), eventId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The fake receiver body and attribute event IDs differ.");
        }
        return new FakeOtlpReceipt(jsonBytes, eventId);
    }

    private static async Task<FakeHttpRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>(512);
        var terminatorState = 0;
        var one = new byte[1];
        while (terminatorState != 4)
        {
            if (headerBytes.Count == MaximumHeaderBytes)
                throw new FakeHttpProtocolException();
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) != 1)
                throw new FakeHttpProtocolException();
            var value = one[0];
            if (value > 0x7f)
                throw new FakeHttpProtocolException();
            headerBytes.Add(value);
            terminatorState = (terminatorState, value) switch
            {
                (0, (byte)'\r') => 1,
                (1, (byte)'\n') => 2,
                (2, (byte)'\r') => 3,
                (3, (byte)'\n') => 4,
                (_, (byte)'\r') => 1,
                _ => 0,
            };
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var lines = headerText[..^4].Split("\r\n", StringSplitOptions.None);
        if (lines.Length < 2 || lines.Length > MaximumHeaders + 1)
            throw new FakeHttpProtocolException();
        var requestParts = lines[0].Split(' ', StringSplitOptions.None);
        if (requestParts.Length != 3 ||
            requestParts[0] != "POST" ||
            requestParts[2] != "HTTP/1.1" ||
            requestParts[1].Length == 0)
        {
            throw new FakeHttpProtocolException();
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < lines.Length; index++)
        {
            var separator = lines[index].IndexOf(':');
            if (separator <= 0)
                throw new FakeHttpProtocolException();
            var name = lines[index][..separator];
            if (!IsHttpToken(name) || !headers.TryAdd(name, lines[index][(separator + 1)..].Trim()))
                throw new FakeHttpProtocolException();
        }

        if (headers.ContainsKey("Transfer-Encoding") ||
            !headers.TryGetValue("Content-Length", out var contentLengthText) ||
            !int.TryParse(contentLengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var contentLength) ||
            contentLength is < 1 or > MaximumRequestBytes ||
            !headers.TryGetValue("Content-Type", out var contentType) ||
            !string.Equals(contentType, "application/x-protobuf", StringComparison.OrdinalIgnoreCase))
        {
            throw new FakeHttpProtocolException();
        }

        var body = new byte[contentLength];
        var offset = 0;
        while (offset != body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new FakeHttpProtocolException();
            offset += read;
        }
        headers.TryGetValue("Authorization", out var authorization);
        return new FakeHttpRequest(requestParts[1], authorization, body);
    }

    private static bool IsHttpToken(string value) =>
        value.Length != 0 && value.All(character =>
            character is >= '0' and <= '9' or
                >= 'A' and <= 'Z' or
                >= 'a' and <= 'z' or
                '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~');

    private async Task SendFailureAsync(
        Stream stream,
        int statusCode,
        string message,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null) =>
        await SendAsync(
            stream,
            statusCode,
            EncodeGoogleStatus(message),
            headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);

    private async Task SendAsync(
        Stream stream,
        int statusCode,
        byte[] body,
        IReadOnlyDictionary<string, string> extraHeaders,
        CancellationToken cancellationToken)
    {
        _responseStarted.TrySetResult();
        var reason = statusCode switch
        {
            200 => "OK",
            307 => "Temporary Redirect",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            503 => "Service Unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(statusCode)),
        };
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: application/x-protobuf\r\n")
            .Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Connection: close\r\n");
        foreach (var header in extraHeaders)
            builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        builder.Append("\r\n");
        var responseHeaders = Encoding.ASCII.GetBytes(builder.ToString());
        LastResponse = new FakeOtlpResponseSnapshot(
            statusCode,
            "application/x-protobuf",
            body.ToArray(),
            new Dictionary<string, string>(extraHeaders, StringComparer.OrdinalIgnoreCase));
        await stream.WriteAsync(responseHeaders, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static byte[] EncodeGoogleStatus(string message)
    {
        using var output = new MemoryStream();
        var protobuf = new CodedOutputStream(output, leaveOpen: true);
        protobuf.WriteTag(2, WireFormat.WireType.LengthDelimited);
        protobuf.WriteString(message);
        protobuf.Flush();
        return output.ToArray();
    }

    private sealed record FakeHttpRequest(string Target, string? Authorization, byte[] Body);

    private sealed class FakeHttpProtocolException : Exception;
}
