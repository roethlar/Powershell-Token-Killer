using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PtkMcpServer.Worker;

namespace PtkMcpServer;

/// <summary>
/// Process-tree containment for background jobs, mirroring the foreground
/// runner model. On Unix this delegates to
/// <see cref="ProcessTreeContainment"/> (group sweep in exclusive mode,
/// tracked-survivor SIGKILL in fallback mode). On Windows the started
/// root is assigned to a kill-on-close Job Object, so closing the handle
/// — at kill escalation, at terminal cleanup, or by OS handle cleanup
/// when the server process dies — reaps every remaining member of the
/// tree, matching the containment foreground workers already have.
/// </summary>
/// <remarks>
/// Attach is best-effort and never throws: the root has already started,
/// so a containment setup failure must not convert a started job into a
/// start failure. On Windows the assignment happens immediately after
/// <c>Process.Start</c>; children spawned inside that window escape the
/// job. On the kill path that window is covered by the snapshot-walk
/// kill (<c>Process.Kill(entireProcessTree: true)</c>) that precedes
/// escalation; on natural root exit no walk occurs, so a child spawned
/// inside the pre-assign window and abandoned there is an accepted
/// residual of the post-start attach model, bounded by the assignment
/// happening immediately after start. Unknown-start retention (a start
/// whose outcome could not be proven) never attaches: containment
/// applies to confirmed starts only.
/// </remarks>
internal static class BackgroundJobContainment
{
    private static readonly ConditionalWeakTable<Process, State> Registry = [];

    private sealed class State : IDisposable
    {
        internal ProcessTreeContainment? Tracker;
        internal IWindowsJobHandle? Job;
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            try { Tracker?.Dispose(); } catch { }
            try { Job?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Begins containment for a successfully started background root.
    /// Never throws.
    /// </summary>
    /// <summary>
    /// Pre-launch initialization. On Unix forces the one-shot exclusive
    /// process-group acquisition before the child starts, so the first
    /// tracked root inherits the exclusive group instead of silently
    /// degrading to fallback polling. No-op on Windows; idempotent; never
    /// throws; call before every <see cref="Process.Start"/>.
    /// </summary>
    internal static void PrepareForLaunch()
    {
        try { ProcessTreeContainment.EnsureExclusiveGroup(); } catch { }
    }

    internal static void Attach(Process process)
    {
        try
        {
            var state = new State();
            if (OperatingSystem.IsWindows())
                state.Job = TryCreateKillOnCloseJob(process);
            else
                state.Tracker = ProcessTreeContainment.Track(process);
            Registry.AddOrUpdate(process, state);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Kill-time escalation. On Windows closes the kill-on-close job
    /// handle, which terminates every remaining member of the tree. On
    /// Unix delegates to
    /// <see cref="ProcessTreeContainment.EscalateAsync"/>. Never throws;
    /// a no-op when <paramref name="process"/> was never attached.
    /// </summary>
    internal static async Task EscalateAsync(Process process, bool stopped)
    {
        State? state;
        try
        {
            if (!Registry.TryGetValue(process, out state) || state is null)
                return;
        }
        catch
        {
            return;
        }

        if (state.Job is not null)
        {
            state.Dispose();
            return;
        }

        try
        {
            _ = await ProcessTreeContainment.EscalateAsync(process, stopped)
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Terminal cleanup. On Windows closes the job handle, reaping any
    /// descendants that survived root exit so no orphan outlives the job.
    /// On Unix runs a final containment sweep before stopping tracking,
    /// giving the same guarantee: later kill/reset/shutdown requests no
    /// longer find a registry entry, so this is the last point at which
    /// escaped descendants of this job can be reaped. The returned task
    /// completes only after that sweep has run: terminal completion — and
    /// therefore shutdown, which waits on it — cannot outrun the sweep,
    /// and the caller must keep the <see cref="Process"/> undisposed until
    /// the task completes (rbc-15 T2-2). Idempotent and never throws.
    /// </summary>
    internal static Task ReleaseAsync(Process process)
    {
        try
        {
            if (!Registry.TryGetValue(process, out var state) || state is null)
                return Task.CompletedTask;
            Registry.Remove(process);
            if (state.Job is not null)
            {
                state.Dispose();
                return Task.CompletedTask;
            }

            // stopped: true — the job is terminal, so only escaped
            // descendants are signalled; the root is never re-killed here.
            // The tracker is disposed only after the sweep completes so
            // its frozen tracked set stays available to the sweep itself.
            return SweepAndDisposeAsync(process, state);
        }
        catch
        {
            return Task.CompletedTask;
        }

        static async Task SweepAndDisposeAsync(Process process, State unixState)
        {
            try
            {
                _ = await ProcessTreeContainment
                    .EscalateAsync(process, stopped: true)
                    .ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                unixState.Dispose();
            }
        }
    }

    private static IWindowsJobHandle? TryCreateKillOnCloseJob(Process process)
    {
        JobObjectNative.JobHandle? job = null;
        try
        {
            job = JobObjectNative.CreateKillOnCloseJob();
            JobObjectNative.AssignProcess(job, process.SafeHandle);
            return job;
        }
        catch
        {
            try { job?.Dispose(); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Self-contained job-object shim owned by the background path.
    /// <see cref="WindowsWorkerNative"/>'s P/Invoke surface is pinned by a
    /// guard test to exactly one atomic create-in-job with no post-start
    /// assign escape hatch, and that invariant is deliberate: the worker
    /// pipeline must never regain an assign-after-start fallback. The
    /// background root necessarily starts via <see cref="Process.Start()"/>,
    /// so its best-effort post-start assign lives here, outside the guarded
    /// worker surface, with the residual window documented in the class
    /// remarks above.
    /// </summary>
    private static class JobObjectNative
    {
        private const uint JobObjectExtendedLimitInformationClass = 9;

        internal static JobHandle CreateKillOnCloseJob()
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid)
                throw new InvalidOperationException(
                    $"CreateJobObjectW failed ({Marshal.GetLastWin32Error()}).");

            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = WindowsProcessTreeSupervisor.KillOnJobClose,
                },
            };
            if (!SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformationClass,
                    ref information,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                var error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new InvalidOperationException(
                    $"SetInformationJobObject failed ({error}).");
            }

            return job;
        }

        internal static void AssignProcess(JobHandle job, SafeProcessHandle process)
        {
            if (AssignProcessToJobObject(job, process)) return;

            throw new InvalidOperationException(
                $"AssignProcessToJobObject failed ({Marshal.GetLastWin32Error()}).");
        }

        internal sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid, IWindowsJobHandle
        {
            public JobHandle() : base(ownsHandle: true) { }

            protected override bool ReleaseHandle() => CloseHandle(handle);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            internal long PerProcessUserTimeLimit;
            internal long PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal nuint MinimumWorkingSetSize;
            internal nuint MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal nuint Affinity;
            internal uint PriorityClass;
            internal uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            internal ulong ReadOperationCount;
            internal ulong WriteOperationCount;
            internal ulong OtherOperationCount;
            internal ulong ReadTransferCount;
            internal ulong WriteTransferCount;
            internal ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            internal JobObjectBasicLimitInformation BasicLimitInformation;
            internal IoCounters IoInfo;
            internal nuint ProcessMemoryLimit;
            internal nuint JobMemoryLimit;
            internal nuint PeakProcessMemoryUsed;
            internal nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern JobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            JobHandle job,
            uint informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(
            JobHandle job,
            SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
