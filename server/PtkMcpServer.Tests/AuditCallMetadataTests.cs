using System.Text.Json;
using ModelContextProtocol.Protocol;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditCallMetadataTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 34, 56, TimeSpan.Zero);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromHours(1);

    [Fact]
    public void Invoke_capture_preserves_field_presence_defaults_and_exact_script_separately()
    {
        var call = Call(
            "ptk_invoke",
            ("raw", false),
            ("script", "Get-Process"),
            ("background", false));
        var client = new AuditClientContext("Claude Code", "2.1.207", "stdio-7");

        Assert.True(Capture(call, client, out var metadata, out var script, out var failure));

        Assert.Null(failure);
        Assert.Equal("Get-Process", script);
        Assert.Equal("mcp_stdio", metadata!.Actor.Transport);
        Assert.Equal("Claude Code", metadata.Actor.ClientName);
        Assert.Equal("2.1.207", metadata.Actor.ClientVersion);
        Assert.Equal("stdio-7", metadata.Actor.ClientSessionId);
        Assert.Equal("client_asserted", metadata.Actor.AttributionStrength);
        Assert.Equal("ptk_invoke", metadata.Request.Tool);
        Assert.Equal("invoke", metadata.Request.Action);
        Assert.Equal(["background", "raw", "script"], metadata.Request.ProvidedFields);
        Assert.Equal("default", metadata.Request.SessionRequested);
        Assert.Equal("auto", metadata.Request.Route);
        Assert.False(metadata.Request.Background);
        Assert.False(metadata.Request.Raw);
        Assert.Equal(300_000, metadata.Request.TimeoutMs);
        Assert.Equal(Now.AddMinutes(5), metadata.Request.DeadlineUtc);
        Assert.DoesNotContain("Get-Process", metadata.ToString(), StringComparison.Ordinal);
        Assert.True(metadata.OperationProfile.RequiresScriptEvidence);
        Assert.True(metadata.OperationProfile.MayHaveSideEffects);
        Assert.Equal(9, metadata.OperationProfile.MaximumCallRecordSlots);
        Assert.Equal(0, metadata.OperationProfile.PersistentJobTerminalSlots);
    }

    [Fact]
    public void Invoke_route_and_timeout_normalization_match_current_tool_behavior()
    {
        var forced = Call(
            "ptk_invoke",
            ("script", "'ok'"),
            ("route", "PWSH"),
            ("timeoutSeconds", 7_200));
        Assert.True(Capture(forced, new(), out var metadata, out _, out _));
        Assert.Equal("pwsh", metadata!.Request.Route);
        Assert.Equal(3_600_000, metadata.Request.TimeoutMs);
        Assert.Equal(Now.AddHours(1), metadata.Request.DeadlineUtc);

        var unknownRoute = Call("ptk_invoke", ("script", "'ok'"), ("route", "future-route"));
        Assert.True(Capture(unknownRoute, new(), out metadata, out _, out _));
        Assert.Equal("auto", metadata!.Request.Route);

        var negativeOverride = Call("ptk_invoke", ("script", "'ok'"), ("timeoutSeconds", -10));
        Assert.True(Capture(negativeOverride, new(), out metadata, out _, out _));
        Assert.Equal(300_000, metadata!.Request.TimeoutMs);
    }

    [Fact]
    public void Background_profile_carries_a_separate_persistent_job_terminal_slot()
    {
        var call = Call("ptk_invoke", ("script", "dotnet test"), ("background", true));

        Assert.True(Capture(call, new(), out var metadata, out _, out _));

        var profile = metadata!.OperationProfile;
        Assert.Equal(9, profile.MaximumCallRecordSlots);
        Assert.Equal(1, profile.PersistentJobTerminalSlots);
        Assert.Equal(10, profile.MaximumRecordSlots);
        Assert.Equal(10L * 65_536, profile.MaximumReservationBytes(65_536));
    }

    [Fact]
    public void Job_capture_uses_normalized_action_positive_int64_id_and_effective_output_offset()
    {
        const long jobId = 5_000_000_000;
        var call = Call("ptk_job", ("id", jobId), ("action", "OUTPUT"));

        Assert.True(Capture(call, new(), out var metadata, out var script, out _));

        Assert.Null(script);
        Assert.Equal("output", metadata!.Request.Action);
        Assert.Equal(jobId, metadata.Request.JobId);
        Assert.Equal(0, metadata.Request.Offset);
        Assert.Equal(["action", "id"], metadata.Request.ProvidedFields);
        Assert.True(metadata.OperationProfile.MayHaveSideEffects);
        Assert.Equal(5, metadata.OperationProfile.MaximumCallRecordSlots);

        Assert.True(Capture(Call("ptk_job", ("action", "list")), new(), out metadata, out _, out _));
        Assert.Null(metadata!.Request.JobId);
        Assert.Null(metadata.Request.Offset);
        Assert.Equal(3, metadata.OperationProfile.MaximumCallRecordSlots);

        Assert.True(Capture(
            Call("ptk_job", ("action", "list"), ("id", 42L)),
            new(),
            out metadata,
            out _,
            out _));
        Assert.Null(metadata!.Request.JobId);
        Assert.Contains("id", metadata.Request.ProvidedFields);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("output")]
    [InlineData("kill")]
    public void Job_specific_actions_require_an_identifier(string action)
    {
        AssertRejected(
            Call("ptk_job", ("action", action)),
            "id is required for this action");
    }

    [Fact]
    public void State_and_reset_capture_effective_current_defaults()
    {
        Assert.True(Capture(Call("ptk_state"), new(), out var state, out _, out _));
        Assert.Equal("state", state!.Request.Action);
        Assert.False(state.Request.ListAvailable);
        Assert.Empty(state.Request.ProvidedFields);
        Assert.Equal("transport_only", state.Actor.AttributionStrength);
        Assert.True(state.OperationProfile.MayHaveSideEffects);
        Assert.Equal(5, state.OperationProfile.MaximumCallRecordSlots);

        Assert.True(Capture(Call("ptk_reset"), new(), out var reset, out _, out _));
        Assert.Equal("reset", reset!.Request.Action);
        Assert.True(reset.OperationProfile.MayHaveSideEffects);
        Assert.Equal(4, reset.OperationProfile.MaximumCallRecordSlots);
    }

    [Fact]
    public void Unknown_tools_fields_and_actions_fail_closed_without_partial_metadata()
    {
        AssertRejected(Call("ptk_future"), "unknown tool");
        AssertRejected(Call("ptk_state", ("futureField", "secret-value")), "unknown argument field", "secret-value");
        AssertRejected(Call("ptk_reset", ("force", true)), "unknown argument field");

        Assert.True(Capture(
            Call("ptk_job", ("action", "future-action")),
            new(),
            out var unknownAction,
            out _,
            out _));
        Assert.Equal("future-action", unknownAction!.Request.Action);
        Assert.Equal(2, unknownAction.OperationProfile.MaximumCallRecordSlots);
        Assert.False(unknownAction.OperationProfile.MayHaveSideEffects);
    }

    [Fact]
    public void Wrong_json_kinds_ranges_and_missing_required_values_fail_before_acceptance()
    {
        AssertRejected(Call("ptk_invoke", ("script", 42)), "wrong JSON kind");
        AssertRejected(Call("ptk_invoke", ("script", "'ok'"), ("raw", "true")), "wrong JSON kind");
        AssertRejected(Call("ptk_invoke"), "required argument script is missing");
        AssertRejected(Call("ptk_job", ("action", "status"), ("id", 0)), "positive int64");
        AssertRejected(Call("ptk_job", ("action", "output"), ("offset", -1)), "nonnegative int64");
        AssertRejected(Call("ptk_state", ("listAvailable", 1)), "wrong JSON kind");

        Assert.True(Capture(
            Call("ptk_invoke", ("script", "'ok'"), ("route", null)),
            new(),
            out var nullRoute,
            out _,
            out _));
        Assert.Equal("auto", nullRoute!.Request.Route);
        Assert.Contains("route", nullRoute.Request.ProvidedFields);

        Assert.True(Capture(
            Call("ptk_job", ("action", null)),
            new(),
            out var nullAction,
            out _,
            out _));
        Assert.Null(nullAction!.Request.Action);
    }

    [Fact]
    public void Oversize_script_is_never_truncated_or_copied_into_failure()
    {
        var secret = new string('x', 131_073);
        var call = Call("ptk_invoke", ("script", secret));

        Assert.False(Capture(call, new(), out var metadata, out var script, out var failure));

        Assert.Null(metadata);
        Assert.Null(script);
        Assert.Contains("script is not representable", failure);
        Assert.DoesNotContain(secret, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_assertions_are_bounded_and_never_mislabeled_authenticated()
    {
        Assert.True(Capture(
            Call("ptk_state"),
            new AuditClientContext(ClientName: "codex"),
            out var metadata,
            out _,
            out _));
        Assert.Equal("client_asserted", metadata!.Actor.AttributionStrength);
        Assert.NotEqual("authenticated", metadata.Actor.AttributionStrength);

        var secret = "token=" + new string('s', 300);
        Assert.False(Capture(
            Call("ptk_state"),
            new AuditClientContext(ClientName: secret),
            out metadata,
            out _,
            out var failure));
        Assert.Null(metadata);
        Assert.DoesNotContain(secret, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Nonintegral_or_non_utc_boundary_time_is_rejected_not_rounded()
    {
        Assert.False(AuditCallMetadataCapture.TryCapture(
            Call("ptk_state"),
            new(),
            TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond + 1),
            MaximumTimeout,
            Now,
            out _,
            out _,
            out var fractionalFailure));
        Assert.Contains("default timeout is not representable", fractionalFailure);

        Assert.False(AuditCallMetadataCapture.TryCapture(
            Call("ptk_state"),
            new(),
            DefaultTimeout,
            MaximumTimeout,
            Now.ToOffset(TimeSpan.FromHours(1)),
            out _,
            out _,
            out var clockFailure));
        Assert.Contains("clock is not UTC", clockFailure);
    }

    private static bool Capture(
        CallToolRequestParams call,
        AuditClientContext client,
        out AuditCallMetadata? metadata,
        out string? script,
        out string? failure) =>
        AuditCallMetadataCapture.TryCapture(
            call,
            client,
            DefaultTimeout,
            MaximumTimeout,
            Now,
            out metadata,
            out script,
            out failure);

    private static void AssertRejected(
        CallToolRequestParams call,
        string expectedFailure,
        string? forbiddenValue = null)
    {
        Assert.False(Capture(call, new(), out var metadata, out var script, out var failure));
        Assert.Null(metadata);
        Assert.Null(script);
        Assert.Contains(expectedFailure, failure);
        if (forbiddenValue is not null)
            Assert.DoesNotContain(forbiddenValue, failure, StringComparison.Ordinal);
    }

    private static CallToolRequestParams Call(string name, params (string Name, object? Value)[] arguments)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (argumentName, value) in arguments)
            values.Add(argumentName, JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object)));

        return new CallToolRequestParams
        {
            Name = name,
            Arguments = values,
        };
    }
}
