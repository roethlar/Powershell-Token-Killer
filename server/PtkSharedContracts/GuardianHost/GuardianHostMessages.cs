using System.Buffers;
using System.Globalization;
using System.Text;

namespace PtkSharedContracts;

public enum GuardianHostCancelReason { CallerCanceled, DeadlineExpired, GuardianShutdown }
public enum GuardianHostShutdownReason { GuardianEof, GuardianShutdown, HostRecycle }
public enum GuardianHostInvokeRoute { Auto, Pwsh, Rtk }
public enum GuardianHostDeliveryState { NotDispatched, WriteStarted, TerminalDecoded }
public enum GuardianHostSessionLifecycleReason
{
    RequestedOpen, RequestedClose, RequestedRestart, RequestedReset, AutomaticRecovery,
    ExecutionTimeout, WorkerExit, BrokerLost, BootstrapFailed, ContainmentUnconfirmed,
    CircuitTransition,
}
public enum GuardianHostWorkerLostReason
{ ProcessExit, ProtocolFatal, BrokerLost, ExecutionTimeout, HostShutdown }
public enum GuardianHostTerminationCertainty { Confirmed, Unconfirmed }
public enum GuardianHostEffectsState { NotDispatched, OutcomeUnknown, TerminalKnown }
public enum GuardianHostDiagnosticStream { Stdout, Stderr }
public enum GuardianHostJobState
{ Queued, Running, Completed, Failed, Canceled, TimedOut, Lost, OutcomeUnknown }
public enum GuardianHostOutputState { None, Streaming, Sealed, SealedIncomplete, Unavailable }
public enum GuardianHostOutputSealState { Complete, Incomplete }

public sealed class GuardianHostWorkerIdentity
{
    public GuardianHostWorkerIdentity(WorkerBootId bootId, WorkerGeneration generation)
    {
        GuardianHostDtoValidation.Require(bootId);
        GuardianHostDtoValidation.Require(generation);
        BootId = bootId;
        Generation = generation;
    }

    public WorkerBootId BootId { get; }
    public WorkerGeneration Generation { get; }
}

public sealed class GuardianHostOperationIdentity
{
    public GuardianHostOperationIdentity(PlanId planId, OperationId operationId)
    {
        GuardianHostDtoValidation.Require(planId);
        GuardianHostDtoValidation.Require(operationId);
        PlanId = planId;
        OperationId = operationId;
    }

    public PlanId PlanId { get; }
    public OperationId OperationId { get; }
}

public sealed class DispatchCapability
{
    public DispatchCapability(
        CapabilityToken token,
        CallId callId,
        long expiresUnixTimeMilliseconds)
    {
        GuardianHostDtoValidation.Require(token);
        GuardianHostDtoValidation.Require(callId);
        GuardianHostDtoValidation.RequirePositive(expiresUnixTimeMilliseconds, nameof(expiresUnixTimeMilliseconds));
        Token = token;
        CallId = callId;
        ExpiresUnixTimeMilliseconds = expiresUnixTimeMilliseconds;
    }

    public CapabilityToken Token { get; }
    public CallId CallId { get; }
    public long ExpiresUnixTimeMilliseconds { get; }
}

public sealed class OutputCapability
{
    public OutputCapability(
        CapabilityToken token,
        int maximumBytes,
        long expiresUnixTimeMilliseconds)
    {
        GuardianHostDtoValidation.Require(token);
        GuardianHostDtoValidation.RequireRange(
            maximumBytes, 1, ContractLimits.MaximumOutputBytes, nameof(maximumBytes));
        GuardianHostDtoValidation.RequirePositive(expiresUnixTimeMilliseconds, nameof(expiresUnixTimeMilliseconds));
        Token = token;
        MaximumBytes = maximumBytes;
        ExpiresUnixTimeMilliseconds = expiresUnixTimeMilliseconds;
    }

    public CapabilityToken Token { get; }
    public int MaximumBytes { get; }
    public long ExpiresUnixTimeMilliseconds { get; }
}

public abstract class GuardianHostMessage
{
    private protected GuardianHostMessage(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration)
    {
        GuardianHostDtoValidation.Require(guardianBootId);
        GuardianHostDtoValidation.Require(hostBootId);
        GuardianHostDtoValidation.Require(hostGeneration);
        GuardianBootId = guardianBootId;
        HostBootId = hostBootId;
        HostGeneration = hostGeneration;
    }

    public int ProtocolVersion => ContractLimits.GuardianHostProtocolVersion;
    public abstract GuardianHostMessageKind Kind { get; }
    public abstract GuardianHostPeer Sender { get; }
    public GuardianBootId GuardianBootId { get; }
    public HostBootId HostBootId { get; }
    public HostGeneration HostGeneration { get; }
}

public sealed class GuardianHostHello : GuardianHostMessage
{
    public GuardianHostHello(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        int hostPid,
        Sha256Digest hostExecutableDigest,
        Sha256Digest hostBuildDigest,
        Sha256Digest publicContractDigest,
        Sha256Digest configurationDigest)
        : base(guardianBootId, hostBootId, hostGeneration)
    {
        GuardianHostDtoValidation.RequirePositive(hostPid, nameof(hostPid));
        GuardianHostDtoValidation.Require(hostExecutableDigest);
        GuardianHostDtoValidation.Require(hostBuildDigest);
        GuardianHostDtoValidation.Require(publicContractDigest);
        GuardianHostDtoValidation.Require(configurationDigest);
        HostPid = hostPid;
        HostExecutableDigest = hostExecutableDigest;
        HostBuildDigest = hostBuildDigest;
        PublicContractDigest = publicContractDigest;
        ConfigurationDigest = configurationDigest;
    }

    public override GuardianHostMessageKind Kind => GuardianHostMessageKind.Hello;
    public override GuardianHostPeer Sender => GuardianHostPeer.Host;
    public int HostPid { get; }
    public Sha256Digest HostExecutableDigest { get; }
    public Sha256Digest HostBuildDigest { get; }
    public Sha256Digest PublicContractDigest { get; }
    public Sha256Digest ConfigurationDigest { get; }
    public bool RequestChannelOwned => true;
    public bool EventChannelOwned => true;
}

public sealed class GuardianHostInitialize : GuardianHostMessage
{
    public GuardianHostInitialize(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        PrivateRequestId requestId,
        Sha256Digest hostExecutableDigest,
        Sha256Digest hostBuildDigest,
        Sha256Digest publicContractDigest,
        Sha256Digest configurationDigest,
        Sha256Digest packageManifestDigest)
        : base(guardianBootId, hostBootId, hostGeneration)
    {
        GuardianHostDtoValidation.Require(requestId);
        GuardianHostDtoValidation.Require(hostExecutableDigest);
        GuardianHostDtoValidation.Require(hostBuildDigest);
        GuardianHostDtoValidation.Require(publicContractDigest);
        GuardianHostDtoValidation.Require(configurationDigest);
        GuardianHostDtoValidation.Require(packageManifestDigest);
        RequestId = requestId;
        HostExecutableDigest = hostExecutableDigest;
        HostBuildDigest = hostBuildDigest;
        PublicContractDigest = publicContractDigest;
        ConfigurationDigest = configurationDigest;
        PackageManifestDigest = packageManifestDigest;
    }

    public override GuardianHostMessageKind Kind => GuardianHostMessageKind.Initialize;
    public override GuardianHostPeer Sender => GuardianHostPeer.Guardian;
    public PrivateRequestId RequestId { get; }
    public int GuardianProtocolVersion => ContractLimits.GuardianHostProtocolVersion;
    public int HostProtocolVersion => ContractLimits.GuardianHostProtocolVersion;
    public Sha256Digest HostExecutableDigest { get; }
    public Sha256Digest HostBuildDigest { get; }
    public Sha256Digest PublicContractDigest { get; }
    public Sha256Digest ConfigurationDigest { get; }
    public Sha256Digest PackageManifestDigest { get; }
    public int MaximumManifestBytes => ContractLimits.MaximumManifestBytes;
    public int MaximumManifestChunkRawBytes => ContractLimits.MaximumManifestChunkBytes;
    public int MaximumAliases => ContractLimits.MaximumAliases;
    public int MaximumTemplates => ContractLimits.MaximumTemplates;
}

public sealed class GuardianHostReady : GuardianHostMessage
{
    public GuardianHostReady(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        PrivateRequestId initializeRequestId,
        ManifestId manifestId,
        Sha256Digest manifestDigest,
        int hostPid)
        : base(guardianBootId, hostBootId, hostGeneration)
    {
        GuardianHostDtoValidation.Require(initializeRequestId);
        GuardianHostDtoValidation.Require(manifestId);
        GuardianHostDtoValidation.Require(manifestDigest);
        GuardianHostDtoValidation.RequirePositive(hostPid, nameof(hostPid));
        InitializeRequestId = initializeRequestId;
        ManifestId = manifestId;
        ManifestDigest = manifestDigest;
        HostPid = hostPid;
    }

