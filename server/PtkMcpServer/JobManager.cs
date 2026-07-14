using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;

namespace PtkMcpServer;

/// <summary>
/// Startup-frozen identity of the PowerShell executable used by background
/// jobs. An unavailable resolution is retained deliberately: later PATH
/// changes must not turn a failed startup lookup into a different executable.
/// </summary>
internal readonly record struct JobPwshExecutable(string? AbsolutePath)
{
    internal static JobPwshExecutable ResolveFromPath()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var names = OperatingSystem.IsWindows()
            ? new[] { "pwsh.exe", "pwsh" }
            : new[] { "pwsh" };

        foreach (var rawEntry in pathValue.Split(Path.PathSeparator))
        {
            var entry = rawEntry.Trim().Trim('"');
            if (entry.Length == 0) entry = Environment.CurrentDirectory;
            if (OperatingSystem.IsWindows())
                entry = Environment.ExpandEnvironmentVariables(entry);

            foreach (var name in names)
            {
                string candidate;
                try
                {
                    candidate = Path.GetFullPath(Path.Combine(entry, name));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                if (IsEligibleExecutable(candidate)) return new JobPwshExecutable(candidate);
            }
        }

        return new JobPwshExecutable(null);
    }

    private static bool IsEligibleExecutable(string candidate)
    {
        if (!File.Exists(candidate)) return false;
        if (OperatingSystem.IsWindows()) return true;

        try
        {
            var mode = File.GetUnixFileMode(candidate);
            const UnixFileMode executeBits =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (mode & executeBits) != 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    internal string RequireAvailable() =>
        AbsolutePath ?? throw new InvalidOperationException(
            "Background jobs are unavailable because pwsh was not found on the server-start PATH.");
}

public enum JobTerminationReason
{
    None,
    ExplicitKill,
    Reset,
    Shutdown,
}

public enum JobKillDisposition
{
    Requested,
    NotFound,
    AlreadyExited,
    AlreadyRequested,
    Failed,
}

public readonly record struct JobKillResult(
    long JobId,
    JobKillDisposition Disposition,
    JobTerminationReason Reason)
{
    public bool KillRequested => Disposition == JobKillDisposition.Requested;
}

/// <summary>
/// Immutable execution facts retained with a job. The dispatch is the one
/// authorized descriptor; every exposed fact is projected from that same
/// object so route and provenance cannot drift independently.
/// </summary>
internal sealed class JobExecutionMetadata
{
    internal JobExecutionMetadata(ExecutionDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        Dispatch = dispatch;
    }

    internal ExecutionDispatch Dispatch { get; }
    internal ExecutionDomain? Domain => Dispatch.Domain;
    internal ExecutionPath ExecutionPath => Dispatch.ExecutionPath;
    internal ResolutionContext ResolutionContext => Dispatch.ResolutionContext;
    internal RequestedExecutionRoute RequestedRoute => Dispatch.RequestedRoute;
    internal OutputProvenance OutputProvenance => Dispatch.OutputProvenance;
    internal ImmutableArray<ExecutionPath> PermittedFallbacks => Dispatch.PermittedFallbacks;
    internal ExecutionFallbackReason? FallbackReason => Dispatch.FallbackReason;
    internal RtkExecutableIdentity? RtkExecutableIdentity => Dispatch.RtkExecutableIdentity;
}

/// <summary>
/// A classified process-start failure. Only <see cref="ProcessStarted"/> false
/// proves that an authorized pre-start fallback may still be considered.
/// </summary>
internal sealed class JobStartException : InvalidOperationException
{
    internal JobStartException(
        string detailCode,
        string message,
        bool? processStarted,
        ExecutionFallbackReason? provenPreStartFallbackReason = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        DetailCode = detailCode;
        ProcessStarted = processStarted;
        ProvenPreStartFallbackReason = provenPreStartFallbackReason;
    }

    internal string DetailCode { get; }
    internal bool? ProcessStarted { get; }
    internal ExecutionFallbackReason? ProvenPreStartFallbackReason { get; }
}

public sealed record JobSnapshot(
    long Id,
    int Pid,
    bool Running,
    int? ExitCode,
    DateTimeOffset StartedUtc,
    string Script,
    string OutputPath,
    JobTerminationReason TerminationReason = JobTerminationReason.None)
{
    public bool KillRequested => TerminationReason != JobTerminationReason.None;

    internal JobExecutionMetadata Execution { get; init; } = null!;
    internal bool ExecutionRoutingAuthoritative { get; init; }
    internal bool? OutputCaptureComplete { get; init; }
    internal string? OutputFailureCode { get; init; }
    internal bool RootTerminationConfirmed { get; init; } = true;
    internal bool StartOutcomeUnknown { get; init; }
    internal bool ExecutionOutcomeUnknown { get; init; }
    internal string? ExecutionOutcomeFailureCode { get; init; }
}

/// <summary>
/// Side-effect-free background-start descriptor. The public job ID is allocated
/// here so audit can durably name the exact job before <see cref="Process.Start"/>.
/// A prepared ID is never reused, even when the later start fails.
/// </summary>
public sealed record JobStartPlan(
    long Id,
    long Generation,
    string Script,
    string? WorkingDirectory,
    string OutputPath,
    string? EncodedCommand)
{
    internal ExecutionDispatch Dispatch { get; init; } = null!;
    internal JobExecutionMetadata Execution { get; init; } = null!;
    internal bool DispatchBound { get; init; }
    internal string? AuthorizedWorkingDirectory { get; init; }
    internal ExecutionPath ExecutionPath =>
        Dispatch?.ExecutionPath ?? PtkMcpServer.ExecutionPath.PowerShellDirect;
}

/// <summary>
/// Owns cold background jobs as direct PowerShell or RTK child processes and
/// retains one typed route/provenance descriptor through terminal publication.
/// Jobs deliberately do NOT see the warm session's state; a separate process
/// gives robust kill/cleanup semantics without runspace thread-safety hazards.
/// </summary>
public sealed class JobManager : IDisposable
{
    private static readonly TimeSpan PostExitOutputDrainGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AbortedOutputDrainGrace = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<long, JobEntry> _jobs = new();
    private readonly Dictionary<long, JobStartAttempt> _startAttempts = new();
    private readonly string _jobsDir;
    private readonly JobPwshExecutable _pwshExecutable;
    private readonly bool _allowColdBackground;
    private readonly TimeSpan _postExitOutputDrainGrace;
    private readonly TimeSpan _abortedOutputDrainGrace;
    private readonly object _shutdownGate = new();
    private long _nextId;
    private Task? _shutdownTask;
    private bool _stopping;
    private bool _resetting;
    private long _generation;
    internal Func<Task>? ShutdownOverrideForTests { get; set; }
    internal Action<JobStartPlan>? BeforeProcessStartForTests { get; set; }
    internal Func<Process, bool>? ProcessStartOverrideForTests { get; set; }
    internal Action<Process>? AfterProcessStartForTests { get; set; }
    internal Action<Process>? BeforeKillForTests { get; set; }
    internal Action<Process>? BeforeInternalContainmentForTests { get; set; }
    internal Action<JobStartPlan>? BeforeOutputWriteForTests { get; set; }
    internal Func<JobStartPlan, Task>? BeforeOutputReadForTests { get; set; }
    internal Func<JobStartPlan, Task>? BeforeOutputDrainCompletesForTests { get; set; }

    private enum JobStartAttemptState
    {
        InProgress,
        FallbackAvailable,
        Consumed,
    }

    private sealed class JobStartAttempt
    {
        public required ExecutionPlan Plan { get; init; }
        public required bool IsFallbackAttempt { get; set; }
        public ExecutionFallbackReason? ProvenFallbackReason { get; set; }
        public JobStartAttemptState State { get; set; }
    }

    private sealed class JobEntry
    {
        public required Process Process { get; init; }
        public required string Script { get; init; }
        public required string OutputPath { get; init; }
        public required DateTimeOffset StartedUtc { get; init; }
        public required JobStartPlan StartPlan { get; init; }
        public required JobExecutionMetadata Execution { get; init; }
        public Func<JobSnapshot, Task>? OnTerminal { get; init; }
        public TaskCompletionSource<bool> StartRecordPublished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> TerminalCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> InternalContainmentRequiredSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TerminalPublished;
        public int TerminationReason;
        public int ProcessId;
        public int RootExited;
        public int RootTerminationConfirmed;
        public int StartOutcomeUnknown;
        public int ExecutionOutcomeUnknown;
        public int InternalContainmentRequired;
        public int? ExitCode;
        public int OutputCaptureState;
        public int OutputFinalized;
        public string? OutputFailureCode;
        public string? ExecutionOutcomeFailureCode;
        public Task OutputTask { get; set; } = Task.CompletedTask;
        public Stream? StandardOutputStream { get; set; }
        public Stream? StandardErrorStream { get; set; }
        public JobOutputWriter? OutputWriter { get; init; }
    }

    private sealed class JobOutputWriter : IDisposable
    {
        private readonly FileStream _stream;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Action? _beforeWrite;
        private readonly Action<string> _recordFailure;
        private readonly TimeSpan _operationTimeout;
        private int _writesDisabled;
        private int _disposed;

        internal JobOutputWriter(
            string path,
            Action? beforeWrite,
            Action<string> recordFailure,
            TimeSpan operationTimeout)
        {
            _beforeWrite = beforeWrite;
            _recordFailure = recordFailure;
            _operationTimeout = operationTimeout;
            _stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        internal async Task AppendAsync(ReadOnlyMemory<byte> bytes)
        {
            if (Volatile.Read(ref _writesDisabled) != 0) return;
            // A timed-out append may outlive its pump call. Own the bytes so
            // the ArrayPool buffer can be reused without mutating a late write
            // or leaking another stream/job's later data into this log.
            var ownedBytes = bytes.ToArray();
            var append = AppendCoreAsync(ownedBytes);
            if (await Task.WhenAny(append, Task.Delay(_operationTimeout)).ConfigureAwait(false) !=
                append)
            {
                Volatile.Write(ref _writesDisabled, 1);
                _recordFailure("rtk_output_write_unconfirmed");
                ObserveLateFailure(append);
                return;
            }
            await append.ConfigureAwait(false);
        }

        private async Task AppendCoreAsync(ReadOnlyMemory<byte> bytes)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_writesDisabled != 0 || _disposed != 0) return;
                try
                {
                    // Ensure the bounded caller receives the Task before a
                    // synchronous filesystem/hook stall can hold this method.
                    await Task.Yield();
                    _beforeWrite?.Invoke();
                    await _stream.WriteAsync(bytes).ConfigureAwait(false);
                    await _stream.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    Volatile.Write(ref _writesDisabled, 1);
                    _recordFailure("rtk_output_write_failed");
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        internal async Task CompleteAsync(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
            {
                Abort();
                _recordFailure("rtk_output_close_unconfirmed");
                return;
            }
            var completion = CompleteCoreAsync();
            if (await Task.WhenAny(completion, Task.Delay(timeout)).ConfigureAwait(false) !=
                completion)
            {
                Abort();
                _recordFailure("rtk_output_close_unconfirmed");
                ObserveLateFailure(completion);
                return;
            }
            await completion.ConfigureAwait(false);
        }

        private async Task CompleteCoreAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed != 0) return;
                Volatile.Write(ref _writesDisabled, 1);
                try { await _stream.FlushAsync().ConfigureAwait(false); }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    _recordFailure("rtk_output_flush_failed");
                }
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    try { await _stream.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception exception) when (!IsFatal(exception))
                    {
                        _recordFailure("rtk_output_close_failed");
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        internal void Abort()
        {
            Volatile.Write(ref _writesDisabled, 1);
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _stream.Dispose(); } catch { }
        }

        public void Dispose()
        {
            Abort();
        }

        private static void ObserveLateFailure(Task task) =>
            _ = task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    public JobManager(string? jobsDirOverride = null)
        : this(
            JobPwshExecutable.ResolveFromPath(),
            jobsDirOverride,
            allowColdBackground: true)
    {
    }

    internal JobManager(
        JobPwshExecutable pwshExecutable,
        string? jobsDirOverride = null,
        bool allowColdBackground = true,
        TimeSpan? postExitOutputDrainGrace = null,
        TimeSpan? abortedOutputDrainGrace = null)
    {
        if (postExitOutputDrainGrace is { } drainGrace && drainGrace <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(postExitOutputDrainGrace));
        if (abortedOutputDrainGrace is { } abortGrace && abortGrace <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(abortedOutputDrainGrace));
        _pwshExecutable = pwshExecutable;
        _allowColdBackground = allowColdBackground;
        _postExitOutputDrainGrace = postExitOutputDrainGrace ?? PostExitOutputDrainGrace;
        _abortedOutputDrainGrace = abortedOutputDrainGrace ?? AbortedOutputDrainGrace;
        _jobsDir = jobsDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ptk", "jobs");
        SweepOldLogs();
    }

    /// <summary>
    /// Frozen session admission fact. The reserved default session constructs
    /// its manager with the public default (true); later named-session binding
    /// passes its own frozen value through the internal constructor.
    /// </summary>
    internal bool AllowColdBackground => _allowColdBackground;

    // Logs must not accumulate forever in ~/.ptk/jobs; anything a week old
    // belongs to a long-dead session. Best effort — never fail construction.
    private void SweepOldLogs()
    {
        try
        {
            if (!Directory.Exists(_jobsDir)) return;
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var file in Directory.EnumerateFiles(_jobsDir, "job-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    try { File.Delete(file); } catch { /* in use or gone */ }
                }
            }
        }
        catch { /* sweep is a nicety */ }
    }

    /// <summary>
    /// Allocates the monotonically increasing public ID and builds a start
    /// descriptor without creating directories, files, or processes.
    /// </summary>
    public JobStartPlan PrepareStart(string script, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        var compatibilityPlan = new ExecutionPlan(
            script,
            script,
            ExecutionDomain.PowerShell,
            ExecutionPath.PowerShellDirect,
            PreExecutionValidation.None,
            ResolutionContext.Cold,
            RequestedExecutionRoute.PowerShell,
            OutputProvenance.DirectText,
            ImmutableArray<ExecutionPath>.Empty,
            fallbackReason: null,
            rtkExecutableIdentity: null);
        return PrepareStartCore(
            ExecutionDispatch.FromPlan(compatibilityPlan),
            workingDirectory);
    }

    /// <summary>
    /// Retains one already-selected cold dispatch without allocating files or
    /// starting a process. The caller-supplied cwd is authoritative for cold
    /// direct PowerShell until cold direct plans carry it themselves.
    /// </summary>
    internal JobStartPlan PrepareStart(
        ExecutionDispatch dispatch,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        var reservation = PrepareStart(dispatch.Plan.OriginalScript);
        return BindDispatch(reservation, dispatch, workingDirectory);
    }

    /// <summary>
    /// Binds the final cold dispatch to an already allocated/audited public job
    /// ID without allocating a second ID or performing filesystem/process work.
    /// </summary>
    internal JobStartPlan BindDispatch(
        JobStartPlan reservation,
        ExecutionDispatch dispatch,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!string.Equals(
                reservation.Script,
                dispatch.Plan.OriginalScript,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The allocated job intent differs from the authorized dispatch.");
        }
        if (reservation.DispatchBound)
        {
            if (reservation.AuthorizedWorkingDirectory is null ||
                !PathsEqual(reservation.AuthorizedWorkingDirectory, workingDirectory))
            {
                throw new InvalidOperationException(
                    "A bound job cannot be rebound to a different working directory.");
            }
            if (!ReferenceEquals(reservation.Dispatch.Plan, dispatch.Plan))
            {
                throw new InvalidOperationException(
                    "A bound job can only consume a fallback from its originally authorized plan.");
            }
            if (!ReferenceEquals(reservation.Dispatch, dispatch) &&
                !(reservation.Dispatch.ExecutionPath == ExecutionPath.Rtk &&
                  dispatch.IsFallback))
            {
                throw new InvalidOperationException(
                    "A bound job dispatch cannot be replanned or retried after fallback.");
            }
        }
        if (!PathsEqual(reservation.OutputPath, ExpectedOutputPath(reservation.Id)))
            throw new InvalidOperationException("The allocated job output path is invalid.");
        if (dispatch.ResolutionContext != ResolutionContext.Cold)
            throw new InvalidOperationException("Background execution requires a cold dispatch.");
        if (dispatch.ExecutionPath is not (ExecutionPath.PowerShellDirect or ExecutionPath.Rtk))
            throw new InvalidOperationException("The background runner supports cold PowerShell or RTK dispatches only.");
        if (!Path.IsPathFullyQualified(workingDirectory))
            throw new InvalidOperationException("A typed background dispatch requires an absolute filesystem cwd.");
        if (dispatch.ExecutionPath == ExecutionPath.PowerShellDirect &&
            dispatch.ExecutionScript is null)
        {
            throw new InvalidOperationException("A direct PowerShell dispatch requires an execution script.");
        }
        if (dispatch.WorkingDirectory is { } plannedWorkingDirectory &&
            !PathsEqual(plannedWorkingDirectory, workingDirectory))
        {
            throw new InvalidOperationException("The background cwd differs from the authorized dispatch.");
        }

        var encodedCommand = dispatch.ExecutionPath == ExecutionPath.PowerShellDirect
            ? BuildEncodedCommand(dispatch.ExecutionScript!, reservation.OutputPath)
            : null;
        return reservation with
        {
            WorkingDirectory = workingDirectory,
            EncodedCommand = encodedCommand,
            Dispatch = dispatch,
            Execution = new JobExecutionMetadata(dispatch),
            DispatchBound = true,
            AuthorizedWorkingDirectory = workingDirectory,
        };
    }

    private JobStartPlan PrepareStartCore(
        ExecutionDispatch dispatch,
        string? workingDirectory)
    {
        long generation;
        lock (_shutdownGate)
        {
            // Preparing is deliberately effect-free so audit can name intent
            // even while admission is closed. A descriptor prepared during
            // reset/shutdown is permanently stale and can never be committed.
            generation = _stopping || _resetting ? -1 : _generation;
        }
        var id = Interlocked.Increment(ref _nextId);
        var outputPath = ExpectedOutputPath(id);
        var encodedCommand = dispatch.ExecutionPath == ExecutionPath.PowerShellDirect
            ? BuildEncodedCommand(dispatch.ExecutionScript!, outputPath)
            : null;
        var execution = new JobExecutionMetadata(dispatch);

        return new JobStartPlan(
            id,
            generation,
            dispatch.Plan.OriginalScript,
            workingDirectory,
            outputPath,
            encodedCommand)
        {
            Dispatch = dispatch,
            Execution = execution,
            AuthorizedWorkingDirectory = workingDirectory,
        };
    }

    private string ExpectedOutputPath(long id) =>
        Path.Combine(_jobsDir, $"job-{Environment.ProcessId}-{id}.log");

    private static string BuildEncodedCommand(string script, string outputPath)
    {
        // The child redirects its own streams to the log file, so output survives
        // regardless of this server's pipes. The user script is compiled inside
        // the wrapper so parse failures also land in the log (exit 64).
        var logLiteral = "'" + outputPath.Replace("'", "''") + "'";
        var scriptB64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var wrapped =
            $"$ptkJobLog = {logLiteral}\n" +
            $"$ptkJobScript = [System.Text.Encoding]::Unicode.GetString([System.Convert]::FromBase64String('{scriptB64}'))\n" +
            "try { $ptkJobBlock = [scriptblock]::Create($ptkJobScript) }\n" +
            "catch { $_ | Out-File -LiteralPath $ptkJobLog; exit 64 }\n" +
            "& $ptkJobBlock *> $ptkJobLog\n" +
            "if ($global:LASTEXITCODE -is [int]) { exit $global:LASTEXITCODE } else { exit 0 }";
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
    }

    /// <summary>
    /// Consumes a prepared descriptor and starts the process exactly once.
    /// Callers must durably record their pre-effect intent before entering this
    /// method. The entry is installed before Process.Start so any exception
    /// after launch cannot leave executing work untracked.
    /// </summary>
    public JobSnapshot CommitStart(
        JobStartPlan plan,
        Func<JobSnapshot, Task>? onTerminal = null,
        DateTimeOffset? deadline = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!_allowColdBackground)
        {
            throw new InvalidOperationException(
                "Cold background jobs are disabled for this session.");
        }
        ValidateStartPlan(plan);
        BeginStartAttempt(plan);
        try
        {
            var snapshot = CommitAdmittedStart(
                plan,
                onTerminal,
                deadline,
                cancellationToken);
            FinishStartAttempt(plan, provenFallbackReason: null);
            return snapshot;
        }
        catch (JobStartException exception)
        {
            FinishStartAttempt(
                plan,
                exception.ProcessStarted is false
                    ? exception.ProvenPreStartFallbackReason
                    : null);
            throw;
        }
        catch
        {
            FinishStartAttempt(plan, provenFallbackReason: null);
            throw;
        }
    }

    private JobSnapshot CommitAdmittedStart(
        JobStartPlan plan,
        Func<JobSnapshot, Task>? onTerminal,
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        ThrowIfStartBudgetExpired(deadline, cancellationToken);
        ProcessStartInfo startInfo;
        if (plan.ExecutionPath == ExecutionPath.PowerShellDirect)
        {
            if (_pwshExecutable.AbsolutePath is not { } pwshExecutablePath)
            {
                throw new JobStartException(
                    "pwsh_executable_unavailable",
                    "Background jobs are unavailable because pwsh was not found on the server-start PATH.",
                    processStarted: false);
            }
            startInfo = CreatePowerShellStartInfo(plan, pwshExecutablePath);
        }
        else
        {
            ValidateRtkLaunchFacts(plan);
            try
            {
                startInfo = RtkProcessRunner.CreateStartInfo(plan.Dispatch);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                throw RtkPreparationFailed(exception);
            }
        }

        lock (_shutdownGate)
        {
            ThrowIfAdmissionClosedLocked();
        }
        ThrowIfStartBudgetExpired(deadline, cancellationToken);
        Directory.CreateDirectory(_jobsDir);
        ThrowIfStartBudgetExpired(deadline, cancellationToken);

        JobEntry? entry = null;
        JobOutputWriter? outputWriter = null;
        if (plan.ExecutionPath == ExecutionPath.Rtk)
        {
            var outputExisted = File.Exists(plan.OutputPath);
            try
            {
                outputWriter = new JobOutputWriter(
                    plan.OutputPath,
                    () => BeforeOutputWriteForTests?.Invoke(plan),
                    code =>
                    {
                        if (entry is not null) MarkOutputIncomplete(entry, code);
                    },
                    _abortedOutputDrainGrace);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                if (!outputExisted)
                {
                    try { File.Delete(plan.OutputPath); } catch { }
                }
                throw RtkPreparationFailed(exception);
            }
        }

        var process = new Process
        {
            StartInfo = startInfo,
        };
        entry = new JobEntry
        {
            Process = process,
            Script = plan.Script,
            OutputPath = plan.OutputPath,
            StartedUtc = DateTimeOffset.UtcNow,
            StartPlan = plan,
            Execution = plan.Execution,
            OnTerminal = onTerminal,
            OutputWriter = outputWriter,
        };

        lock (_shutdownGate)
        {
            if (_stopping || _resetting || plan.Generation != _generation)
            {
                CleanupUnstarted(plan, entry);
                throw new InvalidOperationException(
                    "The background-job start was invalidated by reset or shutdown.");
            }
            if (!_jobs.TryAdd(plan.Id, entry))
            {
                CleanupUnstarted(plan, entry);
                throw new InvalidOperationException($"job id {plan.Id} is already registered");
            }

            try
            {
                ThrowIfStartBudgetExpired(deadline, cancellationToken);
                BeforeProcessStartForTests?.Invoke(plan);
                ThrowIfStartBudgetExpired(deadline, cancellationToken);
                if (plan.ExecutionPath == ExecutionPath.Rtk)
                    ValidateRtkLaunchFacts(plan);
                ThrowIfStartBudgetExpired(deadline, cancellationToken);
                StartProcessOrThrow(plan, process);
            }
            catch (JobStartException exception) when (exception.ProcessStarted is null)
            {
                if (!RetainUnknownStart(entry)) throw;
                throw new JobStartException(
                    exception.DetailCode,
                    exception.Message,
                    processStarted: true,
                    innerException: exception);
            }
            catch (OperationCanceledException)
            {
                CleanupUnstarted(plan, entry);
                throw;
            }
            catch (JobStartException)
            {
                CleanupUnstarted(plan, entry);
                throw;
            }
            catch (Exception exception) when (
                plan.ExecutionPath == ExecutionPath.Rtk && !IsFatal(exception))
            {
                CleanupUnstarted(plan, entry);
                throw RtkPreparationFailed(exception);
            }
            catch
            {
                CleanupUnstarted(plan, entry);
                throw;
            }

            // Process.Start returning true is the no-retry boundary. Everything
            // below retains the entry and converts capture failures into terminal
            // metadata instead of a fallback-eligible start failure.
            try
            {
                AfterProcessStartForTests?.Invoke(process);
                entry.ProcessId = process.Id;
                try { process.StandardInput.Close(); } catch { }
                if (plan.ExecutionPath == ExecutionPath.Rtk)
                    InitializeRtkOutputCapture(entry);
                _ = ObserveTerminalAsync(plan.Id, entry);
                return SnapshotLocked(plan.Id, entry);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                var processStarted = RetainUnknownStart(entry) ? true : (bool?)null;
                throw new JobStartException(
                    "background_process_start_outcome_unknown",
                    "Background process startup raised an indeterminate host failure; PTK will not retry it.",
                    processStarted,
                    innerException: exception);
            }
        }
    }

    private void BeginStartAttempt(JobStartPlan plan)
    {
        lock (_shutdownGate)
        {
            ThrowIfAdmissionClosedLocked();
            if (plan.Generation != _generation)
            {
                throw new InvalidOperationException(
                    "The background-job start was invalidated by reset or shutdown.");
            }

            if (!_startAttempts.TryGetValue(plan.Id, out var attempt))
            {
                if (plan.Dispatch.IsFallback)
                {
                    throw new InvalidOperationException(
                        "A fallback job start requires a proven no-start from its initial RTK attempt.");
                }
                _startAttempts.Add(
                    plan.Id,
                    new JobStartAttempt
                    {
                        Plan = plan.Dispatch.Plan,
                        IsFallbackAttempt = plan.Dispatch.IsFallback,
                        State = JobStartAttemptState.InProgress,
                    });
                return;
            }

            if (attempt.State == JobStartAttemptState.FallbackAvailable &&
                ReferenceEquals(attempt.Plan, plan.Dispatch.Plan) &&
                plan.Dispatch.IsFallback &&
                plan.Dispatch.FallbackReason == attempt.ProvenFallbackReason)
            {
                attempt.IsFallbackAttempt = true;
                attempt.State = JobStartAttemptState.InProgress;
                return;
            }

            throw new InvalidOperationException(
                $"job start plan {plan.Id} was already consumed");
        }
    }

    private void FinishStartAttempt(
        JobStartPlan plan,
        ExecutionFallbackReason? provenFallbackReason)
    {
        lock (_shutdownGate)
        {
            if (!_startAttempts.TryGetValue(plan.Id, out var attempt) ||
                !ReferenceEquals(attempt.Plan, plan.Dispatch.Plan))
            {
                throw new InvalidOperationException(
                    "The background start-attempt ledger lost its authorized plan.");
            }

            var fallbackAvailable = provenFallbackReason is not null &&
                            !attempt.IsFallbackAttempt &&
                            plan.Dispatch.ExecutionPath == ExecutionPath.Rtk &&
                            plan.Dispatch.PermittedFallbacks.Contains(
                                ExecutionPath.PowerShellDirect);
            attempt.ProvenFallbackReason = fallbackAvailable
                ? provenFallbackReason
                : null;
            attempt.State = fallbackAvailable
                ? JobStartAttemptState.FallbackAvailable
                : JobStartAttemptState.Consumed;
        }
    }

    /// <summary>Consumes an unused proved-no-start fallback capability. This
    /// is idempotent so cancellation/audit-failure cleanup can safely run from
    /// more than one enclosing path without reopening the attempt.</summary>
    internal bool AbandonFallback(JobStartPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_shutdownGate)
        {
            if (!_startAttempts.TryGetValue(plan.Id, out var attempt))
                return false;
            if (!ReferenceEquals(attempt.Plan, plan.Dispatch.Plan))
            {
                throw new InvalidOperationException(
                    "The abandoned fallback does not belong to the authorized plan.");
            }
            if (attempt.State == JobStartAttemptState.Consumed)
                return false;
            if (attempt.State != JobStartAttemptState.FallbackAvailable)
            {
                throw new InvalidOperationException(
                    "A background fallback can only be abandoned after a proved no-start.");
            }

            attempt.ProvenFallbackReason = null;
            attempt.State = JobStartAttemptState.Consumed;
            return true;
        }
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(
        JobStartPlan plan,
        string pwshExecutablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = pwshExecutablePath,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        if (plan.WorkingDirectory is not null)
            startInfo.WorkingDirectory = plan.WorkingDirectory;
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(plan.EncodedCommand!);
        return startInfo;
    }

    private void ValidateStartPlan(JobStartPlan plan)
    {
        if (plan.Dispatch is null || plan.Execution is null ||
            !ReferenceEquals(plan.Execution.Dispatch, plan.Dispatch))
        {
            throw new InvalidOperationException("The background descriptor has no immutable execution dispatch.");
        }
        var dispatch = plan.Dispatch;
        if (dispatch.ResolutionContext != ResolutionContext.Cold)
            throw new InvalidOperationException("Background execution requires a cold dispatch.");
        if (dispatch.ExecutionPath is not (ExecutionPath.PowerShellDirect or ExecutionPath.Rtk))
            throw new InvalidOperationException("The background runner supports cold PowerShell or RTK dispatches only.");
        if (!string.Equals(plan.Script, dispatch.Plan.OriginalScript, StringComparison.Ordinal))
            throw new InvalidOperationException("The background script differs from the authorized dispatch.");
        if (!PathsEqual(plan.OutputPath, ExpectedOutputPath(plan.Id)))
            throw new InvalidOperationException("The background output path differs from its allocated job ID.");

        if (dispatch.ExecutionPath == ExecutionPath.PowerShellDirect)
        {
            if (dispatch.ExecutionScript is null ||
                !string.Equals(
                    plan.EncodedCommand,
                    BuildEncodedCommand(dispatch.ExecutionScript, plan.OutputPath),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The PowerShell wrapper differs from the authorized dispatch.");
            }
            if (dispatch.WorkingDirectory is { } plannedWorkingDirectory &&
                (plan.WorkingDirectory is null ||
                 !PathsEqual(plannedWorkingDirectory, plan.WorkingDirectory)))
            {
                throw new InvalidOperationException("The background cwd differs from the authorized dispatch.");
            }
            if (plan.DispatchBound &&
                (plan.WorkingDirectory is null ||
                 plan.AuthorizedWorkingDirectory is null ||
                 !PathsEqual(plan.AuthorizedWorkingDirectory, plan.WorkingDirectory)))
            {
                throw new InvalidOperationException("The background cwd differs from the bound job dispatch.");
            }
            return;
        }

        if (plan.EncodedCommand is not null ||
            plan.WorkingDirectory is null ||
            !Path.IsPathFullyQualified(plan.WorkingDirectory) ||
            dispatch.WorkingDirectory is null ||
            !PathsEqual(plan.WorkingDirectory, dispatch.WorkingDirectory))
        {
            throw new InvalidOperationException("The RTK descriptor differs from its authorized typed execution facts.");
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static JobStartException RtkIdentityChanged() => new(
        "rtk_identity_changed",
        "Pinned RTK execution facts changed before the background process started.",
        processStarted: false,
        ExecutionFallbackReason.RtkExecutableBecameUnavailable);

    private static JobStartException RtkTargetResolutionChanged() => new(
        "rtk_target_resolution_changed",
        "The cold command target changed before the background process started.",
        processStarted: false,
        ExecutionFallbackReason.RtkTargetResolutionChanged);

    private static JobStartException RtkPreparationFailed(Exception exception) => new(
        "rtk_execution_preparation_failed",
        "RTK background execution preparation failed before any process started.",
        processStarted: false,
        ExecutionFallbackReason.RtkExecutionPreparationFailed,
        exception);

    private static void ValidateRtkLaunchFacts(JobStartPlan plan)
    {
        if (!plan.Dispatch.RtkExecutableIdentity!.MatchesCurrentFile())
            throw RtkIdentityChanged();
        if (plan.Dispatch.ColdCommandTargetIdentity is not { } target ||
            !target.MatchesCurrentResolution())
        {
            throw RtkTargetResolutionChanged();
        }
    }

    private static void ThrowIfStartBudgetExpired(
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline is not null && DateTimeOffset.UtcNow >= deadline.Value)
        {
            throw new JobStartException(
                "prestart_deadline_expired",
                "The wall-clock budget expired before the background process started.",
                processStarted: false);
        }
    }

    private void StartProcessOrThrow(JobStartPlan plan, Process process)
    {
        try
        {
            var started = ProcessStartOverrideForTests?.Invoke(process) ?? process.Start();
            if (started) return;
            throw new JobStartException(
                plan.ExecutionPath == ExecutionPath.Rtk
                    ? "rtk_process_start_failed"
                    : "pwsh_process_start_failed",
                "The background process did not start.",
                processStarted: false,
                plan.ExecutionPath == ExecutionPath.Rtk
                    ? ExecutionFallbackReason.RtkExecutionPreparationFailed
                    : null);
        }
        catch (JobStartException)
        {
            throw;
        }
        catch (Exception exception) when (RtkProcessRunner.IsProvenNoProcessStart(exception))
        {
            throw new JobStartException(
                plan.ExecutionPath == ExecutionPath.Rtk
                    ? "rtk_process_start_failed"
                    : "pwsh_process_start_failed",
                "The background process could not be started.",
                processStarted: false,
                plan.ExecutionPath == ExecutionPath.Rtk
                    ? ExecutionFallbackReason.RtkExecutionPreparationFailed
                    : null,
                exception);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new JobStartException(
                "background_process_start_outcome_unknown",
                "Background process startup raised an indeterminate host failure; PTK will not retry it.",
                processStarted: null,
                innerException: exception);
        }
    }

    private void CleanupUnstarted(JobStartPlan plan, JobEntry entry)
    {
        entry.StartRecordPublished.TrySetResult(false);
        entry.TerminalCompleted.TrySetResult(true);
        ((ICollection<KeyValuePair<long, JobEntry>>)_jobs).Remove(
            new KeyValuePair<long, JobEntry>(plan.Id, entry));
        entry.OutputWriter?.Dispose();
        entry.Process.Dispose();
        if (plan.ExecutionPath == ExecutionPath.Rtk)
        {
            try { File.Delete(plan.OutputPath); } catch { }
        }
    }

    private bool RetainUnknownStart(JobEntry entry)
    {
        MarkOutputIncomplete(entry, "background_process_start_outcome_unknown");
        MarkExecutionOutcomeUnknown(entry, "process_start_outcome_unknown");

        try
        {
            // If Process.Start associated a child before raising, retain and
            // contain that exact process. It is never eligible for fallback.
            entry.ProcessId = entry.Process.Id;
            try { entry.Process.StandardInput.Close(); } catch { }
            if (entry.Execution.ExecutionPath == ExecutionPath.Rtk)
                InitializeRtkOutputCapture(entry);
            var containmentRequired = !RootAlreadyExited(entry.Process);
            if (containmentRequired)
                RequireInternalContainment(entry);
            _ = ObserveTerminalAsync(entry.StartPlan.Id, entry);
            if (containmentRequired)
                _ = TryRequestContainmentBoundedAsync(entry.Process);
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // No associated Process object is available. The public record is
            // retained as an outcome-unknown tombstone instead of claiming a
            // proven no-start or deleting its evidence spool.
        }

        Volatile.Write(ref entry.StartOutcomeUnknown, 1);
        try
        {
            entry.OutputWriter?.CompleteAsync(_abortedOutputDrainGrace)
                .GetAwaiter().GetResult();
        }
        catch { }
        Volatile.Write(ref entry.RootExited, 1);
        Volatile.Write(ref entry.OutputFinalized, 1);
        Interlocked.Exchange(ref entry.TerminalPublished, 1);
        entry.Process.Dispose();
        entry.TerminalCompleted.TrySetResult(true);
        return false;
    }

    /// <summary>
    /// Releases a completed job's terminal callback only after the durable
    /// job.started record exists. This closes the fast-exit race where a child
    /// can terminate before the starting MCP call records its start.
    /// </summary>
    public bool ConfirmStartRecorded(long id)
    {
        if (!_jobs.TryGetValue(id, out var entry)) return false;
        return entry.StartRecordPublished.TrySetResult(true);
    }

    private void InitializeRtkOutputCapture(JobEntry entry)
    {
        try
        {
            entry.StandardOutputStream = entry.Process.StandardOutput.BaseStream;
            entry.StandardErrorStream = entry.Process.StandardError.BaseStream;
            entry.OutputTask = CaptureRtkOutputAsync(entry);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            MarkOutputIncomplete(entry, "rtk_output_pump_setup_failed");
            entry.OutputTask = Task.CompletedTask;
            MarkExecutionOutcomeUnknownIfContainmentRequired(
                entry,
                "rtk_output_pump_setup_failed");
        }
    }

    private async Task CaptureRtkOutputAsync(JobEntry entry)
    {
        try
        {
            await Task.WhenAll(
                PumpRtkOutputAsync(entry.StandardOutputStream!, entry),
                PumpRtkOutputAsync(entry.StandardErrorStream!, entry))
                .ConfigureAwait(false);
            if (BeforeOutputDrainCompletesForTests is { } beforeComplete)
                await beforeComplete(entry.StartPlan).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            MarkOutputIncomplete(entry, "rtk_output_drain_failed");
        }
    }

    private async Task PumpRtkOutputAsync(Stream source, JobEntry entry)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                if (BeforeOutputReadForTests is { } beforeRead)
                    await beforeRead(entry.StartPlan).ConfigureAwait(false);
                var read = await source.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0) return;
                await entry.OutputWriter!.AppendAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            MarkOutputIncomplete(entry, "rtk_output_read_failed");
            MarkExecutionOutcomeUnknownIfContainmentRequired(
                entry,
                "rtk_output_read_failed");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ObserveTerminalAsync(long id, JobEntry entry)
    {
        if (Interlocked.Exchange(ref entry.TerminalPublished, 1) != 0) return;
        try
        {
            var rootTerminationConfirmed =
                await ObserveRootExitAsync(entry).ConfigureAwait(false);
            if (rootTerminationConfirmed)
                Volatile.Write(ref entry.RootTerminationConfirmed, 1);
            Volatile.Write(ref entry.RootExited, 1);

            await FinalizeOutputAsync(entry).ConfigureAwait(false);
            if (Volatile.Read(ref entry.StartOutcomeUnknown) != 0) return;
            if (!await entry.StartRecordPublished.Task.ConfigureAwait(false)) return;
            var snapshot = Snapshot(id);
            if (snapshot is not null && entry.OnTerminal is not null)
            {
                await entry.OnTerminal(snapshot).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ptk: job {id} terminal callback failed ({ex.Message})");
        }
        finally
        {
            entry.Process.Dispose();
            entry.TerminalCompleted.TrySetResult(true);
        }
    }

    private enum ContainmentRequestDisposition
    {
        AlreadyExited,
        Requested,
        Indeterminate,
    }

    private async Task<bool> ObserveRootExitAsync(JobEntry entry)
    {
        try
        {
            var wait = entry.Process.WaitForExitAsync();
            if (!wait.IsCompleted &&
                await Task.WhenAny(
                    wait,
                    entry.InternalContainmentRequiredSignal.Task).ConfigureAwait(false) != wait &&
                await Task.WhenAny(
                    wait,
                    Task.Delay(_abortedOutputDrainGrace)).ConfigureAwait(false) != wait)
            {
                return await TryContainAndConfirmExitAsync(entry).ConfigureAwait(false);
            }

            await wait.ConfigureAwait(false);
            CacheExitCode(entry);
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return await TryContainAndConfirmExitAsync(entry).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryContainAndConfirmExitAsync(JobEntry entry)
    {
        var request = await TryRequestContainmentBoundedAsync(entry.Process)
            .ConfigureAwait(false);
        if (request == ContainmentRequestDisposition.AlreadyExited)
        {
            CacheExitCode(entry);
            return true;
        }
        MarkOutputIncomplete(entry, "job_process_observation_failed");
        MarkExecutionOutcomeUnknown(entry, "job_process_observation_failed");

        var deadline = DateTimeOffset.UtcNow + _abortedOutputDrainGrace;
        do
        {
            try
            {
                if (entry.Process.HasExited)
                {
                    CacheExitCode(entry);
                    return true;
                }
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(
                remaining < TimeSpan.FromMilliseconds(25)
                    ? remaining
                    : TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
        }
        while (true);

        return false;
    }

    private async Task FinalizeOutputAsync(JobEntry entry)
    {
        if (entry.Execution.ExecutionPath == ExecutionPath.Rtk)
        {
            if (!entry.OutputTask.IsCompleted &&
                await Task.WhenAny(
                    entry.OutputTask,
                    Task.Delay(_postExitOutputDrainGrace)).ConfigureAwait(false) != entry.OutputTask)
            {
                MarkOutputIncomplete(entry, "rtk_output_eof_unconfirmed");
                try { entry.StandardOutputStream?.Dispose(); } catch { }
                try { entry.StandardErrorStream?.Dispose(); } catch { }
                await Task.WhenAny(
                    entry.OutputTask,
                    Task.Delay(_abortedOutputDrainGrace)).ConfigureAwait(false);
            }
            try
            {
                if (entry.OutputTask.IsCompleted)
                    await entry.OutputTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                MarkOutputIncomplete(entry, "rtk_output_drain_failed");
            }
            if (entry.OutputWriter is not null)
            {
                await entry.OutputWriter.CompleteAsync(_abortedOutputDrainGrace)
                    .ConfigureAwait(false);
            }
        }

        if (entry.Execution.ExecutionPath == ExecutionPath.Rtk &&
            Interlocked.CompareExchange(ref entry.OutputCaptureState, 1, 0) == 0)
        {
            entry.OutputFailureCode = null;
        }
        Volatile.Write(ref entry.OutputFinalized, 1);
    }

    private static void MarkOutputIncomplete(JobEntry entry, string detailCode)
    {
        if (Volatile.Read(ref entry.OutputCaptureState) != 0) return;
        Interlocked.CompareExchange(ref entry.OutputFailureCode, detailCode, null);
        Interlocked.CompareExchange(ref entry.OutputCaptureState, 2, 0);
    }

    private static void MarkExecutionOutcomeUnknown(
        JobEntry entry,
        string detailCode)
    {
        Interlocked.CompareExchange(
            ref entry.ExecutionOutcomeFailureCode,
            detailCode,
            null);
        Volatile.Write(ref entry.ExecutionOutcomeUnknown, 1);
    }

    private void MarkExecutionOutcomeUnknownIfContainmentRequired(
        JobEntry entry,
        string detailCode)
    {
        if (RootAlreadyExited(entry.Process)) return;
        MarkExecutionOutcomeUnknown(entry, detailCode);
        RequireInternalContainment(entry);
        _ = TryRequestContainmentBoundedAsync(entry.Process);
    }

    private static void RequireInternalContainment(JobEntry entry)
    {
        Volatile.Write(ref entry.InternalContainmentRequired, 1);
        entry.InternalContainmentRequiredSignal.TrySetResult(true);
    }

    private ContainmentRequestDisposition TryRequestContainment(Process process)
    {
        try
        {
            if (process.HasExited)
                return ContainmentRequestDisposition.AlreadyExited;
            BeforeInternalContainmentForTests?.Invoke(process);
            process.Kill(entireProcessTree: true);
            return ContainmentRequestDisposition.Requested;
        }
        catch
        {
            try
            {
                if (process.HasExited)
                    return ContainmentRequestDisposition.AlreadyExited;
            }
            catch { }
            return ContainmentRequestDisposition.Indeterminate;
        }
    }

    private async Task<ContainmentRequestDisposition> TryRequestContainmentBoundedAsync(
        Process process)
    {
        var request = Task.Run(() => TryRequestContainment(process));
        if (await Task.WhenAny(
                request,
                Task.Delay(_abortedOutputDrainGrace)).ConfigureAwait(false) != request)
        {
            return ContainmentRequestDisposition.Indeterminate;
        }
        return await request.ConfigureAwait(false);
    }

    private static bool RootAlreadyExited(Process process)
    {
        try { return process.HasExited; }
        catch { return false; }
    }

    private static void CacheExitCode(JobEntry entry)
    {
        try { entry.ExitCode = entry.Process.ExitCode; }
        catch (Exception exception) when (!IsFatal(exception)) { }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    /// <summary>Compatibility entry point for direct unit callers.</summary>
    public JobSnapshot Start(string script, string? workingDirectory = null)
    {
        var plan = PrepareStart(script, workingDirectory);
        var snapshot = CommitStart(plan);
        ConfirmStartRecorded(plan.Id);
        return snapshot;
    }

    public JobSnapshot? Snapshot(long id)
    {
        lock (_shutdownGate)
        {
            if (!_jobs.TryGetValue(id, out var entry)) return null;
            return SnapshotLocked(id, entry);
        }
    }

    private static JobSnapshot SnapshotLocked(long id, JobEntry entry)
    {
        var running = Volatile.Read(ref entry.RootExited) == 0 ||
                      Volatile.Read(ref entry.OutputFinalized) == 0;
        return new JobSnapshot(
            id,
            entry.ProcessId,
            running,
            running ? null : entry.ExitCode,
            entry.StartedUtc,
            entry.Script,
            entry.OutputPath,
            (JobTerminationReason)Volatile.Read(ref entry.TerminationReason))
        {
            Execution = entry.Execution,
            ExecutionRoutingAuthoritative = entry.StartPlan.DispatchBound,
            OutputCaptureComplete = Volatile.Read(ref entry.OutputCaptureState) switch
            {
                0 => null,
                1 => true,
                _ => false,
            },
            OutputFailureCode = entry.OutputFailureCode,
            RootTerminationConfirmed =
                Volatile.Read(ref entry.RootTerminationConfirmed) != 0,
            StartOutcomeUnknown =
                Volatile.Read(ref entry.StartOutcomeUnknown) != 0,
            ExecutionOutcomeUnknown =
                Volatile.Read(ref entry.ExecutionOutcomeUnknown) != 0,
            ExecutionOutcomeFailureCode = entry.ExecutionOutcomeFailureCode,
        };
    }

    public JobSnapshot[] List()
    {
        lock (_shutdownGate)
            return [.. _jobs.Keys.OrderBy(id => id).Select(id => Snapshot(id)!).Where(job => job is not null)];
    }

    /// <summary>
    /// Requests termination and retains the first successful reason before the
    /// process can publish its asynchronous terminal event.
    /// </summary>
    public JobKillResult RequestKill(
        long id,
        JobTerminationReason reason = JobTerminationReason.ExplicitKill)
    {
        if (reason == JobTerminationReason.None || !Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));

        lock (_shutdownGate)
        {
            if (!_jobs.TryGetValue(id, out var entry))
                return new JobKillResult(id, JobKillDisposition.NotFound, reason);
            if (Volatile.Read(ref entry.RootExited) != 0)
            {
                return new JobKillResult(
                    id,
                    Volatile.Read(ref entry.RootTerminationConfirmed) != 0
                        ? JobKillDisposition.AlreadyExited
                        : JobKillDisposition.Failed,
                    reason);
            }

            var existingReason = (JobTerminationReason)Volatile.Read(ref entry.TerminationReason);
            if (existingReason != JobTerminationReason.None)
                return new JobKillResult(id, JobKillDisposition.AlreadyRequested, existingReason);

            Interlocked.Exchange(ref entry.TerminationReason, (int)reason);
            try
            {
                BeforeKillForTests?.Invoke(entry.Process);
                entry.Process.Kill(entireProcessTree: true);
                return new JobKillResult(id, JobKillDisposition.Requested, reason);
            }
            catch
            {
                Interlocked.Exchange(ref entry.TerminationReason, (int)JobTerminationReason.None);
                return new JobKillResult(id, JobKillDisposition.Failed, reason);
            }
        }
    }

    /// <summary>Compatibility boolean for callers that only need effect admission.</summary>
    public bool Kill(long id, JobTerminationReason reason = JobTerminationReason.ExplicitKill) =>
        RequestKill(id, reason).KillRequested;

    /// <summary>Kills every running job (ptk_reset, server shutdown). Returns the count.</summary>
    public int KillAll(JobTerminationReason reason = JobTerminationReason.ExplicitKill)
        => KillAllDetailed(reason).Count(result => result.KillRequested);

    public JobKillResult[] KillAllDetailed(
        JobTerminationReason reason = JobTerminationReason.ExplicitKill)
    {
        if (reason == JobTerminationReason.None || !Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));

        lock (_shutdownGate)
        {
            return _jobs.Keys
                .Select(id => RequestKill(id, reason))
                .ToArray();
        }
    }

    /// <summary>
    /// Closes job-start admission for the complete runspace reset, invalidates
    /// every already-prepared plan, and kills the set linearized before the
    /// reset. Disposing the lease reopens admission after the fresh runspace is
    /// published (or after a failed reset has reported its outcome).
    /// </summary>
    internal JobResetLease BeginReset()
    {
        lock (_shutdownGate)
        {
            if (_stopping || _resetting)
                throw new InvalidOperationException("The background-job manager cannot begin reset.");
            _resetting = true;
            _generation = checked(_generation + 1);
            return new JobResetLease(this, KillAllDetailed(JobTerminationReason.Reset));
        }
    }

    private void EndReset()
    {
        lock (_shutdownGate)
            _resetting = false;
    }

    private void ThrowIfAdmissionClosedLocked()
    {
        if (_stopping || _resetting)
            throw new InvalidOperationException("The background-job manager is resetting or stopping.");
    }

    internal sealed class JobResetLease : IDisposable
    {
        private JobManager? _owner;

        internal JobResetLease(JobManager owner, JobKillResult[] killResults)
        {
            _owner = owner;
            KillResults = killResults;
        }

        internal IReadOnlyList<JobKillResult> KillResults { get; }

        internal int TerminationRequestedCount => KillResults.Count(result => result.KillRequested);

        internal int FailedCount => KillResults.Count(result => result.Disposition == JobKillDisposition.Failed);

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndReset();
    }

    /// <summary>Reads new output since <paramref name="offset"/>; null when the job
    /// does not exist. A full buffer is cut back to the last newline so the next
    /// poll resumes on a clean boundary.</summary>
    public (string Text, long NextOffset, int BytesRead)? ReadOutput(
        long id,
        long offset,
        int maxBytes = 131072)
    {
        if (!_jobs.TryGetValue(id, out var entry)) return null;
        if (!File.Exists(entry.OutputPath)) return (string.Empty, Math.Max(0, offset), 0);

        using var stream = new FileStream(
            entry.OutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset < 0) offset = 0;
        if (offset > stream.Length) offset = stream.Length;
        stream.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[maxBytes];
        var read = stream.Read(buffer, 0, maxBytes);
        if (read <= 0) return (string.Empty, offset, 0);

        var take = read;
        if (read == maxBytes)
        {
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (lastNewline > 0) take = lastNewline + 1;
        }
        return (Encoding.UTF8.GetString(buffer, 0, take), offset + take, take);
    }

    /// <summary>
    /// Stops admission, kills every live child, and waits until each terminal
    /// callback has completed. The audited runtime awaits this before writing
    /// server.stopped, so no job terminal can appear after the lifecycle end.
    /// </summary>
    internal Task ShutdownAsync()
    {
        lock (_shutdownGate)
        {
            _stopping = true;
            return _shutdownTask ??= ShutdownCoreAsync();
        }
    }

    private async Task ShutdownCoreAsync()
    {
        if (ShutdownOverrideForTests is { } shutdownOverride)
            await shutdownOverride().ConfigureAwait(false);
        var entries = _jobs.Values.ToArray();
        KillAllDetailed(JobTerminationReason.Shutdown);
        await Task.WhenAll(entries.Select(entry => entry.TerminalCompleted.Task))
            .ConfigureAwait(false);
        if (entries.Any(entry =>
                Volatile.Read(ref entry.RootTerminationConfirmed) == 0))
        {
            throw new InvalidOperationException(
                "Background-job shutdown could not confirm every tracked root termination.");
        }
    }

    public void Dispose() => ShutdownAsync().GetAwaiter().GetResult();
}
