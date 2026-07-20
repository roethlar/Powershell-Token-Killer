using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PtkMcpServer;

internal enum BashSyntaxValidationStatus
{
    Valid,
    SyntaxInvalid,
    TimedOut,
    Canceled,
    StartFailed,
    StartOutcomeUnknown,
    RuntimeFailed,
    IdentityChanged,
    AuditUnavailable,
}

internal sealed record BashSyntaxValidationResult(
    BashSyntaxValidationStatus Status,
    bool ProcessStarted,
    int? ExitCode = null,
    bool? RootTerminationConfirmed = null)
{
    internal string DetailCode => Status switch
    {
        BashSyntaxValidationStatus.Valid => "bash_syntax_valid",
        BashSyntaxValidationStatus.SyntaxInvalid => "bash_syntax_invalid",
        BashSyntaxValidationStatus.TimedOut => "bash_validator_timed_out",
        BashSyntaxValidationStatus.Canceled => "bash_validator_canceled",
        BashSyntaxValidationStatus.StartFailed => "bash_validator_start_failed",
        BashSyntaxValidationStatus.StartOutcomeUnknown => "bash_validator_start_outcome_unknown",
        BashSyntaxValidationStatus.RuntimeFailed => "bash_validator_runtime_failed",
        BashSyntaxValidationStatus.IdentityChanged => "bash_identity_changed",
        BashSyntaxValidationStatus.AuditUnavailable => "bash_validator_audit_unavailable",
        _ => throw new ArgumentOutOfRangeException(),
    };
}

/// <summary>Starts the trusted no-execution Bash syntax validator and the
/// eventual RTK proxy as direct processes with exact argument vectors. Neither
/// path reconstructs submitted script text as shell or PowerShell source.</summary>
internal static class BashProcessRunner
{
    internal static readonly TimeSpan DefaultValidationLimit = TimeSpan.FromSeconds(5);
    internal const int MaximumCapturedStreamBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan ProcessStopGrace = TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding Utf8 = new(false, false);

    internal static ProcessStartInfo CreateValidationStartInfo(
        BashExecutableIdentity bash,
        string script,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(bash);
        ArgumentNullException.ThrowIfNull(script);
        var startInfo = CreateBaseStartInfo(bash.ExecutablePath, workingDirectory);
        startInfo.ArgumentList.Add("--noprofile");
        startInfo.ArgumentList.Add("--norc");
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        return startInfo;
    }

    internal static ProcessStartInfo CreateExecutionStartInfo(ExecutionDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (dispatch.ExecutionPath != ExecutionPath.BashViaRtk ||
            dispatch.RtkExecutableIdentity is not { } rtk ||
            dispatch.BashExecutableIdentity is not { } bash ||
            dispatch.WorkingDirectory is not { } workingDirectory)
        {
            throw new InvalidOperationException("A typed Bash dispatch is required.");
        }

        var startInfo = CreateBaseStartInfo(rtk.ExecutablePath, workingDirectory);
        startInfo.ArgumentList.Add("proxy");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(bash.ExecutablePath);
        startInfo.ArgumentList.Add("--noprofile");
        startInfo.ArgumentList.Add("--norc");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(dispatch.Plan.OriginalScript);
        return startInfo;
    }

    internal static async Task<BashSyntaxValidationResult> ValidateAsync(
        ExecutionDispatch dispatch,
        DateTimeOffset deadline,
        CancellationToken cancellationToken,
        Func<ValueTask<bool>> recordStarted,
        TimeSpan? validationLimit = null,
        Action? afterProcessStartedForTests = null)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(recordStarted);
        if (dispatch.ExecutionPath != ExecutionPath.BashViaRtk ||
            dispatch.BashExecutableIdentity is not { } bash ||
            dispatch.WorkingDirectory is not { } workingDirectory)
        {
            throw new InvalidOperationException("A typed Bash dispatch is required.");
        }

        if (cancellationToken.IsCancellationRequested)
            return new(BashSyntaxValidationStatus.Canceled, ProcessStarted: false);
        if (DateTimeOffset.UtcNow >= deadline)
            return new(BashSyntaxValidationStatus.TimedOut, ProcessStarted: false);
        if (!bash.MatchesCurrentFile())
            return new(BashSyntaxValidationStatus.IdentityChanged, ProcessStarted: false);

