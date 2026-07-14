using System.ComponentModel;
using ModelContextProtocol.Server;
using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class ResetTool
{
    [McpServerTool(Name = "ptk_reset")]
    [Description(
        "Recycle the warm runspace to factory state: discards all variables, loaded " +
        "modules, connections, and the current directory, restores environment " +
        "variables to their server-start values (a PATH polluted by test shims comes " +
        "back clean), and kills running background jobs. Use when leaked state is " +
        "corrupting results; ptk_state shows what has drifted.")]
    public static Task<string> Reset(
        ISessionOperations runtime,
        CancellationToken cancellationToken = default,
        AuditCallContextAccessor? auditContext = null)
        => runtime.ResetAsync(
            cancellationToken,
            auditContext);
}
