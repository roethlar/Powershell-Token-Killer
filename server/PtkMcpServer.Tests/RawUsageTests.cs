using System.ComponentModel;
using System.Reflection;
using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

// Deprecated raw compatibility telemetry: raw=true is inert but remains
// counted at the ptk_invoke user boundary and visible in ptk_state until the
// next breaking tool-schema revision removes it.
public sealed class RawUsageTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60));
    private readonly JobManager _jobs = new(
        Path.Combine(Path.GetTempPath(), "ptk-rawusage-jobs-" + Guid.NewGuid().ToString("N")));
    private readonly RawUsageCounter _rawUsage = new();

    public void Dispose()
    {
        _host.Dispose();
        _jobs.Dispose();
    }

    [Fact]
    public async Task User_raw_call_increments_exactly_once_and_surfaces_in_state()
    {
        // A permanently zero counter must fail this battery, not satisfy it
        // (plan Verification): the positive leg is exact — once per call.
        await InvokeTool.Invoke(_host, _jobs, _rawUsage, "'first'", CancellationToken.None, raw: true);
        Assert.Equal(1, _rawUsage.Count);

        await InvokeTool.Invoke(_host, _jobs, _rawUsage, "'second'", CancellationToken.None, raw: true);
        Assert.Equal(2, _rawUsage.Count);

        var state = await StateTool.State(_host, _jobs, _rawUsage, listAvailable: false, CancellationToken.None);
        Assert.Contains("raw calls this session: 2", state);
    }

    [Fact]
    public async Task Raw_call_emits_the_server_log_line()
    {
        // Console.Error is process-global; the setter wraps the writer in a
        // synchronized TextWriter, and the assertion is Contains, so
        // unrelated concurrent stderr lines cannot break it.
        var original = Console.Error;
        var capture = new StringWriter();
        Console.SetError(capture);
        try
        {
            await InvokeTool.Invoke(_host, _jobs, _rawUsage, "'logged'", CancellationToken.None, raw: true);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Contains("ptk: raw=true call #1 this session", capture.ToString());
    }

    [Fact]
    public async Task Internal_probes_never_inflate_the_raw_counter()
    {
        // State/cwd probes are internal implementation work, not compatibility
        // requests. Only an explicit user raw=true reaches this counter.
        await StateTool.State(_host, _jobs, _rawUsage, listAvailable: false, CancellationToken.None);
        Assert.Equal(0, _rawUsage.Count);

        var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "'bg work'", CancellationToken.None, background: true);
        Assert.Contains("[job 1 started]", text);
        Assert.Equal(0, _rawUsage.Count);

        var nonRaw = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "'plain'", CancellationToken.None);
        Assert.Contains("plain", nonRaw);
        Assert.Equal(0, _rawUsage.Count);
    }

    [Fact]
    public async Task Oversized_job_poll_names_the_log_as_the_recovery_not_raw()
    {
        // sd3-2: an elided poll must override the foreground marker default
        // with its honest existing recovery — the job log — in-band. This
        // also pins the JobTool↔marker wording coupling: a marker reword that
        // silently drops the note fails here.
        var started = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage,
            "1..3000 | ForEach-Object { \"job line $_ \" + ('z' * 20) }",
            CancellationToken.None, background: true);
        Assert.Contains("[job 1 started]", started);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        string status;
        do
        {
            await Task.Delay(250);
            status = await JobTool.Job(_host, _jobs, "status", CancellationToken.None, id: 1);
        } while (status.Contains("running") && DateTime.UtcNow < deadline);

        var poll = await JobTool.Job(_host, _jobs, "output", CancellationToken.None, id: 1, offset: 0);

        // The marker itself carries the job-context recovery (sd3-2..sd3-4:
        // the hint rides into shaping; nothing is inferred downstream).
        Assert.Contains(
            "elided - read the available captured log (completeness unknown) at",
            poll);
        Assert.Contains("job-", poll); // the captured log path is named
        Assert.DoesNotContain("rerun with raw=true", poll);
    }

    [Fact]
    public async Task Job_printed_marker_text_gets_no_false_recovery_note()
    {
        // sd3-3: a job whose OWN output contains marker text (a grep over
        // this repo's source, a cat of previously elided output — plain or
        // ANSI-colored, the tail that broke the length heuristic) must not
        // yield job-context recovery advice on an under-limit poll: the
        // module composes that advice only when IT elides.
        var started = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage,
            "Write-Output '[5 lines elided - rerun with raw=true only if the elided middle matters]'\n" +
            "Write-Output \"$([char]27)[31m[6 lines elided - rerun with raw=true only if the elided middle matters]$([char]27)[0m\"",
            CancellationToken.None, background: true);
        Assert.Contains("[job 1 started]", started);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        string status;
        do
        {
            await Task.Delay(250);
            status = await JobTool.Job(_host, _jobs, "status", CancellationToken.None, id: 1);
        } while (status.Contains("running") && DateTime.UtcNow < deadline);

        var poll = await JobTool.Job(_host, _jobs, "output", CancellationToken.None, id: 1, offset: 0);

        Assert.Contains("elided", poll); // the job's own text came through
        // "captured log" catches any form of fabricated log-recovery advice —
        // the current hint phrasing and the earlier appended-note phrasing
        // alike; the job's own printed text contains neither.
        Assert.DoesNotContain("captured log", poll);
    }

    private static string DescriptionOf(ICustomAttributeProvider member) =>
        ((DescriptionAttribute)member.GetCustomAttributes(typeof(DescriptionAttribute), false).Single()).Description;

    [Fact]
    public void Invoke_descriptions_teach_same_invocation_recovery_and_inert_legacy_raw()
    {
        var invoke = typeof(InvokeTool).GetMethod(nameof(InvokeTool.Invoke))!;
        var tool = DescriptionOf(invoke);
        Assert.Contains("Output is normally shaped", tool);
        Assert.Contains("ptk_output handle", tool);
        Assert.Contains("same invocation", tool);
        Assert.Contains("never reruns", tool);
        Assert.Contains("legacy raw is deprecated", tool);
        Assert.Contains("does not change interpreter, routing, capture, or shaping", tool);
        Assert.DoesNotContain("Recovery hatch", tool);
        Assert.DoesNotContain("route=pwsh with raw=false", tool);
        Assert.DoesNotContain("full uncompressed output", tool);
        Assert.Contains("outside the runspace", tool);
        Assert.Contains("delegated Bash state is process-local", tool);
        Assert.Contains("preserves warm state", tool);
        Assert.DoesNotContain(
            "only a call that overruns while executing is aborted with the runspace recycled",
            tool);

        var rawParam = invoke.GetParameters().Single(p => p.Name == "raw");
        var raw = ((DescriptionAttribute)rawParam.GetCustomAttributes(typeof(DescriptionAttribute), false).Single()).Description;
        Assert.Equal(typeof(bool), rawParam.ParameterType);
        Assert.True(rawParam.HasDefaultValue);
        Assert.Equal(false, rawParam.DefaultValue);
        Assert.Contains("Deprecated compatibility flag", raw);
        Assert.Contains(
            "no effect on dialect handling, interpreter/routing, process choice, capture, or shaping",
            raw);
        Assert.Contains("Use ptk_output when a handle is returned", raw);
        Assert.DoesNotContain("Recovery hatch", raw);
        Assert.DoesNotContain("route=pwsh with raw=false", raw);
        Assert.DoesNotContain("full uncompressed output", raw);

        var routeParam = invoke.GetParameters().Single(p => p.Name == "route");
        var route = DescriptionOf(routeParam);
        Assert.Contains(
            "explicit consent to interpret the exact original text as PowerShell",
            route);
        Assert.Contains("normal capture and shaping still apply", route);
        Assert.DoesNotContain("raw=false", route);
    }
}