    public override GuardianHostMessageKind Kind => GuardianHostMessageKind.Ready;
    public override GuardianHostPeer Sender => GuardianHostPeer.Host;
    public PrivateRequestId InitializeRequestId { get; }
    public ManifestId ManifestId { get; }
    public Sha256Digest ManifestDigest { get; }
    public int HostPid { get; }
}

public abstract class GuardianHostRequest : GuardianHostMessage
{
    private protected GuardianHostRequest(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        PrivateRequestId requestId)
        : base(guardianBootId, hostBootId, hostGeneration)
    {
        GuardianHostDtoValidation.Require(requestId);
        RequestId = requestId;
    }

    public sealed override GuardianHostMessageKind Kind => GuardianHostMessageKind.Request;
    public sealed override GuardianHostPeer Sender => GuardianHostPeer.Guardian;
    public PrivateRequestId RequestId { get; }
    public abstract GuardianHostRequestMethod Method { get; }
    public abstract long? DeadlineUnixTimeMilliseconds { get; }
    public abstract CanonicalAlias? SessionAlias { get; }
    public abstract SessionTransitionVersion? SessionTransitionVersion { get; }
    public abstract GuardianHostWorkerIdentity? WorkerIdentity { get; }
    public abstract GuardianHostOperationIdentity? OperationIdentity { get; }
}

public sealed class ManifestHeaderRequest : GuardianHostRequest
{
    public ManifestHeaderRequest(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        PrivateRequestId requestId,
        ManifestId manifestId,
        int totalBytes,
        Sha256Digest manifestDigest,
        int aliasCount,
        int templateCount)
        : base(guardianBootId, hostBootId, hostGeneration, requestId)
    {
        GuardianHostDtoValidation.Require(manifestId);
        GuardianHostDtoValidation.RequireRange(totalBytes, 1, ContractLimits.MaximumManifestBytes, nameof(totalBytes));
        GuardianHostDtoValidation.Require(manifestDigest);
        GuardianHostDtoValidation.RequireRange(aliasCount, 1, ContractLimits.MaximumAliases, nameof(aliasCount));
        GuardianHostDtoValidation.RequireRange(templateCount, 0, ContractLimits.MaximumTemplates, nameof(templateCount));
        ManifestId = manifestId;
        TotalBytes = totalBytes;
        ManifestDigest = manifestDigest;
        AliasCount = aliasCount;
        TemplateCount = templateCount;
    }

    public override GuardianHostRequestMethod Method => GuardianHostRequestMethod.ManifestHeader;
    public override long? DeadlineUnixTimeMilliseconds => null;
    public override CanonicalAlias? SessionAlias => null;
    public override SessionTransitionVersion? SessionTransitionVersion => null;
    public override GuardianHostWorkerIdentity? WorkerIdentity => null;
    public override GuardianHostOperationIdentity? OperationIdentity => null;
    public ManifestId ManifestId { get; }
    public int TotalBytes { get; }
    public int ChunkCount => checked((TotalBytes + ContractLimits.MaximumManifestChunkBytes - 1) /
        ContractLimits.MaximumManifestChunkBytes);
    public Sha256Digest ManifestDigest { get; }
    public int AliasCount { get; }
    public int TemplateCount { get; }
}

public sealed class ManifestChunkRequest : GuardianHostRequest
{
    private readonly byte[] _rawBytes;

    public ManifestChunkRequest(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        PrivateRequestId requestId,
        ManifestId manifestId,
        int chunkIndex,
        ReadOnlyMemory<byte> rawBytes)
        : base(guardianBootId, hostBootId, hostGeneration, requestId)
    {
        GuardianHostDtoValidation.Require(manifestId);
        GuardianHostDtoValidation.RequireRange(chunkIndex, 0, ContractLimits.MaximumManifestChunks - 1, nameof(chunkIndex));
        GuardianHostDtoValidation.RequireRange(rawBytes.Length, 1, ContractLimits.MaximumManifestChunkBytes, nameof(rawBytes));
        ManifestId = manifestId;
        ChunkIndex = chunkIndex;
        _rawBytes = rawBytes.ToArray();
        RawDigest = Sha256Digest.Compute(_rawBytes);
    }

    public override GuardianHostRequestMethod Method => GuardianHostRequestMethod.ManifestChunk;
    public override long? DeadlineUnixTimeMilliseconds => null;
    public override CanonicalAlias? SessionAlias => null;
    public override SessionTransitionVersion? SessionTransitionVersion => null;
    public override GuardianHostWorkerIdentity? WorkerIdentity => null;
    public override GuardianHostOperationIdentity? OperationIdentity => null;
    public ManifestId ManifestId { get; }
    public int ChunkIndex { get; }
    public int Offset => checked(ChunkIndex * ContractLimits.MaximumManifestChunkBytes);
    public int RawByteCount => _rawBytes.Length;
    public Sha256Digest RawDigest { get; }
    public byte[] GetRawBytes() => _rawBytes.ToArray();
    internal ReadOnlySpan<byte> RawSpan => _rawBytes;
}

public sealed class ManifestSealRequest : GuardianHostRequest
{
    public ManifestSealRequest(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        PrivateRequestId requestId,
        ManifestId manifestId,
        int totalBytes,
        Sha256Digest manifestDigest)
        : base(guardianBootId, hostBootId, hostGeneration, requestId)
    {
        GuardianHostDtoValidation.Require(manifestId);
        GuardianHostDtoValidation.RequireRange(totalBytes, 1, ContractLimits.MaximumManifestBytes, nameof(totalBytes));
        GuardianHostDtoValidation.Require(manifestDigest);
        ManifestId = manifestId;
        TotalBytes = totalBytes;
        ManifestDigest = manifestDigest;
    }

    public override GuardianHostRequestMethod Method => GuardianHostRequestMethod.ManifestSeal;
    public override long? DeadlineUnixTimeMilliseconds => null;
    public override CanonicalAlias? SessionAlias => null;
    public override SessionTransitionVersion? SessionTransitionVersion => null;
    public override GuardianHostWorkerIdentity? WorkerIdentity => null;
    public override GuardianHostOperationIdentity? OperationIdentity => null;
    public ManifestId ManifestId { get; }
    public int TotalBytes { get; }
    public int ChunkCount => checked((TotalBytes + ContractLimits.MaximumManifestChunkBytes - 1) /
        ContractLimits.MaximumManifestChunkBytes);
    public Sha256Digest ManifestDigest { get; }
}

public sealed class OperationRequest : GuardianHostRequest
{
    private readonly long _deadline;
    private readonly CanonicalAlias _alias;
    private readonly SessionTransitionVersion _transitionVersion;

    public OperationRequest(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        PrivateRequestId requestId,
        long deadlineUnixTimeMilliseconds,
        CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion,
        GuardianHostWorkerIdentity? workerIdentity,
        GuardianHostOperationIdentity? operationIdentity,
        GuardianHostOperation operation)
        : base(guardianBootId, hostBootId, hostGeneration, requestId)
    {
        ArgumentNullException.ThrowIfNull(operation);
        GuardianHostDtoValidation.RequirePositive(deadlineUnixTimeMilliseconds, nameof(deadlineUnixTimeMilliseconds));
        GuardianHostDtoValidation.Require(sessionAlias);
        GuardianHostDtoValidation.RequirePositive(sessionTransitionVersion, nameof(sessionTransitionVersion));

        var needsWorker = operation.Kind is not GuardianHostOperationKind.SessionOpen;
        var permitsMissingWorker = operation.Kind is GuardianHostOperationKind.SessionClose or
            GuardianHostOperationKind.SessionRestart;
        var needsOperationIdentity = operation.Kind is GuardianHostOperationKind.InvokeForeground or
            GuardianHostOperationKind.InvokeBackground;
        if (needsWorker && !permitsMissingWorker && workerIdentity is null ||
            !needsWorker && workerIdentity is not null ||
            needsOperationIdentity != (operationIdentity is not null))
            throw new ArgumentException("Operation correlation does not match the operation kind.");

        _deadline = deadlineUnixTimeMilliseconds;
        _alias = sessionAlias;
        _transitionVersion = sessionTransitionVersion;
        Worker = workerIdentity;
        Identity = operationIdentity;
        Operation = operation;
    }

    public override GuardianHostRequestMethod Method => GuardianHostRequestMethod.Operation;
    public override long? DeadlineUnixTimeMilliseconds => _deadline;
    public override CanonicalAlias? SessionAlias => _alias;
    public override SessionTransitionVersion? SessionTransitionVersion => _transitionVersion;
    public override GuardianHostWorkerIdentity? WorkerIdentity => Worker;
    public override GuardianHostOperationIdentity? OperationIdentity => Identity;
    public GuardianHostWorkerIdentity? Worker { get; }
    public GuardianHostOperationIdentity? Identity { get; }
    public GuardianHostOperation Operation { get; }
}

