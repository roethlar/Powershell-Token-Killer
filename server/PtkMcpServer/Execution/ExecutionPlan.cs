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

internal sealed record PostSuccessGuidance
{
    internal const string PreferNativeRedirection = "prefer_native_redirection";
    internal const int MaximumSuggestedScriptCharacters = 1024;

    internal PostSuccessGuidance(string code, string suggestedScript)
    {
        if (!string.Equals(code, PreferNativeRedirection, StringComparison.Ordinal))
            throw new ArgumentException("Unknown post-success guidance code.", nameof(code));
        if (string.IsNullOrWhiteSpace(suggestedScript) ||
            suggestedScript.Length > MaximumSuggestedScriptCharacters ||
            suggestedScript.Contains('\r') || suggestedScript.Contains('\n'))
        {
            throw new ArgumentException(
                "A suggested script must be one bounded nonempty line.",
                nameof(suggestedScript));
        }

        Code = code;
        SuggestedScript = suggestedScript;
    }

    internal string Code { get; }
    internal string SuggestedScript { get; }

    internal string Render() =>
        $"[ptk:routing] mixed native/PowerShell file capture completed unchanged. " +
        $"For direct file capture next time, prefer: {SuggestedScript}";
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
    RtkExecutableBecameUnavailable,
    RtkIneligibleShape,
    RtkSelfInvocation,
    RtkResolutionNotApplication,
    RtkFidelityExclusion,
    RtkExecutionPreparationFailed,
    RtkTargetResolutionChanged,
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
    RtkVerifiedBinaryIdentity? Verified = null,
    string? CapturedBinaryDigest = null,
    UnixFileMode? CapturedUnixFileMode = null)
{
    internal string? AuditBinaryDigest =>
        Verified?.BinaryDigest ?? CapturedBinaryDigest;

    internal static RtkExecutableIdentity? TryCapture(
        string? path,
        RtkVerifiedBinaryIdentity? verified = null)
    {
        var identity = ExecutableFileIdentity.TryCapture(path);
        return identity is null
            ? null
            : new RtkExecutableIdentity(
                identity.ExecutablePath,
                verified,
                identity.BinaryDigest,
                identity.UnixFileMode);
    }

    internal bool MatchesCurrentFile()
    {
        if (CapturedBinaryDigest is null)
            return File.Exists(ExecutablePath);
        var current = ExecutableFileIdentity.TryCapture(ExecutablePath);
        return current is not null &&
               string.Equals(
                   current.ExecutablePath,
                   ExecutablePath,
                   OperatingSystem.IsWindows()
                       ? StringComparison.OrdinalIgnoreCase
                       : StringComparison.Ordinal) &&
               string.Equals(
                   current.BinaryDigest,
                   CapturedBinaryDigest,
                   StringComparison.Ordinal) &&
               (CapturedUnixFileMode is null ||
                current.UnixFileMode == CapturedUnixFileMode);
    }
}

