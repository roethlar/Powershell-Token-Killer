using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PtkMcpServer;
using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;

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

// Audit is mandatory and startup-frozen. No export configuration means
// protected local-only logging. Merely supplying PTK_AUDIT_EXPORT_CONFIG is
// explicit anchored intent: its complete protected configuration must validate
// before the host exists, and there is no endpoint or credential fallback.
using var auditStartup = AuditStartupConfiguration.LoadFromEnvironment();
using var outputRequestProtector = new AuditOutputRequestProtector();
var auditOptions = auditStartup.AuditOptions;
var producerVersion = typeof(RunspaceHost).Assembly.GetName().Version?.ToString() ?? "0.0.0";
using var auditExporter = auditStartup.ExportOptions is null
    ? null
    : AuditOtlpHttpExporter.Create(auditStartup.ExportOptions, producerVersion);

builder.Services.AddSingleton(auditOptions);
builder.Services.AddSingleton(sp => new AuditHealth(sp.GetRequiredService<AuditOptions>()));
builder.Services.AddSingleton(sp => new ScriptEvidenceStoreProvider(
    sp.GetRequiredService<AuditOptions>()));
builder.Services.AddScoped<AuditCallContextAccessor>();
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<AuditOptions>();
    var health = sp.GetRequiredService<AuditHealth>();
    Func<AuditRuntimeResources>? openRuntime = auditExporter is null
        ? null
        : () => AuditRuntimeResources.OpenAnchored(
            options,
            health,
            producerVersion,
            auditExporter,
            sp.GetRequiredService<ScriptEvidenceStoreProvider>());
    return new AuditRuntimeGate(
        options,
        health,
        sp.GetRequiredService<ScriptEvidenceStoreProvider>(),
        producerVersion,
        openRuntime: openRuntime);
});
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AuditRuntimeGate>());

// Harness-lifetime recovery belongs to the supervisor service provider, not
// a request scope or the replaceable runspace host.
builder.Services.AddSingleton(_ => new OutputStore(OutputStoreOptions.Production()));
// The default session is one owned runtime. Audit startup must be durable
// before either the runspace or the job manager can be constructed. The
// runtime preserves shutdown order (jobs, then runspace), and the output store
// remains supervisor-owned so recovery handles outlive session replacement.
builder.Services.AddSingleton<ISessionOperations>(sp =>
    sp.GetRequiredService<AuditRuntimeGate>().RunSessionAfterStarted(() =>
    {
        var host = new RunspaceHost(callTimeout, maxCallTimeout: maxCallTimeout);
        try
        {
            return new SessionRuntime(
                host,
                new JobManager(jobPwshExecutable),
                new RawUsageCounter());
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }));
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
        filters.AddCallToolFilter(AuditCallFilter.Create(
            callTimeout,
            maxCallTimeout,
            outputRequestProtector)))
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
