using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PtkSiemReceiver.Ingest;
using PtkSiemReceiver.Security;
using PtkSiemReceiver.Storage;
using PtkMcpServer.Audit.OtlpWire;

namespace PtkSiemReceiver.Tests;

public sealed class SqliteIngestStoreTests
{
    private static readonly IngestReceiptContext Receipt = new(
        new DateTimeOffset(2026, 7, 15, 16, 30, 45, TimeSpan.Zero).AddTicks(1234567),
        new string('a', 64),
        "127.0.0.1:4318");

    [Fact]
    public void Custody_v1_deterministic_framing_has_frozen_digest()
    {
        Assert.Equal(
            "72f8734275e69561219341294a6c909cbf324e25f49ddda585fd7696aa37dc07",
            CustodyHash.Compute(
                7,
                string.Concat(Enumerable.Repeat("ab", 32)),
                [0, 1, 255],
                "2026-07-15T16:30:45.1234567Z",
                string.Concat(Enumerable.Repeat("cd", 32)),
                "[::1]:4318",
                "quarantine:attributes",
                "quarantine",
                "42"));
    }

    [Fact]
    public void Open_migrates_once_and_asserts_wal_full_on_each_writer()
    {
        using var database = new TestDatabase();
        string receiverId;
        using (var store = SqliteIngestStore.Open(database.Path))
        {
            Assert.Equal("wal", store.WriterPolicy.JournalMode);
            Assert.Equal(2, store.WriterPolicy.Synchronous);
            AssertExactProtection(database.Root, isDirectory: true);
            AssertExactProtection(database.Path, isDirectory: false);
            AssertExactProtection(database.Path + "-wal", isDirectory: false);
            AssertExactProtection(database.Path + "-shm", isDirectory: false);
            Assert.Equal(1L, Scalar<long>(database.Path, "PRAGMA user_version;"));
            Assert.Equal("wal", Scalar<string>(database.Path, "PRAGMA journal_mode;"));
            receiverId = Scalar<string>(
                database.Path,
                "SELECT value FROM meta WHERE key = 'receiver_id';");
            Assert.True(Guid.TryParseExact(receiverId, "D", out _));
        }

        using var reopened = SqliteIngestStore.Open(database.Path);
        Assert.Equal("wal", reopened.WriterPolicy.JournalMode);
        Assert.Equal(2, reopened.WriterPolicy.Synchronous);
        Assert.Equal(
            receiverId,
            Scalar<string>(database.Path, "SELECT value FROM meta WHERE key = 'receiver_id';"));
    }

    [Theory]
    [InlineData("database")]
    [InlineData("wal")]
    [InlineData("shm")]
    public void Insecure_preexisting_storage_artifact_fails_untouched(string role)
    {
        using var database = new TestDatabase();
        _ = SiemProtectedPath.CreateProtectedFile(database.Path);
        var path = role switch
        {
            "database" => database.Path,
            "wal" => database.Path + "-wal",
            "shm" => database.Path + "-shm",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        if (role != "database")
            _ = SiemTestFileSystem.WriteProtectedBytes(database.Root, Path.GetFileName(path), [1, 2, 3]);
        else
            File.WriteAllBytes(path, [1, 2, 3]);
        Broaden(path, isDirectory: false);
        var beforeBytes = File.ReadAllBytes(path);
        var beforeProtection = ProtectionSnapshot(path, isDirectory: false);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            SqliteIngestStore.Open(database.Path));

        Assert.Equal("storage_protection", exception.FailureCode);
        Assert.Equal(beforeBytes, File.ReadAllBytes(path));
        Assert.Equal(beforeProtection, ProtectionSnapshot(path, isDirectory: false));
        _ = SiemProtectedPath.ProtectCreatedFile(path);
    }

    [Fact]
    public void Insecure_data_directory_fails_without_creating_database()
    {
        using var database = new TestDatabase();
        Broaden(database.Root, isDirectory: true);
        var before = ProtectionSnapshot(database.Root, isDirectory: true);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            SqliteIngestStore.Open(database.Path));

