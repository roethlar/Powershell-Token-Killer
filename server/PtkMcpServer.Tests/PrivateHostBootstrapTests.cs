using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PtkMcpServer.GuardianHost;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class PrivateHostBootstrapTests
{
    private const string Secret = "/private/bootstrap/secret-9b1e";

    [Fact]
    public void Host_environment_contract_reserves_exactly_two_names()
    {
        Assert.Equal("PTK_HOST_REQUEST_READ_HANDLE", PrivateHostBootstrapEnvironment.RequestReadHandle);
        Assert.Equal("PTK_HOST_EVENT_WRITE_HANDLE", PrivateHostBootstrapEnvironment.EventWriteHandle);
        Assert.Equal(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                PrivateHostBootstrapEnvironment.RequestReadHandle,
                PrivateHostBootstrapEnvironment.EventWriteHandle,
            },
            PrivateHostBootstrapEnvironment.ReservedHandleVariables);
    }

    [Fact]
    public void Capture_reads_then_removes_both_values_before_owning_handles()
    {
        var timeline = new List<string>();
        var environment = HostEnvironment("101", "202", timeline);
        var native = new RecordingNative(timeline);

        using var values = PrivateHostBootstrapCapture.CaptureAndRemove(
            environment,
            native,
            pointerSize: 8);

        Assert.Equal(
            [
                "get:PTK_HOST_REQUEST_READ_HANDLE",
                "get:PTK_HOST_EVENT_WRITE_HANDLE",
                "remove:PTK_HOST_REQUEST_READ_HANDLE",
                "remove:PTK_HOST_EVENT_WRITE_HANDLE",
                "own:101",
                "own:202",
            ],
            timeline);
        Assert.False(environment.Values.ContainsKey(
            PrivateHostBootstrapEnvironment.RequestReadHandle));
        Assert.False(environment.Values.ContainsKey(
            PrivateHostBootstrapEnvironment.EventWriteHandle));
    }

    [Theory]
    [MemberData(nameof(InvalidHandleValues))]
    public void Invalid_values_are_rejected_only_after_both_variables_are_removed(
        string? invalidValue)
    {
        var timeline = new List<string>();
        var environment = HostEnvironment(invalidValue, "202", timeline);
        var native = new RecordingNative(timeline);

        var exception = Assert.Throws<PrivateHostBootstrapException>(() =>
            PrivateHostBootstrapCapture.CaptureAndRemove(
                environment,
                native,
                pointerSize: 8));

        Assert.Contains(exception.DetailCode, new[] { "handle_missing", "handle_invalid" });
        Assert.Equal(
            [
                "get:PTK_HOST_REQUEST_READ_HANDLE",
                "get:PTK_HOST_EVENT_WRITE_HANDLE",
                "remove:PTK_HOST_REQUEST_READ_HANDLE",
                "remove:PTK_HOST_EVENT_WRITE_HANDLE",
            ],
            timeline);
        Assert.Empty(native.Handles);
        Assert.Empty(environment.Values);
        AssertNormalized(exception);
    }

    public static TheoryData<string?> InvalidHandleValues => new()
    {
        null,
        string.Empty,
        "0",
        "00",
        "01",
        "+1",
        "-1",
        " 1",
        "1 ",
        "1.0",
        "1,0",
        "0x1",
        "١",
        "18446744073709551615",
        "18446744073709551616",
    };

    [Fact]
    public void Pointer_width_overflow_is_rejected_after_removal()
    {
        var environment = HostEnvironment("4294967295", "202");

        var exception = Assert.Throws<PrivateHostBootstrapException>(() =>
            PrivateHostBootstrapCapture.CaptureAndRemove(
                environment,
                new RecordingNative(),
                pointerSize: 4));

        Assert.Equal("handle_invalid", exception.DetailCode);
        Assert.Empty(environment.Values);
    }

    [Fact]
    public void Same_handle_is_rejected_before_native_ownership()
    {
        var timeline = new List<string>();
        var environment = HostEnvironment("303", "303", timeline);
        var native = new RecordingNative(timeline);

        var exception = Assert.Throws<PrivateHostBootstrapException>(() =>
            PrivateHostBootstrapCapture.CaptureAndRemove(
                environment,
                native,
                pointerSize: 8));

        Assert.Equal("handle_alias", exception.DetailCode);
        Assert.Empty(native.Handles);
        Assert.Equal(4, timeline.Count);
        Assert.Empty(environment.Values);
    }

    [Fact]
    public void Capture_failures_still_attempt_both_gets_and_both_removals()
    {
        var environment = HostEnvironment("101", "202");
        environment.ThrowOnGet.Add(PrivateHostBootstrapEnvironment.RequestReadHandle);

        var exception = Assert.Throws<PrivateHostBootstrapException>(() =>
            PrivateHostBootstrapCapture.CaptureAndRemove(
                environment,
                new RecordingNative(),
                pointerSize: 8));

        Assert.Equal("environment_capture_failed", exception.DetailCode);
        Assert.Equal(
            [
                "get:PTK_HOST_REQUEST_READ_HANDLE",
                "get:PTK_HOST_EVENT_WRITE_HANDLE",
                "remove:PTK_HOST_REQUEST_READ_HANDLE",
                "remove:PTK_HOST_EVENT_WRITE_HANDLE",
            ],
            environment.Timeline);
        Assert.Empty(environment.Values);
        AssertNormalized(exception);
    }

    [Fact]
    public void Removal_failures_do_not_skip_the_second_removal()
    {
        var environment = HostEnvironment("101", "202");
        environment.ThrowOnRemove.Add(PrivateHostBootstrapEnvironment.RequestReadHandle);

        var exception = Assert.Throws<PrivateHostBootstrapException>(() =>
            PrivateHostBootstrapCapture.CaptureAndRemove(
                environment,
                new RecordingNative(),
                pointerSize: 8));

        Assert.Equal("environment_removal_failed", exception.DetailCode);
        Assert.Equal(
            [
                "get:PTK_HOST_REQUEST_READ_HANDLE",
                "get:PTK_HOST_EVENT_WRITE_HANDLE",
                "remove:PTK_HOST_REQUEST_READ_HANDLE",
                "remove:PTK_HOST_EVENT_WRITE_HANDLE",
            ],
            environment.Timeline);
        Assert.Empty(environment.Values);
        AssertNormalized(exception);
    }

    [Fact]
    public void Values_open_one_read_and_one_write_stream_exactly_once()
    {
        var timeline = new List<string>();
        var native = new RecordingNative(timeline);
        using var values = PrivateHostBootstrapCapture.CaptureAndRemove(
            HostEnvironment("101", "202", timeline),
            native,
            pointerSize: 8);

        using var streams = values.OpenStreams();

        Assert.Same(native.Handles[0].Stream, streams.RequestReadStream);
        Assert.Same(native.Handles[1].Stream, streams.EventWriteStream);
        Assert.Equal(FileAccess.Read, native.Handles[0].Access);
        Assert.Equal(FileAccess.Write, native.Handles[1].Access);
        Assert.Equal(1, native.Handles[0].OpenCalls);
        Assert.Equal(1, native.Handles[1].OpenCalls);

        var exception = Assert.Throws<PrivateHostBootstrapException>(values.OpenStreams);
        Assert.Equal("bootstrap_consumed", exception.DetailCode);
        Assert.Equal(1, native.Handles[0].OpenCalls);
        Assert.Equal(1, native.Handles[1].OpenCalls);
    }

    [Fact]
    public void Values_dispose_unopened_handles_and_prevent_later_use()
    {
        var native = new RecordingNative();
        var values = PrivateHostBootstrapCapture.CaptureAndRemove(
            HostEnvironment("101", "202"),
            native,
            pointerSize: 8);

        values.Dispose();
        values.Dispose();

        Assert.All(native.Handles, handle => Assert.Equal(1, handle.DisposeCalls));
        var exception = Assert.Throws<PrivateHostBootstrapException>(values.OpenStreams);
        Assert.Equal("bootstrap_consumed", exception.DetailCode);
    }

    [Fact]
    public void Partial_stream_open_failure_cleans_every_owner_and_cannot_be_retried()
    {
        var native = new RecordingNative();
        using var values = PrivateHostBootstrapCapture.CaptureAndRemove(
            HostEnvironment("101", "202"),
            native,
            pointerSize: 8);
        native.Handles[1].ThrowOnOpen = true;

        var exception = Assert.Throws<PrivateHostBootstrapException>(values.OpenStreams);

        Assert.Equal("stream_creation_failed", exception.DetailCode);
        Assert.True(native.Handles[0].Stream.Disposed);
        Assert.Equal(1, native.Handles[0].DisposeCalls);
        Assert.Equal(1, native.Handles[1].DisposeCalls);
        Assert.Equal("bootstrap_consumed", Assert.Throws<PrivateHostBootstrapException>(
            values.OpenStreams).DetailCode);
        AssertNormalized(exception);
    }

    [Fact]
    public void Successful_stream_owner_disposes_both_streams_and_hides_them_afterward()
    {
        var native = new RecordingNative();
        using var values = PrivateHostBootstrapCapture.CaptureAndRemove(
            HostEnvironment("101", "202"),
            native,
            pointerSize: 8);
        var streams = values.OpenStreams();

        streams.Dispose();
        streams.Dispose();

        Assert.True(native.Handles[0].Stream.Disposed);
        Assert.True(native.Handles[1].Stream.Disposed);
        Assert.Throws<ObjectDisposedException>(() => streams.RequestReadStream);
        Assert.Throws<ObjectDisposedException>(() => streams.EventWriteStream);
    }

    [Fact]
    public void Ownership_failure_closes_partial_ownership_and_normalizes_the_error()
    {
        var native = new RecordingNative { ThrowOnValue = 202 };

        var exception = Assert.Throws<PrivateHostBootstrapException>(() =>
            PrivateHostBootstrapCapture.CaptureAndRemove(
                HostEnvironment("101", "202"),
                native,
                pointerSize: 8));

        Assert.Equal("handle_ownership_failed", exception.DetailCode);
        Assert.Single(native.Handles);
        Assert.Equal(1, native.Handles[0].DisposeCalls);
        AssertNormalized(exception);
    }

    [Fact]
    public void Concrete_poison_removes_host_and_worker_variables_without_reading()
    {
        var environment = AllPrivateEnvironment();
        environment.FailAnyGet = true;
        var boundary = new PrivateProcessBootstrapBoundary(environment);

        boundary.PoisonAndRemove();

        Assert.Equal(
            [
                "remove:PTK_HOST_REQUEST_READ_HANDLE",
                "remove:PTK_HOST_EVENT_WRITE_HANDLE",
                "remove:PTK_WORKER_REQUEST_HANDLE",
                "remove:PTK_WORKER_EVENT_HANDLE",
            ],
            environment.Timeline);
        Assert.Empty(environment.Values);
    }

    [Fact]
    public void Concrete_poison_attempts_all_removals_and_normalizes_failure()
    {
        var environment = AllPrivateEnvironment();
        environment.FailAnyGet = true;
        environment.ThrowOnRemove.Add(PrivateHostBootstrapEnvironment.RequestReadHandle);
        environment.ThrowOnRemove.Add(WorkerBootstrapEnvironment.RequestHandle);
        var boundary = new PrivateProcessBootstrapBoundary(environment);

        var exception = Assert.Throws<PrivateHostBootstrapException>(
            boundary.PoisonAndRemove);

        Assert.Equal("environment_removal_failed", exception.DetailCode);
        Assert.Equal(4, environment.Timeline.Count);
        Assert.All(environment.Timeline, item => Assert.StartsWith("remove:", item));
        Assert.Empty(environment.Values);
        AssertNormalized(exception);
    }

    [Fact]
    public void Concrete_worker_capture_uses_only_the_existing_worker_boundary()
    {
        var environment = AllPrivateEnvironment();
        var boundary = new PrivateProcessBootstrapBoundary(environment);

        var values = boundary.CaptureAndRemoveWorker();

        Assert.Equal(new WorkerBootstrapValues("303", "404"), values);
        Assert.Equal(
            [
                "get:PTK_WORKER_REQUEST_HANDLE",
                "get:PTK_WORKER_EVENT_HANDLE",
                "remove:PTK_WORKER_REQUEST_HANDLE",
                "remove:PTK_WORKER_EVENT_HANDLE",
            ],
            environment.Timeline);
        Assert.True(environment.Values.ContainsKey(
            PrivateHostBootstrapEnvironment.RequestReadHandle));
        Assert.True(environment.Values.ContainsKey(
            PrivateHostBootstrapEnvironment.EventWriteHandle));
    }

    [Fact]
    public void Production_unix_fcntl_shims_freeze_the_native_intptr_descriptor_abi()
    {
        var nativeMethods = typeof(PrivateHostBootstrapNative).GetNestedType(
            "NativeMethods",
            BindingFlags.NonPublic);
        Assert.NotNull(nativeMethods);
        var get = nativeMethods.GetMethod(
            "GetDescriptorFlags",
            BindingFlags.NonPublic | BindingFlags.Static);
        var set = nativeMethods.GetMethod(
            "SetDescriptorFlags",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(get);
        Assert.NotNull(set);

        Assert.Equal([typeof(IntPtr)], get.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(
            [typeof(IntPtr), typeof(int)],
            set.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(int), get.ReturnType);
        Assert.Equal(typeof(int), set.ReturnType);

        var getImport = get.GetCustomAttribute<DllImportAttribute>();
        var setImport = set.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(getImport);
        Assert.NotNull(setImport);
        Assert.Equal("System.Native", getImport.Value);
        Assert.Equal("SystemNative_FcntlGetFD", getImport.EntryPoint);
        Assert.Equal("System.Native", setImport.Value);
        Assert.Equal("SystemNative_FcntlSetFD", setImport.EntryPoint);
    }

    [Fact]
    public void Unix_production_ownership_sets_close_on_exec_on_both_real_pipe_handles()
    {
        if (OperatingSystem.IsWindows())
            return;

        var requestPipe = CreateUnixPipe();
        var eventPipe = CreateUnixPipe();
        var requestRead = requestPipe[0];
        var eventWrite = eventPipe[1];
        var ownershipSubmitted = false;
        using var requestWriteHandle = new SafeFileHandle(
            (IntPtr)requestPipe[1],
            ownsHandle: true);
        using var eventReadHandle = new SafeFileHandle(
            (IntPtr)eventPipe[0],
            ownsHandle: true);
        using var requestWrite = new FileStream(
            requestWriteHandle,
            FileAccess.Write,
            bufferSize: 1,
            isAsync: false);
        using var eventRead = new FileStream(
            eventReadHandle,
            FileAccess.Read,
            bufferSize: 1,
            isAsync: false);
        try
        {
            Assert.Equal(0, GetUnixDescriptorFlags(requestRead) & TestFdCloseOnExec);
            Assert.Equal(0, GetUnixDescriptorFlags(eventWrite) & TestFdCloseOnExec);

            ownershipSubmitted = true;
            using var values = PrivateHostBootstrapCapture.CaptureAndRemove(
                HostEnvironment(
                    requestRead.ToString(CultureInfo.InvariantCulture),
                    eventWrite.ToString(CultureInfo.InvariantCulture)),
                pointerSize: IntPtr.Size);

            Assert.NotEqual(0, GetUnixDescriptorFlags(requestRead) & TestFdCloseOnExec);
            Assert.NotEqual(0, GetUnixDescriptorFlags(eventWrite) & TestFdCloseOnExec);

            using var streams = values.OpenStreams();
            requestWrite.WriteByte(0x41);
            requestWrite.Flush();
            Assert.Equal(0x41, streams.RequestReadStream.ReadByte());
            streams.EventWriteStream.WriteByte(0x42);
            streams.EventWriteStream.Flush();
            Assert.Equal(0x42, eventRead.ReadByte());
        }
        finally
        {
            if (!ownershipSubmitted)
            {
                CloseUnixDescriptor(requestRead);
                CloseUnixDescriptor(eventWrite);
            }
        }
    }

    [Fact]
    public void Unix_out_of_range_second_descriptor_closes_first_owned_descriptor()
    {
        if (OperatingSystem.IsWindows())
            return;

        var requestPipe = CreateUnixPipe();
        var requestRead = requestPipe[0];
        using var requestWrite = new SafeFileHandle(
            (IntPtr)requestPipe[1],
            ownsHandle: true);
        var invalidDescriptor = ((ulong)int.MaxValue + 1).ToString(
            CultureInfo.InvariantCulture);

        var exception = Assert.Throws<PrivateHostBootstrapException>(() =>
            PrivateHostBootstrapCapture.CaptureAndRemove(
                HostEnvironment(
                    requestRead.ToString(CultureInfo.InvariantCulture),
                    invalidDescriptor),
                pointerSize: 8));

        Assert.Equal("handle_ownership_failed", exception.DetailCode);
        Assert.Equal(-1, TestNativeMethods.GetDescriptorFlags(requestRead, TestFGetFd));
        AssertNormalized(exception);
    }

    [Fact]
    public void Windows_production_ownership_clears_inherit_on_both_real_pipe_handles()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var requestPipe = CreateWindowsPipe();
        var eventPipe = CreateWindowsPipe();
        using var requestReadOwner = requestPipe.Read;
        using var requestWrite = new FileStream(
            requestPipe.Write,
            FileAccess.Write,
            bufferSize: 1,
            isAsync: false);
        using var eventRead = new FileStream(
            eventPipe.Read,
            FileAccess.Read,
            bufferSize: 1,
            isAsync: false);
        using var eventWriteOwner = eventPipe.Write;
        var requestRead = requestReadOwner.DangerousGetHandle();
        var eventWrite = eventWriteOwner.DangerousGetHandle();
        Assert.True(TestNativeMethods.GetHandleInformation(requestRead, out var requestFlags));
        Assert.True(TestNativeMethods.GetHandleInformation(eventWrite, out var eventFlags));
        Assert.NotEqual(0u, requestFlags & TestHandleFlagInherit);
        Assert.NotEqual(0u, eventFlags & TestHandleFlagInherit);

        requestReadOwner.SetHandleAsInvalid();
        eventWriteOwner.SetHandleAsInvalid();
        using var values = PrivateHostBootstrapCapture.CaptureAndRemove(
            HostEnvironment(ToNativeHandleString(requestRead), ToNativeHandleString(eventWrite)),
            pointerSize: IntPtr.Size);

        Assert.True(TestNativeMethods.GetHandleInformation(requestRead, out requestFlags));
        Assert.True(TestNativeMethods.GetHandleInformation(eventWrite, out eventFlags));
        Assert.Equal(0u, requestFlags & TestHandleFlagInherit);
        Assert.Equal(0u, eventFlags & TestHandleFlagInherit);

        using var streams = values.OpenStreams();
        requestWrite.WriteByte(0x41);
        requestWrite.Flush();
        Assert.Equal(0x41, streams.RequestReadStream.ReadByte());
        streams.EventWriteStream.WriteByte(0x42);
        streams.EventWriteStream.Flush();
        Assert.Equal(0x42, eventRead.ReadByte());
    }

    private static RecordingEnvironment HostEnvironment(
        string? request,
        string? events,
        List<string>? timeline = null) =>
        new(
            new Dictionary<string, string?>
            {
                [PrivateHostBootstrapEnvironment.RequestReadHandle] = request,
                [PrivateHostBootstrapEnvironment.EventWriteHandle] = events,
            },
            timeline);

    private static RecordingEnvironment AllPrivateEnvironment() =>
        new(new Dictionary<string, string?>
        {
            [PrivateHostBootstrapEnvironment.RequestReadHandle] = "101",
            [PrivateHostBootstrapEnvironment.EventWriteHandle] = "202",
            [WorkerBootstrapEnvironment.RequestHandle] = "303",
            [WorkerBootstrapEnvironment.EventHandle] = "404",
        });

    private const int TestFGetFd = 1;
    private const int TestFdCloseOnExec = 1;
    private const uint TestHandleFlagInherit = 0x00000001;

    private static int[] CreateUnixPipe()
    {
        var descriptors = new int[2];
        if (TestNativeMethods.CreatePipe(descriptors) != 0)
            throw new InvalidOperationException("Test pipe creation failed.");
        return descriptors;
    }

    private static int GetUnixDescriptorFlags(int descriptor)
    {
        var flags = TestNativeMethods.GetDescriptorFlags(descriptor, TestFGetFd);
        Assert.True(flags >= 0);
        return flags;
    }

    private static void CloseUnixDescriptor(int descriptor)
    {
        if (descriptor >= 0)
            _ = TestNativeMethods.CloseDescriptor(descriptor);
    }

    private static WindowsPipe CreateWindowsPipe()
    {
        var securityAttributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };
        if (!TestNativeMethods.CreatePipe(
                out var read,
                out var write,
                ref securityAttributes,
                size: 0))
        {
            throw new InvalidOperationException("Test pipe creation failed.");
        }
        return new WindowsPipe(read, write);
    }

    private static string ToNativeHandleString(IntPtr handle) =>
        unchecked((nuint)(nint)handle).ToString(CultureInfo.InvariantCulture);

    private static void AssertNormalized(PrivateHostBootstrapException exception)
    {
        Assert.Equal("Private host bootstrap failed.", exception.Message);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    private sealed class RecordingEnvironment(
        Dictionary<string, string?> values,
        List<string>? timeline = null) : IPrivateHostBootstrapEnvironmentSource
    {
        internal Dictionary<string, string?> Values { get; } = values;
        internal List<string> Timeline { get; } = timeline ?? [];
        internal HashSet<string> ThrowOnGet { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> ThrowOnRemove { get; } = new(StringComparer.Ordinal);
        internal bool FailAnyGet { get; set; }

        public string? Get(string variable)
        {
            Timeline.Add($"get:{variable}");
            if (FailAnyGet || ThrowOnGet.Contains(variable))
                throw new InvalidOperationException($"source failed: {Secret}");
            return Values.TryGetValue(variable, out var value) ? value : null;
        }

        public void Remove(string variable)
        {
            Timeline.Add($"remove:{variable}");
            Values.Remove(variable);
            if (ThrowOnRemove.Contains(variable))
                throw new InvalidOperationException($"removal failed: {Secret}");
        }
    }

    private sealed class RecordingNative(List<string>? timeline = null) :
        IPrivateHostBootstrapNative
    {
        private readonly List<string> _timeline = timeline ?? [];

        internal List<RecordingHandle> Handles { get; } = [];
        internal nuint? ThrowOnValue { get; init; }

        public IPrivateHostBootstrapHandle OwnInherited(nuint handleValue)
        {
            _timeline.Add($"own:{handleValue}");
            if (ThrowOnValue == handleValue)
                throw new IOException($"native ownership failed: {Secret}");
            var handle = new RecordingHandle(_timeline);
            Handles.Add(handle);
            return handle;
        }
    }

    private sealed class RecordingHandle(List<string> timeline) :
        IPrivateHostBootstrapHandle
    {
        internal TrackingStream Stream { get; } = new();
        internal int DisposeCalls { get; private set; }
        internal int OpenCalls { get; private set; }
        internal FileAccess? Access { get; private set; }
        internal bool ThrowOnOpen { get; set; }

        public Stream OpenStream(FileAccess access)
        {
            timeline.Add($"open:{access}");
            OpenCalls++;
            Access = access;
            if (ThrowOnOpen)
                throw new IOException($"stream open failed: {Secret}");
            return Stream;
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class TrackingStream : MemoryStream
    {
        internal bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed record WindowsPipe(SafeFileHandle Read, SafeFileHandle Write);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        internal bool InheritHandle;
    }

    private static class TestNativeMethods
    {
        [DllImport("libc", EntryPoint = "pipe", SetLastError = true)]
        internal static extern int CreatePipe([Out] int[] descriptors);

        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        internal static extern int GetDescriptorFlags(int descriptor, int command);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        internal static extern int CloseDescriptor(int descriptor);

        [DllImport("kernel32.dll", EntryPoint = "CreatePipe", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(
            out SafeFileHandle readPipe,
            out SafeFileHandle writePipe,
            ref SecurityAttributes pipeAttributes,
            int size);

        [DllImport("kernel32.dll", EntryPoint = "GetHandleInformation", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetHandleInformation(IntPtr handle, out uint flags);
    }
}
