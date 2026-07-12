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

    internal static AuditRuntimeResources OpenLocal(
        AuditOptions options,
        AuditHealth health,
        string producerVersion,
        ScriptEvidenceStoreProvider evidence)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);
        if (options.ProtectionMode != AuditProtectionMode.LocalOnly)
        {
            throw new ArgumentException(
                "Local runtime resources require local-only audit options.",
                nameof(options));
        }

        AuditJournal? journal = null;
        try
        {
            // The writer creates its new current segment but cannot sweep old
            // closed segments until evidence reconciliation has inspected the
            // complete retained topology. A failed proof closes admission, so
            // the first CanReserve cannot turn uncertainty into deletion.
            journal = AuditJournalFactory.OpenReconciledLocal(
                options,
                health,
                producerVersion,
                evidence);
            var resources = new AuditRuntimeResources(journal);
            journal = null;
            return resources;
        }
        finally
        {
            journal?.Dispose();
        }
    }

    internal static AuditRuntimeResources OpenAnchored(
        AuditOptions options,
        AuditHealth health,
        string producerVersion,
        IAuditOtlpExportTransport transport,
        ScriptEvidenceStoreProvider evidence,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
        {
            throw new ArgumentException(
                "Anchored runtime resources require anchored audit options.",
                nameof(options));
        }
        if (!string.Equals(
                options.ExportConfigurationIdentity,
                transport.ConfigurationIdentity,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The audit transport does not match the startup-frozen export configuration.",
                nameof(transport));
        }

        AuditAnchoredWriterPreparation? preparation = null;
        AuditExportCheckpointStore? checkpointStore = null;
        AuditJournal? journal = null;
        AuditBootExportSource? current = null;
        AuditExportCoordinator? coordinator = null;
        IAuditExportStepSource? exportSource = null;
        AuditExportLoop? exportLoop = null;
        var acknowledgmentObserver =
            new ScriptEvidenceAcknowledgmentObserver(evidence);
        try
        {
            preparation = FileAuditJournalSink.PrepareAnchored(
                options,
                Guid.NewGuid());
            checkpointStore = preparation.CreateCheckpointStore();
            var sink = preparation.Activate(checkpointStore);
            journal = AuditJournalFactory.OpenActivatedAnchored(
                options,
                health,
                producerVersion,
                sink);
            // Activate consumes and releases the staged writer's global spool
            // quota before reconciliation takes evidence then journal then
            // global topology. Startup can therefore never invert the staged
            // global -> checkpoint construction order.
            var evidenceReconciler = new AuditEvidenceOrphanReconciler(
                journal,
                evidence,
                timeProvider);
            _ = evidenceReconciler.ReconcileNow();
            current = new AuditBootExportSource(
                journal,
                checkpointStore,
                transport,
                acknowledgmentObserver,
                timeProvider);
            coordinator = new AuditExportCoordinator(
                options,
                current,
                transport,
                acknowledgmentObserver,
                timeProvider);
            current = null;
            exportSource = new AuditEvidenceReconcilingExportSource(
                coordinator,
                evidenceReconciler);
            coordinator = null;
            exportLoop = new AuditExportLoop(
                exportSource,
                timeProvider: timeProvider,
                healthObserver: new AuditExportTransitionRecorder(
                    journal,
                    health.ExportObserver,
                    AuditBacklogHysteresis.CreateFor(options)));
            exportSource = null;

            var resources = new AuditRuntimeResources(
                journal,
                ownsJournal: true,
                exportLoop,
                checkpointStore);
            journal = null;
            exportLoop = null;
            checkpointStore = null;
            return resources;
        }
        finally
        {
            preparation?.Dispose();
            if (exportLoop is not null)
            {
                try { exportLoop.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch when (journal is not null || checkpointStore is not null)
                {
                    // Continue unwinding the construction graph. A caller sees
                    // the originating construction/disposal failure.
                }
            }
            else
            {
                exportSource?.Dispose();
                coordinator?.Dispose();
                current?.Dispose();
            }
            journal?.Dispose();
            checkpointStore?.Dispose();
        }
    }

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
