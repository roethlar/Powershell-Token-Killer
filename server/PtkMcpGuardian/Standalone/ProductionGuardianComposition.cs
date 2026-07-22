using PtkMcpGuardian.Host;
using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Ownership;
using PtkMcpGuardian.Output;
using PtkMcpGuardian.Package;
using PtkMcpServer;
using PtkMcpServer.Audit;
using PtkSharedContracts;

namespace PtkMcpGuardian.Standalone;

/// <summary>
/// Production ownership root for one public guardian boot. Every mutable
/// resource here outlives a replaceable private host generation.
/// </summary>
internal sealed class ProductionGuardianComposition : IAsyncDisposable
{
    internal static readonly TimeSpan HostStartupTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaximumCallTimeout = TimeSpan.FromHours(1);

    private readonly ProductionGuardianAuditRuntime _audit;
    private readonly OutputStore _outputStore;
    private readonly GuardianOutputCoordinator _outputCoordinator;
    private readonly GuardianJobCapabilityRegistry _jobCapabilities;
    private readonly GuardianHostSupervisor _supervisor;
    private readonly GuardianMcpApplication _application;
    private int _runClaimed;
    private int _disposeClaimed;

    private ProductionGuardianComposition(
        GuardianBootId guardianBootId,
        FrozenDefaultSessionState sessionState,
        GuardianHostSupervisorPins pins,
        ProductionGuardianAuditRuntime audit,
        OutputStore outputStore,
        GuardianOutputCoordinator outputCoordinator,
        GuardianJobCapabilityRegistry jobCapabilities,
        GuardianHostSupervisor supervisor,
        GuardianMcpApplication application)
    {
        GuardianBootId = guardianBootId;
        SessionState = sessionState;
        Pins = pins;
        _audit = audit;
        _outputStore = outputStore;
        _outputCoordinator = outputCoordinator;
        _jobCapabilities = jobCapabilities;
        _supervisor = supervisor;
        _application = application;
    }

    internal GuardianBootId GuardianBootId { get; }

    internal FrozenDefaultSessionState SessionState { get; }

    internal GuardianHostSupervisorPins Pins { get; }

    internal GuardianHostSupervisor Supervisor => _supervisor;

    internal static ProductionGuardianComposition Create(MatchedPackageFacts package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var launcher = CreatePlatformLauncher();
        var auditStartup = AuditStartupConfiguration.LoadFromEnvironment();
        try
        {
            return Create(
                package,
                auditStartup,
                launcher,
                OutputStoreOptions.Production());
        }
        catch
        {
            auditStartup.Dispose();
            throw;
        }
    }

    internal static ProductionGuardianComposition Create(
        MatchedPackageFacts package,
        AuditStartupConfiguration auditStartup,
        IPrivateHostProcessLauncher launcher,
        OutputStoreOptions outputStoreOptions,
        TimeProvider? timeProvider = null,
        IGuardianHostSupervisorScheduler? scheduler = null,
        GuardianBootId? guardianBootId = null,
        WorkerBootId? defaultWorkerBootId = null,
        IEnumerable<KeyValuePair<string, string>>? parentEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(auditStartup);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(outputStoreOptions);

        var selectedTimeProvider = timeProvider ?? TimeProvider.System;
        var selectedScheduler = scheduler ??
            new SystemGuardianHostSupervisorScheduler(selectedTimeProvider);
        var selectedGuardianBootId = guardianBootId ??
            new GuardianBootId(Guid.NewGuid());
        var sessionState = new FrozenDefaultSessionState(
            selectedGuardianBootId,
            defaultWorkerBootId ?? new WorkerBootId(Guid.NewGuid()),
            new FrozenSessionCatalog([]),
            allowColdBackground: true);
        var pins = new GuardianHostSupervisorPins(
            package.HostExecutableDigest,
            package.HostBuildDigest,
            package.PublicContractDigest,
            sessionState.ConfigurationDigest,
            sessionState.CatalogDigest,
            package.PackageManifestDigest);
        var hostSnapshots = new GuardianAuditHostSnapshotSource();

        ProductionGuardianAuditRuntime? audit = null;
        OutputStore? outputStore = null;
        GuardianOutputCoordinator? outputCoordinator = null;
        GuardianJobCapabilityRegistry? jobCapabilities = null;
        GuardianHostSupervisor? supervisor = null;
        var startupOwnedByAudit = false;
        try
        {
            audit = ProductionGuardianAuditRuntime.Create(
                auditStartup,
                hostSnapshots);
            startupOwnedByAudit = true;
            outputStore = new OutputStore(outputStoreOptions);
            outputCoordinator = new GuardianOutputCoordinator(
                new GuardianOutputCapabilityRegistry(
                    outputStore,
                    selectedTimeProvider));
            jobCapabilities = new GuardianJobCapabilityRegistry(
                new MonotonicPublicJobIdAllocator());
            var attemptFactory = new PrivateHostAttemptFactory(
                package,
                pins,
                launcher,
                parentEnvironment);
            var lifecycle = new GuardianHostLifecycleController(
                selectedGuardianBootId,
                new MonotonicHostGenerationAllocator(),
                new RandomGuardianHostBootIdSource(),
                new FixedGuardianHostStartupDeadlineSource(
                    selectedTimeProvider,
                    HostStartupTimeout),
                attemptFactory,
                selectedTimeProvider,
                hostSnapshots);
            supervisor = new GuardianHostSupervisor(
                selectedGuardianBootId,
                lifecycle,
                sessionState,
                new MonotonicPrivateRequestIdAllocator(),
                selectedTimeProvider,
                selectedScheduler,
                sessionState,
                NoOpGuardianHostSupervisorDispatchObserver.Instance,
                pins,
                outputCoordinator: outputCoordinator,
                jobCapabilities: jobCapabilities,
                outputProtector: audit.OutputProtector,
                lifecycleAudit: new GuardianHostLifecycleAudit(audit.Runtime));
            var application = new GuardianMcpApplication(
                supervisor,
                audit.Runtime,
                DefaultCallTimeout,
                MaximumCallTimeout,
                audit.OutputProtector);
            var composition = new ProductionGuardianComposition(
                selectedGuardianBootId,
                sessionState,
                pins,
                audit,
                outputStore,
                outputCoordinator,
                jobCapabilities,
                supervisor,
                application);
            audit = null;
            outputStore = null;
            outputCoordinator = null;
            jobCapabilities = null;
            supervisor = null;
            return composition;
        }
        finally
        {
            try
            {
                if (supervisor is not null)
                    supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                try
                {
                    jobCapabilities?.Dispose();
                }
                finally
                {
                    try
                    {
                        outputCoordinator?.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            outputStore?.Dispose();
                        }
                        finally
                        {
                            audit?.Dispose();
                            if (!startupOwnedByAudit)
                                auditStartup.Dispose();
                        }
                    }
                }
            }
        }
    }

    internal async Task RunAsync(
        Stream publicInput,
        Stream publicOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publicInput);
        ArgumentNullException.ThrowIfNull(publicOutput);
        if (Interlocked.CompareExchange(ref _runClaimed, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "The production guardian composition can run only once.");
        }

        await _audit.Runtime.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var health = _audit.Runtime.Health.Snapshot().State;
            if (health is AuditHealthState.Healthy or AuditHealthState.Recovered)
                await _supervisor.StartAsync(cancellationToken).ConfigureAwait(false);

            await _application
                .RunAsync(publicInput, publicOutput, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await _supervisor.ShutdownAsync().ConfigureAwait(false);
            await _audit.Runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeClaimed, 1) != 0)
            return;
        try
        {
            await _supervisor.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                try
                {
                    _jobCapabilities.Dispose();
                }
                finally
                {
                    try
                    {
                        _outputCoordinator.Dispose();
                    }
                    finally
                    {
                        _outputStore.Dispose();
                    }
                }
            }
            finally
            {
                _audit.Dispose();
            }
        }
    }

    private static IPrivateHostProcessLauncher CreatePlatformLauncher()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsPrivateHostProcessLauncher();
        throw new PlatformNotSupportedException(
            "The production Unix guardian requires the native PtkGuardianBroker launcher.");
    }
}

