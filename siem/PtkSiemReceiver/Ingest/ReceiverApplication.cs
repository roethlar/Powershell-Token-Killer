using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Net.Http.Headers;
using PtkMcpServer.Audit.OtlpWire;
using PtkSiemReceiver.Configuration;
using PtkSiemReceiver.Security;

namespace PtkSiemReceiver.Ingest;

internal static class ReceiverApplication
{
    private const string ProtobufMediaType = "application/x-protobuf";
    internal const int MaximumTlsMaterialBytes = 4 * 1024 * 1024;

    internal static WebApplication Build(
        SiemReceiverOptions options,
        string[]? args = null,
        IIngestCommitter? committer = null,
        TimeProvider? timeProvider = null,
        Storage.ISqliteIngestFaultInjector? storageFaultInjector = null,
        ProtectedPathTestHooks? protectedPathTestHooks = null,
        Action? tlsMaterialAcquiredForTests = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        RejectMutableStorageCollisions(options);

        var sensitiveBuffers = new List<byte[]>();
        try
        {
            var protectedExternalIdentities = new HashSet<ProtectedPathIdentity>();
            if (options.ConfigurationIdentity is { } configurationIdentity)
                protectedExternalIdentities.Add(configurationIdentity);

            var serverCertificateRead = ReadTlsMaterial(
                options.ServerCertificatePath,
                "tls_protection",
                "server_certificate",
                sensitiveBuffers,
                protectedPathTestHooks);
            protectedExternalIdentities.Add(serverCertificateRead.Identity);
            var serverKeyRead = ReadTlsMaterial(
                options.ServerCertificateKeyPath,
                "tls_protection",
                "server_certificate",
                sensitiveBuffers,
                protectedPathTestHooks);
            protectedExternalIdentities.Add(serverKeyRead.Identity);
            var authorityReads = options.ClientCaBundlePaths
                .Select(path => ReadTlsMaterial(
                    path,
                    "tls_protection",
                    "client_ca_bundle",
                    sensitiveBuffers,
                    protectedPathTestHooks))
                .ToArray();
            foreach (var authorityRead in authorityReads)
                protectedExternalIdentities.Add(authorityRead.Identity);
            if (options.OperatorHttpsCertificatePath is not null)
            {
                var operatorCertificateRead = ReadTlsMaterial(
                    options.OperatorHttpsCertificatePath,
                    "operator_https_material",
                    "operator_https_material",
                    sensitiveBuffers,
                    protectedPathTestHooks);
                protectedExternalIdentities.Add(operatorCertificateRead.Identity);
                var operatorKeyRead = ReadTlsMaterial(
                    options.OperatorHttpsCertificateKeyPath!,
                    "operator_https_material",
                    "operator_https_material",
                    sensitiveBuffers,
                    protectedPathTestHooks);
                protectedExternalIdentities.Add(operatorKeyRead.Identity);
            }

            tlsMaterialAcquiredForTests?.Invoke();
            var serverCertificate = ReceiverCertificateLoader.LoadServerCertificate(
                serverCertificateRead.Bytes,
                serverKeyRead.Bytes);
            IReadOnlyList<X509Certificate2> clientAuthorities;
            try
            {
                clientAuthorities = ReceiverCertificateLoader.LoadAuthorities(
                    authorityReads.Select(read => read.Bytes).ToArray());
            }
            catch
            {
                serverCertificate.Dispose();
                throw;
            }
            var trustStore = new ReceiverTrustStore(clientAuthorities);
            Storage.SqliteIngestStore? ownedStore = null;
            WebApplication? application = null;
            try
            {
                if (committer is null)
                {
                    // Open and complete storage protection before Kestrel can bind.
                    ownedStore = Storage.SqliteIngestStore.Open(
                        options.SqlitePath,
                        storageFaultInjector,
                        protectedPathTestHooks,
                        protectedExternalIdentities);
                }

                var builder = WebApplication.CreateSlimBuilder(args ?? []);
                builder.WebHost.ConfigureKestrel(kestrel =>
                {
                    kestrel.AddServerHeader = false;
                    kestrel.Limits.MaxRequestBodySize = null;
                    kestrel.Listen(options.IngestBindAddress, options.IngestPort, listen =>
                    {
                        listen.Protocols = HttpProtocols.Http1AndHttp2;
                        listen.UseHttps(new HttpsConnectionAdapterOptions
                        {
                            ServerCertificate = serverCertificate,
                            ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                            ClientCertificateValidation = (certificate, chain, errors) =>
                                ClientCertificateValidator.Validate(
                                    certificate,
                                    chain,
                                    errors,
                                    trustStore.Authorities,
                                    options.RevocationCheckMode),
                            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        });
                    });
                });

                builder.Services.AddSingleton(options);
                builder.Services.AddSingleton<X509Certificate2>(_ => serverCertificate);
                builder.Services.AddSingleton<ReceiverTrustStore>(_ => trustStore);
                builder.Services.AddSingleton(timeProvider ?? TimeProvider.System);
                if (ownedStore is not null)
                {
                    builder.Services.AddSingleton<IIngestCommitter>(_ => ownedStore);
                }
                else
                {
                    builder.Services.AddSingleton(committer!);
                }
                builder.Services.AddHostedService<ReceiverLifecycleService>();

                application = builder.Build();
                // Ensure the container owns all captured disposable singletons.
                _ = application.Services.GetRequiredService<X509Certificate2>();
                _ = application.Services.GetRequiredService<ReceiverTrustStore>();
                _ = application.Services.GetRequiredService<IIngestCommitter>();
                application.MapPost("/v1/logs", HandleIngestAsync);
                return application;
            }
            catch
            {
                if (application is not null)
                {
                    application.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                else
                {
                    ownedStore?.Dispose();
                    serverCertificate.Dispose();
                    trustStore.Dispose();
                }
                throw;
            }
        }
        finally
        {
            foreach (var buffer in sensitiveBuffers)
                CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void RejectMutableStorageCollisions(SiemReceiverOptions options)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var storagePaths = new[]
        {
            options.SqlitePath,
            options.SqlitePath + "-wal",
            options.SqlitePath + "-shm",
        };
        var externalPaths = new List<string>
        {
            options.ServerCertificatePath,
            options.ServerCertificateKeyPath,
        };
        externalPaths.AddRange(options.ClientCaBundlePaths);
        if (options.ConfigurationPath is not null)
            externalPaths.Add(options.ConfigurationPath);
        if (options.OperatorHttpsCertificatePath is not null)
        {
            externalPaths.Add(options.OperatorHttpsCertificatePath);
            externalPaths.Add(options.OperatorHttpsCertificateKeyPath!);
        }

        if (externalPaths.Any(externalPath =>
                storagePaths.Any(storagePath =>
                    string.Equals(externalPath, storagePath, comparison))))
        {
            throw new SiemReceiverStartupException("protected_path_collision");
        }
    }

    private static ProtectedFileRead ReadTlsMaterial(
        string path,
        string protectionFailureCode,
        string emptyFailureCode,
        ICollection<byte[]> ownedBuffers,
        ProtectedPathTestHooks? testHooks)
    {
        ProtectedFileRead protectedRead;
        try
        {
            protectedRead = SiemProtectedPath.ReadExternalFileWithIdentity(
                path,
                MaximumTlsMaterialBytes,
                testHooks);
        }
        catch (ProtectedPathException exception)
        {
            throw new SiemReceiverStartupException(protectionFailureCode, exception);
        }

        ownedBuffers.Add(protectedRead.Bytes);
        if (protectedRead.Bytes.Length == 0)
            throw new SiemReceiverStartupException(emptyFailureCode);
        return protectedRead;
    }

    private static async Task HandleIngestAsync(
        HttpContext context,
        SiemReceiverOptions options,
        IIngestCommitter committer,
        TimeProvider timeProvider)
    {
        var receivedUtc = timeProvider.GetUtcNow();
        if (!HasExactProtobufContentType(context.Request))
        {
            await OtlpHttpResponse.WritePermanentAsync(
                context.Response,
                "content_type",
                context.RequestAborted);
            return;
        }

        var body = await ReadBoundedAsync(
            context.Request.Body,
            options.MaxRequestBytes,
            context.RequestAborted);
        if (body is null)
        {
            await OtlpHttpResponse.WritePermanentAsync(
                context.Response,
                "request_too_large",
                context.RequestAborted);
            return;
        }

        var receipt = CreateReceiptContext(context, receivedUtc);
        if (receipt is null)
        {
            await OtlpHttpResponse.WriteTransientAsync(
                context.Response,
                "connection_metadata",
                context.RequestAborted);
            return;
        }

        var validation = OtlpRequestValidator.Validate(body);
        var commitResult = validation.IsValid
            ? await InvokeCommitAsync(
                () => committer.CommitAsync(
                    validation.Record!,
                    receipt,
                    context.RequestAborted),
                context.RequestAborted)
            : await InvokeCommitAsync(
                () => committer.QuarantineAsync(
                    validation.RejectedAttempt!,
                    receipt,
                    context.RequestAborted),
                context.RequestAborted);

        await WriteCommitResultAsync(context, commitResult);
    }

    private static async Task<IngestCommitResult> InvokeCommitAsync(
        Func<Task<IngestCommitResult>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return IngestCommitResult.Transient("commit_failed");
        }
    }

    private static async Task WriteCommitResultAsync(
        HttpContext context,
        IngestCommitResult commitResult)
    {
        if (commitResult.Kind == IngestCommitResultKind.Accepted)
        {
            await OtlpHttpResponse.WriteSuccessAsync(context.Response, context.RequestAborted);
        }
        else if (commitResult.Kind == IngestCommitResultKind.PermanentFailure)
        {
            await OtlpHttpResponse.WritePermanentAsync(
                context.Response,
                commitResult.FailureCode,
                context.RequestAborted);
        }
        else
        {
            await OtlpHttpResponse.WriteTransientAsync(
                context.Response,
                commitResult.FailureCode,
                context.RequestAborted);
        }
    }

    private static IngestReceiptContext? CreateReceiptContext(
        HttpContext context,
        DateTimeOffset receivedUtc)
    {
        var certificate = context.Connection.ClientCertificate;
        var address = context.Connection.RemoteIpAddress;
        if (certificate is null || address is null) return null;

        var addressText = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
        var endpoint = $"{addressText}:{context.Connection.RemotePort}";
        var thumbprint = Convert.ToHexString(SHA256.HashData(certificate.RawData))
            .ToLowerInvariant();
        return new IngestReceiptContext(
            receivedUtc.ToUniversalTime(),
            thumbprint,
            endpoint);
    }

    private static bool HasExactProtobufContentType(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderNames.ContentType, out var values) ||
            values.Count != 1 ||
            !MediaTypeHeaderValue.TryParse(values[0], out var parsed))
        {
            return false;
        }

