namespace PtkMcpServer.Tests;

// ProcessEnvironment collection: the execution-policy test mutates
// PSExecutionPolicyPreference, which job children inherit at spawn.
[Collection("ProcessEnvironment")]
public sealed class JobManagerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ptk-job-tests-" + Guid.NewGuid().ToString("N"));
    private readonly JobManager _jobs;

    public JobManagerTests() => _jobs = new JobManager(_dir);

    public void Dispose()
    {
        _jobs.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* logs may lag a beat */ }
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

            var error = Assert.Throws<InvalidOperationException>(() => jobs.PrepareStart("'must not run'"));

            Assert.Contains("server-start PATH", error.Message);
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

        using (_jobs.BeginReset())
        {
            Assert.Throws<InvalidOperationException>(() =>
                _jobs.CommitStart(stale));
            Assert.Throws<InvalidOperationException>(() =>
                _jobs.PrepareStart("'blocked during reset'"));
        }

        var fresh = _jobs.PrepareStart("'fresh generation'");
        Assert.Equal(stale.Id + 1, fresh.Id);
        Assert.NotEqual(stale.Generation, fresh.Generation);
    }

    [Fact]
    public async Task Reset_waits_for_inflight_commit_then_kills_the_linearized_job()
    {
        using var commitEntered = new ManualResetEventSlim();
        using var releaseCommit = new ManualResetEventSlim();
        _jobs.BeforeProcessStartForTests = () =>
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
            Assert.Equal(1, resetLease.KilledCount);
            Assert.Single(await list.WaitAsync(TimeSpan.FromSeconds(10)));
            var final = await WaitForExitAsync(started.Id);
            Assert.True(final.KillRequested);
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
        Assert.False(_jobs.Kill(job.Id)); // already dead
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
        }
        finally
        {
            _jobs.BeforeKillForTests = null;
            Assert.True(_jobs.Kill(job.Id));
        }

        var final = await WaitForExitAsync(job.Id);
        Assert.True(final.KillRequested);
    }

    [Fact]
    public async Task KillAll_reports_how_many_jobs_it_stopped()
    {
        var a = _jobs.Start("Start-Sleep -Seconds 300");
        var b = _jobs.Start("Start-Sleep -Seconds 300");

        Assert.Equal(2, _jobs.KillAll());
        await WaitForExitAsync(a.Id);
        await WaitForExitAsync(b.Id);
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

    [Fact]
    public void Missing_job_reads_as_null()
    {
        Assert.Null(_jobs.Snapshot(999));
        Assert.Null(_jobs.ReadOutput(999, 0));
        Assert.False(_jobs.Kill(999));
    }
}
