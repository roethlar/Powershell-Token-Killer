using System.ComponentModel;
using ModelContextProtocol.Server;
using PtkMcpServer.Audit;

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
    public static async Task<string> Reset(
        RunspaceHost host,
        JobManager jobs,
        CancellationToken cancellationToken = default,
        AuditCallContextAccessor? auditContext = null)
    {
        var audit = auditContext?.Current;
        if (audit is not null && !audit.AuthorizeControl("reset.requested"))
            return AuditCallContext.NotStartedMessage;

        using var jobReset = jobs.BeginReset();
        var killed = jobReset.KilledCount;
        try
        {
            await host.ResetAsync(cancellationToken);
            audit?.RecordControlOutcome("runspace.recycled", "completed", warmStateLost: true);
        }
        catch
        {
            audit?.RecordControlOutcome(
                "runspace.recycled",
                "outcome_unknown",
                detailCode: "reset_failed",
                warmStateLost: true,
                terminationCertainty: "unknown");
            throw;
        }
        return killed > 0
            ? $"Runspace recycled; all warm state cleared, environment restored, {killed} background job(s) killed."
            : "Runspace recycled; all warm state cleared and environment restored.";
    }
}
