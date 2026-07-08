using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

// ProcessEnvironment collection: mutates PTK_RTK_PATH, which a parallel
// reset-driven environment restore would otherwise wipe mid-test.
[Collection("ProcessEnvironment")]
public sealed class InvokeToolTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60));

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Returns_plain_output_for_a_clean_call()
    {
        var text = await InvokeTool.Invoke(_host, "'hello from warm runspace'", CancellationToken.None);

        Assert.Contains("hello from warm runspace", text);
        Assert.DoesNotContain("[errors]", text);
        Assert.DoesNotContain("[warnings]", text);
    }

    [Fact]
    public async Task State_persists_across_tool_calls()
    {
        await InvokeTool.Invoke(_host, "$warm = 41", CancellationToken.None);
        var text = await InvokeTool.Invoke(_host, "$warm + 1", CancellationToken.None);

        Assert.Contains("42", text);
    }

    [Fact]
    public async Task Errors_and_warnings_are_reported_in_labelled_sections()
    {
        var text = await InvokeTool.Invoke(
            _host, "Write-Warning 'careful'; Write-Error 'boom'; 'partial'", CancellationToken.None);

        Assert.Contains("partial", text);
        Assert.Contains("[errors]", text);
        Assert.Contains("boom", text);
        Assert.Contains("[warnings]", text);
        Assert.Contains("careful", text);
    }

    [Fact]
    public async Task Empty_output_says_so_instead_of_returning_nothing()
    {
        var text = await InvokeTool.Invoke(_host, "$null", CancellationToken.None);

        Assert.Contains("(no output)", text);
    }

    private static string NativeExit(int code) =>
        OperatingSystem.IsWindows() ? $"cmd /c exit {code}" : $"sh -c 'exit {code}'";

    [Fact]
    public async Task Native_nonzero_exit_code_is_reported()
    {
        var text = await InvokeTool.Invoke(_host, NativeExit(7), CancellationToken.None);

        Assert.Contains("[exit] 7", text);
    }

    [Fact]
    public async Task Native_zero_exit_code_is_not_reported()
    {
        var text = await InvokeTool.Invoke(_host, NativeExit(0), CancellationToken.None);

        Assert.DoesNotContain("[exit]", text);
    }

    [Fact]
    public async Task Stale_exit_code_is_not_reported_against_a_later_pure_PowerShell_call()
    {
        await InvokeTool.Invoke(_host, NativeExit(7), CancellationToken.None);
        var text = await InvokeTool.Invoke(_host, "'clean call'", CancellationToken.None);

        Assert.Contains("clean call", text);
        Assert.DoesNotContain("[exit]", text);
    }

    [Fact]
    public async Task Native_exit_code_survives_rtk_log_shaping()
    {
        // Round-2 review repro: log-shaped output routes through the module's
        // native rtk leg, whose own exit code must not replace the script's.
        var stubDir = Directory.CreateTempSubdirectory("ptk-rtk-stub-");
        string stubPath;
        if (OperatingSystem.IsWindows())
        {
            stubPath = Path.Combine(stubDir.FullName, "rtk-stub.cmd");
            File.WriteAllText(stubPath, "@echo off\r\necho RTKSTUB shaped\r\nexit /b 0\r\n");
        }
        else
        {
            stubPath = Path.Combine(stubDir.FullName, "rtk-stub.sh");
            File.WriteAllText(stubPath, "#!/bin/sh\necho 'RTKSTUB shaped'\nexit 0\n");
            File.SetUnixFileMode(stubPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stubPath);
            var script =
                "1..8 | ForEach-Object { \"2026-07-03 10:00:0$_ ERROR worker: step $_ failed\" }; "
                + NativeExit(7);
            var text = await InvokeTool.Invoke(_host, script, CancellationToken.None);

            Assert.Contains("[ptk:log via rtk]", text);
            Assert.Contains("[exit] 7", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            stubDir.Delete(recursive: true);
        }
    }

    private static (DirectoryInfo dir, string path) CreateRtkStub(string body)
    {
        var dir = Directory.CreateTempSubdirectory("ptk-rtk-route-");
        string path;
        if (OperatingSystem.IsWindows())
        {
            path = Path.Combine(dir.FullName, "rtk-stub.cmd");
            File.WriteAllText(path, "@echo off\r\n" + body.Replace("\n", "\r\n") + "\r\n");
        }
        else
        {
            path = Path.Combine(dir.FullName, "rtk-stub.sh");
            File.WriteAllText(path,
                "#!/bin/sh\n" + body.Replace("%*", "\"$@\"").Replace("exit /b ", "exit ") + "\n");
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return (dir, path);
    }

    [Fact]
    public async Task Single_native_command_routes_through_rtk()
    {
        var (dir, stub) = CreateRtkStub("echo RTKROUTE %*\nexit /b 0");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var text = await InvokeTool.Invoke(_host, "git status", CancellationToken.None);

            Assert.Contains("RTKROUTE git status", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Route_pwsh_forces_plain_execution()
    {
        var (dir, stub) = CreateRtkStub("echo RTKROUTE %*\nexit /b 0");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var text = await InvokeTool.Invoke(
                _host, "git --version", CancellationToken.None, route: "pwsh");

            Assert.DoesNotContain("RTKROUTE", text);
            Assert.Contains("git version", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Routed_command_exit_code_is_reported()
    {
        var (dir, stub) = CreateRtkStub("echo RTKROUTE %*\nexit /b 5");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var text = await InvokeTool.Invoke(_host, "git status", CancellationToken.None);

            Assert.Contains("RTKROUTE git status", text);
            Assert.Contains("[exit] 5", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Timeout_is_reported_with_the_state_loss_warning()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(2));

        var text = await InvokeTool.Invoke(host, "Start-Sleep -Seconds 60", CancellationToken.None);

        Assert.Contains("[errors]", text);
        Assert.Contains("timeout", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recycled", text);
    }
}
