using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PtkContainmentTestFixture;

/// <summary>
/// Disposable Windows-only owner used by the R0 nested-Job feasibility test.
/// It is deliberately isolated from the product containment implementation.
/// </summary>
public sealed class WindowsNestedJobGuardianFixture : IDisposable
{
    private readonly object _gate = new();
    private readonly SuspendedNestedProcess _host;
    private SafeNestedJobHandle? _outerJob;
    private int _disposed;

    private WindowsNestedJobGuardianFixture(
        SafeNestedJobHandle outerJob,
        SuspendedNestedProcess host)
    {
        _outerJob = outerJob;
        _host = host;
    }

    public int HostProcessId => _host.ProcessId;
    public StreamWriter HostCommands => _host.CommandWriter;
    public StreamReader HostEvents => _host.EventReader;
    public bool OuterJobHandleIsNonInheritable { get; private init; }
    public uint OuterJobLimitFlags { get; private init; }

    public static WindowsNestedJobGuardianFixture Launch(string scratchDirectory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The nested Job probe requires Windows.");
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);
        if (!Path.IsPathFullyQualified(scratchDirectory))
            throw new ArgumentException("The nested Job scratch directory must be absolute.", nameof(scratchDirectory));

        SafeNestedJobHandle? outerJob = null;
        SuspendedNestedProcess? host = null;
        try
        {
            outerJob = WindowsNestedJobNative.CreateKillOnCloseJob();
            var limitFlags = WindowsNestedJobNative.QueryJobLimitFlags(outerJob);
            var nonInheritable = WindowsNestedJobNative.IsHandleNonInheritable(outerJob);
            host = WindowsNestedJobNative.LaunchSuspended(
                NestedFixtureCommand.Create(
                    "nested-host",
                    scratchDirectory,
                    WindowsNestedJobNative.HandleValue(outerJob)),
                outerJob);

            var result = new WindowsNestedJobGuardianFixture(outerJob, host)
            {
                OuterJobHandleIsNonInheritable = nonInheritable,
                OuterJobLimitFlags = limitFlags,
            };
            outerJob = null;
            host = null;
            return result;
        }
        finally
        {
            // The job closes first so every successfully created child remains
            // contained even when construction fails after CreateProcessW.
            outerJob?.Dispose();
            host?.Dispose();
        }
    }

    public void ResumeHost() => _host.ResumeOnce();

    public bool IsInOuterJob(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (_gate)
        {
            var outerJob = _outerJob ?? throw new ObjectDisposedException(
                nameof(WindowsNestedJobGuardianFixture),
                "The outer Job handle has already closed.");
            return WindowsNestedJobNative.IsProcessInJob(process.SafeHandle, outerJob);
        }
    }

    public void CloseOuterJob()
    {
        SafeNestedJobHandle? outerJob;
        lock (_gate)
        {
            outerJob = _outerJob;
            _outerJob = null;
        }
        outerJob?.Dispose();
    }

    public string CompletedHostError => _host.CompletedError;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CloseOuterJob();
        _host.Dispose();
    }
}

internal static class WindowsNestedJobFixture
{
    private const string CreateWorkerCommand = "create-worker";
    private const string ReleaseWorkerCommand = "release-worker";
    private const string SpawnDescendantCommand = "spawn-descendant";

