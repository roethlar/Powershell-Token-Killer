using System.Diagnostics;
using PtkMcpGuardian.Ownership;
using PtkMcpServer.Sessions;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class SessionOperationAuthorityTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly HostGeneration Generation = new(7);
    private static readonly CanonicalAlias Alias = new("default");
    private static readonly SessionTransitionVersion Transition = new(1);
    private static readonly GuardianHostWorkerIdentity Worker = new(
        new WorkerBootId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")),
        new WorkerGeneration(1));
    private static readonly GuardianHostOperationIdentity OperationIdentity = new(
        new PlanId(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd")),
        new OperationId(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")));
    private static readonly CallId Call = new(
        Guid.Parse("ffffffff-ffff-7fff-8fff-ffffffffffff"));

    [Fact]
    public void Authority_retains_the_exact_wire_deadline_and_host_created_background_identity()
    {
        var deadline = DateTimeOffset.UtcNow
            .AddMinutes(2)
            .ToUnixTimeMilliseconds();
        var publicJobId = new PublicJobId(71);
        var operation = BackgroundOperation(
            "'authority'",
            deadline,
            publicJobId);
        var request = Request(operation, deadline);
        // The capability is created inside the trusted host adapter. It is not
        // a field on InvokeBackgroundOperation's wire request.
        var hostCreatedCapability = Capability(9);

        var authority = SessionOperationAuthority.CreateBackgroundInvoke(
            request,
            hostCreatedCapability);

        Assert.Equal(
            deadline,
            authority.AbsoluteDeadlineUnixTimeMilliseconds);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(deadline),
            authority.AbsoluteDeadlineUtc);
        Assert.Same(operation, authority.RequireOperation(operation));
        Assert.Same(publicJobId, authority.BackgroundJob!.PublicJobId);
        Assert.Same(
            hostCreatedCapability,
            authority.BackgroundJob.JobCapability);
        Assert.Throws<ArgumentException>(() =>
            SessionOperationAuthority.Create(request));

        var foreground = ForegroundOperation("'other'", deadline);
        var foregroundRequest = Request(foreground, deadline);
        Assert.Throws<ArgumentException>(() =>
            SessionOperationAuthority.CreateBackgroundInvoke(
                foregroundRequest,
                hostCreatedCapability));
        Assert.Throws<ArgumentException>(() =>
            authority.RequireOperation(foreground));
    }

    [Fact]
    public async Task Long_max_wire_deadline_is_valid_and_does_not_block_execution()
    {
        using var fixture = new RuntimeFixture();
        var operation = ForegroundOperation(
            "'far-future-deadline'",
            long.MaxValue);
        var authority = SessionOperationAuthority.Create(
            Request(operation, long.MaxValue));

        Assert.Equal(
            long.MaxValue,
            authority.AbsoluteDeadlineUnixTimeMilliseconds);
        Assert.Equal(DateTimeOffset.MaxValue, authority.AbsoluteDeadlineUtc);

        var response = await fixture.Runtime.InvokeAsync(
            authority,
            operation,
            CancellationToken.None,
            timeoutSeconds: int.MaxValue);

        Assert.Contains(
            "far-future-deadline",
            response,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_wire_deadline_with_huge_timeout_never_starts_foreground_script()
    {
        using var fixture = new RuntimeFixture();
        var marker = Path.Combine(fixture.Root, "foreground-ran.txt");
        var deadline = DateTimeOffset.UtcNow
            .AddSeconds(-5)
            .ToUnixTimeMilliseconds();
        var operation = ForegroundOperation(
            $"[IO.File]::WriteAllText({Literal(marker)}, 'ran')",
            deadline);
        var authority = SessionOperationAuthority.Create(
            Request(operation, deadline));

        var response = await fixture.Runtime.InvokeAsync(
            authority,
            operation,
            CancellationToken.None,
            timeoutSeconds: int.MaxValue);

        Assert.Contains(
            "NOT executed",
            response,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Expired_wire_deadline_with_huge_timeout_never_probes_or_starts_background_work()
    {
        using var fixture = new RuntimeFixture();
        var marker = Path.Combine(fixture.Root, "background-ran.txt");
        var probes = 0;
        var processStarts = 0;
        fixture.Host.CurrentLocationReaderOverrideForTests = () =>
        {
            Interlocked.Increment(ref probes);
            return fixture.Root;
        };
        fixture.Jobs.ProcessStartOverrideForTests = _ =>
        {
            Interlocked.Increment(ref processStarts);
            return false;
        };
        var deadline = DateTimeOffset.UtcNow
            .AddSeconds(-5)
            .ToUnixTimeMilliseconds();
        var operation = BackgroundOperation(
            $"[IO.File]::WriteAllText({Literal(marker)}, 'ran')",
            deadline,
            new PublicJobId(72),
            GuardianHostInvokeRoute.Pwsh);
        var authority = SessionOperationAuthority.CreateBackgroundInvoke(
            Request(operation, deadline),
            Capability(10));

        var response = await fixture.Runtime.InvokeAsync(
            authority,
            operation,
            CancellationToken.None,
            timeoutSeconds: int.MaxValue);

        Assert.Contains("[job not started]", response, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref probes));
        Assert.Equal(0, Volatile.Read(ref processStarts));
        Assert.Equal(0, fixture.Allocator.AllocationAttempts);
        Assert.Empty(fixture.Jobs.List());
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Wire_deadline_bounds_cold_planning_even_when_timeout_is_huge()
    {
        using var fixture = new RuntimeFixture();
        fixture.Host.CurrentLocationReaderOverrideForTests = () => fixture.Root;
        fixture.Host.PreflightDelayForTests = TimeSpan.FromSeconds(2);
        var processStarts = 0;
        fixture.Jobs.ProcessStartOverrideForTests = _ =>
        {
            Interlocked.Increment(ref processStarts);
            return false;
        };
        var deadline = DateTimeOffset.UtcNow
            .AddMilliseconds(500)
            .ToUnixTimeMilliseconds();
        var operation = BackgroundOperation(
            "'planning deadline'",
            deadline,
            new PublicJobId(73));
        var authority = SessionOperationAuthority.CreateBackgroundInvoke(
            Request(operation, deadline),
            Capability(11));
        var elapsed = Stopwatch.StartNew();

        var response = await fixture.Runtime.InvokeAsync(
            authority,
            operation,
            CancellationToken.None,
            timeoutSeconds: int.MaxValue);

        elapsed.Stop();
        Assert.Contains("[job not started]", response, StringComparison.Ordinal);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromMilliseconds(1500),
            $"Cold planning ignored the wire deadline: {elapsed.Elapsed}.");
        Assert.Equal(0, Volatile.Read(ref processStarts));
        Assert.Equal(0, fixture.Allocator.AllocationAttempts);
        Assert.Empty(fixture.Jobs.List());
    }

    [Fact]
    public async Task Wire_deadline_is_rechecked_at_the_final_process_start_gate()
    {
        using var fixture = new RuntimeFixture();
        fixture.Host.CurrentLocationReaderOverrideForTests = () => fixture.Root;
        var beforeStart = 0;
        var processStarts = 0;
        fixture.Jobs.BeforeProcessStartForTests = _ =>
        {
            Interlocked.Increment(ref beforeStart);
            Thread.Sleep(TimeSpan.FromMilliseconds(2500));
        };
        fixture.Jobs.ProcessStartOverrideForTests = _ =>
        {
            Interlocked.Increment(ref processStarts);
            return false;
        };
        var deadline = DateTimeOffset.UtcNow
            .AddSeconds(2)
            .ToUnixTimeMilliseconds();
        var operation = BackgroundOperation(
            "'final gate'",
            deadline,
            new PublicJobId(74),
            GuardianHostInvokeRoute.Pwsh);
        var authority = SessionOperationAuthority.CreateBackgroundInvoke(
            Request(operation, deadline),
            Capability(12));

        var response = await fixture.Runtime.InvokeAsync(
            authority,
            operation,
            CancellationToken.None,
            timeoutSeconds: int.MaxValue);

        Assert.Contains("[job not started]", response, StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref beforeStart));
        Assert.Equal(0, Volatile.Read(ref processStarts));
        Assert.Equal(0, fixture.Allocator.AllocationAttempts);
        Assert.Empty(fixture.Jobs.List());
    }

    [Fact]
    public async Task Reserved_wire_id_and_host_created_capability_reach_the_started_job_exactly()
    {
        using var fixture = new RuntimeFixture();
        fixture.Host.CurrentLocationReaderOverrideForTests = () => fixture.Root;
        JobStartPlan? observedPlan = null;
        fixture.Jobs.BeforeProcessStartForTests = plan => observedPlan = plan;
        var deadline = FutureDeadline();
        var publicJobId = new PublicJobId(75);
        var hostCreatedCapability = Capability(13);
        var operation = BackgroundOperation(
            "'identity round trip'",
            deadline,
            publicJobId,
            GuardianHostInvokeRoute.Pwsh);
        var authority = SessionOperationAuthority.CreateBackgroundInvoke(
            Request(operation, deadline),
            hostCreatedCapability);

        var response = await fixture.Runtime.InvokeAsync(
            authority,
            operation,
            CancellationToken.None,
            timeoutSeconds: int.MaxValue);

        Assert.Contains("[job 75 started]", response, StringComparison.Ordinal);
        Assert.NotNull(observedPlan);
        Assert.Equal(publicJobId.Value, observedPlan.Id);
        Assert.Same(hostCreatedCapability, observedPlan.JobCapability);
        Assert.Equal(0, fixture.Allocator.AllocationAttempts);
        var completed = await WaitForTerminalAsync(
            fixture.Jobs,
            publicJobId,
            hostCreatedCapability);
        Assert.False(completed.Running);
        Assert.Equal(publicJobId.Value, completed.Id);
        Assert.Throws<JobCapabilityException>(() =>
            fixture.Jobs.Snapshot(publicJobId, Capability(14)));
    }

    [Fact]
    public async Task Wrong_or_expired_job_authority_is_rejected_before_output_or_kill_effects()
    {
        using var fixture = new RuntimeFixture();
        fixture.Host.CurrentLocationReaderOverrideForTests = () => fixture.Root;
        var deadline = FutureDeadline();
        var publicJobId = new PublicJobId(76);
        var jobCapability = Capability(15);
        var start = BackgroundOperation(
            "Start-Sleep -Seconds 300",
            deadline,
            publicJobId,
            GuardianHostInvokeRoute.Pwsh);
        var startAuthority = SessionOperationAuthority.CreateBackgroundInvoke(
            Request(start, deadline),
            jobCapability);
        var response = await fixture.Runtime.InvokeAsync(
            startAuthority,
            start,
            CancellationToken.None,
            timeoutSeconds: int.MaxValue);
        Assert.Contains("[job 76 started]", response, StringComparison.Ordinal);

        var outputReads = 0;
        var killRequests = 0;
        fixture.Jobs.BeforePollingOutputReadForTests = _ =>
            Interlocked.Increment(ref outputReads);
        fixture.Jobs.BeforeKillForTests = _ =>
            Interlocked.Increment(ref killRequests);
        try
        {
            var wrongCapability = Capability(16);
            var controlDeadline = FutureDeadline();
            var status = new JobStatusOperation(
                Call,
                Dispatch(controlDeadline),
                publicJobId,
                wrongCapability);
            var statusAuthority = SessionOperationAuthority.Create(
                Request(status, controlDeadline));
            await Assert.ThrowsAsync<JobCapabilityException>(() =>
                fixture.Runtime.JobAsync(
                    statusAuthority,
                    status,
                    CancellationToken.None));

            var output = new JobOutputOperation(
                Call,
                Dispatch(controlDeadline),
                Output(controlDeadline),
                publicJobId,
                wrongCapability,
                offset: 0);
            var outputAuthority = SessionOperationAuthority.Create(
                Request(output, controlDeadline));
            await Assert.ThrowsAsync<JobCapabilityException>(() =>
                fixture.Runtime.JobAsync(
                    outputAuthority,
                    output,
                    CancellationToken.None));

            var kill = new JobKillOperation(
                Call,
                Dispatch(controlDeadline),
                publicJobId,
                wrongCapability);
            var killAuthority = SessionOperationAuthority.Create(
                Request(kill, controlDeadline));
            await Assert.ThrowsAsync<JobCapabilityException>(() =>
                fixture.Runtime.JobAsync(
                    killAuthority,
                    kill,
                    CancellationToken.None));

            var expiredDeadline = DateTimeOffset.UtcNow
                .AddSeconds(-2)
                .ToUnixTimeMilliseconds();
            var expiredOutput = new JobOutputOperation(
                Call,
                Dispatch(expiredDeadline),
                Output(expiredDeadline),
                publicJobId,
                jobCapability,
                offset: 0);
            var expiredOutputAuthority = SessionOperationAuthority.Create(
                Request(expiredOutput, expiredDeadline));
            var deadlineFailure = await Assert.ThrowsAsync<
                SessionOperationDeadlineException>(() =>
                    fixture.Runtime.JobAsync(
                        expiredOutputAuthority,
                        expiredOutput,
                        CancellationToken.None));
            Assert.Equal(
                GuardianHostPrivateDetailCode.RequestDeadlineExpired,
                deadlineFailure.DetailCode);

            var expiredKill = new JobKillOperation(
                Call,
                Dispatch(expiredDeadline),
                publicJobId,
                jobCapability);
            var expiredKillAuthority = SessionOperationAuthority.Create(
                Request(expiredKill, expiredDeadline));
            await Assert.ThrowsAsync<SessionOperationDeadlineException>(() =>
                fixture.Runtime.JobAsync(
                    expiredKillAuthority,
                    expiredKill,
                    CancellationToken.None));

            var expiredList = new JobListOperation(
                Call,
                Dispatch(expiredDeadline));
            var expiredListAuthority = SessionOperationAuthority.Create(
                Request(expiredList, expiredDeadline));
            await Assert.ThrowsAsync<SessionOperationDeadlineException>(() =>
                fixture.Runtime.JobAsync(
                    expiredListAuthority,
                    expiredList,
                    CancellationToken.None));

            Assert.Equal(0, Volatile.Read(ref outputReads));
            Assert.Equal(0, Volatile.Read(ref killRequests));
            Assert.True(
                fixture.Jobs.Snapshot(publicJobId, jobCapability).Running);
        }
        finally
        {
            fixture.Jobs.BeforePollingOutputReadForTests = null;
            fixture.Jobs.BeforeKillForTests = null;
            fixture.Jobs.RequestKill(publicJobId, jobCapability);
            await WaitForTerminalAsync(
                fixture.Jobs,
                publicJobId,
                jobCapability);
        }
    }

    [Fact]
    public async Task Job_output_shaping_inherits_the_exact_wire_deadline()
    {
        using var fixture = new RuntimeFixture(
            callTimeout: TimeSpan.FromSeconds(10));
        Assert.True(fixture.Host.ModuleLoaded);
        fixture.Host.CurrentLocationReaderOverrideForTests = () => fixture.Root;
        var deadline = FutureDeadline();
        var publicJobId = new PublicJobId(77);
        var jobCapability = Capability(17);
        var start = BackgroundOperation(
            "'shape-deadline-text'",
            deadline,
            publicJobId,
            GuardianHostInvokeRoute.Pwsh);
        var startAuthority = SessionOperationAuthority.CreateBackgroundInvoke(
            Request(start, deadline),
            jobCapability);
        var response = await fixture.Runtime.InvokeAsync(
            startAuthority,
            start,
            CancellationToken.None);
        Assert.Contains("[job 77 started]", response, StringComparison.Ordinal);
        await WaitForTerminalAsync(
            fixture.Jobs,
            publicJobId,
            jobCapability);

        var blocker = fixture.Host.InvokeAsync(
            "Start-Sleep -Milliseconds 2500",
            route: "pwsh");
        await WaitForBusyAsync(fixture.Host);
        var outputDeadline = DateTimeOffset.UtcNow
            .AddMilliseconds(500)
            .ToUnixTimeMilliseconds();
        var output = new JobOutputOperation(
            Call,
            Dispatch(outputDeadline),
            Output(outputDeadline),
            publicJobId,
            jobCapability,
            offset: 0);
        var authority = SessionOperationAuthority.Create(
            Request(output, outputDeadline));
        var elapsed = Stopwatch.StartNew();

        var text = await fixture.Runtime.JobAsync(
            authority,
            output,
            CancellationToken.None);

        elapsed.Stop();
        Assert.Contains("shape-deadline-text", text, StringComparison.Ordinal);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromMilliseconds(1500),
            $"Job output shaping ignored the wire deadline: {elapsed.Elapsed}.");
        await blocker;
    }

    private static InvokeForegroundOperation ForegroundOperation(
        string script,
        long deadline) =>
        new(
            Call,
            Dispatch(deadline),
            Output(deadline),
            script,
            raw: false,
            GuardianHostInvokeRoute.Pwsh);

    private static InvokeBackgroundOperation BackgroundOperation(
        string script,
        long deadline,
        PublicJobId publicJobId,
        GuardianHostInvokeRoute route = GuardianHostInvokeRoute.Auto) =>
        new(
            Call,
            Dispatch(deadline),
            Output(deadline),
            script,
            raw: false,
            route,
            publicJobId);

    private static OperationRequest Request(
        GuardianHostOperation operation,
        long deadline) =>
        new(
            Guardian,
            Host,
            Generation,
            new PrivateRequestId(1),
            deadline,
            Alias,
            Transition,
            Worker,
            operation.Kind is GuardianHostOperationKind.InvokeForeground or
                GuardianHostOperationKind.InvokeBackground
                ? OperationIdentity
                : null,
            operation);

    private static DispatchCapability Dispatch(long deadline) =>
        new(Capability(1), Call, deadline);

    private static OutputCapability Output(long deadline) =>
        new(Capability(2), maximumBytes: 4096, deadline);

    private static CapabilityToken Capability(byte value)
    {
        var bytes = Enumerable.Repeat(
            value,
            ContractLimits.CapabilityTokenBytes).ToArray();
        return new CapabilityToken(Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'));
    }

    private static long FutureDeadline() =>
        DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds();

    private static string Literal(string text) =>
        "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async Task<JobSnapshot> WaitForTerminalAsync(
        JobManager jobs,
        PublicJobId publicJobId,
        CapabilityToken jobCapability)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var snapshot = jobs.Snapshot(publicJobId, jobCapability);
            if (!snapshot.Running) return snapshot;
            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Job {publicJobId.Value} did not reach a terminal state.");
    }

    private static async Task WaitForBusyAsync(RunspaceHost host)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (host.GetGateStatus().Busy) return;
            await Task.Delay(10);
        }

        throw new TimeoutException("The foreground runspace call never became busy.");
    }

    private sealed class RuntimeFixture : IDisposable
    {
        internal RuntimeFixture(TimeSpan? callTimeout = null)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "ptk-session-authority-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Allocator = new RejectingPublicJobIdAllocator();
            Host = new RunspaceHost(
                callTimeout: callTimeout ?? TimeSpan.FromSeconds(60));
            Jobs = new JobManager(
                Allocator,
                JobPwshExecutable.ResolveFromPath(),
                Root);
            Runtime = new SessionRuntime(Host, Jobs, new RawUsageCounter());
        }

        internal string Root { get; }

        internal RejectingPublicJobIdAllocator Allocator { get; }

        internal RunspaceHost Host { get; }

        internal JobManager Jobs { get; }

        internal SessionRuntime Runtime { get; }

        public void Dispose()
        {
            Runtime.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private sealed class RejectingPublicJobIdAllocator : IPublicJobIdAllocator
    {
        internal int AllocationAttempts { get; private set; }

        public PublicJobId Allocate()
        {
            AllocationAttempts++;
            throw new InvalidOperationException(
                "The private host must not allocate a guardian-owned public job ID.");
        }
    }
}
