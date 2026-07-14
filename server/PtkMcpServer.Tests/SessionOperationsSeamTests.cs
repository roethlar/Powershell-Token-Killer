using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;
using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

public sealed class SessionOperationsSeamTests
{
    [Fact]
    public async Task Tool_adapters_delegate_only_through_the_session_operations_seam()
    {
        var operations = new RecordingSessionOperations();
        var audit = new AuditCallContextAccessor();
        using var cancellation = new CancellationTokenSource();
        using var outputStore = new OutputStore(new OutputStoreOptions(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ptk",
                "session-operations-tests",
                Guid.NewGuid().ToString("N")),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1),
            MaximumArtifactBytes: 1024,
            MaximumSessionBytes: 2048,
            MaximumAggregateBytes: 4096));

        Assert.Equal(
            "invoke",
            await InvokeTool.Invoke(
                operations,
                "Get-Item .",
                cancellation.Token,
                raw: true,
                route: "rtk",
                background: true,
                timeoutSeconds: 17,
                auditContext: audit,
                outputStore: outputStore));
        Assert.Equal(
            ["Get-Item .", cancellation.Token, true, "rtk", true, 17, audit, outputStore],
            operations.LastArguments);

        Assert.Equal(
            "job",
            await JobTool.Job(
                operations,
                "status",
                cancellation.Token,
                id: 41,
                offset: 9,
                auditContext: audit));
        Assert.Equal(
            ["status", cancellation.Token, 41L, 9L, audit],
            operations.LastArguments);

        Assert.Equal(
            "state",
            await StateTool.State(
                operations,
                listAvailable: true,
                cancellationToken: cancellation.Token,
                auditContext: audit));
        Assert.Equal(
            [true, cancellation.Token, audit],
            operations.LastArguments);

        Assert.Equal(
            "reset",
            await ResetTool.Reset(
                operations,
                cancellationToken: cancellation.Token,
                auditContext: audit));
        Assert.Equal(
            [cancellation.Token, audit],
            operations.LastArguments);
    }

    private sealed class RecordingSessionOperations : ISessionOperations
    {
        internal object?[] LastArguments { get; private set; } = [];

        public Task<string> InvokeAsync(
            string script,
            CancellationToken cancellationToken,
            bool raw,
            string route,
            bool background,
            int timeoutSeconds,
            AuditCallContextAccessor? auditContext,
            OutputStore? outputStore)
        {
            LastArguments =
                [script, cancellationToken, raw, route, background, timeoutSeconds, auditContext, outputStore];
            return Task.FromResult("invoke");
        }

        public Task<string> JobAsync(
            string action,
            CancellationToken cancellationToken,
            long id,
            long offset,
            AuditCallContextAccessor? auditContext)
        {
            LastArguments = [action, cancellationToken, id, offset, auditContext];
            return Task.FromResult("job");
        }

        public Task<string> StateAsync(
            bool listAvailable,
            CancellationToken cancellationToken,
            AuditCallContextAccessor? auditContext)
        {
            LastArguments = [listAvailable, cancellationToken, auditContext];
            return Task.FromResult("state");
        }

        public Task<string> ResetAsync(
            CancellationToken cancellationToken,
            AuditCallContextAccessor? auditContext)
        {
            LastArguments = [cancellationToken, auditContext];
            return Task.FromResult("reset");
        }
    }
}
