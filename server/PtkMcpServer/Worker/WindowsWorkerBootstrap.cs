using System.ComponentModel;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PtkMcpServer.Worker;

internal sealed class WorkerBootstrapException : Exception
{
    internal WorkerBootstrapException(string detailCode, Exception? innerException = null)
        : base("Managed worker bootstrap failed.", innerException)
    {
        DetailCode = detailCode;
    }

    internal string DetailCode { get; }
}

internal readonly record struct WorkerBootstrapValues(
    string? RequestHandle,
    string? EventHandle);

internal interface IWorkerBootstrapEnvironmentSource
{
    string? Get(string variable);
    void Remove(string variable);
}

internal interface IWorkerBootstrapHandle : IDisposable;

internal interface IWindowsWorkerBootstrapNative
{
    IWorkerBootstrapHandle OwnInherited(nuint handleValue);

    IWorkerBootstrapHandle Duplicate(
        IWorkerBootstrapHandle source,
        uint desiredAccess,
        bool inheritHandle,
        uint options);

    bool IsPipe(IWorkerBootstrapHandle handle);
    bool IsInheritable(IWorkerBootstrapHandle handle);

    // A successful call transfers handle ownership to the returned stream.
    Stream CreateStream(IWorkerBootstrapHandle handle, FileAccess access);
}

internal interface IWorkerBootstrapStreams : IDisposable
{
    Stream RequestStream { get; }
    Stream EventStream { get; }
}

internal static class WorkerBootstrapCapture
{
    internal static WorkerBootstrapValues CaptureAndRemove(
        IWorkerBootstrapEnvironmentSource? source = null)
    {
        source ??= ProcessEnvironmentSource.Instance;
        string? requestHandle = null;
        string? eventHandle = null;
        Exception? captureFailure = null;
        Exception? removalFailure = null;

        Capture(
            () => source.Get(WorkerBootstrapEnvironment.RequestHandle),
            value => requestHandle = value,
            ref captureFailure);
        Capture(
            () => source.Get(WorkerBootstrapEnvironment.EventHandle),
            value => eventHandle = value,
            ref captureFailure);
        Attempt(
            () => source.Remove(WorkerBootstrapEnvironment.RequestHandle),
            ref removalFailure);
        Attempt(
            () => source.Remove(WorkerBootstrapEnvironment.EventHandle),
            ref removalFailure);

        if (removalFailure is not null)
        {
            throw new WorkerBootstrapException(
                "environment_removal_failed",
                removalFailure);
        }
        if (captureFailure is not null)
            throw new WorkerBootstrapException("bootstrap_failure", captureFailure);

        return new WorkerBootstrapValues(requestHandle, eventHandle);
    }

    private static void Capture(
        Func<string?> get,
        Action<string?> assign,
        ref Exception? failure)
    {
        try
        {
            assign(get());
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure ??= exception;
        }
    }

    private static void Attempt(Action action, ref Exception? failure)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure ??= exception;
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed class ProcessEnvironmentSource : IWorkerBootstrapEnvironmentSource
    {
        internal static ProcessEnvironmentSource Instance { get; } = new();

        public string? Get(string variable) =>
            Environment.GetEnvironmentVariable(variable);

        public void Remove(string variable) =>
            Environment.SetEnvironmentVariable(variable, null);
    }
}

internal static class WindowsWorkerBootstrap
{
    internal const uint DuplicateSameAccess = 0x00000002;

