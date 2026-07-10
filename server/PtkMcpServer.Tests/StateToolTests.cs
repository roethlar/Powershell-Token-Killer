using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

// Shares the ProcessEnvironment collection: env vars are process-global, and
// these tests read drift against a baseline other classes could otherwise
// mutate mid-test.
[Collection("ProcessEnvironment")]
public sealed class StateToolTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60));
    private readonly JobManager _jobs = new(
        Path.Combine(Path.GetTempPath(), "ptk-state-jobs-" + Guid.NewGuid().ToString("N")));
    private readonly RawUsageCounter _rawUsage = new();

    public void Dispose()
    {
        _host.Dispose();
        _jobs.Dispose();
    }

    [Fact]
    public async Task State_answers_promptly_with_busy_indicator_during_an_active_call()
    {
        var slow = _host.InvokeAsync("Start-Sleep -Seconds 6; 'slow-done'");
        await Task.Delay(500); // let the slow call own the gate

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var state = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: false, CancellationToken.None);
        sw.Stop();

        // The health check must not queue behind the workload it diagnoses
        // (issue #6): prompt answer, busy line, host-level facts intact.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"ptk_state took {sw.Elapsed}");
        Assert.Contains("runspace: busy (active call running", state);
        Assert.Contains("waiting)", state);
        Assert.Contains("unavailable while busy", state);
        Assert.Contains($"pid {Environment.ProcessId}", state);
        Assert.Contains("[env drift since server start]", state);
        Assert.Contains("jobs:", state);

        var slowResult = await slow;
        Assert.True(slowResult.Success); // the probe disturbed nothing
    }

    [Fact]
    public async Task Busy_listAvailable_leg_reports_unavailable_without_queueing()
    {
        StateTool.ClearAvailableCacheForTests();
        var slow = _host.InvokeAsync("Start-Sleep -Seconds 6");
        await Task.Delay(500);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var state = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: true, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"ptk_state took {sw.Elapsed}");
        Assert.Contains("unavailable while the runspace is busy (not cached)", state);
        await slow;
    }

    [Fact]
    public async Task Concurrent_listAvailable_state_calls_do_not_queue_on_the_cache_gate()
    {
        // A slow first enumeration holds the cache gate; a second state call
        // must report and return, not block behind it (i56-7).
        StateTool.ClearAvailableCacheForTests();
        await _host.InvokeAsync("function global:Get-Module { Start-Sleep -Seconds 5 }", route: "pwsh");
        var first = StateTool.State(_host, _jobs, _rawUsage, listAvailable: true, CancellationToken.None);
        await Task.Delay(700); // let the first call take the cache gate and start enumerating

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var second = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: true, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"second state call took {sw.Elapsed}");
        Assert.Contains($"pid {Environment.ProcessId}", second);
        await first;
    }

    [Fact]
    public async Task Busy_state_call_refreshes_the_idle_clock()
    {
        // A served busy report is user activity (plan finding i56p-10): the
        // idle watchdog must not stop a server right after it answered.
        var slow = _host.InvokeAsync("Start-Sleep -Seconds 4");
        await Task.Delay(500);
        var before = _host.LastActivityUtc;
        await Task.Delay(300);

        await StateTool.State(_host, _jobs, _rawUsage, listAvailable: false, CancellationToken.None);

        Assert.True(_host.LastActivityUtc > before, "busy-path ptk_state did not stamp LastActivityUtc");
        await slow;
    }

    [Fact]
    public async Task State_reports_server_and_session_basics()
    {
        var state = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: false, CancellationToken.None);

        Assert.Contains($"pid {Environment.ProcessId}", state);
        Assert.Contains("engine: 7", state);
        Assert.Contains("cwd: ", state);
        Assert.Contains("modules loaded", state);
        Assert.Contains("[env drift since server start]", state);
        Assert.Contains("variables: ", state);
    }

    [Fact]
    public async Task Loaded_module_list_grows_after_an_import()
    {
        var before = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: false, CancellationToken.None);
        Assert.DoesNotContain("PtkWarmTest", before);

        await _host.InvokeAsync(
            "New-Module -Name PtkWarmTest -ScriptBlock { function Get-Warm { 'warm' } } | Import-Module");

        var after = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: false, CancellationToken.None);
        Assert.Contains("PtkWarmTest", after);
    }

    [Fact]
    public async Task Available_list_contains_shipped_modules()
    {
        var state = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: true, CancellationToken.None);

        Assert.Contains("Microsoft.PowerShell.Utility", state);
    }

    [Fact]
    public async Task Probing_state_does_not_perturb_the_variable_count()
    {
        var first = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: false, CancellationToken.None);
        var second = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: false, CancellationToken.None);

        static string VariablesLine(string state) =>
            state.Split('\n').First(l => l.StartsWith("variables: ")).Trim();
        Assert.Equal(VariablesLine(first), VariablesLine(second));
    }

    [Fact]
    public async Task Running_jobs_appear_in_the_state_report()
    {
        var before = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: false, CancellationToken.None);
        Assert.Contains("jobs: (none)", before);

        var job = _jobs.Start("Start-Sleep -Seconds 300");
        try
        {
            var during = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: false, CancellationToken.None);
            Assert.Contains($"job {job.Id}: running", during);
        }
        finally
        {
            _jobs.Kill(job.Id);
        }
    }

    [Fact]
    public async Task Probe_failure_is_surfaced_and_not_cached()
    {
        StateTool.ClearAvailableCacheForTests();
        try
        {
            // A session can shadow the very cmdlets the probe uses; the state
            // report must say so rather than present partial output as truth.
            await _host.InvokeAsync("function global:Get-Module { throw 'poisoned by session' }");

            var poisoned = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: true, CancellationToken.None);
            Assert.Contains("[state probe errors]", poisoned);
            Assert.Contains("poisoned by session", poisoned);
            Assert.Contains("probe reported errors (not cached)", poisoned);

            await _host.InvokeAsync("Remove-Item function:Get-Module");

            var healthy = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: true, CancellationToken.None);
            Assert.DoesNotContain("[state probe errors]", healthy);
            Assert.Contains("Microsoft.PowerShell.Utility", healthy);
        }
        finally
        {
            StateTool.ClearAvailableCacheForTests();
        }
    }

    [Fact]
    public async Task NonTerminating_probe_errors_also_block_the_cache()
    {
        StateTool.ClearAvailableCacheForTests();
        try
        {
            // Success=true still carries non-terminating errors: a poisoned
            // Get-Module that Write-Errors AND emits fake data must not be cached.
            await _host.InvokeAsync(
                "function global:Get-Module { Write-Error 'poison'; [pscustomobject]@{ Name = 'FakeModule'; Version = '0.0' } }");

            var poisoned = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: true, CancellationToken.None);
            Assert.Contains("not cached", poisoned);

            await _host.InvokeAsync("Remove-Item function:Get-Module");

            var healthy = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: true, CancellationToken.None);
            Assert.DoesNotContain("FakeModule", healthy);
            Assert.Contains("Microsoft.PowerShell.Utility", healthy);
        }
        finally
        {
            StateTool.ClearAvailableCacheForTests();
        }
    }

    [Fact]
    public async Task Env_drift_reports_session_changes()
    {
        try
        {
            var before = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: false, CancellationToken.None);
            Assert.DoesNotContain("PTK_STATE_DRIFT_PROBE", before);

            await _host.InvokeAsync("$env:PTK_STATE_DRIFT_PROBE = 'polluted'");
            var after = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: false, CancellationToken.None);

            Assert.Contains("added: ", after);
            Assert.Contains("PTK_STATE_DRIFT_PROBE", after);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_STATE_DRIFT_PROBE", null);
        }
    }

    [Fact]
    public async Task Path_drift_reports_an_entry_level_diff()
    {
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            await _host.InvokeAsync(
                "$env:PATH = 'ptk-fake-shim-dir' + [System.IO.Path]::PathSeparator + $env:PATH");

            var state = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: false, CancellationToken.None);

            Assert.Contains("PATH entries added: ptk-fake-shim-dir", state);
            Assert.DoesNotContain("PATH entries removed:", state);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }
}
