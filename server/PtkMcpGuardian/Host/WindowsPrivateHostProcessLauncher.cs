using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PtkMcpGuardian.Lifecycle;

namespace PtkMcpGuardian.Host;

/// <summary>
/// Captures identity-fenced process leases while the outer Job handle is
/// still open.
/// </summary>
internal interface IWindowsJobContainmentTracker
{
    /// <summary>
    /// The implementation must not retain <paramref name="jobHandle"/> or
    /// <paramref name="hostHandle"/>; the launcher closes the kill-on-close
    /// lease immediately after this method returns.
    /// </summary>
    IWindowsJobContainmentLease? Capture(nint jobHandle, nint hostHandle);
}

internal interface IWindowsJobContainmentLease : IDisposable
{
    Task Confirmation { get; }
}

/// <summary>
/// Windows private-host launcher. The child is created suspended in the outer
/// kill-on-close Job and receives only the two private bootstrap handles plus
/// launcher-owned NUL standard handles through an explicit handle list.
/// </summary>
internal sealed class WindowsPrivateHostProcessLauncher : IPrivateHostProcessLauncher
{
    internal const uint KillOnJobClose = 0x00002000;

    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorMoreData = 234;
    private const uint JobObjectBasicProcessIdListClass = 3;
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
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const int MaximumTrackedJobProcesses = 65536;
    private static readonly TimeSpan ContainmentPollInterval =
        TimeSpan.FromMilliseconds(25);

    private readonly IWindowsJobContainmentTracker _containmentTracker;

    internal WindowsPrivateHostProcessLauncher(
        IWindowsJobContainmentTracker? containmentTracker = null) =>
        _containmentTracker = containmentTracker ?? new NativeJobContainmentTracker();

    public PrivateHostProcessLaunchResult Launch(PrivateHostLaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows private-host containment requires Windows 10 or Windows Server 2016 or newer.");
        }

