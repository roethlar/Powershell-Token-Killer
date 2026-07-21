using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpGuardian.Tests;

public sealed class AuditCallLifecycleTests
{
    [Fact]
    public void Guardian_safe_lifecycle_owns_admission_chain_and_terminal_reservation()
    {
        var options = AuditOptions.Create(
            Path.Combine(Path.GetTempPath(), "ptk-guardian-audit-" + Guid.NewGuid().ToString("N")),
            maxRecordBytes: AuditEventSerializer.MaximumLineBytes,
            segmentBytes: AuditEventSerializer.MaximumLineBytes * 16L,
            aggregateBytes: AuditEventSerializer.MaximumLineBytes * 16L,
            emergencyReserveBytes: AuditEventSerializer.MaximumLineBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: ScriptEvidenceStore.MaximumScriptBytes,
            evidenceAggregateBytes: ScriptEvidenceStore.MaximumScriptBytes * 2L,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
        var health = new AuditHealth(options);
        var sink = new InMemoryAuditJournalSink(
            options.SegmentBytes,
            options.AggregateBytes,
            options.ProtectionMode,
            options.RetentionAge);
        using var journal = new AuditJournal(
            options,
            health,
            sink,
            "guardian-audit-test",
            binaryDigest: null,
            hostId: Guid.Parse("12345678-1234-4abc-8def-0123456789ab"),
            supervisorBootId: Guid.Parse("22345678-1234-4abc-8def-0123456789ab"));
        var call = new AuditCallLifecycle(
            journal,
            new ScriptEvidenceStore(options.EvidenceDirectory));
        var metadata = Metadata();

        Assert.True(call.TryBegin(metadata, exactSubmittedScript: null, out var failure), failure);
        Assert.True(call.Accepted);
        Assert.Equal(4L * options.MaxRecordBytes, journal.ReservedBytes);

        ((IAuditBoundaryCall)call).CompleteFromFilter("completed", bytesReturned: 17);

        Assert.True(call.TerminalWritten);
        Assert.Equal(0, journal.ReservedBytes);
        var events = sink.Lines.Select(Parse).ToArray();
        Assert.Equal(["call.accepted", "call.completed"], events.Select(EventType));
        Assert.All(events, value =>
        {
            Assert.Equal("ptk.audit/2", value.GetProperty("schema_version").GetString());
            Assert.False(value.TryGetProperty("host", out _));
        });
        var callId = events[0].GetProperty("correlation").GetProperty("call_id").GetGuid();
        Assert.Equal(callId, events[1].GetProperty("correlation").GetProperty("call_id").GetGuid());
        Assert.Equal(
            events[0].GetProperty("event_id").GetGuid(),
            events[1].GetProperty("correlation").GetProperty("parent_event_id").GetGuid());
        Assert.Equal(
            17,
            events[1].GetProperty("outcome").GetProperty("bytes_returned").GetInt64());
    }

    [Fact]
    public void Guardian_safe_lifecycle_freezes_bound_session_after_authorization()
    {
        var options = AuditOptions.Create(
            Path.Combine(Path.GetTempPath(), "ptk-guardian-session-" + Guid.NewGuid().ToString("N")),
            maxRecordBytes: AuditEventSerializer.MaximumLineBytes,
            segmentBytes: AuditEventSerializer.MaximumLineBytes * 16L,
            aggregateBytes: AuditEventSerializer.MaximumLineBytes * 16L,
            emergencyReserveBytes: AuditEventSerializer.MaximumLineBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: ScriptEvidenceStore.MaximumScriptBytes,
            evidenceAggregateBytes: ScriptEvidenceStore.MaximumScriptBytes * 2L,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
        var health = new AuditHealth(options);
        var sink = new InMemoryAuditJournalSink(
            options.SegmentBytes,
            options.AggregateBytes,
            options.ProtectionMode,
            options.RetentionAge);
        using var journal = new AuditJournal(
            options,
            health,
            sink,
            "guardian-session-test",
            binaryDigest: null,
            hostId: Guid.Parse("13345678-1234-4abc-8def-0123456789ab"),
            supervisorBootId: Guid.Parse("23345678-1234-4abc-8def-0123456789ab"));
        var call = new SessionProjectingAuditCall(
            journal,
            new ScriptEvidenceStore(options.EvidenceDirectory));

        Assert.True(call.TryBegin(Metadata(), null, out var failure), failure);
        call.Authorize(new AuditSession
        {
            Name = "build",
            Generation = 17,
            BindingKind = "template",
            TemplateName = "ci",
            TemplateDigest = new string('1', 64),
            BootstrapDigest = new string('2', 64),
            DeclaredPurpose = "compile",
            DeclaredTarget = "local",
            DeclaredIdentity = "builder",
            EffectiveIdentity = "builder-effective",
            AllowColdBackground = false,
        });
        Assert.Throws<InvalidOperationException>(() => call.Authorize(new AuditSession
        {
            Name = "other",
            Generation = 18,
            BindingKind = "dynamic",
        }));
        call.CompleteCall("completed", "ok");

        var events = sink.Lines.Select(Parse).ToArray();
        Assert.Equal(
            ["call.accepted", "job.kill_requested", "call.completed"],
            events.Select(EventType));
        Assert.Equal("default", events[0].GetProperty("session").GetProperty("name").GetString());
        foreach (var value in events[1..])
        {
            var session = value.GetProperty("session");
            Assert.Equal("build", session.GetProperty("name").GetString());
            Assert.Equal(17, session.GetProperty("generation").GetInt64());
            Assert.Equal("template", session.GetProperty("binding_kind").GetString());
            Assert.Equal("ci", session.GetProperty("template_name").GetString());
            Assert.Equal(new string('1', 64), session.GetProperty("template_digest").GetString());
            Assert.Equal(new string('2', 64), session.GetProperty("bootstrap_digest").GetString());
            Assert.Equal("compile", session.GetProperty("declared_purpose").GetString());
            Assert.Equal("local", session.GetProperty("declared_target").GetString());
            Assert.Equal("builder", session.GetProperty("declared_identity").GetString());
            Assert.Equal("builder-effective", session.GetProperty("effective_identity").GetString());
            Assert.False(session.GetProperty("allow_cold_background").GetBoolean());
        }
    }

    [Fact]
    public void Guardian_gate_admits_the_exact_lifecycle_from_its_factory()
    {
        var options = AuditOptions.Create(
            Path.Combine(Path.GetTempPath(), "ptk-guardian-gate-" + Guid.NewGuid().ToString("N")),
            maxRecordBytes: AuditEventSerializer.MaximumLineBytes,
            segmentBytes: AuditEventSerializer.MaximumLineBytes * 16L,
            aggregateBytes: AuditEventSerializer.MaximumLineBytes * 16L,
            emergencyReserveBytes: AuditEventSerializer.MaximumLineBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: ScriptEvidenceStore.MaximumScriptBytes,
            evidenceAggregateBytes: ScriptEvidenceStore.MaximumScriptBytes * 2L,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
        var health = new AuditHealth(options);
        var sink = new InMemoryAuditJournalSink(
            options.SegmentBytes,
            options.AggregateBytes,
            options.ProtectionMode,
            options.RetentionAge);
        using var journal = new AuditJournal(
            options,
            health,
            sink,
            "guardian-gate-test",
            binaryDigest: null,
            hostId: Guid.Parse("32345678-1234-4abc-8def-0123456789ab"),
            supervisorBootId: Guid.Parse("42345678-1234-4abc-8def-0123456789ab"));
        var evidence = new ScriptEvidenceStore(options.EvidenceDirectory);
        var factory = new RecordingCallFactory();
        using var gate = AuditRuntimeGate.CreateOperationalForTests(
            options,
            health,
            journal,
            evidence,
            factory);

        Assert.True(gate.TryBeginCall(
            Metadata(),
            exactSubmittedScript: null,
            out var call,
            out var lease,
            out var failure), failure);

        call!.CompleteCall("completed", "ok");
        lease!.Dispose();
        Assert.Equal(1, factory.CreateCount);
        Assert.Same(factory.Created, call);
        Assert.Equal(["call.accepted", "call.completed"], sink.Lines.Select(Parse).Select(EventType));
    }

    [Fact]
    public void Guardian_journal_captures_one_v3_host_snapshot_for_each_event()
    {
        var options = AuditOptions.Create(
            Path.Combine(Path.GetTempPath(), "ptk-guardian-v3-" + Guid.NewGuid().ToString("N")),
            maxRecordBytes: AuditEventSerializer.MaximumLineBytes,
            segmentBytes: AuditEventSerializer.MaximumLineBytes * 16L,
            aggregateBytes: AuditEventSerializer.MaximumLineBytes * 16L,
            emergencyReserveBytes: AuditEventSerializer.MaximumLineBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: ScriptEvidenceStore.MaximumScriptBytes,
            evidenceAggregateBytes: ScriptEvidenceStore.MaximumScriptBytes * 2L,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
        var health = new AuditHealth(options);
        var sink = new InMemoryAuditJournalSink(
            options.SegmentBytes,
            options.AggregateBytes,
            options.ProtectionMode,
            options.RetentionAge);
        var hostSnapshots = new GuardianAuditHostSnapshotSource();
        using var journal = new AuditJournal(
            options,
            health,
            sink,
            "guardian-v3-test",
            binaryDigest: null,
            hostId: Guid.Parse("62345678-1234-4abc-8def-0123456789ab"),
            supervisorBootId: Guid.Parse("72345678-1234-4abc-8def-0123456789ab"),
            hostSnapshots: hostSnapshots);
        var call = new AuditCallLifecycle(
            journal,
            new ScriptEvidenceStore(options.EvidenceDirectory));

        Assert.True(call.TryBegin(Metadata(), null, out var failure), failure);
        var live = new AuditHostSnapshot(
            Guid.Parse("82345678-1234-4abc-8def-0123456789ab"),
            Generation: 7,
            State: "ready",
            RecoveryAttempt: 0);
        hostSnapshots.Publish(live);
        call.CompleteCall("completed", "ok");

        var events = sink.Lines.Select(Parse).ToArray();
        Assert.Equal(2, events.Length);
        Assert.All(events, value => Assert.Equal(
            "ptk.audit/3",
            value.GetProperty("schema_version").GetString()));
        var acceptedHost = events[0].GetProperty("host");
        Assert.Equal(JsonValueKind.Null, acceptedHost.GetProperty("boot_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, acceptedHost.GetProperty("generation").ValueKind);
        Assert.Equal("absent", acceptedHost.GetProperty("state").GetString());
        Assert.Equal(0, acceptedHost.GetProperty("recovery_attempt").GetInt64());
        var terminalHost = events[1].GetProperty("host");
        Assert.Equal(live.BootId, terminalHost.GetProperty("boot_id").GetGuid());
        Assert.Equal(live.Generation, terminalHost.GetProperty("generation").GetInt64());
        Assert.Equal(live.State, terminalHost.GetProperty("state").GetString());
        Assert.Equal(live.RecoveryAttempt, terminalHost.GetProperty("recovery_attempt").GetInt64());
        Assert.Throws<AuditEventValidationException>(() => hostSnapshots.Publish(
            live with { State = "stopped" }));
        Assert.Same(live, hostSnapshots.Capture());
    }

    [Fact]
    public async Task Guardian_gate_propagates_v3_snapshots_through_default_runtime_resources()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ptk-guardian-v3-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = AuditOptions.Create(
                root,
                maxRecordBytes: AuditEventSerializer.MaximumLineBytes,
                segmentBytes: AuditEventSerializer.MaximumLineBytes * 16L,
                aggregateBytes: AuditEventSerializer.MaximumLineBytes * 16L,
                emergencyReserveBytes: AuditEventSerializer.MaximumLineBytes * 2L,
                retentionAge: TimeSpan.FromMinutes(10),
                maxEvidenceBytes: ScriptEvidenceStore.MaximumScriptBytes,
                evidenceAggregateBytes: ScriptEvidenceStore.MaximumScriptBytes * 2L,
                evidenceRetentionAge: TimeSpan.FromMinutes(10));
            var health = new AuditHealth(options);
            var evidence = new ScriptEvidenceStoreProvider(options);
            var hostSnapshots = new GuardianAuditHostSnapshotSource();
            using (var gate = new AuditRuntimeGate(
                       options,
                       health,
                       evidence,
                       "guardian-v3-runtime-test",
                       hostSnapshots: hostSnapshots))
            {
                await gate.StartAsync(CancellationToken.None);
                hostSnapshots.Publish(new AuditHostSnapshot(
                    Guid.Parse("92345678-1234-4abc-8def-0123456789ab"),
                    Generation: 9,
                    State: "ready",
                    RecoveryAttempt: 0));
                Assert.True(gate.TryBeginCall(
                    Metadata(),
                    exactSubmittedScript: null,
                    out var call,
                    out var lease,
                    out var failure), failure);
                call!.CompleteCall("completed", "ok");
                lease!.Dispose();
                await gate.StopAsync(CancellationToken.None);
            }

            var spool = Assert.Single(Directory.GetFiles(
                options.SpoolDirectory,
                "ptk-audit-*.jsonl"));
            var events = File.ReadLines(spool)
                .Select(ParseLine)
                .ToArray();
            Assert.Equal(
                ["server.started", "call.accepted", "call.completed", "server.stopped"],
                events.Select(EventType));
            Assert.All(events, value =>
            {
                Assert.Equal("ptk.audit/3", value.GetProperty("schema_version").GetString());
                Assert.True(value.TryGetProperty("host", out _));
            });
            Assert.Equal("absent", events[0].GetProperty("host").GetProperty("state").GetString());
            Assert.All(events[1..], value => Assert.Equal(
                "ready",
                value.GetProperty("host").GetProperty("state").GetString()));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static AuditCallMetadata Metadata() => new(
            new AuditActor
            {
                Transport = "mcp_stdio",
                ClientName = "guardian-test",
                ClientVersion = "1",
                ClientSessionId = "session",
                AttributionStrength = "client_asserted",
            },
            new AuditRequest
            {
                Tool = "ptk_state",
                Action = "state",
                SessionRequested = "default",
                ProvidedFields = [],
                ListAvailable = false,
            },
            new AuditOperationProfile(
                MaximumCallRecordSlots: 5,
                PersistentJobTerminalSlots: 0,
                RequiresScriptEvidence: false,
                MayHaveSideEffects: true));

    private static JsonElement Parse(byte[] line)
    {
        using var document = JsonDocument.Parse(line.AsMemory(0, line.Length - 1));
        return document.RootElement.Clone();
    }

    private static JsonElement ParseLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private static string EventType(JsonElement value) =>
        value.GetProperty("event_type").GetString()!;

    private sealed class RecordingCallFactory : IAuditCallFactory
    {
        internal int CreateCount { get; private set; }

        internal AuditCallLifecycle? Created { get; private set; }

        public AuditCallLifecycle Create(
            AuditJournal journal,
            ScriptEvidenceStoreProvider evidence)
        {
            CreateCount++;
            return Created = new AuditCallLifecycle(journal, evidence);
        }
    }

    private sealed class SessionProjectingAuditCall : AuditCallLifecycle
    {
        internal SessionProjectingAuditCall(
            AuditJournal journal,
            ScriptEvidenceStore evidence)
            : base(journal, evidence)
        {
        }

        internal void Authorize(AuditSession session)
        {
            var previous = ProjectSession(session);
            try
            {
                Append("job.kill_requested", "requested", jobId: 42);
                _effectAuthorized = true;
            }
            catch
            {
                _ = ProjectSession(previous);
                throw;
            }
        }
    }
}
