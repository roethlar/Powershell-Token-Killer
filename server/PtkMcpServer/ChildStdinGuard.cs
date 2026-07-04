using System.Runtime.InteropServices;

namespace PtkMcpServer;

/// <summary>
/// Points the process's standard-input HANDLE at the null device after the MCP
/// transport has captured the real stdin stream. Without this, native commands
/// run in the warm runspace inherit the live-but-idle JSON-RPC stdin pipe and
/// any child that reads or waits on stdin (git's MSYS runtime, sort, ssh)
/// blocks until the whole session ends. With it, children read instant EOF.
/// The transport is unaffected: its stream wraps the original handle, captured
/// before the swap.
/// </summary>
internal static class ChildStdinGuard
{
    private const int StdInputHandle = -10;

    // Holds the NUL handle for the process lifetime. A local would be
    // finalized by the GC, closing the handle installed via SetStdHandle and
    // leaving later children a stale stdin handle.
    private static Microsoft.Win32.SafeHandles.SafeFileHandle? _nulHandle;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int nStdHandle, nint handle);

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);

    public static void DetachChildStdin()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _nulHandle = File.OpenHandle("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                SetStdHandle(StdInputHandle, _nulHandle.DangerousGetHandle());
            }
            else
            {
                var devNull = open("/dev/null", 0 /* O_RDONLY */);
                if (devNull >= 0) dup2(devNull, 0);
            }
        }
        catch
        {
            // Best effort: without the guard the server still works for
            // everything except natives that read stdin.
        }
    }
}
