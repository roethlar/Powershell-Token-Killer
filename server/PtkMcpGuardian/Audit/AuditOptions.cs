using System.Text.RegularExpressions;

namespace PtkMcpServer.Audit;

public enum AuditProtectionMode
{
    LocalOnly,
    Anchored,
}

/// <summary>
/// Immutable, startup-frozen audit storage configuration. Secrets and exporter
/// credentials deliberately do not belong in this object.
/// </summary>
public sealed class AuditOptions
{
    public const int DefaultMaxRecordBytes = 65_536;
    public const long DefaultSegmentBytes = 16L * 1024 * 1024;
    public const long DefaultAggregateBytes = 256L * 1024 * 1024;
    public const long DefaultEmergencyReserveBytes = 4L * 1024 * 1024;
    public const int DefaultMaxEvidenceBytes = 131_072;
    public const long DefaultEvidenceAggregateBytes = 256L * 1024 * 1024;

    public static readonly TimeSpan DefaultRetentionAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan DefaultEvidenceRetentionAge = TimeSpan.FromDays(30);

    private static readonly Regex ConfigurationIdentityPattern = new(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private AuditOptions(
        string rootDirectory,
        AuditProtectionMode protectionMode,
        string? exportConfigurationIdentity,
        int maxRecordBytes,
        long segmentBytes,
        long aggregateBytes,
        long emergencyReserveBytes,
        TimeSpan retentionAge,
        int maxEvidenceBytes,
        long evidenceAggregateBytes,
        TimeSpan evidenceRetentionAge)
    {
        RootDirectory = rootDirectory;
        SpoolDirectory = Path.Combine(rootDirectory, "spool");
        EvidenceDirectory = Path.Combine(rootDirectory, "evidence");
        ProtectionMode = protectionMode;
        ExportConfigurationIdentity = exportConfigurationIdentity;
        MaxRecordBytes = maxRecordBytes;
        SegmentBytes = segmentBytes;
        AggregateBytes = aggregateBytes;
        EmergencyReserveBytes = emergencyReserveBytes;
        RetentionAge = retentionAge;
        MaxEvidenceBytes = maxEvidenceBytes;
        EvidenceAggregateBytes = evidenceAggregateBytes;
        EvidenceRetentionAge = evidenceRetentionAge;
    }

    public string RootDirectory { get; }

    public string SpoolDirectory { get; }

    public string EvidenceDirectory { get; }

    public AuditProtectionMode ProtectionMode { get; }

    /// <summary>A nonsecret digest identifying anchored exporter configuration.</summary>
    public string? ExportConfigurationIdentity { get; }

    public int MaxRecordBytes { get; }

    public long SegmentBytes { get; }

    public long AggregateBytes { get; }

    public long EmergencyReserveBytes { get; }

    public TimeSpan RetentionAge { get; }

    public int MaxEvidenceBytes { get; }

    public long EvidenceAggregateBytes { get; }

    public TimeSpan EvidenceRetentionAge { get; }

    public static AuditOptions CreateDefault()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile) || !Path.IsPathFullyQualified(profile))
        {
            throw new InvalidOperationException("An absolute user profile is required for the default audit path.");
        }

        return Create(Path.Combine(profile, ".ptk", "audit"));
    }

    /// <summary>
    /// Creates an immutable configuration. Supplying every storage bound makes
    /// deliberately small, isolated journal configurations possible in tests.
    /// </summary>
    public static AuditOptions Create(
        string rootDirectory,
        AuditProtectionMode protectionMode = AuditProtectionMode.LocalOnly,
        string? exportConfigurationIdentity = null,
        int maxRecordBytes = DefaultMaxRecordBytes,
        long segmentBytes = DefaultSegmentBytes,
        long aggregateBytes = DefaultAggregateBytes,
        long emergencyReserveBytes = DefaultEmergencyReserveBytes,
        TimeSpan? retentionAge = null,
        int maxEvidenceBytes = DefaultMaxEvidenceBytes,
        long evidenceAggregateBytes = DefaultEvidenceAggregateBytes,
        TimeSpan? evidenceRetentionAge = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("Audit root directory must be an absolute path.", nameof(rootDirectory));
        }

        if (!Enum.IsDefined(protectionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(protectionMode));
        }

        if (protectionMode == AuditProtectionMode.LocalOnly)
        {
            if (exportConfigurationIdentity is not null)
            {
                throw new ArgumentException(
                    "Local-only audit must not have an export configuration identity.",
                    nameof(exportConfigurationIdentity));
            }
        }
        else if (exportConfigurationIdentity is null ||
                 !ConfigurationIdentityPattern.IsMatch(exportConfigurationIdentity))
        {
            throw new ArgumentException(
                "Anchored audit requires a lowercase SHA-256 export configuration identity.",
                nameof(exportConfigurationIdentity));
        }

        ValidateRange(maxRecordBytes, 256, DefaultMaxRecordBytes, nameof(maxRecordBytes));
        ValidateRange(segmentBytes, maxRecordBytes, 4L * 1024 * 1024 * 1024, nameof(segmentBytes));
        ValidateRange(aggregateBytes, segmentBytes, 1024L * 1024 * 1024 * 1024, nameof(aggregateBytes));
        if (protectionMode == AuditProtectionMode.Anchored &&
            aggregateBytes < checked(2 * segmentBytes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(aggregateBytes),
                aggregateBytes,
                "Anchored audit requires one writer segment plus one full restart segment.");
        }
        ValidateRange(
            emergencyReserveBytes,
            checked(2L * maxRecordBytes),
            segmentBytes,
            nameof(emergencyReserveBytes));

        var effectiveRetentionAge = retentionAge ?? DefaultRetentionAge;
        ValidateDuration(effectiveRetentionAge, nameof(retentionAge));

        ValidateRange(maxEvidenceBytes, 256, DefaultMaxEvidenceBytes, nameof(maxEvidenceBytes));
        ValidateRange(
            evidenceAggregateBytes,
            maxEvidenceBytes,
            1024L * 1024 * 1024 * 1024,
            nameof(evidenceAggregateBytes));

        var effectiveEvidenceRetentionAge = evidenceRetentionAge ?? DefaultEvidenceRetentionAge;
        ValidateDuration(effectiveEvidenceRetentionAge, nameof(evidenceRetentionAge));

        var frozenRoot = Path.GetFullPath(rootDirectory);
        return new AuditOptions(
            frozenRoot,
            protectionMode,
            exportConfigurationIdentity,
            maxRecordBytes,
            segmentBytes,
            aggregateBytes,
            emergencyReserveBytes,
            effectiveRetentionAge,
            maxEvidenceBytes,
            evidenceAggregateBytes,
            effectiveEvidenceRetentionAge);
    }

    private static void ValidateRange(long value, long minimum, long maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must be between {minimum} and {maximum} inclusive.");
        }
    }

    private static void ValidateDuration(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.FromMinutes(1) || value > TimeSpan.FromDays(3650))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Duration must be between one minute and 3650 days inclusive.");
        }
    }
}
