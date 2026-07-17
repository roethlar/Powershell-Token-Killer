using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using PtkMcpServer.Audit;
using PtkMcpServer.Audit.OtlpWire;

namespace PtkMcpServer.Tests;

public sealed class AuditV3CompatibilityTests
{
    private static readonly Guid ProducerHostId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid SupervisorBootId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid LiveHostBootId =
        Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly AuditProducerContext Producer = new(
        ProducerHostId,
        SupervisorBootId,
        WorkerBootId: null,
        Pid: 4242,
        Version: "0.2.0-contract-vector",
        BinaryDigest: new string('a', 64));

    [Fact]
    public void Explicit_v3_serializer_matches_both_frozen_r0_vectors_byte_for_byte()
    {
        var nullHost = AuditEventSerializer.SerializeVersion3(
            1,
            previousEventHash: null,
            Producer,
            new AuditHostSnapshot(null, null, "stopped", 0),
            NullHostInput(),
            Guid.Parse("019bd2f0-1000-7000-8000-000000000001"),
            Timestamp(56, 1_234_567),
            Timestamp(56, 1_234_567));

        var liveHost = AuditEventSerializer.SerializeVersion3(
            2,
            new string('b', 64),
            Producer,
            new AuditHostSnapshot(
                LiveHostBootId,
                long.MaxValue,
                "ready",
                long.MaxValue),
            LiveHostInput(),
            Guid.Parse("019bd2f0-1001-7000-8000-000000000002"),
            Timestamp(57, 1_234_567),
            Timestamp(57, 7_654_321));

        Assert.Equal(ReadVector("audit-v3-null.jsonl"), nullHost.Utf8Line.ToArray());
        Assert.Equal(ReadVector("audit-v3-host.jsonl"), liveHost.Utf8Line.ToArray());
    }

    [Fact]
    public void Existing_serializer_remains_v2_and_v3_cannot_be_requested_without_a_snapshot()
    {
        Assert.Equal("ptk.audit/2", AuditEventSerializer.CurrentSchemaVersion);
        var v2 = AuditEventSerializer.Serialize(
            1,
            null,
            Producer,
            NullHostInput(),
            Guid.Parse("019bd2f0-1000-7000-8000-000000000001"),
            Timestamp(56, 1_234_567),
            Timestamp(56, 1_234_567));

        using var document = JsonDocument.Parse(v2.Utf8Line[..^1]);
        Assert.Equal("ptk.audit/2", document.RootElement.GetProperty("schema_version").GetString());
        Assert.False(document.RootElement.TryGetProperty("host", out _));
        Assert.Throws<ArgumentNullException>(() => AuditEventSerializer.SerializeVersion3(
            1,
            null,
            Producer,
            null!,
            NullHostInput(),
            Guid.Parse("019bd2f0-1000-7000-8000-000000000001"),
            Timestamp(56, 1_234_567),
            Timestamp(56, 1_234_567)));
    }

    public static IEnumerable<object[]> InvalidHostSnapshots()
    {
        yield return [new AuditHostSnapshot(null, null, "ready", 0)];
        yield return [new AuditHostSnapshot(LiveHostBootId, 1, "stopped", 0)];
        yield return [new AuditHostSnapshot(LiveHostBootId, null, "starting", 0)];
        yield return [new AuditHostSnapshot(null, 1, "starting", 0)];
        yield return [new AuditHostSnapshot(LiveHostBootId, 0, "starting", 0)];
        yield return [new AuditHostSnapshot(LiveHostBootId, 1, "unknown", 0)];
        yield return [new AuditHostSnapshot(LiveHostBootId, 1, "starting", -1)];
        yield return [new AuditHostSnapshot(
            Guid.Parse("019bd2f0-1000-7000-8000-000000000001"),
            1,
            "starting",
            0)];
    }

    [Theory]
    [MemberData(nameof(InvalidHostSnapshots))]
    public void Explicit_v3_serializer_rejects_invalid_host_identity_state_pairs(
        object value)
    {
        var host = Assert.IsType<AuditHostSnapshot>(value);
        Assert.Throws<AuditEventValidationException>(() =>
            AuditEventSerializer.SerializeVersion3(
                1,
                null,
                Producer,
                host,
                NullHostInput(),
                Guid.Parse("019bd2f0-1000-7000-8000-000000000001"),
                Timestamp(56, 1_234_567),
                Timestamp(56, 1_234_567)));
    }

