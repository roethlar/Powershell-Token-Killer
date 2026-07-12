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
    internal static ExecutionPlan Create(
        string script,
        string? route,
        string? effectiveRtkPath,
        TrustedCommandSnapshot commands,
        bool raw,
        bool compressAvailable,
        ResolutionContext resolutionContext)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(commands);

        var requestedRoute = NormalizeRoute(route);
        // The current in-process dispatch has no post-plan resolution barrier,
        // so it cannot truthfully promise a fallback yet. The dedicated RTK
        // runner adds only the paths it can enforce before execution starts.
        var fallbacks = ImmutableArray<ExecutionPath>.Empty;
        if (raw || requestedRoute == RequestedExecutionRoute.PowerShell)
        {
            return Direct(
                script,
                raw,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain: null,
                fallbacks,
                fallbackReason: null);
        }

        var domain = ClassifyDomain(script, commands);
        if (string.IsNullOrEmpty(effectiveRtkPath))
        {
            return Direct(
                script,
                raw,
                compressAvailable,
                resolutionContext,
                requestedRoute,
                domain,
                fallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkExecutableUnavailable
                    : null);
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
                fallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkIneligibleShape
                    : null);
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
                fallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkSelfInvocation
                    : null);
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
                fallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkResolutionNotApplication
                    : null);
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
                fallbacks,
                requestedRoute == RequestedExecutionRoute.Rtk
                    ? ExecutionFallbackReason.RtkFidelityExclusion
                    : null);
        }

        var escapedRtk = effectiveRtkPath.Replace("'", "''");
        return new ExecutionPlan(
            script,
            $"& '{escapedRtk}' {command.Extent.Text}",
            domain,
            ExecutionPath.Rtk,
            PreExecutionValidation.None,
            resolutionContext,
            requestedRoute,
            OutputProvenance.RtkUnknown,
            fallbacks,
            fallbackReason: null,
            new RtkExecutableIdentity(effectiveRtkPath));
    }

    internal static ExecutionPlan CreateDirect(
        string script,
        string? route,
        bool raw,
        bool compressAvailable,
        ResolutionContext resolutionContext) =>
        Direct(
            script,
            raw,
            compressAvailable,
            resolutionContext,
            NormalizeRoute(route),
            domain: null,
            ImmutableArray<ExecutionPath>.Empty,
            fallbackReason: null);

    private static ExecutionPlan Direct(
        string script,
        bool raw,
        bool compressAvailable,
        ResolutionContext resolutionContext,
        RequestedExecutionRoute requestedRoute,
        ExecutionDomain? domain,
        ImmutableArray<ExecutionPath> fallbacks,
        ExecutionFallbackReason? fallbackReason) =>
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
            rtkExecutableIdentity: null);

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
        if (ast.ParamBlock is not null || ast.BeginBlock is not null || ast.ProcessBlock is not null)
            return null;
        if (ast.EndBlock is null || ast.EndBlock.Statements.Count != 1) return null;
        if (ast.EndBlock.Statements[0] is not PipelineAst pipeline) return null;
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

    private static ExecutionDomain? ClassifyDomain(
        string script,
        TrustedCommandSnapshot commands)
    {
        var ast = Parser.ParseInput(script, out _, out var parseErrors);
        if (parseErrors.Length > 0) return null;
        if (ast.ParamBlock is not null || ast.BeginBlock is not null || ast.ProcessBlock is not null)
            return ExecutionDomain.MixedDataflow;
        if (ast.EndBlock is null || ast.EndBlock.Statements.Count == 0)
            return ExecutionDomain.PowerShell;
        if (ast.EndBlock.Statements.Count != 1)
            return ExecutionDomain.MixedDataflow;
        if (ast.EndBlock.Statements[0] is not PipelineAst pipeline)
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
