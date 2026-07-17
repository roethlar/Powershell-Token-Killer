using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

/// <summary>
/// The only execution boundary visible to the private host protocol owner.
/// Concrete session/runtime composition remains outside the protocol server.
/// </summary>
internal interface IPrivateHostRuntime
{
    ValueTask InitializeAsync(
        PrivateHostInitialization initialization,
        CancellationToken cancellationToken);

    ValueTask<PrivateHostOperationOutcome> ExecuteOperationAsync(
        OperationRequest request,
        CancellationToken cancellationToken);

    ValueTask ShutdownAsync(
        GuardianHostShutdown shutdown,
        CancellationToken cancellationToken);
}

/// <summary>
/// A closed runtime terminal that deliberately carries no wire identity or
/// request correlation. The protocol server remains their sole owner.
/// </summary>
internal sealed class PrivateHostOperationOutcome
{
    private PrivateHostOperationOutcome(
        GuardianHostOperationResult? result,
        GuardianHostPrivateError? error)
    {
        if ((result is null) == (error is null))
            throw new ArgumentException("Exactly one private operation terminal is required.");
        Result = result;
        Error = error;
    }

    internal GuardianHostOperationResult? Result { get; }

    internal GuardianHostPrivateError? Error { get; }

    internal static PrivateHostOperationOutcome Completed(
        GuardianHostOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new PrivateHostOperationOutcome(result, error: null);
    }

    internal static PrivateHostOperationOutcome Failed(
        GuardianHostPrivateDetailCode detailCode) =>
        new(result: null, new GuardianHostPrivateError(detailCode));
}
