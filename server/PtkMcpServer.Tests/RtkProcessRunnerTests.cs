using System.Collections.Immutable;

namespace PtkMcpServer.Tests;

public sealed class RtkProcessRunnerTests
{
    [Fact]
    public void Start_info_uses_only_the_audited_identity_cwd_and_argument_vector()
    {
        var rtkPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trusted", "rtk"));
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var dispatch = Dispatch(
            new RtkExecutableIdentity(rtkPath),
            cwd,
            ["git", "", "hello world", "001", "-x:joined value"]);

        var startInfo = RtkProcessRunner.CreateStartInfo(dispatch);

        Assert.Equal(rtkPath, startInfo.FileName);
        Assert.Equal(cwd, startInfo.WorkingDirectory);
        Assert.Equal(
            ["git", "", "hello world", "001", "-x:joined value"],
            startInfo.ArgumentList.ToArray());
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public async Task Direct_capture_preserves_stdout_stderr_and_nonzero_exit()
    {
        var executable = ResolveCommandProcessor();
        var identity = RtkExecutableIdentity.TryCapture(executable);
        Assert.NotNull(identity);
        var arguments = OperatingSystem.IsWindows()
            ? new[]
            {
                "/d", "/s", "/c",
                "echo DIRECT_STDOUT&echo DIRECT_STDERR>&2&exit /b 7",
            }
            : new[]
            {
                "-c",
                "printf '%s\\n' DIRECT_STDOUT; printf '%s\\n' DIRECT_STDERR 1>&2; exit 7",
            };
        var dispatch = Dispatch(
            identity,
            Path.GetFullPath(Path.GetTempPath()),
            [.. arguments]);

        var result = await RtkProcessRunner.ExecuteAsync(
            dispatch,
            DateTimeOffset.UtcNow.AddSeconds(30),
            CancellationToken.None);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(InvokeDisposition.Completed, result.Disposition);
        Assert.Contains("DIRECT_STDOUT", result.Output);
        Assert.NotNull(result.Stderr);
        Assert.Contains("DIRECT_STDERR", result.Stderr);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task Deadline_stops_the_started_root_without_retrying_it()
    {
        var dir = Directory.CreateTempSubdirectory("ptk-rtk-timeout-");
        var marker = Path.Combine(dir.FullName, "starts.txt");
        try
        {
            var executable = ResolveCommandProcessor();
            var identity = RtkExecutableIdentity.TryCapture(executable);
            Assert.NotNull(identity);
            var arguments = OperatingSystem.IsWindows()
                ? new[]
                {
                    "/d", "/s", "/c",
                    "echo x>>starts.txt&ping -n 30 127.0.0.1>nul",
                }
                : new[]
                {
                    "-c",
                    "printf 'x\\n' >> \"$1\"; sleep 30",
                    "sh",
                    marker,
                };
            var dispatch = Dispatch(identity, dir.FullName, [.. arguments]);
            var executionBudget = OperatingSystem.IsWindows()
                ? TimeSpan.FromSeconds(5)
                : TimeSpan.FromMilliseconds(500);

            var result = await RtkProcessRunner.ExecuteAsync(
                dispatch,
                DateTimeOffset.UtcNow.Add(executionBudget),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(result.TimedOut);
            Assert.True(result.UserExecutionStarted);
            Assert.Equal(InvokeDisposition.OutcomeUnknown, result.Disposition);
            Assert.Equal(["x"], File.ReadAllLines(marker));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static ExecutionDispatch Dispatch(
        RtkExecutableIdentity identity,
        string workingDirectory,
        ImmutableArray<string> arguments)
    {
        var plan = new ExecutionPlan(
            originalScript: "fixture command",
            executionScript: null,
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            [ExecutionPath.PowerShellDirect],
            fallbackReason: null,
            identity,
            workingDirectory: workingDirectory,
            rtkArgumentVector: arguments,
            directFallbackProvenance: OutputProvenance.PowerShellObjects);
        return ExecutionDispatch.FromPlan(plan);
    }

    private static string ResolveCommandProcessor() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe")
            : File.Exists("/bin/sh")
                ? "/bin/sh"
                : "/usr/bin/sh";
}
