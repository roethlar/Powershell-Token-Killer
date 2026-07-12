using System.Collections.Immutable;

namespace PtkMcpServer;

internal enum ExecutionDomain
{
    PowerShell,
    NativeTerminal,
    MixedDataflow,
    Bash,
}

internal enum ExecutionPath
{
    PowerShellDirect,
    Rtk,
    NativeDirect,
    BashViaRtk,
}

internal enum PreExecutionValidation
{
    None,
    BashSyntax,
}

internal enum ResolutionContext
{
    Warm,
    Cold,
}

internal enum RequestedExecutionRoute
{
    Auto,
    PowerShell,
    Rtk,
}

internal enum ExecutionFallbackReason
{
    RtkExecutableUnavailable,
    RtkIneligibleShape,
    RtkSelfInvocation,
    RtkResolutionNotApplication,
    RtkFidelityExclusion,
}

internal enum OutputProvenance
{
    PowerShellObjects,
    DirectText,
    RtkUnknown,
    RtkFiltered,
    RtkPassthrough,
}

internal sealed record RtkVerifiedBinaryIdentity
{
    internal RtkVerifiedBinaryIdentity(string version, string binaryDigest)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(binaryDigest);
        if (string.IsNullOrWhiteSpace(version) || version.Length > 128)
            throw new ArgumentException("RTK version must be 1..128 characters.", nameof(version));
        if (binaryDigest.Length != 64 ||
            binaryDigest.Any(character => character is not (
                >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "RTK binary digest must be lowercase SHA-256 hex.",
                nameof(binaryDigest));
        }

        Version = version;
        BinaryDigest = binaryDigest;
    }

    internal string Version { get; }
    internal string BinaryDigest { get; }
}

internal sealed record RtkExecutableIdentity(
    string ExecutablePath,
    RtkVerifiedBinaryIdentity? Verified = null);

/// <summary>
/// Immutable foreground preparation handed unchanged to the audit barrier and
/// then to dispatch. The planner owns every constructor input; execution code
/// must not recover routing facts by comparing script strings.
/// </summary>
internal sealed record ExecutionPlan
{
    internal ExecutionPlan(
        string originalScript,
        string executionScript,
        ExecutionDomain? domain,
        ExecutionPath executionPath,
        PreExecutionValidation preExecutionValidation,
        ResolutionContext resolutionContext,
        RequestedExecutionRoute requestedRoute,
        OutputProvenance outputProvenance,
        ImmutableArray<ExecutionPath> permittedFallbacks,
        ExecutionFallbackReason? fallbackReason,
        RtkExecutableIdentity? rtkExecutableIdentity)
    {
        ArgumentNullException.ThrowIfNull(originalScript);
        ArgumentNullException.ThrowIfNull(executionScript);
        if (permittedFallbacks.IsDefault)
            throw new ArgumentException("Fallbacks must be initialized.", nameof(permittedFallbacks));
        if (permittedFallbacks.Any(path => path is not (
                ExecutionPath.PowerShellDirect or ExecutionPath.NativeDirect)))
        {
            throw new ArgumentException(
                "Only exact-semantics direct paths may be fallbacks.",
                nameof(permittedFallbacks));
        }
        if (permittedFallbacks.Distinct().Count() != permittedFallbacks.Length)
            throw new ArgumentException("Fallbacks must be unique.", nameof(permittedFallbacks));
        if (executionPath is ExecutionPath.Rtk or ExecutionPath.BashViaRtk &&
            (rtkExecutableIdentity is null ||
             string.IsNullOrWhiteSpace(rtkExecutableIdentity.ExecutablePath) ||
             !Path.IsPathFullyQualified(rtkExecutableIdentity.ExecutablePath)))
        {
            throw new ArgumentException(
                "RTK execution requires a pinned executable identity.",
                nameof(rtkExecutableIdentity));
        }
        if (executionPath is ExecutionPath.Rtk or ExecutionPath.BashViaRtk)
        {
            // Until Slice 4 negotiates a machine-readable RTK capture seam,
            // PTK cannot distinguish filtered output from passthrough.
            if (outputProvenance != OutputProvenance.RtkUnknown)
                throw new ArgumentException(
                    "RTK provenance is unknown without a negotiated capture seam.",
                    nameof(outputProvenance));
        }
        else if (rtkExecutableIdentity is not null ||
                 outputProvenance is OutputProvenance.RtkUnknown or
                     OutputProvenance.RtkFiltered or OutputProvenance.RtkPassthrough)
        {
            throw new ArgumentException(
                "Only RTK execution may carry RTK identity or provenance.",
                nameof(rtkExecutableIdentity));
        }
        if (fallbackReason is not null &&
            (requestedRoute != RequestedExecutionRoute.Rtk ||
             executionPath != ExecutionPath.PowerShellDirect))
        {
            throw new ArgumentException(
                "An RTK fallback reason requires a requested RTK route and a direct result.",
                nameof(fallbackReason));
        }

        OriginalScript = originalScript;
        ExecutionScript = executionScript;
        Domain = domain;
        ExecutionPath = executionPath;
        PreExecutionValidation = preExecutionValidation;
        ResolutionContext = resolutionContext;
        RequestedRoute = requestedRoute;
        OutputProvenance = outputProvenance;
        PermittedFallbacks = permittedFallbacks;
        FallbackReason = fallbackReason;
        RtkExecutableIdentity = rtkExecutableIdentity;
    }

