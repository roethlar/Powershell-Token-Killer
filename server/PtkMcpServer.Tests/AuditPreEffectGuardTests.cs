using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;
using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

[Collection("ProcessEnvironment")]
public sealed class AuditPreEffectGuardTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* Keep the assertion failure authoritative. */ }
        }
    }

    [Theory]
    [InlineData((int)SecureAuditStorageFaultStage.Write)]
    [InlineData((int)SecureAuditStorageFaultStage.Flush)]
    [InlineData((int)SecureAuditStorageFaultStage.Publish)]
    public async Task Evidence_failure_never_enters_the_tool_handler_or_executes_the_script(int stageValue)
    {
        var stage = (SecureAuditStorageFaultStage)stageValue;
        using var fixture = CreateFixture(evidenceFault: current =>
        {
            if (current == stage) throw new IOException("injected evidence failure");
        });
        var marker = Path.Combine(fixture.Root, "foreground-marker");
        var handlerCalled = false;

        var result = await fixture.Filter(
            Call("ptk_invoke", ("script", $"Set-Content -LiteralPath {Literal(marker)} -Value ran")),
            async token =>
            {
                handlerCalled = true;
                return Text(await InvokeTool.Invoke(
                    fixture.Host,
                    fixture.Jobs,
                    fixture.RawUsage,
                    "Set-Content -LiteralPath ignored -Value ran",
                    token,
                    auditContext: fixture.AuditContext));
            });

        Assert.False(handlerCalled);
        Assert.False(File.Exists(marker));
        Assert.True(result.IsError);
        AssertNoStartRefusal(result);
    }

    [Theory]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 4)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 4)]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 6)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 6)]
    public async Task Foreground_authorization_failure_never_executes_user_script(
        int pointValue,
        int failingAppend)
    {
        var point = (AuditSinkFaultPoint)pointValue;
        using var fixture = CreateFixture(
            journalFault: (current, call) => current == point && call == failingAppend);
        var marker = Path.Combine(fixture.Root, "foreground-marker");
        var poison = await fixture.Host.InvokeAsync(
            "function global:Get-PtcShellDialectFinding { param($Script) " +
            $"Set-Content -LiteralPath {Literal(marker)} -Value dialect; $null }}; " +
            "function global:Resolve-PtcInvokeScript { param($Script, $Route) " +
            $"Set-Content -LiteralPath {Literal(marker)} -Value routing; $Script }}; " +
            "@('Get-PtcShellDialectFinding','Resolve-PtcInvokeScript') | " +
            "Microsoft.PowerShell.Core\\ForEach-Object { " +
            "(Microsoft.PowerShell.Core\\Get-Command $_ -CommandType Function).Name }",
            raw: true,
            route: "pwsh");
        Assert.True(
            poison.Success,
            string.Join(Environment.NewLine, poison.Errors));
        Assert.Empty(poison.Errors);
        Assert.Contains("Get-PtcShellDialectFinding", poison.Output);
        Assert.Contains("Resolve-PtcInvokeScript", poison.Output);
        Assert.False(File.Exists(marker));
        var script = $"Set-Content -LiteralPath {Literal(marker)} -Value ran";

        var result = await fixture.Filter(
            Call("ptk_invoke", ("script", script)),
            async token => Text(await InvokeTool.Invoke(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                script,
                token,
                auditContext: fixture.AuditContext)));

        Assert.False(File.Exists(marker));
        Assert.True(result.IsError);
        AssertNoStartRefusal(result);
    }

    [Fact]
    public async Task Foreground_success_writes_the_exact_audit_lifecycle()
    {
        using var fixture = CreateFixture();
        const string script = "'safe-output'";

        var result = await fixture.Filter(
            Call("ptk_invoke", ("script", script), ("raw", true), ("route", "pwsh")),
            async token => Text(await InvokeTool.Invoke(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                script,
                token,
                raw: true,
                route: "pwsh",
                auditContext: fixture.AuditContext)));

        Assert.False(result.IsError ?? false);
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
            fixture.EventTypes());
    }

    [Fact]
    public async Task Full_language_user_script_cannot_reflect_the_ambient_audit_capability()
    {
        using var fixture = CreateFixture();
        const string script = """
            $type = [PtkMcpServer.RunspaceHost].Assembly.GetType('PtkMcpServer.Audit.AuditCallContext')
            $flags = [System.Reflection.BindingFlags]'Static,NonPublic'
            $property = $type.GetProperty('Current', $flags)
            $current = if ($null -eq $property) { $null } else { $property.GetValue($null) }
            if ($null -eq $property -or $null -eq $current) {
                'AUDIT_CAPABILITY_HIDDEN'
            }
            else {
                $instanceFlags = [System.Reflection.BindingFlags]'Instance,NonPublic'
                $complete = $type.GetMethod('CompleteCall', $instanceFlags)
                $arguments = [object[]]@('completed', 'forged', 'not_applicable', $null)
                $complete.Invoke($current, $arguments)
                'AUDIT_CAPABILITY_VISIBLE'
            }
            """;

        var result = await fixture.Filter(
            Call("ptk_invoke", ("script", script), ("raw", true), ("route", "pwsh")),
            async token => Text(await InvokeTool.Invoke(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                script,
                token,
                raw: true,
                route: "pwsh",
                auditContext: fixture.AuditContext)));

        Assert.False(result.IsError ?? false);
        Assert.Contains("AUDIT_CAPABILITY_HIDDEN", ResultText(result), StringComparison.Ordinal);
        Assert.DoesNotContain("AUDIT_CAPABILITY_VISIBLE", ResultText(result), StringComparison.Ordinal);
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
            fixture.EventTypes());
    }

    [Fact]
    public async Task Prepare_command_lookup_cannot_auto_import_module_code_before_dispatch()
    {
        using var fixture = CreateFixture(
            journalFault: (point, call) =>
                point == AuditSinkFaultPoint.BeforeAppend && call == 6);
        var moduleBase = Path.Combine(fixture.Root, "malicious-module-path");
        var moduleRoot = Path.Combine(moduleBase, "PtkAutoImportProbe");
        Directory.CreateDirectory(moduleRoot);
        var marker = Path.Combine(fixture.Root, "auto-import-marker");
        File.WriteAllText(
            Path.Combine(moduleRoot, "PtkAutoImportProbe.psd1"),
            "@{ RootModule = 'PtkAutoImportProbe.psm1'; ModuleVersion = '1.0.0'; " +
            "GUID = '12345678-1234-4abc-8def-0123456789ab'; FunctionsToExport = @('export') }");
        File.WriteAllText(
            Path.Combine(moduleRoot, "PtkAutoImportProbe.psm1"),
            $"Set-Content -LiteralPath {Literal(marker)} -Value imported\n" +
            "function export { param([Parameter(ValueFromRemainingArguments=$true)]$rest) 'ran' }\n" +
            "Export-ModuleMember -Function export\n");
        var savedModulePath = Environment.GetEnvironmentVariable("PSModulePath");
        Environment.SetEnvironmentVariable(
            "PSModulePath",
            moduleBase + Path.PathSeparator + savedModulePath);
        try
        {
            var result = await fixture.Filter(
                Call("ptk_invoke", ("script", "export FOO=1")),
                async token => Text(await InvokeTool.Invoke(
                    fixture.Host,
                    fixture.Jobs,
                    fixture.RawUsage,
                    "export FOO=1",
                    token,
                    auditContext: fixture.AuditContext)));

            Assert.NotEqual(true, result.IsError);
            Assert.Contains("[ptk:dialect]", ResultText(result), StringComparison.Ordinal);
            Assert.False(
                File.Exists(marker),
                "prepare auto-imported a discoverable module before execution.dispatched was durable");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSModulePath", savedModulePath);
        }
    }

    [Theory]
    [InlineData("Get-PtcShellDialectFinding")]
    [InlineData("Resolve-PtcInvokeScript")]
    public async Task Trusted_preflight_ignores_user_debugger_hooks_before_dispatch(string commandName)
    {
        using var fixture = CreateFixture(
            journalFault: (point, call) =>
                point == AuditSinkFaultPoint.BeforeAppend && call == 6);
        var actionMarker = Path.Combine(fixture.Root, "breakpoint-action-marker");
        var updateMarker = Path.Combine(fixture.Root, "breakpoint-update-marker");
        var handlerType =
            "System.EventHandler[System.Management.Automation.BreakpointUpdatedEventArgs]";
        var poison = await fixture.Host.InvokeAsync(
            $"Set-PSBreakpoint -Command {Literal(commandName)} -Action {{ " +
            $"[IO.File]::WriteAllText({Literal(actionMarker)}, 'action') }} | Out-Null; " +
            $"$global:ptkBreakpointUpdateHandler = [{handlerType}] {{ param($sender, $eventArgs) " +
            $"[IO.File]::WriteAllText({Literal(updateMarker)}, 'updated') }}; " +
            "([runspace]::DefaultRunspace).Debugger.add_BreakpointUpdated(" +
            "$global:ptkBreakpointUpdateHandler)",
            raw: true,
            route: "pwsh");
        Assert.True(poison.Success, string.Join(Environment.NewLine, poison.Errors));

        var modeBefore = await fixture.Host.InvokeAsync(
            "([runspace]::DefaultRunspace).Debugger.DebugMode.ToString()",
            raw: true,
            route: "pwsh");
        var result = await fixture.Filter(
            Call("ptk_invoke", ("script", "'safe-output'")),
            async token => Text(await InvokeTool.Invoke(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                "'safe-output'",
                token,
                auditContext: fixture.AuditContext)));
        var modeAfter = await fixture.Host.InvokeAsync(
            "([runspace]::DefaultRunspace).Debugger.DebugMode.ToString()",
            raw: true,
            route: "pwsh");

        Assert.True(result.IsError);
        AssertNoStartRefusal(result);
        Assert.True(modeBefore.Success, string.Join(Environment.NewLine, modeBefore.Errors));
        Assert.True(modeAfter.Success, string.Join(Environment.NewLine, modeAfter.Errors));
        Assert.Equal(modeBefore.Output.Trim(), modeAfter.Output.Trim());
        Assert.False(
            File.Exists(actionMarker),
            $"user command breakpoint ran while {commandName} executed as trusted preflight");
        Assert.False(
            File.Exists(updateMarker),
            "trusted preflight toggled user breakpoints and fired BreakpointUpdated");
    }

    [Theory]
    [InlineData("pre-lookup")]
    [InlineData("post-lookup")]
    [InlineData("not-found")]
    [InlineData("default-parameter")]
    [InlineData("type-data")]
    [InlineData("attributed-autoload-variable")]
    public async Task Trusted_preflight_executes_no_user_ambient_hooks_before_dispatch(string channel)
    {
        using var fixture = CreateFixture(
            journalFault: (point, call) =>
                point == AuditSinkFaultPoint.BeforeAppend && call == 6);
        var marker = Path.Combine(fixture.Root, $"ambient-{channel}-marker");
        var handlerType =
            "System.EventHandler[System.Management.Automation.CommandLookupEventArgs]";
        var poisonScript = channel switch
        {
            "pre-lookup" =>
                $"$global:ptkLookupHandler = [{handlerType}] {{ param($sender, $eventArgs) " +
                $"[IO.File]::WriteAllText({Literal(marker)}, 'pre') }}; " +
                "$ExecutionContext.InvokeCommand.PreCommandLookupAction = $global:ptkLookupHandler",
            "post-lookup" =>
                $"$global:ptkLookupHandler = [{handlerType}] {{ param($sender, $eventArgs) " +
                $"[IO.File]::WriteAllText({Literal(marker)}, 'post') }}; " +
                "$ExecutionContext.InvokeCommand.PostCommandLookupAction = $global:ptkLookupHandler",
            "not-found" =>
                $"$global:ptkLookupHandler = [{handlerType}] {{ param($sender, $eventArgs) " +
                $"[IO.File]::WriteAllText({Literal(marker)}, 'missing') }}; " +
                "$ExecutionContext.InvokeCommand.CommandNotFoundAction = $global:ptkLookupHandler",
            "default-parameter" =>
                "$global:PSDefaultParameterValues = @{ '*:ErrorAction' = { " +
                $"[IO.File]::WriteAllText({Literal(marker)}, 'default'); 'Continue' }} }}",
            "type-data" =>
                "Update-TypeData -TypeName System.Management.Automation.ApplicationInfo " +
                "-MemberType ScriptProperty -MemberName CommandType -Value { " +
                $"[IO.File]::WriteAllText({Literal(marker)}, 'typedata'); " +
                "[System.Management.Automation.CommandTypes]::Application } -Force",
            "attributed-autoload-variable" =>
                $"[ValidateScript({{ [IO.File]::WriteAllText({Literal(marker)}, 'validate'); $true }})]" +
                "[object]$global:PSModuleAutoLoadingPreference = 'All'",
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };
        var poison = await fixture.Host.InvokeAsync(
            poisonScript,
            raw: true,
            route: "pwsh");
        Assert.True(poison.Success, string.Join(Environment.NewLine, poison.Errors));
        File.Delete(marker); // setup itself is outside the behavior under test

        var rtk = Path.Combine(fixture.Root, "rtk-preflight-probe");
        File.WriteAllText(rtk, string.Empty);
        var savedRtk = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        Environment.SetEnvironmentVariable("PTK_RTK_PATH", rtk);
        try
        {
            var submittedScript = channel == "not-found"
                ? "ptk_missing_preflight_command --version"
                : "git --version";
            var result = await fixture.Filter(
                Call("ptk_invoke", ("script", submittedScript)),
                async token => Text(await InvokeTool.Invoke(
                    fixture.Host,
                    fixture.Jobs,
                    fixture.RawUsage,
                    submittedScript,
                    token,
                    auditContext: fixture.AuditContext)));

            Assert.False(
                File.Exists(marker),
                $"trusted preflight executed the user-controlled {channel} channel");
            Assert.True(result.IsError);
            AssertNoStartRefusal(result);

            // Isolation is temporary, not destructive: the user's ambient
            // state must still be present for their next actual pipeline.
            var restorationProbe = channel switch
            {
                "not-found" => "ptk_missing_restoration_probe",
                "type-data" =>
                    "(Microsoft.PowerShell.Core\\Get-Command git).CommandType | Out-Null",
                "attributed-autoload-variable" =>
                    "$global:PSModuleAutoLoadingPreference = 'ModuleQualified'",
                _ => "Get-Date | Out-Null",
            };
            _ = await fixture.Host.InvokeAsync(
                restorationProbe,
                raw: true,
                route: "pwsh");
            Assert.True(
                File.Exists(marker),
                $"trusted preflight did not restore the user-controlled {channel} channel");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", savedRtk);
        }
    }

    [Fact]
    public async Task Shadowed_shaper_cannot_add_an_unsubmitted_effect_after_dispatch()
    {
        using var fixture = CreateFixture();
        var marker = Path.Combine(fixture.Root, "shaper-marker");
        var poison = await fixture.Host.InvokeAsync(
            "function global:Compress-PtcOutput { process { " +
            $"Set-Content -LiteralPath {Literal(marker)} -Value shaped; $_ }}}}; " +
            "(Microsoft.PowerShell.Core\\Get-Command Compress-PtcOutput -CommandType Function).Name",
            raw: true,
            route: "pwsh");
        Assert.True(
            poison.Success,
            string.Join(Environment.NewLine, poison.Errors));
        Assert.Empty(poison.Errors);
        Assert.Contains("Compress-PtcOutput", poison.Output);
        Assert.False(File.Exists(marker));

        var result = await fixture.Filter(
            Call("ptk_invoke", ("script", "'safe-output'")),
            async token => Text(await InvokeTool.Invoke(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                "'safe-output'",
                token,
                auditContext: fixture.AuditContext)));

        Assert.False(result.IsError ?? false);
        Assert.Contains("safe-output", ResultText(result), StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 4)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 4)]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 6)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 6)]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 7)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 7)]
    public async Task Background_authorization_failure_never_starts_a_job(
        int pointValue,
        int failingAppend)
    {
        var point = (AuditSinkFaultPoint)pointValue;
        using var fixture = CreateFixture(
            journalFault: (current, call) => current == point && call == failingAppend);
        var marker = Path.Combine(fixture.Root, "background-marker");
        var poison = await fixture.Host.InvokeAsync(
            "function global:Get-Location { " +
            $"Set-Content -LiteralPath {Literal(marker)} -Value cwd; " +
            "[pscustomobject]@{ Path = $PWD.Path } }",
            raw: true,
            route: "pwsh");
        Assert.True(poison.Success);
        Assert.Empty(poison.Errors);
        var script = $"Set-Content -LiteralPath {Literal(marker)} -Value ran";

        var result = await fixture.Filter(
            Call(
                "ptk_invoke",
                ("script", script),
                ("raw", true),
                ("route", "pwsh"),
                ("background", true)),
            async token => Text(await InvokeTool.Invoke(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                script,
                token,
                raw: true,
                route: "pwsh",
                background: true,
                auditContext: fixture.AuditContext)));

        Assert.Empty(fixture.Jobs.List());
        Assert.False(File.Exists(marker));
        Assert.True(result.IsError);
        AssertNoStartRefusal(result);
    }

    [Theory]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend)]
    [InlineData((int)AuditSinkFaultPoint.Flush)]
    public async Task Reset_authorization_failure_preserves_warm_state(int pointValue)
    {
        var point = (AuditSinkFaultPoint)pointValue;
        using var fixture = CreateFixture(
            journalFault: (current, call) => current == point && call == 2);
        var setup = await fixture.Host.InvokeAsync("$auditResetGuard = 42", raw: true, route: "pwsh");
        Assert.True(setup.Success);

        var result = await fixture.Filter(
            Call("ptk_reset"),
            async token => Text(await ResetTool.Reset(
                fixture.Host,
                fixture.Jobs,
                token,
                fixture.AuditContext)));

        var after = await fixture.Host.InvokeAsync("$auditResetGuard", raw: true, route: "pwsh");
        Assert.Contains("42", after.Output, StringComparison.Ordinal);
        Assert.True(result.IsError);
        AssertNoStartRefusal(result);
    }

    [Theory]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend)]
    [InlineData((int)AuditSinkFaultPoint.Flush)]
    public async Task Kill_authorization_failure_leaves_the_job_running(int pointValue)
    {
        var point = (AuditSinkFaultPoint)pointValue;
        using var fixture = CreateFixture(
            journalFault: (current, call) => current == point && call == 2);
        var job = fixture.Jobs.Start("Start-Sleep -Seconds 30");

        var result = await fixture.Filter(
            Call("ptk_job", ("action", "kill"), ("id", job.Id)),
            async token => Text(await JobTool.Job(
                fixture.Host,
                fixture.Jobs,
                "kill",
                token,
                job.Id,
                auditContext: fixture.AuditContext)));

        Assert.True(fixture.Jobs.Snapshot(job.Id)?.Running);
        Assert.True(result.IsError);
        AssertNoStartRefusal(result);
    }

    [Fact]
    public async Task Started_job_writes_one_terminal_event_without_any_poll_call()
    {
        using var fixture = CreateFixture();
        const string script = "Start-Sleep -Milliseconds 100; 'finished'";

        var result = await fixture.Filter(
            Call(
                "ptk_invoke",
                ("script", script),
                ("raw", true),
                ("route", "pwsh"),
                ("background", true)),
            async token => Text(await InvokeTool.Invoke(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                script,
                token,
                raw: true,
                route: "pwsh",
                background: true,
                auditContext: fixture.AuditContext)));

        Assert.False(result.IsError ?? false);
        var job = Assert.Single(fixture.Jobs.List());
        await WaitUntilAsync(() => fixture.Jobs.Snapshot(job.Id)?.Running == false);
        await WaitUntilAsync(() => fixture.EventTypes().Count(type => type == "job.completed") == 1);

        var events = fixture.EventTypes();
        Assert.Equal(
            [
                "call.accepted",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "execution.planned",
                "execution.dispatched",
                "job.start_requested",
                "job.started",
                "call.completed",
                "job.completed",
            ],
            events);
    }

    private GuardFixture CreateFixture(
        Func<AuditSinkFaultPoint, int, bool>? journalFault = null,
        Action<SecureAuditStorageFaultStage>? evidenceFault = null)
    {
        const int maxRecordBytes = 4096;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "test-audit-guard-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var options = AuditOptions.Create(
            root,
            maxRecordBytes: maxRecordBytes,
            segmentBytes: maxRecordBytes * 32L,
            aggregateBytes: maxRecordBytes * 32L,
            emergencyReserveBytes: maxRecordBytes * 2L,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: maxRecordBytes,
            evidenceAggregateBytes: maxRecordBytes * 16L,
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
            "guard-test",
            binaryDigest: null,
            hostId: Guid.Parse("12345678-1234-4abc-8def-0123456789ab"),
            supervisorBootId: Guid.Parse("22345678-1234-4abc-8def-0123456789ab"));
        var evidence = new ScriptEvidenceStore(options.EvidenceDirectory, evidenceFault);
        var runtime = AuditRuntimeGate.CreateOperationalForTests(
            options, health, journal, evidence);
        var auditContext = new AuditCallContextAccessor();
        var provider = new ServiceCollection()
            .AddSingleton(health)
            .AddSingleton(journal)
            .AddSingleton(evidence)
            .AddSingleton(runtime)
            .AddSingleton(auditContext)
            .BuildServiceProvider();
        return new GuardFixture(
            root,
            sink,
            journal,
            provider,
            new RunspaceHost(TimeSpan.FromSeconds(10), maxCallTimeout: TimeSpan.FromSeconds(30)),
            new JobManager(Path.Combine(root, "jobs")),
            new RawUsageCounter(),
            auditContext);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Condition was not observed.");
            await Task.Delay(25);
        }
    }

    private static CallToolRequestParams Call(string name, params (string Name, object? Value)[] arguments)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (argumentName, value) in arguments)
            values.Add(argumentName, JsonSerializer.SerializeToElement(value));
        return new CallToolRequestParams { Name = name, Arguments = values };
    }

    private static CallToolResult Text(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }],
    };

    private static string ResultText(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static void AssertNoStartRefusal(CallToolResult result)
    {
        var text = ResultText(result);
        Assert.Contains("original operation was not started", text, StringComparison.Ordinal);
        Assert.DoesNotContain("retry", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw=true", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    private sealed record GuardFixture(
        string Root,
        InMemoryAuditJournalSink Sink,
        AuditJournal Journal,
        ServiceProvider Provider,
        RunspaceHost Host,
        JobManager Jobs,
        RawUsageCounter RawUsage,
        AuditCallContextAccessor AuditContext) : IDisposable
    {
        internal async ValueTask<CallToolResult> Filter(
            CallToolRequestParams call,
            Func<CancellationToken, ValueTask<CallToolResult>> next)
        {
            using var scope = Provider.CreateScope();
            return await AuditCallFilter.InvokeAsync(
                call,
                new AuditClientContext("guard-test", "1", "guard-session"),
                scope.ServiceProvider,
                next,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30),
                () => DateTimeOffset.UtcNow,
                CancellationToken.None);
        }

        internal List<string> EventTypes()
        {
            return Sink.Lines.Select(line =>
            {
                using var document = JsonDocument.Parse(line.AsMemory(0, line.Length - 1));
                return document.RootElement.GetProperty("event_type").GetString()!;
            }).ToList();
        }

        public void Dispose()
        {
            Jobs.Dispose();
            Host.Dispose();
            Provider.Dispose();
            Journal.Dispose();
        }
    }
}
