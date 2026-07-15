using System.Text.Json;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerSessionOperationCodecTests
{
    [Fact]
    public void Argument_codecs_round_trip_each_exact_closed_operation()
    {
        var cases = new (string Operation, WorkerSessionOperationArguments Value, string Json)[]
        {
            ("invoke", new WorkerInvokeArguments(string.Empty, false, WorkerInvokeRoute.Auto),
                "{\"script\":\"\",\"raw\":false,\"route\":\"auto\"}"),
            ("invoke", new WorkerInvokeArguments("Get-Date", false, WorkerInvokeRoute.Pwsh),
                "{\"script\":\"Get-Date\",\"raw\":false,\"route\":\"pwsh\"}"),
            ("invoke", new WorkerInvokeArguments("Get-Item .", true, WorkerInvokeRoute.Rtk),
                "{\"script\":\"Get-Item .\",\"raw\":true,\"route\":\"rtk\"}"),
            ("job_list", new WorkerJobListArguments(), "{}"),
            ("job_status", new WorkerJobStatusArguments(41), "{\"jobId\":41}"),
            ("job_output", new WorkerJobOutputArguments(41, 0),
                "{\"jobId\":41,\"offset\":0}"),
            ("job_output", new WorkerJobOutputArguments(41, 9),
                "{\"jobId\":41,\"offset\":9}"),
            ("job_kill", new WorkerJobKillArguments(41), "{\"jobId\":41}"),
            ("state", new WorkerStateArguments(true), "{\"listAvailable\":true}"),
        };

        foreach (var test in cases)
        {
            var encoded = WorkerSessionOperationCodec.CreateArguments(
                test.Operation,
                test.Value);
            Assert.Equal(test.Json, encoded.GetRawText());
            Assert.Equal(
                test.Value,
                WorkerSessionOperationCodec.ParseArguments(test.Operation, encoded));
        }
    }

    [Fact]
    public void Argument_codecs_reject_unknown_missing_null_wrong_kind_range_and_duplicate()
    {
        var cases = new (string Operation, string Json, string DetailCode)[]
        {
            ("reset", "{}", "unsupported_operation"),
            ("job", "{}", "unsupported_operation"),
            ("invoke", "[]", "invalid_operation_field"),
            ("invoke", "{\"script\":\"x\",\"route\":\"auto\"}", "missing_operation_field"),
            ("invoke", "{\"script\":null,\"raw\":false,\"route\":\"auto\"}", "invalid_operation_field"),
            ("invoke", "{\"script\":1,\"raw\":false,\"route\":\"auto\"}", "invalid_operation_field"),
            ("invoke", "{\"script\":\"x\",\"raw\":null,\"route\":\"auto\"}", "invalid_operation_field"),
            ("invoke", "{\"script\":\"x\",\"raw\":0,\"route\":\"auto\"}", "invalid_operation_field"),
            ("invoke", "{\"script\":\"x\",\"raw\":false,\"route\":null}", "invalid_operation_field"),
            ("invoke", "{\"script\":\"x\",\"raw\":false,\"route\":\"Auto\"}", "invalid_operation_field"),
            ("invoke", "{\"script\":\"x\",\"raw\":false,\"route\":\" auto\"}", "invalid_operation_field"),
            ("invoke", "{\"script\":\"x\",\"raw\":false,\"route\":\"auto\",\"background\":false}", "unknown_operation_field"),
            ("invoke", "{\"script\":\"x\",\"raw\":false,\"route\":\"auto\",\"timeoutSeconds\":1}", "unknown_operation_field"),
            ("job_list", "{\"jobId\":1}", "unknown_operation_field"),
            ("job_status", "{}", "missing_operation_field"),
            ("job_status", "{\"jobId\":null}", "invalid_operation_field"),
            ("job_status", "{\"jobId\":0}", "invalid_operation_field"),
            ("job_status", "{\"jobId\":-1}", "invalid_operation_field"),
            ("job_status", "{\"jobId\":1.0}", "invalid_operation_field"),
            ("job_status", "{\"jobId\":9223372036854775808}", "invalid_operation_field"),
            ("job_status", "{\"jobId\":1,\"jobId\":2}", "duplicate_field"),
            ("job_status", "{\"action\":\"status\",\"jobId\":1}", "unknown_operation_field"),
            ("job_output", "{\"jobId\":1}", "missing_operation_field"),
            ("job_output", "{\"jobId\":1,\"offset\":-1}", "invalid_operation_field"),
            ("job_output", "{\"jobId\":1,\"offset\":1.0}", "invalid_operation_field"),
            ("job_output", "{\"jobId\":1,\"offset\":9223372036854775808}", "invalid_operation_field"),
            ("job_kill", "{\"jobId\":1,\"offset\":0}", "unknown_operation_field"),
            ("state", "{\"listAvailable\":null}", "invalid_operation_field"),
            ("state", "{\"listAvailable\":0}", "invalid_operation_field"),
            ("state", "{\"listAvailable\":false,\"extra\":true}", "unknown_operation_field"),
        };

        foreach (var test in cases)
        {
            var exception = Assert.Throws<WorkerProtocolException>(() =>
                WorkerSessionOperationCodec.ParseArguments(
                    test.Operation,
                    Json(test.Json)));
            Assert.Equal(test.DetailCode, exception.DetailCode);
        }

        Assert.Equal(
            "operation_argument_mismatch",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerSessionOperationCodec.CreateArguments(
                    "state",
                    new WorkerInvokeArguments("x", false, WorkerInvokeRoute.Auto))).DetailCode);
        Assert.Equal(
            "invalid_operation_field",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerSessionOperationCodec.CreateArguments(
                    "job_status",
                    new WorkerJobStatusArguments(0))).DetailCode);
        Assert.Equal(
            "invalid_operation_field",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerSessionOperationCodec.CreateArguments(
                    "job_output",
                    new WorkerJobOutputArguments(1, -1))).DetailCode);
        Assert.Equal(
            "invalid_operation_field",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerSessionOperationCodec.CreateArguments(
                    "invoke",
                    new WorkerInvokeArguments("x", false, (WorkerInvokeRoute)99))).DetailCode);
        Assert.Equal(
            "invalid_operation_field",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerSessionOperationCodec.CreateArguments(
                    "invoke",
                    new WorkerInvokeArguments(null!, false, WorkerInvokeRoute.Auto))).DetailCode);

        Assert.Equal(
            long.MaxValue,
            Assert.IsType<WorkerJobOutputArguments>(
                WorkerSessionOperationCodec.ParseArguments(
                    "job_output",
                    WorkerSessionOperationCodec.CreateArguments(
                        "job_output",
                        new WorkerJobOutputArguments(long.MaxValue, long.MaxValue)))).Offset);
    }

    [Fact]
    public void Script_limit_is_strict_logical_utf8_on_parse_and_create()
    {
        Assert.Equal(131_072, WorkerSessionOperationCodec.MaximumLogicalTextBytes);
        var exact = new string('x', WorkerSessionOperationCodec.MaximumLogicalTextBytes);
        var exactMultibyte = new string(
            'é',
            WorkerSessionOperationCodec.MaximumLogicalTextBytes / 2);
        var escapedAtLimit = new string(
            '\n',
            WorkerSessionOperationCodec.MaximumLogicalTextBytes);

        foreach (var script in new[] { exact, exactMultibyte, escapedAtLimit })
        {
            var encoded = WorkerSessionOperationCodec.CreateArguments(
                "invoke",
                new WorkerInvokeArguments(script, false, WorkerInvokeRoute.Auto));
            Assert.Equal(
                script,
                Assert.IsType<WorkerInvokeArguments>(
                    WorkerSessionOperationCodec.ParseArguments("invoke", encoded)).Script);
        }

        Assert.Equal(
            "operation_script_too_large",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerSessionOperationCodec.CreateArguments(
                    "invoke",
                    new WorkerInvokeArguments(exact + "x", false, WorkerInvokeRoute.Auto))).DetailCode);
        Assert.Equal(
            "operation_script_too_large",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerSessionOperationCodec.ParseArguments(
                    "invoke",
                    JsonSerializer.SerializeToElement(new
                    {
                        script = exactMultibyte + "é",
                        raw = false,
                        route = "auto",
                    }))).DetailCode);

        var invalidLogicalText = "secret-prefix-" + new string((char)0xd800, 1) + "-secret-suffix";
        var createInvalid = Assert.Throws<WorkerProtocolException>(() =>
            WorkerSessionOperationCodec.CreateArguments(
                "invoke",
                new WorkerInvokeArguments(
                    invalidLogicalText,
                    false,
                    WorkerInvokeRoute.Auto)));
        Assert.Equal(
            "invalid_operation_field",
            createInvalid.DetailCode);
        Assert.Null(createInvalid.InnerException);
        Assert.DoesNotContain("secret-prefix", createInvalid.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-suffix", createInvalid.ToString(), StringComparison.Ordinal);
        var parseInvalid = Assert.Throws<WorkerProtocolException>(() =>
            WorkerSessionOperationCodec.ParseArguments(
                "invoke",
                Json("{\"script\":\"\\uD800\",\"raw\":false,\"route\":\"auto\"}")));
        Assert.Equal(
            "invalid_operation_field",
            parseInvalid.DetailCode);
        Assert.Null(parseInvalid.InnerException);
    }

    [Fact]
    public void Result_codecs_round_trip_distinct_types_and_enforce_both_bounds()
    {
        var exact = new string('x', WorkerSessionOperationCodec.MaximumLogicalTextBytes);
        var over = exact + "x";
        var cases = new (string Operation, Func<string, WorkerSessionOperationResult> Create)[]
        {
            ("invoke", text => new WorkerInvokeResult(text)),
            ("job_list", text => new WorkerJobListResult(text)),
            ("job_status", text => new WorkerJobStatusResult(text)),
            ("job_output", text => new WorkerJobOutputResult(text)),
            ("job_kill", text => new WorkerJobKillResult(text)),
            ("state", text => new WorkerStateResult(text)),
        };

        foreach (var test in cases)
        {
            Assert.Equal(
                string.Empty,
                WorkerSessionOperationCodec.CreateResult(
                    test.Operation,
                    test.Create(string.Empty)).GetProperty("text").GetString());
            var expected = test.Create(exact);
            var encoded = WorkerSessionOperationCodec.CreateResult(
                test.Operation,
                expected);
            Assert.Equal(exact, encoded.GetProperty("text").GetString());
            Assert.Equal(
                expected,
                WorkerSessionOperationCodec.ParseResult(test.Operation, encoded));

            Assert.Equal(
                "operation_result_too_large",
                Assert.Throws<WorkerProtocolException>(() =>
                    WorkerSessionOperationCodec.CreateResult(
                        test.Operation,
                        test.Create(over))).DetailCode);
            Assert.Equal(
                "operation_result_too_large",
                Assert.Throws<WorkerProtocolException>(() =>
                    WorkerSessionOperationCodec.ParseResult(
                        test.Operation,
                        JsonSerializer.SerializeToElement(new { text = over }))).DetailCode);
        }

        var multibyte = new string(
            'é',
            WorkerSessionOperationCodec.MaximumLogicalTextBytes / 2);
        Assert.Equal(
            multibyte,
            Assert.IsType<WorkerStateResult>(
                WorkerSessionOperationCodec.ParseResult(
                    "state",
                    WorkerSessionOperationCodec.CreateResult(
                        "state",
                        new WorkerStateResult(multibyte)))).Text);
        Assert.Equal(
            "operation_result_too_large",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerSessionOperationCodec.CreateResult(
                    "state",
                    new WorkerStateResult(multibyte + "é"))).DetailCode);
    }

    [Fact]
    public void Result_codecs_reject_shape_mismatch_unknown_and_invalid_logical_text()
    {
        var cases = new (string Operation, string Json, string DetailCode)[]
        {
            ("invoke", "{}", "missing_operation_field"),
            ("invoke", "{\"text\":null}", "invalid_operation_field"),
            ("invoke", "{\"text\":\"ok\",\"extra\":true}", "unknown_operation_field"),
            ("invoke", "{\"text\":\"one\",\"text\":\"two\"}", "duplicate_field"),
            ("invoke", "[]", "invalid_operation_field"),
            ("reset", "{\"text\":\"ok\"}", "unsupported_operation"),
        };

        foreach (var test in cases)
        {
            var exception = Assert.Throws<WorkerProtocolException>(() =>
                WorkerSessionOperationCodec.ParseResult(
                    test.Operation,
                    Json(test.Json)));
            Assert.Equal(test.DetailCode, exception.DetailCode);
        }

        Assert.Equal(
            "operation_result_mismatch",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerSessionOperationCodec.CreateResult(
                    "state",
                    new WorkerInvokeResult("wrong type"))).DetailCode);
        Assert.Equal(
            "invalid_operation_field",
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerSessionOperationCodec.CreateResult(
                    "state",
                    new WorkerStateResult(null!))).DetailCode);

        var invalidLogicalText = "result-secret-" + new string((char)0xd800, 1);
        var createInvalid = Assert.Throws<WorkerProtocolException>(() =>
            WorkerSessionOperationCodec.CreateResult(
                "state",
                new WorkerStateResult(invalidLogicalText)));
        Assert.Equal(
            "invalid_operation_field",
            createInvalid.DetailCode);
        Assert.Null(createInvalid.InnerException);
        Assert.DoesNotContain("result-secret", createInvalid.ToString(), StringComparison.Ordinal);
        var parseInvalid = Assert.Throws<WorkerProtocolException>(() =>
            WorkerSessionOperationCodec.ParseResult(
                "state",
                Json("{\"text\":\"\\uD800\"}")));
        Assert.Equal(
            "invalid_operation_field",
            parseInvalid.DetailCode);
        Assert.Null(parseInvalid.InnerException);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