    internal static int RunHost(string encodedScratchPath, string outerJobHandle)
    {
        if (!OperatingSystem.IsWindows()) return 69;
        WindowsNestedJobNative.ClearInheritedControlHandleFlags();
        WindowsNestedJobNative.RequireJobHandleAbsent(outerJobHandle);
        var scratchDirectory = NestedFixtureCommand.DecodeScratchPath(encodedScratchPath);
        WriteMarker(scratchDirectory, "host-entered.marker");
        WritePublicEvent($"host-entered:{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}");

        RequireCommand(Console.In, CreateWorkerCommand);
        using var innerJob = WindowsNestedJobNative.CreateKillOnCloseJob();
        using var worker = WindowsNestedJobNative.LaunchSuspended(
            NestedFixtureCommand.Create(
                "nested-worker",
                scratchDirectory,
                outerJobHandle,
                WindowsNestedJobNative.HandleValue(innerJob)),
            innerJob);
        if (!WindowsNestedJobNative.IsProcessInJob(worker.ProcessHandle, innerJob))
            throw new InvalidOperationException("The gated worker was not in its exact inner Job.");

        WritePublicEvent($"worker-gated:{worker.ProcessId.ToString(CultureInfo.InvariantCulture)}");
        RequireCommand(Console.In, ReleaseWorkerCommand);
        worker.ResumeOnce();

        var workerEntered = ReadRequiredEvent(worker.EventReader, "worker-entered:");
        if (ParseProcessId(workerEntered, "worker-entered:") != worker.ProcessId)
            throw new InvalidDataException("The released worker identity changed.");
        WritePublicEvent(workerEntered);

        RequireCommand(Console.In, SpawnDescendantCommand);
        worker.CommandWriter.WriteLine(SpawnDescendantCommand);
        var descendantEvent = ReadRequiredEvent(worker.EventReader, "descendant:");
        var descendantProcessId = ParseProcessId(descendantEvent, "descendant:");
        using (var descendant = Process.GetProcessById(descendantProcessId))
        {
            _ = descendant.SafeHandle;
            if (!WindowsNestedJobNative.IsProcessInJob(descendant.SafeHandle, innerJob))
            {
                throw new InvalidOperationException(
                    "The released worker descendant was not in its exact inner Job.");
            }
        }

        WritePublicEvent(
            $"descendant-ready:{descendantProcessId.ToString(CultureInfo.InvariantCulture)}");
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    internal static int RunWorker(
        string encodedScratchPath,
        string outerJobHandle,
        string innerJobHandle)
    {
        if (!OperatingSystem.IsWindows()) return 69;
        WindowsNestedJobNative.ClearInheritedControlHandleFlags();
        WindowsNestedJobNative.RequireJobHandleAbsent(outerJobHandle);
        WindowsNestedJobNative.RequireJobHandleAbsent(innerJobHandle);
        var scratchDirectory = NestedFixtureCommand.DecodeScratchPath(encodedScratchPath);
        WriteMarker(scratchDirectory, "worker-entered.marker");
        WritePublicEvent($"worker-entered:{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}");

        RequireCommand(Console.In, SpawnDescendantCommand);
        using var descendant = StartOrdinaryDescendant(
            scratchDirectory,
            outerJobHandle,
            innerJobHandle);
        WritePublicEvent($"descendant:{descendant.Id.ToString(CultureInfo.InvariantCulture)}");
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    internal static int RunDescendant(
        string encodedScratchPath,
        string outerJobHandle,
        string innerJobHandle)
    {
        if (!OperatingSystem.IsWindows()) return 69;
        WindowsNestedJobNative.ClearInheritedControlHandleFlags();
        WindowsNestedJobNative.RequireJobHandleAbsent(outerJobHandle);
        WindowsNestedJobNative.RequireJobHandleAbsent(innerJobHandle);
        var scratchDirectory = NestedFixtureCommand.DecodeScratchPath(encodedScratchPath);
        WriteMarker(scratchDirectory, "descendant-entered.marker");
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static Process StartOrdinaryDescendant(
        string scratchDirectory,
        string outerJobHandle,
        string innerJobHandle)
    {
        var command = NestedFixtureCommand.Create(
            "nested-descendant",
            scratchDirectory,
            outerJobHandle,
            innerJobHandle);
        var startInfo = new ProcessStartInfo
        {
            FileName = command.ExecutablePath,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException(
            "The ordinary nested-Job descendant did not start.");
    }

    private static void RequireCommand(TextReader reader, string expected)
    {
        var actual = reader.ReadLine();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Expected fixture command '{expected}'.");
    }

    private static string ReadRequiredEvent(StreamReader reader, string prefix)
    {
        var line = reader.ReadLine();
        if (line is null)
            throw new EndOfStreamException("A nested-Job child closed its event pipe.");
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("A nested-Job child emitted an invalid event.");
        return line;
    }

    private static int ParseProcessId(string line, string prefix)
    {
        if (!line.StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(
                line.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var processId) ||
            processId <= 0)
        {
            throw new InvalidDataException("A nested-Job event contained an invalid process ID.");
        }
        return processId;
    }

    private static void WriteMarker(string scratchDirectory, string name)
    {
        File.WriteAllText(
            Path.Combine(scratchDirectory, name),
            "entered\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WritePublicEvent(string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }
}

internal sealed record NestedFixtureCommand(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory)
{
    internal static NestedFixtureCommand Create(
        string role,
        string scratchDirectory,
        params string[] additionalArguments)
    {
        var assemblyPath = typeof(FixtureAssemblyMarker).Assembly.Location;
        var assemblyDirectory = Path.GetDirectoryName(assemblyPath) ?? throw new InvalidOperationException(
            "The containment fixture directory is unavailable.");
        var encodedScratchPath = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(Path.GetFullPath(scratchDirectory)));
        var roleArguments = new[] { role, encodedScratchPath }
            .Concat(additionalArguments)
            .ToArray();
        var appHost = Path.Combine(assemblyDirectory, "PtkContainmentTestFixture.exe");
        if (File.Exists(appHost))
            return new NestedFixtureCommand(appHost, roleArguments, assemblyDirectory);

        return new NestedFixtureCommand(
            ResolveDotnetHost(),
            [assemblyPath, .. roleArguments],
            assemblyDirectory);
    }

    internal static string DecodeScratchPath(string encodedScratchPath)
    {
        byte[] encoded;
        try
        {
            encoded = Convert.FromBase64String(encodedScratchPath);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The nested-Job scratch path was not valid base64.", exception);
        }

        string path;
        try
        {
            path = new UTF8Encoding(false, true).GetString(encoded);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The nested-Job scratch path was not strict UTF-8.", exception);
        }
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new InvalidDataException("The nested-Job scratch path was not an existing absolute directory.");
        return path;
    }

    internal string BuildCommandLine()
    {
        var result = new StringBuilder();
        AppendQuotedArgument(result, ExecutablePath);
        foreach (var argument in Arguments)
        {
            result.Append(' ');
            AppendQuotedArgument(result, argument);
        }
        return result.ToString();
    }

    private static string ResolveDotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) &&
            Path.IsPathFullyQualified(configured) &&
            File.Exists(configured))
        {
            return configured;
        }

        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = runtime.Parent?.Parent?.Parent ?? throw new InvalidOperationException(
            "The dotnet host directory is unavailable.");
        var inferred = Path.Combine(dotnetRoot.FullName, "dotnet.exe");
        return File.Exists(inferred)
            ? inferred
            : throw new FileNotFoundException("The dotnet host executable is unavailable.", inferred);
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
}

internal sealed class SuspendedNestedProcess : IDisposable
{
    private readonly SafeProcessHandle _process;
    private SafeNestedThreadHandle? _primaryThread;
    private readonly StreamReader _errorReader;
    private readonly Task<string> _error;
    private int _resumeAttempted;
    private int _disposed;

    internal SuspendedNestedProcess(
        SafeProcessHandle process,
        SafeNestedThreadHandle primaryThread,
        int processId,
        SafeFileHandle commandHandle,
        SafeFileHandle eventHandle,
        SafeFileHandle errorHandle)
    {
        _process = process;
        _primaryThread = primaryThread;
        ProcessId = processId;
        CommandWriter = new StreamWriter(
            new FileStream(commandHandle, FileAccess.Write, 1, isAsync: false),
            Encoding.ASCII,
            bufferSize: 128,
            leaveOpen: false)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        EventReader = new StreamReader(
            new FileStream(eventHandle, FileAccess.Read, 1, isAsync: false),
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 128,
            leaveOpen: false);
        _errorReader = new StreamReader(
            new FileStream(errorHandle, FileAccess.Read, 1, isAsync: false),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 256,
            leaveOpen: false);
        _error = _errorReader.ReadToEndAsync();
    }

    internal int ProcessId { get; }
    internal SafeProcessHandle ProcessHandle => _process;
    internal StreamWriter CommandWriter { get; }
    internal StreamReader EventReader { get; }
    internal string CompletedError => _error.IsCompletedSuccessfully ? _error.Result : string.Empty;

    internal void ResumeOnce()
    {
        if (Interlocked.Exchange(ref _resumeAttempted, 1) != 0)
            throw new InvalidOperationException("A nested-Job process may be resumed only once.");
        var primaryThread = Interlocked.Exchange(ref _primaryThread, null) ?? throw new InvalidOperationException(
            "The nested-Job primary thread is unavailable.");
        using (primaryThread)
            WindowsNestedJobNative.ResumeExactlyOnce(primaryThread);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _primaryThread?.Dispose();
        _primaryThread = null;
        CommandWriter.Dispose();
        EventReader.Dispose();
        _errorReader.Dispose();
        _process.Dispose();
    }
}

internal static class WindowsNestedJobNative
{
    internal const uint KillOnJobClose = 0x00002000;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorInvalidHandle = 6;
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint HandleFlagInherit = 0x00000001;
    private const int StandardInputHandle = -10;
    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const nuint ProcThreadAttributeJobList = 0x0002000D;

    internal static SafeNestedJobHandle CreateKillOnCloseJob()
    {
        var job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "Could not create the nested-Job fixture Job Object.");
        }

        try
        {
            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = KillOnJobClose,
                },
            };
            if (!NativeMethods.SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformationClass,
                    ref information,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw Win32("Could not configure the nested-Job fixture Job Object.");
            }
            if (QueryJobLimitFlags(job) != KillOnJobClose)
                throw new InvalidOperationException("The nested-Job fixture Job limits changed on query.");
            if (!IsHandleNonInheritable(job))
                throw new InvalidOperationException("A nested-Job fixture Job handle was inheritable.");
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    internal static uint QueryJobLimitFlags(SafeNestedJobHandle job)
    {
        if (NativeMethods.QueryInformationJobObject(
                job,
                JobObjectExtendedLimitInformationClass,
                out var information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>(),
                out _))
        {
            return information.BasicLimitInformation.LimitFlags;
        }
        throw Win32("Could not query the nested-Job fixture Job Object.");
    }

    internal static bool IsHandleNonInheritable(SafeNestedJobHandle job)
    {
        if (!NativeMethods.GetHandleInformation(job, out var flags))
            throw Win32("Could not query nested-Job fixture handle flags.");
        return (flags & HandleFlagInherit) == 0;
    }

    internal static string HandleValue(SafeHandle handle) =>
        unchecked((ulong)handle.DangerousGetHandle().ToInt64())
            .ToString(CultureInfo.InvariantCulture);

    internal static void RequireJobHandleAbsent(string encodedHandle)
    {
        if (!ulong.TryParse(
                encodedHandle,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value) ||
            value is 0 or ulong.MaxValue)
        {
            throw new InvalidDataException("A nested-Job handle probe value was invalid.");
        }

        var candidate = new IntPtr(unchecked((long)value));
        var unexpectedlyPresent = NativeMethods.QueryInformationJobObjectByValue(
            candidate,
            JobObjectExtendedLimitInformationClass,
            out _,
            (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>(),
            out _);
        var error = Marshal.GetLastWin32Error();
        if (unexpectedlyPresent)
            throw new InvalidOperationException("A child inherited a nested-Job handle.");
        if (error != ErrorInvalidHandle)
        {
            throw new Win32Exception(
                error,
                "A nested-Job handle absence probe did not fail with ERROR_INVALID_HANDLE.");
        }
    }

    internal static void ClearInheritedControlHandleFlags()
    {
        foreach (var standardHandle in new[]
                 {
                     StandardInputHandle,
                     StandardOutputHandle,
                     StandardErrorHandle,
                 })
        {
            var handle = NativeMethods.GetStdHandle(standardHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                throw new InvalidOperationException("A nested-Job fixture standard handle was unavailable.");
            if (!NativeMethods.SetHandleInformationByValue(handle, HandleFlagInherit, flags: 0))
                throw Win32("Could not clear nested-Job fixture control-handle inheritance.");
        }
    }

    internal static SuspendedNestedProcess LaunchSuspended(
        NestedFixtureCommand command,
        SafeNestedJobHandle job)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(job);
        using var standardInput = NestedPipePair.Create(childReads: true);
        using var standardOutput = NestedPipePair.Create(childReads: false);
        using var standardError = NestedPipePair.Create(childReads: false);
        var childHandles = new[]
        {
            standardInput.ChildHandle,
            standardOutput.ChildHandle,
            standardError.ChildHandle,
        };

        using var lease = new NestedSafeHandleLease(
            [job, .. childHandles]);
        using var attributes = new NestedProcessAttributeList(job, childHandles);
        var startupInfo = new StartupInfoEx
        {
            StartupInfo = new StartupInfo
            {
                Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                Flags = StartfUseStdHandles,
                StandardInput = standardInput.ChildHandle.DangerousGetHandle(),
                StandardOutput = standardOutput.ChildHandle.DangerousGetHandle(),
                StandardError = standardError.ChildHandle.DangerousGetHandle(),
            },
            AttributeList = attributes.Pointer,
        };
        var commandLine = new StringBuilder(command.BuildCommandLine());
        ProcessInformation processInformation = default;
        try
        {
            if (!NativeMethods.CreateProcessW(
                    command.ExecutablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateNoWindow | CreateSuspended,
                    IntPtr.Zero,
                    command.WorkingDirectory,
                    ref startupInfo,
                    out processInformation))
            {
                throw Win32("Could not create a gated nested-Job fixture process.");
            }

            var process = new SafeProcessHandle(processInformation.Process, ownsHandle: true);
            processInformation.Process = IntPtr.Zero;
            var thread = new SafeNestedThreadHandle(processInformation.Thread, ownsHandle: true);
            processInformation.Thread = IntPtr.Zero;
            try
            {
                var result = new SuspendedNestedProcess(
                    process,
                    thread,
                    checked((int)processInformation.ProcessId),
                    standardInput.TakeParentHandle(),
                    standardOutput.TakeParentHandle(),
                    standardError.TakeParentHandle());
                process = null!;
                thread = null!;
                return result;
            }
            finally
            {
                process?.Dispose();
                thread?.Dispose();
            }
        }
        finally
        {
            CloseRawHandle(processInformation.Process);
            CloseRawHandle(processInformation.Thread);
        }
    }

    internal static bool IsProcessInJob(SafeProcessHandle process, SafeNestedJobHandle job)
    {
        if (NativeMethods.IsProcessInJob(process, job, out var result)) return result;
        throw Win32("Could not query exact nested-Job process membership.");
    }

    internal static void ResumeExactlyOnce(SafeNestedThreadHandle thread)
    {
        var priorSuspendCount = NativeMethods.ResumeThread(thread);
        if (priorSuspendCount == uint.MaxValue)
            throw Win32("Could not release a gated nested-Job fixture process.");
        if (priorSuspendCount != 1)
            throw new InvalidOperationException("A nested-Job process had an unexpected suspend count.");
    }

    private static Win32Exception Win32(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    private static void CloseRawHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            _ = WindowsNestedJobCloseHandle.Close(handle);
    }

    private sealed class NestedPipePair : IDisposable
    {
        private SafeFileHandle? _parent;
        private SafeFileHandle? _child;

        private NestedPipePair(SafeFileHandle parent, SafeFileHandle child)
        {
            _parent = parent;
            _child = child;
        }

        internal SafeFileHandle ChildHandle => _child ?? throw new ObjectDisposedException(
            nameof(NestedPipePair));

        internal static NestedPipePair Create(bool childReads)
        {
            var security = SecurityAttributes.Inheritable();
            if (!NativeMethods.CreatePipe(out var readValue, out var writeValue, ref security, size: 0))
                throw Win32("Could not create a nested-Job fixture pipe.");

            SafeFileHandle? read = new(readValue, ownsHandle: true);
            SafeFileHandle? write = new(writeValue, ownsHandle: true);
            try
            {
                var parent = childReads ? write : read;
                var child = childReads ? read : write;
                if (!NativeMethods.SetHandleInformation(parent, HandleFlagInherit, flags: 0))
                    throw Win32("Could not make a nested-Job parent pipe noninheritable.");
                var result = new NestedPipePair(parent, child);
                read = null;
                write = null;
                return result;
            }
            finally
            {
                read?.Dispose();
                write?.Dispose();
            }
        }

        internal SafeFileHandle TakeParentHandle()
        {
            var result = _parent ?? throw new InvalidOperationException(
                "The nested-Job parent pipe was already transferred.");
            _parent = null;
            return result;
        }

        public void Dispose()
        {
            _child?.Dispose();
            _parent?.Dispose();
            _child = null;
            _parent = null;
        }
    }

    private sealed class NestedSafeHandleLease : IDisposable
    {
        private readonly SafeHandle[] _handles;
        private int _added;

        internal NestedSafeHandleLease(SafeHandle[] handles)
        {
            _handles = handles;
            try
            {
                foreach (var handle in handles)
                {
                    var succeeded = false;
                    handle.DangerousAddRef(ref succeeded);
                    if (!succeeded) throw new ObjectDisposedException(handle.GetType().Name);
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

    private sealed class NestedProcessAttributeList : IDisposable
    {
        private IntPtr _attributes;
        private IntPtr _jobValues;
        private IntPtr _handleValues;
        private bool _initialized;

        internal NestedProcessAttributeList(
            SafeNestedJobHandle job,
            IReadOnlyList<SafeFileHandle> childHandles)
        {
            if (childHandles.Count != 3)
                throw new ArgumentException("Exactly three stdio handles are required.", nameof(childHandles));
            try
            {
                nuint size = 0;
                var sized = NativeMethods.InitializeProcThreadAttributeList(
                    IntPtr.Zero,
                    attributeCount: 2,
                    flags: 0,
                    ref size);
                var sizeError = Marshal.GetLastWin32Error();
                if (sized || size == 0 || sizeError != ErrorInsufficientBuffer)
                    throw new Win32Exception(sizeError, "Could not size nested-Job process attributes.");

                _attributes = Marshal.AllocHGlobal(checked((nint)size));
                if (!NativeMethods.InitializeProcThreadAttributeList(
                        _attributes,
                        attributeCount: 2,
                        flags: 0,
                        ref size))
                {
                    throw Win32("Could not initialize nested-Job process attributes.");
                }
                _initialized = true;

                _jobValues = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(_jobValues, job.DangerousGetHandle());
                if (!NativeMethods.UpdateProcThreadAttribute(
                        _attributes,
                        flags: 0,
                        ProcThreadAttributeJobList,
                        _jobValues,
                        (nuint)IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw Win32("Could not set nested-Job creation-time membership.");
                }

                _handleValues = Marshal.AllocHGlobal(checked(IntPtr.Size * childHandles.Count));
                for (var index = 0; index < childHandles.Count; index++)
                {
                    Marshal.WriteIntPtr(
                        _handleValues,
                        checked(index * IntPtr.Size),
                        childHandles[index].DangerousGetHandle());
                }
                if (!NativeMethods.UpdateProcThreadAttribute(
                        _attributes,
                        flags: 0,
                        ProcThreadAttributeHandleList,
                        _handleValues,
                        checked((nuint)(IntPtr.Size * childHandles.Count)),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw Win32("Could not set the nested-Job explicit handle list.");
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
        internal static extern SafeNestedJobHandle CreateJobObjectW(
            IntPtr jobAttributes,
            string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeNestedJobHandle job,
            uint informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(
            SafeNestedJobHandle job,
            uint informationClass,
            out JobObjectExtendedLimitInformation information,
            uint informationLength,
            out uint returnLength);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "QueryInformationJobObject",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObjectByValue(
            IntPtr job,
            uint informationClass,
            out JobObjectExtendedLimitInformation information,
            uint informationLength,
            out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetHandleInformation(
            SafeNestedJobHandle handle,
            out uint flags);

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

        [DllImport(
            "kernel32.dll",
            EntryPoint = "SetHandleInformation",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformationByValue(
            IntPtr handle,
            uint mask,
            uint flags);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetStdHandle(int standardHandle);

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
            SafeNestedJobHandle job,
            [MarshalAs(UnmanagedType.Bool)] out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(SafeNestedThreadHandle thread);

    }
}

internal sealed class SafeNestedJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeNestedJobHandle() : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => WindowsNestedJobCloseHandle.Close(handle);
}

internal sealed class SafeNestedThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeNestedThreadHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => WindowsNestedJobCloseHandle.Close(handle);
}

internal static class WindowsNestedJobCloseHandle
{
    internal static bool Close(IntPtr handle) => CloseHandle(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
