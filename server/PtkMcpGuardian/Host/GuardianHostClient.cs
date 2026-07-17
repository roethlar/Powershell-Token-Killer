using System.Security.Cryptography;
using PtkMcpGuardian.Lifecycle;
using PtkSharedContracts;

namespace PtkMcpGuardian.Host;

internal enum GuardianHostClientState
{
    Created,
    Initializing,
    Ready,
    Stopping,
    Faulted,
    Stopped,
}

internal enum GuardianHostClientFailureKind
{
    ContractMismatch,
    ProtocolViolation,
    UnexpectedEof,
    TransportFailure,
    RequestIdExhausted,
    DuplicateOrStaleResponse,
    UnknownResponse,
    ResponseCorrelationMismatch,
    EventSequenceInvalid,
    EventCorrelationMismatch,
    WriteAuthorityRejected,
    ShutdownRejected,
    ShutdownIncomplete,
    ResponseHandlerFailed,
    EventHandlerFailed,
    Canceled,
    Stopped,
}

internal sealed class GuardianHostClientException : IOException
{
    internal GuardianHostClientException(GuardianHostClientFailureKind detailKind)
        : base(MessageFor(detailKind))
    {
        if (!Enum.IsDefined(detailKind))
            throw new ArgumentOutOfRangeException(nameof(detailKind));

        DetailKind = detailKind;
    }

    internal GuardianHostClientFailureKind DetailKind { get; }

    internal string DetailCode => DetailKind switch
    {
        GuardianHostClientFailureKind.ContractMismatch => "host_contract_mismatch",
        GuardianHostClientFailureKind.ProtocolViolation => "host_protocol_violation",
        GuardianHostClientFailureKind.UnexpectedEof => "host_unexpected_eof",
        GuardianHostClientFailureKind.TransportFailure => "host_transport_failure",
        GuardianHostClientFailureKind.RequestIdExhausted => "private_request_id_exhausted",
        GuardianHostClientFailureKind.DuplicateOrStaleResponse => "host_duplicate_or_stale_response",
        GuardianHostClientFailureKind.UnknownResponse => "host_unknown_response",
        GuardianHostClientFailureKind.ResponseCorrelationMismatch => "host_response_correlation_mismatch",
        GuardianHostClientFailureKind.EventSequenceInvalid => "host_event_sequence_invalid",
        GuardianHostClientFailureKind.EventCorrelationMismatch => "host_event_correlation_mismatch",
        GuardianHostClientFailureKind.WriteAuthorityRejected => "host_write_authority_rejected",
        GuardianHostClientFailureKind.ShutdownRejected => "host_shutdown_rejected",
        GuardianHostClientFailureKind.ShutdownIncomplete => "host_shutdown_incomplete",
        GuardianHostClientFailureKind.ResponseHandlerFailed => "host_response_handler_failed",
        GuardianHostClientFailureKind.EventHandlerFailed => "host_event_handler_failed",
        GuardianHostClientFailureKind.Canceled => "host_client_canceled",
        GuardianHostClientFailureKind.Stopped => "host_client_stopped",
        _ => throw new InvalidOperationException("Unknown private-host client failure kind."),
    };

    private static string MessageFor(GuardianHostClientFailureKind detailKind) => detailKind switch
    {
        GuardianHostClientFailureKind.ContractMismatch =>
            "The private host did not match the guardian's pinned contract.",
        GuardianHostClientFailureKind.ProtocolViolation =>
            "The private host violated its frozen protocol.",
        GuardianHostClientFailureKind.UnexpectedEof =>
            "The private host channel ended unexpectedly.",
        GuardianHostClientFailureKind.TransportFailure =>
            "The private host transport failed.",
        GuardianHostClientFailureKind.RequestIdExhausted =>
            "The guardian private request-ID sequence is exhausted.",
        GuardianHostClientFailureKind.DuplicateOrStaleResponse =>
            "The private host returned a duplicate or stale response.",
        GuardianHostClientFailureKind.UnknownResponse =>
            "The private host returned an unknown response.",
        GuardianHostClientFailureKind.ResponseCorrelationMismatch =>
            "The private host response did not match its request.",
        GuardianHostClientFailureKind.EventSequenceInvalid =>
            "The private host event sequence was duplicate or stale.",
        GuardianHostClientFailureKind.EventCorrelationMismatch =>
            "The private host event did not match its request or control transition.",
        GuardianHostClientFailureKind.WriteAuthorityRejected =>
            "The private host generation no longer permits writes.",
        GuardianHostClientFailureKind.ShutdownRejected =>
            "The private host rejected the shutdown request.",
        GuardianHostClientFailureKind.ShutdownIncomplete =>
            "The private host did not complete shutdown before its deadline.",
        GuardianHostClientFailureKind.ResponseHandlerFailed =>
            "The private host response handler failed.",
        GuardianHostClientFailureKind.EventHandlerFailed =>
            "The private host event handler failed.",
        GuardianHostClientFailureKind.Canceled =>
            "The private host client operation was canceled.",
        GuardianHostClientFailureKind.Stopped =>
            "The private host client is stopped.",
        _ => throw new ArgumentOutOfRangeException(nameof(detailKind)),
    };
}

internal sealed class GuardianHostClientPins
{
    internal GuardianHostClientPins(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        int hostProcessId,
        Sha256Digest hostExecutableDigest,
        Sha256Digest hostBuildDigest,
        Sha256Digest publicContractDigest,
        Sha256Digest configurationDigest,
        Sha256Digest catalogDigest,
        Sha256Digest packageManifestDigest)
    {
        GuardianBootId = guardianBootId ?? throw new ArgumentNullException(nameof(guardianBootId));
        HostBootId = hostBootId ?? throw new ArgumentNullException(nameof(hostBootId));
        HostGeneration = hostGeneration ?? throw new ArgumentNullException(nameof(hostGeneration));
        if (hostProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(hostProcessId));
        HostProcessId = hostProcessId;
        HostExecutableDigest = hostExecutableDigest ??
            throw new ArgumentNullException(nameof(hostExecutableDigest));
        HostBuildDigest = hostBuildDigest ??
            throw new ArgumentNullException(nameof(hostBuildDigest));
        PublicContractDigest = publicContractDigest ??
            throw new ArgumentNullException(nameof(publicContractDigest));
        ConfigurationDigest = configurationDigest ??
            throw new ArgumentNullException(nameof(configurationDigest));
        CatalogDigest = catalogDigest ?? throw new ArgumentNullException(nameof(catalogDigest));
        PackageManifestDigest = packageManifestDigest ??
            throw new ArgumentNullException(nameof(packageManifestDigest));
    }

