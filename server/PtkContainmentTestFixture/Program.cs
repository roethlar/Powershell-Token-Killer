using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PtkContainmentTestFixture;

public static class FixtureAssemblyMarker
{
}

internal static partial class Program
{
    private const string RequestHandleEnvironmentVariable = "PTK_WORKER_REQUEST_HANDLE";
    private const string EventHandleEnvironmentVariable = "PTK_WORKER_EVENT_HANDLE";
    private const string ExactEnvironmentVariable = "PTK_CONTAINMENT_EXACT_ENV";
    private const string ExactEnvironmentValue = "exact-λ-value";
    private const string AmbientLeakEnvironmentVariable = "PTK_CONTAINMENT_AMBIENT_LEAK";
    private const string ExactArgument = "argument λ with \"quote\" and trailing\\";
    private const uint HandleFlagInherit = 0x00000001;
    private const int StandardInputHandle = -10;
    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;
    private static readonly byte[] SpawnCommand = "spawn\n"u8.ToArray();

    private static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["worker", var markerPath, ExactArgument] => RunWorker(markerPath),
                ["descendant"] => RunDescendant(),
                _ => 64,
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"fixture:error:{exception.GetType().Name}");
            return 70;
        }
    }

    private static int RunWorker(string markerPath)
    {
        if (!OperatingSystem.IsWindows()) return 69;
        if (!Path.IsPathFullyQualified(markerPath)) return 64;

        var standardInputHandle = GetRequiredStandardHandle(StandardInputHandle);
        var requestHandleValue = TakeBootstrapHandleValue(RequestHandleEnvironmentVariable);
        var eventHandleValue = TakeBootstrapHandleValue(EventHandleEnvironmentVariable);
        var standardOutputHandle = GetRequiredStandardHandle(StandardOutputHandle);
        var standardErrorHandle = GetRequiredStandardHandle(StandardErrorHandle);

        if (Environment.GetEnvironmentVariable(ExactEnvironmentVariable) != ExactEnvironmentValue ||
            Environment.GetEnvironmentVariable(AmbientLeakEnvironmentVariable) is not null)
        {
            throw new InvalidOperationException("The fixture did not receive its exact closed environment.");
        }

        DisableInheritance(standardInputHandle);
        DisableInheritance(requestHandleValue);
        DisableInheritance(eventHandleValue);
        DisableInheritance(standardOutputHandle);
        DisableInheritance(standardErrorHandle);

        using var requestHandle = new SafeFileHandle(requestHandleValue, ownsHandle: true);
        using var eventHandle = new SafeFileHandle(eventHandleValue, ownsHandle: true);

        using var request = new FileStream(
            requestHandle,
            FileAccess.Read,
            bufferSize: 1,
            isAsync: false);
        using var events = new FileStream(
            eventHandle,
            FileAccess.Write,
            bufferSize: 1,
            isAsync: false);

        if (Console.In.Read() != -1)
            throw new InvalidOperationException("Fixture standard input was not NUL/EOF.");
        WriteEvent(events, "stdin:eof\n");

        File.WriteAllText(markerPath, "entered\n", new UTF8Encoding(false));
        WriteEvent(events, "entered\n");
        Console.Out.WriteLine("fixture:stdout");
        Console.Out.Flush();
        Console.Error.WriteLine("fixture:stderr");
        Console.Error.Flush();

        ReadExactCommand(request, SpawnCommand);
        using var descendant = StartDescendant();
        WriteEvent(events, $"descendant:{descendant.Id.ToString(CultureInfo.InvariantCulture)}\n");

        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static int RunDescendant()
    {
        if (!OperatingSystem.IsWindows()) return 69;
        if (Environment.GetEnvironmentVariable(RequestHandleEnvironmentVariable) is not null ||
            Environment.GetEnvironmentVariable(EventHandleEnvironmentVariable) is not null)
        {
            return 65;
        }

        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static IntPtr TakeBootstrapHandleValue(string variableName)
    {
        var text = Environment.GetEnvironmentVariable(variableName);
        Environment.SetEnvironmentVariable(variableName, null);
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value is 0 or ulong.MaxValue)
        {
            throw new InvalidOperationException($"Missing or invalid {variableName}.");
        }
        return new IntPtr(unchecked((long)value));
    }

    private static IntPtr GetRequiredStandardHandle(int standardHandle)
    {
        var actual = GetStdHandle(standardHandle);
        if (actual == IntPtr.Zero || actual == new IntPtr(-1))
            throw new InvalidOperationException("A fixture standard handle was not mapped.");
        return actual;
    }

    private static void DisableInheritance(IntPtr handle)
    {
        if (!SetHandleInformation(handle, HandleFlagInherit, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void ReadExactCommand(Stream stream, ReadOnlySpan<byte> expected)
    {
        Span<byte> received = stackalloc byte[expected.Length];
        var offset = 0;
        while (offset < received.Length)
        {
            var read = stream.Read(received[offset..]);
            if (read == 0)
                throw new EndOfStreamException("Supervisor request pipe closed before spawn.");
            offset += read;
        }
        if (!received.SequenceEqual(expected))
            throw new InvalidDataException("Unexpected supervisor request.");
    }

    private static void WriteEvent(Stream stream, string value)
    {
        var encoded = Encoding.ASCII.GetBytes(value);
        stream.Write(encoded);
        stream.Flush();
    }

    private static Process StartDescendant()
    {
        var processPath = Environment.ProcessPath ??
            throw new InvalidOperationException("The fixture process path is unavailable.");
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals(
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(entryAssemblyPath))
                throw new InvalidOperationException("The fixture entry assembly path is unavailable.");
            startInfo.ArgumentList.Add(entryAssemblyPath);
        }
        startInfo.ArgumentList.Add("descendant");
        return Process.Start(startInfo) ??
            throw new InvalidOperationException("The fixture descendant did not start.");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(
        IntPtr hObject,
        uint dwMask,
        uint dwFlags);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetStdHandle(int nStdHandle);
}
