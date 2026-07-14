using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PtkMcpServer.Worker;

internal enum WorkerLaunchStage
{
    CreateJob,
    ConfigureJob,
    QueryJob,
    CreatePipe,
    ConfigureHandleInheritance,
    OpenNullInput,
    InitializeAttributeList,
    AddJobAttribute,
    AddHandleAttribute,
    CreateProcess,
    CloseChildHandles,
    VerifyContainment,
    ResumePrimaryThread,
}

internal sealed class WorkerLaunchException : Exception
{
    internal WorkerLaunchException(
        string detailCode,
        WorkerLaunchStage stage,
        int? nativeErrorCode = null,
        Exception? innerException = null)
        : base(BuildMessage(detailCode, stage, nativeErrorCode), innerException)
    {
        DetailCode = detailCode;
        Stage = stage;
        NativeErrorCode = nativeErrorCode;
    }

    internal string DetailCode { get; }
    internal WorkerLaunchStage Stage { get; }
    internal int? NativeErrorCode { get; }

    private static string BuildMessage(
        string detailCode,
        WorkerLaunchStage stage,
        int? nativeErrorCode) =>
        nativeErrorCode is { } error
            ? $"Windows worker launch failed at {stage} ({detailCode}, Win32 {error})."
            : $"Windows worker launch failed at {stage} ({detailCode}).";
}

/// <summary>
/// The sole Win32 process-creation implementation used by the Windows worker
/// supervisor. It deliberately exposes no plain process creation, post-create
/// job assignment, or explicit job termination path.
/// </summary>
internal sealed class WindowsWorkerNative : IWindowsWorkerNative
{
    private const int ErrorInsufficientBuffer = 122;
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const nuint ProcThreadAttributeJobList = 0x0002000D;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    public IWindowsJobHandle CreateUnnamedJob()
    {
        var job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (!job.IsInvalid) return job;

        var error = Marshal.GetLastWin32Error();
        job.Dispose();
        throw Failure("containment_setup_failed", WorkerLaunchStage.CreateJob, error);
    }