public sealed class WorkerCreateCapabilityGrantRequest : GuardianHostRequest
{
    private readonly long _deadline;
    private readonly CanonicalAlias _alias;
    private readonly SessionTransitionVersion _transitionVersion;

    public WorkerCreateCapabilityGrantRequest(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        PrivateRequestId requestId,
        long deadlineUnixTimeMilliseconds,
        CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion,
        WorkerGeneration workerGeneration,
        HostEventSequence sourceEventSequence,
        CapabilityToken token)
        : this(guardianBootId, hostBootId, hostGeneration, requestId,
            deadlineUnixTimeMilliseconds, sessionAlias, sessionTransitionVersion,
            workerGeneration, sourceEventSequence, token, workerGeneration)
    {
    }

    private WorkerCreateCapabilityGrantRequest(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        PrivateRequestId requestId,
        long deadlineUnixTimeMilliseconds,
        CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion,
        WorkerGeneration workerGeneration,
        HostEventSequence sourceEventSequence,
        CapabilityToken token,
        WorkerGeneration grantedWorkerGeneration)
        : base(guardianBootId, hostBootId, hostGeneration, requestId)
    {
        GuardianHostDtoValidation.RequirePositive(deadlineUnixTimeMilliseconds, nameof(deadlineUnixTimeMilliseconds));
        GuardianHostDtoValidation.Require(sessionAlias);
        GuardianHostDtoValidation.RequirePositive(sessionTransitionVersion, nameof(sessionTransitionVersion));
        GuardianHostDtoValidation.Require(workerGeneration);
        GuardianHostDtoValidation.Require(sourceEventSequence);
        GuardianHostDtoValidation.Require(token);
        GuardianHostDtoValidation.Require(grantedWorkerGeneration);
        if (workerGeneration != grantedWorkerGeneration)
            throw new ArgumentException(
                "The envelope and granted worker generations must match.",
                nameof(grantedWorkerGeneration));
        _deadline = deadlineUnixTimeMilliseconds;
        _alias = sessionAlias;
        _transitionVersion = sessionTransitionVersion;
        WorkerGeneration = workerGeneration;
        SourceEventSequence = sourceEventSequence;
        Token = token;
        GrantedWorkerGeneration = grantedWorkerGeneration;
    }

    public override GuardianHostRequestMethod Method => GuardianHostRequestMethod.WorkerCreateCapabilityGrant;
    public override long? DeadlineUnixTimeMilliseconds => _deadline;
    public override CanonicalAlias? SessionAlias => _alias;
    public override SessionTransitionVersion? SessionTransitionVersion => _transitionVersion;
    public override GuardianHostWorkerIdentity? WorkerIdentity => null;
    public override GuardianHostOperationIdentity? OperationIdentity => null;
    public WorkerGeneration WorkerGeneration { get; }
    public HostEventSequence SourceEventSequence { get; }
    public CapabilityToken Token { get; }
    public WorkerGeneration GrantedWorkerGeneration { get; }
}

public abstract class WorkerContainmentAckRequest : GuardianHostRequest
{
    private readonly long _deadline;
    private readonly CanonicalAlias _alias;
    private readonly SessionTransitionVersion _transitionVersion;

    private protected WorkerContainmentAckRequest(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        PrivateRequestId requestId,
        long deadlineUnixTimeMilliseconds,
        CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion,
        GuardianHostWorkerIdentity workerIdentity,
        HostEventSequence sourceEventSequence)
        : base(guardianBootId, hostBootId, hostGeneration, requestId)
    {
        GuardianHostDtoValidation.RequirePositive(deadlineUnixTimeMilliseconds, nameof(deadlineUnixTimeMilliseconds));
        GuardianHostDtoValidation.Require(sessionAlias);
        GuardianHostDtoValidation.RequirePositive(sessionTransitionVersion, nameof(sessionTransitionVersion));
        ArgumentNullException.ThrowIfNull(workerIdentity);
        GuardianHostDtoValidation.Require(sourceEventSequence);
        _deadline = deadlineUnixTimeMilliseconds;
        _alias = sessionAlias;
        _transitionVersion = sessionTransitionVersion;
        Worker = workerIdentity;
        SourceEventSequence = sourceEventSequence;
    }

    public override long? DeadlineUnixTimeMilliseconds => _deadline;
    public override CanonicalAlias? SessionAlias => _alias;
    public override SessionTransitionVersion? SessionTransitionVersion => _transitionVersion;
    public override GuardianHostWorkerIdentity WorkerIdentity => Worker;
    public override GuardianHostOperationIdentity? OperationIdentity => null;
    public GuardianHostWorkerIdentity Worker { get; }
    public HostEventSequence SourceEventSequence { get; }
}

public sealed class WorkerContainmentPendingAckRequest : WorkerContainmentAckRequest
{
    public WorkerContainmentPendingAckRequest(GuardianBootId guardianBootId, HostBootId hostBootId,
        HostGeneration hostGeneration, PrivateRequestId requestId, long deadlineUnixTimeMilliseconds,
        CanonicalAlias sessionAlias, SessionTransitionVersion sessionTransitionVersion,
        GuardianHostWorkerIdentity workerIdentity, HostEventSequence sourceEventSequence)
        : base(guardianBootId, hostBootId, hostGeneration, requestId, deadlineUnixTimeMilliseconds,
            sessionAlias, sessionTransitionVersion, workerIdentity, sourceEventSequence) { }
    public override GuardianHostRequestMethod Method => GuardianHostRequestMethod.WorkerContainmentPendingAck;
}

public sealed class WorkerContainmentArmedAckRequest : WorkerContainmentAckRequest
{
    public WorkerContainmentArmedAckRequest(GuardianBootId guardianBootId, HostBootId hostBootId,
        HostGeneration hostGeneration, PrivateRequestId requestId, long deadlineUnixTimeMilliseconds,
        CanonicalAlias sessionAlias, SessionTransitionVersion sessionTransitionVersion,
        GuardianHostWorkerIdentity workerIdentity, HostEventSequence sourceEventSequence)
        : base(guardianBootId, hostBootId, hostGeneration, requestId, deadlineUnixTimeMilliseconds,
            sessionAlias, sessionTransitionVersion, workerIdentity, sourceEventSequence) { }
    public override GuardianHostRequestMethod Method => GuardianHostRequestMethod.WorkerContainmentArmedAck;
}

public sealed class WorkerContainmentRemoveAckRequest : WorkerContainmentAckRequest
{
    public WorkerContainmentRemoveAckRequest(GuardianBootId guardianBootId, HostBootId hostBootId,
        HostGeneration hostGeneration, PrivateRequestId requestId, long deadlineUnixTimeMilliseconds,
        CanonicalAlias sessionAlias, SessionTransitionVersion sessionTransitionVersion,
        GuardianHostWorkerIdentity workerIdentity, HostEventSequence sourceEventSequence)
        : base(guardianBootId, hostBootId, hostGeneration, requestId, deadlineUnixTimeMilliseconds,
            sessionAlias, sessionTransitionVersion, workerIdentity, sourceEventSequence) { }
    public override GuardianHostRequestMethod Method => GuardianHostRequestMethod.WorkerContainmentRemoveAck;
}

public sealed class GuardianHostCancel : GuardianHostMessage
{
    public GuardianHostCancel(GuardianBootId guardianBootId, HostBootId hostBootId,
        HostGeneration hostGeneration, PrivateRequestId requestId,
        PrivateRequestId targetRequestId, GuardianHostCancelReason reason)
        : base(guardianBootId, hostBootId, hostGeneration)
    {
        GuardianHostDtoValidation.Require(requestId);
        GuardianHostDtoValidation.Require(targetRequestId);
        GuardianHostDtoValidation.RequireDefined(reason, nameof(reason));
        RequestId = requestId;
        TargetRequestId = targetRequestId;
        Reason = reason;
    }
    public override GuardianHostMessageKind Kind => GuardianHostMessageKind.Cancel;
    public override GuardianHostPeer Sender => GuardianHostPeer.Guardian;
    public PrivateRequestId RequestId { get; }
    public PrivateRequestId TargetRequestId { get; }
    public GuardianHostCancelReason Reason { get; }
}

