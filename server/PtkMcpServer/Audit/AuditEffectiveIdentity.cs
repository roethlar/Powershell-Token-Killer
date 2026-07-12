using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace PtkMcpServer.Audit;

/// <summary>
/// Captures the effective OS principal that executes out-of-band audit
/// administration. This identifies the token/UID boundary only; it does not
/// claim that PTK authenticated the originating human.
/// </summary>
internal static class AuditEffectiveIdentity
{
    internal static string Capture() => OperatingSystem.IsWindows()
        ? CaptureWindowsSid()
        : "unix-euid:" + GetEffectiveUserId().ToString(CultureInfo.InvariantCulture);

    [SupportedOSPlatform("windows")]
    private static string CaptureWindowsSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User
            ?? throw new IOException("The effective Windows user SID is unavailable.");
        return "windows-sid:" + sid.Value;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
