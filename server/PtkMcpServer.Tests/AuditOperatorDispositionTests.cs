using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditOperatorDispositionTests : IDisposable
{
    private const string ConfigurationIdentity =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ptk-operator-disposition-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Exact_permanent_blocks_advance_only_after_durable_intent()
    {
        foreach (var failureClass in new[]
                 {
                     AuditExportFailureClass.PartialRejection,
                     AuditExportFailureClass.Data,
                     AuditExportFailureClass.Protocol,
                 })
        {
            var options = Options();
            var target = CreateBlockedTarget(options, failureClass);
            using var admin = AdminJournal(options);
            var operations = new AuditAdminOperations(options, admin.Journal);
            var intentObserved = false;

            var dispositionId = operations.ApplyPermanentBlockDisposition(
                target.BootId,
                target.EventId,
                AuditOperatorDispositionProof.VerifiedReceipt(new string('b', 64)),
                afterDurableIntentForTests: () =>
                {
                    intentObserved = true;
                    Assert.NotEmpty(Directory.GetFiles(
                        options.RootDirectory,
                        "operator.disposition-*.json"));
                    Assert.NotNull(Checkpoint(options, target.BootId).BlockedRecord);
                });

            Assert.True(intentObserved);
            Assert.True(AuditSpoolSegmentIdentity.IsUuidV4(dispositionId));
            var checkpoint = Checkpoint(options, target.BootId);
            Assert.Equal(1, checkpoint.Sequence);
            Assert.Equal(target.EventId, checkpoint.AcknowledgedEventId);
            Assert.Null(checkpoint.BlockedRecord);
            Assert.Equal(
                ["export.disposition_intent", "export.disposition_completed"],
                admin.Sink.Lines.Select(EventType).ToArray());
            using var intentEvent = JsonDocument.Parse(admin.Sink.Lines[0]);
            Assert.Equal(
                target.EventId.ToString("D"),
                intentEvent.RootElement.GetProperty("correlation")
                    .GetProperty("parent_event_id").GetString());
        }
    }

    [Fact]
    public void Crash_after_intent_reuses_exact_intent_and_advances_once()
    {
        var options = Options();
        var target = CreateBlockedTarget(options, AuditExportFailureClass.Data);
        var proof = AuditOperatorDispositionProof.AcknowledgedGap("collector.data_loss_accepted");
        Guid persistedId;
        using (var firstAdmin = AdminJournal(options))
        {
            var first = new AuditAdminOperations(options, firstAdmin.Journal);
            Assert.Throws<AuditAdminOperationException>(() =>
                first.ApplyPermanentBlockDisposition(
                    target.BootId,
                    target.EventId,
                    proof,
                    afterDurableIntentForTests: () => throw new IOException("crash seam")));
            Assert.NotNull(Checkpoint(options, target.BootId).BlockedRecord);
            persistedId = ReadDispositionId(options);
            Assert.Equal(
                ["export.disposition_intent", "export.disposition_failed"],
                firstAdmin.Sink.Lines.Select(EventType).ToArray());
        }

        using var secondAdmin = AdminJournal(options);
        var second = new AuditAdminOperations(options, secondAdmin.Journal);
        var resumedId = second.ApplyPermanentBlockDisposition(
            target.BootId,
            target.EventId,
            proof);

        Assert.Equal(persistedId, resumedId);
        Assert.Null(Checkpoint(options, target.BootId).BlockedRecord);
    }

    [Fact]
    public void Crash_after_checkpoint_is_idempotently_completed_on_restart()
    {
        var options = Options();
        var target = CreateBlockedTarget(options, AuditExportFailureClass.Protocol);
        var proof = AuditOperatorDispositionProof.VerifiedReceipt(new string('c', 64));
        using (var firstAdmin = AdminJournal(options))
        {
            var first = new AuditAdminOperations(options, firstAdmin.Journal);
            Assert.Throws<AuditAdminOperationException>(() =>
                first.ApplyPermanentBlockDisposition(
                    target.BootId,
                    target.EventId,
                    proof,
                    afterCheckpointAdvanceForTests: () => throw new IOException("crash seam")));
            Assert.Null(Checkpoint(options, target.BootId).BlockedRecord);
        }
        var persistedId = ReadDispositionId(options);

        using var secondAdmin = AdminJournal(options);
        var second = new AuditAdminOperations(options, secondAdmin.Journal);
        var resumedId = second.ApplyPermanentBlockDisposition(
            target.BootId,
            target.EventId,
            proof);

        Assert.Equal(persistedId, resumedId);
        Assert.Null(Checkpoint(options, target.BootId).BlockedRecord);
        Assert.Equal(
            ["export.disposition_intent", "export.disposition_completed"],
            secondAdmin.Sink.Lines.Select(EventType).ToArray());
        using var completed = JsonDocument.Parse(secondAdmin.Sink.Lines[1]);
        Assert.Equal(
            "disposition.already_applied",
            completed.RootElement.GetProperty("outcome").GetProperty("detail_code").GetString());
    }

    [Fact]
    public void Conflicting_restart_proof_cannot_advance_the_block()
    {
        var options = Options();
        var target = CreateBlockedTarget(options, AuditExportFailureClass.Data);
        using (var firstAdmin = AdminJournal(options))
        {
            var first = new AuditAdminOperations(options, firstAdmin.Journal);
            Assert.Throws<AuditAdminOperationException>(() =>
                first.ApplyPermanentBlockDisposition(
                    target.BootId,
                    target.EventId,
                    AuditOperatorDispositionProof.VerifiedReceipt(new string('d', 64)),
                    afterDurableIntentForTests: () => throw new IOException("crash seam")));
        }

        using var secondAdmin = AdminJournal(options);
        var second = new AuditAdminOperations(options, secondAdmin.Journal);
        Assert.Throws<AuditAdminOperationException>(() =>
            second.ApplyPermanentBlockDisposition(
                target.BootId,
                target.EventId,
                AuditOperatorDispositionProof.AcknowledgedGap("different.reason")));

        Assert.NotNull(Checkpoint(options, target.BootId).BlockedRecord);
        Assert.Equal(
            ["export.disposition_intent", "export.disposition_failed"],
            secondAdmin.Sink.Lines.Select(EventType).ToArray());
    }

    [Fact]
    public void Tampered_durable_intent_cannot_advance_the_block()
    {
        var options = Options();
        var target = CreateBlockedTarget(options, AuditExportFailureClass.Data);
        using var admin = AdminJournal(options);
        var operations = new AuditAdminOperations(options, admin.Journal);

        Assert.Throws<AuditAdminOperationException>(() =>
            operations.ApplyPermanentBlockDisposition(
                target.BootId,
                target.EventId,
                AuditOperatorDispositionProof.VerifiedReceipt(new string('e', 64)),
                afterDurableIntentForTests: () =>
                {
                    var path = Assert.Single(Directory.GetFiles(
                        options.RootDirectory,
                        "operator.disposition-*.json"));
                    var bytes = File.ReadAllBytes(path);
                    bytes[20] ^= 1;
                    File.WriteAllBytes(path, bytes);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(path, SecureAuditStorage.OwnerFileMode);
                }));

        Assert.NotNull(Checkpoint(options, target.BootId).BlockedRecord);
        Assert.Equal(
            ["export.disposition_intent", "export.disposition_failed"],
            admin.Sink.Lines.Select(EventType).ToArray());
    }

    [Theory]
    [InlineData(BlockedField.DetailCode)]
    [InlineData(BlockedField.ResponseDigest)]
    [InlineData(BlockedField.FirstFailureUtc)]
    [InlineData(BlockedField.ExportConfigurationIdentity)]
    public void Durable_intent_rejects_each_mutated_frozen_block_field(BlockedField field)
    {
        var options = Options();
        var target = CreateBlockedTarget(
            options,
            AuditExportFailureClass.Data,
            responseDigest: new string('a', 64));
        var blocked = Assert.IsType<AuditExportBlockedRecord>(
            Checkpoint(options, target.BootId).BlockedRecord);
        var position = new IntentPosition(
            blocked.Spool,
            blocked.ByteOffset,
            checked(blocked.ByteOffset + 1),
            blocked.Sequence,
            blocked.EventId);
        var intent = AuditOperatorDispositionIntent.CreateOrOpen(
            options,
            position,
            blocked,
            AuditOperatorDispositionProof.AcknowledgedGap("operator.accepted"));

        var path = Assert.Single(Directory.GetFiles(
            options.RootDirectory,
            "operator.disposition-*.json"));
        using (var document = JsonDocument.Parse(File.ReadAllBytes(path)))
        {
            var root = document.RootElement;
            Assert.Equal(blocked.DetailCode, root.GetProperty("detail_code").GetString());
            Assert.Equal(blocked.ResponseDigest, root.GetProperty("response_digest").GetString());
            Assert.Equal(
                blocked.FirstFailureUtc.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture),
                root.GetProperty("first_failure_utc").GetString());
            Assert.Equal(
                blocked.ExportConfigurationIdentity,
                root.GetProperty("export_configuration_identity").GetString());
        }

        Assert.Throws<ArgumentException>(() =>
            intent.ConsumeForCheckpointAdvance(
                target.BootId,
                position.NextOffset,
                Mutate(blocked, field)));
        Assert.NotNull(Checkpoint(options, target.BootId).BlockedRecord);
    }

    [Fact]
    public void Configuration_block_and_live_checkpoint_lease_both_fail_closed()
    {
        var options = Options();
        var configuration = CreateBlockedTarget(options, AuditExportFailureClass.Configuration);
        using (var admin = AdminJournal(options))
        {
            var operations = new AuditAdminOperations(options, admin.Journal);
            Assert.Throws<AuditAdminOperationException>(() =>
                operations.ApplyPermanentBlockDisposition(
                    configuration.BootId,
                    configuration.EventId,
                    AuditOperatorDispositionProof.AcknowledgedGap("not.allowed")));
            Assert.NotNull(Checkpoint(options, configuration.BootId).BlockedRecord);
        }

        var permanent = CreateBlockedTarget(options, AuditExportFailureClass.Data);
        Assert.True(AuditExportCheckpointStore.TryAcquireExisting(
            options,
            permanent.BootId,
            out var liveStore));
        using (liveStore)
        using (var admin = AdminJournal(options))
        {
            var operations = new AuditAdminOperations(options, admin.Journal);
            Assert.Throws<AuditAdminOperationException>(() =>
                operations.ApplyPermanentBlockDisposition(
                    permanent.BootId,
                    permanent.EventId,
                    AuditOperatorDispositionProof.AcknowledgedGap("operator.accepted")));
            Assert.Equal(
                ["export.disposition_intent", "export.disposition_failed"],
                admin.Sink.Lines.Select(EventType).ToArray());
        }
    }

    private BlockedTarget CreateBlockedTarget(
        AuditOptions options,
        AuditExportFailureClass failureClass,
        string? responseDigest = null)
    {
        var bootId = Guid.NewGuid();
        using (var preparation = FileAuditJournalSink.PrepareAnchored(options, bootId))
        using (var checkpointStore = preparation.CreateCheckpointStore())
        {
            var sink = preparation.Activate(checkpointStore);
            using var journal = AuditJournalFactory.OpenActivatedAnchored(
                options,
                new AuditHealth(options),
                "test",
                sink);
            var written = journal.AppendAutomaticTransition(TargetEvent(options));
            bootId = journal.SupervisorBootId;
            var eventId = written.EventId;
            journal.Dispose();
            checkpointStore.Dispose();

            Assert.True(AuditExportCheckpointStore.TryAcquireExisting(
                options,
                bootId,
                out var adopted));
            using (adopted)
            using (var reader = new AuditClosedSpoolChainReader(options, adopted!))
            {
                var recovery = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                    reader.ResolveCheckpoint());
                reader.PersistBlock(
                    recovery.Position,
                    failureClass,
                    "test.block",
                    responseDigest,
                    DateTimeOffset.UtcNow,
                    ConfigurationIdentity);
            }
            return new BlockedTarget(bootId, eventId);
        }
    }

    private AuditOptions Options() => AuditOptions.Create(
        _root,
        AuditProtectionMode.Anchored,
        ConfigurationIdentity);

    private static AdminJournalFixture AdminJournal(AuditOptions options)
    {
        var sink = new InMemoryAuditJournalSink(
            options.SegmentBytes,
            options.AggregateBytes,
            options.ProtectionMode,
            options.RetentionAge);
        var journal = new AuditJournal(
            options,
            new AuditHealth(options),
            sink,
            "test",
            binaryDigest: null,
            Guid.NewGuid(),
            Guid.NewGuid());
        return new AdminJournalFixture(journal, sink);
    }

    private static AuditEventInput TargetEvent(AuditOptions options) => new()
    {
        EventType = "test.export_target",
        Session = new AuditSession { DeclaredPurpose = "test" },
        Actor = new AuditActor { AttributionStrength = "unknown" },
        Correlation = new AuditCorrelation(),
        Request = new AuditRequest { Tool = "audit_test", Action = "target" },
        Routing = new AuditRouting(),
        Outcome = new AuditOutcome
        {
            State = "completed",
            DetailCode = "test.complete",
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
            ProtectionMode = "anchored",
            ExportConfigurationIdentity = options.ExportConfigurationIdentity,
            HealthState = "healthy",
        },
    };

    private static AuditExportCheckpoint Checkpoint(AuditOptions options, Guid bootId) =>
        AuditExportCheckpointStore.ReadSnapshot(options, bootId);

    private static Guid ReadDispositionId(AuditOptions options)
    {
        var path = Assert.Single(Directory.GetFiles(
            options.RootDirectory,
            "operator.disposition-*.json"));
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return Guid.ParseExact(
            document.RootElement.GetProperty("disposition_id").GetString()!,
            "D");
    }

    private static string EventType(byte[] line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("event_type").GetString()!;
    }

    private static AuditExportBlockedRecord Mutate(
        AuditExportBlockedRecord blocked,
        BlockedField field) => new(
        blocked.Spool,
        blocked.ByteOffset,
        blocked.Sequence,
        blocked.EventId,
        blocked.FailureClass,
        field == BlockedField.DetailCode ? "test.changed" : blocked.DetailCode,
        field == BlockedField.ResponseDigest ? new string('f', 64) : blocked.ResponseDigest,
        field == BlockedField.FirstFailureUtc
            ? blocked.FirstFailureUtc.AddTicks(1)
            : blocked.FirstFailureUtc,
        field == BlockedField.ExportConfigurationIdentity
            ? new string('f', 64)
            : blocked.ExportConfigurationIdentity);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record BlockedTarget(Guid BootId, Guid EventId);

    public enum BlockedField
    {
        DetailCode,
        ResponseDigest,
        FirstFailureUtc,
        ExportConfigurationIdentity,
    }

    private sealed record IntentPosition(
        AuditSpoolSegmentIdentity Spool,
        long StartOffset,
        long NextOffset,
        long Sequence,
        Guid EventId) : IAuditClosedSpoolRecordPosition
    {
        public string? PreviousEventHash => null;

        public string EventHash => new('0', 64);

        public ReadOnlyMemory<byte> ExactJsonlBytes => ReadOnlyMemory<byte>.Empty;
    }

    private sealed record AdminJournalFixture(
        AuditJournal Journal,
        InMemoryAuditJournalSink Sink) : IDisposable
    {
        public void Dispose() => Journal.Dispose();
    }
}
