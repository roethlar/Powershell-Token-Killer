using System.Text;

namespace PtkMcpServer.Audit;

/// <summary>
/// One request-scoped audit lifecycle. The MCP filter initializes it before a
/// tool is resolved; tools may obtain effect authorization only through the
/// durable event methods below. No submitted script text enters core events.
/// </summary>
internal sealed class AuditCallContext : IInvocationAuthorizer
{
    private static readonly UTF8Encoding Utf8 = new(false);

    internal const string NotStartedMessage =
        "[operation not started] Required audit persistence is unavailable; the original operation was not started.";

    private readonly AuditJournal _journal;
    private readonly ScriptEvidenceStoreProvider _evidence;
    private AuditReservation? _reservation;
    private AuditCallMetadata? _metadata;
    private AuditRequest? _request;
    private AuditRouting _routing = new();
    private Guid _callId;
    private Guid? _parentEventId;
    private Guid? _planId;
    private ExecutionPlan? _authorizedPlan;
    private ExecutionDispatch? _authorizedDispatch;
    private bool _accepted;
    private bool _validationStarted;
    private bool _validationCompleted;
    private bool _jobStartRequested;
    private bool _effectAuthorized;
    private bool _authorizationPersistenceFailed;
    private bool _terminalWritten;
    private DateTimeOffset _startedUtc;
    private AuditJobTerminalLease? _jobTerminalLease;

    internal AuditCallContext(AuditJournal journal, ScriptEvidenceStore evidence)
        : this(journal, new ScriptEvidenceStoreProvider(evidence))
    {
    }

    internal AuditCallContext(AuditJournal journal, ScriptEvidenceStoreProvider evidence)
    {
        _journal = journal;
        _evidence = evidence;
    }

    internal bool Accepted => _accepted;

    internal bool EffectAuthorized => _effectAuthorized;

    internal bool AuthorizationPersistenceFailed => _authorizationPersistenceFailed;

    internal bool TerminalWritten => _terminalWritten;


    internal AuditCallMetadata Metadata =>
        _metadata ?? throw new InvalidOperationException("Audit call has not been initialized.");

    internal bool TryBegin(
        AuditCallMetadata metadata,
        string? exactSubmittedScript,
        out string? failureClass)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (_accepted || _reservation is not null)
            throw new InvalidOperationException("Audit call was already initialized.");

        if (metadata.OperationProfile.RequiresScriptEvidence)
        {
            var health = _journal.Health.Snapshot();
            if (health.State == AuditHealthState.Unavailable &&
                string.Equals(health.FailureClass, "evidence.storage", StringComparison.Ordinal) &&
                (!_evidence.Probe() ||
                 !_journal.TryRecoverExternal(
                     "evidence.storage",
                     metadata.OperationProfile.MaximumRecordSlots)))
            {
                failureClass = "evidence.storage";
                return false;
            }
        }

        if (!_journal.TryReserve(
                metadata.OperationProfile.MaximumRecordSlots,
                out var reservation,
                out failureClass))
        {
            return false;
        }

        _reservation = reservation;
        _metadata = metadata;
        _request = metadata.Request;
        _startedUtc = DateTimeOffset.UtcNow;
        IScriptEvidencePublication? evidencePublication = null;
        var auditAppendAttempted = false;