/// <summary>
/// Immutable execution preparation handed unchanged to the audit barrier and
/// then to dispatch. The planner owns every constructor input; execution code
/// must not recover routing facts by comparing script strings.
/// </summary>
internal sealed record ExecutionPlan
{
    internal ExecutionPlan(
        string originalScript,
        string? executionScript,
        ExecutionDomain? domain,
        ExecutionPath executionPath,
        PreExecutionValidation preExecutionValidation,
        ResolutionContext resolutionContext,
        RequestedExecutionRoute requestedRoute,
        OutputProvenance outputProvenance,
        ImmutableArray<ExecutionPath> permittedFallbacks,
        ExecutionFallbackReason? fallbackReason,
        RtkExecutableIdentity? rtkExecutableIdentity,
        BashExecutableIdentity? bashExecutableIdentity = null,
        string? workingDirectory = null,
        RtkExecutableIdentity? outputShapingRtkIdentity = null,
        PostSuccessGuidance? postSuccessGuidance = null,
        ImmutableArray<string> rtkArgumentVector = default,
        OutputProvenance? directFallbackProvenance = null,
        ColdCommandTargetIdentity? coldCommandTargetIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(originalScript);
        var isBash = executionPath == ExecutionPath.BashViaRtk;
        var isRtk = executionPath == ExecutionPath.Rtk;
        var normalizedRtkArguments = rtkArgumentVector.IsDefault
            ? ImmutableArray<string>.Empty
            : rtkArgumentVector;
        if (isBash)
        {
            if (executionScript is not null ||
                domain != ExecutionDomain.Bash ||
                preExecutionValidation != PreExecutionValidation.BashSyntax ||
                requestedRoute == RequestedExecutionRoute.PowerShell ||
                bashExecutableIdentity is null ||
                string.IsNullOrWhiteSpace(bashExecutableIdentity.ExecutablePath) ||
                !Path.IsPathFullyQualified(bashExecutableIdentity.ExecutablePath) ||
                string.IsNullOrWhiteSpace(workingDirectory) ||
                !Path.IsPathFullyQualified(workingDirectory) ||
                normalizedRtkArguments.Length != 0)
            {
                throw new ArgumentException(
                    "Bash delegation requires a typed pinned identity, filesystem cwd, and no constructed script.");
            }
        }
        else if (isRtk)
        {
            if (executionScript is not null ||
                bashExecutableIdentity is not null ||
                preExecutionValidation != PreExecutionValidation.None ||
                string.IsNullOrWhiteSpace(workingDirectory) ||
                !Path.IsPathFullyQualified(workingDirectory) ||
                normalizedRtkArguments.Length == 0 ||
                string.IsNullOrWhiteSpace(normalizedRtkArguments[0]) ||
                normalizedRtkArguments.Any(argument => argument is null))
            {
                throw new ArgumentException(
                    "RTK execution requires a typed argument vector, filesystem cwd, and no constructed script.");
            }
        }
        else if (executionScript is null ||
                 bashExecutableIdentity is not null ||
                 workingDirectory is not null ||
                 normalizedRtkArguments.Length != 0 ||
                 preExecutionValidation != PreExecutionValidation.None)
        {
            throw new ArgumentException(
                "Only typed external execution may carry validation, cwd, or argument-vector facts.",
                nameof(bashExecutableIdentity));
        }
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
        if (isBash && permittedFallbacks.Length != 0)
            throw new ArgumentException("Bash delegation has no PowerShell fallback.", nameof(permittedFallbacks));
        var hasPowerShellFallback =
            permittedFallbacks.Contains(ExecutionPath.PowerShellDirect);
        if (isRtk && hasPowerShellFallback)
        {
            if (directFallbackProvenance is not (
                    OutputProvenance.PowerShellObjects or OutputProvenance.DirectText))
            {
                throw new ArgumentException(
                    "An RTK PowerShell fallback requires frozen direct-output provenance.",
                    nameof(directFallbackProvenance));
            }
            if (resolutionContext == ResolutionContext.Cold &&
                directFallbackProvenance != OutputProvenance.DirectText)
            {
                throw new ArgumentException(
                    "A cold PowerShell fallback produces direct text.",
                    nameof(directFallbackProvenance));
            }
        }
        else if (directFallbackProvenance is not null)
        {
            throw new ArgumentException(
                "Only an RTK plan with a PowerShell fallback may carry direct-fallback provenance.",
                nameof(directFallbackProvenance));
        }
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
        if (isRtk && resolutionContext == ResolutionContext.Cold &&
            coldCommandTargetIdentity is null)
        {
            throw new ArgumentException(
                "Cold RTK execution requires a revalidatable target identity.",
                nameof(coldCommandTargetIdentity));
        }
        if (coldCommandTargetIdentity is not null &&
            (!isRtk || resolutionContext != ResolutionContext.Cold))
        {
            throw new ArgumentException(
                "A cold target identity belongs only to cold RTK execution.",
                nameof(coldCommandTargetIdentity));
        }
        if (coldCommandTargetIdentity is not null &&
            (!string.Equals(
                 coldCommandTargetIdentity.CommandName,
                 normalizedRtkArguments[0],
                 StringComparison.Ordinal) ||
             !Path.IsPathFullyQualified(
                 coldCommandTargetIdentity.Executable.ExecutablePath) ||
             !string.Equals(
                 coldCommandTargetIdentity.WorkingDirectory,
                 Path.GetFullPath(workingDirectory!),
                 OperatingSystem.IsWindows()
                     ? StringComparison.OrdinalIgnoreCase
                     : StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "A cold target identity must bind the RTK command and cwd.",
                nameof(coldCommandTargetIdentity));
        }
        if (resolutionContext == ResolutionContext.Cold &&
            executionPath == ExecutionPath.PowerShellDirect &&
            outputProvenance != OutputProvenance.DirectText)
        {
            throw new ArgumentException(
                "Cold direct PowerShell execution produces direct text.",
                nameof(outputProvenance));
        }
        if (outputShapingRtkIdentity is not null &&
            (executionPath != ExecutionPath.PowerShellDirect ||
             outputProvenance != OutputProvenance.PowerShellObjects ||
             string.IsNullOrWhiteSpace(outputShapingRtkIdentity.ExecutablePath) ||
             !Path.IsPathFullyQualified(outputShapingRtkIdentity.ExecutablePath)))
        {
            throw new ArgumentException(
                "An output-shaping RTK identity requires a shaped direct PowerShell plan.",
                nameof(outputShapingRtkIdentity));
        }
        if (postSuccessGuidance is not null &&
            (domain != ExecutionDomain.MixedDataflow ||
             executionPath != ExecutionPath.PowerShellDirect))
        {
            throw new ArgumentException(
                "Post-success guidance requires a mixed-dataflow direct plan.",
                nameof(postSuccessGuidance));
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
        BashExecutableIdentity = bashExecutableIdentity;
        WorkingDirectory = workingDirectory;
        OutputShapingRtkIdentity = outputShapingRtkIdentity;
        PostSuccessGuidance = postSuccessGuidance;
        RtkArgumentVector = normalizedRtkArguments;
        DirectFallbackProvenance = directFallbackProvenance;
        ColdCommandTargetIdentity = coldCommandTargetIdentity;
    }

    internal string OriginalScript { get; }
    internal string? ExecutionScript { get; }
    internal ExecutionDomain? Domain { get; }
    internal ExecutionPath ExecutionPath { get; }
    internal PreExecutionValidation PreExecutionValidation { get; }
    internal ResolutionContext ResolutionContext { get; }
    internal RequestedExecutionRoute RequestedRoute { get; }
    internal OutputProvenance OutputProvenance { get; }
    internal ImmutableArray<ExecutionPath> PermittedFallbacks { get; }
    internal ExecutionFallbackReason? FallbackReason { get; }
    internal RtkExecutableIdentity? RtkExecutableIdentity { get; }
    internal BashExecutableIdentity? BashExecutableIdentity { get; }
    internal string? WorkingDirectory { get; }
    internal RtkExecutableIdentity? OutputShapingRtkIdentity { get; }
    internal PostSuccessGuidance? PostSuccessGuidance { get; }
    internal ImmutableArray<string> RtkArgumentVector { get; }
    internal OutputProvenance? DirectFallbackProvenance { get; }
    internal ColdCommandTargetIdentity? ColdCommandTargetIdentity { get; }

    internal string EffectiveRoute => ExecutionPath.ToMachineCode();
}

/// <summary>
/// Immutable execution selected after the prepared plan has crossed its audit
/// barrier. A dispatch may either preserve that plan exactly or consume one of
/// its explicitly permitted, exact-semantics fallbacks. It never invents a
/// route by comparing script text.
/// </summary>
internal sealed record ExecutionDispatch
{
    private ExecutionDispatch(
        ExecutionPlan plan,
        string? executionScript,
        ExecutionPath executionPath,
        OutputProvenance outputProvenance,
        ExecutionFallbackReason? fallbackReason,
        RtkExecutableIdentity? rtkExecutableIdentity)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var followsPlan = executionPath == plan.ExecutionPath;
        if (!followsPlan && !plan.PermittedFallbacks.Contains(executionPath))
            throw new InvalidOperationException("The dispatch path was not authorized by the plan.");

        if (followsPlan)
        {
            if (!string.Equals(executionScript, plan.ExecutionScript, StringComparison.Ordinal) ||
                outputProvenance != plan.OutputProvenance ||
                fallbackReason != plan.FallbackReason ||
                rtkExecutableIdentity != plan.RtkExecutableIdentity)
            {
                throw new InvalidOperationException(
                    "A same-path dispatch must preserve every planned execution fact.");
            }
        }
        else
        {
            if (!string.Equals(executionScript, plan.OriginalScript, StringComparison.Ordinal))
                throw new InvalidOperationException("A direct fallback must execute the exact original script.");
            if (executionPath != ExecutionPath.PowerShellDirect ||
                outputProvenance != plan.DirectFallbackProvenance ||
                fallbackReason is null ||
                rtkExecutableIdentity is not null)
            {
                throw new InvalidOperationException(
                    "A fallback must carry exact direct-path provenance and a truthful reason.");
            }
        }

        Plan = plan;
        ExecutionScript = executionScript;
        ExecutionPath = executionPath;
        OutputProvenance = outputProvenance;
        FallbackReason = fallbackReason;
        RtkExecutableIdentity = rtkExecutableIdentity;
    }

    internal ExecutionPlan Plan { get; }
    internal string? ExecutionScript { get; }
    internal ExecutionPath ExecutionPath { get; }
    internal OutputProvenance OutputProvenance { get; }
    internal ExecutionFallbackReason? FallbackReason { get; }
    internal RtkExecutableIdentity? RtkExecutableIdentity { get; }
    internal ImmutableArray<string> RtkArgumentVector =>
        ExecutionPath == ExecutionPath.Rtk
            ? Plan.RtkArgumentVector
            : ImmutableArray<string>.Empty;
    internal ColdCommandTargetIdentity? ColdCommandTargetIdentity =>
        ExecutionPath == ExecutionPath.Rtk
            ? Plan.ColdCommandTargetIdentity
            : null;
    internal BashExecutableIdentity? BashExecutableIdentity => Plan.BashExecutableIdentity;
    internal string? WorkingDirectory => Plan.WorkingDirectory;
    internal RtkExecutableIdentity? OutputShapingRtkIdentity =>
        ExecutionPath == ExecutionPath.PowerShellDirect
            ? Plan.OutputShapingRtkIdentity
            : null;
    internal PostSuccessGuidance? PostSuccessGuidance => Plan.PostSuccessGuidance;
    internal ExecutionDomain? Domain => Plan.Domain;
    internal PreExecutionValidation PreExecutionValidation => Plan.PreExecutionValidation;
    internal ResolutionContext ResolutionContext => Plan.ResolutionContext;
    internal RequestedExecutionRoute RequestedRoute => Plan.RequestedRoute;
    internal ImmutableArray<ExecutionPath> PermittedFallbacks => Plan.PermittedFallbacks;
    internal string EffectiveRoute => ExecutionPath.ToMachineCode();
    internal bool IsFallback => ExecutionPath != Plan.ExecutionPath;

    internal static ExecutionDispatch FromPlan(ExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new ExecutionDispatch(
            plan,
            plan.ExecutionScript,
            plan.ExecutionPath,
            plan.OutputProvenance,
            plan.FallbackReason,
            plan.RtkExecutableIdentity);
    }

    internal static ExecutionDispatch RtkUnavailableFallback(ExecutionPlan plan)
        => RtkPreStartFallback(
            plan,
            ExecutionFallbackReason.RtkExecutableBecameUnavailable);

    internal static ExecutionDispatch RtkPreStartFallback(
        ExecutionPlan plan,
        ExecutionFallbackReason reason)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.ExecutionPath != ExecutionPath.Rtk ||
            !plan.PermittedFallbacks.Contains(ExecutionPath.PowerShellDirect))
        {
            throw new InvalidOperationException(
                "Only an RTK plan with an authorized direct fallback may fall back.");
        }
        if (reason is not (
                ExecutionFallbackReason.RtkExecutableBecameUnavailable or
                ExecutionFallbackReason.RtkExecutionPreparationFailed or
                ExecutionFallbackReason.RtkTargetResolutionChanged))
        {
            throw new InvalidOperationException(
                "Only a proven pre-start RTK failure may consume the exact fallback.");
        }

        return new ExecutionDispatch(
            plan,
            plan.OriginalScript,
            ExecutionPath.PowerShellDirect,
            plan.DirectFallbackProvenance ?? throw new InvalidOperationException(
                "The RTK plan did not freeze direct-fallback provenance."),
            reason,
            rtkExecutableIdentity: null);
    }
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

    internal static string ToMachineCode(this ExecutionFallbackReason value) => value switch
    {
        ExecutionFallbackReason.RtkExecutableUnavailable => "rtk_executable_unavailable",
        ExecutionFallbackReason.RtkExecutableBecameUnavailable => "rtk_executable_became_unavailable",
        ExecutionFallbackReason.RtkIneligibleShape => "rtk_ineligible_shape",
        ExecutionFallbackReason.RtkSelfInvocation => "rtk_self_invocation",
        ExecutionFallbackReason.RtkResolutionNotApplication => "rtk_resolution_not_application",
        ExecutionFallbackReason.RtkFidelityExclusion => "rtk_fidelity_exclusion",
        ExecutionFallbackReason.RtkExecutionPreparationFailed => "rtk_execution_preparation_failed",
        ExecutionFallbackReason.RtkTargetResolutionChanged => "rtk_target_resolution_changed",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
