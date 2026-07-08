using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class InvokeTool
{
    [McpServerTool(Name = "ptk_invoke")]
    [Description(
        "Run any shell command - PowerShell or native - in the server's persistent " +
        "warm runspace. Preferred over Bash/PowerShell tools for all shell work: " +
        "output arrives token-compressed by shape. Single native commands (git, npm, " +
        "docker, ...) route through rtk's per-command filters, PowerShell objects " +
        "become compact typed summaries, log-shaped text is deduplicated, plain text " +
        "passes through with terminal color codes stripped (oversized text is elided " +
        "with a labeled marker). Variables, imported modules, and established " +
        "connections persist across calls for the whole session, so heavy modules " +
        "import once instead of on every call. Set raw=true for full uncompressed " +
        "output. Calls run serially; a call that exceeds the server timeout is " +
        "aborted and the runspace is recycled, losing all warm state.")]
    public static async Task<string> Invoke(
        RunspaceHost host,
        JobManager jobs,
        [Description("The command to execute: a PowerShell script or a native command line (git, npm, ...).")] string script,
        CancellationToken cancellationToken,
        [Description("Skip output compression and return plain formatted text.")] bool raw = false,
        [Description(
            "Routing override: 'auto' (default) runs a single native command " +
            "through rtk's filters; 'pwsh' forces plain execution in the warm " +
            "runspace; 'rtk' forces the rtk rewrite when the script shape allows it.")]
        string route = "auto",
        [Description(
            "Run the script as a background job in a separate cold pwsh process and " +
            "return a job id immediately. Use for long stateless work (builds, " +
            "watchers, deploys) that could exceed the call timeout. The job does NOT " +
            "see warm session state (variables, modules, connections); poll it with " +
            "ptk_job.")]
        bool background = false,
        [Description(
            "Per-call timeout override in seconds, capped by the server maximum. Use " +
            "for long work that NEEDS the warm session (live connections, imported " +
            "modules); stateless long work should use background=true instead.")]
        int timeoutSeconds = 0)
    {
        if (background)
        {
            try
            {
                var cwd = await host.TryGetCurrentLocationAsync(cancellationToken);
                var job = jobs.Start(script, cwd);
                return $"[job {job.Id} started] pid {job.Pid}, cold process (no warm session state), log: {job.OutputPath}\n" +
                       $"Poll with ptk_job action=output id={job.Id} (then pass the returned next offset); " +
                       $"ptk_job action=status id={job.Id} for exit state.";
            }
            catch (Exception ex)
            {
                return $"[job start failed] {ex.Message}";
            }
        }

        route = route?.ToLowerInvariant() switch
        {
            "pwsh" => "pwsh",
            "rtk" => "rtk",
            _ => "auto",
        };
        var result = await host.InvokeAsync(script, raw, cancellationToken, route, timeoutSeconds);

        var sb = new StringBuilder();
        var output = result.Output.TrimEnd();
        sb.Append(output.Length > 0 ? output : "(no output)");

        if (result.ExitCode is int exitCode)
        {
            sb.AppendLine();
            sb.Append($"[exit] {exitCode}");
        }

        if (result.Errors.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[errors]");
            foreach (var error in result.Errors) sb.AppendLine(error);
        }

        if (result.Warnings.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[warnings]");
            foreach (var warning in result.Warnings) sb.AppendLine(warning);
        }

        return sb.ToString().TrimEnd();
    }
}
