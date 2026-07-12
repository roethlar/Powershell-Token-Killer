using System.Security.Cryptography;
using System.Text;

namespace PtkMcpServer.Audit;

public sealed record ScriptEvidenceReference(
    string EvidenceId,
    string ScriptDigest,
    int ByteLength);

internal interface IScriptEvidencePublication : IDisposable
{
    ScriptEvidenceReference Reference { get; }

    /// <summary>
    /// Releases the publication/quota lease only after the audit record that
    /// references <see cref="Reference"/> has durably committed.
    /// </summary>
    void CompleteAfterAuditAppend();

    /// <summary>
    /// Marks a publication retention-eligible only when the caller can prove
    /// no audit append was attempted. Ambiguous append failures must Dispose
    /// without calling this method and remain pinned.
    /// </summary>
    void AbandonBeforeAuditAppend();

    /// <summary>
    /// Uses the already-held publication/quota lease to reconcile an append
    /// whose durable outcome was ambiguous. This method must not reacquire the
    /// evidence lease; doing so would self-deadlock.
    /// </summary>
    bool ReconcileAfterAmbiguousAuditAppend(AuditJournal journal);
}

internal interface IAuditEvidenceAnchorLease : IDisposable
{
    /// <summary>
    /// Finalizes retention eligibility after the exact acknowledged record's
    /// checkpoint has durably advanced.
    /// </summary>
    void CompleteAfterCheckpoint();
}

internal readonly record struct AuditEvidenceAcknowledgmentPosition(
    Guid SupervisorBootId,
    long Sequence,
    Guid EventId,
    string EvidenceId,
    string ScriptDigest);

internal enum ScriptEvidenceStorageFailureKind
{
    Absent,
    ControlInvalid,
    Storage,
}

public sealed class ScriptEvidenceStorageException : IOException
{
    internal ScriptEvidenceStorageException()
        : this(ScriptEvidenceStorageFailureKind.Storage)
    {
    }

    internal ScriptEvidenceStorageException(
        ScriptEvidenceStorageFailureKind failureKind,
        Exception? innerException = null)
        : base("Protected script evidence storage failed.", innerException)
    {
        FailureKind = failureKind;
    }

    internal ScriptEvidenceStorageFailureKind FailureKind { get; }
}

/// <summary>
/// Writes the exact strict-UTF-8 bytes of submitted scripts into a protected
/// evidence directory. A reference is returned only after the payload and its
/// directory entry have been durably committed.
/// </summary>
public sealed class ScriptEvidenceStore
{
    public const int MaximumScriptBytes = 131_072;
    private const string QuotaLockFileName = ".ptk-evidence-quota.lock";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _root;
    private readonly int _maximumBytes;
    private readonly long _aggregateBytes;
    private readonly TimeSpan _retentionAge;
    private readonly AuditProtectionMode _protectionMode;
    private readonly string _quotaLockPath;
    private readonly AuditOptions? _checkpointOptions;
    private readonly object _gate = new();
    private readonly Action<SecureAuditStorageFaultStage>? _faultInjector;
    private readonly Action<AuditEvidenceRetentionFaultPoint>? _retentionFaultInjector;

    private enum ArtifactState
    {
        AwaitingAnchor,
        Anchoring,
        Anchored,
        LocalCommitted,
        Unreferenced,
        Temporary,
    }

    public ScriptEvidenceStore(
        string absoluteRootPath,
        Action<SecureAuditStorageFaultStage>? faultInjector = null)
        : this(
            absoluteRootPath,
            MaximumScriptBytes,
            AuditOptions.DefaultEvidenceAggregateBytes,
            AuditOptions.DefaultEvidenceRetentionAge,
            AuditProtectionMode.LocalOnly,
            faultInjector,
            retentionFaultInjector: null,
            checkpointOptions: null)
    {
    }

    internal ScriptEvidenceStore(
        AuditOptions options,
        Action<SecureAuditStorageFaultStage>? faultInjector = null,
        Action<AuditEvidenceRetentionFaultPoint>? retentionFaultInjector = null)
        : this(
            options.EvidenceDirectory,
            options.MaxEvidenceBytes,
            options.EvidenceAggregateBytes,
            options.EvidenceRetentionAge,
            options.ProtectionMode,
            faultInjector,
            retentionFaultInjector,
            options)
    {
    }

