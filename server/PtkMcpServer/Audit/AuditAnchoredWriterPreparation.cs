namespace PtkMcpServer.Audit;

/// <summary>
/// Opaque construction capability for a new anchored journal writer. Until
/// activation, the retained segment is deliberately not exposed as an audit
/// sink and therefore cannot accept records before its checkpoint controls
/// are durable.
/// </summary>
internal sealed class AuditAnchoredWriterPreparation : IDisposable
{
    private readonly AuditOptions _options;
    private readonly Guid _supervisorBootId;
    private AuditSpoolQuotaLease? _quota;
    private FileAuditJournalSink? _preparedSink;
    private AuditExportCheckpointStore? _checkpointStore;
    private bool _activated;

    internal AuditAnchoredWriterPreparation(
        AuditOptions options,
        Guid supervisorBootId,
        AuditSpoolQuotaLease quota,
        FileAuditJournalSink preparedSink)
    {
        _options = options;
        _supervisorBootId = supervisorBootId;
        _quota = quota;
        _preparedSink = preparedSink;
    }

    internal Guid SupervisorBootId => _supervisorBootId;

    internal string SegmentPath => RequirePreparedSink().CurrentSegmentPath;

    internal AuditExportCheckpointStore CreateCheckpointStore()
    {
        return AuditExportCheckpointStore.CreateForPreparedWriter(
            _options,
            this);
    }

    internal FileAuditJournalSink Activate(AuditExportCheckpointStore checkpointStore)
    {
        ArgumentNullException.ThrowIfNull(checkpointStore);
        if (_activated)
            throw new InvalidOperationException("The anchored writer preparation was already activated.");
        if (!ReferenceEquals(checkpointStore, _checkpointStore))
        {
            throw new ArgumentException(
                "The checkpoint owner was not created by this writer preparation.",
                nameof(checkpointStore));
        }

        var quota = RequireQuota();
        var sink = RequirePreparedSink();
        sink.AttachPreparedCheckpointOwner(_options, checkpointStore, quota);
        _activated = true;
        _preparedSink = null;
        _quota = null;
        quota.Dispose();
        return sink;
    }

    internal void VerifyForCheckpointCreation(AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_activated ||
            options.ProtectionMode != AuditProtectionMode.Anchored ||
            !PathsEqual(options.RootDirectory, _options.RootDirectory) ||
            !PathsEqual(options.SpoolDirectory, _options.SpoolDirectory))
        {
            throw new ArgumentException(
                "The checkpoint configuration does not match this writer preparation.",
                nameof(options));
        }

        RequirePreparedSink().VerifyPreparedAnchoredSegment(RequireQuota());
    }

    internal void BindCheckpointStore(AuditExportCheckpointStore checkpointStore)
    {
        ArgumentNullException.ThrowIfNull(checkpointStore);
        if (_checkpointStore is not null)
        {
            throw new InvalidOperationException(
                "The anchored writer preparation already created checkpoint state.");
        }
        _checkpointStore = checkpointStore;
    }

    public void Dispose()
    {
        var quota = Interlocked.Exchange(ref _quota, null);
        var sink = Interlocked.Exchange(ref _preparedSink, null);
        if (quota is null)
            return;

        try
        {
            sink?.ClosePreparedUnderRetainedQuota(quota);
        }
        finally
        {
            quota.Dispose();
        }
    }

    private AuditSpoolQuotaLease RequireQuota() =>
        _quota ?? throw new ObjectDisposedException(nameof(AuditAnchoredWriterPreparation));

    private FileAuditJournalSink RequirePreparedSink() =>
        _preparedSink ?? throw new ObjectDisposedException(nameof(AuditAnchoredWriterPreparation));

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>
/// Narrow crash recovery for the one state produced before checkpoint
/// publication. No other spool or control shape is ever removed here.
/// </summary>
internal static class AuditAnchoredWriterStartupPreflight
{
    internal static void RunUnderQuota(
        AuditOptions options,
        AuditSpoolQuotaLease retainedQuota)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(retainedQuota);
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
        {
            throw new ArgumentException(
                "Anchored writer preflight requires anchored protection mode.",
                nameof(options));
        }

        retainedQuota.VerifyOwnership();
        var initial = ReadTopology(options);
        var initialControls = ReadControls(options);
        var candidates = ClassifyCandidatesOrThrow(options, initial, initialControls);
        if (candidates.Length == 0)
            return;

