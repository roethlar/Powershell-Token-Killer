namespace PtkMcpServer.Audit;

/// <summary>
/// Loads the process-wide, startup-frozen audit mode. The absence of an export
/// configuration selects local-only logging; the presence of one selects
/// anchored mode and must produce a complete validated exporter configuration.
/// </summary>
internal sealed class AuditStartupConfiguration : IDisposable
{
    internal const string AuditRootEnvironmentVariable = "PTK_AUDIT_ROOT";
    internal const string ExportConfigurationEnvironmentVariable =
        "PTK_AUDIT_EXPORT_CONFIG";

    private readonly AuditExportOptions? _exportOptions;
    private int _disposed;

    private AuditStartupConfiguration(
        AuditOptions auditOptions,
        AuditExportOptions? exportOptions)
    {
        AuditOptions = auditOptions;
        _exportOptions = exportOptions;
    }

    internal AuditOptions AuditOptions { get; }

    internal AuditExportOptions? ExportOptions
    {
        get
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            return _exportOptions;
        }
    }

    internal static AuditStartupConfiguration LoadFromEnvironment() =>
        Load(
            Environment.GetEnvironmentVariable(AuditRootEnvironmentVariable),
            Environment.GetEnvironmentVariable(ExportConfigurationEnvironmentVariable),
            static (configurationPath, auditRoot) =>
                AuditExportConfigurationLoader.Load(configurationPath, auditRoot));

    internal static AuditStartupConfiguration Load(
        string? configuredAuditRoot,
        string? configuredExportPath,
        Func<string, string, AuditExportOptions> loadExportOptions)
    {
        ArgumentNullException.ThrowIfNull(loadExportOptions);

        var localOptions = string.IsNullOrWhiteSpace(configuredAuditRoot)
            ? AuditOptions.CreateDefault()
            : AuditOptions.Create(Path.GetFullPath(configuredAuditRoot));
        if (configuredExportPath is null)
            return new AuditStartupConfiguration(localOptions, exportOptions: null);

        AuditExportOptions? exportOptions = null;
        try
        {
            // An explicitly present but empty value is intentionally delegated
            // to the strict loader and fails as incomplete anchored setup.
            exportOptions = loadExportOptions(
                configuredExportPath,
                localOptions.RootDirectory);
            var anchoredOptions = AuditOptions.Create(
                localOptions.RootDirectory,
                AuditProtectionMode.Anchored,
                exportOptions.ConfigurationIdentity);
            return new AuditStartupConfiguration(anchoredOptions, exportOptions);
        }
        catch
        {
            exportOptions?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _exportOptions?.Dispose();
    }
}