public sealed class GuardianHostShutdown : GuardianHostMessage
{
    public GuardianHostShutdown(GuardianBootId guardianBootId, HostBootId hostBootId,
        HostGeneration hostGeneration, PrivateRequestId requestId,
        long deadlineUnixTimeMilliseconds, GuardianHostShutdownReason reason)
        : base(guardianBootId, hostBootId, hostGeneration)
    {
        GuardianHostDtoValidation.Require(requestId);
        GuardianHostDtoValidation.RequirePositive(deadlineUnixTimeMilliseconds, nameof(deadlineUnixTimeMilliseconds));
        GuardianHostDtoValidation.RequireDefined(reason, nameof(reason));
        RequestId = requestId;
        DeadlineUnixTimeMilliseconds = deadlineUnixTimeMilliseconds;
        Reason = reason;
    }
    public override GuardianHostMessageKind Kind => GuardianHostMessageKind.Shutdown;
    public override GuardianHostPeer Sender => GuardianHostPeer.Guardian;
    public PrivateRequestId RequestId { get; }
    public long DeadlineUnixTimeMilliseconds { get; }
    public GuardianHostShutdownReason Reason { get; }
}

public abstract class GuardianHostOperation
{
    private protected GuardianHostOperation(CallId callId, DispatchCapability dispatchCapability,
        OutputCapability? outputCapability)
    {
        GuardianHostDtoValidation.Require(callId);
        ArgumentNullException.ThrowIfNull(dispatchCapability);
        if (dispatchCapability.CallId != callId)
            throw new ArgumentException("Dispatch capability call ID must match the operation call ID.", nameof(dispatchCapability));
        CallId = callId;
        DispatchCapability = dispatchCapability;
        OutputCapability = outputCapability;
    }

    public abstract GuardianHostOperationKind Kind { get; }
    public CallId CallId { get; }
    public DispatchCapability DispatchCapability { get; }
    public OutputCapability? OutputCapability { get; }
}

public sealed class InvokeForegroundOperation : GuardianHostOperation
{
    public InvokeForegroundOperation(CallId callId, DispatchCapability dispatchCapability,
        OutputCapability outputCapability, string script, bool raw, GuardianHostInvokeRoute route)
        : base(callId, dispatchCapability, outputCapability ?? throw new ArgumentNullException(nameof(outputCapability)))
    {
        GuardianHostDtoValidation.RequireScript(script, nameof(script));
        GuardianHostDtoValidation.RequireDefined(route, nameof(route));
        Script = script; Raw = raw; Route = route;
    }
    public override GuardianHostOperationKind Kind => GuardianHostOperationKind.InvokeForeground;
    public string Script { get; }
    public bool Raw { get; }
    public GuardianHostInvokeRoute Route { get; }
}

public sealed class InvokeBackgroundOperation : GuardianHostOperation
{
    public InvokeBackgroundOperation(CallId callId, DispatchCapability dispatchCapability,
        OutputCapability outputCapability, string script, bool raw, GuardianHostInvokeRoute route,
        PublicJobId publicJobId)
        : base(callId, dispatchCapability, outputCapability ?? throw new ArgumentNullException(nameof(outputCapability)))
    {
        GuardianHostDtoValidation.RequireScript(script, nameof(script));
        GuardianHostDtoValidation.RequireDefined(route, nameof(route));
        GuardianHostDtoValidation.Require(publicJobId);
        Script = script; Raw = raw; Route = route; PublicJobId = publicJobId;
    }
    public override GuardianHostOperationKind Kind => GuardianHostOperationKind.InvokeBackground;
    public string Script { get; }
    public bool Raw { get; }
    public GuardianHostInvokeRoute Route { get; }
    public PublicJobId PublicJobId { get; }
}

public sealed class JobListOperation : GuardianHostOperation
{
    public JobListOperation(CallId callId, DispatchCapability dispatchCapability)
        : base(callId, dispatchCapability, null) { }
    public override GuardianHostOperationKind Kind => GuardianHostOperationKind.JobList;
}

public abstract class GuardianHostJobIdentityOperation : GuardianHostOperation
{
    private protected GuardianHostJobIdentityOperation(CallId callId, DispatchCapability dispatchCapability,
        OutputCapability? outputCapability, PublicJobId publicJobId, CapabilityToken jobCapability)
        : base(callId, dispatchCapability, outputCapability)
    {
        GuardianHostDtoValidation.Require(publicJobId);
        GuardianHostDtoValidation.Require(jobCapability);
        PublicJobId = publicJobId;
        JobCapability = jobCapability;
    }
    public PublicJobId PublicJobId { get; }
    public CapabilityToken JobCapability { get; }
}

public sealed class JobStatusOperation : GuardianHostJobIdentityOperation
{
    public JobStatusOperation(CallId callId, DispatchCapability dispatchCapability,
        PublicJobId publicJobId, CapabilityToken jobCapability)
        : base(callId, dispatchCapability, null, publicJobId, jobCapability) { }
    public override GuardianHostOperationKind Kind => GuardianHostOperationKind.JobStatus;
}

public sealed class JobOutputOperation : GuardianHostJobIdentityOperation
{
    public JobOutputOperation(CallId callId, DispatchCapability dispatchCapability,
        OutputCapability outputCapability, PublicJobId publicJobId, CapabilityToken jobCapability,
        long offset)
        : base(callId, dispatchCapability, outputCapability ?? throw new ArgumentNullException(nameof(outputCapability)),
            publicJobId, jobCapability)
    {
        GuardianHostDtoValidation.RequireNonnegative(offset, nameof(offset));
        Offset = offset;
    }
    public override GuardianHostOperationKind Kind => GuardianHostOperationKind.JobOutput;
    public long Offset { get; }
}

public sealed class JobKillOperation : GuardianHostJobIdentityOperation
{
    public JobKillOperation(CallId callId, DispatchCapability dispatchCapability,
        PublicJobId publicJobId, CapabilityToken jobCapability)
        : base(callId, dispatchCapability, null, publicJobId, jobCapability) { }
    public override GuardianHostOperationKind Kind => GuardianHostOperationKind.JobKill;
}

public abstract class GuardianHostGenerationOperation : GuardianHostOperation
{
    private protected GuardianHostGenerationOperation(CallId callId, DispatchCapability dispatchCapability,
        long expectedGeneration, bool force)
        : base(callId, dispatchCapability, null)
    {
        GuardianHostDtoValidation.RequireNonnegative(expectedGeneration, nameof(expectedGeneration));
        ExpectedGeneration = expectedGeneration;
        Force = force;
    }
    public long ExpectedGeneration { get; }
    public bool Force { get; }
}

public sealed class ResetOperation : GuardianHostGenerationOperation
{
    public ResetOperation(CallId callId, DispatchCapability dispatchCapability,
        long expectedGeneration, bool force)
        : base(callId, dispatchCapability, expectedGeneration, force) { }
    public override GuardianHostOperationKind Kind => GuardianHostOperationKind.Reset;
}

public sealed class SessionOpenOperation : GuardianHostOperation
{
    public SessionOpenOperation(CallId callId, DispatchCapability dispatchCapability,
        CanonicalAlias? template, bool allowColdBackground)
        : base(callId, dispatchCapability, null)
    {
        if (template is { } value) GuardianHostDtoValidation.Require(value);
        Template = template;
        AllowColdBackground = allowColdBackground;
    }
    public override GuardianHostOperationKind Kind => GuardianHostOperationKind.SessionOpen;
    public CanonicalAlias? Template { get; }
    public bool AllowColdBackground { get; }
}

public sealed class SessionCloseOperation : GuardianHostGenerationOperation
{
    public SessionCloseOperation(CallId callId, DispatchCapability dispatchCapability,
        long expectedGeneration, bool force)
        : base(callId, dispatchCapability, expectedGeneration, force) { }
    public override GuardianHostOperationKind Kind => GuardianHostOperationKind.SessionClose;
}

public sealed class SessionRestartOperation : GuardianHostGenerationOperation
{
    public SessionRestartOperation(CallId callId, DispatchCapability dispatchCapability,
        long expectedGeneration, bool force)
        : base(callId, dispatchCapability, expectedGeneration, force) { }
    public override GuardianHostOperationKind Kind => GuardianHostOperationKind.SessionRestart;
}

public sealed class GuardianHostContainmentIdentity
{
    public GuardianHostContainmentIdentity(
        uint brokerPid,
        ulong brokerStartIdentityHigh,
        ulong brokerStartIdentityLow,
        uint workerPid,
        ulong workerStartIdentityHigh,
        ulong workerStartIdentityLow)
    {
        if (brokerPid == 0) throw new ArgumentOutOfRangeException(nameof(brokerPid));
        if (workerPid == 0) throw new ArgumentOutOfRangeException(nameof(workerPid));
        BrokerPid = brokerPid;
        BrokerStartIdentityHigh = brokerStartIdentityHigh;
        BrokerStartIdentityLow = brokerStartIdentityLow;
        WorkerPid = workerPid;
        WorkerStartIdentityHigh = workerStartIdentityHigh;
        WorkerStartIdentityLow = workerStartIdentityLow;
    }

