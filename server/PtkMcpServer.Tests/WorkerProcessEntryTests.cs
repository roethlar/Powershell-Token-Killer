using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using PtkMcpServer.GuardianHost;
using PtkMcpServer.Sessions;
using PtkMcpServer.Worker;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

[Collection(WindowsProcessCreationCollection.Name)]
public sealed class WorkerProcessEntryTests
{
    private static readonly Guid BootId =
        Guid.Parse("27e13a09-3106-4c60-936d-2f6e165f54ad");

    [Fact]
    public async Task Malformed_worker_arguments_remove_environment_and_start_nothing()
    {
        string[][] malformed =
        [
            ["--worker", "extra"],
            ["extra", "--worker"],
        ];

        foreach (var arguments in malformed)
        {
            var environment = EnvironmentWithHandles();
            var diagnostic = new MemoryStream();
            var bootstrapCalls = 0;
            var runtimeCalls = 0;

            var exitCode = await WorkerProcessEntry.RunAsync(
                arguments,
                environment,
                _ =>
                {
                    bootstrapCalls++;
                    throw new InvalidOperationException("bootstrap must not run");
                },
                (_, _) =>
                {
                    runtimeCalls++;
                    throw new InvalidOperationException("runtime must not run");
                },
                () => BootId,
                () => diagnostic);

            Assert.Equal(64, exitCode);
            Assert.Equal(0, bootstrapCalls);
            Assert.Equal(0, runtimeCalls);
            AssertCaptureAndRemoval(environment);
            Assert.Equal(
                "ptk_worker_exit kind=invocation_error detail=invalid_arguments\n",
                Encoding.ASCII.GetString(diagnostic.ToArray()));
        }
    }

    [Fact]
    public async Task Environment_capture_and_removal_precede_exact_invocation_validation()
    {
        var timeline = new List<string>();
        var environment = new RecordingEnvironment(
            new Dictionary<string, string?>
            {
                [WorkerBootstrapEnvironment.RequestHandle] = "101",
                [WorkerBootstrapEnvironment.EventHandle] = "202",
            },
            timeline);
        var arguments = new RecordingArguments(
            ["extra", "--worker"],
            timeline);

        var exitCode = await WorkerProcessEntry.RunAsync(
            arguments,
            environment,
            _ => throw new InvalidOperationException("bootstrap must not run"),
            (_, _) => throw new InvalidOperationException("runtime must not run"),
            () => BootId,
            () => new MemoryStream());

        Assert.Equal(64, exitCode);
        Assert.Equal(
            [
                "get:" + WorkerBootstrapEnvironment.RequestHandle,
                "get:" + WorkerBootstrapEnvironment.EventHandle,
                "remove:" + WorkerBootstrapEnvironment.RequestHandle,
                "remove:" + WorkerBootstrapEnvironment.EventHandle,
                "arguments:count",
            ],
            timeline);
    }

    [Fact]
    public async Task Environment_is_removed_before_platform_or_bootstrap_validation()
    {
        var environment = EnvironmentWithHandles();
        var diagnostic = new MemoryStream();
        var runtimeCalls = 0;

        var exitCode = await WorkerProcessEntry.RunAsync(
            ["--worker"],
            environment,
            values =>
            {
                AssertCaptureAndRemoval(environment);
                Assert.Equal("101", values.RequestHandle);
                Assert.Equal("202", values.EventHandle);
                throw new WorkerBootstrapException("platform_unsupported");
            },
            (_, _) =>
            {
                runtimeCalls++;
                throw new InvalidOperationException("runtime must not run");
            },
            () => BootId,
            () => diagnostic);

        Assert.Equal(80, exitCode);
        Assert.Equal(0, runtimeCalls);
        Assert.Equal(
            "ptk_worker_exit kind=bootstrap_failure detail=platform_unsupported\n",
            Encoding.ASCII.GetString(diagnostic.ToArray()));
    }

