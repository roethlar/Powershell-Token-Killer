using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

// Shares the ProcessEnvironment collection: reset restores the process-wide
// environment baseline, which would wipe env vars a parallel test class set.
[Collection("ProcessEnvironment")]
public sealed class ResetToolTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60));

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Reset_clears_variables_and_loaded_modules()
    {
        await _host.InvokeAsync("$x = 'precious'");
        await _host.InvokeAsync(
            "New-Module -Name PtkWarmTest -ScriptBlock { function Get-Warm { 'warm' } } | Import-Module");

        var message = await ResetTool.Reset(_host, CancellationToken.None);
        Assert.Contains("recycled", message);

        var variable = await _host.InvokeAsync("if ($null -eq $x) { 'gone' } else { $x }");
        Assert.Equal("gone", variable.Output.Trim());

        var state = await StateTool.State(_host, listAvailable: false, CancellationToken.None);
        Assert.DoesNotContain("PtkWarmTest", state);
    }

    [Fact]
    public async Task Reset_restores_the_environment_baseline()
    {
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            await _host.InvokeAsync("$env:PTK_RESET_DRIFT_PROBE = 'polluted'");
            await _host.InvokeAsync(
                "$env:PATH = 'ptk-fake-shim-dir' + [System.IO.Path]::PathSeparator + $env:PATH");
            Assert.Equal("polluted", Environment.GetEnvironmentVariable("PTK_RESET_DRIFT_PROBE"));

            await ResetTool.Reset(_host, CancellationToken.None);

            Assert.Null(Environment.GetEnvironmentVariable("PTK_RESET_DRIFT_PROBE"));
            Assert.Equal(savedPath, Environment.GetEnvironmentVariable("PATH"));

            var state = await StateTool.State(_host, listAvailable: false, CancellationToken.None);
            Assert.DoesNotContain("PTK_RESET_DRIFT_PROBE", state);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RESET_DRIFT_PROBE", null);
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }
}
