using System.Runtime.ExceptionServices;

namespace PtkMcpServer.Audit;

internal sealed class AuditAdminOperationException(Exception innerException)
    : IOException("The out-of-band audit administration operation failed.", innerException);

/// <summary>
/// Owns the short-lived audit writer used only by the separate administration
/// executable. It does not start an MCP host or exporter. Anchored records are
/// left as a closed boot for the normal exporter coordinator to adopt.
/// </summary>
internal sealed class AuditAdminJournalSession : IDisposable
{
    private readonly AuditExportCheckpointStore? _checkpointStore;
    private int _disposed;

    private AuditAdminJournalSession(
        AuditJournal journal,
        AuditExportCheckpointStore? checkpointStore)
    {
        Journal = journal;
        _checkpointStore = checkpointStore;
    }

    internal AuditJournal Journal { get; }

    internal static AuditAdminJournalSession Open(
        AuditOptions options,
        string producerVersion)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);
        var health = new AuditHealth(options);
        if (options.ProtectionMode == AuditProtectionMode.LocalOnly)
        {
            return new AuditAdminJournalSession(
                AuditJournalFactory.OpenReconciledLocal(
                    options,
                    health,
                    producerVersion,
                    new ScriptEvidenceStoreProvider(options)),
                checkpointStore: null);
        }

        AuditAnchoredWriterPreparation? preparation = null;
        AuditExportCheckpointStore? checkpointStore = null;
        AuditJournal? journal = null;
        try
        {
            preparation = FileAuditJournalSink.PrepareAnchored(options, Guid.NewGuid());
            checkpointStore = preparation.CreateCheckpointStore();
            var sink = preparation.Activate(checkpointStore);
            journal = AuditJournalFactory.OpenActivatedAnchored(
                options,
                health,
                producerVersion,
                sink);
            var result = new AuditAdminJournalSession(journal, checkpointStore);
            journal = null;
            checkpointStore = null;
            return result;
        }
        finally
        {
            preparation?.Dispose();
            journal?.Dispose();
            checkpointStore?.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Exception? failure = null;
        try
        {
            Journal.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure = exception;
        }
        try
        {
            _checkpointStore?.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}

internal sealed class AuditAdminOperations
{
    private readonly AuditOptions _options;
    private readonly AuditJournal _journal;
    private readonly Func<ScriptEvidenceStore> _evidenceFactory;
    private readonly Action? _beforeEvidenceExportPublishForTests;

    internal AuditAdminOperations(
        AuditOptions options,
        AuditJournal journal,
        Func<ScriptEvidenceStore>? evidenceFactory = null,
        Action? beforeEvidenceExportPublishForTests = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        if (!ReferenceEquals(options, journal.Options) &&
            (!PathsEqual(options.RootDirectory, journal.Options.RootDirectory) ||
             options.ProtectionMode != journal.Options.ProtectionMode ||
             !string.Equals(
                 options.ExportConfigurationIdentity,
                 journal.Options.ExportConfigurationIdentity,
                 StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The administration journal does not match the frozen audit configuration.",
                nameof(journal));
        }
        _evidenceFactory = evidenceFactory ?? (() => new ScriptEvidenceStore(options));
        _beforeEvidenceExportPublishForTests = beforeEvidenceExportPublishForTests;
    }

    internal ScriptEvidenceReference ReadEvidence(string evidenceId, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return AccessEvidence("read", evidenceId, destination, outputPath: null);
    }

    internal ScriptEvidenceReference ExportEvidence(
        string evidenceId,
        string absoluteOutputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteOutputPath);
        return AccessEvidence("export", evidenceId, destination: null, absoluteOutputPath);
    }

    private ScriptEvidenceReference AccessEvidence(
        string action,
        string evidenceId,
        Stream? destination,
        string? outputPath)
    {
        var parsedEvidenceId = TryParseEvidenceId(evidenceId);
        EvidenceExportPublication? exportPublication = null;
        using var reservation = ReserveOperation();
        var intent = AppendEvent(
            reservation,
            $"evidence.{action}_intent",
            action,
            "accepted",
            "intent.committed",
            parsedEvidenceId,
            scriptDigest: null,
            bytesReturned: null,
            parentEventId: null,
            declaredTarget: null);

        try
        {
            if (parsedEvidenceId is null)
                throw new ArgumentException("A canonical UUIDv4 evidence ID is required.", nameof(evidenceId));
            if (outputPath is not null && !Path.IsPathFullyQualified(outputPath))
            {
                throw new ArgumentException(
                    "Evidence export output must be an absolute path.",
                    nameof(outputPath));
            }
            // Construction inventories and hashes evidence, so it deliberately
            // occurs only after the access intent above is durable.
            var store = _evidenceFactory();
            ScriptEvidenceReference reference;
            if (destination is not null)
            {
                reference = store.ReadExact(evidenceId, bytes =>
                {
                    destination.Write(bytes.Span);
                    destination.Flush();
                });
            }
            else
            {
                exportPublication = PrepareProtectedExport(
                    store,
                    evidenceId,
                    outputPath!);
                reference = exportPublication.Reference;
            }

            AppendEvent(
                reservation,
                $"evidence.{action}_completed",
                action,
                "completed",
                "operation.completed",
                parsedEvidenceId,
                reference.ScriptDigest,
                reference.ByteLength,
                intent.EventId,
                declaredTarget: null);
            exportPublication?.CompleteAfterAuditOutcome();
            return reference;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            ThrowAfterFailureEvent(
                exception,
                $"evidence.{action}_failed",
                action,
                parsedEvidenceId,
                intent.EventId,
                declaredTarget: null,
                reservation);
            throw;
        }
        finally
        {
            exportPublication?.Dispose();
        }
    }

    private EvidenceExportPublication PrepareProtectedExport(
        ScriptEvidenceStore store,
        string evidenceId,
        string outputPath)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new IOException("The evidence export parent is unavailable.");
        SecureAuditStorage.VerifyExternalProtectedDirectory(parent);

        var temporaryPath = Path.Combine(
            parent,
            $".ptk-evidence-export-{Guid.NewGuid():N}.tmp");
        var output = SecureAuditStorage.CreateExclusiveFile(
            temporaryPath,
            share: FileShare.Delete,
            access: FileAccess.ReadWrite);
        try
        {
            var reference = store.ReadExact(evidenceId, bytes =>
            {
                output.Write(bytes.Span);
                output.Flush(flushToDisk: true);
            });
            _beforeEvidenceExportPublishForTests?.Invoke();
            SecureAuditStorage.PublishAtomically(
                temporaryPath,
                fullPath,
                parent);
            SecureAuditStorage.ConfirmRetainedCreatedFileDurability(
                parent,
                fullPath,
                output.SafeFileHandle);
            return new EvidenceExportPublication(
                parent,
                fullPath,
                output,
                reference);
        }
        catch
        {
            TryDeleteRetainedExport(parent, fullPath, output);
            TryDeleteRetainedExport(parent, temporaryPath, output);
            output.Dispose();
            throw;
        }
    }

    private static void TryDeleteRetainedExport(
        string parent,
        string path,
        FileStream output)
    {
        try
        {
            SecureAuditStorage.DeleteRetainedProtectedFile(
                parent,
                path,
                output.SafeFileHandle);
        }
        catch
        {
            // The sanitized operation failure remains authoritative. A path
            // replacement is never deleted without the retained identity.
        }
    }

    private sealed class EvidenceExportPublication(
        string parent,
        string path,
        FileStream output,
        ScriptEvidenceReference reference) : IDisposable
    {
        private FileStream? _output = output;
        private bool _outcomeCommitted;

        internal ScriptEvidenceReference Reference { get; } = reference;

        internal void CompleteAfterAuditOutcome()
        {
            _outcomeCommitted = true;
            var stream = Interlocked.Exchange(ref _output, null);
            if (stream is null) return;
            try { stream.Dispose(); }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // The bytes and the completed outcome are already durable.
                // A close failure cannot be truthfully rewritten as a failed
                // export, and the process teardown will release the handle.
            }
        }

        public void Dispose()
        {
            var stream = Interlocked.Exchange(ref _output, null);
            if (stream is null) return;
            try
            {
                if (!_outcomeCommitted)
                {
                    SecureAuditStorage.DeleteRetainedProtectedFile(
                        parent,
                        path,
                        stream.SafeFileHandle);
                }
            }
            finally
            {
                stream.Dispose();
            }
        }
    }

    internal Guid ApplyPermanentBlockDisposition(
        Guid supervisorBootId,
        Guid blockedEventId,
        AuditOperatorDispositionProof proof,
        Action? afterDurableIntentForTests = null,
        Action? afterCheckpointAdvanceForTests = null,
        Action? afterCompletedAuditAppendForTests = null,
        Action? afterOutcomePublishedForTests = null)
    {
        ArgumentNullException.ThrowIfNull(proof);
        AuditSpoolSegmentIdentity.RequireUuidV4(supervisorBootId, nameof(supervisorBootId));
        if (!IsUuidV7(blockedEventId))
            throw new ArgumentException("A canonical UUIDv7 blocked event ID is required.", nameof(blockedEventId));
        if (_options.ProtectionMode != AuditProtectionMode.Anchored)
            throw new InvalidOperationException("Export-block disposition requires anchored audit mode.");

        // A completion-receipt failure happens after the completed event is
        // durable and must still have one reserved slot for its failure fact.
        using var reservation = ReserveOperation(maxRecordSlots: 3);
        AuditSpoolQuotaLease? quota = null;
        AuditExportCheckpointStore? checkpointStore = null;
        AuditClosedSpoolChainReader? reader = null;
        var auditIntent = AppendDispositionEvent(
            reservation,
            "export.disposition_intent",
            "accepted",
            ProofDetail(proof),
            supervisorBootId,
            blockedEventId,
            parentEventId: blockedEventId);
        try
        {
            var durableIntent = AuditOperatorDispositionIntent.OpenExisting(
                _options,
                supervisorBootId,
                blockedEventId,
                proof);

            if (durableIntent is not null &&
                AuditOperatorDispositionOutcome.TryOpenCommitted(
                    _options,
                    durableIntent) is not null)
            {
                afterDurableIntentForTests?.Invoke();
                AppendDispositionEvent(
                    reservation,
                    "export.disposition_completed",
                    "completed",
                    "disposition.previously_completed",
                    supervisorBootId,
                    blockedEventId,
                    auditIntent.EventId);
                return durableIntent.DispositionId;
            }

            quota = AuditSpoolQuotaLease.AcquireExisting(_options.SpoolDirectory);
            if (!AuditExportCheckpointStore.TryAcquireExisting(
                    _options,
                    supervisorBootId,
                    out checkpointStore) || checkpointStore is null)
            {
                throw new IOException("The target audit export checkpoint is live.");
            }

            if (durableIntent is not null && durableIntent.IsAlreadyApplied(checkpointStore.Current))
            {
                afterDurableIntentForTests?.Invoke();
                var completed = AppendDispositionEvent(
                    reservation,
                    "export.disposition_completed",
                    "completed",
                    "disposition.already_applied",
                    supervisorBootId,
                    blockedEventId,
                    auditIntent.EventId);
                afterCompletedAuditAppendForTests?.Invoke();
                _ = AuditOperatorDispositionOutcome.Commit(
                    _options,
                    durableIntent,
                    _journal.SupervisorBootId,
                    completed,
                    afterOutcomePublishedForTests);
                return durableIntent.DispositionId;
            }

            reader = new AuditClosedSpoolChainReader(_options, checkpointStore);
            var recovery = reader.ResolveCheckpointForAdoption(quota);
            quota = null;
            if (recovery is not AuditClosedSpoolRecovery.Record record ||
                record.BlockedRecord is not { } blocked ||
                blocked.EventId != blockedEventId)
            {
                throw new IOException("The target audit export block is absent or ambiguous.");
            }

            durableIntent ??= AuditOperatorDispositionIntent.CreateOrOpen(
                _options,
                record.Position,
                blocked,
                proof);
            afterDurableIntentForTests?.Invoke();
            reader.ApplyPermanentBlockDisposition(record.Position, durableIntent);
            afterCheckpointAdvanceForTests?.Invoke();
            var applied = AppendDispositionEvent(
                reservation,
                "export.disposition_completed",
                "completed",
                "disposition.applied",
                supervisorBootId,
                blockedEventId,
                auditIntent.EventId);
            afterCompletedAuditAppendForTests?.Invoke();
            _ = AuditOperatorDispositionOutcome.Commit(
                _options,
                durableIntent,
                _journal.SupervisorBootId,
                applied,
                afterOutcomePublishedForTests);
            return durableIntent.DispositionId;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            ThrowAfterFailureEvent(
                exception,
                "export.disposition_failed",
                "disposition",
                evidenceId: null,
                auditIntent.EventId,
                supervisorBootId.ToString("D"),
                reservation);
            throw;
        }
        finally
        {
            reader?.Dispose();
            checkpointStore?.Dispose();
            quota?.Dispose();
        }
    }

    private void ThrowAfterFailureEvent(
        Exception operationFailure,
        string eventType,
        string action,
        Guid? evidenceId,
        Guid parentEventId,
        string? declaredTarget,
        AuditReservation reservation)
    {
        try
        {
            AppendEvent(
                reservation,
                eventType,
                action,
                "failed",
                "operation.failed",
                evidenceId,
                scriptDigest: null,
                bytesReturned: null,
                parentEventId,
                declaredTarget);
        }
        catch (Exception auditFailure) when (!IsFatal(auditFailure))
        {
            throw new AuditAdminOperationException(
                new AggregateException(operationFailure, auditFailure));
        }
        throw new AuditAdminOperationException(operationFailure);
    }

    private SerializedAuditEvent AppendDispositionEvent(
        AuditReservation reservation,
        string eventType,
        string state,
        string detailCode,
        Guid bootId,
        Guid eventId,
        Guid parentEventId) =>
        AppendEvent(
            reservation,
            eventType,
            "disposition",
            state,
            detailCode,
            evidenceId: null,
            scriptDigest: null,
            bytesReturned: null,
            parentEventId,
            bootId.ToString("D"));

    private SerializedAuditEvent AppendEvent(
        AuditReservation reservation,
        string eventType,
        string action,
        string state,
        string detailCode,
        Guid? evidenceId,
        string? scriptDigest,
        long? bytesReturned,
        Guid? parentEventId,
        string? declaredTarget)
    {
        var health = _journal.Health.Snapshot();
        var unhealthy = health.State is AuditHealthState.Degraded or AuditHealthState.Unavailable;
        return _journal.Append(reservation, new AuditEventInput
        {
            EventType = eventType,
            Session = new AuditSession
            {
                DeclaredPurpose = "audit_administration",
                DeclaredTarget = declaredTarget,
            },
            Actor = new AuditActor { AttributionStrength = "unknown" },
            Correlation = new AuditCorrelation { ParentEventId = parentEventId },
            Request = new AuditRequest
            {
                Tool = "audit_admin",
                Action = action,
                ScriptEvidenceId = evidenceId,
                OriginalScriptDigest = scriptDigest,
            },
            Routing = new AuditRouting(),
            Outcome = new AuditOutcome
            {
                State = state,
                DetailCode = detailCode,
                BytesReturned = bytesReturned,
                TerminationCertainty = "not_applicable",
            },
            Coverage = new AuditCoverage
            {
                PtkRequest = false,
                RootProcessObserved = "not_applicable",
                DescendantsObserved = "not_applicable",
                RemoteEffectObserved = "not_applicable",
            },
            Audit = new AuditEventHealth
            {
                ProtectionMode = _options.ProtectionMode == AuditProtectionMode.Anchored
                    ? "anchored"
                    : "local-only",
                ExportConfigurationIdentity = _options.ExportConfigurationIdentity,
                HealthState = unhealthy ? "degraded" : "healthy",
                FailureClass = unhealthy ? health.FailureClass : null,
                DegradedSinceUtc = unhealthy ? health.DegradedSinceUtc : null,
            },
        });
    }

    private AuditReservation ReserveOperation(int maxRecordSlots = 2)
    {
        if (!_journal.TryReserve(
                maxRecordSlots,
                out var reservation,
                out var failureClass) || reservation is null)
        {
            throw new AuditUnavailableException();
        }
        return reservation;
    }

    private static Guid? TryParseEvidenceId(string evidenceId)
    {
        if (string.IsNullOrEmpty(evidenceId) ||
            !Guid.TryParseExact(evidenceId, "D", out var parsed) ||
            !string.Equals(evidenceId, parsed.ToString("D"), StringComparison.Ordinal) ||
            !AuditSpoolSegmentIdentity.IsUuidV4(parsed))
        {
            return null;
        }
        return parsed;
    }

    private static bool IsUuidV7(Guid value)
    {
        var text = value.ToString("D");
        return text[14] == '7' && text[19] is '8' or '9' or 'a' or 'b';
    }

    private static string ProofDetail(AuditOperatorDispositionProof proof) => proof.Kind switch
    {
        AuditOperatorDispositionProofKind.VerifiedReceipt => "proof.verified_receipt",
        AuditOperatorDispositionProofKind.AcknowledgedGap => "proof.acknowledged_gap",
        _ => throw new ArgumentOutOfRangeException(nameof(proof)),
    };

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
