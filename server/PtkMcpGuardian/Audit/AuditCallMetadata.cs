namespace PtkMcpServer.Audit;

internal sealed record AuditClientContext(
    string? ClientName = null,
    string? ClientVersion = null,
    string? ClientSessionId = null);

internal sealed record AuditOperationProfile(
    int MaximumCallRecordSlots,
    int PersistentJobTerminalSlots,
    bool RequiresScriptEvidence,
    bool MayHaveSideEffects)
{
    internal int MaximumRecordSlots => checked(MaximumCallRecordSlots + PersistentJobTerminalSlots);

    internal long MaximumReservationBytes(int maximumRecordBytes)
    {
        if (maximumRecordBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumRecordBytes));
        return checked((long)MaximumRecordSlots * maximumRecordBytes);
    }
}

internal sealed record AuditCallMetadata(
    AuditActor Actor,
    AuditRequest Request,
    AuditOperationProfile OperationProfile);
