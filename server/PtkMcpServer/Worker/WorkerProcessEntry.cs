using PtkMcpServer.Sessions;

namespace PtkMcpServer.Worker;

internal static class WorkerProcessEntry
{
    private const string WorkerArgument = "--worker";

    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        IWorkerBootstrapEnvironmentSource? environment,
        Func<WorkerBootstrapValues, IWorkerBootstrapStreams> openBootstrap,
        Func<WorkerInitializeRequest, CancellationToken, Task<ISessionLifetime>> runtimeFactory,
        Func<Guid> bootIdFactory,
        Func<Stream> standardErrorFactory)
    {
        WorkerBootstrapValues values;
        try
        {
            values = WorkerBootstrapCapture.CaptureAndRemove(environment);
        }
        catch (WorkerBootstrapException exception)
        {
            return CompleteBootstrapFailure(
                exception.DetailCode,
                standardErrorFactory);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return CompleteBootstrapFailure("bootstrap_failure", standardErrorFactory);
        }

        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(openBootstrap);
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        ArgumentNullException.ThrowIfNull(bootIdFactory);
        ArgumentNullException.ThrowIfNull(standardErrorFactory);

        return await RunCapturedCoreAsync(
            arguments,
            values,
            openBootstrap,
            runtimeFactory,
            bootIdFactory,
            standardErrorFactory).ConfigureAwait(false);
    }

    /// <summary>
    /// Production handoff for the exact first-action role boundary. Bootstrap
    /// values have already been captured and removed before this method is
    /// called, so it must never inspect process environment.
    /// </summary>
    internal static Task<int> RunCapturedAsync(
        WorkerBootstrapValues values,
        CancellationToken cancellationToken = default) =>
        RunCapturedAsync(
            values,
            openBootstrap: captured => WindowsWorkerBootstrap.Open(captured),
            runtimeFactory: CreateRuntimeAsync,
            bootIdFactory: Guid.NewGuid,
            standardErrorFactory: Console.OpenStandardError,
            cancellationToken);

    internal static int CompleteCapturedBootstrapFailure(
        string detailCode,
        Func<Stream> standardErrorFactory) =>
        CompleteBootstrapFailure(
            detailCode,
            standardErrorFactory);

    internal static Task<int> RunCapturedAsync(
        WorkerBootstrapValues values,
        Func<WorkerBootstrapValues, IWorkerBootstrapStreams> openBootstrap,
        Func<WorkerInitializeRequest, CancellationToken, Task<ISessionLifetime>> runtimeFactory,
        Func<Guid> bootIdFactory,
        Func<Stream> standardErrorFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openBootstrap);
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        ArgumentNullException.ThrowIfNull(bootIdFactory);
        ArgumentNullException.ThrowIfNull(standardErrorFactory);
        cancellationToken.ThrowIfCancellationRequested();
        return RunCapturedCoreAsync(
            [WorkerArgument],
            values,
            openBootstrap,
            runtimeFactory,
            bootIdFactory,
            standardErrorFactory);
    }

    private static async Task<int> RunCapturedCoreAsync(
        IReadOnlyList<string> arguments,
        WorkerBootstrapValues values,
        Func<WorkerBootstrapValues, IWorkerBootstrapStreams> openBootstrap,
        Func<WorkerInitializeRequest, CancellationToken, Task<ISessionLifetime>> runtimeFactory,
        Func<Guid> bootIdFactory,
        Func<Stream> standardErrorFactory)
    {
        if (arguments.Count != 1 ||
            !string.Equals(arguments[0], WorkerArgument, StringComparison.Ordinal))
        {
            return CompleteInvocationFailure(standardErrorFactory);
        }

        IWorkerBootstrapStreams? bootstrap = null;
        WorkerServerExit finalExit;
        var serverConstructed = false;
        try
        {
            bootstrap = openBootstrap(values) ?? throw new InvalidOperationException(
                "Worker bootstrap returned no stream owner.");
            var workerBootId = bootIdFactory();
            var server = new WorkerServer(
                bootstrap.RequestStream,
                bootstrap.EventStream,
                runtimeFactory,
                workerBootId);
            serverConstructed = true;
            finalExit = await server.RunAsync().ConfigureAwait(false);
        }
        catch (WorkerBootstrapException exception)
        {
            return CompleteBootstrapFailure(
                exception.DetailCode,
                standardErrorFactory,
                bootstrap);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            if (!serverConstructed)
            {
                return CompleteBootstrapFailure(
                    "bootstrap_failure",
                    standardErrorFactory,
                    bootstrap);
            }
            finalExit = new WorkerServerExit(
                WorkerServerExitKind.RuntimeFailure,
                "runtime_failure");
        }

        try
        {
            bootstrap!.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            finalExit = new WorkerServerExit(
                WorkerServerExitKind.RuntimeFailure,
                "cleanup_failed");
        }

        return CompleteServerExit(finalExit, standardErrorFactory);
    }

    private static Task<ISessionLifetime> CreateRuntimeAsync(
        WorkerInitializeRequest initialize,
        CancellationToken cancellationToken)
    {
        _ = initialize;
        cancellationToken.ThrowIfCancellationRequested();
        var callTimeout = DefaultSessionRuntimeFactory.ReadCallTimeout();
        var maxCallTimeout = DefaultSessionRuntimeFactory.ReadMaxCallTimeout();
        var jobPwshExecutable = JobPwshExecutable.ResolveFromPath();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ISessionLifetime>(
            DefaultSessionRuntimeFactory.Create(
                callTimeout,
                maxCallTimeout,
                jobPwshExecutable,
                cancellationToken));
    }

    private static int CompleteInvocationFailure(Func<Stream> standardErrorFactory) =>
        CompleteWithStream(
            64,
            standardErrorFactory,
            WorkerProcessExit.WriteInvocationFailure);

    private static int CompleteBootstrapFailure(
        string detailCode,
        Func<Stream> standardErrorFactory,
        IWorkerBootstrapStreams? bootstrap = null)
    {
        if (bootstrap is not null)
        {
            try
            {
                bootstrap.Dispose();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                detailCode = "bootstrap_failure";
            }
        }
        return CompleteWithStream(
            80,
            standardErrorFactory,
            stream => WorkerProcessExit.WriteBootstrapFailure(detailCode, stream));
    }

    private static int CompleteServerExit(
        WorkerServerExit exit,
        Func<Stream> standardErrorFactory)
    {
        var fallbackCode = WorkerProcessExit.MapCode(exit);
        if (fallbackCode == 0) return 0;
        return CompleteWithStream(
            fallbackCode,
            standardErrorFactory,
            stream => WorkerProcessExit.WriteServerExit(exit, stream));
    }

    private static int CompleteWithStream(
        int fallbackCode,
        Func<Stream> standardErrorFactory,
        Func<Stream, int> complete)
    {
        try
        {
            return complete(standardErrorFactory());
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return fallbackCode;
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;
}
