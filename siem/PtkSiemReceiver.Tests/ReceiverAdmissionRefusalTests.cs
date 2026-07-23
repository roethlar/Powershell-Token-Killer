using Microsoft.AspNetCore.Http;
using PtkSiemReceiver.Ingest;

namespace PtkSiemReceiver.Tests;

/// <summary>
/// Unit-level pin of the rbc-12 refusal-before-buffering contract:
/// a saturated admission gate must refuse the request before touching
/// the body stream, the options, or the committer.
/// </summary>
public sealed class ReceiverAdmissionRefusalTests
{
    [Fact]
    public async Task Saturated_gate_refuses_before_touching_body_options_or_committer()
    {
        using var gate = new IngestAdmissionGate(1);
        Assert.True(gate.TryEnter()); // park the only slot

        var context = new DefaultHttpContext();
        context.Request.Body = new ThrowingStream();
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        // options/committer are deliberately null!: the rbc-12 contract is
        // that a saturated gate refuses before *anything* else is consulted.
        // Any reordering that dereferences them — or reads the body (which
        // throws) — fails this test loudly instead of silently buffering.
        await ReceiverApplication.HandleIngestAsync(
            context,
            options: null!,
            committer: null!,
            TimeProvider.System,
            gate);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("1", context.Response.Headers.RetryAfter.ToString());
        Assert.True(responseBody.Length > 0, "transient refusal must carry a google.rpc.Status body");

        // The refused request must NOT release the slot it never acquired:
        // if the refusal path erroneously called Exit(), this TryEnter would
        // succeed and over-admit under saturation.
        Assert.False(gate.TryEnter());
    }

    /// <summary>Fails the test if any byte of the request body is read.</summary>
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw Refusal();
        public override long Position
        {
            get => throw Refusal();
            set => throw Refusal();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw Refusal();
        public override int Read(Span<byte> buffer) => throw Refusal();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw Refusal();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw Refusal();
        public override void Flush() => throw Refusal();
        public override long Seek(long offset, SeekOrigin origin) => throw Refusal();
        public override void SetLength(long value) => throw Refusal();
        public override void Write(byte[] buffer, int offset, int count) => throw Refusal();

        private static InvalidOperationException Refusal() =>
            new("rbc-12 violation: the request body must not be touched when admission is refused.");
    }
}
