using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PtkMcpServer.Audit;

namespace PtkMcpServer;

internal enum OutputArtifactState
{
    Available,
    Incomplete,
    Expired,
    Evicted,
    NotFound,
    InvalidOffset,
    InsufficientBound,
}

internal static class OutputArtifactStateCodes
{
    internal static string ToMachineCode(this OutputArtifactState state) => state switch
    {
        OutputArtifactState.Available => "available",
        OutputArtifactState.Incomplete => "incomplete",
        OutputArtifactState.Expired => "expired",
        OutputArtifactState.Evicted => "evicted",
        OutputArtifactState.NotFound => "not_found",
        OutputArtifactState.InvalidOffset => "invalid_offset",
        OutputArtifactState.InsufficientBound => "insufficient_bound",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}

/// <summary>The separately retained streams and terminal facts from one
/// invocation. The store renders these values into one stable, labeled UTF-8
/// recovery view while retaining segment offsets in the supervisor table.</summary>
internal sealed record OutputArtifactContent(
    string StandardOutput,
    string[] StandardError,
    string[] Errors,
    string[] Warnings,
    int? ExitCode,
    OutputProvenance Provenance,
    bool Complete = true,
    string? IncompleteReason = null);

internal sealed record OutputStoreOptions(
    string RootDirectory,
    TimeSpan TimeToLive,
    TimeSpan RetentionInterval,
    long MaximumArtifactBytes,
    long MaximumSessionBytes,
    long MaximumAggregateBytes,
    Func<DateTimeOffset>? UtcNow = null,
    Action<string>? ArtifactDeleteStartingForTests = null,
    int MaximumRetainedArtifacts = 4096,
    Action<string>? ArtifactCreateStartingForTests = null,
    Action? ReservationStartingForTests = null,
    Action<string>? ArtifactWriteStartingForTests = null,
    Action<string>? ArtifactUnlinkIdentityVerifiedForTests = null,
    Action? ArtifactPublishingClaimedForTests = null,
    Action? RetainedReadStartingForTests = null)
{
    internal static OutputStoreOptions Production()
    {
        var configuredParent = Environment.GetEnvironmentVariable("PTK_OUTPUT_ROOT");
        var parent = string.IsNullOrWhiteSpace(configuredParent)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ptk",
                "output")
            : Path.GetFullPath(configuredParent);
        var root = Path.Combine(
            parent,
            $"server-{Environment.ProcessId}-{Guid.NewGuid():N}");
        return new OutputStoreOptions(
            root,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(1),
            MaximumArtifactBytes: 8 * 1024 * 1024,
            MaximumSessionBytes: 32 * 1024 * 1024,
            MaximumAggregateBytes: 64 * 1024 * 1024);
    }
}

internal sealed record OutputSealResult(
    bool Success,
    string? Handle,
    OutputArtifactState State,
    long Bytes,
    string? DetailCode);

internal sealed record OutputArtifactStatus(
    OutputArtifactState State,
    long Bytes,
    bool Complete,
    OutputProvenance? Provenance,
    DateTimeOffset? ExpiresUtc,
    string? DetailCode);

internal sealed record OutputReadResult(
    OutputArtifactState State,
    string Text,
    long Offset,
    long NextOffset,
    long TotalBytes,
    int BytesRead,
    bool Complete,
    OutputProvenance? Provenance,
    string? DetailCode);

internal sealed record OutputSearchMatch(long Offset, string Preview);

internal sealed record OutputSearchResult(
    OutputArtifactState State,
    OutputSearchMatch[] Matches,
    long Offset,
    long NextOffset,
    long TotalBytes,
    int BytesScanned,
    bool Complete,
    OutputProvenance? Provenance,
    string? DetailCode);

/// <summary>A one-shot supervisor-issued write capability. The opaque public
/// handle is returned only by a successful seal, never by the reservation.</summary>
internal sealed class OutputCaptureReservation : IDisposable
{
    private const int Active = 0;
    private const int Sealing = 1;
    private const int Canceled = 2;
    private const int Publishing = 3;
    private const int Published = 4;
    private const int Closed = 5;

    private OutputStore? _owner;
    private readonly Guid _artifactId;
    private int _state = Active;

    internal OutputCaptureReservation(OutputStore owner, Guid artifactId)
    {
        _owner = owner;
        _artifactId = artifactId;
    }

    /// <summary>Internal artifact identity suitable for a future worker frame.
    /// It is not the public handle and grants no read capability.</summary>
    internal string ArtifactId => _artifactId.ToString("N", CultureInfo.InvariantCulture);

    internal OutputSealResult Seal(OutputArtifactContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var owner = Volatile.Read(ref _owner);
        return owner is null ||
               Interlocked.CompareExchange(ref _state, Sealing, Active) != Active
            ? new OutputSealResult(false, null, OutputArtifactState.NotFound, 0, "reservation_closed")
            : owner.Seal(this, _artifactId, content);
    }

    internal OutputSealResult SealIncomplete(OutputArtifactContent prefix, string reason)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(reason);
        return Seal(prefix with
        {
            Complete = false,
            IncompleteReason = reason,
        });
    }

    /// <summary>Wins the cancel-versus-publish race. Once publication has
    /// started, the coordinator must observe and return that exact result.</summary>
    internal bool TryCancel()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state is Canceled or Closed) return true;
            if (state == Publishing) return false;
            if (state == Published)
            {
                Interlocked.Exchange(ref _owner, null);
                return false;
            }
            if (Interlocked.CompareExchange(ref _state, Canceled, state) != state)
                continue;

            Interlocked.Exchange(ref _owner, null)?.CancelSeal(_artifactId);
            return true;
        }
    }

    internal bool TryBeginPublishing() =>
        Interlocked.CompareExchange(ref _state, Publishing, Sealing) == Sealing;

    internal void MarkPublished() => Volatile.Write(ref _state, Published);

    internal void CompleteObserved()
    {
        Interlocked.Exchange(ref _owner, null);
        if (Volatile.Read(ref _state) != Published)
            Volatile.Write(ref _state, Closed);
    }

    public void Dispose() => _ = TryCancel();
}

/// <summary>Request-owned supervisor coordinator for one foreground capture.
/// Reservation stays lazy until the host has selected a capturable dispatch,
/// then the worker-facing path receives only the one-shot write capability.</summary>
internal sealed class ForegroundOutputCapture : IDisposable
{
    private sealed class ReservationAttempt
    {
        private const int Pending = 0;
        private const int Canceled = 1;
        private const int Published = 2;
        private int _state = Pending;

        internal bool TryCancel() =>
            Interlocked.CompareExchange(ref _state, Canceled, Pending) == Pending;

