namespace PtkMcpServer.Audit;

/// <summary>
/// Stateful full-jitter retry timing for one exporter loop. Receiver-provided
/// Retry-After values are authoritative; otherwise the random ceiling doubles
/// from one second to sixty seconds. Any acknowledged progress resets the
/// series for the next failure.
/// </summary>
internal sealed class AuditExportRetrySchedule
{
    private static readonly TimeSpan InitialCeiling = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumCeiling = TimeSpan.FromSeconds(60);

    private readonly Func<double> _nextUnitInterval;
    private int _consecutiveFailures;

    internal AuditExportRetrySchedule(Func<double>? nextUnitInterval = null)
    {
        _nextUnitInterval = nextUnitInterval ?? Random.Shared.NextDouble;
    }

    internal TimeSpan NextDelay(TimeSpan? retryAfter = null)
    {
        if (retryAfter < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryAfter));

        var failure = _consecutiveFailures;
        if (_consecutiveFailures < int.MaxValue)
            _consecutiveFailures++;

        if (retryAfter is { } receiverDelay)
            return receiverDelay;

        var sample = _nextUnitInterval();
        if (double.IsNaN(sample) || sample < 0d || sample >= 1d)
        {
            throw new InvalidOperationException(
                "The audit export jitter source returned a value outside [0, 1).");
        }

        var doublingCount = Math.Min(failure, 6);
        var ceilingTicks = checked(InitialCeiling.Ticks << doublingCount);
        ceilingTicks = Math.Min(ceilingTicks, MaximumCeiling.Ticks);
        return TimeSpan.FromTicks((long)Math.Floor(ceilingTicks * sample));
    }

    internal void Reset() => _consecutiveFailures = 0;
}
