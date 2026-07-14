using System.Collections.Immutable;
using System.Security.Cryptography;
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
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 2)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 2)]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 3)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 3)]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 4)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 4)]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 5)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 5)]
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

    [Theory]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 7)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 7)]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 8)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 8)]
    public async Task Bash_validator_audit_failure_never_starts_submitted_script(
        int pointValue,
        int failingAppend)
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash")) return;

        var point = (AuditSinkFaultPoint)pointValue;
        var dependencyRoot = Directory.CreateTempSubdirectory("ptk-bash-audit-fault-").FullName;
        _roots.Add(dependencyRoot);
        var rtkMarker = Path.Combine(dependencyRoot, "bash-rtk-started");
        var userMarker = Path.Combine(dependencyRoot, "bash-user-started");
        var rtkStub = Path.Combine(dependencyRoot, "rtk-proxy-stub");
        File.WriteAllText(
            rtkStub,
            "#!/bin/sh\n" +
            $"printf started > '{rtkMarker.Replace("'", "'\\''")}'\n" +
            "[ \"$1\" = proxy ] || exit 91\n" +
            "shift\n" +
            "[ \"$1\" = -- ] && shift\n" +
            "exec \"$@\"\n");
        File.SetUnixFileMode(
            rtkStub,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var fixture = CreateFixture(
            journalFault: (current, call) => current == point && call == failingAppend,
            bashPathOverride: "/bin/bash",
            rtkPathOverride: rtkStub);
        var script =
            "cat <<'EOF' >/dev/null\nvalidator-only\nEOF\n" +
            $"printf ran > '{userMarker.Replace("'", "'\\''")}'";

        var result = await fixture.Filter(
            Call("ptk_invoke", ("script", script)),
            async token => Text(await InvokeTool.Invoke(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                script,
                token,
                auditContext: fixture.AuditContext)));

        Assert.False(File.Exists(rtkMarker));
        Assert.False(File.Exists(userMarker));
        Assert.True(result.IsError);
        AssertNoStartRefusal(result);
        Assert.DoesNotContain("execution.completed", fixture.EventTypes());
    }

    [Fact]
    public async Task Bash_success_executes_once_and_writes_the_exact_audit_lifecycle()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash")) return;

        var dependencyRoot = Directory.CreateTempSubdirectory("ptk-bash-audit-success-").FullName;
        _roots.Add(dependencyRoot);
        var rtkCount = Path.Combine(dependencyRoot, "rtk-count");
        var userCount = Path.Combine(dependencyRoot, "user-count");
        var rtkStub = Path.Combine(dependencyRoot, "rtk-proxy-stub");
        File.WriteAllText(
            rtkStub,
            "#!/bin/sh\n" +
            $"printf 1 >> '{rtkCount.Replace("'", "'\\''")}'\n" +
            "[ \"$1\" = proxy ] || exit 91\n" +
            "shift\n" +
            "[ \"$1\" = -- ] || exit 92\n" +
            "shift\n" +
            "exec \"$@\"\n");
        File.SetUnixFileMode(
            rtkStub,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var fixture = CreateFixture(
            bashPathOverride: "/bin/bash",
            rtkPathOverride: rtkStub);
        var script =
            "cat <<'EOF' >/dev/null\nvalidator-only\nEOF\n" +
            $"printf 1 >> '{userCount.Replace("'", "'\\''")}'";

        var result = await fixture.Filter(
            Call("ptk_invoke", ("script", script)),
            async token => Text(await InvokeTool.Invoke(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                script,
                token,
                auditContext: fixture.AuditContext)));

        Assert.False(result.IsError ?? false);
        Assert.Equal("1", File.ReadAllText(rtkCount));
        Assert.Equal("1", File.ReadAllText(userCount));
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
            fixture.EventTypes());
        var dispatched = fixture.Events().Single(value =>
            value.GetProperty("event_type").GetString() == "execution.dispatched");
        var expectedBash = BashExecutableIdentity.TryCapture("/bin/bash");
        Assert.NotNull(expectedBash);
        Assert.Equal(
            expectedBash.AuditIdentityCode,
            dispatched.GetProperty("outcome").GetProperty("detail_code").GetString());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rtkStub))).ToLowerInvariant(),
            dispatched.GetProperty("routing").GetProperty("rtk_binary_digest").GetString());
    }

    [Theory]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 9)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 9)]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 10)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 10)]
    public async Task Bash_terminal_audit_failure_after_execution_never_claims_no_start(
        int pointValue,
        int failingAppend)
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash")) return;

        var dependencyRoot = Directory.CreateTempSubdirectory("ptk-bash-audit-terminal-").FullName;
        _roots.Add(dependencyRoot);
        var rtkCount = Path.Combine(dependencyRoot, "rtk-count");
        var userCount = Path.Combine(dependencyRoot, "user-count");
        var rtkStub = Path.Combine(dependencyRoot, "rtk-proxy-stub");
        File.WriteAllText(
            rtkStub,
            "#!/bin/sh\n" +
            $"printf 1 >> '{rtkCount.Replace("'", "'\\''")}'\n" +
            "shift 2\n" +
            "exec \"$@\"\n");
        File.SetUnixFileMode(
            rtkStub,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var point = (AuditSinkFaultPoint)pointValue;
        using var fixture = CreateFixture(
            journalFault: (current, call) => current == point && call == failingAppend,
            bashPathOverride: "/bin/bash",
            rtkPathOverride: rtkStub);
        var script =
            "cat <<'EOF' >/dev/null\nvalidator-only\nEOF\n" +
            $"printf 1 >> '{userCount.Replace("'", "'\\''")}'";

        var result = await fixture.Filter(
            Call("ptk_invoke", ("script", script)),
            async token => Text(await InvokeTool.Invoke(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                script,
                token,
                auditContext: fixture.AuditContext)));

        Assert.False(result.IsError ?? false);
        Assert.Equal("1", File.ReadAllText(rtkCount));
        Assert.Equal("1", File.ReadAllText(userCount));
        Assert.DoesNotContain(
            "original operation was not started",
            ResultText(result),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("execution.validator_completed", fixture.EventTypes());
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
    public async Task Foreground_rtk_log_shaping_is_an_explicit_audit_lifecycle_fact()
    {
        var dependencyRoot = Directory.CreateTempSubdirectory("ptk-shaping-audit-").FullName;
        _roots.Add(dependencyRoot);
        var invocationMarker = Path.Combine(dependencyRoot, "rtk-log-invoked");
        var rtkStub = Path.Combine(dependencyRoot, "rtk-log.ps1");
        File.WriteAllText(
            rtkStub,
            "param($verb, $path)\n" +
            $"[IO.File]::AppendAllText('{invocationMarker.Replace("'", "''")}', '1')\n" +
            "'AUDITED_RTK_LOG'\n");
        using var fixture = CreateFixture(rtkPathOverride: rtkStub);
        const string script =
            "1..8 | ForEach-Object { \"2026-07-12 10:00:0$_ INFO worker: step $_\" }";

        var result = await fixture.Filter(
            Call("ptk_invoke", ("script", script)),
            async token => Text(await InvokeTool.Invoke(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                script,
                token,
                auditContext: fixture.AuditContext)));

        Assert.False(result.IsError ?? false);
        Assert.Contains("AUDITED_RTK_LOG", ResultText(result), StringComparison.Ordinal);
        Assert.True(
            File.Exists(invocationMarker),
            $"RTK marker missing; durable events: {string.Join(", ", fixture.EventTypes())}");
        Assert.Equal("1", File.ReadAllText(invocationMarker));
        Assert.Equal(
            [
                "call.accepted",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "execution.planned",
                "execution.dispatched",
                "output.shaped",
                "execution.completed",
                "call.completed",
            ],
            fixture.EventTypes());
        var shaped = fixture.Events().Single(value =>
            value.GetProperty("event_type").GetString() == "output.shaped");
        var dispatched = fixture.Events().Single(value =>
            value.GetProperty("event_type").GetString() == "execution.dispatched");
        var expectedDigest =
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rtkStub))).ToLowerInvariant();
        Assert.Equal(
            expectedDigest,
            dispatched.GetProperty("routing").GetProperty("rtk_binary_digest").GetString());
        Assert.Equal(
            "rtk_log_authorized",
            dispatched.GetProperty("outcome").GetProperty("detail_code").GetString());
        Assert.Equal("rtk_log_used", shaped.GetProperty("outcome").GetProperty("detail_code").GetString());
        Assert.Equal("rtk_filtered", shaped.GetProperty("routing").GetProperty("provenance").GetString());
        Assert.Equal(
            expectedDigest,
            shaped.GetProperty("routing").GetProperty("rtk_binary_digest").GetString());
    }

    [Fact]
    public async Task Same_call_rtk_log_replacement_is_refused_and_audited_without_execution()
    {
        var dependencyRoot = Directory.CreateTempSubdirectory("ptk-shaping-replacement-").FullName;
        _roots.Add(dependencyRoot);
        var replacementMarker = Path.Combine(dependencyRoot, "replacement-invoked");
        var rtkStub = Path.Combine(dependencyRoot, "rtk-log.ps1");
        File.WriteAllText(rtkStub, "param($verb, $path) 'ORIGINAL_RTK_LOG'\n");
        var expectedDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rtkStub)))
            .ToLowerInvariant();
        using var fixture = CreateFixture(rtkPathOverride: rtkStub);
        var replacement =
            "param($verb, $path) " +
            $"[IO.File]::AppendAllText('{replacementMarker.Replace("'", "''")}', '1'); " +
            "'REPLACEMENT_RTK_LOG'";
        var script =
            $"Set-Content -LiteralPath '{rtkStub.Replace("'", "''")}' -Value '{replacement.Replace("'", "''")}' -NoNewline; " +
            "1..8 | ForEach-Object { \"2026-07-12 10:00:0$_ INFO worker: step $_\" }";

        var result = await fixture.Filter(
            Call("ptk_invoke", ("script", script)),
            async token => Text(await InvokeTool.Invoke(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                script,
                token,
                auditContext: fixture.AuditContext)));

        Assert.False(result.IsError ?? false);
        Assert.Contains("[ptk:log rtk not found", ResultText(result), StringComparison.Ordinal);
        Assert.DoesNotContain("REPLACEMENT_RTK_LOG", ResultText(result), StringComparison.Ordinal);
        Assert.False(File.Exists(replacementMarker));
        var failed = fixture.Events().Single(value =>
            value.GetProperty("event_type").GetString() == "output.shaping_failed");
        Assert.Equal(
            "rtk_log_identity_unavailable",
            failed.GetProperty("outcome").GetProperty("detail_code").GetString());
        Assert.Equal("direct_text", failed.GetProperty("routing").GetProperty("provenance").GetString());
        Assert.Equal(
            expectedDigest,
            failed.GetProperty("routing").GetProperty("rtk_binary_digest").GetString());
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

    [Fact]
    public async Task Cold_background_policy_refusal_records_request_without_plan_or_effect()
    {
        var cwdProbes = 0;
        var processStarts = 0;
        var outputReservations = 0;
        using var fixture = CreateFixture(
            allowColdBackground: false,
            outputReservationStartingForTests: () =>
                Interlocked.Increment(ref outputReservations));
        fixture.Host.CurrentLocationReaderOverrideForTests = () =>
        {
            Interlocked.Increment(ref cwdProbes);
            throw new InvalidOperationException("cold-background policy was checked too late");
        };
        fixture.Jobs.BeforeProcessStartForTests = _ =>
        {
            Interlocked.Increment(ref processStarts);
            throw new InvalidOperationException("cold-background process started");
        };

        var result = await fixture.Filter(
            Call(
                "ptk_invoke",
                ("script", "'must not run'"),
                ("route", "pwsh"),
                ("background", true)),
            async token => Text(await InvokeTool.Invoke(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                "'must not run'",
                token,
                route: "pwsh",
                background: true,
                auditContext: fixture.AuditContext,
                outputStore: fixture.OutputStore)));

        Assert.False(result.IsError ?? false);
        Assert.Contains("[job not started]", ResultText(result), StringComparison.Ordinal);
        Assert.Equal(0, cwdProbes);
        Assert.Equal(0, processStarts);
        Assert.Equal(0, outputReservations);
        Assert.Empty(fixture.Jobs.List());
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "jobs")));
        Assert.Equal(
            [
                "call.accepted",
                "job.start_requested",
                "job.not_started",
                "call.not_started",
            ],
            fixture.EventTypes());
        var notStarted = fixture.Events().Single(value =>
            value.GetProperty("event_type").GetString() == "job.not_started");
        Assert.Equal(
            "cold_background_disabled",
            notStarted.GetProperty("outcome").GetProperty("detail_code").GetString());
        var coverage = notStarted.GetProperty("coverage");
        Assert.Equal("none", coverage.GetProperty("root_process_observed").GetString());
        Assert.Equal("none", coverage.GetProperty("descendants_observed").GetString());
        Assert.Equal("none", coverage.GetProperty("remote_effect_observed").GetString());
        Assert.Equal(
            notStarted.GetProperty("correlation").GetProperty("job_id").GetInt64(),
            fixture.Events().Single(value =>
                    value.GetProperty("event_type").GetString() == "job.start_requested")
                .GetProperty("correlation").GetProperty("job_id").GetInt64());
    }

    [Theory]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 2)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 2)]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 3)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 3)]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend, 4)]
    [InlineData((int)AuditSinkFaultPoint.Flush, 4)]
    public async Task Cold_background_policy_audit_failure_is_generic_and_has_no_effect(
        int pointValue,
        int failingAppend)
    {
        var point = (AuditSinkFaultPoint)pointValue;
        var cwdProbes = 0;
        var processStarts = 0;
        var outputReservations = 0;
        using var fixture = CreateFixture(
            journalFault: (current, append) => current == point && append == failingAppend,
            allowColdBackground: false,
            outputReservationStartingForTests: () =>
                Interlocked.Increment(ref outputReservations));
        fixture.Host.CurrentLocationReaderOverrideForTests = () =>
        {
            Interlocked.Increment(ref cwdProbes);
            throw new InvalidOperationException("cold-background policy was checked too late");
        };
        fixture.Jobs.BeforeProcessStartForTests = _ =>
        {
            Interlocked.Increment(ref processStarts);
            throw new InvalidOperationException("cold-background process started");
        };

        var result = await fixture.Filter(
            Call(
                "ptk_invoke",
                ("script", "'must not run'"),
                ("route", "pwsh"),
                ("background", true)),
            async token => Text(await InvokeTool.Invoke(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                "'must not run'",
                token,
                route: "pwsh",
                background: true,
                auditContext: fixture.AuditContext,
                outputStore: fixture.OutputStore)));

        Assert.True(result.IsError);
        AssertNoStartRefusal(result);
        Assert.Equal(0, cwdProbes);
        Assert.Equal(0, processStarts);
        Assert.Equal(0, outputReservations);
        Assert.Empty(fixture.Jobs.List());
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "jobs")));
        Assert.Equal(0, fixture.Journal.ReservedBytes);
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

    [Fact]
    public async Task Reset_reports_partial_effect_when_a_job_kill_request_fails()
    {
        using var fixture = CreateFixture();
        var job = fixture.Jobs.Start("Start-Sleep -Seconds 300");
        fixture.Jobs.BeforeKillForTests = _ =>
            throw new InvalidOperationException("injected kill failure");
        try
        {
            var result = await fixture.Filter(
                Call("ptk_reset"),
                async token => Text(await ResetTool.Reset(
                    fixture.Host,
                    fixture.Jobs,
                    token,
                    fixture.AuditContext)));

            Assert.Contains("1 kill request(s) failed", ResultText(result), StringComparison.Ordinal);
            Assert.True(fixture.Jobs.Snapshot(job.Id)!.Running);
            var partial = fixture.Events().Single(value =>
                value.GetProperty("event_type").GetString() == "reset.partial_effect");
            Assert.Equal(
                "runspace_recycled_job_kill_failed",
                partial.GetProperty("outcome").GetProperty("detail_code").GetString());
            Assert.True(partial.GetProperty("outcome").GetProperty("warm_state_lost").GetBoolean());
        }
        finally
        {
            fixture.Jobs.BeforeKillForTests = null;
        }
    }

    [Fact]
    public async Task Reset_failure_after_job_kill_never_claims_runspace_recycled()
    {
        using var fixture = CreateFixture();
        var job = fixture.Jobs.Start("Start-Sleep -Seconds 300");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Filter(
                Call("ptk_reset"),
                _ => new ValueTask<CallToolResult>(ResetAndWrapAsync())));

        async Task<CallToolResult> ResetAndWrapAsync() => Text(await ResetTool.Reset(
            fixture.Host,
            fixture.Jobs,
            canceled.Token,
            fixture.AuditContext));

        await WaitUntilAsync(() => fixture.Jobs.Snapshot(job.Id)?.Running == false);
        var events = fixture.Events();
        Assert.DoesNotContain(events, value =>
            value.GetProperty("event_type").GetString() == "runspace.recycled");
        var partial = events.Single(value =>
            value.GetProperty("event_type").GetString() == "reset.partial_effect");
        var outcome = partial.GetProperty("outcome");
        Assert.Equal("jobs_killed_runspace_outcome_unknown", outcome.GetProperty("detail_code").GetString());
        Assert.Equal("unknown", outcome.GetProperty("termination_certainty").GetString());
        Assert.Equal(JsonValueKind.Null, outcome.GetProperty("warm_state_lost").ValueKind);
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
    public async Task Job_output_access_is_durable_with_raw_bytes_and_next_offset_before_release()
    {
        using var fixture = CreateFixture();
        var job = fixture.Jobs.Start("'audited-output'");
        await WaitUntilAsync(() => fixture.Jobs.Snapshot(job.Id)?.Running == false);

        var result = await fixture.Filter(
            Call("ptk_job", ("action", "output"), ("id", job.Id), ("offset", 0L)),
            async token => Text(await JobTool.Job(
                fixture.Host,
                fixture.Jobs,
                "output",
                token,
                job.Id,
                0,
                fixture.AuditContext)));

        Assert.Contains("audited-output", ResultText(result), StringComparison.Ordinal);
        var access = fixture.Events().Single(value =>
            value.GetProperty("event_type").GetString() == "job.output_accessed");
        var outcome = access.GetProperty("outcome");
        var rawBytes = new FileInfo(job.OutputPath).Length;
        Assert.Equal(rawBytes, outcome.GetProperty("bytes_returned").GetInt64());
        Assert.Equal(rawBytes, outcome.GetProperty("next_offset").GetInt64());
    }

    [Fact]
    public async Task Job_output_rtk_log_shaping_is_audited_after_raw_access()
    {
        var dependencyRoot = Directory.CreateTempSubdirectory("ptk-job-shaping-audit-").FullName;
        _roots.Add(dependencyRoot);
        var invocationMarker = Path.Combine(dependencyRoot, "rtk-log-invoked");
        var rtkStub = Path.Combine(dependencyRoot, "rtk-log.ps1");
        File.WriteAllText(
            rtkStub,
            "param($verb, $path)\n" +
            $"[IO.File]::AppendAllText('{invocationMarker.Replace("'", "''")}', '1')\n" +
            "'AUDITED_JOB_RTK_LOG'\n");
        using var fixture = CreateFixture(rtkPathOverride: rtkStub);
        var job = fixture.Jobs.Start(
            "1..8 | ForEach-Object { \"2026-07-12 10:00:0$_ INFO worker: step $_\" }");
        await WaitUntilAsync(() => fixture.Jobs.Snapshot(job.Id)?.Running == false);

        var result = await fixture.Filter(
            Call("ptk_job", ("action", "output"), ("id", job.Id), ("offset", 0L)),
            async token => Text(await JobTool.Job(
                fixture.Host,
                fixture.Jobs,
                "output",
                token,
                job.Id,
                0,
                fixture.AuditContext)));

        Assert.Contains("AUDITED_JOB_RTK_LOG", ResultText(result), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "rtk capture unsupported",
            ResultText(result),
            StringComparison.Ordinal);
        Assert.Equal("1", File.ReadAllText(invocationMarker));
        var eventTypes = fixture.EventTypes();
        Assert.True(
            eventTypes.IndexOf("job.output_accessed") < eventTypes.IndexOf("output.shaped"));
        var shaped = fixture.Events().Single(value =>
            value.GetProperty("event_type").GetString() == "output.shaped");
        var access = fixture.Events().Single(value =>
            value.GetProperty("event_type").GetString() == "job.output_accessed");
        var expectedDigest =
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rtkStub))).ToLowerInvariant();
        Assert.Equal(
            expectedDigest,
            access.GetProperty("routing").GetProperty("rtk_binary_digest").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            access.GetProperty("routing").GetProperty("domain").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            access.GetProperty("routing").GetProperty("requested_route").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            access.GetProperty("routing").GetProperty("effective_route").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            access.GetProperty("routing").GetProperty("provenance").ValueKind);
        Assert.Equal(
            "rtk_log_authorized",
            access.GetProperty("outcome").GetProperty("detail_code").GetString());
        Assert.Equal("rtk_log_used", shaped.GetProperty("outcome").GetProperty("detail_code").GetString());
        Assert.Equal("rtk_filtered", shaped.GetProperty("routing").GetProperty("provenance").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rtk_unknown_job_output_preserves_source_route_without_second_rtk(
        bool shapingFails)
    {
        var dependencyRoot = Directory.CreateTempSubdirectory("ptk-job-rtk-poll-audit-").FullName;
        _roots.Add(dependencyRoot);
        var shapingMarker = Path.Combine(dependencyRoot, "rtk-log-must-not-run");
        var shapingStub = Path.Combine(dependencyRoot, "rtk-log.ps1");
        File.WriteAllText(
            shapingStub,
            "param($verb, $path)\n" +
            $"[IO.File]::AppendAllText('{shapingMarker.Replace("'", "''")}', '1')\n" +
            "'SECOND_RTK_MUST_NOT_RUN'\n");
        using var fixture = CreateFixture(rtkPathOverride: shapingStub);
        if (shapingFails)
        {
            fixture.Host.OutputShapingFailureForTests = () =>
                throw new InvalidOperationException("forced RTK cleanup failure");
        }
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
            : File.Exists("/bin/sh") ? "/bin/sh" : "/usr/bin/sh";
        var rtkExecutable = RtkTestStub.CreatePassthrough(dependencyRoot).Path;
        var identity = RtkExecutableIdentity.TryCapture(rtkExecutable);
        Assert.NotNull(identity);
        var targetIdentity = ColdCommandTargetIdentity.TryCapture(
            executable,
            new ResolvedCommand(
                System.Management.Automation.CommandTypes.Application,
                executable,
                executable),
            dependencyRoot);
        Assert.NotNull(targetIdentity);
        var command = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,8) do @echo 2026-07-13 10:00:00 INFO worker: step %i"
            : "i=1; while [ \"$i\" -le 8 ]; do echo \"2026-07-13 10:00:00 INFO worker: step $i\"; i=$((i + 1)); done";
        var arguments = OperatingSystem.IsWindows()
            ? ImmutableArray.Create(executable, "/d", "/s", "/c", command)
            : ImmutableArray.Create(executable, "-c", command);
        var plan = new ExecutionPlan(
            originalScript: "typed RTK audit polling fixture",
            executionScript: null,
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Cold,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            [ExecutionPath.PowerShellDirect],
            fallbackReason: null,
            identity,
            workingDirectory: dependencyRoot,
            rtkArgumentVector: arguments,
            directFallbackProvenance: OutputProvenance.DirectText,
            coldCommandTargetIdentity: targetIdentity);
        var start = fixture.Jobs.PrepareStart(
            ExecutionDispatch.FromPlan(plan),
            dependencyRoot);
        var job = fixture.Jobs.CommitStart(start);
        Assert.True(fixture.Jobs.ConfirmStartRecorded(job.Id));
        await WaitUntilAsync(() => fixture.Jobs.Snapshot(job.Id)?.Running == false);

        var result = await fixture.Filter(
            Call("ptk_job", ("action", "output"), ("id", job.Id), ("offset", 0L)),
            async token => Text(await JobTool.Job(
                fixture.Host,
                fixture.Jobs,
                "output",
                token,
                job.Id,
                0,
                fixture.AuditContext)));

        var response = ResultText(result);
        Assert.Contains("step 8", response, StringComparison.Ordinal);
        Assert.Contains(
            "recovery=unavailable: rtk capture unsupported",
            response,
            StringComparison.Ordinal);
        Assert.False(File.Exists(shapingMarker), "generic rtk log ran on already-RTK output");
        Assert.DoesNotContain("output.shaped", fixture.EventTypes());
        var outputEvents = fixture.Events();
        if (shapingFails)
        {
            Assert.Contains("[ptk: shaping failed; raw text returned]", response, StringComparison.Ordinal);
            var failure = outputEvents.Single(value =>
                value.GetProperty("event_type").GetString() == "output.shaping_failed");
            AssertSourceRouting(failure);
        }
        else
        {
            Assert.DoesNotContain("output.shaping_failed", fixture.EventTypes());
        }
        var access = outputEvents.Single(value =>
            value.GetProperty("event_type").GetString() == "job.output_accessed");
        AssertSourceRouting(access);
        Assert.Equal(
            JsonValueKind.Null,
            access.GetProperty("outcome").GetProperty("detail_code").ValueKind);
        AssertSourceRouting(outputEvents.Last(value =>
            value.GetProperty("event_type").GetString() == "call.completed"));

        _ = await fixture.Filter(
            Call("ptk_job", ("action", "status"), ("id", job.Id)),
            async token => Text(await JobTool.Job(
                fixture.Host,
                fixture.Jobs,
                "status",
                token,
                job.Id,
                auditContext: fixture.AuditContext)));
        var status = fixture.Events().Single(value =>
            value.GetProperty("event_type").GetString() == "job.status_accessed");
        AssertSourceRouting(status);

        void AssertSourceRouting(JsonElement auditEvent)
        {
            var routing = auditEvent.GetProperty("routing");
            Assert.Equal("native_terminal", routing.GetProperty("domain").GetString());
            Assert.Equal("auto", routing.GetProperty("requested_route").GetString());
            Assert.Equal("rtk", routing.GetProperty("effective_route").GetString());
            Assert.Equal("rtk_unknown", routing.GetProperty("provenance").GetString());
            Assert.Equal(
                identity.AuditBinaryDigest,
                routing.GetProperty("rtk_binary_digest").GetString());
        }
    }

    [Fact]
    public async Task Job_output_shaping_pipeline_failure_is_a_typed_audit_fact()
    {
        var dependencyRoot = Directory.CreateTempSubdirectory("ptk-job-shaping-failure-").FullName;
        _roots.Add(dependencyRoot);
        var rtkStub = Path.Combine(dependencyRoot, "rtk-log.ps1");
        File.WriteAllText(rtkStub, "param($verb, $path) 'SHOULD_NOT_RUN'\n");
        using var fixture = CreateFixture(rtkPathOverride: rtkStub);
        fixture.Host.OutputShapingFailureForTests = () =>
            throw new InvalidOperationException("forced shaping pipeline failure");
        var job = fixture.Jobs.Start(
            "1..8 | ForEach-Object { \"2026-07-12 10:00:0$_ INFO worker: step $_\" }");
        await WaitUntilAsync(() => fixture.Jobs.Snapshot(job.Id)?.Running == false);

        var result = await fixture.Filter(
            Call("ptk_job", ("action", "output"), ("id", job.Id), ("offset", 0L)),
            async token => Text(await JobTool.Job(
                fixture.Host,
                fixture.Jobs,
                "output",
                token,
                job.Id,
                0,
                fixture.AuditContext)));

        Assert.Contains("step 8", ResultText(result), StringComparison.Ordinal);
        Assert.Contains(
            "[ptk: shaping failed; raw text returned]",
            ResultText(result),
            StringComparison.Ordinal);
        var failed = fixture.Events().Single(value =>
            value.GetProperty("event_type").GetString() == "output.shaping_failed");
        Assert.Equal(
            "output_shaping_pipeline_failed",
            failed.GetProperty("outcome").GetProperty("detail_code").GetString());
        Assert.Equal("failed", failed.GetProperty("outcome").GetProperty("state").GetString());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rtkStub))).ToLowerInvariant(),
            failed.GetProperty("routing").GetProperty("rtk_binary_digest").GetString());
    }

    [Fact]
    public async Task Job_output_access_pre_authorizes_rtk_before_a_post_effect_audit_fault()
    {
        var dependencyRoot = Directory.CreateTempSubdirectory("ptk-job-shaping-fault-").FullName;
        _roots.Add(dependencyRoot);
        var invocationMarker = Path.Combine(dependencyRoot, "rtk-log-invoked");
        var rtkStub = Path.Combine(dependencyRoot, "rtk-log.ps1");
        File.WriteAllText(
            rtkStub,
            "param($verb, $path)\n" +
            $"[IO.File]::AppendAllText('{invocationMarker.Replace("'", "''")}', '1')\n" +
            "'AUDITED_JOB_RTK_LOG'\n");
        using var fixture = CreateFixture(
            rtkPathOverride: rtkStub,
            journalFault: (point, append) =>
                point == AuditSinkFaultPoint.BeforeAppend && append == 4);
        var job = fixture.Jobs.Start(
            "1..8 | ForEach-Object { \"2026-07-12 10:00:0$_ INFO worker: step $_\" }");
        await WaitUntilAsync(() => fixture.Jobs.Snapshot(job.Id)?.Running == false);

        _ = await fixture.Filter(
            Call("ptk_job", ("action", "output"), ("id", job.Id), ("offset", 0L)),
            async token => Text(await JobTool.Job(
                fixture.Host,
                fixture.Jobs,
                "output",
                token,
                job.Id,
                0,
                fixture.AuditContext)));

        Assert.True(
            File.Exists(invocationMarker),
            $"RTK marker missing; durable events: {string.Join(", ", fixture.EventTypes())}");
        Assert.Equal("1", File.ReadAllText(invocationMarker));
        Assert.DoesNotContain("output.shaped", fixture.EventTypes());
        var access = fixture.Events().Single(value =>
            value.GetProperty("event_type").GetString() == "job.output_accessed");
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rtkStub))).ToLowerInvariant(),
            access.GetProperty("routing").GetProperty("rtk_binary_digest").GetString());
        Assert.Equal(
            "rtk_log_authorized",
            access.GetProperty("outcome").GetProperty("detail_code").GetString());
    }

    [Fact]
    public async Task Job_output_access_persistence_failure_releases_no_tool_result()
    {
        using var fixture = CreateFixture(
            journalFault: (point, append) => point == AuditSinkFaultPoint.Flush && append == 3);
        var job = fixture.Jobs.Start("'must-not-be-released'");
        await WaitUntilAsync(() => fixture.Jobs.Snapshot(job.Id)?.Running == false);

        await Assert.ThrowsAsync<AuditUnavailableException>(async () =>
            await fixture.Filter(
                Call("ptk_job", ("action", "output"), ("id", job.Id), ("offset", 0L)),
                async token => Text(await JobTool.Job(
                    fixture.Host,
                    fixture.Jobs,
                    "output",
                    token,
                    job.Id,
                    0,
                    fixture.AuditContext))));
    }

    [Fact]
    public async Task Output_read_is_durable_before_captured_bytes_are_released()
    {
        using var fixture = CreateFixture();
        var sealedArtifact = SealOutput(fixture.OutputStore, "audited recovery");

        var result = await fixture.Filter(
            Call(
                "ptk_output",
                ("handle", sealedArtifact.Handle!),
                ("action", "read"),
                ("offset", 0L),
                ("maxBytes", 64)),
            token => ValueTask.FromResult(Text(OutputTool.Output(
                fixture.OutputStore,
                sealedArtifact.Handle!,
                "read",
                0,
                64,
                cancellationToken: token,
                auditContext: fixture.AuditContext))));

        Assert.Contains("audited recovery", ResultText(result), StringComparison.Ordinal);
        Assert.Equal(
            ["call.accepted", "output.read_accessed", "call.completed"],
            fixture.EventTypes());
        var access = fixture.Events().Single(value =>
            value.GetProperty("event_type").GetString() == "output.read_accessed");
        var request = access.GetProperty("request");
        Assert.Equal(
            fixture.OutputProtector.HandleDigest(sealedArtifact.Handle!),
            request.GetProperty("output_handle_digest").GetString());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("pattern_fingerprint").ValueKind);
        var outcome = access.GetProperty("outcome");
        Assert.Equal(sealedArtifact.Bytes, outcome.GetProperty("bytes_returned").GetInt64());
        Assert.Equal(sealedArtifact.Bytes, outcome.GetProperty("next_offset").GetInt64());
        var auditText = string.Concat(fixture.Sink.Lines);
        Assert.DoesNotContain(sealedArtifact.Handle!, auditText, StringComparison.Ordinal);
        Assert.DoesNotContain("audited recovery", auditText, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.OutputStore.RootPathForTests, auditText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_search_audits_only_the_domain_separated_pattern_fingerprint()
    {
        using var fixture = CreateFixture();
        var sealedArtifact = SealOutput(fixture.OutputStore, "alpha needle omega");
        const string pattern = "needle";

        var result = await fixture.Filter(
            Call(
                "ptk_output",
                ("handle", sealedArtifact.Handle!),
                ("action", "search"),
                ("pattern", pattern),
                ("offset", 0L),
                ("maxBytes", 64)),
            token => ValueTask.FromResult(Text(OutputTool.Output(
                fixture.OutputStore,
                sealedArtifact.Handle!,
                "search",
                0,
                64,
                pattern,
                token,
                fixture.AuditContext))));

        Assert.Contains("offset=6", ResultText(result), StringComparison.Ordinal);
        Assert.Equal(
            ["call.accepted", "output.search_accessed", "call.completed"],
            fixture.EventTypes());
        var access = fixture.Events().Single(value =>
            value.GetProperty("event_type").GetString() == "output.search_accessed");
        Assert.Equal(
            fixture.OutputProtector.PatternFingerprint(pattern),
            access.GetProperty("request").GetProperty("pattern_fingerprint").GetString());
        Assert.Equal(
            sealedArtifact.Bytes,
            access.GetProperty("outcome").GetProperty("next_offset").GetInt64());
        Assert.DoesNotContain(pattern, string.Concat(fixture.Sink.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_status_is_durable_before_availability_is_released()
    {
        using var fixture = CreateFixture();
        var sealedArtifact = SealOutput(fixture.OutputStore, "status snapshot");

        var result = await fixture.Filter(
            Call("ptk_output", ("handle", sealedArtifact.Handle!), ("action", "status")),
            token => ValueTask.FromResult(Text(OutputTool.Output(
                fixture.OutputStore,
                sealedArtifact.Handle!,
                "status",
                cancellationToken: token,
                auditContext: fixture.AuditContext))));

        Assert.Contains("state=available", ResultText(result), StringComparison.Ordinal);
        Assert.Equal(
            ["call.accepted", "output.status_accessed", "call.completed"],
            fixture.EventTypes());
        var access = fixture.Events().Single(value =>
            value.GetProperty("event_type").GetString() == "output.status_accessed");
        Assert.Equal(
            fixture.OutputProtector.HandleDigest(sealedArtifact.Handle!),
            access.GetProperty("request").GetProperty("output_handle_digest").GetString());
    }

    [Fact]
    public async Task Output_access_persistence_failure_releases_no_recovery_result()
    {
        using var fixture = CreateFixture(
            journalFault: (point, append) => point == AuditSinkFaultPoint.Flush && append == 2);
        var sealedArtifact = SealOutput(fixture.OutputStore, "must-not-be-released");

        await Assert.ThrowsAsync<AuditUnavailableException>(async () =>
            await fixture.Filter(
                Call("ptk_output", ("handle", sealedArtifact.Handle!)),
                token => ValueTask.FromResult(Text(OutputTool.Output(
                    fixture.OutputStore,
                    sealedArtifact.Handle!,
                    cancellationToken: token,
                    auditContext: fixture.AuditContext)))));

        // The failed disclosure consumed no artifact cursor and never removed
        // the immutable snapshot; a later healthy supervisor call could read it.
        Assert.Contains(
            "must-not-be-released",
            fixture.OutputStore.Read(sealedArtifact.Handle!, 0, 64).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_admission_failure_never_enters_the_handler()
    {
        using var fixture = CreateFixture(
            journalFault: (point, append) => point == AuditSinkFaultPoint.BeforeAppend && append == 1);
        var sealedArtifact = SealOutput(fixture.OutputStore, "withheld");
        var handlerCalled = false;

        var result = await fixture.Filter(
            Call("ptk_output", ("handle", sealedArtifact.Handle!)),
            _ =>
            {
                handlerCalled = true;
                return ValueTask.FromResult(Text("leaked"));
            });

        Assert.False(handlerCalled);
        Assert.True(result.IsError);
        AssertNoStartRefusal(result);
    }

    [Fact]
    public async Task Output_capacity_refusal_cannot_use_the_emergency_state_path()
    {
        using var fixture = CreateFixture();
        Assert.True(fixture.Journal.TryReserve(28, out var pressure, out var reserveFailure), reserveFailure);
        using (pressure)
        {
            var handlerCalled = false;
            var result = await fixture.Filter(
                Call("ptk_output", ("handle", "ptko_capacity-test")),
                _ =>
                {
                    handlerCalled = true;
                    return ValueTask.FromResult(Text("leaked"));
                });

            Assert.False(handlerCalled);
            Assert.True(result.IsError);
            AssertNoStartRefusal(result);
            Assert.DoesNotContain("audit emergency state", ResultText(result), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(["audit.degraded"], fixture.EventTypes());
        }
    }

    [Fact]
    public async Task Read_only_job_and_state_calls_emit_specific_access_outcomes()
    {
        using var fixture = CreateFixture();

        _ = await fixture.Filter(
            Call("ptk_job", ("action", "list")),
            async token => Text(await JobTool.Job(
                fixture.Host,
                fixture.Jobs,
                "list",
                token,
                auditContext: fixture.AuditContext)));
        _ = await fixture.Filter(
            Call("ptk_job", ("action", "status"), ("id", 999L)),
            async token => Text(await JobTool.Job(
                fixture.Host,
                fixture.Jobs,
                "status",
                token,
                999,
                auditContext: fixture.AuditContext)));
        _ = await fixture.Filter(
            Call("ptk_state"),
            async token => Text(await StateTool.State(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                listAvailable: false,
                cancellationToken: token,
                auditContext: fixture.AuditContext)));

        var events = fixture.Events();
        Assert.Single(events, value => value.GetProperty("event_type").GetString() == "job.list_accessed");
        var status = Assert.Single(events, value =>
            value.GetProperty("event_type").GetString() == "job.status_accessed");
        Assert.Equal("not_found", status.GetProperty("outcome").GetProperty("state").GetString());
        Assert.Single(events, value =>
            value.GetProperty("event_type").GetString() == "state.probe_completed");
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
                "job.start_requested",
                "execution.validation_started",
                "execution.prepare_authorized",
                "execution.validation_completed",
                "execution.planned",
                "execution.dispatched",
                "job.started",
                "call.completed",
                "job.completed",
            ],
            events);
    }

    [Fact]
    public async Task Successful_cold_rtk_job_preserves_rtk_terminal_routing()
    {
        var (rtkDirectory, rtkPath) = RtkTestStub.CreatePassthrough();
        var savedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var savedPathExt = Environment.GetEnvironmentVariable("PATHEXT");
        var savedMarker = Environment.GetEnvironmentVariable("PTK_COLD_AUDIT_MARKER");
        try
        {
            using var fixture = CreateFixture(rtkPathOverride: rtkPath);
            var marker = Path.Combine(fixture.Root, "cold-audit-rtk-starts.txt");
            var targetBody = OperatingSystem.IsWindows()
                ? ">>\"%PTK_COLD_AUDIT_MARKER%\" echo x\necho AUDITED_RTK_TARGET %*\nexit /b 0"
                : "printf 'x\\n' >> \"$PTK_COLD_AUDIT_MARKER\"\n" +
                  "printf 'AUDITED_RTK_TARGET %s\\n' \"$*\"\nexit 0";
            var (targetDirectory, _) = RtkTestStub.Create(
                targetBody,
                fixture.Root,
                "ptk-audit-rtk-target");
            Environment.SetEnvironmentVariable(
                "PATH",
                targetDirectory.FullName + Path.PathSeparator + savedPath);
            if (OperatingSystem.IsWindows())
                Environment.SetEnvironmentVariable("PATHEXT", ".EXE");
            Environment.SetEnvironmentVariable("PTK_COLD_AUDIT_MARKER", marker);
            var attempts = new List<ExecutionPath>();
            fixture.Jobs.BeforeProcessStartForTests = plan =>
                attempts.Add(plan.ExecutionPath);
            const string script = "ptk-audit-rtk-target ARG";

            var result = await fixture.Filter(
                Call(
                    "ptk_invoke",
                    ("script", script),
                    ("route", "rtk"),
                    ("background", true)),
                async token => Text(await InvokeTool.Invoke(
                    fixture.Host,
                    fixture.Jobs,
                    fixture.RawUsage,
                    script,
                    token,
                    route: "rtk",
                    background: true,
                    auditContext: fixture.AuditContext)));

            Assert.False(result.IsError ?? false);
            Assert.Contains("[job 1 started]", ResultText(result), StringComparison.Ordinal);
            var job = Assert.Single(fixture.Jobs.List());
            await WaitUntilAsync(() => fixture.Jobs.Snapshot(job.Id)?.Running == false);
            await WaitUntilAsync(() =>
                fixture.EventTypes().Count(type => type == "job.completed") == 1);

            Assert.Equal([ExecutionPath.Rtk], attempts);
            Assert.Equal(["x"], File.ReadAllLines(marker));
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
                    "call.completed",
                    "job.completed",
                ],
                events.Select(value =>
                    value.GetProperty("event_type").GetString()));
            foreach (var actual in events.Where(value =>
                         value.GetProperty("event_type").GetString() is
                             "execution.dispatched" or
                             "job.started" or
                             "call.completed" or
                             "job.completed"))
            {
                var routing = actual.GetProperty("routing");
                Assert.Equal(
                    "rtk",
                    routing.GetProperty("effective_route").GetString());
                Assert.Equal(
                    "rtk_unknown",
                    routing.GetProperty("provenance").GetString());
                Assert.Equal(
                    JsonValueKind.Null,
                    routing.GetProperty("fallback_reason").ValueKind);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Environment.SetEnvironmentVariable("PATHEXT", savedPathExt);
            Environment.SetEnvironmentVariable("PTK_COLD_AUDIT_MARKER", savedMarker);
            rtkDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Proved_rtk_no_start_audits_one_fallback_and_terminal_actual_route()
    {
        var (rtkDirectory, rtkPath) = RtkTestStub.CreatePassthrough();
        var savedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var savedPathExt = Environment.GetEnvironmentVariable("PATHEXT");
        var savedMarker = Environment.GetEnvironmentVariable("PTK_COLD_AUDIT_MARKER");
        try
        {
            using var fixture = CreateFixture(rtkPathOverride: rtkPath);
            var marker = Path.Combine(fixture.Root, "cold-audit-target-starts.txt");
            var targetBody = OperatingSystem.IsWindows()
                ? ">>\"%PTK_COLD_AUDIT_MARKER%\" echo x\necho AUDITED_COLD_TARGET %*\nexit /b 0"
                : "printf 'x\\n' >> \"$PTK_COLD_AUDIT_MARKER\"\n" +
                  "printf 'AUDITED_COLD_TARGET %s\\n' \"$*\"\nexit 0";
            var (targetDirectory, _) = RtkTestStub.Create(
                targetBody,
                fixture.Root,
                "ptk-audit-fallback-target");
            Environment.SetEnvironmentVariable(
                "PATH",
                targetDirectory.FullName + Path.PathSeparator + savedPath);
            if (OperatingSystem.IsWindows())
                Environment.SetEnvironmentVariable("PATHEXT", ".EXE");
            Environment.SetEnvironmentVariable("PTK_COLD_AUDIT_MARKER", marker);
            var attempts = new List<ExecutionPath>();
            fixture.Jobs.BeforeProcessStartForTests = plan =>
                attempts.Add(plan.ExecutionPath);
            var processStarts = 0;
            fixture.Jobs.ProcessStartOverrideForTests = process =>
            {
                var attempt = Interlocked.Increment(ref processStarts);
                return attempt == 1 ? false : process.Start();
            };
            const string script = "ptk-audit-fallback-target ARG";

            var result = await fixture.Filter(
                Call(
                    "ptk_invoke",
                    ("script", script),
                    ("route", "rtk"),
                    ("background", true)),
                async token => Text(await InvokeTool.Invoke(
                    fixture.Host,
                    fixture.Jobs,
                    fixture.RawUsage,
                    script,
                    token,
                    route: "rtk",
                    background: true,
                    auditContext: fixture.AuditContext)));

            Assert.False(result.IsError ?? false);
            Assert.Contains(
                "requested=rtk effective=powershell_direct " +
                "fallback=rtk_execution_preparation_failed",
                ResultText(result),
                StringComparison.Ordinal);
            var job = Assert.Single(fixture.Jobs.List());
            await WaitUntilAsync(() => fixture.Jobs.Snapshot(job.Id)?.Running == false);
            await WaitUntilAsync(() =>
                fixture.EventTypes().Count(type => type == "job.completed") == 1);

            Assert.Equal([ExecutionPath.Rtk, ExecutionPath.PowerShellDirect], attempts);
            Assert.Equal(2, processStarts);
            Assert.Equal(["x"], File.ReadAllLines(marker));
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
                events.Select(value =>
                    value.GetProperty("event_type").GetString()));
            Assert.Equal(
                "rtk",
                events[5].GetProperty("routing").GetProperty("effective_route").GetString());
            Assert.Equal(
                "rtk",
                events[6].GetProperty("routing").GetProperty("effective_route").GetString());
            foreach (var actual in events.Skip(7))
            {
                var routing = actual.GetProperty("routing");
                Assert.Equal(
                    "powershell_direct",
                    routing.GetProperty("effective_route").GetString());
                Assert.Equal(
                    "direct_text",
                    routing.GetProperty("provenance").GetString());
                Assert.Equal(
                    "rtk_execution_preparation_failed",
                    routing.GetProperty("fallback_reason").GetString());
            }
            Assert.Single(events
                .Select(value => value.GetProperty("correlation").GetProperty("plan_id"))
                .Where(value => value.ValueKind != JsonValueKind.Null)
                .Select(value => value.GetGuid())
                .Distinct());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Environment.SetEnvironmentVariable("PATHEXT", savedPathExt);
            Environment.SetEnvironmentVariable("PTK_COLD_AUDIT_MARKER", savedMarker);
            rtkDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData((int)AuditSinkFaultPoint.BeforeAppend)]
    [InlineData((int)AuditSinkFaultPoint.Flush)]
    public async Task Fallback_dispatch_persistence_failure_starts_nothing(
        int pointValue)
    {
        var (rtkDirectory, rtkPath) = RtkTestStub.CreatePassthrough();
        var savedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var savedPathExt = Environment.GetEnvironmentVariable("PATHEXT");
        var savedMarker = Environment.GetEnvironmentVariable("PTK_COLD_AUDIT_MARKER");
        try
        {
            var point = (AuditSinkFaultPoint)pointValue;
            using var fixture = CreateFixture(
                journalFault: (current, append) =>
                    current == point && append == 8,
                rtkPathOverride: rtkPath);
            var marker = Path.Combine(fixture.Root, "forbidden-fallback-start.txt");
            var targetBody = OperatingSystem.IsWindows()
                ? ">>\"%PTK_COLD_AUDIT_MARKER%\" echo x\nexit /b 0"
                : "printf 'x\\n' >> \"$PTK_COLD_AUDIT_MARKER\"\nexit 0";
            var (targetDirectory, _) = RtkTestStub.Create(
                targetBody,
                fixture.Root,
                "ptk-audit-fault-target");
            Environment.SetEnvironmentVariable(
                "PATH",
                targetDirectory.FullName + Path.PathSeparator + savedPath);
            if (OperatingSystem.IsWindows())
                Environment.SetEnvironmentVariable("PATHEXT", ".EXE");
            Environment.SetEnvironmentVariable("PTK_COLD_AUDIT_MARKER", marker);
            var processStarts = 0;
            fixture.Jobs.ProcessStartOverrideForTests = process =>
            {
                Interlocked.Increment(ref processStarts);
                return false;
            };
            const string script = "ptk-audit-fault-target ARG";

            var result = await fixture.Filter(
                Call(
                    "ptk_invoke",
                    ("script", script),
                    ("route", "rtk"),
                    ("background", true)),
                async token => Text(await InvokeTool.Invoke(
                    fixture.Host,
                    fixture.Jobs,
                    fixture.RawUsage,
                    script,
                    token,
                    route: "rtk",
                    background: true,
                    auditContext: fixture.AuditContext)));

            Assert.True(result.IsError);
            AssertNoStartRefusal(result);
            Assert.Equal(1, processStarts);
            Assert.Empty(fixture.Jobs.List());
            Assert.False(File.Exists(marker));
            var dispatches = fixture.Events()
                .Where(value =>
                    value.GetProperty("event_type").GetString() ==
                    "execution.dispatched")
                .ToArray();
            Assert.InRange(dispatches.Length, 1, 2);
            var initial = dispatches[0];
            Assert.Equal(
                "rtk",
                initial.GetProperty("routing").GetProperty("effective_route").GetString());
            // A flush fault can leave the unflushed candidate visible in this
            // in-memory sink; a before-append fault cannot. Neither authorizes
            // the fallback process, which is the safety boundary under test.
            if (dispatches.Length == 2)
            {
                Assert.Equal(
                    AuditSinkFaultPoint.Flush,
                    point);
                Assert.Equal(
                    "powershell_direct",
                    dispatches[1].GetProperty("routing")
                        .GetProperty("effective_route").GetString());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Environment.SetEnvironmentVariable("PATHEXT", savedPathExt);
            Environment.SetEnvironmentVariable("PTK_COLD_AUDIT_MARKER", savedMarker);
            rtkDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Associated_start_exception_records_started_failed_call_and_one_unknown_terminal()
    {
        using var fixture = CreateFixture();
        var marker = Path.Combine(fixture.Root, "associated-starts.txt");
        var script =
            $"Add-Content -LiteralPath {Literal(marker)} -Value x; Start-Sleep -Seconds 300";
        var processStarts = 0;
        fixture.Jobs.ProcessStartOverrideForTests = process =>
        {
            Assert.True(process.Start());
            Interlocked.Increment(ref processStarts);
            Assert.True(
                SpinWait.SpinUntil(MarkerWasWritten, TimeSpan.FromSeconds(10)),
                "the associated background process never reached its marker");
            throw new IOException("injected host failure after Process.Start");
        };

        var result = await fixture.Filter(
            Call(
                "ptk_invoke",
                ("script", script),
                ("route", "pwsh"),
                ("background", true)),
            async token => Text(await InvokeTool.Invoke(
                fixture.Host,
                fixture.Jobs,
                fixture.RawUsage,
                script,
                token,
                route: "pwsh",
                background: true,
                auditContext: fixture.AuditContext)));

        Assert.Contains("started; outcome unknown", ResultText(result), StringComparison.Ordinal);
        var job = Assert.Single(fixture.Jobs.List());
        await WaitUntilAsync(() => fixture.Jobs.Snapshot(job.Id)?.Running == false);
        await WaitUntilAsync(() =>
            fixture.EventTypes().Count(type => type == "job.outcome_unknown") == 1);

        Assert.Equal(1, processStarts);
        Assert.Equal(["x"], File.ReadAllLines(marker));
        var final = fixture.Jobs.Snapshot(job.Id)!;
        Assert.False(final.StartOutcomeUnknown);
        Assert.True(final.ExecutionOutcomeUnknown);
        Assert.True(final.RootTerminationConfirmed);
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
            events.Select(value => value.GetProperty("event_type").GetString()));
        var terminal = events[^1];
        Assert.Equal(
            "outcome_unknown",
            terminal.GetProperty("outcome").GetProperty("state").GetString());
        Assert.Equal(
            "confirmed",
            terminal.GetProperty("outcome").GetProperty("termination_certainty").GetString());
        Assert.Equal(
            "complete",
            terminal.GetProperty("coverage").GetProperty("root_process_observed").GetString());

        bool MarkerWasWritten()
        {
            try { return File.Exists(marker) && File.ReadAllLines(marker).Length == 1; }
            catch (IOException) { return false; }
        }
    }

    [Fact]
    public async Task Explicit_kill_dispatch_is_unconfirmed_and_terminal_carries_explicit_reason()
    {
        using var fixture = CreateFixture();
        const string script = "Start-Sleep -Seconds 300";
        var start = await fixture.Filter(
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
        Assert.False(start.IsError ?? false);
        var job = Assert.Single(fixture.Jobs.List());

        var kill = await fixture.Filter(
            Call("ptk_job", ("action", "kill"), ("id", job.Id)),
            async token => Text(await JobTool.Job(
                fixture.Host,
                fixture.Jobs,
                "kill",
                token,
                job.Id,
                auditContext: fixture.AuditContext)));
        Assert.Contains("kill requested", ResultText(kill), StringComparison.Ordinal);
        await WaitUntilAsync(() => fixture.EventTypes().Count(type => type == "job.killed") == 1);

        var events = fixture.Events();
        var requestedIndex = events.FindIndex(value =>
            value.GetProperty("event_type").GetString() == "job.kill_requested");
        var dispatchedIndex = events.FindIndex(value =>
            value.GetProperty("event_type").GetString() == "job.kill_dispatched");
        var terminalIndex = events.FindIndex(value =>
            value.GetProperty("event_type").GetString() == "job.killed");
        Assert.True(requestedIndex >= 0 && dispatchedIndex > requestedIndex && terminalIndex > requestedIndex);
        Assert.Equal(
            "not_applicable",
            events[dispatchedIndex].GetProperty("outcome").GetProperty("termination_certainty").GetString());
        Assert.Equal(
            "explicit_kill",
            events[terminalIndex].GetProperty("outcome").GetProperty("detail_code").GetString());
        Assert.Contains(events, value =>
            value.GetProperty("event_type").GetString() == "call.completed" &&
            value.GetProperty("request").GetProperty("tool").GetString() == "ptk_job" &&
            value.GetProperty("request").GetProperty("action").GetString() == "kill");
    }

    [Fact]
    public async Task Kill_noop_and_failure_have_truthful_call_terminals()
    {
        using var fixture = CreateFixture();
        const long missingId = 999;
        _ = await fixture.Filter(
            Call("ptk_job", ("action", "kill"), ("id", missingId)),
            async token => Text(await JobTool.Job(
                fixture.Host,
                fixture.Jobs,
                "kill",
                token,
                missingId,
                auditContext: fixture.AuditContext)));

        var job = fixture.Jobs.Start("Start-Sleep -Seconds 300");
        fixture.Jobs.BeforeKillForTests = _ =>
            throw new InvalidOperationException("injected kill failure");
        try
        {
            _ = await fixture.Filter(
                Call("ptk_job", ("action", "kill"), ("id", job.Id)),
                async token => Text(await JobTool.Job(
                    fixture.Host,
                    fixture.Jobs,
                    "kill",
                    token,
                    job.Id,
                    auditContext: fixture.AuditContext)));
        }
        finally
        {
            fixture.Jobs.BeforeKillForTests = null;
        }

        var events = fixture.Events();
        Assert.Contains(events, value =>
            value.GetProperty("event_type").GetString() == "call.not_started" &&
            value.GetProperty("request").GetProperty("job_id").GetInt64() == missingId);
        Assert.Contains(events, value =>
            value.GetProperty("event_type").GetString() == "call.failed" &&
            value.GetProperty("request").GetProperty("job_id").GetInt64() == job.Id);
    }

    [Fact]
    public async Task Reset_kill_terminal_carries_reset_reason_after_asynchronous_exit()
    {
        using var fixture = CreateFixture();
        const string script = "Start-Sleep -Seconds 300";
        _ = await fixture.Filter(
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

        _ = await fixture.Filter(
            Call("ptk_reset"),
            async token => Text(await ResetTool.Reset(
                fixture.Host,
                fixture.Jobs,
                token,
                fixture.AuditContext)));
        await WaitUntilAsync(() => fixture.EventTypes().Count(type => type == "job.killed") == 1);

        var terminal = fixture.Events().Single(value =>
            value.GetProperty("event_type").GetString() == "job.killed");
        Assert.Equal(
            "reset",
            terminal.GetProperty("outcome").GetProperty("detail_code").GetString());
    }

    private GuardFixture CreateFixture(
        Func<AuditSinkFaultPoint, int, bool>? journalFault = null,
        Action<SecureAuditStorageFaultStage>? evidenceFault = null,
        string? bashPathOverride = null,
        string? rtkPathOverride = null,
        bool allowColdBackground = true,
        Action? outputReservationStartingForTests = null)
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
        var outputStore = new OutputStore(new OutputStoreOptions(
            Path.Combine(root, "output"),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1),
            MaximumArtifactBytes: 1024,
            MaximumSessionBytes: 2048,
            MaximumAggregateBytes: 4096,
            ReservationStartingForTests: outputReservationStartingForTests));
        var outputProtector = new AuditOutputRequestProtector();
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
            new RunspaceHost(
                TimeSpan.FromSeconds(10),
                maxCallTimeout: TimeSpan.FromSeconds(30),
                bashPathOverride: bashPathOverride,
                rtkPathOverride: rtkPathOverride),
            new JobManager(
                JobPwshExecutable.ResolveFromPath(),
                Path.Combine(root, "jobs"),
                allowColdBackground),
            new RawUsageCounter(),
            auditContext,
            outputStore,
            outputProtector);
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

    private static OutputSealResult SealOutput(OutputStore store, string text)
    {
        Assert.True(store.TryReserve("default", out var reservation, out var failure), failure);
        using (reservation)
        {
            return reservation!.Seal(new OutputArtifactContent(
                text,
                [],
                [],
                [],
                null,
                OutputProvenance.PowerShellObjects));
        }
    }

    private sealed record GuardFixture(
        string Root,
        InMemoryAuditJournalSink Sink,
        AuditJournal Journal,
        ServiceProvider Provider,
        RunspaceHost Host,
        JobManager Jobs,
        RawUsageCounter RawUsage,
        AuditCallContextAccessor AuditContext,
        OutputStore OutputStore,
        AuditOutputRequestProtector OutputProtector) : IDisposable
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
                CancellationToken.None,
                OutputProtector);
        }

        internal List<string> EventTypes()
        {
            return Sink.Lines.Select(line =>
            {
                using var document = JsonDocument.Parse(line.AsMemory(0, line.Length - 1));
                return document.RootElement.GetProperty("event_type").GetString()!;
            }).ToList();
        }

        internal List<JsonElement> Events() => Sink.Lines.Select(line =>
        {
            using var document = JsonDocument.Parse(line.AsMemory(0, line.Length - 1));
            return document.RootElement.Clone();
        }).ToList();

        public void Dispose()
        {
            OutputProtector.Dispose();
            OutputStore.Dispose();
            Jobs.Dispose();
            Host.Dispose();
            Provider.Dispose();
            Journal.Dispose();
        }
    }
}
