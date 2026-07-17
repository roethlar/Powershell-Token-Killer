using PtkMcpGuardian.Lifecycle;

namespace PtkMcpGuardian.Standalone.Fake;

internal sealed class R3FakeHostAttemptFactory : IGuardianHostAttemptFactory
{
    internal const int MaximumRetainedAttempts = 128;

    private static int s_nextHostProcessId = 10_000;

    private readonly object _sync = new();
    private readonly GuardianHostSupervisorPins _pins;
    private readonly R3FakeHostControl _control;
    private readonly R3FakeHostProfile _profile;
    private readonly int _streamCapacity;
    private readonly Action<byte[]>? _retiredBufferObserver;
    private readonly List<R3FakeHostAttemptResources> _attempts = [];

    internal R3FakeHostAttemptFactory(
        GuardianHostSupervisorPins pins,
        R3FakeHostControl? control = null,
        int streamCapacity = R3BoundedOneWayStream.DefaultCapacity,
        Action<byte[]>? retiredBufferObserver = null,
        R3FakeHostProfile? profile = null)
    {
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _control = control ?? new R3FakeHostControl();
        _profile = profile ?? R3FakeHostProfile.StrictDefault;
        if (streamCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(streamCapacity));
        _streamCapacity = streamCapacity;
        _retiredBufferObserver = retiredBufferObserver;
    }

    internal R3FakeHostControl Control => _control;

    internal R3FakeHostProfile Profile => _profile;

    internal IReadOnlyList<R3FakeHostAttemptResources> Attempts
    {
        get
        {
            lock (_sync)
                return _attempts.ToArray();
        }
    }

    public IGuardianHostAttemptResources Prepare(
        GuardianHostIdentity identity,
        GuardianHostStartupDeadline startupDeadline)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var plan = _control.TakeAttempt();
        if (plan.FailPrepare)
            throw new IOException("The R3 fake host failed before child creation.");

        var hostProcessId = Interlocked.Increment(ref s_nextHostProcessId);
        if (hostProcessId <= 0)
            throw new InvalidOperationException("Synthetic fake-host PID space was exhausted.");
        var resources = new R3FakeHostAttemptResources(
            identity,
            startupDeadline,
            _pins,
            _profile,
            hostProcessId,
            _control,
            plan,
            _streamCapacity,
            _retiredBufferObserver);
        lock (_sync)
        {
            if (_attempts.Count == MaximumRetainedAttempts)
                _attempts.RemoveAt(0);
            _attempts.Add(resources);
        }
        return resources;
    }
}

