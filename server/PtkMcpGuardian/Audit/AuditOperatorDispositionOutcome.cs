using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PtkMcpServer.Audit;

/// <summary>
/// Protected durable receipt proving that one exact operator disposition
/// intent reached a durable completed audit event. Until this receipt exists,
/// the target boot remains ineligible for completed-chain retirement.
/// </summary>
internal sealed class AuditOperatorDispositionOutcome
{
    private const string SchemaVersion = "ptk.operator-disposition-outcome/1";
    internal const string FilePrefix = "operator.disposition-completed-";
    internal const string FileSuffix = ".json";
    internal const string TemporaryPrefix = ".operator.disposition-completed-";
    internal const string TemporarySuffix = ".json.tmp";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private const int MaximumBytes = 8 * 1024;

    private static readonly HashSet<string> Properties = new(
        [
            "schema_version",
            "disposition_id",
            "supervisor_boot_id",
            "blocked_event_id",
            "intent_sha256",
            "completed_audit_supervisor_boot_id",
            "completed_audit_event_id",
            "completed_audit_event_hash",
            "completed_audit_sequence",
            "completed_audit_record_sha256",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> CompletedDispositionProperties = new(
        [
            "disposition_id", "target_supervisor_boot_id", "target_spool_file",
            "target_start_offset", "target_next_offset", "target_sequence",
            "target_event_id", "failure_class", "detail_code", "response_digest",
            "first_failure_utc", "target_export_configuration_identity",
            "proof_kind", "verified_receipt_digest", "acknowledged_gap_reason",
        ],
        StringComparer.Ordinal);

    private AuditOperatorDispositionOutcome(OutcomeFields fields)
    {
        DispositionId = fields.DispositionId;
        SupervisorBootId = fields.SupervisorBootId;
        BlockedEventId = fields.BlockedEventId;
        IntentSha256 = fields.IntentSha256;
        CompletedAuditSupervisorBootId = fields.CompletedAuditSupervisorBootId;
        CompletedAuditEventId = fields.CompletedAuditEventId;
        CompletedAuditEventHash = fields.CompletedAuditEventHash;
        CompletedAuditSequence = fields.CompletedAuditSequence;
        CompletedAuditRecordSha256 = fields.CompletedAuditRecordSha256;
        Validate(fields);
    }

    internal Guid DispositionId { get; }

    internal Guid SupervisorBootId { get; }

    internal Guid BlockedEventId { get; }

    internal string IntentSha256 { get; }

    internal Guid CompletedAuditSupervisorBootId { get; }

    internal Guid CompletedAuditEventId { get; }

    internal string CompletedAuditEventHash { get; }

    internal long CompletedAuditSequence { get; }

    internal string CompletedAuditRecordSha256 { get; }

    internal static AuditOperatorDispositionOutcome Commit(
        AuditOptions options,
        AuditOperatorDispositionIntent intent,
        Guid completedAuditSupervisorBootId,
        SerializedAuditEvent completedAuditEvent,
        Action? afterPublishedForTests = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(intent);
        RequireCompletedAuditEvent(
            intent,
            completedAuditSupervisorBootId,
            completedAuditEvent);
        var existing = TryOpenCommitted(options, intent);
        if (existing is not null)
            return existing;

        var fields = new OutcomeFields(
            intent.DispositionId,
            intent.SupervisorBootId,
            intent.EventId,
            intent.CanonicalSha256,
            completedAuditSupervisorBootId,
            completedAuditEvent.EventId,
            completedAuditEvent.EventHash,
            completedAuditEvent.Sequence,
            LowerSha256(completedAuditEvent.Utf8Line.Span));
        var bytes = Serialize(fields);
        var root = RequireRoot(options);
        var publishedPath = Path.Combine(
            root,
            FileName(intent.SupervisorBootId, intent.EventId));
        var temporaryPath = Path.Combine(
            root,
            TemporaryFileName(intent.SupervisorBootId, intent.EventId));
        if (EntryExists(temporaryPath))
        {
            var recovered = TryOpenCommitted(options, intent);
            if (recovered is not null)
                return recovered;
            throw new AuditOperatorDispositionOutcomeException(
                AuditOperatorDispositionOutcomeFailureKind.Incomplete,
                "A disposition completion publication is already pending.");
        }

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
            SecureAuditStorage.PublishAtomically(temporaryPath, publishedPath, root);
        }
        catch (Exception exception) when (
            AuditJournalFactory.IsConcurrentPublishCollision(exception) &&
            EntryExists(publishedPath))
        {
            var concurrent = TryOpenCommitted(options, intent)
                ?? throw InvalidControl("The disposition completion publication is ambiguous.");
            return concurrent;
        }
        afterPublishedForTests?.Invoke();
        var committed = ReadControl(publishedPath, intent);
        return committed;
    }

    internal static AuditOperatorDispositionOutcome? TryOpenCommitted(
        AuditOptions options,
        AuditOperatorDispositionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(intent);
        var root = RequireRoot(options);
        var publishedPath = Path.Combine(
            root,
            FileName(intent.SupervisorBootId, intent.EventId));
        var temporaryPath = Path.Combine(
            root,
            TemporaryFileName(intent.SupervisorBootId, intent.EventId));
        var publishedExists = EntryExists(publishedPath);
        var temporaryExists = EntryExists(temporaryPath);

        if (publishedExists && temporaryExists)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new AuditOperatorDispositionOutcomeException(
                    AuditOperatorDispositionOutcomeFailureKind.Invalid,
                    "The disposition completion publication has ambiguous aliases.");
            }
            RecoverPublishedAliasControl(
                root,
                publishedPath,
                temporaryPath,
                intent.SupervisorBootId,
                intent.EventId,
                intent);
            temporaryExists = false;
        }
        else if (!publishedExists && temporaryExists)
        {
            var pending = ReadControl(temporaryPath, intent);
            try
            {
                SecureAuditStorage.PublishAtomically(temporaryPath, publishedPath, root);
            }
            catch (Exception exception) when (
                AuditJournalFactory.IsConcurrentPublishCollision(exception) &&
                EntryExists(publishedPath))
            {
                // Another exact retry published the same durable receipt.
            }
            publishedExists = EntryExists(publishedPath);
            temporaryExists = EntryExists(temporaryPath);
            if (publishedExists && temporaryExists)
            {
                if (OperatingSystem.IsWindows())
                {
                    throw new AuditOperatorDispositionOutcomeException(
                        AuditOperatorDispositionOutcomeFailureKind.Invalid,
                        "The disposition completion publication has ambiguous aliases.");
                }
                RecoverPublishedAliasControl(
                    root,
                    publishedPath,
                    temporaryPath,
                    intent.SupervisorBootId,
                    intent.EventId,
                    intent);
                temporaryExists = false;
            }
        }

