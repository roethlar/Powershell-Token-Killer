using System.Globalization;
using PtkMcpGuardian.Host;
using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Package;
using PtkMcpGuardian.Standalone;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class WindowsPrivateHostProcessLauncherTests
{
    [Fact]
    public void Command_line_quotes_the_exact_apphost_and_private_role()
    {
        var command = Command(Path.Combine(
            Path.GetTempPath(),
            "ptk host & runtime",
            "PtkMcpServer.exe"));

        Assert.Equal(
            $"\"{command.ExecutablePath}\" --host",
            WindowsPrivateHostProcessLauncher.BuildCommandLine(command));
    }

    [Fact]
    public void Environment_block_replaces_stale_bootstrap_case_insensitively_and_is_closed()
    {
        var command = Command(Path.Combine(
            Path.GetTempPath(),
            "ptk-host-launcher",
            "PtkMcpServer.exe"));
        var block = WindowsPrivateHostProcessLauncher.BuildEnvironmentBlockText(
            command.Environment);

        Assert.EndsWith("\0\0", block, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(block, "PTK_HOST_GENERATION="));
        Assert.Contains("PTK_HOST_GENERATION=3\0", block, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PTK_HOST_GENERATION=stale",
            block,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(
            block.IndexOf("A_PARENT=first", StringComparison.Ordinal) <
            block.IndexOf("z_parent=retained", StringComparison.Ordinal));
        Assert.Equal(10, command.BootstrapEnvironment.Count);
        Assert.All(
            command.BootstrapEnvironment.Keys,
            name => Assert.Contains($"{name}=", block, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Windows_launcher_starts_the_real_private_host_in_outer_containment()
    {
        if (!OperatingSystem.IsWindows()) return;

        var appHost = FindServerAppHost();
        var identity = Identity();
        var resources = Assert.IsAssignableFrom<IGuardianHostConnectedAttemptResources>(
            new PrivateHostAttemptFactory(
                    Package(appHost),
                    Pins(),
                    new WindowsPrivateHostProcessLauncher())
                .Prepare(identity, new GuardianHostStartupDeadline(long.MaxValue)));
        using (resources)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            Assert.Equal(GuardianHostLaunchOutcome.Started, resources.Launch());
            var reader = new GuardianHostProtocolReader(
                resources.EventStream,
                GuardianHostPeer.Host);
            var hello = Assert.IsType<GuardianHostHello>(
                await reader.ReadAsync(timeout.Token));

            Assert.Equal(identity.GuardianBootId, hello.GuardianBootId);
            Assert.Equal(identity.HostBootId, hello.HostBootId);
            Assert.Equal(identity.HostGeneration, hello.HostGeneration);
            Assert.Equal(resources.HostProcessId, hello.HostPid);
            Assert.True(resources.HostProcessId > 0);

            resources.BeginContainment(new GuardianHostContainmentDeadline(0, long.MaxValue));
            await resources.HostExited.WaitAsync(timeout.Token);
            await resources.ContainmentConfirmed.WaitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task Containment_confirmation_waits_for_every_captured_job_identity()
    {
        if (!OperatingSystem.IsWindows()) return;

        var tracker = new GatedContainmentTracker();
        var identity = Identity();
        var resources = Assert.IsAssignableFrom<IGuardianHostConnectedAttemptResources>(
            new PrivateHostAttemptFactory(
                    Package(FindServerAppHost()),
                    Pins(),
                    new WindowsPrivateHostProcessLauncher(tracker))
                .Prepare(identity, new GuardianHostStartupDeadline(long.MaxValue)));
        using (resources)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            Assert.Equal(GuardianHostLaunchOutcome.Started, resources.Launch());
            var reader = new GuardianHostProtocolReader(
                resources.EventStream,
                GuardianHostPeer.Host);
            _ = Assert.IsType<GuardianHostHello>(
                await reader.ReadAsync(timeout.Token));

            resources.BeginContainment(
                new GuardianHostContainmentDeadline(0, long.MaxValue));
            await resources.HostExited.WaitAsync(timeout.Token);

            Assert.Equal(1, tracker.CaptureCount);
            Assert.False(resources.ContainmentConfirmed.IsCompleted);
            tracker.Confirm();
            await resources.ContainmentConfirmed.WaitAsync(timeout.Token);
        }
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string FindServerAppHost()
    {
        var configurationDirectory = Directory.GetParent(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) ??
            throw new InvalidOperationException("The test configuration directory is unavailable.");
        var repositoryRoot = FindRepositoryRoot();
        var path = Path.Combine(
            repositoryRoot,
            "server",
            "PtkMcpServer",
            "bin",
            configurationDirectory.Name,
            "net10.0",
            "PtkMcpServer.exe");
        Assert.True(File.Exists(path), $"The private host apphost is absent: {path}");
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static PrivateHostLaunchCommand Command(string appHost) => new(
        Package(appHost),
        Pins(),
        Identity(),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["z_parent"] = "retained",
            ["ptk_host_generation"] = "stale",
            ["A_PARENT"] = "first",
        },
        requestReadHandle: 101,
        eventWriteHandle: 202);

    private static GuardianHostIdentity Identity() => new(
        new GuardianBootId(Guid.Parse("11111111-1111-4111-8111-111111111111")),
        new HostBootId(Guid.Parse("22222222-2222-4222-8222-222222222222")),
        new HostGeneration(3));

    private static GuardianHostSupervisorPins Pins() => new(
        Digest('1'),
        Digest('2'),
        Digest('3'),
        Digest('4'),
        Digest('5'),
        Digest('6'));

    private static MatchedPackageFacts Package(string appHost) => new(
        appHost,
        Digest('1'),
        Digest('2'),
        Digest('3'),
        Digest('6'),
        []);

    private static Sha256Digest Digest(char value) => new(new string(value, 64));

    private sealed class GatedContainmentTracker : IWindowsJobContainmentTracker
    {
        private readonly TaskCompletionSource _confirmation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _captureCount;

        internal int CaptureCount => Volatile.Read(ref _captureCount);

        internal void Confirm() => _confirmation.TrySetResult();

        public IWindowsJobContainmentLease Capture(nint jobHandle, nint hostHandle)
        {
            Assert.NotEqual(IntPtr.Zero, jobHandle);
            Assert.NotEqual(new IntPtr(-1), jobHandle);
            Assert.NotEqual(IntPtr.Zero, hostHandle);
            Assert.NotEqual(new IntPtr(-1), hostHandle);
            Interlocked.Increment(ref _captureCount);
            return new Lease(_confirmation.Task);
        }

        private sealed class Lease(Task confirmation) :
            IWindowsJobContainmentLease
        {
            public Task Confirmation { get; } = confirmation;

            public void Dispose() { }
        }
    }
}
