using PtkSharedContracts;

namespace PtkMcpServer.Sessions;

/// <summary>
/// Host-side authority for one already-validated private operation request.
/// The wire deadline is retained exactly; private runtime paths must not derive
/// a replacement budget from host defaults or optional audit context.
/// </summary>
internal sealed class SessionOperationAuthority
{
    private readonly GuardianHostOperation _operation;

    private SessionOperationAuthority(
        OperationRequest request,
        SessionBackgroundJobIdentity? backgroundJob)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deadlineUnixTimeMilliseconds = request.DeadlineUnixTimeMilliseconds ??
            throw new ArgumentException(
                "A private operation request must carry an absolute deadline.",
                nameof(request));

        _operation = request.Operation;
        AbsoluteDeadlineUnixTimeMilliseconds = deadlineUnixTimeMilliseconds;
        AbsoluteDeadlineUtc = ProjectDeadlineUtc(
            deadlineUnixTimeMilliseconds);
        BackgroundJob = backgroundJob;
    }

    internal long AbsoluteDeadlineUnixTimeMilliseconds { get; }

    /// <summary>
    /// Projection for existing host APIs. The wire accepts every positive
    /// Int64, while DateTimeOffset ends at year 9999; an out-of-range future
    /// deadline is clamped to MaxValue. That can only shorten authority and
    /// never extends the exact wire deadline retained above.
    /// </summary>
    internal DateTimeOffset AbsoluteDeadlineUtc { get; }

    /// <summary>
    /// Present only for a background invoke. Job-control capabilities remain
    /// on their exact typed operation DTO and are never widened into ambient
    /// session authority.
    /// </summary>
    internal SessionBackgroundJobIdentity? BackgroundJob { get; }

    internal static SessionOperationAuthority Create(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Operation is InvokeBackgroundOperation)
        {
            throw new ArgumentException(
                "A background invoke requires its host-created return capability.",
                nameof(request));
        }

        return new SessionOperationAuthority(request, backgroundJob: null);
    }

    /// <summary>
    /// Binds the guardian-reserved wire ID to the fresh capability created by
    /// the trusted host adapter for the eventual InvokeBackgroundResult.
    /// </summary>
    internal static SessionOperationAuthority CreateBackgroundInvoke(
        OperationRequest request,
        CapabilityToken hostCreatedJobCapability)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hostCreatedJobCapability);
        if (request.Operation is not InvokeBackgroundOperation background)
        {
            throw new ArgumentException(
                "A host-created return capability is valid only for a background invoke.",
                nameof(request));
        }

        return new SessionOperationAuthority(
            request,
            new SessionBackgroundJobIdentity(
                background.PublicJobId,
                hostCreatedJobCapability));
    }

    internal TOperation RequireOperation<TOperation>(TOperation operation)
        where TOperation : GuardianHostOperation
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!ReferenceEquals(_operation, operation))
        {
            throw new ArgumentException(
                "The operation authority does not belong to this operation instance.",
                nameof(operation));
        }

        return operation;
    }

    internal void ThrowIfExpired(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >=
            AbsoluteDeadlineUnixTimeMilliseconds)
            throw new SessionOperationDeadlineException();
    }

    private static DateTimeOffset ProjectDeadlineUtc(
        long deadlineUnixTimeMilliseconds) =>
        deadlineUnixTimeMilliseconds >
            DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.FromUnixTimeMilliseconds(
                deadlineUnixTimeMilliseconds);
}

internal sealed record SessionBackgroundJobIdentity
{
    internal SessionBackgroundJobIdentity(
        PublicJobId publicJobId,
        CapabilityToken jobCapability)
    {
        ArgumentNullException.ThrowIfNull(publicJobId);
        ArgumentNullException.ThrowIfNull(jobCapability);
        PublicJobId = publicJobId;
        JobCapability = jobCapability;
    }

    internal PublicJobId PublicJobId { get; }

    internal CapabilityToken JobCapability { get; }
}

internal sealed class SessionOperationDeadlineException : TimeoutException
{
    internal SessionOperationDeadlineException()
        : base("The private operation request deadline has expired.")
    {
    }

    internal GuardianHostPrivateDetailCode DetailCode =>
        GuardianHostPrivateDetailCode.RequestDeadlineExpired;
}
