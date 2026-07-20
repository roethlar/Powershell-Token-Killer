using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PtkMcpServer;

/// <summary>
/// Best-effort Unix descendant containment for launched command processes.
///
/// <see cref="Process.Kill(bool)"/> with <c>entireProcessTree: true</c>
/// delivers SIGKILL on Unix (a SIGTERM trap cannot defeat it), but its
/// descendant enumeration walks live parent links at kill time. A
/// descendant whose intermediate parent already exited was reparented to
/// PID 1 and is invisible to that walk, so it survives the kill (rbc-6).
/// The instant daemonization idiom <c>( cmd &amp; )</c> produces exactly
/// this shape.
///
/// Primary mechanism (exclusive process group): at first use the server
/// makes itself a process-group leader (<c>setpgid(0, 0)</c>; interactive
/// shells already start it as one). From then on the server's pgid is an
/// inherited kernel mark carried by every descendant of every launched
/// process. Kill-time escalation then sweeps: any process whose pgid is
/// the server's, that is not the server itself, and that has no live
/// parent chain back to the server is an escaped descendant — SIGKILL it.
/// This is deterministic: no polling race, and sub-poll-interval
/// intermediate deaths cannot hide an orphan.
///
/// Fallback mechanism (polled closure): when group leadership cannot be
/// acquired, a poller samples the process table and tracks the observed
/// descendant closure of each launched root; escalation SIGKILLs tracked
/// survivors. This misses descendants whose entire intermediate lineage
/// lived shorter than one poll interval.
///
/// Boundaries, by design:
///  - Windows instances are inert; Windows containment is the Job-Object
///    posture tracked by rbc-5.
///  - A descendant that calls setsid (or setpgid) sheds the group mark —
///    the same escape a Windows Job-Object breakaway grants. Closing it
///    requires OS facilities (cgroups, subreapers) that an unprivileged
///    portable parent does not have.
///  - Group leadership means an operator killpg aimed at the server's
///    original group no longer reaches it; service managers that signal
///    direct children or use cgroups are unaffected.
///  - Escalation is containment, not a retry: it never upgrades the audit
///    disposition of the invocation, and a root whose exit was not
///    observed stays unconfirmed unless the post-escalation recheck
///    observes it.
/// </summary>
internal sealed class ProcessTreeContainment : IDisposable
{
    private static readonly ConditionalWeakTable<Process, ProcessTreeContainment> Registry = [];
    private static readonly Lazy<bool> ExclusiveGroup = new(TryAcquireExclusiveGroup);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan EscalationGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Maximum start-time skew treated as the same process incarnation.
    /// Start times come from clock-tick granularity sources (10 ms ticks
    /// on Linux), so genuine conversion error is tens of milliseconds at
    /// most. The tolerance must stay well under <see cref="PollInterval"/>:
    /// a pid recycled between the final poll and the escalation snapshot
    /// must not be able to impersonate the tracked incarnation.
    /// Impersonation additionally requires the recycled occupant's own
    /// start instant to land inside this window around the ORIGINAL's
    /// recorded start — the comparison is between absolute start
    /// instants, not "time since reuse" — so the pid space would have to
    /// wrap back onto the exact pid within the window. Sequential pid
    /// allocation makes that unreachable on supported platforms, and
    /// every ambiguous outcome (unreadable or out-of-window identity on
    /// either side) fails toward NOT killing (rbc-15 T4). Strictly
    /// stronger identity would need an OS handle (pidfd/Job Object);
    /// Windows already uses Job Objects, and this is the Unix fallback.
    /// </summary>
    private static readonly long StartIdentityToleranceTicks =
        TimeSpan.FromMilliseconds(100).Ticks;

    private const int Sigkill = 9;

    private readonly int _rootPid;
    private readonly long _rootStartUtcTicks;
    private readonly bool _inert;
    private readonly bool _useExclusive;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, TrackedEntry> _tracked = [];
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;

    /// <summary>
    /// Fallback-mode tracking record. <see cref="StartUtcTicks"/> is the
    /// incarnation identity: pid plus pgid alone can be recycled after the
    /// poller freezes, so kill-time eligibility additionally requires the
    /// current process's start time to match the one recorded when the pid
    /// was tracked. Zero means the start time could not be read; such
    /// entries are never signalled from the frozen set.
    /// </summary>
    private readonly record struct TrackedEntry(int Pgid, long StartUtcTicks);

