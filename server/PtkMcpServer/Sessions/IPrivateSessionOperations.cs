using PtkMcpGuardian.Ownership;
using PtkSharedContracts;

namespace PtkMcpServer.Sessions;

/// <summary>
/// Exact host-side operations for one already-created in-process session.
/// The private host supplies request authority and execution-scoped output
/// capabilities; the session never receives guardian audit or handle-table
/// ownership.
/// </summary>
internal interface IPrivateSessionOperations : IOrderedOwnedLifetime
{
    Task<string> InvokeAsync(
        SessionOperationAuthority operationAuthority,
        InvokeForegroundOperation operation,
        CancellationToken cancellationToken,
        IExecutionOutputCaptureOwner outputCaptureOwner);

    Task<string> InvokeAsync(
        SessionOperationAuthority operationAuthority,
        InvokeBackgroundOperation operation,
        CancellationToken cancellationToken,
        Func<JobSnapshot, Task> onTerminal,
        IExecutionOutputCaptureOwner outputCaptureOwner);

    Task<string> JobAsync(
        SessionOperationAuthority operationAuthority,
        JobListOperation operation,
        CancellationToken cancellationToken);

    Task<string> JobAsync(
        SessionOperationAuthority operationAuthority,
        JobStatusOperation operation,
        CancellationToken cancellationToken);

    Task<string> JobAsync(
        SessionOperationAuthority operationAuthority,
        JobOutputOperation operation,
        CancellationToken cancellationToken);

    Task<string> JobAsync(
        SessionOperationAuthority operationAuthority,
        JobKillOperation operation,
        CancellationToken cancellationToken);

    Task<string> ResetAsync(
        SessionOperationAuthority operationAuthority,
        ResetOperation operation,
        CancellationToken cancellationToken);
}
