using System.Security.Cryptography;
using PtkMcpGuardian.Lifecycle;
using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

internal enum SessionRecoveryTransitionKind
{
    Open,
    Close,
    Restart,
    Reset,
    Bootstrap,
}

internal enum SessionRecoveryLossDisposition
{
    BeganContainment,
    RecoveryUnknown,
    Faulted,
    Duplicate,
    StaleWorker,
    NotEligible,
    Stopped,
}

internal enum SessionRecoveryDeathDisposition
{
    AttemptStarted,
    ContainmentRequired,
    Scheduled,
    NoRecovery,
    Faulted,
    Duplicate,
    StaleLoss,
    Stopped,
}

internal enum SessionRecoveryAttemptDisposition
{
    Ready,
    ContainmentRequired,
    Faulted,
    DeadlineExpired,
    NotDue,
    Duplicate,
    StaleAttempt,
    Stopped,
}

internal enum SessionRecoveryAttemptStartDisposition
{
    AttemptStarted,
    ContainmentRequired,
    NotDue,
    Faulted,
    Stopped,
}

internal enum SessionRecoveryBaselineOutcome
{
    Ready,
    RetryableFailure,
    DeterministicConfigurationFailure,
}

internal enum SessionRecoveryFailureReason
{
    WorkerLost,
    AmbiguousLifecycle,
    BindingMismatch,
    BootstrapFailed,
    AttemptFailed,
    AttemptDeadlineExpired,
    IdentityExhausted,
    IdentityReused,
}

internal enum SessionRecoveryAttemptStage
{
    Preparing,
    Attempting,
    Bootstrapping,
    Completed,
}

internal readonly record struct SessionRecoveryBaselineResult(
    SessionRecoveryBaselineOutcome Outcome)
{
    internal static SessionRecoveryBaselineResult Ready { get; } =
        new(SessionRecoveryBaselineOutcome.Ready);

    internal static SessionRecoveryBaselineResult RetryableFailure { get; } =
        new(SessionRecoveryBaselineOutcome.RetryableFailure);

    internal static SessionRecoveryBaselineResult DeterministicConfigurationFailure { get; } =
        new(SessionRecoveryBaselineOutcome.DeterministicConfigurationFailure);
}

internal sealed record SessionRecoveryBindingProof
{
    internal SessionRecoveryBindingProof(
        Sha256Digest catalogDigest,
        RecoveryBindingKind bindingKind,
        Sha256Digest bindingDigest,
        Sha256Digest? templateDigest,
        Sha256Digest? bootstrapDigest)
    {
        ArgumentNullException.ThrowIfNull(catalogDigest);
        ArgumentNullException.ThrowIfNull(bindingDigest);
        if (!Enum.IsDefined(bindingKind))
            throw new ArgumentOutOfRangeException(nameof(bindingKind));

        var hasTemplateDigests = templateDigest is not null && bootstrapDigest is not null;
        if (bindingKind == RecoveryBindingKind.Template != hasTemplateDigests ||
            bindingKind != RecoveryBindingKind.Template &&
            (templateDigest is not null || bootstrapDigest is not null))
        {
            throw new ArgumentException(
                "Binding proof template digests do not match its binding kind.");
        }

        CatalogDigest = catalogDigest;
        BindingKind = bindingKind;
        BindingDigest = bindingDigest;
        TemplateDigest = templateDigest;
        BootstrapDigest = bootstrapDigest;
    }

    internal Sha256Digest CatalogDigest { get; }
    internal RecoveryBindingKind BindingKind { get; }
    internal Sha256Digest BindingDigest { get; }
    internal Sha256Digest? TemplateDigest { get; }
    internal Sha256Digest? BootstrapDigest { get; }

    internal static SessionRecoveryBindingProof From(
        Sha256Digest catalogDigest,
        RecoveryBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new SessionRecoveryBindingProof(
            catalogDigest,
            binding.BindingKind,
            binding.BindingDigest,
            binding.TemplateDigest,
            binding.BootstrapDigest);
    }
}

internal sealed record SessionRecoveryAttemptDeadline(
    long StartedTimestamp,
    long AbsoluteTimestamp,
    TimeSpan Timeout);

/// <summary>
/// Exact launch correlation frozen for one recovery attempt. The desired state
/// and transition version stored inside <see cref="FrozenBinding"/> are
/// manifest-time evidence only; launch/capability correlation must use
/// <see cref="DesiredState"/> and <see cref="TransitionVersion"/> from this
/// context so acknowledged lifecycle changes cannot be rolled back.
/// </summary>
internal sealed record SessionRecoveryAttemptContext
{
    internal SessionRecoveryAttemptContext(
        RecoveryBinding frozenBinding,
        DesiredSessionState desiredState,
        SessionTransitionVersion transitionVersion)
    {
        FrozenBinding = frozenBinding ??
            throw new ArgumentNullException(nameof(frozenBinding));
        if (!Enum.IsDefined(desiredState))
            throw new ArgumentOutOfRangeException(nameof(desiredState));
        DesiredState = desiredState;
        TransitionVersion = transitionVersion ??
            throw new ArgumentNullException(nameof(transitionVersion));
    }

    internal RecoveryBinding FrozenBinding { get; }
    internal CanonicalAlias Alias => FrozenBinding.Alias;
    internal DesiredSessionState DesiredState { get; }
    internal SessionTransitionVersion TransitionVersion { get; }
}

internal interface ISessionRecoveryWorkerBootIdSource
{
    WorkerBootId Next();
}

/// <summary>
/// The owning containment seam for one deliberately unwired recovery attempt.
/// The callback must return only after the supplied baseline bytes have been
/// consumed; the state machine clears those bytes immediately after the
/// callback returns or throws.
/// </summary>
internal interface ISessionRecoveryPreparedAttempt : IDisposable
{
    SessionRecoveryBaselineResult RestoreBaseline(
        ReadOnlyMemory<byte> exactBootstrapBytes);

    /// <summary>
    /// Initiates containment and completes only after this attempt's worker
    /// tree is confirmed dead. It must be safe while RestoreBaseline is still
    /// running. Dispose is called only after this confirmation or an externally
    /// reported confirmed-death transition.
    /// </summary>
    ValueTask ContainAndConfirmDeathAsync();
}

internal interface ISessionRecoveryAttemptFactory
{
    /// <summary>
    /// Prepares one attempt. Throwing must prove that no worker or effect was
    /// created; once a prepared attempt is returned, it owns the containment
    /// boundary until the state machine disposes it after confirmed death.
    /// </summary>
    ISessionRecoveryPreparedAttempt Prepare(
        SessionRecoveryAttemptContext context,
        GuardianHostWorkerIdentity workerIdentity,
        SessionRecoveryAttemptDeadline deadline);
}

internal sealed class SessionRecoveryTransitionLease
{
    internal SessionRecoveryTransitionLease(
        long sequence,
        SessionRecoveryTransitionKind kind,
        SessionTransitionVersion expectedTransitionVersion,
        GuardianHostWorkerIdentity? workerIdentity)
    {
        Sequence = sequence;
        Kind = kind;
        ExpectedTransitionVersion = expectedTransitionVersion ??
            throw new ArgumentNullException(nameof(expectedTransitionVersion));
        SourceWorkerIdentity = workerIdentity;
        WorkerIdentity = workerIdentity;
    }

    internal long Sequence { get; }
    internal SessionRecoveryTransitionKind Kind { get; }
    internal SessionTransitionVersion ExpectedTransitionVersion { get; }
    internal GuardianHostWorkerIdentity? SourceWorkerIdentity { get; }
    internal GuardianHostWorkerIdentity? WorkerIdentity { get; set; }
    internal bool Dispatched { get; set; }
    internal bool SourceDeathConfirmed { get; set; }
    internal bool TargetDeathConfirmed { get; set; }
}

internal sealed class SessionRecoveryLossLease
{
    internal SessionRecoveryLossLease(
        long sequence,
        GuardianHostWorkerIdentity workerIdentity,
        bool eligibleForRecovery,
        RecoveryAttemptLease? failedRecoveryLease,
        bool stabilityFrozen,
        ISessionRecoveryPreparedAttempt? resources,
        Task preparationCompletion,
        Task restoreCompletion,
        long nextAttemptOrdinal)
    {
        Sequence = sequence;
        WorkerIdentity = workerIdentity;
        EligibleForRecovery = eligibleForRecovery;
        FailedRecoveryLease = failedRecoveryLease;
        StabilityFrozen = stabilityFrozen;
        Resources = resources;
        PreparationCompletion = preparationCompletion ??
            throw new ArgumentNullException(nameof(preparationCompletion));
        RestoreCompletion = restoreCompletion ??
            throw new ArgumentNullException(nameof(restoreCompletion));
        if (nextAttemptOrdinal <= 0)
            throw new ArgumentOutOfRangeException(nameof(nextAttemptOrdinal));
        NextAttemptOrdinal = nextAttemptOrdinal;
    }

    internal long Sequence { get; }
    internal GuardianHostWorkerIdentity WorkerIdentity { get; }
    internal bool EligibleForRecovery { get; }
    internal RecoveryAttemptLease? FailedRecoveryLease { get; }
    internal bool StabilityFrozen { get; }
    internal ISessionRecoveryPreparedAttempt? Resources { get; }
    internal Task PreparationCompletion { get; }
    internal Task RestoreCompletion { get; }
    internal long NextAttemptOrdinal { get; }
    internal bool DeathConfirmationStarted { get; set; }
    internal bool DeathConfirmed { get; set; }
}

internal sealed class SessionRecoveryAttemptLease
{
    internal SessionRecoveryAttemptLease(
        long sequence,
        GuardianHostWorkerIdentity workerIdentity,
        RecoveryAttemptLease recoveryLease,
        SessionRecoveryAttemptContext context,
        SessionRecoveryAttemptDeadline deadline)
    {
        Sequence = sequence;
        WorkerIdentity = workerIdentity;
        RecoveryLease = recoveryLease;
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Deadline = deadline;
        Stage = SessionRecoveryAttemptStage.Preparing;
    }