    private ScriptEvidenceStore(
        string absoluteRootPath,
        int maximumBytes,
        long aggregateBytes,
        TimeSpan retentionAge,
        AuditProtectionMode protectionMode,
        Action<SecureAuditStorageFaultStage>? faultInjector,
        Action<AuditEvidenceRetentionFaultPoint>? retentionFaultInjector,
        AuditOptions? checkpointOptions)
    {
        _faultInjector = faultInjector;
        _retentionFaultInjector = retentionFaultInjector;
        _maximumBytes = maximumBytes;
        _aggregateBytes = aggregateBytes;
        _retentionAge = retentionAge;
        _protectionMode = protectionMode;
        _checkpointOptions = checkpointOptions;
        try
        {
            _root = SecureAuditStorage.PrepareRoot(absoluteRootPath);
            _quotaLockPath = Path.Combine(_root, QuotaLockFileName);
            EnsureQuotaLockFile();
            lock (_gate)
            using (AcquireQuotaLock())
                _ = MeasureWithoutRetention(requiredPayloadBytes: null);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (ScriptEvidenceControlException exception)
        {
            throw new ScriptEvidenceStorageException(
                ScriptEvidenceStorageFailureKind.ControlInvalid,
                exception);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new ScriptEvidenceStorageException(
                ScriptEvidenceStorageFailureKind.Storage,
                exception);
        }
    }

    public ScriptEvidenceReference Store(string script)
    {
        using var publication = Publish(script);
        publication.CompleteAfterAuditAppend();
        return publication.Reference;
    }

    internal ScriptEvidenceReference Store(string script, AuditJournal retentionJournal)
    {
        using var publication = Publish(script, retentionJournal);
        publication.CompleteAfterAuditAppend();
        return publication.Reference;
    }

    internal IScriptEvidencePublication Publish(string script) =>
        PublishCore(script, retentionJournal: null, retentionContext: null);

    internal IScriptEvidencePublication Publish(string script, AuditJournal retentionJournal)
        => Publish(script, retentionJournal, retentionContext: null);

    internal IScriptEvidencePublication Publish(
        string script,
        AuditJournal retentionJournal,
        AuditEvidenceRetentionContext? retentionContext)
    {
        ArgumentNullException.ThrowIfNull(retentionJournal);
        return PublishCore(script, retentionJournal, retentionContext);
    }

    private IScriptEvidencePublication PublishCore(
        string script,
        AuditJournal? retentionJournal,
        AuditEvidenceRetentionContext? retentionContext)
    {
        ArgumentNullException.ThrowIfNull(script);

        byte[] bytes;
        try
        {
            var byteCount = StrictUtf8.GetByteCount(script);
            if (byteCount > _maximumBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(script),
                    "Script evidence exceeds the strict UTF-8 byte limit.");
            }

            bytes = StrictUtf8.GetBytes(script);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException(
                "Script evidence must be valid strict UTF-8 logical text.",
                nameof(script));
        }

        string? temporaryPath = null;
        IDisposable? quota = null;
        try
        {
            lock (_gate)
            {
                quota = AcquireQuotaLock();
                _ = retentionJournal is null
                    ? MeasureWithoutRetention(bytes.Length)
                    : RetainAndMeasure(bytes.Length, retentionJournal, retentionContext);
                var evidenceId = Guid.NewGuid().ToString("D");
                var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var reference = new ScriptEvidenceReference(evidenceId, digest, bytes.Length);
                temporaryPath = Path.Combine(_root, $".{evidenceId}.tmp");
                var publishedPath = Path.Combine(_root, $"{evidenceId}.{digest}.script");

                using (var stream = SecureAuditStorage.CreateExclusiveFile(
                           temporaryPath,
                           preallocationSize: bytes.Length))
                {
                    InvokeFault(SecureAuditStorageFaultStage.Write);
                    stream.Write(bytes);

                    InvokeFault(SecureAuditStorageFaultStage.Flush);
                    stream.Flush(flushToDisk: true);
                }

                InvokeFault(SecureAuditStorageFaultStage.Publish);
                SecureAuditStorage.PublishAtomically(temporaryPath, publishedPath, _root);
                temporaryPath = null;
                var publication = new EvidencePublication(this, reference, quota);
                quota = null;
                return publication;
            }
        }
        catch (AuditUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            if (temporaryPath is not null)
            {
                SecureAuditStorage.TryDelete(temporaryPath);
            }

            throw new ScriptEvidenceStorageException();
        }
        finally
        {
            quota?.Dispose();
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal void Probe()
    {
        lock (_gate)
        {
            using var quota = AcquireQuotaLock();
            _ = MeasureWithoutRetention(requiredPayloadBytes: null);
            SecureAuditStorage.ProbeWritableDirectory(_root);
        }
    }

    internal void RetainEligible(AuditJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        try
        {
            lock (_gate)
            using (AcquireQuotaLock())
                _ = RetainAndMeasure(
                    requiredPayloadBytes: null,
                    retentionJournal: journal,
                    retentionContext: null);
        }
        catch (AuditUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new ScriptEvidenceStorageException();
        }
    }

    internal IAuditEvidenceAnchorLease MarkAnchored(
        AuditEvidenceAcknowledgmentPosition acknowledgment)
    {
        if (_protectionMode != AuditProtectionMode.Anchored)
        {
            throw new InvalidOperationException(
                "Script evidence anchoring requires anchored protection mode.");
        }
        ValidateAcknowledgment(acknowledgment);
        IDisposable? quota = null;
        try
        {
            lock (_gate)
            {
                quota = AcquireQuotaLock();
                using var inventory = InventoryArtifacts();
                var matches = inventory.Artifacts
                    .Where(artifact =>
                        string.Equals(
                            artifact.EvidenceId,
                            acknowledgment.EvidenceId,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            artifact.Digest,
                            acknowledgment.ScriptDigest,
                            StringComparison.Ordinal))
                    .ToArray();
                if (matches.Length != 1)
                    throw new IOException("The acknowledged evidence artifact state is ambiguous.");

                var artifact = matches[0];
                if (artifact.State is ArtifactState.Unreferenced)
                    throw new IOException("Unreferenced evidence cannot be acknowledged.");
                if (artifact.State is ArtifactState.Anchoring)
                {
                    var prior = artifact.AnchorPosition
                        ?? throw new IOException(
                            "Anchoring evidence has no durable acknowledgment position.");
                    if (prior.SupervisorBootId != acknowledgment.SupervisorBootId ||
                        acknowledgment.Sequence < prior.Sequence ||
                        (acknowledgment.Sequence == prior.Sequence &&
                         acknowledgment.EventId != prior.EventId))
                    {
                        throw new IOException(
                            "The evidence acknowledgment does not follow its durable anchor state.");
                    }
                }

                if (artifact.State is ArtifactState.AwaitingAnchor)
                {
                    var anchoringPath = EvidencePath(
                        acknowledgment.EvidenceId,
                        acknowledgment.ScriptDigest,
                        ArtifactState.Anchoring,
                        acknowledgment);
                    RenameArtifact(artifact, anchoringPath);
                }

                var lease = new EvidenceAnchorLease(
                    this,
                    acknowledgment,
                    artifact.State is ArtifactState.Anchored,
                    quota);
                quota = null;
                return lease;
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new ScriptEvidenceStorageException();
        }
        finally
        {
            quota?.Dispose();
        }
    }

    internal bool ReconcileAwaiting(AuditJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return ReconcileAwaitingCore(
            journal.ScanRetainedEvidenceReferences,
            journal);
    }

    internal bool ReconcileAwaitingBeforeWriter(AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ReconcileAwaitingCore(
            candidates => AuditEvidenceSpoolScanner.CaptureBeforeWriter(
                options,
                candidates),
            retentionJournal: null);
    }

    private bool ReconcileAwaitingCore(
        Func<IReadOnlySet<AuditEvidenceIdentity>, AuditEvidenceReferenceScan> scanReferences,
        AuditJournal? retentionJournal)
    {
        ArgumentNullException.ThrowIfNull(scanReferences);
        try
        {
            lock (_gate)
            using (AcquireQuotaLock())
            {
                // Only a journal-bound periodic pass may delete. Pre-writer
                // reconciliation establishes eligibility but leaves every
                // byte intact until a writer can audit retention.
                _ = retentionJournal is null
                    ? MeasureWithoutRetention(requiredPayloadBytes: null)
                    : RetainAndMeasure(
                        requiredPayloadBytes: null,
                        retentionJournal: retentionJournal,
                        retentionContext: null);
                var changed = false;
                using (var inventory = InventoryArtifacts())
                {
                    var awaiting = inventory.Artifacts
                        .Where(value => value.State == ArtifactState.AwaitingAnchor)
                        .ToArray();
                    if (awaiting.Length == 0) return true;

                    var candidates = new HashSet<AuditEvidenceIdentity>();
                    var evidenceIds = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var artifact in awaiting)
                    {
                        if (evidenceIds.TryGetValue(artifact.EvidenceId, out var prior) &&
                            !string.Equals(prior, artifact.Digest, StringComparison.Ordinal))
                        {
                            throw new IOException(
                                "An awaiting evidence identity has multiple protected digests.");
                        }
                        evidenceIds[artifact.EvidenceId] = artifact.Digest;
                        candidates.Add(new AuditEvidenceIdentity(
                            artifact.EvidenceId,
                            artifact.Digest));
                    }

                    var scan = scanReferences(candidates);
                    if (!scan.IsComplete) return false;
                    foreach (var artifact in awaiting)
                    {
                        var identity = new AuditEvidenceIdentity(
                            artifact.EvidenceId,
                            artifact.Digest);
                        if (scan.ReferencedCandidates.Contains(identity))
                        {
                            if (_protectionMode == AuditProtectionMode.LocalOnly)
                            {
                                RenameArtifact(
                                    artifact,
                                    EvidencePath(
                                        artifact.EvidenceId,
                                        artifact.Digest,
                                        ArtifactState.LocalCommitted));
                                changed = true;
                            }
                            continue;
                        }
                        RenameArtifact(
                            artifact,
                            EvidencePath(
                                artifact.EvidenceId,
                                artifact.Digest,
                                ArtifactState.Unreferenced));
                        changed = true;
                    }
                }

                if (changed)
                {
                    _ = retentionJournal is null
                        ? MeasureWithoutRetention(requiredPayloadBytes: null)
                        : RetainAndMeasure(
                            requiredPayloadBytes: null,
                            retentionJournal: retentionJournal,
                            retentionContext: null);
                }
                return true;
            }
        }
        catch (AuditUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new ScriptEvidenceStorageException();
        }
    }

    /// <summary>
    /// Resolves one opaque evidence ID to exactly one protected artifact,
    /// verifies its filename digest against the retained bytes, and exposes
    /// those bytes only to the supplied in-process consumer. Callers are
    /// responsible for durably recording access intent before invoking this
    /// method. The temporary plaintext buffer is cleared before return.
    /// </summary>
    internal ScriptEvidenceReference ReadExact(
        string evidenceId,
        Action<ReadOnlyMemory<byte>> consume)
    {
        ArgumentException.ThrowIfNullOrEmpty(evidenceId);
        ArgumentNullException.ThrowIfNull(consume);
        if (!IsCanonicalEvidenceId(evidenceId))
            throw new ArgumentException("The script evidence identity is invalid.", nameof(evidenceId));

        byte[]? bytes = null;
        ScriptEvidenceReference? reference = null;
        try
        {
            try
            {
                lock (_gate)
                using (AcquireQuotaLock())
                using (var inventory = InventoryArtifacts())
                {
                    var matches = inventory.Artifacts
                        .Where(artifact => string.Equals(
                            artifact.EvidenceId,
                            evidenceId,
                            StringComparison.Ordinal))
                        .ToArray();
                    if (matches.Length == 0)
                    {
                        throw new ScriptEvidenceStorageException(
                            ScriptEvidenceStorageFailureKind.Absent);
                    }
                    if (matches.Length != 1)
                    {
                        throw new ScriptEvidenceStorageException(
                            ScriptEvidenceStorageFailureKind.ControlInvalid);
                    }

                    var artifact = matches[0];
                    var stream = artifact.RequireHandle();
                    if (stream.Length < 0 || stream.Length > _maximumBytes)
                    {
                        throw new ScriptEvidenceStorageException(
                            ScriptEvidenceStorageFailureKind.ControlInvalid);
                    }

                    bytes = new byte[checked((int)stream.Length)];
                    stream.Position = 0;
                    stream.ReadExactly(bytes);
                    if (stream.Position != stream.Length)
                    {
                        throw new ScriptEvidenceStorageException(
                            ScriptEvidenceStorageFailureKind.ControlInvalid);
                    }

                    var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    if (!string.Equals(digest, artifact.Digest, StringComparison.Ordinal))
                    {
                        throw new ScriptEvidenceStorageException(
                            ScriptEvidenceStorageFailureKind.ControlInvalid);
                    }
                    try
                    {
                        _ = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
                            artifact.Path,
                            stream.SafeFileHandle);
                    }
                    catch (Exception exception) when (!IsFatal(exception))
                    {
                        throw new ScriptEvidenceStorageException(
                            ScriptEvidenceStorageFailureKind.ControlInvalid,
                            exception);
                    }
                    reference = new ScriptEvidenceReference(
                        artifact.EvidenceId,
                        artifact.Digest,
                        bytes.Length);
                }
            }
            catch (ScriptEvidenceStorageException)
            {
                throw;
            }
            catch (ScriptEvidenceControlException exception)
            {
                throw new ScriptEvidenceStorageException(
                    ScriptEvidenceStorageFailureKind.ControlInvalid,
                    exception);
            }
            catch (ArgumentException)
            {
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                throw new ScriptEvidenceStorageException(
                    ScriptEvidenceStorageFailureKind.Storage,
                    exception);
            }

            // The verified snapshot is private to this call. Do not retain the
            // evidence inventory, filesystem quota lease, or process-local gate
            // while invoking a potentially blocking external consumer.
            consume(bytes);
            return reference ?? throw new IOException(
                "The requested evidence artifact was not resolved.");
        }
        finally
        {
            if (bytes is not null)
                CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal IDisposable AcquireQuotaLockForTests() => AcquireQuotaLock();

    private long MeasureWithoutRetention(int? requiredPayloadBytes) =>
        ProcessRetention(requiredPayloadBytes, retentionJournal: null);

    private long RetainAndMeasure(
        int? requiredPayloadBytes,
        AuditJournal retentionJournal,
        AuditEvidenceRetentionContext? retentionContext)
    {
        ArgumentNullException.ThrowIfNull(retentionJournal);
        return ProcessRetention(requiredPayloadBytes, retentionJournal, retentionContext);
    }

    private long ProcessRetention(
        int? requiredPayloadBytes,
        AuditJournal? retentionJournal,
        AuditEvidenceRetentionContext? retentionContext = null)
    {
        if (requiredPayloadBytes is < 0 || requiredPayloadBytes > _maximumBytes)
            throw new IOException("Evidence reservation exceeds its configured bound.");
        // Charge every artifact at its configured worst case, including an
        // empty script. Besides reserving payload capacity before publish,
        // this freezes a finite artifact-count ceiling and prevents an
        // unbounded population of zero/tiny files and full-directory scans.
        var requiredCharge = requiredPayloadBytes.HasValue ? _maximumBytes : 0L;

        var inventory = InventoryArtifacts();
        try
        {
            if (PromoteCheckpointedAnchoring(inventory))
            {
                inventory.Dispose();
                inventory = InventoryArtifacts();
            }
            var retained = inventory.Artifacts.ToList();
            var retentionAuditBlocked = false;
            if (retentionJournal is not null)
            {
                foreach (var temporary in retained
                             .Where(artifact => artifact.State == ArtifactState.Temporary)
                             .OrderBy(artifact => artifact.LastWriteTimeUtc)
                             .ThenBy(artifact => artifact.FileName, StringComparer.Ordinal)
                             .ToArray())
                {
                    if (!DeleteArtifactAudited(
                            temporary,
                            AuditEvidenceRetentionReason.CrashTemporary,
                            retentionJournal,
                            retentionContext))
                    {
                        retentionAuditBlocked = true;
                        break;
                    }
                    retained.Remove(temporary);
                }

                if (!retentionAuditBlocked)
                {
                    var expirationCutoff = DateTime.UtcNow - _retentionAge;
                    foreach (var artifact in retained
                                 .Where(IsOrdinaryRetentionEligible)
                                 .Where(artifact => artifact.LastWriteTimeUtc <= expirationCutoff)
                                 .OrderBy(artifact => artifact.LastWriteTimeUtc)
                                 .ThenBy(artifact => artifact.FileName, StringComparer.Ordinal)
                                 .ToArray())
                    {
                        if (!DeleteArtifactAudited(
                                artifact,
                                AuditEvidenceRetentionReason.AgeExpired,
                                retentionJournal,
                                retentionContext))
                        {
                            retentionAuditBlocked = true;
                            break;
                        }
                        retained.Remove(artifact);
                    }
                }
            }

            var maximumRetained = (_aggregateBytes - requiredCharge) / _maximumBytes;
            if (retentionJournal is not null &&
                !retentionAuditBlocked &&
                retained.Count > maximumRetained)
            {
                foreach (var artifact in retained
                             .Where(IsOrdinaryRetentionEligible)
                             .OrderBy(artifact => artifact.LastWriteTimeUtc)
                             .ThenBy(artifact => artifact.FileName, StringComparer.Ordinal)
                             .ToArray())
                {
                    if (!DeleteArtifactAudited(
                            artifact,
                            AuditEvidenceRetentionReason.CapacityPressure,
                            retentionJournal,
                            retentionContext))
                    {
                        retentionAuditBlocked = true;
                        break;
                    }
                    retained.Remove(artifact);
                    if (retained.Count <= maximumRetained) break;
                }
            }

            if (retained.Count > maximumRetained)
            {
                if (!requiredPayloadBytes.HasValue)
                    return checked((long)retained.Count * _maximumBytes);
                if (retentionAuditBlocked)
                    throw new AuditUnavailableException();
                throw new IOException("Evidence capacity is exhausted.");
            }
            return checked((long)retained.Count * _maximumBytes);
        }
        finally
        {
            inventory.Dispose();
        }
    }

    private EvidenceInventory InventoryArtifacts()
    {
        try
        {
            SecureAuditStorage.VerifyProtectedDirectory(_root);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new ScriptEvidenceControlException(exception);
        }
        var artifacts = new List<EvidenceArtifact>();
        var identities = new HashSet<ProtectedFileIdentity>();
        var maximumEntries = checked(_aggregateBytes / _maximumBytes + 2L);
        long entryCount = 0;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(_root))
            {
                entryCount = checked(entryCount + 1);
                if (entryCount > maximumEntries)
                {
                    throw new ScriptEvidenceControlException(
                        "The evidence inventory exceeds its configured entry bound.");
                }
                if (Directory.Exists(path))
                    throw new ScriptEvidenceControlException(
                        "The evidence root contains an unexpected directory.");
                try
                {
                    SecureAuditStorage.VerifyProtectedFile(path);
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    throw new ScriptEvidenceControlException(exception);
                }
                var file = new FileInfo(path);
                if (string.Equals(file.Name, QuotaLockFileName, StringComparison.Ordinal))
                    continue;
                string evidenceId;
                string expectedDigest;
                ArtifactState state;
                AuditEvidenceAcknowledgmentPosition? anchorPosition;
                if (TryParseTemporaryName(file.Name, out evidenceId))
                {
                    expectedDigest = string.Empty;
                    state = ArtifactState.Temporary;
                    anchorPosition = null;
                }
                else if (!TryParseEvidenceName(
                             file.Name,
                             out evidenceId,
                             out expectedDigest,
                             out state,
                             out anchorPosition))
                {
                    throw new ScriptEvidenceControlException(
                        "The evidence root contains an invalid artifact.");
                }
                if (file.Length > _maximumBytes)
                {
                    throw new ScriptEvidenceControlException(
                        "The evidence root contains an invalid artifact.");
                }

                var stream = new FileStream(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    bufferSize: 16 * 1024,
                    FileOptions.SequentialScan);
                try
                {
                    ProtectedFileIdentity identity;
                    try
                    {
                        identity = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
                            file.FullName,
                            stream.SafeFileHandle);
                    }
                    catch (Exception exception) when (!IsFatal(exception))
                    {
                        throw new ScriptEvidenceControlException(exception);
                    }
                    if (!identities.Add(identity))
                        throw new ScriptEvidenceControlException(
                            "Two evidence names identify the same protected file.");
                    if (stream.Length > _maximumBytes)
                        throw new ScriptEvidenceControlException(
                            "An evidence artifact exceeds its configured bound.");
                    var actualDigest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                    if (state == ArtifactState.Temporary)
                    {
                        expectedDigest = actualDigest;
                    }
                    else if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
                    {
                        throw new ScriptEvidenceControlException(
                            "The evidence artifact digest does not match its protected name.");
                    }
                    artifacts.Add(new EvidenceArtifact(
                        file,
                        evidenceId,
                        expectedDigest,
                        state,
                        anchorPosition,
                        stream,
                        identity));
                    stream = null!;
                }
                finally
                {
                    stream?.Dispose();
                }
            }
            try
            {
                SecureAuditStorage.VerifyProtectedDirectory(_root);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                throw new ScriptEvidenceControlException(exception);
            }
            return new EvidenceInventory(artifacts);
        }
        catch
        {
            foreach (var artifact in artifacts) artifact.Dispose();
            throw;
        }
    }