        Assert.Equal("storage_protection", exception.FailureCode);
        Assert.Equal(before, ProtectionSnapshot(database.Root, isDirectory: true));
        Assert.False(File.Exists(database.Path));
        _ = SiemProtectedPath.ProtectCreatedDirectory(database.Root);
    }

    [Fact]
    public void Missing_data_directory_fails_without_creating_it()
    {
        using var database = new TestDatabase();
        var missingParent = Path.Combine(database.Root, "missing-parent");
        var path = Path.Combine(missingParent, "siem.db");

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            SqliteIngestStore.Open(path));

        Assert.Equal("storage_protection", exception.FailureCode);
        Assert.False(Directory.Exists(missingParent));
    }

    [Theory]
    [InlineData("database")]
    [InlineData("wal")]
    [InlineData("shm")]
    public void Wrong_kind_storage_artifact_is_rejected(string role)
    {
        using var database = new TestDatabase();
        if (role != "database")
            _ = SiemProtectedPath.CreateProtectedFile(database.Path);
        var path = role switch
        {
            "database" => database.Path,
            "wal" => database.Path + "-wal",
            "shm" => database.Path + "-shm",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        Directory.CreateDirectory(path);
        _ = SiemProtectedPath.ProtectCreatedDirectory(path);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            SqliteIngestStore.Open(database.Path));

        Assert.Equal("storage_protection", exception.FailureCode);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("database")]
    [InlineData("wal")]
    [InlineData("shm")]
    public void Every_storage_role_has_an_independent_wrong_owner_guard(string role)
    {
        using var database = new TestDatabase();
        if (role != "parent")
            _ = SiemProtectedPath.CreateProtectedFile(database.Path);
        var target = role switch
        {
            "parent" => database.Root,
            "database" => database.Path,
            "wal" => SiemTestFileSystem.WriteProtectedBytes(
                database.Root,
                Path.GetFileName(database.Path + "-wal"),
                []),
            "shm" => SiemTestFileSystem.WriteProtectedBytes(
                database.Root,
                Path.GetFileName(database.Path + "-shm"),
                []),
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        var hooks = WrongOwnerHooks(target, directory: role == "parent");

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            SqliteIngestStore.Open(
                database.Path,
                protectedPathTestHooks: hooks));

        Assert.Equal("storage_protection", exception.FailureCode);
    }

    [Theory]
    [InlineData("wal")]
    [InlineData("shm")]
    public void Orphan_sidecar_fails_before_database_creation(string role)
    {
        using var database = new TestDatabase();
        var sidecar = database.Path + (role == "wal" ? "-wal" : "-shm");
        _ = SiemTestFileSystem.WriteProtectedBytes(
            database.Root,
            Path.GetFileName(sidecar),
            [1, 2, 3]);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            SqliteIngestStore.Open(database.Path));

        Assert.Equal("storage_orphan_sidecar", exception.FailureCode);
        Assert.False(File.Exists(database.Path));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(sidecar));
    }

    [Theory]
    [InlineData("database")]
    [InlineData("wal")]
    [InlineData("shm")]
    public void Linked_storage_artifact_is_rejected(string role)
    {
        using var database = new TestDatabase();
        if (role != "database")
            _ = SiemProtectedPath.CreateProtectedFile(database.Path);
        var path = role switch
        {
            "database" => database.Path,
            "wal" => database.Path + "-wal",
            "shm" => database.Path + "-shm",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        var target = SiemTestFileSystem.WriteProtectedBytes(
            database.Root,
            "target-" + role,
            []);
        File.CreateSymbolicLink(path, target);

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            SqliteIngestStore.Open(database.Path));

        Assert.Equal("storage_protection", exception.FailureCode);
        Assert.Empty(File.ReadAllBytes(target));
    }

    [Fact]
    public void Ancestor_redirect_is_rejected_without_writing_through_it()
    {
        using var target = new TestDatabase();
        var linkRoot = SiemTestFileSystem.CreateProtectedRoot("ptk-siem-store-link");
        try
        {
            var redirect = Path.Combine(linkRoot, "redirect");
            Directory.CreateSymbolicLink(redirect, target.Root);
            var candidate = Path.Combine(redirect, "redirected.db");

            var exception = Assert.Throws<SiemReceiverStartupException>(() =>
                SqliteIngestStore.Open(candidate));

            Assert.Equal("storage_protection", exception.FailureCode);
            Assert.False(File.Exists(Path.Combine(target.Root, "redirected.db")));
        }
        finally
        {
            Directory.Delete(linkRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("wal")]
    [InlineData("shm")]
    public void Post_wal_protection_sabotage_fails_startup(string role)
    {
        using var database = new TestDatabase();

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            SqliteIngestStore.Open(
                database.Path,
                new StartupProtectionSabotage(role, replaceIdentity: false)));

        Assert.Equal("storage_protection", exception.FailureCode);
    }

    [Theory]
    [InlineData("wal")]
    [InlineData("shm")]
    public void Posix_post_wal_identity_replacement_fails_startup(string role)
    {
        if (OperatingSystem.IsWindows()) return;

        using var database = new TestDatabase();

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            SqliteIngestStore.Open(
                database.Path,
                new StartupProtectionSabotage(role, replaceIdentity: true)));

        Assert.Equal("storage_protection", exception.FailureCode);
    }

    [Fact]
    public void Posix_database_aba_swap_cannot_hide_the_live_sqlite_identity()
    {
        if (OperatingSystem.IsWindows()) return;

        using var database = new TestDatabase();

        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            SqliteIngestStore.Open(
                database.Path,
                new StartupDatabaseAbaSabotage()));

        Assert.Equal("storage_protection", exception.FailureCode);
    }

    [Fact]
    public void Relative_database_path_is_rejected()
    {
        var exception = Assert.Throws<SiemReceiverStartupException>(() =>
            SqliteIngestStore.Open("relative.db"));

        Assert.Equal("storage_path", exception.FailureCode);
    }

    [Fact]
    public async Task Commit_atomically_persists_raw_event_chain_and_custody()
    {
        using var database = new TestDatabase();
        using var store = SqliteIngestStore.Open(database.Path);
        var request = OtlpTestRequest.Create();
        var record = Validate(request);

        var result = await store.CommitAsync(record, Receipt, CancellationToken.None);

        Assert.Equal(IngestCommitResultKind.Accepted, result.Kind);
        Assert.Equal(request.ToByteArray(), Bytes(database.Path, "SELECT raw_request FROM events;"));
        Assert.Equal(record.ExactJsonBody, Bytes(database.Path, "SELECT exact_json_body FROM events;"));
        Assert.Equal(record.EventHash, Scalar<string>(database.Path, "SELECT event_hash FROM events;"));
        Assert.Equal(1L, Scalar<long>(database.Path, "SELECT head_sequence FROM chains;"));
        Assert.Equal("accepted", Scalar<string>(database.Path, "SELECT disposition FROM custody;"));
        Assert.Equal(1L, Scalar<long>(database.Path, "SELECT receipt_sequence FROM custody;"));
        Assert.Equal(DBNull.Value, Scalar(database.Path, "SELECT previous_receipt_hash FROM custody;"));

        var receiptHash = Scalar<string>(database.Path, "SELECT receipt_hash FROM custody;");
        Assert.Equal(
            CustodyHash.Compute(
                1,
                null,
                record.RawRequestBytes,
                "2026-07-15T16:30:45.1234567Z",
                Receipt.ClientCertificateThumbprint,
                Receipt.RemoteEndpoint,
                "accepted",
                "event",
                record.EventId.ToString("D")),
            receiptHash);
    }

    [Fact]
    public async Task Identical_replay_after_head_advance_is_idempotent_without_second_receipt()
    {
        using var database = new TestDatabase();
        using var store = SqliteIngestStore.Open(database.Path);
        var first = Validate(OtlpTestRequest.Create());
        var second = Validate(OtlpTestRequest.Create(
            eventId: "018f6a78-4c20-7a11-8a34-1234567890ac",
            sequence: 2,
            previousEventHash: first.EventHash));

        Assert.Equal(IngestCommitResultKind.Accepted, (await store.CommitAsync(first, Receipt, default)).Kind);
        Assert.Equal(IngestCommitResultKind.Accepted, (await store.CommitAsync(second, Receipt, default)).Kind);
        Assert.Equal(IngestCommitResultKind.Accepted, (await store.CommitAsync(first, Receipt, default)).Kind);

        Assert.Equal(2L, Count(database.Path, "events"));
        Assert.Equal(2L, Count(database.Path, "custody"));
        Assert.Equal(0L, Count(database.Path, "quarantine"));
        Assert.Equal(2L, Scalar<long>(database.Path, "SELECT head_sequence FROM chains;"));
    }

    [Fact]
    public async Task Same_event_id_with_different_bytes_is_durably_quarantined()
    {
        using var database = new TestDatabase();
        using var store = SqliteIngestStore.Open(database.Path);
        var first = Validate(OtlpTestRequest.Create());
        var mismatch = Validate(OtlpTestRequest.Create(eventType: "tool.rejected"));

        await store.CommitAsync(first, Receipt, default);
        var result = await store.CommitAsync(mismatch, Receipt, default);

        Assert.Equal(IngestCommitResultKind.PermanentFailure, result.Kind);
        Assert.Equal("duplicate_mismatch", result.FailureCode);
        Assert.Equal(1L, Count(database.Path, "events"));
        Assert.Equal(1L, Count(database.Path, "quarantine"));
        Assert.Equal(2L, Count(database.Path, "custody"));
        Assert.Equal(
            "quarantine:duplicate_mismatch",
            Scalar<string>(database.Path, "SELECT disposition FROM custody WHERE receipt_sequence = 2;"));
        Assert.Equal(
            Scalar<string>(database.Path, "SELECT receipt_hash FROM custody WHERE receipt_sequence = 1;"),
            Scalar<string>(database.Path, "SELECT previous_receipt_hash FROM custody WHERE receipt_sequence = 2;"));
        Assert.Equal(mismatch.RawRequestBytes, Bytes(database.Path, "SELECT raw_request FROM quarantine;"));
        Assert.Equal(1L, Scalar<long>(database.Path, "SELECT head_sequence FROM chains;"));
    }

    [Fact]
    public async Task Simultaneous_fork_candidates_commit_exactly_one_and_quarantine_the_loser()
    {
        using var database = new TestDatabase();
        using var barrier = new BlockingBeforeCommitFault();
        using var store = SqliteIngestStore.Open(database.Path, barrier);
        var first = Validate(OtlpTestRequest.Create());
        await store.CommitAsync(first, Receipt, default);
        var candidateA = Validate(OtlpTestRequest.Create(
            eventId: "018f6a78-4c20-7a11-8a34-1234567890ac",
            sequence: 2,
            previousEventHash: first.EventHash));
        var candidateB = Validate(OtlpTestRequest.Create(
            eventId: "018f6a78-4c20-7a11-8a34-1234567890ad",
            sequence: 2,
            previousEventHash: first.EventHash,
            eventType: "tool.rejected"));

        barrier.Arm();
        var candidateATask = Task.Run(() => store.CommitAsync(candidateA, Receipt, default));
        Assert.True(barrier.Entered.Wait(TimeSpan.FromSeconds(5)));
        var candidateBTask = Task.Run(() => store.CommitAsync(candidateB, Receipt, default));
        Assert.False(candidateBTask.IsCompleted);
        barrier.Release.Set();
        var results = await Task.WhenAll(candidateATask, candidateBTask);

        Assert.Single(results, result => result.Kind == IngestCommitResultKind.Accepted);
        var loser = Assert.Single(results, result => result.Kind == IngestCommitResultKind.PermanentFailure);
        Assert.Equal("chain_position", loser.FailureCode);
        Assert.Equal(2L, Count(database.Path, "events"));
        Assert.Equal(1L, Count(database.Path, "quarantine"));
        Assert.Equal(3L, Count(database.Path, "custody"));
        Assert.Equal(2L, Scalar<long>(database.Path, "SELECT head_sequence FROM chains;"));
    }

    [Fact]
    public async Task Strict_validator_rejection_is_stored_as_attempt_evidence_and_custody()
    {
        using var database = new TestDatabase();
        using var store = SqliteIngestStore.Open(database.Path);
        var request = OtlpTestRequest.Create();
        request.ResourceLogs[0].ScopeLogs[0].LogRecords[0].Attributes
            .Single(attribute => attribute.Key == "ptk.audit.event_type")
            .Value.StringValue = "tool.rejected";
        var bytes = request.ToByteArray();
        var validation = OtlpRequestValidator.Validate(bytes);
        var rejected = Assert.IsType<RejectedOtlpAttempt>(validation.RejectedAttempt);

        var result = await store.QuarantineAsync(rejected, Receipt, default);

        Assert.Equal(IngestCommitResultKind.PermanentFailure, result.Kind);
        Assert.Equal("attributes", result.FailureCode);
        Assert.Equal(bytes, Bytes(database.Path, "SELECT raw_request FROM quarantine;"));
        Assert.Equal(OtlpTestRequest.DefaultEventId, Scalar<string>(database.Path, "SELECT claimed_event_id FROM quarantine;"));
        Assert.Equal("quarantine:attributes", Scalar<string>(database.Path, "SELECT disposition FROM custody;"));
        Assert.Equal(0L, Count(database.Path, "events"));
    }

    [Fact]
    public async Task Interrupted_event_transaction_leaves_no_partial_rows_after_reopen()
    {
        using var database = new TestDatabase();
        var fault = new ThrowBeforeCommitFault(SqliteIngestWriteKind.Event, new IOException("interrupted"));
        using (var store = SqliteIngestStore.Open(database.Path, fault))
        {
            var record = Validate(OtlpTestRequest.Create());
            await Assert.ThrowsAsync<IOException>(
                () => store.CommitAsync(record, Receipt, default));
        }

        using var reopened = SqliteIngestStore.Open(database.Path);
        Assert.Equal(0L, Count(database.Path, "events"));
        Assert.Equal(0L, Count(database.Path, "chains"));
        Assert.Equal(0L, Count(database.Path, "custody"));
    }

    [Fact]
    public async Task Sqlite_full_before_commit_leaves_no_rows_for_a_false_ack()
    {
        using var database = new TestDatabase();
        var full = new SqliteException("database or disk is full", 13, 13);
        using var store = SqliteIngestStore.Open(
            database.Path,
            new ThrowBeforeCommitFault(SqliteIngestWriteKind.Event, full));
        var record = Validate(OtlpTestRequest.Create());

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => store.CommitAsync(record, Receipt, default));

        Assert.Equal(13, exception.SqliteErrorCode);
        Assert.Equal(0L, Count(database.Path, "events"));
        Assert.Equal(0L, Count(database.Path, "chains"));
        Assert.Equal(0L, Count(database.Path, "custody"));
    }

    [Fact]
    public async Task Interrupted_quarantine_transaction_cannot_emit_durable_rejection_evidence()
    {
        using var database = new TestDatabase();
        using var store = SqliteIngestStore.Open(
            database.Path,
            new ThrowBeforeCommitFault(SqliteIngestWriteKind.Quarantine, new IOException("interrupted")));
        var request = OtlpTestRequest.Create();
        request.ResourceLogs.Clear();
        var validation = OtlpRequestValidator.Validate(request.ToByteArray());

        await Assert.ThrowsAsync<IOException>(
            () => store.QuarantineAsync(validation.RejectedAttempt!, Receipt, default));

        Assert.Equal(0L, Count(database.Path, "quarantine"));
        Assert.Equal(0L, Count(database.Path, "custody"));
    }

    [Fact]
    public async Task Unique_boot_sequence_constraint_backstops_fork_prevention()
    {
        using var database = new TestDatabase();
        using (var store = SqliteIngestStore.Open(database.Path))
        {
            await store.CommitAsync(Validate(OtlpTestRequest.Create()), Receipt, default);
        }

        using var connection = Open(database.Path, readOnly: false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO events(
                event_id, supervisor_boot_id, sequence, schema_version, event_type,
                occurred_utc, observed_utc, host_id, worker_boot_id,
                previous_event_hash, event_hash, session_name, session_generation,
                call_id, job_id, outcome_state, raw_request, exact_json_body,
                received_utc)
            SELECT
                '018f6a78-4c20-7a11-8a34-1234567890ac', supervisor_boot_id, sequence,
                schema_version, event_type, occurred_utc, observed_utc, host_id,
                worker_boot_id, previous_event_hash, event_hash, session_name,
                session_generation, call_id, job_id, outcome_state, raw_request,
                exact_json_body, received_utc
            FROM events
            LIMIT 1;
            """;

        var exception = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Equal(1L, Count(database.Path, "events"));
    }

    private static ValidatedOtlpRecord Validate(ExportLogsServiceRequest request)
    {
        var validation = OtlpRequestValidator.Validate(request.ToByteArray());
        Assert.Null(validation.FailureCode);
        return Assert.IsType<ValidatedOtlpRecord>(validation.Record);
    }

    private static long Count(string path, string table) =>
        Scalar<long>(path, $"SELECT COUNT(*) FROM {table};");

    private static byte[] Bytes(string path, string sql) =>
        Assert.IsType<byte[]>(Scalar(path, sql));

    private static T Scalar<T>(string path, string sql) =>
        (T)Convert.ChangeType(Scalar(path, sql), typeof(T), CultureInfo.InvariantCulture);

    private static object Scalar(string path, string sql)
    {
        using var connection = Open(path, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() ?? DBNull.Value;
    }

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class ThrowBeforeCommitFault(
        SqliteIngestWriteKind target,
        Exception exception) : ISqliteIngestFaultInjector
    {
        public void BeforeCommit(SqliteIngestWriteKind writeKind)
        {
            if (writeKind == target) throw exception;
        }
    }

    private sealed class BlockingBeforeCommitFault : ISqliteIngestFaultInjector, IDisposable
    {
        private int _armed;

        internal ManualResetEventSlim Entered { get; } = new(false);

        internal ManualResetEventSlim Release { get; } = new(false);

        internal void Arm() => Volatile.Write(ref _armed, 1);

        public void BeforeCommit(SqliteIngestWriteKind writeKind)
        {
            if (writeKind != SqliteIngestWriteKind.Event || Volatile.Read(ref _armed) == 0)
                return;

            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The fork-test barrier was not released.");
        }

        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private sealed class StartupProtectionSabotage(
        string role,
        bool replaceIdentity) : ISqliteIngestFaultInjector
    {
        public void BeforeCommit(SqliteIngestWriteKind writeKind)
        {
        }

        public void AfterStartupProtectionForTests(string databasePath)
        {
            var path = databasePath + (role == "wal" ? "-wal" : "-shm");
            if (!replaceIdentity)
            {
                Broaden(path, isDirectory: false);
                return;
            }

            File.Move(path, path + ".displaced");
            _ = SiemProtectedPath.CreateProtectedFile(path);
        }
    }

    private sealed class StartupDatabaseAbaSabotage : ISqliteIngestFaultInjector
    {
        private string? _expectedDatabase;
        private string? _attackerDatabase;

        public void BeforeCommit(SqliteIngestWriteKind writeKind)
        {
        }

        public void BeforeConnectionOpenForTests(string databasePath)
        {
            _expectedDatabase = databasePath + ".expected";
            _attackerDatabase = databasePath + ".attacker";
            File.Move(databasePath, _expectedDatabase);
            _ = SiemProtectedPath.CreateProtectedFile(databasePath);
        }

        public void AfterConnectionOpenForTests(string databasePath)
        {
            File.Move(databasePath, _attackerDatabase!);
            File.Move(_expectedDatabase!, databasePath);
        }
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string _root =
            SiemTestFileSystem.CreateProtectedRoot("ptk-siem-store");

        internal TestDatabase()
        {
            Path = System.IO.Path.Combine(_root, "siem.db");
        }

        internal string Path { get; }

        internal string Root => _root;

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private static void Broaden(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                isDirectory
                    ? SiemProtectedPath.OwnerDirectoryMode |
                      UnixFileMode.GroupRead |
                      UnixFileMode.GroupExecute
                    : SiemProtectedPath.OwnerFileMode | UnixFileMode.GroupRead);
            return;
        }

        AddWindowsWorldRead(path, isDirectory);
    }

    private static ProtectedPathTestHooks WrongOwnerHooks(string target, bool directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            var effective = UnixProtectedPathNative.EffectiveUserId;
            var different = effective == uint.MaxValue ? effective - 1 : effective + 1;
            return new ProtectedPathTestHooks(
                ExpectedUnixUserIdForPath: (candidate, candidateIsDirectory) =>
                    candidateIsDirectory == directory && PathsEqual(candidate, target)
                        ? different
                        : null);
        }

        var foreignSid = SiemTestFileSystem.ForeignWindowsOwnerSid();
        return new ProtectedPathTestHooks(
            ExpectedWindowsOwnerSidForPath: (candidate, candidateIsDirectory) =>
                candidateIsDirectory == directory && PathsEqual(candidate, target)
                    ? foreignSid
                    : null);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string ProtectionSnapshot(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return File.GetUnixFileMode(path).ToString();
        return SnapshotWindowsAcl(path, isDirectory);
    }

    private static void AssertExactProtection(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                isDirectory
                    ? SiemProtectedPath.OwnerDirectoryMode
                    : SiemProtectedPath.OwnerFileMode,
                File.GetUnixFileMode(path));
            Assert.Equal(
                UnixProtectedPathNative.EffectiveUserId,
                UnixProtectedPathNative.GetPathMetadata(path).UserId);
            return;
        }

        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(
            identity.User,
            Assert.IsType<SecurityIdentifier>(security.GetOwner(typeof(SecurityIdentifier))));
        Assert.True(security.AreAccessRulesProtected);
        var rule = Assert.Single(
            security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>());
        Assert.Equal(identity.User, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.False(rule.IsInherited);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsWorldRead(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read,
            AccessControlType.Allow));
        if (isDirectory)
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), (DirectorySecurity)security);
        else
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), (FileSecurity)security);
    }

    [SupportedOSPlatform("windows")]
    private static string SnapshotWindowsAcl(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        return security.GetSecurityDescriptorSddlForm(
            AccessControlSections.Owner | AccessControlSections.Access);
    }
}
