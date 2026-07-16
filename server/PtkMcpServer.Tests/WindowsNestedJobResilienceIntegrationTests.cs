using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PtkContainmentTestFixture;
using Xunit.Sdk;

namespace PtkMcpServer.Tests;

[Collection(WindowsProcessCreationCollection.Name)]
public sealed class WindowsNestedJobResilienceIntegrationTests
{
    private static readonly TimeSpan CheckpointTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ContainmentDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdentityPollInterval = TimeSpan.FromMilliseconds(25);

    [Fact]
    public async Task Outer_close_kills_creation_time_nested_host_worker_and_descendant()
    {
        if (!OperatingSystem.IsWindows()) return;

        var scratchDirectory = Path.Combine(
            Path.GetTempPath(),
            $"ptk-nested-job-r0-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchDirectory);
        var hostMarker = Path.Combine(scratchDirectory, "host-entered.marker");
        var workerMarker = Path.Combine(scratchDirectory, "worker-entered.marker");
        var descendantMarker = Path.Combine(scratchDirectory, "descendant-entered.marker");

        WindowsNestedJobGuardianFixture? guardian = null;
        Process? hostWitness = null;
        Process? workerWitness = null;
        Process? descendantWitness = null;
        try
        {
            guardian = WindowsNestedJobGuardianFixture.Launch(scratchDirectory);
            Assert.Equal(0x00002000u, guardian.OuterJobLimitFlags);
            Assert.True(guardian.OuterJobHandleIsNonInheritable);

            hostWitness = OpenIdentityWitness(guardian.HostProcessId);
            Assert.False(hostWitness.HasExited);
            Assert.False(File.Exists(hostMarker));
            Assert.True(guardian.IsInOuterJob(hostWitness));

            guardian.ResumeHost();
            var hostEntered = await ReadEventAsync(guardian, "host-entered:");
            Assert.Equal(guardian.HostProcessId, ParseProcessId(hostEntered, "host-entered:"));
            Assert.Equal("entered\n", await File.ReadAllTextAsync(hostMarker));

            await SendCommandAsync(guardian, "create-worker");
            var workerGated = await ReadEventAsync(guardian, "worker-gated:");
            var workerProcessId = ParseProcessId(workerGated, "worker-gated:");
            workerWitness = OpenIdentityWitness(workerProcessId);

            // The host emits worker-gated only after querying exact membership
            // in its inner Job. The guardian independently queries exact outer
            // membership while the primary thread remains suspended.
            Assert.False(workerWitness.HasExited);
            Assert.False(File.Exists(workerMarker));
            Assert.True(guardian.IsInOuterJob(workerWitness));

            await SendCommandAsync(guardian, "release-worker");
            var workerEntered = await ReadEventAsync(guardian, "worker-entered:");
            Assert.Equal(workerProcessId, ParseProcessId(workerEntered, "worker-entered:"));
            Assert.Equal("entered\n", await File.ReadAllTextAsync(workerMarker));

            await SendCommandAsync(guardian, "spawn-descendant");
            var descendantReady = await ReadEventAsync(guardian, "descendant-ready:");
            var descendantProcessId = ParseProcessId(descendantReady, "descendant-ready:");
            descendantWitness = OpenIdentityWitness(descendantProcessId);

            // The host emits descendant-ready only after querying the exact
            // inner Job. The guardian independently proves outer membership.
            Assert.False(descendantWitness.HasExited);
            Assert.True(guardian.IsInOuterJob(descendantWitness));
            await WaitForMarkerAsync(descendantMarker, guardian);
            Assert.Equal("entered\n", await File.ReadAllTextAsync(descendantMarker));

            ForceFullCollection();
            Assert.False(hostWitness.HasExited);
            Assert.False(workerWitness.HasExited);
            Assert.False(descendantWitness.HasExited);
            Assert.True(guardian.IsInOuterJob(hostWitness));
            Assert.True(guardian.IsInOuterJob(workerWitness));
            Assert.True(guardian.IsInOuterJob(descendantWitness));

            // This is t0 for one shared absolute containment deadline. Held
            // process handles fence identity; no PID lookup occurs after t0.
            var containmentClock = Stopwatch.StartNew();
            guardian.CloseOuterJob();
            await WaitForAllExitAsync(
                containmentClock,
                hostWitness,
                workerWitness,
                descendantWitness);
        }
        finally
        {
            guardian?.Dispose();
            hostWitness?.Dispose();
            workerWitness?.Dispose();
            descendantWitness?.Dispose();
            DeleteScratchBestEffort(scratchDirectory);
        }
    }

    [Fact]
    public void Disposable_probe_has_one_atomic_creator_and_no_job_handle_escape_path()
    {
        var source = File.ReadAllText(FixtureSourcePath());
        var executableSource = RemoveComments(source);

        Assert.Matches(
            new Regex(
                @"ProcThreadAttributeHandleList\s*=\s*0x00020002\s*;",
                RegexOptions.CultureInvariant),
            executableSource);
        Assert.Matches(
            new Regex(
                @"ProcThreadAttributeJobList\s*=\s*0x0002000D\s*;",
                RegexOptions.CultureInvariant),
            executableSource);
        Assert.Matches(
            new Regex(
                @"KillOnJobClose\s*=\s*0x00002000\s*;",
                RegexOptions.CultureInvariant),
            executableSource);
        Assert.Single(Regex.Matches(
            executableSource,
            @"NativeMethods\s*\.\s*CreateProcessW\s*\(",
            RegexOptions.CultureInvariant));
        Assert.Equal(2, Regex.Matches(
            executableSource,
            @"NativeMethods\s*\.\s*UpdateProcThreadAttribute\s*\(",
            RegexOptions.CultureInvariant).Count);
        Assert.Equal(2, Regex.Matches(
            executableSource,
            @"attributeCount\s*:\s*2",
            RegexOptions.CultureInvariant).Count);

        Assert.Contains(
            "NestedProcessAttributeList(job, childHandles)",
            executableSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "IReadOnlyList<SafeFileHandle> childHandles",
            executableSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (childHandles.Count != 3)",
            executableSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateJobObjectW(IntPtr.Zero, null)",
            executableSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsHandleNonInheritable(job)",
            executableSource,
            StringComparison.Ordinal);
        Assert.Equal(5, Regex.Matches(
            executableSource,
            @"WindowsNestedJobNative\s*\.\s*RequireJobHandleAbsent\s*\(",
            RegexOptions.CultureInvariant).Count);
        Assert.Equal(3, Regex.Matches(
            executableSource,
            @"WindowsNestedJobNative\s*\.\s*ClearInheritedControlHandleFlags\s*\(",
            RegexOptions.CultureInvariant).Count);
        Assert.Contains(
            "QueryInformationJobObjectByValue",
            executableSource,
            StringComparison.Ordinal);
        Assert.Contains("inheritHandles: true", executableSource, StringComparison.Ordinal);

        Assert.DoesNotMatch(
            new Regex(
                @"AssignProcessToJobObject|TerminateJobObject|TerminateProcess|DuplicateHandle|" +
                    @"CREATE_BREAKAWAY_FROM_JOB|JOB_OBJECT_LIMIT_(?:SILENT_)?BREAKAWAY_OK|" +
                    @"\.\s*Kill\s*\(|taskkill|Stop-Process",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
            executableSource);
        Assert.DoesNotMatch(
            new Regex(
                @"SetHandleInformation\s*\(\s*(?:job|outerJob|innerJob)",
                RegexOptions.CultureInvariant),
            executableSource);

        var launchBody = ExtractBody(
            source,
            "internal static SuspendedNestedProcess LaunchSuspended(",
            "internal static bool IsProcessInJob(");
        var executableLaunchBody = RemoveComments(launchBody);
        Assert.Single(Regex.Matches(
            executableLaunchBody,
            @"NativeMethods\s*\.\s*CreateProcessW\s*\(",
            RegexOptions.CultureInvariant));
        Assert.DoesNotContain("Process.Start", executableLaunchBody, StringComparison.Ordinal);

        var hostBody = ExtractBody(
            source,
            "internal static int RunHost(",
            "internal static int RunWorker(");
        var innerMembership = hostBody.IndexOf(
            "IsProcessInJob(worker.ProcessHandle, innerJob)",
            StringComparison.Ordinal);
        var gatedAcknowledgment = hostBody.IndexOf("worker-gated:", StringComparison.Ordinal);
        var releaseCommand = hostBody.IndexOf("ReleaseWorkerCommand", StringComparison.Ordinal);
        var resume = hostBody.IndexOf("worker.ResumeOnce()", StringComparison.Ordinal);
        Assert.True(innerMembership >= 0);
        Assert.True(gatedAcknowledgment > innerMembership);
        Assert.True(releaseCommand > gatedAcknowledgment);
        Assert.True(resume > releaseCommand);

        var descendantBody = ExtractBody(
            source,
            "private static Process StartOrdinaryDescendant(",
            "private static void RequireCommand(");
        Assert.Single(Regex.Matches(
            RemoveComments(descendantBody),
            @"Process\s*\.\s*Start\s*\(",
            RegexOptions.CultureInvariant));
    }

    private static async Task SendCommandAsync(
        WindowsNestedJobGuardianFixture guardian,
        string command)
    {
        await guardian.HostCommands.WriteLineAsync(command);
        await guardian.HostCommands.FlushAsync();
    }

    private static async Task<string> ReadEventAsync(
        WindowsNestedJobGuardianFixture guardian,
        string expectedPrefix)
    {
        var read = guardian.HostEvents.ReadLineAsync();
        var timeout = Task.Delay(CheckpointTimeout);
        if (await Task.WhenAny(read, timeout) != read)
        {
            // The fixture uses synchronous anonymous pipe handles. Closing the
            // sole outer Job at the deadline guarantees child death/pipe EOF,
            // even if cancellation cannot interrupt the in-flight read itself.
            guardian.CloseOuterJob();
            try { await read.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            throw new XunitException(
                $"Timed out waiting for {expectedPrefix}. stderr={guardian.CompletedHostError}");
        }
        var line = await read;
        if (line is null)
        {
            throw new XunitException(
                $"Host event pipe closed before {expectedPrefix}. stderr={guardian.CompletedHostError}");
        }
        Assert.StartsWith(expectedPrefix, line, StringComparison.Ordinal);
        return line;
    }

    private static int ParseProcessId(string line, string prefix)
    {
        Assert.StartsWith(prefix, line, StringComparison.Ordinal);
        Assert.True(int.TryParse(
            line.AsSpan(prefix.Length),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var processId));
        Assert.True(processId > 0);
        return processId;
    }

    private static Process OpenIdentityWitness(int processId)
    {
        var process = Process.GetProcessById(processId);
        try
        {
            _ = process.SafeHandle;
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static async Task WaitForMarkerAsync(
        string marker,
        WindowsNestedJobGuardianFixture guardian)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < CheckpointTimeout)
        {
            if (File.Exists(marker)) return;
            await Task.Delay(IdentityPollInterval);
        }
        throw new XunitException(
            $"Timed out waiting for {Path.GetFileName(marker)}. stderr={guardian.CompletedHostError}");
    }

    private static async Task WaitForAllExitAsync(
        Stopwatch containmentClock,
        params Process[] witnesses)
    {
        while (containmentClock.Elapsed < ContainmentDeadline &&
            witnesses.Any(process => !process.HasExited))
        {
            await Task.Delay(IdentityPollInterval);
        }

        Assert.All(witnesses, process => Assert.True(
            process.HasExited,
            $"Process {process.Id} survived outer Job closure for " +
                $"{containmentClock.Elapsed.TotalMilliseconds:F0} ms."));
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static string FixtureSourcePath([CallerFilePath] string testSourcePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testSourcePath) ?? throw new InvalidOperationException(
                "The test source directory is unavailable."),
            "..",
            "PtkContainmentTestFixture",
            "WindowsNestedJobProbe.cs"));

    private static string ExtractBody(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static string RemoveComments(string source) => Regex.Replace(
        source,
        @"/\*.*?\*/|//[^\r\n]*",
        string.Empty,
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static void DeleteScratchBestEffort(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Process-exit assertions own correctness; Windows can retain a
            // just-closed marker briefly.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException note above.
        }
    }
}