        NativeJobHandle? outerJob = null;
        SafeFileHandle? nullInput = null;
        SafeFileHandle? nullOutput = null;
        SafeFileHandle? requestHandle = null;
        SafeFileHandle? eventHandle = null;
        WindowsPrivateHostAuthority? authority = null;
        NativeCreationInformation creation = default;
        var childCreated = false;
        try
        {
            outerJob = CreateOuterJob();
            ConfigureOuterJob(outerJob);
            if (QueryOuterJobFlags(outerJob) != KillOnJobClose)
                return NoChild();

            nullInput = OpenNullHandle(forInput: true);
            nullOutput = OpenNullHandle(forInput: false);
            requestHandle = BorrowHandle(command.InheritedHandles[0]);
            eventHandle = BorrowHandle(command.InheritedHandles[1]);
            SafeFileHandle[] inheritedHandles =
            [
                requestHandle,
                eventHandle,
                nullInput,
                nullOutput,
            ];
            if (inheritedHandles.Any(handle => !IsInheritable(handle)))
                return NoChild();

            using var handleLease = new SafeHandleLease(
                [outerJob, .. inheritedHandles]);
            using var attributes = new NativeAttributeList(outerJob, inheritedHandles);
            using var environment = UnicodeEnvironmentBlock.Create(command.Environment);
            var startup = new NativeStartupInfoEx
            {
                StartupInfo = new NativeStartupInfo
                {
                    Size = (uint)Marshal.SizeOf<NativeStartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = nullInput.DangerousGetHandle(),
                    StandardOutput = nullOutput.DangerousGetHandle(),
                    StandardError = nullOutput.DangerousGetHandle(),
                },
                AttributeList = attributes.Pointer,
            };
            var commandLine = new StringBuilder(BuildCommandLine(command));
            var creationFlags = ExtendedStartupInfoPresent |
                CreateUnicodeEnvironment |
                CreateNoWindow |
                CreateSuspended;

            if (!NativeMethods.CreateProcessW(
                    command.ExecutablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    creationFlags,
                    environment.Pointer,
                    command.WorkingDirectory,
                    ref startup,
                    out creation))
            {
                return NoChild();
            }
            childCreated = true;

            NativeHostHandle? hostHandle = new(
                creation.HostHandle,
                ownsHandle: true);
            creation.HostHandle = IntPtr.Zero;
            NativeThreadHandle? primaryThread = new(
                creation.PrimaryThread,
                ownsHandle: true);
            creation.PrimaryThread = IntPtr.Zero;
            try
            {
                authority = new WindowsPrivateHostAuthority(
                    outerJob,
                    hostHandle,
                    primaryThread,
                    checked((int)creation.HostId),
                    _containmentTracker);
                outerJob = null;
                hostHandle = null;
                primaryThread = null;
            }
            finally
            {
                hostHandle?.Dispose();
                primaryThread?.Dispose();
            }

            if (!IsContainedAtCreation(authority))
            {
                authority.AbortBeforeRelease();
                return Started(authority);
            }
            if (!authority.ReleaseFromSuspension())
                return Started(authority);
            return Started(authority);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            if (authority is not null)
            {
                authority.AbortBeforeRelease();
                return Started(authority);
            }

            // Before CreateProcessW succeeds, cleanup proves no child. If a
            // post-create ownership construction unexpectedly fails, closing
            // the sole Job lease is still the first rollback action; the
            // failure remains loud because no durable wait authority exists.
            if (!childCreated)
                return NoChild();
            outerJob?.Dispose();
            outerJob = null;
            throw new InvalidOperationException(
                "The Windows private-host launch lost post-create ownership.",
                exception);
        }
        finally
        {
            outerJob?.Dispose();
            requestHandle?.Dispose();
            eventHandle?.Dispose();
            nullInput?.Dispose();
            nullOutput?.Dispose();
            CloseRawHandle(creation.HostHandle);
            CloseRawHandle(creation.PrimaryThread);
        }
    }

    internal static string BuildCommandLine(PrivateHostLaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = new StringBuilder();
        AppendQuotedArgument(result, command.ExecutablePath);
        foreach (var argument in command.Arguments)
        {
            result.Append(' ');
            AppendQuotedArgument(result, argument);
        }
        return result.ToString();
    }

    internal static string BuildEnvironmentBlockText(
        IEnumerable<KeyValuePair<string, string>> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var environment = new SortedDictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in variables)
        {
            ValidateEnvironmentPair(pair);
            environment.Add(pair.Key, pair.Value);
        }

        var text = new StringBuilder();
        foreach (var pair in environment)
        {
            text.Append(pair.Key);
            text.Append('=');
            text.Append(pair.Value);
            text.Append('\0');
        }
        if (environment.Count == 0)
            text.Append('\0');
        text.Append('\0');
        return text.ToString();
    }

    private static void ValidateEnvironmentPair(KeyValuePair<string, string> pair)
    {
        ArgumentException.ThrowIfNullOrEmpty(pair.Key);
        ArgumentNullException.ThrowIfNull(pair.Value);
        if (pair.Key.Contains('\0') || pair.Value.Contains('\0') ||
            (pair.Key[0] != '=' && pair.Key.Contains('=')) ||
            (pair.Key[0] == '=' && pair.Key.AsSpan(1).Contains('=')))
        {
            throw new ArgumentException("The environment contains an invalid name or value.");
        }
    }

    private static NativeJobHandle CreateOuterJob()
    {
        var job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (!job.IsInvalid) return job;
        job.Dispose();
        throw NativeFailure("outer_job_create_failed");
    }

    private static void ConfigureOuterJob(NativeJobHandle job)
    {
        var information = new NativeJobExtendedLimitInformation
        {
            BasicLimitInformation = new NativeJobBasicLimitInformation
            {
                LimitFlags = KillOnJobClose,
            },
        };
        if (NativeMethods.SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformationClass,
                ref information,
                (uint)Marshal.SizeOf<NativeJobExtendedLimitInformation>()))
        {
            return;
        }
        throw NativeFailure("outer_job_configure_failed");
    }

    private static uint QueryOuterJobFlags(NativeJobHandle job)
    {
        if (NativeMethods.QueryInformationJobObject(
                job,
                JobObjectExtendedLimitInformationClass,
                out var information,
                (uint)Marshal.SizeOf<NativeJobExtendedLimitInformation>(),
                out _))
        {
            return information.BasicLimitInformation.LimitFlags;
        }
        throw NativeFailure("outer_job_query_failed");
    }

    private static SafeFileHandle OpenNullHandle(bool forInput)
    {
        var security = NativeSecurityAttributes.Inheritable();
        var handle = NativeMethods.CreateFileW(
            "NUL",
            forInput ? GenericRead : GenericWrite,
            FileShareRead | FileShareWrite | FileShareDelete,
            ref security,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        throw NativeFailure("null_handle_open_failed");
    }

    private static SafeFileHandle BorrowHandle(nuint value) => new(
        IntPtr.Size == sizeof(long)
            ? unchecked((nint)(long)(ulong)value)
            : unchecked((nint)(int)(uint)value),
        ownsHandle: false);

    private static bool IsInheritable(SafeFileHandle handle)
    {
        if (NativeMethods.GetHandleInformation(handle, out var flags))
            return (flags & HandleFlagInherit) == HandleFlagInherit;
        throw NativeFailure("handle_inheritance_query_failed");
    }

    private static bool IsContainedAtCreation(WindowsPrivateHostAuthority authority)
    {
        if (NativeMethods.IsProcessInJob(
                authority.HostHandle,
                authority.OuterJob,
                out var contained))
        {
            return contained;
        }
        return false;
    }

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

    private static PrivateHostProcessLaunchResult Started(
        WindowsPrivateHostAuthority authority) => new(
        GuardianHostLaunchOutcome.Started,
        authority);

    private static PrivateHostProcessLaunchResult NoChild() => new(
        GuardianHostLaunchOutcome.ProvedNoChild,
        process: null);

    private static Win32Exception NativeFailure(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"Windows private-host {operation} (Win32 {error}).");
    }

    private static void CloseRawHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            _ = NativeMethods.CloseHandle(handle);
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed class WindowsPrivateHostAuthority : IPrivateHostLaunchedProcess
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _containment = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private NativeJobHandle? _outerJob;
        private NativeHostHandle? _hostHandle;
        private NativeThreadHandle? _primaryThread;
        private OwnedHostWaitHandle? _waitHandle;
        private RegisteredWaitHandle? _waitRegistration;
        private readonly IWindowsJobContainmentTracker _containmentTracker;
        private IWindowsJobContainmentLease? _containmentLease;
        private int _containmentStarted;
        private bool _disposed;

        internal WindowsPrivateHostAuthority(
            NativeJobHandle outerJob,
            NativeHostHandle hostHandle,
            NativeThreadHandle primaryThread,
            int hostId,
            IWindowsJobContainmentTracker containmentTracker)
        {
            _outerJob = outerJob ?? throw new ArgumentNullException(nameof(outerJob));
            _hostHandle = hostHandle ?? throw new ArgumentNullException(nameof(hostHandle));
            _primaryThread = primaryThread ?? throw new ArgumentNullException(nameof(primaryThread));
            _containmentTracker = containmentTracker ??
                throw new ArgumentNullException(nameof(containmentTracker));
            if (hostId <= 0)
                throw new ArgumentOutOfRangeException(nameof(hostId));
            ProcessId = hostId;

            _waitHandle = new OwnedHostWaitHandle(hostHandle);
            if (_waitHandle.WaitOne(0))
            {
                _exited.TrySetResult();
            }
            else
            {
                _waitRegistration = ThreadPool.RegisterWaitForSingleObject(
                    _waitHandle,
                    static (state, _) =>
                        ((WindowsPrivateHostAuthority)state!).CompleteExit(),
                    this,
                    Timeout.Infinite,
                    executeOnlyOnce: true);
            }
        }

        public int ProcessId { get; }

        public Task Exited => _exited.Task;

        public Task ContainmentConfirmed => _containment.Task;

        internal NativeJobHandle OuterJob
        {
            get
            {
                lock (_sync)
                    return _outerJob ?? throw new ObjectDisposedException(
                        nameof(WindowsPrivateHostAuthority));
            }
        }

        internal NativeHostHandle HostHandle
        {
            get
            {
                lock (_sync)
                    return _hostHandle ?? throw new ObjectDisposedException(
                        nameof(WindowsPrivateHostAuthority));
            }
        }

        internal bool ReleaseFromSuspension()
        {
            NativeThreadHandle primaryThread;
            lock (_sync)
            {
                if (_disposed) return false;
                primaryThread = _primaryThread ?? throw new InvalidOperationException(
                    "The private host release gate has already been consumed.");
                _primaryThread = null;
            }

            using (primaryThread)
            {
                var previousSuspendCount = NativeMethods.ResumeThread(primaryThread);
                if (previousSuspendCount == 1)
                    return true;
            }
            AbortBeforeRelease();
            return false;
        }

        internal void AbortBeforeRelease() => StartContainment();

        public void BeginContainment(GuardianHostContainmentDeadline deadline)
        {
            ArgumentNullException.ThrowIfNull(deadline);
            StartContainment();
        }

        public void Dispose()
        {
            NativeJobHandle? outerJob;
            NativeThreadHandle? primaryThread;
            RegisteredWaitHandle? registration;
            OwnedHostWaitHandle? waitHandle;
            NativeHostHandle? hostHandle;
            IWindowsJobContainmentLease? containmentLease;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                outerJob = _outerJob;
                primaryThread = _primaryThread;
                registration = _waitRegistration;
                waitHandle = _waitHandle;
                hostHandle = _hostHandle;
                containmentLease = _containmentLease;
                _outerJob = null;
                _primaryThread = null;
                _waitRegistration = null;
                _waitHandle = null;
                _hostHandle = null;
                _containmentLease = null;
            }

            outerJob?.Dispose();
            primaryThread?.Dispose();
            registration?.Unregister(null);
            waitHandle?.Dispose();
            hostHandle?.Dispose();
            containmentLease?.Dispose();
        }

        private void CloseOuterJob()
        {
            NativeJobHandle? outerJob;
            lock (_sync)
            {
                outerJob = _outerJob;
                _outerJob = null;
            }
            outerJob?.Dispose();
        }

        private void CompleteExit()
        {
            _exited.TrySetResult();
        }

        private void StartContainment()
        {
            if (Interlocked.Exchange(ref _containmentStarted, 1) != 0)
                return;

            IWindowsJobContainmentLease? containmentLease = null;
            NativeJobHandle? outerJob;
            NativeHostHandle? hostHandle;
            lock (_sync)
            {
                outerJob = _outerJob;
                hostHandle = _hostHandle;
            }
            if (outerJob is not null && hostHandle is not null)
            {
                var addedJobReference = false;
                var addedHostReference = false;
                try
                {
                    outerJob.DangerousAddRef(ref addedJobReference);
                    hostHandle.DangerousAddRef(ref addedHostReference);
                    containmentLease = _containmentTracker.Capture(
                        outerJob.DangerousGetHandle(),
                        hostHandle.DangerousGetHandle());
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    containmentLease?.Dispose();
                    containmentLease = null;
                }
                finally
                {
                    try
                    {
                        if (addedHostReference)
                            hostHandle.DangerousRelease();
                        if (addedJobReference)
                            outerJob.DangerousRelease();
                    }
                    finally
                    {
                        CloseOuterJob();
                    }
                }
            }
            else
            {
                CloseOuterJob();
            }
            if (containmentLease is null)
                return;

            lock (_sync)
            {
                if (_disposed)
                {
                    containmentLease.Dispose();
                    return;
                }
                _containmentLease = containmentLease;
            }
            _ = ObserveContainmentAsync(containmentLease);
        }

        private async Task ObserveContainmentAsync(
            IWindowsJobContainmentLease containmentLease)
        {
            try
            {
                await containmentLease.Confirmation.ConfigureAwait(false);
                _containment.TrySetResult();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // A failed proof remains incomplete; the lifecycle deadline
                // publishes containment-unconfirmed without a false success.
            }
        }
    }

    private sealed class NativeJobContainmentTracker :
        IWindowsJobContainmentTracker
    {
        public IWindowsJobContainmentLease? Capture(
            nint jobHandle,
            nint hostHandle)
        {
            if (jobHandle == IntPtr.Zero || jobHandle == new IntPtr(-1) ||
                hostHandle == IntPtr.Zero || hostHandle == new IntPtr(-1))
                return null;

            var processIds = SnapshotProcessIds(jobHandle);
            if (processIds is null)
                return null;

            var processes = new List<SafeProcessHandle>(processIds.Length + 1);
            var currentProcess = NativeMethods.GetCurrentProcess();
            if (!NativeMethods.DuplicateHandle(
                    currentProcess,
                    hostHandle,
                    currentProcess,
                    out SafeProcessHandle hostIdentity,
                    desiredAccess: 0,
                    inheritHandle: false,
                    options: DuplicateSameAccess))
            {
                hostIdentity.Dispose();
                return null;
            }
            processes.Add(hostIdentity);
            foreach (var processId in processIds)
            {
                var process = NativeMethods.OpenProcess(
                    Synchronize,
                    inheritHandle: false,
                    processId);
                if (!process.IsInvalid)
                {
                    processes.Add(process);
                    continue;
                }

                var error = Marshal.GetLastWin32Error();
                process.Dispose();
                if (error == ErrorInvalidParameter)
                    continue;
                foreach (var opened in processes)
                    opened.Dispose();
                return null;
            }

            return new NativeJobContainmentLease(processes.ToArray());
        }

        private static uint[]? SnapshotProcessIds(nint jobHandle)
        {
            var capacity = 64;
            for (var attempt = 0; attempt < 12; attempt++)
            {
                if (capacity > MaximumTrackedJobProcesses)
                    return null;
                var bytes = checked(8 + capacity * IntPtr.Size);
                var buffer = Marshal.AllocHGlobal(bytes);
                try
                {
                    Marshal.WriteInt32(buffer, 0, 0);
                    Marshal.WriteInt32(buffer, 4, 0);
                    var succeeded = NativeMethods.QueryInformationJobObject(
                        jobHandle,
                        JobObjectBasicProcessIdListClass,
                        buffer,
                        checked((uint)bytes),
                        out _);
                    var assigned = unchecked((uint)Marshal.ReadInt32(buffer, 0));
                    var listed = unchecked((uint)Marshal.ReadInt32(buffer, 4));
                    if (succeeded && assigned == listed && listed <= capacity)
                    {
                        var result = new HashSet<uint>();
                        for (var index = 0; index < listed; index++)
                        {
                            var raw = Marshal.ReadIntPtr(
                                buffer,
                                checked(8 + index * IntPtr.Size));
                            var processId = IntPtr.Size == sizeof(long)
                                ? checked((uint)(ulong)raw.ToInt64())
                                : unchecked((uint)raw.ToInt32());
                            if (processId == 0)
                                return null;
                            result.Add(processId);
                        }
                        return result.Order().ToArray();
                    }

                    if (succeeded || Marshal.GetLastWin32Error() == ErrorMoreData)
                    {
                        var requested = Math.Max(
                            (long)capacity * 2,
                            Math.Max(assigned, listed));
                        if (requested > MaximumTrackedJobProcesses)
                            return null;
                        capacity = checked((int)requested);
                        continue;
                    }
                    return null;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }
    }

    private sealed class NativeJobContainmentLease : IWindowsJobContainmentLease
    {
        private readonly SafeProcessHandle[] _processes;
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _confirmation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal NativeJobContainmentLease(SafeProcessHandle[] processes)
        {
            _processes = processes ?? throw new ArgumentNullException(nameof(processes));
            _ = ObserveAsync();
        }

        public Task Confirmation => _confirmation.Task;

        public void Dispose() => _stop.Cancel();

        private async Task ObserveAsync()
        {
            try
            {
                while (true)
                {
                    var complete = true;
                    foreach (var process in _processes)
                    {
                        var wait = NativeMethods.WaitForSingleObject(process, 0);
                        if (wait == WaitTimeout)
                        {
                            complete = false;
                            continue;
                        }
                        if (wait != WaitObject0)
                            return;
                    }
                    if (complete)
                    {
                        _confirmation.TrySetResult();
                        return;
                    }
                    await Task.Delay(ContainmentPollInterval, _stop.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // Native observation failure leaves containment unconfirmed.
            }
            finally
            {
                foreach (var process in _processes)
                    process.Dispose();
            }
        }

    }

    private sealed class NativeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private NativeJobHandle() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private sealed class NativeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal NativeThreadHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle) =>
            SetHandle(handle);

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private sealed class NativeHostHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal NativeHostHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle) =>
            SetHandle(handle);

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private sealed class OwnedHostWaitHandle : WaitHandle
    {
        internal OwnedHostWaitHandle(NativeHostHandle hostHandle)
        {
            var current = NativeMethods.GetCurrentProcess();
            SafeWaitHandle? duplicate = null;
            try
            {
                if (!NativeMethods.DuplicateHandle(
                        current,
                        hostHandle,
                        current,
                        out duplicate,
                        desiredAccess: 0,
                        inheritHandle: false,
                        options: DuplicateSameAccess))
                {
                    throw NativeFailure("wait_handle_duplicate_failed");
                }
                SafeWaitHandle = duplicate;
                duplicate = null;
            }
            finally
            {
                duplicate?.Dispose();
            }
        }
    }

    private sealed class NativeAttributeList : IDisposable
    {
        private IntPtr _attributes;
        private IntPtr _jobValues;
        private IntPtr _handleValues;
        private bool _initialized;

        internal NativeAttributeList(
            NativeJobHandle outerJob,
            IReadOnlyList<SafeFileHandle> inheritedHandles)
        {
            if (inheritedHandles.Count != 4)
                throw new ArgumentException("Exactly four private child handles are required.");
            try
            {
                nuint size = 0;
                var sized = NativeMethods.InitializeProcThreadAttributeList(
                    IntPtr.Zero,
                    attributeCount: 2,
                    flags: 0,
                    ref size);
                var sizingError = Marshal.GetLastWin32Error();
                if (sized || size == 0 || sizingError != ErrorInsufficientBuffer)
                    throw NativeFailure("attribute_list_size_failed");

                _attributes = Marshal.AllocHGlobal(checked((nint)size));
                if (!NativeMethods.InitializeProcThreadAttributeList(
                        _attributes,
                        attributeCount: 2,
                        flags: 0,
                        ref size))
                {
                    throw NativeFailure("attribute_list_initialize_failed");
                }
                _initialized = true;

                _jobValues = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(_jobValues, outerJob.DangerousGetHandle());
                if (!NativeMethods.UpdateProcThreadAttribute(
                        _attributes,
                        flags: 0,
                        ProcThreadAttributeJobList,
                        _jobValues,
                        (nuint)IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw NativeFailure("job_attribute_failed");
                }

                _handleValues = Marshal.AllocHGlobal(
                    checked(IntPtr.Size * inheritedHandles.Count));
                for (var index = 0; index < inheritedHandles.Count; index++)
                {
                    Marshal.WriteIntPtr(
                        _handleValues,
                        checked(index * IntPtr.Size),
                        inheritedHandles[index].DangerousGetHandle());
                }
                if (!NativeMethods.UpdateProcThreadAttribute(
                        _attributes,
                        flags: 0,
                        ProcThreadAttributeHandleList,
                        _handleValues,
                        checked((nuint)(IntPtr.Size * inheritedHandles.Count)),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw NativeFailure("handle_attribute_failed");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal IntPtr Pointer => _attributes;

        public void Dispose()
        {
            if (_initialized)
            {
                NativeMethods.DeleteProcThreadAttributeList(_attributes);
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
            if (_attributes != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_attributes);
                _attributes = IntPtr.Zero;
            }
        }
    }

    private sealed class UnicodeEnvironmentBlock : IDisposable
    {
        private IntPtr _pointer;

        private UnicodeEnvironmentBlock(IntPtr pointer) => _pointer = pointer;

        internal IntPtr Pointer => _pointer;

        internal static UnicodeEnvironmentBlock Create(
            IEnumerable<KeyValuePair<string, string>> variables)
        {
            var characters = BuildEnvironmentBlockText(variables).ToCharArray();
            var pointer = IntPtr.Zero;
            try
            {
                pointer = Marshal.AllocHGlobal(checked(characters.Length * sizeof(char)));
                Marshal.Copy(characters, 0, pointer, characters.Length);
                return new UnicodeEnvironmentBlock(pointer);
            }
            catch
            {
                if (pointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(pointer);
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

        internal SafeHandleLease(SafeHandle[] handles)
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
    private struct NativeSecurityAttributes
    {
        internal uint Length;
        internal IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;

        internal static NativeSecurityAttributes Inheritable() => new()
        {
            Length = (uint)Marshal.SizeOf<NativeSecurityAttributes>(),
            InheritHandle = true,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeJobBasicLimitInformation
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
    private struct NativeIoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeJobExtendedLimitInformation
    {
        internal NativeJobBasicLimitInformation BasicLimitInformation;
        internal NativeIoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStartupInfo
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
    private struct NativeStartupInfoEx
    {
        internal NativeStartupInfo StartupInfo;
        internal IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCreationInformation
    {
        internal IntPtr HostHandle;
        internal IntPtr PrimaryThread;
        internal uint HostId;
        internal uint PrimaryThreadId;
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
            ref NativeJobExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(
            NativeJobHandle job,
            uint informationClass,
            out NativeJobExtendedLimitInformation information,
            uint informationLength,
            out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(
            IntPtr job,
            uint informationClass,
            IntPtr information,
            uint informationLength,
            out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(
            SafeProcessHandle handle,
            uint milliseconds);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            ref NativeSecurityAttributes securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetHandleInformation(
            SafeFileHandle handle,
            out uint flags);

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
            ref NativeStartupInfoEx startupInfo,
            out NativeCreationInformation creationInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(
            NativeHostHandle hostHandle,
            NativeJobHandle job,
            [MarshalAs(UnmanagedType.Bool)] out bool result);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(
            IntPtr sourceProcessHandle,
            NativeHostHandle sourceHandle,
            IntPtr targetProcessHandle,
            out SafeWaitHandle targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(
            IntPtr sourceProcessHandle,
            IntPtr sourceHandle,
            IntPtr targetProcessHandle,
            out SafeProcessHandle targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(NativeThreadHandle thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
