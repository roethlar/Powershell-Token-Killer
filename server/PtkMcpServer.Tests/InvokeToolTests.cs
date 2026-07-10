using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

// ProcessEnvironment collection: mutates PTK_RTK_PATH, which a parallel
// reset-driven environment restore would otherwise wipe mid-test.
[Collection("ProcessEnvironment")]
public sealed class InvokeToolTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60));
    private readonly JobManager _jobs = new(
        Path.Combine(Path.GetTempPath(), "ptk-invoke-jobs-" + Guid.NewGuid().ToString("N")));
    private readonly RawUsageCounter _rawUsage = new();

    public void Dispose()
    {
        _host.Dispose();
        _jobs.Dispose();
    }

    [Fact]
    public async Task Returns_plain_output_for_a_clean_call()
    {
        var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "'hello from warm runspace'", CancellationToken.None);

        Assert.Contains("hello from warm runspace", text);
        Assert.DoesNotContain("[errors]", text);
        Assert.DoesNotContain("[warnings]", text);
    }

    [Fact]
    public async Task State_persists_across_tool_calls()
    {
        await InvokeTool.Invoke(_host, _jobs, _rawUsage, "$warm = 41", CancellationToken.None);
        var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "$warm + 1", CancellationToken.None);

        Assert.Contains("42", text);
    }

    [Fact]
    public async Task Errors_and_warnings_are_reported_in_labelled_sections()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage, "Write-Warning 'careful'; Write-Error 'boom'; 'partial'", CancellationToken.None);

        Assert.Contains("partial", text);
        Assert.Contains("[errors]", text);
        Assert.Contains("boom", text);
        Assert.Contains("[warnings]", text);
        Assert.Contains("careful", text);
    }

    [Fact]
    public async Task Empty_output_says_so_instead_of_returning_nothing()
    {
        var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "$null", CancellationToken.None);

        Assert.Contains("(no output)", text);
    }

    private static string NativeExit(int code) =>
        OperatingSystem.IsWindows() ? $"cmd /c exit {code}" : $"sh -c 'exit {code}'";

    [Fact]
    public async Task Native_nonzero_exit_code_is_reported()
    {
        var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, NativeExit(7), CancellationToken.None);

        Assert.Contains("[exit] 7", text);
    }

    [Fact]
    public async Task Native_zero_exit_code_is_not_reported()
    {
        var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, NativeExit(0), CancellationToken.None);

        Assert.DoesNotContain("[exit]", text);
    }

    [Fact]
    public async Task Stale_exit_code_is_not_reported_against_a_later_pure_PowerShell_call()
    {
        await InvokeTool.Invoke(_host, _jobs, _rawUsage, NativeExit(7), CancellationToken.None);
        var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "'clean call'", CancellationToken.None);

        Assert.Contains("clean call", text);
        Assert.DoesNotContain("[exit]", text);
    }

    [Fact]
    public async Task Native_exit_code_survives_rtk_log_shaping()
    {
        // Round-2 review repro: log-shaped output routes through the module's
        // native rtk leg, whose own exit code must not replace the script's.
        var stubDir = Directory.CreateTempSubdirectory("ptk-rtk-stub-");
        string stubPath;
        if (OperatingSystem.IsWindows())
        {
            stubPath = Path.Combine(stubDir.FullName, "rtk-stub.cmd");
            File.WriteAllText(stubPath, "@echo off\r\necho RTKSTUB shaped\r\nexit /b 0\r\n");
        }
        else
        {
            stubPath = Path.Combine(stubDir.FullName, "rtk-stub.sh");
            File.WriteAllText(stubPath, "#!/bin/sh\necho 'RTKSTUB shaped'\nexit 0\n");
            File.SetUnixFileMode(stubPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stubPath);
            var script =
                "1..8 | ForEach-Object { \"2026-07-03 10:00:0$_ ERROR worker: step $_ failed\" }; "
                + NativeExit(7);
            var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, script, CancellationToken.None);

            Assert.Contains("[ptk:log via rtk]", text);
            Assert.Contains("[exit] 7", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            stubDir.Delete(recursive: true);
        }
    }

    private static (DirectoryInfo dir, string path) CreateRtkStub(string body)
    {
        var dir = Directory.CreateTempSubdirectory("ptk-rtk-route-");
        string path;
        if (OperatingSystem.IsWindows())
        {
            path = Path.Combine(dir.FullName, "rtk-stub.cmd");
            File.WriteAllText(path, "@echo off\r\n" + body.Replace("\n", "\r\n") + "\r\n");
        }
        else
        {
            path = Path.Combine(dir.FullName, "rtk-stub.sh");
            File.WriteAllText(path,
                "#!/bin/sh\n" + body.Replace("%*", "\"$@\"").Replace("exit /b ", "exit ") + "\n");
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return (dir, path);
    }

    [Fact]
    public async Task Single_native_command_routes_through_rtk()
    {
        var (dir, stub) = CreateRtkStub("echo RTKROUTE %*\nexit /b 0");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "git status", CancellationToken.None);

            Assert.Contains("RTKROUTE git status", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Route_pwsh_forces_plain_execution()
    {
        var (dir, stub) = CreateRtkStub("echo RTKROUTE %*\nexit /b 0");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var text = await InvokeTool.Invoke(
                _host, _jobs, _rawUsage, "git --version", CancellationToken.None, route: "pwsh");

            Assert.DoesNotContain("RTKROUTE", text);
            Assert.Contains("git version", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Routed_command_exit_code_is_reported()
    {
        var (dir, stub) = CreateRtkStub("echo RTKROUTE %*\nexit /b 5");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "git status", CancellationToken.None);

            Assert.Contains("RTKROUTE git status", text);
            Assert.Contains("[exit] 5", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Timeout_is_reported_with_the_state_loss_warning()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(2));

        var text = await InvokeTool.Invoke(host, _jobs, _rawUsage, "Start-Sleep -Seconds 60", CancellationToken.None);

        Assert.Contains("[errors]", text);
        Assert.Contains("timeout", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recycled", text);
    }

    [Fact]
    public async Task Timeout_error_teaches_both_recovery_paths()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(2));

        var text = await InvokeTool.Invoke(host, _jobs, _rawUsage, "Start-Sleep -Seconds 60", CancellationToken.None);

        Assert.Contains("background=true", text);
        Assert.Contains("timeoutSeconds", text);
        // Live use lost a debugging detour to changed resolution after a
        // recycle (v2-feedback slice 3): the message must point at ptk_state.
        Assert.Contains("ptk_state", text);
    }

    [Fact]
    public async Task Rtk_install_nag_is_filtered_but_real_stderr_survives()
    {
        // Per-OS stubs built explicitly: sh's echo may process the backslash
        // in the nag's /!\ marker, so the Unix leg uses printf with a quoted
        // literal (same pattern as the log-shaping stub above).
        var dir = Directory.CreateTempSubdirectory("ptk-rtk-nag-");
        string stub;
        if (OperatingSystem.IsWindows())
        {
            stub = Path.Combine(dir.FullName, "rtk-stub.cmd");
            File.WriteAllText(stub,
                "@echo off\r\n" +
                "echo [rtk] /!\\ No hook installed - run rtk init 1>&2\r\n" +
                "echo [rtk] /!\\ unexpected-real-diagnostic 1>&2\r\n" +
                "echo real-stderr-detail 1>&2\r\n" +
                "echo RTKROUTE %*\r\n" +
                "exit /b 0\r\n");
        }
        else
        {
            stub = Path.Combine(dir.FullName, "rtk-stub.sh");
            File.WriteAllText(stub,
                "#!/bin/sh\n" +
                "printf '%s\\n' '[rtk] /!\\ No hook installed - run rtk init' 1>&2\n" +
                "printf '%s\\n' '[rtk] /!\\ unexpected-real-diagnostic' 1>&2\n" +
                "echo real-stderr-detail 1>&2\n" +
                "echo \"RTKROUTE $@\"\n" +
                "exit 0\n");
            File.SetUnixFileMode(stub,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "git status", CancellationToken.None);

            Assert.Contains("RTKROUTE git status", text);
            Assert.DoesNotContain("No hook installed", text);
            Assert.Contains("real-stderr-detail", text);
            // Only the specific banner is filtered - an rtk-prefixed line that
            // is NOT the nag is a real diagnostic and must survive (v2fb-1).
            Assert.Contains("unexpected-real-diagnostic", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Per_call_timeoutSeconds_overrides_the_default()
    {
        // Default is 60s here; the 1s override must fire first (a broken
        // override would let the sleep run 8s and return success).
        var result = await _host.InvokeAsync("Start-Sleep -Seconds 8", timeoutSeconds: 1);

        Assert.False(result.Success);
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task A_wedged_shaping_call_cannot_hold_the_gate_forever()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(2));
        // A .ps1 rtk stub runs IN the warm runspace, so its sleep wedges the
        // shaping pipeline exactly like a hung rtk child would.
        var dir = Directory.CreateTempSubdirectory("ptk-wedge-stub-");
        var stub = Path.Combine(dir.FullName, "rtk-sleep.ps1");
        File.WriteAllText(stub, "param($verb, $path) Start-Sleep -Seconds 120");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var logShaped = string.Join('\n', Enumerable.Range(1, 8)
                .Select(i => $"2026-07-08 10:00:0{i % 10} INFO worker: step {i}"));

            var shaped = await host.ShapeTextAsync(logShaped);

            Assert.Contains("step 1", shaped);
            Assert.Contains("shaping timed out", shaped);

            var after = await host.InvokeAsync("1 + 1");
            Assert.True(after.Success);
            Assert.Contains("2", after.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    // Issue #5 matrix: native stderr is neutral [stderr]; [errors] is reserved
    // for genuine PowerShell error records. The partition predicate is
    // invocation provenance (Application command), not the forgeable FQID or
    // exception type.
    private static string NativeStderr(string message, int exit = 0) =>
        OperatingSystem.IsWindows()
            ? $"cmd /c \"echo {message} 1>&2 & exit /b {exit}\""
            : $"sh -c 'echo {message} 1>&2; exit {exit}'";

    [Fact]
    public async Task Successful_native_stderr_is_neutral_not_an_error()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage, NativeStderr("normal diagnostic"), CancellationToken.None);

        Assert.Contains("[stderr]", text);
        Assert.Contains("normal diagnostic", text);
        Assert.DoesNotContain("[errors]", text);
        Assert.DoesNotContain("[exit]", text);
    }

    [Fact]
    public async Task Native_stderr_with_nonzero_exit_keeps_both_sections()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage, NativeStderr("failing diagnostic", exit: 7), CancellationToken.None);

        Assert.Contains("[stderr]", text);
        Assert.Contains("failing diagnostic", text);
        Assert.Contains("[exit] 7", text);
    }

    [Fact]
    public async Task Native_stderr_labeling_is_consistent_across_raw_and_route()
    {
        var raw = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage, NativeStderr("raw diagnostic"), CancellationToken.None, raw: true);
        Assert.Contains("[stderr]", raw);
        Assert.DoesNotContain("[errors]", raw);

        var routed = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage, NativeStderr("routed diagnostic"), CancellationToken.None, route: "pwsh");
        Assert.Contains("[stderr]", routed);
        Assert.DoesNotContain("[errors]", routed);
    }

    [Fact]
    public async Task Forged_native_error_id_stays_under_errors()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage,
            "Write-Error -ErrorId NativeCommandError -Message forged-id", CancellationToken.None);

        Assert.Contains("[errors]", text);
        Assert.Contains("forged-id", text);
        Assert.DoesNotContain("[stderr]", text);
    }

    [Fact]
    public async Task Forged_native_exception_stays_under_errors()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage,
            "Write-Error -Exception ([System.Management.Automation.RemoteException]::new('forged-ex'))",
            CancellationToken.None);

        Assert.Contains("[errors]", text);
        Assert.Contains("forged-ex", text);
        Assert.DoesNotContain("[stderr]", text);
    }

    [Fact]
    public async Task Combined_forged_id_and_exception_stays_under_errors()
    {
        // An FQID+exception classifier passes the isolated forgeries but not
        // this one; only invocation provenance holds (plan finding i56p-11).
        var text = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage,
            "Write-Error -ErrorId NativeCommandError -Exception ([System.Management.Automation.RemoteException]::new('forged-both'))",
            CancellationToken.None);

        Assert.Contains("[errors]", text);
        Assert.Contains("forged-both", text);
        Assert.DoesNotContain("[stderr]", text);
    }

    [Fact]
    public async Task Terminating_native_error_path_keeps_exit_code_and_stderr()
    {
        // $PSNativeCommandUseErrorActionPreference + Stop routes a nonzero-exit
        // native command through the RuntimeException catch, which previously
        // dropped [exit] N (plan finding i56p-6).
        var script =
            "$ErrorActionPreference = 'Stop'; $PSNativeCommandUseErrorActionPreference = $true; " +
            NativeStderr("terminating diagnostic", exit: 7);
        var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, script, CancellationToken.None);

        Assert.Contains("[exit] 7", text);
        Assert.Contains("[stderr]", text);
        Assert.Contains("terminating diagnostic", text);
        Assert.Contains("[errors]", text); // the terminating record itself is a genuine error
    }

    [Fact]
    public async Task Background_starts_a_job_and_its_output_is_pollable()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage, "'hello from a ptk job'", CancellationToken.None, background: true);

        Assert.Contains("[job 1 started]", text);
        Assert.Contains("ptk_job", text);

        // Poll until the job exits and its output lands (cold pwsh start is
        // the slow part; 60s is generous).
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string poll;
        do
        {
            await Task.Delay(250);
            poll = await JobTool.Job(_host, _jobs, "output", CancellationToken.None, id: 1, offset: 0);
        } while (!poll.Contains("exited 0") && DateTime.UtcNow < deadline);

        Assert.Contains("hello from a ptk job", poll);
        Assert.Contains("exited 0", poll);
        Assert.Contains("next offset:", poll);
    }
}
