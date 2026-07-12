using System.Text;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditEvidenceSpoolScannerTests
{
    [Fact]
    public void Exact_envelope_shape_accepts_null_and_complete_disposition_objects()
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
