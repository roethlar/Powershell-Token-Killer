using System.Runtime.ExceptionServices;

namespace PtkMcpServer.Audit;

/// <summary>
/// One supervisor generation's audit-owned runtime resources. The journal is
/// available before the optional exporter starts; the gate starts export only
/// after server.started is durable and stops it before server.stopped so the
/// lifecycle terminal remains the final record written by this process.
/// </summary>
internal sealed class AuditRuntimeResources : IDisposable
{
    private readonly bool _ownsJournal;
    private readonly AuditExportLoop? _exportLoop;
    private readonly IDisposable? _checkpointStore;
    private Task? _exportCompletion;
    private int _exportStarted;
    private int _disposed;

    internal AuditRuntimeResources(
        AuditJournal journal,
        bool ownsJournal = true,
        AuditExportLoop? exportLoop = null,
        IDisposable? checkpointStore = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        Journal = journal;
        _ownsJournal = ownsJournal;
        _exportLoop = exportLoop;
        _checkpointStore = checkpointStore;
    }

    internal AuditJournal Journal { get; }

    internal AuditExportLoopSnapshot? ExportSnapshot => _exportLoop?.Snapshot;

    internal void StartExporter()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_exportLoop is null) return;
        if (Interlocked.CompareExchange(ref _exportStarted, 1, 0) != 0)
            throw new InvalidOperationException("The audit exporter has already been started.");
        _exportCompletion = _exportLoop.Start();
    }

    internal async Task StopExporterAsync()
    {
        if (_exportLoop is null || Volatile.Read(ref _exportStarted) == 0) return;
        await _exportLoop.StopAsync().ConfigureAwait(false);
        if (_exportCompletion is not null)
            await _exportCompletion.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Exception? failure = null;

        try
        {
            StopExporterAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure = exception;
        }

        if (_ownsJournal)
        {
            try
            {
                Journal.Dispose();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                failure = Combine(failure, exception);
            }
        }

        if (_exportLoop is not null)
        {
            try
            {
                _exportLoop.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                failure = Combine(failure, exception);
            }
        }

        try
        {
            _checkpointStore?.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure = Combine(failure, exception);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Exception Combine(Exception? prior, Exception next) =>
        prior is null ? next : new AggregateException(prior, next);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
