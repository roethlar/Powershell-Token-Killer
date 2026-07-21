using PtkMcpGuardian.Output;
using PtkMcpServer;

namespace PtkMcpGuardian.Tests;

public sealed class GuardianOutputResponseDecoratorTests
{
    private const string Handle =
        "ptko_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void Available_handle_replaces_the_exact_preseal_elision_marker()
    {
        var text = "head\n[... lines elided - " +
            GuardianOutputResponseDecorator.GenericUnavailableMarker +
            "]\ntail";

        var decorated = GuardianOutputResponseDecorator.Decorate(
            text,
            Available());

        Assert.Equal(
            "head\n[... lines elided - recovery=available: ptk_output handle=" +
            Handle + "]\ntail",
            decorated);
        Assert.DoesNotContain(
            GuardianOutputResponseDecorator.GenericUnavailableMarker,
            decorated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Available_handle_is_appended_when_shaping_did_not_elide()
    {
        var decorated = GuardianOutputResponseDecorator.Decorate(
            "ordinary output",
            Available());

        Assert.Equal(
            "ordinary output" + Environment.NewLine +
            "recovery=available: ptk_output handle=" + Handle,
            decorated);
    }

    [Fact]
    public void Incomplete_artifact_still_advertises_its_readable_handle()
    {
        var decorated = GuardianOutputResponseDecorator.Decorate(
            "prefix",
            new OutputRecoverySummary(
                Handle,
                OutputArtifactState.Incomplete,
                6,
                "host_generation_lost",
                Advertise: true));

        Assert.EndsWith(
            "recovery=available: ptk_output handle=" + Handle,
            decorated,
            StringComparison.Ordinal);
        Assert.DoesNotContain("host_generation_lost", decorated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GuardianOutputResponseDecorator.GenericUnavailableMarker)]
    [InlineData(GuardianOutputResponseDecorator.RtkUnavailableMarker)]
    public void Existing_truthful_unavailable_marker_is_not_duplicated(string marker)
    {
        var text = "output" + Environment.NewLine + marker;

        var decorated = GuardianOutputResponseDecorator.Decorate(
            text,
            Unavailable());

        Assert.Same(text, decorated);
    }

    [Fact]
    public void Unavailable_capture_is_appended_when_host_has_no_marker()
    {
        var decorated = GuardianOutputResponseDecorator.Decorate(
            "ordinary output",
            Unavailable());

        Assert.Equal(
            "ordinary output" + Environment.NewLine +
            GuardianOutputResponseDecorator.GenericUnavailableMarker,
            decorated);
    }

    [Fact]
    public void Nonadvertised_host_summary_does_not_change_text()
    {
        const string text = "unchanged";

        var decorated = GuardianOutputResponseDecorator.Decorate(
            text,
            Available() with { Advertise = false });

        Assert.Same(text, decorated);
    }

    [Fact]
    public void Invalid_handle_never_enters_model_facing_text()
    {
        var decorated = GuardianOutputResponseDecorator.Decorate(
            "output",
            Available() with { Handle = "ptko_bad\nforged" });

        Assert.DoesNotContain("forged", decorated, StringComparison.Ordinal);
        Assert.EndsWith(
            GuardianOutputResponseDecorator.GenericUnavailableMarker,
            decorated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_host_text_becomes_one_recovery_line_without_a_leading_blank()
    {
        Assert.Equal(
            "recovery=available: ptk_output handle=" + Handle,
            GuardianOutputResponseDecorator.Decorate(string.Empty, Available()));
    }

    private static OutputRecoverySummary Available() => new(
        Handle,
        OutputArtifactState.Available,
        42,
        DetailCode: null,
        Advertise: true);

    private static OutputRecoverySummary Unavailable() =>
        OutputRecoverySummary.Unavailable(
            "output_store_unavailable",
            advertise: true);
}
