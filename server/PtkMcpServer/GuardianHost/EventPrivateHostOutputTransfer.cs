using System.Security.Cryptography;
using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

/// <summary>
/// Converts execution-scoped output capabilities into contiguous private-host
/// output events. It owns no guardian store, reservation, path, or public
/// handle; the guardian maps the opaque capability to those resources.
/// </summary>
internal sealed class EventPrivateHostOutputTransfer : IPrivateHostOutputTransfer
{
    private const string GuardianOwnedDetailCode = "guardian_owned_output";

    private readonly PrivateHostServerIdentity _identity;
    private readonly IPrivateHostEventSink _eventSink;
    private readonly Func<long> _unixTimeMilliseconds;

    internal EventPrivateHostOutputTransfer(
        PrivateHostServerIdentity identity,
        IPrivateHostEventSink eventSink,
        Func<long>? unixTimeMilliseconds = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        _unixTimeMilliseconds = unixTimeMilliseconds ??
            (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public IExecutionOutputCaptureOwner CreateExecutionCapture(
        OperationRequest request)
    {
        var context = OutputEventContext.Create(
            request,
            GuardianHostOperationKind.InvokeForeground,
            GuardianHostOperationKind.InvokeBackground);
        return new RequestCaptureOwner(new CaptureCore(this, context));
    }

    public async ValueTask TransferTextAsync(
        OperationRequest request,
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var context = OutputEventContext.Create(
            request,
            GuardianHostOperationKind.JobOutput);
        var deadline = Math.Min(
            context.RequestDeadlineUnixTimeMilliseconds,
            context.OutputCapability.ExpiresUnixTimeMilliseconds);
        cancellationToken.ThrowIfCancellationRequested();
        if (_unixTimeMilliseconds() >= deadline)
            return;

        byte[]? payload = null;
        try
        {
            var rendered = OutputArtifactRenderer.RenderToBytes(
                new OutputArtifactContent(
                    text,
                    [],
                    [],
                    [],
                    ExitCode: null,
                    OutputProvenance.DirectText),
                context.OutputCapability.MaximumBytes);
            payload = rendered.Bytes;
            cancellationToken.ThrowIfCancellationRequested();
            if (_unixTimeMilliseconds() >= deadline)
                return;
            using var deadlineCancellation = CreateDeadlineCancellation(
                deadline,
                maximumWait: null,
                cancellationToken);
            _ = await EmitAsync(
                context,
                payload,
                incomplete: rendered.Truncated,
                incompleteReason: rendered.Truncated
                    ? "artifact_cap_exceeded"
                    : null,
                deadline,
                deadlineCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            if (payload is not null)
                CryptographicOperations.ZeroMemory(payload);
        }
    }

    private async Task<OutputRecoverySummary> SealAsync(
        OutputEventContext context,
        OutputArtifactContent content,
        string? incompleteReason,
        long preparedDeadlineUnixTimeMilliseconds,
        TimeSpan maximumWait)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maximumWait <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumWait));
        if (_unixTimeMilliseconds() >= preparedDeadlineUnixTimeMilliseconds)
        {
            return OutputRecoverySummary.Unavailable(
                "output_capability_expired");
        }

        byte[]? payload = null;
        try
        {
            (byte[] Bytes, bool Truncated) rendered;
            try
            {
                rendered = OutputArtifactRenderer.RenderToBytes(
                    content,
                    context.OutputCapability.MaximumBytes);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                return OutputRecoverySummary.Unavailable(
                    "output_render_invalid");
            }

            payload = rendered.Bytes;
            if (_unixTimeMilliseconds() >= preparedDeadlineUnixTimeMilliseconds)
            {
                return OutputRecoverySummary.Unavailable(
                    "output_capability_expired");
            }
            var incomplete = incompleteReason is not null ||
                !content.Complete ||
                rendered.Truncated;
            var detailCode = rendered.Truncated
                ? "artifact_cap_exceeded"
                : incompleteReason ?? content.IncompleteReason;
            using var deadlineCancellation = CreateDeadlineCancellation(
                preparedDeadlineUnixTimeMilliseconds,
                maximumWait,
                CancellationToken.None);
            return await EmitAsync(
                context,
                payload,
                incomplete,
                detailCode,
                preparedDeadlineUnixTimeMilliseconds,
                deadlineCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            if (payload is not null)
                CryptographicOperations.ZeroMemory(payload);
        }
    }

    private async Task<OutputRecoverySummary> EmitAsync(
        OutputEventContext context,
        byte[] payload,
        bool incomplete,
        string? incompleteReason,
        long deadlineUnixTimeMilliseconds,
        CancellationToken cancellationToken)
    {
        var outputDigest = Sha256Digest.Compute(payload);
        var offset = 0;
        long chunkIndex = 0;
        while (offset < payload.Length)
        {
            ThrowIfExpired(deadlineUnixTimeMilliseconds, cancellationToken);
            var count = Math.Min(
                ContractLimits.MaximumOutputChunkBytes,
                payload.Length - offset);
            var chunkOffset = offset;
            var currentChunkIndex = chunkIndex;
            await _eventSink.WriteEventAsync(
                sequence => new OutputChunkEvent(
                    _identity.GuardianBootId,
                    _identity.HostBootId,
                    _identity.HostGeneration,
                    sequence,
                    context.Request.RequestId,
                    context.Request.SessionAlias!,
                    context.Request.SessionTransitionVersion!,
                    context.Request.Worker!,
                    context.Request.OperationIdentity,
                    context.OutputCapability.Token,
                    currentChunkIndex,
                    chunkOffset,
                    payload.AsMemory(chunkOffset, count)),
                cancellationToken).ConfigureAwait(false);
            offset = checked(offset + count);
            chunkIndex = checked(chunkIndex + 1);
        }

        ThrowIfExpired(deadlineUnixTimeMilliseconds, cancellationToken);
        await _eventSink.WriteEventAsync(
            sequence => new OutputSealEvent(
                _identity.GuardianBootId,
                _identity.HostBootId,
                _identity.HostGeneration,
                sequence,
                context.Request.RequestId,
                context.Request.SessionAlias!,
                context.Request.SessionTransitionVersion!,
                context.Request.Worker!,
                context.Request.OperationIdentity,
                context.OutputCapability.Token,
                incomplete
                    ? GuardianHostOutputSealState.Incomplete
                    : GuardianHostOutputSealState.Complete,
                payload.Length,
                outputDigest),
            cancellationToken).ConfigureAwait(false);

        return new OutputRecoverySummary(
            Handle: null,
            incomplete
                ? OutputArtifactState.Incomplete
                : OutputArtifactState.Available,
            payload.Length,
            incomplete
                ? NormalizeDetailCode(incompleteReason)
                : null,
            Advertise: false);
    }

    private CancellationTokenSource CreateDeadlineCancellation(
        long deadlineUnixTimeMilliseconds,
        TimeSpan? maximumWait,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var now = _unixTimeMilliseconds();
        var remainingMilliseconds = deadlineUnixTimeMilliseconds > now
            ? deadlineUnixTimeMilliseconds - now
            : 0;
        if (maximumWait is { } wait)
        {
            remainingMilliseconds = Math.Min(
                remainingMilliseconds,
                Math.Max(0, (long)wait.TotalMilliseconds));
        }

        if (remainingMilliseconds <= 0)
        {
            source.Cancel();
        }
        else
        {
            // Older timer implementations accept at least Int32.MaxValue
            // milliseconds. A farther deadline remains enforced at every
            // frame boundary without inventing an earlier replacement limit.
            if (remainingMilliseconds <= int.MaxValue)
            {
                source.CancelAfter(TimeSpan.FromMilliseconds(
                    remainingMilliseconds));
            }
        }
        return source;
    }

    private void ThrowIfExpired(
        long deadlineUnixTimeMilliseconds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_unixTimeMilliseconds() >= deadlineUnixTimeMilliseconds)
            throw new OperationCanceledException(
                "The output capability deadline expired during transfer.",
                cancellationToken);
    }

