using System.Collections.Immutable;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditCallContextTests : IDisposable
{
    private readonly List<string> _roots = [];

    public static TheoryData<InvokeDisposition, bool, bool, string, string, string, string> InvokeTerminals =>
        new()
        {
            { InvokeDisposition.Completed, true, false, "execution.completed", "completed", "confirmed", "complete" },
            { InvokeDisposition.Failed, false, false, "execution.failed", "failed", "confirmed", "complete" },
            { InvokeDisposition.Canceled, false, false, "execution.canceled", "canceled", "confirmed", "complete" },
            { InvokeDisposition.OutcomeUnknown, false, true, "execution.timed_out", "outcome_unknown", "unknown", "unknown" },
            { InvokeDisposition.OutcomeUnknown, false, false, "execution.outcome_unknown", "outcome_unknown", "unknown", "unknown" },
        };

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* Preserve the assertion failure that prevented cleanup. */ }
        }
    }

    [Theory]
    [MemberData(nameof(InvokeTerminals))]
    public async Task Invoke_terminal_dispositions_have_exact_event_and_certainty(
        InvokeDisposition disposition,
        bool success,
        bool timedOut,
        string terminalEvent,
        string terminalState,
        string certainty,
        string rootCoverage)
    {
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", "'terminal-matrix'")),
            exactScript: "'terminal-matrix'");
        Assert.True(fixture.Context.BeginValidation());
        Assert.True(await AuthorizePlanAndDispatchAsync(
            fixture.Context,
            ExecutionPlanner.CreateDirect(
                "'terminal-matrix'",
                "auto",
                compressAvailable: true,
                ResolutionContext.Warm),
            CancellationToken.None));

        fixture.Context.RecordInvokeResult(
            new InvokeResult(
                Success: success,
                Output: success ? "ok" : string.Empty,
                Errors: success ? [] : ["failed"],
                Warnings: [],
                TimedOut: timedOut,
                Disposition: disposition,
                UserExecutionStarted: true),
            success ? "ok" : "failed");

        var events = fixture.Events();
        Assert.Equal(
            [
                "call.accepted",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "execution.planned",
                "execution.dispatched",
                terminalEvent,
                success ? "call.completed" : "call.failed",
            ],
            events.Select(EventType));
        AssertOutcome(events[^2], terminalState, certainty, rootCoverage);
        AssertOutcome(
            events[^1],
            success ? "completed" : "failed",
            disposition == InvokeDisposition.OutcomeUnknown ? "unknown" : "not_applicable",
            "not_applicable");
    }

    [Fact]
    public async Task Invocation_audit_uses_plan_domain_not_effective_route()
    {
        const string script = "git diff | Set-Content patch.txt";
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", script)),
            exactScript: script);
        Assert.True(fixture.Context.BeginValidation());
        var plan = new ExecutionPlan(
            script,
            script,
            ExecutionDomain.MixedDataflow,
            ExecutionPath.PowerShellDirect,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.PowerShellObjects,
            ImmutableArray<ExecutionPath>.Empty,
            fallbackReason: null,
            rtkExecutableIdentity: null);

        Assert.True(await AuthorizePlanAndDispatchAsync(
            fixture.Context,
            plan,
            CancellationToken.None));
        fixture.Context.RecordInvokeResult(
            new InvokeResult(
                Success: true,
                Output: "ok",
                Errors: [],
                Warnings: [],
                TimedOut: false,
                Disposition: InvokeDisposition.Completed,
                UserExecutionStarted: true),
            "ok");

        var dispatched = fixture.Events()
            .Single(value => EventType(value) == "execution.dispatched");
        var routing = dispatched.GetProperty("routing");
        Assert.Equal("mixed_dataflow", routing.GetProperty("domain").GetString());
        Assert.Equal("auto", routing.GetProperty("requested_route").GetString());
        Assert.Equal("powershell_direct", routing.GetProperty("effective_route").GetString());
        Assert.Equal("powershell_objects", routing.GetProperty("provenance").GetString());
        Assert.Empty(routing.GetProperty("permitted_fallbacks").EnumerateArray());
    }

    [Fact]
    public async Task Rtk_fallback_audits_the_planned_route_then_the_actual_dispatch_and_terminals()
    {
        const string script = "git status";
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", script)),
            exactScript: script);
        Assert.True(fixture.Context.BeginValidation());
        var rtkPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "audit-rtk"));
        var plan = new ExecutionPlan(
            script,
            executionScript: null,
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            ImmutableArray.Create(ExecutionPath.PowerShellDirect),
            fallbackReason: null,
            new RtkExecutableIdentity(rtkPath),
            workingDirectory: Path.GetFullPath(Path.GetTempPath()),
            rtkArgumentVector: ["git", "status"],
            directFallbackProvenance: OutputProvenance.PowerShellObjects);

        Assert.True(await fixture.Context.AuthorizePlanAsync(
            plan,
            CancellationToken.None));
        var dispatch = ExecutionDispatch.RtkUnavailableFallback(plan);
        Assert.True(await fixture.Context.AuthorizeDispatchAsync(
            dispatch,
            CancellationToken.None));
        fixture.Context.RecordInvokeResult(
            new InvokeResult(
                Success: true,
                Output: "ok",
                Errors: [],
                Warnings: [],
                TimedOut: false,
                Disposition: InvokeDisposition.Completed,
                UserExecutionStarted: true),
            "ok");

        var events = fixture.Events();
        Assert.Equal(
            [
                "call.accepted",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "execution.planned",
                "execution.dispatched",
                "execution.completed",
                "call.completed",
            ],
            events.Select(EventType));

        var planned = events.Single(value => EventType(value) == "execution.planned");
        var planId = planned.GetProperty("correlation").GetProperty("plan_id").GetGuid();
        AssertRouting(
            planned,
            requestedRoute: "auto",
            effectiveRoute: "rtk",
            provenance: "rtk_unknown",
            fallbackReason: null);

        foreach (var actual in events.Where(value => EventType(value) is
                     "execution.dispatched" or "execution.completed" or "call.completed"))
        {
            Assert.Equal(
                planId,
                actual.GetProperty("correlation").GetProperty("plan_id").GetGuid());
            AssertRouting(
                actual,
                requestedRoute: "auto",
                effectiveRoute: "powershell_direct",
                provenance: "powershell_objects",
                fallbackReason: "rtk_executable_became_unavailable");
        }
    }

    [Fact]
    public async Task Rtk_dispatch_may_be_superseded_once_by_its_audited_exact_fallback()
    {
        const string script = "git status";
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", script)),
            exactScript: script);
        Assert.True(fixture.Context.BeginValidation());
        var plan = new ExecutionPlan(
            script,
            executionScript: null,
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            ImmutableArray.Create(ExecutionPath.PowerShellDirect),
            fallbackReason: null,
            new RtkExecutableIdentity(
                Path.GetFullPath(Path.Combine(Path.GetTempPath(), "audit-rtk"))),
            workingDirectory: Path.GetFullPath(Path.GetTempPath()),
            rtkArgumentVector: ["git", "status"],
            directFallbackProvenance: OutputProvenance.PowerShellObjects);

        Assert.True(await fixture.Context.AuthorizePlanAsync(plan, CancellationToken.None));
        Assert.True(await fixture.Context.AuthorizeDispatchAsync(
            ExecutionDispatch.FromPlan(plan),
            CancellationToken.None));
        var fallbackDispatch = ExecutionDispatch.RtkUnavailableFallback(plan);
        Assert.True(await fixture.Context.AuthorizeDispatchAsync(
            fallbackDispatch,
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Context.AuthorizeDispatchAsync(
                fallbackDispatch,
                CancellationToken.None));
        fixture.Context.RecordInvokeResult(
            new InvokeResult(
                Success: true,
                Output: "ok",
                Errors: [],
                Warnings: [],
                TimedOut: false,
                Disposition: InvokeDisposition.Completed,
                UserExecutionStarted: true),
            "ok");

        var events = fixture.Events();
        Assert.Equal(9, events.Count);
        var dispatched = events
            .Where(value => EventType(value) == "execution.dispatched")
            .ToArray();
        Assert.Equal(2, dispatched.Length);
        AssertRouting(
            dispatched[0],
            requestedRoute: "auto",
            effectiveRoute: "rtk",
            provenance: "rtk_unknown",
            fallbackReason: null);
        AssertRouting(
            dispatched[1],
            requestedRoute: "auto",
            effectiveRoute: "powershell_direct",
            provenance: "powershell_objects",
            fallbackReason: "rtk_executable_became_unavailable");
        Assert.Equal(
            "rtk_executable_became_unavailable",
            dispatched[1].GetProperty("outcome").GetProperty("detail_code").GetString());
        var planIds = events
            .Where(value => value.GetProperty("correlation").GetProperty("plan_id").ValueKind != JsonValueKind.Null)
            .Select(value => value.GetProperty("correlation").GetProperty("plan_id").GetGuid())
            .Distinct()
            .ToArray();
        Assert.Single(planIds);
    }

    [Fact]
    public async Task Failed_fallback_dispatch_append_restores_the_initial_rtk_routing()
    {
        const string script = "git status";
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", script)),
            exactScript: script,
            journalFault: (point, append) =>
                point == AuditSinkFaultPoint.BeforeAppend && append == 7);
        Assert.True(fixture.Context.BeginValidation());
        var plan = new ExecutionPlan(
            script,
            executionScript: null,
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            ImmutableArray.Create(ExecutionPath.PowerShellDirect),
            fallbackReason: null,
            new RtkExecutableIdentity(
                Path.GetFullPath(Path.Combine(Path.GetTempPath(), "audit-rtk"))),
            workingDirectory: Path.GetFullPath(Path.GetTempPath()),
            rtkArgumentVector: ["git", "status"],
            directFallbackProvenance: OutputProvenance.PowerShellObjects);

        Assert.True(await fixture.Context.AuthorizePlanAsync(plan, CancellationToken.None));
        Assert.True(await fixture.Context.AuthorizeDispatchAsync(
            ExecutionDispatch.FromPlan(plan),
            CancellationToken.None));
        Assert.False(await fixture.Context.AuthorizeDispatchAsync(
            ExecutionDispatch.RtkUnavailableFallback(plan),
            CancellationToken.None));

        var field = typeof(AuditCallContext).GetField(
            "_routing",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var routing = Assert.IsType<AuditRouting>(field.GetValue(fixture.Context));
        Assert.Equal("rtk", routing.EffectiveRoute);
        Assert.Equal("rtk_unknown", routing.Provenance);
        Assert.Null(routing.FallbackReason);
        var dispatched = fixture.Events()
            .Where(value => EventType(value) == "execution.dispatched")
            .ToArray();
        var initial = Assert.Single(dispatched);
        AssertRouting(
            initial,
            requestedRoute: "auto",
            effectiveRoute: "rtk",
            provenance: "rtk_unknown",
            fallbackReason: null);
    }

    [Fact]
    public async Task Cold_job_fallback_updates_started_call_and_terminal_routing()
    {
        const string script = "ptk-audit-target ARG";
        var root = Directory.CreateTempSubdirectory("ptk-cold-audit-");
        try
        {
            var (_, rtkPath) = RtkTestStub.CreatePassthrough(root.FullName);
            var (_, targetPath) = RtkTestStub.Create(
                OperatingSystem.IsWindows() ? "exit /b 0" : "exit 0",
                root.FullName,
                "ptk-audit-target");
            var rtkIdentity = RtkExecutableIdentity.TryCapture(rtkPath);
            Assert.NotNull(rtkIdentity);
            var targetIdentity = ColdCommandTargetIdentity.TryCapture(
                targetPath,
                new ResolvedCommand(
                    System.Management.Automation.CommandTypes.Application,
                    targetPath,
                    targetPath),
                root.FullName);
            Assert.NotNull(targetIdentity);
            var plan = new ExecutionPlan(
                script,
                executionScript: null,
                ExecutionDomain.NativeTerminal,
                ExecutionPath.Rtk,
                PreExecutionValidation.None,
                ResolutionContext.Cold,
                RequestedExecutionRoute.Rtk,
                OutputProvenance.RtkUnknown,
                ImmutableArray.Create(ExecutionPath.PowerShellDirect),
                fallbackReason: null,
                rtkIdentity,
                workingDirectory: root.FullName,
                rtkArgumentVector: [targetPath, "ARG"],
                directFallbackProvenance: OutputProvenance.DirectText,
                coldCommandTargetIdentity: targetIdentity);
            var initialDispatch = ExecutionDispatch.FromPlan(plan);
            using var jobs = new JobManager(Path.Combine(root.FullName, "jobs"));
            var initial = jobs.PrepareStart(initialDispatch, root.FullName);
            using var fixture = CreateFixture(
                Call(
                    "ptk_invoke",
                    ("script", script),
                    ("route", "rtk"),
                    ("background", true)),
                exactScript: script);

            var terminal = Assert.IsType<AuditJobTerminalLease>(
                await fixture.Context.AuthorizeJobStartAsync(
                    initial,
                    CancellationToken.None));
            var fallbackDispatch = ExecutionDispatch.RtkPreStartFallback(
                plan,
                ExecutionFallbackReason.RtkExecutionPreparationFailed);
            var fallback = jobs.BindDispatch(initial, fallbackDispatch, root.FullName);
            Assert.True(await fixture.Context.AuthorizeJobFallbackAsync(
                fallback,
                CancellationToken.None));
            Assert.True(fixture.Context.RecordJobStarted(fallback.Id, "started"));
            await terminal.CompleteAsync(new JobSnapshot(
                fallback.Id,
                Pid: 123,
                Running: false,
                ExitCode: 0,
                StartedUtc: DateTimeOffset.UtcNow,
                Script: script,
                OutputPath: fallback.OutputPath)
            {
                Execution = fallback.Execution,
                ExecutionRoutingAuthoritative = true,
                RootTerminationConfirmed = true,
                OutputCaptureComplete = true,
            });

            var events = fixture.Events();
            Assert.Equal(
                [
                    "call.accepted",
                    "job.start_requested",
                    "execution.validation_started",
                    "execution.prepare_authorized",
                    "execution.validation_completed",
                    "execution.planned",
                    "execution.dispatched",
                    "execution.dispatched",
                    "job.started",
                    "call.completed",
                    "job.completed",
                ],
                events.Select(EventType));
            AssertRouting(
                events[5],
                requestedRoute: "rtk",
                effectiveRoute: "rtk",
                provenance: "rtk_unknown",
                fallbackReason: null);
            AssertRouting(
                events[6],
                requestedRoute: "rtk",
                effectiveRoute: "rtk",
                provenance: "rtk_unknown",
                fallbackReason: null);
            foreach (var actual in events.Skip(7))
            {
                AssertRouting(
                    actual,
                    requestedRoute: "rtk",
                    effectiveRoute: "powershell_direct",
                    provenance: "direct_text",
                    fallbackReason: "rtk_execution_preparation_failed");
            }
            Assert.Single(
                events,
                value => EventType(value) == "execution.planned");
            Assert.Equal(2, events
                .Count(value => EventType(value) == "execution.dispatched"));
            Assert.Single(events
                .Select(value => value.GetProperty("correlation").GetProperty("plan_id"))
                .Where(value => value.ValueKind != JsonValueKind.Null)
                .Select(value => value.GetGuid())
                .Distinct());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Bash_validator_lifecycle_follows_dispatch_and_preserves_plan_routing()
    {
        const string script = "cat <<EOF\nhello\nEOF";
        var workingDirectory = Path.GetFullPath(Path.GetTempPath());
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", script)),
            exactScript: script);
        Assert.True(fixture.Context.BeginValidation());
        var plan = CreateBashPlan(script, workingDirectory);
        var dispatch = ExecutionDispatch.FromPlan(plan);

        Assert.True(await fixture.Context.AuthorizePlanAsync(plan, CancellationToken.None));
        Assert.True(await fixture.Context.AuthorizeDispatchAsync(dispatch, CancellationToken.None));
        Assert.True(await fixture.Context.RecordValidatorStartedAsync(
            dispatch,
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Context.RecordValidatorStartedAsync(
                dispatch,
                CancellationToken.None));
        var validation = new BashSyntaxValidationResult(
            BashSyntaxValidationStatus.Valid,
            ProcessStarted: true,
            ExitCode: 0,
            RootTerminationConfirmed: true);
        Assert.True(await fixture.Context.RecordValidatorCompletedAsync(
            dispatch,
            validation,
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Context.RecordValidatorCompletedAsync(
                dispatch,
                validation,
                CancellationToken.None));
        fixture.Context.RecordInvokeResult(
            new InvokeResult(
                Success: true,
                Output: "hello",
                Errors: [],
                Warnings: [],
                TimedOut: false,
                Disposition: InvokeDisposition.Completed,
                UserExecutionStarted: true),
            "hello");

        var events = fixture.Events();
        Assert.Equal(
            [
                "call.accepted",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "execution.planned",
                "execution.dispatched",
                "execution.validator_started",
                "execution.validator_completed",
                "execution.completed",
                "call.completed",
            ],
            events.Select(EventType));
        var planId = events
            .Single(value => EventType(value) == "execution.planned")
            .GetProperty("correlation")
            .GetProperty("plan_id")
            .GetGuid();
        foreach (var value in events.Where(value => EventType(value) is
                     "execution.planned" or
                     "execution.dispatched" or
                     "execution.validator_started" or
                     "execution.validator_completed" or
                     "execution.completed" or
                     "call.completed"))
        {
            Assert.Equal(
                planId,
                value.GetProperty("correlation").GetProperty("plan_id").GetGuid());
            var routing = value.GetProperty("routing");
            Assert.Equal("bash", routing.GetProperty("domain").GetString());
            Assert.Equal("bash_via_rtk", routing.GetProperty("effective_route").GetString());
            Assert.Equal("rtk_unknown", routing.GetProperty("provenance").GetString());
            Assert.Empty(routing.GetProperty("permitted_fallbacks").EnumerateArray());
            Assert.Equal(
                workingDirectory,
                value.GetProperty("request").GetProperty("cwd").GetString());
        }
        var dispatched = events.Single(value => EventType(value) == "execution.dispatched");
        Assert.Equal(
            plan.BashExecutableIdentity!.AuditIdentityCode,
            dispatched.GetProperty("outcome").GetProperty("detail_code").GetString());
        var started = events.Single(value => EventType(value) == "execution.validator_started");
        Assert.Equal(
            JsonValueKind.Null,
            started.GetProperty("outcome").GetProperty("detail_code").ValueKind);
        var completed = events.Single(value => EventType(value) == "execution.validator_completed");
        Assert.Equal(
            "bash_syntax_valid",
            completed.GetProperty("outcome").GetProperty("detail_code").GetString());
        Assert.Equal(
            "confirmed",
            completed.GetProperty("outcome").GetProperty("termination_certainty").GetString());
    }

    [Fact]
    public async Task Bash_syntax_rejection_is_a_validator_failure_and_user_execution_not_started()
    {
        const string script = "if true; then\necho missing-fi";
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", script)),
            exactScript: script);
        Assert.True(fixture.Context.BeginValidation());
        var plan = CreateBashPlan(script, Path.GetFullPath(Path.GetTempPath()));
        var dispatch = ExecutionDispatch.FromPlan(plan);
        Assert.True(await fixture.Context.AuthorizePlanAsync(plan, CancellationToken.None));
        Assert.True(await fixture.Context.AuthorizeDispatchAsync(dispatch, CancellationToken.None));
        Assert.True(await fixture.Context.RecordValidatorStartedAsync(
            dispatch,
            CancellationToken.None));
        Assert.True(await fixture.Context.RecordValidatorCompletedAsync(
            dispatch,
            new BashSyntaxValidationResult(
                BashSyntaxValidationStatus.SyntaxInvalid,
                ProcessStarted: true,
                ExitCode: 2,
                RootTerminationConfirmed: true),
            CancellationToken.None));
        fixture.Context.RecordInvokeResult(
            new InvokeResult(
                Success: false,
                Output: "not executed",
                Errors: [],
                Warnings: [],
                TimedOut: false,
                Disposition: InvokeDisposition.NotStarted,
                UserExecutionStarted: false)
            {
                AuditDetailCode = "bash_syntax_invalid",
            },
            "not executed");

        var events = fixture.Events();
        Assert.Contains("execution.validator_failed", events.Select(EventType));
        Assert.Contains("execution.not_started", events.Select(EventType));
        Assert.DoesNotContain("execution.failed", events.Select(EventType));
        var failed = events.Single(value => EventType(value) == "execution.validator_failed");
        Assert.Equal(
            "bash_syntax_invalid",
            failed.GetProperty("outcome").GetProperty("detail_code").GetString());
        Assert.Equal(2, failed.GetProperty("outcome").GetProperty("exit_code").GetInt64());
        Assert.Equal("failed", failed.GetProperty("outcome").GetProperty("state").GetString());
        var terminal = events.Single(value => EventType(value) == "execution.not_started");
        Assert.Equal(
            "bash_syntax_invalid",
            terminal.GetProperty("outcome").GetProperty("detail_code").GetString());
    }

    [Fact]
    public async Task Bash_validator_unconfirmed_stop_is_never_a_confirmed_audit_fact()
    {
        const string script = "cat <<EOF\nhello\nEOF";
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", script)),
            exactScript: script);
        Assert.True(fixture.Context.BeginValidation());
        var plan = CreateBashPlan(script, Path.GetFullPath(Path.GetTempPath()));
        var dispatch = ExecutionDispatch.FromPlan(plan);
        Assert.True(await fixture.Context.AuthorizePlanAsync(plan, CancellationToken.None));
        Assert.True(await fixture.Context.AuthorizeDispatchAsync(dispatch, CancellationToken.None));
        Assert.True(await fixture.Context.RecordValidatorStartedAsync(
            dispatch,
            CancellationToken.None));
        Assert.True(await fixture.Context.RecordValidatorCompletedAsync(
            dispatch,
            new BashSyntaxValidationResult(
                BashSyntaxValidationStatus.TimedOut,
                ProcessStarted: true,
                RootTerminationConfirmed: false),
            CancellationToken.None));

        var failed = fixture.Events().Single(value =>
            EventType(value) == "execution.validator_failed");
        var outcome = failed.GetProperty("outcome");
        Assert.Equal("timed_out", outcome.GetProperty("state").GetString());
        Assert.Equal("unconfirmed", outcome.GetProperty("termination_certainty").GetString());
        Assert.Equal(
            "unknown",
            failed.GetProperty("coverage").GetProperty("root_process_observed").GetString());
    }

    [Fact]
    public async Task Bash_dispatch_records_expected_identity_even_when_validator_never_starts()
    {
        const string script = "cat <<EOF\nhello\nEOF";
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", script)),
            exactScript: script);
        Assert.True(fixture.Context.BeginValidation());
        var plan = CreateBashPlan(script, Path.GetFullPath(Path.GetTempPath()));
        var dispatch = ExecutionDispatch.FromPlan(plan);
        Assert.True(await fixture.Context.AuthorizePlanAsync(plan, CancellationToken.None));
        Assert.True(await fixture.Context.AuthorizeDispatchAsync(dispatch, CancellationToken.None));
        Assert.True(await fixture.Context.RecordValidatorCompletedAsync(
            dispatch,
            new BashSyntaxValidationResult(
                BashSyntaxValidationStatus.StartFailed,
                ProcessStarted: false),
            CancellationToken.None));

        var events = fixture.Events();
        Assert.DoesNotContain(events, value =>
            EventType(value) == "execution.validator_started");
        var dispatched = events.Single(value => EventType(value) == "execution.dispatched");
        Assert.Equal(
            plan.BashExecutableIdentity!.AuditIdentityCode,
            dispatched.GetProperty("outcome").GetProperty("detail_code").GetString());
        var failed = events.Single(value => EventType(value) == "execution.validator_failed");
        var outcome = failed.GetProperty("outcome");
        Assert.Equal("not_started", outcome.GetProperty("state").GetString());
        Assert.Equal("not_applicable", outcome.GetProperty("termination_certainty").GetString());
        Assert.Equal(
            "none",
            failed.GetProperty("coverage").GetProperty("root_process_observed").GetString());
    }

    [Fact]
    public async Task Planned_but_undispatched_execution_records_a_no_start_terminal()
    {
        const string script = "git status";
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", script)),
            exactScript: script);
        Assert.True(fixture.Context.BeginValidation());
        var rtkPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "audit-rtk"));
        var plan = new ExecutionPlan(
            script,
            executionScript: null,
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            ImmutableArray.Create(ExecutionPath.PowerShellDirect),
            fallbackReason: null,
            new RtkExecutableIdentity(rtkPath),
            workingDirectory: Path.GetFullPath(Path.GetTempPath()),
            rtkArgumentVector: ["git", "status"],
            directFallbackProvenance: OutputProvenance.PowerShellObjects);

        Assert.True(await fixture.Context.AuthorizePlanAsync(
            plan,
            CancellationToken.None));
        fixture.Context.RecordInvokeResult(
            new InvokeResult(
                Success: false,
                Output: string.Empty,
                Errors: ["dispatch refused"],
                Warnings: [],
                TimedOut: false,
                Disposition: InvokeDisposition.NotStarted,
                UserExecutionStarted: false),
            "dispatch refused");

        var events = fixture.Events();
        Assert.Equal(
            [
                "call.accepted",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "execution.planned",
                "execution.not_started",
                "call.not_started",
            ],
            events.Select(EventType));
        Assert.DoesNotContain("execution.dispatched", events.Select(EventType));
        var notStarted = events.Single(value => EventType(value) == "execution.not_started");
        Assert.Equal(
            "dispatch_not_authorized",
            notStarted.GetProperty("outcome").GetProperty("detail_code").GetString());
        AssertOutcome(notStarted, "not_started", "not_applicable", "none");
    }

    [Fact]
    public void Preflight_refusal_has_no_execution_event_and_is_terminally_not_started()
    {
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", "'never-ran'")),
            exactScript: "'never-ran'");
        Assert.True(fixture.Context.BeginValidation());

        fixture.Context.RecordInvokeResult(
            new InvokeResult(
                Success: false,
                Output: string.Empty,
                Errors: ["refused"],
                Warnings: [],
                TimedOut: false,
                Disposition: InvokeDisposition.NotStarted,
                UserExecutionStarted: false),
            "refused");

        var events = fixture.Events();
        Assert.Equal(
            [
                "call.accepted",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "call.not_started",
            ],
            events.Select(EventType));
        AssertOutcome(events[^2], "not_started", "not_applicable", "none");
        AssertOutcome(events[^1], "not_started", "not_applicable", "not_applicable");
    }

    [Theory]
    [InlineData("ptk_reset", "reset.requested", "runspace.recycled")]
    [InlineData("ptk_state", "state.probe_requested", "state.probe_completed")]
    public void Control_calls_have_exact_requested_outcome_and_call_terminal_sequence(
        string tool,
        string requestedEvent,
        string completedEvent)
    {
        using var fixture = CreateFixture(Call(tool), exactScript: null);
        Assert.True(fixture.Context.AuthorizeControl(requestedEvent));
        fixture.Context.RecordControlOutcome(
            completedEvent,
            "completed",
            warmStateLost: tool == "ptk_reset");
        fixture.Context.CompleteCall("completed", "ok");

        var events = fixture.Events();
        Assert.Equal(
            ["call.accepted", requestedEvent, completedEvent, "call.completed"],
            events.Select(EventType));
        AssertOutcome(events[1], "requested", "not_applicable", "not_applicable");
        AssertOutcome(events[2], "completed", "confirmed", "not_applicable");
        AssertOutcome(events[3], "completed", "not_applicable", "not_applicable");
    }

    [Fact]
    public void Background_refusal_records_start_intent_before_validation_and_no_terminal_job_event()
    {
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", "'never-ran'"), ("background", true)),
            exactScript: "'never-ran'");

        Assert.True(fixture.Context.BeginJobStartRequest(17));
        fixture.Context.RecordJobNotStarted("dialect_refused", "refused", 17);

        Assert.Equal(
            [
                "call.accepted",
                "job.start_requested",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "job.not_started",
                "call.not_started",
            ],
            fixture.Events().Select(EventType));
    }

    [Fact]
    public void Background_refusal_persistence_failure_is_marked_for_generic_fail_closed_response()
    {
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", "'never-ran'"), ("background", true)),
            exactScript: "'never-ran'",
            journalFault: (point, append) => point == AuditSinkFaultPoint.Flush && append == 5);

        Assert.True(fixture.Context.BeginJobStartRequest(17));
        fixture.Context.RecordValidationNoStart("dialect_refused");
        Assert.True(fixture.Context.AuthorizationPersistenceFailed);

        fixture.Context.RecordJobNotStarted("dialect_refused", "must be replaced", 17);
        Assert.DoesNotContain("job.not_started", fixture.Events().Select(EventType));
    }

    [Fact]
    public async Task No_start_after_plan_authorization_does_not_duplicate_validation_completion()
    {
        const long jobId = 17;
        const string script = "'planned-but-not-dispatched'";
        using var fixture = CreateFixture(
            Call(
                "ptk_invoke",
                ("script", script),
                ("route", "pwsh"),
                ("background", true)),
            exactScript: script);
        Assert.True(fixture.Context.BeginJobStartRequest(jobId));
        var plan = ExecutionPlanner.CreateDirect(
            script,
            "pwsh",
            compressAvailable: false,
            ResolutionContext.Cold);
        Assert.True(await fixture.Context.AuthorizePlanAsync(
            plan,
            CancellationToken.None));

        fixture.Context.RecordValidationNoStart("prestart_deadline_expired");
        fixture.Context.RecordJobNotStarted(
            "prestart_deadline_expired",
            "not started",
            jobId);

        Assert.Equal(
            [
                "call.accepted",
                "job.start_requested",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "execution.planned",
                "job.not_started",
                "call.not_started",
            ],
            fixture.Events().Select(EventType));
    }

    [Fact]
    public async Task Unresolved_process_start_records_one_unknown_start_failure_and_no_job_terminal()
    {
        const long jobId = 17;
        const string response = "start outcome unknown";
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", "'maybe-ran'"), ("background", true)),
            exactScript: "'maybe-ran'");
        Assert.True(fixture.Context.BeginJobStartRequest(jobId));
        var terminal = Assert.IsType<AuditJobTerminalLease>(
            fixture.Context.AuthorizeJobStart(jobId, Path.GetFullPath(Path.GetTempPath())));

        fixture.Context.RecordJobStartOutcomeUnknown(jobId, response);
        terminal.ReleaseWithoutTerminal();
        await terminal.CompleteAsync(new JobSnapshot(
            jobId,
            Pid: 0,
            Running: false,
            ExitCode: null,
            StartedUtc: DateTimeOffset.UtcNow,
            Script: "'maybe-ran'",
            OutputPath: Path.Combine(Path.GetTempPath(), "ptk-unresolved-audit.log"))
        {
            StartOutcomeUnknown = true,
            ExecutionOutcomeUnknown = true,
            ExecutionOutcomeFailureCode = "process_start_outcome_unknown",
            RootTerminationConfirmed = false,
            OutputCaptureComplete = false,
        });

        var events = fixture.Events();
        Assert.Equal(
            [
                "call.accepted",
                "job.start_requested",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "execution.planned",
                "execution.dispatched",
                "job.start_failed",
                "call.failed",
            ],
            events.Select(EventType));
        var failed = events[^2];
        AssertOutcome(failed, "outcome_unknown", "unknown", "unknown");
        Assert.Equal(
            "process_start_outcome_unknown",
            failed.GetProperty("outcome").GetProperty("detail_code").GetString());
        Assert.Equal(
            "unknown",
            failed.GetProperty("coverage").GetProperty("descendants_observed").GetString());
        Assert.Equal(
            "unknown",
            failed.GetProperty("coverage").GetProperty("remote_effect_observed").GetString());
        AssertOutcome(events[^1], "failed", "unknown", "not_applicable");
        Assert.Equal(
            System.Text.Encoding.UTF8.GetByteCount(response),
            events[^1].GetProperty("outcome").GetProperty("bytes_returned").GetInt64());
    }

    [Fact]
    public async Task Associated_process_start_failure_records_started_failed_call_then_one_unknown_terminal()
    {
        const long jobId = 17;
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", "'started'"), ("background", true)),
            exactScript: "'started'");
        Assert.True(fixture.Context.BeginJobStartRequest(jobId));
        var terminal = Assert.IsType<AuditJobTerminalLease>(
            fixture.Context.AuthorizeJobStart(jobId, Path.GetFullPath(Path.GetTempPath())));

        Assert.True(fixture.Context.RecordJobStartedOutcomeUnknown(jobId, "unknown"));
        await terminal.CompleteAsync(new JobSnapshot(
            jobId,
            Pid: 123,
            Running: false,
            ExitCode: 137,
            StartedUtc: DateTimeOffset.UtcNow,
            Script: "'started'",
            OutputPath: Path.Combine(Path.GetTempPath(), "ptk-associated-audit.log"))
        {
            ExecutionOutcomeUnknown = true,
            ExecutionOutcomeFailureCode = "process_start_outcome_unknown",
            RootTerminationConfirmed = true,
            OutputCaptureComplete = false,
        });
        await terminal.CompleteAsync(new JobSnapshot(
            jobId,
            Pid: 123,
            Running: false,
            ExitCode: 137,
            StartedUtc: DateTimeOffset.UtcNow,
            Script: "'started'",
            OutputPath: Path.Combine(Path.GetTempPath(), "ptk-associated-repeat.log")));

        var events = fixture.Events();
        Assert.Equal(
            [
                "call.accepted",
                "job.start_requested",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "execution.planned",
                "execution.dispatched",
                "job.started",
                "call.failed",
                "job.outcome_unknown",
            ],
            events.Select(EventType));
        AssertOutcome(events[^3], "started", "unknown", "unknown");
        AssertOutcome(events[^2], "failed", "unknown", "not_applicable");
        var outcomeUnknown = events[^1];
        AssertOutcome(outcomeUnknown, "outcome_unknown", "confirmed", "complete");
        Assert.Equal(
            "process_start_outcome_unknown",
            outcomeUnknown.GetProperty("outcome").GetProperty("detail_code").GetString());
        Assert.Equal(
            events[^3].GetProperty("event_id").GetGuid(),
            outcomeUnknown.GetProperty("correlation").GetProperty("parent_event_id").GetGuid());
    }

    [Theory]
    [InlineData(false, "job.completed", "output_sink_failed")]
    [InlineData(true, "job.outcome_unknown", "rtk_output_read_failed")]
    public async Task Started_job_terminal_distinguishes_output_loss_from_execution_uncertainty(
        bool executionOutcomeUnknown,
        string expectedEvent,
        string detailCode)
    {
        const long jobId = 17;
        using var fixture = CreateFixture(
            Call("ptk_invoke", ("script", "'started'"), ("background", true)),
            exactScript: "'started'");
        Assert.True(fixture.Context.BeginJobStartRequest(jobId));
        var terminal = Assert.IsType<AuditJobTerminalLease>(
            fixture.Context.AuthorizeJobStart(jobId, Path.GetFullPath(Path.GetTempPath())));
        Assert.True(fixture.Context.RecordJobStarted(jobId, "started"));

        await terminal.CompleteAsync(new JobSnapshot(
            jobId,
            Pid: 123,
            Running: false,
            ExitCode: 0,
            StartedUtc: DateTimeOffset.UtcNow,
            Script: "'started'",
            OutputPath: Path.Combine(Path.GetTempPath(), "ptk-terminal-audit.log"))
        {
            ExecutionOutcomeUnknown = executionOutcomeUnknown,
            ExecutionOutcomeFailureCode = executionOutcomeUnknown ? detailCode : null,
            RootTerminationConfirmed = true,
            OutputCaptureComplete = false,
            OutputFailureCode = executionOutcomeUnknown ? "rtk_output_read_failed" : detailCode,
        });

        var published = fixture.Events()[^1];
        Assert.Equal(expectedEvent, EventType(published));
        AssertOutcome(
            published,
            executionOutcomeUnknown ? "outcome_unknown" : "completed",
            "confirmed",
            "complete");
        Assert.Equal(
            detailCode,
            published.GetProperty("outcome").GetProperty("detail_code").GetString());
    }

    [Fact]
    public void Read_outcome_persists_exact_byte_count_and_next_offset_before_release()
    {
        using var fixture = CreateFixture(
            Call("ptk_job", ("action", "output"), ("id", 17L), ("offset", 4L)),
            exactScript: null);
        Assert.True(fixture.Context.AuthorizeControl("job.output_requested", 17));

        fixture.Context.CommitReadOutcome(
            "job.output_accessed",
            "completed",
            "ignored for byte override",
            jobId: 17,
            nextOffset: 11,
            bytesReturnedOverride: 7);
        fixture.Context.CompleteCall("completed", "response");

        var access = fixture.Events().Single(value => EventType(value) == "job.output_accessed");
        var outcome = access.GetProperty("outcome");
        Assert.Equal(7, outcome.GetProperty("bytes_returned").GetInt64());
        Assert.Equal(11, outcome.GetProperty("next_offset").GetInt64());
        Assert.Equal(17, access.GetProperty("correlation").GetProperty("job_id").GetInt64());
    }

    [Fact]
    public void Read_outcome_projects_the_jobs_persisted_source_routing()
    {
        using var fixture = CreateFixture(
            Call("ptk_job", ("action", "output"), ("id", 17L), ("offset", 0L)),
            exactScript: null);
        Assert.True(fixture.Context.AuthorizeControl("job.output_requested", 17));
        var identity = new RtkExecutableIdentity(
            Path.Combine(Path.GetTempPath(), "rtk-audit-fixture"),
            new RtkVerifiedBinaryIdentity("fixture-1", new string('a', 64)));
        var targetPath = typeof(AuditCallContextTests).Assembly.Location;
        var targetIdentity = ColdCommandTargetIdentity.TryCapture(
            targetPath,
            new ResolvedCommand(
                System.Management.Automation.CommandTypes.Application,
                targetPath,
                targetPath),
            Path.GetFullPath(Path.GetTempPath()));
        Assert.NotNull(targetIdentity);
        var plan = new ExecutionPlan(
            originalScript: "typed RTK audit fixture",
            executionScript: null,
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Cold,
            RequestedExecutionRoute.Rtk,
            OutputProvenance.RtkUnknown,
            [ExecutionPath.PowerShellDirect],
            fallbackReason: null,
            identity,
            workingDirectory: Path.GetFullPath(Path.GetTempPath()),
            rtkArgumentVector: [targetPath],
            directFallbackProvenance: OutputProvenance.DirectText,
            coldCommandTargetIdentity: targetIdentity);
        var execution = new JobExecutionMetadata(ExecutionDispatch.FromPlan(plan));

        fixture.Context.CommitReadOutcome(
            "job.output_accessed",
            "completed",
            "already shaped",
            jobId: 17,
            nextOffset: 14,
            jobExecution: execution);
        fixture.Context.CompleteCall("completed", "response");

        var access = fixture.Events().Single(value => EventType(value) == "job.output_accessed");
        var routing = access.GetProperty("routing");
        Assert.Equal("native_terminal", routing.GetProperty("domain").GetString());
        Assert.Equal("rtk", routing.GetProperty("requested_route").GetString());
        Assert.Equal("rtk", routing.GetProperty("effective_route").GetString());
        Assert.Empty(routing.GetProperty("permitted_fallbacks").EnumerateArray());
        Assert.Equal("fixture-1", routing.GetProperty("rtk_version").GetString());
        Assert.Equal(new string('a', 64), routing.GetProperty("rtk_binary_digest").GetString());
        Assert.Equal("rtk_unknown", routing.GetProperty("provenance").GetString());
        Assert.Equal(JsonValueKind.Null, routing.GetProperty("fallback_reason").ValueKind);
    }

    [Fact]
    public void Read_outcome_persistence_failure_is_not_swallowed()
    {
        using var fixture = CreateFixture(
            Call("ptk_job", ("action", "status"), ("id", 17L)),
            exactScript: null,
            journalFault: (point, append) => point == AuditSinkFaultPoint.Flush && append == 2);

        Assert.Throws<AuditUnavailableException>(() =>
            fixture.Context.CommitReadOutcome(
                "job.status_accessed",
                "completed",
                "must not be released",
                jobId: 17));
    }

    private ContextFixture CreateFixture(
        CallToolRequestParams call,
        string? exactScript,
        Func<AuditSinkFaultPoint, int, bool>? journalFault = null)
    {
        const int maxRecordBytes = 4096;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "test-audit-context-" + Guid.NewGuid().ToString("N"));
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
        var health = new AuditHealth(options);
        var sink = new InMemoryAuditJournalSink(
            options.SegmentBytes,
            options.AggregateBytes,
            options.ProtectionMode,
            options.RetentionAge,
            journalFault);
        var journal = new AuditJournal(
            options,
            health,
            sink,
            "context-test",
            binaryDigest: null,
            hostId: Guid.Parse("12345678-1234-4abc-8def-0123456789ab"),
            supervisorBootId: Guid.Parse("22345678-1234-4abc-8def-0123456789ab"));
        var evidence = new ScriptEvidenceStore(options.EvidenceDirectory);
        Assert.True(AuditCallMetadataCapture.TryCapture(
            call,
            new AuditClientContext("context-test", "1", "session"),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            DateTimeOffset.UtcNow,
            out var metadata,
            out var capturedScript,
            out var failure),
            failure);
        Assert.Equal(exactScript, capturedScript);
        var context = new AuditCallContext(journal, evidence);
        Assert.True(context.TryBegin(metadata!, capturedScript, out failure), failure);
        return new ContextFixture(sink, journal, context);
    }

    private static CallToolRequestParams Call(
        string name,
        params (string Name, object? Value)[] arguments)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (argumentName, value) in arguments)
            values.Add(argumentName, JsonSerializer.SerializeToElement(value));
        return new CallToolRequestParams { Name = name, Arguments = values };
    }

    private static string EventType(JsonElement value) =>
        value.GetProperty("event_type").GetString()!;

    private static async ValueTask<bool> AuthorizePlanAndDispatchAsync(
        AuditCallContext context,
        ExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        if (!await context.AuthorizePlanAsync(plan, cancellationToken)) return false;
        return await context.AuthorizeDispatchAsync(
            ExecutionDispatch.FromPlan(plan),
            cancellationToken);
    }

    private static ExecutionPlan CreateBashPlan(string script, string workingDirectory)
    {
        var bash = BashExecutableIdentity.TryCapture(typeof(AuditCallContextTests).Assembly.Location);
        Assert.NotNull(bash);
        return new ExecutionPlan(
            script,
            executionScript: null,
            ExecutionDomain.Bash,
            ExecutionPath.BashViaRtk,
            PreExecutionValidation.BashSyntax,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            ImmutableArray<ExecutionPath>.Empty,
            fallbackReason: null,
            new RtkExecutableIdentity(
                Path.GetFullPath(Path.Combine(Path.GetTempPath(), "audit-rtk"))),
            bash,
            workingDirectory);
    }

    private static void AssertRouting(
        JsonElement value,
        string requestedRoute,
        string effectiveRoute,
        string provenance,
        string? fallbackReason)
    {
        var routing = value.GetProperty("routing");
        Assert.Equal("native_terminal", routing.GetProperty("domain").GetString());
        Assert.Equal(requestedRoute, routing.GetProperty("requested_route").GetString());
        Assert.Equal(effectiveRoute, routing.GetProperty("effective_route").GetString());
        Assert.Equal(
            ["powershell_direct"],
            routing.GetProperty("permitted_fallbacks")
                .EnumerateArray()
                .Select(item => item.GetString()));
        Assert.Equal(provenance, routing.GetProperty("provenance").GetString());
        if (fallbackReason is null)
            Assert.Equal(JsonValueKind.Null, routing.GetProperty("fallback_reason").ValueKind);
        else
            Assert.Equal(fallbackReason, routing.GetProperty("fallback_reason").GetString());
    }

    private static void AssertOutcome(
        JsonElement value,
        string state,
        string certainty,
        string rootCoverage)
    {
        var outcome = value.GetProperty("outcome");
        Assert.Equal(state, outcome.GetProperty("state").GetString());
        Assert.Equal(certainty, outcome.GetProperty("termination_certainty").GetString());
        Assert.Equal(
            rootCoverage,
            value.GetProperty("coverage").GetProperty("root_process_observed").GetString());
    }

    private sealed record ContextFixture(
        InMemoryAuditJournalSink Sink,
        AuditJournal Journal,
        AuditCallContext Context) : IDisposable
    {
        internal List<JsonElement> Events() => Sink.Lines.Select(line =>
        {
            using var document = JsonDocument.Parse(line.AsMemory(0, line.Length - 1));
            return document.RootElement.Clone();
        }).ToList();

        public void Dispose() => Journal.Dispose();
    }
}
