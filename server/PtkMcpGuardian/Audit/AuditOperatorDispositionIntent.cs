using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PtkMcpServer.Audit;

internal enum AuditOperatorDispositionProofKind
{
    VerifiedReceipt,
    AcknowledgedGap,
}

internal sealed record AuditOperatorDispositionProof
{
    private static readonly Regex ReasonPattern = new(
        "^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private AuditOperatorDispositionProof(
        AuditOperatorDispositionProofKind kind,
        string? verifiedReceiptDigest,
        string? acknowledgedGapReason)
    {
        Kind = kind;
        VerifiedReceiptDigest = verifiedReceiptDigest;
        AcknowledgedGapReason = acknowledgedGapReason;
        Validate();
    }

    internal AuditOperatorDispositionProofKind Kind { get; }

    internal string? VerifiedReceiptDigest { get; }

    internal string? AcknowledgedGapReason { get; }

    internal static AuditOperatorDispositionProof VerifiedReceipt(string digest) =>
        new(AuditOperatorDispositionProofKind.VerifiedReceipt, digest, null);

    internal static AuditOperatorDispositionProof AcknowledgedGap(string reason) =>
        new(AuditOperatorDispositionProofKind.AcknowledgedGap, null, reason);

    private void Validate()
    {
        switch (Kind)
        {
            case AuditOperatorDispositionProofKind.VerifiedReceipt
                when IsLowerHex(VerifiedReceiptDigest, 64) &&
                     AcknowledgedGapReason is null:
            case AuditOperatorDispositionProofKind.AcknowledgedGap
                when VerifiedReceiptDigest is null &&
                     AcknowledgedGapReason is { Length: <= 128 } reason &&
                     ReasonPattern.IsMatch(reason):
                return;
            default:
                throw new ArgumentException(
                    "An operator disposition requires exactly one valid proof.");
        }
    }

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

/// <summary>
/// Immutable, protected, idempotent authority for one exact permanent export
/// block. Merely constructing this object never advances a checkpoint; the
/// checkpoint store re-reads the durable bytes immediately before consuming
/// the one-process capability.
/// </summary>
internal sealed class AuditOperatorDispositionIntent
{
    internal const string SchemaVersion = "ptk.operator-disposition/1";
    internal const int MaximumBytes = 8 * 1024;
    internal const int MaximumDispositionsPerBoot = 64;
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    internal const string FilePrefix = "operator.disposition-";
    internal const string FileSuffix = ".json";
    private const string TemporaryPrefix = ".operator.disposition-";
    private const string TemporarySuffix = ".json.tmp";

    private static readonly HashSet<string> Properties = new(
        [
            "schema_version",
            "disposition_id",
            "supervisor_boot_id",
            "spool_file",
            "start_offset",
            "next_offset",
            "sequence",
            "event_id",
            "failure_class",
            "detail_code",
            "response_digest",
            "first_failure_utc",
            "export_configuration_identity",
            "proof_kind",
            "verified_receipt_digest",
            "acknowledged_gap_reason",
            "created_utc",
        ],
        StringComparer.Ordinal);

    private readonly string _path;
    private readonly byte[] _canonicalBytes;
    private int _consumed;

    private AuditOperatorDispositionIntent(
        string path,
        Guid dispositionId,
        Guid supervisorBootId,
        AuditSpoolSegmentIdentity spool,
        long startOffset,
        long nextOffset,
        long sequence,
        Guid eventId,
        AuditExportFailureClass failureClass,
        string detailCode,
        string? responseDigest,
        DateTimeOffset firstFailureUtc,
        string exportConfigurationIdentity,
        AuditOperatorDispositionProof proof,
        DateTimeOffset createdUtc,
        byte[] canonicalBytes)
    {
        _path = path;
        DispositionId = dispositionId;
        SupervisorBootId = supervisorBootId;
        Spool = spool;
        StartOffset = startOffset;
        NextOffset = nextOffset;
        Sequence = sequence;
        EventId = eventId;
        FailureClass = failureClass;
        DetailCode = detailCode;
        ResponseDigest = responseDigest;
        FirstFailureUtc = firstFailureUtc;
        ExportConfigurationIdentity = exportConfigurationIdentity;
        Proof = proof;
        CreatedUtc = createdUtc;
        _canonicalBytes = canonicalBytes;
        Validate();
    }

