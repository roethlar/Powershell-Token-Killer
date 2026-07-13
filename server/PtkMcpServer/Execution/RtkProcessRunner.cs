using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PtkMcpServer;

/// <summary>Executes an audited RTK plan as a direct child process. No native
/// invocation enters the mutable warm PowerShell runspace, so its preference
/// variables and error list cannot affect capture.</summary>
internal static class RtkProcessRunner
{
    internal const int MaximumCapturedStreamBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan ProcessStopGrace = TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding Utf8 = new(false, false);

    internal static ProcessStartInfo CreateStartInfo(ExecutionDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (dispatch.ExecutionPath != ExecutionPath.Rtk ||
            dispatch.RtkExecutableIdentity is not { } rtk ||
            dispatch.WorkingDirectory is not { } workingDirectory ||
            dispatch.RtkArgumentVector.Length == 0)
        {
            throw new InvalidOperationException("A typed RTK dispatch is required.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = rtk.ExecutablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var argument in dispatch.RtkArgumentVector)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    internal static async Task<InvokeResult> ExecuteAsync(
        ExecutionDispatch dispatch,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (cancellationToken.IsCancellationRequested || DateTimeOffset.UtcNow >= deadline)
            return BudgetFailure(deadline, cancellationToken.IsCancellationRequested);
        if (dispatch.RtkExecutableIdentity is not { } rtk)
            return DependencyFailure(
                "rtk_execution_plan_invalid",
                ExecutionFallbackReason.RtkExecutionPreparationFailed);

        ProcessStartInfo startInfo;
        try { startInfo = CreateStartInfo(dispatch); }
        catch
        {
            return DependencyFailure(
                "rtk_execution_plan_invalid",
                ExecutionFallbackReason.RtkExecutionPreparationFailed);
        }

        // Recheck immediately before Process.Start. Eliminating the remaining
        // same-path replacement race requires an OS-bound executable handle.
        if (!rtk.MatchesCurrentFile())
            return DependencyFailure(
                "rtk_identity_changed",
                ExecutionFallbackReason.RtkExecutableBecameUnavailable);
        if (cancellationToken.IsCancellationRequested || DateTimeOffset.UtcNow >= deadline)
            return BudgetFailure(deadline, cancellationToken.IsCancellationRequested);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) return StartFailure();
        }
        catch (Exception exception) when (IsProvenNoProcessStart(exception))
        {
            return StartFailure();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return StartOutcomeUnknown();
        }

        // A successful Process.Start is the no-retry boundary.
        try { process.StandardInput.Close(); } catch { }
        Task<BoundedTextCapture> stdout;
        Task<BoundedTextCapture> stderr;
        try
        {
            stdout = ReadBoundedTextAsync(process.StandardOutput.BaseStream);
            stderr = ReadBoundedTextAsync(process.StandardError.BaseStream);
        }
        catch
        {
            var stopped = await KillAndDrainAsync(process);
            return CaptureFailure(stopped);
        }

        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            var stopped = await KillAndDrainAsync(process, stdout, stderr);
            return TimedOutAfterStart(stopped);
        }
        using var timeout = new CancellationTokenSource(remaining);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            var stopped = await KillAndDrainAsync(process, stdout, stderr);
            if (cancellationToken.IsCancellationRequested && stopped)
            {
                return new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors: ["Call canceled by the caller; the tracked RTK root process stopped, descendant coverage is unknown, and warm state was preserved."],
                    Warnings: [],
                    TimedOut: false,
                    Disposition: InvokeDisposition.Canceled,
                    UserExecutionStarted: true)
                {
                    AuditDetailCode = "rtk_execution_canceled_root_stopped",
                };
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors: ["Call canceled by the caller, but tracked RTK root termination could not be confirmed. The outcome is unknown and PTK will not retry it."],
                    Warnings: [],
                    TimedOut: false,
                    Disposition: InvokeDisposition.OutcomeUnknown,
                    UserExecutionStarted: true)
                {
                    AuditDetailCode = "rtk_execution_canceled_outcome_unknown",
                };
            }
            return TimedOutAfterStart(stopped);
        }
        catch
        {
            var stopped = await KillAndDrainAsync(process, stdout, stderr);
            return CaptureFailure(stopped);
        }

        var drainOutcome = await WaitForDrainsAsync(
            deadline,
            cancellationToken,
            stdout,
            stderr);
        if (drainOutcome != DrainWaitOutcome.Completed)
        {
            var stopped = await KillAndDrainAsync(process, stdout, stderr);
            if (drainOutcome == DrainWaitOutcome.Canceled)
            {
                return new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors: ["Call canceled after the tracked RTK root exited; descendant or stream completion is unknown and PTK will not retry it."],
                    Warnings: [],
                    TimedOut: false,
                    Disposition: InvokeDisposition.OutcomeUnknown,
                    UserExecutionStarted: true)
                {
                    AuditDetailCode = "rtk_execution_canceled_outcome_unknown",
                };
            }
            return drainOutcome == DrainWaitOutcome.TimedOut
                ? TimedOutAfterStart(stopped)
                : CaptureFailure(stopped);
        }

        BoundedTextCapture output;
        BoundedTextCapture stderrText;
        try
        {
            output = await stdout;
            stderrText = await stderr;
        }
        catch
        {
            var stopped = await KillAndDrainAsync(process, stdout, stderr);
            return CaptureFailure(stopped);
        }

        var warnings = new List<string>(2);
        if (output.Truncated)
        {
            warnings.Add(
                $"RTK stdout exceeded {MaximumCapturedStreamBytes} bytes and was truncated in this response; PTK did not rerun the command.");
        }
        if (stderrText.Truncated)
        {
            warnings.Add(
                $"RTK stderr exceeded {MaximumCapturedStreamBytes} bytes and was truncated in this response; PTK did not rerun the command.");
        }
        var exitCode = process.ExitCode;
        return new InvokeResult(
            Success: true,
            Output: output.Text,
            Errors: [],
            Warnings: [.. warnings],
            TimedOut: false,
            Disposition: InvokeDisposition.Completed,
            UserExecutionStarted: true,
            ExitCode: exitCode == 0 ? null : exitCode,
            Stderr: FilterRtkNag(SplitLines(stderrText.Text)));

        static InvokeResult DependencyFailure(
            string detailCode,
            ExecutionFallbackReason fallbackReason) => new(
            Success: false,
            Output: string.Empty,
            Errors: ["Pinned RTK execution facts became unavailable; the submitted command was NOT started."],
            Warnings: [],
            TimedOut: false,
            Disposition: InvokeDisposition.NotStarted,
            UserExecutionStarted: false)
        {
            AuditDetailCode = detailCode,
            ProvenPreStartFallbackReason = fallbackReason,
        };

        static InvokeResult BudgetFailure(DateTimeOffset deadline, bool canceled) => new(
            Success: false,
            Output: string.Empty,
            Errors: ["RTK execution was not started because the request budget was exhausted or canceled."],
            Warnings: [],
            TimedOut: DateTimeOffset.UtcNow >= deadline,
            Disposition: InvokeDisposition.NotStarted,
            UserExecutionStarted: false)
        {
            AuditDetailCode = canceled
                ? "rtk_execution_canceled_before_start"
                : "rtk_execution_budget_expired",
        };

        static InvokeResult StartFailure() => new(
            Success: false,
            Output: string.Empty,
            Errors: ["Pinned RTK could not start; the submitted command was NOT started."],
            Warnings: [],
            TimedOut: false,
            Disposition: InvokeDisposition.NotStarted,
            UserExecutionStarted: false)
        {
            AuditDetailCode = "rtk_execution_start_failed",
            ProvenPreStartFallbackReason =
                ExecutionFallbackReason.RtkExecutionPreparationFailed,
        };

        static InvokeResult StartOutcomeUnknown() => new(
            Success: false,
            Output: string.Empty,
            Errors: ["RTK process startup raised an indeterminate host failure. A start cannot be ruled out, the outcome is unknown, and PTK will not retry the command."],
            Warnings: [],
            TimedOut: false,
            Disposition: InvokeDisposition.OutcomeUnknown,
            UserExecutionStarted: true)
        {
            AuditDetailCode = "rtk_execution_start_outcome_unknown",
        };

        static InvokeResult CaptureFailure(bool stopped) => new(
            Success: false,
            Output: string.Empty,
            Errors:
            [
                stopped
                    ? "RTK stream capture failed after execution started; the tracked root process stopped, descendant coverage is unknown, and PTK will not retry the command."
                    : "RTK stream capture failed after execution started and tracked-root termination could not be confirmed. The outcome is unknown and PTK will not retry the command."
            ],
            Warnings: [],
            TimedOut: false,
            Disposition: stopped
                ? InvokeDisposition.Failed
                : InvokeDisposition.OutcomeUnknown,
            UserExecutionStarted: true)
        {
            AuditDetailCode = "rtk_execution_capture_failed",
        };

        static InvokeResult TimedOutAfterStart(bool stopped) => new(
            Success: false,
            Output: string.Empty,
            Errors:
            [
                stopped
                    ? "RTK execution exceeded the remaining call budget; the tracked root process stopped, descendant coverage is unknown, and remote effects may already have occurred, so PTK will not retry it."
                    : "RTK execution exceeded the remaining call budget and tracked-root termination could not be confirmed. The outcome is unknown and PTK will not retry it."
            ],
            Warnings: [],
            TimedOut: true,
            Disposition: InvokeDisposition.OutcomeUnknown,
            UserExecutionStarted: true)
        {
            AuditDetailCode = "rtk_execution_timed_out",
        };
    }

    private static string[] FilterRtkNag(IEnumerable<string> stderr) =>
        [.. stderr.Where(line => !line.StartsWith(
            @"[rtk] /!\ No hook installed",
            StringComparison.Ordinal))];

    private sealed record BoundedTextCapture(string Text, bool Truncated);

    private enum DrainWaitOutcome
    {
        Completed,
        Canceled,
        TimedOut,
        Failed,
    }

    private static async Task<DrainWaitOutcome> WaitForDrainsAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken,
        params Task[] drains)
    {
        var all = Task.WhenAll(drains);
        if (all.IsCompleted)
        {
            try
            {
                await all;
                return DrainWaitOutcome.Completed;
            }
            catch { return DrainWaitOutcome.Failed; }
        }

        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return DrainWaitOutcome.TimedOut;
        using var timeout = new CancellationTokenSource(remaining);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            await all.WaitAsync(linked.Token);
            return DrainWaitOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? DrainWaitOutcome.Canceled
                : DrainWaitOutcome.TimedOut;
        }
        catch { return DrainWaitOutcome.Failed; }
    }

    private static async Task<BoundedTextCapture> ReadBoundedTextAsync(Stream stream)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var captured = new MemoryStream(MaximumCapturedStreamBytes);
            var truncated = false;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory());
                if (read == 0) break;
                var available = MaximumCapturedStreamBytes - checked((int)captured.Length);
                var keep = Math.Min(read, Math.Max(0, available));
                if (keep > 0) captured.Write(buffer, 0, keep);
                if (keep != read) truncated = true;
            }

            return new BoundedTextCapture(
                Utf8.GetString(captured.GetBuffer(), 0, checked((int)captured.Length)),
                truncated);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static bool IsProvenNoProcessStart(Exception exception) =>
        exception is Win32Exception or
            FileNotFoundException or
            DirectoryNotFoundException or
            InvalidOperationException or
            ObjectDisposedException or
            PlatformNotSupportedException;

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private static async Task<bool> KillAndDrainAsync(
        Process process,
        params Task[] drains)
    {
        var stopped = false;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            var wait = process.WaitForExitAsync();
            if (await Task.WhenAny(wait, Task.Delay(ProcessStopGrace)) == wait)
            {
                await wait;
                stopped = process.HasExited;
            }
        }
        catch { }
        if (stopped)
        {
            try
            {
                var drain = Task.WhenAll(drains);
                if (await Task.WhenAny(drain, Task.Delay(ProcessStopGrace)) == drain)
                    await drain;
            }
            catch { }
        }
        return stopped;
    }

    private static string[] SplitLines(string text) =>
        text.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries);
}
