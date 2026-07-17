using System.Collections.Immutable;
using PtkMcpGuardian.Ownership;
using PtkMcpServer.Sessions;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

// ProcessEnvironment collection: the execution-policy test mutates
// PSExecutionPolicyPreference, which job children inherit at spawn.
[Collection("ProcessEnvironment")]
public sealed class JobManagerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ptk-job-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _fixtureDir =
        Path.Combine(Path.GetTempPath(), "ptk-job-fixture-" + Guid.NewGuid().ToString("N"));
    private readonly JobManager _jobs;
    private readonly string _rtkFixturePath;
    private readonly List<string> _outputRoots = [];

    public JobManagerTests()
    {
        _jobs = new JobManager(_dir);
        _rtkFixturePath = RtkTestStub.CreatePassthrough(_fixtureDir).Path;
    }

    public void Dispose()
    {
        _jobs.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* logs may lag a beat */ }
        try { Directory.Delete(_fixtureDir, recursive: true); } catch { }
        foreach (var root in _outputRoots)
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private async Task<JobSnapshot> WaitForExitAsync(long id, int timeoutSeconds = 60)
        => await WaitForExitAsync(_jobs, id, timeoutSeconds);

    private static async Task<JobSnapshot> WaitForExitAsync(
        JobManager jobs,
        long id,
        int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var snapshot = jobs.Snapshot(id)!;
            if (!snapshot.Running) return snapshot;
            Assert.True(DateTime.UtcNow < deadline, $"job {id} did not exit within {timeoutSeconds}s");
            await Task.Delay(200);
        }
    }

    private OutputStore CreateOutputStore(
        string scenario,
        Action<string>? artifactCreateStartingForTests = null,
        bool singleReservationCapacity = false,
        Action? reservationStartingForTests = null,
        long maximumArtifactBytes = OutputStore.MaximumReadBytes)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "job-manager-output-tests",
            $"{scenario}-{Guid.NewGuid():N}");
        _outputRoots.Add(root);
        return new OutputStore(new OutputStoreOptions(
            root,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(1),
            MaximumArtifactBytes: maximumArtifactBytes,
            MaximumSessionBytes: maximumArtifactBytes *
                (singleReservationCapacity ? 1L : 2L),
            MaximumAggregateBytes: maximumArtifactBytes *
                (singleReservationCapacity ? 1L : 4L),
            ArtifactCreateStartingForTests: artifactCreateStartingForTests,
            ReservationStartingForTests: reservationStartingForTests));
    }

    [Fact]
    public void PrepareStart_allocates_an_id_without_files_or_process_admission()
    {
        var plan = _jobs.PrepareStart("'prepared only'", Path.GetTempPath());

        Assert.Equal(1L, plan.Id);
        Assert.Empty(_jobs.List());
        Assert.False(Directory.Exists(_dir));
        Assert.False(File.Exists(plan.OutputPath));
    }

    [Fact]
    public void Shared_guardian_allocator_never_reuses_abandoned_or_failed_start_ids()
    {
        IPublicJobIdAllocator allocator = new MonotonicPublicJobIdAllocator();
        using var first = new JobManager(
            allocator,
            new JobPwshExecutable(AbsolutePath: null),
            Path.Combine(_dir, "shared-id-first"));
        using var second = new JobManager(
            allocator,
            new JobPwshExecutable(AbsolutePath: null),
            Path.Combine(_dir, "shared-id-second"));

        var abandoned = first.PrepareStart("'abandoned'", Path.GetTempPath());
        var failed = second.PrepareStart("'fails before start'", Path.GetTempPath());
        var failure = Assert.Throws<JobStartException>(() => second.CommitStart(failed));
        var next = first.PrepareStart("'next'", Path.GetTempPath());

        Assert.Equal(1, abandoned.Id);
        Assert.Equal(2, failed.Id);
        Assert.False(failure.ProcessStarted);
        Assert.Equal(3, next.Id);
        Assert.Null(first.Snapshot(abandoned.Id));
        Assert.Null(first.Snapshot(failed.Id));
        Assert.Null(second.Snapshot(abandoned.Id));
        Assert.Null(second.Snapshot(failed.Id));
    }

    [Fact]
    public void Guardian_reserved_job_id_is_bound_without_host_allocation()
    {
        var allocator = new RejectingPublicJobIdAllocator();
        var root = Path.Combine(_dir, "guardian-reserved");
        using var jobs = new JobManager(
            allocator,
            new JobPwshExecutable(AbsolutePath: null),
            root);
        var cwd = Path.GetFullPath(_fixtureDir);
        Directory.CreateDirectory(cwd);
        var dispatch = CreateRtkDispatch(
            cwd,
            DirectCommand("echo guardian-reserved"),
            originalScript: "guardian-reserved intent");

        var plan = jobs.PrepareStartWithReservedId(
            new PublicJobId(41),
            dispatch,
            cwd);

        Assert.Equal(41, plan.Id);
        Assert.Same(dispatch, plan.Dispatch);
        Assert.True(plan.DispatchBound);
        Assert.Equal(0, allocator.AllocationAttempts);
        Assert.Empty(jobs.List());
        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(plan.OutputPath));
    }

    [Fact]
    public void Typed_cold_dispatch_preparation_is_effect_free_and_preserves_one_metadata_object()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var dispatch = CreateRtkDispatch(
            cwd,
            DirectCommand("echo TYPED_PREPARE"),
            originalScript: "fixture command");
        var reservation = _jobs.PrepareStart("fixture command");

        var plan = _jobs.BindDispatch(reservation, dispatch, cwd);

        Assert.Equal(1L, plan.Id);
        Assert.Equal(reservation.Id, plan.Id);
        Assert.Equal(reservation.Generation, plan.Generation);
        Assert.Equal(reservation.OutputPath, plan.OutputPath);
        Assert.Same(dispatch, plan.Dispatch);
        Assert.Same(dispatch, plan.Execution.Dispatch);
        Assert.True(plan.DispatchBound);
        Assert.Equal(ExecutionPath.Rtk, plan.Execution.ExecutionPath);
        Assert.Equal(OutputProvenance.RtkUnknown, plan.Execution.OutputProvenance);
        Assert.Equal(ResolutionContext.Cold, plan.Execution.ResolutionContext);
        Assert.Null(plan.EncodedCommand);
        Assert.Empty(_jobs.List());
        Assert.False(Directory.Exists(_dir));
        Assert.False(File.Exists(plan.OutputPath));
        Assert.Equal(2L, _jobs.PrepareStart("next intent").Id);
    }

    [Fact]
    public void Cold_background_policy_cannot_be_bypassed_through_direct_commit()
    {
        var disabledRoot = Path.Combine(_dir, "disabled");
        var processStarts = 0;
        using var jobs = new JobManager(
            JobPwshExecutable.ResolveFromPath(),
            disabledRoot,
            allowColdBackground: false)
        {
            BeforeProcessStartForTests = _ => Interlocked.Increment(ref processStarts),
        };
        var plan = jobs.PrepareStart("'must not run'", Path.GetTempPath());

        JobSnapshot? unexpectedStart = null;
        var exception = Record.Exception(() => unexpectedStart = jobs.CommitStart(plan));
        if (unexpectedStart is not null)
            jobs.ConfirmStartRecorded(unexpectedStart.Id);
        var error = Assert.IsType<InvalidOperationException>(exception);

        Assert.Contains("cold background", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, processStarts);
        Assert.Empty(jobs.List());
        Assert.False(Directory.Exists(disabledRoot));
        Assert.False(File.Exists(plan.OutputPath));
    }

    [Fact]
    public void Cold_background_policy_blocks_typed_rtk_before_spool_or_process()
    {
        var disabledRoot = Path.Combine(_dir, "disabled-rtk");
        var processStarts = 0;
        using var jobs = new JobManager(
            JobPwshExecutable.ResolveFromPath(),
            disabledRoot,
            allowColdBackground: false)
        {
            BeforeProcessStartForTests = _ => Interlocked.Increment(ref processStarts),
        };
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var dispatch = CreateRtkDispatch(cwd, DirectCommand("echo MUST_NOT_RUN"));
        var plan = jobs.PrepareStart(dispatch, cwd);

        var error = Assert.Throws<InvalidOperationException>(() => jobs.CommitStart(plan));

        Assert.Contains("cold background", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, processStarts);
        Assert.Empty(jobs.List());
        Assert.False(Directory.Exists(disabledRoot));
        Assert.False(File.Exists(plan.OutputPath));
    }

    [Fact]
    public async Task Job_uses_the_startup_pinned_pwsh_while_inheriting_live_environment()
    {
        var frozen = JobPwshExecutable.ResolveFromPath();
        Assert.NotNull(frozen.AbsolutePath);
        Assert.True(Path.IsPathFullyQualified(frozen.AbsolutePath));

        var shimDir = Path.Combine(_dir, "path-shim");
        var markerPath = Path.Combine(shimDir, "shim-launched.txt");
        Directory.CreateDirectory(shimDir);
        CreatePwshShim(shimDir, markerPath);

        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldProbe = Environment.GetEnvironmentVariable("PTK_JOB_LIVE_ENV_TEST");
        using var jobs = new JobManager(frozen, Path.Combine(_dir, "pinned-jobs"));
        try
        {
            // If CommitStart re-resolves by name, this directory wins. The
            // intended absolute executable must still launch, while the child
            // sees environment changes made after manager construction.
            Environment.SetEnvironmentVariable("PATH", shimDir);
            Environment.SetEnvironmentVariable("PTK_JOB_LIVE_ENV_TEST", "changed-after-construction");

            var job = jobs.Start("'LIVE=' + $env:PTK_JOB_LIVE_ENV_TEST");
            var final = await WaitForExitAsync(jobs, job.Id);
            var output = jobs.ReadOutput(job.Id, 0)!.Value.Text;

            Assert.Equal(0, final.ExitCode);
            Assert.Contains("LIVE=changed-after-construction", output);
            Assert.False(File.Exists(markerPath), "the PATH shim was launched instead of the pinned pwsh");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("PTK_JOB_LIVE_ENV_TEST", oldProbe);
        }
    }

    [Fact]
    public void Missing_startup_pwsh_is_not_re_resolved_from_a_later_PATH()
    {
        var available = JobPwshExecutable.ResolveFromPath();
        Assert.NotNull(available.AbsolutePath);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        using var jobs = new JobManager(
            new JobPwshExecutable(null),
            Path.Combine(_dir, "unavailable-jobs"));
        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(available.AbsolutePath));

            var plan = jobs.PrepareStart("'must not run'");
            var error = Assert.Throws<JobStartException>(() => jobs.CommitStart(plan));

            Assert.Contains("server-start PATH", error.Message);
            Assert.False(error.ProcessStarted);
            Assert.Null(error.ProvenPreStartFallbackReason);
            Assert.Empty(jobs.List());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }

    [Fact]
    public void Resolver_skips_a_non_executable_unix_PATH_candidate()
    {
        if (OperatingSystem.IsWindows()) return;

        var expected = JobPwshExecutable.ResolveFromPath();
        Assert.NotNull(expected.AbsolutePath);
        var deadDir = Path.Combine(_dir, "non-executable-path-entry");
        Directory.CreateDirectory(deadDir);
        var deadCandidate = Path.Combine(deadDir, "pwsh");
        File.WriteAllText(deadCandidate, "#!/bin/sh\nexit 91\n");
        File.SetUnixFileMode(deadCandidate, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable(
                "PATH",
                deadDir + Path.PathSeparator + Path.GetDirectoryName(expected.AbsolutePath));

            var resolved = JobPwshExecutable.ResolveFromPath();

            Assert.Equal(expected.AbsolutePath, resolved.AbsolutePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }

    [Fact]
    public void Resolver_expands_windows_environment_variables_in_PATH_entries()
    {
        if (!OperatingSystem.IsWindows()) return;

        var expected = JobPwshExecutable.ResolveFromPath();
        Assert.NotNull(expected.AbsolutePath);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldProbe = Environment.GetEnvironmentVariable("PTK_JOB_PWSH_HOME_TEST");
        try
        {
            Environment.SetEnvironmentVariable(
                "PTK_JOB_PWSH_HOME_TEST",
                Path.GetDirectoryName(expected.AbsolutePath));
            Environment.SetEnvironmentVariable("PATH", "%PTK_JOB_PWSH_HOME_TEST%");

            var resolved = JobPwshExecutable.ResolveFromPath();

            Assert.Equal(expected.AbsolutePath, resolved.AbsolutePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("PTK_JOB_PWSH_HOME_TEST", oldProbe);
        }
    }

    private static void CreatePwshShim(string shimDir, string markerPath)
    {
        if (OperatingSystem.IsWindows())
        {
            // CreateProcess resolves extensionless names to .exe. A copied
            // cmd.exe is a valid executable but cannot run the encoded
            // PowerShell wrapper, so the assertions above fail if it starts.
            var commandProcessor = Environment.GetEnvironmentVariable("ComSpec")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            File.Copy(commandProcessor, Path.Combine(shimDir, "pwsh.exe"));

            // Also leave a marker-producing PATHEXT shim for runtimes that do
            // consult PATHEXT during executable resolution.
            File.WriteAllText(
                Path.Combine(shimDir, "pwsh.cmd"),
                $"@echo shim>{markerPath}\r\n@exit /b 91\r\n");
            return;
        }

        var shimPath = Path.Combine(shimDir, "pwsh");
        File.WriteAllText(
            shimPath,
            $"#!/bin/sh\nprintf shim > '{markerPath.Replace("'", "'\\''")}'\nexit 91\n");
        File.SetUnixFileMode(
            shimPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Fast_exit_terminal_waits_for_the_durable_start_confirmation()
    {
        var terminal = new TaskCompletionSource<JobSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = _jobs.PrepareStart("exit 0");
        var started = _jobs.CommitStart(plan, snapshot =>
        {
            terminal.TrySetResult(snapshot);
            return Task.CompletedTask;
        });

        await WaitForExitAsync(started.Id);
        Assert.False(terminal.Task.IsCompleted);

        Assert.True(_jobs.ConfirmStartRecorded(started.Id));
        var published = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(started.Id, published.Id);
        Assert.False(published.Running);
        Assert.False(_jobs.ConfirmStartRecorded(started.Id));
    }

    [Fact]
    public async Task Direct_job_seals_one_stable_recovery_handle_before_terminal_callback()
    {
        using var store = CreateOutputStore("terminal");
        const string marker = "DIRECT_JOB_RECOVERY_MARKER";
        var terminal = new TaskCompletionSource<JobSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? callbackHandle = null;
        string? callbackArtifact = null;
        var terminalCalls = 0;
        var plan = _jobs.PrepareStart($"'{marker}'", Path.GetTempPath());

        var started = _jobs.CommitStart(
            plan,
            snapshot =>
            {
                try
                {
                    Assert.Equal(1, Interlocked.Increment(ref terminalCalls));
                    Assert.True(snapshot.OutputRecoveryFinalized);
                    var recovery = Assert.IsType<OutputRecoverySummary>(snapshot.OutputRecovery);
                    Assert.True(recovery.Advertise);
                    Assert.Equal(OutputArtifactState.Available, recovery.State);
                    callbackHandle = Assert.IsType<string>(recovery.Handle);

                    var read = store.Read(
                        callbackHandle,
                        offset: 0,
                        maximumBytes: OutputStore.MaximumReadBytes);
                    Assert.Equal(OutputArtifactState.Available, read.State);
                    Assert.True(read.Complete);
                    Assert.Equal(OutputProvenance.DirectText, read.Provenance);
                    Assert.Contains(marker, read.Text, StringComparison.Ordinal);
                    callbackArtifact = read.Text;
                    terminal.TrySetResult(snapshot);
                }
                catch (Exception exception)
                {
                    terminal.TrySetException(exception);
                }
                return Task.CompletedTask;
            },
            outputStore: store);
        Assert.True(_jobs.ConfirmStartRecorded(started.Id));

        var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, terminalCalls);
        Assert.NotNull(callbackHandle);
        Assert.NotNull(callbackArtifact);
        Assert.Equal(callbackHandle, final.OutputRecovery!.Handle);
        Assert.True(File.Exists(final.OutputPath));
        var recoveryStatus = SessionRuntime.RecoveryStatus(final);
        Assert.Contains($"ptk_output handle={callbackHandle}", recoveryStatus, StringComparison.Ordinal);
        Assert.Contains("recovery=handle", recoveryStatus, StringComparison.Ordinal);
        Assert.Contains("ptk_output reports current availability", recoveryStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("recovery=available", recoveryStatus, StringComparison.Ordinal);
        Assert.DoesNotContain(final.OutputPath, recoveryStatus, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(final.OutputPath, "MUTATED_INTERNAL_JOB_SPOOL");
        File.Delete(final.OutputPath);

        var recoveredAfterSpoolRemoval = store.Read(
            callbackHandle,
            offset: 0,
            maximumBytes: OutputStore.MaximumReadBytes);
        Assert.Equal(callbackArtifact, recoveredAfterSpoolRemoval.Text);
        Assert.DoesNotContain(
            "MUTATED_INTERNAL_JOB_SPOOL",
            recoveredAfterSpoolRemoval.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_start_core_consumes_an_interface_only_capture_owner()
    {
        var owner = new BusyOutputCaptureOwner();
        var plan = _jobs.PrepareStart("'interface-only capture owner'", Path.GetTempPath());

        var started = _jobs.CommitStartCore(
            plan,
            onTerminal: null,
            deadline: null,
            CancellationToken.None,
            owner);
        Assert.True(_jobs.ConfirmStartRecorded(started.Id));

        var final = await WaitForExitAsync(started.Id);
        var recovery = Assert.IsType<OutputRecoverySummary>(final.OutputRecovery);
        Assert.True(final.OutputRecoveryFinalized);
        Assert.Equal("output_store_busy", recovery.DetailCode);
        Assert.True(recovery.Advertise);
        Assert.Equal(1, owner.TryStartCalls);
        Assert.Equal(0, owner.TryReserveCalls);
    }

    [Fact]
    public async Task Killed_direct_job_seals_an_incomplete_recovery_prefix()
    {
        using var store = CreateOutputStore("killed-prefix");
        const string prefix = "DIRECT_JOB_PREFIX_BEFORE_KILL";
        var terminal = new TaskCompletionSource<JobSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = _jobs.PrepareStart(
            $"Write-Output '{prefix}'; Start-Sleep -Seconds 300",
            Path.GetTempPath());
        var started = _jobs.CommitStart(
            plan,
            snapshot =>
            {
                terminal.TrySetResult(snapshot);
                return Task.CompletedTask;
            },
            outputStore: store);
        Assert.True(_jobs.ConfirmStartRecorded(started.Id));

        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (true)
            {
                var captured = _jobs.ReadOutput(started.Id, 0)!.Value.Text;
                if (captured.Contains(prefix, StringComparison.Ordinal)) break;
                Assert.True(
                    DateTimeOffset.UtcNow < deadline,
                    "the direct job did not publish its guarded prefix before the kill deadline");
                await Task.Delay(25);
            }

            Assert.True(_jobs.Kill(started.Id));
            var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(final.Running);
            Assert.True(final.KillRequested);
            Assert.True(final.OutputRecoveryFinalized);
            var recovery = Assert.IsType<OutputRecoverySummary>(final.OutputRecovery);
            Assert.True(recovery.Advertise);
            Assert.Equal(OutputArtifactState.Incomplete, recovery.State);
            var handle = Assert.IsType<string>(recovery.Handle);

            var status = store.Status(handle);
            Assert.Equal(OutputArtifactState.Incomplete, status.State);
            Assert.False(status.Complete);
            Assert.Equal(OutputProvenance.DirectText, status.Provenance);
            var read = store.Read(handle, 0, OutputStore.MaximumReadBytes);
            Assert.Equal(OutputArtifactState.Incomplete, read.State);
            Assert.False(read.Complete);
            Assert.Equal(OutputProvenance.DirectText, read.Provenance);
            Assert.Contains(prefix, read.Text, StringComparison.Ordinal);
            var recoveryStatus = SessionRuntime.RecoveryStatus(final);
            Assert.Contains($"ptk_output handle={handle}", recoveryStatus, StringComparison.Ordinal);
            Assert.Contains("artifact incomplete", recoveryStatus, StringComparison.Ordinal);
            Assert.DoesNotContain(final.OutputPath, recoveryStatus, StringComparison.OrdinalIgnoreCase);
            using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(10));
            var toolStatus = await (new SessionRuntime(host, _jobs, new RawUsageCounter())).JobAsync(
                "status",
                CancellationToken.None,
                id: started.Id);
            Assert.Contains("recovery artifact incomplete", toolStatus, StringComparison.Ordinal);
            Assert.DoesNotContain(", output incomplete", toolStatus, StringComparison.Ordinal);
            Assert.Contains(handle, toolStatus, StringComparison.Ordinal);
            Assert.DoesNotContain(final.OutputPath, toolStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _jobs.Kill(started.Id);
        }
    }

    [Fact]
    public async Task Direct_job_recovery_seal_failure_never_reexecutes_or_advertises_a_handle()
    {
        using var store = CreateOutputStore(
            "seal-failure",
            _ => throw new IOException("injected job recovery seal failure"));
        var executionMarker = Path.Combine(_fixtureDir, "recovery-seal-executions.txt");
        Directory.CreateDirectory(_fixtureDir);
        var escapedMarker = executionMarker.Replace("'", "''", StringComparison.Ordinal);
        var plan = _jobs.PrepareStart(
            $"Add-Content -LiteralPath '{escapedMarker}' -Value x; 'SEALED_ONCE'",
            Path.GetTempPath());

        var started = _jobs.CommitStart(plan, outputStore: store);
        Assert.True(_jobs.ConfirmStartRecorded(started.Id));
        var final = await WaitForExitAsync(started.Id);

        Assert.Equal(["x"], File.ReadAllLines(executionMarker));
        Assert.True(final.OutputRecoveryFinalized);
        var recovery = Assert.IsType<OutputRecoverySummary>(final.OutputRecovery);
        Assert.Null(recovery.Handle);
        Assert.Equal(OutputArtifactState.NotFound, recovery.State);
        Assert.Equal("storage_unavailable", recovery.DetailCode);
        Assert.True(recovery.Advertise);
        var recoveryStatus = SessionRuntime.RecoveryStatus(final);
        Assert.Equal(
            "recovery=unavailable: output capture unavailable; command was not rerun",
            recoveryStatus);
        Assert.DoesNotContain(final.OutputPath, recoveryStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("ptko_", recoveryStatus, StringComparison.Ordinal);

        Assert.True(
            store.TryReserve("default", out var replacement, out var failure),
            failure);
        replacement!.Dispose();
    }

    [Fact]
    public void Direct_job_proved_no_start_releases_its_output_reservation()
    {
        using var store = CreateOutputStore(
            "proved-no-start",
            singleReservationCapacity: true);
        _jobs.ProcessStartOverrideForTests = _ => false;
        var plan = _jobs.PrepareStart("'must not execute'", Path.GetTempPath());

        var error = Assert.Throws<JobStartException>(() =>
            _jobs.CommitStart(plan, outputStore: store));

        Assert.False(error.ProcessStarted);
        Assert.Empty(_jobs.List());
        Assert.True(
            store.TryReserve("default", out var replacement, out var failure),
            failure);
        replacement!.Dispose();
    }

    [Fact]
    public void Direct_job_unassociated_start_unknown_releases_recovery_capacity()
    {
        using var store = CreateOutputStore(
            "unassociated-start",
            singleReservationCapacity: true);
        // This manager intentionally retains an unassociated, root-unconfirmed
        // tombstone. Disposing it would correctly refuse to claim a clean
        // shutdown; it owns no associated process or remaining store capacity.
        var jobs = new JobManager(Path.Combine(_dir, "direct-unassociated-start"));
        jobs.ProcessStartOverrideForTests = _ =>
            throw new IOException("injected unassociated start uncertainty");
        var plan = jobs.PrepareStart("'start outcome unknown'", Path.GetTempPath());

        var error = Assert.Throws<JobStartException>(() =>
            jobs.CommitStart(plan, outputStore: store));

        Assert.Null(error.ProcessStarted);
        var tombstone = Assert.Single(jobs.List());
        Assert.True(tombstone.StartOutcomeUnknown);
        Assert.True(tombstone.OutputRecoveryFinalized);
        Assert.Null(tombstone.OutputRecovery?.Handle);
        Assert.Equal(
            "process_start_outcome_unknown",
            tombstone.OutputRecovery?.DetailCode);
        Assert.True(
            store.TryReserve("default", out var replacement, out var failure),
            failure);
        replacement!.Dispose();
    }

    [Fact]
    public async Task Blocked_output_reservation_is_bounded_and_job_executes_once_without_recovery()
    {
        using var reservationEntered = new ManualResetEventSlim();
        using var releaseReservation = new ManualResetEventSlim();
        using var store = CreateOutputStore(
            "blocked-reservation",
            singleReservationCapacity: true,
            reservationStartingForTests: () =>
            {
                reservationEntered.Set();
                releaseReservation.Wait(TimeSpan.FromSeconds(10));
            });
        using var jobs = new JobManager(
            JobPwshExecutable.ResolveFromPath(),
            Path.Combine(_dir, "blocked-output-reservation"),
            abortedOutputDrainGrace: TimeSpan.FromMilliseconds(100));
        Directory.CreateDirectory(_fixtureDir);
        var executionMarker = Path.Combine(_fixtureDir, "blocked-reservation-executions.txt");
        var escapedMarker = executionMarker.Replace("'", "''", StringComparison.Ordinal);
        var plan = jobs.PrepareStart(
            $"Add-Content -LiteralPath '{escapedMarker}' -Value x; 'RAN_ONCE'",
            Path.GetTempPath());

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var started = jobs.CommitStart(plan, outputStore: store);
            stopwatch.Stop();

            Assert.True(jobs.ConfirmStartRecorded(started.Id));
            Assert.True(reservationEntered.IsSet);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"blocked recovery reservation delayed start for {stopwatch.Elapsed}");
            releaseReservation.Set();
            var final = await WaitForExitAsync(jobs, started.Id, timeoutSeconds: 10);

            Assert.Equal(["x"], File.ReadAllLines(executionMarker));
            Assert.True(final.OutputRecoveryFinalized);
            Assert.Null(final.OutputRecovery?.Handle);
            Assert.Equal(
                "output_store_prepare_timed_out",
                final.OutputRecovery?.DetailCode);

            OutputCaptureReservation? replacement = null;
            string? failure = null;
            Assert.True(
                SpinWait.SpinUntil(
                    () => store.TryReserve("default", out replacement, out failure),
                    TimeSpan.FromSeconds(5)),
                failure ?? "the timed-out reservation did not release output-store capacity");
            replacement!.Dispose();
        }
        finally
        {
            releaseReservation.Set();
        }
    }

    [Fact]
    public async Task Blocked_terminal_seal_uses_one_store_lane_and_second_job_fails_busy()
    {
        using var artifactCreateEntered = new ManualResetEventSlim();
        using var releaseArtifactCreate = new ManualResetEventSlim();
        var artifactCreateCalls = 0;
        using var store = CreateOutputStore(
            "blocked-terminal-seal",
            artifactCreateStartingForTests: _ =>
            {
                Interlocked.Increment(ref artifactCreateCalls);
                artifactCreateEntered.Set();
                releaseArtifactCreate.Wait(TimeSpan.FromSeconds(10));
            });
        using var jobs = new JobManager(
            JobPwshExecutable.ResolveFromPath(),
            Path.Combine(_dir, "blocked-terminal-seal"),
            postExitOutputDrainGrace: TimeSpan.FromSeconds(2),
            abortedOutputDrainGrace: TimeSpan.FromMilliseconds(100));
        Directory.CreateDirectory(_fixtureDir);
        var firstGate = Path.Combine(_fixtureDir, "first-seal.gate");
        var secondGate = Path.Combine(_fixtureDir, "second-seal.gate");
        var escapedFirstGate = firstGate.Replace("'", "''", StringComparison.Ordinal);
        var escapedSecondGate = secondGate.Replace("'", "''", StringComparison.Ordinal);
        var firstTerminal = new TaskCompletionSource<JobSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTerminal = new TaskCompletionSource<JobSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPlan = jobs.PrepareStart(
            $"while (-not (Test-Path -LiteralPath '{escapedFirstGate}')) {{ " +
            "Start-Sleep -Milliseconds 10 }; 'FIRST_SEAL'",
            Path.GetTempPath());
        var secondPlan = jobs.PrepareStart(
            $"while (-not (Test-Path -LiteralPath '{escapedSecondGate}')) {{ " +
            "Start-Sleep -Milliseconds 10 }; 'SECOND_SEAL'",
            Path.GetTempPath());
        var first = jobs.CommitStart(
            firstPlan,
            snapshot =>
            {
                firstTerminal.TrySetResult(snapshot);
                return Task.CompletedTask;
            },
            outputStore: store);
        var second = jobs.CommitStart(
            secondPlan,
            snapshot =>
            {
                secondTerminal.TrySetResult(snapshot);
                return Task.CompletedTask;
            },
            outputStore: store);
        Assert.True(jobs.ConfirmStartRecorded(first.Id));
        Assert.True(jobs.ConfirmStartRecorded(second.Id));

        try
        {
            File.WriteAllText(firstGate, string.Empty);
            Assert.True(
                artifactCreateEntered.Wait(TimeSpan.FromSeconds(10)),
                "the first job never entered the guarded artifact-create seam");
            var sealing = Assert.IsType<JobSnapshot>(jobs.Snapshot(first.Id));
            Assert.True(sealing.Running);
            Assert.False(sealing.OutputRecoveryFinalized);
            Assert.Contains(
                "recovery=pending",
                SessionRuntime.RecoveryStatus(sealing),
                StringComparison.Ordinal);

            File.WriteAllText(secondGate, string.Empty);
            var secondFinal = await secondTerminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var firstFinal = await firstTerminal.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, Volatile.Read(ref artifactCreateCalls));
            Assert.Null(firstFinal.OutputRecovery?.Handle);
            Assert.Equal(
                "output_store_seal_timed_out",
                firstFinal.OutputRecovery?.DetailCode);
            Assert.Null(secondFinal.OutputRecovery?.Handle);
            Assert.Equal("output_store_busy", secondFinal.OutputRecovery?.DetailCode);
        }
        finally
        {
            releaseArtifactCreate.Set();
            jobs.Kill(first.Id);
            jobs.Kill(second.Id);
        }

        Task<int>? laneProbe = null;
        Assert.True(
            SpinWait.SpinUntil(
                () => store.TryStartForegroundOperation(() => 1, out laneProbe),
                TimeSpan.FromSeconds(5)),
            "the timed-out terminal seal did not release the shared storage lane");
        Assert.Equal(1, await laneProbe!);
        Assert.Equal(1, Volatile.Read(ref artifactCreateCalls));
        Assert.True(
            store.TryReserve("default", out var replacement, out var failure),
            failure);
        replacement!.Dispose();
    }

    [Fact]
    public async Task Direct_job_snapshot_cap_preserves_a_complete_utf8_scalar_boundary()
    {
        const int artifactBytes = 64;
        using var store = CreateOutputStore("utf8-cap");
        _jobs.OutputRecoverySnapshotMaximumBytesForTests = artifactBytes;
        var plan = _jobs.PrepareStart(
            "Write-Output (('a' * 63) + [char]::ConvertFromUtf32(0x1F600) + 'TAIL')",
            Path.GetTempPath());

        var started = _jobs.CommitStart(plan, outputStore: store);
        Assert.True(_jobs.ConfirmStartRecorded(started.Id));
        var final = await WaitForExitAsync(started.Id);

        var recovery = Assert.IsType<OutputRecoverySummary>(final.OutputRecovery);
        Assert.Equal(OutputArtifactState.Incomplete, recovery.State);
        Assert.Equal("artifact_cap_exceeded", recovery.DetailCode);
        var handle = Assert.IsType<string>(recovery.Handle);
        var read = store.Read(handle, 0, OutputStore.MaximumReadBytes);
        Assert.Equal(OutputArtifactState.Incomplete, read.State);
        Assert.Equal(
            new string('a', 63),
            read.Text.Split('\n', StringSplitOptions.None)[0].TrimEnd('\r'));
        Assert.Contains("[exit] 0", read.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\ufffd', read.Text);
        Assert.DoesNotContain("TAIL", read.Text, StringComparison.Ordinal);
        var recoveryStatus = SessionRuntime.RecoveryStatus(final);
        Assert.Contains("artifact incomplete", recoveryStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("output incomplete", recoveryStatus, StringComparison.Ordinal);
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(10));
        var toolStatus = await (new SessionRuntime(host, _jobs, new RawUsageCounter())).JobAsync(
            "status",
            CancellationToken.None,
            id: started.Id);
        Assert.Contains("recovery artifact incomplete", toolStatus, StringComparison.Ordinal);
        Assert.DoesNotContain(", output incomplete", toolStatus, StringComparison.Ordinal);
        Assert.Contains(handle, toolStatus, StringComparison.Ordinal);
        Assert.DoesNotContain(final.OutputPath, toolStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rtk_job_uses_typed_identity_cwd_and_arguments_and_retains_metadata()
    {
        Directory.CreateDirectory(_fixtureDir);
        var command = OperatingSystem.IsWindows()
            ? "echo RTK_STDOUT&echo RTK_STDERR>&2&cd&exit /b 7"
            : "printf '%s\n' RTK_STDOUT; printf '%s\n' RTK_STDERR 1>&2; pwd; exit 7";
        var arguments = DirectCommand(command);
        var dispatch = CreateRtkDispatch(_fixtureDir, arguments);
        var observedStarts = 0;
        _jobs.BeforeProcessStartForTests = observed =>
        {
            Assert.Same(dispatch, observed.Dispatch);
            Assert.Null(observed.EncodedCommand);
            Interlocked.Increment(ref observedStarts);
        };
        _jobs.ProcessStartOverrideForTests = process =>
        {
            Assert.True(string.Equals(
                dispatch.RtkExecutableIdentity!.ExecutablePath,
                process.StartInfo.FileName,
                StringComparisonForPaths()));
            Assert.True(string.Equals(
                _fixtureDir,
                process.StartInfo.WorkingDirectory,
                StringComparisonForPaths()));
            Assert.Equal(arguments, process.StartInfo.ArgumentList.ToArray());
            Assert.True(process.StartInfo.RedirectStandardInput);
            Assert.True(process.StartInfo.RedirectStandardOutput);
            Assert.True(process.StartInfo.RedirectStandardError);
            Assert.False(process.StartInfo.UseShellExecute);
            return process.Start();
        };
        var plan = _jobs.PrepareStart(dispatch, _fixtureDir);

        var started = _jobs.CommitStart(plan);
        Assert.True(_jobs.ConfirmStartRecorded(started.Id));
        var final = await WaitForExitAsync(started.Id);
        var output = _jobs.ReadOutput(started.Id, 0)!.Value.Text;
        var listed = Assert.Single(_jobs.List());

        Assert.Equal(1, observedStarts);
        Assert.Equal(7, final.ExitCode);
        Assert.Contains("RTK_STDOUT", output);
        Assert.Contains("RTK_STDERR", output);
        Assert.Contains(_fixtureDir, output, StringComparisonForPaths());
        Assert.Same(plan.Execution, started.Execution);
        Assert.Same(plan.Execution, listed.Execution);
        Assert.Same(plan.Execution, final.Execution);
        Assert.Same(dispatch, final.Execution.Dispatch);
        Assert.Equal(ExecutionPath.Rtk, final.Execution.ExecutionPath);
        Assert.Equal(OutputProvenance.RtkUnknown, final.Execution.OutputProvenance);
        Assert.True(final.OutputCaptureComplete);
        Assert.Null(final.OutputFailureCode);
    }

    [Fact]
    public async Task Cold_rtk_fallback_runs_exact_original_once_as_direct_text_PowerShell()
    {
        Directory.CreateDirectory(_fixtureDir);
        var marker = Path.Combine(_fixtureDir, "fallback-starts.txt");
        var markerLiteral = marker.Replace("'", "''", StringComparison.Ordinal);
        var original =
            $"Add-Content -LiteralPath '{markerLiteral}' -Value x; 'COLD_FALLBACK_EXACT'";
        var initial = CreateRtkDispatch(
            _fixtureDir,
            DirectCommand("echo WRONG_INITIAL_ROUTE"),
            originalScript: original);
        var processStarts = 0;
        _jobs.ProcessStartOverrideForTests = _ =>
        {
            Interlocked.Increment(ref processStarts);
            return false;
        };
        var initialPlan = _jobs.PrepareStart(initial, _fixtureDir);
        var noStart = Assert.Throws<JobStartException>(() =>
            _jobs.CommitStart(initialPlan));
        var fallback = ExecutionDispatch.RtkPreStartFallback(
            initial.Plan,
            noStart.ProvenPreStartFallbackReason!.Value);
        _jobs.BeforeProcessStartForTests = observed =>
        {
            Assert.Same(fallback, observed.Dispatch);
        };
        _jobs.ProcessStartOverrideForTests = process =>
        {
            Interlocked.Increment(ref processStarts);
            return process.Start();
        };
        var plan = _jobs.BindDispatch(initialPlan, fallback, _fixtureDir);

        var started = _jobs.CommitStart(plan);
        Assert.True(_jobs.ConfirmStartRecorded(started.Id));
        var final = await WaitForExitAsync(started.Id);
        var output = _jobs.ReadOutput(started.Id, 0)!.Value.Text;

        Assert.Equal(2, processStarts);
        Assert.Equal(["x"], File.ReadAllLines(marker));
        Assert.Equal(0, final.ExitCode);
        Assert.Contains("COLD_FALLBACK_EXACT", output);
        Assert.DoesNotContain("WRONG_INITIAL_ROUTE", output);
        Assert.Equal(ExecutionPath.PowerShellDirect, final.Execution.ExecutionPath);
        Assert.Equal(OutputProvenance.DirectText, final.Execution.OutputProvenance);
        Assert.Equal(
            ExecutionFallbackReason.RtkExecutionPreparationFailed,
            final.Execution.FallbackReason);
        Assert.Same(fallback, final.Execution.Dispatch);
    }

    [Fact]
    public async Task Rtk_terminal_waits_for_output_drain_after_start_confirmation()
    {
        Directory.CreateDirectory(_fixtureDir);
        var drainEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDrain = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<JobSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.BeforeOutputDrainCompletesForTests = _ =>
        {
            drainEntered.TrySetResult();
            return releaseDrain.Task;
        };
        var command = OperatingSystem.IsWindows()
            ? "echo STDOUT_TAIL&echo STDERR_TAIL>&2&exit /b 0"
            : "printf '%s\n' STDOUT_TAIL; printf '%s\n' STDERR_TAIL 1>&2";
        var dispatch = CreateRtkDispatch(_fixtureDir, DirectCommand(command));
        var plan = _jobs.PrepareStart(dispatch, _fixtureDir);
        try
        {
            var started = _jobs.CommitStart(plan, snapshot =>
            {
                terminal.TrySetResult(snapshot);
                return Task.CompletedTask;
            });
            Assert.True(_jobs.ConfirmStartRecorded(started.Id));
            await drainEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(terminal.Task.IsCompleted);
            var beforeRelease = _jobs.ReadOutput(started.Id, 0)!.Value.Text;
            Assert.Contains("STDOUT_TAIL", beforeRelease);
            Assert.Contains("STDERR_TAIL", beforeRelease);

            releaseDrain.TrySetResult();
            var published = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(published.Running);
            Assert.True(published.OutputCaptureComplete);
            Assert.Same(plan.Execution, published.Execution);
        }
        finally
        {
            releaseDrain.TrySetResult();
            _jobs.BeforeOutputDrainCompletesForTests = null;
        }
    }

    [Fact]
    public async Task Rtk_output_finalization_timeout_is_bounded_and_marked_incomplete()
    {
        Directory.CreateDirectory(_fixtureDir);
        var releaseDrain = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var jobs = new JobManager(
            JobPwshExecutable.ResolveFromPath(),
            Path.Combine(_dir, "bounded-drain"),
            postExitOutputDrainGrace: TimeSpan.FromMilliseconds(100),
            abortedOutputDrainGrace: TimeSpan.FromMilliseconds(100))
        {
            BeforeOutputDrainCompletesForTests = _ => releaseDrain.Task,
        };
        var dispatch = CreateRtkDispatch(
            _fixtureDir,
            DirectCommand(OperatingSystem.IsWindows()
                ? "echo BOUNDED_EOF&exit /b 0"
                : "printf '%s\n' BOUNDED_EOF"));
        var plan = jobs.PrepareStart(dispatch, _fixtureDir);
        try
        {
            var started = jobs.CommitStart(plan);
            Assert.True(jobs.ConfirmStartRecorded(started.Id));

            var final = await WaitForExitAsync(jobs, started.Id, timeoutSeconds: 10);

            Assert.False(final.OutputCaptureComplete);
            Assert.Equal("rtk_output_eof_unconfirmed", final.OutputFailureCode);
            Assert.Contains("BOUNDED_EOF", jobs.ReadOutput(started.Id, 0)!.Value.Text);
        }
        finally
        {
            releaseDrain.TrySetResult();
            jobs.BeforeOutputDrainCompletesForTests = null;
        }
    }

    [Fact]
    public async Task Rtk_drains_large_stdout_and_stderr_concurrently_without_deadlock()
    {
        Directory.CreateDirectory(_fixtureDir);
        var command = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,12000) do (echo OUT_%i&echo ERR_%i 1>&2)&echo STDOUT_FINAL&echo STDERR_FINAL 1>&2"
            : "i=0; while [ $i -lt 12000 ]; do printf 'OUT_%s\\n' \"$i\"; printf 'ERR_%s\\n' \"$i\" 1>&2; i=$((i+1)); done; printf '%s\\n' STDOUT_FINAL; printf '%s\\n' STDERR_FINAL 1>&2";
        var dispatch = CreateRtkDispatch(_fixtureDir, DirectCommand(command));
        var plan = _jobs.PrepareStart(dispatch, _fixtureDir);

        var started = _jobs.CommitStart(plan);
        Assert.True(_jobs.ConfirmStartRecorded(started.Id));
        var final = await WaitForExitAsync(started.Id, timeoutSeconds: 20);
        var output = File.ReadAllText(plan.OutputPath);

        Assert.Equal(0, final.ExitCode);
        Assert.True(final.OutputCaptureComplete);
        Assert.Contains("STDOUT_FINAL", output);
        Assert.Contains("STDERR_FINAL", output);
    }

    [Fact]
    public void Rtk_identity_drift_is_a_typed_proven_no_start_without_job_or_log()
    {
        Directory.CreateDirectory(_fixtureDir);
        var copiedExecutable = CopyCommandProcessor(_fixtureDir);
        var identity = RtkExecutableIdentity.TryCapture(copiedExecutable);
        Assert.NotNull(identity);
        var dispatch = CreateRtkDispatch(
            _fixtureDir,
            DirectCommand("echo MUST_NOT_RUN"),
            identity);
        var plan = _jobs.PrepareStart(dispatch, _fixtureDir);
        var finalChecks = 0;
        var processStarts = 0;
        _jobs.BeforeProcessStartForTests = _ =>
        {
            Interlocked.Increment(ref finalChecks);
            File.Delete(copiedExecutable);
        };
        _jobs.ProcessStartOverrideForTests = process =>
        {
            Interlocked.Increment(ref processStarts);
            return process.Start();
        };

        var error = Assert.Throws<JobStartException>(() => _jobs.CommitStart(plan));

        Assert.Equal(1, finalChecks);
        Assert.Equal(0, processStarts);
        Assert.False(error.ProcessStarted);
        Assert.Equal("rtk_identity_changed", error.DetailCode);
        Assert.Equal(
            ExecutionFallbackReason.RtkExecutableBecameUnavailable,
            error.ProvenPreStartFallbackReason);
        Assert.Empty(_jobs.List());
        Assert.False(File.Exists(plan.OutputPath));
    }

    [Fact]
    public void Cold_target_drift_before_spool_is_a_typed_proven_no_start()
    {
        Directory.CreateDirectory(_fixtureDir);
        var target = CopyCommandProcessor(_fixtureDir);
        var dispatch = CreateRtkDispatch(
            _fixtureDir,
            OperatingSystem.IsWindows()
                ? [target, "/d", "/s", "/c", "echo MUST_NOT_RUN"]
                : [target, "-c", "echo MUST_NOT_RUN"]);
        var plan = _jobs.PrepareStart(dispatch, _fixtureDir);
        File.AppendAllText(target, "target-drift");
        var finalGateChecks = 0;
        var processStarts = 0;
        _jobs.BeforeProcessStartForTests = _ =>
            Interlocked.Increment(ref finalGateChecks);
        _jobs.ProcessStartOverrideForTests = _ =>
        {
            Interlocked.Increment(ref processStarts);
            return false;
        };

        var error = Assert.Throws<JobStartException>(() => _jobs.CommitStart(plan));

        Assert.False(error.ProcessStarted);
        Assert.Equal("rtk_target_resolution_changed", error.DetailCode);
        Assert.Equal(
            ExecutionFallbackReason.RtkTargetResolutionChanged,
            error.ProvenPreStartFallbackReason);
        Assert.Equal(0, finalGateChecks);
        Assert.Equal(0, processStarts);
        Assert.Empty(_jobs.List());
        Assert.False(Directory.Exists(_dir));
        Assert.False(File.Exists(plan.OutputPath));
    }

    [Fact]
    public void Cold_target_drift_at_the_final_gate_cleans_spool_and_starts_nothing()
    {
        Directory.CreateDirectory(_fixtureDir);
        var target = CopyCommandProcessor(_fixtureDir);
        var dispatch = CreateRtkDispatch(
            _fixtureDir,
            OperatingSystem.IsWindows()
                ? [target, "/d", "/s", "/c", "echo MUST_NOT_RUN"]
                : [target, "-c", "echo MUST_NOT_RUN"]);
        var plan = _jobs.PrepareStart(dispatch, _fixtureDir);
        var finalGateChecks = 0;
        var processStarts = 0;
        _jobs.BeforeProcessStartForTests = observed =>
        {
            Assert.Same(plan, observed);
            Assert.True(File.Exists(plan.OutputPath));
            Interlocked.Increment(ref finalGateChecks);
            File.AppendAllText(target, "target-drift");
        };
        _jobs.ProcessStartOverrideForTests = _ =>
        {
            Interlocked.Increment(ref processStarts);
            return false;
        };

        var error = Assert.Throws<JobStartException>(() => _jobs.CommitStart(plan));

        Assert.False(error.ProcessStarted);
        Assert.Equal("rtk_target_resolution_changed", error.DetailCode);
        Assert.Equal(
            ExecutionFallbackReason.RtkTargetResolutionChanged,
            error.ProvenPreStartFallbackReason);
        Assert.Equal(1, finalGateChecks);
        Assert.Equal(0, processStarts);
        Assert.Empty(_jobs.List());
        Assert.False(File.Exists(plan.OutputPath));
    }

    [Fact]
    public void Cold_target_path_reresolution_change_is_a_typed_proven_no_start()
    {
        const string commandName = "ptk-path-reresolution-target";
        var (firstDirectory, firstPath) = RtkTestStub.Create(
            OperatingSystem.IsWindows() ? "exit /b 0" : "exit 0",
            _fixtureDir,
            commandName);
        var (secondDirectory, secondPath) = RtkTestStub.Create(
            OperatingSystem.IsWindows() ? "exit /b 0" : "exit 0",
            _fixtureDir,
            commandName);
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        var savedPathExt = Environment.GetEnvironmentVariable("PATHEXT");
        try
        {
            Environment.SetEnvironmentVariable("PATH", firstDirectory.FullName);
            if (OperatingSystem.IsWindows())
                Environment.SetEnvironmentVariable("PATHEXT", ".EXE");
            var resolved = ColdPathCommandResolver.Resolve(commandName, _fixtureDir);
            Assert.NotNull(resolved);
            Assert.True(string.Equals(
                firstPath,
                resolved.Source,
                StringComparisonForPaths()));
            var targetIdentity = ColdCommandTargetIdentity.TryCapture(
                commandName,
                resolved,
                _fixtureDir);
            Assert.NotNull(targetIdentity);
            var rtkIdentity = RtkExecutableIdentity.TryCapture(_rtkFixturePath);
            Assert.NotNull(rtkIdentity);
            var execution = new ExecutionPlan(
                originalScript: commandName,
                executionScript: null,
                ExecutionDomain.NativeTerminal,
                ExecutionPath.Rtk,
                PreExecutionValidation.None,
                ResolutionContext.Cold,
                RequestedExecutionRoute.Auto,
                OutputProvenance.RtkUnknown,
                [ExecutionPath.PowerShellDirect],
                fallbackReason: null,
                rtkIdentity,
                workingDirectory: _fixtureDir,
                rtkArgumentVector: [commandName],
                directFallbackProvenance: OutputProvenance.DirectText,
                coldCommandTargetIdentity: targetIdentity);
            var plan = _jobs.PrepareStart(
                ExecutionDispatch.FromPlan(execution),
                _fixtureDir);
            var processStarts = 0;
            _jobs.ProcessStartOverrideForTests = _ =>
            {
                Interlocked.Increment(ref processStarts);
                return false;
            };

            Environment.SetEnvironmentVariable("PATH", secondDirectory.FullName);
            var current = ColdPathCommandResolver.Resolve(commandName, _fixtureDir);
            Assert.NotNull(current);
            Assert.True(string.Equals(
                secondPath,
                current.Source,
                StringComparisonForPaths()));
            Assert.True(File.Exists(firstPath));

            var error = Assert.Throws<JobStartException>(() => _jobs.CommitStart(plan));

            Assert.False(error.ProcessStarted);
            Assert.Equal("rtk_target_resolution_changed", error.DetailCode);
            Assert.Equal(
                ExecutionFallbackReason.RtkTargetResolutionChanged,
                error.ProvenPreStartFallbackReason);
            Assert.Equal(0, processStarts);
            Assert.Empty(_jobs.List());
            Assert.False(Directory.Exists(_dir));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Environment.SetEnvironmentVariable("PATHEXT", savedPathExt);
        }
    }

    [Fact]
    public void Rtk_preprocess_setup_failure_is_typed_and_cleans_spool()
    {
        Directory.CreateDirectory(_fixtureDir);
        var dispatch = CreateRtkDispatch(
            _fixtureDir,
            DirectCommand("echo MUST_NOT_RUN"));
        var plan = _jobs.PrepareStart(dispatch, _fixtureDir);
        var processStarts = 0;
        _jobs.BeforeProcessStartForTests = _ =>
            throw new IOException("fixture RTK setup failure");
        _jobs.ProcessStartOverrideForTests = _ =>
        {
            Interlocked.Increment(ref processStarts);
            return false;
        };

        var error = Assert.Throws<JobStartException>(() => _jobs.CommitStart(plan));

        Assert.False(error.ProcessStarted);
        Assert.Equal("rtk_execution_preparation_failed", error.DetailCode);
        Assert.Equal(
            ExecutionFallbackReason.RtkExecutionPreparationFailed,
            error.ProvenPreStartFallbackReason);
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(0, processStarts);
        Assert.Empty(_jobs.List());
        Assert.False(File.Exists(plan.OutputPath));
    }

    [Fact]
    public void Expired_commit_budget_starts_nothing_and_does_not_authorize_fallback()
    {
        Directory.CreateDirectory(_fixtureDir);
        var dispatch = CreateRtkDispatch(
            _fixtureDir,
            DirectCommand("echo MUST_NOT_RUN"));
        var plan = _jobs.PrepareStart(dispatch, _fixtureDir);
        var finalGateChecks = 0;
        var processStarts = 0;
        _jobs.BeforeProcessStartForTests = _ =>
            Interlocked.Increment(ref finalGateChecks);
        _jobs.ProcessStartOverrideForTests = _ =>
        {
            Interlocked.Increment(ref processStarts);
            return false;
        };

        var error = Assert.Throws<JobStartException>(() => _jobs.CommitStart(
            plan,
            deadline: DateTimeOffset.UtcNow.AddSeconds(-1)));

        Assert.False(error.ProcessStarted);
        Assert.Equal("prestart_deadline_expired", error.DetailCode);
        Assert.Null(error.ProvenPreStartFallbackReason);
        Assert.Equal(0, finalGateChecks);
        Assert.Equal(0, processStarts);
        Assert.Empty(_jobs.List());
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Rtk_known_process_start_failure_is_typed_and_removes_the_spool()
    {
        var missingCwd = Path.Combine(_fixtureDir, "missing-cwd");
        var dispatch = CreateRtkDispatch(missingCwd, DirectCommand("echo MUST_NOT_RUN"));
        var plan = _jobs.PrepareStart(dispatch, missingCwd);

        var error = Assert.Throws<JobStartException>(() => _jobs.CommitStart(plan));

        Assert.False(error.ProcessStarted);
        Assert.Equal("rtk_process_start_failed", error.DetailCode);
        Assert.Equal(
            ExecutionFallbackReason.RtkExecutionPreparationFailed,
            error.ProvenPreStartFallbackReason);
        Assert.Empty(_jobs.List());
        Assert.False(File.Exists(plan.OutputPath));
    }

    [Fact]
    public void Proven_direct_no_start_consumes_the_prepared_attempt()
    {
        var processStarts = 0;
        _jobs.ProcessStartOverrideForTests = _ =>
        {
            Interlocked.Increment(ref processStarts);
            return false;
        };
        var plan = _jobs.PrepareStart("'MUST_NOT_RUN'", Path.GetFullPath(Path.GetTempPath()));

        var first = Assert.Throws<JobStartException>(() => _jobs.CommitStart(plan));
        var second = Assert.Throws<InvalidOperationException>(() => _jobs.CommitStart(plan));

        Assert.False(first.ProcessStarted);
        Assert.Null(first.ProvenPreStartFallbackReason);
        Assert.Contains("already consumed", second.Message, StringComparison.Ordinal);
        Assert.Equal(1, processStarts);
        Assert.Empty(_jobs.List());
        Assert.False(File.Exists(plan.OutputPath));
    }

    [Fact]
    public async Task Proven_rtk_no_start_allows_its_exact_fallback_once()
    {
        Directory.CreateDirectory(_fixtureDir);
        var marker = Path.Combine(_fixtureDir, "one-fallback.txt");
        var original =
            $"Add-Content -LiteralPath '{marker.Replace("'", "''", StringComparison.Ordinal)}' -Value x";
        var initial = CreateRtkDispatch(
            _fixtureDir,
            DirectCommand("echo INITIAL_MUST_NOT_RUN"),
            originalScript: original);
        var plan = _jobs.PrepareStart(initial, _fixtureDir);
        var processStarts = 0;
        _jobs.ProcessStartOverrideForTests = _ =>
        {
            Interlocked.Increment(ref processStarts);
            return false;
        };

        var noStart = Assert.Throws<JobStartException>(() => _jobs.CommitStart(plan));
        var repeatedInitial = Assert.Throws<InvalidOperationException>(() =>
            _jobs.CommitStart(plan));
        Assert.False(noStart.ProcessStarted);
        Assert.Equal(
            ExecutionFallbackReason.RtkExecutionPreparationFailed,
            noStart.ProvenPreStartFallbackReason);
        Assert.Contains("already consumed", repeatedInitial.Message, StringComparison.Ordinal);
        Assert.Equal(1, processStarts);

        var mismatched = _jobs.BindDispatch(
            plan,
            ExecutionDispatch.RtkUnavailableFallback(initial.Plan),
            _fixtureDir);
        var mismatchedReason = Assert.Throws<InvalidOperationException>(() =>
            _jobs.CommitStart(mismatched));
        Assert.Contains("already consumed", mismatchedReason.Message, StringComparison.Ordinal);
        Assert.Equal(1, processStarts);

        var fallbackDispatch = ExecutionDispatch.RtkPreStartFallback(
            initial.Plan,
            noStart.ProvenPreStartFallbackReason!.Value);
        var fallback = _jobs.BindDispatch(plan, fallbackDispatch, _fixtureDir);
        _jobs.ProcessStartOverrideForTests = process =>
        {
            Interlocked.Increment(ref processStarts);
            return process.Start();
        };
        var started = _jobs.CommitStart(fallback);
        Assert.True(_jobs.ConfirmStartRecorded(started.Id));
        var final = await WaitForExitAsync(started.Id);

        var repeatedFallback = Assert.Throws<InvalidOperationException>(() =>
            _jobs.CommitStart(fallback));
        Assert.Contains("already consumed", repeatedFallback.Message, StringComparison.Ordinal);
        Assert.Equal(2, processStarts);
        Assert.Equal(["x"], File.ReadAllLines(marker));
        Assert.Equal(ExecutionPath.PowerShellDirect, final.Execution.ExecutionPath);
        Assert.Equal(
            ExecutionFallbackReason.RtkExecutionPreparationFailed,
            final.Execution.FallbackReason);
    }

    [Fact]
    public void Abandoned_proved_fallback_cannot_later_commit()
    {
        Directory.CreateDirectory(_fixtureDir);
        var initial = CreateRtkDispatch(
            _fixtureDir,
            DirectCommand("echo MUST_NOT_RUN"),
            originalScript: "'FALLBACK_MUST_NOT_RUN'");
        var plan = _jobs.PrepareStart(initial, _fixtureDir);
        var processStarts = 0;
        _jobs.ProcessStartOverrideForTests = _ =>
        {
            Interlocked.Increment(ref processStarts);
            return false;
        };
        var noStart = Assert.Throws<JobStartException>(() => _jobs.CommitStart(plan));
        var fallbackDispatch = ExecutionDispatch.RtkPreStartFallback(
            initial.Plan,
            noStart.ProvenPreStartFallbackReason!.Value);
        var fallback = _jobs.BindDispatch(plan, fallbackDispatch, _fixtureDir);

        Assert.True(_jobs.AbandonFallback(plan));
        Assert.False(_jobs.AbandonFallback(plan));
        var rejected = Assert.Throws<InvalidOperationException>(() =>
            _jobs.CommitStart(fallback));

        Assert.Contains("already consumed", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(1, processStarts);
        Assert.Empty(_jobs.List());
    }

    [Fact]
    public async Task Rtk_post_start_output_failure_stays_tracked_and_is_never_a_no_start()
    {
        Directory.CreateDirectory(_fixtureDir);
        var marker = Path.Combine(_fixtureDir, "starts.txt");
        var command = OperatingSystem.IsWindows()
            ? "echo x>>starts.txt&echo CAPTURE_FAIL&exit /b 0"
            : "printf 'x\n' >> starts.txt; printf '%s\n' CAPTURE_FAIL";
        var dispatch = CreateRtkDispatch(_fixtureDir, DirectCommand(command));
        var plan = _jobs.PrepareStart(dispatch, _fixtureDir);
        var writes = 0;
        var terminal = new TaskCompletionSource<JobSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.BeforeOutputWriteForTests = _ =>
        {
            Interlocked.Increment(ref writes);
            throw new IOException("injected output sink failure");
        };
        try
        {
            var exception = Record.Exception(() =>
            {
                var started = _jobs.CommitStart(plan, _ =>
                {
                    terminal.TrySetResult(_);
                    return Task.CompletedTask;
                });
                Assert.True(_jobs.ConfirmStartRecorded(started.Id));
            });
            Assert.Null(exception);

            var final = await WaitForExitAsync(plan.Id);
            var published = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(_jobs.List());
            Assert.Equal(["x"], File.ReadAllLines(marker));
            Assert.Equal(1, writes);
            Assert.Equal(final.Id, published.Id);
            Assert.False(final.OutputCaptureComplete);
            Assert.Equal("rtk_output_write_failed", final.OutputFailureCode);
            Assert.Same(plan.Execution, final.Execution);
        }
        finally
        {
            _jobs.BeforeOutputWriteForTests = null;
        }
    }

    [Fact]
    public async Task Rtk_start_exception_after_process_association_is_retained_and_never_retried()
    {
        Directory.CreateDirectory(_fixtureDir);
        var marker = Path.Combine(_fixtureDir, "starts.txt");
        var command = OperatingSystem.IsWindows()
            ? "echo x>>starts.txt&ping -n 300 127.0.0.1 >nul"
            : "printf 'x\n' >> starts.txt; sleep 300";
        var dispatch = CreateRtkDispatch(_fixtureDir, DirectCommand(command));
        var plan = _jobs.PrepareStart(dispatch, _fixtureDir);
        var processStarts = 0;
        var associatedPid = 0;
        var terminalCalls = 0;
        var terminal = new TaskCompletionSource<JobSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.ProcessStartOverrideForTests = process =>
        {
            Assert.True(process.Start());
            Interlocked.Increment(ref processStarts);
            associatedPid = process.Id;
            Assert.True(
                SpinWait.SpinUntil(MarkerWasWritten, TimeSpan.FromSeconds(10)),
                "the associated process never reached its single-execution marker");
            throw new IOException("injected host failure after Process.Start");
        };

        var error = Assert.Throws<JobStartException>(() =>
            _jobs.CommitStart(plan, snapshot =>
            {
                Interlocked.Increment(ref terminalCalls);
                terminal.TrySetResult(snapshot);
                return Task.CompletedTask;
            }));
        Assert.True(_jobs.ConfirmStartRecorded(plan.Id));

        Assert.True(error.ProcessStarted);
        Assert.Equal("background_process_start_outcome_unknown", error.DetailCode);
        Assert.Null(error.ProvenPreStartFallbackReason);
        var retained = Assert.Single(_jobs.List());
        Assert.Equal(plan.Id, retained.Id);
        Assert.Equal(associatedPid, retained.Pid);
        Assert.False(retained.StartOutcomeUnknown);
        Assert.True(retained.ExecutionOutcomeUnknown);
        Assert.Equal("process_start_outcome_unknown", retained.ExecutionOutcomeFailureCode);

        Assert.ThrowsAny<Exception>(() => _jobs.CommitStart(plan));
        Assert.Equal(1, processStarts);

        var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, terminalCalls);
        Assert.False(final.Running);
        Assert.True(final.RootTerminationConfirmed);
        Assert.True(final.ExecutionOutcomeUnknown);
        Assert.False(final.StartOutcomeUnknown);
        Assert.False(final.OutputCaptureComplete);
        Assert.Equal("background_process_start_outcome_unknown", final.OutputFailureCode);
        Assert.Equal(["x"], File.ReadAllLines(marker));

        bool MarkerWasWritten()
        {
            try { return File.Exists(marker) && File.ReadAllLines(marker).Length == 1; }
            catch (IOException) { return false; }
        }
    }

    [Fact]
    public async Task Post_start_setup_exception_is_retained_contained_and_never_retried()
    {
        Directory.CreateDirectory(_fixtureDir);
        var marker = Path.Combine(_fixtureDir, "post-start-setup.txt");
        var command = OperatingSystem.IsWindows()
            ? "echo x>>post-start-setup.txt&ping -n 300 127.0.0.1 >nul"
            : "printf 'x\n' >> post-start-setup.txt; sleep 300";
        var dispatch = CreateRtkDispatch(_fixtureDir, DirectCommand(command));
        var plan = _jobs.PrepareStart(dispatch, _fixtureDir);
        var processStarts = 0;
        var associatedPid = 0;
        var terminalCalls = 0;
        var terminal = new TaskCompletionSource<JobSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.ProcessStartOverrideForTests = process =>
        {
            Interlocked.Increment(ref processStarts);
            return process.Start();
        };
        _jobs.AfterProcessStartForTests = process =>
        {
            associatedPid = process.Id;
            Assert.True(
                SpinWait.SpinUntil(MarkerWasWritten, TimeSpan.FromSeconds(10)),
                "the confirmed process never reached its single-execution marker");
            throw new IOException("injected failure after Process.Start returned true");
        };

        var error = Assert.Throws<JobStartException>(() =>
            _jobs.CommitStart(plan, snapshot =>
            {
                Interlocked.Increment(ref terminalCalls);
                terminal.TrySetResult(snapshot);
                return Task.CompletedTask;
            }));
        Assert.True(_jobs.ConfirmStartRecorded(plan.Id));

        Assert.True(error.ProcessStarted);
        Assert.Equal("background_process_start_outcome_unknown", error.DetailCode);
        Assert.Null(error.ProvenPreStartFallbackReason);
        var retained = Assert.Single(_jobs.List());
        Assert.Equal(associatedPid, retained.Pid);
        Assert.True(retained.ExecutionOutcomeUnknown);

        Assert.ThrowsAny<Exception>(() => _jobs.CommitStart(plan));
        Assert.Equal(1, processStarts);

        var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, terminalCalls);
        Assert.False(final.Running);
        Assert.True(final.RootTerminationConfirmed);
        Assert.True(final.ExecutionOutcomeUnknown);
        Assert.False(final.StartOutcomeUnknown);
        Assert.Equal(["x"], File.ReadAllLines(marker));

        bool MarkerWasWritten()
        {
            try { return File.Exists(marker) && File.ReadAllLines(marker).Length == 1; }
            catch (IOException) { return false; }
        }
    }

    [Fact]
    public async Task Associated_start_failure_retries_internal_containment_on_a_bounded_observer()
    {
        Directory.CreateDirectory(_fixtureDir);
        var jobs = new JobManager(
            JobPwshExecutable.ResolveFromPath(),
            Path.Combine(_dir, "bounded-internal-containment"),
            postExitOutputDrainGrace: TimeSpan.FromMilliseconds(100),
            abortedOutputDrainGrace: TimeSpan.FromMilliseconds(100));
        var containmentAttempts = 0;
        using var containmentEntered = new ManualResetEventSlim();
        using var releaseContainment = new ManualResetEventSlim();
        jobs.BeforeInternalContainmentForTests = _ =>
        {
            if (Interlocked.Increment(ref containmentAttempts) == 1)
            {
                containmentEntered.Set();
                releaseContainment.Wait();
            }
        };
        jobs.ProcessStartOverrideForTests = process =>
        {
            Assert.True(process.Start());
            throw new IOException("injected host failure after Process.Start");
        };
        var dispatch = CreateRtkDispatch(
            _fixtureDir,
            DirectCommand(OperatingSystem.IsWindows()
                ? "ping -n 300 127.0.0.1 >nul"
                : "sleep 300"));
        var plan = jobs.PrepareStart(dispatch, _fixtureDir);
        var terminal = new TaskCompletionSource<JobSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var commit = Task.Run(() => Record.Exception(() =>
                jobs.CommitStart(plan, snapshot =>
                {
                    terminal.TrySetResult(snapshot);
                    return Task.CompletedTask;
                })));
            Assert.True(containmentEntered.Wait(TimeSpan.FromSeconds(5)));
            var error = Assert.IsType<JobStartException>(
                await commit.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(error.ProcessStarted);
            Assert.True(jobs.ConfirmStartRecorded(plan.Id));

            var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(containmentAttempts >= 2);
            Assert.False(final.Running);
            Assert.True(final.RootTerminationConfirmed);
            Assert.True(final.ExecutionOutcomeUnknown);
            Assert.Equal("process_start_outcome_unknown", final.ExecutionOutcomeFailureCode);
        }
        finally
        {
            releaseContainment.Set();
            jobs.BeforeInternalContainmentForTests = null;
            jobs.Dispose();
        }
    }

    [Fact]
    public async Task Late_rtk_read_failure_wakes_the_bounded_containment_observer()
    {
        Directory.CreateDirectory(_fixtureDir);
        var jobs = new JobManager(
            JobPwshExecutable.ResolveFromPath(),
            Path.Combine(_dir, "late-read-containment"),
            postExitOutputDrainGrace: TimeSpan.FromMilliseconds(100),
            abortedOutputDrainGrace: TimeSpan.FromMilliseconds(100));
        var readEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readHooks = 0;
        var containmentAttempts = 0;
        using var containmentEntered = new ManualResetEventSlim();
        using var releaseContainment = new ManualResetEventSlim();
        jobs.BeforeOutputReadForTests = async _ =>
        {
            if (Interlocked.Increment(ref readHooks) != 1) return;
            readEntered.TrySetResult();
            await releaseRead.Task;
            throw new IOException("injected late pipe read failure");
        };
        jobs.BeforeInternalContainmentForTests = _ =>
        {
            if (Interlocked.Increment(ref containmentAttempts) == 1)
            {
                containmentEntered.Set();
                releaseContainment.Wait();
            }
        };
        var dispatch = CreateRtkDispatch(
            _fixtureDir,
            DirectCommand(OperatingSystem.IsWindows()
                ? "ping -n 300 127.0.0.1 >nul"
                : "sleep 300"));
        var plan = jobs.PrepareStart(dispatch, _fixtureDir);
        var terminal = new TaskCompletionSource<JobSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var started = jobs.CommitStart(plan, snapshot =>
            {
                terminal.TrySetResult(snapshot);
                return Task.CompletedTask;
            });
            Assert.True(jobs.ConfirmStartRecorded(started.Id));
            await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(jobs.Snapshot(started.Id)!.Running);

            releaseRead.TrySetResult();
            Assert.True(containmentEntered.Wait(TimeSpan.FromSeconds(5)));
            var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(containmentAttempts >= 2);
            Assert.False(final.Running);
            Assert.True(final.RootTerminationConfirmed);
            Assert.True(final.ExecutionOutcomeUnknown);
            Assert.Equal("rtk_output_read_failed", final.ExecutionOutcomeFailureCode);
            Assert.False(final.OutputCaptureComplete);
            Assert.Equal("rtk_output_read_failed", final.OutputFailureCode);
        }
        finally
        {
            releaseRead.TrySetResult();
            releaseContainment.Set();
            jobs.BeforeOutputReadForTests = null;
            jobs.BeforeInternalContainmentForTests = null;
            jobs.Dispose();
        }
    }

    [Fact]
    public async Task Rtk_unresolved_start_is_a_retained_tombstone_without_terminal_callback()
    {
        Directory.CreateDirectory(_fixtureDir);
        var jobsRoot = Path.Combine(_dir, "unresolved-start");
        var jobs = new JobManager(
            JobPwshExecutable.ResolveFromPath(),
            jobsRoot,
            abortedOutputDrainGrace: TimeSpan.FromMilliseconds(100));
        var processStarts = 0;
        var terminalCalls = 0;
        jobs.ProcessStartOverrideForTests = _ =>
        {
            Interlocked.Increment(ref processStarts);
            throw new IOException("injected failure without a process association");
        };
        var dispatch = CreateRtkDispatch(
            _fixtureDir,
            DirectCommand("echo MUST_NOT_RUN"));
        var plan = jobs.PrepareStart(dispatch, _fixtureDir);

        var error = Assert.Throws<JobStartException>(() =>
            jobs.CommitStart(plan, _ =>
            {
                Interlocked.Increment(ref terminalCalls);
                return Task.CompletedTask;
            }));

        Assert.Null(error.ProcessStarted);
        Assert.Null(error.ProvenPreStartFallbackReason);
        Assert.Equal(1, processStarts);
        var tombstone = Assert.Single(jobs.List());
        Assert.Equal(plan.Id, tombstone.Id);
        Assert.Equal(0, tombstone.Pid);
        Assert.False(tombstone.Running);
        Assert.True(tombstone.StartOutcomeUnknown);
        Assert.True(tombstone.ExecutionOutcomeUnknown);
        Assert.False(tombstone.RootTerminationConfirmed);
        Assert.False(tombstone.OutputCaptureComplete);
        Assert.Equal("background_process_start_outcome_unknown", tombstone.OutputFailureCode);
        Assert.Equal(0, terminalCalls);
        Assert.Equal(
            JobKillDisposition.Failed,
            jobs.RequestKill(plan.Id).Disposition);
        await Assert.ThrowsAsync<InvalidOperationException>(jobs.ShutdownAsync);
    }

    [Fact]
    public async Task Blocked_rtk_output_writer_cannot_block_terminal_finalization()
    {
        Directory.CreateDirectory(_fixtureDir);
        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        var writerExited = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var jobs = new JobManager(
            JobPwshExecutable.ResolveFromPath(),
            Path.Combine(_dir, "blocked-writer"),
            postExitOutputDrainGrace: TimeSpan.FromMilliseconds(100),
            abortedOutputDrainGrace: TimeSpan.FromMilliseconds(100));
        jobs.BeforeOutputWriteForTests = _ =>
        {
            writerEntered.Set();
            try { releaseWriter.Wait(); }
            finally { writerExited.TrySetResult(); }
        };
        var terminal = new TaskCompletionSource<JobSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatch = CreateRtkDispatch(
            _fixtureDir,
            DirectCommand(OperatingSystem.IsWindows()
                ? "for /L %i in (1,1,20000) do @echo BLOCKED_WRITER_%i"
                : "i=0; while [ $i -lt 20000 ]; do printf 'BLOCKED_WRITER_%s\\n' \"$i\"; i=$((i+1)); done"));
        var plan = jobs.PrepareStart(dispatch, _fixtureDir);
        try
        {
            var started = jobs.CommitStart(plan, snapshot =>
            {
                terminal.TrySetResult(snapshot);
                return Task.CompletedTask;
            });
            Assert.True(jobs.ConfirmStartRecorded(started.Id));
            Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(10)));

            var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(writerExited.Task.IsCompleted);
            Assert.False(final.Running);
            Assert.False(final.OutputCaptureComplete);
            Assert.Equal("rtk_output_write_unconfirmed", final.OutputFailureCode);
            Assert.False(final.ExecutionOutcomeUnknown);
        }
        finally
        {
            releaseWriter.Set();
            if (writerEntered.IsSet)
                await writerExited.Task.WaitAsync(TimeSpan.FromSeconds(10));
            jobs.BeforeOutputWriteForTests = null;
            jobs.Dispose();
        }
    }

    [Fact]
    public async Task Timed_out_rtk_write_owns_its_bytes_across_pool_buffer_reuse()
    {
        Directory.CreateDirectory(_fixtureDir);
        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        var hooks = 0;
        var jobs = new JobManager(
            JobPwshExecutable.ResolveFromPath(),
            Path.Combine(_dir, "owned-write-buffer"),
            postExitOutputDrainGrace: TimeSpan.FromMilliseconds(200),
            abortedOutputDrainGrace: TimeSpan.FromMilliseconds(100));
        jobs.BeforeOutputWriteForTests = _ =>
        {
            if (Interlocked.Increment(ref hooks) != 1) return;
            writerEntered.Set();
            releaseWriter.Wait();
        };
        var command = OperatingSystem.IsWindows()
            ? "echo FIRST_CANARY&ping -n 2 127.0.0.1 >nul&" +
              "echo FOREIGN_CANARY&" +
              "ping -n 4 127.0.0.1 >nul"
            : "printf '%s\n' FIRST_CANARY; sleep 0.2; " +
              "printf '%s\n' FOREIGN_CANARY; " +
              "sleep 2";
        var dispatch = CreateRtkDispatch(_fixtureDir, DirectCommand(command));
        var plan = jobs.PrepareStart(dispatch, _fixtureDir);
        try
        {
            var started = jobs.CommitStart(plan);
            Assert.True(jobs.ConfirmStartRecorded(started.Id));
            Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(10)));

            await Task.Delay(OperatingSystem.IsWindows() ? 1500 : 600);
            releaseWriter.Set();
            var final = await WaitForExitAsync(jobs, started.Id, timeoutSeconds: 15);
            var output = File.ReadAllText(plan.OutputPath);

            Assert.False(final.OutputCaptureComplete);
            Assert.Equal("rtk_output_write_unconfirmed", final.OutputFailureCode);
            Assert.Contains("FIRST_CANARY", output);
            Assert.DoesNotContain("FOREIGN_CANARY", output);
            Assert.Equal(1, hooks);
        }
        finally
        {
            releaseWriter.Set();
            jobs.BeforeOutputWriteForTests = null;
            jobs.Dispose();
        }
    }

    [Fact]
    public async Task Duplicate_commit_preserves_the_original_registered_job()
    {
        var starts = 0;
        _jobs.BeforeProcessStartForTests = _ => Interlocked.Increment(ref starts);
        var plan = _jobs.PrepareStart("Start-Sleep -Seconds 300");
        var started = _jobs.CommitStart(plan);
        Assert.True(_jobs.ConfirmStartRecorded(started.Id));

        var error = Assert.Throws<InvalidOperationException>(() =>
            _jobs.CommitStart(plan));

        Assert.Contains("already consumed", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, starts);
        var retained = Assert.Single(_jobs.List());
        Assert.Equal(started.Id, retained.Id);
        Assert.Equal(started.Pid, retained.Pid);
        Assert.True(retained.Running);
        Assert.Equal(JobKillDisposition.Requested, _jobs.RequestKill(started.Id).Disposition);
        var final = await WaitForExitAsync(started.Id);
        Assert.True(final.RootTerminationConfirmed);
    }

    [Fact]
    public void Bound_descriptor_tamper_is_rejected_before_any_start_effect()
    {
        var jobsRoot = Path.Combine(_dir, "tamper-jobs");
        var processStarts = 0;
        using var jobs = new JobManager(
            JobPwshExecutable.ResolveFromPath(),
            jobsRoot)
        {
            ProcessStartOverrideForTests = process =>
            {
                Interlocked.Increment(ref processStarts);
                return process.Start();
            },
        };
        var cwdA = Path.GetFullPath(Path.Combine(_fixtureDir, "cwd-a"));
        var cwdB = Path.GetFullPath(Path.Combine(_fixtureDir, "cwd-b"));
        var reservation = jobs.PrepareStart("'MUST_NOT_RUN'");
        var bound = jobs.BindDispatch(reservation, reservation.Dispatch, cwdA);
        var alternate = CreateRtkDispatch(cwdA, DirectCommand("echo MUST_NOT_RUN"));
        var rtk = jobs.PrepareStart(alternate, cwdA);
        Assert.Throws<InvalidOperationException>(() =>
            jobs.BindDispatch(bound, bound.Dispatch, cwdB));
        var attempts = new[]
        {
            bound with { WorkingDirectory = cwdB },
            bound with { Script = "'TAMPERED'" },
            bound with { OutputPath = Path.Combine(jobsRoot, "wrong.log") },
            bound with { EncodedCommand = "tampered-wrapper" },
            bound with { Dispatch = alternate },
            bound with { Execution = new JobExecutionMetadata(alternate) },
            rtk with { EncodedCommand = "injected-wrapper" },
        };

        foreach (var attempt in attempts)
            Assert.Throws<InvalidOperationException>(() => jobs.CommitStart(attempt));

        Assert.Equal(0, processStarts);
        Assert.Empty(jobs.List());
        Assert.False(Directory.Exists(jobsRoot));
        Assert.False(File.Exists(bound.OutputPath));
        Assert.False(File.Exists(rtk.OutputPath));
    }

    [Fact]
    public void Bound_rtk_reservation_only_accepts_its_own_authorized_fallback_once()
    {
        var cwd = Path.GetFullPath(_fixtureDir);
        var initial = CreateRtkDispatch(
            cwd,
            DirectCommand("echo INITIAL"),
            originalScript: "'EXACT_ORIGINAL'");
        var bound = _jobs.PrepareStart(initial, cwd);
        var exactFallback = ExecutionDispatch.RtkUnavailableFallback(initial.Plan);
        var rebound = _jobs.BindDispatch(bound, exactFallback, cwd);

        Assert.Equal(bound.Id, rebound.Id);
        Assert.Same(initial.Plan, rebound.Dispatch.Plan);
        Assert.Same(exactFallback, rebound.Dispatch);
        Assert.Equal(ExecutionPath.PowerShellDirect, rebound.ExecutionPath);
        Assert.Equal(OutputProvenance.DirectText, rebound.Execution.OutputProvenance);

        var unrelated = CreateRtkDispatch(
            cwd,
            DirectCommand("echo DIFFERENT_PLAN"),
            originalScript: "'EXACT_ORIGINAL'");
        var unrelatedFallback = ExecutionDispatch.RtkUnavailableFallback(unrelated.Plan);
        Assert.Throws<InvalidOperationException>(() =>
            _jobs.BindDispatch(bound, unrelatedFallback, cwd));
        Assert.Throws<InvalidOperationException>(() =>
            _jobs.BindDispatch(rebound, initial, cwd));

        Assert.Empty(_jobs.List());
        Assert.False(Directory.Exists(_dir));
        Assert.False(File.Exists(bound.OutputPath));
    }

    [Fact]
    public void Unproven_rtk_fallback_cannot_be_the_first_start_attempt()
    {
        var cwd = Path.GetFullPath(_fixtureDir);
        var initial = CreateRtkDispatch(
            cwd,
            DirectCommand("echo INITIAL"),
            originalScript: "'MUST_NOT_RUN'");
        var fallback = ExecutionDispatch.RtkUnavailableFallback(initial.Plan);
        var plan = _jobs.PrepareStart(fallback, cwd);
        var processStarts = 0;
        _jobs.ProcessStartOverrideForTests = _ =>
        {
            Interlocked.Increment(ref processStarts);
            return false;
        };

        var error = Record.Exception(() => _jobs.CommitStart(plan));

        var invalid = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("requires a proven no-start", invalid.Message, StringComparison.Ordinal);
        Assert.Equal(0, processStarts);
        Assert.Empty(_jobs.List());
        Assert.False(Directory.Exists(_dir));
        Assert.False(File.Exists(plan.OutputPath));
    }

    [Fact]
    public void Failed_start_does_not_reuse_its_public_job_id()
    {
        var missingCwd = Path.Combine(_dir, "missing", "cwd");
        var failed = _jobs.PrepareStart("'never starts'", missingCwd);

        Assert.ThrowsAny<Exception>(() => _jobs.CommitStart(failed));
        Assert.Null(_jobs.Snapshot(failed.Id));

        var next = _jobs.PrepareStart("'next'");
        Assert.Equal(failed.Id + 1, next.Id);
    }

    [Fact]
    public void Reset_invalidates_a_prepared_start_and_reopens_admission_after_its_lease()
    {
        var stale = _jobs.PrepareStart("'must not start'");

        JobStartPlan blocked;
        using (_jobs.BeginReset())
        {
            Assert.Throws<InvalidOperationException>(() =>
                _jobs.CommitStart(stale));
            blocked = _jobs.PrepareStart("'blocked during reset'");
            Assert.Throws<InvalidOperationException>(() =>
                _jobs.CommitStart(blocked));
        }
        Assert.Throws<InvalidOperationException>(() =>
            _jobs.CommitStart(blocked));

        var fresh = _jobs.PrepareStart("'fresh generation'");
        Assert.Equal(stale.Id + 2, fresh.Id);
        Assert.NotEqual(stale.Generation, fresh.Generation);
    }

    [Fact]
    public async Task Reset_waits_for_inflight_commit_then_kills_the_linearized_job()
    {
        using var commitEntered = new ManualResetEventSlim();
        using var releaseCommit = new ManualResetEventSlim();
        _jobs.BeforeProcessStartForTests = _ =>
        {
            commitEntered.Set();
            if (!releaseCommit.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("test did not release process start");
        };
        var plan = _jobs.PrepareStart("Start-Sleep -Seconds 300");
        var commit = Task.Run(() => _jobs.CommitStart(plan));
        Task<JobManager.JobResetLease>? reset = null;
        try
        {
            Assert.True(commitEntered.Wait(TimeSpan.FromSeconds(5)));
            var list = Task.Run(_jobs.List);
            await Task.Delay(100);
            Assert.False(list.IsCompleted, "list observed an entry before Process.Start completed");

            reset = Task.Run(_jobs.BeginReset);
            await Task.Delay(100);
            Assert.False(reset.IsCompleted, "reset overtook the in-flight start transaction");
            releaseCommit.Set();

            var started = await commit.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(_jobs.ConfirmStartRecorded(started.Id));
            using var resetLease = await reset.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, resetLease.TerminationRequestedCount);
            Assert.Single(await list.WaitAsync(TimeSpan.FromSeconds(10)));
            var final = await WaitForExitAsync(started.Id);
            Assert.True(final.KillRequested);
            Assert.Equal(JobTerminationReason.Reset, final.TerminationReason);
        }
        finally
        {
            releaseCommit.Set();
            _jobs.BeforeProcessStartForTests = null;
            if (reset is not null)
            {
                try
                {
                    var resetLease = await reset.WaitAsync(TimeSpan.FromSeconds(10));
                    resetLease.Dispose();
                }
                catch { /* preserve primary failure */ }
            }
        }
    }

    [Fact]
    public async Task Reset_waits_for_inflight_typed_rtk_commit_then_kills_it()
    {
        Directory.CreateDirectory(_fixtureDir);
        using var commitEntered = new ManualResetEventSlim();
        using var releaseCommit = new ManualResetEventSlim();
        _jobs.BeforeProcessStartForTests = _ =>
        {
            commitEntered.Set();
            if (!releaseCommit.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("test did not release RTK process start");
        };
        var command = OperatingSystem.IsWindows()
            ? "ping -n 300 127.0.0.1 >nul"
            : "sleep 300";
        var dispatch = CreateRtkDispatch(_fixtureDir, DirectCommand(command));
        var plan = _jobs.PrepareStart(dispatch, _fixtureDir);
        var commit = Task.Run(() => _jobs.CommitStart(plan));
        Task<JobManager.JobResetLease>? reset = null;
        try
        {
            Assert.True(commitEntered.Wait(TimeSpan.FromSeconds(5)));
            var list = Task.Run(_jobs.List);
            await Task.Delay(100);
            Assert.False(list.IsCompleted, "list observed typed RTK before start committed");

            reset = Task.Run(_jobs.BeginReset);
            await Task.Delay(100);
            Assert.False(reset.IsCompleted, "reset overtook typed RTK start transaction");
            releaseCommit.Set();

            var started = await commit.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(_jobs.ConfirmStartRecorded(started.Id));
            using var resetLease = await reset.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, resetLease.TerminationRequestedCount);
            var listed = Assert.Single(await list.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(ExecutionPath.Rtk, listed.Execution.ExecutionPath);
            var final = await WaitForExitAsync(started.Id);
            Assert.True(final.KillRequested);
            Assert.Equal(JobTerminationReason.Reset, final.TerminationReason);
            Assert.Equal(ExecutionPath.Rtk, final.Execution.ExecutionPath);
        }
        finally
        {
            releaseCommit.Set();
            _jobs.BeforeProcessStartForTests = null;
            if (reset is not null)
            {
                try
                {
                    var resetLease = await reset.WaitAsync(TimeSpan.FromSeconds(10));
                    resetLease.Dispose();
                }
                catch { /* preserve primary failure */ }
            }
        }
    }

    [Fact]
    public async Task Job_runs_to_completion_and_output_is_readable()
    {
        var job = _jobs.Start("'job says hello'");

        var final = await WaitForExitAsync(job.Id);
        Assert.Equal(0, final.ExitCode);
        Assert.Null(final.OutputCaptureComplete);

        var read = _jobs.ReadOutput(job.Id, 0)!.Value;
        Assert.Contains("job says hello", read.Text);
        Assert.True(read.NextOffset > 0);
    }

    [Fact]
    public async Task Offset_paging_returns_only_new_output()
    {
        var job = _jobs.Start("'first'; 'second'");
        await WaitForExitAsync(job.Id);

        var all = _jobs.ReadOutput(job.Id, 0)!.Value;
        Assert.Contains("second", all.Text);

        var again = _jobs.ReadOutput(job.Id, all.NextOffset)!.Value;
        Assert.Equal(string.Empty, again.Text);
        Assert.Equal(all.NextOffset, again.NextOffset);
    }

    [Fact]
    public async Task Exit_code_propagates_from_the_job_script()
    {
        var job = _jobs.Start("exit 7");

        var final = await WaitForExitAsync(job.Id);
        Assert.Equal(7, final.ExitCode);
    }

    [Fact]
    public async Task Kill_terminates_a_running_job()
    {
        var job = _jobs.Start("Start-Sleep -Seconds 300");
        Assert.True(_jobs.Snapshot(job.Id)!.Running);

        Assert.True(_jobs.Kill(job.Id));

        var final = await WaitForExitAsync(job.Id);
        Assert.False(final.Running);
        Assert.True(final.KillRequested);
        Assert.Equal(JobTerminationReason.ExplicitKill, final.TerminationReason);
        Assert.False(_jobs.Kill(job.Id)); // already dead
    }

    [Fact]
    public async Task Kill_reason_is_visible_before_process_termination_is_requested()
    {
        var job = _jobs.Start("Start-Sleep -Seconds 300");
        _jobs.BeforeKillForTests = _ =>
            Assert.Equal(JobTerminationReason.Reset, _jobs.Snapshot(job.Id)!.TerminationReason);
        try
        {
            var result = _jobs.RequestKill(job.Id, JobTerminationReason.Reset);
            Assert.Equal(JobKillDisposition.Requested, result.Disposition);
        }
        finally
        {
            _jobs.BeforeKillForTests = null;
        }

        var final = await WaitForExitAsync(job.Id);
        Assert.Equal(JobTerminationReason.Reset, final.TerminationReason);
    }

    [Fact]
    public async Task Failed_kill_request_never_claims_or_audits_the_job_as_killed()
    {
        var job = _jobs.Start("Start-Sleep -Seconds 300");
        _jobs.BeforeKillForTests = _ => throw new InvalidOperationException("injected kill failure");
        try
        {
            Assert.False(_jobs.Kill(job.Id));
            var afterFailure = _jobs.Snapshot(job.Id)!;
            Assert.True(afterFailure.Running);
            Assert.False(afterFailure.KillRequested);
            Assert.Equal(JobTerminationReason.None, afterFailure.TerminationReason);
        }
        finally
        {
            _jobs.BeforeKillForTests = null;
            Assert.True(_jobs.Kill(job.Id));
        }

        var final = await WaitForExitAsync(job.Id);
        Assert.True(final.KillRequested);
        Assert.Equal(JobTerminationReason.ExplicitKill, final.TerminationReason);
    }

    [Fact]
    public async Task KillAll_reports_how_many_jobs_it_stopped()
    {
        var a = _jobs.Start("Start-Sleep -Seconds 300");
        var b = _jobs.Start("Start-Sleep -Seconds 300");

        Assert.Equal(2, _jobs.KillAll());
        await WaitForExitAsync(a.Id);
        await WaitForExitAsync(b.Id);
        Assert.Equal(JobTerminationReason.ExplicitKill, _jobs.Snapshot(a.Id)!.TerminationReason);
        Assert.Equal(JobTerminationReason.ExplicitKill, _jobs.Snapshot(b.Id)!.TerminationReason);
        Assert.Equal(0, _jobs.KillAll());
    }

    [Fact]
    public async Task Jobs_run_scripts_under_a_restrictive_windows_execution_policy()
    {
        // Mirror of the foreground regression (dddbb6b): process-scope policy
        // simulates an unconfigured Windows machine; the job child inherits it
        // and must still be able to import a module. No-op hazard off Windows
        // (policies do not apply), where this simply exercises the import.
        var modulePath = RunspaceHost.ResolveModulePath();
        Assert.NotNull(modulePath);
        var saved = Environment.GetEnvironmentVariable("PSExecutionPolicyPreference");
        try
        {
            Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", "Restricted");
            // Assert the CAPABILITY, not survival: the policy error from a
            // blocked module load is emitted through a path that ignores
            // -ErrorAction Stop and execution continues (the original bug's
            // "silent degradation"), so only checking whether the module's
            // command actually exists distinguishes bypass from blocked.
            var job = _jobs.Start(
                $"Import-Module '{modulePath!.Replace("'", "''")}'; " +
                "if (Get-Command Compress-PtcOutput -ErrorAction SilentlyContinue) { 'MODULE LOADED' } else { 'MODULE MISSING' }");

            await WaitForExitAsync(job.Id);
            var read = _jobs.ReadOutput(job.Id, 0)!.Value;

            Assert.Contains("MODULE LOADED", read.Text);
            Assert.DoesNotContain("MODULE MISSING", read.Text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", saved);
        }
    }

    [Fact]
    public async Task Parse_errors_land_in_the_job_log()
    {
        var job = _jobs.Start("if (");

        var final = await WaitForExitAsync(job.Id);
        var read = _jobs.ReadOutput(job.Id, 0)!.Value;

        Assert.NotEqual(0, final.ExitCode);
        Assert.True(read.Text.Length > 0,
            "the parse error must land in the job log, not vanish on the child's stderr");
    }

    private ExecutionDispatch CreateRtkDispatch(
        string workingDirectory,
        ImmutableArray<string> arguments,
        RtkExecutableIdentity? identity = null,
        string originalScript = "fixture command")
    {
        identity ??= RtkExecutableIdentity.TryCapture(_rtkFixturePath);
        Assert.NotNull(identity);
        Assert.NotEmpty(arguments);
        Assert.True(Path.IsPathFullyQualified(arguments[0]));
        var targetIdentity = ColdCommandTargetIdentity.TryCapture(
            arguments[0],
            new ResolvedCommand(
                System.Management.Automation.CommandTypes.Application,
                arguments[0],
                arguments[0]),
            workingDirectory);
        Assert.NotNull(targetIdentity);
        var plan = new ExecutionPlan(
            originalScript: originalScript,
            executionScript: null,
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Cold,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            [ExecutionPath.PowerShellDirect],
            fallbackReason: null,
            identity,
            workingDirectory: workingDirectory,
            rtkArgumentVector: arguments,
            directFallbackProvenance: OutputProvenance.DirectText,
            coldCommandTargetIdentity: targetIdentity);
        return ExecutionDispatch.FromPlan(plan);
    }

    private static ImmutableArray<string> DirectCommand(string command) =>
        OperatingSystem.IsWindows()
            ? [ResolveCommandProcessor(), "/d", "/s", "/c", command]
            : [ResolveCommandProcessor(), "-c", command];

    private static string ResolveCommandProcessor() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe")
            : File.Exists("/bin/sh")
                ? "/bin/sh"
                : "/usr/bin/sh";

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

    private static string CopyCommandProcessor(string directory)
    {
        var source = ResolveCommandProcessor();
        var destination = Path.Combine(
            directory,
            "rtk-fixture" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        File.Copy(source, destination);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        return destination;
    }

    private static StringComparison StringComparisonForPaths() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed class BusyOutputCaptureOwner : IOutputCaptureOwner
    {
        internal int TryStartCalls { get; private set; }
        internal int TryReserveCalls { get; private set; }

        public long MaximumArtifactBytes => 4096;

        public bool TryStartForegroundOperation<T>(
            Func<T> work,
            out Task<T>? operation)
        {
            TryStartCalls++;
            operation = null;
            return false;
        }

        public bool TryReserve(
            string sessionAlias,
            out OutputCaptureReservation? reservation,
            out string? failure)
        {
            TryReserveCalls++;
            reservation = null;
            failure = "unexpected";
            return false;
        }
    }

    [Fact]
    public void Missing_job_reads_as_null()
    {
        Assert.Null(_jobs.Snapshot(999));
        Assert.Null(_jobs.ReadOutput(999, 0));
        Assert.False(_jobs.Kill(999));
    }
}
