using System.ComponentModel;
using System.Reflection;
using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

// Raw-usage visibility and posture (shell-dialect plan D2, slice 3): raw=true
// is counted at the ptk_invoke user-call boundary only, surfaces in
// ptk_state, and every model-visible description reads as recovery-only.
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
        // ptk_state's own probes and the cwd probe before a background job
        // pass raw:true to the host — plumbing, not a user raw call (plan
        // D2: counted at the user-call boundary only).
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
        // sd3-2: the elision marker advises raw=true, which ptk_job does not
        // have; an elided poll must name the honest recovery — the raw log —
        // in-band. This also pins the JobTool↔marker wording coupling: a
        // marker reword that silently drops the note fails here.
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
        Assert.Contains("elided - read the complete raw log at", poll);
        Assert.Contains("job-", poll); // the raw log path is named
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
        // "raw log" catches any form of fabricated log-recovery advice —
        // the current hint phrasing and the earlier appended-note phrasing
        // alike; the job's own printed text contains neither.
        Assert.DoesNotContain("raw log", poll);
    }

    private static string DescriptionOf(ICustomAttributeProvider member) =>
        ((DescriptionAttribute)member.GetCustomAttributes(typeof(DescriptionAttribute), false).Single()).Description;

    [Fact]
    public void Invoke_descriptions_teach_recovery_only_raw_and_the_pwsh_pairing()
    {
        // The D2 reword is all-or-nothing: a surface drifting back to
        // "returns full uncompressed output" wins over the quieter ones
        // (plan D2 — partial rewording leaves the louder surface winning).
        var invoke = typeof(InvokeTool).GetMethod(nameof(InvokeTool.Invoke))!;
        var tool = DescriptionOf(invoke);
        Assert.Contains("recovering detail the compressed form lost", tool);
        Assert.Contains("not as a default", tool);
        Assert.Contains("route=pwsh with raw=false", tool);
        Assert.DoesNotContain("full uncompressed output", tool);

        var rawParam = invoke.GetParameters().Single(p => p.Name == "raw");
        var raw = ((DescriptionAttribute)rawParam.GetCustomAttributes(typeof(DescriptionAttribute), false).Single()).Description;
        Assert.Contains("Recovery hatch, not a default", raw);
        // sd3-1: the FULL pairing, both halves — "route=pwsh" alone leaves
        // the raw=false half untaught on the one surface aimed squarely at
        // the fidelity habit.
        Assert.Contains("route=pwsh with raw=false", raw);
        Assert.DoesNotContain("full uncompressed output", raw);
    }
}