    private ProcessTreeContainment(
        int rootPid,
        bool inert,
        bool? forceExclusive = null,
        long? rootStartUtcTicksForTests = null)
    {
        _rootPid = rootPid;
        _inert = inert;
        if (inert) return;

        // Root incarnation identity, recorded while the just-started root
        // is verifiably alive. Zero means unreadable; the fallback
        // live-closure fold then stays disabled rather than trusting a
        // possibly recycled root pid (rbc-15 T2-3).
        _rootStartUtcTicks =
            rootStartUtcTicksForTests ?? TryGetStartUtcTicks(rootPid);

        // Exclusive mode is only trusted for roots that verifiably
        // inherited the exclusive group. A root launched before the lazy
        // setpgid switch ran (it inherited the server's original group)
        // degrades to fallback polling instead of silently escaping every
        // sweep; EnsureExclusiveGroup makes that window defensive rather
        // than load-bearing. Classification is one-shot: a root that later
        // leaves the group via setsid/setpgid stays classified exclusive
        // and its shed descendants escape — that is the documented
        // group-shedding containment boundary, not a fallback trigger.
        _useExclusive = forceExclusive
            ?? (ExclusiveGroup.Value && RootInExclusiveGroup(rootPid));
        if (!_useExclusive)
            _ = Task.Run(() => PollLoopAsync(_stop.Token));
    }

    private static bool RootInExclusiveGroup(int rootPid)
    {
        try { return getpgid(rootPid) == getpgrp(); }
        catch { return false; }
    }

    /// <summary>
    /// Forces the one-shot exclusive-group acquisition. Called before the
    /// first child is ever launched so the first tracked root inherits the
    /// exclusive group instead of degrading to fallback polling. Safe on
    /// any platform, idempotent, never throws.
    /// </summary>
    internal static void EnsureExclusiveGroup()
    {
        if (OperatingSystem.IsWindows()) return;
        try { _ = ExclusiveGroup.Value; } catch { }
    }

    /// <summary>Test seam: this tracker's classification.</summary>
    internal bool ExclusiveForTests => _useExclusive;

    /// <summary>
    /// Test seam: a fallback-mode tracker regardless of the process-global
    /// exclusive-group state, so the frozen-set kill-eligibility rules can
    /// be exercised deterministically.
    /// </summary>
    internal static ProcessTreeContainment CreateFallbackForTests(
        int rootPid, long? rootStartUtcTicks = null) =>
        new(rootPid, inert: false, forceExclusive: false,
            rootStartUtcTicksForTests: rootStartUtcTicks);

    /// <summary>Test seam: inject a fallback tracking record.</summary>
    internal void RecordTrackedForTests(int pid, int pgid, long startUtcTicks)
    {
        lock (_gate) _tracked[pid] = new TrackedEntry(pgid, startUtcTicks);
    }

    /// <summary>Test seam: kill-eligibility set for a synthetic table.</summary>
    internal HashSet<int> FindEscapeesForTests(List<ProcessTableRow> snapshot) =>
        FindEscapees(snapshot);

    /// <summary>Test seam: the recorded start-time identity source.</summary>
    internal static long ReadStartUtcTicksForTests(int pid) => TryGetStartUtcTicks(pid);

    /// <summary>Test seam: the incarnation tolerance boundary.</summary>
    internal static long StartIdentityToleranceTicksForTests =>
        StartIdentityToleranceTicks;

    /// <summary>
    /// Begins containment for a successfully started process. Returns an
    /// inert instance on Windows or when the pid cannot be read. Never
    /// throws.
    /// </summary>
    internal static ProcessTreeContainment Track(Process process)
    {
        ProcessTreeContainment tracker;
        try
        {
            tracker = OperatingSystem.IsWindows()
                ? new ProcessTreeContainment(0, inert: true)
                : new ProcessTreeContainment(process.Id, inert: false);
        }
        catch
        {
            tracker = new ProcessTreeContainment(0, inert: true);
        }

        try { Registry.AddOrUpdate(process, tracker); } catch { }
        return tracker;
    }

    /// <summary>
    /// Kill-time escalation. SIGKILLs escaped descendants (group-marked
    /// orphans in exclusive mode; tracked survivors in fallback mode),
    /// re-kills the root if its exit was not confirmed, and returns the
    /// (possibly upgraded) root-termination confirmation. Never throws;
    /// returns <paramref name="stopped"/> unchanged when no active
    /// tracker exists for <paramref name="process"/>.
    /// </summary>
    internal static async Task<bool> EscalateAsync(Process process, bool stopped)
    {
        ProcessTreeContainment? tracker;
        try
        {
            if (!Registry.TryGetValue(process, out tracker) ||
                tracker is null || tracker._inert)
            {
                return stopped;
            }
        }
        catch
        {
            return stopped;
        }

        try { return await tracker.EscalateCoreAsync(process, stopped); }
        catch { return stopped; }
    }

