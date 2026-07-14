using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PtkContainmentTestFixture;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WindowsContainmentIntegrationTests
{
    private static readonly TimeSpan CheckpointTimeout = TimeSpan.FromSeconds(15);
    private const string ExactEnvironmentVariable = "PTK_CONTAINMENT_EXACT_ENV";
    private const string ExactEnvironmentValue = "exact-λ-value";
    private const string AmbientLeakEnvironmentVariable = "PTK_CONTAINMENT_AMBIENT_LEAK";
    private const string ExactArgument = "argument λ with \"quote\" and trailing\\";

    [Fact]
    public async Task Runnable_worker_enters_without_a_proof_resume()
    {
        if (!OperatingSystem.IsWindows()) return;

        var scratch = Path.Combine(
            Path.GetTempPath(),
            $"ptk windows runnable containment {Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        var enteredMarker = Path.Combine(scratch, "entered marker.txt");

        ContainedWindowsWorker? worker = null;
        Process? workerWitness = null;
        var priorAmbientLeak = Environment.GetEnvironmentVariable(AmbientLeakEnvironmentVariable);
        try
        {
            var command = CreateFixtureCommand(enteredMarker);
            Environment.SetEnvironmentVariable(AmbientLeakEnvironmentVariable, "must-not-leak");
            worker = new WindowsProcessTreeSupervisor().Launch(command);
            workerWitness = OpenProcessWitness(worker.ProcessId);

            using var eventReader = CreateReader(worker.EventReader);
            using var outputReader = CreateReader(worker.StandardOutputReader);
            using var errorReader = CreateReader(worker.StandardErrorReader);
            Assert.Equal("stdin:eof", await ReadLineAsync(eventReader));
            Assert.Equal("entered", await ReadLineAsync(eventReader));
            Assert.Equal("fixture:stdout", await ReadLineAsync(outputReader));
            Assert.Equal("fixture:stderr", await ReadLineAsync(errorReader));
            Assert.Equal("entered\n", await File.ReadAllTextAsync(enteredMarker));

            var owner = worker;
            worker = null;
            owner.Dispose();
            await WaitForExitAsync(workerWitness);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AmbientLeakEnvironmentVariable, priorAmbientLeak);
            worker?.Dispose();
            workerWitness?.Dispose();
            DeleteScratchBestEffort(scratch);
        }
    }

    [Fact]
    public async Task Suspended_worker_is_contained_before_entry_and_job_owner_kills_its_tree()
    {
        if (!OperatingSystem.IsWindows()) return;

        var scratch = Path.Combine(
            Path.GetTempPath(),
            $"ptk-windows-containment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        var enteredMarker = Path.Combine(scratch, "entered.marker");

        ContainedWindowsWorker? worker = null;
        Process? workerWitness = null;
        Process? descendantWitness = null;
        var priorAmbientLeak = Environment.GetEnvironmentVariable(AmbientLeakEnvironmentVariable);
        try
        {
            var supervisor = new WindowsProcessTreeSupervisor();
            var command = CreateFixtureCommand(enteredMarker);
            Environment.SetEnvironmentVariable(AmbientLeakEnvironmentVariable, "must-not-leak");
            worker = supervisor.Launch(
                command,
                WindowsProcessCreationMode.SuspendedForContainmentProof);

            // A proof-mode launch returns only after IsProcessInJob succeeds
            // against this worker and this exact Job Object. The primary thread
            // is still suspended, so no managed fixture instruction can have run.
            Assert.False(File.Exists(enteredMarker));
            workerWitness = OpenProcessWitness(worker.ProcessId);
            Assert.False(workerWitness.HasExited);

            worker.ResumeForContainmentProof();

            using var eventReader = CreateReader(worker.EventReader);
            using var outputReader = CreateReader(worker.StandardOutputReader);
            using var errorReader = CreateReader(worker.StandardErrorReader);

            Assert.Equal("stdin:eof", await ReadLineAsync(eventReader));
            Assert.Equal("entered", await ReadLineAsync(eventReader));
            Assert.Equal("fixture:stdout", await ReadLineAsync(outputReader));
            Assert.Equal("fixture:stderr", await ReadLineAsync(errorReader));
            Assert.Equal("entered\n", await File.ReadAllTextAsync(enteredMarker));

            // The fixture is now blocked solely on the private request pipe.
            // Collection/finalization must not close a lost Job Object handle;
            // the returned owner keeps the sole job handle rooted.
            ForceFullCollection();
            Assert.False(workerWitness.HasExited);

            await worker.RequestWriter.WriteAsync("spawn\n"u8.ToArray());
            await worker.RequestWriter.FlushAsync();
            var descendantEvent = await ReadLineAsync(eventReader);
            Assert.StartsWith("descendant:", descendantEvent, StringComparison.Ordinal);
            Assert.True(
                int.TryParse(
                    descendantEvent.AsSpan("descendant:".Length),
                    out var descendantProcessId));

            descendantWitness = OpenProcessWitness(descendantProcessId);
            Assert.False(descendantWitness.HasExited);

            // Process witnesses retain only process handles, never a Job Object
            // handle. Closing the owner's sole job handle must therefore kill
            // both the worker and the ordinary no-breakaway descendant.
            var owner = worker;
            worker = null;
            owner.Dispose();

            await WaitForExitAsync(workerWitness);
            await WaitForExitAsync(descendantWitness);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AmbientLeakEnvironmentVariable, priorAmbientLeak);
            try
            {
                worker?.Dispose();
            }
            finally
            {
                workerWitness?.Dispose();
                descendantWitness?.Dispose();
                DeleteScratchBestEffort(scratch);
            }
        }
    }

    private static WorkerLaunchCommand CreateFixtureCommand(string enteredMarker)
    {
        var fixtureAssembly = typeof(FixtureAssemblyMarker).Assembly.Location;
        var fixtureDirectory = Path.GetDirectoryName(fixtureAssembly) ??
            throw new InvalidOperationException("The containment fixture directory is unavailable.");
        var appHost = Path.Combine(fixtureDirectory, "PtkContainmentTestFixture.exe");

        string executable;
        string[] arguments;
        if (File.Exists(appHost))
        {
            executable = appHost;
            arguments = ["worker", enteredMarker, ExactArgument];
        }
        else
        {
            executable = ResolveDotnetHost();
            arguments = [fixtureAssembly, "worker", enteredMarker, ExactArgument];
        }

        var environment = CaptureCurrentEnvironment().ToList();
        environment.Add(new KeyValuePair<string, string>(
            ExactEnvironmentVariable,
            ExactEnvironmentValue));

        return new WorkerLaunchCommand(
            executable,
            arguments,
            fixtureDirectory,
            environment);
    }

    private static string ResolveDotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) &&
            Path.IsPathFullyQualified(configured) &&
            File.Exists(configured))
        {
            return configured;
        }

        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = runtime.Parent?.Parent?.Parent ??
            throw new InvalidOperationException("The dotnet host directory is unavailable.");
        var inferred = Path.Combine(dotnetRoot.FullName, "dotnet.exe");
        return File.Exists(inferred)
            ? inferred
            : throw new FileNotFoundException("The dotnet host executable is unavailable.", inferred);
    }

    private static IEnumerable<KeyValuePair<string, string>> CaptureCurrentEnvironment()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || entry.Value is not string value ||
                key.Contains('=') ||
                key.Equals(ExactEnvironmentVariable, StringComparison.OrdinalIgnoreCase) ||
                key.Equals(AmbientLeakEnvironmentVariable, StringComparison.OrdinalIgnoreCase) ||
                WorkerBootstrapEnvironment.ReservedHandleVariables.Contains(key))
            {
                continue;
            }
            yield return new KeyValuePair<string, string>(key, value);
        }
    }

    private static StreamReader CreateReader(Stream stream) => new(
        stream,
        Encoding.ASCII,
        detectEncodingFromByteOrderMarks: false,
        bufferSize: 128,
        leaveOpen: true);

    private static async Task<string> ReadLineAsync(StreamReader reader)
    {
        using var cancellation = new CancellationTokenSource(CheckpointTimeout);
        return await reader.ReadLineAsync(cancellation.Token) ??
            throw new EndOfStreamException("A containment fixture stream closed before its checkpoint.");
    }

    private static Process OpenProcessWitness(int processId)
    {
        var process = Process.GetProcessById(processId);
        try
        {
            _ = process.SafeHandle;
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static async Task WaitForExitAsync(Process process)
    {
        using var cancellation = new CancellationTokenSource(CheckpointTimeout);
        await process.WaitForExitAsync(cancellation.Token);
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static void DeleteScratchBestEffort(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // The assertions own correctness. Windows can retain a just-closed
            // diagnostic file briefly; cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException cleanup note above.
        }
    }
}
