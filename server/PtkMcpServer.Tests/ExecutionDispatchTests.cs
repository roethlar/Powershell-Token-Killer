using System.Collections.Immutable;

namespace PtkMcpServer.Tests;

public sealed class ExecutionDispatchTests
{
    private static readonly string RtkPath =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trusted", "rtk"));

    [Fact]
    public void FromPlan_preserves_the_exact_authorized_execution()
    {
        var plan = RtkPlan(
            "git commit -m \"exact original\"",
            $"& '{RtkPath.Replace("'", "''")}' git commit -m \"exact original\"",
            [ExecutionPath.PowerShellDirect]);

        var dispatch = ExecutionDispatch.FromPlan(plan);

        Assert.Same(plan, dispatch.Plan);
        Assert.Equal(plan.ExecutionScript, dispatch.ExecutionScript);
        Assert.Equal(ExecutionDomain.NativeTerminal, dispatch.Domain);
        Assert.Equal(ExecutionPath.Rtk, dispatch.ExecutionPath);
        Assert.Equal("rtk", dispatch.EffectiveRoute);
        Assert.Equal(RequestedExecutionRoute.Auto, dispatch.RequestedRoute);
        Assert.Equal(OutputProvenance.RtkUnknown, dispatch.OutputProvenance);
        Assert.Collection(
            dispatch.PermittedFallbacks,
            path => Assert.Equal(ExecutionPath.PowerShellDirect, path));
        Assert.Null(dispatch.FallbackReason);
        Assert.Same(plan.RtkExecutableIdentity, dispatch.RtkExecutableIdentity);
    }

    [Fact]
    public void RtkUnavailableFallback_dispatches_the_exact_original_once_as_PowerShell()
    {
        const string original = "git commit -m \"$literal; still exact\"";
        var plan = RtkPlan(
            original,
            $"& '{RtkPath.Replace("'", "''")}' git commit -m \"$literal; still exact\"",
            [ExecutionPath.PowerShellDirect]);

        var dispatch = ExecutionDispatch.RtkUnavailableFallback(plan);

        Assert.Same(plan, dispatch.Plan);
        Assert.Equal(original, dispatch.ExecutionScript);
        Assert.Equal(ExecutionDomain.NativeTerminal, dispatch.Domain);
        Assert.Equal(ExecutionPath.PowerShellDirect, dispatch.ExecutionPath);
        Assert.Equal("powershell_direct", dispatch.EffectiveRoute);
        Assert.Equal(RequestedExecutionRoute.Auto, dispatch.RequestedRoute);
        Assert.Equal(OutputProvenance.PowerShellObjects, dispatch.OutputProvenance);
        Assert.Collection(
            dispatch.PermittedFallbacks,
            path => Assert.Equal(ExecutionPath.PowerShellDirect, path));
        Assert.Equal(
            ExecutionFallbackReason.RtkExecutableBecameUnavailable,
            dispatch.FallbackReason);
        Assert.Null(dispatch.RtkExecutableIdentity);
    }

    [Fact]
    public void RtkUnavailableFallback_rejects_a_plan_that_did_not_authorize_it()
    {
        var plan = RtkPlan(
            "git status",
            $"& '{RtkPath.Replace("'", "''")}' git status",
            []);

        Assert.Throws<InvalidOperationException>(
            () => ExecutionDispatch.RtkUnavailableFallback(plan));
    }

    [Fact]
    public void RtkUnavailableFallback_rejects_a_non_rtk_plan()
    {
        var plan = new ExecutionPlan(
            "'direct'",
            "'direct'",
            ExecutionDomain.PowerShell,
            ExecutionPath.PowerShellDirect,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.PowerShell,
            OutputProvenance.PowerShellObjects,
            [ExecutionPath.PowerShellDirect],
            fallbackReason: null,
            rtkExecutableIdentity: null);

        Assert.Throws<InvalidOperationException>(
            () => ExecutionDispatch.RtkUnavailableFallback(plan));
    }

    private static ExecutionPlan RtkPlan(
        string originalScript,
        string executionScript,
        ImmutableArray<ExecutionPath> permittedFallbacks) =>
        new(
            originalScript,
            executionScript,
            ExecutionDomain.NativeTerminal,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            ResolutionContext.Warm,
            RequestedExecutionRoute.Auto,
            OutputProvenance.RtkUnknown,
            permittedFallbacks,
            fallbackReason: null,
            new RtkExecutableIdentity(RtkPath));
}