    internal string OriginalScript { get; }
    internal string ExecutionScript { get; }
    internal ExecutionDomain? Domain { get; }
    internal ExecutionPath ExecutionPath { get; }
    internal PreExecutionValidation PreExecutionValidation { get; }
    internal ResolutionContext ResolutionContext { get; }
    internal RequestedExecutionRoute RequestedRoute { get; }
    internal OutputProvenance OutputProvenance { get; }
    internal ImmutableArray<ExecutionPath> PermittedFallbacks { get; }
    internal ExecutionFallbackReason? FallbackReason { get; }
    internal RtkExecutableIdentity? RtkExecutableIdentity { get; }

    internal string EffectiveRoute => ExecutionPath.ToMachineCode();
}

internal static class ExecutionPlanMachineCodes
{
    internal static string ToMachineCode(this ExecutionDomain value) => value switch
    {
        ExecutionDomain.PowerShell => "powershell",
        ExecutionDomain.NativeTerminal => "native_terminal",
        ExecutionDomain.MixedDataflow => "mixed_dataflow",
        ExecutionDomain.Bash => "bash",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    internal static string ToMachineCode(this ExecutionPath value) => value switch
    {
        ExecutionPath.PowerShellDirect => "powershell_direct",
        ExecutionPath.Rtk => "rtk",
        ExecutionPath.NativeDirect => "native_direct",
        ExecutionPath.BashViaRtk => "bash_via_rtk",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    internal static string ToMachineCode(this RequestedExecutionRoute value) => value switch
    {
        RequestedExecutionRoute.Auto => "auto",
        RequestedExecutionRoute.PowerShell => "pwsh",
        RequestedExecutionRoute.Rtk => "rtk",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    internal static string ToMachineCode(this OutputProvenance value) => value switch
    {
        OutputProvenance.PowerShellObjects => "powershell_objects",
        OutputProvenance.DirectText => "direct_text",
        OutputProvenance.RtkUnknown => "rtk_unknown",
        OutputProvenance.RtkFiltered => "rtk_filtered",
        OutputProvenance.RtkPassthrough => "rtk_passthrough",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    internal static string ToMachineCode(this ExecutionFallbackReason value) => value switch
    {
        ExecutionFallbackReason.RtkExecutableUnavailable => "rtk_executable_unavailable",
        ExecutionFallbackReason.RtkIneligibleShape => "rtk_ineligible_shape",
        ExecutionFallbackReason.RtkSelfInvocation => "rtk_self_invocation",
        ExecutionFallbackReason.RtkResolutionNotApplication => "rtk_resolution_not_application",
        ExecutionFallbackReason.RtkFidelityExclusion => "rtk_fidelity_exclusion",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
