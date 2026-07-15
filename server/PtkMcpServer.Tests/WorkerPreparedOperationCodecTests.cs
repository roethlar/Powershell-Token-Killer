using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerPreparedOperationCodecTests
{
    private const string PlanIdText = "12345678-1234-4234-9234-123456789abc";
    private const string OtherPlanIdText = "87654321-4321-4321-8321-cba987654321";
    private const long DeadlineMilliseconds = 1_900_000_000_123;
    private const string Script = "x";
    private const string ScriptDigest =
        "2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881";
    private const string ValidPrepareJson =
        "{\"planId\":\"12345678-1234-4234-9234-123456789abc\"," +
        "\"generation\":7,\"deadlineUnixTimeMilliseconds\":1900000000123," +
        "\"scriptDigest\":\"2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881\"," +
        "\"operation\":\"invoke\",\"arguments\":{\"script\":\"x\",\"raw\":false,\"route\":\"auto\"}}";
    private const string ValidCorrelationJson =
        "{\"planId\":\"12345678-1234-4234-9234-123456789abc\"," +
        "\"scriptDigest\":\"2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881\"," +
        "\"generation\":7,\"deadlineUnixTimeMilliseconds\":1900000000123}";

    private static readonly Guid PlanId = Guid.ParseExact(PlanIdText, "D");
    private static readonly Guid OtherPlanId = Guid.ParseExact(OtherPlanIdText, "D");
    private static readonly DateTimeOffset DeadlineUtc =
        DateTimeOffset.FromUnixTimeMilliseconds(DeadlineMilliseconds);

    [Fact]
    public void All_payload_codecs_round_trip_exact_closed_shapes()
    {
        var prepare = ValidPrepare();
        var prepared = ValidPrepared();
        var commit = new WorkerCommitPayload(
            PlanId,
            ScriptDigest,
            7,
            DeadlineUtc);
        var abort = new WorkerAbortPayload(
            PlanId,
            ScriptDigest,
            7,
            DeadlineUtc);

        var encodedPrepare = WorkerPreparedOperationCodec.CreatePrepare(prepare);
        Assert.Equal(ValidPrepareJson, encodedPrepare.GetRawText());
        Assert.Equal(
            prepare,
            WorkerPreparedOperationCodec.ParsePrepare(encodedPrepare));

        var encodedPrepared =
            WorkerPreparedOperationCodec.CreatePreparedCorrelation(prepared);
        Assert.Equal(ValidCorrelationJson, encodedPrepared.GetRawText());
        Assert.Equal(
            prepared,
            WorkerPreparedOperationCodec.ParsePreparedCorrelation(encodedPrepared));

        var encodedCommit = WorkerPreparedOperationCodec.CreateCommit(commit);
        Assert.Equal(ValidCorrelationJson, encodedCommit.GetRawText());
        Assert.Equal(commit, WorkerPreparedOperationCodec.ParseCommit(encodedCommit));

        var encodedAbort = WorkerPreparedOperationCodec.CreateAbort(abort);
        Assert.Equal(ValidCorrelationJson, encodedAbort.GetRawText());
        Assert.Equal(abort, WorkerPreparedOperationCodec.ParseAbort(encodedAbort));

        Assert.NotEqual(typeof(WorkerCommitPayload), typeof(WorkerAbortPayload));
    }

    [Fact]
    public void Prepare_rejects_each_missing_field_and_all_noncanonical_shapes()
    {
        foreach (var field in new[]
        {
            "planId",
            "generation",
            "deadlineUnixTimeMilliseconds",
            "scriptDigest",
            "operation",
            "arguments",
        })
        {
            var root = JsonNode.Parse(ValidPrepareJson)!.AsObject();
            Assert.True(root.Remove(field));
            AssertDetail(
                "missing_prepared_field",
                () => WorkerPreparedOperationCodec.ParsePrepare(Json(root.ToJsonString())));
        }

        var cases = new (string Json, string DetailCode)[]
        {
            ("[]", "invalid_prepared_field"),
            (ValidPrepareJson.Insert(1, "\"extra\":true,"), "unknown_prepared_field"),
            (ValidPrepareJson.Insert(1, $"\"planId\":\"{PlanIdText}\","), "duplicate_field"),
            (ValidPrepareJson.Replace($"\"{PlanIdText}\"", "null", StringComparison.Ordinal),
                "invalid_prepared_field"),
            (ValidPrepareJson.Replace(PlanIdText, PlanIdText.ToUpperInvariant(), StringComparison.Ordinal),
                "invalid_prepared_field"),
            (ValidPrepareJson.Replace(PlanIdText, "12345678-1234-5234-9234-123456789abc", StringComparison.Ordinal),
                "invalid_prepared_field"),
            (ValidPrepareJson.Replace("\"generation\":7", "\"generation\":0", StringComparison.Ordinal),
                "invalid_prepared_field"),
            (ValidPrepareJson.Replace("\"generation\":7", "\"generation\":1.0", StringComparison.Ordinal),
                "invalid_prepared_field"),
            (ValidPrepareJson.Replace("\"generation\":7", "\"generation\":9223372036854775808", StringComparison.Ordinal),
                "invalid_prepared_field"),
            (ValidPrepareJson.Replace(DeadlineMilliseconds.ToString(), "0", StringComparison.Ordinal),
                "invalid_prepared_field"),
            (ValidPrepareJson.Replace(DeadlineMilliseconds.ToString(), "1.0", StringComparison.Ordinal),
                "invalid_prepared_field"),
            (ValidPrepareJson.Replace(DeadlineMilliseconds.ToString(), long.MaxValue.ToString(), StringComparison.Ordinal),
                "invalid_prepared_field"),
            (ValidPrepareJson.Replace(ScriptDigest, ScriptDigest.ToUpperInvariant(), StringComparison.Ordinal),
                "invalid_prepared_field"),
            (ValidPrepareJson.Replace(ScriptDigest, "0", StringComparison.Ordinal),
                "invalid_prepared_field"),
            (ValidPrepareJson.Replace(ScriptDigest, new string('g', 64), StringComparison.Ordinal),
                "invalid_prepared_field"),
            (ValidPrepareJson.Replace("\"operation\":\"invoke\"", "\"operation\":null", StringComparison.Ordinal),
                "invalid_prepared_field"),
            (ValidPrepareJson.Replace("\"operation\":\"invoke\"", "\"operation\":\"state\"", StringComparison.Ordinal),
                "unsupported_prepared_operation"),
            (ValidPrepareJson.Replace("\"operation\":\"invoke\"", "\"operation\":\"Invoke\"", StringComparison.Ordinal),
                "unsupported_prepared_operation"),
            (ValidPrepareJson.Replace(
                "{\"script\":\"x\",\"raw\":false,\"route\":\"auto\"}",
                "[]",
                StringComparison.Ordinal), "invalid_prepared_field"),
            (ValidPrepareJson.Replace(
                "{\"script\":\"x\",\"raw\":false,\"route\":\"auto\"}",
                "{\"script\":\"x\",\"route\":\"auto\"}",
                StringComparison.Ordinal), "invalid_prepared_field"),
            (ValidPrepareJson.Replace(
                "{\"script\":\"x\",\"raw\":false,\"route\":\"auto\"}",
                "{\"script\":\"x\",\"script\":\"y\",\"raw\":false,\"route\":\"auto\"}",
                StringComparison.Ordinal), "duplicate_field"),
            (ValidPrepareJson.Replace(
                "{\"script\":\"x\",\"raw\":false,\"route\":\"auto\"}",
                "{\"script\":\"x\",\"raw\":false,\"route\":\"auto\",\"background\":false}",
                StringComparison.Ordinal), "invalid_prepared_field"),
        };

        foreach (var test in cases)
        {
            AssertDetail(
                test.DetailCode,
                () => WorkerPreparedOperationCodec.ParsePrepare(Json(test.Json)));
        }
    }

    [Fact]
    public void Prepare_digest_is_exact_strict_utf8_on_parse_and_create()
    {
        var exactLimit = new string(
            'x',
            WorkerSessionOperationCodec.MaximumLogicalTextBytes);
        foreach (var script in new[] { string.Empty, "é", exactLimit })
        {
            var digest = Digest(script);
            var value = new WorkerInvokePreparePayload(
                PlanId,
                7,
                DeadlineUtc,
                digest,
                new WorkerInvokeArguments(script, false, WorkerInvokeRoute.Auto));
            var encoded = WorkerPreparedOperationCodec.CreatePrepare(value);
            Assert.Equal(digest, encoded.GetProperty("scriptDigest").GetString());
            Assert.Equal(value, WorkerPreparedOperationCodec.ParsePrepare(encoded));
        }

        var wrongDigest = new string('0', 64);
        AssertDetail(
            "prepared_script_digest_mismatch",
            () => WorkerPreparedOperationCodec.CreatePrepare(
                ValidPrepare() with { ScriptDigest = wrongDigest }));
        AssertDetail(
            "prepared_script_digest_mismatch",
            () => WorkerPreparedOperationCodec.ParsePrepare(Json(
                ValidPrepareJson.Replace(
                    ScriptDigest,
                    wrongDigest,
                    StringComparison.Ordinal))));

        var oversize = exactLimit + "x";
        AssertDetail(
            "invalid_prepared_field",
            () => WorkerPreparedOperationCodec.CreatePrepare(new WorkerInvokePreparePayload(
                PlanId,
                7,
                DeadlineUtc,
                Digest(oversize),
                new WorkerInvokeArguments(oversize, false, WorkerInvokeRoute.Auto))));
    }

    [Fact]
    public void Correlation_codecs_reject_each_missing_field_and_noncanonical_value()
    {
        var parsers = new Func<JsonElement, object>[]
        {
            value => WorkerPreparedOperationCodec.ParsePreparedCorrelation(value),
            value => WorkerPreparedOperationCodec.ParseCommit(value),
            value => WorkerPreparedOperationCodec.ParseAbort(value),
        };

        foreach (var parser in parsers)
        {
            foreach (var field in new[]
            {
                "planId",
                "scriptDigest",
                "generation",
                "deadlineUnixTimeMilliseconds",
            })
            {
                var root = JsonNode.Parse(ValidCorrelationJson)!.AsObject();
                Assert.True(root.Remove(field));
                AssertDetail(
                    "missing_prepared_field",
                    () => parser(Json(root.ToJsonString())));
            }

            var cases = new (string Json, string DetailCode)[]
            {
                ("[]", "invalid_prepared_field"),
                (ValidCorrelationJson.Insert(1, "\"extra\":true,"), "unknown_prepared_field"),
                (ValidCorrelationJson.Insert(1, $"\"planId\":\"{PlanIdText}\","), "duplicate_field"),
                (ValidCorrelationJson.Replace(PlanIdText, PlanIdText.ToUpperInvariant(), StringComparison.Ordinal),
                    "invalid_prepared_field"),
                (ValidCorrelationJson.Replace(PlanIdText, $"{{{PlanIdText}}}", StringComparison.Ordinal),
                    "invalid_prepared_field"),
                (ValidCorrelationJson.Replace(PlanIdText, PlanIdText.Replace("-", string.Empty), StringComparison.Ordinal),
                    "invalid_prepared_field"),
                (ValidCorrelationJson.Replace(PlanIdText, "12345678-1234-5234-9234-123456789abc", StringComparison.Ordinal),
                    "invalid_prepared_field"),
                (ValidCorrelationJson.Replace(PlanIdText, "12345678-1234-4234-7234-123456789abc", StringComparison.Ordinal),
                    "invalid_prepared_field"),
                (ValidCorrelationJson.Replace(ScriptDigest, ScriptDigest.ToUpperInvariant(), StringComparison.Ordinal),
                    "invalid_prepared_field"),
                (ValidCorrelationJson.Replace(ScriptDigest, "0", StringComparison.Ordinal),
                    "invalid_prepared_field"),
                (ValidCorrelationJson.Replace(ScriptDigest, ScriptDigest + "0", StringComparison.Ordinal),
                    "invalid_prepared_field"),
                (ValidCorrelationJson.Replace(ScriptDigest, new string('z', 64), StringComparison.Ordinal),
                    "invalid_prepared_field"),
                (ValidCorrelationJson.Replace("\"generation\":7", "\"generation\":0", StringComparison.Ordinal),
                    "invalid_prepared_field"),
                (ValidCorrelationJson.Replace("\"generation\":7", "\"generation\":1.0", StringComparison.Ordinal),
                    "invalid_prepared_field"),
                (ValidCorrelationJson.Replace(DeadlineMilliseconds.ToString(), "0", StringComparison.Ordinal),
                    "invalid_prepared_field"),
                (ValidCorrelationJson.Replace(DeadlineMilliseconds.ToString(), "1.0", StringComparison.Ordinal),
                    "invalid_prepared_field"),
                (ValidCorrelationJson.Replace(DeadlineMilliseconds.ToString(), long.MaxValue.ToString(), StringComparison.Ordinal),
                    "invalid_prepared_field"),
            };

            foreach (var test in cases)
                AssertDetail(test.DetailCode, () => parser(Json(test.Json)));
        }
    }

    [Fact]
    public void Create_paths_reject_invalid_typed_values_without_normalizing()
    {
        foreach (var action in CorrelationCreateActions(
            Guid.Empty,
            ScriptDigest,
            7,
            DeadlineUtc))
        {
            AssertDetail("invalid_prepared_field", action);
        }
        foreach (var action in CorrelationCreateActions(
            Guid.ParseExact("12345678-1234-5234-9234-123456789abc", "D"),
            ScriptDigest,
            7,
            DeadlineUtc))
        {
            AssertDetail("invalid_prepared_field", action);
        }
        foreach (var action in CorrelationCreateActions(
            PlanId,
            ScriptDigest.ToUpperInvariant(),
            7,
            DeadlineUtc))
        {
            AssertDetail("invalid_prepared_field", action);
        }
        foreach (var action in CorrelationCreateActions(
            PlanId,
            ScriptDigest,
            0,
            DeadlineUtc))
        {
            AssertDetail("invalid_prepared_field", action);
        }
        foreach (var deadline in new[]
        {
            DeadlineUtc.ToOffset(TimeSpan.FromHours(1)),
            DeadlineUtc.AddTicks(1),
            DateTimeOffset.UnixEpoch,
        })
        {
            foreach (var action in CorrelationCreateActions(
                PlanId,
                ScriptDigest,
                7,
                deadline))
            {
                AssertDetail("invalid_prepared_field", action);
            }
        }

        foreach (var invalid in new[]
        {
            ValidPrepare() with { PlanId = Guid.Empty },
            ValidPrepare() with { Generation = 0 },
            ValidPrepare() with { ScriptDigest = ScriptDigest.ToUpperInvariant() },
            ValidPrepare() with
            {
                Arguments = new WorkerInvokeArguments(
                    Script,
                    false,
                    (WorkerInvokeRoute)99),
            },
        })
        {
            AssertDetail(
                "invalid_prepared_field",
                () => WorkerPreparedOperationCodec.CreatePrepare(invalid));
        }
    }

    [Fact]
    public void Generation_uses_the_full_positive_signed_64_bit_range()
    {
        var prepare = ValidPrepare() with { Generation = long.MaxValue };
        Assert.Equal(
            long.MaxValue,
            WorkerPreparedOperationCodec.ParsePrepare(
                WorkerPreparedOperationCodec.CreatePrepare(prepare)).Generation);

        var prepared = ValidPrepared() with { Generation = long.MaxValue };
        Assert.Equal(
            long.MaxValue,
            WorkerPreparedOperationCodec.ParsePreparedCorrelation(
                WorkerPreparedOperationCodec.CreatePreparedCorrelation(prepared)).Generation);
        Assert.Equal(
            long.MaxValue,
            WorkerPreparedOperationCodec.ParseCommit(
                WorkerPreparedOperationCodec.CreateCommit(new WorkerCommitPayload(
                    PlanId,
                    ScriptDigest,
                    long.MaxValue,
                    DeadlineUtc))).Generation);
        Assert.Equal(
            long.MaxValue,
            WorkerPreparedOperationCodec.ParseAbort(
                WorkerPreparedOperationCodec.CreateAbort(new WorkerAbortPayload(
                    PlanId,
                    ScriptDigest,
                    long.MaxValue,
                    DeadlineUtc))).Generation);

        AssertDetail(
            "invalid_prepared_field",
            () => WorkerPreparedOperationCodec.ParsePrepare(Json(
                ValidPrepareJson.Replace(
                    "\"generation\":7",
                    "\"generation\":-1",
                    StringComparison.Ordinal))));
        AssertDetail(
            "invalid_prepared_field",
            () => WorkerPreparedOperationCodec.ParseCommit(Json(
                ValidCorrelationJson.Replace(
                    "\"generation\":7",
                    "\"generation\":-1",
                    StringComparison.Ordinal))));
    }

    [Fact]
    public void Deadlines_parse_expired_and_maximum_but_create_requires_lossless_utc()
    {
        var expiredJson = ValidPrepareJson.Replace(
            DeadlineMilliseconds.ToString(),
            "1",
            StringComparison.Ordinal);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1),
            WorkerPreparedOperationCodec.ParsePrepare(Json(expiredJson)).DeadlineUtc);

        const long maximumUnixMilliseconds = 253_402_300_799_999;
        var maximum = DateTimeOffset.FromUnixTimeMilliseconds(maximumUnixMilliseconds);
        var maximumValue = ValidPrepare() with { DeadlineUtc = maximum };
        Assert.Equal(
            maximumUnixMilliseconds,
            WorkerPreparedOperationCodec.CreatePrepare(maximumValue)
                .GetProperty("deadlineUnixTimeMilliseconds")
                .GetInt64());

        AssertDetail(
            "invalid_prepared_field",
            () => WorkerPreparedOperationCodec.CreatePrepare(
                ValidPrepare() with { DeadlineUtc = DeadlineUtc.ToOffset(TimeSpan.FromHours(1)) }));
        AssertDetail(
            "invalid_prepared_field",
            () => WorkerPreparedOperationCodec.CreatePrepare(
                ValidPrepare() with { DeadlineUtc = DeadlineUtc.AddTicks(1) }));
        AssertDetail(
            "invalid_prepared_field",
            () => WorkerPreparedOperationCodec.CreatePrepare(
                ValidPrepare() with { DeadlineUtc = DateTimeOffset.UnixEpoch }));
    }

    [Fact]
    public void Correlation_comparisons_return_typed_mismatch_for_every_field()
    {
        var prepare = ValidPrepare();
        var prepared = ValidPrepared();
        var commit = new WorkerCommitPayload(
            PlanId,
            ScriptDigest,
            7,
            DeadlineUtc);
        var abort = new WorkerAbortPayload(
            PlanId,
            ScriptDigest,
            7,
            DeadlineUtc);
        var otherDigest = new string('0', 64);

        Assert.Equal(
            WorkerPreparedCorrelationMatch.Mismatch,
            default(WorkerPreparedCorrelationMatch));

        Assert.Equal(
            WorkerPreparedCorrelationMatch.Match,
            WorkerPreparedOperationCodec.ComparePreparedToPrepare(prepare, prepared));
        foreach (var changed in new[]
        {
            prepared with { PlanId = OtherPlanId },
            prepared with { ScriptDigest = otherDigest },
            prepared with { Generation = 8 },
            prepared with { DeadlineUtc = DeadlineUtc.AddMilliseconds(1) },
        })
        {
            Assert.Equal(
                WorkerPreparedCorrelationMatch.Mismatch,
                WorkerPreparedOperationCodec.ComparePreparedToPrepare(prepare, changed));
        }

        Assert.Equal(
            WorkerPreparedCorrelationMatch.Match,
            WorkerPreparedOperationCodec.CompareCommitToPrepared(prepared, commit));
        foreach (var changed in new[]
        {
            commit with { PlanId = OtherPlanId },
            commit with { ScriptDigest = otherDigest },
            commit with { Generation = 8 },
            commit with { DeadlineUtc = DeadlineUtc.AddMilliseconds(1) },
        })
        {
            Assert.Equal(
                WorkerPreparedCorrelationMatch.Mismatch,
                WorkerPreparedOperationCodec.CompareCommitToPrepared(prepared, changed));
        }

        Assert.Equal(
            WorkerPreparedCorrelationMatch.Match,
            WorkerPreparedOperationCodec.CompareAbortToPrepared(prepared, abort));
        foreach (var changed in new[]
        {
            abort with { PlanId = OtherPlanId },
            abort with { ScriptDigest = otherDigest },
            abort with { Generation = 8 },
            abort with { DeadlineUtc = DeadlineUtc.AddMilliseconds(1) },
        })
        {
            Assert.Equal(
                WorkerPreparedCorrelationMatch.Mismatch,
                WorkerPreparedOperationCodec.CompareAbortToPrepared(prepared, changed));
        }

        Assert.Equal(
            WorkerPreparedCorrelationMatch.Mismatch,
            WorkerPreparedOperationCodec.ComparePreparedToPrepare(null, prepared));
        Assert.Equal(
            WorkerPreparedCorrelationMatch.Mismatch,
            WorkerPreparedOperationCodec.CompareCommitToPrepared(
                prepared,
                commit with { PlanId = Guid.Empty }));
        Assert.Equal(
            WorkerPreparedCorrelationMatch.Mismatch,
            WorkerPreparedOperationCodec.CompareCommitToPrepared(
                prepared,
                commit with { ScriptDigest = ScriptDigest.ToUpperInvariant() }));
        Assert.Equal(
            WorkerPreparedCorrelationMatch.Mismatch,
            WorkerPreparedOperationCodec.CompareCommitToPrepared(
                prepared,
                commit with { Generation = 0 }));
        Assert.Equal(
            WorkerPreparedCorrelationMatch.Mismatch,
            WorkerPreparedOperationCodec.CompareCommitToPrepared(
                prepared,
                commit with { DeadlineUtc = DeadlineUtc.AddTicks(1) }));
        Assert.Equal(
            WorkerPreparedCorrelationMatch.Mismatch,
            WorkerPreparedOperationCodec.CompareAbortToPrepared(prepared, null));
    }

    [Fact]
    public void Failures_retain_no_script_digest_identifier_or_inner_exception()
    {
        const string secretScript = "private-script-sentinel";
        var secretDigest = new string('0', 64);
        var mismatch = Assert.Throws<WorkerProtocolException>(() =>
            WorkerPreparedOperationCodec.CreatePrepare(new WorkerInvokePreparePayload(
                PlanId,
                7,
                DeadlineUtc,
                secretDigest,
                new WorkerInvokeArguments(
                    secretScript,
                    false,
                    WorkerInvokeRoute.Auto))));
        Assert.Equal("prepared_script_digest_mismatch", mismatch.DetailCode);
        Assert.Null(mismatch.InnerException);
        Assert.DoesNotContain(secretScript, mismatch.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secretDigest, mismatch.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PlanIdText, mismatch.ToString(), StringComparison.Ordinal);

        var unknown = Assert.Throws<WorkerProtocolException>(() =>
            WorkerPreparedOperationCodec.ParsePrepare(Json(
                ValidPrepareJson.Insert(1, "\"private-field-sentinel\":true,"))));
        Assert.Equal("unknown_prepared_field", unknown.DetailCode);
        Assert.Null(unknown.InnerException);
        Assert.DoesNotContain("private-field-sentinel", unknown.ToString(), StringComparison.Ordinal);

        var malformed = Assert.Throws<WorkerProtocolException>(() =>
            WorkerPreparedOperationCodec.ParsePrepare(Json(
                ValidPrepareJson.Replace(
                    $"\"{ScriptDigest}\"",
                    "\"\\uD800\"",
                    StringComparison.Ordinal))));
        Assert.Equal("invalid_prepared_field", malformed.DetailCode);
        Assert.Null(malformed.InnerException);

        var invalidLogicalScript =
            "private-prefix-" + new string((char)0xd800, 1) + "-private-suffix";
        var invalidScript = Assert.Throws<WorkerProtocolException>(() =>
            WorkerPreparedOperationCodec.CreatePrepare(new WorkerInvokePreparePayload(
                PlanId,
                7,
                DeadlineUtc,
                ScriptDigest,
                new WorkerInvokeArguments(
                    invalidLogicalScript,
                    false,
                    WorkerInvokeRoute.Auto))));
        Assert.Equal("invalid_prepared_field", invalidScript.DetailCode);
        Assert.Null(invalidScript.InnerException);
        Assert.DoesNotContain("private-prefix", invalidScript.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-suffix", invalidScript.ToString(), StringComparison.Ordinal);
    }

    private static WorkerInvokePreparePayload ValidPrepare() =>
        new(
            PlanId,
            7,
            DeadlineUtc,
            ScriptDigest,
            new WorkerInvokeArguments(Script, false, WorkerInvokeRoute.Auto));

    private static WorkerPreparedCorrelation ValidPrepared() =>
        new(PlanId, ScriptDigest, 7, DeadlineUtc);

    private static Action[] CorrelationCreateActions(
        Guid planId,
        string scriptDigest,
        long generation,
        DateTimeOffset deadlineUtc) =>
    [
        () => WorkerPreparedOperationCodec.CreatePreparedCorrelation(
            new WorkerPreparedCorrelation(
                planId,
                scriptDigest,
                generation,
                deadlineUtc)),
        () => WorkerPreparedOperationCodec.CreateCommit(
            new WorkerCommitPayload(
                planId,
                scriptDigest,
                generation,
                deadlineUtc)),
        () => WorkerPreparedOperationCodec.CreateAbort(
            new WorkerAbortPayload(
                planId,
                scriptDigest,
                generation,
                deadlineUtc)),
    ];

    private static string Digest(string script)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(script);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void AssertDetail(string detailCode, Action action)
    {
        var exception = Assert.Throws<WorkerProtocolException>(action);
        Assert.Equal(detailCode, exception.DetailCode);
        Assert.Null(exception.InnerException);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