        try
        {
            _callId = _journal.CreateCallId();
            if (metadata.OperationProfile.RequiresScriptEvidence)
            {
                if (exactSubmittedScript is null)
                    throw new InvalidOperationException("Script evidence was required but absent.");

                try
                {
                    evidencePublication = _evidence.Publish(
                        exactSubmittedScript,
                        _journal,
                        new AuditEvidenceRetentionContext(
                            _callId,
                            CallSession(),
                            metadata.Actor));
                }
                catch (ArgumentOutOfRangeException)
                {
                    failureClass = "evidence.limit";
                    return false;
                }
                catch (ScriptEvidenceStorageException)
                {
                    _journal.EnterExternalUnavailable("evidence.storage");
                    failureClass = "evidence.storage";
                    return false;
                }

                var evidence = evidencePublication.Reference;
                _request = _request with
                {
                    OriginalScriptDigest = evidence.ScriptDigest,
                    ScriptEvidenceId = Guid.Parse(evidence.EvidenceId),
                };
            }

            _routing = new AuditRouting
            {
                RequestedRoute = _request.Route,
                PermittedFallbacks = [],
            };
            auditAppendAttempted = true;
            Append("call.accepted", outcomeState: "accepted");
            _accepted = true;
            try
            {
                evidencePublication?.CompleteAfterAuditAppend();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // call.accepted is already durable and owns the remaining
                // terminal reservation. A publication-lock release failure
                // degrades later evidence admission; it must never erase this
                // accepted call or strand its terminal obligation.
                _journal.EnterExternalUnavailable("evidence.storage");
            }
            failureClass = null;
            return true;
        }
        catch (AuditUnavailableException)
        {
            failureClass = "journal.persistence";
            return false;
        }
        catch (AuditEventValidationException)
        {
            _journal.Health.MarkUnavailable("journal.schema");
            failureClass = "journal.schema";
            return false;
        }
        finally
        {
            try
            {
                if (!_accepted &&
                    evidencePublication is not null)
                {
                    try
                    {
                        if (auditAppendAttempted)
                        {
                            // The publication already owns the evidence quota.
                            // Reconcile through that capability before Dispose;
                            // reacquiring via the provider would self-deadlock.
                            _ = evidencePublication.ReconcileAfterAmbiguousAuditAppend(
                                _journal);
                        }
                        else
                        {
                            evidencePublication.AbandonBeforeAuditAppend();
                        }
                    }
                    catch (ScriptEvidenceStorageException)
                    {
                        _journal.EnterExternalUnavailable("evidence.storage");
                        if (!auditAppendAttempted)
                            failureClass = "evidence.storage";
                    }
                }
            }
            finally
            {
                try
                {
                    evidencePublication?.Dispose();
                }
                finally
                {
                    if (!_accepted)
                    {
                        _reservation?.Release();
                        _reservation = null;
                    }
                }
            }
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    internal bool BeginValidation()
    {
        EnsureActive();
        if (_validationStarted) return true;

        _planId ??= Guid.NewGuid();
        var requested = _request!.Route ?? "auto";
        _routing = _routing with
        {
            RequestedRoute = requested,
            PermittedFallbacks = [],
        };
        try
        {
            Append("execution.validation_started", outcomeState: "started");
            Append("execution.prepare_authorized", outcomeState: "authorized");
            _validationStarted = true;
            return true;
        }
        catch (AuditUnavailableException)
        {
            _authorizationPersistenceFailed = true;
            return false;
        }
    }

    public ValueTask<bool> AuthorizePlanAsync(
        ExecutionPlan plan,
        CancellationToken _)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureActive();
        if (_authorizedPlan is not null || _effectAuthorized)
            throw new InvalidOperationException("An execution plan was already authorized.");
        if (_planId is null && !BeginValidation())
        {
            _authorizationPersistenceFailed = true;
            return ValueTask.FromResult(false);
        }

        _routing = _routing with
        {
            Domain = plan.Domain?.ToMachineCode(),
            RequestedRoute = plan.RequestedRoute.ToMachineCode(),
            EffectiveRoute = plan.EffectiveRoute,
            PermittedFallbacks = plan.PermittedFallbacks
                .Select(path => path.ToMachineCode())
                .ToArray(),
            RtkVersion = plan.RtkExecutableIdentity?.Verified?.Version,
            RtkBinaryDigest = plan.RtkExecutableIdentity?.Verified?.BinaryDigest,
            Provenance = plan.OutputProvenance.ToMachineCode(),
            FallbackReason = plan.FallbackReason?.ToMachineCode(),
        };

        try
        {
            Append("execution.validation_completed", outcomeState: "completed");
            _validationCompleted = true;
            Append("execution.planned", outcomeState: "planned");
            _authorizedPlan = plan;
            return ValueTask.FromResult(true);
        }
        catch (AuditUnavailableException)
        {
            _authorizationPersistenceFailed = true;
            return ValueTask.FromResult(false);
        }
    }

