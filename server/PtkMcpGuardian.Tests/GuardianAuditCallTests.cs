using System.Text;
using System.Text.Json;
using PtkMcpServer.Audit;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class GuardianAuditCallTests
{
    [Fact]
    public void Durable_dispatch_authorization_freezes_guardian_call_and_session_identity()
    {
        using var fixture = new Fixture();
        var call = fixture.CreateCall();
        Assert.True(call.TryBegin(Metadata(), null, out var failure), failure);
        var publicCallId = call.PublicCallId;
        var jobId = new PublicJobId(42);

        Assert.True(call.TryAuthorizeDispatch(new GuardianAuditDispatchAuthorization(
            GuardianHostOperationKind.JobKill,
            TemplateSession(),
            jobId)));
        Assert.True(call.EffectAuthorized);
        Assert.Throws<InvalidOperationException>(() =>
            call.TryAuthorizeDispatch(new GuardianAuditDispatchAuthorization(
                GuardianHostOperationKind.JobKill,
                TemplateSession(),
                jobId)));
        call.CompleteCall("completed", "ok");

        var events = fixture.Sink.Lines.Select(Parse).ToArray();
        Assert.Equal(
            ["call.accepted", GuardianAuditCall.DispatchAuthorizedEvent, "call.completed"],
            events.Select(EventType));
        Assert.All(events, value => Assert.Equal(
            "ptk.audit/3",
            value.GetProperty("schema_version").GetString()));

        var authorization = events[1];
        Assert.Equal(
            publicCallId.Value,
            authorization.GetProperty("correlation").GetProperty("call_id").GetGuid());
        Assert.Equal(
            jobId.Value,
            authorization.GetProperty("correlation").GetProperty("job_id").GetInt64());
        Assert.Equal(
            jobId.Value,
            authorization.GetProperty("request").GetProperty("job_id").GetInt64());
        Assert.Equal(
            "job_kill",
            authorization.GetProperty("outcome").GetProperty("detail_code").GetString());
        Assert.Equal(
            "authorized",
            authorization.GetProperty("outcome").GetProperty("state").GetString());
        Assert.Equal(
            "none",
            authorization.GetProperty("coverage").GetProperty("root_process_observed").GetString());

        var host = authorization.GetProperty("host");
        Assert.Equal(Fixture.HostBootId, host.GetProperty("boot_id").GetGuid());
        Assert.Equal(5, host.GetProperty("generation").GetInt64());
        Assert.Equal("ready", host.GetProperty("state").GetString());

        var session = authorization.GetProperty("session");
        Assert.Equal("build", session.GetProperty("name").GetString());
        Assert.Equal(17, session.GetProperty("generation").GetInt64());
        Assert.Equal("template", session.GetProperty("binding_kind").GetString());
        Assert.Equal("ci", session.GetProperty("template_name").GetString());
        Assert.Equal(TemplateDigest.Value, session.GetProperty("template_digest").GetString());
        Assert.Equal(BootstrapDigest.Value, session.GetProperty("bootstrap_digest").GetString());
        Assert.Equal("compile project", session.GetProperty("declared_purpose").GetString());
        Assert.Equal("local", session.GetProperty("declared_target").GetString());
        Assert.Equal("builder", session.GetProperty("declared_identity").GetString());
        Assert.False(session.GetProperty("allow_cold_background").GetBoolean());
    }

    [Fact]
    public void Failed_dispatch_append_returns_no_authority()
    {
        using var fixture = new Fixture((point, call) =>
            point == AuditSinkFaultPoint.BeforeAppend && call == 2);
        var audit = fixture.CreateCall();
        Assert.True(audit.TryBegin(Metadata(), null, out var failure), failure);

        Assert.False(audit.TryAuthorizeDispatch(new GuardianAuditDispatchAuthorization(
            GuardianHostOperationKind.JobKill,
            TemplateSession(),
            new PublicJobId(42))));

        Assert.False(audit.EffectAuthorized);
        Assert.True(audit.AuthorizationPersistenceFailed);
        Assert.Single(fixture.Sink.Lines);
        Assert.Equal("call.accepted", EventType(Parse(fixture.Sink.Lines[0])));
        audit.CompleteCall("not_started", AuditCallLifecycle.NotStartedMessage);
        Assert.Equal(0, fixture.Journal.ReservedBytes);
    }

    [Fact]
    public void Dispatch_job_identity_must_match_kind_and_accepted_request()
    {
        Assert.Throws<ArgumentException>(() =>
            new GuardianAuditDispatchAuthorization(
                GuardianHostOperationKind.JobKill,
                TemplateSession()));
        Assert.Throws<ArgumentException>(() =>
            new GuardianAuditDispatchAuthorization(
                GuardianHostOperationKind.Reset,
                TemplateSession(),
                new PublicJobId(42)));

        using var fixture = new Fixture();
        var call = fixture.CreateCall();
        Assert.True(call.TryBegin(Metadata(), null, out var failure), failure);
        Assert.Throws<InvalidOperationException>(() =>
            call.TryAuthorizeDispatch(new GuardianAuditDispatchAuthorization(
                GuardianHostOperationKind.JobKill,
                TemplateSession(),
                new PublicJobId(43))));
        Assert.False(call.EffectAuthorized);
        call.CompleteCall("not_started", "mismatch");
    }

    [Fact]
    public void Dispatch_authorization_must_match_the_admitted_action_and_session()
    {
        using (var fixture = new Fixture())
        {
            var call = fixture.CreateCall();
            Assert.True(call.TryBegin(Metadata(), null, out var failure), failure);
            Assert.Throws<InvalidOperationException>(() =>
                call.TryAuthorizeDispatch(new GuardianAuditDispatchAuthorization(
                    GuardianHostOperationKind.JobStatus,
                    TemplateSession(),
                    new PublicJobId(42))));
            Assert.False(call.EffectAuthorized);
            call.CompleteCall("not_started", "action mismatch");
        }

        using (var fixture = new Fixture())
        {
            var call = fixture.CreateCall();
            Assert.True(call.TryBegin(Metadata(), null, out var failure), failure);
            Assert.Throws<InvalidOperationException>(() =>
                call.TryAuthorizeDispatch(new GuardianAuditDispatchAuthorization(
                    GuardianHostOperationKind.JobKill,
                    DefaultSession(),
                    new PublicJobId(42))));
            Assert.False(call.EffectAuthorized);
            call.CompleteCall("not_started", "session mismatch");
        }
    }

    [Theory]
    [InlineData(false, GuardianAuditCall.DispatchCompletedEvent, "completed", "confirmed")]
    [InlineData(true, GuardianAuditCall.DispatchFailedEvent, "failed", "confirmed")]
    public void Decoded_private_terminal_closes_the_authorized_delivery(
        bool isError,
        string expectedEvent,
        string expectedState,
        string expectedCertainty)
    {
        using var fixture = new Fixture();
        var call = fixture.CreateCall();
        Assert.True(call.TryBegin(Metadata(), null, out var failure), failure);
        Assert.True(call.TryAuthorizeDispatch(new GuardianAuditDispatchAuthorization(
            GuardianHostOperationKind.JobKill,
            TemplateSession(),
            new PublicJobId(42))));

        call.MarkPrivateWriteStarting();
        call.RecordDecodedTerminal(isError, "private terminal", "private_terminal");

        Assert.True(call.UserExecutionStarted);
        Assert.True(call.TerminalWritten);
        Assert.Equal(0, fixture.Journal.ReservedBytes);
        var events = fixture.Sink.Lines.Select(Parse).ToArray();
        Assert.Equal(
            ["call.accepted", GuardianAuditCall.DispatchAuthorizedEvent, expectedEvent,
                isError ? "call.failed" : "call.completed"],
            events.Select(EventType));
        var dispatchTerminal = events[2];
        Assert.Equal(
            expectedState,
            dispatchTerminal.GetProperty("outcome").GetProperty("state").GetString());
        Assert.Equal(
            expectedCertainty,
            dispatchTerminal.GetProperty("outcome").GetProperty("termination_certainty").GetString());
        Assert.Equal(
            Encoding.UTF8.GetByteCount("private terminal"),
            dispatchTerminal.GetProperty("outcome").GetProperty("bytes_returned").GetInt64());
        Assert.Throws<InvalidOperationException>(() =>
            call.RecordDecodedTerminal(isError, "duplicate"));
    }

    [Fact]
    public void Prewrite_refusal_is_proved_not_started_with_or_without_authorization()
    {
        using var earlyFixture = new Fixture();
        var early = earlyFixture.CreateCall();
        Assert.True(early.TryBegin(Metadata(), null, out var earlyFailure), earlyFailure);
        early.RecordNotDispatched(
            TemplateSession(generation: null),
            "host_recovering",
            "recovering");
        var earlyEvents = earlyFixture.Sink.Lines.Select(Parse).ToArray();
        Assert.Equal(
            ["call.accepted", GuardianAuditCall.DispatchNotStartedEvent, "call.not_started"],
            earlyEvents.Select(EventType));
        Assert.Equal(
            "build",
            earlyEvents[1].GetProperty("session").GetProperty("name").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            earlyEvents[1].GetProperty("session").GetProperty("generation").ValueKind);

        using var revalidatedFixture = new Fixture();
        var revalidated = revalidatedFixture.CreateCall();
        Assert.True(revalidated.TryBegin(Metadata(), null, out var revalidatedFailure), revalidatedFailure);
        Assert.True(revalidated.TryAuthorizeDispatch(new GuardianAuditDispatchAuthorization(
            GuardianHostOperationKind.JobKill,
            TemplateSession(),
            new PublicJobId(42))));
        revalidated.RecordNotDispatched(
            TemplateSession(),
            "backend_lost_before_dispatch",
            "retry later");
        var events = revalidatedFixture.Sink.Lines.Select(Parse).ToArray();
        Assert.Equal(
            ["call.accepted", GuardianAuditCall.DispatchAuthorizedEvent,
                GuardianAuditCall.DispatchNotStartedEvent, "call.not_started"],
            events.Select(EventType));
        Assert.Equal(
            "none",
            events[2].GetProperty("coverage").GetProperty("root_process_observed").GetString());
        Assert.Equal(
            "not_applicable",
            events[2].GetProperty("outcome").GetProperty("termination_certainty").GetString());
    }

    [Fact]
    public void Postwrite_loss_is_terminally_audited_as_outcome_unknown()
    {
        using var fixture = new Fixture();
        var call = fixture.CreateCall();
        Assert.True(call.TryBegin(Metadata(), null, out var failure), failure);
        Assert.Throws<InvalidOperationException>(() => call.MarkPrivateWriteStarting());
        Assert.True(call.TryAuthorizeDispatch(new GuardianAuditDispatchAuthorization(
            GuardianHostOperationKind.JobKill,
            TemplateSession(),
            new PublicJobId(42))));
        Assert.Throws<InvalidOperationException>(() =>
            call.RecordDecodedTerminal(isError: true, "not written"));

        call.MarkPrivateWriteStarting();
        Assert.Throws<InvalidOperationException>(() =>
            call.RecordNotDispatched(
                TemplateSession(),
                "backend_lost_before_dispatch",
                "unsafe"));
        call.RecordOutcomeUnknown("unknown terminal");

        var events = fixture.Sink.Lines.Select(Parse).ToArray();
        Assert.Equal(
            ["call.accepted", GuardianAuditCall.DispatchAuthorizedEvent,
                GuardianAuditCall.DispatchOutcomeUnknownEvent, "call.failed"],
            events.Select(EventType));
        foreach (var value in events[2..])
        {
            Assert.Equal(
                "unknown",
                value.GetProperty("outcome").GetProperty("termination_certainty").GetString());
        }
        Assert.Equal(
            "outcome_unknown",
            events[2].GetProperty("outcome").GetProperty("state").GetString());
        Assert.False(events[2].GetProperty("request").GetProperty("job_id").ValueKind ==
            JsonValueKind.Null);
    }

    private static readonly byte[] BootstrapBytes = Encoding.UTF8.GetBytes("Write-Output ready");
    private static readonly Sha256Digest BootstrapDigest = Sha256Digest.Compute(BootstrapBytes);
    private static readonly Sha256Digest TemplateDigest = new(new string('a', 64));
    private static readonly Sha256Digest BindingDigest = new(new string('b', 64));

    private static GuardianAuditSession TemplateSession(long? generation = 17)
    {
        var templateName = new CanonicalAlias("ci");
        var template = new RecoveryTemplate(
            templateName,
            "compile project",
            startupTimeoutSeconds: 30,
            declaredTarget: "local",
            declaredIdentity: "builder",
            allowColdBackground: false,
            TemplateDigest,
            BootstrapDigest,
            BootstrapBytes);
        var binding = new RecoveryBinding(
            new CanonicalAlias("build"),
            RecoveryBindingKind.Template,
            templateName,
            TemplateDigest,
            BootstrapDigest,
            allowColdBackground: false,
            DesiredSessionState.Ready,
            new SessionTransitionVersion(3),
            BindingDigest);
        return new GuardianAuditSession(
            binding,
            generation is null ? null : new WorkerGeneration(generation.Value),
            template);
    }

    private static GuardianAuditSession DefaultSession()
    {
        var binding = new RecoveryBinding(
            new CanonicalAlias("default"),
            RecoveryBindingKind.Default,
            templateName: null,
            templateDigest: null,
            bootstrapDigest: null,
            allowColdBackground: false,
            DesiredSessionState.Ready,
            new SessionTransitionVersion(1),
            BindingDigest);
        return new GuardianAuditSession(binding, new WorkerGeneration(1));
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
            Tool = "ptk_job",
            Action = "kill",
            SessionRequested = "build",
            ProvidedFields = ["action", "id", "session"],
            JobId = 42,
        },
        new AuditOperationProfile(
            MaximumCallRecordSlots: 4,
            PersistentJobTerminalSlots: 0,
            RequiresScriptEvidence: false,
            MayHaveSideEffects: true));

    private static JsonElement Parse(byte[] line)
    {
        using var document = JsonDocument.Parse(line.AsMemory(0, line.Length - 1));
        return document.RootElement.Clone();
    }

    private static string EventType(JsonElement value) =>
        value.GetProperty("event_type").GetString()!;

    private sealed class Fixture : IDisposable
    {
        internal static readonly Guid HostBootId =
            Guid.Parse("14345678-1234-4abc-8def-0123456789ab");

        private readonly string _root;
        private readonly ScriptEvidenceStoreProvider _evidence;

        internal Fixture(Func<AuditSinkFaultPoint, int, bool>? faultInjector = null)
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "ptk-guardian-dispatch-audit-" + Guid.NewGuid().ToString("N"));
            var options = AuditOptions.Create(
                _root,
                maxRecordBytes: AuditEventSerializer.MaximumLineBytes,
                segmentBytes: AuditEventSerializer.MaximumLineBytes * 16L,
                aggregateBytes: AuditEventSerializer.MaximumLineBytes * 16L,
                emergencyReserveBytes: AuditEventSerializer.MaximumLineBytes * 2L,
                retentionAge: TimeSpan.FromMinutes(10),
                maxEvidenceBytes: ScriptEvidenceStore.MaximumScriptBytes,
                evidenceAggregateBytes: ScriptEvidenceStore.MaximumScriptBytes * 2L,
                evidenceRetentionAge: TimeSpan.FromMinutes(10));
            var health = new AuditHealth(options);
            Sink = new InMemoryAuditJournalSink(
                options.SegmentBytes,
                options.AggregateBytes,
                options.ProtectionMode,
                options.RetentionAge,
                faultInjector);
            var hostSnapshots = new GuardianAuditHostSnapshotSource();
            hostSnapshots.Publish(new AuditHostSnapshot(
                HostBootId,
                Generation: 5,
                State: "ready",
                RecoveryAttempt: 0));
            Journal = new AuditJournal(
                options,
                health,
                Sink,
                "guardian-dispatch-audit-test",
                binaryDigest: null,
                hostId: Guid.Parse("24345678-1234-4abc-8def-0123456789ab"),
                supervisorBootId: Guid.Parse("34345678-1234-4abc-8def-0123456789ab"),
                hostSnapshots: hostSnapshots);
            _evidence = new ScriptEvidenceStoreProvider(options);
        }

        internal InMemoryAuditJournalSink Sink { get; }

        internal AuditJournal Journal { get; }

        internal GuardianAuditCall CreateCall() => Assert.IsType<GuardianAuditCall>(
            GuardianAuditCallFactory.Instance.Create(Journal, _evidence));

        public void Dispose()
        {
            Journal.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