    public uint BrokerPid { get; }
    public ulong BrokerStartIdentityHigh { get; }
    public ulong BrokerStartIdentityLow { get; }
    public uint WorkerPid { get; }
    public ulong WorkerStartIdentityHigh { get; }
    public ulong WorkerStartIdentityLow { get; }
    public uint ProcessGroupId => WorkerPid;
}

public abstract class GuardianHostEvent : GuardianHostMessage
{
    private protected GuardianHostEvent(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        HostEventSequence eventSequence,
        CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion,
        PrivateRequestId? requestId,
        GuardianHostWorkerIdentity? workerIdentity,
        GuardianHostOperationIdentity? operationIdentity)
        : base(guardianBootId, hostBootId, hostGeneration)
    {
        GuardianHostDtoValidation.Require(eventSequence);
        GuardianHostDtoValidation.Require(sessionAlias);
        GuardianHostDtoValidation.RequirePositive(sessionTransitionVersion, nameof(sessionTransitionVersion));
        if (requestId is not null) GuardianHostDtoValidation.Require(requestId);
        EventSequence = eventSequence;
        SessionAlias = sessionAlias;
        SessionTransitionVersion = sessionTransitionVersion;
        RequestId = requestId;
        WorkerIdentity = workerIdentity;
        OperationIdentity = operationIdentity;
    }

    public sealed override GuardianHostMessageKind Kind => GuardianHostMessageKind.Event;
    public sealed override GuardianHostPeer Sender => GuardianHostPeer.Host;
    public abstract GuardianHostEventType EventType { get; }
    public HostEventSequence EventSequence { get; }
    public PrivateRequestId? RequestId { get; }
    public CanonicalAlias SessionAlias { get; }
    public SessionTransitionVersion SessionTransitionVersion { get; }
    public GuardianHostWorkerIdentity? WorkerIdentity { get; }
    public GuardianHostOperationIdentity? OperationIdentity { get; }
}

public sealed class WorkerCreateCapabilityRequestedEvent : GuardianHostEvent
{
    public WorkerCreateCapabilityRequestedEvent(
        GuardianBootId guardianBootId, HostBootId hostBootId, HostGeneration hostGeneration,
        HostEventSequence eventSequence, CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion, Sha256Digest bindingDigest,
        long startupDeadlineUnixTimeMilliseconds)
        : base(guardianBootId, hostBootId, hostGeneration, eventSequence, sessionAlias,
            sessionTransitionVersion, null, null, null)
    {
        GuardianHostDtoValidation.Require(bindingDigest);
        GuardianHostDtoValidation.RequirePositive(startupDeadlineUnixTimeMilliseconds,
            nameof(startupDeadlineUnixTimeMilliseconds));
        BindingDigest = bindingDigest;
        StartupDeadlineUnixTimeMilliseconds = startupDeadlineUnixTimeMilliseconds;
    }
    public override GuardianHostEventType EventType => GuardianHostEventType.WorkerCreateCapabilityRequested;
    public Sha256Digest BindingDigest { get; }
    public long StartupDeadlineUnixTimeMilliseconds { get; }
}

public abstract class GuardianHostContainmentEvent : GuardianHostEvent
{
    private protected GuardianHostContainmentEvent(
        GuardianBootId guardianBootId, HostBootId hostBootId, HostGeneration hostGeneration,
        HostEventSequence eventSequence, CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion, GuardianHostWorkerIdentity workerIdentity,
        GuardianHostContainmentIdentity containmentIdentity)
        : base(guardianBootId, hostBootId, hostGeneration, eventSequence, sessionAlias,
            sessionTransitionVersion, null, workerIdentity ?? throw new ArgumentNullException(nameof(workerIdentity)), null)
    {
        ContainmentIdentity = containmentIdentity ?? throw new ArgumentNullException(nameof(containmentIdentity));
    }
    public GuardianHostContainmentIdentity ContainmentIdentity { get; }
}

public sealed class WorkerContainmentPendingEvent : GuardianHostContainmentEvent
{
    public WorkerContainmentPendingEvent(GuardianBootId guardianBootId, HostBootId hostBootId,
        HostGeneration hostGeneration, HostEventSequence eventSequence, CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion, GuardianHostWorkerIdentity workerIdentity,
        GuardianHostContainmentIdentity containmentIdentity)
        : base(guardianBootId, hostBootId, hostGeneration, eventSequence, sessionAlias,
            sessionTransitionVersion, workerIdentity, containmentIdentity) { }
    public override GuardianHostEventType EventType => GuardianHostEventType.WorkerContainmentPending;
}

public sealed class WorkerContainmentArmedEvent : GuardianHostContainmentEvent
{
    public WorkerContainmentArmedEvent(GuardianBootId guardianBootId, HostBootId hostBootId,
        HostGeneration hostGeneration, HostEventSequence eventSequence, CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion, GuardianHostWorkerIdentity workerIdentity,
        GuardianHostContainmentIdentity containmentIdentity)
        : base(guardianBootId, hostBootId, hostGeneration, eventSequence, sessionAlias,
            sessionTransitionVersion, workerIdentity, containmentIdentity) { }
    public override GuardianHostEventType EventType => GuardianHostEventType.WorkerContainmentArmed;
}

public sealed class WorkerContainmentRemoveRequestedEvent : GuardianHostContainmentEvent
{
    public WorkerContainmentRemoveRequestedEvent(GuardianBootId guardianBootId, HostBootId hostBootId,
        HostGeneration hostGeneration, HostEventSequence eventSequence, CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion, GuardianHostWorkerIdentity workerIdentity,
        GuardianHostContainmentIdentity containmentIdentity)
        : base(guardianBootId, hostBootId, hostGeneration, eventSequence, sessionAlias,
            sessionTransitionVersion, workerIdentity, containmentIdentity) { }
    public override GuardianHostEventType EventType => GuardianHostEventType.WorkerContainmentRemoveRequested;
}

public sealed class OperationDeliveryEvent : GuardianHostEvent
{
    public OperationDeliveryEvent(
        GuardianBootId guardianBootId, HostBootId hostBootId, HostGeneration hostGeneration,
        HostEventSequence eventSequence, PrivateRequestId requestId, CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion, GuardianHostWorkerIdentity workerIdentity,
        GuardianHostOperationIdentity? operationIdentity, CapabilityToken dispatchToken,
        GuardianHostDeliveryState deliveryState, PrivateRequestId? workerRequestId)
        : base(guardianBootId, hostBootId, hostGeneration, eventSequence, sessionAlias,
            sessionTransitionVersion, requestId, workerIdentity ?? throw new ArgumentNullException(nameof(workerIdentity)),
            operationIdentity)
    {
        GuardianHostDtoValidation.Require(dispatchToken);
        GuardianHostDtoValidation.RequireDefined(deliveryState, nameof(deliveryState));
        if (workerRequestId is not null) GuardianHostDtoValidation.Require(workerRequestId);
        if ((deliveryState == GuardianHostDeliveryState.NotDispatched) != (workerRequestId is null))
            throw new ArgumentException("Worker request ID does not match the delivery state.", nameof(workerRequestId));
        DispatchToken = dispatchToken;
        DeliveryState = deliveryState;
        WorkerRequestId = workerRequestId;
    }
    public override GuardianHostEventType EventType => GuardianHostEventType.OperationDelivery;
    public CapabilityToken DispatchToken { get; }
    public GuardianHostDeliveryState DeliveryState { get; }
    public PrivateRequestId? WorkerRequestId { get; }
}

public sealed class SessionLifecycleEvent : GuardianHostEvent
{
    public SessionLifecycleEvent(
        GuardianBootId guardianBootId, HostBootId hostBootId, HostGeneration hostGeneration,
        HostEventSequence eventSequence, PrivateRequestId? requestId, CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion, GuardianHostWorkerIdentity? workerIdentity,
        PublicSessionState? previousState, PublicSessionState state,
        GuardianHostSessionLifecycleReason reason, bool readyForEffects, bool warmStateLost,
        BootstrapState bootstrapState)
        : base(guardianBootId, hostBootId, hostGeneration, eventSequence, sessionAlias,
            sessionTransitionVersion, requestId, workerIdentity, null)
    {
        if (previousState is { } previous) GuardianHostDtoValidation.RequireDefined(previous, nameof(previousState));
        GuardianHostDtoValidation.RequireDefined(state, nameof(state));
        GuardianHostDtoValidation.RequireDefined(reason, nameof(reason));
        GuardianHostDtoValidation.RequireDefined(bootstrapState, nameof(bootstrapState));
        PreviousState = previousState; State = state; Reason = reason;
        ReadyForEffects = readyForEffects; WarmStateLost = warmStateLost; BootstrapState = bootstrapState;
    }
    public override GuardianHostEventType EventType => GuardianHostEventType.SessionLifecycle;
    public PublicSessionState? PreviousState { get; }
    public PublicSessionState State { get; }
    public GuardianHostSessionLifecycleReason Reason { get; }
    public bool ReadyForEffects { get; }
    public bool WarmStateLost { get; }
    public BootstrapState BootstrapState { get; }
}

