using PtkMcpGuardian.Host;
using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Package;
using PtkMcpGuardian.Standalone;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class PrivateHostAttemptFactoryTests
{
    [Fact]
    public async Task Started_attempt_exposes_only_guardian_pipe_ends_and_delegates_containment()
    {
        var process = new RecordingProcess(processId: 4242);
        var launcher = new RecordingLauncher(
            new PrivateHostProcessLaunchResult(
                GuardianHostLaunchOutcome.Started,
                process));
        var factory = new PrivateHostAttemptFactory(Package(), Pins(), launcher);
        var identity = Identity();
        var deadline = new GuardianHostStartupDeadline(1234);

        var resources = Assert.IsAssignableFrom<IGuardianHostConnectedAttemptResources>(
            factory.Prepare(identity, deadline));

        Assert.True(resources.RequestStream.CanWrite);
        Assert.False(resources.RequestStream.CanRead);
        Assert.True(resources.EventStream.CanRead);
        Assert.False(resources.EventStream.CanWrite);
        Assert.Equal(GuardianHostLaunchOutcome.Started, resources.Launch());
        Assert.Equal(4242, resources.HostProcessId);

        var command = Assert.IsType<PrivateHostLaunchCommand>(launcher.Command);
        Assert.Equal(Package().HostAppHostPath, command.ExecutablePath);
        Assert.Equal(["--host"], command.Arguments);
        Assert.Equal(2, command.InheritedHandles.Count);
        Assert.NotEqual(command.InheritedHandles[0], command.InheritedHandles[1]);
        Assert.Equal(
            identity.GuardianBootId.ToString(),
            command.BootstrapEnvironment["PTK_HOST_GUARDIAN_BOOT_ID"]);
        Assert.Equal(
            identity.HostBootId.ToString(),
            command.BootstrapEnvironment["PTK_HOST_BOOT_ID"]);
        Assert.Equal(
            identity.HostGeneration.Value.ToString(),
            command.BootstrapEnvironment["PTK_HOST_GENERATION"]);

        var containmentDeadline = new GuardianHostContainmentDeadline(100, 200);
        resources.CloseTransport();
        resources.BeginContainment(containmentDeadline);
        Assert.Same(containmentDeadline, process.ContainmentDeadline);
        process.CompleteExit();
        process.CompleteContainment();
        await resources.HostExited.WaitAsync(TimeSpan.FromSeconds(5));
        await resources.ContainmentConfirmed.WaitAsync(TimeSpan.FromSeconds(5));

        resources.Dispose();
        resources.Dispose();
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task Proved_no_child_attempt_closes_transport_and_completes_both_waits()
    {
        var launcher = new RecordingLauncher(
            new PrivateHostProcessLaunchResult(
                GuardianHostLaunchOutcome.ProvedNoChild,
                process: null));
        var resources = Assert.IsAssignableFrom<IGuardianHostConnectedAttemptResources>(
            new PrivateHostAttemptFactory(Package(), Pins(), launcher)
                .Prepare(Identity(), new GuardianHostStartupDeadline(1234)));

        Assert.Equal(GuardianHostLaunchOutcome.ProvedNoChild, resources.Launch());
        Assert.True(resources.HostExited.IsCompletedSuccessfully);
        Assert.True(resources.ContainmentConfirmed.IsCompletedSuccessfully);
        Assert.Throws<InvalidOperationException>(() => _ = resources.HostProcessId);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await resources.RequestStream.WriteAsync(new byte[] { 1 }));

        resources.Dispose();
    }

    [Fact]
    public void Launch_result_closes_inconsistent_outcome_and_process_pairs()
    {
        var process = new RecordingProcess(processId: 4242);
        Assert.Throws<ArgumentException>(() =>
            new PrivateHostProcessLaunchResult(
                GuardianHostLaunchOutcome.Started,
                process: null));
        Assert.Throws<ArgumentException>(() =>
            new PrivateHostProcessLaunchResult(
                GuardianHostLaunchOutcome.ProvedNoChild,
                process));
        Assert.Equal(1, process.DisposeCount);
    }

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

    private static MatchedPackageFacts Package() => new(
        Path.Combine(Path.GetTempPath(), "ptk-attempt-factory", "PtkMcpServer"),
        Digest('1'),
        Digest('2'),
        Digest('3'),
        Digest('6'),
        []);

    private static Sha256Digest Digest(char value) => new(new string(value, 64));

    private sealed class RecordingLauncher(PrivateHostProcessLaunchResult result) :
        IPrivateHostProcessLauncher
    {
        internal PrivateHostLaunchCommand? Command { get; private set; }

        public PrivateHostProcessLaunchResult Launch(PrivateHostLaunchCommand command)
        {
            Command = command ?? throw new ArgumentNullException(nameof(command));
            return result;
        }
    }

    private sealed class RecordingProcess(int processId) : IPrivateHostLaunchedProcess
    {
        private readonly TaskCompletionSource _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _containment = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId { get; } = processId;
        public Task Exited => _exited.Task;
        public Task ContainmentConfirmed => _containment.Task;
        internal GuardianHostContainmentDeadline? ContainmentDeadline { get; private set; }
        internal int DisposeCount { get; private set; }

        public void BeginContainment(GuardianHostContainmentDeadline deadline) =>
            ContainmentDeadline = deadline ?? throw new ArgumentNullException(nameof(deadline));

        public void Dispose() => DisposeCount++;

        internal void CompleteExit() => _exited.TrySetResult();
        internal void CompleteContainment() => _containment.TrySetResult();
    }
}