    /// <summary>
    /// True when the deterministic exclusive-group sweep is active for
    /// this server process. Exposed so guards can assert which mechanism
    /// they exercised.
    /// </summary>
    internal static bool UsingExclusiveGroup =>
        !OperatingSystem.IsWindows() && ExclusiveGroup.Value;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _stop.Cancel(); } catch { }
        _stop.Dispose();
    }

    private static bool TryAcquireExclusiveGroup()
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            if (getpgrp() != getpid()) _ = setpgid(0, 0);
            return getpgrp() == getpid();
        }
        catch
        {
            return false;
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        var rootAbsentPolls = 0;
        while (!token.IsCancellationRequested)
        {
            var snapshot = ProcessTableSnapshot.TryTake();
            if (snapshot is not null)
            {
                // Self-terminate once the root is verifiably gone: the
                // frozen tracked set stays valid because escalation
                // re-validates every pid (presence plus recorded pgid)
                // against a fresh snapshot. This bounds the poller's
                // lifetime even when the owning Process is dropped
                // without a terminal Release.
                rootAbsentPolls = Update(snapshot) ? 0 : rootAbsentPolls + 1;
                if (rootAbsentPolls >= 2) return;
            }

            try { await Task.Delay(PollInterval, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private bool Update(List<ProcessTableRow> snapshot)
    {
        var closure = LiveClosure(snapshot, _rootPid);
        var present = new Dictionary<int, (int Ppid, int Pgid)>(snapshot.Count);
        foreach (var row in snapshot) present[row.Pid] = (row.Ppid, row.Pgid);

        lock (_gate)
        {
            // Retain a tracked pid only while it stays present with the
            // pgid recorded when it was last inside the closure, and is
            // either still inside the closure or reparented to PID 1.
            // The pgid identity check bounds pid-reuse misfire: a
            // recycled pid that reappears under PID 1 in a different
            // group is dropped instead of being retained indefinitely.
            List<int>? drop = null;
            foreach (var (pid, entry) in _tracked)
            {
                if (present.TryGetValue(pid, out var row) &&
                    row.Pgid == entry.Pgid &&
                    (closure.Contains(pid) || row.Ppid == 1))
                {
                    continue;
                }

                (drop ??= []).Add(pid);
            }

            if (drop is not null)
            {
                foreach (var pid in drop) _tracked.Remove(pid);
            }

            foreach (var pid in closure)
            {
                if (!present.TryGetValue(pid, out var row)) continue;

                // Keep the existing record (and its start-time identity)
                // while the pid stays in the same group; only a new or
                // regrouped pid pays the start-time read.
                if (_tracked.TryGetValue(pid, out var existing) &&
                    existing.Pgid == row.Pgid)
                {
                    continue;
                }

                _tracked[pid] = new TrackedEntry(row.Pgid, TryGetStartUtcTicks(pid));
            }
        }

        return present.ContainsKey(_rootPid);
    }

    private async Task<bool> EscalateCoreAsync(Process process, bool stopped)
    {
        var snapshot = ProcessTableSnapshot.TryTake();
        if (snapshot is not null)
        {
            foreach (var pid in FindEscapees(snapshot))
            {
                _ = sys_kill(pid, Sigkill);
            }
        }

        if (!stopped)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                var wait = process.WaitForExitAsync();
                if (await Task.WhenAny(wait, Task.Delay(EscalationGrace)) == wait)
                {
                    await wait;
                }

                stopped = process.HasExited;
            }
            catch { }
        }

        return stopped;
    }

    private HashSet<int> FindEscapees(List<ProcessTableRow> snapshot)
    {
        if (_useExclusive)
        {
            // Deterministic sweep: group-marked processes with no live
            // parent chain back to the server are escaped descendants.
            var self = getpid();
            var group = getpgrp();
            var live = LiveClosure(snapshot, self);
            var escapees = new HashSet<int>();
            foreach (var row in snapshot)
            {
                if (row.Pgid == group && row.Pid != self && !live.Contains(row.Pid))
                    escapees.Add(row.Pid);
            }

            return escapees;
        }

        // Fallback: tracked survivors observed by the poller — validated
        // against the escalation snapshot by presence and recorded pgid,
        // so a pid recycled after the poller froze is never signalled —
        // folded with the root's current live closure.
        var present = new Dictionary<int, int>(snapshot.Count);
        foreach (var row in snapshot) present[row.Pid] = row.Pgid;

        var survivors = new HashSet<int>();
        lock (_gate)
        {
            foreach (var (pid, entry) in _tracked)
            {
                if (!present.TryGetValue(pid, out var pgid) || pgid != entry.Pgid)
                    continue;

                // pid + pgid is not identity: a pid recycled into the same
                // group after the poller froze is a different process. The
                // recorded start time must match the current incarnation
                // within StartIdentityToleranceTicks — clock-tick
                // conversion skew only, kept well under the poll interval
                // so a fast recycle cannot impersonate the tracked process
                // (rbc-15 T2-3); entries whose start time was unreadable
                // at record time are never signalled from the frozen set.
                if (entry.StartUtcTicks == 0) continue;
                var current = TryGetStartUtcTicks(pid);
                if (current == 0) continue;
                if (Math.Abs(current - entry.StartUtcTicks) > StartIdentityToleranceTicks)
                    continue;

                survivors.Add(pid);
            }
        }

        // The live-closure fold is trusted only while the root pid still
        // belongs to the incarnation recorded at track time: a recycled
        // root pid would otherwise donate an unrelated process's
        // descendants to the kill set (rbc-15 T2-3). Unreadable identity
        // on either side disables the fold; the frozen tracked set above
        // still applies.
        var rootCurrent = TryGetStartUtcTicks(_rootPid);
        if (_rootStartUtcTicks != 0 && rootCurrent != 0 &&
            Math.Abs(rootCurrent - _rootStartUtcTicks) <= StartIdentityToleranceTicks)
        {
            foreach (var pid in LiveClosure(snapshot, _rootPid))
            {
                if (present.ContainsKey(pid)) survivors.Add(pid);
            }
        }

        survivors.Remove(_rootPid);
        return survivors;
    }

    /// <summary>
    /// Kernel-reported process start time in UTC ticks, or 0 when it
    /// cannot be read (process gone, permission denied). Stable across
    /// repeated reads for the same incarnation on Linux (/proc starttime)
    /// and macOS (sysctl absolute timeval).
    /// </summary>
    private static long TryGetStartUtcTicks(int pid)
    {
        try
        {
            using var probe = Process.GetProcessById(pid);
            return probe.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private static HashSet<int> LiveClosure(
        List<ProcessTableRow> snapshot,
        int rootPid)
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        foreach (var row in snapshot)
        {
            if (!childrenByParent.TryGetValue(row.Ppid, out var children))
            {
                children = [];
                childrenByParent[row.Ppid] = children;
            }

            children.Add(row.Pid);
        }

        var closure = new HashSet<int>();
        var frontier = new Queue<int>();
        frontier.Enqueue(rootPid);
        while (frontier.Count > 0)
        {
            var parent = frontier.Dequeue();
            if (!childrenByParent.TryGetValue(parent, out var children)) continue;
            foreach (var child in children)
            {
                if (closure.Add(child)) frontier.Enqueue(child);
            }
        }

        return closure;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int sys_kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int getpid();

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgrp();

    [DllImport("libc", SetLastError = true)]
    private static extern int setpgid(int pid, int pgid);

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);
}

internal readonly record struct ProcessTableRow(int Pid, int Ppid, int Pgid);

/// <summary>
/// Point-in-time (pid, ppid, pgid) view of the process table. Reads /proc
/// when it exists (Linux); otherwise shells out to <c>/bin/ps</c>
/// (macOS/BSD).
/// </summary>
internal static class ProcessTableSnapshot
{
    internal static List<ProcessTableRow>? TryTake()
    {
        try
        {
            return Directory.Exists("/proc") ? FromProc() : FromPs();
        }
        catch
        {
            return null;
        }
    }

    private static List<ProcessTableRow> FromProc()
    {
        var rows = new List<ProcessTableRow>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid)) continue;
            string stat;
            try { stat = File.ReadAllText(Path.Combine(dir, "stat")); }
            catch { continue; }

            // Format: pid (comm) state ppid pgrp ... — comm may contain
            // spaces and parentheses, so anchor on the last ')'.
            var close = stat.LastIndexOf(')');
            if (close < 0) continue;
            var fields = stat[(close + 1)..].Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3) continue;
            if (int.TryParse(fields[1], out var ppid) &&
                int.TryParse(fields[2], out var pgid))
            {
                rows.Add(new ProcessTableRow(pid, ppid, pgid));
            }
        }

        return rows;
    }

    private static List<ProcessTableRow>? FromPs()
    {
        var startInfo = new ProcessStartInfo("/bin/ps", "-axo pid=,ppid=,pgid=")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var ps = Process.Start(startInfo);
        if (ps is null) return null;
        var text = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        if (ps.ExitCode != 0) return null;

        var rows = new List<ProcessTableRow>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3) continue;
            if (int.TryParse(fields[0], out var pid) &&
                int.TryParse(fields[1], out var ppid) &&
                int.TryParse(fields[2], out var pgid))
            {
                rows.Add(new ProcessTableRow(pid, ppid, pgid));
            }
        }

        return rows;
    }
}
