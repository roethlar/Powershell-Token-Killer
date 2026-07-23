using System.Management.Automation;

namespace PtkMcpServer;

/// <summary>
/// Resolves the literal command names a pristine cold PowerShell child will
/// see through its inherited PATH. Relative PATH entries remain deliberately
/// unresolved until the audited job cwd exists. An explicit uncertain result
/// keeps dialect classification conservative without pretending a script was
/// actually found.
/// </summary>
internal static class ColdPathCommandResolver
{
    internal static ResolvedCommand? Resolve(
        string name,
        string? workingDirectory = null) => Resolve(
        name,
        Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
        Environment.GetEnvironmentVariable("PATHEXT"),
        OperatingSystem.IsWindows(),
        workingDirectory);

    internal static ResolvedCommand? Resolve(
        string name,
        string pathValue,
        string? pathExtensionsValue,
        bool windows,
        string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(pathValue);

        // Cold planning only offers literal command names. A relative path's
        // base is the later audited cwd, not the server process directory.
        if (string.IsNullOrWhiteSpace(name) ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar) ||
            windows && name.Contains('\\'))
        {
            return null;
        }

        if (workingDirectory is not null &&
            !Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException(
                "A cold command working directory must be absolute.",
                nameof(workingDirectory));
        }

        var pathExtensions = windows
            ? (pathExtensionsValue ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
        var pathSeparator = windows ? ';' : ':';
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawEntry in pathValue.Split(
                     pathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            // Match CommandDiscovery.GetLookupDirectoryPaths: leading space is
            // ignored, trailing space and quotes are literal, empty fields are
            // absent, and only PowerShell's home shorthand is expanded.
            var directory = rawEntry.TrimStart();
            var directorySeparator = windows ? '\\' : '/';
            var home = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolderOption.DoNotVerify);
            if (directory.Equals("~", StringComparison.OrdinalIgnoreCase))
            {
                directory = home;
            }
            else if (directory.StartsWith(
                         "~" + directorySeparator,
                         StringComparison.OrdinalIgnoreCase))
            {
                directory = home + directorySeparator + directory[2..];
            }
            if (!seenDirectories.Add(directory) || directory.Length == 0)
                continue;

            if (!Path.IsPathFullyQualified(directory))
            {
                if (workingDirectory is null)
                {
                    // The pre-cwd dialect pass cannot safely guess the base.
                    return new ResolvedCommand(
                        (CommandTypes)0,
                        ResolutionUncertain: true);
                }

                try
                {
                    directory = Path.GetFullPath(
                        Path.Combine(workingDirectory, directory));
                }
                catch (Exception exception) when (exception is
                    ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return new ResolvedCommand(
                        (CommandTypes)0,
                        ResolutionUncertain: true);
                }
            }

            if (windows)
            {
                var resolved = ResolveWindowsDirectory(directory, name, pathExtensions);
                if (resolved is not null) return resolved;
                continue;
            }

            var exactUnix = Path.Combine(directory, name);
            if (File.Exists(exactUnix))
            {
                if (Path.GetExtension(exactUnix)
                    .Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                {
                    return PathCommand(exactUnix, CommandTypes.ExternalScript);
                }

                if (OperatingSystem.IsWindows()) continue;
                try
                {
                    var mode = File.GetUnixFileMode(exactUnix);
                    const UnixFileMode executable =
                        UnixFileMode.UserExecute |
                        UnixFileMode.GroupExecute |
                        UnixFileMode.OtherExecute;
                    if ((mode & executable) != 0)
                        return PathCommand(exactUnix, CommandTypes.Application);
                }
                catch
                {
                    // An unreadable candidate is not safely resolvable.
                }
            }

            var unixScript = Path.Combine(directory, name + ".ps1");
            if (File.Exists(unixScript))
                return PathCommand(unixScript, CommandTypes.ExternalScript);
        }

        return null;
    }

