using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace PtkMcpServer;

/// <summary>Result of one script invocation in the warm runspace.</summary>
/// <param name="Success">False on a terminating error or timeout; non-terminating
/// errors surface in <paramref name="Errors"/> without failing the call.</param>
/// <param name="ExitCode">Nonzero $LASTEXITCODE left by a native command, else null.
/// Reported for visibility; it does not affect <paramref name="Success"/>.</param>
/// <param name="Stderr">Native-command stderr, kept apart from
/// <paramref name="Errors"/>: tools routinely write progress and diagnostics
/// to stderr while succeeding, so labeling it an error sends agents chasing
/// defects that do not exist (issue #5).</param>
/// <param name="WarmStateLost">True when this call recycled the runspace
/// (execution timeout, wedged stop, wedged bookkeeping). Machine-readable so
/// callers never infer state loss from message text: a queue expiry and an
/// execution timeout both set <paramref name="TimedOut"/> but only one
/// destroys warm state (issue #6).</param>
public sealed record InvokeResult(
    bool Success,
    string Output,
    string[] Errors,
    string[] Warnings,
    bool TimedOut,
    int? ExitCode = null,
    string[]? Stderr = null,
    bool WarmStateLost = false);

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
    // The warm runspace, as a task: recycle paths swap in a REBUILD that runs
    // off the response's critical path. The slice-0 incident class — a stalled
    // Runspace.Open or module import silently withholding the labeled timeout
    // response while holding the gate — dies here: the timeout answer goes out
    // first, and the next gate holder awaits the rebuild under ITS own
    // deadline (codex finding i56-2). Swapped only while holding the gate;
    // read lock-free by GetGateStatus.
    private Task<Runspace> _runspaceReady;
    private bool _disposed;

    // Background jobs execute in a cold `pwsh -NoProfile` child, so their
    // dialect check must resolve names against the cold default command table,
    // never warm session state (shell-dialect plan, slice 1(iii)): a
    // warm-defined `export` function must not exempt a script that will die
    // without it. One pristine runspace, created on first use and reused; it
    // only ever runs the detector and the bash probe, never user code, so it
    // keeps the default command table for the life of the server.
    private Runspace? _coldDetectionRunspace;
    private bool _coldDetectionUnavailable;
    private readonly SemaphoreSlim _coldDetectionGate = new(1, 1);

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
        _runspaceReady = Task.FromResult(CreatePrimedRunspace());
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

    /// <summary>Test hook: replacement-runspace construction is synchronous and
    /// can stall in the wild (a hung mount under Runspace.Open); tests inject a
    /// delay here to prove the timeout response does not wait for it.</summary>
    internal TimeSpan CreationDelayForTests { get; set; }

    /// <summary>Creates a runspace and imports the compressor module into it, so
    /// recycle/reset paths get shaping back automatically.</summary>
    private Runspace CreatePrimedRunspace()
    {
        if (CreationDelayForTests > TimeSpan.Zero) Thread.Sleep(CreationDelayForTests);
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
    // (sub-ms each). Both must hold the gate and must never fail a call. They
    // take the runspace explicitly so an abandoned preflight/bookkeeping task
    // can never touch the replacement runspace after a recycle.
    private static void ResetExitCode(Runspace runspace)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript("$global:LASTEXITCODE = 0", useLocalScope: false);
            ps.Invoke();
        }
        catch { /* bookkeeping only */ }
    }

    private static int ReadExitCode(Runspace runspace)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript("if ($global:LASTEXITCODE -is [int]) { $global:LASTEXITCODE } else { 0 }", useLocalScope: false);
            var results = ps.Invoke();
            return results.Count > 0 && results[0]?.BaseObject is int code ? code : 0;
        }
        catch { return 0; }
    }

    // Post-success bookkeeping runs under the call's own wall-clock deadline,
    // capped by a short grace and floored so a long execution that spent its
    // budget still gets a moment to read the code (codex finding i56-3: a
    // fixed monotonic 10s ignored both the request budget and sleep). If the
    // read wedges, the response still goes out — with the recycle SURFACED,
    // never as silent success with the warm state gone.
    private static readonly TimeSpan BookkeepingGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BookkeepingFloor = TimeSpan.FromSeconds(2);

    /// <summary>Test hook: wedging the real LASTEXITCODE read requires session
    /// debugger tricks; tests inject a slow reader here instead.</summary>
    internal Func<int>? ExitCodeReaderOverrideForTests { get; set; }

    private async Task<(int ExitCode, bool Wedged)> ReadExitCodeBoundedAsync(
        Runspace runspace, DateTimeOffset callDeadline, CancellationToken cancellationToken)
    {
        var reader = ExitCodeReaderOverrideForTests;
        var read = Task.Run(() => reader is not null ? reader() : ReadExitCode(runspace));
        var now = DateTimeOffset.UtcNow;
        var deadline = callDeadline;
        if (deadline < now + BookkeepingFloor) deadline = now + BookkeepingFloor;
        if (deadline > now + BookkeepingGrace) deadline = now + BookkeepingGrace;
        if (await WaitForDeadlineAsync(read, deadline, cancellationToken) == WaitOutcome.Completed)
        {
            return (await read, false);
        }
        RecycleAbandoning(read, runspace);
        return (0, true);
    }

    private const string BookkeepingWedgeNote =
        "Exit-code bookkeeping wedged after the script finished; the runspace was recycled and " +
        "all warm state was lost. [exit] is unavailable for this call.";

    // rtk prints an install nag to stderr on every routed invocation; it is
    // pure noise in agent context (v2-feedback slice 3). Match the specific
    // banner, not the bare "[rtk] /!\" prefix: anything else on stderr - even
    // rtk-prefixed - is a real diagnostic and must survive (codex v2fb-1).
    private static string[] FilterRtkNag(IEnumerable<string> stderr) =>
        [.. stderr.Where(e => !e.StartsWith(@"[rtk] /!\ No hook installed", StringComparison.Ordinal))];

    // Native stderr arrives on the error stream as records whose provenance
    // Write-Error cannot forge: the FQID and even the exception type are
    // caller-settable (-ErrorId NativeCommandError, -Exception RemoteException
    // - both verified live), but only a real native invocation carries an
    // Application command in its InvocationInfo. A record that fails the
    // compound check stays an error: mislabeling real stderr as [errors] is
    // the old behavior; the reverse would hide a genuine failure (issue #5).
    private static bool IsNativeStderrRecord(ErrorRecord record) =>
        record.FullyQualifiedErrorId is "NativeCommandError" or "NativeCommandErrorMessage"
        && record.InvocationInfo?.MyCommand?.CommandType == CommandTypes.Application;

    /// <summary>Splits the error stream into neutral native stderr and genuine
    /// error records; <paramref name="terminatingError"/> (the catch path's
    /// RuntimeException text) is always a genuine error.</summary>
    private static (string[] Stderr, string[] Errors) PartitionErrorStream(
        IEnumerable<ErrorRecord> records, string? terminatingError = null)
    {
        var stderr = new List<string>();
        var errors = new List<string>();
        foreach (var record in records)
        {
            (IsNativeStderrRecord(record) ? stderr : errors).Add(record.ToString());
        }
        if (terminatingError is not null) errors.Add(terminatingError);
        return (FilterRtkNag(stderr), [.. errors]);
    }

    // Asks the module to classify/rewrite the script (single native commands
    // route through rtk — unified-shell-routing plan). Any failure returns the
    // script unchanged: routing must never be able to fail a call.
    private static string ResolveScript(Runspace runspace, string script, string route)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
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

    // Runs the module's dialect detector (shell-dialect plan D1, slice 2)
    // against the given runspace and, on a finding, composes the labeled
    // refusal. The runspace argument IS the resolution context (plan slice
    // 1(iii)): the warm runspace for foreground calls — a session-defined
    // `export` function is legitimate PowerShell there and exempts the script
    // — and the cold detection runspace for background jobs. Any failure
    // returns null: detection must never be able to fail a call, so
    // degradation is toward a miss (the pre-detector behavior).
    private static string? TryGetDialectRefusal(Runspace runspace, string script, bool background)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddCommand("Get-PtcShellDialectFinding").AddParameter("Script", script);
            var results = ps.Invoke();
            if (results.Count == 0 || results[0]?.BaseObject is not string finding || finding.Length == 0)
            {
                return null;
            }
            return FormatDialectRefusal(finding, ProbeBashAvailable(runspace), background);
        }
        catch { return null; }
    }

    // The bash -lc recovery wrap is offered only when bash actually resolves
    // as an Application in the resolution context (plan D1): advising it on a
    // box that cannot run it would send the model into a second dead end.
    // Normal command precedence, not an Application-filtered lookup (sd2-2):
    // a warm function or alias named bash shadows the binary, and following
    // the advice would run the shadow — the wrap is offered only when
    // re-issuing it would actually execute bash.
    private static bool ProbeBashAvailable(Runspace runspace)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript(
                "[System.Management.Automation.CommandTypes]::Application -eq " +
                "($ExecutionContext.InvokeCommand.GetCommand('bash', [System.Management.Automation.CommandTypes]::All)).CommandType");
            var results = ps.Invoke();
            return results.Count > 0 && results[0]?.BaseObject is bool available && available;
        }
        catch { return false; }
    }

    private static string FormatDialectRefusal(string finding, bool bashAvailable, bool background)
    {
        var refused = background ? "job not started" : "not executed";
        // Two quoting layers compose in the wrap (sd2-1): the wrap itself is
        // a PowerShell command line (its single-quoted argument is a
        // PowerShell string literal — escape ' by doubling), and bash then
        // parses the string's VALUE as script text (escape ' with a
        // backslash). The POSIX '\'' idiom alone parse-fails at the
        // PowerShell layer before bash ever runs.
        var recovery = bashAvailable
            ? "Rewrite it in PowerShell, or run it unchanged as bash by wrapping the whole script: " +
              "bash -lc '...'. The wrap is itself a PowerShell command line, so write a literal ' " +
              "inside the wrap by doubling it for PowerShell and backslash-escaping it for bash: " +
              "bash -lc 'echo it\\''s' prints it's."
            : "Rewrite it in PowerShell (bash is not available on this machine).";
        return $"[ptk:dialect] {refused}: the script contains {finding} - bash-only syntax, " +
               $"and this tool runs PowerShell 7. {recovery}";
    }

    /// <summary>Dialect check for a script about to start as a background job
    /// (shell-dialect plan, slice 2): must run BEFORE the job starts, so a
    /// detected script is refused fast instead of dying in its log. Resolves
    /// against a cold command table because that is where the job will run.
    /// Null means no finding — or no way to check, which falls back to
    /// today's behavior rather than blocking the job.</summary>
    public async Task<string?> TryGetBackgroundDialectRefusalAsync(string script, CancellationToken cancellationToken = default, DateTimeOffset? deadline = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // A served refusal is user activity (sd2-3): this is a user-call
        // boundary like InvokeAsync/ResetAsync, and a refused background
        // call returns before anything else touches the idle clock — the
        // watchdog must not stop a server right after it answered.
        LastActivityUtc = DateTimeOffset.UtcNow;
        if (_modulePath is null) return null;
        // The WHOLE cold-side check — gate wait, first-use runspace creation
        // and import, detection — runs as one unit raced against the request's
        // wall-clock deadline (codex finding i56-4): a single monotonic gate
        // wait was sleep-unsafe, and the synchronous creation/import after it
        // escaped the budget entirely. Deadline expiry degrades to a miss
        // (null), the pre-detector behavior — the check must never block a
        // job — and the abandoned task finishes its work and releases the
        // cold gate on its own.
        var work = Task.Run(async () =>
        {
            await _coldDetectionGate.WaitAsync(CancellationToken.None);
            try
            {
                if (_coldDetectionRunspace is null)
                {
                    if (_coldDetectionUnavailable) return null;
                    var runspace = CreateRunspace();
                    try
                    {
                        using var ps = PowerShell.Create();
                        ps.Runspace = runspace;
                        ps.AddCommand("Import-Module")
                          .AddParameter("Name", _modulePath)
                          .AddParameter("ErrorAction", "Stop");
                        ps.Invoke();
                        _coldDetectionRunspace = runspace;
                    }
                    catch
                    {
                        // Same module, same path as the warm import, so this is
                        // near-unreachable when the warm import succeeded; a
                        // permanent skip beats re-paying runspace creation on
                        // every background job just to fail again.
                        try { runspace.Dispose(); } catch { /* best effort */ }
                        _coldDetectionUnavailable = true;
                        return null;
                    }
                }
                return TryGetDialectRefusal(_coldDetectionRunspace, script, background: true);
            }
            catch { return null; }
            finally
            {
                _coldDetectionGate.Release();
            }
        });
        if (deadline is null) return await work;
        return await WaitForDeadlineAsync(work, deadline.Value, cancellationToken) == WaitOutcome.Completed
            ? await work
            : null;
    }

    internal enum WaitOutcome { Completed, TimedOut, Canceled }

    // Deadline checks are wall-clock and re-evaluated in bounded chunks, never
    // a single monotonic Task.Delay: monotonic timers stop during system
    // sleep, so a call dispatched into a DarkWake sliver silently outlived its
    // budget by hours while the caller counted wall time (slice-0 incident,
    // 2026-07-10). After any sleep, the next chunk expiry re-reads the wall
    // clock and an overdue deadline fires promptly — the IdleWatchdog model.
    private static readonly TimeSpan DeadlineCheckChunk = TimeSpan.FromSeconds(60);

    /// <summary>Waits for <paramref name="work"/> under a wall-clock deadline,
    /// sleep-safe (see <see cref="DeadlineCheckChunk"/>). A deadline already in
    /// the past reports <see cref="WaitOutcome.TimedOut"/> without waiting.</summary>
    internal static async Task<WaitOutcome> WaitForDeadlineAsync(
        Task work, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (work.IsCompleted) return WaitOutcome.Completed;
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return WaitOutcome.TimedOut;
            var chunk = remaining < DeadlineCheckChunk ? remaining : DeadlineCheckChunk;
            var delay = Task.Delay(chunk, cancellationToken);
            var finished = await Task.WhenAny(work, delay);
            if (finished == work) return WaitOutcome.Completed;
            if (delay.IsCanceled) return WaitOutcome.Canceled;
        }
    }

    // Busy snapshot for the never-queueing ptk_state (issue #6): the active
    // call's start (0 ticks = idle) and how many callers are waiting. Updated
    // lock-free around the gate so reading it never touches the gate itself.
    private long _activeCallStartTicks;
    private int _gateWaiters;

    /// <summary>Lock-free view of the serialization gate for diagnostics:
    /// busy/idle, how long the active call has held the runspace, how many
    /// callers are queued behind it, and whether a post-recycle rebuild is
    /// still in flight (recovering counts as busy for callers).</summary>
    public (bool Busy, TimeSpan? ActiveCallAge, int Waiters, bool Recovering) GetGateStatus()
    {
        var startTicks = Interlocked.Read(ref _activeCallStartTicks);
        var recovering = !_runspaceReady.IsCompleted;
        var busy = _gate.CurrentCount == 0 || recovering;
        TimeSpan? age = busy && startTicks != 0
            ? DateTimeOffset.UtcNow - new DateTimeOffset(startTicks, TimeSpan.Zero)
            : null;
        return (busy, age, Volatile.Read(ref _gateWaiters), recovering);
    }

    private void MarkGateAcquired() =>
        Interlocked.Exchange(ref _activeCallStartTicks, DateTimeOffset.UtcNow.UtcTicks);

    private void ReleaseGate()
    {
        Interlocked.Exchange(ref _activeCallStartTicks, 0);
        _gate.Release();
    }

    // The serialization gate wait is part of the same wall-clock budget as
    // execution (issue #6): a queued call must not overshoot its budget just
    // because another call holds the runspace. Chunked for sleep safety.
    private async Task<bool> TryEnterGateAsync(DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _gateWaiters);
        try
        {
            while (true)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) return false;
                var chunk = remaining < DeadlineCheckChunk ? remaining : DeadlineCheckChunk;
                if (await _gate.WaitAsync(chunk, cancellationToken))
                {
                    // The semaphore wait is monotonic while the deadline is
                    // wall-clock: across a sleep, the gate can be won AFTER
                    // the budget expired, and executing then would break the
                    // never-executes promise for expired queued calls (codex
                    // finding i56-1). Deterministically untestable without a
                    // clock seam — the window needs monotonic/wall divergence
                    // mid-wait — so this guard is by-inspection; the queued
                    // and past-deadline tests cover the surrounding paths.
                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        _gate.Release();
                        return false;
                    }
                    MarkGateAcquired();
                    return true;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _gateWaiters);
        }
    }

    internal TimeSpan EffectiveBudget(int timeoutSeconds) => timeoutSeconds > 0
        ? TimeSpan.FromSeconds(Math.Min(timeoutSeconds, _maxCallTimeout.TotalSeconds))
        : _callTimeout;

    /// <summary>The request's wall-clock deadline. Computed at the tool boundary
    /// for background calls so every pre-start step shares one budget.</summary>
    internal DateTimeOffset ComputeDeadline(int timeoutSeconds) =>
        DateTimeOffset.UtcNow + EffectiveBudget(timeoutSeconds);

    // Queue expiry is NOT the execution timeout: nothing ran, nothing was
    // recycled, and saying otherwise sends the model rebuilding warm state it
    // never lost (plan finding i56p-8). The busy detail makes queue wait and
    // the active call's age independently observable (issue #6).
    private InvokeResult QueueExpiryResult(TimeSpan budget)
    {
        var (_, age, waiters, _) = GetGateStatus();
        var busyDetail = age is not null
            ? $"the active call has held the runspace for {age.Value.TotalSeconds:0}s and {waiters} caller(s) are waiting"
            : "another call holds the runspace";
        return new InvokeResult(
            Success: false,
            Output: string.Empty,
            Errors: [$"Runspace busy: this call's {budget.TotalSeconds:0}s wall-clock budget expired while waiting for another call to finish ({busyDetail}). " +
                "The script was NOT executed and warm state is untouched. " +
                "Retry when the active call finishes, raise timeoutSeconds, or use background=true for stateless work."],
            Warnings: [],
            TimedOut: true);
    }

    private InvokeResult ExecutionTimeoutResult(TimeSpan budget) => new(
        Success: false,
        Output: string.Empty,
        Errors: [$"Call timed out: it exceeded its {budget.TotalSeconds:0}s wall-clock budget (queue wait + execution); the runspace was recycled and all warm state was lost. " +
            "Command and PATH resolution can differ in the fresh runspace - ptk_state shows what drifted. " +
            "For stateless long work (builds, watchers), rerun with background=true and poll with ptk_job. " +
            "For work that needs the warm session (live connections, imported modules), rerun with a larger timeoutSeconds."],
        Warnings: [],
        TimedOut: true,
        WarmStateLost: true);

    public async Task<InvokeResult> InvokeAsync(string script, bool raw = false, CancellationToken cancellationToken = default, string route = "auto", int timeoutSeconds = 0, DateTimeOffset? deadline = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var budget = EffectiveBudget(timeoutSeconds);
        // A caller-supplied deadline (the background pre-start path) is the
        // request's budget; this call inherits whatever remains of it.
        var callDeadline = deadline ?? DateTimeOffset.UtcNow + budget;
        LastActivityUtc = DateTimeOffset.UtcNow;
        if (!await TryEnterGateAsync(callDeadline, cancellationToken))
        {
            return QueueExpiryResult(budget);
        }
        return await InvokeGateHeldAsync(script, raw, route, callDeadline, budget, cancellationToken);
    }

    /// <summary>Runs the script only if the runspace is idle RIGHT NOW; null
    /// means it is busy and nothing ran. The ptk_state probes use this so the
    /// health check never queues behind the workload it exists to diagnose
    /// (issue #6) — the failed zero-wait acquire IS the busy signal, with no
    /// check-then-queue window for a new call to slip into.</summary>
    public async Task<InvokeResult?> TryInvokeIfIdleAsync(string script, bool raw = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // A served busy report is user activity (the sd2-3 class; plan finding
        // i56p-10): stamp BEFORE the acquire attempt so even the null return
        // refreshes the idle clock — the watchdog must not stop a server right
        // after it answered.
        LastActivityUtc = DateTimeOffset.UtcNow;
        if (!_gate.Wait(0)) return null;
        // A pending rebuild counts as busy for a zero-wait probe: waiting for
        // it would reintroduce the queueing this API exists to avoid.
        if (!_runspaceReady.IsCompletedSuccessfully)
        {
            ReleaseGate();
            return null;
        }
        MarkGateAcquired();
        var budget = _callTimeout;
        return await InvokeGateHeldAsync(script, raw, "auto", DateTimeOffset.UtcNow + budget, budget, cancellationToken);
    }

    // After a recycle, the replacement runspace is built off the response's
    // critical path; the next call picks it up here, under its own deadline.
    // A faulted rebuild fails THIS call and kicks off a fresh attempt so one
    // bad build cannot brick the server permanently.
    private async Task<Runspace?> AwaitRunspaceReadyAsync(DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var ready = _runspaceReady;
        if (await WaitForDeadlineAsync(ready, deadline, cancellationToken) != WaitOutcome.Completed)
        {
            return null;
        }
        if (ready.IsCompletedSuccessfully) return ready.Result;
        Console.Error.WriteLine($"ptk: runspace rebuild failed ({ready.Exception?.GetBaseException().Message}); retrying in the background.");
        _runspaceReady = Task.Run(CreatePrimedRunspace);
        return null;
    }

    private InvokeResult RecoveringResult(TimeSpan budget) => new(
        Success: false,
        Output: string.Empty,
        Errors: [$"Runspace recovering: a previous call timed out and the replacement runspace was not ready within this call's {budget.TotalSeconds:0}s wall-clock budget. " +
            "The script was NOT executed. Retry shortly, or raise timeoutSeconds."],
        Warnings: [],
        TimedOut: true);

    private async Task<InvokeResult> InvokeGateHeldAsync(string script, bool raw, string route, DateTimeOffset callDeadline, TimeSpan budget, CancellationToken cancellationToken)
    {
        var ps = PowerShell.Create();
        var handedOff = false;
        try
        {
            var runspace = await AwaitRunspaceReadyAsync(callDeadline, cancellationToken);
            if (runspace is null)
            {
                return RecoveringResult(budget);
            }

            // Preflight runs pipelines on the warm runspace (dialect check,
            // routing, exit-code bookkeeping) BEFORE the timed main pipeline,
            // so it sits inside the same deadline: a session-shadowed helper
            // or wedged rtk child hanging preflight is the d3-1 wedge class
            // (plan finding i56p-1). raw skips routing as well as shaping:
            // raw means the uncompressed truth, executed exactly as written;
            // route=pwsh bypasses by the same consent (shell-dialect plan).
            var checkDialect = ModuleLoaded && !raw && route != "pwsh";
            var preflight = Task.Run(() =>
            {
                var resolved = script;
                if (checkDialect)
                {
                    // Dialect check first (shell-dialect plan D1, slice 2): a
                    // probed bash-only construct gets a fast labeled refusal
                    // with recovery guidance instead of dying later as a
                    // misdirected pwsh error.
                    var refusal = TryGetDialectRefusal(runspace, script, background: false);
                    if (refusal is not null) return (Refusal: refusal, Script: script);
                    resolved = ResolveScript(runspace, script, route);
                }
                // Reset before each call so a previous call's native exit code
                // is never reported against a script that ran no native
                // command (the stale-LASTEXITCODE bug).
                ResetExitCode(runspace);
                return (Refusal: (string?)null, Script: resolved);
            }, CancellationToken.None);

            var preflightOutcome = await WaitForDeadlineAsync(preflight, callDeadline, cancellationToken);
            if (preflightOutcome != WaitOutcome.Completed)
            {
                // A stuck preflight pipeline is a wedged warm runspace; there
                // is no PowerShell handle to stop here, so recycle either way.
                RecycleAbandoning(preflight, runspace);
                return preflightOutcome == WaitOutcome.TimedOut
                    ? ExecutionTimeoutResult(budget)
                    : new InvokeResult(
                        Success: false,
                        Output: string.Empty,
                        Errors: ["Call canceled by the caller during preflight; the runspace was recycled and all warm state was lost."],
                        Warnings: [],
                        TimedOut: false);
            }

            var (preflightRefusal, resolvedScript) = await preflight;
            if (preflightRefusal is not null)
            {
                return new InvokeResult(
                    Success: false,
                    Output: preflightRefusal,
                    Errors: [],
                    Warnings: [],
                    TimedOut: false);
            }

            ps.Runspace = runspace;
            // useLocalScope: false — assignments land in the runspace's session
            // state and survive into the next call; that persistence is the point.
            ps.AddScript(resolvedScript, useLocalScope: false)
              .AddCommand(ModuleLoaded && !raw ? "Compress-PtcOutput" : "Out-String");

            var invokeTask = ps.InvokeAsync();
            var outcome = await WaitForDeadlineAsync(invokeTask, callDeadline, cancellationToken);

            if (outcome == WaitOutcome.Canceled)
            {
                // A cancel (caller aborted, e.g. user Esc) is not a wedge —
                // stop the pipeline and keep the warm runspace; only recycle
                // if the pipeline refuses to stop within the grace period.
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
                AbandonAndRecycle(ps, invokeTask, runspace);
                return new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors: [$"Call canceled by the caller, but the pipeline did not stop within {StopGrace.TotalSeconds:0}s; the runspace was recycled and all warm state was lost."],
                    Warnings: [],
                    TimedOut: false,
                    WarmStateLost: true);
            }

            if (outcome == WaitOutcome.TimedOut)
            {
                handedOff = true;
                AbandonAndRecycle(ps, invokeTask, runspace);
                // Teach the recovery paths at the moment of failure — the one
                // place a model reliably reads documentation (design P4).
                return ExecutionTimeoutResult(budget);
            }

            try
            {
                var results = await invokeTask;
                var output = string.Concat(results.Select(r => r?.ToString()));
                var (exitCode, wedged) = await ReadExitCodeBoundedAsync(runspace, callDeadline, cancellationToken);
                var (stderr, errors) = PartitionErrorStream(ps.Streams.Error);
                if (wedged) errors = [.. errors, BookkeepingWedgeNote];
                return new InvokeResult(
                    Success: true,
                    Output: output,
                    Errors: errors,
                    Warnings: [.. ps.Streams.Warning.Select(w => w.ToString())],
                    TimedOut: false,
                    ExitCode: exitCode == 0 ? null : exitCode,
                    Stderr: stderr,
                    WarmStateLost: wedged);
            }
            catch (RuntimeException ex)
            {
                var (stderr, errors) = PartitionErrorStream(
                    ps.Streams.Error, ex.ErrorRecord?.ToString() ?? ex.Message);
                // The terminating-native-error configuration
                // ($PSNativeCommandUseErrorActionPreference) lands nonzero-exit
                // commands here; without the read their [exit] N silently
                // dropped beside the preserved stderr (plan finding i56p-6).
                var (exitCode, wedged) = await ReadExitCodeBoundedAsync(runspace, callDeadline, cancellationToken);
                if (wedged) errors = [.. errors, BookkeepingWedgeNote];
                return new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors: errors,
                    Warnings: [.. ps.Streams.Warning.Select(w => w.ToString())],
                    TimedOut: false,
                    ExitCode: exitCode == 0 ? null : exitCode,
                    Stderr: stderr,
                    WarmStateLost: wedged);
            }
        }
        finally
        {
            if (!handedOff) ps.Dispose();
            ReleaseGate();
        }
    }

    /// <summary>Runs a text chunk through the module's shaping pipeline in the warm
    /// runspace (ptk_job output polls). The text enters as pipeline INPUT, never as
    /// script, so job output cannot inject code. Falls back to the raw text on any
    /// failure — shaping must never fail a poll — and a shaping call that wedges
    /// (e.g. a hung rtk child on the log leg) is timed out and the runspace
    /// recycled, exactly like a timed-out foreground call: a poll must never hold
    /// the gate forever.</summary>
    public async Task<string> ShapeTextAsync(string text, CancellationToken cancellationToken = default, string? elisionHint = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ModuleLoaded || text.Length == 0) return text;
        LastActivityUtc = DateTimeOffset.UtcNow;
        var deadline = DateTimeOffset.UtcNow + _callTimeout;
        // A poll must never block behind a long foreground call either: on a
        // gate that stays busy past the budget, return the text unshaped —
        // shaping must never fail (or stall) a poll.
        if (!await TryEnterGateAsync(deadline, cancellationToken))
        {
            return text;
        }
        var ps = PowerShell.Create();
        var handedOff = false;
        try
        {
            var runspace = await AwaitRunspaceReadyAsync(deadline, cancellationToken);
            if (runspace is null) return text;
            ps.Runspace = runspace;
            ps.AddCommand("Compress-PtcOutput");
            // Context-correct elision advice, composed BY the elision itself
            // (sd3-2..sd3-4): the caller knows its recovery path; inferring
            // elision downstream from the shaped text proved unsound in both
            // directions.
            if (elisionHint is not null) ps.AddParameter("ElisionHint", elisionHint);
            var input = new PSDataCollection<object> { text };
            input.Complete();

            var invokeTask = ps.InvokeAsync(input);
            var outcome = await WaitForDeadlineAsync(invokeTask, deadline, cancellationToken);
            if (outcome != WaitOutcome.Completed)
            {
                if (outcome == WaitOutcome.Canceled && await TryStopPipelineAsync(ps, invokeTask))
                {
                    return text;
                }
                handedOff = true;
                AbandonAndRecycle(ps, invokeTask, runspace);
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
            ReleaseGate();
        }
    }

    public enum CwdProbeOutcome
    {
        /// <summary>Path resolved; the job may start there.</summary>
        Ok,
        /// <summary>The probe ran but produced no usable path.</summary>
        Failed,
        /// <summary>Budget expired before the probe ran; warm state untouched.</summary>
        QueueExpired,
        /// <summary>The probe itself timed out EXECUTING; the runspace was
        /// recycled and warm state is gone — reporting this as a mere queue
        /// expiry would tell the model its connections survived when they did
        /// not (codex finding i56-6).</summary>
        TimedOutExecuting,
    }

    /// <summary>Current directory of the warm session (background jobs start
    /// there). The caller must FAIL the job start on anything but Ok rather
    /// than silently start the job in the server process cwd — the wrong
    /// project (plan finding i56p-4; codex finding i56-5). Cancellation
    /// propagates.</summary>
    public async Task<(string? Path, CwdProbeOutcome Outcome)> TryGetCurrentLocationAsync(CancellationToken cancellationToken = default, DateTimeOffset? deadline = null)
    {
        try
        {
            var result = await InvokeAsync("(Get-Location).Path", raw: true, cancellationToken: cancellationToken, deadline: deadline);
            if (result.TimedOut)
            {
                return (null, result.WarmStateLost ? CwdProbeOutcome.TimedOutExecuting : CwdProbeOutcome.QueueExpired);
            }
            var path = result.Output.Trim();
            return result.Success && path.Length > 0 && Directory.Exists(path)
                ? (path, CwdProbeOutcome.Ok)
                : (null, CwdProbeOutcome.Failed);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (null, CwdProbeOutcome.Failed); }
    }

    /// <summary>Discard all warm state and start a fresh runspace. Caller-facing
    /// (ptk_reset); also used internally on timeout. Must hold the gate.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LastActivityUtc = DateTimeOffset.UtcNow;
        // Reset waits unbounded by design (the caller asked for the rebuilt
        // world), but it still counts as a waiter: a queued reset is exactly
        // the caller ptk_state should report, since it will discard all warm
        // state the moment the active call finishes (codex finding i56-9).
        Interlocked.Increment(ref _gateWaiters);
        try
        {
            await _gate.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _gateWaiters);
        }
        MarkGateAcquired();
        try
        {
            // Unlike the timeout recycles, reset rebuilds SYNCHRONOUSLY: the
            // caller explicitly asked for a fresh world, and returning before
            // the env baseline is restored would let a reset "complete" with
            // drift still visible. The never-returned hazard (i56-2) belongs
            // to the timeout paths, where the response is the priority.
            var oldReady = _runspaceReady;
            _runspaceReady = Task.FromResult(CreatePrimedRunspace());
            RestoreEnvironmentBaseline();
            _ = oldReady.ContinueWith(
                t => { try { t.Result.Dispose(); } catch { /* wedged runspace */ } },
                TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        finally
        {
            ReleaseGate();
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

    // Both recycle helpers swap in a BACKGROUND rebuild and return
    // immediately: replacement construction (Runspace.Open, module import) is
    // synchronous and unbounded, and running it on the response path would
    // withhold the labeled timeout answer — the exact never-returned class
    // the wall-clock work exists to close (codex finding i56-2, slice-0
    // class). The caller still holds the gate here; the swap is safe.
    private void AbandonAndRecycle(PowerShell wedged, Task abandonedInvoke, Runspace old)
    {
        _runspaceReady = Task.Run(CreatePrimedRunspace);
        _ = Task.Run(async () =>
        {
            try { wedged.Stop(); } catch { /* best effort */ }
            try { await abandonedInvoke; } catch { /* observe, else unobserved-task noise */ }
            try { wedged.Dispose(); } catch { /* best effort */ }
            try { old.Dispose(); } catch { /* wedged runspace */ }
        });
    }

    // Recycle when there is no PowerShell handle to stop (a wedged preflight
    // or bookkeeping task): disposing the old runspace tears down whatever
    // pipeline the abandoned task is stuck in; the task is awaited only to
    // observe its eventual exception.
    private void RecycleAbandoning(Task abandonedWork, Runspace old)
    {
        _runspaceReady = Task.Run(CreatePrimedRunspace);
        _ = Task.Run(async () =>
        {
            try { old.Dispose(); } catch { /* wedged runspace */ }
            try { await abandonedWork; } catch { /* observe, else unobserved-task noise */ }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var ready = _runspaceReady;
        if (ready.IsCompletedSuccessfully)
        {
            try { ready.Result.Dispose(); } catch { /* best effort on teardown */ }
        }
        else
        {
            // A pending rebuild is disposed when it materializes; a faulted
            // one has nothing to dispose.
            _ = ready.ContinueWith(
                t => { try { t.Result.Dispose(); } catch { /* best effort */ } },
                TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        try { _coldDetectionRunspace?.Dispose(); } catch { /* best effort on teardown */ }
        _gate.Dispose();
        _coldDetectionGate.Dispose();
    }
}