        var retained = new List<RetainedCleanupFile>();
        try
        {
            foreach (var cleanup in candidates.SelectMany(candidate => candidate.CleanupFiles))
            {
                FileStream stream;
                try
                {
                    stream = new FileStream(
                        cleanup.Path,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.Delete,
                        bufferSize: 1,
                        FileOptions.WriteThrough);
                }
                catch (IOException exception)
                {
                    throw new IOException(
                        "A crash-left anchored writer preparation is still live.",
                        exception);
                }

                try
                {
                    _ = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
                        cleanup.Path,
                        stream.SafeFileHandle);
                    if (cleanup.ExpectedBytes is null)
                    {
                        if (stream.Length != 0)
                        {
                            throw new IOException(
                                "Crash-left anchored writer construction state is not empty.");
                        }
                    }
                    else
                    {
                        if (stream.Length != cleanup.ExpectedBytes.Length)
                        {
                            throw new IOException(
                                "A crash-left checkpoint temporary has invalid content.");
                        }
                        var bytes = new byte[cleanup.ExpectedBytes.Length];
                        stream.Position = 0;
                        stream.ReadExactly(bytes);
                        if (!bytes.AsSpan().SequenceEqual(cleanup.ExpectedBytes))
                        {
                            throw new IOException(
                                "A crash-left checkpoint temporary has invalid content.");
                        }
                    }
                    retained.Add(new RetainedCleanupFile(cleanup, stream));
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }

            retainedQuota.VerifyOwnership();
            var confirmed = ReadTopology(options);
            var confirmedControls = ReadControls(options);
            if (!initial.SequenceEqual(confirmed))
                throw new IOException("The anchored writer startup topology changed during preflight.");
            if (!initialControls.SequenceEqual(confirmedControls))
            {
                throw new IOException(
                    "Anchored writer control state changed during startup preflight.");
            }
            var confirmedCandidates = ClassifyCandidatesOrThrow(
                options,
                confirmed,
                confirmedControls);
            if (!CleanupPaths(candidates).SequenceEqual(CleanupPaths(confirmedCandidates)))
                throw new IOException("Anchored writer recovery authority changed during preflight.");

            foreach (var item in retained)
            {
                SecureAuditStorage.DeleteRetainedProtectedFile(
                    item.Cleanup.Root,
                    item.Cleanup.Path,
                    item.Stream.SafeFileHandle);
            }

            retainedQuota.VerifyOwnership();
            var deleted = retained
                .Select(item => item.Cleanup.Path)
                .ToHashSet(PathComparer);
            var expected = confirmed
                .Where(entry => !deleted.Contains(entry.Path))
                .ToArray();
            var afterDelete = ReadTopology(options);
            if (!expected.SequenceEqual(afterDelete))
                throw new IOException("Anchored writer preflight did not publish its exact removals.");
            var expectedControls = confirmedControls
                .Where(entry => !deleted.Contains(entry.Path))
                .ToArray();
            var afterDeleteControls = ReadControls(options);
            if (!expectedControls.SequenceEqual(afterDeleteControls))
            {
                throw new IOException(
                    "Anchored writer preflight changed unexpected checkpoint controls.");
            }
        }
        finally
        {
            foreach (var item in retained)
                item.Stream.Dispose();
        }
    }

    private static RecoveryCandidate[] ClassifyCandidatesOrThrow(
        AuditOptions options,
        TopologyEntry[] topology,
        ControlEntry[] controls)
    {
        var candidates = new List<RecoveryCandidate>();
        var controlsByBoot = controls
            .GroupBy(control => control.BootId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var bootsWithSegments = new HashSet<Guid>();
        foreach (var group in topology.GroupBy(entry => entry.Identity.SupervisorBootId))
        {
            var bootId = group.Key;
            bootsWithSegments.Add(bootId);
            controlsByBoot.TryGetValue(bootId, out var bootControls);
            bootControls ??= [];
            var checkpoint = bootControls.SingleOrDefault(control => control.IsCheckpoint);
            var persistentLock = bootControls.SingleOrDefault(control => !control.IsCheckpoint);
            if (checkpoint.Path is not null && persistentLock.Path is not null)
                continue;

            var entries = group.ToArray();
            if (entries.Length != 1 ||
                entries[0].Identity.Index != 0 ||
                entries[0].Length != 0)
            {
                throw new IOException(
                    "Uncontrolled anchored spool state is not an exact empty segment-zero preparation.");
            }

            var temporaries = ReadCheckpointTemporaries(options, bootId);
            if (checkpoint.Path is not null)
            {
                throw new IOException(
                    "A checkpoint-only anchored writer state cannot be recovered automatically.");
            }
            if (persistentLock.Path is null)
            {
                if (temporaries.Length != 0)
                {
                    throw new IOException(
                        "An uncontrolled anchored writer has unexpected checkpoint temporary state.");
                }
                candidates.Add(new RecoveryCandidate(
                    bootId,
                    [new CleanupFile(options.SpoolDirectory, entries[0].Path, null)]));
                continue;
            }

            var cleanup = new List<CleanupFile>();
            var expectedInitial = AuditExportCheckpointCodec.Serialize(
                AuditExportCheckpoint.Initial(bootId));
            foreach (var temporary in temporaries)
            {
                cleanup.Add(new CleanupFile(
                    options.RootDirectory,
                    temporary,
                    expectedInitial));
            }
            cleanup.Add(new CleanupFile(
                options.RootDirectory,
                persistentLock.Path,
                ExpectedBytes: null));
            cleanup.Add(new CleanupFile(
                options.SpoolDirectory,
                entries[0].Path,
                ExpectedBytes: null));
            candidates.Add(new RecoveryCandidate(bootId, cleanup.ToArray()));
        }

        foreach (var orphanControls in controlsByBoot.Where(
                     pair => !bootsWithSegments.Contains(pair.Key)))
        {
            var checkpoint = orphanControls.Value.Any(control => control.IsCheckpoint);
            var persistentLock = orphanControls.Value.Any(control => !control.IsCheckpoint);
            if (checkpoint && persistentLock)
                continue;
            throw new IOException(
                "Incomplete anchored writer control state has no spool segment.");
        }
        return candidates.ToArray();
    }

    private static TopologyEntry[] ReadTopology(AuditOptions options)
    {
        SecureAuditStorage.VerifyExternalProtectedDirectory(options.SpoolDirectory);
        var entries = new List<TopologyEntry>();
        var inventoryCount = 0;
        foreach (var entry in new DirectoryInfo(options.SpoolDirectory)
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            inventoryCount = checked(inventoryCount + 1);
            if (inventoryCount > AuditClosedSpoolChainReader.MaximumSpoolInventoryEntries)
            {
                throw new IOException(
                    "The anchored audit spool exceeds the bounded recovery inventory.");
            }
            if (entry is FileInfo control &&
                string.Equals(
                    control.Name,
                    AuditSpoolQuotaLease.ControlFileName,
                    StringComparison.Ordinal))
            {
                SecureAuditStorage.VerifyExternalProtectedFile(control.FullName);
                continue;
            }
            if (entry is not FileInfo file ||
                !AuditSpoolSegmentIdentity.TryParse(entry.Name, out var identity))
            {
                throw new IOException("The anchored audit spool contains an unknown entry.");
            }

            SecureAuditStorage.VerifyExternalProtectedFile(file.FullName);
            file.Refresh();
            entries.Add(new TopologyEntry(identity, file.FullName, file.Length));
        }
        return entries
            .OrderBy(entry => entry.Path, PathComparer)
            .ToArray();
    }

    private static ControlEntry[] ReadControls(AuditOptions options)
    {
        SecureAuditStorage.VerifyExternalProtectedDirectory(options.RootDirectory);
        var controls = new List<ControlEntry>();
        var inventoryCount = 0;
        foreach (var entry in new DirectoryInfo(options.RootDirectory)
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            inventoryCount = checked(inventoryCount + 1);
            if (inventoryCount > AuditClosedSpoolChainReader.MaximumSpoolInventoryEntries)
            {
                throw new IOException(
                    "The audit root exceeds the bounded writer-recovery inventory.");
            }
            if (!entry.Name.StartsWith("export.checkpoint-", StringComparison.Ordinal))
                continue;
            if (entry is not FileInfo file ||
                !AuditExportCheckpointStore.TryParseControlFileName(
                    entry.Name,
                    out var bootId,
                    out var isCheckpoint))
            {
                throw new IOException("The audit root contains malformed checkpoint control state.");
            }
            SecureAuditStorage.VerifyExternalProtectedFile(file.FullName);
            controls.Add(new ControlEntry(bootId, isCheckpoint, file.FullName));
        }
        return controls
            .OrderBy(control => control.Path, PathComparer)
            .ToArray();
    }

    private static string[] ReadCheckpointTemporaries(
        AuditOptions options,
        Guid bootId)
    {
        var prefix = "." + AuditExportCheckpointStore.CheckpointFileName(bootId) + ".";
        var temporaries = new List<string>();
        foreach (var entry in new DirectoryInfo(options.RootDirectory)
                     .EnumerateFileSystemInfos(prefix + "*", SearchOption.TopDirectoryOnly))
        {
            if (temporaries.Count >= 1 ||
                entry is not FileInfo file ||
                !AuditExportCheckpointStore.IsCanonicalTemporaryName(entry.Name, bootId))
            {
                throw new IOException(
                    "Anchored writer recovery found ambiguous checkpoint temporary state.");
            }
            SecureAuditStorage.VerifyExternalProtectedFile(file.FullName);
            temporaries.Add(file.FullName);
        }
        return temporaries.ToArray();
    }

    private static string[] CleanupPaths(IEnumerable<RecoveryCandidate> candidates) =>
        candidates
            .SelectMany(candidate => candidate.CleanupFiles)
            .Select(cleanup => cleanup.Path)
            .OrderBy(path => path, PathComparer)
            .ToArray();

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly record struct RecoveryCandidate(
        Guid BootId,
        CleanupFile[] CleanupFiles);

    private readonly record struct CleanupFile(
        string Root,
        string Path,
        byte[]? ExpectedBytes);

    private readonly record struct RetainedCleanupFile(
        CleanupFile Cleanup,
        FileStream Stream);

    private readonly record struct ControlEntry(
        Guid BootId,
        bool IsCheckpoint,
        string Path);

    private readonly record struct TopologyEntry(
        AuditSpoolSegmentIdentity Identity,
        string Path,
        long Length);
}
