using System.ComponentModel;
using ModelContextProtocol.Server;
using PtkMcpServer.Audit;

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
        "processes with no warm session state; captured output lives in the job's " +
        "log file and status reports whether its completeness is known. Jobs are " +
        "killed by ptk_reset and at server shutdown.")]
    public static async Task<string> Job(
        RunspaceHost host,
        JobManager jobs,
        [Description("status | output | kill | list")] string action,
        CancellationToken cancellationToken,
        [Description("Job id (required for status/output/kill).")] long id = 0,
        [Description("Byte offset for action=output: pass the previous poll's 'next offset'; 0 reads from the start.")] long offset = 0,
        AuditCallContextAccessor? auditContext = null)
    {
        var audit = auditContext?.Current;
        switch (action?.ToLowerInvariant())
        {
            case "list":
            {
                var all = jobs.List();
                var response = all.Length == 0 ? "(no jobs)" : string.Join('\n', all.Select(Describe));
                audit?.CommitReadOutcome("job.list_accessed", "completed", response);
                return response;
            }
            case "status":
            {
                var snapshot = jobs.Snapshot(id);
                var response = snapshot is null ? $"[no such job: {id}]" : Describe(snapshot);
                audit?.CommitReadOutcome(
                    "job.status_accessed",
                    snapshot is null ? "not_found" : "completed",
                    response,
                    jobId: id);
                return response;
            }
            case "kill":
            {
                if (audit is not null && !audit.AuthorizeControl("job.kill_requested", id))
                    return AuditCallContext.NotStartedMessage;
                var result = jobs.RequestKill(id, JobTerminationReason.ExplicitKill);
                var (eventType, state, detailCode, response, callState) = result.Disposition switch
                {
                    JobKillDisposition.Requested => (
                        "job.kill_dispatched", "dispatched", "explicit_kill", $"[job {id} kill requested]", "completed"),
                    JobKillDisposition.NotFound => (
                        "job.kill_not_started", "not_started", "job_not_found", $"[no such job: {id}]", "not_started"),
                    JobKillDisposition.AlreadyExited => (
                        "job.kill_not_started", "not_started", "already_exited", $"[job {id} already exited]", "not_started"),
                    JobKillDisposition.AlreadyRequested => (
                        "job.kill_not_started", "not_started", "kill_already_requested", $"[job {id} kill already requested]", "not_started"),
                    _ => (
                        "job.kill_failed", "failed", "kill_request_failed", $"[job {id} kill request failed]", "failed"),
                };
                audit?.RecordControlOutcome(
                    eventType,
                    state,
                    detailCode,
                    id,
                    terminationCertainty: "not_applicable");
                audit?.CompleteCall(callState, response);
                return response;
            }
            case "output":
            {
                if (audit is not null && !audit.AuthorizeControl("job.output_requested", id))
                    return AuditCallContext.NotStartedMessage;
                var snapshot = jobs.Snapshot(id);
                if (snapshot is null)
                {
                    var missing = $"[no such job: {id}]";
                    audit?.CommitReadOutcome("job.output_accessed", "not_found", missing, jobId: id, nextOffset: offset);
                    return missing;
                }
                // Snapshot BEFORE reading: a job that exits mid-poll reports
                // "running" one poll late rather than losing tail output.
                var chunk = jobs.ReadOutput(id, offset)!;
                var (text, nextOffset, bytesRead) = chunk.Value;
                var authorizedShapingRtk = text.Length == 0
                    ? null
                    : host.CaptureOutputShapingRtkIdentity();
                audit?.CommitReadOutcome(
                    "job.output_accessed",
                    "completed",
                    text,
                    detailCode: authorizedShapingRtk is null
                        ? null
                        : "rtk_log_authorized",
                    jobId: id,
                    nextOffset: nextOffset,
                    bytesReturnedOverride: bytesRead,
                    outputShapingRtkIdentity: authorizedShapingRtk);
                // sd3-2..sd3-4: the marker's foreground default cannot name
                // this job's existing output. Re-running the job would
                // duplicate side-effecting work and the offset has already
                // moved past the middle. The recovery hint rides INTO
                // shaping, so the marker itself names the honest recovery
                // (the captured log) exactly when the module elides;
                // two downstream inference heuristics both proved unsound
                // (ANSI stripping shortens without eliding, near-boundary
                // elision lengthens).
                var logDescription = snapshot.OutputCaptureComplete switch
                {
                    true => "complete captured log",
                    false => "available partial captured log",
                    _ => "available captured log (completeness unknown)",
                };
                var elisionHint =
                    $"read the {logDescription} at {snapshot.OutputPath} if the elided middle matters";
                var shapedResult = text.Length == 0
                    ? new ShapedTextResult("(no new output)", null)
                    : audit is null
                        ? new ShapedTextResult(
                            await host.ShapeTextAsync(text, cancellationToken, elisionHint),
                            null)
                        : await host.ShapeTextAuditedAsync(
                            text,
                            cancellationToken,
                            elisionHint,
                            authorizedShapingRtk,
                            () => audit.RecordControlOutcome(
                                "runspace.recycled",
                                "completed",
                                detailCode: "job_output_shaping_timed_out",
                                warmStateLost: true));
                if (shapedResult.Shaping is { } shaping)
                    audit?.RecordOutputShaping(shaping);
                var shaped = shapedResult.Text;
                var capture = CaptureState(snapshot);
                return $"{shaped}\n[job {id} {State(snapshot)}{capture}] next offset: {nextOffset}";
            }
            default:
                return "[unknown action - use status | output | kill | list]";
        }
    }

    private static string Describe(JobSnapshot snapshot)
    {
        var script = snapshot.Script.Replace('\r', ' ').Replace('\n', ' ');
        if (script.Length > 100) script = script[..97] + "...";
        return $"job {snapshot.Id}: {State(snapshot)}{CaptureState(snapshot)}, " +
               $"started {snapshot.StartedUtc:HH:mm:ss}Z, log: {snapshot.OutputPath}\n  script: {script}";
    }

    private static string State(JobSnapshot snapshot)
    {
        if (snapshot.Running)
        {
            return snapshot.ExecutionOutcomeUnknown
                ? $"outcome unknown; containment pending (pid {snapshot.Pid})"
                : $"running (pid {snapshot.Pid})";
        }
        if (snapshot.StartOutcomeUnknown)
        {
            return snapshot.RootTerminationConfirmed
                ? "outcome unknown (start uncertain; root termination confirmed)"
                : "outcome unknown (start and root termination unconfirmed)";
        }
        if (!snapshot.RootTerminationConfirmed)
            return "outcome unknown (root termination unconfirmed)";
        if (snapshot.ExecutionOutcomeUnknown)
        {
            return $"outcome unknown (root termination confirmed; " +
                   $"{snapshot.ExecutionOutcomeFailureCode ?? "cause unknown"})";
        }
        return $"exited {snapshot.ExitCode}";
    }

    private static string CaptureState(JobSnapshot snapshot) =>
        snapshot.OutputCaptureComplete switch
        {
            false => $", output incomplete ({snapshot.OutputFailureCode ?? "unknown"})",
            null when !snapshot.Running => ", output completeness unknown",
            _ => string.Empty,
        };
}
