using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PtkMcpServer.Worker;

namespace PtkMcpServer.GuardianHost;

internal static class PrivateHostBootstrapEnvironment
{
    internal const string RequestReadHandle = "PTK_HOST_REQUEST_READ_HANDLE";
    internal const string EventWriteHandle = "PTK_HOST_EVENT_WRITE_HANDLE";

    internal static readonly IReadOnlySet<string> ReservedHandleVariables =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RequestReadHandle,
            EventWriteHandle,
        };
}

internal sealed class PrivateHostBootstrapException : Exception
{
    internal PrivateHostBootstrapException(string detailCode)
        : base("Private host bootstrap failed.")
    {
        DetailCode = detailCode;
    }

    internal string DetailCode { get; }
}

internal interface IPrivateHostBootstrapEnvironmentSource
{
    string? Get(string variable);

    void Remove(string variable);
}

internal interface IPrivateHostBootstrapHandle : IDisposable
{
    // A successful call transfers the native handle to the returned stream.
    Stream OpenStream(FileAccess access);
}

internal interface IPrivateHostBootstrapNative
{
    IPrivateHostBootstrapHandle OwnInherited(nuint handleValue);
}

internal interface IPrivateHostBootstrapStreams : IDisposable
{
    Stream RequestReadStream { get; }

    Stream EventWriteStream { get; }
}

/// <summary>
/// Bounded, one-use ownership of the private host's two inherited protocol
/// handles. The raw values are never exposed after capture.
/// </summary>
internal sealed class PrivateHostBootstrapValues : IDisposable
{
    private IPrivateHostBootstrapHandle? _requestReadHandle;
    private IPrivateHostBootstrapHandle? _eventWriteHandle;
    private int _claimed;

    internal PrivateHostBootstrapValues(
        IPrivateHostBootstrapHandle requestReadHandle,
        IPrivateHostBootstrapHandle eventWriteHandle)
    {
        _requestReadHandle = requestReadHandle ?? throw new ArgumentNullException(
            nameof(requestReadHandle));
        _eventWriteHandle = eventWriteHandle ?? throw new ArgumentNullException(
            nameof(eventWriteHandle));
    }

    internal IPrivateHostBootstrapStreams OpenStreams()
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            throw new PrivateHostBootstrapException("bootstrap_consumed");