        return string.Equals(
                   parsed.MediaType.Value,
                   ProtobufMediaType,
                   StringComparison.OrdinalIgnoreCase) &&
               parsed.Parameters.Count == 0;
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(
                    rented.AsMemory(0, rented.Length),
                    cancellationToken);
                if (read == 0) return buffer.ToArray();
                if (buffer.Length + read > maximumBytes) return null;
                buffer.Write(rented, 0, read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}

internal interface IIngestCommitter
{
    // Compatibility seam for the isolated producer conformance fixture from S2.
    Task<IngestCommitResult> CommitAsync(
        ValidatedOtlpRecord record,
        CancellationToken cancellationToken) =>
        Task.FromException<IngestCommitResult>(
            new NotSupportedException("Receipt metadata is required by the production store."));

    Task<IngestCommitResult> CommitAsync(
        ValidatedOtlpRecord record,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken) =>
        CommitAsync(record, cancellationToken);

    Task<IngestCommitResult> QuarantineAsync(
        RejectedOtlpAttempt attempt,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken) =>
        Task.FromResult(IngestCommitResult.Permanent(attempt.FailureCode));
}

internal sealed record IngestReceiptContext(
    DateTimeOffset ReceivedUtc,
    string ClientCertificateThumbprint,
    string RemoteEndpoint);

internal enum IngestCommitResultKind
{
    Accepted,
    PermanentFailure,
    TransientFailure,
}

internal readonly record struct IngestCommitResult(
    IngestCommitResultKind Kind,
    string FailureCode)
{
    internal static IngestCommitResult Accepted() =>
        new(IngestCommitResultKind.Accepted, string.Empty);

    internal static IngestCommitResult Permanent(string failureCode) =>
        new(IngestCommitResultKind.PermanentFailure, failureCode);

    internal static IngestCommitResult Transient(string failureCode) =>
        new(IngestCommitResultKind.TransientFailure, failureCode);
}