    private static ResolvedCommand? ResolveWindowsDirectory(
        string directory,
        string name,
        IReadOnlyList<string> pathExtensions)
    {
        var candidates = new List<(string Name, CommandTypes Type)>();
        var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string candidate, CommandTypes type)
        {
            if (seenCandidates.Add(candidate)) candidates.Add((candidate, type));
        }

        var hasExtension = Path.HasExtension(name);
        if (hasExtension)
        {
            Add(
                name,
                Path.GetExtension(name).Equals(
                    ".ps1",
                    StringComparison.OrdinalIgnoreCase)
                    ? CommandTypes.ExternalScript
                    : CommandTypes.Application);
        }

        Add(name + ".ps1", CommandTypes.ExternalScript);
        foreach (var extension in pathExtensions)
            Add(name + extension, CommandTypes.Application);
        if (!hasExtension) Add(name, CommandTypes.Application);

        foreach (var candidate in candidates)
        {
            var path = Path.Combine(directory, candidate.Name);
            if (File.Exists(path)) return PathCommand(path, candidate.Type);
        }
        return null;
    }

    private static ResolvedCommand PathCommand(string path, CommandTypes type)
    {
        var fullPath = Path.GetFullPath(path);
        return new ResolvedCommand(type, fullPath, fullPath);
    }
}

/// <summary>
/// Immutable identity of the application a cold RTK plan resolved. Job
/// commit re-resolves the original literal name and hashes the selected file;
/// a PATH, type, link target, content, or mode change is a proved no-start.
/// </summary>
internal sealed record ColdCommandTargetIdentity
{
    private ColdCommandTargetIdentity(
        string commandName,
        string workingDirectory,
        ExecutableFileIdentity executable)
    {
        CommandName = commandName;
        WorkingDirectory = workingDirectory;
        Executable = executable;
    }

    internal string CommandName { get; }
    internal string WorkingDirectory { get; }
    internal ExecutableFileIdentity Executable { get; }

    internal static ColdCommandTargetIdentity? TryCapture(
        string commandName,
        ResolvedCommand? resolved,
        string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(commandName) ||
            resolved?.CommandType != CommandTypes.Application ||
            resolved.ResolutionUncertain ||
            string.IsNullOrWhiteSpace(resolved.Source) ||
            !Path.IsPathFullyQualified(resolved.Source) ||
            string.IsNullOrWhiteSpace(workingDirectory) ||
            !Path.IsPathFullyQualified(workingDirectory))
        {
            return null;
        }

        var executable = ExecutableFileIdentity.TryCapture(resolved.Source);
        return executable is null
            ? null
            : new ColdCommandTargetIdentity(
                commandName,
                Path.GetFullPath(workingDirectory),
                executable);
    }

    // Design requirement (rbc-13, refuted-as-defect 2026-07-19): PATH must be
    // stable between plan prepare and CommitStart. This method deliberately
    // re-resolves against the *live* process PATH at commit time and fails
    // closed on any resolution change (RtkTargetResolutionChanged). PATH
    // hijack is in-threat-model, so a prepare-time snapshot compare would
    // weaken the integrity gate; non-shadowing PATH appends do not change
    // resolution and are not penalized. Users who mutate PATH such that the
    // command resolves differently must re-prepare — that is a proved
    // no-start by design, not a bug. See .agents/review/findings/rbc-13.md.
    internal bool MatchesCurrentResolution()
    {
        var current = Path.IsPathFullyQualified(CommandName)
            ? new ResolvedCommand(
                CommandTypes.Application,
                CommandName,
                CommandName)
            : ColdPathCommandResolver.Resolve(CommandName, WorkingDirectory);
        return Matches(current);
    }

    internal bool Matches(ResolvedCommand? current)
    {
        if (current?.CommandType != CommandTypes.Application ||
            current.ResolutionUncertain)
        {
            return false;
        }
        var identity = ExecutableFileIdentity.TryCapture(current.Source);
        return identity is not null && identity == Executable;
    }
}
