using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

public sealed class RunspaceHostTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60));

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task State_persists_across_calls()
    {
        await _host.InvokeAsync("$x = 41");
        var result = await _host.InvokeAsync("$x + 1");

        Assert.True(result.Success);
        Assert.Equal("42", result.Output.Trim());
        Assert.Equal(InvokeDisposition.Completed, result.Disposition);
        Assert.True(result.UserExecutionStarted);
    }

    [Fact]
    public async Task Imported_module_stays_loaded_across_calls()
    {
        var import = await _host.InvokeAsync(
            "New-Module -Name PtkWarmTest -ScriptBlock { function Get-Warm { 'warm' } } | Import-Module");
        Assert.True(import.Success);

        var result = await _host.InvokeAsync("Get-Warm");

        Assert.True(result.Success);
        Assert.Equal("warm", result.Output.Trim());
    }

    [Fact]
    public async Task Two_stage_capture_and_shaping_preserve_user_runspace_state()
    {
        var workingDirectory = Directory.CreateTempSubdirectory("ptk-capture-state-");
        var outputRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "capture-state-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var escapedPath = workingDirectory.FullName.Replace("'", "''");
            var setup = await _host.InvokeAsync(
                $"Set-Location -LiteralPath '{escapedPath}'; " +
                "$global:captureKeep = 'variable-still-warm'; " +
                "$global:captureConnection = [pscustomobject]@{ State = 'connected' }; " +
                "New-Module -Name PtkCaptureState -ScriptBlock { " +
                "$script:sentinel = 'module-still-warm'; " +
                "function Get-PtkCaptureState { $script:sentinel }; " +
                "Export-ModuleMember -Function Get-PtkCaptureState } | Import-Module; " +
                "Write-Error 'seed-error-entry' -ErrorAction Continue; " +
                "$global:LASTEXITCODE = 37",
                raw: true,
                route: "pwsh");
            Assert.True(setup.Success, string.Join(Environment.NewLine, setup.Errors));
            var before = await _host.CaptureWarmAutomaticStateForTestsAsync();
            Assert.NotEmpty(before.Errors);

            using var store = new OutputStore(new OutputStoreOptions(
                outputRoot,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(1),
                MaximumArtifactBytes: 1024 * 1024,
                MaximumSessionBytes: 2 * 1024 * 1024,
                MaximumAggregateBytes: 4 * 1024 * 1024));
            using var capture = new ForegroundOutputCapture(store);
            var shaped = await _host.InvokeWithOutputCaptureAsync(
                "$global:LASTEXITCODE = 37; " +
                "1..41 | ForEach-Object { [pscustomobject]@{ Row = $_; Value = \"value-$_\" } }",
                capture,
                route: "pwsh");
            Assert.True(shaped.Success, string.Join(Environment.NewLine, shaped.Errors));
            Assert.StartsWith("objects: 41", shaped.Output.Trim(), StringComparison.Ordinal);
            Assert.NotNull(shaped.OutputRecovery?.Handle);

            var after = await _host.CaptureWarmAutomaticStateForTestsAsync();
            Assert.Equal(37, Assert.IsType<int>(after.LastExitCode));
            Assert.Equal(before.Errors.Length, after.Errors.Length);
            for (var index = 0; index < before.Errors.Length; index++)
                Assert.Same(before.Errors[index], after.Errors[index]);

            var state = await _host.InvokeAsync(
                "(Get-Location).ProviderPath; $global:captureKeep; " +
                "$global:captureConnection.State; Get-PtkCaptureState; " +
                "[bool](Get-Module PtkCaptureState)",
                raw: true,
                route: "pwsh");
            var values = state.Output.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(
                [
                    workingDirectory.FullName,
                    "variable-still-warm",
                    "connected",
                    "module-still-warm",
                    "True",
                ],
                values);
        }
        finally
        {
            workingDirectory.Delete(recursive: true);
            try { Directory.Delete(outputRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task Canceled_capture_preparation_does_not_mutate_not_started_warm_state()
    {
        var outputRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "capture-prepare-cancel-tests",
            Guid.NewGuid().ToString("N"));
        using var reserveEntered = new ManualResetEventSlim();
        using var releaseReserve = new ManualResetEventSlim();
        using var reserveReturned = new ManualResetEventSlim();
        try
        {
            var setup = await _host.InvokeAsync(
                "$global:LASTEXITCODE = 73; Remove-Variable -Name captureMustNotRun -Scope Global -ErrorAction SilentlyContinue",
                raw: true,
                route: "pwsh");
            Assert.True(setup.Success, string.Join(Environment.NewLine, setup.Errors));

            using var store = new OutputStore(new OutputStoreOptions(
                outputRoot,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(1),
                MaximumArtifactBytes: 1024,
                MaximumSessionBytes: 2048,
                MaximumAggregateBytes: 4096,
                ReservationStartingForTests: () =>
                {
                    reserveEntered.Set();
                    try { releaseReserve.Wait(); }
                    finally { reserveReturned.Set(); }
                }));
            using var capture = new ForegroundOutputCapture(store);
            using var cancellation = new CancellationTokenSource();
            var invocation = _host.InvokeWithOutputCaptureAsync(
                "$global:captureMustNotRun = $true",
                capture,
                raw: true,
                cancellationToken: cancellation.Token,
                route: "pwsh");
            Assert.True(reserveEntered.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();

            var result = await invocation;

            Assert.False(result.Success);
            Assert.False(result.UserExecutionStarted);
            Assert.Equal(InvokeDisposition.NotStarted, result.Disposition);
            var automatic = await _host.CaptureWarmAutomaticStateForTestsAsync();
            Assert.Equal(73, Assert.IsType<int>(automatic.LastExitCode));
            var effect = await _host.InvokeAsync(
                "[bool](Get-Variable -Name captureMustNotRun -Scope Global -ErrorAction SilentlyContinue)",
                raw: true,
                route: "pwsh");
            Assert.True(effect.Success, string.Join(Environment.NewLine, effect.Errors));
            Assert.Equal("False", effect.Output.Trim());
        }
        finally
        {
            releaseReserve.Set();
            Assert.True(reserveReturned.Wait(TimeSpan.FromSeconds(5)));
            try { Directory.Delete(outputRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task Active_output_members_are_not_executed_during_capture_or_shaping()
    {
        const string activeMemberMarker =
            "[active member not evaluated]";
        var workingDirectory = Directory.CreateTempSubdirectory("ptk-passive-state-");
        var mutationDirectory = Directory.CreateTempSubdirectory("ptk-passive-mutation-");
        var outputRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "passive-capture-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var escapedWorkingPath = workingDirectory.FullName.Replace("'", "''");
            var escapedMutationPath = mutationDirectory.FullName.Replace("'", "''");
            var setup = await _host.InvokeAsync(
                $"Set-Location -LiteralPath '{escapedWorkingPath}'; " +
                "$global:getterReads = 0; " +
                "$global:captureKeep = 'variable-still-warm'; " +
                "$global:captureConnection = [pscustomobject]@{ State = 'connected' }; " +
                "New-Module -Name PtkActiveMemberState -ScriptBlock { " +
                "$script:sentinel = 'module-still-warm'; " +
                "function Get-PtkActiveMemberState { $script:sentinel }; " +
                "function Set-PtkActiveMemberState([string]$Value) { $script:sentinel = $Value }; " +
                "Export-ModuleMember -Function Get-PtkActiveMemberState,Set-PtkActiveMemberState " +
                "} | Import-Module; " +
                "Write-Error 'seed-active-member-error' -ErrorAction Continue; " +
                "$global:LASTEXITCODE = 37",
                raw: true,
                route: "pwsh");
            Assert.True(setup.Success, string.Join(Environment.NewLine, setup.Errors));
            var before = await _host.CaptureWarmAutomaticStateForTestsAsync();
            Assert.NotEmpty(before.Errors);

            using var store = new OutputStore(new OutputStoreOptions(
                outputRoot,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(1),
                MaximumArtifactBytes: 1024 * 1024,
                MaximumSessionBytes: 2 * 1024 * 1024,
                MaximumAggregateBytes: 4 * 1024 * 1024));
            using var capture = new ForegroundOutputCapture(store);
            var result = await _host.InvokeWithOutputCaptureAsync(
                "$global:LASTEXITCODE = 37; " +
                "$object = [pscustomobject]@{ Stable = 'PASSIVE_VALUE' }; " +
                "$object | Add-Member -MemberType ScriptProperty -Name Dynamic -Value { " +
                "$global:getterReads++; " +
                $"Set-Location -LiteralPath '{escapedMutationPath}'; " +
                "$global:captureKeep = 'getter-mutated'; " +
                "$global:captureConnection.State = 'getter-mutated'; " +
                "Set-PtkActiveMemberState 'getter-mutated'; " +
                "Write-Error 'getter-mutated-error' -ErrorAction Continue; " +
                "$global:LASTEXITCODE = 99; " +
                "'DYNAMIC_CONSTANT' }; " +
                "$object",
                capture,
                route: "pwsh");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.False(result.WarmStateLost);
            Assert.Contains(
                activeMemberMarker,
                result.Output,
                StringComparison.Ordinal);
            Assert.Contains("PASSIVE_VALUE", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("DYNAMIC_CONSTANT", result.Output, StringComparison.Ordinal);
            Assert.Equal(OutputArtifactState.Incomplete, result.OutputRecovery?.State);
            Assert.Equal("active_member_not_evaluated", result.OutputRecovery?.DetailCode);
            var handle = Assert.IsType<string>(result.OutputRecovery?.Handle);
            var recovered = OutputTool.Output(
                store,
                handle,
                maxBytes: OutputStore.MaximumReadBytes);
            Assert.Contains(
                activeMemberMarker,
                recovered,
                StringComparison.Ordinal);
            Assert.Contains("PASSIVE_VALUE", recovered, StringComparison.Ordinal);
            Assert.DoesNotContain("DYNAMIC_CONSTANT", recovered, StringComparison.Ordinal);

            var after = await _host.CaptureWarmAutomaticStateForTestsAsync();
            Assert.Equal(37, Assert.IsType<int>(after.LastExitCode));
            Assert.Equal(before.Errors.Length, after.Errors.Length);
            for (var index = 0; index < before.Errors.Length; index++)
                Assert.Same(before.Errors[index], after.Errors[index]);

            var state = await _host.InvokeAsync(
                "(Get-Location).ProviderPath; $global:getterReads; " +
                "$global:captureKeep; $global:captureConnection.State; " +
                "Get-PtkActiveMemberState",
                raw: true,
                route: "pwsh");
            var values = state.Output.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(
                [
                    workingDirectory.FullName,
                    "0",
                    "variable-still-warm",
                    "connected",
                    "module-still-warm",
                ],
                values);
        }
        finally
        {
            workingDirectory.Delete(recursive: true);
            mutationDirectory.Delete(recursive: true);
            try { Directory.Delete(outputRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task Late_type_data_cannot_turn_a_captured_type_name_into_executable_output()
    {
        const string passiveValue = "PASSIVE_VALUE";
        const string dynamicValue = "DYNAMIC_CONSTANT";
        var typeName = "Ptk.LateTypeData." + Guid.NewGuid().ToString("N");
        var outputRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "passive-late-type-data-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var setup = await _host.InvokeAsync(
                "$global:lateTypeDataGetterReads = 0; " +
                "$global:lateTypeDataState = 'unchanged'",
                raw: true,
                route: "pwsh");
            Assert.True(setup.Success, string.Join(Environment.NewLine, setup.Errors));

            using var store = new OutputStore(new OutputStoreOptions(
                outputRoot,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(1),
                MaximumArtifactBytes: 1024 * 1024,
                MaximumSessionBytes: 2 * 1024 * 1024,
                MaximumAggregateBytes: 4 * 1024 * 1024));
            using var capture = new ForegroundOutputCapture(store);
            var escapedTypeName = typeName.Replace("'", "''");
            var result = await _host.InvokeWithOutputCaptureAsync(
                $"$typeName = '{escapedTypeName}'; " +
                "$object = [pscustomobject]@{ PSTypeName = $typeName; Stable = 'PASSIVE_VALUE' }; " +
                "Write-Output $object; " +
                "Update-TypeData -TypeName $typeName -MemberType ScriptProperty " +
                "-MemberName Dynamic -Value { " +
                "$global:lateTypeDataGetterReads++; " +
                "$global:lateTypeDataState = 'getter-ran'; " +
                "'DYNAMIC_CONSTANT' } -Force; " +
                "Update-TypeData -TypeName $typeName -MemberType AliasProperty " +
                "-MemberName Name -Value Dynamic -Force",
                capture,
                route: "pwsh");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Contains(passiveValue, result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(dynamicValue, result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(typeName, result.Output, StringComparison.Ordinal);
            Assert.Equal(OutputArtifactState.Incomplete, result.OutputRecovery?.State);
            Assert.Equal("active_member_not_evaluated", result.OutputRecovery?.DetailCode);

            var handle = Assert.IsType<string>(result.OutputRecovery?.Handle);
            var recovered = OutputTool.Output(
                store,
                handle,
                maxBytes: OutputStore.MaximumReadBytes);
            Assert.Contains(passiveValue, recovered, StringComparison.Ordinal);
            Assert.DoesNotContain(dynamicValue, recovered, StringComparison.Ordinal);
            Assert.DoesNotContain(typeName, recovered, StringComparison.Ordinal);

            var state = await _host.InvokeAsync(
                "$global:lateTypeDataGetterReads; $global:lateTypeDataState; " +
                $"Remove-TypeData -TypeName '{escapedTypeName}' -ErrorAction SilentlyContinue",
                raw: true,
                route: "pwsh");
            var values = state.Output.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(["0", "unchanged"], values);
        }
        finally
        {
            try { Directory.Delete(outputRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task A_spoofed_service_controller_name_never_authorizes_live_getters()
    {
        const string activeMemberMarker = "[active member not evaluated]";
        const string source = """
            namespace System.ServiceProcess
            {
                public sealed class ServiceController
                {
                    public static int Reads;
                    public string Status { get { Reads++; return "Running"; } }
                    public string Name { get { Reads++; return "spoof"; } }
                    public string DisplayName { get { Reads++; return "spoof display"; } }
                }
            }
            """;
        var encodedSource = Convert.ToBase64String(
            System.Text.Encoding.Unicode.GetBytes(source));
        var outputRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "passive-service-spoof-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new OutputStore(new OutputStoreOptions(
                outputRoot,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(1),
                MaximumArtifactBytes: 1024 * 1024,
                MaximumSessionBytes: 2 * 1024 * 1024,
                MaximumAggregateBytes: 4 * 1024 * 1024));
            using var capture = new ForegroundOutputCapture(store);
            var result = await _host.InvokeWithOutputCaptureAsync(
                "$source = [Text.Encoding]::Unicode.GetString(" +
                $"[Convert]::FromBase64String('{encodedSource}')); " +
                "$global:fakeServiceType = @(Add-Type -TypeDefinition $source -PassThru)[0]; " +
                "[Activator]::CreateInstance($global:fakeServiceType)",
                capture,
                route: "pwsh");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Contains(activeMemberMarker, result.Output, StringComparison.Ordinal);
            Assert.Equal(OutputArtifactState.Incomplete, result.OutputRecovery?.State);
            Assert.Equal("active_member_not_evaluated", result.OutputRecovery?.DetailCode);

            var reads = await _host.InvokeAsync(
                "$global:fakeServiceType.GetField('Reads').GetValue($null)",
                raw: true,
                route: "pwsh");
            Assert.Equal("0", reads.Output.Trim());
        }
        finally
        {
            try { Directory.Delete(outputRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task Passive_capture_never_enumerates_a_user_property_adapter()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var targetName = "PtkAdapterTarget" + suffix;
        var adapterName = "PtkProbeAdapter" + suffix;
        var source = $$"""
            using System;
            using System.Collections.ObjectModel;
            using System.Management.Automation;

            public sealed class {{targetName}} { }

            public sealed class {{adapterName}} : PSPropertyAdapter
            {
                public static int GetPropertiesCalls;
                public static int GetPropertyCalls;

                public override Collection<PSAdaptedProperty> GetProperties(object value)
                {
                    GetPropertiesCalls++;
                    return new Collection<PSAdaptedProperty>();
                }

                public override PSAdaptedProperty GetProperty(object value, string name)
                {
                    GetPropertyCalls++;
                    return null;
                }

                public override bool IsSettable(PSAdaptedProperty property) => false;
                public override bool IsGettable(PSAdaptedProperty property) => false;
                public override object GetPropertyValue(PSAdaptedProperty property) => null;
                public override void SetPropertyValue(PSAdaptedProperty property, object value) { }
                public override string GetPropertyTypeName(PSAdaptedProperty property) => "System.Object";
            }
            """;
        var encodedSource = Convert.ToBase64String(
            System.Text.Encoding.Unicode.GetBytes(source));
        var outputRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "passive-adapter-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new OutputStore(new OutputStoreOptions(
                outputRoot,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(1),
                MaximumArtifactBytes: 1024 * 1024,
                MaximumSessionBytes: 2 * 1024 * 1024,
                MaximumAggregateBytes: 4 * 1024 * 1024));
            using var capture = new ForegroundOutputCapture(store);
            var result = await _host.InvokeWithOutputCaptureAsync(
                "$source = [Text.Encoding]::Unicode.GetString(" +
                $"[Convert]::FromBase64String('{encodedSource}')); " +
                "$types = @(Add-Type -TypeDefinition $source -PassThru); " +
                $"$global:adapterTargetType = $types | Where-Object Name -EQ '{targetName}'; " +
                $"$global:probeAdapterType = $types | Where-Object Name -EQ '{adapterName}'; " +
                "$global:probeAdapterType.GetField('GetPropertiesCalls').SetValue($null, 0); " +
                "$global:probeAdapterType.GetField('GetPropertyCalls').SetValue($null, 0); " +
                "Update-TypeData -TypeName $global:adapterTargetType.FullName " +
                "-TypeAdapter $global:probeAdapterType -Force; " +
                "[Activator]::CreateInstance($global:adapterTargetType)",
                capture,
                route: "pwsh");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Contains(
                "[active member not evaluated]",
                result.Output,
                StringComparison.Ordinal);
            Assert.Equal("active_member_not_evaluated", result.OutputRecovery?.DetailCode);

            var reads = await _host.InvokeAsync(
                "$global:probeAdapterType.GetField('GetPropertiesCalls').GetValue($null); " +
                "$global:probeAdapterType.GetField('GetPropertyCalls').GetValue($null); " +
                "Remove-TypeData -TypeName $global:adapterTargetType.FullName -ErrorAction SilentlyContinue",
                raw: true,
                route: "pwsh");
            var values = reads.Output.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(["0", "0"], values);
        }
        finally
        {
            try { Directory.Delete(outputRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task Passive_capture_preserves_typed_scalars_for_shaping_and_recovery()
    {
        const string guidText = "12345678-1234-4abc-8def-0123456789ab";
        var outputRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "passive-typed-scalar-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new OutputStore(new OutputStoreOptions(
                outputRoot,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(1),
                MaximumArtifactBytes: 1024 * 1024,
                MaximumSessionBytes: 2 * 1024 * 1024,
                MaximumAggregateBytes: 4 * 1024 * 1024));
            using var capture = new ForegroundOutputCapture(store);

            var result = await _host.InvokeWithOutputCaptureAsync(
                "$true; " +
                "[datetime]'2026-07-13T12:34:56Z'; " +
                $"[guid]'{guidText}'",
                capture,
                route: "pwsh");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Contains("System.Boolean", result.Output, StringComparison.Ordinal);
            Assert.Contains("System.DateTime", result.Output, StringComparison.Ordinal);
            Assert.Contains("System.Guid", result.Output, StringComparison.Ordinal);
            Assert.Equal(OutputArtifactState.Available, result.OutputRecovery?.State);

            var handle = Assert.IsType<string>(result.OutputRecovery?.Handle);
            var recovered = OutputTool.Output(
                store,
                handle,
                maxBytes: OutputStore.MaximumReadBytes);
            Assert.Contains("True", recovered, StringComparison.Ordinal);
            Assert.Contains("2026", recovered, StringComparison.Ordinal);
            Assert.Contains(guidText, recovered, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(outputRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task Passive_scalar_freezing_never_uses_user_controlled_current_culture()
    {
        var typeName = "PtkProbeCulture" + Guid.NewGuid().ToString("N");
        var source = $$"""
            using System;
            using System.Globalization;
            using System.Threading;

            public sealed class {{typeName}} : CultureInfo
            {
                public static int Reads;

                public {{typeName}}() : base("en-US") { }

                public override DateTimeFormatInfo DateTimeFormat
                {
                    get
                    {
                        Interlocked.Increment(ref Reads);
                        return base.DateTimeFormat;
                    }
                    set { base.DateTimeFormat = value; }
                }

                public override NumberFormatInfo NumberFormat
                {
                    get
                    {
                        Interlocked.Increment(ref Reads);
                        return base.NumberFormat;
                    }
                    set { base.NumberFormat = value; }
                }
            }
            """;
        var encodedSource = Convert.ToBase64String(
            System.Text.Encoding.Unicode.GetBytes(source));
        var outputRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "passive-culture-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new OutputStore(new OutputStoreOptions(
                outputRoot,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(1),
                MaximumArtifactBytes: 1024 * 1024,
                MaximumSessionBytes: 2 * 1024 * 1024,
                MaximumAggregateBytes: 4 * 1024 * 1024));
            using var capture = new ForegroundOutputCapture(store);
            var result = await _host.InvokeWithOutputCaptureAsync(
                "$source = [Text.Encoding]::Unicode.GetString(" +
                $"[Convert]::FromBase64String('{encodedSource}')); " +
                "$probe = @(Add-Type -TypeDefinition $source -PassThru)[0]; " +
                "$oldCulture = [Threading.Thread]::CurrentThread.CurrentCulture; " +
                "try { " +
                "[Threading.Thread]::CurrentThread.CurrentCulture = [Activator]::CreateInstance($probe); " +
                "$probe.GetField('Reads').SetValue($null, 0); " +
                "[datetime]::new(2026, 7, 13, 12, 34, 56, [DateTimeKind]::Utc); " +
                "[decimal]::new(12345); " +
                "$probe.GetField('Reads').GetValue($null) " +
                "} finally { [Threading.Thread]::CurrentThread.CurrentCulture = $oldCulture }",
                capture,
                raw: true,
                route: "pwsh");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            var lines = result.Output.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal("0", lines[^1]);
            Assert.Equal(OutputArtifactState.Available, result.OutputRecovery?.State);
        }
        finally
        {
            try { Directory.Delete(outputRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task Passive_capture_bounds_an_empty_string_flood_by_entry_cost()
    {
        var outputRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "passive-empty-flood-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new OutputStore(new OutputStoreOptions(
                outputRoot,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(1),
                MaximumArtifactBytes: 4096,
                MaximumSessionBytes: 8192,
                MaximumAggregateBytes: 16384));
            using var capture = new ForegroundOutputCapture(store);

            var result = await _host.InvokeWithOutputCaptureAsync(
                "1..10000 | ForEach-Object { '' }",
                capture,
                raw: true,
                route: "pwsh");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(OutputArtifactState.Incomplete, result.OutputRecovery?.State);
            Assert.Equal("capture_bound_exceeded", result.OutputRecovery?.DetailCode);
            Assert.Contains(
                "[ptk:capture incomplete reason=capture_bound_exceeded",
                result.Output,
                StringComparison.Ordinal);
            Assert.Contains("total=10000]", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("retained=10000", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(outputRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task Nonterminating_error_surfaces_without_failing_the_call()
    {
        var result = await _host.InvokeAsync("Write-Error 'boom'; 'still ran'");

        Assert.True(result.Success);
        Assert.Contains("still ran", result.Output);
        Assert.Contains(result.Errors, e => e.Contains("boom"));
    }

    [Fact]
    public async Task Terminating_error_fails_the_call_but_host_survives()
    {
        var result = await _host.InvokeAsync("throw 'bang'");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("bang"));
        Assert.Equal(InvokeDisposition.Failed, result.Disposition);
        Assert.True(result.UserExecutionStarted);

        var next = await _host.InvokeAsync("'alive'");
        Assert.True(next.Success);
        Assert.Equal("alive", next.Output.Trim());
    }

    [Fact]
    public async Task Warning_stream_is_captured()
    {
        var result = await _host.InvokeAsync("Write-Warning 'careful'");

        Assert.True(result.Success);
        Assert.Contains(result.Warnings, w => w.Contains("careful"));
    }

    [Fact]
    public async Task Concurrent_calls_are_serialized_not_corrupted()
    {
        await _host.InvokeAsync("$counter = 0");

        var calls = Enumerable.Range(0, 8)
            .Select(_ => _host.InvokeAsync("$counter = $counter + 1"))
            .ToArray();
        await Task.WhenAll(calls);

        var result = await _host.InvokeAsync("$counter");
        Assert.Equal("8", result.Output.Trim());
    }

    [Fact]
    public async Task Audited_shutdown_does_not_wait_forever_for_a_discarded_private_processor()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60));
        var abandoned = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        host.AbandonOwnedOutputPipelineForTests(abandoned.Task);

        try
        {
            await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            abandoned.TrySetResult();
        }
    }

    [Fact]
    public async Task Private_output_runspace_open_is_deadline_bounded_and_not_awaited_by_shutdown()
    {
        var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(1));
        using var opening = new ManualResetEventSlim();
        using var releaseOpening = new ManualResetEventSlim();
        var runspaceOpened = 0;
        var invocationStarted = 0;
        host.PrivateOutputRunspaceOpeningForTests = () =>
        {
            opening.Set();
            releaseOpening.Wait();
        };
        host.PrivateOutputRunspaceOpenedForTests = () =>
            Interlocked.Increment(ref runspaceOpened);
        host.PrivateOutputInvocationStartedForTests = () =>
            Interlocked.Increment(ref invocationStarted);

        try
        {
            var invocation = host.InvokeAsync(
                "'PRIVATE_OPEN_PREFIX'",
                raw: true,
                route: "pwsh");
            Assert.True(opening.Wait(TimeSpan.FromSeconds(5)));

            var result = await invocation.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(result.Success);
            Assert.True(result.TimedOut);
            Assert.True(result.UserExecutionStarted);
            Assert.Contains(
                result.Errors,
                error => error.Contains(
                    "budget expired before recovery rendering could start",
                    StringComparison.OrdinalIgnoreCase));
            Assert.True(host.OutputProcessorUnavailableForTests);
            await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, Volatile.Read(ref invocationStarted));
        }
        finally
        {
            releaseOpening.Set();
            Assert.True(SpinWait.SpinUntil(
                () => !host.OutputProcessorUnavailableForTests,
                TimeSpan.FromSeconds(5)));
            Assert.Equal(0, Volatile.Read(ref runspaceOpened));
            host.Dispose();
        }
    }

    [Fact]
    public async Task Private_output_runspace_open_uses_the_processwide_creation_lock()
    {
        using var firstHost = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(10));
        using var secondHost = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(10));
        using var firstOpening = new ManualResetEventSlim();
        using var releaseFirstOpening = new ManualResetEventSlim();
        using var secondOpening = new ManualResetEventSlim();
        firstHost.PrivateOutputRunspaceOpeningForTests = () =>
        {
            firstOpening.Set();
            releaseFirstOpening.Wait();
        };
        secondHost.PrivateOutputRunspaceOpeningForTests = () => secondOpening.Set();

        try
        {
            var first = firstHost.InvokeAsync(
                "'FIRST_SERIALIZED_OPEN'",
                raw: true,
                route: "pwsh");
            Assert.True(firstOpening.Wait(TimeSpan.FromSeconds(5)));

            var second = secondHost.InvokeAsync(
                "'SECOND_SERIALIZED_OPEN'",
                raw: true,
                route: "pwsh");
            Assert.False(
                secondOpening.Wait(TimeSpan.FromMilliseconds(250)),
                "A second private Runspace.Open entered while the first held CreationLock.");

            releaseFirstOpening.Set();
            Assert.True(secondOpening.Wait(TimeSpan.FromSeconds(5)));
            var results = await Task.WhenAll(first, second)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(
                results,
                result => Assert.True(
                    result.Success,
                    string.Join(Environment.NewLine, result.Errors)));
        }
        finally
        {
            releaseFirstOpening.Set();
        }
    }

    [Fact]
    public async Task Canceled_private_output_start_never_invokes_after_the_block_releases()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(10));
        using var starting = new ManualResetEventSlim();
        using var releaseStarting = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var invocationStarted = 0;
        host.PrivateOutputInvocationStartingForTests = () =>
        {
            starting.Set();
            releaseStarting.Wait();
        };
        host.PrivateOutputInvocationStartedForTests = () =>
            Interlocked.Increment(ref invocationStarted);

        try
        {
            var invocation = host.InvokeAsync(
                "'PRIVATE_START_PREFIX'",
                raw: true,
                cancellationToken: cancellation.Token,
                route: "pwsh");
            Assert.True(starting.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();

            var result = await invocation.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(result.Success);
            Assert.False(result.TimedOut);
            Assert.Equal(InvokeDisposition.Canceled, result.Disposition);
            Assert.True(result.UserExecutionStarted);
            Assert.True(host.OutputProcessorUnavailableForTests);
        }
        finally
        {
            releaseStarting.Set();
            Assert.True(SpinWait.SpinUntil(
                () => !host.OutputProcessorUnavailableForTests,
                TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(0, Volatile.Read(ref invocationStarted));
    }

    [Fact]
    public async Task Private_output_stop_is_joined_before_disposal_and_guard_release()
    {
        var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(1));
        var pendingInvocation = new TaskCompletionSource<
            System.Management.Automation.PSDataCollection<
                System.Management.Automation.PSObject>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        using var invocationStarted = new ManualResetEventSlim();
        using var stopStarting = new ManualResetEventSlim();
        using var releaseStop = new ManualResetEventSlim();
        try
        {
            var warm = await host.InvokeAsync("'warm'", raw: true, route: "pwsh");
            Assert.True(warm.Success, string.Join(Environment.NewLine, warm.Errors));
            host.PrivateOutputInvocationOverrideForTests = (_, _) => pendingInvocation.Task;
            host.PrivateOutputInvocationStartedForTests = () => invocationStarted.Set();
            host.PrivateOutputStopStartingForTests = () =>
            {
                stopStarting.Set();
                releaseStop.Wait();
            };

            var invocation = host.InvokeAsync(
                "'STOP_JOIN_PREFIX'",
                raw: true,
                route: "pwsh");
            Assert.True(invocationStarted.Wait(TimeSpan.FromSeconds(5)));

            var result = await invocation.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.False(result.Success);
            Assert.True(result.TimedOut);
            Assert.True(stopStarting.Wait(TimeSpan.FromSeconds(2)));
            var emptyResults = new System.Management.Automation.PSDataCollection<
                System.Management.Automation.PSObject>();
            emptyResults.Complete();
            pendingInvocation.TrySetResult(emptyResults);
            Assert.True(host.OutputProcessorUnavailableForTests);
            await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(host.OutputProcessorUnavailableForTests);
        }
        finally
        {
            releaseStop.Set();
            if (!pendingInvocation.Task.IsCompleted)
            {
                var emptyResults = new System.Management.Automation.PSDataCollection<
                    System.Management.Automation.PSObject>();
                emptyResults.Complete();
                pendingInvocation.TrySetResult(emptyResults);
            }
            Assert.True(SpinWait.SpinUntil(
                () => !host.OutputProcessorUnavailableForTests,
                TimeSpan.FromSeconds(5)));
            host.Dispose();
        }
    }

    [Fact]
    public async Task Private_output_disposal_is_bounded_and_holds_the_singleflight_guard()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(10));
        using var disposing = new ManualResetEventSlim();
        using var releaseDisposal = new ManualResetEventSlim();
        var openingCount = 0;
        var disposalCount = 0;
        host.PrivateOutputRunspaceOpeningForTests = () =>
            Interlocked.Increment(ref openingCount);
        host.PrivateOutputProcessorDisposingForTests = () =>
        {
            if (Interlocked.Increment(ref disposalCount) != 1) return;
            disposing.Set();
            releaseDisposal.Wait();
        };

        try
        {
            var first = await host.InvokeAsync(
                    "'FIRST_PRIVATE_RESULT'",
                    raw: true,
                    route: "pwsh")
                .WaitAsync(TimeSpan.FromSeconds(3));

            Assert.True(first.Success, string.Join(Environment.NewLine, first.Errors));
            Assert.Contains("FIRST_PRIVATE_RESULT", first.Output, StringComparison.Ordinal);
            Assert.True(disposing.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(host.OutputProcessorUnavailableForTests);
            Assert.Equal(1, Volatile.Read(ref openingCount));

            var whileDisposing = await host.InvokeAsync(
                    "'PASSIVE_FALLBACK_RESULT'",
                    raw: true,
                    route: "pwsh")
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(
                whileDisposing.Success,
                string.Join(Environment.NewLine, whileDisposing.Errors));
            Assert.Contains(
                "PASSIVE_FALLBACK_RESULT",
                whileDisposing.Output,
                StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref openingCount));

            releaseDisposal.Set();
            Assert.True(SpinWait.SpinUntil(
                () => !host.OutputProcessorUnavailableForTests,
                TimeSpan.FromSeconds(5)));

            var afterCleanup = await host.InvokeAsync(
                "'PRIVATE_PROCESSOR_RECOVERED'",
                raw: true,
                route: "pwsh");
            Assert.True(
                afterCleanup.Success,
                string.Join(Environment.NewLine, afterCleanup.Errors));
            Assert.Contains(
                "PRIVATE_PROCESSOR_RECOVERED",
                afterCleanup.Output,
                StringComparison.Ordinal);
            Assert.Equal(2, Volatile.Read(ref openingCount));
        }
        finally
        {
            releaseDisposal.Set();
        }
    }

    [Fact]
    public async Task Healthy_private_render_cleanup_can_complete_before_same_call_shaping()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(10));
        var openingCount = 0;
        var disposalCount = 0;
        host.PrivateOutputRunspaceOpeningForTests = () =>
            Interlocked.Increment(ref openingCount);
        host.PrivateOutputProcessorDisposingForTests = () =>
        {
            if (Interlocked.Increment(ref disposalCount) == 1)
                Thread.Sleep(TimeSpan.FromMilliseconds(250));
        };

        var result = await host.InvokeAsync(
            "1..41 | ForEach-Object { [pscustomobject]@{ Row = $_; Value = \"value-$_\" } }",
            route: "pwsh");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.StartsWith("objects: 41", result.Output.Trim(), StringComparison.Ordinal);
        Assert.Equal(2, Volatile.Read(ref openingCount));
    }

    [Fact]
    public async Task Cancellation_during_private_render_cleanup_returns_without_waiting_for_disposal()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(10));
        using var disposing = new ManualResetEventSlim();
        using var releaseDisposal = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var disposalCount = 0;
        host.PrivateOutputProcessorDisposingForTests = () =>
        {
            if (Interlocked.Increment(ref disposalCount) != 1) return;
            disposing.Set();
            releaseDisposal.Wait();
        };

        try
        {
            var invocation = host.InvokeAsync(
                "1..41 | ForEach-Object { [pscustomobject]@{ Row = $_ } }",
                cancellationToken: cancellation.Token,
                route: "pwsh");
            Assert.True(disposing.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();

            var result = await invocation.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(result.Success);
            Assert.False(result.TimedOut);
            Assert.Equal(InvokeDisposition.Canceled, result.Disposition);
            Assert.True(host.OutputProcessorUnavailableForTests);
        }
        finally
        {
            releaseDisposal.Set();
            Assert.True(SpinWait.SpinUntil(
                () => !host.OutputProcessorUnavailableForTests,
                TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_timeout_and_preserves_warm_state()
    {
        await _host.InvokeAsync("$keep = 'still-warm'");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var canceled = await _host.InvokeAsync("Start-Sleep -Seconds 60", cancellationToken: cts.Token);

        Assert.False(canceled.Success);
        Assert.False(canceled.TimedOut);
        Assert.Contains(canceled.Errors, e => e.Contains("cancel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(canceled.Errors, e => e.Contains("timeout", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(InvokeDisposition.Canceled, canceled.Disposition);
        Assert.True(canceled.UserExecutionStarted);

        // The runspace survived the cancel: pre-cancel state is still readable.
        var after = await _host.InvokeAsync("$keep");
        Assert.True(after.Success);
        Assert.Equal("still-warm", after.Output.Trim());
    }

    [Fact]
    public async Task Cancel_during_slow_preflight_preserves_warm_state()
    {
        // The ubuntu CI runner caught this live: a cancel landing while the
        // dialect/routing preflight is still running (slow loaded machine)
        // recycled the warm session. Preflight is not user code and finishes
        // on its own - the cancel must wait it out, not destroy state. The
        // instance-local hook makes the race deterministic without allowing
        // user session state to replace the trusted detector.
        await _host.InvokeAsync("$keep = 'still-warm'");
        _host.PreflightDelayForTests = TimeSpan.FromSeconds(2);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var canceled = await _host.InvokeAsync("'never-runs'", cancellationToken: cts.Token);

        Assert.False(canceled.Success);
        Assert.False(canceled.TimedOut);
        Assert.False(canceled.WarmStateLost);
        Assert.Contains(canceled.Errors, e => e.Contains("cancel", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(InvokeDisposition.NotStarted, canceled.Disposition);
        Assert.False(canceled.UserExecutionStarted);

        _host.PreflightDelayForTests = TimeSpan.Zero;
        var after = await _host.InvokeAsync("$keep", route: "pwsh");
        Assert.True(after.Success);
        Assert.Equal("still-warm", after.Output.Trim());
    }

    [Fact]
    public async Task Timeout_recycles_the_runspace_and_host_survives()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(2));

        await host.InvokeAsync("$x = 'before-timeout'");
        var timedOut = await host.InvokeAsync("Start-Sleep -Seconds 60");

        Assert.False(timedOut.Success);
        Assert.True(timedOut.TimedOut);
        Assert.Equal(InvokeDisposition.OutcomeUnknown, timedOut.Disposition);
        Assert.True(timedOut.UserExecutionStarted);

        // Recycled runspace: host answers again, but pre-timeout state is gone.
        var after = await host.InvokeAsync("if ($null -eq $x) { 'state-cleared' } else { $x }");
        Assert.True(after.Success);
        Assert.Equal("state-cleared", after.Output.Trim());
    }

    [Fact]
    public async Task Dialect_refusal_is_structured_as_not_started()
    {
        var authorizationCalls = 0;
        var refused = await _host.InvokeAsync(
            "export X=1",
            new TestInvocationAuthorizer((preparation, cancellationToken) =>
            {
                authorizationCalls++;
                return ValueTask.FromResult(true);
            }));

        Assert.False(refused.Success);
        Assert.Contains("[ptk:dialect]", refused.Output);
        Assert.Equal(InvokeDisposition.NotStarted, refused.Disposition);
        Assert.False(refused.UserExecutionStarted);
        Assert.Equal(0, authorizationCalls);
    }

    [Fact]
    public void Nonpublic_audited_invocation_requires_the_two_barrier_authorizer()
    {
        var auditedOverloads = typeof(RunspaceHost)
            .GetMethods(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
            .Where(method => method.Name == nameof(RunspaceHost.InvokeAsync))
            .Where(method =>
            {
                var parameters = method.GetParameters();
                return parameters.Length >= 2 && !parameters[1].IsOptional;
            })
            .ToArray();

        Assert.NotEmpty(auditedOverloads);
        Assert.All(
            auditedOverloads,
            method => Assert.Equal(
                typeof(IInvocationAuthorizer),
                method.GetParameters()[1].ParameterType));
    }

    [Fact]
    public async Task Unconfirmed_stop_is_structured_as_started_with_unknown_outcome()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60))
        {
            ForcePipelineStopFailureForTests = true,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await host.InvokeAsync("Start-Sleep -Seconds 60", cancellationToken: cts.Token);

        Assert.False(result.Success);
        Assert.False(result.TimedOut);
        Assert.True(result.WarmStateLost);
        Assert.Equal(InvokeDisposition.OutcomeUnknown, result.Disposition);
        Assert.True(result.UserExecutionStarted);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("cold-detection")]
    [InlineData("timeout-rebuild")]
    public async Task Post_start_module_file_mutation_cannot_execute_during_repriming(string reprimePath)
    {
        var sourceManifest = RunspaceHost.ResolveModulePath();
        Assert.NotNull(sourceManifest);
        var sourceModule = Path.ChangeExtension(sourceManifest, ".psm1");
        Assert.True(File.Exists(sourceModule));

        var moduleDirectory = Directory.CreateTempSubdirectory("ptk-frozen-module-");
        try
        {
            var manifest = Path.Combine(moduleDirectory.FullName, "PwshTokenCompressor.psd1");
            var module = Path.Combine(moduleDirectory.FullName, "PwshTokenCompressor.psm1");
            var sentinel = Path.Combine(moduleDirectory.FullName, "module-side-effect.txt");
            File.Copy(sourceManifest, manifest);
            File.Copy(sourceModule, module);

            using var host = new RunspaceHost(
                callTimeout: TimeSpan.FromMilliseconds(500),
                modulePathOverride: manifest);
            Assert.True(host.ModuleLoaded);

            // Model the exact attack: one authorized user pipeline replaces the
            // on-disk module with top-level code. No later module load may read
            // those mutable bytes before a subsequent dispatch authorization.
            var maliciousSource =
                $"[IO.File]::WriteAllText('{PowerShellLiteral(sentinel)}', 'executed')";
            var encodedSource = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(maliciousSource));
            var mutation = await host.InvokeAsync(
                $"[IO.File]::WriteAllBytes('{PowerShellLiteral(module)}', " +
                $"[Convert]::FromBase64String('{encodedSource}'))",
                new TestInvocationAuthorizer((_, _) => ValueTask.FromResult(true)),
                raw: true,
                route: "pwsh");
            Assert.True(mutation.Success, string.Join(Environment.NewLine, mutation.Errors));

            switch (reprimePath)
            {
                case "reset":
                    await host.ResetAsync();
                    break;
                case "cold-detection":
                    _ = await host.TryGetBackgroundDialectRefusalAsync("export PTK_TEST=1");
                    break;
                case "timeout-rebuild":
                    var timedOut = await host.InvokeAsync(
                        "Start-Sleep -Seconds 10",
                        new TestInvocationAuthorizer((_, _) => ValueTask.FromResult(true)),
                        route: "pwsh");
                    Assert.True(timedOut.TimedOut);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(reprimePath));
            }

            var refused = await host.InvokeAsync(
                "'must-not-run'",
                new TestInvocationAuthorizer((_, _) => ValueTask.FromResult(false)),
                route: "pwsh");
            Assert.Equal(InvokeDisposition.NotStarted, refused.Disposition);
            Assert.False(refused.UserExecutionStarted);
            Assert.False(
                File.Exists(sentinel),
                $"mutable module source executed through {reprimePath}");
        }
        finally
        {
            moduleDirectory.Delete(recursive: true);
        }
    }

    private static string PowerShellLiteral(string value) => value.Replace("'", "''");
}