    private static string NormalizeDetailCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return GuardianOwnedDetailCode;
        var normalized = new string(value
            .ToLowerInvariant()
            .Select(character => character is
                (>= 'a' and <= 'z') or
                (>= '0' and <= '9') or '_' or '-'
                    ? character
                    : '_')
            .Take(64)
            .ToArray());
        return normalized.Length == 0 ? GuardianOwnedDetailCode : normalized;
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed record OutputEventContext(
        OperationRequest Request,
        OutputCapability OutputCapability,
        long RequestDeadlineUnixTimeMilliseconds)
    {
        internal static OutputEventContext Create(
            OperationRequest request,
            params GuardianHostOperationKind[] permittedKinds)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!permittedKinds.Contains(request.Operation.Kind))
            {
                throw new ArgumentException(
                    "The output-transfer boundary does not support this operation.",
                    nameof(request));
            }
            var output = request.Operation.OutputCapability ??
                throw new ArgumentException(
                    "The operation has no output capability.",
                    nameof(request));
            var deadline = request.DeadlineUnixTimeMilliseconds ??
                throw new ArgumentException(
                    "The operation has no request deadline.",
                    nameof(request));
            if (request.SessionAlias is null ||
                request.SessionTransitionVersion is null ||
                request.Worker is null)
            {
                throw new ArgumentException(
                    "The operation has incomplete output correlation.",
                    nameof(request));
            }
            return new OutputEventContext(request, output, deadline);
        }
    }

    private sealed class CaptureCore(
        EventPrivateHostOutputTransfer owner,
        OutputEventContext context) : IDisposable
    {
        private readonly object _gate = new();
        private long _preparedDeadlineUnixTimeMilliseconds;
        private bool _prepared;
        private int _state;

        internal long MaximumArtifactBytes =>
            context.OutputCapability.MaximumBytes;

        internal Task<OutputCapturePreparation> PrepareAsync(
            DateTimeOffset absoluteDeadlineUtc,
            TimeSpan maximumWait,
            CancellationToken cancellationToken)
        {
            if (maximumWait <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(maximumWait));
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (Volatile.Read(ref _state) != 0)
                {
                    return Task.FromResult(OutputCapturePreparation.Unavailable(
                        "capture_closed"));
                }

                var deadline = Math.Min(
                    context.RequestDeadlineUnixTimeMilliseconds,
                    context.OutputCapability.ExpiresUnixTimeMilliseconds);
                deadline = Math.Min(
                    deadline,
                    absoluteDeadlineUtc.ToUnixTimeMilliseconds());
                _preparedDeadlineUnixTimeMilliseconds = _prepared
                    ? Math.Min(_preparedDeadlineUnixTimeMilliseconds, deadline)
                    : deadline;
                _prepared = true;
                return Task.FromResult(
                    owner._unixTimeMilliseconds() >=
                        _preparedDeadlineUnixTimeMilliseconds
                        ? OutputCapturePreparation.Unavailable(
                            "output_capability_expired")
                        : OutputCapturePreparation.Pending());
            }
        }

        internal Task<OutputRecoverySummary> SealAsync(
            OutputArtifactContent content,
            string? incompleteReason,
            TimeSpan maximumWait)
        {
            ArgumentNullException.ThrowIfNull(content);
            var priorState = Interlocked.CompareExchange(ref _state, 1, 0);
            if (priorState != 0)
            {
                return Task.FromResult(OutputRecoverySummary.Unavailable(
                    priorState == 2
                        ? "capture_closed"
                        : "capture_already_terminal"));
            }

            long deadline;
            lock (_gate)
            {
                if (!_prepared)
                {
                    return Task.FromResult(OutputRecoverySummary.Unavailable(
                        "capture_not_prepared"));
                }
                deadline = _preparedDeadlineUnixTimeMilliseconds;
            }
            return owner.SealAsync(
                context,
                content,
                incompleteReason,
                deadline,
                maximumWait);
        }

        public void Dispose() =>
            _ = Interlocked.CompareExchange(ref _state, 2, 0);
    }

    private abstract class CaptureLease : IExecutionOutputCapture
    {
        private CaptureCore? _core;

        protected CaptureLease(CaptureCore core) =>
            _core = core ?? throw new ArgumentNullException(nameof(core));

        protected CaptureCore? Detach() =>
            Interlocked.Exchange(ref _core, null);

        public long MaximumArtifactBytes =>
            Volatile.Read(ref _core)?.MaximumArtifactBytes ?? 0;

        public Task<OutputCapturePreparation> PrepareAsync(
            DateTimeOffset absoluteDeadlineUtc,
            TimeSpan maximumWait,
            CancellationToken cancellationToken)
        {
            var core = Volatile.Read(ref _core);
            return core is null
                ? Task.FromResult(OutputCapturePreparation.Unavailable(
                    "capture_closed"))
                : core.PrepareAsync(
                    absoluteDeadlineUtc,
                    maximumWait,
                    cancellationToken);
        }

        public Task<OutputRecoverySummary> SealAsync(
            OutputArtifactContent content,
            TimeSpan maximumWait)
        {
            var core = Volatile.Read(ref _core);
            return core is null
                ? Task.FromResult(OutputRecoverySummary.Unavailable(
                    "capture_closed"))
                : core.SealAsync(content, incompleteReason: null, maximumWait);
        }

        public Task<OutputRecoverySummary> SealIncompleteAsync(
            OutputArtifactContent content,
            string reason,
            TimeSpan maximumWait)
        {
            ArgumentNullException.ThrowIfNull(reason);
            var core = Volatile.Read(ref _core);
            return core is null
                ? Task.FromResult(OutputRecoverySummary.Unavailable(
                    "capture_closed"))
                : core.SealAsync(content, reason, maximumWait);
        }

        public void Dispose() => Detach()?.Dispose();
    }

    private sealed class RequestCaptureOwner(CaptureCore core) :
        CaptureLease(core),
        IExecutionOutputCaptureOwner
    {
        public bool TryTransferToBackground(
            out IExecutionOutputCapture? capture)
        {
            var detached = Detach();
            capture = detached is null
                ? null
                : new BackgroundCaptureOwner(detached);
            return capture is not null;
        }
    }

    private sealed class BackgroundCaptureOwner(CaptureCore core) :
        CaptureLease(core);
}
