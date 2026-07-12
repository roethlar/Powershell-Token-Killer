using System.Buffers;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PtkMcpServer.Audit;

internal enum FileAuditSinkFaultPoint
{
    PhysicalAllocation,
    BeforePhysicalFlush,
    AfterPhysicalFlush,
    MacCompactionCopy,
    MacCompactionTemporaryDurable,
    MacCompactionPublished,
    MacCompactionDirectoryFlushStarting,
}

internal enum AuditCommittedSpoolReadStatus
{
    Data,
    AtCommittedTail,
    Rotated,
    WriterClosed,
    NotCurrent,
}

internal readonly record struct AuditCommittedSpoolRead(
    AuditCommittedSpoolReadStatus Status,
    ReadOnlyMemory<byte> Bytes,
    long CommittedTail,
    AuditSpoolSegmentIdentity? CurrentSegment);

internal interface IAuditCommittedSpoolSource
{
    AuditSpoolSegmentIdentity CurrentSegmentIdentity { get; }

    // The authoritative writer is deliberately lock-free internally. Product
    // callers must hold AuditJournal's gate across this method; direct calls
    // exist only as storage fault-seam tests.
    AuditCommittedSpoolReadStatus TryReadCommitted(
        AuditSpoolSegmentIdentity identity,
        long offset,
        Span<byte> destination,
        out int bytesRead,
        out long committedTail);
}

/// <summary>
/// Protected append-only JSONL segments. Each live segment is held with an
/// exclusive cross-process file lock, physically allocated without extending
/// logical EOF, and excluded from retention. Closed local-only segments are
/// eligible for age/aggregate retention; anchored segments are never swept.
/// </summary>
internal sealed class FileAuditJournalSink : IAuditJournalSink, IAuditCommittedSpoolSource
{
    private const string AllocatingSuffix = ".allocating";
    private const string CompactingSuffix = ".compacting";
    private const int AllocationIdLength = 32;
    private readonly AuditOptions _options;
    private readonly Guid _supervisorBootId;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<FileAuditSinkFaultPoint, int, bool>? _faultInjector;
    private readonly Func<SafeFileHandle, string, int> _macCloneFile;
    private readonly string _spoolRoot;
    private IDisposable? _exportWriterLease;
    private FileStream _stream;
    private string _currentSegmentPath;
    private AuditSpoolSegmentIdentity _currentSegmentIdentity;
    private long _committedSegmentBytes;
    private int _segmentIndex;
    private int _allocationAttempt;
    private int _flushAttempt;
    private int _compactionAttempt;
    private bool _currentPathFailureReported;
    private bool _disposed;

    internal FileAuditJournalSink(
        AuditOptions options,
        Guid supervisorBootId,
        Func<DateTimeOffset>? utcNow = null,
        Func<FileAuditSinkFaultPoint, int, bool>? faultInjector = null,
        AuditExportCheckpointStore? checkpointStore = null,
        Func<SafeFileHandle, string, int>? macCloneFile = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        AuditSpoolSegmentIdentity.RequireUuidV4(supervisorBootId, nameof(supervisorBootId));
        _options = options;
        _supervisorBootId = supervisorBootId;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _faultInjector = faultInjector;
        _macCloneFile = macCloneFile ?? MacNative.CloneFile;
        _spoolRoot = SecureAuditStorage.PrepareRoot(options.SpoolDirectory);
        IDisposable? exportWriterLease = null;
        if (options.ProtectionMode == AuditProtectionMode.Anchored)
        {
            exportWriterLease = checkpointStore?.RetainInitialJournalWriter(
                options,
                supervisorBootId) ?? throw new InvalidOperationException(
                "Anchored audit requires an active matching checkpoint owner.");
        }
        else if (checkpointStore is not null)
        {
            throw new ArgumentException(
                "Local-only audit cannot retain an export checkpoint owner.",
                nameof(checkpointStore));
        }

        try
        {
            using (AuditSpoolQuotaLease.CreateControlAndAcquire(_spoolRoot))
            {
                RecoverCrashAllocationsAndValidateSpool();
                ValidateRetainedSegments();
                // A hard-killed prior supervisor could not trim the keep-EOF
                // allocation on its last segment. Reclaim that invisible physical
                // slack before charging the new supervisor's full live segment.
                ReclaimClosedAllocations();
                EnsurePhysicalAllocationAvailable();
                (_stream, _currentSegmentPath, _currentSegmentIdentity) = CreateSegment(0);
                SweepClosedSegments(GetUtcNow(), options.EmergencyReserveBytes);
            }
            _exportWriterLease = exportWriterLease;
            exportWriterLease = null;
        }
        catch
        {
            _stream?.Dispose();
            throw;
        }
        finally
        {
            exportWriterLease?.Dispose();
        }
    }

    public long SegmentCapacityBytes => _options.SegmentBytes;

    public long AggregateCapacityBytes => _options.AggregateBytes;

    public long CurrentSegmentBytes
    {
        get
        {
            ThrowIfDisposed();
            ValidateCurrentSegmentPath();
            return _stream.Length;
        }
    }

