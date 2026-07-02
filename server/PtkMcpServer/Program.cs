using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the JSON-RPC transport; every log line must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var callTimeout = TimeSpan.FromSeconds(
    double.TryParse(Environment.GetEnvironmentVariable("PTK_CALL_TIMEOUT_SECONDS"), out var s) && s > 0
        ? s
        : 300);
var idleExit = TimeSpan.FromSeconds(
    double.TryParse(Environment.GetEnvironmentVariable("PTK_IDLE_EXIT_SECONDS"), out var i) && i > 0
        ? i
        : 14400); // 4h backstop for orphaned servers; Claude Code normally kills the child itself.

builder.Services.AddSingleton(new PtkMcpServer.RunspaceHost(callTimeout));
builder.Services.AddHostedService(sp => new PtkMcpServer.IdleWatchdog(
    idleExit,
    () => sp.GetRequiredService<PtkMcpServer.RunspaceHost>().LastActivityUtc,
    () => sp.GetRequiredService<IHostApplicationLifetime>().StopApplication()));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