public sealed class WorkerLostEvent : GuardianHostEvent
{
    public WorkerLostEvent(
        GuardianBootId guardianBootId, HostBootId hostBootId, HostGeneration hostGeneration,
        HostEventSequence eventSequence, PrivateRequestId? requestId, CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion, GuardianHostWorkerIdentity workerIdentity,
        GuardianHostOperationIdentity? operationIdentity, GuardianHostWorkerLostReason reason,
        int? exitCode, GuardianHostTerminationCertainty terminationCertainty,
        GuardianHostEffectsState effectsState)
        : base(guardianBootId, hostBootId, hostGeneration, eventSequence, sessionAlias,
            sessionTransitionVersion, requestId, workerIdentity ?? throw new ArgumentNullException(nameof(workerIdentity)),
            operationIdentity)
    {
        GuardianHostDtoValidation.RequireDefined(reason, nameof(reason));
        GuardianHostDtoValidation.RequireDefined(terminationCertainty, nameof(terminationCertainty));
        GuardianHostDtoValidation.RequireDefined(effectsState, nameof(effectsState));
        Reason = reason; ExitCode = exitCode; TerminationCertainty = terminationCertainty;
        EffectsState = effectsState;
    }
    public override GuardianHostEventType EventType => GuardianHostEventType.WorkerLost;
    public GuardianHostWorkerLostReason Reason { get; }
    public int? ExitCode { get; }
    public GuardianHostTerminationCertainty TerminationCertainty { get; }
    public GuardianHostEffectsState EffectsState { get; }
}

public abstract class GuardianHostWorkerDiagnosticEvent : GuardianHostEvent
{
    private protected GuardianHostWorkerDiagnosticEvent(
        GuardianBootId guardianBootId, HostBootId hostBootId, HostGeneration hostGeneration,
        HostEventSequence eventSequence, PrivateRequestId? requestId, CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion, GuardianHostWorkerIdentity workerIdentity,
        GuardianHostOperationIdentity? operationIdentity, GuardianHostDiagnosticStream stream)
        : base(guardianBootId, hostBootId, hostGeneration, eventSequence, sessionAlias,
            sessionTransitionVersion, requestId, workerIdentity ?? throw new ArgumentNullException(nameof(workerIdentity)),
            operationIdentity)
    {
        GuardianHostDtoValidation.RequireDefined(stream, nameof(stream));
        Stream = stream;
    }
    public GuardianHostDiagnosticStream Stream { get; }
}

public sealed class WorkerDiagnosticChunkEvent : GuardianHostWorkerDiagnosticEvent
{
    private readonly byte[] _rawBytes;
    public WorkerDiagnosticChunkEvent(
        GuardianBootId guardianBootId, HostBootId hostBootId, HostGeneration hostGeneration,
        HostEventSequence eventSequence, PrivateRequestId? requestId, CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion, GuardianHostWorkerIdentity workerIdentity,
        GuardianHostOperationIdentity? operationIdentity, GuardianHostDiagnosticStream stream,
        long chunkIndex, int offset, ReadOnlyMemory<byte> rawBytes, bool endOfStream)
        : base(guardianBootId, hostBootId, hostGeneration, eventSequence, requestId, sessionAlias,
            sessionTransitionVersion, workerIdentity, operationIdentity, stream)
    {
        GuardianHostDtoValidation.RequireNonnegative(chunkIndex, nameof(chunkIndex));
        GuardianHostDtoValidation.RequireRange(offset, 0, ContractLimits.MaximumDiagnosticBytesPerStream - 1, nameof(offset));
        GuardianHostDtoValidation.RequireRange(rawBytes.Length, 1, ContractLimits.MaximumDiagnosticChunkBytes, nameof(rawBytes));
        if ((long)offset + rawBytes.Length > ContractLimits.MaximumDiagnosticBytesPerStream)
            throw new ArgumentOutOfRangeException(nameof(rawBytes));
        ChunkIndex = chunkIndex; Offset = offset; _rawBytes = rawBytes.ToArray();
        RawDigest = Sha256Digest.Compute(_rawBytes); EndOfStream = endOfStream;
    }
    public override GuardianHostEventType EventType => GuardianHostEventType.WorkerDiagnosticChunk;
    public long ChunkIndex { get; }
    public int Offset { get; }
    public int RawByteCount => _rawBytes.Length;
    public Sha256Digest RawDigest { get; }
    public bool EndOfStream { get; }
    public byte[] GetRawBytes() => _rawBytes.ToArray();
    internal ReadOnlySpan<byte> RawSpan => _rawBytes;
}

public sealed class WorkerDiagnosticTruncatedEvent : GuardianHostWorkerDiagnosticEvent
{
    public WorkerDiagnosticTruncatedEvent(
        GuardianBootId guardianBootId, HostBootId hostBootId, HostGeneration hostGeneration,
        HostEventSequence eventSequence, PrivateRequestId? requestId, CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion, GuardianHostWorkerIdentity workerIdentity,
        GuardianHostOperationIdentity? operationIdentity, GuardianHostDiagnosticStream stream,
        long discardedBytes)
        : base(guardianBootId, hostBootId, hostGeneration, eventSequence, requestId, sessionAlias,
            sessionTransitionVersion, workerIdentity, operationIdentity, stream)
    {
        GuardianHostDtoValidation.RequirePositive(discardedBytes, nameof(discardedBytes));
        DiscardedBytes = discardedBytes;
    }
    public override GuardianHostEventType EventType => GuardianHostEventType.WorkerDiagnosticTruncated;
    public int CapturedBytes => ContractLimits.MaximumDiagnosticBytesPerStream;
    public long DiscardedBytes { get; }
}

public sealed class JobLifecycleEvent : GuardianHostEvent
{
    public JobLifecycleEvent(
        GuardianBootId guardianBootId, HostBootId hostBootId, HostGeneration hostGeneration,
        HostEventSequence eventSequence, PrivateRequestId? requestId, CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion, GuardianHostWorkerIdentity workerIdentity,
        GuardianHostOperationIdentity? operationIdentity, PublicJobId publicJobId,
        GuardianHostJobState state, int? exitCode, GuardianHostOutputState outputState,
        int outputBytes, Sha256Digest? outputDigest)
        : base(guardianBootId, hostBootId, hostGeneration, eventSequence, sessionAlias,
            sessionTransitionVersion, requestId, workerIdentity ?? throw new ArgumentNullException(nameof(workerIdentity)),
            operationIdentity)
    {
        GuardianHostDtoValidation.Require(publicJobId);
        GuardianHostDtoValidation.RequireDefined(state, nameof(state));
        GuardianHostDtoValidation.RequireDefined(outputState, nameof(outputState));
        GuardianHostDtoValidation.RequireRange(outputBytes, 0, ContractLimits.MaximumOutputBytes, nameof(outputBytes));
        if (outputDigest is not null) GuardianHostDtoValidation.Require(outputDigest);
        PublicJobId = publicJobId; State = state; ExitCode = exitCode; OutputState = outputState;
        OutputBytes = outputBytes; OutputDigest = outputDigest;
    }
    public override GuardianHostEventType EventType => GuardianHostEventType.JobLifecycle;
    public PublicJobId PublicJobId { get; }
    public GuardianHostJobState State { get; }
    public int? ExitCode { get; }
    public GuardianHostOutputState OutputState { get; }
    public int OutputBytes { get; }
    public Sha256Digest? OutputDigest { get; }
}

public sealed class OutputChunkEvent : GuardianHostEvent
{
    private readonly byte[] _rawBytes;
    public OutputChunkEvent(
        GuardianBootId guardianBootId, HostBootId hostBootId, HostGeneration hostGeneration,
        HostEventSequence eventSequence, PrivateRequestId requestId, CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion, GuardianHostWorkerIdentity workerIdentity,
        GuardianHostOperationIdentity? operationIdentity, CapabilityToken outputCapabilityToken,
        long chunkIndex, int offset, ReadOnlyMemory<byte> rawBytes)
        : base(guardianBootId, hostBootId, hostGeneration, eventSequence, sessionAlias,
            sessionTransitionVersion, requestId, workerIdentity ?? throw new ArgumentNullException(nameof(workerIdentity)),
            operationIdentity)
    {
        GuardianHostDtoValidation.Require(outputCapabilityToken);
        GuardianHostDtoValidation.RequireNonnegative(chunkIndex, nameof(chunkIndex));
        GuardianHostDtoValidation.RequireRange(offset, 0, ContractLimits.MaximumOutputBytes - 1, nameof(offset));
        GuardianHostDtoValidation.RequireRange(rawBytes.Length, 1, ContractLimits.MaximumOutputChunkBytes, nameof(rawBytes));
        if ((long)offset + rawBytes.Length > ContractLimits.MaximumOutputBytes)
            throw new ArgumentOutOfRangeException(nameof(rawBytes));
        OutputCapabilityToken = outputCapabilityToken; ChunkIndex = chunkIndex; Offset = offset;
        _rawBytes = rawBytes.ToArray(); RawDigest = Sha256Digest.Compute(_rawBytes);
    }
    public override GuardianHostEventType EventType => GuardianHostEventType.OutputChunk;
    public CapabilityToken OutputCapabilityToken { get; }
    public long ChunkIndex { get; }
    public int Offset { get; }
    public int RawByteCount => _rawBytes.Length;
    public Sha256Digest RawDigest { get; }
    public byte[] GetRawBytes() => _rawBytes.ToArray();
    internal ReadOnlySpan<byte> RawSpan => _rawBytes;
}