    internal GuardianBootId GuardianBootId { get; }
    internal HostBootId HostBootId { get; }
    internal HostGeneration HostGeneration { get; }
    internal int HostProcessId { get; }
    internal Sha256Digest HostExecutableDigest { get; }
    internal Sha256Digest HostBuildDigest { get; }
    internal Sha256Digest PublicContractDigest { get; }
    internal Sha256Digest ConfigurationDigest { get; }
    internal Sha256Digest CatalogDigest { get; }
    internal Sha256Digest PackageManifestDigest { get; }
}

/// <summary>
/// One unwired guardian-side private-host generation. The caller supplies
/// already-contained transport streams and the harness-global request-ID
/// allocator. This type never opens stdio, launches a process, or executes a
/// tool; it owns only typed protocol lifecycle and correlation.
/// </summary>
internal sealed class GuardianHostClient : IAsyncDisposable
{
    internal const int MaximumOutstandingRequests = 64;
    internal const int MaximumUnacknowledgedControlEvents = 64;

    private readonly object _sync = new();
    private readonly GuardianHostClientPins _pins;
    private readonly IPrivateRequestIdAllocator _requestIds;
    private readonly TimeProvider _timeProvider;
    private readonly GuardianHostProtocolReader _reader;
    private readonly GuardianHostProtocolWriter _writer;
    private readonly Func<ManifestId> _manifestIdFactory;
    private readonly Func<IDisposable?> _writeLeaseFactory;
    private readonly Func<GuardianHostEvent, IDisposable?> _eventLeaseFactory;
    private readonly Func<GuardianHostMessage, GuardianHostResponse, IDisposable?>
        _responseLeaseFactory;
    private readonly Func<GuardianHostEvent, CancellationToken, ValueTask> _eventHandler;
    private readonly Func<GuardianHostMessage, GuardianHostResponse, CancellationToken, ValueTask>
        _responseHandler;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operationalWriteGate = new(1, 1);
    private readonly AsyncLocal<bool> _dispatchingInbound = new();
    private readonly Dictionary<long, PendingRequest> _pending = [];
    private readonly Dictionary<long, GuardianHostEvent> _unacknowledgedControlEvents = [];
    private readonly TaskCompletionSource<GuardianHostClientException> _fatal = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _initializationCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private GuardianHostClientState _state = GuardianHostClientState.Created;
    private HostBootId? _hostBootId;
    private GuardianHostClientException? _failure;
    private Task _readerTask = Task.CompletedTask;
    private Task? _disposeTask;
    private long _lastGuardianRequestId;
    private long _lastHostEventSequence;
    private int _initializeStarted;
    private int _disposed;
    private bool _shutdownAccepted;
    private bool _shutdownEofObserved;