    internal long Sequence { get; }
    internal GuardianHostWorkerIdentity WorkerIdentity { get; }
    internal RecoveryAttemptLease RecoveryLease { get; }
    internal SessionRecoveryAttemptContext Context { get; }
    internal SessionRecoveryAttemptDeadline Deadline { get; }
    internal SessionRecoveryAttemptStage Stage { get; set; }
    internal ISessionRecoveryPreparedAttempt? PreparedAttempt { get; set; }
    internal bool BaselineStarted { get; set; }
    internal SessionRecoveryLossLease? ContainmentLoss { get; set; }
    internal TaskCompletionSource PreparationCompletion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource RestoreCompletion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal readonly record struct SessionRecoveryLossTransition(
    SessionRecoveryLossDisposition Disposition,
    SessionRecoveryLossLease? Loss);

internal readonly record struct SessionRecoveryDeathTransition(
    SessionRecoveryDeathDisposition Disposition,
    SessionRecoveryAttemptLease? Attempt,
    SessionRecoveryLossLease? Loss = null);

internal readonly record struct SessionRecoveryAttemptTransition(
    SessionRecoveryAttemptDisposition Disposition,
    SessionRecoveryLossLease? Loss);

internal readonly record struct SessionRecoveryAttemptStartTransition(
    SessionRecoveryAttemptStartDisposition Disposition,
    SessionRecoveryAttemptLease? Attempt,
    SessionRecoveryLossLease? Loss);

internal sealed record RejectedPreparationCleanup(
    GuardianHostWorkerIdentity WorkerIdentity,
    ISessionRecoveryPreparedAttempt Resource,
    Task Completion);

internal sealed record ResourceHandoffFailure(
    ISessionRecoveryPreparedAttempt Resource,
    Exception Exception,
    Task Completion);

internal sealed class SessionRecoveryIdentityReservation
{
    internal SessionRecoveryIdentityReservation(
        long sequence,
        RecoveryAttemptLease recoveryLease,
        SessionRecoveryAttemptContext context,
        SessionRecoveryAttemptDeadline deadline)
    {
        Sequence = sequence;
        RecoveryLease = recoveryLease ??
            throw new ArgumentNullException(nameof(recoveryLease));
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Deadline = deadline ?? throw new ArgumentNullException(nameof(deadline));
    }

    internal long Sequence { get; }
    internal RecoveryAttemptLease RecoveryLease { get; }
    internal SessionRecoveryAttemptContext Context { get; }
    internal SessionRecoveryAttemptDeadline Deadline { get; }
    internal TaskCompletionSource Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed record SessionRecoveryStateMachineSnapshot(
    PublicSessionStateSnapshot Session,
    RecoveryCircuitState CircuitState,
    long ConsecutiveFailedGenerations,
    bool TerminalShutdown,
    SessionRecoveryFailureReason? LastFailureReason,
    SessionRecoveryTransitionKind? ActiveTransition,
    bool ActiveTransitionDispatched,
    bool AwaitingOldTreeDeath);

/// <summary>
/// One transport-neutral, deliberately unwired automatic-recovery controller
/// for one alias. Every mutable field is protected by one alias gate. External
/// completion is reference-fenced by transition, loss, and attempt leases.
/// This type neither launches a process nor references the mutable session runtime.
/// </summary>
internal sealed class SessionRecoveryStateMachine : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly RecoveryBinding _frozenBinding;
    private readonly SessionRecoveryBindingProof _frozenBindingProof;
    private readonly byte[] _frozenBootstrapBytes;
    private readonly TimeSpan _defaultAttemptTimeout;
    private readonly IWorkerGenerationAllocator _generationAllocator;
    private readonly ISessionRecoveryWorkerBootIdSource _workerBootIdSource;
    private readonly ISessionRecoveryAttemptFactory _attemptFactory;
    private RecoveryCircuitMachine _circuit;

    private DesiredSessionState _desiredState;
    private PublicSessionState _state;
    private SessionTransitionVersion _transitionVersion;
    private BootstrapState _bootstrapState;
    private GuardianHostWorkerIdentity? _currentIdentity;
    private RecoveryAttemptLease? _currentRecoveryLease;
    private ISessionRecoveryPreparedAttempt? _currentResources;
    private SessionRecoveryTransitionLease? _activeTransition;
    private SessionRecoveryLossLease? _activeLoss;
    private SessionRecoveryLossLease? _secondaryTransitionLoss;
    private SessionRecoveryAttemptLease? _activeAttempt;
    private SessionRecoveryIdentityReservation? _activeIdentityReservation;
    private SessionRecoveryFailureReason? _lastFailureReason;
    private long _generationHighWatermark;
    private long _leaseSequence;
    private bool _hasObservedGeneration;
    private bool _everReady;
    private bool _warmStateLost;
    private bool _terminalShutdown;
    private TaskCompletionSource? _shutdownCompletion;
    private TaskCompletionSource? _resourceHandoffCompletion;
    private RejectedPreparationCleanup? _rejectedPreparationCleanup;
    private ResourceHandoffFailure? _resourceHandoffFailure;

    internal SessionRecoveryStateMachine(
        Sha256Digest frozenCatalogDigest,
        RecoveryBinding frozenBinding,
        RecoveryTemplate? frozenTemplate,
        WorkerGenerationHighWatermark initialGenerationHighWatermark,
        TimeSpan defaultAttemptTimeout,
        TimeProvider timeProvider,
        IWorkerGenerationAllocator generationAllocator,
        ISessionRecoveryWorkerBootIdSource workerBootIdSource,
        ISessionRecoveryAttemptFactory attemptFactory)
    {
        ArgumentNullException.ThrowIfNull(frozenCatalogDigest);
        ArgumentNullException.ThrowIfNull(frozenBinding);
        ArgumentNullException.ThrowIfNull(initialGenerationHighWatermark);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(generationAllocator);
        ArgumentNullException.ThrowIfNull(workerBootIdSource);
        ArgumentNullException.ThrowIfNull(attemptFactory);
        if (defaultAttemptTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(defaultAttemptTimeout));

        ValidateTemplateBinding(frozenBinding, frozenTemplate);

        _timeProvider = timeProvider;
        _frozenBinding = frozenBinding;
        _frozenBindingProof = SessionRecoveryBindingProof.From(
            frozenCatalogDigest,
            frozenBinding);
        _frozenBootstrapBytes = frozenTemplate?.GetBootstrapBytes() ?? [];
        _defaultAttemptTimeout = defaultAttemptTimeout;
        _generationAllocator = generationAllocator;
        _workerBootIdSource = workerBootIdSource;
        _attemptFactory = attemptFactory;
        _circuit = new RecoveryCircuitMachine(timeProvider);
        _desiredState = frozenBinding.DesiredState;
        _state = PublicSessionState.Cold;
        _transitionVersion = frozenBinding.TransitionVersion;
        _bootstrapState = IsTemplate
            ? BootstrapState.Pending
            : BootstrapState.NotApplicable;
        _generationHighWatermark = initialGenerationHighWatermark.Value;
    }

    private bool IsTemplate =>
        _frozenBinding.BindingKind == RecoveryBindingKind.Template;

    internal SessionRecoveryBindingProof FrozenBindingProof =>
        _frozenBindingProof;

    internal bool RecordAcknowledgedReady(
        GuardianHostWorkerIdentity workerIdentity,
        SessionTransitionVersion transitionVersion,
        BootstrapState bootstrapState)
    {
        ArgumentNullException.ThrowIfNull(workerIdentity);
        ArgumentNullException.ThrowIfNull(transitionVersion);
        ValidateReadyBootstrapState(bootstrapState);

        lock (_gate)
        {
            var sameCurrentIdentity = _currentIdentity is not null &&
                SameIdentity(_currentIdentity, workerIdentity);
            if (_terminalShutdown ||
                _everReady ||
                _state != PublicSessionState.Cold ||
                _desiredState != DesiredSessionState.Ready ||
                _activeLoss is not null ||
                _secondaryTransitionLoss is not null ||
                _activeAttempt is not null ||
                _activeIdentityReservation is not null ||
                _activeTransition is not null ||
                HasUnresolvedCleanupLocked() ||
                transitionVersion != _transitionVersion ||
                _currentResources is not null && !sameCurrentIdentity ||
                !AcceptObservedIdentityLocked(workerIdentity))
            {
                return false;
            }

            _currentIdentity = workerIdentity;
            if (!sameCurrentIdentity)
            {
                _currentRecoveryLease = null;
                _currentResources = null;
            }
            _transitionVersion = transitionVersion;
            _desiredState = DesiredSessionState.Ready;
            _state = PublicSessionState.Ready;
            _bootstrapState = bootstrapState;
            _everReady = true;
            return true;
        }
    }

    internal bool RecordAcknowledgedCold(SessionTransitionVersion transitionVersion)
    {
        ArgumentNullException.ThrowIfNull(transitionVersion);
        lock (_gate)
        {
            if (_terminalShutdown ||
                _everReady ||
                _desiredState != DesiredSessionState.Cold ||
                _state != PublicSessionState.Cold ||
                _activeLoss is not null ||
                _secondaryTransitionLoss is not null ||
                _activeAttempt is not null ||
                _activeIdentityReservation is not null ||
                _activeTransition is not null ||
                HasUnresolvedCleanupLocked() ||
                transitionVersion != _transitionVersion)
            {
                return false;
            }

            _transitionVersion = transitionVersion;
            _desiredState = DesiredSessionState.Cold;
            _state = PublicSessionState.Cold;
            _bootstrapState = IsTemplate
                ? BootstrapState.Pending
                : BootstrapState.NotApplicable;
            return true;
        }
    }

    internal bool RecordExplicitlyFaulted(SessionTransitionVersion transitionVersion)
    {
        ArgumentNullException.ThrowIfNull(transitionVersion);
        lock (_gate)
        {
            if (_terminalShutdown ||
                _activeLoss is not null ||
                _secondaryTransitionLoss is not null ||
                _activeAttempt is not null ||
                _activeIdentityReservation is not null ||
                _activeTransition is not null ||
                HasUnresolvedCleanupLocked() ||
                !IsExpectedExplicitFaultVersionLocked(transitionVersion))
            {
                return false;
            }

            _transitionVersion = transitionVersion;
            _state = PublicSessionState.Faulted;
            _bootstrapState = IsTemplate
                ? BootstrapState.Failed
                : BootstrapState.NotApplicable;
            _lastFailureReason = SessionRecoveryFailureReason.BootstrapFailed;
            _circuit.Stop();
            return true;
        }
    }

    internal SessionRecoveryTransitionLease? BeginLifecycleTransition(
        SessionRecoveryTransitionKind kind,
        SessionRecoveryBindingProof? repairBindingProof = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));

