using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

/// <summary>
/// The event-only authority available to host runtime composition. Runtime
/// code can allocate a sequenced event at the shared serialization point, but
/// cannot write protocol responses or bypass frame ordering.
/// </summary>
internal interface IPrivateHostEventSink
{
    ValueTask WriteEventAsync(
        Func<HostEventSequence, GuardianHostEvent> createEvent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the host-to-guardian serialization point. All ordinary host frames
/// share one gate with event creation, so an event sequence is allocated only
/// after its exact wire position is owned.
/// </summary>
internal sealed class PrivateHostOutboundChannel : IPrivateHostEventSink
{
    private readonly SemaphoreSlim _serialization = new(1, 1);
    private readonly GuardianHostProtocolWriter _writer;
    private readonly PrivateHostServerIdentity _identity;
    private long _lastAllocatedEventSequence;

    internal PrivateHostOutboundChannel(
        Stream hostEventStream,
        PrivateHostServerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(hostEventStream);
        ArgumentNullException.ThrowIfNull(identity);
        if (!hostEventStream.CanWrite)
            throw new ArgumentException(
                "Host event stream must be writable.",
                nameof(hostEventStream));

        _writer = new GuardianHostProtocolWriter(
            hostEventStream,
            GuardianHostPeer.Host);
        _identity = identity;
    }

    internal async ValueTask WriteFrameAsync(
        GuardianHostMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is GuardianHostEvent)
        {
            throw new ArgumentException(
                "Host events require channel-owned sequence allocation.",
                nameof(message));
        }

        var acquired = false;
        try
        {
            await _serialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            cancellationToken.ThrowIfCancellationRequested();
            ValidateIdentity(message);
            await _writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
                _serialization.Release();
        }
    }

    internal async ValueTask WriteEventAsync(
        Func<HostEventSequence, GuardianHostEvent> createEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createEvent);

        var acquired = false;
        GuardianHostEvent? message = null;
        try
        {
            await _serialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            cancellationToken.ThrowIfCancellationRequested();

            var nextValue = checked(_lastAllocatedEventSequence + 1);
            _lastAllocatedEventSequence = nextValue;
            var assignedSequence = new HostEventSequence(nextValue);
            message = createEvent(assignedSequence) ?? throw Protocol(
                "outbound_event_factory_invalid",
                "Host event factory returned no event.");
            ValidateIdentity(message);
            if (message.EventSequence != assignedSequence)
            {
                throw Protocol(
                    "outbound_event_sequence_mismatch",
                    "Host event does not carry its channel-assigned sequence.");
            }

            await _writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (message is IDisposable disposable)
                disposable.Dispose();
            if (acquired)
                _serialization.Release();
        }
    }

    ValueTask IPrivateHostEventSink.WriteEventAsync(
        Func<HostEventSequence, GuardianHostEvent> createEvent,
        CancellationToken cancellationToken) =>
        WriteEventAsync(createEvent, cancellationToken);

    private void ValidateIdentity(GuardianHostMessage message)
    {
        if (message.Sender != GuardianHostPeer.Host ||
            message.GuardianBootId != _identity.GuardianBootId ||
            message.HostBootId != _identity.HostBootId ||
            message.HostGeneration != _identity.HostGeneration)
        {
            throw Protocol(
                "outbound_identity_mismatch",
                "Host outbound identity does not match this generation.");
        }
    }

    private static GuardianHostProtocolException Protocol(
        string detailCode,
        string message) => new(detailCode, message);
}
