using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class ResetTool
{
    [McpServerTool(Name = "ptk_reset")]
    [Description(
        "Recycle the warm runspace: discards all variables, loaded modules, and " +
        "connections, and starts fresh. Use when leaked state (globals, cwd, " +
        "PSDefaultParameterValues) is corrupting results.")]
    public static async Task<string> Reset(RunspaceHost host, CancellationToken cancellationToken = default)
    {
        await host.ResetAsync(cancellationToken);
        return "Runspace recycled; all warm state cleared.";
    }
}