    internal Guid DispositionId { get; }

    internal Guid SupervisorBootId { get; }

    internal AuditSpoolSegmentIdentity Spool { get; }

    internal long StartOffset { get; }

    internal long NextOffset { get; }

    internal long Sequence { get; }

    internal Guid EventId { get; }

    internal AuditExportFailureClass FailureClass { get; }

    internal string DetailCode { get; }

    internal string? ResponseDigest { get; }

    internal DateTimeOffset FirstFailureUtc { get; }

    internal string ExportConfigurationIdentity { get; }

    internal AuditOperatorDispositionProof Proof { get; }

    internal DateTimeOffset CreatedUtc { get; }

    internal string CanonicalSha256 => LowerSha256(_canonicalBytes);

    internal static AuditOperatorDispositionIntent CreateOrOpen(
        AuditOptions options,
        IAuditClosedSpoolRecordPosition position,
        AuditExportBlockedRecord blocked,
        AuditOperatorDispositionProof proof,
        Func<DateTimeOffset>? utcNow = null,
        Func<Guid>? dispositionIdFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(blocked);
        ArgumentNullException.ThrowIfNull(proof);
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
            throw new ArgumentException("Operator disposition requires anchored audit mode.", nameof(options));
        RequireExactBlock(position, blocked);
        if (blocked.FailureClass is not (
                AuditExportFailureClass.PartialRejection or
                AuditExportFailureClass.Data or
                AuditExportFailureClass.Protocol))
        {
            throw new InvalidOperationException(
                "Only a permanent audit export block accepts operator disposition.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootDirectory));
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        var path = Path.Combine(root, FileName(blocked.Spool.SupervisorBootId, blocked.EventId));
        var expected = new IntentFields(
            dispositionIdFactory?.Invoke() ?? Guid.NewGuid(),
            blocked.Spool.SupervisorBootId,
            blocked.Spool,
            blocked.ByteOffset,
            position.NextOffset,
            blocked.Sequence,
            blocked.EventId,
            blocked.FailureClass,
            blocked.DetailCode,
            blocked.ResponseDigest,
            blocked.FirstFailureUtc,
            blocked.ExportConfigurationIdentity,
            proof,
            (utcNow?.Invoke() ?? DateTimeOffset.UtcNow).ToUniversalTime());
        ValidateFields(expected);

        var existing = OpenForRetirement(options, blocked.Spool.SupervisorBootId);
        AuditOperatorDispositionOutcome.ValidateBoundedInventory(
            options,
            blocked.Spool.SupervisorBootId,
            existing);
        var exact = existing.SingleOrDefault(value => value.EventId == blocked.EventId);
        if (exact is not null)
            return RequireCompatible(exact, expected, ignoreIdentityAndTime: true);
        if (existing.Count >= MaximumDispositionsPerBoot)
        {
            throw new AuditOperatorDispositionIntentException(
                AuditOperatorDispositionIntentFailureKind.Invalid,
                "The target audit boot reached its operator disposition control bound.");
        }

        var bytes = Serialize(expected);
        var temporaryPath = Path.Combine(root, TemporaryFileName(
            blocked.Spool.SupervisorBootId,
            blocked.EventId));
        using (var stream = SecureAuditStorage.CreateExclusiveFile(
                   temporaryPath,
                   preallocationSize: bytes.Length))
        {
            try
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            catch
            {
                SecureAuditStorage.DeleteRetainedProtectedFile(
                    root,
                    temporaryPath,
                    stream.SafeFileHandle);
                throw;
            }
        }
        try
        {
            SecureAuditStorage.PublishAtomically(temporaryPath, path, root);
        }
        catch (Exception exception) when (
            AuditJournalFactory.IsConcurrentPublishCollision(exception) && EntryExists(path))
        {
            var concurrent = OpenForRetirement(options, blocked.Spool.SupervisorBootId)
                .Single(value => value.EventId == blocked.EventId);
            return RequireCompatible(concurrent, expected, ignoreIdentityAndTime: true);
        }
        return RequireCompatible(Read(path), expected, ignoreIdentityAndTime: false);
    }

    internal static AuditOperatorDispositionIntent? OpenExisting(
        AuditOptions options,
        Guid supervisorBootId,
        Guid eventId,
        AuditOperatorDispositionProof proof)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(proof);
        AuditSpoolSegmentIdentity.RequireUuidV4(supervisorBootId, nameof(supervisorBootId));
        if (!IsUuidVersion(eventId, 7))
            throw new ArgumentException("A canonical UUIDv7 event ID is required.", nameof(eventId));
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
            throw new ArgumentException("Operator disposition requires anchored audit mode.", nameof(options));

        var intents = OpenForRetirement(options, supervisorBootId);
        AuditOperatorDispositionOutcome.ValidateBoundedInventory(
            options,
            supervisorBootId,
            intents);
        var persisted = intents.SingleOrDefault(value => value.EventId == eventId);
        if (persisted is null) return null;
        if (persisted.SupervisorBootId != supervisorBootId ||
            persisted.EventId != eventId ||
            persisted.Proof != proof)
        {
            throw new AuditOperatorDispositionIntentException(
                AuditOperatorDispositionIntentFailureKind.Conflict,
                "A conflicting operator disposition intent already exists.");
        }
        return persisted;
    }

    internal static IReadOnlyList<AuditOperatorDispositionIntent> OpenForRetirement(
        AuditOptions options,
        Guid supervisorBootId)
    {
        ArgumentNullException.ThrowIfNull(options);
        AuditSpoolSegmentIdentity.RequireUuidV4(supervisorBootId, nameof(supervisorBootId));
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
            throw new ArgumentException("Operator disposition requires anchored audit mode.", nameof(options));

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootDirectory));
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        var publishedPrefix = FilePrefix + supervisorBootId.ToString("N") + "-";
        var temporaryPrefix = TemporaryPrefix + supervisorBootId.ToString("N") + "-";
        var published = new Dictionary<Guid, string>();
        var temporaries = new Dictionary<Guid, string>();
        foreach (var entry in new DirectoryInfo(root)
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            var isPublished = entry.Name.StartsWith(publishedPrefix, StringComparison.Ordinal);
            var isTemporary = entry.Name.StartsWith(temporaryPrefix, StringComparison.Ordinal);
            if (!isPublished && !isTemporary)
                continue;
            if (entry is not FileInfo file)
            {
                throw InvalidControl("The audit root contains malformed operator disposition state.");
            }
            Guid bootId;
            Guid eventId;
            if (isPublished
                    ? !TryParseFileName(file.Name, out bootId, out eventId)
                    : !TryParseTemporaryFileName(file.Name, out bootId, out eventId) ||
                      isPublished)
            {
                throw InvalidControl("The audit root contains malformed operator disposition state.");
            }
            if (bootId != supervisorBootId)
                throw InvalidControl("An operator disposition control names another boot.");
            var controls = isPublished ? published : temporaries;
            if (!controls.TryAdd(eventId, file.FullName))
                throw InvalidControl("The audit root contains duplicate operator disposition state.");
        }