        internal bool TryPublish() =>
            Interlocked.CompareExchange(ref _state, Published, Pending) == Pending;
    }

    private sealed record ReservationWorkResult(
        OutputCaptureReservation? Reservation,
        string? Failure);

    private OutputStore? _store;
    private OutputCaptureReservation? _reservation;
    private readonly Action? _sealCancellationRejectedForTests;
    private readonly Func<TimeSpan, Task>? _sealDelayForTests;
    private string? _failure;
    private bool _prepared;

    internal ForegroundOutputCapture(
        OutputStore store,
        Action? sealCancellationRejectedForTests = null,
        Func<TimeSpan, Task>? sealDelayForTests = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sealCancellationRejectedForTests = sealCancellationRejectedForTests;
        _sealDelayForTests = sealDelayForTests;
    }

    internal long MaximumArtifactBytes =>
        _store?.MaximumArtifactBytes ?? OutputStore.DefaultReadBytes;

    internal async Task PrepareAsync(
        TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        if (_prepared) return;
        _prepared = true;
        if (maximumWait <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumWait));
        var store = _store;
        if (store is null)
        {
            _failure = "output_store_unavailable";
            return;
        }

        var attempt = new ReservationAttempt();
        if (!store.TryStartForegroundOperation(
                () =>
                {
                    try
                    {
                        if (!store.TryReserve(
                                "default",
                                out var reservation,
                                out var failure))
                        {
                            return new ReservationWorkResult(
                                null,
                                failure is null
                                    ? "output_store_unavailable"
                                    : $"output_store_{failure}");
                        }

                        if (attempt.TryPublish())
                            return new ReservationWorkResult(reservation, null);

                        reservation!.Dispose();
                        return new ReservationWorkResult(
                            null,
                            "output_store_prepare_timed_out");
                    }
                    catch
                    {
                        return new ReservationWorkResult(
                            null,
                            "output_store_unavailable");
                    }
                },
                out var prepareTask))
        {
            _failure = "output_store_busy";
            return;
        }

        var delayTask = cancellationToken.CanBeCanceled
            ? Task.Delay(maximumWait, cancellationToken)
            : Task.Delay(maximumWait);
        if (await Task.WhenAny(prepareTask!, delayTask).ConfigureAwait(false) != prepareTask)
        {
            if (attempt.TryCancel())
            {
                _failure = cancellationToken.IsCancellationRequested
                    ? "output_store_prepare_canceled"
                    : "output_store_prepare_timed_out";
                ObserveFault(prepareTask!);
                return;
            }
        }

        var result = await prepareTask!.ConfigureAwait(false);
        _reservation = result.Reservation;
        _failure = result.Failure;
    }

    internal Task<OutputRecoverySummary> SealAsync(
        OutputArtifactContent content,
        TimeSpan maximumWait) =>
        SealCoreAsync(content, incompleteReason: null, maximumWait);

    internal Task<OutputRecoverySummary> SealIncompleteAsync(
        OutputArtifactContent content,
        string reason,
        TimeSpan maximumWait) =>
        SealCoreAsync(
            content,
            reason ?? throw new ArgumentNullException(nameof(reason)),
            maximumWait);

    private async Task<OutputRecoverySummary> SealCoreAsync(
        OutputArtifactContent content,
        string? incompleteReason,
        TimeSpan maximumWait)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maximumWait <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumWait));
        var reservation = Interlocked.Exchange(ref _reservation, null);
        if (reservation is null)
        {
            return OutputRecoverySummary.Unavailable(
                _failure ?? (_prepared ? "output_store_unavailable" : "capture_not_prepared"),
                advertise: true);
        }

        var store = _store;
        if (store is null ||
            !store.TryStartForegroundOperation(
                () => incompleteReason is null
                    ? reservation.Seal(content)
                    : reservation.SealIncomplete(content, incompleteReason),
                out var sealTask))
        {
            _ = reservation.TryCancel();
            return OutputRecoverySummary.Unavailable(
                "output_store_busy",
                advertise: true);
        }
        var delayTask = _sealDelayForTests?.Invoke(maximumWait) ?? Task.Delay(maximumWait);
        if (await Task.WhenAny(sealTask!, delayTask).ConfigureAwait(false) != sealTask)
        {
            if (reservation.TryCancel())
            {
                ObserveFault(sealTask!);
                return OutputRecoverySummary.Unavailable(
                    "output_store_seal_timed_out",
                    advertise: true);
            }
            try
            {
                _sealCancellationRejectedForTests?.Invoke();
            }
            catch
            {
                // This observer runs after cancellation has already lost and
                // cannot change the result the coordinator must await.
            }
        }

        OutputSealResult result;
        try { result = await sealTask!; }
        catch
        {
            _ = reservation.TryCancel();
            return OutputRecoverySummary.Unavailable(
                "output_store_unavailable",
                advertise: true);
        }
        finally
        {
            reservation.CompleteObserved();
        }
        return result.Success
            ? OutputRecoverySummary.FromSeal(result)
            : OutputRecoverySummary.Unavailable(
                result.DetailCode ?? "output_store_unavailable",
                advertise: true);
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public void Dispose()
    {
        Interlocked.Exchange(ref _reservation, null)?.Dispose();
        _store = null;
    }
}

/// <summary>Supervisor-owned, harness-lifetime output artifact store. Public
/// handles are random capabilities held only in this table; artifact paths
/// and internal write identities are never model-facing.</summary>
public sealed class OutputStore : IDisposable
{
    internal const int DefaultReadBytes = 16 * 1024;
    internal const int MaximumReadBytes = 64 * 1024;
    internal const int MaximumSearchMatches = 20;
    internal const int MaximumPatternBytes = 1024;

    private const int MaximumTombstones = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _gate = new();
    private readonly OutputStoreOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly string _root;
    private readonly Timer _retentionTimer;
    private readonly Dictionary<Guid, ArtifactEntry> _entries = [];
    private readonly Dictionary<string, Guid> _handles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sessionBytes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, byte> _canceledReservations = [];
    private long _aggregateBytes;
    private long _reservedBytes;
    private long _nextSequence;
    private int _foregroundOperationActive;
    private int _cancelReaperActive;
    private int _retentionRunning;
    private bool _disposed;
    private List<PendingArtifactDelete> _pendingDeletes = [];
    private int _deletesInFlight;

    // How long a reserver waits for another caller's off-gate unlink to
    // finalize before reporting capacity truthfully (rbc-14).
    private static readonly TimeSpan PendingDeleteSettleTimeout =
        TimeSpan.FromSeconds(2);