        var limit = validationLimit ?? DefaultValidationLimit;
        if (limit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(validationLimit));
        var remaining = deadline - DateTimeOffset.UtcNow;
        var budget = remaining < limit ? remaining : limit;
        if (budget <= TimeSpan.Zero)
            return new(BashSyntaxValidationStatus.TimedOut, ProcessStarted: false);
        var validationDeadline = DateTimeOffset.UtcNow + budget;

        ProcessStartInfo startInfo;
        try
        {
            startInfo = CreateValidationStartInfo(
                bash,
                dispatch.Plan.OriginalScript,
                workingDirectory);
        }
        catch
        {
            return new(BashSyntaxValidationStatus.StartFailed, ProcessStarted: false);
        }
        if (cancellationToken.IsCancellationRequested)
            return new(BashSyntaxValidationStatus.Canceled, ProcessStarted: false);
        if (DateTimeOffset.UtcNow >= validationDeadline ||
            DateTimeOffset.UtcNow >= deadline)
        {
            return new(BashSyntaxValidationStatus.TimedOut, ProcessStarted: false);
        }

        using var process = new Process
        {
            StartInfo = startInfo,
        };
        // Pre-start: force the one-shot exclusive-group acquisition so this
        // root inherits the exclusive group instead of degrading to
        // fallback polling on a first launch (rbc-15 T2-1).
        ProcessTreeContainment.EnsureExclusiveGroup();
        try
        {
            if (!process.Start())
                return new(BashSyntaxValidationStatus.StartFailed, ProcessStarted: false);
        }
        catch (Exception exception) when (IsProvenNoProcessStart(exception))
        {
            return new(BashSyntaxValidationStatus.StartFailed, ProcessStarted: false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return new(
                BashSyntaxValidationStatus.StartOutcomeUnknown,
                ProcessStarted: false,
                RootTerminationConfirmed: false);
        }

        using var containment = ProcessTreeContainment.Track(process);

        // Every successful Process.Start receives one lifecycle fact, even if
        // the process start itself consumed the final validator budget. The
        // dispatch event already authorized this no-execution helper; this
        // callback records what actually happened and is never retried.
        var startedRecord = Task.Run(async () =>
        {
            try { return await recordStarted(); }
            catch { return false; }
        });
        try
        {
            afterProcessStartedForTests?.Invoke();
        }
        catch
        {
            var stopped = await KillAndDrainAsync(process);
            if (!await startedRecord)
            {
                return new(
                    BashSyntaxValidationStatus.AuditUnavailable,
                    ProcessStarted: true,
                    RootTerminationConfirmed: stopped);
            }
            return new(
                BashSyntaxValidationStatus.RuntimeFailed,
                ProcessStarted: true,
                RootTerminationConfirmed: stopped);
        }
        try { process.StandardInput.Close(); } catch { }
        Task stdoutDrain;
        Task stderrDrain;
        try
        {
            stdoutDrain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        }
        catch
        {
            var stopped = await KillAndDrainAsync(process);
            if (!await startedRecord)
            {
                return new(
                    BashSyntaxValidationStatus.AuditUnavailable,
                    ProcessStarted: true,
                    RootTerminationConfirmed: stopped);
            }
            return new(
                BashSyntaxValidationStatus.RuntimeFailed,
                ProcessStarted: true,
                RootTerminationConfirmed: stopped);
        }

        var validationRemaining = validationDeadline - DateTimeOffset.UtcNow;
        if (validationRemaining <= TimeSpan.Zero)
        {
            var stopped = await KillAndDrainAsync(process, stdoutDrain, stderrDrain);
            if (!await startedRecord)
            {
                return new(
                    BashSyntaxValidationStatus.AuditUnavailable,
                    ProcessStarted: true,
                    RootTerminationConfirmed: stopped);
            }
            return new(
                BashSyntaxValidationStatus.TimedOut,
                ProcessStarted: true,
                RootTerminationConfirmed: stopped);
        }
        using var timeout = new CancellationTokenSource(validationRemaining);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        // Arm the kill request before entering the noncancelable audit flush.
        // A wedged flush may hold the call, but it may not let the validator
        // outlive its fixed no-execution budget.
        using var killRegistration = linked.Token.Register(
            static state => TryKillProcessTree((Process)state!),
            process);

        // The noncancelable audit flush runs independently so this method can
        // enforce the process deadline even if durable storage is slow. The
        // call still waits for that one flush before publishing an outcome.
        var startedRecordWait = await WaitForDrainsAsync(
            validationDeadline,
            cancellationToken,
            startedRecord);
        if (startedRecordWait is DrainWaitOutcome.TimedOut or
                DrainWaitOutcome.Canceled)
        {
            var rootStopped = await KillAndDrainAsync(process, stdoutDrain, stderrDrain);
            var eventuallyRecorded = await startedRecord;
            if (!eventuallyRecorded)
            {
                return new(
                    BashSyntaxValidationStatus.AuditUnavailable,
                    ProcessStarted: true,
                    RootTerminationConfirmed: rootStopped);
            }
            return new(
                startedRecordWait == DrainWaitOutcome.Canceled
                    ? BashSyntaxValidationStatus.Canceled
                    : BashSyntaxValidationStatus.TimedOut,
                ProcessStarted: true,
                RootTerminationConfirmed: rootStopped);
        }

        var startedRecorded = startedRecordWait == DrainWaitOutcome.Completed &&
                            await startedRecord;
        if (!startedRecorded)
        {
            var stopped = await KillAndDrainAsync(process, stdoutDrain, stderrDrain);
            return new(
                BashSyntaxValidationStatus.AuditUnavailable,
                ProcessStarted: true,
                RootTerminationConfirmed: stopped);
        }

        if (linked.IsCancellationRequested ||
            DateTimeOffset.UtcNow >= validationDeadline ||
            DateTimeOffset.UtcNow >= deadline)
        {
            var stopped = await KillAndDrainAsync(process, stdoutDrain, stderrDrain);
            return new(
                cancellationToken.IsCancellationRequested
                    ? BashSyntaxValidationStatus.Canceled
                    : BashSyntaxValidationStatus.TimedOut,
                ProcessStarted: true,
                RootTerminationConfirmed: stopped);
        }
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            var stopped = await KillAndDrainAsync(process, stdoutDrain, stderrDrain);
            return new(
                cancellationToken.IsCancellationRequested
                    ? BashSyntaxValidationStatus.Canceled
                    : BashSyntaxValidationStatus.TimedOut,
                ProcessStarted: true,
                RootTerminationConfirmed: stopped);
        }
        catch
        {
            var stopped = await KillAndDrainAsync(process, stdoutDrain, stderrDrain);
            return new(
                BashSyntaxValidationStatus.RuntimeFailed,
                ProcessStarted: true,
                RootTerminationConfirmed: stopped);
        }

