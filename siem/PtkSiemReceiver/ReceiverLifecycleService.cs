using PtkSiemReceiver.Configuration;

namespace PtkSiemReceiver;

/// <summary>
/// Logs a secret-free startup summary. S2 activates the ingest listener;
/// the operator endpoint remains gated to S5.
/// </summary>
internal sealed class ReceiverLifecycleService : BackgroundService
{
    private readonly SiemReceiverOptions _options;
    private readonly ILogger<ReceiverLifecycleService> _logger;

    public ReceiverLifecycleService(
        SiemReceiverOptions options,
        ILogger<ReceiverLifecycleService> logger)
    {
        _options = options;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "PtkSiemReceiver started: ingest {IngestAddress}:{IngestPort}, " +
            "operator {OperatorAddress}:{OperatorPort}, store {SqlitePath}. " +
            "Ingest is active; the operator endpoint is not yet active.",
            _options.IngestBindAddress,
            _options.IngestPort,
            _options.OperatorBindAddress,
            _options.OperatorPort,
            _options.SqlitePath);
        return Task.CompletedTask;
    }
}
