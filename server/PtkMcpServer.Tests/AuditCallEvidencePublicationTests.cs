using System.Text.Json;
using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditCallEvidencePublicationTests : IDisposable
{
    private readonly List<string> _roots = [];

    [Fact]
    public async Task Call_context_holds_publication_quota_until_call_accepted_is_durable()
    {
        using var appendEntered = new ManualResetEventSlim();
        using var releaseAppend = new ManualResetEventSlim();
        using var fixture = CreateFixture((point, append) =>
        {
            if (point != AuditSinkFaultPoint.BeforeAppend || append != 1) return false;
            appendEntered.Set();
            Assert.True(releaseAppend.Wait(TimeSpan.FromSeconds(10)));
            return false;
        });
        var begin = Task.Run(() => fixture.Context.TryBegin(
            fixture.Metadata,
            "first script",
            out _));
        Assert.True(appendEntered.Wait(TimeSpan.FromSeconds(10)));
        var contender = Task.Run(() => fixture.Evidence.Publish("second script"));

        try
        {
            await Task.Delay(150);
            Assert.False(contender.IsCompleted, "call.accepted did not retain evidence quota");
        }
        finally
        {
            releaseAppend.Set();
        }
        Assert.True(await begin.WaitAsync(TimeSpan.FromSeconds(10)));
        using var second = await contender.WaitAsync(TimeSpan.FromSeconds(10));
        second.CompleteAfterAuditAppend();
    }

    [Fact]
    public void Ambiguous_call_accepted_append_failure_keeps_evidence_pinned()
    {
        using var fixture = CreateFixture((point, append) =>
            point == AuditSinkFaultPoint.AfterAppend && append == 1);

        Assert.False(fixture.Context.TryBegin(
            fixture.Metadata,
            "ambiguous script",
            out var failure));

        Assert.Equal("journal.persistence", failure);
        Assert.Single(Directory.GetFiles(fixture.Options.EvidenceDirectory, "*.script"));
        Assert.Empty(Directory.GetFiles(
            fixture.Options.EvidenceDirectory,
            "*.unreferenced.script"));
    }

    [Fact]
    public void Publication_release_failure_preserves_accepted_context_and_terminal_reservation()
    {
        using var fixture = CreateFixture(
            journalFault: null,
            evidenceFault: stage =>
            {
                if (stage == SecureAuditStorageFaultStage.Release)
                    throw new IOException("injected quota release failure");
            });

        Assert.True(fixture.Context.TryBegin(
            fixture.Metadata,
            "accepted despite release failure",
            out var failure),
            failure);
        Assert.True(fixture.Context.Accepted);
        Assert.Equal("evidence.storage", fixture.Journal.Health.Snapshot().FailureClass);

        fixture.Context.CompleteCall("completed", "ok");

        Assert.Equal(3, fixture.Journal.Sequence);
        Assert.True(fixture.Context.TerminalWritten);
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* Preserve the primary assertion. */ }
        }
    }

    private Fixture CreateFixture(
        Func<AuditSinkFaultPoint, int, bool>? journalFault,
        Action<SecureAuditStorageFaultStage>? evidenceFault = null)
    {
        const int maxRecordBytes = 4096;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-call-evidence-publication-tests-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var options = AuditOptions.Create(
            root,
            maxRecordBytes: maxRecordBytes,
            segmentBytes: maxRecordBytes * 64L,
            aggregateBytes: maxRecordBytes * 64L,
            emergencyReserveBytes: maxRecordBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: maxRecordBytes,
            evidenceAggregateBytes: maxRecordBytes * 8L,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
        var sink = new InMemoryAuditJournalSink(
            options.SegmentBytes,
            options.AggregateBytes,
            options.ProtectionMode,
            options.RetentionAge,
            journalFault);
        var journal = new AuditJournal(
            options,
            new AuditHealth(options),
            sink,
            "publication-test",
            binaryDigest: null,
            Guid.Parse("12345678-1234-4abc-8def-0123456789ab"),
            Guid.Parse("22345678-1234-4abc-8def-0123456789ab"));
        var evidence = new ScriptEvidenceStoreProvider(
            new ScriptEvidenceStore(options, evidenceFault));
        Assert.True(AuditCallMetadataCapture.TryCapture(
            new CallToolRequestParams
            {
                Name = "ptk_invoke",
                Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["script"] = JsonSerializer.SerializeToElement("placeholder"),
                },
            },
            new AuditClientContext("publication-test", "1", "session"),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            DateTimeOffset.UtcNow,
            out var metadata,
            out _,
            out var failure),
            failure);
        return new Fixture(
            options,
            journal,
            evidence,
            new AuditCallContext(journal, evidence),
            metadata!);
    }

    private sealed record Fixture(
        AuditOptions Options,
        AuditJournal Journal,
        ScriptEvidenceStoreProvider Evidence,
        AuditCallContext Context,
        AuditCallMetadata Metadata) : IDisposable
    {
        public void Dispose() => Journal.Dispose();
    }
}
