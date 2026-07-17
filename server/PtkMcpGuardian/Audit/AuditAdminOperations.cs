using System.Security.Cryptography;
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
        var evidence = new ScriptEvidenceStoreProvider(options);
        if (options.ProtectionMode == AuditProtectionMode.LocalOnly)
        {
            var localJournal = AuditJournalFactory.OpenReconciledLocal(
                options,
                health,
                producerVersion,
                evidence);
            try
            {
                RetainEvidence(localJournal, evidence);
                return new AuditAdminJournalSession(
                    localJournal,
                    checkpointStore: null);
            }
            catch
            {
                localJournal.Dispose();
                throw;
            }
        }

        AuditEvidenceOrphanReconciler.RequireCompleteBeforeWriter(
            options,
            health,
            evidence);

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
            RetainEvidence(journal, evidence);
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

    private static void RetainEvidence(
        AuditJournal journal,
        ScriptEvidenceStoreProvider evidence)
    {
        try
        {
            evidence.RetainEligible(journal);
        }
        catch (ScriptEvidenceStorageException)
        {
            journal.EnterExternalUnavailable("evidence.storage");
            throw;
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
    private readonly Action? _beforeEvidenceOutcomeAppendForTests;
    private readonly Action? _afterEvidenceExportPublishForTests;
    private readonly string _effectiveIdentity;

    internal AuditAdminOperations(
        AuditOptions options,
        AuditJournal journal,
        Func<ScriptEvidenceStore>? evidenceFactory = null,
        Action? beforeEvidenceExportPublishForTests = null,
        Action? beforeEvidenceOutcomeAppendForTests = null,
        Action? afterEvidenceExportPublishForTests = null)
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
        _beforeEvidenceOutcomeAppendForTests = beforeEvidenceOutcomeAppendForTests;
        _afterEvidenceExportPublishForTests = afterEvidenceExportPublishForTests;
        _effectiveIdentity = AuditEffectiveIdentity.Capture();
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
        return AccessEvidence("export", evidenceId, destination: null, absoluteOutputPath);
    }

    private ScriptEvidenceReference AccessEvidence(
        string action,
        string evidenceId,
        Stream? destination,
        string? outputPath)
    {
        var parsedEvidenceId = TryParseEvidenceId(evidenceId);
        var destinationKind = destination is not null ? "stdout" : "protected_file";
        var auditedDestinationPath = TryCanonicalDestinationPath(outputPath);
        EvidenceExportPublication? exportPublication = null;
        var exportProgress = destination is null ? new EvidenceExportProgress() : null;
        var readWriteStarted = false;
        var readWriteReturned = false;
        var readFlushReturned = false;
        long? readBytesReleased = null;
        string? readScriptDigest = null;
        ScriptEvidenceReference? effectReference = null;
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
            declaredTarget: null,
            destinationKind,
            auditedDestinationPath);

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
                    readScriptDigest = Convert.ToHexString(
                        SHA256.HashData(bytes.Span)).ToLowerInvariant();
                    readWriteStarted = true;
                    destination.Write(bytes.Span);
                    // Stream.Write either returns after accepting the full
                    // span or throws with an unknowable partial effect.
                    readWriteReturned = true;
                    readBytesReleased = bytes.Length;
                    destination.Flush();
                    readFlushReturned = true;
                });
            }
            else
            {
                exportPublication = PrepareProtectedExport(
                    store,
                    evidenceId,
                    outputPath!,
                    exportProgress!);
                reference = exportPublication.Reference;
            }

            effectReference = reference;
            _beforeEvidenceOutcomeAppendForTests?.Invoke();
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
                declaredTarget: null,
                destinationKind,
                auditedDestinationPath);
            exportPublication?.CompleteAfterAuditOutcome();
            return reference;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            var exportPublished =
                exportPublication is not null || exportProgress?.FinalPublished == true;
            var failureReference = effectReference ??
                (exportPublished ? exportProgress?.Reference : null);
            ThrowAfterFailureEvent(
                exception,
                $"evidence.{action}_failed",
                action,
                parsedEvidenceId,
                intent.EventId,
                declaredTarget: null,
                reservation,
                destinationKind,
                auditedDestinationPath,
                failureDetailCode: AuditAdminFailureDetailCode.From(
                    ClassifyEvidenceFailure(
                        exception,
                        parsedEvidenceId,
                        outputPath,
                        auditedDestinationPath,
                        destination is not null,
                        readWriteStarted,
                        readWriteReturned,
                        readFlushReturned,
                        exportPublished)),
                failureScriptDigest: failureReference?.ScriptDigest ?? readScriptDigest,
                failureBytesReturned: destination is not null
                    ? readWriteStarted
                        ? readBytesReleased
                        : 0
                    : exportPublished
                        ? failureReference?.ByteLength
                        : null);
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
        string outputPath,
        EvidenceExportProgress progress)
    {
        string fullPath;
        string parent;
        try
        {
            fullPath = Path.GetFullPath(outputPath);
            parent = Path.GetDirectoryName(fullPath)
                ?? throw new ArgumentException("The evidence export parent is unavailable.");
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new AuditEvidenceDestinationException(
                AuditEvidenceDestinationFailureKind.InvalidPath,
                exception);
        }
        try
        {
            SecureAuditStorage.VerifyExternalProtectedDirectory(parent);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new AuditEvidenceDestinationException(
                AuditEvidenceDestinationFailureKind.Refused,
                exception);
        }

        var temporaryPath = Path.Combine(
            parent,
            $".ptk-evidence-export-{Guid.NewGuid():N}.tmp");
        FileStream output;
        try
        {
            output = SecureAuditStorage.CreateExclusiveFile(
                temporaryPath,
                share: FileShare.Delete,
                access: FileAccess.ReadWrite);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new AuditEvidenceDestinationException(
                IsDestinationRefusal(exception)
                    ? AuditEvidenceDestinationFailureKind.Refused
                    : AuditEvidenceDestinationFailureKind.Storage,
                exception);
        }
        try
        {
            var reference = store.ReadExact(evidenceId, bytes =>
            {
                try
                {
                    output.Write(bytes.Span);
                    output.Flush(flushToDisk: true);
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    throw new AuditEvidenceDestinationException(
                        IsDestinationRefusal(exception)
                            ? AuditEvidenceDestinationFailureKind.Refused
                            : AuditEvidenceDestinationFailureKind.Storage,
                        exception);
                }
            });
            progress.Reference = reference;
            _beforeEvidenceExportPublishForTests?.Invoke();
            try
            {
                SecureAuditStorage.PublishAtomically(
                    temporaryPath,
                    fullPath,
                    parent);
            }
            catch (Exception exception) when (
                !IsFatal(exception) &&
                AuditJournalFactory.IsConcurrentPublishCollision(exception) &&
                DestinationEntryExists(fullPath))
            {
                throw new AuditEvidenceDestinationException(
                    AuditEvidenceDestinationFailureKind.Exists,
                    exception);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                throw new AuditEvidenceDestinationException(
                    IsDestinationRefusal(exception)
                        ? AuditEvidenceDestinationFailureKind.Refused
                        : AuditEvidenceDestinationFailureKind.Storage,
                    exception);
            }
            progress.FinalPublished = true;
            _afterEvidenceExportPublishForTests?.Invoke();
            try
            {
                SecureAuditStorage.ConfirmRetainedCreatedFileDurability(
                    parent,
                    fullPath,
                    output.SafeFileHandle);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                throw new AuditEvidenceDestinationException(
                    AuditEvidenceDestinationFailureKind.Storage,
                    exception);
            }
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

    private static bool IsDestinationRefusal(Exception exception) =>
        exception is UnauthorizedAccessException or System.Security.SecurityException;

    private sealed class EvidenceExportProgress
    {
        internal ScriptEvidenceReference? Reference { get; set; }
        internal bool FinalPublished { get; set; }
    }

    private static bool DestinationEntryExists(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) return true;
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return false;
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
        if (!AuditSpoolSegmentIdentity.IsUuidV4(supervisorBootId) ||
            !IsUuidV7(blockedEventId))
        {
            RejectInvalidDispositionTarget(supervisorBootId, blockedEventId);
        }

        // Intent, pre-effect authorization, completion, and a possible
        // post-completion failure each retain their own durable slot.
        using var reservation = ReserveOperation(maxRecordSlots: 4);
        AuditSpoolQuotaLease? quota = null;
        AuditExportCheckpointStore? checkpointStore = null;
        AuditClosedSpoolChainReader? reader = null;
        var stage = AuditDispositionStage.IntentControl;
        var checkpointAdvanced = false;
        var completedAuditAppended = false;
        var receiptCommitted = false;
        var dispositionFacts = RequestDispositionFacts(
            supervisorBootId,
            blockedEventId,
            proof);
        SerializedAuditEvent? authorized = null;
        var auditIntent = AppendDispositionEvent(
            reservation,
            "export.disposition_intent",
            "accepted",
            ProofDetail(proof),
            dispositionFacts,
            parentEventId: blockedEventId);
        var failureParentEventId = auditIntent.EventId;
        try
        {
            if (_options.ProtectionMode != AuditProtectionMode.Anchored)
                throw new AuditDispositionModeIneligibleException();

            var durableIntent = AuditOperatorDispositionIntent.OpenExisting(
                _options,
                supervisorBootId,
                blockedEventId,
                proof);

            if (durableIntent is not null)
            {
                dispositionFacts = AuthorizedDispositionFacts(durableIntent);
                authorized = AppendDispositionEvent(
                    reservation,
                    "export.disposition_authorized",
                    "authorized",
                    "disposition.authorized",
                    dispositionFacts,
                    auditIntent.EventId);
                failureParentEventId = authorized.Value.EventId;
                afterDurableIntentForTests?.Invoke();
            }

            stage = AuditDispositionStage.OutcomeControl;
            if (durableIntent is not null &&
                AuditOperatorDispositionOutcome.TryOpenCommitted(
                    _options,
                    durableIntent) is not null)
            {
                checkpointAdvanced = true;
                receiptCommitted = true;
                afterDurableIntentForTests?.Invoke();
                var replayCompleted = AppendDispositionEvent(
                    reservation,
                    "export.disposition_completed",
                    "completed",
                    "disposition.previously_completed",
                    dispositionFacts,
                    authorized!.Value.EventId);
                completedAuditAppended = true;
                failureParentEventId = replayCompleted.EventId;
                return durableIntent.DispositionId;
            }

            stage = AuditDispositionStage.BlockResolution;
            var targetCheckpointPath = Path.Combine(
                _options.RootDirectory,
                AuditExportCheckpointStore.CheckpointFileName(supervisorBootId));
            var targetCheckpointLockPath = Path.Combine(
                _options.RootDirectory,
                AuditExportCheckpointStore.LockFileName(supervisorBootId));
            if (!File.Exists(targetCheckpointPath) && !File.Exists(targetCheckpointLockPath))
                throw new AuditDispositionBlockAbsentException();

            stage = AuditDispositionStage.TargetLease;
            quota = AuditSpoolQuotaLease.AcquireExisting(_options.SpoolDirectory);
            if (!AuditExportCheckpointStore.TryAcquireExisting(
                    _options,
                    supervisorBootId,
                    out checkpointStore) || checkpointStore is null)
            {
                throw new AuditDispositionTargetLiveException();
            }

            if (durableIntent is not null && durableIntent.IsAlreadyApplied(checkpointStore.Current))
            {
                checkpointAdvanced = true;
                stage = AuditDispositionStage.CompletionAudit;
                var completed = AppendDispositionEvent(
                    reservation,
                    "export.disposition_completed",
                    "completed",
                    "disposition.already_applied",
                    dispositionFacts,
                    authorized!.Value.EventId);
                completedAuditAppended = true;
                failureParentEventId = completed.EventId;
                afterCompletedAuditAppendForTests?.Invoke();
                stage = AuditDispositionStage.ReceiptCommit;
                _ = AuditOperatorDispositionOutcome.Commit(
                    _options,
                    durableIntent,
                    _journal.SupervisorBootId,
                    completed,
                    () =>
                    {
                        receiptCommitted = true;
                        afterOutcomePublishedForTests?.Invoke();
                    });
                receiptCommitted = true;
                return durableIntent.DispositionId;
            }

            stage = AuditDispositionStage.BlockResolution;
            reader = new AuditClosedSpoolChainReader(_options, checkpointStore);
            var recovery = reader.ResolveCheckpointForAdoption(quota);
            quota = null;
            if (recovery is not AuditClosedSpoolRecovery.Record record ||
                record.BlockedRecord is not { } blocked ||
                blocked.EventId != blockedEventId)
            {
                throw new AuditDispositionBlockAbsentException();
            }
            if (blocked.FailureClass is not (
                    AuditExportFailureClass.PartialRejection or
                    AuditExportFailureClass.Data or
                    AuditExportFailureClass.Protocol))
            {
                throw new AuditDispositionBlockIneligibleException();
            }

            stage = AuditDispositionStage.IntentControl;
            durableIntent ??= AuditOperatorDispositionIntent.CreateOrOpen(
                _options,
                record.Position,
                blocked,
                proof);
            if (authorized is null)
            {
                dispositionFacts = AuthorizedDispositionFacts(durableIntent);
                authorized = AppendDispositionEvent(
                    reservation,
                    "export.disposition_authorized",
                    "authorized",
                    "disposition.authorized",
                    dispositionFacts,
                    auditIntent.EventId);
                failureParentEventId = authorized.Value.EventId;
                afterDurableIntentForTests?.Invoke();
            }
            stage = AuditDispositionStage.CheckpointAdvance;
            reader.ApplyPermanentBlockDisposition(record.Position, durableIntent);
            checkpointAdvanced = true;
            afterCheckpointAdvanceForTests?.Invoke();
            stage = AuditDispositionStage.CompletionAudit;
            var applied = AppendDispositionEvent(
                reservation,
                "export.disposition_completed",
                "completed",
                "disposition.applied",
                dispositionFacts,
                authorized.Value.EventId);
            completedAuditAppended = true;
            failureParentEventId = applied.EventId;
            afterCompletedAuditAppendForTests?.Invoke();
            stage = AuditDispositionStage.ReceiptCommit;
            _ = AuditOperatorDispositionOutcome.Commit(
                _options,
                durableIntent,
                _journal.SupervisorBootId,
                applied,
                () =>
                {
                    receiptCommitted = true;
                    afterOutcomePublishedForTests?.Invoke();
                });
            receiptCommitted = true;
            return durableIntent.DispositionId;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            ThrowAfterFailureEvent(
                exception,
                "export.disposition_failed",
                "disposition",
                evidenceId: null,
                failureParentEventId,
                supervisorBootId.ToString("D"),
                reservation,
                failureDetailCode: AuditAdminDispositionFailureDetailCode.From(
                    ClassifyDispositionFailure(
                        exception,
                        stage,
                        checkpointAdvanced,
                        completedAuditAppended,
                        receiptCommitted)),
                operatorDisposition: dispositionFacts);
            throw;
        }
        finally
        {
            reader?.Dispose();
            checkpointStore?.Dispose();
            quota?.Dispose();
        }
    }

    private void RejectInvalidDispositionTarget(Guid supervisorBootId, Guid blockedEventId)
    {
        using var reservation = ReserveOperation(maxRecordSlots: 1);
        _ = AppendEvent(
            reservation,
            "export.disposition_rejected",
            "disposition",
            "failed",
            AuditAdminDispositionFailureDetailCode.From(
                AuditAdminDispositionFailureKind.TargetIdInvalid),
            evidenceId: null,
            scriptDigest: null,
            bytesReturned: null,
            parentEventId: null,
            $"{supervisorBootId:D}/{blockedEventId:D}");
        throw new AuditAdminOperationException(
            new ArgumentException("The operator disposition target is invalid."));
    }

    private static AuditAdminDispositionFailureKind ClassifyDispositionFailure(
        Exception failure,
        AuditDispositionStage stage,
        bool checkpointAdvanced,
        bool completedAuditAppended,
        bool receiptCommitted)
    {
        if (completedAuditAppended && !receiptCommitted)
            return AuditAdminDispositionFailureKind.CheckpointAdvancedReceiptMissing;
        if (checkpointAdvanced)
            return AuditAdminDispositionFailureKind.AuditOutcomeFailedAfterCheckpoint;

        return failure switch
        {
            AuditDispositionModeIneligibleException =>
                AuditAdminDispositionFailureKind.ModeIneligible,
            AuditDispositionTargetLiveException =>
                AuditAdminDispositionFailureKind.TargetLive,
            AuditDispositionBlockAbsentException =>
                AuditAdminDispositionFailureKind.BlockAbsent,
            AuditDispositionBlockIneligibleException =>
                AuditAdminDispositionFailureKind.BlockIneligible,
            AuditOperatorDispositionIntentException
            {
                FailureKind: AuditOperatorDispositionIntentFailureKind.Conflict,
            } => AuditAdminDispositionFailureKind.ProofConflict,
            AuditOperatorDispositionIntentException
            {
                FailureKind: AuditOperatorDispositionIntentFailureKind.Invalid,
            } => AuditAdminDispositionFailureKind.IntentControlInvalid,
            AuditOperatorDispositionOutcomeException =>
                AuditAdminDispositionFailureKind.OutcomeControlInvalid,
            _ when stage == AuditDispositionStage.IntentControl =>
                AuditAdminDispositionFailureKind.IntentControlInvalid,
            _ when stage == AuditDispositionStage.OutcomeControl =>
                AuditAdminDispositionFailureKind.OutcomeControlInvalid,
            _ => AuditAdminDispositionFailureKind.TargetControlInvalid,
        };
    }

    private enum AuditDispositionStage
    {
        IntentControl,
        OutcomeControl,
        TargetLease,
        BlockResolution,
        CheckpointAdvance,
        CompletionAudit,
        ReceiptCommit,
    }

    private void ThrowAfterFailureEvent(
        Exception operationFailure,
        string eventType,
        string action,
        Guid? evidenceId,
        Guid parentEventId,
        string? declaredTarget,
        AuditReservation reservation,
        string? destinationKind = null,
        string? destinationPath = null,
        string failureDetailCode = "operation.failed",
        string? failureScriptDigest = null,
        long? failureBytesReturned = null,
        AuditOperatorDispositionFacts? operatorDisposition = null)
    {
        try
        {
            AppendEvent(
                reservation,
                eventType,
                action,
                "failed",
                failureDetailCode,
                evidenceId,
                failureScriptDigest,
                failureBytesReturned,
                parentEventId,
                declaredTarget,
                destinationKind,
                destinationPath,
                operatorDisposition);
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
        AuditOperatorDispositionFacts disposition,
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
            disposition.TargetSupervisorBootId.ToString("D"),
            operatorDisposition: disposition);

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
        string? declaredTarget,
        string? destinationKind = null,
        string? destinationPath = null,
        AuditOperatorDispositionFacts? operatorDisposition = null)
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
                EffectiveIdentity = _effectiveIdentity,
            },
            Actor = new AuditActor { AttributionStrength = "unknown" },
            Correlation = new AuditCorrelation { ParentEventId = parentEventId },
            Request = new AuditRequest
            {
                Tool = "audit_admin",
                Action = action,
                DestinationKind = destinationKind,
                DestinationPath = destinationPath,
                ScriptEvidenceId = evidenceId,
                OriginalScriptDigest = scriptDigest,
            },
            OperatorDisposition = operatorDisposition,
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

    private static AuditOperatorDispositionFacts RequestDispositionFacts(
        Guid supervisorBootId,
        Guid blockedEventId,
        AuditOperatorDispositionProof proof) => new()
    {
        TargetSupervisorBootId = supervisorBootId,
        TargetEventId = blockedEventId,
        ProofKind = ProofKind(proof),
        VerifiedReceiptDigest = proof.VerifiedReceiptDigest,
        AcknowledgedGapReason = proof.AcknowledgedGapReason,
    };

    private static AuditOperatorDispositionFacts AuthorizedDispositionFacts(
        AuditOperatorDispositionIntent intent) => new()
    {
        DispositionId = intent.DispositionId,
        TargetSupervisorBootId = intent.SupervisorBootId,
        TargetSpoolFile = intent.Spool.FileName,
        TargetStartOffset = intent.StartOffset,
        TargetNextOffset = intent.NextOffset,
        TargetSequence = intent.Sequence,
        TargetEventId = intent.EventId,
        FailureClass = FailureClass(intent.FailureClass),
        DetailCode = intent.DetailCode,
        ResponseDigest = intent.ResponseDigest,
        FirstFailureUtc = intent.FirstFailureUtc,
        TargetExportConfigurationIdentity = intent.ExportConfigurationIdentity,
        ProofKind = ProofKind(intent.Proof),
        VerifiedReceiptDigest = intent.Proof.VerifiedReceiptDigest,
        AcknowledgedGapReason = intent.Proof.AcknowledgedGapReason,
    };

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

    private static AuditAdminFailureKind ClassifyEvidenceFailure(
        Exception failure,
        Guid? parsedEvidenceId,
        string? outputPath,
        string? auditedDestinationPath,
        bool isRead,
        bool readWriteStarted,
        bool readWriteReturned,
        bool readFlushReturned,
        bool exportPublished)
    {
        if (parsedEvidenceId is null)
            return AuditAdminFailureKind.EvidenceIdInvalid;
        if (outputPath is not null && auditedDestinationPath is null)
            return AuditAdminFailureKind.EvidencePathInvalid;

        if (isRead)
        {
            if (readFlushReturned)
                return AuditAdminFailureKind.AuditOutcomeFailedAfterDisclosure;
            if (readWriteReturned)
                return AuditAdminFailureKind.OperationFlushFailedAfterDisclosure;
            if (readWriteStarted)
                return AuditAdminFailureKind.OperationDisclosureUnknown;
        }
        else if (exportPublished)
        {
            return AuditAdminFailureKind.AuditOutcomeFailedAfterPublish;
        }

        return failure switch
        {
            ScriptEvidenceStorageException
            {
                FailureKind: ScriptEvidenceStorageFailureKind.Absent,
            } => AuditAdminFailureKind.EvidenceAbsent,
            ScriptEvidenceStorageException
            {
                FailureKind: ScriptEvidenceStorageFailureKind.ControlInvalid,
            } => AuditAdminFailureKind.EvidenceControlInvalid,
            ScriptEvidenceStorageException
            {
                FailureKind: ScriptEvidenceStorageFailureKind.Storage,
            } => AuditAdminFailureKind.EvidenceStorageFailed,
            AuditEvidenceDestinationException
            {
                FailureKind: AuditEvidenceDestinationFailureKind.InvalidPath,
            } => AuditAdminFailureKind.EvidencePathInvalid,
            AuditEvidenceDestinationException
            {
                FailureKind: AuditEvidenceDestinationFailureKind.Exists,
            } => AuditAdminFailureKind.EvidenceDestinationExists,
            AuditEvidenceDestinationException
            {
                FailureKind: AuditEvidenceDestinationFailureKind.Refused,
            } => AuditAdminFailureKind.EvidenceDestinationRefused,
            AuditEvidenceDestinationException
            {
                FailureKind: AuditEvidenceDestinationFailureKind.Storage,
            } => AuditAdminFailureKind.EvidenceStorageFailed,
            _ when isRead => AuditAdminFailureKind.OperationFailedBeforeDisclosure,
            _ => AuditAdminFailureKind.EvidenceStorageFailed,
        };
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

    private static string? TryCanonicalDestinationPath(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return null;
        try
        {
            return Path.IsPathFullyQualified(outputPath)
                ? Path.GetFullPath(outputPath)
                : null;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return null;
        }
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

    private static string ProofKind(AuditOperatorDispositionProof proof) => proof.Kind switch
    {
        AuditOperatorDispositionProofKind.VerifiedReceipt => "verified_receipt",
        AuditOperatorDispositionProofKind.AcknowledgedGap => "acknowledged_gap",
        _ => throw new ArgumentOutOfRangeException(nameof(proof)),
    };

    private static string FailureClass(AuditExportFailureClass failureClass) => failureClass switch
    {
        AuditExportFailureClass.PartialRejection => "partial_rejection",
        AuditExportFailureClass.Data => "data",
        AuditExportFailureClass.Protocol => "protocol",
        _ => throw new ArgumentOutOfRangeException(nameof(failureClass)),
    };

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
