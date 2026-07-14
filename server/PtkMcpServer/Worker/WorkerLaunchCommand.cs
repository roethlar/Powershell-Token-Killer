using System.Collections.ObjectModel;

namespace PtkMcpServer.Worker;

internal static class WorkerBootstrapEnvironment
{
    internal const string RequestHandle = "PTK_WORKER_REQUEST_HANDLE";
    internal const string EventHandle = "PTK_WORKER_EVENT_HANDLE";

    internal static readonly IReadOnlySet<string> ReservedHandleVariables =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RequestHandle,
            EventHandle,
        };
}

internal sealed class WorkerLaunchCommand
{
    internal WorkerLaunchCommand(
        string executablePath,
        IEnumerable<string> arguments,
        string workingDirectory,
        IEnumerable<KeyValuePair<string, string>> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(environment);
        if (executablePath.Contains('\0'))
            throw new ArgumentException("Worker executable path contains a null character.", nameof(executablePath));
        if (workingDirectory.Contains('\0'))
            throw new ArgumentException("Worker working directory contains a null character.", nameof(workingDirectory));
        if (!Path.IsPathFullyQualified(executablePath))
            throw new ArgumentException("Worker executable path must be absolute.", nameof(executablePath));
        if (!Path.IsPathFullyQualified(workingDirectory))
            throw new ArgumentException("Worker working directory must be absolute.", nameof(workingDirectory));

        var frozenArguments = arguments.ToArray();
        if (frozenArguments.Any(argument => argument is null))
            throw new ArgumentException("Worker arguments cannot contain null.", nameof(arguments));
        if (frozenArguments.Any(argument => argument.Contains('\0')))
            throw new ArgumentException("Worker arguments cannot contain null characters.", nameof(arguments));

        var frozenEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in environment)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
            if (pair.Key.Contains('=') || pair.Key.Contains('\0') || pair.Value.Contains('\0'))
                throw new ArgumentException("Worker environment contains an invalid name or value.", nameof(environment));
            if (WorkerBootstrapEnvironment.ReservedHandleVariables.Contains(pair.Key))
                throw new ArgumentException("Worker environment cannot set reserved handle variables.", nameof(environment));
            if (!frozenEnvironment.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException("Worker environment contains duplicate names.", nameof(environment));
        }

        ExecutablePath = executablePath;
        Arguments = Array.AsReadOnly(frozenArguments);
        WorkingDirectory = workingDirectory;
        Environment = new ReadOnlyDictionary<string, string>(frozenEnvironment);
    }

    internal string ExecutablePath { get; }
    internal IReadOnlyList<string> Arguments { get; }
    internal string WorkingDirectory { get; }
    internal IReadOnlyDictionary<string, string> Environment { get; }
}
