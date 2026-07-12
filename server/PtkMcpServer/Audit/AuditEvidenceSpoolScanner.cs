using System.Text.Json;

namespace PtkMcpServer.Audit;

internal readonly record struct AuditEvidenceIdentity(
    string EvidenceId,
    string ScriptDigest);

internal sealed record AuditEvidenceReferenceScan(
    bool IsComplete,
    IReadOnlySet<AuditEvidenceIdentity> ReferencedCandidates)
{
    internal static AuditEvidenceReferenceScan Incomplete { get; } =
        new(false, new HashSet<AuditEvidenceIdentity>());
}

/// <summary>
/// Proves whether awaiting evidence is absent from one complete, stable view
/// of every retained audit segment. The caller must already hold the evidence
/// publication/quota lease and AuditJournal's gate. This freezes the lock
/// order as evidence -&gt; journal -&gt; global spool topology, matching the only
/// path that can publish evidence and then make it journal-visible.
/// </summary>
internal static class AuditEvidenceSpoolScanner
{
    private static readonly HashSet<string> RootProperties = new(
        [
            "schema_version", "event_id", "event_type", "occurred_utc",
            "observed_utc", "producer", "sequence", "previous_event_hash",
            "session", "actor", "correlation", "request", "operator_disposition", "routing",
            "outcome", "coverage", "audit", "event_hash",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> ProducerProperties = new(
        [
            "host_id", "supervisor_boot_id", "worker_boot_id", "pid",
            "version", "binary_digest",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> SessionProperties = new(
        [
            "name", "generation", "binding_kind", "template_name",
            "template_digest", "bootstrap_digest", "declared_purpose",
            "declared_target", "declared_identity", "effective_identity",
            "allow_cold_background",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> ActorProperties = new(
        [
            "transport", "client_name", "client_version", "client_session_id",
            "attribution_strength",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> CorrelationProperties = new(
        ["call_id", "job_id", "parent_event_id", "trace_id", "plan_id"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> RequestProperties = new(
        [
            "tool", "action", "provided_fields", "session_requested", "cwd",
            "destination_kind", "destination_path", "timeout_ms", "deadline_utc",
            "route", "background", "raw",
            "list_available", "job_id", "offset", "expected_generation",
            "force", "template", "allow_cold_background", "max_bytes",
            "pattern_fingerprint", "output_handle_digest",
            "original_script_digest", "script_evidence_id",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> RoutingProperties = new(
        [
            "domain", "requested_route", "effective_route", "permitted_fallbacks",
            "rtk_version", "rtk_binary_digest", "provenance", "fallback_reason",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> OperatorDispositionProperties = new(
        [
            "disposition_id", "target_supervisor_boot_id", "target_spool_file",
            "target_start_offset", "target_next_offset", "target_sequence",
            "target_event_id", "failure_class", "detail_code", "response_digest",
            "first_failure_utc", "target_export_configuration_identity",
            "proof_kind", "verified_receipt_digest", "acknowledged_gap_reason",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> OutcomeProperties = new(
        [
            "state", "detail_code", "exit_code", "duration_ms", "queue_ms",
            "bytes_returned", "next_offset", "warm_state_lost", "worker_replaced",
            "termination_certainty",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> CoverageProperties = new(
        [
            "ptk_request", "root_process_observed", "descendants_observed",
            "remote_effect_observed",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> AuditProperties = new(
        [
            "protection_mode", "export_configuration_identity", "health_state",
            "failure_class", "degraded_since_utc", "emergency_probe_count",
            "emergency_probe_first_utc", "emergency_probe_last_utc",
        ],
        StringComparer.Ordinal);

    internal static AuditEvidenceReferenceScan CaptureUnderJournalGate(
        AuditOptions options,
        IAuditCommittedSpoolSource liveSource,
        IReadOnlySet<AuditEvidenceIdentity> candidates)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(liveSource);
        ArgumentNullException.ThrowIfNull(candidates);
        return Capture(
            options,
            liveSource.CurrentSegmentIdentity,
            liveSource,
            candidates);
    }

    internal static void ValidateExactEnvelopeShapeForTests(ReadOnlyMemory<byte> exactJson)
    {
        using var document = JsonDocument.Parse(
            exactJson,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        _ = RequireExactEnvelopeShape(document.RootElement);
    }

    /// <summary>
    /// Captures the retained topology before this process creates a writer.
    /// Every segment must therefore be provably closed. A foreign live writer
    /// denies the retained read handle and makes the proof incomplete.
    /// </summary>
    internal static AuditEvidenceReferenceScan CaptureBeforeWriter(
        AuditOptions options,
        IReadOnlySet<AuditEvidenceIdentity> candidates)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(candidates);
        return Capture(options, current: null, liveSource: null, candidates);
    }

    private static AuditEvidenceReferenceScan Capture(
        AuditOptions options,
        AuditSpoolSegmentIdentity? current,
        IAuditCommittedSpoolSource? liveSource,
        IReadOnlySet<AuditEvidenceIdentity> candidates)
    {
        if (candidates.Count == 0)
        {
            return AuditEvidenceReferenceScan.Incomplete;
        }

        try
        {
            if (!AuditSpoolQuotaLease.TryAcquireExisting(
                    options.SpoolDirectory,
                    out var acquiredQuota) ||
                acquiredQuota is null)
            {
                return AuditEvidenceReferenceScan.Incomplete;
            }

            using var quota = acquiredQuota;
            var inventory = Inventory(options, current, verifyProtection: true);
            ValidateCompleteRetainedTopology(inventory, current);
            using var handles = AcquireClosedHandles(inventory, current);
            RequireSameInventory(
                inventory,
                Inventory(options, current, verifyProtection: false));
            handles.VerifyIdentities();
            quota.VerifyOwnership();

            var candidateDigests = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                if (!IsCanonicalEvidenceId(candidate.EvidenceId) ||
                    !IsLowerHex(candidate.ScriptDigest, 64) ||
                    (candidateDigests.TryGetValue(candidate.EvidenceId, out var prior) &&
                     !string.Equals(prior, candidate.ScriptDigest, StringComparison.Ordinal)))
                {
                    throw new IOException("The awaiting evidence candidate set is ambiguous.");
                }
                candidateDigests[candidate.EvidenceId] = candidate.ScriptDigest;
            }

            var referenced = new HashSet<AuditEvidenceIdentity>();
            foreach (var chain in inventory
                         .GroupBy(segment => segment.Identity.SupervisorBootId)
                         .OrderBy(group => group.Key.ToString("N"), StringComparer.Ordinal))
            {
                var expectedSequence = 1L;
                string? expectedPreviousHash = null;
                foreach (var segment in chain.OrderBy(value => value.Identity.Index))
                {
                    if (current is { } liveIdentity &&
                        segment.Identity == liveIdentity)
                    {
                        ScanLiveSegment(
                            options,
                            liveSource ?? throw new IOException(
                                "The live audit evidence source is absent."),
                            segment,
                            candidateDigests,
                            referenced,
                            ref expectedSequence,
                            ref expectedPreviousHash);
                    }
                    else
                    {
                        ScanClosedSegment(
                            options,
                            handles.Require(segment.Identity),
                            segment,
                            candidateDigests,
                            referenced,
                            ref expectedSequence,
                            ref expectedPreviousHash);
                    }
                }
            }

            handles.VerifyIdentities();
            RequireSameInventory(
                inventory,
                Inventory(options, current, verifyProtection: false));
            if (current is { } finalLiveIdentity &&
                (liveSource is null ||
                 liveSource.CurrentSegmentIdentity != finalLiveIdentity))
                throw new IOException("The live audit segment changed during evidence proof.");
            quota.VerifyOwnership();
            return new AuditEvidenceReferenceScan(true, referenced);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Busy/live foreign chains, retained floors, malformed/torn data,
            // unknown entries, bounds failures, and topology drift are all an
            // absence-proof failure. Awaiting artifacts remain pinned.
            return AuditEvidenceReferenceScan.Incomplete;
        }
    }

    private static SegmentDescriptor[] Inventory(
        AuditOptions options,
        AuditSpoolSegmentIdentity? current,
        bool verifyProtection)
    {
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(options.SpoolDirectory));
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        var segments = new List<SegmentDescriptor>();
        var entries = 0;
        var totalBytes = 0L;
        foreach (var entry in new DirectoryInfo(root)
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            entries = checked(entries + 1);
            if (entries > AuditClosedSpoolChainReader.MaximumSpoolInventoryEntries)
                throw new IOException("The evidence-proof spool inventory exceeds its bound.");

            if (entry is FileInfo quotaControl &&
                string.Equals(
                    quotaControl.Name,
                    AuditSpoolQuotaLease.ControlFileName,
                    StringComparison.Ordinal))
            {
                if (verifyProtection)
                    SecureAuditStorage.VerifyExternalProtectedFile(quotaControl.FullName);
                continue;
            }

            if (entry is not FileInfo file ||
                !AuditSpoolSegmentIdentity.TryParse(file.Name, out var identity))
            {
                throw new IOException("The evidence-proof spool contains an unknown entry.");
            }

            file.Refresh();
            if (file.Length < 0 || file.Length > options.SegmentBytes)
                throw new IOException("An evidence-proof segment exceeds its bound.");
            totalBytes = checked(totalBytes + file.Length);
            if (totalBytes > options.AggregateBytes)
                throw new IOException("The evidence-proof spool exceeds its aggregate bound.");
            if (verifyProtection &&
                (current is null || identity != current.Value))
                SecureAuditStorage.VerifyExternalProtectedFile(file.FullName);
            segments.Add(new SegmentDescriptor(identity, file.FullName, file.Length));
        }
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        return segments
            .OrderBy(value => value.Identity.SupervisorBootId.ToString("N"), StringComparer.Ordinal)
            .ThenBy(value => value.Identity.Index)
            .ToArray();
    }

    private static void ValidateCompleteRetainedTopology(
        SegmentDescriptor[] inventory,
        AuditSpoolSegmentIdentity? current)
    {
        if (current is { } liveIdentity &&
            inventory.Count(value => value.Identity == liveIdentity) != 1)
            throw new IOException("The authoritative live audit segment is absent or ambiguous.");

        foreach (var chain in inventory.GroupBy(value => value.Identity.SupervisorBootId))
        {
            var ordered = chain.OrderBy(value => value.Identity.Index).ToArray();
            if (ordered.Length > AuditClosedSpoolChainReader.MaximumClosedChainSegments)
                throw new IOException("An evidence-proof chain exceeds its segment bound.");
            // A nonzero retained floor means an earlier record could already
            // have been deleted. Absence can no longer be proved from bytes.
            if (ordered.Length == 0 || ordered[0].Identity.Index != 0)
                throw new IOException("An evidence-proof chain has a retained prefix gap.");
            for (var index = 0; index < ordered.Length; index++)
            {
                if (ordered[index].Identity.Index != index)
                    throw new IOException("An evidence-proof chain has a missing segment.");
            }
            if (current is { } currentIdentity &&
                chain.Key == currentIdentity.SupervisorBootId &&
                ordered[^1].Identity != currentIdentity)
                throw new IOException("The authoritative writer is not the retained chain tail.");
        }
    }

    private static ClosedHandleSet AcquireClosedHandles(
        SegmentDescriptor[] inventory,
        AuditSpoolSegmentIdentity? current)
    {
        var handles = new List<SegmentHandle>(
            inventory.Length - (current.HasValue ? 1 : 0));
        var identities = new HashSet<ProtectedFileIdentity>();
        try
        {
            foreach (var segment in inventory)
            {
                if (current is { } liveIdentity &&
                    segment.Identity == liveIdentity)
                    continue;
                var stream = new FileStream(
                    segment.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1,
                    FileOptions.RandomAccess);
                try
                {
                    if (stream.Length != segment.Length)
                        throw new IOException("An evidence-proof segment changed length.");
                    var identity = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
                        segment.Path,
                        stream.SafeFileHandle);
                    if (!identities.Add(identity))
                        throw new IOException("Two evidence-proof names identify one file.");
                    handles.Add(new SegmentHandle(segment, stream, identity));
                    stream = null!;
                }
                finally
                {
                    stream?.Dispose();
                }
            }
            return new ClosedHandleSet(handles);
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            throw;
        }
    }

    private static void ScanClosedSegment(
        AuditOptions options,
        SegmentHandle handle,
        SegmentDescriptor segment,
        IReadOnlyDictionary<string, string> candidateDigests,
        HashSet<AuditEvidenceIdentity> referenced,
        ref long expectedSequence,
        ref string? expectedPreviousHash)
    {
        if (handle.Stream.Length != segment.Length)
            throw new IOException("An evidence-proof segment changed during parsing.");
        var buffer = new byte[options.MaxRecordBytes];
        var offset = 0L;
        while (offset < segment.Length)
        {
            var length = ReadClosedRecord(handle.Stream, offset, segment.Length, buffer);
            ParseRecord(
                buffer.AsSpan(0, length),
                segment.Identity.SupervisorBootId,
                candidateDigests,
                referenced,
                ref expectedSequence,
                ref expectedPreviousHash);
            offset = checked(offset + length);
        }
        if (offset != segment.Length)
            throw new IOException("An evidence-proof segment ended ambiguously.");
    }

    private static void ScanLiveSegment(
        AuditOptions options,
        IAuditCommittedSpoolSource source,
        SegmentDescriptor segment,
        IReadOnlyDictionary<string, string> candidateDigests,
        HashSet<AuditEvidenceIdentity> referenced,
        ref long expectedSequence,
        ref string? expectedPreviousHash)
    {
        if (source.CurrentSegmentIdentity != segment.Identity)
        {
            throw new IOException("The authoritative live segment changed before evidence proof.");
        }

        var buffer = new byte[options.MaxRecordBytes];
        var offset = 0L;
        long? durableTail = null;
        while (true)
        {
            var status = source.TryReadCommitted(
                segment.Identity,
                offset,
                buffer,
                out var bytesRead,
                out var observedTail);
            durableTail ??= observedTail;
            if (durableTail.Value != observedTail || observedTail != segment.Length)
            {
                throw new IOException(
                    "The live audit segment contains bytes outside its durable prefix.");
            }

            if (status == AuditCommittedSpoolReadStatus.AtCommittedTail)
            {
                if (bytesRead != 0 || offset != observedTail) throw new IOException(
                    "The live audit committed-tail proof is inconsistent.");
                break;
            }
            if (status != AuditCommittedSpoolReadStatus.Data || bytesRead < 1)
                throw new IOException("The live audit segment is unavailable for evidence proof.");
            var lf = buffer.AsSpan(0, bytesRead).IndexOf((byte)'\n');
            if (lf < 0)
                throw new IOException("A live audit record is torn or exceeds its bound.");
            var length = lf + 1;
            ParseRecord(
                buffer.AsSpan(0, length),
                segment.Identity.SupervisorBootId,
                candidateDigests,
                referenced,
                ref expectedSequence,
                ref expectedPreviousHash);
            offset = checked(offset + length);
        }

        if (source.CurrentSegmentIdentity != segment.Identity)
        {
            throw new IOException("The live audit segment changed during evidence proof.");
        }
    }

    private static int ReadClosedRecord(
        FileStream stream,
        long offset,
        long segmentLength,
        byte[] buffer)
    {
        var available = checked((int)Math.Min(buffer.Length, segmentLength - offset));
        var length = 0;
        while (length < available)
        {
            var read = RandomAccess.Read(
                stream.SafeFileHandle,
                buffer.AsSpan(length, available - length),
                offset + length);
            if (read == 0) break;
            var lf = buffer.AsSpan(length, read).IndexOf((byte)'\n');
            if (lf >= 0) return length + lf + 1;
            length += read;
        }
        throw new IOException(
            length == buffer.Length
                ? "An evidence-proof record has no LF within its bound."
                : "An evidence-proof segment contains a torn record.");
    }

    private static void ParseRecord(
        ReadOnlySpan<byte> exactJsonl,
        Guid expectedBootId,
        IReadOnlyDictionary<string, string> candidateDigests,
        HashSet<AuditEvidenceIdentity> referenced,
        ref long expectedSequence,
        ref string? expectedPreviousHash)
    {
        if (exactJsonl.Length is < 3 or > AuditEventSerializer.MaximumLineBytes ||
            exactJsonl[^1] != (byte)'\n' ||
            HasUtf8Bom(exactJsonl))
        {
            throw new IOException("An evidence-proof JSONL record has invalid framing.");
        }

        var parsed = AuditSpoolRecordCodec.Parse(exactJsonl[..^1], expectedBootId);
        if (parsed.Sequence != expectedSequence ||
            !string.Equals(
                parsed.PreviousEventHash,
                expectedPreviousHash,
                StringComparison.Ordinal))
        {
            throw new IOException("An evidence-proof audit hash chain is discontinuous.");
        }

        using var document = JsonDocument.Parse(
            exactJsonl[..^1].ToArray(),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        var root = RequireExactObject(document.RootElement, RootProperties);
        var request = RequireExactEnvelopeShape(root);

        var evidenceId = NullableString(request, "script_evidence_id");
        var scriptDigest = NullableString(request, "original_script_digest");
        if ((evidenceId is null) != (scriptDigest is null))
            throw new IOException("An audit record has a partial evidence reference.");
        if (evidenceId is not null)
        {
            if (!IsCanonicalEvidenceId(evidenceId) || !IsLowerHex(scriptDigest!, 64))
                throw new IOException("An audit record has an invalid evidence reference.");
            if (candidateDigests.TryGetValue(evidenceId, out var candidateDigest))
            {
                if (!string.Equals(candidateDigest, scriptDigest, StringComparison.Ordinal))
                {
                    throw new IOException(
                        "An awaiting evidence identity has a conflicting audit digest.");
                }
                referenced.Add(new AuditEvidenceIdentity(evidenceId, scriptDigest!));
            }
        }

        expectedSequence = checked(expectedSequence + 1);
        expectedPreviousHash = parsed.EventHash;
    }

    private static JsonElement RequireExactEnvelopeShape(JsonElement root)
    {
        root = RequireExactObject(root, RootProperties);
        _ = RequireExactObject(root.GetProperty("producer"), ProducerProperties);
        _ = RequireExactObject(root.GetProperty("session"), SessionProperties);
        _ = RequireExactObject(root.GetProperty("actor"), ActorProperties);
        _ = RequireExactObject(root.GetProperty("correlation"), CorrelationProperties);
        var request = RequireExactObject(root.GetProperty("request"), RequestProperties);
        var disposition = root.GetProperty("operator_disposition");
        if (disposition.ValueKind != JsonValueKind.Null)
            _ = RequireExactObject(disposition, OperatorDispositionProperties);
        _ = RequireExactObject(root.GetProperty("routing"), RoutingProperties);
        _ = RequireExactObject(root.GetProperty("outcome"), OutcomeProperties);
        _ = RequireExactObject(root.GetProperty("coverage"), CoverageProperties);
        _ = RequireExactObject(root.GetProperty("audit"), AuditProperties);
        return request;
    }

    private static JsonElement RequireExactObject(
        JsonElement element,
        IReadOnlySet<string> expectedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new IOException("An evidence-proof audit container is not an object.");
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!observed.Add(property.Name) || !expectedProperties.Contains(property.Name))
                throw new IOException("An evidence-proof audit object shape is invalid.");
        }
        if (!observed.SetEquals(expectedProperties))
            throw new IOException("An evidence-proof audit object is incomplete.");
        return element;
    }

    private static string? NullableString(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new IOException("An evidence-proof audit field has the wrong type.");
        return value.GetString()
            ?? throw new IOException("An evidence-proof audit field is invalid.");
    }

    private static void RequireSameInventory(
        SegmentDescriptor[] expected,
        SegmentDescriptor[] observed)
    {
        if (expected.Length != observed.Length)
            throw new IOException("The evidence-proof spool topology changed.");
        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index] != observed[index])
                throw new IOException("An evidence-proof segment changed during snapshot.");
        }
    }

    private static bool IsCanonicalEvidenceId(string value) =>
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal) &&
        value[14] == '4' && value[19] is '8' or '9' or 'a' or 'b';

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool HasUtf8Bom(ReadOnlySpan<byte> value) =>
        value.Length >= 3 &&
        value[0] == 0xef && value[1] == 0xbb && value[2] == 0xbf;

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private readonly record struct SegmentDescriptor(
        AuditSpoolSegmentIdentity Identity,
        string Path,
        long Length);

    private sealed class SegmentHandle(
        SegmentDescriptor descriptor,
        FileStream stream,
        ProtectedFileIdentity identity) : IDisposable
    {
        internal SegmentDescriptor Descriptor { get; } = descriptor;
        internal FileStream Stream { get; } = stream;
        internal ProtectedFileIdentity Identity { get; } = identity;

        public void Dispose() => Stream.Dispose();
    }

    private sealed class ClosedHandleSet(List<SegmentHandle> handles) : IDisposable
    {
        internal SegmentHandle Require(AuditSpoolSegmentIdentity identity) =>
            handles.Single(value => value.Descriptor.Identity == identity);

        internal void VerifyIdentities()
        {
            var identities = new HashSet<ProtectedFileIdentity>();
            foreach (var handle in handles)
            {
                if (handle.Stream.Length != handle.Descriptor.Length)
                    throw new IOException("An evidence-proof segment changed length.");
                var observed = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
                    handle.Descriptor.Path,
                    handle.Stream.SafeFileHandle);
                if (observed != handle.Identity || !identities.Add(observed))
                    throw new IOException("An evidence-proof segment changed identity.");
            }
        }

        public void Dispose()
        {
            foreach (var handle in handles) handle.Dispose();
        }
    }
}
