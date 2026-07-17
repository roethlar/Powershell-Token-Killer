using System.Security.Cryptography;
using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

internal enum PrivateHostServerState
{
    Created,
    HelloSent,
    ReceivingManifest,
    InitializingRuntime,
    Ready,
    Stopping,
    Stopped,
    Faulted,
}

internal sealed record PrivateHostServerIdentity
{
    internal PrivateHostServerIdentity(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        int hostPid)
    {
        ArgumentNullException.ThrowIfNull(guardianBootId);
        ArgumentNullException.ThrowIfNull(hostBootId);
        ArgumentNullException.ThrowIfNull(hostGeneration);
        if (hostPid <= 0) throw new ArgumentOutOfRangeException(nameof(hostPid));

        GuardianBootId = guardianBootId;
        HostBootId = hostBootId;
        HostGeneration = hostGeneration;
        HostPid = hostPid;
    }

    internal GuardianBootId GuardianBootId { get; }
    internal HostBootId HostBootId { get; }
    internal HostGeneration HostGeneration { get; }
    internal int HostPid { get; }
}

internal sealed record PrivateHostServerPins
{
    internal PrivateHostServerPins(
        Sha256Digest hostExecutableDigest,
        Sha256Digest hostBuildDigest,
        Sha256Digest publicContractDigest,
        Sha256Digest configurationDigest,
        Sha256Digest packageManifestDigest)
    {
        ArgumentNullException.ThrowIfNull(hostExecutableDigest);
        ArgumentNullException.ThrowIfNull(hostBuildDigest);
        ArgumentNullException.ThrowIfNull(publicContractDigest);
        ArgumentNullException.ThrowIfNull(configurationDigest);
        ArgumentNullException.ThrowIfNull(packageManifestDigest);

        HostExecutableDigest = hostExecutableDigest;
        HostBuildDigest = hostBuildDigest;
        PublicContractDigest = publicContractDigest;
        ConfigurationDigest = configurationDigest;
        PackageManifestDigest = packageManifestDigest;
    }

    internal Sha256Digest HostExecutableDigest { get; }
    internal Sha256Digest HostBuildDigest { get; }
    internal Sha256Digest PublicContractDigest { get; }
    internal Sha256Digest ConfigurationDigest { get; }
    internal Sha256Digest PackageManifestDigest { get; }
}

internal sealed record PrivateHostInitialization(
    RecoveryManifest Manifest,
    PrivateRequestId InitializeRequestId,
    ManifestId ManifestId,
    Sha256Digest ManifestDigest);

/// <summary>
/// Strict, single-use private-host protocol server. It owns stream framing,
/// identity/correlation, initialization readiness, cancellation, and terminal
/// response ownership while delegating execution through one narrow runtime
/// boundary. It cannot launch a process or change MCP registration.
/// </summary>
internal sealed class PrivateHostServer
{
    private static readonly TimeSpan MaximumDeadlinePoll = TimeSpan.FromMinutes(1);

    private readonly GuardianHostProtocolReader _reader;
    private readonly GuardianHostProtocolWriter _writer;
    private readonly PrivateHostServerIdentity _identity;
    private readonly PrivateHostServerPins _pins;
    private readonly IPrivateHostRuntime _runtime;
    private readonly Func<int, byte[]> _manifestBufferFactory;
    private readonly Func<long> _unixTimeMilliseconds;
    private readonly Func<long, CancellationToken, Task> _waitUntilDeadline;

    private long _lastRequestId;
    private int _started;
    private int _state;

