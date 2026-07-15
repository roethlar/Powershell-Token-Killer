using PtkSiemReceiver.Configuration;
using PtkSiemReceiver.Ingest;

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
        IIngestCommitter committer,
        ILogger<ReceiverLifecycleService> logger)
    {
        _options = options;
        _ = committer; // Force durable-store initialization before startup succeeds.
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
