using System.Management.Automation;
using System.Management.Automation.Language;
using System.Text.RegularExpressions;

namespace PtkMcpServer;

/// <summary>A command-resolution fact captured by trusted host code before
/// preflight. The classifier consumes only these plain values and never calls
/// back into the user runspace.</summary>
internal sealed record ResolvedCommand(
    CommandTypes CommandType,
    string? Source = null,
    string? Definition = null);

/// <summary>Case-insensitive, data-only command facts for one preflight. A
/// missing fact is an authoritative miss; classification never performs a
/// discovery lookup of its own.</summary>
internal sealed class TrustedCommandSnapshot
{
    private readonly Dictionary<string, Dictionary<CommandTypes, ResolvedCommand?>> _commands =
        new(StringComparer.OrdinalIgnoreCase);

    internal void Set(string name, CommandTypes requestedTypes, ResolvedCommand? command)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!_commands.TryGetValue(name, out var byType))
        {
            byType = [];
            _commands.Add(name, byType);
        }
        byType[requestedTypes] = command;
    }

    internal ResolvedCommand? Resolve(string name, CommandTypes requestedTypes)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _commands.TryGetValue(name, out var byType) &&
               byType.TryGetValue(requestedTypes, out var command)
            ? command
            : null;
    }

    internal TrustedCommandSnapshot Clone()
    {
        var clone = new TrustedCommandSnapshot();
        foreach (var (name, byType) in _commands)
        {
            foreach (var (requestedTypes, command) in byType)
                clone.Set(name, requestedTypes, command);
        }
        return clone;
    }
}

/// <summary>Trusted, side-effect-free equivalents of the PowerShell module's
/// routing and dialect preflight functions. Parsing is local CLR work and all
/// command-resolution inputs arrive through <see cref="TrustedCommandSnapshot"/>.</summary>
internal static class TrustedPreflightClassifier
{
    private sealed record LocalDefinition(string Name, int Start, int End);

