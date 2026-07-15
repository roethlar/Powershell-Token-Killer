using System.Net;
using System.Security.Authentication;
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
        IIngestCommitter? committer = null)
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
            builder.Services.AddSingleton<IIngestCommitter>(
                committer ?? UnavailableIngestCommitter.Instance);
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
        IIngestCommitter committer)
    {
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

        var validation = OtlpRequestValidator.Validate(body);
        if (!validation.IsValid)
        {
            await OtlpHttpResponse.WritePermanentAsync(
                context.Response,
                validation.FailureCode!,
                context.RequestAborted);
            return;
        }

        IngestCommitResult commitResult;
        try
        {
            commitResult = await committer.CommitAsync(
                validation.Record!,
                context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            commitResult = IngestCommitResult.Transient("commit_failed");
        }

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
        CancellationToken cancellationToken);
}

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

internal sealed class UnavailableIngestCommitter : IIngestCommitter
{
    internal static UnavailableIngestCommitter Instance { get; } = new();

    private UnavailableIngestCommitter()
    {
    }

    public Task<IngestCommitResult> CommitAsync(
        ValidatedOtlpRecord record,
        CancellationToken cancellationToken) =>
        Task.FromResult(IngestCommitResult.Transient("storage_not_ready"));
}
