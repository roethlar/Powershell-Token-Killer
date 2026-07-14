using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WindowsProcessTreeSupervisorTests
{
    [Fact]
    public void Runnable_launch_uses_the_frozen_sequence_and_transfers_sole_ownership()
    {
        var native = new RecordingNative();
        var supervisor = Supervisor(native);

        var worker = supervisor.Launch(Command());

        Assert.Equal(
            [
                "create_job",
                "set_job",
                "query_job",
                "create_pipes",
                "create_process:Runnable",
                "close_child_ends",
                "verify_membership",
            ],
            native.Calls);
        Assert.Equal(0x00002000u, WindowsProcessTreeSupervisor.KillOnJobClose);
        Assert.Equal(WindowsProcessTreeSupervisor.KillOnJobClose, native.ConfiguredFlags);
        Assert.Equal(1, native.CreateProcessCalls);
        Assert.Equal(4242, worker.ProcessId);
        Assert.Same(native.Pipes.RequestWriter, worker.RequestWriter);
        Assert.Same(native.Pipes.EventReader, worker.EventReader);
        Assert.Same(native.Pipes.StandardOutputReader, worker.StandardOutputReader);
        Assert.Same(native.Pipes.StandardErrorReader, worker.StandardErrorReader);
        Assert.Same(native.Process.WaitTask, worker.WaitForExitAsync());

        worker.Dispose();
        Assert.Equal(
            ["dispose_job", "dispose_process", "dispose_pipes"],
            native.Calls.Where(call => call.StartsWith("dispose_", StringComparison.Ordinal)));

        var callsAfterFirstDispose = native.Calls.ToArray();
        worker.Dispose();
        Assert.Equal(callsAfterFirstDispose, native.Calls);
        Assert.Throws<ObjectDisposedException>(() => _ = worker.ProcessId);
    }

    [Fact]
    public void Proof_launch_returns_verified_but_suspended_until_one_shot_resume()
    {
        var native = new RecordingNative();
        var worker = Supervisor(native).Launch(
            Command(),
            WindowsProcessCreationMode.SuspendedForContainmentProof);

        Assert.Equal("verify_membership", native.Calls[^1]);
        Assert.DoesNotContain("resume", native.Calls);
        Assert.DoesNotContain("dispose_job", native.Calls);

        worker.ResumeForContainmentProof();

        Assert.Equal("resume", native.Calls[^1]);
        Assert.Throws<InvalidOperationException>(worker.ResumeForContainmentProof);
        Assert.Equal(1, native.Calls.Count(call => call == "resume"));
        Assert.DoesNotContain("dispose_job", native.Calls);
        worker.Dispose();
    }

    [Fact]
    public void Runnable_worker_cannot_use_the_proof_only_resume_surface()
    {
        var native = new RecordingNative();
        using var worker = Supervisor(native).Launch(Command());

        Assert.Throws<InvalidOperationException>(worker.ResumeForContainmentProof);

        Assert.DoesNotContain("resume", native.Calls);
        Assert.Equal(1, native.CreateProcessCalls);
    }

    [Fact]
    public void Proof_resume_failure_is_mapped_once_and_contains_before_other_cleanup()
    {
        var native = new RecordingNative { ThrowAt = "resume" };
        var worker = Supervisor(native).Launch(
            Command(),
            WindowsProcessCreationMode.SuspendedForContainmentProof);

        var failure = Assert.Throws<WorkerLaunchException>(
            worker.ResumeForContainmentProof);

        Assert.Equal("containment_resume_failed", failure.DetailCode);
        Assert.Equal(WorkerLaunchStage.ResumePrimaryThread, failure.Stage);
        Assert.IsType<IOException>(failure.InnerException);
        Assert.Equal(1, native.Calls.Count(call => call == "resume"));
        Assert.Equal(
            ["dispose_job", "dispose_process", "dispose_pipes"],
            native.Calls.Where(call => call.StartsWith("dispose_", StringComparison.Ordinal)));
        Assert.Throws<ObjectDisposedException>(worker.ResumeForContainmentProof);
    }

    [Fact]
    public void Unsupported_platform_and_precancellation_refuse_before_native_effects()
    {
        var unsupportedNative = new RecordingNative();
        var unsupported = new WindowsProcessTreeSupervisor(
            unsupportedNative,
            isWindows: () => false);

        Assert.Throws<PlatformNotSupportedException>(() => unsupported.Launch(Command()));
        Assert.Empty(unsupportedNative.Calls);

        var cancelledNative = new RecordingNative();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Supervisor(cancelledNative).Launch(Command(), cancellation.Token));
        Assert.Empty(cancelledNative.Calls);
    }

    [Fact]
    public void Cancellation_during_setup_rolls_back_before_pipe_or_process_creation()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new RecordingNative
        {
            OnCall = call =>
            {
                if (call == "query_job") cancellation.Cancel();
            },
        };

        Assert.Throws<OperationCanceledException>(() =>
            Supervisor(native).Launch(Command(), cancellation.Token));

        Assert.Equal(
            ["create_job", "set_job", "query_job", "dispose_job"],
            native.Calls);
        Assert.Equal(0, native.CreateProcessCalls);
    }

    [Fact]
    public void Cancellation_during_atomic_creation_verifies_then_kills_the_job_before_returning()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new RecordingNative
        {
            OnCall = call =>
            {
                if (call == "verify_membership") cancellation.Cancel();
            },
        };

        Assert.Throws<OperationCanceledException>(() =>
            Supervisor(native).Launch(Command(), cancellation.Token));

        Assert.Equal(1, native.CreateProcessCalls);
        Assert.Equal(
            ["dispose_job", "dispose_process", "dispose_pipes"],
            native.Calls.Where(call => call.StartsWith("dispose_", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(StageFailures))]
    public void Every_launch_stage_failure_is_stably_mapped_and_disposes_owned_handles(
        string failingCall,
        string detailCode,
        string expectedStage,
        string[] expectedDisposals)
    {
        var native = new RecordingNative { ThrowAt = failingCall };

        var failure = Assert.Throws<WorkerLaunchException>(() =>
            Supervisor(native).Launch(Command()));

        Assert.Equal(detailCode, failure.DetailCode);
        Assert.Equal(expectedStage, failure.Stage.ToString());
        Assert.Null(failure.NativeErrorCode);
        Assert.IsType<IOException>(failure.InnerException);
        Assert.Equal(
            expectedDisposals,
            native.Calls.Where(call => call.StartsWith("dispose_", StringComparison.Ordinal)));
        Assert.True(native.CreateProcessCalls <= 1);
    }

    public static TheoryData<string, string, string, string[]> StageFailures =>
        new()
        {
            {
                "create_job",
                "containment_setup_failed",
                nameof(WorkerLaunchStage.CreateJob),
                []
            },
            {
                "set_job",
                "containment_setup_failed",
                nameof(WorkerLaunchStage.ConfigureJob),
                ["dispose_job"]
            },
            {
                "query_job",
                "containment_setup_failed",
                nameof(WorkerLaunchStage.QueryJob),
                ["dispose_job"]
            },
            {
                "create_pipes",
                "containment_setup_failed",
                nameof(WorkerLaunchStage.CreatePipe),
                ["dispose_job"]
            },
            {
                "create_process:Runnable",
                "worker_create_failed",
                nameof(WorkerLaunchStage.CreateProcess),
                ["dispose_job", "dispose_pipes"]
            },
            {
                "close_child_ends",
                "containment_setup_failed",
                nameof(WorkerLaunchStage.CloseChildHandles),
                ["dispose_job", "dispose_process", "dispose_pipes"]
            },
            {
                "verify_membership",
                "containment_verification_failed",
                nameof(WorkerLaunchStage.VerifyContainment),
                ["dispose_job", "dispose_process", "dispose_pipes"]
            },
        };

    [Fact]
    public void Job_limit_query_requires_exact_KILL_ON_CLOSE_equality()
    {
        var native = new RecordingNative
        {
            QueriedFlags = WindowsProcessTreeSupervisor.KillOnJobClose | 0x00000001,
        };

        var failure = Assert.Throws<WorkerLaunchException>(() =>
            Supervisor(native).Launch(Command()));

        Assert.Equal("containment_setup_failed", failure.DetailCode);
        Assert.Equal(WorkerLaunchStage.QueryJob, failure.Stage);
        Assert.Equal(
            ["create_job", "set_job", "query_job", "dispose_job"],
            native.Calls);
    }

    [Fact]
    public void Pipe_set_requires_exact_five_child_handles_before_creation()
    {
        var native = new RecordingNative(childHandleCount: 4);

        var failure = Assert.Throws<WorkerLaunchException>(() =>
            Supervisor(native).Launch(Command()));

        Assert.Equal(WorkerLaunchStage.CreatePipe, failure.Stage);
        Assert.Equal(0, native.CreateProcessCalls);
        Assert.Equal(
            ["dispose_job", "dispose_pipes"],
            native.Calls.Where(call => call.StartsWith("dispose_", StringComparison.Ordinal)));
    }

    [Fact]
    public void Failed_exact_membership_check_does_not_retry_and_kills_job_first()
    {
        var native = new RecordingNative { IsMember = false };

        var failure = Assert.Throws<WorkerLaunchException>(() =>
            Supervisor(native).Launch(Command()));

        Assert.Equal("containment_verification_failed", failure.DetailCode);
        Assert.Equal(WorkerLaunchStage.VerifyContainment, failure.Stage);
        Assert.Equal(1, native.CreateProcessCalls);
        Assert.Equal(1, native.Calls.Count(call => call == "close_child_ends"));
        Assert.Equal(
            ["dispose_job", "dispose_process", "dispose_pipes"],
            native.Calls.Where(call => call.StartsWith("dispose_", StringComparison.Ordinal)));
    }

    [Fact]
    public void Owner_attempts_process_and_pipe_cleanup_when_job_close_throws()
    {
        var native = new RecordingNative();
        var worker = Supervisor(native).Launch(Command());
        native.Job.DisposeFailure = new IOException("job close failed");

        var failure = Assert.Throws<IOException>(worker.Dispose);

        Assert.Same(native.Job.DisposeFailure, failure);
        Assert.Equal(
            ["dispose_job", "dispose_process", "dispose_pipes"],
            native.Calls.Where(call => call.StartsWith("dispose_", StringComparison.Ordinal)));
    }

    [Fact]
    public void Native_production_has_one_atomic_create_and_no_fallback_or_sweep_escape_hatch()
    {
        var methods = typeof(IWindowsWorkerNative).GetMethods();

        Assert.Single(methods, method => method.Name == nameof(IWindowsWorkerNative.CreateProcessInJob));

        var pInvokes = typeof(WindowsWorkerNative)
            .GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
            .Select(method => (Method: method, Import: method.GetCustomAttribute<DllImportAttribute>()))
            .Where(candidate => candidate.Import is not null)
            .ToArray();
        Assert.Equal(
            [
                "CloseHandle",
                "CreateFileW",
                "CreateJobObjectW",
                "CreatePipe",
                "CreateProcessW",
                "DeleteProcThreadAttributeList",
                "DuplicateHandle",
                "GetCurrentProcess",
                "InitializeProcThreadAttributeList",
                "IsProcessInJob",
                "QueryInformationJobObject",
                "ResumeThread",
                "SetHandleInformation",
                "SetInformationJobObject",
                "UpdateProcThreadAttribute",
            ],
            pInvokes
                .Select(candidate => candidate.Import!.EntryPoint ?? candidate.Method.Name)
                .Order(StringComparer.Ordinal));
        var createProcessPInvoke = pInvokes.Single(candidate => string.Equals(
            candidate.Import!.EntryPoint ?? candidate.Method.Name,
            "CreateProcessW",
            StringComparison.Ordinal)).Method;
        var createProcessInJob = typeof(WindowsWorkerNative).GetMethod(
            nameof(IWindowsWorkerNative.CreateProcessInJob),
            BindingFlags.Instance | BindingFlags.Public) ??
            throw new InvalidOperationException("The native create method is unavailable.");
        Assert.Equal(1, CountDirectCalls(createProcessInJob, createProcessPInvoke));

        var duplicateHandlePInvoke = pInvokes.Single(candidate => string.Equals(
            candidate.Import!.EntryPoint ?? candidate.Method.Name,
            "DuplicateHandle",
            StringComparison.Ordinal)).Method;
        var getCurrentProcessPInvoke = pInvokes.Single(candidate => string.Equals(
            candidate.Import!.EntryPoint ?? candidate.Method.Name,
            "GetCurrentProcess",
            StringComparison.Ordinal)).Method;
        var ownedWaitHandle = typeof(WindowsWorkerNative).GetNestedType(
            "OwnedProcessWaitHandle",
            BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The owning process wait handle is unavailable.");
        var ownedWaitConstructor = Assert.Single(ownedWaitHandle.GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Equal(1, CountDirectCalls(ownedWaitConstructor, duplicateHandlePInvoke));
        Assert.Equal(1, CountDirectCalls(ownedWaitConstructor, getCurrentProcessPInvoke));

        var source = File.ReadAllText(NativeSourcePath());
        var executableSource = RemoveComments(source);
        Assert.DoesNotContain("LibraryImport", executableSource, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"ProcThreadAttributeHandleList\s*=\s*0x00020002\s*;",
                RegexOptions.CultureInvariant),
            executableSource);
        Assert.Matches(
            new Regex(
                @"ProcThreadAttributeJobList\s*=\s*0x0002000D\s*;",
                RegexOptions.CultureInvariant),
            executableSource);
        Assert.Matches(
            new Regex(
                @"DuplicateSameAccess\s*=\s*0x00000002\s*;",
                RegexOptions.CultureInvariant),
            executableSource);
        Assert.Equal(
            2,
            Regex.Matches(
                executableSource,
                @"NativeMethods\s*\.\s*UpdateProcThreadAttribute\s*\(",
                RegexOptions.CultureInvariant).Count);
        Assert.Equal(2, Regex.Matches(executableSource, @"attributeCount\s*:\s*2").Count);
        Assert.Contains("if (childHandles.Count != 5)", executableSource, StringComparison.Ordinal);
        Assert.Contains("inheritHandles: true", executableSource, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"SetHandleInformation\s*\(\s*supervisor\s*,\s*HandleFlagInherit\s*,\s*flags\s*:\s*0\s*\)",
                RegexOptions.CultureInvariant),
            executableSource);
        Assert.Matches(
            new Regex(
                @"CreateJobObjectW\s*\(\s*IntPtr\.Zero\s*,\s*null\s*\)",
                RegexOptions.CultureInvariant),
            executableSource);
        Assert.Single(
            Regex.Matches(
                executableSource,
                @"NativeMethods\s*\.\s*CreateProcessW\s*\(",
                RegexOptions.CultureInvariant));
        Assert.DoesNotMatch(
            new Regex(
                @"Process\s*\.\s*(?:Start|GetProcess)|\.\s*Kill\s*\(|AssignProcessToJobObject|" +
                    @"TerminateJobObject|TerminateProcess|taskkill|Stop-Process|" +
                    @"ProcessStartInfo|new\s+(?:System\.Diagnostics\.)?Process\b",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
            executableSource);

        var createStart = source.IndexOf(
            "public IWindowsProcessHandle CreateProcessInJob(",
            StringComparison.Ordinal);
        var createEnd = source.IndexOf(
            "public bool IsProcessInJob(",
            createStart,
            StringComparison.Ordinal);
        Assert.True(createStart >= 0 && createEnd > createStart);
        var executableCreateBody = RemoveComments(source[createStart..createEnd]);
        Assert.Single(
            Regex.Matches(
                executableCreateBody,
                @"NativeMethods\s*\.\s*CreateProcessW\s*\(",
                RegexOptions.CultureInvariant));
        Assert.Single(
            Regex.Matches(
                executableCreateBody,
                @"if\s*\(\s*!\s*NativeMethods\s*\.\s*CreateProcessW\s*\(",
                RegexOptions.CultureInvariant));
        Assert.Single(
            Regex.Matches(
                executableCreateBody,
                @"\bCreateProcessInJob\s*\(",
                RegexOptions.CultureInvariant));
        Assert.DoesNotMatch(
            new Regex(@"\b(?:for|foreach|while|do|goto)\b", RegexOptions.CultureInvariant),
            executableCreateBody);

        var waitStart = source.IndexOf(
            "private sealed class OwnedProcessWaitHandle",
            StringComparison.Ordinal);
        var waitEnd = source.IndexOf(
            "private sealed class NativeWorkerPipeSet",
            waitStart,
            StringComparison.Ordinal);
        Assert.True(waitStart >= 0 && waitEnd > waitStart);
        var executableWaitBody = RemoveComments(source[waitStart..waitEnd]);
        Assert.Contains("desiredAccess: 0", executableWaitBody, StringComparison.Ordinal);
        Assert.Contains("inheritHandle: false", executableWaitBody, StringComparison.Ordinal);
        Assert.Contains("options: DuplicateSameAccess", executableWaitBody, StringComparison.Ordinal);
        Assert.Contains("SafeWaitHandle = duplicate", executableWaitBody, StringComparison.Ordinal);
        Assert.DoesNotContain("BorrowedProcessWaitHandle", executableWaitBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DangerousAddRef", executableWaitBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DangerousRelease", executableWaitBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DangerousGetHandle", executableWaitBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ownsHandle: false", executableWaitBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Launch_command_is_an_immutable_absolute_command_with_an_exact_environment()
    {
        var arguments = new List<string> { "--worker" };
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PTK_TEST_VALUE"] = "original",
        };
        var command = new WorkerLaunchCommand(
            AbsolutePath("ptk-worker-test"),
            arguments,
            AbsolutePath("work"),
            environment);

        arguments[0] = "changed";
        arguments.Add("extra");
        environment["PTK_TEST_VALUE"] = "changed";
        environment["PATH"] = "ambient";

        Assert.Equal(["--worker"], command.Arguments);
        Assert.Equal("original", command.Environment["PTK_TEST_VALUE"]);
        Assert.Single(command.Environment);
        Assert.False(command.Environment.ContainsKey("PATH"));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)command.Arguments).Add("mutation"));
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)command.Environment).Add("MUTATION", "1"));
    }

    [Theory]
    [InlineData(WorkerBootstrapEnvironment.RequestHandle)]
    [InlineData(WorkerBootstrapEnvironment.EventHandle)]
    [InlineData("ptk_worker_request_handle")]
    public void Launch_command_rejects_reserved_handle_variables_case_insensitively(
        string variable)
    {
        Assert.Throws<ArgumentException>(() => new WorkerLaunchCommand(
            AbsolutePath("ptk-worker-test"),
            [],
            AbsolutePath("work"),
            new Dictionary<string, string> { [variable] = "123" }));
    }

    [Fact]
    public void Launch_command_rejects_relative_duplicate_null_and_invalid_values()
    {
        Assert.Throws<ArgumentException>(() => new WorkerLaunchCommand(
            "relative-worker",
            [],
            AbsolutePath("work"),
            []));
        Assert.Throws<ArgumentException>(() => new WorkerLaunchCommand(
            AbsolutePath("ptk-worker-test"),
            [],
            "relative-work",
            []));
        Assert.Throws<ArgumentException>(() => new WorkerLaunchCommand(
            AbsolutePath("ptk-worker-test"),
            ["valid", null!],
            AbsolutePath("work"),
            []));
        Assert.Throws<ArgumentException>(() => new WorkerLaunchCommand(
            AbsolutePath("ptk-worker-test"),
            ["invalid\0argument"],
            AbsolutePath("work"),
            []));
        Assert.Throws<ArgumentException>(() => new WorkerLaunchCommand(
            AbsolutePath("ptk-worker-test"),
            [],
            AbsolutePath("work"),
            [
                new KeyValuePair<string, string>("DUPLICATE", "first"),
                new KeyValuePair<string, string>("duplicate", "second"),
            ]));
        Assert.Throws<ArgumentException>(() => new WorkerLaunchCommand(
            AbsolutePath("ptk-worker-test"),
            [],
            AbsolutePath("work"),
            new Dictionary<string, string> { ["INVALID=NAME"] = "value" }));
    }

    [Fact]
    public void Native_command_line_quotes_empty_space_quote_and_trailing_backslash_arguments()
    {
        var executable = AbsolutePath("ptk worker test");
        var command = new WorkerLaunchCommand(
            executable,
            ["plain", "", "space value", "argument λ with \"quote\" and trailing\\"],
            AbsolutePath("work"),
            []);

        Assert.Equal(
            $"\"{executable}\" plain \"\" \"space value\" " +
                "\"argument λ with \\\"quote\\\" and trailing\\\\\"",
            WindowsWorkerNative.BuildCommandLine(command));
    }

    [Fact]
    public void Native_environment_block_is_case_insensitively_sorted_and_exactly_double_null_terminated()
    {
        var block = WindowsWorkerNative.BuildUnicodeEnvironmentBlockText(
        [
            new("z_LAST", "z"),
            new("a_FIRST", "λ"),
            new("B_MIDDLE", "b"),
        ]);

        Assert.Equal("a_FIRST=λ\0B_MIDDLE=b\0z_LAST=z\0\0", block);
        Assert.Equal("\0\0", WindowsWorkerNative.BuildUnicodeEnvironmentBlockText([]));
    }

    [Fact]
    public void Native_creation_flags_are_exact_and_suspension_is_proof_only()
    {
        const uint extendedStartupInfoPresent = 0x00080000;
        const uint createUnicodeEnvironment = 0x00000400;
        const uint createNoWindow = 0x08000000;
        const uint createSuspended = 0x00000004;
        var runnable = extendedStartupInfoPresent | createUnicodeEnvironment | createNoWindow;

        Assert.Equal(runnable, WindowsWorkerNative.BuildCreationFlags(WindowsProcessCreationMode.Runnable));
        Assert.Equal(
            runnable | createSuspended,
            WindowsWorkerNative.BuildCreationFlags(
                WindowsProcessCreationMode.SuspendedForContainmentProof));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowsWorkerNative.BuildCreationFlags((WindowsProcessCreationMode)int.MaxValue));
    }

    private static WindowsProcessTreeSupervisor Supervisor(RecordingNative native) =>
        new(native, isWindows: () => true);

    private static WorkerLaunchCommand Command() =>
        new(
            AbsolutePath("ptk-worker-test"),
            ["--worker"],
            AbsolutePath("work"),
            new Dictionary<string, string> { ["PTK_TEST"] = "1" });

    private static string AbsolutePath(string leaf) =>
        Path.Combine(Path.GetFullPath(Path.GetTempPath()), leaf);

    private static string NativeSourcePath([CallerFilePath] string testSourcePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testSourcePath) ?? throw new InvalidOperationException(
                "The test source directory is unavailable."),
            "..",
            "PtkMcpServer",
            "Worker",
            "WindowsWorkerNative.cs"));

    private static string RemoveComments(string source) =>
        Regex.Replace(
            source,
            @"//[^\r\n]*|/\*[\s\S]*?\*/",
            string.Empty,
            RegexOptions.CultureInvariant);

    private static int CountDirectCalls(MethodBase caller, MethodInfo callee)
    {
        var il = caller.GetMethodBody()?.GetILAsByteArray() ??
            throw new InvalidOperationException($"{caller.Name} has no managed method body.");
        var token = BitConverter.GetBytes(callee.MetadataToken);
        var count = 0;
        for (var index = 0; index <= il.Length - token.Length - 1; index++)
        {
            if (il[index] != 0x28) continue; // call <metadata-token>
            if (il.AsSpan(index + 1, token.Length).SequenceEqual(token)) count++;
        }
        return count;
    }

    private sealed class RecordingNative : IWindowsWorkerNative
    {
        internal RecordingNative(int childHandleCount = 5)
        {
            Job = new RecordingJob(Calls);
            Process = new RecordingProcess(Calls);
            Pipes = new RecordingPipeSet(Calls, () => ThrowAt, childHandleCount);
        }

        internal List<string> Calls { get; } = [];
        internal RecordingJob Job { get; }
        internal RecordingProcess Process { get; }
        internal RecordingPipeSet Pipes { get; }
        internal string? ThrowAt { get; init; }
        internal Action<string>? OnCall { get; init; }
        internal uint QueriedFlags { get; init; } =
            WindowsProcessTreeSupervisor.KillOnJobClose;
        internal uint? ConfiguredFlags { get; private set; }
        internal bool IsMember { get; init; } = true;
        internal int CreateProcessCalls { get; private set; }

        public IWindowsJobHandle CreateUnnamedJob()
        {
            Record("create_job");
            return Job;
        }

        public void SetJobLimitFlags(IWindowsJobHandle job, uint limitFlags)
        {
            Assert.Same(Job, job);
            ConfiguredFlags = limitFlags;
            Record("set_job");
        }

        public uint QueryJobLimitFlags(IWindowsJobHandle job)
        {
            Assert.Same(Job, job);
            Record("query_job");
            return QueriedFlags;
        }

        public IWindowsWorkerPipeSet CreateWorkerPipeSet()
        {
            Record("create_pipes");
            return Pipes;
        }

        public IWindowsProcessHandle CreateProcessInJob(
            WorkerLaunchCommand command,
            IWindowsJobHandle job,
            IWindowsWorkerPipeSet pipes,
            WindowsProcessCreationMode mode)
        {
            Assert.NotNull(command);
            Assert.Same(Job, job);
            Assert.Same(Pipes, pipes);
            CreateProcessCalls++;
            Record($"create_process:{mode}");
            return Process;
        }

        public bool IsProcessInJob(IWindowsProcessHandle process, IWindowsJobHandle job)
        {
            Assert.Same(Process, process);
            Assert.Same(Job, job);
            Record("verify_membership");
            return IsMember;
        }

        public void ResumePrimaryThreadForContainmentProof(IWindowsProcessHandle process)
        {
            Assert.Same(Process, process);
            Record("resume");
        }

        private void Record(string call)
        {
            Calls.Add(call);
            OnCall?.Invoke(call);
            if (ThrowAt == call)
                throw new IOException($"injected {call} failure");
        }
    }

    private sealed class RecordingJob(List<string> calls) : IWindowsJobHandle
    {
        internal Exception? DisposeFailure { get; set; }

        public void Dispose()
        {
            calls.Add("dispose_job");
            if (DisposeFailure is not null) throw DisposeFailure;
        }
    }

    private sealed class RecordingProcess(List<string> calls) : IWindowsProcessHandle
    {
        internal Task WaitTask { get; } = Task.CompletedTask;
        public int ProcessId => 4242;

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            WaitTask;

        public void Dispose() => calls.Add("dispose_process");
    }

    private sealed class RecordingPipeSet : IWindowsWorkerPipeSet
    {
        private readonly List<string> _calls;
        private readonly Func<string?> _throwAt;

        internal RecordingPipeSet(
            List<string> calls,
            Func<string?> throwAt,
            int childHandleCount)
        {
            _calls = calls;
            _throwAt = throwAt;
            ChildHandleCount = childHandleCount;
        }

        public int ChildHandleCount { get; }
        public Stream RequestWriter { get; } = new MemoryStream();
        public Stream EventReader { get; } = new MemoryStream();
        public Stream StandardOutputReader { get; } = new MemoryStream();
        public Stream StandardErrorReader { get; } = new MemoryStream();

        public void CloseChildEnds()
        {
            _calls.Add("close_child_ends");
            if (_throwAt() == "close_child_ends")
                throw new IOException("injected close_child_ends failure");
        }

        public void Dispose() => _calls.Add("dispose_pipes");
    }
}
