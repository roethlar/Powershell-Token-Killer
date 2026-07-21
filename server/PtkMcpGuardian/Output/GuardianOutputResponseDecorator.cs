using PtkMcpServer;
using PtkSharedContracts;

namespace PtkMcpGuardian.Output;

/// <summary>
/// Reconciles private-host model-facing text with the guardian's authoritative
/// public handle result. Protocol v1 carries text and output events separately,
/// so an elision marker produced before the guardian seals the artifact must be
/// replaced, while an ordinary non-elided result receives one terminal line.
/// </summary>
internal static class GuardianOutputResponseDecorator
{
    internal const string GenericUnavailableMarker =
        "recovery=unavailable: output capture unavailable; command was not rerun";
    internal const string RtkUnavailableMarker =
        "recovery=unavailable: rtk capture unsupported";

    internal static string Decorate(
        string hostText,
        OutputRecoverySummary? recovery)
    {
        ArgumentNullException.ThrowIfNull(hostText);
        if (recovery is not { Advertise: true }) return hostText;

        if (recovery.Handle is { } handle &&
            (recovery.State is OutputArtifactState.Available or
                OutputArtifactState.Incomplete) &&
            IsGuardianHandle(handle))
        {
            var available = $"recovery=available: ptk_output handle={handle}";
            if (hostText.Contains(GenericUnavailableMarker, StringComparison.Ordinal))
            {
                return hostText.Replace(
                    GenericUnavailableMarker,
                    available,
                    StringComparison.Ordinal);
            }
            return Append(hostText, available);
        }

        if (hostText.Contains(GenericUnavailableMarker, StringComparison.Ordinal) ||
            hostText.Contains(RtkUnavailableMarker, StringComparison.Ordinal))
        {
            return hostText;
        }
        return Append(hostText, GenericUnavailableMarker);
    }

    private static string Append(string text, string marker)
    {
        if (text.Length == 0) return marker;
        return text.EndsWith('\n')
            ? text + marker
            : text + Environment.NewLine + marker;
    }

    private static bool IsGuardianHandle(string handle) =>
        handle.Length == 5 + ContractLimits.CapabilityTokenCharacters &&
        handle.StartsWith("ptko_", StringComparison.Ordinal) &&
        ContractValidation.IsCapabilityToken(handle[5..]);
}
