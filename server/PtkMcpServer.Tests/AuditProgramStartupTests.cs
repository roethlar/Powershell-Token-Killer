using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditProgramStartupTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* Preserve the assertion failure that prevented cleanup. */ }
        }
    }

    [Fact]
    public async Task Explicit_empty_export_configuration_fails_before_host_start()
    {
        var auditRoot = NewRoot("invalid-audit");
        using var process = StartServer(
            auditRoot,
            exportConfigurationPath: string.Empty);
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(
                "audit_export_configuration_invalid: config_path",
                await stderr,
                StringComparison.Ordinal);
            Assert.Equal(string.Empty, await process.StandardOutput.ReadToEndAsync());
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* Preserve the test assertion. */ }
        }
    }

    [Fact]
    public async Task Valid_anchored_configuration_starts_and_network_loss_keeps_state_available()
    {
        var auditRoot = NewRoot("anchored-audit");
        var configRoot = NewRoot("anchored-config");
        var configPath = Path.Combine(configRoot, "export.json");
        var configuration = """
            {"schema_version":"ptk.export-config/1","protection_mode":"anchored","endpoint":"https://127.0.0.1:1/v1/logs","headers":{"Authorization":"Bearer integration-test"},"ca_file":null,"client_certificate_file":null,"client_private_key_file":null}
            """;
        using (var stream = SecureAuditStorage.CreateExclusiveFile(configPath))
        {
            stream.Write(Encoding.UTF8.GetBytes(configuration));
            stream.Flush(flushToDisk: true);
        }

        using var process = StartServer(auditRoot, configPath);
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await SendAsync(
                process,
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"anchored-startup-test","version":"1"}}}""");
            _ = await ReadResponseAsync(process, 1);
            await SendAsync(
                process,
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await SendAsync(
                process,
                """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"ptk_state","arguments":{}}}""");
            var response = await ReadResponseAsync(process, 2);
            var result = response.GetProperty("result");
            Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
            var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Contains("protection anchored", text, StringComparison.Ordinal);
            Assert.Contains("audit exporter:", text, StringComparison.Ordinal);
            Assert.DoesNotContain("audit exporter: disabled", text, StringComparison.Ordinal);
            Assert.DoesNotContain("unrecorded=true", text, StringComparison.Ordinal);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* Preserve the test assertion. */ }
            _ = await stderr;
        }
    }

    private Process StartServer(string auditRoot, string exportConfigurationPath)
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "PtkMcpServer.dll");
        Assert.True(File.Exists(serverDll), $"server dll not found at {serverDll}");
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(serverDll);
        start.Environment[AuditStartupConfiguration.AuditRootEnvironmentVariable] = auditRoot;
        start.Environment[AuditStartupConfiguration.ExportConfigurationEnvironmentVariable] =
            exportConfigurationPath;
        return Process.Start(start)
            ?? throw new InvalidOperationException("The audit startup test server did not start.");
    }

    private string NewRoot(string label)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(
            profile,
            ".ptk",
            $"test-{label}-{Guid.NewGuid():N}");
        _roots.Add(root);
        return SecureAuditStorage.PrepareRoot(root);
    }

    private static async Task SendAsync(Process process, string json)
    {
        await process.StandardInput.WriteLineAsync(json);
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonElement> ReadResponseAsync(Process process, int id)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellation.Token)
                ?? throw new InvalidOperationException(
                    $"The server closed stdout while waiting for response {id}.");
            if (string.IsNullOrWhiteSpace(line)) continue;
            var message = JsonSerializer.Deserialize<JsonElement>(line);
            if (message.TryGetProperty("id", out var messageId) &&
                messageId.ValueKind == JsonValueKind.Number &&
                messageId.GetInt32() == id)
            {
                return message;
            }
        }
    }
}
