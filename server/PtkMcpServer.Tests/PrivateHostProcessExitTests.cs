using System.Text;
using PtkMcpServer.GuardianHost;

namespace PtkMcpServer.Tests;

public sealed class PrivateHostProcessExitTests
{
    [Fact]
    public void Bootstrap_diagnostic_is_allow_listed_ascii_and_bounded()
    {
        using var diagnostic = new MemoryStream();

        var exitCode = PrivateHostProcessExit.CompleteBootstrapFailure(
            "handle_missing",
            () => diagnostic);

        Assert.Equal(PrivateHostProcessExit.BootstrapFailureExitCode, exitCode);
        var bytes = diagnostic.ToArray();
        Assert.True(bytes.Length <= PrivateHostProcessExit.MaximumDiagnosticBytes);
        Assert.All(bytes, value => Assert.InRange(value, (byte)0, (byte)0x7f));
        Assert.Equal(
            "ptk_host_exit kind=bootstrap_failure detail=handle_missing\n",
            Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Unknown_bootstrap_detail_cannot_leak_into_the_diagnostic()
    {
        const string planted = "secret=C:\\operator\\private.txt";
        using var diagnostic = new MemoryStream();

        var exitCode = PrivateHostProcessExit.CompleteBootstrapFailure(
            planted,
            () => diagnostic);

        Assert.Equal(PrivateHostProcessExit.BootstrapFailureExitCode, exitCode);
        var text = Encoding.ASCII.GetString(diagnostic.ToArray());
        Assert.Equal(
            "ptk_host_exit kind=bootstrap_failure detail=bootstrap_failure\n",
            text);
        Assert.DoesNotContain(planted, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostic_failure_preserves_the_selected_exit_without_retry()
    {
        var attempts = 0;

        var exitCode = PrivateHostProcessExit.CompleteRuntimeFailure(() =>
        {
            attempts++;
            throw new IOException("stderr unavailable");
        });

        Assert.Equal(PrivateHostProcessExit.RuntimeFailureExitCode, exitCode);
        Assert.Equal(1, attempts);
    }
}
