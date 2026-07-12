using System.Buffers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PtkMcpServer.Audit;

internal enum AuditCompletedChainRetirementFaultPoint
{
    IntentPublished,
    SegmentDeleted,
    CheckpointDeleted,
    LockDeleted,
}

/// <summary>
/// Retires a fully acknowledged orphan chain without ever making a partial
/// deletion look like a fresh boot. A canonical durable intent contains the
/// exact chain-complete checkpoint before the first segment is removed.
/// Startup recovery treats that intent as the sole authority to finish an
/// interrupted retirement.
/// </summary>
internal static class AuditCompletedChainRetirement
{
    private const string SchemaVersion = "ptk.audit-retirement/1";
    private const string FilePrefix = "audit.retirement-";
    private const string FileSuffix = ".json";
    private const string TemporarySuffix = ".tmp";
    private const int MaximumIntentBytes = 32 * 1024;
    private static readonly HashSet<string> IntentProperties = new(
        [
            "schema_version",
            "supervisor_boot_id",
            "retained_floor_index",
            "retained_final_index",
            "checkpoint_base64",
        ],
        StringComparer.Ordinal);

    internal static bool TryRetire(
        AuditOptions options,
        Guid supervisorBootId,
        DateTimeOffset utcNow,
        long requiredHeadroomBytes,
        Action<AuditCompletedChainRetirementFaultPoint, int>? faultInjector = null)
    {
        return TryRetireCore(
            options,
            supervisorBootId,
            utcNow,
            requiredHeadroomBytes,
            acceptFullyAbsentObservedBoot: false,
            faultInjector);
    }

    internal static bool TryRetireObservedCompleted(
        AuditOptions options,
        Guid supervisorBootId,
        DateTimeOffset utcNow,
        long requiredHeadroomBytes)
    {
        return TryRetireCore(
            options,
            supervisorBootId,
            utcNow,
            requiredHeadroomBytes,
            acceptFullyAbsentObservedBoot: true,
            faultInjector: null);
    }

    private static bool TryRetireCore(
        AuditOptions options,
        Guid supervisorBootId,
        DateTimeOffset utcNow,
        long requiredHeadroomBytes,
        bool acceptFullyAbsentObservedBoot,
        Action<AuditCompletedChainRetirementFaultPoint, int>? faultInjector)
    {
        ValidateArguments(options, supervisorBootId, utcNow, requiredHeadroomBytes);
        if (!AuditSpoolQuotaLease.TryAcquireExisting(options.SpoolDirectory, out var quota))
            return false;

        using (quota)
        {
            var recovered = RecoverUnderQuota(options, quota, faultInjector);
            if (recovered.Contains(supervisorBootId))
                return true;
            if (acceptFullyAbsentObservedBoot &&
                IsFullyAbsent(options, supervisorBootId))
            {
                return true;
            }

            if (!AuditExportCheckpointStore.TryAcquireExisting(
                    options,
                    supervisorBootId,
                    out var acquiredStore))
            {
                return false;
            }

            var store = acquiredStore ?? throw new IOException(
                "Completed-chain retirement acquired no checkpoint owner.");
            var storeDisposed = false;
            try
            {
                var checkpoint = store.Current;
                var inventory = InventorySpool(options);
                var target = ValidateCompleteTarget(checkpoint, inventory);
                var totalBytes = TotalBytes(inventory);
                var maximumRetainedBytes = checked(
                    options.AggregateBytes - requiredHeadroomBytes);
                var ageEligible = target.All(segment =>
                    utcNow - new DateTimeOffset(segment.LastWriteTimeUtc, TimeSpan.Zero) >=
                    options.RetentionAge);
                var pressureEligible = totalBytes > maximumRetainedBytes;
                if (!ageEligible && !pressureEligible)
                    return false;

                var checkpointBytes = AuditExportCheckpointCodec.Serialize(checkpoint);
                var intent = new RetirementIntent(
                    supervisorBootId,
                    target[0].Identity.Index,
                    target[^1].Identity.Index,
                    checkpointBytes);
                PublishIntent(options, intent);
                faultInjector?.Invoke(
                    AuditCompletedChainRetirementFaultPoint.IntentPublished,
                    0);

                DeleteSegments(
                    options,
                    target,
                    faultInjector);
                RequireBootSegmentsAbsent(options, supervisorBootId);
                DeleteExactFile(
                    options.RootDirectory,
                    store.CheckpointPath,
                    checkpointBytes);
                faultInjector?.Invoke(
                    AuditCompletedChainRetirementFaultPoint.CheckpointDeleted,
                    0);

                var lockPath = store.LockPath;
                store.Dispose();
                storeDisposed = true;
                DeleteExactFile(
                    options.RootDirectory,
                    lockPath,
                    ReadOnlyMemory<byte>.Empty);
                faultInjector?.Invoke(
                    AuditCompletedChainRetirementFaultPoint.LockDeleted,
                    0);
                DeleteIntent(options, intent);
                quota.VerifyOwnership();
                return true;
            }
            finally
            {
                if (!storeDisposed)
                    store.Dispose();
            }
        }
    }

