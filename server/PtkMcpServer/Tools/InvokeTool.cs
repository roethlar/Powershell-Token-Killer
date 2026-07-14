using System.ComponentModel;
using ModelContextProtocol.Server;
using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class InvokeTool
{
    [McpServerTool(Name = "ptk_invoke")]
    [Description(
        "Run shell work through PTK. PowerShell, mixed-dataflow, and most native " +
        "commands use the persistent warm runspace; eligible terminal native commands " +
        "route internally through rtk, and independently proven parse-fatal Bash syntax " +
        "may execute through startup-pinned Bash/RTK processes outside the runspace. " +
        "Preferred over Bash/PowerShell tools for shell work: output arrives " +
        "token-compressed by shape. PowerShell objects " +
        "become compact typed summaries, log-shaped text is deduplicated, plain text " +
        "passes through with terminal color codes stripped (oversized text is elided " +
        "with a labeled marker). PowerShell variables, imported modules, and established " +
        "connections persist across runspace-routed calls; delegated Bash state is " +
        "process-local. Output is normally shaped and preserves errors, exit codes, " +
        "and structure. When this response or a later ptk_job response includes a " +
        "ptk_output handle, use ptk_output to read the captured unshaped snapshot " +
        "from that same invocation; PTK never " +
        "reruns the command for recovery. The legacy raw is deprecated and accepted " +
        "only for compatibility; it does not change interpreter, routing, capture, or " +
        "shaping. Calls run serially, and the timeout " +
        "is a total wall-clock budget covering queue wait plus execution: a call " +
        "still waiting when its budget expires fails fast WITHOUT executing (warm " +
        "state intact - just retry or go background). A PowerShell execution overrun " +
        "recycles the runspace and loses warm state; a delegated Bash/RTK overrun " +
        "attempts bounded tracked-root termination, preserves warm state, and reports " +
        "descendant and remote outcomes as unknown without retrying.")]
    public static Task<string> Invoke(
        ISessionOperations runtime,
        [Description("The command to execute: a PowerShell script or a native command line (git, npm, ...).")] string script,
        CancellationToken cancellationToken,
        [Description(
            "Deprecated compatibility flag: true has no effect on dialect handling, " +
            "interpreter/routing, process choice, capture, or shaping. Use ptk_output " +
            "when a handle is returned.")]
        bool raw = false,
        [Description(
            "Routing override: 'auto' (default) runs a single native command " +
            "through rtk's filters; 'pwsh' is explicit consent to interpret the exact " +
            "original text as PowerShell and bypass automatic dialect/Bash/RTK routing; " +
            "normal capture and shaping still apply; 'rtk' asserts RTK only for an " +
            "eligible terminal native application. An ineligible assertion executes the exact original " +
            "once and returns a labeled effective route without asking for a retry.")]
        string route = "auto",
        [Description(
            "Run the script as a background job in a separate cold child process and " +
            "return a job id immediately. The cold execution plan selects direct " +
            "PowerShell or eligible RTK routing. Use for long stateless work (builds, " +
            "watchers, deploys) that could exceed the call timeout. The job does NOT " +
            "see warm session state (variables, modules, connections); poll it with ptk_job.")]
        bool background = false,
        [Description(
            "Per-call timeout override in seconds, capped by the server maximum. A " +
            "total wall-clock budget: queue wait behind another call counts against " +
            "it, and a call whose budget expires while still queued fails fast " +
            "without executing (warm state intact). Use for long work that NEEDS " +
            "the warm session (live connections, imported modules); stateless long " +
            "work should use background=true instead.")]
        int timeoutSeconds = 0,
        AuditCallContextAccessor? auditContext = null,
        OutputStore? outputStore = null)
        => runtime.InvokeAsync(
            script,
            cancellationToken,
            raw,
            route,
            background,
            timeoutSeconds,
            auditContext,
            outputStore);
}