    public ValueTask<bool> AuthorizeDispatchAsync(
        ExecutionDispatch dispatch,
        CancellationToken _)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        EnsureActive();
        if (_authorizedPlan is null || !ReferenceEquals(_authorizedPlan, dispatch.Plan))
            throw new InvalidOperationException("The dispatch does not belong to the authorized plan.");
        if (_authorizedDispatch is not null &&
            (_authorizedDispatch.ExecutionPath != ExecutionPath.Rtk ||
             !dispatch.IsFallback))
        {
            throw new InvalidOperationException(
                "Only an authorized RTK dispatch may be superseded by its exact fallback.");
        }

        // A second ordered dispatch does not mean a second execution. It may
        // only supersede an RTK pre-effect authorization with the exact
        // fallback already bounded by the same plan; terminal routing below
        // then inherits this actual dispatch.

        _routing = _routing with
        {
            Domain = dispatch.Domain?.ToMachineCode(),
            RequestedRoute = dispatch.RequestedRoute.ToMachineCode(),
            EffectiveRoute = dispatch.EffectiveRoute,
            PermittedFallbacks = dispatch.PermittedFallbacks
                .Select(path => path.ToMachineCode())
                .ToArray(),
            RtkVersion = dispatch.RtkExecutableIdentity?.Verified?.Version,
            RtkBinaryDigest = dispatch.RtkExecutableIdentity?.Verified?.BinaryDigest,
            Provenance = dispatch.OutputProvenance.ToMachineCode(),
            FallbackReason = dispatch.FallbackReason?.ToMachineCode(),
        };