    internal static IWorkerBootstrapStreams Open(
        WorkerBootstrapValues values,
        IWindowsWorkerBootstrapNative? native = null,
        Func<bool>? isWindows = null,
        int? pointerSize = null)
    {
        if (!(isWindows ?? OperatingSystem.IsWindows)())
            throw new WorkerBootstrapException("platform_unsupported");

        var size = pointerSize ?? IntPtr.Size;
        var requestValue = ParseHandle(values.RequestHandle, size);
        var eventValue = ParseHandle(values.EventHandle, size);
        if (requestValue == eventValue)
            throw new WorkerBootstrapException("handle_alias");

        native ??= new WindowsWorkerBootstrapNative();
        IWorkerBootstrapHandle? requestOriginal = null;
        IWorkerBootstrapHandle? eventOriginal = null;
        IWorkerBootstrapHandle? requestDuplicate = null;
        IWorkerBootstrapHandle? eventDuplicate = null;
        Stream? requestStream = null;
        Stream? eventStream = null;
        try
        {
            requestOriginal = InvokeBoundary(
                "bootstrap_failure",
                () => native.OwnInherited(requestValue));
            eventOriginal = InvokeBoundary(
                "bootstrap_failure",
                () => native.OwnInherited(eventValue));

            requestDuplicate = InvokeBoundary(
                "handle_duplication_failed",
                () => native.Duplicate(
                    requestOriginal,
                    desiredAccess: 0,
                    inheritHandle: false,
                    options: DuplicateSameAccess));
            eventDuplicate = InvokeBoundary(
                "handle_duplication_failed",
                () => native.Duplicate(
                    eventOriginal,
                    desiredAccess: 0,
                    inheritHandle: false,
                    options: DuplicateSameAccess));

            DisposeRequired(ref requestOriginal);
            DisposeRequired(ref eventOriginal);

            RequirePipe(native, requestDuplicate);
            RequirePipe(native, eventDuplicate);
            RequireNotInheritable(native, requestDuplicate);
            RequireNotInheritable(native, eventDuplicate);

            requestStream = InvokeBoundary(
                "stream_creation_failed",
                () => native.CreateStream(requestDuplicate, FileAccess.Read));
            requestDuplicate = null;
            eventStream = InvokeBoundary(
                "stream_creation_failed",
                () => native.CreateStream(eventDuplicate, FileAccess.Write));
            eventDuplicate = null;

            var owner = new BootstrapOwner(requestStream, eventStream);
            requestStream = null;
            eventStream = null;
            return owner;
        }
        finally
        {
            DisposeIgnoringFailure(requestStream);
            DisposeIgnoringFailure(eventStream);
            DisposeIgnoringFailure(requestDuplicate);
            DisposeIgnoringFailure(eventDuplicate);
            DisposeIgnoringFailure(requestOriginal);
            DisposeIgnoringFailure(eventOriginal);
        }
    }

    internal static nuint ParseHandle(string? value, int pointerSize)
    {
        if (value is null)
            throw new WorkerBootstrapException("handle_missing");
        if (value.Length == 0 || value[0] == '0')
            throw new WorkerBootstrapException("handle_invalid");
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
                throw new WorkerBootstrapException("handle_invalid");
        }
        if (!ulong.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new WorkerBootstrapException("handle_invalid");
        }

        var maximum = pointerSize switch
        {
            4 => uint.MaxValue,
            8 => ulong.MaxValue,
            _ => throw new ArgumentOutOfRangeException(nameof(pointerSize)),
        };
        if (parsed == 0 || parsed >= maximum)
            throw new WorkerBootstrapException("handle_invalid");

