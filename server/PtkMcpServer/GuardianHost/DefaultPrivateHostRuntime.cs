using System.Security.Cryptography;
using System.Text;
using PtkMcpServer.Sessions;
using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

/// <summary>
/// Creates the transitional in-process default session only after the exact
/// recovery manifest has been authenticated by the protocol server.
/// </summary>
internal interface IPrivateHostSessionFactory
{
    ValueTask<IPrivateSessionOperations> CreateAsync(
        PrivateHostInitialization initialization,
        RecoveryBinding defaultBinding,
        CancellationToken cancellationToken);
}

/// <summary>
/// Converts one guardian output capability into host-side execution capture
/// and transfers bounded text results that are not produced by the execution
/// capture path. Concrete event framing remains outside the session runtime.
/// </summary>
internal interface IPrivateHostOutputTransfer
{
    IExecutionOutputCaptureOwner CreateExecutionCapture(
        OperationRequest request);

    ValueTask TransferTextAsync(
        OperationRequest request,
        string text,
        CancellationToken cancellationToken);
}

internal enum DefaultPrivateHostRuntimeState
{
    Created,
    Initializing,
    Ready,
    Stopping,
    Stopped,
    Faulted,
}

/// <summary>
/// R4's default-session in-process runtime. It validates the frozen manifest,
/// binds the guardian's logical worker identity, consumes exact operation
/// authority, and reports the in-process dispatch boundary through the same
/// serialized event channel as protocol responses. It does not own audit,
/// public IDs, output handles, or MCP transport.
/// </summary>
internal sealed class DefaultPrivateHostRuntime : IPrivateHostRuntime
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly object _gate = new();
    private readonly PrivateHostServerIdentity _identity;
    private readonly IPrivateHostEventSink _eventSink;
    private readonly IPrivateHostSessionFactory _sessionFactory;
    private readonly IPrivateHostOutputTransfer _outputTransfer;
    private readonly Func<CapabilityToken> _createJobCapability;
    private readonly Func<long> _unixTimeMilliseconds;

    private IPrivateSessionOperations? _session;
    private RecoveryBinding? _binding;
    private WorkerGeneration? _workerGeneration;
    private WorkerBootId? _workerBootId;
    private DefaultPrivateHostRuntimeState _state;

    internal DefaultPrivateHostRuntime(
        PrivateHostServerIdentity identity,
        IPrivateHostEventSink eventSink,
        IPrivateHostSessionFactory sessionFactory,
        IPrivateHostOutputTransfer outputTransfer,
        Func<CapabilityToken>? createJobCapability = null,
        Func<long>? unixTimeMilliseconds = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        _sessionFactory = sessionFactory ??
            throw new ArgumentNullException(nameof(sessionFactory));
        _outputTransfer = outputTransfer ??
            throw new ArgumentNullException(nameof(outputTransfer));
        _createJobCapability = createJobCapability ?? CreateCapabilityToken;
        _unixTimeMilliseconds = unixTimeMilliseconds ??
            (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    internal DefaultPrivateHostRuntimeState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public async ValueTask InitializeAsync(
        PrivateHostInitialization initialization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialization);
        lock (_gate)
        {
            if (_state != DefaultPrivateHostRuntimeState.Created)
                throw new InvalidOperationException(
                    "The default private-host runtime is single-use.");
            _state = DefaultPrivateHostRuntimeState.Initializing;
        }

        IPrivateSessionOperations? created = null;
        try
        {
            var (binding, generation) = ValidateInitialization(initialization);
            cancellationToken.ThrowIfCancellationRequested();
            created = await _sessionFactory.CreateAsync(
                initialization,
                binding,
                cancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException(
                    "Private host session factory returned no session.");
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                if (_state != DefaultPrivateHostRuntimeState.Initializing)
                    throw new InvalidOperationException(
                        "Private host initialization state changed unexpectedly.");
                _binding = binding;
                _workerGeneration = generation;
                _session = created;
                created = null;
                _state = DefaultPrivateHostRuntimeState.Ready;
            }
        }
        catch
        {
            try
            {
                if (created is not null)
                    await created.ShutdownAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    created?.Dispose();
                }
                finally
                {
                    lock (_gate) _state = DefaultPrivateHostRuntimeState.Faulted;
                }
            }
            throw;
        }
    }

    public async ValueTask<PrivateHostOperationOutcome> ExecuteOperationAsync(
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Operation.Kind is
            GuardianHostOperationKind.SessionOpen or
            GuardianHostOperationKind.SessionClose or
            GuardianHostOperationKind.SessionRestart)
        {
            return await RefuseAsync(
                request,
                GuardianHostPrivateDetailCode.UnsupportedOperation,
                cancellationToken).ConfigureAwait(false);
        }

        var validation = ValidateAndBind(request, cancellationToken);
        if (validation.Error is { } error)
        {
            return await RefuseAsync(request, error, cancellationToken)
                .ConfigureAwait(false);
        }

        var session = validation.Session!;
        var worker = validation.Worker!;
        IExecutionOutputCaptureOwner? capture = null;
        try
        {
            SessionOperationAuthority authority;
            CapabilityToken? backgroundJobCapability = null;
            if (request.Operation is InvokeBackgroundOperation)
            {
                try
                {
                    backgroundJobCapability = _createJobCapability() ??
                        throw new InvalidOperationException(
                            "Private host job capability source returned no capability.");
                    authority = SessionOperationAuthority.CreateBackgroundInvoke(
                        request,
                        backgroundJobCapability);
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    return await RefuseAsync(
                        request,
                        GuardianHostPrivateDetailCode.CapabilityInvalid,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                authority = SessionOperationAuthority.Create(request);
            }

            if (request.Operation is
                InvokeForegroundOperation or InvokeBackgroundOperation)
            {
                try
                {
                    capture = _outputTransfer.CreateExecutionCapture(request) ??
                        throw new InvalidOperationException(
                            "Private host output transfer returned no capture owner.");
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    return await RefuseAsync(
                        request,
                        GuardianHostPrivateDetailCode.OutputCapabilityInvalid,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await WriteDeliveryAsync(
                request,
                worker,
                GuardianHostDeliveryState.WriteStarted,
                request.RequestId,
                cancellationToken).ConfigureAwait(false);

            var consumedCapture = capture;
            capture = null;
            PrivateHostOperationOutcome outcome;
            try
            {
                outcome = await DispatchAsync(
                    session,
                    authority,
                    request,
                    worker,
                    backgroundJobCapability,
                    consumedCapture,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (JobCapabilityException)
            {
                outcome = PrivateHostOperationOutcome.Failed(
                    GuardianHostPrivateDetailCode.JobCapabilityInvalid);
            }
            catch (SessionOperationDeadlineException)
            {
                outcome = PrivateHostOperationOutcome.Failed(
                    GuardianHostPrivateDetailCode.RequestDeadlineExpired);
            }

            await WriteDeliveryAsync(
                request,
                worker,
                GuardianHostDeliveryState.TerminalDecoded,
                request.RequestId,
                cancellationToken).ConfigureAwait(false);
            return outcome;
        }
        finally
        {
            capture?.Dispose();
        }
    }

    public async ValueTask ShutdownAsync(
        GuardianHostShutdown shutdown,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        IPrivateSessionOperations session;
        lock (_gate)
        {
            if (_state == DefaultPrivateHostRuntimeState.Stopped)
                return;
            if (_state != DefaultPrivateHostRuntimeState.Ready || _session is null)
                throw new InvalidOperationException(
                    "The default private-host runtime is not ready to stop.");
            _state = DefaultPrivateHostRuntimeState.Stopping;
            session = _session;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await session.ShutdownAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) _state = DefaultPrivateHostRuntimeState.Stopped;
        }
        catch
        {
            lock (_gate) _state = DefaultPrivateHostRuntimeState.Faulted;
            throw;
        }
        finally
        {
            session.Dispose();
            lock (_gate) _session = null;
        }
    }

    private RuntimeValidation ValidateAndBind(
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _unixTimeMilliseconds();
        if (request.Operation.DispatchCapability.ExpiresUnixTimeMilliseconds <= now)
            return RuntimeValidation.Failed(GuardianHostPrivateDetailCode.CapabilityInvalid);
        if (request.Operation.OutputCapability is { } output &&
            output.ExpiresUnixTimeMilliseconds <= now)
        {
            return RuntimeValidation.Failed(
                GuardianHostPrivateDetailCode.OutputCapabilityInvalid);
        }

        lock (_gate)
        {
            if (_state != DefaultPrivateHostRuntimeState.Ready ||
                _session is null ||
                _binding is null ||
                _workerGeneration is null)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.SessionFaulted);
            }
            if (request.SessionAlias != _binding.Alias)
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.SessionNotFound);
            if (request.SessionTransitionVersion != _binding.TransitionVersion)
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.ExpectedGenerationMismatch);
            if (request.Worker is null)
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.WorkerLost);
            if (request.Worker.Generation != _workerGeneration)
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.WorkerGenerationMismatch);
            if (request.Operation is GuardianHostGenerationOperation generation &&
                generation.ExpectedGeneration != 0 &&
                generation.ExpectedGeneration != _workerGeneration.Value)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.ExpectedGenerationMismatch);
            }
            if (_workerBootId is not null &&
                request.Worker.BootId != _workerBootId)
            {
                return RuntimeValidation.Failed(
                    GuardianHostPrivateDetailCode.WorkerBootMismatch);
            }

            _workerBootId ??= request.Worker.BootId;
            return RuntimeValidation.Succeeded(_session, request.Worker);
        }
    }

    private async ValueTask<PrivateHostOperationOutcome> RefuseAsync(
        OperationRequest request,
        GuardianHostPrivateDetailCode detailCode,
        CancellationToken cancellationToken)
    {
        if (request.Worker is { } worker)
        {
            await WriteDeliveryAsync(
                request,
                worker,
                GuardianHostDeliveryState.NotDispatched,
                workerRequestId: null,
                cancellationToken).ConfigureAwait(false);
        }
        return PrivateHostOperationOutcome.Failed(detailCode);
    }

    private async ValueTask<PrivateHostOperationOutcome> DispatchAsync(
        IPrivateSessionOperations session,
        SessionOperationAuthority authority,
        OperationRequest request,
        GuardianHostWorkerIdentity worker,
        CapabilityToken? backgroundJobCapability,
        IExecutionOutputCaptureOwner? capture,
        CancellationToken cancellationToken)
    {
        switch (request.Operation)
        {
            case InvokeForegroundOperation operation:
            {
                var text = await session.InvokeAsync(
                    authority,
                    operation,
                    cancellationToken,
                    RequireCapture(capture)).ConfigureAwait(false);
                return CompleteText(text, static value =>
                    new InvokeForegroundResult(value));
            }
            case InvokeBackgroundOperation operation:
                _ = await session.InvokeAsync(
                    authority,
                    operation,
                    cancellationToken,
                    snapshot => WriteJobLifecycleAsync(
                        request,
                        worker,
                        snapshot),
                    RequireCapture(capture)).ConfigureAwait(false);
                return PrivateHostOperationOutcome.Completed(
                    new InvokeBackgroundResult(
                        operation.PublicJobId,
                        backgroundJobCapability ?? throw new InvalidOperationException(
                            "Background job capability was not created.")));
            case JobListOperation operation:
                return CompleteText(
                    await session.JobAsync(
                        authority,
                        operation,
                        cancellationToken).ConfigureAwait(false),
                    static value => new JobListResult(value));
            case JobStatusOperation operation:
                return CompleteText(
                    await session.JobAsync(
                        authority,
                        operation,
                        cancellationToken).ConfigureAwait(false),
                    static value => new JobStatusResult(value));
            case JobOutputOperation operation:
            {
                var text = await session.JobAsync(
                    authority,
                    operation,
                    cancellationToken).ConfigureAwait(false);
                var outcome = CompleteText(text, static value =>
                    new JobOutputResult(value));
                if (outcome.Result is null)
                    return outcome;
                await _outputTransfer.TransferTextAsync(
                    request,
                    text,
                    cancellationToken).ConfigureAwait(false);
                return outcome;
            }
            case JobKillOperation operation:
                return CompleteText(
                    await session.JobAsync(
                        authority,
                        operation,
                        cancellationToken).ConfigureAwait(false),
                    static value => new PtkSharedContracts.JobKillResult(value));
            case ResetOperation operation:
                _ = await session.ResetAsync(
                    authority,
                    operation,
                    cancellationToken).ConfigureAwait(false);
                return PrivateHostOperationOutcome.Completed(
                    new ResetResult(
                        request.SessionAlias!,
                        PublicSessionState.Ready,
                        worker,
                        request.SessionTransitionVersion!,
                        readyForEffects: true,
                        warmStateLost: true,
                        BootstrapState.Restored));
            default:
                return PrivateHostOperationOutcome.Failed(
                    GuardianHostPrivateDetailCode.UnsupportedOperation);
        }
    }

    private ValueTask WriteDeliveryAsync(
        OperationRequest request,
        GuardianHostWorkerIdentity worker,
        GuardianHostDeliveryState state,
        PrivateRequestId? workerRequestId,
        CancellationToken cancellationToken) =>
        _eventSink.WriteEventAsync(
            sequence => new OperationDeliveryEvent(
                _identity.GuardianBootId,
                _identity.HostBootId,
                _identity.HostGeneration,
                sequence,
                request.RequestId,
                request.SessionAlias!,
                request.SessionTransitionVersion!,
                worker,
                request.OperationIdentity,
                request.Operation.DispatchCapability.Token,
                state,
                workerRequestId),
            cancellationToken);

    private Task WriteJobLifecycleAsync(
        OperationRequest request,
        GuardianHostWorkerIdentity worker,
        JobSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var operation = request.Operation as InvokeBackgroundOperation ??
            throw new InvalidOperationException(
                "A background job terminal is not bound to a background invocation.");
        if (snapshot.Id != operation.PublicJobId.Value || snapshot.Running)
        {
            throw new InvalidOperationException(
                "A background job terminal does not match its private invocation.");
        }

        var recovery = snapshot.OutputRecovery;
        var outputState = recovery?.State switch
        {
            OutputArtifactState.Available => GuardianHostOutputState.Sealed,
            OutputArtifactState.Incomplete => GuardianHostOutputState.SealedIncomplete,
            _ => GuardianHostOutputState.Unavailable,
        };
        var outputBytes = checked((int)(recovery?.Bytes ?? 0));
        var state = !snapshot.RootTerminationConfirmed
            ? GuardianHostJobState.Lost
            : snapshot.ExecutionOutcomeUnknown
                ? GuardianHostJobState.OutcomeUnknown
                : snapshot.KillRequested
                    ? GuardianHostJobState.Canceled
                    : snapshot.ExitCode == 0
                        ? GuardianHostJobState.Completed
                        : GuardianHostJobState.Failed;

        return _eventSink.WriteEventAsync(
            sequence => new JobLifecycleEvent(
                _identity.GuardianBootId,
                _identity.HostBootId,
                _identity.HostGeneration,
                sequence,
                requestId: null,
                request.SessionAlias!,
                request.SessionTransitionVersion!,
                worker,
                request.OperationIdentity ?? throw new InvalidOperationException(
                    "A background job terminal has no operation identity."),
                operation.PublicJobId,
                state,
                snapshot.ExitCode,
                outputState,
                outputBytes,
                outputDigest: null),
            CancellationToken.None).AsTask();
    }

    private static IExecutionOutputCaptureOwner RequireCapture(
        IExecutionOutputCaptureOwner? capture) =>
        capture ?? throw new InvalidOperationException(
            "An invocation has no execution output capture owner.");

    private static PrivateHostOperationOutcome CompleteText(
        string text,
        Func<string, GuardianHostOperationResult> createResult)
    {
        if (text is null)
        {
            return PrivateHostOperationOutcome.Failed(
                GuardianHostPrivateDetailCode.InvalidOperationResponse);
        }

        int encodedBytes;
        try
        {
            encodedBytes = StrictUtf8.GetByteCount(text);
        }
        catch (EncoderFallbackException)
        {
            return PrivateHostOperationOutcome.Failed(
                GuardianHostPrivateDetailCode.InvalidOperationResponse);
        }
        if (encodedBytes > ContractLimits.MaximumTextResultBytes)
        {
            return PrivateHostOperationOutcome.Failed(
                GuardianHostPrivateDetailCode.OperationResultTooLarge);
        }

        try
        {
            return PrivateHostOperationOutcome.Completed(createResult(text));
        }
        catch (ArgumentException)
        {
            return PrivateHostOperationOutcome.Failed(
                GuardianHostPrivateDetailCode.InvalidOperationResponse);
        }
    }

    private static (RecoveryBinding Binding, WorkerGeneration Generation)
        ValidateInitialization(PrivateHostInitialization initialization)
    {
        var manifest = initialization.Manifest;
        if (manifest.Bindings.Count != 1 ||
            manifest.WorkerGenerationHighWatermarks.Count != 1)
        {
            throw new InvalidDataException(
                "The R4 in-process runtime requires exactly one default binding.");
        }

        var binding = manifest.Bindings[0];
        var watermark = manifest.WorkerGenerationHighWatermarks[0];
        if (binding.Alias.Value != "default" ||
            binding.BindingKind != RecoveryBindingKind.Default ||
            binding.DesiredState != DesiredSessionState.Ready ||
            binding.TransitionVersion.Value <= 0 ||
            watermark.Alias != binding.Alias ||
            watermark.Generation.Value <= 0)
        {
            throw new InvalidDataException(
                "The R4 in-process runtime default binding is not ready and generation-bound.");
        }

        return (binding, new WorkerGeneration(watermark.Generation.Value));
    }

    private static CapabilityToken CreateCapabilityToken()
    {
        Span<byte> bytes = stackalloc byte[ContractLimits.CapabilityTokenBytes];
        RandomNumberGenerator.Fill(bytes);
        try
        {
            return new CapabilityToken(Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_'));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed record RuntimeValidation(
        IPrivateSessionOperations? Session,
        GuardianHostWorkerIdentity? Worker,
        GuardianHostPrivateDetailCode? Error)
    {
        internal static RuntimeValidation Succeeded(
            IPrivateSessionOperations session,
            GuardianHostWorkerIdentity worker) =>
            new(session, worker, Error: null);

        internal static RuntimeValidation Failed(
            GuardianHostPrivateDetailCode error) =>
            new(Session: null, Worker: null, error);
    }
}
