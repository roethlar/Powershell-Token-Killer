using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

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
    public async Task Timeout_is_reported_with_the_state_loss_warning()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(2));

        var text = await InvokeTool.Invoke(host, "Start-Sleep -Seconds 60", CancellationToken.None);

        Assert.Contains("[errors]", text);
        Assert.Contains("timeout", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recycled", text);
    }
}
