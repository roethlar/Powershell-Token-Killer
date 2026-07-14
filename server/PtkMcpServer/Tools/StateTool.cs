using System.ComponentModel;
using ModelContextProtocol.Server;
using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class StateTool
{
    [McpServerTool(Name = "ptk_state")]
    [Description(
        "Session introspection and health check for the ptk warm runspace: engine, " +
        "server PID and uptime, current directory, loaded modules, running " +
        "background jobs, and DRIFT - what this session has changed since server " +
        "start (environment variables, PATH as an entry diff, session variable " +
        "count). Use it to diagnose a polluted session (e.g. a test shim left on " +
        "PATH) before it corrupts results; ptk_reset restores factory state. Set " +
        "listAvailable to also enumerate installed modules - slow the first time, " +
        "cached for the rest of the session. Always answers promptly: while " +
        "another call holds the runspace it reports host-level facts plus a " +
        "busy line (active-call age, waiter count) instead of queueing, and " +
        "marks runspace-dependent details unavailable.")]
    public static Task<string> State(
        ISessionOperations runtime,
        [Description("Also enumerate every installed module instead of only loaded ones.")]
        bool listAvailable = false,
        CancellationToken cancellationToken = default,
        AuditCallContextAccessor? auditContext = null)
        => runtime.StateAsync(
            listAvailable,
            cancellationToken,
            auditContext);
}