        if (temporaryExists)
        {
            throw new AuditOperatorDispositionOutcomeException(
                AuditOperatorDispositionOutcomeFailureKind.Incomplete,
                "The disposition completion publication is incomplete.");
        }
        if (!publishedExists)
            return null;
        var committed = ReadControl(publishedPath, intent);
        return committed;
    }

    internal static bool AllowsRetirement(
        AuditOptions options,
        Guid supervisorBootId)
    {
        ArgumentNullException.ThrowIfNull(options);
        AuditSpoolSegmentIdentity.RequireUuidV4(supervisorBootId, nameof(supervisorBootId));
        var intents = AuditOperatorDispositionIntent.OpenForRetirement(options, supervisorBootId);
        var outcomes = InventoryOutcomes(
            options,
            supervisorBootId,
            intents,
            allowOrphans: false);
        foreach (var intent in intents)
        {
            if (!outcomes.TryGetValue(intent.EventId, out var outcome))
                return false;
            RequireCompatible(outcome.Outcome, intent);
        }
        return true;
    }

    internal static void ValidateBoundedInventory(
        AuditOptions options,
        Guid supervisorBootId,
        IReadOnlyList<AuditOperatorDispositionIntent> intents)
    {
        _ = InventoryOutcomes(
            options,
            supervisorBootId,
            intents,
            allowOrphans: false);
    }

    internal static void CleanupRetiredBoot(
        AuditOptions options,
        Guid supervisorBootId,
        Action<int>? afterIntentDeletedForTests = null,
        Action<int>? afterOutcomeDeletedForTests = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        AuditSpoolSegmentIdentity.RequireUuidV4(supervisorBootId, nameof(supervisorBootId));
        var intents = AuditOperatorDispositionIntent.OpenForRetirement(
            options,
            supervisorBootId);
        var outcomes = InventoryOutcomes(
            options,
            supervisorBootId,
            intents,
            allowOrphans: true);
        if (intents.Any(intent => !outcomes.ContainsKey(intent.EventId)))
        {
            throw new IOException(
                "Retirement cleanup found an operator disposition without completion authority.");
        }

        var deletedIntents = 0;
        var deletedOutcomes = 0;
        foreach (var intent in intents.OrderBy(
                     value => value.EventId.ToString("D"),
                     StringComparer.Ordinal))
        {
            var outcome = outcomes[intent.EventId];
            RequireCompatible(outcome.Outcome, intent);
            intent.DeleteForRetirement(options);
            deletedIntents = checked(deletedIntents + 1);
            afterIntentDeletedForTests?.Invoke(deletedIntents);
            DeleteOutcome(options, outcome);
            outcomes.Remove(intent.EventId);
            deletedOutcomes = checked(deletedOutcomes + 1);
            afterOutcomeDeletedForTests?.Invoke(deletedOutcomes);
        }

        // A receipt without its intent is the deliberate crash residue from
        // the intent-first deletion above. The still-present retirement
        // authority is what makes exact deletion of that receipt safe.
        foreach (var outcome in outcomes.Values.OrderBy(
                     value => value.Outcome.BlockedEventId.ToString("D"),
                     StringComparer.Ordinal))
        {
            DeleteOutcome(options, outcome);
            deletedOutcomes = checked(deletedOutcomes + 1);
            afterOutcomeDeletedForTests?.Invoke(deletedOutcomes);
        }

        if (AuditOperatorDispositionIntent.OpenForRetirement(options, supervisorBootId).Count != 0 ||
            InventoryOutcomes(
                options,
                supervisorBootId,
                [],
                allowOrphans: true).Count != 0)
        {
            throw new IOException("Retirement cleanup left operator disposition controls behind.");
        }
    }

    private static Dictionary<Guid, OutcomeControl> InventoryOutcomes(
        AuditOptions options,
        Guid supervisorBootId,
        IReadOnlyList<AuditOperatorDispositionIntent> intents,
        bool allowOrphans)
    {
        var root = RequireRoot(options);
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
                throw new AuditOperatorDispositionOutcomeException(
                    AuditOperatorDispositionOutcomeFailureKind.Invalid,
                    "The audit root contains malformed disposition completion state.");
            }
            Guid bootId;
            Guid eventId;
            if (isPublished
                    ? !TryParseFileName(file.Name, out bootId, out eventId)
                    : !TryParseTemporaryFileName(file.Name, out bootId, out eventId) ||
                      isPublished)
            {
                throw new AuditOperatorDispositionOutcomeException(
                    AuditOperatorDispositionOutcomeFailureKind.Invalid,
                    "The audit root contains malformed disposition completion state.");
            }
            if (bootId != supervisorBootId)
                throw InvalidControl("A disposition completion control names another boot.");
            var controls = isPublished ? published : temporaries;
            if (!controls.TryAdd(eventId, file.FullName))
                throw InvalidControl(
                    "The audit root contains duplicate disposition completion state.");
        }

        var eventIds = published.Keys.Concat(temporaries.Keys).Distinct().ToArray();
        if (eventIds.Length > AuditOperatorDispositionIntent.MaximumDispositionsPerBoot ||
            published.Count + temporaries.Count >
                AuditOperatorDispositionIntent.MaximumDispositionsPerBoot * 2)
        {
            throw new AuditOperatorDispositionOutcomeException(
                AuditOperatorDispositionOutcomeFailureKind.Invalid,
                "The target audit boot exceeds its disposition completion control bound.");
        }
        var intentByEvent = intents.ToDictionary(value => value.EventId);
        if (!allowOrphans && eventIds.Any(eventId => !intentByEvent.ContainsKey(eventId)))
            throw InvalidControl("The audit root contains unmatched disposition completion state.");

        var outcomes = new Dictionary<Guid, OutcomeControl>();
        foreach (var eventId in eventIds.OrderBy(value => value.ToString("D"), StringComparer.Ordinal))
        {
            intentByEvent.TryGetValue(eventId, out var intent);
            var publishedPath = Path.Combine(root, FileName(supervisorBootId, eventId));
            var temporaryPath = Path.Combine(root, TemporaryFileName(supervisorBootId, eventId));
            var hasPublished = published.ContainsKey(eventId);
            var hasTemporary = temporaries.ContainsKey(eventId);
            if (hasPublished && hasTemporary)
            {
                if (OperatingSystem.IsWindows())
                {
                    throw new AuditOperatorDispositionOutcomeException(
                        AuditOperatorDispositionOutcomeFailureKind.Invalid,
                        "The disposition completion publication has ambiguous aliases.");
                }
                RecoverPublishedAliasControl(
                    root,
                    publishedPath,
                    temporaryPath,
                    supervisorBootId,
                    eventId,
                    intent);
            }
            else if (hasTemporary)
            {
                var pending = ReadControl(
                    temporaryPath,
                    intent,
                    supervisorBootId,
                    eventId);
                try
                {
                    SecureAuditStorage.PublishAtomically(temporaryPath, publishedPath, root);
                }
                catch (Exception exception) when (
                    AuditJournalFactory.IsConcurrentPublishCollision(exception) &&
                    EntryExists(publishedPath))
                {
                    if (EntryExists(temporaryPath))
                    {
                        if (OperatingSystem.IsWindows())
                            throw InvalidControl(
                                "The disposition completion publication is ambiguous.");
                        RecoverPublishedAliasControl(
                            root,
                            publishedPath,
                            temporaryPath,
                            supervisorBootId,
                            eventId,
                            intent);
                    }
                }
            }
            var outcome = ReadControl(
                publishedPath,
                intent,
                supervisorBootId,
                eventId);
            outcomes.Add(eventId, new OutcomeControl(publishedPath, outcome));
        }
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        return outcomes;
    }

    private static void RecoverPublishedAlias(
        string root,
        string publishedPath,
        string temporaryPath,
        Guid supervisorBootId,
        Guid eventId,
        AuditOperatorDispositionIntent? intent)
    {
        using var published = OpenPublishedAlias(publishedPath);
        using var temporary = OpenPublishedAlias(temporaryPath);
        if (published.Length is < 2 or > MaximumBytes ||
            published.Length != temporary.Length)
        {
            throw new IOException("The disposition completion alias length is invalid.");
        }
        var publishedBytes = new byte[checked((int)published.Length)];
        var temporaryBytes = new byte[publishedBytes.Length];
        try
        {
            published.ReadExactly(publishedBytes);
            temporary.ReadExactly(temporaryBytes);
            if (!publishedBytes.AsSpan().SequenceEqual(temporaryBytes))
                throw new IOException("The disposition completion aliases differ.");
            var outcome = ParseCanonical(publishedBytes);
            RequireNamedOutcome(outcome, supervisorBootId, eventId);
            if (intent is not null) RequireCompatible(outcome, intent);
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
        Guid eventId,
        AuditOperatorDispositionIntent? intent)
    {
        try
        {
            RecoverPublishedAlias(
                root,
                publishedPath,
                temporaryPath,
                supervisorBootId,
                eventId,
                intent);
        }
        catch (AuditOperatorDispositionOutcomeException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new AuditOperatorDispositionOutcomeException(
                AuditOperatorDispositionOutcomeFailureKind.Invalid,
                "The disposition completion publication is invalid.",
                exception);
        }
    }

    private static void DeleteOutcome(
        AuditOptions options,
        OutcomeControl expected)
    {
        using var stream = new FileStream(
            expected.Path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Delete,
            bufferSize: 1,
            FileOptions.WriteThrough);
        _ = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
            expected.Path,
            stream.SafeFileHandle);
        if (stream.Length is < 2 or > MaximumBytes)
            throw new IOException("The disposition completion receipt changed before retirement.");
        var bytes = new byte[checked((int)stream.Length)];
        try
        {
            stream.ReadExactly(bytes);
            var observed = ParseCanonical(bytes);
            RequireSameOutcome(observed, expected.Outcome);
            SecureAuditStorage.DeleteRetainedProtectedFile(
                options.RootDirectory,
                expected.Path,
                stream.SafeFileHandle);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

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

    private static AuditOperatorDispositionOutcome Read(string path)
    {
        var bytes = SecureAuditStorage.ReadProtectedFile(
            path,
            MaximumBytes,
            requireProtectedParent: true,
            verifyWithoutMutation: true,
            share: FileShare.Read);
        return ParseCanonical(bytes);
    }

    private static AuditOperatorDispositionOutcome ReadControl(
        string path,
        AuditOperatorDispositionIntent intent)
    {
        var outcome = ReadControl(path);
        try
        {
            RequireCompatible(outcome, intent);
            return outcome;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw InvalidControl(
                "The disposition completion receipt belongs to another intent.",
                exception);
        }
    }

    private static AuditOperatorDispositionOutcome ReadControl(
        string path,
        AuditOperatorDispositionIntent? intent,
        Guid supervisorBootId,
        Guid eventId)
    {
        var outcome = ReadControl(path);
        try
        {
            RequireNamedOutcome(outcome, supervisorBootId, eventId);
            if (intent is not null) RequireCompatible(outcome, intent);
            return outcome;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw InvalidControl(
                "The disposition completion receipt is invalid.",
                exception);
        }
    }

    private static AuditOperatorDispositionOutcome ReadControl(string path)
    {
        try
        {
            return Read(path);
        }
        catch (AuditOperatorDispositionOutcomeException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw InvalidControl(
                "The disposition completion receipt is invalid.",
                exception);
        }
    }

    private static AuditOperatorDispositionOutcomeException InvalidControl(
        string message,
        Exception? innerException = null) =>
        new(AuditOperatorDispositionOutcomeFailureKind.Invalid, message, innerException);

    private static AuditOperatorDispositionOutcome ParseCanonical(ReadOnlyMemory<byte> bytes)
    {
        var fields = Parse(bytes);
        var canonical = Serialize(fields);
        if (!bytes.Span.SequenceEqual(canonical))
            throw new IOException("The disposition completion receipt is not canonical.");
        return new AuditOperatorDispositionOutcome(fields);
    }

    private static byte[] Serialize(OutcomeFields fields)
    {
        Validate(fields);
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
            writer.WriteString("blocked_event_id", fields.BlockedEventId.ToString("D"));
            writer.WriteString("intent_sha256", fields.IntentSha256);
            writer.WriteString(
                "completed_audit_supervisor_boot_id",
                fields.CompletedAuditSupervisorBootId.ToString("D"));
            writer.WriteString(
                "completed_audit_event_id",
                fields.CompletedAuditEventId.ToString("D"));
            writer.WriteString("completed_audit_event_hash", fields.CompletedAuditEventHash);
            writer.WriteNumber("completed_audit_sequence", fields.CompletedAuditSequence);
            writer.WriteString("completed_audit_record_sha256", fields.CompletedAuditRecordSha256);
            writer.WriteEndObject();
            writer.Flush();
        }
        if (buffer.WrittenCount >= MaximumBytes)
            throw new IOException("The disposition completion receipt exceeds its bound.");
        var result = new byte[checked(buffer.WrittenCount + 1)];
        buffer.WrittenSpan.CopyTo(result);
        result[^1] = (byte)'\n';
        return result;
    }

    private static OutcomeFields Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            if (bytes.Length is < 2 or > MaximumBytes || bytes.Span[^1] != (byte)'\n')
                throw new FormatException();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException();
            var names = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != Properties.Count ||
                names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                names.Any(name => !Properties.Contains(name)) ||
                !string.Equals(RequiredString(root, "schema_version"), SchemaVersion, StringComparison.Ordinal))
            {
                throw new FormatException();
            }
            var fields = new OutcomeFields(
                RequiredGuid(root, "disposition_id", version: 4),
                RequiredGuid(root, "supervisor_boot_id", version: 4),
                RequiredGuid(root, "blocked_event_id", version: 7),
                RequiredString(root, "intent_sha256"),
                RequiredGuid(root, "completed_audit_supervisor_boot_id", version: 4),
                RequiredGuid(root, "completed_audit_event_id", version: 7),
                RequiredString(root, "completed_audit_event_hash"),
                RequiredInt64(root, "completed_audit_sequence"),
                RequiredString(root, "completed_audit_record_sha256"));
            Validate(fields);
            return fields;
        }
        catch (Exception exception) when (!IsFatal(exception) && exception is not IOException)
        {
            throw new IOException("The disposition completion receipt is invalid.");
        }
    }

    private static void RequireCompatible(
        AuditOperatorDispositionOutcome outcome,
        AuditOperatorDispositionIntent intent)
    {
        if (outcome.DispositionId != intent.DispositionId ||
            outcome.SupervisorBootId != intent.SupervisorBootId ||
            outcome.BlockedEventId != intent.EventId ||
            !string.Equals(outcome.IntentSha256, intent.CanonicalSha256, StringComparison.Ordinal))
        {
            throw new IOException("The disposition completion receipt belongs to another intent.");
        }
    }

    private static void RequireNamedOutcome(
        AuditOperatorDispositionOutcome outcome,
        Guid supervisorBootId,
        Guid eventId)
    {
        if (outcome.SupervisorBootId != supervisorBootId ||
            outcome.BlockedEventId != eventId)
        {
            throw new IOException("A disposition completion receipt names another target.");
        }
    }

    private static void RequireSameOutcome(
        AuditOperatorDispositionOutcome actual,
        AuditOperatorDispositionOutcome expected)
    {
        if (actual.DispositionId != expected.DispositionId ||
            actual.SupervisorBootId != expected.SupervisorBootId ||
            actual.BlockedEventId != expected.BlockedEventId ||
            !string.Equals(actual.IntentSha256, expected.IntentSha256, StringComparison.Ordinal) ||
            actual.CompletedAuditSupervisorBootId != expected.CompletedAuditSupervisorBootId ||
            actual.CompletedAuditEventId != expected.CompletedAuditEventId ||
            !string.Equals(
                actual.CompletedAuditEventHash,
                expected.CompletedAuditEventHash,
                StringComparison.Ordinal) ||
            actual.CompletedAuditSequence != expected.CompletedAuditSequence ||
            !string.Equals(
                actual.CompletedAuditRecordSha256,
                expected.CompletedAuditRecordSha256,
                StringComparison.Ordinal))
        {
            throw new IOException("The disposition completion receipt changed before retirement.");
        }
    }

    private static void RequireCompletedAuditEvent(
        AuditOperatorDispositionIntent intent,
        Guid completedAuditSupervisorBootId,
        SerializedAuditEvent completedAuditEvent)
    {
        AuditSpoolSegmentIdentity.RequireUuidV4(
            completedAuditSupervisorBootId,
            nameof(completedAuditSupervisorBootId));
        if (!IsUuidVersion(completedAuditEvent.EventId, 7) ||
            completedAuditEvent.Sequence < 1 ||
            !IsLowerHex(completedAuditEvent.EventHash, 64))
        {
            throw new ArgumentException("The disposition completion audit event is invalid.");
        }
        try
        {
            using var document = JsonDocument.Parse(completedAuditEvent.Utf8Line);
            var root = document.RootElement;
            var producer = root.GetProperty("producer");
            var session = root.GetProperty("session");
            var correlation = root.GetProperty("correlation");
            var request = root.GetProperty("request");
            var outcome = root.GetProperty("outcome");
            RequireCompletedDispositionFacts(
                root.GetProperty("operator_disposition"),
                intent);
            var detail = RequiredString(outcome, "detail_code");
            if (!string.Equals(
                    RequiredString(root, "event_type"),
                    "export.disposition_completed",
                    StringComparison.Ordinal) ||
                RequiredGuid(root, "event_id", version: 7) != completedAuditEvent.EventId ||
                RequiredGuid(producer, "supervisor_boot_id", version: 4) !=
                    completedAuditSupervisorBootId ||
                RequiredInt64(root, "sequence") != completedAuditEvent.Sequence ||
                !string.Equals(
                    RequiredString(root, "event_hash"),
                    completedAuditEvent.EventHash,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    RequiredString(session, "declared_target"),
                    intent.SupervisorBootId.ToString("D"),
                    StringComparison.Ordinal) ||
                !IsUuidVersion(
                    RequiredGuid(correlation, "parent_event_id", version: 7),
                    7) ||
                !string.Equals(
                    RequiredString(request, "tool"),
                    "audit_admin",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    RequiredString(request, "action"),
                    "disposition",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    RequiredString(outcome, "state"),
                    "completed",
                    StringComparison.Ordinal) ||
                detail is not ("disposition.applied" or "disposition.already_applied"))
            {
                throw new FormatException();
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new ArgumentException(
                "The disposition completion audit event is invalid.",
                nameof(completedAuditEvent),
                exception);
        }
    }

    private static void RequireCompletedDispositionFacts(
        JsonElement facts,
        AuditOperatorDispositionIntent intent)
    {
        if (facts.ValueKind != JsonValueKind.Object)
            throw new FormatException();
        var names = facts.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != CompletedDispositionProperties.Count ||
            names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Any(name => !CompletedDispositionProperties.Contains(name)))
        {
            throw new FormatException();
        }

        var expectedFailureClass = intent.FailureClass switch
        {
            AuditExportFailureClass.PartialRejection => "partial_rejection",
            AuditExportFailureClass.Data => "data",
            AuditExportFailureClass.Protocol => "protocol",
            _ => throw new FormatException(),
        };
        var expectedProofKind = intent.Proof.Kind switch
        {
            AuditOperatorDispositionProofKind.VerifiedReceipt => "verified_receipt",
            AuditOperatorDispositionProofKind.AcknowledgedGap => "acknowledged_gap",
            _ => throw new FormatException(),
        };
        if (RequiredGuid(facts, "disposition_id", version: 4) != intent.DispositionId ||
            RequiredGuid(facts, "target_supervisor_boot_id", version: 4) !=
                intent.SupervisorBootId ||
            !string.Equals(
                RequiredString(facts, "target_spool_file"),
                intent.Spool.FileName,
                StringComparison.Ordinal) ||
            RequiredInt64(facts, "target_start_offset") != intent.StartOffset ||
            RequiredInt64(facts, "target_next_offset") != intent.NextOffset ||
            RequiredInt64(facts, "target_sequence") != intent.Sequence ||
            RequiredGuid(facts, "target_event_id", version: 7) != intent.EventId ||
            !string.Equals(
                RequiredString(facts, "failure_class"),
                expectedFailureClass,
                StringComparison.Ordinal) ||
            !string.Equals(
                RequiredString(facts, "detail_code"),
                intent.DetailCode,
                StringComparison.Ordinal) ||
            !string.Equals(
                RequiredNullableString(facts, "response_digest"),
                intent.ResponseDigest,
                StringComparison.Ordinal) ||
            RequiredTimestamp(facts, "first_failure_utc") != intent.FirstFailureUtc ||
            !string.Equals(
                RequiredString(facts, "target_export_configuration_identity"),
                intent.ExportConfigurationIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                RequiredString(facts, "proof_kind"),
                expectedProofKind,
                StringComparison.Ordinal) ||
            !string.Equals(
                RequiredNullableString(facts, "verified_receipt_digest"),
                intent.Proof.VerifiedReceiptDigest,
                StringComparison.Ordinal) ||
            !string.Equals(
                RequiredNullableString(facts, "acknowledged_gap_reason"),
                intent.Proof.AcknowledgedGapReason,
                StringComparison.Ordinal))
        {
            throw new FormatException();
        }
    }

    private static void Validate(OutcomeFields fields)
    {
        AuditSpoolSegmentIdentity.RequireUuidV4(fields.DispositionId, nameof(fields.DispositionId));
        AuditSpoolSegmentIdentity.RequireUuidV4(fields.SupervisorBootId, nameof(fields.SupervisorBootId));
        AuditSpoolSegmentIdentity.RequireUuidV4(
            fields.CompletedAuditSupervisorBootId,
            nameof(fields.CompletedAuditSupervisorBootId));
        if (!IsUuidVersion(fields.BlockedEventId, 7) ||
            !IsUuidVersion(fields.CompletedAuditEventId, 7) ||
            !IsLowerHex(fields.IntentSha256, 64) ||
            !IsLowerHex(fields.CompletedAuditEventHash, 64) ||
            fields.CompletedAuditSequence < 1 ||
            !IsLowerHex(fields.CompletedAuditRecordSha256, 64))
        {
            throw new IOException("The disposition completion receipt is invalid.");
        }
    }

    private static string RequireRoot(AuditOptions options)
    {
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
            throw new ArgumentException("Disposition completion requires anchored audit mode.", nameof(options));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootDirectory));
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        return root;
    }

    internal static string FileName(Guid bootId, Guid eventId) =>
        FilePrefix + bootId.ToString("N") + "-" + eventId.ToString("D") + FileSuffix;

    internal static string TemporaryFileName(Guid bootId, Guid eventId) =>
        TemporaryPrefix + bootId.ToString("N") + "-" + eventId.ToString("D") + TemporarySuffix;

    internal static bool TryParseFileName(
        string name,
        out Guid supervisorBootId,
        out Guid eventId) => TryParseControlFileName(
        name,
        FilePrefix,
        FileSuffix,
        out supervisorBootId,
        out eventId);

    internal static bool TryParseTemporaryFileName(
        string name,
        out Guid supervisorBootId,
        out Guid eventId) => TryParseControlFileName(
        name,
        TemporaryPrefix,
        TemporarySuffix,
        out supervisorBootId,
        out eventId);

    private static bool TryParseControlFileName(
        string name,
        string prefix,
        string suffix,
        out Guid supervisorBootId,
        out Guid eventId)
    {
        supervisorBootId = default;
        eventId = default;
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }
        var stem = name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length);
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
            throw new FormatException();
        return value.GetString() ?? throw new FormatException();
    }

    private static long RequiredInt64(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
            throw new FormatException();
        return result;
    }

    private static string? RequiredNullableString(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString() ?? throw new FormatException(),
            _ => throw new FormatException(),
        };
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
            !string.Equals(
                text,
                value.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new FormatException();
        }
        return value;
    }

    private static Guid RequiredGuid(JsonElement root, string name, int version)
    {
        var text = RequiredString(root, name);
        if (!Guid.TryParseExact(text, "D", out var value) ||
            !string.Equals(text, value.ToString("D"), StringComparison.Ordinal) ||
            !IsUuidVersion(value, version))
        {
            throw new FormatException();
        }
        return value;
    }

    private static bool IsUuidVersion(Guid value, int version)
    {
        var text = value.ToString("D");
        return text[14] == (char)('0' + version) && text[19] is '8' or '9' or 'a' or 'b';
    }

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string LowerSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;

    private sealed record OutcomeFields(
        Guid DispositionId,
        Guid SupervisorBootId,
        Guid BlockedEventId,
        string IntentSha256,
        Guid CompletedAuditSupervisorBootId,
        Guid CompletedAuditEventId,
        string CompletedAuditEventHash,
        long CompletedAuditSequence,
        string CompletedAuditRecordSha256);

    private sealed record OutcomeControl(
        string Path,
        AuditOperatorDispositionOutcome Outcome);
}
