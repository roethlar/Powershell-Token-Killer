using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using PtkMcpServer.Sessions;
using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

// Deprecated raw compatibility telemetry: raw=true is inert but remains
// counted at the ptk_invoke user boundary and visible in ptk_state until the
// next breaking tool-schema revision removes it.
public sealed class RawUsageTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60));
    private readonly JobManager _jobs = new(
        Path.Combine(Path.GetTempPath(), "ptk-rawusage-jobs-" + Guid.NewGuid().ToString("N")));
    private readonly RawUsageCounter _rawUsage = new();
    private readonly string _outputRoot;
    private readonly OutputStore _outputStore;
    private readonly SessionRuntime _runtime;

    public RawUsageTests()
    {
        _outputRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "rawusage-output-tests",
            Guid.NewGuid().ToString("N"));
        _outputStore = new OutputStore(new OutputStoreOptions(
            _outputRoot,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1),
            MaximumArtifactBytes: 2 * 1024 * 1024,
            MaximumSessionBytes: 4 * 1024 * 1024,
            MaximumAggregateBytes: 8 * 1024 * 1024));
        _runtime = new SessionRuntime(_host, _jobs, _rawUsage);
    }

    public void Dispose()
    {
        _runtime.Dispose();
        _outputStore.Dispose();
        try { Directory.Delete(_outputRoot, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task User_raw_call_increments_exactly_once_and_surfaces_in_state()
    {
        // A permanently zero counter must fail this battery, not satisfy it
        // (plan Verification): the positive leg is exact — once per call.
        await _runtime.InvokeAsync("'first'", CancellationToken.None, raw: true);
        Assert.Equal(1, _rawUsage.Count);

        await _runtime.InvokeAsync("'second'", CancellationToken.None, raw: true);
        Assert.Equal(2, _rawUsage.Count);

        var state = await _runtime.StateAsync(listAvailable: false, CancellationToken.None);
        Assert.Contains("raw calls this session: 2", state);
    }

    [Fact]
    public async Task Raw_call_emits_the_server_log_line()
    {
        // Console.Error is process-global; the setter wraps the writer in a
        // synchronized TextWriter, and the assertion is Contains, so
        // unrelated concurrent stderr lines cannot break it.
        var original = Console.Error;
        var capture = new StringWriter();
        Console.SetError(capture);
        try
        {
            await _runtime.InvokeAsync("'logged'", CancellationToken.None, raw: true);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Contains("ptk: raw=true call #1 this session", capture.ToString());
    }

    [Fact]
    public async Task Internal_probes_never_inflate_the_raw_counter()
    {
        // State/cwd probes are internal implementation work, not compatibility
        // requests. Only an explicit user raw=true reaches this counter.
        await _runtime.StateAsync(listAvailable: false, CancellationToken.None);
        Assert.Equal(0, _rawUsage.Count);

        var text = await _runtime.InvokeAsync("'bg work'", CancellationToken.None, background: true);
        Assert.Contains("[job 1 started]", text);
        Assert.Equal(0, _rawUsage.Count);

        var nonRaw = await _runtime.InvokeAsync("'plain'", CancellationToken.None);
        Assert.Contains("plain", nonRaw);
        Assert.Equal(0, _rawUsage.Count);
    }

    [Fact]
    public async Task Oversized_job_poll_uses_one_stable_path_free_ptk_output_handle()
    {
        const string middleToken = "JOB_HANDLE_ELIDED_MIDDLE_1500";
        var started = await _runtime.InvokeAsync("1..3000 | ForEach-Object { " +
            "if ($_ -eq 1500) { 'JOB_HANDLE_ELIDED_MIDDLE_1500' } " +
            "else { \"job line $_ \" + ('z' * 20) } }",
            CancellationToken.None,
            background: true,
            outputStore: _outputStore);
        Assert.Contains("[job 1 started]", started);
        Assert.Contains("recovery=pending", started, StringComparison.Ordinal);
        Assert.Contains("ptk_output handle", started, StringComparison.Ordinal);
        Assert.Contains("recovery-unavailable", started, StringComparison.Ordinal);
        Assert.DoesNotContain("ptko_", started, StringComparison.Ordinal);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        var status = string.Empty;
        do
        {
            await Task.Delay(250);
            status = await _runtime.JobAsync("status", CancellationToken.None, id: 1);
        } while ((status.Contains("running", StringComparison.OrdinalIgnoreCase) ||
                  !ContainsOutputHandle(status)) &&
                 DateTime.UtcNow < deadline);

        var poll = await _runtime.JobAsync("output", CancellationToken.None, id: 1, offset: 0);
        var list = await _runtime.JobAsync("list", CancellationToken.None);
        var snapshot = Assert.IsType<JobSnapshot>(_jobs.Snapshot(1));
        Assert.False(snapshot.Running);

        // The marker and every discovery surface publish the same opaque
        // supervisor-owned capability, never the job spool's filesystem path.
        var handle = AssertSingleOutputHandle(poll);
        Assert.Equal(handle, AssertSingleOutputHandle(status));
        Assert.Equal(handle, AssertSingleOutputHandle(list));
        Assert.Contains(
            $"lines elided - recovery=handle: ptk_output handle={handle}",
            poll,
            StringComparison.Ordinal);
        foreach (var response in new[] { poll, status, list })
        {
            Assert.Contains("recovery=handle", response, StringComparison.Ordinal);
            Assert.Contains("ptk_output reports current availability", response, StringComparison.Ordinal);
            Assert.DoesNotContain("recovery=available", response, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(middleToken, poll, StringComparison.Ordinal);
        Assert.DoesNotContain("rerun with raw=true", poll);

        var recovered = OutputTool.Output(
            _outputStore,
            handle,
            maxBytes: OutputStore.MaximumReadBytes);
        Assert.Contains(middleToken, recovered, StringComparison.Ordinal);

        var jobsRoot = Assert.IsType<string>(Path.GetDirectoryName(snapshot.OutputPath));
        foreach (var response in new[] { started, poll, status, list, recovered })
        {
            Assert.DoesNotContain(snapshot.OutputPath, response, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(jobsRoot, response, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_outputRoot, response, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("log:", response, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Job_poll_spool_failure_is_path_free_and_never_reruns_the_job()
    {
        var executionMarker = Path.Combine(
            Path.GetTempPath(),
            $"ptk-job-poll-once-{Guid.NewGuid():N}.txt");
        var escapedMarker = executionMarker.Replace("'", "''", StringComparison.Ordinal);
        try
        {
            var started = await _runtime.InvokeAsync($"Add-Content -LiteralPath '{escapedMarker}' -Value x; 'POLLABLE'",
                CancellationToken.None,
                background: true,
                outputStore: _outputStore);
            Assert.Contains("[job 1 started]", started, StringComparison.Ordinal);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            JobSnapshot snapshot;
            do
            {
                await Task.Delay(25);
                snapshot = Assert.IsType<JobSnapshot>(_jobs.Snapshot(1));
            } while (snapshot.Running && DateTimeOffset.UtcNow < deadline);
            Assert.False(snapshot.Running);
            var handle = Assert.IsType<string>(snapshot.OutputRecovery?.Handle);

            _jobs.BeforePollingOutputReadForTests = _ =>
                throw new IOException($"injected read failure at {snapshot.OutputPath}");
            var response = await _runtime.JobAsync("output",
                CancellationToken.None,
                id: 1,
                offset: 17);

            Assert.Contains("output unavailable", response, StringComparison.Ordinal);
            Assert.Contains("command was not rerun", response, StringComparison.Ordinal);
            Assert.Contains(handle, response, StringComparison.Ordinal);
            Assert.Contains("next offset: 17", response, StringComparison.Ordinal);
            Assert.DoesNotContain(snapshot.OutputPath, response, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("injected read failure", response, StringComparison.Ordinal);
            Assert.Equal(["x"], File.ReadAllLines(executionMarker));
        }
        finally
        {
            _jobs.BeforePollingOutputReadForTests = null;
            try { File.Delete(executionMarker); } catch { }
        }
    }

    [Fact]
    public async Task Job_printed_marker_text_gets_no_false_recovery_note()
    {
        // sd3-3: a job whose OWN output contains marker text (a grep over
        // this repo's source, a cat of previously elided output — plain or
        // ANSI-colored, the tail that broke the length heuristic) must not
        // yield job-context recovery advice on an under-limit poll: the
        // module composes that advice only when IT elides.
        var started = await _runtime.InvokeAsync("Write-Output '[5 lines elided - rerun with raw=true only if the elided middle matters]'\n" +
            "Write-Output \"$([char]27)[31m[6 lines elided - rerun with raw=true only if the elided middle matters]$([char]27)[0m\"",
            CancellationToken.None, background: true);
        Assert.Contains("[job 1 started]", started);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        string status;
        do
        {
            await Task.Delay(250);
            status = await _runtime.JobAsync("status", CancellationToken.None, id: 1);
        } while (status.Contains("running") && DateTime.UtcNow < deadline);

        var poll = await _runtime.JobAsync("output", CancellationToken.None, id: 1, offset: 0);

        Assert.Contains("elided", poll); // the job's own text came through
        // "captured log" catches any form of fabricated log-recovery advice —
        // the current hint phrasing and the earlier appended-note phrasing
        // alike; the job's own printed text contains neither.
        Assert.DoesNotContain("captured log", poll);
    }

    private static string DescriptionOf(ICustomAttributeProvider member) =>
        ((DescriptionAttribute)member.GetCustomAttributes(typeof(DescriptionAttribute), false).Single()).Description;

    private static bool ContainsOutputHandle(string response) =>
        Regex.IsMatch(response, @"ptko_[A-Za-z0-9_-]+");

    private static string AssertSingleOutputHandle(string response) =>
        Assert.Single(
            Regex.Matches(response, @"ptko_[A-Za-z0-9_-]+")
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal));

    [Fact]
    public void Invoke_descriptions_teach_same_invocation_recovery_and_inert_legacy_raw()
    {
        var invoke = typeof(InvokeTool).GetMethod(nameof(InvokeTool.Invoke))!;
        var tool = DescriptionOf(invoke);
        Assert.Contains("Output is normally shaped", tool);
        Assert.Contains("ptk_output handle", tool);
        Assert.Contains("same invocation", tool);
        Assert.Contains("never reruns", tool);
        Assert.Contains("legacy raw is deprecated", tool);
        Assert.Contains("does not change interpreter, routing, capture, or shaping", tool);
        Assert.DoesNotContain("Recovery hatch", tool);
        Assert.DoesNotContain("route=pwsh with raw=false", tool);
        Assert.DoesNotContain("full uncompressed output", tool);
        Assert.Contains("outside the runspace", tool);
        Assert.Contains("delegated Bash state is process-local", tool);
        Assert.Contains("preserves warm state", tool);
        Assert.DoesNotContain(
            "only a call that overruns while executing is aborted with the runspace recycled",
            tool);

        var rawParam = invoke.GetParameters().Single(p => p.Name == "raw");
        var raw = ((DescriptionAttribute)rawParam.GetCustomAttributes(typeof(DescriptionAttribute), false).Single()).Description;
        Assert.Equal(typeof(bool), rawParam.ParameterType);
        Assert.True(rawParam.HasDefaultValue);
        Assert.Equal(false, rawParam.DefaultValue);
        Assert.Contains("Deprecated compatibility flag", raw);
        Assert.Contains(
            "no effect on dialect handling, interpreter/routing, process choice, capture, or shaping",
            raw);
        Assert.Contains("Use ptk_output when a handle is returned", raw);
        Assert.DoesNotContain("Recovery hatch", raw);
        Assert.DoesNotContain("route=pwsh with raw=false", raw);
        Assert.DoesNotContain("full uncompressed output", raw);

        var routeParam = invoke.GetParameters().Single(p => p.Name == "route");
        var route = DescriptionOf(routeParam);
        Assert.Contains(
            "explicit consent to interpret the exact original text as PowerShell",
            route);
        Assert.Contains("normal capture and shaping still apply", route);
        Assert.DoesNotContain("raw=false", route);

        var backgroundParam = invoke.GetParameters().Single(p => p.Name == "background");
        var background = DescriptionOf(backgroundParam);
        Assert.Contains("separate cold child process", background, StringComparison.Ordinal);
        Assert.Contains("direct PowerShell or eligible RTK routing", background, StringComparison.Ordinal);
        Assert.DoesNotContain("cold pwsh process", background, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Session_tool_adapters_depend_on_one_runtime_not_execution_components()
    {
        var methods = new[]
        {
            typeof(InvokeTool).GetMethod(nameof(InvokeTool.Invoke))!,
            typeof(JobTool).GetMethod(nameof(JobTool.Job))!,
            typeof(StateTool).GetMethod(nameof(StateTool.State))!,
            typeof(ResetTool).GetMethod(nameof(ResetTool.Reset))!,
        };

        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            Assert.Single(parameters, parameter => parameter.ParameterType == typeof(ISessionOperations));
            Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(SessionRuntime));
            Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(RunspaceHost));
            Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(JobManager));
            Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(RawUsageCounter));
        }

        Assert.Single(
            methods[0].GetParameters(),
            parameter => parameter.ParameterType == typeof(OutputStore));
        foreach (var method in methods.Skip(1))
        {
            Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.ParameterType == typeof(OutputStore));
        }

        Assert.Equal(
            "runtime,script,cancellationToken,raw,route,background,timeoutSeconds,auditContext,outputStore",
            string.Join(',', methods[0].GetParameters().Select(parameter => parameter.Name)));
        Assert.Equal(
            "runtime,action,cancellationToken,id,offset,auditContext",
            string.Join(',', methods[1].GetParameters().Select(parameter => parameter.Name)));
        Assert.Equal(
            "runtime,listAvailable,cancellationToken,auditContext",
            string.Join(',', methods[2].GetParameters().Select(parameter => parameter.Name)));
        Assert.Equal(
            "runtime,cancellationToken,auditContext",
            string.Join(',', methods[3].GetParameters().Select(parameter => parameter.Name)));

        Assert.Equal(false, Parameter(methods[0], "raw").DefaultValue);
        Assert.Equal("auto", Parameter(methods[0], "route").DefaultValue);
        Assert.Equal(false, Parameter(methods[0], "background").DefaultValue);
        Assert.Equal(0, Parameter(methods[0], "timeoutSeconds").DefaultValue);
        Assert.Equal(0L, Parameter(methods[1], "id").DefaultValue);
        Assert.Equal(0L, Parameter(methods[1], "offset").DefaultValue);
        Assert.Equal(false, Parameter(methods[2], "listAvailable").DefaultValue);

        static ParameterInfo Parameter(MethodInfo method, string name) =>
            method.GetParameters().Single(parameter => parameter.Name == name);
    }

    [Fact]
    public void Job_and_output_descriptions_teach_path_free_background_recovery()
    {
        var job = DescriptionOf(typeof(JobTool).GetMethod(nameof(JobTool.Job))!);
        Assert.Contains("path-free output recovery", job, StringComparison.Ordinal);
        Assert.Contains("ptk_output snapshot", job, StringComparison.Ordinal);
        Assert.Contains("seam-absent RTK", job, StringComparison.Ordinal);
        Assert.Contains("failures are reported path-free", job, StringComparison.Ordinal);
        Assert.DoesNotContain("polled output remains available", job, StringComparison.Ordinal);
        Assert.DoesNotContain("log path", job, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("log file", job, StringComparison.OrdinalIgnoreCase);

        var output = DescriptionOf(typeof(OutputTool).GetMethod(nameof(OutputTool.Output))!);
        Assert.Contains("finalized direct background job", output, StringComparison.Ordinal);
        Assert.Contains("without executing or rerunning", output, StringComparison.Ordinal);
        var handle = typeof(OutputTool).GetMethod(nameof(OutputTool.Output))!
            .GetParameters()
            .Single(parameter => parameter.Name == "handle");
        Assert.Contains("ptk_job", DescriptionOf(handle), StringComparison.Ordinal);
    }
}
