using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PtkSiemReceiver.Ingest;

namespace PtkSiemReceiver.Storage;

internal enum SqliteIngestWriteKind
{
    Event,
    Quarantine,
}

internal interface ISqliteIngestFaultInjector
{
    void BeforeCommit(SqliteIngestWriteKind writeKind);
}

internal sealed record SqliteWriterPolicy(string JournalMode, int Synchronous);

internal sealed class SqliteIngestStore : IIngestCommitter, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const int BusyTimeoutSeconds = 5;
    private readonly SqliteConnection _writer;
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly ISqliteIngestFaultInjector? _faultInjector;
    private int _disposed;

    private SqliteIngestStore(
        SqliteConnection writer,
        SqliteWriterPolicy writerPolicy,
        ISqliteIngestFaultInjector? faultInjector)
    {
        _writer = writer;
        WriterPolicy = writerPolicy;
        _faultInjector = faultInjector;
    }

    internal SqliteWriterPolicy WriterPolicy { get; }

    internal static SqliteIngestStore Open(
        string databasePath,
        ISqliteIngestFaultInjector? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        SqliteConnection? connection = null;
        try
        {
            var fullPath = Path.GetFullPath(databasePath);
            var parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                throw new SiemReceiverStartupException("storage_parent");

            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = BusyTimeoutSeconds,
            }.ToString());
            connection.Open();

            var policy = ConfigureAndAssertWriterPolicy(connection);
            ApplyMigrations(connection);
            return new SqliteIngestStore(connection, policy, faultInjector);
        }
        catch (SiemReceiverStartupException)
        {
            connection?.Dispose();
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            connection?.Dispose();
            throw new SiemReceiverStartupException("storage", exception);
        }
    }

    public async Task<IngestCommitResult> CommitAsync(
        ValidatedOtlpRecord record,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateReceipt(receipt);
        ThrowIfDisposed();

        await _writerGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            using var transaction = _writer.BeginTransaction(deferred: false);

            var duplicate = ReadEvent(record.EventId, transaction);
            if (duplicate is not null)
            {
                if (string.Equals(duplicate.EventHash, record.EventHash, StringComparison.Ordinal) &&
                    duplicate.RawRequestBytes.AsSpan().SequenceEqual(record.RawRequestBytes))
                {
                    transaction.Rollback();
                    return IngestCommitResult.Accepted();
                }

                var chain = ReadChain(record.SupervisorBootId, transaction);
                AppendQuarantine(
                    RejectedFrom(record, "duplicate_mismatch"),
                    receipt,
                    chain,
                    transaction);
                cancellationToken.ThrowIfCancellationRequested();
                _faultInjector?.BeforeCommit(SqliteIngestWriteKind.Quarantine);
                transaction.Commit();
                return IngestCommitResult.Permanent("duplicate_mismatch");
            }

            var currentHead = ReadChain(record.SupervisorBootId, transaction);
            var chainFailure = ValidateChainPosition(record, currentHead);
            if (chainFailure is not null)
            {
                AppendQuarantine(
                    RejectedFrom(record, chainFailure),
                    receipt,
                    currentHead,
                    transaction);
                cancellationToken.ThrowIfCancellationRequested();
                _faultInjector?.BeforeCommit(SqliteIngestWriteKind.Quarantine);
                transaction.Commit();
                return IngestCommitResult.Permanent(chainFailure);
            }

            InsertEvent(record, receipt, transaction);
            AdvanceChain(record, currentHead, transaction);
            AppendCustody(
                record.RawRequestBytes,
                receipt,
                "accepted",
                "event",
                FormatGuid(record.EventId),
                transaction);

            cancellationToken.ThrowIfCancellationRequested();
            _faultInjector?.BeforeCommit(SqliteIngestWriteKind.Event);
            transaction.Commit();
            return IngestCommitResult.Accepted();
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public async Task<IngestCommitResult> QuarantineAsync(
        RejectedOtlpAttempt attempt,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ValidateReceipt(receipt);
        ThrowIfDisposed();

        await _writerGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            using var transaction = _writer.BeginTransaction(deferred: false);
            ChainHead? currentHead = null;
            if (Guid.TryParseExact(attempt.ClaimedSupervisorBootId, "D", out var bootId))
                currentHead = ReadChain(bootId, transaction);

            AppendQuarantine(attempt, receipt, currentHead, transaction);
            cancellationToken.ThrowIfCancellationRequested();
            _faultInjector?.BeforeCommit(SqliteIngestWriteKind.Quarantine);
            transaction.Commit();
            return IngestCommitResult.Permanent(attempt.FailureCode);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _writer.Dispose();
        _writerGate.Dispose();
    }

    private static SqliteWriterPolicy ConfigureAndAssertWriterPolicy(SqliteConnection connection)
    {
        var journalMode = Convert.ToString(
            ExecuteScalar(connection, null, "PRAGMA journal_mode=WAL;"),
            CultureInfo.InvariantCulture)?.ToLowerInvariant();
        ExecuteNonQuery(connection, null, "PRAGMA synchronous=FULL;");
        ExecuteNonQuery(connection, null, "PRAGMA foreign_keys=ON;");
        ExecuteNonQuery(
            connection,
            null,
            $"PRAGMA busy_timeout={BusyTimeoutSeconds * 1000};");
        var synchronous = Convert.ToInt32(
            ExecuteScalar(connection, null, "PRAGMA synchronous;"),
            CultureInfo.InvariantCulture);
        var foreignKeys = Convert.ToInt32(
            ExecuteScalar(connection, null, "PRAGMA foreign_keys;"),
            CultureInfo.InvariantCulture);

        if (!string.Equals(journalMode, "wal", StringComparison.Ordinal) ||
            synchronous != 2 ||
            foreignKeys != 1)
        {
            throw new SiemReceiverStartupException("storage_policy");
        }

        return new SqliteWriterPolicy(journalMode!, synchronous);
    }

    private static void ApplyMigrations(SqliteConnection connection)
    {
        var version = Convert.ToInt32(
            ExecuteScalar(connection, null, "PRAGMA user_version;"),
            CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
            throw new SiemReceiverStartupException("storage_schema_newer");

        if (version == 0)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            ExecuteNonQuery(connection, transaction, SchemaVersionOneSql);

            using (var meta = CreateCommand(connection, transaction, """
                INSERT INTO meta(key, value) VALUES
                    ('schema_version', $schema_version),
                    ('receiver_id', $receiver_id);
                """))
            {
                meta.Parameters.AddWithValue("$schema_version", CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
                meta.Parameters.AddWithValue("$receiver_id", Guid.NewGuid().ToString("D"));
                meta.ExecuteNonQuery();
            }

            ExecuteNonQuery(
                connection,
                transaction,
                $"PRAGMA user_version={CurrentSchemaVersion};");
            transaction.Commit();
        }

        var recordedVersion = Convert.ToString(
            ExecuteScalar(
                connection,
                null,
                "SELECT value FROM meta WHERE key = 'schema_version';"),
            CultureInfo.InvariantCulture);
        if (!string.Equals(
                recordedVersion,
                CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new SiemReceiverStartupException("storage_schema");
        }
    }

    private static EventIdentity? ReadEvent(Guid eventId, SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            transaction.Connection!,
            transaction,
            "SELECT event_hash, raw_request FROM events WHERE event_id = $event_id;");
        command.Parameters.AddWithValue("$event_id", FormatGuid(eventId));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new EventIdentity(reader.GetString(0), reader.GetFieldValue<byte[]>(1))
            : null;
    }

    private static ChainHead? ReadChain(Guid supervisorBootId, SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            transaction.Connection!,
            transaction,
            """
            SELECT head_sequence, head_event_id, head_event_hash
            FROM chains
            WHERE supervisor_boot_id = $boot_id;
            """);
        command.Parameters.AddWithValue("$boot_id", FormatGuid(supervisorBootId));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ChainHead(reader.GetInt64(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static string? ValidateChainPosition(
        ValidatedOtlpRecord record,
        ChainHead? currentHead)
    {
        if (currentHead is null)
            return record.Sequence == 1 ? null : "chain_gap";

        if (record.Sequence <= currentHead.Sequence) return "chain_position";
        if (record.Sequence > currentHead.Sequence + 1) return "chain_gap";
        return string.Equals(
            record.PreviousEventHash,
            currentHead.EventHash,
            StringComparison.Ordinal)
            ? null
            : "chain_break";
    }

    private static void InsertEvent(
        ValidatedOtlpRecord record,
        IngestReceiptContext receipt,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(transaction.Connection!, transaction, """
            INSERT INTO events(
                event_id, supervisor_boot_id, sequence, schema_version, event_type,
                occurred_utc, observed_utc, host_id, worker_boot_id,
                previous_event_hash, event_hash, session_name, session_generation,
                call_id, job_id, outcome_state, raw_request, exact_json_body,
                received_utc)
            VALUES(
                $event_id, $boot_id, $sequence, $schema_version, $event_type,
                $occurred_utc, $observed_utc, $host_id, $worker_boot_id,
                $previous_event_hash, $event_hash, $session_name, $session_generation,
                $call_id, $job_id, $outcome_state, $raw_request, $exact_json_body,
                $received_utc);
            """);
        command.Parameters.AddWithValue("$event_id", FormatGuid(record.EventId));
        command.Parameters.AddWithValue("$boot_id", FormatGuid(record.SupervisorBootId));
        command.Parameters.AddWithValue("$sequence", record.Sequence);
        command.Parameters.AddWithValue("$schema_version", record.SchemaVersion);
        command.Parameters.AddWithValue("$event_type", record.EventType);
        command.Parameters.AddWithValue("$occurred_utc", FormatUtc(record.OccurredUtc));
        command.Parameters.AddWithValue("$observed_utc", FormatUtc(record.ObservedUtc));
        command.Parameters.AddWithValue("$host_id", FormatGuid(record.HostId));
        AddNullable(command, "$worker_boot_id", record.WorkerBootId is null ? null : FormatGuid(record.WorkerBootId.Value));
        AddNullable(command, "$previous_event_hash", record.PreviousEventHash);
        command.Parameters.AddWithValue("$event_hash", record.EventHash);
        AddNullable(command, "$session_name", record.SessionName);
        AddNullable(command, "$session_generation", record.SessionGeneration);
        AddNullable(command, "$call_id", record.CallId);
        AddNullable(command, "$job_id", record.JobId);
        AddNullable(command, "$outcome_state", record.OutcomeState);
        command.Parameters.AddWithValue("$raw_request", record.RawRequestBytes);
        command.Parameters.AddWithValue("$exact_json_body", record.ExactJsonBody);
        command.Parameters.AddWithValue("$received_utc", FormatUtc(receipt.ReceivedUtc));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The event insert did not affect exactly one row.");
    }

    private static void AdvanceChain(
        ValidatedOtlpRecord record,
        ChainHead? currentHead,
        SqliteTransaction transaction)
    {
        using var command = currentHead is null
            ? CreateCommand(transaction.Connection!, transaction, """
                INSERT INTO chains(
                    supervisor_boot_id, head_sequence, head_event_id, head_event_hash)
                VALUES($boot_id, $sequence, $event_id, $event_hash);
                """)
            : CreateCommand(transaction.Connection!, transaction, """
                UPDATE chains
                SET head_sequence = $sequence,
                    head_event_id = $event_id,
                    head_event_hash = $event_hash
                WHERE supervisor_boot_id = $boot_id
                  AND head_sequence = $expected_sequence
                  AND head_event_id = $expected_event_id
                  AND head_event_hash = $expected_event_hash;
                """);
        command.Parameters.AddWithValue("$boot_id", FormatGuid(record.SupervisorBootId));
        command.Parameters.AddWithValue("$sequence", record.Sequence);
        command.Parameters.AddWithValue("$event_id", FormatGuid(record.EventId));
        command.Parameters.AddWithValue("$event_hash", record.EventHash);
        if (currentHead is not null)
        {
            command.Parameters.AddWithValue("$expected_sequence", currentHead.Sequence);
            command.Parameters.AddWithValue("$expected_event_id", currentHead.EventId);
            command.Parameters.AddWithValue("$expected_event_hash", currentHead.EventHash);
        }

        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The chain head changed during the serialized ingest transaction.");
    }

    private static void AppendQuarantine(
        RejectedOtlpAttempt attempt,
        IngestReceiptContext receipt,
        ChainHead? currentHead,
        SqliteTransaction transaction)
    {
        using (var command = CreateCommand(transaction.Connection!, transaction, """
            INSERT INTO quarantine(
                failure_code, claimed_event_id, claimed_event_hash,
                claimed_previous_event_hash, claimed_supervisor_boot_id,
                claimed_sequence, observed_head_sequence, observed_head_event_hash,
                raw_request, exact_json_body, received_utc)
            VALUES(
                $failure_code, $claimed_event_id, $claimed_event_hash,
                $claimed_previous_event_hash, $claimed_supervisor_boot_id,
                $claimed_sequence, $observed_head_sequence, $observed_head_event_hash,
                $raw_request, $exact_json_body, $received_utc);
            """))
        {
            command.Parameters.AddWithValue("$failure_code", attempt.FailureCode);
            AddNullable(command, "$claimed_event_id", attempt.ClaimedEventId);
            AddNullable(command, "$claimed_event_hash", attempt.ClaimedEventHash);
            AddNullable(command, "$claimed_previous_event_hash", attempt.ClaimedPreviousEventHash);
            AddNullable(command, "$claimed_supervisor_boot_id", attempt.ClaimedSupervisorBootId);
            AddNullable(command, "$claimed_sequence", attempt.ClaimedSequence);
            AddNullable(command, "$observed_head_sequence", currentHead?.Sequence);
            AddNullable(command, "$observed_head_event_hash", currentHead?.EventHash);
            command.Parameters.AddWithValue("$raw_request", attempt.RawRequestBytes);
            AddNullable(command, "$exact_json_body", attempt.ExactJsonBody);
            command.Parameters.AddWithValue("$received_utc", FormatUtc(receipt.ReceivedUtc));
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The quarantine insert did not affect exactly one row.");
        }

        var attemptId = Convert.ToInt64(
            ExecuteScalar(transaction.Connection!, transaction, "SELECT last_insert_rowid();"),
            CultureInfo.InvariantCulture);
        AppendCustody(
            attempt.RawRequestBytes,
            receipt,
            $"quarantine:{attempt.FailureCode}",
            "quarantine",
            attemptId.ToString(CultureInfo.InvariantCulture),
            transaction);
    }

    private static void AppendCustody(
        byte[] rawRequestBytes,
        IngestReceiptContext receipt,
        string disposition,
        string subjectKind,
        string subjectId,
        SqliteTransaction transaction)
    {
        long previousSequence = 0;
        string? previousHash = null;
        using (var head = CreateCommand(transaction.Connection!, transaction, """
            SELECT receipt_sequence, receipt_hash
            FROM custody
            ORDER BY receipt_sequence DESC
            LIMIT 1;
            """))
        using (var reader = head.ExecuteReader())
        {
            if (reader.Read())
            {
                previousSequence = reader.GetInt64(0);
                previousHash = reader.GetString(1);
            }
        }

        var receiptSequence = checked(previousSequence + 1);
        var receivedUtc = FormatUtc(receipt.ReceivedUtc);
        var receiptHash = CustodyHash.Compute(
            receiptSequence,
            previousHash,
            rawRequestBytes,
            receivedUtc,
            receipt.ClientCertificateThumbprint,
            receipt.RemoteEndpoint,
            disposition,
            subjectKind,
            subjectId);

        using var command = CreateCommand(transaction.Connection!, transaction, """
            INSERT INTO custody(
                receipt_sequence, ledger_version, previous_receipt_hash, receipt_hash,
                received_utc, client_certificate_thumbprint, remote_endpoint,
                disposition, subject_kind, subject_id)
            VALUES(
                $receipt_sequence, 1, $previous_receipt_hash, $receipt_hash,
                $received_utc, $client_certificate_thumbprint, $remote_endpoint,
                $disposition, $subject_kind, $subject_id);
            """);
        command.Parameters.AddWithValue("$receipt_sequence", receiptSequence);
        AddNullable(command, "$previous_receipt_hash", previousHash);
        command.Parameters.AddWithValue("$receipt_hash", receiptHash);
        command.Parameters.AddWithValue("$received_utc", receivedUtc);
        command.Parameters.AddWithValue("$client_certificate_thumbprint", receipt.ClientCertificateThumbprint);
        command.Parameters.AddWithValue("$remote_endpoint", receipt.RemoteEndpoint);
        command.Parameters.AddWithValue("$disposition", disposition);
        command.Parameters.AddWithValue("$subject_kind", subjectKind);
        command.Parameters.AddWithValue("$subject_id", subjectId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The custody insert did not affect exactly one row.");
    }

    private static RejectedOtlpAttempt RejectedFrom(
        ValidatedOtlpRecord record,
        string failureCode) =>
        new(
            record.RawRequestBytes,
            record.ExactJsonBody,
            failureCode,
            FormatGuid(record.EventId),
            record.EventHash,
            record.PreviousEventHash,
            FormatGuid(record.SupervisorBootId),
            record.Sequence);

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        command.CommandTimeout = BusyTimeoutSeconds;
        return command;
    }

    private static object? ExecuteScalar(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        using var command = CreateCommand(connection, transaction, commandText);
        return command.ExecuteScalar();
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        using var command = CreateCommand(connection, transaction, commandText);
        command.ExecuteNonQuery();
    }

    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static void ValidateReceipt(IngestReceiptContext receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.ReceivedUtc.Offset != TimeSpan.Zero ||
            receipt.ClientCertificateThumbprint.Length != 64 ||
            receipt.ClientCertificateThumbprint.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)) ||
            string.IsNullOrWhiteSpace(receipt.RemoteEndpoint))
        {
            throw new ArgumentException("The ingest receipt metadata is invalid.", nameof(receipt));
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static string FormatGuid(Guid value) => value.ToString("D");

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed record EventIdentity(string EventHash, byte[] RawRequestBytes);

    private sealed record ChainHead(long Sequence, string EventId, string EventHash);

    private const string SchemaVersionOneSql = """
        CREATE TABLE meta(
            key TEXT PRIMARY KEY NOT NULL,
            value TEXT NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE events(
            event_id TEXT PRIMARY KEY NOT NULL,
            supervisor_boot_id TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK(sequence >= 1),
            schema_version TEXT NOT NULL,
            event_type TEXT NOT NULL,
            occurred_utc TEXT NOT NULL,
            observed_utc TEXT NOT NULL,
            host_id TEXT NOT NULL,
            worker_boot_id TEXT NULL,
            previous_event_hash TEXT NULL,
            event_hash TEXT NOT NULL,
            session_name TEXT NULL,
            session_generation INTEGER NULL,
            call_id TEXT NULL,
            job_id INTEGER NULL,
            outcome_state TEXT NULL,
            raw_request BLOB NOT NULL,
            exact_json_body BLOB NOT NULL,
            received_utc TEXT NOT NULL,
            UNIQUE(supervisor_boot_id, sequence)
        );

        CREATE INDEX ix_events_occurred_utc
            ON events(occurred_utc);
        CREATE INDEX ix_events_type_occurred
            ON events(event_type, occurred_utc);
        CREATE INDEX ix_events_session_occurred
            ON events(session_name, occurred_utc)
            WHERE session_name IS NOT NULL;

        CREATE TABLE chains(
            supervisor_boot_id TEXT PRIMARY KEY NOT NULL,
            head_sequence INTEGER NOT NULL CHECK(head_sequence >= 1),
            head_event_id TEXT NOT NULL,
            head_event_hash TEXT NOT NULL,
            FOREIGN KEY(head_event_id) REFERENCES events(event_id)
        ) WITHOUT ROWID;

        CREATE TABLE quarantine(
            attempt_id INTEGER PRIMARY KEY AUTOINCREMENT,
            failure_code TEXT NOT NULL,
            claimed_event_id TEXT NULL,
            claimed_event_hash TEXT NULL,
            claimed_previous_event_hash TEXT NULL,
            claimed_supervisor_boot_id TEXT NULL,
            claimed_sequence INTEGER NULL,
            observed_head_sequence INTEGER NULL,
            observed_head_event_hash TEXT NULL,
            raw_request BLOB NOT NULL,
            exact_json_body BLOB NULL,
            received_utc TEXT NOT NULL
        );

        CREATE INDEX ix_quarantine_received
            ON quarantine(received_utc);
        CREATE INDEX ix_quarantine_failure_received
            ON quarantine(failure_code, received_utc);

        CREATE TABLE custody(
            receipt_sequence INTEGER PRIMARY KEY,
            ledger_version INTEGER NOT NULL CHECK(ledger_version = 1),
            previous_receipt_hash TEXT NULL,
            receipt_hash TEXT NOT NULL UNIQUE,
            received_utc TEXT NOT NULL,
            client_certificate_thumbprint TEXT NOT NULL,
            remote_endpoint TEXT NOT NULL,
            disposition TEXT NOT NULL,
            subject_kind TEXT NOT NULL,
            subject_id TEXT NOT NULL
        );
        """;
}

internal static class CustodyHash
{
    private static readonly byte[] Magic = "PTK-SIEM-CUSTODY"u8.ToArray();

    /// <summary>
    /// Version 1 framing is the fixed magic, a big-endian ledger version and
    /// receipt sequence, then eight big-endian-length-prefixed fields in this
    /// exact order: previous hash, raw request, receipt UTC, certificate
    /// thumbprint, remote endpoint, disposition, subject kind, subject ID.
    /// </summary>
    internal static string Compute(
        long receiptSequence,
        string? previousReceiptHash,
        ReadOnlySpan<byte> rawRequestBytes,
        string receivedUtc,
        string certificateThumbprint,
        string remoteEndpoint,
        string disposition,
        string subjectKind,
        string subjectId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Magic);

        Span<byte> integer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt32BigEndian(integer[..sizeof(int)], 1);
        hash.AppendData(integer[..sizeof(int)]);
        BinaryPrimitives.WriteInt64BigEndian(integer, receiptSequence);
        hash.AppendData(integer);

        AppendText(hash, previousReceiptHash ?? string.Empty);
        AppendField(hash, rawRequestBytes);
        AppendText(hash, receivedUtc);
        AppendText(hash, certificateThumbprint);
        AppendText(hash, remoteEndpoint);
        AppendText(hash, disposition);
        AppendText(hash, subjectKind);
        AppendText(hash, subjectId);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendText(IncrementalHash hash, string value) =>
        AppendField(hash, Encoding.UTF8.GetBytes(value));

    private static void AppendField(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