    internal OutputStore(OutputStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        _options = options;
        _utcNow = options.UtcNow ?? (() => DateTimeOffset.UtcNow);
        _root = SecureAuditStorage.PrepareRoot(options.RootDirectory);
        _retentionTimer = new Timer(
            static state => ((OutputStore)state!).RunRetentionSafely(),
            this,
            options.RetentionInterval,
            options.RetentionInterval);
    }

    internal string RootPathForTests => _root;
    internal long MaximumArtifactBytes => _options.MaximumArtifactBytes;

    internal SafeFileHandle RetainedArtifactHandleForTests(string handle)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var entry = FindReadableLocked(handle) ??
                throw new KeyNotFoundException("The output artifact handle is unavailable.");
            return entry.Stream?.SafeFileHandle ??
                throw new InvalidOperationException("The output artifact has no retained stream.");
        }
    }

    /// <summary>Starts at most one potentially uninterruptible foreground
    /// storage operation. A wedged filesystem call therefore consumes one
    /// worker, while later invocations fail capture immediately and still run
    /// their user operation exactly once.</summary>
    internal bool TryStartForegroundOperation<T>(
        Func<T> operation,
        out Task<T>? task)
    {
        ArgumentNullException.ThrowIfNull(operation);
        task = null;
        if (Interlocked.CompareExchange(
                ref _foregroundOperationActive,
                1,
                0) != 0)
        {
            return false;
        }

        try
        {
            task = Task.Run(() =>
            {
                try { return operation(); }
                finally { Volatile.Write(ref _foregroundOperationActive, 0); }
            });
            return true;
        }
        catch
        {
            Volatile.Write(ref _foregroundOperationActive, 0);
            throw;
        }
    }

    internal bool TryReserve(
        string sessionAlias,
        out OutputCaptureReservation? reservation,
        out string? failure)
    {
        try
        {
            return TryReserveCore(sessionAlias, out reservation, out failure);
        }
        finally
        {
            // Unlink I/O for tombstones claimed by this call runs after the
            // store lock is released (rbc-14).
            DrainPendingArtifactDeletes();
        }
    }

    private bool TryReserveCore(
        string sessionAlias,
        out OutputCaptureReservation? reservation,
        out string? failure)
    {
        reservation = null;
        failure = null;
        if (!IsSessionAlias(sessionAlias))
        {
            failure = "invalid_session";
            return false;
        }

        _options.ReservationStartingForTests?.Invoke();

        while (true)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var now = UtcNow();
                RetainLocked(now);
                var step = MakeArtifactCapacityStepLocked(now);
                if (step == CapacityStep.Ready)
                {
                    step = MakeCapacityStepLocked(
                        sessionAlias, _options.MaximumArtifactBytes, now);
                }

                if (step == CapacityStep.Unavailable)
                {
                    if (_pendingDeletes.Count == 0)
                    {
                        // Another caller (e.g. the retention timer) may hold
                        // claims whose off-gate unlinks have not finalized
                        // yet; wait for a finalize pulse before failing
                        // truthfully.
                        if (_deletesInFlight > 0 &&
                            Monitor.Wait(_gate, PendingDeleteSettleTimeout))
                        {
                            continue;
                        }

                        failure = "capacity";
                        return false;
                    }

                    // Queued claims exist: drain them below and re-check.
                }

                if (step == CapacityStep.Ready)
                {
                    var id = Guid.NewGuid();
                    string handle;
                    do { handle = CreateHandle(); }
                    while (_handles.ContainsKey(handle));
                    var entry = new ArtifactEntry(
                        id,
                        handle,
                        sessionAlias,
                        now,
                        checked(++_nextSequence),
                        OutputArtifactState.Incomplete)
                    {
                        Capturing = true,
                        ReservedBytes = _options.MaximumArtifactBytes,
                        DetailCode = "capture_pending",
                    };
                    _entries.Add(id, entry);
                    _handles.Add(handle, id);
                    _sessionBytes.TryAdd(sessionAlias, 0);
                    _reservedBytes = checked(_reservedBytes + entry.ReservedBytes);
                    reservation = new OutputCaptureReservation(this, id);
                    return true;
                }

                // step == CapacityStep.Claimed: an eviction candidate was
                // tombstoned; its unlink must run outside _gate before the
                // freed bytes become visible to the capacity checks.
            }

            // No successful unlink and nothing left in flight means capacity
            // truly cannot be made right now (e.g. a wedged filesystem):
            // fail truthfully instead of tombstoning every remaining
            // artifact (rbc-14).
            if (DrainPendingArtifactDeletes() == 0)
            {
                lock (_gate)
                {
                    if (_deletesInFlight == 0 ||
                        !Monitor.Wait(_gate, PendingDeleteSettleTimeout))
                    {
                        failure = "capacity";
                        return false;
                    }
                }
            }
        }
    }

    internal OutputArtifactStatus Status(string handle)
    {
        OutputArtifactStatus result;
        lock (_gate)
        {
            ThrowIfDisposed();
            var now = UtcNow();
            RetainLocked(now);
            var entry = FindReadableLocked(handle);
            result = entry is null
                ? MissingStatus()
                : StatusOf(entry);
        }

        // Unlink I/O for tombstones claimed by this call's retention pass
        // runs after the store lock is released (rbc-14).
        DrainPendingArtifactDeletes();
        return result;
    }

    internal OutputReadResult Read(string handle, long offset, int maximumBytes)
    {
        try
        {
            return ReadCore(handle, offset, maximumBytes);
        }
        finally
        {
            // Unlink I/O for tombstones claimed by this call's retention
            // pass runs after the store lock is released (rbc-14).
            DrainPendingArtifactDeletes();
        }
    }

    private OutputReadResult ReadCore(string handle, long offset, int maximumBytes)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        ValidateReadBound(maximumBytes);

        SafeFileHandle file;
        long totalBytes;
        bool complete;
        OutputProvenance? provenance;
        lock (_gate)
        {
            ThrowIfDisposed();
            var now = UtcNow();
            RetainLocked(now);
            var entry = FindReadableLocked(handle);
            if (entry is null) return MissingRead(offset);
            if (!IsReadableArtifact(entry)) return StateRead(entry, offset);
            file = entry.Stream!.SafeFileHandle;
            totalBytes = entry.Bytes;
            complete = entry.Complete;
            provenance = entry.Provenance;
        }

        // Sealed artifacts are immutable, so the file reads run outside _gate:
        // a wedged filesystem must not stall Status/TryReserve/Seal callers
        // queued behind this read (rbc-7). The test hook rides ReadExact —
        // the io primitive — so the guard test wedges the real read path. A
        // concurrent retention pass may dispose the stream mid-read; that
        // surfaces as ObjectDisposedException, and every return path
        // (success, invalid offset, insufficient bound) re-validates the
        // entry under _gate so the reported artifact state reflects
        // completion time, never a pre-read snapshot. Retention's
        // unlink/dispose I/O likewise runs outside _gate via
        // DrainPendingArtifactDeletes (rbc-14).
        try
        {
            if (offset > totalBytes || !IsUtf8Boundary(file, offset, totalBytes))
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    var entry = FindReadableLocked(handle);
                    if (entry is null) return MissingRead(offset);
                    if (!IsReadableArtifact(entry)) return StateRead(entry, offset);
                    return new OutputReadResult(
                        OutputArtifactState.InvalidOffset,
                        string.Empty,
                        offset,
                        offset,
                        totalBytes,
                        0,
                        complete,
                        provenance,
                        "offset_not_utf8_boundary");
                }
            }

            var bytes = ReadUtf8Chunk(file, offset, maximumBytes, totalBytes);
            if (bytes.Length == 0 && offset < totalBytes)
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    var entry = FindReadableLocked(handle);
                    if (entry is null) return MissingRead(offset);
                    if (!IsReadableArtifact(entry)) return StateRead(entry, offset);
                    return new OutputReadResult(
                        OutputArtifactState.InsufficientBound,
                        string.Empty,
                        offset,
                        offset,
                        totalBytes,
                        0,
                        complete,
                        provenance,
                        "max_bytes_too_small_for_next_utf8_scalar");
                }
            }
            var text = StrictUtf8.GetString(bytes);
            var nextOffset = checked(offset + bytes.Length);
            lock (_gate)
            {
                ThrowIfDisposed();
                var entry = FindReadableLocked(handle);
                if (entry is null) return MissingRead(offset);
                if (!IsReadableArtifact(entry)) return StateRead(entry, offset);
                return new OutputReadResult(
                    entry.State,
                    text,
                    offset,
                    nextOffset,
                    totalBytes,
                    bytes.Length,
                    entry.Complete,
                    entry.Provenance,
                    entry.DetailCode);
            }
        }
        catch (ObjectDisposedException)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var entry = FindReadableLocked(handle);
                return entry is null ? MissingRead(offset) : StateRead(entry, offset);
            }
        }
    }

    internal OutputSearchResult Search(
        string handle,
        string pattern,
        long offset,
        int maximumBytes)
    {
        try
        {
            return SearchCore(handle, pattern, offset, maximumBytes);
        }
        finally
        {
            // Unlink I/O for tombstones claimed by this call's retention
            // pass runs after the store lock is released (rbc-14).
            DrainPendingArtifactDeletes();
        }
    }

    private OutputSearchResult SearchCore(
        string handle,
        string pattern,
        long offset,
        int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        ValidateReadBound(maximumBytes);
        var patternBytes = StrictUtf8.GetBytes(pattern);
        if (patternBytes.Length is < 1 or > MaximumPatternBytes)
            throw new ArgumentOutOfRangeException(nameof(pattern));
        if (maximumBytes < patternBytes.Length)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        SafeFileHandle file;
        long totalBytes;
        bool complete;
        OutputProvenance? provenance;
        lock (_gate)
        {
            ThrowIfDisposed();
            var now = UtcNow();
            RetainLocked(now);
            var entry = FindReadableLocked(handle);
            if (entry is null) return MissingSearch(offset);
            if (!IsReadableArtifact(entry)) return StateSearch(entry, offset);
            file = entry.Stream!.SafeFileHandle;
            totalBytes = entry.Bytes;
            complete = entry.Complete;
            provenance = entry.Provenance;
        }

        // See Read: file io runs outside _gate (rbc-7); the test hook rides
        // ReadExact, dispose races map to ObjectDisposedException, and every
        // return path (success, invalid offset, insufficient bound)
        // re-validates the entry state under _gate at completion.
        try
        {
            if (offset > totalBytes || !IsUtf8Boundary(file, offset, totalBytes))
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    var entry = FindReadableLocked(handle);
                    if (entry is null) return MissingSearch(offset);
                    if (!IsReadableArtifact(entry)) return StateSearch(entry, offset);
                    return new OutputSearchResult(
                        OutputArtifactState.InvalidOffset,
                        [],
                        offset,
                        offset,
                        totalBytes,
                        0,
                        complete,
                        provenance,
                        offset > totalBytes ? "offset_past_end" : "offset_not_utf8_boundary");
                }
            }

            var scan = ReadUtf8Chunk(file, offset, maximumBytes, totalBytes);
            if (scan.Length == 0 && offset < totalBytes)
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    var entry = FindReadableLocked(handle);
                    if (entry is null) return MissingSearch(offset);
                    if (!IsReadableArtifact(entry)) return StateSearch(entry, offset);
                    return new OutputSearchResult(
                        OutputArtifactState.InsufficientBound,
                        [],
                        offset,
                        offset,
                        totalBytes,
                        0,
                        complete,
                        provenance,
                        "max_bytes_too_small_for_next_utf8_scalar");
                }
            }
            var matches = new List<OutputSearchMatch>();
            var cursor = 0;
            while (cursor <= scan.Length - patternBytes.Length &&
                   matches.Count < MaximumSearchMatches)
            {
                var relative = scan.AsSpan(cursor).IndexOf(patternBytes);
                if (relative < 0) break;
                relative += cursor;
                matches.Add(new OutputSearchMatch(
                    checked(offset + relative),
                    Preview(scan, relative, patternBytes.Length)));
                cursor = relative + Math.Max(1, patternBytes.Length);
            }

            long nextOffset;
            if (matches.Count == MaximumSearchMatches)
            {
                nextOffset = checked(matches[^1].Offset + patternBytes.Length);
            }
            else if (offset + scan.Length >= totalBytes)
            {
                nextOffset = totalBytes;
            }
            else
            {
                var overlap = Math.Min(patternBytes.Length - 1, Math.Max(0, scan.Length - 1));
                nextOffset = checked(offset + scan.Length - overlap);
                while (nextOffset < offset + scan.Length &&
                       !IsUtf8Boundary(file, nextOffset, totalBytes))
                {
                    nextOffset++;
                }
                if (nextOffset <= offset) nextOffset = checked(offset + scan.Length);
            }

            lock (_gate)
            {
                ThrowIfDisposed();
                var entry = FindReadableLocked(handle);
                if (entry is null) return MissingSearch(offset);
                if (!IsReadableArtifact(entry)) return StateSearch(entry, offset);
                return new OutputSearchResult(
                    entry.State,
                    [.. matches],
                    offset,
                    nextOffset,
                    totalBytes,
                    scan.Length,
                    entry.Complete,
                    entry.Provenance,
                    entry.DetailCode);
            }
        }
        catch (ObjectDisposedException)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var entry = FindReadableLocked(handle);
                return entry is null ? MissingSearch(offset) : StateSearch(entry, offset);
            }
        }
    }

    internal void RunRetentionForTests()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RetainLocked(UtcNow());
        }

        DrainPendingArtifactDeletes();
    }

    internal OutputSealResult Seal(
        OutputCaptureReservation reservation,
        Guid artifactId,
        OutputArtifactContent content)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(content);
        ArtifactEntry entry;
        lock (_gate)
        {
            if (_disposed ||
                _canceledReservations.ContainsKey(artifactId) ||
                !_entries.TryGetValue(artifactId, out entry!) ||
                !entry.Capturing)
            {
                return new OutputSealResult(
                    false,
                    null,
                    OutputArtifactState.NotFound,
                    0,
                    "reservation_unavailable");
            }
        }

        var path = Path.Combine(_root, $"artifact-{artifactId:N}.out");
        RenderResult rendered;
        FileStream? stream = null;
        var pathLinked = false;
        try
        {
            _options.ArtifactCreateStartingForTests?.Invoke(path);
            stream = SecureAuditStorage.CreateExclusiveFile(
                path,
                share: FileShare.Delete,
                access: FileAccess.ReadWrite);
            pathLinked = true;
            // The protected file is still empty here. Remove its directory
            // entry before rendering any invocation bytes, so a hard-killed
            // supervisor cannot strand sensitive named output. Recovery reads
            // only through this retained handle. The storage primitive proves
            // that this exact handle is unlinked on Unix or newly delete-pending
            // on Windows before returning; managed DeleteOnClose is deliberately
            // absent because it can delete a later Unix path substitute.
            SecureAuditStorage.DeleteRetainedProtectedFile(
                _root,
                path,
                stream.SafeFileHandle,
                identityVerifiedForTests: () =>
                    _options.ArtifactUnlinkIdentityVerifiedForTests?.Invoke(path));
            pathLinked = false;
            _options.ArtifactWriteStartingForTests?.Invoke(path);
            rendered = Render(stream, content, _options.MaximumArtifactBytes);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            if (stream is not null)
            {
                if (pathLinked) TryDeleteRetainedProtectedFile(path, stream);
                stream.Dispose();
            }
            lock (_gate)
            {
                if (_entries.TryGetValue(artifactId, out var failed) && failed.Capturing)
                    RemoveReservationLocked(failed, removeHandle: true);
                _canceledReservations.TryRemove(artifactId, out _);
            }
            return new OutputSealResult(
                false,
                null,
                OutputArtifactState.NotFound,
                0,
                "storage_unavailable");
        }

        DateTimeOffset sealedUtc;
        DateTimeOffset expiresUtc;
        string? detailCode;
        bool complete;
        OutputArtifactState state;
        try
        {
            sealedUtc = UtcNow();
            expiresUtc = sealedUtc.Add(_options.TimeToLive);
            complete = content.Complete && !rendered.Truncated;
            state = complete
                ? OutputArtifactState.Available
                : OutputArtifactState.Incomplete;
            detailCode = rendered.Truncated
                ? "artifact_cap_exceeded"
                : content.Complete ? null : NormalizeDetail(content.IncompleteReason);
        }
        catch
        {
            RemoveFailedReservation(artifactId);
            if (pathLinked) TryDeleteRetainedProtectedFile(path, stream);
            stream.Dispose();
            return new OutputSealResult(
                false,
                null,
                OutputArtifactState.NotFound,
                0,
                "storage_unavailable");
        }

        OutputSealResult? failure = null;
        OutputSealResult? success = null;
        lock (_gate)
        {
            if (_disposed ||
                _canceledReservations.ContainsKey(artifactId) ||
                !_entries.TryGetValue(artifactId, out entry!) ||
                !entry.Capturing)
            {
                if (_entries.TryGetValue(artifactId, out var canceled) &&
                    canceled.Capturing)
                {
                    RemoveReservationLocked(canceled, removeHandle: true);
                }
                _canceledReservations.TryRemove(artifactId, out _);
                failure = new OutputSealResult(
                    false,
                    null,
                    OutputArtifactState.NotFound,
                    0,
                    "reservation_unavailable");
            }
            else
            {
                long aggregateBytes;
                long sessionBytes;
                try
                {
                    // These are the final potentially throwing calculations.
                    // They must precede the cancel-versus-publish claim.
                    aggregateBytes = checked(_aggregateBytes + rendered.Bytes);
                    sessionBytes = checked(
                        SessionBytesLocked(entry.SessionAlias) + rendered.Bytes);
                }
                catch
                {
                    RemoveReservationLocked(entry, removeHandle: true);
                    failure = new OutputSealResult(
                        false,
                        null,
                        OutputArtifactState.NotFound,
                        0,
                        "storage_unavailable");
                    goto PublishFinished;
                }

                if (!reservation.TryBeginPublishing())
                {
                    RemoveReservationLocked(entry, removeHandle: true);
                    _canceledReservations.TryRemove(artifactId, out _);
                    failure = new OutputSealResult(
                        false,
                        null,
                        OutputArtifactState.NotFound,
                        0,
                        "reservation_unavailable");
                    goto PublishFinished;
                }

                try
                {
                    _options.ArtifactPublishingClaimedForTests?.Invoke();
                }
                catch
                {
                    // Publication is already irreversible. A test observer
                    // cannot be allowed to make the claimed transition throw.
                }

                // Publication is now irreversible and contains only
                // nonthrowing assignments to already allocated state.
                _reservedBytes -= entry.ReservedBytes;
                entry.ReservedBytes = 0;
                entry.Capturing = false;
                entry.Path = pathLinked ? path : null;
                entry.Stream = stream;
                entry.Bytes = rendered.Bytes;
                entry.Provenance = content.Provenance;
                entry.Complete = complete;
                entry.State = state;
                entry.DetailCode = detailCode;
                entry.SealedUtc = sealedUtc;
                entry.ExpiresUtc = expiresUtc;
                entry.Segments = rendered.Segments;
                _aggregateBytes = aggregateBytes;
                _sessionBytes[entry.SessionAlias] = sessionBytes;
                reservation.MarkPublished();

                success = new OutputSealResult(
                    true,
                    entry.Handle,
                    entry.State,
                    entry.Bytes,
                    entry.DetailCode);
            }

        PublishFinished:;
        }

        if (success is not null) return success;
        if (pathLinked) TryDeleteRetainedProtectedFile(path, stream);
        stream.Dispose();
        return failure!;
    }

    internal void CancelSeal(Guid artifactId)
    {
        _canceledReservations.TryAdd(artifactId, 0);
        if (Monitor.TryEnter(_gate))
        {
            try { RemoveCanceledReservationLocked(artifactId); }
            finally { Monitor.Exit(_gate); }
            return;
        }
        ScheduleCancellationReaper();
    }

    internal void Abandon(Guid artifactId) => CancelSeal(artifactId);

    private void RemoveFailedReservation(Guid artifactId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(artifactId, out var entry) && entry.Capturing)
                RemoveReservationLocked(entry, removeHandle: true);
            _canceledReservations.TryRemove(artifactId, out _);
        }
    }

    private void RemoveCanceledReservationLocked(Guid artifactId)
    {
        if (_entries.TryGetValue(artifactId, out var entry) && entry.Capturing)
            RemoveReservationLocked(entry, removeHandle: true);
        _canceledReservations.TryRemove(artifactId, out _);
    }

    private void ScheduleCancellationReaper()
    {
        if (Interlocked.CompareExchange(ref _cancelReaperActive, 1, 0) != 0)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                lock (_gate)
                {
                    foreach (var artifactId in _canceledReservations.Keys)
                        RemoveCanceledReservationLocked(artifactId);
                }
            }
            finally
            {
                Volatile.Write(ref _cancelReaperActive, 0);
                if (!_canceledReservations.IsEmpty)
                    ScheduleCancellationReaper();
            }
        });
    }

    public void Dispose()
    {
        List<(string? Path, FileStream Stream)> artifacts;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _retentionTimer.Dispose();
            artifacts = _entries.Values
                .Where(entry => entry.Stream is not null)
                .Select(entry => (entry.Path, entry.Stream!))
                .ToList();
            _entries.Clear();
            _handles.Clear();
            _sessionBytes.Clear();
            _canceledReservations.Clear();
            _aggregateBytes = 0;
            _reservedBytes = 0;
        }

        foreach (var (path, stream) in artifacts)
        {
            if (path is not null) TryDeleteRetainedProtectedFile(path, stream);
            stream.Dispose();
        }
        try
        {
            Directory.Delete(_root, recursive: false);
        }
        catch
        {
            // Harness exit invalidates every handle. Best-effort cleanup must
            // not turn graceful server shutdown into a crash.
        }
    }

    private void RunRetentionSafely()
    {
        if (Interlocked.CompareExchange(ref _retentionRunning, 1, 0) != 0)
            return;
        try
        {
            lock (_gate)
            {
                if (!_disposed) RetainLocked(UtcNow());
            }

            DrainPendingArtifactDeletes();
        }
        catch
        {
            // A later request receives a truthful unavailable/capacity result;
            // the timer must never terminate the process.
        }
        finally
        {
            Volatile.Write(ref _retentionRunning, 0);
        }
    }

    private CapacityStep MakeCapacityStepLocked(string sessionAlias, long needed, DateTimeOffset now)
    {
        if (SessionBytesLocked(sessionAlias) + ReservedSessionBytesLocked(sessionAlias) + needed >
            _options.MaximumSessionBytes)
        {
            var candidate = OldestAvailableLocked(sessionAlias);
            if (candidate is null) return CapacityStep.Unavailable;
            TombstoneLocked(candidate, OutputArtifactState.Evicted, "session_capacity", now);
            return CapacityStep.Claimed;
        }

        if (_aggregateBytes + _reservedBytes + needed > _options.MaximumAggregateBytes)
        {
            var candidate = OldestAvailableLocked(sessionAlias: null);
            if (candidate is null) return CapacityStep.Unavailable;
            TombstoneLocked(candidate, OutputArtifactState.Evicted, "aggregate_capacity", now);
            return CapacityStep.Claimed;
        }

        return CapacityStep.Ready;
    }

    private CapacityStep MakeArtifactCapacityStepLocked(DateTimeOffset now)
    {
        if (_entries.Values.Count(entry => entry.Capturing || entry.Stream is not null) >=
            _options.MaximumRetainedArtifacts)
        {
            var candidate = OldestAvailableLocked(sessionAlias: null);
            if (candidate is null) return CapacityStep.Unavailable;
            TombstoneLocked(
                candidate,
                OutputArtifactState.Evicted,
                "artifact_count_capacity",
                now);
            return CapacityStep.Claimed;
        }
        return CapacityStep.Ready;
    }

    private void RetainLocked(DateTimeOffset now)
    {
        foreach (var entry in _entries.Values
                     .Where(entry => IsReadableArtifact(entry) && entry.ExpiresUtc <= now)
                     .ToArray())
        {
            TombstoneLocked(entry, OutputArtifactState.Expired, "ttl_expired", now);
        }

        foreach (var entry in _entries.Values
                     .Where(entry => IsTombstoned(entry) && entry.Stream is not null)
                     .ToArray())
        {
            ClaimStoredArtifactDeleteLocked(entry);
        }

        var removable = _entries.Values
            .Where(entry => IsTombstoned(entry) && entry.Stream is null &&
                            entry.TombstonedUtc is { } tombstoned &&
                            tombstoned + _options.TimeToLive <= now)
            .OrderBy(entry => entry.TombstonedUtc)
            .ToList();
        foreach (var entry in removable)
        {
            _entries.Remove(entry.Id);
            _handles.Remove(entry.Handle);
        }

        var tombstones = _entries.Values
            .Where(entry => IsTombstoned(entry) && entry.Stream is null)
            .OrderBy(entry => entry.TombstonedUtc)
            .ToList();
        foreach (var entry in tombstones.Take(Math.Max(0, tombstones.Count - MaximumTombstones)))
        {
            _entries.Remove(entry.Id);
            _handles.Remove(entry.Handle);
        }
    }

    private void TombstoneLocked(
        ArtifactEntry entry,
        OutputArtifactState state,
        string detailCode,
        DateTimeOffset now)
    {
        if (!IsReadableArtifact(entry)) return;
        entry.State = state;
        entry.Complete = false;
        entry.DetailCode = detailCode;
        entry.TombstonedUtc = now;
        entry.ExpiresUtc = null;
        entry.Segments = [];
        ClaimStoredArtifactDeleteLocked(entry);
    }

    private void ClaimStoredArtifactDeleteLocked(ArtifactEntry entry)
    {
        if (entry.DeleteClaimed || entry.Stream is not { } stream) return;
        entry.DeleteClaimed = true;
        _deletesInFlight++;
        _pendingDeletes.Add(new PendingArtifactDelete(entry, stream, entry.Path));
    }

    // Runs the unlink/dispose I/O for claimed tombstones OUTSIDE _gate so a
    // wedged filesystem delete cannot stall Status/Read/Search/TryReserve
    // callers queued on the store lock (rbc-14). Accounting is decremented
    // only after the unlink succeeds, under _gate, so the byte caps are
    // never under-counted; a failed unlink clears the claim and the entry
    // stays visible to the next retention pass for retry. Returns the number
    // of artifacts whose stored bytes were actually released.
    private int DrainPendingArtifactDeletes()
    {
        List<PendingArtifactDelete> pending;
        lock (_gate)
        {
            if (_pendingDeletes.Count == 0) return 0;
            pending = _pendingDeletes;
            _pendingDeletes = [];
        }

        var unlinked = 0;
        foreach (var item in pending)
        {
            var deleted = true;
            try
            {
                // Sealed artifacts already lost their directory entry at seal
                // time (Path is null); their delete io is the retained-handle
                // dispose below. Fire the test seam for both shapes so wedge
                // guards cover the common sealed path (rbc-14).
                _options.ArtifactDeleteStartingForTests?.Invoke(
                    item.Path ?? string.Empty);
                if (item.Path is { } path)
                {
                    SecureAuditStorage.DeleteRetainedProtectedFile(
                        _root,
                        path,
                        item.Stream.SafeFileHandle);
                }
            }
            catch
            {
                deleted = false;
            }

            if (deleted)
            {
                unlinked++;
                try
                {
                    item.Stream.Dispose();
                }
                catch
                {
                    // The file is already unlinked; the accounting below
                    // must still run or the byte caps leak permanently.
                }
            }

            lock (_gate)
            {
                item.Entry.DeleteClaimed = false;
                _deletesInFlight--;
                // Wake reservers waiting for an in-flight unlink to settle.
                Monitor.PulseAll(_gate);
                if (!deleted) continue;
                item.Entry.Stream = null;
                item.Entry.Path = null;
                if (_disposed) continue;
                _aggregateBytes = checked(_aggregateBytes - item.Entry.Bytes);
                _sessionBytes[item.Entry.SessionAlias] = checked(
                    SessionBytesLocked(item.Entry.SessionAlias) - item.Entry.Bytes);
            }
        }

        return unlinked;
    }

    private void RemoveReservationLocked(ArtifactEntry entry, bool removeHandle)
    {
        _reservedBytes -= entry.ReservedBytes;
        entry.ReservedBytes = 0;
        _entries.Remove(entry.Id);
        if (removeHandle) _handles.Remove(entry.Handle);
    }

    private ArtifactEntry? OldestAvailableLocked(string? sessionAlias) =>
        _entries.Values
            .Where(entry => IsReadableArtifact(entry) &&
                            (sessionAlias is null ||
                             string.Equals(entry.SessionAlias, sessionAlias, StringComparison.Ordinal)))
            .OrderBy(entry => entry.SealedUtc)
            .ThenBy(entry => entry.Sequence)
            .FirstOrDefault();

    private ArtifactEntry? FindReadableLocked(string handle)
    {
        if (string.IsNullOrEmpty(handle) ||
            !_handles.TryGetValue(handle, out var id) ||
            !_entries.TryGetValue(id, out var entry) ||
            entry.Capturing)
        {
            return null;
        }
        return entry;
    }

    private static OutputArtifactStatus StatusOf(ArtifactEntry entry) => new(
        entry.State,
        entry.Bytes,
        entry.Complete,
        entry.Provenance,
        entry.ExpiresUtc,
        entry.DetailCode);

    private static OutputArtifactStatus MissingStatus() => new(
        OutputArtifactState.NotFound,
        0,
        false,
        null,
        null,
        "handle_not_found");

    private static OutputReadResult MissingRead(long offset) => new(
        OutputArtifactState.NotFound,
        string.Empty,
        offset,
        offset,
        0,
        0,
        false,
        null,
        "handle_not_found");

    private static OutputReadResult StateRead(ArtifactEntry entry, long offset) => new(
        entry.State,
        string.Empty,
        offset,
        offset,
        0,
        0,
        false,
        entry.Provenance,
        entry.DetailCode);

    private static OutputSearchResult MissingSearch(long offset) => new(
        OutputArtifactState.NotFound,
        [],
        offset,
        offset,
        0,
        0,
        false,
        null,
        "handle_not_found");

    private static OutputSearchResult StateSearch(ArtifactEntry entry, long offset) => new(
        entry.State,
        [],
        offset,
        offset,
        0,
        0,
        false,
        entry.Provenance,
        entry.DetailCode);

    private static bool IsReadableArtifact(ArtifactEntry entry) =>
        entry.Stream is not null &&
        entry.State is OutputArtifactState.Available or OutputArtifactState.Incomplete;

    private static bool IsTombstoned(ArtifactEntry entry) =>
        !entry.Capturing &&
        entry.State is OutputArtifactState.Expired or OutputArtifactState.Evicted;

    private long SessionBytesLocked(string sessionAlias) =>
        _sessionBytes.TryGetValue(sessionAlias, out var bytes) ? bytes : 0;

    private long ReservedSessionBytesLocked(string sessionAlias) =>
        _entries.Values
            .Where(entry => entry.Capturing &&
                            string.Equals(entry.SessionAlias, sessionAlias, StringComparison.Ordinal))
            .Sum(entry => entry.ReservedBytes);

    private DateTimeOffset UtcNow()
    {
        var now = _utcNow();
        if (now.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Output store clock must return UTC.");
        return now;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void ValidateOptions(OutputStoreOptions options)
    {
        if (options.TimeToLive <= TimeSpan.Zero ||
            options.TimeToLive > DateTimeOffset.MaxValue - DateTimeOffset.MinValue)
            throw new ArgumentOutOfRangeException(nameof(options.TimeToLive));
        if (options.RetentionInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.RetentionInterval));
        if (options.MaximumArtifactBytes < 1 ||
            options.MaximumSessionBytes < options.MaximumArtifactBytes ||
            options.MaximumAggregateBytes < options.MaximumSessionBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaximumArtifactBytes));
        }
        if (options.MaximumRetainedArtifacts < 1)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumRetainedArtifacts));
    }

    private static void ValidateReadBound(int maximumBytes)
    {
        if (maximumBytes is < 1 or > MaximumReadBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    }

    private static bool IsSessionAlias(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 ||
            value[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9'))
        {
            return false;
        }
        return value.All(character => character is
            (>= 'a' and <= 'z') or
            (>= '0' and <= '9') or '.' or '_' or '-');
    }

    private static string CreateHandle()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return "ptko_" + Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string? NormalizeDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "capture_incomplete";
        var normalized = new string(value
            .ToLowerInvariant()
            .Select(character => character is
                (>= 'a' and <= 'z') or
                (>= '0' and <= '9') or '_' or '-'
                    ? character
                    : '_')
            .Take(64)
            .ToArray());
        return normalized.Length == 0 ? "capture_incomplete" : normalized;
    }

    private static RenderResult Render(
        FileStream stream,
        OutputArtifactContent content,
        long maximumBytes)
    {
        using var writer = new CappedUtf8Writer(stream, maximumBytes);
        var segments = new List<ArtifactSegment>();

        WriteSegment(writer, segments, "stdout", content.StandardOutput);
        if (content.ExitCode is int exitCode)
        {
            EnsureLineBoundary(writer, content.StandardOutput);
            WriteSegment(writer, segments, "exit", $"[exit] {exitCode}");
        }
        WriteLines(writer, segments, "stderr", "[stderr]", content.StandardError);
        WriteLines(writer, segments, "errors", "[errors]", content.Errors);
        WriteLines(writer, segments, "warnings", "[warnings]", content.Warnings);

        return new RenderResult(writer.BytesWritten, writer.Truncated, [.. segments]);
    }

    private static void WriteLines(
        CappedUtf8Writer writer,
        List<ArtifactSegment> segments,
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
        segments.Add(new ArtifactSegment(name, start, writer.BytesWritten - start));
    }

    private static void WriteSegment(
        CappedUtf8Writer writer,
        List<ArtifactSegment> segments,
        string name,
        string text)
    {
        if (string.IsNullOrEmpty(text) || writer.Truncated) return;
        var start = writer.BytesWritten;
        writer.Write(text);
        segments.Add(new ArtifactSegment(name, start, writer.BytesWritten - start));
    }

    private static void EnsureLineBoundary(CappedUtf8Writer writer, string? previousText)
    {
        if (writer.BytesWritten == 0) return;
        if (previousText is not null &&
            (previousText.EndsWith('\n') || previousText.EndsWith('\r')))
        {
            return;
        }
        writer.Write(Environment.NewLine);
    }

    private byte[] ReadUtf8Chunk(
        SafeFileHandle handle,
        long offset,
        int maximumBytes,
        long totalBytes)
    {
        var requested = (int)Math.Min(maximumBytes, totalBytes - offset);
        if (requested == 0) return [];
        var probe = ReadExact(handle, offset, Math.Min(requested + 3, (int)(totalBytes - offset)));
        var end = Math.Min(requested, probe.Length);
        while (end > 0 && !IsUtf8Boundary(probe, end)) end--;
        return probe[..end];
    }

    private bool IsUtf8Boundary(SafeFileHandle handle, long offset, long totalBytes)
    {
        if (offset == 0 || offset == totalBytes) return true;
        var current = ReadExact(handle, offset, 1)[0];
        return (current & 0b1100_0000) != 0b1000_0000;
    }

    private static bool IsUtf8Boundary(byte[] bytes, int offset) =>
        offset == 0 || offset == bytes.Length ||
        (bytes[offset] & 0b1100_0000) != 0b1000_0000;

    private byte[] ReadExact(SafeFileHandle handle, long offset, int count)
    {
        if (count == 0) return [];
        // The wedge hook rides the io primitive itself: a regression that
        // moves any retained-read file io back under _gate makes the guard
        // test wedge _gate here, and its concurrent Status probe times out.
        _options.RetainedReadStartingForTests?.Invoke();
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var current = RandomAccess.Read(
                handle,
                buffer.AsSpan(read, count - read),
                checked(offset + read));
            if (current == 0) break;
            read += current;
        }
        return read == count ? buffer : buffer[..read];
    }

    private void TryDeleteRetainedProtectedFile(string path, FileStream stream)
    {
        try
        {
            SecureAuditStorage.DeleteRetainedProtectedFile(
                _root,
                path,
                stream.SafeFileHandle);
        }
        catch
        {
            // The original failure or graceful shutdown remains authoritative.
            // Never reopen and delete a path that may now name a substitute.
        }
    }

    private static string Preview(byte[] bytes, int matchOffset, int patternLength)
    {
        var start = Math.Max(0, matchOffset - 48);
        var end = Math.Min(bytes.Length, matchOffset + patternLength + 48);
        while (start < matchOffset && !IsUtf8Boundary(bytes, start)) start++;
        while (end > matchOffset + patternLength && !IsUtf8Boundary(bytes, end)) end--;
        return StrictUtf8.GetString(bytes.AsSpan(start, end - start))
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", " ⏎ ", StringComparison.Ordinal);
    }

    private sealed class CappedUtf8Writer(FileStream stream, long maximumBytes) : IDisposable
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

                var buffer = _buffer ?? throw new ObjectDisposedException(nameof(CappedUtf8Writer));
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
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed record ArtifactSegment(string Name, long Offset, long Length);

    private sealed record RenderResult(
        long Bytes,
        bool Truncated,
        ArtifactSegment[] Segments);

    private enum CapacityStep
    {
        Ready,
        Claimed,
        Unavailable,
    }

    private readonly record struct PendingArtifactDelete(
        ArtifactEntry Entry,
        FileStream Stream,
        string? Path);

    private sealed class ArtifactEntry(
        Guid id,
        string handle,
        string sessionAlias,
        DateTimeOffset createdUtc,
        long sequence,
        OutputArtifactState state)
    {
        internal Guid Id { get; } = id;
        internal string Handle { get; } = handle;
        internal string SessionAlias { get; } = sessionAlias;
        internal DateTimeOffset CreatedUtc { get; } = createdUtc;
        internal long Sequence { get; } = sequence;
        internal OutputArtifactState State { get; set; } = state;
        internal bool Capturing { get; set; }
        internal long ReservedBytes { get; set; }
        internal string? Path { get; set; }
        internal FileStream? Stream { get; set; }
        internal bool DeleteClaimed { get; set; }
        internal long Bytes { get; set; }
        internal bool Complete { get; set; }
        internal OutputProvenance? Provenance { get; set; }
        internal string? DetailCode { get; set; }
        internal DateTimeOffset? SealedUtc { get; set; }
        internal DateTimeOffset? ExpiresUtc { get; set; }
        internal DateTimeOffset? TombstonedUtc { get; set; }
        internal ArtifactSegment[] Segments { get; set; } = [];
    }
}
