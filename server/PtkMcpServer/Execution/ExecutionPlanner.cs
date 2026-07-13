using System.Collections.Immutable;
using System.Management.Automation;
using System.Management.Automation.Language;

namespace PtkMcpServer;

/// <summary>
/// Pure planner over a captured command snapshot. It never resolves commands,
/// starts a process, or enters the user runspace.
/// </summary>
internal static class ExecutionPlanner
{
    private static readonly HashSet<string> ContextChangingContainerWrappers = new(
        ["docker", "podman", "kubectl", "oc"],
        StringComparer.OrdinalIgnoreCase);

    internal static ExecutionPlan Create(
        string script,
        string? route,
        RtkExecutableIdentity? effectiveRtkIdentity,
        TrustedCommandSnapshot commands,
        bool raw,
        bool compressAvailable,
        ResolutionContext resolutionContext,
        bool allowFileSystemGuidance = false)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(commands);
        if (effectiveRtkIdentity is not null &&
            (string.IsNullOrWhiteSpace(effectiveRtkIdentity.ExecutablePath) ||
             !Path.IsPathFullyQualified(effectiveRtkIdentity.ExecutablePath)))
        {
            effectiveRtkIdentity = null;
        }

        var requestedRoute = NormalizeRoute(route);
        var noFallbacks = ImmutableArray<ExecutionPath>.Empty;
        if (raw || requestedRoute == RequestedExecutionRoute.PowerShell)
        {
            return Direct(
                script,
                raw,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain: null,
                noFallbacks,
                fallbackReason: null,
                effectiveRtkIdentity,
                postSuccessGuidance: null);
        }

        var domain = ClassifyDomain(script, commands);
        var postSuccessGuidance = allowFileSystemGuidance &&
                                  domain == ExecutionDomain.MixedDataflow
            ? TryCreateMixedDataflowGuidance(script, commands)
            : null;
        if (effectiveRtkIdentity is null)
        {
            return Direct(
                script,
                raw,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain,
                noFallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkExecutableUnavailable
                    : null,
                outputShapingRtkIdentity: null,
                postSuccessGuidance);
        }

        var command = GetEligibleCommand(script);
        if (command is null)
        {
            return Direct(
                script,
                raw,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain,
                noFallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkIneligibleShape
                    : null,
                effectiveRtkIdentity,
                postSuccessGuidance);
        }

        var name = ((StringConstantExpressionAst)command.CommandElements[0]).Value;
        if (Path.GetFileNameWithoutExtension(name)
            .Equals("rtk", StringComparison.OrdinalIgnoreCase))
        {
            return Direct(
                script,
                raw,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain,
                noFallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkSelfInvocation
                    : null,
                effectiveRtkIdentity,
                postSuccessGuidance);
        }