    private static bool TryParseEvidenceName(
        string name,
        out string evidenceId,
        out string digest,
        out ArtifactState state,
        out AuditEvidenceAcknowledgmentPosition? anchorPosition)
    {
        evidenceId = string.Empty;
        digest = string.Empty;
        state = default;
        anchorPosition = null;
        string stem;
        if (name.EndsWith(".unreferenced.script", StringComparison.Ordinal))
        {
            state = ArtifactState.Unreferenced;
            stem = name[..^20];
        }
        else if (name.Length > 101 &&
                 name.AsSpan(101).StartsWith(".anchoring.", StringComparison.Ordinal) &&
                 name.EndsWith(".script", StringComparison.Ordinal))
        {
            state = ArtifactState.Anchoring;
            stem = name[..101];
            var metadata = name[112..^7].Split('.', StringSplitOptions.None);
            if (metadata.Length != 3 ||
                metadata[0].Length != 32 ||
                !Guid.TryParseExact(metadata[0], "N", out var bootId) ||
                !string.Equals(metadata[0], bootId.ToString("N"), StringComparison.Ordinal) ||
                !AuditSpoolSegmentIdentity.IsUuidV4(bootId) ||
                metadata[1].Length != 20 ||
                !metadata[1].All(char.IsAsciiDigit) ||
                !long.TryParse(
                    metadata[1],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var sequence) ||
                sequence < 1 ||
                !Guid.TryParseExact(metadata[2], "D", out var eventId) ||
                !string.Equals(metadata[2], eventId.ToString("D"), StringComparison.Ordinal) ||
                !IsUuidV7(eventId))
            {
                return false;
            }
            anchorPosition = new AuditEvidenceAcknowledgmentPosition(
                bootId,
                sequence,
                eventId,
                stem[..36],
                stem[37..]);
        }
        else if (name.EndsWith(".anchored.script", StringComparison.Ordinal))
        {
            state = ArtifactState.Anchored;
            stem = name[..^16];
        }
        else if (name.EndsWith(".local-committed.script", StringComparison.Ordinal))
        {
            state = ArtifactState.LocalCommitted;
            stem = name[..^23];
        }
        else if (name.EndsWith(".script", StringComparison.Ordinal))
        {
            state = ArtifactState.AwaitingAnchor;
            stem = name[..^7];
        }
        else
        {
            return false;
        }
        if (stem.Length != 36 + 1 + 64 || stem[36] != '.') return false;
        evidenceId = stem[..36];
        digest = stem[37..];
        if (!IsCanonicalEvidenceId(evidenceId) || !IsLowerHex(digest, 64))
            return false;
        return anchorPosition is null ||
               (string.Equals(
                    anchorPosition.Value.EvidenceId,
                    evidenceId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    anchorPosition.Value.ScriptDigest,
                    digest,
                    StringComparison.Ordinal));
    }

