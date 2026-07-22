using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Package;
using PtkMcpGuardian.Standalone;

namespace PtkMcpGuardian.Host;

/// <summary>
/// The platform containment boundary for one private-host process. A launcher
/// may return ProvedNoChild only when no process or containment authority was
/// created. After creation becomes possible it must return an owned process,
/// even when release or later launch setup fails.
/// </summary>
internal interface IPrivateHostProcessLauncher
{
    PrivateHostProcessLaunchResult Launch(PrivateHostLaunchCommand command);
}

/// <summary>
/// Platform-owned process and outer-containment authority. Completion tasks
/// are notifications and must not fault; platform failures remain represented
/// by an incomplete containment proof.
/// </summary>
internal interface IPrivateHostLaunchedProcess : IDisposable
{
    int ProcessId { get; }

    Task Exited { get; }

    Task ContainmentConfirmed { get; }

    void BeginContainment(GuardianHostContainmentDeadline deadline);
}

internal sealed class PrivateHostProcessLaunchResult
{
    internal PrivateHostProcessLaunchResult(
        GuardianHostLaunchOutcome outcome,
        IPrivateHostLaunchedProcess? process)
    {
        if (!Enum.IsDefined(outcome))
        {
            process?.Dispose();
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        var consistent = outcome switch
        {
            GuardianHostLaunchOutcome.Started => process is not null,
            GuardianHostLaunchOutcome.ProvedNoChild => process is null,
            _ => false,
        };
        if (!consistent)
        {
            process?.Dispose();
            throw new ArgumentException(
                "The launch outcome does not match its process authority.",
                nameof(process));
        }
        if (process is not null &&
            (process.ProcessId <= 0 || process.Exited is null ||
             process.ContainmentConfirmed is null))
        {
            process.Dispose();
            throw new ArgumentException(
                "The launched process authority is incomplete.",
                nameof(process));
        }

        Outcome = outcome;
        LaunchedHost = process;
    }

    internal GuardianHostLaunchOutcome Outcome { get; }

    internal IPrivateHostLaunchedProcess? LaunchedHost { get; }
}

/// <summary>
/// Creates one two-pipe private transport and delegates only the two child
/// handles to a platform launcher. Public stdin/stdout are never inputs to
/// this type and cannot enter the explicit inherited-handle list.
/// </summary>
internal sealed class PrivateHostAttemptFactory : IGuardianHostAttemptFactory
{
    private readonly MatchedPackageFacts _package;
    private readonly GuardianHostSupervisorPins _pins;
    private readonly IPrivateHostProcessLauncher _launcher;

    internal PrivateHostAttemptFactory(
        MatchedPackageFacts package,
        GuardianHostSupervisorPins pins,
        IPrivateHostProcessLauncher launcher)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    public IGuardianHostAttemptResources Prepare(
        GuardianHostIdentity identity,
        GuardianHostStartupDeadline startupDeadline)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _ = startupDeadline;

        AnonymousPipeServerStream? requestWrite = null;
        AnonymousPipeServerStream? eventRead = null;
        try
        {
            requestWrite = new AnonymousPipeServerStream(
                PipeDirection.Out,
                HandleInheritability.Inheritable);
            eventRead = new AnonymousPipeServerStream(
                PipeDirection.In,
                HandleInheritability.Inheritable);
            var command = new PrivateHostLaunchCommand(
                _package,
                _pins,
                identity,
                HandleValue(requestWrite.ClientSafePipeHandle),
                HandleValue(eventRead.ClientSafePipeHandle));
            var resources = new PrivateHostAttemptResources(
                requestWrite,
                eventRead,
                command,
                _launcher);
            requestWrite = null;
            eventRead = null;
            return resources;
        }
        finally
        {
            requestWrite?.Dispose();
            eventRead?.Dispose();
        }
    }

    private static nuint HandleValue(SafePipeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var value = handle.DangerousGetHandle();
        return IntPtr.Size == sizeof(long)
            ? unchecked((nuint)(ulong)value.ToInt64())
            : unchecked((nuint)(uint)value.ToInt32());
    }
}