        var eventIds = published.Keys.Concat(temporaries.Keys).Distinct().ToArray();
        if (eventIds.Length > MaximumDispositionsPerBoot ||
            published.Count + temporaries.Count > MaximumDispositionsPerBoot * 2)
        {
            throw new AuditOperatorDispositionIntentException(
                AuditOperatorDispositionIntentFailureKind.Invalid,
                "The target audit boot exceeds its operator disposition control bound.");
        }

        var intents = new List<AuditOperatorDispositionIntent>(eventIds.Length);
        foreach (var eventId in eventIds.OrderBy(value => value.ToString("D"), StringComparer.Ordinal))
        {
            var finalPath = Path.Combine(root, FileName(supervisorBootId, eventId));
            var temporaryPath = Path.Combine(root, TemporaryFileName(supervisorBootId, eventId));
            var hasPublished = published.ContainsKey(eventId);
            var hasTemporary = temporaries.ContainsKey(eventId);
            if (hasPublished && hasTemporary)
            {
                if (OperatingSystem.IsWindows())
                {
                    throw new AuditOperatorDispositionIntentException(
                        AuditOperatorDispositionIntentFailureKind.Invalid,
                        "The operator disposition publication has ambiguous aliases.");
                }
                RecoverPublishedAliasControl(
                    root,
                    finalPath,
                    temporaryPath,
                    supervisorBootId,
                    eventId);
            }
            else if (hasTemporary)
            {
                var pending = ReadControl(temporaryPath);
                RequireNamedTarget(pending, supervisorBootId, eventId);
                try
                {
                    SecureAuditStorage.PublishAtomically(temporaryPath, finalPath, root);
                }
                catch (Exception exception) when (
                    AuditJournalFactory.IsConcurrentPublishCollision(exception) &&
                    EntryExists(finalPath))
                {
                    if (EntryExists(temporaryPath))
                    {
                        if (OperatingSystem.IsWindows())
                            throw InvalidControl(
                                "The operator disposition publication is ambiguous.");
                        RecoverPublishedAliasControl(
                            root,
                            finalPath,
                            temporaryPath,
                            supervisorBootId,
                            eventId);
                    }
                }
            }
            var intent = ReadControl(finalPath);
            RequireNamedTarget(intent, supervisorBootId, eventId);
            intents.Add(intent);
        }
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        return intents;
    }

