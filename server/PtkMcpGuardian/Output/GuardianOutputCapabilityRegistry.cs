using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using PtkMcpServer;
using PtkSharedContracts;

namespace PtkMcpGuardian.Output;

internal enum GuardianOutputCapabilityFailure
{
    UnknownCapability,
    EventCorrelationMismatch,
    CapabilityExpired,
    ChunkIndexInvalid,
    ChunkOffsetInvalid,
    MaximumBytesExceeded,
    InvalidUtf8,
    SealLengthMismatch,
    SealDigestMismatch,
}

internal sealed class GuardianOutputCapabilityException(
    GuardianOutputCapabilityFailure failure) : Exception(failure.ToString())
{
    internal GuardianOutputCapabilityFailure Failure { get; } = failure;
}

internal sealed record GuardianOutputRegistration(
    CapabilityToken Token,
    PrivateRequestId RequestId,
    long RegistrationId,
    bool StorageAvailable,
    string? StorageFailure);

internal sealed record GuardianOutputTerminal(
    CapabilityToken Token,
    PrivateRequestId RequestId,
    CallId CallId,
    CanonicalAlias SessionAlias,
    GuardianHostOperationKind OperationKind,
    PublicJobId? PublicJobId,
    OutputRecoverySummary Recovery);

/// <summary>
/// Guardian-owned authority for private-host output capabilities. It accepts
/// only exact request correlation, validates one contiguous strict-UTF-8 byte
/// stream and its terminal digest, and maps a successful seal to the public
/// OutputStore handle without exposing that handle to the private host.
/// Storage unavailability degrades capture only: the bounded stream is still
/// validated and discarded so user work is never rerun for a capture failure.
/// </summary>
internal sealed class GuardianOutputCapabilityRegistry : IDisposable
{
    internal const int DefaultMaximumActiveCapabilities = 4096;

    private readonly object _gate = new();
    private readonly OutputStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly int _maximumActiveCapabilities;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private long _nextRegistrationId;
    private bool _disposed;

