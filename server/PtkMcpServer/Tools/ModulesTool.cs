using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class ModulesTool
{
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static string? _availableCache;

    [McpServerTool(Name = "ptk_modules")]
    [Description(
        "List PowerShell modules in the warm runspace. Default lists currently loaded " +
        "modules (cheap). Set listAvailable to also enumerate every installed module — " +
        "slow the first time, cached for the rest of the session.")]
    public static async Task<string> Modules(
        RunspaceHost host,
        [Description("Enumerate all installed modules instead of only loaded ones.")]
        bool listAvailable = false,
        CancellationToken cancellationToken = default)
    {
        if (!listAvailable)
        {
            var loaded = await host.InvokeAsync(
                "Get-Module | Sort-Object Name | ForEach-Object { '{0} {1}' -f $_.Name, $_.Version }",
                raw: true, // this tool formats its own lines; never shape them
                cancellationToken: cancellationToken);
            var text = loaded.Output.TrimEnd();
            return text.Length > 0 ? text : "(no modules loaded)";
        }

        await CacheGate.WaitAsync(cancellationToken);
        try
        {
            if (_availableCache is null)
            {
                var available = await host.InvokeAsync(
                    "Get-Module -ListAvailable | Sort-Object Name -Unique | " +
                    "ForEach-Object { '{0} {1}' -f $_.Name, $_.Version }",
                    raw: true, // this tool formats its own lines; never shape them
                    cancellationToken: cancellationToken);
                _availableCache = available.Output.TrimEnd();
            }
            return _availableCache.Length > 0 ? _availableCache : "(no modules available)";
        }
        finally
        {
            CacheGate.Release();
        }
    }
}
