using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PtkMcpGuardian.Host;
using PtkMcpGuardian.Lifecycle;
using PtkSharedContracts;

namespace PtkMcpGuardian.Standalone;

/// <summary>
/// Owns one replaceable private-host generation behind one stable public MCP
/// dispatcher. The authority semaphore is shared by first-write, inbound
/// response/event, and loss transitions so exactly one side classifies every
/// request. No request is retained for replay.
/// </summary>
internal sealed class GuardianHostSupervisor :
    IGuardianToolDispatcher,
    IAsyncDisposable
{
    internal const int MaximumOutstandingCalls = 64;

    private const string UnsupportedToolText =
        "This standalone guardian routes only ptk_state and ptk_job action=list.";
    private const string CapacityText =
        "The guardian call registry is full; no backend work started.";
    private static readonly TimeSpan DispatchCapabilityLifetime = TimeSpan.FromMinutes(1);

    private readonly object _stateSync = new();
    private readonly SemaphoreSlim _authority = new(1, 1);
    private readonly GuardianBootId _guardianBootId;
    private readonly GuardianHostLifecycleController _lifecycle;
    private readonly IGuardianHostRecoveryManifestSource _manifestSource;
    private readonly IPrivateRequestIdAllocator _requestIds;
    private readonly TimeProvider _timeProvider;
    private readonly IGuardianHostSupervisorScheduler _scheduler;
    private readonly IGuardianHostSupervisorSessionSource _sessionSource;
    private readonly IGuardianHostSupervisorDispatchObserver _dispatchObserver;
    private readonly GuardianHostSupervisorPins _pins;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<long, ActiveCall> _calls = [];
    private readonly Dictionary<Task, string> _background = [];
    private readonly HashSet<GuardianHostClient> _clients = [];

    private ActiveAttempt? _active;
    private TaskCompletionSource<bool> _callsDrained = CompletedSignal();
    private Task? _shutdownTask;
    private Exception? _backgroundFailure;
    private int _startClaimed;
    private int _reservedCalls;
    private bool _stopping;
    private bool _recoverySchedulerRunning;

    internal GuardianHostSupervisor(
        GuardianBootId guardianBootId,
        GuardianHostLifecycleController lifecycle,
        IGuardianHostRecoveryManifestSource manifestSource,
        IPrivateRequestIdAllocator requestIds,
        TimeProvider timeProvider,
        IGuardianHostSupervisorScheduler scheduler,
        IGuardianHostSupervisorSessionSource sessionSource,
        IGuardianHostSupervisorDispatchObserver dispatchObserver,
        GuardianHostSupervisorPins pins)
    {
        _guardianBootId = guardianBootId ??
            throw new ArgumentNullException(nameof(guardianBootId));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _manifestSource = manifestSource ??
            throw new ArgumentNullException(nameof(manifestSource));
        _requestIds = requestIds ?? throw new ArgumentNullException(nameof(requestIds));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _sessionSource = sessionSource ??
            throw new ArgumentNullException(nameof(sessionSource));
        _dispatchObserver = dispatchObserver ??
            throw new ArgumentNullException(nameof(dispatchObserver));
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
    }

    internal int OutstandingCallCount
    {
        get { lock (_stateSync) return _reservedCalls; }
    }

    internal Exception? BackgroundFailure
    {
        get { lock (_stateSync) return _backgroundFailure; }
    }

    internal int BackgroundTaskCount
    {
        get { lock (_stateSync) return _background.Count; }
    }

    internal string BackgroundTaskNames
    {
        get
        {
            lock (_stateSync)
                return string.Join(",", _background.Values.Order(StringComparer.Ordinal));
        }
    }

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _startClaimed, 1, 0) != 0)
            throw new InvalidOperationException("The guardian supervisor can start only once.");

        Task<bool> ready;
        await _authority.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStoppingLocked();
            var transition = _lifecycle.StartInitial();
            if (transition.Attempt is null)
            {
                throw new InvalidOperationException(
                    "The initial private host could not be created.");
            }

            var active = AttachAttemptLocked(transition.Attempt);
            ready = active.Ready.Task;
        }
        finally
        {
            _authority.Release();
        }

        if (!await ready.WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The initial private host did not complete strict initialization.");
        }
    }

    public ValueTask<GuardianToolResult> DispatchAsync(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(arguments);

        if (StringComparer.Ordinal.Equals(toolName, "ptk_state"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new GuardianToolResult(
                EncodeStateSnapshot(),
                isError: false));
        }

        if (!StringComparer.Ordinal.Equals(toolName, "ptk_job") ||
            !arguments.TryGetValue("action", out var action) ||
            action.ValueKind != JsonValueKind.String ||
            !StringComparer.Ordinal.Equals(action.GetString(), "list"))
        {
            return ValueTask.FromResult(new GuardianToolResult(
                UnsupportedToolText,
                isError: true));
        }

        return DispatchJobListAsync(cancellationToken);
    }

    internal PublicStateSnapshot SnapshotState()
    {
        var host = _lifecycle.Snapshot().Host;
        var sessions = _sessionSource.SnapshotSessions();
        return new PublicStateSnapshot(_guardianBootId, host, sessions);
    }

    internal Task ShutdownAsync()
    {
        lock (_stateSync)
        {
            _shutdownTask ??= ShutdownCoreAsync();
            return _shutdownTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _authority.Dispose();
    }

    private async ValueTask<GuardianToolResult> DispatchJobListAsync(
        CancellationToken cancellationToken)
    {
        if (!TryReserveCall())
            return new GuardianToolResult(CapacityText, isError: true);

        ActiveCall? call = null;
        try
        {
            var admission = await CaptureDispatchAsync(cancellationToken)
                .ConfigureAwait(false);
            if (admission.Terminal is { } refused)
                return ToToolResult(refused);

            var active = admission.Active!;
            var target = admission.Target!;
            var callId = new CallId(Guid.CreateVersion7());
            var dispatchToken = NewCapabilityToken();
            var expires = FutureUnixTimeMilliseconds(DispatchCapabilityLifetime);
            var observation = new GuardianHostDispatchObservation(
                active.Lease.Identity,
                target,
                PrivateRequestId: null);

            await _dispatchObserver.BeforeWriteAuthorizationAsync(
                    observation,
                    cancellationToken)
                .ConfigureAwait(false);

            var revalidated = await RevalidateDispatchAsync(active, target, cancellationToken)
                .ConfigureAwait(false);
            if (revalidated is { } changed)
                return ToToolResult(changed);

            try
            {
                _ = await active.Client!.SendRequestAsync(
                        (guardian, host, generation, requestId) =>
                        {
                            var identity = new GuardianPrivateRequestIdentity(
                                host,
                                generation,
                                requestId);
                            var tracker = new GuardianCallDeliveryTracker<
                                GuardianHostSupervisorTerminal>(identity);
                            call = new ActiveCall(active, target, tracker);
                            AddCallUnderAuthority(call);
                            return new OperationRequest(
                                guardian,
                                host,
                                generation,
                                requestId,
                                expires,
                                target.Alias,
                                target.TransitionVersion,
                                target.WorkerIdentity,
                                operationIdentity: null,
                                new JobListOperation(
                                    callId,
                                    new DispatchCapability(dispatchToken, callId, expires)));
                        },
                        onWriteStarting: () => BeginCallWriteUnderAuthority(
                            call!,
                            target),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatalRuntimeException(exception))
            {
                if (call is null)
                {
                    return ToToolResult(
                        await CurrentHostRefusalAsync(
                                active,
                                backendLostBeforeDispatch: true,
                                target.Alias,
                                cancellationToken)
                            .ConfigureAwait(false));
                }

                await ClassifyFailedSendAsync(call, target, exception)
                    .ConfigureAwait(false);
            }

            return ToToolResult(await DeliverAndForgetAsync(call!).ConfigureAwait(false));
        }
        finally
        {
            ReleaseCallReservation();
        }
    }

    private async Task<DispatchAdmission> CaptureDispatchAsync(
        CancellationToken cancellationToken)
    {
        await _authority.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessionSource.TryGetJobListTarget(out var target))
            {
                return new DispatchAdmission(
                    null,
                    null,
                    CreateSessionRefusal(alias: null));
            }

            if (_stopping || _active is not { Client.State: GuardianHostClientState.Ready } active ||
                active.Lease.Stage != GuardianHostAttemptStage.Ready)
            {
                return new DispatchAdmission(
                    null,
                    null,
                    CreateHostRefusalLocked(
                        backendLostBeforeDispatch: false,
                        target.Alias));
            }

            if (!target.ReadyForEffects)
            {
                return new DispatchAdmission(
                    null,
                    null,
                    CreateSessionRefusal(target?.Alias));
            }

            return new DispatchAdmission(active, target, null);
        }
        finally
        {
            _authority.Release();
        }
    }

    private async Task<GuardianHostSupervisorTerminal?> RevalidateDispatchAsync(
        ActiveAttempt expectedActive,
        GuardianHostJobListTarget expectedTarget,
        CancellationToken cancellationToken)
    {
        await _authority.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping || !ReferenceEquals(_active, expectedActive) ||
                expectedActive.Client?.State != GuardianHostClientState.Ready ||
                expectedActive.Lease.Stage != GuardianHostAttemptStage.Ready)
            {
                return CreateHostRefusalLocked(
                    expectedActive,
                    backendLostBeforeDispatch: true,
                    expectedTarget.Alias);
            }

            if (!_sessionSource.TryGetJobListTarget(out var current) ||
                !current.ReadyForEffects ||
                !expectedTarget.SameDispatchIdentity(current))
            {
                return CreateSessionRefusal(expectedTarget.Alias);
            }

            return null;
        }
        finally
        {
            _authority.Release();
        }
    }

    private void BeginCallWriteUnderAuthority(
        ActiveCall call,
        GuardianHostJobListTarget expectedTarget)
    {
        if (!ReferenceEquals(_active, call.Attempt) || _stopping ||
            !_sessionSource.TryGetJobListTarget(out var current) ||
            !current.ReadyForEffects ||
            !expectedTarget.SameDispatchIdentity(current))
        {
            throw new DispatchRefusedException();
        }

        var observation = new GuardianHostDispatchObservation(
            call.Attempt.Lease.Identity,
            expectedTarget,
            call.Tracker.Identity.RequestId);
        _dispatchObserver.OnWriteStarting(observation);
        call.Tracker.BeginFirstWriteAsync(
                static (_, _) => ValueTask.CompletedTask,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private async Task ClassifyFailedSendAsync(
        ActiveCall call,
        GuardianHostJobListTarget target,
        Exception exception)
    {
        _ = exception;
        var state = call.Tracker.Snapshot().State;
        if (state == GuardianPublicDeliveryState.NotDispatched)
        {
            GuardianHostSupervisorTerminal terminal;
            await _authority.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var hostLost = !ReferenceEquals(_active, call.Attempt) ||
                    call.Attempt.Lease.Stage != GuardianHostAttemptStage.Ready;
                terminal = hostLost
                    ? CreateHostRefusalLocked(
                        call.Attempt,
                        backendLostBeforeDispatch: true,
                        target.Alias)
                    : CreateSessionRefusal(target.Alias);
                SignalLocalTerminal(call, terminal);
            }
            finally
            {
                _authority.Release();
            }
            return;
        }

        if (state == GuardianPublicDeliveryState.WriteStarted)
        {
            await ObserveLossAsync(call.Attempt, GuardianHostLossReason.WriterFailure)
                .ConfigureAwait(false);
            if (!call.TerminalAvailable.Task.IsCompleted)
            {
                await _authority.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    SignalClassifiedLossTerminal(call, OutcomeUnknown());
                }
                finally
                {
                    _authority.Release();
                }
            }
        }
    }

    private async Task<GuardianHostSupervisorTerminal> DeliverAndForgetAsync(ActiveCall call)
    {
        await call.TerminalAvailable.Task.ConfigureAwait(false);
        GuardianHostSupervisorTerminal? delivered = call.ClassifiedLossTerminal;
        if (delivered is null)
        {
            var result = await call.Tracker.DeliverPublicTerminalAsync(
                    (terminal, _) =>
                    {
                        delivered = terminal;
                        return ValueTask.CompletedTask;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (result != GuardianPublicTerminalDeliveryResult.Sent || delivered is null)
            {
                throw new InvalidOperationException(
                    "The guardian did not own exactly one public terminal.");
            }
        }

        await _authority.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!_calls.Remove(call.Tracker.Identity.RequestId.Value, out var removed) ||
                !ReferenceEquals(removed, call))
            {
                throw new InvalidOperationException(
                    "The guardian call registry lost terminal ownership.");
            }
            call.Tracker.Stop();
        }
        finally
        {
            _authority.Release();
        }

        return delivered;
    }

    private ActiveAttempt AttachAttemptLocked(GuardianHostAttemptLease lease)
    {
        if (lease.Identity.GuardianBootId != _guardianBootId)
            throw new InvalidOperationException("The lifecycle returned a foreign guardian boot ID.");
        if (lease.Resources is not IGuardianHostConnectedAttemptResources resources)
            throw new InvalidOperationException(
                "The lifecycle attempt did not expose a connected private transport.");

        var active = new ActiveAttempt(lease, resources);
        _active = active;

        if (lease.Stage is GuardianHostAttemptStage.Containing or
            GuardianHostAttemptStage.ContainmentUnconfirmed)
        {
            BeginContainmentLocked(active);
            return active;
        }

        RecoveryManifest manifest;
        try
        {
            ValidateConnectedResources(resources);
            manifest = _manifestSource.Create(lease.Identity) ??
                throw new InvalidOperationException("The recovery manifest source returned null.");
            ValidateManifest(manifest, lease.Identity);
            var clientPins = new GuardianHostClientPins(
                _guardianBootId,
                lease.Identity.HostBootId,
                lease.Identity.HostGeneration,
                resources.HostProcessId,
                _pins.HostExecutableDigest,
                _pins.HostBuildDigest,
                _pins.PublicContractDigest,
                _pins.ConfigurationDigest,
                _pins.CatalogDigest,
                _pins.PackageManifestDigest);
            active.Client = new GuardianHostClient(
                resources.RequestStream,
                resources.EventStream,
                clientPins,
                _requestIds,
                _timeProvider,
                () => AcquireWriteAuthority(active),
                _ => AcquireInboundAuthority(active),
                (_, _) => AcquireInboundAuthority(active),
                static (_, _) => ValueTask.CompletedTask,
                (request, response, _) => HandleResponseUnderAuthorityAsync(
                    active,
                    request,
                    response));
            lock (_stateSync) _clients.Add(active.Client);
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            _lifecycle.ReportLoss(lease, GuardianHostLossReason.ContractMismatch);
            BeginContainmentLocked(active);
            return active;
        }

        if (!_lifecycle.MarkBootstrapping(lease))
        {
            BeginContainmentLocked(active);
            return active;
        }

        TrackBackground(InitializeAttemptAsync(active, manifest));
        TrackBackground(WatchClientFatalAsync(active));
        TrackBackground(WatchHostExitAsync(active));
        TrackBackground(WatchStartupDeadlineAsync(active));
        return active;
    }

    private async Task InitializeAttemptAsync(
        ActiveAttempt active,
        RecoveryManifest manifest)
    {
        try
        {
            await active.Client!.InitializeAsync(manifest, _lifetime.Token)
                .ConfigureAwait(false);
            await _authority.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_active, active) || _stopping ||
                    !_lifecycle.MarkReady(active.Lease))
                {
                    active.Ready.TrySetResult(false);
                    return;
                }
                active.Ready.TrySetResult(true);
                TrackBackground(WatchReadyStabilityAsync(active));
            }
            finally
            {
                _authority.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            active.Ready.TrySetResult(false);
        }
        catch (GuardianHostClientException exception)
        {
            active.Ready.TrySetResult(false);
            await ObserveLossAsync(
                    active,
                    exception.DetailKind == GuardianHostClientFailureKind.ContractMismatch
                        ? GuardianHostLossReason.ContractMismatch
                        : GuardianHostLossReason.InitializationFailure)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            active.Ready.TrySetResult(false);
            await ObserveLossAsync(active, GuardianHostLossReason.InitializationFailure)
                .ConfigureAwait(false);
        }
    }

    private async Task WatchClientFatalAsync(ActiveAttempt active)
    {
        var failure = await active.Client!.Fatal.WaitAsync(_lifetime.Token)
            .ConfigureAwait(false);
        await ObserveLossAsync(active, LossReasonFor(failure)).ConfigureAwait(false);
    }

    private async Task WatchHostExitAsync(ActiveAttempt active)
    {
        await active.Resources.HostExited.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        await ObserveLossAsync(active, GuardianHostLossReason.Exit).ConfigureAwait(false);
    }

    private async Task WatchStartupDeadlineAsync(ActiveAttempt active)
    {
        var now = _timeProvider.GetTimestamp();
        var remainingTicks = active.Lease.StartupDeadline.AbsoluteTimestamp - now;
        var remaining = remainingTicks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(
                (double)remainingTicks / _timeProvider.TimestampFrequency);
        await _scheduler.DelayAsync(remaining, _lifetime.Token).ConfigureAwait(false);

        await _authority.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_active, active)) return;
            if (_lifecycle.ObserveStartupDeadline(active.Lease) ==
                GuardianHostStartupDeadlineDisposition.BeganContainment)
            {
                BeginContainmentLocked(active);
            }
        }
        finally
        {
            _authority.Release();
        }
    }

    private async Task WatchReadyStabilityAsync(ActiveAttempt active)
    {
        await _scheduler.DelayAsync(
                RecoveryCircuitMachine.ReadyStabilityWindow,
                _lifetime.Token)
            .ConfigureAwait(false);
        await _authority.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_active, active) && !_stopping)
                _lifecycle.TryCompleteReadyStability(active.Lease);
        }
        finally
        {
            _authority.Release();
        }
    }

    private async Task ObserveLossAsync(
        ActiveAttempt active,
        GuardianHostLossReason reason)
    {
        await _authority.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_active, active)) return;
            var disposition = _lifecycle.ReportLoss(active.Lease, reason);
            if (disposition is GuardianHostLifecycleLossDisposition.BeganContainment or
                GuardianHostLifecycleLossDisposition.Duplicate &&
                active.Lease.Stage is GuardianHostAttemptStage.Containing or
                    GuardianHostAttemptStage.ContainmentUnconfirmed)
            {
                BeginContainmentLocked(active);
            }
        }
        finally
        {
            _authority.Release();
        }
    }

    private void BeginContainmentLocked(ActiveAttempt active)
    {
        if (Interlocked.Exchange(ref active.ContainmentStarted, 1) != 0)
            return;

        active.Ready.TrySetResult(false);
        var host = _lifecycle.Snapshot().Host;
        active.PrewriteLossHost = host;
        foreach (var call in _calls.Values.Where(value =>
                     ReferenceEquals(value.Attempt, active)).ToArray())
        {
            var loss = call.Tracker.ObserveHostLoss(
                active.Lease.Identity.HostBootId,
                active.Lease.Identity.HostGeneration);
            switch (loss.Disposition)
            {
                case GuardianHostLossDisposition.BackendLostBeforeDispatch:
                    SignalLocalTerminal(
                        call,
                        _stopping
                            ? HostStartFailed()
                            : CreateHostRefusal(
                                host,
                                backendLostBeforeDispatch: true,
                                call.Target.Alias));
                    break;
                case GuardianHostLossDisposition.OutcomeUnknown:
                    SignalClassifiedLossTerminal(call, OutcomeUnknown());
                    break;
                case GuardianHostLossDisposition.RetainedAuthoritativeTerminal:
                case GuardianHostLossDisposition.PublicTerminalAlreadySent:
                    call.TerminalAvailable.TrySetResult(true);
                    break;
                case GuardianHostLossDisposition.StaleHostIdentity:
                    throw new InvalidOperationException(
                        "A current call was bound to a foreign host identity.");
                default:
                    throw new InvalidOperationException("Unknown host-loss classification.");
            }
        }

        TrackBackground(WatchContainmentDeadlineAsync(active));
        TrackBackground(WatchContainmentConfirmationAsync(active));
    }

    private async Task WatchContainmentDeadlineAsync(ActiveAttempt active)
    {
        await _scheduler.DelayAsync(
                GuardianHostLifecycleController.HostContainmentGrace,
                _lifetime.Token)
            .ConfigureAwait(false);
        await _authority.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_active, active))
                _lifecycle.ObserveContainmentDeadline(active.Lease);
        }
        finally
        {
            _authority.Release();
        }
    }

    private async Task WatchContainmentConfirmationAsync(ActiveAttempt active)
    {
        await active.Resources.ContainmentConfirmed.WaitAsync(_lifetime.Token)
            .ConfigureAwait(false);
        GuardianHostClient? oldClient;

        await _authority.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_active, active)) return;
            var transition = _lifecycle.ConfirmContainment(active.Lease);
            if (transition.Disposition is not (
                    GuardianHostContainmentDisposition.Confirmed or
                    GuardianHostContainmentDisposition.Duplicate))
            {
                return;
            }

            oldClient = active.Client;
            _active = null;
            if (transition.StartedAttempt is { } replacement)
                AttachAttemptLocked(replacement);
            else
                ScheduleRecoveryLocked();
        }
        finally
        {
            _authority.Release();
        }

        if (oldClient is not null)
            TrackBackground(DisposeClientAsync(oldClient));
    }

    private void ScheduleRecoveryLocked()
    {
        var state = _lifecycle.Snapshot().Host.State;
        if (_stopping || _recoverySchedulerRunning ||
            state is not (PublicHostState.Backoff or PublicHostState.CircuitOpen))
        {
            return;
        }

        _recoverySchedulerRunning = true;
        TrackBackground(RunRecoverySchedulerAsync());
    }

    private async Task RunRecoverySchedulerAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var snapshot = _lifecycle.Snapshot().Host;
                if (snapshot.State is not (
                        PublicHostState.Backoff or PublicHostState.CircuitOpen) ||
                    snapshot.RetryAfterMilliseconds is not { } retryAfter)
                {
                    return;
                }

                await _scheduler.DelayAsync(
                        TimeSpan.FromMilliseconds(retryAfter),
                        _lifetime.Token)
                    .ConfigureAwait(false);
                await _authority.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (_stopping) return;
                    var transition = _lifecycle.TryStartDueRecovery();
                    if (transition.Attempt is { } attempt)
                    {
                        AttachAttemptLocked(attempt);
                        return;
                    }
                    if (_lifecycle.Snapshot().Host.State is not (
                            PublicHostState.Backoff or PublicHostState.CircuitOpen))
                    {
                        return;
                    }
                }
                finally
                {
                    _authority.Release();
                }
            }
        }
        finally
        {
            await _authority.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _recoverySchedulerRunning = false;
                if (!_stopping)
                    ScheduleRecoveryLocked();
            }
            finally
            {
                _authority.Release();
            }
        }
    }

    private IDisposable? AcquireWriteAuthority(ActiveAttempt active)
    {
        _authority.Wait();
        var release = true;
        try
        {
            if (_stopping || !ReferenceEquals(_active, active) ||
                active.Lease.Stage != GuardianHostAttemptStage.Ready ||
                _lifecycle.BeginFirstWrite(active.Lease, static _ => { }) !=
                    GuardianHostWriteDisposition.Began)
            {
                return null;
            }

            release = false;
            return new AuthorityLease(_authority);
        }
        finally
        {
            if (release) _authority.Release();
        }
    }

    private IDisposable? AcquireInboundAuthority(ActiveAttempt active)
    {
        _authority.Wait();
        if (!_stopping && ReferenceEquals(_active, active) &&
            active.Lease.Stage == GuardianHostAttemptStage.Ready)
        {
            return new AuthorityLease(_authority);
        }

        _authority.Release();
        return null;
    }

    private ValueTask HandleResponseUnderAuthorityAsync(
        ActiveAttempt active,
        GuardianHostMessage request,
        GuardianHostResponse response)
    {
        if (!ReferenceEquals(_active, active) ||
            !_calls.TryGetValue(response.RequestId.Value, out var call) ||
            !ReferenceEquals(call.Attempt, active))
        {
            throw new GuardianHostClientException(
                GuardianHostClientFailureKind.UnknownResponse);
        }

        var identity = new GuardianPrivateRequestIdentity(
            response.HostBootId,
            response.HostGeneration,
            response.RequestId);
        var terminal = TerminalFrom(response);
        if (call.Tracker.TryDecodeTerminal(identity, terminal) !=
            GuardianTerminalCorrelationResult.Accepted)
        {
            throw new GuardianHostClientException(
                GuardianHostClientFailureKind.ResponseCorrelationMismatch);
        }

        _dispatchObserver.OnTerminalDecoded(new GuardianHostDispatchObservation(
            active.Lease.Identity,
            call.Target,
            response.RequestId));
        call.TerminalAvailable.TrySetResult(true);
        _ = request;
        return ValueTask.CompletedTask;
    }

    private async Task<GuardianHostSupervisorTerminal> CurrentHostRefusalAsync(
        ActiveAttempt expectedActive,
        bool backendLostBeforeDispatch,
        CanonicalAlias alias,
        CancellationToken cancellationToken)
    {
        await _authority.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return CreateHostRefusalLocked(
                expectedActive,
                backendLostBeforeDispatch,
                alias);
        }
        finally
        {
            _authority.Release();
        }
    }

    private GuardianHostSupervisorTerminal CreateHostRefusalLocked(
        bool backendLostBeforeDispatch,
        CanonicalAlias alias) =>
        CreateHostRefusal(
            _lifecycle.Snapshot().Host,
            backendLostBeforeDispatch && !_stopping,
            alias);

    private GuardianHostSupervisorTerminal CreateHostRefusalLocked(
        ActiveAttempt expectedActive,
        bool backendLostBeforeDispatch,
        CanonicalAlias alias)
    {
        if (_stopping)
            return HostStartFailed();

        return CreateHostRefusal(
            expectedActive.PrewriteLossHost ?? _lifecycle.Snapshot().Host,
            backendLostBeforeDispatch,
            alias);
    }

    private static GuardianHostSupervisorTerminal CreateHostRefusal(
        PublicHostStateSnapshot host,
        bool backendLostBeforeDispatch,
        CanonicalAlias alias)
    {
        if (host.LastFailureCode == PublicRecoveryDetailCode.HostContractMismatch)
        {
            return RecoveryTerminal(new PublicRecoveryError(
                PublicRecoveryDetailCode.HostContractMismatch,
                retryable: false,
                retryAfterMilliseconds: null,
                recoveryPhase: null,
                recoveryAttempt: null,
                retryGate: null));
        }

        if (backendLostBeforeDispatch &&
            host.RecoveryPhase is { } lostPhase &&
            host.RecoveryAttempt > 0 &&
            host.RetryAfterMilliseconds is { } lostDelay)
        {
            return RecoveryTerminal(new PublicRecoveryError(
                PublicRecoveryDetailCode.BackendLostBeforeDispatch,
                retryable: true,
                lostDelay,
                lostPhase,
                host.RecoveryAttempt,
                new SessionReadyGate(alias)));
        }

        if (host.State is PublicHostState.Recovering or PublicHostState.Backoff or
            PublicHostState.CircuitOpen or PublicHostState.HalfOpen &&
            host.RecoveryPhase is { } phase && host.RecoveryAttempt > 0 &&
            host.RetryAfterMilliseconds is { } retryAfter)
        {
            var detail = host.State == PublicHostState.CircuitOpen
                ? PublicRecoveryDetailCode.HostCircuitOpen
                : PublicRecoveryDetailCode.HostRecovering;
            return RecoveryTerminal(new PublicRecoveryError(
                detail,
                retryable: true,
                retryAfter,
                phase,
                host.RecoveryAttempt,
                new SessionReadyGate(alias)));
        }

        var permanent = host.State == PublicHostState.ContainmentUnconfirmed
            ? PublicRecoveryDetailCode.HostContainmentUnconfirmed
            : PublicRecoveryDetailCode.HostStartFailed;
        return RecoveryTerminal(new PublicRecoveryError(
            permanent,
            retryable: false,
            retryAfterMilliseconds: null,
            recoveryPhase: null,
            recoveryAttempt: null,
            retryGate: null));
    }

    private GuardianHostSupervisorTerminal CreateSessionRefusal(CanonicalAlias? alias)
    {
        var sessions = _sessionSource.SnapshotSessions();
        var session = alias is null
            ? null
            : sessions.FirstOrDefault(value => value.Alias == alias);
        if (session?.RecoveryPhase is { } phase &&
            session.RecoveryAttempt > 0 &&
            session.RetryAfterMilliseconds is { } retryAfter)
        {
            return RecoveryTerminal(new PublicRecoveryError(
                PublicRecoveryDetailCode.SessionRecovering,
                retryable: true,
                retryAfter,
                phase,
                session.RecoveryAttempt,
                new SessionReadyGate(session.Alias)));
        }

        var detail = session?.State == PublicSessionState.RecoveryUnknown
            ? PublicRecoveryDetailCode.SessionRecoveryUnknown
            : PublicRecoveryDetailCode.SessionBootstrapFailed;
        return RecoveryTerminal(new PublicRecoveryError(
            detail,
            retryable: false,
            retryAfterMilliseconds: null,
            recoveryPhase: null,
            recoveryAttempt: null,
            retryGate: null));
    }

    private static GuardianHostSupervisorTerminal OutcomeUnknown() =>
        RecoveryTerminal(new PublicRecoveryError(
            PublicRecoveryDetailCode.OutcomeUnknown,
            retryable: false,
            retryAfterMilliseconds: null,
            recoveryPhase: null,
            recoveryAttempt: null,
            retryGate: null));

    private static GuardianHostSupervisorTerminal HostStartFailed() =>
        RecoveryTerminal(new PublicRecoveryError(
            PublicRecoveryDetailCode.HostStartFailed,
            retryable: false,
            retryAfterMilliseconds: null,
            recoveryPhase: null,
            recoveryAttempt: null,
            retryGate: null));

    private static GuardianHostSupervisorTerminal TerminalFrom(
        GuardianHostResponse response) => response switch
    {
        GuardianHostSuccessResponse
        {
            Payload: OperationCompleted { Result: JobListResult result }
        } => new GuardianHostSupervisorTerminal(result.Text, isError: false),
        GuardianHostErrorResponse
        {
            Error.DetailCode: GuardianHostPrivateDetailCode.OutcomeUnknown
        } => OutcomeUnknown(),
        GuardianHostErrorResponse error => new GuardianHostSupervisorTerminal(
            $"private_host_error:{error.Error.DetailCode}",
            isError: true),
        _ => throw new GuardianHostClientException(
            GuardianHostClientFailureKind.ResponseCorrelationMismatch),
    };

    private static GuardianHostSupervisorTerminal RecoveryTerminal(
        PublicRecoveryError error) => new(
            Encoding.UTF8.GetString(PublicRecoveryCodec.Encode(error)),
            isError: true);

    private static GuardianToolResult ToToolResult(
        GuardianHostSupervisorTerminal terminal) =>
        new(terminal.Text, terminal.IsError);

    private string EncodeStateSnapshot() =>
        Encoding.UTF8.GetString(PublicStateCodec.Encode(SnapshotState()));

    private void AddCallUnderAuthority(ActiveCall call)
    {
        if (_calls.Count >= MaximumOutstandingCalls ||
            !_calls.TryAdd(call.Tracker.Identity.RequestId.Value, call))
        {
            throw new InvalidOperationException("The guardian call registry is inconsistent.");
        }
    }

    private static void SignalLocalTerminal(
        ActiveCall call,
        GuardianHostSupervisorTerminal terminal)
    {
        var result = call.Tracker.TrySetLocalTerminal(terminal);
        if (result is GuardianLocalTerminalResult.Accepted or
            GuardianLocalTerminalResult.TerminalAlreadyDecoded or
            GuardianLocalTerminalResult.PublicTerminalAlreadySent)
        {
            call.TerminalAvailable.TrySetResult(true);
            return;
        }
        throw new InvalidOperationException("A local terminal lost delivery ownership.");
    }

    private static void SignalClassifiedLossTerminal(
        ActiveCall call,
        GuardianHostSupervisorTerminal terminal)
    {
        if (call.ClassifiedLossTerminal is not null)
            return;
        call.ClassifiedLossTerminal = terminal;
        call.TerminalAvailable.TrySetResult(true);
    }

    private bool TryReserveCall()
    {
        lock (_stateSync)
        {
            if (_stopping || _reservedCalls >= MaximumOutstandingCalls)
                return false;
            if (_reservedCalls++ == 0)
            {
                _callsDrained = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return true;
        }
    }

    private void ReleaseCallReservation()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_stateSync)
        {
            if (_reservedCalls <= 0)
                throw new InvalidOperationException("The guardian call reservation underflowed.");
            if (--_reservedCalls == 0)
                drained = _callsDrained;
        }
        drained?.TrySetResult(true);
    }

    private async Task ShutdownCoreAsync()
    {
        Task lifecycleShutdown;
        Task callsDrained;
        lock (_stateSync)
        {
            _stopping = true;
            callsDrained = _callsDrained.Task;
        }

        await _authority.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lifecycleShutdown = _lifecycle.ShutdownAsync();
            if (_active is { } active)
                BeginContainmentLocked(active);
        }
        finally
        {
            _authority.Release();
        }

        await lifecycleShutdown.ConfigureAwait(false);
        await callsDrained.ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);

        GuardianHostClient[] clients;
        lock (_stateSync) clients = _clients.ToArray();
        await Task.WhenAll(clients.Select(DisposeClientAsync)).ConfigureAwait(false);

        Task[] background;
        lock (_stateSync) background = _background.Keys.ToArray();
        try
        {
            await Task.WhenAll(background).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            // TrackBackground retains the first bounded diagnostic.
        }
    }

    private void TrackBackground(
        Task task,
        [CallerArgumentExpression(nameof(task))] string? taskName = null)
    {
        lock (_stateSync) _background.Add(task, taskName ?? "unknown");
        _ = task.ContinueWith(
            completed =>
            {
                lock (_stateSync)
                {
                    _background.Remove(completed);
                    if (completed.IsFaulted &&
                        completed.Exception?.GetBaseException() is not OperationCanceledException &&
                        _backgroundFailure is null)
                    {
                        _backgroundFailure = completed.Exception!.GetBaseException();
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DisposeClientAsync(GuardianHostClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
        }
        finally
        {
            lock (_stateSync) _clients.Remove(client);
        }
    }

    private static GuardianHostLossReason LossReasonFor(
        GuardianHostClientException failure) => failure.DetailKind switch
    {
        GuardianHostClientFailureKind.ContractMismatch =>
            GuardianHostLossReason.ContractMismatch,
        GuardianHostClientFailureKind.UnexpectedEof =>
            GuardianHostLossReason.EndOfStream,
        GuardianHostClientFailureKind.TransportFailure =>
            GuardianHostLossReason.ReaderFailure,
        GuardianHostClientFailureKind.WriteAuthorityRejected =>
            GuardianHostLossReason.WriterFailure,
        _ => GuardianHostLossReason.ProtocolFatal,
    };

    private static void ValidateConnectedResources(
        IGuardianHostConnectedAttemptResources resources)
    {
        if (resources.HostProcessId <= 0)
            throw new InvalidOperationException("The connected host PID must be positive.");
        if (resources.RequestStream is null || !resources.RequestStream.CanWrite)
            throw new InvalidOperationException("The host request stream is not writable.");
        if (resources.EventStream is null || !resources.EventStream.CanRead)
            throw new InvalidOperationException("The host event stream is not readable.");
        if (resources.HostExited is null || resources.ContainmentConfirmed is null)
            throw new InvalidOperationException("The host lifetime tasks are missing.");
    }

    private void ValidateManifest(
        RecoveryManifest manifest,
        GuardianHostIdentity identity)
    {
        if (manifest.GuardianBootId != _guardianBootId ||
            manifest.HostGeneration != identity.HostGeneration ||
            manifest.HostGenerationHighWatermark != identity.HostGeneration ||
            manifest.ConfigurationDigest != _pins.ConfigurationDigest ||
            manifest.CatalogDigest != _pins.CatalogDigest)
        {
            throw new InvalidOperationException(
                "The generation manifest does not match guardian-owned identity pins.");
        }
    }

    private long FutureUnixTimeMilliseconds(TimeSpan duration)
    {
        var value = _timeProvider.GetUtcNow().Add(duration).ToUnixTimeMilliseconds();
        return Math.Max(1, value);
    }

    private static CapabilityToken NewCapabilityToken()
    {
        Span<byte> bytes = stackalloc byte[ContractLimits.CapabilityTokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return new CapabilityToken(Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'));
    }

    private void ThrowIfStoppingLocked()
    {
        if (_stopping)
            throw new InvalidOperationException("The guardian supervisor is stopping.");
    }

    private static bool IsFatalRuntimeException(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var result = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        result.TrySetResult(true);
        return result;
    }

    private sealed class ActiveAttempt(
        GuardianHostAttemptLease lease,
        IGuardianHostConnectedAttemptResources resources)
    {
        internal GuardianHostAttemptLease Lease { get; } = lease;
        internal IGuardianHostConnectedAttemptResources Resources { get; } = resources;
        internal GuardianHostClient? Client { get; set; }
        internal PublicHostStateSnapshot? PrewriteLossHost { get; set; }
        internal TaskCompletionSource<bool> Ready { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ContainmentStarted;
    }

    private sealed class ActiveCall(
        ActiveAttempt attempt,
        GuardianHostJobListTarget target,
        GuardianCallDeliveryTracker<GuardianHostSupervisorTerminal> tracker)
    {
        internal ActiveAttempt Attempt { get; } = attempt;
        internal GuardianHostJobListTarget Target { get; } = target;
        internal GuardianCallDeliveryTracker<GuardianHostSupervisorTerminal> Tracker { get; } = tracker;
        internal GuardianHostSupervisorTerminal? ClassifiedLossTerminal { get; set; }
        internal TaskCompletionSource<bool> TerminalAvailable { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AuthorityLease(SemaphoreSlim authority) : IDisposable
    {
        private SemaphoreSlim? _authority = authority;

        public void Dispose() =>
            Interlocked.Exchange(ref _authority, null)?.Release();
    }

    private sealed class DispatchRefusedException : InvalidOperationException;

    private readonly record struct DispatchAdmission(
        ActiveAttempt? Active,
        GuardianHostJobListTarget? Target,
        GuardianHostSupervisorTerminal? Terminal);
}
