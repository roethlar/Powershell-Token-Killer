using System.Collections;
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
    internal OutputRecoverySummary? OutputRecovery { get; init; }
    internal bool PipelineHadErrors { get; init; }
    internal ExecutionFallbackReason? ProvenPreStartFallbackReason { get; init; }
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

internal sealed record OutputRecoverySummary(
    string? Handle,
    OutputArtifactState State,
    long Bytes,
    string? DetailCode,
    bool Advertise)
{
    internal static OutputRecoverySummary FromSeal(OutputSealResult result) => new(
        result.Handle,
        result.State,
        result.Bytes,
        result.DetailCode,
        Advertise: result.Success && result.Handle is not null);

    internal static OutputRecoverySummary Unavailable(
        string detailCode,
        bool advertise = false) => new(
            Handle: null,
            State: OutputArtifactState.NotFound,
            Bytes: 0,
            DetailCode: detailCode,
            Advertise: advertise);
}

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
    private int _outputProcessorUnavailable;

    internal Action? PrivateOutputRunspaceOpeningForTests { get; set; }
    internal Action? PrivateOutputRunspaceOpenedForTests { get; set; }
    internal Action? PrivateOutputInvocationStartingForTests { get; set; }
    internal Action? PrivateOutputInvocationStartedForTests { get; set; }
    internal Func<PowerShell, PSDataCollection<PSObject>,
        Task<PSDataCollection<PSObject>>>? PrivateOutputInvocationOverrideForTests { get; set; }
    internal Action? PrivateOutputStopStartingForTests { get; set; }
    internal Action? PrivateOutputProcessorDisposingForTests { get; set; }
    internal bool OutputProcessorUnavailableForTests =>
        Volatile.Read(ref _outputProcessorUnavailable) != 0;

    internal void TrackOwnedBackgroundWorkForTests(Task work) => TrackOwnedBackgroundWork(work);
    internal void AbandonOwnedOutputPipelineForTests(Task work) =>
        AbandonOwnedPipeline(PowerShell.Create(), work);

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

    private static Task<PSDataCollection<PSObject>> InvokeAsyncWithoutAmbientAudit(
        PowerShell powerShell,
        PSDataCollection<PSObject> input)
    {
        using var suppressed = ExecutionContext.SuppressFlow();
        return powerShell.InvokeAsync(input);
    }

    private static Task<PSDataCollection<PSObject>> InvokeAsyncWithoutAmbientAudit(
        PowerShell powerShell,
        PSDataCollection<object> input,
        PSDataCollection<PSObject> output)
    {
        using var suppressed = ExecutionContext.SuppressFlow();
        return powerShell.InvokeAsync<object, PSObject>(input, output);
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

    private static Runspace CreateRunspace(
        Action? creationStartingForTests = null,
        Func<InternalOutputPipelineStatus?>? startBlocked = null)
    {
        lock (CreationLock)
        {
            creationStartingForTests?.Invoke();
            if (startBlocked?.Invoke() is { } beforeConstruction)
                throw new PrivateOutputStartupBlockedException(beforeConstruction);
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
            try
            {
                // A private opener may have waited behind another host in
                // CreationLock. Recheck immediately before the unbounded Open
                // so an expired waiter cannot begin provider initialization.
                if (startBlocked?.Invoke() is { } beforeOpen)
                    throw new PrivateOutputStartupBlockedException(beforeOpen);
                runspace.Open();
                return runspace;
            }
            catch
            {
                runspace.Dispose();
                throw;
            }
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

    /// <summary>Gate-held, pipeline-free test observation of the automatic
    /// values whose post-user state private output handling must preserve.</summary>
    internal async Task<(object? LastExitCode, object[] Errors)>
        CaptureWarmAutomaticStateForTestsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        MarkGateAcquired();
        try
        {
            if (!_runspaceReady.IsCompletedSuccessfully)
                throw new InvalidOperationException("The warm runspace is not ready.");
            var runspace = _runspaceReady.Result.Runspace;
            var snapshot = TryCaptureWarmAutomaticState(runspace)
                ?? throw new InvalidOperationException("Automatic state is unavailable.");
            var errors = runspace.SessionStateProxy.GetVariable("Error") is IList list
                ? list.Cast<object>().ToArray()
                : [];
            return (snapshot.LastExitCode, errors);
        }
        finally
        {
            ReleaseGate();
        }
    }

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

    private static void SetExitCode(Runspace runspace, int exitCode)
    {
        try
        {
            // SessionStateProxy writes the global session value directly; it
            // does not run a PowerShell pipeline or touch the warm $Error list.
            runspace.SessionStateProxy.SetVariable("LASTEXITCODE", exitCode);
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

    private static string? TryCaptureNativeArgumentPassing(Runspace runspace)
    {
        try
        {
            return runspace.SessionStateProxy
                .GetVariable("PSNativeCommandArgumentPassing")
                ?.ToString();
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
        InvokeCoreAsync(
            script,
            raw,
            cancellationToken,
            route,
            timeoutSeconds,
            deadline,
            authorizer: null,
            outputCapture: null);

    /// <summary>Supervisor capture path. The reservation is created before the
    /// user script can be committed and is consumed at most once by this call.</summary>
    internal Task<InvokeResult> InvokeWithOutputCaptureAsync(
        string script,
        ForegroundOutputCapture outputCapture,
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
            authorizer: null,
            outputCapture ?? throw new ArgumentNullException(nameof(outputCapture)));

    /// <summary>Production audited invocation path with distinct durable plan
    /// and dispatch barriers.</summary>
    internal Task<InvokeResult> InvokeAsync(
        string script,
        IInvocationAuthorizer authorizer,
        bool raw = false,
        CancellationToken cancellationToken = default,
        string route = "auto",
        int timeoutSeconds = 0,
        DateTimeOffset? deadline = null,
        ForegroundOutputCapture? outputCapture = null) =>
        InvokeCoreAsync(
            script,
            raw,
            cancellationToken,
            route,
            timeoutSeconds,
            deadline,
            authorizer ?? throw new ArgumentNullException(nameof(authorizer)),
            outputCapture);

    private async Task<InvokeResult> InvokeCoreAsync(
        string script,
        bool raw,
        CancellationToken cancellationToken,
        string route,
        int timeoutSeconds,
        DateTimeOffset? deadline,
        IInvocationAuthorizer? authorizer,
        ForegroundOutputCapture? outputCapture)
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
            script,
            raw,
            route,
            callDeadline,
            budget,
            cancellationToken,
            authorizer,
            outputCapture);
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
            authorizer: null,
            outputCapture: null);
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

    private sealed record WarmAutomaticState(object? LastExitCode);

    private sealed record InvocationPrefixSnapshot(
        string[] Output,
        string[] StandardError,
        string[] Errors,
        string[] Warnings);

    private sealed record PassiveOutputSnapshot(
        PSObject[] Output,
        InvocationPrefixSnapshot Prefix,
        long TotalOutputCount,
        bool CaptureBoundExceeded,
        bool ActiveMemberOmitted,
        bool LossyProjection,
        bool CaptureFailed,
        string DetachedTypeNonce)
    {
        internal string? IncompleteReason => CaptureFailed
            ? "passive_capture_failed"
            : CaptureBoundExceeded
                ? "capture_bound_exceeded"
                : ActiveMemberOmitted
                    ? "active_member_not_evaluated"
                    : LossyProjection
                        ? "passive_projection_lossy"
                        : null;

        internal string? LimitationNote => IncompleteReason is null
            ? null
            : $"[ptk:capture incomplete reason={IncompleteReason} " +
              $"retained={Output.Length} total={TotalOutputCount}]";
    }

    /// <summary>Drains producer-owned collections as they grow and retains only
    /// bounded, passive DTOs. Active PowerShell members are executable user
    /// code, so this collector never reads them merely to display output.</summary>
    private sealed class BoundedPassiveOutputCapture
    {
        internal const string ActiveMemberMarker =
            "[active member not evaluated]";

        private readonly object _gate = new();
        private readonly object _drainGate = new();
        private readonly List<PSObject> _captured = [];
        private readonly List<string> _output = [];
        private readonly List<string> _standardError = [];
        private readonly List<string> _errors = [];
        private readonly List<string> _warnings = [];
        private long _remainingProjectionBytes;
        private long _remainingPrefixBytes;
        private long _totalOutputCount;
        private bool _captureBoundExceeded;
        private bool _activeMemberOmitted;
        private bool _lossyProjection;
        private bool _captureFailed;
        private bool _prefixCapacityMarkerWritten;
        private readonly string _detachedTypeNonce = Guid.NewGuid().ToString("N");
        private static readonly System.Reflection.FieldInfo? PsObjectInstanceMembers =
            typeof(PSObject).GetField(
                "_instanceMembers",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
        private PSDataCollection<PSObject>? _outputSource;
        private PSDataCollection<ErrorRecord>? _errorSource;
        private PSDataCollection<WarningRecord>? _warningSource;

        internal BoundedPassiveOutputCapture(long maximumBytes)
        {
            _remainingProjectionBytes = Math.Max(0, maximumBytes);
            _remainingPrefixBytes = Math.Max(0, maximumBytes);
        }

        internal void Attach(
            PSDataCollection<PSObject> output,
            PSDataCollection<ErrorRecord> errors,
            PSDataCollection<WarningRecord> warnings)
        {
            _outputSource = output;
            _errorSource = errors;
            _warningSource = warnings;
            output.DataAdded += (_, _) => DrainOutput(required: false);
            errors.DataAdded += (_, _) => DrainErrors(required: false);
            warnings.DataAdded += (_, _) => DrainWarnings(required: false);
        }

        private Collection<T>? TryReadAll<T>(PSDataCollection<T>? source)
        {
            if (!Monitor.TryEnter(_drainGate)) return null;
            try
            {
                try { return source?.ReadAll() ?? []; }
                catch
                {
                    MarkCaptureFailed();
                    return [];
                }
            }
            finally
            {
                Monitor.Exit(_drainGate);
            }
        }

        private void MarkCaptureFailed()
        {
            if (Monitor.TryEnter(_gate))
            {
                try { Volatile.Write(ref _captureFailed, true); }
                finally { Monitor.Exit(_gate); }
            }
            else
            {
                // A deadline snapshot never waits for this monitor. The flag
                // may be observed on the next successful snapshot instead.
                Volatile.Write(ref _captureFailed, true);
            }
        }

        private void DrainOutput(bool required)
        {
            var batch = TryReadAll(_outputSource);
            if (batch is null)
            {
                if (required) MarkCaptureFailed();
                return;
            }

            try
            {
                lock (_gate)
                {
                    foreach (var value in batch)
                    {
                        Interlocked.Increment(ref _totalOutputCount);
                        var baseObject = value?.BaseObject;
                        var text = TryPassiveScalar(baseObject, out _, out var scalarText)
                            ? scalarText
                            : "[captured object; canonical rendering is unavailable until the invocation completes]";
                        AppendPrefixBounded(_output, text);
                        ProjectOutput(value);
                    }
                }
            }
            catch
            {
                MarkCaptureFailed();
            }
        }

        private void DrainErrors(bool required)
        {
            var batch = TryReadAll(_errorSource);
            if (batch is null)
            {
                if (required) MarkCaptureFailed();
                return;
            }

            try
            {
                lock (_gate)
                {
                    foreach (var error in batch)
                    {
                        var safe = TryFreezeErrorRecord(error, out var text);
                        if (!safe) _activeMemberOmitted = true;
                        AppendPrefixBounded(
                            IsNativeStderrRecord(error) ? _standardError : _errors,
                            text);
                    }
                }
            }
            catch { MarkCaptureFailed(); }
        }

        private void DrainWarnings(bool required)
        {
            var batch = TryReadAll(_warningSource);
            if (batch is null)
            {
                if (required) MarkCaptureFailed();
                return;
            }

            try
            {
                lock (_gate)
                {
                    foreach (var warning in batch)
                        AppendPrefixBounded(_warnings, warning.Message ?? string.Empty);
                }
            }
            catch { MarkCaptureFailed(); }
        }

        internal static bool TryFreezeErrorRecord(ErrorRecord error, out string text)
        {
            const string omitted =
                "[PowerShell error text omitted because its exception type was not safe to inspect]";
            try
            {
                if (!string.IsNullOrEmpty(error.ErrorDetails?.Message))
                {
                    text = error.ErrorDetails.Message;
                    return true;
                }

                var exception = error.Exception;
                if (exception is null || !IsTrustedPowerShellAssembly(exception.GetType().Assembly))
                {
                    text = omitted;
                    return false;
                }

                text = exception.Message ?? string.Empty;
                return true;
            }
            catch
            {
                text = omitted;
                return false;
            }
        }

        private static bool IsTrustedPowerShellAssembly(System.Reflection.Assembly assembly)
        {
            var name = assembly.GetName();
            var token = name.GetPublicKeyToken();
            return (name.Name == "System.Management.Automation" ||
                    name.Name == "Microsoft.PowerShell.Commands.Utility") &&
                   token is [0x31, 0xbf, 0x38, 0x56, 0xad, 0x36, 0x4e, 0x35];
        }

        private void ProjectOutput(PSObject? source)
        {
            if (source is null) return;
            var baseObject = source.BaseObject;
            if (TryPassiveScalar(baseObject, out var scalar, out var scalarText))
            {
                if (!TryChargeProjection(MeasureRetainedText(scalarText) + 64))
                    return;
                _captured.Add(PSObject.AsPSObject(scalar ?? string.Empty));
                return;
            }

            const long objectOverheadBytes = 256;
            if (!TryChargeProjection(objectOverheadBytes)) return;

            var detached = new PSObject();
            detached.TypeNames.Clear();
            var category = PassiveCategory(baseObject);
            var detachedTypeName =
                $"Ptk.Detached.{category}.{_detachedTypeNonce}";
            if (!TryChargeProjection(MeasureRetainedText(detachedTypeName) + 64))
                return;
            detached.TypeNames.Add(detachedTypeName);

            var runtimeType = baseObject?.GetType();
            if (runtimeType == typeof(FileInfo) || runtimeType == typeof(DirectoryInfo))
            {
                _lossyProjection = true;
                var fileSystemInfo = (FileSystemInfo)baseObject!;
                AddPassiveProperty(detached, "PSIsContainer", baseObject is DirectoryInfo);
                AddTrustedProperty(detached, "Name", () => fileSystemInfo.Name);
                AddTrustedProperty(
                    detached,
                    "LastWriteTime",
                    () => fileSystemInfo.LastWriteTime);
                if (baseObject is FileInfo file)
                    AddTrustedProperty(detached, "Length", () => file.Length);
            }
            else if (runtimeType == typeof(Microsoft.PowerShell.Commands.MatchInfo))
            {
                _lossyProjection = true;
                var match = (Microsoft.PowerShell.Commands.MatchInfo)baseObject!;
                AddPassiveProperty(detached, "LineNumber", match.LineNumber);
                AddPassiveProperty(detached, "Path", match.Path);
                AddPassiveProperty(detached, "Line", match.Line);
            }
            else if (runtimeType == typeof(System.Diagnostics.Process))
            {
                _lossyProjection = true;
                var process = (System.Diagnostics.Process)baseObject!;
                AddTrustedProperty(detached, "Id", () => process.Id);
                AddTrustedProperty(detached, "ProcessName", () => process.ProcessName);
                AddTrustedProperty(
                    detached,
                    "CPU",
                    () => process.TotalProcessorTime.TotalSeconds);
                AddTrustedProperty(detached, "WorkingSet64", () => process.WorkingSet64);
            }
            else if (baseObject is PSCustomObject)
            {
                if (!HasOnlyDefaultCustomObjectTypeNames(source))
                    _activeMemberOmitted = true;
                CopyPassiveInstanceNotes(detached, source);
            }
            else
            {
                CopyPassiveInstanceNotes(detached, source);
                AddActiveMemberPlaceholder(detached, "Value");
            }

            _captured.Add(detached);
        }

        private static string PassiveCategory(object? value) => value switch
        {
            FileInfo => "System.IO.FileInfo",
            DirectoryInfo => "System.IO.DirectoryInfo",
            Microsoft.PowerShell.Commands.MatchInfo =>
                "Microsoft.PowerShell.Commands.MatchInfo",
            System.Diagnostics.Process => "System.Diagnostics.Process",
            PSCustomObject => "PSCustomObject",
            _ => "Object",
        };

        private static bool HasOnlyDefaultCustomObjectTypeNames(PSObject source)
        {
            // TypeNames is PSObject's own inert string collection; unlike the
            // ETS Properties collection it does not consult PSPropertyAdapter.
            // Index a bounded default set rather than enumerate a user-grown
            // collection on the producer callback.
            var typeNames = source.TypeNames;
            if (typeNames.Count is < 1 or > 2) return false;
            for (var index = 0; index < typeNames.Count; index++)
            {
                if (typeNames[index] is not
                    ("System.Management.Automation.PSCustomObject" or "System.Object"))
                {
                    return false;
                }
            }
            return true;
        }

        private void CopyPassiveInstanceNotes(PSObject detached, PSObject source)
        {
            object? rawMembers;
            try { rawMembers = PsObjectInstanceMembers?.GetValue(source); }
            catch
            {
                _captureFailed = true;
                AddActiveMemberPlaceholder(detached, "OmittedMember");
                return;
            }

            if (rawMembers is null) return;
            if (rawMembers is not IEnumerable members)
            {
                _captureFailed = true;
                AddActiveMemberPlaceholder(detached, "OmittedMember");
                return;
            }

            var omitted = false;
            try
            {
                foreach (var member in members)
                {
                    // Exact type only: a user subclass must not override an
                    // apparently passive Value/Name getter. Reading the
                    // internal instance-member bag bypasses ETS adapters.
                    if (member?.GetType() == typeof(PSNoteProperty))
                    {
                        var note = (PSNoteProperty)member;
                        AddPassiveProperty(
                            detached,
                            note.Name,
                            PassiveNoteValue(note.Value));
                    }
                    else
                    {
                        omitted = true;
                    }
                }
            }
            catch
            {
                _captureFailed = true;
                omitted = true;
            }

            if (omitted) AddActiveMemberPlaceholder(detached, "OmittedMember");
        }

        private static bool TryPassiveScalar(
            object? value,
            out object? scalar,
            out string text)
        {
            scalar = value;
            switch (value)
            {
                case null:
                    text = string.Empty;
                    return true;
                case string valueText:
                    text = valueText;
                    return true;
                case char character:
                    text = character.ToString();
                    return true;
                case bool boolean:
                    text = boolean ? "True" : "False";
                    return true;
                case byte or sbyte or short or ushort or int or uint or long or
                     ulong or float or double or decimal:
                    text = Convert.ToString(
                        value,
                        System.Globalization.CultureInfo.InvariantCulture) ??
                        string.Empty;
                    return true;
                case DateTime dateTime:
                    text = dateTime.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                case DateTimeOffset dateTimeOffset:
                    text = dateTimeOffset.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                case Guid guid:
                    text = guid.ToString();
                    return true;
                case TimeSpan timeSpan:
                    text = timeSpan.ToString(
                        format: null,
                        System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                case Enum enumeration:
                    text = enumeration.ToString();
                    return true;
                default:
                    scalar = null;
                    text = string.Empty;
                    return false;
            }
        }

        private object? PassiveNoteValue(object? value)
        {
            var baseObject = value is PSObject wrapped ? wrapped.BaseObject : value;
            if (TryPassiveScalar(baseObject, out var scalar, out _)) return scalar;
            _activeMemberOmitted = true;
            return baseObject is null
                ? null
                : $"[nested {baseObject.GetType().FullName ?? "object"} not expanded during passive output capture]";
        }

        private void AddTrustedProperty(
            PSObject detached,
            string name,
            Func<object?> read)
        {
            try { AddPassiveProperty(detached, name, PassiveNoteValue(read())); }
            catch { AddActiveMemberPlaceholder(detached, name); }
        }

        private void AddActiveMemberPlaceholder(PSObject detached, string name)
        {
            _activeMemberOmitted = true;
            AddPassiveProperty(detached, name, ActiveMemberMarker);
        }

        private void AddPassiveProperty(
            PSObject detached,
            string name,
            object? value)
        {
            var valueText = TryPassiveScalar(value, out _, out var scalarText)
                ? scalarText
                : ActiveMemberMarker;
            if (!TryChargeProjection(
                MeasureRetainedText(name) +
                MeasureRetainedText(valueText) +
                128))
            {
                return;
            }
            try { detached.Properties.Add(new PSNoteProperty(name, value)); }
            catch { _captureFailed = true; }
        }

        private bool TryChargeProjection(long bytes)
        {
            if (bytes <= _remainingProjectionBytes)
            {
                _remainingProjectionBytes -= bytes;
                return true;
            }
            _captureBoundExceeded = true;
            return false;
        }

        private static long MeasureRetainedText(string text) =>
            Math.Max(
                (long)Encoding.UTF8.GetByteCount(text),
                (long)text.Length * sizeof(char));

        private void AppendPrefixBounded(List<string> destination, string text)
        {
            // Charge both the retained UTF-16 string/list footprint and the
            // rendered UTF-8 framing. In particular, empty-string floods must
            // consume capacity instead of growing the prefix lists forever.
            const int retainedEntryOverheadBytes = 64;
            var textBytes = MeasureRetainedText(text);
            var bytes = textBytes +
                Encoding.UTF8.GetByteCount(Environment.NewLine) +
                retainedEntryOverheadBytes;
            if (bytes <= _remainingPrefixBytes)
            {
                destination.Add(text);
                _remainingPrefixBytes -= bytes;
                return;
            }
            if (_prefixCapacityMarkerWritten) return;
            _captureBoundExceeded = true;
            const string marker = "[captured prefix exceeded the output artifact bound]";
            var markerBytes = Math.Max(
                    (long)Encoding.UTF8.GetByteCount(marker),
                    (long)marker.Length * sizeof(char)) +
                Encoding.UTF8.GetByteCount(Environment.NewLine) +
                retainedEntryOverheadBytes;
            if (markerBytes <= _remainingPrefixBytes)
            {
                destination.Add(marker);
                _remainingPrefixBytes -= markerBytes;
            }
            _prefixCapacityMarkerWritten = true;
        }

        private void DrainAllRequired()
        {
            DrainOutput(required: true);
            DrainErrors(required: true);
            DrainWarnings(required: true);
        }

        internal PassiveOutputSnapshot CompleteAndSnapshot()
        {
            try { _outputSource?.Complete(); }
            catch { MarkCaptureFailed(); }
            DrainAllRequired();
            return SnapshotCore(deadlineFallback: false);
        }

        /// <summary>Returns only already-frozen data and never waits for a
        /// producer callback. It is safe after the user deadline even when a
        /// hostile or wedged object projection is still being abandoned.</summary>
        internal PassiveOutputSnapshot SnapshotAtDeadline() =>
            SnapshotCore(deadlineFallback: true);

        private PassiveOutputSnapshot SnapshotCore(bool deadlineFallback)
        {
            if (!Monitor.TryEnter(_gate))
            {
                return new(
                    [],
                    new InvocationPrefixSnapshot([], [], [], []),
                    Interlocked.Read(ref _totalOutputCount),
                    CaptureBoundExceeded: false,
                    ActiveMemberOmitted: false,
                    LossyProjection: false,
                    CaptureFailed: true,
                    _detachedTypeNonce);
            }
            try
            {
                return new(
                    [.. _captured],
                    new InvocationPrefixSnapshot(
                        [.. _output],
                        FilterRtkNag(_standardError),
                        [.. _errors],
                        [.. _warnings]),
                    Interlocked.Read(ref _totalOutputCount),
                    _captureBoundExceeded,
                    _activeMemberOmitted,
                    _lossyProjection,
                    Volatile.Read(ref _captureFailed) || deadlineFallback,
                    _detachedTypeNonce);
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }
    }

    private static string? FreezeTerminatingError(
        RuntimeException? terminating,
        out bool safe)
    {
        if (terminating is null)
        {
            safe = true;
            return null;
        }

        safe = BoundedPassiveOutputCapture.TryFreezeErrorRecord(
            terminating.ErrorRecord,
            out var text);
        return text;
    }

    private static WarmAutomaticState? TryCaptureWarmAutomaticState(Runspace runspace)
    {
        try
        {
            return new WarmAutomaticState(
                runspace.SessionStateProxy.GetVariable("LASTEXITCODE"));
        }
        catch
        {
            return null;
        }
    }

    private static bool TryRestoreWarmAutomaticState(
        Runspace runspace,
        WarmAutomaticState? snapshot)
    {
        if (snapshot is null) return false;
        try
        {
            runspace.SessionStateProxy.SetVariable(
                "LASTEXITCODE",
                snapshot.LastExitCode);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private enum InternalOutputPipelineStatus
    {
        Completed,
        Canceled,
        TimedOut,
        Failed,
        Recycled,
        Abandoned,
    }

    private sealed record InternalOutputPipelineResult(
        InternalOutputPipelineStatus Status,
        string Output,
        OutputShapingSummary? Shaping,
        bool TimedOut = false);

    private sealed record PrivateOutputProcessorStart(
        InternalOutputPipelineStatus Status,
        PowerShell? Pipeline,
        Task<PSDataCollection<PSObject>>? Invocation);

    private sealed class PrivateOutputStartupBlockedException(
        InternalOutputPipelineStatus status) : Exception
    {
        internal InternalOutputPipelineStatus Status { get; } = status;
    }

    private sealed class PrivateOutputProcessorLease
    {
        private readonly Action? _stopStartingForTests;
        private readonly TaskCompletionSource<PrivateOutputProcessorStart> _startup =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resultsReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startAttempted;
        private int _stopRequested;
        private int _stopQueued;
        private PowerShell? _pipeline;
        private Task? _stopTask;

        internal PrivateOutputProcessorLease(Action? stopStartingForTests) =>
            _stopStartingForTests = stopStartingForTests;

        internal Task<PrivateOutputProcessorStart> Startup => _startup.Task;
        internal Task ResultsReleased => _resultsReleased.Task;
        internal bool StartAttempted => Volatile.Read(ref _startAttempted) != 0;
        internal Task StopCompleted =>
            Volatile.Read(ref _stopTask) ?? Task.CompletedTask;

        internal void MarkStartAttempted() =>
            Volatile.Write(ref _startAttempted, 1);

        internal void PublishStarted(
            PowerShell pipeline,
            Task<PSDataCollection<PSObject>> invocation)
        {
            Volatile.Write(ref _pipeline, pipeline);
            _startup.TrySetResult(new(
                InternalOutputPipelineStatus.Completed,
                pipeline,
                invocation));
            QueueStopIfRequested();
        }

        internal void PublishNoStart(InternalOutputPipelineStatus status) =>
            _startup.TrySetResult(new(status, null, null));

        internal void RequestStop()
        {
            Volatile.Write(ref _stopRequested, 1);
            QueueStopIfRequested();
        }

        private void QueueStopIfRequested()
        {
            var pipeline = Volatile.Read(ref _pipeline);
            if (Volatile.Read(ref _stopRequested) == 0 ||
                pipeline is null ||
                Interlocked.Exchange(ref _stopQueued, 1) != 0)
            {
                return;
            }

            Task stop;
            using (ExecutionContext.SuppressFlow())
            {
                stop = Task.Run(() =>
                {
                    try { _stopStartingForTests?.Invoke(); }
                    catch { }
                    try { pipeline.Stop(); }
                    catch { }
                });
            }
            Volatile.Write(ref _stopTask, stop);
            _ = stop.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        internal void ReleaseResults() => _resultsReleased.TrySetResult();
    }

    private static InternalOutputPipelineStatus? PrivateOutputStartBlocked(
        DateTimeOffset callDeadline,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return InternalOutputPipelineStatus.Canceled;
        if (DateTimeOffset.UtcNow >= callDeadline)
            return InternalOutputPipelineStatus.TimedOut;
        return null;
    }

    private Task RunPrivateOutputProcessorLifecycleAsync(
        PrivateOutputProcessorLease lease,
        PSDataCollection<PSObject> input,
        DateTimeOffset callDeadline,
        CancellationToken cancellationToken,
        Action<PowerShell> configure)
    {
        Task lifecycle;
        using (ExecutionContext.SuppressFlow())
        {
            lifecycle = Task.Run(async () =>
            {
                Runspace? ownedRunspace = null;
                PowerShell? pipeline = null;
                try
                {
                    if (PrivateOutputStartBlocked(callDeadline, cancellationToken) is { } blocked)
                    {
                        lease.PublishNoStart(blocked);
                        return;
                    }

                    // This is the same process-wide serialized factory used by
                    // warm runspaces. In particular, private shaping cannot
                    // race Runspace.Open on macOS outside CreationLock.
                    ownedRunspace = CreateRunspace(
                        PrivateOutputRunspaceOpeningForTests,
                        () => PrivateOutputStartBlocked(
                            callDeadline,
                            cancellationToken));
                    PrivateOutputRunspaceOpenedForTests?.Invoke();
                    if (PrivateOutputStartBlocked(callDeadline, cancellationToken) is { } afterOpen)
                    {
                        lease.PublishNoStart(afterOpen);
                        return;
                    }

                    pipeline = PowerShell.Create();
                    pipeline.Runspace = ownedRunspace;
                    configure(pipeline);
                    if (PrivateOutputStartBlocked(callDeadline, cancellationToken) is { } afterConfigure)
                    {
                        lease.PublishNoStart(afterConfigure);
                        return;
                    }

                    PrivateOutputInvocationStartingForTests?.Invoke();
                    if (PrivateOutputStartBlocked(callDeadline, cancellationToken) is { } beforeStart)
                    {
                        lease.PublishNoStart(beforeStart);
                        return;
                    }

                    // InvokeAsync can synchronously open/start providers before
                    // returning its Task. Keep that work on this private
                    // lifecycle thread and publish only after the call returns.
                    lease.MarkStartAttempted();
                    var invocation = PrivateOutputInvocationOverrideForTests is { } overrideForTests
                        ? overrideForTests(pipeline, input)
                        : InvokeAsyncWithoutAmbientAudit(pipeline, input);
                    lease.PublishStarted(pipeline, invocation);
                    PrivateOutputInvocationStartedForTests?.Invoke();
                    try { await invocation.ConfigureAwait(false); }
                    catch { }
                    await lease.ResultsReleased.ConfigureAwait(false);
                }
                catch (PrivateOutputStartupBlockedException blocked)
                {
                    lease.PublishNoStart(blocked.Status);
                }
                catch
                {
                    lease.PublishNoStart(
                        lease.StartAttempted
                            ? InternalOutputPipelineStatus.Abandoned
                            : InternalOutputPipelineStatus.Failed);
                }
                finally
                {
                    lease.PublishNoStart(
                        PrivateOutputStartBlocked(callDeadline, cancellationToken) ??
                        (lease.StartAttempted
                            ? InternalOutputPipelineStatus.Abandoned
                            : InternalOutputPipelineStatus.Failed));
                    try { await lease.StopCompleted.ConfigureAwait(false); }
                    catch { }
                    try { PrivateOutputProcessorDisposingForTests?.Invoke(); }
                    catch { }
                    try { pipeline?.Dispose(); }
                    catch { }
                    try { ownedRunspace?.Dispose(); }
                    catch { }
                    try { input.Dispose(); }
                    catch { }
                    Volatile.Write(ref _outputProcessorUnavailable, 0);
                }
            });
        }
        return lifecycle;
    }

    private static readonly TimeSpan PrivateOutputCleanupHandoff =
        TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PrivateOutputFollowupReserve =
        TimeSpan.FromSeconds(2);

    private static async Task AwaitPrivateOutputCleanupHandoffAsync(
        Task cleanup,
        DateTimeOffset callDeadline,
        bool followupProcessorExpected,
        CancellationToken cancellationToken)
    {
        if (cleanup.IsCompleted)
        {
            try { await cleanup.ConfigureAwait(false); }
            catch { }
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var handoffDeadline = followupProcessorExpected
            ? callDeadline - PrivateOutputFollowupReserve
            : now + PrivateOutputCleanupHandoff;
        if (handoffDeadline > callDeadline) handoffDeadline = callDeadline;
        if (await WaitForDeadlineAsync(
                cleanup,
                handoffDeadline,
                cancellationToken).ConfigureAwait(false) == WaitOutcome.Completed)
        {
            try { await cleanup.ConfigureAwait(false); }
            catch { }
        }
    }

    private async Task<InternalOutputPipelineResult>
        RunCapturedOutputPrivatePipelineAsync(
            IReadOnlyCollection<PSObject> captured,
            DateTimeOffset callDeadline,
            CancellationToken cancellationToken,
            Action<PowerShell> configure,
            RtkExecutableIdentity? authorizedShapingRtk,
            bool decodeRoutingEnvelope,
            bool followupProcessorExpected)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new(
                InternalOutputPipelineStatus.Canceled,
                string.Empty,
                Shaping: null);
        }
        if (DateTimeOffset.UtcNow >= callDeadline)
        {
            return new(
                InternalOutputPipelineStatus.TimedOut,
                string.Empty,
                Shaping: null,
                TimedOut: true);
        }

        var input = new PSDataCollection<PSObject>();
        var staged = 0;
        foreach (var value in captured)
        {
            input.Add(value);
            if ((++staged & 0x7f) != 0) continue;
            if (cancellationToken.IsCancellationRequested)
            {
                input.Dispose();
                return new(
                    InternalOutputPipelineStatus.Canceled,
                    string.Empty,
                    Shaping: null);
            }
            if (DateTimeOffset.UtcNow >= callDeadline)
            {
                input.Dispose();
                return new(
                    InternalOutputPipelineStatus.TimedOut,
                    string.Empty,
                    Shaping: null,
                    TimedOut: true);
            }
        }
        input.Complete();

        if (Interlocked.CompareExchange(
                ref _outputProcessorUnavailable,
                1,
                0) != 0)
        {
            input.Dispose();
            return new(
                InternalOutputPipelineStatus.Failed,
                string.Empty,
                Shaping: null);
        }

        var lease = new PrivateOutputProcessorLease(
            PrivateOutputStopStartingForTests);
        var followupEligible = false;
        Task lifecycle;
        try
        {
            lifecycle = RunPrivateOutputProcessorLifecycleAsync(
                lease,
                input,
                callDeadline,
                cancellationToken,
                configure);
        }
        catch
        {
            Volatile.Write(ref _outputProcessorUnavailable, 0);
            input.Dispose();
            return new(
                InternalOutputPipelineStatus.Failed,
                string.Empty,
                Shaping: null);
        }

        try
        {
            var startupOutcome = await WaitForDeadlineAsync(
                lease.Startup,
                callDeadline,
                cancellationToken);
            if (startupOutcome != WaitOutcome.Completed)
            {
                lease.RequestStop();
                var status = lease.StartAttempted
                    ? InternalOutputPipelineStatus.Abandoned
                    : startupOutcome == WaitOutcome.Canceled
                        ? InternalOutputPipelineStatus.Canceled
                        : InternalOutputPipelineStatus.TimedOut;
                return new(
                    status,
                    string.Empty,
                    Shaping: null,
                    TimedOut: startupOutcome == WaitOutcome.TimedOut);
            }

            var started = await lease.Startup.ConfigureAwait(false);
            if (started.Status != InternalOutputPipelineStatus.Completed ||
                started.Pipeline is null ||
                started.Invocation is null)
            {
                return new(
                    started.Status,
                    string.Empty,
                    Shaping: null,
                    TimedOut: started.Status == InternalOutputPipelineStatus.TimedOut);
            }

            var outcome = await WaitForDeadlineAsync(
                started.Invocation,
                callDeadline,
                cancellationToken);
            if (outcome == WaitOutcome.Completed)
            {
                try
                {
                    var results = await started.Invocation.ConfigureAwait(false);
                    if (decodeRoutingEnvelope)
                    {
                        var (text, shaping) = CaptureInvocationOutput(
                            results,
                            authorizedShapingRtk);
                        return new(
                            InternalOutputPipelineStatus.Completed,
                            text,
                            shaping);
                    }
                    followupEligible = true;
                    return new(
                        InternalOutputPipelineStatus.Completed,
                        string.Concat(results.Select(result => result?.ToString())),
                        Shaping: null);
                }
                catch
                {
                    return new(
                        InternalOutputPipelineStatus.Failed,
                        string.Empty,
                        Shaping: null);
                }
            }

            lease.RequestStop();
            if (outcome == WaitOutcome.Canceled &&
                await Task.WhenAny(
                    started.Invocation,
                    Task.Delay(StopGrace)).ConfigureAwait(false) == started.Invocation)
            {
                try { await started.Invocation.ConfigureAwait(false); }
                catch { }
                return new(
                    InternalOutputPipelineStatus.Canceled,
                    string.Empty,
                    Shaping: null);
            }

            return new(
                InternalOutputPipelineStatus.Abandoned,
                string.Empty,
                Shaping: null,
                TimedOut: outcome == WaitOutcome.TimedOut);
        }
        finally
        {
            lease.ReleaseResults();
            await AwaitPrivateOutputCleanupHandoffAsync(
                lifecycle,
                callDeadline,
                followupProcessorExpected && followupEligible,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs formatting/shaping over already-captured objects. The
    /// original script is never present in this pipeline, so no recovery or
    /// shaping branch can execute it a second time.</summary>
    private async Task<InternalOutputPipelineResult> RunCapturedOutputPipelineGateHeldAsync(
        IReadOnlyCollection<PSObject> captured,
        Runspace? runspace,
        DateTimeOffset callDeadline,
        CancellationToken cancellationToken,
        Action<PowerShell> configure,
        RtkExecutableIdentity? authorizedShapingRtk,
        bool decodeRoutingEnvelope,
        bool followupProcessorExpected = false)
    {
        if (runspace is null)
        {
            return await RunCapturedOutputPrivatePipelineAsync(
                captured,
                callDeadline,
                cancellationToken,
                configure,
                authorizedShapingRtk,
                decodeRoutingEnvelope,
                followupProcessorExpected);
        }

        // A private processor that ignored Stop remains process-owned cleanup,
        // but it must neither block audited shutdown nor permit an unbounded
        // succession of abandoned processors. While that one task is live,
        // callers fall back to the already-bounded passive capture.
        if (Volatile.Read(ref _outputProcessorUnavailable) != 0)
        {
            return new(
                InternalOutputPipelineStatus.Failed,
                string.Empty,
                Shaping: null);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return new(
                InternalOutputPipelineStatus.Canceled,
                string.Empty,
                Shaping: null);
        }
        if (DateTimeOffset.UtcNow >= callDeadline)
        {
            return new(
                InternalOutputPipelineStatus.TimedOut,
                string.Empty,
                Shaping: null,
                TimedOut: true);
        }

        var pipeline = PowerShell.Create();
        var handedOff = false;
        try
        {
            pipeline.Runspace = runspace;
            configure(pipeline);
            if (cancellationToken.IsCancellationRequested)
            {
                return new(
                    InternalOutputPipelineStatus.Canceled,
                    string.Empty,
                    Shaping: null);
            }
            if (DateTimeOffset.UtcNow >= callDeadline)
            {
                return new(
                    InternalOutputPipelineStatus.TimedOut,
                    string.Empty,
                    Shaping: null,
                    TimedOut: true);
            }
            var input = new PSDataCollection<PSObject>();
            var staged = 0;
            foreach (var value in captured)
            {
                input.Add(value);
                if ((++staged & 0x7f) != 0) continue;
                if (cancellationToken.IsCancellationRequested)
                {
                    return new(
                        InternalOutputPipelineStatus.Canceled,
                        string.Empty,
                        Shaping: null);
                }
                if (DateTimeOffset.UtcNow >= callDeadline)
                {
                    return new(
                        InternalOutputPipelineStatus.TimedOut,
                        string.Empty,
                        Shaping: null,
                        TimedOut: true);
                }
            }
            input.Complete();
            if (cancellationToken.IsCancellationRequested)
            {
                return new(
                    InternalOutputPipelineStatus.Canceled,
                    string.Empty,
                    Shaping: null);
            }
            if (DateTimeOffset.UtcNow >= callDeadline)
            {
                return new(
                    InternalOutputPipelineStatus.TimedOut,
                    string.Empty,
                    Shaping: null,
                    TimedOut: true);
            }
            var invokeTask = InvokeAsyncWithoutAmbientAudit(pipeline, input);
            var outcome = await WaitForDeadlineAsync(
                invokeTask,
                callDeadline,
                cancellationToken);
            if (outcome == WaitOutcome.Completed)
            {
                try
                {
                    var results = await invokeTask;
                    if (decodeRoutingEnvelope)
                    {
                        var (text, shaping) = CaptureInvocationOutput(
                            results,
                            authorizedShapingRtk);
                        return new(
                            InternalOutputPipelineStatus.Completed,
                            text,
                            shaping);
                    }
                    return new(
                        InternalOutputPipelineStatus.Completed,
                        string.Concat(results.Select(result => result?.ToString())),
                        Shaping: null);
                }
                catch
                {
                    return new(
                        InternalOutputPipelineStatus.Failed,
                        string.Empty,
                        Shaping: null);
                }
            }

            if (outcome == WaitOutcome.Canceled &&
                await TryStopPipelineAsync(pipeline, invokeTask))
            {
                return new(
                    InternalOutputPipelineStatus.Canceled,
                    string.Empty,
                    Shaping: null);
            }

            handedOff = true;
            AbandonAndRecycle(pipeline, invokeTask, runspace);
            return new(
                InternalOutputPipelineStatus.Recycled,
                string.Empty,
                Shaping: null,
                TimedOut: outcome == WaitOutcome.TimedOut);
        }
        catch
        {
            return new(
                InternalOutputPipelineStatus.Failed,
                string.Empty,
                Shaping: null);
        }
        finally
        {
            if (!handedOff) pipeline.Dispose();
        }
    }

    private static string RenderCapturedPrefixWithoutPipeline(
        IEnumerable<PSObject> captured) =>
        string.Join(
            Environment.NewLine,
            captured.Select(value => value?.ToString()));

    private static OutputArtifactContent CaptureIncompleteArtifactContent(
        InvocationPrefixSnapshot prefix,
        OutputProvenance provenance,
        string? terminatingError = null)
    {
        return new OutputArtifactContent(
            string.Join(Environment.NewLine, prefix.Output),
            prefix.StandardError,
            terminatingError is null
                ? prefix.Errors
                : [.. prefix.Errors, terminatingError],
            prefix.Warnings,
            ExitCode: null,
            provenance);
    }

    private static string BoundEmergencyOutput(string text)
    {
        const int maximumCharacters = 40 * 1024;
        if (text.Length <= maximumCharacters) return text;
        const int tailCharacters = maximumCharacters / 4;
        var headCharacters = maximumCharacters - tailCharacters;
        return text[..headCharacters] + Environment.NewLine +
            "[output truncated after internal bookkeeping failure; use the incomplete ptk_output artifact when available]" +
            Environment.NewLine + text[^tailCharacters..];
    }

    private static string? RecoveryElisionHint(OutputRecoverySummary? recovery)
    {
        if (recovery is null) return null;
        return recovery.Handle is { } handle
            ? $"recovery=available: ptk_output handle={handle}"
            : "recovery=unavailable: output capture unavailable; command was not rerun";
    }

    private static OutputArtifactContent CaptureArtifactContent(
        string output,
        InvocationPrefixSnapshot streams,
        int exitCode,
        OutputProvenance provenance,
        string? terminatingError = null)
    {
        var errors = terminatingError is null
            ? streams.Errors
            : [.. streams.Errors, terminatingError];
        return new OutputArtifactContent(
            output,
            streams.StandardError,
            errors,
            streams.Warnings,
            exitCode == 0 ? null : exitCode,
            provenance);
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

    private async Task<InvokeResult> ShapeRtkOutputGateHeldAsync(
        InvokeResult processResult,
        PrimedRunspace primed,
        Runspace runspace,
        DateTimeOffset callDeadline,
        CancellationToken cancellationToken)
    {
        if (processResult.Disposition != InvokeDisposition.Completed ||
            processResult.Output.Length == 0 ||
            primed.CompressCommand is null)
        {
            return processResult;
        }

        var ps = PowerShell.Create();
        var handedOff = false;
        try
        {
            ps.Runspace = runspace;
            ps.AddScript(
                    "$input | & $args[0] -InputProvenance $args[1] -ElisionHint $args[2] -EmitRoutingEnvelope",
                    useLocalScope: true)
              .AddArgument(primed.CompressCommand)
              .AddArgument(OutputProvenance.RtkUnknown.ToMachineCode())
              .AddArgument("recovery=unavailable: rtk capture unsupported");
            var input = new PSDataCollection<object> { processResult.Output };
            input.Complete();

            var invokeTask = InvokeAsyncWithoutAmbientAudit(ps, input);
            var outcome = await WaitForDeadlineAsync(
                invokeTask,
                callDeadline,
                cancellationToken);
            if (outcome == WaitOutcome.Completed)
            {
                var results = await invokeTask;
                var (output, shaping) = CaptureInvocationOutput(
                    results,
                    authorizedShapingRtk: null);
                return processResult with
                {
                    Output = output,
                    OutputShaping = shaping,
                };
            }

            if (outcome == WaitOutcome.Canceled &&
                await TryStopPipelineAsync(ps, invokeTask))
            {
                return processResult with
                {
                    Success = false,
                    Output = string.Empty,
                    Errors = [.. processResult.Errors,
                        "Call canceled after RTK completed; trusted output shaping stopped and no retry was attempted."],
                    TimedOut = false,
                    Disposition = InvokeDisposition.Canceled,
                };
            }

            handedOff = true;
            AbandonAndRecycle(ps, invokeTask, runspace);
            return processResult with
            {
                Success = false,
                Output = string.Empty,
                Errors = [.. processResult.Errors,
                    "RTK completed, but trusted output shaping did not stop within the call budget; the runspace was recycled, no unbounded output was returned, and PTK did not retry the command."],
                TimedOut = outcome == WaitOutcome.TimedOut,
                Disposition = InvokeDisposition.Failed,
                WarmStateLost = true,
            };
        }
        catch
        {
            return processResult with
            {
                Success = false,
                Output = string.Empty,
                Errors = [.. processResult.Errors,
                    "RTK completed, but trusted output shaping failed; no unbounded output was returned and PTK did not retry the command."],
                Disposition = InvokeDisposition.Failed,
            };
        }
        finally
        {
            if (!handedOff) ps.Dispose();
        }
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
        IInvocationAuthorizer? authorizer,
        ForegroundOutputCapture? outputCapture)
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
                    var nativeArgumentPassing =
                        TryCaptureNativeArgumentPassing(runspace);
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
                        allowFileSystemGuidance,
                        workingDirectory,
                        nativeArgumentPassing);
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

                var bashResult = await BashProcessRunner.ExecuteAsync(
                    dispatch,
                    callDeadline,
                    cancellationToken);
                if (outputCapture is not null && bashResult.UserExecutionStarted)
                {
                    bashResult = bashResult with
                    {
                        OutputRecovery = OutputRecoverySummary.Unavailable(
                            "rtk_capture_unsupported",
                            advertise: true),
                    };
                }
                return WithDispatchRouting(bashResult, dispatch);
            }

            if (dispatch.ExecutionPath == ExecutionPath.Rtk)
            {
                var automaticStateBeforeRtkStart =
                    TryCaptureWarmAutomaticState(runspace);
                ResetExitCode(runspace);
                var processResult = await RtkProcessRunner.ExecuteAsync(
                    dispatch,
                    callDeadline,
                    cancellationToken);
                if (processResult.ProvenPreStartFallbackReason is { } fallbackReason)
                {
                    // A machine-proved pre-start failure did not earn a warm
                    // state mutation. Restore the reset before any fallback
                    // authorization/capture gate that can still return
                    // NotStarted.
                    if (!TryRestoreWarmAutomaticState(
                            runspace,
                            automaticStateBeforeRtkStart))
                    {
                        RecycleAbandoning(Task.CompletedTask, runspace);
                        return WithDispatchRouting(new InvokeResult(
                            Success: false,
                            Output: string.Empty,
                            Errors:
                            [
                                "RTK proved that the command did not start, but PTK could not restore automatic session state; the runspace was recycled and the original command was not retried.",
                            ],
                            Warnings: [],
                            TimedOut: false,
                            Disposition: InvokeDisposition.NotStarted,
                            UserExecutionStarted: false,
                            WarmStateLost: true), dispatch);
                    }
                    var fallbackDispatch = ExecutionDispatch.RtkPreStartFallback(
                        plan,
                        fallbackReason);
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
                            return AuthorizationFailureResult(timedOut: false);
                        if (DateTimeOffset.UtcNow >= callDeadline)
                            return AuthorizationFailureResult(timedOut: true);
                    }
                    dispatch = fallbackDispatch;
                }
                else
                {
                    if (processResult.Disposition == InvokeDisposition.Completed)
                        SetExitCode(runspace, processResult.ExitCode ?? 0);
                    var shaped = await ShapeRtkOutputGateHeldAsync(
                        processResult,
                        primed,
                        runspace,
                        callDeadline,
                        cancellationToken);
                    if (outputCapture is not null && shaped.UserExecutionStarted)
                    {
                        shaped = shaped with
                        {
                            OutputRecovery = OutputRecoverySummary.Unavailable(
                                "rtk_capture_unsupported",
                                advertise: true),
                        };
                    }
                    return WithDispatchRouting(shaped, dispatch);
                }
            }

            // The supervisor waits until the exact dispatch is durable and
            // known capturable before reserving output capacity. Reservation
            // failure is diagnostic only: it must never suppress or rerun the
            // already-authorized user operation.
            if (outputCapture is not null)
            {
                await outputCapture.PrepareAsync(
                    OutputSealWait(callDeadline),
                    cancellationToken);
            }
            if (cancellationToken.IsCancellationRequested ||
                DateTimeOffset.UtcNow >= callDeadline)
            {
                var timedOutPreparingCapture = !cancellationToken.IsCancellationRequested;
                return WithDispatchRouting(new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors:
                    [
                        timedOutPreparingCapture
                            ? "The wall-clock budget expired during output-capture preparation. The script was NOT executed and the reservation will be released."
                            : "The call was canceled during output-capture preparation. The script was NOT executed and the reservation will be released.",
                    ],
                    Warnings: [],
                    TimedOut: timedOutPreparingCapture,
                    Disposition: InvokeDisposition.NotStarted,
                    UserExecutionStarted: false), dispatch);
            }

            // Capture preparation is diagnostic and may time out or be
            // canceled. Do not mutate warm automatic state until every
            // remaining no-start gate has passed; the reset still immediately
            // precedes the actual user pipeline.
            ResetExitCode(runspace);
            ps.Runspace = runspace;
            // useLocalScope: false — assignments land in the runspace's session
            // state and survive into the next call; that persistence is the point.
            // This pipeline contains ONLY the user script. Formatting and
            // shaping consume its captured objects below and can never replay it.
            ps.AddScript(dispatch.ExecutionScript!, useLocalScope: false);
            var captured = new PSDataCollection<PSObject>();
            var passiveCapture = new BoundedPassiveOutputCapture(
                outputCapture?.MaximumArtifactBytes ?? 8 * 1024 * 1024);
            passiveCapture.Attach(captured, ps.Streams.Error, ps.Streams.Warning);
            var noInput = new PSDataCollection<object>();
            noInput.Complete();
            var invokeTask = InvokeAsyncWithoutAmbientAudit(ps, noInput, captured);
            var outcome = await WaitForDeadlineAsync(invokeTask, callDeadline, cancellationToken);

            if (outcome == WaitOutcome.Canceled)
            {
                // A cancel (caller aborted, e.g. user Esc) is not a wedge —
                // stop the pipeline and keep the warm runspace; only recycle
                // if the pipeline refuses to stop within the grace period.
                if (await TryStopPipelineAsync(ps, invokeTask))
                {
                    var automaticState = TryCaptureWarmAutomaticState(runspace);
                    var canceledPrefix = passiveCapture.SnapshotAtDeadline().Prefix;
                    var incomplete = outputCapture is null
                        ? null
                        : await outputCapture.SealIncompleteAsync(
                            CaptureIncompleteArtifactContent(
                                canceledPrefix,
                                dispatch.OutputProvenance) with
                            {
                                ExitCode = automaticState?.LastExitCode is int code && code != 0
                                    ? code
                                    : null,
                            },
                            "pipeline_canceled",
                            OutputSealWait(callDeadline));
                    return WithDispatchRouting(new InvokeResult(
                        Success: false,
                        Output: string.Empty,
                        Errors: ["Call canceled by the caller; the pipeline was stopped and warm state was preserved."],
                        Warnings: [],
                        TimedOut: false,
                        Disposition: InvokeDisposition.Canceled,
                        UserExecutionStarted: true)
                    {
                        OutputRecovery = incomplete is null
                            ? null
                            : incomplete with { Advertise = true },
                    }, dispatch);
                }

                handedOff = true;
                AbandonAndRecycle(ps, invokeTask, runspace);
                var wedgedCanceledPrefix = passiveCapture.SnapshotAtDeadline().Prefix;
                var canceledIncomplete = outputCapture is null
                    ? null
                    : await outputCapture.SealIncompleteAsync(
                        CaptureIncompleteArtifactContent(
                            wedgedCanceledPrefix,
                            dispatch.OutputProvenance),
                        "pipeline_canceled_wedged",
                        OutputSealWait(callDeadline));
                return WithDispatchRouting(new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors: [$"Call canceled by the caller, but the pipeline did not stop within {StopGrace.TotalSeconds:0}s; the runspace was recycled and all warm state was lost."],
                    Warnings: [],
                    TimedOut: false,
                    Disposition: InvokeDisposition.OutcomeUnknown,
                    UserExecutionStarted: true,
                    WarmStateLost: true)
                {
                    OutputRecovery = canceledIncomplete is null
                        ? null
                        : canceledIncomplete with { Advertise = true },
                }, dispatch);
            }

            if (outcome == WaitOutcome.TimedOut)
            {
                handedOff = true;
                AbandonAndRecycle(ps, invokeTask, runspace);
                var timedOutPrefix = passiveCapture.SnapshotAtDeadline().Prefix;
                var timedOutIncomplete = outputCapture is null
                    ? null
                    : await outputCapture.SealIncompleteAsync(
                        CaptureIncompleteArtifactContent(
                            timedOutPrefix,
                            dispatch.OutputProvenance),
                        "pipeline_timed_out",
                        OutputSealWait(callDeadline));
                // Teach the recovery paths at the moment of failure — the one
                // place a model reliably reads documentation (design P4).
                var timedOut = ExecutionTimeoutResult(
                    budget,
                    userExecutionStarted: true) with
                {
                    OutputRecovery = timedOutIncomplete is null
                        ? null
                        : timedOutIncomplete with { Advertise = true },
                };
                return WithDispatchRouting(timedOut, dispatch);
            }

            RuntimeException? terminating = null;
            try { _ = await invokeTask; }
            catch (RuntimeException ex) { terminating = ex; }
            var passive = passiveCapture.CompleteAndSnapshot();

            // Snapshot automatic state immediately after the user pipeline.
            // The two private pipelines below must leave these values exactly
            // as the user operation did.
            var userAutomaticState = TryCaptureWarmAutomaticState(runspace);
            var (exitCode, wedged) = await ReadExitCodeBoundedAsync(
                runspace,
                callDeadline,
                cancellationToken);
            var terminatingText = FreezeTerminatingError(
                terminating,
                out var terminatingTextSafe);

            if (wedged)
            {
                var wedgedPrefix = passive.Prefix;
                var prefix = string.Join(Environment.NewLine, wedgedPrefix.Output);
                var incomplete = outputCapture is null
                    ? null
                    : await outputCapture.SealIncompleteAsync(
                        CaptureIncompleteArtifactContent(
                            wedgedPrefix,
                            dispatch.OutputProvenance,
                            terminatingText),
                        "exit_code_bookkeeping_wedged",
                        OutputSealWait(callDeadline));
                var wedgedStderr = wedgedPrefix.StandardError;
                var wedgedErrors = terminatingText is null
                    ? wedgedPrefix.Errors
                    : [.. wedgedPrefix.Errors, terminatingText];
                wedgedErrors = [.. wedgedErrors, BookkeepingWedgeNote];
                return WithDispatchRouting(new InvokeResult(
                    Success: terminating is null,
                    Output: terminating is null
                        ? BoundEmergencyOutput(prefix)
                        : string.Empty,
                    Errors: wedgedErrors,
                    Warnings: wedgedPrefix.Warnings,
                    TimedOut: false,
                    Disposition: terminating is null
                        ? InvokeDisposition.Completed
                        : InvokeDisposition.Failed,
                    UserExecutionStarted: true,
                    ExitCode: exitCode == 0 ? null : exitCode,
                    Stderr: wedgedStderr,
                    WarmStateLost: true)
                {
                    PipelineHadErrors = ps.HadErrors,
                    OutputRecovery = incomplete is null
                        ? null
                        : incomplete with { Advertise = true },
                }, dispatch);
            }

            var rendered = await RunCapturedOutputPipelineGateHeldAsync(
                passive.Output,
                runspace: null,
                callDeadline,
                cancellationToken,
                pipeline => pipeline.AddCommand("Microsoft.PowerShell.Utility\\Out-String"),
                authorizedShapingRtk: null,
                decodeRoutingEnvelope: false,
                followupProcessorExpected: !raw && primed.CompressCommand is not null);
            var unshaped = rendered.Status == InternalOutputPipelineStatus.Completed
                ? rendered.Output
                : RenderCapturedPrefixWithoutPipeline(passive.Output);
            if (passive.LimitationNote is { } limitationNote)
            {
                unshaped = string.IsNullOrEmpty(unshaped)
                    ? limitationNote
                    : unshaped.TrimEnd() + Environment.NewLine + limitationNote;
            }
            var artifact = CaptureArtifactContent(
                unshaped,
                passive.Prefix,
                exitCode,
                dispatch.OutputProvenance,
                terminatingText);
            OutputRecoverySummary? recovery = null;
            if (outputCapture is not null)
            {
                var incompleteReason = passive.IncompleteReason ??
                    (terminatingTextSafe ? null : "active_member_not_evaluated");
                recovery = rendered.Status == InternalOutputPipelineStatus.Completed &&
                           incompleteReason is null
                    ? await outputCapture.SealAsync(
                        artifact,
                        OutputSealWaitBeforeShaping(callDeadline))
                    : await outputCapture.SealIncompleteAsync(
                        artifact,
                        incompleteReason ??
                        $"output_render_{rendered.Status.ToString().ToLowerInvariant()}",
                        OutputSealWaitBeforeShaping(callDeadline));
            }

            if (rendered.Status is InternalOutputPipelineStatus.Canceled or
                InternalOutputPipelineStatus.TimedOut or
                InternalOutputPipelineStatus.Recycled or
                InternalOutputPipelineStatus.Abandoned)
            {
                var recycled = rendered.Status == InternalOutputPipelineStatus.Recycled;
                var processorAbandoned =
                    rendered.Status == InternalOutputPipelineStatus.Abandoned;
                var timedOutRendering = rendered.Status == InternalOutputPipelineStatus.TimedOut;
                var stateLost = recycled;
                if (!recycled &&
                    !TryRestoreWarmAutomaticState(runspace, userAutomaticState))
                {
                    RecycleAbandoning(Task.CompletedTask, runspace);
                    stateLost = true;
                }
                return WithDispatchRouting(new InvokeResult(
                    Success: false,
                    Output: timedOutRendering && terminating is null
                        ? BoundEmergencyOutput(unshaped)
                        : string.Empty,
                    Errors:
                    [
                        .. artifact.Errors,
                        recycled
                            ? "The user script completed, but recovery rendering did not stop within the call budget; the runspace was recycled and PTK did not rerun the script."
                            : processorAbandoned
                                ? stateLost
                                    ? "The user script completed, but the isolated recovery renderer did not stop within the call budget; that processor was discarded, automatic-state restoration failed, the warm runspace was recycled, and PTK did not rerun the script."
                                    : "The user script completed, but the isolated recovery renderer did not stop within the call budget; that processor was discarded, warm state was preserved, and PTK did not rerun the script."
                                : timedOutRendering
                                ? "The user script completed, but the call budget expired before recovery rendering could start; PTK did not rerun the script."
                            : "The user script completed, but the request was canceled during recovery rendering; PTK did not rerun the script.",
                    ],
                    Warnings: artifact.Warnings,
                    TimedOut: rendered.TimedOut,
                    Disposition: recycled || processorAbandoned || timedOutRendering
                        ? InvokeDisposition.Failed
                        : InvokeDisposition.Canceled,
                    UserExecutionStarted: true,
                    ExitCode: artifact.ExitCode,
                    Stderr: artifact.StandardError,
                    WarmStateLost: stateLost)
                {
                    OutputRecovery = recovery is null
                        ? null
                        : recovery with { Advertise = true },
                }, dispatch);
            }

            var output = unshaped;
            OutputShapingSummary? outputShaping = null;
            if (!raw && primed.CompressCommand is not null)
            {
                var authorizedShapingRtk = dispatch.OutputShapingRtkIdentity;
                var hint = RecoveryElisionHint(recovery);
                var shapingWorkingDirectory =
                    TryCaptureCurrentFileSystemLocation(runspace);
                var shaped = await RunCapturedOutputPipelineGateHeldAsync(
                    passive.Output,
                    runspace: null,
                    callDeadline,
                    cancellationToken,
                    pipeline =>
                    {
                        pipeline.AddScript(
                                "$module = Microsoft.PowerShell.Core\\New-Module -Name PwshTokenCompressor -ScriptBlock ([scriptblock]::Create($args[0])) -ErrorAction Stop; " +
                                "Microsoft.PowerShell.Core\\Import-Module -ModuleInfo $module -Force -ErrorAction Stop; " +
                                "if ($args[5]) { Microsoft.PowerShell.Management\\Set-Location -LiteralPath $args[5] -ErrorAction Stop }; " +
                                "if ($args.Count -gt 7) { " +
                                "$input | & 'PwshTokenCompressor\\Compress-PtcOutput' -InputProvenance $args[1] -PinnedRtkPath $args[2] -PinnedRtkDigest $args[3] -PinnedRtkUnixMode $args[4] -DetachedTypeNonce $args[6] -ElisionHint $args[7] -EmitRoutingEnvelope " +
                                "} else { " +
                                "$input | & 'PwshTokenCompressor\\Compress-PtcOutput' -InputProvenance $args[1] -PinnedRtkPath $args[2] -PinnedRtkDigest $args[3] -PinnedRtkUnixMode $args[4] -DetachedTypeNonce $args[6] -EmitRoutingEnvelope }",
                                useLocalScope: true)
                          .AddArgument(_moduleSource!)
                          .AddArgument(dispatch.OutputProvenance.ToMachineCode())
                          .AddArgument(authorizedShapingRtk?.ExecutablePath)
                          .AddArgument(authorizedShapingRtk?.AuditBinaryDigest)
                          .AddArgument(authorizedShapingRtk?.CapturedUnixFileMode)
                          .AddArgument(shapingWorkingDirectory)
                          .AddArgument(passive.DetachedTypeNonce);
                        if (hint is not null) pipeline.AddArgument(hint);
                    },
                    authorizedShapingRtk,
                    decodeRoutingEnvelope: true);

                if (shaped.Status == InternalOutputPipelineStatus.Completed)
                {
                    output = shaped.Output;
                    if (passive.LimitationNote is { } shapedLimitationNote)
                    {
                        output = string.IsNullOrEmpty(output)
                            ? shapedLimitationNote
                            : output.TrimEnd() + Environment.NewLine + shapedLimitationNote;
                    }
                    outputShaping = shaped.Shaping;
                }
                else if (shaped.Status == InternalOutputPipelineStatus.Canceled)
                {
                    var stateLost = false;
                    if (!TryRestoreWarmAutomaticState(runspace, userAutomaticState))
                    {
                        RecycleAbandoning(Task.CompletedTask, runspace);
                        stateLost = true;
                    }
                    return WithDispatchRouting(new InvokeResult(
                        Success: false,
                        Output: string.Empty,
                        Errors: [.. artifact.Errors,
                            "The user script completed, but the request was canceled during output shaping; PTK did not rerun the script."],
                        Warnings: artifact.Warnings,
                        TimedOut: false,
                        Disposition: InvokeDisposition.Canceled,
                        UserExecutionStarted: true,
                        ExitCode: artifact.ExitCode,
                        Stderr: artifact.StandardError,
                        WarmStateLost: stateLost)
                    {
                        OutputRecovery = recovery is null
                            ? null
                            : recovery with { Advertise = true },
                    }, dispatch);
                }
                else if (shaped.Status == InternalOutputPipelineStatus.TimedOut)
                {
                    var stateLost = false;
                    if (!TryRestoreWarmAutomaticState(runspace, userAutomaticState))
                    {
                        RecycleAbandoning(Task.CompletedTask, runspace);
                        stateLost = true;
                    }
                    return WithDispatchRouting(new InvokeResult(
                        Success: false,
                        Output: terminating is null
                            ? BoundEmergencyOutput(unshaped)
                            : string.Empty,
                        Errors: [.. artifact.Errors,
                            "The user script completed, but the call budget expired before output shaping could start; PTK did not rerun the script."],
                        Warnings: artifact.Warnings,
                        TimedOut: true,
                        Disposition: InvokeDisposition.Failed,
                        UserExecutionStarted: true,
                        ExitCode: artifact.ExitCode,
                        Stderr: artifact.StandardError,
                        WarmStateLost: stateLost)
                    {
                        OutputRecovery = recovery is null
                            ? null
                            : recovery with { Advertise = true },
                    }, dispatch);
                }
                else if (shaped.Status == InternalOutputPipelineStatus.Recycled)
                {
                    return WithDispatchRouting(new InvokeResult(
                        Success: false,
                        Output: string.Empty,
                        Errors: [.. artifact.Errors,
                            "The user script completed, but output shaping did not stop within the call budget; the runspace was recycled and PTK did not rerun the script."],
                        Warnings: artifact.Warnings,
                        TimedOut: shaped.TimedOut,
                        Disposition: InvokeDisposition.Failed,
                        UserExecutionStarted: true,
                        ExitCode: artifact.ExitCode,
                        Stderr: artifact.StandardError,
                        WarmStateLost: true)
                    {
                        OutputRecovery = recovery is null
                            ? null
                            : recovery with { Advertise = true },
                    }, dispatch);
                }
                else if (shaped.Status == InternalOutputPipelineStatus.Abandoned)
                {
                    var stateLost = false;
                    if (!TryRestoreWarmAutomaticState(runspace, userAutomaticState))
                    {
                        RecycleAbandoning(Task.CompletedTask, runspace);
                        stateLost = true;
                    }
                    return WithDispatchRouting(new InvokeResult(
                        Success: false,
                        Output: terminating is null
                            ? BoundEmergencyOutput(unshaped)
                            : string.Empty,
                        Errors: [.. artifact.Errors,
                            stateLost
                                ? "The user script completed, but the isolated output processor did not stop within the call budget; that processor was discarded, automatic-state restoration failed, the warm runspace was recycled, and PTK did not rerun the script."
                                : "The user script completed, but the isolated output processor did not stop within the call budget; that processor was discarded, warm state was preserved, and PTK did not rerun the script."],
                        Warnings: artifact.Warnings,
                        TimedOut: shaped.TimedOut,
                        Disposition: InvokeDisposition.Failed,
                        UserExecutionStarted: true,
                        ExitCode: artifact.ExitCode,
                        Stderr: artifact.StandardError,
                        WarmStateLost: stateLost)
                    {
                        OutputRecovery = recovery is null
                            ? null
                            : recovery with { Advertise = true },
                    }, dispatch);
                }
                else
                {
                    output = unshaped;
                    outputShaping = new OutputShapingSummary(
                        OutputShapingStatus.PipelineFailed,
                        authorizedShapingRtk?.AuditBinaryDigest);
                }
            }

            var automaticStateRestored = TryRestoreWarmAutomaticState(
                runspace,
                userAutomaticState);
            var stderr = passive.Prefix.StandardError;
            var errors = terminatingText is null
                ? passive.Prefix.Errors
                : [.. passive.Prefix.Errors, terminatingText];
            var warmStateLost = false;
            if (!automaticStateRestored)
            {
                RecycleAbandoning(Task.CompletedTask, runspace);
                warmStateLost = true;
                errors = [.. errors,
                    "Internal output handling could not restore automatic session state; the runspace was recycled and all warm state was lost."];
            }

            var visibleOutput = terminating is null ? output : string.Empty;
            return WithDispatchRouting(new InvokeResult(
                Success: terminating is null,
                Output: visibleOutput,
                Errors: errors,
                Warnings: passive.Prefix.Warnings,
                TimedOut: false,
                Disposition: terminating is null
                    ? InvokeDisposition.Completed
                    : InvokeDisposition.Failed,
                UserExecutionStarted: true,
                ExitCode: exitCode == 0 ? null : exitCode,
                Stderr: stderr,
                WarmStateLost: warmStateLost)
            {
                OutputShaping = outputShaping,
                OutputRecovery = recovery,
                PipelineHadErrors = ps.HadErrors,
            }, dispatch);
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

    /// <summary>Test seam for deterministic slow-storage containment.</summary>
    internal TimeSpan OutputSealLimitForTests { get; set; } = StopGrace;

    // Artifact persistence is diagnostic and must never withhold a response
    // indefinitely. Before the deadline it consumes only the smaller of the
    // remaining request budget and containment grace; after execution timeout
    // it gets one bounded grace interval to seal the surviving prefix.
    private TimeSpan OutputSealWait(DateTimeOffset callDeadline)
    {
        var sealLimit = OutputSealLimitForTests > TimeSpan.Zero
            ? OutputSealLimitForTests
            : TimeSpan.FromMilliseconds(1);
        var remaining = callDeadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return sealLimit;
        var bounded = remaining < sealLimit ? remaining : sealLimit;
        return bounded < TimeSpan.FromMilliseconds(1)
            ? TimeSpan.FromMilliseconds(1)
            : bounded;
    }

    // Successful execution still needs an isolated shaping pass after the
    // diagnostic artifact write. Reserve enough of the caller's remaining
    // budget that slow storage degrades recovery to unavailable instead of
    // converting a completed user operation into a shaping timeout.
    private TimeSpan OutputSealWaitBeforeShaping(DateTimeOffset callDeadline)
    {
        var sealLimit = OutputSealLimitForTests > TimeSpan.Zero
            ? OutputSealLimitForTests
            : TimeSpan.FromMilliseconds(1);
        var remaining = callDeadline - DateTimeOffset.UtcNow;
        var available = remaining - TimeSpan.FromSeconds(2);
        if (available <= TimeSpan.Zero) return TimeSpan.FromMilliseconds(1);
        var bounded = available < sealLimit ? available : sealLimit;
        return bounded < TimeSpan.FromMilliseconds(1)
            ? TimeSpan.FromMilliseconds(1)
            : bounded;
    }

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

    // A passive recovery renderer owns its disposable private runspace. A
    // wedge there must discard only that processor; user warm state is neither
    // involved nor recycled.
    private void AbandonOwnedPipeline(PowerShell wedged, Task abandonedInvoke)
    {
        Volatile.Write(ref _outputProcessorUnavailable, 1);
        var cleanup = Task.Run(async () =>
        {
            try { wedged.Stop(); } catch { }
            try { await abandonedInvoke; } catch { }
            wedged.Dispose();
        });
        _ = cleanup.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                Volatile.Write(ref _outputProcessorUnavailable, 0);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
