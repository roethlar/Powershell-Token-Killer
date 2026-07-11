using System.Collections.Concurrent;
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

public sealed record JobSnapshot(
    long Id,
    int Pid,
    bool Running,
    int? ExitCode,
    DateTimeOffset StartedUtc,
    string Script,
    string OutputPath,
    bool KillRequested = false);

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
    string EncodedCommand);

/// <summary>
/// Owns background jobs (greenfield-design D3): each job is a child pwsh process
/// that redirects all of its own streams to a log file under the jobs directory.
/// A job deliberately does NOT see the warm session's state — jobs are for
/// builds and watchers, native-heavy work that needs no warm state; the pwsh
/// cold start is noise at job timescales, and a separate process gives robust
/// kill/cleanup semantics with no thread-safety questions against the runspace.
/// </summary>
public sealed class JobManager : IDisposable
{
    private readonly ConcurrentDictionary<long, JobEntry> _jobs = new();
    private readonly string _jobsDir;
    private readonly JobPwshExecutable _pwshExecutable;
    private readonly object _shutdownGate = new();
    private long _nextId;
    private Task? _shutdownTask;
    private bool _stopping;
    private bool _resetting;
    private long _generation;
    internal Func<Task>? ShutdownOverrideForTests { get; set; }
    internal Action? BeforeProcessStartForTests { get; set; }
    internal Action<Process>? BeforeKillForTests { get; set; }

    private sealed class JobEntry
    {
        public required Process Process { get; init; }
        public required string Script { get; init; }
        public required string OutputPath { get; init; }
        public required DateTimeOffset StartedUtc { get; init; }
        public Func<JobSnapshot, Task>? OnTerminal { get; init; }
        public TaskCompletionSource<bool> StartRecordPublished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> TerminalCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TerminalPublished;
        public int KillRequested;
    }

    public JobManager(string? jobsDirOverride = null)
        : this(JobPwshExecutable.ResolveFromPath(), jobsDirOverride)
    {
    }

    internal JobManager(JobPwshExecutable pwshExecutable, string? jobsDirOverride = null)
    {
        _pwshExecutable = pwshExecutable;
        _jobsDir = jobsDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ptk", "jobs");
        SweepOldLogs();
    }

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
        var pwshExecutablePath = _pwshExecutable.RequireAvailable();
        long generation;
        lock (_shutdownGate)
        {
            ThrowIfAdmissionClosedLocked();
            generation = _generation;
        }
        var id = Interlocked.Increment(ref _nextId);
        var outputPath = Path.Combine(_jobsDir, $"job-{Environment.ProcessId}-{id}.log");

        // The child redirects its own streams to the log file, so output streams
        // and survives regardless of this server's pipes. The user script enters
        // the wrapper base64-encoded and is compiled INSIDE it: embedding raw
        // text would let an unparseable script kill the whole encoded command
        // before the redirection exists, losing the parse error to the child's
        // (unredirected) stderr. Parse failures land in the log with exit 64.
        var logLiteral = "'" + outputPath.Replace("'", "''") + "'";
        var scriptB64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var wrapped =
            $"$ptkJobLog = {logLiteral}\n" +
            $"$ptkJobScript = [System.Text.Encoding]::Unicode.GetString([System.Convert]::FromBase64String('{scriptB64}'))\n" +
            "try { $ptkJobBlock = [scriptblock]::Create($ptkJobScript) }\n" +
            "catch { $_ | Out-File -LiteralPath $ptkJobLog; exit 64 }\n" +
            "& $ptkJobBlock *> $ptkJobLog\n" +
            "if ($global:LASTEXITCODE -is [int]) { exit $global:LASTEXITCODE } else { exit 0 }";