internal sealed class ProductionGuardianAuditRuntime : IDisposable
{
    private readonly AuditStartupConfiguration _startup;
    private readonly AuditOtlpHttpExporter? _exporter;
    private int _disposed;

    private ProductionGuardianAuditRuntime(
        AuditStartupConfiguration startup,
        AuditOtlpHttpExporter? exporter,
        AuditRuntimeGate runtime,
        AuditOutputRequestProtector outputProtector)
    {
        _startup = startup;
        _exporter = exporter;
        Runtime = runtime;
        OutputProtector = outputProtector;
    }

    internal AuditRuntimeGate Runtime { get; }

    internal AuditOutputRequestProtector OutputProtector { get; }

    internal static ProductionGuardianAuditRuntime Create(
        AuditStartupConfiguration startup,
        IAuditHostSnapshotSource hostSnapshots)
    {
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(hostSnapshots);

        AuditOtlpHttpExporter? exporter = null;
        AuditOutputRequestProtector? outputProtector = null;
        AuditRuntimeGate? runtime = null;
        try
        {
            var options = startup.AuditOptions;
            var health = new AuditHealth(options);
            var evidence = new ScriptEvidenceStoreProvider(options);
            var producerVersion = typeof(ProductionGuardianComposition)
                .Assembly.GetName().Version?.ToString() ?? "0.0.0";
            exporter = startup.ExportOptions is null
                ? null
                : AuditOtlpHttpExporter.Create(
                    startup.ExportOptions,
                    producerVersion);
            var runtimeExporter = exporter;
            Func<IAuditRuntimeResources> openRuntime = exporter is null
                ? () => AuditRuntimeResources.OpenLocal(
                    options,
                    health,
                    producerVersion,
                    evidence,
                    hostSnapshots)
                : () => AuditRuntimeResources.OpenAnchored(
                    options,
                    health,
                    producerVersion,
                    runtimeExporter!,
                    evidence,
                    hostSnapshots: hostSnapshots);
            runtime = new AuditRuntimeGate(
                options,
                health,
                evidence,
                producerVersion,
                openRuntime: openRuntime,
                callFactory: GuardianAuditCallFactory.Instance);
            outputProtector = new AuditOutputRequestProtector();
            var result = new ProductionGuardianAuditRuntime(
                startup,
                exporter,
                runtime,
                outputProtector);
            exporter = null;
            runtime = null;
            outputProtector = null;
            return result;
        }
        finally
        {
            outputProtector?.Dispose();
            runtime?.Dispose();
            exporter?.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            Runtime.Dispose();
        }
        finally
        {
            try
            {
                OutputProtector.Dispose();
                _exporter?.Dispose();
            }
            finally
            {
                _startup.Dispose();
            }
        }
    }
}
