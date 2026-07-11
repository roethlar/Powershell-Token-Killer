namespace PtkMcpServer.Tests;

public sealed class RunspaceHostTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60));

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task State_persists_across_calls()
    {
        await _host.InvokeAsync("$x = 41");
        var result = await _host.InvokeAsync("$x + 1");

        Assert.True(result.Success);
        Assert.Equal("42", result.Output.Trim());
        Assert.Equal(InvokeDisposition.Completed, result.Disposition);
        Assert.True(result.UserExecutionStarted);
    }

    [Fact]
    public async Task Imported_module_stays_loaded_across_calls()
    {
        var import = await _host.InvokeAsync(
            "New-Module -Name PtkWarmTest -ScriptBlock { function Get-Warm { 'warm' } } | Import-Module");
        Assert.True(import.Success);

        var result = await _host.InvokeAsync("Get-Warm");

        Assert.True(result.Success);
        Assert.Equal("warm", result.Output.Trim());
    }

    [Fact]
    public async Task Nonterminating_error_surfaces_without_failing_the_call()
    {
        var result = await _host.InvokeAsync("Write-Error 'boom'; 'still ran'");

        Assert.True(result.Success);
        Assert.Contains("still ran", result.Output);
        Assert.Contains(result.Errors, e => e.Contains("boom"));
    }

    [Fact]
    public async Task Terminating_error_fails_the_call_but_host_survives()
    {
        var result = await _host.InvokeAsync("throw 'bang'");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("bang"));
        Assert.Equal(InvokeDisposition.Failed, result.Disposition);
        Assert.True(result.UserExecutionStarted);

        var next = await _host.InvokeAsync("'alive'");
        Assert.True(next.Success);
        Assert.Equal("alive", next.Output.Trim());
    }

    [Fact]
    public async Task Warning_stream_is_captured()
    {
        var result = await _host.InvokeAsync("Write-Warning 'careful'");

        Assert.True(result.Success);
        Assert.Contains(result.Warnings, w => w.Contains("careful"));
    }

    [Fact]
    public async Task Concurrent_calls_are_serialized_not_corrupted()
    {
        await _host.InvokeAsync("$counter = 0");

        var calls = Enumerable.Range(0, 8)
            .Select(_ => _host.InvokeAsync("$counter = $counter + 1"))
            .ToArray();
        await Task.WhenAll(calls);

        var result = await _host.InvokeAsync("$counter");
        Assert.Equal("8", result.Output.Trim());
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_timeout_and_preserves_warm_state()
    {
        await _host.InvokeAsync("$keep = 'still-warm'");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var canceled = await _host.InvokeAsync("Start-Sleep -Seconds 60", cancellationToken: cts.Token);

        Assert.False(canceled.Success);
        Assert.False(canceled.TimedOut);
        Assert.Contains(canceled.Errors, e => e.Contains("cancel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(canceled.Errors, e => e.Contains("timeout", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(InvokeDisposition.Canceled, canceled.Disposition);
        Assert.True(canceled.UserExecutionStarted);

        // The runspace survived the cancel: pre-cancel state is still readable.
        var after = await _host.InvokeAsync("$keep");
        Assert.True(after.Success);
        Assert.Equal("still-warm", after.Output.Trim());
    }

    [Fact]
    public async Task Cancel_during_slow_preflight_preserves_warm_state()
    {
        // The ubuntu CI runner caught this live: a cancel landing while the
        // dialect/routing preflight is still running (slow loaded machine)
        // recycled the warm session. Preflight is not user code and finishes
        // on its own - the cancel must wait it out, not destroy state. The
        // instance-local hook makes the race deterministic without allowing
        // user session state to replace the trusted detector.
        await _host.InvokeAsync("$keep = 'still-warm'");
        _host.PreflightDelayForTests = TimeSpan.FromSeconds(2);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var canceled = await _host.InvokeAsync("'never-runs'", cancellationToken: cts.Token);

        Assert.False(canceled.Success);
        Assert.False(canceled.TimedOut);
        Assert.False(canceled.WarmStateLost);
        Assert.Contains(canceled.Errors, e => e.Contains("cancel", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(InvokeDisposition.NotStarted, canceled.Disposition);
        Assert.False(canceled.UserExecutionStarted);

        _host.PreflightDelayForTests = TimeSpan.Zero;
        var after = await _host.InvokeAsync("$keep", route: "pwsh");
        Assert.True(after.Success);
        Assert.Equal("still-warm", after.Output.Trim());
    }

    [Fact]
    public async Task Timeout_recycles_the_runspace_and_host_survives()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(2));

        await host.InvokeAsync("$x = 'before-timeout'");
        var timedOut = await host.InvokeAsync("Start-Sleep -Seconds 60");

        Assert.False(timedOut.Success);
        Assert.True(timedOut.TimedOut);
        Assert.Equal(InvokeDisposition.OutcomeUnknown, timedOut.Disposition);
        Assert.True(timedOut.UserExecutionStarted);

        // Recycled runspace: host answers again, but pre-timeout state is gone.
        var after = await host.InvokeAsync("if ($null -eq $x) { 'state-cleared' } else { $x }");
        Assert.True(after.Success);
        Assert.Equal("state-cleared", after.Output.Trim());
    }

    [Fact]
    public async Task Dialect_refusal_is_structured_as_not_started()
    {
        var authorizationCalls = 0;
        var refused = await _host.InvokeAsync(
            "export X=1",
            (preparation, cancellationToken) =>
            {
                authorizationCalls++;
                return ValueTask.FromResult(true);
            });

        Assert.False(refused.Success);
        Assert.Contains("[ptk:dialect]", refused.Output);
        Assert.Equal(InvokeDisposition.NotStarted, refused.Disposition);
        Assert.False(refused.UserExecutionStarted);
        Assert.Equal(0, authorizationCalls);
    }

    [Fact]
    public async Task Unconfirmed_stop_is_structured_as_started_with_unknown_outcome()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60))
        {
            ForcePipelineStopFailureForTests = true,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await host.InvokeAsync("Start-Sleep -Seconds 60", cancellationToken: cts.Token);

        Assert.False(result.Success);
        Assert.False(result.TimedOut);
        Assert.True(result.WarmStateLost);
        Assert.Equal(InvokeDisposition.OutcomeUnknown, result.Disposition);
        Assert.True(result.UserExecutionStarted);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("cold-detection")]
    [InlineData("timeout-rebuild")]
    public async Task Post_start_module_file_mutation_cannot_execute_during_repriming(string reprimePath)
    {
        var sourceManifest = RunspaceHost.ResolveModulePath();
        Assert.NotNull(sourceManifest);
        var sourceModule = Path.ChangeExtension(sourceManifest, ".psm1");
        Assert.True(File.Exists(sourceModule));

        var moduleDirectory = Directory.CreateTempSubdirectory("ptk-frozen-module-");
        try
        {
            var manifest = Path.Combine(moduleDirectory.FullName, "PwshTokenCompressor.psd1");
            var module = Path.Combine(moduleDirectory.FullName, "PwshTokenCompressor.psm1");
            var sentinel = Path.Combine(moduleDirectory.FullName, "module-side-effect.txt");
            File.Copy(sourceManifest, manifest);
            File.Copy(sourceModule, module);

            using var host = new RunspaceHost(
                callTimeout: TimeSpan.FromMilliseconds(500),
                modulePathOverride: manifest);
            Assert.True(host.ModuleLoaded);

            // Model the exact attack: one authorized user pipeline replaces the
            // on-disk module with top-level code. No later module load may read
            // those mutable bytes before a subsequent dispatch authorization.
            var maliciousSource =
                $"[IO.File]::WriteAllText('{PowerShellLiteral(sentinel)}', 'executed')";
            var encodedSource = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(maliciousSource));
            var mutation = await host.InvokeAsync(
                $"[IO.File]::WriteAllBytes('{PowerShellLiteral(module)}', " +
                $"[Convert]::FromBase64String('{encodedSource}'))",
                (_, _) => ValueTask.FromResult(true),
                raw: true,
                route: "pwsh");
            Assert.True(mutation.Success, string.Join(Environment.NewLine, mutation.Errors));

            switch (reprimePath)
            {
                case "reset":
                    await host.ResetAsync();
                    break;
                case "cold-detection":
                    _ = await host.TryGetBackgroundDialectRefusalAsync("export PTK_TEST=1");
                    break;
                case "timeout-rebuild":
                    var timedOut = await host.InvokeAsync(
                        "Start-Sleep -Seconds 10",
                        (_, _) => ValueTask.FromResult(true),
                        route: "pwsh");
                    Assert.True(timedOut.TimedOut);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(reprimePath));
            }

            var refused = await host.InvokeAsync(
                "'must-not-run'",
                (_, _) => ValueTask.FromResult(false),
                route: "pwsh");
            Assert.Equal(InvokeDisposition.NotStarted, refused.Disposition);
            Assert.False(refused.UserExecutionStarted);
            Assert.False(
                File.Exists(sentinel),
                $"mutable module source executed through {reprimePath}");
        }
        finally
        {
            moduleDirectory.Delete(recursive: true);
        }
    }

    private static string PowerShellLiteral(string value) => value.Replace("'", "''");
}
