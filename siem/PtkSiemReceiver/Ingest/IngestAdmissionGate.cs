namespace PtkSiemReceiver.Ingest;

/// <summary>
/// Global ingest admission cap (rbc-12): bounds how many requests are
/// serviced concurrently so a flood of maximum-size bodies cannot pin
/// unbounded memory (each admitted request may buffer up to
/// maxRequestBytes). Saturation is refused, never queued: the caller
/// gets a transient 503 with Retry-After so compliant OTLP exporters
/// back off and retry.
/// </summary>
internal sealed class IngestAdmissionGate : IDisposable
{
    private readonly SemaphoreSlim _slots;

    internal IngestAdmissionGate(int maxConcurrentRequests)
    {
        _slots = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
    }

    /// <summary>Non-blocking acquire: refuse rather than queue when saturated.</summary>
    internal bool TryEnter() => _slots.Wait(0);

    internal void Exit() => _slots.Release();

    public void Dispose() => _slots.Dispose();
}