    [Fact]
    public void V3_readers_accept_frozen_vectors_and_otlp_adds_only_typed_host_attributes()
    {
        var nullLine = ReadVector("audit-v3-null.jsonl");
        var liveLine = ReadVector("audit-v3-host.jsonl");
        AuditEvidenceSpoolScanner.ValidateExactEnvelopeShapeForTests(nullLine.AsMemory()[..^1]);
        AuditEvidenceSpoolScanner.ValidateExactEnvelopeShapeForTests(liveLine.AsMemory()[..^1]);

        var nullMapped = AuditOtlpRecordMapper.Map(nullLine);
        var nullAttributes = Attributes(nullMapped);
        Assert.Equal("ptk.audit/3", nullAttributes["ptk.audit.schema_version"].StringValue);
        Assert.DoesNotContain("ptk.host.boot_id", nullAttributes.Keys);
        Assert.DoesNotContain("ptk.host.generation", nullAttributes.Keys);
        Assert.Equal("stopped", nullAttributes["ptk.host.state"].StringValue);
        Assert.Equal(0, nullAttributes["ptk.host.recovery_attempt"].IntValue);

        var liveMapped = AuditOtlpRecordMapper.Map(liveLine);
        var liveAttributes = Attributes(liveMapped);
        Assert.Equal(LiveHostBootId.ToString("D"), liveAttributes["ptk.host.boot_id"].StringValue);
        Assert.Equal(long.MaxValue, liveAttributes["ptk.host.generation"].IntValue);
        Assert.Equal("ready", liveAttributes["ptk.host.state"].StringValue);
        Assert.Equal(long.MaxValue, liveAttributes["ptk.host.recovery_attempt"].IntValue);
        Assert.Equal(liveLine.AsSpan()[..^1], liveMapped.ExactJsonBody.Span);
    }

    [Fact]
    public void V3_readers_reject_missing_extra_or_semantically_invalid_host_snapshots()
    {
        var live = Encoding.UTF8.GetString(ReadVector("audit-v3-host.jsonl"));
        var missing = Encoding.UTF8.GetBytes(live.Replace(
            ",\"host\":{\"boot_id\":\"33333333-3333-4333-8333-333333333333\",\"generation\":9223372036854775807,\"state\":\"ready\",\"recovery_attempt\":9223372036854775807}",
            string.Empty,
            StringComparison.Ordinal));
        var extra = Encoding.UTF8.GetBytes(live.Replace(
            "\"recovery_attempt\":9223372036854775807",
            "\"recovery_attempt\":9223372036854775807,\"extra\":null",
            StringComparison.Ordinal));
        var invalidPair = Encoding.UTF8.GetBytes(live.Replace(
            "\"boot_id\":\"33333333-3333-4333-8333-333333333333\",\"generation\":9223372036854775807",
            "\"boot_id\":null,\"generation\":null",
            StringComparison.Ordinal));

        foreach (var malformed in new[] { missing, extra, invalidPair })
        {
            Assert.Throws<IOException>(() =>
                AuditEvidenceSpoolScanner.ValidateExactEnvelopeShapeForTests(
                    malformed.AsMemory()[..^1]));
            Assert.Throws<AuditOtlpMappingException>(() => AuditOtlpRecordMapper.Map(malformed));
        }
    }

    private static Dictionary<string, AnyValue> Attributes(AuditOtlpRecord mapped) =>
        ExportLogsServiceRequest.Parser.ParseFrom(mapped.RequestBytes.Span)
            .ResourceLogs[0]
            .ScopeLogs[0]
            .LogRecords[0]
            .Attributes
            .ToDictionary(value => value.Key, value => value.Value);

    private static AuditEventInput NullHostInput() => new()
    {
        EventType = "host.recovery_failed",
        Session = new AuditSession(),
        Actor = new AuditActor
        {
            Transport = "mcp_stdio",
            ClientName = "contract-vector",
            ClientVersion = "1",
            AttributionStrength = "client_asserted",
        },
        Correlation = new AuditCorrelation(),
        Request = new AuditRequest(),
        Routing = new AuditRouting(),
        Outcome = new AuditOutcome
        {
            State = "failed",
            DetailCode = "host_start_failed",
            DurationMs = 0,
            TerminationCertainty = "not_applicable",
        },
        Coverage = new AuditCoverage
        {
            PtkRequest = false,
            RootProcessObserved = "none",
            DescendantsObserved = "none",
            RemoteEffectObserved = "not_applicable",
        },
        Audit = new AuditEventHealth
        {
            ProtectionMode = "local-only",
            HealthState = "healthy",
        },
    };

    private static AuditEventInput LiveHostInput() => new()
    {
        EventType = "host.ready",
        Session = new AuditSession
        {
            Name = "default",
            Generation = long.MaxValue,
            BindingKind = "default",
            AllowColdBackground = true,
        },
        Actor = new AuditActor
        {
            Transport = "mcp_stdio",
            ClientName = "contract-vector-λ",
            ClientVersion = "1",
            AttributionStrength = "client_asserted",
        },
        Correlation = new AuditCorrelation(),
        Request = new AuditRequest { SessionRequested = "default" },
        Routing = new AuditRouting(),
        Outcome = new AuditOutcome
        {
            State = "completed",
            DetailCode = "host_ready",
            DurationMs = 1,
            WarmStateLost = true,
            TerminationCertainty = "confirmed",
        },
        Coverage = new AuditCoverage
        {
            PtkRequest = false,
            RootProcessObserved = "complete",
            DescendantsObserved = "complete",
            RemoteEffectObserved = "not_applicable",
        },
        Audit = new AuditEventHealth
        {
            ProtectionMode = "anchored",
            ExportConfigurationIdentity = new string('c', 64),
            HealthState = "healthy",
        },
    };

    private static DateTimeOffset Timestamp(int second, long ticks) =>
        new DateTimeOffset(2026, 7, 15, 12, 34, second, TimeSpan.Zero).AddTicks(ticks);

    private static byte[] ReadVector(
        string fileName,
        [CallerFilePath] string sourcePath = "") =>
        File.ReadAllBytes(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            "..",
            "Contracts",
            "ResilienceR0",
            fileName)));
}
