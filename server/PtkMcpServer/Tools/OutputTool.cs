using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
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
        [Range(typeof(long), "0", "9223372036854775807")]
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
        ArgumentNullException.ThrowIfNull(reader);
        cancellationToken.ThrowIfCancellationRequested();
        action = action?.ToLowerInvariant() ?? "read";
        var audit = auditContext?.Current;

        switch (action)
        {
            case "status":
            {
                var status = reader.Status(handle);
                var response = FormatStatus(status);
                audit?.CommitReadOutcome(
                    "output.status_accessed",
                    status.State.ToMachineCode(),
                    response,
                    detailCode: status.DetailCode);
                return response;
            }
            case "search":
            {
                if (pattern is null)
                    return "[ptk output] invalid request: action=search requires pattern.";
                OutputSearchResult result;
                try
                {
                    result = reader.Search(handle, pattern, offset, maxBytes);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return "[ptk output] invalid request: offset, maxBytes, or pattern is outside the bounded contract.";
                }
                var response = FormatSearch(result);
                audit?.CommitReadOutcome(
                    "output.search_accessed",
                    result.State.ToMachineCode(),
                    response,
                    detailCode: result.DetailCode,
                    nextOffset: result.NextOffset);
                return response;
            }
            case "read":
            {
                OutputReadResult result;
                try
                {
                    result = reader.Read(handle, offset, maxBytes);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return "[ptk output] invalid request: offset or maxBytes is outside the bounded contract.";
                }
                var response = FormatRead(result);
                audit?.CommitReadOutcome(
                    "output.read_accessed",
                    result.State.ToMachineCode(),
                    response,
                    detailCode: result.DetailCode,
                    nextOffset: result.NextOffset,
                    bytesReturnedOverride: result.BytesRead);
                return response;
            }
            default:
                return "[ptk output] unknown action - use read | search | status.";
        }
    }

    private static string FormatStatus(OutputArtifactStatus status)
    {
        var sb = new StringBuilder("[ptk output] action=status");
        AppendCommon(
            sb,
            status.State,
            status.Complete,
            status.Provenance,
            status.Bytes,
            status.DetailCode);
        if (status.ExpiresUtc is { } expires)
            sb.Append(" expires_utc=").Append(expires.ToString("O", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string FormatRead(OutputReadResult result)
    {
        var sb = new StringBuilder("[ptk output] action=read");
        AppendCommon(
            sb,
            result.State,
            result.Complete,
            result.Provenance,
            result.TotalBytes,
            result.DetailCode);
        sb.Append(" offset=").Append(result.Offset.ToString(CultureInfo.InvariantCulture));
        sb.Append(" next_offset=").Append(result.NextOffset.ToString(CultureInfo.InvariantCulture));
        sb.Append(" bytes_returned=").Append(result.BytesRead.ToString(CultureInfo.InvariantCulture));
        if (result.Text.Length > 0)
            sb.AppendLine().Append(result.Text);
        else if (result.State is OutputArtifactState.Available or OutputArtifactState.Incomplete)
            sb.AppendLine().Append("(no captured bytes)");
        return sb.ToString();
    }

    private static string FormatSearch(OutputSearchResult result)
    {
        var sb = new StringBuilder("[ptk output] action=search");
        AppendCommon(
            sb,
            result.State,
            result.Complete,
            result.Provenance,
            result.TotalBytes,
            result.DetailCode);
        sb.Append(" offset=").Append(result.Offset.ToString(CultureInfo.InvariantCulture));
        sb.Append(" next_offset=").Append(result.NextOffset.ToString(CultureInfo.InvariantCulture));
        sb.Append(" bytes_scanned=").Append(result.BytesScanned.ToString(CultureInfo.InvariantCulture));
        sb.Append(" matches=").Append(result.Matches.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var match in result.Matches)
        {
            sb.AppendLine();
            sb.Append("offset=").Append(match.Offset.ToString(CultureInfo.InvariantCulture));
            sb.Append(": ").Append(match.Preview);
        }
        return sb.ToString();
    }

    private static void AppendCommon(
        StringBuilder sb,
        OutputArtifactState state,
        bool complete,
        OutputProvenance? provenance,
        long bytes,
        string? detailCode)
    {
        sb.Append(" state=").Append(state.ToMachineCode());
        sb.Append(" complete=").Append(complete ? "true" : "false");
        sb.Append(" bytes=").Append(bytes.ToString(CultureInfo.InvariantCulture));
        if (provenance is { } value)
            sb.Append(" provenance=").Append(value.ToMachineCode());
        if (detailCode is not null)
            sb.Append(" detail=").Append(detailCode);
    }
}