    private bool PromoteCheckpointedAnchoring(EvidenceInventory inventory)
    {
        if (_checkpointOptions is null) return false;
        var promoted = false;
        foreach (var artifact in inventory.Artifacts
                     .Where(value => value.State == ArtifactState.Anchoring))
        {
            var position = artifact.AnchorPosition
                ?? throw new IOException("Anchoring evidence has no checkpoint position.");
            var checkpoint = AuditExportCheckpointStore.ReadSnapshot(
                _checkpointOptions,
                position.SupervisorBootId);
            if (checkpoint.Sequence < position.Sequence) continue;
            if (checkpoint.Sequence == position.Sequence &&
                checkpoint.AcknowledgedEventId != position.EventId)
            {
                throw new IOException(
                    "The evidence anchor does not match its durable checkpoint.");
            }
            RenameArtifact(
                artifact,
                EvidencePath(
                    position.EvidenceId,
                    position.ScriptDigest,
                    ArtifactState.Anchored));
            promoted = true;
        }
        return promoted;
    }

    private static bool IsOrdinaryRetentionEligible(EvidenceArtifact artifact) =>
        artifact.State is ArtifactState.Anchored or ArtifactState.LocalCommitted or
            ArtifactState.Unreferenced;

    private bool DeleteArtifactAudited(
        EvidenceArtifact artifact,
        AuditEvidenceRetentionReason reason,
        AuditJournal journal,
        AuditEvidenceRetentionContext? context)
    {
        var subject = new AuditEvidenceRetentionSubject(
            Guid.ParseExact(artifact.EvidenceId, "D"),
            artifact.Digest,
            artifact.ByteLength,
            ArtifactStateText(artifact.State),
            reason);
        return AuditEvidenceRetentionAudit.TryDelete(
            journal,
            subject,
            () =>
            {
                _retentionFaultInjector?.Invoke(AuditEvidenceRetentionFaultPoint.BeforeDelete);
                DeleteArtifact(artifact);
                _retentionFaultInjector?.Invoke(AuditEvidenceRetentionFaultPoint.AfterDelete);
            },
            () => ExactArtifactStillNamed(artifact),
            context);
    }

