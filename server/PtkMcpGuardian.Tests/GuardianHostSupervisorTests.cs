using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using PtkMcpGuardian.Host;
using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Ownership;
using PtkMcpGuardian.Output;
using PtkMcpGuardian.Standalone;
using PtkMcpGuardian.Standalone.Fake;
using PtkMcpServer;
using PtkMcpServer.Audit;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class GuardianHostSupervisorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly GuardianHostOperationIdentity OperationIdentity = new(
        new PlanId(Guid.Parse("33333333-3333-4333-8333-333333333333")),
        new OperationId(Guid.Parse("44444444-4444-4444-8444-444444444444")));

    [Fact]
    public void Every_private_dispatch_entry_requires_the_admitted_guardian_audit_call()
    {
        string[] dispatchEntries =
        [
            "DispatchSessionOperationAsync",
            "DispatchBackgroundInvokeAsync",
            "DispatchJobStatusAsync",
            "DispatchJobOutputAsync",
            "DispatchJobKillAsync",
        ];

        var methods = typeof(GuardianHostSupervisor).GetMethods(
            BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (var entry in dispatchEntries)
        {
            var method = Assert.Single(methods, candidate => candidate.Name == entry);
            Assert.Single(
                method.GetParameters(),
                parameter => parameter.ParameterType == typeof(GuardianAuditCall));
        }
    }

    [Fact]
    public async Task Loss_before_write_authorization_is_safe_and_never_dispatched()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];
        var blocked = rig.Observer.BlockNextAuthorization();

        var dispatch = rig.DispatchJobListAsync();
        await blocked.WaitAsync(TestTimeout);
        old.Crash();
        await WaitUntilAsync(() =>
            rig.Factory.Resources.Count == 2 &&
            rig.Supervisor.SnapshotState().Host is
            {
                State: PublicHostState.Ready,
                Generation.Value: 2,
            });
        rig.Observer.ReleaseAuthorization();

        var result = await dispatch.WaitAsync(TestTimeout);
        var error = DecodeRecovery(result);
        Assert.Equal(PublicRecoveryDetailCode.BackendLostBeforeDispatch, error.DetailCode);
        Assert.True(error.Retryable);
        var gate = Assert.IsType<SessionReadyGate>(error.RetryGate);
        Assert.Equal(TestRig.Alias, gate.Alias);
        Assert.Equal(RecoveryPhase.Containment, error.RecoveryPhase);
        Assert.Equal(0, old.OperationCount);
        Assert.Equal(0, rig.Factory.Resources[1].OperationCount);
        Assert.Equal(
            ["call.accepted", GuardianAuditCall.DispatchNotStartedEvent, "call.not_started"],
            rig.AuditEventTypes());
    }

    [Fact]
    public async Task Host_recovery_refusal_uses_the_selected_session_gate()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond, AutoConfirmContainment: false),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];

        old.Crash();
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host is
            {
                State: PublicHostState.Recovering,
                RecoveryPhase: RecoveryPhase.Containment,
            });

        var error = DecodeRecovery(
            await rig.DispatchJobListAsync().WaitAsync(TestTimeout));
        Assert.Equal(PublicRecoveryDetailCode.HostRecovering, error.DetailCode);
        Assert.True(error.Retryable);
        var gate = Assert.IsType<SessionReadyGate>(error.RetryGate);
        Assert.Equal(TestRig.Alias, gate.Alias);
        Assert.Equal(0, old.OperationCount);

        old.ConfirmContainment();
    }

    [Fact]
    public async Task Fresh_dispatch_synchronizes_faulted_client_before_refusal()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];
        var fatalBlocked = rig.Observer.BlockNextFatalObservation();

        try
        {
            await old.InjectProtocolFatalAsync();
            await fatalBlocked.WaitAsync(TestTimeout);
            Assert.Equal(PublicHostState.Ready, rig.Supervisor.SnapshotState().Host.State);

            var error = DecodeRecovery(
                await rig.DispatchJobListAsync().WaitAsync(TestTimeout));
            Assert.Equal(PublicRecoveryDetailCode.HostRecovering, error.DetailCode);
            Assert.True(error.Retryable);
            var gate = Assert.IsType<SessionReadyGate>(error.RetryGate);
            Assert.Equal(TestRig.Alias, gate.Alias);
            Assert.Equal(0, old.OperationCount);
        }
        finally
        {
            rig.Observer.ReleaseFatalObservation();
        }

        await WaitUntilAsync(() =>
            rig.Factory.Resources.Count == 2 &&
            rig.Supervisor.SnapshotState().Host is
            {
                State: PublicHostState.Ready,
                Generation.Value: 2,
            });
        Assert.Equal(0, rig.Factory.Resources[1].OperationCount);
    }

    [Fact]
    public async Task Revalidation_synchronizes_faulted_client_as_prewrite_loss()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];
        var authorizationBlocked = rig.Observer.BlockNextAuthorization();
        var fatalBlocked = rig.Observer.BlockNextFatalObservation();

        var dispatch = rig.DispatchJobListAsync();
        await authorizationBlocked.WaitAsync(TestTimeout);
        try
        {
            await old.InjectProtocolFatalAsync();
            await fatalBlocked.WaitAsync(TestTimeout);
            Assert.Equal(PublicHostState.Ready, rig.Supervisor.SnapshotState().Host.State);
            rig.Observer.ReleaseAuthorization();

            var error = DecodeRecovery(await dispatch.WaitAsync(TestTimeout));
            Assert.Equal(
                PublicRecoveryDetailCode.BackendLostBeforeDispatch,
                error.DetailCode);
            Assert.True(error.Retryable);
            var gate = Assert.IsType<SessionReadyGate>(error.RetryGate);
            Assert.Equal(TestRig.Alias, gate.Alias);
            Assert.Equal(0, old.OperationCount);
        }
        finally
        {
            rig.Observer.ReleaseAuthorization();
            rig.Observer.ReleaseFatalObservation();
        }

        await WaitUntilAsync(() =>
            rig.Factory.Resources.Count == 2 &&
            rig.Supervisor.SnapshotState().Host is
            {
                State: PublicHostState.Ready,
                Generation.Value: 2,
            });
        Assert.Equal(0, rig.Factory.Resources[1].OperationCount);
    }

    [Fact]
    public async Task Contract_mismatch_during_recovery_is_permanent()
    {
        await using var rig = new TestRig(
            new AttemptPlan(
                HostBehavior.ContractMismatchHello,
                AutoConfirmContainment: false));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.StartAsync());
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host is
            {
                State: PublicHostState.Recovering,
                LastFailureCode: PublicRecoveryDetailCode.HostContractMismatch,
            });

        var error = DecodeRecovery(
            await rig.DispatchJobListAsync().WaitAsync(TestTimeout));
        Assert.Equal(PublicRecoveryDetailCode.HostContractMismatch, error.DetailCode);
        Assert.False(error.Retryable);
        Assert.Null(error.RetryAfterMilliseconds);
        Assert.Null(error.RecoveryPhase);
        Assert.Null(error.RecoveryAttempt);
        Assert.Null(error.RetryGate);
    }

    [Fact]
    public async Task Contract_mismatch_during_prewrite_loss_beats_retry_guidance()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond, AutoConfirmContainment: false));
        await rig.StartAsync();
        var current = rig.Factory.Resources[0];
        var blocked = rig.Observer.BlockNextAuthorization();

        var dispatch = rig.DispatchJobListAsync();
        await blocked.WaitAsync(TestTimeout);
        await current.InjectContractMismatchAsync();
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host is
            {
                State: PublicHostState.Recovering,
                LastFailureCode: PublicRecoveryDetailCode.HostContractMismatch,
            });
        rig.Observer.ReleaseAuthorization();

        var error = DecodeRecovery(await dispatch.WaitAsync(TestTimeout));
        Assert.Equal(PublicRecoveryDetailCode.HostContractMismatch, error.DetailCode);
        Assert.False(error.Retryable);
        Assert.Null(error.RetryAfterMilliseconds);
        Assert.Null(error.RecoveryPhase);
        Assert.Null(error.RecoveryAttempt);
        Assert.Null(error.RetryGate);
        Assert.Equal(0, current.OperationCount);
    }

    [Fact]
    public async Task Loss_after_private_write_is_outcome_unknown_and_never_replayed()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.CrashAfterRequest, AutoConfirmContainment: false),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];

        var result = await rig.DispatchJobListAsync().WaitAsync(TestTimeout);

        var error = DecodeRecovery(result);
        Assert.Equal(PublicRecoveryDetailCode.OutcomeUnknown, error.DetailCode);
        Assert.False(error.Retryable);
        Assert.Null(error.RetryAfterMilliseconds);
        Assert.Null(error.RecoveryPhase);
        Assert.Null(error.RecoveryAttempt);
        Assert.Null(error.RetryGate);
        Assert.Equal(1, old.OperationCount);
        Assert.Single(rig.Factory.Resources);
        Assert.Equal(
            [
                "call.accepted",
                GuardianAuditCall.DispatchAuthorizedEvent,
                GuardianAuditCall.DispatchOutcomeUnknownEvent,
                "call.failed",
            ],
            rig.AuditEventTypes());

        old.ConfirmContainment();
        await WaitUntilAsync(() => rig.Factory.Resources.Count == 2);
        Assert.Equal(0, rig.Factory.Resources[1].OperationCount);
    }

    [Fact]
    public async Task Decoded_terminal_wins_over_immediately_following_host_loss()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond, AutoConfirmContainment: false),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];
        rig.Observer.OnDecoded = old.Crash;

        var result = await rig.DispatchJobListAsync().WaitAsync(TestTimeout);
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State == PublicHostState.Recovering);

        Assert.False(result.IsError);
        Assert.Equal("jobs-generation-1", result.Text);
        Assert.Equal(1, old.OperationCount);
        Assert.Single(rig.Factory.Resources);
        Assert.Equal(
            [
                "call.accepted",
                GuardianAuditCall.DispatchAuthorizedEvent,
                GuardianAuditCall.DispatchCompletedEvent,
                "call.completed",
            ],
            rig.AuditEventTypes());
        old.ConfirmContainment();
    }

    [Fact]
    public async Task Foreground_output_is_registered_ingested_and_decorated_before_delivery()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            new AttemptPlan(HostBehavior.ForegroundOutput));
        await rig.StartAsync();
        var expected = Encoding.UTF8.GetBytes("foreground exact output");

        var result = await rig.DispatchSessionOperationAsync(
                ForegroundOperation(rig.Clock, Token(31)),
                OperationIdentity)
            .WaitAsync(TestTimeout);

        Assert.False(result.IsError);
        Assert.Contains("foreground shaped", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            GuardianOutputResponseDecorator.GenericUnavailableMarker,
            result.Text,
            StringComparison.Ordinal);
        const string marker = "recovery=available: ptk_output handle=";
        var markerIndex = result.Text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0);
        var handle = result.Text[(markerIndex + marker.Length)..];
        var read = rig.OutputStore!.Read(handle, 0, OutputStore.MaximumReadBytes);
        Assert.Equal(expected, Encoding.UTF8.GetBytes(read.Text));
        Assert.Equal(0, rig.OutputCoordinator!.ActiveCapabilityCount);
        Assert.Equal(0, rig.OutputCoordinator.TrackedCount);
    }

    [Fact]
    public async Task Output_operation_without_guardian_owner_is_rejected_before_dispatch()
    {
        await using var rig = new TestRig(new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.DispatchSessionOperationAsync(
                ForegroundOperation(rig.Clock, Token(35)),
                OperationIdentity));

        Assert.Equal(0, rig.Factory.Resources[0].OperationCount);
        Assert.Equal(0, rig.Supervisor.OutstandingCallCount);
    }

    [Fact]
    public async Task Background_invoke_without_guardian_job_owner_is_rejected_before_dispatch()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            enableJobs: false,
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.DispatchBackgroundInvokeAsync(Token(36)));

        Assert.Equal(0, rig.Factory.Resources[0].OperationCount);
        Assert.Equal(0, rig.OutputCoordinator!.TrackedCount);
        Assert.Equal(0, rig.Supervisor.OutstandingCallCount);
    }

    [Fact]
    public async Task Preassigned_background_id_cannot_enter_the_generic_dispatch_seam()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            rig.DispatchSessionOperationAsync(
                PreassignedBackgroundOperation(
                    rig.Clock,
                    Token(37),
                    new PublicJobId(900)),
                OperationIdentity));

        Assert.Equal(0, rig.Factory.Resources[0].OperationCount);
        Assert.Equal(0, rig.JobCapabilities!.TrackedCount);
        Assert.Equal(0, rig.OutputCoordinator!.TrackedCount);
    }

    [Fact]
    public async Task Failed_background_response_cancels_the_pending_job_reservation()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            new AttemptPlan(HostBehavior.RejectOperation));
        await rig.StartAsync();

        var result = await rig.DispatchBackgroundInvokeAsync(Token(38))
            .WaitAsync(TestTimeout);

        Assert.True(result.IsError);
        Assert.Equal(
            $"private_host_error:{GuardianHostPrivateDetailCode.SessionBusy}",
            result.Text);
        Assert.Equal(1, rig.Factory.Resources[0].OperationCount);
        Assert.Equal(0, rig.JobCapabilities!.TrackedCount);
        Assert.Equal(0, rig.OutputCoordinator!.TrackedCount);
        Assert.Equal(0, rig.OutputCoordinator.ActiveCapabilityCount);
    }

    [Fact]
    public async Task Ambiguous_background_loss_cancels_the_pending_job_reservation()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            new AttemptPlan(HostBehavior.CrashAfterRequest),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();

        var result = await rig.DispatchBackgroundInvokeAsync(Token(39))
            .WaitAsync(TestTimeout);

        var error = DecodeRecovery(result);
        Assert.Equal(PublicRecoveryDetailCode.OutcomeUnknown, error.DetailCode);
        Assert.Equal(1, rig.Factory.Resources[0].OperationCount);
        Assert.Equal(0, rig.JobCapabilities!.TrackedCount);
        Assert.Equal(0, rig.OutputCoordinator!.TrackedCount);
        Assert.Equal(0, rig.OutputCoordinator.ActiveCapabilityCount);
    }

    [Fact]
    public async Task Output_admission_failure_cancels_the_unwritten_job_reservation()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();

        var result = await rig.DispatchBackgroundInvokeAsync(
                Token(40),
                outputExpiresUnixTimeMilliseconds:
                    rig.Clock.GetUtcNow().ToUnixTimeMilliseconds())
            .WaitAsync(TestTimeout);

        Assert.True(result.IsError);
        Assert.Contains("reserve output recovery", result.Text, StringComparison.Ordinal);
        Assert.Equal(0, rig.Factory.Resources[0].OperationCount);
        Assert.Equal(0, rig.JobCapabilities!.TrackedCount);
        Assert.Equal(0, rig.OutputCoordinator!.TrackedCount);
    }

    [Fact]
    public async Task Job_capacity_refusal_happens_before_output_or_private_write()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            enableJobs: true,
            maximumTrackedJobs: 1,
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        Assert.True(rig.JobCapabilities!.TryReserve(
            rig.Factory.Resources[0].Identity,
            TestRig.Alias,
            TestRig.Transition,
            TestRig.Worker,
            out var occupied,
            out _));

        var result = await rig.DispatchBackgroundInvokeAsync(Token(41))
            .WaitAsync(TestTimeout);

        Assert.True(result.IsError);
        Assert.Contains("public job identifier", result.Text, StringComparison.Ordinal);
        Assert.Equal(0, rig.Factory.Resources[0].OperationCount);
        Assert.Equal(1, rig.JobCapabilities.TrackedCount);
        Assert.Equal(0, rig.OutputCoordinator!.TrackedCount);
        Assert.True(rig.JobCapabilities.TryCancel(occupied!));
    }

    [Fact]
    public void Active_call_state_retains_job_correlation_but_no_script_operation()
    {
        var activeCall = typeof(GuardianHostSupervisor).GetNestedType(
            "ActiveCall",
            BindingFlags.NonPublic);

        Assert.NotNull(activeCall);
        var fields = activeCall!.GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Contains(
            fields,
            field => field.FieldType == typeof(GuardianJobRegistration));
        Assert.DoesNotContain(
            fields,
            field => field.FieldType == typeof(OperationRequest) ||
                typeof(GuardianHostOperation).IsAssignableFrom(field.FieldType) ||
                field.FieldType.Name == "BackgroundInvokeDispatch");
    }

    [Fact]
    public async Task Job_controls_use_the_exact_guardian_capability_and_output_owner()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            new AttemptPlan(HostBehavior.BackgroundJobControls));
        await rig.StartAsync();
        var resource = rig.Factory.Resources[0];
        var jobId = new PublicJobId(1);

        var started = await rig.DispatchBackgroundInvokeAsync(Token(50))
            .WaitAsync(TestTimeout);
        var status = await rig.DispatchJobStatusAsync(jobId)
            .WaitAsync(TestTimeout);
        var output = await rig.DispatchJobOutputAsync(jobId, Token(51), offset: 7)
            .WaitAsync(TestTimeout);
        var kill = await rig.DispatchJobKillAsync(jobId)
            .WaitAsync(TestTimeout);

        Assert.False(started.IsError);
        Assert.Equal("[job 1 started]", started.Text);
        Assert.False(status.IsError);
        Assert.Equal("status job 1", status.Text);
        Assert.False(output.IsError);
        Assert.Contains("job output shaped", output.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            GuardianOutputResponseDecorator.GenericUnavailableMarker,
            output.Text,
            StringComparison.Ordinal);
        const string marker = "recovery=available: ptk_output handle=";
        var markerIndex = output.Text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0);
        var handle = output.Text[(markerIndex + marker.Length)..];
        Assert.Equal(
            "job output at 7",
            rig.OutputStore!.Read(handle, 0, OutputStore.MaximumReadBytes).Text);
        Assert.False(kill.IsError);
        Assert.Equal("kill job 1", kill.Text);

        Assert.Collection(
            resource.JobControls,
            observed => AssertJobControl(
                observed,
                GuardianHostOperationKind.JobStatus,
                jobId,
                offset: 0),
            observed => AssertJobControl(
                observed,
                GuardianHostOperationKind.JobOutput,
                jobId,
                offset: 7),
            observed => AssertJobControl(
                observed,
                GuardianHostOperationKind.JobKill,
                jobId,
                offset: 0));
        Assert.Equal(1, rig.JobCapabilities!.ActiveCount);
    }

    [Fact]
    public async Task Public_job_dispatch_routes_every_frozen_action()
    {
        var session = new CanonicalAlias("build");
        await using var rig = new TestRig(
            session,
            enableOutput: true,
            new AttemptPlan(HostBehavior.BackgroundJobControls));
        await rig.StartAsync();
        var resource = rig.Factory.Resources[0];
        var jobId = new PublicJobId(1);

        Assert.False((await rig.DispatchBackgroundInvokeAsync(Token(54), session: session.Value)
            .WaitAsync(TestTimeout)).IsError);
        var list = await rig.DispatchPublicJobAsync("list", session: session.Value)
            .WaitAsync(TestTimeout);
        var status = await rig.DispatchPublicJobAsync("status", jobId, session: session.Value)
            .WaitAsync(TestTimeout);
        var output = await rig.DispatchPublicJobAsync(
                "output",
                jobId,
                offset: 11,
                session: session.Value)
            .WaitAsync(TestTimeout);
        var kill = await rig.DispatchPublicJobAsync("kill", jobId, session: session.Value)
            .WaitAsync(TestTimeout);

        Assert.False(list.IsError);
        Assert.Equal("jobs-generation-1", list.Text);
        Assert.False(status.IsError);
        Assert.Equal("status job 1", status.Text);
        Assert.False(output.IsError);
        Assert.Contains("job output shaped", output.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            GuardianOutputResponseDecorator.GenericUnavailableMarker,
            output.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "recovery=available: ptk_output handle=",
            output.Text,
            StringComparison.Ordinal);
        Assert.False(kill.IsError);
        Assert.Equal("kill job 1", kill.Text);
        Assert.Collection(
            resource.JobControls,
            observed => AssertJobControl(
                observed,
                GuardianHostOperationKind.JobStatus,
                jobId,
                offset: 0),
            observed => AssertJobControl(
                observed,
                GuardianHostOperationKind.JobOutput,
                jobId,
                offset: 11),
            observed => AssertJobControl(
                observed,
                GuardianHostOperationKind.JobKill,
                jobId,
                offset: 0));
    }

    [Fact]
    public async Task Public_foreground_invoke_routes_the_exact_admitted_request()
    {
        var session = new CanonicalAlias("build");
        await using var rig = new TestRig(
            session,
            enableOutput: true,
            new AttemptPlan(HostBehavior.ForegroundOutput));
        await rig.StartAsync();
        const string script = "Write-Output 'exact ☃'\nGet-Date";
        var deadline = rig.Clock.GetUtcNow().AddSeconds(37);

        var result = await rig.DispatchPublicInvokeAsync(
                script,
                raw: true,
                route: "rtk",
                background: false,
                timeoutSeconds: 37,
                session: session.Value)
            .WaitAsync(TestTimeout);

        Assert.False(result.IsError);
        Assert.Contains("foreground shaped", result.Text, StringComparison.Ordinal);
        AssertPublicInvoke(
            Assert.Single(rig.Factory.Resources[0].Invokes),
            GuardianHostOperationKind.InvokeForeground,
            script,
            raw: true,
            GuardianHostInvokeRoute.Rtk,
            session,
            deadline.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Public_background_invoke_reserves_the_guardian_job_and_exact_deadline()
    {
        var session = new CanonicalAlias("build");
        await using var rig = new TestRig(
            session,
            enableOutput: true,
            new AttemptPlan(HostBehavior.BackgroundJobControls));
        await rig.StartAsync();
        const string script = "Start-Sleep -Milliseconds 1; 'done'";
        var deadline = rig.Clock.GetUtcNow().AddSeconds(19);

        var result = await rig.DispatchPublicInvokeAsync(
                script,
                raw: false,
                route: "pwsh",
                background: true,
                timeoutSeconds: 19,
                session: session.Value)
            .WaitAsync(TestTimeout);

        Assert.False(result.IsError);
        Assert.Equal("[job 1 started]", result.Text);
        Assert.Equal(1, rig.JobCapabilities!.ActiveCount);
        AssertPublicInvoke(
            Assert.Single(rig.Factory.Resources[0].Invokes),
            GuardianHostOperationKind.InvokeBackground,
            script,
            raw: false,
            GuardianHostInvokeRoute.Pwsh,
            session,
            deadline.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Public_output_reads_searches_and_reports_guardian_local_artifacts()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            new AttemptPlan(HostBehavior.ForegroundOutput));
        await rig.StartAsync();
        var invoke = await rig.DispatchPublicInvokeAsync(
                "'capture'",
                raw: false,
                route: "auto",
                background: false,
                timeoutSeconds: 30,
                session: "default")
            .WaitAsync(TestTimeout);
        const string marker = "recovery=available: ptk_output handle=";
        var markerIndex = invoke.Text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0);
        var handle = invoke.Text[(markerIndex + marker.Length)..];
        var hostOperationCount = rig.Factory.Resources[0].OperationCount;

        var read = await rig.DispatchPublicOutputAsync(
                "read",
                handle,
                offset: 0,
                maximumBytes: 10)
            .WaitAsync(TestTimeout);
        var search = await rig.DispatchPublicOutputAsync(
                "search",
                handle,
                offset: 0,
                maximumBytes: 1024,
                pattern: "exact")
            .WaitAsync(TestTimeout);
        var status = await rig.DispatchPublicOutputAsync("status", handle)
            .WaitAsync(TestTimeout);

        Assert.False(read.IsError);
        Assert.Contains("action=read state=available", read.Text, StringComparison.Ordinal);
        Assert.Contains("bytes_returned=10", read.Text, StringComparison.Ordinal);
        Assert.EndsWith("foreground", read.Text, StringComparison.Ordinal);
        Assert.False(search.IsError);
        Assert.Contains("action=search state=available", search.Text, StringComparison.Ordinal);
        Assert.Contains("matches=1", search.Text, StringComparison.Ordinal);
        Assert.Contains("exact output", search.Text, StringComparison.Ordinal);
        Assert.False(status.IsError);
        Assert.Contains("action=status state=available", status.Text, StringComparison.Ordinal);
        Assert.Contains("complete=true", status.Text, StringComparison.Ordinal);
        Assert.Equal(hostOperationCount, rig.Factory.Resources[0].OperationCount);
        Assert.Equal(
            [
                "output.read_accessed",
                "call.completed",
                "call.accepted",
                "output.search_accessed",
                "call.completed",
                "call.accepted",
                "output.status_accessed",
                "call.completed",
            ],
            rig.AuditEventTypes()[^8..]);
    }

    [Fact]
    public async Task Public_output_rejects_raw_values_not_bound_to_audit_admission()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();

        var handleMismatch = await rig.DispatchPublicOutputAsync(
                "status",
                "actual-handle",
                admittedHandle: "different-handle")
            .WaitAsync(TestTimeout);
        var patternMismatch = await rig.DispatchPublicOutputAsync(
                "search",
                "same-handle",
                maximumBytes: 1024,
                pattern: "actual-pattern",
                admittedPattern: "different-pattern")
            .WaitAsync(TestTimeout);

        Assert.True(handleMismatch.IsError);
        Assert.True(patternMismatch.IsError);
        Assert.Equal(0, rig.Factory.Resources[0].OperationCount);
        Assert.DoesNotContain(
            rig.AuditEventTypes(),
            eventType => eventType.StartsWith("output.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_job_output_is_refused_before_output_reservation_or_write()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            new AttemptPlan(HostBehavior.BackgroundJobControls));
        await rig.StartAsync();

        var result = await rig.DispatchJobOutputAsync(
                new PublicJobId(404),
                Token(52),
                offset: 0)
            .WaitAsync(TestTimeout);

        Assert.True(result.IsError);
        Assert.Contains("does not own an active job", result.Text, StringComparison.Ordinal);
        Assert.Equal(0, rig.Factory.Resources[0].OperationCount);
        Assert.Equal(0, rig.OutputCoordinator!.TrackedCount);
        Assert.Equal(0, rig.OutputCoordinator.ActiveCapabilityCount);
    }

    [Fact]
    public async Task Stale_host_job_capability_is_refused_on_the_replacement_generation()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            new AttemptPlan(HostBehavior.BackgroundJobControls),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];
        Assert.False((await rig.DispatchBackgroundInvokeAsync(Token(53))
            .WaitAsync(TestTimeout)).IsError);

        old.Crash();
        await WaitUntilAsync(() =>
            rig.Factory.Resources.Count == 2 &&
            rig.Supervisor.SnapshotState().Host is
            {
                State: PublicHostState.Ready,
                Generation.Value: 2,
            });
        var result = await rig.DispatchJobStatusAsync(new PublicJobId(1))
            .WaitAsync(TestTimeout);

        Assert.True(result.IsError);
        Assert.Contains("does not own an active job", result.Text, StringComparison.Ordinal);
        Assert.Equal(1, old.OperationCount);
        Assert.Equal(0, rig.Factory.Resources[1].OperationCount);
        Assert.Equal(1, rig.JobCapabilities!.ActiveCount);
    }

    [Fact]
    public async Task Caller_supplied_job_capability_cannot_enter_generic_dispatch()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var callId = new CallId(Guid.CreateVersion7());
        var expires = rig.Clock.GetUtcNow().AddMinutes(1)
            .ToUnixTimeMilliseconds();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            rig.DispatchSessionOperationAsync(
                new JobStatusOperation(
                    callId,
                    new DispatchCapability(Token(206), callId, expires),
                    new PublicJobId(1),
                    Token(240)),
                operationIdentity: null));

        Assert.Equal(0, rig.Factory.Resources[0].OperationCount);
        Assert.Equal(0, rig.JobCapabilities!.TrackedCount);
    }

    [Fact]
    public async Task Successful_background_response_authorizes_late_output_after_pending_removal()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            new AttemptPlan(HostBehavior.BackgroundLateOutput));
        await rig.StartAsync();
        var resource = rig.Factory.Resources[0];

        var result = await rig.DispatchBackgroundInvokeAsync(Token(32))
            .WaitAsync(TestTimeout);

        Assert.False(result.IsError);
        Assert.Equal("[job 1 started]", result.Text);
        Assert.Equal(1, rig.JobCapabilities!.ActiveCount);
        Assert.True(rig.JobCapabilities.TryGetActive(
            new PublicJobId(1),
            out var jobCapability));
        Assert.Equal(Token(240), jobCapability!.JobCapability);
        Assert.True(jobCapability.MatchesOwner(
            resource.Identity,
            TestRig.Alias,
            TestRig.Transition,
            TestRig.Worker));
        Assert.Equal(1, rig.OutputCoordinator!.ActiveCapabilityCount);
        Assert.Equal(1, rig.OutputCoordinator.TrackedCount);

        resource.ReleaseOutput();
        await resource.OutputFinished.WaitAsync(TestTimeout);
        await WaitUntilAsync(() => rig.OutputCoordinator.TryGetJobRecovery(
            new PublicJobId(1),
            out _));

        Assert.True(rig.OutputCoordinator.TryGetJobRecovery(
            new PublicJobId(1),
            out var recovery));
        Assert.Equal(OutputArtifactState.Available, recovery!.State);
        var read = rig.OutputStore!.Read(
            recovery.Handle!,
            0,
            OutputStore.MaximumReadBytes);
        Assert.Equal("late background output", read.Text);
        Assert.Equal(0, rig.OutputCoordinator.ActiveCapabilityCount);
        Assert.Equal(0, rig.OutputCoordinator.TrackedCount);
    }

    [Fact]
    public async Task Host_loss_seals_background_prefix_into_guardian_job_recovery()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            new AttemptPlan(HostBehavior.BackgroundPrefixBeforeLoss),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var resource = rig.Factory.Resources[0];

        var result = await rig.DispatchBackgroundInvokeAsync(Token(33))
            .WaitAsync(TestTimeout);
        Assert.False(result.IsError);
        Assert.Equal("[job 1 started]", result.Text);
        Assert.Equal(1, rig.JobCapabilities!.ActiveCount);

        resource.ReleaseOutput();
        await resource.OutputFinished.WaitAsync(TestTimeout);
        Assert.False((await rig.DispatchJobListAsync().WaitAsync(TestTimeout)).IsError);
        resource.Crash();
        await WaitUntilAsync(() => rig.OutputCoordinator!.TryGetJobRecovery(
            new PublicJobId(1),
            out _));

        Assert.True(rig.OutputCoordinator!.TryGetJobRecovery(
            new PublicJobId(1),
            out var recovery));
        Assert.Equal(OutputArtifactState.Incomplete, recovery!.State);
        var read = rig.OutputStore!.Read(
            recovery.Handle!,
            0,
            OutputStore.MaximumReadBytes);
        Assert.Equal("surviving background prefix", read.Text);
    }

    [Fact]
    public async Task Ambiguous_foreground_loss_releases_coordinator_call_tracking()
    {
        await using var rig = new TestRig(
            enableOutput: true,
            new AttemptPlan(HostBehavior.CrashAfterRequest),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();

        var result = await rig.DispatchSessionOperationAsync(
                ForegroundOperation(rig.Clock, Token(34)),
                OperationIdentity)
            .WaitAsync(TestTimeout);

        var error = DecodeRecovery(result);
        Assert.Equal(PublicRecoveryDetailCode.OutcomeUnknown, error.DetailCode);
        Assert.Equal(0, rig.OutputCoordinator!.ActiveCapabilityCount);
        Assert.Equal(0, rig.OutputCoordinator.TrackedCount);
    }

    [Fact]
    public async Task Private_request_ids_remain_monotonic_across_replacement()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var first = rig.Factory.Resources[0];

        Assert.False((await rig.DispatchJobListAsync()).IsError);
        first.Crash();
        await WaitUntilAsync(() =>
            rig.Factory.Resources.Count == 2 &&
            rig.Supervisor.SnapshotState().Host is
            {
                State: PublicHostState.Ready,
                Generation.Value: 2,
            });
        Assert.False((await rig.DispatchJobListAsync()).IsError);

        var requestIds = rig.Factory.Resources
            .SelectMany(resource => resource.RequestIds)
            .ToArray();
        Assert.True(requestIds.Length >= 10);
        Assert.All(
            requestIds.Zip(requestIds.Skip(1)),
            pair => Assert.True(pair.First < pair.Second));
        Assert.Equal(1, first.OperationCount);
        Assert.Equal(1, rig.Factory.Resources[1].OperationCount);
    }

    [Fact]
    public async Task State_polling_is_guardian_local_and_scheduler_inert()
    {
        await using var rig = new TestRig(new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var scheduleCount = rig.Scheduler.ScheduleCount;
        var attemptCount = rig.Factory.Resources.Count;

        for (var index = 0; index < 100; index++)
        {
            var snapshot = await rig.ReadStateAsync();
            Assert.Equal(PublicHostState.Ready, snapshot.Host.State);
            Assert.True(snapshot.Host.ReadyForEffects);
        }

        Assert.Equal(scheduleCount, rig.Scheduler.ScheduleCount);
        Assert.Equal(attemptCount, rig.Factory.Resources.Count);
        Assert.Equal(0, rig.Factory.Resources[0].OperationCount);
    }

    [Fact]
    public async Task Host_loss_projects_last_known_ready_session_as_unavailable()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond, AutoConfirmContainment: false),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];

        old.Crash();
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host is
            {
                State: PublicHostState.Recovering,
                RecoveryPhase: RecoveryPhase.Containment,
            });

        var snapshot = rig.Supervisor.SnapshotState();
        var session = Assert.Single(snapshot.Sessions);
        Assert.Equal(PublicSessionState.Recovering, session.State);
        Assert.False(session.ReadyForEffects);
        Assert.Null(session.WorkerBootId);
        Assert.Null(session.Generation);
        Assert.Equal(snapshot.Host.RecoveryPhase, session.RecoveryPhase);
        Assert.Equal(snapshot.Host.RecoveryAttempt, session.RecoveryAttempt);
        Assert.Equal(snapshot.Host.RetryAfterMilliseconds, session.RetryAfterMilliseconds);
        Assert.True(session.WarmStateLost);
        Assert.Equal(BootstrapState.Pending, session.BootstrapState);

        old.ConfirmContainment();
    }

    [Fact]
    public async Task Ready_session_replacement_uses_frozen_prewrite_loss_evidence()
    {
        await using var rig = new TestRig(new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var blocked = rig.Observer.BlockNextAuthorization();

        var dispatch = rig.DispatchJobListAsync();
        await blocked.WaitAsync(TestTimeout);
        rig.Sessions.ReplaceReadyWorker(
            workerGeneration: 2,
            transitionVersion: 2,
            new PublicSessionStateSnapshot(
                TestRig.Alias,
                DesiredSessionState.Ready,
                PublicSessionState.Recovering,
                TestRig.Worker.BootId,
                TestRig.Worker.Generation,
                new SessionTransitionVersion(2),
                RecoveryPhase.Containment,
                recoveryAttempt: 7,
                retryAfterMilliseconds: 913,
                readyForEffects: false,
                lastFailureCode: null,
                warmStateLost: true,
                BootstrapState.Pending));
        rig.Observer.ReleaseAuthorization();

        var error = DecodeRecovery(await dispatch.WaitAsync(TestTimeout));
        Assert.Equal(PublicRecoveryDetailCode.BackendLostBeforeDispatch, error.DetailCode);
        Assert.True(error.Retryable);
        Assert.Equal(RecoveryPhase.Containment, error.RecoveryPhase);
        Assert.Equal(7, error.RecoveryAttempt);
        Assert.Equal(913, error.RetryAfterMilliseconds);
        var gate = Assert.IsType<SessionReadyGate>(error.RetryGate);
        Assert.Equal(TestRig.Alias, gate.Alias);
        Assert.Equal(0, rig.Factory.Resources[0].OperationCount);

        var state = await rig.ReadStateAsync();
        Assert.Equal(PublicHostState.Ready, state.Host.State);
        Assert.True(state.Host.ReadyForEffects);
        var session = Assert.Single(state.Sessions);
        Assert.Equal(PublicSessionState.Ready, session.State);
        Assert.True(session.ReadyForEffects);
        Assert.Equal(new WorkerGeneration(2), session.Generation);
        Assert.Equal(new SessionTransitionVersion(2), session.TransitionVersion);
        Assert.Null(session.RecoveryPhase);
        Assert.Equal(7, session.RecoveryAttempt);
        Assert.Null(session.RetryAfterMilliseconds);

        var fresh = await rig.DispatchJobListAsync().WaitAsync(TestTimeout);
        Assert.False(fresh.IsError);
        Assert.Equal("jobs-generation-1", fresh.Text);
        Assert.Equal(1, rig.Factory.Resources[0].OperationCount);
    }

    [Fact]
    public async Task Ready_session_replacement_without_evidence_is_recovery_unknown()
    {
        await using var rig = new TestRig(new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var blocked = rig.Observer.BlockNextAuthorization();

        var dispatch = rig.DispatchJobListAsync();
        await blocked.WaitAsync(TestTimeout);
        rig.Sessions.ReplaceReadyWorker(
            workerGeneration: 2,
            transitionVersion: 2,
            recoverySnapshot: null);
        rig.Observer.ReleaseAuthorization();

        var error = DecodeRecovery(await dispatch.WaitAsync(TestTimeout));
        Assert.Equal(PublicRecoveryDetailCode.SessionRecoveryUnknown, error.DetailCode);
        Assert.False(error.Retryable);
        Assert.Null(error.RecoveryPhase);
        Assert.Null(error.RecoveryAttempt);
        Assert.Null(error.RetryAfterMilliseconds);
        Assert.Null(error.RetryGate);
        Assert.Equal(0, rig.Factory.Resources[0].OperationCount);

        var state = await rig.ReadStateAsync();
        Assert.Equal(PublicHostState.Ready, state.Host.State);
        Assert.True(state.Host.ReadyForEffects);
        var session = Assert.Single(state.Sessions);
        Assert.Equal(PublicSessionState.Ready, session.State);
        Assert.True(session.ReadyForEffects);
        Assert.Equal(new WorkerGeneration(2), session.Generation);
        Assert.Equal(new SessionTransitionVersion(2), session.TransitionVersion);
        Assert.Null(session.RecoveryPhase);
        Assert.Equal(0, session.RecoveryAttempt);
        Assert.Null(session.RetryAfterMilliseconds);

        var fresh = await rig.DispatchJobListAsync().WaitAsync(TestTimeout);
        Assert.False(fresh.IsError);
        Assert.Equal("jobs-generation-1", fresh.Text);
        Assert.Equal(1, rig.Factory.Resources[0].OperationCount);
    }

    [Fact]
    public async Task Unconfirmed_containment_blocks_every_replacement()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond, AutoConfirmContainment: false),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];

        old.Crash();
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State == PublicHostState.Recovering);
        Assert.Single(rig.Factory.Resources);

        await rig.Scheduler.AdvanceAndCompleteAsync(
            GuardianHostLifecycleController.HostContainmentGrace);
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State ==
                PublicHostState.ContainmentUnconfirmed);
        Assert.Single(rig.Factory.Resources);

        old.ConfirmContainment();
        await WaitUntilAsync(() => rig.Factory.Resources.Count == 2);
        Assert.Equal(2, rig.Factory.Resources[1].Identity.HostGeneration.Value);
    }

    [Fact]
    public async Task Containment_watcher_uses_remaining_absolute_deadline()
    {
        await using var rig = new TestRig(
            new AttemptPlan(
                HostBehavior.Respond,
                AutoConfirmContainment: false,
                AdvanceClockOnContainment: TimeSpan.FromSeconds(3)));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];

        old.Crash();
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State == PublicHostState.Recovering &&
            (rig.Scheduler.HasPending(TimeSpan.FromSeconds(7)) ||
             rig.Scheduler.HasPending(
                 GuardianHostLifecycleController.HostContainmentGrace)));

        Assert.Equal(TimeSpan.FromSeconds(3).Ticks, rig.Clock.GetTimestamp());
        Assert.True(rig.Scheduler.HasPending(TimeSpan.FromSeconds(7)));
        Assert.False(rig.Scheduler.HasPending(
            GuardianHostLifecycleController.HostContainmentGrace));

        await rig.Scheduler.CompleteWithoutAdvancingAsync(TimeSpan.FromSeconds(7));
        await WaitUntilAsync(() =>
            rig.Scheduler.HasPending(TimeSpan.FromSeconds(7)));
        Assert.Equal(PublicHostState.Recovering,
            rig.Supervisor.SnapshotState().Host.State);
        Assert.Equal(TimeSpan.FromSeconds(3).Ticks, rig.Clock.GetTimestamp());

        await rig.Scheduler.AdvanceAndCompleteAsync(TimeSpan.FromSeconds(7));
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State ==
                PublicHostState.ContainmentUnconfirmed);
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, rig.Clock.GetTimestamp());
        Assert.Single(rig.Factory.Resources);
    }

    [Fact]
    public async Task Shutdown_claim_during_containment_disposal_prevents_replacement()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond, AutoConfirmContainment: false),
            new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];

        old.Crash();
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State == PublicHostState.Recovering);
        var disposeEntered = old.BlockDispose();
        old.ConfirmContainment();
        await disposeEntered.WaitAsync(TestTimeout);

        var shutdown = rig.Supervisor.ShutdownAsync();
        old.ReleaseDispose();
        await shutdown.WaitAsync(TestTimeout);

        Assert.Equal(PublicHostState.Stopped, rig.Supervisor.SnapshotState().Host.State);
        Assert.Single(rig.Factory.Resources);
    }

    [Fact]
    public async Task Terminal_unconfirmed_shutdown_disposes_attempt_watcher_ownership()
    {
        await using var rig = new TestRig(
            new AttemptPlan(HostBehavior.Respond, AutoConfirmContainment: false));
        await rig.StartAsync();
        var old = rig.Factory.Resources[0];

        old.Crash();
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State == PublicHostState.Recovering);
        await rig.Scheduler.AdvanceAndCompleteAsync(
            GuardianHostLifecycleController.HostContainmentGrace);
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State ==
                PublicHostState.ContainmentUnconfirmed);

        await rig.Supervisor.ShutdownAsync().WaitAsync(TestTimeout);

        Assert.Equal(
            PublicHostState.ContainmentUnconfirmed,
            rig.Supervisor.SnapshotState().Host.State);
        Assert.Equal(0, rig.Supervisor.OwnedAttemptWatcherSetCount);
        Assert.Equal(0, rig.Supervisor.OwnedClientCount);
        Assert.Equal(0, rig.Supervisor.BackgroundTaskCount);
        Assert.Null(rig.Supervisor.BackgroundFailure);
        Assert.Equal(0, rig.Scheduler.PendingCount);
        await WaitUntilAsync(() => rig.Scheduler.EntryCount == 0);
        Assert.False(old.IsDisposed);
        Assert.Single(rig.Factory.Resources);
    }

    [Fact]
    public async Task Failed_generations_open_a_bounded_circuit_without_poll_driven_probes()
    {
        var plans = new List<AttemptPlan>
        {
            new(HostBehavior.Respond),
        };
        plans.AddRange(Enumerable.Repeat(
            new AttemptPlan(HostBehavior.ProvedNoChild),
            6));
        await using var rig = new TestRig(plans.ToArray());
        await rig.StartAsync();

        rig.Factory.Resources[0].Crash();
        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State == PublicHostState.Backoff);
        var backoffs = new[]
        {
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
        };
        for (var index = 0; index < backoffs.Length; index++)
        {
            await rig.Scheduler.AdvanceAndCompleteAllAsync(backoffs[index]);
            var expectedAttempts = index + 3;
            await WaitUntilAsync(() => rig.Factory.Resources.Count >= expectedAttempts);
        }

        await WaitUntilAsync(() =>
            rig.Supervisor.SnapshotState().Host.State == PublicHostState.CircuitOpen);
        Assert.Equal(7, rig.Factory.Resources.Count);
        var countBeforePolls = rig.Factory.Resources.Count;
        for (var index = 0; index < 100; index++)
            _ = rig.Supervisor.SnapshotState();
        Assert.Equal(countBeforePolls, rig.Factory.Resources.Count);
        var circuit = rig.Supervisor.SnapshotState().Host;
        Assert.Equal(RecoveryPhase.CircuitOpen, circuit.RecoveryPhase);
        Assert.Equal(7, circuit.RecoveryAttempt);
        Assert.False(circuit.ReadyForEffects);
    }

    [Fact]
    public async Task Attempt_watcher_ownership_is_bounded_across_one_hundred_recoveries()
    {
        await using var rig = new TestRig(new AttemptPlan(HostBehavior.Respond));
        await rig.StartAsync();

        for (var generation = 1; generation <= 101; generation++)
        {
            await WaitUntilAsync(() =>
                rig.Supervisor.SnapshotState().Host is
                {
                    State: PublicHostState.Ready,
                    Generation.Value: var currentGeneration,
                } && currentGeneration == generation);

            await rig.Scheduler.AdvanceAndCompleteAsync(
                RecoveryCircuitMachine.ReadyStabilityWindow);
            await WaitUntilAsync(() =>
                !rig.Supervisor.BackgroundTaskNames.Contains(
                    "WatchReadyStabilityAsync",
                    StringComparison.Ordinal));

            Assert.Equal(0, rig.Scheduler.PendingCount);
            await WaitUntilAsync(() =>
                rig.Scheduler.EntryCount == 0 &&
                rig.Supervisor.BackgroundTaskCount == 2 &&
                rig.Supervisor.OwnedClientCount == 1);
            Assert.Equal(1, rig.Supervisor.OwnedAttemptWatcherSetCount);
            Assert.Null(rig.Supervisor.BackgroundFailure);
            Assert.Equal(
                "WatchClientFatalAsync(active),WatchHostExitAsync(active)",
                rig.Supervisor.BackgroundTaskNames);

            var result = await rig.DispatchJobListAsync().WaitAsync(TestTimeout);
            Assert.False(result.IsError);
            Assert.Equal($"jobs-generation-{generation}", result.Text);

            if (generation <= 100)
                rig.Factory.Resources[generation - 1].Crash();
        }

        var resources = rig.Factory.Resources;
        Assert.Equal(101, resources.Count);
        Assert.Equal(
            Enumerable.Range(1, 101).Select(value => (long)value),
            resources.Select(value => value.Identity.HostGeneration.Value));
        Assert.All(resources.Take(100), value => Assert.True(value.IsDisposed));
        Assert.False(resources[^1].IsDisposed);
        Assert.Single(resources, value => !value.IsDisposed);
        Assert.All(resources, value => Assert.Equal(1, value.OperationCount));
        Assert.Equal(1, rig.Supervisor.OwnedClientCount);
        Assert.Equal(1, rig.Supervisor.OwnedAttemptWatcherSetCount);
        Assert.Equal(2, rig.Supervisor.BackgroundTaskCount);
        Assert.Equal(0, rig.Scheduler.PendingCount);
        Assert.Equal(0, rig.Scheduler.EntryCount);

        var requestIds = resources.SelectMany(value => value.RequestIds).ToArray();
        Assert.True(requestIds.Length >= 101);
        Assert.All(
            requestIds.Zip(requestIds.Skip(1)),
            pair => Assert.True(pair.First < pair.Second));
    }

    [Fact]
    public async Task Registry_is_bounded_and_shutdown_drains_every_written_call()
    {
        await using var rig = new TestRig(new AttemptPlan(HostBehavior.Hold));
        await rig.StartAsync();

        var calls = Enumerable.Range(0, GuardianHostSupervisor.MaximumOutstandingCalls)
            .Select(_ => rig.DispatchJobListAsync())
            .ToArray();
        await WaitUntilAsync(() =>
            rig.Supervisor.OutstandingCallCount ==
                GuardianHostSupervisor.MaximumOutstandingCalls &&
            rig.Factory.Resources[0].OperationCount ==
                GuardianHostSupervisor.MaximumOutstandingCalls);

        var refused = await rig.DispatchJobListAsync().WaitAsync(TestTimeout);
        Assert.True(refused.IsError);
        Assert.Contains("registry is full", refused.Text, StringComparison.Ordinal);

        var shutdown = rig.Supervisor.ShutdownAsync();
        var results = await Task.WhenAll(calls).WaitAsync(TestTimeout);
        await shutdown.WaitAsync(TestTimeout);

        Assert.All(results, result =>
        {
            var error = DecodeRecovery(result);
            Assert.Equal(PublicRecoveryDetailCode.OutcomeUnknown, error.DetailCode);
            Assert.False(error.Retryable);
        });
        Assert.Equal(0, rig.Supervisor.OutstandingCallCount);
        Assert.Equal(0, rig.Supervisor.SnapshotState().Host.RecoveryAttempt);
        Assert.Equal(PublicHostState.Stopped, rig.Supervisor.SnapshotState().Host.State);
    }

    private static InvokeForegroundOperation ForegroundOperation(
        TimeProvider clock,
        CapabilityToken outputToken)
    {
        var callId = new CallId(Guid.CreateVersion7());
        var expires = clock.GetUtcNow().AddMinutes(1).ToUnixTimeMilliseconds();
        return new InvokeForegroundOperation(
            callId,
            new DispatchCapability(Token(201), callId, expires),
            new OutputCapability(outputToken, maximumBytes: 1024, expires),
            "Write-Output secret",
            raw: false,
            GuardianHostInvokeRoute.Auto);
    }

    private static InvokeBackgroundOperation PreassignedBackgroundOperation(
        TimeProvider clock,
        CapabilityToken outputToken,
        PublicJobId publicJobId)
    {
        var callId = new CallId(Guid.CreateVersion7());
        var expires = clock.GetUtcNow().AddMinutes(1).ToUnixTimeMilliseconds();
        return new InvokeBackgroundOperation(
            callId,
            new DispatchCapability(Token(202), callId, expires),
            new OutputCapability(outputToken, maximumBytes: 1024, expires),
            "Write-Output secret",
            raw: false,
            GuardianHostInvokeRoute.Auto,
            publicJobId);
    }

    private static CapabilityToken Token(byte marker)
    {
        Span<byte> bytes = stackalloc byte[ContractLimits.CapabilityTokenBytes];
        bytes.Fill(marker);
        return new CapabilityToken(Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'));
    }

    private static void AssertJobControl(
        ObservedJobControl observed,
        GuardianHostOperationKind operationKind,
        PublicJobId publicJobId,
        long offset)
    {
        Assert.Equal(operationKind, observed.OperationKind);
        Assert.Equal(publicJobId, observed.PublicJobId);
        Assert.Equal(Token(240), observed.JobCapability);
        Assert.Equal(offset, observed.Offset);
    }

    private static void AssertPublicInvoke(
        ObservedInvoke observed,
        GuardianHostOperationKind operationKind,
        string script,
        bool raw,
        GuardianHostInvokeRoute route,
        CanonicalAlias session,
        long deadlineUnixTimeMilliseconds)
    {
        Assert.Equal(operationKind, observed.OperationKind);
        Assert.Equal(script, observed.Script);
        Assert.Equal(raw, observed.Raw);
        Assert.Equal(route, observed.Route);
        Assert.Equal(session, observed.SessionAlias);
        Assert.Equal(deadlineUnixTimeMilliseconds, observed.DeadlineUnixTimeMilliseconds);
        Assert.Equal(
            deadlineUnixTimeMilliseconds,
            observed.DispatchCapability.ExpiresUnixTimeMilliseconds);
        Assert.Equal(
            deadlineUnixTimeMilliseconds,
            observed.OutputCapability.ExpiresUnixTimeMilliseconds);
        Assert.Equal(1024, observed.OutputCapability.MaximumBytes);
        Assert.Equal(4, observed.OperationIdentity.PlanId.Value.Version);
        Assert.Equal(4, observed.OperationIdentity.OperationId.Value.Version);
    }

    private static PublicRecoveryError DecodeRecovery(GuardianToolResult result)
    {
        Assert.True(result.IsError);
        return PublicRecoveryCodec.Decode(Encoding.UTF8.GetBytes(result.Text));
    }

    private static IReadOnlyDictionary<string, JsonElement> JobListArguments()
    {
        using var document = JsonDocument.Parse("{\"action\":\"list\"}");
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["action"] = document.RootElement.GetProperty("action").Clone(),
        };
    }

    private static IReadOnlyDictionary<string, JsonElement> JobArguments(
        string action,
        PublicJobId? publicJobId,
        long offset,
        string session)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action"] = action,
            ["session"] = session,
        };
        if (publicJobId is not null)
            values["id"] = publicJobId.Value;
        if (StringComparer.Ordinal.Equals(action, "output"))
            values["offset"] = offset;

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(values));
        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, JsonElement> InvokeArguments(
        string script,
        bool raw,
        string route,
        bool background,
        int timeoutSeconds,
        string session)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["script"] = script,
            ["raw"] = raw,
            ["route"] = route,
            ["background"] = background,
            ["timeoutSeconds"] = timeoutSeconds,
            ["session"] = session,
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(values));
        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, JsonElement> OutputArguments(
        string action,
        string handle,
        long offset,
        int maximumBytes,
        string? pattern)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["handle"] = handle,
            ["action"] = action,
        };
        if (!StringComparer.Ordinal.Equals(action, "status"))
        {
            values["offset"] = offset;
            values["maxBytes"] = maximumBytes;
        }
        if (StringComparer.Ordinal.Equals(action, "search"))
            values["pattern"] = pattern;

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(values));
        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, JsonElement> EmptyArguments() =>
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        while (!predicate())
            await Task.Delay(5, cancellation.Token);
    }

    private sealed class TestRig : IAsyncDisposable
    {
        private readonly string? _outputRoot;
        private readonly R3FakeGuardianAuditRuntime _audit;

        internal static readonly GuardianBootId Guardian = new(
            Guid.Parse("11111111-1111-4111-8111-111111111111"));
        internal static readonly CanonicalAlias Alias = new("default");
        internal static readonly SessionTransitionVersion Transition = new(1);
        internal static readonly GuardianHostWorkerIdentity Worker = new(
            new WorkerBootId(Guid.Parse("22222222-2222-4222-8222-222222222222")),
            new WorkerGeneration(1));
        internal static readonly Sha256Digest ExecutableDigest = Digest('1');
        internal static readonly Sha256Digest BuildDigest = Digest('2');
        internal static readonly Sha256Digest ContractDigest = Digest('3');
        internal static readonly Sha256Digest ConfigurationDigest = Digest('4');
        internal static readonly Sha256Digest CatalogDigest = Digest('5');
        internal static readonly Sha256Digest PackageDigest = Digest('6');
        internal static readonly Sha256Digest BindingDigest = Digest('7');

        internal TestRig(params AttemptPlan[] plans)
            : this(enableOutput: false, enableJobs: false, plans)
        {
        }

        internal TestRig(bool enableOutput, params AttemptPlan[] plans)
            : this(enableOutput, enableJobs: enableOutput, plans)
        {
        }

        internal TestRig(
            bool enableOutput,
            bool enableJobs,
            params AttemptPlan[] plans)
            : this(
                enableOutput,
                enableJobs,
                GuardianJobCapabilityRegistry.DefaultMaximumTrackedJobs,
                Alias,
                plans)
        {
        }

        internal TestRig(
            CanonicalAlias sessionAlias,
            bool enableOutput,
            params AttemptPlan[] plans)
            : this(
                enableOutput,
                enableJobs: enableOutput,
                GuardianJobCapabilityRegistry.DefaultMaximumTrackedJobs,
                sessionAlias,
                plans)
        {
        }

        internal TestRig(
            bool enableOutput,
            bool enableJobs,
            int maximumTrackedJobs,
            params AttemptPlan[] plans)
            : this(
                enableOutput,
                enableJobs,
                maximumTrackedJobs,
                Alias,
                plans)
        {
        }

        internal TestRig(
            bool enableOutput,
            bool enableJobs,
            int maximumTrackedJobs,
            CanonicalAlias sessionAlias,
            params AttemptPlan[] plans)
        {
            ArgumentNullException.ThrowIfNull(sessionAlias);
            Clock = new ManualTimeProvider();
            Scheduler = new ManualScheduler(Clock);
            Observer = new BlockingDispatchObserver();
            Sessions = new StaticSessionSource(sessionAlias);
            Factory = new FakeAttemptFactory(plans, CreatePins(), Clock);
            if (enableOutput)
            {
                _outputRoot = Path.Combine(
                    Path.GetTempPath(),
                    $"ptk-supervisor-output-{Guid.NewGuid():N}");
                OutputStore = new OutputStore(new OutputStoreOptions(
                    _outputRoot,
                    TimeSpan.FromMinutes(5),
                    TimeSpan.FromHours(1),
                    MaximumArtifactBytes: 1024,
                    MaximumSessionBytes: 8192,
                    MaximumAggregateBytes: 8192,
                    UtcNow: Clock.GetUtcNow));
                OutputCoordinator = new GuardianOutputCoordinator(
                    new GuardianOutputCapabilityRegistry(OutputStore, Clock));
            }
            if (enableJobs)
            {
                JobCapabilities = new GuardianJobCapabilityRegistry(
                    new MonotonicPublicJobIdAllocator(),
                    maximumTrackedJobs);
            }
            var hostSnapshots = new GuardianAuditHostSnapshotSource();
            _audit = R3FakeGuardianAuditRuntime.Create(Guardian, hostSnapshots);
            var lifecycle = new GuardianHostLifecycleController(
                Guardian,
                new MonotonicHostGenerationAllocator(),
                new RandomBootIdSource(),
                new FixedDeadlineSource(Clock),
                Factory,
                Clock,
                hostSnapshots);
            Supervisor = new GuardianHostSupervisor(
                Guardian,
                lifecycle,
                new FrozenManifestSource(sessionAlias),
                new MonotonicPrivateRequestIdAllocator(),
                Clock,
                Scheduler,
                Sessions,
                Observer,
                CreatePins(),
                Observer.BeforeClientFatalObservationAsync,
                OutputCoordinator,
                JobCapabilities,
                _audit.OutputProtector);
        }

        internal ManualTimeProvider Clock { get; }
        internal ManualScheduler Scheduler { get; }
        internal BlockingDispatchObserver Observer { get; }
        internal ITestSessionControl Sessions { get; }
        internal FakeAttemptFactory Factory { get; }
        internal GuardianHostSupervisor Supervisor { get; }
        internal OutputStore? OutputStore { get; }
        internal GuardianOutputCoordinator? OutputCoordinator { get; }
        internal GuardianJobCapabilityRegistry? JobCapabilities { get; }

        internal string[] AuditEventTypes() => _audit.Sink.Lines
            .Select(static line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement
                    .GetProperty("event_type")
                    .GetString()!;
            })
            .ToArray();

        internal Task StartAsync() => Supervisor.StartAsync();

        internal Task<GuardianToolResult> DispatchJobListAsync() =>
            DispatchPublicAsync("ptk_job", "list", JobListArguments());

        internal Task<GuardianToolResult> DispatchPublicJobAsync(
            string action,
            PublicJobId? publicJobId = null,
            long offset = 0,
            string session = "default")
        {
            var arguments = JobArguments(action, publicJobId, offset, session);
            return DispatchAuditedAsync(
                Metadata(
                    "ptk_job",
                    action,
                    maximumCallRecordSlots: action == "output" ? 6 : 4,
                    jobId: publicJobId?.Value,
                    session: session),
                exactSubmittedScript: null,
                auditCall => Supervisor.DispatchAsync(
                    "ptk_job",
                    arguments,
                    auditCall,
                    CancellationToken.None));
        }

        internal Task<GuardianToolResult> DispatchPublicInvokeAsync(
            string script,
            bool raw,
            string route,
            bool background,
            int timeoutSeconds,
            string session)
        {
            var arguments = InvokeArguments(
                script,
                raw,
                route,
                background,
                timeoutSeconds,
                session);
            var timeout = TimeSpan.FromSeconds(timeoutSeconds);
            return DispatchAuditedAsync(
                Metadata(
                    "ptk_invoke",
                    "invoke",
                    maximumCallRecordSlots: 11,
                    persistentJobTerminalSlots: background ? 1 : 0,
                    background: background,
                    session: session,
                    raw: raw,
                    route: route,
                    timeoutMilliseconds: checked((long)timeout.TotalMilliseconds),
                    deadlineUtc: Clock.GetUtcNow().Add(timeout),
                    providedFields: arguments.Keys.Order(StringComparer.Ordinal).ToArray()),
                script,
                auditCall => Supervisor.DispatchAsync(
                    "ptk_invoke",
                    arguments,
                    auditCall,
                    CancellationToken.None));
        }

        internal Task<GuardianToolResult> DispatchPublicOutputAsync(
            string action,
            string handle,
            long offset = 0,
            int maximumBytes = OutputStore.DefaultReadBytes,
            string? pattern = null,
            string? admittedHandle = null,
            string? admittedPattern = null)
        {
            var arguments = OutputArguments(
                action,
                handle,
                offset,
                maximumBytes,
                pattern);
            var metadata = new AuditCallMetadata(
                new AuditActor
                {
                    Transport = "mcp_stdio",
                    AttributionStrength = "transport_only",
                },
                new AuditRequest
                {
                    Tool = "ptk_output",
                    Action = action,
                    ProvidedFields = arguments.Keys.Order(StringComparer.Ordinal).ToArray(),
                    SessionRequested = "default",
                    Offset = action == "status" ? null : offset,
                    MaxBytes = action == "status" ? null : maximumBytes,
                    PatternFingerprint = pattern is null
                        ? null
                        : _audit.OutputProtector.PatternFingerprint(
                            admittedPattern ?? pattern),
                    OutputHandleDigest = _audit.OutputProtector.HandleDigest(
                        admittedHandle ?? handle),
                },
                new AuditOperationProfile(
                    MaximumCallRecordSlots: 3,
                    PersistentJobTerminalSlots: 0,
                    RequiresScriptEvidence: false,
                    MayHaveSideEffects: false));
            return DispatchAuditedAsync(
                metadata,
                exactSubmittedScript: null,
                auditCall => Supervisor.DispatchAsync(
                    "ptk_output",
                    arguments,
                    auditCall,
                    CancellationToken.None));
        }

        internal Task<GuardianToolResult> DispatchSessionOperationAsync(
            GuardianHostOperation operation,
            GuardianHostOperationIdentity? operationIdentity)
        {
            ArgumentNullException.ThrowIfNull(operation);
            var invoke = operation as InvokeForegroundOperation;
            return DispatchAuditedAsync(
                invoke is null
                    ? Metadata("ptk_job", "list", maximumCallRecordSlots: 4)
                    : Metadata(
                        "ptk_invoke",
                        "invoke",
                        maximumCallRecordSlots: 11,
                        background: false,
                        raw: invoke.Raw,
                        route: invoke.Route.ToString().ToLowerInvariant(),
                        timeoutMilliseconds:
                            invoke.DispatchCapability.ExpiresUnixTimeMilliseconds -
                            Clock.GetUtcNow().ToUnixTimeMilliseconds(),
                        deadlineUtc: DateTimeOffset.FromUnixTimeMilliseconds(
                            invoke.DispatchCapability.ExpiresUnixTimeMilliseconds)),
                invoke?.Script,
                auditCall => Supervisor.DispatchSessionOperationAsync(
                    invoke is null
                        ? operation
                        : new InvokeForegroundOperation(
                            auditCall.PublicCallId,
                            RebindDispatch(
                                operation.DispatchCapability,
                                auditCall.PublicCallId),
                            invoke.OutputCapability!,
                            invoke.Script,
                            invoke.Raw,
                            invoke.Route),
                    operationIdentity,
                    auditCall,
                    CancellationToken.None));
        }

        internal Task<GuardianToolResult> DispatchBackgroundInvokeAsync(
            CapabilityToken outputToken,
            long? outputExpiresUnixTimeMilliseconds = null,
            string session = "default")
        {
            var expires = Clock.GetUtcNow().AddMinutes(1)
                .ToUnixTimeMilliseconds();
            const string script = "Write-Output secret";
            return DispatchAuditedAsync(
                Metadata(
                    "ptk_invoke",
                    "invoke",
                    maximumCallRecordSlots: 11,
                    persistentJobTerminalSlots: 1,
                    background: true,
                    session: session,
                    raw: false,
                    route: "auto",
                    timeoutMilliseconds:
                        expires - Clock.GetUtcNow().ToUnixTimeMilliseconds(),
                    deadlineUtc: DateTimeOffset.FromUnixTimeMilliseconds(expires)),
                script,
                auditCall => Supervisor.DispatchBackgroundInvokeAsync(
                    auditCall.PublicCallId,
                    new DispatchCapability(
                        Token(202),
                        auditCall.PublicCallId,
                        expires),
                    new OutputCapability(
                        outputToken,
                        maximumBytes: 1024,
                        outputExpiresUnixTimeMilliseconds ?? expires),
                    script,
                    raw: false,
                    GuardianHostInvokeRoute.Auto,
                    OperationIdentity,
                    auditCall,
                    CancellationToken.None));
        }

        internal Task<GuardianToolResult> DispatchJobStatusAsync(
            PublicJobId publicJobId)
        {
            return DispatchAuditedAsync(
                Metadata(
                    "ptk_job",
                    "status",
                    maximumCallRecordSlots: 4,
                    jobId: publicJobId.Value),
                exactSubmittedScript: null,
                auditCall => Supervisor.DispatchJobStatusAsync(
                    auditCall.PublicCallId,
                    NewDispatch(Token(203), auditCall.PublicCallId),
                    publicJobId,
                    auditCall,
                    CancellationToken.None));
        }

        internal Task<GuardianToolResult> DispatchJobOutputAsync(
            PublicJobId publicJobId,
            CapabilityToken outputToken,
            long offset)
        {
            return DispatchAuditedAsync(
                Metadata(
                    "ptk_job",
                    "output",
                    maximumCallRecordSlots: 6,
                    jobId: publicJobId.Value),
                exactSubmittedScript: null,
                auditCall =>
                {
                    var dispatch = NewDispatch(Token(204), auditCall.PublicCallId);
                    return Supervisor.DispatchJobOutputAsync(
                        auditCall.PublicCallId,
                        dispatch,
                        new OutputCapability(
                            outputToken,
                            maximumBytes: 1024,
                            dispatch.ExpiresUnixTimeMilliseconds),
                        publicJobId,
                        offset,
                        auditCall,
                        CancellationToken.None);
                });
        }

        internal Task<GuardianToolResult> DispatchJobKillAsync(
            PublicJobId publicJobId)
        {
            return DispatchAuditedAsync(
                Metadata(
                    "ptk_job",
                    "kill",
                    maximumCallRecordSlots: 4,
                    jobId: publicJobId.Value),
                exactSubmittedScript: null,
                auditCall => Supervisor.DispatchJobKillAsync(
                    auditCall.PublicCallId,
                    NewDispatch(Token(205), auditCall.PublicCallId),
                    publicJobId,
                    auditCall,
                    CancellationToken.None));
        }

        private DispatchCapability NewDispatch(
            CapabilityToken token,
            CallId callId)
        {
            var expires = Clock.GetUtcNow().AddMinutes(1)
                .ToUnixTimeMilliseconds();
            return new DispatchCapability(token, callId, expires);
        }

        internal async Task<PublicStateSnapshot> ReadStateAsync()
        {
            var result = await DispatchPublicAsync(
                "ptk_state",
                "state",
                EmptyArguments());
            Assert.False(result.IsError);
            return PublicStateCodec.Decode(Encoding.UTF8.GetBytes(result.Text));
        }

        private static DispatchCapability RebindDispatch(
            DispatchCapability source,
            CallId callId) => new(
                source.Token,
                callId,
                source.ExpiresUnixTimeMilliseconds);

        private static AuditCallMetadata Metadata(
            string tool,
            string action,
            int maximumCallRecordSlots,
            int persistentJobTerminalSlots = 0,
            bool? background = null,
            long? jobId = null,
            string session = "default",
            bool? raw = null,
            string? route = null,
            long? timeoutMilliseconds = null,
            DateTimeOffset? deadlineUtc = null,
            IReadOnlyList<string>? providedFields = null) => new(
                new AuditActor
                {
                    Transport = "mcp_stdio",
                    AttributionStrength = "transport_only",
                },
                new AuditRequest
                {
                    Tool = tool,
                    Action = action,
                    ProvidedFields = providedFields ?? [],
                    SessionRequested = session,
                    Background = background,
                    JobId = jobId,
                    Raw = raw,
                    Route = route,
                    TimeoutMs = timeoutMilliseconds,
                    DeadlineUtc = deadlineUtc,
                },
                new AuditOperationProfile(
                    maximumCallRecordSlots,
                    persistentJobTerminalSlots,
                    RequiresScriptEvidence: tool == "ptk_invoke",
                    MayHaveSideEffects: true));

        private async Task<GuardianToolResult> DispatchAuditedAsync(
            AuditCallMetadata metadata,
            string? exactSubmittedScript,
            Func<GuardianAuditCall, ValueTask<GuardianToolResult>> dispatch)
        {
            Assert.True(_audit.Runtime.TryBeginCall(
                metadata,
                exactSubmittedScript,
                out var admitted,
                out var lease,
                out var failure), failure);
            var auditCall = Assert.IsType<GuardianAuditCall>(admitted);
            using (lease!)
            {
                try
                {
                    var result = await dispatch(auditCall);
                    var authorizationRefused =
                        auditCall.AuthorizationPersistenceFailed &&
                        !auditCall.UserExecutionStarted;
                    if (!auditCall.TerminalWritten)
                    {
                        auditCall.CompleteFromFilter(
                            authorizationRefused || result.IsError
                                ? "failed"
                                : "completed",
                            authorizationRefused
                                ? 0
                                : Encoding.UTF8.GetByteCount(result.Text));
                    }
                    return authorizationRefused
                        ? new GuardianToolResult(
                            "audit_unavailable: the guardian could not persist dispatch authorization",
                            isError: true)
                        : result;
                }
                catch
                {
                    if (!auditCall.TerminalWritten)
                        auditCall.CompleteFromFilter("failed", 0);
                    throw;
                }
            }
        }

        private async Task<GuardianToolResult> DispatchPublicAsync(
            string tool,
            string action,
            IReadOnlyDictionary<string, JsonElement> arguments)
        {
            var metadata = new AuditCallMetadata(
                new AuditActor
                {
                    Transport = "mcp_stdio",
                    AttributionStrength = "transport_only",
                },
                new AuditRequest
                {
                    Tool = tool,
                    Action = action,
                    ProvidedFields = arguments.Keys.Order(StringComparer.Ordinal).ToArray(),
                    SessionRequested = "default",
                },
                new AuditOperationProfile(
                    MaximumCallRecordSlots: tool == "ptk_state" ? 5 : 4,
                    PersistentJobTerminalSlots: 0,
                    RequiresScriptEvidence: false,
                    MayHaveSideEffects: true));
            return await DispatchAuditedAsync(
                metadata,
                exactSubmittedScript: null,
                auditCall => Supervisor.DispatchAsync(
                    tool,
                    arguments,
                    auditCall,
                    CancellationToken.None));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (var resource in Factory.Resources)
                    resource.ConfirmContainment();
                try
                {
                    await Supervisor.ShutdownAsync().WaitAsync(TestTimeout);
                }
                catch (TimeoutException exception)
                {
                    var state = Supervisor.SnapshotState().Host;
                    throw new InvalidOperationException(
                        $"shutdown timed out in {state.State}/{state.RecoveryPhase}; " +
                        $"calls={Supervisor.OutstandingCallCount}; " +
                        $"background_tasks={Supervisor.BackgroundTaskCount}; " +
                        $"background_names={Supervisor.BackgroundTaskNames}; " +
                        $"background_failure={Supervisor.BackgroundFailure}",
                        exception);
                }
                await Supervisor.DisposeAsync();
            }
            finally
            {
                _audit.Dispose();
                JobCapabilities?.Dispose();
                OutputCoordinator?.Dispose();
                OutputStore?.Dispose();
                try
                {
                    if (_outputRoot is not null && Directory.Exists(_outputRoot))
                        Directory.Delete(_outputRoot, recursive: true);
                }
                catch
                {
                }
            }
        }

        private static GuardianHostSupervisorPins CreatePins() => new(
            ExecutableDigest,
            BuildDigest,
            ContractDigest,
            ConfigurationDigest,
            CatalogDigest,
            PackageDigest);

        private static Sha256Digest Digest(char value) => new(new string(value, 64));

        private sealed class FrozenManifestSource(CanonicalAlias alias) :
            IGuardianHostRecoveryManifestSource
        {
            public RecoveryManifest Create(GuardianHostIdentity identity)
            {
                var defaultAlias = new CanonicalAlias("default");
                CanonicalAlias[] aliases = alias == defaultAlias
                    ? [defaultAlias]
                    : new[] { alias, defaultAlias }
                        .OrderBy(value => value.Value, StringComparer.Ordinal)
                        .ToArray();
                return new RecoveryManifest(
                    Guardian,
                    identity.HostGeneration,
                    CatalogDigest,
                    ConfigurationDigest,
                    [],
                    aliases.Select(value => new RecoveryBinding(
                        value,
                        value == defaultAlias
                            ? RecoveryBindingKind.Default
                            : RecoveryBindingKind.Dynamic,
                        null,
                        null,
                        null,
                        false,
                        DesiredSessionState.Ready,
                        Transition,
                        BindingDigest)),
                    aliases.Select(value => new WorkerGenerationHighWatermarkEntry(
                        value,
                        new WorkerGenerationHighWatermark(1))),
                    identity.HostGeneration);
            }
        }

        internal interface ITestSessionControl : IGuardianHostSupervisorSessionSource
        {
            void ReplaceReadyWorker(
                long workerGeneration,
                long transitionVersion,
                PublicSessionStateSnapshot? recoverySnapshot);
        }

        private sealed class StaticSessionSource : ITestSessionControl
        {
            private readonly object _sync = new();
            private readonly CanonicalAlias _alias;
            private long _workerGeneration = 1;
            private long _transitionVersion = 1;
            private long _recoveryAttempt;
            private GuardianHostJobListTargetInvalidation? _invalidation;

            internal StaticSessionSource(CanonicalAlias alias)
            {
                _alias = alias ?? throw new ArgumentNullException(nameof(alias));
            }

            public IReadOnlyList<PublicSessionStateSnapshot> SnapshotSessions()
            {
                lock (_sync)
                {
                    return
                    [
                        new PublicSessionStateSnapshot(
                            _alias,
                            DesiredSessionState.Ready,
                            PublicSessionState.Ready,
                            Worker.BootId,
                            new WorkerGeneration(_workerGeneration),
                            new SessionTransitionVersion(_transitionVersion),
                            recoveryPhase: null,
                            _recoveryAttempt,
                            retryAfterMilliseconds: null,
                            readyForEffects: true,
                            lastFailureCode: null,
                            warmStateLost: false,
                            BootstrapState.Restored),
                    ];
                }
            }

            public bool TryGetJobListTarget(
                CanonicalAlias alias,
                [NotNullWhen(true)] out GuardianHostJobListTarget? target)
            {
                ArgumentNullException.ThrowIfNull(alias);
                lock (_sync)
                    target = _alias == alias ? CreateTargetLocked() : null;
                return target is not null;
            }

            public bool TryGetJobListTargetInvalidation(
                GuardianHostJobListTarget target,
                [NotNullWhen(true)] out GuardianHostJobListTargetInvalidation? invalidation)
            {
                ArgumentNullException.ThrowIfNull(target);
                lock (_sync)
                {
                    if (_invalidation is not null && _invalidation.AppliesTo(target))
                    {
                        invalidation = _invalidation;
                        return true;
                    }

                    invalidation = null;
                    return false;
                }
            }

            public void ReplaceReadyWorker(
                long workerGeneration,
                long transitionVersion,
                PublicSessionStateSnapshot? recoverySnapshot)
            {
                var nextGeneration = new WorkerGeneration(workerGeneration);
                var nextTransition = new SessionTransitionVersion(transitionVersion);
                lock (_sync)
                {
                    var invalidated = CreateTargetLocked();
                    _invalidation = recoverySnapshot is null
                        ? null
                        : new GuardianHostJobListTargetInvalidation(
                            invalidated,
                            recoverySnapshot);
                    _workerGeneration = nextGeneration.Value;
                    _transitionVersion = nextTransition.Value;
                    _recoveryAttempt = recoverySnapshot?.RecoveryAttempt ?? 0;
                }
            }

            private GuardianHostJobListTarget CreateTargetLocked()
            {
                var transition = new SessionTransitionVersion(_transitionVersion);
                var worker = new GuardianHostWorkerIdentity(
                    Worker.BootId,
                    new WorkerGeneration(_workerGeneration));
                var binding = new RecoveryBinding(
                    _alias,
                    _alias == new CanonicalAlias("default")
                        ? RecoveryBindingKind.Default
                        : RecoveryBindingKind.Dynamic,
                    null,
                    null,
                    null,
                    false,
                    DesiredSessionState.Ready,
                    transition,
                    BindingDigest);
                return new GuardianHostJobListTarget(
                    _alias,
                    transition,
                    worker,
                    new GuardianAuditSession(binding, worker.Generation),
                    readyForEffects: true);
            }
        }
    }

    private sealed class RandomBootIdSource : IGuardianHostBootIdSource
    {
        public HostBootId Next() => new(Guid.NewGuid());
    }

    private sealed class FixedDeadlineSource(ManualTimeProvider clock) :
        IGuardianHostStartupDeadlineSource
    {
        public GuardianHostStartupDeadline Next() => new(
            checked(clock.GetTimestamp() + TimeSpan.FromSeconds(30).Ticks));
    }

    private sealed class BlockingDispatchObserver :
        IGuardianHostSupervisorDispatchObserver
    {
        private readonly object _sync = new();
        private TaskCompletionSource<bool>? _entered;
        private TaskCompletionSource<bool>? _release;
        private TaskCompletionSource<bool>? _fatalEntered;
        private TaskCompletionSource<bool>? _fatalRelease;

        internal Action? OnDecoded { get; set; }

        internal Task BlockNextAuthorization()
        {
            lock (_sync)
            {
                _entered = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _release = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return _entered.Task;
            }
        }

        internal void ReleaseAuthorization()
        {
            lock (_sync)
                (_release ?? throw new InvalidOperationException()).TrySetResult(true);
        }

        internal Task BlockNextFatalObservation()
        {
            lock (_sync)
            {
                _fatalEntered = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _fatalRelease = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return _fatalEntered.Task;
            }
        }

        internal void ReleaseFatalObservation()
        {
            lock (_sync)
                _fatalRelease?.TrySetResult(true);
        }

        internal async ValueTask BeforeClientFatalObservationAsync(
            GuardianHostClientException failure,
            CancellationToken cancellationToken)
        {
            Task? release = null;
            lock (_sync)
            {
                if (_fatalEntered is not null && _fatalRelease is not null)
                {
                    _fatalEntered.TrySetResult(true);
                    release = _fatalRelease.Task;
                    _fatalEntered = null;
                }
            }
            if (release is not null)
                await release.WaitAsync(cancellationToken);
            _ = failure;
        }

        public async ValueTask BeforeWriteAuthorizationAsync(
            GuardianHostDispatchObservation observation,
            CancellationToken cancellationToken)
        {
            Task? release = null;
            lock (_sync)
            {
                if (_entered is not null && _release is not null)
                {
                    _entered.TrySetResult(true);
                    release = _release.Task;
                    _entered = null;
                }
            }
            if (release is not null)
                await release.WaitAsync(cancellationToken);
            _ = observation;
        }

        public void OnWriteStarting(GuardianHostDispatchObservation observation) =>
            _ = observation;

        public void OnTerminalDecoded(GuardianHostDispatchObservation observation)
        {
            _ = observation;
            OnDecoded?.Invoke();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utc = DateTimeOffset.UnixEpoch.AddDays(1);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow() => _utc;

        internal void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref _timestamp, duration.Ticks);
            _utc = _utc.Add(duration);
        }
    }

    private sealed class ManualScheduler(ManualTimeProvider clock) :
        IGuardianHostSupervisorScheduler
    {
        private readonly object _sync = new();
        private readonly List<ScheduledDelay> _delays = [];
        private int _scheduleCount;

        internal int ScheduleCount => Volatile.Read(ref _scheduleCount);

        internal int PendingCount
        {
            get
            {
                lock (_sync)
                    return _delays.Count(value => !value.Task.IsCompleted);
            }
        }

        internal int EntryCount
        {
            get { lock (_sync) return _delays.Count; }
        }

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var scheduled = new ScheduledDelay(delay, cancellationToken);
            lock (_sync) _delays.Add(scheduled);
            Interlocked.Increment(ref _scheduleCount);
            _ = scheduled.Task.ContinueWith(
                _ =>
                {
                    lock (_sync) _delays.Remove(scheduled);
                    scheduled.DisposeRegistration();
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            return new ValueTask(scheduled.Task);
        }

        internal async Task AdvanceAndCompleteAsync(TimeSpan delay)
        {
            await WaitUntilAsync(() => HasPending(delay));
            clock.Advance(delay);
            ScheduledDelay selected;
            lock (_sync)
            {
                selected = _delays.First(value =>
                    value.Delay == delay && !value.Task.IsCompleted);
            }
            selected.Complete();
        }

        internal async Task CompleteWithoutAdvancingAsync(TimeSpan delay)
        {
            await WaitUntilAsync(() => HasPending(delay));
            ScheduledDelay selected;
            lock (_sync)
            {
                selected = _delays.First(value =>
                    value.Delay == delay && !value.Task.IsCompleted);
            }
            selected.Complete();
        }

        internal async Task AdvanceAndCompleteAllAsync(TimeSpan delay)
        {
            await WaitUntilAsync(() => HasPending(delay));
            clock.Advance(delay);
            ScheduledDelay[] selected;
            lock (_sync)
            {
                selected = _delays.Where(value =>
                    value.Delay == delay && !value.Task.IsCompleted).ToArray();
            }
            foreach (var value in selected) value.Complete();
        }

        internal bool HasPending(TimeSpan delay)
        {
            lock (_sync)
                return _delays.Any(value =>
                    value.Delay == delay && !value.Task.IsCompleted);
        }

        private sealed class ScheduledDelay
        {
            private readonly TaskCompletionSource<bool> _completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenRegistration _registration;

            internal ScheduledDelay(TimeSpan delay, CancellationToken cancellationToken)
            {
                Delay = delay;
                _registration = cancellationToken.Register(() =>
                    _completion.TrySetCanceled(cancellationToken));
            }

            internal TimeSpan Delay { get; }
            internal Task Task => _completion.Task;

            internal void Complete()
            {
                _completion.TrySetResult(true);
            }

            internal void DisposeRegistration() => _registration.Dispose();
        }
    }

    private enum HostBehavior
    {
        Respond,
        ForegroundOutput,
        BackgroundLateOutput,
        BackgroundJobControls,
        BackgroundPrefixBeforeLoss,
        ContractMismatchHello,
        CrashAfterRequest,
        Hold,
        ProvedNoChild,
        RejectOperation,
    }

    private sealed record AttemptPlan(
        HostBehavior Behavior,
        bool AutoConfirmContainment = true,
        TimeSpan AdvanceClockOnContainment = default);

    private sealed record ObservedJobControl(
        GuardianHostOperationKind OperationKind,
        PublicJobId PublicJobId,
        CapabilityToken JobCapability,
        long Offset);

    private sealed record ObservedInvoke(
        GuardianHostOperationKind OperationKind,
        string Script,
        bool Raw,
        GuardianHostInvokeRoute Route,
        CanonicalAlias SessionAlias,
        long DeadlineUnixTimeMilliseconds,
        DispatchCapability DispatchCapability,
        OutputCapability OutputCapability,
        GuardianHostOperationIdentity OperationIdentity);

    private sealed class FakeAttemptFactory : IGuardianHostAttemptFactory
    {
        private readonly object _sync = new();
        private readonly Queue<AttemptPlan> _plans;
        private readonly GuardianHostSupervisorPins _pins;
        private readonly ManualTimeProvider _clock;
        private readonly List<FakeConnectedResources> _resources = [];

        internal FakeAttemptFactory(
            IEnumerable<AttemptPlan> plans,
            GuardianHostSupervisorPins pins,
            ManualTimeProvider clock)
        {
            _plans = new Queue<AttemptPlan>(plans);
            _pins = pins;
            _clock = clock;
        }

        internal IReadOnlyList<FakeConnectedResources> Resources
        {
            get { lock (_sync) return _resources.ToArray(); }
        }

        public IGuardianHostAttemptResources Prepare(
            GuardianHostIdentity identity,
            GuardianHostStartupDeadline startupDeadline)
        {
            AttemptPlan plan;
            lock (_sync)
            {
                plan = _plans.Count == 0
                    ? new AttemptPlan(HostBehavior.Respond)
                    : _plans.Dequeue();
                var resources = new FakeConnectedResources(
                    identity,
                    plan,
                    _pins,
                    _clock,
                    checked(100 + (int)identity.HostGeneration.Value));
                _resources.Add(resources);
                _ = startupDeadline;
                return resources;
            }
        }
    }

    private sealed class FakeConnectedResources :
        IGuardianHostConnectedAttemptResources
    {
        private readonly AttemptPlan _plan;
        private readonly ManualTimeProvider _clock;
        private readonly TestTransportStream _requests = new();
        private readonly TestTransportStream _events = new();
        private readonly TaskCompletionSource<bool> _hostExited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _containmentConfirmed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _disposeEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _outputRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _outputFinished = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly FakeHostPeer _peer;
        private ManualResetEventSlim? _disposeRelease;
        private int _launched;
        private int _closed;
        private int _disposed;

        internal FakeConnectedResources(
            GuardianHostIdentity identity,
            AttemptPlan plan,
            GuardianHostSupervisorPins pins,
            ManualTimeProvider clock,
            int hostProcessId)
        {
            Identity = identity;
            _plan = plan;
            _clock = clock;
            HostProcessId = hostProcessId;
            _peer = new FakeHostPeer(this, _requests, _events, pins);
        }

        internal GuardianHostIdentity Identity { get; }
        internal int OperationCount => _peer.OperationCount;
        internal IReadOnlyList<long> RequestIds => _peer.RequestIds;
        internal IReadOnlyList<ObservedJobControl> JobControls => _peer.JobControls;
        internal IReadOnlyList<ObservedInvoke> Invokes => _peer.Invokes;
        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        internal Task OutputFinished => _outputFinished.Task;

        internal Task InjectContractMismatchAsync() =>
            _peer.InjectContractMismatchAsync();

        internal Task InjectProtocolFatalAsync() =>
            _peer.InjectProtocolFatalAsync();

        internal void ReleaseOutput() => _outputRelease.TrySetResult(true);

        internal Task WaitForOutputReleaseAsync() => _outputRelease.Task;

        internal void SignalOutputFinished() => _outputFinished.TrySetResult(true);

        public Stream RequestStream => _requests;
        public Stream EventStream => _events;
        public int HostProcessId { get; }
        public Task HostExited => _hostExited.Task;
        public Task ContainmentConfirmed => _containmentConfirmed.Task;

        public GuardianHostLaunchOutcome Launch()
        {
            if (Interlocked.Exchange(ref _launched, 1) != 0)
                throw new InvalidOperationException("Attempt launched twice.");
            if (_plan.Behavior == HostBehavior.ProvedNoChild)
                return GuardianHostLaunchOutcome.ProvedNoChild;
            _peer.Start(_plan.Behavior);
            return GuardianHostLaunchOutcome.Started;
        }

        public void CloseTransport()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _outputRelease.TrySetResult(true);
            _requests.CompleteWriting();
            _events.CompleteWriting();
            _hostExited.TrySetResult(true);
        }

        public void BeginContainment(GuardianHostContainmentDeadline deadline)
        {
            _ = deadline;
            _clock.Advance(_plan.AdvanceClockOnContainment);
            if (_plan.AutoConfirmContainment)
                _containmentConfirmed.TrySetResult(true);
        }

        internal void Crash()
        {
            _events.CompleteWriting();
            _hostExited.TrySetResult(true);
        }

        internal void ConfirmContainment() =>
            _containmentConfirmed.TrySetResult(true);

        internal Task BlockDispose()
        {
            _disposeRelease = new ManualResetEventSlim();
            return _disposeEntered.Task;
        }

        internal void ReleaseDispose() => _disposeRelease?.Set();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _outputRelease.TrySetResult(true);
            _outputFinished.TrySetResult(true);
            _disposeEntered.TrySetResult(true);
            _disposeRelease?.Wait();
            _requests.Dispose();
            _events.Dispose();
            _hostExited.TrySetResult(true);
            _containmentConfirmed.TrySetResult(true);
        }
    }

    private sealed class FakeHostPeer
    {
        private readonly object _sync = new();
        private readonly FakeConnectedResources _owner;
        private readonly GuardianHostProtocolReader _reader;
        private readonly GuardianHostProtocolWriter _writer;
        private readonly GuardianHostSupervisorPins _pins;
        private readonly List<long> _requestIds = [];
        private readonly List<ObservedJobControl> _jobControls = [];
        private readonly List<ObservedInvoke> _invokes = [];
        private int _operationCount;
        private long _eventSequence;

        internal FakeHostPeer(
            FakeConnectedResources owner,
            Stream requests,
            Stream events,
            GuardianHostSupervisorPins pins)
        {
            _owner = owner;
            _reader = new GuardianHostProtocolReader(requests, GuardianHostPeer.Guardian);
            _writer = new GuardianHostProtocolWriter(events, GuardianHostPeer.Host);
            _pins = pins;
        }

        internal int OperationCount => Volatile.Read(ref _operationCount);
        internal IReadOnlyList<long> RequestIds
        {
            get { lock (_sync) return _requestIds.ToArray(); }
        }
        internal IReadOnlyList<ObservedJobControl> JobControls
        {
            get { lock (_sync) return _jobControls.ToArray(); }
        }
        internal IReadOnlyList<ObservedInvoke> Invokes
        {
            get { lock (_sync) return _invokes.ToArray(); }
        }

        internal void Start(HostBehavior behavior) =>
            _ = Task.Run(() => RunAsync(behavior));

        internal Task InjectContractMismatchAsync() =>
            _writer.WriteAsync(new GuardianHostHello(
                new GuardianBootId(Guid.Parse("99999999-9999-4999-8999-999999999999")),
                _owner.Identity.HostBootId,
                _owner.Identity.HostGeneration,
                _owner.HostProcessId,
                _pins.HostExecutableDigest,
                _pins.HostBuildDigest,
                _pins.PublicContractDigest,
                _pins.ConfigurationDigest)).AsTask();

        internal Task InjectProtocolFatalAsync() =>
            _writer.WriteAsync(new GuardianHostHello(
                _owner.Identity.GuardianBootId,
                _owner.Identity.HostBootId,
                _owner.Identity.HostGeneration,
                _owner.HostProcessId,
                _pins.HostExecutableDigest,
                _pins.HostBuildDigest,
                _pins.PublicContractDigest,
                _pins.ConfigurationDigest)).AsTask();

        private async Task RunAsync(HostBehavior behavior)
        {
            try
            {
                var identity = _owner.Identity;
                await _writer.WriteAsync(new GuardianHostHello(
                    identity.GuardianBootId,
                    identity.HostBootId,
                    identity.HostGeneration,
                    _owner.HostProcessId,
                    _pins.HostExecutableDigest,
                    _pins.HostBuildDigest,
                    behavior == HostBehavior.ContractMismatchHello
                        ? new Sha256Digest(new string('8', 64))
                        : _pins.PublicContractDigest,
                    _pins.ConfigurationDigest));
                if (behavior == HostBehavior.ContractMismatchHello)
                    return;

                var initialize = Assert.IsType<GuardianHostInitialize>(await ReadAsync());
                Record(initialize.RequestId);
                var header = Assert.IsType<ManifestHeaderRequest>(await ReadAsync());
                Record(header.RequestId);
                await _writer.WriteAsync(Success(
                    header.RequestId,
                    new ManifestHeaderAccepted(header.ManifestId)));

                using var transferred = new MemoryStream();
                for (var index = 0; index < header.ChunkCount; index++)
                {
                    using var chunk = Assert.IsType<ManifestChunkRequest>(await ReadAsync());
                    Record(chunk.RequestId);
                    var bytes = chunk.GetRawBytes();
                    await transferred.WriteAsync(bytes);
                    await _writer.WriteAsync(Success(
                        chunk.RequestId,
                        new ManifestChunkAccepted(
                            chunk.ManifestId,
                            chunk.ChunkIndex,
                            checked((int)transferred.Length))));
                }

                var seal = Assert.IsType<ManifestSealRequest>(await ReadAsync());
                Record(seal.RequestId);
                var transferredBytes = transferred.ToArray();
                var digest = Sha256Digest.Compute(transferredBytes);
                await _writer.WriteAsync(Success(
                    seal.RequestId,
                    new ManifestSealed(seal.ManifestId, digest, transferredBytes.Length)));
                await _writer.WriteAsync(new GuardianHostReady(
                    identity.GuardianBootId,
                    identity.HostBootId,
                    identity.HostGeneration,
                    initialize.RequestId,
                    seal.ManifestId,
                    digest,
                    _owner.HostProcessId));

                while (await ReadAsync() is { } message)
                {
                    if (message is not OperationRequest operation)
                        continue;
                    Record(operation.RequestId);
                    Interlocked.Increment(ref _operationCount);
                    RecordInvoke(operation);
                    if (behavior == HostBehavior.Hold)
                        continue;
                    if (behavior == HostBehavior.CrashAfterRequest)
                    {
                        _owner.Crash();
                        return;
                    }
                    if (behavior == HostBehavior.RejectOperation)
                    {
                        await _writer.WriteAsync(new GuardianHostErrorResponse(
                            identity.GuardianBootId,
                            identity.HostBootId,
                            identity.HostGeneration,
                            operation.RequestId,
                            new GuardianHostPrivateError(
                                GuardianHostPrivateDetailCode.SessionBusy)));
                        continue;
                    }

                    switch (operation.Operation)
                    {
                        case InvokeBackgroundOperation background
                            when behavior == HostBehavior.BackgroundJobControls:
                            await _writer.WriteAsync(Success(
                                operation.RequestId,
                                new OperationCompleted(new InvokeBackgroundResult(
                                    background.PublicJobId,
                                    Token(240)))));
                            continue;
                        case JobStatusOperation status:
                            RecordJobControl(status, offset: 0);
                            await _writer.WriteAsync(Success(
                                operation.RequestId,
                                new OperationCompleted(new JobStatusResult(
                                    $"status job {status.PublicJobId.Value}"))));
                            continue;
                        case JobOutputOperation output:
                        {
                            RecordJobControl(output, output.Offset);
                            var bytes = Encoding.UTF8.GetBytes(
                                $"job output at {output.Offset}");
                            await WriteOutputChunkAsync(operation, bytes);
                            await WriteOutputSealAsync(operation, bytes);
                            await _writer.WriteAsync(Success(
                                operation.RequestId,
                                new OperationCompleted(new JobOutputResult(
                                    "job output shaped" + Environment.NewLine +
                                    GuardianOutputResponseDecorator
                                        .GenericUnavailableMarker))));
                            continue;
                        }
                        case JobKillOperation kill:
                            RecordJobControl(kill, offset: 0);
                            await _writer.WriteAsync(Success(
                                operation.RequestId,
                                new OperationCompleted(new JobKillResult(
                                    $"kill job {kill.PublicJobId.Value}"))));
                            continue;
                    }

                    switch (behavior)
                    {
                        case HostBehavior.ForegroundOutput:
                        {
                            Assert.IsType<InvokeForegroundOperation>(operation.Operation);
                            var bytes = Encoding.UTF8.GetBytes("foreground exact output");
                            await WriteOutputChunkAsync(operation, bytes);
                            await WriteOutputSealAsync(operation, bytes);
                            await _writer.WriteAsync(Success(
                                operation.RequestId,
                                new OperationCompleted(new InvokeForegroundResult(
                                    "foreground shaped" + Environment.NewLine +
                                    GuardianOutputResponseDecorator.GenericUnavailableMarker))));
                            break;
                        }
                        case HostBehavior.BackgroundLateOutput
                            when operation.Operation is InvokeBackgroundOperation:
                        case HostBehavior.BackgroundPrefixBeforeLoss
                            when operation.Operation is InvokeBackgroundOperation:
                        {
                            var background = Assert.IsType<InvokeBackgroundOperation>(
                                operation.Operation);
                            await _writer.WriteAsync(Success(
                                operation.RequestId,
                                new OperationCompleted(new InvokeBackgroundResult(
                                    background.PublicJobId,
                                    Token(240)))));
                            await _owner.WaitForOutputReleaseAsync();
                            var text = behavior == HostBehavior.BackgroundLateOutput
                                ? "late background output"
                                : "surviving background prefix";
                            var bytes = Encoding.UTF8.GetBytes(text);
                            await WriteOutputChunkAsync(operation, bytes);
                            if (behavior == HostBehavior.BackgroundPrefixBeforeLoss)
                            {
                                _owner.SignalOutputFinished();
                                break;
                            }
                            await WriteOutputSealAsync(operation, bytes);
                            _owner.SignalOutputFinished();
                            break;
                        }
                        default:
                            await _writer.WriteAsync(Success(
                                operation.RequestId,
                                new OperationCompleted(new JobListResult(
                                    $"jobs-generation-{identity.HostGeneration.Value}"))));
                            break;
                    }
                }
            }
            catch (Exception exception) when (exception is
                IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }

        private async Task<GuardianHostMessage?> ReadAsync() =>
            await _reader.ReadAsync().ConfigureAwait(false);

        private GuardianHostSuccessResponse Success(
            PrivateRequestId requestId,
            GuardianHostSuccessPayload payload) => new(
                _owner.Identity.GuardianBootId,
                _owner.Identity.HostBootId,
                _owner.Identity.HostGeneration,
                requestId,
                payload);

        private async Task WriteOutputChunkAsync(
            OperationRequest request,
            byte[] bytes)
        {
            using var chunk = new OutputChunkEvent(
                request.GuardianBootId,
                request.HostBootId,
                request.HostGeneration,
                new HostEventSequence(Interlocked.Increment(ref _eventSequence)),
                request.RequestId,
                request.SessionAlias!,
                request.SessionTransitionVersion!,
                request.WorkerIdentity!,
                request.OperationIdentity,
                request.Operation.OutputCapability!.Token,
                chunkIndex: 0,
                offset: 0,
                bytes);
            await _writer.WriteAsync(chunk);
        }

        private Task WriteOutputSealAsync(
            OperationRequest request,
            byte[] bytes) =>
            _writer.WriteAsync(new OutputSealEvent(
                    request.GuardianBootId,
                    request.HostBootId,
                    request.HostGeneration,
                    new HostEventSequence(Interlocked.Increment(ref _eventSequence)),
                    request.RequestId,
                    request.SessionAlias!,
                    request.SessionTransitionVersion!,
                    request.WorkerIdentity!,
                    request.OperationIdentity,
                    request.Operation.OutputCapability!.Token,
                    GuardianHostOutputSealState.Complete,
                    bytes.Length,
                    Sha256Digest.Compute(bytes)))
                .AsTask();

        private void Record(PrivateRequestId requestId)
        {
            lock (_sync) _requestIds.Add(requestId.Value);
        }

        private void RecordJobControl(
            GuardianHostJobIdentityOperation operation,
            long offset)
        {
            lock (_sync)
            {
                _jobControls.Add(new ObservedJobControl(
                    operation.Kind,
                    operation.PublicJobId,
                    operation.JobCapability,
                    offset));
            }
        }

        private void RecordInvoke(OperationRequest request)
        {
            var invoke = request.Operation switch
            {
                InvokeForegroundOperation foreground => new ObservedInvoke(
                    foreground.Kind,
                    foreground.Script,
                    foreground.Raw,
                    foreground.Route,
                    request.SessionAlias!,
                    request.DeadlineUnixTimeMilliseconds!.Value,
                    foreground.DispatchCapability,
                    foreground.OutputCapability!,
                    request.OperationIdentity!),
                InvokeBackgroundOperation background => new ObservedInvoke(
                    background.Kind,
                    background.Script,
                    background.Raw,
                    background.Route,
                    request.SessionAlias!,
                    request.DeadlineUnixTimeMilliseconds!.Value,
                    background.DispatchCapability,
                    background.OutputCapability!,
                    request.OperationIdentity!),
                _ => null,
            };
            if (invoke is null) return;
            lock (_sync) _invokes.Add(invoke);
        }
    }

    private sealed class TestTransportStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
        private byte[]? _current;
        private int _currentOffset;
        private int _disposed;

        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => Volatile.Read(ref _disposed) == 0;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void CompleteWriting() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            while (_current is null || _currentOffset == _current.Length)
            {
                _current = null;
                _currentOffset = 0;
                try
                {
                    _current = await _chunks.Reader.ReadAsync(cancellationToken);
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
            _current.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            return count;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _chunks.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _chunks.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }
}
