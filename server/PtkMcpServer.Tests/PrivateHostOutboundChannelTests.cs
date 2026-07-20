using PtkMcpServer.GuardianHost;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class PrivateHostOutboundChannelTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly HostGeneration Generation = new(7);
    private static readonly PrivateHostServerIdentity Identity = new(
        Guardian,
        Host,
        Generation,
        hostPid: 4242);
    private static readonly CanonicalAlias Alias = new("default");
    private static readonly SessionTransitionVersion Transition = new(1);

    [Fact]
    public async Task Concurrent_events_receive_positive_monotonic_sequences_in_wire_order()
    {
        using var stream = new MemoryStream();
        var channel = new PrivateHostOutboundChannel(stream, Identity);

        var writes = Enumerable.Range(0, 64)
            .Select(index => Task.Run(async () =>
                await channel.WriteEventAsync(sequence => Event(
                    sequence,
                    Sha256Digest.Compute([(byte)index])))
                    .ConfigureAwait(false)))
            .ToArray();
        await Task.WhenAll(writes).WaitAsync(TestTimeout);

        var events = (await ReadAllAsync(stream)).Cast<WorkerCreateCapabilityRequestedEvent>().ToArray();
        Assert.Equal(64, events.Length);
        Assert.Equal(
            Enumerable.Range(1, 64).Select(value => (long)value),
            events.Select(value => value.EventSequence.Value));
        Assert.Equal(64, events.Select(value => value.BindingDigest).Distinct().Count());
    }

    [Fact]
    public async Task Event_factory_runs_only_after_the_channel_owns_serialization()
    {
        using var stream = new MemoryStream();
        var channel = new PrivateHostOutboundChannel(stream, Identity);
        using var firstFactoryEntered = new ManualResetEventSlim();
        using var releaseFirstFactory = new ManualResetEventSlim();
        var secondFactoryEntered = false;

        var first = Task.Run(async () =>
            await channel.WriteEventAsync(sequence =>
            {
                firstFactoryEntered.Set();
                if (!releaseFirstFactory.Wait(TestTimeout))
                    throw new TimeoutException("First event factory was not released.");
                return Event(sequence, '1');
            }).ConfigureAwait(false));

        Assert.True(firstFactoryEntered.Wait(TestTimeout));
        var second = channel.WriteEventAsync(sequence =>
        {
            secondFactoryEntered = true;
            return Event(sequence, '2');
        }).AsTask();
        var createdBeforeRelease = secondFactoryEntered;

        releaseFirstFactory.Set();
        await Task.WhenAll(first, second).WaitAsync(TestTimeout);

        Assert.False(createdBeforeRelease);
        Assert.True(secondFactoryEntered);
        var events = (await ReadAllAsync(stream)).Cast<GuardianHostEvent>().ToArray();
        Assert.Equal([1L, 2L], events.Select(value => value.EventSequence.Value));
    }

    [Fact]
    public async Task Cancellation_while_waiting_consumes_no_event_sequence()
    {
        using var stream = new MemoryStream();
        var channel = new PrivateHostOutboundChannel(stream, Identity);
        using var firstFactoryEntered = new ManualResetEventSlim();
        using var releaseFirstFactory = new ManualResetEventSlim();
        var canceledFactoryCalls = 0;

        var first = Task.Run(async () =>
            await channel.WriteEventAsync(sequence =>
            {
                firstFactoryEntered.Set();
                if (!releaseFirstFactory.Wait(TestTimeout))
                    throw new TimeoutException("First event factory was not released.");
                return Event(sequence, '1');
            }).ConfigureAwait(false));
        Assert.True(firstFactoryEntered.Wait(TestTimeout));

        using var cancellation = new CancellationTokenSource();
        var canceled = channel.WriteEventAsync(sequence =>
        {
            Interlocked.Increment(ref canceledFactoryCalls);
            return Event(sequence, '2');
        }, cancellation.Token).AsTask();
        Assert.False(canceled.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await canceled.ConfigureAwait(false));

        releaseFirstFactory.Set();
        await first.WaitAsync(TestTimeout);
        await channel.WriteEventAsync(sequence => Event(sequence, '3'));

        Assert.Equal(0, canceledFactoryCalls);
        var events = (await ReadAllAsync(stream)).Cast<GuardianHostEvent>().ToArray();
        Assert.Equal([1L, 2L], events.Select(value => value.EventSequence.Value));
    }

    [Fact]
    public async Task Wrong_identity_and_wrong_assigned_sequence_are_rejected_without_reuse()
    {
        using var stream = new MemoryStream();
        var channel = new PrivateHostOutboundChannel(stream, Identity);
        var wrongHost = new HostBootId(
            Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));

        var identityFailure = await Assert.ThrowsAsync<GuardianHostProtocolException>(async () =>
            await channel.WriteEventAsync(sequence => Event(
                sequence,
                '1',
                hostBootId: wrongHost)));
        Assert.Equal("outbound_identity_mismatch", identityFailure.DetailCode);

        var sequenceFailure = await Assert.ThrowsAsync<GuardianHostProtocolException>(async () =>
            await channel.WriteEventAsync(sequence => Event(
                new HostEventSequence(sequence.Value + 10),
                '2')));
        Assert.Equal("outbound_event_sequence_mismatch", sequenceFailure.DetailCode);

        long assigned = 0;
        await channel.WriteEventAsync(sequence =>
        {
            assigned = sequence.Value;
            return Event(sequence, '3');
        });

        Assert.Equal(3, assigned);
        var written = Assert.Single(await ReadAllAsync(stream));
        Assert.Equal(3, Assert.IsAssignableFrom<GuardianHostEvent>(written).EventSequence.Value);
    }

    [Fact]
    public async Task Ambiguous_writer_failure_is_latched_and_allocated_sequences_are_never_reused()
    {
        using var stream = new AmbiguousFailingStream();
        var channel = new PrivateHostOutboundChannel(stream, Identity);
        var assigned = new List<long>();

        await Assert.ThrowsAsync<IOException>(async () =>
            await channel.WriteEventAsync(sequence =>
            {
                assigned.Add(sequence.Value);
                return Event(sequence, '1');
            }));
        var terminal = await Assert.ThrowsAsync<GuardianHostProtocolException>(async () =>
            await channel.WriteEventAsync(sequence =>
            {
                assigned.Add(sequence.Value);
                return Event(sequence, '2');
            }));

        Assert.Equal("writer_faulted", terminal.DetailCode);
        Assert.IsType<IOException>(terminal.InnerException);
        Assert.Equal([1L, 2L], assigned);
        Assert.Equal(1, stream.WriteAttempts);
        Assert.Single(stream.PrefixBytes);
    }

    [Fact]
    public async Task Ordinary_host_frames_share_serialization_without_consuming_event_sequences()
    {
        using var stream = new MemoryStream();
        var channel = new PrivateHostOutboundChannel(stream, Identity);

        var responses = Enumerable.Range(1, 32)
            .Select(requestId => Task.Run(async () =>
                await channel.WriteFrameAsync(new GuardianHostSuccessResponse(
                    Guardian,
                    Host,
                    Generation,
                    new PrivateRequestId(requestId),
                    new ShutdownAccepted())).ConfigureAwait(false)))
            .ToArray();
        await Task.WhenAll(responses).WaitAsync(TestTimeout);
        long firstEventSequence = 0;
        await channel.WriteEventAsync(sequence =>
        {
            firstEventSequence = sequence.Value;
            return Event(sequence, 'f');
        });

        var frames = await ReadAllAsync(stream);
        Assert.Equal(33, frames.Count);
        Assert.Equal(32, frames.OfType<GuardianHostSuccessResponse>().Count());
        Assert.Single(frames.OfType<GuardianHostEvent>());
        Assert.Equal(1, firstEventSequence);
    }

    [Fact]
    public async Task Prebuilt_events_cannot_bypass_channel_sequence_ownership()
    {
        using var stream = new MemoryStream();
        var channel = new PrivateHostOutboundChannel(stream, Identity);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await channel.WriteFrameAsync(Event(new HostEventSequence(99), '1')));
        long assigned = 0;
        await channel.WriteEventAsync(sequence =>
        {
            assigned = sequence.Value;
            return Event(sequence, '2');
        });

        Assert.Equal(1, assigned);
    }

    private static WorkerCreateCapabilityRequestedEvent Event(
        HostEventSequence sequence,
        char digest,
        GuardianBootId? guardianBootId = null,
        HostBootId? hostBootId = null,
        HostGeneration? generation = null) => Event(
            sequence,
            Digest(digest),
            guardianBootId,
            hostBootId,
            generation);

    private static WorkerCreateCapabilityRequestedEvent Event(
        HostEventSequence sequence,
        Sha256Digest digest,
        GuardianBootId? guardianBootId = null,
        HostBootId? hostBootId = null,
        HostGeneration? generation = null) =>
        new(
            guardianBootId ?? Guardian,
            hostBootId ?? Host,
            generation ?? Generation,
            sequence,
            Alias,
            Transition,
            digest,
            startupDeadlineUnixTimeMilliseconds: 1);

    private static Sha256Digest Digest(char value) => new(new string(value, 64));

    private static async Task<List<GuardianHostMessage>> ReadAllAsync(MemoryStream stream)
    {
        stream.Position = 0;
        using var reader = new GuardianHostProtocolReader(stream, GuardianHostPeer.Host);
        var messages = new List<GuardianHostMessage>();
        while (await reader.ReadAsync() is { } message)
            messages.Add(message);
        return messages;
    }

    private sealed class AmbiguousFailingStream : Stream
    {
        private readonly List<byte> _prefixBytes = [];
        private int _writeAttempts;

        internal int WriteAttempts => Volatile.Read(ref _writeAttempts);

        internal IReadOnlyList<byte> PrefixBytes => _prefixBytes.AsReadOnly();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _writeAttempts);
            _prefixBytes.Add(buffer.Span[0]);
            return ValueTask.FromException(
                new IOException("Injected ambiguous outbound write failure."));
        }
    }
}
