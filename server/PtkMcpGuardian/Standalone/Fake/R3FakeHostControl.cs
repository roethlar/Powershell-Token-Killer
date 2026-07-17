using System.Collections.Concurrent;
using PtkMcpGuardian.Lifecycle;
using PtkSharedContracts;

namespace PtkMcpGuardian.Standalone.Fake;

internal sealed record R3FakeHostProfile
{
    private static readonly GuardianHostJobListTarget DefaultTarget = new(
        new CanonicalAlias("default"),
        new SessionTransitionVersion(1),
        new GuardianHostWorkerIdentity(
            new WorkerBootId(Guid.Parse(
                "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")),
            new WorkerGeneration(1)),
        readyForEffects: true);

    internal R3FakeHostProfile(
        GuardianHostJobListTarget jobListTarget,
        Sha256Digest bindingDigest,
        WorkerGenerationHighWatermark workerGenerationHighWatermark,
        bool allowColdBackground = false,
        DesiredSessionState desiredState = DesiredSessionState.Ready)
    {
        JobListTarget = jobListTarget ??
            throw new ArgumentNullException(nameof(jobListTarget));
        BindingDigest = bindingDigest ??
            throw new ArgumentNullException(nameof(bindingDigest));
        WorkerGenerationHighWatermark = workerGenerationHighWatermark ??
            throw new ArgumentNullException(nameof(workerGenerationHighWatermark));
        if (!Enum.IsDefined(desiredState))
            throw new ArgumentOutOfRangeException(nameof(desiredState));
        if (!jobListTarget.ReadyForEffects)
            throw new ArgumentException(
                "The fake host profile must name a ready job-list target.",
                nameof(jobListTarget));
        AllowColdBackground = allowColdBackground;
        DesiredState = desiredState;
    }

    internal GuardianHostJobListTarget JobListTarget { get; }

    internal Sha256Digest BindingDigest { get; }

    internal WorkerGenerationHighWatermark WorkerGenerationHighWatermark { get; }

    internal bool AllowColdBackground { get; }

    internal DesiredSessionState DesiredState { get; }

    internal static R3FakeHostProfile StrictDefault { get; } = new(
        DefaultTarget,
        new Sha256Digest(new string('7', 64)),
        new WorkerGenerationHighWatermark(1));
}

internal sealed class R3FakeHostBarrier
{
    private readonly TaskCompletionSource _reached = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Reached => _reached.Task;

    internal bool IsReached => _reached.Task.IsCompleted;

    internal bool IsReleased => _released.Task.IsCompleted;

    internal void Reach() => _reached.TrySetResult();

    internal void Release() => _released.TrySetResult();

    internal async ValueTask ReachAndWaitAsync(CancellationToken cancellationToken)
    {
        Reach();
        await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal enum R3FakeHostHelloFault
{
    None,
    WrongGuardianBootId,
    WrongHostBootId,
    WrongGeneration,
    WrongExecutableDigest,
    WrongBuildDigest,
    WrongPublicContractDigest,
    WrongConfigurationDigest,
}

internal enum R3FakeHostOperationBehavior
{
    Complete,
    CrashAfterReceive,
    HoldBeforeResponse,
    DuplicateResponse,
    WrongGenerationResponse,
}

internal sealed record R3FakeHostAttemptPlan
{
    internal bool FailPrepare { get; init; }

    internal GuardianHostLaunchOutcome LaunchOutcome { get; init; } =
        GuardianHostLaunchOutcome.Started;

    internal bool ThrowOnLaunch { get; init; }

    internal R3FakeHostHelloFault HelloFault { get; init; }

    internal bool HoldContainmentProof { get; init; }
}

internal sealed record R3FakeHostOperationPlan
{
    internal R3FakeHostOperationBehavior Behavior { get; init; } =
        R3FakeHostOperationBehavior.Complete;

    internal string ResponseText { get; init; } = "[]";

    internal R3FakeHostBarrier Received { get; } = new();

    internal R3FakeHostBarrier BeforeResponse { get; } = new();

    internal R3FakeHostBarrier ResponseSent { get; } = new();
}

/// <summary>
/// Out-of-band deterministic controls for the in-process fake. None of these
/// controls are serialized onto the frozen private protocol.
/// </summary>
internal sealed class R3FakeHostControl
{
    private readonly ConcurrentQueue<R3FakeHostAttemptPlan> _attempts = new();
    private readonly ConcurrentQueue<R3FakeHostOperationPlan> _operations = new();

    internal void EnqueueAttempt(R3FakeHostAttemptPlan plan) =>
        _attempts.Enqueue(plan ?? throw new ArgumentNullException(nameof(plan)));

    internal void EnqueueOperation(R3FakeHostOperationPlan plan) =>
        _operations.Enqueue(plan ?? throw new ArgumentNullException(nameof(plan)));

    internal R3FakeHostAttemptPlan TakeAttempt() =>
        _attempts.TryDequeue(out var plan) ? plan : new R3FakeHostAttemptPlan();

    internal R3FakeHostOperationPlan TakeOperation()
    {
        var plan = _operations.TryDequeue(out var queued)
            ? queued
            : new R3FakeHostOperationPlan();
        if (plan.Behavior != R3FakeHostOperationBehavior.HoldBeforeResponse)
            plan.BeforeResponse.Release();
        return plan;
    }
}
