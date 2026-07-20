using System.Text;
using PtkMcpServer.Audit;
using PtkSharedContracts;

namespace PtkMcpServer.Sessions;

/// <summary>
/// Owns one warm PowerShell session and all session-lifetime execution state.
/// Request-scoped audit capabilities and supervisor-owned output storage are
/// borrowed per operation instead of becoming runtime-lifetime dependencies.
/// Background jobs may retain only their designed terminal audit and output
/// capabilities until completion.
/// </summary>
public sealed class SessionRuntime : ISessionOperations, ISessionLifetime, IDisposable
{
    private readonly RunspaceHost _host;
    private readonly JobManager _jobs;
    private readonly RawUsageCounter _rawUsage;
    private readonly SemaphoreSlim _availableModuleCacheGate = new(1, 1);
    private string? _availableModuleCache;
    private int _disposed;

    internal SessionRuntime(
        RunspaceHost host,
        JobManager jobs,
        RawUsageCounter rawUsage)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(rawUsage);

        _host = host;
        _jobs = jobs;
        _rawUsage = rawUsage;
    }

    Task<string> ISessionOperations.InvokeAsync(
        string script,
        CancellationToken cancellationToken,
        bool raw,
        string route,
        bool background,
        int timeoutSeconds,
        AuditCallContextAccessor? auditContext,
        OutputStore? outputStore)
        => InvokeAsync(
            script,
            cancellationToken,
            raw,
            route,
            background,
            timeoutSeconds,
            auditContext?.Current,
            outputStore);

    Task<string> ISessionOperations.JobAsync(
        string action,
        CancellationToken cancellationToken,
        long id,
        long offset,
        AuditCallContextAccessor? auditContext)
        => JobAsync(
            action,
            cancellationToken,
            id,
            offset,
            auditContext?.Current);

    Task<string> ISessionOperations.StateAsync(
        bool listAvailable,
        CancellationToken cancellationToken,
        AuditCallContextAccessor? auditContext)
        => StateAsync(
            listAvailable,
            cancellationToken,
            auditContext?.Current);

    Task<string> ISessionOperations.ResetAsync(
        CancellationToken cancellationToken,
        AuditCallContextAccessor? auditContext)
        => ResetAsync(
            cancellationToken,
            auditContext?.Current);

    Task PtkMcpGuardian.Ownership.IOrderedOwnedLifetime.ShutdownAsync() => ShutdownAsync();

    internal async Task ShutdownAsync()
    {
        await _jobs.ShutdownAsync().ConfigureAwait(false);
        await _host.ShutdownAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _jobs.Dispose();
        }
        finally
        {
            try
            {
                _host.Dispose();
            }
            finally
            {
                _availableModuleCacheGate.Dispose();
            }
        }
    }

    internal Task<string> InvokeAsync(
        string script,
        CancellationToken cancellationToken,
        bool raw = false,
        string route = "auto",
        bool background = false,
        int timeoutSeconds = 0,
        AuditCallContext? audit = null,
        OutputStore? outputStore = null)
    {
        var outputCapture = outputStore is null
            ? null
            : ExecutionOutputCaptureAdapter.Create(outputStore);
        return InvokeCoreAsync(
            script,
            cancellationToken,
            raw,
            route,
            background,
            timeoutSeconds,
            audit,
            operationAuthority: null,
            outputCapture);
    }

    /// <summary>
    /// Private foreground boundary. The exact typed operation supplies every
    /// execution argument, while its authority supplies the immutable wire
    /// deadline even when no audit call context exists.
    /// </summary>
    internal Task<string> InvokeAsync(
        SessionOperationAuthority operationAuthority,
        InvokeForegroundOperation operation,
        CancellationToken cancellationToken,
        int timeoutSeconds = 0,
        IExecutionOutputCaptureOwner? outputCaptureOwner = null)
    {
        ArgumentNullException.ThrowIfNull(operationAuthority);
        operationAuthority.RequireOperation(operation);
        return InvokeCoreAsync(
            operation.Script,
            cancellationToken,
            operation.Raw,
            Route(operation.Route),
            background: false,
            timeoutSeconds,
            audit: null,
            operationAuthority,
            outputCaptureOwner);
    }

    /// <summary>
    /// Private background boundary. The guardian-reserved public ID and the
    /// host-created return capability are consumed only from authority; the
    /// host allocator is never consulted.
    /// </summary>
    internal Task<string> InvokeAsync(
        SessionOperationAuthority operationAuthority,
        InvokeBackgroundOperation operation,
        CancellationToken cancellationToken,
        int timeoutSeconds = 0,
        IExecutionOutputCaptureOwner? outputCaptureOwner = null)
    {
        ArgumentNullException.ThrowIfNull(operationAuthority);
        operationAuthority.RequireOperation(operation);
        _ = operationAuthority.BackgroundJob ??
            throw new ArgumentException(
                "Background invocation authority has no bound job identity.",
                nameof(operationAuthority));
        return InvokeCoreAsync(
            operation.Script,
            cancellationToken,
            operation.Raw,
            Route(operation.Route),
            background: true,
            timeoutSeconds,
            audit: null,
            operationAuthority,
            outputCaptureOwner);
    }

    private async Task<string> InvokeCoreAsync(
        string script,
        CancellationToken cancellationToken,
        bool raw,
        string route,
        bool background,
        int timeoutSeconds,
        AuditCallContext? audit,
        SessionOperationAuthority? operationAuthority,
        IExecutionOutputCaptureOwner? outputCaptureOwner)
    {
        using var outputCapture = outputCaptureOwner;
        var host = _host;
        var jobs = _jobs;
        var rawUsage = _rawUsage;
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
                var deadline = operationAuthority?.AbsoluteDeadlineUtc
                    ?? audit?.Metadata.Request.DeadlineUtc
                    ?? host.ComputeDeadline(timeoutSeconds);

                activePlan = operationAuthority?.BackgroundJob is { } backgroundJob
                    ? jobs.PrepareStartWithReservedId(
                        backgroundJob.PublicJobId,
                        backgroundJob.JobCapability,
                        script)
                    : jobs.PrepareStart(script);
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
                    job = jobs.CommitStartCore(
                        activePlan,
                        onTerminal,
                        deadline,
                        cancellationToken,
                        outputCapture);
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

                    job = jobs.CommitStartCore(
                        activePlan,
                        onTerminal,
                        deadline,
                        cancellationToken,
                        outputCapture);
                }
                terminalCallbackOwnedByJob = true;

                var started = $"[job {job.Id} started] pid {job.Pid}, cold process (no warm session state)\n" +
                    (job.Execution.FallbackReason is { } actualFallback
                        ? $"[route] requested={job.Execution.RequestedRoute.ToMachineCode()} " +
                          $"effective={job.Execution.ExecutionPath.ToMachineCode()} " +
                          $"fallback={actualFallback.ToMachineCode()}; the original script was dispatched once " +
                          "and PTK did not retry it.\n"
                        : string.Empty) +
                    $"{RecoveryStatus(job)}\n" +
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
        var executionDeadline = operationAuthority?.AbsoluteDeadlineUtc;
        var result = audit is null
            ? outputCapture is null
                ? await host.InvokeAsync(
                    script,
                    cancellationToken: cancellationToken,
                    route: route,
                    timeoutSeconds: timeoutSeconds,
                    deadline: executionDeadline)
                : await host.InvokeWithOutputCaptureAsync(
                    script,
                    outputCapture,
                    cancellationToken: cancellationToken,
                    route: route,
                    timeoutSeconds: timeoutSeconds,
                    deadline: executionDeadline)
            : await host.InvokeAsync(
                script,
                audit,
                cancellationToken: cancellationToken,
                route: route,
                timeoutSeconds: timeoutSeconds,
                deadline: executionDeadline ?? audit.Metadata.Request.DeadlineUtc,
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

    private static string Route(GuardianHostInvokeRoute route) => route switch
    {
        GuardianHostInvokeRoute.Auto => "auto",
        GuardianHostInvokeRoute.Pwsh => "pwsh",
        GuardianHostInvokeRoute.Rtk => "rtk",
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    private static string RecordJobNotStarted(
        AuditCallContext? audit,
        string detailCode,
        string response)
    {
        audit?.RecordValidationNoStart(detailCode);
        audit?.RecordJobNotStarted(detailCode, response);
        return response;
    }

    internal Task<string> JobAsync(
        string action,
        CancellationToken cancellationToken,
        long id = 0,
        long offset = 0,
        AuditCallContext? audit = null)
        => JobCoreAsync(
            action,
            cancellationToken,
            id,
            offset,
            audit,
            operationAuthority: null,
            privateJobId: null,
            privateJobCapability: null);

    internal Task<string> JobAsync(
        SessionOperationAuthority operationAuthority,
        JobListOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operationAuthority);
        operationAuthority.RequireOperation(operation);
        return JobCoreAsync(
            "list",
            cancellationToken,
            id: 0,
            offset: 0,
            audit: null,
            operationAuthority,
            privateJobId: null,
            privateJobCapability: null);
    }

    internal Task<string> JobAsync(
        SessionOperationAuthority operationAuthority,
        JobStatusOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operationAuthority);
        operationAuthority.RequireOperation(operation);
        return JobCoreAsync(
            "status",
            cancellationToken,
            operation.PublicJobId.Value,
            offset: 0,
            audit: null,
            operationAuthority,
            operation.PublicJobId,
            operation.JobCapability);
    }

    internal Task<string> JobAsync(
        SessionOperationAuthority operationAuthority,
        JobOutputOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operationAuthority);
        operationAuthority.RequireOperation(operation);
        return JobCoreAsync(
            "output",
            cancellationToken,
            operation.PublicJobId.Value,
            operation.Offset,
            audit: null,
            operationAuthority,
            operation.PublicJobId,
            operation.JobCapability);
    }

    internal Task<string> JobAsync(
        SessionOperationAuthority operationAuthority,
        JobKillOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operationAuthority);
        operationAuthority.RequireOperation(operation);
        return JobCoreAsync(
            "kill",
            cancellationToken,
            operation.PublicJobId.Value,
            offset: 0,
            audit: null,
            operationAuthority,
            operation.PublicJobId,
            operation.JobCapability);
    }

    private async Task<string> JobCoreAsync(
        string action,
        CancellationToken cancellationToken,
        long id,
        long offset,
        AuditCallContext? audit,
        SessionOperationAuthority? operationAuthority,
        PublicJobId? privateJobId,
        CapabilityToken? privateJobCapability)
    {
        if ((privateJobId is null) != (privateJobCapability is null) ||
            operationAuthority is null && privateJobId is not null ||
            operationAuthority is not null &&
                privateJobId is null &&
                !string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Private job authority and identity must be supplied together.");
        }

        var host = _host;
        var jobs = _jobs;
        switch (action?.ToLowerInvariant())
        {
            case "list":
                {
                    operationAuthority?.ThrowIfExpired(cancellationToken);
                    var all = jobs.List();
                    var response = all.Length == 0 ? "(no jobs)" : string.Join('\n', all.Select(Describe));
                    audit?.CommitReadOutcome("job.list_accessed", "completed", response);
                    return response;
                }
            case "status":
                {
                    operationAuthority?.ThrowIfExpired(cancellationToken);
                    var snapshot = privateJobId is null
                        ? jobs.Snapshot(id)
                        : jobs.Snapshot(privateJobId, privateJobCapability!);
                    var response = snapshot is null ? $"[no such job: {id}]" : Describe(snapshot);
                    audit?.CommitReadOutcome(
                        "job.status_accessed",
                        snapshot is null ? "not_found" : "completed",
                        response,
                        jobId: id,
                        jobExecution: snapshot?.ExecutionRoutingAuthoritative == true
                            ? snapshot.Execution
                            : null);
                    return response;
                }
            case "kill":
                {
                    operationAuthority?.ThrowIfExpired(cancellationToken);
                    if (audit is not null && !audit.AuthorizeControl("job.kill_requested", id))
                        return AuditCallContext.NotStartedMessage;
                    operationAuthority?.ThrowIfExpired(cancellationToken);
                    var result = privateJobId is null
                        ? jobs.RequestKill(id, JobTerminationReason.ExplicitKill)
                        : jobs.RequestKill(
                            privateJobId,
                            privateJobCapability!,
                            JobTerminationReason.ExplicitKill);
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
                    operationAuthority?.ThrowIfExpired(cancellationToken);
                    if (audit is not null && !audit.AuthorizeControl("job.output_requested", id))
                        return AuditCallContext.NotStartedMessage;
                    operationAuthority?.ThrowIfExpired(cancellationToken);
                    var snapshot = privateJobId is null
                        ? jobs.Snapshot(id)
                        : jobs.Snapshot(privateJobId, privateJobCapability!);
                    if (snapshot is null)
                    {
                        var missing = $"[no such job: {id}]";
                        audit?.CommitReadOutcome("job.output_accessed", "not_found", missing, jobId: id, nextOffset: offset);
                        return missing;
                    }
                    // Snapshot BEFORE reading: a job that exits mid-poll reports
                    // "running" one poll late rather than losing tail output.
                    (string Text, long NextOffset, int BytesRead)? chunk;
                    operationAuthority?.ThrowIfExpired(cancellationToken);
                    try
                    {
                        chunk = privateJobId is null
                            ? jobs.ReadOutput(id, offset)
                            : jobs.ReadOutput(
                                privateJobId,
                                privateJobCapability!,
                                offset);
                    }
                    catch (Exception exception) when (!IsFatal(exception))
                    {
                        var unchangedOffset = Math.Max(0, offset);
                        var unavailable =
                            $"[job {id} output unavailable: internal polling spool could not be read; " +
                            "command was not rerun]\n" +
                            $"{RecoveryStatus(snapshot)}\n" +
                            $"[job {id} {State(snapshot)}{CaptureState(snapshot)}] " +
                            $"next offset: {unchangedOffset}";
                        audit?.CommitReadOutcome(
                            "job.output_accessed",
                            "failed",
                            unavailable,
                            detailCode: "job_output_read_failed",
                            jobId: id,
                            nextOffset: unchangedOffset,
                            bytesReturnedOverride: 0,
                            jobExecution: snapshot.ExecutionRoutingAuthoritative
                                ? snapshot.Execution
                                : null);
                        return unavailable;
                    }
                    if (chunk is null)
                    {
                        var missing = $"[no such job: {id}]";
                        audit?.CommitReadOutcome(
                            "job.output_accessed",
                            "not_found",
                            missing,
                            jobId: id,
                            nextOffset: offset);
                        return missing;
                    }
                    var (text, nextOffset, bytesRead) = chunk.Value;
                    var provenance = snapshot.Execution.OutputProvenance;
                    var shapeDirectText = provenance switch
                    {
                        OutputProvenance.DirectText => true,
                        OutputProvenance.RtkUnknown or
                            OutputProvenance.RtkFiltered or
                            OutputProvenance.RtkPassthrough => false,
                        _ => throw new InvalidOperationException(
                            "Cold background output cannot carry PowerShell object provenance."),
                    };
                    var authorizedShapingRtk = text.Length == 0 || !shapeDirectText
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
                        outputShapingRtkIdentity: authorizedShapingRtk,
                        jobExecution: snapshot.ExecutionRoutingAuthoritative
                            ? snapshot.Execution
                            : null);
                    // sd3-2..sd3-4: the marker's foreground default cannot name
                    // this job's recovery. Re-running the job would duplicate
                    // side-effecting work and the offset has already moved past
                    // the middle. The persisted recovery hint rides INTO shaping,
                    // so the marker itself names the honest opaque capability
                    // exactly when the module elides;
                    // two downstream inference heuristics both proved unsound
                    // (ANSI stripping shortens without eliding, near-boundary
                    // elision lengthens).
                    var elisionHint = RecoveryElisionHint(snapshot);
                    var shapingDeadline = operationAuthority?.AbsoluteDeadlineUtc
                        ?? audit?.Metadata.Request.DeadlineUtc;
                    var shapedResult = text.Length == 0
                        ? new ShapedTextResult("(no new output)", null)
                        : audit is null
                            ? new ShapedTextResult(
                                await host.ShapeJobTextAsync(
                                    text,
                                    provenance,
                                    cancellationToken,
                                    elisionHint,
                                    shapingDeadline),
                                null)
                            : await host.ShapeTextAuditedAsync(
                                text,
                                cancellationToken,
                                elisionHint,
                                provenance,
                                authorizedShapingRtk,
                                () => audit.RecordControlOutcome(
                                    "runspace.recycled",
                                    "completed",
                                    detailCode: "job_output_shaping_timed_out",
                                    warmStateLost: true),
                                shapingDeadline);
                    if (shapedResult.Shaping is { } shaping)
                        audit?.RecordOutputShaping(shaping, provenance);
                    var shaped = shapedResult.Text;
                    var capture = CaptureState(snapshot);
                    return $"{shaped}\n{RecoveryStatus(snapshot)}\n" +
                           $"[job {id} {State(snapshot)}{capture}] next offset: {nextOffset}";
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
               $"started {snapshot.StartedUtc:HH:mm:ss}Z, {RecoveryStatus(snapshot)}\n" +
               $"  script: {script}";
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

    private static string CaptureState(JobSnapshot snapshot)
    {
        if (snapshot.OutputRecovery is { Handle: not null } recovery)
        {
            return recovery.State == OutputArtifactState.Incomplete
                ? $", recovery artifact incomplete ({recovery.DetailCode ?? "unknown"})"
                : string.Empty;
        }

        return snapshot.OutputCaptureComplete switch
        {
            false => $", output incomplete ({snapshot.OutputFailureCode ?? "unknown"})",
            null when !snapshot.Running => ", output completeness unknown",
            _ => string.Empty,
        };
    }

    internal static string RecoveryStatus(JobSnapshot snapshot)
    {
        if (snapshot.OutputRecovery is { Handle: { } handle } recovery &&
            recovery.State is OutputArtifactState.Available or OutputArtifactState.Incomplete)
        {
            var incomplete = recovery.State == OutputArtifactState.Incomplete
                ? $"; artifact incomplete (detail={recovery.DetailCode ?? "capture_incomplete"})"
                : string.Empty;
            return $"recovery=handle: ptk_output handle={handle}{incomplete}; " +
                   "ptk_output reports current availability";
        }

        if (!snapshot.OutputRecoveryFinalized && snapshot.Running)
        {
            return "recovery=pending: output capture finalizes after job exit; " +
                   "a later ptk_job status/output response will report either a " +
                   "ptk_output handle or explicit recovery-unavailable state";
        }

        if (string.Equals(
                snapshot.OutputRecovery?.DetailCode,
                "rtk_capture_unsupported",
                StringComparison.Ordinal) ||
            snapshot.Execution?.OutputProvenance == OutputProvenance.RtkUnknown)
        {
            return "recovery=unavailable: rtk capture unsupported";
        }

        return "recovery=unavailable: output capture unavailable; command was not rerun";
    }

    private static string RecoveryElisionHint(JobSnapshot snapshot) =>
        RecoveryStatus(snapshot);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    internal async Task<string> StateAsync(
        bool listAvailable = false,
        CancellationToken cancellationToken = default,
        AuditCallContext? audit = null)
    {
        var host = _host;
        var jobs = _jobs;
        var rawUsage = _rawUsage;
        if (audit is not null && !audit.AuthorizeControl("state.probe_requested"))
            return AuditCallContext.NotStartedMessage;
        var runspaceLossRecorded = false;

        // No assignments in this script: probing the session must not add
        // variables to it (the report would perturb its own drift numbers).
        var script = string.Join('\n',
            "\"engine: $($PSVersionTable.PSVersion)\"",
            "\"cwd: $((Microsoft.PowerShell.Management\\Get-Location).Path)\"",
            $"\"variables: $(@(Microsoft.PowerShell.Utility\\Get-Variable).Count) (baseline {host.BaselineVariableCount})\"",
            "$(if (@(Microsoft.PowerShell.Core\\Get-Module).Count -eq 0) { 'modules loaded: (none)' } else { 'modules loaded:' })",
            "Microsoft.PowerShell.Core\\Get-Module | Microsoft.PowerShell.Utility\\Sort-Object Name | " +
            "Microsoft.PowerShell.Core\\ForEach-Object { '  ' + $_.Name + ' ' + $_.Version }");
        // Zero-wait acquire: the health check must never queue behind the
        // workload it exists to diagnose (issue #6). Null = busy; the failed
        // acquire IS the busy signal — no snapshot-then-queue race window.
        var result = await host.TryInvokeStateProbeIfIdleAsync(
            script,
            cancellationToken: cancellationToken);
        if (result?.WarmStateLost == true && audit is not null)
        {
            audit.RecordControlOutcome(
                "runspace.recycled",
                "completed",
                detailCode: "state_probe_timed_out",
                warmStateLost: true);
            runspaceLossRecorded = true;
        }

        var probeState = result is not null && (!result.Success || result.Errors.Length > 0)
            ? "partial"
            : "completed";
        string? probeDetailCode = result is null
            ? "runspace_busy"
            : probeState == "partial" ? "probe_errors" : null;

        string Finish(string response)
        {
            audit?.CommitReadOutcome(
                "state.probe_completed",
                probeState,
                response,
                detailCode: probeDetailCode);
            return response;
        }

        var sb = new StringBuilder();
        // Raw count is compatibility telemetry for user-level raw=true calls
        // only. Internal state probes never touch the compatibility flag.
        sb.AppendLine(
            $"ptk server: pid {Environment.ProcessId}, up {FormatUptime(DateTimeOffset.UtcNow - host.StartedUtc)}, " +
            $"shaping {(host.ModuleLoaded ? "on" : "off")}, raw calls this session: {rawUsage.Count}");
        if (audit is not null) sb.AppendLine(audit.HealthStatusLine());
        var busyLineEmitted = false;
        if (result is null)
        {
            busyLineEmitted = true;
            sb.AppendLine(FormatBusyLine(host));
            sb.AppendLine("runspace-dependent details (engine, cwd, variables, loaded modules) unavailable while busy.");
        }
        else
        {
            if (result.Output.TrimEnd().Length > 0) sb.AppendLine(result.Output.TrimEnd());
            // A probe can still fail (provider/module faults, cancellation, or
            // non-terminating errors): surface that instead of silently
            // reporting partial state as the truth.
            if (!result.Success || result.Errors.Length > 0)
            {
                sb.AppendLine("[state probe errors]");
                foreach (var error in result.Errors) sb.AppendLine(error);
            }
        }

        var allJobs = jobs.List();
        if (allJobs.Length == 0)
        {
            sb.AppendLine("jobs: (none)");
        }
        else
        {
            sb.AppendLine("jobs:");
            foreach (var job in allJobs)
            {
                sb.AppendLine($"  job {job.Id}: {(job.Running ? $"running (pid {job.Pid})" : $"exited {job.ExitCode}")}");
            }
        }

        var drift = host.GetEnvironmentDrift();
        sb.AppendLine("[env drift since server start]");
        if (drift.IsEmpty)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            if (drift.Added.Length > 0) sb.AppendLine("added: " + string.Join(", ", drift.Added));
            if (drift.Modified.Length > 0) sb.AppendLine("modified: " + string.Join(", ", drift.Modified));
            if (drift.Removed.Length > 0) sb.AppendLine("removed: " + string.Join(", ", drift.Removed));
            if (drift.PathEntriesAdded.Length > 0) sb.AppendLine("PATH entries added: " + string.Join("; ", drift.PathEntriesAdded));
            if (drift.PathEntriesRemoved.Length > 0) sb.AppendLine("PATH entries removed: " + string.Join("; ", drift.PathEntriesRemoved));
        }

        if (listAvailable)
        {
            // A populated cache renders without touching the gate: a caller
            // merely reading the cache must not make a concurrent call claim
            // an enumeration is running (codex finding i56-15). The cache is
            // written once per session; a stale null read just falls through
            // to the gate path.
            if (_availableModuleCache is string cachedFast)
            {
                sb.AppendLine("modules available:");
                sb.AppendLine(cachedFast.Length > 0 ? cachedFast : "  (none)");
                return Finish(sb.ToString().TrimEnd());
            }
            // Zero-wait like every other status probe (codex finding i56-7):
            // a second state call must not block for minutes behind another
            // caller's slow first enumeration - that would withhold even the
            // host-level facts this tool promises to always deliver.
            if (!_availableModuleCacheGate.Wait(0))
            {
                sb.AppendLine("modules available: enumeration already in progress in another state call (not cached)");
                probeDetailCode ??= "module_enumeration_in_progress";
                return Finish(sb.ToString().TrimEnd());
            }
            try
            {
                if (_availableModuleCache is null)
                {
                    // Independently zero-wait: a long call can win the runspace
                    // between the first probe and this one, and queueing here
                    // would reintroduce the blocked health check (issue #6).
                    var available = await host.TryInvokeStateProbeIfIdleAsync(
                        "Microsoft.PowerShell.Core\\Get-Module -ListAvailable | " +
                        "Microsoft.PowerShell.Utility\\Sort-Object Name -Unique | " +
                        "Microsoft.PowerShell.Core\\ForEach-Object { '  {0} {1}' -f $_.Name, $_.Version }",
                        cancellationToken: cancellationToken);
                    if (available?.WarmStateLost == true && audit is not null && !runspaceLossRecorded)
                    {
                        audit.RecordControlOutcome(
                            "runspace.recycled",
                            "completed",
                            detailCode: "module_probe_timed_out",
                            warmStateLost: true);
                        runspaceLossRecorded = true;
                    }
                    // Cache only a clean probe: a failed/canceled enumeration must
                    // not masquerade as "(none)", and Success=true still carries
                    // non-terminating errors can accompany fake/partial data,
                    // so both must be clear before caching.
                    if (available is null)
                    {
                        probeDetailCode ??= "module_probe_busy";
                        sb.AppendLine("modules available: unavailable while the runspace is busy (not cached)");
                        // A long call can win the gate BETWEEN the main probe
                        // and this one; the promised busy snapshot must not
                        // vanish just because the main leg was idle (i56-8).
                        if (!busyLineEmitted) sb.AppendLine(FormatBusyLine(host));
                    }
                    else if (available.Success && available.Errors.Length == 0)
                    {
                        _availableModuleCache = available.Output.TrimEnd();
                    }
                    else
                    {
                        probeState = "partial";
                        probeDetailCode = "module_probe_errors";
                        sb.AppendLine("modules available: probe reported errors (not cached)");
                        foreach (var error in available.Errors) sb.AppendLine("  " + error);
                    }
                }
                if (_availableModuleCache is not null)
                {
                    sb.AppendLine("modules available:");
                    sb.AppendLine(_availableModuleCache.Length > 0 ? _availableModuleCache : "  (none)");
                }
            }
            finally
            {
                _availableModuleCacheGate.Release();
            }
        }

        return Finish(sb.ToString().TrimEnd());
    }

    /// <summary>Test hook for explicit cache lifecycle assertions.</summary>
    internal void ClearAvailableCacheForTests() => _availableModuleCache = null;

    // Queue-wait and execution age are independently observable (issue #6):
    // this line carries the active call's age and the waiter count; the
    // queue-expiry failure on ptk_invoke carries the wait it spent.
    private static string FormatBusyLine(RunspaceHost host)
    {
        var (_, age, waiters, recovering) = host.GetGateStatus();
        if (recovering) return $"runspace: busy (rebuilding after a recycle, {waiters} waiting)";
        return age is not null
            ? $"runspace: busy (active call running {age.Value.TotalSeconds:0}s, {waiters} waiting)"
            : $"runspace: busy ({waiters} waiting)";
    }

    private static string FormatUptime(TimeSpan up) =>
        up.TotalHours >= 1 ? $"{(int)up.TotalHours}h{up.Minutes:00}m"
        : up.TotalMinutes >= 1 ? $"{(int)up.TotalMinutes}m{up.Seconds:00}s"
        : $"{Math.Max(0, (int)up.TotalSeconds)}s";

    internal async Task<string> ResetAsync(
        CancellationToken cancellationToken = default,
        AuditCallContext? audit = null)
    {
        var host = _host;
        var jobs = _jobs;
        if (audit is not null && !audit.AuthorizeControl("reset.requested"))
            return AuditCallContext.NotStartedMessage;

        JobManager.JobResetLease jobReset;
        try
        {
            jobReset = jobs.BeginReset();
        }
        catch
        {
            audit?.RecordControlOutcome(
                "reset.not_started",
                "not_started",
                detailCode: "job_reset_admission_failed",
                terminationCertainty: "not_applicable");
            throw;
        }

        using (jobReset)
            try
            {
                await host.ResetAsync(cancellationToken);
                audit?.RecordControlOutcome(
                    jobReset.FailedCount == 0 ? "runspace.recycled" : "reset.partial_effect",
                    jobReset.FailedCount == 0 ? "completed" : "partial",
                    detailCode: jobReset.FailedCount == 0 ? null : "runspace_recycled_job_kill_failed",
                    warmStateLost: true);
            }
            catch
            {
                audit?.RecordControlOutcome(
                    jobReset.TerminationRequestedCount > 0 ? "reset.partial_effect" : "reset.outcome_unknown",
                    "outcome_unknown",
                    detailCode: jobReset.TerminationRequestedCount > 0
                        ? "jobs_killed_runspace_outcome_unknown"
                        : "runspace_outcome_unknown",
                    terminationCertainty: "unknown");
                throw;
            }
        if (jobReset.FailedCount > 0)
        {
            return $"Runspace recycled; all warm state cleared and environment restored; " +
                   $"{jobReset.TerminationRequestedCount} background job(s) received a kill request and " +
                   $"{jobReset.FailedCount} kill request(s) failed.";
        }
        return jobReset.TerminationRequestedCount > 0
            ? $"Runspace recycled; all warm state cleared, environment restored, {jobReset.TerminationRequestedCount} background job(s) killed."
            : "Runspace recycled; all warm state cleared and environment restored.";
    }
}
