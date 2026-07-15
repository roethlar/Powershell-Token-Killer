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

namespace PtkSiemReceiver.Ingest;

internal static class ReceiverApplication
{
    private const string ProtobufMediaType = "application/x-protobuf";

    internal static WebApplication Build(
        SiemReceiverOptions options,
        string[]? args = null,
        IIngestCommitter? committer = null,
        TimeProvider? timeProvider = null,
        Storage.ISqliteIngestFaultInjector? storageFaultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var serverCertificate = ReceiverCertificateLoader.LoadServerCertificate(
            options.ServerCertificatePath,
            options.ServerCertificateKeyPath);
        var clientAuthorities = ReceiverCertificateLoader.LoadAuthorities(
            options.ClientCaBundlePaths);
        var trustStore = new ReceiverTrustStore(clientAuthorities);

        try
        {
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
            builder.Services.AddSingleton(serverCertificate);
            builder.Services.AddSingleton(trustStore);
            builder.Services.AddSingleton(timeProvider ?? TimeProvider.System);
            if (committer is null)
            {
                builder.Services.AddSingleton<IIngestCommitter>(serviceProvider =>
                    Storage.SqliteIngestStore.Open(
                        serviceProvider.GetRequiredService<SiemReceiverOptions>().SqlitePath,
                        storageFaultInjector));
            }
            else
            {
                builder.Services.AddSingleton(committer);
            }
            builder.Services.AddHostedService<ReceiverLifecycleService>();

            var application = builder.Build();
            application.MapPost("/v1/logs", HandleIngestAsync);
            return application;
        }
        catch
        {
            serverCertificate.Dispose();
            trustStore.Dispose();
            throw;
        }
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
    Task<IngestCommitResult> CommitAsync(
        ValidatedOtlpRecord record,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken);

    Task<IngestCommitResult> QuarantineAsync(
        RejectedOtlpAttempt attempt,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken);
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
