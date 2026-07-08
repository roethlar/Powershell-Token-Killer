using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class StateTool
{
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static string? _availableCache;

    [McpServerTool(Name = "ptk_state")]
    [Description(
        "Session introspection and health check for the ptk warm runspace: engine, " +
        "server PID and uptime, current directory, loaded modules, and DRIFT - what " +
        "this session has changed since server start (environment variables, PATH as " +
        "an entry diff, session variable count). Use it to diagnose a polluted " +
        "session (e.g. a test shim left on PATH) before it corrupts results; " +
        "ptk_reset restores factory state. Set listAvailable to also enumerate " +
        "installed modules - slow the first time, cached for the rest of the session.")]
    public static async Task<string> State(
        RunspaceHost host,
        [Description("Also enumerate every installed module instead of only loaded ones.")]
        bool listAvailable = false,
        CancellationToken cancellationToken = default)
    {
        // No assignments in this script: probing the session must not add
        // variables to it (the report would perturb its own drift numbers).
        var script = string.Join('\n',
            "\"engine: $($PSVersionTable.PSVersion)\"",
            "\"cwd: $((Get-Location).Path)\"",
            $"\"variables: $(@(Get-Variable).Count) (baseline {host.BaselineVariableCount})\"",
            "$(if (@(Get-Module).Count -eq 0) { 'modules loaded: (none)' } else { 'modules loaded:' })",
            "Get-Module | Sort-Object Name | ForEach-Object { '  ' + $_.Name + ' ' + $_.Version }");
        var result = await host.InvokeAsync(
            script,
            raw: true, // this tool formats its own lines; never shape them
            cancellationToken: cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine(
            $"ptk server: pid {Environment.ProcessId}, up {FormatUptime(DateTimeOffset.UtcNow - host.StartedUtc)}, " +
            $"shaping {(host.ModuleLoaded ? "on" : "off")}");
        sb.AppendLine(result.Output.TrimEnd());

        var drift = host.GetEnvironmentDrift();
        sb.AppendLine("[env drift since server start]");
        if (drift.IsEmpty)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            if (drift.Added.Length > 0) sb.AppendLine("added: " + string.Join(", ", drift.Added));
            if (drift.Modified.Length > 0) sb.AppendLine("modified: " + string.Join(", ", drift.Modified));
            if (drift.Removed.Length > 0) sb.AppendLine("removed: " + string.Join(", ", drift.Removed));
            if (drift.PathEntriesAdded.Length > 0) sb.AppendLine("PATH entries added: " + string.Join("; ", drift.PathEntriesAdded));
            if (drift.PathEntriesRemoved.Length > 0) sb.AppendLine("PATH entries removed: " + string.Join("; ", drift.PathEntriesRemoved));
        }

        if (listAvailable)
        {
            await CacheGate.WaitAsync(cancellationToken);
            try
            {
                if (_availableCache is null)
                {
                    var available = await host.InvokeAsync(
                        "Get-Module -ListAvailable | Sort-Object Name -Unique | " +
                        "ForEach-Object { '  {0} {1}' -f $_.Name, $_.Version }",
                        raw: true, // this tool formats its own lines; never shape them
                        cancellationToken: cancellationToken);
                    _availableCache = available.Output.TrimEnd();
                }
                sb.AppendLine("modules available:");
                sb.AppendLine(_availableCache.Length > 0 ? _availableCache : "  (none)");
            }
            finally
            {
                CacheGate.Release();
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatUptime(TimeSpan up) =>
        up.TotalHours >= 1 ? $"{(int)up.TotalHours}h{up.Minutes:00}m"
        : up.TotalMinutes >= 1 ? $"{(int)up.TotalMinutes}m{up.Seconds:00}s"
        : $"{Math.Max(0, (int)up.TotalSeconds)}s";
}
