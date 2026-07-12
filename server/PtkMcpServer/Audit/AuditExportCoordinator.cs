namespace PtkMcpServer.Audit;

internal enum AuditExportCoordinatorStepKind
{
    Advanced,
    Progressed,
    Idle,
    Complete,
    Retry,
    Blocked,
}

internal sealed record AuditExportCoordinatorStep(
    AuditExportCoordinatorStepKind Kind,
    Guid SupervisorBootId,
    bool IsCurrentBoot,
    Guid? EventId,
    string DetailCode,
    AuditExportFailureClass? FailureClass = null,
    TimeSpan? RetryAfter = null,
    bool HasHealthWarning = false);

/// <summary>
/// Serializes the shared OTLP transport across the current boot and one
/// adopted orphan at a time. Orphan acquisition is quota first, checkpoint
/// second, retained segment handles third; missing control state is never
/// created by this path.
/// </summary>
internal sealed class AuditExportCoordinator : IDisposable
{
    private readonly AuditOptions _options;
    private readonly AuditBootExportSource _current;
    private readonly IAuditOtlpExportTransport _transport;
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<Guid> _parkedBoots = [];
    private readonly HashSet<Guid> _completedBoots = [];

    private AdoptedChain? _adopted;
    private AuditExportCoordinatorStep? _currentBlocked;
    private bool _currentComplete;
    private bool _preferCurrent;
    private int _lifecycle;

    internal AuditExportCoordinator(
        AuditOptions options,
        AuditBootExportSource current,
        IAuditOtlpExportTransport transport,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(transport);
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
        {
            throw new ArgumentException(
                "The audit export coordinator requires anchored protection.",
                nameof(options));
        }
        if (!current.UsesOptions(options) ||
            !current.UsesTransport(transport) ||
            !string.Equals(
                options.ExportConfigurationIdentity,
                transport.ConfigurationIdentity,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The current boot and orphan coordinator must share one startup transport.",
                nameof(transport));
        }

        _options = options;
        _current = current;
        _transport = transport;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<AuditExportCoordinatorStep> ExportNextAsync(
        CancellationToken cancellationToken)
    {
        var prior = Interlocked.CompareExchange(ref _lifecycle, 1, 0);
        if (prior == 2)
            throw new ObjectDisposedException(nameof(AuditExportCoordinator));
        if (prior != 0)
        {
            throw new InvalidOperationException(
                "An audit export coordinator step is already running.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_preferCurrent)
            {
                _ = _adopted is not null || TryAdoptOrphan();
                if (_adopted is not null)
                    return await ExportAdoptedAsync(cancellationToken).ConfigureAwait(false);
                _preferCurrent = true;
            }

            if (!_currentComplete && _currentBlocked is null)
            {
                var currentStep = await _current.ExportNextAsync(cancellationToken)
                    .ConfigureAwait(false);
                var mapped = MapCurrent(currentStep);
                if (currentStep.Kind == AuditBootExportStepKind.Idle)
                {
                    _ = _adopted is not null || TryAdoptOrphan();
                    if (_adopted is not null)
                        return await ExportAdoptedAsync(cancellationToken).ConfigureAwait(false);
                    return mapped;
                }

                _preferCurrent = currentStep.Kind == AuditBootExportStepKind.Progressed;
                return mapped;
            }

            _ = _adopted is not null || TryAdoptOrphan();
            if (_adopted is not null)
                return await ExportAdoptedAsync(cancellationToken).ConfigureAwait(false);

            if (_currentBlocked is not null)
                return _currentBlocked;
            return new AuditExportCoordinatorStep(
                AuditExportCoordinatorStepKind.Complete,
                _current.SupervisorBootId,
                IsCurrentBoot: true,
                EventId: null,
                "chain.complete");
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _lifecycle, 0, 1) != 1)
            {
                throw new InvalidOperationException(
                    "The audit export coordinator lifecycle is invalid.");
            }
        }
    }

    private AuditExportCoordinatorStep MapCurrent(AuditBootExportStep step)
    {
        var kind = step.Kind switch
        {
            AuditBootExportStepKind.Advanced => AuditExportCoordinatorStepKind.Advanced,
            AuditBootExportStepKind.Progressed => AuditExportCoordinatorStepKind.Progressed,
            AuditBootExportStepKind.Idle => AuditExportCoordinatorStepKind.Idle,
            AuditBootExportStepKind.Complete => AuditExportCoordinatorStepKind.Complete,
            AuditBootExportStepKind.Retry => AuditExportCoordinatorStepKind.Retry,
            AuditBootExportStepKind.Blocked => AuditExportCoordinatorStepKind.Blocked,
            _ => throw new IOException("The current boot returned an unknown export step."),
        };
        var mapped = new AuditExportCoordinatorStep(
            kind,
            _current.SupervisorBootId,
            IsCurrentBoot: true,
            step.EventId,
            step.DetailCode,
            step.FailureClass,
            step.RetryAfter,
            step.HasHealthWarning);
        if (step.Kind == AuditBootExportStepKind.Blocked)
            _currentBlocked = mapped;
        else if (step.Kind == AuditBootExportStepKind.Complete)
            _currentComplete = true;
        return mapped;
    }

