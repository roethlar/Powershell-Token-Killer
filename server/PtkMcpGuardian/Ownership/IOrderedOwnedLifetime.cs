namespace PtkMcpGuardian.Ownership;

/// <summary>
/// Guardian-safe ownership boundary for a runtime that must complete its
/// ordered asynchronous drain before its synchronous resources are disposed.
/// </summary>
internal interface IOrderedOwnedLifetime : IDisposable
{
    Task ShutdownAsync();
}
