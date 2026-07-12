using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditClosedSpoolExportPumpTests
{
    private static readonly Guid HostId =
        Guid.Parse("22345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid BootId =
        Guid.Parse("12345678-1234-4abc-8def-0123456789ab");
    private static readonly DateTimeOffset BaseTime =
        DateTimeOffset.Parse("2026-07-11T12:34:56.1234567Z");
    private const string ConfigurationIdentity =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ChangedConfigurationIdentity =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string ResponseDigest =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task Acknowledgment_advances_exactly_once_and_completes_after_the_final_record()
    {
        using var fixture = new ClosedFixture(recordCount: 2);
        using var reader = new AuditClosedSpoolChainReader(fixture.Options, fixture.Store);
        var transport = new ScriptedTransport(
            AuditExportAttemptResult.Acknowledged(ResponseDigest, warning: true),
            AuditExportAttemptResult.Acknowledged(ResponseDigest, warning: false));
        using var pump = new AuditClosedSpoolExportPump(
            reader,
            transport);

        var first = await pump.ExportNextAsync(CancellationToken.None);
        Assert.Equal(AuditClosedSpoolExportStepKind.Advanced, first.Kind);
        Assert.Equal(fixture.Records[0].EventId, first.EventId);
        Assert.True(first.HasHealthWarning);
        Assert.Equal(1, fixture.Store.Current.Sequence);
        Assert.False(fixture.Store.Current.ChainComplete);

        var second = await pump.ExportNextAsync(CancellationToken.None);
        Assert.Equal(AuditClosedSpoolExportStepKind.ChainComplete, second.Kind);
        Assert.Equal(fixture.Records[1].EventId, second.EventId);
        Assert.Equal(2, fixture.Store.Current.Sequence);
        Assert.True(fixture.Store.Current.ChainComplete);
        Assert.Equal(
            fixture.Records.Select(record => record.EventId),
            transport.Records.Select(record => record.EventId));

        var alreadyComplete = await pump.ExportNextAsync(CancellationToken.None);
        Assert.Equal(AuditClosedSpoolExportStepKind.ChainComplete, alreadyComplete.Kind);
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task Real_https_receipt_is_durable_before_the_checkpoint_completes()
    {
        using var fixture = new ClosedFixture(recordCount: 1);
        using var pki = FakeOtlpPki.Create();
        await using var receiver = new FakeOtlpHttpsReceiver(
            pki,
            blockReceiptFlush: true);
        var exportOptions = new AuditExportOptions(
            receiver.Endpoint.AbsoluteUri,
            receiver.Endpoint,
            [new AuditExportHeader("Authorization", receiver.AuthorizationValue)],
            [pki.CreateTrustedRoot()],
            clientCertificate: null,
            ConfigurationIdentity);
        using var transport = AuditOtlpHttpExporter.Create(exportOptions, "9.8.7");
        using var reader = new AuditClosedSpoolChainReader(fixture.Options, fixture.Store);
        using var pump = new AuditClosedSpoolExportPump(
            reader,
            transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var export = pump.ExportNextAsync(timeout.Token);
        await receiver.WaitForReceiptFlushPendingAsync(timeout.Token);
        try
        {
            Assert.False(receiver.ResponseStarted);
            Assert.Equal(0, fixture.Store.Current.Sequence);
            Assert.False(fixture.Store.Current.ChainComplete);
        }
        finally
        {
            receiver.ReleaseReceiptFlush();
        }

        var completed = await export;
        Assert.Equal(AuditClosedSpoolExportStepKind.ChainComplete, completed.Kind);
        Assert.Equal(fixture.Records[0].EventId, completed.EventId);
        Assert.True(fixture.Store.Current.ChainComplete);
        var receipt = Assert.Single(receiver.ReadDurableReceipts());
        Assert.Equal(fixture.Records[0].EventId.ToString("D"), receipt.EventId);
        Assert.Equal(
            fixture.Records[0].Utf8Line[..^1].ToArray(),
            receipt.ExactJsonBody);
    }

    [Fact]
    public async Task Empty_closed_chain_completes_from_its_end_proof_without_transport()
    {
        using var fixture = new ClosedFixture(recordCount: 0);
        using var reader = new AuditClosedSpoolChainReader(fixture.Options, fixture.Store);
        var transport = new ScriptedTransport();
        using var pump = new AuditClosedSpoolExportPump(
            reader,
            transport);

        var completed = await pump.ExportNextAsync(CancellationToken.None);

        Assert.Equal(AuditClosedSpoolExportStepKind.ChainComplete, completed.Kind);
        Assert.Null(completed.EventId);
        Assert.Equal(0, transport.Calls);
        Assert.True(fixture.Store.Current.ChainComplete);
    }

    [Fact]
    public void Transport_identity_must_match_the_anchored_reader_configuration()
    {
        using var fixture = new ClosedFixture(recordCount: 1);
        using var reader = new AuditClosedSpoolChainReader(fixture.Options, fixture.Store);
        var transport = new ScriptedTransport(
            AuditExportAttemptResult.Acknowledged(ResponseDigest, warning: false))
        {
            ConfigurationIdentity = ChangedConfigurationIdentity,
        };

        Assert.Throws<ArgumentException>(() =>
            new AuditClosedSpoolExportPump(reader, transport));
        Assert.Equal(0, transport.Calls);
        Assert.Equal(0, fixture.Store.Current.Sequence);
    }

    [Fact]
    public async Task Reader_grants_exactly_one_disposable_export_pump_lease()
    {
        using var fixture = new ClosedFixture(recordCount: 1);
        using var reader = new AuditClosedSpoolChainReader(fixture.Options, fixture.Store);
        var firstTransport = new ScriptedTransport();
        using var first = new AuditClosedSpoolExportPump(reader, firstTransport);

        Assert.Throws<InvalidOperationException>(() =>
            new AuditClosedSpoolExportPump(reader, new ScriptedTransport()));

        first.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            first.ExportNextAsync(CancellationToken.None));
        using var successor = new AuditClosedSpoolExportPump(
            reader,
            new ScriptedTransport());
    }

    [Fact]
    public async Task Pump_disposal_cannot_release_reader_ownership_during_a_remote_attempt()
    {
        using var fixture = new ClosedFixture(recordCount: 1);
        using var reader = new AuditClosedSpoolChainReader(fixture.Options, fixture.Store);
        var transport = new BlockingTransport();
        using var pump = new AuditClosedSpoolExportPump(reader, transport);
        var export = pump.ExportNextAsync(CancellationToken.None);
        await transport.Started.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Throws<InvalidOperationException>(pump.Dispose);

        transport.Release();
        Assert.Equal(
            AuditClosedSpoolExportStepKind.ChainComplete,
            (await export.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        pump.Dispose();
    }

    [Fact]
    public async Task Retry_preserves_the_cursor_and_reuses_the_stable_event_body()
    {
        using var fixture = new ClosedFixture(recordCount: 1);
        using var reader = new AuditClosedSpoolChainReader(fixture.Options, fixture.Store);
        var retryAfter = TimeSpan.FromSeconds(7);
        var transport = new ScriptedTransport(
            AuditExportAttemptResult.Retry("http.503", retryAfter: retryAfter),
            AuditExportAttemptResult.Acknowledged(ResponseDigest, warning: false));
        using var pump = new AuditClosedSpoolExportPump(
            reader,
            transport);

        var retry = await pump.ExportNextAsync(CancellationToken.None);
        Assert.Equal(AuditClosedSpoolExportStepKind.Retry, retry.Kind);
        Assert.Equal(retryAfter, retry.RetryAfter);
        Assert.Equal(0, fixture.Store.Current.Sequence);
        Assert.Null(fixture.Store.Current.BlockedRecord);

        var acknowledged = await pump.ExportNextAsync(CancellationToken.None);
        Assert.Equal(AuditClosedSpoolExportStepKind.ChainComplete, acknowledged.Kind);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(
            transport.Records[0].RequestBytes.ToArray(),
            transport.Records[1].RequestBytes.ToArray());
        Assert.True(fixture.Store.Current.ChainComplete);
    }

    [Fact]
    public async Task Permanent_block_is_persisted_and_suppresses_every_later_transport_attempt()
    {
        using var fixture = new ClosedFixture(recordCount: 1);
        using var reader = new AuditClosedSpoolChainReader(fixture.Options, fixture.Store);
        var transport = new ScriptedTransport(
            AuditExportAttemptResult.Blocked(
                AuditExportFailureClass.Data,
                "http.400",
                ResponseDigest));
        using var pump = new AuditClosedSpoolExportPump(
            reader,
            transport,
            new FixedTimeProvider(BaseTime));

        var blocked = await pump.ExportNextAsync(CancellationToken.None);
        Assert.Equal(AuditClosedSpoolExportStepKind.Blocked, blocked.Kind);
        Assert.Equal(AuditExportFailureClass.Data, blocked.FailureClass);
        var persisted = Assert.IsType<AuditExportBlockedRecord>(
            fixture.Store.Current.BlockedRecord);
        Assert.Equal(BaseTime, persisted.FirstFailureUtc);
        Assert.Equal(ResponseDigest, persisted.ResponseDigest);
        Assert.Equal(0, fixture.Store.Current.Sequence);

        var stillBlocked = await pump.ExportNextAsync(CancellationToken.None);
        Assert.Equal(AuditClosedSpoolExportStepKind.Blocked, stillBlocked.Kind);
        Assert.Equal(1, transport.Calls);

        reader.Dispose();
        using var restartedReader = new AuditClosedSpoolChainReader(
            fixture.Options,
            fixture.Store);
        var restartedTransport = new ScriptedTransport(
            AuditExportAttemptResult.Acknowledged(ResponseDigest, warning: false));
        using var restartedPump = new AuditClosedSpoolExportPump(
            restartedReader,
            restartedTransport);
        var afterRestart = await restartedPump.ExportNextAsync(CancellationToken.None);
        Assert.Equal(AuditClosedSpoolExportStepKind.Blocked, afterRestart.Kind);
        Assert.Equal(0, restartedTransport.Calls);
        Assert.Equal(0, fixture.Store.Current.Sequence);
    }

    [Fact]
    public async Task Checkpoint_failure_after_a_remote_result_faults_before_any_second_request()
    {
        using var fixture = new ClosedFixture(recordCount: 1);
        using var reader = new AuditClosedSpoolChainReader(fixture.Options, fixture.Store);
        var transport = new ScriptedTransport(
            AuditExportAttemptResult.Blocked(
                AuditExportFailureClass.Data,
                "http.400"),
            AuditExportAttemptResult.Blocked(
                AuditExportFailureClass.Data,
                "http.400"))
        {
            BeforeResult = (_, call) =>
            {
                if (call == 1)
                {
                    File.WriteAllText(
                        fixture.Store.CheckpointPath,
                        "{\"invalid\":true}\n");
                }
            },
        };
        using var pump = new AuditClosedSpoolExportPump(reader, transport);

        await Assert.ThrowsAsync<IOException>(() =>
            pump.ExportNextAsync(CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() =>
            pump.ExportNextAsync(CancellationToken.None));

        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Configuration_block_allows_a_new_attempt_only_for_a_changed_identity()
    {
        using var fixture = new ClosedFixture(recordCount: 1);
        using var reader = new AuditClosedSpoolChainReader(fixture.Options, fixture.Store);
        var rejectedTransport = new ScriptedTransport(
            AuditExportAttemptResult.Blocked(
                AuditExportFailureClass.Configuration,
                "http.401"));
        using var initialPump = new AuditClosedSpoolExportPump(
            reader,
            rejectedTransport,
            new FixedTimeProvider(BaseTime));
        Assert.Equal(
            AuditClosedSpoolExportStepKind.Blocked,
            (await initialPump.ExportNextAsync(CancellationToken.None)).Kind);

        reader.Dispose();
        using var sameReader = new AuditClosedSpoolChainReader(fixture.Options, fixture.Store);
        var sameTransport = new ScriptedTransport(
            AuditExportAttemptResult.Acknowledged(ResponseDigest, warning: false));
        using var samePump = new AuditClosedSpoolExportPump(
            sameReader,
            sameTransport);
        Assert.Equal(
            AuditClosedSpoolExportStepKind.Blocked,
            (await samePump.ExportNextAsync(CancellationToken.None)).Kind);
        Assert.Equal(0, sameTransport.Calls);

        sameReader.Dispose();
        var changedOptions = OptionsWithIdentity(
            fixture.Options,
            ChangedConfigurationIdentity);
        using var changedReader = new AuditClosedSpoolChainReader(
            changedOptions,
            fixture.Store);
        var changedTransport = new ScriptedTransport(
            AuditExportAttemptResult.Acknowledged(ResponseDigest, warning: false))
        {
            ConfigurationIdentity = ChangedConfigurationIdentity,
        };
        using var changedPump = new AuditClosedSpoolExportPump(
            changedReader,
            changedTransport);
        Assert.Equal(
            AuditClosedSpoolExportStepKind.ChainComplete,
            (await changedPump.ExportNextAsync(CancellationToken.None)).Kind);
        Assert.Equal(1, changedTransport.Calls);
        Assert.Null(fixture.Store.Current.BlockedRecord);
        Assert.True(fixture.Store.Current.ChainComplete);
    }

    private sealed class ScriptedTransport(
        params AuditExportAttemptResult[] results) : IAuditOtlpExportTransport
    {
        private readonly Queue<AuditExportAttemptResult> _results = new(results);

        internal List<AuditOtlpRecord> Records { get; } = [];

        internal int Calls => Records.Count;

        public string ConfigurationIdentity { get; init; } =
            AuditClosedSpoolExportPumpTests.ConfigurationIdentity;

        internal Action<AuditOtlpRecord, int>? BeforeResult { get; init; }

        public Task<AuditExportAttemptResult> ExportAsync(
            AuditOtlpRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Add(record);
            BeforeResult?.Invoke(record, Records.Count);
            if (!_results.TryDequeue(out var result))
                throw new InvalidOperationException("The scripted audit transport has no result.");
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingTransport : IAuditOtlpExportTransport
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ConfigurationIdentity =>
            AuditClosedSpoolExportPumpTests.ConfigurationIdentity;

        internal Task Started => _started.Task;

        internal void Release() => _release.TrySetResult();

        public async Task<AuditExportAttemptResult> ExportAsync(
            AuditOtlpRecord record,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return AuditExportAttemptResult.Acknowledged(
                ResponseDigest,
                warning: false);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static AuditOptions OptionsWithIdentity(
        AuditOptions source,
        string configurationIdentity) => AuditOptions.Create(
        source.RootDirectory,
        AuditProtectionMode.Anchored,
        configurationIdentity,
        source.MaxRecordBytes,
        source.SegmentBytes,
        source.AggregateBytes,
        source.EmergencyReserveBytes,
        source.RetentionAge,
        source.MaxEvidenceBytes,
        source.EvidenceAggregateBytes,
        source.EvidenceRetentionAge);

    private sealed class ClosedFixture : IDisposable
    {
        private readonly string _root;

        internal ClosedFixture(int recordCount)
        {
            _root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ptk-export-pump-tests-" + Guid.NewGuid().ToString("N"));
            Options = AuditOptions.Create(
                _root,
                AuditProtectionMode.Anchored,
                ConfigurationIdentity,
                maxRecordBytes: 4096,
                segmentBytes: 16_384,
                aggregateBytes: 65_536,
                emergencyReserveBytes: 8192,
                retentionAge: TimeSpan.FromMinutes(10),
                maxEvidenceBytes: 4096,
                evidenceAggregateBytes: 4096,
                evidenceRetentionAge: TimeSpan.FromMinutes(10));
            Store = AuditExportCheckpointStore.CreateForWriter(Options, BootId);
            Records = CreateRecords(recordCount);
            using var sink = new FileAuditJournalSink(
                Options,
                BootId,
                checkpointStore: Store);
            foreach (var record in Records)
            {
                sink.Append(record.Utf8Line);
                sink.FlushToDisk();
            }
        }

        internal AuditOptions Options { get; }

        internal AuditExportCheckpointStore Store { get; }

        internal SerializedAuditEvent[] Records { get; }

        public void Dispose()
        {
            Store.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static SerializedAuditEvent[] CreateRecords(int count)
    {
        var records = new SerializedAuditEvent[count];
        string? previousHash = null;
        for (var index = 0; index < count; index++)
        {
            records[index] = AuditEventSerializer.Serialize(
                index + 1,
                previousHash,
                new AuditProducerContext(
                    HostId,
                    BootId,
                    null,
                    4321,
                    "1.2.3-test",
                    ConfigurationIdentity),
                Input(),
                Guid.CreateVersion7(),
                BaseTime.AddTicks(index),
                BaseTime.AddTicks(index));
            previousHash = records[index].EventHash;
        }
        return records;
    }

    private static AuditEventInput Input() => new()
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
        Outcome = new AuditOutcome { TerminationCertainty = "not_applicable" },
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
    };
}