    internal void ConsumeForCheckpointAdvance(
        Guid supervisorBootId,
        long nextOffset,
        AuditExportBlockedRecord blocked)
    {
        ArgumentNullException.ThrowIfNull(blocked);
        if (Volatile.Read(ref _consumed) != 0)
            throw new InvalidOperationException("The operator disposition intent was already consumed.");
        RequireExactTarget(
            supervisorBootId,
            nextOffset,
            blocked);

        var persisted = ReadControl(_path);
        if (!persisted._canonicalBytes.AsSpan().SequenceEqual(_canonicalBytes) ||
            persisted.DispositionId != DispositionId)
        {
            throw InvalidControl("The durable operator disposition intent changed.");
        }
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
            throw new InvalidOperationException("The operator disposition intent was already consumed.");
    }

    internal bool IsAlreadyApplied(AuditExportCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.SupervisorBootId != SupervisorBootId ||
            checkpoint.Sequence < Sequence ||
            (checkpoint.Sequence == Sequence &&
             (checkpoint.Spool != Spool ||
              checkpoint.ByteOffset != NextOffset ||
              checkpoint.AcknowledgedEventId != EventId)))
        {
            return false;
        }
        return checkpoint.BlockedRecord is null ||
               checkpoint.BlockedRecord.Sequence > Sequence;
    }

    internal void DeleteForRetirement(AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Delete,
            bufferSize: 1,
            FileOptions.WriteThrough);
        _ = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
            _path,
            stream.SafeFileHandle);
        if (stream.Length != _canonicalBytes.Length)
            throw new IOException("The operator disposition intent changed before retirement.");
        var observed = new byte[_canonicalBytes.Length];
        try
        {
            stream.ReadExactly(observed);
            if (!observed.AsSpan().SequenceEqual(_canonicalBytes))
                throw new IOException("The operator disposition intent changed before retirement.");
            SecureAuditStorage.DeleteRetainedProtectedFile(
                options.RootDirectory,
                _path,
                stream.SafeFileHandle);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(observed);
        }
    }

    private void RequireExactTarget(
        Guid supervisorBootId,
        long nextOffset,
        AuditExportBlockedRecord blocked)
    {
        if (SupervisorBootId != supervisorBootId ||
            Spool != blocked.Spool ||
            StartOffset != blocked.ByteOffset ||
            NextOffset != nextOffset ||
            Sequence != blocked.Sequence ||
            EventId != blocked.EventId ||
            FailureClass != blocked.FailureClass ||
            !string.Equals(DetailCode, blocked.DetailCode, StringComparison.Ordinal) ||
            !string.Equals(ResponseDigest, blocked.ResponseDigest, StringComparison.Ordinal) ||
            FirstFailureUtc != blocked.FirstFailureUtc ||
            !string.Equals(
                ExportConfigurationIdentity,
                blocked.ExportConfigurationIdentity,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The operator disposition intent belongs to another blocked record.");
        }
    }

    private static AuditOperatorDispositionIntent RequireCompatible(
        AuditOperatorDispositionIntent actual,
        IntentFields expected,
        bool ignoreIdentityAndTime)
    {
        var compatible = actual.SupervisorBootId == expected.SupervisorBootId &&
                         actual.Spool == expected.Spool &&
                         actual.StartOffset == expected.StartOffset &&
                         actual.NextOffset == expected.NextOffset &&
                         actual.Sequence == expected.Sequence &&
                         actual.EventId == expected.EventId &&
                         actual.FailureClass == expected.FailureClass &&
                         string.Equals(actual.DetailCode, expected.DetailCode, StringComparison.Ordinal) &&
                         string.Equals(actual.ResponseDigest, expected.ResponseDigest, StringComparison.Ordinal) &&
                         actual.FirstFailureUtc == expected.FirstFailureUtc &&
                         string.Equals(
                             actual.ExportConfigurationIdentity,
                             expected.ExportConfigurationIdentity,
                             StringComparison.Ordinal) &&
                         actual.Proof == expected.Proof;
        if (!ignoreIdentityAndTime)
        {
            compatible &= actual.DispositionId == expected.DispositionId &&
                          actual.CreatedUtc == expected.CreatedUtc;
        }
        if (!compatible)
        {
            throw new AuditOperatorDispositionIntentException(
                AuditOperatorDispositionIntentFailureKind.Conflict,
                "A conflicting operator disposition intent already exists.");
        }
        return actual;
    }

    private static void RequireExactBlock(
        IAuditClosedSpoolRecordPosition position,
        AuditExportBlockedRecord blocked)
    {
        if (position.Spool != blocked.Spool ||
            position.StartOffset != blocked.ByteOffset ||
            position.Sequence != blocked.Sequence ||
            position.EventId != blocked.EventId)
        {
            throw new ArgumentException(
                "The blocked export state does not identify the resolved spool record.",
                nameof(blocked));
        }
    }

    private static AuditOperatorDispositionIntent Read(string path)
    {
        var bytes = SecureAuditStorage.ReadProtectedFile(
            path,
            MaximumBytes,
            requireProtectedParent: true,
            verifyWithoutMutation: true,
            share: FileShare.Read);
        return ReadCanonical(path, bytes);
    }

    private static AuditOperatorDispositionIntent ReadControl(string path)
    {
        try
        {
            return Read(path);
        }
        catch (AuditOperatorDispositionIntentException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new AuditOperatorDispositionIntentException(
                AuditOperatorDispositionIntentFailureKind.Invalid,
                "The operator disposition intent is invalid.",
                exception);
        }
    }

    private static AuditOperatorDispositionIntent ReadCanonical(
        string path,
        ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var fields = Parse(bytes);
            var canonical = Serialize(fields);
            if (!bytes.Span.SequenceEqual(canonical))
                throw new IOException("The operator disposition intent is not canonical.");
            return new AuditOperatorDispositionIntent(
                path,
                fields.DispositionId,
                fields.SupervisorBootId,
                fields.Spool,
                fields.StartOffset,
                fields.NextOffset,
                fields.Sequence,
                fields.EventId,
                fields.FailureClass,
                fields.DetailCode,
                fields.ResponseDigest,
                fields.FirstFailureUtc,
                fields.ExportConfigurationIdentity,
                fields.Proof,
                fields.CreatedUtc,
                canonical);
        }
        catch (Exception exception) when (exception is not IOException)
        {
            throw new IOException("The operator disposition intent is invalid.");
        }
    }

    private static void RequireNamedTarget(
        AuditOperatorDispositionIntent intent,
        Guid supervisorBootId,
        Guid eventId)
    {
        if (intent.SupervisorBootId != supervisorBootId || intent.EventId != eventId)
            throw new IOException("An operator disposition intent names another target.");
    }

    private static void RecoverPublishedAlias(
        string root,
        string publishedPath,
        string temporaryPath,
        Guid supervisorBootId,
        Guid eventId)
    {
        using var published = OpenPublishedAlias(publishedPath);
        using var temporary = OpenPublishedAlias(temporaryPath);
        if (published.Length is < 2 or > MaximumBytes ||
            published.Length != temporary.Length)
        {
            throw new IOException("The operator disposition alias length is invalid.");
        }
        var publishedBytes = new byte[checked((int)published.Length)];
        var temporaryBytes = new byte[publishedBytes.Length];
        try
        {
            published.ReadExactly(publishedBytes);
            temporary.ReadExactly(temporaryBytes);
            if (!publishedBytes.AsSpan().SequenceEqual(temporaryBytes))
                throw new IOException("The operator disposition aliases differ.");
            var intent = ReadCanonical(publishedPath, publishedBytes);
            RequireNamedTarget(intent, supervisorBootId, eventId);
            SecureAuditStorage.RemoveRetainedPublishedAlias(
                root,
                publishedPath,
                published.SafeFileHandle,
                temporaryPath,
                temporary.SafeFileHandle);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publishedBytes);
            CryptographicOperations.ZeroMemory(temporaryBytes);
        }
    }

    private static void RecoverPublishedAliasControl(
        string root,
        string publishedPath,
        string temporaryPath,
        Guid supervisorBootId,
        Guid eventId)
    {
        try
        {
            RecoverPublishedAlias(
                root,
                publishedPath,
                temporaryPath,
                supervisorBootId,
                eventId);
        }
        catch (AuditOperatorDispositionIntentException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new AuditOperatorDispositionIntentException(
                AuditOperatorDispositionIntentFailureKind.Invalid,
                "The operator disposition publication is invalid.",
                exception);
        }
    }

    private static AuditOperatorDispositionIntentException InvalidControl(string message) =>
        new(AuditOperatorDispositionIntentFailureKind.Invalid, message);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static FileStream OpenPublishedAlias(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Delete,
            bufferSize: 1,
            FileOptions.WriteThrough);
        try
        {
            _ = SecureAuditStorage.VerifyRetainedPublishedAliasIdentity(
                path,
                stream.SafeFileHandle);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static byte[] Serialize(IntentFields fields)
    {
        ValidateFields(fields);
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.Default,
                   Indented = false,
                   SkipValidation = false,
               }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", SchemaVersion);
            writer.WriteString("disposition_id", fields.DispositionId.ToString("D"));
            writer.WriteString("supervisor_boot_id", fields.SupervisorBootId.ToString("D"));
            writer.WriteString("spool_file", fields.Spool.FileName);
            writer.WriteNumber("start_offset", fields.StartOffset);
            writer.WriteNumber("next_offset", fields.NextOffset);
            writer.WriteNumber("sequence", fields.Sequence);
            writer.WriteString("event_id", fields.EventId.ToString("D"));
            writer.WriteString("failure_class", FailureClassText(fields.FailureClass));
            writer.WriteString("detail_code", fields.DetailCode);
            WriteNullableString(writer, "response_digest", fields.ResponseDigest);
            writer.WriteString(
                "first_failure_utc",
                fields.FirstFailureUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            writer.WriteString(
                "export_configuration_identity",
                fields.ExportConfigurationIdentity);
            writer.WriteString("proof_kind", ProofKindText(fields.Proof.Kind));
            WriteNullableString(writer, "verified_receipt_digest", fields.Proof.VerifiedReceiptDigest);
            WriteNullableString(writer, "acknowledged_gap_reason", fields.Proof.AcknowledgedGapReason);
            writer.WriteString(
                "created_utc",
                fields.CreatedUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            writer.WriteEndObject();
            writer.Flush();
        }
        if (buffer.WrittenCount >= MaximumBytes)
            throw new IOException("The operator disposition intent exceeds its bound.");
        var result = new byte[checked(buffer.WrittenCount + 1)];
        buffer.WrittenSpan.CopyTo(result);
        result[^1] = (byte)'\n';
        return result;
    }

    private static IntentFields Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 2 or > MaximumBytes || bytes.Span[^1] != (byte)'\n')
            throw new IOException("The operator disposition intent is invalid.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 3,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new IOException("The operator disposition intent is invalid.");
        var names = root.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != Properties.Count ||
            names.Distinct(StringComparer.Ordinal).Count() != Properties.Count ||
            names.Any(name => !Properties.Contains(name)))
        {
            throw new IOException("The operator disposition intent is invalid.");
        }
        if (!string.Equals(RequiredString(root, "schema_version"), SchemaVersion, StringComparison.Ordinal))
            throw new IOException("The operator disposition intent is invalid.");

        var bootId = RequiredGuid(root, "supervisor_boot_id", version: 4);
        var spoolText = RequiredString(root, "spool_file");
        if (!AuditSpoolSegmentIdentity.TryParse(spoolText, out var spool) ||
            spool.SupervisorBootId != bootId)
        {
            throw new IOException("The operator disposition intent is invalid.");
        }
        var proofKind = RequiredString(root, "proof_kind") switch
        {
            "verified_receipt" => AuditOperatorDispositionProofKind.VerifiedReceipt,
            "acknowledged_gap" => AuditOperatorDispositionProofKind.AcknowledgedGap,
            _ => throw new IOException("The operator disposition intent is invalid."),
        };
        var proof = proofKind == AuditOperatorDispositionProofKind.VerifiedReceipt
            ? AuditOperatorDispositionProof.VerifiedReceipt(
                RequiredNullableString(root, "verified_receipt_digest") ?? string.Empty)
            : AuditOperatorDispositionProof.AcknowledgedGap(
                RequiredNullableString(root, "acknowledged_gap_reason") ?? string.Empty);
        if ((proofKind == AuditOperatorDispositionProofKind.VerifiedReceipt &&
             RequiredNullableString(root, "acknowledged_gap_reason") is not null) ||
            (proofKind == AuditOperatorDispositionProofKind.AcknowledgedGap &&
             RequiredNullableString(root, "verified_receipt_digest") is not null))
        {
            throw new IOException("The operator disposition intent is invalid.");
        }

        return new IntentFields(
            RequiredGuid(root, "disposition_id", version: 4),
            bootId,
            spool,
            RequiredInt64(root, "start_offset"),
            RequiredInt64(root, "next_offset"),
            RequiredInt64(root, "sequence"),
            RequiredGuid(root, "event_id", version: 7),
            ParseFailureClass(RequiredString(root, "failure_class")),
            RequiredString(root, "detail_code"),
            RequiredNullableString(root, "response_digest"),
            RequiredTimestamp(root, "first_failure_utc"),
            RequiredString(root, "export_configuration_identity"),
            proof,
            RequiredTimestamp(root, "created_utc"));
    }

    private void Validate()
    {
        ValidateFields(new IntentFields(
            DispositionId,
            SupervisorBootId,
            Spool,
            StartOffset,
            NextOffset,
            Sequence,
            EventId,
            FailureClass,
            DetailCode,
            ResponseDigest,
            FirstFailureUtc,
            ExportConfigurationIdentity,
            Proof,
            CreatedUtc));
    }

    private static void ValidateFields(IntentFields fields)
    {
        AuditSpoolSegmentIdentity.RequireUuidV4(fields.DispositionId, nameof(fields.DispositionId));
        AuditSpoolSegmentIdentity.RequireUuidV4(fields.SupervisorBootId, nameof(fields.SupervisorBootId));
        _ = new AuditExportBlockedRecord(
            fields.Spool,
            fields.StartOffset,
            fields.Sequence,
            fields.EventId,
            fields.FailureClass,
            fields.DetailCode,
            fields.ResponseDigest,
            fields.FirstFailureUtc,
            fields.ExportConfigurationIdentity);
        if (fields.Spool.SupervisorBootId != fields.SupervisorBootId ||
            fields.StartOffset < 0 ||
            fields.NextOffset <= fields.StartOffset ||
            fields.Sequence < 1 ||
            !IsUuidVersion(fields.EventId, 7) ||
            fields.FailureClass is not (
                AuditExportFailureClass.PartialRejection or
                AuditExportFailureClass.Data or
                AuditExportFailureClass.Protocol) ||
            fields.CreatedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The operator disposition intent target is invalid.");
        }
        ArgumentNullException.ThrowIfNull(fields.Proof);
    }

    internal static string FileName(Guid bootId, Guid eventId) =>
        FilePrefix + bootId.ToString("N") + "-" + eventId.ToString("D") + FileSuffix;

    internal static string TemporaryFileName(Guid bootId, Guid eventId) =>
        TemporaryPrefix + bootId.ToString("N") + "-" + eventId.ToString("D") +
        TemporarySuffix;

    internal static bool TryParseFileName(
        string name,
        out Guid supervisorBootId,
        out Guid eventId)
    {
        supervisorBootId = default;
        eventId = default;
        if (!name.StartsWith(FilePrefix, StringComparison.Ordinal) ||
            !name.EndsWith(FileSuffix, StringComparison.Ordinal))
        {
            return false;
        }
        var stem = name.AsSpan(
            FilePrefix.Length,
            name.Length - FilePrefix.Length - FileSuffix.Length);
        if (stem.Length != 32 + 1 + 36 || stem[32] != '-')
            return false;
        return Guid.TryParseExact(stem[..32], "N", out supervisorBootId) &&
               AuditSpoolSegmentIdentity.IsUuidV4(supervisorBootId) &&
               Guid.TryParseExact(stem[33..], "D", out eventId) &&
               string.Equals(stem[33..].ToString(), eventId.ToString("D"), StringComparison.Ordinal) &&
               IsUuidVersion(eventId, 7);
    }

    internal static bool TryParseTemporaryFileName(
        string name,
        out Guid supervisorBootId,
        out Guid eventId)
    {
        supervisorBootId = default;
        eventId = default;
        if (!name.StartsWith(TemporaryPrefix, StringComparison.Ordinal) ||
            !name.EndsWith(TemporarySuffix, StringComparison.Ordinal))
        {
            return false;
        }
        var stem = name.AsSpan(
            TemporaryPrefix.Length,
            name.Length - TemporaryPrefix.Length - TemporarySuffix.Length);
        if (stem.Length != 32 + 1 + 36 || stem[32] != '-')
            return false;
        return Guid.TryParseExact(stem[..32], "N", out supervisorBootId) &&
               AuditSpoolSegmentIdentity.IsUuidV4(supervisorBootId) &&
               Guid.TryParseExact(stem[33..], "D", out eventId) &&
               string.Equals(stem[33..].ToString(), eventId.ToString("D"), StringComparison.Ordinal) &&
               IsUuidVersion(eventId, 7);
    }

    private static bool EntryExists(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        return file.Exists || file.LinkTarget is not null || Directory.Exists(path);
    }

    private static string RequiredString(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
            throw new IOException("The operator disposition intent is invalid.");
        return value.GetString() ?? throw new IOException("The operator disposition intent is invalid.");
    }

    private static string? RequiredNullableString(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new IOException("The operator disposition intent is invalid."),
        };
    }

    private static long RequiredInt64(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
            throw new IOException("The operator disposition intent is invalid.");
        return result;
    }

    private static Guid RequiredGuid(JsonElement root, string name, int version)
    {
        var text = RequiredString(root, name);
        if (!Guid.TryParseExact(text, "D", out var value) ||
            !string.Equals(text, value.ToString("D"), StringComparison.Ordinal) ||
            !IsUuidVersion(value, version))
        {
            throw new IOException("The operator disposition intent is invalid.");
        }
        return value;
    }

    private static DateTimeOffset RequiredTimestamp(JsonElement root, string name)
    {
        var text = RequiredString(root, name);
        if (!DateTimeOffset.TryParseExact(
                text,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value) ||
            !string.Equals(text, value.ToString(TimestampFormat, CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new IOException("The operator disposition intent is invalid.");
        }
        return value;
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static string FailureClassText(AuditExportFailureClass value) => value switch
    {
        AuditExportFailureClass.PartialRejection => "partial_rejection",
        AuditExportFailureClass.Data => "data",
        AuditExportFailureClass.Protocol => "protocol",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static AuditExportFailureClass ParseFailureClass(string value) => value switch
    {
        "partial_rejection" => AuditExportFailureClass.PartialRejection,
        "data" => AuditExportFailureClass.Data,
        "protocol" => AuditExportFailureClass.Protocol,
        _ => throw new IOException("The operator disposition intent is invalid."),
    };

    private static string ProofKindText(AuditOperatorDispositionProofKind value) => value switch
    {
        AuditOperatorDispositionProofKind.VerifiedReceipt => "verified_receipt",
        AuditOperatorDispositionProofKind.AcknowledgedGap => "acknowledged_gap",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static bool IsUuidVersion(Guid value, int version)
    {
        var text = value.ToString("D");
        return text[14] == (char)('0' + version) && text[19] is '8' or '9' or 'a' or 'b';
    }

    private static string LowerSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record IntentFields(
        Guid DispositionId,
        Guid SupervisorBootId,
        AuditSpoolSegmentIdentity Spool,
        long StartOffset,
        long NextOffset,
        long Sequence,
        Guid EventId,
        AuditExportFailureClass FailureClass,
        string DetailCode,
        string? ResponseDigest,
        DateTimeOffset FirstFailureUtc,
        string ExportConfigurationIdentity,
        AuditOperatorDispositionProof Proof,
        DateTimeOffset CreatedUtc);
}