        lock (_gate)
        {
            if (_terminalShutdown ||
                _activeTransition is not null ||
                _activeLoss is not null ||
                _secondaryTransitionLoss is not null ||
                _activeAttempt is not null ||
                _activeIdentityReservation is not null ||
                _resourceHandoffCompletion is not null ||
                HasUnresolvedCleanupLocked() ||
                _state is (
                    PublicSessionState.Faulted or
                    PublicSessionState.RecoveryUnknown) &&
                    (repairBindingProof is null ||
                        !BindingMatches(repairBindingProof)) ||
                !TransitionAllowedLocked(kind))
            {
                return null;
            }

            SessionTransitionVersion expectedTransitionVersion;
            try
            {
                expectedTransitionVersion = new SessionTransitionVersion(
                    checked(_transitionVersion.Value + 1));
            }
            catch (OverflowException)
            {
                return null;
            }

            var lease = new SessionRecoveryTransitionLease(
                NextLeaseSequenceLocked(),
                kind,
                expectedTransitionVersion,
                _currentIdentity);
            _activeTransition = lease;
            return lease;
        }
    }

    internal bool AbandonUndispatchedLifecycleTransition(
        SessionRecoveryTransitionLease transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        lock (_gate)
        {
            if (_terminalShutdown ||
                !ReferenceEquals(_activeTransition, transition) ||
                transition.Dispatched)
            {
                return false;
            }

            // Any identity attached before local dispatch has already consumed
            // its generation. Clearing the lease must never lower the high-water
            // mark or make that generation reusable.
            _activeTransition = null;
            return true;
        }
    }

    internal bool MarkLifecycleDispatched(SessionRecoveryTransitionLease transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        lock (_gate)
        {
            if (_terminalShutdown ||
                !ReferenceEquals(_activeTransition, transition) ||
                transition.Dispatched ||
                transition.Kind is (
                    SessionRecoveryTransitionKind.Open or
                    SessionRecoveryTransitionKind.Restart or
                    SessionRecoveryTransitionKind.Reset) &&
                    (transition.WorkerIdentity is null ||
                        transition.SourceWorkerIdentity is not null &&
                        SameIdentity(
                            transition.WorkerIdentity,
                            transition.SourceWorkerIdentity)))
            {
                return false;
            }

            transition.Dispatched = true;
            _transitionVersion = transition.ExpectedTransitionVersion;
            _state = transition.Kind switch
            {
                SessionRecoveryTransitionKind.Open => PublicSessionState.Starting,
                SessionRecoveryTransitionKind.Close => PublicSessionState.Closing,
                SessionRecoveryTransitionKind.Restart or
                SessionRecoveryTransitionKind.Reset => PublicSessionState.Resetting,
                SessionRecoveryTransitionKind.Bootstrap =>
                    PublicSessionState.Bootstrapping,
                _ => throw new InvalidOperationException(
                    "Unknown lifecycle transition kind."),
            };
            if (transition.Kind == SessionRecoveryTransitionKind.Bootstrap && IsTemplate)
                _bootstrapState = BootstrapState.Pending;
            return true;
        }
    }

    internal bool AttachLifecycleWorkerIdentity(
        SessionRecoveryTransitionLease transition,
        GuardianHostWorkerIdentity workerIdentity)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(workerIdentity);
        lock (_gate)
        {
            if (_terminalShutdown ||
                !ReferenceEquals(_activeTransition, transition) ||
                transition.Kind is not (
                    SessionRecoveryTransitionKind.Open or
                    SessionRecoveryTransitionKind.Restart or
                    SessionRecoveryTransitionKind.Reset) ||
                transition.Dispatched ||
                (transition.WorkerIdentity is not null &&
                    !ReferenceEquals(
                        transition.WorkerIdentity,
                        transition.SourceWorkerIdentity)) ||
                workerIdentity.Generation.Value <= _generationHighWatermark)
            {
                return false;
            }

            _generationHighWatermark = workerIdentity.Generation.Value;
            _hasObservedGeneration = true;
            transition.WorkerIdentity = workerIdentity;
            return true;
        }
    }

    internal bool CompleteLifecycleTerminal(
        SessionRecoveryTransitionLease transition,
        PublicSessionState observedState,
        GuardianHostWorkerIdentity? observedIdentity,
        SessionTransitionVersion transitionVersion,
        BootstrapState bootstrapState,
        bool oldWorkerDeathConfirmed)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(transitionVersion);
        if (!Enum.IsDefined(observedState))
            throw new ArgumentOutOfRangeException(nameof(observedState));
        if (!Enum.IsDefined(bootstrapState))
            throw new ArgumentOutOfRangeException(nameof(bootstrapState));

        ISessionRecoveryPreparedAttempt? deadResources = null;
        TaskCompletionSource? handoff = null;
        lock (_gate)
        {
            if (_terminalShutdown ||
                !ReferenceEquals(_activeTransition, transition) ||
                !transition.Dispatched ||
                _activeLoss is not null ||
                _secondaryTransitionLoss is not null ||
                _activeAttempt is not null ||
                _activeIdentityReservation is not null ||
                transitionVersion != transition.ExpectedTransitionVersion ||
                _resourceHandoffCompletion is not null ||
                !LifecycleTerminalMatchesLocked(
                    transition,
                    observedState,
                    observedIdentity,
                    bootstrapState,
                    oldWorkerDeathConfirmed))
            {
                return false;
            }

            var identityChanged = observedIdentity is not null &&
                (_currentIdentity is null ||
                    !SameIdentity(_currentIdentity, observedIdentity));
            var identityWasAttached = observedIdentity is not null &&
                transition.WorkerIdentity is not null &&
                SameIdentity(transition.WorkerIdentity, observedIdentity);
            if (identityChanged && !identityWasAttached &&
                !AcceptObservedIdentityLocked(observedIdentity!))
                return false;

            if (oldWorkerDeathConfirmed && _currentResources is not null &&
                (observedState != PublicSessionState.Ready || identityChanged))
            {
                deadResources = _currentResources;
                _currentResources = null;
                _currentRecoveryLease = null;
                _resourceHandoffCompletion = handoff = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _transitionVersion = transitionVersion;
            _state = observedState;
            if (observedState == PublicSessionState.Ready)
            {
                if (identityChanged)
                    _circuit = new RecoveryCircuitMachine(_timeProvider);
                _currentIdentity = observedIdentity;
                _desiredState = DesiredSessionState.Ready;
                _bootstrapState = bootstrapState;
                _everReady = true;
            }
            else if (observedState == PublicSessionState.Cold)
            {
                _currentIdentity = null;
                _currentRecoveryLease = null;
                _desiredState = DesiredSessionState.Cold;
                _bootstrapState = IsTemplate
                    ? BootstrapState.Pending
                    : BootstrapState.NotApplicable;
            }
            else
            {
                if (oldWorkerDeathConfirmed)
                {
                    _currentIdentity = null;
                    _currentRecoveryLease = null;
                }
                _bootstrapState = IsTemplate
                    ? BootstrapState.Failed
                    : BootstrapState.NotApplicable;
                _lastFailureReason = SessionRecoveryFailureReason.BootstrapFailed;
                _circuit.Stop();
            }

            _activeTransition = null;
        }

        if (deadResources is not null &&
            !CompleteResourceHandoff(deadResources, handoff!))
        {
            return false;
        }
        return true;
    }

    internal SessionRecoveryLossTransition BeginUnexpectedLoss(
        GuardianHostWorkerIdentity workerIdentity,
        SessionRecoveryBindingProof observedBinding)
    {
        ArgumentNullException.ThrowIfNull(workerIdentity);
        ArgumentNullException.ThrowIfNull(observedBinding);

        lock (_gate)
        {
            if (_terminalShutdown)
            {
                return new SessionRecoveryLossTransition(
                    SessionRecoveryLossDisposition.Stopped,
                    null);
            }

            if (_secondaryTransitionLoss is not null &&
                SameIdentity(_secondaryTransitionLoss.WorkerIdentity, workerIdentity))
            {
                return new SessionRecoveryLossTransition(
                    SessionRecoveryLossDisposition.Duplicate,
                    null);
            }

            if (_activeLoss is not null)
            {
                if (SameIdentity(_activeLoss.WorkerIdentity, workerIdentity))
                {
                    return new SessionRecoveryLossTransition(
                        SessionRecoveryLossDisposition.Duplicate,
                        null);
                }

                var counterpartLoss =
                    TryBeginAmbiguousTransitionCounterpartLossLocked(workerIdentity);
                return counterpartLoss is not null
                    ? new SessionRecoveryLossTransition(
                        SessionRecoveryLossDisposition.RecoveryUnknown,
                        counterpartLoss)
                    : new SessionRecoveryLossTransition(
                        SessionRecoveryLossDisposition.StaleWorker,
                        null);
            }

            var sourceAttempt = _activeAttempt;
            var sourceIsAttempt = sourceAttempt is not null &&
                SameIdentity(sourceAttempt.WorkerIdentity, workerIdentity);
            var sourceIsReady = _currentIdentity is not null &&
                SameIdentity(_currentIdentity, workerIdentity);
            var transitionSourceMatches =
                _activeTransition?.SourceWorkerIdentity is not null &&
                SameIdentity(
                    _activeTransition.SourceWorkerIdentity,
                    workerIdentity);
            var transitionTargetMatches =
                _activeTransition?.WorkerIdentity is not null &&
                SameIdentity(_activeTransition.WorkerIdentity, workerIdentity);
            var distinctTransitionTarget = transitionTargetMatches &&
                !transitionSourceMatches;
            var transitionHasDistinctTarget =
                _activeTransition?.WorkerIdentity is not null &&
                (_activeTransition.SourceWorkerIdentity is null ||
                    !SameIdentity(
                        _activeTransition.SourceWorkerIdentity,
                        _activeTransition.WorkerIdentity));
            if (distinctTransitionTarget &&
                _activeTransition is { Dispatched: false })
            {
                // Attachment reserves an identity; it does not prove that the
                // replacement was launched. A pre-dispatch target loss cannot
                // consume the still-live source or start speculative recovery.
                return new SessionRecoveryLossTransition(
                    SessionRecoveryLossDisposition.StaleWorker,
                    null);
            }

            if (HasUnresolvedCleanupLocked())
            {
                return new SessionRecoveryLossTransition(
                    SessionRecoveryLossDisposition.StaleWorker,
                    null);
            }
            var sourceIsTransition = transitionSourceMatches ||
                transitionTargetMatches &&
                    _activeTransition is { Dispatched: true };
            if (!sourceIsAttempt && !sourceIsReady && !sourceIsTransition)
            {
                return new SessionRecoveryLossTransition(
                    _currentIdentity is null && _activeAttempt is null
                        ? SessionRecoveryLossDisposition.NotEligible
                        : SessionRecoveryLossDisposition.StaleWorker,
                    null);
            }

            var bindingMatches = BindingMatches(observedBinding);
            var ambiguousTransition = sourceIsTransition &&
                _activeTransition is { Dispatched: true };
            var ambiguousBootstrap = sourceIsAttempt && sourceAttempt!.BaselineStarted;
            var recoveryUnknown = ambiguousTransition || ambiguousBootstrap;
            var eligible = bindingMatches &&
                !recoveryUnknown &&
                _everReady &&
                _desiredState == DesiredSessionState.Ready &&
                (sourceIsAttempt || _state == PublicSessionState.Ready);

            RecoveryAttemptLease? failedRecoveryLease = null;
            ISessionRecoveryPreparedAttempt? resources = null;
            var preparationCompletion = Task.CompletedTask;
            var restoreCompletion = Task.CompletedTask;
            var stabilityFrozen = false;
            if (sourceIsAttempt)
            {
                failedRecoveryLease = sourceAttempt!.RecoveryLease;
                resources = sourceAttempt.PreparedAttempt;
                preparationCompletion = sourceAttempt.PreparationCompletion.Task;
                restoreCompletion = sourceAttempt.BaselineStarted
                    ? sourceAttempt.RestoreCompletion.Task
                    : Task.CompletedTask;
                sourceAttempt.Stage = SessionRecoveryAttemptStage.Completed;
                _activeAttempt = null;
            }
            else if (sourceIsReady && _currentRecoveryLease is not null)
            {
                failedRecoveryLease = _currentRecoveryLease;
                stabilityFrozen = _circuit.FreezeReadyStability(
                    _currentRecoveryLease);
                _currentRecoveryLease = null;
                resources = _currentResources;
                _currentResources = null;
            }

            var circuitAtLoss = _circuit.Snapshot();
            var nextAttemptOrdinal = failedRecoveryLease is null ||
                circuitAtLoss.StabilityReset
                ? 1
                : checked(failedRecoveryLease.AttemptOrdinal + 1);

            var retainAmbiguousTransition = recoveryUnknown &&
                ambiguousTransition &&
                _activeTransition?.SourceWorkerIdentity is not null &&
                transitionHasDistinctTarget;
            if (!retainAmbiguousTransition)
                _activeTransition = null;
            var loss = new SessionRecoveryLossLease(
                NextLeaseSequenceLocked(),
                workerIdentity,
                eligible,
                failedRecoveryLease,
                stabilityFrozen,
                resources,
                preparationCompletion,
                restoreCompletion,
                nextAttemptOrdinal);
            if (sourceAttempt is not null)
                sourceAttempt.ContainmentLoss = loss;
            _activeLoss = loss;
            _warmStateLost = true;

            if (recoveryUnknown)
            {
                _circuit.Stop();
                _state = PublicSessionState.RecoveryUnknown;
                _bootstrapState = IsTemplate
                    ? BootstrapState.Unknown
                    : BootstrapState.NotApplicable;
                _lastFailureReason = SessionRecoveryFailureReason.AmbiguousLifecycle;
                return new SessionRecoveryLossTransition(
                    SessionRecoveryLossDisposition.RecoveryUnknown,
                    loss);
            }

            if (!bindingMatches)
            {
                StopAsFaultedLocked(SessionRecoveryFailureReason.BindingMismatch);
                return new SessionRecoveryLossTransition(
                    SessionRecoveryLossDisposition.Faulted,
                    loss);
            }

            if (!eligible)
            {
                if (_state is not (
                    PublicSessionState.Faulted or
                    PublicSessionState.RecoveryUnknown))
                    _state = PublicSessionState.Lost;
                _lastFailureReason = SessionRecoveryFailureReason.WorkerLost;
                return new SessionRecoveryLossTransition(
                    SessionRecoveryLossDisposition.NotEligible,
                    loss);
            }

            _state = PublicSessionState.Recovering;
            _bootstrapState = IsTemplate
                ? BootstrapState.Pending
                : BootstrapState.NotApplicable;
            _lastFailureReason = SessionRecoveryFailureReason.WorkerLost;
            return new SessionRecoveryLossTransition(
                SessionRecoveryLossDisposition.BeganContainment,
                loss);
        }
    }

    internal SessionRecoveryDeathTransition ConfirmOldTreeDeath(
        SessionRecoveryLossLease loss)
    {
        ArgumentNullException.ThrowIfNull(loss);
        if (Monitor.IsEntered(_gate))
        {
            throw new InvalidOperationException(
                "Synchronous tree-death confirmation cannot run while the state gate is held.");
        }
        if (!loss.PreparationCompletion.IsCompleted ||
            !loss.RestoreCompletion.IsCompleted)
        {
            throw new InvalidOperationException(
                "Use ConfirmOldTreeDeathAsync while attempt preparation or restore is still in flight.");
        }
        lock (_gate)
        {
            if (_resourceHandoffCompletion is { Task.IsCompleted: false })
            {
                throw new InvalidOperationException(
                    "Use ConfirmOldTreeDeathAsync while resource ownership handoff is still in flight.");
            }
        }

        return ConfirmOldTreeDeathAsync(loss).AsTask().GetAwaiter().GetResult();
    }

    internal async ValueTask<SessionRecoveryDeathTransition>
        ConfirmOldTreeDeathAsync(SessionRecoveryLossLease loss)
    {
        ArgumentNullException.ThrowIfNull(loss);
        Task priorResourceHandoff;
        lock (_gate)
        {
            if (_terminalShutdown)
            {
                return new SessionRecoveryDeathTransition(
                    SessionRecoveryDeathDisposition.Stopped,
                    null);
            }

            if (!IsTrackedLossLocked(loss))
            {
                return new SessionRecoveryDeathTransition(
                    loss.DeathConfirmed || loss.DeathConfirmationStarted
                        ? SessionRecoveryDeathDisposition.Duplicate
                        : SessionRecoveryDeathDisposition.StaleLoss,
                    null);
            }

            if (loss.DeathConfirmed || loss.DeathConfirmationStarted)
            {
                return new SessionRecoveryDeathTransition(
                    SessionRecoveryDeathDisposition.Duplicate,
                    null);
            }

            loss.DeathConfirmationStarted = true;
            priorResourceHandoff = _resourceHandoffCompletion?.Task ??
                Task.CompletedTask;
        }

        // Keep caller/event-pump threads responsive even when every joined task
        // is already complete and the next factory or cleanup seam blocks.
        await Task.CompletedTask.ConfigureAwait(
            ConfigureAwaitOptions.ForceYielding);

        try
        {
            await Task.WhenAll(
                loss.PreparationCompletion,
                loss.RestoreCompletion,
                priorResourceHandoff).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (IsTrackedLossLocked(loss) && !_terminalShutdown)
                {
                    RemoveTrackedLossLocked(loss);
                    StopAsFaultedLocked(SessionRecoveryFailureReason.AttemptFailed);
                }
            }
            return new SessionRecoveryDeathTransition(
                SessionRecoveryDeathDisposition.Faulted,
                null);
        }

        ISessionRecoveryPreparedAttempt? deadResources;
        TaskCompletionSource? handoff = null;
        bool eligibleForRecovery;
        lock (_gate)
        {
            if (_terminalShutdown)
            {
                return new SessionRecoveryDeathTransition(
                    SessionRecoveryDeathDisposition.Stopped,
                    null);
            }
            if (!IsTrackedLossLocked(loss))
            {
                return new SessionRecoveryDeathTransition(
                    SessionRecoveryDeathDisposition.Duplicate,
                    null);
            }

            loss.DeathConfirmed = true;
            RecordAmbiguousTransitionDeathLocked(loss.WorkerIdentity);
            if (_currentIdentity is not null &&
                SameIdentity(_currentIdentity, loss.WorkerIdentity))
            {
                _currentIdentity = null;
            }
            deadResources = loss.Resources;
            eligibleForRecovery = loss.EligibleForRecovery;
            if (deadResources is not null)
            {
                if (_resourceHandoffCompletion is not null)
                {
                    StopAsFaultedLocked(
                        SessionRecoveryFailureReason.AttemptFailed);
                    return new SessionRecoveryDeathTransition(
                        SessionRecoveryDeathDisposition.Faulted,
                        null);
                }

                _resourceHandoffCompletion = handoff = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (deadResources is not null &&
            !CompleteResourceHandoff(deadResources, handoff!))
        {
            lock (_gate)
            {
                RemoveTrackedLossLocked(loss);
                TryResolveAmbiguousTransitionLocked();
            }
            return new SessionRecoveryDeathTransition(
                SessionRecoveryDeathDisposition.Faulted,
                null);
        }

        lock (_gate)
        {
            RemoveTrackedLossLocked(loss);
            TryResolveAmbiguousTransitionLocked();
        }

        if (!eligibleForRecovery)
        {
            lock (_gate)
            {
                return new SessionRecoveryDeathTransition(
                    _terminalShutdown
                        ? SessionRecoveryDeathDisposition.Stopped
                        : _state == PublicSessionState.Faulted
                            ? SessionRecoveryDeathDisposition.Faulted
                            : SessionRecoveryDeathDisposition.NoRecovery,
                    null);
            }
        }

        SessionRecoveryIdentityReservation? reservation;
        SessionRecoveryDeathDisposition immediateDisposition;
        lock (_gate)
        {
            if (_terminalShutdown)
            {
                return new SessionRecoveryDeathTransition(
                    SessionRecoveryDeathDisposition.Stopped,
                    null);
            }

            RecoveryAttemptLease? recoveryLease;
            if (loss.FailedRecoveryLease is null)
            {
                recoveryLease = _circuit.BeginRecovery();
            }
            else
            {
                var failure = loss.StabilityFrozen
                    ? _circuit.ReportGenerationFailureAfterFrozenStability(
                        loss.FailedRecoveryLease)
                    : _circuit.ReportGenerationFailure(loss.FailedRecoveryLease);
                if (!failure.Accepted || failure.Exhausted)
                {
                    StopAsFaultedLocked(
                        SessionRecoveryFailureReason.IdentityExhausted);
                    return new SessionRecoveryDeathTransition(
                        SessionRecoveryDeathDisposition.Faulted,
                        null);
                }

                recoveryLease = failure.ImmediateAttempt;
            }

            if (recoveryLease is null)
            {
                ApplyCircuitStateLocked();
                return new SessionRecoveryDeathTransition(
                    CurrentDeathDispositionLocked(),
                    null);
            }

            reservation = ReserveAttemptLocked(recoveryLease);
            immediateDisposition = reservation is null
                ? CurrentDeathDispositionLocked()
                : SessionRecoveryDeathDisposition.AttemptStarted;
        }

        if (reservation is null)
        {
            return new SessionRecoveryDeathTransition(
                immediateDisposition,
                null);
        }

        var reservedAttempt = CompleteIdentityReservation(reservation);
        if (reservedAttempt is null)
        {
            lock (_gate)
            {
                return new SessionRecoveryDeathTransition(
                    _terminalShutdown
                        ? SessionRecoveryDeathDisposition.Stopped
                        : CurrentDeathDispositionLocked(),
                    null);
            }
        }

        var prepared = PrepareReservedAttempt(reservedAttempt);
        lock (_gate)
        {
            if (_terminalShutdown)
            {
                return new SessionRecoveryDeathTransition(
                    SessionRecoveryDeathDisposition.Stopped,
                    null);
            }
            if (reservedAttempt.ContainmentLoss is not null &&
                ReferenceEquals(_activeLoss, reservedAttempt.ContainmentLoss))
            {
                return new SessionRecoveryDeathTransition(
                    SessionRecoveryDeathDisposition.ContainmentRequired,
                    null,
                    reservedAttempt.ContainmentLoss);
            }
            return prepared is not null &&
                ReferenceEquals(_activeAttempt, prepared)
                ? new SessionRecoveryDeathTransition(
                    SessionRecoveryDeathDisposition.AttemptStarted,
                    prepared)
                : new SessionRecoveryDeathTransition(
                    CurrentDeathDispositionLocked(),
                    null);
        }
    }

    internal SessionRecoveryAttemptStartTransition TryStartDueAttempt()
    {
        SessionRecoveryIdentityReservation? reservation;
        lock (_gate)
        {
            if (_terminalShutdown)
            {
                return new(
                    SessionRecoveryAttemptStartDisposition.Stopped,
                    null,
                    null);
            }
            if (_activeAttempt is not null ||
                _activeIdentityReservation is not null ||
                _activeLoss is not null ||
                _secondaryTransitionLoss is not null ||
                HasUnresolvedCleanupLocked())
            {
                return new(
                    SessionRecoveryAttemptStartDisposition.NotDue,
                    null,
                    null);
            }

            var recoveryLease = _circuit.TryAcquireDueAttempt();
            if (recoveryLease is null)
            {
                return new(
                    _state == PublicSessionState.Faulted
                        ? SessionRecoveryAttemptStartDisposition.Faulted
                        : SessionRecoveryAttemptStartDisposition.NotDue,
                    null,
                    null);
            }

            reservation = ReserveAttemptLocked(recoveryLease);
        }

        if (reservation is null)
        {
            lock (_gate)
            {
                return new(
                    _terminalShutdown
                        ? SessionRecoveryAttemptStartDisposition.Stopped
                        : _state == PublicSessionState.Faulted
                        ? SessionRecoveryAttemptStartDisposition.Faulted
                        : SessionRecoveryAttemptStartDisposition.NotDue,
                    null,
                    null);
            }
        }

        var reserved = CompleteIdentityReservation(reservation);
        if (reserved is null)
        {
            lock (_gate)
            {
                return new(
                    _terminalShutdown
                        ? SessionRecoveryAttemptStartDisposition.Stopped
                        : _state == PublicSessionState.Faulted
                            ? SessionRecoveryAttemptStartDisposition.Faulted
                            : SessionRecoveryAttemptStartDisposition.NotDue,
                    null,
                    null);
            }
        }

        var prepared = PrepareReservedAttempt(reserved);
        lock (_gate)
        {
            if (_terminalShutdown)
            {
                return new(
                    SessionRecoveryAttemptStartDisposition.Stopped,
                    null,
                    null);
            }
            if (reserved.ContainmentLoss is not null &&
                ReferenceEquals(_activeLoss, reserved.ContainmentLoss))
            {
                return new(
                    SessionRecoveryAttemptStartDisposition.ContainmentRequired,
                    null,
                    reserved.ContainmentLoss);
            }

            return prepared is not null && ReferenceEquals(_activeAttempt, prepared)
                ? new(
                    SessionRecoveryAttemptStartDisposition.AttemptStarted,
                    prepared,
                    null)
                : new(
                    _state == PublicSessionState.Faulted
                        ? SessionRecoveryAttemptStartDisposition.Faulted
                        : SessionRecoveryAttemptStartDisposition.NotDue,
                    null,
                    null);
        }
    }

    internal SessionRecoveryAttemptTransition ExecuteBaseline(
        SessionRecoveryAttemptLease attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ISessionRecoveryPreparedAttempt prepared;
        byte[] transientBootstrapBytes;

        lock (_gate)
        {
            if (_terminalShutdown)
                return new(SessionRecoveryAttemptDisposition.Stopped, null);
            if (!ReferenceEquals(_activeAttempt, attempt))
                return new(SessionRecoveryAttemptDisposition.StaleAttempt, null);
            if (attempt.BaselineStarted)
                return new(SessionRecoveryAttemptDisposition.Duplicate, null);
            if (AttemptDeadlineElapsedLocked(attempt))
            {
                var loss = BeginFailedAttemptContainmentLocked(
                    attempt,
                    SessionRecoveryFailureReason.AttemptDeadlineExpired,
                    deterministicFault: false);
                return new(SessionRecoveryAttemptDisposition.DeadlineExpired, loss);
            }

            prepared = attempt.PreparedAttempt ??
                throw new InvalidOperationException("Recovery attempt is not prepared.");
            transientBootstrapBytes = IsTemplate
                ? _frozenBootstrapBytes.ToArray()
                : [];
            if (IsTemplate &&
                Sha256Digest.Compute(transientBootstrapBytes) !=
                _frozenBinding.BootstrapDigest)
            {
                CryptographicOperations.ZeroMemory(transientBootstrapBytes);
                var loss = BeginFailedAttemptContainmentLocked(
                    attempt,
                    SessionRecoveryFailureReason.BindingMismatch,
                    deterministicFault: true);
                return new(SessionRecoveryAttemptDisposition.Faulted, loss);
            }

            attempt.BaselineStarted = true;
            attempt.Stage = IsTemplate
                ? SessionRecoveryAttemptStage.Bootstrapping
                : SessionRecoveryAttemptStage.Attempting;
            if (IsTemplate)
                _state = PublicSessionState.Bootstrapping;
        }

        SessionRecoveryBaselineResult result;
        try
        {
            result = prepared.RestoreBaseline(transientBootstrapBytes);
        }
        catch
        {
            result = SessionRecoveryBaselineResult.RetryableFailure;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transientBootstrapBytes);
            attempt.RestoreCompletion.TrySetResult();
        }

        if (!Enum.IsDefined(result.Outcome))
            result = SessionRecoveryBaselineResult.DeterministicConfigurationFailure;

        lock (_gate)
        {
            if (_terminalShutdown)
                return new(SessionRecoveryAttemptDisposition.Stopped, null);
            if (!ReferenceEquals(_activeAttempt, attempt))
                return new(SessionRecoveryAttemptDisposition.StaleAttempt, null);
            if (AttemptDeadlineElapsedLocked(attempt))
            {
                var loss = BeginFailedAttemptContainmentLocked(
                    attempt,
                    SessionRecoveryFailureReason.AttemptDeadlineExpired,
                    deterministicFault: false);
                return new(SessionRecoveryAttemptDisposition.DeadlineExpired, loss);
            }

            return result.Outcome switch
            {
                SessionRecoveryBaselineOutcome.Ready =>
                    CompleteReadyLocked(attempt),
                SessionRecoveryBaselineOutcome.RetryableFailure =>
                    CompleteRetryableFailureLocked(attempt),
                SessionRecoveryBaselineOutcome.DeterministicConfigurationFailure =>
                    CompleteDeterministicFailureLocked(attempt),
                _ => throw new InvalidOperationException(
                    "Unknown session recovery baseline outcome."),
            };
        }
    }

    internal SessionRecoveryAttemptTransition TryExpireAttempt(
        SessionRecoveryAttemptLease attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate)
        {
            if (_terminalShutdown ||
                !ReferenceEquals(_activeAttempt, attempt))
            {
                return new(
                    _terminalShutdown
                        ? SessionRecoveryAttemptDisposition.Stopped
                        : SessionRecoveryAttemptDisposition.StaleAttempt,
                    null);
            }

            if (!AttemptDeadlineElapsedLocked(attempt))
            {
                return new(SessionRecoveryAttemptDisposition.NotDue, null);
            }

            var loss = BeginFailedAttemptContainmentLocked(
                attempt,
                SessionRecoveryFailureReason.AttemptDeadlineExpired,
                deterministicFault: false);
            return new(SessionRecoveryAttemptDisposition.DeadlineExpired, loss);
        }
    }

    internal bool TryCompleteReadyStability()
    {
        lock (_gate)
        {
            return !_terminalShutdown &&
                _currentRecoveryLease is not null &&
                _circuit.TryCompleteReadyStability(_currentRecoveryLease);
        }
    }

    internal ValueTask ShutdownAsync()
    {
        TaskCompletionSource completion;
        ISessionRecoveryPreparedAttempt[] resources;
        Task[] ownershipTasks;
        lock (_gate)
        {
            if (_shutdownCompletion is not null)
                return new ValueTask(_shutdownCompletion.Task);

            _terminalShutdown = true;
            _shutdownCompletion = completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CryptographicOperations.ZeroMemory(_frozenBootstrapBytes);

            var owned = new HashSet<ISessionRecoveryPreparedAttempt>(
                ReferenceEqualityComparer.Instance);
            var waits = new List<Task>();
            if (_activeAttempt is not null)
            {
                waits.Add(_activeAttempt.PreparationCompletion.Task);
                if (_activeAttempt.BaselineStarted)
                    waits.Add(_activeAttempt.RestoreCompletion.Task);
                if (_activeAttempt.PreparedAttempt is not null)
                    owned.Add(_activeAttempt.PreparedAttempt);
                _activeAttempt.Stage = SessionRecoveryAttemptStage.Completed;
            }
            if (_activeIdentityReservation is not null)
                waits.Add(_activeIdentityReservation.Completion.Task);
            if (_currentResources is not null)
                owned.Add(_currentResources);
            if (_activeLoss is not null)
            {
                waits.Add(_activeLoss.PreparationCompletion);
                waits.Add(_activeLoss.RestoreCompletion);
                if (!_activeLoss.DeathConfirmed &&
                    _activeLoss.Resources is not null)
                    owned.Add(_activeLoss.Resources);
            }
            if (_secondaryTransitionLoss is not null)
            {
                waits.Add(_secondaryTransitionLoss.PreparationCompletion);
                waits.Add(_secondaryTransitionLoss.RestoreCompletion);
                if (!_secondaryTransitionLoss.DeathConfirmed &&
                    _secondaryTransitionLoss.Resources is not null)
                {
                    owned.Add(_secondaryTransitionLoss.Resources);
                }
            }
            if (_resourceHandoffCompletion is not null)
                waits.Add(_resourceHandoffCompletion.Task);
            if (_rejectedPreparationCleanup is not null)
                waits.Add(_rejectedPreparationCleanup.Completion);
            if (_resourceHandoffFailure is not null)
                waits.Add(_resourceHandoffFailure.Completion);
            resources = [.. owned];
            ownershipTasks = [.. waits];

            _activeTransition = null;
            _activeLoss = null;
            _secondaryTransitionLoss = null;
            _activeAttempt = null;
            _activeIdentityReservation = null;
            _currentIdentity = null;
            _currentRecoveryLease = null;
            _currentResources = null;
            _circuit.Stop();
            if (_state != PublicSessionState.Cold)
                _state = PublicSessionState.Lost;
            if (IsTemplate && _bootstrapState == BootstrapState.Pending)
                _bootstrapState = BootstrapState.Unknown;
        }

        _ = CompleteShutdownAsync(
            resources,
            ownershipTasks,
            completion);
        return new ValueTask(completion.Task);
    }

    internal void Shutdown() =>
        ShutdownAsync().AsTask().GetAwaiter().GetResult();

    public void Dispose() => Shutdown();

    internal SessionRecoveryStateMachineSnapshot Snapshot()
    {
        lock (_gate)
        {
            var circuit = _circuit.Snapshot();
            var identity = VisibleIdentityLocked();
            var (phase, attempt, retryAfter) = RecoveryMetadataLocked(circuit);
            var publicFailure = _lastFailureReason switch
            {
                SessionRecoveryFailureReason.AmbiguousLifecycle =>
                    PublicRecoveryDetailCode.SessionRecoveryUnknown,
                SessionRecoveryFailureReason.BindingMismatch or
                SessionRecoveryFailureReason.BootstrapFailed or
                SessionRecoveryFailureReason.IdentityExhausted or
                SessionRecoveryFailureReason.IdentityReused =>
                    PublicRecoveryDetailCode.SessionBootstrapFailed,
                SessionRecoveryFailureReason.WorkerLost or
                SessionRecoveryFailureReason.AttemptFailed or
                SessionRecoveryFailureReason.AttemptDeadlineExpired =>
                    PublicRecoveryDetailCode.SessionRecovering,
                _ => (PublicRecoveryDetailCode?)null,
            };
            var session = new PublicSessionStateSnapshot(
                _frozenBinding.Alias,
                _desiredState,
                _state,
                identity?.BootId,
                identity?.Generation,
                _transitionVersion,
                phase,
                attempt,
                retryAfter,
                _state == PublicSessionState.Ready,
                publicFailure,
                _warmStateLost,
                _bootstrapState);

            return new SessionRecoveryStateMachineSnapshot(
                session,
                circuit.State,
                circuit.ConsecutiveFailedGenerations,
                _terminalShutdown,
                _lastFailureReason,
                _activeTransition?.Kind,
                _activeTransition?.Dispatched ?? false,
                (_activeLoss is not null && !_activeLoss.DeathConfirmed) ||
                    (_secondaryTransitionLoss is not null &&
                        !_secondaryTransitionLoss.DeathConfirmed) ||
                    HasUnconfirmedTransitionTargetLocked() ||
                    _rejectedPreparationCleanup is not null);
        }
    }

    private SessionRecoveryIdentityReservation? ReserveAttemptLocked(
        RecoveryAttemptLease recoveryLease)
    {
        SessionRecoveryAttemptDeadline deadline;
        try
        {
            deadline = CreateAttemptDeadlineLocked();
        }
        catch
        {
            FailUnpreparedAttemptLocked(
                recoveryLease,
                SessionRecoveryFailureReason.AttemptFailed);
            return null;
        }

        var reservation = new SessionRecoveryIdentityReservation(
            NextLeaseSequenceLocked(),
            recoveryLease,
            new SessionRecoveryAttemptContext(
                _frozenBinding,
                _desiredState,
                _transitionVersion),
            deadline);
        _activeIdentityReservation = reservation;
        _state = recoveryLease.IsHalfOpen
            ? PublicSessionState.HalfOpen
            : PublicSessionState.Recovering;
        _bootstrapState = IsTemplate
            ? BootstrapState.Pending
            : BootstrapState.NotApplicable;
        return reservation;
    }

    private SessionRecoveryAttemptLease? CompleteIdentityReservation(
        SessionRecoveryIdentityReservation reservation)
    {
        WorkerGeneration? generation = null;
        Exception? allocationFailure = null;
        try
        {
            generation = _generationAllocator.Allocate(_frozenBinding.Alias);
        }
        catch (Exception exception)
        {
            allocationFailure = exception;
        }

        lock (_gate)
        {
            var generationReused = generation is not null &&
                generation.Value <= _generationHighWatermark;
            if (generation is not null && !generationReused)
            {
                _generationHighWatermark = generation.Value;
                _hasObservedGeneration = true;
            }

            if (!ReferenceEquals(_activeIdentityReservation, reservation))
            {
                reservation.Completion.TrySetResult();
                return null;
            }
            if (allocationFailure is not null)
            {
                _activeIdentityReservation = null;
                if (!_terminalShutdown)
                {
                    StopAsFaultedLocked(
                        SessionRecoveryFailureReason.IdentityExhausted);
                }
                reservation.Completion.TrySetResult();
                return null;
            }
            if (generationReused)
            {
                _activeIdentityReservation = null;
                StopAsFaultedLocked(SessionRecoveryFailureReason.IdentityReused);
                reservation.Completion.TrySetResult();
                return null;
            }
            if (_terminalShutdown)
            {
                _activeIdentityReservation = null;
                reservation.Completion.TrySetResult();
                return null;
            }
            if (AttemptDeadlineElapsedLocked(reservation.Deadline))
            {
                _activeIdentityReservation = null;
                FailUnpreparedAttemptLocked(
                    reservation.RecoveryLease,
                    SessionRecoveryFailureReason.AttemptDeadlineExpired);
                reservation.Completion.TrySetResult();
                return null;
            }
        }

        WorkerBootId? bootId = null;
        Exception? bootIdFailure = null;
        try
        {
            bootId = _workerBootIdSource.Next() ??
                throw new InvalidOperationException("Worker boot ID source returned null.");
        }
        catch (Exception exception)
        {
            bootIdFailure = exception;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_activeIdentityReservation, reservation))
            {
                reservation.Completion.TrySetResult();
                return null;
            }
            _activeIdentityReservation = null;
            if (_terminalShutdown)
            {
                reservation.Completion.TrySetResult();
                return null;
            }
            if (bootIdFailure is not null)
            {
                FailUnpreparedAttemptLocked(
                    reservation.RecoveryLease,
                    SessionRecoveryFailureReason.AttemptFailed);
                reservation.Completion.TrySetResult();
                return null;
            }
            if (AttemptDeadlineElapsedLocked(reservation.Deadline))
            {
                FailUnpreparedAttemptLocked(
                    reservation.RecoveryLease,
                    SessionRecoveryFailureReason.AttemptDeadlineExpired);
                reservation.Completion.TrySetResult();
                return null;
            }

            var attempt = new SessionRecoveryAttemptLease(
                NextLeaseSequenceLocked(),
                new GuardianHostWorkerIdentity(bootId!, generation!),
                reservation.RecoveryLease,
                reservation.Context,
                reservation.Deadline);
            _activeAttempt = attempt;
            reservation.Completion.TrySetResult();
            return attempt;
        }
    }

    private SessionRecoveryAttemptLease? PrepareReservedAttempt(
        SessionRecoveryAttemptLease attempt)
    {
        lock (_gate)
        {
            if (_terminalShutdown || !ReferenceEquals(_activeAttempt, attempt))
            {
                attempt.PreparationCompletion.TrySetResult();
                return null;
            }
            if (AttemptDeadlineElapsedLocked(attempt))
            {
                FailAttemptLocked(
                    attempt,
                    SessionRecoveryFailureReason.AttemptDeadlineExpired);
                attempt.PreparationCompletion.TrySetResult();
                return null;
            }
        }

        ISessionRecoveryPreparedAttempt? prepared = null;
        try
        {
            prepared = _attemptFactory.Prepare(
                attempt.Context,
                attempt.WorkerIdentity,
                attempt.Deadline);
            if (prepared is null)
                throw new InvalidOperationException(
                    "Recovery attempt factory returned null.");
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeAttempt, attempt) && !_terminalShutdown)
                    FailAttemptLocked(attempt, SessionRecoveryFailureReason.AttemptFailed);
            }
            attempt.PreparationCompletion.TrySetResult();
            return null;
        }

        var accepted = false;
        RejectedPreparationCleanup? rejectedCleanup = null;
        lock (_gate)
        {
            if (!_terminalShutdown && ReferenceEquals(_activeAttempt, attempt))
            {
                attempt.PreparedAttempt = prepared;
                attempt.Stage = SessionRecoveryAttemptStage.Attempting;
                if (AttemptDeadlineElapsedLocked(attempt))
                {
                    attempt.ContainmentLoss = BeginFailedAttemptContainmentLocked(
                        attempt,
                        SessionRecoveryFailureReason.AttemptDeadlineExpired,
                        deterministicFault: false);
                }
                accepted = true;
            }
            else
            {
                if (_rejectedPreparationCleanup is not null)
                {
                    throw new InvalidOperationException(
                        "A rejected preparation cleanup is already active.");
                }
                _rejectedPreparationCleanup = rejectedCleanup = new(
                    attempt.WorkerIdentity,
                    prepared,
                    attempt.PreparationCompletion.Task);
            }
        }

        if (!accepted)
        {
            _ = CompleteRejectedPreparationAsync(
                rejectedCleanup!,
                attempt.PreparationCompletion);
            return null;
        }

        attempt.PreparationCompletion.TrySetResult();
        return attempt;
    }

    private async Task CompleteRejectedPreparationAsync(
        RejectedPreparationCleanup cleanup,
        TaskCompletionSource preparationCompletion)
    {
        Exception? failure = null;
        try
        {
            await cleanup.Resource.ContainAndConfirmDeathAsync()
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (failure is null)
        {
            try
            {
                cleanup.Resource.Dispose();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        lock (_gate)
        {
            if (failure is null &&
                ReferenceEquals(_rejectedPreparationCleanup, cleanup))
            {
                _rejectedPreparationCleanup = null;
            }
            else if (failure is not null && !_terminalShutdown)
            {
                StopAsFaultedLocked(SessionRecoveryFailureReason.AttemptFailed);
            }
        }

        if (failure is null)
            preparationCompletion.TrySetResult();
        else
            preparationCompletion.TrySetException(failure);
    }

    private static async Task CompleteShutdownAsync(
        ISessionRecoveryPreparedAttempt[] resources,
        Task[] ownershipTasks,
        TaskCompletionSource completion)
    {
        Exception? failure = null;
        var containmentTasks = resources
            .Select(TryContainResourceAsync)
            .ToArray();
        try
        {
            await Task.WhenAll(ownershipTasks).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        var containmentResults = await Task.WhenAll(containmentTasks)
            .ConfigureAwait(false);
        foreach (var (resource, containmentFailure) in containmentResults)
        {
            if (containmentFailure is not null)
            {
                failure = failure is null
                    ? containmentFailure
                    : new AggregateException(failure, containmentFailure);
                continue;
            }

            try
            {
                resource.Dispose();
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(failure, exception);
            }
        }

        if (failure is null)
            completion.TrySetResult();
        else
            completion.TrySetException(failure);
    }

    private static async Task ContainResourceAsync(
        ISessionRecoveryPreparedAttempt resource) =>
        await resource.ContainAndConfirmDeathAsync().ConfigureAwait(false);

    private static async Task<(
        ISessionRecoveryPreparedAttempt Resource,
        Exception? Failure)> TryContainResourceAsync(
            ISessionRecoveryPreparedAttempt resource)
    {
        try
        {
            await ContainResourceAsync(resource).ConfigureAwait(false);
            return (resource, null);
        }
        catch (Exception exception)
        {
            return (resource, exception);
        }
    }

    private SessionRecoveryAttemptTransition CompleteReadyLocked(
        SessionRecoveryAttemptLease attempt)
    {
        if (!_circuit.MarkReady(attempt.RecoveryLease))
        {
            var loss = BeginFailedAttemptContainmentLocked(
                attempt,
                SessionRecoveryFailureReason.AttemptFailed,
                deterministicFault: true);
            return new(SessionRecoveryAttemptDisposition.Faulted, loss);
        }

        attempt.Stage = SessionRecoveryAttemptStage.Completed;
        _activeAttempt = null;
        _currentIdentity = attempt.WorkerIdentity;
        _currentRecoveryLease = attempt.RecoveryLease;
        _currentResources = attempt.PreparedAttempt;
        _desiredState = DesiredSessionState.Ready;
        _state = PublicSessionState.Ready;
        _bootstrapState = IsTemplate
            ? BootstrapState.Restored
            : BootstrapState.NotApplicable;
        _everReady = true;
        return new(SessionRecoveryAttemptDisposition.Ready, null);
    }

    private SessionRecoveryAttemptTransition CompleteRetryableFailureLocked(
        SessionRecoveryAttemptLease attempt)
    {
        var loss = BeginFailedAttemptContainmentLocked(
            attempt,
            SessionRecoveryFailureReason.AttemptFailed,
            deterministicFault: false);
        return new(SessionRecoveryAttemptDisposition.ContainmentRequired, loss);
    }

    private SessionRecoveryAttemptTransition CompleteDeterministicFailureLocked(
        SessionRecoveryAttemptLease attempt)
    {
        var loss = BeginFailedAttemptContainmentLocked(
            attempt,
            SessionRecoveryFailureReason.BootstrapFailed,
            deterministicFault: true);
        return new(SessionRecoveryAttemptDisposition.Faulted, loss);
    }

    private SessionRecoveryLossLease BeginFailedAttemptContainmentLocked(
        SessionRecoveryAttemptLease attempt,
        SessionRecoveryFailureReason reason,
        bool deterministicFault)
    {
        attempt.Stage = SessionRecoveryAttemptStage.Completed;
        _activeAttempt = null;
        var loss = new SessionRecoveryLossLease(
            NextLeaseSequenceLocked(),
            attempt.WorkerIdentity,
            eligibleForRecovery: !deterministicFault,
            failedRecoveryLease: attempt.RecoveryLease,
            stabilityFrozen: false,
            resources: attempt.PreparedAttempt,
            preparationCompletion: attempt.PreparationCompletion.Task,
            restoreCompletion: attempt.BaselineStarted
                ? attempt.RestoreCompletion.Task
                : Task.CompletedTask,
            nextAttemptOrdinal: checked(attempt.RecoveryLease.AttemptOrdinal + 1));
        attempt.ContainmentLoss = loss;
        _activeLoss = loss;
        _lastFailureReason = reason;
        _warmStateLost = true;

        if (deterministicFault)
        {
            _circuit.Stop();
            _state = PublicSessionState.Faulted;
            _bootstrapState = IsTemplate
                ? BootstrapState.Failed
                : BootstrapState.NotApplicable;
        }
        else
        {
            _state = PublicSessionState.Recovering;
            _bootstrapState = IsTemplate
                ? BootstrapState.Pending
                : BootstrapState.NotApplicable;
        }

        return loss;
    }

    private void FailAttemptLocked(
        SessionRecoveryAttemptLease attempt,
        SessionRecoveryFailureReason reason)
    {
        attempt.Stage = SessionRecoveryAttemptStage.Completed;
        _activeAttempt = null;
        FailUnpreparedAttemptLocked(attempt.RecoveryLease, reason);
    }

    private void FailUnpreparedAttemptLocked(
        RecoveryAttemptLease recoveryLease,
        SessionRecoveryFailureReason reason)
    {
        var failure = _circuit.ReportGenerationFailure(recoveryLease);
        _lastFailureReason = reason;
        if (!failure.Accepted || failure.Exhausted)
        {
            StopAsFaultedLocked(SessionRecoveryFailureReason.IdentityExhausted);
            return;
        }

        ApplyCircuitStateLocked();
    }

    private void ApplyCircuitStateLocked()
    {
        var circuit = _circuit.Snapshot();
        _state = circuit.State switch
        {
            RecoveryCircuitState.Backoff => PublicSessionState.Backoff,
            RecoveryCircuitState.CircuitOpen => PublicSessionState.CircuitOpen,
            RecoveryCircuitState.HalfOpen => PublicSessionState.HalfOpen,
            RecoveryCircuitState.Attempting => PublicSessionState.Recovering,
            RecoveryCircuitState.Ready => PublicSessionState.Ready,
            _ => PublicSessionState.Faulted,
        };
        _bootstrapState = IsTemplate
            ? BootstrapState.Pending
            : BootstrapState.NotApplicable;
    }

    private void StopAsFaultedLocked(SessionRecoveryFailureReason reason)
    {
        _circuit.Stop();
        _activeAttempt = null;
        _activeIdentityReservation = null;
        _currentRecoveryLease = null;
        _state = PublicSessionState.Faulted;
        _bootstrapState = IsTemplate
            ? BootstrapState.Failed
            : BootstrapState.NotApplicable;
        _lastFailureReason = reason;
    }

    private SessionRecoveryDeathDisposition CurrentDeathDispositionLocked() =>
        _state switch
        {
            PublicSessionState.Backoff or PublicSessionState.CircuitOpen =>
                SessionRecoveryDeathDisposition.Scheduled,
            PublicSessionState.Faulted =>
                SessionRecoveryDeathDisposition.Faulted,
            _ => SessionRecoveryDeathDisposition.NoRecovery,
        };

    private SessionRecoveryAttemptDeadline CreateAttemptDeadlineLocked()
    {
        var timeout = IsTemplate
            ? TimeSpan.FromSeconds(
                _frozenTemplateStartupTimeoutSeconds ??
                throw new InvalidOperationException("Template timeout is unavailable."))
            : _defaultAttemptTimeout;
        var started = _timeProvider.GetTimestamp();
        var scaledTicks = decimal.Divide(
            decimal.Multiply(timeout.Ticks, _timeProvider.TimestampFrequency),
            TimeSpan.TicksPerSecond);
        var delta = checked((long)decimal.Ceiling(scaledTicks));
        return new SessionRecoveryAttemptDeadline(
            started,
            checked(started + delta),
            timeout);
    }

    private int? _frozenTemplateStartupTimeoutSeconds;

    private bool AttemptDeadlineElapsedLocked(SessionRecoveryAttemptLease attempt) =>
        AttemptDeadlineElapsedLocked(attempt.Deadline);

    private bool AttemptDeadlineElapsedLocked(SessionRecoveryAttemptDeadline deadline) =>
        _timeProvider.GetElapsedTime(
            deadline.StartedTimestamp,
            _timeProvider.GetTimestamp()) >= deadline.Timeout;

    private (RecoveryPhase? Phase, long Attempt, int? RetryAfter)
        RecoveryMetadataLocked(RecoveryCircuitSnapshot circuit)
    {
        if (_state == PublicSessionState.Recovering && _activeLoss is not null)
        {
            return (
                RecoveryPhase.Containment,
                _activeLoss.NextAttemptOrdinal,
                ContractLimits.MinimumRetryAfterMilliseconds);
        }

        if (_state == PublicSessionState.Bootstrapping)
        {
            return (
                RecoveryPhase.Bootstrap,
                Math.Max(1, circuit.RecoveryAttempt),
                circuit.RetryAfterMilliseconds ??
                    ContractLimits.MinimumRetryAfterMilliseconds);
        }

        if (_state is PublicSessionState.Recovering or
            PublicSessionState.Backoff or
            PublicSessionState.CircuitOpen or
            PublicSessionState.HalfOpen)
        {
            return (
                circuit.RecoveryPhase,
                circuit.RecoveryAttempt,
                circuit.RetryAfterMilliseconds);
        }

        return (null, circuit.RecoveryAttempt, null);
    }

    private GuardianHostWorkerIdentity? VisibleIdentityLocked()
    {
        if (_currentIdentity is not null)
            return _currentIdentity;
        if (_activeAttempt is not null)
            return _activeAttempt.WorkerIdentity;
        if (_activeLoss is { DeathConfirmed: false })
            return _activeLoss.WorkerIdentity;
        if (_secondaryTransitionLoss is { DeathConfirmed: false })
            return _secondaryTransitionLoss.WorkerIdentity;
        if (_rejectedPreparationCleanup is not null)
            return _rejectedPreparationCleanup.WorkerIdentity;
        return null;
    }

    private bool AcceptObservedIdentityLocked(GuardianHostWorkerIdentity identity)
    {
        if (!_hasObservedGeneration)
        {
            if (identity.Generation.Value != _generationHighWatermark)
                return false;
            _generationHighWatermark = identity.Generation.Value;
            _hasObservedGeneration = true;
            return true;
        }

        if (_currentIdentity is not null && SameIdentity(_currentIdentity, identity))
            return true;
        if (identity.Generation.Value <= _generationHighWatermark)
            return false;

        _generationHighWatermark = identity.Generation.Value;
        return true;
    }

    private bool BindingMatches(SessionRecoveryBindingProof observed) =>
        observed.CatalogDigest == _frozenBindingProof.CatalogDigest &&
        observed.BindingKind == _frozenBindingProof.BindingKind &&
        observed.BindingDigest == _frozenBindingProof.BindingDigest &&
        observed.TemplateDigest == _frozenBindingProof.TemplateDigest &&
        observed.BootstrapDigest == _frozenBindingProof.BootstrapDigest;

    private bool TransitionAllowedLocked(SessionRecoveryTransitionKind kind) => kind switch
    {
        SessionRecoveryTransitionKind.Open => _state == PublicSessionState.Cold,
        SessionRecoveryTransitionKind.Close or
        SessionRecoveryTransitionKind.Restart or
        SessionRecoveryTransitionKind.Reset =>
            _state is PublicSessionState.Ready or
                PublicSessionState.Faulted or
                PublicSessionState.RecoveryUnknown,
        SessionRecoveryTransitionKind.Bootstrap =>
            _state == PublicSessionState.Ready && _currentIdentity is not null,
        _ => false,
    };

    private bool LifecycleTerminalMatchesLocked(
        SessionRecoveryTransitionLease transition,
        PublicSessionState observedState,
        GuardianHostWorkerIdentity? observedIdentity,
        BootstrapState bootstrapState,
        bool oldWorkerDeathConfirmed)
    {
        if (observedState == PublicSessionState.Cold)
        {
            return transition.Kind == SessionRecoveryTransitionKind.Close &&
                oldWorkerDeathConfirmed &&
                observedIdentity is null &&
                bootstrapState == (IsTemplate
                    ? BootstrapState.Pending
                    : BootstrapState.NotApplicable);
        }

        if (observedState == PublicSessionState.Ready)
        {
            if (observedIdentity is null)
                return false;

            var expectedBootstrap = IsTemplate
                ? BootstrapState.Restored
                : BootstrapState.NotApplicable;
            if (bootstrapState != expectedBootstrap)
                return false;

            return transition.Kind switch
            {
                SessionRecoveryTransitionKind.Open =>
                    !oldWorkerDeathConfirmed &&
                    transition.SourceWorkerIdentity is null &&
                    transition.WorkerIdentity is not null &&
                    SameIdentity(transition.WorkerIdentity, observedIdentity),
                SessionRecoveryTransitionKind.Restart or
                SessionRecoveryTransitionKind.Reset =>
                    oldWorkerDeathConfirmed &&
                    transition.WorkerIdentity is not null &&
                    !ReferenceEquals(
                        transition.WorkerIdentity,
                        transition.SourceWorkerIdentity) &&
                    (transition.SourceWorkerIdentity is null ||
                        !SameIdentity(
                            transition.SourceWorkerIdentity,
                            transition.WorkerIdentity)) &&
                    SameIdentity(transition.WorkerIdentity, observedIdentity),
                SessionRecoveryTransitionKind.Bootstrap =>
                    !oldWorkerDeathConfirmed &&
                    transition.SourceWorkerIdentity is not null &&
                    transition.WorkerIdentity is not null &&
                    SameIdentity(
                        transition.SourceWorkerIdentity,
                        transition.WorkerIdentity) &&
                    SameIdentity(transition.SourceWorkerIdentity, observedIdentity),
                _ => false,
            };
        }

        if (observedState != PublicSessionState.Faulted ||
            bootstrapState != (IsTemplate
                ? BootstrapState.Failed
                : BootstrapState.NotApplicable))
        {
            return false;
        }

        if (oldWorkerDeathConfirmed)
            return observedIdentity is null;
        return observedIdentity is not null &&
            _currentIdentity is not null &&
            SameIdentity(_currentIdentity, observedIdentity);
    }

    private bool CompleteResourceHandoff(
        ISessionRecoveryPreparedAttempt resource,
        TaskCompletionSource handoff)
    {
        Exception? failure = null;
        try
        {
            resource.Dispose();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_resourceHandoffCompletion, handoff))
                _resourceHandoffCompletion = null;
            if (failure is not null)
            {
                _resourceHandoffFailure ??= new(
                    resource,
                    failure,
                    handoff.Task);
                if (!_terminalShutdown)
                    StopAsFaultedLocked(SessionRecoveryFailureReason.AttemptFailed);
            }
        }

        if (failure is null)
        {
            handoff.TrySetResult();
            return true;
        }
        else
        {
            handoff.TrySetException(failure);
            return false;
        }
    }

    private SessionRecoveryLossLease?
        TryBeginAmbiguousTransitionCounterpartLossLocked(
            GuardianHostWorkerIdentity workerIdentity)
    {
        var transition = _activeTransition;
        var primaryLoss = _activeLoss;
        if (_state != PublicSessionState.RecoveryUnknown ||
            transition is not { Dispatched: true } ||
            primaryLoss is null ||
            _secondaryTransitionLoss is not null ||
            transition.SourceWorkerIdentity is null ||
            transition.WorkerIdentity is null ||
            SameIdentity(
                transition.SourceWorkerIdentity,
                transition.WorkerIdentity))
        {
            return null;
        }

        var primaryIsSource = SameIdentity(
            primaryLoss.WorkerIdentity,
            transition.SourceWorkerIdentity);
        var primaryIsTarget = SameIdentity(
            primaryLoss.WorkerIdentity,
            transition.WorkerIdentity);
        var incomingIsSource = SameIdentity(
            workerIdentity,
            transition.SourceWorkerIdentity);
        var incomingIsTarget = SameIdentity(
            workerIdentity,
            transition.WorkerIdentity);
        if (!(primaryIsSource && incomingIsTarget) &&
            !(primaryIsTarget && incomingIsSource))
        {
            return null;
        }

        RecoveryAttemptLease? failedRecoveryLease = null;
        ISessionRecoveryPreparedAttempt? resources = null;
        if (incomingIsSource &&
            _currentIdentity is not null &&
            SameIdentity(_currentIdentity, workerIdentity))
        {
            failedRecoveryLease = _currentRecoveryLease;
            resources = _currentResources;
            _currentRecoveryLease = null;
            _currentResources = null;
        }

        var counterpart = new SessionRecoveryLossLease(
            NextLeaseSequenceLocked(),
            workerIdentity,
            eligibleForRecovery: false,
            failedRecoveryLease,
            stabilityFrozen: false,
            resources,
            Task.CompletedTask,
            Task.CompletedTask,
            primaryLoss.NextAttemptOrdinal);
        _secondaryTransitionLoss = counterpart;
        _warmStateLost = true;
        return counterpart;
    }

    private bool IsTrackedLossLocked(SessionRecoveryLossLease loss) =>
        ReferenceEquals(_activeLoss, loss) ||
        ReferenceEquals(_secondaryTransitionLoss, loss);

    private void RemoveTrackedLossLocked(SessionRecoveryLossLease loss)
    {
        if (ReferenceEquals(_activeLoss, loss))
        {
            _activeLoss = _secondaryTransitionLoss;
            _secondaryTransitionLoss = null;
        }
        else if (ReferenceEquals(_secondaryTransitionLoss, loss))
        {
            _secondaryTransitionLoss = null;
        }
    }

    private void RecordAmbiguousTransitionDeathLocked(
        GuardianHostWorkerIdentity workerIdentity)
    {
        var transition = _activeTransition;
        if (transition is not { Dispatched: true })
            return;
        if (transition.SourceWorkerIdentity is not null &&
            SameIdentity(transition.SourceWorkerIdentity, workerIdentity))
        {
            transition.SourceDeathConfirmed = true;
        }
        if (transition.WorkerIdentity is not null &&
            SameIdentity(transition.WorkerIdentity, workerIdentity))
        {
            transition.TargetDeathConfirmed = true;
        }
    }

    private void TryResolveAmbiguousTransitionLocked()
    {
        var transition = _activeTransition;
        if (transition is not { Dispatched: true, TargetDeathConfirmed: true } ||
            transition.SourceWorkerIdentity is null ||
            transition.WorkerIdentity is null ||
            SameIdentity(
                transition.SourceWorkerIdentity,
                transition.WorkerIdentity))
        {
            return;
        }

        var sourceLossStillTracked =
            _activeLoss is not null &&
                SameIdentity(
                    _activeLoss.WorkerIdentity,
                    transition.SourceWorkerIdentity) ||
            _secondaryTransitionLoss is not null &&
                SameIdentity(
                    _secondaryTransitionLoss.WorkerIdentity,
                    transition.SourceWorkerIdentity);
        if (transition.SourceDeathConfirmed || !sourceLossStillTracked)
            _activeTransition = null;
    }

    private bool HasUnconfirmedTransitionTargetLocked()
    {
        var transition = _activeTransition;
        return transition is
            { Dispatched: true, SourceDeathConfirmed: true,
                TargetDeathConfirmed: false } &&
            transition.SourceWorkerIdentity is not null &&
            transition.WorkerIdentity is not null &&
            !SameIdentity(
                transition.SourceWorkerIdentity,
                transition.WorkerIdentity);
    }

    private long NextLeaseSequenceLocked() =>
        _leaseSequence = checked(_leaseSequence + 1);

    private bool HasUnresolvedCleanupLocked() =>
        _rejectedPreparationCleanup is not null ||
        _resourceHandoffFailure is not null;

    private bool IsExpectedExplicitFaultVersionLocked(
        SessionTransitionVersion transitionVersion)
    {
        if (!_everReady)
            return transitionVersion == _transitionVersion;
        try
        {
            return transitionVersion.Value ==
                checked(_transitionVersion.Value + 1);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private void ValidateReadyBootstrapState(BootstrapState state)
    {
        var expected = IsTemplate
            ? BootstrapState.Restored
            : BootstrapState.NotApplicable;
        if (state != expected)
        {
            throw new ArgumentException(
                "Ready bootstrap state does not match the frozen binding.",
                nameof(state));
        }
    }

    private static bool SameIdentity(
        GuardianHostWorkerIdentity left,
        GuardianHostWorkerIdentity right) =>
        left.BootId == right.BootId &&
        left.Generation == right.Generation;

    private void ValidateTemplateBinding(
        RecoveryBinding binding,
        RecoveryTemplate? template)
    {
        if (binding.BindingKind == RecoveryBindingKind.Template)
        {
            if (template is null ||
                template.Name != binding.TemplateName ||
                template.TemplateDigest != binding.TemplateDigest ||
                template.BootstrapDigest != binding.BootstrapDigest)
            {
                throw new ArgumentException(
                    "Frozen template does not match the recovery binding.",
                    nameof(template));
            }

            _frozenTemplateStartupTimeoutSeconds = template.StartupTimeoutSeconds;
            return;
        }

        if (template is not null)
        {
            throw new ArgumentException(
                "A non-template binding cannot carry bootstrap bytes.",
                nameof(template));
        }
    }
}