    internal PrivateHostServer(
        Stream guardianRequestStream,
        Stream hostEventStream,
        PrivateHostServerIdentity identity,
        PrivateHostServerPins pins,
        IPrivateHostRuntime runtime,
        Func<int, byte[]>? manifestBufferFactory = null,
        Func<long>? unixTimeMilliseconds = null,
        Func<long, CancellationToken, Task>? waitUntilDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(guardianRequestStream);
        ArgumentNullException.ThrowIfNull(hostEventStream);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentNullException.ThrowIfNull(runtime);
        if (!guardianRequestStream.CanRead)
            throw new ArgumentException("Guardian request stream must be readable.", nameof(guardianRequestStream));
        if (!hostEventStream.CanWrite)
            throw new ArgumentException("Host event stream must be writable.", nameof(hostEventStream));

        _reader = new GuardianHostProtocolReader(
            guardianRequestStream,
            GuardianHostPeer.Guardian);
        _writer = new GuardianHostProtocolWriter(
            hostEventStream,
            GuardianHostPeer.Host);
        _identity = identity;
        _pins = pins;
        _runtime = runtime;
        _manifestBufferFactory = manifestBufferFactory ??
            (length => GC.AllocateUninitializedArray<byte>(length));
        _unixTimeMilliseconds = unixTimeMilliseconds ??
            (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _waitUntilDeadline = waitUntilDeadline ?? WaitUntilDeadlineAsync;
        _state = (int)PrivateHostServerState.Created;
    }

    internal PrivateHostServerState State =>
        (PrivateHostServerState)Volatile.Read(ref _state);

    internal async ValueTask<PrivateHostInitialization> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        BeginSingleUse();

        try
        {
            return await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            SetState(PrivateHostServerState.Faulted);
            throw;
        }
    }

    internal async Task RunAsync(CancellationToken cancellationToken = default)
    {
        BeginSingleUse();

        try
        {
            _ = await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
            await RunOperationalLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            SetState(PrivateHostServerState.Faulted);
            throw;
        }
    }

    private async ValueTask<PrivateHostInitialization> InitializeCoreAsync(
        CancellationToken cancellationToken)
    {
        await WriteHelloAsync(cancellationToken).ConfigureAwait(false);
        SetState(PrivateHostServerState.HelloSent);

        var initialize = await ReadRequiredAsync<GuardianHostInitialize>(
            "initialize_required",
            cancellationToken).ConfigureAwait(false);
        ValidateInitialize(initialize);
        var lastRequestId = initialize.RequestId.Value;

        var header = await ReadRequiredAsync<ManifestHeaderRequest>(
            "manifest_header_required",
            cancellationToken).ConfigureAwait(false);
        ValidateRequest(header, ref lastRequestId);
        SetState(PrivateHostServerState.ReceivingManifest);

        byte[]? manifestBytes = null;
        try
        {
            manifestBytes = _manifestBufferFactory(header.TotalBytes) ??
                throw new InvalidOperationException("Manifest buffer factory returned null.");
            if (manifestBytes.Length != header.TotalBytes)
            {
                throw Protocol(
                    "manifest_buffer_invalid",
                    "Manifest buffer factory returned an incorrectly sized buffer.");
            }
            await WriteSuccessAsync(
                header.RequestId,
                new ManifestHeaderAccepted(header.ManifestId),
                cancellationToken).ConfigureAwait(false);

            var nextOffset = 0;
            for (var expectedChunk = 0; expectedChunk < header.ChunkCount; expectedChunk++)
            {
                var chunk = await ReadRequiredAsync<ManifestChunkRequest>(
                    "manifest_chunk_required",
                    cancellationToken).ConfigureAwait(false);
                using (chunk)
                {
                    ValidateRequest(chunk, ref lastRequestId);
                    ValidateChunk(header, chunk, expectedChunk, nextOffset);
                    chunk.RawSpan.CopyTo(manifestBytes.AsSpan(nextOffset));
                    nextOffset = checked(nextOffset + chunk.RawByteCount);
                }

                await WriteSuccessAsync(
                    chunk.RequestId,
                    new ManifestChunkAccepted(
                        header.ManifestId,
                        expectedChunk,
                        nextOffset),
                    cancellationToken).ConfigureAwait(false);
            }

            if (nextOffset != header.TotalBytes)
                throw Protocol("manifest_length_mismatch", "Recovery manifest length does not match its header.");

            var seal = await ReadRequiredAsync<ManifestSealRequest>(
                "manifest_seal_required",
                cancellationToken).ConfigureAwait(false);
            ValidateRequest(seal, ref lastRequestId);
            ValidateSeal(header, seal, manifestBytes);

            RecoveryManifest manifest;
            try
            {
                manifest = RecoveryManifestCodec.DecodeForInitialize(
                    manifestBytes,
                    _pins.ConfigurationDigest);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Protocol(
                    "manifest_invalid",
                    "Recovery manifest failed strict canonical validation.",
                    exception);
            }

            ValidateManifest(header, manifest);
            await WriteSuccessAsync(
                seal.RequestId,
                new ManifestSealed(
                    header.ManifestId,
                    header.ManifestDigest,
                    header.TotalBytes),
                cancellationToken).ConfigureAwait(false);

            var initialization = new PrivateHostInitialization(
                manifest,
                initialize.RequestId,
                header.ManifestId,
                header.ManifestDigest);
            SetState(PrivateHostServerState.InitializingRuntime);
            await _runtime.InitializeAsync(initialization, cancellationToken).ConfigureAwait(false);

            await _writer.WriteAsync(
                new GuardianHostReady(
                    _identity.GuardianBootId,
                    _identity.HostBootId,
                    _identity.HostGeneration,
                    initialize.RequestId,
                    header.ManifestId,
                    header.ManifestDigest,
                    _identity.HostPid),
                cancellationToken).ConfigureAwait(false);

            _lastRequestId = lastRequestId;
            SetState(PrivateHostServerState.Ready);
            return initialization;
        }
        finally
        {
            if (manifestBytes is not null)
                CryptographicOperations.ZeroMemory(manifestBytes);
        }
    }