    public long TotalBytes
    {
        get
        {
            ThrowIfDisposed();
            using (AcquireQuotaLock())
            {
                RecoverCrashAllocationsAndValidateSpool();
                return TotalBytesUnderQuotaLock();
            }
        }
    }

    internal string CurrentSegmentPath
    {
        get
        {
            ThrowIfDisposed();
            return _currentSegmentPath;
        }
    }

    internal AuditSpoolSegmentIdentity CurrentSegmentIdentity
    {
        get
        {
            ThrowIfDisposed();
            return _currentSegmentIdentity;
        }
    }

    AuditSpoolSegmentIdentity IAuditCommittedSpoolSource.CurrentSegmentIdentity =>
        _currentSegmentIdentity;

    public bool CanReserve(long reservedBytes)
    {
        ThrowIfDisposed();
        using (AcquireQuotaLock())
        {
            RecoverCrashAllocationsAndValidateSpool();
            ValidateCurrentSegmentPath();
            if (reservedBytes < 0 || reservedBytes > SegmentCapacityBytes)
                return false;

            var now = GetUtcNow();
            SweepClosedSegments(now, reservedBytes);
            if (TotalBytesUnderQuotaLock() > AggregateCapacityBytes - reservedBytes)
                return false;

            if (_stream.Length > SegmentCapacityBytes - reservedBytes)
            {
                Rotate();
                SweepClosedSegments(now, reservedBytes);
            }

            return _stream.Length <= SegmentCapacityBytes - reservedBytes &&
                   TotalBytesUnderQuotaLock() <= AggregateCapacityBytes - reservedBytes;
        }
    }

    public void Append(ReadOnlyMemory<byte> line)
    {
        ThrowIfDisposed();
        ValidateCurrentSegmentPath();
        if (line.Length < 1 || line.Length > _options.MaxRecordBytes || line.Span[^1] != (byte)'\n')
            throw new IOException("The audit record does not fit the configured JSONL record contract.");
        if (_stream.Length > SegmentCapacityBytes - line.Length)
            throw new IOException("The live audit segment has no committed capacity for this record.");

        // This is deliberately the only write call for the authoritative line.
        _stream.Write(line.Span);
    }

    public void FlushToDisk()
    {
        ThrowIfDisposed();
        ValidateCurrentSegmentPath(requireLengthMatch: false);
        var attempt = checked(++_flushAttempt);
        if (_faultInjector?.Invoke(FileAuditSinkFaultPoint.BeforePhysicalFlush, attempt) == true)
            throw new IOException("Injected pre-flush audit failure.");
        _stream.Flush(flushToDisk: true);
        // Once Flush(true) returns, these exact bytes can survive this process
        // and will be read from the closed segment after rotation or restart.
        // Publish the same durable boundary to the live reader even if a
        // subsequent path check poisons the journal; hiding it only in memory
        // would make live and recovered export disagree.
        _committedSegmentBytes = _stream.Length;
        if (_faultInjector?.Invoke(FileAuditSinkFaultPoint.AfterPhysicalFlush, attempt) == true)
            throw new IOException("Injected post-flush audit failure.");
        ValidateCurrentSegmentPath();
    }

    public AuditCommittedSpoolReadStatus TryReadCommitted(
        AuditSpoolSegmentIdentity identity,
        long offset,
        Span<byte> destination,
        out int bytesRead,
        out long committedTail)
    {
        bytesRead = 0;
        committedTail = 0;
        if (identity.SupervisorBootId != _currentSegmentIdentity.SupervisorBootId ||
            identity.Index > _currentSegmentIdentity.Index)
            return AuditCommittedSpoolReadStatus.NotCurrent;
        if (_disposed)
            return AuditCommittedSpoolReadStatus.WriterClosed;
        if (identity.Index < _currentSegmentIdentity.Index)
            return AuditCommittedSpoolReadStatus.Rotated;
        if (identity != _currentSegmentIdentity)
            return AuditCommittedSpoolReadStatus.NotCurrent;
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (destination.Length < 1)
            throw new ArgumentException("A committed audit read requires a destination.", nameof(destination));

        ValidateCurrentSegmentPath(requireLengthMatch: false);
        committedTail = _committedSegmentBytes;
        if (new FileInfo(_currentSegmentPath).Length < committedTail)
            throw new IOException("The live audit segment is shorter than its durable tail.");
        if (offset > committedTail)
            throw new IOException("The committed audit read offset is beyond the durable tail.");
        if (offset == committedTail)
            return AuditCommittedSpoolReadStatus.AtCommittedTail;

        var required = checked((int)Math.Min(destination.Length, committedTail - offset));
        while (bytesRead < required)
        {
            var read = RandomAccess.Read(
                _stream.SafeFileHandle,
                destination.Slice(bytesRead, required - bytesRead),
                offset + bytesRead);
            if (read == 0)
                throw new IOException("The committed audit segment ended before its durable tail.");
            bytesRead += read;
        }
        ValidateCurrentSegmentPath(requireLengthMatch: false);
        if (_committedSegmentBytes != committedTail)
            throw new IOException("The committed audit tail changed during a synchronized read.");
        return AuditCommittedSpoolReadStatus.Data;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        ExceptionDispatchInfo? failure = null;
        try
        {
            using (AcquireQuotaLock())
                CloseAndTrimCurrent();
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            // Quota acquisition can fail before CloseAndTrimCurrent owns the
            // stream. The authoritative live handle must still be gone before
            // checkpoint ownership becomes adoptable.
            _stream.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }

        _disposed = true;
        try
        {
            Interlocked.Exchange(ref _exportWriterLease, null)?.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }

        failure?.Throw();
    }