    private static readonly Regex EnvironmentPrefix =
        new("^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.CultureInvariant);
    private static readonly Regex AssignmentArgument =
        new("^[A-Za-z_][A-Za-z0-9_]*(=|$)", RegexOptions.CultureInvariant);
    private static readonly Regex SetFlag =
        new("^[euxo]{1,4}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex QuotedGenericFragment =
        new("(?s)\"(`.|[^\"`])*\"|'(''|[^'])*'", RegexOptions.CultureInvariant);
    private static readonly Regex CompleteBacktickPair =
        new("^`[A-Za-z][^`]*`$", RegexOptions.CultureInvariant);
    private static readonly Regex BacktickCloser =
        new("^[^`]*`$", RegexOptions.CultureInvariant);
    private static readonly Regex BacktickOpener =
        new("^`[A-Za-z][^`]*$", RegexOptions.CultureInvariant);

    internal static string[] GetRequiredCommandNames(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var ast = Parser.ParseInput(script, out _, out _);
        return
        [
            .. ast.FindAll(node => node is CommandAst, searchNestedScriptBlocks: true)
                .Cast<CommandAst>()
                .Select(command => command.CommandElements.FirstOrDefault())
                .OfType<StringConstantExpressionAst>()
                .Select(element => element.Value)
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    internal static string ResolveScript(
        string script,
        string route,
        string? effectiveRtkPath,
        TrustedCommandSnapshot commands)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(commands);

        if (route.Equals("pwsh", StringComparison.OrdinalIgnoreCase)) return script;
        if (!route.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !route.Equals("rtk", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(nameof(route), route, "Route must be auto, pwsh, or rtk.");
        }
        if (string.IsNullOrEmpty(effectiveRtkPath)) return script;

        var ast = Parser.ParseInput(script, out _, out var parseErrors);
        if (parseErrors.Length > 0) return script;
        if (ast.ParamBlock is not null || ast.BeginBlock is not null || ast.ProcessBlock is not null)
            return script;
        if (ast.EndBlock is null || ast.EndBlock.Statements.Count != 1) return script;
        if (ast.EndBlock.Statements[0] is not PipelineAst pipeline) return script;
        if (pipeline.PipelineElements.Count != 1) return script;
        if (pipeline.PipelineElements[0] is not CommandAst command) return script;
        if (command.InvocationOperator != TokenKind.Unknown || command.Redirections.Count > 0)
            return script;

        var elements = command.CommandElements;
        if (elements.Count == 0 || elements[0] is not StringConstantExpressionAst commandName)
            return script;
        var name = commandName.Value;
        if (Path.GetFileNameWithoutExtension(name).Equals("rtk", StringComparison.OrdinalIgnoreCase))
            return script;

        foreach (var element in elements.Skip(1))
        {
            var isConstant = element is ConstantExpressionAst ||
                element is CommandParameterAst parameter &&
                (parameter.Argument is null || parameter.Argument is ConstantExpressionAst);
            if (!isConstant) return script;
        }

        if (!route.Equals("rtk", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = commands.Resolve(name, CommandTypes.All);
            if (resolved?.CommandType != CommandTypes.Application) return script;
            var extension = Path.GetExtension(resolved.Source ?? string.Empty);
            if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                return script;
            }
        }

        return $"& '{effectiveRtkPath.Replace("'", "''")}' {command.Extent.Text}";
    }

    internal static string? GetShellDialectFinding(
        string script,
        TrustedCommandSnapshot commands)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(commands);

        var ast = Parser.ParseInput(script, out var tokens, out var parseErrors);
        var localDefinitions = CollectLocalDefinitions(ast);

        if (parseErrors.Length > 0)
        {
            var scanText = BlankNonCodeEvidence(script, tokens);
            if (ShapeNearError(parseErrors, scanText, "MissingFileSpecification", "<<"))
                return "a bash heredoc (<<WORD ... WORD)";

            var parseCommands = ast.FindAll(
                    node => node is CommandAst,
                    searchNestedScriptBlocks: true)
                .Cast<CommandAst>()
                .ToArray();
            if (KeywordCommandNearError(
                    parseErrors,
                    parseCommands,
                    localDefinitions,
                    commands,
                    "MissingOpenParenthesisInIfStatement",
                    ["then"]))
            {
                return "a bash if/then/fi statement";
            }
            if (KeywordCommandNearError(
                    parseErrors,
                    parseCommands,
                    localDefinitions,
                    commands,
                    "MissingOpenParenthesisAfterKeyword",
                    ["do", "done"]))
            {
                return "a bash do/done loop";
            }
            if (ShapeNearError(
                    parseErrors,
                    scanText,
                    "MissingTypename",
                    @"(^|[\s;&|(])\[{1,2}\s"))
            {
                return "a bash test expression ([ ... ] or [[ ... ]])";
            }
            if (ShapeNearError(
                    parseErrors,
                    scanText,
                    "ExpectedExpression",
                    @"\w+\s*\(\s*\)\s*\{"))
            {
                return "a bash function definition (name() { ... })";
            }
            if (ShapeNearError(
                    parseErrors,
                    scanText,
                    "RedirectionNotSupported",
                    @"<\("))
            {
                return "bash process substitution (<(...))";
            }
            return null;
        }

        var commandAsts = ast.FindAll(
                node => node is CommandAst,
                searchNestedScriptBlocks: true)
            .Cast<CommandAst>();
        foreach (var command in commandAsts)
        {
            var elements = command.CommandElements;
            string? name = null;
            if (elements.Count > 0 &&
                elements[0] is StringConstantExpressionAst first &&
                first.StringConstantType == StringConstantType.BareWord)
            {
                name = first.Value;
            }

            string? label = null;
            if (name is not null && EnvironmentPrefix.IsMatch(name))
            {
                label = $"a bash environment-variable prefix ({name} ...)";
            }
            else if (name is not null &&
                     (name.Equals("export", StringComparison.OrdinalIgnoreCase) ||
                      name.Equals("local", StringComparison.OrdinalIgnoreCase)) &&
                     elements.Count >= 2 &&
                     AssignmentArgument.IsMatch(elements[1].Extent.Text))
            {
                label = $"the bash '{name}' builtin";
            }
            else if (name?.Equals("source", StringComparison.OrdinalIgnoreCase) == true &&
                     elements.Count == 2 &&
                     elements[1] is StringConstantExpressionAst)
            {
                label = "the bash 'source' builtin";
            }

            if (label is not null && name is not null &&
                !IsLocallyDefined(localDefinitions, name, command, includeContainingDefinition: true) &&
                commands.Resolve(name, CommandTypes.All) is null)
            {
                return label;
            }

            if (name?.Equals("set", StringComparison.OrdinalIgnoreCase) == true &&
                elements.Count >= 2 && elements[1] is CommandParameterAst)
            {
                var flagsOnly = elements.Skip(1).All(element =>
                    element is CommandParameterAst parameter &&
                    parameter.Argument is null &&
                    SetFlag.IsMatch(parameter.ParameterName) ||
                    element is StringConstantExpressionAst literal &&
                    literal.Value.Equals("pipefail", StringComparison.OrdinalIgnoreCase));
                if (flagsOnly)
                {
                    var setOverridden = IsLocallyDefined(
                        localDefinitions,
                        "set",
                        command,
                        includeContainingDefinition: true);
                    if (!setOverridden)
                    {
                        var resolvedSet = commands.Resolve("set", CommandTypes.All);
                        setOverridden = resolvedSet is not null &&
                            !(resolvedSet.CommandType == CommandTypes.Alias &&
                              string.Equals(
                                  resolvedSet.Definition,
                                  "Set-Variable",
                                  StringComparison.OrdinalIgnoreCase));
                    }
                    if (!setOverridden)
                        return "bash 'set' shell options (set -e/-u/-x/-o pipefail)";
                }
            }

            var openerSeen = false;
            foreach (var element in elements)
            {
                var extentText = element.Extent.Text;
                if (CompleteBacktickPair.IsMatch(extentText) ||
                    openerSeen && BacktickCloser.IsMatch(extentText))
                {
                    return "bash command substitution in backticks (`cmd`)";
                }
                if (!openerSeen && BacktickOpener.IsMatch(extentText)) openerSeen = true;
            }
        }

        return null;
    }

    private static List<LocalDefinition> CollectLocalDefinitions(ScriptBlockAst ast)
    {
        var definitions = ast.FindAll(
                node => node is FunctionDefinitionAst,
                searchNestedScriptBlocks: true)
            .Cast<FunctionDefinitionAst>()
            .Select(definition => new LocalDefinition(
                definition.Name,
                definition.Extent.StartOffset,
                definition.Extent.EndOffset))
            .ToList();

        foreach (var aliasCommand in ast.FindAll(
                     node => node is CommandAst,
                     searchNestedScriptBlocks: true).Cast<CommandAst>())
        {
            var elements = aliasCommand.CommandElements;
            if (elements.Count == 0 ||
                elements[0] is not StringConstantExpressionAst aliasVerb ||
                !(aliasVerb.Value.Equals("Set-Alias", StringComparison.OrdinalIgnoreCase) ||
                  aliasVerb.Value.Equals("New-Alias", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string? aliasName = null;
            for (var index = 1; index < elements.Count; index++)
            {
                var element = elements[index];
                if (element is CommandParameterAst parameter)
                {
                    if (parameter.ParameterName.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    {
                        if (parameter.Argument is StringConstantExpressionAst attachedName)
                        {
                            aliasName = attachedName.Value;
                        }
                        else if (index + 1 < elements.Count &&
                                 elements[index + 1] is StringConstantExpressionAst followingName)
                        {
                            aliasName = followingName.Value;
                        }
                    }
                    break;
                }
                if (element is StringConstantExpressionAst positionalName)
                {
                    aliasName = positionalName.Value;
                    break;
                }
            }
            if (!string.IsNullOrEmpty(aliasName))
            {
                definitions.Add(new LocalDefinition(
                    aliasName,
                    aliasCommand.Extent.StartOffset,
                    aliasCommand.Extent.EndOffset));
            }
        }

        return definitions;
    }

    private static string BlankNonCodeEvidence(string script, IReadOnlyList<Token> tokens)
    {
        var chars = script.ToCharArray();
        foreach (var token in tokens)
        {
            if (token.Kind is TokenKind.Comment or
                TokenKind.StringLiteral or
                TokenKind.StringExpandable or
                TokenKind.HereStringLiteral or
                TokenKind.HereStringExpandable)
            {
                Blank(chars, token.Extent.StartOffset, token.Extent.EndOffset);
                continue;
            }

            if (token.Kind == TokenKind.Generic &&
                (token is StringLiteralToken || token is StringExpandableToken))
            {
                foreach (Match fragment in QuotedGenericFragment.Matches(token.Text))
                {
                    var start = token.Extent.StartOffset + fragment.Index;
                    Blank(chars, start, start + fragment.Length);
                }
            }
        }
        return new string(chars);
    }

    private static void Blank(char[] chars, int start, int end)
    {
        start = Math.Max(0, start);
        end = Math.Min(end, chars.Length);
        for (var index = start; index < end; index++) chars[index] = '\u0001';
    }

    private static bool ShapeNearError(
        IEnumerable<ParseError> parseErrors,
        string scanText,
        string errorId,
        string pattern)
    {
        foreach (var parseError in parseErrors)
        {
            if (!parseError.ErrorId.Equals(errorId, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (Match match in Regex.Matches(
                         scanText,
                         pattern,
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (match.Index >= parseError.Extent.StartOffset) return true;
                if (match.Index <= parseError.Extent.EndOffset &&
                    parseError.Extent.StartOffset <= match.Index + match.Length)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool KeywordCommandNearError(
        IEnumerable<ParseError> parseErrors,
        IReadOnlyList<CommandAst> parseCommands,
        IReadOnlyList<LocalDefinition> localDefinitions,
        TrustedCommandSnapshot commands,
        string errorId,
        IReadOnlyList<string> keywords)
    {
        foreach (var parseError in parseErrors)
        {
            if (!parseError.ErrorId.Equals(errorId, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var command in parseCommands)
            {
                if (command.CommandElements.Count == 0 ||
                    command.CommandElements[0] is not StringConstantExpressionAst first ||
                    first.StringConstantType != StringConstantType.BareWord ||
                    !keywords.Contains(first.Value, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                var locallyDefined = localDefinitions.Any(definition =>
                    definition.Name.Equals(first.Value, StringComparison.OrdinalIgnoreCase) &&
                    definition.End <= command.Extent.StartOffset);
                if (locallyDefined || commands.Resolve(first.Value, CommandTypes.All) is not null)
                    continue;

                var start = command.Extent.StartOffset;
                if (start >= parseError.Extent.StartOffset) return true;
                if (start <= parseError.Extent.EndOffset &&
                    parseError.Extent.StartOffset <= command.Extent.EndOffset)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsLocallyDefined(
        IEnumerable<LocalDefinition> localDefinitions,
        string name,
        CommandAst command,
        bool includeContainingDefinition)
    {
        foreach (var definition in localDefinitions)
        {
            if (!definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (definition.End <= command.Extent.StartOffset) return true;
            if (includeContainingDefinition &&
                definition.Start <= command.Extent.StartOffset &&
                command.Extent.EndOffset <= definition.End)
            {
                return true;
            }
        }
        return false;
    }
}