        try
        {
            Append(
                "execution.dispatched",
                outcomeState: "dispatched",
                detailCode: dispatch.IsFallback
                    ? dispatch.FallbackReason?.ToMachineCode()
                    : null);
            _authorizedDispatch = dispatch;
            _effectAuthorized = true;
            return ValueTask.FromResult(true);
        }
        catch (AuditUnavailableException)
        {
            _authorizationPersistenceFailed = true;
            return ValueTask.FromResult(false);
        }
    }

    internal void RecordValidationNoStart(string detailCode)
    {
        EnsureActive();
        if (_planId is null && !BeginValidation()) return;
        try
        {
            Append("execution.validation_completed", "not_started", detailCode);
            _validationCompleted = true;
        }
        catch (AuditUnavailableException)
        {
            _authorizationPersistenceFailed = true;
        }
    }

    internal bool BeginJobStartRequest(long jobId)
    {
        EnsureActive();
        if (_jobStartRequested)
            return _request!.JobId == jobId;

        _request = _request! with { JobId = jobId };
        try
        {
            Append("job.start_requested", outcomeState: "requested", jobId: jobId);
            _jobStartRequested = true;
        }
        catch (AuditUnavailableException)
        {
            _authorizationPersistenceFailed = true;
            return false;
        }

        return BeginValidation();
    }

    internal AuditJobTerminalLease? AuthorizeJobStart(long jobId, string? cwd)
    {
        EnsureActive();
        if (_jobStartRequested && _request!.JobId != jobId)
            throw new InvalidOperationException("The authorized job ID does not match the requested job ID.");
        if ((!_jobStartRequested && !BeginJobStartRequest(jobId)) ||
            (!_validationStarted && !BeginValidation()))
        {
            _authorizationPersistenceFailed = true;
            return null;
        }

        _request = _request! with { JobId = jobId, Cwd = cwd };
        _routing = _routing with
        {
            Domain = "powershell",
            EffectiveRoute = "powershell_direct",
            PermittedFallbacks = [],
            FallbackReason = null,
        };

        try
        {
            Append("execution.validation_completed", outcomeState: "completed");
            _validationCompleted = true;
            Append("execution.planned", outcomeState: "planned");
            Append("execution.dispatched", outcomeState: "dispatched");
            var terminalReservation = _reservation!.TransferSlot();
            _effectAuthorized = true;
            _jobTerminalLease = new AuditJobTerminalLease(
                _journal,
                terminalReservation,
                Metadata.Actor,
                _request,
                _routing,
                _callId,
                _parentEventId,
                _planId,
                jobId);
            return _jobTerminalLease;
        }
        catch (AuditUnavailableException)
        {
            _authorizationPersistenceFailed = true;
            return null;
        }
    }

    internal bool RecordJobStarted(long jobId, string response)
    {
        try
        {
            Append("job.started", outcomeState: "started", jobId: jobId);
            if (_parentEventId is Guid parent) _jobTerminalLease?.SetParent(parent);
            CompleteCall("completed", response);
            return true;
        }
        catch (AuditUnavailableException)
        {
            _reservation?.Release();
            return false;
        }
    }

    internal void RecordJobStartFailed(long jobId, string detailCode, string response)
    {
        TryAppend("job.start_failed", "failed", detailCode, jobId);
        CompleteCall("failed", response);
    }

    internal void RecordJobNotStarted(string detailCode, string response, long? jobId = null)
    {
        if (_authorizationPersistenceFailed)
        {
            CompleteCall("not_started", response);
            return;
        }

        try
        {
            if (!_validationCompleted)
            {
                if (!_validationStarted && !BeginValidation())
                {
                    CompleteCall("not_started", response);
                    return;
                }
                Append("execution.validation_completed", "not_started", detailCode, jobId);
                _validationCompleted = true;
            }
            Append("job.not_started", "not_started", detailCode, jobId);
        }
        catch (AuditUnavailableException)
        {
            _authorizationPersistenceFailed = true;
        }
        CompleteCall("not_started", response);
    }

    internal bool AuthorizeControl(string eventType, long? jobId = null)
    {
        EnsureActive();
        try
        {
            Append(eventType, outcomeState: "requested", jobId: jobId);
            _effectAuthorized = true;
            return true;
        }
        catch (AuditUnavailableException)
        {
            _authorizationPersistenceFailed = true;
            return false;
        }
    }

    internal void RecordControlOutcome(
        string eventType,
        string state,
        string? detailCode = null,
        long? jobId = null,
        bool? warmStateLost = null,
        string terminationCertainty = "confirmed")
    {
        EnsureActive();
        TryAppend(
            eventType,
            state,
            detailCode,
            jobId,
            warmStateLost: warmStateLost,
            terminationCertainty: terminationCertainty,
            rootCoverage: "not_applicable");
    }

    /// <summary>
    /// Persists a read/access fact before the caller may release the result.
    /// Unlike post-effect terminal reporting, persistence failure is propagated.
    /// </summary>
    internal void CommitReadOutcome(
        string eventType,
        string state,
        string response,
        string? detailCode = null,
        long? jobId = null,
        long? nextOffset = null,
        long? bytesReturnedOverride = null)
    {
        EnsureActive();
        Append(
            eventType,
            state,
            detailCode,
            jobId,
            bytesReturned: bytesReturnedOverride ?? Utf8.GetByteCount(response ?? string.Empty),
            nextOffset: nextOffset,
            terminationCertainty: "not_applicable",
            rootCoverage: "not_applicable");
    }

    internal string HealthStatusLine()
    {
        var snapshot = _journal.Health.Snapshot();
        var protection = snapshot.ProtectionMode == AuditProtectionMode.LocalOnly
            ? "local-only"
            : "anchored";
        var state = snapshot.State.ToString().ToLowerInvariant();
        var failure = snapshot.FailureClass is null ? string.Empty : $", failure {snapshot.FailureClass}";
        var since = snapshot.DegradedSinceUtc is null
            ? string.Empty
            : $", since {snapshot.DegradedSinceUtc.Value:O}";
        var export = snapshot.ExportConfigurationIdentity is null
            ? "none"
            : snapshot.ExportConfigurationIdentity;
        return $"audit: {state}, protection {protection}, export configuration {export}{failure}{since}\n" +
            $"audit storage: spool {snapshot.SpoolBytes}/{snapshot.SpoolCapacityBytes} bytes, " +
            $"reserved {snapshot.ReservedBytes} bytes, effective free {snapshot.EffectiveFreeBytes} bytes, " +
            $"emergency reserved {snapshot.EmergencyReserveBytes}/{snapshot.EmergencyReserveCapacityBytes} bytes\n" +
            AuditExporterHealthText.FormatNormal(snapshot.Exporter);
    }

    internal void RecordInvokeResult(InvokeResult result, string response)
    {
        ArgumentNullException.ThrowIfNull(result);
        EnsureActive();

        if (!_effectAuthorized && !result.UserExecutionStarted && _planId is not null)
        {
            TryAppend(
                _validationCompleted
                    ? "execution.not_started"
                    : "execution.validation_completed",
                "not_started",
                result.Disposition == InvokeDisposition.Canceled
                    ? "canceled"
                    : _validationCompleted ? "dispatch_not_authorized" : "preflight_refused",
                terminationCertainty: "not_applicable",
                rootCoverage: "none");
        }
        else if (_effectAuthorized || result.UserExecutionStarted)
        {
            var eventType = result.Disposition switch
            {
                InvokeDisposition.Completed => "execution.completed",
                InvokeDisposition.Failed => "execution.failed",
                InvokeDisposition.Canceled => "execution.canceled",
                InvokeDisposition.OutcomeUnknown when result.TimedOut => "execution.timed_out",
                InvokeDisposition.OutcomeUnknown => "execution.outcome_unknown",
                _ => "execution.not_started",
            };
            TryAppend(
                eventType,
                Machine(result.Disposition),
                result.Recovering ? "runspace_recovering" : null,
                exitCode: result.ExitCode,
                warmStateLost: result.WarmStateLost,
                terminationCertainty: result.Disposition == InvokeDisposition.OutcomeUnknown
                    ? "unknown"
                    : result.UserExecutionStarted ? "confirmed" : "not_applicable",
                rootCoverage: result.UserExecutionStarted
                    ? result.Disposition == InvokeDisposition.OutcomeUnknown ? "unknown" : "complete"
                    : "none");
        }

        if (result.WarmStateLost)
        {
            TryAppend(
                "runspace.recycled",
                "completed",
                warmStateLost: true,
                terminationCertainty: "confirmed");
        }

        CompleteCall(
            result.Disposition == InvokeDisposition.NotStarted
                ? "not_started"
                : result.Success ? "completed" : "failed",
            response,
            result.Disposition == InvokeDisposition.OutcomeUnknown ? "unknown" : "not_applicable");
    }

    internal void CompleteCall(
        string state,
        string response,
        string terminationCertainty = "not_applicable",
        long? bytesReturnedOverride = null)
    {
        if (!_accepted || _terminalWritten) return;
        var eventType = state switch
        {
            "completed" => "call.completed",
            "not_started" => "call.not_started",
            _ => "call.failed",
        };
        try
        {
            Append(
                eventType,
                state,
                bytesReturned: bytesReturnedOverride ?? Utf8.GetByteCount(response ?? string.Empty),
                terminationCertainty: terminationCertainty,
                rootCoverage: "not_applicable");
            _terminalWritten = true;
        }
        catch (AuditUnavailableException)
        {
            // The journal already poisoned itself. Preserve the actual tool
            // result while admission fails closed for every later call.
        }
        finally
        {
            _reservation?.Release();
        }
    }

    internal void CompleteFromFilter(string state, long bytesReturned)
    {
        CompleteCall(state, string.Empty, bytesReturnedOverride: bytesReturned);
    }

    internal void Abandon()
    {
        _reservation?.Release();
    }

    private SerializedAuditEvent Append(
        string eventType,
        string? outcomeState = null,
        string? detailCode = null,
        long? jobId = null,
        long? exitCode = null,
        long? bytesReturned = null,
        long? nextOffset = null,
        bool? warmStateLost = null,
        string terminationCertainty = "not_applicable",
        string rootCoverage = "not_applicable")
    {
        EnsureActiveReservation();
        var correlation = new AuditCorrelation
        {
            CallId = _callId,
            JobId = jobId ?? _request!.JobId,
            ParentEventId = _parentEventId,
            PlanId = _planId,
        };
        SerializedAuditEvent serialized;
        try
        {
            serialized = _journal.Append(
                _reservation!,
                BuildInput(
                    eventType,
                    Metadata.Actor,
                    correlation,
                    _request!,
                    _routing,
                    outcomeState,
                    detailCode,
                    exitCode,
                    bytesReturned,
                    nextOffset,
                    warmStateLost,
                    terminationCertainty,
                    rootCoverage));
        }
        catch (AuditEventValidationException)
        {
            _journal.Health.MarkUnavailable("journal.schema");
            throw new AuditUnavailableException();
        }
        _parentEventId = serialized.EventId;
        return serialized;
    }

    private void TryAppend(
        string eventType,
        string? outcomeState = null,
        string? detailCode = null,
        long? jobId = null,
        long? exitCode = null,
        long? bytesReturned = null,
        long? nextOffset = null,
        bool? warmStateLost = null,
        string terminationCertainty = "not_applicable",
        string rootCoverage = "not_applicable")
    {
        try
        {
            Append(
                eventType,
                outcomeState,
                detailCode,
                jobId,
                exitCode,
                bytesReturned,
                nextOffset,
                warmStateLost,
                terminationCertainty,
                rootCoverage);
        }
        catch (AuditUnavailableException)
        {
        }
    }

    private AuditEventInput BuildInput(
        string eventType,
        AuditActor actor,
        AuditCorrelation correlation,
        AuditRequest request,
        AuditRouting routing,
        string? outcomeState,
        string? detailCode,
        long? exitCode,
        long? bytesReturned,
        long? nextOffset,
        bool? warmStateLost,
        string terminationCertainty,
        string rootCoverage)
    {
        return new AuditEventInput
        {
            EventType = eventType,
            Session = CallSession(),
            Actor = actor,
            Correlation = correlation,
            Request = request,
            Routing = routing,
            Outcome = new AuditOutcome
            {
                State = outcomeState,
                DetailCode = detailCode,
                ExitCode = exitCode,
                DurationMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - _startedUtc).TotalMilliseconds),
                QueueMs = null,
                BytesReturned = bytesReturned,
                NextOffset = nextOffset,
                WarmStateLost = warmStateLost,
                WorkerReplaced = warmStateLost,
                TerminationCertainty = terminationCertainty,
            },
            Coverage = new AuditCoverage
            {
                PtkRequest = true,
                RootProcessObserved = rootCoverage,
                DescendantsObserved = rootCoverage switch
                {
                    "none" => "none",
                    "not_applicable" => "not_applicable",
                    _ => "unknown",
                },
                RemoteEffectObserved = rootCoverage switch
                {
                    "none" => "none",
                    "not_applicable" => "not_applicable",
                    _ => "unknown",
                },
            },
            Audit = HealthyEvent(_journal.Health.Snapshot()),
        };
    }

    private static AuditSession CallSession() => new()
    {
        Name = "default",
        Generation = 0,
        BindingKind = "default",
        AllowColdBackground = true,
    };

    private static AuditEventHealth HealthyEvent(AuditHealthSnapshot snapshot)
    {
        var unhealthy = snapshot.State is AuditHealthState.Degraded or AuditHealthState.Unavailable;
        return new AuditEventHealth
        {
            ProtectionMode = snapshot.ProtectionMode == AuditProtectionMode.LocalOnly
                ? "local-only"
                : "anchored",
            ExportConfigurationIdentity = snapshot.ExportConfigurationIdentity,
            HealthState = unhealthy ? "degraded" : "healthy",
            FailureClass = unhealthy ? snapshot.FailureClass : null,
            DegradedSinceUtc = unhealthy ? snapshot.DegradedSinceUtc : null,
        };
    }

    private void EnsureActive()
    {
        if (!_accepted)
            throw new InvalidOperationException("Audit call has not been accepted.");
        if (_terminalWritten)
            throw new InvalidOperationException("Audit call is already terminal.");
    }

    private void EnsureActiveReservation()
    {
        if (_reservation is null)
            throw new InvalidOperationException("Audit reservation is unavailable.");
    }

    private static string Machine(InvokeDisposition disposition) => disposition switch
    {
        InvokeDisposition.NotStarted => "not_started",
        InvokeDisposition.Completed => "completed",
        InvokeDisposition.Failed => "failed",
        InvokeDisposition.Canceled => "canceled",
        _ => "outcome_unknown",
    };

}

