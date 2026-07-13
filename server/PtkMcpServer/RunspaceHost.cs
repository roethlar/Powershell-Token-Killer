using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;

namespace PtkMcpServer;

/// <summary>Machine-readable terminal disposition of one invocation. Timeout
/// remains a separate fact because it can expire before user execution starts
/// or leave an already-started effect with an unknown outcome.</summary>
public enum InvokeDisposition
{
    NotStarted,
    Completed,
    Failed,
    Canceled,
    OutcomeUnknown,
}

/// <summary>Durable pre-effect barriers around dispatch selection. The plan
/// barrier authorizes bounded choices; each dispatch barrier records the exact
/// initial or fallback choice before it may execute.</summary>
internal interface IInvocationAuthorizer
{
    ValueTask<bool> AuthorizePlanAsync(
        ExecutionPlan plan,
        CancellationToken cancellationToken);

    ValueTask<bool> AuthorizeDispatchAsync(
        ExecutionDispatch dispatch,
        CancellationToken cancellationToken);

    ValueTask<bool> RecordValidatorStartedAsync(
        ExecutionDispatch dispatch,
        CancellationToken cancellationToken);

    ValueTask<bool> RecordValidatorCompletedAsync(
        ExecutionDispatch dispatch,
        BashSyntaxValidationResult result,
        CancellationToken cancellationToken);
}

/// <summary>Result of one foreground invocation managed by the session host.
/// A typed Bash/RTK dispatch executes outside the PowerShell runspace.</summary>
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
/// <param name="Recovering">True when the call could not run because a
/// post-recycle rebuild was still in flight — distinct from queue contention,
/// which the busy messages describe as another call holding the runspace
/// (codex finding i56-11).</param>
/// <param name="Disposition">Structured terminal state for audit consumers;
/// never inferred from human-facing output or error strings.</param>
/// <param name="UserExecutionStarted">True once the original user work has
/// been handed to its selected execution engine. Preflight activity, including
/// the no-execution Bash validator, and warm-state loss alone never set this
/// field.</param>
public sealed record InvokeResult(
    bool Success,
    string Output,
    string[] Errors,
    string[] Warnings,
    bool TimedOut,
    InvokeDisposition Disposition,
    bool UserExecutionStarted,
    int? ExitCode = null,
    string[]? Stderr = null,
    bool WarmStateLost = false,
    bool Recovering = false)
{
    internal ExecutionRouteSummary? Routing { get; init; }
    internal string? AuditDetailCode { get; init; }
    internal OutputShapingSummary? OutputShaping { get; init; }
    internal bool PipelineHadErrors { get; init; }
}

internal sealed record ExecutionRouteSummary(
    RequestedExecutionRoute RequestedRoute,
    ExecutionPath EffectivePath,
    ExecutionFallbackReason? FallbackReason,
    bool OriginalScriptDispatched);

internal enum OutputShapingStatus
{
    RtkLogUsed,
    RtkLogUnavailable,
    RtkLogIdentityUnavailable,
    RtkLogFailed,
    ProtocolInvalid,
    PipelineCanceled,
    PipelineTimedOut,
    PipelineFailed,
}

internal sealed record OutputShapingSummary(
    OutputShapingStatus Status,
    string? RtkBinaryDigest)
{
    internal string DetailCode => Status switch
    {
        OutputShapingStatus.RtkLogUsed => "rtk_log_used",
        OutputShapingStatus.RtkLogUnavailable => "rtk_log_unavailable",
        OutputShapingStatus.RtkLogIdentityUnavailable => "rtk_log_identity_unavailable",
        OutputShapingStatus.RtkLogFailed => "rtk_log_failed",
        OutputShapingStatus.ProtocolInvalid => "rtk_log_protocol_invalid",
        OutputShapingStatus.PipelineCanceled => "output_shaping_pipeline_canceled",
        OutputShapingStatus.PipelineTimedOut => "output_shaping_pipeline_timed_out",
        OutputShapingStatus.PipelineFailed => "output_shaping_pipeline_failed",
        _ => throw new ArgumentOutOfRangeException(),
    };

    internal bool UsedRtk => Status == OutputShapingStatus.RtkLogUsed;
}

