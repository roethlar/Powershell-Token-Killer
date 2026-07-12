using System.Text.Json;
using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditAnchoredRuntimeTests : IDisposable
{
    private const string ConfigurationIdentity =
        "9999999999999999999999999999999999999999999999999999999999999999";
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ptk-anchored-runtime-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* Preserve the assertion failure that prevented cleanup. */ }
    }

    [Fact]
    public async Task Anchored_runtime_exports_only_after_start_and_restart_adopts_clean_stop()
    {
        var options = AuditOptions.Create(
            _root,
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
        var transport = new AcknowledgingTransport(ConfigurationIdentity);

        var firstHealth = new AuditHealth(options);
        var first = Runtime(options, firstHealth, transport);
        await first.StartAsync(CancellationToken.None);
        Assert.NotEqual(AuditHealthState.Unavailable, firstHealth.Snapshot().State);
        var firstStarted = await transport.WaitForAsync(
            record => record.EventType == "server.started",
            TimeSpan.FromSeconds(10));
        await WaitUntilAsync(
            () => AuditExportCheckpointStore
                .ReadSnapshot(options, firstStarted.SupervisorBootId).Sequence == 1,
            TimeSpan.FromSeconds(10));
        await WaitUntilAsync(
            () => firstHealth.Snapshot().Exporter.LastAcknowledgment?.EventId ==
                  firstStarted.EventId,
            TimeSpan.FromSeconds(10));
        var firstExporter = firstHealth.Snapshot().Exporter;
        Assert.Equal(firstStarted.EventId, firstExporter.LastAcknowledgment?.EventId);
        Assert.Contains(
            firstExporter.State,
            new[] { AuditExporterState.Running, AuditExporterState.Idle });
        Assert.Equal(1, AuditExportCheckpointStore
            .ReadSnapshot(options, firstStarted.SupervisorBootId).Sequence);

        Assert.True(AuditCallMetadataCapture.TryCapture(
            new CallToolRequestParams
            {
                Name = "ptk_invoke",
                Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["script"] = JsonSerializer.SerializeToElement("'runtime evidence'"),
                },
            },
            new AuditClientContext("anchored-runtime-test", "1", "session"),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            DateTimeOffset.UtcNow,
            out var metadata,
            out var exactScript,
            out var captureFailure),
            captureFailure);
        Assert.True(first.TryBeginCall(
            metadata!,
            exactScript,
            out var callContext,
            out var callLease,
            out var beginFailure),
            beginFailure);
        callContext!.CompleteCall("completed", "ok");
        callLease!.Dispose();
        _ = await transport.WaitForAsync(
            record => record.SupervisorBootId == firstStarted.SupervisorBootId &&
                      record.EventType == "call.accepted",
            TimeSpan.FromSeconds(10));
        await WaitUntilAsync(
            () => Directory.GetFiles(options.EvidenceDirectory, "*.anchored.script").Length == 1,
            TimeSpan.FromSeconds(10));
        Assert.Empty(Directory.GetFiles(
            options.EvidenceDirectory,
            "*.anchoring.*.script"));

        await first.StopAsync(CancellationToken.None);
        first.Dispose();
        Assert.DoesNotContain(
            transport.Snapshot(),
            record => record.SupervisorBootId == firstStarted.SupervisorBootId &&
                      record.EventType == "server.stopped");

        var secondHealth = new AuditHealth(options);
        var second = Runtime(options, secondHealth, transport);
        await second.StartAsync(CancellationToken.None);
        var adoptedStop = await transport.WaitForAsync(
            record => record.SupervisorBootId == firstStarted.SupervisorBootId &&
                      record.EventType == "server.stopped",
            TimeSpan.FromSeconds(10));
        Assert.NotEqual(firstStarted.EventId, adoptedStop.EventId);
        await WaitUntilAsync(
            () => AuditExportCheckpointStore
                .ReadSnapshot(options, firstStarted.SupervisorBootId).ChainComplete,
            TimeSpan.FromSeconds(10));

        await second.StopAsync(CancellationToken.None);
        second.Dispose();
        Assert.Single(
            transport.Snapshot(),
            record => record.SupervisorBootId == firstStarted.SupervisorBootId &&
                      record.EventType == "server.started");
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
            "anchored-runtime-test",
            openRuntime: () => AuditRuntimeResources.OpenAnchored(
                options,
                health,
                "anchored-runtime-test",
                transport,
                evidence));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("The anchored runtime condition did not become true.");
            await Task.Delay(10);
        }
    }

    private sealed class AcknowledgingTransport(string configurationIdentity)
        : IAuditOtlpExportTransport
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _arrived = new(0);
        private readonly List<ExportedRecord> _records = [];

        public string ConfigurationIdentity { get; } = configurationIdentity;

        public Task<AuditExportAttemptResult> ExportAsync(
            AuditOtlpRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(record.ExactJsonBody);
            var root = document.RootElement;
            var exported = new ExportedRecord(
                Guid.Parse(root.GetProperty("producer")
                    .GetProperty("supervisor_boot_id").GetString()!),
                root.GetProperty("event_type").GetString()!,
                record.EventId);
            lock (_gate)
                _records.Add(exported);
            _arrived.Release();
            return Task.FromResult(AuditExportAttemptResult.Acknowledged(
                new string('0', 64),
                warning: false));
        }

        internal ExportedRecord[] Snapshot()
        {
            lock (_gate)
                return _records.ToArray();
        }

        internal async Task<ExportedRecord> WaitForAsync(
            Func<ExportedRecord, bool> predicate,
            TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                var match = Snapshot().FirstOrDefault(predicate);
                if (match is not null) return match;
                await _arrived.WaitAsync(cancellation.Token);
            }
        }
    }

    private sealed record ExportedRecord(
        Guid SupervisorBootId,
        string EventType,
        Guid EventId);
}