/// <summary>
/// Request-scoped tool-layer holder for the admitted audit capability. It is
/// deliberately an ordinary DI object, not static/AsyncLocal state: a
/// FullLanguage runspace can reflect the hosting assembly but has no reference
/// to the request service scope that owns this instance.
/// </summary>
public sealed class AuditCallContextAccessor
{
    internal AuditCallContext? Current { get; set; }
}

/// <summary>Persistent one-record obligation detached from a completed start call.</summary>
internal sealed class AuditJobTerminalLease
{
    private readonly AuditJournal _journal;
    private readonly AuditReservation _reservation;
    private readonly AuditActor _actor;
    private readonly AuditRequest _request;
    private readonly AuditRouting _routing;
    private readonly Guid _callId;
    private Guid? _parentEventId;
    private readonly Guid? _planId;
    private readonly long _jobId;
    private int _completed;

    internal AuditJobTerminalLease(
        AuditJournal journal,
        AuditReservation reservation,
        AuditActor actor,
        AuditRequest request,
        AuditRouting routing,
        Guid callId,
        Guid? parentEventId,
        Guid? planId,
        long jobId)
    {
        _journal = journal;
        _reservation = reservation;
        _actor = actor;
        _request = request;
        _routing = routing;
        _callId = callId;
        _parentEventId = parentEventId;
        _planId = planId;
        _jobId = jobId;
    }