    private async Task<AuditExportCoordinatorStep> ExportAdoptedAsync(
        CancellationToken cancellationToken)
    {
        var adopted = _adopted ?? throw new IOException(
            "The audit export coordinator lost its adopted chain.");
        var step = await adopted.Pump.ExportNextAsync(cancellationToken)
            .ConfigureAwait(false);
        _preferCurrent = true;
        switch (step.Kind)
        {
            case AuditClosedSpoolExportStepKind.Advanced:
                return MapAdopted(adopted, step, AuditExportCoordinatorStepKind.Advanced);
            case AuditClosedSpoolExportStepKind.Retry:
                return MapAdopted(adopted, step, AuditExportCoordinatorStepKind.Retry);
            case AuditClosedSpoolExportStepKind.Blocked:
            {
                var mapped = MapAdopted(
                    adopted,
                    step,
                    AuditExportCoordinatorStepKind.Blocked);
                _parkedBoots.Add(adopted.SupervisorBootId);
                ReleaseAdopted(adopted);
                return mapped;
            }
            case AuditClosedSpoolExportStepKind.ChainComplete:
            {
                var mapped = MapAdopted(
                    adopted,
                    step,
                    step.EventId is null
                        ? AuditExportCoordinatorStepKind.Complete
                        : AuditExportCoordinatorStepKind.Advanced);
                _completedBoots.Add(adopted.SupervisorBootId);
                ReleaseAdopted(adopted);
                return mapped;
            }
            case AuditClosedSpoolExportStepKind.PrefixComplete:
                throw new IOException("An adopted orphan resolved as a live prefix.");
            default:
                throw new IOException("The adopted audit pump returned an unknown step.");
        }
    }

    private static AuditExportCoordinatorStep MapAdopted(
        AdoptedChain adopted,
        AuditClosedSpoolExportStep step,
        AuditExportCoordinatorStepKind kind) =>
        new(
            kind,
            adopted.SupervisorBootId,
            IsCurrentBoot: false,
            step.EventId,
            step.DetailCode,
            step.FailureClass,
            step.RetryAfter,
            step.HasHealthWarning);

    private bool TryAdoptOrphan()
    {
        if (!AuditSpoolQuotaLease.TryAcquireExisting(
                _options.SpoolDirectory,
                out var quota))
        {
            return false;
        }

        try
        {
            var candidates = InventoryCandidateBoots(quota);
            foreach (var supervisorBootId in candidates)
            {
                if (supervisorBootId == _current.SupervisorBootId ||
                    _parkedBoots.Contains(supervisorBootId) ||
                    _completedBoots.Contains(supervisorBootId))
                {
                    continue;
                }

                if (!AuditExportCheckpointStore.TryAcquireExisting(
                        _options,
                        supervisorBootId,
                        out var acquiredStore))
                {
                    continue;
                }

                var store = acquiredStore ?? throw new IOException(
                    "The orphan checkpoint acquisition returned no owner.");
                if (store.Current.ChainComplete)
                {
                    _completedBoots.Add(supervisorBootId);
                    store.Dispose();
                    continue;
                }

                AuditClosedSpoolChainReader? reader = null;
                try
                {
                    reader = new AuditClosedSpoolChainReader(_options, store);
                    var initial = reader.ResolveCheckpointForAdoption(quota);
                    quota = null;
                    var pump = AuditClosedSpoolExportPump.ForAdoptedClosedChain(
                        reader,
                        initial,
                        _transport,
                        _timeProvider);
                    _adopted = new AdoptedChain(
                        supervisorBootId,
                        store,
                        reader,
                        pump);
                    return true;
                }
                catch (AuditSpoolChainBusyException)
                {
                    reader?.Dispose();
                    store.Dispose();
                    quota = null;
                    return false;
                }
                catch
                {
                    reader?.Dispose();
                    store.Dispose();
                    throw;
                }
            }
            return false;
        }
        finally
        {
            quota?.Dispose();
        }
    }

    private Guid[] InventoryCandidateBoots(AuditSpoolQuotaLease quota)
    {
        quota.VerifyOwnership();
        var spoolRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(_options.SpoolDirectory));
        SecureAuditStorage.VerifyExternalProtectedDirectory(spoolRoot);
        var candidates = new HashSet<Guid>();
        var entries = 0;
        foreach (var entry in new DirectoryInfo(spoolRoot)
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            entries = checked(entries + 1);
            if (entries > AuditClosedSpoolChainReader.MaximumSpoolInventoryEntries)
            {
                throw new IOException(
                    "The audit spool inventory exceeds its recovery entry bound.");
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
            if (entry is not FileInfo segment ||
                !AuditSpoolSegmentIdentity.TryParse(segment.Name, out var identity))
            {
                throw new IOException("The audit spool contains an unknown entry.");
            }
            SecureAuditStorage.VerifyExternalProtectedFile(segment.FullName);
            candidates.Add(identity.SupervisorBootId);
        }
        quota.VerifyOwnership();
        return candidates
            .OrderBy(value => value.ToString("N"), StringComparer.Ordinal)
            .ToArray();
    }

    private void ReleaseAdopted(AdoptedChain adopted)
    {
        if (!ReferenceEquals(_adopted, adopted))
            throw new IOException("The audit export coordinator adopted-chain state changed.");
        _adopted = null;
        adopted.Dispose();
    }

    public void Dispose()
    {
        var prior = Interlocked.CompareExchange(ref _lifecycle, 2, 0);
        if (prior == 2) return;
        if (prior == 1)
        {
            throw new InvalidOperationException(
                "A running audit export coordinator step must finish before disposal.");
        }
        try
        {
            _adopted?.Dispose();
            _adopted = null;
        }
        finally
        {
            _current.Dispose();
        }
    }

    private sealed class AdoptedChain(
        Guid supervisorBootId,
        AuditExportCheckpointStore store,
        AuditClosedSpoolChainReader reader,
        AuditClosedSpoolExportPump pump) : IDisposable
    {
        private int _disposed;

        internal Guid SupervisorBootId { get; } = supervisorBootId;

        internal AuditClosedSpoolExportPump Pump { get; } = pump;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                Pump.Dispose();
            }
            finally
            {
                try
                {
                    reader.Dispose();
                }
                finally
                {
                    store.Dispose();
                }
            }
        }
    }
}