    private void Rotate()
    {
        // Keep the preallocated current writer intact until its replacement
        // is fully allocated, protected, published, flushed, and reopened.
        // Rotation belongs to a prospective admission; its failure must not
        // strand terminal slots that were already accepted against this
        // handle.
        ReclaimClosedAllocations();
        EnsurePhysicalAllocationAvailable();
        var nextIndex = checked(_segmentIndex + 1);
        var (nextStream, nextPath, nextIdentity) = CreateSegment(nextIndex);
        var previous = _stream;
        var previousPath = _currentSegmentPath;
        _stream = nextStream;
        _currentSegmentPath = nextPath;
        _currentSegmentIdentity = nextIdentity;
        _committedSegmentBytes = 0;
        _segmentIndex = nextIndex;
        _currentPathFailureReported = false;
        CloseAndTrim(previous, previousPath, pathFailureAlreadyReported: false);
    }

    private AuditSpoolQuotaLease AcquireQuotaLock() =>
        AuditSpoolQuotaLease.AcquireExisting(_spoolRoot);

    internal IDisposable AcquireQuotaLockForTests() => AcquireQuotaLock();

    private void ReclaimClosedAllocations()
    {
        foreach (var segment in EnumerateSegments())
            TryTrimClosedSegment(segment);
    }

    private void EnsurePhysicalAllocationAvailable()
    {
        var segments = EnumerateSegments();
        var committed = PhysicalCommittedBytes(segments);
        if (committed <= AggregateCapacityBytes - SegmentCapacityBytes) return;
        if (_options.ProtectionMode == AuditProtectionMode.Anchored)
            throw new IOException("Anchored audit allocation capacity is exhausted.");

        while (committed > AggregateCapacityBytes - SegmentCapacityBytes)
        {
            var deleted = false;
            foreach (var segment in EnumerateDeletionFronts(excludedPath: null))
            {
                if (!TryDeleteClosedSegment(segment.Segment))
                    continue;

                deleted = true;
                break;
            }

            if (!deleted)
                throw new IOException("Audit allocation capacity is exhausted by live segments.");

            committed = PhysicalCommittedBytes(EnumerateSegments());
        }
    }

    private long PhysicalCommittedBytes(IEnumerable<FileInfo> segments)
    {
        long committed = 0;
        foreach (var segment in ClassifyRetainedSegments(segments))
        {
            // Only the live newest handle can still own the full keep-EOF
            // preallocation. Export-reader handles retain already-closed
            // prefix bytes and are charged at their aligned logical length.
            committed = checked(committed + (segment.Access == RetainedSegmentAccess.LiveNewest
                ? SegmentCapacityBytes
                : AlignAllocation(segment.Named.Segment.Length)));
        }
        return committed;
    }

    // Every caller holds the global spool quota. Compliant writers and export
    // readers therefore cannot acquire a new segment handle while this
    // topology is classified; a handle may only be released underneath us.
    private ClassifiedSegment[] ClassifyRetainedSegments(IEnumerable<FileInfo> segments)
    {
        var classified = new List<ClassifiedSegment>();
        foreach (var group in segments
                     .Select(ParseNamedSegment)
                     .GroupBy(item => item.BootId))
        {
            var ordered = group.OrderBy(item => item.Index).ToArray();
            var sawClosed = false;
            var checkpointOwnerConfirmed = false;
            int? priorIndex = null;
            for (var index = 0; index < ordered.Length; index++)
            {
                var item = ordered[index];
                if (priorIndex is int previousIndex && item.Index != previousIndex + 1)
                    throw new IOException("A retained audit segment sequence has an internal gap.");
                if (priorIndex is null &&
                    _options.ProtectionMode == AuditProtectionMode.Anchored &&
                    item.Index != 0)
                {
                    throw new IOException("An anchored audit segment prefix is missing.");
                }
                priorIndex = item.Index;

                if (!IsLockedSegment(item.Segment))
                {
                    sawClosed = true;
                    classified.Add(new ClassifiedSegment(item, RetainedSegmentAccess.Closed));
                    continue;
                }

                if (index == ordered.Length - 1)
                {
                    classified.Add(new ClassifiedSegment(item, RetainedSegmentAccess.LiveNewest));
                    continue;
                }

                if (sawClosed)
                    throw new IOException("A nonterminal audit segment is unexpectedly locked.");
                if (!checkpointOwnerConfirmed)
                {
                    RequireCheckpointOwnedPrefix(item.BootId);
                    checkpointOwnerConfirmed = true;
                }
                classified.Add(new ClassifiedSegment(
                    item,
                    RetainedSegmentAccess.ExporterRetainedPrefix));
            }
        }

        return classified.ToArray();
    }

