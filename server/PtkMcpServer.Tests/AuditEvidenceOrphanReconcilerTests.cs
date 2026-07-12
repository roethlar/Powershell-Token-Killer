using System.Text.Json;
using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditEvidenceOrphanReconcilerTests : IDisposable
{
    private const string ConfigurationIdentity =
        "abababababababababababababababababababababababababababababababab";
    private readonly List<string> _roots = [];

    [Fact]
    public void Failed_append_with_no_written_bytes_is_proved_unreferenced()
    {
        using var fixture = CreateFixture(wrapSink: sink => new FailFirstAppendSink(sink));
        var context = new AuditCallContext(fixture.Journal, fixture.Evidence);

        Assert.False(context.TryBegin(
            CaptureInvokeMetadata(),
            "never written",
            out var failure));

        Assert.Equal("journal.persistence", failure);
        Assert.Empty(AwaitingPaths(fixture.Options));
        Assert.Single(Directory.GetFiles(
            fixture.Options.EvidenceDirectory,
            "*.unreferenced.script"));
    }

    [Fact]
    public void Append_failure_after_durable_flush_finds_reference_and_keeps_evidence_pinned()
    {
        using var fixture = CreateFixture(
            sinkFault: (point, attempt) =>
                point == FileAuditSinkFaultPoint.AfterPhysicalFlush && attempt == 1);
        var context = new AuditCallContext(fixture.Journal, fixture.Evidence);

        Assert.False(context.TryBegin(
            CaptureInvokeMetadata(),
            "durable despite reported failure",
            out var failure));

        Assert.Equal("journal.persistence", failure);
        Assert.Single(AwaitingPaths(fixture.Options));
        Assert.Empty(Directory.GetFiles(
            fixture.Options.EvidenceDirectory,
            "*.unreferenced.script"));
    }

    [Fact]
    public void Referenced_evidence_in_rotated_segment_remains_pinned()
    {
        using var fixture = CreateFixture();
        using (var publication = fixture.Evidence.Publish("rotated reference"))
        {
            AppendRecord(fixture, publication.Reference);
            publication.CompleteAfterAuditAppend();
        }

        for (var attempt = 0;
             attempt < 64 && SegmentCount(fixture.Options) < 2;
             attempt++)
        {
            AppendRecord(fixture, evidence: null);
        }
        Assert.True(SegmentCount(fixture.Options) >= 2, "test did not force rotation");

        var reconciler = new AuditEvidenceOrphanReconciler(
            fixture.Journal,
            fixture.Evidence);
        Assert.True(reconciler.ReconcileNow());

        Assert.Single(AwaitingPaths(fixture.Options));
        Assert.Empty(Directory.GetFiles(
            fixture.Options.EvidenceDirectory,
            "*.unreferenced.script"));
    }

    [Fact]
    public async Task Publication_quota_blocks_reconciliation_until_durable_reference_is_visible()
    {
        using var fixture = CreateFixture();
        using var publication = fixture.Evidence.Publish("concurrent reference");
        var reconciler = new AuditEvidenceOrphanReconciler(
            fixture.Journal,
            fixture.Evidence,
            interval: TimeSpan.FromMilliseconds(1));
        var reconcile = Task.Run(reconciler.ReconcileNow);

        await Task.Delay(150);
        Assert.False(reconcile.IsCompleted, "reconciliation bypassed publication quota");

        AppendRecord(fixture, publication.Reference);
        publication.CompleteAfterAuditAppend();

        Assert.True(await reconcile.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Single(AwaitingPaths(fixture.Options));
        Assert.Empty(Directory.GetFiles(
            fixture.Options.EvidenceDirectory,
            "*.unreferenced.script"));
    }

    [Fact]
    public void Unknown_spool_entry_makes_absence_proof_indeterminate()
    {
        using var fixture = CreateFixture();
        using (var publication = fixture.Evidence.Publish("fault-pinned"))
        {
            publication.Dispose();
        }
        File.WriteAllText(Path.Combine(fixture.Options.SpoolDirectory, "unknown.entry"), "x");

        var reconciler = new AuditEvidenceOrphanReconciler(
            fixture.Journal,
            fixture.Evidence);
        Assert.False(reconciler.ReconcileNow());

        Assert.Single(AwaitingPaths(fixture.Options));
        Assert.Empty(Directory.GetFiles(
            fixture.Options.EvidenceDirectory,
            "*.unreferenced.script"));
    }

    [Fact]
    public void Retained_nonzero_segment_floor_cannot_prove_absence()
    {
        using var fixture = CreateFixture();
        using (var publication = fixture.Evidence.Publish("floor-pinned"))
        {
            publication.Dispose();
        }
        var priorBoot = Guid.NewGuid();
        var floor = Path.Combine(
            fixture.Options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(priorBoot, 1).FileName);
        using (SecureAuditStorage.CreateExclusiveFile(floor))
        {
        }

        var reconciler = new AuditEvidenceOrphanReconciler(
            fixture.Journal,
            fixture.Evidence);
        Assert.False(reconciler.ReconcileNow());

        Assert.Single(AwaitingPaths(fixture.Options));
        Assert.Empty(Directory.GetFiles(
            fixture.Options.EvidenceDirectory,
            "*.unreferenced.script"));
    }

    [Fact]
    public void Local_failed_append_with_no_written_bytes_is_proved_unreferenced()
    {
        using var fixture = CreateLocalFixture(
            wrapSink: sink => new FailFirstAppendSink(sink));
        var context = new AuditCallContext(fixture.Journal, fixture.Evidence);

        Assert.False(context.TryBegin(
            CaptureInvokeMetadata(),
            "local never written",
            out var failure));

        Assert.Equal("journal.persistence", failure);
        Assert.Empty(AwaitingPaths(fixture.Options));
        Assert.Single(Directory.GetFiles(
            fixture.Options.EvidenceDirectory,
            "*.unreferenced.script"));
    }

    [Fact]
    public void Local_append_failure_after_flush_promotes_durable_reference()
    {
        using var fixture = CreateLocalFixture(
            sinkFault: (point, attempt) =>
                point == FileAuditSinkFaultPoint.AfterPhysicalFlush && attempt == 1);
        var context = new AuditCallContext(fixture.Journal, fixture.Evidence);

        Assert.False(context.TryBegin(
            CaptureInvokeMetadata(),
            "local durable reference",
            out var failure));

        Assert.Equal("journal.persistence", failure);
        Assert.Empty(AwaitingPaths(fixture.Options));
        Assert.Single(Directory.GetFiles(
            fixture.Options.EvidenceDirectory,
            "*.local-committed.script"));
        Assert.Empty(Directory.GetFiles(
            fixture.Options.EvidenceDirectory,
            "*.unreferenced.script"));
    }

    [Fact]
    public void Local_startup_reconciles_before_age_eligible_spool_retention()
    {
        var options = CreateLocalOptions(NewRoot());
        SeedAwaitingLocalDurableReference(options);

        var health = new AuditHealth(options);
        using var resources = AuditRuntimeResources.OpenLocal(
            options,
            health,
            "local-startup-reconcile-test",
            new ScriptEvidenceStoreProvider(options));

        Assert.NotEqual(AuditHealthState.Unavailable, health.Snapshot().State);
        Assert.Empty(AwaitingPaths(options));
        Assert.Single(Directory.GetFiles(
            options.EvidenceDirectory,
            "*.local-committed.script"));
    }

    [Fact]
    public void Local_admin_startup_reconciles_before_age_eligible_spool_retention()
    {
        var options = CreateLocalOptions(NewRoot());
        SeedAwaitingLocalDurableReference(options);

        using var session = AuditAdminJournalSession.Open(
            options,
            "local-admin-reconcile-test");

        Assert.NotEqual(
            AuditHealthState.Unavailable,
            session.Journal.Health.Snapshot().State);
        Assert.Empty(AwaitingPaths(options));
        Assert.Single(Directory.GetFiles(
            options.EvidenceDirectory,
            "*.local-committed.script"));
    }

    [Fact]
    public void Startup_reconciliation_runs_after_staged_global_quota_release()
    {
        var options = CreateOptions(NewRoot());
        using (var publication = new ScriptEvidenceStore(options).Publish("startup orphan"))
        {
            publication.Dispose();
        }

        var observedReleasedQuota = false;
        var evidence = new ScriptEvidenceStoreProvider(options, () =>
        {
            observedReleasedQuota = AuditSpoolQuotaLease.TryAcquireExisting(
                options.SpoolDirectory,
                out var quota);
            quota?.Dispose();
            return new ScriptEvidenceStore(options);
        });
        using var resources = AuditRuntimeResources.OpenAnchored(
            options,
            new AuditHealth(options),
            "evidence-startup-test",
            new AcknowledgingTransport(ConfigurationIdentity),
            evidence);

        Assert.True(observedReleasedQuota);
        Assert.Single(Directory.GetFiles(
            options.EvidenceDirectory,
            "*.unreferenced.script"));
    }

    [Fact]
    public async Task Export_loop_periodically_reconciles_orphans_created_after_startup()
    {
        var options = CreateOptions(NewRoot());
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero));
        var evidence = new ScriptEvidenceStoreProvider(options);
        using var resources = AuditRuntimeResources.OpenAnchored(
            options,
            new AuditHealth(options),
            "evidence-periodic-test",
            new AcknowledgingTransport(ConfigurationIdentity),
            evidence,
            time);
        using (var publication = evidence.Publish("periodic orphan"))
        {
            publication.Dispose();
        }
        time.Advance(AuditEvidenceOrphanReconciler.DefaultInterval + TimeSpan.FromSeconds(1));

        resources.StartExporter();
        await WaitUntilAsync(
            () => Directory.GetFiles(
                options.EvidenceDirectory,
                "*.unreferenced.script").Length == 1,
            TimeSpan.FromSeconds(10));
        await resources.StopExporterAsync();
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* Preserve the assertion that prevented cleanup. */ }
        }
    }

    private AnchoredFixture CreateFixture(
        Func<FileAuditSinkFaultPoint, int, bool>? sinkFault = null,
        Func<FileAuditJournalSink, IAuditJournalSink>? wrapSink = null)
    {
        var options = CreateOptions(NewRoot());
        var supervisorBootId = Guid.NewGuid();
        using var preparation = FileAuditJournalSink.PrepareAnchored(
            options,
            supervisorBootId,
            faultInjector: sinkFault);
        var checkpoint = preparation.CreateCheckpointStore();
        var fileSink = preparation.Activate(checkpoint);
        var sink = wrapSink?.Invoke(fileSink) ?? fileSink;
        var health = new AuditHealth(options);
        var journal = new AuditJournal(
            options,
            health,
            sink,
            "evidence-reconcile-test",
            binaryDigest: null,
            Guid.NewGuid(),
            supervisorBootId);
        var evidence = new ScriptEvidenceStoreProvider(
            new ScriptEvidenceStore(options));
        return new AnchoredFixture(options, journal, checkpoint, evidence);
    }

    private LocalFixture CreateLocalFixture(
        Func<FileAuditSinkFaultPoint, int, bool>? sinkFault = null,
        Func<FileAuditJournalSink, IAuditJournalSink>? wrapSink = null)
    {
        var options = CreateLocalOptions(NewRoot());
        var supervisorBootId = Guid.NewGuid();
        var fileSink = new FileAuditJournalSink(
            options,
            supervisorBootId,
            faultInjector: sinkFault);
        var sink = wrapSink?.Invoke(fileSink) ?? fileSink;
        var journal = new AuditJournal(
            options,
            new AuditHealth(options),
            sink,
            "local-evidence-reconcile-test",
            binaryDigest: null,
            Guid.NewGuid(),
            supervisorBootId);
        var evidence = new ScriptEvidenceStoreProvider(
            new ScriptEvidenceStore(options));
        return new LocalFixture(options, journal, evidence);
    }

    private AuditOptions CreateOptions(string root) => AuditOptions.Create(
        root,
        AuditProtectionMode.Anchored,
        ConfigurationIdentity,
        maxRecordBytes: 4096,
        segmentBytes: 65_536,
        aggregateBytes: 262_144,
        emergencyReserveBytes: 8192,
        retentionAge: TimeSpan.FromMinutes(10),
        maxEvidenceBytes: 4096,
        evidenceAggregateBytes: 32_768,
        evidenceRetentionAge: TimeSpan.FromMinutes(10));

    private AuditOptions CreateLocalOptions(string root) => AuditOptions.Create(
        root,
        maxRecordBytes: 4096,
        segmentBytes: 65_536,
        aggregateBytes: 262_144,
        emergencyReserveBytes: 8192,
        retentionAge: TimeSpan.FromMinutes(10),
        maxEvidenceBytes: 4096,
        evidenceAggregateBytes: 32_768,
        evidenceRetentionAge: TimeSpan.FromMinutes(10));

    private string NewRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-evidence-reconcile-tests-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private static void SeedAwaitingLocalDurableReference(AuditOptions options)
    {
        var supervisorBootId = Guid.NewGuid();
        var sink = new FileAuditJournalSink(options, supervisorBootId);
        var journal = new AuditJournal(
            options,
            new AuditHealth(options),
            sink,
            "local-startup-reconcile-test",
            binaryDigest: null,
            Guid.NewGuid(),
            supervisorBootId);
        var firstEvidence = new ScriptEvidenceStoreProvider(
            new ScriptEvidenceStore(options));
        using (var publication = firstEvidence.Publish("local prior durable reference"))
        {
            AppendRecord(
                journal,
                publication.Reference,
                protectionMode: "local-only",
                configurationIdentity: null);
            // Simulate process loss after the durable append but before the
            // evidence publication state was finalized.
            publication.Dispose();
        }
        journal.Dispose();
        foreach (var path in Directory.GetFiles(
                     options.SpoolDirectory,
                     "ptk-audit-*.jsonl"))
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-20));
        }
    }

    private static AuditCallMetadata CaptureInvokeMetadata()
    {
        Assert.True(AuditCallMetadataCapture.TryCapture(
            new CallToolRequestParams
            {
                Name = "ptk_invoke",
                Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["script"] = JsonSerializer.SerializeToElement("placeholder"),
                },
            },
            new AuditClientContext("evidence-reconcile-test", "1", "session"),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            DateTimeOffset.UtcNow,
            out var metadata,
            out _,
            out var failure),
            failure);
        return metadata!;
    }

    private static void AppendRecord(
        AnchoredFixture fixture,
        ScriptEvidenceReference? evidence) => AppendRecord(
            fixture.Journal,
            evidence,
            "anchored",
            ConfigurationIdentity);

    private static void AppendRecord(
        AuditJournal journal,
        ScriptEvidenceReference? evidence,
        string protectionMode,
        string? configurationIdentity)
    {
        Assert.True(journal.TryReserve(
            1,
            out var reservation,
            out var failure),
            failure);
        using (reservation)
        {
            _ = journal.Append(
                reservation!,
                new AuditEventInput
                {
                    EventType = "test.record",
                    Session = new AuditSession(),
                    Actor = new AuditActor { AttributionStrength = "unknown" },
                    Correlation = new AuditCorrelation(),
                    Request = new AuditRequest
                    {
                        OriginalScriptDigest = evidence?.ScriptDigest,
                        ScriptEvidenceId = evidence is null
                            ? null
                            : Guid.Parse(evidence.EvidenceId),
                    },
                    Routing = new AuditRouting(),
                    Outcome = new AuditOutcome
                    {
                        State = "completed",
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
                        ProtectionMode = protectionMode,
                        ExportConfigurationIdentity = configurationIdentity,
                        HealthState = "healthy",
                    },
                });
        }
    }

    private static int SegmentCount(AuditOptions options) =>
        Directory.GetFiles(options.SpoolDirectory, "ptk-audit-*.jsonl").Length;

    private static string[] AwaitingPaths(AuditOptions options) =>
        Directory.GetFiles(options.EvidenceDirectory, "*.script")
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return !name.EndsWith(".unreferenced.script", StringComparison.Ordinal) &&
                       !name.EndsWith(".local-committed.script", StringComparison.Ordinal) &&
                       !name.EndsWith(".anchored.script", StringComparison.Ordinal) &&
                       !name.Contains(".anchoring.", StringComparison.Ordinal);
            })
            .ToArray();

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Evidence reconciliation did not complete.");
            await Task.Delay(10);
        }
    }

    private sealed record AnchoredFixture(
        AuditOptions Options,
        AuditJournal Journal,
        AuditExportCheckpointStore Checkpoint,
        ScriptEvidenceStoreProvider Evidence) : IDisposable
    {
        public void Dispose()
        {
            Journal.Dispose();
            Checkpoint.Dispose();
        }
    }

    private sealed record LocalFixture(
        AuditOptions Options,
        AuditJournal Journal,
        ScriptEvidenceStoreProvider Evidence) : IDisposable
    {
        public void Dispose() => Journal.Dispose();
    }

    private sealed class FailFirstAppendSink(FileAuditJournalSink inner) :
        IAuditJournalSink,
        IAuditCommittedSpoolSource
    {
        private readonly FileAuditJournalSink _inner = inner;
        private int _failed;

        public long SegmentCapacityBytes => _inner.SegmentCapacityBytes;
        public long AggregateCapacityBytes => _inner.AggregateCapacityBytes;
        public long CurrentSegmentBytes => _inner.CurrentSegmentBytes;
        public long TotalBytes => _inner.TotalBytes;
        public AuditSpoolSegmentIdentity CurrentSegmentIdentity =>
            ((IAuditCommittedSpoolSource)_inner).CurrentSegmentIdentity;

        public bool CanReserve(long reservedBytes) => _inner.CanReserve(reservedBytes);

        public void Append(ReadOnlyMemory<byte> line)
        {
            if (Interlocked.Exchange(ref _failed, 1) == 0)
                throw new IOException("Injected pre-write failure.");
            _inner.Append(line);
        }

        public void FlushToDisk() => _inner.FlushToDisk();

        public AuditCommittedSpoolReadStatus TryReadCommitted(
            AuditSpoolSegmentIdentity identity,
            long offset,
            Span<byte> destination,
            out int bytesRead,
            out long committedTail) =>
            ((IAuditCommittedSpoolSource)_inner).TryReadCommitted(
                identity,
                offset,
                destination,
                out bytesRead,
                out committedTail);

        public void Dispose() => _inner.Dispose();
    }

    private sealed class AcknowledgingTransport(string configurationIdentity) :
        IAuditOtlpExportTransport
    {
        public string ConfigurationIdentity { get; } = configurationIdentity;

        public Task<AuditExportAttemptResult> ExportAsync(
            AuditOtlpRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AuditExportAttemptResult.Acknowledged(
                new string('0', 64),
                warning: false));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