    private static bool IsFullyAbsent(
        AuditOptions options,
        Guid supervisorBootId)
    {
        var checkpointPath = Path.Combine(
            options.RootDirectory,
            AuditExportCheckpointStore.CheckpointFileName(supervisorBootId));
        var lockPath = Path.Combine(
            options.RootDirectory,
            AuditExportCheckpointStore.LockFileName(supervisorBootId));
        if (EntryExists(checkpointPath) || EntryExists(lockPath))
            return false;
        return !InventorySpool(options).Any(segment =>
            segment.Identity.SupervisorBootId == supervisorBootId);
    }

    /// <summary>
    /// Completes every durably authorized retirement while the caller owns the
    /// global spool topology. Canonical unpublished temporaries are discarded:
    /// no deletion is allowed before publication, so they grant no authority.
    /// </summary>
    internal static IReadOnlySet<Guid> RecoverUnderQuota(
        AuditOptions options,
        AuditSpoolQuotaLease retainedQuota,
        Action<AuditCompletedChainRetirementFaultPoint, int>? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(retainedQuota);
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
        {
            throw new ArgumentException(
                "Completed-chain retirement requires anchored protection.",
                nameof(options));
        }

        retainedQuota.VerifyOwnership();
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(options.RootDirectory));
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        var intentPaths = new List<(Guid BootId, string Path)>();
        var temporaries = new List<(Guid BootId, string Path)>();
        var entries = 0;
        foreach (var entry in new DirectoryInfo(root)
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            entries = checked(entries + 1);
            if (entries > AuditClosedSpoolChainReader.MaximumSpoolInventoryEntries)
            {
                throw new IOException(
                    "The audit root exceeds its retirement recovery bound.");
            }
            if (entry is not FileInfo file)
            {
                if (entry.Name.StartsWith(FilePrefix, StringComparison.Ordinal) ||
                    entry.Name.StartsWith("." + FilePrefix, StringComparison.Ordinal))
                {
                    throw new IOException("The audit root contains malformed retirement state.");
                }
                continue;
            }
            if (TryParseIntentFileName(file.Name, out var bootId))
            {
                SecureAuditStorage.VerifyExternalProtectedFile(file.FullName);
                intentPaths.Add((bootId, file.FullName));
                continue;
            }
            if (TryParseTemporaryFileName(file.Name, out var temporaryBootId))
            {
                SecureAuditStorage.VerifyExternalProtectedFile(file.FullName);
                temporaries.Add((temporaryBootId, file.FullName));
                continue;
            }
            if (file.Name.StartsWith(FilePrefix, StringComparison.Ordinal) ||
                file.Name.StartsWith("." + FilePrefix, StringComparison.Ordinal))
            {
                throw new IOException("The audit root contains malformed retirement state.");
            }
        }

        if (intentPaths.Select(value => value.BootId).Distinct().Count() !=
            intentPaths.Count)
        {
            throw new IOException("The audit root contains duplicate retirement authority.");
        }

