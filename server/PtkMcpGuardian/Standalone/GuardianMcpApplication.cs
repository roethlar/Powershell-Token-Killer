using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PtkSharedContracts;

namespace PtkMcpGuardian.Standalone;

internal interface IGuardianToolDispatcher
{
    ValueTask<GuardianToolResult> DispatchAsync(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken);
}

internal sealed record GuardianToolResult
{
    internal GuardianToolResult(string text, bool isError)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        IsError = isError;
    }

    internal string Text { get; }
    internal bool IsError { get; }
}

/// <summary>
/// Owns one immutable public MCP contract and one public stream transport.
/// Replaceable private-host state is deliberately hidden behind the dispatcher.
/// </summary>
internal sealed class GuardianMcpApplication
{
    internal const string UnknownToolText =
        "The requested tool is not in the frozen PTK contract.";

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyArguments =
        new ReadOnlyDictionary<string, JsonElement>(
            new Dictionary<string, JsonElement>(StringComparer.Ordinal));

    private readonly IGuardianToolDispatcher _dispatcher;
    private readonly PublicToolContractSnapshot _contract;
    private readonly IReadOnlySet<string> _toolNames;
    private int _runStarted;

    internal GuardianMcpApplication(IGuardianToolDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _contract = PublicToolContractResource.Parse();
        _toolNames = _contract.Tools
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    internal async Task RunAsync(
        Stream publicInput,
        Stream publicOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publicInput);
        ArgumentNullException.ThrowIfNull(publicOutput);
        if (!publicInput.CanRead)
            throw new ArgumentException("The public input stream must be readable.", nameof(publicInput));
        if (!publicOutput.CanWrite)
            throw new ArgumentException("The public output stream must be writable.", nameof(publicOutput));
        if (Interlocked.CompareExchange(ref _runStarted, 1, 0) != 0)
            throw new InvalidOperationException("A guardian MCP application can run only once.");

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        // stdout is the public JSON-RPC transport. Remove every inherited
        // provider before installing the one stderr-only logging path.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
            options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMcpServer(options =>
            {
                options.ScopeRequests = true;
                options.ServerInfo = new Implementation
                {
                    Name = _contract.ServerIdentity.Name,
                    Version = _contract.ServerIdentity.Version,
                };
                options.ServerInstructions = _contract.Instructions;
            })
            .WithStreamServerTransport(publicInput, publicOutput)
            .WithListToolsHandler(HandleListToolsAsync)
            .WithCallToolHandler(HandleCallToolAsync);

        using var host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<ListToolsResult> HandleListToolsAsync(
        RequestContext<ListToolsRequestParams> context,
        CancellationToken cancellationToken)
    {
        _ = context;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ListToolsResult
        {
            Tools = _contract.Tools.Select(CreateTool).ToArray(),
        });
    }

    private async ValueTask<CallToolResult> HandleCallToolAsync(
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Params ??
            throw new InvalidDataException("The MCP tool request has no parameters.");
        if (string.IsNullOrEmpty(request.Name) || !_toolNames.Contains(request.Name))
            return TextResult(UnknownToolText, isError: true);

        var arguments = request.Arguments is null
            ? EmptyArguments
            : new ReadOnlyDictionary<string, JsonElement>(request.Arguments);
        var result = await _dispatcher.DispatchAsync(
                request.Name,
                arguments,
                cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
            throw new InvalidOperationException("The guardian tool dispatcher returned null.");
        return TextResult(result.Text, result.IsError);
    }

    private static Tool CreateTool(PublicToolDefinition definition) => new()
    {
        Name = definition.Name,
        Description = definition.Description,
        InputSchema = definition.InputSchema.Clone(),
    };

    private static CallToolResult TextResult(string text, bool isError) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError,
    };
}
