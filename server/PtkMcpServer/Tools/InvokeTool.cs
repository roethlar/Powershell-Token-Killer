using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class InvokeTool
{
    [McpServerTool(Name = "ptk_invoke")]
    [Description(
        "Run shell work through PTK. PowerShell, mixed-dataflow, and most native " +
        "commands use the persistent warm runspace; eligible terminal native commands " +
        "route internally through rtk, and independently proven parse-fatal Bash syntax " +
        "may execute through startup-pinned Bash/RTK processes outside the runspace. " +
        "Preferred over Bash/PowerShell tools for shell work: output arrives " +
        "token-compressed by shape. PowerShell objects " +
        "become compact typed summaries, log-shaped text is deduplicated, plain text " +
        "passes through with terminal color codes stripped (oversized text is elided " +
        "with a labeled marker). PowerShell variables, imported modules, and established " +
        "connections persist across runspace-routed calls; delegated Bash state is " +
        "process-local. Compressed output preserves errors, " +
        "exit codes, and structure; raw=true exists for recovering detail the " +
        "compressed form lost, not as a default - for exact execution with shaped " +
        "output use route=pwsh with raw=false. Calls run serially, and the timeout " +
        "is a total wall-clock budget covering queue wait plus execution: a call " +
        "still waiting when its budget expires fails fast WITHOUT executing (warm " +
        "state intact - just retry or go background). A PowerShell execution overrun " +
        "recycles the runspace and loses warm state; a delegated Bash/RTK overrun " +
        "attempts bounded tracked-root termination, preserves warm state, and reports " +
        "descendant and remote outcomes as unknown without retrying.")]
    public static async Task<string> Invoke(
        RunspaceHost host,
        JobManager jobs,
        RawUsageCounter rawUsage,
        [Description("The command to execute: a PowerShell script or a native command line (git, npm, ...).")] string script,
        CancellationToken cancellationToken,
        [Description(
            "Recovery hatch, not a default: skip output compression only to recover " +
            "detail the compressed form lost (errors, exit codes, and structure are " +
            "already preserved compressed). For exact execution with shaped output " +
            "use route=pwsh with raw=false instead.")]
        bool raw = false,
        [Description(
            "Routing override: 'auto' (default) runs a single native command " +
            "through rtk's filters; 'pwsh' forces plain execution in the warm " +
            "runspace; 'rtk' asserts RTK only for an eligible terminal native " +
            "application. An ineligible assertion executes the exact original " +
            "once and returns a labeled effective route without asking for a retry.")]
        string route = "auto",
        [Description(
            "Run the script as a background job in a separate cold pwsh process and " +
            "return a job id immediately. Use for long stateless work (builds, " +
            "watchers, deploys) that could exceed the call timeout. The job does NOT " +
            "see warm session state (variables, modules, connections); poll it with " +
            "ptk_job.")]
        bool background = false,
        [Description(
            "Per-call timeout override in seconds, capped by the server maximum. A " +
            "total wall-clock budget: queue wait behind another call counts against " +
            "it, and a call whose budget expires while still queued fails fast " +
            "without executing (warm state intact). Use for long work that NEEDS " +
            "the warm session (live connections, imported modules); stateless long " +
            "work should use background=true instead.")]
        int timeoutSeconds = 0,
        AuditCallContextAccessor? auditContext = null,
        OutputStore? outputStore = null)
    {
        var audit = auditContext?.Current;
        if (!background && audit is not null && !audit.BeginValidation())
            return AuditCallContext.NotStartedMessage;

        // Raw-usage visibility (shell-dialect plan D2): counted here at the
        // user-call boundary only — internal raw:true probes below this
        // layer (ptk_state, the background cwd probe) must not inflate the
        // signal. The log line gives the owner per-call visibility on
        // stderr; the counter surfaces in ptk_state.
        if (raw)
        {
            Console.Error.WriteLine($"ptk: raw=true call #{rawUsage.Increment()} this session");
        }

        route = route?.ToLowerInvariant() switch
        {
            "pwsh" => "pwsh",
            "rtk" => "rtk",
            _ => "auto",
        };

        if (background)
        {
            try
            {
                // One wall-clock budget for the whole request, established
                // BEFORE any pre-start step: the dialect check and the cwd
                // probe used to run under the server default regardless of
                // the caller's timeoutSeconds, so a 1s-budget background
                // retry could still block for minutes behind a busy runspace
                // (plan finding i56p-3).
                var deadline = audit?.Metadata.Request.DeadlineUtc
                    ?? host.ComputeDeadline(timeoutSeconds);

                var plan = jobs.PrepareStart(script);
                if (audit is not null && !audit.BeginJobStartRequest(plan.Id))
                    return AuditCallContext.NotStartedMessage;

                // Dialect check BEFORE the job starts (shell-dialect plan,
                // slice 2): a detected bash-only script is refused fast, never
                // started as a job that dies in its log. Same consent bypasses
                // as the foreground path (raw=true, route=pwsh); the check
                // resolves against a cold command table because that is where
                // the job will run.
                if (!raw && route != "pwsh")
                {
                    var refusal = await host.TryGetBackgroundDialectRefusalAsync(script, cancellationToken, deadline);
                    if (refusal is not null)
                        return RecordJobNotStarted(audit, "dialect_refused", refusal);
                }

                // Anything but Ok fails the start rather than degrading to the
                // server process cwd: a build silently running in the wrong
                // project is unrecoverable; a failed start with guidance is
                // not (plan finding i56p-4; codex findings i56-5, i56-6). The
                // messages differ because only an executing-probe timeout
                // costs warm state.
                var (cwd, cwdOutcome) = await host.TryGetCurrentLocationAsync(
                    cancellationToken,
                    deadline,
                    () => audit?.RecordControlOutcome(
                        "runspace.recycled",
                        "completed",
                        detailCode: "cwd_probe_canceled_wedged",
                        warmStateLost: true));
                switch (cwdOutcome)
                {
                    case RunspaceHost.CwdProbeOutcome.QueueExpired:
                        return RecordJobNotStarted(audit, "cwd_queue_expired",
                               "[job not started] Runspace busy: the wall-clock budget expired while probing the " +
                               "session's current directory behind another call. Nothing was executed and warm state " +
                               "is untouched. Retry when the active call finishes, or raise timeoutSeconds.");
                    case RunspaceHost.CwdProbeOutcome.TimedOutExecuting:
                        audit?.RecordControlOutcome(
                            "runspace.recycled",
                            "completed",
                            detailCode: "cwd_probe_timed_out",
                            warmStateLost: true);
                        return RecordJobNotStarted(audit, "cwd_probe_timed_out",
                               "[job not started] The session-directory probe timed out while executing; the " +
                               "runspace was recycled and all warm state was lost (ptk_state shows what drifted). " +
                               "Retry to start the job in the fresh session's directory.");
                    case RunspaceHost.CwdProbeOutcome.Recovering:
                        return RecordJobNotStarted(audit, "runspace_recovering",
                               "[job not started] The runspace is being rebuilt after a previous timeout and was " +
                               "not ready within this call's budget. Nothing was executed. Retry shortly.");
                    case RunspaceHost.CwdProbeOutcome.Failed:
                        return RecordJobNotStarted(audit, "cwd_probe_failed",
                               "[job not started] Could not determine the session's current directory, and jobs " +
                               "run in the session's directory by contract - starting elsewhere could run in the " +
                               "wrong project. Run a foreground ptk_invoke (e.g. Get-Location) to diagnose, or " +
                               "Set-Location explicitly and retry.");
                }
                // Last look at the clock before the point of no return: the
                // cwd continuation can resume after a sleep pushed the wall
                // clock past the budget, and an expired request must not
                // start work (codex finding i56-4).
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return RecordJobNotStarted(audit, "prestart_deadline_expired",
                           "[job not started] The wall-clock budget expired during pre-start checks. " +
                           "Nothing was executed. Retry, or raise timeoutSeconds.");
                }

                plan = plan with { WorkingDirectory = cwd };
                var terminalLease = audit?.AuthorizeJobStart(plan.Id, cwd);
                if (audit is not null && terminalLease is null)
                    return AuditCallContext.NotStartedMessage;

                JobSnapshot job;
                try
                {
                    Func<JobSnapshot, Task>? onTerminal = terminalLease is null
                        ? null
                        : terminalLease.CompleteAsync;
                    job = jobs.CommitStart(plan, onTerminal);
                }
                catch (Exception)
                {
                    terminalLease?.ReleaseWithoutTerminal();
                    const string failed = "[job start failed] The background process could not be started.";
                    audit?.RecordJobStartFailed(plan.Id, "process_start_failed", failed);
                    return failed;
                }

                var started = $"[job {job.Id} started] pid {job.Pid}, cold process (no warm session state), log: {job.OutputPath}\n" +
                              $"Poll with ptk_job action=output id={job.Id} (then pass the returned next offset); " +
                              $"ptk_job action=status id={job.Id} for exit state.";
                var startRecorded = audit?.RecordJobStarted(job.Id, started) ?? true;
                jobs.ConfirmStartRecorded(job.Id);
                return startRecorded
                    ? started
                    : $"[job {job.Id} started] Required audit persistence failed after launch; the execution outcome is unknown.";
            }
            catch (OperationCanceledException)
            {
                return RecordJobNotStarted(
                    audit,
                    "canceled",
                    "[job not started] The request was canceled before the background process started.");
            }
            catch (Exception)
            {
                return RecordJobNotStarted(
                    audit,
                    "prestart_failed",
                    "[job not started] Background pre-start checks failed; the original operation was not started.");
            }
        }
        using var outputCapture = outputStore is null
            ? null
            : new ForegroundOutputCapture(outputStore);
        var result = audit is null
            ? outputCapture is null
                ? await host.InvokeAsync(script, raw, cancellationToken, route, timeoutSeconds)
                : await host.InvokeWithOutputCaptureAsync(
                    script,
                    outputCapture,
                    raw,
                    cancellationToken,
                    route,
                    timeoutSeconds)
            : await host.InvokeAsync(
                script,
                audit,
                raw,
                cancellationToken,
                route,
                timeoutSeconds,
                audit.Metadata.Request.DeadlineUtc,
                outputCapture);

        var sb = new StringBuilder();
        var output = result.Output.TrimEnd();
        sb.Append(output.Length > 0 ? output : "(no output)");

        if (result.UserExecutionStarted &&
            result.Routing is
            {
                FallbackReason: { } fallbackReason,
                OriginalScriptDispatched: true,
            } routing)
        {
            sb.AppendLine();
            sb.Append(
                $"[route] requested={routing.RequestedRoute.ToMachineCode()} " +
                $"effective={routing.EffectivePath.ToMachineCode()} " +
                $"fallback={fallbackReason.ToMachineCode()}; " +
                "the original script was dispatched once and PTK did not retry it.");
        }

        if (result.ExitCode is int exitCode)
        {
            sb.AppendLine();
            sb.Append($"[exit] {exitCode}");
        }

        // Neutral by design: native tools write progress and diagnostics to
        // stderr while succeeding, so this section is not a failure signal -
        // [errors] below is (issue #5).
        if (result.Stderr is { Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("[stderr]");
            foreach (var line in result.Stderr) sb.AppendLine(line);
        }

        if (result.Errors.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[errors]");
            foreach (var error in result.Errors) sb.AppendLine(error);
        }

        if (result.Warnings.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[warnings]");
            foreach (var warning in result.Warnings) sb.AppendLine(warning);
        }

        if (result.OutputRecovery is { Advertise: true } recovery)
        {
            sb.AppendLine();
            sb.Append(recovery.Handle is { } handle
                ? $"recovery=available: ptk_output handle={handle}"
                : recovery.DetailCode == "rtk_capture_unsupported"
                    ? "recovery=unavailable: rtk capture unsupported"
                    : "recovery=unavailable: output capture unavailable; command was not rerun");
        }

        var response = sb.ToString().TrimEnd();
        if (audit?.AuthorizationPersistenceFailed == true && !result.UserExecutionStarted)
            response = AuditCallContext.NotStartedMessage;
        audit?.RecordInvokeResult(result, response);
        return response;
    }

    private static string RecordJobNotStarted(
        AuditCallContext? audit,
        string detailCode,
        string response)
    {
        audit?.RecordValidationNoStart(detailCode);
        audit?.RecordJobNotStarted(detailCode, response);
        return response;
    }
}
