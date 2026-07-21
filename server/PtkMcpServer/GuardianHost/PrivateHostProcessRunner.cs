namespace PtkMcpServer.GuardianHost;

internal sealed record PrivateHostProcessContext(
    Stream RequestReadStream,
    Stream EventWriteStream,
    PrivateHostServerIdentity Identity,
    PrivateHostServerPins Pins,
    IPrivateHostRuntime Runtime);

/// <summary>
/// Consumes one already-captured host bootstrap and owns its streams for the
/// entire private protocol lifetime. Process-exit classification remains at
/// the outer first-action boundary.
/// </summary>
internal static class PrivateHostProcessRunner
{
    internal static Task<int> RunAsync(
        PrivateHostBootstrapValues bootstrap,
        Func<PrivateHostServerIdentity, PrivateHostServerPins, IPrivateHostRuntime>
            runtimeFactory,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            bootstrap,
            runtimeFactory,
            static (context, token) => new PrivateHostServer(
                context.RequestReadStream,
                context.EventWriteStream,
                context.Identity,
                context.Pins,
                context.Runtime).RunAsync(token),
            Environment.ProcessId,
            cancellationToken);

    internal static async Task<int> RunAsync(
        PrivateHostBootstrapValues bootstrap,
        Func<PrivateHostServerIdentity, PrivateHostServerPins, IPrivateHostRuntime>
            runtimeFactory,
        Func<PrivateHostProcessContext, CancellationToken, Task> runServer,
        int hostProcessId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        ArgumentNullException.ThrowIfNull(runServer);
        if (hostProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(hostProcessId));

        using (bootstrap)
        {
            var identity = bootstrap.CreateServerIdentity(hostProcessId);
            var pins = bootstrap.ServerPins;
            using var streams = bootstrap.OpenStreams();
            var runtime = runtimeFactory(identity, pins) ??
                throw new InvalidOperationException(
                    "Private host runtime factory returned no runtime.");
            var context = new PrivateHostProcessContext(
                streams.RequestReadStream,
                streams.EventWriteStream,
                identity,
                pins,
                runtime);
            await runServer(context, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }
}
