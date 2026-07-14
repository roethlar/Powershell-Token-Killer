namespace PtkMcpServer.Worker;

internal enum WindowsProcessCreationMode
{
    Runnable,
    SuspendedForContainmentProof,
}

internal interface IWindowsWorkerNative
{
    IWindowsJobHandle CreateUnnamedJob();
    void SetJobLimitFlags(IWindowsJobHandle job, uint limitFlags);
    uint QueryJobLimitFlags(IWindowsJobHandle job);
    IWindowsWorkerPipeSet CreateWorkerPipeSet();
    IWindowsProcessHandle CreateProcessInJob(
        WorkerLaunchCommand command,
        IWindowsJobHandle job,
        IWindowsWorkerPipeSet pipes,
        WindowsProcessCreationMode mode);
    bool IsProcessInJob(IWindowsProcessHandle process, IWindowsJobHandle job);
    void ResumePrimaryThreadForContainmentProof(IWindowsProcessHandle process);
}

internal interface IWindowsJobHandle : IDisposable;

internal interface IWindowsProcessHandle : IDisposable
{
    int ProcessId { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken = default);
}

internal interface IWindowsWorkerPipeSet : IDisposable
{
    int ChildHandleCount { get; }
    Stream RequestWriter { get; }
    Stream EventReader { get; }
    Stream StandardOutputReader { get; }
    Stream StandardErrorReader { get; }
    void CloseChildEnds();
}

internal sealed class WindowsProcessTreeSupervisor
{
    internal const uint KillOnJobClose = 0x00002000;

    private const int RequiredChildHandleCount = 5;
    private readonly IWindowsWorkerNative _native;
    private readonly Func<bool> _isWindows;

    internal WindowsProcessTreeSupervisor()
        : this(new WindowsWorkerNative())
    {
    }

    internal WindowsProcessTreeSupervisor(
        IWindowsWorkerNative native,
        Func<bool>? isWindows = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _isWindows = isWindows ?? OperatingSystem.IsWindows;
    }

    internal ContainedWindowsWorker Launch(
        WorkerLaunchCommand command,
        CancellationToken cancellationToken = default) =>
        Launch(command, WindowsProcessCreationMode.Runnable, cancellationToken);

