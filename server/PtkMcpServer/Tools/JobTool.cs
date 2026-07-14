using System.ComponentModel;
using ModelContextProtocol.Server;
using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class JobTool
{
    [McpServerTool(Name = "ptk_job")]
    [Description(
        "Manage background jobs started by ptk_invoke background=true. " +
        "action=output returns new output since the given offset, shaped and " +
        "bounded, and ends with the next offset to pass on the following poll; " +
        "action=status reports run state, exit code, and path-free output recovery; kill stops " +
        "a job (and its process tree); list shows all jobs. Jobs are cold child " +
        "processes with no warm session state. After termination PTK attempts to " +
        "seal direct job output into a supervisor-owned ptk_output snapshot. " +
        "Recovery sealing is separate from the internal polling spool: failures " +
        "are reported path-free, and seam-absent RTK jobs have no raw recovery. Jobs are " +
        "killed by ptk_reset and at server shutdown.")]
    public static Task<string> Job(
        ISessionOperations runtime,
        [Description("status | output | kill | list")] string action,
        CancellationToken cancellationToken,
        [Description("Job id (required for status/output/kill).")] long id = 0,
        [Description("Byte offset for action=output: pass the previous poll's 'next offset'; 0 reads from the start.")] long offset = 0,
        AuditCallContextAccessor? auditContext = null)
        => runtime.JobAsync(
            action,
            cancellationToken,
            id,
            offset,
            auditContext);
}
