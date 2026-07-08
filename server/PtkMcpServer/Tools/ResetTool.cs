using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class ResetTool
{
    [McpServerTool(Name = "ptk_reset")]
    [Description(
        "Recycle the warm runspace to factory state: discards all variables, loaded " +
        "modules, connections, and the current directory, and restores environment " +
        "variables to their server-start values (a PATH polluted by test shims comes " +
        "back clean). Use when leaked state is corrupting results; ptk_state shows " +
        "what has drifted.")]
    public static async Task<string> Reset(RunspaceHost host, CancellationToken cancellationToken = default)
    {
        await host.ResetAsync(cancellationToken);
        return "Runspace recycled; all warm state cleared.";
    }
}