        var psi = new ProcessStartInfo
        {
            FileName = pwshExecutablePath,
            // Closed immediately below: stdin readers see EOF, never a live pipe
            // (the same hazard ChildStdinGuard closes for foreground children).
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        // Same rationale as the foreground runspace's pinned Bypass (the
        // dddbb6b regression): an unconfigured Windows policy silently blocks
        // module/script loads in the child; ptk is not a security boundary.
        // Harmless off-Windows, where policies do not apply.
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped)));

        return new JobStartPlan(
            id,
            generation,
            script,
            workingDirectory,
            outputPath,
            psi.ArgumentList[^1]);
    }

    /// <summary>
    /// Consumes a prepared descriptor and starts the process exactly once.
    /// Callers must durably record their pre-effect intent before entering this
    /// method. The entry is installed before Process.Start so any exception
    /// after launch cannot leave executing work untracked.
    /// </summary>
    public JobSnapshot CommitStart(
        JobStartPlan plan,
        Func<JobSnapshot, Task>? onTerminal = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var pwshExecutablePath = _pwshExecutable.RequireAvailable();
        lock (_shutdownGate)
        {
            ThrowIfAdmissionClosedLocked();
        }
        Directory.CreateDirectory(_jobsDir);

        var psi = new ProcessStartInfo
        {
            FileName = pwshExecutablePath,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        if (plan.WorkingDirectory is not null) psi.WorkingDirectory = plan.WorkingDirectory;
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(plan.EncodedCommand);

        var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true,
        };
        var entry = new JobEntry
        {
            Process = process,
            Script = plan.Script,
            OutputPath = plan.OutputPath,
            StartedUtc = DateTimeOffset.UtcNow,
            OnTerminal = onTerminal,
        };

        lock (_shutdownGate)
        {
            if (_stopping || _resetting || plan.Generation != _generation)
            {
                process.Dispose();
                throw new InvalidOperationException(
                    "The background-job start was invalidated by reset or shutdown.");
            }
            if (!_jobs.TryAdd(plan.Id, entry))
            {
                process.Dispose();
                throw new InvalidOperationException($"job id {plan.Id} is already registered");
            }

            process.Exited += (_, _) => _ = PublishTerminalAsync(plan.Id, entry);
            var processStarted = false;
            try
            {
                BeforeProcessStartForTests?.Invoke();
                if (!process.Start()) throw new InvalidOperationException("pwsh did not start");
                processStarted = true;
                try { process.StandardInput.Close(); } catch { /* EOF is best effort after a confirmed start */ }
                return Snapshot(plan.Id)!;
            }
            catch
            {
                entry.StartRecordPublished.TrySetResult(false);
                entry.TerminalCompleted.TrySetResult(true);
                _jobs.TryRemove(plan.Id, out _);
                if (processStarted)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                    catch { /* Preserve the authoritative start failure. */ }
                }
                process.Dispose();
                throw;
            }
        }
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

    private async Task PublishTerminalAsync(long id, JobEntry entry)
    {
        if (Interlocked.Exchange(ref entry.TerminalPublished, 1) != 0) return;
        try
        {
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
            entry.TerminalCompleted.TrySetResult(true);
        }
    }

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
            var running = !entry.Process.HasExited;
            return new JobSnapshot(
                id,
                entry.Process.Id,
                running,
                running ? null : entry.Process.ExitCode,
                entry.StartedUtc,
                entry.Script,
                entry.OutputPath,
                Volatile.Read(ref entry.KillRequested) != 0);
        }
    }

    public JobSnapshot[] List()
    {
        lock (_shutdownGate)
            return [.. _jobs.Keys.OrderBy(id => id).Select(id => Snapshot(id)!).Where(job => job is not null)];
    }

    /// <summary>True when the job existed and was still running (and is now killed).</summary>
    public bool Kill(long id)
    {
        lock (_shutdownGate)
        {
            if (!_jobs.TryGetValue(id, out var entry) || entry.Process.HasExited) return false;
            try
            {
                BeforeKillForTests?.Invoke(entry.Process);
                entry.Process.Kill(entireProcessTree: true);
                Interlocked.Exchange(ref entry.KillRequested, 1);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Kills every running job (ptk_reset, server shutdown). Returns the count.</summary>
    public int KillAll()
    {
        lock (_shutdownGate)
        {
            var killed = 0;
            foreach (var id in _jobs.Keys)
            {
                if (Kill(id)) killed++;
            }
            return killed;
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
            return new JobResetLease(this, KillAll());
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

        internal JobResetLease(JobManager owner, int killedCount)
        {
            _owner = owner;
            KilledCount = killedCount;
        }

        internal int KilledCount { get; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndReset();
    }

    /// <summary>Reads new output since <paramref name="offset"/>; null when the job
    /// does not exist. A full buffer is cut back to the last newline so the next
    /// poll resumes on a clean boundary.</summary>
    public (string Text, long NextOffset)? ReadOutput(long id, long offset, int maxBytes = 131072)
    {
        if (!_jobs.TryGetValue(id, out var entry)) return null;
        if (!File.Exists(entry.OutputPath)) return (string.Empty, Math.Max(0, offset));

        using var stream = new FileStream(
            entry.OutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset < 0) offset = 0;
        if (offset > stream.Length) offset = stream.Length;
        stream.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[maxBytes];
        var read = stream.Read(buffer, 0, maxBytes);
        if (read <= 0) return (string.Empty, offset);

        var take = read;
        if (read == maxBytes)
        {
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (lastNewline > 0) take = lastNewline + 1;
        }
        return (Encoding.UTF8.GetString(buffer, 0, take), offset + take);
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
        KillAll();
        foreach (var entry in entries)
        {
            try { await entry.Process.WaitForExitAsync().ConfigureAwait(false); }
            catch (InvalidOperationException) { /* a failed start was removed */ }
        }
        await Task.WhenAll(entries.Select(entry => entry.TerminalCompleted.Task))
            .ConfigureAwait(false);
    }

    public void Dispose() => ShutdownAsync().GetAwaiter().GetResult();
}
