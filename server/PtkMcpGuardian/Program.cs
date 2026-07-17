namespace PtkMcpGuardian;

internal static class Program
{
    internal const int UsageExitCode = 64;
    internal const string UnsupportedModeMessage =
        "ptk guardian: no runnable private-host mode is available in this build.";

    public static int Main(string[] args) => Run(args, Console.Error);

    internal static int Run(IReadOnlyList<string> arguments, TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardError);

        // Fail closed until a private host is wired. Accepting an invocation
        // here would advertise a mode that cannot satisfy the guardian contract.
        standardError.WriteLine(UnsupportedModeMessage);
        return UsageExitCode;
    }
}
