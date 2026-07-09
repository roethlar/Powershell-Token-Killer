using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace PtkMcpServer;

/// <summary>Result of one script invocation in the warm runspace.</summary>
/// <param name="Success">False on a terminating error or timeout; non-terminating
/// errors surface in <paramref name="Errors"/> without failing the call.</param>
/// <param name="ExitCode">Nonzero $LASTEXITCODE left by a native command, else null.
/// Reported for visibility; it does not affect <paramref name="Success"/>.</param>
public sealed record InvokeResult(
    bool Success,
    string Output,
    string[] Errors,
    string[] Warnings,
    bool TimedOut,
    int? ExitCode = null);

/// <summary>What the session has changed in the process environment since the
/// post-priming baseline. PATH is additionally reported as an entry-level diff
/// because prepended tool shims are the recorded warm-state hazard.</summary>
public sealed record EnvironmentDrift(
    string[] Added,
    string[] Modified,
    string[] Removed,
    string[] PathEntriesAdded,
    string[] PathEntriesRemoved)
{
    public bool IsEmpty => Added.Length == 0 && Modified.Length == 0 && Removed.Length == 0;
}

/// <summary>
/// Owns the single long-lived PowerShell runspace. Calls are serialized (a runspace
/// runs one pipeline at a time); a call that exceeds the timeout gets its pipeline
/// stopped and the runspace recycled so warm state never wedges the whole session.
/// The recycled-away runspace is abandoned to background disposal rather than
/// awaited, because a truly wedged pipeline can block Dispose indefinitely.
/// </summary>
public sealed class RunspaceHost : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _callTimeout;
    private readonly TimeSpan _maxCallTimeout;
    private readonly string? _modulePath;
    private Runspace _runspace;
    private bool _disposed;

    /// <summary>Timestamp of the most recent invoke/reset; read by the idle watchdog.</summary>
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>UTC time this host was constructed; ptk_state reports uptime from it.</summary>
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>Session-variable count right after the current runspace was primed;
    /// ptk_state reports the current count against it. -1 when the probe failed.</summary>
    public int BaselineVariableCount { get; private set; }

    /// <summary>True when the PwshTokenCompressor module is imported into the current
    /// warm runspace; false disables output shaping (calls fall back to Out-String).</summary>
    public bool ModuleLoaded { get; private set; }

    // PowerShell decodes native stdout with Console.OutputEncoding, which in a
    // hosted console-less process defaults to the OEM codepage on Windows —
    // modern tools emit UTF-8, so non-ASCII came back as mojibake in live use
    // (ΓÇö for em-dash; v2-feedback plan, slice 2). Pin BOM-less UTF-8 once for
    // the process. The MCP transport is unaffected: it writes bytes on the raw
    // captured streams, not through Console.Out. Trade-off accepted: genuinely
    // OEM-emitting tools now mojibake instead — modern toolchains emit UTF-8,
    // and raw job logs keep original bytes.
    static RunspaceHost()
    {
        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); }
        catch { /* best effort — a console host that refuses keeps its default */ }
    }

    public RunspaceHost(TimeSpan? callTimeout = null, string? modulePathOverride = null, TimeSpan? maxCallTimeout = null)
    {
        _callTimeout = callTimeout ?? TimeSpan.FromSeconds(300);
        // Caps only the per-call timeoutSeconds override; an env-configured
        // default is the owner's own setting and is not second-guessed.
        _maxCallTimeout = maxCallTimeout ?? TimeSpan.FromSeconds(3600);
        _modulePath = modulePathOverride ?? ResolveModulePath();
        if (_modulePath is null)
        {
            Console.Error.WriteLine(
                "ptk: PwshTokenCompressor module not found (set PTK_MODULE_PATH); output shaping disabled, calls return plain Out-String text.");
        }
        _runspace = CreatePrimedRunspace();
        // Environment variables are process-wide, not runspace state, and engine
        // startup/priming legitimately touches some (e.g. PSModulePath) — so the
        // drift baseline is captured AFTER the first primed runspace exists;
        // drift then shows only what session calls changed.
        _envBaseline = SnapshotEnvironment();
    }

    // Windows env-var names are case-insensitive; Unix names are not. The same
    // comparer serves the PATH entry diff (paths follow the same platform rule).
    private static readonly StringComparer EnvNameComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Dictionary<string, string> _envBaseline;

    private static Dictionary<string, string> SnapshotEnvironment()
    {
        var snapshot = new Dictionary<string, string>(EnvNameComparer);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name) snapshot[name] = entry.Value as string ?? string.Empty;
        }
        return snapshot;
    }

    /// <summary>Computes what the session has changed in the process environment
    /// since the post-priming baseline (names only, plus a PATH entry diff).</summary>
    public EnvironmentDrift GetEnvironmentDrift()
    {
        var current = SnapshotEnvironment();
        var added = new List<string>();
        var modified = new List<string>();
        foreach (var (name, value) in current)
        {
            if (!_envBaseline.TryGetValue(name, out var baselineValue)) added.Add(name);
            else if (!string.Equals(value, baselineValue, StringComparison.Ordinal)) modified.Add(name);
        }
        var removed = _envBaseline.Keys.Where(name => !current.ContainsKey(name)).ToList();

        string[] pathAdded = [], pathRemoved = [];
        var baselinePath = _envBaseline.GetValueOrDefault("PATH", string.Empty);
        var currentPath = current.GetValueOrDefault("PATH", string.Empty);
        if (!string.Equals(baselinePath, currentPath, StringComparison.Ordinal))
        {
            var baselineEntries = SplitPathEntries(baselinePath);
            var currentEntries = SplitPathEntries(currentPath);
            pathAdded = [.. currentEntries.Except(baselineEntries, EnvNameComparer)];
            pathRemoved = [.. baselineEntries.Except(currentEntries, EnvNameComparer)];
        }

        added.Sort(EnvNameComparer);
        modified.Sort(EnvNameComparer);
        removed.Sort(EnvNameComparer);
        return new EnvironmentDrift([.. added], [.. modified], [.. removed], pathAdded, pathRemoved);
    }

    private static string[] SplitPathEntries(string value) =>
        [.. value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    // Reset semantics are "factory state" (greenfield-design plan), and env vars
    // are process-wide, so a recycled runspace alone would keep them — the
    // recorded PATH-shim hazard would outlive its own recovery tool. Restore the
    // post-priming baseline: remove additions, reinstate modified/removed values.
    // Timeout recycles deliberately do NOT restore: the abandoned pipeline may
    // still be running and mutating env; only the explicit ptk_reset nukes.
    private void RestoreEnvironmentBaseline()
    {
        try
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string name && !_envBaseline.ContainsKey(name))
                {
                    Environment.SetEnvironmentVariable(name, null);
                }
            }
            foreach (var (name, value) in _envBaseline)
            {
                if (!string.Equals(Environment.GetEnvironmentVariable(name), value, StringComparison.Ordinal))
                {
                    Environment.SetEnvironmentVariable(name, value);
                }
            }
        }
        catch { /* never fail a reset over environment bookkeeping */ }
    }

    // Runspace.Open initializes the FileSystem provider via DriveInfo.GetDrives,
    // whose native getmntinfo call is not thread-safe on macOS — two concurrent
    // opens in one process can AccessViolation. Serialize creation process-wide.
    private static readonly object CreationLock = new();

    private static Runspace CreateRunspace()
    {
        lock (CreationLock)
        {
            var iss = InitialSessionState.CreateDefault();
            if (OperatingSystem.IsWindows())
            {
                // A hosted runspace resolves Windows execution policy like pwsh does,
                // and on a machine with no policy configured the hosted default
                // (Restricted) blocks the module import, silently degrading shaping
                // and routing. ptk_invoke replaces a harness tool that runs
                // `pwsh -ExecutionPolicy Bypass`, and ptk is not a security boundary
                // (recorded threat model), so pin Bypass rather than inherit
                // machine-to-machine policy variance. Off Windows the engine is
                // always Unrestricted and the property is not applicable.
                iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            }
            var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            return runspace;
        }
    }

    /// <summary>Creates a runspace and imports the compressor module into it, so
    /// recycle/reset paths get shaping back automatically.</summary>
    private Runspace CreatePrimedRunspace()
    {
        var runspace = CreateRunspace();
        ModuleLoaded = _modulePath is not null && TryImportModule(runspace, _modulePath);
        BaselineVariableCount = TryCountVariables(runspace);
        return runspace;
    }

    // Seeds $global:LASTEXITCODE first so the baseline matches later counts
    // (every InvokeAsync resets it before running, creating it if absent).
    private static int TryCountVariables(Runspace runspace)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript("$global:LASTEXITCODE = 0; @(Get-Variable).Count", useLocalScope: false);
            var results = ps.Invoke();
            return results.Count > 0 && results[0]?.BaseObject is int count ? count : -1;
        }
        catch { return -1; }
    }

    // An explicitly set PTK_MODULE_PATH wins outright: if it points at nothing,
    // shaping is disabled rather than silently falling back to a probed copy, so a
    // misconfiguration stays visible (same semantics as the module's PTK_RTK_PATH).
    // The start parameters exist for the tests; production callers pass
    // nothing and get the real binary dir and cwd.
    internal static string? ResolveModulePath(string? baseDirStart = null, string? cwdStart = null)
    {
        var env = Environment.GetEnvironmentVariable("PTK_MODULE_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return File.Exists(env) ? env : null;
        }

        // Binary-dir first: an installed server (~/.ptk/bin beside its src/)
        // must win over a repo checkout the session happens to sit in; cwd
        // stays the fallback so a checkout run with no installed layout
        // still resolves its own module.
        return ProbeForModule(
            baseDirStart ?? AppContext.BaseDirectory,
            cwdStart ?? Directory.GetCurrentDirectory());
    }

    private static string? ProbeForModule(params string[] starts)
    {
        foreach (var start in starts)
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "src", "PwshTokenCompressor.psd1");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static bool TryImportModule(Runspace runspace, string modulePath)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddCommand("Import-Module")
              .AddParameter("Name", modulePath)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"ptk: import of '{modulePath}' failed ({ex.Message}); output shaping disabled, calls return plain Out-String text.");
            return false;
        }
    }

    // Exit-code bookkeeping runs as tiny extra pipelines on the warm runspace
    // (sub-ms each). Both must hold the gate and must never fail a call.
    private void ResetExitCode()
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript("$global:LASTEXITCODE = 0", useLocalScope: false);
            ps.Invoke();
        }
        catch { /* bookkeeping only */ }
    }

    private int ReadExitCode()
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript("if ($global:LASTEXITCODE -is [int]) { $global:LASTEXITCODE } else { 0 }", useLocalScope: false);
            var results = ps.Invoke();
            return results.Count > 0 && results[0]?.BaseObject is int code ? code : 0;
        }
        catch { return 0; }
    }

    // rtk prints an install nag to stderr on every routed invocation; it is
    // pure noise in agent context (v2-feedback slice 3). Match the specific
    // banner, not the bare "[rtk] /!\" prefix: anything else on stderr - even
    // rtk-prefixed - is a real diagnostic and must survive (codex v2fb-1).
    private static string[] CollectErrors(IEnumerable<string> errors) =>
        [.. errors.Where(e => !e.StartsWith(@"[rtk] /!\ No hook installed", StringComparison.Ordinal))];

    // Asks the module to classify/rewrite the script (single native commands
    // route through rtk — unified-shell-routing plan). Any failure returns the
    // script unchanged: routing must never be able to fail a call.
    private string ResolveScript(string script, string route)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Resolve-PtcInvokeScript")
              .AddParameter("Script", script)
              .AddParameter("Route", route);
            var results = ps.Invoke();
            return results.Count > 0 && results[0]?.BaseObject is string resolved && resolved.Length > 0
                ? resolved
                : script;
        }
        catch { return script; }
    }

    public async Task<InvokeResult> InvokeAsync(string script, bool raw = false, CancellationToken cancellationToken = default, string route = "auto", int timeoutSeconds = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var callTimeout = timeoutSeconds > 0
            ? TimeSpan.FromSeconds(Math.Min(timeoutSeconds, _maxCallTimeout.TotalSeconds))
            : _callTimeout;
        LastActivityUtc = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken);
        var ps = PowerShell.Create();
        var handedOff = false;
        try
        {
            // raw skips routing as well as shaping: raw means the uncompressed
            // truth, executed exactly as written.
            if (ModuleLoaded && !raw && route != "pwsh")
            {
                script = ResolveScript(script, route);
            }

            // Reset before each call so a previous call's native exit code is never
            // reported against a script that ran no native command (the stale-
            // LASTEXITCODE bug first found and fixed on the since-retired CLI path).
            ResetExitCode();
            ps.Runspace = _runspace;
            // useLocalScope: false — assignments land in the runspace's session
            // state and survive into the next call; that persistence is the point.
            ps.AddScript(script, useLocalScope: false)
              .AddCommand(ModuleLoaded && !raw ? "Compress-PtcOutput" : "Out-String");

            var invokeTask = ps.InvokeAsync();
            var delayTask = Task.Delay(callTimeout, cancellationToken);
            var finished = await Task.WhenAny(invokeTask, delayTask);

            if (finished != invokeTask)
            {
                // The delay task ends two ways: canceled (caller aborted the call,
                // e.g. user Esc) or ran to completion (real timeout). A cancel is
                // not a wedge — stop the pipeline and keep the warm runspace; only
                // recycle if the pipeline refuses to stop within the grace period.
                if (delayTask.IsCanceled)
                {
                    if (await TryStopPipelineAsync(ps, invokeTask))
                    {
                        return new InvokeResult(
                            Success: false,
                            Output: string.Empty,
                            Errors: ["Call canceled by the caller; the pipeline was stopped and warm state was preserved."],
                            Warnings: [],
                            TimedOut: false);
                    }

                    handedOff = true;
                    AbandonAndRecycle(ps, invokeTask);
                    return new InvokeResult(
                        Success: false,
                        Output: string.Empty,
                        Errors: [$"Call canceled by the caller, but the pipeline did not stop within {StopGrace.TotalSeconds:0}s; the runspace was recycled and all warm state was lost."],
                        Warnings: [],
                        TimedOut: false);
                }

                handedOff = true;
                AbandonAndRecycle(ps, invokeTask);
                // Teach the recovery paths at the moment of failure — the one
                // place a model reliably reads documentation (design P4). The
                // two paths differ by workload, so name both.
                return new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors: [$"Call exceeded the {callTimeout.TotalSeconds:0}s timeout; the runspace was recycled and all warm state was lost. " +
                        "Command and PATH resolution can differ in the fresh runspace - ptk_state shows what drifted. " +
                        "For stateless long work (builds, watchers), rerun with background=true and poll with ptk_job. " +
                        "For work that needs the warm session (live connections, imported modules), rerun with a larger timeoutSeconds."],
                    Warnings: [],
                    TimedOut: true);
            }

            try
            {
                var results = await invokeTask;
                var output = string.Concat(results.Select(r => r?.ToString()));
                var exitCode = ReadExitCode();
                return new InvokeResult(
                    Success: true,
                    Output: output,
                    Errors: CollectErrors(ps.Streams.Error.Select(e => e.ToString())),
                    Warnings: [.. ps.Streams.Warning.Select(w => w.ToString())],
                    TimedOut: false,
                    ExitCode: exitCode == 0 ? null : exitCode);
            }
            catch (RuntimeException ex)
            {
                var errors = ps.Streams.Error.Select(e => e.ToString())
                    .Append(ex.ErrorRecord?.ToString() ?? ex.Message);
                return new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors: CollectErrors(errors),
                    Warnings: [.. ps.Streams.Warning.Select(w => w.ToString())],
                    TimedOut: false);
            }
        }
        finally
        {
            if (!handedOff) ps.Dispose();
            _gate.Release();
        }
    }

    /// <summary>Runs a text chunk through the module's shaping pipeline in the warm
    /// runspace (ptk_job output polls). The text enters as pipeline INPUT, never as
    /// script, so job output cannot inject code. Falls back to the raw text on any
    /// failure — shaping must never fail a poll — and a shaping call that wedges
    /// (e.g. a hung rtk child on the log leg) is timed out and the runspace
    /// recycled, exactly like a timed-out foreground call: a poll must never hold
    /// the gate forever.</summary>
    public async Task<string> ShapeTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ModuleLoaded || text.Length == 0) return text;
        LastActivityUtc = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken);
        var ps = PowerShell.Create();
        var handedOff = false;
        try
        {
            ps.Runspace = _runspace;
            ps.AddCommand("Compress-PtcOutput");
            var input = new PSDataCollection<object> { text };
            input.Complete();

            var invokeTask = ps.InvokeAsync(input);
            var delayTask = Task.Delay(_callTimeout, cancellationToken);
            var finished = await Task.WhenAny(invokeTask, delayTask);
            if (finished != invokeTask)
            {
                if (delayTask.IsCanceled && await TryStopPipelineAsync(ps, invokeTask))
                {
                    return text;
                }
                handedOff = true;
                AbandonAndRecycle(ps, invokeTask);
                return text + Environment.NewLine +
                    "[ptk: shaping timed out; the runspace was recycled; raw text returned]";
            }

            var results = await invokeTask;
            return string.Concat(results.Select(r => r?.ToString()));
        }
        catch { return text; }
        finally
        {
            if (!handedOff) ps.Dispose();
            _gate.Release();
        }
    }

    /// <summary>Current directory of the warm session (background jobs start
    /// there); null when the probe fails.</summary>
    public async Task<string?> TryGetCurrentLocationAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await InvokeAsync("(Get-Location).Path", raw: true, cancellationToken: cancellationToken);
            var path = result.Output.Trim();
            return result.Success && path.Length > 0 && Directory.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>Discard all warm state and start a fresh runspace. Caller-facing
    /// (ptk_reset); also used internally on timeout. Must hold the gate.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LastActivityUtc = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var old = _runspace;
            _runspace = CreatePrimedRunspace();
            RestoreEnvironmentBaseline();
            _ = Task.Run(() => { try { old.Dispose(); } catch { /* wedged runspace */ } });
        }
        finally
        {
            _gate.Release();
        }
    }

    // How long a canceled pipeline gets to stop before it is treated as wedged
    // and the runspace is recycled anyway.
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

    /// <summary>Stops a canceled pipeline in place. True = it stopped within the
    /// grace period and the runspace is safe to keep using; false = treat as wedged.</summary>
    private static async Task<bool> TryStopPipelineAsync(PowerShell ps, Task invokeTask)
    {
        try
        {
            var stopTask = Task.Factory.FromAsync(ps.BeginStop, ps.EndStop, null);
            if (await Task.WhenAny(stopTask, Task.Delay(StopGrace)) != stopTask) return false;
            await stopTask;
            try { await invokeTask; } catch { /* PipelineStoppedException is the expected outcome */ }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void AbandonAndRecycle(PowerShell wedged, Task abandonedInvoke)
    {
        var old = _runspace;
        _runspace = CreatePrimedRunspace();
        _ = Task.Run(async () =>
        {
            try { wedged.Stop(); } catch { /* best effort */ }
            try { await abandonedInvoke; } catch { /* observe, else unobserved-task noise */ }
            try { wedged.Dispose(); } catch { /* best effort */ }
            try { old.Dispose(); } catch { /* wedged runspace */ }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _runspace.Dispose(); } catch { /* best effort on teardown */ }
        _gate.Dispose();
    }
}