    private void BeginSingleUse()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("Private-host server is single-use.");
    }

    private async Task RunOperationalLoopAsync(CancellationToken cancellationToken)
    {
        var active = new Dictionary<long, ActiveOperation>();
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task<GuardianHostMessage?>? pendingRead = null;

        try
        {
            while (true)
            {
                pendingRead ??= _reader.ReadAsync(readCancellation.Token).AsTask();
                Task completed = pendingRead;
                if (active.Count != 0)
                {
                    var waits = new Task[active.Count + 1];
                    waits[0] = pendingRead;
                    var index = 1;
                    foreach (var operation in active.Values)
                        waits[index++] = operation.OwnerTask;
                    completed = await Task.WhenAny(waits).ConfigureAwait(false);
                }

                if (!ReferenceEquals(completed, pendingRead))
                {
                    var operation = active.Values.Single(candidate =>
                        ReferenceEquals(candidate.OwnerTask, completed));
                    await operation.OwnerTask.ConfigureAwait(false);
                    active.Remove(operation.Request.RequestId.Value);
                    operation.Dispose();
                    continue;
                }

                var message = await pendingRead.ConfigureAwait(false);
                pendingRead = null;
                if (message is null)
                {
                    throw Protocol(
                        "unexpected_eof",
                        "Private guardian channel ended before shutdown.");
                }

                switch (message)
                {
                    case OperationRequest request:
                        ValidateOperationalRequest(request);
                        if (active.Count >= ContractLimits.MaximumOutstandingPrivateRequests)
                        {
                            throw Protocol(
                                "outstanding_request_limit_exceeded",
                                "Private host outstanding request capacity is exhausted.");
                        }
                        var operation = new ActiveOperation(request, cancellationToken);
                        active.Add(request.RequestId.Value, operation);
                        operation.OwnerTask = Task.Factory.StartNew(
                            () => RunOperationAsync(operation),
                            CancellationToken.None,
                            TaskCreationOptions.DenyChildAttach,
                            TaskScheduler.Default).Unwrap();
                        break;

                    case GuardianHostCancel cancel:
                        ValidateOperationalCancel(cancel);
                        if (active.TryGetValue(cancel.TargetRequestId.Value, out var target))
                            target.RequestCancellation(OperationCancellationReason.Explicit);
                        break;

                    case GuardianHostShutdown shutdown:
                        ValidateOperationalShutdown(shutdown);
                        await CompleteShutdownAsync(
                            shutdown,
                            active,
                            cancellationToken).ConfigureAwait(false);
                        SetState(PrivateHostServerState.Stopped);
                        return;

                    default:
                        try
                        {
                            ValidateIdentity(message);
                            throw Protocol(
                                "operational_message_invalid",
                                "Private host accepts only operation, cancel, or shutdown after readiness.");
                        }
                        finally
                        {
                            if (message is IDisposable disposable)
                                disposable.Dispose();
                        }
                }
            }
        }
        finally
        {
            await CancelIgnoringFailureAsync(readCancellation).ConfigureAwait(false);
            foreach (var operation in active.Values)
                operation.RequestCancellation(OperationCancellationReason.ServerFailure);

            if (pendingRead is not null)
            {
                try
                {
                    var unread = await pendingRead.ConfigureAwait(false);
                    if (unread is IDisposable disposable)
                        disposable.Dispose();
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                }
            }

            foreach (var operation in active.Values)
            {
                try
                {
                    await operation.OwnerTask.ConfigureAwait(false);
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                }
                operation.Dispose();
            }
            active.Clear();
        }
    }

    private async Task RunOperationAsync(ActiveOperation active)
    {
        var deadline = active.Request.DeadlineUnixTimeMilliseconds ??
            throw new InvalidOperationException("An operation request has no deadline.");
        if (deadline <= _unixTimeMilliseconds())
        {
            active.MarkTerminal();
            await WriteOperationTerminalAsync(
                active.Request,
                PrivateHostOperationOutcome.Failed(
                    GuardianHostPrivateDetailCode.RequestDeadlineExpired)).ConfigureAwait(false);
            return;
        }

        active.DeadlineTask = ObserveOperationDeadlineAsync(active, deadline);
        var executionTask = ExecuteRuntimeOperationAsync(active);
        _ = await Task.WhenAny(executionTask, active.DeadlineTask)
            .ConfigureAwait(false);
        var deadlineOwnsTerminal = active.DeadlineOwnsTerminal;

        if (deadlineOwnsTerminal)
        {
            active.MarkTerminal();
            // Deadline ownership is fixed before observing the runtime's late
            // result, but no terminal byte may escape while effects are still
            // live. Quiesce first, then emit the one authoritative deadline.
            _ = await executionTask.ConfigureAwait(false);
            await FinishOperationAsync(active).ConfigureAwait(false);
            await WriteOperationTerminalAsync(
                active.Request,
                PrivateHostOperationOutcome.Failed(
                    GuardianHostPrivateDetailCode.RequestDeadlineExpired)).ConfigureAwait(false);
            return;
        }

        var outcome = await executionTask.ConfigureAwait(false);
        active.MarkTerminal();
        await FinishOperationAsync(active).ConfigureAwait(false);
        await WriteOperationTerminalAsync(active.Request, outcome).ConfigureAwait(false);
    }

    private async Task<PrivateHostOperationOutcome> ExecuteRuntimeOperationAsync(
        ActiveOperation active)
    {
        try
        {
            return await _runtime.ExecuteOperationAsync(
                active.Request,
                active.Token).ConfigureAwait(false) ??
                PrivateHostOperationOutcome.Failed(
                    GuardianHostPrivateDetailCode.InvalidOperationResponse);
        }
        catch (OperationCanceledException exception) when (
            active.IsOwnedCancellation(exception))
        {
            return PrivateHostOperationOutcome.Failed(
                active.Reason == OperationCancellationReason.Deadline
                    ? GuardianHostPrivateDetailCode.RequestDeadlineExpired
                    : active.Reason == OperationCancellationReason.ServerFailure
                        ? GuardianHostPrivateDetailCode.OutcomeUnknown
                        : GuardianHostPrivateDetailCode.RequestCanceled);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return PrivateHostOperationOutcome.Failed(
                GuardianHostPrivateDetailCode.OutcomeUnknown);
        }
        finally
        {
            active.MarkRuntimeCompleted();
        }
    }

    private async Task FinishOperationAsync(ActiveOperation active)
    {
        active.StopDeadline();
        await active.ObserveCancellationAsync().ConfigureAwait(false);
        await active.ObserveDeadlineAsync().ConfigureAwait(false);
    }

    private ValueTask WriteOperationTerminalAsync(
        OperationRequest request,
        PrivateHostOperationOutcome outcome) =>
        _writer.WriteAsync(
            CreateOperationTerminal(request, outcome),
            CancellationToken.None);

    private GuardianHostResponse CreateOperationTerminal(
        OperationRequest request,
        PrivateHostOperationOutcome outcome)
    {
        if (outcome.Result is { } result)
        {
            if (result.OperationKind == request.Operation.Kind)
            {
                return new GuardianHostSuccessResponse(
                    _identity.GuardianBootId,
                    _identity.HostBootId,
                    _identity.HostGeneration,
                    request.RequestId,
                    new OperationCompleted(result));
            }

            return CreateOperationError(
                request.RequestId,
                GuardianHostPrivateDetailCode.OperationResultMismatch);
        }

        return CreateOperationError(
            request.RequestId,
            outcome.Error?.DetailCode ??
                GuardianHostPrivateDetailCode.InvalidOperationResponse);
    }

    private GuardianHostErrorResponse CreateOperationError(
        PrivateRequestId requestId,
        GuardianHostPrivateDetailCode detailCode) =>
        new(
            _identity.GuardianBootId,
            _identity.HostBootId,
            _identity.HostGeneration,
            requestId,
            new GuardianHostPrivateError(detailCode));

    private async Task<bool> ObserveOperationDeadlineAsync(
        ActiveOperation active,
        long deadlineUnixTimeMilliseconds)
    {
        try
        {
            await _waitUntilDeadline(
                deadlineUnixTimeMilliseconds,
                active.DeadlineToken).ConfigureAwait(false);
            return active.RequestDeadlineCancellation();
        }
        catch (OperationCanceledException) when (active.DeadlineToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            active.RequestCancellation(OperationCancellationReason.ServerFailure);
            return false;
        }
    }

    private async Task CompleteShutdownAsync(
        GuardianHostShutdown shutdown,
        Dictionary<long, ActiveOperation> active,
        CancellationToken cancellationToken)
    {
        SetState(PrivateHostServerState.Stopping);
        foreach (var operation in active.Values)
            operation.RequestCancellation(OperationCancellationReason.Shutdown);

        using var shutdownCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        using var deadlineStop = new CancellationTokenSource();
        var deadlineTask = CancelAtDeadlineAsync(
            shutdown.DeadlineUnixTimeMilliseconds,
            shutdownCancellation,
            deadlineStop.Token);
        try
        {
            var ownerTasks = active.Values
                .Select(operation => operation.OwnerTask)
                .ToArray();
            await Task.WhenAll(ownerTasks)
                .WaitAsync(shutdownCancellation.Token)
                .ConfigureAwait(false);
            foreach (var operation in active.Values)
                operation.Dispose();
            active.Clear();

            await _runtime.ShutdownAsync(
                shutdown,
                shutdownCancellation.Token).ConfigureAwait(false);
            shutdownCancellation.Token.ThrowIfCancellationRequested();
            await WriteSuccessAsync(
                shutdown.RequestId,
                new ShutdownAccepted(),
                shutdownCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            await CancelIgnoringFailureAsync(deadlineStop).ConfigureAwait(false);
            try
            {
                await deadlineTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
            }
        }
    }

    private async Task CancelAtDeadlineAsync(
        long deadlineUnixTimeMilliseconds,
        CancellationTokenSource cancellation,
        CancellationToken stopToken)
    {
        try
        {
            if (deadlineUnixTimeMilliseconds > _unixTimeMilliseconds())
            {
                await _waitUntilDeadline(
                    deadlineUnixTimeMilliseconds,
                    stopToken).ConfigureAwait(false);
            }
            if (!stopToken.IsCancellationRequested)
                await CancelIgnoringFailureAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            await CancelIgnoringFailureAsync(cancellation).ConfigureAwait(false);
        }
    }

    private void ValidateOperationalRequest(OperationRequest request)
    {
        ValidateIdentity(request);
        ValidateNextRequestId(request.RequestId);
    }

    private void ValidateOperationalCancel(GuardianHostCancel cancel)
    {
        ValidateIdentity(cancel);
        ValidateNextRequestId(cancel.RequestId);
        if (cancel.TargetRequestId.Value >= cancel.RequestId.Value)
        {
            throw Protocol(
                "cancel_target_invalid",
                "Private cancellation must target an earlier request ID.");
        }
    }

    private void ValidateOperationalShutdown(GuardianHostShutdown shutdown)
    {
        ValidateIdentity(shutdown);
        ValidateNextRequestId(shutdown.RequestId);
    }

    private void ValidateNextRequestId(PrivateRequestId requestId)
    {
        if (requestId.Value <= _lastRequestId)
        {
            throw Protocol(
                "request_id_not_increasing",
                "Private guardian request IDs must be strictly increasing.");
        }
        _lastRequestId = requestId.Value;
    }

    private async Task WaitUntilDeadlineAsync(
        long deadlineUnixTimeMilliseconds,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = _unixTimeMilliseconds();
            if (deadlineUnixTimeMilliseconds <= now) return;
            var remaining = (decimal)deadlineUnixTimeMilliseconds - now;
            var delayMilliseconds = (double)Math.Min(
                remaining,
                (decimal)MaximumDeadlinePoll.TotalMilliseconds);
            await Task.Delay(
                TimeSpan.FromMilliseconds(delayMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CancelIgnoringFailureAsync(
        CancellationTokenSource cancellation)
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private ValueTask WriteHelloAsync(CancellationToken cancellationToken) =>
        _writer.WriteAsync(
            new GuardianHostHello(
                _identity.GuardianBootId,
                _identity.HostBootId,
                _identity.HostGeneration,
                _identity.HostPid,
                _pins.HostExecutableDigest,
                _pins.HostBuildDigest,
                _pins.PublicContractDigest,
                _pins.ConfigurationDigest),
            cancellationToken);

    private async ValueTask<TMessage> ReadRequiredAsync<TMessage>(
        string detailCode,
        CancellationToken cancellationToken)
        where TMessage : GuardianHostMessage
    {
        var message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (message is null)
            throw Protocol("unexpected_eof", "Private guardian channel ended during initialization.");
        return RequireMessage<TMessage>(message, detailCode);
    }

    internal TMessage RequireMessage<TMessage>(
        GuardianHostMessage message,
        string detailCode)
        where TMessage : GuardianHostMessage
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(detailCode);

        try
        {
            ValidateIdentity(message);
            return message as TMessage ?? throw Protocol(
                detailCode,
                $"Expected private message '{typeof(TMessage).Name}'.");
        }
        catch
        {
            if (message is IDisposable disposable)
                disposable.Dispose();
            throw;
        }
    }

    private void ValidateIdentity(GuardianHostMessage message)
    {
        if (message.GuardianBootId != _identity.GuardianBootId ||
            message.HostBootId != _identity.HostBootId ||
            message.HostGeneration != _identity.HostGeneration)
        {
            throw Protocol("identity_mismatch", "Private initialization identity does not match this host generation.");
        }
    }

    private void ValidateInitialize(GuardianHostInitialize initialize)
    {
        if (initialize.HostExecutableDigest != _pins.HostExecutableDigest ||
            initialize.HostBuildDigest != _pins.HostBuildDigest ||
            initialize.PublicContractDigest != _pins.PublicContractDigest ||
            initialize.ConfigurationDigest != _pins.ConfigurationDigest ||
            initialize.PackageManifestDigest != _pins.PackageManifestDigest)
        {
            throw Protocol("initialize_pin_mismatch", "Guardian initialization pins do not match the private host.");
        }
    }

    private static void ValidateRequest(
        GuardianHostRequest request,
        ref long lastRequestId)
    {
        if (request.RequestId.Value <= lastRequestId)
        {
            throw Protocol(
                "request_id_not_increasing",
                "Private guardian request IDs must be strictly increasing.");
        }

        lastRequestId = request.RequestId.Value;
    }

    private static void ValidateChunk(
        ManifestHeaderRequest header,
        ManifestChunkRequest chunk,
        int expectedChunk,
        int nextOffset)
    {
        var expectedBytes = Math.Min(
            ContractLimits.MaximumManifestChunkBytes,
            header.TotalBytes - nextOffset);
        if (chunk.ManifestId != header.ManifestId ||
            chunk.ChunkIndex != expectedChunk ||
            chunk.Offset != nextOffset ||
            chunk.RawByteCount != expectedBytes)
        {
            throw Protocol(
                "manifest_chunk_mismatch",
                "Recovery manifest chunk order, identity, offset, or length is invalid.");
        }
    }

    private static void ValidateSeal(
        ManifestHeaderRequest header,
        ManifestSealRequest seal,
        byte[] manifestBytes)
    {
        if (seal.ManifestId != header.ManifestId ||
            seal.TotalBytes != header.TotalBytes ||
            seal.ManifestDigest != header.ManifestDigest ||
            Sha256Digest.Compute(manifestBytes) != header.ManifestDigest)
        {
            throw Protocol("manifest_seal_mismatch", "Recovery manifest seal does not match the transferred bytes.");
        }
    }

    private void ValidateManifest(
        ManifestHeaderRequest header,
        RecoveryManifest manifest)
    {
        if (manifest.GuardianBootId != _identity.GuardianBootId ||
            manifest.HostGeneration != _identity.HostGeneration ||
            manifest.HostGenerationHighWatermark != _identity.HostGeneration ||
            manifest.Bindings.Count != header.AliasCount ||
            manifest.Templates.Count != header.TemplateCount)
        {
            throw Protocol(
                "manifest_identity_mismatch",
                "Recovery manifest identity or declared collection counts do not match initialization.");
        }
    }

    private ValueTask WriteSuccessAsync(
        PrivateRequestId requestId,
        GuardianHostSuccessPayload payload,
        CancellationToken cancellationToken) =>
        _writer.WriteAsync(
            new GuardianHostSuccessResponse(
                _identity.GuardianBootId,
                _identity.HostBootId,
                _identity.HostGeneration,
                requestId,
                payload),
            cancellationToken);

    private enum OperationCancellationReason
    {
        None,
        Explicit,
        Deadline,
        Shutdown,
        ServerFailure,
    }

    private sealed class ActiveOperation : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _execution;
        private readonly CancellationTokenSource _deadline = new();

        private Task? _cancellationTask;
        private OperationCancellationReason _reason;
        private bool _deadlineOwnsTerminal;
        private bool _runtimeCompleted;
        private bool _terminal;

        internal ActiveOperation(
            OperationRequest request,
            CancellationToken serverCancellation)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            _execution = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
        }

        internal OperationRequest Request { get; }

        internal CancellationToken Token => _execution.Token;

        internal CancellationToken DeadlineToken => _deadline.Token;

        internal Task OwnerTask { get; set; } = null!;

        internal Task<bool>? DeadlineTask { get; set; }

        internal bool DeadlineOwnsTerminal
        {
            get
            {
                lock (_gate) return _deadlineOwnsTerminal;
            }
        }

        internal OperationCancellationReason Reason
        {
            get
            {
                lock (_gate) return _reason;
            }
        }

        internal void RequestCancellation(OperationCancellationReason reason)
        {
            lock (_gate)
            {
                if (_terminal || _reason != OperationCancellationReason.None) return;
                _reason = reason;
                _cancellationTask = CancelIgnoringFailureAsync(_execution);
            }
        }

        internal bool RequestDeadlineCancellation()
        {
            lock (_gate)
            {
                if (_terminal || _runtimeCompleted) return false;
                if (_reason == OperationCancellationReason.Deadline) return true;
                if (_reason != OperationCancellationReason.None) return false;
                _deadlineOwnsTerminal = true;
                _reason = OperationCancellationReason.Deadline;
                _cancellationTask = CancelIgnoringFailureAsync(_execution);
                return true;
            }
        }

        internal bool IsOwnedCancellation(OperationCanceledException exception)
        {
            lock (_gate)
            {
                return _reason != OperationCancellationReason.None &&
                       _execution.IsCancellationRequested &&
                       exception.CancellationToken == _execution.Token;
            }
        }

        internal void MarkTerminal()
        {
            lock (_gate) _terminal = true;
        }

        internal void MarkRuntimeCompleted()
        {
            lock (_gate) _runtimeCompleted = true;
        }

        internal void StopDeadline()
        {
            try
            {
                _deadline.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        internal async Task ObserveCancellationAsync()
        {
            Task? cancellationTask;
            lock (_gate) cancellationTask = _cancellationTask;
            if (cancellationTask is not null)
                await cancellationTask.ConfigureAwait(false);
        }

        internal async Task ObserveDeadlineAsync()
        {
            if (DeadlineTask is not null)
                _ = await DeadlineTask.ConfigureAwait(false);
        }

        public void Dispose()
        {
            _deadline.Dispose();
            _execution.Dispose();
        }
    }

    private void SetState(PrivateHostServerState state) =>
        Volatile.Write(ref _state, (int)state);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private static GuardianHostProtocolException Protocol(
        string detailCode,
        string message,
        Exception? innerException = null) =>
        innerException is null
            ? new GuardianHostProtocolException(detailCode, message)
            : new GuardianHostProtocolException(detailCode, message, innerException);
}
