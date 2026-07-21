using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PtkMcpGuardian.Host;
using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Ownership;
using PtkMcpGuardian.Output;
using PtkMcpServer.Audit;
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
    private const string OutputAdmissionText =
        "The guardian could not reserve output recovery; no backend work started.";
    private const string JobAdmissionText =
        "The guardian could not reserve a public job identifier; no backend work started.";
    private const string JobLookupText =
        "The guardian does not own an active job with that identifier; no backend work started.";
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
    private readonly GuardianOutputCoordinator? _outputCoordinator;
    private readonly GuardianJobCapabilityRegistry? _jobCapabilities;
    private readonly Func<GuardianHostClientException, CancellationToken, ValueTask>
        _beforeClientFatalObservation;
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
    private int _ownedAttemptWatcherSets;
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
        GuardianHostSupervisorPins pins,
        Func<GuardianHostClientException, CancellationToken, ValueTask>?
            beforeClientFatalObservation = null,
        GuardianOutputCoordinator? outputCoordinator = null,
        GuardianJobCapabilityRegistry? jobCapabilities = null)
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
        _outputCoordinator = outputCoordinator;
        _jobCapabilities = jobCapabilities;
        _beforeClientFatalObservation = beforeClientFatalObservation ??
            (static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            });
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

    internal int OwnedClientCount
    {
        get { lock (_stateSync) return _clients.Count; }
    }

    internal int OwnedAttemptWatcherSetCount
    {
        get { lock (_stateSync) return _ownedAttemptWatcherSets; }
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
        GuardianAuditCall auditCall,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(auditCall);

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

        return DispatchJobListAsync(auditCall, cancellationToken);
    }

    internal PublicStateSnapshot SnapshotState()
    {
        var host = _lifecycle.Snapshot().Host;
        var sessions = GuardianHostSessionStateProjection.Project(
            host,
            _sessionSource.SnapshotSessions());
        return new PublicStateSnapshot(_guardianBootId, host, sessions);
    }

    internal Task ShutdownAsync()
    {
        _lifecycle.ClaimTerminalShutdown();
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

    private ValueTask<GuardianToolResult> DispatchJobListAsync(
        GuardianAuditCall auditCall,
        CancellationToken cancellationToken)
    {
        var callId = auditCall.PublicCallId;
        var expires = FutureUnixTimeMilliseconds(DispatchCapabilityLifetime);
        return DispatchSessionOperationAsync(
            new JobListOperation(
                callId,
                new DispatchCapability(NewCapabilityToken(), callId, expires)),
            operationIdentity: null,
            auditCall,
            cancellationToken);
    }

    /// <summary>
    /// Shared private dispatch core for session-scoped operations. Public tool
    /// parsing and audit admission remain outside this seam; the resulting
    /// audit capability is mandatory here and consumed at final authorization.
    /// </summary>
    internal async ValueTask<GuardianToolResult> DispatchSessionOperationAsync(
        GuardianHostOperation operation,
        GuardianHostOperationIdentity? operationIdentity,
        GuardianAuditCall auditCall,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(auditCall);
        if (operation.Kind is not (
                GuardianHostOperationKind.InvokeForeground or
                GuardianHostOperationKind.JobList))
        {
            throw new ArgumentException(
                "The operation does not use a session dispatch gate.",
                nameof(operation));
        }
        var needsOperationIdentity = operation.Kind ==
            GuardianHostOperationKind.InvokeForeground;
        if (needsOperationIdentity != (operationIdentity is not null))
        {
            throw new ArgumentException(
                "The operation identity does not match the operation kind.",
                nameof(operationIdentity));
        }
        if (operation.OutputCapability is not null && _outputCoordinator is null)
        {
            throw new InvalidOperationException(
                "An output operation requires guardian output ownership.");
        }

        return await DispatchSessionOperationCoreAsync(
                operation,
                backgroundInvoke: null,
                jobControl: null,
                operationIdentity,
                auditCall,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Guardian-owned background-start boundary. The public job identifier is
    /// reserved against the exact selected host/session/worker only inside the
    /// first-write authority, then inserted into the private operation. Public
    /// argument parsing and audit admission remain outside this seam; the
    /// resulting capability is authorized here against the reserved job ID.
    /// </summary>
    internal ValueTask<GuardianToolResult> DispatchBackgroundInvokeAsync(
        CallId callId,
        DispatchCapability dispatchCapability,
        OutputCapability outputCapability,
        string script,
        bool raw,
        GuardianHostInvokeRoute route,
        GuardianHostOperationIdentity operationIdentity,
        GuardianAuditCall auditCall,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operationIdentity);
        ArgumentNullException.ThrowIfNull(auditCall);
        if (_jobCapabilities is null)
        {
            throw new InvalidOperationException(
                "A background invoke requires guardian job ownership.");
        }
        if (_outputCoordinator is null)
        {
            throw new InvalidOperationException(
                "A background invoke requires guardian output ownership.");
        }

        var backgroundInvoke = new BackgroundInvokeDispatch(
            callId,
            dispatchCapability,
            outputCapability,
            script,
            raw,
            route);
        return DispatchSessionOperationCoreAsync(
            operation: null,
            backgroundInvoke,
            jobControl: null,
            operationIdentity,
            auditCall,
            cancellationToken);
    }

    internal ValueTask<GuardianToolResult> DispatchJobStatusAsync(
        CallId callId,
        DispatchCapability dispatchCapability,
        PublicJobId publicJobId,
        GuardianAuditCall auditCall,
        CancellationToken cancellationToken) =>
        DispatchJobControlAsync(
            new JobControlDispatch(
                GuardianHostOperationKind.JobStatus,
                callId,
                dispatchCapability,
                outputCapability: null,
                publicJobId,
                offset: 0),
            auditCall,
            cancellationToken);

    internal ValueTask<GuardianToolResult> DispatchJobOutputAsync(
        CallId callId,
        DispatchCapability dispatchCapability,
        OutputCapability outputCapability,
        PublicJobId publicJobId,
        long offset,
        GuardianAuditCall auditCall,
        CancellationToken cancellationToken) =>
        DispatchJobControlAsync(
            new JobControlDispatch(
                GuardianHostOperationKind.JobOutput,
                callId,
                dispatchCapability,
                outputCapability,
                publicJobId,
                offset),
            auditCall,
            cancellationToken);

    internal ValueTask<GuardianToolResult> DispatchJobKillAsync(
        CallId callId,
        DispatchCapability dispatchCapability,
        PublicJobId publicJobId,
        GuardianAuditCall auditCall,
        CancellationToken cancellationToken) =>
        DispatchJobControlAsync(
            new JobControlDispatch(
                GuardianHostOperationKind.JobKill,
                callId,
                dispatchCapability,
                outputCapability: null,
                publicJobId,
                offset: 0),
            auditCall,
            cancellationToken);

    /// <summary>
    /// Resolves one guardian-held job capability only under the same authority
    /// that guards first write. Public argument parsing and audit admission
    /// remain outside this seam; final audit authorization happens here.
    /// </summary>
    private ValueTask<GuardianToolResult> DispatchJobControlAsync(
        JobControlDispatch jobControl,
        GuardianAuditCall auditCall,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditCall);
        if (_jobCapabilities is null)
        {
            throw new InvalidOperationException(
                "A job operation requires guardian job ownership.");
        }
        if (jobControl.OutputCapability is not null && _outputCoordinator is null)
        {
            throw new InvalidOperationException(
                "A job output operation requires guardian output ownership.");
        }

        return DispatchSessionOperationCoreAsync(
            operation: null,
            backgroundInvoke: null,
            jobControl,
            operationIdentity: null,
            auditCall,
            cancellationToken);
    }

    private async ValueTask<GuardianToolResult> DispatchSessionOperationCoreAsync(
        GuardianHostOperation? operation,
        BackgroundInvokeDispatch? backgroundInvoke,
        JobControlDispatch? jobControl,
        GuardianHostOperationIdentity? operationIdentity,
        GuardianAuditCall auditCall,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditCall);
        var operationSourceCount = (operation is null ? 0 : 1) +
            (backgroundInvoke is null ? 0 : 1) +
            (jobControl is null ? 0 : 1);
        if (operationSourceCount != 1)
        {
            throw new InvalidOperationException(
                "Exactly one private session operation source is required.");
        }

        var publicCallId = auditCall.PublicCallId;
        var sourceCallId = operation?.CallId ??
            backgroundInvoke?.CallId ??
            jobControl!.CallId;
        if (sourceCallId != publicCallId)
        {
            throw new ArgumentException(
                "The private operation call ID must match the admitted guardian audit call.");
        }

        var admission = await CaptureDispatchAsync(cancellationToken)
            .ConfigureAwait(false);
        if (admission.Terminal is { } refused)
        {
            return RecordNoDispatch(
                auditCall,
                admission.Target,
                refused,
                "guardian_dispatch_not_started");
        }

        var active = admission.Active!;
        var target = admission.Target!;
        if (!TryReserveCall())
        {
            return RecordNoDispatch(
                auditCall,
                target,
                new GuardianHostSupervisorTerminal(
                    CapacityText,
                    isError: true,
                    auditDetailCode: "guardian_capacity"),
                "guardian_capacity");
        }

        ActiveCall? call = null;
        try
        {
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
            {
                return RecordNoDispatch(
                    auditCall,
                    target,
                    changed,
                    "guardian_dispatch_not_started");
            }

            try
            {
                _ = await active.Client!.SendRequestAsync(
                        (guardian, host, generation, requestId) =>
                        {
                            GuardianJobRegistration? jobRegistration = null;
                            GuardianOutputRegistration? outputRegistration = null;
                            var callAdded = false;
                            try
                            {
                                var dispatchedOperation = operation;
                                if (backgroundInvoke is not null)
                                {
                                    try
                                    {
                                        if (!_jobCapabilities!.TryReserve(
                                                active.Lease.Identity,
                                                target.Alias,
                                                target.TransitionVersion,
                                                target.WorkerIdentity,
                                                out jobRegistration,
                                                out _))
                                        {
                                            throw new JobAdmissionException();
                                        }
                                    }
                                    catch (PublicJobIdExhaustedException)
                                    {
                                        throw new JobAdmissionException();
                                    }
                                    dispatchedOperation = backgroundInvoke.CreateOperation(
                                        jobRegistration!.PublicJobId);
                                }
                                else if (jobControl is not null)
                                {
                                    if (!_jobCapabilities!.TryGetActive(
                                            jobControl.PublicJobId,
                                            out var jobCapability) ||
                                        !jobCapability!.MatchesOwner(
                                            active.Lease.Identity,
                                            target.Alias,
                                            target.TransitionVersion,
                                            target.WorkerIdentity))
                                    {
                                        throw new JobLookupException();
                                    }
                                    dispatchedOperation = jobControl.CreateOperation(
                                        jobCapability);
                                }

                                var request = new OperationRequest(
                                    guardian,
                                    host,
                                    generation,
                                    requestId,
                                    dispatchedOperation!.DispatchCapability
                                        .ExpiresUnixTimeMilliseconds,
                                    target.Alias,
                                    target.TransitionVersion,
                                    target.WorkerIdentity,
                                    operationIdentity,
                                    dispatchedOperation);
                                if (dispatchedOperation.OutputCapability is not null &&
                                    !_outputCoordinator!.TryRegister(
                                        request,
                                        out outputRegistration,
                                        out _))
                                {
                                    throw new OutputAdmissionException();
                                }

                                var identity = new GuardianPrivateRequestIdentity(
                                    host,
                                    generation,
                                    requestId);
                                var tracker = new GuardianCallDeliveryTracker<
                                    GuardianHostSupervisorTerminal>(identity);
                                call = new ActiveCall(
                                    active,
                                    target,
                                    tracker,
                                    auditCall,
                                    outputRegistration,
                                    jobRegistration);
                                AddCallUnderAuthority(call);
                                callAdded = true;
                                var publicJobId = dispatchedOperation switch
                                {
                                    InvokeBackgroundOperation background =>
                                        background.PublicJobId,
                                    GuardianHostJobIdentityOperation job =>
                                        job.PublicJobId,
                                    _ => null,
                                };
                                if (!auditCall.TryAuthorizeDispatch(
                                        new GuardianAuditDispatchAuthorization(
                                            dispatchedOperation.Kind,
                                            target.AuditSession,
                                            publicJobId)))
                                {
                                    throw new AuditAuthorizationException();
                                }
                                return request;
                            }
                            catch
                            {
                                var registryRemoved = true;
                                if (callAdded)
                                {
                                    registryRemoved = _calls.Remove(
                                            call!.Tracker.Identity.RequestId.Value,
                                            out var removed) &&
                                        ReferenceEquals(removed, call);
                                    callAdded = false;
                                }
                                var outputCanceled = outputRegistration is null ||
                                    _outputCoordinator!.TryCancel(outputRegistration);
                                var jobCanceled = jobRegistration is null ||
                                    _jobCapabilities!.TryCancel(jobRegistration);
                                call = null;
                                if (!registryRemoved || !outputCanceled || !jobCanceled)
                                {
                                    throw new InvalidOperationException(
                                        "The rejected guardian call lost ownership during rollback.");
                                }
                                throw;
                            }
                        },
                        onWriteStarting: () => BeginCallWriteUnderAuthority(
                            call!,
                            target),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OutputAdmissionException)
            {
                return RecordNoDispatch(
                    auditCall,
                    target,
                    new GuardianHostSupervisorTerminal(
                        OutputAdmissionText,
                        isError: true,
                        auditDetailCode: "output_admission_failed"),
                    "output_admission_failed");
            }
            catch (JobAdmissionException)
            {
                return RecordNoDispatch(
                    auditCall,
                    target,
                    new GuardianHostSupervisorTerminal(
                        JobAdmissionText,
                        isError: true,
                        auditDetailCode: "job_admission_failed"),
                    "job_admission_failed");
            }
            catch (JobLookupException)
            {
                return RecordNoDispatch(
                    auditCall,
                    target,
                    new GuardianHostSupervisorTerminal(
                        JobLookupText,
                        isError: true,
                        auditDetailCode: "job_lookup_failed"),
                    "job_lookup_failed");
            }
            catch (AuditAuthorizationException)
            {
                return new GuardianToolResult(
                    "The guardian audit authorization could not be persisted; no backend work started.",
                    isError: true);
            }
            catch (Exception exception) when (!IsFatalRuntimeException(exception))
            {
                if (call is null)
                {
                    var currentRefusal = await CurrentHostRefusalAsync(
                            active,
                            backendLostBeforeDispatch: true,
                            target.Alias,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return RecordNoDispatch(
                        auditCall,
                        target,
                        currentRefusal,
                        "guardian_dispatch_not_started");
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
            if (_active is { } currentActive)
                SynchronizeFaultedClientLossLocked(currentActive);

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
                    target,
                    CreateHostRefusalLocked(
                        backendLostBeforeDispatch: false,
                        target.Alias));
            }

            if (!target.ReadyForEffects)
            {
                return new DispatchAdmission(
                    null,
                    target,
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
            SynchronizeFaultedClientLossLocked(expectedActive);
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
                !current.ReadyForEffects)
            {
                return CreateSessionRefusal(expectedTarget.Alias);
            }
            if (!expectedTarget.SameDispatchIdentity(current))
                return CreateSessionTargetInvalidationRefusal(expectedTarget);

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
        call.AuditCall.MarkPrivateWriteStarting();
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
                    : CreatePrewriteSessionRefusal(target);
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
            if (call.OutputRegistration is not null &&
                !call.OutputResponseResolved)
            {
                _outputCoordinator!.AbandonCall(
                    call.Tracker.Identity.RequestId);
            }
            if (call.JobRegistration is not null &&
                !call.JobResponseResolved &&
                !_jobCapabilities!.TryCancel(call.JobRegistration))
            {
                throw new InvalidOperationException(
                    "The terminal guardian call lost its pending job reservation.");
            }
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

        var active = new ActiveAttempt(lease, resources, _lifetime.Token);
        lock (_stateSync) _ownedAttemptWatcherSets++;
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
                HandleHostEventUnderAuthorityAsync,
                (request, response, _) => HandleResponseUnderAuthorityAsync(
                    active,
                    request,
                    response),
                retainedOutputEventCorrelation: _outputCoordinator is null
                    ? null
                    : _outputCoordinator.MatchesActiveEvent);
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
        var cancellationToken = active.PreContainmentToken;
        try
        {
            await active.Client!.InitializeAsync(manifest, cancellationToken)
                .ConfigureAwait(false);
            await _authority.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_active, active) || _stopping ||
                    !_lifecycle.MarkReady(active.Lease))
                {
                    active.Ready.TrySetResult(false);
                    return;
                }
                active.CancelStartupDeadline();
                active.Ready.TrySetResult(true);
                TrackBackground(WatchReadyStabilityAsync(active));
            }
            finally
            {
                _authority.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        var cancellationToken = active.PreContainmentToken;
        var failure = await active.Client!.Fatal.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        await _beforeClientFatalObservation(failure, cancellationToken)
            .ConfigureAwait(false);
        await ObserveLossAsync(active, LossReasonFor(failure)).ConfigureAwait(false);
    }

    private async Task WatchHostExitAsync(ActiveAttempt active)
    {
        var cancellationToken = active.PreContainmentToken;
        await active.Resources.HostExited.WaitAsync(cancellationToken).ConfigureAwait(false);
        await ObserveLossAsync(active, GuardianHostLossReason.Exit).ConfigureAwait(false);
    }

    private async Task WatchStartupDeadlineAsync(ActiveAttempt active)
    {
        var cancellationToken = active.StartupDeadlineToken;
        await _scheduler.DelayAsync(
                RemainingUntil(active.Lease.StartupDeadline.AbsoluteTimestamp),
                cancellationToken)
            .ConfigureAwait(false);

        await _authority.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        var cancellationToken = active.PreContainmentToken;
        await _scheduler.DelayAsync(
                RecoveryCircuitMachine.ReadyStabilityWindow,
                cancellationToken)
            .ConfigureAwait(false);
        await _authority.WaitAsync(cancellationToken).ConfigureAwait(false);
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

    private void SynchronizeFaultedClientLossLocked(ActiveAttempt active)
    {
        if (active.Client is null ||
            !active.Client.TryGetFatalFailure(out var failure) ||
            failure is null)
        {
            return;
        }

        var disposition = _lifecycle.ReportLoss(active.Lease, LossReasonFor(failure));
        if (disposition is GuardianHostLifecycleLossDisposition.BeganContainment or
            GuardianHostLifecycleLossDisposition.Duplicate &&
            active.Lease.Stage is GuardianHostAttemptStage.Containing or
                GuardianHostAttemptStage.ContainmentUnconfirmed)
        {
            BeginContainmentLocked(active);
        }
    }

    private void BeginContainmentLocked(ActiveAttempt active)
    {
        if (Interlocked.Exchange(ref active.ContainmentStarted, 1) != 0)
            return;

        active.CancelPreContainment();
        active.CancelStartupDeadline();
        active.Ready.TrySetResult(false);
        var host = _lifecycle.Snapshot().Host;
        active.PrewriteLossHost = host;
        _outputCoordinator?.AbandonGeneration(
            active.Lease.Identity.GuardianBootId,
            active.Lease.Identity.HostBootId,
            active.Lease.Identity.HostGeneration,
            "host_generation_lost");
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
        var cancellationToken = active.ContainmentDeadlineToken;
        var deadline = active.Lease.ContainmentDeadline ??
            throw new InvalidOperationException(
                "Containment started without an absolute deadline.");
        while (true)
        {
            await _scheduler.DelayAsync(
                    RemainingUntil(deadline.AbsoluteTimestamp),
                    cancellationToken)
                .ConfigureAwait(false);
            var pending = false;
            await _authority.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_active, active)) return;
                pending = _lifecycle.ObserveContainmentDeadline(active.Lease).Disposition ==
                    GuardianHostContainmentDisposition.Pending;
            }
            finally
            {
                _authority.Release();
            }

            if (!pending) return;
        }
    }

    private TimeSpan RemainingUntil(long absoluteTimestamp)
    {
        var now = _timeProvider.GetTimestamp();
        if (absoluteTimestamp <= now) return TimeSpan.Zero;

        var remainingTimestampTicks = checked(absoluteTimestamp - now);
        var timeSpanTicks = decimal.Ceiling(
            (decimal)remainingTimestampTicks * TimeSpan.TicksPerSecond /
            _timeProvider.TimestampFrequency);
        return TimeSpan.FromTicks(decimal.ToInt64(timeSpanTicks));
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

            active.CancelContainmentDeadline();
            DisposeAttemptWatcherOwnership(active);
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
            SynchronizeFaultedClientLossLocked(active);
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

    private ValueTask HandleHostEventUnderAuthorityAsync(
        GuardianHostEvent hostEvent,
        CancellationToken cancellationToken)
    {
        if (hostEvent is not (OutputChunkEvent or OutputSealEvent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
        if (_outputCoordinator is null)
        {
            throw new InvalidOperationException(
                "The private host emitted output without guardian ownership.");
        }
        return _outputCoordinator.HandleEventAsync(hostEvent, cancellationToken);
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
        ResolveJobResponse(call, response);
        var recovery = call.OutputRegistration is null
            ? null
            : _outputCoordinator!.ResolveResponse(
                response.RequestId,
                response is GuardianHostSuccessResponse);
        call.OutputResponseResolved = call.OutputRegistration is not null;
        var terminal = TerminalFrom(response);
        if (call.OutputRegistration is not null)
        {
            terminal = new GuardianHostSupervisorTerminal(
                GuardianOutputResponseDecorator.Decorate(terminal.Text, recovery),
                terminal.IsError,
                terminal.AuditDetailCode);
        }
        if (call.Tracker.TryDecodeTerminal(identity, terminal) !=
            GuardianTerminalCorrelationResult.Accepted)
        {
            throw new GuardianHostClientException(
                GuardianHostClientFailureKind.ResponseCorrelationMismatch);
        }

        if (StringComparer.Ordinal.Equals(
                terminal.AuditDetailCode,
                "outcome_unknown"))
        {
            call.AuditCall.RecordOutcomeUnknown(terminal.Text);
        }
        else
        {
            call.AuditCall.RecordDecodedTerminal(
                terminal.IsError,
                terminal.Text,
                terminal.AuditDetailCode);
        }

        _dispatchObserver.OnTerminalDecoded(new GuardianHostDispatchObservation(
            active.Lease.Identity,
            call.Target,
            response.RequestId));
        call.TerminalAvailable.TrySetResult(true);
        _ = request;
        return ValueTask.CompletedTask;
    }

    private void ResolveJobResponse(
        ActiveCall call,
        GuardianHostResponse response)
    {
        if (call.JobRegistration is not { } registration)
            return;

        var resolved = response is GuardianHostSuccessResponse
        {
            Payload: OperationCompleted { Result: InvokeBackgroundResult result },
        }
            ? _jobCapabilities!.TryActivate(
                registration,
                result,
                out _,
                out _)
            : _jobCapabilities!.TryCancel(registration);
        if (!resolved)
        {
            throw new GuardianHostClientException(
                GuardianHostClientFailureKind.ResponseCorrelationMismatch);
        }
        call.JobResponseResolved = true;
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

    private GuardianHostSupervisorTerminal CreatePrewriteSessionRefusal(
        GuardianHostJobListTarget expectedTarget)
    {
        if (_sessionSource.TryGetJobListTarget(out var current) &&
            current.ReadyForEffects &&
            !expectedTarget.SameDispatchIdentity(current))
        {
            return CreateSessionTargetInvalidationRefusal(expectedTarget);
        }

        return CreateSessionRefusal(expectedTarget.Alias);
    }

    private GuardianHostSupervisorTerminal CreateSessionTargetInvalidationRefusal(
        GuardianHostJobListTarget expectedTarget)
    {
        if (_sessionSource.TryGetJobListTargetInvalidation(
                expectedTarget,
                out var invalidation) &&
            invalidation.AppliesTo(expectedTarget))
        {
            var recovery = invalidation.RecoverySnapshot;
            return RecoveryTerminal(new PublicRecoveryError(
                PublicRecoveryDetailCode.BackendLostBeforeDispatch,
                retryable: true,
                recovery.RetryAfterMilliseconds,
                recovery.RecoveryPhase,
                recovery.RecoveryAttempt,
                new SessionReadyGate(expectedTarget.Alias)));
        }

        return SessionRecoveryUnknown();
    }

    private static GuardianHostSupervisorTerminal SessionRecoveryUnknown() =>
        RecoveryTerminal(new PublicRecoveryError(
            PublicRecoveryDetailCode.SessionRecoveryUnknown,
            retryable: false,
            retryAfterMilliseconds: null,
            recoveryPhase: null,
            recoveryAttempt: null,
            retryGate: null));

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
            Payload: OperationCompleted { Result: GuardianHostTextOperationResult result }
        } => new GuardianHostSupervisorTerminal(result.Text, isError: false),
        GuardianHostSuccessResponse
        {
            Payload: OperationCompleted { Result: InvokeBackgroundResult result }
        } => new GuardianHostSupervisorTerminal(
            $"[job {result.PublicJobId.Value} started]",
            isError: false),
        GuardianHostErrorResponse
        {
            Error.DetailCode: GuardianHostPrivateDetailCode.OutcomeUnknown
        } => OutcomeUnknown(),
        GuardianHostErrorResponse error => new GuardianHostSupervisorTerminal(
            $"private_host_error:{error.Error.DetailCode}",
            isError: true,
            auditDetailCode: "private_host_error"),
        _ => throw new GuardianHostClientException(
            GuardianHostClientFailureKind.ResponseCorrelationMismatch),
    };

    private static GuardianHostSupervisorTerminal RecoveryTerminal(
        PublicRecoveryError error) => new(
            Encoding.UTF8.GetString(PublicRecoveryCodec.Encode(error)),
            isError: true,
            auditDetailCode: PublicRecoveryCodec.DetailCode(error.DetailCode));

    private static GuardianToolResult ToToolResult(
        GuardianHostSupervisorTerminal terminal) =>
        new(terminal.Text, terminal.IsError);

    private static GuardianToolResult RecordNoDispatch(
        GuardianAuditCall auditCall,
        GuardianHostJobListTarget? target,
        GuardianHostSupervisorTerminal terminal,
        string fallbackDetailCode)
    {
        ArgumentNullException.ThrowIfNull(auditCall);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDetailCode);
        if (target is not null)
        {
            auditCall.RecordNotDispatched(
                target.AuditSession,
                terminal.AuditDetailCode ?? fallbackDetailCode,
                terminal.Text);
        }
        return ToToolResult(terminal);
    }

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
        if (result == GuardianLocalTerminalResult.Accepted)
        {
            call.AuditCall.RecordNotDispatched(
                call.Target.AuditSession,
                terminal.AuditDetailCode ?? "guardian_dispatch_not_started",
                terminal.Text);
            call.TerminalAvailable.TrySetResult(true);
            return;
        }
        if (result is GuardianLocalTerminalResult.TerminalAlreadyDecoded or
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
        call.AuditCall.RecordOutcomeUnknown(terminal.Text);
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

        if (_active is { } remaining)
            DisposeAttemptWatcherOwnership(remaining);
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

    private void DisposeAttemptWatcherOwnership(ActiveAttempt active)
    {
        if (!active.DisposeWatcherOwnership()) return;
        lock (_stateSync)
        {
            if (_ownedAttemptWatcherSets <= 0)
            {
                throw new InvalidOperationException(
                    "The attempt watcher-ownership count underflowed.");
            }
            _ownedAttemptWatcherSets--;
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

    private sealed class ActiveAttempt
    {
        private readonly CancellationTokenSource _preContainment;
        private readonly CancellationTokenSource _startupDeadline;
        private readonly CancellationTokenSource _containmentDeadline;
        private int _watcherOwnershipDisposed;

        internal ActiveAttempt(
            GuardianHostAttemptLease lease,
            IGuardianHostConnectedAttemptResources resources,
            CancellationToken lifetime)
        {
            Lease = lease;
            Resources = resources;
            _preContainment = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _startupDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _containmentDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        }

        internal GuardianHostAttemptLease Lease { get; }
        internal IGuardianHostConnectedAttemptResources Resources { get; }
        internal GuardianHostClient? Client { get; set; }
        internal PublicHostStateSnapshot? PrewriteLossHost { get; set; }
        internal CancellationToken PreContainmentToken => _preContainment.Token;
        internal CancellationToken StartupDeadlineToken => _startupDeadline.Token;
        internal CancellationToken ContainmentDeadlineToken => _containmentDeadline.Token;
        internal TaskCompletionSource<bool> Ready { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ContainmentStarted;

        internal void CancelPreContainment() => _preContainment.Cancel();

        internal void CancelStartupDeadline() => _startupDeadline.Cancel();

        internal void CancelContainmentDeadline() => _containmentDeadline.Cancel();

        internal bool DisposeWatcherOwnership()
        {
            if (Interlocked.Exchange(ref _watcherOwnershipDisposed, 1) != 0)
                return false;

            _preContainment.Cancel();
            _startupDeadline.Cancel();
            _containmentDeadline.Cancel();
            _preContainment.Dispose();
            _startupDeadline.Dispose();
            _containmentDeadline.Dispose();
            return true;
        }
    }

    private sealed class ActiveCall(
        ActiveAttempt attempt,
        GuardianHostJobListTarget target,
        GuardianCallDeliveryTracker<GuardianHostSupervisorTerminal> tracker,
        GuardianAuditCall auditCall,
        GuardianOutputRegistration? outputRegistration,
        GuardianJobRegistration? jobRegistration)
    {
        internal ActiveAttempt Attempt { get; } = attempt;
        internal GuardianHostJobListTarget Target { get; } = target;
        internal GuardianCallDeliveryTracker<GuardianHostSupervisorTerminal> Tracker { get; } = tracker;
        internal GuardianAuditCall AuditCall { get; } = auditCall ??
            throw new ArgumentNullException(nameof(auditCall));
        internal GuardianOutputRegistration? OutputRegistration { get; } = outputRegistration;
        internal GuardianJobRegistration? JobRegistration { get; } = jobRegistration;
        internal bool OutputResponseResolved { get; set; }
        internal bool JobResponseResolved { get; set; }
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

    private sealed class OutputAdmissionException : InvalidOperationException;

    private sealed class JobAdmissionException : InvalidOperationException;

    private sealed class JobLookupException : InvalidOperationException;

    private sealed class AuditAuthorizationException : InvalidOperationException;

    private sealed class BackgroundInvokeDispatch
    {
        private readonly CallId _callId;
        private readonly DispatchCapability _dispatchCapability;
        private readonly OutputCapability _outputCapability;
        private readonly string _script;
        private readonly bool _raw;
        private readonly GuardianHostInvokeRoute _route;

        internal BackgroundInvokeDispatch(
            CallId callId,
            DispatchCapability dispatchCapability,
            OutputCapability outputCapability,
            string script,
            bool raw,
            GuardianHostInvokeRoute route)
        {
            ArgumentNullException.ThrowIfNull(callId);
            ArgumentNullException.ThrowIfNull(dispatchCapability);
            ArgumentNullException.ThrowIfNull(outputCapability);
            GuardianHostDtoValidation.RequireScript(script, nameof(script));
            GuardianHostDtoValidation.RequireDefined(route, nameof(route));
            if (dispatchCapability.CallId != callId)
            {
                throw new ArgumentException(
                    "Dispatch capability call ID must match the operation call ID.",
                    nameof(dispatchCapability));
            }
            _callId = callId;
            _dispatchCapability = dispatchCapability;
            _outputCapability = outputCapability;
            _script = script;
            _raw = raw;
            _route = route;
        }

        internal InvokeBackgroundOperation CreateOperation(PublicJobId publicJobId) =>
            new(
                _callId,
                _dispatchCapability,
                _outputCapability,
                _script,
                _raw,
                _route,
                publicJobId);

        internal CallId CallId => _callId;
    }

    private sealed class JobControlDispatch
    {
        private readonly GuardianHostOperationKind _operationKind;
        private readonly CallId _callId;
        private readonly DispatchCapability _dispatchCapability;
        private readonly long _offset;

        internal JobControlDispatch(
            GuardianHostOperationKind operationKind,
            CallId callId,
            DispatchCapability dispatchCapability,
            OutputCapability? outputCapability,
            PublicJobId publicJobId,
            long offset)
        {
            if (operationKind is not (
                    GuardianHostOperationKind.JobStatus or
                    GuardianHostOperationKind.JobOutput or
                    GuardianHostOperationKind.JobKill))
            {
                throw new ArgumentOutOfRangeException(nameof(operationKind));
            }
            ArgumentNullException.ThrowIfNull(callId);
            ArgumentNullException.ThrowIfNull(dispatchCapability);
            ArgumentNullException.ThrowIfNull(publicJobId);
            if (dispatchCapability.CallId != callId)
            {
                throw new ArgumentException(
                    "Dispatch capability call ID must match the operation call ID.",
                    nameof(dispatchCapability));
            }
            if ((operationKind == GuardianHostOperationKind.JobOutput) !=
                (outputCapability is not null))
            {
                throw new ArgumentException(
                    "Only job output accepts an output capability.",
                    nameof(outputCapability));
            }
            if (operationKind == GuardianHostOperationKind.JobOutput)
                GuardianHostDtoValidation.RequireNonnegative(offset, nameof(offset));
            else if (offset != 0)
                throw new ArgumentOutOfRangeException(nameof(offset));

            _operationKind = operationKind;
            _callId = callId;
            _dispatchCapability = dispatchCapability;
            OutputCapability = outputCapability;
            PublicJobId = publicJobId;
            _offset = offset;
        }

        internal OutputCapability? OutputCapability { get; }

        internal PublicJobId PublicJobId { get; }

        internal CallId CallId => _callId;

        internal GuardianHostOperation CreateOperation(
            GuardianJobCapability capability)
        {
            if (capability.PublicJobId != PublicJobId)
            {
                throw new InvalidOperationException(
                    "The resolved job capability does not match the requested ID.");
            }

            return _operationKind switch
            {
                GuardianHostOperationKind.JobStatus => new JobStatusOperation(
                    _callId,
                    _dispatchCapability,
                    PublicJobId,
                    capability.JobCapability),
                GuardianHostOperationKind.JobOutput => new JobOutputOperation(
                    _callId,
                    _dispatchCapability,
                    OutputCapability!,
                    PublicJobId,
                    capability.JobCapability,
                    _offset),
                GuardianHostOperationKind.JobKill => new JobKillOperation(
                    _callId,
                    _dispatchCapability,
                    PublicJobId,
                    capability.JobCapability),
                _ => throw new InvalidOperationException(
                    "The job control operation kind is invalid."),
            };
        }
    }

    private readonly record struct DispatchAdmission(
        ActiveAttempt? Active,
        GuardianHostJobListTarget? Target,
        GuardianHostSupervisorTerminal? Terminal);
}