    internal Task CompleteAsync(JobSnapshot snapshot)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return Task.CompletedTask;
        try
        {
            var eventType = snapshot.KillRequested ? "job.killed" : "job.completed";
            var detailCode = snapshot.TerminationReason switch
            {
                JobTerminationReason.ExplicitKill => "explicit_kill",
                JobTerminationReason.Reset => "reset",
                JobTerminationReason.Shutdown => "shutdown",
                _ => null,
            };
            _journal.Append(
                _reservation,
                new AuditEventInput
                {
                    EventType = eventType,
                    Session = new AuditSession
                    {
                        Name = "default",
                        Generation = 0,
                        BindingKind = "default",
                        AllowColdBackground = true,
                    },
                    Actor = _actor,
                    Correlation = new AuditCorrelation
                    {
                        CallId = _callId,
                        JobId = _jobId,
                        ParentEventId = _parentEventId,
                        PlanId = _planId,
                    },
                    Request = _request,
                    Routing = _routing,
                    Outcome = new AuditOutcome
                    {
                        State = snapshot.KillRequested ? "killed" : "completed",
                        DetailCode = detailCode,
                        ExitCode = snapshot.ExitCode,
                        TerminationCertainty = "confirmed",
                    },
                    Coverage = new AuditCoverage
                    {
                        PtkRequest = true,
                        RootProcessObserved = "complete",
                        DescendantsObserved = "unknown",
                        RemoteEffectObserved = "unknown",
                    },
                    Audit = EventHealth(),
                });
        }
        catch (AuditUnavailableException)
        {
        }
        finally
        {
            _reservation.Release();
        }
        return Task.CompletedTask;
    }

    internal void ReleaseWithoutTerminal()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            _reservation.Release();
    }

    internal void SetParent(Guid parentEventId) => _parentEventId = parentEventId;

    private AuditEventHealth EventHealth()
    {
        var snapshot = _journal.Health.Snapshot();
        var unhealthy = snapshot.State is AuditHealthState.Degraded or AuditHealthState.Unavailable;
        return new AuditEventHealth
        {
            ProtectionMode = snapshot.ProtectionMode == AuditProtectionMode.LocalOnly
                ? "local-only"
                : "anchored",
            ExportConfigurationIdentity = snapshot.ExportConfigurationIdentity,
            HealthState = unhealthy ? "degraded" : "healthy",
            FailureClass = unhealthy ? snapshot.FailureClass : null,
            DegradedSinceUtc = unhealthy ? snapshot.DegradedSinceUtc : null,
        };
    }
}
