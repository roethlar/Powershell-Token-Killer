namespace PtkMcpServer.Audit;

/// <summary>
/// Runs fail-closed awaiting-evidence reconciliation at startup and at a
/// bounded periodic cadence. An indeterminate spool snapshot changes no
/// artifact state; a protected evidence-storage failure closes admission.
/// </summary>
internal sealed class AuditEvidenceOrphanReconciler
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly AuditJournal _journal;
    private readonly ScriptEvidenceStoreProvider _evidence;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private DateTimeOffset _nextAttemptUtc = DateTimeOffset.MinValue;

    internal AuditEvidenceOrphanReconciler(
        AuditJournal journal,
        ScriptEvidenceStoreProvider evidence,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = interval ?? DefaultInterval;
        if (_interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
    }

    internal bool ReconcileNow()
    {
        lock (_gate)
            _nextAttemptUtc = checked(_timeProvider.GetUtcNow() + _interval);
        return ReconcileCore();
    }

    internal bool ReconcileIfDue()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (now < _nextAttemptUtc) return false;
            _nextAttemptUtc = checked(now + _interval);
        }
        return ReconcileCore();
    }

    private bool ReconcileCore()
    {
        try
        {
            return _evidence.ReconcileExistingAwaiting(_journal);
        }
        catch (ScriptEvidenceStorageException)
        {
            _journal.EnterExternalUnavailable("evidence.storage");
            return false;
        }
    }
}

/// <summary>
/// Gives a long-lived exporter safe periodic reconciliation points without
/// coupling evidence retention to transport success or checkpoint progress.
/// </summary>
internal sealed class AuditEvidenceReconcilingExportSource(
    IAuditExportStepSource inner,
    AuditEvidenceOrphanReconciler reconciler) : IAuditExportStepSource
{
    private readonly IAuditExportStepSource _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly AuditEvidenceOrphanReconciler _reconciler =
        reconciler ?? throw new ArgumentNullException(nameof(reconciler));

    public Task<AuditExportCoordinatorStep> ExportNextAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = _reconciler.ReconcileIfDue();
        return _inner.ExportNextAsync(cancellationToken);
    }

    public void Dispose() => _inner.Dispose();
}
