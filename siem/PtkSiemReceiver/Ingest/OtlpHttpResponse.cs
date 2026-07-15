using Google.Protobuf;
using PtkMcpServer.Audit.OtlpWire;

namespace PtkSiemReceiver.Ingest;

internal static class OtlpHttpResponse
{
    private const string ProtobufMediaType = "application/x-protobuf";

    internal static Task WriteSuccessAsync(
        HttpResponse response,
        CancellationToken cancellationToken) =>
        WriteAsync(
            response,
            StatusCodes.Status200OK,
            new ExportLogsServiceResponse().ToByteArray(),
            retryAfter: null,
            cancellationToken);

    internal static Task WritePermanentAsync(
        HttpResponse response,
        string failureCode,
        CancellationToken cancellationToken) =>
        WriteAsync(
            response,
            StatusCodes.Status400BadRequest,
            EncodeGoogleStatus(3, failureCode),
            retryAfter: null,
            cancellationToken);

    internal static Task WriteTransientAsync(
        HttpResponse response,
        string failureCode,
        CancellationToken cancellationToken) =>
        WriteAsync(
            response,
            StatusCodes.Status503ServiceUnavailable,
            EncodeGoogleStatus(14, failureCode),
            retryAfter: "1",
            cancellationToken);

    private static async Task WriteAsync(
        HttpResponse response,
        int statusCode,
        byte[] body,
        string? retryAfter,
        CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        response.ContentType = ProtobufMediaType;
        response.ContentLength = body.Length;
        if (retryAfter is not null) response.Headers.RetryAfter = retryAfter;
        await response.Body.WriteAsync(body, cancellationToken);
    }

    private static byte[] EncodeGoogleStatus(int code, string message)
    {
        using var stream = new MemoryStream();
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            output.WriteTag(1, WireFormat.WireType.Varint);
            output.WriteInt32(code);
            output.WriteTag(2, WireFormat.WireType.LengthDelimited);
            output.WriteString(message);
        }
        return stream.ToArray();
    }
}
