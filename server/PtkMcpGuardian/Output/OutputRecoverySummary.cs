namespace PtkMcpServer;

internal sealed record OutputRecoverySummary(
    string? Handle,
    OutputArtifactState State,
    long Bytes,
    string? DetailCode,
    bool Advertise)
{
    internal static OutputRecoverySummary FromSeal(OutputSealResult result) => new(
        result.Handle,
        result.State,
        result.Bytes,
        result.DetailCode,
        Advertise: result.Success && result.Handle is not null);

    internal static OutputRecoverySummary Unavailable(
        string detailCode,
        bool advertise = false) => new(
            Handle: null,
            State: OutputArtifactState.NotFound,
            Bytes: 0,
            DetailCode: detailCode,
            Advertise: advertise);
}