internal sealed record ShapedTextResult(
    string Text,
    OutputShapingSummary? Shaping);

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
/// Owns the single long-lived PowerShell runspace and foreground routing gate.
/// PowerShell pipelines are serialized; typed Bash validation/execution holds the
/// same gate but uses direct child processes. A PowerShell timeout recycles the
/// runspace, while Bash/RTK timeout handling preserves it and reports only the
/// termination coverage it can prove. A recycled-away runspace is abandoned to
/// background disposal because a truly wedged pipeline can block Dispose forever.
/// </summary>
public sealed class RunspaceHost : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _callTimeout;
    private readonly TimeSpan _maxCallTimeout;
    private readonly string? _modulePath;
    private readonly string? _moduleSource;
    private readonly RtkExecutableIdentity? _rtkExecutableIdentity;
    private readonly BashExecutableIdentity? _bashExecutableIdentity;
    // The warm runspace, as a task: recycle paths swap in a REBUILD that runs
    // off the response's critical path. The slice-0 incident class — a stalled
    // Runspace.Open or module import silently withholding the labeled timeout
    // response while holding the gate — dies here: the timeout answer goes out
    // first, and the next gate holder awaits the rebuild under ITS own
    // deadline (codex finding i56-2). Swapped only while holding the gate;
    // read lock-free by GetGateStatus.
    private Task<PrimedRunspace> _runspaceReady;
    private bool _disposed;
    private readonly object _ownedWorkGate = new();
    private readonly HashSet<Task> _ownedBackgroundWork = [];
    private Exception? _ownedBackgroundWorkFailure;

    internal void TrackOwnedBackgroundWorkForTests(Task work) => TrackOwnedBackgroundWork(work);

    private void TrackOwnedBackgroundWork(Task work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_ownedWorkGate)
            _ownedBackgroundWork.Add(work);
        _ = work.ContinueWith(
            completed =>
            {
                lock (_ownedWorkGate)
                {
                    _ownedBackgroundWork.Remove(completed);
                    if (completed.IsFaulted)
                    {
                        _ownedBackgroundWorkFailure ??=
                            completed.Exception?.GetBaseException() ??
                            new IOException("Owned runspace cleanup failed.");
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DrainOwnedBackgroundWorkAsync()
    {
        while (true)
        {
            Task[] pending;
            Exception? failure;
            lock (_ownedWorkGate)
            {
                pending = [.. _ownedBackgroundWork];
                failure = _ownedBackgroundWorkFailure;
            }

            if (pending.Length == 0)
            {
                if (failure is not null)
                    throw new IOException("Owned runspace cleanup could not be confirmed.", failure);
                return;
            }

            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch { /* the tracked continuation preserves the authoritative failure */ }
        }
    }

    private static Collection<PSObject> InvokeWithoutAmbientAudit(PowerShell powerShell)
    {
        using var suppressed = ExecutionContext.SuppressFlow();
        return powerShell.Invoke();
    }

    private static Task<PSDataCollection<PSObject>> InvokeAsyncWithoutAmbientAudit(
        PowerShell powerShell)
    {
        using var suppressed = ExecutionContext.SuppressFlow();
        return powerShell.InvokeAsync();
    }

    private static Task<PSDataCollection<PSObject>> InvokeAsyncWithoutAmbientAudit(
        PowerShell powerShell,
        PSDataCollection<object> input)
    {
        using var suppressed = ExecutionContext.SuppressFlow();
        return powerShell.InvokeAsync(input);
    }

    // Background jobs execute in a cold `pwsh -NoProfile` child. Preserve the
    // stock alias/function/cmdlet table captured before the first user runspace
    // is published, then overlay live PATH applications for each job request.
    // The classifier itself is pure C#; there is no second enumerable runspace
    // for prior user code (or a ThreadJob) to poison.
    private readonly TrustedCommandSnapshot _backgroundStockCommands;

    /// <summary>Timestamp of the most recent invoke/reset; read by the idle watchdog.</summary>
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>UTC time this host was constructed; ptk_state reports uptime from it.</summary>
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>Session-variable count right after the current runspace was primed;
    /// ptk_state reports the current count against it. -1 when the probe failed.</summary>
    public int BaselineVariableCount { get; private set; }

    /// <summary>True when the trusted PwshTokenCompressor shaping handle was captured
    /// for the current warm runspace; false disables shaping and its paired
    /// routing/dialect behavior. The module is detached after capture; trusted
    /// preflight itself is pure C# over data-only command facts.</summary>
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

    public RunspaceHost(
        TimeSpan? callTimeout = null,
        string? modulePathOverride = null,
        TimeSpan? maxCallTimeout = null,
        string? bashPathOverride = null,
        string? rtkPathOverride = null)
    {
        _callTimeout = callTimeout ?? TimeSpan.FromSeconds(300);
        // Caps only the per-call timeoutSeconds override; an env-configured
        // default is the owner's own setting and is not second-guessed.
        _maxCallTimeout = maxCallTimeout ?? TimeSpan.FromSeconds(3600);
        _modulePath = modulePathOverride ?? ResolveModulePath();
        _moduleSource = TryCaptureModuleSource(_modulePath);
        if (_modulePath is null)
        {
            Console.Error.WriteLine(
                "ptk: PwshTokenCompressor module not found (set PTK_MODULE_PATH); output shaping disabled, calls return plain Out-String text.");
        }
        var initialPrimed = CreatePrimedRunspace();
        try
        {
            _rtkExecutableIdentity = CaptureStartupRtkIdentity(
                initialPrimed.Runspace,
                rtkPathOverride);
            _bashExecutableIdentity = CaptureStartupBashIdentity(
                initialPrimed.Runspace,
                bashPathOverride);
            _backgroundStockCommands = CaptureBackgroundStockCommands(initialPrimed.Runspace);
        }
        catch
        {
            initialPrimed.Runspace.Dispose();
            throw;
        }
        PublishPrimedMetadata(initialPrimed);
        _runspaceReady = Task.FromResult(initialPrimed);
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

    private volatile bool _moduleImportDisabledForTests;

    /// <summary>Test hook for a superseded rebuild whose module construction
    /// fails after another runspace has already won publication.</summary>
    internal bool ModuleImportDisabledForTests
    {
        get => _moduleImportDisabledForTests;
        set => _moduleImportDisabledForTests = value;
    }

    /// <summary>Test hook for deadline/cancellation behavior while the pure C#
    /// preflight and its CLR command snapshot are active.</summary>
    internal TimeSpan PreflightDelayForTests { get; set; }

    /// <summary>Test seam for the fixed internal Bash syntax-validation cap.</summary>
    internal TimeSpan BashValidationLimitForTests { get; set; } =
        BashProcessRunner.DefaultValidationLimit;

    /// <summary>Test seam that may alter only detector output. The independent
    /// PowerShell parse-fatal fact remains owned by the trusted parser.</summary>
    internal Func<string, string?>? DialectFindingOverrideForTests { get; set; }

    /// <summary>Test-only compatibility seam for fixtures that vary a fake
    /// RTK after constructing a shared host. Production always uses the
    /// startup-frozen identity.</summary>
    internal Func<RtkExecutableIdentity?, RtkExecutableIdentity?>?
        RtkIdentityOverrideForTests { get; set; }

    /// <summary>Test-only exception seam for the post-access job-output
    /// shaping pipeline. Production never assigns it.</summary>
    internal Action? OutputShapingFailureForTests { get; set; }

    private RtkExecutableIdentity? EffectiveRtkIdentity() =>
        RtkIdentityOverrideForTests is { } overrideForTests
            ? overrideForTests(_rtkExecutableIdentity)
            : _rtkExecutableIdentity;

    /// <summary>Test hook for state-probe error/cache branches without
    /// allowing user functions to shadow module-qualified probe commands.</summary>
    internal Func<string, InvokeResult?>? IdleInvocationOverrideForTests { get; set; }

    /// <summary>Test hook for current-directory failure and timeout branches.
    /// Production reads the provider intrinsic directly.</summary>
    internal Func<string?>? CurrentLocationReaderOverrideForTests { get; set; }

    /// <summary>A runspace plus the metadata its own priming produced. The
    /// bundle is immutable and host-global fields are published only when a
    /// bundle is CONSUMED as current: a superseded background rebuild finishing
    /// after a reset must not stamp its import result over the runspace that
    /// actually won (codex finding i56-12).</summary>
    private sealed record PrimedRunspace(
        Runspace Runspace,
        bool ModuleLoaded,
        int BaselineVariableCount,
        CommandInfo? CompressCommand);

    /// <summary>Creates a runspace and imports the compressor module into it, so
    /// recycle/reset paths get shaping back automatically. Pure: touches no
    /// host-global state.</summary>
    private PrimedRunspace CreatePrimedRunspace()
    {
        if (CreationDelayForTests > TimeSpan.Zero) Thread.Sleep(CreationDelayForTests);
        var runspace = CreateRunspace();
        var imported = !ModuleImportDisabledForTests && _moduleSource is not null &&
            TryImportModule(runspace, _moduleSource, _modulePath!);
        var compressCommand = imported
            ? TryCaptureModuleCommand(runspace, "Compress-PtcOutput")
            : null;
        var moduleLoaded = imported &&
            compressCommand is not null &&
            TryDetachModule(runspace);
        return new PrimedRunspace(
            runspace,
            moduleLoaded,
            TryCountVariables(runspace),
            moduleLoaded ? compressCommand : null);
    }

    private static CommandInfo? TryCaptureModuleCommand(Runspace runspace, string name)
    {
        try
        {
            var command = runspace.SessionStateProxy.InvokeCommand.GetCommand(
                $"PwshTokenCompressor\\{name}",
                CommandTypes.Function);
            return command is FunctionInfo { ModuleName: "PwshTokenCompressor" }
                ? command
                : null;
        }
        catch { return null; }
    }

    private void PublishPrimedMetadata(PrimedRunspace primed)
    {
        ModuleLoaded = primed.ModuleLoaded;
        BaselineVariableCount = primed.BaselineVariableCount;
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
            var results = InvokeWithoutAmbientAudit(ps);
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

    private static string? TryCaptureModuleSource(string? modulePath)
    {
        if (modulePath is null) return null;

        // The product module is a manifest plus a same-named script module.
        // Capture the executable source once, before the initial runspace is
        // published and before user code can mutate the checkout. All later
        // warm/cold imports consume this immutable string, never the path.
        var sourcePath = string.Equals(
            Path.GetExtension(modulePath), ".psd1", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(modulePath, ".psm1")
            : modulePath;
        try
        {
            using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"ptk: capture of module source '{sourcePath}' failed ({ex.Message}); output shaping disabled, calls return plain Out-String text.");
            return null;
        }
    }

    private static bool TryImportModule(
        Runspace runspace,
        string moduleSource,
        string moduleOrigin)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddCommand("Microsoft.PowerShell.Core\\New-Module")
              .AddParameter("Name", "PwshTokenCompressor")
              .AddParameter("ScriptBlock", ScriptBlock.Create(moduleSource))
              .AddParameter("ErrorAction", "Stop");
            var created = InvokeWithoutAmbientAudit(ps);
            var module = created
                .Select(result => result?.BaseObject)
                .OfType<PSModuleInfo>()
                .SingleOrDefault();
            if (ps.HadErrors || module is null)
                throw new InvalidOperationException("PTK in-memory module construction failed.");

            ps.Commands.Clear();
            ps.AddCommand("Microsoft.PowerShell.Core\\Import-Module")
              .AddParameter("ModuleInfo", module)
              .AddParameter("Global")
              .AddParameter("Force")
              .AddParameter("ErrorAction", "Stop");
            _ = InvokeWithoutAmbientAudit(ps);
            if (ps.HadErrors)
                throw new InvalidOperationException("PTK module import failed.");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"ptk: import of frozen module from '{moduleOrigin}' failed ({ex.Message}); output shaping disabled, calls return plain Out-String text.");
            return false;
        }
    }

    private static bool TryDetachModule(Runspace runspace)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddCommand("Microsoft.PowerShell.Core\\Remove-Module")
              .AddParameter("Name", "PwshTokenCompressor")
              .AddParameter("Force")
              .AddParameter("ErrorAction", "Stop");
            _ = InvokeWithoutAmbientAudit(ps);
            if (ps.HadErrors) return false;

            ps.Commands.Clear();
            ps.AddCommand("Microsoft.PowerShell.Core\\Get-Command")
              .AddParameter(
                  "Name",
                  WildcardPattern.Escape("PwshTokenCompressor\\Compress-PtcOutput"))
              .AddParameter("CommandType", CommandTypes.Function)
              .AddParameter("ListImported")
              .AddParameter("ErrorAction", "Ignore");
            // Get-Command marks the PowerShell instance HadErrors when the
            // expected post-removal lookup misses even with ErrorAction=Ignore.
            // The empty imported-only result is the detachment proof.
            return InvokeWithoutAmbientAudit(ps).Count == 0;
        }
        catch { return false; }
    }

    private sealed record CommandLookupRequest(
        string Name,
        CommandTypes QueryTypes,
        CommandTypes StoredTypes);

    /// <summary>Runs read-only CLR command-table enumeration. Pattern-mode
    /// enumeration stays inside the already-loaded/session table and PATH; it
    /// neither auto-imports modules nor enters lookup callbacks. No ambient
    /// variable, debugger, delegate, type-data, or session state is changed.</summary>
    private static T WithReadOnlyCommandLookup<T>(
        Runspace runspace,
        Func<CommandInvocationIntrinsics, T> inspection)
    {
        try
        {
            return inspection(runspace.SessionStateProxy.InvokeCommand);
        }
        catch (TrustedPreflightIsolationException) { throw; }
        catch (Exception ex)
        {
            throw new TrustedPreflightIsolationException(
                "Trusted CLR command snapshot failed.", ex);
        }
    }

    private static ResolvedCommand? CaptureResolvedCommand(
        CommandInvocationIntrinsics invocation,
        string name,
        CommandTypes queryTypes)
    {
        var escapedName = WildcardPattern.Escape(name);
        var command = invocation
            .GetCommands(escapedName, queryTypes, nameIsPattern: true)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

        // Pattern-mode lookup is the no-auto-load command-table surface, but
        // Windows does not apply extension expansion to the exact pattern
        // (`git` is exposed as `git.exe`). Preserve session-command precedence
        // by expanding only after the exact lookup misses. Enumerating
        // ExternalScript and Application together retains PowerShell's
        // .ps1-before-.exe order; PATHEXT filters non-invocable applications.
        var extensionQueryTypes = queryTypes &
                                  (CommandTypes.Application | CommandTypes.ExternalScript);
        if (command is null &&
            OperatingSystem.IsWindows() &&
            extensionQueryTypes != 0)
        {
            var pathExtensions = (Environment.GetEnvironmentVariable("PATHEXT") ??
                                  ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            command = invocation
                .GetCommands($"{escapedName}.*", extensionQueryTypes, nameIsPattern: true)
                .FirstOrDefault(candidate =>
                    string.Equals(
                        Path.GetFileNameWithoutExtension(candidate.Name),
                        name,
                        StringComparison.OrdinalIgnoreCase) &&
                    (candidate.CommandType == CommandTypes.ExternalScript ||
                     candidate.CommandType == CommandTypes.Application &&
                     pathExtensions.Contains(
                         Path.GetExtension(candidate.Name),
                         StringComparer.OrdinalIgnoreCase)));
        }
        return command is null
            ? null
            : new ResolvedCommand(
                command.CommandType,
                command.Source,
                command.Definition,
                command is CmdletInfo cmdlet &&
                cmdlet.ImplementingType ==
                    typeof(Microsoft.PowerShell.Commands.SetContentCommand));
    }

    private static TrustedCommandSnapshot CaptureCommandFacts(
        Runspace runspace,
        IEnumerable<CommandLookupRequest> requests)
    {
        return WithReadOnlyCommandLookup(runspace, invocation =>
        {
            var snapshot = new TrustedCommandSnapshot();
            foreach (var request in requests
                         .DistinctBy(
                             request => (request.Name.ToUpperInvariant(), request.QueryTypes, request.StoredTypes)))
            {
                snapshot.Set(
                    request.Name,
                    request.StoredTypes,
                    CaptureResolvedCommand(invocation, request.Name, request.QueryTypes));
            }
            return snapshot;
        });
    }

    private static TrustedCommandSnapshot CaptureBackgroundStockCommands(Runspace runspace)
    {
        return WithReadOnlyCommandLookup(runspace, invocation =>
        {
            // A cold child receives stock aliases/functions/cmdlets but resolves
            // PATH applications at launch time. Exclude applications/scripts
            // here so each background request can overlay the current process
            // environment and observe both additions and removals.
            var names = invocation
                .GetCommands("*", CommandTypes.All, nameIsPattern: true)
                .Where(command => command.CommandType is not
                    (CommandTypes.Application or CommandTypes.ExternalScript))
                .Select(command => command.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var snapshot = new TrustedCommandSnapshot();
            foreach (var name in names)
            {
                var resolved = CaptureResolvedCommand(invocation, name, CommandTypes.All);
                if (resolved is not null && resolved.CommandType is not
                    (CommandTypes.Application or CommandTypes.ExternalScript))
                {
                    snapshot.Set(name, CommandTypes.All, resolved);
                }
            }
            return snapshot;
        });
    }

    private TrustedCommandSnapshot CaptureBackgroundCommandFacts(string script)
    {
        var snapshot = _backgroundStockCommands.Clone();
        foreach (var name in TrustedPreflightClassifier
                     .GetRequiredCommandNames(script)
                     .Append("bash")
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (snapshot.Resolve(name, CommandTypes.All) is not null) continue;
            snapshot.Set(name, CommandTypes.All, ResolveCurrentPathCommand(name));
        }
        return snapshot;
    }

    private static ResolvedCommand? ResolveCurrentPathCommand(string name)
    {
        // Dialect classification only asks about literal command names. A name
        // containing a path separator is not a bash builtin/shadow candidate,
        // and its relative base belongs to the later audited CWD probe.
        if (string.IsNullOrWhiteSpace(name) ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathExtensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        foreach (var entry in path.Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            // Empty/relative PATH entries resolve against the future job's warm
            // working directory, which is intentionally probed only after this
            // detector. Never substitute the server process CWD. Conservatively
            // treat the name as a non-Application collision: avoid a false bash
            // refusal, but do not offer bash-wrap guidance we cannot prove.
            if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
                return new ResolvedCommand(CommandTypes.ExternalScript);

            if (OperatingSystem.IsWindows())
            {
                if (Path.HasExtension(name))
                {
                    var exact = Path.Combine(directory, name);
                    var resolved = ClassifyPathCommand(exact, pathExtensions);
                    if (resolved is not null) return resolved;
                }
                else
                {
                    foreach (var extension in pathExtensions)
                    {
                        var resolved = ClassifyPathCommand(
                            Path.Combine(directory, name + extension),
                            pathExtensions);
                        if (resolved is not null) return resolved;
                    }
                    var script = ClassifyPathCommand(
                        Path.Combine(directory, name + ".ps1"),
                        pathExtensions);
                    if (script is not null) return script;
                }
            }
            else
            {
                var exact = Path.Combine(directory, name);
                if (File.Exists(exact))
                {
                    if (Path.GetExtension(exact).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                        return PathCommand(exact, CommandTypes.ExternalScript);
                    try
                    {
                        var mode = File.GetUnixFileMode(exact);
                        var executable = UnixFileMode.UserExecute |
                                         UnixFileMode.GroupExecute |
                                         UnixFileMode.OtherExecute;
                        if ((mode & executable) != 0)
                            return PathCommand(exact, CommandTypes.Application);
                    }
                    catch { /* An unreadable candidate is not safely resolvable. */ }
                }
                var script = Path.Combine(directory, name + ".ps1");
                if (File.Exists(script))
                    return PathCommand(script, CommandTypes.ExternalScript);
            }
        }
        return null;
    }

    private static ResolvedCommand? ClassifyPathCommand(
        string path,
        IReadOnlyList<string> pathExtensions)
    {
        if (!File.Exists(path)) return null;
        var extension = Path.GetExtension(path);
        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            return PathCommand(path, CommandTypes.ExternalScript);
        return pathExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? PathCommand(path, CommandTypes.Application)
            : null;
    }

    private static ResolvedCommand PathCommand(string path, CommandTypes type)
    {
        var fullPath = Path.GetFullPath(path);
        return new ResolvedCommand(type, fullPath, fullPath);
    }

    private static TrustedCommandSnapshot CaptureForegroundCommandFacts(
        Runspace runspace,
        string script)
    {
        var requests = TrustedPreflightClassifier
            .GetRequiredCommandNames(script)
            .Select(name => new CommandLookupRequest(name, CommandTypes.All, CommandTypes.All))
            .Append(new CommandLookupRequest("bash", CommandTypes.All, CommandTypes.All));
        return CaptureCommandFacts(runspace, requests);
    }

    private static RtkExecutableIdentity? CaptureStartupRtkIdentity(
        Runspace runspace,
        string? rtkPathOverride)
    {
        var configured = rtkPathOverride ??
                         Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        if (configured is not null)
            return RtkExecutableIdentity.TryCapture(configured);

        var resolved = WithReadOnlyCommandLookup(
            runspace,
            invocation => CaptureResolvedCommand(
                invocation,
                "rtk",
                CommandTypes.Application));
        return RtkExecutableIdentity.TryCapture(resolved?.Source);
    }

    // Exit-code bookkeeping runs as tiny extra pipelines on the warm runspace
    // (sub-ms each). Both must hold the gate and must never fail a call. They
    // take the runspace explicitly so an abandoned preflight/bookkeeping task
    // can never touch the replacement runspace after a recycle.
    private void ResetExitCode(Runspace runspace)
    {
        try
        {
            ExitCodeResetObserverForTests?.Invoke();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddScript("$global:LASTEXITCODE = 0", useLocalScope: false);
            _ = InvokeWithoutAmbientAudit(ps);
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
            var results = InvokeWithoutAmbientAudit(ps);
            return results.Count > 0 && results[0]?.BaseObject is int code ? code : 0;
        }
        catch { return 0; }
    }

    // Post-success bookkeeping runs strictly inside the call's wall-clock
    // deadline, additionally capped by a short grace (codex finding i56-3,
    // both legs: a fixed monotonic 10s ignored budget and sleep, and a floor
    // past the deadline overshot the advertised total). A budget already
    // exhausted when bookkeeping starts skips the read — the caller gets its
    // output on time without [exit] rather than late with it. If the read
    // wedges mid-flight, the response still goes out with the recycle
    // SURFACED, never as silent success with the warm state gone.
    private static readonly TimeSpan BookkeepingGrace = TimeSpan.FromSeconds(10);

    /// <summary>Test hook: wedging the real LASTEXITCODE read requires session
    /// debugger tricks; tests inject a slow reader here instead.</summary>
    internal Func<int>? ExitCodeReaderOverrideForTests { get; set; }

    /// <summary>Test hook that makes the authorization-before-bookkeeping
    /// ordering observable without exposing runspace internals.</summary>
    internal Action? ExitCodeResetObserverForTests { get; set; }

    private async Task<(int ExitCode, bool Wedged)> ReadExitCodeBoundedAsync(
        Runspace runspace, DateTimeOffset callDeadline, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (callDeadline <= now) return (0, false);
        var reader = ExitCodeReaderOverrideForTests;
        var read = Task.Run(() => reader is not null ? reader() : ReadExitCode(runspace));
        var deadline = callDeadline > now + BookkeepingGrace ? now + BookkeepingGrace : callDeadline;
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

    private static bool BashAvailable(TrustedCommandSnapshot commands) =>
        commands.Resolve("bash", CommandTypes.All)?.CommandType == CommandTypes.Application;

    private static BashExecutableIdentity? CaptureStartupBashIdentity(
        Runspace runspace,
        string? bashPathOverride)
    {
        if (bashPathOverride is not null)
            return BashExecutableIdentity.TryCapture(bashPathOverride);

        var resolved = WithReadOnlyCommandLookup(
            runspace,
            invocation => CaptureResolvedCommand(
                invocation,
                "bash",
                CommandTypes.Application));
        return BashExecutableIdentity.TryCapture(resolved?.Source);
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

    private static string FormatBashDelegationUnavailable(
        string finding,
        bool bashAvailable,
        bool rtkAvailable,
        bool workingDirectoryAvailable)
    {
        var reason = !bashAvailable
            ? "the startup-pinned Bash executable is unavailable"
            : !rtkAvailable
                ? "the pinned RTK executable is unavailable"
                : !workingDirectoryAvailable
                    ? "the warm filesystem working directory is unavailable"
                    : "the trusted Bash execution plan could not be prepared";
        return $"[ptk:dialect] not executed: the script contains {finding}, but {reason}; " +
               "the original script was NOT started and PTK did not request a retry.";
    }

    private static string? TryCaptureCurrentFileSystemLocation(Runspace runspace)
    {
        try
        {
            var path = runspace.SessionStateProxy.Path.CurrentFileSystemLocation.ProviderPath;
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                ? path
                : null;
        }
        catch { return null; }
    }

    private static bool IsCurrentLocationFileSystem(Runspace runspace)
    {
        try
        {
            return string.Equals(
                runspace.SessionStateProxy.Path.CurrentLocation.Provider.Name,
                "FileSystem",
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool AllowsAdvisoryFileWriteGuidance(Runspace runspace)
    {
        try
        {
            if (LanguagePrimitives.IsTrue(
                    runspace.SessionStateProxy.GetVariable("WhatIfPreference")))
            {
                return false;
            }

            if (runspace.SessionStateProxy.GetVariable("ConfirmPreference") is not
                ConfirmImpact.High)
            {
                return false;
            }

            return runspace.SessionStateProxy.GetVariable("PSDefaultParameterValues") is not
                System.Collections.IDictionary { Count: > 0 };
        }
        catch
        {
            // Advice is optional. If ambient semantics cannot be proven, stay
            // silent instead of suggesting a non-equivalent native redirect.
            return false;
        }
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
        if (_moduleSource is null) return null;
        var callDeadline = deadline ?? DateTimeOffset.UtcNow + _callTimeout;
        var work = Task.Run(() =>
        {
            try
            {
                var commands = CaptureBackgroundCommandFacts(script);
                var finding = TrustedPreflightClassifier.GetShellDialectFinding(script, commands);
                return finding is null
                    ? null
                    : FormatDialectRefusal(finding, BashAvailable(commands), background: true);
            }
            catch { return null; }
        }, CancellationToken.None);
        TrackOwnedBackgroundWork(work);
        if (await WaitForDeadlineAsync(work, callDeadline, cancellationToken) == WaitOutcome.Completed)
        {
            return await work;
        }
        return null;
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
        // A FAULTED rebuild counts as recovering too (i56-11): the next
        // invoke retries it, and until then the runspace is not servable —
        // reporting idle would be false.
        var recovering = !_runspaceReady.IsCompletedSuccessfully;
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
            TimedOut: true,
            Disposition: InvokeDisposition.NotStarted,
            UserExecutionStarted: false);
    }

    private InvokeResult ExecutionTimeoutResult(TimeSpan budget, bool userExecutionStarted) => new(
        Success: false,
        Output: string.Empty,
        Errors: [$"Call timed out: it exceeded its {budget.TotalSeconds:0}s wall-clock budget (queue wait + execution); the runspace was recycled and all warm state was lost. " +
            "Command and PATH resolution can differ in the fresh runspace - ptk_state shows what drifted. " +
            "For stateless long work (builds, watchers), rerun with background=true and poll with ptk_job. " +
            "For work that needs the warm session (live connections, imported modules), rerun with a larger timeoutSeconds."],
        Warnings: [],
        TimedOut: true,
        Disposition: userExecutionStarted
            ? InvokeDisposition.OutcomeUnknown
            : InvokeDisposition.NotStarted,
        UserExecutionStarted: userExecutionStarted,
        WarmStateLost: true);

    public Task<InvokeResult> InvokeAsync(string script, bool raw = false, CancellationToken cancellationToken = default, string route = "auto", int timeoutSeconds = 0, DateTimeOffset? deadline = null) =>
        InvokeCoreAsync(script, raw, cancellationToken, route, timeoutSeconds, deadline, authorizer: null);

    /// <summary>Production audited invocation path with distinct durable plan
    /// and dispatch barriers.</summary>
    internal Task<InvokeResult> InvokeAsync(
        string script,
        IInvocationAuthorizer authorizer,
        bool raw = false,
        CancellationToken cancellationToken = default,
        string route = "auto",
        int timeoutSeconds = 0,
        DateTimeOffset? deadline = null) =>
        InvokeCoreAsync(
            script,
            raw,
            cancellationToken,
            route,
            timeoutSeconds,
            deadline,
            authorizer ?? throw new ArgumentNullException(nameof(authorizer)));

    private async Task<InvokeResult> InvokeCoreAsync(
        string script,
        bool raw,
        CancellationToken cancellationToken,
        string route,
        int timeoutSeconds,
        DateTimeOffset? deadline,
        IInvocationAuthorizer? authorizer)
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
        return await InvokeGateHeldAsync(
            script, raw, route, callDeadline, budget, cancellationToken, authorizer);
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
        if (IdleInvocationOverrideForTests is { } idleOverride &&
            idleOverride(script) is { } overridden)
        {
            return overridden;
        }
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
        return await InvokeGateHeldAsync(
            script, raw, "auto", DateTimeOffset.UtcNow + budget, budget, cancellationToken,
            authorizer: null);
    }

    internal enum ReadyOutcome { Ready, TimedOut, Canceled, Faulted }

    // After a recycle, the replacement runspace is built off the response's
    // critical path; the next call picks it up here, under its own deadline.
    // Outcomes stay discriminated (codex finding i56-11): a cancel is not a
    // timeout, and a faulted rebuild is its own labeled failure — it also
    // kicks off a fresh attempt so one bad build cannot brick the server.
    private async Task<(PrimedRunspace? Primed, ReadyOutcome Outcome)> AwaitRunspaceReadyAsync(
        DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var ready = _runspaceReady;
        var wait = await WaitForDeadlineAsync(ready, deadline, cancellationToken);
        if (wait == WaitOutcome.TimedOut) return (null, ReadyOutcome.TimedOut);
        if (wait == WaitOutcome.Canceled) return (null, ReadyOutcome.Canceled);
        if (ready.IsCompletedSuccessfully)
        {
            // Metadata publishes at consumption, from the bundle that actually
            // won (i56-12): a superseded rebuild never stamps host state.
            PublishPrimedMetadata(ready.Result);
            return (ready.Result, ReadyOutcome.Ready);
        }
        Console.Error.WriteLine($"ptk: runspace rebuild failed ({ready.Exception?.GetBaseException().Message}); retrying in the background.");
        _runspaceReady = Task.Run(CreatePrimedRunspace);
        return (null, ReadyOutcome.Faulted);
    }

    private InvokeResult RecoveringResult(TimeSpan budget, ReadyOutcome outcome) => outcome switch
    {
        ReadyOutcome.Canceled => new InvokeResult(
            Success: false,
            Output: string.Empty,
            Errors: ["Call canceled by the caller while the replacement runspace was being rebuilt. The script was NOT executed."],
            Warnings: [],
            TimedOut: false,
            Disposition: InvokeDisposition.NotStarted,
            UserExecutionStarted: false,
            Recovering: true),
        ReadyOutcome.Faulted => new InvokeResult(
            Success: false,
            Output: string.Empty,
            Errors: ["Runspace rebuild FAILED after a previous recycle; a fresh rebuild was started. The script was NOT executed. Retry shortly."],
            Warnings: [],
            TimedOut: false,
            Disposition: InvokeDisposition.NotStarted,
            UserExecutionStarted: false,
            Recovering: true),
        _ => new InvokeResult(
            Success: false,
            Output: string.Empty,
            Errors: [$"Runspace recovering: a previous call timed out and the replacement runspace was not ready within this call's {budget.TotalSeconds:0}s wall-clock budget. " +
                "The script was NOT executed. Retry shortly, or raise timeoutSeconds."],
            Warnings: [],
            TimedOut: true,
            Disposition: InvokeDisposition.NotStarted,
            UserExecutionStarted: false,
            Recovering: true),
    };

    private static InvokeResult AuthorizationFailureResult(bool timedOut) => new(
        Success: false,
        Output: string.Empty,
        Errors: timedOut
            ? ["The wall-clock budget expired during pre-execution authorization. The script was NOT executed."]
            : ["Pre-execution authorization failed or was refused. The script was NOT executed."],
        Warnings: [],
        TimedOut: timedOut,
        Disposition: InvokeDisposition.NotStarted,
        UserExecutionStarted: false);

    private static InvokeResult BashValidationFailureResult(
        BashSyntaxValidationResult validation)
    {
        var status = validation.Status;
        var reason = status switch
        {
            BashSyntaxValidationStatus.SyntaxInvalid =>
                "Bash syntax validation rejected the submitted script",
            BashSyntaxValidationStatus.TimedOut =>
                "the bounded Bash syntax validator timed out",
            BashSyntaxValidationStatus.Canceled =>
                "the Bash syntax validator was canceled",
            BashSyntaxValidationStatus.StartFailed =>
                "the startup-pinned Bash validator could not start",
            BashSyntaxValidationStatus.StartOutcomeUnknown =>
                "the Bash validator start outcome could not be proven",
            BashSyntaxValidationStatus.RuntimeFailed =>
                "the Bash syntax validator failed internally",
            BashSyntaxValidationStatus.IdentityChanged =>
                "the startup-pinned Bash identity changed",
            BashSyntaxValidationStatus.AuditUnavailable =>
                "validator audit persistence became unavailable",
            _ => "Bash syntax validation did not authorize execution",
        };
        var termination = validation.RootTerminationConfirmed == false
            ? "; validator root-process termination could not be confirmed and descendant coverage is unknown"
            : string.Empty;
        return new InvokeResult(
            Success: false,
            Output: $"[ptk:dialect] not executed: {reason}{termination}; the original script was NOT started and PTK did not request a retry.",
            Errors: [],
            Warnings: [],
            TimedOut: status == BashSyntaxValidationStatus.TimedOut,
            Disposition: InvokeDisposition.NotStarted,
            UserExecutionStarted: false)
        {
            AuditDetailCode = validation.DetailCode,
        };
    }

    private static InvokeResult BashDependencyFailureResult(string detailCode) => new(
        Success: false,
        Output: "[ptk:dialect] not executed: the pinned Bash/RTK delegation identity became unavailable; the original script was NOT started and PTK did not request a retry.",
        Errors: [],
        Warnings: [],
        TimedOut: false,
        Disposition: InvokeDisposition.NotStarted,
        UserExecutionStarted: false)
    {
        AuditDetailCode = detailCode,
    };

    private static async Task<bool> InvokeAuthorizationBarrierAsync(
        Func<ValueTask<bool>> authorize)
    {
        ValueTask<bool> authorization;
        try
        {
            // Invoke exactly once. Observing the returned ValueTask never
            // retries a failed audit commit.
            authorization = authorize();
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            return await authorization;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ExecutionDispatch SelectDispatch(ExecutionPlan plan)
    {
        if (plan.ExecutionPath == ExecutionPath.Rtk &&
            plan.RtkExecutableIdentity is { } identity &&
            plan.PermittedFallbacks.Contains(ExecutionPath.PowerShellDirect) &&
            !identity.MatchesCurrentFile())
        {
            // Digest/path drift is a pre-start availability proof. Nothing in
            // this method starts the RTK or user process.
            return ExecutionDispatch.RtkUnavailableFallback(plan);
        }

        return ExecutionDispatch.FromPlan(plan);
    }

    private static InvokeResult WithDispatchRouting(
        InvokeResult result,
        ExecutionDispatch dispatch)
    {
        var warnings = result.Warnings;
        if (result.Success &&
            result.Disposition == InvokeDisposition.Completed &&
            result.Errors.Length == 0 &&
            !result.PipelineHadErrors &&
            result.ExitCode is null &&
            dispatch.PostSuccessGuidance is { } guidance)
        {
            warnings = [.. warnings, guidance.Render()];
        }
        return result with
        {
            Warnings = warnings,
            Routing = new ExecutionRouteSummary(
                dispatch.RequestedRoute,
                dispatch.ExecutionPath,
                dispatch.FallbackReason,
                dispatch.ExecutionPath == ExecutionPath.BashViaRtk ||
                string.Equals(
                    dispatch.ExecutionScript,
                    dispatch.Plan.OriginalScript,
                    StringComparison.Ordinal)),
        };
    }

    private static (string Output, OutputShapingSummary? Shaping)
        CaptureInvocationOutput(
            PSDataCollection<PSObject> results,
            RtkExecutableIdentity? authorizedShapingRtk)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count != 1 ||
            !results[0].TypeNames.Contains(
                "Ptk.OutputRoutingEnvelope",
                StringComparer.Ordinal))
        {
            return (string.Concat(results.Select(result => result?.ToString())), null);
        }

        var envelope = results[0];
        var text = envelope.Properties["Text"]?.Value?.ToString() ?? string.Empty;
        var code = envelope.Properties["ShapingCode"]?.Value?.ToString();
        var digest = envelope.Properties["RtkBinaryDigest"]?.Value?.ToString();
        var authorizedDigest = authorizedShapingRtk?.AuditBinaryDigest;
        var status = code switch
        {
            "rtk_log_used" => OutputShapingStatus.RtkLogUsed,
            "rtk_log_unavailable" => OutputShapingStatus.RtkLogUnavailable,
            "rtk_log_identity_unavailable" => OutputShapingStatus.RtkLogIdentityUnavailable,
            "rtk_log_failed" => OutputShapingStatus.RtkLogFailed,
            _ => OutputShapingStatus.ProtocolInvalid,
        };
        if (digest is not null &&
            (digest.Length != 64 || digest.Any(character => character is not (
                >= '0' and <= '9' or >= 'a' and <= 'f'))))
        {
            status = OutputShapingStatus.ProtocolInvalid;
        }
        if (!string.Equals(digest, authorizedDigest, StringComparison.Ordinal) ||
            (status is OutputShapingStatus.RtkLogUsed or
                OutputShapingStatus.RtkLogFailed) && authorizedDigest is null)
        {
            status = OutputShapingStatus.ProtocolInvalid;
        }

        return (text, new OutputShapingSummary(status, authorizedDigest));
    }

    private sealed class TrustedPreflightIsolationException(
        string message,
        Exception? innerException = null) : Exception(message, innerException);

    private async Task<InvokeResult> InvokeGateHeldAsync(
        string script,
        bool raw,
        string route,
        DateTimeOffset callDeadline,
        TimeSpan budget,
        CancellationToken cancellationToken,
        IInvocationAuthorizer? authorizer)
    {
        var ps = PowerShell.Create();
        var handedOff = false;
        try
        {
            var (primed, readyOutcome) = await AwaitRunspaceReadyAsync(callDeadline, cancellationToken);
            if (primed is null)
            {
                return RecoveringResult(budget, readyOutcome);
            }
            var runspace = primed.Runspace;

            // Trusted preflight executes no PowerShell. Read-only CLR pattern
            // enumeration captures plain facts without entering lookup hooks
            // or auto-import and without mutating any ambient user state; the
            // C# classifier then decides over those values only. raw skips
            // routing and shaping; route=pwsh bypasses by explicit consent.
            var checkDialect = primed.ModuleLoaded &&
                               !raw &&
                               !string.Equals(route, "pwsh", StringComparison.OrdinalIgnoreCase);
            var preflightDelay = PreflightDelayForTests;
            var preflight = Task.Run(() =>
            {
                if (preflightDelay > TimeSpan.Zero)
                    Thread.Sleep(preflightDelay);
                var effectiveRtkIdentity = EffectiveRtkIdentity();
                var plan = ExecutionPlanner.CreateDirect(
                    script,
                    route,
                    raw,
                    compressAvailable: !raw && primed.CompressCommand is not null,
                    ResolutionContext.Warm,
                    effectiveRtkIdentity);
                if (checkDialect)
                {
                    var commands = CaptureForegroundCommandFacts(runspace, script);
                    var workingDirectory =
                        TryCaptureCurrentFileSystemLocation(runspace);
                    var allowFileSystemGuidance =
                        IsCurrentLocationFileSystem(runspace) &&
                        AllowsAdvisoryFileWriteGuidance(runspace);
                    var assessment = TrustedPreflightClassifier.AssessShellDialect(script, commands);
                    var findingOverride = DialectFindingOverrideForTests;
                    var finding = findingOverride is null
                        ? assessment.Finding
                        : findingOverride(script);
                    if (finding is not null)
                    {
                        if (assessment.PowerShellParseFatal)
                        {
                            if (_bashExecutableIdentity is not null &&
                                effectiveRtkIdentity is not null &&
                                workingDirectory is not null)
                            {
                                plan = ExecutionPlanner.CreateBash(
                                    script,
                                    route,
                                    effectiveRtkIdentity,
                                    _bashExecutableIdentity,
                                    workingDirectory,
                                    ResolutionContext.Warm);
                                return (Refusal: (string?)null, Plan: plan);
                            }

                            return (
                                Refusal: FormatBashDelegationUnavailable(
                                    finding,
                                    _bashExecutableIdentity is not null,
                                    effectiveRtkIdentity is not null,
                                    workingDirectory is not null),
                                Plan: plan);
                        }

                        return (
                            Refusal: FormatDialectRefusal(
                                finding,
                                BashAvailable(commands),
                                background: false),
                            Plan: plan);
                    }
                    plan = ExecutionPlanner.Create(
                        script,
                        route,
                        effectiveRtkIdentity,
                        commands,
                        raw,
                        compressAvailable: !raw && primed.CompressCommand is not null,
                        ResolutionContext.Warm,
                        allowFileSystemGuidance);
                }
                return (Refusal: (string?)null, Plan: plan);
            }, CancellationToken.None);

            var preflightOutcome = await WaitForDeadlineAsync(preflight, callDeadline, cancellationToken);
            if (preflightOutcome == WaitOutcome.Canceled)
            {
                // Preflight is NOT user code and normally finishes in
                // milliseconds; a cancel that happens to land inside it (a
                // slow first call on a loaded machine — the ubuntu CI runner
                // found this live) must not cost the warm session. Give it
                // the same grace a canceled main pipeline gets to stop: if it
                // finishes, warm state is untouched; only a genuinely wedged
                // preflight recycles.
                if (await Task.WhenAny(preflight, Task.Delay(StopGrace)) == preflight)
                {
                    return new InvokeResult(
                        Success: false,
                        Output: string.Empty,
                        Errors: ["Call canceled by the caller during pre-execution checks; the script was not started and warm state was preserved."],
                        Warnings: [],
                        TimedOut: false,
                        Disposition: InvokeDisposition.NotStarted,
                        UserExecutionStarted: false);
                }
            }
            if (preflightOutcome != WaitOutcome.Completed)
            {
                // Snapshot capture touches the runspace's CLR session table. If
                // the combined snapshot/classifier task wedges, its access can
                // no longer be proven finished; recycle rather than dispatch or
                // preserve a runspace still observed by abandoned host work.
                RecycleAbandoning(preflight, runspace);
                return preflightOutcome == WaitOutcome.TimedOut
                    ? ExecutionTimeoutResult(budget, userExecutionStarted: false)
                    : new InvokeResult(
                        Success: false,
                        Output: string.Empty,
                        Errors: [$"Call canceled by the caller, and preflight did not finish within {StopGrace.TotalSeconds:0}s; the runspace was recycled and all warm state was lost."],
                        Warnings: [],
                        TimedOut: false,
                        Disposition: InvokeDisposition.NotStarted,
                        UserExecutionStarted: false,
                        WarmStateLost: true); // the flag must match the text (i56-14)
            }

            (string? preflightRefusal, ExecutionPlan plan) preflightResult;
            try
            {
                preflightResult = await preflight;
            }
            catch (TrustedPreflightIsolationException)
            {
                // A failed CLR snapshot leaves preflight unproven. Do not
                // dispatch or preserve a runspace still potentially observed
                // by abandoned host work.
                RecycleAbandoning(Task.CompletedTask, runspace);
                return new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors: ["Trusted pre-execution isolation failed; the script was NOT executed and the runspace was recycled."],
                    Warnings: [],
                    TimedOut: false,
                    Disposition: InvokeDisposition.NotStarted,
                    UserExecutionStarted: false,
                    WarmStateLost: true);
            }
            var (preflightRefusal, plan) = preflightResult;
            if (preflightRefusal is not null)
            {
                return new InvokeResult(
                    Success: false,
                    Output: preflightRefusal,
                    Errors: [],
                    Warnings: [],
                    TimedOut: false,
                    Disposition: InvokeDisposition.NotStarted,
                    UserExecutionStarted: false);
            }

            // Completion is not permission to CONTINUE: readiness or preflight
            // can finish after the wall deadline (sleep, late timers), and
            // starting the user pipeline then would begin side effects past
            // budget. Work already DONE returns its results; new work does not
            // start (codex finding i56-1, reopened leg).
            if (DateTimeOffset.UtcNow >= callDeadline)
            {
                return new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors: [$"The {budget.TotalSeconds:0}s wall-clock budget expired during pre-execution checks. " +
                        "The script was NOT executed and warm state is untouched. Retry, or raise timeoutSeconds."],
                    Warnings: [],
                    TimedOut: true,
                    Disposition: InvokeDisposition.NotStarted,
                    UserExecutionStarted: false);
            }

            if (authorizer is not null)
            {
                // The persistence barrier is server-owned: client
                // cancellation must not abandon it and let a terminal record
                // overtake a late plan append.
                if (!await InvokeAuthorizationBarrierAsync(() =>
                        authorizer.AuthorizePlanAsync(plan, CancellationToken.None)))
                {
                    return AuthorizationFailureResult(timedOut: false);
                }

                // Only after the durable barrier settles may the client token
                // or deadline terminate this request. In either case no
                // bookkeeping reset or user pipeline has started.
                if (cancellationToken.IsCancellationRequested)
                {
                    return AuthorizationFailureResult(timedOut: false);
                }

                // Audit durability consumes the same request budget. Never
                // begin execution merely because authorization completed a
                // moment after its deadline.
                if (DateTimeOffset.UtcNow >= callDeadline)
                {
                    return AuthorizationFailureResult(timedOut: true);
                }
            }

            ExecutionDispatch dispatch;
            try
            {
                // Selection occurs only after the exact prepared plan is
                // durable. It either preserves that plan or consumes one of
                // its bounded exact-semantics fallbacks before anything starts.
                dispatch = SelectDispatch(plan);
            }
            catch (Exception)
            {
                return AuthorizationFailureResult(timedOut: false);
            }

            if (authorizer is not null)
            {
                if (!await InvokeAuthorizationBarrierAsync(() =>
                        authorizer.AuthorizeDispatchAsync(dispatch, CancellationToken.None)))
                {
                    return AuthorizationFailureResult(timedOut: false);
                }

                // Dispatch persistence is another noncancelable barrier. Only
                // after it settles may request cancellation or the deadline
                // stop progress, and neither case has started user execution.
                if (cancellationToken.IsCancellationRequested)
                {
                    return AuthorizationFailureResult(timedOut: false);
                }

                if (DateTimeOffset.UtcNow >= callDeadline)
                {
                    return AuthorizationFailureResult(timedOut: true);
                }
            }

            if (dispatch.ExecutionPath == ExecutionPath.Rtk &&
                dispatch.RtkExecutableIdentity is { } dispatchedIdentity &&
                !dispatchedIdentity.MatchesCurrentFile())
            {
                // The durable dispatch barrier itself can outlive the RTK
                // path snapshot. Recheck after it, while still gate-held and
                // before resetting session state or starting a user pipeline.
                // The plan already bounded this exact fallback; authorize its
                // actual dispatch as a second pre-effect record rather than
                // asking the caller to reconstruct or retry the script.
                var fallbackDispatch = ExecutionDispatch.RtkUnavailableFallback(plan);
                if (authorizer is not null)
                {
                    if (!await InvokeAuthorizationBarrierAsync(() =>
                            authorizer.AuthorizeDispatchAsync(
                                fallbackDispatch,
                                CancellationToken.None)))
                    {
                        return AuthorizationFailureResult(timedOut: false);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return AuthorizationFailureResult(timedOut: false);
                    }

                    if (DateTimeOffset.UtcNow >= callDeadline)
                    {
                        return AuthorizationFailureResult(timedOut: true);
                    }
                }

                dispatch = fallbackDispatch;
            }

            if (dispatch.ExecutionPath == ExecutionPath.BashViaRtk)
            {
                if (dispatch.RtkExecutableIdentity is not { } bashRtk ||
                    !bashRtk.MatchesCurrentFile())
                {
                    return WithDispatchRouting(
                        BashDependencyFailureResult("rtk_identity_changed"),
                        dispatch);
                }

                var validation = await BashProcessRunner.ValidateAsync(
                    dispatch,
                    callDeadline,
                    cancellationToken,
                    () => authorizer is null
                        ? ValueTask.FromResult(true)
                        : authorizer.RecordValidatorStartedAsync(
                            dispatch,
                            CancellationToken.None),
                    BashValidationLimitForTests);

                if (validation.Status == BashSyntaxValidationStatus.AuditUnavailable)
                    return AuthorizationFailureResult(timedOut: false);

                if (authorizer is not null &&
                    !await InvokeAuthorizationBarrierAsync(() =>
                        authorizer.RecordValidatorCompletedAsync(
                            dispatch,
                            validation,
                            CancellationToken.None)))
                {
                    return AuthorizationFailureResult(timedOut: false);
                }

                if (validation.Status != BashSyntaxValidationStatus.Valid)
                {
                    return WithDispatchRouting(
                        BashValidationFailureResult(validation),
                        dispatch);
                }

                return WithDispatchRouting(
                    await BashProcessRunner.ExecuteAsync(
                        dispatch,
                        callDeadline,
                        cancellationToken),
                    dispatch);
            }

            // Reset only after the audited barrier authorizes this exact
            // preparation. A refusal/exception therefore performs no later
            // session mutation (the stale-LASTEXITCODE guard still precedes
            // every actual user pipeline).
            ResetExitCode(runspace);

            ps.Runspace = runspace;
            // useLocalScope: false — assignments land in the runspace's session
            // state and survive into the next call; that persistence is the point.
            ps.AddScript(dispatch.ExecutionScript!, useLocalScope: false);
            RtkExecutableIdentity? authorizedShapingRtk = null;
            if (!raw && primed.CompressCommand is not null)
            {
                authorizedShapingRtk = dispatch.OutputShapingRtkIdentity;
                ps.AddScript(
                        "$input | & $args[0] -InputProvenance $args[1] -PinnedRtkPath $args[2] -PinnedRtkDigest $args[3] -PinnedRtkUnixMode $args[4] -EmitRoutingEnvelope",
                        useLocalScope: true)
                  .AddArgument(primed.CompressCommand)
                  .AddArgument(dispatch.OutputProvenance.ToMachineCode())
                  .AddArgument(authorizedShapingRtk?.ExecutablePath)
                  .AddArgument(authorizedShapingRtk?.AuditBinaryDigest)
                  .AddArgument(authorizedShapingRtk?.CapturedUnixFileMode);
            }
            else
                ps.AddCommand("Microsoft.PowerShell.Utility\\Out-String");

            var invokeTask = InvokeAsyncWithoutAmbientAudit(ps);
            var outcome = await WaitForDeadlineAsync(invokeTask, callDeadline, cancellationToken);

            if (outcome == WaitOutcome.Canceled)
            {
                // A cancel (caller aborted, e.g. user Esc) is not a wedge —
                // stop the pipeline and keep the warm runspace; only recycle
                // if the pipeline refuses to stop within the grace period.
                if (await TryStopPipelineAsync(ps, invokeTask))
                {
                    return WithDispatchRouting(new InvokeResult(
                        Success: false,
                        Output: string.Empty,
                        Errors: ["Call canceled by the caller; the pipeline was stopped and warm state was preserved."],
                        Warnings: [],
                        TimedOut: false,
                        Disposition: InvokeDisposition.Canceled,
                        UserExecutionStarted: true), dispatch);
                }

                handedOff = true;
                AbandonAndRecycle(ps, invokeTask, runspace);
                return WithDispatchRouting(new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors: [$"Call canceled by the caller, but the pipeline did not stop within {StopGrace.TotalSeconds:0}s; the runspace was recycled and all warm state was lost."],
                    Warnings: [],
                    TimedOut: false,
                    Disposition: InvokeDisposition.OutcomeUnknown,
                    UserExecutionStarted: true,
                    WarmStateLost: true), dispatch);
            }

            if (outcome == WaitOutcome.TimedOut)
            {
                handedOff = true;
                AbandonAndRecycle(ps, invokeTask, runspace);
                // Teach the recovery paths at the moment of failure — the one
                // place a model reliably reads documentation (design P4).
                return WithDispatchRouting(
                    ExecutionTimeoutResult(budget, userExecutionStarted: true),
                    dispatch);
            }

            try
            {
                var results = await invokeTask;
                var (output, outputShaping) = CaptureInvocationOutput(
                    results,
                    authorizedShapingRtk);
                var (exitCode, wedged) = await ReadExitCodeBoundedAsync(runspace, callDeadline, cancellationToken);
                var (stderr, errors) = PartitionErrorStream(ps.Streams.Error);
                if (wedged) errors = [.. errors, BookkeepingWedgeNote];
                return WithDispatchRouting(new InvokeResult(
                    Success: true,
                    Output: output,
                    Errors: errors,
                    Warnings: [.. ps.Streams.Warning.Select(w => w.ToString())],
                    TimedOut: false,
                    Disposition: InvokeDisposition.Completed,
                    UserExecutionStarted: true,
                    ExitCode: exitCode == 0 ? null : exitCode,
                    Stderr: stderr,
                    WarmStateLost: wedged)
                {
                    OutputShaping = outputShaping,
                    PipelineHadErrors = ps.HadErrors,
                }, dispatch);
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
                return WithDispatchRouting(new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors: errors,
                    Warnings: [.. ps.Streams.Warning.Select(w => w.ToString())],
                    TimedOut: false,
                    Disposition: InvokeDisposition.Failed,
                    UserExecutionStarted: true,
                    ExitCode: exitCode == 0 ? null : exitCode,
                    Stderr: stderr,
                    WarmStateLost: wedged), dispatch);
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
    public async Task<string> ShapeTextAsync(
        string text,
        CancellationToken cancellationToken = default,
        string? elisionHint = null) =>
        (await ShapeTextCoreAsync(
            text,
            cancellationToken,
            elisionHint,
            EffectiveRtkIdentity(),
            runspaceRecycled: null)).Text;

    internal RtkExecutableIdentity? CaptureOutputShapingRtkIdentity() =>
        EffectiveRtkIdentity();

    internal Task<ShapedTextResult> ShapeTextAuditedAsync(
        string text,
        CancellationToken cancellationToken,
        string? elisionHint,
        RtkExecutableIdentity? authorizedShapingRtk,
        Action runspaceRecycled) =>
        ShapeTextCoreAsync(
            text,
            cancellationToken,
            elisionHint,
            authorizedShapingRtk,
            runspaceRecycled);

    private async Task<ShapedTextResult> ShapeTextCoreAsync(
        string text,
        CancellationToken cancellationToken,
        string? elisionHint,
        RtkExecutableIdentity? authorizedShapingRtk,
        Action? runspaceRecycled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ModuleLoaded || text.Length == 0) return new(text, null);
        LastActivityUtc = DateTimeOffset.UtcNow;
        var deadline = DateTimeOffset.UtcNow + _callTimeout;
        // A poll must never block behind a long foreground call either: on a
        // gate that stays busy past the budget, return the text unshaped —
        // shaping must never fail (or stall) a poll.
        if (!await TryEnterGateAsync(deadline, cancellationToken))
        {
            return new(text, null);
        }
        var ps = PowerShell.Create();
        var handedOff = false;
        try
        {
            var (primed, _) = await AwaitRunspaceReadyAsync(deadline, cancellationToken);
            if (primed?.CompressCommand is null) return new(text, null);
            var runspace = primed.Runspace;
            ps.Runspace = runspace;
            ps.AddScript(
                    "if ($args.Count -gt 4) { " +
                    "$input | & $args[0] -PinnedRtkPath $args[1] -PinnedRtkDigest $args[2] -PinnedRtkUnixMode $args[3] -ElisionHint $args[4] -EmitRoutingEnvelope " +
                    "} else { $input | & $args[0] -PinnedRtkPath $args[1] -PinnedRtkDigest $args[2] -PinnedRtkUnixMode $args[3] -EmitRoutingEnvelope }",
                    useLocalScope: true)
              .AddArgument(primed.CompressCommand)
              .AddArgument(authorizedShapingRtk?.ExecutablePath)
              .AddArgument(authorizedShapingRtk?.AuditBinaryDigest)
              .AddArgument(authorizedShapingRtk?.CapturedUnixFileMode);
            // Context-correct elision advice, composed BY the elision itself
            // (sd3-2..sd3-4): the caller knows its recovery path; inferring
            // elision downstream from the shaped text proved unsound in both
            // directions.
            if (elisionHint is not null) ps.AddArgument(elisionHint);
            var input = new PSDataCollection<object> { text };
            input.Complete();

            OutputShapingFailureForTests?.Invoke();
            var invokeTask = InvokeAsyncWithoutAmbientAudit(ps, input);
            var outcome = await WaitForDeadlineAsync(invokeTask, deadline, cancellationToken);
            if (outcome != WaitOutcome.Completed)
            {
                if (outcome == WaitOutcome.Canceled && await TryStopPipelineAsync(ps, invokeTask))
                {
                    return new(
                        text + Environment.NewLine +
                        "[ptk: shaping canceled; raw text returned]",
                        new OutputShapingSummary(
                            OutputShapingStatus.PipelineCanceled,
                            authorizedShapingRtk?.AuditBinaryDigest));
                }
                handedOff = true;
                AbandonAndRecycle(ps, invokeTask, runspace);
                runspaceRecycled?.Invoke();
                return new(
                    text + Environment.NewLine +
                    "[ptk: shaping timed out; the runspace was recycled; raw text returned]",
                    new OutputShapingSummary(
                        OutputShapingStatus.PipelineTimedOut,
                        authorizedShapingRtk?.AuditBinaryDigest));
            }

            var results = await invokeTask;
            var (output, shaping) = CaptureInvocationOutput(
                results,
                authorizedShapingRtk);
            return new(output, shaping);
        }
        catch
        {
            return new(
                text + Environment.NewLine +
                "[ptk: shaping failed; raw text returned]",
                new OutputShapingSummary(
                    OutputShapingStatus.PipelineFailed,
                    authorizedShapingRtk?.AuditBinaryDigest));
        }
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
        /// <summary>A post-recycle rebuild was still in flight; the probe never
        /// ran. Distinct from queue contention: no other call holds the gate,
        /// and the queue-expiry message's claims would be false (i56-11).</summary>
        Recovering,
    }

    /// <summary>Current directory of the warm session (background jobs start
    /// there). The caller must FAIL the job start on anything but Ok rather
    /// than silently start the job in the server process cwd — the wrong
    /// project (plan finding i56p-4; codex finding i56-5). Cancellation
    /// propagates.</summary>
    public async Task<(string? Path, CwdProbeOutcome Outcome)> TryGetCurrentLocationAsync(
        CancellationToken cancellationToken = default,
        DateTimeOffset? deadline = null,
        Action? canceledRecycleObserver = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var callDeadline = deadline ?? DateTimeOffset.UtcNow + _callTimeout;
        LastActivityUtc = DateTimeOffset.UtcNow;
        if (!await TryEnterGateAsync(callDeadline, cancellationToken))
            return (null, CwdProbeOutcome.QueueExpired);

        MarkGateAcquired();
        try
        {
            var (primed, readyOutcome) = await AwaitRunspaceReadyAsync(callDeadline, cancellationToken);
            if (primed is null)
            {
                return readyOutcome == ReadyOutcome.Canceled
                    ? throw new OperationCanceledException(cancellationToken)
                    : (null, CwdProbeOutcome.Recovering);
            }
            var runspace = primed.Runspace;

            // Read the provider intrinsic directly. Running `(Get-Location).Path`
            // as a user-scope script allowed a prior call to shadow Get-Location
            // and execute arbitrary effects before the background job's durable
            // start authorization.
            var locationReader = CurrentLocationReaderOverrideForTests;
            var probe = Task.Run(() =>
            {
                if (locationReader is not null)
                    return locationReader();
                var location = runspace.SessionStateProxy.Path.CurrentLocation;
                return string.Equals(location.Provider?.Name, "FileSystem", StringComparison.OrdinalIgnoreCase)
                    ? location.Path
                    : null;
            }, CancellationToken.None);
            var probeOutcome = await WaitForDeadlineAsync(probe, callDeadline, cancellationToken);
            if (probeOutcome == WaitOutcome.Canceled)
            {
                if (await Task.WhenAny(probe, Task.Delay(StopGrace)) == probe)
                    throw new OperationCanceledException(cancellationToken);
                RecycleAbandoning(probe, runspace);
                canceledRecycleObserver?.Invoke();
                throw new OperationCanceledException(cancellationToken);
            }
            if (probeOutcome == WaitOutcome.TimedOut)
            {
                RecycleAbandoning(probe, runspace);
                return (null, CwdProbeOutcome.TimedOutExecuting);
            }

            var path = await probe;
            return path is { Length: > 0 }
                ? (path, CwdProbeOutcome.Ok)
                : (null, CwdProbeOutcome.Failed);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (null, CwdProbeOutcome.Failed); }
        finally
        {
            ReleaseGate();
        }
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
            var fresh = CreatePrimedRunspace();
            PublishPrimedMetadata(fresh);
            _runspaceReady = Task.FromResult(fresh);
            RestoreEnvironmentBaseline();
            TrackOwnedBackgroundWork(DisposeRunspaceWhenReadyAsync(oldReady));
        }
        finally
        {
            ReleaseGate();
        }
    }

    // How long a canceled pipeline gets to stop before it is treated as wedged
    // and the runspace is recycled anyway.
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

    /// <summary>Test hook for the otherwise platform/race-dependent path where
    /// cancellation cannot confirm that an already-started pipeline stopped.</summary>
    internal bool ForcePipelineStopFailureForTests { get; set; }

    /// <summary>Stops a canceled pipeline in place. True = it stopped within the
    /// grace period and the runspace is safe to keep using; false = treat as wedged.</summary>
    private async Task<bool> TryStopPipelineAsync(PowerShell ps, Task invokeTask)
    {
        if (ForcePipelineStopFailureForTests) return false;

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
        var cleanup = Task.Run(async () =>
        {
            try { wedged.Stop(); } catch { /* best effort */ }
            try { await abandonedInvoke; } catch { /* observe, else unobserved-task noise */ }
            wedged.Dispose();
            old.Dispose();
        });
        TrackOwnedBackgroundWork(cleanup);
    }

    // Recycle when there is no PowerShell handle to stop (a wedged preflight
    // or bookkeeping task): disposing the old runspace tears down whatever
    // pipeline the abandoned task is stuck in; the task is awaited only to
    // observe its eventual exception.
    private void RecycleAbandoning(Task abandonedWork, Runspace old)
    {
        _runspaceReady = Task.Run(CreatePrimedRunspace);
        var cleanup = Task.Run(async () =>
        {
            Exception? disposalFailure = null;
            try { old.Dispose(); }
            catch (Exception exception) { disposalFailure = exception; }
            try { await abandonedWork; } catch { /* observe, else unobserved-task noise */ }
            if (disposalFailure is not null)
                throw new IOException("Abandoned runspace disposal failed.", disposalFailure);
        });
        TrackOwnedBackgroundWork(cleanup);
    }

    private static async Task DisposeRunspaceWhenReadyAsync(Task<PrimedRunspace> ready)
    {
        PrimedRunspace primed;
        try { primed = await ready.ConfigureAwait(false); }
        catch { return; } // A faulted rebuild never published a live replacement.
        primed.Runspace.Dispose();
    }

    /// <summary>
    /// Completes teardown of the current or rebuilding runspace. Unlike the
    /// synchronous best-effort Dispose path, audited server shutdown awaits a
    /// pending rebuild so server.stopped cannot overtake runspace creation or
    /// destruction that still belongs to this process lifetime.
    /// </summary>
    internal async Task ShutdownAsync()
    {
        if (_disposed) return;

        _disposed = true;
        var ready = _runspaceReady;
        await DisposeRunspaceWhenReadyAsync(ready).ConfigureAwait(false);
        await DrainOwnedBackgroundWorkAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var ready = _runspaceReady;
        if (ready.IsCompletedSuccessfully)
        {
            try { ready.Result.Runspace.Dispose(); } catch { /* best effort on teardown */ }
        }
        else
        {
            // A pending rebuild is disposed when it materializes; a faulted
            // one has nothing to dispose.
            _ = ready.ContinueWith(
                t => { try { t.Result.Runspace.Dispose(); } catch { /* best effort */ } },
                TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        _gate.Dispose();
    }
}
