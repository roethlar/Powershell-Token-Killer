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
    private readonly string? _modulePath;
    private Runspace _runspace;
    private bool _disposed;

    /// <summary>Timestamp of the most recent invoke/reset; read by the idle watchdog.</summary>
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>True when the PwshTokenCompressor module is imported into the current
    /// warm runspace; false disables output shaping (calls fall back to Out-String).</summary>
    public bool ModuleLoaded { get; private set; }

    public RunspaceHost(TimeSpan? callTimeout = null, string? modulePathOverride = null)
    {
        _callTimeout = callTimeout ?? TimeSpan.FromSeconds(300);
        _modulePath = modulePathOverride ?? ResolveModulePath();
        if (_modulePath is null)
        {
            Console.Error.WriteLine(
                "ptk: PwshTokenCompressor module not found (set PTK_MODULE_PATH); output shaping disabled, calls return plain Out-String text.");
        }
        _runspace = CreatePrimedRunspace();
    }

    // Runspace.Open initializes the FileSystem provider via DriveInfo.GetDrives,
    // whose native getmntinfo call is not thread-safe on macOS — two concurrent
    // opens in one process can AccessViolation. Serialize creation process-wide.
    private static readonly object CreationLock = new();

    private static Runspace CreateRunspace()
    {
        lock (CreationLock)
        {
            var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault());
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
        return runspace;
    }

    // An explicitly set PTK_MODULE_PATH wins outright: if it points at nothing,
    // shaping is disabled rather than silently falling back to a probed copy, so a
    // misconfiguration stays visible (same semantics as the module's PTK_RTK_PATH).
    private static string? ResolveModulePath()
    {
        var env = Environment.GetEnvironmentVariable("PTK_MODULE_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return File.Exists(env) ? env : null;
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
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

    public async Task<InvokeResult> InvokeAsync(string script, bool raw = false, CancellationToken cancellationToken = default, string route = "auto")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
            // LASTEXITCODE bug the CLI path already fixed in Invoke-PtcRun).
            ResetExitCode();
            ps.Runspace = _runspace;
            // useLocalScope: false — assignments land in the runspace's session
            // state and survive into the next call; that persistence is the point.
            ps.AddScript(script, useLocalScope: false)
              .AddCommand(ModuleLoaded && !raw ? "Compress-PtcOutput" : "Out-String");

            var invokeTask = ps.InvokeAsync();
            var delayTask = Task.Delay(_callTimeout, cancellationToken);
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
                return new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors: [$"Call exceeded the {_callTimeout.TotalSeconds:0}s timeout; the runspace was recycled and all warm state was lost."],
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
                    Errors: [.. ps.Streams.Error.Select(e => e.ToString())],
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
                    Errors: [.. errors],
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
