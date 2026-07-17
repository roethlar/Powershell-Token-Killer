namespace PtkMcpServer.Audit;

internal readonly record struct AuditAnchoredSpoolRetentionOutcome(
    int DeletedSegmentCount,
    long DeletedBytes,
    bool HeadroomSatisfied);

/// <summary>
/// Deletes only the acknowledged, closed front of one anchored boot chain.
/// The per-boot checkpoint owner authorizes the floor; the global quota lease
/// freezes compliant spool topology; and each retained file handle authorizes
/// deletion of only the exact protected inode that was inspected.
/// </summary>
internal static class AuditAnchoredSpoolPrefixRetention
{
    internal static AuditAnchoredSpoolRetentionOutcome Sweep(
        AuditOptions options,
        AuditExportCheckpointStore checkpointStore,
        DateTimeOffset utcNow,
        long requiredHeadroomBytes,
        Action<string>? candidateRetainedForTests = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
        {
            throw new ArgumentException(
                "Anchored spool retention requires anchored audit protection.",
                nameof(options));
        }
        if (utcNow.Offset != TimeSpan.Zero)
            throw new ArgumentException("The retention clock must be UTC.", nameof(utcNow));
        if (requiredHeadroomBytes < 0 || requiredHeadroomBytes > options.AggregateBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredHeadroomBytes),
                "Retention headroom must fit inside the aggregate spool bound.");
        }

        var spoolRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(options.SpoolDirectory));
        using var checkpointLease = checkpointStore.RetainExportReader(options);
        using var quotaLease = AuditSpoolQuotaLease.AcquireExisting(spoolRoot);
        return checkpointLease.WithOwnedCheckpoint(checkpoint =>
            SweepOwnedCheckpoint(
                options,
                checkpoint,
                spoolRoot,
                quotaLease,
                utcNow,
                requiredHeadroomBytes,
                candidateRetainedForTests));
    }

    private static AuditAnchoredSpoolRetentionOutcome SweepOwnedCheckpoint(
        AuditOptions options,
        AuditExportCheckpoint checkpoint,
        string spoolRoot,
        AuditSpoolQuotaLease quotaLease,
        DateTimeOffset utcNow,
        long requiredHeadroomBytes,
        Action<string>? candidateRetainedForTests)
    {
        AuditExportCheckpointCodec.Validate(checkpoint);
        quotaLease.VerifyOwnership();
        var inventory = Inventory(options, spoolRoot);
        var target = ValidateTargetTopology(checkpoint, inventory);
        var totalBytes = TotalBytes(inventory);
        var maximumRetainedBytes = checked(options.AggregateBytes - requiredHeadroomBytes);

        if (checkpoint.Spool is not { } acknowledgedSpool)
        {
            quotaLease.VerifyOwnership();
            return new AuditAnchoredSpoolRetentionOutcome(
                0,
                0,
                totalBytes <= maximumRetainedBytes);
        }

        var planned = new List<SegmentSnapshot>();
        var simulatedBytes = totalBytes;
        foreach (var segment in target)
        {
            if (segment.Identity.Index >= acknowledgedSpool.Index)
                break;

            var ageEligible =
                utcNow - new DateTimeOffset(segment.LastWriteTimeUtc, TimeSpan.Zero) >=
                options.RetentionAge;
            var pressureEligible = simulatedBytes > maximumRetainedBytes;
            if (!ageEligible && !pressureEligible)
                break;

            planned.Add(segment);
            simulatedBytes = checked(simulatedBytes - segment.Length);
        }

        if (planned.Count == 0)
        {
            quotaLease.VerifyOwnership();
            return new AuditAnchoredSpoolRetentionOutcome(
                0,
                0,
                totalBytes <= maximumRetainedBytes);
        }

        var retained = new List<RetainedCandidate>(planned.Count);
        var retainedIdentities = new HashSet<ProtectedFileIdentity>();
        try
        {
            foreach (var segment in planned)
            {
                if (!TryRetainCandidate(segment, out var stream))
                {
                    quotaLease.VerifyOwnership();
                    return new AuditAnchoredSpoolRetentionOutcome(
                        0,
                        0,
                        totalBytes <= maximumRetainedBytes);
                }

                try
                {
                    var retainedStream = stream ?? throw new IOException(
                        "An audit retention candidate was not retained.");
                    if (retainedStream.Length != segment.Length)
                        throw new IOException("An audit retention candidate changed length.");
                    var identity = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
                        segment.Path,
                        retainedStream.SafeFileHandle);
                    if (!retainedIdentities.Add(identity))
                    {
                        throw new IOException(
                            "Two audit retention candidates identify the same file.");
                    }
                    retained.Add(new RetainedCandidate(segment, retainedStream, identity));
                    stream = null;
                }
                finally
                {
                    stream?.Dispose();
                }
            }

            quotaLease.VerifyOwnership();
            var deletedCount = 0;
            var deletedBytes = 0L;
            foreach (var candidate in retained)
            {
                candidateRetainedForTests?.Invoke(candidate.Segment.Path);
                quotaLease.VerifyOwnership();
                var observedIdentity =
                    SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
                        candidate.Segment.Path,
                        candidate.Stream.SafeFileHandle);
                if (observedIdentity != candidate.Identity ||
                    candidate.Stream.Length != candidate.Segment.Length)
                {
                    throw new IOException(
                        "An audit retention candidate changed after it was retained.");
                }

                SecureAuditStorage.DeleteRetainedProtectedFile(
                    spoolRoot,
                    candidate.Segment.Path,
                    candidate.Stream.SafeFileHandle);
                deletedCount = checked(deletedCount + 1);
                deletedBytes = checked(deletedBytes + candidate.Segment.Length);
            }

            quotaLease.VerifyOwnership();
            var observed = Inventory(options, spoolRoot);
            VerifyExpectedInventoryAfterDeletion(inventory, retained, observed);
            _ = ValidateTargetTopology(checkpoint, observed);
            var remainingBytes = TotalBytes(observed);
            return new AuditAnchoredSpoolRetentionOutcome(
                deletedCount,
                deletedBytes,
                remainingBytes <= maximumRetainedBytes);
        }
        finally
        {
            foreach (var candidate in retained)
                candidate.Stream.Dispose();
        }
    }

    private static SegmentSnapshot[] Inventory(AuditOptions options, string spoolRoot)
    {
        SecureAuditStorage.VerifyExternalProtectedDirectory(spoolRoot);
        var segments = new List<SegmentSnapshot>();
        var entries = 0;
        foreach (var entry in new DirectoryInfo(spoolRoot)
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            entries = checked(entries + 1);
            if (entries > AuditClosedSpoolChainReader.MaximumSpoolInventoryEntries)
            {
                throw new IOException(
                    "The audit spool inventory exceeds its retention entry bound.");
            }

            if (entry is FileInfo quotaControl &&
                string.Equals(
                    quotaControl.Name,
                    AuditSpoolQuotaLease.ControlFileName,
                    StringComparison.Ordinal))
            {
                SecureAuditStorage.VerifyExternalProtectedFile(quotaControl.FullName);
                continue;
            }

            if (entry is not FileInfo file ||
                !AuditSpoolSegmentIdentity.TryParse(file.Name, out var identity))
            {
                throw new IOException("The audit spool contains an unknown entry.");
            }
            if (identity.Index >= AuditClosedSpoolChainReader.MaximumClosedChainSegments)
            {
                throw new IOException(
                    "An audit spool chain exceeds its retention segment bound.");
            }

            SecureAuditStorage.VerifyExternalProtectedFile(file.FullName);
            file.Refresh();
            if (file.Length < 0 || file.Length > options.SegmentBytes)
            {
                throw new IOException(
                    "An audit spool segment exceeds its configured retention bound.");
            }
            segments.Add(new SegmentSnapshot(
                identity,
                file.FullName,
                file.Length,
                file.LastWriteTimeUtc));
        }
        SecureAuditStorage.VerifyExternalProtectedDirectory(spoolRoot);
        return segments.ToArray();
    }

    private static SegmentSnapshot[] ValidateTargetTopology(
        AuditExportCheckpoint checkpoint,
        SegmentSnapshot[] inventory)
    {
        var target = inventory
            .Where(segment =>
                segment.Identity.SupervisorBootId == checkpoint.SupervisorBootId)
            .OrderBy(segment => segment.Identity.Index)
            .ToArray();
        if (target.Length == 0)
            throw new IOException("The owned audit checkpoint has no spool segment.");

        for (var index = 1; index < target.Length; index++)
        {
            if (target[index].Identity.Index != target[index - 1].Identity.Index + 1)
                throw new IOException("The owned audit spool chain has an internal gap.");
        }

        var retainedFloor = target[0].Identity.Index;
        if (retainedFloor != 0 &&
            (checkpoint.Spool is not { } floorProof ||
             floorProof.SupervisorBootId != checkpoint.SupervisorBootId ||
             floorProof.Index < retainedFloor))
        {
            throw new IOException(
                "The owned audit checkpoint does not prove the retained spool floor.");
        }

        if (checkpoint.Spool is { } acknowledged &&
            !target.Any(segment => segment.Identity == acknowledged))
        {
            throw new IOException(
                "The owned audit checkpoint segment is absent from the retained chain.");
        }
        if (checkpoint.BlockedRecord is { } blocked &&
            !target.Any(segment => segment.Identity == blocked.Spool))
        {
            throw new IOException(
                "The blocked audit checkpoint segment is absent from the retained chain.");
        }
        return target;
    }

    private static long TotalBytes(IEnumerable<SegmentSnapshot> inventory)
    {
        var total = 0L;
        foreach (var segment in inventory)
            total = checked(total + segment.Length);
        return total;
    }

    private static bool TryRetainCandidate(
        SegmentSnapshot segment,
        out FileStream? stream)
    {
        stream = null;
        try
        {
            stream = new FileStream(
                segment.Path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Delete,
                bufferSize: 1,
                FileOptions.None);
            return true;
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            return false;
        }
    }

    private static void VerifyExpectedInventoryAfterDeletion(
        SegmentSnapshot[] before,
        IEnumerable<RetainedCandidate> deleted,
        SegmentSnapshot[] after)
    {
        var deletedNames = deleted
            .Select(candidate => candidate.Segment.Identity)
            .ToHashSet();
        var expected = before
            .Where(segment => !deletedNames.Contains(segment.Identity))
            .OrderBy(segment => segment.Identity.FileName, StringComparer.Ordinal)
            .ToArray();
        var observed = after
            .OrderBy(segment => segment.Identity.FileName, StringComparer.Ordinal)
            .ToArray();
        if (expected.Length != observed.Length)
            throw new IOException("The audit spool inventory changed during retention.");

        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index].Identity != observed[index].Identity ||
                expected[index].Length != observed[index].Length ||
                !PathsEqual(expected[index].Path, observed[index].Path))
            {
                throw new IOException("The audit spool inventory changed during retention.");
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var nativeCode = exception.HResult & 0xffff;
        if (OperatingSystem.IsWindows())
            return nativeCode is 32 or 33;
        if (OperatingSystem.IsLinux())
            return exception.HResult == 11 || nativeCode == 11;
        if (OperatingSystem.IsMacOS())
            return exception.HResult == 35 || nativeCode == 35;
        return false;
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private readonly record struct SegmentSnapshot(
        AuditSpoolSegmentIdentity Identity,
        string Path,
        long Length,
        DateTime LastWriteTimeUtc);

    private sealed record RetainedCandidate(
        SegmentSnapshot Segment,
        FileStream Stream,
        ProtectedFileIdentity Identity);
}