public sealed class OutputSealEvent : GuardianHostEvent
{
    public OutputSealEvent(
        GuardianBootId guardianBootId, HostBootId hostBootId, HostGeneration hostGeneration,
        HostEventSequence eventSequence, PrivateRequestId requestId, CanonicalAlias sessionAlias,
        SessionTransitionVersion sessionTransitionVersion, GuardianHostWorkerIdentity workerIdentity,
        GuardianHostOperationIdentity? operationIdentity, CapabilityToken outputCapabilityToken,
        GuardianHostOutputSealState state, int totalBytes, Sha256Digest outputDigest)
        : base(guardianBootId, hostBootId, hostGeneration, eventSequence, sessionAlias,
            sessionTransitionVersion, requestId, workerIdentity ?? throw new ArgumentNullException(nameof(workerIdentity)),
            operationIdentity)
    {
        GuardianHostDtoValidation.Require(outputCapabilityToken);
        GuardianHostDtoValidation.RequireDefined(state, nameof(state));
        GuardianHostDtoValidation.RequireRange(totalBytes, 0, ContractLimits.MaximumOutputBytes, nameof(totalBytes));
        GuardianHostDtoValidation.Require(outputDigest);
        OutputCapabilityToken = outputCapabilityToken; State = state; TotalBytes = totalBytes;
        OutputDigest = outputDigest;
    }
    public override GuardianHostEventType EventType => GuardianHostEventType.OutputSeal;
    public CapabilityToken OutputCapabilityToken { get; }
    public GuardianHostOutputSealState State { get; }
    public int TotalBytes { get; }
    public Sha256Digest OutputDigest { get; }
}

public abstract class GuardianHostResponse : GuardianHostMessage
{
    private protected GuardianHostResponse(GuardianBootId guardianBootId, HostBootId hostBootId,
        HostGeneration hostGeneration, PrivateRequestId requestId)
        : base(guardianBootId, hostBootId, hostGeneration)
    {
        GuardianHostDtoValidation.Require(requestId);
        RequestId = requestId;
    }
    public sealed override GuardianHostMessageKind Kind => GuardianHostMessageKind.Response;
    public sealed override GuardianHostPeer Sender => GuardianHostPeer.Host;
    public PrivateRequestId RequestId { get; }
}

public sealed class GuardianHostSuccessResponse : GuardianHostResponse
{
    public GuardianHostSuccessResponse(GuardianBootId guardianBootId, HostBootId hostBootId,
        HostGeneration hostGeneration, PrivateRequestId requestId, GuardianHostSuccessPayload payload)
        : base(guardianBootId, hostBootId, hostGeneration, requestId)
    { Payload = payload ?? throw new ArgumentNullException(nameof(payload)); }
    public GuardianHostSuccessPayload Payload { get; }
}

public sealed class GuardianHostErrorResponse : GuardianHostResponse
{
    public GuardianHostErrorResponse(GuardianBootId guardianBootId, HostBootId hostBootId,
        HostGeneration hostGeneration, PrivateRequestId requestId, GuardianHostPrivateError error)
        : base(guardianBootId, hostBootId, hostGeneration, requestId)
    {
        if (!Enum.IsDefined(error.DetailCode) ||
            error.MessageCode != GuardianHostPrivateError.MessageFor(error.DetailCode))
            throw new ArgumentException("Private error detail/message mapping is invalid.", nameof(error));
        Error = error;
    }
    public GuardianHostPrivateError Error { get; }
}

public abstract class GuardianHostSuccessPayload
{
    private protected GuardianHostSuccessPayload() { }
    public abstract GuardianHostResponseType ResponseType { get; }
}

public sealed class ManifestHeaderAccepted : GuardianHostSuccessPayload
{
    public ManifestHeaderAccepted(ManifestId manifestId)
    { GuardianHostDtoValidation.Require(manifestId); ManifestId = manifestId; }
    public override GuardianHostResponseType ResponseType => GuardianHostResponseType.ManifestHeaderAccepted;
    public ManifestId ManifestId { get; }
    public int NextChunkIndex => 0;
    public int NextOffset => 0;
}

public sealed class ManifestChunkAccepted : GuardianHostSuccessPayload
{
    public ManifestChunkAccepted(ManifestId manifestId, int chunkIndex, int nextOffset)
    {
        GuardianHostDtoValidation.Require(manifestId);
        GuardianHostDtoValidation.RequireRange(chunkIndex, 0, ContractLimits.MaximumManifestChunks - 1, nameof(chunkIndex));
        GuardianHostDtoValidation.RequireRange(nextOffset, 1, ContractLimits.MaximumManifestBytes, nameof(nextOffset));
        ManifestId = manifestId; ChunkIndex = chunkIndex; NextOffset = nextOffset;
    }
    public override GuardianHostResponseType ResponseType => GuardianHostResponseType.ManifestChunkAccepted;
    public ManifestId ManifestId { get; }
    public int ChunkIndex { get; }
    public int NextChunkIndex => ChunkIndex + 1;
    public int NextOffset { get; }
}

public sealed class ManifestSealed : GuardianHostSuccessPayload
{
    public ManifestSealed(ManifestId manifestId, Sha256Digest manifestDigest, int totalBytes)
    {
        GuardianHostDtoValidation.Require(manifestId); GuardianHostDtoValidation.Require(manifestDigest);
        GuardianHostDtoValidation.RequireRange(totalBytes, 1, ContractLimits.MaximumManifestBytes, nameof(totalBytes));
        ManifestId = manifestId; ManifestDigest = manifestDigest; TotalBytes = totalBytes;
    }
    public override GuardianHostResponseType ResponseType => GuardianHostResponseType.ManifestSealed;
    public ManifestId ManifestId { get; }
    public Sha256Digest ManifestDigest { get; }
    public int TotalBytes { get; }
}

public sealed class OperationCompleted : GuardianHostSuccessPayload
{
    public OperationCompleted(GuardianHostOperationResult result)
    { Result = result ?? throw new ArgumentNullException(nameof(result)); }
    public override GuardianHostResponseType ResponseType => GuardianHostResponseType.OperationCompleted;
    public GuardianHostOperationKind OperationKind => Result.OperationKind;
    public GuardianHostOperationResult Result { get; }
}

public sealed class ControlAcknowledged : GuardianHostSuccessPayload
{
    public ControlAcknowledged(HostEventSequence sourceEventSequence)
    { GuardianHostDtoValidation.Require(sourceEventSequence); SourceEventSequence = sourceEventSequence; }
    public override GuardianHostResponseType ResponseType => GuardianHostResponseType.ControlAcknowledged;
    public HostEventSequence SourceEventSequence { get; }
}

public sealed class ShutdownAccepted : GuardianHostSuccessPayload
{
    public override GuardianHostResponseType ResponseType => GuardianHostResponseType.ShutdownAccepted;
}

public abstract class GuardianHostOperationResult
{
    private protected GuardianHostOperationResult() { }
    public abstract GuardianHostOperationKind OperationKind { get; }
}

public abstract class GuardianHostTextOperationResult : GuardianHostOperationResult
{
    private protected GuardianHostTextOperationResult(string text)
    { GuardianHostDtoValidation.RequireTextResult(text, nameof(text)); Text = text; }
    public string Text { get; }
}

public sealed class InvokeForegroundResult : GuardianHostTextOperationResult
{ public InvokeForegroundResult(string text) : base(text) { } public override GuardianHostOperationKind OperationKind => GuardianHostOperationKind.InvokeForeground; }
public sealed class JobListResult : GuardianHostTextOperationResult
{ public JobListResult(string text) : base(text) { } public override GuardianHostOperationKind OperationKind => GuardianHostOperationKind.JobList; }
public sealed class JobStatusResult : GuardianHostTextOperationResult
{ public JobStatusResult(string text) : base(text) { } public override GuardianHostOperationKind OperationKind => GuardianHostOperationKind.JobStatus; }
public sealed class JobOutputResult : GuardianHostTextOperationResult
{ public JobOutputResult(string text) : base(text) { } public override GuardianHostOperationKind OperationKind => GuardianHostOperationKind.JobOutput; }
public sealed class JobKillResult : GuardianHostTextOperationResult
{ public JobKillResult(string text) : base(text) { } public override GuardianHostOperationKind OperationKind => GuardianHostOperationKind.JobKill; }

