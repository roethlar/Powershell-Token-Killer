namespace PtkMcpServer;

internal enum OutputProvenance
{
    PowerShellObjects,
    DirectText,
    RtkUnknown,
    RtkFiltered,
    RtkPassthrough,
}

internal static class OutputProvenanceMachineCodes
{
    internal static string ToMachineCode(this OutputProvenance value) => value switch
    {
        OutputProvenance.PowerShellObjects => "powershell_objects",
        OutputProvenance.DirectText => "direct_text",
        OutputProvenance.RtkUnknown => "rtk_unknown",
        OutputProvenance.RtkFiltered => "rtk_filtered",
        OutputProvenance.RtkPassthrough => "rtk_passthrough",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };
}
