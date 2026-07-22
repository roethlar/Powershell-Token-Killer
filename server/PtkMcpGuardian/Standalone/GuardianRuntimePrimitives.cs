using PtkMcpGuardian.Lifecycle;
using PtkSharedContracts;

namespace PtkMcpGuardian.Standalone;

internal sealed class RandomGuardianHostBootIdSource : IGuardianHostBootIdSource
{
    public HostBootId Next() => new(Guid.NewGuid());
}

internal sealed class FixedGuardianHostStartupDeadlineSource :
    IGuardianHostStartupDeadlineSource
{
    private readonly TimeProvider _timeProvider;
    private readonly long _timeoutTimestampTicks;

    internal FixedGuardianHostStartupDeadlineSource(
        TimeProvider timeProvider,
        TimeSpan timeout)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeoutTimestampTicks = checked((long)Math.Ceiling(
            timeout.TotalSeconds * timeProvider.TimestampFrequency));
        if (_timeoutTimestampTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public GuardianHostStartupDeadline Next() => new(
        checked(_timeProvider.GetTimestamp() + _timeoutTimestampTicks));
}

internal sealed class SystemGuardianHostSupervisorScheduler(TimeProvider timeProvider) :
    IGuardianHostSupervisorScheduler
{
    private readonly TimeProvider _timeProvider = timeProvider ??
        throw new ArgumentNullException(nameof(timeProvider));

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay));
        return new ValueTask(Task.Delay(delay, _timeProvider, cancellationToken));
    }
}

internal sealed class NoOpGuardianHostSupervisorDispatchObserver :
    IGuardianHostSupervisorDispatchObserver
{
    internal static NoOpGuardianHostSupervisorDispatchObserver Instance { get; } = new();

    private NoOpGuardianHostSupervisorDispatchObserver() { }

    public ValueTask BeforeWriteAuthorizationAsync(
        GuardianHostDispatchObservation observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public void OnWriteStarting(GuardianHostDispatchObservation observation) =>
        ArgumentNullException.ThrowIfNull(observation);

    public void OnTerminalDecoded(GuardianHostDispatchObservation observation) =>
        ArgumentNullException.ThrowIfNull(observation);
}

internal sealed class NoOpGuardianHostLifecycleAudit : IGuardianHostLifecycleAudit
{
    internal static NoOpGuardianHostLifecycleAudit Instance { get; } = new();

    private NoOpGuardianHostLifecycleAudit() { }

    public void RecordStarting() { }
}
