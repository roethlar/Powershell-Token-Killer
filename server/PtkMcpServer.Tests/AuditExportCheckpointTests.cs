using System.Text;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditExportCheckpointTests
{
    private static readonly Guid BootId =
        Guid.ParseExact("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "D");
    private static readonly Guid OtherBootId =
        Guid.ParseExact("11111111-2222-4333-8444-555555555555", "D");
    private static readonly Guid AcknowledgedEventId =
        Guid.ParseExact("01949f6a-3c00-7abc-8def-0123456789ab", "D");
    private static readonly Guid BlockedEventId =
        Guid.ParseExact("01949f6a-3c01-7abc-9def-0123456789ab", "D");
    private static readonly DateTimeOffset FirstFailureUtc =
        new DateTimeOffset(2026, 7, 11, 12, 34, 56, TimeSpan.Zero).AddTicks(1_234_567);
    private const string ResponseDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ConfigurationIdentity =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string SegmentZeroName =
        "ptk-audit-aaaaaaaabbbb4ccc8dddeeeeeeeeeeee-00000000.jsonl";
    private const string ExactInitialJson =
        "{\"schema_version\":\"ptk.export-checkpoint/1\",\"supervisor_boot_id\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\",\"chain_complete\":false,\"spool_file\":null,\"byte_offset\":0,\"sequence\":0,\"acknowledged_event_id\":null,\"blocked_record\":null}\n";
    private const string ExactBlockedJson =
        "{\"schema_version\":\"ptk.export-checkpoint/1\",\"supervisor_boot_id\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\",\"chain_complete\":false,\"spool_file\":\"ptk-audit-aaaaaaaabbbb4ccc8dddeeeeeeeeeeee-00000000.jsonl\",\"byte_offset\":128,\"sequence\":1,\"acknowledged_event_id\":\"01949f6a-3c00-7abc-8def-0123456789ab\",\"blocked_record\":{\"spool_file\":\"ptk-audit-aaaaaaaabbbb4ccc8dddeeeeeeeeeeee-00000000.jsonl\",\"byte_offset\":128,\"sequence\":2,\"event_id\":\"01949f6a-3c01-7abc-9def-0123456789ab\",\"failure_class\":\"configuration\",\"detail_code\":\"http.401\",\"response_digest\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"first_failure_utc\":\"2026-07-11T12:34:56.1234567Z\",\"export_configuration_identity\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}}\n";

    [Fact]
    public void Initial_checkpoint_has_one_exact_canonical_byte_vector()
    {
        var bytes = AuditExportCheckpointCodec.Serialize(AuditExportCheckpoint.Initial(BootId));

        Assert.Equal(Encoding.UTF8.GetBytes(ExactInitialJson), bytes);
        var parsed = AuditExportCheckpointCodec.Parse(bytes);
        Assert.Equal(BootId, parsed.SupervisorBootId);
        Assert.False(parsed.ChainComplete);
        Assert.Null(parsed.Spool);
        Assert.Equal(0, parsed.ByteOffset);
        Assert.Equal(0, parsed.Sequence);
        Assert.Null(parsed.AcknowledgedEventId);
        Assert.Null(parsed.BlockedRecord);
    }

    [Fact]
    public void Blocked_checkpoint_has_one_exact_canonical_byte_vector()
    {
        var bytes = AuditExportCheckpointCodec.Serialize(BlockedCheckpoint());

        Assert.Equal(Encoding.UTF8.GetBytes(ExactBlockedJson), bytes);
        var parsed = AuditExportCheckpointCodec.Parse(bytes);
        Assert.Equal(SegmentZeroName, parsed.Spool?.FileName);
        Assert.Equal(128, parsed.ByteOffset);
        Assert.Equal(1, parsed.Sequence);
        Assert.Equal(AcknowledgedEventId, parsed.AcknowledgedEventId);
        var blocked = Assert.IsType<AuditExportBlockedRecord>(parsed.BlockedRecord);
        Assert.Equal(SegmentZeroName, blocked.Spool.FileName);
        Assert.Equal(2, blocked.Sequence);
        Assert.Equal(BlockedEventId, blocked.EventId);
        Assert.Equal(AuditExportFailureClass.Configuration, blocked.FailureClass);
        Assert.Equal("http.401", blocked.DetailCode);
        Assert.Equal(ResponseDigest, blocked.ResponseDigest);
        Assert.Equal(FirstFailureUtc, blocked.FirstFailureUtc);
        Assert.Equal(ConfigurationIdentity, blocked.ExportConfigurationIdentity);
    }

    public static TheoryData<string, string> NonCanonicalJsonCases => new()
    {
        {
            "missing LF",
            ExactBlockedJson[..^1]
        },
        {
            "extra LF",
            ExactBlockedJson + "\n"
        },
        {
            "noncanonical whitespace",
            ExactBlockedJson.Replace("\"chain_complete\":false", "\"chain_complete\": false", StringComparison.Ordinal)
        },
        {
            "unknown property",
            ExactBlockedJson.Replace("{\"schema_version\"", "{\"extra\":null,\"schema_version\"", StringComparison.Ordinal)
        },
        {
            "duplicate property",
            ExactBlockedJson.Replace("{\"schema_version\"", "{\"schema_version\":\"ptk.export-checkpoint/1\",\"schema_version\"", StringComparison.Ordinal)
        },
        {
            "uppercase boot UUID",
            ExactBlockedJson.Replace("aaaaaaaa-bbbb", "AAAAAAAA-bbbb", StringComparison.Ordinal)
        },
        {
            "wrong numeric kind",
            ExactBlockedJson.Replace("\"sequence\":1", "\"sequence\":\"1\"", StringComparison.Ordinal)
        },
        {
            "noncanonical timestamp",
            ExactBlockedJson.Replace("12:34:56.1234567Z", "12:34:56Z", StringComparison.Ordinal)
        },
        {
            "uppercase digest",
            ExactBlockedJson.Replace(ResponseDigest, ResponseDigest.ToUpperInvariant(), StringComparison.Ordinal)
        },
        {
            "unknown failure class",
            ExactBlockedJson.Replace("\"configuration\"", "\"retry\"", StringComparison.Ordinal)
        },
        {
            "detail-code final newline",
            ExactBlockedJson.Replace("http.401", "http.401\\n", StringComparison.Ordinal)
        },
        {
            "response-digest final newline",
            ExactBlockedJson.Replace(ResponseDigest, ResponseDigest + "\\n", StringComparison.Ordinal)
        },
        {
            "configuration-identity final newline",
            ExactBlockedJson.Replace(ConfigurationIdentity, ConfigurationIdentity + "\\n", StringComparison.Ordinal)
        },
        {
            "trailing comma",
            ExactBlockedJson.Replace("}}\n", "},}\n", StringComparison.Ordinal)
        },
    };

    [Theory]
    [MemberData(nameof(NonCanonicalJsonCases))]
    public void Parser_rejects_noncanonical_or_unknown_state(string _, string json)
    {
        Assert.Throws<IOException>(() =>
            AuditExportCheckpointCodec.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Parser_rejects_utf8_bom_invalid_utf8_and_oversized_state()
    {
        var valid = Encoding.UTF8.GetBytes(ExactInitialJson);
        var bom = new byte[valid.Length + 3];
        bom[0] = 0xef;
        bom[1] = 0xbb;
        bom[2] = 0xbf;
        valid.CopyTo(bom, 3);
        Assert.Throws<IOException>(() => AuditExportCheckpointCodec.Parse(bom));

        var invalidUtf8 = valid.ToArray();
        invalidUtf8[1] = 0xff;
        Assert.Throws<IOException>(() => AuditExportCheckpointCodec.Parse(invalidUtf8));

        var oversized = new byte[AuditExportCheckpointCodec.MaximumBytes + 1];
        oversized[^1] = (byte)'\n';
        Assert.Throws<IOException>(() => AuditExportCheckpointCodec.Parse(oversized));
    }

    [Fact]
    public void Cursor_fields_are_all_absent_at_zero_and_all_present_after_acknowledgment()
    {
        var segment = AuditSpoolSegmentIdentity.Create(BootId, 0);
        Assert.Throws<ArgumentException>(() =>
            new AuditExportCheckpoint(BootId, false, segment, 0, 0, null, null));
        Assert.Throws<ArgumentException>(() =>
            new AuditExportCheckpoint(
                BootId,
                false,
                null,
                128,
                1,
                AcknowledgedEventId,
                null));
        Assert.Throws<ArgumentException>(() =>
            new AuditExportCheckpoint(
                BootId,
                false,
                segment,
                128,
                1,
                null,
                null));
        Assert.Throws<ArgumentException>(() =>
            new AuditExportCheckpoint(
                BootId,
                false,
                AuditSpoolSegmentIdentity.Create(OtherBootId, 0),
                128,
                1,
                AcknowledgedEventId,
                null));
    }

    [Fact]
    public void Blocked_record_is_exactly_the_next_cursor_position_in_the_same_boot()
    {
        var segment = AuditSpoolSegmentIdentity.Create(BootId, 0);
        var wrongSequence = Blocked(segment, byteOffset: 128, sequence: 3);
        Assert.Throws<ArgumentException>(() =>
            AcknowledgedCheckpoint(segment, blocked: wrongSequence));

        var wrongOffset = Blocked(segment, byteOffset: 129, sequence: 2);
        Assert.Throws<ArgumentException>(() =>
            AcknowledgedCheckpoint(segment, blocked: wrongOffset));

        var nextSegmentWrongOffset = Blocked(
            AuditSpoolSegmentIdentity.Create(BootId, 1),
            byteOffset: 1,
            sequence: 2);
        Assert.Throws<ArgumentException>(() =>
            AcknowledgedCheckpoint(segment, blocked: nextSegmentWrongOffset));

        var otherBoot = Blocked(
            AuditSpoolSegmentIdentity.Create(OtherBootId, 0),
            byteOffset: 128,
            sequence: 2);
        Assert.Throws<ArgumentException>(() =>
            AcknowledgedCheckpoint(segment, blocked: otherBoot));

        Assert.Throws<ArgumentException>(() =>
            new AuditExportCheckpoint(BootId, true, segment, 128, 1, AcknowledgedEventId, Blocked(segment)));
    }

    [Fact]
    public void Initial_block_must_be_the_first_record_of_segment_zero()
    {
        Assert.Throws<ArgumentException>(() =>
            new AuditExportCheckpoint(
                BootId,
                false,
                null,
                0,
                0,
                null,
                Blocked(AuditSpoolSegmentIdentity.Create(BootId, 1), byteOffset: 0, sequence: 1)));
        Assert.Throws<ArgumentException>(() =>
            new AuditExportCheckpoint(
                BootId,
                false,
                null,
                0,
                0,
                null,
                Blocked(AuditSpoolSegmentIdentity.Create(BootId, 0), byteOffset: 1, sequence: 1)));

        var valid = new AuditExportCheckpoint(
            BootId,
            false,
            null,
            0,
            0,
            null,
            Blocked(AuditSpoolSegmentIdentity.Create(BootId, 0), byteOffset: 0, sequence: 1));
        Assert.NotNull(valid.BlockedRecord);
    }

    [Fact]
    public void Complete_empty_chain_round_trips_as_complete()
    {
        var checkpoint = new AuditExportCheckpoint(BootId, true, null, 0, 0, null, null);

        var parsed = AuditExportCheckpointCodec.Parse(
            AuditExportCheckpointCodec.Serialize(checkpoint));

        Assert.True(parsed.ChainComplete);
        Assert.Equal(0, parsed.Sequence);
    }

    [Fact]
    public void Next_segment_block_at_offset_zero_is_valid()
    {
        var checkpoint = AcknowledgedCheckpoint(
            AuditSpoolSegmentIdentity.Create(BootId, 0),
            Blocked(
                AuditSpoolSegmentIdentity.Create(BootId, 1),
                byteOffset: 0,
                sequence: 2));

        var parsed = AuditExportCheckpointCodec.Parse(
            AuditExportCheckpointCodec.Serialize(checkpoint));

        Assert.Equal(1, parsed.BlockedRecord?.Spool.Index);
        Assert.Equal(0, parsed.BlockedRecord?.ByteOffset);
    }

    [Fact]
    public void Configuration_block_without_a_response_digest_round_trips()
    {
        var segment = AuditSpoolSegmentIdentity.Create(BootId, 0);
        var blocked = new AuditExportBlockedRecord(
            segment,
            0,
            1,
            BlockedEventId,
            AuditExportFailureClass.Configuration,
            "tls.validation",
            null,
            FirstFailureUtc,
            ConfigurationIdentity);
        var checkpoint = new AuditExportCheckpoint(
            BootId,
            false,
            null,
            0,
            0,
            null,
            blocked);

        var parsed = AuditExportCheckpointCodec.Parse(
            AuditExportCheckpointCodec.Serialize(checkpoint));

        Assert.Null(parsed.BlockedRecord?.ResponseDigest);
    }

    [Fact]
    public void Block_metadata_rejects_open_values_and_noncanonical_identifiers()
    {
        var segment = AuditSpoolSegmentIdentity.Create(BootId, 0);
        Assert.Throws<ArgumentException>(() =>
            new AuditExportBlockedRecord(
                segment,
                0,
                1,
                Guid.NewGuid(),
                AuditExportFailureClass.Configuration,
                "http.401",
                null,
                FirstFailureUtc,
                ConfigurationIdentity));
        Assert.Throws<ArgumentException>(() =>
            new AuditExportBlockedRecord(
                segment,
                0,
                1,
                BlockedEventId,
                AuditExportFailureClass.Configuration,
                "http.401\n",
                null,
                FirstFailureUtc,
                ConfigurationIdentity));
        Assert.Throws<ArgumentException>(() =>
            new AuditExportBlockedRecord(
                segment,
                0,
                1,
                BlockedEventId,
                AuditExportFailureClass.Configuration,
                "http.401",
                ResponseDigest + "\n",
                FirstFailureUtc,
                ConfigurationIdentity));
        Assert.Throws<ArgumentException>(() =>
            new AuditExportBlockedRecord(
                segment,
                0,
                1,
                BlockedEventId,
                AuditExportFailureClass.Configuration,
                "http.401",
                null,
                FirstFailureUtc,
                ConfigurationIdentity + "\n"));
        Assert.Throws<ArgumentException>(() =>
            new AuditExportBlockedRecord(
                segment,
                0,
                1,
                BlockedEventId,
                (AuditExportFailureClass)999,
                "http.401",
                null,
                FirstFailureUtc,
                ConfigurationIdentity));
        Assert.Throws<ArgumentException>(() =>
            new AuditExportBlockedRecord(
                segment,
                0,
                1,
                BlockedEventId,
                AuditExportFailureClass.Configuration,
                "HTTP 401",
                null,
                FirstFailureUtc,
                ConfigurationIdentity));
        Assert.Throws<ArgumentException>(() =>
            new AuditExportBlockedRecord(
                segment,
                0,
                1,
                BlockedEventId,
                AuditExportFailureClass.Configuration,
                "http.401",
                ResponseDigest.ToUpperInvariant(),
                FirstFailureUtc,
                ConfigurationIdentity));
        Assert.Throws<ArgumentException>(() =>
            new AuditExportBlockedRecord(
                segment,
                0,
                1,
                BlockedEventId,
                AuditExportFailureClass.Configuration,
                "http.401",
                null,
                FirstFailureUtc.ToOffset(TimeSpan.FromHours(1)),
                ConfigurationIdentity));
    }

    [Theory]
    [InlineData((int)AuditExportFailureClass.Configuration, "configuration")]
    [InlineData((int)AuditExportFailureClass.PartialRejection, "partial_rejection")]
    [InlineData((int)AuditExportFailureClass.Data, "data")]
    [InlineData((int)AuditExportFailureClass.Protocol, "protocol")]
    public void Failure_classes_have_closed_stable_wire_names(
        int failureClassValue,
        string wireName)
    {
        var failureClass = (AuditExportFailureClass)failureClassValue;
        var checkpoint = new AuditExportCheckpoint(
            BootId,
            false,
            null,
            0,
            0,
            null,
            Blocked(
                AuditSpoolSegmentIdentity.Create(BootId, 0),
                byteOffset: 0,
                sequence: 1,
                failureClass));

        var text = Encoding.UTF8.GetString(AuditExportCheckpointCodec.Serialize(checkpoint));
        Assert.Contains($"\"failure_class\":\"{wireName}\"", text, StringComparison.Ordinal);
        Assert.Equal(
            failureClass,
            AuditExportCheckpointCodec.Parse(Encoding.UTF8.GetBytes(text))
                .BlockedRecord?.FailureClass);
    }

    private static AuditExportCheckpoint BlockedCheckpoint()
    {
        var segment = AuditSpoolSegmentIdentity.Create(BootId, 0);
        return AcknowledgedCheckpoint(segment, Blocked(segment));
    }

    private static AuditExportCheckpoint AcknowledgedCheckpoint(
        AuditSpoolSegmentIdentity segment,
        AuditExportBlockedRecord? blocked)
    {
        return new AuditExportCheckpoint(
            BootId,
            false,
            segment,
            128,
            1,
            AcknowledgedEventId,
            blocked);
    }

    private static AuditExportBlockedRecord Blocked(
        AuditSpoolSegmentIdentity segment,
        long byteOffset = 128,
        long sequence = 2,
        AuditExportFailureClass failureClass = AuditExportFailureClass.Configuration)
    {
        return new AuditExportBlockedRecord(
            segment,
            byteOffset,
            sequence,
            BlockedEventId,
            failureClass,
            "http.401",
            ResponseDigest,
            FirstFailureUtc,
            ConfigurationIdentity);
    }
}
