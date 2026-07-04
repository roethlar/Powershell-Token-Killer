using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class InvokeTool
{
    [McpServerTool(Name = "ptk_invoke")]
    [Description(
        "Run a PowerShell script in the server's persistent warm runspace. Variables, " +
        "imported modules, and established connections persist across calls for the " +
        "whole session, so heavy modules import once instead of on every call. Output " +
        "is token-compressed by shape: objects become compact typed summaries, " +
        "log-shaped text is deduplicated, plain text passes through unchanged; set " +
        "raw=true when you need the full uncompressed output. Calls run serially; a " +
        "call that exceeds the server timeout is aborted and the runspace is " +
        "recycled, losing all warm state.")]
    public static async Task<string> Invoke(
        RunspaceHost host,
        [Description("The PowerShell script to execute.")] string script,
        CancellationToken cancellationToken,
        [Description("Skip output compression and return plain formatted text.")] bool raw = false,
        [Description(
            "Routing override: 'auto' (default) runs a single native command " +
            "through rtk's filters; 'pwsh' forces plain execution in the warm " +
            "runspace; 'rtk' forces the rtk rewrite when the script shape allows it.")]
        string route = "auto")
    {
        route = route?.ToLowerInvariant() switch
        {
            "pwsh" => "pwsh",
            "rtk" => "rtk",
            _ => "auto",
        };
        var result = await host.InvokeAsync(script, raw, cancellationToken, route);

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
