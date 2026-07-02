using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

public sealed class ModulesAndResetToolTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60));

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Loaded_list_grows_after_an_import()
    {
        var before = await ModulesTool.Modules(_host, listAvailable: false, CancellationToken.None);
        Assert.DoesNotContain("PtkWarmTest", before);

        await _host.InvokeAsync(
            "New-Module -Name PtkWarmTest -ScriptBlock { function Get-Warm { 'warm' } } | Import-Module");

        var after = await ModulesTool.Modules(_host, listAvailable: false, CancellationToken.None);
        Assert.Contains("PtkWarmTest", after);
    }

    [Fact]
    public async Task Available_list_contains_shipped_modules()
    {
        var available = await ModulesTool.Modules(_host, listAvailable: true, CancellationToken.None);

        Assert.Contains("Microsoft.PowerShell.Utility", available);
    }

    [Fact]
    public async Task Reset_clears_variables_and_loaded_modules()
    {
        await _host.InvokeAsync("$x = 'precious'");
        await _host.InvokeAsync(
            "New-Module -Name PtkWarmTest -ScriptBlock { function Get-Warm { 'warm' } } | Import-Module");

        var message = await ResetTool.Reset(_host, CancellationToken.None);
        Assert.Contains("recycled", message);

        var variable = await _host.InvokeAsync("if ($null -eq $x) { 'gone' } else { $x }");
        Assert.Equal("gone", variable.Output.Trim());

        var modules = await ModulesTool.Modules(_host, listAvailable: false, CancellationToken.None);
        Assert.DoesNotContain("PtkWarmTest", modules);
    }
}
