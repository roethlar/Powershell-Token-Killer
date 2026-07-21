using PtkMcpServer.Worker;

namespace PtkMcpServer.GuardianHost;

internal enum PrivateServerProcessRole
{
    TransitionalDevelopment,
    Host,
    Worker,
    Invalid,
}

internal readonly record struct PrivateServerFirstActionResult(
    bool ContinueTransitionalDevelopment,
    int ExitCode)
{
    internal static PrivateServerFirstActionResult ContinueDevelopment() => new(true, 0);

    internal static PrivateServerFirstActionResult Exit(int exitCode) => new(false, exitCode);
}

/// <summary>
/// Captures and removes inherited private bootstrap material after role
/// classification and before a private runtime delegate can run.
/// </summary>
/// <remarks>
/// Implementations of the bootstrap boundary capture the selected role's
/// inherited bootstrap material and remove it from the process before returning.
/// <see cref="PoisonAndRemove"/> discards and removes material for every private role. The
/// concrete channel names and stream-opening mechanism belong to the launcher.
/// </remarks>
internal interface IPrivateProcessBootstrapBoundary<out THostBootstrap, out TWorkerBootstrap>
{
    THostBootstrap CaptureAndRemoveHost();

    TWorkerBootstrap CaptureAndRemoveWorker();

    void PoisonAndRemove();
}

/// <summary>
/// Owns the first executable action of the private server process.
/// </summary>
internal static class PrivateHostProcessEntry
{
    internal const int InvalidInvocationExitCode = 64;

    private const string HostArgument = "--host";
    private const string WorkerArgument = "--worker";

    internal static PrivateServerProcessRole Classify(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0)
            return PrivateServerProcessRole.TransitionalDevelopment;
        if (arguments.Count != 1)
            return PrivateServerProcessRole.Invalid;
        if (string.Equals(arguments[0], HostArgument, StringComparison.Ordinal))
            return PrivateServerProcessRole.Host;
        if (string.Equals(arguments[0], WorkerArgument, StringComparison.Ordinal))
            return PrivateServerProcessRole.Worker;
        return PrivateServerProcessRole.Invalid;
    }

    internal static async Task<PrivateServerFirstActionResult> RunFirstActionAsync<
        THostBootstrap,
        TWorkerBootstrap>(
        IReadOnlyList<string> arguments,
        IPrivateProcessBootstrapBoundary<THostBootstrap, TWorkerBootstrap>? bootstrapBoundary,
        Func<THostBootstrap, CancellationToken, Task<int>>? runHost,
        Func<TWorkerBootstrap, CancellationToken, Task<int>>? runWorker,
        CancellationToken cancellationToken = default)
    {
        // This classification must remain the first executable action. In
        // particular, no environment, runtime, logging, or stream boundary may
        // be touched before the exact process role is known.
        var role = Classify(arguments);

        return await RunClassifiedActionAsync(
            role,
            bootstrapBoundary,
            runHost,
            runWorker,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<PrivateServerFirstActionResult> RunClassifiedActionAsync<
        THostBootstrap,
        TWorkerBootstrap>(
        PrivateServerProcessRole role,
        IPrivateProcessBootstrapBoundary<THostBootstrap, TWorkerBootstrap>? bootstrapBoundary,
        Func<THostBootstrap, CancellationToken, Task<int>>? runHost,
        Func<TWorkerBootstrap, CancellationToken, Task<int>>? runWorker,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));

        switch (role)
        {
            case PrivateServerProcessRole.TransitionalDevelopment:
                return PrivateServerFirstActionResult.ContinueDevelopment();

            case PrivateServerProcessRole.Host:
            {
                ArgumentNullException.ThrowIfNull(bootstrapBoundary);
                ArgumentNullException.ThrowIfNull(runHost);
                var bootstrap = bootstrapBoundary.CaptureAndRemoveHost();
                var exitCode = await runHost(bootstrap, cancellationToken).ConfigureAwait(false);
                return PrivateServerFirstActionResult.Exit(exitCode);
            }

            case PrivateServerProcessRole.Worker:
            {
                ArgumentNullException.ThrowIfNull(bootstrapBoundary);
                ArgumentNullException.ThrowIfNull(runWorker);
                var bootstrap = bootstrapBoundary.CaptureAndRemoveWorker();
                var exitCode = await runWorker(bootstrap, cancellationToken).ConfigureAwait(false);
                return PrivateServerFirstActionResult.Exit(exitCode);
            }

            case PrivateServerProcessRole.Invalid:
                ArgumentNullException.ThrowIfNull(bootstrapBoundary);
                bootstrapBoundary.PoisonAndRemove();
                return PrivateServerFirstActionResult.Exit(InvalidInvocationExitCode);

            default:
                throw new InvalidOperationException("Unknown private server process role.");
        }
    }

    /// <summary>
    /// Production composition after Program has classified the role as its
    /// first executable action. No public host, audit, output, or MCP boundary
    /// exists on this path.
    /// </summary>
    internal static async Task<int> RunClassifiedProductionAsync(
        PrivateServerProcessRole role,
        CancellationToken cancellationToken = default)
    {
        if (role == PrivateServerProcessRole.TransitionalDevelopment)
        {
            throw new ArgumentException(
                "The transitional development role has no private action.",
                nameof(role));
        }

        try
        {
            var result = await RunClassifiedActionAsync(
                    role,
                    new PrivateProcessBootstrapBoundary(),
                    static (bootstrap, token) => PrivateHostProcessRunner.RunAsync(
                        bootstrap,
                        DefaultPrivateHostRuntimeFactory.Create,
                        token),
                    static (bootstrap, token) => WorkerProcessEntry.RunCapturedAsync(
                        bootstrap,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (WorkerBootstrapException exception)
            when (role == PrivateServerProcessRole.Worker)
        {
            return WorkerProcessEntry.CompleteCapturedBootstrapFailure(
                exception.DetailCode,
                Console.OpenStandardError);
        }
        catch (PrivateHostBootstrapException exception)
        {
            return PrivateHostProcessExit.CompleteBootstrapFailure(
                exception.DetailCode);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return role == PrivateServerProcessRole.Worker
                ? WorkerProcessEntry.CompleteCapturedBootstrapFailure(
                    "bootstrap_failure",
                    Console.OpenStandardError)
                : PrivateHostProcessExit.CompleteRuntimeFailure();
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;
}