    internal ContainedWindowsWorker Launch(
        WorkerLaunchCommand command,
        WindowsProcessCreationMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (mode is not WindowsProcessCreationMode.Runnable and
            not WindowsProcessCreationMode.SuspendedForContainmentProof)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (!_isWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows worker containment requires Windows 10 or Windows Server 2016 or newer.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        IWindowsJobHandle? job = null;
        IWindowsWorkerPipeSet? pipes = null;
        IWindowsProcessHandle? process = null;
        try
        {
            job = InvokeStage(
                "containment_setup_failed",
                WorkerLaunchStage.CreateJob,
                () => _native.CreateUnnamedJob() ??
                    throw new InvalidOperationException("Native job creation returned no job handle."));
            InvokeStage(
                "containment_setup_failed",
                WorkerLaunchStage.ConfigureJob,
                () => _native.SetJobLimitFlags(job, KillOnJobClose));
            var configuredFlags = InvokeStage(
                "containment_setup_failed",
                WorkerLaunchStage.QueryJob,
                () => _native.QueryJobLimitFlags(job));
            if (configuredFlags != KillOnJobClose)
            {
                throw new WorkerLaunchException(
                    "containment_setup_failed",
                    WorkerLaunchStage.QueryJob);
            }

            cancellationToken.ThrowIfCancellationRequested();
            pipes = InvokeStage(
                "containment_setup_failed",
                WorkerLaunchStage.CreatePipe,
                () => _native.CreateWorkerPipeSet() ??
                    throw new InvalidOperationException("Native pipe creation returned no pipe set."));
            var childHandleCount = InvokeStage(
                "containment_setup_failed",
                WorkerLaunchStage.CreatePipe,
                () => pipes.ChildHandleCount);
            if (childHandleCount != RequiredChildHandleCount)
            {
                throw new WorkerLaunchException(
                    "containment_setup_failed",
                    WorkerLaunchStage.CreatePipe);
            }

            cancellationToken.ThrowIfCancellationRequested();
            process = InvokeStage(
                "worker_create_failed",
                WorkerLaunchStage.CreateProcess,
                () => _native.CreateProcessInJob(command, job, pipes, mode) ??
                    throw new InvalidOperationException("Native process creation returned no process handle."));
            InvokeStage(
                "containment_setup_failed",
                WorkerLaunchStage.CloseChildHandles,
                pipes.CloseChildEnds);

            var containedAtCreation = InvokeStage(
                "containment_verification_failed",
                WorkerLaunchStage.VerifyContainment,
                () => _native.IsProcessInJob(process, job));
            if (!containedAtCreation)
            {
                throw new WorkerLaunchException(
                    "containment_verification_failed",
                    WorkerLaunchStage.VerifyContainment);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var contained = new ContainedWindowsWorker(_native, job, process, pipes, mode);
            job = null;
            process = null;
            pipes = null;
            return contained;
        }
        catch
        {
            // Closing the sole job handle is the first rollback action after a
            // process exists. That contains the entire tree before ordinary
            // process/stream handle cleanup, including when cleanup itself fails.
            DisposeIgnoringFailure(job);
            DisposeIgnoringFailure(process);
            DisposeIgnoringFailure(pipes);
            throw;
        }
    }

    private static void DisposeIgnoringFailure(IDisposable? value)
    {
        try
        {
            value?.Dispose();
        }
        catch
        {
            // Preserve the launch-stage failure after attempting every cleanup.
        }
    }

    private static T InvokeStage<T>(
        string detailCode,
        WorkerLaunchStage stage,
        Func<T> action)
    {
        try
        {
            return action();
        }
        catch (WorkerLaunchException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new WorkerLaunchException(detailCode, stage, innerException: exception);
        }
    }

    private static void InvokeStage(
        string detailCode,
        WorkerLaunchStage stage,
        Action action) =>
        InvokeStage(
            detailCode,
            stage,
            () =>
            {
                action();
                return true;
            });

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;
}

internal sealed class ContainedWindowsWorker : IDisposable
{
    private Ownership? _ownership;

    internal ContainedWindowsWorker(
        IWindowsWorkerNative native,
        IWindowsJobHandle job,
        IWindowsProcessHandle process,
        IWindowsWorkerPipeSet pipes,
        WindowsProcessCreationMode mode)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(pipes);
        _ownership = new Ownership(native, job, process, pipes, mode);
    }

    internal int ProcessId => Current.Process.ProcessId;
    internal Stream RequestWriter => Current.Pipes.RequestWriter;
    internal Stream EventReader => Current.Pipes.EventReader;
    internal Stream StandardOutputReader => Current.Pipes.StandardOutputReader;
    internal Stream StandardErrorReader => Current.Pipes.StandardErrorReader;

    internal Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        Current.Process.WaitForExitAsync(cancellationToken);

    internal void ResumeForContainmentProof()
    {
        var ownership = Current;
        if (ownership.Mode != WindowsProcessCreationMode.SuspendedForContainmentProof)
        {
            throw new InvalidOperationException(
                "Only a containment-proof worker may be resumed explicitly.");
        }

        if (Interlocked.CompareExchange(ref ownership.ResumeAttempted, 1, 0) != 0)
            throw new InvalidOperationException("The containment-proof worker resume is one-shot.");

        try
        {
            ownership.Native.ResumePrimaryThreadForContainmentProof(ownership.Process);
        }
        catch (WorkerLaunchException)
        {
            // Resume is intentionally not retryable. Kill the still-contained
            // process tree before releasing its remaining handles.
            DisposeIgnoringFailure();
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            var failure = new WorkerLaunchException(
                "containment_resume_failed",
                WorkerLaunchStage.ResumePrimaryThread,
                innerException: exception);
            DisposeIgnoringFailure();
            throw failure;
        }
    }

    public void Dispose()
    {
        var ownership = Interlocked.Exchange(ref _ownership, null);
        if (ownership is null)
            return;

        List<Exception>? failures = null;
        DisposeAndCapture(ownership.Job, ref failures);
        DisposeAndCapture(ownership.Process, ref failures);
        DisposeAndCapture(ownership.Pipes, ref failures);

        if (failures is { Count: 1 })
            throw failures[0];
        if (failures is { Count: > 1 })
            throw new AggregateException("Contained worker cleanup failed.", failures);
    }

    private void DisposeIgnoringFailure()
    {
        try
        {
            Dispose();
        }
        catch
        {
            // Preserve the resume-stage failure after attempting every cleanup.
        }
    }

    private Ownership Current => Volatile.Read(ref _ownership) ??
        throw new ObjectDisposedException(nameof(ContainedWindowsWorker));

    private static void DisposeAndCapture(IDisposable value, ref List<Exception>? failures)
    {
        try
        {
            value.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed class Ownership(
        IWindowsWorkerNative native,
        IWindowsJobHandle job,
        IWindowsProcessHandle process,
        IWindowsWorkerPipeSet pipes,
        WindowsProcessCreationMode mode)
    {
        internal IWindowsWorkerNative Native { get; } = native;
        internal IWindowsJobHandle Job { get; } = job;
        internal IWindowsProcessHandle Process { get; } = process;
        internal IWindowsWorkerPipeSet Pipes { get; } = pipes;
        internal WindowsProcessCreationMode Mode { get; } = mode;
        internal int ResumeAttempted;
    }
}