    [Fact]
    public async Task Precaptured_handoff_uses_supplied_values_without_environment_recapture()
    {
        var diagnostic = new MemoryStream();
        var supplied = new WorkerBootstrapValues("captured-request", "captured-event");
        var runtimeCalls = 0;

        var exitCode = await WorkerProcessEntry.RunCapturedAsync(
            supplied,
            values =>
            {
                Assert.Equal(supplied, values);
                throw new WorkerBootstrapException("handle_invalid");
            },
            (_, _) =>
            {
                runtimeCalls++;
                throw new InvalidOperationException("runtime must not run");
            },
            () => BootId,
            () => diagnostic);

        Assert.Equal(80, exitCode);
        Assert.Equal(0, runtimeCalls);
        Assert.Equal(
            "ptk_worker_exit kind=bootstrap_failure detail=handle_invalid\n",
            Encoding.ASCII.GetString(diagnostic.ToArray()));
    }

    [Fact]
    public async Task Canceled_precaptured_handoff_starts_no_platform_effect()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var openCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WorkerProcessEntry.RunCapturedAsync(
                new WorkerBootstrapValues("captured-request", "captured-event"),
                _ =>
                {
                    openCalls++;
                    throw new InvalidOperationException("bootstrap must not open");
                },
                (_, _) => throw new InvalidOperationException("runtime must not run"),
                () => BootId,
                () => new MemoryStream(),
                cancellation.Token));

        Assert.Equal(0, openCalls);
    }

    [Fact]
    public async Task Capture_failure_still_attempts_both_environment_removals()
    {
        var environment = EnvironmentWithHandles();
        environment.ThrowOnRequestGet = true;
        var diagnostic = new MemoryStream();
        var bootstrapCalls = 0;

        var exitCode = await WorkerProcessEntry.RunAsync(
            ["--worker"],
            environment,
            _ =>
            {
                bootstrapCalls++;
                throw new InvalidOperationException("bootstrap must not run");
            },
            (_, _) => throw new InvalidOperationException("runtime must not run"),
            () => BootId,
            () => diagnostic);

        Assert.Equal(80, exitCode);
        Assert.Equal(0, bootstrapCalls);
        AssertCaptureAndRemoval(environment);
        Assert.Equal(
            "ptk_worker_exit kind=bootstrap_failure detail=bootstrap_failure\n",
            Encoding.ASCII.GetString(diagnostic.ToArray()));
    }

    [Theory]
    [InlineData(true, "handle_invalid", "handle_invalid")]
    [InlineData(true, "unlisted secret detail", "bootstrap_failure")]
    [InlineData(false, "ignored", "bootstrap_failure")]
    public async Task Bootstrap_failures_exit_80_with_normalized_diagnostics(
        bool typedFailure,
        string suppliedDetail,
        string expectedDetail)
    {
        var environment = EnvironmentWithHandles();
        var diagnostic = new MemoryStream();
        var runtimeCalls = 0;

        var exitCode = await WorkerProcessEntry.RunAsync(
            ["--worker"],
            environment,
            _ => throw (typedFailure
                ? new WorkerBootstrapException(suppliedDetail)
                : new InvalidOperationException("sensitive bootstrap failure")),
            (_, _) =>
            {
                runtimeCalls++;
                throw new InvalidOperationException("runtime must not run");
            },
            () => BootId,
            () => diagnostic);

        Assert.Equal(80, exitCode);
        Assert.Equal(0, runtimeCalls);
        AssertCaptureAndRemoval(environment);
        Assert.Equal(
            $"ptk_worker_exit kind=bootstrap_failure detail={expectedDetail}\n",
            Encoding.ASCII.GetString(diagnostic.ToArray()));
    }

    [Fact]
    public async Task Successful_lifecycle_constructs_runtime_only_after_streams_and_exits_silently()
    {
        var environment = EnvironmentWithHandles();
        var streams = new TestBootstrapStreams();
        var lifetime = new RecordingLifetime();
        var runtimeCalls = 0;
        var standardErrorCalls = 0;

        var run = WorkerProcessEntry.RunAsync(
            ["--worker"],
            environment,
            values =>
            {
                AssertCaptureAndRemoval(environment);
                Assert.Equal(new WorkerBootstrapValues("101", "202"), values);
                streams.Opened = true;
                return streams;
            },
            (_, _) =>
            {
                Assert.True(streams.Opened);
                Assert.False(streams.Disposed);
                Interlocked.Increment(ref runtimeCalls);
                return Task.FromResult<ISessionLifetime>(lifetime);
            },
            () => BootId,
            () =>
            {
                standardErrorCalls++;
                throw new InvalidOperationException("zero exit touched stderr");
            });

        var eventReader = new WorkerProtocolReader(streams.EventStream);
        var requestWriter = new WorkerProtocolWriter(streams.RequestStream);
        var hello = await ReadAsync(eventReader);
        Assert.Equal(WorkerMessageKind.Event, hello.Kind);
        Assert.Equal(BootId, hello.WorkerBootId);
        Assert.Equal("hello", hello.Payload.GetProperty("event").GetString());
        Assert.Equal(0, Volatile.Read(ref runtimeCalls));

        await WriteInitializeAsync(requestWriter);
        var ready = await ReadAsync(eventReader);
        Assert.Equal(WorkerMessageKind.Response, ready.Kind);
        Assert.Equal("ready", ready.Payload.GetProperty("status").GetString());
        Assert.Equal(1, Volatile.Read(ref runtimeCalls));

        await WriteShutdownAsync(requestWriter);
        var stopped = await ReadAsync(eventReader);
        Assert.Equal(WorkerMessageKind.Response, stopped.Kind);
        Assert.Equal("stopped", stopped.Payload.GetProperty("status").GetString());

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, runtimeCalls);
        Assert.Equal(1, lifetime.ShutdownCalls);
        Assert.Equal(1, lifetime.DisposeCalls);
        Assert.True(streams.Disposed);
        Assert.Equal(["request", "event"], streams.DisposeOrder);
        Assert.Equal(0, standardErrorCalls);
    }

    [Fact]
    public async Task Protocol_eof_before_initialize_is_a_silent_zero_exit()
    {
        var streams = new TestBootstrapStreams();
        var runtimeCalls = 0;
        var standardErrorCalls = 0;
        var run = StartEntry(
            streams,
            (_, _) =>
            {
                runtimeCalls++;
                throw new InvalidOperationException("runtime must not run");
            },
            () =>
            {
                standardErrorCalls++;
                throw new InvalidOperationException("zero exit touched stderr");
            });

        var eventReader = new WorkerProtocolReader(streams.EventStream);
        _ = await ReadAsync(eventReader);
        streams.RequestStreamPipe.CompleteWriting();

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, runtimeCalls);
        Assert.Equal(0, standardErrorCalls);
        Assert.True(streams.Disposed);
    }

    [Fact]
    public async Task Server_protocol_failure_exits_82_and_never_constructs_runtime()
    {
        var streams = new TestBootstrapStreams();
        var diagnostic = new MemoryStream();
        var runtimeCalls = 0;
        var run = StartEntry(
            streams,
            (_, _) =>
            {
                runtimeCalls++;
                throw new InvalidOperationException("runtime must not run");
            },
            () => diagnostic);

        var eventReader = new WorkerProtocolReader(streams.EventStream);
        var requestWriter = new WorkerProtocolWriter(streams.RequestStream);
        _ = await ReadAsync(eventReader);
        await requestWriter.WriteAsync(new WorkerEnvelope(
            WorkerProtocol.Version,
            WorkerMessageKind.Shutdown,
            BootId,
            1,
            JsonSerializer.SerializeToElement(new { })));

        Assert.Equal(82, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, runtimeCalls);
        Assert.Equal(
            "ptk_worker_exit kind=protocol_error detail=initialize_required\n",
            Encoding.ASCII.GetString(diagnostic.ToArray()));
    }

    [Fact]
    public async Task Runtime_cleanup_failure_exits_84()
    {
        var streams = new TestBootstrapStreams();
        var diagnostic = new MemoryStream();
        var lifetime = new RecordingLifetime { ThrowOnShutdown = true };
        var run = StartEntry(
            streams,
            (_, _) => Task.FromResult<ISessionLifetime>(lifetime),
            () => diagnostic);

        var eventReader = new WorkerProtocolReader(streams.EventStream);
        var requestWriter = new WorkerProtocolWriter(streams.RequestStream);
        _ = await ReadAsync(eventReader);
        await WriteInitializeAsync(requestWriter);
        _ = await ReadAsync(eventReader);
        await WriteShutdownAsync(requestWriter);
        var failed = await ReadAsync(eventReader);
        Assert.Equal("failed", failed.Payload.GetProperty("status").GetString());
        Assert.Equal("shutdown_failed", failed.Payload.GetProperty("detailCode").GetString());

        Assert.Equal(84, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, lifetime.ShutdownCalls);
        Assert.Equal(1, lifetime.DisposeCalls);
        Assert.Equal(
            "ptk_worker_exit kind=runtime_failure detail=shutdown_failed\n",
            Encoding.ASCII.GetString(diagnostic.ToArray()));
    }

    [Fact]
    public async Task Stream_owner_cleanup_failure_upgrades_clean_shutdown_to_exit_84()
    {
        var streams = new TestBootstrapStreams { ThrowOnDispose = true };
        var diagnostic = new MemoryStream();
        var run = StartEntry(
            streams,
            (_, _) => Task.FromResult<ISessionLifetime>(new RecordingLifetime()),
            () => diagnostic);

        var eventReader = new WorkerProtocolReader(streams.EventStream);
        var requestWriter = new WorkerProtocolWriter(streams.RequestStream);
        _ = await ReadAsync(eventReader);
        await WriteInitializeAsync(requestWriter);
        _ = await ReadAsync(eventReader);
        await WriteShutdownAsync(requestWriter);
        var stopped = await ReadAsync(eventReader);
        Assert.Equal("stopped", stopped.Payload.GetProperty("status").GetString());

        Assert.Equal(84, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(streams.Disposed);
        Assert.Equal(["request", "event"], streams.DisposeOrder);
        Assert.Equal(
            "ptk_worker_exit kind=runtime_failure detail=cleanup_failed\n",
            Encoding.ASCII.GetString(diagnostic.ToArray()));
    }

    [Fact]
    public async Task Standard_error_factory_failure_preserves_selected_exit_code()
    {
        var streams = new TestBootstrapStreams();
        var run = StartEntry(
            streams,
            (_, _) => throw new InvalidOperationException("runtime must not run"),
            () => throw new IOException("simulated stderr open failure"));

        var eventReader = new WorkerProtocolReader(streams.EventStream);
        var requestWriter = new WorkerProtocolWriter(streams.RequestStream);
        _ = await ReadAsync(eventReader);
        await requestWriter.WriteAsync(new WorkerEnvelope(
            WorkerProtocol.Version,
            WorkerMessageKind.Shutdown,
            BootId,
            1,
            JsonSerializer.SerializeToElement(new { })));

        Assert.Equal(82, await run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Managed_failure_exits_write_once_only_to_injected_standard_error()
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        var directOutput = new StringWriter();
        var directError = new StringWriter();

        Console.SetOut(directOutput);
        Console.SetError(directError);
        try
        {
            foreach (var expected in new[]
            {
                new ManagedFailureExpectation(
                    ManagedFailure.Initialize,
                    81,
                    "ptk_worker_exit kind=initialize_failed detail=initialize_failed\n"),
                new ManagedFailureExpectation(
                    ManagedFailure.Protocol,
                    82,
                    "ptk_worker_exit kind=protocol_error detail=initialize_required\n"),
                new ManagedFailureExpectation(
                    ManagedFailure.Transport,
                    83,
                    "ptk_worker_exit kind=transport_failure detail=event_transport_failure\n"),
                new ManagedFailureExpectation(
                    ManagedFailure.Runtime,
                    84,
                    "ptk_worker_exit kind=runtime_failure detail=shutdown_failed\n"),
            })
            {
                using var diagnostic = new CountingWriteStream();

                var exitCode = await DriveManagedFailureAsync(
                    expected.Failure,
                    diagnostic);

                Assert.Equal(expected.ExitCode, exitCode);
                Assert.Equal(1, diagnostic.WriteCalls);
                var line = Encoding.ASCII.GetString(diagnostic.ToArray());
                Assert.Equal(expected.Diagnostic, line);
                Assert.Equal(1, line.Count(character => character == '\n'));
            }
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }

        Assert.Equal(string.Empty, directOutput.ToString());
        Assert.Equal(string.Empty, directError.ToString());
    }

    [Fact]
    public void Worker_diagnostics_have_no_global_console_bypass()
    {
        var workerDirectory = Path.Combine(
            FindRepositoryRoot(),
            "server",
            "PtkMcpServer",
            "Worker");
        var exitSource = File.ReadAllText(Path.Combine(
            workerDirectory,
            "WorkerProcessExit.cs"));
        var entrySource = File.ReadAllText(Path.Combine(
            workerDirectory,
            "WorkerProcessEntry.cs"));

        Assert.Empty(FindConsoleMembers(exitSource));
        Assert.DoesNotContain("System.Console", exitSource, StringComparison.Ordinal);

        Assert.Equal(
            [
                "OpenStandardError",
            ],
            FindConsoleMembers(entrySource));
        Assert.DoesNotContain("System.Console", entrySource, StringComparison.Ordinal);
        foreach (var bypass in new[]
        {
            "Console.Out",
            "Console.Error",
            "OpenStandardOutput",
            "GetStdHandle",
            "STD_OUTPUT_HANDLE",
            "STD_ERROR_HANDLE",
        })
        {
            Assert.DoesNotContain(bypass, entrySource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Program_private_role_return_precedes_every_public_startup_boundary()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "server",
            "PtkMcpServer",
            "Program.cs"));

        var classification = RequiredIndex(
            source,
            "PrivateHostProcessEntry.Classify(args)");
        var privateRun = RequiredIndex(
            source,
            "PrivateHostProcessEntry.RunClassifiedProductionAsync(privateRole)");
        var privateReturn = source.IndexOf("return;", privateRun, StringComparison.Ordinal);
        Assert.True(
            privateReturn > privateRun,
            "private branch must return after private-role execution");

        Assert.True(classification < privateRun);
        foreach (var supervisorBoundary in new[]
        {
            "Host.CreateApplicationBuilder(args)",
            "DefaultSessionRuntimeFactory.ReadCallTimeout()",
            "JobPwshExecutable.ResolveFromPath()",
            "AuditStartupConfiguration.LoadFromEnvironment()",
            "new OutputStore(",
            ".AddMcpServer(",
            "ChildStdinGuard.DetachChildStdin()",
        })
        {
            Assert.True(
                privateReturn < RequiredIndex(source, supervisorBoundary),
                $"private return must precede {supervisorBoundary}");
        }
    }

    [Fact]
    public async Task Subprocess_malformed_worker_bypasses_poisoned_audit_startup()
    {
        var result = await RunPrivateSubprocessAsync("--worker", "extra");

        Assert.Equal(64, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Subprocess_missing_handles_bypasses_poisoned_audit_startup()
    {
        var result = await RunPrivateSubprocessAsync("--worker");

        var expectedDetail = OperatingSystem.IsWindows()
            ? "handle_missing"
            : "platform_unsupported";
        Assert.Equal(80, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(
            $"ptk_worker_exit kind=bootstrap_failure detail={expectedDetail}\n",
            result.StandardError);
    }

    [Fact]
    public async Task Subprocess_missing_host_handles_bypasses_poisoned_audit_startup()
    {
        var result = await RunPrivateSubprocessAsync("--host");

        Assert.Equal(PrivateHostProcessExit.BootstrapFailureExitCode, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(
            "ptk_host_exit kind=bootstrap_failure detail=handle_missing\n",
            result.StandardError);
    }

    private static Task<int> StartEntry(
        TestBootstrapStreams streams,
        Func<WorkerInitializeRequest, CancellationToken, Task<ISessionLifetime>> runtimeFactory,
        Func<Stream> standardErrorFactory)
    {
        var environment = EnvironmentWithHandles();
        return WorkerProcessEntry.RunAsync(
            ["--worker"],
            environment,
            values =>
            {
                AssertCaptureAndRemoval(environment);
                Assert.Equal(new WorkerBootstrapValues("101", "202"), values);
                streams.Opened = true;
                return streams;
            },
            runtimeFactory,
            () => BootId,
            standardErrorFactory);
    }

    private static async Task<int> DriveManagedFailureAsync(
        ManagedFailure failure,
        Stream diagnostic)
    {
        var streams = failure == ManagedFailure.Transport
            ? new TestBootstrapStreams { EventStreamOverride = new ThrowingWriteStream() }
            : new TestBootstrapStreams();
        var lifetime = new RecordingLifetime
        {
            ThrowOnShutdown = failure == ManagedFailure.Runtime,
        };
        var run = StartEntry(
            streams,
            (_, _) => failure == ManagedFailure.Initialize
                ? Task.FromException<ISessionLifetime>(
                    new IOException("simulated runtime initialization failure"))
                : Task.FromResult<ISessionLifetime>(lifetime),
            () => diagnostic);

        if (failure == ManagedFailure.Transport)
            return await run.WaitAsync(TimeSpan.FromSeconds(10));

        var eventReader = new WorkerProtocolReader(streams.EventStream);
        var requestWriter = new WorkerProtocolWriter(streams.RequestStream);
        _ = await ReadAsync(eventReader);

        if (failure == ManagedFailure.Protocol)
        {
            await requestWriter.WriteAsync(new WorkerEnvelope(
                WorkerProtocol.Version,
                WorkerMessageKind.Shutdown,
                BootId,
                1,
                JsonSerializer.SerializeToElement(new { })));
            return await run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await WriteInitializeAsync(requestWriter);
        var initialized = await ReadAsync(eventReader);
        if (failure == ManagedFailure.Initialize)
        {
            Assert.Equal("failed", initialized.Payload.GetProperty("status").GetString());
            Assert.Equal(
                "initialize_failed",
                initialized.Payload.GetProperty("detailCode").GetString());
            return await run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal("ready", initialized.Payload.GetProperty("status").GetString());
        await WriteShutdownAsync(requestWriter);
        var stopped = await ReadAsync(eventReader);
        Assert.Equal("failed", stopped.Payload.GetProperty("status").GetString());
        Assert.Equal("shutdown_failed", stopped.Payload.GetProperty("detailCode").GetString());
        return await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static IReadOnlyList<string> FindConsoleMembers(string source) =>
        Regex.Matches(
                source,
                @"(?<![A-Za-z0-9_])Console\s*\.\s*(?<member>[A-Za-z_][A-Za-z0-9_]*)",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["member"].Value)
            .ToArray();

    private static async Task WriteInitializeAsync(WorkerProtocolWriter writer)
    {
        await writer.WriteAsync(new WorkerEnvelope(
            WorkerProtocol.Version,
            WorkerMessageKind.Initialize,
            BootId,
            1,
            JsonSerializer.SerializeToElement(new
            {
                generation = 1L,
                deadlineUnixTimeMilliseconds = DateTimeOffset.UtcNow
                    .AddSeconds(30)
                    .ToUnixTimeMilliseconds(),
            })));
    }

    private static async Task WriteShutdownAsync(WorkerProtocolWriter writer)
    {
        await writer.WriteAsync(new WorkerEnvelope(
            WorkerProtocol.Version,
            WorkerMessageKind.Shutdown,
            BootId,
            2,
            JsonSerializer.SerializeToElement(new { })));
    }

    private static async Task<WorkerEnvelope> ReadAsync(WorkerProtocolReader reader)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await reader.ReadAsync(cancellation.Token) ??
            throw new EndOfStreamException("Worker protocol stream closed before its expected frame.");
    }

    private static RecordingEnvironment EnvironmentWithHandles() => new(
        new Dictionary<string, string?>
        {
            [WorkerBootstrapEnvironment.RequestHandle] = "101",
            [WorkerBootstrapEnvironment.EventHandle] = "202",
        });

    private static void AssertCaptureAndRemoval(RecordingEnvironment environment)
    {
        Assert.Equal(
            [
                "get:" + WorkerBootstrapEnvironment.RequestHandle,
                "get:" + WorkerBootstrapEnvironment.EventHandle,
                "remove:" + WorkerBootstrapEnvironment.RequestHandle,
                "remove:" + WorkerBootstrapEnvironment.EventHandle,
            ],
            environment.Operations);
        Assert.False(environment.Values.ContainsKey(
            WorkerBootstrapEnvironment.RequestHandle));
        Assert.False(environment.Values.ContainsKey(
            WorkerBootstrapEnvironment.EventHandle));
    }

    private static async Task<SubprocessResult> RunPrivateSubprocessAsync(
        params string[] arguments)
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "PtkMcpServer.dll");
        Assert.True(File.Exists(serverDll), $"server dll not found at {serverDll}");
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(serverDll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PTK_AUDIT_EXPORT_CONFIG"] = string.Empty;
        foreach (var variable in PrivateHostBootstrapEnvironment.VariablesInCaptureOrder)
            start.Environment.Remove(variable);
        start.Environment.Remove(WorkerBootstrapEnvironment.RequestHandle);
        start.Environment.Remove(WorkerBootstrapEnvironment.EventHandle);

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Worker subprocess did not start.");
        process.StandardInput.Close();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            return new SubprocessResult(
                process.ExitCode,
                await standardOutput,
                await standardError);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* The worker already exited or never started. */ }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "server",
                    "PtkMcpServer",
                    "Program.cs")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException(
            "Repository root not found upward from the test base directory.");
    }

    private static int RequiredIndex(string source, string value)
    {
        var index = source.IndexOf(value, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected source marker not found: {value}");
        return index;
    }

    private sealed record SubprocessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class RecordingEnvironment(
        Dictionary<string, string?> values,
        List<string>? operations = null) : IWorkerBootstrapEnvironmentSource
    {
        internal Dictionary<string, string?> Values { get; } = values;
        internal List<string> Operations { get; } = operations ?? [];
        internal bool ThrowOnRequestGet { get; set; }

        public string? Get(string variable)
        {
            Operations.Add("get:" + variable);
            if (ThrowOnRequestGet &&
                variable == WorkerBootstrapEnvironment.RequestHandle)
            {
                throw new InvalidOperationException("simulated environment read failure");
            }
            return Values.GetValueOrDefault(variable);
        }

        public void Remove(string variable)
        {
            Operations.Add("remove:" + variable);
            Values.Remove(variable);
        }
    }

    private sealed class RecordingArguments(
        IReadOnlyList<string> arguments,
        List<string> operations) : IReadOnlyList<string>
    {
        public int Count
        {
            get
            {
                operations.Add("arguments:count");
                return arguments.Count;
            }
        }

        public string this[int index]
        {
            get
            {
                operations.Add("arguments:get:" + index);
                return arguments[index];
            }
        }

        public IEnumerator<string> GetEnumerator() => arguments.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class TestBootstrapStreams : IWorkerBootstrapStreams
    {
        internal ChannelStream RequestStreamPipe { get; } = new();
        internal ChannelStream EventStreamPipe { get; } = new();
        internal bool Opened { get; set; }
        internal bool Disposed { get; private set; }
        internal bool ThrowOnDispose { get; set; }
        internal Stream? EventStreamOverride { get; init; }
        internal List<string> DisposeOrder { get; } = [];

        public Stream RequestStream => RequestStreamPipe;
        public Stream EventStream => EventStreamOverride ?? EventStreamPipe;

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            DisposeOrder.Add("request");
            RequestStreamPipe.Dispose();
            DisposeOrder.Add("event");
            EventStream.Dispose();
            if (ThrowOnDispose)
                throw new IOException("simulated bootstrap stream cleanup failure");
        }
    }

    private sealed class CountingWriteStream : MemoryStream
    {
        internal int WriteCalls { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCalls++;
            base.Write(buffer, offset, count);
        }
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("simulated initial event write failure");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("simulated initial event write failure"));
    }

    private enum ManagedFailure
    {
        Initialize,
        Protocol,
        Transport,
        Runtime,
    }

    private sealed record ManagedFailureExpectation(
        ManagedFailure Failure,
        int ExitCode,
        string Diagnostic);

    private sealed class RecordingLifetime : ISessionLifetime
    {
        internal int ShutdownCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal bool ThrowOnShutdown { get; set; }

        public Task ShutdownAsync()
        {
            ShutdownCalls++;
            return ThrowOnShutdown
                ? Task.FromException(new IOException("simulated runtime shutdown failure"))
                : Task.CompletedTask;
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class ChannelStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        private byte[]? _current;
        private int _currentOffset;
        private int _completed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void CompleteWriting()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
                _channel.Writer.TryComplete();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            while (true)
            {
                if (_current is { } current && _currentOffset < current.Length)
                {
                    var count = Math.Min(buffer.Length, current.Length - _currentOffset);
                    current.AsMemory(_currentOffset, count).CopyTo(buffer);
                    _currentOffset += count;
                    if (_currentOffset == current.Length)
                    {
                        _current = null;
                        _currentOffset = 0;
                    }
                    return count;
                }

                while (await _channel.Reader.WaitToReadAsync(cancellationToken)
                           .ConfigureAwait(false))
                {
                    if (_channel.Reader.TryRead(out var next))
                    {
                        _current = next;
                        _currentOffset = 0;
                        break;
                    }
                }
                if (_current is null) return 0;
            }
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _completed) != 0)
                return ValueTask.FromException(new ObjectDisposedException(nameof(ChannelStream)));
            return _channel.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Volatile.Read(ref _completed) != 0)
                throw new ObjectDisposedException(nameof(ChannelStream));
            if (!_channel.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray()))
                throw new IOException("Channel stream is closed.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) CompleteWriting();
            base.Dispose(disposing);
        }
    }
}