    private static string ArtifactStateText(ArtifactState state) => state switch
    {
        ArtifactState.Anchored => "anchored",
        ArtifactState.LocalCommitted => "local_committed",
        ArtifactState.Unreferenced => "unreferenced",
        ArtifactState.Temporary => "temporary",
        _ => throw new IOException("The evidence artifact is not retention-eligible."),
    };

    private static bool ExactArtifactStillNamed(EvidenceArtifact artifact)
    {
        try
        {
            var handle = artifact.RequireHandle();
            return SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
                       artifact.Path,
                       handle.SafeFileHandle) == artifact.Identity;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return false;
        }
    }

    private void DeleteArtifact(EvidenceArtifact artifact)
    {
        var handle = artifact.RequireHandle();
        var observed = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
            artifact.Path,
            handle.SafeFileHandle);
        if (observed != artifact.Identity)
            throw new IOException("An evidence artifact changed identity before retention.");
        SecureAuditStorage.DeleteRetainedProtectedFile(
            _root,
            artifact.Path,
            handle.SafeFileHandle);
        artifact.ReleaseHandle();
    }

    private void RenameArtifact(EvidenceArtifact artifact, string destinationPath)
    {
        var handle = artifact.RequireHandle();
        var before = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
            artifact.Path,
            handle.SafeFileHandle);
        if (before != artifact.Identity)
            throw new IOException("An evidence artifact changed identity before rename.");
        SecureAuditStorage.PublishAtomically(
            artifact.Path,
            destinationPath,
            _root);
        var after = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
            destinationPath,
            handle.SafeFileHandle);
        if (after != artifact.Identity)
            throw new IOException("An evidence artifact changed identity during rename.");
        artifact.ReleaseHandle();
    }

    private void MarkUnreferencedWhileQuotaHeld(ScriptEvidenceReference reference)
    {
        ValidateEvidenceIdentity(reference.EvidenceId, reference.ScriptDigest);
        using var inventory = InventoryArtifacts();
        var matches = inventory.Artifacts
            .Where(artifact =>
                string.Equals(artifact.EvidenceId, reference.EvidenceId, StringComparison.Ordinal) &&
                string.Equals(artifact.Digest, reference.ScriptDigest, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            throw new IOException("The unreferenced evidence state is ambiguous.");
        var artifact = matches[0];
        if (artifact.State == ArtifactState.Unreferenced) return;
        if (artifact.State != ArtifactState.AwaitingAnchor)
            throw new IOException("Only an unpublished audit reference can be abandoned.");
        RenameArtifact(
            artifact,
            EvidencePath(
                reference.EvidenceId,
                reference.ScriptDigest,
                ArtifactState.Unreferenced));
    }

    private bool ReconcilePublicationWhileQuotaHeld(
        ScriptEvidenceReference reference,
        AuditJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ValidateEvidenceIdentity(reference.EvidenceId, reference.ScriptDigest);
        var identity = new AuditEvidenceIdentity(
            reference.EvidenceId,
            reference.ScriptDigest);
        var candidates = new HashSet<AuditEvidenceIdentity> { identity };
        var scan = journal.ScanRetainedEvidenceReferences(candidates);
        if (!scan.IsComplete)
            return false;
        if (scan.ReferencedCandidates.Contains(identity))
        {
            MarkLocalCommittedWhileQuotaHeld(reference);
            return true;
        }
        MarkUnreferencedWhileQuotaHeld(reference);
        return true;
    }

    private void MarkLocalCommittedWhileQuotaHeld(ScriptEvidenceReference reference)
    {
        if (_protectionMode != AuditProtectionMode.LocalOnly)
            return;
        ValidateEvidenceIdentity(reference.EvidenceId, reference.ScriptDigest);
        using var inventory = InventoryArtifacts();
        var matches = inventory.Artifacts
            .Where(artifact =>
                string.Equals(artifact.EvidenceId, reference.EvidenceId, StringComparison.Ordinal) &&
                string.Equals(artifact.Digest, reference.ScriptDigest, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            throw new IOException("The locally committed evidence state is ambiguous.");
        var artifact = matches[0];
        if (artifact.State == ArtifactState.LocalCommitted) return;
        if (artifact.State != ArtifactState.AwaitingAnchor)
            throw new IOException("Only newly published local evidence can be committed.");
        RenameArtifact(
            artifact,
            EvidencePath(
                reference.EvidenceId,
                reference.ScriptDigest,
                ArtifactState.LocalCommitted));
    }

    private void FinalizeAnchoredWhileQuotaHeld(
        AuditEvidenceAcknowledgmentPosition acknowledgment,
        bool alreadyAnchored)
    {
        if (alreadyAnchored) return;
        using var inventory = InventoryArtifacts();
        var matches = inventory.Artifacts
            .Where(artifact =>
                string.Equals(
                    artifact.EvidenceId,
                    acknowledgment.EvidenceId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    artifact.Digest,
                    acknowledgment.ScriptDigest,
                    StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            throw new IOException("The acknowledged evidence finalization state is ambiguous.");
        var artifact = matches[0];
        if (artifact.State == ArtifactState.Anchored) return;
        if (artifact.State != ArtifactState.Anchoring)
            throw new IOException("The evidence artifact is not awaiting checkpoint finalization.");
        RenameArtifact(
            artifact,
            EvidencePath(
                acknowledgment.EvidenceId,
                acknowledgment.ScriptDigest,
                ArtifactState.Anchored));
    }

    private string EvidencePath(
        string evidenceId,
        string scriptDigest,
        ArtifactState state,
        AuditEvidenceAcknowledgmentPosition? acknowledgment = null)
    {
        var suffix = state switch
        {
            ArtifactState.AwaitingAnchor => ".script",
            ArtifactState.Anchoring when acknowledgment is { } position =>
                $".anchoring.{position.SupervisorBootId:N}.{position.Sequence:D20}." +
                $"{position.EventId:D}.script",
            ArtifactState.Anchoring => throw new IOException(
                "Anchoring evidence requires an exact checkpoint position."),
            ArtifactState.Anchored => ".anchored.script",
            ArtifactState.LocalCommitted => ".local-committed.script",
            ArtifactState.Unreferenced => ".unreferenced.script",
            _ => throw new IOException("The evidence artifact state is invalid."),
        };
        return Path.Combine(_root, evidenceId + "." + scriptDigest + suffix);
    }

    private static void ValidateEvidenceIdentity(string evidenceId, string scriptDigest)
    {
        ArgumentException.ThrowIfNullOrEmpty(evidenceId);
        ArgumentException.ThrowIfNullOrEmpty(scriptDigest);
        if (!IsCanonicalEvidenceId(evidenceId) || !IsLowerHex(scriptDigest, 64))
            throw new ArgumentException("The script evidence identity is invalid.");
    }

    private static void ValidateAcknowledgment(
        AuditEvidenceAcknowledgmentPosition acknowledgment)
    {
        ValidateEvidenceIdentity(
            acknowledgment.EvidenceId,
            acknowledgment.ScriptDigest);
        if (!AuditSpoolSegmentIdentity.IsUuidV4(acknowledgment.SupervisorBootId) ||
            acknowledgment.Sequence < 1 ||
            !IsUuidV7(acknowledgment.EventId))
        {
            throw new ArgumentException("The evidence acknowledgment position is invalid.");
        }
    }

    private static bool IsCanonicalEvidenceId(string value) =>
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal) &&
        value[14] == '4' &&
        value[19] is '8' or '9' or 'a' or 'b';

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsUuidV7(Guid value)
    {
        var text = value.ToString("D");
        return text[14] == '7' && text[19] is '8' or '9' or 'a' or 'b';
    }

    private sealed class ScriptEvidenceControlException : IOException
    {
        internal ScriptEvidenceControlException(string message)
            : base(message)
        {
        }

        internal ScriptEvidenceControlException(Exception innerException)
            : base("Protected script evidence control state is invalid.", innerException)
        {
        }
    }

    private sealed class EvidenceInventory(List<EvidenceArtifact> artifacts) : IDisposable
    {
        internal IReadOnlyList<EvidenceArtifact> Artifacts { get; } = artifacts;

        public void Dispose()
        {
            foreach (var artifact in artifacts) artifact.Dispose();
        }
    }

    private sealed class EvidenceArtifact(
        FileInfo file,
        string evidenceId,
        string digest,
        ArtifactState state,
        AuditEvidenceAcknowledgmentPosition? anchorPosition,
        FileStream stream,
        ProtectedFileIdentity identity) : IDisposable
    {
        private FileStream? _stream = stream;

        internal string FileName { get; } = file.Name;
        internal string Path { get; } = file.FullName;
        internal DateTime LastWriteTimeUtc { get; } = file.LastWriteTimeUtc;
        internal long ByteLength { get; } = file.Length;
        internal string EvidenceId { get; } = evidenceId;
        internal string Digest { get; } = digest;
        internal ArtifactState State { get; } = state;
        internal AuditEvidenceAcknowledgmentPosition? AnchorPosition { get; } =
            anchorPosition;
        internal ProtectedFileIdentity Identity { get; } = identity;

        internal FileStream RequireHandle() =>
            _stream ?? throw new IOException("The retained evidence handle is unavailable.");

        internal void ReleaseHandle()
        {
            var value = Interlocked.Exchange(ref _stream, null);
            value?.Dispose();
        }

        public void Dispose() => ReleaseHandle();
    }

    private sealed class EvidencePublication(
        ScriptEvidenceStore owner,
        ScriptEvidenceReference reference,
        IDisposable quota) : IScriptEvidencePublication
    {
        private readonly object _gate = new();
        private IDisposable? _quota = quota;

        public ScriptEvidenceReference Reference { get; } = reference;

        public void CompleteAfterAuditAppend()
        {
            lock (_gate)
            {
                if (_quota is null)
                    throw new ObjectDisposedException(nameof(EvidencePublication));
                owner.MarkLocalCommittedWhileQuotaHeld(Reference);
                ReleaseLocked();
            }
        }

        public void AbandonBeforeAuditAppend()
        {
            lock (_gate)
            {
                if (_quota is null)
                    throw new ObjectDisposedException(nameof(EvidencePublication));
                owner.MarkUnreferencedWhileQuotaHeld(Reference);
                ReleaseLocked();
            }
        }

        public bool ReconcileAfterAmbiguousAuditAppend(AuditJournal journal)
        {
            lock (_gate)
            {
                if (_quota is null)
                    throw new ObjectDisposedException(nameof(EvidencePublication));
                try
                {
                    return owner.ReconcilePublicationWhileQuotaHeld(Reference, journal);
                }
                catch (ScriptEvidenceStorageException)
                {
                    throw;
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    throw new ScriptEvidenceStorageException();
                }
            }
        }

        public void Dispose()
        {
            lock (_gate) ReleaseLocked();
        }

        private void ReleaseLocked()
        {
            var value = _quota;
            _quota = null;
            if (value is null) return;
            Exception? failure = null;
            try
            {
                owner.InvokeFault(SecureAuditStorageFaultStage.Release);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                failure = exception;
            }
            try
            {
                value.Dispose();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                failure ??= exception;
            }
            if (failure is not null) throw new ScriptEvidenceStorageException();
        }
    }

    private sealed class EvidenceAnchorLease(
        ScriptEvidenceStore owner,
        AuditEvidenceAcknowledgmentPosition acknowledgment,
        bool alreadyAnchored,
        IDisposable quota) : IAuditEvidenceAnchorLease
    {
        private readonly object _gate = new();
        private IDisposable? _quota = quota;

        public void CompleteAfterCheckpoint()
        {
            lock (_gate)
            {
                if (_quota is null)
                    throw new ObjectDisposedException(nameof(EvidenceAnchorLease));
                owner.FinalizeAnchoredWhileQuotaHeld(
                    acknowledgment,
                    alreadyAnchored);
                ReleaseLocked();
            }
        }

        public void Dispose()
        {
            lock (_gate) ReleaseLocked();
        }

        private void ReleaseLocked()
        {
            var value = _quota;
            _quota = null;
            value?.Dispose();
        }
    }

    private static bool TryParseTemporaryName(string name, out string evidenceId)
    {
        evidenceId = string.Empty;
        const string probePrefix = ".ptk-audit-probe-";
        if (name.StartsWith(probePrefix, StringComparison.Ordinal) &&
            name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            var probeId = name[probePrefix.Length..^4];
            if (Guid.TryParseExact(probeId, "N", out var parsedProbe) &&
                AuditSpoolSegmentIdentity.IsUuidV4(parsedProbe))
            {
                evidenceId = parsedProbe.ToString("D");
                return true;
            }
        }
        if (!name.StartsWith(".", StringComparison.Ordinal) ||
            !name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }
        var id = name[1..^4];
        if (!Guid.TryParseExact(id, "D", out var value) ||
            value.ToString("D") != id ||
            !AuditSpoolSegmentIdentity.IsUuidV4(value))
        {
            return false;
        }
        evidenceId = id;
        return true;
    }

    private IDisposable AcquireQuotaLock()
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                // An exclusive open is backed by the filesystem object, so
                // case/alias spellings converge on one lock unlike a mutex
                // named from path text. Disposing the handle releases it after
                // a crash on every supported platform.
                var stream = new FileStream(
                    _quotaLockPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                return new FileLockLease(stream);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

    private void EnsureQuotaLockFile()
    {
        if (File.Exists(_quotaLockPath))
        {
            SecureAuditStorage.VerifyProtectedFile(_quotaLockPath);
            return;
        }

        try
        {
            using var stream = SecureAuditStorage.CreateExclusiveFile(_quotaLockPath);
            stream.WriteByte(0x50);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(_quotaLockPath))
        {
            SecureAuditStorage.VerifyProtectedFile(_quotaLockPath);
        }
    }

    private sealed class FileLockLease(FileStream stream) : IDisposable
    {
        private FileStream? _stream = stream;

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _stream, null);
            if (value is null) return;
            value.Dispose();
        }
    }

    private void InvokeFault(SecureAuditStorageFaultStage stage) => _faultInjector?.Invoke(stage);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
