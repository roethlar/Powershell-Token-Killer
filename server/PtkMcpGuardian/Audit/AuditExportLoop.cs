using System.Runtime.ExceptionServices;

namespace PtkMcpServer.Audit;

internal interface IAuditExportStepSource : IDisposable
{
    Task<AuditExportCoordinatorStep> ExportNextAsync(
        CancellationToken cancellationToken);
}

internal interface IAuditExportHealthObserver
{
    void Observe(AuditExportHealthObservation observation);
}

internal enum AuditExportLoopState
{
    Created,
    Running,
    WaitingForWork,
    WaitingToRetry,
    Completed,
    Stopped,
    Faulted,
    Disposed,
}

internal sealed record AuditExportLoopSnapshot(
    AuditExportLoopState State,
    AuditExportCoordinatorStep? LastStep,
    TimeSpan? ScheduledDelay);

internal sealed record AuditExportHealthObservation(
    AuditExportLoopSnapshot Snapshot,
    AuditExportCoordinatorStep? ObservedStep,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Owns one export coordinator and runs its single-step contract in the
/// background. The coordinator remains the only component that performs a
/// remote attempt; this loop supplies retry and idle timing around those
/// steps and exposes a nonsecret lifecycle snapshot for health reporting.
/// </summary>
internal sealed class AuditExportLoop : IAsyncDisposable
{
    private static readonly TimeSpan DefaultIdlePollInterval =
        TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly IAuditExportStepSource _source;
    private readonly AuditExportRetrySchedule _retrySchedule;
    private readonly TimeSpan _idlePollInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _timeProvider;
    private readonly IAuditExportHealthObserver? _healthObserver;
    private readonly HashSet<BlockedStepIdentity> _blocksWithoutProgress = [];
    private readonly TaskCompletionSource _disposeFinished = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private AuditExportLoopSnapshot _snapshot = new(
        AuditExportLoopState.Created,
        LastStep: null,
        ScheduledDelay: null);
    private CancellationTokenSource? _stopSource;
    private Task? _completion;
    private int _disposeStarted;

    internal AuditExportLoop(
        IAuditExportStepSource source,
        AuditExportRetrySchedule? retrySchedule = null,
        TimeSpan? idlePollInterval = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IAuditExportHealthObserver? healthObserver = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var pollInterval = idlePollInterval ?? DefaultIdlePollInterval;
        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idlePollInterval));

        var clock = timeProvider ?? TimeProvider.System;
        _source = source;
        _retrySchedule = retrySchedule ?? new AuditExportRetrySchedule();
        _idlePollInterval = pollInterval;
        _timeProvider = clock;
        _healthObserver = healthObserver;
        _delay = delay ?? ((duration, cancellationToken) =>
            Task.Delay(duration, clock, cancellationToken));
        ObserveHealth(_snapshot, observedStep: null);
    }

    internal AuditExportLoopSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot;
        }
    }

    internal Task Start(CancellationToken lifetimeToken = default)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
                throw new ObjectDisposedException(nameof(AuditExportLoop));
            if (_snapshot.State != AuditExportLoopState.Created)
            {
                throw new InvalidOperationException(
                    "The audit export loop can be started only once.");
            }

            _stopSource = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeToken);
            _snapshot = new AuditExportLoopSnapshot(
                AuditExportLoopState.Running,
                LastStep: null,
                ScheduledDelay: null);
            ObserveHealth(_snapshot, observedStep: null);
            _completion = RunLoopAsync(_stopSource.Token);
            return _completion;
        }
    }

    internal async Task StopAsync()
    {
        CancellationTokenSource? stopSource;
        Task? completion;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                stopSource = null;
                completion = _disposeFinished.Task;
            }
            else if (_snapshot.State == AuditExportLoopState.Created)
            {
                _snapshot = _snapshot with
                {
                    State = AuditExportLoopState.Stopped,
                    ScheduledDelay = null,
                };
                ObserveHealth(_snapshot, observedStep: null);
                return;
            }
            else
            {
                stopSource = _stopSource;
                completion = _completion;
            }
        }

        stopSource?.Cancel();
        if (completion is not null)
            await completion.ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        // Ensure Start never executes coordinator work while holding the
        // lifecycle lock, even when a test or future source completes inline.
        await Task.Yield();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateSnapshot(
                    AuditExportLoopState.Running,
                    lastStep: null,
                    replaceLastStep: false,
                    scheduledDelay: null);

                var step = await _source.ExportNextAsync(cancellationToken)
                    .ConfigureAwait(false);
                switch (step.Kind)
                {
                    case AuditExportCoordinatorStepKind.Advanced:
                    case AuditExportCoordinatorStepKind.Progressed:
                        _retrySchedule.Reset();
                        _blocksWithoutProgress.Clear();
                        UpdateSnapshot(
                            AuditExportLoopState.Running,
                            step,
                            replaceLastStep: true,
                            scheduledDelay: null);
                        // A source may have several local transition steps in
                        // succession. Yield between them rather than spinning.
                        await Task.Yield();
                        break;

                    case AuditExportCoordinatorStepKind.Retry:
                    {
                        var delay = _retrySchedule.NextDelay(step.RetryAfter);
                        await WaitAsync(
                                AuditExportLoopState.WaitingToRetry,
                                step,
                                delay,
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }

                    case AuditExportCoordinatorStepKind.Blocked:
                    {
                        var blockedIdentity = BlockedStepIdentity.From(step);
                        if (_blocksWithoutProgress.Add(blockedIdentity))
                        {
                            // A newly parked boot may reveal another orphan or
                            // the current boot. Give the coordinator one
                            // immediate local step before polling a repeated
                            // permanent block.
                            UpdateSnapshot(
                                AuditExportLoopState.Running,
                                step,
                                replaceLastStep: true,
                                scheduledDelay: null);
                            await Task.Yield();
                            break;
                        }

                        await WaitAsync(
                                AuditExportLoopState.WaitingForWork,
                                step,
                                _idlePollInterval,
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }

                    case AuditExportCoordinatorStepKind.Idle:
                        await WaitAsync(
                                AuditExportLoopState.WaitingForWork,
                                step,
                                _idlePollInterval,
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case AuditExportCoordinatorStepKind.Complete:
                        UpdateSnapshot(
                            AuditExportLoopState.Completed,
                            step,
                            replaceLastStep: true,
                            scheduledDelay: null);
                        return;

                    default:
                        throw new IOException(
                            "The audit export coordinator returned an unknown step.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            UpdateSnapshot(
                AuditExportLoopState.Stopped,
                lastStep: null,
                replaceLastStep: false,
                scheduledDelay: null);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            UpdateSnapshot(
                AuditExportLoopState.Faulted,
                lastStep: null,
                replaceLastStep: false,
                scheduledDelay: null);
            throw;
        }
    }

    private async Task WaitAsync(
        AuditExportLoopState state,
        AuditExportCoordinatorStep step,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        UpdateSnapshot(
            state,
            step,
            replaceLastStep: true,
            scheduledDelay: delay);
        await _delay(delay, cancellationToken).ConfigureAwait(false);

        // Full jitter legitimately includes zero. Yield in that vanishingly
        // rare case so a pathological random source cannot monopolize a
        // thread with synchronously completed retries.
        if (delay == TimeSpan.Zero)
            await Task.Yield();
    }

    private void UpdateSnapshot(
        AuditExportLoopState state,
        AuditExportCoordinatorStep? lastStep,
        bool replaceLastStep,
        TimeSpan? scheduledDelay)
    {
        AuditExportLoopSnapshot snapshot;
        lock (_gate)
        {
            if (_snapshot.State == AuditExportLoopState.Disposed)
                return;
            _snapshot = new AuditExportLoopSnapshot(
                state,
                replaceLastStep ? lastStep : _snapshot.LastStep,
                scheduledDelay);
            snapshot = _snapshot;
        }
        ObserveHealth(snapshot, replaceLastStep ? lastStep : null);
    }

    private void ObserveHealth(
        AuditExportLoopSnapshot snapshot,
        AuditExportCoordinatorStep? observedStep)
    {
        try
        {
            _healthObserver?.Observe(new AuditExportHealthObservation(
                snapshot,
                observedStep,
                _timeProvider.GetUtcNow().ToUniversalTime()));
        }
        catch (Exception exception) when (
            !IsFatal(exception) &&
            exception is not AuditExportTransitionPersistenceException)
        {
            // Health reporting is deliberately observational. It cannot turn
            // a successful export/checkpoint operation into an export fault.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0)
        {
            await _disposeFinished.Task.ConfigureAwait(false);
            return;
        }

        CancellationTokenSource? stopSource;
        Task? completion;
        lock (_gate)
        {
            stopSource = _stopSource;
            completion = _completion;
            if (_snapshot.State == AuditExportLoopState.Created)
            {
                _snapshot = _snapshot with
                {
                    State = AuditExportLoopState.Stopped,
                    ScheduledDelay = null,
                };
                ObserveHealth(_snapshot, observedStep: null);
            }
        }

        Exception? failure = null;
        try
        {
            stopSource?.Cancel();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure = exception;
        }

        try
        {
            if (completion is not null)
                await completion.ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }

        try
        {
            _source.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }
        finally
        {
            stopSource?.Dispose();
            lock (_gate)
            {
                _snapshot = _snapshot with
                {
                    State = AuditExportLoopState.Disposed,
                    ScheduledDelay = null,
                };
                ObserveHealth(_snapshot, observedStep: null);
            }
            _disposeFinished.TrySetResult();
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private readonly record struct BlockedStepIdentity(
        Guid SupervisorBootId,
        bool IsCurrentBoot,
        Guid? EventId,
        string DetailCode,
        AuditExportFailureClass? FailureClass)
    {
        internal static BlockedStepIdentity From(AuditExportCoordinatorStep step) =>
            new(
                step.SupervisorBootId,
                step.IsCurrentBoot,
                step.EventId,
                step.DetailCode,
                step.FailureClass);
    }
}
