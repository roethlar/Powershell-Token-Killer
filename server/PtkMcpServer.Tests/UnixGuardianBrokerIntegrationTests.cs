using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PtkMcpServer.Tests;

[Collection(ResilienceProcessCreationCollection.Name)]
public sealed class UnixGuardianBrokerIntegrationTests
{
    private static readonly TimeSpan CheckpointTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] ReadyProperties =
    [
        "barrier", "registry", "guardianPid", "guardianPgid", "brokerPid",
        "brokerPgid", "hostPid", "hostPgid", "workerBrokerPid",
        "workerBrokerPgid", "workerPid", "workerPgid", "descendantPid",
        "livenessWriters",
    ];
    private static readonly string[] TranscriptProperties =
    [
        "barrier", "registry", "termAtMs", "killAtMs", "termToKillMs",
        "deadlineMs", "pollMs", "waitpidCalls", "waitpidTarget",
        "nonChildWaitpidCalls", "identityPolls", "polledWorkerBroker",
        "polledWorker", "polledDescendant", "hostAliveAfterTerm",
        "nonchildrenAliveAfterTerm", "completedAtMs", "hostGroupGone",
        "workerGroupGone", "survivors", "zombies",
    ];

    [Theory]
    [MemberData(nameof(Barriers))]
    public async Task Guardian_death_contains_every_creation_barrier(
        string barrier,
        string expectedRegistry,
        bool workerExists,
        bool workerMoved,
        bool userReleased)
    {
        if (OperatingSystem.IsWindows()) return;

        var scratch = Path.Combine(
            Path.GetTempPath(),
            $"ptk-unix-guardian-r0-{barrier}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        var fixture = Path.Combine(scratch, "ptk-guardian-broker-fixture");
        var transcriptPath = Path.Combine(scratch, "transcript.json");
        var markerPath = Path.Combine(scratch, "user-entered.marker");
        Process? guardian = null;
        Task<string>? standardError = null;
        var observedProcesses = new Dictionary<int, DateTime>();
        var completed = false;
        try
        {
            await CompileFixtureAsync(fixture);
            guardian = StartFixture(fixture, barrier, transcriptPath, markerPath);
            standardError = guardian.StandardError.ReadToEndAsync();

            var readyLine = await ReadLineAsync(guardian.StandardOutput, CheckpointTimeout);
            var ready = ParseReady(readyLine);
            observedProcesses = CaptureProcessIdentities(ready.ProcessIds);

            Assert.Equal(barrier, ready.Barrier);
            Assert.Equal(expectedRegistry, ready.Registry);
            Assert.Equal(guardian.Id, ready.GuardianPid);
            Assert.Equal(ready.GuardianPid, ready.GuardianPgid);
            Assert.Equal(ready.GuardianPgid, ready.BrokerPgid);
            Assert.Equal(1, ready.LivenessWriters);
            Assert.True(ready.BrokerPid > 0);
            Assert.True(ready.HostPid > 0);
            Assert.Equal(ready.HostPid, ready.HostPgid);
            Assert.NotEqual(ready.BrokerPgid, ready.HostPgid);
            Assert.NotEqual(ready.GuardianPid, ready.BrokerPid);
            Assert.NotEqual(ready.BrokerPid, ready.HostPid);

            if (!workerExists)
            {
                Assert.Equal(0, ready.WorkerBrokerPid);
                Assert.Equal(0, ready.WorkerBrokerPgid);
                Assert.Equal(0, ready.WorkerPid);
                Assert.Equal(0, ready.WorkerPgid);
            }
            else
            {
                Assert.True(ready.WorkerBrokerPid > 0);
                Assert.Equal(ready.HostPid, ready.WorkerBrokerPgid);
                Assert.True(ready.WorkerPid > 0);
                Assert.NotEqual(ready.WorkerBrokerPid, ready.WorkerPid);
                Assert.DoesNotContain(ready.WorkerBrokerPid, new int[]
                {
                    ready.GuardianPid, ready.BrokerPid, ready.HostPid,
                });
                Assert.DoesNotContain(ready.WorkerPid, new int[]
                {
                    ready.GuardianPid, ready.BrokerPid, ready.HostPid,
                });
                Assert.Equal(
                    workerMoved ? ready.WorkerPid : ready.HostPid,
                    ready.WorkerPgid);
            }

            if (userReleased)
            {
                Assert.True(ready.DescendantPid > 0);
                Assert.NotEqual(ready.WorkerPid, ready.DescendantPid);
                Assert.Equal("user-entered\n", await File.ReadAllTextAsync(markerPath));
            }
            else
            {
                Assert.Equal(0, ready.DescendantPid);
                Assert.False(File.Exists(markerPath));
            }

            // This is deliberately a single-process kill, never a managed
            // process-tree cleanup. The outer broker must observe EOF from the
            // guardian's sole liveness writer and do all descendant cleanup.
            guardian.Kill();
            await guardian.WaitForExitAsync().WaitAsync(CheckpointTimeout);

            var transcriptLine = await ReadFileLineAsync(
                transcriptPath,
                CheckpointTimeout);
            var transcript = ParseTranscript(transcriptLine);

            Assert.Equal(barrier, transcript.Barrier);
            Assert.Equal(expectedRegistry, transcript.Registry);
            Assert.InRange(transcript.TermAtMs, 0, 25);
            Assert.Equal(2_000, transcript.TermToKillMs);
            Assert.True(transcript.KillAtMs >= transcript.TermToKillMs);
            Assert.Equal(10_000, transcript.DeadlineMs);
            Assert.Equal(25, transcript.PollMs);
            Assert.InRange(
                transcript.CompletedAtMs,
                transcript.KillAtMs,
                transcript.DeadlineMs);
            Assert.True(transcript.HostAliveAfterTerm);
            Assert.Equal(workerExists ? (userReleased ? 3 : 2) : 0,
                transcript.NonchildrenAliveAfterTerm);
            Assert.InRange(transcript.WaitpidCalls, 1, 401);
            Assert.Equal(ready.HostPid, transcript.WaitpidTarget);
            Assert.Equal(0, transcript.NonChildWaitpidCalls);
            Assert.Equal(workerExists, transcript.PolledWorkerBroker);
            Assert.Equal(workerExists, transcript.PolledWorker);
            Assert.Equal(userReleased, transcript.PolledDescendant);
            Assert.True(transcript.IdentityPolls >= (workerExists ? (userReleased ? 3 : 2) : 0));
            Assert.True(transcript.HostGroupGone);
            Assert.True(transcript.WorkerGroupGone);
            Assert.Equal(0, transcript.Survivors);
            Assert.Equal(0, transcript.Zombies);

            await AssertProcessesGoneAsync(observedProcesses, CheckpointTimeout);
            if (userReleased)
                Assert.Equal("user-entered\n", await File.ReadAllTextAsync(markerPath));
            else
                Assert.False(File.Exists(markerPath));
            Assert.Equal(string.Empty, await standardError.WaitAsync(CheckpointTimeout));
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                KillBestEffort(guardian);
                foreach (var process in observedProcesses)
                    KillBestEffort(process.Key, process.Value);
            }
            guardian?.Dispose();
            DeleteDirectoryBestEffort(scratch);
        }
    }

    [Fact]
    public async Task Guardian_death_interrupts_a_stalled_creation_protocol()
    {
        if (OperatingSystem.IsWindows()) return;

        var scratch = Path.Combine(
            Path.GetTempPath(),
            $"ptk-unix-guardian-r0-stalled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        var fixture = Path.Combine(scratch, "ptk-guardian-broker-fixture");
        var transcriptPath = Path.Combine(scratch, "transcript.json");
        var markerPath = Path.Combine(scratch, "user-entered.marker");
        Process? guardian = null;
        Task<string>? standardError = null;
        var observedProcesses = new Dictionary<int, DateTime>();
        var completed = false;
        try
        {
            await CompileFixtureAsync(fixture);
            guardian = StartFixture(
                fixture,
                "host_gated",
                transcriptPath,
                markerPath,
                mode: "guardian-stalled");
            standardError = guardian.StandardError.ReadToEndAsync();

            var ready = ParseReady(await ReadLineAsync(
                guardian.StandardOutput,
                CheckpointTimeout));
            observedProcesses = CaptureProcessIdentities(ready.ProcessIds);
            Assert.Equal("host_gated", ready.Barrier);
            Assert.Equal("none", ready.Registry);
            Assert.Equal(guardian.Id, ready.GuardianPid);
            Assert.Equal(ready.GuardianPid, ready.GuardianPgid);
            Assert.Equal(ready.GuardianPgid, ready.BrokerPgid);
            Assert.Equal(ready.HostPid, ready.HostPgid);
            Assert.NotEqual(ready.BrokerPgid, ready.HostPgid);
            Assert.Equal(0, ready.WorkerPid);
            Assert.False(File.Exists(markerPath));

            guardian.Kill();
            await guardian.WaitForExitAsync().WaitAsync(CheckpointTimeout);

            var transcript = ParseTranscript(await ReadFileLineAsync(
                transcriptPath,
                CheckpointTimeout));
            Assert.Equal("host_gated", transcript.Barrier);
            Assert.Equal("none", transcript.Registry);
            Assert.InRange(transcript.TermAtMs, 0, 25);
            Assert.Equal(2_000, transcript.TermToKillMs);
            Assert.True(transcript.KillAtMs >= transcript.TermToKillMs);
            Assert.Equal(10_000, transcript.DeadlineMs);
            Assert.InRange(
                transcript.CompletedAtMs,
                transcript.KillAtMs,
                transcript.DeadlineMs);
            Assert.True(transcript.HostAliveAfterTerm);
            Assert.Equal(0, transcript.NonchildrenAliveAfterTerm);
            Assert.InRange(transcript.WaitpidCalls, 1, 401);
            Assert.Equal(ready.HostPid, transcript.WaitpidTarget);
            Assert.Equal(0, transcript.NonChildWaitpidCalls);
            Assert.True(transcript.HostGroupGone);
            Assert.True(transcript.WorkerGroupGone);
            Assert.Equal(0, transcript.Survivors);
            Assert.Equal(0, transcript.Zombies);

            await AssertProcessesGoneAsync(observedProcesses, CheckpointTimeout);
            Assert.False(File.Exists(markerPath));
            Assert.Equal(string.Empty, await standardError.WaitAsync(CheckpointTimeout));
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                KillBestEffort(guardian);
                foreach (var process in observedProcesses)
                    KillBestEffort(process.Key, process.Value);
            }
            guardian?.Dispose();
            DeleteDirectoryBestEffort(scratch);
        }
    }

    [Fact]
    public async Task Corrupted_start_identity_cannot_signal_a_live_sentinel()
    {
        if (OperatingSystem.IsWindows()) return;

        var scratch = Path.Combine(
            Path.GetTempPath(),
            $"ptk-unix-identity-r0-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        var fixture = Path.Combine(scratch, "ptk-guardian-broker-fixture");
        Process? sentinel = null;
        Process? probe = null;
        try
        {
            await CompileFixtureAsync(fixture);
            sentinel = StartFixtureProcess(fixture, "sentinel");
            var sentinelError = sentinel.StandardError.ReadToEndAsync();
            var sentinelReady = await ReadLineAsync(
                sentinel.StandardOutput,
                CheckpointTimeout);
            using (var document = JsonDocument.Parse(sentinelReady, StrictJsonOptions()))
            {
                AssertExactProperties(document.RootElement, ["pid"]);
                Assert.Equal(sentinel.Id, document.RootElement.GetProperty("pid").GetInt32());
            }

            probe = StartFixtureProcess(
                fixture,
                "identity-fence",
                sentinel.Id.ToString(CultureInfo.InvariantCulture));
            var probeOutput = probe.StandardOutput.ReadToEndAsync();
            var probeError = probe.StandardError.ReadToEndAsync();
            await probe.WaitForExitAsync().WaitAsync(CheckpointTimeout);
            var output = await probeOutput;
            Assert.Equal(0, probe.ExitCode);
            Assert.Equal(string.Empty, await probeError);
            Assert.EndsWith("\n", output, StringComparison.Ordinal);
            Assert.Equal(1, output.Count(character => character == '\n'));

            using (var document = JsonDocument.Parse(
                output.TrimEnd('\n'),
                StrictJsonOptions()))
            {
                var root = document.RootElement;
                AssertExactProperties(
                    root,
                    ["targetPid", "corruptedIdentityRejected", "targetAlive"]);
                Assert.Equal(sentinel.Id, root.GetProperty("targetPid").GetInt32());
                Assert.True(root.GetProperty("corruptedIdentityRejected").GetBoolean());
                Assert.True(root.GetProperty("targetAlive").GetBoolean());
            }
            Assert.False(sentinel.HasExited);

            sentinel.Kill();
            await sentinel.WaitForExitAsync().WaitAsync(CheckpointTimeout);
            Assert.Equal(string.Empty, await sentinel.StandardOutput.ReadToEndAsync());
            Assert.Equal(string.Empty, await sentinelError);
        }
        finally
        {
            await KillAndWaitBestEffortAsync(probe);
            await KillAndWaitBestEffortAsync(sentinel);
            probe?.Dispose();
            sentinel?.Dispose();
            DeleteDirectoryBestEffort(scratch);
        }
    }

    [Fact]
    public void Native_source_freezes_the_liveness_registry_and_reaping_boundary()
    {
        var source = File.ReadAllText(SourcePath());

        Assert.Matches(
            new Regex(@"#define\s+PTK_TERM_TO_KILL_MILLISECONDS\s+2000\b"),
            source);
        Assert.Matches(
            new Regex(@"#define\s+PTK_CONTAINMENT_DEADLINE_MILLISECONDS\s+10000\b"),
            source);
        Assert.Matches(
            new Regex(@"#define\s+PTK_IDENTITY_POLL_MILLISECONDS\s+25\b"),
            source);

        // Portable wait operations own only direct children. There is one
        // nonblocking waitpid call site in the entire native fixture and its
        // target is the broker's direct host child; worker identities are
        // polled instead.
        Assert.Single(Regex.Matches(source, @"\bwaitpid\s*\(").Cast<Match>());
        Assert.Empty(Regex.Matches(
            source,
            @"\b(?:waitid|wait3|wait4|wait|syscall)\s*\(").Cast<Match>());
        Assert.Contains("waitpid(host_pid, &status, WNOHANG)", source, StringComparison.Ordinal);
        Assert.Contains("count_live_nonchildren(", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"\b(?:system|popen|setsid|setpgrp)\s*\("),
            source);
        Assert.DoesNotContain("kill(-1", source, StringComparison.Ordinal);

        // The guardian owns the only liveness writer. The broker closes that
        // inherited writer before it can fork the host; the guardian closes
        // the read end and retains the writer until its hard death.
        Assert.Contains("close_quietly(liveness[1]);", source, StringComparison.Ordinal);
        Assert.Contains("close_checked(liveness[0]);", source, StringComparison.Ordinal);
        Assert.Contains("host_main(host_command[0], host_event[1], liveness_read, ready_write)",
            source, StringComparison.Ordinal);
        Assert.Contains("close_quietly(inherited_liveness);", source, StringComparison.Ordinal);

        var outerStart = source.IndexOf("static void outer_broker_main(", StringComparison.Ordinal);
        Assert.True(outerStart >= 0);
        var outer = source[outerStart..];
        Assert.Contains("poll(descriptors, 2U, -1)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "receive_message(\n        host_event[0]",
            outer,
            StringComparison.Ordinal);
        AssertOrder(outer,
            "require_process_group(observed.worker_broker_pid, host);",
            "observed.registry = REGISTRY_PENDING;",
            "observed.worker_process_group = observed.worker_pid;",
            "send_message(host_command[1], &pending_ack);",
            "MESSAGE_MOVE_STARTED,",
            "if (barrier == BARRIER_DURING_MOVE)",
            "send_message(host_command[1], &continue_move_command);",
            "MESSAGE_WORKER_MOVED,",
            "require_process_group(observed.worker_pid, observed.worker_pid);",
            "observed.registry = REGISTRY_ARMED;",
            "send_message(host_command[1], &armed_ack);",
            "send_message(host_command[1], &continue_armed);",
            "send_message(host_command[1], &open_gate);");

        var workerBrokerStart = source.IndexOf(
            "static void worker_broker_main(",
            StringComparison.Ordinal);
        var hostStart = source.IndexOf("static void host_main(", StringComparison.Ordinal);
        Assert.True(workerBrokerStart >= 0 && hostStart > workerBrokerStart);
        var workerBroker = source[workerBrokerStart..hostStart];
        AssertOrder(workerBroker,
            "MESSAGE_MOVE_WORKER",
            "setpgid(worker, worker)",
            "MESSAGE_MOVE_STARTED",
            "MESSAGE_CONTINUE_MOVE",
            "MESSAGE_WORKER_MOVED");

        var containmentStart = source.IndexOf("static void contain_and_report(", StringComparison.Ordinal);
        Assert.True(containmentStart >= 0);
        var containment = source[containmentStart..outerStart];
        Assert.Contains("kill(-leader, signal_number)", source, StringComparison.Ordinal);
        Assert.Contains(
            "signal_direct_host_group(observed->host_pid, SIGKILL);",
            containment,
            StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"sleep_until\(started \+ \(uint64_t\)PTK_TERM_TO_KILL_MILLISECONDS\);\s*" +
                @"uint64_t kill_at = monotonic_milliseconds\(\) - started;\s*" +
                @"signal_direct_host_group\(observed->host_pid, SIGKILL\);",
                RegexOptions.CultureInvariant),
            containment);
        Assert.DoesNotContain(
            "signal_one_identity(\n        observed->worker_pid",
            containment,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "signal_one_identity(\n        observed->descendant_pid",
            containment,
            StringComparison.Ordinal);
        AssertOrder(containment,
            "SIGTERM",
            "sleep_until(started + (uint64_t)PTK_TERM_TO_KILL_MILLISECONDS);",
            "uint64_t kill_at = monotonic_milliseconds() - started;",
            "signal_direct_host_group(observed->host_pid, SIGKILL);",
            "SIGKILL",
            "close_quietly(host_command_write);",
            "PTK_CONTAINMENT_DEADLINE_MILLISECONDS",
            "reap_direct_host(observed->host_pid);",
            "count_live_nonchildren(");
    }

    [Fact]
    public void Creation_barrier_matrix_is_exact()
    {
        Assert.Equal(
            new[]
            {
                "host_gated", "before_pending", "during_move",
                "before_armed_ack", "after_armed_ack",
                "after_release_command", "after_release",
            },
            BarrierCases.Select(value => value.Barrier));
    }

    private static readonly BarrierCase[] BarrierCases =
    [
        new("host_gated", "none", false, false, false),
        new("before_pending", "none", true, false, false),
        new("during_move", "pending", true, true, false),
        new("before_armed_ack", "armed", true, true, false),
        new("after_armed_ack", "armed", true, true, false),
        new("after_release_command", "armed", true, true, false),
        new("after_release", "armed", true, true, true),
    ];

    public static TheoryData<string, string, bool, bool, bool> Barriers
    {
        get
        {
            var result = new TheoryData<string, string, bool, bool, bool>();
            foreach (var value in BarrierCases)
                result.Add(
                    value.Barrier,
                    value.Registry,
                    value.WorkerExists,
                    value.WorkerMoved,
                    value.UserReleased);
            return result;
        }
    }

    private static async Task CompileFixtureAsync(string outputPath)
    {
        const string compiler = "/usr/bin/cc";
        Assert.True(File.Exists(compiler), $"Required native compiler is missing: {compiler}");
        var startInfo = new ProcessStartInfo
        {
            FileName = compiler,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "-std=c17", "-O2", "-fno-common", "-fstack-protector-strong",
            "-Wall", "-Wextra", "-Werror", "-Wpedantic", "-Wshadow",
            "-Wstrict-prototypes", "-Wmissing-prototypes",
            SourcePath(), "-o", outputPath,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var compilerProcess = Process.Start(startInfo) ??
            throw new InvalidOperationException("The native fixture compiler did not start.");
        var standardOutput = compilerProcess.StandardOutput.ReadToEndAsync();
        var standardError = compilerProcess.StandardError.ReadToEndAsync();
        await compilerProcess.WaitForExitAsync().WaitAsync(CheckpointTimeout);
        var output = await standardOutput;
        var error = await standardError;
        Assert.True(
            compilerProcess.ExitCode == 0,
            $"Native fixture compile failed with {compilerProcess.ExitCode}. stdout='{output}' stderr='{error}'");
        Assert.Equal(string.Empty, output);
        Assert.Equal(string.Empty, error);
        Assert.True(File.Exists(outputPath));
    }

    private static Process StartFixture(
        string fixture,
        string barrier,
        string transcriptPath,
        string markerPath,
        string mode = "guardian")
    {
        return StartFixtureProcess(
            fixture,
            mode,
            barrier,
            transcriptPath,
            markerPath);
    }

    private static Process StartFixtureProcess(string fixture, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fixture,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ??
            throw new InvalidOperationException("The Unix guardian fixture did not start.");
    }

    private static async Task<string> ReadLineAsync(StreamReader reader, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        return await reader.ReadLineAsync(cancellation.Token) ??
            throw new EndOfStreamException("The Unix guardian fixture closed before its barrier checkpoint.");
    }

    private static async Task<string> ReadFileLineAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastFailure = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var text = await File.ReadAllTextAsync(path);
                    if (text.EndsWith('\n') && text.Count(character => character == '\n') == 1)
                        return text.TrimEnd('\n');
                }
            }
            catch (IOException exception)
            {
                lastFailure = exception;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException(
            $"The Unix guardian broker did not publish one complete transcript at '{path}'.",
            lastFailure);
    }

    private static ReadySnapshot ParseReady(string json)
    {
        using var document = JsonDocument.Parse(json, StrictJsonOptions());
        var root = document.RootElement;
        AssertExactProperties(root, ReadyProperties);
        return new ReadySnapshot(
            root.GetProperty("barrier").GetString()!,
            root.GetProperty("registry").GetString()!,
            root.GetProperty("guardianPid").GetInt32(),
            root.GetProperty("guardianPgid").GetInt32(),
            root.GetProperty("brokerPid").GetInt32(),
            root.GetProperty("brokerPgid").GetInt32(),
            root.GetProperty("hostPid").GetInt32(),
            root.GetProperty("hostPgid").GetInt32(),
            root.GetProperty("workerBrokerPid").GetInt32(),
            root.GetProperty("workerBrokerPgid").GetInt32(),
            root.GetProperty("workerPid").GetInt32(),
            root.GetProperty("workerPgid").GetInt32(),
            root.GetProperty("descendantPid").GetInt32(),
            root.GetProperty("livenessWriters").GetInt32());
    }

    private static TranscriptSnapshot ParseTranscript(string json)
    {
        using var document = JsonDocument.Parse(json, StrictJsonOptions());
        var root = document.RootElement;
        AssertExactProperties(root, TranscriptProperties);
        return new TranscriptSnapshot(
            root.GetProperty("barrier").GetString()!,
            root.GetProperty("registry").GetString()!,
            root.GetProperty("termAtMs").GetInt64(),
            root.GetProperty("killAtMs").GetInt64(),
            root.GetProperty("termToKillMs").GetInt32(),
            root.GetProperty("deadlineMs").GetInt32(),
            root.GetProperty("pollMs").GetInt32(),
            root.GetProperty("waitpidCalls").GetInt32(),
            root.GetProperty("waitpidTarget").GetInt32(),
            root.GetProperty("nonChildWaitpidCalls").GetInt32(),
            root.GetProperty("identityPolls").GetInt32(),
            root.GetProperty("polledWorkerBroker").GetBoolean(),
            root.GetProperty("polledWorker").GetBoolean(),
            root.GetProperty("polledDescendant").GetBoolean(),
            root.GetProperty("hostAliveAfterTerm").GetBoolean(),
            root.GetProperty("nonchildrenAliveAfterTerm").GetInt32(),
            root.GetProperty("completedAtMs").GetInt64(),
            root.GetProperty("hostGroupGone").GetBoolean(),
            root.GetProperty("workerGroupGone").GetBoolean(),
            root.GetProperty("survivors").GetInt32(),
            root.GetProperty("zombies").GetInt32());
    }

    private static JsonDocumentOptions StrictJsonOptions() => new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    private static void AssertExactProperties(JsonElement value, string[] expected)
    {
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(actual.Length, actual.Distinct(StringComparer.Ordinal).Count());
    }

    private static async Task AssertProcessesGoneAsync(
        IReadOnlyDictionary<int, DateTime> processIdentities,
        TimeSpan timeout)
    {
        var remaining = processIdentities
            .Where(process => process.Key > 0)
            .ToDictionary(process => process.Key, process => process.Value);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (remaining.Count > 0 && DateTimeOffset.UtcNow < deadline)
        {
            foreach (var process in remaining.ToArray())
            {
                if (ProcessIdentityIsGone(process.Key, process.Value))
                    remaining.Remove(process.Key);
            }
            if (remaining.Count > 0) await Task.Delay(25);
        }
        Assert.True(
            remaining.Count == 0,
            $"Unix guardian fixture left process IDs: {string.Join(", ", remaining.Keys.Order())}");
    }

    private static Dictionary<int, DateTime> CaptureProcessIdentities(
        IEnumerable<int> processIds)
    {
        var identities = new Dictionary<int, DateTime>();
        foreach (var processId in processIds.Where(id => id > 0))
        {
            using var process = Process.GetProcessById(processId);
            identities.Add(processId, process.StartTime.ToUniversalTime());
        }
        return identities;
    }

    private static bool ProcessIdentityIsGone(int processId, DateTime expectedStartTimeUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ||
                process.StartTime.ToUniversalTime() != expectedStartTimeUtc;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task KillAndWaitBestEffortAsync(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill();
            if (process is not null)
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Preserve the primary assertion; the test owns only this process.
        }
    }

    private static void KillBestEffort(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill();
        }
        catch
        {
            // Preserve the primary assertion; known-PID cleanup continues.
        }
    }

    private static void KillBestEffort(int processId, DateTime expectedStartTimeUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited &&
                process.StartTime.ToUniversalTime() == expectedStartTimeUtc)
                process.Kill();
        }
        catch
        {
            // The identity already disappeared or cannot be opened.
        }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Assertions own correctness; cleanup is best effort on failure.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException cleanup note.
        }
    }

    private static void AssertOrder(string source, params string[] values)
    {
        var previous = -1;
        foreach (var value in values)
        {
            var current = source.IndexOf(value, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{value}' after source offset {previous}.");
            previous = current;
        }
    }

    private static string SourcePath([CallerFilePath] string testSourcePath = "") =>
        Path.Combine(
            Path.GetDirectoryName(testSourcePath) ??
                throw new InvalidOperationException("The test source directory is unavailable."),
            "Native",
            "ptk_guardian_broker_fixture.c");

    private sealed record ReadySnapshot(
        string Barrier,
        string Registry,
        int GuardianPid,
        int GuardianPgid,
        int BrokerPid,
        int BrokerPgid,
        int HostPid,
        int HostPgid,
        int WorkerBrokerPid,
        int WorkerBrokerPgid,
        int WorkerPid,
        int WorkerPgid,
        int DescendantPid,
        int LivenessWriters)
    {
        internal IEnumerable<int> ProcessIds =>
        [GuardianPid, BrokerPid, HostPid, WorkerBrokerPid, WorkerPid, DescendantPid];
    }

    private sealed record BarrierCase(
        string Barrier,
        string Registry,
        bool WorkerExists,
        bool WorkerMoved,
        bool UserReleased);

    private sealed record TranscriptSnapshot(
        string Barrier,
        string Registry,
        long TermAtMs,
        long KillAtMs,
        int TermToKillMs,
        int DeadlineMs,
        int PollMs,
        int WaitpidCalls,
        int WaitpidTarget,
        int NonChildWaitpidCalls,
        int IdentityPolls,
        bool PolledWorkerBroker,
        bool PolledWorker,
        bool PolledDescendant,
        bool HostAliveAfterTerm,
        int NonchildrenAliveAfterTerm,
        long CompletedAtMs,
        bool HostGroupGone,
        bool WorkerGroupGone,
        int Survivors,
        int Zombies);
}