        foreach (var group in temporaries.GroupBy(value => value.BootId))
        {
            var published = intentPaths.SingleOrDefault(value => value.BootId == group.Key);
            if (published.Path is null)
            {
                foreach (var temporary in group)
                    DeleteAnyProtectedFile(root, temporary.Path);
                continue;
            }
            var aliases = group.ToArray();
            if (aliases.Length != 1)
            {
                throw new IOException(
                    "The audit root contains ambiguous retirement publication aliases.");
            }
            RecoverPublishedAlias(root, published.Path, aliases[0].Path);
        }

        var intents = new List<(RetirementIntent Intent, string Path)>();
        foreach (var published in intentPaths)
        {
            var bytes = SecureAuditStorage.ReadProtectedFile(
                published.Path,
                MaximumIntentBytes);
            var intent = ParseIntent(bytes);
            if (intent.SupervisorBootId != published.BootId)
                throw new IOException("An audit retirement intent names another boot.");
            intents.Add((intent, published.Path));
        }

        var recovered = new HashSet<Guid>();
        foreach (var item in intents.OrderBy(
                     value => value.Intent.SupervisorBootId.ToString("N"),
                     StringComparer.Ordinal))
        {
            RecoverIntent(options, item.Intent, item.Path, faultInjector);
            recovered.Add(item.Intent.SupervisorBootId);
        }
        retainedQuota.VerifyOwnership();
        return recovered;
    }

    private static void RecoverPublishedAlias(
        string root,
        string publishedPath,
        string temporaryPath)
    {
        using var published = OpenPublishedAlias(publishedPath);
        using var temporary = OpenPublishedAlias(temporaryPath);
        if (published.Length is < 2 or > MaximumIntentBytes ||
            published.Length != temporary.Length)
        {
            throw new IOException("The retirement publication alias length is invalid.");
        }
        var publishedBytes = new byte[checked((int)published.Length)];
        var temporaryBytes = new byte[publishedBytes.Length];
        try
        {
            published.Position = 0;
            published.ReadExactly(publishedBytes);
            temporary.Position = 0;
            temporary.ReadExactly(temporaryBytes);
            if (!publishedBytes.AsSpan().SequenceEqual(temporaryBytes))
                throw new IOException("The retirement publication alias content differs.");
            _ = ParseIntent(publishedBytes);
            SecureAuditStorage.RemoveRetainedPublishedAlias(
                root,
                publishedPath,
                published.SafeFileHandle,
                temporaryPath,
                temporary.SafeFileHandle);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publishedBytes);
            CryptographicOperations.ZeroMemory(temporaryBytes);
        }
    }

    private static FileStream OpenPublishedAlias(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Delete,
            bufferSize: 1,
            FileOptions.WriteThrough);
        try
        {
            _ = SecureAuditStorage.VerifyRetainedPublishedAliasIdentity(
                path,
                stream.SafeFileHandle);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void RecoverIntent(
        AuditOptions options,
        RetirementIntent intent,
        string intentPath,
        Action<AuditCompletedChainRetirementFaultPoint, int>? faultInjector)
    {
        var checkpointPath = Path.Combine(
            options.RootDirectory,
            AuditExportCheckpointStore.CheckpointFileName(intent.SupervisorBootId));
        var lockPath = Path.Combine(
            options.RootDirectory,
            AuditExportCheckpointStore.LockFileName(intent.SupervisorBootId));
        var checkpointExists = EntryExists(checkpointPath);
        var lockExists = EntryExists(lockPath);
        var target = InventorySpool(options)
            .Where(segment =>
                segment.Identity.SupervisorBootId == intent.SupervisorBootId)
            .OrderBy(segment => segment.Identity.Index)
            .ToArray();

        ValidateRecoveryTopology(intent, checkpointExists, lockExists, target);
        if (lockExists)
        {
            using var retainedLock = OpenRetained(lockPath);
            RequireExactBytes(retainedLock, ReadOnlySpan<byte>.Empty);
            if (checkpointExists)
            {
                DeleteSegments(options, target, faultInjector);
                RequireBootSegmentsAbsent(options, intent.SupervisorBootId);
                DeleteExactFile(
                    options.RootDirectory,
                    checkpointPath,
                    intent.CheckpointBytes);
                faultInjector?.Invoke(
                    AuditCompletedChainRetirementFaultPoint.CheckpointDeleted,
                    0);
            }
            SecureAuditStorage.DeleteRetainedProtectedFile(
                options.RootDirectory,
                lockPath,
                retainedLock.SafeFileHandle);
            faultInjector?.Invoke(
                AuditCompletedChainRetirementFaultPoint.LockDeleted,
                0);
        }
        DeleteExactFile(
            options.RootDirectory,
            intentPath,
            SerializeIntent(intent));
    }

    private static void ValidateRecoveryTopology(
        RetirementIntent intent,
        bool checkpointExists,
        bool lockExists,
        SegmentSnapshot[] target)
    {
        if (!lockExists)
        {
            if (checkpointExists || target.Length != 0)
            {
                throw new IOException(
                    "Retirement state lost its lease before protected data was removed.");
            }
            return;
        }
        if (!checkpointExists)
        {
            if (target.Length != 0)
            {
                throw new IOException(
                    "Retirement removed its checkpoint before all segments.");
            }
            return;
        }
        if (target.Length == 0)
            return;
        if (target[^1].Identity.Index != intent.RetainedFinalIndex ||
            target[0].Identity.Index < intent.RetainedFloorIndex)
        {
            throw new IOException("Retirement recovery found a segment outside its authority.");
        }
        for (var index = 1; index < target.Length; index++)
        {
            if (target[index].Identity.Index != target[index - 1].Identity.Index + 1)
                throw new IOException("Retirement recovery found a non-suffix segment gap.");
        }
    }

    private static SegmentSnapshot[] ValidateCompleteTarget(
        AuditExportCheckpoint checkpoint,
        SegmentSnapshot[] inventory)
    {
        if (!checkpoint.ChainComplete || checkpoint.BlockedRecord is not null)
            throw new IOException("Only an unblocked chain-complete checkpoint may retire.");
        var target = inventory
            .Where(segment =>
                segment.Identity.SupervisorBootId == checkpoint.SupervisorBootId)
            .OrderBy(segment => segment.Identity.Index)
            .ToArray();
        if (target.Length == 0)
            throw new IOException("A completed audit chain has no retained segment.");
        for (var index = 1; index < target.Length; index++)
        {
            if (target[index].Identity.Index != target[index - 1].Identity.Index + 1)
                throw new IOException("A completed audit chain has an internal segment gap.");
        }

        if (checkpoint.Spool is { } final)
        {
            if (target[^1].Identity != final ||
                target[^1].Length != checkpoint.ByteOffset)
            {
                throw new IOException(
                    "The chain-complete checkpoint does not prove the final retained EOF.");
            }
        }
        else if (checkpoint.Sequence != 0 ||
                 target.Length != 1 ||
                 target[0].Identity.Index != 0 ||
                 target[0].Length != 0)
        {
            throw new IOException("An empty chain-complete checkpoint has unexpected spool data.");
        }
        return target;
    }

    private static SegmentSnapshot[] InventorySpool(AuditOptions options)
    {
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(options.SpoolDirectory));
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        var segments = new List<SegmentSnapshot>();
        var entries = 0;
        foreach (var entry in new DirectoryInfo(root)
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            entries = checked(entries + 1);
            if (entries > AuditClosedSpoolChainReader.MaximumSpoolInventoryEntries)
            {
                throw new IOException(
                    "The audit spool exceeds its retirement inventory bound.");
            }
            if (entry is FileInfo quota && string.Equals(
                    quota.Name,
                    AuditSpoolQuotaLease.ControlFileName,
                    StringComparison.Ordinal))
            {
                SecureAuditStorage.VerifyExternalProtectedFile(quota.FullName);
                continue;
            }
            if (entry is not FileInfo file ||
                !AuditSpoolSegmentIdentity.TryParse(file.Name, out var identity))
            {
                throw new IOException("The audit spool contains unknown retirement state.");
            }
            SecureAuditStorage.VerifyExternalProtectedFile(file.FullName);
            file.Refresh();
            if (file.Length < 0 || file.Length > options.SegmentBytes)
                throw new IOException("An audit segment exceeds the retirement bound.");
            segments.Add(new SegmentSnapshot(
                identity,
                file.FullName,
                file.Length,
                file.LastWriteTimeUtc));
        }
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        return segments.ToArray();
    }

    private static void RequireBootSegmentsAbsent(
        AuditOptions options,
        Guid supervisorBootId)
    {
        if (InventorySpool(options).Any(segment =>
                segment.Identity.SupervisorBootId == supervisorBootId))
        {
            throw new IOException(
                "Completed-chain retirement did not remove its exact boot topology.");
        }
    }

    private static void DeleteSegments(
        AuditOptions options,
        IEnumerable<SegmentSnapshot> segments,
        Action<AuditCompletedChainRetirementFaultPoint, int>? faultInjector)
    {
        var retained = new List<(SegmentSnapshot Segment, FileStream Stream)>();
        try
        {
            foreach (var segment in segments)
            {
                var stream = OpenRetained(segment.Path);
                try
                {
                    if (stream.Length != segment.Length)
                        throw new IOException("An audit segment changed before retirement.");
                    retained.Add((segment, stream));
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            var deleted = 0;
            foreach (var item in retained)
            {
                SecureAuditStorage.DeleteRetainedProtectedFile(
                    options.SpoolDirectory,
                    item.Segment.Path,
                    item.Stream.SafeFileHandle);
                deleted = checked(deleted + 1);
                faultInjector?.Invoke(
                    AuditCompletedChainRetirementFaultPoint.SegmentDeleted,
                    deleted);
            }
        }
        finally
        {
            foreach (var item in retained)
                item.Stream.Dispose();
        }
    }

    private static FileStream OpenRetained(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Delete,
            bufferSize: 1,
            FileOptions.WriteThrough);
        try
        {
            _ = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
                path,
                stream.SafeFileHandle);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void DeleteExactFile(
        string root,
        string path,
        ReadOnlyMemory<byte> expectedBytes)
    {
        using var stream = OpenRetained(path);
        RequireExactBytes(stream, expectedBytes.Span);
        SecureAuditStorage.DeleteRetainedProtectedFile(
            root,
            path,
            stream.SafeFileHandle);
    }

    private static void DeleteAnyProtectedFile(string root, string path)
    {
        using var stream = OpenRetained(path);
        SecureAuditStorage.DeleteRetainedProtectedFile(
            root,
            path,
            stream.SafeFileHandle);
    }

    private static void RequireExactBytes(FileStream stream, ReadOnlySpan<byte> expected)
    {
        if (stream.Length != expected.Length)
            throw new IOException("Retirement control content changed.");
        var bytes = new byte[expected.Length];
        try
        {
            stream.Position = 0;
            stream.ReadExactly(bytes);
            if (!bytes.AsSpan().SequenceEqual(expected))
                throw new IOException("Retirement control content changed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void PublishIntent(AuditOptions options, RetirementIntent intent)
    {
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(options.RootDirectory));
        var path = Path.Combine(root, IntentFileName(intent.SupervisorBootId));
        if (EntryExists(path))
            throw new IOException("An audit chain already has retirement authority.");
        var temporary = Path.Combine(
            root,
            TemporaryFileName(intent.SupervisorBootId, Guid.NewGuid()));
        var bytes = SerializeIntent(intent);
        try
        {
            using (var stream = SecureAuditStorage.CreateExclusiveFile(temporary))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            SecureAuditStorage.PublishAtomically(temporary, path, root);
            var persisted = SecureAuditStorage.ReadProtectedFile(path, MaximumIntentBytes);
            if (!persisted.AsSpan().SequenceEqual(bytes))
                throw new IOException("The audit retirement intent was not published exactly.");
        }
        catch
        {
            // A failure after the atomic rename deliberately leaves the
            // published intent for recovery. Only an unpublished temporary is
            // safe to remove here.
            if (!EntryExists(path))
                SecureAuditStorage.TryDelete(temporary);
            throw;
        }
    }

    private static void DeleteIntent(AuditOptions options, RetirementIntent intent)
    {
        var path = Path.Combine(
            options.RootDirectory,
            IntentFileName(intent.SupervisorBootId));
        try
        {
            DeleteExactFile(
                options.RootDirectory,
                path,
                SerializeIntent(intent));
        }
        catch (Exception exception) when (!IsFatal(exception) && !EntryExists(path))
        {
            // The exact intent was removed, but a post-unlink durability/path
            // check failed. If the name reappears after a crash, recovery can
            // safely consume it against the already-empty retired topology.
        }
    }

    private static byte[] SerializeIntent(RetirementIntent intent)
    {
        ValidateIntent(intent);
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.Default,
                       Indented = false,
                       SkipValidation = false,
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", SchemaVersion);
            writer.WriteString(
                "supervisor_boot_id",
                intent.SupervisorBootId.ToString("D"));
            writer.WriteNumber("retained_floor_index", intent.RetainedFloorIndex);
            writer.WriteNumber("retained_final_index", intent.RetainedFinalIndex);
            writer.WriteBase64String("checkpoint_base64", intent.CheckpointBytes);
            writer.WriteEndObject();
            writer.Flush();
        }
        if (buffer.WrittenCount >= MaximumIntentBytes)
            throw new IOException("The audit retirement intent exceeds its bound.");
        var bytes = new byte[checked(buffer.WrittenCount + 1)];
        buffer.WrittenSpan.CopyTo(bytes);
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    private static RetirementIntent ParseIntent(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            if (bytes.Length is < 2 or > MaximumIntentBytes || bytes.Span[^1] != (byte)'\n')
                throw new FormatException();
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 3,
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException();
            var names = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != IntentProperties.Count ||
                names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                names.Any(name => !IntentProperties.Contains(name)))
            {
                throw new FormatException();
            }
            if (!string.Equals(
                    root.GetProperty("schema_version").GetString(),
                    SchemaVersion,
                    StringComparison.Ordinal))
            {
                throw new FormatException();
            }
            var bootText = root.GetProperty("supervisor_boot_id").GetString();
            if (!Guid.TryParseExact(bootText, "D", out var bootId) ||
                !string.Equals(bootText, bootId.ToString("D"), StringComparison.Ordinal))
            {
                throw new FormatException();
            }
            var floor = root.GetProperty("retained_floor_index").GetInt32();
            var final = root.GetProperty("retained_final_index").GetInt32();
            var checkpoint = root.GetProperty("checkpoint_base64").GetBytesFromBase64();
            var intent = new RetirementIntent(bootId, floor, final, checkpoint);
            ValidateIntent(intent);
            if (!bytes.Span.SequenceEqual(SerializeIntent(intent)))
                throw new FormatException();
            return intent;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new IOException("The audit retirement intent is invalid.");
        }
    }

    private static void ValidateIntent(RetirementIntent intent)
    {
        AuditSpoolSegmentIdentity.RequireUuidV4(
            intent.SupervisorBootId,
            nameof(intent.SupervisorBootId));
        if (intent.RetainedFloorIndex < 0 ||
            intent.RetainedFinalIndex < intent.RetainedFloorIndex ||
            intent.RetainedFinalIndex >= AuditClosedSpoolChainReader.MaximumClosedChainSegments)
        {
            throw new IOException("The audit retirement segment range is invalid.");
        }
        var checkpoint = AuditExportCheckpointCodec.Parse(intent.CheckpointBytes);
        if (checkpoint.SupervisorBootId != intent.SupervisorBootId ||
            !checkpoint.ChainComplete ||
            checkpoint.BlockedRecord is not null)
        {
            throw new IOException("The audit retirement checkpoint is not chain-complete.");
        }
        if (checkpoint.Spool is { } final &&
            final.Index != intent.RetainedFinalIndex)
        {
            throw new IOException("The audit retirement checkpoint final segment changed.");
        }
        if (checkpoint.Spool is null &&
            (checkpoint.Sequence != 0 ||
             intent.RetainedFloorIndex != 0 ||
             intent.RetainedFinalIndex != 0))
        {
            throw new IOException("The empty retirement checkpoint has an invalid range.");
        }
    }

    private static void ValidateArguments(
        AuditOptions options,
        Guid supervisorBootId,
        DateTimeOffset utcNow,
        long requiredHeadroomBytes)
    {
        ArgumentNullException.ThrowIfNull(options);
        AuditSpoolSegmentIdentity.RequireUuidV4(supervisorBootId, nameof(supervisorBootId));
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
        {
            throw new ArgumentException(
                "Completed-chain retirement requires anchored protection.",
                nameof(options));
        }
        if (utcNow.Offset != TimeSpan.Zero)
            throw new ArgumentException("The retirement clock must be UTC.", nameof(utcNow));
        if (requiredHeadroomBytes < 0 || requiredHeadroomBytes > options.AggregateBytes)
            throw new ArgumentOutOfRangeException(nameof(requiredHeadroomBytes));
    }

    private static long TotalBytes(IEnumerable<SegmentSnapshot> segments)
    {
        var total = 0L;
        foreach (var segment in segments)
            total = checked(total + segment.Length);
        return total;
    }

    private static string IntentFileName(Guid bootId) =>
        FilePrefix + bootId.ToString("N") + FileSuffix;

    private static string TemporaryFileName(Guid bootId, Guid temporaryId) =>
        "." + FilePrefix + bootId.ToString("N") + "." +
        temporaryId.ToString("N") + TemporarySuffix;

    private static bool TryParseIntentFileName(string name, out Guid bootId)
    {
        bootId = default;
        if (!name.StartsWith(FilePrefix, StringComparison.Ordinal) ||
            !name.EndsWith(FileSuffix, StringComparison.Ordinal))
        {
            return false;
        }
        var value = name.AsSpan(
            FilePrefix.Length,
            name.Length - FilePrefix.Length - FileSuffix.Length);
        return value.Length == 32 &&
               Guid.TryParseExact(value, "N", out bootId) &&
               AuditSpoolSegmentIdentity.IsUuidV4(bootId);
    }

    private static bool TryParseTemporaryFileName(string name, out Guid bootId)
    {
        bootId = default;
        if (!name.StartsWith("." + FilePrefix, StringComparison.Ordinal) ||
            !name.EndsWith(TemporarySuffix, StringComparison.Ordinal))
        {
            return false;
        }
        var stem = name.AsSpan(
            1 + FilePrefix.Length,
            name.Length - (1 + FilePrefix.Length) - TemporarySuffix.Length);
        var separator = stem.IndexOf('.');
        if (separator != 32 || stem.Length != 32 + 1 + 32)
            return false;
        return Guid.TryParseExact(stem[..separator], "N", out bootId) &&
               AuditSpoolSegmentIdentity.IsUuidV4(bootId) &&
               Guid.TryParseExact(stem[(separator + 1)..], "N", out var temporaryId) &&
               AuditSpoolSegmentIdentity.IsUuidV4(temporaryId);
    }

    private static bool EntryExists(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        return file.Exists || file.LinkTarget is not null || Directory.Exists(path);
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed record RetirementIntent(
        Guid SupervisorBootId,
        int RetainedFloorIndex,
        int RetainedFinalIndex,
        byte[] CheckpointBytes);

    private readonly record struct SegmentSnapshot(
        AuditSpoolSegmentIdentity Identity,
        string Path,
        long Length,
        DateTime LastWriteTimeUtc);
}
