using System.Collections.Immutable;
using System.Management.Automation;

namespace PtkMcpServer.Tests;

public sealed class ExecutionPlannerTests
{
    private static readonly string RtkPath =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trusted", "rtk"));

    [Fact]
    public void Plans_one_application_through_pinned_rtk_and_preserves_constant_text()
    {
        var commands = Application("git", "/usr/bin/git");

        var plan = Plan("git commit -m \"hello world\"", "auto", RtkPath, commands);

        Assert.Equal("git commit -m \"hello world\"", plan.OriginalScript);
        Assert.Equal(
            $"& '{RtkPath.Replace("'", "''")}' git commit -m \"hello world\"",
            plan.ExecutionScript);
        Assert.Equal(ExecutionDomain.NativeTerminal, plan.Domain);
        Assert.Equal(ExecutionPath.Rtk, plan.ExecutionPath);
        Assert.Equal("rtk", plan.EffectiveRoute);
        Assert.Equal(PreExecutionValidation.None, plan.PreExecutionValidation);
        Assert.Equal(ResolutionContext.Warm, plan.ResolutionContext);
        Assert.Equal(RequestedExecutionRoute.Auto, plan.RequestedRoute);
        Assert.Equal(OutputProvenance.RtkUnknown, plan.OutputProvenance);
        Assert.Empty(plan.PermittedFallbacks);
        Assert.Null(plan.FallbackReason);
        Assert.Equal(RtkPath, plan.RtkExecutableIdentity?.ExecutablePath);
        Assert.Null(plan.RtkExecutableIdentity?.Verified);

        var apostrophePath =
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trusted", "o'brien", "rtk"));
        var apostrophe = Plan("git status", "auto", apostrophePath, commands);
        Assert.Equal(
            $"& '{apostrophePath.Replace("'", "''")}' git status",
            apostrophe.ExecutionScript);
        Assert.Equal(apostrophePath, apostrophe.RtkExecutableIdentity?.ExecutablePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("param(); git status")]
    [InlineData("begin { git status }")]
    [InlineData("process { git status }")]
    [InlineData("git status; git diff")]
    [InlineData("if ($true) { git status }")]
    [InlineData("git status | Out-Null")]
    [InlineData("1 + 2")]
    [InlineData("& git status")]
    [InlineData("git log -1 > out.txt")]
    [InlineData("$cmd status")]
    [InlineData("rtk gain")]
    [InlineData("/opt/RTK.EXE gain")]
    [InlineData("git commit -m \"$msg\"")]
    [InlineData("git -flag:$value")]
    [InlineData("git status ||| (")]
    public void Keeps_every_non_single_constant_command_shape_on_PowerShell(string script)
    {
        var plan = Plan(script, "auto", RtkPath, Application("git", "/usr/bin/git"));

        AssertDirect(plan, script, RequestedExecutionRoute.Auto);
    }

    [Theory]
    [InlineData(CommandTypes.Alias, null, null)]
    [InlineData(CommandTypes.Function, null, null)]
    [InlineData(CommandTypes.Cmdlet, null, null)]
    [InlineData(CommandTypes.ExternalScript, "/tmp/git.ps1", null)]
    [InlineData(CommandTypes.Application, "/tmp/git.cmd", null)]
    [InlineData(CommandTypes.Application, "/tmp/git.BAT", null)]
    public void Auto_route_keeps_non_native_or_batch_resolution_on_PowerShell(
        CommandTypes type,
        string? source,
        string? definition)
    {
        var commands = new TrustedCommandSnapshot();
        commands.Set("git", CommandTypes.All, new ResolvedCommand(type, source, definition));

        var plan = Plan("git status", "auto", RtkPath, commands);

        AssertDirect(plan, "git status", RequestedExecutionRoute.Auto);
        Assert.Equal(
            type == CommandTypes.Application
                ? ExecutionDomain.NativeTerminal
                : ExecutionDomain.PowerShell,
            plan.Domain);
    }

    [Fact]
    public void Honors_absent_rtk_pwsh_and_strict_forced_rtk_contracts()
    {
        var commands = new TrustedCommandSnapshot();
        commands.Set("Get-ChildItem", CommandTypes.All, new ResolvedCommand(CommandTypes.Cmdlet));

        var absent = Plan("git status", "auto", null, commands);
        AssertDirect(absent, "git status", RequestedExecutionRoute.Auto);
        Assert.Null(absent.Domain);

        var empty = Plan("git status", "auto", string.Empty, commands);
        AssertDirect(empty, "git status", RequestedExecutionRoute.Auto);
        Assert.Null(empty.Domain);

        var pwsh = Plan("git status", "PWSH", RtkPath, commands);
        AssertDirect(pwsh, "git status", RequestedExecutionRoute.PowerShell);
        Assert.Null(pwsh.Domain);

        var forced = Plan("Get-ChildItem", "RTK", RtkPath, commands);
        AssertDirect(forced, "Get-ChildItem", RequestedExecutionRoute.Rtk);
        Assert.Equal(ExecutionDomain.PowerShell, forced.Domain);
        Assert.Equal(
            ExecutionFallbackReason.RtkResolutionNotApplication,
            forced.FallbackReason);

        var batchCommands = Application("git", "/tmp/git.cmd");
        var forcedBatch = Plan("git status", "rtk", RtkPath, batchCommands);
        AssertDirect(forcedBatch, "git status", RequestedExecutionRoute.Rtk);
        Assert.Equal(ExecutionDomain.NativeTerminal, forcedBatch.Domain);
        Assert.Equal(
            ExecutionFallbackReason.RtkFidelityExclusion,
            forcedBatch.FallbackReason);

        var forcedFallback = Plan("git status | Out-Null", "rtk", RtkPath, commands);
        AssertDirect(
            forcedFallback,
            "git status | Out-Null",
            RequestedExecutionRoute.Rtk);
        Assert.Equal(ExecutionDomain.MixedDataflow, forcedFallback.Domain);
        Assert.Equal(ExecutionFallbackReason.RtkIneligibleShape, forcedFallback.FallbackReason);
    }

    [Fact]
    public void Raw_is_an_explicit_direct_plan_without_fallbacks()
    {
        var plan = ExecutionPlanner.Create(
            "git status",
            "rtk",
            RtkPath,
            Application("git", "/usr/bin/git"),
            raw: true,
            compressAvailable: true,
            ResolutionContext.Warm);

        AssertDirect(
            plan,
            "git status",
            RequestedExecutionRoute.Rtk,
            OutputProvenance.DirectText);
        Assert.Null(plan.Domain);
        Assert.Null(plan.FallbackReason);
    }

    [Fact]
    public void Compressor_unavailable_direct_plan_has_unknown_domain_and_direct_text()
    {
        var plan = ExecutionPlanner.CreateDirect(
            "'plain text'",
            "auto",
            raw: false,
            compressAvailable: false,
            ResolutionContext.Warm);

        Assert.Null(plan.Domain);
        Assert.Equal(ExecutionPath.PowerShellDirect, plan.ExecutionPath);
        Assert.Equal(OutputProvenance.DirectText, plan.OutputProvenance);
        Assert.Empty(plan.PermittedFallbacks);
    }

    [Fact]
    public void Machine_codes_cover_every_frozen_plan_value()
    {
        Assert.Equal(
            ["powershell", "native_terminal", "mixed_dataflow", "bash"],
            Enum.GetValues<ExecutionDomain>().Select(value => value.ToMachineCode()));
        Assert.Equal(
            ["powershell_direct", "rtk", "native_direct", "bash_via_rtk"],
            Enum.GetValues<ExecutionPath>().Select(value => value.ToMachineCode()));
        Assert.Equal(
            ["auto", "pwsh", "rtk"],
            Enum.GetValues<RequestedExecutionRoute>().Select(value => value.ToMachineCode()));
        Assert.Equal(
            ["powershell_objects", "direct_text", "rtk_unknown", "rtk_filtered", "rtk_passthrough"],
            Enum.GetValues<OutputProvenance>().Select(value => value.ToMachineCode()));
        Assert.Equal(
            [
                "rtk_executable_unavailable",
                "rtk_ineligible_shape",
                "rtk_self_invocation",
                "rtk_resolution_not_application",
                "rtk_fidelity_exclusion",
            ],
            Enum.GetValues<ExecutionFallbackReason>().Select(value => value.ToMachineCode()));
    }

    [Fact]
    public void Plan_constructor_rejects_false_rtk_identity_or_provenance()
    {
        var identity = new RtkExecutableIdentity(RtkPath);

        Assert.Throws<ArgumentException>(() => new ExecutionPlan(
            "git status",
            "git status",
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.DirectText,
            ImmutableArray<ExecutionPath>.Empty,
            fallbackReason: null,
            identity));
        Assert.Throws<ArgumentException>(() => new ExecutionPlan(
            "'direct'",
            "'direct'",
            ExecutionDomain.PowerShell,
            ExecutionPath.PowerShellDirect,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.PowerShellObjects,
            ImmutableArray<ExecutionPath>.Empty,
            fallbackReason: null,
            identity));
        Assert.Throws<ArgumentException>(() => new ExecutionPlan(
            "'direct'",
            "'direct'",
            ExecutionDomain.PowerShell,
            ExecutionPath.PowerShellDirect,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            ImmutableArray<ExecutionPath>.Empty,
            fallbackReason: null,
            rtkExecutableIdentity: null));
        Assert.Throws<ArgumentException>(() => new ExecutionPlan(
            "'direct'",
            "'direct'",
            ExecutionDomain.PowerShell,
            ExecutionPath.PowerShellDirect,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.PowerShellObjects,
            ImmutableArray<ExecutionPath>.Empty,
            ExecutionFallbackReason.RtkIneligibleShape,
            rtkExecutableIdentity: null));
    }

    [Theory]
    [InlineData("git status | Out-Null", "mixed_dataflow")]
    [InlineData("git log -1 > out.txt", "mixed_dataflow")]
    [InlineData("1 + 2", "powershell")]
    public void Classifies_domain_independently_from_the_direct_execution_path(
        string script,
        string expectedDomain)
    {
        var plan = Plan(script, "auto", RtkPath, Application("git", "/usr/bin/git"));

        Assert.Equal(ExecutionPath.PowerShellDirect, plan.ExecutionPath);
        Assert.Equal(expectedDomain, plan.Domain?.ToMachineCode());
    }

    private static ExecutionPlan Plan(
        string script,
        string route,
        string? rtkPath,
        TrustedCommandSnapshot commands) =>
        ExecutionPlanner.Create(
            script,
            route,
            rtkPath,
            commands,
            raw: false,
            compressAvailable: true,
            ResolutionContext.Warm);

    private static void AssertDirect(
        ExecutionPlan plan,
        string script,
        RequestedExecutionRoute requestedRoute,
        OutputProvenance expectedProvenance = OutputProvenance.PowerShellObjects)
    {
        Assert.Equal(script, plan.OriginalScript);
        Assert.Equal(script, plan.ExecutionScript);
        Assert.Equal(ExecutionPath.PowerShellDirect, plan.ExecutionPath);
        Assert.Equal("powershell_direct", plan.EffectiveRoute);
        Assert.Equal(PreExecutionValidation.None, plan.PreExecutionValidation);
        Assert.Equal(ResolutionContext.Warm, plan.ResolutionContext);
        Assert.Equal(requestedRoute, plan.RequestedRoute);
        Assert.Equal(expectedProvenance, plan.OutputProvenance);
        Assert.Empty(plan.PermittedFallbacks);
        Assert.Null(plan.RtkExecutableIdentity);
    }

    private static TrustedCommandSnapshot Application(string name, string source)
    {
        var commands = new TrustedCommandSnapshot();
        commands.Set(name, CommandTypes.All,
            new ResolvedCommand(CommandTypes.Application, source));
        return commands;
    }
}