        var resolved = commands.Resolve(name, CommandTypes.All);
        if (resolved?.CommandType != CommandTypes.Application)
        {
            return Direct(
                script,
                raw,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain,
                noFallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkResolutionNotApplication
                    : null,
                effectiveRtkIdentity,
                postSuccessGuidance);
        }
        var extension = Path.GetExtension(resolved.Source ?? string.Empty);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return Direct(
                script,
                raw,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain,
                noFallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkFidelityExclusion
                    : null,
                effectiveRtkIdentity,
                postSuccessGuidance);
        }
        if (IsContextChangingWrapper(command))
        {
            return Direct(
                script,
                raw,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain,
                noFallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkFidelityExclusion
                    : null,
                effectiveRtkIdentity,
                postSuccessGuidance);
        }

        var escapedRtk = effectiveRtkIdentity.ExecutablePath.Replace("'", "''");
        return new ExecutionPlan(
            script,
            $"& '{escapedRtk}' {command.Extent.Text}",
            domain,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            resolutionContext,
            requestedRoute,
            OutputProvenance.RtkUnknown,
            ImmutableArray.Create(ExecutionPath.PowerShellDirect),
            fallbackReason: null,
            effectiveRtkIdentity);
    }

    internal static ExecutionPlan CreateDirect(
        string script,
        string? route,
        bool raw,
        bool compressAvailable,
        ResolutionContext resolutionContext,
        RtkExecutableIdentity? outputShapingRtkIdentity = null) =>
        Direct(
            script,
            raw,
            compressAvailable,
            resolutionContext,
            NormalizeRoute(route),
            domain: null,
            ImmutableArray<ExecutionPath>.Empty,
            fallbackReason: null,
            outputShapingRtkIdentity,
            postSuccessGuidance: null);

    internal static ExecutionPlan CreateBash(
        string script,
        string? route,
        RtkExecutableIdentity rtkExecutableIdentity,
        BashExecutableIdentity bashExecutableIdentity,
        string workingDirectory,
        ResolutionContext resolutionContext)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(rtkExecutableIdentity);
        ArgumentNullException.ThrowIfNull(bashExecutableIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var requestedRoute = NormalizeRoute(route);
        Parser.ParseInput(script, out _, out var parseErrors);
        if (parseErrors.Length == 0 || requestedRoute == RequestedExecutionRoute.PowerShell)
        {
            throw new ArgumentException(
                "Bash delegation requires independently parse-fatal PowerShell input without route=pwsh consent.",
                nameof(script));
        }

        return new ExecutionPlan(
            script,
            executionScript: null,
            ExecutionDomain.Bash,
            ExecutionPath.BashViaRtk,
            PreExecutionValidation.BashSyntax,
            resolutionContext,
            requestedRoute,
            OutputProvenance.RtkUnknown,
            ImmutableArray<ExecutionPath>.Empty,
            fallbackReason: null,
            rtkExecutableIdentity,
            bashExecutableIdentity,
            workingDirectory);
    }

    private static ExecutionPlan Direct(
        string script,
        bool raw,
        bool compressAvailable,
        ResolutionContext resolutionContext,
        RequestedExecutionRoute requestedRoute,
        ExecutionDomain? domain,
        ImmutableArray<ExecutionPath> fallbacks,
        ExecutionFallbackReason? fallbackReason,
        RtkExecutableIdentity? outputShapingRtkIdentity,
        PostSuccessGuidance? postSuccessGuidance) =>
        new(
            script,
            script,
            domain,
            ExecutionPath.PowerShellDirect,
            PreExecutionValidation.None,
            resolutionContext,
            requestedRoute,
            raw || !compressAvailable
                ? OutputProvenance.DirectText
                : OutputProvenance.PowerShellObjects,
            fallbacks,
            fallbackReason,
            rtkExecutableIdentity: null,
            outputShapingRtkIdentity: raw || !compressAvailable
                ? null
                : outputShapingRtkIdentity,
            postSuccessGuidance: raw
                ? null
                : postSuccessGuidance);

    private static RequestedExecutionRoute NormalizeRoute(string? route) =>
        route?.ToLowerInvariant() switch
        {
            "pwsh" => RequestedExecutionRoute.PowerShell,
            "rtk" => RequestedExecutionRoute.Rtk,
            _ => RequestedExecutionRoute.Auto,
        };

    private static CommandAst? GetEligibleCommand(string script)
    {
        var ast = Parser.ParseInput(script, out _, out var parseErrors);
        if (parseErrors.Length > 0) return null;
        if (ast.UsingStatements.Count > 0 ||
            ast.ParamBlock is not null || ast.DynamicParamBlock is not null ||
            ast.BeginBlock is not null || ast.ProcessBlock is not null ||
            ast.CleanBlock is not null)
            return null;
        if (ast.EndBlock is null || ast.EndBlock.Statements.Count != 1) return null;
        if (ast.EndBlock.Statements[0] is not PipelineAst pipeline) return null;
        if (pipeline.Background) return null;
        if (pipeline.PipelineElements.Count != 1) return null;
        if (pipeline.PipelineElements[0] is not CommandAst command) return null;
        if (command.InvocationOperator != TokenKind.Unknown || command.Redirections.Count > 0)
            return null;

        var elements = command.CommandElements;
        if (elements.Count == 0 || elements[0] is not StringConstantExpressionAst)
            return null;

        foreach (var element in elements.Skip(1))
        {
            var isConstant = element is ConstantExpressionAst ||
                element is CommandParameterAst parameter &&
                (parameter.Argument is null || parameter.Argument is ConstantExpressionAst);
            if (!isConstant) return null;
        }

        return command;
    }

    private static bool IsContextChangingWrapper(CommandAst command)
    {
        if (command.CommandElements.FirstOrDefault() is not
                StringConstantExpressionAst executable)
        {
            return false;
        }

        var executableName = Path.GetFileNameWithoutExtension(executable.Value);
        return ContextChangingContainerWrappers.Contains(executableName) &&
               command.CommandElements.Skip(1)
                   .OfType<StringConstantExpressionAst>()
                   .Any(element => element.Value.Equals(
                       "exec",
                       StringComparison.OrdinalIgnoreCase));
    }

    private static PostSuccessGuidance? TryCreateMixedDataflowGuidance(
        string script,
        TrustedCommandSnapshot commands)
    {
        if (script.Contains('\r') || script.Contains('\n')) return null;
        var ast = Parser.ParseInput(script, out _, out var parseErrors);
        if (parseErrors.Length > 0 || ast.UsingStatements.Count > 0 ||
            ast.ParamBlock is not null ||
            ast.DynamicParamBlock is not null || ast.BeginBlock is not null ||
            ast.ProcessBlock is not null || ast.CleanBlock is not null ||
            ast.EndBlock?.Statements.Count != 1 ||
            ast.EndBlock.Statements[0] is not PipelineAst pipeline ||
            pipeline.Background ||
            pipeline.PipelineElements.Count != 2 ||
            pipeline.PipelineElements[0] is not CommandAst producer ||
            pipeline.PipelineElements[1] is not CommandAst sink ||
            producer.InvocationOperator != TokenKind.Unknown ||
            sink.InvocationOperator != TokenKind.Unknown ||
            producer.Redirections.Count > 0 || sink.Redirections.Count > 0 ||
            producer.CommandElements.FirstOrDefault() is not
                StringConstantExpressionAst producerName ||
            sink.CommandElements.FirstOrDefault() is not
                StringConstantExpressionAst sinkName ||
            !HasOnlyConstantArguments(producer))
        {
            return null;
        }

        var resolvedProducer = commands.Resolve(producerName.Value, CommandTypes.All);
        if (resolvedProducer?.CommandType != CommandTypes.Application)
            return null;
        var producerExtension = Path.GetExtension(resolvedProducer.Source ?? string.Empty);
        if (producerExtension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            producerExtension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var resolvedSink = commands.Resolve(sinkName.Value, CommandTypes.All);
        if (!sinkName.Value.Equals("Set-Content", StringComparison.OrdinalIgnoreCase) ||
            resolvedSink?.CommandType != CommandTypes.Cmdlet ||
            !resolvedSink.IsCanonicalManagementSetContent ||
            !string.Equals(
                resolvedSink.Source,
                "Microsoft.PowerShell.Management",
                StringComparison.OrdinalIgnoreCase) ||
            !TryGetSimpleSetContentPath(sink, out var pathExpression))
        {
            return null;
        }

        var path = pathExpression.Value;
        if (string.IsNullOrWhiteSpace(path) ||
            path.IndexOfAny(['*', '?', '[', ']']) >= 0 ||
            LooksProviderQualified(path))
        {
            return null;
        }

        var suggestedScript =
            $"{producer.Extent.Text.Trim()} > {pathExpression.Extent.Text}";
        if (suggestedScript.Length >
                PostSuccessGuidance.MaximumSuggestedScriptCharacters ||
            suggestedScript.Contains('\r') || suggestedScript.Contains('\n'))
        {
            return null;
        }
        return new PostSuccessGuidance(
            PostSuccessGuidance.PreferNativeRedirection,
            suggestedScript);
    }

    private static bool LooksProviderQualified(string path)
    {
        // PowerShell drive letters are dynamic PSDrives, so even C:\\ is not
        // proof that Set-Content will target the filesystem provider.
        return path.Contains(':');
    }

    private static bool HasOnlyConstantArguments(CommandAst command)
    {
        foreach (var element in command.CommandElements.Skip(1))
        {
            var isConstant = element is ConstantExpressionAst ||
                element is CommandParameterAst parameter &&
                (parameter.Argument is null ||
                 parameter.Argument is ConstantExpressionAst);
            if (!isConstant) return false;
        }
        return true;
    }

    private static bool TryGetSimpleSetContentPath(
        CommandAst sink,
        out StringConstantExpressionAst path)
    {
        path = null!;
        var elements = sink.CommandElements;
        if (elements.Count == 2 &&
            elements[1] is StringConstantExpressionAst positional)
        {
            path = positional;
            return true;
        }
        if (elements.Count == 2 && elements[1] is CommandParameterAst
            {
                ParameterName: var parameterName,
                Argument: StringConstantExpressionAst attached,
            } && parameterName.Equals("Path", StringComparison.OrdinalIgnoreCase))
        {
            path = attached;
            return true;
        }
        if (elements.Count == 3 && elements[1] is CommandParameterAst
            {
                ParameterName: var separatedName,
                Argument: null,
            } && separatedName.Equals("Path", StringComparison.OrdinalIgnoreCase) &&
            elements[2] is StringConstantExpressionAst separated)
        {
            path = separated;
            return true;
        }
        return false;
    }

    private static ExecutionDomain? ClassifyDomain(
        string script,
        TrustedCommandSnapshot commands)
    {
        var ast = Parser.ParseInput(script, out _, out var parseErrors);
        if (parseErrors.Length > 0) return null;
        if (ast.UsingStatements.Count > 0 ||
            ast.ParamBlock is not null || ast.DynamicParamBlock is not null ||
            ast.BeginBlock is not null || ast.ProcessBlock is not null ||
            ast.CleanBlock is not null)
            return ExecutionDomain.MixedDataflow;
        if (ast.EndBlock is null || ast.EndBlock.Statements.Count == 0)
            return ExecutionDomain.PowerShell;
        if (ast.EndBlock.Statements.Count != 1)
            return ExecutionDomain.MixedDataflow;
        if (ast.EndBlock.Statements[0] is not PipelineAst pipeline)
            return ExecutionDomain.MixedDataflow;
        if (pipeline.Background)
            return ExecutionDomain.MixedDataflow;
        if (pipeline.PipelineElements.Count != 1)
            return ExecutionDomain.MixedDataflow;
        if (pipeline.PipelineElements[0] is not CommandAst command)
            return ExecutionDomain.PowerShell;
        if (command.Redirections.Count > 0)
            return ExecutionDomain.MixedDataflow;

        var first = command.CommandElements.FirstOrDefault();
        if (first is not StringConstantExpressionAst commandName)
            return null;
        return commands.Resolve(commandName.Value, CommandTypes.All)?.CommandType switch
        {
            CommandTypes.Application => ExecutionDomain.NativeTerminal,
            CommandTypes.Alias or CommandTypes.Function or CommandTypes.Cmdlet or
                CommandTypes.ExternalScript or CommandTypes.Filter or CommandTypes.Configuration =>
                ExecutionDomain.PowerShell,
            _ => null,
        };
    }
}
