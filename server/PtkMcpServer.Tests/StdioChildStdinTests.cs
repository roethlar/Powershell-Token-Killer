using System.Diagnostics;
using System.Text.Json;

namespace PtkMcpServer.Tests;

public sealed class StdioChildStdinTests
{
    // Spawns the built server exactly as a real harness does - stdio pipes,
    // stdin kept open and idle, no console - and runs a native command that
    // reads stdin. Two failure modes are guarded, and BOTH assertions are
    // load-bearing:
    // - hang: without ChildStdinGuard the child inherits the live JSON-RPC
    //   stdin pipe and blocks until the session dies (the bare-git hang);
    // - invalid handle: a NON-INHERITABLE NUL handle returns promptly but
    //   gives children a stdin value absent from their handle table - programs
    //   that touch stdin fail with "The handle is invalid (os error 6)" (the
    //   rustup-shim failure from live use; v2-feedback plan, slice 0 probe).
    //   Asserting only "returns promptly" hid that bug: the call must SUCCEED
    //   with clean EOF semantics, not merely return.
    [Fact]
    public async Task Stdin_reading_native_reads_clean_EOF_under_idle_stdio()
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "PtkMcpServer.dll");
        Assert.True(File.Exists(serverDll), $"server dll not found at {serverDll}");
        var auditRoot = NewAuditRoot();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true, // console-less, like the harness spawn
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(serverDll);
        psi.Environment["PTK_AUDIT_ROOT"] = auditRoot;

        using var proc = Process.Start(psi)!;
        _ = proc.StandardError.ReadToEndAsync();
        try
        {
            await SendAsync(proc,
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"stdin-guard-test","version":"0.0.0"}}}""");
            await ReadResponseAsync(proc, 1);
            await SendAsync(proc, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            // route=pwsh pins plain in-runspace execution (auto would rewrite
            // through rtk, which wires its own child stdio and masks the bug).
            var script = OperatingSystem.IsWindows() ? "cmd /c sort" : "cat";
            var call = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new { name = "ptk_invoke", arguments = new { script, route = "pwsh" } },
            });
            await SendAsync(proc, call);
            var response = await ReadResponseAsync(proc, 2);

            // sort/cat on a valid NUL stdin reads EOF: no output, exit 0. An
            // invalid inherited handle instead yields an error and exit 1.
            var text = response.GetProperty("result").GetProperty("content")[0]
                .GetProperty("text").GetString() ?? string.Empty;
            Assert.Contains("(no output)", text);
            Assert.DoesNotContain("invalid", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("[exit]", text);
        }
        finally
        {
            await StopAndCleanAsync(proc, auditRoot);
        }
    }

    // Same console-less spawn, different defect class: a hosted server with no
    // console decodes native stdout with the OEM codepage on Windows, so UTF-8
    // output (em-dashes, any non-ASCII) came back as mojibake in live use
    // (v2-feedback plan, slice 2). A pwsh child emits a UTF-8 em-dash; it must
    // round-trip intact through ptk_invoke.
    [Fact]
    public async Task Native_utf8_output_roundtrips_without_mojibake()
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "PtkMcpServer.dll");
        Assert.True(File.Exists(serverDll), $"server dll not found at {serverDll}");
        var auditRoot = NewAuditRoot();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(serverDll);
        psi.Environment["PTK_AUDIT_ROOT"] = auditRoot;

        using var proc = Process.Start(psi)!;
        _ = proc.StandardError.ReadToEndAsync();
        try
        {
            await SendAsync(proc,
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"utf8-test","version":"0.0.0"}}}""");
            await ReadResponseAsync(proc, 1);
            await SendAsync(proc, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            var script =
                "pwsh -NoProfile -Command \"[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new(); 'em' + [char]0x2014 + 'dash'\"";
            var call = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new { name = "ptk_invoke", arguments = new { script, route = "pwsh" } },
            });
            await SendAsync(proc, call);
            var response = await ReadResponseAsync(proc, 2);

            var text = response.GetProperty("result").GetProperty("content")[0]
                .GetProperty("text").GetString() ?? string.Empty;
            Assert.Contains("em—dash", text);
        }
        finally
        {
            await StopAndCleanAsync(proc, auditRoot);
        }
    }

    private static string NewAuditRoot()
    {
        // These tests exercise stdio behavior, so they must not inherit or
        // contend with the operator's live mandatory-audit journal.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.True(Path.IsPathFullyQualified(profile), "test user profile must be absolute");
        return Path.Combine(profile, ".ptk", "test-stdio-audit-" + Guid.NewGuid().ToString("N"));
    }

    private static async Task StopAndCleanAsync(Process proc, string auditRoot)
    {
        try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
        try { Directory.Delete(auditRoot, recursive: true); } catch { /* preserve the test result */ }
    }

    private static async Task SendAsync(Process proc, string json)
    {
        await proc.StandardInput.WriteLineAsync(json);
        await proc.StandardInput.FlushAsync();
    }

    private static async Task<JsonElement> ReadResponseAsync(Process proc, int id)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            var line = await proc.StandardOutput.ReadLineAsync(cts.Token)
                ?? throw new InvalidOperationException($"server closed stdout waiting for id={id}");
            if (string.IsNullOrWhiteSpace(line)) continue;
            var msg = JsonSerializer.Deserialize<JsonElement>(line);
            if (msg.TryGetProperty("id", out var msgId) &&
                msgId.ValueKind == JsonValueKind.Number &&
                msgId.GetInt32() == id)
            {
                return msg;
            }
        }
    }
}
