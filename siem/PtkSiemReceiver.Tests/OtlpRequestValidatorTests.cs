using Google.Protobuf;
using PtkMcpServer.Audit.OtlpWire;
using PtkSiemReceiver.Ingest;

namespace PtkSiemReceiver.Tests;

public sealed class OtlpRequestValidatorTests
{
    [Theory]
    [InlineData("ptk.audit/1")]
    [InlineData("ptk.audit/2")]
    [InlineData("ptk.audit/3")]
    public void Exact_supported_request_is_accepted(string schemaVersion)
    {
        var request = OtlpTestRequest.Create(schemaVersion);

        var result = OtlpRequestValidator.Validate(request.ToByteArray());

        Assert.True(result.IsValid);
        Assert.Null(result.FailureCode);
        Assert.Equal(schemaVersion, result.Record?.SchemaVersion);
        Assert.Equal(request.ToByteArray(), result.Record?.RawRequestBytes);
    }

    [Fact]
    public void Empty_or_malformed_protobuf_is_rejected()
    {
        Assert.Equal("protobuf", OtlpRequestValidator.Validate([]).FailureCode);
        Assert.Equal("protobuf", OtlpRequestValidator.Validate([0x0a, 0xff]).FailureCode);
    }

    [Fact]
    public void More_than_one_log_record_is_rejected()
    {
        var request = OtlpTestRequest.Create();
        request.ResourceLogs[0].ScopeLogs[0].LogRecords.Add(
            request.ResourceLogs[0].ScopeLogs[0].LogRecords[0].Clone());

        Assert.Equal("otlp_shape", Failure(request));
    }

    [Fact]
    public void Event_body_mutation_without_hash_recomputation_is_rejected()
    {
        var request = OtlpTestRequest.Create();
        var record = request.ResourceLogs[0].ScopeLogs[0].LogRecords[0];
        record.Body.StringValue = record.Body.StringValue.Replace(
            "tool.completed",
            "tool.rejected",
            StringComparison.Ordinal);

        Assert.Equal("event_hash", Failure(request));
    }

    [Fact]
    public void Projected_attribute_value_mismatch_is_rejected()
    {
        var request = OtlpTestRequest.Create();
        var attribute = request.ResourceLogs[0].ScopeLogs[0].LogRecords[0].Attributes
            .Single(candidate => candidate.Key == "ptk.audit.event_type");
        attribute.Value.StringValue = "tool.rejected";

        Assert.Equal("attributes", Failure(request));
    }

    [Fact]
    public void Projected_attribute_type_mismatch_is_rejected()
    {
        var request = OtlpTestRequest.Create();
        var attribute = request.ResourceLogs[0].ScopeLogs[0].LogRecords[0].Attributes
            .Single(candidate => candidate.Key == "ptk.audit.sequence");
        attribute.Value = new AnyValue { StringValue = "1" };

        Assert.Equal("attributes", Failure(request));
    }

    [Fact]
    public void Duplicate_or_unknown_projected_attribute_is_rejected()
    {
        var duplicate = OtlpTestRequest.Create();
        duplicate.ResourceLogs[0].ScopeLogs[0].LogRecords[0].Attributes.Add(
            duplicate.ResourceLogs[0].ScopeLogs[0].LogRecords[0].Attributes[0].Clone());
        Assert.Equal("attributes", Failure(duplicate));

        var unknown = OtlpTestRequest.Create();
        unknown.ResourceLogs[0].ScopeLogs[0].LogRecords[0].Attributes.Add(new KeyValue
        {
            Key = "ptk.unknown",
            Value = new AnyValue { StringValue = "value" },
        });
        Assert.Equal("attributes", Failure(unknown));
    }

    [Fact]
    public void V3_requires_both_typed_always_present_host_attributes()
    {
        var request = OtlpTestRequest.Create("ptk.audit/3");
        var record = request.ResourceLogs[0].ScopeLogs[0].LogRecords[0];
        record.Attributes.Remove(
            record.Attributes.Single(attribute => attribute.Key == "ptk.host.recovery_attempt"));

        Assert.Equal("attributes", Failure(request));
    }

    [Fact]
    public void Resource_and_scope_projection_mismatches_are_rejected()
    {
        var resource = OtlpTestRequest.Create();
        resource.ResourceLogs[0].Resource.Attributes[0].Value.StringValue = "not-ptk";
        Assert.Equal("resource", Failure(resource));

        var scope = OtlpTestRequest.Create();
        scope.ResourceLogs[0].ScopeLogs[0].Scope.Name = "other";
        Assert.Equal("scope", Failure(scope));
    }

    private static string? Failure(ExportLogsServiceRequest request) =>
        OtlpRequestValidator.Validate(request.ToByteArray()).FailureCode;
}