        return checked((nuint)parsed);
    }

    private static void RequirePipe(
        IWindowsWorkerBootstrapNative native,
        IWorkerBootstrapHandle handle)
    {
        var isPipe = InvokeBoundary("handle_invalid", () => native.IsPipe(handle));
        if (!isPipe)
            throw new WorkerBootstrapException("handle_not_pipe");
    }

    private static void RequireNotInheritable(
        IWindowsWorkerBootstrapNative native,
        IWorkerBootstrapHandle handle)
    {
        var inheritable = InvokeBoundary(
            "handle_invalid",
            () => native.IsInheritable(handle));
        if (inheritable)
            throw new WorkerBootstrapException("handle_inheritable");
    }

    private static T InvokeBoundary<T>(string detailCode, Func<T> action)
    {
        try
        {
            return action() ?? throw new InvalidOperationException(
                "Worker bootstrap boundary returned null.");
        }
        catch (WorkerBootstrapException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new WorkerBootstrapException(detailCode, exception);
        }
    }

    private static void DisposeRequired(ref IWorkerBootstrapHandle? handle)
    {
        var owned = handle;
        handle = null;
        if (owned is null) return;
        try
        {
            owned.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new WorkerBootstrapException("bootstrap_failure", exception);
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
            // Preserve the acquisition or validation failure after attempting
            // every remaining cleanup boundary.
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed class BootstrapOwner(
        Stream requestStream,
        Stream eventStream) : IWorkerBootstrapStreams
    {
        private Stream? _requestStream = requestStream;
        private Stream? _eventStream = eventStream;

        public Stream RequestStream => Volatile.Read(ref _requestStream) ??
            throw new ObjectDisposedException(nameof(BootstrapOwner));

        public Stream EventStream => Volatile.Read(ref _eventStream) ??
            throw new ObjectDisposedException(nameof(BootstrapOwner));

        public void Dispose()
        {
            var request = Interlocked.Exchange(ref _requestStream, null);
            var events = Interlocked.Exchange(ref _eventStream, null);
            Exception? failure = null;
            try
            {
                request?.Dispose();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                failure = exception;
            }
            try
            {
                events?.Dispose();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                failure ??= exception;
            }
            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

internal sealed class WindowsWorkerBootstrapNative : IWindowsWorkerBootstrapNative
{
    private const uint FileTypePipe = 0x00000003;
    private const uint HandleFlagInherit = 0x00000001;

    public IWorkerBootstrapHandle OwnInherited(nuint handleValue) =>
        new BootstrapHandle(
            new SafeFileHandle((IntPtr)(nint)handleValue, ownsHandle: true));

    public IWorkerBootstrapHandle Duplicate(
        IWorkerBootstrapHandle source,
        uint desiredAccess,
        bool inheritHandle,
        uint options)
    {
        var sourceHandle = Require(source).Borrow();
        var currentProcess = NativeMethods.GetCurrentProcess();
        SafeFileHandle? duplicate = null;
        try
        {
            if (!NativeMethods.DuplicateHandleForBootstrap(
                    currentProcess,
                    sourceHandle,
                    currentProcess,
                    out duplicate,
                    desiredAccess,
                    inheritHandle,
                    options))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var owned = new BootstrapHandle(duplicate);
            duplicate = null;
            return owned;
        }
        finally
        {
            duplicate?.Dispose();
        }
    }

    public bool IsPipe(IWorkerBootstrapHandle handle) =>
        NativeMethods.GetFileType(Require(handle).Borrow()) == FileTypePipe;

    public bool IsInheritable(IWorkerBootstrapHandle handle)
    {
        if (!NativeMethods.GetHandleInformation(Require(handle).Borrow(), out var flags))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (flags & HandleFlagInherit) != 0;
    }

    public Stream CreateStream(IWorkerBootstrapHandle handle, FileAccess access)
    {
        if (access is not FileAccess.Read and not FileAccess.Write)
            throw new ArgumentOutOfRangeException(nameof(access));
        var safeHandle = Require(handle).Take();
        try
        {
            var stream = new FileStream(
                safeHandle,
                access,
                bufferSize: 4096,
                isAsync: false);
            safeHandle = null;
            return stream;
        }
        finally
        {
            safeHandle?.Dispose();
        }
    }

    private static BootstrapHandle Require(IWorkerBootstrapHandle handle) =>
        handle as BootstrapHandle ?? throw new ArgumentException(
            "Bootstrap handle did not originate from the Windows native provider.",
            nameof(handle));

    private sealed class BootstrapHandle(SafeFileHandle handle) : IWorkerBootstrapHandle
    {
        private SafeFileHandle? _handle = handle;

        internal SafeFileHandle Borrow() => Volatile.Read(ref _handle) ??
            throw new ObjectDisposedException(nameof(BootstrapHandle));

        internal SafeFileHandle Take() =>
            Interlocked.Exchange(ref _handle, null) ??
            throw new ObjectDisposedException(nameof(BootstrapHandle));

        public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandleForBootstrap(
            IntPtr sourceProcessHandle,
            SafeFileHandle sourceHandle,
            IntPtr targetProcessHandle,
            out SafeFileHandle targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetFileType(SafeFileHandle handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetHandleInformation(
            SafeFileHandle handle,
            out uint flags);
    }
}
