using PtkMcpServer.Audit;
using PtkMcpGuardian.Ownership;

namespace PtkMcpServer.Sessions;

/// <summary>
/// Tool-facing operations for the reserved default session. Implementations
/// own where the session runs; MCP adapters retain request-scoped audit and
/// supervisor-owned output capabilities and pass them at the call boundary.
/// This is not the worker wire contract: remote implementations consume those
/// capabilities in the supervisor and serialize only bounded protocol DTOs.
/// </summary>
public interface ISessionOperations
{
    Task<string> InvokeAsync(
        string script,
        CancellationToken cancellationToken,
        bool raw,
        string route,
        bool background,
        int timeoutSeconds,
        AuditCallContextAccessor? auditContext,
        OutputStore? outputStore);

    Task<string> JobAsync(
        string action,
        CancellationToken cancellationToken,
        long id,
        long offset,
        AuditCallContextAccessor? auditContext);

    Task<string> StateAsync(
        bool listAvailable,
        CancellationToken cancellationToken,
        AuditCallContextAccessor? auditContext);

    Task<string> ResetAsync(
        CancellationToken cancellationToken,
        AuditCallContextAccessor? auditContext);
}

/// <summary>
/// Ordered owned-session drain used by the supervisor lifecycle. This remains
/// separate from tool operations so request code cannot initiate shutdown.
/// </summary>
internal interface ISessionLifetime : IOrderedOwnedLifetime
{
}
