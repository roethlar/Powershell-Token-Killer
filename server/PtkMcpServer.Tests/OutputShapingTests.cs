namespace PtkMcpServer.Tests;

public sealed class OutputShapingTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60));

    public void Dispose() => _host.Dispose();

    [Fact]
    public void Compressor_module_loads_into_the_warm_runspace()
    {
        Assert.True(_host.ModuleLoaded);
    }

    [Fact]
    public async Task Object_output_is_compressed()
    {
        var result = await _host.InvokeAsync(
            "[pscustomobject]@{ Name = 'a'; Value = 1 }, [pscustomobject]@{ Name = 'b'; Value = 2 }");

        Assert.True(result.Success);
        Assert.StartsWith("objects: 2", result.Output.Trim());
    }

    [Fact]
    public async Task String_output_passes_through_untruncated()
    {
        var result = await _host.InvokeAsync("1..40 | ForEach-Object { \"line $_\" }");

        Assert.True(result.Success);
        Assert.Contains("line 40", result.Output);
        Assert.DoesNotContain("more", result.Output);
    }

    [Fact]
    public async Task Raw_skips_compression()
    {
        var result = await _host.InvokeAsync("[pscustomobject]@{ Name = 'a'; Value = 1 }", raw: true);

        Assert.True(result.Success);
        Assert.DoesNotContain("objects:", result.Output);
        Assert.Contains("Name", result.Output); // Out-String table header
    }

    [Fact]
    public async Task Missing_module_falls_back_to_plain_output()
    {
        using var host = new RunspaceHost(
            callTimeout: TimeSpan.FromSeconds(60),
            modulePathOverride: Path.Combine(Path.GetTempPath(), "no-such-module.psd1"));

        Assert.False(host.ModuleLoaded);

        var result = await host.InvokeAsync("[pscustomobject]@{ Name = 'a'; Value = 1 }");
        Assert.True(result.Success);
        Assert.DoesNotContain("objects:", result.Output);
        Assert.Contains("Name", result.Output);
    }

    [Fact]
    public async Task Reset_reimports_the_module_so_shaping_survives_recycles()
    {
        await _host.ResetAsync();

        Assert.True(_host.ModuleLoaded);

        var result = await _host.InvokeAsync(
            "[pscustomobject]@{ Name = 'a'; Value = 1 }, [pscustomobject]@{ Name = 'b'; Value = 2 }");
        Assert.StartsWith("objects: 2", result.Output.Trim());
    }
}
