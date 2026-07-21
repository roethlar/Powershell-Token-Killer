using System.Text;
using PtkSharedContracts;

namespace PtkMcpServer.Audit;

/// <summary>
/// Frozen guardian-owned projection of one desired session binding and its
/// exact worker generation when one has been allocated. No private host state
/// is reread to reconstruct these facts.
/// </summary>
internal sealed record GuardianAuditSession
{
    internal GuardianAuditSession(
        RecoveryBinding binding,
        WorkerGeneration? generation,
        RecoveryTemplate? template = null)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var templateBinding = binding.BindingKind == RecoveryBindingKind.Template;
        if (templateBinding != (template is not null))
        {
            throw new ArgumentException(
                "Only a template binding may carry its frozen template facts.",
                nameof(template));
        }
        if (template is not null &&
            (binding.TemplateName != template.Name ||
             binding.TemplateDigest != template.TemplateDigest ||
             binding.BootstrapDigest != template.BootstrapDigest ||
             binding.AllowColdBackground != template.AllowColdBackground))
        {
            throw new ArgumentException(
                "The frozen template facts do not match the session binding.",
                nameof(template));
        }

        Session = new AuditSession
        {
            Name = binding.Alias.Value,
            Generation = generation?.Value,
            BindingKind = binding.BindingKind switch
            {
                RecoveryBindingKind.Default => "default",
                RecoveryBindingKind.Dynamic => "dynamic",
                RecoveryBindingKind.Template => "template",
                _ => throw new ArgumentOutOfRangeException(nameof(binding)),
            },
            TemplateName = binding.TemplateName?.Value,
            TemplateDigest = binding.TemplateDigest?.Value,
            BootstrapDigest = binding.BootstrapDigest?.Value,
            DeclaredPurpose = template?.Description,
            DeclaredTarget = template?.DeclaredTarget,
            DeclaredIdentity = template?.DeclaredIdentity,
            EffectiveIdentity = null,
            AllowColdBackground = binding.AllowColdBackground,
        };
    }

    internal AuditSession Session { get; }
}

/// <summary>
/// Exact immutable facts consumed by the guardian-to-host pre-write audit
/// barrier. A public job ID is mandatory exactly when the private operation
/// addresses or creates guardian-owned job state.
/// </summary>
internal sealed record GuardianAuditDispatchAuthorization
{
    internal GuardianAuditDispatchAuthorization(
        GuardianHostOperationKind operationKind,
        GuardianAuditSession session,
        PublicJobId? publicJobId = null)
    {
        if (!Enum.IsDefined(operationKind))
            throw new ArgumentOutOfRangeException(nameof(operationKind));
        ArgumentNullException.ThrowIfNull(session);
        if (session.Session.Generation is null)
        {
            throw new ArgumentException(
                "A private dispatch authorization requires an exact worker generation.",
                nameof(session));
        }

        var requiresJobId = operationKind is
            GuardianHostOperationKind.InvokeBackground or
            GuardianHostOperationKind.JobStatus or
            GuardianHostOperationKind.JobOutput or
            GuardianHostOperationKind.JobKill;
        if (requiresJobId != (publicJobId is not null))
        {
            throw new ArgumentException(
                "The public job ID does not match the private operation kind.",
                nameof(publicJobId));
        }

        OperationKind = operationKind;
        Session = session;
        PublicJobId = publicJobId;
    }

    internal GuardianHostOperationKind OperationKind { get; }

    internal GuardianAuditSession Session { get; }

    internal PublicJobId? PublicJobId { get; }
}

/// <summary>
/// Guardian-side audit capability for one admitted public MCP call. Its
/// dispatch record is an outer transport authorization, not a substitute for
/// a later plan-specific execution authorization inside the private protocol.
/// A true return proves the record crossed the durable journal barrier.
/// </summary>
internal sealed class GuardianAuditCall : AuditCallLifecycle
{
    private static readonly UTF8Encoding Utf8 = new(false);

    internal const string DispatchAuthorizedEvent = "guardian.dispatch_authorized";
    internal const string DispatchNotStartedEvent = "guardian.dispatch_not_started";
    internal const string DispatchCompletedEvent = "guardian.dispatch_completed";
    internal const string DispatchFailedEvent = "guardian.dispatch_failed";
    internal const string DispatchOutcomeUnknownEvent = "guardian.dispatch_outcome_unknown";

