using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PtkMcpServer;
using PtkMcpServer.Audit;

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
var maxCallTimeout = TimeSpan.FromSeconds(
    double.TryParse(Environment.GetEnvironmentVariable("PTK_MAX_CALL_TIMEOUT_SECONDS"), out var m) && m > 0
        ? m
        : 3600); // caps the per-call timeoutSeconds override on ptk_invoke
// Resolve once before serving. Warm-session scripts can mutate the process
// PATH, but background jobs must keep using the executable selected at server
// startup. A failed lookup is also frozen so a later PATH cannot supply one.
var jobPwshExecutable = JobPwshExecutable.ResolveFromPath();

// Audit is mandatory and startup-frozen. Local-only is the default and needs
// no SIEM configuration; PTK_AUDIT_ROOT exists for an operator-controlled
// protected location and for isolated integration tests.
var configuredAuditRoot = Environment.GetEnvironmentVariable("PTK_AUDIT_ROOT");
var auditOptions = string.IsNullOrWhiteSpace(configuredAuditRoot)
    ? AuditOptions.CreateDefault()
    : AuditOptions.Create(Path.GetFullPath(configuredAuditRoot));
var producerVersion = typeof(RunspaceHost).Assembly.GetName().Version?.ToString() ?? "0.0.0";

builder.Services.AddSingleton(auditOptions);
builder.Services.AddSingleton(sp => new AuditHealth(sp.GetRequiredService<AuditOptions>()));
builder.Services.AddSingleton(sp => new ScriptEvidenceStoreProvider(
    sp.GetRequiredService<AuditOptions>()));
builder.Services.AddScoped<AuditCallContextAccessor>();
builder.Services.AddSingleton(sp => new AuditRuntimeGate(
    sp.GetRequiredService<AuditOptions>(),
    sp.GetRequiredService<AuditHealth>(),
    sp.GetRequiredService<ScriptEvidenceStoreProvider>(),
    producerVersion));
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AuditRuntimeGate>());

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<AuditRuntimeGate>().RunRunspaceHostAfterStarted(
        () => new RunspaceHost(callTimeout, maxCallTimeout: maxCallTimeout)));
builder.Services.AddSingleton(new RawUsageCounter());
// Factory registration so the container disposes it on graceful shutdown,
// killing running jobs. A hard-killed server can leave jobs orphaned - the
// trade-off of process-based jobs, documented in server/README.md.
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<AuditRuntimeGate>().RunJobManagerAfterStarted(
        () => new JobManager(jobPwshExecutable)));
builder.Services.AddHostedService(sp => new IdleWatchdog(
    idleExit,
    () => sp.GetRequiredService<AuditRuntimeGate>().LastActivityUtc,
    () => sp.GetRequiredService<IHostApplicationLifetime>().StopApplication()));
// Capture the transport streams BEFORE detaching stdin: the streams wrap the
// original handles, so the JSON-RPC channel keeps working while every child
// process spawned from the warm runspace inherits NUL/EOF instead of the live
// pipe (see ChildStdinGuard).
var mcpIn = Console.OpenStandardInput();
var mcpOut = Console.OpenStandardOutput();
PtkMcpServer.ChildStdinGuard.DetachChildStdin();
builder.Services
    .AddMcpServer(options => options.ScopeRequests = true)
    .WithStreamServerTransport(mcpIn, mcpOut)
    .WithRequestFilters(filters =>
        filters.AddCallToolFilter(AuditCallFilter.Create(callTimeout, maxCallTimeout)))
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
