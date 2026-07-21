using System.Text;

namespace PtkMcpServer.Audit;

/// <summary>
/// Guardian-safe admission and terminal core for one public MCP call. It owns
/// the complete audit reservation, exact-script evidence publication, call
/// identity, event chain, and terminal obligation without depending on any
/// server execution or PowerShell type. Specialized call contexts may add
/// typed pre-effect authorization above this lifecycle.
/// </summary>
internal class AuditCallLifecycle : IAuditBoundaryCall
{
    private static readonly UTF8Encoding Utf8 = new(false);

    internal const string NotStartedMessage =
        "[operation not started] Required audit persistence is unavailable; the original operation was not started.";

    protected readonly AuditJournal _journal;
    protected readonly ScriptEvidenceStoreProvider _evidence;
    protected AuditReservation? _reservation;
    protected AuditCallMetadata? _metadata;
    protected AuditRequest? _request;
    protected AuditRouting _routing = new();
    protected Guid _callId;
    protected Guid? _parentEventId;
    protected Guid? _planId;
    protected bool _effectAuthorized;
    protected bool _authorizationPersistenceFailed;
    protected bool _userExecutionStarted;
    protected bool _terminalWritten;
    protected DateTimeOffset _startedUtc;

    internal AuditCallLifecycle(AuditJournal journal, ScriptEvidenceStore evidence)
        : this(journal, new ScriptEvidenceStoreProvider(evidence))
    {
    }

    internal AuditCallLifecycle(
        AuditJournal journal,
        ScriptEvidenceStoreProvider evidence)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    internal bool Accepted { get; private set; }

    internal bool EffectAuthorized => _effectAuthorized;

    internal bool AuthorizationPersistenceFailed => _authorizationPersistenceFailed;

    internal bool UserExecutionStarted => _userExecutionStarted;

    internal bool TerminalWritten => _terminalWritten;

    bool IAuditBoundaryCall.AuthorizationPersistenceFailed => AuthorizationPersistenceFailed;

    bool IAuditBoundaryCall.UserExecutionStarted => UserExecutionStarted;

    bool IAuditBoundaryCall.TerminalWritten => TerminalWritten;

    internal AuditCallMetadata Metadata =>
        _metadata ?? throw new InvalidOperationException("Audit call has not been initialized.");

    internal bool TryBegin(
        AuditCallMetadata metadata,
        string? exactSubmittedScript,
        out string? failureClass)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (Accepted || _reservation is not null)
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
            Accepted = true;
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
                if (!Accepted && evidencePublication is not null)
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
                    if (!Accepted)
                    {
                        _reservation?.Release();
                        _reservation = null;
                    }
                }
            }
        }
    }

    internal void CompleteCall(
        string state,
        string response,
        string terminationCertainty = "not_applicable",
        long? bytesReturnedOverride = null)
    {
        if (!Accepted || _terminalWritten) return;
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

    void IAuditBoundaryCall.CompleteFromFilter(string state, long bytesReturned) =>
        CompleteFromFilter(state, bytesReturned);

    internal void Abandon()
    {
        _reservation?.Release();
    }

    protected SerializedAuditEvent Append(
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

    protected void TryAppend(
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

    protected void EnsureActive()
    {
        if (!Accepted)
            throw new InvalidOperationException("Audit call has not been accepted.");
        if (_terminalWritten)
            throw new InvalidOperationException("Audit call is already terminal.");
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

    private void EnsureActiveReservation()
    {
        if (_reservation is null)
            throw new InvalidOperationException("Audit reservation is unavailable.");
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;
}
