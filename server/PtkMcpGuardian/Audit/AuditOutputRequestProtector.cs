using System.Security.Cryptography;
using System.Text;

namespace PtkMcpServer.Audit;

/// <summary>Per-supervisor, memory-only protection for sensitive output
/// request fields. Handles have enough entropy for a plain SHA-256 audit
/// reference; low-entropy search patterns use a domain-separated HMAC so the
/// core stream records equality without enabling offline dictionary recovery.</summary>
internal sealed class AuditOutputRequestProtector : IDisposable
{
    private static readonly byte[] PatternDomain =
        Encoding.ASCII.GetBytes("ptk.output-pattern/1\0");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private byte[]? _key;

    internal AuditOutputRequestProtector()
    {
        _key = RandomNumberGenerator.GetBytes(32);
    }

    internal AuditOutputRequestProtector(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
            throw new ArgumentOutOfRangeException(nameof(key));
        _key = key.ToArray();
    }

    internal string HandleDigest(string handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var bytes = StrictUtf8.GetBytes(handle);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal string PatternFingerprint(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var key = _key ?? throw new ObjectDisposedException(nameof(AuditOutputRequestProtector));
        var patternBytes = StrictUtf8.GetBytes(pattern);
        var input = new byte[checked(PatternDomain.Length + patternBytes.Length)];
        PatternDomain.CopyTo(input, 0);
        patternBytes.CopyTo(input, PatternDomain.Length);
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(key, input)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(patternBytes);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    public void Dispose()
    {
        var key = Interlocked.Exchange(ref _key, null);
        if (key is not null) CryptographicOperations.ZeroMemory(key);
    }
}