public sealed class InvokeBackgroundResult : GuardianHostOperationResult
{
    public InvokeBackgroundResult(PublicJobId publicJobId, CapabilityToken jobCapability)
    {
        GuardianHostDtoValidation.Require(publicJobId); GuardianHostDtoValidation.Require(jobCapability);
        PublicJobId = publicJobId; JobCapability = jobCapability;
    }
    public override GuardianHostOperationKind OperationKind => GuardianHostOperationKind.InvokeBackground;
    public PublicJobId PublicJobId { get; }
    public CapabilityToken JobCapability { get; }
}

public abstract class GuardianHostSessionOperationResult : GuardianHostOperationResult
{
    private protected GuardianHostSessionOperationResult(CanonicalAlias alias, PublicSessionState state,
        GuardianHostWorkerIdentity? workerIdentity, SessionTransitionVersion transitionVersion,
        bool readyForEffects, bool warmStateLost, BootstrapState bootstrapState)
    {
        GuardianHostDtoValidation.Require(alias); GuardianHostDtoValidation.RequireDefined(state, nameof(state));
        GuardianHostDtoValidation.Require(transitionVersion); GuardianHostDtoValidation.RequireDefined(bootstrapState, nameof(bootstrapState));
        if (readyForEffects && state != PublicSessionState.Ready)
            throw new ArgumentException("ready_for_effects=true requires state=ready.", nameof(readyForEffects));
        Alias = alias; State = state; WorkerIdentity = workerIdentity; TransitionVersion = transitionVersion;
        ReadyForEffects = readyForEffects; WarmStateLost = warmStateLost; BootstrapState = bootstrapState;
    }
    public CanonicalAlias Alias { get; }
    public PublicSessionState State { get; }
    public GuardianHostWorkerIdentity? WorkerIdentity { get; }
    public SessionTransitionVersion TransitionVersion { get; }
    public bool ReadyForEffects { get; }
    public bool WarmStateLost { get; }
    public BootstrapState BootstrapState { get; }
}

public sealed class ResetResult : GuardianHostSessionOperationResult
{
    public ResetResult(CanonicalAlias alias, PublicSessionState state, GuardianHostWorkerIdentity? workerIdentity,
        SessionTransitionVersion transitionVersion, bool readyForEffects, bool warmStateLost, BootstrapState bootstrapState)
        : base(alias, state, workerIdentity, transitionVersion, readyForEffects, warmStateLost, bootstrapState) { }
    public override GuardianHostOperationKind OperationKind => GuardianHostOperationKind.Reset;
}
public sealed class SessionOpenResult : GuardianHostSessionOperationResult
{
    public SessionOpenResult(CanonicalAlias alias, PublicSessionState state, GuardianHostWorkerIdentity? workerIdentity,
        SessionTransitionVersion transitionVersion, bool readyForEffects, bool warmStateLost, BootstrapState bootstrapState)
        : base(alias, state, workerIdentity, transitionVersion, readyForEffects, warmStateLost, bootstrapState) { }
    public override GuardianHostOperationKind OperationKind => GuardianHostOperationKind.SessionOpen;
}
public sealed class SessionCloseResult : GuardianHostSessionOperationResult
{
    public SessionCloseResult(CanonicalAlias alias, PublicSessionState state, GuardianHostWorkerIdentity? workerIdentity,
        SessionTransitionVersion transitionVersion, bool readyForEffects, bool warmStateLost, BootstrapState bootstrapState)
        : base(alias, state, workerIdentity, transitionVersion, readyForEffects, warmStateLost, bootstrapState) { }
    public override GuardianHostOperationKind OperationKind => GuardianHostOperationKind.SessionClose;
}
public sealed class SessionRestartResult : GuardianHostSessionOperationResult
{
    public SessionRestartResult(CanonicalAlias alias, PublicSessionState state, GuardianHostWorkerIdentity? workerIdentity,
        SessionTransitionVersion transitionVersion, bool readyForEffects, bool warmStateLost, BootstrapState bootstrapState)
        : base(alias, state, workerIdentity, transitionVersion, readyForEffects, warmStateLost, bootstrapState) { }
    public override GuardianHostOperationKind OperationKind => GuardianHostOperationKind.SessionRestart;
}

internal static class GuardianHostDtoValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void Require(CanonicalAlias value)
    { ArgumentNullException.ThrowIfNull(value); if (!ContractValidation.IsAlias(value.Value)) throw new ArgumentException("Alias is invalid.", nameof(value)); }
    internal static void Require(Sha256Digest value)
    { ArgumentNullException.ThrowIfNull(value); if (!ContractValidation.IsSha256(value.Value)) throw new ArgumentException("Digest is invalid.", nameof(value)); }
    internal static void Require(CapabilityToken value)
    { ArgumentNullException.ThrowIfNull(value); if (!ContractValidation.IsCapabilityToken(value.Value)) throw new ArgumentException("Capability is invalid.", nameof(value)); }
    internal static void Require(GuardianBootId value)
    { ArgumentNullException.ThrowIfNull(value); ContractValidation.RequireUuid(value.Value, 4, nameof(value)); }
    internal static void Require(HostBootId value)
    { ArgumentNullException.ThrowIfNull(value); ContractValidation.RequireUuid(value.Value, 4, nameof(value)); }
    internal static void Require(WorkerBootId value)
    { ArgumentNullException.ThrowIfNull(value); ContractValidation.RequireUuid(value.Value, 4, nameof(value)); }
    internal static void Require(ManifestId value)
    { ArgumentNullException.ThrowIfNull(value); ContractValidation.RequireUuid(value.Value, 4, nameof(value)); }
    internal static void Require(PlanId value)
    { ArgumentNullException.ThrowIfNull(value); ContractValidation.RequireUuid(value.Value, 4, nameof(value)); }
    internal static void Require(OperationId value)
    { ArgumentNullException.ThrowIfNull(value); ContractValidation.RequireUuid(value.Value, 4, nameof(value)); }
    internal static void Require(CallId value)
    { ArgumentNullException.ThrowIfNull(value); ContractValidation.RequireUuid(value.Value, 7, nameof(value)); }
    internal static void Require(HostGeneration value)
    { ArgumentNullException.ThrowIfNull(value); RequirePositive(value.Value, nameof(value)); }
    internal static void Require(WorkerGeneration value)
    { ArgumentNullException.ThrowIfNull(value); RequirePositive(value.Value, nameof(value)); }
    internal static void Require(PrivateRequestId value)
    { ArgumentNullException.ThrowIfNull(value); RequirePositive(value.Value, nameof(value)); }
    internal static void Require(HostEventSequence value)
    { ArgumentNullException.ThrowIfNull(value); RequirePositive(value.Value, nameof(value)); }
    internal static void Require(SessionTransitionVersion value)
    { ArgumentNullException.ThrowIfNull(value); RequireNonnegative(value.Value, nameof(value)); }
    internal static void Require(PublicJobId value)
    { ArgumentNullException.ThrowIfNull(value); RequirePositive(value.Value, nameof(value)); }
    internal static void RequirePositive(SessionTransitionVersion value, string name)
    { ArgumentNullException.ThrowIfNull(value); RequirePositive(value.Value, name); }

    internal static void RequirePositive(long value, string name)
    { if (value <= 0) throw new ArgumentOutOfRangeException(name); }
    internal static void RequireNonnegative(long value, string name)
    { if (value < 0) throw new ArgumentOutOfRangeException(name); }
    internal static void RequireRange(int value, int minimum, int maximum, string name)
    { if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(name); }
    internal static void RequireDefined<T>(T value, string name) where T : struct, Enum
    { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(name); }

    internal static void RequireScript(string value, string name) =>
        RequireBoundedText(value, ContractLimits.MaximumScriptBytes, ContractLimits.MaximumScriptBytes, name);
    internal static void RequireTextResult(string value, string name) =>
        RequireBoundedText(value, ContractLimits.MaximumTextResultBytes, ContractLimits.MaximumTextResultBytes, name);

    private static void RequireBoundedText(string value, int maximumScalars, int maximumUtf8Bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(value);
        var scalars = 0;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out _, out var consumed);
            if (status != OperationStatus.Done) throw new ArgumentException("Text is not valid Unicode scalar text.", name);
            scalars++;
            if (scalars > maximumScalars) throw new ArgumentOutOfRangeException(name);
            remaining = remaining[consumed..];
        }
        if (StrictUtf8.GetByteCount(value) > maximumUtf8Bytes) throw new ArgumentOutOfRangeException(name);
    }
}