    public void SetJobLimitFlags(IWindowsJobHandle job, uint limitFlags)
    {
        var nativeJob = RequireJob(job);
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = limitFlags,
            },
        };

        if (NativeMethods.SetInformationJobObject(
                nativeJob,
                JobObjectExtendedLimitInformationClass,
                ref information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            return;
        }

        throw Failure(
            "containment_setup_failed",
            WorkerLaunchStage.ConfigureJob,
            Marshal.GetLastWin32Error());
    }

    public uint QueryJobLimitFlags(IWindowsJobHandle job)
    {
        var nativeJob = RequireJob(job);
        if (NativeMethods.QueryInformationJobObject(
                nativeJob,
                JobObjectExtendedLimitInformationClass,
                out var information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>(),
                out _))
        {
            return information.BasicLimitInformation.LimitFlags;
        }

        throw Failure(
            "containment_setup_failed",
            WorkerLaunchStage.QueryJob,
            Marshal.GetLastWin32Error());
    }

    public IWindowsWorkerPipeSet CreateWorkerPipeSet()
    {
        SafeFileHandle? standardInput = null;
        PipePair? request = null;
        PipePair? events = null;
        PipePair? standardOutput = null;
        PipePair? standardError = null;
        FileStream? requestWriter = null;
        FileStream? eventReader = null;
        FileStream? standardOutputReader = null;
        FileStream? standardErrorReader = null;

        try
        {
            standardInput = OpenNullInput();
            request = CreatePipe(childReads: true);
            events = CreatePipe(childReads: false);
            standardOutput = CreatePipe(childReads: false);
            standardError = CreatePipe(childReads: false);

            requestWriter = new FileStream(
                request.TakeSupervisorEnd(),
                FileAccess.Write,
                bufferSize: 4096,
                isAsync: false);
            eventReader = new FileStream(
                events.TakeSupervisorEnd(),
                FileAccess.Read,
                bufferSize: 4096,
                isAsync: false);
            standardOutputReader = new FileStream(
                standardOutput.TakeSupervisorEnd(),
                FileAccess.Read,
                bufferSize: 4096,
                isAsync: false);
            standardErrorReader = new FileStream(
                standardError.TakeSupervisorEnd(),
                FileAccess.Read,
                bufferSize: 4096,
                isAsync: false);

            var result = new NativeWorkerPipeSet(
                standardInput,
                request.TakeChildEnd(),
                events.TakeChildEnd(),
                standardOutput.TakeChildEnd(),
                standardError.TakeChildEnd(),
                requestWriter,
                eventReader,
                standardOutputReader,
                standardErrorReader);

            standardInput = null;
            requestWriter = null;
            eventReader = null;
            standardOutputReader = null;
            standardErrorReader = null;
            return result;
        }
        catch (WorkerLaunchException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new WorkerLaunchException(
                "containment_setup_failed",
                WorkerLaunchStage.CreatePipe,
                innerException: exception);
        }
        finally
        {
            requestWriter?.Dispose();
            eventReader?.Dispose();
            standardOutputReader?.Dispose();
            standardErrorReader?.Dispose();
            request?.Dispose();
            events?.Dispose();
            standardOutput?.Dispose();
            standardError?.Dispose();
            standardInput?.Dispose();
        }
    }

    public IWindowsProcessHandle CreateProcessInJob(
        WorkerLaunchCommand command,
        IWindowsJobHandle job,
        IWindowsWorkerPipeSet pipes,
        WindowsProcessCreationMode mode)
    {
        // HANDLE_LIST limits what this worker receives, but its five selected
        // handles must still be inheritable while CreateProcessW runs. Do not
        // wire this launcher into a supervisor that can concurrently use a
        // generic inheriting spawn path; every supervisor-side spawn must first
        // share one disciplined creation gate or use an explicit handle list.
        ArgumentNullException.ThrowIfNull(command);
        var nativeJob = RequireJob(job);
        var nativePipes = RequirePipes(pipes);
        if (mode is not WindowsProcessCreationMode.Runnable and
            not WindowsProcessCreationMode.SuspendedForContainmentProof)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        using var handleLease = new SafeHandleLease(
            nativeJob,
            nativePipes.StandardInputChild,
            nativePipes.RequestChild,
            nativePipes.EventChild,
            nativePipes.StandardOutputChild,
            nativePipes.StandardErrorChild);
        using var attributes = new ProcessAttributeList(
            nativeJob,
            nativePipes.ChildHandles);
        using var environment = UnicodeEnvironmentBlock.Create(
            command.Environment,
            nativePipes);

        var startupInfo = new StartupInfoEx
        {
            StartupInfo = new StartupInfo
            {
                Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                Flags = StartfUseStdHandles,
                StandardInput = nativePipes.StandardInputChild.DangerousGetHandle(),
                StandardOutput = nativePipes.StandardOutputChild.DangerousGetHandle(),
                StandardError = nativePipes.StandardErrorChild.DangerousGetHandle(),
            },
            AttributeList = attributes.Pointer,
        };
        var commandLine = new StringBuilder(BuildCommandLine(command));
        var creationFlags = BuildCreationFlags(mode);

        ProcessInformation processInformation = default;
        var created = false;
        try
        {
            if (!NativeMethods.CreateProcessW(
                    command.ExecutablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    creationFlags,
                    environment.Pointer,
                    command.WorkingDirectory,
                    ref startupInfo,
                    out processInformation))
            {
                throw Failure(
                    "worker_create_failed",
                    WorkerLaunchStage.CreateProcess,
                    Marshal.GetLastWin32Error());
            }
            created = true;

            var process = new SafeProcessHandle(processInformation.Process, ownsHandle: true);
            processInformation.Process = IntPtr.Zero;
            var primaryThread = new NativeThreadHandle(
                processInformation.Thread,
                ownsHandle: true);
            processInformation.Thread = IntPtr.Zero;
            try
            {
                var result = new NativeProcessHandle(
                    process,
                    primaryThread,
                    checked((int)processInformation.ProcessId),
                    mode == WindowsProcessCreationMode.SuspendedForContainmentProof);
                process = null!;
                primaryThread = null!;
                return result;
            }
            finally
            {
                process?.Dispose();
                primaryThread?.Dispose();
            }
        }
        finally
        {
            if (!created || processInformation.Process != IntPtr.Zero)
                CloseRawHandle(processInformation.Process);
            if (!created || processInformation.Thread != IntPtr.Zero)
                CloseRawHandle(processInformation.Thread);
        }
    }

    public bool IsProcessInJob(IWindowsProcessHandle process, IWindowsJobHandle job)
    {
        var nativeProcess = RequireProcess(process);
        var nativeJob = RequireJob(job);
        if (NativeMethods.IsProcessInJob(
                nativeProcess.ProcessHandle,
                nativeJob,
                out var inJob))
        {
            return inJob;
        }

        throw Failure(
            "containment_verification_failed",
            WorkerLaunchStage.VerifyContainment,
            Marshal.GetLastWin32Error());
    }

    public void ResumePrimaryThreadForContainmentProof(IWindowsProcessHandle process)
    {
        RequireProcess(process).ResumePrimaryThreadForContainmentProof();
    }

    private static NativeJobHandle RequireJob(IWindowsJobHandle job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job as NativeJobHandle ?? throw new ArgumentException(
            "The job handle was not created by this Windows native launcher.",
            nameof(job));
    }

    private static NativeWorkerPipeSet RequirePipes(IWindowsWorkerPipeSet pipes)
    {
        ArgumentNullException.ThrowIfNull(pipes);
        return pipes as NativeWorkerPipeSet ?? throw new ArgumentException(
            "The worker pipes were not created by this Windows native launcher.",
            nameof(pipes));
    }

    private static NativeProcessHandle RequireProcess(IWindowsProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return process as NativeProcessHandle ?? throw new ArgumentException(
            "The process handle was not created by this Windows native launcher.",
            nameof(process));
    }

    private static SafeFileHandle OpenNullInput()
    {
        var security = SecurityAttributes.Inheritable();
        var handle = NativeMethods.CreateFileW(
            "NUL",
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            ref security,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw Failure(
            "containment_setup_failed",
            WorkerLaunchStage.OpenNullInput,
            error);
    }

    private static PipePair CreatePipe(bool childReads)
    {
        var security = SecurityAttributes.Inheritable();
        if (!NativeMethods.CreatePipe(
                out var read,
                out var write,
                ref security,
                size: 0))
        {
            var error = Marshal.GetLastWin32Error();
            CloseRawHandle(read);
            CloseRawHandle(write);
            throw Failure(
                "containment_setup_failed",
                WorkerLaunchStage.CreatePipe,
                error);
        }

        var readHandle = new SafeFileHandle(read, ownsHandle: true);
        var writeHandle = new SafeFileHandle(write, ownsHandle: true);
        var supervisor = childReads ? writeHandle : readHandle;
        var child = childReads ? readHandle : writeHandle;
        try
        {
            if (!NativeMethods.SetHandleInformation(
                    supervisor,
                    HandleFlagInherit,
                    flags: 0))
            {
                throw Failure(
                    "containment_setup_failed",
                    WorkerLaunchStage.ConfigureHandleInheritance,
                    Marshal.GetLastWin32Error());
            }
            return new PipePair(supervisor, child);
        }
        catch
        {
            readHandle.Dispose();
            writeHandle.Dispose();
            throw;
        }
    }

    internal static string BuildCommandLine(WorkerLaunchCommand command)
    {
        var result = new StringBuilder();
        AppendQuotedArgument(result, command.ExecutablePath);
        foreach (var argument in command.Arguments)
        {
            result.Append(' ');
            AppendQuotedArgument(result, argument);
        }
        return result.ToString();
    }

    internal static uint BuildCreationFlags(WindowsProcessCreationMode mode) =>
        mode switch
        {
            WindowsProcessCreationMode.Runnable =>
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateNoWindow,
            WindowsProcessCreationMode.SuspendedForContainmentProof =>
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateNoWindow | CreateSuspended,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static void AppendQuotedArgument(StringBuilder result, string argument)
    {
        if (argument.Length != 0 &&
            !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            result.Append(argument);
            return;
        }

        result.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', checked(backslashes * 2 + 1));
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }
        result.Append('\\', checked(backslashes * 2));
        result.Append('"');
    }

    private static string HandleValue(SafeHandle handle) =>
        unchecked((ulong)handle.DangerousGetHandle().ToInt64())
            .ToString(CultureInfo.InvariantCulture);

    internal static string BuildUnicodeEnvironmentBlockText(
        IEnumerable<KeyValuePair<string, string>> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var environment = new SortedDictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in variables) environment.Add(pair.Key, pair.Value);

        var text = new StringBuilder();
        foreach (var pair in environment)
        {
            text.Append(pair.Key);
            text.Append('=');
            text.Append(pair.Value);
            text.Append('\0');
        }
        if (environment.Count == 0) text.Append('\0');
        text.Append('\0');
        return text.ToString();
    }

    private static WorkerLaunchException Failure(
        string detailCode,
        WorkerLaunchStage stage,
        int nativeErrorCode) =>
        new(detailCode, stage, nativeErrorCode, new Win32Exception(nativeErrorCode));

    private static void CloseRawHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            _ = NativeMethods.CloseHandle(handle);
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed class NativeJobHandle : SafeHandleZeroOrMinusOneIsInvalid, IWindowsJobHandle
    {
        private NativeJobHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private sealed class NativeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal NativeThreadHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private sealed class NativeProcessHandle : IWindowsProcessHandle
    {
        private readonly object _gate = new();
        private SafeProcessHandle? _process;
        private NativeThreadHandle? _primaryThread;
        private readonly bool _createdSuspendedForProof;

        internal NativeProcessHandle(
            SafeProcessHandle process,
            NativeThreadHandle primaryThread,
            int processId,
            bool createdSuspendedForProof)
        {
            _process = process;
            _primaryThread = primaryThread;
            _createdSuspendedForProof = createdSuspendedForProof;
            ProcessId = processId;
            if (!createdSuspendedForProof)
            {
                _primaryThread.Dispose();
                _primaryThread = null;
            }
        }

        public int ProcessId { get; }

        internal SafeProcessHandle ProcessHandle
        {
            get
            {
                lock (_gate)
                {
                    return _process ?? throw new ObjectDisposedException(nameof(NativeProcessHandle));
                }
            }
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            BorrowedProcessWaitHandle waitHandle;
            lock (_gate)
            {
                var process = _process ??
                    throw new ObjectDisposedException(nameof(NativeProcessHandle));
                waitHandle = new BorrowedProcessWaitHandle(process);
            }

            using (waitHandle)
            {
                if (waitHandle.WaitOne(0)) return;

                var completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                RegisteredWaitHandle? registration = null;
                CancellationTokenRegistration cancellationRegistration = default;
                try
                {
                    registration = ThreadPool.RegisterWaitForSingleObject(
                        waitHandle,
                        static (state, _) =>
                            ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                        completion,
                        Timeout.Infinite,
                        executeOnlyOnce: true);
                    if (cancellationToken.CanBeCanceled)
                    {
                        cancellationRegistration = cancellationToken.Register(
                            static state =>
                                ((TaskCompletionSource<bool>)state!).TrySetResult(false),
                            completion);
                    }

                    if (!await completion.Task.ConfigureAwait(false))
                        throw new OperationCanceledException(cancellationToken);
                }
                finally
                {
                    cancellationRegistration.Dispose();
                    registration?.Unregister(null);
                }
            }
        }

        internal void ResumePrimaryThreadForContainmentProof()
        {
            NativeThreadHandle primaryThread;
            lock (_gate)
            {
                if (!_createdSuspendedForProof)
                {
                    throw new InvalidOperationException(
                        "Only a containment-proof process may be resumed explicitly.");
                }
                primaryThread = _primaryThread ?? throw new InvalidOperationException(
                    "The containment-proof primary thread has already been resumed or disposed.");
                _primaryThread = null;
            }

            using (primaryThread)
            {
                var previousSuspendCount = NativeMethods.ResumeThread(primaryThread);
                if (previousSuspendCount == uint.MaxValue)
                {
                    throw Failure(
                        "containment_resume_failed",
                        WorkerLaunchStage.ResumePrimaryThread,
                        Marshal.GetLastWin32Error());
                }
                if (previousSuspendCount != 1)
                {
                    throw new WorkerLaunchException(
                        "containment_resume_failed",
                        WorkerLaunchStage.ResumePrimaryThread);
                }
            }
        }

        public void Dispose()
        {
            SafeProcessHandle? process;
            NativeThreadHandle? primaryThread;
            lock (_gate)
            {
                process = _process;
                primaryThread = _primaryThread;
                _process = null;
                _primaryThread = null;
            }
            primaryThread?.Dispose();
            process?.Dispose();
        }
    }

    private sealed class BorrowedProcessWaitHandle : WaitHandle
    {
        private readonly SafeProcessHandle _process;
        private bool _addedReference;

        internal BorrowedProcessWaitHandle(SafeProcessHandle process)
        {
            _process = process;
            var addedReference = false;
            try
            {
                process.DangerousAddRef(ref addedReference);
                _addedReference = addedReference;
                SafeWaitHandle = new SafeWaitHandle(
                    process.DangerousGetHandle(),
                    ownsHandle: false);
            }
            catch
            {
                if (addedReference) process.DangerousRelease();
                throw;
            }
        }

        protected override void Dispose(bool explicitDisposing)
        {
            base.Dispose(explicitDisposing);
            if (!_addedReference) return;
            _addedReference = false;
            _process.DangerousRelease();
        }
    }

    private sealed class NativeWorkerPipeSet : IWindowsWorkerPipeSet
    {
        private readonly object _gate = new();
        private SafeFileHandle? _standardInputChild;
        private SafeFileHandle? _requestChild;
        private SafeFileHandle? _eventChild;
        private SafeFileHandle? _standardOutputChild;
        private SafeFileHandle? _standardErrorChild;
        private bool _disposed;

        internal NativeWorkerPipeSet(
            SafeFileHandle standardInputChild,
            SafeFileHandle requestChild,
            SafeFileHandle eventChild,
            SafeFileHandle standardOutputChild,
            SafeFileHandle standardErrorChild,
            Stream requestWriter,
            Stream eventReader,
            Stream standardOutputReader,
            Stream standardErrorReader)
        {
            _standardInputChild = standardInputChild;
            _requestChild = requestChild;
            _eventChild = eventChild;
            _standardOutputChild = standardOutputChild;
            _standardErrorChild = standardErrorChild;
            RequestWriter = requestWriter;
            EventReader = eventReader;
            StandardOutputReader = standardOutputReader;
            StandardErrorReader = standardErrorReader;
        }

        public int ChildHandleCount => 5;
        public Stream RequestWriter { get; }
        public Stream EventReader { get; }
        public Stream StandardOutputReader { get; }
        public Stream StandardErrorReader { get; }

        internal SafeFileHandle StandardInputChild => ChildHandle(_standardInputChild);
        internal SafeFileHandle RequestChild => ChildHandle(_requestChild);
        internal SafeFileHandle EventChild => ChildHandle(_eventChild);
        internal SafeFileHandle StandardOutputChild => ChildHandle(_standardOutputChild);
        internal SafeFileHandle StandardErrorChild => ChildHandle(_standardErrorChild);

        internal IReadOnlyList<SafeFileHandle> ChildHandles =>
        [
            StandardInputChild,
            RequestChild,
            EventChild,
            StandardOutputChild,
            StandardErrorChild,
        ];

        public void CloseChildEnds()
        {
            SafeFileHandle? standardInput;
            SafeFileHandle? request;
            SafeFileHandle? events;
            SafeFileHandle? standardOutput;
            SafeFileHandle? standardError;
            lock (_gate)
            {
                standardInput = _standardInputChild;
                request = _requestChild;
                events = _eventChild;
                standardOutput = _standardOutputChild;
                standardError = _standardErrorChild;
                _standardInputChild = null;
                _requestChild = null;
                _eventChild = null;
                _standardOutputChild = null;
                _standardErrorChild = null;
            }
            standardInput?.Dispose();
            request?.Dispose();
            events?.Dispose();
            standardOutput?.Dispose();
            standardError?.Dispose();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            CloseChildEnds();
            RequestWriter.Dispose();
            EventReader.Dispose();
            StandardOutputReader.Dispose();
            StandardErrorReader.Dispose();
        }

        private SafeFileHandle ChildHandle(SafeFileHandle? handle)
        {
            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(NativeWorkerPipeSet));
                return handle ?? throw new InvalidOperationException(
                    "The worker child handles have already been closed.");
            }
        }
    }

    private sealed class PipePair : IDisposable
    {
        private SafeFileHandle? _supervisor;
        private SafeFileHandle? _child;

        internal PipePair(SafeFileHandle supervisor, SafeFileHandle child)
        {
            _supervisor = supervisor;
            _child = child;
        }

        internal SafeFileHandle TakeSupervisorEnd()
        {
            var result = _supervisor ?? throw new InvalidOperationException(
                "The supervisor pipe end has already been transferred.");
            _supervisor = null;
            return result;
        }

        internal SafeFileHandle TakeChildEnd()
        {
            var result = _child ?? throw new InvalidOperationException(
                "The child pipe end has already been transferred.");
            _child = null;
            return result;
        }

        public void Dispose()
        {
            _supervisor?.Dispose();
            _child?.Dispose();
            _supervisor = null;
            _child = null;
        }
    }

    private sealed class ProcessAttributeList : IDisposable
    {
        private IntPtr _attributeList;
        private IntPtr _jobValues;
        private IntPtr _handleValues;
        private bool _initialized;

        internal ProcessAttributeList(
            NativeJobHandle job,
            IReadOnlyList<SafeFileHandle> childHandles)
        {
            if (childHandles.Count != 5)
                throw new ArgumentException("Exactly five child handles are required.", nameof(childHandles));

            try
            {
                nuint size = 0;
                var sizingSucceeded = NativeMethods.InitializeProcThreadAttributeList(
                    IntPtr.Zero,
                    attributeCount: 2,
                    flags: 0,
                    ref size);
                var sizingError = Marshal.GetLastWin32Error();
                if (sizingSucceeded || size == 0 || sizingError != ErrorInsufficientBuffer)
                {
                    throw Failure(
                        "containment_setup_failed",
                        WorkerLaunchStage.InitializeAttributeList,
                        sizingError);
                }

                _attributeList = Marshal.AllocHGlobal(checked((nint)size));
                if (!NativeMethods.InitializeProcThreadAttributeList(
                        _attributeList,
                        attributeCount: 2,
                        flags: 0,
                        ref size))
                {
                    throw Failure(
                        "containment_setup_failed",
                        WorkerLaunchStage.InitializeAttributeList,
                        Marshal.GetLastWin32Error());
                }
                _initialized = true;

                _jobValues = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(_jobValues, job.DangerousGetHandle());
                if (!NativeMethods.UpdateProcThreadAttribute(
                        _attributeList,
                        flags: 0,
                        ProcThreadAttributeJobList,
                        _jobValues,
                        (nuint)IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw Failure(
                        "containment_setup_failed",
                        WorkerLaunchStage.AddJobAttribute,
                        Marshal.GetLastWin32Error());
                }

                _handleValues = Marshal.AllocHGlobal(
                    checked(IntPtr.Size * childHandles.Count));
                for (var index = 0; index < childHandles.Count; index++)
                {
                    Marshal.WriteIntPtr(
                        _handleValues,
                        checked(index * IntPtr.Size),
                        childHandles[index].DangerousGetHandle());
                }
                if (!NativeMethods.UpdateProcThreadAttribute(
                        _attributeList,
                        flags: 0,
                        ProcThreadAttributeHandleList,
                        _handleValues,
                        checked((nuint)(IntPtr.Size * childHandles.Count)),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw Failure(
                        "containment_setup_failed",
                        WorkerLaunchStage.AddHandleAttribute,
                        Marshal.GetLastWin32Error());
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal IntPtr Pointer => _attributeList;

        public void Dispose()
        {
            if (_initialized)
            {
                NativeMethods.DeleteProcThreadAttributeList(_attributeList);
                _initialized = false;
            }
            if (_handleValues != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_handleValues);
                _handleValues = IntPtr.Zero;
            }
            if (_jobValues != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_jobValues);
                _jobValues = IntPtr.Zero;
            }
            if (_attributeList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_attributeList);
                _attributeList = IntPtr.Zero;
            }
        }
    }

    private sealed class UnicodeEnvironmentBlock : IDisposable
    {
        private IntPtr _pointer;

        private UnicodeEnvironmentBlock(IntPtr pointer)
        {
            _pointer = pointer;
        }

        internal IntPtr Pointer => _pointer;

        internal static UnicodeEnvironmentBlock Create(
            IReadOnlyDictionary<string, string> source,
            NativeWorkerPipeSet pipes)
        {
            var variables = new List<KeyValuePair<string, string>>(source.Count + 2);
            variables.AddRange(source);
            variables.Add(new(
                WorkerBootstrapEnvironment.RequestHandle,
                HandleValue(pipes.RequestChild)));
            variables.Add(new(
                WorkerBootstrapEnvironment.EventHandle,
                HandleValue(pipes.EventChild)));

            var characters = BuildUnicodeEnvironmentBlockText(variables).ToCharArray();
            var pointer = IntPtr.Zero;
            try
            {
                pointer = Marshal.AllocHGlobal(checked(characters.Length * sizeof(char)));
                Marshal.Copy(characters, 0, pointer, characters.Length);
                return new UnicodeEnvironmentBlock(pointer);
            }
            catch
            {
                if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer);
                throw;
            }
        }

        public void Dispose()
        {
            if (_pointer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(_pointer);
            _pointer = IntPtr.Zero;
        }
    }

    private sealed class SafeHandleLease : IDisposable
    {
        private readonly SafeHandle[] _handles;
        private int _added;

        internal SafeHandleLease(params SafeHandle[] handles)
        {
            _handles = handles;
            try
            {
                foreach (var handle in handles)
                {
                    var success = false;
                    handle.DangerousAddRef(ref success);
                    if (!success)
                        throw new ObjectDisposedException(handle.GetType().Name);
                    _added++;
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            while (_added > 0)
            {
                _added--;
                _handles[_added].DangerousRelease();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal uint Length;
        internal IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;

        internal static SecurityAttributes Inheritable() => new()
        {
            Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal uint Size;
        internal IntPtr Reserved;
        internal IntPtr Desktop;
        internal IntPtr Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort Reserved2Count;
        internal IntPtr Reserved2;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern NativeJobHandle CreateJobObjectW(
            IntPtr jobAttributes,
            string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            NativeJobHandle job,
            uint informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(
            NativeJobHandle job,
            uint informationClass,
            out JobObjectExtendedLimitInformation information,
            uint informationLength,
            out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(
            out IntPtr readPipe,
            out IntPtr writePipe,
            ref SecurityAttributes pipeAttributes,
            uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(
            SafeFileHandle handle,
            uint mask,
            uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            ref SecurityAttributes securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            uint attributeCount,
            uint flags,
            ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            nuint attribute,
            IntPtr value,
            nuint size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(
            SafeProcessHandle process,
            NativeJobHandle job,
            [MarshalAs(UnmanagedType.Bool)] out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(NativeThreadHandle thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
