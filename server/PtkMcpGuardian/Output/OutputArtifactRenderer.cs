using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace PtkMcpServer;

/// <summary>
/// One labeled segment in the stable UTF-8 recovery artifact view. Segment
/// metadata remains guardian-owned; private-host transfer uses only the exact
/// rendered bytes.
/// </summary>
internal sealed record OutputArtifactSegment(
    string Name,
    long Offset,
    long Length);

internal sealed record OutputArtifactRenderResult(
    long Bytes,
    bool Truncated,
    OutputArtifactSegment[] Segments);

/// <summary>
/// Canonical renderer shared by the guardian store and the private host's
/// capability-scoped event adapter. It owns no store, path, reservation,
/// public handle, or transport.
/// </summary>
internal static class OutputArtifactRenderer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static OutputArtifactRenderResult Render(
        Stream stream,
        OutputArtifactContent content,
        long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(content);
        if (!stream.CanWrite)
            throw new ArgumentException("Output artifact stream must be writable.", nameof(stream));
        if (maximumBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        using var writer = new CappedUtf8Writer(stream, maximumBytes);
        var segments = new List<OutputArtifactSegment>();

        WriteSegment(writer, segments, "stdout", content.StandardOutput);
        if (content.ExitCode is int exitCode)
        {
            EnsureLineBoundary(writer, content.StandardOutput);
            WriteSegment(writer, segments, "exit", $"[exit] {exitCode}");
        }
        WriteLines(writer, segments, "stderr", "[stderr]", content.StandardError);
        WriteLines(writer, segments, "errors", "[errors]", content.Errors);
        WriteLines(writer, segments, "warnings", "[warnings]", content.Warnings);

        return new OutputArtifactRenderResult(
            writer.BytesWritten,
            writer.Truncated,
            [.. segments]);
    }

    /// <summary>
    /// Returns a caller-owned sensitive byte buffer. The caller must clear it
    /// after the transport or other immediate consumer has completed.
    /// </summary>
    internal static (byte[] Bytes, bool Truncated) RenderToBytes(
        OutputArtifactContent content,
        int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maximumBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        using var stream = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        try
        {
            var rendered = Render(stream, content, maximumBytes);
            return (stream.ToArray(), rendered.Truncated);
        }
        finally
        {
            if (stream.TryGetBuffer(out var buffer) &&
                buffer.Array is not null &&
                stream.Length > 0)
            {
                CryptographicOperations.ZeroMemory(
                    buffer.Array.AsSpan(buffer.Offset, checked((int)stream.Length)));
            }
        }
    }

    private static void WriteLines(
        CappedUtf8Writer writer,
        List<OutputArtifactSegment> segments,
        string name,
        string label,
        IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || writer.Truncated) return;
        EnsureLineBoundary(writer, previousText: null);
        var start = writer.BytesWritten;
        writer.Write(label);
        writer.Write(Environment.NewLine);
        for (var index = 0; index < lines.Count && !writer.Truncated; index++)
        {
            writer.Write(lines[index] ?? string.Empty);
            if (index + 1 < lines.Count) writer.Write(Environment.NewLine);
        }
        segments.Add(new OutputArtifactSegment(
            name,
            start,
            writer.BytesWritten - start));
    }

    private static void WriteSegment(
        CappedUtf8Writer writer,
        List<OutputArtifactSegment> segments,
        string name,
        string text)
    {
        if (string.IsNullOrEmpty(text) || writer.Truncated) return;
        var start = writer.BytesWritten;
        writer.Write(text);
        segments.Add(new OutputArtifactSegment(
            name,
            start,
            writer.BytesWritten - start));
    }

    private static void EnsureLineBoundary(
        CappedUtf8Writer writer,
        string? previousText)
    {
        if (writer.BytesWritten == 0) return;
        if (previousText is not null &&
            (previousText.EndsWith('\n') || previousText.EndsWith('\r')))
        {
            return;
        }
        writer.Write(Environment.NewLine);
    }

    private sealed class CappedUtf8Writer(
        Stream stream,
        long maximumBytes) : IDisposable
    {
        private readonly Encoder _encoder = StrictUtf8.GetEncoder();
        private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        internal long BytesWritten { get; private set; }
        internal bool Truncated { get; private set; }

        internal void Write(string text)
        {
            if (Truncated || string.IsNullOrEmpty(text)) return;
            var chars = text.AsSpan();
            while (!chars.IsEmpty)
            {
                var remaining = maximumBytes - BytesWritten;
                if (remaining <= 0)
                {
                    Truncated = true;
                    return;
                }

                var buffer = _buffer ??
                    throw new ObjectDisposedException(nameof(CappedUtf8Writer));
                var target = buffer.AsSpan();
                _encoder.Convert(
                    chars,
                    target,
                    flush: true,
                    out var charsUsed,
                    out var bytesUsed,
                    out _);
                var writable = (int)Math.Min(bytesUsed, remaining);
                while (writable < bytesUsed &&
                       writable > 0 &&
                       (buffer[writable] & 0b1100_0000) == 0b1000_0000)
                {
                    writable--;
                }
                if (writable > 0)
                {
                    stream.Write(target[..writable]);
                    BytesWritten += writable;
                }
                if (writable < bytesUsed)
                {
                    Truncated = true;
                    return;
                }
                chars = chars[charsUsed..];
                if (charsUsed == 0)
                {
                    Truncated = true;
                    return;
                }
            }
        }

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
