using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;

namespace PtkMcpServer.Tests;

public sealed class AuditCallFilterTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 18, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromHours(1);
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Preserve the authoritative test failure.
            }
        }
    }

    [Fact]
    public async Task Malformed_request_is_refused_before_request_services_or_handler_are_touched()
    {
        var services = new ThrowingServiceProvider();
        var handlerCalled = false;
        var call = Call("ptk_invoke", ("script", 42));

        var result = await AuditCallFilter.InvokeAsync(
            call,
            new AuditClientContext("test", "1", "stdio-1"),
            services,
            _ =>
            {
                handlerCalled = true;
                return ValueTask.FromResult(Text("executed"));
            },
            DefaultTimeout,
            MaximumTimeout,
            () => Now,
            CancellationToken.None);

        Assert.False(handlerCalled);
        Assert.Equal(0, services.AccessCount);
        Assert.True(result.IsError);
        var error = ResultText(result);
        Assert.Contains("original operation was not started", error, StringComparison.Ordinal);
        Assert.DoesNotContain("retry", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_job_id_is_refused_without_poisoning_later_audited_calls()
    {
        using var fixture = CreateFixture(slots: 8);
        var malformedHandlerCalled = false;

        var malformed = await Invoke(
            fixture,
            Call("ptk_job", ("action", "status")),
            _ =>
            {
                malformedHandlerCalled = true;
                fixture.Accessor.Current!.CommitReadOutcome(
                    "job.status_accessed",
                    "not_found",
                    "[no such job: 0]",
                    jobId: 0);
                return ValueTask.FromResult(Text("[no such job: 0]"));
            });

        Assert.False(malformedHandlerCalled);
        Assert.True(malformed.IsError);
        Assert.Empty(fixture.Sink.Lines);
        Assert.Equal(AuditHealthState.Healthy, fixture.Health.Snapshot().State);

        var validHandlerCalled = false;
        var valid = await Invoke(
            fixture,
            Call("ptk_job", ("action", "status"), ("id", 17L)),
            _ =>
            {
                validHandlerCalled = true;
                return ValueTask.FromResult(Text("healthy"));
            });

        Assert.True(validHandlerCalled);
        Assert.False(valid.IsError);
        Assert.Equal("healthy", ResultText(valid));
        Assert.Equal(AuditHealthState.Healthy, fixture.Health.Snapshot().State);
        Assert.Equal(2, fixture.Sink.Lines.Count);
        Assert.Equal(
            "call.completed",
            Parse(fixture.Sink.Lines[1]).RootElement.GetProperty("event_type").GetString());
    }

    [Fact]
    public void Authorization_persistence_refusal_resolves_failed_zero_byte_terminal()
    {
        var terminal = AuditCallFilter.ResolveFallbackTerminal(
            Text(AuditCallContext.NotStartedMessage),
            authorizationRefused: true);

        Assert.Equal("failed", terminal.State);
        Assert.Equal(0, terminal.BytesReturned);
    }

    [Fact]
    public async Task Admission_append_failure_refuses_before_handler_and_does_not_echo_script()
    {
        const string submittedScript = "$password = 'do-not-echo'; Remove-Item important";
        using var fixture = CreateFixture(
            slots: 12,
            faultInjector: (point, call) => point == AuditSinkFaultPoint.BeforeAppend && call == 1);
        var handlerCalled = false;
        var call = Call("ptk_invoke", ("script", submittedScript));

        var result = await Invoke(fixture, call, _ =>
        {
            handlerCalled = true;
            return ValueTask.FromResult(Text("executed"));
        });

        Assert.False(handlerCalled);
        Assert.True(result.IsError);
        Assert.Equal(AuditHealthState.Unavailable, fixture.Health.Snapshot().State);
        var error = ResultText(result);
        Assert.Contains("original operation was not started", error, StringComparison.Ordinal);
        Assert.DoesNotContain(submittedScript, error, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-echo", error, StringComparison.Ordinal);
        Assert.DoesNotContain("retry", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw=true", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Emergency_state_is_supervisor_only_and_does_not_touch_tool_dependencies()
    {
        using var fixture = CreateFixture(slots: 8, includeToolDependencySentinels: true);
        Assert.True(fixture.Journal.TryReserve(5, out var blocker, out _));
        var handlerCalled = false;

        var result = await Invoke(fixture, Call("ptk_state"), cancellationToken =>
        {
            handlerCalled = true;
            fixture.Services.GetRequiredService<ISessionOperations>();
            return ValueTask.FromResult(Text("full state"));
        });
        blocker!.Release();

        Assert.False(handlerCalled);
        Assert.False(result.IsError);
        Assert.Equal(0, fixture.ToolDependencies.RuntimeResolutions);
        var diagnostic = ResultText(result);
        Assert.Contains("audit=unavailable", diagnostic, StringComparison.Ordinal);
        Assert.Contains("unrecorded=true", diagnostic, StringComparison.Ordinal);
        Assert.Contains("failure_class=journal.capacity", diagnostic, StringComparison.Ordinal);
        Assert.Contains("protection_mode=local-only", diagnostic, StringComparison.Ordinal);
        Assert.Contains("export_configuration_identity=none", diagnostic, StringComparison.Ordinal);
        Assert.Contains("exporter_state=disabled", diagnostic, StringComparison.Ordinal);
        Assert.Contains("exporter_warning=false", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("runspace", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("job", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.Health.Snapshot().EmergencyProbeCount);
        Assert.Single(fixture.Sink.Lines);
        using var degraded = Parse(fixture.Sink.Lines[0]);
        Assert.Equal("audit.degraded", degraded.RootElement.GetProperty("event_type").GetString());
    }

    [Fact]
    public async Task Emergency_state_includes_nonsecret_anchored_export_stall_metadata()
    {
        using var fixture = CreateFixture(
            slots: 8,
            protectionMode: AuditProtectionMode.Anchored);
        var blocked = new AuditExportCoordinatorStep(
            AuditExportCoordinatorStepKind.Blocked,
            Guid.Parse("32345678-1234-4abc-8def-0123456789ab"),
            IsCurrentBoot: false,
            Guid.Parse("42345678-1234-4abc-8def-0123456789ab"),
            "http.401",
            AuditExportFailureClass.Configuration);
        fixture.Health.ExportObserver.Observe(new AuditExportHealthObservation(
            new AuditExportLoopSnapshot(
                AuditExportLoopState.WaitingForWork,
                blocked,
                TimeSpan.FromSeconds(3)),
            blocked,
            Now));
        Assert.True(fixture.Journal.TryReserve(5, out var blocker, out _));

        var result = await Invoke(
            fixture,
            Call("ptk_state"),
            _ => ValueTask.FromResult(Text("must not run")));
        blocker!.Release();

        var diagnostic = ResultText(result);
        Assert.Contains("exporter_state=stalled", diagnostic, StringComparison.Ordinal);
        Assert.Contains("exporter_blocked_supervisor_boot_id=32345678-1234-4abc-8def-0123456789ab", diagnostic, StringComparison.Ordinal);
        Assert.Contains("exporter_blocked_is_current_boot=false", diagnostic, StringComparison.Ordinal);
        Assert.Contains("exporter_blocked_event_id=42345678-1234-4abc-8def-0123456789ab", diagnostic, StringComparison.Ordinal);
        Assert.Contains("exporter_blocked_detail=http.401", diagnostic, StringComparison.Ordinal);
        Assert.Contains("exporter_blocked_failure_class=configuration", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Successful_handler_runs_under_current_scope_and_filter_records_text_bytes()
    {
        using var fixture = CreateFixture(slots: 6);
        Assert.Null(fixture.Accessor.Current);
        Assert.Null(fixture.Services.GetService<AuditRuntimeGate>());
        Assert.NotNull(fixture.Services.GetService<IAuditAdmissionOwner>());
        AuditCallContext? observed = null;

        var result = await Invoke(
            fixture,
            Call("ptk_job", ("action", "status"), ("id", 7L)),
            _ =>
            {
                observed = fixture.Accessor.Current;
                return ValueTask.FromResult(Text("é"));
            });

        Assert.NotNull(observed);
        Assert.Contains(
            "audit exporter: disabled (local-only), warning false",
            observed.HealthStatusLine(),
            StringComparison.Ordinal);
        var health = observed.HealthStatusLine();
        Assert.Contains("audit storage: spool ", health, StringComparison.Ordinal);
        Assert.Contains(
            "/28672 bytes, reserved 0 bytes, effective free ",
            health,
            StringComparison.Ordinal);
        Assert.Contains(
            "emergency reserved 8192/8192 bytes",
            health,
            StringComparison.Ordinal);
        Assert.Null(fixture.Accessor.Current);
        Assert.Equal("é", ResultText(result));
        Assert.Equal(2, fixture.Sink.Lines.Count);
        using var terminal = Parse(fixture.Sink.Lines[1]);
        Assert.Equal("call.completed", terminal.RootElement.GetProperty("event_type").GetString());
        Assert.Equal(2, terminal.RootElement.GetProperty("outcome").GetProperty("bytes_returned").GetInt64());
    }

    [Fact]
    public async Task Incompatible_boundary_refuses_without_dispatch_and_releases_once()
    {
        using var fixture = CreateFixture(slots: 6);
        var boundary = new RecordingBoundaryCall();
        var lease = new RecordingCallLease();
        var owner = new StubAdmissionOwner(
            fixture.Health,
            (_, _) => new AdmissionResult(true, boundary, lease, null));
        var handlerCalls = 0;

        var result = await InvokeWithOwner(
            owner,
            Call("ptk_job", ("action", "status"), ("id", 71L)),
            _ =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Text("must not run"));
            });

        Assert.Equal(0, handlerCalls);
        Assert.Equal(1, boundary.CompletionCount);
        Assert.Equal("failed", boundary.CompletionState);
        Assert.Equal(0, boundary.CompletionBytes);
        Assert.Equal(1, lease.DisposeCount);
        Assert.StartsWith("audit_boundary_invalid: ", ResultText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_boundary_refuses_without_dispatch_and_releases_once()
    {
        using var fixture = CreateFixture(slots: 6);
        var lease = new RecordingCallLease();
        var owner = new StubAdmissionOwner(
            fixture.Health,
            (_, _) => new AdmissionResult(true, null, lease, null));
        var handlerCalls = 0;

        var result = await InvokeWithOwner(
            owner,
            Call("ptk_job", ("action", "status"), ("id", 72L)),
            _ =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Text("must not run"));
            });

        Assert.Equal(0, handlerCalls);
        Assert.Equal(1, lease.DisposeCount);
        Assert.StartsWith("audit_boundary_invalid: ", ResultText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_lease_fails_the_local_boundary_terminal_without_dispatch()
    {
        using var fixture = CreateFixture(slots: 6);
        var owner = new StubAdmissionOwner(
            fixture.Health,
            (metadata, submittedScript) =>
            {
                var context = new AuditCallContext(fixture.Journal, fixture.Evidence);
                Assert.True(context.TryBegin(metadata, submittedScript, out var failureClass));
                Assert.Null(failureClass);
                return new AdmissionResult(true, context, null, null);
            });
        var handlerCalls = 0;

        var result = await InvokeWithOwner(
            owner,
            Call("ptk_job", ("action", "status"), ("id", 73L)),
            _ =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Text("must not run"));
            });

        Assert.Equal(0, handlerCalls);
        Assert.StartsWith("audit_boundary_invalid: ", ResultText(result), StringComparison.Ordinal);
        Assert.Equal(2, fixture.Sink.Lines.Count);
        using var terminal = Parse(fixture.Sink.Lines[1]);
        Assert.Equal("call.failed", terminal.RootElement.GetProperty("event_type").GetString());
        Assert.Equal(0, terminal.RootElement.GetProperty("outcome").GetProperty("bytes_returned").GetInt64());
    }

    [Fact]
    public async Task False_admission_with_owned_outputs_fails_and_releases_once()
    {
        using var fixture = CreateFixture(slots: 6);
        var boundary = new RecordingBoundaryCall();
        var lease = new RecordingCallLease();
        var owner = new StubAdmissionOwner(
            fixture.Health,
            (_, _) => new AdmissionResult(false, boundary, lease, "journal.unavailable"));
        var handlerCalls = 0;

        var result = await InvokeWithOwner(
            owner,
            Call("ptk_job", ("action", "status"), ("id", 74L)),
            _ =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Text("must not run"));
            });

        Assert.Equal(0, handlerCalls);
        Assert.Equal(1, boundary.CompletionCount);
        Assert.Equal(1, lease.DisposeCount);
        Assert.StartsWith("audit_boundary_invalid: ", ResultText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admission_exception_after_assigning_outputs_fails_and_releases_once()
    {
        using var fixture = CreateFixture(slots: 6);
        var boundary = new RecordingBoundaryCall();
        var lease = new RecordingCallLease();
        IAuditAdmissionOwner owner = new ThrowingAfterOwnershipAdmissionOwner(
            fixture.Health,
            boundary,
            lease);
        var handlerCalls = 0;

        var result = await InvokeWithOwner(
            owner,
            Call("ptk_job", ("action", "status"), ("id", 75L)),
            _ =>
            {
                handlerCalls++;
                return ValueTask.FromResult(Text("must not run"));
            });

        Assert.Equal(0, handlerCalls);
        Assert.Equal(1, boundary.CompletionCount);
        Assert.Equal("failed", boundary.CompletionState);
        Assert.Equal(0, boundary.CompletionBytes);
        Assert.Equal(1, lease.DisposeCount);
        Assert.True(result.IsError);
        Assert.Contains("original operation was not started", ResultText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handler_exception_records_a_terminal_event_and_propagates()
    {
        using var fixture = CreateFixture(slots: 6);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(
            fixture,
            Call("ptk_job", ("action", "status"), ("id", 9L)),
            _ => ValueTask.FromException<CallToolResult>(new InvalidOperationException("handler failed"))).AsTask());

        Assert.Equal("handler failed", exception.Message);
        Assert.Null(fixture.Accessor.Current);
        Assert.Equal(2, fixture.Sink.Lines.Count);
        using var terminal = Parse(fixture.Sink.Lines[1]);
        Assert.Equal("call.failed", terminal.RootElement.GetProperty("event_type").GetString());
        Assert.Equal("failed", terminal.RootElement.GetProperty("outcome").GetProperty("state").GetString());
        Assert.Equal(0, terminal.RootElement.GetProperty("outcome").GetProperty("bytes_returned").GetInt64());
    }

    [Fact]
    public async Task Capacity_recovery_is_persisted_with_the_exact_emergency_probe_gap_before_admission_reopens()
    {
        using var fixture = CreateFixture(slots: 10);
        Assert.True(fixture.Journal.TryReserve(6, out var blocker, out _));

        var emergency = await Invoke(
            fixture,
            Call("ptk_state"),
            _ => ValueTask.FromResult(Text("must not run")));
        Assert.Contains("audit=unavailable", ResultText(emergency), StringComparison.Ordinal);
        blocker!.Release();

        var handlerCalled = false;
        var recoveredCall = await Invoke(
            fixture,
            Call("ptk_job", ("action", "status"), ("id", 11L)),
            _ =>
            {
                handlerCalled = true;
                return ValueTask.FromResult(Text("recovered"));
            });

        Assert.True(handlerCalled);
        Assert.Equal("recovered", ResultText(recoveredCall));
        Assert.Equal(AuditHealthState.Healthy, fixture.Health.Snapshot().State);
        Assert.Equal(4, fixture.Sink.Lines.Count);
        using var degraded = Parse(fixture.Sink.Lines[0]);
        Assert.Equal("audit.degraded", degraded.RootElement.GetProperty("event_type").GetString());
        using var recovery = Parse(fixture.Sink.Lines[1]);
        var root = recovery.RootElement;
        Assert.Equal("audit.recovered", root.GetProperty("event_type").GetString());
        var audit = root.GetProperty("audit");
        Assert.Equal("recovered", audit.GetProperty("health_state").GetString());
        Assert.Equal("journal.capacity", audit.GetProperty("failure_class").GetString());
        Assert.Equal(1, audit.GetProperty("emergency_probe_count").GetInt64());
        Assert.Equal(Now, audit.GetProperty("emergency_probe_first_utc").GetDateTimeOffset());
        Assert.Equal(Now, audit.GetProperty("emergency_probe_last_utc").GetDateTimeOffset());
        Assert.Equal("call.accepted", Parse(fixture.Sink.Lines[2]).RootElement.GetProperty("event_type").GetString());
        Assert.Equal("call.completed", Parse(fixture.Sink.Lines[3]).RootElement.GetProperty("event_type").GetString());
    }

    private async ValueTask<CallToolResult> Invoke(
        FilterFixture fixture,
        CallToolRequestParams call,
        Func<CancellationToken, ValueTask<CallToolResult>> next)
    {
        return await AuditCallFilter.InvokeAsync(
            call,
            new AuditClientContext("test-client", "1.0", "stdio-test"),
            fixture.Services,
            next,
            DefaultTimeout,
            MaximumTimeout,
            () => Now,
            CancellationToken.None);
    }

    private static async ValueTask<CallToolResult> InvokeWithOwner(
        IAuditAdmissionOwner owner,
        CallToolRequestParams call,
        Func<CancellationToken, ValueTask<CallToolResult>> next)
    {
        var accessor = new AuditCallContextAccessor();
        using var provider = new ServiceCollection()
            .AddSingleton(owner)
            .AddSingleton(accessor)
            .BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Null(scope.ServiceProvider.GetService<AuditRuntimeGate>());

        return await AuditCallFilter.InvokeAsync(
            call,
            new AuditClientContext("test-client", "1.0", "stdio-interface-test"),
            scope.ServiceProvider,
            next,
            DefaultTimeout,
            MaximumTimeout,
            () => Now,
            CancellationToken.None);
    }

    private FilterFixture CreateFixture(
        int slots,
        Func<AuditSinkFaultPoint, int, bool>? faultInjector = null,
        bool includeToolDependencySentinels = false,
        AuditProtectionMode protectionMode = AuditProtectionMode.LocalOnly)
    {
        const int maximumRecordBytes = 4096;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-audit-filter-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var options = AuditOptions.Create(
            root,
            protectionMode,
            protectionMode == AuditProtectionMode.Anchored ? new string('a', 64) : null,
            maxRecordBytes: maximumRecordBytes,
            segmentBytes: maximumRecordBytes * (long)(slots + 1),
            aggregateBytes: maximumRecordBytes * (long)(slots + 1) *
                (protectionMode == AuditProtectionMode.Anchored ? 2L : 1L),
            emergencyReserveBytes: maximumRecordBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: maximumRecordBytes,
            evidenceAggregateBytes: maximumRecordBytes * 4L,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
        var health = new AuditHealth(options, () => Now);
        var sink = new InMemoryAuditJournalSink(
            options.SegmentBytes,
            options.AggregateBytes,
            options.ProtectionMode,
            options.RetentionAge,
            faultInjector);
        var journal = new AuditJournal(
            options,
            health,
            sink,
            "filter-test",
            binaryDigest: null,
            hostId: Guid.Parse("12345678-1234-4abc-8def-0123456789ab"),
            supervisorBootId: Guid.Parse("22345678-1234-4abc-8def-0123456789ab"),
            utcNow: () => Now);
        var evidence = new ScriptEvidenceStore(root);
        var runtime = AuditRuntimeGate.CreateOperationalForTests(
            options, health, journal, evidence);
        var touches = new ToolDependencyTouches();
        var accessor = new AuditCallContextAccessor();
        var collection = new ServiceCollection()
            .AddSingleton(options)
            .AddSingleton(health)
            .AddSingleton(journal)
            .AddSingleton(evidence)
            .AddSingleton<IAuditAdmissionOwner>(runtime)
            .AddSingleton(accessor);
        if (includeToolDependencySentinels)
        {
            collection.AddSingleton<ISessionOperations>(_ =>
            {
                touches.RuntimeResolutions++;
                throw new InvalidOperationException("Session runtime must not be resolved.");
            });
        }
        var provider = collection.BuildServiceProvider();
        var scope = provider.CreateScope();
        return new FilterFixture(
            health,
            sink,
            journal,
            evidence,
            provider,
            scope,
            touches,
            accessor);
    }

    private static CallToolRequestParams Call(string name, params (string Name, object? Value)[] arguments)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (argumentName, value) in arguments)
            values.Add(argumentName, JsonSerializer.SerializeToElement(value));
        return new CallToolRequestParams { Name = name, Arguments = values };
    }

    private static CallToolResult Text(string text, bool isError = false) => new()
    {
        IsError = isError,
        Content = [new TextContentBlock { Text = text }],
    };

    private static string ResultText(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static JsonDocument Parse(byte[] line) =>
        JsonDocument.Parse(line.AsMemory(0, line.Length - 1));

    private sealed class ThrowingServiceProvider : IServiceProvider
    {
        internal int AccessCount { get; private set; }

        public object? GetService(Type serviceType)
        {
            AccessCount++;
            throw new InvalidOperationException("Request services must not be touched.");
        }
    }

    private sealed class ToolDependencyTouches
    {
        internal int RuntimeResolutions { get; set; }
    }

    private sealed record AdmissionResult(
        bool Admitted,
        IAuditBoundaryCall? Call,
        IDisposable? Lease,
        string? FailureClass);

    private sealed class StubAdmissionOwner(
        AuditHealth health,
        Func<AuditCallMetadata, string?, AdmissionResult> admit) : IAuditAdmissionOwner
    {
        public AuditHealth Health { get; } = health;

        public void Touch()
        {
        }

        public bool TryBeginCall(
            AuditCallMetadata metadata,
            string? exactSubmittedScript,
            out IAuditBoundaryCall? call,
            out IDisposable? callLease,
            out string? failureClass)
        {
            var result = admit(metadata, exactSubmittedScript);
            call = result.Call;
            callLease = result.Lease;
            failureClass = result.FailureClass;
            return result.Admitted;
        }
    }

    private sealed class RecordingBoundaryCall : IAuditBoundaryCall
    {
        public bool AuthorizationPersistenceFailed => false;

        public bool UserExecutionStarted => false;

        public bool TerminalWritten { get; private set; }

        internal int CompletionCount { get; private set; }

        internal string? CompletionState { get; private set; }

        internal long? CompletionBytes { get; private set; }

        public void CompleteFromFilter(string state, long bytesReturned)
        {
            CompletionCount++;
            CompletionState = state;
            CompletionBytes = bytesReturned;
            TerminalWritten = true;
        }
    }

    private sealed class ThrowingAfterOwnershipAdmissionOwner(
        AuditHealth health,
        IAuditBoundaryCall boundary,
        IDisposable lease) : IAuditAdmissionOwner
    {
        public AuditHealth Health { get; } = health;

        public void Touch()
        {
        }

        public bool TryBeginCall(
            AuditCallMetadata metadata,
            string? exactSubmittedScript,
            out IAuditBoundaryCall? call,
            out IDisposable? callLease,
            out string? failureClass)
        {
            call = boundary;
            callLease = lease;
            failureClass = null;
            throw new InvalidOperationException("admission failed after ownership transfer");
        }
    }

    private sealed class RecordingCallLease : IDisposable
    {
        internal int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed record FilterFixture(
        AuditHealth Health,
        InMemoryAuditJournalSink Sink,
        AuditJournal Journal,
        ScriptEvidenceStore Evidence,
        ServiceProvider Provider,
        IServiceScope Scope,
        ToolDependencyTouches ToolDependencies,
        AuditCallContextAccessor Accessor) : IDisposable
    {
        internal IServiceProvider Services => Scope.ServiceProvider;

        public void Dispose()
        {
            Scope.Dispose();
            Provider.Dispose();
        }
    }
}
