using System.Globalization;
using System.Text;

namespace PtkMcpServer;

/// <summary>
/// Exact audit facts produced by one guardian-local immutable output access.
/// The raw handle and search pattern remain outside this result.
/// </summary>
internal sealed record OutputAccessAuditOutcome(
    string EventType,
    string State,
    string? DetailCode,
    long? NextOffset,
    long? BytesReturnedOverride);

internal sealed record OutputToolRuntimeResult(
    string Text,
    OutputAccessAuditOutcome? AuditOutcome);

/// <summary>
/// Shared pure adapter over the guardian-owned output store. The transitional
/// server and the standalone guardian use this one formatter so public output
/// bytes do not drift during R4-R6.
/// </summary>
internal static class OutputToolRuntime
{
    internal static OutputToolRuntimeResult Execute(
        IOutputArtifactReader reader,
        string handle,
        string action,
        long offset,
        int maximumBytes,
        string? pattern,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        cancellationToken.ThrowIfCancellationRequested();
        action = action?.ToLowerInvariant() ?? "read";

        switch (action)
        {
            case "status":
            {
                var status = reader.Status(handle);
                return Audited(
                    FormatStatus(status),
                    "output.status_accessed",
                    status.State,
                    status.DetailCode);
            }
            case "search":
            {
                if (pattern is null)
                {
                    return Unadmitted(
                        "[ptk output] invalid request: action=search requires pattern.");
                }
                OutputSearchResult result;
                try
                {
                    result = reader.Search(handle, pattern, offset, maximumBytes);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return Unadmitted(
                        "[ptk output] invalid request: offset, maxBytes, or pattern is outside the bounded contract.");
                }
                return Audited(
                    FormatSearch(result),
                    "output.search_accessed",
                    result.State,
                    result.DetailCode,
                    result.NextOffset);
            }
            case "read":
            {
                OutputReadResult result;
                try
                {
                    result = reader.Read(handle, offset, maximumBytes);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return Unadmitted(
                        "[ptk output] invalid request: offset or maxBytes is outside the bounded contract.");
                }
                return Audited(
                    FormatRead(result),
                    "output.read_accessed",
                    result.State,
                    result.DetailCode,
                    result.NextOffset,
                    result.BytesRead);
            }
            default:
                return Unadmitted(
                    "[ptk output] unknown action - use read | search | status.");
        }
    }

    private static OutputToolRuntimeResult Audited(
        string text,
        string eventType,
        OutputArtifactState state,
        string? detailCode,
        long? nextOffset = null,
        long? bytesReturnedOverride = null) => new(
            text,
            new OutputAccessAuditOutcome(
                eventType,
                state.ToMachineCode(),
                detailCode,
                nextOffset,
                bytesReturnedOverride));

    private static OutputToolRuntimeResult Unadmitted(string text) => new(text, null);

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
