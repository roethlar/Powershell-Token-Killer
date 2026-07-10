using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

// Server wiring for the shell-dialect detector (shell-dialect plan, slice 2):
// a probed bash-only construct is refused fast with recovery guidance on BOTH
// execution paths — foreground before routing, background before the job
// starts — while raw=true and route=pwsh bypass by consent. ProcessEnvironment
// collection: one test mutates PTK_RTK_PATH.
[Collection("ProcessEnvironment")]
public sealed class ShellDialectWiringTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60));
    private readonly JobManager _jobs = new(
        Path.Combine(Path.GetTempPath(), "ptk-dialect-jobs-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        _host.Dispose();
        _jobs.Dispose();
    }

    [Fact]
    public async Task Foreground_bash_only_script_is_refused_and_nothing_executes()
    {
        // The probe file proves refusal means non-execution: undetected, this
        // script would fail on `export` but still create the file.
        var probe = Path.Combine(Path.GetTempPath(), "ptk-dialect-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var text = await InvokeTool.Invoke(
                _host, _jobs, $"export X=1; New-Item -ItemType File -Path '{probe}'", CancellationToken.None);

            Assert.Contains("[ptk:dialect] not executed", text);
            Assert.Contains("the bash 'export' builtin", text);
            Assert.Contains("PowerShell", text);
            Assert.DoesNotContain("[errors]", text);
            Assert.False(File.Exists(probe));
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
        }
    }

    [Fact]
    public async Task Parse_fatal_bash_shape_is_refused_with_its_construct_named()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, "cat <<EOF\nhello\nEOF", CancellationToken.None);

        Assert.Contains("[ptk:dialect] not executed", text);
        Assert.Contains("heredoc", text);
    }

    [Fact]
    public async Task Shared_dialect_shapes_are_not_refused()
    {
        // The plan's false-positive principle at the wiring level: shapes both
        // dialects own must run exactly as before.
        var text = await InvokeTool.Invoke(
            _host, _jobs, "echo hi && echo there", CancellationToken.None);

        Assert.DoesNotContain("[ptk:dialect]", text);
        Assert.Contains("hi", text);
        Assert.Contains("there", text);
    }

    [Fact]
    public async Task Raw_true_bypasses_detection_as_consent()
    {
        var result = await _host.InvokeAsync("export X=1", raw: true);

        // Executed exactly as written: today's late CommandNotFound failure,
        // never a refusal.
        Assert.DoesNotContain("[ptk:dialect]", result.Output);
        Assert.Contains(result.Errors, e => e.Contains("export"));
    }

    [Fact]
    public async Task Route_pwsh_bypasses_detection_as_consent()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, "export X=1", CancellationToken.None, route: "pwsh");

        Assert.DoesNotContain("[ptk:dialect]", text);
        Assert.Contains("[errors]", text);
        Assert.Contains("export", text);
    }

    [Fact]
    public async Task Warm_session_definition_exempts_foreground_but_not_background()
    {
        // Resolution context must match execution context (plan slice 1(iii)).
        await _host.InvokeAsync("function export { param($Assignment) \"shadow:$Assignment\" }");

        var foreground = await _host.InvokeAsync("export X=1");
        Assert.True(foreground.Success);
        Assert.Contains("shadow:X=1", foreground.Output);

        // The same script as a background job runs in a cold child where the
        // warm function does not exist — it must still be refused.
        var backgroundText = await InvokeTool.Invoke(
            _host, _jobs, "export X=1", CancellationToken.None, background: true);
        Assert.Contains("[ptk:dialect] job not started", backgroundText);
        Assert.Empty(_jobs.List());
    }

    [Fact]
    public async Task Background_detected_script_is_refused_before_any_job_starts()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, "export X=1", CancellationToken.None, background: true);

        Assert.Contains("[ptk:dialect] job not started", text);
        Assert.DoesNotContain("[job", text);
        Assert.Empty(_jobs.List());
    }

    [Fact]
    public async Task Background_raw_bypasses_detection_and_starts_the_job()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, "export X=1", CancellationToken.None, raw: true, background: true);

        Assert.DoesNotContain("[ptk:dialect]", text);
        Assert.Contains("[job 1 started]", text);
        Assert.Single(_jobs.List());
    }

    [Fact]
    public async Task Detection_fires_with_rtk_absent()
    {
        // Plan slice 1(i)/Verification: detection cannot depend on the rtk
        // routing leg being present — an explicitly-set-but-missing
        // PTK_RTK_PATH disables rtk entirely.
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable(
                "PTK_RTK_PATH", Path.Combine(Path.GetTempPath(), "no-such-rtk-" + Guid.NewGuid().ToString("N")));
            var text = await InvokeTool.Invoke(_host, _jobs, "export X=1", CancellationToken.None);

            Assert.Contains("[ptk:dialect] not executed", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
        }
    }

    [Fact]
    public async Task Moduleless_host_skips_detection_on_both_paths()
    {
        // No module means no detector; both paths degrade to today's behavior
        // rather than refusing or blocking.
        using var host = new RunspaceHost(
            callTimeout: TimeSpan.FromSeconds(60),
            modulePathOverride: Path.Combine(Path.GetTempPath(), "no-such-module.psd1"));

        var foreground = await host.InvokeAsync("export X=1");
        Assert.DoesNotContain("[ptk:dialect]", foreground.Output);

        var backgroundText = await InvokeTool.Invoke(
            host, _jobs, "export X=1", CancellationToken.None, background: true);
        Assert.Contains("[job 1 started]", backgroundText);
        Assert.Single(_jobs.List());
    }

    [Fact]
    public async Task Refusal_recovery_guidance_is_platform_aware()
    {
        var result = await _host.InvokeAsync("export X=1");
        Assert.False(result.Success);

        var bashProbe = await _host.InvokeAsync(
            "$null -ne $ExecutionContext.InvokeCommand.GetCommand('bash', [System.Management.Automation.CommandTypes]::Application)",
            raw: true);
        var bashAvailable = bashProbe.Output.Trim()
            .Equals("True", StringComparison.OrdinalIgnoreCase);

        if (bashAvailable)
        {
            // Both recovery paths, with the apostrophe-escaping note.
            Assert.Contains("Rewrite it in PowerShell", result.Output);
            Assert.Contains("bash -lc", result.Output);
            Assert.Contains("'\\''", result.Output);
        }
        else
        {
            // A wrap that cannot run here is not offered.
            Assert.Contains("Rewrite it in PowerShell", result.Output);
            Assert.DoesNotContain("bash -lc", result.Output);
        }
    }
}