    private static bool IsLockedSegment(FileInfo segment)
    {
        if (!TryOpenClosedSegment(segment, out var stream) || stream is null)
            return true;
        stream.Dispose();
        return false;
    }

    private void RequireCheckpointOwnedPrefix(Guid supervisorBootId)
    {
        if (_options.ProtectionMode != AuditProtectionMode.Anchored)
        {
            throw new IOException("A nonterminal audit segment is unexpectedly locked.");
        }

        if (!AuditExportCheckpointStore.TryAcquireExisting(
                _options,
                supervisorBootId,
                out var availableOwner))
        {
            return;
        }

        availableOwner?.Dispose();
        throw new IOException(
            "A nonterminal locked audit prefix has no active checkpoint owner.");
    }

    private static long AlignAllocation(long length)
    {
        const long block = 4096;
        return checked((length + block - 1) / block * block);
    }

    private (FileStream Stream, string Path, AuditSpoolSegmentIdentity Identity) CreateSegment(int index)
    {
        var identity = AuditSpoolSegmentIdentity.Create(_supervisorBootId, index);
        var fileName = identity.FileName;
        var path = Path.Combine(_spoolRoot, fileName);
        var temporaryPath = Path.Combine(
            _spoolRoot,
            $".{fileName}.{Guid.NewGuid():N}.allocating");
        FileStream? stream = null;
        try
        {
            var attempt = checked(++_allocationAttempt);
            if (_faultInjector?.Invoke(FileAuditSinkFaultPoint.PhysicalAllocation, attempt) == true)
                throw new IOException("Injected physical-allocation failure.");

            // Delete sharing permits the protected atomic publish while this
            // authoritative handle remains open; read/write remain denied, so
            // retention's exclusive-open probe sees it live. The shared secure
            // creator applies and verifies owner-only ACL/mode before publish.
            stream = SecureAuditStorage.CreateExclusiveFile(
                temporaryPath,
                SegmentCapacityBytes,
                FileShare.Delete);
            if (stream.Length != 0 || stream.Position != 0)
                throw new IOException("Physical audit allocation changed logical JSONL EOF.");
            SecureAuditStorage.PublishAtomically(temporaryPath, path, _spoolRoot);
            stream.Flush(flushToDisk: true);
            stream.Dispose();
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough);
            return (stream, path, identity);
        }
        catch
        {
            stream?.Dispose();
            SecureAuditStorage.TryDelete(temporaryPath);
            SecureAuditStorage.TryDelete(path);
            throw;
        }
    }

    private void CloseAndTrimCurrent() => CloseAndTrim(
        _stream,
        _currentSegmentPath,
        _currentPathFailureReported);

    private void CloseAndTrim(
        FileStream stream,
        string path,
        bool pathFailureAlreadyReported)
    {
        var logicalLength = stream.Length;
        try
        {
            stream.Flush(flushToDisk: true);
            if (!File.Exists(path))
            {
                if (pathFailureAlreadyReported) return;
                throw new IOException("The live audit segment disappeared before close.");
            }
            TrimOrCompact(stream, path, logicalLength);
            if (new FileInfo(path).Length != logicalLength)
                throw new IOException("Trimming audit allocation changed logical JSONL EOF.");
            if (!OperatingSystem.IsMacOS()) stream.Flush(flushToDisk: true);
        }
        finally
        {
            stream.Dispose();
        }
    }

    private void TrimOrCompact(FileStream stream, string path, long logicalLength)
    {
        if (!OperatingSystem.IsMacOS())
        {
            PhysicalAllocation.TrimBeyondEof(stream, logicalLength, SegmentCapacityBytes);
            return;
        }

        var allocation = SecureAuditStorage.GetMacFileAllocation(stream.SafeFileHandle);
        if (allocation.LogicalBytes != logicalLength)
            throw new IOException("The macOS audit segment changed before compaction.");
        var allocationUnit = Math.Max(512L, allocation.AllocationUnitBytes);
        var compactLogicalBytes = checked(
            (logicalLength + allocationUnit - 1) / allocationUnit * allocationUnit);
        if (allocation.AllocatedBytes <= compactLogicalBytes)
            return;

        CompactMacSegment(stream, path, logicalLength);
    }

    private void CompactMacSegment(FileStream source, string path, long logicalLength)
    {
        var fileName = Path.GetFileName(path);
        if (!AuditSpoolSegmentIdentity.TryParse(fileName, out _))
            throw new IOException("The macOS audit compaction source name is invalid.");
        var temporaryPath = Path.Combine(
            _spoolRoot,
            $".{fileName}.{Guid.NewGuid():N}{CompactingSuffix}");
        var attempt = checked(++_compactionAttempt);
        try
        {
            if (_macCloneFile(source.SafeFileHandle, temporaryPath) != 0)
            {
                SecureAuditStorage.TryDelete(temporaryPath);
                CopyExactMacSegment(source, temporaryPath, logicalLength, attempt);
            }
            else
            {
                SecureAuditStorage.ProtectExistingFile(temporaryPath);
                using var clone = new FileStream(
                    temporaryPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    FileOptions.WriteThrough);
                if (clone.Length != logicalLength)
                    throw new IOException("The cloned audit segment changed logical EOF.");
                ValidateExactMacClone(source, clone, logicalLength);
                clone.Flush(flushToDisk: true);
            }

            if (_faultInjector?.Invoke(
                    FileAuditSinkFaultPoint.MacCompactionTemporaryDurable,
                    attempt) == true)
            {
                throw new IOException("Injected pre-publish macOS compaction failure.");
            }

            SecureAuditStorage.ReplaceAtomically(
                temporaryPath,
                path,
                _spoolRoot,
                () =>
                {
                    if (_faultInjector?.Invoke(
                            FileAuditSinkFaultPoint.MacCompactionPublished,
                            attempt) == true)
                    {
                        throw new IOException("Injected post-publish macOS compaction failure.");
                    }
                },
                () =>
                {
                    if (_faultInjector?.Invoke(
                            FileAuditSinkFaultPoint.MacCompactionDirectoryFlushStarting,
                            attempt) == true)
                    {
                        throw new IOException("Injected macOS compaction directory-flush failure.");
                    }
                });
            if (new FileInfo(path).Length != logicalLength)
                throw new IOException("The compacted audit segment changed logical EOF.");
        }
        finally
        {
            SecureAuditStorage.TryDelete(temporaryPath);
        }
    }

    private void CopyExactMacSegment(
        FileStream source,
        string temporaryPath,
        long logicalLength,
        int attempt)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var destination = SecureAuditStorage.CreateExclusiveFile(temporaryPath);
            if (_faultInjector?.Invoke(FileAuditSinkFaultPoint.MacCompactionCopy, attempt) == true)
                throw new IOException("Injected macOS compaction copy failure.");
            long offset = 0;
            while (offset < logicalLength)
            {
                var requested = checked((int)Math.Min(buffer.Length, logicalLength - offset));
                var read = RandomAccess.Read(
                    source.SafeFileHandle,
                    buffer.AsSpan(0, requested),
                    offset);
                if (read == 0)
                    throw new IOException("The audit segment ended during macOS compaction.");
                destination.Write(buffer.AsSpan(0, read));
                offset += read;
            }
            if (source.Length != logicalLength || destination.Length != logicalLength)
                throw new IOException("The audit segment changed during macOS compaction.");
            destination.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void ValidateExactMacClone(
        FileStream source,
        FileStream clone,
        long logicalLength)
    {
        var sourceBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var cloneBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long offset = 0;
            while (offset < logicalLength)
            {
                var requested = checked((int)Math.Min(sourceBuffer.Length, logicalLength - offset));
                ReadExactlyAt(
                    source.SafeFileHandle,
                    sourceBuffer.AsSpan(0, requested),
                    offset);
                ReadExactlyAt(
                    clone.SafeFileHandle,
                    cloneBuffer.AsSpan(0, requested),
                    offset);
                if (!sourceBuffer.AsSpan(0, requested).SequenceEqual(
                        cloneBuffer.AsSpan(0, requested)))
                {
                    throw new IOException("The cloned audit segment does not match the writer's bytes.");
                }

                offset += requested;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(cloneBuffer, clearArray: true);
        }
    }

    private static void ReadExactlyAt(
        SafeFileHandle file,
        Span<byte> destination,
        long offset)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(file, destination[total..], offset + total);
            if (read == 0)
                throw new IOException("The audit segment ended during exact comparison.");
            total += read;
        }
    }

    private void SweepClosedSegments(DateTimeOffset now, long requiredReservedBytes)
    {
        if (_options.ProtectionMode == AuditProtectionMode.Anchored)
            return;

        var candidates = EnumerateSegments()
            .Where(segment => !PathsEqual(segment.FullName, _currentSegmentPath))
            .OrderBy(segment => segment.LastWriteTimeUtc)
            .ThenBy(segment => segment.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var segment in candidates)
            TryTrimClosedSegment(segment);

        DeleteEligiblePrefixSegments(segment =>
            now - new DateTimeOffset(segment.LastWriteTimeUtc, TimeSpan.Zero) >= _options.RetentionAge);

        if (TotalBytesUnderQuotaLock() <= AggregateCapacityBytes - requiredReservedBytes)
            return;

        while (TotalBytesUnderQuotaLock() > AggregateCapacityBytes - requiredReservedBytes)
        {
            var deleted = false;
            foreach (var segment in EnumerateDeletionFronts(_currentSegmentPath))
            {
                if (!TryDeleteClosedSegment(segment.Segment))
                    continue;

                deleted = true;
                break;
            }

            if (!deleted)
                return;
        }
    }

    private void DeleteEligiblePrefixSegments(Func<FileInfo, bool> eligible)
    {
        while (true)
        {
            var deleted = false;
            foreach (var segment in EnumerateDeletionFronts(_currentSegmentPath))
            {
                if (!eligible(segment.Segment) || !TryDeleteClosedSegment(segment.Segment))
                    continue;

                deleted = true;
            }

            if (!deleted)
                return;
        }
    }

    private bool TryDeleteClosedSegment(FileInfo segment)
    {
        RefuseLinkOrReparsePoint(segment);
        FileStream? exclusive = null;
        try
        {
            exclusive = new FileStream(
                segment.FullName,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException)
        {
            // Another supervisor still holds this live segment. Retention may
            // never treat a lock failure as permission to unlink it.
            return false;
        }

        using (exclusive)
        {
            RefuseLinkOrReparsePoint(new FileInfo(segment.FullName));
        }

        File.Delete(segment.FullName);
        return true;
    }

    private void TryTrimClosedSegment(FileInfo segment)
    {
        RefuseLinkOrReparsePoint(segment);
        var lastWriteUtc = segment.LastWriteTimeUtc;
        FileStream? exclusive = null;
        try
        {
            exclusive = new FileStream(
                segment.FullName,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
        }
        catch (IOException)
        {
            // A live supervisor still owns it.
            return;
        }

        using (exclusive)
        {
            RefuseLinkOrReparsePoint(new FileInfo(segment.FullName));
            var logicalLength = exclusive.Length;
            TrimOrCompact(exclusive, segment.FullName, logicalLength);
            if (new FileInfo(segment.FullName).Length != logicalLength)
                throw new IOException("Reclaiming a closed audit allocation changed logical JSONL EOF.");
            if (!OperatingSystem.IsMacOS()) exclusive.Flush(flushToDisk: true);
        }
        File.SetLastWriteTimeUtc(segment.FullName, lastWriteUtc);
        segment.Refresh();
    }

    private FileInfo[] EnumerateSegments()
    {
        SecureAuditStorage.VerifyProtectedDirectory(_spoolRoot);
        var directory = new DirectoryInfo(_spoolRoot);
        var segments = new List<FileInfo>();
        foreach (var entry in directory
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly)
                     .ToArray())
        {
            if (entry is FileInfo lockFile &&
                string.Equals(
                    lockFile.Name,
                    AuditSpoolQuotaLease.ControlFileName,
                    StringComparison.Ordinal))
            {
                continue;
            }
            if (entry is not FileInfo segment ||
                !AuditSpoolSegmentIdentity.TryParse(entry.Name, out _))
                throw new IOException("The audit spool contains an unknown entry.");

            segments.Add(segment);
        }

        foreach (var segment in segments)
        {
            RefuseLinkOrReparsePoint(segment);
            SecureAuditStorage.VerifyProtectedFile(segment.FullName);
        }
        return segments.ToArray();
    }

    private void RecoverCrashAllocationsAndValidateSpool()
    {
        SecureAuditStorage.VerifyProtectedDirectory(_spoolRoot);
        var directory = new DirectoryInfo(_spoolRoot);
        foreach (var entry in directory
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly)
                     .ToArray())
        {
            if (entry is not FileInfo file)
                throw new IOException("The audit spool contains an unknown entry.");
            if (string.Equals(
                    file.Name,
                    AuditSpoolQuotaLease.ControlFileName,
                    StringComparison.Ordinal))
                continue;
            if (AuditSpoolSegmentIdentity.TryParse(file.Name, out _))
                continue;
            if (TryParseCanonicalCompactionName(file.Name, out var sourceName))
            {
                RecoverCrashCompaction(file, sourceName);
                continue;
            }
            if (!IsCanonicalAllocationName(file.Name))
            {
                throw new IOException("The audit spool contains an unknown entry.");
            }

            RefuseLinkOrReparsePoint(file);
            SecureAuditStorage.VerifyProtectedFile(file.FullName);
            using (var exclusive = new FileStream(
                       file.FullName,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       bufferSize: 1,
                       FileOptions.WriteThrough))
            {
                RefuseLinkOrReparsePoint(new FileInfo(file.FullName));
                if (exclusive.Length != 0)
                    throw new IOException("A crash-left audit allocation has an invalid logical length.");
            }

            File.Delete(file.FullName);
            if (File.Exists(file.FullName))
                throw new IOException("A crash-left audit allocation could not be removed.");
        }
    }

    private void RecoverCrashCompaction(FileInfo temporary, string sourceName)
    {
        RefuseLinkOrReparsePoint(temporary);
        SecureAuditStorage.VerifyProtectedFile(temporary.FullName);
        var sourcePath = Path.Combine(_spoolRoot, sourceName);
        if (!File.Exists(sourcePath))
            throw new IOException("A crash-left audit compaction lost its source segment.");
        SecureAuditStorage.VerifyProtectedFile(sourcePath);
        using (var exclusive = new FileStream(
                   temporary.FullName,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None,
                   bufferSize: 1,
                   FileOptions.WriteThrough))
        {
            RefuseLinkOrReparsePoint(new FileInfo(temporary.FullName));
        }
        File.Delete(temporary.FullName);
        if (File.Exists(temporary.FullName))
            throw new IOException("A crash-left audit compaction could not be removed.");
    }

    private long TotalBytesUnderQuotaLock() =>
        EnumerateSegments().Sum(segment => segment.Length);

    private NamedSegment[] EnumerateDeletionFronts(string? excludedPath)
    {
        return EnumerateSegments()
            .Select(ParseNamedSegment)
            .Where(segment => excludedPath is null ||
                              !PathsEqual(segment.Segment.FullName, excludedPath))
            .GroupBy(segment => segment.BootId)
            .Select(group => group.OrderBy(segment => segment.Index).First())
            .OrderBy(segment => segment.Segment.LastWriteTimeUtc)
            .ThenBy(segment => segment.Segment.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static NamedSegment ParseNamedSegment(FileInfo segment)
    {
        if (!AuditSpoolSegmentIdentity.TryParse(segment.Name, out var identity))
        {
            throw new IOException("An audit segment name is invalid.");
        }

        return new NamedSegment(segment, identity.SupervisorBootId, identity.Index);
    }

    private readonly record struct NamedSegment(FileInfo Segment, Guid BootId, int Index);

    private enum RetainedSegmentAccess
    {
        Closed,
        ExporterRetainedPrefix,
        LiveNewest,
    }

    private readonly record struct ClassifiedSegment(
        NamedSegment Named,
        RetainedSegmentAccess Access);

    private void ValidateRetainedSegments()
    {
        var named = ClassifyRetainedSegments(EnumerateSegments())
            .GroupBy(item => item.Named.BootId);

        foreach (var group in named)
        {
            var ordered = group.OrderBy(item => item.Named.Index).ToArray();
            long? priorSequence = null;
            string? priorHash = null;
            var hasOpaquePrefix = false;
            foreach (var classified in ordered)
            {
                var item = classified.Named;
                if (classified.Access == RetainedSegmentAccess.ExporterRetainedPrefix)
                {
                    // The export reader retains these exact closed handles
                    // FileShare.None. Ownership and topology authorize only
                    // leaving the bytes untouched; this writer does not parse,
                    // trim, or infer deletion from the inaccessible prefix.
                    hasOpaquePrefix = true;
                    continue;
                }
                if (classified.Access == RetainedSegmentAccess.LiveNewest)
                    break;

                if (!TryOpenClosedSegment(item.Segment, out var stream) || stream is null)
                {
                    throw new IOException(
                        "A retained audit segment lock changed during validation.");
                }

                using (stream)
                {
                    if (stream.Length == 0)
                    {
                        if (item.Index != ordered[^1].Named.Index)
                            throw new IOException("An intermediate audit segment is empty.");
                        continue;
                    }
                    if (stream.Length > SegmentCapacityBytes)
                        throw new IOException("A retained audit segment has an oversized tail.");
                    stream.Position = stream.Length - 1;
                    if (stream.ReadByte() != (byte)'\n')
                        throw new IOException("A retained audit segment has a torn tail.");
                    stream.Position = 0;

                    // Segment capacity is operator-configurable up to 4 GiB;
                    // validate as bounded records instead of allocating the
                    // whole retained segment. Individual records are capped at
                    // MaxRecordBytes by the serializer contract.
                    var recordBuffer = new byte[_options.MaxRecordBytes];
                    var recordLength = 0;
                    while (stream.Position < stream.Length)
                    {
                        var value = stream.ReadByte();
                        if (value < 0)
                            throw new IOException("A retained audit segment ended unexpectedly.");
                        if (value != (byte)'\n')
                        {
                            if (recordLength >= recordBuffer.Length)
                                throw new IOException("A retained audit record exceeds its configured bound.");
                            recordBuffer[recordLength++] = (byte)value;
                            continue;
                        }

                        var record = AuditSpoolRecordCodec.Parse(
                            recordBuffer.AsSpan(0, recordLength),
                            item.BootId);
                        if (priorSequence is long sequence)
                        {
                            if (record.Sequence != sequence + 1 ||
                                !string.Equals(
                                    record.PreviousEventHash,
                                    priorHash,
                                    StringComparison.Ordinal))
                            {
                                throw new IOException("A retained audit hash chain is discontinuous.");
                            }
                        }
                        else if (!hasOpaquePrefix &&
                                 _options.ProtectionMode == AuditProtectionMode.Anchored &&
                                 (record.Sequence != 1 || record.PreviousEventHash is not null))
                        {
                            throw new IOException("An anchored audit hash chain does not begin at sequence one.");
                        }

                        priorSequence = record.Sequence;
                        priorHash = record.EventHash;
                        recordLength = 0;
                    }
                    if (recordLength != 0)
                        throw new IOException("A retained audit segment has a torn tail.");
                }
            }
        }
    }

    private static bool TryOpenClosedSegment(FileInfo segment, out FileStream? stream)
    {
        try
        {
            stream = new FileStream(
                segment.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            return true;
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            stream = null;
            return false;
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

    private void ValidateCurrentSegmentPath(bool requireLengthMatch = true)
    {
        try
        {
            SecureAuditStorage.VerifyProtectedDirectory(_spoolRoot);
            SecureAuditStorage.VerifyProtectedFile(_currentSegmentPath);
            var named = new FileInfo(_currentSegmentPath);
            named.Refresh();
            if (!named.Exists || (requireLengthMatch && named.Length != _stream.Length))
                throw new IOException("The live audit segment path no longer identifies the writer's file.");
        }
        catch
        {
            _currentPathFailureReported = true;
            throw;
        }
    }

    private static void RefuseLinkOrReparsePoint(FileInfo file)
    {
        file.Refresh();
        if (file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("An audit segment cannot be a link or reparse point.");
    }

    private DateTimeOffset GetUtcNow()
    {
        var now = _utcNow();
        if (now.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("The audit clock must return UTC timestamps.");
        return now;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsCanonicalAllocationName(string fileName)
    {
        var allocationSeparator = 1 + AuditSpoolSegmentIdentity.FileNameLength;
        var expectedLength = allocationSeparator + 1 + AllocationIdLength + AllocatingSuffix.Length;
        if (fileName.Length != expectedLength || fileName[0] != '.' ||
            fileName[allocationSeparator] != '.' ||
            !fileName.EndsWith(AllocatingSuffix, StringComparison.Ordinal) ||
            !AuditSpoolSegmentIdentity.TryParse(
                fileName.Substring(1, AuditSpoolSegmentIdentity.FileNameLength),
                out _))
        {
            return false;
        }

        return AuditSpoolSegmentIdentity.TryParseCanonicalUuidV4(
            fileName.AsSpan(allocationSeparator + 1, AllocationIdLength),
            out _);
    }

    private static bool TryParseCanonicalCompactionName(
        string fileName,
        out string sourceName)
    {
        sourceName = string.Empty;
        var idSeparator = 1 + AuditSpoolSegmentIdentity.FileNameLength;
        var expectedLength = idSeparator + 1 + AllocationIdLength + CompactingSuffix.Length;
        if (fileName.Length != expectedLength || fileName[0] != '.' ||
            fileName[idSeparator] != '.' ||
            !fileName.EndsWith(CompactingSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        sourceName = fileName.Substring(1, AuditSpoolSegmentIdentity.FileNameLength);
        return AuditSpoolSegmentIdentity.TryParse(sourceName, out _) &&
               AuditSpoolSegmentIdentity.TryParseCanonicalUuidV4(
                   fileName.AsSpan(idSeparator + 1, AllocationIdLength),
                   out _);
    }

    private static class MacNative
    {
        private const int AtCurrentWorkingDirectory = -2;

        internal static int CloneFile(
            SafeFileHandle source,
            string destination)
        {
            try
            {
                return fclonefileat(
                    source,
                    AtCurrentWorkingDirectory,
                    destination,
                    flags: 0);
            }
            catch (EntryPointNotFoundException)
            {
                return -1;
            }
        }

        [DllImport("libc", EntryPoint = "fclonefileat", SetLastError = true)]
        private static extern int fclonefileat(
            SafeFileHandle source,
            int destinationDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string destination,
            int flags);
    }

    private static class PhysicalAllocation
    {
        private const int LinuxKeepSize = 0x01;
        private const int LinuxPunchHole = 0x02;
        private const int WindowsFileAllocationInfo = 5;
        private const long AllocationBlock = 4096;

        internal static void TrimBeyondEof(FileStream stream, long logicalLength, long allocatedBytes)
        {
            if (logicalLength < 0 || logicalLength > allocatedBytes)
                throw new ArgumentOutOfRangeException(nameof(logicalLength));

            if (OperatingSystem.IsLinux())
            {
                var trimStart = checked((logicalLength + AllocationBlock - 1) / AllocationBlock * AllocationBlock);
                if (trimStart < allocatedBytes &&
                    fallocate(
                        Descriptor(stream.SafeFileHandle),
                        LinuxKeepSize | LinuxPunchHole,
                        trimStart,
                        allocatedBytes - trimStart) != 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                var allocation = new WindowsAllocationInfo { AllocationSize = logicalLength };
                if (!SetFileInformationByHandle(
                        stream.SafeFileHandle,
                        WindowsFileAllocationInfo,
                        ref allocation,
                        Marshal.SizeOf<WindowsAllocationInfo>()))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                return;
            }

            throw new PlatformNotSupportedException();
        }

        private static int Descriptor(SafeFileHandle handle) => handle.DangerousGetHandle().ToInt32();

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowsAllocationInfo
        {
            internal long AllocationSize;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int fallocate(int fileDescriptor, int mode, long offset, long length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle fileHandle,
            int fileInformationClass,
            ref WindowsAllocationInfo fileInformation,
            int bufferSize);
    }
}