    private bool _privateWriteMayHaveStarted;
    private bool _dispatchTerminalObserved;

    internal GuardianAuditCall(
        AuditJournal journal,
        ScriptEvidenceStoreProvider evidence)
        : base(journal, evidence)
    {
    }

    internal CallId PublicCallId
    {
        get
        {
            EnsureActive();
            return new CallId(_callId);
        }
    }

    internal CanonicalAlias RequestedSessionAlias
    {
        get
        {
            EnsureActive();
            return new CanonicalAlias(_request!.SessionRequested ??
                throw new InvalidOperationException(
                    "The admitted guardian call has no requested session alias."));
        }
    }

    internal bool TryAuthorizeDispatch(
        GuardianAuditDispatchAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        EnsureActive();
        if (_effectAuthorized)
        {
            throw new InvalidOperationException(
                "The guardian dispatch was already authorized.");
        }

        if (!MatchesAcceptedOperation(_request!, authorization.OperationKind))
        {
            throw new InvalidOperationException(
                "The guardian dispatch operation does not match the accepted request.");
        }
        if (!StringComparer.Ordinal.Equals(
                _request!.SessionRequested,
                authorization.Session.Session.Name))
        {
            throw new InvalidOperationException(
                "The guardian dispatch session does not match the accepted request.");
        }

        var publicJobId = authorization.PublicJobId?.Value;
        var addressesExistingJob = authorization.OperationKind is
            GuardianHostOperationKind.JobStatus or
            GuardianHostOperationKind.JobOutput or
            GuardianHostOperationKind.JobKill;
        if (addressesExistingJob && _request.JobId != publicJobId)
        {
            throw new InvalidOperationException(
                "The guardian dispatch job ID does not match the accepted request.");
        }

        var previousRequest = _request;
        var previousSession = ProjectSession(authorization.Session.Session);
        if (publicJobId is not null)
            _request = _request with { JobId = publicJobId };

        try
        {
            Append(
                DispatchAuthorizedEvent,
                outcomeState: "authorized",
                detailCode: OperationCode(authorization.OperationKind),
                jobId: publicJobId,
                terminationCertainty: "not_applicable",
                rootCoverage: "none");
            _effectAuthorized = true;
            return true;
        }
        catch (AuditUnavailableException)
        {
            _request = previousRequest;
            _ = ProjectSession(previousSession);
            _authorizationPersistenceFailed = true;
            return false;
        }
    }

    private static bool MatchesAcceptedOperation(
        AuditRequest request,
        GuardianHostOperationKind operationKind) => request.Tool switch
    {
        "ptk_invoke" when StringComparer.Ordinal.Equals(request.Action, "invoke") =>
            request.Background == true
                ? operationKind == GuardianHostOperationKind.InvokeBackground
                : operationKind == GuardianHostOperationKind.InvokeForeground,
        "ptk_job" => request.Action switch
        {
            "list" => operationKind == GuardianHostOperationKind.JobList,
            "status" => operationKind == GuardianHostOperationKind.JobStatus,
            "output" => operationKind == GuardianHostOperationKind.JobOutput,
            "kill" => operationKind == GuardianHostOperationKind.JobKill,
            _ => false,
        },
        "ptk_reset" when StringComparer.Ordinal.Equals(request.Action, "reset") =>
            operationKind == GuardianHostOperationKind.Reset,
        "ptk_session" => request.Action switch
        {
            "open" => operationKind == GuardianHostOperationKind.SessionOpen,
            "close" => operationKind == GuardianHostOperationKind.SessionClose,
            "restart" => operationKind == GuardianHostOperationKind.SessionRestart,
            _ => false,
        },
        _ => false,
    };