    internal GuardianHostClient(
        Stream requestStream,
        Stream eventStream,
        GuardianHostClientPins pins,
        IPrivateRequestIdAllocator requestIds,
        TimeProvider timeProvider,
        Func<IDisposable?> writeLeaseFactory,
        Func<GuardianHostEvent, IDisposable?> eventLeaseFactory,
        Func<GuardianHostMessage, GuardianHostResponse, IDisposable?> responseLeaseFactory,
        Func<GuardianHostEvent, CancellationToken, ValueTask> eventHandler,
        Func<GuardianHostMessage, GuardianHostResponse, CancellationToken, ValueTask>
            responseHandler,
        Func<ManifestId>? manifestIdFactory = null)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(eventStream);
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _requestIds = requestIds ?? throw new ArgumentNullException(nameof(requestIds));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _writeLeaseFactory = writeLeaseFactory ??
            throw new ArgumentNullException(nameof(writeLeaseFactory));
        _eventLeaseFactory = eventLeaseFactory ??
            throw new ArgumentNullException(nameof(eventLeaseFactory));
        _responseLeaseFactory = responseLeaseFactory ??
            throw new ArgumentNullException(nameof(responseLeaseFactory));
        _reader = new GuardianHostProtocolReader(eventStream, GuardianHostPeer.Host);
        _writer = new GuardianHostProtocolWriter(requestStream, GuardianHostPeer.Guardian);
        _eventHandler = eventHandler ?? throw new ArgumentNullException(nameof(eventHandler));
        _responseHandler = responseHandler ?? throw new ArgumentNullException(nameof(responseHandler));
        _manifestIdFactory = manifestIdFactory ??
            (() => new ManifestId(Guid.NewGuid()));
    }

    internal GuardianHostClientState State
    {
        get { lock (_sync) return _state; }
    }

    internal HostBootId? HostBootId
    {
        get { lock (_sync) return _hostBootId; }
    }

    internal int OutstandingRequestCount
    {
        get { lock (_sync) return _pending.Count; }
    }

    internal long LastHostEventSequence
    {
        get { lock (_sync) return _lastHostEventSequence; }
    }

    internal Task<GuardianHostClientException> Fatal => _fatal.Task;

    internal Task ReaderCompletion
    {
        get { lock (_sync) return _readerTask; }
    }

    internal Task InitializeAsync(
        RecoveryManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (Interlocked.CompareExchange(ref _initializeStarted, 1, 0) != 0)
            throw new InvalidOperationException("A private-host client can initialize only once.");

        return InitializeTrackedAsync(manifest, cancellationToken);
    }

    private async Task InitializeTrackedAsync(
        RecoveryManifest manifest,
        CancellationToken cancellationToken)
    {
        try
        {
            await InitializeCoreAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _initializationCompletion.TrySetResult();
        }
    }

    private async Task InitializeCoreAsync(
        RecoveryManifest manifest,
        CancellationToken cancellationToken)
    {

        lock (_sync)
        {
            ThrowIfDisposedLocked();
            if (_state != GuardianHostClientState.Created)
                throw new InvalidOperationException("The private-host client cannot initialize from its current state.");
            _state = GuardianHostClientState.Initializing;
        }

        byte[]? manifestBytes = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            ValidateManifest(manifest);
            manifestBytes = RecoveryManifestCodec.Encode(manifest);
            var manifestDigest = Sha256Digest.Compute(manifestBytes);
            var manifestId = _manifestIdFactory() ??
                throw new InvalidOperationException("The manifest-ID factory returned null.");

            var hello = await ReadRequiredAsync<GuardianHostHello>(linked.Token)
                .ConfigureAwait(false);
            ValidateHello(hello);
            lock (_sync)
            {
                if (_state != GuardianHostClientState.Initializing)
                    throw new GuardianHostClientException(GuardianHostClientFailureKind.Stopped);
                _hostBootId = _pins.HostBootId;
            }

            var initializeId = NextRequestId();
            await WriteAsync(new GuardianHostInitialize(
                _pins.GuardianBootId,
                hello.HostBootId,
                _pins.HostGeneration,
                initializeId,
                _pins.HostExecutableDigest,
                _pins.HostBuildDigest,
                _pins.PublicContractDigest,
                _pins.ConfigurationDigest,
                _pins.PackageManifestDigest), linked.Token).ConfigureAwait(false);

            var headerId = NextRequestId();
            var header = new ManifestHeaderRequest(
                _pins.GuardianBootId,
                hello.HostBootId,
                _pins.HostGeneration,
                headerId,
                manifestId,
                manifestBytes.Length,
                manifestDigest,
                manifest.Bindings.Count,
                manifest.Templates.Count);
            await WriteAsync(header, linked.Token).ConfigureAwait(false);
            var headerAccepted = await ReadSuccessAsync<ManifestHeaderAccepted>(
                headerId,
                linked.Token).ConfigureAwait(false);
            if (headerAccepted.ManifestId != manifestId ||
                headerAccepted.NextChunkIndex != 0 ||
                headerAccepted.NextOffset != 0)
            {
                throw new GuardianHostClientException(
                    GuardianHostClientFailureKind.ResponseCorrelationMismatch);
            }

            var offset = 0;
            var chunkIndex = 0;
            while (offset < manifestBytes.Length)
            {
                var chunkLength = Math.Min(
                    ContractLimits.MaximumManifestChunkBytes,
                    manifestBytes.Length - offset);
                var chunkId = NextRequestId();
                using (var chunk = new ManifestChunkRequest(
                    _pins.GuardianBootId,
                    hello.HostBootId,
                    _pins.HostGeneration,
                    chunkId,
                    manifestId,
                    chunkIndex,
                    manifestBytes.AsMemory(offset, chunkLength)))
                {
                    await WriteAsync(chunk, linked.Token).ConfigureAwait(false);
                }
                var chunkAccepted = await ReadSuccessAsync<ManifestChunkAccepted>(
                    chunkId,
                    linked.Token).ConfigureAwait(false);
                var nextOffset = checked(offset + chunkLength);
                if (chunkAccepted.ManifestId != manifestId ||
                    chunkAccepted.ChunkIndex != chunkIndex ||
                    chunkAccepted.NextChunkIndex != chunkIndex + 1 ||
                    chunkAccepted.NextOffset != nextOffset)
                {
                    throw new GuardianHostClientException(
                        GuardianHostClientFailureKind.ResponseCorrelationMismatch);
                }

                offset = nextOffset;
                chunkIndex++;
            }

            if (chunkIndex != header.ChunkCount)
            {
                throw new GuardianHostClientException(
                    GuardianHostClientFailureKind.ProtocolViolation);
            }

            var sealId = NextRequestId();
            var seal = new ManifestSealRequest(
                _pins.GuardianBootId,
                hello.HostBootId,
                _pins.HostGeneration,
                sealId,
                manifestId,
                manifestBytes.Length,
                manifestDigest);
            await WriteAsync(seal, linked.Token).ConfigureAwait(false);
            var sealedManifest = await ReadSuccessAsync<ManifestSealed>(
                sealId,
                linked.Token).ConfigureAwait(false);
            if (sealedManifest.ManifestId != manifestId ||
                sealedManifest.ManifestDigest != manifestDigest ||
                sealedManifest.TotalBytes != manifestBytes.Length)
            {
                throw new GuardianHostClientException(
                    GuardianHostClientFailureKind.ResponseCorrelationMismatch);
            }

            var ready = await ReadRequiredAsync<GuardianHostReady>(linked.Token)
                .ConfigureAwait(false);
            ValidateIdentity(ready);
            if (ready.InitializeRequestId != initializeId ||
                ready.ManifestId != manifestId ||
                ready.ManifestDigest != manifestDigest ||
                ready.HostPid != _pins.HostProcessId)
            {
                throw new GuardianHostClientException(
                    GuardianHostClientFailureKind.ResponseCorrelationMismatch);
            }

            lock (_sync)
            {
                if (_state != GuardianHostClientState.Initializing)
                    throw new GuardianHostClientException(GuardianHostClientFailureKind.Stopped);
                _state = GuardianHostClientState.Ready;
                _readerTask = ReadOperationalFramesAsync();
            }
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            var failure = Fault(NormalizeFailure(exception));
            throw failure;
        }
        finally
        {
            if (manifestBytes is not null)
                CryptographicOperations.ZeroMemory(manifestBytes);
        }
    }

    internal async Task<GuardianHostResponse> SendRequestAsync(
        Func<GuardianBootId, HostBootId, HostGeneration, PrivateRequestId, GuardianHostRequest>
            requestFactory,
        Action? onWriteStarting = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        if (_dispatchingInbound.Value)
            throw new InvalidOperationException(
                "An ordered event handler cannot send a request before it returns.");
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            await _operationalWriteGate.WaitAsync(admission.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            lock (_sync) throw CurrentStateExceptionLocked();
        }

        PendingRequest pending;
        var writeAttempted = false;
        try
        {
            using var writeLease = AcquireWriteLease();
            cancellationToken.ThrowIfCancellationRequested();
            var hostBootId = RequireReadyHost();
            var requestId = NextRequestId();
            var request = requestFactory(
                _pins.GuardianBootId,
                hostBootId,
                _pins.HostGeneration,
                requestId) ?? throw new InvalidOperationException("The request factory returned null.");
            ValidateOutboundRequest(request, requestId, hostBootId);
            pending = new PendingRequest(request);

            GuardianHostEvent? controlEvent;
            lock (_sync)
            {
                RequireRequestCapacityLocked(hostBootId, requestId);
                controlEvent = ValidateControlEventLocked(request);
            }
            using var controlLease = controlEvent is null
                ? null
                : AcquireEventLease(controlEvent);

            ValueTask write;
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireRequestCapacityLocked(hostBootId, requestId);
                if (controlEvent is not null &&
                    !ReferenceEquals(ValidateControlEventLocked(request), controlEvent))
                    throw new InvalidOperationException("The private control event changed before dispatch.");
                onWriteStarting?.Invoke();
                ClaimControlEventLocked(controlEvent);
                _pending.Add(requestId.Value, pending);
                writeAttempted = true;
                write = _writer.WriteAsync(request, _lifetime.Token);
            }
            await write.ConfigureAwait(false);
        }
        catch (GuardianHostClientException exception) when (
            !writeAttempted &&
            exception.DetailKind == GuardianHostClientFailureKind.RequestIdExhausted)
        {
            var failure = Fault(exception);
            throw failure;
        }
        catch (Exception exception) when (
            !writeAttempted && !IsFatalRuntimeException(exception))
        {
            throw;
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            var failure = Fault(NormalizeWriteFailure(exception));
            throw failure;
        }
        finally
        {
            _operationalWriteGate.Release();
        }

        return await pending.Completion.Task.ConfigureAwait(false);
    }

    internal async ValueTask<bool> TrySendCancelAsync(
        PrivateRequestId targetRequestId,
        GuardianHostCancelReason reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetRequestId);
        if (_dispatchingInbound.Value)
            throw new InvalidOperationException(
                "An ordered event handler cannot send a cancellation before it returns.");
        var hostBootId = RequireReadyHost();
        lock (_sync)
        {
            if (!_pending.TryGetValue(targetRequestId.Value, out var target) ||
                target.Message is not OperationRequest ||
                target.TerminalCandidateReserved)
            {
                return false;
            }
        }

        using var admission = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            await _operationalWriteGate.WaitAsync(admission.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            lock (_sync) throw CurrentStateExceptionLocked();
        }
        var writeAttempted = false;
        try
        {
            using var writeLease = AcquireWriteLease();
            cancellationToken.ThrowIfCancellationRequested();
            var cancelId = NextRequestId();
            var cancel = new GuardianHostCancel(
                _pins.GuardianBootId,
                hostBootId,
                _pins.HostGeneration,
                cancelId,
                targetRequestId,
                reason);
            ValueTask write;
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireReadyLocked(hostBootId);
                if (!_pending.TryGetValue(targetRequestId.Value, out var target) ||
                    target.Message is not OperationRequest ||
                    target.TerminalCandidateReserved)
                {
                    return false;
                }
                writeAttempted = true;
                write = _writer.WriteAsync(cancel, _lifetime.Token);
            }
            await write.ConfigureAwait(false);
            return true;
        }
        catch (GuardianHostClientException exception) when (
            !writeAttempted &&
            exception.DetailKind == GuardianHostClientFailureKind.RequestIdExhausted)
        {
            var failure = Fault(exception);
            throw failure;
        }
        catch (Exception exception) when (
            !writeAttempted && !IsFatalRuntimeException(exception))
        {
            throw;
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            var failure = Fault(NormalizeWriteFailure(exception));
            throw failure;
        }
        finally
        {
            _operationalWriteGate.Release();
        }
    }

    internal async Task ShutdownAsync(
        long deadlineUnixTimeMilliseconds,
        GuardianHostShutdownReason reason,
        CancellationToken cancellationToken = default)
    {
        if (_dispatchingInbound.Value)
            throw new InvalidOperationException(
                "An ordered inbound handler cannot shut down its client before it returns.");
        if (deadlineUnixTimeMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(deadlineUnixTimeMilliseconds));
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (deadlineUnixTimeMilliseconds <= now)
            throw new ArgumentOutOfRangeException(nameof(deadlineUnixTimeMilliseconds));
        var remainingMilliseconds = deadlineUnixTimeMilliseconds - now;
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(remainingMilliseconds),
            _timeProvider);
        using var shutdownLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token,
            deadline.Token);
        cancellationToken.ThrowIfCancellationRequested();
        HostBootId hostBootId;
        lock (_sync)
        {
            if (_state != GuardianHostClientState.Ready || _hostBootId is null)
                throw CurrentStateExceptionLocked();
            hostBootId = _hostBootId;
            _state = GuardianHostClientState.Stopping;
        }
        try
        {
            await _operationalWriteGate.WaitAsync(shutdownLifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            lock (_sync) throw CurrentStateExceptionLocked();
        }
        catch (OperationCanceledException)
        {
            var failure = Fault(new GuardianHostClientException(
                GuardianHostClientFailureKind.ShutdownIncomplete));
            throw failure;
        }

        PendingRequest pending;
        var writeAttempted = false;
        try
        {
            using var writeLease = AcquireWriteLease();
            shutdownLifetime.Token.ThrowIfCancellationRequested();
            var requestId = NextRequestId();
            var shutdown = new GuardianHostShutdown(
                _pins.GuardianBootId,
                hostBootId,
                _pins.HostGeneration,
                requestId,
                deadlineUnixTimeMilliseconds,
                reason);
            pending = new PendingRequest(shutdown);

            ValueTask write;
            lock (_sync)
            {
                shutdownLifetime.Token.ThrowIfCancellationRequested();
                RequireStoppingLocked(hostBootId);
                if (_pending.ContainsKey(requestId.Value))
                    throw new InvalidOperationException("The private request ID is already registered.");
                _pending.Add(requestId.Value, pending);
                writeAttempted = true;
                write = _writer.WriteAsync(shutdown, shutdownLifetime.Token);
            }
            await write.ConfigureAwait(false);
        }
        catch (GuardianHostClientException exception) when (
            !writeAttempted &&
            exception.DetailKind == GuardianHostClientFailureKind.RequestIdExhausted)
        {
            var failure = Fault(exception);
            throw failure;
        }
        catch (OperationCanceledException) when (
            !_lifetime.IsCancellationRequested)
        {
            var failure = Fault(new GuardianHostClientException(
                GuardianHostClientFailureKind.ShutdownIncomplete));
            throw failure;
        }
        catch (Exception exception) when (
            !writeAttempted && !IsFatalRuntimeException(exception))
        {
            var failure = Fault(NormalizeWriteFailure(exception));
            throw failure;
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            var failure = Fault(NormalizeWriteFailure(exception));
            throw failure;
        }
        finally
        {
            _operationalWriteGate.Release();
        }

        try
        {
            var response = await pending.Completion.Task.WaitAsync(shutdownLifetime.Token)
                .ConfigureAwait(false);
            if (response is not GuardianHostSuccessResponse
                {
                    Payload: ShutdownAccepted,
                })
            {
                var rejected = Fault(new GuardianHostClientException(
                    GuardianHostClientFailureKind.ShutdownRejected));
                throw rejected;
            }
            await ReaderCompletion.WaitAsync(shutdownLifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            lock (_sync) throw CurrentStateExceptionLocked();
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            var incomplete = Fault(new GuardianHostClientException(
                GuardianHostClientFailureKind.ShutdownIncomplete));
            throw incomplete;
        }

        lock (_sync)
        {
            if (_state == GuardianHostClientState.Stopped &&
                _shutdownAccepted &&
                _shutdownEofObserved)
                return;
            throw _failure ?? new GuardianHostClientException(
                GuardianHostClientFailureKind.ShutdownIncomplete);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_dispatchingInbound.Value)
            throw new InvalidOperationException(
                "An ordered event handler cannot dispose its client before it returns.");

        TaskCompletionSource? owner = null;
        Task disposeTask;
        lock (_sync)
        {
            if (_disposeTask is null)
            {
                owner = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = owner.Task;
                Volatile.Write(ref _disposed, 1);
                _state = GuardianHostClientState.Stopped;
            }
            disposeTask = _disposeTask;
        }
        if (owner is not null)
            _ = DisposeAndSignalAsync(owner);
        return new ValueTask(disposeTask);
    }

    private async Task DisposeAndSignalAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {

        PendingRequest[] pending;
        Task readerTask;
        GuardianHostClientException stopped;
        lock (_sync)
        {
            _state = GuardianHostClientState.Stopped;
            stopped = new GuardianHostClientException(GuardianHostClientFailureKind.Stopped);
            pending = RemoveUnreservedPendingLocked();
            _unacknowledgedControlEvents.Clear();
            readerTask = _readerTask;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var request in pending)
            request.Completion.TrySetException(stopped);
        if (Volatile.Read(ref _initializeStarted) != 0)
            await _initializationCompletion.Task.ConfigureAwait(false);

        try
        {
            await readerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (GuardianHostClientException)
        {
            // Fatal already owns the bounded generation terminal.
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            // Stop owns all bounded reader/dispatcher teardown failures.
        }
        await _operationalWriteGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _operationalWriteGate.Release();
        _lifetime.Dispose();
    }

    private async Task ReadOperationalFramesAsync()
    {
        try
        {
            while (true)
            {
                var message = await _reader.ReadAsync(_lifetime.Token).ConfigureAwait(false);
                if (message is null)
                {
                    PendingRequest[] stoppedPending = [];
                    GuardianHostClientException? stopped = null;
                    lock (_sync)
                    {
                        if (_state == GuardianHostClientState.Stopping && _shutdownAccepted)
                        {
                            _state = GuardianHostClientState.Stopped;
                            _shutdownEofObserved = true;
                            stoppedPending = _pending.Values.ToArray();
                            _pending.Clear();
                            _unacknowledgedControlEvents.Clear();
                            stopped = new GuardianHostClientException(
                                GuardianHostClientFailureKind.Stopped);
                        }
                    }
                    if (stopped is not null)
                    {
                        foreach (var request in stoppedPending)
                            request.Completion.TrySetException(stopped);
                        return;
                    }
                    throw new GuardianHostClientException(
                        GuardianHostClientFailureKind.UnexpectedEof);
                }
                using (message as IDisposable)
                {
                    ValidateIdentity(message);
                    lock (_sync)
                    {
                        if (_state == GuardianHostClientState.Stopping && _shutdownAccepted)
                            throw new GuardianHostClientException(
                                GuardianHostClientFailureKind.ProtocolViolation);
                    }

                    switch (message)
                    {
                        case GuardianHostResponse response:
                            await CompleteResponseAsync(response).ConfigureAwait(false);
                            break;
                        case GuardianHostEvent hostEvent:
                            using (AcquireEventLease(hostEvent))
                            {
                                ValidateAndReserveEvent(hostEvent);
                                lock (_sync)
                                {
                                    if (!IsInboundActiveLocked())
                                        throw CurrentStateExceptionLocked();
                                }
                                _dispatchingInbound.Value = true;
                                try
                                {
                                    await _eventHandler(hostEvent, _lifetime.Token)
                                        .ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (
                                    _lifetime.IsCancellationRequested)
                                {
                                    throw;
                                }
                                catch (Exception exception) when (!IsFatalRuntimeException(exception))
                                {
                                    throw new GuardianHostClientException(
                                        GuardianHostClientFailureKind.EventHandlerFailed);
                                }
                                finally
                                {
                                    _dispatchingInbound.Value = false;
                                }
                            }
                            break;
                        default:
                            throw new GuardianHostClientException(
                                GuardianHostClientFailureKind.ProtocolViolation);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            var failure = Fault(NormalizeFailure(exception));
            throw failure;
        }
    }

    private IDisposable AcquireEventLease(GuardianHostEvent hostEvent)
    {
        IDisposable? lease;
        try
        {
            lease = _eventLeaseFactory(hostEvent);
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            throw new GuardianHostClientException(
                GuardianHostClientFailureKind.EventCorrelationMismatch);
        }
        if (lease is null)
            throw new GuardianHostClientException(
                GuardianHostClientFailureKind.EventCorrelationMismatch);
        return lease;
    }

    private IDisposable AcquireWriteLease()
    {
        IDisposable? lease;
        try
        {
            lease = _writeLeaseFactory();
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            throw new GuardianHostClientException(
                GuardianHostClientFailureKind.WriteAuthorityRejected);
        }
        if (lease is null)
            throw new GuardianHostClientException(
                GuardianHostClientFailureKind.WriteAuthorityRejected);
        return lease;
    }

    private async Task CompleteResponseAsync(GuardianHostResponse response)
    {
        PendingRequest pending;
        lock (_sync)
        {
            if (!_pending.TryGetValue(response.RequestId.Value, out pending!))
            {
                var kind = response.RequestId.Value <= _lastGuardianRequestId
                    ? GuardianHostClientFailureKind.DuplicateOrStaleResponse
                    : GuardianHostClientFailureKind.UnknownResponse;
                throw new GuardianHostClientException(kind);
            }
            if (!ResponseMatchesRequest(response, pending.Message))
            {
                throw new GuardianHostClientException(
                    GuardianHostClientFailureKind.ResponseCorrelationMismatch);
            }
            RequireInboundActiveLocked(response.HostBootId);
            if (pending.TerminalCandidateReserved)
            {
                throw new GuardianHostClientException(
                    GuardianHostClientFailureKind.DuplicateOrStaleResponse);
            }
            pending.TerminalCandidateReserved = true;
        }

        IDisposable responseLease;
        try
        {
            responseLease = AcquireResponseLease(pending.Message, response);
        }
        catch (GuardianHostClientException exception)
        {
            lock (_sync)
            {
                if (_pending.TryGetValue(response.RequestId.Value, out var current) &&
                    ReferenceEquals(current, pending))
                    _pending.Remove(response.RequestId.Value);
            }
            var failure = Fault(exception);
            pending.Completion.TrySetException(failure);
            throw failure;
        }

        using (responseLease)
        {
            lock (_sync)
            {
                if (!_pending.Remove(response.RequestId.Value, out var current) ||
                    !ReferenceEquals(current, pending) ||
                    !pending.TerminalCandidateReserved)
                {
                    throw new GuardianHostClientException(
                        GuardianHostClientFailureKind.DuplicateOrStaleResponse);
                }
                if (pending.Message is GuardianHostShutdown &&
                    response is GuardianHostSuccessResponse { Payload: ShutdownAccepted })
                    _shutdownAccepted = true;
            }

            _dispatchingInbound.Value = true;
            try
            {
                await _responseHandler(pending.Message, response, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatalRuntimeException(exception))
            {
                var failure = Fault(new GuardianHostClientException(
                    GuardianHostClientFailureKind.ResponseHandlerFailed));
                pending.Completion.TrySetException(failure);
                throw failure;
            }
            finally
            {
                _dispatchingInbound.Value = false;
            }

            if (pending.Message is GuardianHostShutdown &&
                response is not GuardianHostSuccessResponse { Payload: ShutdownAccepted })
            {
                var failure = Fault(new GuardianHostClientException(
                    GuardianHostClientFailureKind.ShutdownRejected));
                pending.Completion.TrySetException(failure);
                throw failure;
            }

            pending.Completion.TrySetResult(response);
        }
    }

    private IDisposable AcquireResponseLease(
        GuardianHostMessage request,
        GuardianHostResponse response)
    {
        IDisposable? lease;
        try
        {
            lease = _responseLeaseFactory(request, response);
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            throw new GuardianHostClientException(
                GuardianHostClientFailureKind.ResponseCorrelationMismatch);
        }
        if (lease is null)
            throw new GuardianHostClientException(
                GuardianHostClientFailureKind.ResponseCorrelationMismatch);
        return lease;
    }

    private void ValidateAndReserveEvent(GuardianHostEvent hostEvent)
    {
        lock (_sync)
        {
            RequireInboundActiveLocked(hostEvent.HostBootId);
            if (hostEvent.EventSequence.Value <= _lastHostEventSequence)
            {
                throw new GuardianHostClientException(
                    GuardianHostClientFailureKind.EventSequenceInvalid);
            }

            if (hostEvent.RequestId is { } requestId)
            {
                if (!_pending.TryGetValue(requestId.Value, out var pending) ||
                    pending.Message is not OperationRequest request ||
                    !EventMatchesRequest(hostEvent, request))
                {
                    throw new GuardianHostClientException(
                        GuardianHostClientFailureKind.EventCorrelationMismatch);
                }
            }

            if (IsControlEvent(hostEvent.EventType))
            {
                if (_unacknowledgedControlEvents.Count >= MaximumUnacknowledgedControlEvents)
                {
                    throw new GuardianHostClientException(
                        GuardianHostClientFailureKind.EventCorrelationMismatch);
                }
                _unacknowledgedControlEvents.Add(
                    hostEvent.EventSequence.Value,
                    hostEvent);
            }

            _lastHostEventSequence = hostEvent.EventSequence.Value;
        }
    }

    private GuardianHostEvent? ValidateControlEventLocked(GuardianHostRequest request)
    {
        if (request is not (
            WorkerCreateCapabilityGrantRequest or
            WorkerContainmentPendingAckRequest or
            WorkerContainmentArmedAckRequest or
            WorkerContainmentRemoveAckRequest))
        {
            return null;
        }

        var sourceSequence = request switch
        {
            WorkerCreateCapabilityGrantRequest value => value.SourceEventSequence,
            WorkerContainmentAckRequest value => value.SourceEventSequence,
            _ => throw new InvalidOperationException(),
        };
        if (!_unacknowledgedControlEvents.TryGetValue(
                sourceSequence.Value,
                out var sourceEvent) ||
            !ControlPairMatches(sourceEvent.EventType, request.Method) ||
            !ControlEnvelopeMatches(sourceEvent, request))
        {
            throw new ArgumentException(
                "The guardian control request does not match one unclaimed host event.",
                nameof(request));
        }
        return sourceEvent;
    }

    private void ClaimControlEventLocked(GuardianHostEvent? sourceEvent)
    {
        if (sourceEvent is null) return;
        if (!_unacknowledgedControlEvents.Remove(sourceEvent.EventSequence.Value, out var removed) ||
            !ReferenceEquals(removed, sourceEvent))
            throw new InvalidOperationException("The private control event is no longer claimable.");
    }

    private void RequireRequestCapacityLocked(
        HostBootId hostBootId,
        PrivateRequestId requestId)
    {
        RequireReadyLocked(hostBootId);
        if (_pending.Count >= MaximumOutstandingRequests)
            throw new InvalidOperationException("Private request capacity is exhausted.");
        if (_pending.ContainsKey(requestId.Value))
            throw new InvalidOperationException("The private request ID is already registered.");
    }

    private async Task<TPayload> ReadSuccessAsync<TPayload>(
        PrivateRequestId requestId,
        CancellationToken cancellationToken)
        where TPayload : GuardianHostSuccessPayload
    {
        var response = await ReadRequiredAsync<GuardianHostResponse>(cancellationToken)
            .ConfigureAwait(false);
        ValidateIdentity(response);
        if (response.RequestId != requestId ||
            response is not GuardianHostSuccessResponse success ||
            success.Payload is not TPayload payload)
        {
            throw new GuardianHostClientException(
                GuardianHostClientFailureKind.ResponseCorrelationMismatch);
        }
        return payload;
    }

    private async Task<TMessage> ReadRequiredAsync<TMessage>(
        CancellationToken cancellationToken)
        where TMessage : GuardianHostMessage
    {
        var message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (message is null)
            throw new GuardianHostClientException(GuardianHostClientFailureKind.UnexpectedEof);
        if (message is not TMessage expected)
        {
            (message as IDisposable)?.Dispose();
            throw new GuardianHostClientException(GuardianHostClientFailureKind.ProtocolViolation);
        }
        return expected;
    }

    private async ValueTask WriteAsync(
        GuardianHostMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            await _writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            throw NormalizeWriteFailure(exception);
        }
    }

    private PrivateRequestId NextRequestId()
    {
        lock (_sync)
        {
            PrivateRequestId requestId;
            try
            {
                requestId = _requestIds.Allocate();
            }
            catch (GuardianIdentityExhaustedException)
            {
                throw new GuardianHostClientException(
                    GuardianHostClientFailureKind.RequestIdExhausted);
            }

            if (requestId.Value <= _lastGuardianRequestId)
            {
                throw new GuardianHostClientException(
                    GuardianHostClientFailureKind.RequestIdExhausted);
            }
            _lastGuardianRequestId = requestId.Value;
            return requestId;
        }
    }

    private void ValidateManifest(RecoveryManifest manifest)
    {
        if (manifest.GuardianBootId != _pins.GuardianBootId ||
            manifest.HostGeneration != _pins.HostGeneration ||
            manifest.HostGenerationHighWatermark != _pins.HostGeneration ||
            manifest.ConfigurationDigest != _pins.ConfigurationDigest ||
            manifest.CatalogDigest != _pins.CatalogDigest)
        {
            throw new GuardianHostClientException(
                GuardianHostClientFailureKind.ContractMismatch);
        }
    }

    private void ValidateHello(GuardianHostHello hello)
    {
        if (hello.GuardianBootId != _pins.GuardianBootId ||
            hello.HostBootId != _pins.HostBootId ||
            hello.HostGeneration != _pins.HostGeneration ||
            hello.HostPid != _pins.HostProcessId ||
            hello.HostExecutableDigest != _pins.HostExecutableDigest ||
            hello.HostBuildDigest != _pins.HostBuildDigest ||
            hello.PublicContractDigest != _pins.PublicContractDigest ||
            hello.ConfigurationDigest != _pins.ConfigurationDigest)
        {
            throw new GuardianHostClientException(
                GuardianHostClientFailureKind.ContractMismatch);
        }
    }

    private void ValidateIdentity(GuardianHostMessage message)
    {
        HostBootId? hostBootId;
        lock (_sync) hostBootId = _hostBootId;
        if (hostBootId is null ||
            message.GuardianBootId != _pins.GuardianBootId ||
            message.HostBootId != hostBootId ||
            message.HostGeneration != _pins.HostGeneration)
        {
            throw new GuardianHostClientException(
                GuardianHostClientFailureKind.ContractMismatch);
        }
    }

    private void ValidateOutboundRequest(
        GuardianHostRequest request,
        PrivateRequestId requestId,
        HostBootId hostBootId)
    {
        if (request.GuardianBootId != _pins.GuardianBootId ||
            request.HostBootId != hostBootId ||
            request.HostGeneration != _pins.HostGeneration ||
            request.RequestId != requestId)
        {
            throw new ArgumentException(
                "The outbound request does not match its allocated host identity.",
                nameof(request));
        }
        if (request.Method is GuardianHostRequestMethod.ManifestHeader or
            GuardianHostRequestMethod.ManifestChunk or
            GuardianHostRequestMethod.ManifestSeal)
        {
            throw new ArgumentException(
                "Manifest transfer is closed after private-host readiness.",
                nameof(request));
        }
    }

    private HostBootId RequireReadyHost()
    {
        lock (_sync)
        {
            if (_state != GuardianHostClientState.Ready || _hostBootId is null)
                throw CurrentStateExceptionLocked();
            return _hostBootId;
        }
    }

    private void RequireReadyLocked(HostBootId hostBootId)
    {
        if (_state != GuardianHostClientState.Ready ||
            _hostBootId is null ||
            _hostBootId != hostBootId)
        {
            throw CurrentStateExceptionLocked();
        }
    }

    private void RequireStoppingLocked(HostBootId hostBootId)
    {
        if (_state != GuardianHostClientState.Stopping ||
            _hostBootId is null ||
            _hostBootId != hostBootId ||
            _shutdownAccepted)
        {
            throw CurrentStateExceptionLocked();
        }
    }

    private void RequireInboundActiveLocked(HostBootId hostBootId)
    {
        if (!IsInboundActiveLocked() ||
            _hostBootId is null ||
            _hostBootId != hostBootId)
        {
            throw CurrentStateExceptionLocked();
        }
    }

    private bool IsInboundActiveLocked() => _state is
        GuardianHostClientState.Ready or GuardianHostClientState.Stopping;

    private Exception CurrentStateExceptionLocked() => _failure ??
        new GuardianHostClientException(
            _state is GuardianHostClientState.Stopped or GuardianHostClientState.Stopping
                ? GuardianHostClientFailureKind.Stopped
                : GuardianHostClientFailureKind.ProtocolViolation);

    private void ThrowIfDisposedLocked() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0,
        this);

    private GuardianHostClientException Fault(GuardianHostClientException proposed)
    {
        PendingRequest[] pending;
        GuardianHostClientException failure;
        var first = false;
        lock (_sync)
        {
            if (_failure is null && _state != GuardianHostClientState.Stopped)
            {
                _failure = proposed;
                _state = GuardianHostClientState.Faulted;
                first = true;
            }
            failure = _failure ?? proposed;
            pending = first ? RemoveUnreservedPendingLocked() : [];
            if (first)
            {
                _unacknowledgedControlEvents.Clear();
            }
        }

        if (first)
        {
            _fatal.TrySetResult(failure);
            foreach (var request in pending)
                request.Completion.TrySetException(failure);
            try { _lifetime.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        return failure;
    }

    private PendingRequest[] RemoveUnreservedPendingLocked()
    {
        var removable = _pending
            .Where(item => !item.Value.TerminalCandidateReserved)
            .ToArray();
        foreach (var item in removable)
            _pending.Remove(item.Key);
        return removable.Select(item => item.Value).ToArray();
    }

    private static GuardianHostClientException NormalizeFailure(Exception exception) => exception switch
    {
        GuardianHostClientException value => value,
        GuardianHostProtocolException => new GuardianHostClientException(
            GuardianHostClientFailureKind.ProtocolViolation),
        OperationCanceledException => new GuardianHostClientException(
            GuardianHostClientFailureKind.Canceled),
        IOException or ObjectDisposedException => new GuardianHostClientException(
            GuardianHostClientFailureKind.TransportFailure),
        _ => new GuardianHostClientException(
            GuardianHostClientFailureKind.ProtocolViolation),
    };

    private static GuardianHostClientException NormalizeWriteFailure(Exception exception) =>
        exception switch
        {
            GuardianHostClientException value => value,
            GuardianHostProtocolException or IOException or ObjectDisposedException or
                OperationCanceledException => new GuardianHostClientException(
                    GuardianHostClientFailureKind.TransportFailure),
            _ => new GuardianHostClientException(
                GuardianHostClientFailureKind.ProtocolViolation),
        };

    private static bool ResponseMatchesRequest(
        GuardianHostResponse response,
        GuardianHostMessage request)
    {
        if (response is GuardianHostErrorResponse) return true;
        if (response is not GuardianHostSuccessResponse success) return false;
        return request switch
        {
            OperationRequest operation => success.Payload is OperationCompleted completed &&
                completed.OperationKind == operation.Operation.Kind &&
                OperationResultMatches(operation, completed.Result),
            WorkerCreateCapabilityGrantRequest grant =>
                success.Payload is ControlAcknowledged acknowledged &&
                acknowledged.SourceEventSequence == grant.SourceEventSequence,
            WorkerContainmentAckRequest containment =>
                success.Payload is ControlAcknowledged acknowledged &&
                acknowledged.SourceEventSequence == containment.SourceEventSequence,
            GuardianHostShutdown => success.Payload is ShutdownAccepted,
            _ => false,
        };
    }

    private static bool EventMatchesRequest(
        GuardianHostEvent hostEvent,
        OperationRequest request)
    {
        if (hostEvent.SessionAlias != request.SessionAlias ||
            hostEvent.SessionTransitionVersion != request.SessionTransitionVersion)
        {
            return false;
        }

        return hostEvent switch
        {
            OperationDeliveryEvent value =>
                WorkerMatches(value.WorkerIdentity, request.WorkerIdentity) &&
                OperationMatches(value.OperationIdentity, request.OperationIdentity) &&
                value.DispatchToken == request.Operation.DispatchCapability.Token,
            WorkerLostEvent value =>
                (IsSessionChangingOperation(request.Operation.Kind) ||
                    WorkerMatches(value.WorkerIdentity, request.WorkerIdentity)) &&
                OperationMatches(value.OperationIdentity, request.OperationIdentity),
            GuardianHostWorkerDiagnosticEvent value =>
                (IsSessionChangingOperation(request.Operation.Kind) ||
                    WorkerMatches(value.WorkerIdentity, request.WorkerIdentity)) &&
                OperationMatches(value.OperationIdentity, request.OperationIdentity),
            JobLifecycleEvent value =>
                WorkerMatches(value.WorkerIdentity, request.WorkerIdentity) &&
                OperationMatches(value.OperationIdentity, request.OperationIdentity) &&
                JobMatches(value.PublicJobId, request.Operation),
            OutputChunkEvent value =>
                WorkerMatches(value.WorkerIdentity, request.WorkerIdentity) &&
                OperationMatches(value.OperationIdentity, request.OperationIdentity) &&
                request.Operation.OutputCapability is { } output &&
                value.OutputCapabilityToken == output.Token,
            OutputSealEvent value =>
                WorkerMatches(value.WorkerIdentity, request.WorkerIdentity) &&
                OperationMatches(value.OperationIdentity, request.OperationIdentity) &&
                request.Operation.OutputCapability is { } output &&
                value.OutputCapabilityToken == output.Token,
            SessionLifecycleEvent value =>
                (IsSessionChangingOperation(request.Operation.Kind) ||
                    WorkerMatches(value.WorkerIdentity, request.WorkerIdentity)) &&
                LifecycleReasonMatches(value.Reason, request.Operation.Kind),
            _ => false,
        };
    }

    private static bool WorkerMatches(
        GuardianHostWorkerIdentity? actual,
        GuardianHostWorkerIdentity? expected) =>
        actual is null && expected is null ||
        actual is not null && expected is not null &&
        actual.BootId == expected.BootId &&
        actual.Generation == expected.Generation;

    private static bool OperationResultMatches(
        OperationRequest request,
        GuardianHostOperationResult result)
    {
        if (request.Operation is InvokeBackgroundOperation background &&
            result is InvokeBackgroundResult backgroundResult &&
            backgroundResult.PublicJobId != background.PublicJobId)
        {
            return false;
        }

        return result is not GuardianHostSessionOperationResult session ||
            session.Alias == request.SessionAlias &&
            session.TransitionVersion == request.SessionTransitionVersion;
    }

    private static bool LifecycleReasonMatches(
        GuardianHostSessionLifecycleReason reason,
        GuardianHostOperationKind operationKind) => reason switch
        {
            GuardianHostSessionLifecycleReason.RequestedReset =>
                operationKind == GuardianHostOperationKind.Reset,
            GuardianHostSessionLifecycleReason.RequestedOpen =>
                operationKind == GuardianHostOperationKind.SessionOpen,
            GuardianHostSessionLifecycleReason.RequestedClose =>
                operationKind == GuardianHostOperationKind.SessionClose,
            GuardianHostSessionLifecycleReason.RequestedRestart =>
                operationKind == GuardianHostOperationKind.SessionRestart,
            _ => true,
        };

    private static bool IsSessionChangingOperation(GuardianHostOperationKind operationKind) =>
        operationKind is
            GuardianHostOperationKind.Reset or
            GuardianHostOperationKind.SessionOpen or
            GuardianHostOperationKind.SessionClose or
            GuardianHostOperationKind.SessionRestart;

    private static bool OperationMatches(
        GuardianHostOperationIdentity? actual,
        GuardianHostOperationIdentity? expected) =>
        actual is null && expected is null ||
        actual is not null && expected is not null &&
        actual.PlanId == expected.PlanId &&
        actual.OperationId == expected.OperationId;

    private static bool JobMatches(PublicJobId actual, GuardianHostOperation operation) =>
        operation switch
        {
            InvokeBackgroundOperation value => actual == value.PublicJobId,
            GuardianHostJobIdentityOperation value => actual == value.PublicJobId,
            _ => false,
        };

    private static bool IsControlEvent(GuardianHostEventType eventType) => eventType is
        GuardianHostEventType.WorkerCreateCapabilityRequested or
        GuardianHostEventType.WorkerContainmentPending or
        GuardianHostEventType.WorkerContainmentArmed or
        GuardianHostEventType.WorkerContainmentRemoveRequested;

    private static bool ControlPairMatches(
        GuardianHostEventType eventType,
        GuardianHostRequestMethod requestMethod) =>
        (eventType, requestMethod) switch
        {
            (GuardianHostEventType.WorkerCreateCapabilityRequested,
                GuardianHostRequestMethod.WorkerCreateCapabilityGrant) => true,
            (GuardianHostEventType.WorkerContainmentPending,
                GuardianHostRequestMethod.WorkerContainmentPendingAck) => true,
            (GuardianHostEventType.WorkerContainmentArmed,
                GuardianHostRequestMethod.WorkerContainmentArmedAck) => true,
            (GuardianHostEventType.WorkerContainmentRemoveRequested,
                GuardianHostRequestMethod.WorkerContainmentRemoveAck) => true,
            _ => false,
        };

    private static bool ControlEnvelopeMatches(
        GuardianHostEvent sourceEvent,
        GuardianHostRequest request)
    {
        if (sourceEvent.SessionAlias != request.SessionAlias ||
            sourceEvent.SessionTransitionVersion != request.SessionTransitionVersion)
        {
            return false;
        }

        return (sourceEvent, request) switch
        {
            (WorkerCreateCapabilityRequestedEvent created,
                WorkerCreateCapabilityGrantRequest grant) =>
                grant.DeadlineUnixTimeMilliseconds ==
                    created.StartupDeadlineUnixTimeMilliseconds,
            (GuardianHostContainmentEvent containment,
                WorkerContainmentAckRequest acknowledgement) =>
                WorkerMatches(containment.WorkerIdentity, acknowledgement.WorkerIdentity),
            _ => false,
        };
    }

    private static bool IsFatalRuntimeException(Exception exception) => exception is
        OutOfMemoryException or
        StackOverflowException or
        AccessViolationException;

    private sealed class PendingRequest(GuardianHostMessage message)
    {
        internal GuardianHostMessage Message { get; } = message;
        internal bool TerminalCandidateReserved { get; set; }
        internal TaskCompletionSource<GuardianHostResponse> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

}
