using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Server;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tools;

[McpServerToolType]
public static class OutputTool
{
    [McpServerTool(Name = "ptk_output")]
    [Description(
        "Read an immutable output snapshot captured from a completed ptk_invoke or " +
        "finalized direct background job without executing or rerunning anything. " +
        "action=read returns a bounded UTF-8 byte chunk and " +
        "next offset; action=search performs a bounded ordinal literal search; " +
        "action=status reports availability, completeness, provenance, size, and " +
        "expiry. Handles are harness-local and may be expired, evicted, incomplete, " +
        "or unavailable. This tool accepts no script and never starts a session or worker.")]
    public static string Output(
        OutputStore store,
        [Description("Opaque ptk_output handle returned by ptk_invoke or ptk_job.")]
        [Required, MaxLength(256)]
        string handle,
        [Description("read | search | status")]
        [AllowedValues("read", "search", "status")]
        string action = "read",
        [Description(
            "UTF-8 byte offset for read/search. Start at 0 and reuse next_offset from the prior result.")]
        // Numeric Range operands only: the (Type, string, string) constructor makes the
        // generated JSON schema emit "minimum"/"maximum" as strings, which violates
        // JSON Schema draft 2020-12 and is rejected by strict MCP clients. The maximum
        // is 2^53-1 (largest exactly-representable integral double); real offsets are
        // bounded far below it by OutputStore size caps.
        [Range(0d, 9007199254740991d)]
        long offset = 0,
        [Description(
            "Bound for one read/search request, from 1 through 65536 UTF-8 bytes.")]
        [Range(1, OutputStore.MaximumReadBytes)]
        int maxBytes = OutputStore.DefaultReadBytes,
        [Description(
            "Bounded ordinal literal required only for action=search; raw pattern text is not written to audit.")]
        [MaxLength(OutputStore.MaximumPatternBytes)]
        string? pattern = null,
        CancellationToken cancellationToken = default,
        AuditCallContextAccessor? auditContext = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        return OutputCore(
            store,
            handle,
            action,
            offset,
            maxBytes,
            pattern,
            cancellationToken,
            auditContext);
    }

    internal static string OutputCore(
        IOutputArtifactReader reader,
        string handle,
        string action,
        long offset,
        int maxBytes,
        string? pattern,
        CancellationToken cancellationToken,
        AuditCallContextAccessor? auditContext)
    {
        var result = OutputToolRuntime.Execute(
            reader,
            handle,
            action,
            offset,
            maxBytes,
            pattern,
            cancellationToken);
        var audit = auditContext?.Current;
        if (result.AuditOutcome is { } outcome)
        {
            audit?.CommitReadOutcome(
                outcome.EventType,
                outcome.State,
                result.Text,
                detailCode: outcome.DetailCode,
                nextOffset: outcome.NextOffset,
                bytesReturnedOverride: outcome.BytesReturnedOverride);
        }
        return result.Text;
    }
}
