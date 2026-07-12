using System.Buffers;
using System.Globalization;
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
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private const string FilePrefix = "operator.disposition-";
    private const string FileSuffix = ".json";

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

    internal AuditOperatorDispositionProof Proof { get; }

    internal DateTimeOffset CreatedUtc { get; }

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
            proof,
            (utcNow?.Invoke() ?? DateTimeOffset.UtcNow).ToUniversalTime());
        ValidateFields(expected);

        if (EntryExists(path))
            return RequireCompatible(Read(path), expected, ignoreIdentityAndTime: true);

        var bytes = Serialize(expected);
        var temporaryPath = Path.Combine(
            root,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = SecureAuditStorage.CreateExclusiveFile(
                       temporaryPath,
                       preallocationSize: bytes.Length))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            try
            {
                SecureAuditStorage.PublishAtomically(temporaryPath, path, root);
            }
            catch (Exception exception) when (
                AuditJournalFactory.IsConcurrentPublishCollision(exception) && EntryExists(path))
            {
                SecureAuditStorage.TryDelete(temporaryPath);
                return RequireCompatible(Read(path), expected, ignoreIdentityAndTime: true);
            }
            return RequireCompatible(Read(path), expected, ignoreIdentityAndTime: false);
        }
        finally
        {
            SecureAuditStorage.TryDelete(temporaryPath);
        }
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

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootDirectory));
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        var path = Path.Combine(root, FileName(supervisorBootId, eventId));
        if (!EntryExists(path)) return null;
        var persisted = Read(path);
        if (persisted.SupervisorBootId != supervisorBootId ||
            persisted.EventId != eventId ||
            persisted.Proof != proof)
        {
            throw new IOException("A conflicting operator disposition intent already exists.");
        }
        return persisted;
    }

    internal void ConsumeForCheckpointAdvance(
        Guid supervisorBootId,
        AuditSpoolSegmentIdentity spool,
        long startOffset,
        long nextOffset,
        long sequence,
        Guid eventId,
        AuditExportFailureClass failureClass)
    {
        if (Volatile.Read(ref _consumed) != 0)
            throw new InvalidOperationException("The operator disposition intent was already consumed.");
        RequireExactTarget(
            supervisorBootId,
            spool,
            startOffset,
            nextOffset,
            sequence,
            eventId,
            failureClass);

        var persisted = Read(_path);
        if (!persisted._canonicalBytes.AsSpan().SequenceEqual(_canonicalBytes) ||
            persisted.DispositionId != DispositionId)
        {
            throw new IOException("The durable operator disposition intent changed.");
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

    private void RequireExactTarget(
        Guid supervisorBootId,
        AuditSpoolSegmentIdentity spool,
        long startOffset,
        long nextOffset,
        long sequence,
        Guid eventId,
        AuditExportFailureClass failureClass)
    {
        if (SupervisorBootId != supervisorBootId ||
            Spool != spool ||
            StartOffset != startOffset ||
            NextOffset != nextOffset ||
            Sequence != sequence ||
            EventId != eventId ||
            FailureClass != failureClass)
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
                         actual.Proof == expected.Proof;
        if (!ignoreIdentityAndTime)
        {
            compatible &= actual.DispositionId == expected.DispositionId &&
                          actual.CreatedUtc == expected.CreatedUtc;
        }
        if (!compatible)
            throw new IOException("A conflicting operator disposition intent already exists.");
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
        try
        {
            var fields = Parse(bytes);
            var canonical = Serialize(fields);
            if (!bytes.AsSpan().SequenceEqual(canonical))
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
                fields.Proof,
                fields.CreatedUtc,
                canonical);
        }
        catch (Exception exception) when (exception is not IOException)
        {
            throw new IOException("The operator disposition intent is invalid.");
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
            Proof,
            CreatedUtc));
    }

    private static void ValidateFields(IntentFields fields)
    {
        AuditSpoolSegmentIdentity.RequireUuidV4(fields.DispositionId, nameof(fields.DispositionId));
        AuditSpoolSegmentIdentity.RequireUuidV4(fields.SupervisorBootId, nameof(fields.SupervisorBootId));
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

    private static string FileName(Guid bootId, Guid eventId) =>
        FilePrefix + bootId.ToString("N") + "-" + eventId.ToString("D") + FileSuffix;

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

    private sealed record IntentFields(
        Guid DispositionId,
        Guid SupervisorBootId,
        AuditSpoolSegmentIdentity Spool,
        long StartOffset,
        long NextOffset,
        long Sequence,
        Guid EventId,
        AuditExportFailureClass FailureClass,
        AuditOperatorDispositionProof Proof,
        DateTimeOffset CreatedUtc);
}