    internal GuardianOutputCapabilityRegistry(
        OutputStore store,
        TimeProvider? timeProvider = null,
        int maximumActiveCapabilities = DefaultMaximumActiveCapabilities)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (maximumActiveCapabilities is < 1 or > DefaultMaximumActiveCapabilities)
            throw new ArgumentOutOfRangeException(nameof(maximumActiveCapabilities));
        _maximumActiveCapabilities = maximumActiveCapabilities;
    }

    internal int ActiveCount
    {
        get { lock (_gate) return _entries.Count; }
    }

    internal bool TryRegister(
        OperationRequest request,
        out GuardianOutputRegistration? registration,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(request);
        registration = null;
        failure = null;
        var output = ValidateRegistrationRequest(request);

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (output.ExpiresUnixTimeMilliseconds <= UnixTimeMilliseconds())
            {
                failure = "output_capability_expired";
                return false;
            }
            if (_entries.Count >= _maximumActiveCapabilities)
            {
                failure = "output_capability_capacity";
                return false;
            }
            if (_entries.ContainsKey(output.Token.Value))
            {
                failure = "output_capability_duplicate";
                return false;
            }

            OutputCaptureReservation? reservation = null;
            string? storageFailure = null;
            if (output.MaximumBytes > _store.MaximumArtifactBytes)
            {
                storageFailure = "output_store_bound";
            }
            else
            {
                try
                {
                    if (!_store.TryReserve(
                            request.SessionAlias!.Value,
                            out reservation,
                            out var reserveFailure))
                    {
                        storageFailure = reserveFailure is null
                            ? "output_store_unavailable"
                            : $"output_store_{reserveFailure}";
                    }
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    storageFailure = "output_store_unavailable";
                }
            }

            var registrationId = checked(++_nextRegistrationId);
            Entry entry;
            try
            {
                entry = new Entry(
                    request,
                    output,
                    registrationId,
                    reservation,
                    storageFailure);
            }
            catch
            {
                reservation?.Dispose();
                throw;
            }

            _entries.Add(output.Token.Value, entry);
            registration = entry.Registration;
            return true;
        }
    }

    /// <summary>
    /// Exact retained-correlation check used only after the originating
    /// private request has completed. It grants no bytes and mutates no entry.
    /// </summary>
    internal bool MatchesActiveEvent(GuardianHostEvent hostEvent)
    {
        ArgumentNullException.ThrowIfNull(hostEvent);
        if (!TryGetToken(hostEvent, out var token)) return false;

        lock (_gate)
        {
            return !_disposed &&
                _entries.TryGetValue(token.Value, out var entry) &&
                entry.ExpiresUnixTimeMilliseconds > UnixTimeMilliseconds() &&
                entry.Matches(hostEvent);
        }
    }

    /// <summary>
    /// Consumes one output event. A null result is an accepted chunk; a
    /// non-null result is the single terminal seal and removes the capability.
    /// </summary>
    internal GuardianOutputTerminal? AcceptEvent(GuardianHostEvent hostEvent)
    {
        ArgumentNullException.ThrowIfNull(hostEvent);
        if (!TryGetToken(hostEvent, out var token))
            throw new ArgumentException("The event is not an output event.", nameof(hostEvent));

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (!_entries.TryGetValue(token.Value, out var entry))
            {
                throw new GuardianOutputCapabilityException(
                    GuardianOutputCapabilityFailure.UnknownCapability);
            }

            try
            {
                if (!entry.Matches(hostEvent))
                {
                    throw new GuardianOutputCapabilityException(
                        GuardianOutputCapabilityFailure.EventCorrelationMismatch);
                }
                if (entry.ExpiresUnixTimeMilliseconds <= UnixTimeMilliseconds())
                {
                    throw new GuardianOutputCapabilityException(
                        GuardianOutputCapabilityFailure.CapabilityExpired);
                }

                switch (hostEvent)
                {
                    case OutputChunkEvent chunk:
                        entry.Accept(chunk);
                        return null;
                    case OutputSealEvent seal:
                        entry.Validate(seal);
                        _entries.Remove(token.Value);
                        return entry.Publish(seal.State);
                    default:
                        throw new ArgumentException(
                            "The event is not an output event.",
                            nameof(hostEvent));
                }
            }
            catch (GuardianOutputCapabilityException)
            {
                _entries.Remove(token.Value);
                entry.Cancel();
                throw;
            }
        }
    }

    internal bool TryCancel(GuardianOutputRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        Entry? canceled = null;
        lock (_gate)
        {
            if (_disposed) return false;
            if (_entries.TryGetValue(registration.Token.Value, out var entry) &&
                entry.Registration.RegistrationId == registration.RegistrationId &&
                entry.RequestId == registration.RequestId)
            {
                _entries.Remove(registration.Token.Value);
                canceled = entry;
            }
        }

        canceled?.Cancel();
        return canceled is not null;
    }

    /// <summary>
    /// Terminalizes every still-active capability for one exact host
    /// generation. A nonempty valid prefix becomes an incomplete artifact;
    /// zero bytes or a split/invalid UTF-8 prefix remains unavailable.
    /// </summary>
    internal IReadOnlyList<GuardianOutputTerminal> AbandonGeneration(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(guardianBootId);
        ArgumentNullException.ThrowIfNull(hostBootId);
        ArgumentNullException.ThrowIfNull(hostGeneration);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            var abandoned = _entries.Values
                .Where(entry => entry.MatchesHost(
                    guardianBootId,
                    hostBootId,
                    hostGeneration))
                .OrderBy(entry => entry.Registration.RegistrationId)
                .ToArray();
            foreach (var entry in abandoned)
                _entries.Remove(entry.Registration.Token.Value);
            return abandoned
                .Select(entry => entry.PublishIncomplete(reason))
                .ToArray();
        }
    }

    internal IReadOnlyList<GuardianOutputTerminal> SweepExpired()
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            var now = UnixTimeMilliseconds();
            var expired = _entries.Values
                .Where(entry => entry.ExpiresUnixTimeMilliseconds <= now)
                .OrderBy(entry => entry.Registration.RegistrationId)
                .ToArray();
            foreach (var entry in expired)
                _entries.Remove(entry.Registration.Token.Value);
            return expired
                .Select(entry => entry.PublishIncomplete(
                    "output_capability_expired"))
                .ToArray();
        }
    }

    public void Dispose()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.Values.ToArray();
            _entries.Clear();
        }

        foreach (var entry in entries)
            entry.Cancel();
    }

    private static OutputCapability ValidateRegistrationRequest(
        OperationRequest request)
    {
        if (request.Operation.Kind is not (
                GuardianHostOperationKind.InvokeForeground or
                GuardianHostOperationKind.InvokeBackground or
                GuardianHostOperationKind.JobOutput) ||
            request.Operation.OutputCapability is not { } output ||
            request.SessionAlias is null ||
            request.SessionTransitionVersion is null ||
            request.WorkerIdentity is null)
        {
            throw new ArgumentException(
                "The request has no complete output-capability correlation.",
                nameof(request));
        }
        return output;
    }

    private static bool TryGetToken(
        GuardianHostEvent hostEvent,
        out CapabilityToken token)
    {
        switch (hostEvent)
        {
            case OutputChunkEvent chunk:
                token = chunk.OutputCapabilityToken;
                return true;
            case OutputSealEvent seal:
                token = seal.OutputCapabilityToken;
                return true;
            default:
                token = null!;
                return false;
        }
    }

    private long UnixTimeMilliseconds() =>
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private void ThrowIfDisposedLocked()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GuardianOutputCapabilityRegistry));
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed class Entry : IDisposable
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private readonly GuardianBootId _guardianBootId;
        private readonly HostBootId _hostBootId;
        private readonly HostGeneration _hostGeneration;
        private readonly PrivateRequestId _requestId;
        private readonly CanonicalAlias _sessionAlias;
        private readonly SessionTransitionVersion _sessionTransitionVersion;
        private readonly GuardianHostWorkerIdentity _workerIdentity;
        private readonly GuardianHostOperationIdentity? _operationIdentity;
        private readonly CallId _callId;
        private readonly GuardianHostOperationKind _operationKind;
        private readonly PublicJobId? _publicJobId;
        private readonly OutputCapability _capability;
        private readonly Decoder _decoder = StrictUtf8.GetDecoder();
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        private OutputCaptureReservation? _reservation;
        private byte[]? _buffer;
        private long _nextChunkIndex;
        private int _bytesReceived;
        private int _closed;

        internal Entry(
            OperationRequest request,
            OutputCapability capability,
            long registrationId,
            OutputCaptureReservation? reservation,
            string? storageFailure)
        {
            _guardianBootId = request.GuardianBootId;
            _hostBootId = request.HostBootId;
            _hostGeneration = request.HostGeneration;
            _requestId = request.RequestId;
            _sessionAlias = request.SessionAlias!;
            _sessionTransitionVersion = request.SessionTransitionVersion!;
            _workerIdentity = request.Worker!;
            _operationIdentity = request.OperationIdentity;
            _callId = request.Operation.CallId;
            _operationKind = request.Operation.Kind;
            _publicJobId = request.Operation switch
            {
                InvokeBackgroundOperation background => background.PublicJobId,
                GuardianHostJobIdentityOperation job => job.PublicJobId,
                _ => null,
            };
            _capability = capability;
            _reservation = reservation;
            StorageFailure = storageFailure;
            Registration = new GuardianOutputRegistration(
                capability.Token,
                _requestId,
                registrationId,
                reservation is not null,
                storageFailure);
        }

        internal GuardianOutputRegistration Registration { get; }
        internal PrivateRequestId RequestId => _requestId;
        internal long ExpiresUnixTimeMilliseconds =>
            _capability.ExpiresUnixTimeMilliseconds;
        private string? StorageFailure { get; }

        internal bool Matches(GuardianHostEvent hostEvent) =>
            hostEvent.GuardianBootId == _guardianBootId &&
            hostEvent.HostBootId == _hostBootId &&
            hostEvent.HostGeneration == _hostGeneration &&
            hostEvent.RequestId == _requestId &&
            hostEvent.SessionAlias == _sessionAlias &&
            hostEvent.SessionTransitionVersion ==
                _sessionTransitionVersion &&
            WorkerMatches(hostEvent.WorkerIdentity, _workerIdentity) &&
            OperationMatches(
                hostEvent.OperationIdentity,
                _operationIdentity);

        internal bool MatchesHost(
            GuardianBootId guardianBootId,
            HostBootId hostBootId,
            HostGeneration hostGeneration) =>
            _guardianBootId == guardianBootId &&
            _hostBootId == hostBootId &&
            _hostGeneration == hostGeneration;

        internal void Accept(OutputChunkEvent chunk)
        {
            if (chunk.ChunkIndex != _nextChunkIndex)
                throw Failure(GuardianOutputCapabilityFailure.ChunkIndexInvalid);
            if (chunk.Offset != _bytesReceived)
                throw Failure(GuardianOutputCapabilityFailure.ChunkOffsetInvalid);
            if ((long)_bytesReceived + chunk.RawByteCount > _capability.MaximumBytes)
                throw Failure(GuardianOutputCapabilityFailure.MaximumBytesExceeded);

            try
            {
                ValidateUtf8Chunk(chunk.RawSpan);
            }
            catch (DecoderFallbackException)
            {
                throw Failure(GuardianOutputCapabilityFailure.InvalidUtf8);
            }

            _hash.AppendData(chunk.RawSpan);
            if (_reservation is not null)
            {
                _buffer ??= new byte[_capability.MaximumBytes];
                chunk.RawSpan.CopyTo(_buffer.AsSpan(_bytesReceived));
            }
            _bytesReceived = checked(_bytesReceived + chunk.RawByteCount);
            _nextChunkIndex = checked(_nextChunkIndex + 1);
        }

        internal void Validate(OutputSealEvent seal)
        {
            if (seal.TotalBytes != _bytesReceived)
                throw Failure(GuardianOutputCapabilityFailure.SealLengthMismatch);
            try
            {
                _ = _decoder.GetChars(
                    ReadOnlySpan<byte>.Empty,
                    Span<char>.Empty,
                    flush: true);
            }
            catch (DecoderFallbackException)
            {
                throw Failure(GuardianOutputCapabilityFailure.InvalidUtf8);
            }

            var digest = _hash.GetCurrentHash();
            try
            {
                var actual = Convert.ToHexString(digest).ToLowerInvariant();
                if (!StringComparer.Ordinal.Equals(actual, seal.OutputDigest.Value))
                    throw Failure(GuardianOutputCapabilityFailure.SealDigestMismatch);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }

        internal GuardianOutputTerminal Publish(
            GuardianHostOutputSealState state) =>
            PublishCore(
                state == GuardianHostOutputSealState.Complete,
                state == GuardianHostOutputSealState.Incomplete
                    ? "private_host_reported_incomplete"
                    : null);

        internal GuardianOutputTerminal PublishIncomplete(string reason)
        {
            if (_bytesReceived == 0)
                return CancelAndUnavailable(reason);
            try
            {
                _ = _decoder.GetChars(
                    ReadOnlySpan<byte>.Empty,
                    Span<char>.Empty,
                    flush: true);
            }
            catch (DecoderFallbackException)
            {
                return CancelAndUnavailable("output_prefix_invalid_utf8");
            }
            return PublishCore(complete: false, reason);
        }

        internal void Cancel()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try
            {
                _reservation?.TryCancel();
            }
            finally
            {
                Cleanup();
            }
        }

        public void Dispose() => Cancel();

        private GuardianOutputTerminal PublishCore(
            bool complete,
            string? incompleteReason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                throw new InvalidOperationException(
                    "The output capability is already terminal.");

            OutputRecoverySummary recovery;
            try
            {
                var reservation = _reservation;
                if (reservation is null)
                {
                    recovery = OutputRecoverySummary.Unavailable(
                        StorageFailure ?? "output_store_unavailable",
                        advertise: true);
                }
                else
                {
                    OutputSealResult result;
                    try
                    {
                        result = reservation.SealTransferredUtf8(
                            _buffer is null
                                ? ReadOnlyMemory<byte>.Empty
                                : _buffer.AsMemory(0, _bytesReceived),
                            complete,
                            incompleteReason);
                    }
                    catch (Exception exception) when (!IsFatal(exception))
                    {
                        reservation.TryCancel();
                        result = new OutputSealResult(
                            false,
                            null,
                            OutputArtifactState.NotFound,
                            0,
                            "storage_unavailable");
                    }
                    finally
                    {
                        reservation.CompleteObserved();
                    }

                    recovery = result.Success
                        ? OutputRecoverySummary.FromSeal(result)
                        : OutputRecoverySummary.Unavailable(
                            result.DetailCode is null
                                ? "output_store_unavailable"
                                : $"output_store_{result.DetailCode}",
                            advertise: true);
                }
            }
            finally
            {
                Cleanup();
            }

            return Terminal(recovery);
        }

        private GuardianOutputTerminal CancelAndUnavailable(string reason)
        {
            Cancel();
            return Terminal(OutputRecoverySummary.Unavailable(
                reason,
                advertise: true));
        }

        private GuardianOutputTerminal Terminal(OutputRecoverySummary recovery)
        {
            return new GuardianOutputTerminal(
                _capability.Token,
                _requestId,
                _callId,
                _sessionAlias,
                _operationKind,
                _publicJobId,
                recovery);
        }

        private void Cleanup()
        {
            _hash.Dispose();
            _reservation?.Dispose();
            _reservation = null;
            if (_buffer is not null)
            {
                CryptographicOperations.ZeroMemory(_buffer);
                _buffer = null;
            }
        }

        private void ValidateUtf8Chunk(ReadOnlySpan<byte> bytes)
        {
            var characters = ArrayPool<char>.Shared.Rent(
                StrictUtf8.GetMaxCharCount(bytes.Length));
            try
            {
                _ = _decoder.GetChars(
                    bytes,
                    characters.AsSpan(),
                    flush: false);
            }
            finally
            {
                Array.Clear(characters);
                ArrayPool<char>.Shared.Return(characters);
            }
        }

        private static GuardianOutputCapabilityException Failure(
            GuardianOutputCapabilityFailure failure) => new(failure);

        private static bool WorkerMatches(
            GuardianHostWorkerIdentity? actual,
            GuardianHostWorkerIdentity? expected) =>
            actual is null && expected is null ||
            actual is not null && expected is not null &&
            actual.BootId == expected.BootId &&
            actual.Generation == expected.Generation;

        private static bool OperationMatches(
            GuardianHostOperationIdentity? actual,
            GuardianHostOperationIdentity? expected) =>
            actual is null && expected is null ||
            actual is not null && expected is not null &&
            actual.PlanId == expected.PlanId &&
            actual.OperationId == expected.OperationId;
    }
}
