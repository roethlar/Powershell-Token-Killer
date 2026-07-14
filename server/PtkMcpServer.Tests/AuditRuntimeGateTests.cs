using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;

namespace PtkMcpServer.Tests;

public sealed class AuditRuntimeGateTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* assertion failures remain authoritative */ }
        }
    }

    [Fact]
    public async Task Broken_startup_stays_diagnostic_only_and_never_constructs_runtime()
    {
        var parent = NewRoot();
        var blocker = Path.Combine(parent, "blocked");
        File.WriteAllText(blocker, "not a directory");
        var options = CreateOptions(Path.Combine(blocker, "audit"));
        var health = new AuditHealth(options);
        using var runtime = CreateRuntime(options, health);
        await runtime.StartAsync(CancellationToken.None);

        var snapshot = health.Snapshot();
        Assert.Equal(AuditHealthState.Unavailable, snapshot.State);
        Assert.Equal("journal.startup", snapshot.FailureClass);

        var factoryCalls = 0;
        Assert.Throws<AuditUnavailableException>(() =>
            runtime.RunAfterStarted(() =>
            {
                factoryCalls++;
                return new object();
            }));
        Assert.Equal(0, factoryCalls);

        using var services = new ServiceCollection()
            .AddSingleton(runtime)
            .AddSingleton(new AuditCallContextAccessor())
            .BuildServiceProvider();
        var handlerCalls = 0;
        var state = await Filter(
            services,
            Call("ptk_state"),
            _ =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Text("must not run"));
            });
        Assert.False(state.IsError ?? false);
        Assert.Contains("audit=unavailable", ResultText(state));
        Assert.Contains("unrecorded=true", ResultText(state));
        Assert.Contains("failure_class=journal.startup", ResultText(state));

        var invoke = await Filter(
            services,
            Call("ptk_invoke", ("script", "'must-not-run'")),
            _ =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Text("must not run"));
            });
        Assert.True(invoke.IsError);
        Assert.Contains("operation was not started", ResultText(invoke));
        Assert.Equal(0, handlerCalls);
    }

    [Fact]
    public async Task Export_starts_after_server_started_and_stops_before_server_stopped()
    {
        var root = NewRoot();
        var options = CreateOptions(Path.Combine(root, "audit"));
        var health = new AuditHealth(options);
        BlockingExportSource? source = null;

        AuditRuntimeResources OpenRuntime()
        {
            var journal = AuditJournalFactory.Open(
                options,
                health,
                "runtime-export-order-test");
            source = new BlockingExportSource(() => journal.Sequence);
            var loop = new AuditExportLoop(
                source,
                idlePollInterval: TimeSpan.FromMilliseconds(10));
            return new AuditRuntimeResources(journal, exportLoop: loop);
        }

        var runtime = new AuditRuntimeGate(
            options,
            health,
            new ScriptEvidenceStoreProvider(options),
            "runtime-export-order-test",
            openRuntime: OpenRuntime);
        await runtime.StartAsync(CancellationToken.None);
        var activeSource = Assert.IsType<BlockingExportSource>(source);
        await activeSource.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, activeSource.SequenceAtStart);
        await runtime.StopAsync(CancellationToken.None);
        Assert.False(activeSource.ServerStoppedWasDurableAtCancellation);
        runtime.Dispose();

        Assert.True(activeSource.Disposed);
        Assert.Equal(
            ["server.started", "server.stopped"],
            ReadEvents(options).Select(EventType));
    }

    [Fact]
    public async Task Export_loop_failure_prevents_false_server_stopped()
    {
        var root = NewRoot();
        var options = CreateOptions(Path.Combine(root, "audit"));
        var health = new AuditHealth(options);
        ThrowingExportSource? source = null;

        AuditRuntimeResources OpenRuntime()
        {
            var journal = AuditJournalFactory.Open(
                options,
                health,
                "runtime-export-fault-test");
            source = new ThrowingExportSource();
            return new AuditRuntimeResources(
                journal,
                exportLoop: new AuditExportLoop(source));
        }

        var runtime = new AuditRuntimeGate(
            options,
            health,
            new ScriptEvidenceStoreProvider(options),
            "runtime-export-fault-test",
            openRuntime: OpenRuntime);
        await runtime.StartAsync(CancellationToken.None);
        await Assert.IsType<ThrowingExportSource>(source).Faulted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<IOException>(() =>
            runtime.StopAsync(CancellationToken.None));
        Assert.ThrowsAny<Exception>(() => runtime.Dispose());
        Assert.Equal(
            ["server.started"],
            ReadEvents(options).Select(EventType));
    }

    [Fact]
    public async Task Repaired_startup_persists_recovery_before_start_and_acceptance()
    {
        var parent = NewRoot();
        var blocker = Path.Combine(parent, "blocked");
        File.WriteAllText(blocker, "not a directory");
        var options = CreateOptions(Path.Combine(blocker, "audit"));
        var health = new AuditHealth(options);
        using var runtime = CreateRuntime(options, health);
        await runtime.StartAsync(CancellationToken.None);
        using var services = new ServiceCollection()
            .AddSingleton(runtime)
            .AddSingleton(new AuditCallContextAccessor())
            .BuildServiceProvider();

        var emergency = await Filter(
            services,
            Call("ptk_state"),
            _ => throw new InvalidOperationException("handler must not run"));
        Assert.Contains("audit=unavailable", ResultText(emergency));

        File.Delete(blocker);
        var handlerCalls = 0;
        var admitted = await Filter(
            services,
            Call("ptk_invoke", ("script", "'recovered'")),
            _ =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Text("handled"));
            });
        Assert.False(admitted.IsError ?? false);
        Assert.Equal(1, handlerCalls);
        Assert.Equal(AuditHealthState.Healthy, health.Snapshot().State);

        await runtime.StopAsync(CancellationToken.None);
        runtime.Dispose();
        var events = ReadEvents(options);
        Assert.Equal(
            ["audit.recovered", "server.started", "call.accepted", "call.completed", "server.stopped"],
            events.Select(EventType));
        var recovered = events[0].RootElement.GetProperty("audit");
        Assert.Equal(1, recovered.GetProperty("emergency_probe_count").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, recovered.GetProperty("emergency_probe_first_utc").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, recovered.GetProperty("emergency_probe_last_utc").ValueKind);
    }

    [Fact]
    public async Task Evidence_failure_is_lazy_and_emergency_state_does_not_probe_it()
    {
        var root = NewRoot();
        var options = CreateOptions(Path.Combine(root, "audit"));
        var health = new AuditHealth(options);
        using var runtime = CreateRuntime(options, health);
        await runtime.StartAsync(CancellationToken.None);
        File.WriteAllText(options.EvidenceDirectory, "not a directory");
        using var services = new ServiceCollection()
            .AddSingleton(runtime)
            .AddSingleton(new AuditCallContextAccessor())
            .BuildServiceProvider();

        var handlerCalls = 0;
        var refused = await Filter(
            services,
            Call("ptk_invoke", ("script", "'blocked evidence'")),
            _ =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Text("must not run"));
            });
        Assert.True(refused.IsError);
        Assert.Equal(0, handlerCalls);
        Assert.Equal("evidence.storage", health.Snapshot().FailureClass);

        var emergency = await Filter(
            services,
            Call("ptk_state"),
            _ => throw new InvalidOperationException("handler must not run"));
        Assert.Contains("failure_class=evidence.storage", ResultText(emergency));
        Assert.True(File.Exists(options.EvidenceDirectory));

        File.Delete(options.EvidenceDirectory);
        var admitted = await Filter(
            services,
            Call("ptk_invoke", ("script", "'recovered evidence'")),
            _ =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Text("handled"));
            });
        Assert.False(admitted.IsError ?? false);
        Assert.Equal(1, handlerCalls);

        await runtime.StopAsync(CancellationToken.None);
        runtime.Dispose();
        var events = ReadEvents(options);
        Assert.Equal(
            ["server.started", "audit.degraded", "audit.recovered", "call.accepted", "call.completed", "server.stopped"],
            events.Select(EventType));
        Assert.Equal(
            1,
            events[2].RootElement
                .GetProperty("audit")
                .GetProperty("emergency_probe_count")
                .GetInt64());
    }

    [Fact]
    public async Task Concurrent_startup_repair_opens_and_publishes_one_runtime()
    {
        var root = NewRoot();
        var options = CreateOptions(Path.Combine(root, "audit"));
        var health = new AuditHealth(options);
        using var repairEntered = new ManualResetEventSlim();
        using var releaseRepair = new ManualResetEventSlim();
        var openCalls = 0;
        AuditJournal OpenJournal()
        {
            var call = Interlocked.Increment(ref openCalls);
            if (call == 1) throw new IOException("injected first-start failure");
            if (call == 2)
            {
                repairEntered.Set();
                if (!releaseRepair.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("test did not release repair");
            }
            return AuditJournalFactory.Open(options, health, "runtime-race-test");
        }

        using var runtime = new AuditRuntimeGate(
            options,
            health,
            new ScriptEvidenceStoreProvider(options),
            "runtime-race-test",
            OpenJournal);
        await runtime.StartAsync(CancellationToken.None);
        using var services = new ServiceCollection()
            .AddSingleton(runtime)
            .AddScoped<AuditCallContextAccessor>()
            .BuildServiceProvider();
        var handlerCalls = 0;
        var overlappingHandlersEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOverlappingHandlers = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var contenderTimeout = TimeSpan.FromSeconds(15);
        var contenders = Enumerable.Range(0, 8)
            .Select(index => Task.Factory.StartNew(
                async () =>
                {
                    using var scope = services.CreateScope();
                    return await Filter(
                        scope.ServiceProvider,
                        Call("ptk_invoke", ("script", $"'startup contender {index}'")),
                        async _ =>
                        {
                            var entered = Interlocked.Increment(ref handlerCalls);
                            if (entered <= 2)
                            {
                                if (entered == 2)
                                    overlappingHandlersEntered.TrySetResult(true);
                                await releaseOverlappingHandlers.Task;
                            }
                            return Text("handled");
                        });
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap())
            .ToArray();
        try
        {
            Assert.True(repairEntered.Wait(TimeSpan.FromSeconds(5)), "repair did not enter the journal factory");
            releaseRepair.Set();

            try
            {
                await Task.WhenAny(
                    overlappingHandlersEntered.Task,
                    Task.Delay(contenderTimeout));
                Assert.True(
                    overlappingHandlersEntered.Task.IsCompleted,
                    $"Only {Volatile.Read(ref handlerCalls)} of the first 2 handlers entered.");
            }
            finally
            {
                releaseOverlappingHandlers.TrySetResult(true);
            }
            var results = await Task.WhenAll(contenders).WaitAsync(contenderTimeout);
            Assert.All(results, result => Assert.False(result.IsError ?? false));
            Assert.Equal(8, handlerCalls);
            Assert.Equal(2, Volatile.Read(ref openCalls));
            await runtime.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            runtime.Dispose();
        }
        finally
        {
            releaseRepair.Set();
            releaseOverlappingHandlers.TrySetResult(true);
        }

        var events = ReadEvents(options);
        var eventTypes = events.Select(EventType).ToArray();
        Assert.Equal("audit.recovered", eventTypes[0]);
        Assert.Equal("server.started", eventTypes[1]);
        Assert.Equal(1, eventTypes.Count(type => type == "audit.recovered"));
        Assert.Equal(1, eventTypes.Count(type => type == "server.started"));
        Assert.Equal(8, eventTypes.Count(type => type == "call.accepted"));
        Assert.Equal(8, eventTypes.Count(type => type == "call.completed"));
        Assert.Equal("server.stopped", eventTypes[^1]);
    }

    [Fact]
    public async Task Diagnostic_only_shutdown_is_idempotent_and_never_reopens_storage()
    {
        var root = NewRoot();
        var options = CreateOptions(Path.Combine(root, "audit"));
        var health = new AuditHealth(options);
        var openCalls = 0;
        using var runtime = new AuditRuntimeGate(
            options,
            health,
            new ScriptEvidenceStoreProvider(options),
            "runtime-stop-test",
            () =>
            {
                Interlocked.Increment(ref openCalls);
                throw new IOException("injected startup failure");
            });
        await runtime.StartAsync(CancellationToken.None);
        Assert.Equal(1, openCalls);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await runtime.StopAsync(canceled.Token);
        await runtime.StopAsync(CancellationToken.None);
        Assert.False(runtime.TryCreateCallContext(1, out _));
        var factoryCalls = 0;
        Assert.Throws<AuditUnavailableException>(() =>
            runtime.RunAfterStarted(() =>
            {
                factoryCalls++;
                return new object();
            }));
        Assert.Equal(0, factoryCalls);

        using var services = new ServiceCollection()
            .AddSingleton(runtime)
            .AddSingleton(new AuditCallContextAccessor())
            .BuildServiceProvider();
        var handlerCalls = 0;
        var state = await Filter(
            services,
            Call("ptk_state"),
            _ =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Text("must not run"));
            });
        Assert.True(state.IsError);
        Assert.Contains("operation was not started", ResultText(state));
        var invoke = await Filter(
            services,
            Call("ptk_invoke", ("script", "'must-not-run'")),
            _ =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Text("must not run"));
            });
        Assert.True(invoke.IsError);
        Assert.Equal(0, handlerCalls);
        Assert.Equal(1, openCalls);
    }

    [Fact]
    public async Task Shutdown_waits_for_an_admitted_call_terminal_before_server_stopped()
    {
        var root = NewRoot();
        var options = CreateOptions(Path.Combine(root, "audit"));
        var health = new AuditHealth(options);
        using var runtime = CreateRuntime(options, health);
        await runtime.StartAsync(CancellationToken.None);
        using var services = new ServiceCollection()
            .AddSingleton(runtime)
            .AddSingleton(new AuditCallContextAccessor())
            .BuildServiceProvider();
        var handlerEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var call = Filter(
            services,
            Call("ptk_state"),
            async _ =>
            {
                handlerEntered.TrySetResult(true);
                await releaseHandler.Task;
                return Text("handled");
            }).AsTask();
        Task? stop = null;
        try
        {
            await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            stop = runtime.StopAsync(CancellationToken.None);
            await Task.Delay(100);
            Assert.False(stop.IsCompleted, "server shutdown overtook an admitted call");
            releaseHandler.TrySetResult(true);
            await call.WaitAsync(TimeSpan.FromSeconds(10));
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            runtime.Dispose();
        }
        finally
        {
            releaseHandler.TrySetResult(true);
            try { await call.WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* preserve primary failure */ }
            if (stop is not null)
            {
                try { await stop.WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* preserve primary failure */ }
            }
        }

        var events = ReadEvents(options);
        Assert.Equal(
            ["server.started", "call.accepted", "call.completed", "server.stopped"],
            events.Select(EventType));
    }

    [Fact]
    public async Task Shutdown_kills_jobs_and_awaits_their_terminal_callback_before_server_stopped()
    {
        var root = NewRoot();
        var options = CreateOptions(Path.Combine(root, "audit"));
        var health = new AuditHealth(options);
        using var runtime = CreateRuntime(options, health);
        await runtime.StartAsync(CancellationToken.None);
        var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(5));
        var jobs = new JobManager(Path.Combine(root, "jobs"));
        using var session = runtime.RunSessionAfterStarted(() =>
            new SessionRuntime(host, jobs, new RawUsageCounter()));
        var callbackEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        const string script = "Start-Sleep -Seconds 300";
        Assert.True(AuditCallMetadataCapture.TryCapture(
            Call("ptk_invoke", ("script", script), ("background", true)),
            new AuditClientContext("runtime-test", "1", "session"),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            DateTimeOffset.UtcNow,
            out var metadata,
            out var exactScript,
            out _));
        Assert.True(runtime.TryBeginCall(
            metadata!, exactScript, out var audit, out var activeCall, out _));
        using var activeCallLease = activeCall!;
        var plan = jobs.PrepareStart(script, Directory.GetCurrentDirectory());
        var terminal = Assert.IsType<AuditJobTerminalLease>(
            audit!.AuthorizeJobStart(plan.Id, Directory.GetCurrentDirectory()));
        var job = jobs.CommitStart(
            plan,
            async snapshot =>
            {
                callbackEntered.TrySetResult(true);
                await releaseCallback.Task;
                await terminal.CompleteAsync(snapshot);
            });
        Assert.True(audit.RecordJobStarted(job.Id, "started"));
        Assert.True(jobs.ConfirmStartRecorded(plan.Id));
        activeCallLease.Dispose();

        Task? stop = null;
        try
        {
            stop = runtime.StopAsync(CancellationToken.None);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(stop.IsCompleted, "server shutdown overtook a job terminal callback");
            releaseCallback.TrySetResult(true);
            await stop.WaitAsync(TimeSpan.FromSeconds(15));
            runtime.Dispose();
        }
        finally
        {
            releaseCallback.TrySetResult(true);
            if (stop is not null)
            {
                try { await stop.WaitAsync(TimeSpan.FromSeconds(15)); } catch { /* preserve primary failure */ }
            }
        }

        var events = ReadEvents(options);
        Assert.Equal("job.killed", EventType(events[^2]));
        Assert.Equal(
            "shutdown",
            events[^2].RootElement.GetProperty("outcome").GetProperty("detail_code").GetString());
        Assert.Equal("server.stopped", EventType(events[^1]));
    }

    [Fact]
    public async Task Shutdown_awaits_the_owned_session_through_its_lifetime_seam()
    {
        var root = NewRoot();
        var options = CreateOptions(Path.Combine(root, "audit"));
        var health = new AuditHealth(options);
        using var runtime = CreateRuntime(options, health);
        await runtime.StartAsync(CancellationToken.None);
        var session = new BlockingSessionLifetime();

        var tracked = runtime.RunSessionAfterStarted(() => session);

        Assert.Same(session, tracked);
        Task? stop = null;
        try
        {
            stop = runtime.StopAsync(CancellationToken.None);
            await session.ShutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, session.ShutdownCount);
            Assert.False(stop.IsCompleted, "server shutdown overtook the owned session lifetime");
            session.ReleaseShutdown.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            runtime.Dispose();
            Assert.Equal(1, session.ShutdownCount);
        }
        finally
        {
            session.ReleaseShutdown.TrySetResult();
            if (stop is not null)
            {
                try { await stop.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { /* preserve primary failure */ }
            }
        }
        Assert.Equal(
            ["server.started", "server.stopped"],
            ReadEvents(options).Select(EventType));
    }

    [Fact]
    public async Task Shutdown_awaits_runspace_teardown_before_server_stopped()
    {
        var root = NewRoot();
        var options = CreateOptions(Path.Combine(root, "audit"));
        var health = new AuditHealth(options);
        using var runtime = CreateRuntime(options, health);
        await runtime.StartAsync(CancellationToken.None);
        var shutdownEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseShutdown = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(5));
        var jobs = new JobManager(Path.Combine(root, "jobs"));
        using var session = runtime.RunSessionAfterStarted(() =>
            new SessionRuntime(host, jobs, new RawUsageCounter()));
        host.TrackOwnedBackgroundWorkForTests(Task.Run(async () =>
        {
            shutdownEntered.TrySetResult(true);
            await releaseShutdown.Task;
        }));

        Task? stop = null;
        try
        {
            stop = runtime.StopAsync(CancellationToken.None);
            await shutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(stop.IsCompleted, "server shutdown overtook runspace teardown");

            releaseShutdown.TrySetResult(true);
            await stop.WaitAsync(TimeSpan.FromSeconds(15));
            runtime.Dispose();
        }
        finally
        {
            releaseShutdown.TrySetResult(true);
            if (stop is not null)
            {
                try { await stop.WaitAsync(TimeSpan.FromSeconds(15)); } catch { /* preserve primary failure */ }
            }
        }

        Assert.Equal(
            ["server.started", "server.stopped"],
            ReadEvents(options).Select(EventType));
    }

    [Fact]
    public async Task Shutdown_cannot_capture_runtime_between_construction_and_tracking()
    {
        var root = NewRoot();
        var options = CreateOptions(Path.Combine(root, "audit"));
        var health = new AuditHealth(options);
        using var runtime = CreateRuntime(options, health);
        await runtime.StartAsync(CancellationToken.None);
        var factoryEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseShutdown = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        JobManager? manager = null;
        var construction = Task.Run(() => runtime.RunSessionAfterStarted(() =>
        {
            factoryEntered.TrySetResult(true);
            releaseFactory.Task.GetAwaiter().GetResult();
            var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(5));
            manager = new JobManager(Path.Combine(root, "tracked-jobs"))
            {
                ShutdownOverrideForTests = async () =>
                {
                    shutdownEntered.TrySetResult(true);
                    await releaseShutdown.Task;
                },
            };
            return new SessionRuntime(host, manager, new RawUsageCounter());
        }));
        Task? stop = null;
        SessionRuntime? session = null;
        try
        {
            await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop = Task.Run(async () =>
                await runtime.StopAsync(CancellationToken.None));
            await Task.Delay(100);
            Assert.False(stop.IsCompleted, "shutdown entered the construction/tracking transaction");

            releaseFactory.TrySetResult(true);
            session = await construction.WaitAsync(TimeSpan.FromSeconds(10));
            await shutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(stop.IsCompleted, "shutdown failed to track the constructed runtime");
            releaseShutdown.TrySetResult(true);
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            runtime.Dispose();
        }
        finally
        {
            releaseFactory.TrySetResult(true);
            releaseShutdown.TrySetResult(true);
            try { session ??= await construction.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            if (stop is not null)
            {
                try { await stop.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            }
            session?.Dispose();
        }

        Assert.Equal(
            ["server.started", "server.stopped"],
            ReadEvents(options).Select(EventType));
    }

    [Fact]
    public async Task Concurrent_evidence_recovery_closes_one_outage_without_resurrecting_it()
    {
        var root = NewRoot();
        var options = CreateOptions(Path.Combine(root, "audit"));
        var health = new AuditHealth(options);
        using var runtime = CreateRuntime(options, health);
        await runtime.StartAsync(CancellationToken.None);
        File.WriteAllText(options.EvidenceDirectory, "not a directory");
        using var services = new ServiceCollection()
            .AddSingleton(runtime)
            .AddScoped<AuditCallContextAccessor>()
            .BuildServiceProvider();

        CallToolResult first;
        using (var scope = services.CreateScope())
        {
            first = await Filter(
                scope.ServiceProvider,
                Call("ptk_invoke", ("script", "'initial failure'")),
                _ => throw new InvalidOperationException("handler must not run"));
        }
        Assert.True(first.IsError);
        Assert.Equal("evidence.storage", health.Snapshot().FailureClass);
        File.Delete(options.EvidenceDirectory);

        var start = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var overlappingHandlersEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOverlappingHandlers = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCalls = 0;
        var contenderTimeout = TimeSpan.FromSeconds(20);
        var contenders = Enumerable.Range(0, 8)
            .Select(index => Task.Run(async () =>
            {
                await start.Task;
                using var scope = services.CreateScope();
                return await Filter(
                    scope.ServiceProvider,
                    Call("ptk_invoke", ("script", $"'recovered {index}'")),
                    async _ =>
                    {
                        var entered = Interlocked.Increment(ref handlerCalls);
                        if (entered <= 2)
                        {
                            if (entered == 2)
                                overlappingHandlersEntered.TrySetResult(true);
                            await releaseOverlappingHandlers.Task;
                        }
                        return Text("handled");
                    });
            }))
            .ToArray();
        start.TrySetResult(true);
        try
        {
            try
            {
                await Task.WhenAny(
                    overlappingHandlersEntered.Task,
                    Task.Delay(contenderTimeout));
                Assert.True(
                    overlappingHandlersEntered.Task.IsCompleted,
                    $"Only {Volatile.Read(ref handlerCalls)} of the first 2 handlers entered.");
            }
            finally
            {
                releaseOverlappingHandlers.TrySetResult(true);
            }

            var results = await Task.WhenAll(contenders).WaitAsync(contenderTimeout);
            Assert.All(results, result => Assert.False(result.IsError ?? false));
            Assert.Equal(8, handlerCalls);
            Assert.Equal(AuditHealthState.Healthy, health.Snapshot().State);
            await runtime.StopAsync(CancellationToken.None);
            runtime.Dispose();
        }
        finally
        {
            start.TrySetResult(true);
            releaseOverlappingHandlers.TrySetResult(true);
        }

        var eventTypes = ReadEvents(options).Select(EventType).ToArray();
        Assert.Equal(1, eventTypes.Count(type => type == "audit.degraded"));
        Assert.Equal(1, eventTypes.Count(type => type == "audit.recovered"));
        Assert.Equal(8, eventTypes.Count(type => type == "call.accepted"));
        Assert.Equal(8, eventTypes.Count(type => type == "call.completed"));
        Assert.Equal("server.stopped", eventTypes[^1]);
    }

    [Fact]
    public async Task Configured_evidence_limit_is_input_rejection_not_a_storage_outage()
    {
        var root = NewRoot();
        const int recordBytes = 8192;
        var options = AuditOptions.Create(
            Path.Combine(root, "audit"),
            maxRecordBytes: recordBytes,
            segmentBytes: recordBytes * 64L,
            aggregateBytes: recordBytes * 64L,
            emergencyReserveBytes: recordBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: 256,
            evidenceAggregateBytes: 1024,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
        var health = new AuditHealth(options);
        using var runtime = CreateRuntime(options, health);
        await runtime.StartAsync(CancellationToken.None);
        using var services = new ServiceCollection()
            .AddSingleton(runtime)
            .AddSingleton(new AuditCallContextAccessor())
            .BuildServiceProvider();
        var handlerCalls = 0;

        var result = await Filter(
            services,
            Call("ptk_invoke", ("script", new string('x', 257))),
            _ =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Text("must not run"));
            });

        Assert.True(result.IsError);
        Assert.Contains("audit_boundary_invalid", ResultText(result));
        Assert.Equal(0, handlerCalls);
        Assert.Equal(AuditHealthState.Healthy, health.Snapshot().State);
        await runtime.StopAsync(CancellationToken.None);
        runtime.Dispose();
        Assert.Equal(
            ["server.started", "server.stopped"],
            ReadEvents(options).Select(EventType));
    }

    [Fact]
    public async Task Failed_runtime_drain_never_emits_a_false_server_stopped()
    {
        var root = NewRoot();
        var options = CreateOptions(Path.Combine(root, "audit"));
        var health = new AuditHealth(options);
        var runtime = CreateRuntime(options, health);
        await runtime.StartAsync(CancellationToken.None);
        var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(5));
        var jobs = new JobManager(Path.Combine(root, "jobs"))
        {
            ShutdownOverrideForTests = () =>
                Task.FromException(new IOException("injected drain failure")),
        };
        var session = runtime.RunSessionAfterStarted(() =>
            new SessionRuntime(host, jobs, new RawUsageCounter()));
        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                runtime.StopAsync(CancellationToken.None));
            Assert.Throws<IOException>(() => runtime.Dispose());

            var eventTypes = ReadEvents(options).Select(EventType).ToArray();
            Assert.Equal(["server.started"], eventTypes);
            Assert.DoesNotContain("server.stopped", eventTypes);
        }
        finally
        {
            jobs.ShutdownOverrideForTests = null;
            try { session.Dispose(); }
            catch (IOException) { /* the intentionally faulted shutdown task is cached */ }
        }
    }

    [Fact]
    public async Task Failed_owned_runspace_cleanup_never_emits_a_false_server_stopped()
    {
        var root = NewRoot();
        var options = CreateOptions(Path.Combine(root, "audit"));
        var health = new AuditHealth(options);
        var runtime = CreateRuntime(options, health);
        await runtime.StartAsync(CancellationToken.None);
        var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(5));
        var jobs = new JobManager(Path.Combine(root, "jobs"));
        var session = runtime.RunSessionAfterStarted(() =>
            new SessionRuntime(host, jobs, new RawUsageCounter()));
        host.TrackOwnedBackgroundWorkForTests(
            Task.FromException(new IOException("injected teardown failure")));

        await Assert.ThrowsAsync<IOException>(() =>
            runtime.StopAsync(CancellationToken.None));
        Assert.Throws<IOException>(() => runtime.Dispose());

        var eventTypes = ReadEvents(options).Select(EventType).ToArray();
        Assert.Equal(["server.started"], eventTypes);
        Assert.DoesNotContain("server.stopped", eventTypes);
        session.Dispose();
    }

    [Fact]
    public async Task Slow_runtime_construction_does_not_block_emergency_state_and_rechecks_health()
    {
        var root = NewRoot();
        var options = CreateOptions(Path.Combine(root, "audit"));
        var health = new AuditHealth(options);
        using var runtime = CreateRuntime(options, health);
        await runtime.StartAsync(CancellationToken.None);
        using var constructionEntered = new ManualResetEventSlim();
        using var releaseConstruction = new ManualResetEventSlim();
        var productDisposed = false;
        var constructing = Task.Run(() => runtime.RunAfterStarted(() =>
        {
            constructionEntered.Set();
            if (!releaseConstruction.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("test did not release construction");
            return new TrackedDisposable(() => productDisposed = true);
        }));
        try
        {
            Assert.True(
                constructionEntered.Wait(TimeSpan.FromSeconds(5)),
                "runtime factory never entered");
            health.MarkUnavailable("evidence.storage");
            using var services = new ServiceCollection()
                .AddSingleton(runtime)
                .AddSingleton(new AuditCallContextAccessor())
                .BuildServiceProvider();
            var handlerCalls = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var emergency = await Filter(
                services,
                Call("ptk_state"),
                _ =>
                {
                    handlerCalls++;
                    return ValueTask.FromResult(Text("must not run"));
                });
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"emergency state took {sw.Elapsed}");
            Assert.Contains("audit=unavailable", ResultText(emergency));
            Assert.Equal(0, handlerCalls);

            releaseConstruction.Set();
            await Assert.ThrowsAsync<AuditUnavailableException>(async () =>
                await constructing.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(productDisposed);

            var recovered = await Filter(
                services,
                Call("ptk_invoke", ("script", "'recover construction test'")),
                _ => ValueTask.FromResult(Text("handled")));
            Assert.False(recovered.IsError ?? false);
            await runtime.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseConstruction.Set();
            try { await constructing.WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* expected fault or primary failure */ }
        }
    }

    private string NewRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-runtime-gate-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        Directory.CreateDirectory(root);
        return root;
    }

    private static AuditOptions CreateOptions(string root)
    {
        const int recordBytes = 8192;
        return AuditOptions.Create(
            root,
            maxRecordBytes: recordBytes,
            segmentBytes: recordBytes * 64L,
            aggregateBytes: recordBytes * 64L,
            emergencyReserveBytes: recordBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: recordBytes,
            evidenceAggregateBytes: recordBytes * 16L,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
    }

    private static AuditRuntimeGate CreateRuntime(AuditOptions options, AuditHealth health) =>
        new(
            options,
            health,
            new ScriptEvidenceStoreProvider(options),
            "runtime-gate-test");

    private static async ValueTask<CallToolResult> Filter(
        IServiceProvider services,
        CallToolRequestParams call,
        Func<CancellationToken, ValueTask<CallToolResult>> next) =>
        await AuditCallFilter.InvokeAsync(
            call,
            new AuditClientContext("runtime-test", "1", "session"),
            services,
            next,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            () => DateTimeOffset.UtcNow,
            CancellationToken.None);

    private static CallToolRequestParams Call(
        string name,
        params (string Name, object? Value)[] arguments)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (argumentName, value) in arguments)
            values.Add(argumentName, JsonSerializer.SerializeToElement(value));
        return new CallToolRequestParams { Name = name, Arguments = values };
    }

    private static CallToolResult Text(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }],
    };

    private static string ResultText(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static List<JsonDocument> ReadEvents(AuditOptions options)
    {
        return Directory
            .EnumerateFiles(options.SpoolDirectory, "*.jsonl")
            .Order(StringComparer.Ordinal)
            .SelectMany(File.ReadLines)
            .Where(line => line.Length > 0)
            .Select(line => JsonDocument.Parse(line))
            .ToList();
    }

    private static string EventType(JsonDocument document) =>
        document.RootElement.GetProperty("event_type").GetString()!;

    private sealed class TrackedDisposable(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    private sealed class BlockingSessionLifetime : ISessionLifetime
    {
        internal int ShutdownCount { get; private set; }

        internal TaskCompletionSource ShutdownEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseShutdown { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ShutdownAsync()
        {
            ShutdownCount++;
            ShutdownEntered.TrySetResult();
            await ReleaseShutdown.Task;
        }

        public void Dispose() => ReleaseShutdown.TrySetResult();
    }

    private sealed class BlockingExportSource(Func<long> readSequence) : IAuditExportStepSource
    {
        private int _disposed;

        internal TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal long SequenceAtStart { get; private set; }

        internal bool ServerStoppedWasDurableAtCancellation { get; private set; }

        internal bool Disposed => Volatile.Read(ref _disposed) != 0;

        public async Task<AuditExportCoordinatorStep> ExportNextAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                SequenceAtStart = readSequence();
            }
            finally
            {
                Started.TrySetResult();
            }
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ServerStoppedWasDurableAtCancellation =
                    readSequence() >= 2;
                throw;
            }
            throw new InvalidOperationException("The blocking export source resumed unexpectedly.");
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class ThrowingExportSource : IAuditExportStepSource
    {
        internal TaskCompletionSource Faulted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AuditExportCoordinatorStep> ExportNextAsync(
            CancellationToken cancellationToken)
        {
            Faulted.TrySetResult();
            throw new IOException("injected exporter loop failure");
        }

        public void Dispose()
        {
        }
    }
}
