using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PtkMcpGuardian.Host;
using PtkMcpGuardian.Lifecycle;
using PtkMcpGuardian.Package;
using PtkMcpGuardian.Standalone;
using PtkMcpGuardian.Standalone.Fake;
using PtkMcpServer;
using PtkMcpServer.Audit;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class ProductionGuardianCompositionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly WorkerBootId Worker = new(
        Guid.Parse("22222222-2222-4222-8222-222222222222"));

    [Fact]
    public async Task Composition_freezes_package_session_and_guardian_owned_state()
    {
        var auditRoot = TemporaryRoot("audit");
        var outputRoot = TemporaryRoot("output");
        var package = Package(Path.Combine(Path.GetTempPath(), "never-launched-host"));
        var composition = ProductionGuardianComposition.Create(
            package,
            LocalAudit(auditRoot),
            new NeverLauncher(),
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        try
        {
            Assert.Equal(Guardian, composition.GuardianBootId);
            Assert.Equal(package.HostExecutableDigest, composition.Pins.HostExecutableDigest);
            Assert.Equal(package.HostBuildDigest, composition.Pins.HostBuildDigest);
            Assert.Equal(package.PublicContractDigest, composition.Pins.PublicContractDigest);
            Assert.Equal(package.PackageManifestDigest, composition.Pins.PackageManifestDigest);
            Assert.Equal(
                composition.SessionState.ConfigurationDigest,
                composition.Pins.ConfigurationDigest);
            Assert.Equal(
                composition.SessionState.CatalogDigest,
                composition.Pins.CatalogDigest);

            var state = composition.Supervisor.SnapshotState();
            Assert.Equal(Guardian, state.GuardianBootId);
            Assert.Equal(PublicHostState.Absent, state.Host.State);
            Assert.False(state.Host.ReadyForEffects);
            var session = Assert.Single(state.Sessions);
            Assert.Equal("default", session.Alias.Value);
            Assert.Equal(PublicSessionState.Lost, session.State);
            Assert.False(session.ReadyForEffects);
            Assert.True(session.WarmStateLost);
            Assert.Equal(BootstrapState.Unknown, session.BootstrapState);
        }
        finally
        {
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Windows_composition_serves_the_real_private_host_before_public_initialize()
    {
        if (!OperatingSystem.IsWindows()) return;

        var auditRoot = TemporaryRoot("real-audit");
        var outputRoot = TemporaryRoot("real-output");
        var composition = ProductionGuardianComposition.Create(
            Package(FindServerAppHost()),
            LocalAudit(auditRoot),
            new WindowsPrivateHostProcessLauncher(),
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-guardian-composition-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);

            var stateResponse = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new
                {
                    name = "ptk_state",
                    arguments = new { },
                },
                timeout.Token);
            var state = PublicStateCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(stateResponse, expectedError: false)));
            Assert.Equal(PublicHostState.Ready, state.Host.State);
            Assert.True(state.Host.ReadyForEffects);
            var session = Assert.Single(state.Sessions);
            Assert.Equal(Worker, session.WorkerBootId);
            Assert.True(session.ReadyForEffects);

            var jobs = await RequestAsync(
                writer,
                reader,
                requestId: 3,
                "tools/call",
                new
                {
                    name = "ptk_job",
                    arguments = new { action = "list" },
                },
                timeout.Token);
            Assert.Equal("(no jobs)", ToolText(jobs, expectedError: false));

            var invocation = await RequestAsync(
                writer,
                reader,
                requestId: 4,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "Write-Output 'production-private-host'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "production-private-host",
                ToolText(invocation, expectedError: false),
                StringComparison.Ordinal);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
            Assert.Equal(0, composition.Supervisor.OutstandingCallCount);
            Assert.Equal(0, composition.Supervisor.BackgroundTaskCount);
            Assert.Equal(0, composition.Supervisor.OwnedClientCount);
            Assert.Equal(0, composition.Supervisor.OwnedAttemptWatcherSetCount);
        }
        finally
        {
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Windows_private_host_ignores_the_transitional_idle_watchdog()
    {
        if (!OperatingSystem.IsWindows()) return;

        var auditRoot = TemporaryRoot("idle-audit");
        var outputRoot = TemporaryRoot("idle-output");
        var launcher = new GatedContainmentLauncher();
        launcher.ReleaseFirstContainmentConfirmation();
        var composition = ProductionGuardianComposition.Create(
            Package(FindServerAppHost()),
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker,
            parentEnvironment: ParentEnvironmentWith(
                "PTK_IDLE_EXIT_SECONDS",
                "1"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-private-host-idle-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);
            var firstHostProcessId = await launcher.FirstHostProcessId.WaitAsync(timeout.Token);

            var mutation = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "$global:PtkR5IdleSentinel = 'survived'; 'sentinel-set'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "sentinel-set",
                ToolText(mutation, expectedError: false),
                StringComparison.Ordinal);

            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            using (var firstHost = Process.GetProcessById(firstHostProcessId))
                Assert.False(firstHost.HasExited);

            var stateResponse = await RequestAsync(
                writer,
                reader,
                requestId: 3,
                "tools/call",
                new
                {
                    name = "ptk_state",
                    arguments = new { },
                },
                timeout.Token);
            var state = PublicStateCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(stateResponse, expectedError: false)));
            Assert.Equal(PublicHostState.Ready, state.Host.State);
            Assert.Equal(1, state.Host.Generation?.Value);
            var session = Assert.Single(state.Sessions);
            Assert.Equal(Worker, session.WorkerBootId);
            Assert.False(session.WarmStateLost);

            var proof = await RequestAsync(
                writer,
                reader,
                requestId: 4,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "if ($global:PtkR5IdleSentinel -eq 'survived') { 'sentinel-present' } else { 'sentinel-absent' }",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "sentinel-present",
                ToolText(proof, expectedError: false),
                StringComparison.Ordinal);
            Assert.Equal(1, launcher.LaunchCount);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
        }
        finally
        {
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public Task Windows_composition_classifies_real_prewrite_loss() =>
        RunRealDispatchBarrierAsync(RealDispatchBarrier.BeforeWriteAuthorization);

    [Fact]
    public Task Windows_composition_classifies_real_possibly_written_loss() =>
        RunRealDispatchBarrierAsync(RealDispatchBarrier.WriteStarting);

    [Fact]
    public Task Windows_composition_retains_real_decoded_terminal_on_loss() =>
        RunRealDispatchBarrierAsync(RealDispatchBarrier.TerminalDecoded);

    private async Task RunRealDispatchBarrierAsync(
        RealDispatchBarrier barrier)
    {
        if (!OperatingSystem.IsWindows()) return;

        var auditRoot = TemporaryRoot($"barrier-{barrier}-audit");
        var outputRoot = TemporaryRoot($"barrier-{barrier}-output");
        var effectRoot = TemporaryRoot($"barrier-{barrier}-effect");
        Directory.CreateDirectory(effectRoot);
        var effectPath = Path.Combine(effectRoot, "effect.txt");
        var launcher = new GatedContainmentLauncher();
        launcher.ReleaseFirstContainmentConfirmation();
        var observer = new RealHostKillingDispatchObserver(barrier, launcher);
        var composition = ProductionGuardianComposition.Create(
            Package(FindServerAppHost()),
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker,
            dispatchObserver: observer);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-host-dispatch-barrier-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);

            var escapedEffectPath = effectPath.Replace("'", "''", StringComparison.Ordinal);
            var response = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = $"[IO.File]::AppendAllText('{escapedEffectPath}', 'effect' + [Environment]::NewLine); 'barrier-effect'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            await observer.Triggered
                .WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);

            if (barrier == RealDispatchBarrier.TerminalDecoded)
            {
                Assert.Contains(
                    "barrier-effect",
                    ToolText(response, expectedError: false),
                    StringComparison.Ordinal);
            }
            else
            {
                var recovery = PublicRecoveryCodec.Decode(
                    Encoding.UTF8.GetBytes(ToolText(response, expectedError: true)));
                if (barrier == RealDispatchBarrier.BeforeWriteAuthorization)
                {
                    Assert.Equal(
                        PublicRecoveryDetailCode.BackendLostBeforeDispatch,
                        recovery.DetailCode);
                    Assert.True(recovery.Retryable);
                    Assert.IsType<SessionReadyGate>(recovery.RetryGate);
                }
                else
                {
                    Assert.Equal(PublicRecoveryDetailCode.OutcomeUnknown, recovery.DetailCode);
                    Assert.False(recovery.Retryable);
                    Assert.Null(recovery.RetryAfterMilliseconds);
                    Assert.Null(recovery.RetryGate);
                }
            }

            _ = await launcher.ReplacementHostProcessId.WaitAsync(timeout.Token);
            PublicStateSnapshot? recovered = null;
            var requestId = 3;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var stateResponse = await RequestAsync(
                    writer,
                    reader,
                    requestId++,
                    "tools/call",
                    new
                    {
                        name = "ptk_state",
                        arguments = new { },
                    },
                    timeout.Token);
                var candidate = PublicStateCodec.Decode(
                    Encoding.UTF8.GetBytes(ToolText(stateResponse, expectedError: false)));
                if (candidate.Host.ReadyForEffects)
                {
                    recovered = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }
            Assert.NotNull(recovered);
            Assert.Equal(2, recovered.Host.Generation?.Value);
            Assert.True(Assert.Single(recovered.Sessions).WarmStateLost);
            Assert.Equal(2, launcher.LaunchCount);

            var effectCount = File.Exists(effectPath)
                ? File.ReadLines(effectPath).Count(line =>
                    StringComparer.Ordinal.Equals(line, "effect"))
                : 0;
            if (barrier == RealDispatchBarrier.BeforeWriteAuthorization)
                Assert.Equal(0, effectCount);
            else if (barrier == RealDispatchBarrier.TerminalDecoded)
                Assert.Equal(1, effectCount);
            else
                Assert.InRange(effectCount, 0, 1);

            var postRecovery = await RequestAsync(
                writer,
                reader,
                requestId,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "'post-barrier-recovery'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "post-barrier-recovery",
                ToolText(postRecovery, expectedError: false),
                StringComparison.Ordinal);
            Assert.Equal(2, launcher.LaunchCount);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
        }
        finally
        {
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
            DeleteRoot(effectRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Windows_composition_recovers_a_real_host_on_the_same_public_connection()
    {
        if (!OperatingSystem.IsWindows()) return;

        var auditRoot = TemporaryRoot("recovery-audit");
        var outputRoot = TemporaryRoot("recovery-output");
        var launcher = new GatedContainmentLauncher();
        var composition = ProductionGuardianComposition.Create(
            Package(FindServerAppHost()),
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-host-recovery-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);

            var initialResponse = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new
                {
                    name = "ptk_state",
                    arguments = new { },
                },
                timeout.Token);
            var initial = PublicStateCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(initialResponse, expectedError: false)));
            Assert.Equal(PublicHostState.Ready, initial.Host.State);
            Assert.Equal(1, initial.Host.Generation?.Value);
            Assert.False(Assert.Single(initial.Sessions).WarmStateLost);
            var firstHostProcessId = await launcher.FirstHostProcessId.WaitAsync(timeout.Token);

            var warmMutation = await RequestAsync(
                writer,
                reader,
                requestId: 3,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "$global:PtkR5UndeclaredState = 'old-generation'; 'warm-state-set'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "warm-state-set",
                ToolText(warmMutation, expectedError: false),
                StringComparison.Ordinal);
            var warmProof = await RequestAsync(
                writer,
                reader,
                requestId: 4,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "if ($global:PtkR5UndeclaredState -eq 'old-generation') { 'warm-state-present' } else { 'warm-state-absent' }",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "warm-state-present",
                ToolText(warmProof, expectedError: false),
                StringComparison.Ordinal);
            var descendantResponse = await RequestAsync(
                writer,
                reader,
                requestId: 5,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "$child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32/PING.EXE') -ArgumentList '-t','127.0.0.1' -PassThru; 'PTK_CHILD_PID=' + $child.Id",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            var descendantProcessId = MarkerInteger(
                ToolText(descendantResponse, expectedError: false),
                "PTK_CHILD_PID=");
            using var descendantProcess = Process.GetProcessById(descendantProcessId);
            Assert.False(descendantProcess.HasExited);

            using (var firstHost = Process.GetProcessById(firstHostProcessId))
            {
                firstHost.Kill();
                await firstHost.WaitForExitAsync(timeout.Token);
                Assert.True(firstHost.HasExited);
            }
            await launcher.FirstContainmentConfirmed.WaitAsync(timeout.Token);
            await descendantProcess.WaitForExitAsync(timeout.Token);
            Assert.True(descendantProcess.HasExited);
            Assert.Equal(1, launcher.LaunchCount);

            var recoveryStateResponse = await RequestAsync(
                writer,
                reader,
                requestId: 6,
                "tools/call",
                new
                {
                    name = "ptk_state",
                    arguments = new { },
                },
                timeout.Token);
            var recovering = PublicStateCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(recoveryStateResponse, expectedError: false)));
            Assert.Equal(PublicHostState.Recovering, recovering.Host.State);
            Assert.Equal(RecoveryPhase.Containment, recovering.Host.RecoveryPhase);
            Assert.Equal(1, recovering.Host.RecoveryAttempt);
            Assert.Equal(1, recovering.Host.Generation?.Value);
            Assert.False(recovering.Host.ReadyForEffects);
            var recoveringSession = Assert.Single(recovering.Sessions);
            Assert.Equal(PublicSessionState.Recovering, recoveringSession.State);
            Assert.True(recoveringSession.WarmStateLost);
            Assert.False(recoveringSession.ReadyForEffects);

            var refusedResponse = await RequestAsync(
                writer,
                reader,
                requestId: 7,
                "tools/call",
                new
                {
                    name = "ptk_job",
                    arguments = new { action = "list" },
                },
                timeout.Token);
            var refusal = PublicRecoveryCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(refusedResponse, expectedError: true)));
            Assert.Equal(PublicRecoveryDetailCode.HostRecovering, refusal.DetailCode);
            Assert.True(refusal.Retryable);
            Assert.Equal(RecoveryPhase.Containment, refusal.RecoveryPhase);
            Assert.Equal(1, refusal.RecoveryAttempt);
            var retryGate = Assert.IsType<SessionReadyGate>(refusal.RetryGate);
            Assert.Equal("default", retryGate.Alias.Value);
            Assert.Equal(1, launcher.LaunchCount);

            launcher.ReleaseFirstContainmentConfirmation();
            var replacementHostProcessId = await launcher.ReplacementHostProcessId
                .WaitAsync(timeout.Token);
            Assert.NotEqual(firstHostProcessId, replacementHostProcessId);

            PublicStateSnapshot? recovered = null;
            var requestId = 8;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var stateResponse = await RequestAsync(
                    writer,
                    reader,
                    requestId++,
                    "tools/call",
                    new
                    {
                        name = "ptk_state",
                        arguments = new { },
                    },
                    timeout.Token);
                var candidate = PublicStateCodec.Decode(
                    Encoding.UTF8.GetBytes(ToolText(stateResponse, expectedError: false)));
                if (candidate.Host.ReadyForEffects)
                {
                    recovered = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }

            Assert.NotNull(recovered);
            Assert.Equal(PublicHostState.Ready, recovered.Host.State);
            Assert.Equal(2, recovered.Host.Generation?.Value);
            var recoveredSession = Assert.Single(recovered.Sessions);
            Assert.Equal(PublicSessionState.Ready, recoveredSession.State);
            Assert.True(recoveredSession.ReadyForEffects);
            Assert.True(recoveredSession.WarmStateLost);
            Assert.Equal(BootstrapState.Restored, recoveredSession.BootstrapState);

            var invocation = await RequestAsync(
                writer,
                reader,
                requestId,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "if (Get-Variable -Name PtkR5UndeclaredState -Scope Global -ErrorAction Ignore) { 'warm-state-present' } else { 'warm-state-absent' }; 'recovered-private-host'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            var recoveredText = ToolText(invocation, expectedError: false);
            Assert.Contains("warm-state-absent", recoveredText, StringComparison.Ordinal);
            Assert.DoesNotContain("warm-state-present", recoveredText, StringComparison.Ordinal);
            Assert.Contains("recovered-private-host", recoveredText, StringComparison.Ordinal);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
            Assert.Equal(0, composition.Supervisor.OutstandingCallCount);
            Assert.Equal(0, composition.Supervisor.BackgroundTaskCount);
            Assert.Equal(0, composition.Supervisor.OwnedClientCount);
            Assert.Equal(0, composition.Supervisor.OwnedAttemptWatcherSetCount);
        }
        finally
        {
            launcher.ReleaseFirstContainmentConfirmation();
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Windows_composition_recovers_after_replacement_dies_during_startup()
    {
        if (!OperatingSystem.IsWindows()) return;

        var auditRoot = TemporaryRoot("startup-crash-audit");
        var outputRoot = TemporaryRoot("startup-crash-output");
        var launcher = new CrashSecondLaunchLauncher();
        var composition = ProductionGuardianComposition.Create(
            Package(FindServerAppHost()),
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        var run = composition.RunAsync(input, output, timeout.Token);
        try
        {
            var firstHostProcessId = await launcher.FirstHostProcessId
                .WaitAsync(timeout.Token);
            PublicStateSnapshot? initial = null;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var candidate = composition.Supervisor.SnapshotState();
                if (candidate.Host.ReadyForEffects)
                {
                    initial = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }
            Assert.NotNull(initial);
            Assert.Equal(1, initial.Host.Generation?.Value);
            var firstHostBootId = initial.Host.BootId;

            using (var firstHost = Process.GetProcessById(firstHostProcessId))
            {
                firstHost.Kill();
                await firstHost.WaitForExitAsync(timeout.Token);
            }

            var failedReplacementProcessId = await launcher.FailedReplacementProcessId
                .WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
            var recoveredHostProcessId = await launcher.RecoveredHostProcessId
                .WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
            Assert.NotEqual(firstHostProcessId, failedReplacementProcessId);
            Assert.NotEqual(failedReplacementProcessId, recoveredHostProcessId);

            PublicStateSnapshot? recovered = null;
            for (var attempt = 0; attempt < 400; attempt++)
            {
                var candidate = composition.Supervisor.SnapshotState();
                if (candidate.Host.ReadyForEffects)
                {
                    recovered = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }
            Assert.NotNull(recovered);
            Assert.Equal(PublicHostState.Ready, recovered.Host.State);
            Assert.Equal(3, recovered.Host.Generation?.Value);
            Assert.NotEqual(firstHostBootId, recovered.Host.BootId);
            var session = Assert.Single(recovered.Sessions);
            Assert.True(session.ReadyForEffects);
            Assert.True(session.WarmStateLost);
            Assert.Equal(BootstrapState.Restored, session.BootstrapState);
            Assert.Equal(3, launcher.LaunchCount);

            input.CompleteWriting();
            await run.WaitAsync(timeout.Token);
        }
        finally
        {
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Windows_composition_keeps_a_real_job_tombstone_and_sealed_output()
    {
        if (!OperatingSystem.IsWindows()) return;

        var auditRoot = TemporaryRoot("job-tombstone-audit");
        var outputRoot = TemporaryRoot("job-tombstone-output");
        var launcher = new GatedContainmentLauncher();
        var composition = ProductionGuardianComposition.Create(
            Package(FindServerAppHost()),
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-host-job-tombstone-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);
            var firstHostProcessId = await launcher.FirstHostProcessId.WaitAsync(timeout.Token);

            var startedResponse = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "Write-Output 'PTK_R5_SEALED_JOB_OUTPUT'",
                        raw = true,
                        route = "pwsh",
                        background = true,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            var startedText = ToolText(startedResponse, expectedError: false);
            var jobId = MarkerInteger(startedText, "[job ");

            string? completedStatus = null;
            string? lastStatus = null;
            var requestId = 3;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var statusResponse = await RequestAsync(
                    writer,
                    reader,
                    requestId++,
                    "tools/call",
                    new
                    {
                        name = "ptk_job",
                        arguments = new
                        {
                            action = "status",
                            id = jobId,
                            session = "default",
                        },
                    },
                    timeout.Token);
                var candidate = ToolText(statusResponse, expectedError: false);
                lastStatus = candidate;
                if (candidate.Contains("exited 0", StringComparison.Ordinal) &&
                    candidate.Contains(
                        "recovery=available: ptk_output handle=",
                        StringComparison.Ordinal))
                {
                    completedStatus = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }
            Assert.True(
                completedStatus is not null,
                $"The background job did not publish a sealed terminal. Last status: {lastStatus}");

            using (var firstHost = Process.GetProcessById(firstHostProcessId))
            {
                firstHost.Kill();
                await firstHost.WaitForExitAsync(timeout.Token);
                Assert.True(firstHost.HasExited);
            }
            await launcher.FirstContainmentConfirmed.WaitAsync(timeout.Token);
            Assert.Equal(1, launcher.LaunchCount);

            var tombstoneStatusResponse = await RequestAsync(
                writer,
                reader,
                requestId++,
                "tools/call",
                new
                {
                    name = "ptk_job",
                    arguments = new
                    {
                        action = "status",
                        id = jobId,
                        session = "default",
                    },
                },
                timeout.Token);
            var tombstoneStatus = ToolText(
                tombstoneStatusResponse,
                expectedError: false);
            Assert.Contains(
                $"job {jobId}: exited 0 (original host generation lost)",
                tombstoneStatus,
                StringComparison.Ordinal);
            Assert.Contains(
                "recovery=handle: ptk_output handle=",
                tombstoneStatus,
                StringComparison.Ordinal);

            var tombstoneOutputResponse = await RequestAsync(
                writer,
                reader,
                requestId++,
                "tools/call",
                new
                {
                    name = "ptk_job",
                    arguments = new
                    {
                        action = "output",
                        id = jobId,
                        offset = 0,
                        session = "default",
                    },
                },
                timeout.Token);
            var tombstoneOutput = ToolText(
                tombstoneOutputResponse,
                expectedError: false);
            Assert.Contains(
                "PTK_R5_SEALED_JOB_OUTPUT",
                tombstoneOutput,
                StringComparison.Ordinal);
            Assert.Contains(
                $"[job {jobId} exited 0 (original host generation lost)] next offset:",
                tombstoneOutput,
                StringComparison.Ordinal);

            var tombstoneListResponse = await RequestAsync(
                writer,
                reader,
                requestId++,
                "tools/call",
                new
                {
                    name = "ptk_job",
                    arguments = new
                    {
                        action = "list",
                        session = "default",
                    },
                },
                timeout.Token);
            var tombstoneList = ToolText(
                tombstoneListResponse,
                expectedError: false);
            Assert.Contains(
                $"job {jobId}: exited 0 (original host generation lost)",
                tombstoneList,
                StringComparison.Ordinal);
            Assert.Equal(1, launcher.LaunchCount);

            launcher.ReleaseFirstContainmentConfirmation();
            var replacementHostProcessId = await launcher.ReplacementHostProcessId
                .WaitAsync(timeout.Token);
            Assert.NotEqual(firstHostProcessId, replacementHostProcessId);

            PublicStateSnapshot? recovered = null;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var stateResponse = await RequestAsync(
                    writer,
                    reader,
                    requestId++,
                    "tools/call",
                    new
                    {
                        name = "ptk_state",
                        arguments = new { },
                    },
                    timeout.Token);
                var candidate = PublicStateCodec.Decode(
                    Encoding.UTF8.GetBytes(ToolText(stateResponse, expectedError: false)));
                if (candidate.Host.ReadyForEffects)
                {
                    recovered = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }
            Assert.NotNull(recovered);
            Assert.Equal(2, recovered.Host.Generation?.Value);

            var confirmedStatusResponse = await RequestAsync(
                writer,
                reader,
                requestId,
                "tools/call",
                new
                {
                    name = "ptk_job",
                    arguments = new
                    {
                        action = "status",
                        id = jobId,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "exited 0 (original host generation lost)",
                ToolText(confirmedStatusResponse, expectedError: false),
                StringComparison.Ordinal);
            Assert.Equal(2, launcher.LaunchCount);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
            Assert.Equal(0, composition.Supervisor.OutstandingCallCount);
            Assert.Equal(0, composition.Supervisor.BackgroundTaskCount);
            Assert.Equal(0, composition.Supervisor.OwnedClientCount);
            Assert.Equal(0, composition.Supervisor.OwnedAttemptWatcherSetCount);
        }
        finally
        {
            launcher.ReleaseFirstContainmentConfirmation();
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task Windows_composition_never_replays_a_real_effect_when_the_host_dies()
    {
        if (!OperatingSystem.IsWindows()) return;

        var auditRoot = TemporaryRoot("outcome-unknown-audit");
        var outputRoot = TemporaryRoot("outcome-unknown-output");
        var launcher = new GatedContainmentLauncher();
        launcher.ReleaseFirstContainmentConfirmation();
        var composition = ProductionGuardianComposition.Create(
            Package(FindServerAppHost()),
            LocalAudit(auditRoot),
            launcher,
            OutputOptions(outputRoot),
            guardianBootId: Guardian,
            defaultWorkerBootId: Worker);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var input = new R3BoundedOneWayStream();
        using var output = new R3BoundedOneWayStream();
        using var writer = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        using var reader = new StreamReader(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var standardError = new StringWriter();
        var run = Program.RunAsync(
            [],
            input,
            output,
            standardError,
            productionComposition: composition,
            cancellationToken: timeout.Token);
        try
        {
            var initialized = await RequestAsync(
                writer,
                reader,
                requestId: 1,
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "production-host-outcome-unknown-test",
                        version = "1.0.0",
                    },
                },
                timeout.Token);
            Assert.True(initialized.TryGetProperty("result", out _), initialized.GetRawText());
            await WriteAsync(
                writer,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { },
                },
                timeout.Token);
            var firstHostProcessId = await launcher.FirstHostProcessId.WaitAsync(timeout.Token);

            var ambiguousResponse = await RequestAsync(
                writer,
                reader,
                requestId: 2,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "[System.Diagnostics.Process]::GetCurrentProcess().Kill()",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            var ambiguous = PublicRecoveryCodec.Decode(
                Encoding.UTF8.GetBytes(ToolText(ambiguousResponse, expectedError: true)));
            Assert.Equal(PublicRecoveryDetailCode.OutcomeUnknown, ambiguous.DetailCode);
            Assert.False(ambiguous.Retryable);
            Assert.Null(ambiguous.RetryAfterMilliseconds);
            Assert.Null(ambiguous.RecoveryPhase);
            Assert.Null(ambiguous.RecoveryAttempt);
            Assert.Null(ambiguous.RetryGate);

            var replacementHostProcessId = await launcher.ReplacementHostProcessId
                .WaitAsync(timeout.Token);
            Assert.NotEqual(firstHostProcessId, replacementHostProcessId);

            PublicStateSnapshot? recovered = null;
            var requestId = 3;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var stateResponse = await RequestAsync(
                    writer,
                    reader,
                    requestId++,
                    "tools/call",
                    new
                    {
                        name = "ptk_state",
                        arguments = new { },
                    },
                    timeout.Token);
                var candidate = PublicStateCodec.Decode(
                    Encoding.UTF8.GetBytes(ToolText(stateResponse, expectedError: false)));
                if (candidate.Host.ReadyForEffects)
                {
                    recovered = candidate;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }

            Assert.NotNull(recovered);
            Assert.Equal(PublicHostState.Ready, recovered.Host.State);
            Assert.Equal(2, recovered.Host.Generation?.Value);
            Assert.True(Assert.Single(recovered.Sessions).WarmStateLost);
            Assert.Equal(2, launcher.LaunchCount);

            var invocation = await RequestAsync(
                writer,
                reader,
                requestId,
                "tools/call",
                new
                {
                    name = "ptk_invoke",
                    arguments = new
                    {
                        script = "Write-Output 'effect-was-not-replayed'",
                        raw = true,
                        route = "pwsh",
                        background = false,
                        timeoutSeconds = 10,
                        session = "default",
                    },
                },
                timeout.Token);
            Assert.Contains(
                "effect-was-not-replayed",
                ToolText(invocation, expectedError: false),
                StringComparison.Ordinal);
            Assert.Equal(2, launcher.LaunchCount);

            input.CompleteWriting();
            Assert.Equal(0, await run.WaitAsync(timeout.Token));
            Assert.Equal(string.Empty, standardError.ToString());
            Assert.Equal(0, composition.Supervisor.OutstandingCallCount);
            Assert.Equal(0, composition.Supervisor.BackgroundTaskCount);
            Assert.Equal(0, composition.Supervisor.OwnedClientCount);
            Assert.Equal(0, composition.Supervisor.OwnedAttemptWatcherSetCount);
        }
        finally
        {
            launcher.ReleaseFirstContainmentConfirmation();
            input.CompleteWriting();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            await composition.DisposeAsync();
            DeleteRoot(auditRoot);
        }

        Assert.False(Directory.Exists(outputRoot));
    }

    private static async Task<JsonElement> RequestAsync(
        StreamWriter writer,
        StreamReader reader,
        int requestId,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        await WriteAsync(
            writer,
            new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["method"] = method,
                ["params"] = parameters,
            },
            cancellationToken);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            Assert.NotNull(line);
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (message.TryGetProperty("id", out var responseId) &&
                responseId.ValueKind == JsonValueKind.Number &&
                responseId.GetInt32() == requestId)
            {
                return message.Clone();
            }
        }
    }

    private static async Task WriteAsync(
        StreamWriter writer,
        object message,
        CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(message);
        await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static string ToolText(JsonElement response, bool expectedError)
    {
        var result = response.GetProperty("result");
        Assert.Equal(expectedError, result.GetProperty("isError").GetBoolean());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        return Assert.IsType<string>(content.GetProperty("text").GetString());
    }

    private static int MarkerInteger(string text, string marker)
    {
        var markerOffset = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerOffset >= 0, $"Marker '{marker}' was absent from '{text}'.");
        var start = checked(markerOffset + marker.Length);
        var end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
            end++;
        Assert.True(end > start, $"Marker '{marker}' had no integer in '{text}'.");
        return int.Parse(
            text.AsSpan(start, end - start),
            NumberStyles.None,
            CultureInfo.InvariantCulture);
    }

    private static AuditStartupConfiguration LocalAudit(string root) =>
        AuditStartupConfiguration.Load(
            root,
            configuredExportPath: null,
            static (_, _) => throw new InvalidOperationException(
                "Local-only test audit must not load export configuration."));

    private static OutputStoreOptions OutputOptions(string root) => new(
        root,
        TimeSpan.FromMinutes(5),
        TimeSpan.FromHours(1),
        MaximumArtifactBytes: 1024 * 1024,
        MaximumSessionBytes: 4 * 1024 * 1024,
        MaximumAggregateBytes: 8 * 1024 * 1024);

    private static KeyValuePair<string, string>[] ParentEnvironmentWith(
        string name,
        string value)
    {
        var environment = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment.Add(
                Assert.IsType<string>(entry.Key),
                Assert.IsType<string>(entry.Value));
        }
        environment[name] = value;
        return [.. environment];
    }

    private static MatchedPackageFacts Package(string hostAppHost) => new(
        hostAppHost,
        Digest('1'),
        Digest('2'),
        PublicToolContractResource.ComputeDigest(),
        Digest('6'),
        []);

    private static string FindServerAppHost()
    {
        var configurationDirectory = Directory.GetParent(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) ??
            throw new InvalidOperationException("The test configuration directory is unavailable.");
        var repositoryRoot = FindRepositoryRoot();
        var path = Path.Combine(
            repositoryRoot,
            "server",
            "PtkMcpServer",
            "bin",
            configurationDirectory.Name,
            "net10.0",
            "PtkMcpServer.exe");
        Assert.True(File.Exists(path), $"The private host apphost is absent: {path}");
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static string TemporaryRoot(string kind) => Path.Combine(
        Path.GetTempPath(),
        $"ptk-production-guardian-{kind}-{Guid.NewGuid():N}");

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static Sha256Digest Digest(char value) => new(new string(value, 64));

    private sealed class NeverLauncher : IPrivateHostProcessLauncher
    {
        public PrivateHostProcessLaunchResult Launch(PrivateHostLaunchCommand command) =>
            throw new InvalidOperationException("The construction test must not launch a host.");
    }

    private sealed class GatedContainmentLauncher : IPrivateHostProcessLauncher
    {
        private readonly WindowsPrivateHostProcessLauncher _inner = new();
        private readonly TaskCompletionSource<int> _firstHostProcessId = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstContainmentConfirmed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstContainmentRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _replacementHostProcessId = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _launchCount;

        internal Task<int> FirstHostProcessId => _firstHostProcessId.Task;

        internal Task FirstContainmentConfirmed => _firstContainmentConfirmed.Task;

        internal Task<int> ReplacementHostProcessId => _replacementHostProcessId.Task;

        internal int LaunchCount => Volatile.Read(ref _launchCount);

        internal void ReleaseFirstContainmentConfirmation() =>
            _firstContainmentRelease.TrySetResult();

        public PrivateHostProcessLaunchResult Launch(PrivateHostLaunchCommand command)
        {
            var launchNumber = Interlocked.Increment(ref _launchCount);
            var result = _inner.Launch(command);
            if (result.Outcome != GuardianHostLaunchOutcome.Started)
                return result;

            var process = result.LaunchedHost!;
            if (launchNumber == 1)
            {
                _firstHostProcessId.TrySetResult(process.ProcessId);
                return new PrivateHostProcessLaunchResult(
                    GuardianHostLaunchOutcome.Started,
                    new GatedContainmentProcess(
                        process,
                        _firstContainmentConfirmed,
                        _firstContainmentRelease.Task));
            }

            if (launchNumber == 2)
                _replacementHostProcessId.TrySetResult(process.ProcessId);
            return result;
        }

        private sealed class GatedContainmentProcess : IPrivateHostLaunchedProcess
        {
            private readonly IPrivateHostLaunchedProcess _inner;
            private readonly Task _containmentConfirmed;

            internal GatedContainmentProcess(
                IPrivateHostLaunchedProcess inner,
                TaskCompletionSource firstContainmentConfirmed,
                Task containmentRelease)
            {
                _inner = inner;
                _containmentConfirmed = ConfirmContainmentAsync(
                    inner.ContainmentConfirmed,
                    firstContainmentConfirmed,
                    containmentRelease);
            }

            public int ProcessId => _inner.ProcessId;

            public Task Exited => _inner.Exited;

            public Task ContainmentConfirmed => _containmentConfirmed;

            public void BeginContainment(GuardianHostContainmentDeadline deadline) =>
                _inner.BeginContainment(deadline);

            public void Dispose() => _inner.Dispose();

            private static async Task ConfirmContainmentAsync(
                Task innerConfirmation,
                TaskCompletionSource firstContainmentConfirmed,
                Task containmentRelease)
            {
                await innerConfirmation.ConfigureAwait(false);
                firstContainmentConfirmed.TrySetResult();
                await containmentRelease.ConfigureAwait(false);
            }
        }
    }

    private sealed class CrashSecondLaunchLauncher : IPrivateHostProcessLauncher
    {
        private readonly WindowsPrivateHostProcessLauncher _inner = new();
        private readonly TaskCompletionSource<int> _firstHostProcessId = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _failedReplacementProcessId = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _recoveredHostProcessId = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _launchCount;

        internal Task<int> FirstHostProcessId => _firstHostProcessId.Task;

        internal Task<int> FailedReplacementProcessId =>
            _failedReplacementProcessId.Task;

        internal Task<int> RecoveredHostProcessId => _recoveredHostProcessId.Task;

        internal int LaunchCount => Volatile.Read(ref _launchCount);

        public PrivateHostProcessLaunchResult Launch(PrivateHostLaunchCommand command)
        {
            var launchNumber = Interlocked.Increment(ref _launchCount);
            var result = _inner.Launch(command);
            if (result.Outcome != GuardianHostLaunchOutcome.Started)
                return result;

            var processId = result.LaunchedHost!.ProcessId;
            if (launchNumber == 1)
            {
                _firstHostProcessId.TrySetResult(processId);
            }
            else if (launchNumber == 2)
            {
                _failedReplacementProcessId.TrySetResult(processId);
                using var process = Process.GetProcessById(processId);
                process.Kill();
            }
            else if (launchNumber == 3)
            {
                _recoveredHostProcessId.TrySetResult(processId);
            }
            return result;
        }
    }

    private enum RealDispatchBarrier
    {
        BeforeWriteAuthorization,
        WriteStarting,
        TerminalDecoded,
    }

    private sealed class RealHostKillingDispatchObserver(
        RealDispatchBarrier barrier,
        GatedContainmentLauncher launcher) : IGuardianHostSupervisorDispatchObserver
    {
        private readonly TaskCompletionSource _triggered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claimed;

        internal Task Triggered => _triggered.Task;

        public async ValueTask BeforeWriteAuthorizationAsync(
            GuardianHostDispatchObservation observation,
            CancellationToken cancellationToken)
        {
            _ = observation;
            if (!TryClaim(RealDispatchBarrier.BeforeWriteAuthorization))
                return;

            await KillFirstHostAsync(cancellationToken).ConfigureAwait(false);
            await launcher.FirstContainmentConfirmed
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public void OnWriteStarting(GuardianHostDispatchObservation observation)
        {
            _ = observation;
            if (TryClaim(RealDispatchBarrier.WriteStarting))
                KillFirstHost();
        }

        public void OnTerminalDecoded(GuardianHostDispatchObservation observation)
        {
            _ = observation;
            if (TryClaim(RealDispatchBarrier.TerminalDecoded))
                KillFirstHost();
        }

        private bool TryClaim(RealDispatchBarrier candidate)
        {
            if (barrier != candidate || Interlocked.Exchange(ref _claimed, 1) != 0)
                return false;
            _triggered.TrySetResult();
            return true;
        }

        private async Task KillFirstHostAsync(CancellationToken cancellationToken)
        {
            var processId = await launcher.FirstHostProcessId
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            using var process = Process.GetProcessById(processId);
            process.Kill();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        private void KillFirstHost()
        {
            var processId = launcher.FirstHostProcessId.GetAwaiter().GetResult();
            using var process = Process.GetProcessById(processId);
            process.Kill();
        }
    }
}