internal sealed class R3FakeHostAttemptResources : IGuardianHostConnectedAttemptResources
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _peerCancellation = new();
    private readonly TaskCompletionSource _hostExited = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _containmentConfirmed = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly R3FakeHostAttemptPlan _plan;

    private Exception? _peerFailure;
    private bool _launched;
    private bool _transportClosed;
    private bool _crashed;
    private bool _containmentStarted;
    private bool _disposed;
    private int _transportCloseCount;
    private int _crashCount;
    private int _containmentStartCount;
    private int _disposeCount;
    private int _peerCancellationDisposed;

    internal R3FakeHostAttemptResources(
        GuardianHostIdentity identity,
        GuardianHostStartupDeadline startupDeadline,
        GuardianHostSupervisorPins pins,
        R3FakeHostProfile profile,
        int hostProcessId,
        R3FakeHostControl control,
        R3FakeHostAttemptPlan plan,
        int streamCapacity,
        Action<byte[]>? retiredBufferObserver)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        StartupDeadline = startupDeadline;
        HostProcessId = hostProcessId;
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        RequestTransport = new R3BoundedOneWayStream(streamCapacity, retiredBufferObserver);
        EventTransport = new R3BoundedOneWayStream(streamCapacity, retiredBufferObserver);
        Peer = new R3FakeHostPeer(
            identity,
            pins ?? throw new ArgumentNullException(nameof(pins)),
            profile ?? throw new ArgumentNullException(nameof(profile)),
            hostProcessId,
            RequestTransport,
            EventTransport,
            control ?? throw new ArgumentNullException(nameof(control)),
            plan,
            retiredBufferObserver);
        ContainmentProofBarrier = new R3FakeHostBarrier();
        if (!plan.HoldContainmentProof)
            ContainmentProofBarrier.Release();
    }

    internal GuardianHostIdentity Identity { get; }

    internal GuardianHostStartupDeadline StartupDeadline { get; }

    internal R3BoundedOneWayStream RequestTransport { get; }

    internal R3BoundedOneWayStream EventTransport { get; }

    internal R3FakeHostPeer Peer { get; }

    internal R3FakeHostBarrier ContainmentProofBarrier { get; }

    internal Exception? PeerFailure
    {
        get { lock (_sync) return _peerFailure; }
    }

    internal int TransportCloseCount => Volatile.Read(ref _transportCloseCount);

    internal int CrashCount => Volatile.Read(ref _crashCount);

    internal int ContainmentStartCount => Volatile.Read(ref _containmentStartCount);

    internal int DisposeCount => Volatile.Read(ref _disposeCount);

    public Stream RequestStream => RequestTransport;

    public Stream EventStream => EventTransport;

    public int HostProcessId { get; }

    public Task HostExited => _hostExited.Task;

    public Task ContainmentConfirmed => _containmentConfirmed.Task;

    public GuardianHostLaunchOutcome Launch()
    {
        lock (_sync)
        {
            ThrowIfDisposedLocked();
            if (_launched)
                throw new InvalidOperationException("The fake attempt can launch only once.");
            if (_transportClosed)
                throw new InvalidOperationException("The fake attempt transport is already closed.");
            _launched = true;
        }

        if (_plan.LaunchOutcome == GuardianHostLaunchOutcome.ProvedNoChild)
        {
            EventTransport.CompleteWriting();
            RequestTransport.RejectWrites();
            _hostExited.TrySetResult();
            return GuardianHostLaunchOutcome.ProvedNoChild;
        }

        _ = ObservePeerAsync();
        if (_plan.ThrowOnLaunch)
            throw new IOException("The R3 fake launch failed after child creation became possible.");
        return GuardianHostLaunchOutcome.Started;
    }

    public void CloseTransport()
    {
        var completeWithoutLaunch = false;
        lock (_sync)
        {
            if (_transportClosed)
                return;
            _transportClosed = true;
            completeWithoutLaunch = !_launched;
            Interlocked.Increment(ref _transportCloseCount);
        }
        _peerCancellation.Cancel();
        RequestTransport.RejectWrites();
        EventTransport.RejectWrites();
        if (completeWithoutLaunch)
            _hostExited.TrySetResult();
    }

    internal void Crash()
    {
        lock (_sync)
        {
            if (_crashed)
                return;
            _crashed = true;
            Interlocked.Increment(ref _crashCount);
        }
        CloseTransport();
    }

    public void BeginContainment(GuardianHostContainmentDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        lock (_sync)
        {
            if (_containmentStarted)
                return;
            _containmentStarted = true;
            Interlocked.Increment(ref _containmentStartCount);
        }
        CloseTransport();
        _ = ConfirmContainmentAsync();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            Interlocked.Increment(ref _disposeCount);
        }

        CloseTransport();
        Peer.Dispose();
        RequestTransport.Dispose();
        EventTransport.Dispose();
        if (HostExited.IsCompleted)
        {
            DisposePeerCancellation();
        }
        else
        {
            _ = HostExited.ContinueWith(
                static (_, state) =>
                    ((R3FakeHostAttemptResources)state!).DisposePeerCancellation(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task ObservePeerAsync()
    {
        try
        {
            await Peer.RunAsync(_peerCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            lock (_sync)
                _peerFailure = exception;
        }
        finally
        {
            _hostExited.TrySetResult();
        }
    }

    private async Task ConfirmContainmentAsync()
    {
        await HostExited.ConfigureAwait(false);
        Peer.Dispose();
        RequestTransport.Dispose();
        EventTransport.Dispose();
        await ContainmentProofBarrier.ReachAndWaitAsync(CancellationToken.None)
            .ConfigureAwait(false);
        _containmentConfirmed.TrySetResult();
    }

    private void ThrowIfDisposedLocked()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(R3FakeHostAttemptResources));
    }

    private static bool IsFatalRuntimeException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private void DisposePeerCancellation()
    {
        if (Interlocked.Exchange(ref _peerCancellationDisposed, 1) == 0)
            _peerCancellation.Dispose();
    }
}
