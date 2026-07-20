using System.Diagnostics;

namespace PtkMcpServer.Tests;

public sealed class ProcessTreeContainmentTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("ptk-containment-").FullName;

    [Fact]
    public void Snapshot_contains_current_process_with_plausible_lineage()
    {
        if (OperatingSystem.IsWindows()) return;

        var snapshot = ProcessTableSnapshot.TryTake();

        Assert.NotNull(snapshot);
        var self = snapshot.Single(row => row.Pid == Environment.ProcessId);
        Assert.True(self.Ppid > 0);
        Assert.True(self.Pgid > 0);
    }

    [Fact]
    public void Tree_kill_defeats_a_sigterm_trap()
    {
        // Pins the refutation of rbc-6 as originally filed: Kill(tree)
        // sends SIGKILL on Unix, so a TERM trap cannot outlive it.
        if (OperatingSystem.IsWindows()) return;

        using var process = Process.Start(new ProcessStartInfo(
            "/bin/bash",
            "-c \"trap '' TERM; sleep 300\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        Assert.NotNull(process);
        process.Kill(entireProcessTree: true);

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Instantly_daemonized_orphan_is_reaped_by_escalation()
    {
        // The rbc-6 gap: "( cmd & )" reparents the grandchild to PID 1
        // before any poll can observe the lineage edge, so Kill(tree)
        // cannot see it. The exclusive-group sweep must still reap it.
        if (OperatingSystem.IsWindows()) return;
        if (!ProcessTreeContainment.UsingExclusiveGroup) return;

        var pidFile = Path.Combine(_root, "orphan.pid");
        var script = Path.Combine(_root, "root.sh");
        File.WriteAllText(
            script,
            """
            #!/bin/bash
            ( /bin/bash -c 'echo $$ > "$0"; exec sleep 300' "$1" & )
            sleep 300
            """);

        using var process = Process.Start(new ProcessStartInfo(
            "/bin/bash",
            $"\"{script}\" \"{pidFile}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(process);
        using var containment = ProcessTreeContainment.Track(process);

        var orphanPid = await ReadPidAsync(pidFile);
        Assert.True(IsAlive(orphanPid), "orphan should be running before the kill");

        process.Kill(entireProcessTree: true);
        var stopped = process.WaitForExit(TimeSpan.FromSeconds(5));
        Assert.True(stopped, "root should stop under tree kill");
        Assert.True(
            IsAlive(orphanPid),
            "orphan should survive the tree kill (this is the rbc-6 gap)");

        stopped = await ProcessTreeContainment.EscalateAsync(process, stopped);

        Assert.True(stopped);
        Assert.False(
            await SpinUntilDeadAsync(orphanPid)
                ? false
                : IsAlive(orphanPid),
            "escalation should reap the group-marked orphan");
    }

    [Fact]
    public void Prepared_launch_classifies_root_exclusive()
    {
        // rbc-15 turn-2 finding 1: the first tracked root must not be
        // forced into fallback because group acquisition ran lazily after
        // the launch. PrepareForLaunch runs before Process.Start in
        // production; after it, a freshly started root classifies
        // exclusive.
        if (OperatingSystem.IsWindows()) return;

        BackgroundJobContainment.PrepareForLaunch();
        BackgroundJobContainment.PrepareForLaunch(); // idempotent
        if (!ProcessTreeContainment.UsingExclusiveGroup) return;

        using var process = Process.Start(new ProcessStartInfo(
            "/bin/bash",
            "-c \"sleep 300\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(process);
        using var containment = ProcessTreeContainment.Track(process);

        try
        {
            Assert.True(
                containment.ExclusiveForTests,
                "a root launched after PrepareForLaunch must classify exclusive");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Terminal_release_sweeps_escaped_orphans()
    {
        // rbc-15 turn-2 finding 2: terminal cleanup removes the registry
        // entry, so later kill/reset/shutdown requests can no longer
        // sweep. Release itself must fire the final sweep that reaps
        // escaped descendants.
        if (OperatingSystem.IsWindows()) return;
        if (!ProcessTreeContainment.UsingExclusiveGroup) return;

        var pidFile = Path.Combine(_root, "release-orphan.pid");
        var script = Path.Combine(_root, "release-root.sh");
        File.WriteAllText(
            script,
            """
            #!/bin/bash
            ( /bin/bash -c 'echo $$ > "$0"; exec sleep 300' "$1" & )
            sleep 300
            """);

        using var process = Process.Start(new ProcessStartInfo(
            "/bin/bash",
            $"\"{script}\" \"{pidFile}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(process);
        BackgroundJobContainment.Attach(process);

        var orphanPid = await ReadPidAsync(pidFile);
        Assert.True(IsAlive(orphanPid), "orphan should be running before the kill");

        process.Kill(entireProcessTree: true);
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(5)));

        await BackgroundJobContainment.ReleaseAsync(process);

        Assert.True(
            await SpinUntilDeadAsync(orphanPid),
            "terminal release must reap the escaped orphan");
    }

    [Fact]
    public void Fallback_survivor_requires_matching_incarnation()
    {
        // rbc-15 turn-2 finding 3: pid + pgid alone can be recycled after
        // the fallback poller freezes. Kill eligibility additionally
        // requires the recorded start-time identity to match the current
        // incarnation, and unknown identities are never signalled.
        if (OperatingSystem.IsWindows()) return;

        using var child = Process.Start(new ProcessStartInfo(
            "/bin/bash",
            "-c \"sleep 300\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(child);

        try
        {
            using var tracker =
                ProcessTreeContainment.CreateFallbackForTests(Environment.ProcessId);
            var start = ProcessTreeContainment.ReadStartUtcTicksForTests(child.Id);
            Assert.NotEqual(0L, start);
            var snapshot = ProcessTableSnapshot.TryTake();
            Assert.NotNull(snapshot);
            var pgid = snapshot.Single(row => row.Pid == child.Id).Pgid;

            // Synthetic table: the child is present, reparented to PID 1,
            // outside any live closure of the root.
            var table = new List<ProcessTableRow> { new(child.Id, 1, pgid) };

            tracker.RecordTrackedForTests(child.Id, pgid, start);
            Assert.Contains(child.Id, tracker.FindEscapeesForTests(table));

            tracker.RecordTrackedForTests(
                child.Id, pgid, start + TimeSpan.FromHours(1).Ticks);
            Assert.DoesNotContain(child.Id, tracker.FindEscapeesForTests(table));

            // Tolerance boundary (codex rbc-15 turn-4): skew exactly at the
            // window edge is still the same incarnation; one tick past the
            // edge — on either side — is a different incarnation and must
            // fail toward NOT killing.
            var tolerance =
                ProcessTreeContainment.StartIdentityToleranceTicksForTests;
            tracker.RecordTrackedForTests(child.Id, pgid, start + tolerance);
            Assert.Contains(child.Id, tracker.FindEscapeesForTests(table));

            tracker.RecordTrackedForTests(
                child.Id, pgid, start + tolerance + 1);
            Assert.DoesNotContain(child.Id, tracker.FindEscapeesForTests(table));

            tracker.RecordTrackedForTests(
                child.Id, pgid, start - tolerance - 1);
            Assert.DoesNotContain(child.Id, tracker.FindEscapeesForTests(table));

            tracker.RecordTrackedForTests(child.Id, pgid, 0);
            Assert.DoesNotContain(child.Id, tracker.FindEscapeesForTests(table));
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit(TimeSpan.FromSeconds(5));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static async Task<int> ReadPidAsync(string pidFile)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var text = File.ReadAllText(pidFile).Trim();
                if (int.TryParse(text, out var pid) && pid > 0) return pid;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            await Task.Delay(50);
        }

        throw new TimeoutException("orphan pid file never appeared");
    }

    private static async Task<bool> SpinUntilDeadAsync(int pid)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsAlive(pid)) return true;
            await Task.Delay(100);
        }

        return !IsAlive(pid);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var probe = Process.GetProcessById(pid);
            return !probe.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
