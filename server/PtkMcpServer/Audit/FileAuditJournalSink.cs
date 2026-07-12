using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace PtkMcpServer.Audit;

internal enum FileAuditSinkFaultPoint
{
    PhysicalAllocation,
    BeforePhysicalFlush,
    AfterPhysicalFlush,
}

internal enum AuditCommittedSpoolReadStatus
{
    Data,
    AtCommittedTail,
    NotCurrent,
}

internal readonly record struct AuditCommittedSpoolRead(
    AuditCommittedSpoolReadStatus Status,
    ReadOnlyMemory<byte> Bytes,
    long CommittedTail);

internal interface IAuditCommittedSpoolSource
{
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
    private const string QuotaLockFileName = ".ptk-audit-quota.lock";
    private const string AllocatingSuffix = ".allocating";
    private const int AllocationIdLength = 32;
    private static readonly byte[] EventHashMarker = Encoding.ASCII.GetBytes(",\"event_hash\":\"");
    private readonly AuditOptions _options;
    private readonly Guid _supervisorBootId;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<FileAuditSinkFaultPoint, int, bool>? _faultInjector;
    private readonly string _spoolRoot;
    private readonly string _quotaLockPath;
    private FileStream _stream;
    private string _currentSegmentPath;
    private AuditSpoolSegmentIdentity _currentSegmentIdentity;
    private long _committedSegmentBytes;
    private int _segmentIndex;
    private int _allocationAttempt;
    private int _flushAttempt;
    private bool _disposed;

    internal FileAuditJournalSink(
        AuditOptions options,
        Guid supervisorBootId,
        Func<DateTimeOffset>? utcNow = null,
        Func<FileAuditSinkFaultPoint, int, bool>? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        AuditSpoolSegmentIdentity.RequireUuidV4(supervisorBootId, nameof(supervisorBootId));
        _options = options;
        _supervisorBootId = supervisorBootId;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _faultInjector = faultInjector;
        _spoolRoot = SecureAuditStorage.PrepareRoot(options.SpoolDirectory);
        _quotaLockPath = Path.Combine(_spoolRoot, QuotaLockFileName);
        EnsureQuotaLockFile();
        using (AcquireQuotaLock())
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
        if (_disposed || identity != _currentSegmentIdentity)
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

        try
        {
            using (AcquireQuotaLock())
                CloseAndTrimCurrent();
        }
        finally
        {
            _disposed = true;
        }
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
        _stream = nextStream;
        _currentSegmentPath = nextPath;
        _currentSegmentIdentity = nextIdentity;
        _committedSegmentBytes = 0;
        _segmentIndex = nextIndex;
        CloseAndTrim(previous);
    }

    private void EnsureQuotaLockFile()
    {
        if (File.Exists(_quotaLockPath))
        {
            SecureAuditStorage.VerifyProtectedFile(_quotaLockPath);
            return;
        }

        try
        {
            using var stream = SecureAuditStorage.CreateExclusiveFile(_quotaLockPath);
            stream.WriteByte(0x50);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(_quotaLockPath))
        {
            SecureAuditStorage.VerifyProtectedFile(_quotaLockPath);
        }
    }

