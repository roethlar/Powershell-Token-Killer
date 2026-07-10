using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class StateTool
{
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static string? _availableCache;

    [McpServerTool(Name = "ptk_state")]
    [Description(
        "Session introspection and health check for the ptk warm runspace: engine, " +
        "server PID and uptime, current directory, loaded modules, running " +
        "background jobs, and DRIFT - what this session has changed since server " +
        "start (environment variables, PATH as an entry diff, session variable " +
        "count). Use it to diagnose a polluted session (e.g. a test shim left on " +
        "PATH) before it corrupts results; ptk_reset restores factory state. Set " +
        "listAvailable to also enumerate installed modules - slow the first time, " +
        "cached for the rest of the session. Always answers promptly: while " +
        "another call holds the runspace it reports host-level facts plus a " +
        "busy line (active-call age, waiter count) instead of queueing, and " +
        "marks runspace-dependent details unavailable.")]
    public static async Task<string> State(
        RunspaceHost host,
        JobManager jobs,
        RawUsageCounter rawUsage,
        [Description("Also enumerate every installed module instead of only loaded ones.")]
        bool listAvailable = false,
        CancellationToken cancellationToken = default)
    {
        // No assignments in this script: probing the session must not add
        // variables to it (the report would perturb its own drift numbers).
        var script = string.Join('\n',
            "\"engine: $($PSVersionTable.PSVersion)\"",
            "\"cwd: $((Get-Location).Path)\"",
            $"\"variables: $(@(Get-Variable).Count) (baseline {host.BaselineVariableCount})\"",
            "$(if (@(Get-Module).Count -eq 0) { 'modules loaded: (none)' } else { 'modules loaded:' })",
            "Get-Module | Sort-Object Name | ForEach-Object { '  ' + $_.Name + ' ' + $_.Version }");
        // Zero-wait acquire: the health check must never queue behind the
        // workload it exists to diagnose (issue #6). Null = busy; the failed
        // acquire IS the busy signal — no snapshot-then-queue race window.
        var result = await host.TryInvokeIfIdleAsync(
            script,
            raw: true, // this tool formats its own lines; never shape them
            cancellationToken: cancellationToken);

        var sb = new StringBuilder();
        // raw count: user-level raw=true calls only (shell-dialect plan D2) —
        // this tool's own raw:true probes are plumbing and never counted.
        sb.AppendLine(
            $"ptk server: pid {Environment.ProcessId}, up {FormatUptime(DateTimeOffset.UtcNow - host.StartedUtc)}, " +
            $"shaping {(host.ModuleLoaded ? "on" : "off")}, raw calls this session: {rawUsage.Count}");
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
            // A session can break the probe itself (e.g. a shadowing Get-Module):
            // surface that instead of silently reporting partial state as the truth.
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
            // Zero-wait like every other status probe (codex finding i56-7):
            // a second state call must not block for minutes behind another
            // caller's slow first enumeration - that would withhold even the
            // host-level facts this tool promises to always deliver.
            if (!CacheGate.Wait(0))
            {
                sb.AppendLine("modules available: enumeration already in progress in another state call (not cached)");
                return sb.ToString().TrimEnd();
            }
            try
            {
                if (_availableCache is null)
                {
                    // Independently zero-wait: a long call can win the runspace
                    // between the first probe and this one, and queueing here
                    // would reintroduce the blocked health check (issue #6).
                    var available = await host.TryInvokeIfIdleAsync(
                        "Get-Module -ListAvailable | Sort-Object Name -Unique | " +
                        "ForEach-Object { '  {0} {1}' -f $_.Name, $_.Version }",
                        raw: true, // this tool formats its own lines; never shape them
                        cancellationToken: cancellationToken);
                    // Cache only a clean probe: a failed/canceled enumeration must
                    // not masquerade as "(none)", and Success=true still carries
                    // non-terminating errors (a poisoned Get-Module can Write-Error
                    // and emit fake data), so both must be clear before caching.
                    if (available is null)
                    {
                        sb.AppendLine("modules available: unavailable while the runspace is busy (not cached)");
                        // A long call can win the gate BETWEEN the main probe
                        // and this one; the promised busy snapshot must not
                        // vanish just because the main leg was idle (i56-8).
                        if (!busyLineEmitted) sb.AppendLine(FormatBusyLine(host));
                    }
                    else if (available.Success && available.Errors.Length == 0)
                    {
                        _availableCache = available.Output.TrimEnd();
                    }
                    else
                    {
                        sb.AppendLine("modules available: probe reported errors (not cached)");
                        foreach (var error in available.Errors) sb.AppendLine("  " + error);
                    }
                }
                if (_availableCache is not null)
                {
                    sb.AppendLine("modules available:");
                    sb.AppendLine(_availableCache.Length > 0 ? _availableCache : "  (none)");
                }
            }
            finally
            {
                CacheGate.Release();
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Test hook: the available-module cache is process-static, so cache
    /// semantics are untestable without clearing it between cases.</summary>
    internal static void ClearAvailableCacheForTests() => _availableCache = null;

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
}
