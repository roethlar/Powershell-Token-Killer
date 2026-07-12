namespace PtkMcpServer.Tests;

internal sealed class TestInvocationAuthorizer(
    Func<ExecutionPlan, CancellationToken, ValueTask<bool>> authorizePlan,
    Func<ExecutionDispatch, CancellationToken, ValueTask<bool>>? authorizeDispatch = null)
    : IInvocationAuthorizer
{
    private readonly Func<ExecutionPlan, CancellationToken, ValueTask<bool>> _authorizePlan =
        authorizePlan ?? throw new ArgumentNullException(nameof(authorizePlan));

    private readonly Func<ExecutionDispatch, CancellationToken, ValueTask<bool>> _authorizeDispatch =
        authorizeDispatch ?? ((_, _) => ValueTask.FromResult(true));

    public ValueTask<bool> AuthorizePlanAsync(
        ExecutionPlan plan,
        CancellationToken cancellationToken) =>
        _authorizePlan(plan, cancellationToken);

    public ValueTask<bool> AuthorizeDispatchAsync(
        ExecutionDispatch dispatch,
        CancellationToken cancellationToken) =>
        _authorizeDispatch(dispatch, cancellationToken);
}
