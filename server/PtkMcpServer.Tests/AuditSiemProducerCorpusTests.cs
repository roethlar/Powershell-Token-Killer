using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditSiemProducerCorpusTests
{
    private static readonly string[] RecordNames =
        ["audit-v1", "audit-v2", "audit-v3-null", "audit-v3-host"];

    [Fact]
    public void Tracked_corpus_is_the_exact_current_v1_v2_and_frozen_v3_producer_wire()
    {
        var v2 = AuditOtlpTestRecord.Create().Utf8Line.ToArray();
        var expectedJsonl = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["audit-v1"] = AuditCoreSchemaTestRecords.ToLegacyV1(v2),
            ["audit-v2"] = v2,
            ["audit-v3-null"] = ReadR0("audit-v3-null.jsonl"),
            ["audit-v3-host"] = ReadR0("audit-v3-host.jsonl"),
        };

        using var manifest = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(CorpusRoot(), "corpus.json")),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
        Assert.Equal(
            ["schema_version", "encoding", "records"],
            manifest.RootElement.EnumerateObject().Select(value => value.Name));
        Assert.Equal(
            "ptk.siem-producer-corpus/1",
            manifest.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal(
            "canonical_base64_then_lf",
            manifest.RootElement.GetProperty("encoding").GetString());

        var entries = manifest.RootElement.GetProperty("records").EnumerateArray().ToArray();
        Assert.Equal(RecordNames, entries.Select(entry => entry.GetProperty("name").GetString()));
        for (var index = 0; index < RecordNames.Length; index++)
        {
            var name = RecordNames[index];
            var entry = entries[index];
            Assert.Equal(
                ["name", "jsonl_file", "jsonl_sha256", "otlp_file", "otlp_sha256", "authoritative_source"],
                entry.EnumerateObject().Select(value => value.Name));

            var jsonl = ReadCanonicalBase64File(entry.GetProperty("jsonl_file").GetString()!);
            var otlp = ReadCanonicalBase64File(entry.GetProperty("otlp_file").GetString()!);
            Assert.Equal(expectedJsonl[name], jsonl);
            Assert.Equal(AuditOtlpRecordMapper.Map(jsonl).RequestBytes.ToArray(), otlp);
            Assert.Equal(
                Sha256(jsonl),
                entry.GetProperty("jsonl_sha256").GetString());
            Assert.Equal(
                Sha256(otlp),
                entry.GetProperty("otlp_sha256").GetString());

            var source = entry.GetProperty("authoritative_source").GetString();
            Assert.Equal(
                name switch
                {
                    "audit-v1" => "AuditEventSerializer+legacy_v1_adapter",
                    "audit-v2" => "AuditEventSerializer",
                    _ => $"../ResilienceR0/{name}.jsonl",
                },
                source);
        }
    }

    private static byte[] ReadCanonicalBase64File(string fileName)
    {
        Assert.Equal(Path.GetFileName(fileName), fileName);
        var bytes = File.ReadAllBytes(Path.Combine(CorpusRoot(), fileName));
        Assert.NotEmpty(bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(1, bytes.Count(value => value == (byte)'\n'));
        var encoded = Encoding.ASCII.GetString(bytes.AsSpan()[..^1]);
        var decoded = Convert.FromBase64String(encoded);
        Assert.Equal(encoded, Convert.ToBase64String(decoded));
        return decoded;
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] ReadR0(
        string fileName,
        [CallerFilePath] string sourcePath = "") =>
        File.ReadAllBytes(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            "..",
            "Contracts",
            "ResilienceR0",
            fileName)));

    private static string CorpusRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            "..",
            "Contracts",
            "SiemConformance"));
}
