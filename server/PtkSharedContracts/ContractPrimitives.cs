using System.Security.Cryptography;

namespace PtkSharedContracts;

public static class ContractLimits
{
    public const int GuardianHostProtocolVersion = 1;
    public const int MaximumEncodedFrameBytes = 1_048_576;
    public const int MaximumJsonDepth = 32;
    public const int MaximumDiagnosticBytesPerStream = 65_536;
    public const int MaximumDiagnosticChunkBytes = 16_384;
    public const int MaximumManifestBytes = 25_165_824;
    public const int MaximumManifestChunkBytes = 524_288;
    public const int MaximumManifestChunks = 48;
    public const int MaximumAliases = 128;
    public const int MaximumTemplates = 128;
    public const int MaximumScriptBytes = 131_072;
    public const int MaximumTextResultBytes = 131_072;
    public const int MaximumOutputBytes = 8_388_608;
    public const int MaximumOutputChunkBytes = 65_536;
    public const int MaximumPublicRecoveryBytes = 4_096;
    public const int MinimumRetryAfterMilliseconds = 250;
    public const int MaximumRetryAfterMilliseconds = 60_000;
    public const int MaximumOutputHandleCharacters = 256;
    public const int DefaultOutputReadBytes = 16_384;
    public const int MaximumOutputReadBytes = 65_536;
    public const int MaximumOutputPatternBytes = 1_024;
    public const int CapabilityTokenCharacters = 43;
    public const int CapabilityTokenBytes = 32;
}

public sealed record CanonicalAlias
{
    public CanonicalAlias(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!ContractValidation.IsAlias(value))
            throw new ArgumentException("Alias is not canonical.", nameof(value));
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record Sha256Digest
{
    public Sha256Digest(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!ContractValidation.IsSha256(value))
            throw new ArgumentException("Digest must be lowercase SHA-256 hex.", nameof(value));
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;

    public static Sha256Digest Compute(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
}

public sealed record CapabilityToken
{
    public CapabilityToken(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!ContractValidation.IsCapabilityToken(value))
            throw new ArgumentException("Capability token is not canonical.", nameof(value));
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record GuardianBootId
{
    public GuardianBootId(Guid value) { ContractValidation.RequireUuid(value, 4, nameof(value)); Value = value; }
    public Guid Value { get; }
    public override string ToString() => Value.ToString("D");
}

public sealed record HostBootId
{
    public HostBootId(Guid value) { ContractValidation.RequireUuid(value, 4, nameof(value)); Value = value; }
    public Guid Value { get; }
    public override string ToString() => Value.ToString("D");
}

public sealed record WorkerBootId
{
    public WorkerBootId(Guid value) { ContractValidation.RequireUuid(value, 4, nameof(value)); Value = value; }
    public Guid Value { get; }
    public override string ToString() => Value.ToString("D");
}

public sealed record ManifestId
{
    public ManifestId(Guid value) { ContractValidation.RequireUuid(value, 4, nameof(value)); Value = value; }
    public Guid Value { get; }
    public override string ToString() => Value.ToString("D");
}

public sealed record PlanId
{
    public PlanId(Guid value) { ContractValidation.RequireUuid(value, 4, nameof(value)); Value = value; }
    public Guid Value { get; }
    public override string ToString() => Value.ToString("D");
}

public sealed record OperationId
{
    public OperationId(Guid value) { ContractValidation.RequireUuid(value, 4, nameof(value)); Value = value; }
    public Guid Value { get; }
    public override string ToString() => Value.ToString("D");
}

public sealed record CallId
{
    public CallId(Guid value) { ContractValidation.RequireUuid(value, 7, nameof(value)); Value = value; }
    public Guid Value { get; }
    public override string ToString() => Value.ToString("D");
}

public sealed record HostGeneration
{
    public HostGeneration(long value) { ContractValidation.RequirePositive(value, nameof(value)); Value = value; }
    public long Value { get; }
}

public sealed record WorkerGeneration
{
    public WorkerGeneration(long value) { ContractValidation.RequirePositive(value, nameof(value)); Value = value; }
    public long Value { get; }
}

public sealed record PrivateRequestId
{
    public PrivateRequestId(long value) { ContractValidation.RequirePositive(value, nameof(value)); Value = value; }
    public long Value { get; }
}

public sealed record HostEventSequence
{
    public HostEventSequence(long value) { ContractValidation.RequirePositive(value, nameof(value)); Value = value; }
    public long Value { get; }
}

public sealed record SessionTransitionVersion
{
    public SessionTransitionVersion(long value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }
    public long Value { get; }
}

public sealed record WorkerGenerationHighWatermark
{
    public WorkerGenerationHighWatermark(long value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }
    public long Value { get; }
}

public sealed record PublicJobId
{
    public PublicJobId(long value) { ContractValidation.RequirePositive(value, nameof(value)); Value = value; }
    public long Value { get; }
}

internal static class ContractValidation
{
    internal static bool IsAlias(string value) =>
        value.Length is >= 1 and <= 64 &&
        IsLowerAlphaNumeric(value[0]) &&
        value.All(character => IsLowerAlphaNumeric(character) || character is '.' or '_' or '-');

    internal static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsCapabilityToken(string value)
    {
        if (value.Length != ContractLimits.CapabilityTokenCharacters ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            return false;

        Span<char> padded = stackalloc char[44];
        value.AsSpan().CopyTo(padded);
        for (var index = 0; index < value.Length; index++)
        {
            padded[index] = padded[index] switch { '-' => '+', '_' => '/', _ => padded[index] };
        }
        padded[^1] = '=';
        Span<byte> decoded = stackalloc byte[ContractLimits.CapabilityTokenBytes];
        try
        {
            if (!Convert.TryFromBase64Chars(padded, decoded, out var written) ||
                written != ContractLimits.CapabilityTokenBytes)
                return false;

            var canonical = Convert.ToBase64String(decoded)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return string.Equals(value, canonical, StringComparison.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    internal static void RequireUuid(Guid value, int version, string name)
    {
        var text = value.ToString("D");
        if (value == Guid.Empty || text[14] != (char)('0' + version) ||
            text[19] is not ('8' or '9' or 'a' or 'b'))
            throw new ArgumentException($"{name} must be RFC 4122 UUIDv{version}.", name);
    }

    internal static void RequirePositive(long value, string name)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(name);
    }

    private static bool IsLowerAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';
}