    private IDisposable AcquireQuotaLock()
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                return new FileLockLease(new FileStream(
                    _quotaLockPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough));
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

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
        foreach (var segment in segments)
        {
            committed = checked(committed + (IsLockedLiveSegment(segment)
                ? SegmentCapacityBytes
                : AlignAllocation(segment.Length)));
        }
        return committed;
    }

    private static bool IsLockedLiveSegment(FileInfo segment)
    {
        try
        {
            using var stream = new FileStream(
                segment.FullName,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static long AlignAllocation(long length)
    {
        const long block = 4096;
        return checked((length + block - 1) / block * block);
    }

    private sealed class FileLockLease(FileStream stream) : IDisposable
    {
        private FileStream? _stream = stream;

        public void Dispose()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
        }
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

    private void CloseAndTrimCurrent() => CloseAndTrim(_stream);

    private void CloseAndTrim(FileStream stream)
    {
        var logicalLength = stream.Length;
        try
        {
            stream.Flush(flushToDisk: true);
            PhysicalAllocation.TrimBeyondEof(stream, logicalLength, SegmentCapacityBytes);
            if (stream.Length != logicalLength)
                throw new IOException("Trimming audit allocation changed logical JSONL EOF.");
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            stream.Dispose();
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
            PhysicalAllocation.TrimBeyondEof(exclusive, logicalLength, SegmentCapacityBytes);
            if (exclusive.Length != logicalLength)
                throw new IOException("Reclaiming a closed audit allocation changed logical JSONL EOF.");
            exclusive.Flush(flushToDisk: true);
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
                string.Equals(lockFile.Name, QuotaLockFileName, StringComparison.Ordinal))
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
            if (string.Equals(file.Name, QuotaLockFileName, StringComparison.Ordinal))
                continue;
            if (AuditSpoolSegmentIdentity.TryParse(file.Name, out _))
                continue;
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

    private void ValidateRetainedSegments()
    {
        var named = EnumerateSegments()
            .Select(ParseNamedSegment)
            .GroupBy(item => item.BootId);

        foreach (var group in named)
        {
            long? priorSequence = null;
            string? priorHash = null;
            int? priorIndex = null;
            foreach (var item in group.OrderBy(item => item.Index))
            {
                if (priorIndex is int previousIndex && item.Index != previousIndex + 1)
                    throw new IOException("A retained audit segment sequence has an internal gap.");
                if (priorIndex is null && _options.ProtectionMode == AuditProtectionMode.Anchored && item.Index != 0)
                    throw new IOException("An anchored audit segment prefix is missing.");
                priorIndex = item.Index;

                if (!TryOpenClosedSegment(item.Segment, out var stream) || stream is null)
                {
                    // A live segment must be the writer's newest segment for
                    // that boot. Older locked data beside a newer segment is
                    // not a valid rotation state.
                    if (item.Index != group.Max(candidate => candidate.Index))
                        throw new IOException("A nonterminal audit segment is unexpectedly locked.");
                    break;
                }

                using (stream)
                {
                    if (stream.Length == 0) continue;
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

                        var record = ValidateRetainedRecord(
                            recordBuffer.AsSpan(0, recordLength),
                            item.BootId);
                        if (priorSequence is long sequence)
                        {
                            if (record.Sequence != sequence + 1 ||
                                !string.Equals(record.PreviousHash, priorHash, StringComparison.Ordinal))
                            {
                                throw new IOException("A retained audit hash chain is discontinuous.");
                            }
                        }
                        else if (_options.ProtectionMode == AuditProtectionMode.Anchored &&
                                 (record.Sequence != 1 || record.PreviousHash is not null))
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
        catch (IOException)
        {
            stream = null;
            return false;
        }
    }

    private static RetainedRecord ValidateRetainedRecord(ReadOnlySpan<byte> line, Guid expectedBootId)
    {
        if (line.Length < EventHashMarker.Length + 66)
            throw new IOException("A retained audit record is truncated.");
        var markerOffset = line.Length - EventHashMarker.Length - 66;
        if (!line.Slice(markerOffset, EventHashMarker.Length).SequenceEqual(EventHashMarker) ||
            line[^2] != (byte)'"' || line[^1] != (byte)'}')
        {
            throw new IOException("A retained audit record does not end with event_hash.");
        }

        var hashText = Encoding.ASCII.GetString(line.Slice(markerOffset + EventHashMarker.Length, 64));
        if (hashText.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new IOException("A retained audit event hash is invalid.");
        }

        var preHash = new byte[markerOffset + 1];
        line[..markerOffset].CopyTo(preHash);
        preHash[^1] = (byte)'}';
        var computed = Convert.ToHexString(SHA256.HashData(preHash)).ToLowerInvariant();
        if (!string.Equals(computed, hashText, StringComparison.Ordinal))
            throw new IOException("A retained audit event hash does not match its bytes.");

        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var root = document.RootElement;
            if (root.GetProperty("schema_version").GetString() != "ptk.audit/1" ||
                root.GetProperty("event_hash").GetString() != hashText ||
                !Guid.TryParseExact(
                    root.GetProperty("producer").GetProperty("supervisor_boot_id").GetString(),
                    "D",
                    out var bootId) ||
                bootId != expectedBootId)
            {
                throw new IOException("A retained audit record has invalid identity metadata.");
            }

            var sequence = root.GetProperty("sequence").GetInt64();
            if (sequence < 1)
                throw new IOException("A retained audit sequence is invalid.");
            var previousElement = root.GetProperty("previous_event_hash");
            var previousHash = previousElement.ValueKind == JsonValueKind.Null
                ? null
                : previousElement.GetString();
            if (sequence == 1 ? previousHash is not null : previousHash?.Length != 64)
                throw new IOException("A retained audit previous hash is invalid.");
            return new RetainedRecord(sequence, previousHash, hashText);
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new IOException("A retained audit record is invalid.");
        }
    }

    private sealed record RetainedRecord(long Sequence, string? PreviousHash, string EventHash);

    private void ValidateCurrentSegmentPath(bool requireLengthMatch = true)
    {
        SecureAuditStorage.VerifyProtectedDirectory(_spoolRoot);
        SecureAuditStorage.VerifyProtectedFile(_currentSegmentPath);
        var named = new FileInfo(_currentSegmentPath);
        named.Refresh();
        if (!named.Exists || (requireLengthMatch && named.Length != _stream.Length))
            throw new IOException("The live audit segment path no longer identifies the writer's file.");
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

            if (OperatingSystem.IsMacOS())
            {
                // Preallocation on macOS reserves blocks beyond logical EOF.
                // Truncating to the already-current EOF is a no-op and leaves
                // those blocks charged. First materialize the reserved range as
                // the logical extent, then truncate it back to the exact JSONL
                // length so APFS/HFS+ releases every block beyond EOF.
                var descriptor = Descriptor(stream.SafeFileHandle);
                if (ftruncate(descriptor, allocatedBytes) != 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (ftruncate(descriptor, logicalLength) != 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
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

        [DllImport("libc", SetLastError = true)]
        private static extern int ftruncate(int fileDescriptor, long length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle fileHandle,
            int fileInformationClass,
            ref WindowsAllocationInfo fileInformation,
            int bufferSize);
    }
}
