using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class JobTool
{
    [McpServerTool(Name = "ptk_job")]
    [Description(
        "Manage background jobs started by ptk_invoke background=true. " +
        "action=output returns new output since the given offset, shaped and " +
        "bounded, and ends with the next offset to pass on the following poll; " +
        "action=status reports run state, exit code, and the log path; kill stops " +
        "a job (and its process tree); list shows all jobs. Jobs are cold child " +
        "processes with no warm session state; the complete raw output lives in " +
        "the job's log file. Jobs are killed by ptk_reset and at server shutdown.")]
    public static async Task<string> Job(
        RunspaceHost host,
        JobManager jobs,
        [Description("status | output | kill | list")] string action,
        CancellationToken cancellationToken,
        [Description("Job id (required for status/output/kill).")] int id = 0,
        [Description("Byte offset for action=output: pass the previous poll's 'next offset'; 0 reads from the start.")] long offset = 0)
    {
        switch (action?.ToLowerInvariant())
        {
            case "list":
            {
                var all = jobs.List();
                return all.Length == 0 ? "(no jobs)" : string.Join('\n', all.Select(Describe));
            }
            case "status":
            {
                var snapshot = jobs.Snapshot(id);
                return snapshot is null ? $"[no such job: {id}]" : Describe(snapshot);
            }
            case "kill":
            {
                return jobs.Kill(id)
                    ? $"[job {id} killed]"
                    : $"[no such job or already exited: {id}]";
            }
            case "output":
            {
                var snapshot = jobs.Snapshot(id);
                if (snapshot is null) return $"[no such job: {id}]";
                // Snapshot BEFORE reading: a job that exits mid-poll reports
                // "running" one poll late rather than losing tail output.
                var chunk = jobs.ReadOutput(id, offset)!;
                var (text, nextOffset) = chunk.Value;
                // sd3-2..sd3-4: the marker's default advice (raw=true) is a
                // ptk_invoke control this tool does not have — re-running
                // the JOB duplicates side-effecting work and the offset has
                // already moved past the middle. The recovery hint rides
                // INTO shaping, so the marker itself names the honest
                // recovery (the raw log) exactly when the module elides;
                // two downstream inference heuristics both proved unsound
                // (ANSI stripping shortens without eliding, near-boundary
                // elision lengthens).
                var shaped = text.Length > 0
                    ? await host.ShapeTextAsync(text, cancellationToken,
                        elisionHint: $"read the complete raw log at {snapshot.OutputPath} if the elided middle matters")
                    : "(no new output)";
                var state = snapshot.Running ? "running" : $"exited {snapshot.ExitCode}";
                return $"{shaped}\n[job {id} {state}] next offset: {nextOffset}";
            }
            default:
                return "[unknown action - use status | output | kill | list]";
        }
    }

    private static string Describe(JobSnapshot snapshot)
    {
        var state = snapshot.Running ? $"running (pid {snapshot.Pid})" : $"exited {snapshot.ExitCode}";
        var script = snapshot.Script.Replace('\r', ' ').Replace('\n', ' ');
        if (script.Length > 100) script = script[..97] + "...";
        return $"job {snapshot.Id}: {state}, started {snapshot.StartedUtc:HH:mm:ss}Z, log: {snapshot.OutputPath}\n  script: {script}";
    }
}