    /// <summary>
    /// Closes an admitted call whose private write was proved not to have
    /// started. This is valid both for an early readiness refusal and for a
    /// dispatch authorization invalidated by the final pre-write recheck.
    /// </summary>
    internal void RecordNotDispatched(
        GuardianAuditSession session,
        string detailCode,
        string response)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(detailCode);
        EnsureDispatchTerminalAvailable();
        if (_privateWriteMayHaveStarted)
        {
            throw new InvalidOperationException(
                "A possibly written guardian dispatch cannot be recorded as not started.");
        }
        if (_effectAuthorized)
        {
            if (_session != session.Session)
            {
                throw new InvalidOperationException(
                    "The no-start session does not match the authorized guardian dispatch.");
            }
        }
        else
        {
            _ = ProjectSession(session.Session);
        }

        _dispatchTerminalObserved = true;
        TryAppend(
            DispatchNotStartedEvent,
            outcomeState: "not_started",
            detailCode,
            bytesReturned: Utf8.GetByteCount(response ?? string.Empty),
            terminationCertainty: "not_applicable",
            rootCoverage: "none");
        CompleteCall("not_started", response ?? string.Empty);
    }

    /// <summary>
    /// Advances the in-memory delivery fence immediately before the first
    /// possibly-writing private API. The durable authorization must already
    /// exist; no later path may claim proved-no-start after this edge.
    /// </summary>
    internal void MarkPrivateWriteStarting()
    {
        EnsureActive();
        if (!_effectAuthorized)
        {
            throw new InvalidOperationException(
                "A private write cannot start without durable guardian authorization.");
        }
        if (_privateWriteMayHaveStarted)
            throw new InvalidOperationException("The guardian private write already started.");

        _privateWriteMayHaveStarted = true;
        // At this boundary the private host may begin the requested work. The
        // inherited flag is conservative: a later audit failure must never be
        // rewritten as a pre-effect authorization refusal.
        _userExecutionStarted = true;
    }

    internal void RecordDecodedTerminal(
        bool isError,
        string response,
        string? detailCode = null)
    {
        EnsureWrittenDispatchTerminalAvailable();
        _dispatchTerminalObserved = true;
        TryAppend(
            isError ? DispatchFailedEvent : DispatchCompletedEvent,
            outcomeState: isError ? "failed" : "completed",
            detailCode,
            bytesReturned: Utf8.GetByteCount(response ?? string.Empty),
            terminationCertainty: "confirmed",
            rootCoverage: "unknown");
        CompleteCall(isError ? "failed" : "completed", response ?? string.Empty);
    }

    internal void RecordOutcomeUnknown(string response)
    {
        EnsureWrittenDispatchTerminalAvailable();
        _dispatchTerminalObserved = true;
        TryAppend(
            DispatchOutcomeUnknownEvent,
            outcomeState: "outcome_unknown",
            detailCode: "outcome_unknown",
            bytesReturned: Utf8.GetByteCount(response ?? string.Empty),
            terminationCertainty: "unknown",
            rootCoverage: "unknown");
        CompleteCall(
            "failed",
            response ?? string.Empty,
            terminationCertainty: "unknown");
    }

    private void EnsureDispatchTerminalAvailable()
    {
        EnsureActive();
        if (_dispatchTerminalObserved)
            throw new InvalidOperationException("The guardian dispatch is already terminal.");
    }

    private void EnsureWrittenDispatchTerminalAvailable()
    {
        EnsureDispatchTerminalAvailable();
        if (!_privateWriteMayHaveStarted)
        {
            throw new InvalidOperationException(
                "A guardian dispatch without a private write cannot have a backend terminal.");
        }
    }

    private static string OperationCode(GuardianHostOperationKind operationKind) =>
        operationKind switch
        {
            GuardianHostOperationKind.InvokeForeground => "invoke_foreground",
            GuardianHostOperationKind.InvokeBackground => "invoke_background",
            GuardianHostOperationKind.JobList => "job_list",
            GuardianHostOperationKind.JobStatus => "job_status",
            GuardianHostOperationKind.JobOutput => "job_output",
            GuardianHostOperationKind.JobKill => "job_kill",
            GuardianHostOperationKind.Reset => "reset",
            GuardianHostOperationKind.SessionOpen => "session_open",
            GuardianHostOperationKind.SessionClose => "session_close",
            GuardianHostOperationKind.SessionRestart => "session_restart",
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind)),
        };
}