internal sealed class PrivateHostAttemptResources :
    IGuardianHostConnectedAttemptResources
{
    private readonly object _sync = new();
    private readonly AnonymousPipeServerStream _requestWrite;
    private readonly AnonymousPipeServerStream _eventRead;
    private readonly PrivateHostLaunchCommand _command;
    private readonly IPrivateHostProcessLauncher _launcher;
    private readonly TaskCompletionSource _hostExited = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _containmentConfirmed = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private IPrivateHostLaunchedProcess? _process;
    private bool _launchClaimed;
    private bool _clientHandlesDisposed;
    private bool _transportClosed;
    private bool _containmentStarted;
    private bool _disposed;

    internal PrivateHostAttemptResources(
        AnonymousPipeServerStream requestWrite,
        AnonymousPipeServerStream eventRead,
        PrivateHostLaunchCommand command,
        IPrivateHostProcessLauncher launcher)
    {
        _requestWrite = requestWrite ?? throw new ArgumentNullException(nameof(requestWrite));
        _eventRead = eventRead ?? throw new ArgumentNullException(nameof(eventRead));
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    public Stream RequestStream => _requestWrite;

    public Stream EventStream => _eventRead;

    public int HostProcessId
    {
        get
        {
            lock (_sync)
                return _process?.ProcessId ?? throw new InvalidOperationException(
                    "The private host attempt has no started process.");
        }
    }

    public Task HostExited => _hostExited.Task;

    public Task ContainmentConfirmed => _containmentConfirmed.Task;

    public GuardianHostLaunchOutcome Launch()
    {
        lock (_sync)
        {
            ThrowIfDisposedLocked();
            if (_launchClaimed)
                throw new InvalidOperationException("The private host attempt can launch only once.");
            if (_transportClosed)
                throw new InvalidOperationException("The private host transport is already closed.");
            _launchClaimed = true;
        }

        PrivateHostProcessLaunchResult result;
        try
        {
            result = _launcher.Launch(_command) ??
                throw new InvalidOperationException("The platform launcher returned no result.");
        }
        finally
        {
            DisposeLocalClientHandles();
        }

        if (result.Outcome == GuardianHostLaunchOutcome.ProvedNoChild)
        {
            CloseTransport();
            _hostExited.TrySetResult();
            _containmentConfirmed.TrySetResult();
            return result.Outcome;
        }

        var process = result.LaunchedHost!;
        lock (_sync)
        {
            if (_disposed)
            {
                process.Dispose();
                throw new ObjectDisposedException(nameof(PrivateHostAttemptResources));
            }
            _process = process;
        }
        _ = RelayCompletionAsync(process.Exited, _hostExited);
        _ = RelayCompletionAsync(
            process.ContainmentConfirmed,
            _containmentConfirmed);
        return result.Outcome;
    }

    public void CloseTransport()
    {
        lock (_sync)
        {
            if (_transportClosed)
                return;
            _transportClosed = true;
        }

        DisposeLocalClientHandles();
        _requestWrite.Dispose();
        _eventRead.Dispose();
    }

    public void BeginContainment(GuardianHostContainmentDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        IPrivateHostLaunchedProcess? process;
        lock (_sync)
        {
            if (_containmentStarted)
                return;
            _containmentStarted = true;
            process = _process;
        }

        CloseTransport();
        if (process is null)
        {
            _hostExited.TrySetResult();
            _containmentConfirmed.TrySetResult();
            return;
        }
        process.BeginContainment(deadline);
    }

    public void Dispose()
    {
        IPrivateHostLaunchedProcess? process;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            process = _process;
            _process = null;
        }

        CloseTransport();
        process?.Dispose();
    }

    private void DisposeLocalClientHandles()
    {
        lock (_sync)
        {
            if (_clientHandlesDisposed)
                return;
            _requestWrite.DisposeLocalCopyOfClientHandle();
            _eventRead.DisposeLocalCopyOfClientHandle();
            _clientHandlesDisposed = true;
        }
    }

    private void ThrowIfDisposedLocked()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PrivateHostAttemptResources));
    }

    private static async Task RelayCompletionAsync(
        Task source,
        TaskCompletionSource destination)
    {
        try
        {
            await source.ConfigureAwait(false);
            destination.TrySetResult();
        }
        catch (Exception exception)
        {
            destination.TrySetException(exception);
        }
    }
}