        var drainOutcome = await WaitForDrainsAsync(
            validationDeadline,
            cancellationToken,
            stdoutDrain,
            stderrDrain);
        if (drainOutcome != DrainWaitOutcome.Completed)
        {
            var rootStopped = await KillAndDrainAsync(process, stdoutDrain, stderrDrain);
            return new(
                drainOutcome switch
                {
                    DrainWaitOutcome.Canceled => BashSyntaxValidationStatus.Canceled,
                    DrainWaitOutcome.TimedOut => BashSyntaxValidationStatus.TimedOut,
                    _ => BashSyntaxValidationStatus.RuntimeFailed,
                },
                ProcessStarted: true,
                RootTerminationConfirmed: rootStopped);
        }
        return new(
            process.ExitCode == 0
                ? BashSyntaxValidationStatus.Valid
                : BashSyntaxValidationStatus.SyntaxInvalid,
            ProcessStarted: true,
            process.ExitCode,
            RootTerminationConfirmed: true);
    }

    internal static async Task<InvokeResult> ExecuteAsync(
        ExecutionDispatch dispatch,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (cancellationToken.IsCancellationRequested || DateTimeOffset.UtcNow >= deadline)
            return BudgetFailure(deadline, cancellationToken.IsCancellationRequested);
        if (dispatch.RtkExecutableIdentity is not { } rtk ||
            dispatch.BashExecutableIdentity is not { } bash)
        {
            return DependencyFailure("bash_execution_plan_invalid");
        }

        ProcessStartInfo startInfo;
        try { startInfo = CreateExecutionStartInfo(dispatch); }
        catch { return StartFailure(); }

        // Hash after building the child environment, then check the request
        // budget immediately before Process.Start. The remaining same-path
        // replacement race requires an OS-bound handle to eliminate.
        var rtkMatches = rtk.MatchesCurrentFile();
        var bashMatches = bash.MatchesCurrentFile();
        if (!rtkMatches || !bashMatches)
        {
            return DependencyFailure(
                !rtkMatches && !bashMatches
                    ? "bash_rtk_identity_changed"
                    : !rtkMatches
                        ? "rtk_identity_changed"
                        : "bash_identity_changed");
        }
        if (cancellationToken.IsCancellationRequested || DateTimeOffset.UtcNow >= deadline)
            return BudgetFailure(deadline, cancellationToken.IsCancellationRequested);

        using var process = new Process { StartInfo = startInfo };
        // Pre-start: force the one-shot exclusive-group acquisition so this
        // root inherits the exclusive group instead of degrading to
        // fallback polling on a first launch (rbc-15 T2-1).
        ProcessTreeContainment.EnsureExclusiveGroup();
        try
        {
            if (!process.Start())
                return StartFailure();
        }
        catch (Exception exception) when (IsProvenNoProcessStart(exception))
        {
            return StartFailure();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return StartOutcomeUnknown();
        }

        using var containment = ProcessTreeContainment.Track(process);
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
                    Errors: ["Call canceled by the caller; the tracked Bash/RTK root process stopped, descendant coverage is unknown, and warm state was preserved."],
                    Warnings: [],
                    TimedOut: false,
                    Disposition: InvokeDisposition.Canceled,
                    UserExecutionStarted: true)
                {
                    AuditDetailCode = "bash_execution_canceled_root_stopped",
                };
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors: ["Call canceled by the caller, but tracked RTK/Bash root termination could not be confirmed. The outcome is unknown and PTK will not retry it."],
                    Warnings: [],
                    TimedOut: false,
                    Disposition: InvokeDisposition.OutcomeUnknown,
                    UserExecutionStarted: true)
                {
                    AuditDetailCode = "bash_execution_canceled_outcome_unknown",
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
            var rootStopped = await KillAndDrainAsync(process, stdout, stderr);
            if (drainOutcome == DrainWaitOutcome.Canceled)
            {
                return new InvokeResult(
                    Success: false,
                    Output: string.Empty,
                    Errors:
                    [
                        rootStopped
                            ? "Call canceled after the tracked Bash/RTK root exited; descendant or stream completion is unknown and PTK will not retry it."
                            : "Call canceled, but Bash/RTK root termination could not be confirmed. The outcome is unknown and PTK will not retry it."
                    ],
                    Warnings: [],
                    TimedOut: false,
                    Disposition: InvokeDisposition.OutcomeUnknown,
                    UserExecutionStarted: true)
                {
                    AuditDetailCode = "bash_execution_canceled_outcome_unknown",
                };
            }
            if (drainOutcome == DrainWaitOutcome.TimedOut)
                return TimedOutAfterStart(rootStopped);
            return CaptureFailure(rootStopped);
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
        var exitCode = process.ExitCode;
        var warnings = new List<string>(2);
        if (output.Truncated)
        {
            warnings.Add(
                $"Bash/RTK stdout exceeded {MaximumCapturedStreamBytes} bytes and was truncated in this response; PTK did not rerun the command.");
        }
        if (stderrText.Truncated)
        {
            warnings.Add(
                $"Bash/RTK stderr exceeded {MaximumCapturedStreamBytes} bytes and was truncated in this response; PTK did not rerun the command.");
        }
        return new InvokeResult(
            Success: true,
            Output: output.Text,
            Errors: [],
            Warnings: [.. warnings],
            TimedOut: false,
            Disposition: InvokeDisposition.Completed,
            UserExecutionStarted: true,
            ExitCode: exitCode == 0 ? null : exitCode,
            Stderr: SplitLines(stderrText.Text));

        static InvokeResult DependencyFailure(string detailCode) => new(
            Success: false,
            Output: string.Empty,
            Errors: ["Pinned Bash or RTK identity changed before execution; the submitted script was NOT started."],
            Warnings: [],
            TimedOut: false,
            Disposition: InvokeDisposition.NotStarted,
            UserExecutionStarted: false)
        {
            AuditDetailCode = detailCode,
        };

        static InvokeResult BudgetFailure(
            DateTimeOffset deadline,
            bool canceled) => new(
            Success: false,
            Output: string.Empty,
            Errors: ["Bash execution was not started because the request budget was exhausted or canceled."],
            Warnings: [],
            TimedOut: DateTimeOffset.UtcNow >= deadline,
            Disposition: InvokeDisposition.NotStarted,
            UserExecutionStarted: false)
        {
            AuditDetailCode = canceled
                ? "bash_execution_canceled_before_start"
                : "bash_execution_budget_expired",
        };

        static InvokeResult StartFailure() => new(
            Success: false,
            Output: string.Empty,
            Errors: ["Pinned RTK could not start Bash; the submitted script was NOT started."],
            Warnings: [],
            TimedOut: false,
            Disposition: InvokeDisposition.NotStarted,
            UserExecutionStarted: false)
        {
            AuditDetailCode = "bash_execution_start_failed",
        };

        static InvokeResult StartOutcomeUnknown() => new(
            Success: false,
            Output: string.Empty,
            Errors: ["RTK/Bash process startup raised an indeterminate host failure. A start cannot be ruled out, the outcome is unknown, and PTK will not retry the command."],
            Warnings: [],
            TimedOut: false,
            Disposition: InvokeDisposition.OutcomeUnknown,
            UserExecutionStarted: true)
        {
            AuditDetailCode = "bash_execution_start_outcome_unknown",
        };

        static InvokeResult CaptureFailure(bool stopped) => new(
            Success: false,
            Output: string.Empty,
            Errors:
            [
                stopped
                    ? "Bash/RTK stream capture failed after execution started; the tracked root process stopped, descendant coverage is unknown, and PTK will not retry the command."
                    : "Bash/RTK stream capture failed after execution started and tracked-root termination could not be confirmed. The outcome is unknown and PTK will not retry the command."
            ],
            Warnings: [],
            TimedOut: false,
            Disposition: stopped
                ? InvokeDisposition.Failed
                : InvokeDisposition.OutcomeUnknown,
            UserExecutionStarted: true)
        {
            AuditDetailCode = "bash_execution_capture_failed",
        };

        static InvokeResult TimedOutAfterStart(bool stopped) => new(
            Success: false,
            Output: string.Empty,
            Errors:
            [
                stopped
                    ? "Bash execution exceeded the remaining call budget; the tracked RTK/Bash root process stopped, descendant coverage is unknown, and remote effects may already have occurred, so PTK will not retry it."
                    : "Bash execution exceeded the remaining call budget and tracked-root termination could not be confirmed. The outcome is unknown and PTK will not retry it."
            ],
            Warnings: [],
            TimedOut: true,
            Disposition: InvokeDisposition.OutcomeUnknown,
            UserExecutionStarted: true)
        {
            AuditDetailCode = "bash_execution_timed_out",
        };
    }

    private static ProcessStartInfo CreateBaseStartInfo(
        string executablePath,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        ScrubBashStartupEnvironment(startInfo.Environment);
        return startInfo;
    }

    internal static void ScrubBashStartupEnvironment(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var remove = environment.Keys
            .Where(name =>
                name.Equals("BASH_ENV", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ENV", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SHELLOPTS", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("BASHOPTS", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("BASH_COMPAT", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LD_PRELOAD", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LD_AUDIT", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LD_LIBRARY_PATH", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("DYLD_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("BASH_FUNC_", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var name in remove) environment.Remove(name);
    }

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
            catch
            {
                return DrainWaitOutcome.Failed;
            }
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
        catch
        {
            return DrainWaitOutcome.Failed;
        }
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

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(checked((int)ProcessStopGrace.TotalMilliseconds));
        }
        catch { }
    }

    private static bool IsProvenNoProcessStart(Exception exception) =>
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
        // rbc-6: reap descendants the tree-walk cannot see (reparented to
        // PID 1 before the kill). Best-effort; never throws.
        stopped = await ProcessTreeContainment.EscalateAsync(process, stopped);
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
