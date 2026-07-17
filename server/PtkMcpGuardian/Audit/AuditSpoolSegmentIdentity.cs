using System.Globalization;

namespace PtkMcpServer.Audit;

/// <summary>
/// Canonical identity encoded by an authoritative audit-spool segment name.
/// </summary>
internal readonly record struct AuditSpoolSegmentIdentity
{
    private const string FileNamePrefix = "ptk-audit-";
    private const string FileNameSuffix = ".jsonl";
    private const int BootIdLength = 32;
    private const int IndexLength = 8;
    internal const int MaximumIndex = 99_999_999;
    internal const int FileNameLength = 57;

    private AuditSpoolSegmentIdentity(Guid supervisorBootId, int index)
    {
        SupervisorBootId = supervisorBootId;
        Index = index;
    }

    internal Guid SupervisorBootId { get; }

    internal int Index { get; }

    internal string FileName =>
        FileNamePrefix +
        SupervisorBootId.ToString("N") +
        "-" +
        Index.ToString("D8", CultureInfo.InvariantCulture) +
        FileNameSuffix;

    internal static AuditSpoolSegmentIdentity Create(Guid supervisorBootId, int index)
    {
        RequireUuidV4(supervisorBootId, nameof(supervisorBootId));
        if (index is < 0 or > MaximumIndex)
            throw new ArgumentOutOfRangeException(nameof(index));

        return new AuditSpoolSegmentIdentity(supervisorBootId, index);
    }

    internal static bool TryParse(
        string? fileName,
        out AuditSpoolSegmentIdentity identity)
    {
        identity = default;
        if (fileName is null || fileName.Length != FileNameLength)
            return false;
        if (!fileName.StartsWith(FileNamePrefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(FileNameSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var value = fileName.AsSpan();
        var bootIdStart = FileNamePrefix.Length;
        var indexSeparator = bootIdStart + BootIdLength;
        var indexStart = indexSeparator + 1;
        if (value[indexSeparator] != '-' ||
            !TryParseCanonicalUuidV4(
                value.Slice(bootIdStart, BootIdLength),
                out var supervisorBootId))
        {
            return false;
        }

        var index = 0;
        foreach (var character in value.Slice(indexStart, IndexLength))
        {
            if (character is < '0' or > '9')
                return false;
            index = checked(index * 10 + character - '0');
        }

        identity = new AuditSpoolSegmentIdentity(supervisorBootId, index);
        return true;
    }

    internal static void RequireUuidV4(Guid value, string parameterName)
    {
        if (!IsUuidV4(value))
            throw new ArgumentException("A canonical RFC 4122 UUIDv4 is required.", parameterName);
    }

    internal static bool IsUuidV4(Guid value)
    {
        var text = value.ToString("D");
        return text[14] == '4' && text[19] is '8' or '9' or 'a' or 'b';
    }

    internal static bool TryParseCanonicalUuidV4(
        ReadOnlySpan<char> value,
        out Guid parsed)
    {
        parsed = default;
        if (value.Length != BootIdLength || value[12] != '4' ||
            value[16] is not ('8' or '9' or 'a' or 'b'))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return Guid.TryParseExact(value, "N", out parsed);
    }

    public override string ToString() => FileName;
}
