using System.Text;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditEvidenceSpoolScannerTests
{
    [Fact]
    public void Exact_envelope_shape_accepts_v2_null_and_complete_disposition_objects()
    {
        var ordinary = AuditOtlpTestRecord.Create();
        AuditEvidenceSpoolScanner.ValidateExactEnvelopeShapeForTests(
            ordinary.Utf8Line[..^1]);

        var disposition = AuditOtlpTestRecord.Create(
            operatorDisposition: ResolvedDisposition());
        AuditEvidenceSpoolScanner.ValidateExactEnvelopeShapeForTests(
            disposition.Utf8Line[..^1]);
    }

    [Fact]
    public void Exact_envelope_shape_and_chain_codec_accept_original_v1()
    {
        var source = AuditOtlpTestRecord.Create();
        var legacy = AuditCoreSchemaTestRecords.ToLegacyV1(source.Utf8Line);

        AuditEvidenceSpoolScanner.ValidateExactEnvelopeShapeForTests(legacy[..^1]);
        var parsed = AuditSpoolRecordCodec.Parse(
            legacy.AsSpan(0, legacy.Length - 1),
            AuditOtlpTestRecord.SupervisorBootId);

        Assert.Equal(source.EventId, parsed.EventId);
        Assert.Equal(source.Sequence, parsed.Sequence);
    }

    [Fact]
    public void Chain_codec_preserves_hash_linkage_across_supported_schema_versions()
    {
        var firstV2 = AuditOtlpTestRecord.Create();
        var firstV1 = AuditCoreSchemaTestRecords.ToLegacyV1(firstV2.Utf8Line);
        var first = AuditSpoolRecordCodec.Parse(
            firstV1.AsSpan(0, firstV1.Length - 1),
            AuditOtlpTestRecord.SupervisorBootId);
        var secondV2 = AuditOtlpTestRecord.Create(
            sequence: 2,
            previousEventHash: first.EventHash);

        var second = AuditSpoolRecordCodec.Parse(
            secondV2.Utf8Line.Span[..^1],
            AuditOtlpTestRecord.SupervisorBootId);

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(first.EventHash, second.PreviousEventHash);
    }

    [Fact]
    public void Exact_envelope_shape_and_chain_codec_reject_version_shape_hybrids()
    {
        var source = AuditOtlpTestRecord.Create();
        var legacy = AuditCoreSchemaTestRecords.ToLegacyV1(source.Utf8Line);
        var expandedV1 = AuditCoreSchemaTestRecords.RelabelV2AsV1WithoutShrinking(
            source.Utf8Line);
        var incompleteV2 = AuditCoreSchemaTestRecords.RelabelV1AsV2WithoutExpanding(
            legacy);

        Assert.Throws<IOException>(() =>
            AuditEvidenceSpoolScanner.ValidateExactEnvelopeShapeForTests(expandedV1[..^1]));
        Assert.Throws<IOException>(() =>
            AuditEvidenceSpoolScanner.ValidateExactEnvelopeShapeForTests(incompleteV2[..^1]));
        Assert.Throws<IOException>(() => AuditSpoolRecordCodec.Parse(
            expandedV1.AsSpan(0, expandedV1.Length - 1),
            AuditOtlpTestRecord.SupervisorBootId));
        Assert.Throws<IOException>(() => AuditSpoolRecordCodec.Parse(
            incompleteV2.AsSpan(0, incompleteV2.Length - 1),
            AuditOtlpTestRecord.SupervisorBootId));
    }

    [Fact]
    public void Exact_envelope_shape_rejects_an_unknown_disposition_property()
    {
        var source = AuditOtlpTestRecord.Create(
            operatorDisposition: ResolvedDisposition());
        var text = Encoding.UTF8.GetString(source.Utf8Line.Span[..^1]);
        var malformed = Encoding.UTF8.GetBytes(text.Replace(
            "\"operator_disposition\":{",
            "\"operator_disposition\":{\"unexpected\":null,",
            StringComparison.Ordinal));

        Assert.Throws<IOException>(() =>
            AuditEvidenceSpoolScanner.ValidateExactEnvelopeShapeForTests(malformed));
    }

    private static AuditOperatorDispositionFacts ResolvedDisposition() => new()
    {
        DispositionId = Guid.Parse("62345678-1234-4abc-8def-0123456789ab"),
        TargetSupervisorBootId = AuditOtlpTestRecord.SupervisorBootId,
        TargetSpoolFile = AuditSpoolSegmentIdentity.Create(
            AuditOtlpTestRecord.SupervisorBootId,
            2).FileName,
        TargetStartOffset = 10,
        TargetNextOffset = 20,
        TargetSequence = 7,
        TargetEventId = AuditOtlpTestRecord.ParentEventId,
        FailureClass = "protocol",
        DetailCode = "otlp.bad_response",
        FirstFailureUtc = AuditOtlpTestRecord.Occurred,
        TargetExportConfigurationIdentity = AuditOtlpTestRecord.HashA,
        ProofKind = "verified_receipt",
        VerifiedReceiptDigest = AuditOtlpTestRecord.HashA,
    };
}
