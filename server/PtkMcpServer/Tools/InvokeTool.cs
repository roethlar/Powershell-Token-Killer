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
        "process-local. Output is normally shaped and preserves errors, exit codes, " +
        "and structure. When the response includes a ptk_output handle, use ptk_output " +
        "to read the captured unshaped snapshot from that same invocation; PTK never " +
        "reruns the command for recovery. The legacy raw is deprecated and accepted " +
        "only for compatibility; it does not change interpreter, routing, capture, or " +
        "shaping. Calls run serially, and the timeout " +
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
            "Deprecated compatibility flag: true has no effect on dialect handling, " +
            "interpreter/routing, process choice, capture, or shaping. Use ptk_output " +
            "when a handle is returned.")]
        bool raw = false,
        [Description(
            "Routing override: 'auto' (default) runs a single native command " +
            "through rtk's filters; 'pwsh' is explicit consent to interpret the exact " +
            "original text as PowerShell and bypass automatic dialect/Bash/RTK routing; " +
            "normal capture and shaping still apply; 'rtk' asserts RTK only for an " +
            "eligible terminal native application. An ineligible assertion executes the exact original " +
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

        // Deprecated-flag visibility is counted at the user-call boundary
        // only. The flag is intentionally not forwarded into planning or
        // execution; the log and ptk_state count show remaining compatibility
        // usage until the next breaking schema revision removes it.
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
            JobStartPlan? activePlan = null;
            AuditJobTerminalLease? terminalLease = null;
            var terminalCallbackOwnedByJob = false;
            var fallbackMustBeAbandoned = false;
            var startAuthorized = false;
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

                activePlan = jobs.PrepareStart(script);
                if (audit is not null && !audit.RecordJobStartRequest(activePlan.Id))
                    return AuditCallContext.NotStartedMessage;

                if (!jobs.AllowColdBackground)
                {
                    const string disabled =
                        "[job not started] This session does not allow cold background jobs. " +
                        "Nothing was executed. Background jobs are cold and stateless; run " +
                        "connection-dependent work in the foreground so it uses the warm session.";
                    audit?.RecordJobAdmissionRefused(
                        activePlan.Id,
                        "cold_background_disabled",
                        disabled);
                    return audit?.AuthorizationPersistenceFailed == true
                        ? AuditCallContext.NotStartedMessage
                        : disabled;
                }

                if (audit is not null && !audit.BeginJobStartRequest(activePlan.Id))
                    return AuditCallContext.NotStartedMessage;

                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return RecordJobNotStarted(
                        audit,
                        "prestart_deadline_expired",
                        "[job not started] The wall-clock budget expired during pre-start checks. " +
                        "Nothing was executed. Retry, or raise timeoutSeconds.");
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

                var preparation = await host.PrepareBackgroundExecutionAsync(
                    script,
                    route,
                    cwd!,
                    deadline,
                    cancellationToken);
                switch (preparation.Status)
                {
                    case RunspaceHost.BackgroundExecutionPreparationStatus.DialectRefused:
                        return RecordJobNotStarted(
                            audit,
                            preparation.DetailCode ?? "dialect_refused",
                            preparation.Response ??
                            "[job not started] Shell dialect validation refused this cold job.");
                    case RunspaceHost.BackgroundExecutionPreparationStatus.TimedOut:
                        return RecordJobNotStarted(
                            audit,
                            preparation.DetailCode ?? "prestart_deadline_expired",
                            "[job not started] The wall-clock budget expired during cold execution planning. " +
                            "Nothing was executed. Retry, or raise timeoutSeconds.");
                    case RunspaceHost.BackgroundExecutionPreparationStatus.Canceled:
                        return RecordJobNotStarted(
                            audit,
                            preparation.DetailCode ?? "canceled",
                            "[job not started] The request was canceled before the background process started.");
                    case RunspaceHost.BackgroundExecutionPreparationStatus.Failed:
                        return RecordJobNotStarted(
                            audit,
                            preparation.DetailCode ?? "cold_planning_failed",
                            "[job not started] Cold execution planning failed; the original operation was not started.");
                }

                var executionPlan = preparation.Plan ??
                    throw new InvalidOperationException(
                        "Cold execution planning completed without an execution plan.");
                var initialDispatch = ExecutionDispatch.FromPlan(executionPlan);
                activePlan = jobs.BindDispatch(activePlan, initialDispatch, cwd!);
                if (audit is not null)
                {
                    if (!await audit.AuthorizeJobPlanAsync(
                            activePlan,
                            CancellationToken.None))
                    {
                        return AuditCallContext.NotStartedMessage;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        return RecordJobNotStarted(
                            audit,
                            "prestart_deadline_expired",
                            "[job not started] The wall-clock budget expired after plan authorization. " +
                            "Nothing was executed. Retry, or raise timeoutSeconds.");
                    }

                    terminalLease = await audit.AuthorizeJobDispatchAsync(
                        activePlan,
                        CancellationToken.None);
                    if (terminalLease is null)
                        return AuditCallContext.NotStartedMessage;
                }
                startAuthorized = true;

                // Audit append/flush is deliberately noncancelable. Recheck
                // the caller's wall-clock authority immediately afterward and
                // again inside JobManager at the final process-start gate.
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return RecordJobNotStarted(
                        audit,
                        "prestart_deadline_expired",
                        "[job not started] The wall-clock budget expired after dispatch authorization. " +
                        "Nothing was executed. Retry, or raise timeoutSeconds.");
                }

                JobSnapshot job;
                Func<JobSnapshot, Task>? onTerminal = terminalLease is null
                    ? null
                    : terminalLease.CompleteAsync;
                try
                {
                    job = jobs.CommitStart(
                        activePlan,
                        onTerminal,
                        deadline,
                        cancellationToken);
                }
                catch (JobStartException exception) when (
                    exception.ProcessStarted is false &&
                    exception.ProvenPreStartFallbackReason is { } coldFallbackReason &&
                    activePlan.Dispatch.ExecutionPath == ExecutionPath.Rtk)
                {
                    // ProcessStarted=false plus the typed reason is the only
                    // capability that permits another dispatch. This is one
                    // explicit branch, never a retry loop.
                    fallbackMustBeAbandoned = true;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        return RecordJobNotStarted(
                            audit,
                            "prestart_deadline_expired",
                            "[job not started] The wall-clock budget expired before the proved-no-start fallback. " +
                            "Nothing was executed. Retry, or raise timeoutSeconds.");
                    }

                    var fallbackDispatch = ExecutionDispatch.RtkPreStartFallback(
                        executionPlan,
                        coldFallbackReason);
                    activePlan = jobs.BindDispatch(activePlan, fallbackDispatch, cwd!);
                    if (audit is not null && !await audit.AuthorizeJobFallbackAsync(
                            activePlan,
                            CancellationToken.None))
                    {
                        return AuditCallContext.NotStartedMessage;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        return RecordJobNotStarted(
                            audit,
                            "prestart_deadline_expired",
                            "[job not started] The wall-clock budget expired after fallback authorization. " +
                            "Nothing was executed. Retry, or raise timeoutSeconds.");
                    }

                    job = jobs.CommitStart(
                        activePlan,
                        onTerminal,
                        deadline,
                        cancellationToken);
                }
                terminalCallbackOwnedByJob = true;

                var started = $"[job {job.Id} started] pid {job.Pid}, cold process (no warm session state), log: {job.OutputPath}\n" +
                    (job.Execution.FallbackReason is { } actualFallback
                        ? $"[route] requested={job.Execution.RequestedRoute.ToMachineCode()} " +
                          $"effective={job.Execution.ExecutionPath.ToMachineCode()} " +
                          $"fallback={actualFallback.ToMachineCode()}; the original script was dispatched once " +
                          "and PTK did not retry it.\n"
                        : string.Empty) +
                    $"Poll with ptk_job action=output id={job.Id} (then pass the returned next offset); " +
                    $"ptk_job action=status id={job.Id} for exit state.";
                var startRecorded = audit?.RecordJobStarted(job.Id, started) ?? true;
                jobs.ConfirmStartRecorded(job.Id);
                return startRecorded
                    ? started
                    : $"[job {job.Id} started] Required audit persistence failed after launch; the execution outcome is unknown.";
            }
            catch (JobStartException exception) when (exception.ProcessStarted is true)
            {
                var jobId = activePlan!.Id;
                terminalCallbackOwnedByJob = true;
                var unknown =
                    $"[job {jobId} started; outcome unknown] The host confirmed that the " +
                    "background process started but its startup path then failed. PTK retained " +
                    "the job, attempted containment, and will not retry it; inspect " +
                    $"ptk_job status/output id={jobId}.";
                var outcomeRecorded =
                    audit?.RecordJobStartedOutcomeUnknown(jobId, unknown) ?? true;
                jobs.ConfirmStartRecorded(jobId);
                return outcomeRecorded
                    ? unknown
                    : $"[job {jobId} started] Required audit persistence failed after launch; the execution outcome is unknown.";
            }
            catch (JobStartException exception) when (exception.ProcessStarted is null)
            {
                var jobId = activePlan!.Id;
                var unknown =
                    $"[job {jobId} start outcome unknown] The host could not confirm whether " +
                    "the background process started. PTK retained the job record but had no " +
                    "associated process handle with which to confirm containment, and it will " +
                    $"not retry; inspect ptk_job status/output id={jobId}.";
                audit?.RecordJobStartOutcomeUnknown(jobId, unknown);
                return unknown;
            }
            catch (JobStartException exception) when (
                exception.ProcessStarted is false &&
                exception.DetailCode == "prestart_deadline_expired")
            {
                return RecordJobNotStarted(
                    audit,
                    exception.DetailCode,
                    "[job not started] The wall-clock budget expired before the background process started. " +
                    "Nothing was executed. Retry, or raise timeoutSeconds.");
            }
            catch (JobStartException exception) when (exception.ProcessStarted is false)
            {
                const string failed =
                    "[job start failed] The background process could not be started.";
                audit?.RecordJobStartFailed(
                    activePlan!.Id,
                    exception.DetailCode,
                    failed);
                return failed;
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
                if (startAuthorized)
                {
                    const string failed =
                        "[job start failed] The background process could not be started.";
                    audit?.RecordJobStartFailed(
                        activePlan!.Id,
                        "process_start_failed",
                        failed);
                    return failed;
                }
                return RecordJobNotStarted(
                    audit,
                    "prestart_failed",
                    "[job not started] Background pre-start checks failed; the original operation was not started.");
            }
            finally
            {
                try
                {
                    if (fallbackMustBeAbandoned && activePlan is not null)
                        jobs.AbandonFallback(activePlan);
                }
                finally
                {
                    if (!terminalCallbackOwnedByJob)
                        terminalLease?.ReleaseWithoutTerminal();
                }
            }
        }
        using var outputCapture = outputStore is null
            ? null
            : new ForegroundOutputCapture(outputStore);
        var result = audit is null
            ? outputCapture is null
                ? await host.InvokeAsync(
                    script,
                    cancellationToken: cancellationToken,
                    route: route,
                    timeoutSeconds: timeoutSeconds)
                : await host.InvokeWithOutputCaptureAsync(
                    script,
                    outputCapture,
                    cancellationToken: cancellationToken,
                    route: route,
                    timeoutSeconds: timeoutSeconds)
            : await host.InvokeAsync(
                script,
                audit,
                cancellationToken: cancellationToken,
                route: route,
                timeoutSeconds: timeoutSeconds,
                deadline: audit.Metadata.Request.DeadlineUtc,
                outputCapture: outputCapture);

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
