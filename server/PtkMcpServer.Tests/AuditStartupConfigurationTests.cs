using System.Security.Cryptography.X509Certificates;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditStartupConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ptk-audit-startup-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* Preserve the assertion failure that prevented cleanup. */ }
    }

    [Fact]
    public void Missing_export_configuration_selects_local_only_without_loading_a_siem()
    {
        var loaded = false;
        using var startup = AuditStartupConfiguration.Load(
            _root,
            configuredExportPath: null,
            (_, _) =>
            {
                loaded = true;
                throw new InvalidOperationException("must not load");
            });

        Assert.False(loaded);
        Assert.Equal(AuditProtectionMode.LocalOnly, startup.AuditOptions.ProtectionMode);
        Assert.Null(startup.AuditOptions.ExportConfigurationIdentity);
        Assert.Null(startup.ExportOptions);
    }

    [Fact]
    public void Present_export_configuration_selects_anchored_with_the_loaded_identity()
    {
        const string identity =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string? observedPath = null;
        string? observedRoot = null;
        using var startup = AuditStartupConfiguration.Load(
            _root,
            "/operator/export.json",
            (path, root) =>
            {
                observedPath = path;
                observedRoot = root;
                return ExportOptions(identity);
            });

        Assert.Equal("/operator/export.json", observedPath);
        Assert.Equal(Path.GetFullPath(_root), observedRoot);
        Assert.Equal(AuditProtectionMode.Anchored, startup.AuditOptions.ProtectionMode);
        Assert.Equal(identity, startup.AuditOptions.ExportConfigurationIdentity);
        Assert.Same(startup.ExportOptions, startup.ExportOptions);
        Assert.Equal(identity, startup.ExportOptions!.ConfigurationIdentity);
    }

    [Fact]
    public void Explicit_empty_export_configuration_is_not_treated_as_local_only()
    {
        var exception = Assert.Throws<AuditExportConfigurationException>(() =>
            AuditStartupConfiguration.Load(
                _root,
                string.Empty,
                (path, _) => throw new AuditExportConfigurationException(
                    path.Length == 0 ? "config_path" : "wrong_path")));

        Assert.Equal("config_path", exception.FailureCode);
    }

    private static AuditExportOptions ExportOptions(string identity) =>
        new(
            "https://collector.example/v1/logs",
            new Uri("https://collector.example/v1/logs"),
            [new AuditExportHeader("Authorization", "Bearer test")],
            [],
            clientCertificate: null,
            X509RevocationMode.NoCheck,
            identity);
}