        var requestHandle = Interlocked.Exchange(ref _requestReadHandle, null) ??
            throw new PrivateHostBootstrapException("bootstrap_consumed");
        var eventHandle = Interlocked.Exchange(ref _eventWriteHandle, null) ??
            throw new PrivateHostBootstrapException("bootstrap_consumed");
        Stream? requestStream = null;
        Stream? eventStream = null;
        try
        {
            requestStream = OpenStream(requestHandle, FileAccess.Read);
            eventStream = OpenStream(eventHandle, FileAccess.Write);

            var owner = new PrivateHostBootstrapStreamOwner(
                requestStream,
                eventStream);
            requestStream = null;
            eventStream = null;
            return owner;
        }
        finally
        {
            DisposeIgnoringFailure(requestStream);
            DisposeIgnoringFailure(eventStream);
            DisposeIgnoringFailure(requestHandle);
            DisposeIgnoringFailure(eventHandle);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _claimed, 1) != 0)
            return;

        var requestHandle = Interlocked.Exchange(ref _requestReadHandle, null);
        var eventHandle = Interlocked.Exchange(ref _eventWriteHandle, null);
        Exception? failure = null;
        DisposeRequired(requestHandle, ref failure);
        DisposeRequired(eventHandle, ref failure);
        if (failure is not null)
            throw new PrivateHostBootstrapException("handle_cleanup_failed");
    }

    private static Stream OpenStream(
        IPrivateHostBootstrapHandle handle,
        FileAccess access)
    {
        try
        {
            return handle.OpenStream(access) ?? throw new InvalidOperationException();
        }
        catch (PrivateHostBootstrapException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new PrivateHostBootstrapException("stream_creation_failed");
        }
    }

    private static void DisposeRequired(
        IDisposable? value,
        ref Exception? failure)
    {
        try
        {
            value?.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure ??= exception;
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
            // Preserve the acquisition/open failure after attempting every
            // remaining ownership cleanup boundary.
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed class PrivateHostBootstrapStreamOwner(
        Stream requestReadStream,
        Stream eventWriteStream) : IPrivateHostBootstrapStreams
    {
        private Stream? _requestReadStream = requestReadStream;
        private Stream? _eventWriteStream = eventWriteStream;

        public Stream RequestReadStream => Volatile.Read(ref _requestReadStream) ??
            throw new ObjectDisposedException(nameof(PrivateHostBootstrapStreamOwner));

        public Stream EventWriteStream => Volatile.Read(ref _eventWriteStream) ??
            throw new ObjectDisposedException(nameof(PrivateHostBootstrapStreamOwner));

        public void Dispose()
        {
            var request = Interlocked.Exchange(ref _requestReadStream, null);
            var events = Interlocked.Exchange(ref _eventWriteStream, null);
            Exception? failure = null;
            DisposeRequired(request, ref failure);
            DisposeRequired(events, ref failure);
            if (failure is not null)
                throw new PrivateHostBootstrapException("stream_cleanup_failed");
        }
    }
}

internal static class PrivateHostBootstrapCapture
{
    internal static PrivateHostBootstrapValues CaptureAndRemove(
        IPrivateHostBootstrapEnvironmentSource? source = null,
        IPrivateHostBootstrapNative? native = null,
        int? pointerSize = null)
    {
        source ??= ProcessEnvironmentSource.Instance;
        native ??= PrivateHostBootstrapNative.Instance;
        string? requestValue = null;
        string? eventValue = null;
        Exception? captureFailure = null;
        Exception? removalFailure = null;

        Capture(
            () => source.Get(PrivateHostBootstrapEnvironment.RequestReadHandle),
            value => requestValue = value,
            ref captureFailure);
        Capture(
            () => source.Get(PrivateHostBootstrapEnvironment.EventWriteHandle),
            value => eventValue = value,
            ref captureFailure);
        Attempt(
            () => source.Remove(PrivateHostBootstrapEnvironment.RequestReadHandle),
            ref removalFailure);
        Attempt(
            () => source.Remove(PrivateHostBootstrapEnvironment.EventWriteHandle),
            ref removalFailure);

        if (removalFailure is not null)
            throw new PrivateHostBootstrapException("environment_removal_failed");
        if (captureFailure is not null)
            throw new PrivateHostBootstrapException("environment_capture_failed");

        var size = pointerSize ?? IntPtr.Size;
        var requestHandle = ParseHandle(requestValue, size);
        var eventHandle = ParseHandle(eventValue, size);
        if (requestHandle == eventHandle)
            throw new PrivateHostBootstrapException("handle_alias");

        IPrivateHostBootstrapHandle? ownedRequest = null;
        IPrivateHostBootstrapHandle? ownedEvent = null;
        try
        {
            ownedRequest = Own(native, requestHandle);
            ownedEvent = Own(native, eventHandle);
            var values = new PrivateHostBootstrapValues(ownedRequest, ownedEvent);
            ownedRequest = null;
            ownedEvent = null;
            return values;
        }
        finally
        {
            DisposeIgnoringFailure(ownedRequest);
            DisposeIgnoringFailure(ownedEvent);
        }
    }

    internal static nuint ParseHandle(string? value, int pointerSize)
    {
        if (value is null)
            throw new PrivateHostBootstrapException("handle_missing");
        if (value.Length == 0 || value[0] == '0')
            throw new PrivateHostBootstrapException("handle_invalid");
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
                throw new PrivateHostBootstrapException("handle_invalid");
        }
        if (!ulong.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new PrivateHostBootstrapException("handle_invalid");
        }

        var maximum = pointerSize switch
        {
            4 => uint.MaxValue,
            8 => ulong.MaxValue,
            _ => throw new ArgumentOutOfRangeException(nameof(pointerSize)),
        };
        if (parsed == 0 || parsed >= maximum)
            throw new PrivateHostBootstrapException("handle_invalid");

        return checked((nuint)parsed);
    }

    private static IPrivateHostBootstrapHandle Own(
        IPrivateHostBootstrapNative native,
        nuint handleValue)
    {
        try
        {
            return native.OwnInherited(handleValue) ??
                throw new InvalidOperationException();
        }
        catch (PrivateHostBootstrapException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new PrivateHostBootstrapException("handle_ownership_failed");
        }
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

    private static void DisposeIgnoringFailure(IDisposable? value)
    {
        try
        {
            value?.Dispose();
        }
        catch
        {
            // Preserve the capture/ownership failure after attempting cleanup.
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed class ProcessEnvironmentSource : IPrivateHostBootstrapEnvironmentSource
    {
        internal static ProcessEnvironmentSource Instance { get; } = new();

        public string? Get(string variable) =>
            Environment.GetEnvironmentVariable(variable);

        public void Remove(string variable) =>
            Environment.SetEnvironmentVariable(variable, null);
    }
}

/// <summary>
/// Concrete process bootstrap boundary for the exact private roles. It remains
/// unwired until the private role cutover composes it in Program.
/// </summary>
internal sealed class PrivateProcessBootstrapBoundary :
    IPrivateProcessBootstrapBoundary<PrivateHostBootstrapValues, WorkerBootstrapValues>
{
    private readonly IPrivateHostBootstrapEnvironmentSource _source;
    private readonly IPrivateHostBootstrapNative? _native;
    private readonly int? _pointerSize;

    internal PrivateProcessBootstrapBoundary(
        IPrivateHostBootstrapEnvironmentSource? source = null,
        IPrivateHostBootstrapNative? native = null,
        int? pointerSize = null)
    {
        _source = source ?? ProcessEnvironmentSource.Instance;
        _native = native;
        _pointerSize = pointerSize;
    }

    public PrivateHostBootstrapValues CaptureAndRemoveHost() =>
        PrivateHostBootstrapCapture.CaptureAndRemove(
            _source,
            _native,
            _pointerSize);

    public WorkerBootstrapValues CaptureAndRemoveWorker() =>
        WorkerBootstrapCapture.CaptureAndRemove(new WorkerEnvironmentAdapter(_source));

    public void PoisonAndRemove()
    {
        Exception? removalFailure = null;
        Remove(PrivateHostBootstrapEnvironment.RequestReadHandle, ref removalFailure);
        Remove(PrivateHostBootstrapEnvironment.EventWriteHandle, ref removalFailure);
        Remove(WorkerBootstrapEnvironment.RequestHandle, ref removalFailure);
        Remove(WorkerBootstrapEnvironment.EventHandle, ref removalFailure);
        if (removalFailure is not null)
            throw new PrivateHostBootstrapException("environment_removal_failed");
    }

    private void Remove(string variable, ref Exception? failure)
    {
        try
        {
            _source.Remove(variable);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure ??= exception;
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed class WorkerEnvironmentAdapter(
        IPrivateHostBootstrapEnvironmentSource source) : IWorkerBootstrapEnvironmentSource
    {
        public string? Get(string variable) => source.Get(variable);

        public void Remove(string variable) => source.Remove(variable);
    }

    private sealed class ProcessEnvironmentSource : IPrivateHostBootstrapEnvironmentSource
    {
        internal static ProcessEnvironmentSource Instance { get; } = new();

        public string? Get(string variable) =>
            Environment.GetEnvironmentVariable(variable);

        public void Remove(string variable) =>
            Environment.SetEnvironmentVariable(variable, null);
    }
}

internal sealed class PrivateHostBootstrapNative : IPrivateHostBootstrapNative
{
    private const uint HandleFlagInherit = 0x00000001;
    private const int FdCloseOnExec = 1;

    internal static PrivateHostBootstrapNative Instance { get; } = new();

    public IPrivateHostBootstrapHandle OwnInherited(nuint handleValue)
    {
        SafeFileHandle? owned = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                owned = new SafeFileHandle(
                    (IntPtr)(nint)handleValue,
                    ownsHandle: true);
                if (!NativeMethods.SetHandleInformation(
                        owned,
                        HandleFlagInherit,
                        flags: 0))
                {
                    throw new InvalidOperationException();
                }
            }
            else
            {
                if (handleValue == 0 || handleValue > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(handleValue));

                var descriptor = checked((int)handleValue);
                owned = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
                var nativeDescriptor = (IntPtr)descriptor;
                var descriptorFlags = NativeMethods.GetDescriptorFlags(nativeDescriptor);
                if (descriptorFlags < 0 ||
                    NativeMethods.SetDescriptorFlags(
                        nativeDescriptor,
                        descriptorFlags | FdCloseOnExec) < 0)
                {
                    throw new InvalidOperationException();
                }
            }

            var result = new OwnedHandle(owned);
            owned = null;
            return result;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private sealed class OwnedHandle(SafeFileHandle handle) : IPrivateHostBootstrapHandle
    {
        private SafeFileHandle? _handle = handle;

        public Stream OpenStream(FileAccess access)
        {
            if (access is not FileAccess.Read and not FileAccess.Write)
                throw new ArgumentOutOfRangeException(nameof(access));

            var owned = Interlocked.Exchange(ref _handle, null) ??
                throw new ObjectDisposedException(nameof(OwnedHandle));
            try
            {
                var stream = new FileStream(
                    owned,
                    access,
                    bufferSize: 4096,
                    isAsync: false);
                owned = null;
                return stream;
            }
            finally
            {
                owned?.Dispose();
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "SetHandleInformation", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(
            SafeFileHandle handle,
            uint mask,
            uint flags);

        // These runtime shims are fixed-signature fcntl(F_GETFD/F_SETFD)
        // calls. Their fd parameter is native intptr_t, not C int. A direct
        // variadic fcntl P/Invoke is not ABI-correct on every supported Unix
        // architecture.
        [DllImport(
            "System.Native",
            EntryPoint = "SystemNative_FcntlGetFD",
            SetLastError = true)]
        internal static extern int GetDescriptorFlags(IntPtr descriptor);

        [DllImport(
            "System.Native",
            EntryPoint = "SystemNative_FcntlSetFD",
            SetLastError = true)]
        internal static extern int SetDescriptorFlags(
            IntPtr descriptor,
            int flags);
    }
}
