using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace PtkMcpServer;

public sealed record JobSnapshot(
    int Id,
    int Pid,
    bool Running,
    int? ExitCode,
    DateTimeOffset StartedUtc,
    string Script,
    string OutputPath);

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
    private readonly ConcurrentDictionary<int, JobEntry> _jobs = new();
    private readonly string _jobsDir;
    private int _nextId;

    private sealed record JobEntry(Process Process, string Script, string OutputPath, DateTimeOffset StartedUtc);

    public JobManager(string? jobsDirOverride = null)
    {
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

    /// <summary>Starts a job and returns its snapshot. Throws when pwsh cannot start.</summary>
    public JobSnapshot Start(string script, string? workingDirectory = null)
    {
        Directory.CreateDirectory(_jobsDir);
        var id = Interlocked.Increment(ref _nextId);
        var outputPath = Path.Combine(_jobsDir, $"job-{Environment.ProcessId}-{id}.log");

        // The child redirects its own streams to the log file, so output streams
        // and survives regardless of this server's pipes. The wrapper propagates
        // a native exit code; single quotes in the path are escaped for the
        // single-quoted PowerShell literal.
        var wrapped =
            "& {\n" + script + "\n} *> '" + outputPath.Replace("'", "''") + "'\n" +
            "if ($global:LASTEXITCODE -is [int]) { exit $global:LASTEXITCODE } else { exit 0 }";

        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
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

        var process = Process.Start(psi) ?? throw new InvalidOperationException("pwsh did not start");
        process.StandardInput.Close();
        _jobs[id] = new JobEntry(process, script, outputPath, DateTimeOffset.UtcNow);
        return Snapshot(id)!;
    }

    public JobSnapshot? Snapshot(int id)
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
            entry.OutputPath);
    }

    public JobSnapshot[] List() => [.. _jobs.Keys.OrderBy(id => id).Select(id => Snapshot(id)!)];

    /// <summary>True when the job existed and was still running (and is now killed).</summary>
    public bool Kill(int id)
    {
        if (!_jobs.TryGetValue(id, out var entry) || entry.Process.HasExited) return false;
        try { entry.Process.Kill(entireProcessTree: true); } catch { /* racing its own exit */ }
        return true;
    }

    /// <summary>Kills every running job (ptk_reset, server shutdown). Returns the count.</summary>
    public int KillAll()
    {
        var killed = 0;
        foreach (var id in _jobs.Keys)
        {
            if (Kill(id)) killed++;
        }
        return killed;
    }

    /// <summary>Reads new output since <paramref name="offset"/>; null when the job
    /// does not exist. A full buffer is cut back to the last newline so the next
    /// poll resumes on a clean boundary.</summary>
    public (string Text, long NextOffset)? ReadOutput(int id, long offset, int maxBytes = 131072)
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

    public void Dispose() => KillAll();
}
