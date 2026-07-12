using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditOtlpExportCompositionTests
{
    private const string ConfigurationIdentity =
        "abababababababababababababababababababababababababababababababab";

    [Fact]
    public async Task Real_https_503_retries_identical_record_and_checkpoints_only_after_200()
    {
        using var fixture = new ClosedChainFixture();
        using var pki = FakeOtlpPki.Create();
        await using var receiver = new FakeOtlpHttpsReceiver(
            pki,
            [FakeOtlpResponseMode.ServiceUnavailable, FakeOtlpResponseMode.Success]);
        using var exporter = CreateExporter(receiver, pki);
        using var store = fixture.OpenStore();
        using var reader = new AuditClosedSpoolChainReader(fixture.Options, store);
        using var pump = new AuditClosedSpoolExportPump(reader, exporter);

        var retry = await pump.ExportNextAsync(CancellationToken.None);

        Assert.Equal(AuditClosedSpoolExportStepKind.Retry, retry.Kind);
        Assert.Equal(TimeSpan.FromSeconds(1), retry.RetryAfter);
        Assert.Equal(0, store.Current.Sequence);
        Assert.False(store.Current.ChainComplete);
        Assert.Null(store.Current.BlockedRecord);
        Assert.Equal(1, receiver.RequestCount);
        Assert.Empty(receiver.ReadDurableReceipts());

        var acknowledged = await pump.ExportNextAsync(CancellationToken.None);

        Assert.Equal(AuditClosedSpoolExportStepKind.ChainComplete, acknowledged.Kind);
        Assert.Equal(fixture.Record.EventId, acknowledged.EventId);
        Assert.Equal(1, store.Current.Sequence);
        Assert.True(store.Current.ChainComplete);
        var requests = receiver.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].Body, requests[1].Body);
        var receipt = Assert.Single(receiver.ReadDurableReceipts());
        Assert.Equal(fixture.Record.EventId.ToString("D"), receipt.EventId);
        Assert.Equal(fixture.Record.Utf8Line[..^1].ToArray(), receipt.ExactJsonBody);
    }

    [Fact]
    public async Task Durable_receiver_response_loss_replays_an_identical_duplicate_before_checkpoint()
    {
        using var fixture = new ClosedChainFixture();
        using var pki = FakeOtlpPki.Create();
        await using var receiver = new FakeOtlpHttpsReceiver(
            pki,
            [FakeOtlpResponseMode.PersistThenDisconnect, FakeOtlpResponseMode.Success]);
        using var exporter = CreateExporter(receiver, pki);
        using var store = fixture.OpenStore();
        using var reader = new AuditClosedSpoolChainReader(fixture.Options, store);
        using var pump = new AuditClosedSpoolExportPump(reader, exporter);

        var unknown = await pump.ExportNextAsync(CancellationToken.None);

        Assert.Equal(AuditClosedSpoolExportStepKind.Retry, unknown.Kind);
        Assert.Equal("transport.connection", unknown.DetailCode);
        Assert.Equal(0, store.Current.Sequence);
        Assert.False(store.Current.ChainComplete);
        var firstReceipt = Assert.Single(receiver.ReadDurableReceipts());
        Assert.Equal(fixture.Record.EventId.ToString("D"), firstReceipt.EventId);

        var acknowledged = await pump.ExportNextAsync(CancellationToken.None);

        Assert.Equal(AuditClosedSpoolExportStepKind.ChainComplete, acknowledged.Kind);
        Assert.Equal(1, store.Current.Sequence);
        Assert.True(store.Current.ChainComplete);
        var requests = receiver.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].Body, requests[1].Body);
        var receipts = receiver.ReadDurableReceipts();
        Assert.Equal(2, receipts.Count);
        Assert.All(
            receipts,
            receipt => Assert.Equal(fixture.Record.EventId.ToString("D"), receipt.EventId));
        Assert.Equal(receipts[0].ExactJsonBody, receipts[1].ExactJsonBody);
    }

    [Theory]
    [InlineData(
        (int)FakeOtlpResponseMode.PartialRejection,
        (int)AuditExportFailureClass.PartialRejection,
        "otlp.partial_rejection")]
    [InlineData(
        (int)FakeOtlpResponseMode.BadRequest,
        (int)AuditExportFailureClass.Data,
        "http.400")]
    public async Task Real_https_permanent_rejection_is_durably_blocked_across_restart(
        int responseModeValue,
        int failureClassValue,
        string detailCode)
    {
        var responseMode = (FakeOtlpResponseMode)responseModeValue;
        var failureClass = (AuditExportFailureClass)failureClassValue;
        using var fixture = new ClosedChainFixture();
        using var pki = FakeOtlpPki.Create();
        await using var receiver = new FakeOtlpHttpsReceiver(pki, responseMode);

        using (var exporter = CreateExporter(receiver, pki))
        using (var store = fixture.OpenStore())
        using (var reader = new AuditClosedSpoolChainReader(fixture.Options, store))
        using (var pump = new AuditClosedSpoolExportPump(reader, exporter))
        {
            var blocked = await pump.ExportNextAsync(CancellationToken.None);

            Assert.Equal(AuditClosedSpoolExportStepKind.Blocked, blocked.Kind);
            Assert.Equal(failureClass, blocked.FailureClass);
            Assert.Equal(detailCode, blocked.DetailCode);
            Assert.Equal(0, store.Current.Sequence);
            Assert.False(store.Current.ChainComplete);
            var persisted = Assert.IsType<AuditExportBlockedRecord>(
                store.Current.BlockedRecord);
            Assert.Equal(fixture.Record.EventId, persisted.EventId);
            Assert.Equal(failureClass, persisted.FailureClass);
            Assert.Equal(detailCode, persisted.DetailCode);
        }

        Assert.Equal(1, receiver.RequestCount);
        Assert.Empty(receiver.ReadDurableReceipts());

        using (var restartedExporter = CreateExporter(receiver, pki))
        using (var restartedStore = fixture.OpenStore())
        using (var restartedReader = new AuditClosedSpoolChainReader(
                   fixture.Options,
                   restartedStore))
        using (var restartedPump = new AuditClosedSpoolExportPump(
                   restartedReader,
                   restartedExporter))
        {
            var stillBlocked = await restartedPump.ExportNextAsync(
                CancellationToken.None);

            Assert.Equal(AuditClosedSpoolExportStepKind.Blocked, stillBlocked.Kind);
            Assert.Equal(failureClass, stillBlocked.FailureClass);
            Assert.Equal(detailCode, stillBlocked.DetailCode);
            Assert.Equal(0, restartedStore.Current.Sequence);
            Assert.False(restartedStore.Current.ChainComplete);
        }

        Assert.Equal(1, receiver.RequestCount);
    }

    [Fact]
    public async Task Real_https_redirect_pins_checkpoint_and_never_contacts_target()
    {
        using var fixture = new ClosedChainFixture();
        using var pki = FakeOtlpPki.Create();
        await using var target = new FakeOtlpHttpsReceiver(pki);
        await using var receiver = new FakeOtlpHttpsReceiver(
            pki,
            FakeOtlpResponseMode.Redirect,
            target.Endpoint);
        using var exporter = CreateExporter(receiver, pki);
        using var store = fixture.OpenStore();
        using var reader = new AuditClosedSpoolChainReader(fixture.Options, store);
        using var pump = new AuditClosedSpoolExportPump(reader, exporter);

        var blocked = await pump.ExportNextAsync(CancellationToken.None);

        Assert.Equal(AuditClosedSpoolExportStepKind.Blocked, blocked.Kind);
        Assert.Equal(AuditExportFailureClass.Configuration, blocked.FailureClass);
        Assert.Equal("http.307", blocked.DetailCode);
        Assert.Equal(0, store.Current.Sequence);
        Assert.False(store.Current.ChainComplete);
        Assert.NotNull(store.Current.BlockedRecord);
        Assert.Equal(1, receiver.RequestCount);
        Assert.Equal(0, target.RequestCount);
        Assert.Empty(receiver.ReadDurableReceipts());
        Assert.Empty(target.ReadDurableReceipts());
    }

    [Fact]
    public async Task Zero_rejection_warnings_checkpoint_and_create_one_nonrecursive_audit_fact()
    {
        using var fixture = new RuntimeFixture();
        using var pki = FakeOtlpPki.Create();
        await using var receiver = new FakeOtlpHttpsReceiver(
            pki,
            FakeOtlpResponseMode.ZeroRejectionWarning);
        using var exporter = CreateExporter(receiver, pki);
        var health = new AuditHealth(fixture.Options);
        using var runtime = Runtime(fixture.Options, health, exporter);
        Guid supervisorBootId = default;

        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var receipts = await WaitForReceiptsAsync(
                receiver,
                expectedCount: 3,
                TimeSpan.FromSeconds(15));
            var eventTypes = receipts.Select(EventType).ToArray();

            Assert.Equal(
                ["server.started", "export.started", "export.warning"],
                eventTypes);
            Assert.Single(eventTypes, eventType => eventType == "export.warning");
            supervisorBootId = SupervisorBootId(receipts[0]);
            await WaitUntilAsync(
                () => AuditExportCheckpointStore
                    .ReadSnapshot(fixture.Options, supervisorBootId).Sequence == 3,
                TimeSpan.FromSeconds(10));
            Assert.Equal(3, AuditExportCheckpointStore
                .ReadSnapshot(fixture.Options, supervisorBootId).Sequence);
            Assert.True(health.Snapshot().Exporter.HasHealthWarning);
        }
        finally
        {
            await runtime.StopAsync(CancellationToken.None);
        }

        runtime.Dispose();
        Assert.NotEqual(Guid.Empty, supervisorBootId);
        Assert.Equal(3, receiver.RequestCount);
        Assert.Equal(3, receiver.ReadDurableReceipts().Count);
        Assert.Equal(3, AuditExportCheckpointStore
            .ReadSnapshot(fixture.Options, supervisorBootId).Sequence);
        var localEvents = ReadJournalEventTypes(fixture.Options.SpoolDirectory);
        Assert.Equal(
            ["server.started", "export.started", "export.warning", "server.stopped"],
            localEvents);
        Assert.Single(localEvents, eventType => eventType == "export.warning");
    }

    private static AuditRuntimeGate Runtime(
        AuditOptions options,
        AuditHealth health,
        IAuditOtlpExportTransport transport)
    {
        var evidence = new ScriptEvidenceStoreProvider(options);
        return new AuditRuntimeGate(
            options,
            health,
            evidence,
            "export-composition-test",
            openRuntime: () => AuditRuntimeResources.OpenAnchored(
                options,
                health,
                "export-composition-test",
                transport,
                evidence));
    }

    private static AuditOtlpHttpExporter CreateExporter(
        FakeOtlpHttpsReceiver receiver,
        FakeOtlpPki pki)
    {
        var options = new AuditExportOptions(
            receiver.Endpoint.AbsoluteUri,
            receiver.Endpoint,
            [new AuditExportHeader("Authorization", receiver.AuthorizationValue)],
            [pki.CreateTrustedRoot()],
            clientCertificate: null,
            ConfigurationIdentity);
        return AuditOtlpHttpExporter.Create(options, "9.8.7");
    }

    private static async Task<IReadOnlyList<FakeOtlpReceipt>> WaitForReceiptsAsync(
        FakeOtlpHttpsReceiver receiver,
        int expectedCount,
        TimeSpan timeout)
    {
        IReadOnlyList<FakeOtlpReceipt> receipts = [];
        await WaitUntilAsync(
            () =>
            {
                try
                {
                    receipts = receiver.ReadDurableReceipts();
                    return receipts.Count >= expectedCount;
                }
                catch (IOException)
                {
                    return false;
                }
            },
            timeout);
        return receipts;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("The export composition condition did not become true.");
            await Task.Delay(10);
        }
    }

    private static string EventType(FakeOtlpReceipt receipt)
    {
        using var document = JsonDocument.Parse(receipt.ExactJsonBody);
        return document.RootElement.GetProperty("event_type").GetString()!;
    }

    private static Guid SupervisorBootId(FakeOtlpReceipt receipt)
    {
        using var document = JsonDocument.Parse(receipt.ExactJsonBody);
        return Guid.Parse(document.RootElement
            .GetProperty("producer")
            .GetProperty("supervisor_boot_id")
            .GetString()!);
    }

    private static string[] ReadJournalEventTypes(string spoolDirectory) =>
        Directory.GetFiles(spoolDirectory, "ptk-audit-*.jsonl")
            .Order(StringComparer.Ordinal)
            .SelectMany(File.ReadLines)
            .Where(line => line.Length != 0)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("event_type").GetString()!;
            })
            .ToArray();

    private sealed class ClosedChainFixture : IDisposable
    {
        private readonly string _root;

        internal ClosedChainFixture()
        {
            _root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ptk-export-composition-" + Guid.NewGuid().ToString("N"));
            SupervisorBootId = Guid.NewGuid();
            Options = CreateOptions(_root);
            using var store = AuditExportCheckpointStore.CreateForWriter(
                Options,
                SupervisorBootId);
            Record = CreateRecord(SupervisorBootId);
            using var sink = new FileAuditJournalSink(
                Options,
                SupervisorBootId,
                checkpointStore: store);
            sink.Append(Record.Utf8Line);
            sink.FlushToDisk();
        }

        internal AuditOptions Options { get; }

        internal Guid SupervisorBootId { get; }

        internal SerializedAuditEvent Record { get; }

        internal AuditExportCheckpointStore OpenStore()
        {
            Assert.True(AuditExportCheckpointStore.TryAcquireExisting(
                Options,
                SupervisorBootId,
                out var store));
            return Assert.IsType<AuditExportCheckpointStore>(store);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-export-composition-runtime-" + Guid.NewGuid().ToString("N"));

        internal RuntimeFixture()
        {
            Options = CreateOptions(_root);
        }

        internal AuditOptions Options { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static AuditOptions CreateOptions(string root) =>
        AuditOptions.Create(
            root,
            AuditProtectionMode.Anchored,
            ConfigurationIdentity,
            maxRecordBytes: 4096,
            segmentBytes: 65_536,
            aggregateBytes: 131_072,
            emergencyReserveBytes: 8192,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: 4096,
            evidenceAggregateBytes: 16_384,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));

    private static SerializedAuditEvent CreateRecord(Guid supervisorBootId) =>
        AuditEventSerializer.Serialize(
            sequence: 1,
            previousEventHash: null,
            new AuditProducerContext(
                Guid.NewGuid(),
                supervisorBootId,
                null,
                4321,
                "export-composition-test",
                ConfigurationIdentity),
            new AuditEventInput
            {
                EventType = "call.accepted",
                Session = new AuditSession(),
                Actor = new AuditActor
                {
                    AttributionStrength = "transport_only",
                    Transport = "mcp_stdio",
                },
                Correlation = new AuditCorrelation(),
                Request = new AuditRequest(),
                Routing = new AuditRouting(),
                Outcome = new AuditOutcome
                {
                    TerminationCertainty = "not_applicable",
                },
                Coverage = new AuditCoverage
                {
                    PtkRequest = true,
                    RootProcessObserved = "not_applicable",
                    DescendantsObserved = "not_applicable",
                    RemoteEffectObserved = "not_applicable",
                },
                Audit = new AuditEventHealth
                {
                    ProtectionMode = "anchored",
                    ExportConfigurationIdentity = ConfigurationIdentity,
                    HealthState = "healthy",
                },
            },
            eventId: Guid.CreateVersion7(),
            occurredUtc: DateTimeOffset.UtcNow,
            observedUtc: DateTimeOffset.UtcNow);
}
