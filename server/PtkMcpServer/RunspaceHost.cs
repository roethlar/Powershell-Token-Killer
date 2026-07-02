using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace PtkMcpServer;

/// <summary>Result of one script invocation in the warm runspace.</summary>
/// <param name="Success">False on a terminating error or timeout; non-terminating
/// errors surface in <paramref name="Errors"/> without failing the call.</param>
public sealed record InvokeResult(
    bool Success,
    string Output,
    string[] Errors,
    string[] Warnings,
    bool TimedOut);

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
    private Runspace _runspace;
    private bool _disposed;

    public RunspaceHost(TimeSpan? callTimeout = null)
    {
        _callTimeout = callTimeout ?? TimeSpan.FromSeconds(300);
        _runspace = CreateRunspace();
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

    public async Task<InvokeResult> InvokeAsync(string script, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        var ps = PowerShell.Create();
        var handedOff = false;
        try
        {
            ps.Runspace = _runspace;
            // useLocalScope: false — assignments land in the runspace's session
            // state and survive into the next call; that persistence is the point.
            ps.AddScript(script, useLocalScope: false)
              .AddCommand("Out-String");

            var invokeTask = ps.InvokeAsync();
            var finished = await Task.WhenAny(invokeTask, Task.Delay(_callTimeout, cancellationToken));

            if (finished != invokeTask)
            {
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
                return new InvokeResult(
                    Success: true,
                    Output: output,
                    Errors: [.. ps.Streams.Error.Select(e => e.ToString())],
                    Warnings: [.. ps.Streams.Warning.Select(w => w.ToString())],
                    TimedOut: false);
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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var old = _runspace;
            _runspace = CreateRunspace();
            _ = Task.Run(() => { try { old.Dispose(); } catch { /* wedged runspace */ } });
        }
        finally
        {
            _gate.Release();
        }
    }

    private void AbandonAndRecycle(PowerShell wedged, Task abandonedInvoke)
    {
        var old = _runspace;
        _runspace = CreateRunspace();
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
